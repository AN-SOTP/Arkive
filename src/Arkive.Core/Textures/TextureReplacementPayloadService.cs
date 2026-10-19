using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkive.Core.Textures;

public sealed record TexturePayloadQualityMetrics(
    bool ExactGpuBlocks,
    double MeanAbsoluteChannelError,
    double RootMeanSquareChannelError,
    double PsnrDb,
    int MaximumChannelError,
    long PixelsAboveError16,
    double? MeanNormalAngularErrorDegrees,
    double? MaximumNormalAngularErrorDegrees,
    bool QualityGatePassed);

public sealed record TextureReplacementMipPayload(
    int MipIndex,
    int Width,
    int Height,
    uint BulkDataFlags,
    string SourceStorageCodec,
    int SourceStoredBytes,
    string SourceStoredSha256,
    string EncodingAction,
    string ReplacementRelativePath,
    int ReplacementStoredBytes,
    string ReplacementStoredSha256,
    int CandidateGpuBytes,
    string CandidateGpuSha256,
    string DecodeBackGpuSha256,
    bool CodecMetadataVerified,
    bool DecodeBackVerified,
    TexturePayloadQualityMetrics Quality,
    string Encoder,
    string EncoderCommand);

public sealed record TextureReplacementPayloadRequest(
    string TextureImportCandidateManifestPath,
    string OutputDirectory);

public sealed record TextureReplacementPayloadManifest(
    int SchemaVersion,
    string PayloadSetId,
    DateTimeOffset CreatedAt,
    string TextureImportCandidateManifestPath,
    string CandidateId,
    string CandidateDdsPath,
    string CandidateDdsSha256,
    string NativeTextureManifestPath,
    string SourceExportId,
    string LayoutConfirmationManifestPath,
    string LayoutConfirmationId,
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
    string CrunchEncoder,
    bool MipPayloadLayoutConfirmed,
    bool AllStoragePayloadsDecode,
    bool AllQualityGatesPassed,
    bool PayloadSetReadyForWriter,
    bool PackageWasModified,
    int SourcePayloadsCopied,
    int CrunchPayloadsEncoded,
    int Lz4PayloadsEncoded,
    long SourceStoredBytes,
    long ReplacementStoredBytes,
    IReadOnlyList<TextureLayoutCheck> Checks,
    IReadOnlyList<TextureReplacementMipPayload> Mips,
    double DurationSeconds)
{
    /// <summary>Mip levels added above the source texture by an upscaled edit (Width/Height/MipCount are the new size).</summary>
    public int UpscaleLevels { get; init; }
}

public sealed record TextureReplacementPayloadResult(
    string OutputDirectory,
    string ManifestPath,
    TextureReplacementPayloadManifest Manifest,
    TextureLayoutConfirmationResult LayoutConfirmation,
    IReadOnlyList<string> OutputFiles,
    TimeSpan Duration);

public sealed class TextureReplacementPayloadRefusalException : NotSupportedException
{
    public TextureReplacementPayloadRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Converts a validated candidate DDS into one source-storage payload per UE3 mip.
/// It creates evidence only; no package bytes are changed.
/// </summary>
public sealed class TextureReplacementPayloadService
{
    // Schema 2 guarantees that every 0x80 UE3 bulk block contains an actual
    // UModel-compatible LZ4 token stream, including equal-size tiny mips.
    public const int CurrentManifestSchemaVersion = 2;
    public const string ManifestFileName = "arkive-texture-replacement-payloads.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TextureLayoutConfirmationService _layoutService;
    private readonly ICrunchEncoder _crunchEncoder;
    private readonly ICrunchDecoder _crunchDecoder;

    public TextureReplacementPayloadService(
        TextureLayoutConfirmationService layoutService,
        ICrunchEncoder crunchEncoder,
        ICrunchDecoder? crunchDecoder = null)
    {
        _layoutService = layoutService ?? throw new ArgumentNullException(nameof(layoutService));
        _crunchEncoder = crunchEncoder ?? throw new ArgumentNullException(nameof(crunchEncoder));
        _crunchDecoder = crunchDecoder ?? new NativeCrunchDecoder();
    }

