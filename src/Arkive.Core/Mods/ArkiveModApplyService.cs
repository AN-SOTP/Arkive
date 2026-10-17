using System.Diagnostics;
using System.Text.Json;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.StaticMeshes;

namespace Arkive.Core.Mods;

public enum ArkiveModPackageAction
{
    None,
    Install,
    Rebuild,
    Restore,
    Blocked,
}

public sealed record ArkiveModPackageContributor(
    string? ModId,
    string ModName,
    int Priority,
    string AssetName,
    UnifiedPackageAssetKind Kind,
    string ObjectName,
    bool IsDraft);

public sealed record ArkiveModPendingSetup(
    string ModId,
    string ModName,
    ArkiveModFileAsset Asset,
    ArkiveModAssetBinding? ReplacesBinding,
    string Reason);

public sealed record ArkiveModPackagePlan(
    string PhysicalPackageName,
    string PackageAlias,
    string? SourcePackagePath,
    string? PackageProjectId,
    ArkiveModPackageAction Action,
    bool RestoreFirst,
    string? ActiveBundleManifestPath,
    IReadOnlyList<ArkiveModPackageContributor> Contributors,
    IReadOnlyList<ArkiveModPendingSetup> Setups,
    string Reason)
{
    public string ContentFingerprint { get; init; } = string.Empty;

    public string Describe => Action switch
    {
        ArkiveModPackageAction.None => "Up to date",
        ArkiveModPackageAction.Install => "Install existing verified build",
        ArkiveModPackageAction.Rebuild => RestoreFirst ? "Restore, rebuild, and reinstall" : "Rebuild and install",
        ArkiveModPackageAction.Restore => "Restore the original package",
        _ => "Blocked",
    };
}

public sealed record ArkiveModApplyPlan(
    ArkiveModResolution Resolution,
    IReadOnlyList<ArkiveModPackagePlan> Packages,
    IReadOnlyList<string> BlockingProblems,
    IReadOnlyList<string> Warnings)
{
    public bool HasChanges => Packages.Any(package => package.Action is not ArkiveModPackageAction.None);

    public bool CanApply => BlockingProblems.Count == 0;

    public string Fingerprint => string.Join("|", Packages
        .OrderBy(package => package.PhysicalPackageName, StringComparer.OrdinalIgnoreCase)
        .Select(package =>
            $"{package.PhysicalPackageName.ToUpperInvariant()}:{package.Action}:{package.RestoreFirst}:{package.ContentFingerprint}:" +
            string.Join(",", package.Contributors.Select(item => $"{item.ModId}/{item.ObjectName}".ToUpperInvariant())
                .Order(StringComparer.Ordinal)) + ":" +
            string.Join(",", package.Setups.Select(item => item.Asset.AssetId.ToUpperInvariant()).Order(StringComparer.Ordinal))));

    public string Summary
    {
        get
        {
            if (!CanApply) return BlockingProblems[0];
            var changed = Packages.Where(package => package.Action != ArkiveModPackageAction.None).ToArray();
            if (changed.Length == 0) return "Every enabled mod is already in game. Nothing needs to change.";
            var rebuild = changed.Count(package => package.Action == ArkiveModPackageAction.Rebuild);
            var install = changed.Count(package => package.Action == ArkiveModPackageAction.Install);
            var restore = changed.Count(package => package.Action == ArkiveModPackageAction.Restore);
            var setup = changed.Sum(package => package.Setups.Count);
            return $"{changed.Length:N0} package(s) change: {rebuild:N0} rebuild, {install:N0} install, " +
                   $"{restore:N0} restore; {setup:N0} mod asset(s) will be set up from .arkive files.";
        }
    }
}

public sealed record ArkiveModApplyProgress(int Step, int TotalSteps, string Package, string Message);

public sealed record ArkiveModPackageOutcome(
    string PhysicalPackageName,
    ArkiveModPackageAction Action,
    bool Succeeded,
    string Message,
    string? RevisionId = null);

public sealed record ArkiveModApplyResult(
    ArkiveModApplyPlan Plan,
    IReadOnlyList<ArkiveModPackageOutcome> Packages,
    int SetUpAssetCount,
    int RebuiltPackageCount,
    int InstalledPackageCount,
    int RestoredPackageCount,
    TimeSpan Duration)
{
    public bool Succeeded => Packages.All(package => package.Succeeded);
}

public sealed record ArkiveModAssetUpdateOutcome(
    string AssetId,
    string DisplayName,
    bool Succeeded,
    bool Changed,
    string Message);

public enum ArkiveModSetupState
{
    NotSetUp,
    Ready,
    EditMissing,
}

public sealed record ArkiveModAssetStatus(
    ArkiveModFileAsset Asset,
    ArkiveModAssetOutcome Outcome,
    ArkiveModAssetContender? Competitor,
    ArkiveModAssetBinding? Binding,
    ArkiveModLocalEdit? Edit,
    ArkiveModSetupState Setup,
    bool ModifiedLocally,
    bool? InGame);

public sealed record ArkiveModStatus(
    ArkiveModLibraryEntry Mod,
    bool FileMissing,
    IReadOnlyList<ArkiveModAssetStatus> Assets)
{
    public int WinningCount => Assets.Count(asset => asset.Outcome == ArkiveModAssetOutcome.Winning);

    public int OverriddenCount => Assets.Count(asset => asset.Outcome == ArkiveModAssetOutcome.Overridden);

    public int TiedCount => Assets.Count(asset => asset.Outcome == ArkiveModAssetOutcome.Tied);

    public bool ModifiedLocally => Assets.Any(asset => asset.ModifiedLocally);

    /// <summary>A game package this mod targets was changed outside this Arkive (see FindOutsideChangesAsync).</summary>
    public bool OutsideChange { get; init; }

    private IEnumerable<ArkiveModAssetStatus> Winners =>
        Assets.Where(asset => asset.Outcome == ArkiveModAssetOutcome.Winning);

    public bool InGame => !FileMissing && Mod.Enabled && TiedCount == 0 && WinningCount > 0 &&
                          Winners.All(asset => asset.Setup == ArkiveModSetupState.Ready && asset.InGame == true);

    public string State
    {
        get
        {
            if (FileMissing) return "File missing";
            if (!Mod.Enabled) return "Disabled";
            if (TiedCount > 0) return "Conflict needs a choice";
            if (WinningCount == 0) return "Fully overridden";
            if (ModifiedLocally) return "Unsaved edits";
            if (OutsideChange && !InGame) return "Game file changed outside Arkive";
            if (Winners.Any(asset => asset.Setup != ArkiveModSetupState.Ready)) return "Ready to apply";
            return InGame ? OverriddenCount > 0 ? "Applied · partially overridden" : "Applied" : "Ready to apply";
        }
    }

    public string ConflictSummary => TiedCount > 0
        ? $"{TiedCount:N0} tied"
        : OverriddenCount > 0
            ? $"{OverriddenCount:N0} overridden"
            : Assets.Any(asset => asset.Competitor is not null)
                ? "Wins all conflicts"
                : "None";
}

