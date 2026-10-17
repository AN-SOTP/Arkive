using static Arkive.SmokeTests.ArkiveModTestFixture;

namespace Arkive.SmokeTests;

internal static class ModAuthoringSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("persists named authoring drafts and optional references across restarts", TestDurableDraft),
        new("removes multiple included, reference, and unprepared assets without losing other draft state", TestBulkRemoval),
        new("saves bulk removals atomically and refuses publishing an empty mod without changing saved files", TestSavedBulkRemoval),
        new("saves one authored mod across packages and retains asset identity on replacement", TestMultiPackageAuthoring),
        new("refuses incomplete, duplicate, or foreign-owned authoring assets without changing the saved mod", TestAuthoringRefusals),
        new("prepares an imported mod for editing without enabling or installing it", TestPrepareImportedMod),
        new("prepares editing from a verified clean backup while another mod stays installed", TestPrepareInstalledPackage),
        new("refuses unsaved edits and invalidates a reviewed apply plan when saved content changes", TestSaveApplyConsistency),
        new("revalidates in-place mesh edits on save and preserves the portable mod on failure", TestMeshSaveValidation),
        new("leaves a mod's unchanged mesh out of builds instead of failing its package", TestUnchangedMeshApply),
        new("packs logs, mod list and mesh checks into a problem report without game files", TestProblemReport),
        new("accepts a game mesh's own zero-area triangles wherever an edit moves them", TestMovedDegenerateTriangles),
        new("explains unweighted vertices that Blender exports as neutral_bone", TestNeutralBone),
        new("exports clean assets into independent working copies without changing game files", TestDirectAssetExport),
        new("reports asset-export stages and honours cancellation without adding a working edit", TestAssetExportCancellation),
        new("refuses changed retained mesh evidence before invoking the asset-kit exporter", TestRetainedKitEvidence),
        new("reconciles authoring bindings after rebuilds while retaining pending replacements", TestReconcileBindings),
        new("reports damaged authoring drafts while retaining healthy drafts", TestDamagedDraft),
        new("prefers the executable's Arkive data root over an unrelated working checkout", TestRuntimeRoot),
    ];

    private static void TestBulkRemoval()
    {
        var workspace = new ArkiveModAuthoringWorkspace(Path.Combine(fixtureDirectory, "authoring-bulk-removal"));
        var draft = workspace.CreateAsync(new ArkiveModMetadata("Bulk edit", "1.2.0", "Tester")).GetAwaiter().GetResult() with
        {
            EditIds = ["mesh", "cloth", "keep"],
            ReferenceEditIds = ["reference", "keep-reference"],
            RemovedAssetIds = ["already-removed"],
            SavedBindings = new Dictionary<string, string> { ["mesh-asset"] = "mesh", ["cloth-asset"] = "cloth" },
        };
        var reduced = workspace.SaveAsync(draft.RemoveAssets(["MESH", "reference", "reference"], ["CLOTH-ASSET", "unprepared"]))
            .GetAwaiter().GetResult();
        Assert(reduced.EditIds.SequenceEqual(["keep"]) && reduced.ReferenceEditIds.SequenceEqual(["keep-reference"]) &&
               reduced.RemovedAssetIds.Count == 4 && reduced.RemovedAssetIds.Contains("mesh-asset"),
            "bulk removal lost unselected rows, duplicated removals, or left a saved binding included");
        Assert(reduced.DraftId == draft.DraftId && reduced.Metadata == draft.Metadata && reduced.SavedBindings == draft.SavedBindings,
            "bulk removal changed the draft identity, metadata, or saved evidence");
        var empty = workspace.SaveAsync(reduced.RemoveAssets(reduced.EditIds.Concat(reduced.ReferenceEditIds), []))
            .GetAwaiter().GetResult();
        var loaded = workspace.LoadAsync().GetAwaiter().GetResult().Single();
        Assert(empty.EditIds.Count == 0 && empty.ReferenceEditIds.Count == 0 && loaded.EditIds.Count == 0 &&
               loaded.RemovedAssetIds.SequenceEqual(empty.RemovedAssetIds), "clearing all assets did not survive reopening");
    }

    private static void TestDurableDraft()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-durable");
        var workspace = new ArkiveModAuthoringWorkspace(root);
        var draft = workspace.CreateAsync(new ArkiveModMetadata("Brelshaza outfit", "1.0.0", "Tester"))
            .GetAwaiter().GetResult();
        draft = workspace.SaveAsync(draft with
        {
            EditIds = ["first", "second", "first"],
            ReferenceEditIds = ["reference"],
            RemovedAssetIds = ["removed"],
        }).GetAwaiter().GetResult();
        var loaded = new ArkiveModAuthoringWorkspace(root).LoadAsync().GetAwaiter().GetResult().Single();
        Assert(loaded.DraftId == draft.DraftId && loaded.ModId is null && loaded.Metadata.Name == "Brelshaza outfit" &&
               loaded.EditIds.SequenceEqual(["first", "second"]) && loaded.ReferenceEditIds.SequenceEqual(["reference"]) &&
               loaded.RemovedAssetIds.SequenceEqual(["removed"]), "authoring state did not survive reopening");
        Assert(!Directory.EnumerateFiles(root, "*.arkive").Any(), "an empty draft was published as a portable mod");
        try
        {
            workspace.DeleteAsync("../other").GetAwaiter().GetResult();
            throw new InvalidOperationException("authoring accepted a traversing draft identity");
        }
        catch (ArgumentException) { }
        workspace.DeleteAsync(draft.DraftId).GetAwaiter().GetResult();
        Assert(workspace.LoadAsync().GetAwaiter().GetResult().Count == 0, "draft removal was not durable");
    }

    private static void TestSavedBulkRemoval()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-saved-bulk-removal");
        var client = CreateMaterialClient(root, out var cleanHash);
        var second = Path.Combine(Path.GetDirectoryName(client)!, "second.upk");
        var third = Path.Combine(Path.GetDirectoryName(client)!, "third.upk");
        File.Copy(client, second);
        File.Copy(client, third);
        var env = CreateEnvironment(Path.Combine(root, "workspace"));
        var edits = new[] { client, second, third }.Select((path, index) =>
            CreateMaterialEdit(env, path, $"Asset {index}", 0.8f, null, candidate: false)).ToArray();
        var author = new ArkiveModAuthoringService(env.Library, env.Edits);
        var workspace = new ArkiveModAuthoringWorkspace(Path.Combine(env.Library.StateDirectory, "authoring"));
        var draft = workspace.CreateAsync(new ArkiveModMetadata("Bulk saved mod", "1.0.0")).GetAwaiter().GetResult() with
        { EditIds = edits.Select(edit => edit.Document.EditId).ToArray() };
        var mod = author.SaveDraftAsync(draft).GetAwaiter().GetResult();
        env.Library.SetPriorityAsync(mod.ModId, 5).GetAwaiter().GetResult();
        mod = env.Library.LoadAsync().GetAwaiter().GetResult().Find(mod.ModId)!;
        draft = workspace.BeginEditAsync(mod).GetAwaiter().GetResult();
        var modPath = env.Library.GetModFilePath(mod);
        var savedHash = Hash(modPath);
        var removed = draft.RemoveAssets(draft.EditIds.Take(2), []);
        Assert(Hash(modPath) == savedHash, "staging bulk removal modified the portable archive");
        var reduced = author.SaveDraftAsync(removed).GetAwaiter().GetResult();
        Assert(reduced.Manifest.Assets.Count == 1 && reduced.Priority == 5 &&
               reduced.Manifest.Assets[0].AssetId == mod.Manifest.Assets[2].AssetId,
            "saving bulk removal changed the surviving asset identity or priority");
        var currentHash = Hash(modPath);
        var empty = (removed with
        {
            EditIds = reduced.Bindings.Select(binding => binding.EditId).ToArray(),
            RemovedAssetIds = [],
            SavedBindings = reduced.Bindings.ToDictionary(binding => binding.AssetId, binding => binding.EditId),
        }).RemoveAssets(reduced.Bindings.Select(binding => binding.EditId), []);
        Expect("AUT062", () => author.SaveDraftAsync(empty).GetAwaiter().GetResult());
        Assert(Hash(modPath) == currentHash && Hash(client) == cleanHash && Hash(second) == cleanHash &&
               Hash(third) == cleanHash && edits.All(edit => File.Exists(edit.DocumentPath)),
            "empty-save refusal deleted working copies or modified a game package or portable archive");
    }

    private static void TestMultiPackageAuthoring()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-multi");
        var client = CreateMaterialClient(root, out _);
        var second = Path.Combine(Path.GetDirectoryName(client)!, "second-material.upk");
        File.Copy(client, second);
        var env = CreateEnvironment(Path.Combine(root, "workspace"));
        var firstEdit = CreateMaterialEdit(env, client, "Outfit", 0.8f, null, candidate: false);
        var secondEdit = CreateMaterialEdit(env, second, "Accessory", 0.7f, null, candidate: false);
        var workspace = new ArkiveModAuthoringWorkspace(Path.Combine(env.Library.StateDirectory, "authoring"));
        var draft = workspace.CreateAsync(new ArkiveModMetadata("Complete outfit", "1.0.0")).GetAwaiter().GetResult() with
        { EditIds = [firstEdit.Document.EditId, secondEdit.Document.EditId] };
        var author = new ArkiveModAuthoringService(env.Library, env.Edits);
        var mod = author.SaveDraftAsync(draft).GetAwaiter().GetResult();
        Assert(!mod.Enabled && mod.Manifest.Assets.Count == 2 && mod.Manifest.PhysicalPackageNames.Count == 2,
            "a multi-package authored mod lost assets or enabled itself");
        env.Library.SetPriorityAsync(mod.ModId, 5).GetAwaiter().GetResult();
        var assetId = mod.Manifest.Assets.Single(asset => asset.Target.PhysicalPackageName == Path.GetFileName(client)).AssetId;
        var replacement = CreateMaterialEdit(env, client, "New outfit", 0.6f, null, candidate: false);
        var replaced = author.SaveDraftAsync(draft with
        {
            ModId = mod.ModId,
            EditIds = [replacement.Document.EditId, secondEdit.Document.EditId],
        }).GetAwaiter().GetResult();
        Assert(replaced.Priority == 5 && replaced.Manifest.Assets.Count == 2 &&
               replaced.BindingFor(assetId)?.EditId == replacement.Document.EditId,
            "asset replacement changed priority, asset identity, or another package");
        var old = env.Edits.MaterialEdits.LoadEntryAsync(firstEdit.DocumentPath).GetAwaiter().GetResult();
        Assert(!old.Document.IncludedInPackageBuild && File.Exists(old.DocumentPath), "replacement did not retain its old working copy as an excluded draft");
        var accessoryId = replaced.Manifest.Assets.Single(asset => asset.Target.PhysicalPackageName == Path.GetFileName(second)).AssetId;
        var reduced = author.SaveDraftAsync(draft with
        {
            ModId = mod.ModId,
            EditIds = [replacement.Document.EditId],
            RemovedAssetIds = [accessoryId],
        }).GetAwaiter().GetResult();
        Assert(reduced.Manifest.Assets.Count == 1 && reduced.Priority == 5, "removing one package affected the remaining mod asset");
    }

    private static void TestAuthoringRefusals()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-refusals");
        var client = CreateMaterialClient(root, out _);
        var env = CreateEnvironment(Path.Combine(root, "workspace"));
        var edit = CreateMaterialEdit(env, client, "Owned", 0.8f, null, false);
        var author = new ArkiveModAuthoringService(env.Library, env.Edits);
        var draft = new ArkiveModAuthoringDraft(1, Guid.NewGuid().ToString("N"), null,
            new ArkiveModMetadata("Owned mod", "1.0.0"), [edit.Document.EditId], DateTimeOffset.UtcNow);
        var mod = author.SaveDraftAsync(draft).GetAwaiter().GetResult();
        var bytes = File.ReadAllBytes(env.Library.GetModFilePath(mod));
        Expect("AUT070", () => author.SaveDraftAsync(draft with { Metadata = new ArkiveModMetadata("Other mod", "1.0.0") }).GetAwaiter().GetResult());
        Expect("AUT091", () => author.SaveDraftAsync(draft with { ModId = mod.ModId, EditIds = ["missing"] }).GetAwaiter().GetResult());
        var duplicate = CreateMaterialEdit(env, client, "Duplicate", 0.3f, null, false);
        Expect("AUT092", () => author.SaveDraftAsync(draft with { ModId = mod.ModId, EditIds = [edit.Document.EditId, duplicate.Document.EditId] })
            .GetAwaiter().GetResult());
        Expect("AUT090", () => author.SaveDraftAsync(draft with { ModId = mod.ModId, Metadata = new ArkiveModMetadata(" ", "1.0.0") })
            .GetAwaiter().GetResult());
        Assert(File.ReadAllBytes(env.Library.GetModFilePath(mod)).SequenceEqual(bytes), "failed authoring changed the portable file");
    }

    private static void TestPrepareImportedMod()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-prepare");
        var client = CreateMaterialClient(root, out var cleanHash);
        var source = AuthorMaterialMod(root, client, "Imported outfit", 0.9f, null);
        var env = CreateEnvironment(Path.Combine(root, "player"));
        var imported = env.Library.ImportAsync(source).GetAwaiter().GetResult().Entry;
        var service = CreateApplyService(env, Path.GetDirectoryName(client)!);
        service.PrepareModForEditingAsync(imported.ModId).GetAwaiter().GetResult();
        var prepared = env.Library.LoadAsync().GetAwaiter().GetResult().Find(imported.ModId)!;
        Assert(!prepared.Enabled && prepared.Bindings.Count == 1 && Hash(client) == cleanHash,
            "preparing imported assets changed the game or enabled the mod");
        var edit = ArkiveModEditSnapshot.LoadAsync(env.Edits).GetAwaiter().GetResult().Find(prepared.Bindings[0].EditId);
        Assert(edit is not null && !edit.Included, "prepared working copy was not independent of deployment");
        service.PrepareModForEditingAsync(imported.ModId).GetAwaiter().GetResult();
        Assert(env.Library.LoadAsync().GetAwaiter().GetResult().Find(imported.ModId)!.Bindings[0].EditId == prepared.Bindings[0].EditId,
            "preparing an existing working copy recreated it");
        var preview = service.PreviewModAsync(imported.ModId).GetAwaiter().GetResult();
        Assert(preview.CanApply && preview.HasChanges && !env.Library.LoadAsync().GetAwaiter().GetResult().Find(imported.ModId)!.Enabled &&
            Hash(client) == cleanHash, "previewing a disabled mod enabled it or changed the game");
    }

    private static void TestPrepareInstalledPackage()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-installed");
        var client = CreateMaterialClient(root, out _);
        var env = CreateEnvironment(Path.Combine(root, "player"));
        var active = env.Library.ImportAsync(AuthorMaterialMod(root, client, "Active", 0.8f, null)).GetAwaiter().GetResult().Entry;
        var replacementFile = AuthorMaterialMod(root, client, "Replacement", 0.6f, null);
        env.Library.SetEnabledAsync(active.ModId, true).GetAwaiter().GetResult();
        var service = CreateApplyService(env, Path.GetDirectoryName(client)!);
        Assert(service.ApplyAsync().GetAwaiter().GetResult().Succeeded, "initial mod did not install");
        var installedHash = Hash(client);
        var replacement = env.Library.ImportAsync(replacementFile).GetAwaiter().GetResult().Entry;
        service.PrepareModForEditingAsync(replacement.ModId).GetAwaiter().GetResult();
        Assert(Hash(client) == installedHash, "preparing a replacement changed the installed package");
        var prepared = env.Library.LoadAsync().GetAwaiter().GetResult().Find(replacement.ModId)!;
        Assert(prepared.Bindings.Count == 1 && prepared.Bindings[0].SourcePackagePath != client,
            "editing did not use the clean backup");
        env.Library.SetPriorityAsync(replacement.ModId, 10).GetAwaiter().GetResult();
        env.Library.SetEnabledAsync(replacement.ModId, true).GetAwaiter().GetResult();
        var plan = service.PlanAsync().GetAwaiter().GetResult();
        Assert(plan.CanApply && plan.Packages.Single().RestoreFirst && plan.Packages.Single().Setups.Count == 1,
            "backup-based editing did not plan preparation against the restored client");
        Assert(service.ApplyAsync(plan.Fingerprint).GetAwaiter().GetResult().Succeeded && Hash(client) != installedHash,
            "prepared replacement could not be applied");
    }

    private static void TestSaveApplyConsistency()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-consistency");
        var client = CreateMaterialClient(root, out var cleanHash);
        var env = CreateEnvironment(Path.Combine(root, "workspace"));
        var edit = CreateMaterialEdit(env, client, "Edit", 0.8f, null, true);
        var author = new ArkiveModAuthoringService(env.Library, env.Edits);
        var draft = new ArkiveModAuthoringDraft(1, Guid.NewGuid().ToString("N"), null,
            new ArkiveModMetadata("Consistent", "1.0.0"), [edit.Document.EditId], DateTimeOffset.UtcNow);
        var mod = author.SaveDraftAsync(draft).GetAwaiter().GetResult();
        env.Library.SetEnabledAsync(mod.ModId, true).GetAwaiter().GetResult();
        var service = CreateApplyService(env, Path.GetDirectoryName(client)!);
        var plan = service.PlanAsync().GetAwaiter().GetResult();
        var changed = edit with
        {
            Document = edit.Document with
            { Scalars = edit.Document.Scalars.Select(item => item with { Value = 0.6f }).ToArray() }
        };
        changed = env.Edits.MaterialEdits.SaveAsync(changed).GetAwaiter().GetResult();
        var candidate = new MaterialInstanceEditService().BuildCandidateAsync(changed.DocumentPath, env.MaterialCandidates)
            .GetAwaiter().GetResult();
        env.Edits.MaterialEdits.RecordCandidateAsync(changed, candidate).GetAwaiter().GetResult();
        Assert(!service.PlanAsync().GetAwaiter().GetResult().CanApply, "unsaved local edits could be installed");
        author.SaveModAsync(mod.ModId).GetAwaiter().GetResult();
        Expect("APL001", () => service.ApplyAsync(plan.Fingerprint).GetAwaiter().GetResult());
        Assert(Hash(client) == cleanHash, "an obsolete reviewed plan changed the game");
        Assert(service.ApplyAsync().GetAwaiter().GetResult().Succeeded, "saved changes could not be applied");
    }

    private static void TestUnchangedMeshApply()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-unchanged-mesh");
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(game);
        var package = Path.Combine(game, "mesh.upk");
        var range = WriteSkeletalMeshReconPackage(package);
        var original = File.ReadAllBytes(package);
        var report = new SkeletalMeshReconnaissanceService().InspectAsync(new SkeletalMeshReconnaissanceRequest(
            package, "mesh", 1, AssetKind.SkeletalMesh, "SkeletalMesh", "TestMesh", range.SerialOffset, range.SerialSize))
            .GetAwaiter().GetResult();
        var env = CreateEnvironment(Path.Combine(root, "workspace"));
        var recon = new SkeletalMeshReconnaissanceWorkspaceStore(Path.Combine(root, "recon")).SaveAsync(report).GetAwaiter().GetResult();
        var export = new SkeletalMeshEditableExportService().ExportAsync(new SkeletalMeshEditableExportRequest(
            report, recon.JsonPath, Path.Combine(root, "editable"))).GetAwaiter().GetResult();
        var source = new SkeletalMeshEditableWorkspaceEntry(export.OutputDirectory, export.ManifestPath, export.GlbPath,
            export.Manifest, true, true, true);
        // A body-wide upscale piece it does not reach: saved exactly as the game has it.
        var unchanged = env.Edits.SkeletalMeshEdits.RegisterAsync(source, export.GlbPath).GetAwaiter().GetResult().Entry;
        Assert(unchanged.Manifest.Preflight.Readiness == SkeletalMeshEditReadiness.ReadyNoChanges, "an unchanged mesh was not recognised");
        var author = new ArkiveModAuthoringService(env.Library, env.Edits);
        var mod = author.SaveDraftAsync(new ArkiveModAuthoringDraft(1, Guid.NewGuid().ToString("N"), null,
            new ArkiveModMetadata("Untouched piece", "1.0.0"), [unchanged.Manifest.EditId], DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
        env.Library.SetEnabledAsync(mod.ModId, true).GetAwaiter().GetResult();
        var service = CreateApplyService(env, game);
        var plan = service.PlanAsync().GetAwaiter().GetResult();
        Assert(plan.CanApply && plan.Packages.Single().Action == ArkiveModPackageAction.None,
            "a package whose only mod mesh is unchanged was not left alone: " +
            string.Join(" | ", plan.Packages.Select(item => $"{item.Action} {item.Reason}")) + " " + string.Join(" | ", plan.BlockingProblems));
        Assert(service.ApplyAsync(plan.Fingerprint).GetAwaiter().GetResult().Succeeded && File.ReadAllBytes(package).SequenceEqual(original),
            "applying an unchanged mesh failed or changed the game file");
        var library = env.Library.LoadAsync().GetAwaiter().GetResult();
        var status = ArkiveModApplyService.DescribeAsync(env.Library, library,
            ArkiveModEditSnapshot.LoadAsync(env.Edits).GetAwaiter().GetResult(),
            new Dictionary<string, GuidedTexturePackageRevision?>()).GetAwaiter().GetResult().Single();
        Assert(status.Assets.Single().InGame == true, "an unchanged mesh was reported as still waiting to be applied");
    }

    private static void TestMeshSaveValidation()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-mesh");
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, "mesh.upk");
        var range = WriteSkeletalMeshReconPackage(package);
        var report = new SkeletalMeshReconnaissanceService().InspectAsync(new SkeletalMeshReconnaissanceRequest(
            package, "mesh", 1, AssetKind.SkeletalMesh, "SkeletalMesh", "TestMesh", range.SerialOffset, range.SerialSize))
            .GetAwaiter().GetResult();
        var env = CreateEnvironment(Path.Combine(root, "workspace"));
        var recon = new SkeletalMeshReconnaissanceWorkspaceStore(Path.Combine(root, "recon")).SaveAsync(report).GetAwaiter().GetResult();
        var export = new SkeletalMeshEditableExportService().ExportAsync(new SkeletalMeshEditableExportRequest(
            report, recon.JsonPath, Path.Combine(root, "editable"))).GetAwaiter().GetResult();
        var source = new SkeletalMeshEditableWorkspaceEntry(export.OutputDirectory, export.ManifestPath, export.GlbPath,
            export.Manifest, true, true, true);
        var registered = env.Edits.SkeletalMeshEdits.RegisterAsync(source, export.GlbPath).GetAwaiter().GetResult().Entry;
        WriteSkeletalMeshPositionEdit(export.GlbPath, registered.EditedGlbPath, 0.025f);
        var author = new ArkiveModAuthoringService(env.Library, env.Edits);
        var draft = new ArkiveModAuthoringDraft(1, Guid.NewGuid().ToString("N"), null,
            new ArkiveModMetadata("Mesh deformation", "1.0.0"), [registered.Manifest.EditId], DateTimeOffset.UtcNow);
        var mod = author.SaveDraftAsync(draft).GetAwaiter().GetResult();
        var portable = File.ReadAllBytes(env.Library.GetModFilePath(mod));
        var asset = mod.Manifest.Assets.Single();
        var local = ArkiveModEditSnapshot.LoadAsync(env.Edits).GetAwaiter().GetResult().Find(registered.Manifest.EditId)!;
        Assert(!ArkiveModAuthoringService.IsModifiedAsync(local, mod.BindingFor(asset.AssetId)!).GetAwaiter().GetResult(),
            "saved mesh was still marked as modified");
        WriteSkeletalMeshMaterialRename(export.GlbPath, registered.EditedGlbPath);
        Assert(ArkiveModAuthoringService.IsModifiedAsync(local, mod.BindingFor(asset.AssetId)!).GetAwaiter().GetResult(),
            "in-place mesh change was hidden by a stale preflight hash");
        Assert(ArkiveModEditCheck.Read(local) is { Passed: true } && ArkiveModEditCheck.IsStaleAsync(local).GetAwaiter().GetResult(),
            "a working mesh changed after its check was not reported as needing a new check");
        var rechecked = author.ValidateEditAsync(local).GetAwaiter().GetResult();
        Assert(ArkiveModEditCheck.Read(rechecked) is { Passed: false, Problems: [var problem, ..] } && problem.StartsWith("SMT013", StringComparison.Ordinal) &&
               !ArkiveModEditCheck.IsStaleAsync(rechecked).GetAwaiter().GetResult(),
            "the in-place check did not name the failing mesh's problem");
        var refusal = Expect("AUT020", () => author.SaveDraftAsync(draft with { ModId = mod.ModId }).GetAwaiter().GetResult());
        Assert(refusal.Contains("SMT013", StringComparison.Ordinal), "the save refusal did not name the mesh check's error");
        Assert(File.ReadAllBytes(env.Library.GetModFilePath(mod)).SequenceEqual(portable), "invalid mesh save changed the portable mod");
        // A batch save checks pieces side by side and leaves a failing mesh out instead of refusing everything.
        var passing = env.Edits.SkeletalMeshEdits.RegisterAsync(source, export.GlbPath).GetAwaiter().GetResult().Entry;
        var skipped = new List<string>();
        var kept = author.SaveDraftAsync(new ArkiveModAuthoringDraft(1, Guid.NewGuid().ToString("N"), null,
                new ArkiveModMetadata("Mesh batch", "1.0.0"), [registered.Manifest.EditId, passing.Manifest.EditId], DateTimeOffset.UtcNow),
            skippedMeshes: skipped, parallelism: 2).GetAwaiter().GetResult();
        Assert(skipped is [var left] && left.Contains("SMT013", StringComparison.Ordinal) && kept.Manifest.Assets.Count == 1 &&
               kept.Bindings.Single().EditId == passing.Manifest.EditId,
            "a batch save did not leave out the failing mesh: " + string.Join(" | ", skipped));
    }

    private static void TestMovedDegenerateTriangles()
    {
        static SkeletalMeshGlbTrianglePositions Flat(float x) =>
            new(new System.Numerics.Vector3(x, 0, 0), new System.Numerics.Vector3(x + 1, 0, 0), new System.Numerics.Vector3(x + 2, 0, 0));
        static SkeletalMeshGlbTrianglePositions Scaled(SkeletalMeshGlbTrianglePositions triangle) =>
            new(triangle.A * 1.05f, triangle.B * 1.05f, triangle.C * 1.05f);
        SkeletalMeshGlbTrianglePositions[] game = [Flat(1), Flat(5)];
        Assert(SkeletalMeshEditPreflightService.NewDegenerateTriangles(game, game).Count == 0,
            "the game mesh's own zero-area triangles were refused where they were");
        Assert(SkeletalMeshEditPreflightService.NewDegenerateTriangles(game.Select(Scaled).ToArray(), game).Count == 0,
            "the game mesh's own zero-area triangles were refused after the mesh was scaled");
        Assert(SkeletalMeshEditPreflightService.NewDegenerateTriangles([.. game.Select(Scaled), Flat(20)], game).Count == 1,
            "a new zero-area triangle was accepted alongside moved original ones");
        Assert(SkeletalMeshEditPreflightService.NewDegenerateTriangles([game[0], Flat(20), Flat(30)], game).Count == 1,
            "only as many moved triangles as the game mesh had may be accepted");
    }

    private static void TestNeutralBone()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-neutral-bone");
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, "mesh.upk");
        var range = WriteSkeletalMeshReconPackage(package);
        var report = new SkeletalMeshReconnaissanceService().InspectAsync(new SkeletalMeshReconnaissanceRequest(
            package, "mesh", 1, AssetKind.SkeletalMesh, "SkeletalMesh", "TestMesh", range.SerialOffset, range.SerialSize))
            .GetAwaiter().GetResult();
        var recon = new SkeletalMeshReconnaissanceWorkspaceStore(Path.Combine(root, "recon")).SaveAsync(report).GetAwaiter().GetResult();
        var export = new SkeletalMeshEditableExportService().ExportAsync(new SkeletalMeshEditableExportRequest(
            report, recon.JsonPath, Path.Combine(root, "editable"))).GetAwaiter().GetResult();
        var source = new SkeletalMeshEditableWorkspaceEntry(export.OutputDirectory, export.ManifestPath, export.GlbPath,
            export.Manifest, true, true, true);
        // What Blender writes for vertices without weights: an extra joint called neutral_bone with no Arkive identity.
        var bytes = File.ReadAllBytes(export.GlbPath);
        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        var json = JsonNode.Parse(Encoding.UTF8.GetString(bytes, 20, jsonLength))!.AsObject();
        var joints = json["skins"]!.AsArray()[0]!["joints"]!.AsArray();
        var node = json["nodes"]!.AsArray()[joints[^1]!.GetValue<int>()]!.AsObject();
        node["name"] = "neutral_bone";
        node.Remove("extras");
        var edited = Path.Combine(root, "neutral-bone.glb");
        File.WriteAllBytes(edited, RebuildGlb(json, bytes.AsSpan(20 + jsonLength)));
        var preflight = new SkeletalMeshEditPreflightService().InspectAsync(source, edited).GetAwaiter().GetResult();
        Assert(preflight.Issues.Any(issue => issue.Code == "SMI145" && issue.Severity == SkeletalMeshEditIssueSeverity.Error &&
                                             issue.Message.Contains("no bone weights", StringComparison.Ordinal) &&
                                             issue.Message.Contains("neutral_bone", StringComparison.Ordinal)),
            "neutral_bone was not explained: " + string.Join(" | ", preflight.Issues.Select(issue => $"{issue.Code}:{issue.Message}")));
    }

    /// <summary>A GLB with new JSON and the original binary chunk (which starts at <paramref name="binChunk"/>).</summary>
    private static byte[] RebuildGlb(JsonObject json, ReadOnlySpan<byte> binChunk)
    {
        var text = Encoding.UTF8.GetBytes(json.ToJsonString());
        var padded = (text.Length + 3) & ~3;
        using var output = new MemoryStream();
        var word = new byte[4];
        void Write(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(word, value); output.Write(word); }
        Write(0x46546C67);
        Write(2);
        Write((uint)(12 + 8 + padded + binChunk.Length));
        Write((uint)padded);
        Write(0x4E4F534A);
        output.Write(text);
        for (var i = text.Length; i < padded; i++) output.WriteByte((byte)' ');
        output.Write(binChunk);
        return output.ToArray();
    }

    private static void TestProblemReport()
    {
        var root = Path.Combine(fixtureDirectory, "problem-report");
        Directory.CreateDirectory(root);
        var log = Path.Combine(root, "arkive-current.log");
        File.WriteAllText(log, "older lines\n");
        using var writer = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        writer.Write("still being written\n"u8);
        writer.Flush();
        var large = Path.Combine(root, "large.log");
        using (var stream = File.Create(large))
        {
            stream.Write(new byte[ArkiveProblemReport.MaximumFileBytes]);
            stream.Write("the end"u8);
        }
        var path = ArkiveProblemReport.CreateAsync(Path.Combine(root, "reports"), new ArkiveProblemReportContent("Arkive test",
                new Dictionary<string, string> { ["mods.txt"] = "[on] A mod" },
                new Dictionary<string, string> { ["logs/current.log"] = log, ["logs/large.log"] = large, ["logs/missing.log"] = Path.Combine(root, "missing.log") }))
            .GetAwaiter().GetResult();
        using var zip = ZipFile.OpenRead(path);
        string Read(string name) { using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return reader.ReadToEnd(); }
        Assert(zip.Entries.Select(entry => entry.FullName).Order().SequenceEqual(["logs/current.log", "logs/large.log", "mods.txt", "summary.txt"]),
            "the problem report holds unexpected entries: " + string.Join(", ", zip.Entries.Select(entry => entry.FullName)));
        Assert(Read("summary.txt") == "Arkive test" && Read("logs/current.log").Contains("still being written", StringComparison.Ordinal),
            "the problem report lost the summary or the log being written");
        Assert(zip.GetEntry("logs/large.log")!.Length == ArkiveProblemReport.MaximumFileBytes && Read("logs/large.log").EndsWith("the end", StringComparison.Ordinal),
            "a long log was not cut to its end");
        Assert(!Directory.EnumerateFiles(Path.Combine(root, "reports"), "*.tmp").Any(), "a partial problem report was left behind");
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void TestDirectAssetExport()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-export");
        var client = CreateMaterialClient(root, out var cleanHash);
        var env = CreateEnvironment(Path.Combine(root, "workspace"));
        var materializer = CreateMaterializer(env);
        var report = ReadMaterial(client);
        var selection = new ArkiveModAssetSelection(client, report.PackageAlias, "MaterialInstanceConstant", report.ObjectName);
        var stages = new List<ArkiveModAssetProgress>();
        var first = materializer.CreateEditableAsync(selection, default, new InlineProgress<ArkiveModAssetProgress>(stages.Add))
            .GetAwaiter().GetResult();
        Assert(stages.Count >= 2 && stages.All(stage => stage.ObjectName == report.ObjectName), "asset-export progress omitted its stages or identity");
        var second = materializer.CreateEditableAsync(selection).GetAwaiter().GetResult();
        Assert(first.EditId != second.EditId && Hash(client) == cleanHash &&
            ArkiveModEditSnapshot.LoadAsync(env.Edits).GetAwaiter().GetResult().Edits.All(edit => !edit.Included),
            "asset export reused an owned edit or selected it for installation");
        var meshPath = Path.Combine(Path.GetDirectoryName(client)!, "export-mesh.upk");
        WriteSkeletalMeshReconPackage(meshPath);
        var meshHash = Hash(meshPath);
        var mesh = materializer.CreateEditableAsync(new ArkiveModAssetSelection(meshPath, "mesh", "SkeletalMesh", "TestMesh"))
            .GetAwaiter().GetResult();
        Assert(mesh.Entry is SkeletalMeshEditWorkspaceEntry entry && File.Exists(entry.EditedGlbPath) &&
               Hash(meshPath) == meshHash, "direct mesh export did not create an independent GLB");
        var preflight = ((SkeletalMeshEditWorkspaceEntry)mesh.Entry).Manifest.Preflight;
        Assert(preflight.Readiness == SkeletalMeshEditReadiness.ReadyNoChanges && preflight.AnimationValidation is { Passed: true },
            "identical-source acceleration removed the animation safety proof");
    }

    private static void TestAssetExportCancellation()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-export-cancellation");
        var client = CreateMaterialClient(root, out var cleanHash);
        var env = CreateEnvironment(Path.Combine(root, "workspace"));
        var report = ReadMaterial(client);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<ArkiveModAssetProgress>(_ => cancellation.Cancel());
        try
        {
            CreateMaterializer(env).CreateEditableAsync(new(client, report.PackageAlias, "MaterialInstanceConstant", report.ObjectName),
                cancellation.Token, progress).GetAwaiter().GetResult();
            throw new InvalidOperationException("Cancelled asset export succeeded.");
        }
        catch (OperationCanceledException) { }
        Assert(Hash(client) == cleanHash && ArkiveModEditSnapshot.LoadAsync(env.Edits).GetAwaiter().GetResult().Edits.Count == 0,
            "cancelled export modified the client or registered a working edit");
    }

    private static void TestRetainedKitEvidence()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-retained-kit");
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, "mesh.upk");
        WriteSkeletalMeshReconPackage(package);
        var cleanHash = Hash(package);
        var env = CreateEnvironment(Path.Combine(root, "workspace"));
        var mesh = CreateMaterializer(env).CreateEditableAsync(new(package, "mesh", "SkeletalMesh", "TestMesh"))
            .GetAwaiter().GetResult();
        var edit = (SkeletalMeshEditWorkspaceEntry)mesh.Entry;
        var source = JsonSerializer.Deserialize<SkeletalMeshEditableExportManifest>(File.ReadAllText(edit.Manifest.SourceManifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var glb = Path.Combine(Path.GetDirectoryName(edit.Manifest.SourceManifestPath)!, source.GlbRelativePath);
        var bytes = File.ReadAllBytes(glb);
        bytes[^1] ^= 1;
        File.WriteAllBytes(glb, bytes);
        var output = Path.Combine(root, "kit");
        try
        {
            new SkeletalMeshAssetKitService(new UmodelBridge(Environment.ProcessPath!))
                .ExtractFromEditableAsync(edit.Manifest.SourceManifestPath, output).GetAwaiter().GetResult();
            throw new InvalidOperationException("A tampered retained source was exported as an asset kit.");
        }
        catch (InvalidDataException) { }
        Assert(!Directory.Exists(output) && Hash(package) == cleanHash, "invalid retained evidence created a kit or changed the package");
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static void TestReconcileBindings()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-reconcile");
        var client = CreateMaterialClient(root, out _);
        var env = CreateEnvironment(Path.Combine(root, "workspace"));
        var edit = CreateMaterialEdit(env, client, "Original", 0.8f, null, false);
        var author = new ArkiveModAuthoringService(env.Library, env.Edits);
        var captured = ArkiveModAuthoringService.CaptureAsync(ArkiveModEditSnapshot.LoadAsync(env.Edits)
            .GetAwaiter().GetResult().Find(edit.Document.EditId)!).GetAwaiter().GetResult();
        var mod = author.CreateModAsync(new ArkiveModMetadata("Rebased mod", "1.0.0"), [captured], ArkiveModOrigin.Created, false)
            .GetAwaiter().GetResult();
        var workspace = new ArkiveModAuthoringWorkspace(Path.Combine(env.Library.StateDirectory, "authoring"));
        var draft = workspace.BeginEditAsync(mod).GetAwaiter().GetResult();
        var rebound = mod with { Bindings = mod.Bindings.Select(binding => binding with { EditId = "rebased-edit" }).ToArray() };
        var updated = workspace.ReconcileAsync(draft, rebound).GetAwaiter().GetResult();
        Assert(updated.EditIds.SequenceEqual(["rebased-edit"]), "rebuilding left the authoring draft pointing at the retired edit");
        var pending = updated with { EditIds = ["pending-replacement"] };
        var again = workspace.ReconcileAsync(pending, mod).GetAwaiter().GetResult();
        Assert(again.EditIds.SequenceEqual(["pending-replacement"]), "binding reconciliation overwrote a pending user replacement");
    }

    private static void TestDamagedDraft()
    {
        var root = Path.Combine(fixtureDirectory, "authoring-damaged");
        var workspace = new ArkiveModAuthoringWorkspace(root);
        var healthy = workspace.CreateAsync(new ArkiveModMetadata("Healthy", "1.0.0")).GetAwaiter().GetResult();
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{bad JSON");
        var drafts = workspace.LoadAsync().GetAwaiter().GetResult();
        Assert(drafts.Count == 1 && drafts[0].DraftId == healthy.DraftId && workspace.LoadIssues.Count == 1 && File.Exists(path),
            "a damaged draft hid healthy work or was deleted without authorization");
    }

    private static void TestRuntimeRoot()
    {
        var expected = UmodelLocator.FindProjectRoot();
        Assert(expected is not null, "the smoke-test runtime is not inside an Arkive checkout");
        var unrelated = Path.Combine(fixtureDirectory, "unrelated-checkout");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "Arkive.slnx"), "<Solution />");
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = unrelated;
            Assert(UmodelLocator.FindProjectRoot() == expected, "an unrelated working directory redirected the mod library");
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    private static string Expect(string code, Action action)
    {
        try { action(); throw new InvalidOperationException($"Expected refusal {code}."); }
        catch (ArkiveModRefusalException refusal) when (refusal.Code == code) { return refusal.Message; }
    }
}