    public string EncoderName => _crunchEncoder.Name;

    public static async Task<TextureReplacementPayloadManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = RequireFile(path, "TRP001", "replacement-payload manifest");
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<TextureReplacementPayloadManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new TextureReplacementPayloadRefusalException(
                           "TRP002", "The replacement-payload manifest was empty.");
        if (manifest.SchemaVersion != CurrentManifestSchemaVersion)
        {
            var migration = manifest.SchemaVersion == 1
                ? " Schema 1 may contain equal-size raw tiny mips under Lost Ark's LZ4 flag; rebuild this payload set from its import candidate."
                : string.Empty;
            Refuse(
                "TRP003",
                $"Replacement-payload schema {manifest.SchemaVersion} is unsupported; " +
                $"Arkive requires {CurrentManifestSchemaVersion}.{migration}");
        }
        return manifest;
    }

    public async Task<TextureReplacementPayloadResult> BuildAsync(
        TextureReplacementPayloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var candidateManifestPath = RequireFile(
            request.TextureImportCandidateManifestPath,
            "TRP004",
            "texture import-candidate manifest");
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            Refuse("TRP005", "The replacement-payload output directory must be empty.");
        }

        var candidate = await TextureImportCandidateService.LoadManifestAsync(
            candidateManifestPath, cancellationToken).ConfigureAwait(false);
        var layoutDirectory = Path.Combine(outputDirectory, "layout-confirmation");
        var layoutResult = await _layoutService.ConfirmAsync(
            new TextureLayoutConfirmationRequest(candidate.NativeTextureManifestPath, layoutDirectory),
            cancellationToken).ConfigureAwait(false);
        var layout = layoutResult.Manifest;
        if (!layout.MipPayloadLayoutConfirmed ||
            !layout.SourceExportId.Equals(candidate.SourceExportId, StringComparison.OrdinalIgnoreCase) ||
            layout.NativeExportIndex != candidate.NativeExportIndex)
        {
            Refuse("TRP006", "The fresh layout confirmation does not match the import candidate.");
        }

