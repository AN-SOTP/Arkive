using System.Globalization;
using System.Text;
using Arkive.Core.Meshes;

namespace Arkive.Core.Mods;

public sealed record ArkiveStorageCleanupCategory(string Name, int Count, long Bytes);

/// <summary>A build whose step files the trimmer's second pass would delete (see <see cref="PackageRevisionTrimmer"/>).</summary>
public sealed record ArkiveBuildTrimItem(
    string PackageProjectId,
    string PackageName,
    string PackageManifestPath,
    string RevisionId,
    string DirectoryPath,
    int Files,
    long Bytes);

/// <summary>Everything one clean-up would remove: package builds first, then build files, working files and caches.</summary>
public sealed record ArkiveStorageCleanupPlan(
    ArkiveBuildCleanupPlan Builds,
    ArkiveWorkingFileCleanupPlan WorkingFiles,
    TimeSpan MinimumAge)
{
    /// <summary>Kept builds whose step files nothing reads any more.</summary>
    public IReadOnlyList<ArkiveBuildTrimItem> Trims { get; init; } = [];

    public ArkiveCacheCleanupPlan Caches { get; init; } = new([], []);

    public long Bytes => Builds.Bytes + Trims.Sum(item => item.Bytes) + WorkingFiles.Bytes + Caches.Bytes;

    public int Count => Builds.Items.Count + Trims.Count + WorkingFiles.Items.Count + Caches.Items.Count;

    public IReadOnlyList<ArkiveStorageCleanupCategory> Categories =>
        Builds.Items.GroupBy(item => item.Unused).OrderBy(group => group.Key)
            .Select(group => new ArkiveStorageCleanupCategory(
                group.Key ? ArkiveStorageCleanup.UnusedBuildsCategory : ArkiveStorageCleanup.BuildsCategory,
                group.Count(), group.Sum(item => item.Bytes)))
            .Concat(Trims.Count == 0
                ? []
                : [new ArkiveStorageCleanupCategory(ArkiveStorageCleanup.TrimCategory, Trims.Count, Trims.Sum(item => item.Bytes))])
            .Concat(WorkingFiles.Items.GroupBy(item => item.Kind).OrderBy(group => group.Key)
                .Select(group => new ArkiveStorageCleanupCategory(
                    ArkiveStorageCleanup.CategoryName(group.Key), group.Count(), group.Sum(item => item.Bytes))))
            .Concat(Caches.Items.Count == 0
                ? []
                : [new ArkiveStorageCleanupCategory(ArkiveStorageCleanup.CacheCategory, Caches.Items.Count, Caches.Bytes)])
            .ToArray();
}

/// <param name="Trimmed">The builds trimmed, each with the bytes its trim freed.</param>
public sealed record ArkiveStorageCleanupResult(ArkiveBuildCleanupResult Builds, ArkiveWorkingFileRemoval WorkingFiles)
{
    public IReadOnlyList<ArkiveBuildTrimItem> Trimmed { get; init; } = [];

    public IReadOnlyList<ArkiveCacheItem> Caches { get; init; } = [];

    public long Bytes => Builds.Bytes + Trimmed.Sum(item => item.Bytes) + WorkingFiles.Bytes + Caches.Sum(item => item.Bytes);

    public int Count => Builds.Removed.Count + Trimmed.Count + WorkingFiles.Removed.Count + Caches.Count;
}

/// <summary>
/// One clean-up of a data folder, shared by the window and the command line, in this order: package builds nothing can
/// need (<see cref="ArkiveSupersededBuildCleanup"/>; with the mod library, also every build of a package nothing uses),
/// the step files kept builds no longer read (<see cref="PackageRevisionTrimmer"/>), working files and leftovers nothing
/// uses (<see cref="ArkiveWorkingFileCleanup"/>), and cache entries unused for 30 days (<see cref="ArkiveCacheCleanup"/>).
/// Builds go first because a build's records keep the working files it was made from. Nothing is kept aside to be put
/// back, so every clean-up first writes what it will remove, with sizes and reasons, to reports\clean-up-*.txt.
/// </summary>
public sealed class ArkiveStorageCleanup
{
    public const string BuildsCategory = "Superseded package builds";
    public const string UnusedBuildsCategory = "Builds of packages nothing uses";
    public const string TrimCategory = "Build files no longer needed";
    public const string CacheCategory = "Cache entries unused for 30 days";
    private readonly string _dataRoot;
    private readonly GuidedTexturePackageWorkspace _packages;
    private readonly ArkiveModEditWorkspaces _edits;
    private readonly ArkiveModLibrary? _library;

