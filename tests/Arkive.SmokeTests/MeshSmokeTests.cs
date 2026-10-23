namespace Arkive.SmokeTests;

internal static class MeshSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("refreshes stale SkeletalMesh selections from the current export table", TestSkeletalMeshSelectionRefresh),
        new("extracts exact SkeletalMesh material and texture dependencies", TestSkeletalMeshAssetKitDependencies),
        new("round-trips real Lost Ark StaticMesh render data without touching collision", TestRealClientStaticMeshRoundTrip),
        new("reconnoitres and byte-preserves a native SkeletalMesh payload", TestSkeletalMeshReconnaissanceAndNoOp),
        new("classifies skeletons and creates a conservative retarget workspace", TestSkeletonCompatibilityAndRetargetWorkspace),
        new("parses the SkeletalMesh UModel semantic oracle", TestSkeletalMeshOracleParsing),
        new("grows the per-material list after a SkeletalMesh's LODs with its added material slots", TestTrailingMaterialList),
    ];

    private static void TestTrailingMaterialList()
    {
        // As Sympathetic Eternity's pants end: two bone names, three empty lists, one material, then the mesh's last values.
        var tail = new byte[] { 0x7F, 0x80, 0x99, 0x42, 2 };
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(2);
            for (var bone = 0; bone < 2; bone++) { writer.Write(10 + bone); writer.Write(0); writer.Write(bone); }
            writer.Write(0); writer.Write(0); writer.Write(0);
            writer.Write(1); writer.Write(0);
            writer.Write(tail);
        }
        var source = stream.ToArray();
        var grown = SkeletalMeshTopologyCompilerService.TrailingWithMaterials(source, 1, 3);
        Assert(grown is not null && grown.Length == source.Length + 12 &&
               BinaryPrimitives.ReadInt32LittleEndian(grown.AsSpan(4 + 24 + 12)) == 4 &&
               grown.AsSpan(4 + 24 + 16, 16).IndexOfAnyExcept((byte)0) < 0 &&
               grown.AsSpan(grown.Length - tail.Length).SequenceEqual(tail) &&
               grown.AsSpan(0, 4 + 24 + 12).SequenceEqual(source.AsSpan(0, 4 + 24 + 12)),
            "the per-material list did not grow with the added slots");
        Assert(SkeletalMeshTopologyCompilerService.TrailingWithMaterials(source, 2, 3) is null &&
               SkeletalMeshTopologyCompilerService.TrailingWithMaterials(source, 1, 0) is null,
            "a list that does not follow the material count, or no added slot, should leave the bytes as they are");
    }

    private static void TestSkeletalMeshSelectionRefresh()
    {
        var current = new AssetCatalogEntry(
            162, "mn_cuas_05_sk", "mn_cuas_05.mn_cuas_05_sk", "SkeletalMesh3",
            AssetKind.SkeletalMesh, 8_063_990, 24_714_288);
        var unrelated = new AssetCatalogEntry(
            5, "other_sk", "other.other_sk", "SkeletalMesh3",
            AssetKind.SkeletalMesh, 100, 200);
        var resolved = AssetCatalogSelectionResolver.ResolveSkeletalMesh(
            [unrelated, current], "mn_cuas_05_sk",
            preferredExportIndex: 161,
            preferredSerialOffset: 5_881_240,
            preferredSerialSize: 9_158_328);
        Assert(ReferenceEquals(resolved, current),
            "stale SkeletalMesh selection was not refreshed to the current named export");

        var refused = false;
        try
        {
            AssetCatalogSelectionResolver.ResolveSkeletalMesh(
                [current, current with { ExportIndex = 163, SerialOffset = 32_000_000 }],
                "mn_cuas_05_sk", 161, 5_881_240, 9_158_328);
        }
        catch (InvalidDataException)
        {
            refused = true;
        }
        Assert(refused, "ambiguous SkeletalMesh name resolution was not refused");
    }

    private static void TestSkeletalMeshAssetKitDependencies()
    {
        var root = Path.Combine(fixtureDirectory, "skeletal-mesh-asset-kit-dependencies");
        var meshDirectory = Path.Combine(root, "TEST_PKG", "SkeletalMesh3");
        var materialDirectory = Path.Combine(root, "TEST_PKG", "MaterialInstanceConstant");
        var textureDirectory = Path.Combine(root, "TEST_PKG", "Texture2D");
        Directory.CreateDirectory(meshDirectory);
        Directory.CreateDirectory(materialDirectory);
        Directory.CreateDirectory(textureDirectory);

        var gltf = Path.Combine(meshDirectory, "hero.gltf");
        var directMaterial = Path.Combine(materialDirectory, "body_mi.props.txt");
        var directMaterialBinary = Path.Combine(materialDirectory, "body_mi.mat");
        var unrelatedMaterial = Path.Combine(materialDirectory, "unused_mi.props.txt");
        var directTexture = Path.Combine(textureDirectory, "body_d.tga");
        var unrelatedTexture = Path.Combine(textureDirectory, "unused_d.tga");
        File.WriteAllText(gltf, "{\"asset\":{\"version\":\"2.0\"},\"materials\":[{\"name\":\"body_mi\"}]}");
        File.WriteAllText(directMaterial, "Diffuse=Texture2D'TEST_PKG.body_d'");
        File.WriteAllBytes(directMaterialBinary, [1, 2, 3]);
        File.WriteAllText(unrelatedMaterial, "Diffuse=Texture2D'TEST_PKG.unused_d'");
        File.WriteAllBytes(directTexture, [4, 5, 6]);
        File.WriteAllBytes(unrelatedTexture, [7, 8, 9]);

        var targets = GameAssetDependencyResolver.ParseExportTargets(root, "hero",
            [gltf, directMaterial, directMaterialBinary, unrelatedMaterial, directTexture, unrelatedTexture]);
        Assert(targets.Count == 2, "asset-kit dependency parser included unrelated package exports");
        Assert(targets.Any(item => item.ClassName == "MaterialInstanceConstant" && item.ObjectName == "body_mi"),
            "asset-kit dependency parser omitted the mesh's direct material");
        Assert(targets.Any(item => item.ClassName == "Texture2D" && item.ObjectName == "body_d"),
            "asset-kit dependency parser omitted the direct material's Texture2D");
        Assert(targets.All(item => !item.ObjectName.StartsWith("unused", StringComparison.OrdinalIgnoreCase)),
            "asset-kit dependency parser used unsafe filename-family matching");
    }

    private static void TestRealClientStaticMeshRoundTrip()
    {
        var packagePath = Path.Combine(Environment.GetEnvironmentVariable("ARKIVE_TEST_PACKAGE_DIR") ?? string.Empty,
            "542NMO92RQNC2C5F0ORE92HC.upk");
        if (!File.Exists(packagePath)) return;
        var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
        var catalog = new AssetCatalogReader(new UpkPackageReader(), provider).Read(packagePath);
        var asset = catalog.Assets.Single(item => item.ObjectName.Equals(
            "bg_yor_etc_furnace02a_sm_psk", StringComparison.OrdinalIgnoreCase));
        using var packageStream = File.OpenRead(packagePath);
        var packageHash = Convert.ToHexString(SHA256.HashData(packageStream));
        var service = new StaticMeshReconnaissanceService(new UpkPackageReader(), provider);
        var report = service.InspectAsync(new StaticMeshReconnaissanceRequest(
            packagePath, "BG_YOR_ETC_B", asset.ExportIndex, AssetKind.StaticMesh,
            asset.ClassName, asset.ObjectName, asset.SerialOffset, asset.SerialSize, packageHash)
        {
            SkipUmodelOracle = true,
        }).GetAwaiter().GetResult();
        Assert(report.Readiness == StaticMeshReadiness.Editable, "real StaticMesh was not editable");
        Assert(report.NativeLayout is { Lods.Count: 1 }, "real StaticMesh LOD layout was not decoded");
        Assert(report.NativeLayout!.Lods[0].VertexCount == 110, "real StaticMesh vertex count changed");
        Assert(report.NativeLayout.Lods[0].Indices.ElementCount == 228, "real StaticMesh index count changed");
        Assert(report.NativeLayout.Lods[0].TextureCoordinateCount == 2, "real StaticMesh UV count changed");
        Assert(report.NativeLayout.CollisionNodeCount == 16 && report.NativeLayout.CollisionTriangleCount == 72,
            "real StaticMesh collision evidence changed");

        var root = Path.Combine(fixtureDirectory, "real-staticmesh");
        var recon = new StaticMeshReconnaissanceWorkspaceStore(Path.Combine(root, "recon"))
            .SaveAsync(report).GetAwaiter().GetResult();
        var editable = new StaticMeshEditableExportService(new UpkPackageReader(), provider)
            .ExportAsync(new StaticMeshEditableExportRequest(
                report, recon.JsonPath, Path.Combine(root, "editable"))).GetAwaiter().GetResult();
        Assert(File.Exists(editable.GlbPath) && editable.Manifest.Lods[0].VertexCount == 110,
            "real StaticMesh editable GLB was not created");
        var edit = new StaticMeshEditWorkspaceStore(Path.Combine(root, "edits"),
                new UpkPackageReader(), provider)
            .RegisterAsync(editable.ManifestPath, editable.GlbPath, "no-op StaticMesh")
            .GetAwaiter().GetResult();
        Assert(edit.Manifest.Preflight.Readiness == StaticMeshEditReadiness.ReadyForSameTopologyCompilation,
            "no-op StaticMesh did not retain its same-topology route");
        var compiled = new StaticMeshCompilationService(new UpkPackageReader(), provider)
            .CompileAsync(edit.ManifestPath, Path.Combine(root, "compiled")).GetAwaiter().GetResult();
        Assert(compiled.Manifest.DecodeBackPassed && compiled.Manifest.CollisionPreservedFromSource,
            "StaticMesh compilation did not retain decode-back/collision proof");
        Assert(compiled.Manifest.CompiledExportSha256.Equals(
                report.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase),
            "no-op StaticMesh compilation was not byte-identical");

        var nudgedGlb = Path.Combine(root, "furnace-nudged.glb");
        NudgeFirstGlbPosition(editable.GlbPath, nudgedGlb, 0.01f);
        var nudgedEdit = new StaticMeshEditWorkspaceStore(Path.Combine(root, "nudged-edits"),
                new UpkPackageReader(), provider)
            .RegisterAsync(editable.ManifestPath, nudgedGlb, "nudged StaticMesh")
            .GetAwaiter().GetResult();
        Assert(nudgedEdit.Manifest.Preflight.Readiness ==
               StaticMeshEditReadiness.ReadyForSameTopologyCompilation,
            "position-edited StaticMesh did not retain its same-topology route");
        var nudgedCompiled = new StaticMeshCompilationService(new UpkPackageReader(), provider)
            .CompileAsync(nudgedEdit.ManifestPath, Path.Combine(root, "nudged-compiled"))
            .GetAwaiter().GetResult();
        Assert(!nudgedCompiled.Manifest.CompiledExportSha256.Equals(
                report.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase),
            "position-edited StaticMesh unexpectedly retained source bytes");
        var staged = new SkeletalMeshStagedUpkService(new UpkPackageReader(), provider)
            .BuildSerializedExportAsync(new SerializedExportStagedUpkRequest(
                packagePath, packageHash, Path.GetFileName(packagePath), "BG_YOR_ETC_B",
                asset.ExportIndex, asset.ClassName, asset.ObjectName, asset.SerialOffset, asset.SerialSize,
                report.ExportPayloadSha256, nudgedCompiled.CompiledExportPath,
                nudgedCompiled.Manifest.CompiledExportSha256, nudgedCompiled.ManifestPath,
                Path.Combine(root, "nudged-staged"))).GetAwaiter().GetResult();
        Assert(staged.Manifest.ExpectedLogicalImageMatched && staged.Manifest.OutputContainsReplacement &&
               staged.Manifest.AllUnrelatedLogicalBytesIdentical,
            "same-size StaticMesh staged-UPK proof was incomplete");
        Assert(File.Exists(staged.OutputUpkPath), "same-size StaticMesh staged UPK was not created");

        var topologyGlb = Path.Combine(root, "furnace-extra-triangle.glb");
        AppendDuplicateTriangleToFirstPrimitive(editable.GlbPath, topologyGlb);
        var topologyEdit = new StaticMeshEditWorkspaceStore(Path.Combine(root, "topology-edits"),
                new UpkPackageReader(), provider)
            .RegisterAsync(editable.ManifestPath, topologyGlb, "topology StaticMesh")
            .GetAwaiter().GetResult();
        Assert(topologyEdit.Manifest.Preflight.Readiness == StaticMeshEditReadiness.ReadyForTopologyCompilation &&
               topologyEdit.Manifest.Preflight.TopologyChanged,
            "triangle-edited StaticMesh did not select the topology compiler");
        var topologyCompiled = new StaticMeshCompilationService(new UpkPackageReader(), provider)
            .CompileAsync(topologyEdit.ManifestPath, Path.Combine(root, "topology-compiled"))
            .GetAwaiter().GetResult();
        Assert(topologyCompiled.Manifest.CompiledSerialSize != topologyCompiled.Manifest.SourceSerialSize &&
               topologyCompiled.Manifest.DecodeBackPassed && topologyCompiled.Manifest.CollisionPreservedFromSource,
            "topology StaticMesh compilation did not produce a verified variable-size export");
        var topologyStaged = new SkeletalMeshStagedUpkService(new UpkPackageReader(), provider)
            .BuildVariableSerializedExportAsync(new VariableSerializedExportStagedUpkRequest(
                packagePath, packageHash, Path.GetFileName(packagePath), "BG_YOR_ETC_B",
                asset.ExportIndex, asset.ClassName, asset.ObjectName, asset.SerialOffset, asset.SerialSize,
                report.ExportPayloadSha256, topologyCompiled.CompiledExportPath,
                topologyCompiled.Manifest.CompiledExportSha256, topologyCompiled.ManifestPath,
                report.NativeDataRelativeOffset!.Value, Path.Combine(root, "topology-staged")))
            .GetAwaiter().GetResult();
        Assert(topologyStaged.Manifest.AppendAndRetargetReplacement &&
               topologyStaged.Manifest.TargetNativeDecodePassed &&
               topologyStaged.Manifest.OriginalExportBytesRetained &&
               topologyStaged.Manifest.UnrelatedExportsEquivalent,
            "variable-size StaticMesh staged-UPK proof was incomplete");

        var landmark = catalog.Assets.Single(item => item.ObjectName.Equals(
            "bg_yor_etc_landmark01_sm", StringComparison.OrdinalIgnoreCase));
        var landmarkReport = service.InspectAsync(new StaticMeshReconnaissanceRequest(
            packagePath, "BG_YOR_ETC_B", landmark.ExportIndex, AssetKind.StaticMesh,
            landmark.ClassName, landmark.ObjectName, landmark.SerialOffset, landmark.SerialSize, packageHash)
        {
            SkipUmodelOracle = true,
        }).GetAwaiter().GetResult();
        Assert(landmarkReport.NativeLayout is { CollisionTriangleCount: 4635 },
            "large StaticMesh collision tree was not decoded");
        Assert(landmarkReport.NativeLayout!.Lods[0].VertexCount == 4165,
            "large StaticMesh render geometry was not decoded");
    }

    private static void TestSkeletalMeshReconnaissanceAndNoOp()
    {
        var meshClientPackages = Path.Combine(
            fixtureDirectory, "mesh-client", "EFGame", "ReleasePC", "Packages");
        Directory.CreateDirectory(meshClientPackages);
        var path = Path.Combine(meshClientPackages, "skeletal-mesh-recon.upk");
        var range = WriteSkeletalMeshReconPackage(path);
        RewriteAsSingleCompressedPackage(path);
        var report = new SkeletalMeshReconnaissanceService()
            .InspectAsync(new SkeletalMeshReconnaissanceRequest(
                path, "skeletal-mesh-recon", 1, AssetKind.SkeletalMesh,
                "SkeletalMesh", "TestMesh", range.SerialOffset, range.SerialSize))
            .GetAwaiter().GetResult();
        Assert(report.Readiness == SkeletalMeshReadiness.NoOpReady,
            "synthetic SkeletalMesh was not marked no-op ready");
        Assert(report.NativeExportIndex == 1, "native SkeletalMesh export index was not resolved");
        Assert(report.PropertyStreamRelativeOffset == 4,
            "SkeletalMesh root property preamble was not retained");
        Assert(report.Properties.Any(property => property.Name == "lodinfo"),
            "SkeletalMesh root LODInfo property was not parsed");
        Assert(report.NativeDataRelativeOffset is > 4,
            "SkeletalMesh native-data boundary was not proven");
        Assert(report.NativeDataSha256?.Length == 64, "SkeletalMesh native region was not hashed");
        Assert(report.NativeLayout is { BoneCount: 2 } && report.NativeLayout.Lods.Count == 1,
            "SkeletalMesh native skeleton/LOD layout was not parsed");
        Assert(report.NativeLayout!.Lods[0].VertexBuffer is { ElementCount: 3, ElementSize: 32 },
            "SkeletalMesh GPU vertex buffer was not inventoried");
        Assert(report.DeformationDependencies is
            {
                ClothState: SkeletalMeshDependencyState.None,
                MorphState: SkeletalMeshDependencyState.None,
                TopologyPreservingSupported: true,
                TopologyChangingSupported: true,
            }, "SkeletalMesh dependency audit did not prove the dependency-free synthetic mesh");
        Assert(report.Issues.Any(issue => issue.Code == "SKM300"),
            "missing semantic oracle was not recorded explicitly");

        var skeletalMeshWorkspace = Path.Combine(fixtureDirectory, "skeletal-mesh-recon-workspace");
        var entry = new SkeletalMeshReconnaissanceWorkspaceStore(skeletalMeshWorkspace)
            .SaveAsync(report).GetAwaiter().GetResult();
        Assert(File.Exists(entry.JsonPath) && File.Exists(entry.TextPath),
            "SkeletalMesh reconnaissance reports were not written");
        var result = new SkeletalMeshNoOpService()
            .ReconstructAsync(new SkeletalMeshNoOpRequest(
                report, entry.JsonPath, Path.Combine(entry.DirectoryPath, "no-op-proof")))
            .GetAwaiter().GetResult();
        Assert(result.Manifest.ByteIdentical && result.Manifest.FirstDifferenceOffset == -1,
            "SkeletalMesh no-op export was not byte-identical");
        Assert(result.Manifest.SourceExportSha256 == result.Manifest.ReconstructedExportSha256,
            "SkeletalMesh no-op hashes differ");
        Assert(result.Manifest.NativeParserProfile == report.NativeLayout.ParserProfile &&
               result.Manifest.VerifiedNativeRangeCount == report.NativeLayout.Ranges.Count,
            "SkeletalMesh no-op did not record the independently verified native ranges");
        Assert(new FileInfo(result.ExportPath).Length == range.SerialSize,
            "SkeletalMesh no-op export length changed");

        var editable = new SkeletalMeshEditableExportService()
            .ExportAsync(new SkeletalMeshEditableExportRequest(
                report, entry.JsonPath, Path.Combine(entry.DirectoryPath, "editable-glb")))
            .GetAwaiter().GetResult();
        Assert(File.Exists(editable.GlbPath) && File.Exists(editable.ManifestPath),
            "editable SkeletalMesh GLB artifacts were not written");
        Assert(editable.Manifest.Lods is [{ VertexCount: 3, IndexCount: 3, NativeBoneIndexWidth: 1 }],
            "editable SkeletalMesh manifest lost native topology/profile evidence");
        Assert(!editable.Manifest.Lods[0].WindingReversedForGltf &&
               editable.Manifest.Lods[0].FaceOrientationTriangleCount == 1 &&
               editable.Manifest.Lods[0].OpposedFaceNormalTriangleCount == 0,
            "editable SkeletalMesh GLB did not retain validated native face winding");
        Assert(editable.Manifest.BoneCount == 2 && editable.Manifest.GlbInspectionErrors == 0,
            "editable SkeletalMesh GLB did not retain a valid reference skeleton");
        Assert(editable.Manifest.Lods[0].StableVertexIdSha256.Length == 64 &&
               editable.Manifest.Lods[0].StableVertexIdUvSha256.Length == 64 &&
               editable.Manifest.Lods[0].StableVertexIdTextureCoordinate == 1 &&
               editable.Manifest.Lods[0].DecodedPositionSha256.Length == 64,
            "editable SkeletalMesh semantic hashes were not recorded");
        var editableWorkspace = new SkeletalMeshEditableWorkspaceStore(skeletalMeshWorkspace);
        var editableSnapshot = editableWorkspace.LoadAsync().GetAwaiter().GetResult();
        Assert(editableSnapshot.Entries is [{ IsCurrentSchema: true, GlbExists: true, GlbLengthMatches: true }] &&
               editableSnapshot.Issues.Count == 0,
            "durable editable SkeletalMesh exports were not rediscovered");
        var editedGlb = Path.Combine(entry.DirectoryPath, "synthetic-position-edit.glb");
        WriteSkeletalMeshPositionEdit(editable.GlbPath, editedGlb, 0.025f);
        var preflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(editableSnapshot.Entries[0], editedGlb)
            .GetAwaiter().GetResult();
        Assert(preflight.Readiness == SkeletalMeshEditReadiness.ReadyForPositionTangentEncoding &&
               preflight.ChangedPositionVertexCount == 1 && preflight.ErrorCount == 0,
            "topology-preserving edited SkeletalMesh GLB did not pass semantic preflight");
        Assert(preflight.SchemaVersion == SkeletalMeshEditPreflightService.CurrentSchemaVersion &&
               preflight.SkeletonCompatibility is
               {
                   Compatibility: SkeletalMeshSkeletonCompatibilityKind.ExactTargetSkeleton,
                   ExactSkeletonIdentity: true,
                   MappedSourceWeightPercent: > 99.999,
               } &&
               preflight.AnimationValidation is
               {
                   Passed: true,
                   StaticOnly: false,
                   UsesGameAnimationClips: false,
                   PoseCount: 1,
                   NonFiniteCandidateVertexCount: 0,
               } && preflight.Issues.Any(issue => issue.Code == "SMA099"),
            "topology-preserving mesh did not retain its animation-aware bind-space proof");
        var topologyEdit = Path.Combine(entry.DirectoryPath, "synthetic-topology-edit.glb");
        WriteSkeletalMeshExpandedTopology(editable.GlbPath, topologyEdit);
        var topologyPreflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(editableSnapshot.Entries[0], topologyEdit)
            .GetAwaiter().GetResult();
        Assert(topologyPreflight.Readiness == SkeletalMeshEditReadiness.ReadyForTopologyCompilation &&
               topologyPreflight.ImportProfile == SkeletalMeshImportProfile.ReplaceLod0AndDuplicate &&
               topologyPreflight.Topology is
               {
                   CandidateVertexCount: 4,
                   CandidateTriangleCount: 2,
                   TargetLods.Count: 1,
                   MaximumInfluencesPerVertex: 1,
               } && topologyPreflight.ErrorCount == 0 &&
               topologyPreflight.AnimationValidation is { Passed: true, StaticOnly: false, PoseCount: 1 } &&
               topologyPreflight.Issues.Any(issue => issue.Code == "SMT099"),
            "changed SkeletalMesh topology did not pass the bounded LOD0 replacement contract: " +
            string.Join(" | ", topologyPreflight.Issues.Select(issue => $"{issue.Code}:{issue.Message}")));
        var blenderStyleEdit = Path.Combine(entry.DirectoryPath, "synthetic-blender-style-topology-edit.glb");
        WriteSkeletalMeshBlenderStyleExport(topologyEdit, blenderStyleEdit);
        var blenderStylePreflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(editableSnapshot.Entries[0], blenderStyleEdit)
            .GetAwaiter().GetResult();
        Assert(blenderStylePreflight.Readiness == SkeletalMeshEditReadiness.ReadyForTopologyCompilation &&
               blenderStylePreflight.ErrorCount == 0 &&
               new[] { "SMI038", "SMT023", "SMT024" }.All(code => blenderStylePreflight.Issues.Any(issue =>
                   issue.Code == code && issue.Severity == SkeletalMeshEditIssueSeverity.Warning)),
            "a shape key, an extra UV map or missing tangents stopped a topology edit: " +
            string.Join(" | ", blenderStylePreflight.Issues.Select(issue => $"{issue.Code}:{issue.Message}")));
        var stableIdentityEdit = Path.Combine(entry.DirectoryPath, "synthetic-no-stable-identity.glb");
        WriteSkeletalMeshStableIdentityRemoval(editable.GlbPath, stableIdentityEdit);
        var stableIdentityPreflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(editableSnapshot.Entries[0], stableIdentityEdit)
            .GetAwaiter().GetResult();
        Assert(stableIdentityPreflight.Readiness == SkeletalMeshEditReadiness.ReadyForTopologyCompilation &&
               stableIdentityPreflight.ImportProfile == SkeletalMeshImportProfile.ReplaceLod0AndDuplicate &&
               stableIdentityPreflight.Topology is { CandidateVertexCount: 3, CandidateTriangleCount: 1 },
            "topology reader did not recover a valid Blender-style GLB without stable source vertex IDs");
        var degenerateTopologyEdit = Path.Combine(entry.DirectoryPath, "synthetic-degenerate-topology-edit.glb");
        WriteSkeletalMeshIndexEdit(editable.GlbPath, degenerateTopologyEdit);
        var degeneratePreflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(editableSnapshot.Entries[0], degenerateTopologyEdit)
            .GetAwaiter().GetResult();
        Assert(degeneratePreflight.Readiness == SkeletalMeshEditReadiness.Refused &&
               degeneratePreflight.ImportProfile == SkeletalMeshImportProfile.ReplaceLod0AndDuplicate &&
               degeneratePreflight.Issues.Any(issue => issue.Code == "SMT030"),
            "topology preflight accepted a degenerate candidate triangle");
        var materialEdit = Path.Combine(entry.DirectoryPath, "synthetic-material-edit.glb");
        WriteSkeletalMeshMaterialRename(editable.GlbPath, materialEdit);
        var materialPreflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(editableSnapshot.Entries[0], materialEdit)
            .GetAwaiter().GetResult();
        Assert(materialPreflight.Readiness == SkeletalMeshEditReadiness.Refused &&
               materialPreflight.ImportProfile == SkeletalMeshImportProfile.ReplaceLod0AndDuplicate &&
               materialPreflight.Issues.Any(issue => issue.Code == "SMT013"),
            "topology preflight accepted a material that cannot map to an original slot");
        var weightEdit = Path.Combine(entry.DirectoryPath, "synthetic-weight-sum-edit.glb");
        WriteSkeletalMeshWeightSumEdit(editable.GlbPath, weightEdit, 0.5f);
        var weightPreflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(editableSnapshot.Entries[0], weightEdit)
            .GetAwaiter().GetResult();
        Assert(weightPreflight.Readiness == SkeletalMeshEditReadiness.Refused &&
               weightPreflight.ImportProfile == SkeletalMeshImportProfile.ReplaceLod0AndDuplicate &&
               weightPreflight.Issues.Any(issue => issue.Code == "SMT027"),
            "topology preflight accepted grossly unnormalized skin weights");
        var collapsedJointsEdit = Path.Combine(entry.DirectoryPath, "synthetic-collapsed-joints-edit.glb");
        WriteSkeletalMeshJointCollapseEdit(editable.GlbPath, collapsedJointsEdit);
        var collapsedJointsPreflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(editableSnapshot.Entries[0], collapsedJointsEdit)
            .GetAwaiter().GetResult();
        Assert(collapsedJointsPreflight.Readiness == SkeletalMeshEditReadiness.Refused &&
               collapsedJointsPreflight.ImportProfile == SkeletalMeshImportProfile.ReplaceLod0AndDuplicate &&
               collapsedJointsPreflight.AnimationValidation is
               {
                   Passed: false,
                   MissingSignificantCandidateBoneCount: > 0,
               } &&
               collapsedJointsPreflight.Issues.Any(issue => issue.Code is "SMA010" or "SMA011"),
            "animation-aware preflight accepted a normalized mesh whose child influences collapsed to root");
        var skeletonEdit = Path.Combine(entry.DirectoryPath, "synthetic-skeleton-edit.glb");
        WriteSkeletalMeshInverseBindEdit(editable.GlbPath, skeletonEdit, 0.1f);
        var skeletonPreflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(editableSnapshot.Entries[0], skeletonEdit)
            .GetAwaiter().GetResult();
        Assert(skeletonPreflight.Readiness == SkeletalMeshEditReadiness.Refused &&
               skeletonPreflight.SkeletonCompatibility is
               {
                   Compatibility: SkeletalMeshSkeletonCompatibilityKind.SameNamedHierarchyAlteredBindPose,
                   ExactSkeletonIdentity: false,
               } && skeletonPreflight.Issues.Any(issue => issue.Code == "SKC001"),
            "mesh preflight accepted a changed reference skeleton bind matrix");
        var blenderDriftEdit = Path.Combine(entry.DirectoryPath, "synthetic-blender-bind-drift.glb");
        WriteSkeletalMeshInverseBindEdit(editable.GlbPath, blenderDriftEdit, 5e-4f);
        var blenderDriftPreflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(editableSnapshot.Entries[0], blenderDriftEdit)
            .GetAwaiter().GetResult();
        Assert(blenderDriftPreflight.SkeletonCompatibility is { ExactSkeletonIdentity: true } &&
               !blenderDriftPreflight.Issues.Any(issue => issue.Code == "SKC001"),
            "mesh preflight refused the bind-matrix rounding Blender adds on an untouched import/export");

        var clothReconPath = Path.Combine(entry.DirectoryPath, "synthetic-cloth-recon.json");
        var clothProperty = new SkeletalMeshPropertyEvidence(
            0, 0, "clothingassets", "ArrayProperty", 8, 0, null, 0,
            "0100000001000000", "0100000001000000");
        var clothDependencies = report.DeformationDependencies! with
        {
            ClothState = SkeletalMeshDependencyState.ActiveVertexBoundData,
            ActiveClothReferenceCount = 1,
            TopologyChangingSupported = false,
            TopologyChangingReason = "Synthetic active cloth mapping.",
            ClothBindings =
            [
                new SkeletalMeshClothBindingEvidence(
                    "clothingassets", "ArrayProperty", 1, [1], ["1:synthetic"], true,
                    "Synthetic active object reference."),
            ],
        };
        var clothReport = report with
        {
            Properties = report.Properties.Append(clothProperty).ToArray(),
            DeformationDependencies = clothDependencies,
        };
        File.WriteAllText(clothReconPath, JsonSerializer.Serialize(clothReport));
        var clothManifest = editable.Manifest with { ReconnaissanceJsonPath = clothReconPath };
        var clothSource = editableSnapshot.Entries[0] with { Manifest = clothManifest };
        var clothPreflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(clothSource, topologyEdit).GetAwaiter().GetResult();
        Assert(clothPreflight.Readiness == SkeletalMeshEditReadiness.Refused &&
               clothPreflight.Topology is { SourceHasClothData: true } &&
               clothPreflight.Issues.Any(issue => issue.Code == "SMT050"),
            "topology preflight accepted a source with a non-null clothing asset reference");

        var inactiveClothDependencies = clothDependencies with
        {
            ClothState = SkeletalMeshDependencyState.InactivePlaceholder,
            ActiveClothReferenceCount = 0,
            TopologyChangingSupported = true,
            TopologyChangingReason = "Only null placeholders are serialized.",
            ClothBindings =
            [
                new SkeletalMeshClothBindingEvidence(
                    "clothingassets", "ArrayProperty", 2, [0, 0], [], false,
                    "Two null placeholder references."),
            ],
        };
        var inactiveClothReconPath = Path.Combine(entry.DirectoryPath, "synthetic-inactive-cloth-recon.json");
        File.WriteAllText(inactiveClothReconPath, JsonSerializer.Serialize(report with
        {
            DeformationDependencies = inactiveClothDependencies,
        }));
        var inactiveClothSource = editableSnapshot.Entries[0] with
        {
            Manifest = editable.Manifest with { ReconnaissanceJsonPath = inactiveClothReconPath },
        };
        var inactiveClothPreflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(inactiveClothSource, topologyEdit).GetAwaiter().GetResult();
        Assert(inactiveClothPreflight.Readiness == SkeletalMeshEditReadiness.ReadyForTopologyCompilation &&
               inactiveClothPreflight.Topology is { SourceHasClothData: false } &&
               inactiveClothPreflight.Issues.Any(issue => issue.Code == "SMD102"),
            "topology preflight treated null clothing placeholders as an active cloth mapping");

        var namedFaceSource = editableSnapshot.Entries[0] with
        {
            Manifest = editable.Manifest with { ObjectName = "pc_wr_f_00_face_sk" },
        };
        var namedFacePreflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(namedFaceSource, topologyEdit).GetAwaiter().GetResult();
        Assert(namedFacePreflight.Readiness == SkeletalMeshEditReadiness.ReadyForTopologyCompilation &&
               namedFacePreflight.Issues.All(issue => issue.Code != "SMT051"),
            "mesh naming alone still produced a false morph-target refusal");

        var morphDependencies = report.DeformationDependencies! with
        {
            MorphState = SkeletalMeshDependencyState.ActiveVertexBoundData,
            MorphTargetSetCount = 1,
            MorphTargetCount = 1,
            MorphDeltaCount = 1,
            TopologyChangingSupported = false,
            TopologyChangingReason = "Synthetic external morph delta mapping.",
            MorphSets =
            [
                new SkeletalMeshMorphSetEvidence(
                    2, "synthetic_morphtargetset", "synthetic.synthetic_morphtargetset",
                    report.NativeExportIndex, true, 1, 1,
                    [new SkeletalMeshMorphTargetEvidence(
                        3, "synthetic_morph", "synthetic.synthetic_morphtargetset.synthetic_morph",
                        44, true, 1, [new SkeletalMeshMorphLodEvidence(0, 1, 3, 2, true)],
                        "Synthetic decoded delta.")]),
            ],
        };
        var morphReconPath = Path.Combine(entry.DirectoryPath, "synthetic-morph-recon.json");
        File.WriteAllText(morphReconPath, JsonSerializer.Serialize(report with
        {
            ObjectName = "pc_wr_f_00_face_sk",
            DeformationDependencies = morphDependencies,
        }));
        var morphSource = editableSnapshot.Entries[0] with
        {
            Manifest = editable.Manifest with
            {
                ObjectName = "pc_wr_f_00_face_sk",
                ReconnaissanceJsonPath = morphReconPath,
            },
        };
        var morphPreflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(morphSource, topologyEdit).GetAwaiter().GetResult();
        Assert(morphPreflight.Readiness == SkeletalMeshEditReadiness.Refused &&
               morphPreflight.Topology is { SourceMayHaveLinkedMorphTargets: true } &&
               morphPreflight.Issues.Any(issue => issue.Code == "SMT051"),
            "topology preflight accepted a proven active morph-target dependency");
        var preservingMorphPreflight = new SkeletalMeshEditPreflightService()
            .InspectAsync(morphSource, editedGlb).GetAwaiter().GetResult();
        Assert(preservingMorphPreflight.Readiness == SkeletalMeshEditReadiness.ReadyForPositionTangentEncoding &&
               preservingMorphPreflight.Issues.Any(issue => issue.Code == "SMD101"),
            "topology-preserving preflight did not explicitly preserve linked morph targets");

        var syntheticMorphPayload = new byte[44];
        BinaryPrimitives.WriteInt32LittleEndian(syntheticMorphPayload.AsSpan(0), 123);
        BinaryPrimitives.WriteInt32LittleEndian(syntheticMorphPayload.AsSpan(4), 0); // None name.
        BinaryPrimitives.WriteInt32LittleEndian(syntheticMorphPayload.AsSpan(8), 0);
        BinaryPrimitives.WriteInt32LittleEndian(syntheticMorphPayload.AsSpan(12), 1); // LOD count.
        BinaryPrimitives.WriteInt32LittleEndian(syntheticMorphPayload.AsSpan(16), 1); // Delta count.
        BinaryPrimitives.WriteInt32LittleEndian(syntheticMorphPayload.AsSpan(36), 2); // Source vertex.
        BinaryPrimitives.WriteInt32LittleEndian(syntheticMorphPayload.AsSpan(40), 3); // Base vertices.
        Assert(SkeletalMeshDeformationDependencyService.TryParseMorphTarget(
                   syntheticMorphPayload, [new UnrealNameEntry(0, "None", 0)],
                   out var decodedMorphLods, out var morphRefusal) &&
               decodedMorphLods is [{ DeltaCount: 1, BaseMeshVertexCount: 3,
                   MaximumSourceVertexIndex: 2, SourceIndicesInRange: true }],
            "UE3 MorphTarget delta parser rejected a bounded valid payload: " + morphRefusal);
        var editedWorkspace = new SkeletalMeshEditWorkspaceStore(
            Path.Combine(fixtureDirectory, "skeletal-mesh-edits"));
        var registered = editedWorkspace.RegisterAsync(
                editableSnapshot.Entries[0], editedGlb, "Synthetic position edit")
            .GetAwaiter().GetResult();
        var registeredSnapshot = editedWorkspace.LoadAsync().GetAwaiter().GetResult();
        Assert(registered.Preflight.Readiness == SkeletalMeshEditReadiness.ReadyForPositionTangentEncoding &&
               registeredSnapshot.Entries is [{ Manifest.DisplayName: "Synthetic position edit" }] &&
               registeredSnapshot.Issues.Count == 0,
            "edited SkeletalMesh derivative was not registered durably");
        var topologyWorkspace = new SkeletalMeshEditWorkspaceStore(
            Path.Combine(fixtureDirectory, "skeletal-mesh-topology-edits"));
        var registeredTopology = topologyWorkspace.RegisterAsync(
                editableSnapshot.Entries[0], topologyEdit, "Synthetic topology replacement")
            .GetAwaiter().GetResult();
        Assert(registeredTopology.Preflight.Readiness == SkeletalMeshEditReadiness.ReadyForTopologyCompilation &&
               registeredTopology.Entry.Manifest.Preflight.Topology is { TargetLods.Count: 1 } &&
               File.Exists(registeredTopology.Entry.EditedGlbPath),
            "topology-capable edited SkeletalMesh was not registered durably");
        try
        {
            _ = new SkeletalMeshNativeEncodingService()
                .EncodeAsync(new SkeletalMeshNativeEncodingRequest(
                    registeredTopology.Entry, Path.Combine(entry.DirectoryPath, "invalid-same-size-topology-encoding")))
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("topology replacement entered the legacy same-length encoder");
        }
        catch (SkeletalMeshNativeEncodingRefusalException exception)
        {
            Assert(exception.Code == "SMN008",
                "topology replacement was not stopped at the explicit native-compiler gate");
        }
        var topologyCompilation = new SkeletalMeshTopologyCompilerService()
            .CompileAsync(new SkeletalMeshTopologyCompilationRequest(
                registeredTopology.Entry, Path.Combine(entry.DirectoryPath, "topology-compilation")))
            .GetAwaiter().GetResult();
        Assert(topologyCompilation.Manifest.DecodeBackPassed &&
               topologyCompilation.Manifest.PackageIntegrationEnabled &&
               topologyCompilation.Manifest.PrefixPreservedExceptBounds &&
               topologyCompilation.Manifest.TrailingBytesPreserved &&
               topologyCompilation.Manifest.Lods is
               [{ CandidateVertexCount: 4, CompiledVertexCount: 4, CandidateTriangleCount: 2,
                   CompiledTriangleCount: 2, SectionCount: 1, ChunkCount: 1,
                   TriangleMaterialTopologyMatches: true, JointsDecodeExactly: true }] &&
               topologyCompilation.Manifest.CompiledSerialSize > range.SerialSize &&
               topologyCompilation.Manifest.SerialSizeDelta > 0 &&
               File.Exists(topologyCompilation.CompiledExportPath) &&
               File.Exists(topologyCompilation.DecodedProofGlbPath),
            "topology compiler did not produce and independently decode a variable-size native LOD: " +
            JsonSerializer.Serialize(topologyCompilation.Manifest));
        var blenderStyleCompilation = new SkeletalMeshTopologyCompilerService()
            .CompileAsync(new SkeletalMeshTopologyCompilationRequest(
                topologyWorkspace.RegisterAsync(editableSnapshot.Entries[0], blenderStyleEdit, "Blender-style topology")
                    .GetAwaiter().GetResult().Entry,
                Path.Combine(entry.DirectoryPath, "blender-style-topology-compilation")))
            .GetAwaiter().GetResult();
        Assert(blenderStyleCompilation.Manifest.DecodeBackPassed &&
               blenderStyleCompilation.Manifest.Lods is [{ CompiledTriangleCount: 2, TriangleMaterialTopologyMatches: true }],
            "the compiler did not fit a Blender-style export to the game mesh's UV streams");
        var loadedTopologyCompilation = SkeletalMeshTopologyCompilerService
            .LoadManifestAsync(topologyCompilation.ManifestPath).GetAwaiter().GetResult();
        Assert(loadedTopologyCompilation.CompilationId == topologyCompilation.Manifest.CompilationId,
            "topology compiler manifest did not pass its schema/identity reload gate");
        var topologyStaged = new SkeletalMeshStagedUpkService()
            .BuildTopologyAsync(new SkeletalMeshTopologyStagedUpkRequest(
                topologyCompilation.ManifestPath,
                Path.Combine(entry.DirectoryPath, "topology-staged-upk")))
            .GetAwaiter().GetResult();
        Assert(topologyStaged.Manifest.AppendAndRetargetReplacement &&
               topologyStaged.Manifest.ExportTablePatched &&
               topologyStaged.Manifest.OriginalExportBytesRetained &&
               topologyStaged.Manifest.TargetExportRetargeted &&
               topologyStaged.Manifest.ReplacementExportBytesVerified &&
               topologyStaged.Manifest.AllUnplannedSourceBytesIdentical &&
               topologyStaged.Manifest.UnrelatedExportsEquivalent &&
               topologyStaged.Manifest.AssetCatalogueShapeRetained &&
               topologyStaged.Manifest.ExpectedLogicalImageMatched &&
               topologyStaged.Manifest.StructuralValidationPassed &&
               topologyStaged.Manifest.TargetNativeDecodePassed &&
               topologyStaged.Manifest.UmodelPreviewReady &&
               !topologyStaged.Manifest.DeploymentReady &&
               topologyStaged.Manifest.AppendedSerialSize == topologyCompilation.Manifest.CompiledSerialSize &&
               topologyStaged.Manifest.AppendedSerialOffset >=
                   topologyStaged.Manifest.SourceLogicalPackageBytes &&
               File.Exists(topologyStaged.OutputUpkPath),
            "variable-size topology export was not appended, retargeted, and decoded from a staged UPK: " +
            JsonSerializer.Serialize(topologyStaged.Manifest));
        var topologyVerification = SkeletalMeshStagedUpkService
            .VerifyTopologyOutputAsync(topologyStaged.ManifestPath).GetAwaiter().GetResult();
        Assert(topologyVerification.OutputUpkPath == topologyStaged.OutputUpkPath &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) ==
                   topologyStaged.Manifest.SourcePackageSha256,
            "topology staged-UPK verification lost its artifact or changed the source package");
        var topologyCatalog = new AssetCatalogReader().Read(topologyStaged.OutputUpkPath);
        var topologyTarget = topologyCatalog.Exports.Single(item =>
            item.TableIndex == topologyCompilation.Manifest.NativeExportIndex);
        Assert(topologyTarget.SerialOffset == topologyStaged.Manifest.AppendedSerialOffset &&
               topologyTarget.SerialSize == topologyStaged.Manifest.AppendedSerialSize,
            "topology staged UPK did not reread the retargeted export-table values");
        try
        {
            _ = new SkeletalMeshTopologyCompilerService()
                .CompileAsync(new SkeletalMeshTopologyCompilationRequest(
                    registered.Entry, Path.Combine(entry.DirectoryPath, "invalid-topology-compilation")))
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("same-topology deformation entered the variable-size compiler");
        }
        catch (SkeletalMeshTopologyCompilationRefusalException exception)
        {
            Assert(exception.Code == "SMC005",
                "variable-size compiler did not retain its topology-readiness gate");
        }
        var encoded = new SkeletalMeshNativeEncodingService()
            .EncodeAsync(new SkeletalMeshNativeEncodingRequest(
                registered.Entry, Path.Combine(entry.DirectoryPath, "native-encoding")))
            .GetAwaiter().GetResult();
        Assert(encoded.Manifest.DecodeBackPassed && encoded.Manifest.LockedBytesPreserved &&
               encoded.Manifest.ChangedLockedByteCount == 0 &&
               encoded.Manifest.Lods is [{ PositionWriteCount: 1, TangentFrameWriteCount: 0 }] &&
               encoded.Manifest.ActualChangedByteCount > 0 && encoded.Manifest.BoundsUpdated &&
               File.Exists(encoded.EncodedExportPath) && File.Exists(encoded.DecodedProofGlbPath) &&
               new FileInfo(encoded.EncodedExportPath).Length == range.SerialSize,
            "native SkeletalMesh position encoding did not preserve locked bytes and decode back");
        var sourcePackageHashBeforeStaging = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var staged = new SkeletalMeshStagedUpkService()
            .BuildAsync(new SkeletalMeshStagedUpkRequest(
                encoded.ManifestPath, Path.Combine(entry.DirectoryPath, "staged-upk")))
            .GetAwaiter().GetResult();
        Assert(staged.Manifest.SameSizeInPlaceReplacement && staged.Manifest.ExportTableUnchanged &&
               staged.Manifest.AllUnrelatedLogicalBytesIdentical &&
               staged.Manifest.ExpectedLogicalImageMatched && staged.Manifest.AssetCatalogueEquivalent &&
               staged.Manifest.StructuralValidationPassed && staged.Manifest.TargetNativeDecodePassed &&
               staged.Manifest.UmodelPreviewReady && !staged.Manifest.DeploymentReady &&
               File.Exists(staged.OutputUpkPath),
            "staged SkeletalMesh UPK did not pass in-place reconstruction proof");
        var stagedVerification = SkeletalMeshStagedUpkService.VerifyOutputAsync(staged.ManifestPath)
            .GetAwaiter().GetResult();
        Assert(stagedVerification.OutputUpkPath == staged.OutputUpkPath &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == sourcePackageHashBeforeStaging,
            "staged SkeletalMesh verification changed or lost the source package identity");
        var stagedCatalog = new AssetCatalogReader().Read(staged.OutputUpkPath);
        using (var stagedLogical = new LogicalPackageStream(stagedCatalog.Summary))
        {
            var stagedPayload = new byte[checked((int)range.SerialSize)];
            stagedLogical.Position = range.SerialOffset;
            stagedLogical.ReadExactly(stagedPayload);
            Assert(Convert.ToHexString(SHA256.HashData(stagedPayload)) == encoded.Manifest.EncodedExportSha256,
                "staged SkeletalMesh UPK did not contain the encoded export at its original range");
        }
        var meshBundle = new ArkiveMeshModBundleService(
                new SyntheticModPackageExternalValidator())
            .BuildAsync(new ArkiveMeshModBundleRequest(
                staged.ManifestPath,
                Path.Combine(entry.DirectoryPath, "mesh-deployment-bundle"),
                Path.Combine(fixtureDirectory, "mesh-package-backups"),
                "Synthetic SkeletalMesh deployment"))
            .GetAwaiter().GetResult();
        Assert(meshBundle.Bundle.ReadyForControlledInstall &&
               meshBundle.Bundle.InstallationEnabled && meshBundle.Bundle.Assets is [{ ClassName: "SkeletalMesh" }] &&
               meshBundle.Readiness.SourceIdentityMatches &&
               meshBundle.Readiness.PayloadIdentityMatchesManifest &&
               meshBundle.Readiness.PackageSummaryIdentityRetained &&
               meshBundle.Readiness.ExternalCataloguePassed &&
               meshBundle.Readiness.ExternalNativeLoadPassed &&
               meshBundle.Readiness.ExpectedObjectFound &&
               !meshBundle.Readiness.InstallationPerformed &&
               !meshBundle.Readiness.GameFilesModified,
            "SkeletalMesh deployment rehearsal did not retain every controlled-install gate");
        var meshDeploymentRequest = new ArkiveModDeploymentRequest(
            meshBundle.BundleManifestPath,
            Path.Combine(fixtureDirectory, "mesh-package-backups"),
            Path.Combine(fixtureDirectory, "mesh-installation-state"));
        var meshDeployment = new ArkiveModDeploymentService(
            processGuard: new SyntheticModDeploymentProcessGuard());
        var meshOriginalTimestamp = File.GetLastWriteTimeUtc(path);
        var meshInitial = meshDeployment.InspectAsync(meshDeploymentRequest).GetAwaiter().GetResult();
        Assert(meshInitial.State == ModInstallationState.NotInstalled && meshInitial.CanInstall &&
               !meshInitial.CanRestore,
            "prepared SkeletalMesh bundle was not installable against its exact source package");
        var meshInstalled = meshDeployment.InstallAsync(meshDeploymentRequest).GetAwaiter().GetResult();
        Assert(meshInstalled.ChangedTarget && meshInstalled.Inspection.State == ModInstallationState.Installed &&
               meshInstalled.Inspection.CanRestore && meshInstalled.Receipt.BackupVerified &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == meshBundle.Bundle.PayloadSha256,
            "guarded SkeletalMesh installation did not atomically install and verify its payload");
        var meshRestored = meshDeployment.RestoreAsync(meshDeploymentRequest).GetAwaiter().GetResult();
        Assert(meshRestored.ChangedTarget && meshRestored.Inspection.State == ModInstallationState.Restored &&
               meshRestored.Inspection.CanInstall && !meshRestored.Inspection.CanRestore &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == sourcePackageHashBeforeStaging &&
               File.GetLastWriteTimeUtc(path) == meshOriginalTimestamp,
            "SkeletalMesh restore did not recover the byte-exact source package and timestamp");

        var meshIncludedOff = editedWorkspace.SetIncludedAsync(registered.Entry, false)
            .GetAwaiter().GetResult();
        var meshIncludedOn = editedWorkspace.SetIncludedAsync(meshIncludedOff, true)
            .GetAwaiter().GetResult();
        Assert(meshIncludedOff.Manifest.IncludedInPackageBuild == false &&
               meshIncludedOn.Manifest.IncludedInPackageBuild &&
               editedWorkspace.LoadAsync().GetAwaiter().GetResult().Entries.Single()
                   .Manifest.IncludedInPackageBuild,
            "SkeletalMesh package-selection state was not persisted");
        registered = registered with { Entry = meshIncludedOn };

        var unifiedMeshPackageWorkspace = new GuidedTexturePackageWorkspace(
            Path.Combine(fixtureDirectory, "unified-mesh-package-workspace"));
        var unifiedMeshPackageProject = unifiedMeshPackageWorkspace.GetOrCreateAsync(
                report.PackageAlias, Path.GetFileName(path), path)
            .GetAwaiter().GetResult();
        var unifiedMeshComposer = new UnifiedPackageComposerService(
            new UpkPackageReader(),
            NoChunkProtectionProvider.Instance,
            new SkeletalMeshReconnaissanceService(),
            new SkeletalMeshEditableExportService(),
            new SkeletalMeshNativeEncodingService(),
            new SkeletalMeshStagedUpkService(),
            null!,
            new ArkivePackageModBundleService(
                new SyntheticModPackageExternalValidator(),
                protectionProvider: NoChunkProtectionProvider.Instance));
        try
        {
            _ = unifiedMeshComposer.BuildAsync(
                    new UnifiedPackageCompositionRequest(
                        unifiedMeshPackageProject, [], [registered.Entry, registered.Entry],
                        Path.Combine(fixtureDirectory, "unified-mesh-conflict"),
                        Path.Combine(fixtureDirectory, "unified-mesh-backups"),
                        "Conflicting mesh smoke test"))
                .GetAwaiter().GetResult();
            throw new InvalidOperationException(
                "unified package composer accepted two edits for one native export");
        }
        catch (NotSupportedException exception)
        {
            Assert(exception.Message.Contains("More than one selected project targets native export",
                    StringComparison.Ordinal),
                "unified package conflict refusal did not identify the duplicate native export");
        }

        // Existing edits created before deformation-dependency reconnaissance use schema 3.
        // Their UModel material oracle is still trustworthy when its complete identity and
        // logical export hash match; a current-schema recon/preflight is generated during rebase.
        var legacyOracleDirectory = Path.Combine(entry.DirectoryPath, "legacy-schema-3-oracle");
        Directory.CreateDirectory(legacyOracleDirectory);
        var legacyReconPath = Path.Combine(legacyOracleDirectory, "arkive-skeletalmesh-recon.json");
        var legacyReconJson = JsonNode.Parse(File.ReadAllText(entry.JsonPath))!.AsObject();
        legacyReconJson["schemaVersion"] = 3;
        legacyReconJson["objectName"] = "WrongMesh";
        File.WriteAllText(legacyReconPath, legacyReconJson.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));
        var legacySourceManifestPath = Path.Combine(
            legacyOracleDirectory, "arkive-editable-skeletalmesh.json");
        var legacySourceJson = JsonNode.Parse(File.ReadAllText(editable.ManifestPath))!.AsObject();
        legacySourceJson["reconnaissanceSchemaVersion"] = 3;
        legacySourceJson["reconnaissanceJsonPath"] = legacyReconPath;
        File.WriteAllText(legacySourceManifestPath, legacySourceJson.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));
        var legacyRegisteredEntry = registered.Entry with
        {
            Manifest = registered.Entry.Manifest with
            {
                SourceManifestPath = legacySourceManifestPath,
            },
        };
        try
        {
            _ = unifiedMeshComposer.BuildAsync(
                    new UnifiedPackageCompositionRequest(
                        unifiedMeshPackageProject, [], [legacyRegisteredEntry],
                        Path.Combine(fixtureDirectory, "unified-mesh-invalid-legacy-oracle"),
                        Path.Combine(fixtureDirectory, "unified-mesh-backups"),
                        "Invalid legacy oracle smoke test"))
                .GetAwaiter().GetResult();
            throw new InvalidOperationException(
                "unified package composer accepted a legacy oracle with the wrong object identity");
        }
        catch (NotSupportedException exception)
        {
            Assert(exception.Message.Contains("not bound to the byte-identical source export",
                    StringComparison.Ordinal),
                "legacy oracle identity refusal did not retain the strict binding gate");
        }
        legacyReconJson["objectName"] = report.ObjectName;
        File.WriteAllText(legacyReconPath, legacyReconJson.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));

        var unifiedMeshBuild = unifiedMeshComposer.BuildAsync(
                new UnifiedPackageCompositionRequest(
                    unifiedMeshPackageProject,
                    [],
                    [legacyRegisteredEntry],
                    Path.Combine(fixtureDirectory, "unified-mesh-package"),
                    Path.Combine(fixtureDirectory, "unified-mesh-backups"),
                    "Unified mesh-only smoke test"))
            .GetAwaiter().GetResult();
        var unifiedMeshVerification = UnifiedPackageComposerService.VerifyOutputAsync(
                unifiedMeshBuild.CompositionManifestPath)
            .GetAwaiter().GetResult();
        var unifiedMeshBundle = ArkiveModBundleService.LoadBundleAsync(
                unifiedMeshBuild.Revision.ModBundleManifestPath)
            .GetAwaiter().GetResult();
        Assert(unifiedMeshBuild.Manifest.TextureCount == 0 &&
               unifiedMeshBuild.Manifest.SkeletalMeshCount == 1 &&
               unifiedMeshBuild.Manifest.Stages is [{ Kind: UnifiedPackageAssetKind.SkeletalMesh }] &&
               unifiedMeshBuild.Revision.Assets.Count == 0 &&
               unifiedMeshBuild.Revision.MeshAssets is [{ EditId: var unifiedEditId }] &&
               unifiedEditId == registered.Entry.Manifest.EditId &&
               unifiedMeshVerification.OutputUpkPath == unifiedMeshBuild.Revision.OutputUpkPath &&
               unifiedMeshBundle.Assets is [{ ClassName: "SkeletalMesh" }] &&
               unifiedMeshBundle.InstallationEnabled,
            "unified package composer did not retain the schema-3 mesh compatibility path");
        var unifiedMeshDeploymentRequest = new ArkiveModDeploymentRequest(
            unifiedMeshBuild.Revision.ModBundleManifestPath,
            Path.Combine(fixtureDirectory, "unified-mesh-backups"),
            Path.Combine(fixtureDirectory, "unified-mesh-installation-state"));
        var unifiedMeshDeployment = new ArkiveModDeploymentService(
            processGuard: new SyntheticModDeploymentProcessGuard());
        var unifiedMeshInstalled = unifiedMeshDeployment.InstallAsync(unifiedMeshDeploymentRequest)
            .GetAwaiter().GetResult();
        var unifiedMeshRestored = unifiedMeshDeployment.RestoreAsync(unifiedMeshDeploymentRequest)
            .GetAwaiter().GetResult();
        Assert(unifiedMeshInstalled.Inspection.State == ModInstallationState.Installed &&
               unifiedMeshRestored.Inspection.State == ModInstallationState.Restored &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == sourcePackageHashBeforeStaging,
            "unified mesh-only bundle did not install and restore as one guarded package");

        var unifiedTopologyBuild = unifiedMeshComposer.BuildAsync(
                new UnifiedPackageCompositionRequest(
                    unifiedMeshPackageProject,
                    [],
                    [registeredTopology.Entry],
                    Path.Combine(fixtureDirectory, "unified-topology-package"),
                    Path.Combine(fixtureDirectory, "unified-topology-backups"),
                    "Unified topology preview smoke test")
                {
                    ForcePreviewOnly = true,
                })
            .GetAwaiter().GetResult();
        var unifiedTopologyVerification = UnifiedPackageComposerService.VerifyOutputAsync(
                unifiedTopologyBuild.CompositionManifestPath)
            .GetAwaiter().GetResult();
        var unifiedTopologyBundle = ArkiveModBundleService.LoadBundleAsync(
                unifiedTopologyBuild.Revision.ModBundleManifestPath)
            .GetAwaiter().GetResult();
        var unifiedTopologyReadiness = ArkiveModBundleService.LoadReadinessAsync(
                unifiedTopologyBuild.Revision.DeploymentReadinessManifestPath)
            .GetAwaiter().GetResult();
        var unifiedTopologyAsset = unifiedTopologyBuild.Revision.MeshAssets.Single();
        var unifiedTopologyCompilation = SkeletalMeshTopologyCompilerService.LoadManifestAsync(
                unifiedTopologyAsset.TopologyCompilationManifestPath!)
            .GetAwaiter().GetResult();
        var unifiedTopologyCatalogue = new AssetCatalogReader().Read(
            unifiedTopologyVerification.OutputUpkPath);
        var unifiedTopologyExport = unifiedTopologyCatalogue.Assets.Single(asset =>
            asset.ExportIndex == unifiedTopologyAsset.NativeExportIndex);
        using (var unifiedTopologyLogical = new LogicalPackageStream(unifiedTopologyCatalogue.Summary))
        {
            unifiedTopologyLogical.Position = unifiedTopologyExport.SerialOffset;
            var finalTopologyBytes = new byte[checked((int)unifiedTopologyExport.SerialSize)];
            unifiedTopologyLogical.ReadExactly(finalTopologyBytes);
            Assert(Convert.ToHexString(SHA256.HashData(finalTopologyBytes)) ==
                   unifiedTopologyCompilation.CompiledExportSha256,
                "unified topology composition lost its compiled export payload");
        }
        Assert(unifiedTopologyBuild.Manifest.PreviewOnly &&
               unifiedTopologyBuild.Manifest.VariableSizeMeshCount == 1 &&
               unifiedTopologyBuild.Manifest.ExactObjectExternalValidationPassed &&
               unifiedTopologyBuild.Manifest.Stages is
                   [{ Kind: UnifiedPackageAssetKind.SkeletalMesh, VariableSizeReplacement: true }] &&
               unifiedTopologyBuild.Manifest.Assets is
                   [{ Kind: UnifiedPackageAssetKind.SkeletalMesh, VariableSizeReplacement: true }] &&
               unifiedTopologyBuild.Revision.PreviewOnly &&
               unifiedTopologyBuild.Revision.ContainsVariableSizeMeshReplacements &&
               unifiedTopologyAsset.VariableSizeReplacement &&
               unifiedTopologyAsset.NativeEncodingManifestPath == string.Empty &&
               unifiedTopologyExport.SerialOffset == unifiedTopologyAsset.SerialOffset &&
               unifiedTopologyExport.SerialSize == unifiedTopologyAsset.SerialSize &&
               !unifiedTopologyBundle.InstallationEnabled &&
               !unifiedTopologyBundle.ReadyForControlledInstall &&
               unifiedTopologyBundle.VariableSizeDeploymentGateRelativePath is null &&
               unifiedTopologyBundle.VariableSizeDeploymentGateSha256 is null &&
               !unifiedTopologyReadiness.ReadyForControlledInstall &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == sourcePackageHashBeforeStaging,
            "unified topology composition did not retain preview-only append/retarget safety gates");

        var unifiedTopologyDeploymentBuild = unifiedMeshComposer.BuildAsync(
                new UnifiedPackageCompositionRequest(
                    unifiedMeshPackageProject,
                    [],
                    [registeredTopology.Entry],
                    Path.Combine(fixtureDirectory, "unified-topology-deployment-package"),
                    Path.Combine(fixtureDirectory, "unified-topology-deployment-backups"),
                    "Unified topology controlled deployment smoke test"))
            .GetAwaiter().GetResult();
        _ = UnifiedPackageComposerService.VerifyOutputAsync(
                unifiedTopologyDeploymentBuild.CompositionManifestPath)
            .GetAwaiter().GetResult();
        var unifiedTopologyDeploymentBundle = ArkiveModBundleService.LoadBundleAsync(
                unifiedTopologyDeploymentBuild.Revision.ModBundleManifestPath)
            .GetAwaiter().GetResult();
        var unifiedTopologyDeploymentReadiness = ArkiveModBundleService.LoadReadinessAsync(
                unifiedTopologyDeploymentBuild.Revision.DeploymentReadinessManifestPath)
            .GetAwaiter().GetResult();
        var unifiedTopologyDeploymentGate = UnifiedPackageComposerService
            .LoadVariableSizeDeploymentGateAsync(
                unifiedTopologyDeploymentBuild.Revision.VariableSizeDeploymentGateManifestPath!)
            .GetAwaiter().GetResult();
        var unifiedTopologyDeploymentGatePath = Path.GetFullPath(
            unifiedTopologyDeploymentBuild.Revision.VariableSizeDeploymentGateManifestPath!);
        var unifiedTopologyDeploymentGateHash = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(unifiedTopologyDeploymentGatePath)));
        Assert(!unifiedTopologyDeploymentBuild.Manifest.PreviewOnly &&
               unifiedTopologyDeploymentBuild.Manifest.VariableSizeDeploymentGatePassed &&
               unifiedTopologyDeploymentBuild.Revision.ContainsVariableSizeMeshReplacements &&
               unifiedTopologyDeploymentBuild.Revision.VariableSizeDeploymentApproved &&
               unifiedTopologyDeploymentGate.ControlledInstallApproved &&
               unifiedTopologyDeploymentGate.PackageSizeChanged &&
               unifiedTopologyDeploymentGate.EveryTopologyStageVerified &&
               unifiedTopologyDeploymentGate.EveryUnrelatedExportRetained &&
               unifiedTopologyDeploymentGate.EveryAffectedChunkDecodedBack &&
               unifiedTopologyDeploymentGate.ExactObjectExternalValidationPassed &&
               unifiedTopologyDeploymentBundle.VariableSizeMeshCount == 1 &&
               unifiedTopologyDeploymentBundle.VariableSizeDeploymentApproved &&
               unifiedTopologyDeploymentBundle.VariableSizeDeploymentGateRelativePath ==
                   UnifiedPackageComposerService.VariableSizeDeploymentGateManifestFileName &&
               unifiedTopologyDeploymentBundle.VariableSizeDeploymentGateSha256 ==
                   unifiedTopologyDeploymentGateHash &&
               unifiedTopologyDeploymentBundle.InstallationEnabled &&
               unifiedTopologyDeploymentBundle.ReadyForControlledInstall &&
               unifiedTopologyDeploymentReadiness.VariableSizeMeshCount == 1 &&
               unifiedTopologyDeploymentReadiness.VariableSizeDeploymentApproved &&
               unifiedTopologyDeploymentReadiness.VariableSizeDeploymentGateRelativePath ==
                   unifiedTopologyDeploymentBundle.VariableSizeDeploymentGateRelativePath &&
               unifiedTopologyDeploymentReadiness.VariableSizeDeploymentGateSha256 ==
                   unifiedTopologyDeploymentGateHash &&
               unifiedTopologyDeploymentReadiness.ReadyForControlledInstall,
            "unified topology composition did not pass the second-batch controlled deployment gate");
        var unifiedTopologyDeploymentRequest = new ArkiveModDeploymentRequest(
            unifiedTopologyDeploymentBuild.Revision.ModBundleManifestPath,
            Path.Combine(fixtureDirectory, "unified-topology-deployment-backups"),
            Path.Combine(fixtureDirectory, "unified-topology-deployment-state"));
        var unifiedTopologyDeployment = new ArkiveModDeploymentService(
            processGuard: new SyntheticModDeploymentProcessGuard());
        var topologyDeploymentOriginalTimestamp = File.GetLastWriteTimeUtc(path);
        var topologyInstalled = unifiedTopologyDeployment.InstallAsync(
                unifiedTopologyDeploymentRequest)
            .GetAwaiter().GetResult();
        Assert(topologyInstalled.ChangedTarget &&
               topologyInstalled.Inspection.State == ModInstallationState.Installed &&
               new FileInfo(path).Length == unifiedTopologyDeploymentBundle.PayloadBytes &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) ==
               unifiedTopologyDeploymentBundle.PayloadSha256,
            "variable-size topology deployment did not atomically install the exact payload");
        var untamperedTopologyGate = File.ReadAllBytes(unifiedTopologyDeploymentGatePath);
        File.AppendAllText(unifiedTopologyDeploymentGatePath, "tampered");
        try
        {
            _ = unifiedTopologyDeployment.InspectAsync(unifiedTopologyDeploymentRequest)
                .GetAwaiter().GetResult();
            throw new InvalidOperationException(
                "variable-size topology deployment accepted a modified gate receipt");
        }
        catch (ArkiveModDeploymentRefusalException exception)
        {
            Assert(exception.Code == "DEP030",
                "variable-size topology deployment returned the wrong gate-tamper refusal");
        }
        var recoveryInspection = unifiedTopologyDeployment.InspectForRecoveryAsync(
                unifiedTopologyDeploymentRequest)
            .GetAwaiter().GetResult();
        Assert(recoveryInspection.State == ModInstallationState.Installed &&
               recoveryInspection.CanRestore && !recoveryInspection.CanInstall &&
               recoveryInspection.BackupVerified,
            "gate-tampered variable-size deployment was not recoverable from exact payload, receipt, and backup identities");
        var topologyRestored = unifiedTopologyDeployment.RestoreAsync(
                unifiedTopologyDeploymentRequest)
            .GetAwaiter().GetResult();
        Assert(topologyRestored.ChangedTarget &&
               topologyRestored.Inspection.State == ModInstallationState.Restored &&
               !topologyRestored.Inspection.CanInstall &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) ==
               sourcePackageHashBeforeStaging &&
               File.GetLastWriteTimeUtc(path) == topologyDeploymentOriginalTimestamp,
            "gate-tampered variable-size topology deployment did not restore the byte-exact source package and timestamp");
        File.WriteAllBytes(unifiedTopologyDeploymentGatePath, untamperedTopologyGate);
        var deletionIdentityBundlePath = Path.GetFullPath(
            unifiedTopologyDeploymentBuild.Revision.ModBundleManifestPath);
        var deletionIdentityReadinessPath = Path.GetFullPath(
            unifiedTopologyDeploymentBuild.Revision.DeploymentReadinessManifestPath);
        var installReadyBundleBytes = File.ReadAllBytes(deletionIdentityBundlePath);
        var installReadyReadinessBytes = File.ReadAllBytes(deletionIdentityReadinessPath);
        var deletionBundleJson = JsonNode.Parse(installReadyBundleBytes)!.AsObject();
        var deletionReadinessJson = JsonNode.Parse(installReadyReadinessBytes)!.AsObject();
        deletionBundleJson["readyForControlledInstall"] = false;
        deletionReadinessJson["readyForControlledInstall"] = false;
        File.WriteAllText(deletionIdentityBundlePath, deletionBundleJson.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(deletionIdentityReadinessPath, deletionReadinessJson.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            try
            {
                _ = unifiedTopologyDeployment.InspectAsync(unifiedTopologyDeploymentRequest)
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException(
                    "normal deployment inspection accepted a preview-only retained revision");
            }
            catch (ArkiveModDeploymentRefusalException exception)
            {
                Assert(exception.Code == "DEP015",
                    "preview-only retained revision returned the wrong install-readiness refusal");
            }
            var deletionIdentity = unifiedTopologyDeployment.InspectForDeletionIdentityAsync(
                    unifiedTopologyDeploymentRequest)
                .GetAwaiter().GetResult();
            Assert(deletionIdentity.State == ModInstallationState.Restored &&
                   !deletionIdentity.CanInstall && !deletionIdentity.CanRestore &&
                   deletionIdentity.CurrentTargetSha256 == sourcePackageHashBeforeStaging,
                "preview-only retained revision could not prove its restored target for safe deletion");
        }
        finally
        {
            File.WriteAllBytes(deletionIdentityBundlePath, installReadyBundleBytes);
            File.WriteAllBytes(deletionIdentityReadinessPath, installReadyReadinessBytes);
        }
        var normalEditedGlb = Path.Combine(entry.DirectoryPath, "synthetic-normal-edit.glb");
        WriteSkeletalMeshNormalEdit(editable.GlbPath, normalEditedGlb, 0.1f);
        var normalRegistered = editedWorkspace.RegisterAsync(
                editableSnapshot.Entries[0], normalEditedGlb, "Synthetic normal edit")
            .GetAwaiter().GetResult();
        var normalEncoded = new SkeletalMeshNativeEncodingService()
            .EncodeAsync(new SkeletalMeshNativeEncodingRequest(
                normalRegistered.Entry, Path.Combine(entry.DirectoryPath, "native-normal-encoding")))
            .GetAwaiter().GetResult();
        Assert(normalEncoded.Manifest.DecodeBackPassed && normalEncoded.Manifest.LockedBytesPreserved &&
               normalEncoded.Manifest.ChangedLockedByteCount == 0 &&
               normalEncoded.Manifest.Lods is [{ PositionWriteCount: 0, TangentFrameWriteCount: 3 }] &&
               normalEncoded.Manifest.Lods[0].MaximumNormalDecodeError <= 0.025 &&
               normalEncoded.Manifest.Lods[0].MaximumTangentDecodeError <= 0.025,
            "native SkeletalMesh tangent-frame encoding did not quantize and decode back safely");
        editedWorkspace.DeleteAsync(normalRegistered.Entry).GetAwaiter().GetResult();
        editedWorkspace.DeleteAsync(registeredSnapshot.Entries[0]).GetAwaiter().GetResult();
        Assert(!Directory.Exists(registered.Entry.DirectoryPath) && File.Exists(editable.GlbPath),
            "edited SkeletalMesh deletion escaped its derivative directory");
        editableWorkspace.DeleteAsync(editableSnapshot.Entries[0]).GetAwaiter().GetResult();
        Assert(!Directory.Exists(editable.OutputDirectory) && File.Exists(entry.JsonPath),
            "editable SkeletalMesh deletion did not remain isolated from reconnaissance evidence");

        var firstRange = report.NativeLayout.Ranges[0];
        var corruptedRanges = report.NativeLayout.Ranges.ToArray();
        corruptedRanges[0] = firstRange with { Sha256 = new string('0', 64) };
        var corruptedReport = report with
        {
            NativeLayout = report.NativeLayout with { Ranges = corruptedRanges },
        };
        try
        {
            _ = new SkeletalMeshNoOpService().ReconstructAsync(new SkeletalMeshNoOpRequest(
                    corruptedReport, entry.JsonPath, Path.Combine(entry.DirectoryPath, "corrupt-range-proof")))
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("tampered SkeletalMesh range evidence was accepted");
        }
        catch (SkeletalMeshNoOpRefusalException exception)
        {
            Assert(exception.Code == "SKN021", "tampered SkeletalMesh range used the wrong refusal code");
        }
    }

    private static void TestSkeletonCompatibilityAndRetargetWorkspace()
    {
        var root = Path.Combine(fixtureDirectory, "skeleton-compatibility");
        Directory.CreateDirectory(root);

        SkeletalMeshEditableWorkspaceEntry Export(string id, string childName, float childHeight)
        {
            var packageDirectory = Path.Combine(root, id, "EFGame", "ReleasePC", "Packages");
            Directory.CreateDirectory(packageDirectory);
            var packagePath = Path.Combine(packageDirectory, id + ".upk");
            var range = WriteSkeletalMeshReconPackage(packagePath, childName, childHeight);
            var report = new SkeletalMeshReconnaissanceService()
                .InspectAsync(new SkeletalMeshReconnaissanceRequest(
                    packagePath, id, 1, AssetKind.SkeletalMesh,
                    "SkeletalMesh", "TestMesh", range.SerialOffset, range.SerialSize))
                .GetAwaiter().GetResult();
            var reconStore = new SkeletalMeshReconnaissanceWorkspaceStore(
                Path.Combine(root, id, "recon"));
            var recon = reconStore.SaveAsync(report).GetAwaiter().GetResult();
            var editable = new SkeletalMeshEditableExportService()
                .ExportAsync(new SkeletalMeshEditableExportRequest(
                    report, recon.JsonPath, Path.Combine(recon.DirectoryPath, "editable")))
                .GetAwaiter().GetResult();
            var file = new FileInfo(editable.GlbPath);
            return new SkeletalMeshEditableWorkspaceEntry(
                editable.OutputDirectory, editable.ManifestPath, editable.GlbPath,
                editable.Manifest, file.Exists, file.Length == editable.Manifest.GlbBytes, true);
        }

        var target = Export("target", "ChildBone", 50f);
        var exactDonor = Export("exact-donor", "ChildBone", 50f);
        var alteredBindDonor = Export("altered-bind-donor", "ChildBone", 65f);
        var incompatibleDonor = Export("incompatible-donor", "DonorTail", 50f);
        var service = new SkeletalMeshSkeletonCompatibilityService();

        var exact = service.AnalyzeAsync(exactDonor, target).GetAwaiter().GetResult();
        Assert(exact.Compatibility == SkeletalMeshSkeletonCompatibilityKind.ExactTargetSkeleton &&
               exact.ExactSkeletonIdentity && exact.AutomaticRetargetAllowed &&
               exact.ExactNameMappingCount == 2 && exact.MappedSourceWeightPercent > 99.999d &&
               exact.Source.NameHierarchySha256 == exact.Target.NameHierarchySha256 &&
               exact.Source.BindPoseSha256 == exact.Target.BindPoseSha256,
            "identical synthetic rigs were not classified as exact target skeletons");

        var altered = service.AnalyzeAsync(alteredBindDonor, target).GetAwaiter().GetResult();
        Assert(altered.Compatibility ==
                   SkeletalMeshSkeletonCompatibilityKind.SameNamedHierarchyAlteredBindPose &&
               !altered.ExactSkeletonIdentity && !altered.AutomaticRetargetAllowed &&
               altered.BlenderAuthoringWorkspaceAllowed && altered.MappedSourceWeightPercent > 99.999d &&
               altered.MaximumMappedBindTranslationDeltaMetres > 0.1d &&
               altered.Issues.Any(issue => issue.Code == "SKC021"),
            "same-named rig with an altered bind pose was not isolated for retarget authoring");

        var incompatible = service.AnalyzeAsync(incompatibleDonor, target).GetAwaiter().GetResult();
        Assert(incompatible.Compatibility == SkeletalMeshSkeletonCompatibilityKind.Incompatible &&
               !incompatible.BlenderAuthoringWorkspaceAllowed &&
               incompatible.MissingSourceBoneCount == 1 && incompatible.MappedSourceWeightPercent < 50d &&
               incompatible.BoneMappings.Any(mapping =>
                   mapping.SourceBoneName == "DonorTail" && mapping.TargetBoneIndex is null),
            "low-coverage rig was not refused as incompatible");

        var tamperedPath = Path.Combine(root, "tampered-donor.glb");
        var tamperedBytes = File.ReadAllBytes(exactDonor.GlbPath);
        tamperedBytes[^1] ^= 0x01;
        File.WriteAllBytes(tamperedPath, tamperedBytes);
        try
        {
            _ = service.AnalyzeAsync(exactDonor with { GlbPath = tamperedPath }, target)
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("tampered clean donor GLB entered skeleton comparison");
        }
        catch (InvalidDataException exception)
        {
            Assert(exception.Message.StartsWith("SKC002:", StringComparison.Ordinal),
                "tampered clean donor GLB used the wrong identity-refusal code");
        }

        var workspace = new SkeletalMeshRetargetWorkspaceService().CreateAsync(
                new SkeletalMeshRetargetWorkspaceRequest(
                    alteredBindDonor, target, Path.Combine(root, "retarget-workspace"), "Synthetic retarget"))
            .GetAwaiter().GetResult();
        Assert(workspace.Manifest.Compatibility ==
                   SkeletalMeshSkeletonCompatibilityKind.SameNamedHierarchyAlteredBindPose &&
               !workspace.Manifest.AutomaticRetargetPerformed &&
               workspace.Manifest.RequiresManualGeometryFitting &&
               workspace.Manifest.RequiresManualWeightPainting &&
               File.Exists(workspace.ManifestPath) && File.Exists(workspace.CompatibilityReportPath) &&
               File.Exists(workspace.BlenderSetupScriptPath) && File.Exists(workspace.InstructionsPath) &&
               File.Exists(Path.Combine(workspace.OutputDirectory, workspace.Manifest.DonorGlbRelativePath)) &&
               File.Exists(Path.Combine(workspace.OutputDirectory, workspace.Manifest.TargetGlbRelativePath)) &&
               File.ReadAllText(workspace.BlenderSetupScriptPath).Contains("ARKIVE_UNMAPPED__", StringComparison.Ordinal) &&
               File.ReadAllText(workspace.BlenderSetupScriptPath).Contains("TARGET_REFERENCE_ARMATURE", StringComparison.Ordinal),
            "retarget authoring workspace did not retain its target-armature and unresolved-weight safety contract");

        try
        {
            _ = new SkeletalMeshRetargetWorkspaceService().CreateAsync(
                    new SkeletalMeshRetargetWorkspaceRequest(
                        incompatibleDonor, target, Path.Combine(root, "invalid-retarget-workspace")))
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("incompatible rigs entered the retarget authoring workflow");
        }
        catch (NotSupportedException exception)
        {
            Assert(exception.Message.StartsWith("SKR001:", StringComparison.Ordinal),
                "incompatible retarget refusal did not retain its stable diagnostic code");
        }
    }

    private static void TestSkeletalMeshOracleParsing()
    {
        const string dump = """
            Object info:
            ClassName: SkeletalMesh3 ObjectName: TestMesh
                bHasVertexColors = true
                Materials[1] = { MaterialInstanceConstant'mat.test_mi' }
                Lods[2] =
                {
                    Lods[0] =
                    {
                        Sections[1] =
                        {
                            Sections[0] =
                            {
                                Material = MaterialInstanceConstant'mat.test_mi'
                                FirstIndex = 0
                                NumFaces = 12
                            }
                        }
                        NumVerts = 10
                        IndexCount = 36
                        NumTexCoords = 2
                    }
                    Lods[1] =
                    {
                        Sections[2] =
                        {
                            Sections[0] = { Material=MaterialInstanceConstant'mat.test_mi', FirstIndex=0, NumFaces=4 }
                            Sections[1] =
                            {
                                Material = MaterialInstanceConstant'mat.test_mi'
                                FirstIndex = 12
                                NumFaces = 6
                            }
                        }
                        NumVerts = 7
                        IndexCount = 30
                        NumTexCoords = 1
                    }
                }
                LodCount = 2
                BoneCount = 42
                SocketCount = 3
            """;
        var oracle = SkeletalMeshReconnaissanceService.ParseSkeletalMeshDump(
            dump, TimeSpan.FromMilliseconds(25), "SkeletalMesh");
        Assert(oracle.ReportedClassName == "SkeletalMesh3", "oracle class was not parsed");
        Assert(oracle.LodCount == 2 && oracle.Lods.Count == 2, "oracle LODs were not parsed");
        Assert(oracle.BoneCount == 42 && oracle.SocketCount == 3, "oracle skeleton summary was not parsed");
        Assert(oracle.HasVertexColors == true, "oracle vertex-color state was not parsed");
        Assert(oracle.Lods[0].VertexCount == 10 && oracle.Lods[0].TriangleCount == 12,
            "oracle LOD 0 geometry was not parsed");
        Assert(oracle.Lods[1].Sections.Count == 2 && oracle.Lods[1].TriangleCount == 10,
            "oracle section evidence was not parsed");
        Assert(oracle.Lods[1].Sections[0] is { FirstIndex: 0, TriangleCount: 4 },
            "oracle one-line section evidence was not parsed");
    }

}
