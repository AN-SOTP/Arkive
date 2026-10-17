namespace Arkive.SmokeTests;

internal static class WorkingFileSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("cleans up only mesh working files that nothing uses, keeping recent and hand-edited ones", TestWorkingFileCleanup),
        new("trims a finished package build to its payload, once, and leaves a changed build alone", TestPackageRevisionTrim),
    ];

    private static void TestPackageRevisionTrim()
    {
        var root = Path.Combine(fixtureDirectory, "revision-trim");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var package = Enumerable.Range(0, 300_000).Select(index => (byte)(index * 7)).ToArray();
        var payload = Write(Path.Combine("bundle", "payload", "EFGame", "ReleasePC", "Packages", "PACKAGE.upk"), package);
        var output = Write(Path.Combine("m01", "05-staged-upk", "PACKAGE.upk"), package);
        var earlier = Write(Path.Combine("01-textures", "a01", "09-upk", "arkive-replacement-staging.upk"), package[..250_000]);
        var noOp = Write(Path.Combine("01-textures", "a01", "08-noop", "arkive-noop-reconstructed.upk"), package[..290_000]);
        var logical = Write(Path.Combine("01-textures", "a01", "06-logical", "arkive-logical-package-image.bin"), package);
        var kept = Write(Path.Combine("m01", "04-native-encoding", "piece.skeletalmesh.edit.bin"), [1, 2, 3]);
        var hash = Convert.ToHexString(SHA256.HashData(package));
        var revision = new GuidedTexturePackageRevision("r1", DateTimeOffset.UtcNow, "", [], output, "", "", "", payload,
            package.Length, hash, 1);

        var changed = revision with { PayloadSha256 = new string('0', 64) };
        Assert(PackageRevisionTrimmer.TrimAsync(root, changed).GetAwaiter().GetResult() == 0 && File.Exists(noOp) && File.Exists(logical),
            "a build whose payload no longer matches its record was trimmed");
        var freed = PackageRevisionTrimmer.TrimAsync(root, revision).GetAwaiter().GetResult();
        Assert(!File.Exists(earlier) && !File.Exists(noOp) && !File.Exists(logical) && File.Exists(kept),
            "the trim did not delete exactly the earlier staged package, the no-change proof and the decompressed image");
        Assert(File.ReadAllBytes(output).SequenceEqual(package) && File.ReadAllBytes(payload).SequenceEqual(package),
            "the final staged package or the payload changed");
        Assert(freed == package.Length * 2L + 250_000 + 290_000, $"the trim reported {freed:N0} bytes freed");
        Assert(File.Exists(Path.Combine(root, PackageRevisionTrimmer.MarkerFileName)) &&
               PackageRevisionTrimmer.TrimAsync(root, revision).GetAwaiter().GetResult() == 0,
            "a trimmed build was not marked, or was trimmed twice");
        Directory.Delete(root, recursive: true);

        string Write(string relative, byte[] bytes)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }

    private static void TestWorkingFileCleanup()
    {
        var root = Path.Combine(fixtureDirectory, "working-files");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var edits = new SkeletalMeshEditWorkspaceStore(Path.Combine(root, "staging", "skeletal-mesh-edits"));
        var exports = new SkeletalMeshEditableWorkspaceStore(Path.Combine(root, "staging", "skeletal-mesh-reconnaissance"));

        var drafted = Export("drafted");
        var orphaned = Export("orphaned");
        var preview = Export("preview", editable: false);
        var handEdited = Export("hand-edited");
        var recent = Export("recent");
        var included = Export("included");
        var bound = Export("bound");
        var recorded = Export("recorded");
        var draftEdit = Edit("draft-edit", drafted);
        var orphanEdit = Edit("orphan-edit", orphaned);
        var includedEdit = Edit("included-edit", included, includedInBuild: true);
        var boundEdit = Edit("bound-edit", bound);
        var recordedEdit = Edit("recorded-edit", recorded);
        Write(Path.Combine(root, "staging", "mod-library", "authoring", "draft.json"), new { editIds = new[] { Id("draft-edit") } });
        Write(Path.Combine(root, "staging", "mod-library", "arkive-mod-library.json"),
            new { mods = new[] { new { bindings = new[] { new { editId = Id("bound-edit") } } } } });
        Write(Path.Combine(root, "staging", "texture-mods", "_packages", "_r", "revision.json"),
            new { meshAssets = new[] { new { editedGlb = Path.Combine(recordedEdit, "edited.glb"), source = Path.Combine(recorded, "editable-glb") } } });
        foreach (var folder in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Append(root))
            Age(folder, TimeSpan.FromDays(2));
        // Saved over in Blender after Arkive exported it, and made a few minutes ago.
        var handEditedGlb = Directory.EnumerateFiles(handEdited, "mesh.glb", SearchOption.AllDirectories).Single();
        File.SetLastWriteTimeUtc(handEditedGlb, DateTime.UtcNow.AddDays(-1));
        foreach (var file in Directory.EnumerateFiles(recent, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow);

        var cleanup = new ArkiveWorkingFileCleanup(root, edits, exports);
        var age = TimeSpan.FromHours(1);
        var plan = cleanup.PlanAsync(age).GetAwaiter().GetResult();
        var planned = plan.Items.Select(item => item.DirectoryPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] expected = [orphanEdit, orphaned, preview];
        Assert(planned.SequenceEqual(expected.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            "the cleanup did not pick exactly the unused edit, its export and the preview export: " + string.Join(", ", planned));
        Assert(plan.Bytes > 0, "the cleanup did not count the bytes it would free");

        var targeted = cleanup.PlanAsync(TimeSpan.Zero, [Id("orphan-edit"), Id("bound-edit")]).GetAwaiter().GetResult();
        Assert(targeted.Items.Select(item => item.DirectoryPath).Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(new[] { orphanEdit, orphaned }.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            "a cleanup of named pieces touched more than the unused piece and its export, or deleted a mod's piece");

        // A draft that picks the edit up after the plan was shown keeps it.
        Write(Path.Combine(root, "staging", "mod-library", "authoring", "later.json"), new { editIds = new[] { Id("orphan-edit") } });
        cleanup.DeleteAsync(plan, age).GetAwaiter().GetResult();
        Assert(Directory.Exists(orphanEdit) && Directory.Exists(orphaned) && !Directory.Exists(preview),
            "the cleanup deleted an edit a draft started using after the plan, or kept the unused preview");
        File.Delete(Path.Combine(root, "staging", "mod-library", "authoring", "later.json"));

        var freed = cleanup.DeleteAsync(cleanup.PlanAsync(age).GetAwaiter().GetResult(), age).GetAwaiter().GetResult();
        Assert(freed > 0 && !Directory.Exists(orphanEdit) && !Directory.Exists(orphaned) &&
               !Directory.Exists(Path.GetDirectoryName(orphanEdit)),
            "the cleanup did not delete the unused edit, its export and its emptied folder");
        foreach (var kept in new[] { drafted, handEdited, recent, included, bound, recorded, draftEdit, includedEdit, boundEdit, recordedEdit })
            Assert(Directory.Exists(kept), $"the cleanup deleted {kept}, which is in use, recent or edited by hand");
        Assert(edits.LoadAsync().GetAwaiter().GetResult().Entries.Count == 4, "the edit workspace no longer lists the kept edits");
        Directory.Delete(root, recursive: true);

        string Id(string name) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();

        string Export(string name, bool editable = true)
        {
            var folder = Path.Combine(exports.RootDirectory, "pc_test", $"{name}_sk-20261017-{Id(name)}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "arkive-skeletalmesh-recon.json"), "{}");
            if (!editable) return folder;
            var session = Path.Combine(folder, "editable-glb", Id(name + "-session"));
            Directory.CreateDirectory(session);
            File.WriteAllBytes(Path.Combine(session, "mesh.glb"), [1, 2, 3, 4]);
            Write(Path.Combine(session, SkeletalMeshEditableWorkspaceStore.ManifestFileName),
                new { schemaVersion = 1, exportId = Id(name + "-session"), objectName = name + "_sk", glbRelativePath = "mesh.glb", glbBytes = 4 });
            return folder;
        }

        string Edit(string name, string export, bool includedInBuild = false)
        {
            var manifest = Directory.EnumerateFiles(export, SkeletalMeshEditableWorkspaceStore.ManifestFileName, SearchOption.AllDirectories).Single();
            var folder = Path.Combine(edits.RootDirectory, Id(name + "-source"), $"20261017-120000000-{Id(name)}");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "edited.glb"), [1, 2, 3, 4]);
            Write(Path.Combine(folder, SkeletalMeshEditWorkspaceStore.ManifestFileName), new
            {
                schemaVersion = SkeletalMeshEditWorkspaceStore.CurrentSchemaVersion, editId = Id(name),
                createdAt = DateTimeOffset.UtcNow.AddDays(-2), displayName = name, sourceExportId = Id(name + "-source"),
                sourceManifestPath = manifest, objectName = name + "_sk", editedGlbRelativePath = "edited.glb", editedGlbBytes = 4,
                includedInPackageBuild = includedInBuild,
            });
            return folder;
        }

        static void Write(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }

        static void Age(string folder, TimeSpan age)
        {
            foreach (var file in Directory.EnumerateFiles(folder)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow - age);
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - age);
        }
    }
}
