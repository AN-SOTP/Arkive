using Arkive.Core.Interface;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.Particles;
using Arkive.Core.Physics;
using Arkive.Core.StaticMeshes;

namespace Arkive.Core.Mods;

/// <summary>Which build of a package project the game holds now, with the check that showed it.</summary>
/// <param name="Error">Why the latest build could not be checked, when it could not.</param>
/// <param name="ForDeletionIdentityOnly">An earlier build, checked only to identify it; it is never offered for installing.</param>
public sealed record PackageDeploymentState(
    GuidedTexturePackageRevision Revision,
    ModDeploymentInspection? Inspection,
    string? Error,
    bool ForDeletionIdentityOnly = false)
{
    /// <summary>The check proved this build is in the game now.</summary>
    public bool IsActive => Inspection?.HasActiveDeployment == true;
}

/// <summary>
/// Decides which build of each package project is in the game, from the package project records and install receipts
/// alone. The package view, the Mods list and the launch benchmark all ask here, so they cannot disagree. Checks reuse
/// the hashes of files whose length and write time are unchanged: this state is only shown. Install, restore and Apply
/// run their own full checks before they change anything.
/// </summary>
public sealed class PackageInstallStateService
{
    /// <summary>Package projects checked at once; each reads its builds, receipts and game file.</summary>
    public const int Parallelism = 8;

    private readonly ArkiveModDeploymentService _deployment;
    private readonly string _backupRoot;
    private readonly string _stateRoot;

    public PackageInstallStateService(ArkiveModApplyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _deployment = context.Deployment;
        _backupRoot = Path.GetFullPath(context.BackupRootDirectory);
        _stateRoot = Path.GetFullPath(context.InstallationStateRoot);
    }

    /// <summary>Receives a line for the session log about each check that found an earlier build or failed.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// The latest build when its check finds it in the game; otherwise an earlier build that a receipt says was
    /// installed and whose check finds it in the game; otherwise the clean root of the build history when the game file
    /// is that clean original; otherwise the latest build with the check that found it not in the game. Null for a
    /// project with no build.
    /// </summary>
    public Task<PackageDeploymentState?> ResolveAsync(
        GuidedTexturePackageEntry package,
        CancellationToken cancellationToken = default) =>
        ResolveCoreAsync(package, inconclusive: null, cancellationToken);

    /// <summary>
    /// <see cref="ResolveAsync"/>, with the files its state was worked out from (see <see cref="DependencyPathsAsync"/>)
    /// for the launch memo (<see cref="PackageInstallMemo"/>). The files are null when any check was inconclusive or
    /// they cannot all be named: such a state is shown but never remembered, since a file that could not be read this
    /// time may read differently next time without its length or write time changing.
    /// </summary>
    public async Task<(PackageDeploymentState? State, IReadOnlyList<string>? Files)> ResolveWithFilesAsync(
        GuidedTexturePackageEntry package,
        CancellationToken cancellationToken = default)
    {
        var inconclusive = false;
        var state = await ResolveCoreAsync(package, () => inconclusive = true, cancellationToken).ConfigureAwait(false);
        if (inconclusive || state?.Error is not null) return (state, null);
        return (state, await DependencyPathsAsync(package, state, cancellationToken).ConfigureAwait(false));
    }

