using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Inspection;
using Arkive.Core.Packages;

namespace Arkive.Core.Textures;

public sealed record TextureExportAssemblyRequest(
    string TextureReplacementPayloadManifestPath,
    string OutputDirectory);

public sealed record TextureExportAssemblyMip(
    int MipIndex,
    int Width,
    int Height,
    uint BulkDataFlags,
    int SourceElementCount,
    int SourceStoredBytes,
    int ReplacementElementCount,
    int ReplacementStoredBytes,
    long HeaderRelativeOffset,
    long ElementCountFieldRelativeOffset,
    long StoredSizeFieldRelativeOffset,
    long BulkOffsetFieldRelativeOffset,
    long PayloadRelativeOffset,
    long PayloadLogicalOffset,
    string StorageCodec,
    string ReplacementStoredSha256,
    string DecodedGpuSha256,
    bool DescriptorReplayed,
    bool CodecMetadataVerified,
    bool DecodeBackVerified,
    bool QualityGateRetained);

public sealed record TextureExportAssemblyManifest(
    int SchemaVersion,
    string AssemblyId,
    DateTimeOffset CreatedAt,
    string TextureReplacementPayloadManifestPath,
    string PayloadSetId,
    string LayoutConfirmationManifestPath,
    string LayoutConfirmationId,
    string NativeTextureManifestPath,
    string SourcePackagePath,
    string SourcePackageSha256,
    string PackageAlias,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    string PixelFormat,
    int Width,
    int Height,
    int MipCount,
    long SerialOffset,
    long SourceSerialSize,
    string SourceExportSha256,
    string NoOpExportRelativePath,
    long NoOpExportBytes,
    string NoOpExportSha256,
    bool NoOpExportByteIdentical,
    string ReplacementExportRelativePath,
    long ReplacementExportBytes,
    string ReplacementExportSha256,
    long ReplacementSizeDelta,
    long PreservedPrefixBytes,
    string PreservedPrefixSha256,
    long PreservedSuffixBytes,
    string PreservedSuffixSha256,
    bool AllNonMipBytesPreserved,
    bool AllDescriptorsReplayed,
    bool AllStoragePayloadsDecode,
    bool AllQualityGatesRetained,
    bool ReplacementBlobReadyForRewritePlan,
    bool PackageWasModified,
    IReadOnlyList<TextureLayoutCheck> Checks,
    IReadOnlyList<TextureExportAssemblyMip> Mips,
    double DurationSeconds)
{
    /// <summary>Mip levels added above the source texture by an upscaled edit.</summary>
    public int UpscaleLevels { get; init; }
}

public sealed record TextureExportAssemblyResult(
    string OutputDirectory,
    string ManifestPath,
    string NoOpExportPath,
    string ReplacementExportPath,
    TextureExportAssemblyManifest Manifest,
    IReadOnlyList<string> OutputFiles,
    TimeSpan Duration);

public sealed class TextureExportAssemblyRefusalException : NotSupportedException
{
    public TextureExportAssemblyRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Reconstructs only the selected serialized Texture2D export. It proves a no-op
/// reconstruction is byte-identical and validates a replacement reconstruction by
/// replaying every inline mip descriptor and decoding every stored payload. It never
/// writes a package.
/// </summary>
public sealed class TextureExportAssemblyService
{
    public const int CurrentManifestSchemaVersion = 1;
    public const string ManifestFileName = "arkive-texture-export-assembly.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly UpkPackageReader _summaryReader;
    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly ICrunchDecoder _crunchDecoder;

    public TextureExportAssemblyService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null,
        ICrunchDecoder? crunchDecoder = null)
    {
        _summaryReader = summaryReader ?? new UpkPackageReader();
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(_summaryReader, _protectionProvider);
        _crunchDecoder = crunchDecoder ?? new NativeCrunchDecoder();
    }

