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

public enum TextureLayoutCheckStatus
{
    Passed,
    Information,
    Warning,
}

public sealed record TextureLayoutCheck(
    string Code,
    TextureLayoutCheckStatus Status,
    string Message);

public sealed record TextureMipLayoutConfirmation(
    int MipIndex,
    int Width,
    int Height,
    long HeaderRelativeOffset,
    long PayloadRelativeOffset,
    long PayloadLogicalOffset,
    uint BulkDataFlags,
    int ElementCount,
    int StoredBytes,
    string StorageCodec,
    string StoredSha256,
    int DecodedGpuBytes,
    string DecodedGpuSha256,
    string DdsMipSha256,
    bool DescriptorMatches,
    bool CodecMetadataMatches,
    bool DecodeMatchesExportedDds);

public sealed record TextureLayoutConfirmationRequest(
    string NativeTextureManifestPath,
    string OutputDirectory);

public sealed record TextureLayoutConfirmationManifest(
    int SchemaVersion,
    string ConfirmationId,
    DateTimeOffset CreatedAt,
    string NativeTextureManifestPath,
    string SourceExportId,
    string ReconnaissanceJsonPath,
    string SourcePackagePath,
    string SourcePackageSha256,
    string PackageAlias,
    int SourceExportIndex,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    string PixelFormat,
    int Width,
    int Height,
    int MipCount,
    long SerialOffset,
    long SerialSize,
    long PropertyStreamRelativeOffset,
    long NativeDataRelativeOffset,
    long MipArrayRelativeOffset,
    long MipArrayEndRelativeOffset,
    int OpaqueNativePrefixBytes,
    string OpaqueNativePrefixSha256,
    int OpaqueNativeSuffixBytes,
    string OpaqueNativeSuffixSha256,
    bool MipPayloadLayoutConfirmed,
    bool PackageRewriteConfirmed,
    string Scope,
    IReadOnlyList<TextureLayoutCheck> Checks,
    IReadOnlyList<TextureMipLayoutConfirmation> Mips,
    double DurationSeconds);

public sealed record TextureLayoutConfirmationResult(
    string OutputDirectory,
    string ManifestPath,
    TextureLayoutConfirmationManifest Manifest,
    IReadOnlyList<string> OutputFiles,
    TimeSpan Duration);

public sealed class TextureLayoutConfirmationRefusalException : NotSupportedException
{
    public TextureLayoutConfirmationRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Promotes heuristic reconnaissance to a replacement-safe mip-payload layout by
/// independently replaying descriptor parsing, codec metadata validation and
/// decode-to-DDS hash comparisons against the unchanged source package.
/// </summary>
public sealed class TextureLayoutConfirmationService
{
    public const int CurrentManifestSchemaVersion = 1;
    public const string ManifestFileName = "arkive-texture-layout-confirmation.json";

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

    public TextureLayoutConfirmationService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null,
        ICrunchDecoder? crunchDecoder = null)
    {
        _summaryReader = summaryReader ?? new UpkPackageReader();
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(_summaryReader, _protectionProvider);
        _crunchDecoder = crunchDecoder ?? new NativeCrunchDecoder();
    }

    public static async Task<TextureLayoutConfirmationManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = RequireFile(path, "TLC001", "layout-confirmation manifest");
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<TextureLayoutConfirmationManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new TextureLayoutConfirmationRefusalException(
                           "TLC002", "The layout-confirmation manifest was empty.");
        if (manifest.SchemaVersion != CurrentManifestSchemaVersion)
        {
            Refuse(
                "TLC003",
                $"Layout-confirmation schema {manifest.SchemaVersion} is unsupported; " +
                $"Arkive requires {CurrentManifestSchemaVersion}.");
        }
        return manifest;
    }

    public async Task<TextureLayoutConfirmationResult> ConfirmAsync(
        TextureLayoutConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var nativeManifestPath = RequireFile(
            request.NativeTextureManifestPath, "TLC004", "native texture manifest");
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            Refuse("TLC005", "The layout-confirmation output directory must be empty.");
        }

        var native = await ReadJsonAsync<NativeTextureExportManifest>(
            nativeManifestPath, "TLC006", cancellationToken).ConfigureAwait(false);
        if (native.SchemaVersion < TextureImportCandidateService.MinimumNativeTextureManifestSchemaVersion ||
            native.SchemaVersion > NativeTextureExportService.CurrentManifestSchemaVersion)
        {
            Refuse("TLC007", $"Native texture manifest schema {native.SchemaVersion} is unsupported.");
        }
        if (!native.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) ||
            native.NativeExportIndex is null)
        {
            Refuse("TLC008", "An exact Texture2D with a resolved native export index is required.");
        }

        var reconPath = RequireFile(
            native.ReconnaissanceJsonPath, "TLC009", "texture reconnaissance report");
        var recon = await ReadJsonAsync<TextureReconnaissanceReport>(
            reconPath, "TLC010", cancellationToken).ConfigureAwait(false);
        ValidateIdentity(native, recon);
        if (recon.MipCandidates.Count != 1 || recon.MipCandidates[0].Mips.Count != native.MipCount)
        {
            Refuse("TLC011", "Exactly one complete reconnaissance mip chain is required.");
        }
        if (!NativeTextureFormats.TryResolve(recon, out var format) ||
            !format.ManifestPixelFormat.Equals(native.PixelFormat, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("TLC012", "The reconnaissance and native export texture formats disagree.");
        }

        var packagePath = RequireFile(native.SourcePackagePath, "TLC013", "source package");
        var package = new FileInfo(packagePath);
        if (package.Length != native.SourcePackageLength ||
            package.LastWriteTimeUtc.Ticks != native.SourcePackageLastWriteUtcTicks)
        {
            Refuse("TLC014", "The source package metadata changed after native export.");
        }
        var packageHash = await HashFileAsync(packagePath, cancellationToken).ConfigureAwait(false);
        if (!packageHash.Equals(native.SourcePackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("TLC015", "The source package SHA-256 changed after native export.");
        }

        var catalog = _catalogReader.Read(packagePath);
        var asset = catalog.Assets.SingleOrDefault(item =>
            item.ExportIndex == native.NativeExportIndex.Value &&
            item.SerialOffset == native.SerialOffset &&
            item.SerialSize == native.SerialSize &&
            item.ObjectName.Equals(native.ObjectName, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            Refuse("TLC016", "The native export table no longer resolves to the recorded texture range.");
        }

        var summary = _summaryReader.Read(packagePath);
        using var logical = new LogicalPackageStream(summary, _protectionProvider);
        if (native.SerialOffset < 0 || native.SerialSize <= 0 ||
            native.SerialOffset > logical.Length - native.SerialSize || native.SerialSize > int.MaxValue)
        {
            Refuse("TLC017", "The recorded logical export range is outside the current package.");
        }
        var exportBytes = new byte[checked((int)native.SerialSize)];
        logical.Position = native.SerialOffset;
        logical.ReadExactly(exportBytes);
        var exportHash = Convert.ToHexString(SHA256.HashData(exportBytes));
        if (!exportHash.Equals(native.SourcePayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            !exportHash.Equals(recon.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("TLC018", "The logical Texture2D export payload no longer matches both source reports.");
        }

        var nativeDirectory = Path.GetDirectoryName(nativeManifestPath)!;
        var ddsPath = ResolveWithin(nativeDirectory, native.DdsRelativePath, "TLC019");
        var dds = await DdsBlockTexture.ReadAsync(ddsPath, cancellationToken).ConfigureAwait(false);
        var ddsHash = Convert.ToHexString(SHA256.HashData(dds.Bytes));
        if (!ddsHash.Equals(native.DdsSha256, StringComparison.OrdinalIgnoreCase) ||
            dds.Width != native.Width || dds.Height != native.Height ||
            dds.MipCount != native.MipCount)
        {
            Refuse("TLC020", "The authoritative DDS no longer matches its native export manifest.");
        }

        var checks = new List<TextureLayoutCheck>
        {
            new("TLC_PACKAGE_HASH", TextureLayoutCheckStatus.Passed,
                $"Source package still matches {packageHash}."),
            new("TLC_EXPORT_IDENTITY", TextureLayoutCheckStatus.Passed,
                $"Native UE export {native.NativeExportIndex} still resolves uniquely to " +
                $"0x{native.SerialOffset:X}+0x{native.SerialSize:X}."),
            new("TLC_EXPORT_HASH", TextureLayoutCheckStatus.Passed,
                $"Logical Texture2D export still matches {exportHash}."),
            new("TLC_DDS_HASH", TextureLayoutCheckStatus.Passed,
                $"Authoritative {native.PixelFormat} DDS still matches {ddsHash}."),
        };

        var candidate = recon.MipCandidates[0];
        var arrayOffset = checked((int)candidate.ArrayRelativeOffset);
        if (arrayOffset < native.NativeDataRelativeOffset || arrayOffset > exportBytes.Length - 4 ||
            BinaryPrimitives.ReadInt32LittleEndian(exportBytes.AsSpan(arrayOffset)) != native.MipCount)
        {
            Refuse("TLC021", "The native mip array count or boundary is not reproducible.");
        }
        var prefix = exportBytes.AsSpan(
            checked((int)native.NativeDataRelativeOffset),
            arrayOffset - checked((int)native.NativeDataRelativeOffset));
        var cursor = arrayOffset + 4;
        var mipResults = new List<TextureMipLayoutConfirmation>(native.MipCount);
        for (var index = 0; index < native.MipCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = candidate.Mips[index];
            var exported = native.Mips[index];
            if (cursor > exportBytes.Length - 24 || evidence.HeaderRelativeOffset != cursor)
            {
                Refuse("TLC022", $"Mip {index} header is not contiguous with the verified array.");
            }
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(exportBytes.AsSpan(cursor));
            var elementCount = BinaryPrimitives.ReadInt32LittleEndian(exportBytes.AsSpan(cursor + 4));
            var storedSize = BinaryPrimitives.ReadInt32LittleEndian(exportBytes.AsSpan(cursor + 8));
            var bulkOffset = BinaryPrimitives.ReadInt32LittleEndian(exportBytes.AsSpan(cursor + 12));
            var payloadOffset = cursor + 16;
            if (storedSize <= 0 || payloadOffset > exportBytes.Length - storedSize - 8)
            {
                Refuse("TLC023", $"Mip {index} payload is unbounded.");
            }
            var width = BinaryPrimitives.ReadInt32LittleEndian(exportBytes.AsSpan(payloadOffset + storedSize));
            var height = BinaryPrimitives.ReadInt32LittleEndian(exportBytes.AsSpan(payloadOffset + storedSize + 4));
            var payloadLogical = native.SerialOffset + payloadOffset;
            var descriptorMatches =
                evidence.MipIndex == index && evidence.HeaderRelativeOffset == cursor &&
                evidence.PayloadRelativeOffset == payloadOffset &&
                evidence.PayloadLogicalOffset == payloadLogical && evidence.BulkOffsetMatchesPayload &&
                flags == evidence.BulkDataFlags && elementCount == evidence.ElementCount &&
                storedSize == evidence.StoredSize && bulkOffset == payloadLogical &&
                width == evidence.Width && height == evidence.Height &&
                exported.MipIndex == index && exported.SourcePayloadLogicalOffset == payloadLogical &&
                exported.BulkDataFlags == flags && exported.StoredPayloadBytes == storedSize;
            if (!descriptorMatches)
            {
                Refuse("TLC024", $"Mip {index} descriptor fields disagree across package, recon and export.");
            }

            var stored = exportBytes.AsSpan(payloadOffset, storedSize).ToArray();
            var storedHash = Convert.ToHexString(SHA256.HashData(stored));
            if (!storedHash.Equals(evidence.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
                !storedHash.Equals(exported.StoredPayloadSha256, StringComparison.OrdinalIgnoreCase))
            {
                Refuse("TLC025", $"Mip {index} stored payload hash changed.");
            }

            var expectedGpuBytes = format.CalculateMipBytes(width, height);
            byte[] decoded;
            string codec;
            var codecMatches = false;
            if (stored.Length >= 2 && stored[0] == (byte)'H' && stored[1] == (byte)'x')
            {
                var info = _crunchDecoder.Inspect(stored);
                codecMatches = flags == 0x200 && elementCount == storedSize &&
                    info.Width == width && info.Height == height && info.Levels == 1 &&
                    info.Faces == 1 && info.Format == format.CrunchFormat &&
                    info.FourCc == format.CrunchFourCc && info.BytesPerBlock == format.BytesPerBlock &&
                    info.ActualDataSize == storedSize;
                decoded = format.DecodeCrunch(_crunchDecoder, stored);
                codec = $"Crunch CRN / {format.CrunchFourCc}";
            }
            else if (flags == Ue3BulkMipDecoder.LostArkCompressedBulkFlag)
            {
                decoded = Ue3BulkMipDecoder.DecodeLostArkLz4(stored, expectedGpuBytes, out var info);
                codecMatches = elementCount == expectedGpuBytes && info.UncompressedBytes == expectedGpuBytes &&
                               info.CompressedBytes > 0 && info.BlockCount > 0;
                codec = info.Codec;
            }
            else
            {
                Refuse("TLC026", $"Mip {index} has an unsupported source storage codec.");
                throw new UnreachableException();
            }
            if (!codecMatches || decoded.Length != expectedGpuBytes)
            {
                Refuse("TLC027", $"Mip {index} codec metadata does not prove its GPU payload layout.");
            }

            var decodedHash = Convert.ToHexString(SHA256.HashData(decoded));
            var ddsMip = dds.Mips[index];
            var ddsMipHash = Convert.ToHexString(SHA256.HashData(
                dds.Bytes.AsSpan(ddsMip.Offset, ddsMip.Length)));
            var decodeMatches = decodedHash.Equals(exported.DecodedGpuSha256, StringComparison.OrdinalIgnoreCase) &&
                                decodedHash.Equals(ddsMipHash, StringComparison.OrdinalIgnoreCase) &&
                                ddsMip.Length == decoded.Length && exported.DdsPayloadOffset == ddsMip.Offset;
            if (!decodeMatches)
            {
                Refuse("TLC028", $"Mip {index} decode does not match the authoritative DDS level.");
            }

            mipResults.Add(new TextureMipLayoutConfirmation(
                index, width, height, cursor, payloadOffset, payloadLogical, flags,
                elementCount, storedSize, codec, storedHash, decoded.Length, decodedHash,
                ddsMipHash, descriptorMatches, codecMatches, decodeMatches));
            cursor = checked(payloadOffset + storedSize + 8);
        }

        if (cursor > exportBytes.Length)
        {
            Refuse("TLC029", "The confirmed mip array extends beyond the Texture2D export.");
        }
        var suffix = exportBytes.AsSpan(cursor);
        var prefixHash = Convert.ToHexString(SHA256.HashData(prefix));
        var suffixHash = Convert.ToHexString(SHA256.HashData(suffix));
        checks.Add(new TextureLayoutCheck(
            "TLC_MIP_ARRAY_COVERAGE", TextureLayoutCheckStatus.Passed,
            $"The {native.MipCount}-mip array is contiguous from +0x{arrayOffset:X} through +0x{cursor:X}; " +
            "every descriptor, stored hash, codec header and decoded DDS level agrees."));
        checks.Add(new TextureLayoutCheck(
            "TLC_OPAQUE_RANGES", TextureLayoutCheckStatus.Passed,
            $"Arkive bounded and hashed {prefix.Length:N0} opaque native-prefix byte(s) and " +
            $"{suffix.Length:N0} opaque suffix byte(s); a writer must preserve them exactly."));
        checks.Add(new TextureLayoutCheck(
            "TLC_SCOPE", TextureLayoutCheckStatus.Information,
            "Mip-payload replacement layout is confirmed. This does not yet confirm package export-table, " +
            "logical-package compression, or final UPK rewriting."));

        watch.Stop();
        var manifest = new TextureLayoutConfirmationManifest(
            CurrentManifestSchemaVersion,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            nativeManifestPath,
            native.ExportId,
            reconPath,
            packagePath,
            packageHash,
            native.PackageAlias,
            native.SourceExportIndex,
            native.NativeExportIndex.Value,
            native.ClassName,
            native.ObjectName,
            native.PixelFormat,
            native.Width,
            native.Height,
            native.MipCount,
            native.SerialOffset,
            native.SerialSize,
            native.PropertyStreamRelativeOffset,
            native.NativeDataRelativeOffset,
            arrayOffset,
            cursor,
            prefix.Length,
            prefixHash,
            suffix.Length,
            suffixHash,
            MipPayloadLayoutConfirmed: true,
            PackageRewriteConfirmed: false,
            Scope: "Texture2D inline mip descriptors and payloads only",
            checks,
            mipResults,
            watch.Elapsed.TotalSeconds);
        var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return new TextureLayoutConfirmationResult(
            outputDirectory,
            manifestPath,
            manifest,
            [manifestPath],
            watch.Elapsed);
    }

    private static void ValidateIdentity(
        NativeTextureExportManifest native,
        TextureReconnaissanceReport recon)
    {
        if (recon.SchemaVersion != TextureReconnaissanceService.CurrentSchemaVersion ||
            !Path.GetFullPath(recon.PackagePath).Equals(
                Path.GetFullPath(native.SourcePackagePath), StringComparison.OrdinalIgnoreCase) ||
            recon.PackageAlias != native.PackageAlias || recon.ObjectName != native.ObjectName ||
            recon.ClassName != native.ClassName || recon.SerialOffset != native.SerialOffset ||
            recon.SerialSize != native.SerialSize || recon.NativeExportIndex != native.NativeExportIndex ||
            recon.PropertyStreamRelativeOffset != native.PropertyStreamRelativeOffset ||
            recon.NativeDataRelativeOffset != native.NativeDataRelativeOffset)
        {
            Refuse("TLC030", "Reconnaissance identity/boundaries disagree with the native export manifest.");
        }
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
            throw new TextureLayoutConfirmationRefusalException(
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

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
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
        throw new TextureLayoutConfirmationRefusalException(code, message);
}