    private async Task<PackageDeploymentState?> ResolveCoreAsync(
        GuidedTexturePackageEntry package,
        Action? inconclusive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        var revisions = package.Manifest.Revisions
            .Where(revision => File.Exists(revision.ModBundleManifestPath))
            .OrderByDescending(revision => revision.CreatedAt)
            .ToArray();
        if (revisions.Length == 0) return null;

        var latest = package.Manifest.LatestRevisionId is null
            ? revisions[0]
            : revisions.FirstOrDefault(revision => revision.RevisionId.Equals(
                package.Manifest.LatestRevisionId, StringComparison.OrdinalIgnoreCase)) ?? revisions[0];
        PackageDeploymentState? latestState = null;
        try
        {
            var inspection = await _deployment.InspectForRecoveryAsync(Request(latest), cancellationToken)
                .ConfigureAwait(false);
            latestState = new PackageDeploymentState(latest, inspection, null);
            if (inspection.HasActiveDeployment) return latestState;
        }
        catch (Exception exception) when (Inconclusive(exception))
        {
            inconclusive?.Invoke();
            latestState = new PackageDeploymentState(latest, null, exception.Message);
            Log?.Invoke(
                $"Guided package refresh latest-revision inspection failed: " +
                $"packageId={package.Manifest.PackageProjectId}, revision={latest.RevisionId}, error={exception}");
        }

        foreach (var revision in revisions.Where(revision => !revision.RevisionId.Equals(
                     latest.RevisionId, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var bundle = await ArkiveModBundleService.LoadBundleAsync(revision.ModBundleManifestPath, cancellationToken)
                    .ConfigureAwait(false);
                var receiptPath = Path.Combine(_stateRoot, bundle.ModId, ArkiveModDeploymentService.ReceiptFileName);
                if (!File.Exists(receiptPath)) continue;
                var receipt = await ArkiveModDeploymentService.LoadReceiptAsync(receiptPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!receipt.GameFilesModified && receipt.State is not (
                        ModInstallationState.Installed or ModInstallationState.Prepared)) continue;

                var inspection = await _deployment.InspectForDeletionIdentityAsync(Request(revision), cancellationToken)
                    .ConfigureAwait(false);
                if (!inspection.HasActiveDeployment) continue;
                Log?.Invoke(
                    $"Guided package active earlier revision resolved: " +
                    $"packageId={package.Manifest.PackageProjectId}, revision={revision.RevisionId}, " +
                    $"latestRevision={latest.RevisionId}, state={inspection.State}, " +
                    $"targetSha={inspection.CurrentTargetSha256}.");
                return new PackageDeploymentState(revision, inspection, null, ForDeletionIdentityOnly: true);
            }
            catch (Exception exception) when (Inconclusive(exception))
            {
                inconclusive?.Invoke();
                Log?.Invoke(
                    $"Guided package earlier-revision inspection was inconclusive: " +
                    $"packageId={package.Manifest.PackageProjectId}, revision={revision.RevisionId}, error={exception.Message}");
            }
        }

        var lineage = new List<(GuidedTexturePackageRevision Revision, ArkiveModBundleManifest Bundle)>();
        foreach (var revision in revisions)
        {
            try
            {
                lineage.Add((revision, await ArkiveModBundleService.LoadBundleAsync(
                    revision.ModBundleManifestPath, cancellationToken).ConfigureAwait(false)));
            }
            catch (Exception exception) when (Inconclusive(exception))
            {
                inconclusive?.Invoke();
                Log?.Invoke($"Guided package lineage manifest was unreadable: packageId={package.Manifest.PackageProjectId}, " +
                            $"revision={revision.RevisionId}, error={exception.Message}");
            }
        }
        var payloadHashes = lineage.Select(item => item.Bundle.PayloadSha256)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var root in lineage.Where(item =>
                     !payloadHashes.Contains(item.Bundle.RequiredSourcePackageSha256)))
        {
            try
            {
                var inspection = await _deployment.InspectForRecoveryAsync(Request(root.Revision), cancellationToken)
                    .ConfigureAwait(false);
                if (inspection.State is not (
                        ModInstallationState.NotInstalled or ModInstallationState.Restored) ||
                    !inspection.CurrentTargetSha256!.Equals(
                        root.Bundle.RequiredSourcePackageSha256,
                        StringComparison.OrdinalIgnoreCase)) continue;
                Log?.Invoke(
                    $"Guided package clean lineage root resolved: packageId={package.Manifest.PackageProjectId}, " +
                    $"revision={root.Revision.RevisionId}, rootSha={root.Bundle.RequiredSourcePackageSha256}, " +
                    $"latestRevision={latest.RevisionId}.");
                return new PackageDeploymentState(root.Revision, inspection, null);
            }
            catch (Exception exception) when (Inconclusive(exception))
            {
                inconclusive?.Invoke();
                Log?.Invoke($"Guided package root-revision inspection was inconclusive: " +
                            $"packageId={package.Manifest.PackageProjectId}, revision={root.Revision.RevisionId}, error={exception.Message}");
            }
        }

        return latestState;
    }