    public static async Task<TextureExportAssemblyManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = RequireFile(path, "TEA001", "texture export-assembly manifest");
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<TextureExportAssemblyManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new TextureExportAssemblyRefusalException(
                           "TEA002", "The texture export-assembly manifest was empty.");
        if (manifest.SchemaVersion != CurrentManifestSchemaVersion)
        {
            Refuse("TEA003", $"Export-assembly schema {manifest.SchemaVersion} is unsupported.");
        }
        return manifest;
    }

    public async Task<TextureExportAssemblyResult> AssembleAsync(
        TextureExportAssemblyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var payloadManifestPath = RequireFile(
            request.TextureReplacementPayloadManifestPath, "TEA004", "replacement-payload manifest");
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            Refuse("TEA005", "The export-assembly output directory must be empty.");
        }

        var payload = await TextureReplacementPayloadService.LoadManifestAsync(
            payloadManifestPath, cancellationToken).ConfigureAwait(false);
        if (!payload.PayloadSetReadyForWriter || payload.PackageWasModified ||
            !payload.MipPayloadLayoutConfirmed || !payload.AllStoragePayloadsDecode ||
            !payload.AllQualityGatesPassed)
        {
            Refuse("TEA006", "A fully passing, staging-only replacement payload set is required.");
        }
        var layoutPath = RequireFile(
            payload.LayoutConfirmationManifestPath, "TEA007", "layout-confirmation manifest");
        var layout = await TextureLayoutConfirmationService.LoadManifestAsync(
            layoutPath, cancellationToken).ConfigureAwait(false);
        if (!layout.MipPayloadLayoutConfirmed || layout.PackageRewriteConfirmed ||
            !layout.ConfirmationId.Equals(payload.LayoutConfirmationId, StringComparison.OrdinalIgnoreCase) ||
            layout.NativeExportIndex != payload.NativeExportIndex ||
            payload.UpscaleLevels is < 0 or > 2 || layout.Mips.Count + payload.UpscaleLevels != payload.Mips.Count)
        {
            Refuse("TEA008", "The payload set and layout-confirmation identity/scope disagree.");
        }

        var nativePath = RequireFile(layout.NativeTextureManifestPath, "TEA009", "native texture manifest");
        var native = await ReadJsonAsync<NativeTextureExportManifest>(
            nativePath, "TEA010", cancellationToken).ConfigureAwait(false);
        if (native.NativeExportIndex != layout.NativeExportIndex || native.SerialOffset != layout.SerialOffset ||
            native.SerialSize != layout.SerialSize || native.MipCount != layout.MipCount ||
            !native.SourcePackageSha256.Equals(layout.SourcePackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("TEA011", "Native export identity differs from the confirmed layout.");
        }
        var profile = NativeTextureFormats.Supported.FirstOrDefault(item =>
            item.ManifestPixelFormat.Equals(payload.PixelFormat, StringComparison.OrdinalIgnoreCase)) ??
            throw new TextureExportAssemblyRefusalException(
                "TEA012", $"No export assembler is registered for {payload.PixelFormat}.");

        var packagePath = RequireFile(payload.SourcePackagePath, "TEA013", "source package");
        var packageInfo = new FileInfo(packagePath);
        if (packageInfo.Length != native.SourcePackageLength ||
            packageInfo.LastWriteTimeUtc.Ticks != native.SourcePackageLastWriteUtcTicks)
        {
            Refuse("TEA014", "The source package metadata changed after native export.");
        }
        var packageHash = await HashFileAsync(packagePath, cancellationToken).ConfigureAwait(false);
        if (!packageHash.Equals(payload.SourcePackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("TEA015", "The source package SHA-256 changed after payload generation.");
        }
        var catalog = _catalogReader.Read(packagePath);
        var asset = catalog.Assets.SingleOrDefault(item =>
            item.ExportIndex == payload.NativeExportIndex && item.SerialOffset == layout.SerialOffset &&
            item.SerialSize == layout.SerialSize &&
            item.ObjectName.Equals(payload.ObjectName, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            Refuse("TEA016", "The target Texture2D no longer resolves to the confirmed export table entry.");
        }

        var summary = _summaryReader.Read(packagePath);
        using var logical = new LogicalPackageStream(summary, _protectionProvider);
        if (layout.SerialOffset < 0 || layout.SerialSize <= 0 || layout.SerialSize > int.MaxValue ||
            layout.SerialOffset > logical.Length - layout.SerialSize)
        {
            Refuse("TEA017", "The confirmed source export range is outside the logical package.");
        }
        var sourceExport = new byte[checked((int)layout.SerialSize)];
        logical.Position = layout.SerialOffset;
        logical.ReadExactly(sourceExport);
        var sourceHash = Hash(sourceExport);
        if (!sourceHash.Equals(native.SourcePayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("TEA018", "The current logical source export no longer matches its native hash.");
        }

        var sizePatches = payload.UpscaleLevels == 0
            ? []
            : await SizePatchesAsync(native, layout, payload, cancellationToken).ConfigureAwait(false);
        var payloadDirectory = Path.GetDirectoryName(payloadManifestPath)!;
        var replacementPayloads = new byte[payload.Mips.Count][];
        for (var index = 0; index < payload.Mips.Count; index++)
        {
            var mip = payload.Mips[index];
            if (mip.MipIndex != index || !mip.Quality.QualityGatePassed ||
                !mip.CodecMetadataVerified || !mip.DecodeBackVerified)
            {
                Refuse("TEA019", $"Replacement mip {index} is incomplete or failed verification.");
            }
            var path = ResolveWithin(payloadDirectory, mip.ReplacementRelativePath, "TEA020");
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length != mip.ReplacementStoredBytes ||
                !Hash(bytes).Equals(mip.ReplacementStoredSha256, StringComparison.OrdinalIgnoreCase))
            {
                Refuse("TEA021", $"Replacement mip {index} size/hash changed after generation.");
            }
            replacementPayloads[index] = bytes;
        }

        var noOp = BuildBlob(sourceExport, layout, payload, replacementPayloads, [], useReplacement: false);
        var noOpHash = Hash(noOp);
        var noOpIdentical = noOp.AsSpan().SequenceEqual(sourceExport);
        if (!noOpIdentical || !noOpHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("TEA022", "The reconstructed no-op Texture2D export was not byte-identical.");
        }
        _ = ValidateBlob(noOp, sourceExport, layout, payload, profile, useReplacement: false);

        var replacement = BuildBlob(
            sourceExport, layout, payload, replacementPayloads, sizePatches, useReplacement: true);
        var validation = ValidateBlob(
            replacement, sourceExport, layout, payload, profile, useReplacement: true);
        var allDescriptors = validation.All(item => item.DescriptorReplayed);
        var allCodecs = validation.All(item => item.CodecMetadataVerified && item.DecodeBackVerified);
        var allQuality = validation.All(item => item.QualityGateRetained);
        var prefixBytes = checked((int)layout.MipArrayRelativeOffset);
        var suffixBytes = checked((int)(sourceExport.Length - layout.MipArrayEndRelativeOffset));
        var expectedPrefix = PatchedPrefix(sourceExport, prefixBytes, sizePatches);
        var nonMipPreserved = replacement.AsSpan(0, prefixBytes).SequenceEqual(expectedPrefix) &&
                              replacement.AsSpan(replacement.Length - suffixBytes, suffixBytes)
                                  .SequenceEqual(sourceExport.AsSpan(sourceExport.Length - suffixBytes, suffixBytes));
        if (!allDescriptors || !allCodecs || !allQuality || !nonMipPreserved)
        {
            Refuse("TEA023", "The replacement export failed descriptor, codec, quality, or preservation replay.");
        }

        var safeName = SafeFileName(payload.ObjectName);
        var noOpRelative = safeName + "-noop-export.bin";
        var replacementRelative = safeName + "-replacement-export.bin";
        var noOpPath = Path.Combine(outputDirectory, noOpRelative);
        var replacementPath = Path.Combine(outputDirectory, replacementRelative);
        await WriteBytesAtomicAsync(noOpPath, noOp, cancellationToken).ConfigureAwait(false);
        await WriteBytesAtomicAsync(replacementPath, replacement, cancellationToken).ConfigureAwait(false);

        var checks = new List<TextureLayoutCheck>
        {
            new("TEA_SOURCE_IDENTITY", TextureLayoutCheckStatus.Passed,
                $"Current package/export hashes still match {packageHash} / {sourceHash}."),
            new("TEA_NOOP_IDENTICAL", TextureLayoutCheckStatus.Passed,
                $"Reconstructed no-op export is byte-identical across {noOp.Length:N0} bytes."),
            new("TEA_NON_MIP_PRESERVED", TextureLayoutCheckStatus.Passed,
                sizePatches.Count == 0
                    ? $"Preserved {prefixBytes:N0} byte(s) before and {suffixBytes:N0} byte(s) after the mip array exactly."
                    : $"Preserved {prefixBytes:N0} byte(s) before and {suffixBytes:N0} byte(s) after the mip array exactly, " +
                      $"except the {string.Join(", ", sizePatches.Select(patch => patch.Name))} values set for the " +
                      $"{payload.Width}x{payload.Height} upscale."),
            new("TEA_DESCRIPTOR_REPLAY", TextureLayoutCheckStatus.Passed,
                $"Replayed all {validation.Count:N0} mip descriptors with recalculated sizes and logical payload offsets."),
            new("TEA_DECODE_BACK", TextureLayoutCheckStatus.Passed,
                "Every assembled CRN or UE3/LZ4 payload decoded to its recorded replacement GPU hash."),
            new("TEA_NO_PACKAGE_WRITE", TextureLayoutCheckStatus.Information,
                "Only standalone export blobs were written; the source package was never opened for writing."),
        };
        watch.Stop();
        var manifest = new TextureExportAssemblyManifest(
            CurrentManifestSchemaVersion,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            payloadManifestPath,
            payload.PayloadSetId,
            layoutPath,
            layout.ConfirmationId,
            nativePath,
            packagePath,
            packageHash,
            payload.PackageAlias,
            payload.NativeExportIndex,
            payload.ClassName,
            payload.ObjectName,
            payload.PixelFormat,
            payload.Width,
            payload.Height,
            payload.MipCount,
            layout.SerialOffset,
            layout.SerialSize,
            sourceHash,
            noOpRelative,
            noOp.Length,
            noOpHash,
            noOpIdentical,
            replacementRelative,
            replacement.Length,
            Hash(replacement),
            replacement.Length - sourceExport.LongLength,
            prefixBytes,
            Hash(sourceExport.AsSpan(0, prefixBytes)),
            suffixBytes,
            Hash(sourceExport.AsSpan(sourceExport.Length - suffixBytes, suffixBytes)),
            nonMipPreserved,
            allDescriptors,
            allCodecs,
            allQuality,
            ReplacementBlobReadyForRewritePlan: true,
            PackageWasModified: false,
            checks,
            validation,
            watch.Elapsed.TotalSeconds)
        {
            UpscaleLevels = payload.UpscaleLevels,
        };
        var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return new TextureExportAssemblyResult(
            outputDirectory,
            manifestPath,
            noOpPath,
            replacementPath,
            manifest,
            [noOpPath, replacementPath, manifestPath],
            watch.Elapsed);
    }

    private byte[] BuildBlob(
        byte[] source,
        TextureLayoutConfirmationManifest layout,
        TextureReplacementPayloadManifest payload,
        IReadOnlyList<byte[]> replacementPayloads,
        IReadOnlyList<SizePropertyPatch> sizePatches,
        bool useReplacement)
    {
        var capacity = useReplacement
            ? checked((int)(source.LongLength + payload.ReplacementStoredBytes - payload.SourceStoredBytes))
            : source.Length;
        using var stream = new MemoryStream(capacity);
        using var writer = new BinaryWriter(stream);
        var arrayOffset = checked((int)layout.MipArrayRelativeOffset);
        writer.Write(PatchedPrefix(source, arrayOffset, sizePatches));
        var mipCount = useReplacement ? payload.Mips.Count : layout.MipCount;
        writer.Write(mipCount);
        for (var index = 0; index < mipCount; index++)
        {
            var confirmed = useReplacement ? TargetMip(layout, payload, index) : layout.Mips[index];
            var generated = payload.Mips[useReplacement ? index : index + payload.UpscaleLevels];
            var stored = useReplacement
                ? replacementPayloads[index]
                : source.AsSpan(checked((int)confirmed.PayloadRelativeOffset), confirmed.StoredBytes).ToArray();
            var elementCount = useReplacement
                ? ReplacementElementCount(confirmed, generated, stored.Length)
                : confirmed.ElementCount;
            writer.Write(confirmed.BulkDataFlags);
            writer.Write(elementCount);
            writer.Write(stored.Length);
            var payloadRelativeOffset = checked((int)stream.Position + 4);
            var payloadLogicalOffset = checked(layout.SerialOffset + payloadRelativeOffset);
            if (payloadLogicalOffset > int.MaxValue)
            {
                Refuse("TEA024", $"Mip {index} logical payload offset exceeds UE3 Int32 storage.");
            }
            writer.Write(checked((int)payloadLogicalOffset));
            writer.Write(stored);
            writer.Write(confirmed.Width);
            writer.Write(confirmed.Height);
        }
        var suffixOffset = checked((int)layout.MipArrayEndRelativeOffset);
        writer.Write(source, suffixOffset, source.Length - suffixOffset);
        return stream.ToArray();
    }

    private IReadOnlyList<TextureExportAssemblyMip> ValidateBlob(
        byte[] blob,
        byte[] source,
        TextureLayoutConfirmationManifest layout,
        TextureReplacementPayloadManifest payload,
        NativeTextureFormatProfile profile,
        bool useReplacement)
    {
        var cursor = checked((int)layout.MipArrayRelativeOffset);
        var mipCount = useReplacement ? payload.Mips.Count : layout.MipCount;
        if (BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(cursor)) != mipCount)
        {
            Refuse("TEA025", "The assembled mip-array count is incorrect.");
        }
        cursor += 4;
        var results = new List<TextureExportAssemblyMip>(mipCount);
        for (var index = 0; index < mipCount; index++)
        {
            if (cursor > blob.Length - 24) Refuse("TEA026", $"Assembled mip {index} is truncated.");
            var confirmed = useReplacement ? TargetMip(layout, payload, index) : layout.Mips[index];
            var generated = payload.Mips[useReplacement ? index : index + payload.UpscaleLevels];
            var header = cursor;
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(cursor));
            var elements = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(cursor + 4));
            var storedBytes = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(cursor + 8));
            var bulkOffset = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(cursor + 12));
            var payloadOffset = cursor + 16;
            if (storedBytes <= 0 || payloadOffset > blob.Length - storedBytes - 8)
            {
                Refuse("TEA027", $"Assembled mip {index} payload is unbounded.");
            }
            var width = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(payloadOffset + storedBytes));
            var height = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(payloadOffset + storedBytes + 4));
            var expectedElements = useReplacement
                ? ReplacementElementCount(confirmed, generated, storedBytes)
                : confirmed.ElementCount;
            var descriptor = flags == confirmed.BulkDataFlags && elements == expectedElements &&
                             storedBytes == (useReplacement ? generated.ReplacementStoredBytes : confirmed.StoredBytes) &&
                             bulkOffset == layout.SerialOffset + payloadOffset &&
                             width == confirmed.Width && height == confirmed.Height;
            if (!descriptor) Refuse("TEA028", $"Assembled mip {index} descriptor replay failed.");

            var stored = blob.AsSpan(payloadOffset, storedBytes).ToArray();
            var storedHash = Hash(stored);
            var expectedStoredHash = useReplacement ? generated.ReplacementStoredSha256 : confirmed.StoredSha256;
            if (!storedHash.Equals(expectedStoredHash, StringComparison.OrdinalIgnoreCase))
            {
                Refuse("TEA029", $"Assembled mip {index} stored hash is incorrect.");
            }
            byte[] decoded;
            var codec = false;
            if (generated.SourceStorageCodec.StartsWith("Crunch CRN", StringComparison.Ordinal))
            {
                var info = _crunchDecoder.Inspect(stored);
                codec = info.Width == width && info.Height == height && info.Levels == 1 &&
                        info.Faces == 1 && info.Format == profile.CrunchFormat &&
                        info.FourCc == profile.CrunchFourCc && info.ActualDataSize == stored.Length;
                decoded = profile.DecodeCrunch(_crunchDecoder, stored);
            }
            else if (generated.SourceStorageCodec.Contains("LZ4", StringComparison.OrdinalIgnoreCase))
            {
                var expectedBytes = useReplacement ? generated.CandidateGpuBytes : confirmed.DecodedGpuBytes;
                decoded = Ue3BulkMipDecoder.DecodeLostArkLz4(stored, expectedBytes, out var info);
                codec = info.UncompressedBytes == expectedBytes && info.BlockCount > 0;
            }
            else
            {
                Refuse("TEA030", $"Assembled mip {index} uses an unsupported storage codec.");
                throw new UnreachableException();
            }
            var decodedHash = Hash(decoded);
            var expectedDecodedHash = useReplacement ? generated.DecodeBackGpuSha256 : confirmed.DecodedGpuSha256;
            var decode = decodedHash.Equals(expectedDecodedHash, StringComparison.OrdinalIgnoreCase);
            if (!codec || !decode) Refuse("TEA031", $"Assembled mip {index} failed codec/decode replay.");
            results.Add(new TextureExportAssemblyMip(
                index,
                width,
                height,
                flags,
                confirmed.ElementCount,
                confirmed.StoredBytes,
                expectedElements,
                storedBytes,
                header,
                header + 4L,
                header + 8L,
                header + 12L,
                payloadOffset,
                layout.SerialOffset + payloadOffset,
                generated.SourceStorageCodec,
                storedHash,
                decodedHash,
                descriptor,
                codec,
                decode,
                !useReplacement || generated.Quality.QualityGatePassed));
            cursor = payloadOffset + storedBytes + 8;
        }
        var expectedEnd = blob.Length - checked((int)(source.LongLength - layout.MipArrayEndRelativeOffset));
        if (cursor != expectedEnd) Refuse("TEA032", "Assembled mip-array end is inconsistent with the preserved suffix.");
        return results;
    }

    private sealed record SizePropertyPatch(string Name, int Offset, int Value);

    /// <summary>
    /// The confirmed source layout of replacement mip <paramref name="index"/>; levels an upscale added on top
    /// take the top source mip's storage settings at their own size (their source bytes are never read).
    /// </summary>
    private static TextureMipLayoutConfirmation TargetMip(
        TextureLayoutConfirmationManifest layout,
        TextureReplacementPayloadManifest payload,
        int index)
    {
        if (index >= payload.UpscaleLevels) return layout.Mips[index - payload.UpscaleLevels];
        var generated = payload.Mips[index];
        return layout.Mips[0] with { Width = generated.Width, Height = generated.Height };
    }

    /// <summary>The Texture2D size properties to rewrite so the export describes its upscaled mip chain.</summary>
    private static async Task<IReadOnlyList<SizePropertyPatch>> SizePatchesAsync(
        NativeTextureExportManifest native,
        TextureLayoutConfirmationManifest layout,
        TextureReplacementPayloadManifest payload,
        CancellationToken cancellationToken)
    {
        var recon = await ReadJsonAsync<TextureReconnaissanceReport>(
            RequireFile(layout.ReconnaissanceJsonPath, "TEA034", "texture reconnaissance report"),
            "TEA034", cancellationToken).ConfigureAwait(false);
        var levels = payload.UpscaleLevels;
        var patches = new List<SizePropertyPatch>();
        void Add(string name, Func<long, long> value, bool required)
        {
            var property = recon.Properties.SingleOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                item.Type.Equals("IntProperty", StringComparison.OrdinalIgnoreCase) && item.Size == 4);
            if (property?.NumericValue is not { } current)
            {
                if (required) Refuse("TEA035", $"The source Texture2D has no verified 4-byte {name} property to resize.");
                return;
            }
            if (property.ValueRelativeOffset < 0 || property.ValueRelativeOffset > layout.MipArrayRelativeOffset - 4)
                Refuse("TEA035", $"The source {name} property is outside the preserved Texture2D prefix.");
            patches.Add(new(name, checked((int)property.ValueRelativeOffset), checked((int)value(current))));
        }
        Add("SizeX", value => value << levels, required: true);
        Add("SizeY", value => value << levels, required: true);
        Add("OriginalSizeX", value => value << levels, required: false);
        Add("OriginalSizeY", value => value << levels, required: false);
        Add("MipTailBaseIdx", value => value + levels, required: false);
        if (patches[0].Value != payload.Width || patches[1].Value != payload.Height ||
            native.Width << levels != payload.Width || native.Height << levels != payload.Height)
            Refuse("TEA035", "The upscaled size properties disagree with the replacement payload set.");
        return patches;
    }

    private static byte[] PatchedPrefix(byte[] source, int length, IReadOnlyList<SizePropertyPatch> patches)
    {
        var prefix = source.AsSpan(0, length).ToArray();
        foreach (var patch in patches)
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(patch.Offset), patch.Value);
        return prefix;
    }

    private static int ReplacementElementCount(
        TextureMipLayoutConfirmation confirmed,
        TextureReplacementMipPayload generated,
        int storedBytes) =>
        confirmed.BulkDataFlags switch
        {
            0x200 => storedBytes,
            Ue3BulkMipDecoder.LostArkCompressedBulkFlag => generated.CandidateGpuBytes,
            _ => throw new TextureExportAssemblyRefusalException(
                "TEA033", $"Unsupported bulk flags 0x{confirmed.BulkDataFlags:X8}."),
        };

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "texture" : cleaned;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<T> ReadJsonAsync<T>(string path, string code, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                       .ConfigureAwait(false) ?? throw new JsonException("The document was empty.");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new TextureExportAssemblyRefusalException(
                code, $"{Path.GetFileName(path)} could not be read: {exception.Message}");
        }
    }

    private static string ResolveWithin(string directory, string relativePath, string code)
    {
        var path = Path.GetFullPath(Path.Combine(directory, relativePath));
        var prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            Refuse(code, $"Manifest file is missing or escapes its session: {relativePath}");
        }
        return path;
    }

    private static string RequireFile(string path, string code, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) Refuse(code, $"The {label} path is empty.");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) Refuse(code, $"The {label} was not found: {fullPath}");
        return fullPath;
    }

    private static async Task WriteBytesAtomicAsync(
        string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task SaveJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new TextureExportAssemblyRefusalException(code, message);
}
