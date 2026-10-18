namespace Arkive.SmokeTests;

internal static class PackageArchiveSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("reads an empty Western package", TestEmptyPackage),
        new("reads a Lost Ark compressed-chunk record", TestCompressedPackage),
        new("decodes an unprotected LZ4 logical chunk", TestLogicalLz4Package),
        new("decodes an LZ4 block exactly as long as its data as LZ4, not as raw bytes", TestEqualSizeLz4Block),
        new("decodes a protected logical chunk with an explicit provider", TestProtectedPackage),
        new("decodes a protected chunk with Arkive's Western profile", TestBuiltInWesternProvider),
        new("rejects a protected chunk without a provider", TestMissingProtectionProvider),
        new("builds a UE3 asset catalogue", TestAssetCatalogue),
        new("decodes a Lost Ark package alias", TestPackageNameDecode),
        new("rejects an invalid package tag", TestInvalidTag),
    ];

    private static void TestEmptyPackage()
    {
        var path = Path.Combine(fixtureDirectory, "empty.upk");
        WritePackage(path, chunks: []);

        var summary = new UpkPackageReader().Read(path);
        Assert(summary.IsWesternLostArkProfile, "profile was not detected");
        Assert(summary.NameCount == 0, "name count was not zero");
        Assert(summary.CompressedChunks.Count == 0, "chunk count was not zero");
        AssertNoValidationErrors(summary);
    }

    private static void TestCompressedPackage()
    {
        var path = Path.Combine(fixtureDirectory, "compressed-record.upk");
        var chunk = new CompressedChunk(
            UncompressedOffset: 129,
            UncompressedSize: 32,
            CompressedOffset: 149,
            CompressedSize: 16,
            LostArkFlags: 1);
        WritePackage(path, [chunk]);

        var summary = new UpkPackageReader().Read(path);
        Assert(summary.CompressedChunks.Count == 1, "chunk count was not one");
        Assert(summary.CompressedChunks[0] == chunk, "chunk record did not round-trip");
        Assert(summary.CompressedChunkTableOffset + 20 == summary.PackageSourceOffset &&
               summary.CompressedChunkCountOffset + 4 == summary.CompressedChunkTableOffset &&
               summary.CompressionFlagsOffset + 4 == summary.CompressedChunkCountOffset,
            "physical summary field offsets were not recorded exactly");
        AssertNoValidationErrors(summary);
    }

    private static void TestLogicalLz4Package()
    {
        var path = Path.Combine(fixtureDirectory, "logical-lz4.upk");
        var payload = Enumerable.Range(0, 96).Select(index => (byte)(index * 3)).ToArray();
        WriteCompressedPayloadPackage(path, payload, protect: false);

        var summary = new UpkPackageReader().Read(path);
        using var stream = new LogicalPackageStream(summary);
        stream.Position = 129;
        var decoded = new byte[payload.Length];
        stream.ReadExactly(decoded);
        Assert(decoded.SequenceEqual(payload), "logical LZ4 payload did not round-trip");
    }

    private static void TestEqualSizeLz4Block()
    {
        // 20 literals, a 5-byte match 20 back, then 12 literals: 37 bytes of LZ4 for 37 bytes of data. The game's own
        // packages have such blocks.
        var payload = Enumerable.Range(1, 20).Select(value => (byte)value)
            .Concat(Enumerable.Range(1, 5).Select(value => (byte)value))
            .Concat(Enumerable.Range(100, 12).Select(value => (byte)value)).ToArray();
        var block = new List<byte> { 0xF1, 5 };
        block.AddRange(payload.Take(20));
        block.AddRange([20, 0, 0xC0]);
        block.AddRange(payload.Skip(25));
        Assert(block.Count == payload.Length && Arkive.Core.Compression.Lz4BlockDecoder.Decode(block.ToArray(), payload.Length).SequenceEqual(payload),
            "the fixture is not an equal-size LZ4 block");
        var path = Path.Combine(fixtureDirectory, "logical-lz4-equal-size.upk");
        WriteCompressedPayloadPackage(path, payload, protect: false, compressedBlock: block.ToArray());

        using var stream = new LogicalPackageStream(new UpkPackageReader().Read(path));
        stream.Position = 129;
        var decoded = new byte[payload.Length];
        stream.ReadExactly(decoded);
        Assert(decoded.SequenceEqual(payload), "an LZ4 block as long as its data was read as raw bytes");
    }

    private static void TestProtectedPackage()
    {
        var path = Path.Combine(fixtureDirectory, "logical-protected.upk");
        var payload = Encoding.ASCII.GetBytes(
            "Arkive synthetic protected block. This contains no game data or production key material.");
        WriteCompressedPayloadPackage(path, payload, protect: true);

        var summary = new UpkPackageReader().Read(path);
        using var stream = new LogicalPackageStream(
            summary,
            new AesEcbPrefixProtectionProvider(testKey));
        stream.Position = 129;
        var decoded = new byte[payload.Length];
        stream.ReadExactly(decoded);
        Assert(decoded.SequenceEqual(payload), "protected logical payload did not round-trip");

        var provider = new AesEcbPrefixProtectionProvider(testKey);
        var block = Enumerable.Range(0, 5000).Select(index => (byte)(index * 17)).ToArray();
        var original = block.ToArray();
        var chunk = summary.CompressedChunks.Single();
        Assert(provider.CanProtect(summary, chunk), "AES provider did not advertise protected writes");
        provider.ProtectBlock(summary, chunk, 0, block);
        Assert(!block.SequenceEqual(original), "AES protected-prefix writer did not transform the block");
        Assert(block.AsSpan(4096).SequenceEqual(original.AsSpan(4096)),
            "AES writer changed bytes beyond the protected prefix");
        provider.UnprotectBlock(summary, chunk, 0, block);
        Assert(block.SequenceEqual(original), "AES protect/unprotect did not round-trip exactly");
    }

    private static void TestBuiltInWesternProvider()
    {
        var path = Path.Combine(fixtureDirectory, "logical-western-profile.upk");
        var payload = Encoding.ASCII.GetBytes(
            "Arkive built-in Western profile regression fixture. This contains no game data.");
        var westernKey = Encoding.ASCII.GetBytes("NCMZPFB5R74EGAUWE191UTCLBJV2N6GX");
        try
        {
            WriteCompressedPayloadPackage(path, payload, protect: true, westernKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(westernKey);
        }

        var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
        var summary = new UpkPackageReader().Read(path);
        using var stream = new LogicalPackageStream(summary, provider);
        stream.Position = 129;
        var decoded = new byte[payload.Length];
        stream.ReadExactly(decoded);
        Assert(decoded.SequenceEqual(payload), "built-in Western profile did not decode the protected block");
        Assert(
            provider.Name == ChunkProtectionProviderFactory.WesternGlobalProviderName,
            "built-in Western provider was not identified");
    }

    private static void TestMissingProtectionProvider()
    {
        var path = Path.Combine(fixtureDirectory, "logical-provider-required.upk");
        var payload = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
        WriteCompressedPayloadPackage(path, payload, protect: true);

        var summary = new UpkPackageReader().Read(path);
        using var stream = new LogicalPackageStream(summary);
        stream.Position = 129;
        try
        {
            _ = stream.ReadByte();
            throw new InvalidOperationException("protected package was decoded without a provider");
        }
        catch (PackageAccessException)
        {
        }
    }

    private static void TestAssetCatalogue()
    {
        var path = Path.Combine(fixtureDirectory, "catalog.upk");
        WriteCataloguePackage(path);

        var catalog = new AssetCatalogReader().Read(path);
        Assert(catalog.Names.Count == 6, "unexpected name count");
        Assert(catalog.Imports.Count == 1, "unexpected import count");
        Assert(catalog.Exports.Count == 1, "unexpected export count");
        Assert(catalog.Assets.Count == 1, "unexpected asset count");

        var asset = catalog.Assets[0];
        Assert(asset.ObjectName == "TestTexture", "object name was not resolved");
        Assert(asset.ClassName == "Texture2D", "class name was not resolved");
        Assert(asset.Kind == AssetKind.Texture, "asset kind was not inferred");
        Assert(asset.ObjectPath.EndsWith("catalog.TestTexture", StringComparison.Ordinal),
            "object path was not built");
    }

    private static void TestPackageNameDecode()
    {
        Assert(
            LostArkPackageName.TryDecodeFileName("DKV6KRSCXY3T6D9CJIK3G.upk", out var decoded),
            "encoded package name was not recognized");
        Assert(decoded == "startup", $"unexpected decoded package name: {decoded}");
        Assert(
            LostArkPackageName.GetDisplayName("ordinary_package.upk") == "ordinary_package",
            "ordinary filename should not be decoded");
        Assert(
            LostArkPackageName.GetDisplayName("startup.upk") == "startup",
            "short ordinary filename should not be decoded");
    }

    private static void TestInvalidTag()
    {
        var path = Path.Combine(fixtureDirectory, "invalid.upk");
        File.WriteAllBytes(path, new byte[129]);

        try
        {
            _ = new UpkPackageReader().Read(path);
            throw new InvalidOperationException("invalid tag was accepted");
        }
        catch (PackageFormatException exception)
        {
            Assert(exception.Message.Contains("Unexpected package tag", StringComparison.Ordinal),
                "unexpected error was returned for invalid tag");
        }
    }

}
