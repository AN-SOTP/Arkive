using System.Globalization;
using System.Text;
using Arkive.Core.Meshes;

namespace Arkive.Core.Mods;

public sealed record ArkiveStorageCleanupCategory(string Name, int Count, long Bytes);

/// <summary>Everything one clean-up would remove: superseded package builds first, then unused working files.</summary>
public sealed record ArkiveStorageCleanupPlan(
    ArkiveBuildCleanupPlan Builds,
    ArkiveWorkingFileCleanupPlan WorkingFiles,
    TimeSpan MinimumAge)
{
    public long Bytes => Builds.Bytes + WorkingFiles.Bytes;

    public int Count => Builds.Items.Count + WorkingFiles.Items.Count;

    public IReadOnlyList<ArkiveStorageCleanupCategory> Categories =>
        (Builds.Items.Count == 0
            ? Enumerable.Empty<ArkiveStorageCleanupCategory>()
            : [new ArkiveStorageCleanupCategory(ArkiveStorageCleanup.BuildsCategory, Builds.Items.Count, Builds.Bytes)])
        .Concat(WorkingFiles.Items.GroupBy(item => item.Kind).OrderBy(group => group.Key)
            .Select(group => new ArkiveStorageCleanupCategory(
                ArkiveStorageCleanup.CategoryName(group.Key), group.Count(), group.Sum(item => item.Bytes))))
        .ToArray();
}

public sealed record ArkiveStorageCleanupResult(ArkiveBuildCleanupResult Builds, ArkiveWorkingFileRemoval WorkingFiles)
{
    public long Bytes => Builds.Bytes + WorkingFiles.Bytes;

    public int Count => Builds.Removed.Count + WorkingFiles.Removed.Count;
}

/// <summary>
/// One clean-up of a data folder, shared by the window and the command line: superseded package builds
/// (<see cref="ArkiveSupersededBuildCleanup"/>), then working files nothing uses (<see cref="ArkiveWorkingFileCleanup"/>),
/// in that order, because a build's records keep the working files it was made from. Nothing is kept aside to be put
/// back, so every clean-up first writes what it will remove, with sizes and reasons, to reports\clean-up-*.txt.
/// </summary>
public sealed class ArkiveStorageCleanup
{
    public const string BuildsCategory = "Superseded package builds";
    private readonly string _dataRoot;
    private readonly GuidedTexturePackageWorkspace _packages;

    public ArkiveStorageCleanup(
        string dataRoot,
        ArkiveModEditWorkspaces edits,
        SkeletalMeshEditableWorkspaceStore meshExports,
        GuidedTexturePackageWorkspace packages,
        ArkiveModDeploymentService deployment)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _packages = packages;
        Builds = new ArkiveSupersededBuildCleanup(packages, deployment,
            Path.Combine(_dataRoot, "backups", "packages"), Path.Combine(_dataRoot, "backups", "installations"), _dataRoot);
        WorkingFiles = new ArkiveWorkingFileCleanup(_dataRoot, edits, meshExports);
    }

    public ArkiveSupersededBuildCleanup Builds { get; }

    public ArkiveWorkingFileCleanup WorkingFiles { get; }

    public string ReportsDirectory => Path.Combine(_dataRoot, "reports");

    /// <summary>The first step of a clean-up: drops the spare package copies finished builds keep (see <see cref="PackageRevisionTrimmer"/>).</summary>
    public Task<long> TrimBuildsAsync(CancellationToken cancellationToken = default) =>
        _packages.TrimRevisionsAsync(cancellationToken);

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
        _ => kind.ToString(),
    };

    /// <summary>
    /// What a clean-up would remove now. Working files are planned as if the planned builds were already gone, as
    /// they will be when the clean-up runs.
    /// </summary>
    public async Task<ArkiveStorageCleanupPlan> PlanAsync(
        TimeSpan minimumAge, bool fullInspection = false, CancellationToken cancellationToken = default)
    {
        var builds = await Builds.PlanAsync(fullInspection, cancellationToken: cancellationToken).ConfigureAwait(false);
        var files = await WorkingFiles.PlanAsync(minimumAge, removedBuilds: builds.Items, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new ArkiveStorageCleanupPlan(builds, files, minimumAge);
    }

    /// <summary>Removes the planned builds, then the planned working files; each is checked again just before.</summary>
    public async Task<ArkiveStorageCleanupResult> RunAsync(
        ArkiveStorageCleanupPlan plan, CancellationToken cancellationToken = default)
    {
        var builds = await Builds.DeleteAsync(plan.Builds, cancellationToken).ConfigureAwait(false);
        var files = await WorkingFiles.RemoveAsync(plan.WorkingFiles, plan.MinimumAge, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new ArkiveStorageCleanupResult(builds, files);
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
    /// path: every item to be removed with its size and reason, then every candidate kept and why.
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
            text.AppendLine($"  {BuildsCategory} | {build.Bytes} | {build.DirectoryPath} | {build.PackageName}, build {build.RevisionId}: {build.Reason}");
        foreach (var item in plan.WorkingFiles.Items)
        {
            text.AppendLine($"  {CategoryName(item.Kind)} | {item.Bytes} | {item.DirectoryPath} | {item.Description}: {item.Reason}");
            foreach (var owned in item.OwnedDirectories) text.AppendLine($"      with {owned}");
        }
        if (includeKept)
        {
            text.AppendLine();
            text.AppendLine("Kept (category | folder | reason):");
            foreach (var build in plan.Builds.Kept)
                text.AppendLine($"  {BuildsCategory} | {build.DirectoryPath} | {build.PackageName}, build {build.RevisionId}: {build.Reason}");
            foreach (var item in plan.WorkingFiles.Kept)
                text.AppendLine($"  {CategoryName(item.Kind)} | {item.DirectoryPath} | {item.Description}: {item.Reason}");
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
