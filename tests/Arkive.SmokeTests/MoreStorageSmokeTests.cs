using Arkive.Core.Diagnostics;
using static Arkive.SmokeTests.ArkiveModTestFixture;

namespace Arkive.SmokeTests;

internal static class MoreStorageSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("trims a verified build's step copies once, leaving every reader of the build as it was", TestBuildTrim),
        new("removes every build of a package nothing uses, and keeps used, recent and mod-set packages", TestUnusedPackageBuilds),
        new("releases a disabled mod's working files and sets them up again to the same game file", TestReleaseWorkingFiles),
        new("cleans up cache entries unused for 30 days, keeping recent, reused and non-cache files", TestCaches),
        new("cleans up unpacked mod files and material candidates nothing needs", TestUnpackedAndCandidates),
    ];

    private static readonly DateTime LongAgo = DateTime.UtcNow.AddDays(-40);

    private static void TestBuildTrim()
    {
        // Which files the second pass takes, by their place in a build.
        string[] unneeded =
        [
            @"m01-0123abcd\02-editable-reference\piece.arkive-edit.glb", @"m01-0123abcd\04-native-encoding\piece.skeletalmesh.edit.bin",
            @"m01-0123abcd\04-native-encoding\piece.native-decode-proof.glb", @"m02-0123abcd\03-rebased-edit\a\b\arkive-edited-skeletalmesh.json",
            @"s01-0123abcd\03-rebased-edit\a\piece.glb", @"01-textures\a01-0123abcd\05-encode\piece.dds", @"01-textures\a01-0123abcd\x.png",
            @"01-textures\a01-0123abcd\x.crn", @"01-textures\a01-0123abcd\x.ue3bulk", @"u01-0123abcd\01-candidate\candidate-export.bin",
        ];
        string[] needed =
        [
            @"arkive-package-composition.json", @"arkive-trimmed.json", @"bundle\arkive-mod.json", @"bundle\payload\EFGame\x.bin",
            @"bundle\x.glb", @"m01-0123abcd\02-editable-reference\arkive-editable-skeletalmesh.json",
            @"m01-0123abcd\01-recon\x\arkive-skeletalmesh-recon.json", @"m01-0123abcd\04-native-encoding\arkive-skeletalmesh-native-encoding.json",
            @"m01-0123abcd\05-staged-upk\arkive-skeletalmesh-staged-upk.json", @"01-textures\a01-0123abcd\arkive-replacement-upk.json",
            @"03-rebased-edit\x.json", @"v01-values\arkive-values-pass.json",
        ];
        foreach (var path in unneeded)
            Assert(PackageRevisionTrimmer.IsUnneededFile(path), $"the trimmer would keep {path}");
        foreach (var path in needed)
            Assert(!PackageRevisionTrimmer.IsUnneededFile(path), $"the trimmer would delete {path}, which is read again");

        var root = Path.Combine(fixtureDirectory, "more-storage-trim");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var (env, service, packagePath, _, second) = StorageCleanupSmokeTests.TwoBuilds(root);
        var directory = Path.GetDirectoryName(second.CompositionManifestPath!)!;
        var marker = Path.Combine(directory, PackageRevisionTrimmer.MarkerFileName);
        var bundle = Path.Combine(directory, "bundle");
        // A build made now has had both passes: its material candidate's export is gone, its records are kept.
        Assert(PackageRevisionTrimmer.MarkerVersion(marker) == PackageRevisionTrimmer.CurrentMarkerVersion,
            "a committed build was not marked as trimmed by the second pass");
        Assert(!Directory.EnumerateFiles(directory, "*.bin", SearchOption.AllDirectories).Any(file => !file.StartsWith(bundle, StringComparison.OrdinalIgnoreCase)) &&
               Directory.EnumerateFiles(directory, "arkive-material-candidate.json", SearchOption.AllDirectories).Any(),
            "a committed build kept its step's candidate export, or lost the candidate's record");

        // A build an earlier Arkive trimmed: its marker has no version, and it still has its steps' copies.
        File.WriteAllText(marker, "{\"trimmedAt\":\"2026-10-01T00:00:00.0000000+00:00\",\"freedBytes\":5}");
        var copies = unneeded.Select(path => Write(Path.Combine(directory, path), 1000)).ToArray();
        var records = new[]
        {
            Write(Path.Combine(directory, @"m01-0123abcd\02-editable-reference\arkive-editable-skeletalmesh.json"), 10),
            Write(Path.Combine(directory, @"m01-0123abcd\04-native-encoding\arkive-skeletalmesh-native-encoding.json"), 10),
        };
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Append(new DirectoryInfo(directory)))
            item.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-2);
        var gamePackages = Path.GetDirectoryName(packagePath)!;
        var before = Readers(env, service, gamePackages, second);

        var exports = new SkeletalMeshEditableWorkspaceStore(Path.Combine(env.Root, "staging", "skeletal-mesh-reconnaissance"));
        var cleanup = new ArkiveStorageCleanup(env.Root, env.Edits, exports, env.Packages, env.Deployment);
        var plan = cleanup.PlanAsync(TimeSpan.FromHours(1)).GetAwaiter().GetResult();
        Assert(plan.Trims.SingleOrDefault(item => item.RevisionId == second.RevisionId) is { Files: var files, Bytes: var bytes } &&
               files == copies.Length && bytes == copies.Length * 1000L &&
               plan.Categories.Any(category => category.Name == ArkiveStorageCleanup.TrimCategory),
            "the clean-up plan did not list the build's step copies with their size");

        // A build that fails its checks is left alone: here its composition record no longer matches the payload.
        var composition = File.ReadAllText(second.CompositionManifestPath!);
        File.WriteAllText(second.CompositionManifestPath!, composition.Replace(second.PayloadSha256, new string('0', 64), StringComparison.OrdinalIgnoreCase));
        var entry = env.Packages.LoadAsync().GetAwaiter().GetResult().Single();
        Assert(env.Packages.TrimRevisionAsync(entry, second, unneededFiles: true).GetAwaiter().GetResult() == 0 && copies.All(File.Exists),
            "a build whose composition no longer verifies was trimmed");
        File.WriteAllText(second.CompositionManifestPath!, composition);

        var result = cleanup.RunAsync(plan).GetAwaiter().GetResult();
        Assert(result.Trimmed.SingleOrDefault(item => item.RevisionId == second.RevisionId)?.Bytes == copies.Length * 1000L,
            "the clean-up did not report the bytes the trim freed");
        Assert(copies.All(path => !File.Exists(path)) && records.All(File.Exists),
            "the trim did not delete exactly the steps' copies");
        Assert(!Directory.Exists(Path.Combine(directory, "m02-0123abcd")), "a step folder the trim emptied was left behind");
        Assert(PackageRevisionTrimmer.MarkerVersion(marker) == PackageRevisionTrimmer.CurrentMarkerVersion,
            "the trimmed build was not marked");
        var after = Readers(env, service, gamePackages, second);
        Assert(after == before, "trimming changed what a reader of the build sees:\n" + before + "\n---\n" + after);
        Assert(env.Packages.TrimRevisionAsync(entry, second, unneededFiles: true).GetAwaiter().GetResult() == 0 &&
               cleanup.PlanAsync(TimeSpan.FromHours(1)).GetAwaiter().GetResult().Trims.Count == 0,
            "a build was trimmed twice");
        Directory.Delete(root, recursive: true);
    }

    /// <summary>What everything that reads a build sees: install state, verification, Mods list, Apply plan, dependencies.</summary>
    private static string Readers(ArkiveTestEnvironment env, ArkiveModApplyService service, string gamePackages,
        GuidedTexturePackageRevision revision)
    {
        var inspection = env.Deployment.InspectForRecoveryAsync(
            new ArkiveModDeploymentRequest(revision.ModBundleManifestPath, env.Backups, env.State)).GetAwaiter().GetResult();
        var verified = UnifiedPackageComposerService.VerifyOutputAsync(revision.CompositionManifestPath!).GetAwaiter().GetResult();
        var context = Context(env, gamePackages);
        var list = ArkiveModParityDumps.ListAsync(context, service, env.Root).GetAwaiter().GetResult();
        var plan = ArkiveModParityDumps.PlanAsync(service, env.Library, env.Root).GetAwaiter().GetResult();
        var graph = new ModConflictDependencyService().AnalyzeAsync(env.Packages.LoadAsync().GetAwaiter().GetResult())
            .GetAwaiter().GetResult();
        return string.Join("\n",
            $"inspection {inspection.State} {inspection.CanRestore} {inspection.CurrentTargetSha256} {inspection.HasActiveDeployment}",
            $"verified {verified.OutputUpkPath} {verified.Manifest.FinalOutputUpkSha256}",
            graph.Summary,
            string.Join(";", graph.Conflicts.Select(conflict => $"{conflict.Code} {conflict.Target}")),
            string.Join(";", graph.Dependencies.Select(dependency => $"{dependency.Relation} {dependency.TargetObject} {dependency.Resolution}")),
            list, plan);
    }

    private static void TestUnusedPackageBuilds()
    {
        var root = Path.Combine(fixtureDirectory, "more-storage-unused");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var (env, service, packagePath, first, second) = StorageCleanupSmokeTests.TwoBuilds(root);
        var gamePackages = Path.GetDirectoryName(packagePath)!;
        var exports = new SkeletalMeshEditableWorkspaceStore(Path.Combine(env.Root, "staging", "skeletal-mesh-reconnaissance"));
        var cleanup = new ArkiveStorageCleanup(env.Root, env.Edits, exports, env.Packages, env.Deployment, env.Library);
        var package = env.Packages.LoadAsync().GetAwaiter().GetResult().Single();

        // An enabled mod uses the package: only the superseded build may go.
        var plan = cleanup.PlanAsync(TimeSpan.Zero).GetAwaiter().GetResult();
        Assert(plan.Builds.Items.All(item => !item.Unused && item.RevisionId != second.RevisionId),
            "the latest build of a package an enabled mod uses was planned for removal");

        // Turned off and applied: the original package is back, and nothing uses it.
        var library = env.Library.LoadAsync().GetAwaiter().GetResult();
        foreach (var mod in library.Mods) env.Library.SetEnabledAsync(mod.ModId, false).GetAwaiter().GetResult();
        ArkiveModSelectionService.SynchronizeAsync(env.Library, env.Edits).GetAwaiter().GetResult();
        var restore = service.ApplyAsync(service.PlanAsync().GetAwaiter().GetResult().Fingerprint).GetAwaiter().GetResult();
        Assert(restore.Succeeded, "turning the mods off did not restore the game file");
        Assert(Directory.Exists(Path.Combine(env.Root, "staging", "mod-sets", "_quick-actions")),
            "the fixture has no quick-action mod set naming the package");

        // Builds made within the minimum age stay.
        Assert(cleanup.PlanAsync(TimeSpan.FromHours(1)).GetAwaiter().GetResult().Builds.Items.All(item => !item.Unused),
            "builds made in the last hour were planned for removal");
        // A mod set the user made keeps the package's builds.
        var set = Path.Combine(env.Root, "staging", "mod-sets", Guid.NewGuid().ToString("N"), "arkive-mod-set.json");
        Directory.CreateDirectory(Path.GetDirectoryName(set)!);
        File.WriteAllText(set, JsonSerializer.Serialize(new { members = new[] { new { packageProjectId = package.Manifest.PackageProjectId } } }));
        Assert(cleanup.PlanAsync(TimeSpan.Zero).GetAwaiter().GetResult().Builds.Items.All(item => !item.Unused),
            "a package a mod set names lost its builds");
        Directory.Delete(Path.GetDirectoryName(set)!, recursive: true);
        // A working file still checked for a build (a draft's) keeps them.
        var snapshot = ArkiveModEditSnapshot.LoadAsync(env.Edits).GetAwaiter().GetResult();
        var edit = snapshot.Edits.First();
        ArkiveModSelectionService.SetIncludedAsync(env.Edits, edit, true).GetAwaiter().GetResult();
        Assert(cleanup.PlanAsync(TimeSpan.Zero).GetAwaiter().GetResult().Builds.Items.All(item => !item.Unused),
            "a package a checked working file targets lost its builds");
        ArkiveModSelectionService.SetIncludedAsync(env.Edits, edit, false).GetAwaiter().GetResult();

        var listBefore = ArkiveModParityDumps.ListAsync(Context(env, gamePackages), service, env.Root).GetAwaiter().GetResult();
        var planBefore = ArkiveModParityDumps.PlanAsync(service, env.Library, env.Root).GetAwaiter().GetResult();
        var receipts = Directory.EnumerateFiles(env.State, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllText, StringComparer.OrdinalIgnoreCase);
        var backups = Directory.EnumerateFiles(env.Backups, "*", SearchOption.AllDirectories).Count();
        plan = cleanup.PlanAsync(TimeSpan.Zero).GetAwaiter().GetResult();
        Assert(plan.Builds.Items.Count == 2 && plan.Builds.Items.All(item => item.Unused) &&
               plan.Builds.Items.Any(item => item.RevisionId == second.RevisionId && item.Reason.StartsWith("the latest build", StringComparison.Ordinal)) &&
               plan.Categories.Any(category => category.Name == ArkiveStorageCleanup.UnusedBuildsCategory),
            "the builds of a package nothing uses were not planned, latest included: " +
            string.Join(" | ", plan.Builds.Kept.Select(item => item.Reason)));
        var report = cleanup.WriteReport(plan, "smoke test", dryRun: false);
        Assert(File.ReadAllText(report).Contains(ArkiveStorageCleanup.UnusedBuildsCategory, StringComparison.Ordinal),
            "the report does not list the unused package's builds");
        var result = cleanup.RunAsync(plan).GetAwaiter().GetResult();
        var after = env.Packages.LoadManifestAsync(package.ManifestPath).GetAwaiter().GetResult();
        Assert(result.Builds.Removed.Count == 2 && after.Manifest.Revisions.Count == 0 && after.Manifest.LatestRevisionId is null &&
               !Directory.Exists(Path.GetDirectoryName(first.CompositionManifestPath)) &&
               !Directory.Exists(Path.GetDirectoryName(second.CompositionManifestPath)),
            "the package project was not left without builds, as a new one is");
        Assert(Directory.EnumerateFiles(env.State, "*", SearchOption.AllDirectories)
                   .All(path => receipts.TryGetValue(path, out var text) && text == File.ReadAllText(path)) &&
               Directory.EnumerateFiles(env.State, "*", SearchOption.AllDirectories).Count() == receipts.Count &&
               Directory.EnumerateFiles(env.Backups, "*", SearchOption.AllDirectories).Count() == backups,
            "removing the builds touched receipts or backups");
        Assert(ArkiveModParityDumps.ListAsync(Context(env, gamePackages), service, env.Root).GetAwaiter().GetResult() == listBefore &&
               ArkiveModParityDumps.PlanAsync(service, env.Library, env.Root).GetAwaiter().GetResult() == planBefore,
            "removing an unused package's builds changed the Mods list or the Apply plan");
        var graph = new ModConflictDependencyService().AnalyzeAsync(env.Packages.LoadAsync().GetAwaiter().GetResult()).GetAwaiter().GetResult();
        Assert(graph.Conflicts.All(conflict => conflict.Code == "MCV003"), "the dependency view does not cope with a package without builds");
        Assert(cleanup.PlanAsync(TimeSpan.Zero).GetAwaiter().GetResult().Builds.Items.Count == 0, "a second plan found more builds");

        // Turned on again, the mod is built and installed again.
        var matte = env.Library.LoadAsync().GetAwaiter().GetResult().Mods.Single(mod => mod.Manifest.Name == "Matte face");
        env.Library.SetEnabledAsync(matte.ModId, true).GetAwaiter().GetResult();
        var again = service.ApplyAsync(service.PlanAsync().GetAwaiter().GetResult().Fingerprint).GetAwaiter().GetResult();
        Assert(again.Succeeded && ReadMaterial(packagePath).ScalarParameters.Single().Value == 0.25f,
            "the mod was not built and installed again after its package lost its builds");
        Directory.Delete(root, recursive: true);
    }

    private static void TestReleaseWorkingFiles()
    {
        var root = Path.Combine(fixtureDirectory, "more-storage-release");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var packagePath = CreateMaterialClient(root, out var cleanHash);
        var gamePackages = Path.GetDirectoryName(packagePath)!;
        var modFile = AuthorMaterialMod(root, packagePath, "Glossy face", roughness: 0.75f, blue: null);
        var env = CreateEnvironment(Path.Combine(root, "player"));
        var modId = env.Library.ImportAsync(modFile).GetAwaiter().GetResult().Entry.ModId;
        env.Library.SetEnabledAsync(modId, true).GetAwaiter().GetResult();
        var service = CreateApplyService(env, gamePackages);
        Assert(service.ApplyAsync(service.PlanAsync().GetAwaiter().GetResult().Fingerprint).GetAwaiter().GetResult().Succeeded,
            "the mod did not install");
        var installed = File.ReadAllBytes(packagePath);
        var context = Context(env, gamePackages);
        var drafts = new ArkiveModAuthoringWorkspace(Path.Combine(env.Library.StateDirectory, "authoring"));
        var author = new ArkiveModAuthoringService(env.Library, env.Edits)
        {
            Drafts = drafts,
            MeshExports = new SkeletalMeshEditableWorkspaceStore(Path.Combine(env.Root, "staging", "skeletal-mesh-reconnaissance")),
        };
        var plan = author.PlanReleaseAsync(modId, context).GetAwaiter().GetResult();
        Assert(plan.Problems.Any(problem => problem.Contains("turned on", StringComparison.Ordinal)) &&
               plan.Problems.Any(problem => problem.Contains("in the game", StringComparison.Ordinal)),
            "a mod that is on and in the game could be released: " + string.Join(" ", plan.Problems));

        env.Library.SetEnabledAsync(modId, false).GetAwaiter().GetResult();
        Assert(service.ApplyAsync(service.PlanAsync().GetAwaiter().GetResult().Fingerprint).GetAwaiter().GetResult().Succeeded &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))) == cleanHash,
            "turning the mod off did not restore the game file");
        var mod = env.Library.LoadAsync().GetAwaiter().GetResult().Find(modId)!;
        var binding = mod.Bindings.Single();
        var local = ArkiveModEditSnapshot.LoadAsync(env.Edits).GetAwaiter().GetResult().Find(binding.EditId)!;
        var folder = Path.GetDirectoryName(local.ManifestPath)!;
        var modFileHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(env.Library.GetModFilePath(mod))));

        // Refused, with nothing deleted: a change not saved to the mod, another draft using the file, an unsaved draft.
        var document = ((MaterialInstanceEditEntry)local.Entry).DocumentPath;
        var saved = File.ReadAllText(document);
        var material = env.Edits.MaterialEdits.LoadAsync().GetAwaiter().GetResult().Single(item => item.Document.EditId == binding.EditId);
        env.Edits.MaterialEdits.SaveAsync(material with
        {
            Document = material.Document with { Scalars = material.Document.Scalars.Select(item => item with { Value = 0.5f }).ToArray() },
        }).GetAwaiter().GetResult();
        Refused("unsaved changes to a working file", "not saved");
        File.WriteAllText(document, saved);
        var other = drafts.SaveAsync(drafts.CreateAsync(new ArkiveModMetadata("Next idea", "1.0.0")).GetAwaiter().GetResult() with
        { EditIds = [binding.EditId] }).GetAwaiter().GetResult();
        Refused("a draft using its working file", "draft uses");
        drafts.DeleteAsync(other.DraftId).GetAwaiter().GetResult();
        var own = drafts.BeginEditAsync(mod).GetAwaiter().GetResult();
        drafts.SaveAsync(own with { RemovedAssetIds = [binding.AssetId] }).GetAwaiter().GetResult();
        Refused("an unsaved draft of the mod", "open for editing");
        drafts.SaveAsync(own).GetAwaiter().GetResult();

        plan = author.PlanReleaseAsync(modId, context).GetAwaiter().GetResult();
        Assert(plan.Problems.Count == 0 && plan.EditIds.SequenceEqual([binding.EditId]) && plan.Bytes > 0 &&
               plan.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase),
            "the release plan is wrong: " + string.Join(" ", plan.Problems));
        var release = author.ReleaseWorkingFilesAsync(modId, context).GetAwaiter().GetResult();
        mod = env.Library.LoadAsync().GetAwaiter().GetResult().Find(modId)!;
        Assert(release.DeletedEditIds.SequenceEqual([binding.EditId]) && !Directory.Exists(folder) && mod.Bindings.Count == 0 &&
               release.DeletedDraftIds.SequenceEqual([own.DraftId]) && drafts.LoadAsync().GetAwaiter().GetResult().Count == 0,
            "the working file, binding or unchanged draft was not released");
        Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(env.Library.GetModFilePath(mod)))) == modFileHash &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))) == cleanHash,
            "releasing working files changed the .arkive file or the game");
        var statuses = ArkiveModApplyService.DescribeAsync(env.Library, env.Library.LoadAsync().GetAwaiter().GetResult(),
            ArkiveModEditSnapshot.LoadAsync(env.Edits).GetAwaiter().GetResult()).GetAwaiter().GetResult();
        Assert(statuses.Single().Assets.All(asset => asset.Setup == ArkiveModSetupState.NotSetUp),
            "a released mod's assets do not show as not set up");
        Assert(author.PlanReleaseAsync(modId, context).GetAwaiter().GetResult().Problems.Count > 0,
            "a mod with nothing left to release could be released again");

        // Set up again the normal way: the same content, and the same game file once applied.
        service.PrepareModForEditingAsync(modId).GetAwaiter().GetResult();
        mod = env.Library.LoadAsync().GetAwaiter().GetResult().Find(modId)!;
        var rebound = mod.Bindings.Single();
        var again = ArkiveModEditSnapshot.LoadAsync(env.Edits).GetAwaiter().GetResult().Find(rebound.EditId)!;
        Assert(rebound.PackedContentSha256 == binding.PackedContentSha256 && rebound.AssetId == binding.AssetId &&
               !ArkiveModAuthoringService.IsModifiedAsync(again, rebound).GetAwaiter().GetResult(),
            "the working file set up again does not hold what the released one held");
        env.Library.SetEnabledAsync(modId, true).GetAwaiter().GetResult();
        var applyPlan = service.PlanAsync().GetAwaiter().GetResult();
        Assert(applyPlan.CanApply && applyPlan.Packages.Single().Contributors.Single().ModId == modId,
            "the Apply plan after setting the mod up again is not the mod's");
        Assert(service.ApplyAsync(applyPlan.Fingerprint).GetAwaiter().GetResult().Succeeded &&
               File.ReadAllBytes(packagePath).SequenceEqual(installed),
            "applying the mod set up again did not give the game file it gave before the release");
        Directory.Delete(root, recursive: true);

        void Refused(string what, string reason)
        {
            var refused = author.PlanReleaseAsync(modId, context).GetAwaiter().GetResult();
            Assert(refused.Problems.Any(problem => problem.Contains(reason, StringComparison.Ordinal)),
                $"{what} did not stop the release: " + string.Join(" ", refused.Problems));
            try
            {
                author.ReleaseWorkingFilesAsync(modId, context).GetAwaiter().GetResult();
                Assert(false, $"{what} did not stop the release");
            }
            catch (ArkiveModRefusalException)
            {
            }
            Assert(Directory.Exists(folder) && env.Library.LoadAsync().GetAwaiter().GetResult().Find(modId)!.Bindings.Count == 1,
                $"a refused release ({what}) deleted something");
        }
    }

    private static void TestCaches()
    {
        var root = Path.Combine(fixtureDirectory, "more-storage-caches");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var cache = Path.Combine(root, "cache");
        string[] old =
        [
            Entry(@"model-viewer\pc_scene-0123", true), Entry(@"model-viewer\animations\0123abcd", true),
            Entry(@"particle-textures\fx_material", true), Entry(@"particle-textures\meshes\fx_mesh", true),
            Entry(@"pieces\0123abcd", true), Entry(@"outfit-swap\pieces\0123abcd", true), Entry(@"outfit-swap\fields\v3-1-a--b.bin", true),
            Entry(@"outfit-swap\work\0123", true), Entry(@"sounds\preview\1-abcd.wav", true), Entry(@"body-proportions\0123abcd", true),
            Entry(@"zone-maps\0123abcd", true),
        ];
        string[] recent =
        [
            Entry(@"model-viewer\pc_scene-4567", false), Entry(@"pieces\4567abcd", false), Entry(@"zone-maps\4567abcd", false),
        ];
        string[] untouched =
        [
            Entry(@"sounds\event-names.tsv.gz", true), Entry(@"game-data\tables", true), Entry(@"library-index\index", true),
            Entry("arkive-settings.json", true), Entry("file-hashes.json", true),
        ];
        // An entry used just now is kept, however old its files.
        var reused = Entry(@"body-proportions\4567abcd", true);
        ArkiveCacheCleanup.MarkUsed(reused);

        var cleanup = new ArkiveCacheCleanup(root);
        var plan = cleanup.Plan();
        var planned = plan.Items.Select(item => item.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert(planned.SequenceEqual(old.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            "the cache clean-up did not pick exactly the entries unused for 30 days: " + string.Join(", ", planned.Select(path => Path.GetRelativePath(cache, path))));
        Assert(plan.Sizes.Count == ArkiveCacheCleanup.Caches.Count && plan.Sizes.Sum(size => size.OldEntries) == old.Length,
            "the cache sizes were not reported");
        // Used between the plan and the clean-up: kept.
        ArkiveCacheCleanup.MarkUsed(old[0]);
        var removed = cleanup.Remove(plan);
        Assert(removed.Count == old.Length - 1 && Directory.Exists(old[0]) && old.Skip(1).All(path => !Path.Exists(path)),
            "the cache clean-up did not remove exactly the entries still unused");
        Assert(recent.Concat(untouched).Append(reused).All(Path.Exists), "the cache clean-up removed a recent entry or a file that is no cache");
        Directory.Delete(root, recursive: true);

        string Entry(string relative, bool isOld)
        {
            var path = Path.GetFullPath(Path.Combine(cache, relative));
            var time = isOld ? LongAgo : DateTime.UtcNow;
            if (Path.HasExtension(relative))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[100]);
                File.SetLastWriteTimeUtc(path, time);
                return path;
            }
            Directory.CreateDirectory(Path.Combine(path, "inner"));
            var file = Path.Combine(path, "inner", "piece.glb");
            File.WriteAllBytes(file, new byte[100]);
            File.SetLastWriteTimeUtc(file, time);
            Directory.SetLastWriteTimeUtc(Path.Combine(path, "inner"), time);
            Directory.SetLastWriteTimeUtc(path, time);
            return path;
        }
    }

    private static void TestUnpackedAndCandidates()
    {
        var root = Path.Combine(fixtureDirectory, "more-storage-unpacked");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var env = CreateEnvironment(root);
        var exports = new SkeletalMeshEditableWorkspaceStore(Path.Combine(root, "staging", "skeletal-mesh-reconnaissance"));
        var materialized = Path.Combine(root, "staging", "mod-library", "materialized");
        var named = Folder(Path.Combine(materialized, "0123abcd", "authorin-export-1"));
        var orphan = Folder(Path.Combine(materialized, "4567abcd", "texture2d-1"));
        var namedFromOldPlace = Folder(Path.Combine(materialized, "89abcdef", "texture2d-2"));
        var recent = Folder(Path.Combine(materialized, "89abcdef", "texture-validate-3"));
        // A texture project (kept: checked for builds) names its native texture there; another record names a folder by
        // where the data folder used to be.
        Write(Path.Combine(root, "staging", "texture-mods", "a", "kept-0123abcd", "arkive-texture-mod.json"), new
        {
            schemaVersion = 1, projectId = new string('a', 32), createdAt = DateTimeOffset.UtcNow.AddDays(-3), updatedAt = DateTimeOffset.UtcNow.AddDays(-3),
            displayName = "kept", packageAlias = "a", physicalPackageName = "a.upk", sourcePackagePath = Path.Combine(root, "a.upk"),
            className = "Texture2D", objectName = "kept", exportIndex = 1, pixelFormat = "PF_DXT5", width = 4, height = 4,
            nativeTextureManifestPath = Path.Combine(named, "native", "arkive-native-texture.json"),
            editableImagePath = Path.Combine(root, "missing.png"), state = "Editing", latestRevisionId = (string?)null,
            revisions = Array.Empty<object>(), includedInPackageBuild = true,
        });
        Write(Path.Combine(root, "staging", "client-update-audits", "audit.json"), new
        {
            uses = @"D:\Old\Arkive\staging\mod-library\materialized\89abcdef\texture2d-2\native\arkive-native-texture.json",
        });

        var client = CreateMaterialClient(root, out _);
        var kept = CreateMaterialEdit(env, client, "Kept gloss", 0.4f, null, candidate: true);
        var keptCandidate = Path.GetDirectoryName(kept.Document.LatestCandidateManifestPath)!;
        // An earlier candidate of the same edit, and one whose edit was deleted.
        var earlier = Path.Combine(env.MaterialCandidates, "material_instance", "000001-old-11111111");
        CopyFolder(keptCandidate, earlier);
        var gone = Path.Combine(env.MaterialCandidates, "material_instance", "000001-gone-22222222");
        CopyFolder(keptCandidate, gone);
        var goneRecord = Path.Combine(gone, "arkive-material-candidate.json");
        File.WriteAllText(goneRecord, File.ReadAllText(goneRecord).Replace(Path.GetFileName(Path.GetDirectoryName(kept.DocumentPath)!),
            "deleted-edit-33333333", StringComparison.Ordinal));
        env.Edits.MaterialEdits.SetIncludedAsync(kept, true).GetAwaiter().GetResult();

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Append(root))
        {
            foreach (var file in Directory.EnumerateFiles(directory)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-2));
        }
        File.SetLastWriteTimeUtc(Path.Combine(recent, "native", "arkive-native-texture.json"), DateTime.UtcNow);

        var cleanup = new ArkiveWorkingFileCleanup(root, env.Edits, exports);
        var plan = cleanup.PlanAsync(TimeSpan.FromHours(1)).GetAwaiter().GetResult();
        var planned = plan.Items.Where(item => item.Kind is ArkiveWorkingFileKind.UnpackedModFiles or ArkiveWorkingFileKind.MaterialCandidate)
            .Select(item => item.DirectoryPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] expected = [orphan, Path.GetFullPath(gone)];
        Assert(planned.SequenceEqual(expected.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            "the clean-up did not pick exactly the unpacked files and candidates nothing needs: " + string.Join(", ", planned) +
            "; kept: " + string.Join(" | ", plan.Kept.Where(item => item.Kind is ArkiveWorkingFileKind.UnpackedModFiles or ArkiveWorkingFileKind.MaterialCandidate)
                .Select(item => $"{Path.GetFileName(item.DirectoryPath)}: {item.Reason}")));
        Assert(plan.Kept.Any(item => item.DirectoryPath.Equals(Path.GetFullPath(earlier), StringComparison.OrdinalIgnoreCase) &&
                                     item.Reason.Contains("material edit", StringComparison.Ordinal)),
            "an earlier candidate of an edit that still exists was not kept for it");
        cleanup.RemoveAsync(plan, TimeSpan.FromHours(1)).GetAwaiter().GetResult();
        Assert(!Directory.Exists(orphan) && !Directory.Exists(gone) && !Directory.Exists(Path.Combine(materialized, "4567abcd")) &&
               new[] { named, namedFromOldPlace, recent, keptCandidate, earlier, Path.GetDirectoryName(kept.DocumentPath)! }.All(Directory.Exists),
            "the clean-up removed the wrong unpacked files or candidates");
        Directory.Delete(root, recursive: true);

        static string Folder(string path)
        {
            Directory.CreateDirectory(Path.Combine(path, "native"));
            File.WriteAllText(Path.Combine(path, "native", "arkive-native-texture.json"), "{}");
            File.WriteAllBytes(Path.Combine(path, "native", "texture.bin"), new byte[64]);
            return Path.GetFullPath(path);
        }

        static void CopyFolder(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }

    private static string Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        return path;
    }

    private static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }
}
