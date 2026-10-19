using static Arkive.SmokeTests.ArkiveModTestFixture;

namespace Arkive.SmokeTests;

internal static class ArkiveModSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("round-trips a verified .arkive container and refuses tampering", TestArkiveFileRoundTripAndTamper),
        new("refuses unsafe, unknown, or newer .arkive containers", TestArkiveFileRefusals),
        new("imports, scans, and persists .arkive library state and priorities", TestArkiveLibraryImportAndPriorities),
        new("resolves .arkive conflicts by priority, merges disjoint assets, and blocks ties", TestArkiveConflictResolution),
        new("applies the higher-priority .arkive mod and rebuilds when priorities change", TestArkiveApplyPriorityEndToEnd),
        new("rebuilds and installs several packages in one apply", TestArkiveApplySeveralPackages),
        new("saves an applied mod again by reading its originals from the clean backup", TestArkiveSaveAfterApply),
        new("migrates package projects to .arkive mods without changing installed game files", TestArkivePackageProjectMigration),
        new("saves package-project drafts into its own .arkive mod without overriding priorities", TestArkivePackageProjectAuthoring),
        new("rebuilds .arkive mods after unchanged and breaking client updates", TestArkiveClientUpdate),
        new("updates an installed .arkive mod to a newer version of the same mod", TestArkiveModVersionUpdate),
        new("reinstalls the previous build when an .arkive asset cannot be set up", TestArkiveFailedSetupKeepsPreviousBuild),
        new("keeps same-named package objects apart by their object path", TestArkiveSameNamedObjectPaths),
        new("detects game files changed by another Arkive data folder and restores them from its backups", TestArkiveOutsideChanges),
    ];

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static void TestArkiveFileRoundTripAndTamper()
    {
        var root = Path.Combine(fixtureDirectory, "arkive-format");
        Directory.CreateDirectory(root);
        var image = Path.Combine(root, "face-edit.png");
        var glb = Path.Combine(root, "body-edit.glb");
        File.WriteAllBytes(image, Enumerable.Range(0, 4096).Select(index => (byte)(index * 7)).ToArray());
        File.WriteAllBytes(glb, Enumerable.Range(0, 9000).Select(index => (byte)(index % 251)).ToArray());
        var texture = TextureDescriptor("VC2N892NX2HXYCQF0REHFEBF.upk", "pc_wr_f_17_face_d");
        var mesh = new ArkiveModFileAsset(
            NewId(), UnifiedPackageAssetKind.SkeletalMesh, "Body sculpt",
            Target("WD3O9A3OY3IZ23I8EL6G1MPNM.upk", "SkeletalMesh", "pc_wr_f_av_178b_dress_sk"), [])
        {
            Mesh = new ArkiveModMeshData("TopologyPreserving", new string('C', 64)),
        };
        var material = new ArkiveModFileAsset(
            NewId(), UnifiedPackageAssetKind.MaterialInstanceConstant, "Face material",
            Target("VC2N892NX2HXYCQF0REHFEBF.upk", "MaterialInstanceConstant", "pc_wr_f_face_mi"), [])
        {
            Material = new ArkiveModMaterialData(
                [new MaterialScalarEdit("Roughness", 0.5f, 0.8f)], [],
                [new ArkiveModMaterialReference("Diffuse", "export:texture2d:a", "Texture2D'a'",
                    "export:texture2d:b", "Texture2D'b'")], null),
        };
        var manifest = ArkiveModAuthoringService.CreateManifest(
            NewId(), new ArkiveModMetadata("Format smoke mod", "1.2.3", "Arkive tests", "Round trip.",
                "https://example.invalid/mod", ["smoke", "Smoke", "format"]),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var output = Path.Combine(root, "Format smoke mod.arkive");
        var written = ArkiveModFileService.WriteAsync(output, manifest,
            [
                new ArkiveModPackAsset(texture, [new ArkiveModPackFile(ArkiveModFileFormat.ImageRole, image, "face-edit.png")]),
                new ArkiveModPackAsset(mesh, [new ArkiveModPackFile(ArkiveModFileFormat.GlbRole, glb, "body-edit.glb")]),
                new ArkiveModPackAsset(material, []),
            ]).GetAwaiter().GetResult();
        var read = ArkiveModFileService.ReadAsync(output).GetAwaiter().GetResult();
        var packedTexture = read.Manifest.Assets.Single(asset => asset.Kind == UnifiedPackageAssetKind.Texture2D);
        var file = packedTexture.Files.Single();
        Assert(read.Sha256 == written.Sha256 && read.Manifest.Assets.Count == 3 &&
               read.Manifest.Version == "1.2.3" && read.Manifest.Tags.Count == 2 &&
               read.Manifest.Format == ArkiveModFileFormat.FormatId &&
               file.Role == ArkiveModFileFormat.ImageRole &&
               file.Path == $"assets/{texture.AssetId}/face-edit.png" &&
               file.Sha256 == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image))) &&
               ArkiveModFileService.ComputeContentSha256(packedTexture) == file.Sha256 &&
               read.Manifest.PhysicalPackageNames.Count == 2,
            ".arkive round trip did not preserve metadata, file identities, or content hashes");
        var extracted = ArkiveModFileService.ExtractAssetFilesAsync(
                output, packedTexture, Path.Combine(root, "extract"))
            .GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(extracted[ArkiveModFileFormat.ImageRole]).SequenceEqual(File.ReadAllBytes(image)),
            ".arkive extraction did not reproduce the packed image byte-for-byte");
        using (var zip = ZipFile.OpenRead(output))
            Assert(zip.Entries.All(entry => entry.FullName == ArkiveModFileFormat.ManifestEntryName ||
                                            entry.FullName.StartsWith("assets/", StringComparison.Ordinal)) &&
                   !zip.Entries.Any(entry => entry.FullName.EndsWith(".upk", StringComparison.OrdinalIgnoreCase)),
                ".arkive container included something other than its manifest and edited assets");

        var tampered = Path.Combine(root, "tampered.arkive");
        File.Copy(output, tampered);
        ReplaceEntry(tampered, file.Path, File.ReadAllBytes(image).Select(value => (byte)(value ^ 0x5A)).ToArray());
        ExpectRefusal(tampered, "ARK010", "same-length tampered image");
        var truncated = Path.Combine(root, "truncated.arkive");
        File.Copy(output, truncated);
        ReplaceEntry(truncated, file.Path, [1, 2, 3]);
        ExpectRefusal(truncated, "ARK009", "resized tampered image");
    }

    private static void TestArkiveFileRefusals()
    {
        var root = Path.Combine(fixtureDirectory, "arkive-refusals");
        Directory.CreateDirectory(root);
        var image = Path.Combine(root, "edit.png");
        File.WriteAllBytes(image, [137, 80, 78, 71, 1, 2, 3, 4]);
        var asset = TextureDescriptor("PACKAGE.upk", "body_d");
        var manifest = ArkiveModAuthoringService.CreateManifest(
            NewId(), new ArkiveModMetadata("Refusal base", "1.0.0"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var valid = Path.Combine(root, "valid.arkive");
        var written = ArkiveModFileService.WriteAsync(valid, manifest,
                [new ArkiveModPackAsset(asset, [new ArkiveModPackFile(ArkiveModFileFormat.ImageRole, image, "edit.png")])])
            .GetAwaiter().GetResult();

        var notZip = Path.Combine(root, "not-a-zip.arkive");
        File.WriteAllBytes(notZip, Encoding.ASCII.GetBytes("This is not an archive."));
        ExpectRefusal(notZip, "ARK002", "non-ZIP file");

        var unreferenced = Path.Combine(root, "unreferenced.arkive");
        File.Copy(valid, unreferenced);
        using (var zip = ZipFile.Open(unreferenced, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open()))
            writer.Write("Smuggled file.");
        ExpectRefusal(unreferenced, "ARK006", "unreferenced entry");

        var traversal = Path.Combine(root, "traversal.arkive");
        WriteRawArchive(traversal, written.Manifest, ("../evil.png", [1]));
        ExpectRefusal(traversal, "ARK015", "path-traversal entry");

        var newer = Path.Combine(root, "newer.arkive");
        WriteRawArchive(newer, written.Manifest with { FormatVersion = ArkiveModFileFormat.CurrentFormatVersion + 1 },
            (written.Manifest.Assets[0].Files[0].Path, File.ReadAllBytes(image)));
        ExpectRefusal(newer, "ARK021", "newer format version");

        var noManifest = Path.Combine(root, "no-manifest.arkive");
        using (var zip = ZipFile.Open(noManifest, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("assets/x.png").Open())
            stream.Write([1, 2, 3]);
        ExpectRefusal(noManifest, "ARK003", "archive without a manifest");

        ExpectManifestRefusal(written.Manifest with { GameProfile = "lost-ark-korea" }, "ARK028", "foreign game profile");
        ExpectManifestRefusal(written.Manifest with
        {
            Assets = [written.Manifest.Assets[0], written.Manifest.Assets[0] with { AssetId = NewId() }],
        }, "ARK034", "duplicate asset target");
        ExpectManifestRefusal(written.Manifest with
        {
            Assets = [written.Manifest.Assets[0] with
            {
                Target = written.Manifest.Assets[0].Target with { PhysicalPackageName = "..\\other.upk" },
            }],
        }, "ARK039", "path-like package name");
        try
        {
            _ = ArkiveModFileService.WriteAsync(Path.Combine(root, "wrong.zip"), manifest,
                [new ArkiveModPackAsset(asset, [new ArkiveModPackFile(ArkiveModFileFormat.ImageRole, image, "edit.png")])])
                .GetAwaiter().GetResult();
            throw new InvalidOperationException(".arkive writer accepted a non-.arkive extension");
        }
        catch (ArkiveModRefusalException exception) when (exception.Code == "ARK040")
        {
        }
    }

    private static void TestArkiveLibraryImportAndPriorities()
    {
        var root = Path.Combine(fixtureDirectory, "arkive-library");
        var downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(downloads);
        var first = WriteTextureMod(Path.Combine(downloads, "shared-name.arkive"), "Shared name", out var firstManifest);
        var library = new ArkiveModLibrary(Path.Combine(root, "mods"), Path.Combine(root, "state"));
        var imported = library.ImportAsync(first).GetAwaiter().GetResult();
        Assert(!imported.UpdatedExistingMod && !imported.Entry.Enabled && imported.Entry.Priority == 0 &&
               File.Exists(Path.Combine(library.ModsDirectory, "Shared name.arkive")) &&
               File.Exists(first),
            "importing an .arkive did not copy it into the mods folder as a disabled priority-0 mod");
        library.SetEnabledAsync(imported.Entry.ModId, true).GetAwaiter().GetResult();
        library.SetPriorityAsync(imported.Entry.ModId, 7).GetAwaiter().GetResult();
        var asset = firstManifest.Assets[0];
        library.UpdateBindingsAsync(imported.Entry.ModId, _ =>
        [
            new ArkiveModAssetBinding(asset.AssetId, asset.Kind, NewId(), Path.Combine(root, "PACKAGE.upk"),
                ArkiveModFileService.ComputeContentSha256(asset)),
        ]).GetAwaiter().GetResult();
        var reopened = new ArkiveModLibrary(library.ModsDirectory, library.StateDirectory)
            .LoadAsync().GetAwaiter().GetResult().Find(imported.Entry.ModId);
        Assert(reopened is { Enabled: true, Priority: 7, Bindings.Count: 1 },
            "mod library did not persist enabled state, priority, and local bindings");

        var updatedImage = Path.Combine(downloads, "changed.png");
        File.WriteAllBytes(updatedImage, [9, 9, 9, 9, 9]);
        var updatedPath = Path.Combine(downloads, "updated.arkive");
        ArkiveModFileService.WriteAsync(updatedPath, firstManifest with { Version = "2.0.0", UpdatedAt = DateTimeOffset.UtcNow },
                [new ArkiveModPackAsset(asset, [new ArkiveModPackFile(ArkiveModFileFormat.ImageRole, updatedImage, "edit.png")])])
            .GetAwaiter().GetResult();
        var update = library.ImportAsync(updatedPath).GetAwaiter().GetResult();
        Assert(update.UpdatedExistingMod && update.Entry.Enabled && update.Entry.Priority == 7 &&
               update.Entry.Manifest.Version == "2.0.0" && update.Entry.Bindings.Count == 0 &&
               update.Released is [{ Delete: true }] &&
               update.Library.Mods.Count == 1,
            "re-importing a newer mod version did not keep settings and invalidate only changed bindings");

        var collision = WriteTextureMod(Path.Combine(downloads, "other.arkive"), "Shared name", out _);
        var second = library.ImportAsync(collision).GetAwaiter().GetResult();
        Assert(second.Entry.FileName == "Shared name (2).arkive",
            "two different mods with the same name did not receive distinct library files");

        WriteTextureMod(Path.Combine(library.ModsDirectory, "dropped.arkive"), "Dropped in folder", out var dropped);
        File.WriteAllText(Path.Combine(library.ModsDirectory, "broken.arkive"), "not a mod");
        var scan = library.ScanAsync().GetAwaiter().GetResult();
        Assert(scan.RegisteredModIds.SequenceEqual([dropped.ModId]) &&
               scan.Issues.Count == 1 && scan.Issues[0].Path.EndsWith("broken.arkive", StringComparison.Ordinal) &&
               scan.Library.Find(dropped.ModId) is { Enabled: false, FileName: "dropped.arkive" },
            "scanning the mods folder did not register new valid files and isolate invalid ones");
        try
        {
            library.SetPriorityAsync(dropped.ModId, ArkiveModLibrary.MaximumPriority + 1).GetAwaiter().GetResult();
            throw new InvalidOperationException("library accepted an out-of-range priority");
        }
        catch (ArkiveModRefusalException exception) when (exception.Code == "LIB030")
        {
        }
        var afterRemove = library.RemoveAsync(second.Entry.ModId, deleteFile: true).GetAwaiter().GetResult();
        Assert(afterRemove.Find(second.Entry.ModId) is null &&
               !File.Exists(Path.Combine(library.ModsDirectory, "Shared name (2).arkive")),
            "removing a mod did not remove its library entry and file");
    }

    private static void TestArkiveConflictResolution()
    {
        var x = TextureDescriptor("p1.upk", "texture_x");
        var z = TextureDescriptor("P1", "texture_z");
        var y = TextureDescriptor("P1.UPK", "texture_y");
        var a = LibraryEntry("Priority five", enabled: true, priority: 5, x);
        var b = LibraryEntry("Priority two", enabled: true, priority: 2,
            x with { AssetId = NewId(), Target = x.Target with { PhysicalPackageName = "P1.upk", ObjectName = "TEXTURE_X" } },
            z);
        var c = LibraryEntry("Only Y", enabled: true, priority: 0, y);
        var d = LibraryEntry("Disabled Y", enabled: false, priority: 100, y with { AssetId = NewId() });
        var resolution = ArkiveModConflictResolver.Resolve([a, b, c, d]);
        Assert(resolution.Targets.Count == 3 && resolution.BlockingTies.Count == 0 &&
               resolution.OutcomeFor(a.ModId, x.AssetId) == ArkiveModAssetOutcome.Winning &&
               resolution.OutcomeFor(b.ModId, b.Manifest.Assets[0].AssetId) == ArkiveModAssetOutcome.Overridden &&
               resolution.OutcomeFor(b.ModId, z.AssetId) == ArkiveModAssetOutcome.Winning &&
               resolution.OutcomeFor(c.ModId, y.AssetId) == ArkiveModAssetOutcome.Winning &&
               resolution.OutcomeFor(d.ModId, d.Manifest.Assets[0].AssetId) == ArkiveModAssetOutcome.Disabled &&
               resolution.Targets.Count(target => target.Winner is not null) == 3,
            "priority resolution did not let 5 beat 2, merge disjoint assets, and ignore disabled mods");
        var conflicts = resolution.ConflictsFor(b.ModId);
        Assert(conflicts is [{ OtherModId: var other, Outcome: var outcome, SharedObjects.Count: 1 }] &&
               other == a.ModId && outcome.Contains("higher priority", StringComparison.Ordinal) &&
               resolution.StrongestCompetitor(b.ModId, b.Manifest.Assets[0].AssetId)?.ModId == a.ModId,
            "pairwise conflict report did not describe the overriding mod");

        var tied = ArkiveModConflictResolver.Resolve([a, b with { Priority = 5 }, c, d]);
        Assert(tied.BlockingTies.Count == 1 && tied.BlockingTies[0].Winner is null &&
               tied.OutcomeFor(a.ModId, x.AssetId) == ArkiveModAssetOutcome.Tied &&
               tied.PriorityToWinAll(b.ModId) == 6 &&
               tied.ConflictsFor(a.ModId).Single().Outcome.StartsWith("Tie", StringComparison.Ordinal),
            "equal-priority conflicts were not reported as blocking ties with a winning-priority suggestion");
    }

    private static void TestArkiveApplySeveralPackages()
    {
        // Apply rebuilds packages a few at a time; each must still get its own mod's values and install together.
        var root = Path.Combine(fixtureDirectory, "arkive-apply-several");
        var first = CreateMaterialClient(root, out _);
        var packages = new[] { first }.Concat(Enumerable.Range(2, 4).Select(index =>
        {
            var path = Path.Combine(Path.GetDirectoryName(first)!, $"material-instance-{index}.upk");
            WriteMaterialInstancePackage(path);
            return path;
        })).ToArray();
        var player = CreateEnvironment(Path.Combine(root, "player"));
        for (var index = 0; index < packages.Length; index++)
        {
            var mod = AuthorMaterialMod(root, packages[index], $"Face {index}", roughness: 0.6f + 0.05f * index, blue: null);
            var entry = player.Library.ImportAsync(mod).GetAwaiter().GetResult().Entry;
            player.Library.SetEnabledAsync(entry.ModId, true).GetAwaiter().GetResult();
        }
        var service = CreateApplyService(player, Path.GetDirectoryName(first)!);
        var plan = service.PlanAsync().GetAwaiter().GetResult();
        Assert(plan.CanApply && plan.Packages.Count == packages.Length &&
               plan.Packages.All(package => package.Action == ArkiveModPackageAction.Rebuild),
            "every package with a mod was not planned for a rebuild: " + plan.Summary);
        var applied = service.ApplyAsync(plan.Fingerprint).GetAwaiter().GetResult();
        Assert(applied.Succeeded && applied.RebuiltPackageCount == packages.Length && applied.InstalledPackageCount == packages.Length,
            "the packages were not all rebuilt and installed: " + string.Join(" | ", applied.Packages.Select(item => item.Message)));
        for (var index = 0; index < packages.Length; index++)
            Assert(ReadMaterial(packages[index]).ScalarParameters.Single().Value == 0.6f + 0.05f * index,
                $"{Path.GetFileName(packages[index])} did not get its own mod's value");
        Assert(!service.PlanAsync().GetAwaiter().GetResult().HasChanges, "the applied packages still reported pending changes");
    }

    private static void TestArkiveSaveAfterApply()
    {
        // Applying sets the mod's working copy up against the game package and then rebuilds that package. Saving the
        // same mod again must read the original from the clean backup instead of refusing the changed game file.
        var root = Path.Combine(fixtureDirectory, "arkive-save-after-apply");
        var packagePath = CreateMaterialClient(root, out var cleanHash);
        var player = CreateEnvironment(Path.Combine(root, "player"));
        var edit = CreateMaterialEdit(player, packagePath, "Glossy face", 0.75f, null, candidate: false);
        var workspace = new ArkiveModAuthoringWorkspace(Path.Combine(player.Library.StateDirectory, "authoring"));
        var service = CreateApplyService(player, Path.GetDirectoryName(packagePath)!);
        var author = new ArkiveModAuthoringService(player.Library, player.Edits)
        {
            CleanPackageResolver = (path, token) => service.ResolveCleanPackageAsync(path, token),
        };
        var draft = workspace.CreateAsync(new ArkiveModMetadata("Glossy face", "1.0.0")).GetAwaiter().GetResult() with
        { EditIds = [edit.Document.EditId] };
        var mod = author.SaveDraftAsync(draft).GetAwaiter().GetResult();
        player.Library.SetEnabledAsync(mod.ModId, true).GetAwaiter().GetResult();
        var applied = service.ApplyAsync(service.PlanAsync().GetAwaiter().GetResult().Fingerprint).GetAwaiter().GetResult();
        Assert(applied.Succeeded && Sha(packagePath) != cleanHash, "the mod was not installed into the game package");

        mod = player.Library.LoadAsync().GetAwaiter().GetResult().Find(mod.ModId)!;
        var bound = mod.Bindings.Single().EditId;
        var local = player.Edits.MaterialEdits.LoadAsync().GetAwaiter().GetResult().Single(item => item.Document.EditId == bound);
        player.Edits.MaterialEdits.SaveAsync(local with
        {
            Document = local.Document with { Scalars = local.Document.Scalars.Select(item => item with { Value = 0.6f }).ToArray() },
        }).GetAwaiter().GetResult();
        var again = draft with
        {
            ModId = mod.ModId, EditIds = [bound],
            SavedBindings = mod.Bindings.ToDictionary(binding => binding.AssetId, binding => binding.EditId),
        };
        var saved = author.SaveDraftAsync(again).GetAwaiter().GetResult();
        var moved = player.Edits.MaterialEdits.LoadAsync().GetAwaiter().GetResult().Single(item => item.Document.EditId == bound);
        Assert(saved.Manifest.Assets.Count == 1 && !string.Equals(Path.GetFullPath(moved.Document.SourcePackagePath), Path.GetFullPath(packagePath), StringComparison.OrdinalIgnoreCase) &&
               Sha(moved.Document.SourcePackagePath) == cleanHash,
            "the applied mod's working copy was not moved to the clean backup when saving again");
        var plan = service.PlanAsync().GetAwaiter().GetResult();
        var reapplied = service.ApplyAsync(plan.Fingerprint).GetAwaiter().GetResult();
        Assert(reapplied.Succeeded && ReadMaterial(packagePath).ScalarParameters.Single().Value == 0.6f,
            "the re-saved mod did not apply its new value: " + string.Join(" | ", reapplied.Packages.Select(item => item.Message)));
    }

    private static void TestArkiveApplyPriorityEndToEnd()
    {
        var root = Path.Combine(fixtureDirectory, "arkive-apply");
        var packagePath = CreateMaterialClient(root, out var cleanHash);
        var modA = AuthorMaterialMod(root, packagePath, "Glossy face", roughness: 0.75f, blue: null);
        var modB = AuthorMaterialMod(root, packagePath, "Matte blue face", roughness: 0.25f, blue: 0.75f);

        var player = CreateEnvironment(Path.Combine(root, "player"));
        var a = player.Library.ImportAsync(modA).GetAwaiter().GetResult().Entry;
        var b = player.Library.ImportAsync(modB).GetAwaiter().GetResult().Entry;
        Assert(a.Bindings.Count == 0 && b.Bindings.Count == 0,
            "freshly imported .arkive mods unexpectedly carried local bindings");
        player.Library.SetEnabledAsync(a.ModId, true).GetAwaiter().GetResult();
        player.Library.SetEnabledAsync(b.ModId, true).GetAwaiter().GetResult();
        player.Library.SetPriorityAsync(a.ModId, 5).GetAwaiter().GetResult();
        player.Library.SetPriorityAsync(b.ModId, 2).GetAwaiter().GetResult();
        var service = CreateApplyService(player, Path.GetDirectoryName(packagePath)!);

        var plan = service.PlanAsync().GetAwaiter().GetResult();
        Assert(plan.CanApply && plan.Packages is [{ Action: ArkiveModPackageAction.Rebuild, RestoreFirst: false }] &&
               plan.Packages[0].Setups is [{ ModId: var setupMod }] && setupMod == a.ModId &&
               plan.Packages[0].Contributors is [{ ModName: "Glossy face", IsDraft: false }],
            "apply plan did not set up and rebuild only the higher-priority mod: " + plan.Summary);
        var applied = service.ApplyAsync(plan.Fingerprint).GetAwaiter().GetResult();
        var installed = ReadMaterial(packagePath);
        Assert(applied.Succeeded && applied.SetUpAssetCount == 1 && applied.RebuiltPackageCount == 1 &&
               applied.InstalledPackageCount == 1 &&
               installed.ScalarParameters.Single().Value == 0.75f &&
               installed.VectorParameters.Single().Blue == 0.25f,
            "applying priority 5 over priority 2 did not install the winning mod's material values: " +
            string.Join(" | ", applied.Packages.Select(item => item.Message)));
        Assert(!service.PlanAsync().GetAwaiter().GetResult().HasChanges,
            "an applied mod configuration still reported pending changes");
        var library = player.Library.LoadAsync().GetAwaiter().GetResult();
        var snapshot = ArkiveModEditSnapshot.LoadAsync(player.Edits).GetAwaiter().GetResult();
        var aEdit = snapshot.Find(library.Find(a.ModId)!.Bindings.Single().EditId)!;
        Assert(aEdit.Included && library.Find(b.ModId)!.Bindings.Count == 0,
            "the winning mod was not bound and checked while the overridden mod stayed unset");
        var statuses = ArkiveModApplyService.DescribeAsync(player.Library, library, snapshot).GetAwaiter().GetResult();
        Assert(statuses.Single(item => item.Mod.ModId == b.ModId).State == "Fully overridden" &&
               !statuses.Single(item => item.Mod.ModId == a.ModId).ModifiedLocally,
            "mod status did not report the overridden mod or falsely reported local changes");

        player.Library.SetPriorityAsync(a.ModId, 1).GetAwaiter().GetResult();
        player.Library.SetPriorityAsync(b.ModId, 9).GetAwaiter().GetResult();
        var swap = service.PlanAsync().GetAwaiter().GetResult();
        Assert(swap.Packages is [{ Action: ArkiveModPackageAction.Rebuild, RestoreFirst: true }] &&
               swap.Packages[0].Setups.Single().ModId == b.ModId,
            "raising the other mod's priority did not plan a restore and rebuild");
        var swapped = service.ApplyAsync(swap.Fingerprint).GetAwaiter().GetResult();
        installed = ReadMaterial(packagePath);
        Assert(swapped.Succeeded && swapped.RestoredPackageCount == 1 &&
               installed.ScalarParameters.Single().Value == 0.25f &&
               installed.VectorParameters.Single().Blue == 0.75f &&
               !ArkiveModEditSnapshot.LoadAsync(player.Edits).GetAwaiter().GetResult().Find(aEdit.EditId)!.Included,
            "after changing priorities the newly winning mod was not installed in place of the old winner");

        player.Library.SetPriorityAsync(a.ModId, 9).GetAwaiter().GetResult();
        var tie = service.PlanAsync().GetAwaiter().GetResult();
        Assert(!tie.CanApply && tie.BlockingProblems[0].Contains("same priority", StringComparison.Ordinal),
            "an equal-priority conflict was not blocked before apply");
        try
        {
            _ = service.ApplyAsync().GetAwaiter().GetResult();
            throw new InvalidOperationException("apply accepted a tied mod configuration");
        }
        catch (ArkiveModRefusalException exception) when (exception.Code == "APL002")
        {
        }
        Assert(tie.Packages is [{ Action: ArkiveModPackageAction.Blocked }] &&
               ReadMaterial(packagePath).ScalarParameters.Single().Value == 0.25f,
            "a refused tied apply was not blocked or changed the installed package");
        try
        {
            _ = service.ApplyAsync(swap.Fingerprint).GetAwaiter().GetResult();
            throw new InvalidOperationException("apply accepted an outdated reviewed plan");
        }
        catch (ArkiveModRefusalException exception) when (exception.Code == "APL001")
        {
        }

        player.Library.SetEnabledAsync(a.ModId, false).GetAwaiter().GetResult();
        player.Library.SetEnabledAsync(b.ModId, false).GetAwaiter().GetResult();
        var restore = service.PlanAsync().GetAwaiter().GetResult();
        Assert(restore.Packages is [{ Action: ArkiveModPackageAction.Restore }],
            "disabling every mod did not plan a restore of the installed package");
        var restored = service.ApplyAsync(restore.Fingerprint).GetAwaiter().GetResult();
        Assert(restored.Succeeded && Sha(packagePath) == cleanHash &&
               !service.PlanAsync().GetAwaiter().GetResult().HasChanges,
            "disabling every mod did not restore the exact clean client package");
    }

    private static void TestArkivePackageProjectMigration()
    {
        var root = Path.Combine(fixtureDirectory, "arkive-migration");
        var installedPath = CreateMaterialClient(root, out _);
        var secondPath = Path.Combine(Path.GetDirectoryName(installedPath)!, "material-second.upk");
        WriteMaterialInstancePackage(secondPath);
        var environment = CreateEnvironment(Path.Combine(root, "workspace"));
        var installedEdit = CreateMaterialEdit(environment, installedPath, "Installed edit", 0.9f, null, candidate: true);
        var draft = CreateMaterialEdit(environment, installedPath, "Unchecked variant", 0.1f, null, candidate: true);
        environment.Edits.MaterialEdits.SetIncludedAsync(draft, false).GetAwaiter().GetResult();
        var secondEdit = CreateMaterialEdit(environment, secondPath, "Not installed edit", 0.3f, null, candidate: true);

        var installedProject = environment.Packages.GetOrCreateAsync(
            "material_instance", Path.GetFileName(installedPath), installedPath).GetAwaiter().GetResult();
        installedProject = environment.Packages.UpdateMetadataAsync(
            installedProject.ManifestPath, "Installed material mod", "Made before .arkive existed.")
            .GetAwaiter().GetResult();
        _ = environment.Packages.GetOrCreateAsync(
            "material_second", Path.GetFileName(secondPath), secondPath).GetAwaiter().GetResult();
        var composition = Composer().BuildAsync(new UnifiedPackageCompositionRequest(
                installedProject, [], [], environment.Packages.CreateRevisionDirectory(installedProject),
                environment.Backups, "Installed material mod")
            {
                MaterialEdits = [environment.Edits.MaterialEdits.LoadEntryAsync(installedEdit.DocumentPath).GetAwaiter().GetResult()],
            }).GetAwaiter().GetResult();
        environment.Packages.CommitRevisionAsync(installedProject.ManifestPath, composition.Revision)
            .GetAwaiter().GetResult();
        environment.Deployment.InstallAsync(new ArkiveModDeploymentRequest(
                composition.Revision.ModBundleManifestPath, environment.Backups, environment.State))
            .GetAwaiter().GetResult();
        var installedHash = Sha(installedPath);

        var context = Context(environment, Path.GetDirectoryName(installedPath)!);
        var migration = new ArkiveModMigrationService(context).MigratePackageProjectsAsync("Smoke tester")
            .GetAwaiter().GetResult();
        var library = environment.Library.LoadAsync().GetAwaiter().GetResult();
        var installedMod = library.Mods.Single(mod => mod.AuthoredForPackageProjectId == installedProject.Manifest.PackageProjectId);
        var secondMod = library.Mods.Single(mod => mod.ModId != installedMod.ModId);
        var snapshot = ArkiveModEditSnapshot.LoadAsync(environment.Edits).GetAwaiter().GetResult();
        Assert(migration.Migrated.Count == 2 && library.Mods.Count == 2 &&
               library.GamePackagesDirectory == Path.GetDirectoryName(installedPath) &&
               installedMod is { Enabled: true, Origin: ArkiveModOrigin.Migrated, Priority: 0 } &&
               installedMod.Manifest.Name == "Installed material mod" &&
               installedMod.Manifest.Author == "Smoke tester" &&
               installedMod.Manifest.Description == "Made before .arkive existed." &&
               installedMod.Bindings.Single().EditId == installedEdit.Document.EditId &&
               !secondMod.Enabled && secondMod.Bindings.Single().EditId == secondEdit.Document.EditId &&
               !snapshot.Find(secondEdit.Document.EditId)!.Included &&
               snapshot.Find(installedEdit.Document.EditId)!.Included &&
               !snapshot.Find(draft.Document.EditId)!.Included &&
               library.FindOwner(draft.Document.EditId) is null &&
               ArkiveModFileService.ReadAsync(environment.Library.GetModFilePath(installedMod)).GetAwaiter().GetResult()
                   .Manifest.Assets.Single().Material!.Scalars.Single().Value == 0.9f &&
               Sha(installedPath) == installedHash,
            "package-project migration did not create bound .arkive mods that mirror installed state");
        var plan = CreateApplyService(environment, Path.GetDirectoryName(installedPath)!).PlanAsync().GetAwaiter().GetResult();
        Assert(plan.CanApply && !plan.HasChanges,
            "the first apply after migration would change game files: " + plan.Summary);
        var again = new ArkiveModMigrationService(context).MigratePackageProjectsAsync().GetAwaiter().GetResult();
        Assert(again.Migrated.Count == 0 && again.Skipped.Count == 2 &&
               environment.Library.LoadAsync().GetAwaiter().GetResult().Mods.Count == 2,
            "running package-project migration twice created duplicate mods");
        environment.Deployment.RestoreAsync(new ArkiveModDeploymentRequest(
                composition.Revision.ModBundleManifestPath, environment.Backups, environment.State))
            .GetAwaiter().GetResult();
    }

    private static void TestArkivePackageProjectAuthoring()
    {
        var root = Path.Combine(fixtureDirectory, "arkive-authoring");
        var packagePath = CreateMaterialClient(root, out _);
        var otherPath = AuthorMaterialMod(root, packagePath, "Other author mod", roughness: 0.2f, blue: null);
        var environment = CreateEnvironment(Path.Combine(root, "workspace"));
        var other = environment.Library.ImportAsync(otherPath).GetAwaiter().GetResult().Entry;
        environment.Library.SetEnabledAsync(other.ModId, true).GetAwaiter().GetResult();
        environment.Library.SetPriorityAsync(other.ModId, 5).GetAwaiter().GetResult();
        var package = environment.Packages.GetOrCreateAsync(
            "material_instance", Path.GetFileName(packagePath), packagePath).GetAwaiter().GetResult();
        package = environment.Packages.UpdateMetadataAsync(
            package.ManifestPath, "My package mod", "Built from a package project.").GetAwaiter().GetResult();
        var draft = CreateMaterialEdit(environment, packagePath, "My glossy draft", 0.8f, null, candidate: true);
        var authoring = new ArkiveModAuthoringService(environment.Library, environment.Edits);
        try
        {
            _ = authoring.SavePackageProjectAsync(package).GetAwaiter().GetResult();
            throw new InvalidOperationException("a draft silently took an asset supplied by another enabled mod");
        }
        catch (ArkiveModRefusalException exception) when (exception.Code == "AUT080")
        {
        }

        environment.Library.SetEnabledAsync(other.ModId, false).GetAwaiter().GetResult();
        var authored = authoring.SavePackageProjectAsync(package).GetAwaiter().GetResult()!;
        Assert(authored is { Enabled: true, Priority: 0, Origin: ArkiveModOrigin.Created } &&
               authored.AuthoredForPackageProjectId == package.Manifest.PackageProjectId &&
               authored.Manifest.Name == "My package mod" &&
               authored.Manifest.Description == "Built from a package project." &&
               authored.Bindings.Single().EditId == draft.Document.EditId &&
               File.Exists(environment.Library.GetModFilePath(authored)),
            "saving a package project's checked drafts did not create its own bound .arkive mod");

        environment.Library.SetEnabledAsync(other.ModId, true).GetAwaiter().GetResult();
        var locallyChanged = environment.Edits.MaterialEdits.LoadEntryAsync(draft.DocumentPath).GetAwaiter().GetResult();
        environment.Edits.MaterialEdits.SaveAsync(locallyChanged with
        {
            Document = locallyChanged.Document with
            {
                Scalars = locallyChanged.Document.Scalars.Select(item => item with { Value = 0.85f }).ToArray(),
            },
        }).GetAwaiter().GetResult();
        var resaved = authoring.SavePackageProjectAsync(package).GetAwaiter().GetResult()!;
        var library = environment.Library.LoadAsync().GetAwaiter().GetResult();
        Assert(resaved.Priority == 0 &&
               ArkiveModFileService.ReadAsync(environment.Library.GetModFilePath(resaved)).GetAwaiter().GetResult()
                   .Manifest.Assets.Single().Material!.Scalars.Single().Value == 0.85f &&
               ArkiveModConflictResolver.Resolve(library.Mods)
                   .OutcomeFor(other.ModId, other.Manifest.Assets.Single().AssetId) == ArkiveModAssetOutcome.Winning,
            "saving local changes raised the authored mod's priority or did not write the change into its .arkive");

        environment.Library.SetEnabledAsync(other.ModId, false).GetAwaiter().GetResult();
        ArkiveModSelectionService.SynchronizeAsync(environment.Library, environment.Edits).GetAwaiter().GetResult();
        var reexported = CreateMaterialEdit(environment, packagePath, "Re-exported draft", 0.95f, null, candidate: true);
        var replaced = authoring.SavePackageProjectAsync(package).GetAwaiter().GetResult()!;
        library = environment.Library.LoadAsync().GetAwaiter().GetResult();
        var snapshot = ArkiveModEditSnapshot.LoadAsync(environment.Edits).GetAwaiter().GetResult();
        Assert(replaced.Bindings.Single().EditId == reexported.Document.EditId &&
               replaced.Manifest.Assets.Single().AssetId == authored.Manifest.Assets.Single().AssetId &&
               replaced.Manifest.Assets.Single().Material!.Scalars.Single().Value == 0.95f &&
               !snapshot.Find(draft.Document.EditId)!.Included &&
               library.FindOwner(draft.Document.EditId) is null,
            "a re-exported draft did not replace the mod's asset in place and retire the previous working copy");
    }

    private static void TestArkiveOutsideChanges()
    {
        var root = Path.Combine(fixtureDirectory, "arkive-outside");
        var packagePath = CreateMaterialClient(root, out var originalHash);
        var arkive = AuthorMaterialMod(root, packagePath, "Gloss from elsewhere", roughness: 0.8f, blue: null);
        var gameDirectory = Path.GetDirectoryName(packagePath)!;

        // An older Arkive with its own data folder installs the mod.
        var old = CreateEnvironment(Path.Combine(root, "old-data"));
        var oldMod = old.Library.ImportAsync(arkive).GetAwaiter().GetResult().Entry;
        old.Library.SetEnabledAsync(oldMod.ModId, true).GetAwaiter().GetResult();
        Assert(CreateApplyService(old, gameDirectory).ApplyAsync().GetAwaiter().GetResult().Succeeded &&
               Sha(packagePath) != originalHash, "the old Arkive did not install the mod");
        var installedHash = Sha(packagePath);

        // A new data folder gets only the .arkive file and sees a changed game file it never touched.
        var fresh = CreateEnvironment(Path.Combine(root, "new-data"));
        var mod = fresh.Library.ImportAsync(arkive).GetAwaiter().GetResult().Entry;
        fresh.Library.SetEnabledAsync(mod.ModId, true).GetAwaiter().GetResult();
        var service = CreateApplyService(fresh, gameDirectory);
        var outside = service.FindOutsideChangesAsync().GetAwaiter().GetResult();
        Assert(outside is [{ GameSha256: var seen }] && seen.Equals(installedHash, StringComparison.OrdinalIgnoreCase),
            "a game file changed by another Arkive data folder was not reported");
        var plan = service.PlanAsync().GetAwaiter().GetResult();
        Assert(!plan.CanApply && plan.BlockingProblems.Any(problem =>
                   problem.Contains("Restore from another Arkive folder", StringComparison.Ordinal)),
            "Apply would have built on a game file changed outside this Arkive: " + plan.Summary);
        var statuses = ArkiveModApplyService.DescribeAsync(fresh.Library, fresh.Library.LoadAsync().GetAwaiter().GetResult(),
            ArkiveModEditSnapshot.LoadAsync(fresh.Edits).GetAwaiter().GetResult(),
            outsideChangedPackages: outside.Select(change => change.PhysicalPackageName).ToArray()).GetAwaiter().GetResult();
        Assert(statuses.Single().State == "Game file changed outside Arkive",
            "the mod list still offered a changed game file as ready to apply");

        var empty = service.PlanRestoreFromOtherFolderAsync(Path.Combine(root, "nowhere")).GetAwaiter().GetResult();
        Assert(empty.Items.Count == 0 && empty.Unresolved.Count == 1, "a folder without backups offered a restore");
        var restorePlan = service.PlanRestoreFromOtherFolderAsync(old.Root).GetAwaiter().GetResult();
        Assert(restorePlan.Items is [{ OriginalSha256: var original }] &&
               original.Equals(originalHash, StringComparison.OrdinalIgnoreCase),
            "the verified original in the old data folder was not found");
        var restored = service.RestoreFromOtherFolderAsync(restorePlan).GetAwaiter().GetResult();
        Assert(Sha(packagePath) == originalHash &&
               File.Exists(Path.Combine(restored.SafetyCopyDirectory, Path.GetFileName(packagePath))) &&
               Sha(Path.Combine(restored.SafetyCopyDirectory, Path.GetFileName(packagePath))) == installedHash,
            "restoring from the old data folder did not put the original back and keep the replaced file");
        Assert(service.FindOutsideChangesAsync().GetAwaiter().GetResult().Count == 0,
            "the restored game file was still reported as changed");
        Assert(service.ApplyAsync().GetAwaiter().GetResult().Succeeded &&
               ReadMaterial(packagePath).ScalarParameters.Single().Value == 0.8f,
            "the new Arkive could not install the mod after the restore");
    }

    private static void TestArkiveClientUpdate()
    {
        var root = Path.Combine(fixtureDirectory, "arkive-update");
        var packagePath = CreateMaterialClient(root, out var originalHash);
        var arkive = AuthorMaterialMod(root, packagePath, "Update-proof gloss", roughness: 0.75f, blue: null);
        var player = CreateEnvironment(Path.Combine(root, "player"));
        var mod = player.Library.ImportAsync(arkive).GetAwaiter().GetResult().Entry;
        player.Library.SetEnabledAsync(mod.ModId, true).GetAwaiter().GetResult();
        var service = CreateApplyService(player, Path.GetDirectoryName(packagePath)!);
        Assert(service.ApplyAsync().GetAwaiter().GetResult().Succeeded &&
               ReadMaterial(packagePath).ScalarParameters.Single().Value == 0.75f,
            "the update fixture mod did not install");

        player.Library.SetEnabledAsync(mod.ModId, false).GetAwaiter().GetResult();
        Assert(service.ApplyAsync().GetAwaiter().GetResult().Succeeded && Sha(packagePath) == originalHash,
            "disabling the mod before the simulated client update did not restore the package");
        var firstEdit = player.Library.LoadAsync().GetAwaiter().GetResult().Find(mod.ModId)!.Bindings.Single().EditId;

        var bytes = File.ReadAllBytes(packagePath);
        Assert(bytes[128] == 0, "the synthetic package fixture no longer has padding before its name table");
        bytes[128] = 0x42;
        File.WriteAllBytes(packagePath, bytes);
        var unchangedAssetHash = Sha(packagePath);
        player.Library.SetEnabledAsync(mod.ModId, true).GetAwaiter().GetResult();
        var plan = service.PlanAsync().GetAwaiter().GetResult();
        Assert(plan.CanApply && plan.Packages is [{ Action: ArkiveModPackageAction.Rebuild }] &&
               plan.Packages[0].Setups.Single().ReplacesBinding?.EditId == firstEdit,
            "an unchanged asset in an updated client was not scheduled for automatic re-setup: " + plan.Summary);
        var rebuilt = service.ApplyAsync(plan.Fingerprint).GetAwaiter().GetResult();
        var binding = player.Library.LoadAsync().GetAwaiter().GetResult().Find(mod.ModId)!.Bindings.Single();
        Assert(rebuilt.Succeeded && ReadMaterial(packagePath).ScalarParameters.Single().Value == 0.75f &&
               binding.EditId != firstEdit &&
               ArkiveModEditSnapshot.LoadAsync(player.Edits).GetAwaiter().GetResult().Find(firstEdit) is null,
            "the .arkive mod was not rebuilt against the updated client with a fresh binding");

        player.Library.SetEnabledAsync(mod.ModId, false).GetAwaiter().GetResult();
        Assert(service.ApplyAsync().GetAwaiter().GetResult().Succeeded && Sha(packagePath) == unchangedAssetHash,
            "the second pre-update restore failed");
        var current = ReadMaterial(packagePath);
        bytes = File.ReadAllBytes(packagePath);
        BinaryPrimitives.WriteSingleLittleEndian(
            bytes.AsSpan(checked((int)(current.SerialOffset + current.ScalarParameters.Single().ValueRelativeOffset)), 4),
            0.6f);
        File.WriteAllBytes(packagePath, bytes);
        Assert(ReadMaterial(packagePath).ScalarParameters.Single().Value == 0.6f,
            "the breaking-update fixture did not change the material default");
        player.Library.SetEnabledAsync(mod.ModId, true).GetAwaiter().GetResult();
        var blocked = service.PlanAsync().GetAwaiter().GetResult();
        Assert(!blocked.CanApply && blocked.BlockingProblems.Any(problem =>
                   problem.Contains("Update for this game version", StringComparison.Ordinal)),
            "a game change to the modded asset was not held for an explicit mod update");
        var updates = service.UpdateModForCurrentClientAsync(mod.ModId).GetAwaiter().GetResult();
        Assert(updates is [{ Succeeded: true, Changed: true }],
            "explicitly updating the mod for the changed client failed: " +
            string.Join(" | ", updates.Select(item => item.Message)));
        var afterUpdate = service.ApplyAsync().GetAwaiter().GetResult();
        var updated = ReadMaterial(packagePath);
        var library = player.Library.LoadAsync().GetAwaiter().GetResult();
        var statuses = ArkiveModApplyService.DescribeAsync(player.Library, library,
            ArkiveModEditSnapshot.LoadAsync(player.Edits).GetAwaiter().GetResult()).GetAwaiter().GetResult();
        Assert(afterUpdate.Succeeded && updated.ScalarParameters.Single().Value == 0.75f &&
               updated.VectorParameters.Single().Blue == 0.25f &&
               !statuses.Single().ModifiedLocally,
            "the updated mod did not keep its change on the new client or reported false local edits");
    }

    private static void TestArkiveModVersionUpdate()
    {
        var root = Path.Combine(fixtureDirectory, "arkive-version-update");
        var packagePath = CreateMaterialClient(root, out _);
        var first = AuthorMaterialMod(root, packagePath, "Versioned gloss", roughness: 0.75f, blue: null);
        var player = CreateEnvironment(Path.Combine(root, "player"));
        var authoring = new ArkiveModAuthoringService(player.Library, player.Edits);
        var mod = authoring.ImportAsync(first).GetAwaiter().GetResult().Entry;
        player.Library.SetEnabledAsync(mod.ModId, true).GetAwaiter().GetResult();
        var service = CreateApplyService(player, Path.GetDirectoryName(packagePath)!);
        Assert(service.ApplyAsync().GetAwaiter().GetResult().Succeeded, "the first mod version did not install");
        var firstEdit = player.Library.LoadAsync().GetAwaiter().GetResult().Find(mod.ModId)!.Bindings.Single().EditId;

        var v1 = ArkiveModFileService.ReadAsync(first).GetAwaiter().GetResult().Manifest;
        var asset = v1.Assets.Single();
        var second = Path.Combine(root, "Versioned gloss 2.arkive");
        ArkiveModFileService.WriteAsync(second, v1 with { Version = "2.0.0", UpdatedAt = v1.UpdatedAt.AddMinutes(1) },
            [new ArkiveModPackAsset(asset with
            {
                Material = asset.Material! with
                {
                    Scalars = asset.Material.Scalars.Select(item => item with { Value = 0.4f }).ToArray(),
                },
            }, [])]).GetAwaiter().GetResult();
        var update = authoring.ImportAsync(second).GetAwaiter().GetResult();
        var plan = service.PlanAsync().GetAwaiter().GetResult();
        Assert(update.UpdatedExistingMod && update.Released is [{ Delete: true }] &&
               ArkiveModEditSnapshot.LoadAsync(player.Edits).GetAwaiter().GetResult().Find(firstEdit) is null &&
               plan.CanApply && plan.Packages is [{ Action: ArkiveModPackageAction.Rebuild, RestoreFirst: true }],
            "importing a newer version of an installed mod did not release its old working copy: " + plan.Summary);
        Assert(service.ApplyAsync(plan.Fingerprint).GetAwaiter().GetResult().Succeeded &&
               ReadMaterial(packagePath).ScalarParameters.Single().Value == 0.4f,
            "the newer mod version was not installed in place of the old one");
        try
        {
            _ = authoring.ImportAsync(first).GetAwaiter().GetResult();
            throw new InvalidOperationException("an older copy silently replaced the library's newer mod");
        }
        catch (ArkiveModRefusalException exception) when (exception.Code == "LIB012")
        {
        }
    }

    private static void TestArkiveFailedSetupKeepsPreviousBuild()
    {
        var root = Path.Combine(fixtureDirectory, "arkive-failed-setup");
        var packagePath = CreateMaterialClient(root, out _);
        var working = AuthorMaterialMod(root, packagePath, "Working gloss", roughness: 0.75f, blue: null);
        var player = CreateEnvironment(Path.Combine(root, "player"));
        var authoring = new ArkiveModAuthoringService(player.Library, player.Edits);
        var mod = authoring.ImportAsync(working).GetAwaiter().GetResult().Entry;
        player.Library.SetEnabledAsync(mod.ModId, true).GetAwaiter().GetResult();
        var service = CreateApplyService(player, Path.GetDirectoryName(packagePath)!);
        Assert(service.ApplyAsync().GetAwaiter().GetResult().Succeeded, "the working mod did not install");
        var installedHash = Sha(packagePath);

        var source = ArkiveModFileService.ReadAsync(working).GetAwaiter().GetResult().Manifest;
        var asset = source.Assets.Single();
        var broken = Path.Combine(root, "Broken target.arkive");
        ArkiveModFileService.WriteAsync(broken, source with { ModId = NewId(), Name = "Broken target" },
            [new ArkiveModPackAsset(asset with
            {
                AssetId = NewId(),
                Target = asset.Target with { ObjectName = "missing_material" },
            }, [])]).GetAwaiter().GetResult();
        var brokenMod = authoring.ImportAsync(broken).GetAwaiter().GetResult().Entry;
        player.Library.SetEnabledAsync(brokenMod.ModId, true).GetAwaiter().GetResult();
        var plan = service.PlanAsync().GetAwaiter().GetResult();
        var result = service.ApplyAsync(plan.Fingerprint).GetAwaiter().GetResult();
        Assert(plan.Packages is [{ Action: ArkiveModPackageAction.Rebuild, RestoreFirst: true }] &&
               !result.Succeeded && result.RestoredPackageCount == 0 &&
               result.Packages.Single().Message.Contains("previous build was reinstalled", StringComparison.Ordinal) &&
               Sha(packagePath) == installedHash,
            "a failed setup left the package without its previously installed mods: " +
            string.Join(" | ", result.Packages.Select(item => item.Message)));
    }


    private static string WriteTextureMod(string path, string name, out ArkiveModFileManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var image = path + ".source.png";
        File.WriteAllBytes(image, Encoding.UTF8.GetBytes(name + Guid.NewGuid()));
        var written = ArkiveModFileService.WriteAsync(path,
                ArkiveModAuthoringService.CreateManifest(NewId(), new ArkiveModMetadata(name, "1.0.0"),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                [new ArkiveModPackAsset(TextureDescriptor("PACKAGE.upk", "body_d"),
                    [new ArkiveModPackFile(ArkiveModFileFormat.ImageRole, image, "edit.png")])])
            .GetAwaiter().GetResult();
        File.Delete(image);
        manifest = written.Manifest;
        return path;
    }

    private static ArkiveModLibraryEntry LibraryEntry(
        string name, bool enabled, int priority, params ArkiveModFileAsset[] assets)
    {
        var manifest = ArkiveModAuthoringService.CreateManifest(
            NewId(), new ArkiveModMetadata(name, "1.0.0"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) with
        {
            Assets = assets,
        };
        return new ArkiveModLibraryEntry(manifest.ModId, name + ".arkive", new string('0', 64), 1,
            DateTimeOffset.UtcNow, enabled, priority, ArkiveModOrigin.Imported, manifest, []);
    }

    private static void TestArkiveSameNamedObjectPaths()
    {
        // pc_dl_av_214 holds both pc_dl_av_214_head_orm and tex.pc_dl_av_214_head_orm.
        var root = new AssetCatalogEntry(47, "head_orm", "VC2NJ32NY12NE7SF0LEHM7EH.head_orm", "Texture2D",
            AssetKind.Texture, 240_640, 17_618_191);
        var grouped = new AssetCatalogEntry(55, "head_orm", "VC2NJ32NY12NE7SF0LEHM7EH.tex.head_orm", "Texture2D",
            AssetKind.Texture, 247_389, 18_888_832);
        var unique = new AssetCatalogEntry(56, "head_d", "VC2NJ32NY12NE7SF0LEHM7EH.tex.head_d", "Texture2D",
            AssetKind.Texture, 1, 2);
        AssetCatalogEntry[] catalogue = [root, grouped, unique];
        Assert(ArkiveModObjectPath.Select(catalogue, "tex.head_orm").Single() == grouped &&
               ArkiveModObjectPath.Select(catalogue, "head_orm").Single() == root &&
               ArkiveModObjectPath.Select(catalogue, "TEX.HEAD_ORM").Single() == grouped &&
               ArkiveModObjectPath.Select(catalogue, "mat.head_orm").Length == 0,
            "object paths did not pick the exact same-named export");
        Assert(ArkiveModObjectPath.IfAmbiguous(catalogue, grouped) == "tex.head_orm" &&
               ArkiveModObjectPath.IfAmbiguous(catalogue, root) == "head_orm" &&
               ArkiveModObjectPath.IfAmbiguous(catalogue, unique) is null,
            "an object path was recorded for a unique name, or missed for a repeated one");
        Assert(ArkiveModObjectPath.IsValid("tex.head_orm", "head_orm") && ArkiveModObjectPath.IsValid("head_orm", "head_orm") &&
               !ArkiveModObjectPath.IsValid("tex.head_d", "head_orm") && !ArkiveModObjectPath.IsValid("tex..head_orm", "head_orm"),
            "object path validation accepted or refused the wrong paths");

        var directory = Path.Combine(fixtureDirectory, "arkive-same-named");
        Directory.CreateDirectory(directory);
        var image = Path.Combine(directory, "edit.png");
        File.WriteAllBytes(image, Enumerable.Range(0, 512).Select(index => (byte)index).ToArray());
        var physical = "VC2NJ32NY12NE7SF0LEHM7EH.upk";
        var first = TextureDescriptor(physical, "head_orm");
        first = first with { Target = first.Target with { ObjectPath = "head_orm" } };
        var second = TextureDescriptor(physical, "head_orm");
        second = second with { Target = second.Target with { ObjectPath = "tex.head_orm" } };
        var plain = TextureDescriptor(physical, "head_d");
        Assert(ArkiveModTargetKey.From(first) != ArkiveModTargetKey.From(second) &&
               ArkiveModTargetKey.From(plain) == ArkiveModTargetKey.From(physical, UnifiedPackageAssetKind.Texture2D, "head_d"),
            "same-named targets shared a key, or a plain target's key changed");
        var manifest = ArkiveModAuthoringService.CreateManifest(
            NewId(), new ArkiveModMetadata("Same-named smoke mod", "1.0.0"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var output = Path.Combine(directory, "same-named.arkive");
        ArkiveModFileService.WriteAsync(output, manifest,
            new[] { first, second, plain }.Select((asset, index) => new ArkiveModPackAsset(asset,
                [new ArkiveModPackFile(ArkiveModFileFormat.ImageRole, image, $"edit-{index}.png")])).ToArray())
            .GetAwaiter().GetResult();
        var read = ArkiveModFileService.ReadAsync(output).GetAwaiter().GetResult();
        Assert(read.Manifest.Assets.Select(asset => asset.Target.ObjectPath)
                   .SequenceEqual(["head_orm", "tex.head_orm", null]),
            ".arkive files did not keep object paths");
        using (var archive = System.IO.Compression.ZipFile.OpenRead(output))
        using (var reader = new StreamReader(archive.GetEntry(ArkiveModFileFormat.ManifestEntryName)!.Open()))
            Assert(System.Text.RegularExpressions.Regex.Matches(reader.ReadToEnd(), "\"objectPath\"").Count == 2,
                "a plain target wrote an object path, so older Arkive versions would see a changed file");

        var broken = TextureDescriptor(physical, "head_orm");
        broken = broken with { Target = broken.Target with { ObjectPath = "tex.head_d" } };
        try
        {
            ArkiveModFileService.WriteAsync(Path.Combine(directory, "broken.arkive"), manifest,
                [new ArkiveModPackAsset(broken, [new ArkiveModPackFile(ArkiveModFileFormat.ImageRole, image, "edit.png")])])
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("an object path naming another object was accepted");
        }
        catch (ArkiveModRefusalException exception) when (exception.Code == "ARK039")
        {
        }
    }

    private static ArkiveModFileAsset TextureDescriptor(string physical, string objectName) => new(
        NewId(), UnifiedPackageAssetKind.Texture2D, objectName + " edit",
        Target(physical.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? physical : physical + ".upk",
            "Texture2D", objectName), [])
    {
        Texture = new ArkiveModTextureData("PF_DXT5", 64, 64, TextureImportAlphaPolicy.PreserveOriginal),
    };

    private static ArkiveModAssetTarget Target(string physical, string className, string objectName) => new(
        "package_alias", physical, className, objectName, 4, new string('A', 64), new string('B', 64));

    private static void ExpectRefusal(string path, string code, string description)
    {
        try
        {
            _ = ArkiveModFileService.ReadAsync(path).GetAwaiter().GetResult();
            throw new InvalidOperationException($".arkive reader accepted a {description}");
        }
        catch (ArkiveModRefusalException exception) when (exception.Code == code)
        {
        }
    }

    private static void ExpectManifestRefusal(ArkiveModFileManifest manifest, string code, string description)
    {
        try
        {
            ArkiveModFileService.ValidateManifest(manifest);
            throw new InvalidOperationException($".arkive validation accepted a {description}");
        }
        catch (ArkiveModRefusalException exception) when (exception.Code == code)
        {
        }
    }

    private static void ReplaceEntry(string archivePath, string entryName, byte[] content)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        zip.GetEntry(entryName)!.Delete();
        using var stream = zip.CreateEntry(entryName).Open();
        stream.Write(content);
    }

    private static void WriteRawArchive(
        string path, ArkiveModFileManifest manifest, params (string Name, byte[] Content)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var stream = zip.CreateEntry(ArkiveModFileFormat.ManifestEntryName).Open())
            JsonSerializer.Serialize(stream, manifest, ManifestJson);
        foreach (var (name, content) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(content);
        }
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string NewId() => Guid.NewGuid().ToString("N");
}
