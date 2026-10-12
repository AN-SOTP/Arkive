using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Packages;
using Arkive.Core.Validation;

namespace Arkive.Core.Textures;

public sealed record NoOpUpkReconstructionRequest(
    string PhysicalPackagePlanManifestPath,
    string OutputDirectory);

public sealed record NoOpCopyRegionEvidence(
    int RegionIndex,
    string Kind,
    long PhysicalOffset,
    long Bytes,
    string PlannedSha256,
    string WrittenSha256,
    bool ByteIdentical,
    int? ChunkIndex);

public sealed record NoOpUpkReconstructionManifest(
    int SchemaVersion,
    string ReconstructionId,
    DateTimeOffset CreatedAt,
    string PhysicalPackagePlanManifestPath,
    string PhysicalPlanId,
    string LogicalPackageImageManifestPath,
    string LogicalImageId,
    string SourcePackagePath,
    string SourcePackageSha256,
    long SourcePhysicalPackageBytes,
    string OutputUpkRelativePath,
    long OutputPhysicalPackageBytes,
    string OutputPackageSha256,
    long LogicalPackageBytes,
    string SourceLogicalPackageSha256,
    string OutputLogicalPackageSha256,
    string SourceCatalogueSha256,
    string OutputCatalogueSha256,
    string PackageAlias,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    string PixelFormat,
    int ChunkCount,
    int CopyRegionCount,
    bool AllCopyRegionsByteIdentical,
    bool PhysicalFileByteIdentical,
    bool PackageSummaryEquivalent,
    bool ChunkTableEquivalent,
    bool LogicalImageByteIdentical,
    bool AssetCatalogueEquivalent,
    bool StructuralValidationPassed,
    bool SourcePackageIdentityRetained,
    bool NoOpReconstructionProofPassed,
    bool OutputContainsReplacement,
    bool ReplacementPhysicalWriterReady,
    bool PackageWasModified,
    IReadOnlyList<TextureLayoutCheck> Checks,
    IReadOnlyList<NoOpCopyRegionEvidence> CopyRegions,
    IReadOnlyList<ValidationIssue> ValidationIssues,
    double DurationSeconds);

public sealed record NoOpUpkReconstructionResult(
    string OutputDirectory,
    string ManifestPath,
    string OutputUpkPath,
    NoOpUpkReconstructionManifest Manifest,
    IReadOnlyList<string> OutputFiles,
    TimeSpan Duration);

public sealed class NoOpUpkReconstructionRefusalException : NotSupportedException
{
    public NoOpUpkReconstructionRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Reassembles a source UPK from the physical plan's complete ordered region
/// inventory. Every output byte must match the source. This proves Arkive's
/// physical layout reader/emitter boundary without yet encoding changed chunks.
/// </summary>
public sealed class NoOpUpkReconstructionService
{
    public const int CurrentManifestSchemaVersion = 1;
    public const string ManifestFileName = "arkive-noop-upk-reconstruction.json";
    public const string OutputUpkFileName = "arkive-noop-reconstructed.upk";

    private const int BufferBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly UpkPackageReader _summaryReader;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly AssetCatalogReader _catalogReader;

    public NoOpUpkReconstructionService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _summaryReader = summaryReader ?? new UpkPackageReader();
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(_summaryReader, _protectionProvider);
    }

    public static async Task<NoOpUpkReconstructionManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = RequireFile(path, "NPR001", "no-op UPK reconstruction manifest");
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<NoOpUpkReconstructionManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new NoOpUpkReconstructionRefusalException(
                           "NPR002", "The no-op UPK reconstruction manifest was empty.");
        if (manifest.SchemaVersion != CurrentManifestSchemaVersion)
        {
            Refuse("NPR003", $"No-op reconstruction schema {manifest.SchemaVersion} is unsupported.");
        }
        return manifest;
    }

    public async Task<NoOpUpkReconstructionResult> ReconstructAsync(
        NoOpUpkReconstructionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var planPath = RequireFile(
            request.PhysicalPackagePlanManifestPath, "NPR004", "physical package-plan manifest");
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            Refuse("NPR005", "The no-op reconstruction output directory must be empty.");
        }

        var temporaryPath = Path.Combine(
            outputDirectory, $"{OutputUpkFileName}.{Guid.NewGuid():N}.tmp");
        var outputPath = Path.Combine(outputDirectory, OutputUpkFileName);
        try
        {
            var plan = await PhysicalPackagePlanService.LoadManifestAsync(
                planPath, cancellationToken).ConfigureAwait(false);
            if (!plan.PlanReadyForNoOpReconstruction || !plan.AllChunkEnvelopesParsed ||
                !plan.AllProtectedBlocksReadable || !plan.LogicalSourceIdentityConfirmed ||
                !plan.CopyRegionsCoverPhysicalFile || plan.PackageWasModified)
            {
                Refuse("NPR006", "A complete, source-identical physical package plan is required.");
            }

            var logicalManifest = await LogicalPackageImageService.LoadManifestAsync(
                plan.LogicalPackageImageManifestPath, cancellationToken).ConfigureAwait(false);
            if (!logicalManifest.ImageId.Equals(plan.LogicalImageId, StringComparison.OrdinalIgnoreCase) ||
                logicalManifest.NativeExportIndex != plan.NativeExportIndex)
            {
                Refuse("NPR007", "The physical plan no longer agrees with its logical-image session.");
            }

            var sourcePath = RequireFile(plan.SourcePackagePath, "NPR008", "source package");
            var sourceHashBefore = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (!sourceHashBefore.Equals(plan.SourcePackageSha256, StringComparison.OrdinalIgnoreCase) ||
                new FileInfo(sourcePath).Length != plan.SourcePhysicalPackageBytes)
            {
                Refuse("NPR009", "The source package changed after physical planning.");
            }

            var regionEvidence = new List<NoOpCopyRegionEvidence>(plan.CopyRegions.Count);
            await using (var source = new FileStream(
                             sourcePath, FileMode.Open, FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete, BufferBytes,
                             FileOptions.Asynchronous | FileOptions.RandomAccess))
            await using (var output = new FileStream(
                             temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                foreach (var region in plan.CopyRegions.OrderBy(item => item.RegionIndex))
                {
                    if (region.PhysicalOffset != output.Position)
                    {
                        Refuse("NPR010", "The ordered physical copy plan contains a gap or overlap.");
                    }
                    source.Position = region.PhysicalOffset;
                    var writtenHash = await CopyAndHashAsync(
                        source, output, region.Bytes, cancellationToken).ConfigureAwait(false);
                    if (!writtenHash.Equals(region.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Refuse("NPR011", $"Physical region {region.RegionIndex} changed after planning.");
                    }
                    regionEvidence.Add(new NoOpCopyRegionEvidence(
                        region.RegionIndex,
                        region.Kind,
                        region.PhysicalOffset,
                        region.Bytes,
                        region.Sha256,
                        writtenHash,
                        true,
                        region.ChunkIndex));
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var outputInfo = new FileInfo(temporaryPath);
            var outputHash = await HashFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            var physicalIdentical = outputInfo.Length == plan.SourcePhysicalPackageBytes &&
                                    outputHash.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase);
            if (!physicalIdentical)
            {
                Refuse("NPR012", "The reconstructed no-op UPK is not physically byte-identical.");
            }

            var sourceSummary = _summaryReader.Read(sourcePath);
            var outputSummary = _summaryReader.Read(temporaryPath);
            var summaryEquivalent = SummariesEquivalent(sourceSummary, outputSummary);
            var chunksEquivalent = sourceSummary.CompressedChunks.SequenceEqual(outputSummary.CompressedChunks);
            if (!summaryEquivalent || !chunksEquivalent)
            {
                Refuse("NPR013", "The reconstructed package summary or chunk table differs from source.");
            }

            var validation = PackageValidator.Validate(outputSummary);
            var validationPassed = validation.All(issue => issue.Severity != ValidationSeverity.Error);
            if (!validationPassed)
            {
                Refuse("NPR014", "The reconstructed UPK failed structural package validation.");
            }

            var outputLogicalHash = await HashLogicalAsync(
                outputSummary, cancellationToken).ConfigureAwait(false);
            var logicalIdentical = outputLogicalHash.Equals(
                plan.SourceLogicalPackageSha256, StringComparison.OrdinalIgnoreCase);
            if (!logicalIdentical)
            {
                Refuse("NPR015", "The reconstructed UPK does not decode to the source logical identity.");
            }

            var sourceCatalog = _catalogReader.Read(sourceSummary);
            var outputCatalog = _catalogReader.Read(outputSummary);
            var sourceCatalogHash = HashCatalogue(sourceCatalog);
            var outputCatalogHash = HashCatalogue(outputCatalog);
            var catalogEquivalent = sourceCatalogHash.Equals(
                outputCatalogHash, StringComparison.OrdinalIgnoreCase) &&
                                    sourceCatalog.Assets.Count == outputCatalog.Assets.Count &&
                                    sourceCatalog.Exports.Count == outputCatalog.Exports.Count;
            if (!catalogEquivalent)
            {
                Refuse("NPR016", "The reconstructed UPK asset catalogue differs from source.");
            }

            var sourceHashAfter = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
            {
                Refuse("NPR017", "The source UPK identity changed during no-op reconstruction.");
            }

            File.Move(temporaryPath, outputPath);
            watch.Stop();
            var checks = new List<TextureLayoutCheck>
            {
                new("NPR_SOURCE_IDENTITY", TextureLayoutCheckStatus.Passed,
                    $"Source UPK remained {sourceHashAfter} before and after reconstruction."),
                new("NPR_REGION_REPLAY", TextureLayoutCheckStatus.Passed,
                    $"Reassembled all {regionEvidence.Count:N0} planned physical regions in order with matching hashes."),
                new("NPR_PHYSICAL_IDENTITY", TextureLayoutCheckStatus.Passed,
                    $"The {outputInfo.Length:N0}-byte output is physically byte-identical: {outputHash}."),
                new("NPR_SUMMARY", TextureLayoutCheckStatus.Passed,
                    $"Package summary and all {outputSummary.CompressedChunks.Count:N0} chunk records are equivalent."),
                new("NPR_LOGICAL_IDENTITY", TextureLayoutCheckStatus.Passed,
                    $"The output decodes to the original {plan.SourceLogicalPackageBytes:N0}-byte logical SHA-256 {outputLogicalHash}."),
                new("NPR_CATALOGUE", TextureLayoutCheckStatus.Passed,
                    $"The output reproduces {outputCatalog.Assets.Count:N0} assets and catalogue SHA-256 {outputCatalogHash}."),
                new("NPR_VALIDATION", TextureLayoutCheckStatus.Passed,
                    "The reconstructed UPK passed Arkive structural validation without errors."),
                new("NPR_REPLACEMENT_SCOPE", TextureLayoutCheckStatus.Information,
                    "This proof contains no replacement. Changed-chunk encoding and physical-table mutation remain disabled."),
                new("NPR_NO_GAME_WRITE", TextureLayoutCheckStatus.Information,
                    "The reconstructed UPK exists only in Arkive staging; the source/game package was not written."),
            };
            var manifest = new NoOpUpkReconstructionManifest(
                CurrentManifestSchemaVersion,
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                planPath,
                plan.PhysicalPlanId,
                plan.LogicalPackageImageManifestPath,
                plan.LogicalImageId,
                sourcePath,
                sourceHashAfter,
                plan.SourcePhysicalPackageBytes,
                OutputUpkFileName,
                outputInfo.Length,
                outputHash,
                plan.SourceLogicalPackageBytes,
                plan.SourceLogicalPackageSha256,
                outputLogicalHash,
                sourceCatalogHash,
                outputCatalogHash,
                plan.PackageAlias,
                plan.NativeExportIndex,
                plan.ClassName,
                plan.ObjectName,
                plan.PixelFormat,
                plan.CompressedChunkCount,
                regionEvidence.Count,
                AllCopyRegionsByteIdentical: true,
                PhysicalFileByteIdentical: true,
                PackageSummaryEquivalent: true,
                ChunkTableEquivalent: true,
                LogicalImageByteIdentical: true,
                AssetCatalogueEquivalent: true,
                StructuralValidationPassed: true,
                SourcePackageIdentityRetained: true,
                NoOpReconstructionProofPassed: true,
                OutputContainsReplacement: false,
                ReplacementPhysicalWriterReady: false,
                PackageWasModified: false,
                checks,
                regionEvidence,
                validation,
                watch.Elapsed.TotalSeconds);
            var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
            await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            return new NoOpUpkReconstructionResult(
                outputDirectory,
                manifestPath,
                outputPath,
                manifest,
                [outputPath, manifestPath],
                watch.Elapsed);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            throw;
        }
    }

    private static bool SummariesEquivalent(PackageSummary left, PackageSummary right) =>
        left.Tag == right.Tag &&
        left.FileVersion == right.FileVersion &&
        left.LicenseeVersion == right.LicenseeVersion &&
        left.HeaderSize == right.HeaderSize &&
        left.FolderName == right.FolderName &&
        left.PackageFlags == right.PackageFlags &&
        left.NameCount == right.NameCount &&
        left.NameOffset == right.NameOffset &&
        left.ExportCount == right.ExportCount &&
        left.ExportOffset == right.ExportOffset &&
        left.ImportCount == right.ImportCount &&
        left.ImportOffset == right.ImportOffset &&
        left.DependsOffset == right.DependsOffset &&
        left.ImportExportGuidsOffset == right.ImportExportGuidsOffset &&
        left.ImportGuidsCount == right.ImportGuidsCount &&
        left.ExportGuidsCount == right.ExportGuidsCount &&
        left.ThumbnailTableOffset == right.ThumbnailTableOffset &&
        left.PackageGuid == right.PackageGuid &&
        left.Generations.SequenceEqual(right.Generations) &&
        left.EngineVersion == right.EngineVersion &&
        left.CookerVersion == right.CookerVersion &&
        left.CompressionFlags == right.CompressionFlags &&
        left.PackageSource == right.PackageSource &&
        left.ParsedSummarySize == right.ParsedSummarySize &&
        left.PhysicalFileSize == right.PhysicalFileSize;

    private async Task<string> HashLogicalAsync(
        PackageSummary summary,
        CancellationToken cancellationToken)
    {
        using var logical = new LogicalPackageStream(summary, _protectionProvider);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferBytes];
        while (true)
        {
            var read = await logical.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string HashCatalogue(AssetCatalog catalog)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var asset in catalog.Assets.OrderBy(item => item.ExportIndex))
        {
            var export = catalog.Exports[asset.ExportIndex - 1];
            var line = string.Join('|',
                asset.ExportIndex,
                asset.ClassName,
                asset.ObjectName,
                asset.Kind,
                export.SerialOffset,
                export.SerialSize) + "\n";
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> CopyAndHashAsync(
        Stream source,
        Stream output,
        long bytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferBytes];
        long remaining = bytes;
        while (remaining > 0)
        {
            var count = checked((int)Math.Min(buffer.Length, remaining));
            var read = await source.ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("A planned source region was truncated.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
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
        throw new NoOpUpkReconstructionRefusalException(code, message);
}
