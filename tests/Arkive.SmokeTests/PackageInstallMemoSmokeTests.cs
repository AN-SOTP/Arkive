using static Arkive.SmokeTests.ArkiveModTestFixture;
using Setup = Arkive.SmokeTests.PackageInstallStateSmokeTests.Setup;

namespace Arkive.SmokeTests;

/// <summary>
/// The install-state memo launch shows the Mods list from (PackageInstallMemo): an entry is used only while every file
/// its state came from is unchanged, gives the same status as the install-state service, and is ignored or corrected
/// otherwise.
/// </summary>
internal static class PackageInstallMemoSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("install memo: entries are used while nothing changed and give the service's status", TestUsedWhileUnchanged),
        new("install memo: each change to a file a state came from has the package checked again", TestEachChangeInvalidates),
        new("install memo: another version, data folder, game folder, format or a damaged file is ignored", TestIgnored),
        new("install memo: the background check corrects a wrong entry", TestBackgroundCorrects),
    ];

    private const string Version = "memo-test";

    private sealed record Installed(Setup Setup, GuidedTexturePackageRevision Earlier, GuidedTexturePackageRevision Latest)
    {
        public string Root => Setup.Environment.Root;

        public ArkiveModBundleManifest Bundle(GuidedTexturePackageRevision revision) =>
            ArkiveModBundleService.LoadBundleAsync(revision.ModBundleManifestPath).GetAwaiter().GetResult();

        public string Receipt(GuidedTexturePackageRevision revision) =>
            Path.Combine(Setup.Environment.State, Bundle(revision).ModId, ArkiveModDeploymentService.ReceiptFileName);

        public string Backup(GuidedTexturePackageRevision revision)
        {
            var bundle = Bundle(revision);
            return Path.Combine(Setup.Environment.Backups, bundle.RequiredSourcePackageSha256, bundle.SourcePhysicalPackageName);
        }
    }

    /// <summary>Two builds of one package, the latest installed (or none, with <paramref name="install"/> false).</summary>
    private static Installed NewInstalled(string name, bool install = true)
    {
        var setup = PackageInstallStateSmokeTests.NewSetup("memo-" + name);
        var earlier = PackageInstallStateSmokeTests.Build(setup, 0.4f);
        var latest = PackageInstallStateSmokeTests.Build(setup, 0.7f);
        if (install) setup.Environment.Deployment.InstallAsync(setup.Request(latest)).GetAwaiter().GetResult();
        Age(setup);
        return new Installed(setup, earlier, latest);
    }

    /// <summary>
    /// Moves every file's write time an hour back: a state is remembered only when no file it came from was written in
    /// the two seconds before it was checked, and the fixtures were all written just now.
    /// </summary>
    private static void Age(Setup setup)
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(setup.Environment.Root)!, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file) - TimeSpan.FromHours(1));
    }

    private static IReadOnlyList<GuidedTexturePackageEntry> Projects(Setup setup) =>
        setup.Environment.Packages.LoadAsync().GetAwaiter().GetResult();

    private static PackageInstallChecks Check(Setup setup, PackageInstallMemoDocument? memo, bool useMemo = true) =>
        PackageInstallMemo.CheckAllAsync(new PackageInstallStateService(setup.Context), Projects(setup), memo,
            setup.Environment.Root, useMemo).GetAwaiter().GetResult();

    /// <summary>The memo of <paramref name="checks"/>, saved to the data folder and loaded back as launch loads it.</summary>
    private static PackageInstallMemoDocument SaveAndLoad(Setup setup, PackageInstallChecks checks)
    {
        PackageInstallMemo.Save(setup.Environment.Root, PackageInstallMemo.Document(checks, setup.Environment.Root, Version, setup.GameDirectory));
        return PackageInstallMemo.Load(setup.Environment.Root, Version, setup.GameDirectory) ??
               throw new InvalidOperationException("The memo just saved could not be loaded back.");
    }

    /// <summary>
    /// Each package's state in <paramref name="checks"/> is what the install-state service finds now, and the Mods list
    /// map made from them is the one made from the service's states.
    /// </summary>
    private static void AssertSameAsService(Setup setup, PackageInstallChecks checks, string when)
    {
        var service = new PackageInstallStateService(setup.Context);
        var projects = checks.Packages.Select(check => check.Package).ToArray();
        var states = service.ResolveAllAsync(projects).GetAwaiter().GetResult();
        foreach (var check in checks.Packages)
        {
            var real = PackageInstallSummary.Of(states[check.Package]);
            Assert(PackageInstallSummary.Same(check.Summary, real),
                $"{when}: the state for {check.Package.Manifest.PackageAlias} ({(check.FromMemo ? "memo" : "checked")}) " +
                $"is {PackageInstallSummary.Describe(check.Summary)}, the service finds {PackageInstallSummary.Describe(real)}");
        }
        var fromChecks = PackageInstallStateService.ActiveRevisionsByPhysicalName(projects, setup.GameDirectory,
            project => checks.Packages.Single(check => check.Package == project).Summary);
        var fromService = PackageInstallStateService.ActiveRevisionsByPhysicalName(projects, setup.GameDirectory,
            project => states.GetValueOrDefault(project));
        Assert(fromChecks.Count == fromService.Count && fromChecks.All(pair =>
                fromService.TryGetValue(pair.Key, out var other) && other?.RevisionId == pair.Value?.RevisionId),
            $"{when}: the Mods list map from the memo differs from the service's");
    }

    /// <summary>
    /// Four packages in every state the service can find (latest installed, an earlier build still installed, the
    /// clean original back, no build at all): the first check makes an entry for each, the next uses every one, and
    /// both give the service's status.
    /// </summary>
    private static void TestUsedWhileUnchanged()
    {
        var setup = PackageInstallStateSmokeTests.NewSetup("memo-states");
        _ = PackageInstallStateSmokeTests.Build(setup, 0.4f);
        var latest = PackageInstallStateSmokeTests.Build(setup, 0.7f);
        setup.Environment.Deployment.InstallAsync(setup.Request(latest)).GetAwaiter().GetResult();
        var earlierPath = Path.Combine(setup.GameDirectory, "material-earlier.upk");
        WriteMaterialInstancePackage(earlierPath);
        var earlier = PackageInstallStateSmokeTests.Build(setup, 0.3f, earlierPath, "material_earlier");
        _ = PackageInstallStateSmokeTests.Build(setup, 0.45f, earlierPath, "material_earlier");
        setup.Environment.Deployment.InstallAsync(setup.Request(earlier)).GetAwaiter().GetResult();
        var restoredPath = Path.Combine(setup.GameDirectory, "material-restored.upk");
        WriteMaterialInstancePackage(restoredPath);
        var restored = PackageInstallStateSmokeTests.Build(setup, 0.6f, restoredPath, "material_restored");
        setup.Environment.Deployment.InstallAsync(setup.Request(restored)).GetAwaiter().GetResult();
        setup.Environment.Deployment.RestoreAsync(setup.Request(restored)).GetAwaiter().GetResult();
        var emptyPath = Path.Combine(setup.GameDirectory, "material-empty.upk");
        WriteMaterialInstancePackage(emptyPath);
        setup.Environment.Packages.GetOrCreateAsync("material_empty", "material-empty.upk", emptyPath).GetAwaiter().GetResult();
        Age(setup);

        var first = Check(setup, null);
        Assert(first is { FromMemo: 0, Inspected: 4, NotRemembered: 0 } && first.Packages.All(check => check.Entry is not null),
            $"the first check did not make an entry for each package: inspected={first.Inspected}, notRemembered={first.NotRemembered}");
        AssertSameAsService(setup, first, "first check");
        var summaries = first.Packages.Select(check => check.Summary).ToArray();
        Assert(summaries.Count(summary => summary is { Active: true, InstalledByThisArkive: true, ForDeletionIdentityOnly: false }) == 1 &&
               summaries.Count(summary => summary is { Active: true, ForDeletionIdentityOnly: true }) == 1 &&
               summaries.Count(summary => summary is { Active: false }) == 1 && summaries.Count(summary => summary is null) == 1,
            "the fixture's packages are not in the four states: " + string.Join("; ", summaries.Select(PackageInstallSummary.Describe)));

        var memo = SaveAndLoad(setup, first);
        var second = Check(setup, memo);
        Assert(second is { FromMemo: 4, Inspected: 0 } && second.FileChecks == memo.Entries.Sum(entry => entry.Files.Count),
            $"unchanged entries were not all used: fromMemo={second.FromMemo}, inspected={second.Inspected}, " +
            $"fileChecks={second.FileChecks}");
        AssertSameAsService(setup, second, "from the memo");
        var installed = memo.Entries.Single(entry => entry is { Active: true, ForDeletionIdentityOnly: false });
        Assert(installed.Files.Count == 6 && installed.Files.All(file => file.Exists),
            $"the installed latest build's entry does not stamp exactly its build, readiness, payload, receipt, backup and " +
            $"game file: {string.Join(", ", installed.Files.Select(file => Path.GetFileName(file.Path)))}");
        Assert(memo.Entries.Single(entry => !entry.HasBuild).Files.Count == 0, "a project with no build stamped files");

        // A change to the record that the check does not read (a favourite) leaves the entry standing.
        var project = Projects(setup).Single(item => item.Manifest.PackageAlias == "material_instance");
        var text = File.ReadAllText(project.ManifestPath);
        File.WriteAllText(project.ManifestPath, text.Replace("\"isFavorite\": false", "\"isFavorite\": true"));
        Assert(Projects(setup).Single(item => item.Manifest.PackageAlias == "material_instance").Manifest.IsFavorite,
            "the fixture could not mark the project a favourite");
        Assert(Check(setup, memo) is { FromMemo: 4 }, "marking a project a favourite, which its state does not depend on, dropped its entry");
    }

    /// <summary>
    /// For each kind of change, a new installed package: its entry is used before the change, and after it the package
    /// is checked again by the service, with the service's status.
    /// </summary>
    private static void TestEachChangeInvalidates()
    {
        (string Name, bool Install, Action<Installed> Change)[] changes =
        [
            ("payload replaced", true, fixture =>
            {
                var bytes = File.ReadAllBytes(fixture.Latest.PayloadPath);
                bytes[^1] ^= 0x5A;
                File.WriteAllBytes(fixture.Latest.PayloadPath, bytes);
            }),
            ("game file replaced", true, fixture => File.Copy(fixture.Backup(fixture.Latest), fixture.Setup.PackagePath, overwrite: true)),
            ("receipt changed", true, fixture =>
            {
                var path = fixture.Receipt(fixture.Latest);
                var text = File.ReadAllText(path);
                Assert(text.Contains("\"gameFilesModified\": true") && text.Contains("\"state\": \"Installed\""),
                    "the receipt is not written as the test expects");
                File.WriteAllText(path, text.Replace("\"gameFilesModified\": true", "\"gameFilesModified\": false")
                    .Replace("\"state\": \"Installed\"", "\"state\": \"Restored\""));
            }),
            ("receipt removed", true, fixture => File.Delete(fixture.Receipt(fixture.Latest))),
            ("backup removed", true, fixture => File.Delete(fixture.Backup(fixture.Latest))),
            ("project record changed", true, fixture =>
            {
                var project = Projects(fixture.Setup).Single();
                var text = File.ReadAllText(project.ManifestPath);
                File.WriteAllText(project.ManifestPath, text.Replace(
                    $"\"latestRevisionId\": \"{fixture.Latest.RevisionId}\"", $"\"latestRevisionId\": \"{fixture.Earlier.RevisionId}\""));
                Assert(Projects(fixture.Setup).Single().Manifest.LatestRevisionId == fixture.Earlier.RevisionId,
                    "the fixture could not name the earlier build the latest");
            }),
            ("a new build added", true, fixture => PackageInstallStateSmokeTests.Build(fixture.Setup, 0.9f)),
            ("a file appearing where one was absent", false, fixture =>
            {
                var path = fixture.Receipt(fixture.Latest);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "{ this is not a receipt");
            }),
        ];
        foreach (var (name, install, change) in changes)
        {
            var fixture = NewInstalled(name.Replace(' ', '-'), install);
            var memo = SaveAndLoad(fixture.Setup, Check(fixture.Setup, null));
            Assert(memo.Entries.Count == 1, $"{name}: no entry was made for the package");
            if (!install)
                Assert(memo.Entries[0].Files.Any(file => !file.Exists &&
                           Path.GetFileName(file.Path) == ArkiveModDeploymentService.ReceiptFileName),
                    $"{name}: the entry of a build never installed does not record its receipt as absent");
            var before = Check(fixture.Setup, memo);
            Assert(before is { FromMemo: 1, Inspected: 0 }, $"{name}: the entry was not used before the change");
            AssertSameAsService(fixture.Setup, before, $"{name}, before");

            change(fixture);
            var after = Check(fixture.Setup, memo);
            Assert(after is { FromMemo: 0, Inspected: 1 }, $"{name}: the entry was still used after the change");
            AssertSameAsService(fixture.Setup, after, $"{name}, after");
        }
    }

    private static void TestIgnored()
    {
        var fixture = NewInstalled("ignored");
        var setup = fixture.Setup;
        var root = setup.Environment.Root;
        _ = SaveAndLoad(setup, Check(setup, null));
        var path = PackageInstallMemo.PathFor(root);
        var saved = File.ReadAllText(path);
        Assert(PackageInstallMemo.Load(root, "another-version", setup.GameDirectory) is null,
            "a memo made by another version of Arkive was used");
        Assert(PackageInstallMemo.Load(root, Version, Path.Combine(setup.GameDirectory, "other")) is null &&
               PackageInstallMemo.Load(root, Version, null) is null,
            "a memo made for another game folder was used");
        var otherRoot = Path.Combine(Path.GetDirectoryName(root)!, "other-data");
        Directory.CreateDirectory(Path.Combine(otherRoot, "cache"));
        File.Copy(path, PackageInstallMemo.PathFor(otherRoot), overwrite: true);
        Assert(PackageInstallMemo.Load(otherRoot, Version, setup.GameDirectory) is null,
            "a memo made for another data folder was used");
        File.WriteAllText(path, saved.Replace($"\"formatVersion\":{PackageInstallMemo.FormatVersion}", "\"formatVersion\":99"));
        Assert(saved.Contains($"\"formatVersion\":{PackageInstallMemo.FormatVersion}") &&
               PackageInstallMemo.Load(root, Version, setup.GameDirectory) is null,
            "a memo of another format was used");
        File.WriteAllText(path, saved[..(saved.Length / 2)]);
        Assert(PackageInstallMemo.Load(root, Version, setup.GameDirectory) is null, "a damaged memo was used");
        File.WriteAllText(path, saved);
        Assert(PackageInstallMemo.Load(root, Version, setup.GameDirectory) is not null, "the memo put back could not be loaded");
    }

    /// <summary>
    /// An entry that says the installed build is not in the game, with every file unchanged: launch would show it (the
    /// memo is only ever shown), and the background check, which checks every package with the service, finds the
    /// build in the game and makes the entry right.
    /// </summary>
    private static void TestBackgroundCorrects()
    {
        var fixture = NewInstalled("wrong-entry");
        var setup = fixture.Setup;
        var memo = SaveAndLoad(setup, Check(setup, null));
        var wrong = memo with
        {
            Entries = memo.Entries.Select(entry => entry with { Active = false, InstalledByThisArkive = false }).ToArray(),
        };
        PackageInstallMemo.Save(setup.Environment.Root, wrong);
        wrong = PackageInstallMemo.Load(setup.Environment.Root, Version, setup.GameDirectory)!;

        var launch = Check(setup, wrong);
        Assert(launch is { FromMemo: 1 } && launch.Packages[0].Summary is { Active: false },
            "the deliberately wrong entry was not what launch would show");
        var background = Check(setup, wrong, useMemo: false);
        Assert(background is { FromMemo: 0, Inspected: 1 } &&
               background.Packages[0].Summary is { Active: true, InstalledByThisArkive: true } summary &&
               summary.Revision.RevisionId == fixture.Latest.RevisionId &&
               background.Packages[0].Entry is { Active: true, InstalledByThisArkive: true },
            "the background check did not find the installed build and correct the entry");
        AssertSameAsService(setup, background, "background check");
        var corrected = SaveAndLoad(setup, background);
        var next = Check(setup, corrected);
        Assert(next is { FromMemo: 1 } && next.Packages[0].Summary is { Active: true }, "the corrected memo is not what the next launch shows");
        AssertSameAsService(setup, next, "after the correction");
    }
}
