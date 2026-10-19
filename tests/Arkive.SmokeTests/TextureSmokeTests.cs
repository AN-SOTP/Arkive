namespace Arkive.SmokeTests;

internal static class TextureSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("reconnoitres a native Texture2D payload", TestTextureReconnaissance),
        new("decodes Lost Ark extended texture property tags", TestLostArkTextureProperties),
        new("loads the native Crunch decoder runtime", TestNativeCrunchRuntime),
        new("decodes Unity Crunch DXT1 blocks exactly", TestUnityCrunchDxt1Regression),
        new("decodes Unity Crunch DXT5 blocks exactly", TestUnityCrunchDxt5Regression),
        new("decodes Unity Crunch BC5 blocks exactly", TestUnityCrunchBc5Regression),
        new("decodes Lost Ark UE3 LZ4 bulk mips", TestLostArkBulkMipDecoder),
        new("encodes deterministic LZ4 and UE3 bulk mips", TestLostArkBulkMipEncoder),
        new("exports a gated native DXT5 Crunch texture", TestNativeTextureExport),
        new("exports a gated native DXT1 Crunch texture", TestNativeDxt1TextureExport),
        new("exports a gated native BC5 Crunch texture", TestNativeBc5TextureExport),
        new("round-trips Arkive editable PNG pixels", TestEditablePngCodec),
        new("compares editable textures and writes an amplified diff", TestTextureEditComparison),
        new("exports confirmed Crunch mips when the optional boolean tag is absent", TestNativeCrunchInference),
        new("creates deterministic alpha-safe numbered face overlays", TestFaceTextureNumberOverlay),
        new("creates a fully visible editable PNG for native texture exports", TestNativeEditablePngExport),
        new("rebuilds BC mip chains while preserving unchanged blocks", TestTextureBlockRebuild),
        new("writes, reads and rebuilds uncompressed 32-bit textures (LUTs, sky layers) pixel by pixel", TestUncompressedTextureRebuild),
        new("normalizes a GIMP-style DDS through a legacy texture manifest", TestTextureImportCandidate),
        new("builds a BC1 texture import candidate without inventing an alpha channel", TestBc1TextureImportCandidate),
        new("encodes pinned Unity CRN payloads for BC1, BC3, and BC5", TestPinnedUnityCrunchEncoder,
            SmokeTestPrerequisites.UnityCrunchEncoder),
        new("builds the complete Texture2D replacement chain through a replacement-bearing staging UPK", TestTextureReplacementPayloadWorkflow,
            SmokeTestPrerequisites.UnityCrunchEncoder),
        new("builds BC1 and BC5 replacement payload workflows", TestTextureReplacementPayloadFormats,
            SmokeTestPrerequisites.UnityCrunchEncoder),
        new("refuses native export without verified Crunch", TestNativeTextureExportRefusal),
    ];

    private static void TestTextureReconnaissance()
    {
        var path = Path.Combine(fixtureDirectory, "texture-recon.upk");
        var range = WriteTextureReconPackage(path);
        var report = new TextureReconnaissanceService()
            .InspectAsync(new TextureReconnaissanceRequest(
                path,
                "texture-recon",
                1,
                AssetKind.Texture,
                "Texture2D",
                "TestTexture",
                range.SerialOffset,
                range.SerialSize))
            .GetAwaiter()
            .GetResult();

        Assert(report.PackageSha256.Length == 64, "package SHA-256 was not recorded");
        Assert(report.ExportPayloadSha256.Length == 64, "export SHA-256 was not recorded");
        Assert(report.Properties.Count == 3, "Texture2D properties were not parsed");
        Assert(report.Properties.Any(property =>
            property.Name == "Format" && property.NumericValue == 5),
            "Texture2D pixel format property was not decoded");
        Assert(report.PixelFormatEvidence.Contains("PF_DXT1", StringComparison.Ordinal),
            "DXT1 format evidence was not resolved");
        Assert(report.MipCandidates.Count > 0, "mip array candidate was not found");
        var candidate = report.MipCandidates[0];
        Assert(candidate.Confidence == TextureReconnaissanceConfidence.Confirmed,
            "synthetic mip evidence was not confirmed");
        Assert(candidate.Mips.Count == 1, "unexpected synthetic mip count");
        Assert(candidate.Mips[0].Storage == "Inline", "inline mip storage was not identified");
        Assert(candidate.Mips[0].Width == 4 && candidate.Mips[0].Height == 4,
            "mip dimensions were not decoded");
        Assert(candidate.Mips[0].PayloadSha256?.Length == 64,
            "inline mip payload was not hashed");

        var entry = new TextureReconnaissanceWorkspaceStore(
                Path.Combine(fixtureDirectory, "texture-recon-workspace"))
            .SaveAsync(report)
            .GetAwaiter()
            .GetResult();
        Assert(File.Exists(entry.JsonPath), "texture reconnaissance JSON was not written");
        Assert(File.Exists(entry.TextPath), "texture reconnaissance text report was not written");
    }

    private static void TestLostArkTextureProperties()
    {
        var path = Path.Combine(fixtureDirectory, "texture-recon-lostark-tags.upk");
        var range = WriteTextureReconPackage(path, lostArkTagExtensions: true);
        var report = new TextureReconnaissanceService()
            .InspectAsync(new TextureReconnaissanceRequest(
                path,
                "texture-recon-lostark-tags",
                1,
                AssetKind.Texture,
                "Texture2D",
                "TestTexture",
                range.SerialOffset,
                range.SerialSize))
            .GetAwaiter()
            .GetResult();

        Assert(report.PropertyStreamRelativeOffset == 4,
            "Lost Ark property stream preamble was not skipped");
        Assert(report.Properties.Count == 6, "Lost Ark extended properties were not parsed");
        Assert(report.Properties.Count(property =>
            property.Type == "IntProperty" &&
            property.TypeMetadata?.StartsWith("Lost Ark tag marker:", StringComparison.Ordinal) == true) == 4,
            "Lost Ark IntProperty tag markers were not consumed");
        Assert(report.Properties.Any(property =>
            property.Name == "Format" && property.ValuePreview == "PF_DXT5" && property.NumericValue == 7),
            "Lost Ark FName-backed pixel format was not decoded");
        Assert(report.PixelFormatEvidence.Contains("PF_DXT5", StringComparison.Ordinal),
            "Lost Ark DXT5 format evidence was not resolved");
        Assert(report.Properties.Any(property =>
            property.Name == "bUseCrunchCompression" && property.NumericValue == 1),
            "Lost Ark Crunch-compression property was not decoded");
        Assert(report.NativeExportIndex == 1, "native export index was not resolved");
        Assert(report.MipCandidates.First().Confidence == TextureReconnaissanceConfidence.Confirmed,
            "Lost Ark extended property fixture did not confirm its mip layout");
    }

    private static void TestNativeCrunchRuntime()
    {
        var decoder = new NativeCrunchDecoder();
        Assert(decoder.Name.Contains("Unity Crunch", StringComparison.Ordinal),
            "native decoder did not identify the Lost Ark-compatible Unity fork");
        Assert(decoder.Name.Contains("CRND 1.04", StringComparison.Ordinal),
            "native Crunch decoder version was not reported");
        try
        {
            _ = decoder.Inspect(new byte[64]);
            throw new InvalidOperationException("invalid CRN data was accepted");
        }
        catch (CrunchDecodeException exception)
        {
            Assert(exception.Status is 2 or 8 or 9, "unexpected invalid-CRN status was returned");
        }

        var headerOnlyCrn = new byte[74];
        headerOnlyCrn[0] = (byte)'H';
        headerOnlyCrn[1] = (byte)'x';
        BinaryPrimitives.WriteUInt16BigEndian(headerOnlyCrn.AsSpan(2), 74);
        BinaryPrimitives.WriteUInt32BigEndian(headerOnlyCrn.AsSpan(6), 74);
        BinaryPrimitives.WriteUInt16BigEndian(headerOnlyCrn.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt16BigEndian(headerOnlyCrn.AsSpan(14), 4);
        headerOnlyCrn[16] = 1;
        headerOnlyCrn[17] = 1;
        headerOnlyCrn[18] = NativeCrunchDecoder.Dxt5Format;
        try
        {
            _ = decoder.Inspect(headerOnlyCrn);
            throw new InvalidOperationException("bad-checksum CRN data was accepted");
        }
        catch (CrunchDecodeException exception)
        {
            Assert(exception.Status == 7,
                $"valid Hx header did not reach checksum validation (status {exception.Status})");
        }
    }

    private static void TestUnityCrunchDxt1Regression()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "unity-crunch-dxt1.crn");
        var decoded = new NativeCrunchDecoder().DecodeDxt1(File.ReadAllBytes(path));
        Assert(decoded.Length == 128, "Unity Crunch 16x16 DXT1 output size was incorrect");
        Assert(
            Convert.ToHexString(SHA256.HashData(decoded)) ==
            "D02745C73A2A832605F0DD74D597779B82925ACB47A0378451A4B9A08FA61FA8",
            "Unity Crunch DXT1 payload differed from the trusted transcoder output");
    }

    private static void TestUnityCrunchDxt5Regression()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "unity-crunch-dxt5.crn");
        var decoded = new NativeCrunchDecoder().DecodeDxt5(File.ReadAllBytes(path));
        Assert(decoded.Length == 256, "Unity Crunch 16x16 DXT5 output size was incorrect");
        Assert(
            Convert.ToHexString(SHA256.HashData(decoded)) ==
            "A73D55B01BD720233665BF6978117F637A386D903A8188B03F6528A3BA088A1F",
            "Unity Crunch DXT5 payload differed from the trusted transcoder output");
    }

    private static void TestUnityCrunchBc5Regression()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "unity-crunch-bc5.crn");
        var decoder = new NativeCrunchDecoder();
        var info = decoder.Inspect(File.ReadAllBytes(path));
        Assert(info.Format == NativeCrunchDecoder.DxnXyFormat && info.FourCc == "A2XY",
            "Unity Crunch BC5 fixture was not identified as DXN_XY/A2XY");
        var decoded = decoder.DecodeBc5(File.ReadAllBytes(path));
        Assert(decoded.Length == 256, "Unity Crunch 16x16 BC5 output size was incorrect");
        Assert(
            Convert.ToHexString(SHA256.HashData(decoded)) ==
            "CD82083179433FC93D7688308EE6A6F901C6E8EB42AD1E1AC02D6687D4D66580",
            "Unity Crunch BC5 payload differed from the trusted transcoder output");
    }

    private static void TestLostArkBulkMipDecoder()
    {
        var expected = Enumerable.Range(0, 256).Select(index => (byte)index).ToArray();
        var compressed = EncodeLz4LiteralBlock(expected);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(PackageConstants.UnrealPackageTag);
            writer.Write(0x20000);
            writer.Write(compressed.Length);
            writer.Write(expected.Length);
            writer.Write(compressed.Length);
            writer.Write(expected.Length);
            writer.Write(compressed);
        }

        var decoded = Ue3BulkMipDecoder.DecodeLostArkLz4(
            stream.ToArray(), expected.Length, out var info);
        Assert(decoded.SequenceEqual(expected), "Lost Ark UE3 bulk LZ4 bytes were incorrect");
        Assert(info.BlockCount == 1 && info.HeaderBytes == 24,
            "Lost Ark UE3 bulk block metadata was incorrect");
        Assert(info.Codec.Contains("LZ4", StringComparison.Ordinal),
            "Lost Ark UE3 bulk codec was not identified");
    }

    private static void TestLostArkBulkMipEncoder()
    {
        var expected = Enumerable.Range(0, 4096)
            .Select(index => (byte)((index / 17 + index * 3) & 0xFF))
            .ToArray();
        var compressed = Arkive.Core.Compression.Lz4BlockEncoder.Encode(expected);
        var decoded = Arkive.Core.Compression.Lz4BlockDecoder.Decode(compressed, expected.Length);
        Assert(decoded.SequenceEqual(expected), "managed LZ4 encoder did not round-trip");
        Assert(
            Arkive.Core.Compression.Lz4BlockEncoder.Encode(expected).SequenceEqual(compressed),
            "managed LZ4 encoder output was not deterministic");
        var literalOnly = Arkive.Core.Compression.Lz4BlockEncoder.EncodeLiteralOnly(expected);
        Assert(Arkive.Core.Compression.Lz4BlockDecoder.Decode(literalOnly, expected.Length)
                .SequenceEqual(expected),
            "literal-only LZ4 encoder did not round-trip");

        var bulk = Ue3BulkMipEncoder.EncodeLostArkLz4(expected, blockSize: 1024);
        var bulkDecoded = Ue3BulkMipDecoder.DecodeLostArkLz4(
            bulk, expected.Length, out var info);
        Assert(bulkDecoded.SequenceEqual(expected), "UE3/LZ4 encoder did not round-trip exactly");
        Assert(info.BlockSize == 1024 && info.BlockCount == 4,
            "UE3/LZ4 encoder did not retain the requested block geometry");

        var tiny = Enumerable.Range(0, 16).Select(index => (byte)index).ToArray();
        var tinyBulk = Ue3BulkMipEncoder.EncodeLostArkLz4(tiny);
        var tinyStored = BinaryPrimitives.ReadInt32LittleEndian(tinyBulk.AsSpan(16));
        Assert(tinyStored != tiny.Length,
            "tiny UE3 bulk mip incorrectly fell back to raw bytes under a compressed flag");
        Assert(Ue3BulkMipDecoder.DecodeLostArkLz4(tinyBulk, tiny.Length, out _).SequenceEqual(tiny),
            "tiny UE3/LZ4 bulk mip did not decode after forced LZ4 emission");

        var invalidEqualSizeRaw = new byte[24 + tiny.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(
            invalidEqualSizeRaw, PackageConstants.UnrealPackageTag);
        BinaryPrimitives.WriteInt32LittleEndian(invalidEqualSizeRaw.AsSpan(4), 0x20000);
        BinaryPrimitives.WriteInt32LittleEndian(invalidEqualSizeRaw.AsSpan(8), tiny.Length);
        BinaryPrimitives.WriteInt32LittleEndian(invalidEqualSizeRaw.AsSpan(12), tiny.Length);
        BinaryPrimitives.WriteInt32LittleEndian(invalidEqualSizeRaw.AsSpan(16), tiny.Length);
        BinaryPrimitives.WriteInt32LittleEndian(invalidEqualSizeRaw.AsSpan(20), tiny.Length);
        tiny.CopyTo(invalidEqualSizeRaw, 24);
        try
        {
            _ = Ue3BulkMipDecoder.DecodeLostArkLz4(invalidEqualSizeRaw, tiny.Length, out _);
            throw new InvalidOperationException(
                "UE3/LZ4 decoder accepted equal-size raw bytes under the compressed flag");
        }
        catch (InvalidDataException)
        {
            // UModel also rejects this legacy Arkive representation.
        }
    }

    private static void TestNativeTextureExport()
    {
        var path = Path.Combine(fixtureDirectory, "native-texture-export.upk");
        var range = WriteTextureReconPackage(path, lostArkTagExtensions: true);
        var report = new TextureReconnaissanceService()
            .InspectAsync(new TextureReconnaissanceRequest(
                path,
                "native-texture-export",
                0,
                AssetKind.Texture,
                "Texture2D",
                "TestTexture",
                range.SerialOffset,
                range.SerialSize))
            .GetAwaiter()
            .GetResult();
        var reconJson = Path.Combine(fixtureDirectory, "native-texture-recon.json");
        File.WriteAllText(reconJson, "{}");
        var workspace = new ExportWorkspaceStore(Path.Combine(fixtureDirectory, "native-export-workspace"));
        var outputDirectory = workspace.CreateSessionDirectory(report.PackageAlias, report.ObjectName);
        var result = new NativeTextureExportService(crunchDecoder: new SyntheticCrunchDecoder())
            .ExportAsync(new NativeTextureExportRequest(report, outputDirectory, reconJson))
            .GetAwaiter()
            .GetResult();

        Assert(File.Exists(result.DdsPath), "native DDS was not written");
        Assert(File.Exists(result.ManifestPath), "native texture manifest was not written");
        Assert(result.Manifest.MipCount == 1, "native manifest mip count was incorrect");
        Assert(result.Manifest.PixelFormat == "PF_DXT5 / BC3", "native manifest format was incorrect");
        Assert(result.Manifest.Mips[0].StoredPayloadBytes == 16, "stored mip bytes were not recorded");
        Assert(result.Manifest.Mips[0].StorageCodec == "Crunch CRN / DXT5",
            "stored mip codec was not recorded");
        Assert(result.Manifest.Mips[0].DecodedGpuBytes == 16, "decoded BC3 bytes were not recorded");
        Assert(result.DdsInspection.ErrorCount == 0, "generated DDS failed structural inspection");
        Assert(new FileInfo(result.DdsPath).Length == DdsBc3Writer.HeaderBytes + 16,
            "generated DDS length was incorrect");
        var dds = File.ReadAllBytes(result.DdsPath);
        Assert(dds.AsSpan(DdsBc3Writer.HeaderBytes).SequenceEqual(SyntheticCrunchDecoder.DecodedBlock),
            "generated DDS did not contain the decoded BC3 block");

        var workspaceEntry = workspace
            .CommitNativeTextureAsync(result, report, [])
            .GetAwaiter()
            .GetResult();
        Assert(workspaceEntry.Manifest.ExportMethod == "Arkive native PF_DXT5 / BC3 DDS",
            "native export method was not recorded in the workspace");
        Assert(workspaceEntry.Manifest.NativeExportIndex == 1,
            "native UE export index was not recorded in the workspace");
        var snapshot = workspace.LoadAsync().GetAwaiter().GetResult();
        Assert(snapshot.Entries.Count == 1, "native export workspace session was not reloaded");
    }

    private static void TestNativeDxt1TextureExport()
    {
        var path = Path.Combine(fixtureDirectory, "native-dxt1-texture-export.upk");
        var range = WriteTextureReconPackage(
            path,
            lostArkTagExtensions: true,
            pixelFormatName: "PF_DXT1");
        var report = new TextureReconnaissanceService()
            .InspectAsync(new TextureReconnaissanceRequest(
                path,
                "native-dxt1-texture-export",
                0,
                AssetKind.Texture,
                "Texture2D",
                "TestTexture",
                range.SerialOffset,
                range.SerialSize))
            .GetAwaiter()
            .GetResult();
        var reconJson = Path.Combine(fixtureDirectory, "native-dxt1-texture-recon.json");
        File.WriteAllText(reconJson, "{}");
        var output = Path.Combine(fixtureDirectory, "native-dxt1-output");
        var result = new NativeTextureExportService(
                crunchDecoder: new SyntheticCrunchDecoder(NativeTextureFormats.Dxt1))
            .ExportAsync(new NativeTextureExportRequest(report, output, reconJson))
            .GetAwaiter()
            .GetResult();

        Assert(result.Manifest.PixelFormat == "PF_DXT1 / BC1", "DXT1 manifest format was incorrect");
        Assert(result.Manifest.Mips[0].StorageCodec == "Crunch CRN / DXT1",
            "DXT1 stored mip codec was incorrect");
        Assert(result.Manifest.Mips[0].DecodedGpuBytes == 8,
            "decoded BC1 bytes were not recorded");
        Assert(result.DdsInspection.ErrorCount == 0, "generated DXT1 DDS failed inspection");
        var dds = File.ReadAllBytes(result.DdsPath);
        Assert(Encoding.ASCII.GetString(dds, 84, 4) == "DXT1", "DDS FourCC was not DXT1");
        Assert(dds.AsSpan(DdsBc1Writer.HeaderBytes).SequenceEqual(SyntheticCrunchDecoder.Dxt1DecodedBlock),
            "generated DDS did not contain the decoded BC1 block");
    }

    private static void TestNativeBc5TextureExport()
    {
        var path = Path.Combine(fixtureDirectory, "native-bc5-texture-export.upk");
        var range = WriteTextureReconPackage(
            path,
            lostArkTagExtensions: true,
            pixelFormatName: "PF_BC5");
        var report = new TextureReconnaissanceService()
            .InspectAsync(new TextureReconnaissanceRequest(
                path,
                "native-bc5-texture-export",
                0,
                AssetKind.Texture,
                "Texture2D",
                "TestNormal",
                range.SerialOffset,
                range.SerialSize))
            .GetAwaiter()
            .GetResult();
        var reconJson = Path.Combine(fixtureDirectory, "native-bc5-texture-recon.json");
        File.WriteAllText(reconJson, "{}");
        var output = Path.Combine(fixtureDirectory, "native-bc5-output");
        var result = new NativeTextureExportService(
                crunchDecoder: new SyntheticCrunchDecoder(NativeTextureFormats.Bc5))
            .ExportAsync(new NativeTextureExportRequest(report, output, reconJson))
            .GetAwaiter()
            .GetResult();

        Assert(result.Manifest.PixelFormat == "PF_BC5 / BC5", "BC5 manifest format was incorrect");
        Assert(result.Manifest.Mips[0].StorageCodec == "Crunch CRN / A2XY",
            "BC5 CRN storage codec was incorrect");
        Assert(result.Manifest.Mips[0].DecodedGpuBytes == 16,
            "decoded BC5 bytes were not recorded");
        Assert(result.DdsInspection.ErrorCount == 0, "generated BC5 DDS failed inspection");
        var dds = File.ReadAllBytes(result.DdsPath);
        Assert(Encoding.ASCII.GetString(dds, 84, 4) == "ATI2", "BC5 DDS FourCC was not ATI2");
        Assert(dds.AsSpan(DdsBlockCompressedWriter.HeaderBytes).SequenceEqual(SyntheticCrunchDecoder.DecodedBlock),
            "generated DDS did not contain the decoded BC5 block");
    }

    private static void TestEditablePngCodec()
    {
        var pixels = new byte[]
        {
            1, 2, 3, 4, 10, 20, 30, 40, 50, 60, 70, 80,
            90, 100, 110, 120, 130, 140, 150, 160, 200, 210, 220, 230,
        };
        var path = Path.Combine(fixtureDirectory, "editable-pixels.png");
        PngRgbaCodec.WriteAsync(path, new RgbaImage(3, 2, pixels))
            .GetAwaiter()
            .GetResult();
        var decoded = PngRgbaCodec.ReadAsync(path).GetAwaiter().GetResult();
        Assert(decoded.Width == 3 && decoded.Height == 2, "editable PNG dimensions changed");
        Assert(decoded.Pixels.SequenceEqual(pixels), "editable PNG pixels did not round-trip exactly");
        var inspection = new ExportArtifactInspector().InspectAsync(path).GetAwaiter().GetResult();
        Assert(inspection.Format == "PNG" && inspection.ErrorCount == 0,
            "editable PNG failed artifact inspection");
    }

    private static void TestTextureEditComparison()
    {
        var directory = Path.Combine(fixtureDirectory, "texture-comparison");
        Directory.CreateDirectory(directory);
        var originalPath = Path.Combine(directory, "original.png");
        var editedPath = Path.Combine(directory, "edited.png");
        var originalPixels = new byte[]
        {
            10, 20, 30, 255,
            40, 50, 60, 128,
        };
        var editedPixels = originalPixels.ToArray();
        editedPixels[4] = 50;
        PngRgbaCodec.WriteAsync(originalPath, new RgbaImage(2, 1, originalPixels))
            .GetAwaiter().GetResult();
        PngRgbaCodec.WriteAsync(editedPath, new RgbaImage(2, 1, editedPixels))
            .GetAwaiter().GetResult();
        var result = TextureEditComparisonService.CompareAsync(
                originalPath, editedPath, Path.Combine(directory, "output"))
            .GetAwaiter().GetResult();
        var difference = PngRgbaCodec.ReadAsync(result.DifferenceImagePath)
            .GetAwaiter().GetResult();
        Assert(result.ChangedPixels == 1 && Math.Abs(result.ChangedPercent - 50d) < 0.001 &&
               result.MaximumChannelDelta == 10,
            "texture comparison statistics were incorrect");
        Assert(difference.Pixels[4] == 40 && difference.Pixels[7] == 255,
            "amplified texture difference image was incorrect");
    }

    private static void TestNativeCrunchInference()
    {
        var path = Path.Combine(fixtureDirectory, "native-inferred-crunch-texture.upk");
        var compressed = new byte[] { (byte)'H', (byte)'x', 1, 2, 3, 4, 5, 6 };
        var range = WriteTextureReconPackage(
            path,
            lostArkTagExtensions: true,
            storedMipOverride: compressed,
            omitCrunchProperty: true,
            mipElementCountOverride: 16);
        var report = new TextureReconnaissanceService()
            .InspectAsync(new TextureReconnaissanceRequest(
                path,
                "native-inferred-crunch-texture",
                0,
                AssetKind.Texture,
                "Texture2D",
                "TestTexture",
                range.SerialOffset,
                range.SerialSize))
            .GetAwaiter().GetResult();
        Assert(!report.Properties.Any(property =>
                property.Name.Equals("bUseCrunchCompression", StringComparison.OrdinalIgnoreCase)),
            "inferred-Crunch fixture unexpectedly retained the optional boolean tag");
        Assert(NativeTextureExportService.TryValidateReconnaissanceGate(
                report, out var profile, out _),
            "compressed-size evidence did not satisfy the native Crunch gate");
        Assert(profile == NativeTextureFormats.Dxt5,
            "inferred-Crunch fixture resolved to the wrong native format");
        var reconJson = Path.Combine(fixtureDirectory, "native-inferred-crunch-recon.json");
        File.WriteAllText(reconJson, "{}");
        var result = new NativeTextureExportService(
                crunchDecoder: new SyntheticCrunchDecoder(NativeTextureFormats.Dxt5))
            .ExportAsync(new NativeTextureExportRequest(
                report,
                Path.Combine(fixtureDirectory, "native-inferred-crunch-output"),
                reconJson))
            .GetAwaiter().GetResult();
        Assert(result.DdsInspection.ErrorCount == 0,
            "inferred-Crunch native export failed DDS validation");
    }

    private static void TestFaceTextureNumberOverlay()
    {
        Assert(FaceTextureMappingService.TryParseDiffuseObjectName(
                   "pc_wr_f", "pc_wr_f_18_face_d", out var legacyNumber) && legacyNumber == 18,
            "legacy numbered face texture names were not recognized");
        Assert(FaceTextureMappingService.TryParseDiffuseObjectName(
                   "pc_ft", "pc_ft_face_15_d", out var alternateNumber) && alternateNumber == 15,
            "alternate Female Martial Artist face texture names were not recognized");
        Assert(!FaceTextureMappingService.TryParseDiffuseObjectName(
                   "pc_ft", "pc_ft_face_15_n", out _),
            "numbered normal texture was incorrectly accepted as a diffuse face map candidate");

        var original = new byte[96 * 96 * 4];
        for (var pixel = 0; pixel < original.Length / 4; pixel++)
        {
            original[pixel * 4] = 80;
            original[pixel * 4 + 1] = 100;
            original[pixel * 4 + 2] = 120;
            original[pixel * 4 + 3] = (byte)(pixel % 251);
        }
        var first = Path.Combine(fixtureDirectory, "face-map-07-a.png");
        var second = Path.Combine(fixtureDirectory, "face-map-07-b.png");
        PngRgbaCodec.WriteAsync(first, new RgbaImage(96, 96, original.ToArray()))
            .GetAwaiter().GetResult();
        PngRgbaCodec.WriteAsync(second, new RgbaImage(96, 96, original.ToArray()))
            .GetAwaiter().GetResult();
        FaceTextureNumberOverlay.ApplyAsync(first, 7).GetAwaiter().GetResult();
        FaceTextureNumberOverlay.ApplyAsync(second, 7).GetAwaiter().GetResult();
        var mapped = PngRgbaCodec.ReadAsync(first).GetAwaiter().GetResult();
        var mappedAgain = PngRgbaCodec.ReadAsync(second).GetAwaiter().GetResult();

        Assert(mapped.Pixels.SequenceEqual(mappedAgain.Pixels),
            "numbered face overlay was not deterministic");
        Assert(!mapped.Pixels.SequenceEqual(original),
            "numbered face overlay did not change RGB pixels");
        Assert(Enumerable.Range(0, 96 * 96).All(pixel =>
                mapped.Pixels[pixel * 4 + 3] == original[pixel * 4 + 3]),
            "numbered face overlay changed the source alpha channel");
        Assert(Enumerable.Range(0, 96 * 96).Any(pixel =>
                mapped.Pixels[pixel * 4] == 255 &&
                mapped.Pixels[pixel * 4 + 1] == 238 &&
                mapped.Pixels[pixel * 4 + 2] == 0),
            "numbered face overlay did not render its high-contrast digits");
    }

    private static void TestNativeEditablePngExport()
    {
        var path = Path.Combine(fixtureDirectory, "native-alpha-masked-texture-export.upk");
        var range = WriteTextureReconPackage(path, lostArkTagExtensions: true);
        var report = new TextureReconnaissanceService()
            .InspectAsync(new TextureReconnaissanceRequest(
                path,
                "native-alpha-masked-texture-export",
                0,
                AssetKind.Texture,
                "Texture2D",
                "TestTexture",
                range.SerialOffset,
                range.SerialSize))
            .GetAwaiter()
            .GetResult();
        var reconJson = Path.Combine(fixtureDirectory, "native-alpha-masked-recon.json");
        File.WriteAllText(reconJson, "{}");
        var output = Path.Combine(fixtureDirectory, "native-alpha-masked-output");
        var transparentBlock = new byte[16];
        transparentBlock[0] = 0;
        transparentBlock[1] = 255;
        var result = new NativeTextureExportService(
                crunchDecoder: new SyntheticCrunchDecoder(
                    NativeTextureFormats.Dxt5,
                    transparentBlock))
            .ExportAsync(new NativeTextureExportRequest(report, output, reconJson))
            .GetAwaiter()
            .GetResult();

        Assert(result.DdsInspection.Issues.Any(issue =>
                issue.Code == "DDS_BC3_ALPHA_MOSTLY_TRANSPARENT"),
            "alpha-masked native export did not retain its channel warning");
        Assert(!string.IsNullOrWhiteSpace(result.Manifest.EditImageRelativePath),
            "native export did not record its editable PNG");
        var editPath = Path.Combine(output, result.Manifest.EditImageRelativePath!);
        Assert(File.Exists(editPath), "native editable PNG was not written");
        Assert(result.OutputFiles.Contains(editPath, StringComparer.OrdinalIgnoreCase),
            "native export inventory omitted its editable PNG");
        var edit = PngRgbaCodec.ReadAsync(editPath).GetAwaiter().GetResult();
        Assert(edit.Width == 4 && edit.Height == 4, "native editable PNG dimensions were wrong");
        Assert(Enumerable.Range(0, 16).All(pixel => edit.Pixels[pixel * 4 + 3] == 255),
            "native editable PNG was not fully visible");
        Assert(result.Manifest.EditImageSemantic == TextureEditSemantic.Colour.ToString(),
            "native editable PNG semantic was not recorded");
        Assert(result.Manifest.RgbViewDdsRelativePath is null,
            "new native export still emitted the obsolete opaque-alpha DDS copy");
    }

    private static void TestUncompressedTextureRebuild()
    {
        var directory = Path.Combine(fixtureDirectory, "bgra8");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "lut.dds");
        if (File.Exists(path)) File.Delete(path);
        // A 4x2 BGRA texture and its 2x1 mip: blue, green, red and alpha bytes per pixel.
        var top = Enumerable.Range(0, 8).SelectMany(pixel => new byte[] { (byte)(pixel * 10), 100, 200, (byte)(255 - pixel) }).ToArray();
        var mip = new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 };
        DdsBlockCompressedWriter.WriteAsync(path, 4, 2, NativeTextureFormats.Bgra8.DdsFormat, [top, mip]).GetAwaiter().GetResult();
        var texture = DdsBlockTexture.ReadAsync(path).GetAwaiter().GetResult();
        Assert(texture.Format == BlockTextureFormat.Bgra8 && texture.MipCount == 2 && texture.Mips[1].Length == 8 &&
               NativeTextureFormats.Bgra8.CalculateMipBytes(4, 2) == 32,
            "an uncompressed DDS was not read as 32-bit BGRA with its mips");
        var image = texture.DecodeMip(0, editableView: false);
        Assert(image.Pixels.AsSpan(0, 8).SequenceEqual(new byte[] { 200, 100, 0, 255, 200, 100, 10, 254 }),
            "BGRA pixels were not decoded to RGBA: " + string.Join(",", image.Pixels.Take(8)));

        // Change only the first two pixels of the top mip to pure red.
        var edited = texture.DecodeMip(0, editableView: true);
        for (var pixel = 0; pixel < 2; pixel++) new byte[] { 255, 0, 0, 255 }.CopyTo(edited.Pixels, pixel * 4);
        var rebuilt = TextureBlockRebuilder.Rebuild(texture, edited, "lv_test_lut", TextureImportAlphaPolicy.PreserveOriginal);
        var after = DdsBlockTexture.Parse(path, rebuilt.DdsBytes);
        var bytes = after.Bytes.AsSpan(after.Mips[0].Offset, 32).ToArray();
        Assert(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0, 0, 255, 255, 0, 0, 255, 254 }) &&
               bytes.AsSpan(8).SequenceEqual(top.AsSpan(8)),
            "the rebuild did not write only the edited pixels, in BGRA, keeping their alpha: " + string.Join(",", bytes));
        var lower = after.Bytes.AsSpan(after.Mips[1].Offset, 8).ToArray();
        Assert(!lower.AsSpan(0, 4).SequenceEqual(mip.AsSpan(0, 4)) && lower.AsSpan(4).SequenceEqual(mip.AsSpan(4)),
            "the lower mip was not redrawn where the edit reached it and kept elsewhere: " + string.Join(",", lower));
    }

    private static void TestTextureBlockRebuild()
    {
        var ddsPath = Path.Combine(fixtureDirectory, "rebuild-mips.dds");
        var block = new byte[16]
        {
            0, 255, 0, 0, 0, 0, 0, 0,
            0xFF, 0xFF, 0, 0, 0, 0, 0, 0,
        };
        var top = Enumerable.Range(0, 4).SelectMany(_ => block).ToArray();
        DdsBc3Writer.WriteAsync(
                ddsPath,
                8,
                8,
                new ReadOnlyMemory<byte>[] { top, block, block, block })
            .GetAwaiter()
            .GetResult();
        var original = DdsBlockTexture.ReadAsync(ddsPath).GetAwaiter().GetResult();
        var edited = original.DecodeMip(0, editableView: true);
        edited.Pixels[0] = edited.Pixels[1] = edited.Pixels[2] = 0;
        var rebuilt = TextureBlockRebuilder.Rebuild(
            original,
            edited,
            "TestDiffuse_d",
            TextureImportAlphaPolicy.PreserveOriginal);
        Assert(rebuilt.TotalBlocks == 7, "unexpected BC3 mip block count");
        Assert(rebuilt.TouchedBlocks == 4 && rebuilt.UnchangedBlocksCopied == 3,
            "localized edit did not preserve unaffected BC3 blocks");
        foreach (var mip in original.Mips)
        {
            for (var offset = 0; offset < mip.Length; offset += 16)
            {
                Assert(original.Bytes.AsSpan(mip.Offset + offset, 8)
                        .SequenceEqual(rebuilt.DdsBytes.AsSpan(mip.Offset + offset, 8)),
                    "BC3 rebuild changed authoritative alpha bytes");
            }
        }

        var candidatePath = Path.Combine(fixtureDirectory, "rebuild-mips-candidate.dds");
        File.WriteAllBytes(candidatePath, rebuilt.DdsBytes);
        var candidate = DdsBlockTexture.ReadAsync(candidatePath).GetAwaiter().GetResult();
        Assert(candidate.MipCount == 4 && candidate.Width == 8 && candidate.Height == 8,
            "BC3 rebuild did not retain the original mip layout");

        var bc5Path = Path.Combine(fixtureDirectory, "rebuild-normal.dds");
        DdsBlockCompressedWriter.WriteAsync(
                bc5Path,
                4,
                4,
                NativeTextureFormats.Bc5.DdsFormat,
                new ReadOnlyMemory<byte>[] { block })
            .GetAwaiter()
            .GetResult();
        var bc5 = DdsBlockTexture.ReadAsync(bc5Path).GetAwaiter().GetResult();
        var normalEdit = bc5.DecodeMip(0, editableView: true);
        normalEdit.Pixels[0] ^= 0x7F;
        var normalRebuild = TextureBlockRebuilder.Rebuild(
            bc5,
            normalEdit,
            "TestNormal_n",
            TextureImportAlphaPolicy.PreserveOriginal);
        Assert(normalRebuild.Semantic == TextureEditSemantic.NormalXY,
            "BC5 rebuild did not use normal-map semantics");
        Assert(normalRebuild.CandidateBlocksDifferentFromOriginal == 1,
            "BC5 edited block was not rebuilt");
    }

    private static void TestTextureImportCandidate()
    {
        var packagePath = Path.Combine(fixtureDirectory, "texture-import-candidate.upk");
        var range = WriteTextureReconPackage(packagePath, lostArkTagExtensions: true);
        var report = new TextureReconnaissanceService()
            .InspectAsync(new TextureReconnaissanceRequest(
                packagePath,
                "texture-import-candidate",
                0,
                AssetKind.Texture,
                "Texture2D",
                "TestFace_d",
                range.SerialOffset,
                range.SerialSize))
            .GetAwaiter()
            .GetResult();
        var reconJson = Path.Combine(fixtureDirectory, "texture-import-candidate-recon.json");
        File.WriteAllText(reconJson, "{}");
        var workspace = new ExportWorkspaceStore(
            Path.Combine(fixtureDirectory, "texture-import-candidate-workspace"));
        var nativeOutput = workspace.CreateSessionDirectory(report.PackageAlias, report.ObjectName);
        var transparentBlock = new byte[16]
        {
            0, 255, 0, 0, 0, 0, 0, 0,
            0x10, 0x20, 0x30, 0x40, 0x55, 0x66, 0x77, 0x88,
        };
        var nativeResult = new NativeTextureExportService(
                crunchDecoder: new SyntheticCrunchDecoder(
                    NativeTextureFormats.Dxt5,
                    transparentBlock))
            .ExportAsync(new NativeTextureExportRequest(report, nativeOutput, reconJson))
            .GetAwaiter()
            .GetResult();
        var sourceEntry = workspace
            .CommitNativeTextureAsync(nativeResult, report, [])
            .GetAwaiter()
            .GetResult();
        var editPngPath = Path.Combine(nativeOutput, nativeResult.Manifest.EditImageRelativePath!);
        var editImage = PngRgbaCodec.ReadAsync(editPngPath).GetAwaiter().GetResult();
        editImage.Pixels[0] = (byte)(255 - editImage.Pixels[0]);
        editImage.Pixels[1] = 0;
        editImage.Pixels[2] = 0;
        var editedPath = Path.Combine(fixtureDirectory, "TestFace_d-edited.dds");
        WriteUncompressedBgraDds(editedPath, editImage);
        var originalBefore = File.ReadAllBytes(nativeResult.DdsPath);
        var legacyManifestPath = Path.Combine(nativeOutput, "arkive-native-texture-schema3.json");
        var legacyJson = File.ReadAllText(nativeResult.ManifestPath)
            .Replace("\"schemaVersion\": 5", "\"schemaVersion\": 3", StringComparison.Ordinal);
        File.WriteAllText(legacyManifestPath, legacyJson);

        var candidateOutput = workspace.CreateSessionDirectory(
            report.PackageAlias,
            report.ObjectName + "-candidate");
        var result = new TextureImportCandidateService()
            .CreateAsync(new TextureImportCandidateRequest(
                legacyManifestPath,
                editedPath,
                candidateOutput,
                TextureImportAlphaPolicy.PreserveOriginal))
            .GetAwaiter()
            .GetResult();
        var candidate = File.ReadAllBytes(result.CandidateDdsPath);
        Assert(File.ReadAllBytes(nativeResult.DdsPath).SequenceEqual(originalBefore),
            "texture import candidate modified the authoritative DDS");
        Assert(candidate.AsSpan(0, 128).SequenceEqual(originalBefore.AsSpan(0, 128)),
            "texture import candidate did not retain the authoritative DDS header");
        Assert(candidate.AsSpan(128, 8).SequenceEqual(originalBefore.AsSpan(128, 8)),
            "texture import candidate did not preserve authoritative BC3 alpha");
        Assert(!candidate.AsSpan(136, 8).SequenceEqual(originalBefore.AsSpan(136, 8)),
            "texture import candidate did not encode the painted BC3 colour data");
        Assert(result.Manifest.AlphaPolicy == TextureImportAlphaPolicy.PreserveOriginal,
            "texture import candidate recorded the wrong alpha policy");
        Assert(result.Manifest.EditedColourBlocksDifferentFromOriginal == 1,
            "texture import candidate did not count edited colour blocks");
        Assert(result.Manifest.EditedInputFormat == "DDS uncompressed RGB32" &&
               result.Manifest.EditedInputMipCount == 1,
            "GIMP-style DDS was not recognized and normalized");
        Assert(result.Manifest.AutomaticNormalization && result.Manifest.GeneratedMipCount == 1,
            "automatic texture normalization was not recorded");
        Assert(result.Manifest.CandidateBlocksDifferentFromOriginal == 1,
            "texture import candidate did not count final changed blocks");
        Assert(result.Manifest.PreflightChecks.Any(check =>
                check.Code == "TIC_NATIVE_MANIFEST_COMPATIBILITY"),
            "legacy schema-3 native export was not accepted through compatibility mode");
        Assert(result.Manifest.PreflightChecks.Any(check =>
                check.Code == "TIC_PACKAGE_SNAPSHOT" &&
                check.Status == TextureImportPreflightStatus.Passed),
            "texture import candidate did not verify the source package snapshot");
        Assert(result.CandidateInspection.ErrorCount == 0,
            "texture import candidate failed structural DDS inspection");

        var candidateEntry = workspace
            .CommitTextureImportCandidateAsync(result, sourceEntry)
            .GetAwaiter()
            .GetResult();
        Assert(candidateEntry.Manifest.ParentExportId == sourceEntry.Manifest.ExportId,
            "candidate workspace entry did not retain its parent export identity");
        Assert(candidateEntry.Manifest.TextureImportCandidateManifestPath == result.ManifestPath,
            "candidate workspace entry did not retain its detailed manifest path");
        var snapshot = workspace.LoadAsync().GetAwaiter().GetResult();
        Assert(snapshot.Entries.Count == 2,
            "native export and texture import candidate were not both reloadable");

        var mismatchPath = Path.Combine(fixtureDirectory, "TestFace_d-wrong-size.png");
        PngRgbaCodec.WriteAsync(mismatchPath, new RgbaImage(3, 4, new byte[3 * 4 * 4]))
            .GetAwaiter()
            .GetResult();
        try
        {
            _ = new TextureImportCandidateService()
                .CreateAsync(new TextureImportCandidateRequest(
                    legacyManifestPath,
                    mismatchPath,
                    Path.Combine(fixtureDirectory, "wrong-size-candidate")))
                .GetAwaiter()
                .GetResult();
            throw new InvalidOperationException("dimension-mismatched edited DDS passed preflight");
        }
        catch (TextureImportCandidateRefusalException exception)
        {
            Assert(exception.Code == "TIC014",
                $"dimension mismatch produced unexpected refusal {exception.Code}");
        }
    }

    private static void TestBc1TextureImportCandidate()
    {
        var packagePath = Path.Combine(fixtureDirectory, "bc1-texture-import-candidate.upk");
        var range = WriteTextureReconPackage(
            packagePath,
            lostArkTagExtensions: true,
            pixelFormatName: "PF_DXT1");
        var report = new TextureReconnaissanceService()
            .InspectAsync(new TextureReconnaissanceRequest(
                packagePath,
                "bc1-texture-import-candidate",
                0,
                AssetKind.Texture,
                "Texture2D",
                "TestBody_d",
                range.SerialOffset,
                range.SerialSize))
            .GetAwaiter()
            .GetResult();
        var reconJson = Path.Combine(fixtureDirectory, "bc1-texture-import-candidate-recon.json");
        File.WriteAllText(reconJson, "{}");
        var nativeOutput = Path.Combine(fixtureDirectory, "bc1-texture-import-native");
        var nativeResult = new NativeTextureExportService(
                crunchDecoder: new SyntheticCrunchDecoder(NativeTextureFormats.Dxt1))
            .ExportAsync(new NativeTextureExportRequest(report, nativeOutput, reconJson))
            .GetAwaiter()
            .GetResult();
        var editedPath = Path.Combine(fixtureDirectory, "TestBody_d-edited.png");
        var edited = PngRgbaCodec
            .ReadAsync(Path.Combine(nativeOutput, nativeResult.Manifest.EditImageRelativePath!))
            .GetAwaiter()
            .GetResult();
        edited.Pixels[0] ^= 0x7F;
        PngRgbaCodec.WriteAsync(editedPath, edited).GetAwaiter().GetResult();
        var output = Path.Combine(fixtureDirectory, "bc1-texture-import-output");
        var result = new TextureImportCandidateService()
            .CreateAsync(new TextureImportCandidateRequest(
                nativeResult.ManifestPath,
                editedPath,
                output,
                TextureImportAlphaPolicy.PreserveOriginal))
            .GetAwaiter()
            .GetResult();

        var candidate = File.ReadAllBytes(result.CandidateDdsPath);
        Assert(!candidate.AsSpan(128).SequenceEqual(File.ReadAllBytes(nativeResult.DdsPath).AsSpan(128)),
            "BC1 texture import candidate did not encode edited pixels");
        Assert(result.Manifest.EditedInputFormat == "PNG RGBA8",
            "BC1 editable PNG was not accepted directly");
        Assert(result.Manifest.PreflightChecks.Any(check =>
                check.Code == "TIC_ALPHA_POLICY" &&
                check.Message.Contains("BC1", StringComparison.Ordinal)),
            "BC1 texture import candidate did not explain automatic alpha preservation");
    }

    private static void TestPinnedUnityCrunchEncoder()
    {
        var executable = CrunchEncoderLocator.FindExecutable();
        Assert(executable is not null, "pinned Unity Crunch encoder was not found");
        var encoder = new UnityCrunchEncoder(executable!);
        var decoder = new NativeCrunchDecoder();
        var profiles = new[]
        {
            (NativeTextureFormats.Dxt1, "unity-crunch-dxt1.crn"),
            (NativeTextureFormats.Dxt5, "unity-crunch-dxt5.crn"),
            (NativeTextureFormats.Bc5, "unity-crunch-bc5.crn"),
        };
        foreach (var (profile, fixtureName) in profiles)
        {
            var fixture = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName));
            var gpu = profile.DecodeCrunch(decoder, fixture);
            var ddsPath = Path.Combine(fixtureDirectory, $"encoder-{profile.PixelFormatName}.dds");
            var crnPath = Path.Combine(fixtureDirectory, $"encoder-{profile.PixelFormatName}.crn");
            DdsBlockCompressedWriter.WriteAsync(
                    ddsPath,
                    16,
                    16,
                    profile.DdsFormat,
                    new ReadOnlyMemory<byte>[] { gpu })
                .GetAwaiter()
                .GetResult();
            var result = encoder.EncodeAsync(ddsPath, crnPath, profile)
                .GetAwaiter()
                .GetResult();
            var encoded = File.ReadAllBytes(crnPath);
            var info = decoder.Inspect(encoded);
            var decoded = profile.DecodeCrunch(decoder, encoded);
            Assert(result.QualityLevel == 255, "Unity Crunch encoder did not use maximum quality");
            Assert(info.Width == 16 && info.Height == 16 && info.Levels == 1 && info.Faces == 1,
                $"{profile.PixelFormatName} CRN geometry was incorrect");
            Assert(info.Format == profile.CrunchFormat && info.FourCc == profile.CrunchFourCc,
                $"{profile.PixelFormatName} CRN format was incorrect");
            Assert(decoded.Length == gpu.Length,
                $"{profile.PixelFormatName} CRN decode-back byte count was incorrect");
            Assert(result.OutputSha256 == Convert.ToHexString(SHA256.HashData(encoded)),
                $"{profile.PixelFormatName} CRN output hash was not recorded correctly");
        }
    }

    private static void TestTextureReplacementPayloadWorkflow()
    {
        var packageDirectory = Path.Combine(
            fixtureDirectory, "synthetic-client", "EFGame", "ReleasePC", "Packages");
        Directory.CreateDirectory(packageDirectory);
        var packagePath = Path.Combine(packageDirectory, "replacement-payload-workflow.upk");
        var sourceCrn = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "unity-crunch-dxt5.crn"));
        var range = WriteTextureReconPackage(
            packagePath,
            lostArkTagExtensions: true,
            pixelFormatName: "PF_DXT5",
            storedMipOverride: sourceCrn,
            textureWidth: 16,
            textureHeight: 16);
        RewriteAsSingleCompressedPackage(packagePath);
        var decoder = new NativeCrunchDecoder();
        var report = new TextureReconnaissanceService()
            .InspectAsync(new TextureReconnaissanceRequest(
                packagePath,
                "replacement-payload-workflow",
                0,
                AssetKind.Texture,
                "Texture2D",
                "TestTexture",
                range.SerialOffset,
                range.SerialSize))
            .GetAwaiter()
            .GetResult();
        Assert(report.MipCandidates.Single().Confidence == TextureReconnaissanceConfidence.StrongCandidate,
            "synthetic CRN report did not begin at the expected heuristic confidence");
        var reconEntry = new TextureReconnaissanceWorkspaceStore(
                Path.Combine(fixtureDirectory, "replacement-recon"))
            .SaveAsync(report)
            .GetAwaiter()
            .GetResult();
        var workspace = new ExportWorkspaceStore(
            Path.Combine(fixtureDirectory, "replacement-workspace"));
        var nativeOutput = workspace.CreateSessionDirectory(report.PackageAlias, report.ObjectName);
        var nativeResult = new NativeTextureExportService(crunchDecoder: decoder)
            .ExportAsync(new NativeTextureExportRequest(report, nativeOutput, reconEntry.JsonPath))
            .GetAwaiter()
            .GetResult();
        var nativeEntry = workspace.CommitNativeTextureAsync(nativeResult, report, [])
            .GetAwaiter()
            .GetResult();

        var editedPath = Path.Combine(fixtureDirectory, "replacement-edited.png");
        var edited = PngRgbaCodec.ReadAsync(
                Path.Combine(nativeOutput, nativeResult.Manifest.EditImageRelativePath!))
            .GetAwaiter()
            .GetResult();
        edited.Pixels[0] = 0;
        edited.Pixels[1] = 120;
        edited.Pixels[2] = 255;
        PngRgbaCodec.WriteAsync(editedPath, edited).GetAwaiter().GetResult();
        var candidateOutput = workspace.CreateSessionDirectory(
            report.PackageAlias, report.ObjectName + "-candidate");
        var candidateResult = new TextureImportCandidateService()
            .CreateAsync(new TextureImportCandidateRequest(
                nativeResult.ManifestPath, editedPath, candidateOutput))
            .GetAwaiter()
            .GetResult();
        var candidateEntry = workspace.CommitTextureImportCandidateAsync(
                candidateResult, nativeEntry)
            .GetAwaiter()
            .GetResult();

        var layoutService = new TextureLayoutConfirmationService(crunchDecoder: decoder);
        var layoutOutput = workspace.CreateSessionDirectory(
            report.PackageAlias, report.ObjectName + "-layout");
        var layoutResult = layoutService.ConfirmAsync(
                new TextureLayoutConfirmationRequest(nativeResult.ManifestPath, layoutOutput))
            .GetAwaiter()
            .GetResult();
        Assert(layoutResult.Manifest.MipPayloadLayoutConfirmed,
            "layout confirmation did not promote the CRN mip payload");
        Assert(!layoutResult.Manifest.PackageRewriteConfirmed,
            "layout confirmation overstated package-writer readiness");
        Assert(layoutResult.Manifest.Mips.Single().DescriptorMatches &&
               layoutResult.Manifest.Mips.Single().CodecMetadataMatches &&
               layoutResult.Manifest.Mips.Single().DecodeMatchesExportedDds,
            "layout confirmation did not independently verify every mip evidence layer");
        _ = workspace.CommitTextureLayoutConfirmationAsync(layoutResult, nativeEntry)
            .GetAwaiter()
            .GetResult();

        var encoderPath = CrunchEncoderLocator.FindExecutable();
        Assert(encoderPath is not null, "pinned Unity Crunch encoder was not found");
        var payloadOutput = workspace.CreateSessionDirectory(
            report.PackageAlias, report.ObjectName + "-payloads");
        var payloadResult = new TextureReplacementPayloadService(
                layoutService,
                new UnityCrunchEncoder(encoderPath!),
                decoder)
            .BuildAsync(new TextureReplacementPayloadRequest(
                candidateResult.ManifestPath, payloadOutput))
            .GetAwaiter()
            .GetResult();
        Assert(payloadResult.Manifest.MipPayloadLayoutConfirmed,
            "payload builder did not retain fresh layout confirmation");
        Assert(payloadResult.Manifest.AllStoragePayloadsDecode,
            "encoded CRN payload did not pass decode-back");
        Assert(payloadResult.Manifest.AllQualityGatesPassed &&
               payloadResult.Manifest.PayloadSetReadyForWriter,
            "encoded CRN payload did not pass quality/readiness gates");
        Assert(payloadResult.Manifest.CrunchPayloadsEncoded == 1 &&
               payloadResult.Manifest.Lz4PayloadsEncoded == 0,
            "payload builder recorded the wrong encoding route");
        Assert(!payloadResult.Manifest.PackageWasModified,
            "payload builder claimed to modify a package");
        var payload = payloadResult.Manifest.Mips.Single();
        Assert(payload.CodecMetadataVerified && payload.DecodeBackVerified &&
               payload.Quality.QualityGatePassed &&
               double.IsFinite(payload.Quality.MeanAbsoluteChannelError) &&
               double.IsFinite(payload.Quality.RootMeanSquareChannelError) &&
               double.IsFinite(payload.Quality.PsnrDb),
            "encoded CRN mip evidence was incomplete");
        var payloadEntry = workspace.CommitTextureReplacementPayloadsAsync(
                payloadResult, candidateEntry)
            .GetAwaiter()
            .GetResult();
        Assert(payloadEntry.Manifest.TextureReplacementPayloadManifestPath == payloadResult.ManifestPath,
            "workspace did not retain the payload manifest path");

        var assemblyOutput = workspace.CreateSessionDirectory(
            report.PackageAlias, report.ObjectName + "-assembly");
        var assemblyResult = new TextureExportAssemblyService(crunchDecoder: decoder)
            .AssembleAsync(new TextureExportAssemblyRequest(payloadResult.ManifestPath, assemblyOutput))
            .GetAwaiter()
            .GetResult();
        Assert(assemblyResult.Manifest.NoOpExportByteIdentical &&
               assemblyResult.Manifest.NoOpExportSha256 == nativeResult.Manifest.SourcePayloadSha256,
            "Texture2D no-op assembly was not byte-identical");
        Assert(assemblyResult.Manifest.AllNonMipBytesPreserved &&
               assemblyResult.Manifest.AllDescriptorsReplayed &&
               assemblyResult.Manifest.AllStoragePayloadsDecode &&
               assemblyResult.Manifest.AllQualityGatesRetained &&
               assemblyResult.Manifest.ReplacementBlobReadyForRewritePlan,
            "replacement Texture2D export blob did not pass replay validation");
        Assert(!assemblyResult.Manifest.PackageWasModified &&
               assemblyResult.Manifest.ReplacementExportBytes ==
               assemblyResult.Manifest.SourceSerialSize + assemblyResult.Manifest.ReplacementSizeDelta,
            "export assembly misstated its size or package-write state");
        var assemblyEntry = workspace.CommitTextureExportAssemblyAsync(assemblyResult, payloadEntry)
            .GetAwaiter()
            .GetResult();
        Assert(assemblyEntry.Manifest.TextureExportAssemblyManifestPath == assemblyResult.ManifestPath,
            "workspace did not retain the export-assembly manifest path");

        var planOutput = workspace.CreateSessionDirectory(
            report.PackageAlias, report.ObjectName + "-rewrite-plan");
        var planResult = new PackageRewritePlanService()
            .PlanAsync(new PackageRewritePlanRequest(assemblyResult.ManifestPath, planOutput))
            .GetAwaiter()
            .GetResult();
        Assert(planResult.Manifest.PlanReadyForLogicalImageWriter &&
               !planResult.Manifest.PhysicalChunkPlanReady &&
               !planResult.Manifest.PackageFileWriterReady &&
               !planResult.Manifest.PackageWasModified,
            "rewrite plan overstated or understated its safety boundary");
        Assert(planResult.Manifest.SelectedStrategy.StartsWith("Append", StringComparison.Ordinal) &&
               planResult.Manifest.ExistingExportsRelocated == 0 &&
               planResult.Manifest.ExistingExportPayloadsModified == 0 &&
               planResult.Manifest.OriginalExportBytesRetained,
            "rewrite plan did not select non-relocating append-and-retarget");
        Assert(planResult.Manifest.SelectedTablePatches.Count == 2 &&
               planResult.Manifest.BulkOffsetRebases.Count == 1 &&
               planResult.Manifest.AllBulkOffsetsRebased &&
               planResult.Manifest.ExportTablePatchLocationsConfirmed,
            "rewrite plan did not account for every selected field patch");
        var planEntry = workspace.CommitPackageRewritePlanAsync(planResult, assemblyEntry)
            .GetAwaiter()
            .GetResult();
        Assert(planEntry.Manifest.PackageRewritePlanManifestPath == planResult.ManifestPath,
            "workspace did not retain the package rewrite-plan manifest path");

        var imageOutput = workspace.CreateSessionDirectory(
            report.PackageAlias, report.ObjectName + "-logical-image");
        var imageResult = new LogicalPackageImageService()
            .BuildAsync(new LogicalPackageImageRequest(planResult.ManifestPath, imageOutput))
            .GetAwaiter()
            .GetResult();
        Assert(imageResult.Manifest.LogicalImageReadyForPhysicalWriter &&
               !imageResult.Manifest.PhysicalChunkWriterReady &&
               !imageResult.Manifest.PackageFileWriterReady &&
               !imageResult.Manifest.PackageWasModified,
            "logical image overstated or understated its safety boundary");
        Assert(imageResult.Manifest.AllUnplannedSourceBytesIdentical &&
               imageResult.Manifest.AllTablePatchesApplied &&
               imageResult.Manifest.AllBulkOffsetsRebased &&
               imageResult.Manifest.OriginalExportBytesRetained &&
               imageResult.Manifest.AppendedReplacementVerified &&
               imageResult.Manifest.SourcePackageIdentityRetained,
            "logical image did not pass full replay verification");
        Assert(imageResult.Manifest.LogicalImageBytes == planResult.Manifest.PlannedLogicalPackageBytes &&
               new FileInfo(imageResult.LogicalImagePath).Length == imageResult.Manifest.LogicalImageBytes &&
               imageResult.Manifest.TablePatches.Count == 2 &&
               imageResult.Manifest.BulkOffsetRebases.Count == 1,
            "logical image size or mutation evidence was incomplete");
        var imageEntry = workspace.CommitLogicalPackageImageAsync(imageResult, planEntry)
            .GetAwaiter()
            .GetResult();
        Assert(imageEntry.Manifest.LogicalPackageImageManifestPath == imageResult.ManifestPath,
            "workspace did not retain the logical package-image manifest path");

        var physicalOutput = workspace.CreateSessionDirectory(
            report.PackageAlias, report.ObjectName + "-physical-plan");
        var physicalResult = new PhysicalPackagePlanService()
            .PlanAsync(new PhysicalPackagePlanRequest(imageResult.ManifestPath, physicalOutput))
            .GetAwaiter()
            .GetResult();
        Assert(physicalResult.Manifest.PlanReadyForNoOpReconstruction &&
               physicalResult.Manifest.CompressedChunkCount == 1 &&
               physicalResult.Manifest.ReplacementAffectedChunkCount == 1 &&
               physicalResult.Manifest.AllChunkEnvelopesParsed &&
               physicalResult.Manifest.AllProtectedBlocksReadable &&
               physicalResult.Manifest.LogicalSourceIdentityConfirmed &&
               physicalResult.Manifest.CopyRegionsCoverPhysicalFile &&
               !physicalResult.Manifest.ReplacementPhysicalWriterReady &&
               !physicalResult.Manifest.PackageWasModified,
            "physical package plan misstated its evidence or safety boundary");
        Assert(physicalResult.Manifest.CopyRegions.Sum(region => region.Bytes) ==
               physicalResult.Manifest.SourcePhysicalPackageBytes,
            "physical copy regions did not cover the source package");
        var physicalEntry = workspace.CommitPhysicalPackagePlanAsync(physicalResult, imageEntry)
            .GetAwaiter()
            .GetResult();
        Assert(physicalEntry.Manifest.PhysicalPackagePlanManifestPath == physicalResult.ManifestPath,
            "workspace did not retain the physical package-plan manifest path");

        var noOpOutput = workspace.CreateSessionDirectory(
            report.PackageAlias, report.ObjectName + "-noop-upk");
        var noOpResult = new NoOpUpkReconstructionService()
            .ReconstructAsync(new NoOpUpkReconstructionRequest(
                physicalResult.ManifestPath, noOpOutput))
            .GetAwaiter()
            .GetResult();
        Assert(noOpResult.Manifest.NoOpReconstructionProofPassed &&
               noOpResult.Manifest.AllCopyRegionsByteIdentical &&
               noOpResult.Manifest.PhysicalFileByteIdentical &&
               noOpResult.Manifest.PackageSummaryEquivalent &&
               noOpResult.Manifest.ChunkTableEquivalent &&
               noOpResult.Manifest.LogicalImageByteIdentical &&
               noOpResult.Manifest.AssetCatalogueEquivalent &&
               noOpResult.Manifest.StructuralValidationPassed &&
               noOpResult.Manifest.SourcePackageIdentityRetained,
            "no-op UPK reconstruction did not pass equivalence proof");
        Assert(!noOpResult.Manifest.OutputContainsReplacement &&
               !noOpResult.Manifest.ReplacementPhysicalWriterReady &&
               !noOpResult.Manifest.PackageWasModified &&
               noOpResult.Manifest.OutputPackageSha256 == noOpResult.Manifest.SourcePackageSha256,
            "no-op UPK reconstruction overstated replacement or write state");
        var noOpEntry = workspace.CommitNoOpUpkReconstructionAsync(noOpResult, physicalEntry)
            .GetAwaiter()
            .GetResult();
        Assert(noOpEntry.Manifest.NoOpUpkReconstructionManifestPath == noOpResult.ManifestPath,
            "workspace did not retain the no-op UPK reconstruction manifest path");

        var replacementUpkOutput = workspace.CreateSessionDirectory(
            report.PackageAlias, report.ObjectName + "-replacement-upk");
        var replacementUpkResult = new ReplacementUpkBuildService()
            .BuildAsync(new ReplacementUpkBuildRequest(
                noOpResult.ManifestPath, replacementUpkOutput))
            .GetAwaiter()
            .GetResult();
        Assert(replacementUpkResult.Manifest.ReplacementPackageBuildPassed &&
               replacementUpkResult.Manifest.NoOpProofRequiredAndPassed &&
               replacementUpkResult.Manifest.ExpectedLogicalImageMatched &&
               replacementUpkResult.Manifest.TargetExportRetargeted &&
               replacementUpkResult.Manifest.OriginalExportBytesRetained &&
               replacementUpkResult.Manifest.ReplacementExportBytesVerified &&
               replacementUpkResult.Manifest.UnrelatedExportsEquivalent &&
               replacementUpkResult.Manifest.AssetCatalogueShapeRetained &&
               replacementUpkResult.Manifest.StructuralValidationPassed &&
               replacementUpkResult.Manifest.TargetTextureNativeDecodePassed &&
               replacementUpkResult.Manifest.SourcePackageIdentityRetained,
            "replacement-bearing staging UPK did not pass its complete validation gate");
        Assert(replacementUpkResult.Manifest.OutputContainsReplacement &&
               replacementUpkResult.Manifest.StagingPackageDiffersFromSource &&
               !replacementUpkResult.Manifest.DeploymentReady &&
               !replacementUpkResult.Manifest.PackageWasModified,
            "replacement UPK misstated replacement, deployment, or source-write state");
        var verifiedReplacementOutput = ReplacementUpkBuildService
            .VerifyOutputAsync(replacementUpkResult.ManifestPath)
            .GetAwaiter()
            .GetResult();
        Assert(verifiedReplacementOutput.OutputUpkPath == replacementUpkResult.OutputUpkPath &&
               verifiedReplacementOutput.OutputBytes == replacementUpkResult.Manifest.OutputPhysicalPackageBytes &&
               verifiedReplacementOutput.OutputSha256 == replacementUpkResult.Manifest.OutputPackageSha256 &&
               verifiedReplacementOutput.Manifest.BuildId == replacementUpkResult.Manifest.BuildId,
            "replacement UPK external-consumer verification did not bind the manifest to its output");
        var stagedCatalog = new AssetCatalogReader().Read(replacementUpkResult.OutputUpkPath);
        var stagedTarget = stagedCatalog.Exports.Single(
            item => item.TableIndex == imageResult.Manifest.NativeExportIndex);
        Assert(stagedTarget.SerialOffset == imageResult.Manifest.AppendedSerialOffset &&
               stagedTarget.SerialSize == imageResult.Manifest.AppendedSerialSize,
            "staged package target export did not reread at the appended replacement range");
        var replacementEntry = workspace.CommitReplacementUpkBuildAsync(
                replacementUpkResult, noOpEntry)
            .GetAwaiter()
            .GetResult();
        Assert(replacementEntry.Manifest.ReplacementUpkBuildManifestPath == replacementUpkResult.ManifestPath,
            "workspace did not retain the replacement UPK manifest path");
        Assert(workspace.LoadAsync().GetAwaiter().GetResult().Entries.Count == 10,
            "replacement workflow sessions were not all reloadable");

        var modBundleOutput = workspace.CreateSessionDirectory(
            report.PackageAlias, report.ObjectName + "-mod-bundle");
        var modBundleResult = new ArkiveModBundleService(
                new SyntheticModPackageExternalValidator())
            .BuildAsync(new ArkiveModBundleRequest(
                replacementUpkResult.ManifestPath,
                modBundleOutput,
                Path.Combine(fixtureDirectory, "package-backups"),
                "Smoke-test texture mod"))
            .GetAwaiter()
            .GetResult();
        Assert(modBundleResult.Bundle.ReadyForControlledInstall &&
               modBundleResult.Bundle.ExactClientMatchRequired &&
               modBundleResult.Bundle.BackupRequired &&
               modBundleResult.Bundle.ReversibleInstallPlanned &&
               !modBundleResult.Bundle.InstallationEnabled &&
               !modBundleResult.Bundle.GameFilesModified,
            "mod bundle misstated readiness or installation safety");
        Assert(modBundleResult.Readiness.SourceIdentityMatches &&
               modBundleResult.Readiness.PayloadIdentityMatchesManifest &&
               modBundleResult.Readiness.PayloadUsesOriginalPhysicalName &&
               modBundleResult.Readiness.PackageSummaryIdentityRetained &&
               modBundleResult.Readiness.StructuralValidationPassed &&
               modBundleResult.Readiness.ExternalCataloguePassed &&
               modBundleResult.Readiness.ExternalNativeLoadPassed &&
               modBundleResult.Readiness.ExpectedObjectFound &&
               modBundleResult.Readiness.BackupSpaceAvailable &&
               modBundleResult.Readiness.ReadyForControlledInstall &&
               !modBundleResult.Readiness.InstallationPerformed &&
               !modBundleResult.Readiness.GameFilesModified,
            "deployment rehearsal did not retain its complete dry-run safety gate");
        Assert(Path.GetFileName(modBundleResult.PayloadPath) == Path.GetFileName(packagePath) &&
               modBundleResult.Bundle.PayloadSha256 == replacementUpkResult.Manifest.OutputPackageSha256 &&
               modBundleResult.OutputFiles.Count == 3,
            "mod bundle did not preserve the original physical name or staged payload identity");
        Assert(ArkiveModBundleService.LoadBundleAsync(modBundleResult.BundleManifestPath)
                   .GetAwaiter().GetResult().ModId == modBundleResult.Bundle.ModId &&
               ArkiveModBundleService.LoadReadinessAsync(modBundleResult.ReadinessManifestPath)
                   .GetAwaiter().GetResult().AuditId == modBundleResult.Readiness.AuditId,
            "mod-bundle manifests were not reloadable through their schema gates");
        var modBundleEntry = workspace.CommitModBundleAsync(
                modBundleResult, replacementEntry)
            .GetAwaiter()
            .GetResult();
        Assert(modBundleEntry.Manifest.ModBundleManifestPath == modBundleResult.BundleManifestPath &&
               modBundleEntry.Manifest.DeploymentReadinessManifestPath == modBundleResult.ReadinessManifestPath,
            "workspace did not retain mod-bundle rehearsal manifests");
        Assert(workspace.LoadAsync().GetAwaiter().GetResult().Entries.Count == 11,
            "mod-bundle session was not reloadable");

        var guidedWorkspace = new GuidedTextureModWorkspace(
            Path.Combine(fixtureDirectory, "guided-texture-mods"));
        var guidedProject = guidedWorkspace.CreateAsync(
                "Guided smoke-test texture mod",
                report.PackageAlias,
                Path.GetFileName(packagePath),
                packagePath,
                report.ClassName,
                report.ObjectName,
                report.ExportIndex,
                nativeResult.Manifest.PixelFormat,
                nativeResult.Manifest.Width,
                nativeResult.Manifest.Height,
                nativeResult.ManifestPath,
                editedPath)
            .GetAwaiter().GetResult();
        var guidedService = new GuidedTextureModBuildService(
            new TextureImportCandidateService(),
            new TextureReplacementPayloadService(
                layoutService, new UnityCrunchEncoder(encoderPath!), decoder),
            new TextureExportAssemblyService(crunchDecoder: decoder),
            new PackageRewritePlanService(),
            new LogicalPackageImageService(),
            new PhysicalPackagePlanService(),
            new NoOpUpkReconstructionService(),
            new ReplacementUpkBuildService(),
            new ArkiveModBundleService(new SyntheticModPackageExternalValidator()));
        var guidedRevisionDirectory = guidedWorkspace.CreateRevisionDirectory(guidedProject);
        var guidedResult = guidedService.BuildAsync(
                guidedProject,
                guidedRevisionDirectory,
                Path.Combine(fixtureDirectory, "guided-backups"))
            .GetAwaiter().GetResult();
        guidedProject = guidedWorkspace.CommitRevisionAsync(
                guidedProject.ManifestPath, guidedResult.Revision)
            .GetAwaiter().GetResult();
        Assert(guidedProject.Manifest.State == GuidedTextureModState.Ready &&
               guidedProject.Manifest.Revisions.Count == 1 &&
               guidedProject.Manifest.LatestRevisionId == guidedResult.Revision.RevisionId &&
               File.Exists(guidedResult.Revision.OutputUpkPath) &&
               File.Exists(guidedResult.Revision.ModBundleManifestPath),
            "guided texture workflow did not persist its first verified modified-UPK revision");

        guidedProject = guidedWorkspace.SetIncludedAsync(guidedProject.ManifestPath, false)
            .GetAwaiter().GetResult();
        Assert(guidedProject.Manifest.IncludedInPackageBuild == false,
            "guided package selection did not persist an excluded texture");
        guidedProject = guidedWorkspace.SetIncludedAsync(guidedProject.ManifestPath, true)
            .GetAwaiter().GetResult();
        Assert(guidedWorkspace.LoadAsync().GetAwaiter().GetResult().Single()
                   .Manifest.IncludedInPackageBuild == true,
            "guided package selection did not persist an included texture");
        guidedProject = guidedWorkspace.SetFavoriteAsync(guidedProject.ManifestPath, true)
            .GetAwaiter().GetResult();
        guidedProject = guidedWorkspace.TouchAsync(guidedProject.ManifestPath)
            .GetAwaiter().GetResult();
        Assert(guidedProject.Manifest.IsFavorite && guidedProject.Manifest.LastOpenedAt is not null &&
               guidedWorkspace.LoadAsync().GetAwaiter().GetResult().Single().Manifest.IsFavorite,
            "guided texture favourite and recent-use metadata did not persist");

        var packageWorkspace = new GuidedTexturePackageWorkspace(
            Path.Combine(fixtureDirectory, "guided-package-mods"));
        var packageProject = packageWorkspace.GetOrCreateAsync(
                report.PackageAlias, Path.GetFileName(packagePath), packagePath)
            .GetAwaiter().GetResult();
        packageProject = packageWorkspace.UpdateMetadataAsync(
                packageProject.ManifestPath,
                "Smoke-test texture pack",
                "Metadata should survive package builds.",
                favorite: true)
            .GetAwaiter().GetResult();
        Assert(packageProject.Manifest.DisplayName == "Smoke-test texture pack" &&
               packageProject.Manifest.Notes == "Metadata should survive package builds." &&
               packageProject.Manifest.IsFavorite,
            "guided package name, notes, and favourite metadata did not persist");
        var packageBuildService = new GuidedTexturePackageBuildService(
            new UpkPackageReader(),
            NoChunkProtectionProvider.Instance,
            new NativeTextureExportService(crunchDecoder: decoder),
            new TextureImportCandidateService(),
            new TextureReplacementPayloadService(
                layoutService, new UnityCrunchEncoder(encoderPath!), decoder),
            new TextureExportAssemblyService(crunchDecoder: decoder),
            new PackageRewritePlanService(),
            new LogicalPackageImageService(),
            new PhysicalPackagePlanService(),
            new NoOpUpkReconstructionService(),
            new ReplacementUpkBuildService(),
            new ArkiveModBundleService(new SyntheticModPackageExternalValidator()));
        var packageRevisionDirectory = packageWorkspace.CreateRevisionDirectory(packageProject);
        var packageBuild = packageBuildService.BuildAsync(
                packageProject,
                [guidedProject],
                packageRevisionDirectory,
                Path.Combine(fixtureDirectory, "guided-package-backups"))
            .GetAwaiter().GetResult();
        packageProject = packageWorkspace.CommitRevisionAsync(
                packageProject.ManifestPath, packageBuild.Revision)
            .GetAwaiter().GetResult();
        var packageBundle = ArkiveModBundleService.LoadBundleAsync(
                packageBuild.Revision.ModBundleManifestPath)
            .GetAwaiter().GetResult();
        Assert(packageProject.Manifest.Revisions.Count == 1 &&
               packageProject.Manifest.ClientBaselineSha256 == packageBuild.Revision.SourcePackageSha256 &&
               packageProject.Manifest.ClientBaselineAt is not null &&
               packageBuild.Revision.Assets.Count == 1 &&
               packageBuild.Revision.Assets[0].ProjectId == guidedProject.Manifest.ProjectId &&
               packageBundle.Assets.Count == 1 &&
               packageBundle.Assets[0].ObjectName == guidedProject.Manifest.ObjectName &&
               packageBundle.DisplayName == "Smoke-test texture pack" &&
               packageBundle.RequiredSourcePackageSha256 ==
               packageBuild.Revision.SourcePackageSha256 &&
               File.Exists(packageBuild.Revision.OutputUpkPath),
            "package-level texture composer did not persist and bundle its selected asset");

        var unifiedTextureComposer = new UnifiedPackageComposerService(
            new UpkPackageReader(),
            NoChunkProtectionProvider.Instance,
            new SkeletalMeshReconnaissanceService(),
            new SkeletalMeshEditableExportService(),
            new SkeletalMeshNativeEncodingService(),
            new SkeletalMeshStagedUpkService(),
            packageBuildService,
            new ArkivePackageModBundleService(
                new SyntheticModPackageExternalValidator(),
                protectionProvider: NoChunkProtectionProvider.Instance));
        var unifiedTextureBuild = unifiedTextureComposer.BuildAsync(
                new UnifiedPackageCompositionRequest(
                    packageProject,
                    [guidedProject],
                    [],
                    Path.Combine(fixtureDirectory, "unified-texture-package"),
                    Path.Combine(fixtureDirectory, "unified-texture-backups"),
                    "Unified texture-only smoke test"))
            .GetAwaiter().GetResult();
        var unifiedTextureVerification = UnifiedPackageComposerService.VerifyOutputAsync(
                unifiedTextureBuild.CompositionManifestPath)
            .GetAwaiter().GetResult();
        var unifiedTextureBundle = ArkiveModBundleService.LoadBundleAsync(
                unifiedTextureBuild.Revision.ModBundleManifestPath)
            .GetAwaiter().GetResult();
        Assert(unifiedTextureBuild.Manifest.TextureCount == 1 &&
               unifiedTextureBuild.Manifest.SkeletalMeshCount == 0 &&
               unifiedTextureBuild.Manifest.TexturesAppliedBeforeMeshes &&
               unifiedTextureBuild.Manifest.CleanSourceIdentityRetained &&
               unifiedTextureBuild.Manifest.EveryStageVerified &&
               unifiedTextureBuild.Manifest.EverySelectedAssetCatalogued &&
               unifiedTextureBuild.Manifest.OnePhysicalPayloadProduced &&
               unifiedTextureBuild.Manifest.Stages.Count == 1 &&
               unifiedTextureBuild.Revision.MeshAssets.Count == 0 &&
               unifiedTextureVerification.OutputUpkPath == unifiedTextureBuild.Revision.OutputUpkPath &&
               unifiedTextureBundle.Assets is [{ ClassName: "Texture2D" }] &&
               unifiedTextureBundle.InstallationEnabled,
            "unified package composer did not retain the texture-only compatibility path");
        var unifiedTextureDeployment = new ArkiveModDeploymentService(
            processGuard: new SyntheticModDeploymentProcessGuard());
        var unifiedTextureDeploymentRequest = new ArkiveModDeploymentRequest(
            unifiedTextureBuild.Revision.ModBundleManifestPath,
            Path.Combine(fixtureDirectory, "unified-texture-backups"),
            Path.Combine(fixtureDirectory, "unified-texture-installation-state"));
        var unifiedTextureInstalled = unifiedTextureDeployment.InstallAsync(
                unifiedTextureDeploymentRequest).GetAwaiter().GetResult();
        var unifiedTextureRestored = unifiedTextureDeployment.RestoreAsync(
                unifiedTextureDeploymentRequest).GetAwaiter().GetResult();
        Assert(unifiedTextureInstalled.Inspection.State == ModInstallationState.Installed &&
               unifiedTextureInstalled.Inspection.CanRestore &&
               unifiedTextureRestored.Inspection.State == ModInstallationState.Restored &&
               unifiedTextureRestored.Inspection.CanInstall &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))) ==
               unifiedTextureBuild.Manifest.CleanSourcePackageSha256,
            "unified texture-only bundle did not install and restore as one guarded package");

        var secondPackagePath = Path.Combine(fixtureDirectory, "second-client", "EFGame",
            "ReleasePC", "Packages", "second-client-package.upk");
        Directory.CreateDirectory(Path.GetDirectoryName(secondPackagePath)!);
        File.Copy(packagePath, secondPackagePath, overwrite: true);
        var secondPackageHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(secondPackagePath)));
        var secondBundle = new ArkivePackageModBundleService(
                new SyntheticModPackageExternalValidator(),
                protectionProvider: NoChunkProtectionProvider.Instance)
            .BuildAsync(new ArkivePackageModBundleRequest(
                unifiedTextureBuild.Revision.OutputUpkPath,
                secondPackagePath,
                secondPackageHash,
                Path.Combine(fixtureDirectory, "second-package-bundle"),
                Path.Combine(fixtureDirectory, "mod-set-backups"),
                "Second package smoke mod",
                Guid.NewGuid().ToString("N"),
                unifiedTextureBundle.Assets))
            .GetAwaiter().GetResult();
        var secondWorkspace = new GuidedTexturePackageWorkspace(
            Path.Combine(fixtureDirectory, "second-package-projects"));
        var secondProject = secondWorkspace.GetOrCreateAsync(
                "second_client_package", Path.GetFileName(secondPackagePath), secondPackagePath)
            .GetAwaiter().GetResult();
        var secondRevision = packageBuild.Revision with
        {
            RevisionId = "second-" + Guid.NewGuid().ToString("N")[..12],
            CreatedAt = DateTimeOffset.UtcNow,
            SourcePackageSha256 = secondPackageHash,
            OutputUpkPath = unifiedTextureBuild.Revision.OutputUpkPath,
            ModBundleManifestPath = secondBundle.BundleManifestPath,
            DeploymentReadinessManifestPath = secondBundle.ReadinessManifestPath,
            PayloadPath = secondBundle.PayloadPath,
            PayloadBytes = secondBundle.Bundle.PayloadBytes,
            PayloadSha256 = secondBundle.Bundle.PayloadSha256,
        };
        secondProject = secondWorkspace.CommitRevisionAsync(secondProject.ManifestPath, secondRevision)
            .GetAwaiter().GetResult();
        var modSetWorkspace = new ArkiveModSetWorkspace(Path.Combine(fixtureDirectory, "mod-sets"));
        var modSet = modSetWorkspace.CreateAsync("Two-package smoke set", "Transactional set smoke coverage.")
            .GetAwaiter().GetResult();
        modSet = modSetWorkspace.SetPackageAsync(modSet.ManifestPath, packageProject, true)
            .GetAwaiter().GetResult();
        modSet = modSetWorkspace.SetPackageAsync(modSet.ManifestPath, secondProject, true)
            .GetAwaiter().GetResult();
        var modSetService = new ArkiveModSetService(
            modSetWorkspace, packageWorkspace,
            new ArkiveModDeploymentService(processGuard: new SyntheticModDeploymentProcessGuard()));
        var modSetRequest = new ModSetDeploymentRequest(
            modSet.ManifestPath,
            Path.Combine(fixtureDirectory, "mod-set-backups"),
            Path.Combine(fixtureDirectory, "mod-set-state"));
        var quickInstallSet = modSetWorkspace.UpsertUtilitySetAsync(
                "install-selected", "Install selected Package Projects", [packageProject, secondProject])
            .GetAwaiter().GetResult();
        var quickInstallInspection = modSetService.InspectAsync(
                modSetRequest with { ModSetManifestPath = quickInstallSet.ManifestPath })
            .GetAwaiter().GetResult();
        Assert(quickInstallSet.Manifest.IsUtility && quickInstallSet.Manifest.Members.Count == 2 &&
               quickInstallInspection.CanInstall && modSetWorkspace.LoadAsync().GetAwaiter().GetResult().Count == 1,
            "quick multi-package action did not use a hidden, fully preflighted utility set");
        quickInstallSet = modSetWorkspace.UpsertUtilitySetAsync(
                "install-selected", "Install selected Package Projects", [secondProject])
            .GetAwaiter().GetResult();
        Assert(quickInstallSet.Manifest.Members.Count == 1 &&
               quickInstallSet.Manifest.Members.Single().PackageProjectId ==
               secondProject.Manifest.PackageProjectId &&
               modSetWorkspace.LoadAsync().GetAwaiter().GetResult().Single().Manifest.ModSetId ==
               modSet.Manifest.ModSetId,
            "quick multi-package action did not atomically replace its selection or remained visible as a named Mod Set");
        var modSetInspection = modSetService.InspectAsync(modSetRequest).GetAwaiter().GetResult();
        Assert(modSetInspection.Members.Count == 2 && modSetInspection.CanInstall &&
               modSetInspection.Conflicts.All(conflict => conflict.Severity != ModSetConflictSeverity.Blocking),
            "two-package Mod Set did not preflight every independent target without conflicts: " +
            modSetInspection.Summary + " | " + string.Join(" | ", modSetInspection.Conflicts.Select(item => item.Code + ":" + item.Message)));
        var modSetInstalled = modSetService.InstallAsync(modSetRequest).GetAwaiter().GetResult();
        Assert(modSetInstalled.Inspection.State == ModSetInstallationState.Installed &&
               modSetInstalled.ChangedPackageCount == 2 && modSetInstalled.Inspection.CanRestore,
            "two-package Mod Set was not installed as one verified transaction");
        var quickRestoreSet = modSetWorkspace.UpsertUtilitySetAsync(
                "restore-all", "Restore all active package mods", [packageProject, secondProject])
            .GetAwaiter().GetResult();
        var modSetRestored = modSetService.RestoreAsync(
                modSetRequest with { ModSetManifestPath = quickRestoreSet.ManifestPath })
            .GetAwaiter().GetResult();
        Assert(modSetRestored.Inspection.State == ModSetInstallationState.Restored &&
               modSetRestored.ChangedPackageCount == 2 &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))) ==
               unifiedTextureBuild.Manifest.CleanSourcePackageSha256 &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(secondPackagePath))) == secondPackageHash,
            "two-package Mod Set restore did not recover both exact client packages");
        var persistedSet = modSetWorkspace.LoadAsync().GetAwaiter().GetResult().Single();
        Assert(persistedSet.Manifest.Members.Count == 2 &&
               persistedSet.Manifest.Members.All(member => member.TrackLatestRevision && member.Included),
            "Mod Set membership and latest-revision tracking did not persist");
        var guardChecks = 0;
        var rollbackService = new ArkiveModSetService(
            modSetWorkspace, packageWorkspace,
            new ArkiveModDeploymentService(processGuard: new SyntheticModDeploymentProcessGuard(() =>
            {
                if (++guardChecks == 2) throw new IOException("Synthetic second-package install failure.");
            })));
        try
        {
            _ = rollbackService.InstallAsync(modSetRequest).GetAwaiter().GetResult();
            throw new InvalidOperationException("Mod Set transaction did not surface its synthetic second-package failure");
        }
        catch (IOException exception) when (exception.Message.Contains("Synthetic second-package", StringComparison.Ordinal))
        {
        }
        var rollbackReceipt = ArkiveModSetService.LoadReceiptAsync(
                modSetRequest.StateRootDirectory, modSet.Manifest.ModSetId)
            .GetAwaiter().GetResult();
        Assert(rollbackReceipt?.State == ModSetInstallationState.Restored &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))) ==
               unifiedTextureBuild.Manifest.CleanSourcePackageSha256 &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(secondPackagePath))) == secondPackageHash,
            "failed Mod Set installation did not roll back every package changed by that transaction");

        var compatibilityService = new ClientUpdateCompatibilityService();
        var exactCompatibility = compatibilityService.InspectAsync(
                packageProject, [guidedProject], [])
            .GetAwaiter().GetResult();
        Assert(exactCompatibility.State == ClientPackageCompatibilityState.ExactSource &&
               exactCompatibility.CanRebuildWithoutRebase && !exactCompatibility.CanAutomaticallyRebase,
            "unchanged client package was not classified as the exact package-project source");
        var clientAuditService = new ClientUpdateAuditService(compatibilityService);
        var clientAuditInput = new ClientUpdatePackageInput(packageProject, [guidedProject], []);
        var exactAudit = clientAuditService.InspectAllAsync(
                [clientAuditInput], unifiedTextureDeploymentRequest.StateRootDirectory)
            .GetAwaiter().GetResult();
        Assert(exactAudit.State == ClientUpdateAuditState.Compatible &&
               exactAudit.EveryAffectedPackageFormatCompatible &&
               !exactAudit.RequiresRestoreBeforeUpdateCheck &&
               exactAudit.Packages.Single().FormatProbe is
                   { CanRead: true, CanReconstruct: true } exactProbe &&
               exactProbe.DecodedChunkCount == exactProbe.CompressedChunkCount,
            "client-wide audit did not deeply validate an exact restored package");
        var auditStore = new ClientUpdateAuditWorkspaceStore(
            Path.Combine(fixtureDirectory, "client-update-audits"));
        var exactAuditEntry = auditStore.SaveAsync(exactAudit).GetAwaiter().GetResult();
        var reloadedAudit = auditStore.LoadLatestAsync().GetAwaiter().GetResult();
        Assert(File.Exists(exactAuditEntry.ManifestPath) &&
               reloadedAudit?.Report.AuditId == exactAudit.AuditId,
            "client-wide compatibility audit did not persist and reload");

        _ = unifiedTextureDeployment.InstallAsync(unifiedTextureDeploymentRequest)
            .GetAwaiter().GetResult();
        var installedAudit = clientAuditService.InspectAllAsync(
                [clientAuditInput], unifiedTextureDeploymentRequest.StateRootDirectory)
            .GetAwaiter().GetResult();
        Assert(installedAudit.State == ClientUpdateAuditState.RestoreRequired &&
               installedAudit.RequiresRestoreBeforeUpdateCheck &&
               !installedAudit.CanBulkRebase &&
               installedAudit.InstallationTargets.Any(target => target.State ==
                   ClientInstallationTargetState.ActivePayloadRestoreAvailable),
            "client-wide audit did not refuse an installed Arkive payload with explicit restore evidence");
        _ = unifiedTextureDeployment.RestoreAsync(unifiedTextureDeploymentRequest)
            .GetAwaiter().GetResult();
        var cleanLength = new FileInfo(packagePath).Length;
        var cleanWriteTime = File.GetLastWriteTimeUtc(packagePath);
        using (var stream = new FileStream(packagePath, FileMode.Append, FileAccess.Write, FileShare.None))
            stream.WriteByte(0x5A);
        var safeUpdate = compatibilityService.InspectAsync(packageProject, [guidedProject], [])
            .GetAwaiter().GetResult();
        Assert(safeUpdate.State == ClientPackageCompatibilityState.SafeAutomaticRebase &&
               safeUpdate.CanAutomaticallyRebase &&
               safeUpdate.Assets.All(asset => asset.State == ClientAssetCompatibilityState.Unchanged),
            "client package with unchanged selected export was not offered a conservative automatic rebase");
        var safeAudit = clientAuditService.InspectAllAsync(
                [clientAuditInput], unifiedTextureDeploymentRequest.StateRootDirectory)
            .GetAwaiter().GetResult();
        Assert(safeAudit.State == ClientUpdateAuditState.RebaseAvailable &&
               safeAudit.CanBulkRebase && safeAudit.SafeRebasePackageCount == 1 &&
               safeAudit.EveryAffectedPackageFormatCompatible,
            "client-wide audit did not offer bulk rebase for a structurally compatible package with byte-identical selected exports");

        var packageManifestSnapshot = File.ReadAllBytes(packageProject.ManifestPath);
        var textureManifestSnapshot = File.ReadAllBytes(guidedProject.ManifestPath);
        var bulkTextureReconWorkspace = new TextureReconnaissanceWorkspaceStore(
            Path.Combine(fixtureDirectory, "bulk-client-rebase-texture-recon"));
        var bulkMeshReconWorkspace = new SkeletalMeshReconnaissanceWorkspaceStore(
            Path.Combine(fixtureDirectory, "bulk-client-rebase-mesh-recon"));
        var bulkMeshEditWorkspace = new SkeletalMeshEditWorkspaceStore(
            Path.Combine(fixtureDirectory, "bulk-client-rebase-mesh-edits"));
        var bulkRebaseService = new ClientUpdateBulkRebaseService(
            new ClientUpdateRebaseService(
                compatibilityService, new TextureReconnaissanceService(),
                bulkTextureReconWorkspace, new NativeTextureExportService(crunchDecoder: decoder),
                guidedWorkspace, new SkeletalMeshReconnaissanceService(),
                bulkMeshReconWorkspace, new SkeletalMeshEditableExportService(),
                bulkMeshEditWorkspace, new MaterialInstanceReconnaissanceService(),
                new MaterialInstanceWorkspaceStore(Path.Combine(fixtureDirectory, "bulk-material-recon")),
                new MaterialInstanceEditWorkspaceStore(Path.Combine(fixtureDirectory, "bulk-material-edits"))),
            packageWorkspace, bulkMeshEditWorkspace);
        var bulkResult = bulkRebaseService.RebaseSafePackagesAsync(
                safeAudit, [clientAuditInput],
                Path.Combine(fixtureDirectory, "bulk-client-update-rebase"))
            .GetAwaiter().GetResult();
        Assert(bulkResult.EveryPackageRebased && !bulkResult.GameFilesModified &&
               bulkResult.Packages.Count == 1 && bulkResult.TextureProjectCount == 1 &&
               bulkResult.MeshProjectCount == 0,
            "client-wide bulk rebase did not refresh the safe texture project transactionally");
        File.WriteAllBytes(packageProject.ManifestPath, packageManifestSnapshot);
        File.WriteAllBytes(guidedProject.ManifestPath, textureManifestSnapshot);
        using (var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(cleanLength);
        using (var stream = new FileStream(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = report.SerialOffset;
            var value = stream.ReadByte();
            stream.Position = report.SerialOffset;
            stream.WriteByte((byte)(value ^ 0x01));
        }
        var unsafeUpdate = compatibilityService.InspectAsync(packageProject, [guidedProject], [])
            .GetAwaiter().GetResult();
        Assert(unsafeUpdate.State == ClientPackageCompatibilityState.ReviewRequired &&
               !unsafeUpdate.CanAutomaticallyRebase &&
               unsafeUpdate.Assets.Any(asset => asset.State == ClientAssetCompatibilityState.Changed),
            "client package with a changed selected native export was not blocked for review");
        var unsafeAudit = clientAuditService.InspectAllAsync(
                [clientAuditInput], unifiedTextureDeploymentRequest.StateRootDirectory)
            .GetAwaiter().GetResult();
        Assert(unsafeAudit.State == ClientUpdateAuditState.ReviewRequired &&
               unsafeAudit.ReviewPackageCount == 1 && !unsafeAudit.CanBulkRebase,
            "client-wide audit did not block a package whose selected native export changed");
        using (var stream = new FileStream(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = report.SerialOffset;
            var value = stream.ReadByte();
            stream.Position = report.SerialOffset;
            stream.WriteByte((byte)(value ^ 0x01));
        }
        using (var stream = new FileStream(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            stream.Position = sizeof(uint);
            writer.Write((ushort)(PackageConstants.WesternFileVersion - 1));
        }
        var unsupportedProfileAudit = clientAuditService.InspectAllAsync(
                [clientAuditInput], unifiedTextureDeploymentRequest.StateRootDirectory)
            .GetAwaiter().GetResult();
        Assert(unsupportedProfileAudit.State == ClientUpdateAuditState.UnsupportedClientFormat &&
               !unsupportedProfileAudit.EveryAffectedPackageFormatCompatible &&
               unsupportedProfileAudit.Packages.Single().FormatProbe?.State ==
                   ClientPackageFormatState.UnsupportedProfile,
            "client-wide audit did not globally block a changed package profile");
        using (var stream = new FileStream(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            stream.Position = sizeof(uint);
            writer.Write(PackageConstants.WesternFileVersion);
        }
        var alternatePayloadPath = Path.Combine(fixtureDirectory, "alternate-conflicting-payload.upk");
        File.Copy(unifiedTextureBuild.Revision.OutputUpkPath, alternatePayloadPath, overwrite: true);
        using (var stream = new FileStream(alternatePayloadPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = stream.Length - 1;
            var value = stream.ReadByte();
            stream.Position = stream.Length - 1;
            stream.WriteByte((byte)(value ^ 0x01));
        }
        var alternateBundle = new ArkivePackageModBundleService(
                new SyntheticModPackageExternalValidator(), protectionProvider: NoChunkProtectionProvider.Instance)
            .BuildAsync(new ArkivePackageModBundleRequest(
                alternatePayloadPath, packagePath,
                unifiedTextureBuild.Manifest.CleanSourcePackageSha256,
                Path.Combine(fixtureDirectory, "alternate-conflicting-bundle"),
                Path.Combine(fixtureDirectory, "mod-set-backups"), "Alternate conflicting payload",
                Guid.NewGuid().ToString("N"), unifiedTextureBundle.Assets))
            .GetAwaiter().GetResult();
        var alternateWorkspace = new GuidedTexturePackageWorkspace(
            Path.Combine(fixtureDirectory, "alternate-package-project"));
        var alternateProject = alternateWorkspace.GetOrCreateAsync(
                report.PackageAlias, Path.GetFileName(packagePath), packagePath)
            .GetAwaiter().GetResult();
        alternateProject = alternateWorkspace.CommitRevisionAsync(alternateProject.ManifestPath,
                packageBuild.Revision with
                {
                    RevisionId = "alternate-" + Guid.NewGuid().ToString("N")[..10],
                    ModBundleManifestPath = alternateBundle.BundleManifestPath,
                    DeploymentReadinessManifestPath = alternateBundle.ReadinessManifestPath,
                    PayloadPath = alternateBundle.PayloadPath,
                    PayloadBytes = alternateBundle.Bundle.PayloadBytes,
                    PayloadSha256 = alternateBundle.Bundle.PayloadSha256,
                })
            .GetAwaiter().GetResult();
        var conflictSet = modSetWorkspace.CreateAsync("Conflicting package smoke set")
            .GetAwaiter().GetResult();
        conflictSet = modSetWorkspace.SetPackageAsync(conflictSet.ManifestPath, packageProject, true)
            .GetAwaiter().GetResult();
        conflictSet = modSetWorkspace.SetPackageAsync(conflictSet.ManifestPath, alternateProject, true)
            .GetAwaiter().GetResult();
        var conflictInspection = modSetService.InspectAsync(
                modSetRequest with { ModSetManifestPath = conflictSet.ManifestPath })
            .GetAwaiter().GetResult();
        Assert(!conflictInspection.CanInstall &&
               conflictInspection.Conflicts.Any(conflict =>
                   conflict.Severity == ModSetConflictSeverity.Blocking &&
                   conflict.Code is "SET_OBJECT_CONFLICT" or "SET_PACKAGE_CONFLICT"),
            "Mod Set did not block two different payloads targeting the same physical UPK");
        File.SetLastWriteTimeUtc(packagePath, cleanWriteTime);
        var replacementPackageRevisionDirectory = packageWorkspace.CreateRevisionDirectory(packageProject);
        var replacementPackageRevisionId = Path.GetFileName(replacementPackageRevisionDirectory);
        var replacementPackageRevision = packageBuild.Revision with
        {
            RevisionId = replacementPackageRevisionId,
            CreatedAt = packageBuild.Revision.CreatedAt.AddSeconds(1),
            OutputUpkPath = Path.Combine(replacementPackageRevisionDirectory, "replacement.upk"),
        };
        File.WriteAllText(replacementPackageRevision.OutputUpkPath, "newest retained revision");
        packageProject = packageWorkspace.CommitRevisionAsync(
                packageProject.ManifestPath, replacementPackageRevision)
            .GetAwaiter().GetResult();
        packageProject = packageWorkspace.PruneRevisionsAsync(
                packageProject.ManifestPath,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { replacementPackageRevisionId })
            .GetAwaiter().GetResult();
        Assert(packageProject.Manifest.Revisions.Count == 1 &&
               packageProject.Manifest.LatestRevisionId == replacementPackageRevisionId &&
               !Directory.Exists(packageRevisionDirectory) &&
               Directory.Exists(replacementPackageRevisionDirectory),
            "guided package revision cleanup did not retain the latest build and remove its superseded directory");
        var failedPackageRevision = packageWorkspace.CreateRevisionDirectory(packageProject);
        File.WriteAllText(Path.Combine(failedPackageRevision, "partial.tmp"), "failed build");
        packageWorkspace.DeleteFailedRevisionDirectory(failedPackageRevision);
        Assert(!Directory.Exists(failedPackageRevision),
            "failed package-build revision cleanup did not remove its isolated staging directory");

        var guidedEdit = PngRgbaCodec.ReadAsync(guidedProject.Manifest.EditableImagePath)
            .GetAwaiter().GetResult();
        guidedEdit.Pixels[4] ^= 0x7F;
        var guidedEditReplacement = guidedProject.Manifest.EditableImagePath + ".replacement.png";
        PngRgbaCodec.WriteAsync(guidedEditReplacement, guidedEdit)
            .GetAwaiter().GetResult();
        File.Move(guidedEditReplacement, guidedProject.Manifest.EditableImagePath, overwrite: true);
        var guidedRebuildDirectory = guidedWorkspace.CreateRevisionDirectory(guidedProject);
        var guidedRebuild = guidedService.BuildAsync(
                guidedProject,
                guidedRebuildDirectory,
                Path.Combine(fixtureDirectory, "guided-backups"))
            .GetAwaiter().GetResult();
        guidedProject = guidedWorkspace.CommitRevisionAsync(
                guidedProject.ManifestPath, guidedRebuild.Revision)
            .GetAwaiter().GetResult();
        var guidedReload = guidedWorkspace.LoadAsync().GetAwaiter().GetResult().Single();
        Assert(guidedReload.Manifest.Revisions.Count == 2 &&
               guidedReload.Manifest.LatestRevisionId == guidedRebuild.Revision.RevisionId &&
               guidedReload.Manifest.Revisions[0].EditedImageSha256 !=
               guidedReload.Manifest.Revisions[1].EditedImageSha256,
            "guided texture workflow did not retain revision history after editing and rebuilding");
        guidedReload = guidedWorkspace.ResetEditableAsync(guidedReload.ManifestPath, editedPath)
            .GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(guidedReload.Manifest.EditableImagePath)
                   .SequenceEqual(File.ReadAllBytes(editedPath)) &&
               guidedReload.Manifest.State == GuidedTextureModState.Editing,
            "guided editable-image reset did not restore the project's untouched source copy");
        var deletedGuidedProjectDirectory = guidedReload.DirectoryPath;
        guidedWorkspace.DeleteAsync(guidedReload).GetAwaiter().GetResult();
        Assert(guidedWorkspace.LoadAsync().GetAwaiter().GetResult().Count == 0 &&
               !Directory.Exists(deletedGuidedProjectDirectory) &&
               !Directory.Exists(guidedResult.RevisionDirectory) &&
               !Directory.Exists(guidedRebuild.RevisionDirectory) &&
               guidedWorkspace.IsLegacyImportSuppressedAsync(guidedResult.Revision.ModBundleManifestPath)
                   .GetAwaiter().GetResult(),
            "guided texture project deletion did not remove owned revisions or retain its import tombstone");
        var explicitLegacyBundle = Path.Combine(fixtureDirectory, "legacy-bundle.json");
        guidedWorkspace.SuppressLegacyImportsAsync([explicitLegacyBundle])
            .GetAwaiter().GetResult();
        Assert(guidedWorkspace.IsLegacyImportSuppressedAsync(explicitLegacyBundle)
                   .GetAwaiter().GetResult(),
            "package-level legacy import suppression did not persist its tombstone");
        var deletedPackageProjectDirectory = packageProject.DirectoryPath;
        var deletedPackageRevisionRoot = Path.Combine(
            packageWorkspace.RootDirectory, "_r", packageProject.Manifest.PackageProjectId[..8]);
        packageWorkspace.DeleteAsync(packageProject).GetAwaiter().GetResult();
        Assert(packageWorkspace.LoadAsync().GetAwaiter().GetResult().Count == 0 &&
               !Directory.Exists(deletedPackageProjectDirectory) &&
               !Directory.Exists(deletedPackageRevisionRoot),
            "guided modified-UPK deletion did not remove the package project and owned revisions");

        var deploymentRequest = new ArkiveModDeploymentRequest(
            modBundleResult.BundleManifestPath,
            Path.Combine(fixtureDirectory, "package-backups"),
            Path.Combine(fixtureDirectory, "installation-state"));
        var deploymentService = new ArkiveModDeploymentService(
            processGuard: new SyntheticModDeploymentProcessGuard());
        var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));
        var originalTimestamp = File.GetLastWriteTimeUtc(packagePath);
        var initialDeployment = deploymentService.InspectAsync(deploymentRequest)
            .GetAwaiter().GetResult();
        Assert(initialDeployment.State == ModInstallationState.NotInstalled &&
               initialDeployment.CanInstall && !initialDeployment.CanRestore &&
               initialDeployment.PayloadVerified && !initialDeployment.BackupExists,
            "clean mod deployment did not report an installable exact-client target");

        var installResult = deploymentService.InstallAsync(deploymentRequest)
            .GetAwaiter().GetResult();
        Assert(installResult.ChangedTarget &&
               !installResult.RecoveredInterruptedOperation &&
               installResult.Inspection.State == ModInstallationState.Installed &&
               !installResult.Inspection.CanInstall && installResult.Inspection.CanRestore &&
               installResult.Receipt.State == ModInstallationState.Installed &&
               installResult.Receipt.BackupVerified && installResult.Receipt.TargetVerified &&
               installResult.Receipt.GameFilesModified,
            "controlled mod installation did not commit a verified installed receipt");
        Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))) ==
                   modBundleResult.Bundle.PayloadSha256 &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(installResult.Receipt.BackupPath))) ==
                   originalHash &&
               File.Exists(installResult.Inspection.ReceiptPath),
            "controlled mod installation did not atomically install the payload or retain the original");

        var interruptedReceiptJson = File.ReadAllText(installResult.Inspection.ReceiptPath);
        Assert(interruptedReceiptJson.Contains(
                "\"state\": \"Installed\"", StringComparison.Ordinal),
            "installation receipt did not serialize its state as an auditable enum name");
        File.WriteAllText(
            installResult.Inspection.ReceiptPath,
            interruptedReceiptJson.Replace(
                "\"state\": \"Installed\"",
                "\"state\": \"Prepared\"",
                StringComparison.Ordinal));
        var interruptedInspection = deploymentService.InspectAsync(deploymentRequest)
            .GetAwaiter().GetResult();
        Assert(interruptedInspection.State == ModInstallationState.Prepared &&
               interruptedInspection.CanInstall && interruptedInspection.CanRestore,
            "interrupted installed transaction was not recoverable in either direction");
        var recoveredInstall = deploymentService.InstallAsync(deploymentRequest)
            .GetAwaiter().GetResult();
        Assert(!recoveredInstall.ChangedTarget && recoveredInstall.RecoveredInterruptedOperation &&
               recoveredInstall.Inspection.State == ModInstallationState.Installed,
            "interrupted installed transaction was not finalized without rewriting the target");
        var repeatedInstall = deploymentService.InstallAsync(deploymentRequest)
            .GetAwaiter().GetResult();
        Assert(!repeatedInstall.ChangedTarget &&
               repeatedInstall.Inspection.State == ModInstallationState.Installed,
            "repeated install was not an idempotent verified no-op");

        // Steam repair/reinstall can restore the clean package without updating Arkive's
        // retained receipt. Current byte identity must win over that historical state so
        // rebuild and restore-all do not treat the clean client as an active mod.
        File.Copy(repeatedInstall.Receipt.BackupPath, packagePath, overwrite: true);
        File.SetLastWriteTimeUtc(packagePath, originalTimestamp);
        var externallyRestored = deploymentService.InspectAsync(deploymentRequest)
            .GetAwaiter().GetResult();
        Assert(externallyRestored.State == ModInstallationState.NotInstalled &&
               externallyRestored.CanInstall && !externallyRestored.CanRestore &&
               !externallyRestored.HasActiveDeployment &&
               externallyRestored.Receipt?.State == ModInstallationState.Installed,
            "a clean package restored outside Arkive was incorrectly treated as an active mod from its stale receipt");
        File.Copy(externallyRestored.PayloadPath, packagePath, overwrite: true);
        var reidentifiedInstalledPayload = deploymentService.InspectAsync(deploymentRequest)
            .GetAwaiter().GetResult();
        Assert(reidentifiedInstalledPayload.HasActiveDeployment &&
               reidentifiedInstalledPayload.State == ModInstallationState.Installed,
            "the exact installed payload was not reidentified after the external-restore receipt test");

        var restoreResult = deploymentService.RestoreAsync(deploymentRequest)
            .GetAwaiter().GetResult();
        Assert(restoreResult.ChangedTarget &&
               restoreResult.Inspection.State == ModInstallationState.Restored &&
               restoreResult.Inspection.CanInstall && !restoreResult.Inspection.CanRestore &&
               restoreResult.Receipt.State == ModInstallationState.Restored &&
               !restoreResult.Receipt.GameFilesModified &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))) == originalHash &&
               File.GetLastWriteTimeUtc(packagePath) == originalTimestamp,
            "verified restore did not recover the exact original package and timestamp");
        var repeatedRestore = deploymentService.RestoreAsync(deploymentRequest)
            .GetAwaiter().GetResult();
        Assert(!repeatedRestore.ChangedTarget &&
               repeatedRestore.Inspection.State == ModInstallationState.Restored,
            "repeated restore was not an idempotent verified no-op");

        using (var stream = new FileStream(
                   packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var originalByte = stream.ReadByte();
            stream.Position = 0;
            stream.WriteByte((byte)(originalByte ^ 0x01));
        }
        var clientConflict = deploymentService.InspectAsync(deploymentRequest)
            .GetAwaiter().GetResult();
        Assert(clientConflict.State == ModInstallationState.Conflict &&
               !clientConflict.CanInstall && !clientConflict.CanRestore,
            "changed client package was not classified as an unsafe deployment conflict");
        try
        {
            _ = deploymentService.InstallAsync(deploymentRequest).GetAwaiter().GetResult();
            throw new InvalidOperationException("deployment installed across a changed client package");
        }
        catch (ArkiveModDeploymentRefusalException exception)
        {
            Assert(exception.Code == "DEP007",
                $"changed-client install refusal used unexpected code {exception.Code}");
        }
        File.Copy(restoreResult.Receipt.BackupPath, packagePath, overwrite: true);
        File.SetLastWriteTimeUtc(packagePath, originalTimestamp);

        var raceService = new ArkiveModDeploymentService(
            processGuard: new SyntheticModDeploymentProcessGuard(() =>
            {
                using var stream = new FileStream(
                    packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var originalByte = stream.ReadByte();
                stream.Position = 0;
                stream.WriteByte((byte)(originalByte ^ 0x01));
            }));
        try
        {
            _ = raceService.InstallAsync(deploymentRequest).GetAwaiter().GetResult();
            throw new InvalidOperationException("deployment overwrote a client package changed after preflight");
        }
        catch (ArkiveModDeploymentRefusalException exception)
        {
            Assert(exception.Code == "DEP025",
                $"commit-time identity refusal used unexpected code {exception.Code}");
        }
        Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))) !=
                   modBundleResult.Bundle.PayloadSha256,
            "commit-time race refusal unexpectedly installed the mod payload");
        File.Copy(restoreResult.Receipt.BackupPath, packagePath, overwrite: true);
        File.SetLastWriteTimeUtc(packagePath, originalTimestamp);

        using (var stream = new FileStream(
                   restoreResult.Receipt.BackupPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var originalByte = stream.ReadByte();
            stream.Position = 0;
            stream.WriteByte((byte)(originalByte ^ 0x01));
        }
        var corruptBackup = deploymentService.InspectAsync(deploymentRequest)
            .GetAwaiter().GetResult();
        Assert(corruptBackup.State == ModInstallationState.Restored &&
               corruptBackup.BackupExists && !corruptBackup.BackupVerified &&
               !corruptBackup.CanInstall,
            "corrupt content-addressed backup did not disable installation");
        File.Copy(packagePath, restoreResult.Receipt.BackupPath, overwrite: true);
        File.SetLastWriteTimeUtc(restoreResult.Receipt.BackupPath, originalTimestamp);

        using (var stream = new FileStream(
                   modBundleResult.PayloadPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var originalByte = stream.ReadByte();
            stream.Position = 0;
            stream.WriteByte((byte)(originalByte ^ 0x01));
        }
        try
        {
            _ = deploymentService.InspectAsync(deploymentRequest).GetAwaiter().GetResult();
            throw new InvalidOperationException("deployment accepted a tampered bundle payload");
        }
        catch (ArkiveModDeploymentRefusalException exception)
        {
            Assert(exception.Code == "DEP004",
                $"tampered-payload refusal used unexpected code {exception.Code}");
        }
        File.Copy(replacementUpkResult.OutputUpkPath, modBundleResult.PayloadPath, overwrite: true);

        var blockedService = new ArkiveModDeploymentService(
            processGuard: new SyntheticModDeploymentProcessGuard("LOSTARK (PID 4242)"));
        try
        {
            _ = blockedService.InstallAsync(deploymentRequest).GetAwaiter().GetResult();
            throw new InvalidOperationException("deployment installed while the game process gate was blocked");
        }
        catch (ArkiveModDeploymentRefusalException exception)
        {
            Assert(exception.Code == "DEP020",
                $"running-game refusal used unexpected code {exception.Code}");
        }
        Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))) == originalHash,
            "a refused deployment changed the synthetic client package");

        var tamperedVerificationDirectory = Path.Combine(
            fixtureDirectory, "replacement-upk-tamper-verification");
        Directory.CreateDirectory(tamperedVerificationDirectory);
        var tamperedManifestPath = Path.Combine(
            tamperedVerificationDirectory, ReplacementUpkBuildService.ManifestFileName);
        var tamperedUpkPath = Path.Combine(
            tamperedVerificationDirectory, ReplacementUpkBuildService.OutputUpkFileName);
        File.Copy(replacementUpkResult.ManifestPath, tamperedManifestPath, overwrite: true);
        File.Copy(replacementUpkResult.OutputUpkPath, tamperedUpkPath, overwrite: true);
        using (var tamperedStream = new FileStream(
                   tamperedUpkPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var originalByte = tamperedStream.ReadByte();
            Assert(originalByte >= 0, "replacement UPK tamper fixture was unexpectedly empty");
            tamperedStream.Position = 0;
            tamperedStream.WriteByte((byte)(originalByte ^ 0x01));
        }
        try
        {
            _ = ReplacementUpkBuildService.VerifyOutputAsync(tamperedManifestPath)
                .GetAwaiter()
                .GetResult();
            throw new InvalidOperationException(
                "replacement UPK verification accepted a same-size tampered output");
        }
        catch (ReplacementUpkBuildRefusalException exception)
        {
            Assert(exception.Code == "RUB034",
                $"replacement UPK tamper refusal used unexpected code {exception.Code}");
        }
    }

    private static void TestTextureReplacementPayloadFormats()
    {
        RunFormat("PF_DXT1", "unity-crunch-dxt1.crn", channel: 2);
        RunFormat("PF_BC5", "unity-crunch-bc5.crn", channel: 1);

        void RunFormat(string pixelFormat, string fixtureName, int channel)
        {
            var suffix = pixelFormat.Replace("PF_", string.Empty, StringComparison.Ordinal);
            var packagePath = Path.Combine(fixtureDirectory, $"replacement-{suffix}.upk");
            var sourceCrn = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName));
            var range = WriteTextureReconPackage(
                packagePath,
                lostArkTagExtensions: true,
                pixelFormatName: pixelFormat,
                storedMipOverride: sourceCrn,
                textureWidth: 16,
                textureHeight: 16);
            var decoder = new NativeCrunchDecoder();
            var report = new TextureReconnaissanceService()
                .InspectAsync(new TextureReconnaissanceRequest(
                    packagePath, $"replacement-{suffix}", 0, AssetKind.Texture,
                    "Texture2D", "TestTexture", range.SerialOffset, range.SerialSize))
                .GetAwaiter()
                .GetResult();
            var recon = new TextureReconnaissanceWorkspaceStore(
                    Path.Combine(fixtureDirectory, $"replacement-{suffix}-recon"))
                .SaveAsync(report)
                .GetAwaiter()
                .GetResult();
            var nativeOutput = Path.Combine(fixtureDirectory, $"replacement-{suffix}-native");
            var native = new NativeTextureExportService(crunchDecoder: decoder)
                .ExportAsync(new NativeTextureExportRequest(report, nativeOutput, recon.JsonPath))
                .GetAwaiter()
                .GetResult();
            var editPath = Path.Combine(fixtureDirectory, $"replacement-{suffix}-edit.png");
            var edit = PngRgbaCodec.ReadAsync(
                    Path.Combine(nativeOutput, native.Manifest.EditImageRelativePath!))
                .GetAwaiter()
                .GetResult();
            edit.Pixels[channel] = (byte)(255 - edit.Pixels[channel]);
            PngRgbaCodec.WriteAsync(editPath, edit).GetAwaiter().GetResult();
            var candidateOutput = Path.Combine(fixtureDirectory, $"replacement-{suffix}-candidate");
            var candidate = new TextureImportCandidateService()
                .CreateAsync(new TextureImportCandidateRequest(
                    native.ManifestPath, editPath, candidateOutput))
                .GetAwaiter()
                .GetResult();
            var layout = new TextureLayoutConfirmationService(crunchDecoder: decoder);
            var encoderPath = CrunchEncoderLocator.FindExecutable();
            Assert(encoderPath is not null, "pinned Unity Crunch encoder was not found");
            var payload = new TextureReplacementPayloadService(
                    layout, new UnityCrunchEncoder(encoderPath!), decoder)
                .BuildAsync(new TextureReplacementPayloadRequest(
                    candidate.ManifestPath,
                    Path.Combine(fixtureDirectory, $"replacement-{suffix}-payloads")))
                .GetAwaiter()
                .GetResult();
            Assert(payload.Manifest.PixelFormat.StartsWith(pixelFormat, StringComparison.Ordinal),
                $"{pixelFormat} payload manifest recorded the wrong format");
            Assert(payload.Manifest.PayloadSetReadyForWriter &&
                   payload.Manifest.CrunchPayloadsEncoded == 1,
                $"{pixelFormat} payload workflow did not pass readiness " +
                $"(layout={payload.Manifest.MipPayloadLayoutConfirmed}, " +
                $"decode={payload.Manifest.AllStoragePayloadsDecode}, " +
                $"quality={payload.Manifest.AllQualityGatesPassed}, " +
                $"mip={string.Join("; ", payload.Manifest.Mips.Select(mip =>
                    $"{mip.MipIndex}:codec={mip.CodecMetadataVerified},decode={mip.DecodeBackVerified}," +
                    $"quality={mip.Quality.QualityGatePassed},psnr={mip.Quality.PsnrDb:F2}," +
                    $"meanAngle={mip.Quality.MeanNormalAngularErrorDegrees:F2}," +
                    $"maxAngle={mip.Quality.MaximumNormalAngularErrorDegrees:F2}"))})");
            Assert(payload.Manifest.Mips.Single().CodecMetadataVerified &&
                   payload.Manifest.Mips.Single().DecodeBackVerified &&
                   payload.Manifest.Mips.Single().Quality.QualityGatePassed,
                $"{pixelFormat} payload workflow evidence was incomplete");

            var assembly = new TextureExportAssemblyService(crunchDecoder: decoder)
                .AssembleAsync(new TextureExportAssemblyRequest(
                    payload.ManifestPath,
                    Path.Combine(fixtureDirectory, $"replacement-{suffix}-assembly")))
                .GetAwaiter()
                .GetResult();
            Assert(assembly.Manifest.NoOpExportByteIdentical &&
                   assembly.Manifest.ReplacementBlobReadyForRewritePlan &&
                   assembly.Manifest.AllStoragePayloadsDecode,
                $"{pixelFormat} export assembly did not pass replay verification");
            var plan = new PackageRewritePlanService()
                .PlanAsync(new PackageRewritePlanRequest(
                    assembly.ManifestPath,
                    Path.Combine(fixtureDirectory, $"replacement-{suffix}-plan")))
                .GetAwaiter()
                .GetResult();
            Assert(plan.Manifest.PlanReadyForLogicalImageWriter &&
                   plan.Manifest.BulkOffsetRebases.Count == 1 &&
                   plan.Manifest.SelectedTablePatches.Count == 2 &&
                   plan.Manifest.ExistingExportsRelocated == 0,
                $"{pixelFormat} rewrite dry run did not account for all selected mutations");
            var image = new LogicalPackageImageService()
                .BuildAsync(new LogicalPackageImageRequest(
                    plan.ManifestPath,
                    Path.Combine(fixtureDirectory, $"replacement-{suffix}-logical-image")))
                .GetAwaiter()
                .GetResult();
            Assert(image.Manifest.LogicalImageReadyForPhysicalWriter &&
                   image.Manifest.AllUnplannedSourceBytesIdentical &&
                   image.Manifest.AllTablePatchesApplied &&
                   image.Manifest.AllBulkOffsetsRebased &&
                   image.Manifest.AppendedReplacementVerified &&
                   !image.Manifest.PackageWasModified,
                $"{pixelFormat} logical image did not pass replay verification");

            // A doubled canvas becomes a larger texture: one new top mip, the original mip kept below it.
            var doubled = new RgbaImage(edit.Width * 2, edit.Height * 2, new byte[edit.Pixels.Length * 4]);
            for (var y = 0; y < doubled.Height; y++)
            for (var x = 0; x < doubled.Width; x++)
                edit.Pixels.AsSpan(((y / 2) * edit.Width + x / 2) * 4, 4)
                    .CopyTo(doubled.Pixels.AsSpan((y * doubled.Width + x) * 4, 4));
            var doubledPath = Path.Combine(fixtureDirectory, $"replacement-{suffix}-edit-2x.png");
            PngRgbaCodec.WriteAsync(doubledPath, doubled).GetAwaiter().GetResult();
            var upscaledCandidate = new TextureImportCandidateService()
                .CreateAsync(new TextureImportCandidateRequest(
                    native.ManifestPath, doubledPath, Path.Combine(fixtureDirectory, $"replacement-{suffix}-2x-candidate")))
                .GetAwaiter()
                .GetResult();
            Assert(upscaledCandidate.Manifest.UpscaleLevels == 1 &&
                   upscaledCandidate.Manifest.Width == native.Manifest.Width * 2 &&
                   upscaledCandidate.Manifest.MipCount == native.Manifest.MipCount + 1,
                $"{pixelFormat} doubled edit was not imported as a larger texture");
            var upscaledPayload = new TextureReplacementPayloadService(
                    layout, new UnityCrunchEncoder(encoderPath!), decoder)
                .BuildAsync(new TextureReplacementPayloadRequest(
                    upscaledCandidate.ManifestPath, Path.Combine(fixtureDirectory, $"replacement-{suffix}-2x-payloads")))
                .GetAwaiter()
                .GetResult();
            Assert(upscaledPayload.Manifest.PayloadSetReadyForWriter &&
                   upscaledPayload.Manifest.Mips.Count == native.Manifest.MipCount + 1 &&
                   upscaledPayload.Manifest.Mips[0].Width == native.Manifest.Width * 2,
                $"{pixelFormat} doubled payload set was not ready for the writer");
            var upscaledAssemblyOutput = Path.Combine(fixtureDirectory, $"replacement-{suffix}-2x-assembly");
            var upscaledAssembly = new TextureExportAssemblyService(crunchDecoder: decoder)
                .AssembleAsync(new TextureExportAssemblyRequest(upscaledPayload.ManifestPath, upscaledAssemblyOutput))
                .GetAwaiter()
                .GetResult();
            var upscaledExport = File.ReadAllBytes(Path.Combine(
                upscaledAssemblyOutput, upscaledAssembly.Manifest.ReplacementExportRelativePath));
            int PropertyValue(string name) => BinaryPrimitives.ReadInt32LittleEndian(upscaledExport.AsSpan(checked((int)
                report.Properties.Single(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ValueRelativeOffset)));
            Assert(upscaledAssembly.Manifest.NoOpExportByteIdentical &&
                   upscaledAssembly.Manifest.ReplacementBlobReadyForRewritePlan &&
                   upscaledAssembly.Manifest.Mips.Count == native.Manifest.MipCount + 1 &&
                   PropertyValue("SizeX") == native.Manifest.Width * 2 &&
                   PropertyValue("SizeY") == native.Manifest.Height * 2,
                $"{pixelFormat} doubled export did not carry the new mips and size");
            var upscaledPlan = new PackageRewritePlanService()
                .PlanAsync(new PackageRewritePlanRequest(
                    upscaledAssembly.ManifestPath, Path.Combine(fixtureDirectory, $"replacement-{suffix}-2x-plan")))
                .GetAwaiter()
                .GetResult();
            var upscaledImage = new LogicalPackageImageService()
                .BuildAsync(new LogicalPackageImageRequest(
                    upscaledPlan.ManifestPath, Path.Combine(fixtureDirectory, $"replacement-{suffix}-2x-logical-image")))
                .GetAwaiter()
                .GetResult();
            Assert(upscaledPlan.Manifest.BulkOffsetRebases.Count == native.Manifest.MipCount + 1 &&
                   upscaledImage.Manifest.LogicalImageReadyForPhysicalWriter &&
                   upscaledImage.Manifest.AllBulkOffsetsRebased &&
                   upscaledImage.Manifest.AppendedReplacementVerified,
                $"{pixelFormat} doubled texture did not pass the logical package rewrite");
        }
    }

    private static void TestNativeTextureExportRefusal()
    {
        var path = Path.Combine(fixtureDirectory, "native-texture-refusal.upk");
        var range = WriteTextureReconPackage(path);
        var report = new TextureReconnaissanceService()
            .InspectAsync(new TextureReconnaissanceRequest(
                path,
                "native-texture-refusal",
                1,
                AssetKind.Texture,
                "Texture2D",
                "TestTexture",
                range.SerialOffset,
                range.SerialSize))
            .GetAwaiter()
            .GetResult();
        var output = Path.Combine(fixtureDirectory, "native-refusal-output");
        try
        {
            _ = new NativeTextureExportService(crunchDecoder: new SyntheticCrunchDecoder())
                .ExportAsync(new NativeTextureExportRequest(report, output, "recon.json"))
                .GetAwaiter()
                .GetResult();
            throw new InvalidOperationException("texture without verified Crunch was exported");
        }
        catch (NativeTextureExportRefusalException exception)
        {
            Assert(exception.Code == "NTX013", "unexpected native export refusal code");
        }
    }

}
