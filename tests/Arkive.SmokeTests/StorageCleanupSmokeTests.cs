using static Arkive.SmokeTests.ArkiveModTestFixture;

namespace Arkive.SmokeTests;

internal static class StorageCleanupSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("cleans up unused working files of every kind, keeping selected, named, recent and hand-edited ones", TestEveryKind),
        new("removes superseded package builds once a newer build is in the game, and keeps installed, unreadable and unchecked ones", TestSupersededBuilds),
        new("keeps the clean lineage roots Arkive describes a package by, now and after Restore", TestLineageRoots),
        new("removes the superseded builds of the packages an Apply rebuilt, after the Apply", TestAfterApply),
        new("deleting a mod deletes its working files and draft, keeping ones another mod or draft names", TestDeleteMod),
    ];

    private static readonly DateTimeOffset LongAgo = DateTimeOffset.UtcNow.AddDays(-3);

    private static void TestEveryKind()
    {
        var root = Path.Combine(fixtureDirectory, "storage-cleanup-kinds");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var env = CreateEnvironment(root);
        var exports = new SkeletalMeshEditableWorkspaceStore(Path.Combine(root, "staging", "skeletal-mesh-reconnaissance"));
        var staging = Path.Combine(root, "staging");

        var unusedEffect = Effect("unused-effect", included: false);
        var selectedEffect = Effect("selected-effect", included: true);
        var boundEffect = Effect("bound-effect", included: false);
        var recentEffect = Effect("recent-effect", included: false);
        var namedByBroken = Effect("named-by-broken", included: false);
        var unusedPhysics = Physics("unused-physics");
        // Named only from inside an unused effect's folder: goes with it.
        var chainedPhysics = Physics("chained-physics");
        // Named from inside a selected effect's folder: kept with it.
        var keptByEffect = Physics("kept-by-effect");
        var unusedMovie = Movie("unused-movie");
        var draftedMovie = Movie("drafted-movie");
        var unusedStatic = StaticMesh("unused-static");
        var unusedTexture = Texture("unused-texture", included: false, out _);
        var handEdited = Texture("hand-edited", included: false, out var handEditedImage);
        var defaultTexture = Texture("default-texture", included: null, out _);
        var client = CreateMaterialClient(root, out _);
        var material = CreateMaterialEdit(env, client, "Unused gloss", 0.5f, null, candidate: false);
        material = env.Edits.MaterialEdits.SetIncludedAsync(material, false).GetAwaiter().GetResult();
        // Its candidate (the export built from it) names it, but does not use it.
        var built = CreateMaterialEdit(env, client, "Built gloss", 0.6f, null, candidate: true);
        built = env.Edits.MaterialEdits.SetIncludedAsync(built, false).GetAwaiter().GetResult();
        Assert(Directory.EnumerateFiles(env.MaterialCandidates, "*.json", SearchOption.AllDirectories).Any(),
            "the fixture material edit has no candidate");
        foreach (var document in new[] { material.DocumentPath, built.DocumentPath })
        {
            // Made days ago, as the rest of the fixture.
            var node = JsonNode.Parse(File.ReadAllText(document))!.AsObject();
            var created = node.Select(property => property.Key).Single(key => key.Equals("createdAt", StringComparison.OrdinalIgnoreCase));
            node[created] = LongAgo;
            File.WriteAllText(document, node.ToJsonString());
        }

        Write(Path.Combine(unusedEffect, "notes.json"), new { uses = Id("chained-physics") });
        Write(Path.Combine(selectedEffect, "notes.json"), new { uses = Path.Combine(keptByEffect, "arkive-physics-edit.json") });
        Write(Path.Combine(staging, "mod-library", "arkive-mod-library-extra.json"), new { bindings = new[] { new { editId = Id("bound-effect") } } });
        Write(Path.Combine(staging, "mod-library", "authoring", "draft-like.json"), new { editIds = new[] { Id("drafted-movie") } });
        Directory.CreateDirectory(Path.Combine(staging, "mod-sets"));
        File.WriteAllText(Path.Combine(staging, "mod-sets", "broken.json"), "{ \"oops\": \"" + Id("named-by-broken") + "\", ");

        foreach (var folder in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Append(root))
            Age(folder, TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(handEditedImage, DateTime.UtcNow.AddDays(-1));
        foreach (var file in Directory.EnumerateFiles(recentEffect)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow);

        var cleanup = new ArkiveWorkingFileCleanup(root, env.Edits, exports);
        var plan = cleanup.PlanAsync(TimeSpan.FromHours(1)).GetAwaiter().GetResult();
        var planned = plan.Items.Select(item => item.DirectoryPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        // The unused built edit's candidate goes with it.
        var builtCandidate = Path.GetFullPath(Path.GetDirectoryName(built.Document.LatestCandidateManifestPath)!);
        string[] expected =
        [
            unusedEffect, unusedPhysics, chainedPhysics, unusedMovie, unusedStatic, unusedTexture,
            Path.GetFullPath(material.DirectoryPath), Path.GetFullPath(built.DirectoryPath), builtCandidate,
        ];
        Assert(planned.SequenceEqual(expected.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            "the clean-up did not pick exactly the unused working files of each kind: " + string.Join(", ", planned.Select(Path.GetFileName)) +
            "; kept: " + string.Join(" | ", plan.Kept.Where(item => expected.Contains(item.DirectoryPath, StringComparer.OrdinalIgnoreCase))
                .Select(item => $"{Path.GetFileName(item.DirectoryPath)}: {item.Reason}")));
        Assert(plan.Items.Select(item => item.Kind).Distinct().Count() == 7, "the plan did not name each item's kind");
        string[] kept = [selectedEffect, boundEffect, recentEffect, namedByBroken, keptByEffect, draftedMovie, handEdited, defaultTexture];
        foreach (var folder in kept)
            Assert(plan.Kept.Any(item => item.DirectoryPath.Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase) &&
                                         item.Reason.Length > 0),
                $"{Path.GetFileName(folder)} was not listed as kept with a reason");
        Assert(plan.Kept.Single(item => item.DirectoryPath.Equals(Path.GetFullPath(handEdited), StringComparison.OrdinalIgnoreCase))
                .Reason.Contains("editable image", StringComparison.Ordinal),
            "the hand-edited texture project was not kept for its changed image");

        // Something that starts using a planned item after the plan keeps it.
        Write(Path.Combine(staging, "mod-library", "authoring", "later.json"), new { editIds = new[] { Id("unused-movie") } });
        var removal = cleanup.RemoveAsync(plan, TimeSpan.FromHours(1)).GetAwaiter().GetResult();
        Assert(Directory.Exists(unusedMovie) && removal.NotRemoved.Any(item => item.DirectoryPath == Path.GetFullPath(unusedMovie)),
            "an item that came into use after the plan was deleted");
        foreach (var folder in expected.Where(folder => folder != unusedMovie))
            Assert(!Directory.Exists(folder), $"{Path.GetFileName(folder)} was not deleted");
        foreach (var folder in kept)
            Assert(Directory.Exists(folder), $"{Path.GetFileName(folder)} was deleted although it is in use, recent or edited by hand");
        Assert(File.Exists(client), "the game package fixture was touched");
        Directory.Delete(root, recursive: true);

        string Effect(string name, bool included) => Document(Path.Combine(staging, "particle-edits", Id(name)),
            "arkive-particle-edit.json", new
            {
                schemaVersion = 1, editId = Id(name), displayName = name, sourcePackagePath = Path.Combine(root, "a.upk"),
                physicalPackageName = "a.upk", packageAlias = "a", sourcePackageSha256 = "00", systemObjectName = name,
                systemObjectPath = name, systemExportIndex = 1, systemExportSha256 = "00", patches = Array.Empty<object>(),
                includedInPackageBuild = included, createdAt = LongAgo, updatedAt = LongAgo,
            });

        string Physics(string name) => Document(Path.Combine(staging, "physics-edits", Id(name)),
            "arkive-physics-edit.json", new
            {
                schemaVersion = 1, editId = Id(name), displayName = name, sourcePackagePath = Path.Combine(root, "a.upk"),
                physicalPackageName = "a.upk", packageAlias = "a", sourcePackageSha256 = "00", assetObjectName = name,
                assetObjectPath = name, assetExportIndex = 1, assetExportSha256 = "00", patches = Array.Empty<object>(),
                includedInPackageBuild = false, createdAt = LongAgo, updatedAt = LongAgo,
            });

        string Movie(string name) => Document(Path.Combine(staging, "interface-movie-edits", Id(name)),
            "arkive-interface-movie-edit.json", new
            {
                schemaVersion = 1, editId = Id(name), displayName = name, createdAt = LongAgo, packageAlias = "a",
                physicalPackageName = "a.upk", sourcePackagePath = Path.Combine(root, "a.upk"), sourcePackageSha256 = "00",
                nativeExportIndex = 1, className = "EFSwfMovie", objectName = name, serialOffset = 0, serialSize = 0,
                sourceExportSha256 = "00", originalMovieSha256 = "00", originalMoviePath = "original.gfx",
                editedMoviePath = "edited.gfx", includedInPackageBuild = false,
            });

        string StaticMesh(string name) => Document(Path.Combine(staging, "static-mesh-edits", $"a-{name}-{Id(name)[..8]}"),
            "arkive-staticmesh-edit.json", new
            {
                schemaVersion = 1, editId = Id(name), createdAt = LongAgo, displayName = name,
                sourceEditableManifestPath = Path.Combine(root, "missing.json"), editedGlbPath = "edited.glb",
                editedGlbSha256 = "00", includedInPackageBuild = false,
            });

        string Texture(string name, bool? included, out string image)
        {
            var folder = Path.Combine(staging, "texture-mods", "a", $"{name}-{Id(name)[..8]}");
            image = Path.Combine(folder, "editable", name + "-edit.png");
            Directory.CreateDirectory(Path.GetDirectoryName(image)!);
            File.WriteAllBytes(image, [1, 2, 3]);
            return Document(folder, "arkive-texture-mod.json", new
            {
                schemaVersion = 1, projectId = Id(name), createdAt = LongAgo, updatedAt = LongAgo, displayName = name,
                packageAlias = "a", physicalPackageName = "a.upk", sourcePackagePath = Path.Combine(root, "a.upk"),
                className = "Texture2D", objectName = name, exportIndex = 1, pixelFormat = "PF_DXT5", width = 4, height = 4,
                nativeTextureManifestPath = Path.Combine(root, "native.json"), editableImagePath = image, state = "Editing",
                latestRevisionId = (string?)null, revisions = Array.Empty<object>(), includedInPackageBuild = included,
            });
        }

        static string Document(string folder, string fileName, object value)
        {
            Write(Path.Combine(folder, fileName), value);
            return Path.GetFullPath(folder);
        }
    }

    private static void TestSupersededBuilds()
    {
        var root = Path.Combine(fixtureDirectory, "storage-cleanup-builds");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var (env, service, packagePath, first, second) = TwoBuilds(root);
        var builds = Builds(env, root);
        var manifestPath = env.Packages.LoadAsync().GetAwaiter().GetResult().Single().ManifestPath;

        // The newer build is in the game, so the first is a conflict to inspection, but is not in the game.
        var plan = builds.PlanAsync(fullInspection: false).GetAwaiter().GetResult();
        Assert(plan.Items is [{ RevisionId: var planned }] && planned == first.RevisionId &&
               plan.Kept.Single().RevisionId == second.RevisionId && plan.Kept.Single().Reason == "the latest build",
            "the superseded build was not planned, or the installed latest was not kept: " +
            string.Join(" | ", plan.Kept.Select(item => item.Reason)));

        var firstDirectory = Path.GetDirectoryName(first.ModBundleManifestPath)!;
        var bundle = ArkiveModBundleService.LoadBundleAsync(first.ModBundleManifestPath).GetAwaiter().GetResult();
        var receipt = Path.Combine(env.State, bundle.ModId, ArkiveModDeploymentService.ReceiptFileName);
        Assert(File.Exists(receipt), "the fixture's first build has no receipt");

        // An unreadable receipt keeps the build.
        var receiptText = File.ReadAllText(receipt);
        File.WriteAllText(receipt, "{ broken");
        Assert(KeptReason(builds, first)?.Contains("could not be checked", StringComparison.Ordinal) == true,
            "a build whose receipt cannot be read was not kept");
        File.WriteAllText(receipt, receiptText);

        // An inspection that cannot finish (the payload is gone) keeps the build.
        var payload = Path.Combine(firstDirectory, bundle.PayloadRelativePath);
        File.Move(payload, payload + ".away");
        Assert(KeptReason(builds, first)?.Contains("could not be checked", StringComparison.Ordinal) == true,
            "a build whose inspection failed was not kept");
        File.Move(payload + ".away", payload);

        // An unreadable bundle keeps the build.
        var bundleText = File.ReadAllText(first.ModBundleManifestPath);
        File.WriteAllText(first.ModBundleManifestPath, "{ broken");
        Assert(KeptReason(builds, first)?.Contains("bundle cannot be read", StringComparison.Ordinal) == true,
            "a build whose bundle cannot be read was not kept");
        File.WriteAllText(first.ModBundleManifestPath, bundleText);

        // A build whose receipt says it is installed is kept, even while the game holds the newer build.
        var parsed = ArkiveModDeploymentService.LoadReceiptAsync(receipt).GetAwaiter().GetResult();
        File.WriteAllText(receipt, JsonSerializer.Serialize(parsed with { State = ModInstallationState.Installed }, ReceiptJson));
        Assert(KeptReason(builds, first) is { } installedReason && installedReason.Contains("Conflict", StringComparison.Ordinal),
            "a build whose receipt says it is installed was not kept");
        File.WriteAllText(receipt, receiptText);

        // A build the game holds while a newer one is the latest is kept: it is the one Restore would undo.
        var game = File.ReadAllBytes(packagePath);
        File.Copy(payload, packagePath, overwrite: true);
        File.WriteAllText(receipt, JsonSerializer.Serialize(parsed with { State = ModInstallationState.Installed, GameFilesModified = true }, ReceiptJson));
        Assert(KeptReason(builds, first) is { } activeReason &&
               (activeReason.Contains("Installed", StringComparison.Ordinal) || activeReason.Contains("restore data", StringComparison.Ordinal)),
            "an earlier build that is still in the game was not kept");
        File.WriteAllBytes(packagePath, game);
        File.WriteAllText(receipt, receiptText);

        // A build a mod set pins is kept.
        Write(Path.Combine(root, "player", "staging", "mod-sets", "set", "arkive-mod-set.json"), new { pinnedRevisionId = first.RevisionId });
        Assert(KeptReason(builds, first) == "a mod set uses it", "a build a mod set pins was not kept");
        Directory.Delete(Path.Combine(root, "player", "staging", "mod-sets", "set"), recursive: true);

        // The removal checks again with full inspections, then takes the folder and the record entry.
        var before = service.PlanAsync().GetAwaiter().GetResult();
        var result = builds.DeleteAsync(plan).GetAwaiter().GetResult();
        var after = env.Packages.LoadManifestAsync(manifestPath).GetAwaiter().GetResult();
        Assert(result.Removed is [{ RevisionId: var removed }] && removed == first.RevisionId && !Directory.Exists(firstDirectory) &&
               after.Manifest.Revisions is [{ RevisionId: var left }] && left == second.RevisionId,
            "the superseded build's folder and record entry were not removed");
        Assert(File.Exists(receipt) && File.ReadAllText(receipt) == receiptText,
            "the clean-up changed or removed an install receipt");
        Assert(Directory.EnumerateFiles(env.Backups, "*", SearchOption.AllDirectories).Any(),
            "the clean-up removed game backups");
        var again = service.PlanAsync().GetAwaiter().GetResult();
        Assert(again.Fingerprint == before.Fingerprint && !again.HasChanges && again.Summary == before.Summary,
            "removing the superseded build changed the Apply plan");
        Assert(builds.PlanAsync(fullInspection: true).GetAwaiter().GetResult().Items.Count == 0,
            "a second plan still found builds to remove");
        Directory.Delete(root, recursive: true);
    }

    private static void TestLineageRoots()
    {
        static ArkiveSupersededBuildCleanup.LineageBuild Build(string id, string required, string payload) => new(id, required, payload);
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Every build made on the clean game: the latest is its own root, so nothing else is needed.
        IReadOnlyList<ArkiveSupersededBuildCleanup.LineageBuild> simple =
            [Build("latest", "S0", "P3"), Build("b", "S0", "P2"), Build("a", "S0", "P1")];
        Assert(ArkiveSupersededBuildCleanup.LineageRootsToKeep(simple, none, Set("latest"), ["S0", "P3"]).Count == 0,
            "builds were kept as lineage roots although the latest describes the clean game");

        // The latest was built on top of an earlier build: that earlier build is the clean root Arkive reports while the
        // game is clean, and the build Restore leaves the game matching.
        IReadOnlyList<ArkiveSupersededBuildCleanup.LineageBuild> descendant =
            [Build("latest", "P1", "P2"), Build("root", "S0", "P1")];
        var keep = ArkiveSupersededBuildCleanup.LineageRootsToKeep(descendant, none, Set("latest"), ["S0", "P1"]);
        Assert(keep.SequenceEqual(["root"]), "the clean lineage root was not kept: " + string.Join(",", keep));

        // Two clean roots: Arkive reports the newer one, and the older one is what the latest was built on.
        IReadOnlyList<ArkiveSupersededBuildCleanup.LineageBuild> twoRoots =
            [Build("latest", "P1", "P2"), Build("newer", "S0", "P0"), Build("root", "S0", "P1")];
        keep = ArkiveSupersededBuildCleanup.LineageRootsToKeep(twoRoots, none, Set("latest"), ["S0", "P1"]);
        Assert(keep.Contains("newer") && keep.Contains("root"),
            "the root Arkive reports, or the build the latest stands on, was not kept: " + string.Join(",", keep));

        // A chain: removing the middle build would make the latest look like a root of its own.
        IReadOnlyList<ArkiveSupersededBuildCleanup.LineageBuild> chain =
            [Build("latest", "P2", "P3"), Build("middle", "P1", "P2"), Build("root", "S0", "P1")];
        keep = ArkiveSupersededBuildCleanup.LineageRootsToKeep(chain, none, Set("latest"), ["S0", "P2"]);
        Assert(keep.Contains("root") && keep.Contains("middle"),
            "the chain back to the clean root was not kept: " + string.Join(",", keep));

        // A root whose inspection fails is passed over by Arkive, so the next one is the one to keep.
        IReadOnlyList<ArkiveSupersededBuildCleanup.LineageBuild> failing =
            [Build("latest", "S0", "P3"), Build("b", "S0", "P2"), Build("a", "S0", "P1")];
        keep = ArkiveSupersededBuildCleanup.LineageRootsToKeep(failing, Set("latest"), Set("latest"), ["S0"]);
        Assert(keep.SequenceEqual(["b"]), "the root Arkive falls back to was not kept: " + string.Join(",", keep));

        // After a game update the old clean file is no game state, so builds made on it are not roots to keep.
        IReadOnlyList<ArkiveSupersededBuildCleanup.LineageBuild> updated =
            [Build("latest", "S1", "P3"), Build("before-update", "S0", "P1")];
        Assert(ArkiveSupersededBuildCleanup.LineageRootsToKeep(updated, none, Set("latest"), ["S1", "P3"]).Count == 0,
            "a build made on the client before an update was kept as a root of the current game");

        static HashSet<string> Set(params string[] ids) => new(ids, StringComparer.OrdinalIgnoreCase);
    }

    private static void TestAfterApply()
    {
        var root = Path.Combine(fixtureDirectory, "storage-cleanup-after-apply");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var (env, service, packagePath, first, second) = TwoBuilds(root, out var applied);
        var exports = new SkeletalMeshEditableWorkspaceStore(Path.Combine(root, "player", "staging", "skeletal-mesh-reconnaissance"));
        var cleanup = new ArkiveStorageCleanup(env.Root, env.Edits, exports, env.Packages, env.Deployment);
        var statusesBefore = Describe(env);
        var gameBefore = File.ReadAllBytes(packagePath);
        var done = cleanup.AfterApplyAsync(applied).GetAwaiter().GetResult();
        Assert(done is { Result.Removed: [{ RevisionId: var removed }] } && removed == first.RevisionId,
            "the superseded build of the rebuilt package was not removed after Apply");
        Assert(File.Exists(done!.Value.ReportPath) &&
               File.ReadAllText(done.Value.ReportPath).Contains(first.RevisionId, StringComparison.Ordinal) &&
               Path.GetDirectoryName(done.Value.ReportPath)!.Equals(Path.Combine(env.Root, "reports"), StringComparison.OrdinalIgnoreCase),
            "the clean-up after Apply did not list what it removed in reports\\clean-up-*.txt");
        Assert(!Directory.Exists(Path.GetDirectoryName(first.ModBundleManifestPath)) &&
               Directory.Exists(Path.GetDirectoryName(second.ModBundleManifestPath)),
            "the wrong build folder was removed");
        Assert(File.ReadAllBytes(packagePath).SequenceEqual(gameBefore), "the clean-up after Apply changed the game file");
        Assert(Describe(env) == statusesBefore, "the clean-up after Apply changed what the Mods list shows");
        Assert(!service.PlanAsync().GetAwaiter().GetResult().HasChanges, "the clean-up after Apply left changes to apply");
        Assert(cleanup.AfterApplyAsync(applied).GetAwaiter().GetResult() is null, "a second clean-up found more to remove");
        var failed = applied with { Packages = applied.Packages.Select(package => package with { Succeeded = false }).ToArray() };
        Assert(cleanup.AfterApplyAsync(failed).GetAwaiter().GetResult() is null, "a failed Apply was cleaned up after");
        Directory.Delete(root, recursive: true);
    }

    private static void TestDeleteMod()
    {
        var root = Path.Combine(fixtureDirectory, "storage-cleanup-delete-mod");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var env = CreateEnvironment(Path.Combine(root, "player"));
        var client = CreateMaterialClient(root, out _);
        var drafts = new ArkiveModAuthoringWorkspace(Path.Combine(env.Library.StateDirectory, "authoring"));
        var author = new ArkiveModAuthoringService(env.Library, env.Edits) { Drafts = drafts };

        var (own, ownMod) = Authored("Own work");
        var (shared, sharedMod) = Authored("Shared work");
        var (other, otherMod) = Authored("Other work");
        var (drafted, draftedMod) = Authored("Drafted work");
        // Its own draft, as Edit mods leaves it.
        drafts.BeginEditAsync(env.Library.LoadAsync().GetAwaiter().GetResult().Find(ownMod)!).GetAwaiter().GetResult();
        // Another mod's draft uses the working file as a reference (the library lets one binding hold each file).
        var otherDraft = drafts.BeginEditAsync(env.Library.LoadAsync().GetAwaiter().GetResult().Find(otherMod)!).GetAwaiter().GetResult();
        drafts.SaveAsync(otherDraft with { ReferenceEditIds = [shared.Document.EditId] }).GetAwaiter().GetResult();
        // A draft of a new mod that uses another mod's working file.
        drafts.SaveAsync(drafts.CreateAsync(new ArkiveModMetadata("Next idea", "1.0.0")).GetAwaiter().GetResult() with
        { EditIds = [drafted.Document.EditId] }).GetAwaiter().GetResult();
        var ownFile = env.Library.GetModFilePath(env.Library.LoadAsync().GetAwaiter().GetResult().Find(ownMod)!);

        var removal = author.RemoveModAsync(ownMod).GetAwaiter().GetResult();
        Assert(!Directory.Exists(own.DirectoryPath) && removal.DeletedEditIds.SequenceEqual([own.Document.EditId]) &&
               !File.Exists(ownFile) && removal.DeletedDraftIds.Count == 1 &&
               drafts.LoadAsync().GetAwaiter().GetResult().All(draft => draft.ModId != ownMod),
            "deleting a mod made here did not delete its working file, .arkive file and draft");
        author.RemoveModAsync(sharedMod).GetAwaiter().GetResult();
        Assert(Directory.Exists(shared.DirectoryPath), "a working file another mod is bound to was deleted with the mod");
        author.RemoveModAsync(draftedMod).GetAwaiter().GetResult();
        Assert(Directory.Exists(drafted.DirectoryPath), "a working file a draft uses was deleted with the mod");
        var kept = env.Edits.MaterialEdits.LoadAsync().GetAwaiter().GetResult();
        Assert(kept.Single(item => item.Document.EditId == drafted.Document.EditId).Document.IncludedInPackageBuild is false,
            "a kept working file of a deleted mod was left selected for package builds");
        Assert(Directory.Exists(other.DirectoryPath) && File.Exists(client), "deleting mods touched another mod's files or the game");
        Directory.Delete(root, recursive: true);

        (MaterialInstanceEditEntry Edit, string ModId) Authored(string name)
        {
            var edit = CreateMaterialEdit(env, client, name, 0.3f, null, candidate: false);
            var draft = drafts.CreateAsync(new ArkiveModMetadata(name, "1.0.0")).GetAwaiter().GetResult() with
            { EditIds = [edit.Document.EditId] };
            var mod = author.SaveDraftAsync(draft).GetAwaiter().GetResult();
            drafts.DeleteAsync(draft.DraftId).GetAwaiter().GetResult();
            var bound = env.Library.LoadAsync().GetAwaiter().GetResult().Find(mod.ModId)!.Bindings.Single().EditId;
            return (env.Edits.MaterialEdits.LoadAsync().GetAwaiter().GetResult().Single(item => item.Document.EditId == bound), mod.ModId);
        }
    }

    /// <summary>A material mod applied, then a second mod applied in its place: the first build is superseded.</summary>
    internal static (ArkiveTestEnvironment Env, ArkiveModApplyService Service, string Package,
        GuidedTexturePackageRevision First, GuidedTexturePackageRevision Second) TwoBuilds(string root) =>
        TwoBuilds(root, out _);

    internal static (ArkiveTestEnvironment Env, ArkiveModApplyService Service, string Package,
        GuidedTexturePackageRevision First, GuidedTexturePackageRevision Second) TwoBuilds(string root, out ArkiveModApplyResult secondApply)
    {
        var packagePath = CreateMaterialClient(root, out _);
        var modA = AuthorMaterialMod(root, packagePath, "Glossy face", roughness: 0.75f, blue: null);
        var modB = AuthorMaterialMod(root, packagePath, "Matte face", roughness: 0.25f, blue: null);
        var env = CreateEnvironment(Path.Combine(root, "player"));
        var a = env.Library.ImportAsync(modA).GetAwaiter().GetResult().Entry;
        var b = env.Library.ImportAsync(modB).GetAwaiter().GetResult().Entry;
        env.Library.SetEnabledAsync(a.ModId, true).GetAwaiter().GetResult();
        var service = CreateApplyService(env, Path.GetDirectoryName(packagePath)!);
        var firstApply = service.ApplyAsync(service.PlanAsync().GetAwaiter().GetResult().Fingerprint).GetAwaiter().GetResult();
        Assert(firstApply.Succeeded, "the first mod did not install");
        env.Library.SetEnabledAsync(a.ModId, false).GetAwaiter().GetResult();
        env.Library.SetEnabledAsync(b.ModId, true).GetAwaiter().GetResult();
        secondApply = service.ApplyAsync(service.PlanAsync().GetAwaiter().GetResult().Fingerprint).GetAwaiter().GetResult();
        Assert(secondApply.Succeeded && ReadMaterial(packagePath).ScalarParameters.Single().Value == 0.25f,
            "the second mod did not install: " + string.Join(" | ", secondApply.Packages.Select(item => item.Message)));
        var package = env.Packages.LoadAsync().GetAwaiter().GetResult().Single();
        Assert(package.Manifest.Revisions.Count == 2, $"the fixture made {package.Manifest.Revisions.Count} builds instead of two");
        var revisions = package.Manifest.Revisions.OrderBy(revision => revision.CreatedAt).ToArray();
        return (env, service, packagePath, revisions[0], revisions[1]);
    }

    internal static ArkiveSupersededBuildCleanup Builds(ArkiveTestEnvironment env, string root) =>
        new(env.Packages, env.Deployment, env.Backups, env.State, env.Root);

    private static string? KeptReason(ArkiveSupersededBuildCleanup builds, GuidedTexturePackageRevision revision) =>
        builds.PlanAsync(fullInspection: true).GetAwaiter().GetResult().Kept
            .FirstOrDefault(item => item.RevisionId == revision.RevisionId)?.Reason;

    internal static string Describe(ArkiveTestEnvironment env)
    {
        var library = env.Library.LoadAsync().GetAwaiter().GetResult();
        var statuses = ArkiveModApplyService.DescribeAsync(env.Library, library,
            ArkiveModEditSnapshot.LoadAsync(env.Edits).GetAwaiter().GetResult()).GetAwaiter().GetResult();
        return string.Join("\n", statuses.Select(status => $"{status.Mod.ModId} {status.State} {status.ConflictSummary} {status.ModifiedLocally}"));
    }

    private static readonly JsonSerializerOptions ReceiptJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static string Id(string name) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();

    private static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }

    private static void Age(string folder, TimeSpan age)
    {
        foreach (var file in Directory.EnumerateFiles(folder)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow - age);
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - age);
    }
}
