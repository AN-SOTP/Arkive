using System.Text.Json;

namespace Arkive.Core.Mods;

/// <summary>A superseded package build a clean-up removes: its folder under _r and its entry in the package project.</summary>
public sealed record ArkiveBuildCleanupItem(
    string PackageProjectId,
    string PackageName,
    string PackageManifestPath,
    string RevisionId,
    string DirectoryPath,
    long Bytes,
    string Reason)
{
    /// <summary>Removed because nothing uses its package; may be the latest (see <see cref="ArkiveSupersededBuildCleanup"/>).</summary>
    public bool Unused { get; init; }
}

/// <summary>A package build the clean-up looked at and kept, and why.</summary>
public sealed record ArkiveBuildCleanupKept(
    string PackageProjectId,
    string PackageName,
    string RevisionId,
    string DirectoryPath,
    string Reason);

/// <summary>
/// The game packages something may still need a build of: those an enabled mod has an asset in, and those a working
/// file selected for a package build targets (by <see cref="ArkiveModFileService.PhysicalPackageKey"/>).
/// <see cref="Unknown"/> when that could not be worked out, so every package counts as used.
/// </summary>
public sealed record ArkivePackageUse(IReadOnlySet<string> PhysicalPackageKeys, bool Unknown)
{
    public static ArkivePackageUse Everything { get; } = new(new HashSet<string>(), Unknown: true);

    public bool Uses(GuidedTexturePackageManifest package) =>
        Unknown || PhysicalPackageKeys.Contains(ArkiveModFileService.PhysicalPackageKey(package.PhysicalPackageName)) ||
        PhysicalPackageKeys.Contains(ArkiveModFileService.PhysicalPackageKey(Path.GetFileName(package.SourcePackagePath)));
}

public sealed record ArkiveBuildCleanupPlan(
    IReadOnlyList<ArkiveBuildCleanupItem> Items,
    IReadOnlyList<ArkiveBuildCleanupKept> Kept)
{
    public long Bytes => Items.Sum(item => item.Bytes);
}

public sealed record ArkiveBuildCleanupResult(
    IReadOnlyList<ArkiveBuildCleanupItem> Removed,
    IReadOnlyList<ArkiveBuildCleanupKept> NotRemoved)
{
    public long Bytes => Removed.Sum(item => item.Bytes);
}

/// <summary>
/// Removes package builds that a newer build of the same package replaced and that nothing can need again.
/// <para>
/// A build other than the latest is removed only when a deployment inspection says its package is not installed (or
/// was restored), it holds no restore data, and its receipt does not say it changed game files; when its bundle,
/// receipt or payload cannot be read, or the inspection fails, it is kept. A build a mod set pins is kept. While a
/// newer build is installed, inspection reports every other build as a conflict; such a build is removed too when the
/// game file is exactly the payload of another build of the package that Arkive installed, and its own receipt, if
/// any, says it was restored or never installed.
/// </para>
/// <para>
/// The package rows and the Mods list describe a package that is not installed by its "clean lineage root": the newest
/// build made from a clean game file that matches the game now. Removing builds can change which build that is, both
/// by removing it and by making a later build look like a root once the build it was made on top of is gone. So the
/// lineage is worked out, by hashes, for the game file as it is now and as Restore would leave it (each kept build's
/// clean original), with every build and with only the builds that would be kept; builds are kept until both give
/// the same answer.
/// </para>
/// <para>
/// With <see cref="PackageUse"/>, the builds of a package nobody can be using go too, the latest with them: no enabled
/// mod has an asset in the package, no working file selected for a build targets it, no mod set
/// the user made names it, and every one of its builds passes the checks above (not in the game, no restore data, no
/// receipt saying it changed game files, not pinned, readable) and is older than <see cref="MinimumAge"/>, unchanged
/// since. Nothing of the package is left to describe the game by, so no lineage root is kept; the package project
/// remains, with no builds, as a new one is. Apply builds the package again if a mod needs it.
/// </para>
/// </summary>
public sealed class ArkiveSupersededBuildCleanup
{
    private const string UnusedPackageReason =
        "no enabled mod, checked working file or mod set uses its package and no build of it is in the game; " +
        "Apply builds it again if a mod needs it";
    private readonly GuidedTexturePackageWorkspace _packages;
    private readonly ArkiveModDeploymentService _deployment;
    private readonly string _backupRoot;
    private readonly string _stateRoot;
    private readonly string[] _modSetRoots;