        var native = await ReadJsonAsync<NativeTextureExportManifest>(
            layout.NativeTextureManifestPath, "TRP007", cancellationToken).ConfigureAwait(false);
        var profile = NativeTextureFormats.Supported.FirstOrDefault(item =>
            item.ManifestPixelFormat.Equals(native.PixelFormat, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            Refuse("TRP008", $"No replacement encoder is registered for {native.PixelFormat}.");
        }

        var candidateDirectory = Path.GetDirectoryName(candidateManifestPath)!;
        var candidateDdsPath = ResolveWithin(
            candidateDirectory, candidate.CandidateDdsRelativePath, "TRP009");
        var candidateDds = await DdsBlockTexture.ReadAsync(candidateDdsPath, cancellationToken)
            .ConfigureAwait(false);
        var candidateDdsHash = Convert.ToHexString(SHA256.HashData(candidateDds.Bytes));
        var upscale = candidate.UpscaleLevels;
        if (!candidateDdsHash.Equals(candidate.CandidateDdsSha256, StringComparison.OrdinalIgnoreCase) ||
            upscale is < 0 or > 2 ||
            candidateDds.Width != native.Width << upscale || candidateDds.Height != native.Height << upscale ||
            candidateDds.MipCount != native.MipCount + upscale || candidateDds.FourCc != profile.DdsFourCc)
        {
            Refuse("TRP010", "The candidate DDS no longer matches its manifest/native texture layout.");
        }

        var nativeDirectory = Path.GetDirectoryName(layout.NativeTextureManifestPath)!;
        var originalDdsPath = ResolveWithin(nativeDirectory, native.DdsRelativePath, "TRP011");
        var originalDds = await DdsBlockTexture.ReadAsync(originalDdsPath, cancellationToken)
            .ConfigureAwait(false);
        var payloadDirectory = Path.Combine(outputDirectory, "replacement-mips");
        var workDirectory = Path.Combine(outputDirectory, ".encoder-work");
        Directory.CreateDirectory(payloadDirectory);
        Directory.CreateDirectory(workDirectory);

        var mipResults = new List<TextureReplacementMipPayload>(candidateDds.MipCount);
        var checks = new List<TextureLayoutCheck>
        {
            new("TRP_LAYOUT", TextureLayoutCheckStatus.Passed,
                $"Fresh layout confirmation {layout.ConfirmationId} passed for all {layout.MipCount} mips."),
            new("TRP_CANDIDATE_HASH", TextureLayoutCheckStatus.Passed,
                $"Candidate DDS still matches {candidateDdsHash}."),
            new("TRP_NO_PACKAGE_WRITE", TextureLayoutCheckStatus.Information,
                "Only isolated per-mip payloads are produced; no UPK or game file is opened for writing."),
        };
        var copied = 0;
        var crnEncoded = 0;
        var lz4Encoded = 0;
        try
        {
            for (var index = 0; index < candidateDds.MipCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Levels added by an upscale have no source mip; they reuse the top source mip's storage codec.
                var added = index < upscale;
                var sourceIndex = Math.Max(0, index - upscale);
                var source = native.Mips[sourceIndex];
                var confirmed = added
                    ? layout.Mips[0] with { Width = candidateDds.Mips[index].Width, Height = candidateDds.Mips[index].Height }
                    : layout.Mips[sourceIndex];
                var candidateMip = candidateDds.Mips[index];
                var originalMip = originalDds.Mips[sourceIndex];
                var candidateGpu = candidateDds.Bytes.AsSpan(
                    candidateMip.Offset, candidateMip.Length).ToArray();
                var originalGpu = added ? [] : originalDds.Bytes.AsSpan(
                    originalMip.Offset, originalMip.Length).ToArray();
                var candidateGpuHash = Convert.ToHexString(SHA256.HashData(candidateGpu));
                var sourceStoredPath = ResolveWithin(
                    nativeDirectory, source.StoredPayloadRelativePath, "TRP012");
                var sourceStored = await File.ReadAllBytesAsync(sourceStoredPath, cancellationToken)
                    .ConfigureAwait(false);
                var sourceStoredHash = Convert.ToHexString(SHA256.HashData(sourceStored));
                if (!sourceStoredHash.Equals(source.StoredPayloadSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Refuse("TRP013", $"Source stored mip {index} changed after native export.");
                }

                byte[] replacement;
                byte[] decodedBack;
                string relativePath;
                string action;
                string encoder;
                string command;
                var codecVerified = false;
                if (candidateGpu.SequenceEqual(originalGpu))
                {
                    replacement = sourceStored;
                    decodedBack = candidateGpu;
                    var extension = Path.GetExtension(sourceStoredPath);
                    relativePath = Path.Combine(
                        "replacement-mips", $"mip-{index:D2}-{confirmed.Width}x{confirmed.Height}{extension}");
                    action = "Copied unchanged source storage payload byte-for-byte";
                    encoder = "No encoder required";
                    command = "copy source payload";
                    codecVerified = true;
                    copied++;
                }
                else if (confirmed.StorageCodec.StartsWith("Crunch CRN", StringComparison.Ordinal))
                {
                    string encoderInput;
                    if (profile.PixelFormatValue == 24)
                    {
                        // This Unity Crunch build treats ATI2 DDS input as DXN_YX,
                        // while Lost Ark CRN is DXN_XY/A2XY. Feed decoded R/G pixels
                        // through PNG so the encoder receives unambiguous X/Y channel
                        // semantics instead of reversing the two BC4 blocks.
                        encoderInput = Path.Combine(workDirectory, $"mip-{index:D2}.png");
                        await PngRgbaCodec.WriteAsync(
                            encoderInput,
                            new RgbaImage(
                                candidateMip.Width,
                                candidateMip.Height,
                                DecodeGpu(profile, candidateMip.Width, candidateMip.Height, candidateGpu)),
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        encoderInput = Path.Combine(workDirectory, $"mip-{index:D2}.dds");
                        await DdsBlockCompressedWriter.WriteAsync(
                            encoderInput,
                            candidateMip.Width,
                            candidateMip.Height,
                            profile.DdsFormat,
                            new ReadOnlyMemory<byte>[] { candidateGpu },
                            cancellationToken).ConfigureAwait(false);
                    }
                    relativePath = Path.Combine(
                        "replacement-mips", $"mip-{index:D2}-{confirmed.Width}x{confirmed.Height}.crn");
                    var outputPath = Path.Combine(outputDirectory, relativePath);
                    var encoded = await _crunchEncoder.EncodeAsync(
                        encoderInput, outputPath, profile, cancellationToken).ConfigureAwait(false);
                    replacement = await File.ReadAllBytesAsync(outputPath, cancellationToken)
                        .ConfigureAwait(false);
                    var info = _crunchDecoder.Inspect(replacement);
                    codecVerified = info.Width == confirmed.Width && info.Height == confirmed.Height &&
                                    info.Levels == 1 && info.Faces == 1 &&
                                    info.Format == profile.CrunchFormat &&
                                    info.FourCc == profile.CrunchFourCc &&
                                    info.BytesPerBlock == profile.BytesPerBlock &&
                                    info.ActualDataSize == replacement.Length;
                    decodedBack = profile.DecodeCrunch(_crunchDecoder, replacement);
                    action = "Encoded max-quality one-level Unity CRN and decoded it back";
                    encoder = _crunchEncoder.Name;
                    command = encoded.CommandSummary;
                    crnEncoded++;
                }
                else if (confirmed.StorageCodec.Contains("LZ4", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Ue3BulkMipDecoder.DecodeLostArkLz4(
                        sourceStored, source.DecodedGpuBytes, out var sourceInfo);
                    replacement = Ue3BulkMipEncoder.EncodeLostArkLz4(
                        candidateGpu, sourceInfo.BlockSize);
                    decodedBack = Ue3BulkMipDecoder.DecodeLostArkLz4(
                        replacement, candidateGpu.Length, out var outputInfo);
                    codecVerified = outputInfo.BlockSize == sourceInfo.BlockSize &&
                                    outputInfo.UncompressedBytes == candidateGpu.Length &&
                                    outputInfo.BlockCount > 0;
                    relativePath = Path.Combine(
                        "replacement-mips", $"mip-{index:D2}-{confirmed.Width}x{confirmed.Height}.ue3bulk");
                    action = "Encoded UE3 compressed-bulk wrapper with deterministic LZ4 blocks";
                    encoder = "Arkive managed UE3/LZ4 encoder";
                    command = $"UE3 bulk blockSize={sourceInfo.BlockSize}";
                    lz4Encoded++;
                }
                else
                {
                    Refuse("TRP014", $"Mip {index} source codec is unsupported: {confirmed.StorageCodec}");
                    throw new UnreachableException();
                }

                var outputPayloadPath = Path.Combine(outputDirectory, relativePath);
                if (!File.Exists(outputPayloadPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPayloadPath)!);
                    await File.WriteAllBytesAsync(
                        outputPayloadPath, replacement, cancellationToken).ConfigureAwait(false);
                }
                var decodedHash = Convert.ToHexString(SHA256.HashData(decodedBack));
                var metrics = CompareGpuPayloads(
                    profile, candidateMip.Width, candidateMip.Height, candidateGpu, decodedBack);
                var decodeVerified = codecVerified && decodedBack.Length == candidateGpu.Length;
                if (!decodeVerified)
                {
                    Refuse("TRP015", $"Mip {index} replacement payload failed codec decode-back.");
                }
                mipResults.Add(new TextureReplacementMipPayload(
                    index,
                    confirmed.Width,
                    confirmed.Height,
                    confirmed.BulkDataFlags,
                    confirmed.StorageCodec,
                    added ? 0 : sourceStored.Length,
                    added ? "" : sourceStoredHash,
                    action,
                    relativePath,
                    replacement.Length,
                    Convert.ToHexString(SHA256.HashData(replacement)),
                    candidateGpu.Length,
                    candidateGpuHash,
                    decodedHash,
                    codecVerified,
                    decodeVerified,
                    metrics,
                    encoder,
                    command));
            }
        }
        finally
        {
            if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, recursive: true);
        }

        var allDecode = mipResults.Count == candidateDds.MipCount &&
                        mipResults.All(mip => mip.CodecMetadataVerified && mip.DecodeBackVerified);
        var allQuality = mipResults.All(mip => mip.Quality.QualityGatePassed);
        checks.Add(new TextureLayoutCheck(
            "TRP_DECODE_BACK",
            allDecode ? TextureLayoutCheckStatus.Passed : TextureLayoutCheckStatus.Warning,
            allDecode
                ? "Every stored replacement payload decoded with the expected codec, format, dimensions and byte count."
                : "At least one stored replacement payload did not pass structural decode-back."));
        checks.Add(new TextureLayoutCheck(
            "TRP_QUALITY",
            allQuality ? TextureLayoutCheckStatus.Passed : TextureLayoutCheckStatus.Warning,
            allQuality
                ? "Every lossy CRN level passed Arkive's decoded-pixel/normal quality gate; exact LZ4 levels matched byte-for-byte."
                : "At least one lossy CRN level fell below Arkive's conservative decoded quality gate."));

        watch.Stop();
        var manifest = new TextureReplacementPayloadManifest(
            CurrentManifestSchemaVersion,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            candidateManifestPath,
            candidate.CandidateId,
            candidateDdsPath,
            candidateDdsHash,
            layout.NativeTextureManifestPath,
            native.ExportId,
            layoutResult.ManifestPath,
            layout.ConfirmationId,
            native.SourcePackagePath,
            layout.SourcePackageSha256,
            native.PackageAlias,
            native.NativeExportIndex!.Value,
            native.ClassName,
            native.ObjectName,
            native.PixelFormat,
            candidateDds.Width,
            candidateDds.Height,
            candidateDds.MipCount,
            _crunchEncoder.Name,
            layout.MipPayloadLayoutConfirmed,
            allDecode,
            allQuality,
            PayloadSetReadyForWriter: layout.MipPayloadLayoutConfirmed && allDecode && allQuality,
            PackageWasModified: false,
            copied,
            crnEncoded,
            lz4Encoded,
            mipResults.Sum(mip => (long)mip.SourceStoredBytes),
            mipResults.Sum(mip => (long)mip.ReplacementStoredBytes),
            checks,
            mipResults,
            watch.Elapsed.TotalSeconds)
        {
            UpscaleLevels = upscale,
        };
        var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        var outputFiles = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new TextureReplacementPayloadResult(
            outputDirectory, manifestPath, manifest, layoutResult, outputFiles, watch.Elapsed);
    }

    private static TexturePayloadQualityMetrics CompareGpuPayloads(
        NativeTextureFormatProfile profile,
        int width,
        int height,
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> actual)
    {
        if (expected.Length != actual.Length)
        {
            return new TexturePayloadQualityMetrics(
                false, 255d, 255d, 0, 255,
                width * (long)height, null, null, false);
        }
        if (expected.SequenceEqual(actual))
        {
            return new TexturePayloadQualityMetrics(
                true, 0, 0, 999d, 0, 0, 0, 0, true);
        }

        var expectedPixels = DecodeGpu(profile, width, height, expected);
        var actualPixels = DecodeGpu(profile, width, height, actual);
        var channels = profile.PixelFormatValue == 24 ? 2 : 4;
        long absolute = 0;
        double squared = 0;
        var maximum = 0;
        long pixelsAbove16 = 0;
        double angleTotal = 0;
        double angleMaximum = 0;
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var offset = pixel * 4;
            var above = false;
            for (var channel = 0; channel < channels; channel++)
            {
                var difference = Math.Abs(expectedPixels[offset + channel] - actualPixels[offset + channel]);
                absolute += difference;
                squared += difference * difference;
                maximum = Math.Max(maximum, difference);
                above |= difference > 16;
            }
            if (above) pixelsAbove16++;
            if (profile.PixelFormatValue == 24)
            {
                var expectedNormal = Normal(expectedPixels, offset);
                var actualNormal = Normal(actualPixels, offset);
                var dot = Math.Clamp(
                    expectedNormal.X * actualNormal.X + expectedNormal.Y * actualNormal.Y +
                    expectedNormal.Z * actualNormal.Z, -1d, 1d);
                var angle = Math.Acos(dot) * 180d / Math.PI;
                angleTotal += angle;
                angleMaximum = Math.Max(angleMaximum, angle);
            }
        }
        var samples = Math.Max(1L, width * (long)height * channels);
        var mae = absolute / (double)samples;
        var rmse = Math.Sqrt(squared / samples);
        // JSON has no representation for infinity. A non-identical BC payload can
        // still decode to pixel-identical output; use the same explicit finite
        // perfect-quality sentinel as the byte-identical path above.
        var psnr = rmse == 0 ? 999d : 20d * Math.Log10(255d / rmse);
        var meanAngle = profile.PixelFormatValue == 24
            ? angleTotal / Math.Max(1, width * height)
            : (double?)null;
        var maxAngle = profile.PixelFormatValue == 24 ? angleMaximum : (double?)null;
        var qualityPassed = width <= 8 ||
                            (profile.PixelFormatValue == 24
                                // A single BC5 endpoint outlier can exceed 45 degrees at
                                // sharp UV/island boundaries even when the map remains
                                // excellent overall. Retain the strict eight-degree mean
                                // gate while allowing bounded isolated errors up to 60.
                                ? meanAngle <= 8d && angleMaximum <= 60d
                                : psnr >= 24d);
        return new TexturePayloadQualityMetrics(
            false, mae, rmse, psnr, maximum, pixelsAbove16,
            meanAngle, maxAngle, qualityPassed);
    }