    /// <summary>
    /// <see cref="ResolveAsync"/> for every package, <see cref="Parallelism"/> at a time: hundreds of packages took
    /// 5-20 s one after another.
    /// </summary>
    public async Task<IReadOnlyDictionary<GuidedTexturePackageEntry, PackageDeploymentState?>> ResolveAllAsync(
        IEnumerable<GuidedTexturePackageEntry> packages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packages);
        using var gate = new SemaphoreSlim(Parallelism);
        var pending = packages.Distinct(ReferenceEqualityComparer.Instance).Cast<GuidedTexturePackageEntry>()
            .Select(package => (Package: package, State: Resolve(package)))
            .ToArray();
        var states = new Dictionary<GuidedTexturePackageEntry, PackageDeploymentState?>(ReferenceEqualityComparer.Instance);
        foreach (var (package, state) in pending) states[package] = await state.ConfigureAwait(false);
        return states;

        async Task<PackageDeploymentState?> Resolve(GuidedTexturePackageEntry package)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await ResolveAsync(package, cancellationToken).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
    }

    /// <summary>
    /// Every file <paramref name="state"/> (what <see cref="ResolveAsync"/> just gave for <paramref name="package"/>) was
    /// worked out from, besides the package project record itself: a build file whose presence decides which build is
    /// the latest, and each checked build's record, readiness record, payload, variable-size approval, receipt, backup
    /// and game file. When the latest build was found in the game (nearly every installed package) only its files
    /// count, since the check stopped there; otherwise every build that exists counts, which may be more than the check
    /// looked at but never fewer. Null when a build or readiness record that exists cannot be read, so the list could
    /// miss a file.
    /// </summary>
    public async Task<IReadOnlyList<string>?> DependencyPathsAsync(
        GuidedTexturePackageEntry package,
        PackageDeploymentState? state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var manifest = package.Manifest;
        var files = new List<string>();
        // The latest is the build the record names while that build exists; otherwise the newest that exists, which
        // every build's presence decides.
        var namedLatest = manifest.LatestRevisionId is { } latestId && state is not null &&
                          state.Revision.RevisionId.Equals(latestId, StringComparison.OrdinalIgnoreCase) &&
                          manifest.Revisions.Count(revision => revision.RevisionId.Equals(
                              latestId, StringComparison.OrdinalIgnoreCase)) == 1;
        if (!namedLatest) files.AddRange(manifest.Revisions.Select(revision => Path.GetFullPath(revision.ModBundleManifestPath)));
        try
        {
            // An active state that is not an earlier build is the latest build's own check, which ended the search.
            if (namedLatest && state is { IsActive: true, ForDeletionIdentityOnly: false, Inspection: { } inspection })
            {
                files.AddRange([inspection.BundleManifestPath, inspection.ReadinessManifestPath, inspection.PayloadPath,
                    inspection.ReceiptPath, inspection.BackupPath, inspection.TargetPath]);
                if (GatePath(Path.GetDirectoryName(inspection.BundleManifestPath)!, inspection.Bundle) is { } gate) files.Add(gate);
                return Distinct(files);
            }
            foreach (var revision in manifest.Revisions)
            {
                var bundlePath = Path.GetFullPath(revision.ModBundleManifestPath);
                files.Add(bundlePath);
                if (!File.Exists(bundlePath)) continue;
                var bundle = await ArkiveModBundleService.LoadBundleAsync(bundlePath, cancellationToken).ConfigureAwait(false);
                var directory = Path.GetDirectoryName(bundlePath)!;
                var readinessPath = Path.GetFullPath(Path.Combine(directory, bundle.DeploymentReadinessRelativePath));
                files.Add(readinessPath);
                files.Add(Path.GetFullPath(Path.Combine(directory, bundle.PayloadRelativePath)));
                if (GatePath(directory, bundle) is { } gate) files.Add(gate);
                files.Add(Path.GetFullPath(Path.Combine(_stateRoot, bundle.ModId, ArkiveModDeploymentService.ReceiptFileName)));
                files.Add(Path.GetFullPath(Path.Combine(_backupRoot, bundle.RequiredSourcePackageSha256, bundle.SourcePhysicalPackageName)));
                if (!File.Exists(readinessPath)) continue;
                var readiness = await ArkiveModBundleService.LoadReadinessAsync(readinessPath, cancellationToken).ConfigureAwait(false);
                files.Add(Path.GetFullPath(readiness.InstallTargetPath));
            }
            return Distinct(files);
        }
        catch (Exception exception) when (Inconclusive(exception))
        {
            return null;
        }

        static string? GatePath(string directory, ArkiveModBundleManifest bundle) =>
            bundle.VariableSizeMeshCount > 0 && !string.IsNullOrWhiteSpace(bundle.VariableSizeDeploymentGateRelativePath)
                ? Path.GetFullPath(Path.Combine(directory, bundle.VariableSizeDeploymentGateRelativePath))
                : null;

        static IReadOnlyList<string> Distinct(IEnumerable<string> paths) =>
            paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// The build in the game for each game package, keyed by <see cref="ArkiveModFileService.PhysicalPackageKey"/>, as
    /// the Mods list reads it: for each package the project <see cref="ArkiveModApplyService.FindPackageProject"/> picks
    /// among <paramref name="projects"/> (in their order), its active build or null.
    /// </summary>
    public static IReadOnlyDictionary<string, GuidedTexturePackageRevision?> ActiveRevisionsByPhysicalName(
        IReadOnlyList<GuidedTexturePackageEntry> projects,
        string? gameDirectory,
        Func<GuidedTexturePackageEntry, PackageDeploymentState?> stateOf)
    {
        ArgumentNullException.ThrowIfNull(stateOf);
        return ActiveRevisionsByPhysicalName(projects, gameDirectory,
            (Func<GuidedTexturePackageEntry, PackageInstallSummary?>)(project => PackageInstallSummary.Of(stateOf(project))));
    }

    /// <summary>
    /// <see cref="ActiveRevisionsByPhysicalName(IReadOnlyList{GuidedTexturePackageEntry}, string?, Func{GuidedTexturePackageEntry, PackageDeploymentState?})"/>
    /// from what the Mods list keeps of each state, which the launch memo can also give.
    /// </summary>
    public static IReadOnlyDictionary<string, GuidedTexturePackageRevision?> ActiveRevisionsByPhysicalName(
        IReadOnlyList<GuidedTexturePackageEntry> projects,
        string? gameDirectory,
        Func<GuidedTexturePackageEntry, PackageInstallSummary?> summaryOf)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(summaryOf);
        var map = new Dictionary<string, GuidedTexturePackageRevision?>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var physical = Path.GetFileName(project.Manifest.SourcePackagePath);
            if (ArkiveModApplyService.FindPackageProject(physical, gameDirectory, projects) != project) continue;
            var summary = summaryOf(project);
            map[ArkiveModFileService.PhysicalPackageKey(physical)] = summary?.Active == true ? summary.Revision : null;
        }
        return map;
    }

    /// <summary>
    /// The package projects the package view lists: one for each game package a working file changes (the first record
    /// read for that file), most recently changed first with favourites at the top.
    /// </summary>
    public static IReadOnlyList<GuidedTexturePackageEntry> ProjectsForWorkingFiles(
        IReadOnlyList<GuidedTexturePackageEntry> records,
        IEnumerable<string> workingFileSourcePackages)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(workingFileSourcePackages);
        var sources = workingFileSourcePackages.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byPackage = new Dictionary<string, GuidedTexturePackageEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var source = Path.GetFullPath(record.Manifest.SourcePackagePath);
            if (sources.Contains(source)) byPackage.TryAdd(source, record);
        }
        return OrderForView(byPackage.Values);
    }

    /// <summary>The package view's order: favourites first, then the most recently changed, then by name.</summary>
    public static IReadOnlyList<GuidedTexturePackageEntry> OrderForView(IEnumerable<GuidedTexturePackageEntry> projects) =>
        projects.OrderByDescending(project => project.Manifest.IsFavorite)
            .ThenByDescending(project => project.Manifest.UpdatedAt)
            .ThenBy(project => project.Manifest.PackageAlias, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Creates the package project of every game package a working file in <paramref name="snapshot"/> changes that has
    /// none yet (effect, physics and interface edits get none of their own), so its builds have a place and its
    /// install state can be read. Returns <paramref name="records"/> with the created ones added.
    /// </summary>
    public static async Task<IReadOnlyList<GuidedTexturePackageEntry>> EnsureProjectsAsync(
        GuidedTexturePackageWorkspace workspace,
        IReadOnlyList<GuidedTexturePackageEntry> records,
        ArkiveModEditSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(snapshot);
        var known = records.Select(record => Path.GetFullPath(record.Manifest.SourcePackagePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = snapshot.Edits.Where(edit => !known.Contains(edit.SourcePackagePath)).ToArray();
        if (missing.Length == 0) return records;
        var all = records.ToList();
        // The kinds in the order the package view used to meet them, so a new project takes the same name.
        foreach (var edit in missing.OrderBy(edit => KindOrder(edit.Kind)))
        {
            if (known.Contains(edit.SourcePackagePath)) continue;
            if (await PackageNamesAsync(edit, cancellationToken).ConfigureAwait(false) is not { } names) continue;
            var created = await workspace.GetOrCreateAsync(names.Alias, names.Physical, names.Source, cancellationToken)
                .ConfigureAwait(false);
            known.Add(Path.GetFullPath(created.Manifest.SourcePackagePath));
            known.Add(edit.SourcePackagePath);
            if (!all.Any(record => record.ManifestPath.Equals(created.ManifestPath, StringComparison.OrdinalIgnoreCase)))
                all.Add(created);
        }
        return all;
    }

    private static int KindOrder(UnifiedPackageAssetKind kind) => kind switch
    {
        UnifiedPackageAssetKind.Texture2D => 0,
        UnifiedPackageAssetKind.SkeletalMesh => 1,
        UnifiedPackageAssetKind.MaterialInstanceConstant => 2,
        UnifiedPackageAssetKind.StaticMesh => 3,
        _ => 4,
    };

    private static async Task<(string Alias, string Physical, string Source)?> PackageNamesAsync(
        ArkiveModLocalEdit edit,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (edit.Entry)
            {
                case GuidedTextureModProjectEntry texture:
                    return (texture.Manifest.PackageAlias, texture.Manifest.PhysicalPackageName, texture.Manifest.SourcePackagePath);
                case SkeletalMeshEditWorkspaceEntry mesh:
                    var meshSource = await ArkiveModEditSnapshot.LoadSkeletalSourceAsync(mesh, cancellationToken).ConfigureAwait(false);
                    return (meshSource.PackageAlias, meshSource.PhysicalPackageName, meshSource.SourcePackagePath);
                case MaterialInstanceEditEntry material:
                    return (material.Document.PackageAlias, Path.GetFileName(material.Document.SourcePackagePath),
                        material.Document.SourcePackagePath);
                case StaticMeshEditEntry staticMesh:
                    var staticSource = await StaticMeshEditableExportService.LoadManifestAsync(
                        staticMesh.Manifest.SourceEditableManifestPath, cancellationToken).ConfigureAwait(false);
                    return (staticSource.PackageAlias, staticSource.PhysicalPackageName, staticSource.SourcePackagePath);
                case ParticleEffectEditEntry particle:
                    return (particle.Document.PackageAlias, particle.Document.PhysicalPackageName, particle.Document.SourcePackagePath);
                case PhysicsAssetEditEntry physics:
                    return (physics.Document.PackageAlias, physics.Document.PhysicalPackageName, physics.Document.SourcePackagePath);
                case InterfaceMovieEditEntry movie:
                    return (movie.Manifest.PackageAlias, Path.GetFileName(movie.Manifest.SourcePackagePath), movie.Manifest.SourcePackagePath);
                default:
                    return null;
            }
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException or
                                              NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A build, receipt or game file that could not be read leaves that check inconclusive: the build is not shown as
    /// in the game on its account, and the other builds and packages are still checked. Damaged JSON counts too; it
    /// used to end the whole read, which left every package without its state.
    /// </summary>
    private static bool Inconclusive(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or
            InvalidDataException or System.Text.Json.JsonException;

    private ArkiveModDeploymentRequest Request(GuidedTexturePackageRevision revision) =>
        new(revision.ModBundleManifestPath, _backupRoot, _stateRoot) { ReuseKnownHashes = true };
}