    /// <param name="library">
    /// The mod library. With it, builds of packages no enabled mod, checked working file or mod set uses are
    /// removed too, the latest included; without it every package keeps its latest build.
    /// </param>
    public ArkiveStorageCleanup(
        string dataRoot,
        ArkiveModEditWorkspaces edits,
        SkeletalMeshEditableWorkspaceStore meshExports,
        GuidedTexturePackageWorkspace packages,
        ArkiveModDeploymentService deployment,
        ArkiveModLibrary? library = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _packages = packages;
        _edits = edits;
        _library = library;
        Builds = new ArkiveSupersededBuildCleanup(packages, deployment,
            Path.Combine(_dataRoot, "backups", "packages"), Path.Combine(_dataRoot, "backups", "installations"), _dataRoot)
        {
            PackageUse = library is null ? null : PackageUseAsync,
        };
        WorkingFiles = new ArkiveWorkingFileCleanup(_dataRoot, edits, meshExports);
        Caches = new ArkiveCacheCleanup(_dataRoot);
    }

    public ArkiveSupersededBuildCleanup Builds { get; }

    public ArkiveWorkingFileCleanup WorkingFiles { get; }

    public ArkiveCacheCleanup Caches { get; }

    public string ReportsDirectory => Path.Combine(_dataRoot, "reports");

    /// <summary>
    /// The first step of a clean-up: drops the spare package copies finished builds keep (the trimmer's first pass, see
    /// <see cref="PackageRevisionTrimmer"/>). The second pass is part of the plan, so it is listed first.
    /// </summary>
    public Task<long> TrimBuildsAsync(CancellationToken cancellationToken = default) =>
        _packages.TrimRevisionsAsync(unneededFiles: false, cancellationToken);

    public static string CategoryName(ArkiveWorkingFileKind kind) => kind switch
    {
        ArkiveWorkingFileKind.EditedMesh => "Edited meshes",
        ArkiveWorkingFileKind.MeshExport => "Game mesh exports",
        ArkiveWorkingFileKind.TextureProject => "Texture projects",
        ArkiveWorkingFileKind.EffectEdit => "Effect edits",
        ArkiveWorkingFileKind.MaterialEdit => "Material edits",
        ArkiveWorkingFileKind.PhysicsEdit => "Physics edits",
        ArkiveWorkingFileKind.InterfaceMovieEdit => "Interface movie edits",
        ArkiveWorkingFileKind.StaticMeshEdit => "Static mesh edits",
        ArkiveWorkingFileKind.UnpackedModFiles => "Unpacked mod files",
        ArkiveWorkingFileKind.MaterialCandidate => "Material candidates",
        _ => kind.ToString(),
    };