public sealed record ArkiveModApplyContext(
    ArkiveModLibrary Library,
    ArkiveModEditWorkspaces Edits,
    GuidedTexturePackageWorkspace Packages,
    ArkiveModSetWorkspace ModSets,
    ArkiveModDeploymentService Deployment,
    string BackupRootDirectory,
    string InstallationStateRoot)
{
    public ArkiveModDeploymentRequest DeploymentRequest(string bundleManifestPath) => new(
        bundleManifestPath, Path.GetFullPath(BackupRootDirectory), Path.GetFullPath(InstallationStateRoot));

    public async Task<(GuidedTexturePackageRevision Revision, ModDeploymentInspection Inspection)?> FindActiveDeploymentAsync(
        GuidedTexturePackageEntry package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        foreach (var revision in package.Manifest.Revisions
                     .Where(revision => File.Exists(revision.ModBundleManifestPath))
                     .OrderByDescending(revision => revision.CreatedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string modId;
            try
            {
                modId = (await ArkiveModBundleService.LoadBundleAsync(revision.ModBundleManifestPath, cancellationToken)
                    .ConfigureAwait(false)).ModId;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
                                                  NotSupportedException or UnauthorizedAccessException)
            {
                continue;
            }
            var receiptPath = Path.Combine(Path.GetFullPath(InstallationStateRoot), modId,
                ArkiveModDeploymentService.ReceiptFileName);
            if (!File.Exists(receiptPath)) continue;
            var receipt = await ArkiveModDeploymentService.LoadReceiptAsync(receiptPath, cancellationToken)
                .ConfigureAwait(false);
            if (!receipt.GameFilesModified &&
                receipt.State is not (ModInstallationState.Installed or ModInstallationState.Prepared)) continue;
            var inspection = await Deployment.InspectForDeletionIdentityAsync(
                DeploymentRequest(revision.ModBundleManifestPath), cancellationToken).ConfigureAwait(false);
            if (inspection.HasActiveDeployment) return (revision, inspection);
        }
        return null;
    }
}

public sealed class ArkiveModApplyService
{
    public const string UtilitySetId = "apply-arkive-mods";
    private readonly ArkiveModApplyContext _context;
    private readonly ArkiveModMaterializationService _materializer;
    private readonly UnifiedPackageComposerService? _composer;
    private readonly string _rebuildUnavailable;

    /// <param name="composerUnavailableReason">Why <paramref name="composer"/> is null, shown when a rebuild is needed.</param>
    public ArkiveModApplyService(
        ArkiveModApplyContext context,
        ArkiveModMaterializationService materializer,
        UnifiedPackageComposerService? composer,
        string? composerUnavailableReason = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        _composer = composer;
        _rebuildUnavailable = "Package rebuilding is unavailable in this session: " +
            (composerUnavailableReason ?? "UModel or the texture pipeline is missing").TrimEnd('.') + ".";
    }

    public static async Task<IReadOnlyList<ArkiveModStatus>> DescribeAsync(
        ArkiveModLibrary library,
        ArkiveModLibraryManifest manifest,
        ArkiveModEditSnapshot snapshot,
        IReadOnlyDictionary<string, GuidedTexturePackageRevision?>? activeRevisionsByPhysicalName = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? outsideChangedPackages = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(snapshot);
        var resolution = ArkiveModConflictResolver.Resolve(manifest.Mods);
        var statuses = new List<ArkiveModStatus>(manifest.Mods.Count);
        foreach (var mod in manifest.Mods)
        {
            var assets = new List<ArkiveModAssetStatus>(mod.Manifest.Assets.Count);
            foreach (var asset in mod.Manifest.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var binding = mod.BindingFor(asset.AssetId);
                var edit = binding is null ? null : snapshot.Find(binding.EditId);
                var setup = binding is null
                    ? ArkiveModSetupState.NotSetUp
                    : edit is null ? ArkiveModSetupState.EditMissing : ArkiveModSetupState.Ready;
                var modified = false;
                bool? inGame = null;
                if (edit is not null)
                {
                    modified = await ArkiveModAuthoringService.IsModifiedAsync(edit, binding!, cancellationToken)
                        .ConfigureAwait(false);
                    if (activeRevisionsByPhysicalName is not null)
                    {
                        activeRevisionsByPhysicalName.TryGetValue(
                            ArkiveModFileService.PhysicalPackageKey(asset.Target.PhysicalPackageName), out var active);
                        var built = await ArkiveModEditSnapshot.GetBuildContentSha256Async(edit, cancellationToken)
                            .ConfigureAwait(false);
                        inGame = ChangesNothing(edit) || active is not null && built is not null &&
                                 RevisionContent(active).Contains(ContentKey(edit.EditId, built));
                    }
                }
                assets.Add(new ArkiveModAssetStatus(
                    asset, resolution.OutcomeFor(mod.ModId, asset.AssetId),
                    resolution.StrongestCompetitor(mod.ModId, asset.AssetId), binding, edit, setup, modified, inGame));
            }
            statuses.Add(new ArkiveModStatus(mod, !File.Exists(library.GetModFilePath(mod)), assets)
            {
                OutsideChange = outsideChangedPackages is not null && mod.Manifest.Assets.Any(asset =>
                    outsideChangedPackages.Contains(ArkiveModFileService.NormalizePhysicalPackageName(asset.Target.PhysicalPackageName),
                        StringComparer.OrdinalIgnoreCase)),
            });
        }
        return statuses;
    }

    public Task<ArkiveModApplyPlan> PlanAsync(CancellationToken cancellationToken = default) =>
        PlanAsync(null, cancellationToken);

    public Task<ArkiveModApplyPlan> PreviewModAsync(string modId, CancellationToken cancellationToken = default) =>
        PlanAsync(modId, cancellationToken);

    private async Task<ArkiveModApplyPlan> PlanAsync(string? previewModId, CancellationToken cancellationToken)
    {
        var library = await _context.Library.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (previewModId is not null)
        {
            if (library.Find(previewModId) is null) Refuse("APL021", "The mod is missing from the library.");
            library = library with
            { Mods = library.Mods.Select(mod => mod.ModId == previewModId ? mod with { Enabled = true } : mod).ToArray() };
        }
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_context.Edits, cancellationToken).ConfigureAwait(false);
        var packages = await _context.Packages.LoadAsync(cancellationToken).ConfigureAwait(false);
        var resolution = ArkiveModConflictResolver.Resolve(library.Mods);
        var problems = new List<string>();
        var warnings = new List<string>();
        foreach (var tie in resolution.BlockingTies)
            problems.Add(
                $"{tie.ObjectName} ({tie.PackageAlias}) is replaced by {string.Join(" and ", tie.Tied.Select(item => item.ModName))} " +
                $"at the same priority {tie.Tied[0].Priority}. Give one of them a higher priority.");
        foreach (var mod in library.Mods.Where(mod => mod.Enabled &&
                     !File.Exists(_context.Library.GetModFilePath(mod))))
            problems.Add($"{mod.Manifest.Name} is enabled but its .arkive file is missing from the mods folder.");

        var gameDirectory = library.GamePackagesDirectory;
        var physicalNames = library.Mods
            .SelectMany(mod => mod.Manifest.Assets.Select(asset => asset.Target.PhysicalPackageName))
            .Select(name => ArkiveModFileService.NormalizePhysicalPackageName(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var plans = new List<ArkiveModPackagePlan>(physicalNames.Length);
        foreach (var physical in physicalNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ArkiveModFileService.PhysicalPackageKey(physical);
            var winners = resolution.Targets
                .Where(target => target.Key.PhysicalPackageName == key && target.Winner is not null)
                .Select(target => (Mod: library.Find(target.Winner!.ModId)!, target.Winner.Asset))
                .ToArray();
            var project = FindPackageProject(physical, gameDirectory, packages);
            var alias = project?.Manifest.PackageAlias ??
                        library.Mods.SelectMany(mod => mod.Manifest.Assets)
                            .First(asset => ArkiveModFileService.NormalizePhysicalPackageName(
                                asset.Target.PhysicalPackageName).Equals(physical, StringComparison.OrdinalIgnoreCase))
                            .Target.PackageAlias;
            var packagePath = project?.Manifest.SourcePackagePath ??
                              (gameDirectory is null
                                  ? winners.Select(item => item.Mod.BindingFor(item.Asset.AssetId)?.SourcePackagePath)
                                      .FirstOrDefault(path => path is not null)
                                  : Path.Combine(gameDirectory, physical));
            if (packagePath is null || !File.Exists(packagePath))
            {
                if (winners.Length == 0) continue;
                var message = packagePath is null
                    ? $"{physical} ({alias}) has not been located yet. Choose Lost Ark's EFGame\\ReleasePC\\Packages folder."
                    : $"{physical} ({alias}) was not found at {packagePath}.";
                problems.Add(message);
                plans.Add(new ArkiveModPackagePlan(physical, alias, packagePath, project?.Manifest.PackageProjectId,
                    ArkiveModPackageAction.Blocked, false, null, Contributors(winners, []), [], message));
                continue;
            }
            packagePath = Path.GetFullPath(packagePath);
            var tie = resolution.BlockingTies.FirstOrDefault(target => target.Key.PhysicalPackageName == key);
            if (tie is not null)
            {
                plans.Add(new ArkiveModPackagePlan(physical, alias, packagePath, project?.Manifest.PackageProjectId,
                    ArkiveModPackageAction.Blocked, false, null, Contributors(winners, []), [],
                    $"{tie.ObjectName} is tied between {string.Join(" and ", tie.Tied.Select(item => item.ModName))}."));
                continue;
            }

            var active = project is null
                ? null
                : await _context.FindActiveDeploymentAsync(project, cancellationToken).ConfigureAwait(false);
            var cleanPath = active is null ? packagePath : active.Value.Inspection.BackupPath;
            var cleanHash = active is null
                ? await ArkiveModEditSnapshot.HashFileCachedAsync(packagePath, cancellationToken).ConfigureAwait(false)
                : active.Value.Inspection.Bundle.RequiredSourcePackageSha256.ToUpperInvariant();

            var packageProblems = new List<string>();
            var setups = new List<ArkiveModPendingSetup>();
            var desired = new HashSet<string>(StringComparer.Ordinal);
            var contentKnown = true;
            var unchanged = 0;
            foreach (var (mod, asset) in winners)
            {
                var binding = mod.BindingFor(asset.AssetId);
                var local = binding is null ? null : snapshot.Find(binding.EditId);
                if (binding is null || local is null)
                {
                    setups.Add(new ArkiveModPendingSetup(mod.ModId, mod.Manifest.Name, asset, binding,
                        binding is null ? "Not set up on this computer yet." : "Its local working copy is missing."));
                    continue;
                }
                if (await ArkiveModAuthoringService.IsModifiedAsync(local, binding, cancellationToken)
                        .ConfigureAwait(false))
                {
                    packageProblems.Add($"{mod.Manifest.Name}: {asset.DisplayName} has unsaved edits. Save the mod before applying.");
                    continue;
                }
                if (!ArkiveModEditSnapshot.SamePath(local.SourcePackagePath, packagePath))
                {
                    setups.Add(new ArkiveModPendingSetup(mod.ModId, mod.Manifest.Name, asset, binding,
                        "The editing workspace used a clean backup; it will be prepared against the restored game package."));
                    continue;
                }
                var localSource = await ArkiveModEditSnapshot.GetSourcePackageSha256Async(local, cancellationToken)
                    .ConfigureAwait(false);
                if (localSource is null || !localSource.Equals(cleanHash, StringComparison.OrdinalIgnoreCase))
                {
                    if (await ArkiveModAuthoringService.IsModifiedAsync(local, binding, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        packageProblems.Add(
                            $"{mod.Manifest.Name}: {asset.DisplayName} has local changes made for an older client. " +
                            "Save them to the .arkive file first, then apply again.");
                        continue;
                    }
                    var check = await _materializer.CheckTargetAsync(asset, cleanPath, cleanHash, cancellationToken)
                        .ConfigureAwait(false);
                    if (check.State == ArkiveModTargetState.Exact)
                        setups.Add(new ArkiveModPendingSetup(mod.ModId, mod.Manifest.Name, asset, binding,
                            "The client was updated but this asset is unchanged; it will be set up again automatically."));
                    else
                        packageProblems.Add(
                            $"{mod.Manifest.Name}: {asset.DisplayName} — {check.Message}" +
                            (check.State == ArkiveModTargetState.Changed
                                ? " Select the mod and choose 'Update for this game version'."
                                : string.Empty));
                    continue;
                }
                if (ChangesNothing(local))
                {
                    unchanged++;
                    continue;
                }
                var content = await ArkiveModEditSnapshot.GetBuildContentSha256Async(local, cancellationToken)
                    .ConfigureAwait(false);
                if (content is null) contentKnown = false;
                else desired.Add(ContentKey(local.EditId, content));
            }

            var winnerKeys = winners.Select(item => ArkiveModTargetKey.From(item.Asset)).ToHashSet();
            var drafts = snapshot.DraftsFor(packagePath, library);
            foreach (var draft in drafts)
            {
                if (winnerKeys.Contains(draft.TargetKey))
                {
                    var winner = winners.First(item => ArkiveModTargetKey.From(item.Asset) == draft.TargetKey);
                    packageProblems.Add(
                        $"The checked draft '{draft.DisplayName}' and mod '{winner.Mod.Manifest.Name}' both replace " +
                        $"{draft.ObjectName}. Uncheck the draft or add it to a mod.");
                    continue;
                }
                if (ChangesNothing(draft))
                {
                    unchanged++;
                    continue;
                }
                var content = await ArkiveModEditSnapshot.GetBuildContentSha256Async(draft, cancellationToken)
                    .ConfigureAwait(false);
                if (content is null) contentKnown = false;
                else desired.Add(ContentKey(draft.EditId, content));
            }
            foreach (var duplicate in drafts.GroupBy(draft => draft.TargetKey).Where(group => group.Count() > 1))
                packageProblems.Add(
                    $"More than one checked draft replaces {duplicate.First().ObjectName} in {physical}. Keep one.");
            if (drafts.Count > 0)
                warnings.Add($"{physical} also includes {drafts.Count:N0} checked draft edit(s) that belong to no mod.");

            // Mods set up from their .arkive files must start from the clean package they were made for. A package
            // this Arkive never changed but whose target objects differ was changed elsewhere; building on it would
            // keep those changes and later back them up as the "original".
            if (active is null)
            {
                foreach (var setup in setups.Where(setup =>
                             !setup.Asset.Target.SourcePackageSha256.Equals(cleanHash, StringComparison.OrdinalIgnoreCase)))
                {
                    var check = await _materializer.CheckTargetAsync(setup.Asset, cleanPath, cleanHash, cancellationToken)
                        .ConfigureAwait(false);
                    if (check.State == ArkiveModTargetState.Exact) continue;
                    packageProblems.Add(OutsideChangeMessage(physical, alias, setup.ModName, setup.Asset.Target.ObjectName));
                }
            }

            var contributors = Contributors(winners, drafts);
            if (packageProblems.Count > 0)
            {
                problems.AddRange(packageProblems);
                plans.Add(new ArkiveModPackagePlan(physical, alias, packagePath, project?.Manifest.PackageProjectId,
                    ArkiveModPackageAction.Blocked, false, active?.Revision.ModBundleManifestPath, contributors, setups,
                    packageProblems[0]));
                continue;
            }

            ArkiveModPackageAction action;
            string reason;
            var restoreFirst = false;
            if (winners.Length + drafts.Count == unchanged && setups.Count == 0)
            {
                action = active is null ? ArkiveModPackageAction.None : ArkiveModPackageAction.Restore;
                reason = unchanged > 0
                    ? active is null
                        ? "The enabled mods leave every asset in this package as the game has it."
                        : "The enabled mods leave every asset here as the game has it; the original package will be restored."
                    : active is null
                        ? "No enabled mod replaces assets in this package."
                        : "No enabled mod replaces assets here any more; the original package will be restored.";
            }
            else if (setups.Count > 0 || !contentKnown)
            {
                action = ArkiveModPackageAction.Rebuild;
                restoreFirst = active is not null;
                reason = setups.Count > 0
                    ? $"{setups.Count:N0} mod asset(s) must be set up from their .arkive file."
                    : "A checked asset has no verified build content yet.";
            }
            else if (active is not null)
            {
                var activeMatches = RevisionContent(active.Value.Revision).SetEquals(desired) &&
                                    active.Value.Revision.SourcePackageSha256.Equals(
                                        cleanHash, StringComparison.OrdinalIgnoreCase);
                action = activeMatches ? ArkiveModPackageAction.None : ArkiveModPackageAction.Rebuild;
                restoreFirst = !activeMatches;
                reason = activeMatches
                    ? "The installed package already contains exactly these mod assets."
                    : "The installed package differs from the enabled mods and priorities.";
            }
            else
            {
                var latest = Latest(project);
                var reusable = latest is not null && File.Exists(latest.ModBundleManifestPath) &&
                               File.Exists(latest.PayloadPath) &&
                               !latest.PreviewOnly &&
                               (!latest.ContainsVariableSizeMeshReplacements || latest.VariableSizeDeploymentApproved) &&
                               latest.SourcePackageSha256.Equals(cleanHash, StringComparison.OrdinalIgnoreCase) &&
                               RevisionContent(latest).SetEquals(desired);
                action = reusable ? ArkiveModPackageAction.Install : ArkiveModPackageAction.Rebuild;
                reason = reusable
                    ? "The latest verified build already matches; it only needs to be installed."
                    : "The package must be rebuilt from the clean client with the winning mod assets.";
            }
            plans.Add(new ArkiveModPackagePlan(physical, alias, packagePath, project?.Manifest.PackageProjectId,
                action, restoreFirst, active?.Revision.ModBundleManifestPath, contributors, setups, reason)
            {
                ContentFingerprint = cleanHash + ":" + string.Join(",", desired.Order(StringComparer.Ordinal)) + ":" +
                    string.Join(",", setups.Select(setup => ArkiveModFileService.ComputeContentSha256(setup.Asset))
                        .Order(StringComparer.Ordinal)),
            });
        }

        if (plans.Any(plan => plan.Action == ArkiveModPackageAction.Rebuild) && _composer is null)
            problems.Add(_rebuildUnavailable);
        return new ArkiveModApplyPlan(resolution, plans, problems.Distinct().ToArray(), warnings);
    }

    public async Task<ArkiveModApplyResult> ApplyAsync(
        string? expectedPlanFingerprint = null,
        IProgress<ArkiveModApplyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var plan = await PlanAsync(cancellationToken).ConfigureAwait(false);
        if (expectedPlanFingerprint is not null &&
            !expectedPlanFingerprint.Equals(plan.Fingerprint, StringComparison.Ordinal))
            Refuse("APL001", "The mod library or game files changed since the plan was reviewed. Review it again.");
        if (!plan.CanApply) Refuse("APL002", plan.Summary);
        var outcomes = new Dictionary<string, ArkiveModPackageOutcome>(StringComparer.OrdinalIgnoreCase);
        var changing = plan.Packages.Where(package => package.Action != ArkiveModPackageAction.None).ToArray();
        var totalSteps = Math.Max(1,
            changing.Count(package => package.RestoreFirst || package.Action == ArkiveModPackageAction.Restore) +
            changing.Sum(package => package.Setups.Count) +
            changing.Count(package => package.Action == ArkiveModPackageAction.Rebuild) + 2);
        var step = 0;
        void Report(string package, string message) =>
            progress?.Report(new ArkiveModApplyProgress(++step, totalSteps, package, message));

        var restored = 0;
        var restoredForRebuild = new List<ArkiveModPackagePlan>();
        foreach (var package in changing.Where(package =>
                     package.RestoreFirst || package.Action == ArkiveModPackageAction.Restore))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(package.PhysicalPackageName, "Restoring the verified original package...");
            try
            {
                await _context.Deployment.RestoreAsync(
                        _context.DeploymentRequest(package.ActiveBundleManifestPath!), cancellationToken)
                    .ConfigureAwait(false);
                restored++;
                if (package.Action == ArkiveModPackageAction.Restore)
                    outcomes[package.PhysicalPackageName] = new ArkiveModPackageOutcome(
                        package.PhysicalPackageName, package.Action, true, "Original package restored.");
                else
                    restoredForRebuild.Add(package);
            }
            catch (Exception exception) when (IsOperational(exception))
            {
                outcomes[package.PhysicalPackageName] = new ArkiveModPackageOutcome(
                    package.PhysicalPackageName, package.Action, false, $"Restore failed: {exception.Message}");
            }
        }

        var setUp = 0;
        foreach (var package in changing.Where(package => package.Setups.Count > 0 &&
                                                          !outcomes.ContainsKey(package.PhysicalPackageName)))
        {
            foreach (var pending in package.Setups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(package.PhysicalPackageName, $"Setting up {pending.Asset.DisplayName} from {pending.ModName}...");
                try
                {
                    await SetUpAssetAsync(pending.ModId, pending.Asset, package.SourcePackagePath!,
                        allowChangedSource: false, cancellationToken).ConfigureAwait(false);
                    setUp++;
                }
                catch (Exception exception) when (IsOperational(exception))
                {
                    outcomes[package.PhysicalPackageName] = new ArkiveModPackageOutcome(
                        package.PhysicalPackageName, package.Action, false,
                        $"{pending.ModName}: {pending.Asset.DisplayName} could not be set up: {exception.Message}");
                    break;
                }
            }
        }

        Report("Mods", "Applying priorities to the package selections...");
        await ArkiveModSelectionService.SynchronizeAsync(_context.Library, _context.Edits, cancellationToken)
            .ConfigureAwait(false);

        var rebuilt = 0;
        var installable = new List<GuidedTexturePackageEntry>();
        var projects = await _context.Packages.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var package in changing.Where(package => !outcomes.ContainsKey(package.PhysicalPackageName)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (package.Action == ArkiveModPackageAction.Install)
            {
                installable.Add(projects.First(item => item.Manifest.PackageProjectId.Equals(
                    package.PackageProjectId, StringComparison.OrdinalIgnoreCase)));
                continue;
            }
            if (package.Action != ArkiveModPackageAction.Rebuild) continue;
            Report(package.PhysicalPackageName, "Rebuilding the package from the clean client...");
            var built = await RebuildAsync(package, cancellationToken).ConfigureAwait(false);
            if (built.Error is not null)
            {
                outcomes[package.PhysicalPackageName] = new ArkiveModPackageOutcome(
                    package.PhysicalPackageName, package.Action, false, $"Rebuild failed: {built.Error}");
                continue;
            }
            if (built.Package is null)
            {
                outcomes[package.PhysicalPackageName] = new ArkiveModPackageOutcome(
                    package.PhysicalPackageName, package.Action, true,
                    "Left as the game has it: the enabled mods change nothing in this package.");
                continue;
            }
            rebuilt++;
            installable.Add(built.Package!);
            outcomes[package.PhysicalPackageName] = new ArkiveModPackageOutcome(
                package.PhysicalPackageName, package.Action, true, "Rebuilt; awaiting installation.",
                built.Package!.Manifest.LatestRevisionId);
        }

        var installed = 0;
        if (installable.Count > 0)
        {
            Report("Mods", $"Installing {installable.Count:N0} package(s) as one transaction...");
            try
            {
                var set = await _context.ModSets.UpsertUtilitySetAsync(
                    UtilitySetId, "Apply Arkive mods", installable, cancellationToken).ConfigureAwait(false);
                var service = new ArkiveModSetService(_context.ModSets, _context.Packages, _context.Deployment);
                var request = new ModSetDeploymentRequest(
                    set.ManifestPath, Path.GetFullPath(_context.BackupRootDirectory),
                    Path.GetFullPath(_context.InstallationStateRoot));
                var inspection = await service.InspectAsync(request, cancellationToken).ConfigureAwait(false);
                if (!inspection.CanInstall && inspection.State != ModSetInstallationState.Installed)
                    throw new ArkiveModRefusalException("APL010", inspection.Summary);
                if (inspection.State != ModSetInstallationState.Installed)
                    await service.InstallAsync(request, cancellationToken).ConfigureAwait(false);
                installed = installable.Count;
                RecordInstallOutcomes(true, "Installed and verified.");
            }
            catch (Exception exception) when (IsOperational(exception))
            {
                RecordInstallOutcomes(false, $"Installation was rolled back: {exception.Message}");
            }
        }
        foreach (var package in restoredForRebuild)
        {
            if (outcomes.GetValueOrDefault(package.PhysicalPackageName) is not { Succeeded: false } failed) continue;
            try
            {
                await _context.Deployment.InstallAsync(
                        _context.DeploymentRequest(package.ActiveBundleManifestPath!), cancellationToken)
                    .ConfigureAwait(false);
                restored--;
                outcomes[package.PhysicalPackageName] = failed with
                {
                    Message = failed.Message + " The previous build was reinstalled.",
                };
            }
            catch (Exception exception) when (IsOperational(exception))
            {
                outcomes[package.PhysicalPackageName] = failed with
                {
                    Message = failed.Message + $" The previous build could not be reinstalled: {exception.Message}",
                };
            }
        }
        Report("Mods", "Apply finished.");
        watch.Stop();

        void RecordInstallOutcomes(bool succeeded, string message)
        {
            foreach (var package in installable)
            {
                var physical = Path.GetFileName(package.Manifest.SourcePackagePath);
                var action = changing.First(item => item.PhysicalPackageName.Equals(
                    physical, StringComparison.OrdinalIgnoreCase)).Action;
                outcomes[physical] = new ArkiveModPackageOutcome(
                    physical, action, succeeded, message, succeeded ? package.Manifest.LatestRevisionId : null);
            }
        }
        return new ArkiveModApplyResult(
            plan,
            changing.Select(package => outcomes.GetValueOrDefault(package.PhysicalPackageName) ??
                                       new ArkiveModPackageOutcome(package.PhysicalPackageName, package.Action, true,
                                           "Completed.")).ToArray(),
            setUp, rebuilt, installed, restored, watch.Elapsed);
    }

    public async Task<string> ResolveCleanPackageAsync(
        string packagePath, CancellationToken cancellationToken = default)
    {
        var packages = await _context.Packages.LoadAsync(cancellationToken).ConfigureAwait(false);
        var project = packages.FirstOrDefault(package =>
            ArkiveModEditSnapshot.SamePath(package.Manifest.SourcePackagePath, packagePath));
        var active = project is null ? null : await _context.FindActiveDeploymentAsync(project, cancellationToken)
            .ConfigureAwait(false);
        if (active is null) return Path.GetFullPath(packagePath);
        var backup = active.Value.Inspection.BackupPath;
        if (!File.Exists(backup) || !(await ArkiveModEditSnapshot.HashFileCachedAsync(backup, cancellationToken)
                .ConfigureAwait(false)).Equals(active.Value.Inspection.Bundle.RequiredSourcePackageSha256,
                StringComparison.OrdinalIgnoreCase))
            Refuse("APL025", "The installed package has no verified clean backup. Restore or repair it before editing.");
        return backup;
    }

    public async Task PrepareModForEditingAsync(string modId, CancellationToken cancellationToken = default)
    {
        var library = await _context.Library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var mod = library.Find(modId) ?? throw new ArkiveModRefusalException("APL021", "The mod is missing from the library.");
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_context.Edits, cancellationToken).ConfigureAwait(false);
        foreach (var asset in mod.Manifest.Assets)
        {
            var binding = mod.BindingFor(asset.AssetId);
            if (binding is not null && snapshot.Find(binding.EditId) is not null) continue;
            var packagePath = library.GamePackagesDirectory is { } gameDirectory
                ? Path.Combine(gameDirectory, ArkiveModFileService.NormalizePhysicalPackageName(asset.Target.PhysicalPackageName))
                : binding?.SourcePackagePath ?? throw new ArkiveModRefusalException("APL022", "Choose the Lost Ark game folder first.");
            var clean = await ResolveCleanPackageAsync(packagePath, cancellationToken).ConfigureAwait(false);
            await SetUpAssetAsync(modId, asset, clean, allowChangedSource: false, cancellationToken).ConfigureAwait(false);
        }
        await ArkiveModSelectionService.SynchronizeAsync(_context.Library, _context.Edits, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ArkiveModAssetUpdateOutcome>> UpdateModForCurrentClientAsync(
        string modId,
        CancellationToken cancellationToken = default)
    {
        var library = await _context.Library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var mod = library.Find(modId) ?? throw new ArkiveModRefusalException("APL021", $"Mod {modId} is not in the library.");
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_context.Edits, cancellationToken).ConfigureAwait(false);
        var packages = await _context.Packages.LoadAsync(cancellationToken).ConfigureAwait(false);
        var installed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<ArkiveModAssetUpdateOutcome>();
        foreach (var asset in mod.Manifest.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var physical = ArkiveModFileService.NormalizePhysicalPackageName(asset.Target.PhysicalPackageName);
                var gameDirectory = library.GamePackagesDirectory;
                var project = FindPackageProject(physical, gameDirectory, packages);
                var binding = mod.BindingFor(asset.AssetId);
                var packagePath = project?.Manifest.SourcePackagePath ??
                                  (gameDirectory is null ? binding?.SourcePackagePath : Path.Combine(gameDirectory, physical)) ??
                                  throw new ArkiveModRefusalException("APL022",
                                      "Choose Lost Ark's EFGame\\ReleasePC\\Packages folder first.");
                if (project is not null && !installed.ContainsKey(physical))
                    installed[physical] = await _context.FindActiveDeploymentAsync(project, cancellationToken)
                        .ConfigureAwait(false) is not null;
                if (installed.GetValueOrDefault(physical))
                    Refuse("APL023", $"{physical} has an installed Arkive package. Restore it before updating this mod.");
                var local = binding is null ? null : snapshot.Find(binding.EditId);
                var cleanHash = await ArkiveModEditSnapshot.HashFileCachedAsync(packagePath, cancellationToken)
                    .ConfigureAwait(false);
                if (local is not null &&
                    (await ArkiveModEditSnapshot.GetSourcePackageSha256Async(local, cancellationToken)
                        .ConfigureAwait(false))?.Equals(cleanHash, StringComparison.OrdinalIgnoreCase) == true)
                {
                    outcomes.Add(new ArkiveModAssetUpdateOutcome(asset.AssetId, asset.DisplayName, true, false,
                        "Already set up for this client."));
                    continue;
                }
                if (local is not null && binding is not null &&
                    await ArkiveModAuthoringService.IsModifiedAsync(local, binding, cancellationToken).ConfigureAwait(false))
                    Refuse("APL024", "It has unsaved local changes. Save them to the .arkive file first.");
                var check = await SetUpAssetAsync(mod.ModId, asset, packagePath, allowChangedSource: true, cancellationToken)
                    .ConfigureAwait(false);
                outcomes.Add(new ArkiveModAssetUpdateOutcome(asset.AssetId, asset.DisplayName, true,
                    check == ArkiveModTargetState.Changed,
                    check == ArkiveModTargetState.Changed
                        ? "The game changed this asset; the mod passed every native check and was re-applied to the new version. Review it in UModel before playing."
                        : "Set up for this client."));
            }
            catch (Exception exception) when (IsOperational(exception))
            {
                outcomes.Add(new ArkiveModAssetUpdateOutcome(asset.AssetId, asset.DisplayName, false, false,
                    exception.Message));
            }
        }
        await ArkiveModSelectionService.SynchronizeAsync(_context.Library, _context.Edits, cancellationToken)
            .ConfigureAwait(false);
        return outcomes;
    }

    private async Task<ArkiveModTargetState> SetUpAssetAsync(
        string modId,
        ArkiveModFileAsset asset,
        string packagePath,
        bool allowChangedSource,
        CancellationToken cancellationToken)
    {
        var library = await _context.Library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var mod = library.Find(modId) ?? throw new ArkiveModRefusalException("APL021", $"Mod {modId} is not in the library.");
        var (binding, state) = await _materializer.MaterializeAsync(
            mod, asset, packagePath, allowChangedSource, cancellationToken).ConfigureAwait(false);
        var previous = mod.BindingFor(asset.AssetId);
        await _context.Library.UpdateBindingsAsync(modId, bindings => bindings
                .Where(item => !item.AssetId.Equals(asset.AssetId, StringComparison.OrdinalIgnoreCase))
                .Append(binding).ToArray(), cancellationToken)
            .ConfigureAwait(false);
        if (previous is not null && !previous.EditId.Equals(binding.EditId, StringComparison.OrdinalIgnoreCase))
            await ArkiveModSelectionService.ReleaseAsync(_context.Edits,
                [new ArkiveModReleasedEdit(previous.EditId, mod.Origin == ArkiveModOrigin.Imported)],
                cancellationToken).ConfigureAwait(false);
        return state;
    }

    private async Task<(GuidedTexturePackageEntry? Package, string? Error)> RebuildAsync(
        ArkiveModPackagePlan plan,
        CancellationToken cancellationToken)
    {
        if (_composer is null) return (null, _rebuildUnavailable);
        var package = await _context.Packages.GetOrCreateAsync(
            plan.PackageAlias, plan.PhysicalPackageName, plan.SourcePackagePath!, cancellationToken).ConfigureAwait(false);
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_context.Edits, cancellationToken).ConfigureAwait(false);
        var checkedEdits = snapshot.ForPackage(plan.SourcePackagePath!).Where(edit => edit.Included).ToArray();
        if (checkedEdits.Length == 0) return (null, "No checked assets remain for this package.");
        // A piece identical to the game's own needs no rebuild; the rest of the package is still built.
        var included = checkedEdits.Where(edit => !ChangesNothing(edit)).ToArray();
        if (included.Length == 0) return (null, null);
        var revisionDirectory = _context.Packages.CreateRevisionDirectory(package);
        try
        {
            var materials = new List<MaterialInstanceEditEntry>();
            foreach (var material in included.Select(edit => edit.Entry).OfType<MaterialInstanceEditEntry>())
                materials.Add(await _materializer.EnsureCandidateAsync(material, cancellationToken).ConfigureAwait(false));
            var displayName = !string.IsNullOrWhiteSpace(package.Manifest.DisplayName)
                ? package.Manifest.DisplayName!
                : $"{package.Manifest.PackageAlias} (Arkive mods)";
            var result = await _composer.BuildAsync(
                new UnifiedPackageCompositionRequest(
                    package,
                    included.Select(edit => edit.Entry).OfType<GuidedTextureModProjectEntry>().ToArray(),
                    included.Select(edit => edit.Entry).OfType<SkeletalMeshEditWorkspaceEntry>().ToArray(),
                    revisionDirectory, Path.GetFullPath(_context.BackupRootDirectory), displayName)
                {
                    MaterialEdits = materials,
                    StaticMeshEdits = included.Select(edit => edit.Entry).OfType<StaticMeshEditEntry>().ToArray(),
                    ParticleEdits = included.Select(edit => edit.Entry).OfType<Particles.ParticleEffectEditEntry>().ToArray(),
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var committed = await _context.Packages.CommitRevisionAsync(package.ManifestPath, result.Revision, cancellationToken)
                .ConfigureAwait(false);
            return (committed, null);
        }
        catch (Exception exception) when (IsOperational(exception) || exception is TimeoutException or
                                              System.ComponentModel.Win32Exception)
        {
            try { _context.Packages.DeleteFailedRevisionDirectory(revisionDirectory); }
            catch (IOException) { }
            return (null, exception.Message);
        }
    }

    internal static string OutsideChangeMessage(string physical, string alias, string modName, string objectName) =>
        $"{physical} ({alias}) is not the game file {modName} was made for: its {objectName} differs, and this Arkive " +
        "has no record of changing it. If you installed mods with Arkive from another data folder, restore that file " +
        "with More actions → Restore from another Arkive folder…; otherwise use Steam's Verify integrity of game files. " +
        "If a game patch changed it, select the mod and choose 'Update for this game version'.";

    /// <summary>
    /// Game packages that mods in the library target, which are not the clean versions those mods were made for and
    /// which this Arkive has not installed: changed by another Arkive data folder, another tool or a game patch.
    /// Packages whose target objects are all unchanged are not reported.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<string>> OutsideChangeChecks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ArkiveModOutsideChange>> FindOutsideChangesAsync(CancellationToken cancellationToken = default)
    {
        var library = await _context.Library.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (library.GamePackagesDirectory is not { } gameDirectory || !Directory.Exists(gameDirectory)) return [];
        var packages = await _context.Packages.LoadAsync(cancellationToken).ConfigureAwait(false);
        var changes = new List<ArkiveModOutsideChange>();
        foreach (var group in library.Mods.SelectMany(mod => mod.Manifest.Assets)
                     .GroupBy(asset => ArkiveModFileService.NormalizePhysicalPackageName(asset.Target.PhysicalPackageName),
                         StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(gameDirectory, group.Key);
            if (!File.Exists(path)) continue;
            var project = FindPackageProject(group.Key, gameDirectory, packages);
            if (project is not null &&
                await _context.FindActiveDeploymentAsync(project, cancellationToken).ConfigureAwait(false) is not null)
                continue;
            var hash = await ArkiveModEditSnapshot.HashFileCachedAsync(path, cancellationToken).ConfigureAwait(false);
            var clean = group.Select(asset => asset.Target.SourcePackageSha256.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (clean.Contains(hash, StringComparer.OrdinalIgnoreCase)) continue;
            var assets = group.DistinctBy(asset => asset.Target.ObjectName, StringComparer.OrdinalIgnoreCase).ToArray();
            // The same changed file is compared object by object on every refresh of the Mods list; the answer only
            // changes with the file's bytes or the objects asked about.
            var key = $"{path}|{hash}|{string.Join(",", assets.Select(asset => $"{asset.Target.ObjectName}:{asset.Target.SourcePackageSha256}"))}";
            if (!OutsideChangeChecks.TryGetValue(key, out var changed))
            {
                var found = new List<string>();
                foreach (var asset in assets)
                {
                    var check = await _materializer.CheckTargetAsync(asset, path, hash, cancellationToken).ConfigureAwait(false);
                    if (check.State != ArkiveModTargetState.Exact) found.Add(asset.Target.ObjectName);
                }
                changed = found;
                OutsideChangeChecks[key] = found;
            }
            if (changed.Count > 0)
                changes.Add(new ArkiveModOutsideChange(group.Key, group.First().Target.PackageAlias, path, hash, clean, changed));
        }
        return changes;
    }

    /// <summary>
    /// Finds, in another Arkive data folder's backups, the verified originals of game packages changed outside this
    /// Arkive. Backups are kept under backups\packages\SHA-256-of-the-original\, so each candidate is matched to the
    /// clean version the mods were made for and its bytes are hashed before it is offered.
    /// </summary>
    public async Task<ArkiveModOutsideRestorePlan> PlanRestoreFromOtherFolderAsync(
        string otherDataFolder, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(otherDataFolder);
        var backups = Path.Combine(root, "backups", "packages");
        if (!Directory.Exists(backups) && Path.GetFileName(root).Equals("packages", StringComparison.OrdinalIgnoreCase))
            backups = root;
        var changes = await FindOutsideChangesAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<ArkiveModOutsideRestoreItem>();
        var unresolved = new List<ArkiveModOutsideChange>();
        foreach (var change in changes)
        {
            ArkiveModOutsideRestoreItem? found = null;
            foreach (var clean in change.CleanSha256s)
            {
                var candidate = Path.Combine(backups, clean, change.PhysicalPackageName);
                if (!File.Exists(candidate)) continue;
                var hash = await ArkiveModEditSnapshot.HashFileCachedAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (!hash.Equals(clean, StringComparison.OrdinalIgnoreCase)) continue;
                found = new ArkiveModOutsideRestoreItem(change.PhysicalPackageName, change.PackageAlias, change.GamePath, candidate, clean);
                break;
            }
            if (found is null) unresolved.Add(change);
            else items.Add(found);
        }
        return new ArkiveModOutsideRestorePlan(root, items, unresolved);
    }

    /// <summary>
    /// Puts the planned originals back into the game folder. The game must be closed. Each replaced game file is kept
    /// in this data folder's backups\replaced-outside-arkive folder first, and each original is copied beside the game
    /// file and hashed before it replaces it.
    /// </summary>
    public async Task<ArkiveModOutsideRestoreResult> RestoreFromOtherFolderAsync(
        ArkiveModOutsideRestorePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var blocking = _context.Deployment.FindBlockingProcesses();
        if (blocking.Count > 0)
            Refuse("APL040", $"Close {string.Join(", ", blocking)} before restoring game files.");
        var safety = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_context.BackupRootDirectory))!,
            "replaced-outside-arkive", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var restored = new List<ArkiveModOutsideRestoreItem>();
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var backupHash = await ArkiveModEditSnapshot.HashFileCachedAsync(item.BackupPath, cancellationToken).ConfigureAwait(false);
            if (!backupHash.Equals(item.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                Refuse("APL041", $"The backup of {item.PhysicalPackageName} changed since it was checked.");
            Directory.CreateDirectory(safety);
            File.Copy(item.GamePath, Path.Combine(safety, item.PhysicalPackageName), overwrite: true);
            var temporary = item.GamePath + ".arkive-restore.tmp";
            File.Copy(item.BackupPath, temporary, overwrite: true);
            var copied = await ArkiveModEditSnapshot.HashFileCachedAsync(temporary, cancellationToken).ConfigureAwait(false);
            if (!copied.Equals(item.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporary);
                Refuse("APL042", $"Copying the original of {item.PhysicalPackageName} did not verify; the game file was not changed.");
            }
            File.Move(temporary, item.GamePath, overwrite: true);
            restored.Add(item);
        }
        return new ArkiveModOutsideRestoreResult(restored, safety);
    }

    public static GuidedTexturePackageEntry? FindPackageProject(
        string physicalPackageName,
        string? gameDirectory,
        IEnumerable<GuidedTexturePackageEntry> packages)
    {
        var named = packages.Where(package => Path.GetFileName(package.Manifest.SourcePackagePath)
            .Equals(physicalPackageName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (gameDirectory is null) return named.FirstOrDefault();
        var gamePath = Path.Combine(gameDirectory, physicalPackageName);
        return named.FirstOrDefault(package => ArkiveModEditSnapshot.SamePath(package.Manifest.SourcePackagePath, gamePath)) ??
               named.FirstOrDefault(package => !File.Exists(package.Manifest.SourcePackagePath));
    }

    /// <summary>
    /// A mesh whose check found it identical to the game's own (a piece a body-wide upscale does not reach, such as a
    /// shoulder pad). There is nothing to write for it, so it is left out of builds instead of failing its package.
    /// </summary>
    internal static bool ChangesNothing(ArkiveModLocalEdit edit) =>
        edit.Entry is SkeletalMeshEditWorkspaceEntry mesh &&
        mesh.Manifest.Preflight?.Readiness == SkeletalMeshEditReadiness.ReadyNoChanges;

    public static HashSet<string> RevisionContent(GuidedTexturePackageRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return revision.Assets.Select(asset => ContentKey(asset.ProjectId, asset.EditedImageSha256))
            .Concat(revision.MeshAssets.Select(asset => ContentKey(asset.EditId, asset.EditedGlbSha256)))
            .Concat(revision.StaticMeshAssets.Select(asset => ContentKey(asset.EditId, asset.EditedGlbSha256)))
            .Concat(revision.MaterialAssets.Select(asset => ContentKey(asset.EditId, asset.CandidateExportSha256)))
            .Concat(revision.ParticleAssets.Select(asset => ContentKey(asset.EditId, asset.ContentSha256)))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ContentKey(string editId, string sha256) => $"{editId}:{sha256}".ToUpperInvariant();

    private static GuidedTexturePackageRevision? Latest(GuidedTexturePackageEntry? package) =>
        package is null
            ? null
            : package.Manifest.LatestRevisionId is null
                ? package.Manifest.Revisions.LastOrDefault()
                : package.Manifest.Revisions.FirstOrDefault(revision => revision.RevisionId.Equals(
                    package.Manifest.LatestRevisionId, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ArkiveModPackageContributor> Contributors(
        IEnumerable<(ArkiveModLibraryEntry Mod, ArkiveModFileAsset Asset)> winners,
        IEnumerable<ArkiveModLocalEdit> drafts) =>
        winners.Select(item => new ArkiveModPackageContributor(
                item.Mod.ModId, item.Mod.Manifest.Name, item.Mod.Priority, item.Asset.DisplayName,
                item.Asset.Kind, item.Asset.Target.ObjectName, IsDraft: false))
            .Concat(drafts.Select(draft => new ArkiveModPackageContributor(
                null, "Local draft", 0, draft.DisplayName, draft.Kind, draft.ObjectName, IsDraft: true)))
            .OrderBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool IsOperational(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or
            ArgumentException or InvalidDataException or JsonException or InvalidOperationException;

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new ArkiveModRefusalException(code, message);
}
