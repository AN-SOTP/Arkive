namespace Arkive.SmokeTests;

internal static class ExternalArtifactSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("builds an isolated UModel physical-package shadow", TestUmodelPackageShadow),
        new("parses UModel list output", TestUmodelListOutput),
        new("records isolated export workspace sessions", TestExportWorkspace),
        new("inspects DDS headers and mip payloads", TestDdsArtifactInspection),
        new("inspects glTF structure and dependencies", TestGltfArtifactInspection),
        new("inspects GLB embedded buffers", TestGlbArtifactInspection),
    ];

    private static void TestUmodelPackageShadow()
    {
        var root = Path.Combine(fixtureDirectory, "umodel-shadow-source");
        var shadow = Path.Combine(fixtureDirectory, "umodel-shadow-output");
        Directory.CreateDirectory(root);
        var model = Path.Combine(root, "MODEL.upk");
        var target = Path.Combine(root, "TARGET.upk");
        var dependency = Path.Combine(root, "MATERIAL.upk");
        var replacement = Path.Combine(fixtureDirectory, "replacement.upk");
        File.WriteAllBytes(model, [1, 2, 3]);
        File.WriteAllBytes(target, [4, 5, 6]);
        File.WriteAllBytes(dependency, [7, 8]);
        File.WriteAllBytes(replacement, [9, 10, 11, 12]);

        var result = UmodelPackageShadowBuilder.BuildAsync(
            root,
            model,
            ["MODEL.upk", "TARGET.upk", "MATERIAL.upk"],
            replacement,
            "TARGET",
            shadow).GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(result.ReplacementPackagePath).SequenceEqual(new byte[] { 9, 10, 11, 12 }),
            "UModel shadow did not place the replacement under the clean physical filename.");
        Assert(File.ReadAllBytes(result.ModelPackagePath).SequenceEqual(new byte[] { 1, 2, 3 }) &&
               result.StagedPackagePaths.Count == 3 && result.CopiedBytes == 9,
            "UModel shadow did not preserve the probed dependency closure.");

        var refused = false;
        try
        {
            UmodelPackageShadowBuilder.BuildAsync(
                root,
                model,
                ["MODEL.upk", "MATERIAL.upk"],
                replacement,
                "TARGET.upk",
                Path.Combine(fixtureDirectory, "umodel-shadow-refused")).GetAwaiter().GetResult();
        }
        catch (NotSupportedException)
        {
            refused = true;
        }
        Assert(refused, "UModel shadow accepted a model that never loaded the target texture package.");
    }

    private static void TestUmodelListOutput()
    {
        const string output = """
            Loading package: fixture.upk Ver: 868/16 Engine: 12097 Names: 3 Exports: 2 Imports: 1 Game: 80004E
               0     261D       10 objectredirector old_asset
               1    3C9587     B06F texture2d test_texture_d
               2    1D375E    5C129 skeletalmesh test_character_sk
            """;

        var assets = UmodelBridge.ParseListOutput(output);
        Assert(assets.Count == 3, "unexpected UModel asset count");
        Assert(assets[1].SerialOffset == 0x3C9587, "UModel offset was not parsed as hexadecimal");
        Assert(assets[1].SerialSize == 0xB06F, "UModel size was not parsed as hexadecimal");
        Assert(assets[1].Kind == AssetKind.Texture, "UModel texture kind was not inferred");
        Assert(assets[1].DisplayClassName == "Texture2D", "UModel class display name was not normalized");
        Assert(assets[1].IsExportable, "UModel texture was not marked exportable");
        Assert(assets[2].Kind == AssetKind.SkeletalMesh, "UModel mesh kind was not inferred");
        var materialExpression = new UmodelAssetEntry(
            3,
            0,
            0,
            "materialexpressionscalarparameter",
            "expression");
        Assert(
            materialExpression.Kind == AssetKind.Object && !materialExpression.IsExportable,
            "UModel material expression was incorrectly treated as an exportable material");
        var lightmap = new UmodelAssetEntry(4, 0, 0, "lightmaptexture2d", "lightmap");
        Assert(
            lightmap.Kind == AssetKind.Texture && lightmap.IsExportable,
            "UModel lightmap texture was not classified as a texture");
    }

    private static void TestExportWorkspace()
    {
        var root = Path.Combine(fixtureDirectory, "export-workspace");
        var packagePath = Path.Combine(fixtureDirectory, "export-source.upk");
        File.WriteAllBytes(packagePath, [1, 2, 3, 4]);
        var store = new ExportWorkspaceStore(root);
        var outputDirectory = store.CreateSessionDirectory("decoded_package", "fixture_texture");
        var outputPath = Path.Combine(outputDirectory, "fixture_texture.dds");
        File.WriteAllBytes(outputPath, new byte[128]);
        var asset = new UmodelAssetEntry(7, 16, 128, "Texture2D", "fixture_texture");
        var result = new UmodelExportResult(
            packagePath,
            asset,
            outputDirectory,
            [outputPath],
            [],
            TimeSpan.FromSeconds(1.25),
            UsedPackageWideFallback: false);

        var entry = store.CommitAsync(result, "decoded_package", ["Fixture Texture"], packagePath)
            .GetAwaiter()
            .GetResult();
        Assert(File.Exists(entry.ManifestPath), "export manifest was not written");
        Assert(entry.Manifest.OutputFiles.Count == 1, "exported file inventory was not recorded");
        Assert(entry.Manifest.OutputBytes == 128, "exported byte count was incorrect");
        var snapshot = store.LoadAsync().GetAwaiter().GetResult();
        Assert(snapshot.Entries.Count == 1, "export workspace session was not reloaded");
        Assert(snapshot.UnreadableManifestCount == 0, "valid export manifest was unreadable");
    }

    private static void TestDdsArtifactInspection()
    {
        var path = Path.Combine(fixtureDirectory, "fixture_normal.dds");
        var bytes = new byte[136];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 0x20534444);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x000A1007);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), 4);
        Encoding.ASCII.GetBytes("DXT1").CopyTo(bytes, 84);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108), 0x1000);
        File.WriteAllBytes(path, bytes);

        var inspector = new ExportArtifactInspector();
        var result = inspector.InspectAsync(path).GetAwaiter().GetResult();
        Assert(result.Format == "DDS", "DDS inspector returned the wrong format");
        Assert(result.ErrorCount == 0, "valid DDS fixture produced structural errors");
        Assert(result.Summary.Contains("BC1", StringComparison.Ordinal), "DDS format was not identified");
        Assert(
            result.Properties.Any(property =>
                property.Name == "Expected minimum bytes" && property.Value == "8"),
            "DDS mip payload size was not calculated");

        var truncatedPath = Path.Combine(fixtureDirectory, "fixture_truncated.dds");
        File.WriteAllBytes(truncatedPath, bytes[..132]);
        var truncated = inspector.InspectAsync(truncatedPath).GetAwaiter().GetResult();
        Assert(
            truncated.Issues.Any(issue => issue.Code == "DDS_PAYLOAD_TRUNCATED"),
            "truncated DDS payload was not detected");

        var transparentBc3Path = Path.Combine(fixtureDirectory, "fixture_face_d.dds");
        var transparentBlock = new byte[16];
        transparentBlock[0] = 0;
        transparentBlock[1] = 255;
        DdsBc3Writer.WriteAsync(
                transparentBc3Path,
                4,
                4,
                [transparentBlock])
            .GetAwaiter()
            .GetResult();
        var transparentBc3 = inspector.InspectAsync(transparentBc3Path).GetAwaiter().GetResult();
        Assert(
            transparentBc3.Issues.Any(issue => issue.Code == "DDS_BC3_ALPHA_MOSTLY_TRANSPARENT"),
            "mostly transparent BC3 alpha did not produce an editor-visibility warning");
        Assert(
            transparentBc3.Properties.Any(property =>
                property.Name == "Nearly transparent pixels" && property.Value.StartsWith("100.0%", StringComparison.Ordinal)),
            "BC3 top-mip alpha coverage was not reported");
        Assert(
            transparentBc3.Properties.Any(property =>
                property.Name == "Likely role" && property.Value.Contains("diffuse", StringComparison.OrdinalIgnoreCase)),
            "diffuse filename role was not inferred");

        var originalBc3 = File.ReadAllBytes(transparentBc3Path);
        var rgbViewPath = Path.Combine(fixtureDirectory, "fixture_face_d-rgb-view-opaque-alpha.dds");
        var rgbViewWrite = DdsBc3RgbViewWriter
            .CreateAsync(transparentBc3Path, rgbViewPath)
            .GetAwaiter()
            .GetResult();
        var rgbViewBytes = File.ReadAllBytes(rgbViewPath);
        Assert(File.ReadAllBytes(transparentBc3Path).SequenceEqual(originalBc3),
            "RGB-view creation modified the authoritative source DDS");
        Assert(rgbViewBytes.AsSpan(136, 8).SequenceEqual(originalBc3.AsSpan(136, 8)),
            "RGB-view creation changed BC3 colour-block bytes");
        Assert(rgbViewBytes[128] == 255 && rgbViewBytes[129] == 255 &&
               rgbViewBytes.AsSpan(130, 6).IndexOfAnyExcept((byte)0) < 0,
            "RGB-view copy did not force its BC3 alpha block opaque");
        Assert(rgbViewWrite.AlphaBlocksOverridden == 1,
            "RGB-view copy recorded the wrong alpha-block count");
        var rgbViewInspection = inspector.InspectAsync(rgbViewPath).GetAwaiter().GetResult();
        Assert(!rgbViewInspection.Issues.Any(issue =>
                issue.Code == "DDS_BC3_ALPHA_MOSTLY_TRANSPARENT"),
            "opaque-alpha RGB-view copy remained mostly transparent");
        Assert(rgbViewInspection.Properties.Any(property =>
                property.Name == "Nearly opaque pixels" &&
                property.Value.StartsWith("100.0%", StringComparison.Ordinal)),
            "opaque-alpha RGB-view coverage was not verified");
    }

    private static void TestGltfArtifactInspection()
    {
        var path = Path.Combine(fixtureDirectory, "fixture.gltf");
        var bufferPath = Path.Combine(fixtureDirectory, "mesh.bin");
        File.WriteAllBytes(bufferPath, new byte[12]);
        File.WriteAllText(
            path,
            """
            {
              "asset": { "version": "2.0", "generator": "Arkive fixture" },
              "scene": 0,
              "scenes": [{ "nodes": [0] }],
              "nodes": [{ "mesh": 0 }],
              "buffers": [{ "uri": "mesh.bin", "byteLength": 12 }],
              "bufferViews": [{ "buffer": 0, "byteOffset": 0, "byteLength": 12 }],
              "accessors": [
                { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [-1, -2, -3], "max": [1, 2, 3] },
                { "bufferView": 0, "componentType": 5123, "count": 3, "type": "SCALAR" }
              ],
              "meshes": [{ "primitives": [{ "attributes": { "POSITION": 0 }, "indices": 1 }] }]
            }
            """);

        var inspector = new ExportArtifactInspector();
        var result = inspector.InspectAsync(path).GetAwaiter().GetResult();
        Assert(result.Format == "glTF", "glTF inspector returned the wrong format");
        Assert(result.ErrorCount == 0, "valid glTF fixture produced structural errors");
        Assert(result.Dependencies.Count == 1 && result.Dependencies[0].Exists,
            "glTF buffer dependency was not resolved");
        Assert(
            result.Properties.Any(property => property.Name == "Triangles" && property.Value == "1"),
            "glTF triangle count was not calculated");

        File.Delete(bufferPath);
        var missing = inspector.InspectAsync(path).GetAwaiter().GetResult();
        Assert(
            missing.Issues.Any(issue => issue.Code == "GLTF_DEPENDENCY_MISSING"),
            "missing glTF dependency was not detected");
    }

    private static void TestGlbArtifactInspection()
    {
        var path = Path.Combine(fixtureDirectory, "fixture.glb");
        var json = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":4}]}");
        var paddedJsonLength = (json.Length + 3) & ~3;
        var totalLength = 12 + 8 + paddedJsonLength + 8 + 4;
        var bytes = new byte[totalLength];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)totalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)paddedJsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x4E4F534A);
        bytes.AsSpan(20, paddedJsonLength).Fill(0x20);
        json.CopyTo(bytes, 20);
        var binHeader = 20 + paddedJsonLength;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(binHeader), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(binHeader + 4), 0x004E4942);
        File.WriteAllBytes(path, bytes);

        var result = new ExportArtifactInspector().InspectAsync(path).GetAwaiter().GetResult();
        Assert(result.Format == "GLB", "GLB inspector returned the wrong format");
        Assert(result.ErrorCount == 0, "valid GLB fixture produced structural errors");
        Assert(
            result.Dependencies.Count == 1 && result.Dependencies[0].IsEmbedded,
            "GLB embedded buffer was not reported");
    }

}