    /// <param name="dataRoot">Arkive's data folder, whose mod sets may pin builds.</param>
    public ArkiveSupersededBuildCleanup(
        GuidedTexturePackageWorkspace packages, ArkiveModDeploymentService deployment,
        string backupRoot, string stateRoot, string dataRoot)
    {
        _packages = packages;
        _deployment = deployment;
        _backupRoot = Path.GetFullPath(backupRoot);
        _stateRoot = Path.GetFullPath(stateRoot);
        _modSetRoots = [Path.Combine(Path.GetFullPath(dataRoot), "staging", "mod-sets"), Path.Combine(_stateRoot, "sets")];
    }

    /// <summary>When set, packages nothing uses lose every build, the latest included (see the class remarks).</summary>
    public Func<CancellationToken, Task<ArkivePackageUse>>? PackageUse { get; init; }

    /// <summary>A package's builds made or changed this recently are not removed for being unused.</summary>
    public TimeSpan MinimumAge { get; set; } = TimeSpan.FromHours(1);

    /// <param name="fullInspection">
    /// Hash every payload, backup and game file. A plan only shown to the user may reuse known hashes; the removal
    /// itself always plans again with full inspections.
    /// </param>
    /// <param name="onlyPackageProjectIds">When given, only these package projects are looked at.</param>
    public async Task<ArkiveBuildCleanupPlan> PlanAsync(
        bool fullInspection, IReadOnlyCollection<string>? onlyPackageProjectIds = null,
        CancellationToken cancellationToken = default)
    {
        var only = onlyPackageProjectIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Looking at a few packages (after Apply, the ones it rebuilt and so uses) needs no reading of every working file.
        var use = only is null ? await UseAsync(cancellationToken).ConfigureAwait(false) : ArkivePackageUse.Everything;
        var sets = await ReadModSetValuesAsync(cancellationToken).ConfigureAwait(false);
        var entries = (await _packages.LoadAsync(cancellationToken).ConfigureAwait(false))
            .Where(entry => only is null || only.Contains(entry.Manifest.PackageProjectId))
            .Where(entry => entry.Manifest.Revisions.Count > 1 || entry.Manifest.Revisions.Count == 1 && Unused(entry, use, sets))
            .ToArray();
        var plans = new (IReadOnlyList<ArkiveBuildCleanupItem> Items, IReadOnlyList<ArkiveBuildCleanupKept> Kept)[entries.Length];
        // Each package's inspections read its builds, backup and game file; a few packages at a time.
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (index, token) => plans[index] = await PlanPackageAsync(
                entries[index], fullInspection, sets.All, Unused(entries[index], use, sets), token).ConfigureAwait(false))
            .ConfigureAwait(false);
        return new ArkiveBuildCleanupPlan(
            plans.SelectMany(plan => plan.Items).ToArray(), plans.SelectMany(plan => plan.Kept).ToArray());
    }

    /// <summary>
    /// Removes the planned builds that a plan made now, with full inspections, still finds removable, one package at a
    /// time; each package's builds are inspected again just before they are removed.
    /// </summary>
    public async Task<ArkiveBuildCleanupResult> DeleteAsync(
        ArkiveBuildCleanupPlan plan, CancellationToken cancellationToken = default)
    {
        var removed = new List<ArkiveBuildCleanupItem>();
        var notRemoved = new List<ArkiveBuildCleanupKept>();
        var use = plan.Items.Any(item => item.Unused)
            ? await UseAsync(cancellationToken).ConfigureAwait(false)
            : ArkivePackageUse.Everything;
        var sets = await ReadModSetValuesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var package in plan.Items.GroupBy(item => item.PackageManifestPath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            GuidedTexturePackageEntry entry;
            try
            {
                entry = await _packages.LoadManifestAsync(package.Key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                notRemoved.AddRange(package.Select(item => Kept(item, "its package project cannot be read: " + exception.Message)));
                continue;
            }
            var now = await PlanPackageAsync(entry, fullInspection: true, sets.All, Unused(entry, use, sets), cancellationToken)
                .ConfigureAwait(false);
            var confirmed = now.Items.ToDictionary(item => item.RevisionId, StringComparer.OrdinalIgnoreCase);
            var keptNow = now.Kept.ToDictionary(item => item.RevisionId, StringComparer.OrdinalIgnoreCase);
            var ids = package.Where(item => confirmed.ContainsKey(item.RevisionId))
                .Select(item => item.RevisionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // The latest goes only with every other build, and only when this check still finds the package unused.
            var everyBuild = entry.Manifest.Revisions.All(revision => ids.Contains(revision.RevisionId)) &&
                             ids.All(id => confirmed[id].Unused);
            if (!everyBuild && entry.Manifest.LatestRevisionId is { } latest && ids.Remove(latest))
                notRemoved.Add(Kept(package.First(item => item.RevisionId.Equals(latest, StringComparison.OrdinalIgnoreCase)),
                    "the latest build, and some other build of its package was kept"));
            foreach (var item in package.Where(item => !ids.Contains(item.RevisionId) &&
                                                       notRemoved.All(left => left.DirectoryPath != item.DirectoryPath)))
                notRemoved.Add(Kept(item, keptNow.TryGetValue(item.RevisionId, out var kept) ? kept.Reason : "no longer in its package project"));
            if (ids.Count == 0) continue;
            var gone = (await _packages.RemoveRevisionsAsync(entry.ManifestPath, ids, cancellationToken, everyBuild)
                    .ConfigureAwait(false))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in package.Where(item => ids.Contains(item.RevisionId)))
            {
                if (gone.Contains(item.RevisionId)) removed.Add(confirmed[item.RevisionId]);
                else notRemoved.Add(Kept(item, "its folder could not be removed"));
            }
        }
        return new ArkiveBuildCleanupResult(removed, notRemoved);

        static ArkiveBuildCleanupKept Kept(ArkiveBuildCleanupItem item, string reason) =>
            new(item.PackageProjectId, item.PackageName, item.RevisionId, item.DirectoryPath, reason);
    }

    private sealed record BuildState(
        GuidedTexturePackageRevision Revision,
        string Directory,
        ArkiveModBundleManifest? Bundle,
        ModDeploymentInspection? Inspection,
        string? Problem);

    private async Task<(IReadOnlyList<ArkiveBuildCleanupItem> Items, IReadOnlyList<ArkiveBuildCleanupKept> Kept)> PlanPackageAsync(
        GuidedTexturePackageEntry entry, bool fullInspection, IReadOnlySet<string> pins, bool unused,
        CancellationToken cancellationToken)
    {
        var manifest = entry.Manifest;
        var name = string.IsNullOrWhiteSpace(manifest.DisplayName) ? manifest.PackageAlias : manifest.DisplayName.Trim();
        // The latest as the record names it, and the last and newest build when it names none.
        var latestIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(manifest.LatestRevisionId)) latestIds.Add(manifest.LatestRevisionId);
        else if (manifest.Revisions.Count > 0)
        {
            latestIds.Add(manifest.Revisions[^1].RevisionId);
            latestIds.Add(manifest.Revisions.MaxBy(revision => revision.CreatedAt)!.RevisionId);
        }

        var states = new List<BuildState>();
        foreach (var revision in manifest.Revisions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetFullPath(Path.Combine(_packages.RootDirectory, "_r",
                manifest.PackageProjectId[..Math.Min(8, manifest.PackageProjectId.Length)], revision.RevisionId));
            ArkiveModBundleManifest? bundle = null;
            ModDeploymentInspection? inspection = null;
            string? problem = null;
            try
            {
                if (File.Exists(revision.ModBundleManifestPath))
                    bundle = await ArkiveModBundleService.LoadBundleAsync(revision.ModBundleManifestPath, cancellationToken)
                        .ConfigureAwait(false);
                inspection = await _deployment.InspectForRecoveryAsync(
                    new ArkiveModDeploymentRequest(revision.ModBundleManifestPath, _backupRoot, _stateRoot)
                    {
                        ReuseKnownHashes = !fullInspection,
                    }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                problem = exception.Message;
            }
            states.Add(new BuildState(revision, directory, bundle, inspection, problem));
        }

        // Builds whose payload the game holds now, installed by Arkive with a receipt (normally the latest).
        var installed = states.Where(state => state.Inspection is { HasActiveDeployment: true } && state.Bundle is not null)
            .GroupBy(state => state.Bundle!.PayloadSha256, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Revision.RevisionId, StringComparer.OrdinalIgnoreCase);
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var replacedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // A package nobody uses loses every build, the latest too, when each passes the checks below for itself.
        if (unused && states.Count > 0 && states.All(state =>
                ReplacedInGameBy(state) is null && KeepReason(state, null) is null && Settled(state)))
        {
            return (states.Select(state => new ArkiveBuildCleanupItem(manifest.PackageProjectId, name, entry.ManifestPath,
                    state.Revision.RevisionId, state.Directory, Size(state.Directory, state.Revision),
                    $"{(latestIds.Contains(state.Revision.RevisionId) ? "the latest build" : "superseded")}; {UnusedPackageReason}")
                {
                    Unused = true,
                }).ToArray(), []);
        }
        foreach (var state in states)
        {
            var revision = state.Revision;
            var holder = ReplacedInGameBy(state);
            var reason = latestIds.Contains(revision.RevisionId) ? "the latest build" : KeepReason(state, holder);
            if (reason is not null) kept[revision.RevisionId] = reason;
            else if (holder is not null) replacedBy[revision.RevisionId] = holder;
        }

        // Why a build other than the latest must stay, or null.
        string? KeepReason(BuildState state, string? holder)
        {
            var revision = state.Revision;
            return !Directory.Exists(state.Directory) ? "its folder is missing"
                : !IsWithin(state.Directory, revision.ModBundleManifestPath) ? "it is not laid out as Arkive lays out builds"
                : state.Bundle is null ? "its bundle cannot be read" + (state.Problem is null ? string.Empty : ": " + state.Problem)
                : state.Inspection is not { } inspection ? "its install state could not be checked: " + state.Problem
                : inspection.State is not (ModInstallationState.NotInstalled or ModInstallationState.Restored) && holder is null
                    ? $"its install state is {inspection.State}"
                : inspection.CanRestore ? "it holds restore data"
                : inspection.Receipt is { GameFilesModified: true } ? "its receipt says it changed game files"
                : pins.Contains(revision.RevisionId) || pins.Any(value => IsWithin(state.Directory, value) ||
                                                                          value.Equals(state.Directory, StringComparison.OrdinalIgnoreCase))
                    ? "a mod set uses it"
                : null;
        }

        // Made, and left unchanged, longer ago than the minimum age.
        bool Settled(BuildState state)
        {
            if (DateTimeOffset.UtcNow - state.Revision.CreatedAt <= MinimumAge) return false;
            var info = new DirectoryInfo(state.Directory);
            var newest = info.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                .Select(item => item.LastWriteTimeUtc).Append(info.LastWriteTimeUtc).Max();
            return DateTime.UtcNow - newest > MinimumAge;
        }

        // Inspection calls every build but the installed one a conflict, since the game file is neither its clean
        // original nor its payload. Such a build is not in the game and can restore nothing. It may go when the game
        // holds exactly the payload of another build of this package that Arkive installed (which is kept), and its own
        // receipt, if any, says it was restored or never installed.
        string? ReplacedInGameBy(BuildState state) =>
            state.Inspection is { State: ModInstallationState.Conflict, CurrentTargetSha256: { } target } conflict &&
            installed.TryGetValue(target, out var holder) &&
            !holder.Equals(state.Revision.RevisionId, StringComparison.OrdinalIgnoreCase) &&
            !target.Equals(state.Bundle?.PayloadSha256, StringComparison.OrdinalIgnoreCase) &&
            conflict.Receipt is null or { State: ModInstallationState.Restored or ModInstallationState.NotInstalled }
                ? holder
                : null;

        // The game file now, and as Restore would leave it: each kept build's clean original.
        var lineage = states.Where(state => state.Bundle is not null)
            .OrderByDescending(state => state.Revision.CreatedAt)
            .Select(state => new LineageBuild(state.Revision.RevisionId, state.Bundle!.RequiredSourcePackageSha256,
                state.Bundle.PayloadSha256))
            .ToArray();
        var failed = states.Where(state => state.Inspection is null).Select(state => state.Revision.RevisionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gameStates = states.Select(state => state.Inspection?.CurrentTargetSha256)
            .Concat(states.Where(state => kept.ContainsKey(state.Revision.RevisionId))
                .Select(state => state.Bundle?.RequiredSourcePackageSha256))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var root in LineageRootsToKeep(lineage, failed, kept.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), gameStates))
            kept.TryAdd(root, "Arkive may describe the game by it when the package is not installed (clean lineage root)");

        var items = new List<ArkiveBuildCleanupItem>();
        var keptItems = new List<ArkiveBuildCleanupKept>();
        foreach (var state in states)
        {
            if (kept.TryGetValue(state.Revision.RevisionId, out var reason))
            {
                keptItems.Add(new ArkiveBuildCleanupKept(manifest.PackageProjectId, name, state.Revision.RevisionId,
                    state.Directory, reason));
                continue;
            }
            items.Add(new ArkiveBuildCleanupItem(manifest.PackageProjectId, name, entry.ManifestPath,
                state.Revision.RevisionId, state.Directory, Size(state.Directory, state.Revision),
                replacedBy.TryGetValue(state.Revision.RevisionId, out var holder)
                    ? $"superseded; the game holds build {holder}, which Arkive installed, and this one holds no restore data"
                    : $"superseded by a newer build; {state.Inspection!.State} and holds no restore data"));
        }
        return (items, keptItems);
    }

    /// <summary>One build in a package's lineage: the clean file it needs and the file it installs, by SHA-256.</summary>
    public sealed record LineageBuild(string RevisionId, string RequiredSourceSha256, string PayloadSha256);

    /// <summary>
    /// The builds, among those not already kept, that must stay so that the clean lineage root Arkive picks for each
    /// game state is the same as with every build. <paramref name="lineage"/> is newest first.
    /// </summary>
    public static IReadOnlyList<string> LineageRootsToKeep(
        IReadOnlyList<LineageBuild> lineage, IReadOnlySet<string> failed, IReadOnlySet<string> kept,
        IReadOnlyCollection<string> gameStates)
    {
        var keep = new HashSet<string>(kept, StringComparer.OrdinalIgnoreCase);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var state in gameStates)
            {
                var everything = Root(lineage, state);
                var left = Root(lineage.Where(build => keep.Contains(build.RevisionId)).ToArray(), state);
                if (everything?.RevisionId == left?.RevisionId) continue;
                if (everything is not null && keep.Add(everything.RevisionId))
                {
                    changed = true;
                    continue;
                }
                // A kept build looks like a root only because the build it was made on top of would go.
                var parents = left is null ? [] : lineage.Where(build =>
                    build.PayloadSha256.Equals(left.RequiredSourceSha256, StringComparison.OrdinalIgnoreCase) &&
                    keep.Add(build.RevisionId)).ToArray();
                if (parents.Length > 0)
                {
                    changed = true;
                    continue;
                }
                return lineage.Select(build => build.RevisionId).Where(id => !kept.Contains(id)).ToArray();
            }
        }
        return keep.Where(id => !kept.Contains(id)).ToArray();

        LineageBuild? Root(IReadOnlyList<LineageBuild> builds, string state)
        {
            var payloads = builds.Select(build => build.PayloadSha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return builds.FirstOrDefault(build => !payloads.Contains(build.RequiredSourceSha256) &&
                                                  !failed.Contains(build.RevisionId) &&
                                                  build.RequiredSourceSha256.Equals(state, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task<ArkivePackageUse> UseAsync(CancellationToken cancellationToken) =>
        PackageUse is null ? ArkivePackageUse.Everything : await PackageUse(cancellationToken).ConfigureAwait(false);

    /// <summary>Nothing uses the package, and no mod set the user made names its project.</summary>
    private static bool Unused(GuidedTexturePackageEntry entry, ArkivePackageUse use, ModSetValues sets) =>
        !use.Uses(entry.Manifest) && !sets.Named.Contains(entry.Manifest.PackageProjectId) &&
        !sets.Named.Any(value => SamePath(value, entry.ManifestPath) || SamePath(value, entry.DirectoryPath));

    /// <param name="All">Every string in the mod sets' records and receipts; a build any of them names is kept.</param>
    /// <param name="Named">
    /// The strings in the sets the user made and their receipts, without Arkive's own quick-action sets (Apply, Restore
    /// all), which are written afresh from the packages at hand each time they are used.
    /// </param>
    private sealed record ModSetValues(IReadOnlySet<string> All, IReadOnlySet<string> Named);

    private async Task<ModSetValues> ReadModSetValuesAsync(CancellationToken cancellationToken)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _modSetRoots.Where(Directory.Exists))
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            var quickAction = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar) is [var first, _, ..] &&
                              (first.Equals("_quick-actions", StringComparison.OrdinalIgnoreCase) ||
                               first.StartsWith("utility-", StringComparison.OrdinalIgnoreCase));
            var found = new List<string>();
            try
            {
                var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true });
                while (reader.Read())
                    if (reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } value)
                        found.Add(value);
            }
            catch (JsonException)
            {
                // A damaged mod set could pin any build: every token in it counts.
                foreach (var token in System.Text.Encoding.UTF8.GetString(bytes).Replace(@"\\", @"\")
                             .Split('"', ',', '\r', '\n', ' ', '\t'))
                    if (token.Length > 0) found.Add(token);
            }
            values.UnionWith(found);
            if (!quickAction) named.UnionWith(found);
        }
        return new ModSetValues(values, named);
    }

    private static bool SamePath(string value, string path)
    {
        if (value.IndexOfAny(['\\', '/']) < 0) return false;
        try
        {
            return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsWithin(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return Path.GetFullPath(path).StartsWith(
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The bytes removing a build frees. A trimmed build's staged package is a hard link to its payload (see
    /// <see cref="PackageRevisionTrimmer"/>), so the two are one file on disk and counted once.
    /// </summary>
    private static long Size(string directory, GuidedTexturePackageRevision revision)
    {
        if (!Directory.Exists(directory)) return 0;
        var bytes = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        try
        {
            var output = new FileInfo(revision.OutputUpkPath);
            if (output.Exists && File.Exists(revision.PayloadPath) && IsWithin(directory, output.FullName) &&
                IsWithin(directory, revision.PayloadPath) && SameFile(output.FullName, revision.PayloadPath))
                bytes -= output.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
        return bytes;
    }

    private static bool SameFile(string left, string right)
    {
        if (!OperatingSystem.IsWindows() || Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase))
            return false;
        using var first = File.OpenHandle(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var second = File.OpenHandle(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(first, out var a) && GetFileInformationByHandle(second, out var b) &&
               a.VolumeSerialNumber == b.VolumeSerialNumber && a.FileIndexHigh == b.FileIndexHigh && a.FileIndexLow == b.FileIndexLow;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInformation information);
}
