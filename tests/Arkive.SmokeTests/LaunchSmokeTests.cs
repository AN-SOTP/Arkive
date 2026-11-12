using System.Globalization;
using Arkive.Core.Diagnostics;

namespace Arkive.SmokeTests;

internal static class LaunchSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("builds a launch fixture that loads cleanly through every store, the library and the Mods list status", TestFixtureLoads),
        new("runs the launch benchmark on a fixture, reporting every step and writing nothing", TestBenchmark),
        new("dumps the Mods list and the Apply plan byte for byte the same each time and in another copy, only reading", TestDumps),
        new("measures startup with no visible window, writing only its results and measure-logs", TestMeasuringMode, AppPrerequisite),
    ];

    private static void TestFixtureLoads()
    {
        var root = Path.Combine(fixtureDirectory, "launch-fixture");
        var options = new LaunchFixtureOptions(MeshEdits: 40, Textures: 30, Effects: 60, Materials: 30, Packages: 12,
            BuildsPerPackage: 2, Mods: 10);
        var summary = LaunchFixture.CreateAsync(root, options).GetAwaiter().GetResult();
        Console.WriteLine("  " + LaunchFixture.Describe(summary));
        var staging = Path.Combine(root, "staging");
        var edits = new ArkiveModEditWorkspaces(
            new GuidedTextureModWorkspace(Path.Combine(staging, "texture-mods")),
            new SkeletalMeshEditWorkspaceStore(Path.Combine(staging, "skeletal-mesh-edits")),
            new StaticMeshEditWorkspaceStore(Path.Combine(staging, "static-mesh-edits")),
            new MaterialInstanceEditWorkspaceStore(Path.Combine(staging, "material-edits")));
        var meshes = edits.SkeletalMeshEdits.LoadAsync().GetAwaiter().GetResult();
        var exports = new SkeletalMeshEditableWorkspaceStore(Path.Combine(staging, "skeletal-mesh-reconnaissance")).LoadAsync().GetAwaiter().GetResult();
        Assert(meshes.Entries.Count == 40 && meshes.Issues.Count == 0 && exports.Entries.Count == 40 && exports.Issues.Count == 0 &&
               exports.Entries.All(entry => entry.GlbLengthMatches && entry.IsCurrentSchema),
            $"the mesh stores read {meshes.Entries.Count} edits ({meshes.Issues.Count} unreadable) and {exports.Entries.Count} exports");
        Assert(edits.Textures.LoadAsync().GetAwaiter().GetResult().Count == 30 &&
               edits.MaterialEdits.LoadAsync().GetAwaiter().GetResult().Count == 30 &&
               edits.ParticleEdits.LoadAsync().GetAwaiter().GetResult().Count == 60,
            "the texture, material or effect store did not read every record");
        var record = new FileInfo(meshes.Entries[0].ManifestPath).Length;
        var skeleton = JsonSerializer.SerializeToUtf8Bytes(meshes.Entries[0].Manifest.Preflight.SkeletonCompatibility,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }).Length;
        Assert(record is > 400_000 and < 490_000 && skeleton is > 250_000 and < 360_000,
            $"a mesh edit record is {record:N0} bytes with a {skeleton:N0}-byte skeleton report; the owner's are about 443 KB and 300 KB");
        Console.WriteLine($"  mesh edit record {record:N0} bytes, skeleton report {skeleton:N0} bytes");

        var library = new ArkiveModLibrary(Path.Combine(root, "mods"), Path.Combine(staging, "mod-library"));
        var manifestBefore = File.ReadAllBytes(library.ManifestPath);
        var scan = library.ScanAsync().GetAwaiter().GetResult();
        Assert(scan.RegisteredModIds.Count + scan.RefreshedModIds.Count + scan.Issues.Count == 0 &&
               File.ReadAllBytes(library.ManifestPath).SequenceEqual(manifestBefore),
            "scanning the fixture's mods folder found new, changed or unreadable mod files");
        Assert(ArkiveModSelectionService.SynchronizeAsync(library, edits).GetAwaiter().GetResult().Count == 0,
            "the fixture's working files are not checked for the build as their mods' selection says");
        var manifest = library.LoadAsync().GetAwaiter().GetResult();
        Assert(manifest.PackageProjectsMigrated && manifest.GamePackagesDirectory == summary.GamePackagesDirectory &&
               manifest.Mods.Count == summary.Mods && manifest.Mods.Count(mod => mod.Enabled) == summary.EnabledMods,
            "the library does not hold the fixture's mods and game folder");

        var snapshot = ArkiveModEditSnapshot.LoadAsync(edits).GetAwaiter().GetResult();
        Assert(snapshot.Edits.Count == 160, $"the working-file snapshot read {snapshot.Edits.Count} of 160 working files");
        var statuses = ArkiveModApplyService.DescribeAsync(library, manifest, snapshot).GetAwaiter().GetResult();
        var assets = statuses.SelectMany(status => status.Assets.Select(asset => (status.Mod, asset))).ToArray();
        Assert(assets.Length == summary.EnabledEdits + summary.DisabledEdits &&
               assets.All(item => item.asset.Setup == ArkiveModSetupState.Ready && !item.asset.ModifiedLocally &&
                                  item.asset.Outcome == (item.Mod.Enabled ? ArkiveModAssetOutcome.Winning : ArkiveModAssetOutcome.Disabled)),
            "a fixture mod asset is not set up, is modified, or has the wrong conflict outcome");
        var total = (double)snapshot.Edits.Count;
        Assert(Math.Abs(summary.EnabledEdits / total - 0.156) < 0.08 && Math.Abs(summary.DisabledEdits / total - 0.364) < 0.1 &&
               Math.Abs(summary.UnboundEdits / total - 0.48) < 0.1,
            $"the fixture's shares are {summary.EnabledEdits}/{summary.DisabledEdits}/{summary.UnboundEdits} of {total}, not about 15/37/48 %");

        var packages = new GuidedTexturePackageWorkspace(Path.Combine(staging, "texture-mods", "_packages")).LoadAsync().GetAwaiter().GetResult();
        Assert(packages.Count == 12 && packages.All(package => package.Manifest.Revisions.Count == 2 &&
                                                              package.Manifest.Revisions.All(revision => File.Exists(revision.PayloadPath))),
            "the package projects or their builds are missing");
        Directory.Delete(root, recursive: true);
    }

    private static void TestBenchmark()
    {
        var root = Path.Combine(fixtureDirectory, "launch-benchmark");
        var summary = LaunchFixture.CreateAsync(root, new LaunchFixtureOptions(MeshEdits: 200, Textures: 60, Effects: 300,
            Materials: 80, Packages: 60)).GetAwaiter().GetResult();
        Console.WriteLine("  " + LaunchFixture.Describe(summary));
        var before = Listing(root);
        var report = LaunchBenchmark.RunAsync(root, forcedGc: true).GetAwaiter().GetResult();
        Assert(report.Error is null, "the benchmark failed: " + report.Error);
        string[] expected = ["hash-cache", "mesh-exports", "mesh-edits", "static-mesh-edits", "materials", "textures", "effects",
            "physics", "interface-movies", "library", "working-files", "package-projects", "install-state", "status", "database",
            "index-status"];
        Assert(report.Steps.Select(step => step.Id).SequenceEqual(expected),
            "the benchmark's steps were " + string.Join(", ", report.Steps.Select(step => step.Id)));
        Assert(report.Steps.All(step => step.Error is null && step.Seconds >= 0 && step.HeapBytes > 0),
            "a benchmark step failed or reported nothing");
        Assert(report.Steps.Single(step => step.Id == "working-files").Note == "items=640" &&
               report.Steps.Single(step => step.Id == "mesh-edits").Note == "items=200, unreadable=0" &&
               report.Steps.Single(step => step.Id == "package-projects").Note == "items=60, builds=180",
            "the benchmark did not read every working file and package project");
        Assert(report.Steps.Where(step => step.Id is "database" or "index-status").All(step => step.Skipped),
            "the benchmark did not report the game database and index status as absent");
        foreach (var step in report.Steps)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {step.Id,-18} {step.Seconds,7:0.000} s  cpu {step.CpuSeconds,7:0.000} s  read {step.BytesRead / 1048576d,7:0.0} MB  " +
                $"heap {step.HeapBytes / 1048576d,6:0} MB  {(step.Skipped ? "skipped" : step.Note)}"));
        Assert(Listing(root).SequenceEqual(before), "the benchmark wrote to the data folder: " + Difference(before, Listing(root)));
        var json = Path.Combine(fixtureDirectory, "launch-benchmark.json");
        report.Write(json);
        Assert(LaunchMeasurementReport.Read(json).Steps.Count == expected.Length, "the benchmark's JSON did not read back");
        Directory.Delete(root, recursive: true);
    }

    private static void TestDumps()
    {
        var options = new LaunchFixtureOptions(MeshEdits: 24, Textures: 20, Effects: 30, Materials: 16, Packages: 10,
            BuildsPerPackage: 2, Mods: 8);
        var first = Path.Combine(fixtureDirectory, "launch-dump-first");
        var second = Path.Combine(fixtureDirectory, "launch-dump-second-copy");
        LaunchFixture.CreateAsync(first, options).GetAwaiter().GetResult();
        LaunchFixture.CreateAsync(second, options).GetAwaiter().GetResult();
        var before = Listing(first);
        var (list, plan) = Dumps(first);
        var (listAgain, planAgain) = Dumps(first);
        Assert(list == listAgain && plan == planAgain, "two dumps of the same data folder differ");
        Assert(Listing(first).SequenceEqual(before), "dumping wrote to the data folder: " + Difference(before, Listing(first)));
        var (otherList, otherPlan) = Dumps(second);
        Assert(list == otherList, "the Mods list dumps of two copies of the same data differ:\n" + FirstDifference(list, otherList));
        Assert(plan == otherPlan, "the Apply plan dumps of two copies of the same data differ:\n" + FirstDifference(plan, otherPlan));
        Assert(!list.Contains(first, StringComparison.OrdinalIgnoreCase) && !plan.Contains(first, StringComparison.OrdinalIgnoreCase) &&
               !list.Contains(fixtureDirectory, StringComparison.OrdinalIgnoreCase),
            "a dump names the data folder's location");
        using var listDocument = JsonDocument.Parse(list);
        using var planDocument = JsonDocument.Parse(plan);
        var mods = listDocument.RootElement.GetProperty("mods");
        Assert(mods.GetArrayLength() > 0 && mods.EnumerateArray().All(mod => mod.GetProperty("assets").GetArrayLength() > 0) &&
               planDocument.RootElement.GetProperty("packages").GetArrayLength() > 0,
            "the dumps are missing the mods, their assets or the plan's packages");
        Console.WriteLine($"  Mods list dump {list.Length:N0} characters, {mods.GetArrayLength()} mods; Apply plan dump {plan.Length:N0} characters, " +
                          $"{planDocument.RootElement.GetProperty("packages").GetArrayLength()} packages");
        Directory.Delete(first, recursive: true);
        Directory.Delete(second, recursive: true);

        static (string List, string Plan) Dumps(string root)
        {
            var environment = ArkiveModTestFixture.CreateEnvironment(root);
            var context = new ArkiveModApplyContext(
                environment.Library, environment.Edits, environment.Packages, environment.ModSets, environment.Deployment,
                environment.Backups, environment.State)
            {
                GameDataRoot = root,
            };
            var apply = new ArkiveModApplyService(context, ArkiveModTestFixture.CreateMaterializer(environment), null);
            return (ArkiveModParityDumps.ListAsync(context, apply, root).GetAwaiter().GetResult(),
                ArkiveModParityDumps.PlanAsync(apply, environment.Library, root).GetAwaiter().GetResult());
        }
    }

    private static void TestMeasuringMode()
    {
        var root = Path.Combine(fixtureDirectory, "launch-measure");
        LaunchFixture.CreateAsync(root, new LaunchFixtureOptions(MeshEdits: 30, Textures: 20, Effects: 40, Materials: 20,
            Packages: 10, BuildsPerPackage: 2, Mods: 8)).GetAwaiter().GetResult();
        var before = Listing(root);
        var output = Path.Combine(fixtureDirectory, "launch-measure.json");
        var run = HiddenDesktop.Run(AppExecutable()!, $"--measure-startup \"{output}\" --data-root \"{root}\" --forced-gc --wait-background",
            TimeSpan.FromMinutes(5));
        Assert(!run.TimedOut, "the measuring run did not finish within five minutes");
        Assert(run.VisibleWindows.Count == 0, "the measuring run showed a window: " + string.Join(", ", run.VisibleWindows));
        Assert(File.Exists(output), $"the measuring run (exit code {run.ExitCode}) wrote no results");
        var report = LaunchMeasurementReport.Read(output);
        Assert(run.ExitCode == 0 && report.Error is null, $"the measuring run failed (exit code {run.ExitCode}): {report.Error}");
        string[] loading = ["window", "hash-cache", "outside-change-cache", "exports", "baselines", "mesh", "static-mesh", "materials",
            "library", "packages", "drafts", "update-audit", "mod-sets", "modified-meshes", "index-status", "library-search"];
        var ran = report.Steps.Where(step => !step.Skipped).Select(step => step.Id).ToArray();
        Assert(ran.SequenceEqual(loading), "the measuring run's loading steps were " + string.Join(", ", ran));
        Assert(report.Steps.Where(step => step.Skipped).Select(step => step.Id).Order(StringComparer.Ordinal).SequenceEqual(
                ["cache-save", "download-and-viewer-cleanup", "game-update-notice", "mod-file-import", "package-build-trim", "preview-cache-cleanup"]),
            "the measuring run did not skip exactly the steps that write user state");
        Assert(report.ForcedGc && report.DataRoot == Path.GetFullPath(root), "the results do not describe this run");
        var after = Listing(root);
        var logs = after.Where(entry => entry.StartsWith(StartupLogs, StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert(logs.Any(entry => entry.Contains(".log|", StringComparison.OrdinalIgnoreCase)),
            "the measuring run wrote no session log to measure-logs");
        var rest = after.Except(logs).ToArray();
        Assert(rest.SequenceEqual(before), "the measuring run wrote to the data folder: " + Difference(before, rest));
        Assert(!Directory.Exists(Path.Combine(root, "logs")), "the measuring run wrote to the normal logs folder");
        foreach (var step in report.Steps.Where(step => !step.Skipped))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {step.Id,-20} {step.Seconds,7:0.000} s  blocked {step.UiBlockedSeconds,6:0.00} s  heap {step.HeapBytes / 1048576d,6:0} MB"));
        Directory.Delete(root, recursive: true);
    }

    private const string StartupLogs = "measure-logs";

    private static SmokeTestPrerequisiteResult AppPrerequisite() => AppExecutable() is not null
        ? SmokeTestPrerequisiteResult.Available
        : new(false, "Arkive.App.exe of this configuration is not built");

    /// <summary>The app built beside these tests (same configuration), or null.</summary>
    private static string? AppExecutable()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = output.Parent?.Name;
        var repository = output.Parent?.Parent?.Parent?.Parent?.Parent?.FullName;
        if (configuration is null || repository is null) return null;
        var path = Path.Combine(repository, "src", "Arkive.App", "bin", configuration, "net10.0-windows", "Arkive.App.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Every folder and file under <paramref name="root"/> with its size and write time, sorted.</summary>
    private static string[] Listing(string root) =>
        Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(root, path) + "\\")
            .Concat(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(path =>
            {
                var info = new FileInfo(path);
                return $"{Path.GetRelativePath(root, path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            }))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Difference(IReadOnlyList<string> before, IReadOnlyList<string> after) =>
        string.Join("; ", before.Except(after).Select(entry => "was " + entry).Concat(after.Except(before).Select(entry => "now " + entry)).Take(10));

    private static string FirstDifference(string left, string right)
    {
        var leftLines = left.Split('\n');
        var rightLines = right.Split('\n');
        for (var index = 0; index < Math.Min(leftLines.Length, rightLines.Length); index++)
            if (leftLines[index] != rightLines[index])
                return $"line {index + 1}: {leftLines[index].Trim()} | {rightLines[index].Trim()}";
        return "one dump is longer";
    }
}