    /// <summary>
    /// What a clean-up would remove now. Build files and working files are planned as if the planned builds were already
    /// gone, as they will be when the clean-up runs.
    /// </summary>
    public async Task<ArkiveStorageCleanupPlan> PlanAsync(
        TimeSpan minimumAge, bool fullInspection = false, CancellationToken cancellationToken = default)
    {
        Builds.MinimumAge = minimumAge;
        var builds = await Builds.PlanAsync(fullInspection, cancellationToken: cancellationToken).ConfigureAwait(false);
        var trims = await PlanTrimsAsync(builds, minimumAge, cancellationToken).ConfigureAwait(false);
        var files = await WorkingFiles.PlanAsync(minimumAge, removedBuilds: builds.Items, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var caches = Caches.Plan(cancellationToken);
        return new ArkiveStorageCleanupPlan(builds, files, minimumAge) { Trims = trims, Caches = caches };
    }

    /// <summary>
    /// Removes the planned builds, trims the planned builds' files, then removes the planned working files and cache
    /// entries; each is checked again just before.
    /// </summary>
    public async Task<ArkiveStorageCleanupResult> RunAsync(
        ArkiveStorageCleanupPlan plan, CancellationToken cancellationToken = default)
    {
        Builds.MinimumAge = plan.MinimumAge;
        var builds = await Builds.DeleteAsync(plan.Builds, cancellationToken).ConfigureAwait(false);
        var trimmed = new List<ArkiveBuildTrimItem>();
        foreach (var item in plan.Trims)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GuidedTexturePackageEntry entry;
            try
            {
                entry = await _packages.LoadManifestAsync(item.PackageManifestPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  System.Text.Json.JsonException or NotSupportedException)
            {
                continue;
            }
            // Still a build of its package; the trimmer checks the build again before it deletes anything.
            if (entry.Manifest.Revisions.FirstOrDefault(revision =>
                    revision.RevisionId.Equals(item.RevisionId, StringComparison.OrdinalIgnoreCase)) is not { } revision) continue;
            var freed = await _packages.TrimRevisionAsync(entry, revision, unneededFiles: true, cancellationToken)
                .ConfigureAwait(false);
            if (freed > 0) trimmed.Add(item with { Bytes = freed });
        }
        var files = await WorkingFiles.RemoveAsync(plan.WorkingFiles, plan.MinimumAge, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var caches = Caches.Remove(plan.Caches, cancellationToken);
        return new ArkiveStorageCleanupResult(builds, files) { Trimmed = trimmed, Caches = caches };
    }

    /// <summary>The kept builds with step files to trim; a build changed within <paramref name="minimumAge"/> is left alone.</summary>
    private async Task<IReadOnlyList<ArkiveBuildTrimItem>> PlanTrimsAsync(
        ArkiveBuildCleanupPlan builds, TimeSpan minimumAge, CancellationToken cancellationToken)
    {
        var removed = builds.Items.Select(item => item.RevisionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = new List<ArkiveBuildTrimItem>();
        foreach (var entry in await _packages.LoadAsync(cancellationToken).ConfigureAwait(false))
        foreach (var revision in entry.Manifest.Revisions.Where(revision => !removed.Contains(revision.RevisionId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FileInfo> files;
            string directory;
            try
            {
                directory = _packages.RevisionDirectoryOf(entry, revision);
                files = PackageRevisionTrimmer.PlanUnneededFiles(directory, revision);
                if (files.Count > 0)
                {
                    var info = new DirectoryInfo(directory);
                    var newest = info.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                        .Select(item => item.LastWriteTimeUtc).Append(info.LastWriteTimeUtc).Max();
                    if (DateTime.UtcNow - newest <= minimumAge) continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (files.Count == 0) continue;
            var name = string.IsNullOrWhiteSpace(entry.Manifest.DisplayName) ? entry.Manifest.PackageAlias : entry.Manifest.DisplayName.Trim();
            items.Add(new ArkiveBuildTrimItem(entry.Manifest.PackageProjectId, name, entry.ManifestPath, revision.RevisionId,
                directory, files.Count, files.Sum(file => file.Length)));
        }
        return items;
    }

    /// <summary>
    /// The packages an enabled mod has an asset in, or a working file selected for a build targets. When the library or
    /// an edited mesh cannot be read, every package counts as used.
    /// </summary>
    private async Task<ArkivePackageUse> PackageUseAsync(CancellationToken cancellationToken)
    {
        if (_library is null) return ArkivePackageUse.Everything;
        try
        {
            var library = await _library.LoadAsync(cancellationToken).ConfigureAwait(false);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mod in library.Mods.Where(mod => mod.Enabled))
            foreach (var asset in mod.Manifest.Assets)
                keys.Add(ArkiveModFileService.PhysicalPackageKey(asset.Target.PhysicalPackageName));
            if ((await _edits.SkeletalMeshEdits.LoadAsync(cancellationToken).ConfigureAwait(false)).Issues.Count > 0)
                return ArkivePackageUse.Everything;
            // Apply also builds the checked working files that belong to no mod. A draft's working files are kept by the
            // working-file rules; a draft needs no build until its mod is turned on, and then Apply makes one.
            var snapshot = await ArkiveModEditSnapshot.LoadAsync(_edits, cancellationToken).ConfigureAwait(false);
            foreach (var edit in snapshot.Edits.Where(edit => edit.Included))
                keys.Add(ArkiveModFileService.PhysicalPackageKey(Path.GetFileName(edit.SourcePackagePath)));
            return new ArkivePackageUse(keys, Unknown: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.Text.Json.JsonException or InvalidDataException or
                                              NotSupportedException or ArkiveModRefusalException)
        {
            return ArkivePackageUse.Everything;
        }
    }

    /// <summary>
    /// After a successful Apply, removes the superseded builds of the packages it just rebuilt, writing the report
    /// first. Returns null when there was nothing to remove.
    /// </summary>
    public async Task<(ArkiveBuildCleanupResult Result, string ReportPath)?> AfterApplyAsync(
        ArkiveModApplyResult applied, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applied);
        if (!applied.Succeeded) return null;
        var revisions = applied.Packages
            .Where(package => package is { Action: ArkiveModPackageAction.Rebuild, Succeeded: true, RevisionId: not null })
            .Select(package => package.RevisionId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (revisions.Count == 0) return null;
        var packages = (await _packages.LoadAsync(cancellationToken).ConfigureAwait(false))
            .Where(entry => entry.Manifest.Revisions.Any(revision => revisions.Contains(revision.RevisionId)))
            .Select(entry => entry.Manifest.PackageProjectId).ToArray();
        if (packages.Length == 0) return null;
        var plan = await Builds.PlanAsync(fullInspection: false, packages, cancellationToken).ConfigureAwait(false);
        if (plan.Items.Count == 0) return null;
        var whole = new ArkiveStorageCleanupPlan(plan, new ArkiveWorkingFileCleanupPlan([]), TimeSpan.Zero);
        var report = WriteReport(whole, "after Apply, for the packages it rebuilt", dryRun: false, includeKept: true);
        var result = await Builds.DeleteAsync(plan, cancellationToken).ConfigureAwait(false);
        AppendResult(report, new ArkiveStorageCleanupResult(result, new ArkiveWorkingFileRemoval([], [])));
        return (result, report);
    }

    /// <summary>
    /// Writes the plan to <paramref name="path"/>, or to a new reports\clean-up-yyyyMMdd-HHmmss.txt, and returns the
    /// path: every item to be removed with its size and reason, then every candidate kept and why, then the caches'
    /// sizes.
    /// </summary>
    public string WriteReport(
        ArkiveStorageCleanupPlan plan, string trigger, bool dryRun, string? path = null, bool includeKept = true)
    {
        path = path is null ? NewReportPath() : Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = new StringBuilder();
        text.AppendLine("Arkive clean-up " + (dryRun ? "plan (dry run: nothing is removed)" : "plan"));
        text.AppendLine($"Written:       {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"Data folder:   {_dataRoot}");
        text.AppendLine($"Started by:    {trigger}");
        text.AppendLine($"Minimum age:   {plan.MinimumAge.TotalHours.ToString("0.##", CultureInfo.InvariantCulture)} hour(s)");
        text.AppendLine($"To remove:     {plan.Count:N0} item(s), {FormatBytes(plan.Bytes)} ({plan.Bytes:N0} bytes)");
        foreach (var category in plan.Categories)
            text.AppendLine($"  {category.Name}: {category.Count:N0}, {FormatBytes(category.Bytes)} ({category.Bytes:N0} bytes)");
        text.AppendLine();
        text.AppendLine("Will remove (category | bytes | folder | reason):");
        foreach (var build in plan.Builds.Items)
            text.AppendLine($"  {(build.Unused ? UnusedBuildsCategory : BuildsCategory)} | {build.Bytes} | {build.DirectoryPath} | {build.PackageName}, build {build.RevisionId}: {build.Reason}");
        foreach (var trim in plan.Trims)
            text.AppendLine($"  {TrimCategory} | {trim.Bytes} | {trim.DirectoryPath} | {trim.PackageName}, build {trim.RevisionId}: " +
                            $"{trim.Files:N0} file(s) (mesh, buffer and image copies of its steps) that nothing reads once a build is verified");
        foreach (var item in plan.WorkingFiles.Items)
        {
            text.AppendLine($"  {CategoryName(item.Kind)} | {item.Bytes} | {item.DirectoryPath} | {item.Description}: {item.Reason}");
            foreach (var owned in item.OwnedDirectories) text.AppendLine($"      with {owned}");
        }
        foreach (var item in plan.Caches.Items)
            text.AppendLine($"  {CacheCategory} | {item.Bytes} | {item.Path} | {item.Cache}: last used {item.LastUsedUtc.ToLocalTime():yyyy-MM-dd}; Arkive makes it again when it is next needed");
        if (includeKept)
        {
            text.AppendLine();
            text.AppendLine("Kept (category | folder | reason):");
            foreach (var build in plan.Builds.Kept)
                text.AppendLine($"  {BuildsCategory} | {build.DirectoryPath} | {build.PackageName}, build {build.RevisionId}: {build.Reason}");
            foreach (var item in plan.WorkingFiles.Kept)
                text.AppendLine($"  {CategoryName(item.Kind)} | {item.DirectoryPath} | {item.Description}: {item.Reason}");
        }
        if (plan.Caches.Sizes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Caches (entries and size now | unused for 30 days):");
            foreach (var size in plan.Caches.Sizes)
                text.AppendLine($"  {size.Cache}: {size.Entries:N0}, {FormatBytes(size.Bytes)} | {size.OldEntries:N0}, {FormatBytes(size.OldBytes)}");
        }
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Adds what was actually removed, and what was left after its last check, to a written report.</summary>
    public static void AppendResult(string reportPath, ArkiveStorageCleanupResult result)
    {
        var text = new StringBuilder();
        text.AppendLine();
        text.AppendLine($"Result ({DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}): removed {result.Count:N0} item(s), {FormatBytes(result.Bytes)} ({result.Bytes:N0} bytes)");
        if (result.Trimmed.Count > 0)
            text.AppendLine($"  {TrimCategory}: {result.Trimmed.Count:N0} build(s), {FormatBytes(result.Trimmed.Sum(item => item.Bytes))}");
        if (result.Caches.Count > 0)
            text.AppendLine($"  {CacheCategory}: {result.Caches.Count:N0}, {FormatBytes(result.Caches.Sum(item => item.Bytes))}");
        var left = result.Builds.NotRemoved.Select(item => $"  {BuildsCategory} | {item.DirectoryPath} | {item.Reason}")
            .Concat(result.WorkingFiles.NotRemoved.Select(item =>
                $"  {CategoryName(item.Kind)} | {item.DirectoryPath} | {item.Reason}"))
            .ToArray();
        if (left.Length > 0)
        {
            text.AppendLine("Planned but left in place after the last check:");
            foreach (var line in left) text.AppendLine(line);
        }
        File.AppendAllText(reportPath, text.ToString(), new UTF8Encoding(false));
    }

    private string NewReportPath()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(ReportsDirectory, $"clean-up-{stamp}.txt");
        for (var attempt = 2; File.Exists(path); attempt++)
            path = Path.Combine(ReportsDirectory, $"clean-up-{stamp}-{attempt}.txt");
        return path;
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d:N2} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024d:N1} MB",
        >= 1024L => $"{bytes / 1024d:N0} KB",
        _ => $"{bytes:N0} bytes",
    };
}
