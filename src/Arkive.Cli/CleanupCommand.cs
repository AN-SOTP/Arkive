using System.Diagnostics;
using System.Text.Json;
using Arkive.Core.Archives;
using Arkive.Core.Interface;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.Mods;
using Arkive.Core.Packages;
using Arkive.Core.StaticMeshes;

/// <summary>
/// working-files-cleanup: the window's Clean up working files, for a data folder. Package builds nothing can need go
/// first, then build files, working files and leftovers nothing uses, then old cache entries; the full list is written
/// to a report before anything is removed.
/// </summary>
internal static class CleanupCommand
{
    public static ArkiveStorageCleanup Create(string dataRoot)
    {
        var root = Path.GetFullPath(dataRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Arkive data folder was not found: {root}");
        var staging = Path.Combine(root, "staging");
        var reader = new UpkPackageReader();
        var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
        var edits = new ArkiveModEditWorkspaces(
            new GuidedTextureModWorkspace(Path.Combine(staging, "texture-mods")),
            new SkeletalMeshEditWorkspaceStore(Path.Combine(staging, "skeletal-mesh-edits")),
            new StaticMeshEditWorkspaceStore(Path.Combine(staging, "static-mesh-edits"), reader, provider),
            new MaterialInstanceEditWorkspaceStore(Path.Combine(staging, "material-edits"), provider))
        {
            InterfaceMovies = new InterfaceMovieEditWorkspaceStore(Path.Combine(staging, "interface-movie-edits"), provider),
        };
        return new ArkiveStorageCleanup(root, edits,
            new SkeletalMeshEditableWorkspaceStore(Path.Combine(staging, "skeletal-mesh-reconnaissance")),
            new GuidedTexturePackageWorkspace(Path.Combine(staging, "texture-mods", "_packages")),
            new ArkiveModDeploymentService(reader),
            new ArkiveModLibrary(Path.Combine(root, "mods"), Path.Combine(staging, "mod-library")));
    }

    public static int Run(string dataRoot, bool dryRun, string? reportPath, int? minimumAgeHours, bool json)
    {
        if (minimumAgeHours is < 0) throw new ArgumentException("--min-age-hours cannot be negative.");
        var cleanup = Create(dataRoot);
        var minimumAge = TimeSpan.FromHours(minimumAgeHours ?? 1);
        var clock = Stopwatch.StartNew();
        // The trim changes builds in place; a dry run leaves the data folder exactly as it was.
        var trimmed = dryRun ? 0 : cleanup.TrimBuildsAsync().GetAwaiter().GetResult();
        var plan = cleanup.PlanAsync(minimumAge, fullInspection: false).GetAwaiter().GetResult();
        var planSeconds = clock.Elapsed.TotalSeconds;
        var report = cleanup.WriteReport(plan, "the working-files-cleanup command" + (dryRun ? " (--dry-run)" : string.Empty),
            dryRun, reportPath);
        ArkiveStorageCleanupResult? result = null;
        if (!dryRun && plan.Count > 0)
        {
            result = cleanup.RunAsync(plan).GetAwaiter().GetResult();
            ArkiveStorageCleanup.AppendResult(report, result);
        }
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                DryRun = dryRun, Report = report, TrimmedBytes = trimmed, PlannedItems = plan.Count, PlannedBytes = plan.Bytes,
                Categories = plan.Categories, KeptBuilds = plan.Builds.Kept.Count, KeptWorkingFiles = plan.WorkingFiles.Kept.Count,
                RemovedItems = result?.Count, RemovedBytes = result?.Bytes, PlanSeconds = planSeconds,
                TotalSeconds = clock.Elapsed.TotalSeconds,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (trimmed > 0) Console.WriteLine($"Trimmed extra package copies: {ArkiveStorageCleanup.FormatBytes(trimmed)}.");
        Console.WriteLine($"{(dryRun ? "Would remove" : "Planned")} {plan.Count:N0} item(s), {ArkiveStorageCleanup.FormatBytes(plan.Bytes)} " +
                          $"(planned in {planSeconds:N1}s):");
        foreach (var category in plan.Categories)
            Console.WriteLine($"  {category.Name}: {category.Count:N0}, {ArkiveStorageCleanup.FormatBytes(category.Bytes)}");
        Console.WriteLine($"Kept {plan.Builds.Kept.Count:N0} package build(s) and {plan.WorkingFiles.Kept.Count:N0} working file(s); reasons are in the report.");
        if (result is not null)
            Console.WriteLine($"Removed {result.Count:N0} item(s), {ArkiveStorageCleanup.FormatBytes(result.Bytes)}; " +
                              $"{result.Builds.NotRemoved.Count + result.WorkingFiles.NotRemoved.Count:N0} planned item(s) were left after their last check.");
        Console.WriteLine($"Report: {report}");
        Console.WriteLine($"Took {clock.Elapsed.TotalSeconds:N1}s.");
        return 0;
    }

    /// <summary>After arkive-apply: the superseded builds of the packages it just rebuilt, as the window does.</summary>
    public static void AfterApply(string dataRoot, ArkiveModApplyResult result, bool json)
    {
        try
        {
            if (Create(dataRoot).AfterApplyAsync(result).GetAwaiter().GetResult() is not { } cleaned) return;
            if (!json)
                Console.WriteLine($"Removed {cleaned.Result.Removed.Count:N0} superseded build(s) of the rebuilt packages, " +
                                  $"{ArkiveStorageCleanup.FormatBytes(cleaned.Result.Bytes)}. Report: {cleaned.ReportPath}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                              InvalidDataException or NotSupportedException)
        {
            Console.Error.WriteLine($"warning: superseded builds were not cleaned up: {exception.Message}");
        }
    }
}
