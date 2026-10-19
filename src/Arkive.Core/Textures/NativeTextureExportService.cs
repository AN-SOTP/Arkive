using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Inspection;
using Arkive.Core.Packages;

namespace Arkive.Core.Textures;

public sealed class NativeTextureExportService
{
    public const int CurrentManifestSchemaVersion = 5;
    public const string ManifestFileName = "arkive-native-texture.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly UpkPackageReader _summaryReader;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly ICrunchDecoder _crunchDecoder;
    private readonly ExportArtifactInspector _artifactInspector = new();

    public NativeTextureExportService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null,
        ICrunchDecoder? crunchDecoder = null)
    {
        _summaryReader = summaryReader ?? new UpkPackageReader();
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _crunchDecoder = crunchDecoder ?? new NativeCrunchDecoder();
    }

    public string DecoderName => $"{_crunchDecoder.Name}; managed UE3/LZ4 bulk decoder";

    public static async Task<NativeTextureExportManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The native texture manifest was not found.", fullPath);
        }
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<NativeTextureExportManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new InvalidDataException("The native texture manifest was empty.");
        if (manifest.SchemaVersion != CurrentManifestSchemaVersion)
        {
            throw new NotSupportedException(
                $"Native texture manifest schema {manifest.SchemaVersion} is unsupported; " +
                $"Arkive requires {CurrentManifestSchemaVersion}.");
        }
        return manifest;
    }

    public static bool TryValidateReconnaissanceGate(
        TextureReconnaissanceReport report,
        out NativeTextureFormatProfile? profile,
        out string detail)
    {
        try
        {
            profile = ValidateReportGate(report);
            detail = $"Exporter reconnaissance gate satisfied: {profile.ManifestPixelFormat}.";
            return true;
        }
        catch (NativeTextureExportRefusalException exception)
        {
            profile = null;
            detail = exception.Message;
            return false;
        }
    }

    public Task<NativeTextureExportResult> ExportAsync(
        NativeTextureExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExportCoreAsync(request, cancellationToken);
    }

    private async Task<NativeTextureExportResult> ExportCoreAsync(
        NativeTextureExportRequest request,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var report = request.Reconnaissance;
        var formatProfile = ValidateReportGate(report);

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            Refuse("NTX002", "The native texture export directory must be empty.");
        }

        var source = new FileInfo(report.PackagePath);
        if (!source.Exists)
        {
            throw new FileNotFoundException("The source package no longer exists.", source.FullName);
        }

        if (source.Length != report.PackageLength ||
            source.LastWriteTimeUtc.Ticks != report.PackageLastWriteUtcTicks)
        {
            Refuse("NTX003", "The source package metadata changed after reconnaissance; run it again.");
        }

        var summary = _summaryReader.Read(source.FullName);
        using var logical = new LogicalPackageStream(summary, _protectionProvider);
        if (report.SerialOffset < 0 || report.SerialSize <= 0 ||
            report.SerialOffset > logical.Length - report.SerialSize ||
            report.SerialSize > int.MaxValue)
        {
            Refuse("NTX004", "The reconnaissance export range is outside the current logical package.");
        }

        var exportBytes = new byte[checked((int)report.SerialSize)];
        logical.Position = report.SerialOffset;
        logical.ReadExactly(exportBytes);
        var exportHash = Convert.ToHexString(SHA256.HashData(exportBytes));
        if (!exportHash.Equals(report.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("NTX005", "The logical export payload no longer matches its reconnaissance hash.");
        }

        var candidate = report.MipCandidates[0];
        var width = RequiredIntProperty(report, "SizeX");
        var height = RequiredIntProperty(report, "SizeY");
        var originalWidth = RequiredIntProperty(report, "OriginalSizeX");
        var originalHeight = RequiredIntProperty(report, "OriginalSizeY");
        if (candidate.Mips[0].Width != width || candidate.Mips[0].Height != height)
        {
            Refuse(
                "NTX006",
                "The top mip dimensions do not match the verified SizeX/SizeY properties.");
        }

        var rawDirectory = Path.Combine(outputDirectory, "source-mips");
        Directory.CreateDirectory(rawDirectory);
        var decodedMips = new List<ReadOnlyMemory<byte>>(candidate.Mips.Count);
        var mipExports = new List<NativeTextureMipExport>(candidate.Mips.Count);
        long ddsPayloadOffset = DdsBlockCompressedWriter.HeaderBytes;
        for (var index = 0; index < candidate.Mips.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mip = candidate.Mips[index];
            ValidateMipGate(mip, index, width, height, exportBytes.Length, formatProfile.IsUncompressed ? 1 : 4);
            var payloadOffset = checked((int)mip.PayloadRelativeOffset!.Value);
            var storedData = exportBytes.AsSpan(payloadOffset, mip.StoredSize).ToArray();
            var storedHash = Convert.ToHexString(SHA256.HashData(storedData));
            if (!storedHash.Equals(mip.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            {
                Refuse("NTX007", $"Stored mip {index} does not match its reconnaissance hash.");
            }

            var expectedDecodedSize = formatProfile.CalculateMipBytes(
                Math.Max(1, width >> index),
                Math.Max(1, height >> index));
            byte[] decoded;
            string storageCodec;
            string rawExtension;
            int storageHeaderBytes;
            if (storedData.Length >= 2 && storedData[0] == (byte)'H' && storedData[1] == (byte)'x')
            {
                CrunchTextureInfo crnInfo;
                try
                {
                    crnInfo = _crunchDecoder.Inspect(storedData);
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidDataException or NotSupportedException)
                {
                    throw new InvalidDataException(
                        $"Mip {index} Crunch inspection failed: dimensions={mip.Width}x{mip.Height}, " +
                        $"flags=0x{mip.BulkDataFlags:X8}, storedBytes={storedData.Length:N0}, " +
                        $"storedSha256={storedHash}, prefix=" +
                        Convert.ToHexString(storedData.AsSpan(0, Math.Min(16, storedData.Length))) + ".",
                        exception);
                }
                if (crnInfo.Width != mip.Width || crnInfo.Height != mip.Height ||
                    crnInfo.Levels != 1 || crnInfo.Faces != 1 ||
                    crnInfo.Format != formatProfile.CrunchFormat ||
                    crnInfo.FourCc != formatProfile.CrunchFourCc ||
                    crnInfo.BytesPerBlock != formatProfile.BytesPerBlock ||
                    crnInfo.ActualDataSize != storedData.Length ||
                    crnInfo.HeaderSize <= 0 || crnInfo.HeaderSize > storedData.Length)
                {
                    Refuse(
                        "NTX008",
                        $"Mip {index} CRN metadata disagrees with the verified {formatProfile.CrunchFourCc} layout: " +
                        $"{crnInfo.Width}x{crnInfo.Height}, levels={crnInfo.Levels}, faces={crnInfo.Faces}, " +
                        $"format={crnInfo.Format}/{crnInfo.FourCc}, data={crnInfo.ActualDataSize}.");
                }

                try
                {
                    decoded = formatProfile.DecodeCrunch(_crunchDecoder, storedData);
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidDataException or NotSupportedException)
                {
                    throw new InvalidDataException(
                        $"Mip {index} Crunch {formatProfile.CrunchFourCc} transcoding failed after metadata validation: " +
                        $"dimensions={mip.Width}x{mip.Height}, storedSha256={storedHash}.",
                        exception);
                }
                storageCodec = $"Crunch CRN / {formatProfile.CrunchFourCc}";
                rawExtension = ".crn";
                storageHeaderBytes = crnInfo.HeaderSize;
            }
            else if (mip.BulkDataFlags == Ue3BulkMipDecoder.LostArkCompressedBulkFlag)
            {
                if (mip.ElementCount != expectedDecodedSize)
                {
                    Refuse(
                        "NTX020",
                        $"Mip {index} compressed-bulk element count is {mip.ElementCount:N0}; " +
                        $"{formatProfile.GpuFormatName} requires {expectedDecodedSize:N0} bytes.");
                }

                Ue3BulkDecodeInfo bulkInfo;
                try
                {
                    decoded = Ue3BulkMipDecoder.DecodeLostArkLz4(
                        storedData,
                        expectedDecodedSize,
                        out bulkInfo);
                }
                catch (InvalidDataException exception)
                {
                    throw new InvalidDataException(
                        $"Mip {index} Lost Ark UE3/LZ4 bulk decoding failed: " +
                        $"dimensions={mip.Width}x{mip.Height}, flags=0x{mip.BulkDataFlags:X8}, " +
                        $"storedBytes={storedData.Length:N0}, expectedGpuBytes={expectedDecodedSize:N0}, " +
                        $"storedSha256={storedHash}, prefix=" +
                        Convert.ToHexString(storedData.AsSpan(0, Math.Min(24, storedData.Length))) + ".",
                        exception);
                }
                storageCodec = bulkInfo.Codec;
                rawExtension = ".ue3bulk";
                storageHeaderBytes = bulkInfo.HeaderBytes;
            }
            else
            {
                Refuse(
                    "NTX021",
                    $"Mip {index} uses unsupported stored data: flags=0x{mip.BulkDataFlags:X8}, " +
                    $"prefix={Convert.ToHexString(storedData.AsSpan(0, Math.Min(8, storedData.Length)))}.");
                throw new UnreachableException();
            }

            if (decoded.Length != expectedDecodedSize)
            {
                Refuse(
                    "NTX009",
                    $"Decoded mip {index} has {decoded.Length:N0} bytes; the {formatProfile.GpuFormatName} DDS level requires " +
                    $"{expectedDecodedSize:N0}.");
            }

            var rawFileName = $"mip-{index:D2}-{mip.Width}x{mip.Height}{rawExtension}";
            var rawPath = Path.Combine(rawDirectory, rawFileName);
            await File.WriteAllBytesAsync(rawPath, storedData, cancellationToken).ConfigureAwait(false);
            var decodedHash = Convert.ToHexString(SHA256.HashData(decoded));
            decodedMips.Add(decoded);
            mipExports.Add(new NativeTextureMipExport(
                index,
                mip.Width,
                mip.Height,
                mip.PayloadLogicalOffset!.Value,
                mip.BulkDataFlags,
                storageCodec,
                storedData.Length,
                storedHash,
                Path.GetRelativePath(outputDirectory, rawPath),
                storageHeaderBytes,
                decoded.Length,
                decodedHash,
                ddsPayloadOffset));
            ddsPayloadOffset = checked(ddsPayloadOffset + decoded.Length);
        }

        var safeObjectName = SafePathSegment(report.ObjectName);
        var ddsPath = Path.Combine(outputDirectory, safeObjectName + ".dds");
        await DdsBlockCompressedWriter.WriteAsync(
            ddsPath,
            width,
            height,
            formatProfile.DdsFormat,
            decodedMips,
            cancellationToken).ConfigureAwait(false);
        var ddsInspection = await _artifactInspector
            .InspectAsync(ddsPath, cancellationToken)
            .ConfigureAwait(false);
        if (ddsInspection.ErrorCount != 0)
        {
            throw new InvalidDataException(
                $"Arkive's post-export DDS inspection found {ddsInspection.ErrorCount} structural error(s): " +
                string.Join("; ", ddsInspection.Issues
                    .Where(issue => issue.Severity == ArtifactInspectionSeverity.Error)
                    .Select(issue => $"{issue.Code}: {issue.Message}")));
        }

        var editImagePath = Path.Combine(outputDirectory, safeObjectName + "-edit.png");
        var editImage = await TextureEditableImageService.CreatePngAsync(
            ddsPath,
            editImagePath,
            report.ObjectName,
            cancellationToken).ConfigureAwait(false);
        var editImageReload = await TextureEditableImageService.LoadAsync(
            editImagePath,
            cancellationToken).ConfigureAwait(false);
        if (editImageReload.Image.Width != width || editImageReload.Image.Height != height ||
            !editImageReload.Sha256.Equals(editImage.OutputSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Arkive's editable PNG failed post-creation validation.");
        }

        var ddsFile = new FileInfo(ddsPath);
        var ddsHash = await ComputeFileHashAsync(ddsPath, cancellationToken).ConfigureAwait(false);
        var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        var manifest = new NativeTextureExportManifest(
            CurrentManifestSchemaVersion,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            source.FullName,
            source.Name,
            report.PackageAlias,
            source.Length,
            source.LastWriteTimeUtc.Ticks,
            report.PackageSha256,
            logical.ProtectionProviderName,
            report.ExportIndex,
            report.NativeExportIndex,
            report.ClassName,
            report.ObjectName,
            report.SerialOffset,
            report.SerialSize,
            exportHash,
            report.PropertyStreamRelativeOffset!.Value,
            report.NativeDataRelativeOffset!.Value,
            formatProfile.ManifestPixelFormat,
            $"Hybrid per mip: Crunch CRN/{formatProfile.CrunchFourCc} to DDS/{formatProfile.DdsFourCc} or " +
            $"UE3 compressed-bulk Lost Ark LZ4/{formatProfile.GpuFormatName}",
            width,
            height,
            originalWidth,
            originalHeight,
            decodedMips.Count,
            DecoderName,
            Path.GetRelativePath(outputDirectory, ddsPath),
            ddsFile.Length,
            ddsHash,
            mipExports,
            Path.GetFullPath(request.ReconnaissanceJsonPath),
            watch.Elapsed.TotalSeconds,
            RgbViewDdsRelativePath: null,
            RgbViewDdsBytes: null,
            RgbViewDdsSha256: null,
            EditImageRelativePath: Path.GetRelativePath(outputDirectory, editImage.OutputPngPath),
            EditImageBytes: editImage.OutputBytes,
            EditImageSha256: editImage.OutputSha256,
            EditImageSemantic: editImage.Semantic.ToString());
        await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);

        source.Refresh();
        if (!source.Exists || source.Length != report.PackageLength ||
            source.LastWriteTimeUtc.Ticks != report.PackageLastWriteUtcTicks)
        {
            throw new IOException(
                "The source package changed while native texture export was running; output must be discarded.");
        }

        watch.Stop();
        var outputFiles = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new NativeTextureExportResult(
            outputDirectory,
            ddsPath,
            manifestPath,
            manifest with { DurationSeconds = watch.Elapsed.TotalSeconds },
            outputFiles,
            ddsInspection,
            watch.Elapsed);
    }

    private static NativeTextureFormatProfile ValidateReportGate(TextureReconnaissanceReport report)
    {
        if (report.SchemaVersion != TextureReconnaissanceService.CurrentSchemaVersion)
        {
            Refuse(
                "NTX010",
                "The reconnaissance report schema is stale; rerun Native texture recon first.");
        }

        if (!report.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
        {
            Refuse("NTX011", "The first exporter supports only the exact Texture2D class.");
        }

        if (report.PropertyStreamRelativeOffset is null || report.NativeDataRelativeOffset is null)
        {
            Refuse("NTX012", "The UObject property stream was not verified.");
        }

        if (!NativeTextureFormats.TryResolve(report, out var formatProfile))
        {
            Refuse(
                "NTX013",
                "The verified texture format is not a currently supported PF_DXT1, PF_DXT5, or PF_BC5 profile.");
        }

        if (report.MipCandidates.Count != 1)
        {
            Refuse("NTX015", "Exactly one mip-layout candidate is required.");
        }

        var candidate = report.MipCandidates[0];
        if (candidate.Confidence < TextureReconnaissanceConfidence.StrongCandidate ||
            candidate.DeclaredMipCount < 1 ||
            candidate.DeclaredMipCount != candidate.Mips.Count)
        {
            Refuse("NTX016", "The unique mip chain does not meet the strong-candidate gate.");
        }

        var crunch = report.Properties.SingleOrDefault(property =>
            property.Name.Equals("bUseCrunchCompression", StringComparison.OrdinalIgnoreCase));
        var compressedSizeEvidence = candidate.Mips.Any(mip =>
            mip.StoredSize > 0 && mip.ElementCount > 0 && mip.StoredSize < mip.ElementCount);
        if (crunch is not null && crunch.NumericValue != 1)
        {
            Refuse("NTX014", "bUseCrunchCompression is present but is not True.");
        }
        if (crunch is null && !compressedSizeEvidence)
        {
            Refuse(
                "NTX014",
                "Neither bUseCrunchCompression=True nor a confirmed compressed-size mip chain was verified.");
        }

        return formatProfile;
    }

    private static void ValidateMipGate(
        TextureMipEvidence mip,
        int index,
        int width,
        int height,
        int exportSize,
        int smallest)
    {
        // Block-compressed mips stop at one 4x4 block; uncompressed ones go down to single pixels.
        var expectedWidth = Math.Max(smallest, width >> index);
        var expectedHeight = Math.Max(smallest, height >> index);
        if (mip.MipIndex != index || mip.Width != expectedWidth || mip.Height != expectedHeight)
        {
            Refuse(
                "NTX017",
                $"Mip {index} dimensions/order are not the verified block-aligned chain: " +
                $"expected {expectedWidth}x{expectedHeight}, found {mip.Width}x{mip.Height}.");
        }

        if (!mip.Storage.Equals("Inline", StringComparison.Ordinal) ||
            !mip.BulkOffsetMatchesPayload ||
            mip.PayloadRelativeOffset is null ||
            mip.PayloadLogicalOffset is null ||
            string.IsNullOrWhiteSpace(mip.PayloadSha256) ||
            mip.StoredSize <= 0 ||
            mip.PayloadRelativeOffset < 0 ||
            mip.PayloadRelativeOffset > exportSize - mip.StoredSize)
        {
            Refuse("NTX018", $"Mip {index} is not a bounded, hash-verified inline payload.");
        }
    }

    private static int RequiredIntProperty(TextureReconnaissanceReport report, string name)
    {
        var property = report.Properties.SingleOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (property?.NumericValue is not { } value || value is <= 0 or > 65_536)
        {
            throw new NativeTextureExportRefusalException(
                "NTX019",
                $"The required positive {name} property was not verified.");
        }

        return checked((int)value);
    }

    private static void Refuse(string code, string message) =>
        throw new NativeTextureExportRefusalException(code, message);

    private static async Task<string> ComputeFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>Writes a native texture manifest to a new file (it never replaces an existing one).</summary>
    public static Task SaveManifestAsync(string path, NativeTextureExportManifest manifest,
        CancellationToken cancellationToken = default) =>
        SaveJsonAtomicAsync(Path.GetFullPath(path), manifest, cancellationToken);

    private static async Task SaveJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string SafePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned)
            ? "texture"
            : cleaned[..Math.Min(80, cleaned.Length)];
    }
}