    private static byte[] DecodeGpu(
        NativeTextureFormatProfile profile,
        int width,
        int height,
        ReadOnlySpan<byte> blocks)
    {
        var output = new byte[checked(width * height * 4)];
        if (profile.IsUncompressed)
        {
            BgraPixels.ToRgba(blocks[..output.Length], output);
            return output;
        }
        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        Span<byte> pixels = stackalloc byte[64];
        for (var blockY = 0; blockY < blocksHigh; blockY++)
        {
            for (var blockX = 0; blockX < blocksWide; blockX++)
            {
                var blockIndex = blockY * blocksWide + blockX;
                var block = blocks.Slice(blockIndex * profile.BytesPerBlock, profile.BytesPerBlock);
                switch (profile.PixelFormatValue)
                {
                    case 5: BlockCompression.DecodeBc1(block, pixels, allowTransparency: true); break;
                    case 7: BlockCompression.DecodeBc3(block, pixels); break;
                    case 24: BlockCompression.DecodeBc5(block, pixels); break;
                    default: throw new NotSupportedException();
                }
                for (var y = 0; y < 4; y++)
                {
                    var destinationY = blockY * 4 + y;
                    if (destinationY >= height) break;
                    for (var x = 0; x < 4; x++)
                    {
                        var destinationX = blockX * 4 + x;
                        if (destinationX >= width) break;
                        pixels.Slice((y * 4 + x) * 4, 4).CopyTo(
                            output.AsSpan((destinationY * width + destinationX) * 4, 4));
                    }
                }
            }
        }
        return output;
    }

    private static (double X, double Y, double Z) Normal(byte[] pixels, int offset)
    {
        var x = pixels[offset] / 127.5d - 1d;
        var y = pixels[offset + 1] / 127.5d - 1d;
        var z = Math.Sqrt(Math.Max(0d, 1d - x * x - y * y));
        var length = Math.Sqrt(x * x + y * y + z * z);
        return length <= double.Epsilon ? (0, 0, 1) : (x / length, y / length, z / length);
    }

    private static async Task<T> ReadJsonAsync<T>(
        string path,
        string code,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                       .ConfigureAwait(false) ?? throw new JsonException("The document was empty.");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new TextureReplacementPayloadRefusalException(
                code, $"{Path.GetFileName(path)} could not be read: {exception.Message}");
        }
    }

    private static string ResolveWithin(string directory, string relativePath, string code)
    {
        var path = Path.GetFullPath(Path.Combine(directory, relativePath));
        var prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
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

    private static async Task SaveJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
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
        throw new TextureReplacementPayloadRefusalException(code, message);
}
