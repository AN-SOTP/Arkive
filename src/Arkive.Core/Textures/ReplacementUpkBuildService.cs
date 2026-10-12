using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Compression;
using Arkive.Core.Inspection;
using Arkive.Core.Packages;
using Arkive.Core.Validation;

namespace Arkive.Core.Textures;

public sealed record ReplacementUpkBuildRequest(
    string NoOpUpkReconstructionManifestPath,
    string OutputDirectory);

public sealed record ReplacementChunkBlockEvidence(
    int BlockIndex,
    int LogicalBytes,
    int PlainStoredBytes,
    int ProtectedPrefixBytes,
    bool StoredRaw,
    string PlainStoredSha256,
    string PhysicalStoredSha256,
    bool DecodeBackVerified);

public sealed record ReplacementChunkWriteEvidence(
    int ChunkIndex,
    uint SourceLogicalOffset,
    uint SourceLogicalBytes,
    uint OutputLogicalOffset,
    uint OutputLogicalBytes,
    uint SourcePhysicalOffset,
    uint SourcePhysicalBytes,
    uint OutputPhysicalOffset,
    uint OutputPhysicalBytes,
    uint LostArkFlags,
    string Action,
    bool Rebuilt,
    bool SourcePhysicalBytesRetained,
    string OutputPhysicalSha256,
    string OutputLogicalSha256,
    IReadOnlyList<ReplacementChunkBlockEvidence> Blocks);

public sealed record ReplacementChunkTablePatchEvidence(
    int ChunkIndex,
    string Field,
    long PhysicalOffset,
    uint SourceValue,
    uint OutputValue,
    bool Changed);

public sealed record ReplacementUpkBuildManifest(
    int SchemaVersion,
    string BuildId,
    DateTimeOffset CreatedAt,
    string NoOpUpkReconstructionManifestPath,
    string NoOpReconstructionId,
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
    long OutputLogicalPackageBytes,
    string ExpectedLogicalPackageSha256,
    string OutputLogicalPackageSha256,
    string PackageAlias,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    string PixelFormat,
    long OriginalSerialOffset,
    long OriginalSerialSize,
    long ReplacementSerialOffset,
    long ReplacementSerialSize,
    int ChunkCount,
    int RebuiltChunkCount,
    int ByteIdenticalChunkCount,
    int ProtectedRebuiltChunkCount,
    int EncodedBlockCount,
    bool NoOpProofRequiredAndPassed,
    bool ChunkTableRecalculated,
    bool AllEncodedBlocksDecodeBack,
    bool AllUnaffectedChunksByteIdentical,
    bool ExpectedLogicalImageMatched,
    bool TargetExportRetargeted,
    bool OriginalExportBytesRetained,
    bool ReplacementExportBytesVerified,
    bool UnrelatedExportsEquivalent,
    bool AssetCatalogueShapeRetained,
    bool StructuralValidationPassed,
    bool TargetTextureNativeDecodePassed,
    bool SourcePackageIdentityRetained,
    bool ReplacementPackageBuildPassed,
    bool OutputContainsReplacement,
    bool StagingPackageDiffersFromSource,
    bool DeploymentReady,
    bool PackageWasModified,
    IReadOnlyList<TextureLayoutCheck> Checks,
    IReadOnlyList<ReplacementChunkWriteEvidence> Chunks,
    IReadOnlyList<ReplacementChunkTablePatchEvidence> ChunkTablePatches,
    IReadOnlyList<ValidationIssue> ValidationIssues,
    double DurationSeconds);

public sealed record ReplacementUpkBuildResult(
    string OutputDirectory,
    string ManifestPath,
    string OutputUpkPath,
    ReplacementUpkBuildManifest Manifest,
    IReadOnlyList<string> OutputFiles,
    TimeSpan Duration);

public sealed record ReplacementUpkOutputVerification(
    string ManifestPath,
    string OutputUpkPath,
    long OutputBytes,
    string OutputSha256,
    ReplacementUpkBuildManifest Manifest);

public sealed class ReplacementUpkBuildRefusalException : NotSupportedException
{
    public ReplacementUpkBuildRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Emits the first replacement-bearing UPK into Arkive staging. A previously
/// verified no-op reconstruction is a mandatory gate. Unaffected physical chunks
/// are retained byte-for-byte; affected chunks are rebuilt from the audited logical
/// image and the physical chunk table is recalculated. The installed source package
/// is always opened read-only and deployment remains deliberately disabled.
/// </summary>
public sealed class ReplacementUpkBuildService
{
    public const int CurrentManifestSchemaVersion = 2;
    public const string ManifestFileName = "arkive-replacement-upk.json";
    public const string OutputUpkFileName = "arkive-replacement-staging.upk";

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

    public ReplacementUpkBuildService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _summaryReader = summaryReader ?? new UpkPackageReader();
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(_summaryReader, _protectionProvider);
    }

    public static async Task<ReplacementUpkBuildManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = RequireFile(path, "RUB001", "replacement UPK manifest");
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<ReplacementUpkBuildManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new ReplacementUpkBuildRefusalException(
                           "RUB002", "The replacement UPK manifest was empty.");
        if (manifest.SchemaVersion != CurrentManifestSchemaVersion)
        {
            var migration = manifest.SchemaVersion == 1
                ? " Schema 1 predates strict native mip decoding and may contain equal-size raw bytes under Lost Ark's LZ4 flag. Rebuild native mip payloads and every downstream replacement stage."
                : string.Empty;
            Refuse(
                "RUB003",
                $"Replacement UPK schema {manifest.SchemaVersion} is unsupported; " +
                $"Arkive requires {CurrentManifestSchemaVersion}.{migration}");
        }
        return manifest;
    }

    /// <summary>
    /// Revalidates a staged replacement package immediately before an external
    /// consumer is allowed to open it. This prevents a stale, moved, or modified
    /// output from being mistaken for the artifact approved by its build manifest.
    /// </summary>
    public static async Task<ReplacementUpkOutputVerification> VerifyOutputAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var fullManifestPath = RequireFile(
            manifestPath, "RUB030", "replacement UPK manifest");
        var manifest = await LoadManifestAsync(fullManifestPath, cancellationToken)
            .ConfigureAwait(false);
        if (!manifest.ReplacementPackageBuildPassed || !manifest.OutputContainsReplacement ||
            !manifest.NoOpProofRequiredAndPassed || !manifest.ChunkTableRecalculated ||
            !manifest.AllEncodedBlocksDecodeBack || !manifest.ExpectedLogicalImageMatched ||
            !manifest.TargetExportRetargeted || !manifest.ReplacementExportBytesVerified ||
            !manifest.UnrelatedExportsEquivalent || !manifest.AssetCatalogueShapeRetained ||
            !manifest.StructuralValidationPassed || !manifest.TargetTextureNativeDecodePassed ||
            !manifest.SourcePackageIdentityRetained ||
            manifest.DeploymentReady || manifest.PackageWasModified)
        {
            Refuse("RUB031", "The manifest does not describe a complete, staging-only replacement build.");
        }

        var outputPath = ResolveWithin(
            Path.GetDirectoryName(fullManifestPath)!, manifest.OutputUpkRelativePath, "RUB032");
        var outputInfo = new FileInfo(outputPath);
        if (outputInfo.Length != manifest.OutputPhysicalPackageBytes)
        {
            Refuse(
                "RUB033",
                $"The staged UPK size changed after validation " +
                $"({outputInfo.Length:N0} bytes; expected {manifest.OutputPhysicalPackageBytes:N0}).");
        }

        var outputHash = await HashFileAsync(outputPath, cancellationToken).ConfigureAwait(false);
        if (!outputHash.Equals(manifest.OutputPackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("RUB034", "The staged UPK SHA-256 changed after validation.");
        }

        return new ReplacementUpkOutputVerification(
            fullManifestPath,
            outputPath,
            outputInfo.Length,
            outputHash,
            manifest);
    }

    public async Task<ReplacementUpkBuildResult> BuildAsync(
        ReplacementUpkBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var noOpPath = RequireFile(
            request.NoOpUpkReconstructionManifestPath, "RUB004", "no-op reconstruction manifest");
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            Refuse("RUB005", "The replacement UPK output directory must be empty.");
        }

        var temporaryPath = Path.Combine(outputDirectory, $"{OutputUpkFileName}.{Guid.NewGuid():N}.tmp");
        var outputPath = Path.Combine(outputDirectory, OutputUpkFileName);
        try
        {
            var noOp = await NoOpUpkReconstructionService.LoadManifestAsync(
                noOpPath, cancellationToken).ConfigureAwait(false);
            if (!noOp.NoOpReconstructionProofPassed || !noOp.AllCopyRegionsByteIdentical ||
                !noOp.PhysicalFileByteIdentical || !noOp.PackageSummaryEquivalent ||
                !noOp.ChunkTableEquivalent || !noOp.LogicalImageByteIdentical ||
                !noOp.AssetCatalogueEquivalent || !noOp.StructuralValidationPassed ||
                !noOp.SourcePackageIdentityRetained || noOp.OutputContainsReplacement ||
                noOp.PackageWasModified)
            {
                Refuse("RUB006", "A complete source-identical no-op UPK proof is required.");
            }

            var plan = await PhysicalPackagePlanService.LoadManifestAsync(
                noOp.PhysicalPackagePlanManifestPath, cancellationToken).ConfigureAwait(false);
            if (!plan.PhysicalPlanId.Equals(noOp.PhysicalPlanId, StringComparison.OrdinalIgnoreCase) ||
                !plan.PlanReadyForNoOpReconstruction || !plan.AllChunkEnvelopesParsed ||
                !plan.AllProtectedBlocksReadable || !plan.LogicalSourceIdentityConfirmed ||
                !plan.CopyRegionsCoverPhysicalFile || plan.PackageWasModified)
            {
                Refuse("RUB007", "The no-op proof no longer agrees with a complete physical plan.");
            }

            var logicalManifest = await LogicalPackageImageService.LoadManifestAsync(
                plan.LogicalPackageImageManifestPath, cancellationToken).ConfigureAwait(false);
            if (!logicalManifest.ImageId.Equals(plan.LogicalImageId, StringComparison.OrdinalIgnoreCase) ||
                !logicalManifest.LogicalImageReadyForPhysicalWriter ||
                !logicalManifest.AllUnplannedSourceBytesIdentical ||
                !logicalManifest.AllTablePatchesApplied || !logicalManifest.AllBulkOffsetsRebased ||
                !logicalManifest.OriginalExportBytesRetained ||
                !logicalManifest.AppendedReplacementVerified ||
                !logicalManifest.SourcePackageIdentityRetained || logicalManifest.PackageWasModified)
            {
                Refuse("RUB008", "The physical plan no longer agrees with a verified logical image.");
            }

            var logicalPath = ResolveWithin(
                Path.GetDirectoryName(plan.LogicalPackageImageManifestPath)!,
                logicalManifest.LogicalImageRelativePath, "RUB009");
            var logicalBytes = await File.ReadAllBytesAsync(logicalPath, cancellationToken).ConfigureAwait(false);
            if (logicalBytes.LongLength != logicalManifest.LogicalImageBytes ||
                !Hash(logicalBytes).Equals(logicalManifest.LogicalImageSha256, StringComparison.OrdinalIgnoreCase))
            {
                Refuse("RUB010", "The logical image changed after physical planning.");
            }

            var sourcePath = RequireFile(plan.SourcePackagePath, "RUB011", "source package");
            var sourceHashBefore = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (!sourceHashBefore.Equals(plan.SourcePackageSha256, StringComparison.OrdinalIgnoreCase) ||
                !sourceHashBefore.Equals(noOp.SourcePackageSha256, StringComparison.OrdinalIgnoreCase) ||
                new FileInfo(sourcePath).Length != plan.SourcePhysicalPackageBytes)
            {
                Refuse("RUB012", "The source package changed after the no-op proof.");
            }

            var sourceSummary = _summaryReader.Read(sourcePath);
            if (sourceSummary.CompressedChunks.Count != plan.Chunks.Count ||
                sourceSummary.CompressedChunkTableOffset != plan.CompressedChunkTablePhysicalOffset)
            {
                Refuse("RUB013", "The source chunk table no longer matches the physical plan.");
            }

            var build = sourceSummary.CompressedChunks.Count == 0
                ? await BuildUncompressedAsync(logicalBytes, temporaryPath, cancellationToken).ConfigureAwait(false)
                : await BuildCompressedAsync(
                    sourcePath, sourceSummary, plan, logicalBytes, temporaryPath, cancellationToken)
                    .ConfigureAwait(false);

            var outputSummary = _summaryReader.Read(temporaryPath);
            var validation = PackageValidator.Validate(outputSummary);
            if (validation.Any(issue => issue.Severity == ValidationSeverity.Error))
            {
                Refuse("RUB014", "The staged replacement UPK failed structural validation.");
            }

            var decoded = await ReadLogicalAsync(outputSummary, cancellationToken).ConfigureAwait(false);
            var expectedHash = Hash(build.ExpectedLogicalBytes);
            var decodedHash = Hash(decoded);
            if (!decoded.AsSpan().SequenceEqual(build.ExpectedLogicalBytes))
            {
                Refuse("RUB015", "The staged UPK does not decode to the finalized expected logical image.");
            }

            var sourceCatalog = _catalogReader.Read(sourceSummary);
            var outputCatalog = _catalogReader.Read(outputSummary);
            var targetSource = sourceCatalog.Exports.SingleOrDefault(
                item => item.TableIndex == logicalManifest.NativeExportIndex);
            var targetOutput = outputCatalog.Exports.SingleOrDefault(
                item => item.TableIndex == logicalManifest.NativeExportIndex);
            if (targetSource is null || targetOutput is null ||
                targetOutput.SerialOffset != logicalManifest.AppendedSerialOffset ||
                targetOutput.SerialSize != logicalManifest.AppendedSerialSize)
            {
                Refuse("RUB016", "The selected export was not retargeted to the appended replacement.");
            }

            var unrelatedEquivalent = sourceCatalog.Exports
                .Where(item => item.TableIndex != logicalManifest.NativeExportIndex)
                .Zip(outputCatalog.Exports.Where(
                    item => item.TableIndex != logicalManifest.NativeExportIndex), ExportsEquivalent)
                .All(value => value) &&
                sourceCatalog.Exports.Count == outputCatalog.Exports.Count;
            var catalogueShape = sourceCatalog.Names.SequenceEqual(outputCatalog.Names) &&
                                 sourceCatalog.Imports.SequenceEqual(outputCatalog.Imports) &&
                                 sourceCatalog.Assets.Zip(outputCatalog.Assets, AssetIdentitiesEquivalent)
                                     .All(value => value) &&
                                 sourceCatalog.Assets.Count == outputCatalog.Assets.Count &&
                                 sourceCatalog.Exports.Count == outputCatalog.Exports.Count;
            if (!unrelatedEquivalent || !catalogueShape)
            {
                Refuse("RUB017",
                    $"An unrelated export or catalogue table changed in the staged package " +
                    $"(unrelatedExports={unrelatedEquivalent}, catalogueShape={catalogueShape}, " +
                    $"source/output exports={sourceCatalog.Exports.Count}/{outputCatalog.Exports.Count}, " +
                    $"assets={sourceCatalog.Assets.Count}/{outputCatalog.Assets.Count}).");
            }

            var originalExport = decoded.AsSpan(
                checked((int)logicalManifest.OriginalSerialOffset),
                checked((int)logicalManifest.OriginalSerialSize));
            var replacementExport = decoded.AsSpan(
                checked((int)logicalManifest.AppendedSerialOffset),
                checked((int)logicalManifest.AppendedSerialSize));
            var originalRetained = Hash(originalExport).Equals(
                logicalManifest.OriginalExportSha256, StringComparison.OrdinalIgnoreCase);
            var replacementVerified = Hash(replacementExport).Equals(
                logicalManifest.RebasedReplacementExportSha256, StringComparison.OrdinalIgnoreCase);
            if (!originalRetained || !replacementVerified)
            {
                Refuse("RUB018", "Original or replacement export byte evidence failed after reconstruction.");
            }

            var outputHash = await HashFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            var outputInfo = new FileInfo(temporaryPath);
            var stagingDiffers = outputInfo.Length != plan.SourcePhysicalPackageBytes ||
                                 !outputHash.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase);
            if (!stagingDiffers)
            {
                Refuse("RUB019", "The replacement staging package unexpectedly matches the source package.");
            }

            var nativeDecode = await ValidateTargetTextureAsync(
                temporaryPath,
                outputHash,
                logicalManifest,
                targetOutput,
                outputDirectory,
                cancellationToken).ConfigureAwait(false);

            var sourceHashAfter = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
            {
                Refuse("RUB020", "The installed source package identity changed during staging.");
            }

            File.Move(temporaryPath, outputPath);
            watch.Stop();
            var rebuilt = build.Chunks.Count(chunk => chunk.Rebuilt);
            var protectedRebuilt = build.Chunks.Count(chunk => chunk.Rebuilt && chunk.LostArkFlags == 1);
            var blocks = build.Chunks.Sum(chunk => chunk.Blocks.Count);
            var checks = new List<TextureLayoutCheck>
            {
                new("RUB_NOOP_GATE", TextureLayoutCheckStatus.Passed,
                    $"Required and revalidated no-op proof {noOp.ReconstructionId}."),
                new("RUB_SOURCE_IDENTITY", TextureLayoutCheckStatus.Passed,
                    $"Source UPK remained read-only and SHA-256 {sourceHashAfter}."),
                new("RUB_CHUNK_WRITER", TextureLayoutCheckStatus.Passed,
                    $"Rebuilt {rebuilt:N0} affected chunk(s), including {protectedRebuilt:N0} protected chunk(s), and retained {build.Chunks.Count - rebuilt:N0} chunk(s) byte-for-byte."),
                new("RUB_BLOCK_DECODE_BACK", TextureLayoutCheckStatus.Passed,
                    $"All {blocks:N0} newly encoded block(s) decoded back to their exact logical bytes."),
                new("RUB_CHUNK_TABLE", TextureLayoutCheckStatus.Passed,
                    $"Recalculated and reread {outputSummary.CompressedChunks.Count:N0} physical chunk record(s)."),
                new("RUB_LOGICAL_IMAGE", TextureLayoutCheckStatus.Passed,
                    $"The staged package decodes to the finalized {decoded.LongLength:N0}-byte logical SHA-256 {decodedHash}."),
                new("RUB_TARGET_EXPORT", TextureLayoutCheckStatus.Passed,
                    $"Export {logicalManifest.NativeExportIndex} now points to 0x{targetOutput.SerialOffset:X}+0x{targetOutput.SerialSize:X}."),
                new("RUB_EXPORT_ISOLATION", TextureLayoutCheckStatus.Passed,
                    $"All {outputCatalog.Exports.Count - 1:N0} unrelated export records and catalogue table shapes remain equivalent."),
                new("RUB_EXPORT_BYTES", TextureLayoutCheckStatus.Passed,
                    "Both the retained original export and appended replacement export match their audited hashes."),
                new("RUB_VALIDATION", TextureLayoutCheckStatus.Passed,
                    "The staged replacement UPK passed Arkive structural validation without errors."),
                new("RUB_TARGET_NATIVE_DECODE", TextureLayoutCheckStatus.Passed,
                    $"The finalized target Texture2D decoded through its native {nativeDecode.PixelFormat} storage chain ({nativeDecode.MipCount:N0} mip(s))."),
                new("RUB_STAGING_ONLY", TextureLayoutCheckStatus.Information,
                    "This replacement-bearing UPK exists only in Arkive staging. Deployment to the game directory remains disabled."),
            };

            var manifest = new ReplacementUpkBuildManifest(
                CurrentManifestSchemaVersion,
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                noOpPath,
                noOp.ReconstructionId,
                noOp.PhysicalPackagePlanManifestPath,
                plan.PhysicalPlanId,
                plan.LogicalPackageImageManifestPath,
                logicalManifest.ImageId,
                sourcePath,
                sourceHashAfter,
                plan.SourcePhysicalPackageBytes,
                OutputUpkFileName,
                outputInfo.Length,
                outputHash,
                decoded.LongLength,
                expectedHash,
                decodedHash,
                logicalManifest.PackageAlias,
                logicalManifest.NativeExportIndex,
                logicalManifest.ClassName,
                logicalManifest.ObjectName,
                logicalManifest.PixelFormat,
                logicalManifest.OriginalSerialOffset,
                logicalManifest.OriginalSerialSize,
                logicalManifest.AppendedSerialOffset,
                logicalManifest.AppendedSerialSize,
                build.Chunks.Count,
                rebuilt,
                build.Chunks.Count - rebuilt,
                protectedRebuilt,
                blocks,
                NoOpProofRequiredAndPassed: true,
                ChunkTableRecalculated: true,
                AllEncodedBlocksDecodeBack: build.Chunks.SelectMany(chunk => chunk.Blocks).All(block => block.DecodeBackVerified),
                AllUnaffectedChunksByteIdentical: build.Chunks.Where(chunk => !chunk.Rebuilt).All(chunk => chunk.SourcePhysicalBytesRetained),
                ExpectedLogicalImageMatched: true,
                TargetExportRetargeted: true,
                OriginalExportBytesRetained: true,
                ReplacementExportBytesVerified: true,
                UnrelatedExportsEquivalent: true,
                AssetCatalogueShapeRetained: true,
                StructuralValidationPassed: true,
                TargetTextureNativeDecodePassed: true,
                SourcePackageIdentityRetained: true,
                ReplacementPackageBuildPassed: true,
                OutputContainsReplacement: true,
                StagingPackageDiffersFromSource: true,
                DeploymentReady: false,
                PackageWasModified: false,
                checks,
                build.Chunks,
                build.TablePatches,
                validation,
                watch.Elapsed.TotalSeconds);
            var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
            await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            return new ReplacementUpkBuildResult(
                outputDirectory, manifestPath, outputPath, manifest,
                [outputPath, manifestPath], watch.Elapsed);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            throw;
        }
    }

    private async Task<NativeTextureExportManifest> ValidateTargetTextureAsync(
        string packagePath,
        string packageSha256,
        LogicalPackageImageManifest logicalManifest,
        UnrealObjectExport targetOutput,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var validationDirectory = Path.Combine(
            outputDirectory, $".target-native-validation-{Guid.NewGuid():N}");
        try
        {
            var report = await new TextureReconnaissanceService(
                    _summaryReader, _protectionProvider)
                .InspectAsync(
                    new TextureReconnaissanceRequest(
                        packagePath,
                        logicalManifest.PackageAlias,
                        targetOutput.TableIndex,
                        AssetKind.Texture,
                        logicalManifest.ClassName,
                        logicalManifest.ObjectName,
                        targetOutput.SerialOffset,
                        targetOutput.SerialSize,
                        packageSha256),
                    cancellationToken)
                .ConfigureAwait(false);
            var result = await new NativeTextureExportService(
                    _summaryReader, _protectionProvider)
                .ExportAsync(
                    new NativeTextureExportRequest(
                        report,
                        validationDirectory,
                        Path.Combine(outputDirectory, ManifestFileName)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Manifest.MipCount <= 0 ||
                result.Manifest.Mips.Count != result.Manifest.MipCount)
            {
                Refuse("RUB035", "The staged target Texture2D did not produce a complete native mip chain.");
            }
            return result.Manifest;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ReplacementUpkBuildRefusalException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Refuse(
                "RUB035",
                $"The staged target Texture2D failed native storage decode: {exception.Message}");
            throw new UnreachableException();
        }
        finally
        {
            if (Directory.Exists(validationDirectory))
            {
                Directory.Delete(validationDirectory, recursive: true);
            }
        }
    }

    private static async Task<BuildEvidence> BuildUncompressedAsync(
        byte[] logicalBytes,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(outputPath, logicalBytes, cancellationToken).ConfigureAwait(false);
        return new BuildEvidence(logicalBytes, [], []);
    }

    private async Task<BuildEvidence> BuildCompressedAsync(
        string sourcePath,
        PackageSummary summary,
        PhysicalPackagePlanManifest plan,
        byte[] logicalImage,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var firstLogicalOffset = summary.CompressedChunks[0].UncompressedOffset;
        var firstPhysicalOffset = summary.CompressedChunks[0].CompressedOffset;
        var chunkTableEnd = summary.CompressedChunkTableOffset + summary.CompressedChunks.Count * 20L;
        if (chunkTableEnd > firstPhysicalOffset || firstPhysicalOffset < firstLogicalOffset)
        {
            Refuse("RUB021", "The chunk table/prefix geometry cannot be safely rewritten.");
        }

        var affectedIndices = new HashSet<int>();
        for (var index = 0; index < summary.CompressedChunks.Count; index++)
        {
            var evidence = plan.Chunks[index];
            if (evidence.ContainsReplacementTablePatch || evidence.ReceivesLogicalAppend)
            {
                affectedIndices.Add(index);
            }
        }
        if (affectedIndices.Count == 0)
        {
            Refuse("RUB022", "The physical plan identifies no replacement-affected chunks.");
        }

        Dictionary<int, EncodedChunk> encoded = [];
        IReadOnlyList<CompressedChunk>? previousPlan = null;
        IReadOnlyList<CompressedChunk> plannedChunks = [];
        var expectedLogical = logicalImage.ToArray();
        var converged = false;
        for (var pass = 0; pass < 32; pass++)
        {
            encoded = EncodeAffected(expectedLogical);
            plannedChunks = PlanChunks(encoded);
            var nextExpected = logicalImage.ToArray();
            ApplyChunkTable(nextExpected, plannedChunks, null, logicalPrefixOnly: true);
            if (previousPlan is not null && previousPlan.SequenceEqual(plannedChunks) &&
                nextExpected.AsSpan().SequenceEqual(expectedLogical))
            {
                expectedLogical = nextExpected;
                converged = true;
                break;
            }
            previousPlan = plannedChunks.ToArray();
            expectedLogical = nextExpected;
        }
        if (!converged)
        {
            Refuse("RUB029", "The self-referential compressed chunk-table layout did not converge.");
        }

        Dictionary<int, EncodedChunk> EncodeAffected(byte[] logical)
        {
            var result = new Dictionary<int, EncodedChunk>();
            foreach (var index in affectedIndices)
            {
                var chunk = summary.CompressedChunks[index];
                var evidence = plan.Chunks[index];
                var logicalLength = checked((int)(evidence.ReceivesLogicalAppend
                    ? evidence.PlannedReplacementLogicalBytes
                    : chunk.UncompressedSize));
                result[index] = EncodeChunk(
                    summary, chunk, evidence,
                    logical.AsSpan(checked((int)chunk.UncompressedOffset), logicalLength));
            }
            return result;
        }

        IReadOnlyList<CompressedChunk> PlanChunks(IReadOnlyDictionary<int, EncodedChunk> encodedChunks)
        {
            var result = new List<CompressedChunk>(summary.CompressedChunks.Count);
            long outputCursor = firstPhysicalOffset;
            long previousSourceEnd = firstPhysicalOffset;
            for (var index = 0; index < summary.CompressedChunks.Count; index++)
            {
                var sourceChunk = summary.CompressedChunks[index];
                var gap = checked((long)sourceChunk.CompressedOffset - previousSourceEnd);
                outputCursor = checked(outputCursor + gap);
                long logicalBytes = encodedChunks.TryGetValue(index, out var rebuilt)
                    ? rebuilt.LogicalBytes
                    : sourceChunk.UncompressedSize;
                var physicalBytes = rebuilt?.PhysicalBytes.LongLength ?? sourceChunk.CompressedSize;
                result.Add(new CompressedChunk(
                    sourceChunk.UncompressedOffset,
                    checked((uint)logicalBytes),
                    checked((uint)outputCursor),
                    checked((uint)physicalBytes),
                    sourceChunk.LostArkFlags));
                outputCursor = checked(outputCursor + physicalBytes);
                previousSourceEnd = checked((long)sourceChunk.CompressedEnd);
            }
            return result;
        }

        var prefix = new byte[checked((int)firstPhysicalOffset)];
        await using (var source = new FileStream(
                         sourcePath, FileMode.Open, FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete, BufferBytes,
                         FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            await source.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        }
        var patches = new List<ReplacementChunkTablePatchEvidence>(plannedChunks.Count * 5);
        ApplyChunkTable(prefix, plannedChunks, patches, logicalPrefixOnly: false);

        void ApplyChunkTable(
            byte[] destination,
            IReadOnlyList<CompressedChunk> outputChunks,
            List<ReplacementChunkTablePatchEvidence>? evidence,
            bool logicalPrefixOnly)
        {
            for (var index = 0; index < outputChunks.Count; index++)
            {
                var sourceChunk = summary.CompressedChunks[index];
                var outputChunk = outputChunks[index];
                var recordOffset = summary.CompressedChunkTableOffset + index * 20L;
                PatchField(index, "UncompressedOffset", recordOffset, sourceChunk.UncompressedOffset, outputChunk.UncompressedOffset);
                PatchField(index, "UncompressedSize", recordOffset + 4, sourceChunk.UncompressedSize, outputChunk.UncompressedSize);
                PatchField(index, "CompressedOffset", recordOffset + 8, sourceChunk.CompressedOffset, outputChunk.CompressedOffset);
                PatchField(index, "CompressedSize", recordOffset + 12, sourceChunk.CompressedSize, outputChunk.CompressedSize);
                PatchField(index, "LostArkFlags", recordOffset + 16, sourceChunk.LostArkFlags, outputChunk.LostArkFlags);
            }

            void PatchField(int chunkIndex, string field, long offset, uint sourceValue, uint outputValue)
            {
                // The physical chunk table may extend beyond the raw logical prefix.
                // Bytes at those same numeric offsets are decoded chunk payload, not
                // the physical table, and must never receive physical metadata.
                if (logicalPrefixOnly && offset + sizeof(uint) > firstLogicalOffset) return;
                BinaryPrimitives.WriteUInt32LittleEndian(
                    destination.AsSpan(checked((int)offset), sizeof(uint)), outputValue);
                evidence?.Add(new ReplacementChunkTablePatchEvidence(
                    chunkIndex, field, offset, sourceValue, outputValue, sourceValue != outputValue));
            }
        }

        var evidenceList = new List<ReplacementChunkWriteEvidence>(plannedChunks.Count);
        await using var sourceStream = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferBytes,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        await using var output = new FileStream(
            outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        long previousSourceEnd = firstPhysicalOffset;
        for (var index = 0; index < plannedChunks.Count; index++)
        {
            var sourceChunk = summary.CompressedChunks[index];
            var outputChunk = plannedChunks[index];
            var gap = checked((long)sourceChunk.CompressedOffset - previousSourceEnd);
            if (gap > 0)
            {
                sourceStream.Position = previousSourceEnd;
                await CopyAsync(sourceStream, output, gap, cancellationToken).ConfigureAwait(false);
            }
            if (output.Position != outputChunk.CompressedOffset)
            {
                Refuse("RUB023", $"Chunk {index} output offset does not match its planned table entry.");
            }

            byte[] physicalBytes;
            bool rebuilt;
            IReadOnlyList<ReplacementChunkBlockEvidence> blockEvidence;
            if (encoded.TryGetValue(index, out var rebuiltChunk))
            {
                physicalBytes = rebuiltChunk.PhysicalBytes;
                rebuilt = true;
                blockEvidence = rebuiltChunk.Blocks;
            }
            else
            {
                physicalBytes = new byte[checked((int)sourceChunk.CompressedSize)];
                sourceStream.Position = sourceChunk.CompressedOffset;
                await sourceStream.ReadExactlyAsync(physicalBytes, cancellationToken).ConfigureAwait(false);
                if (!Hash(physicalBytes).Equals(plan.Chunks[index].PhysicalSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Refuse("RUB024", $"Unaffected source chunk {index} changed after planning.");
                }
                rebuilt = false;
                blockEvidence = [];
            }
            await output.WriteAsync(physicalBytes, cancellationToken).ConfigureAwait(false);
            var logicalLength = checked((int)outputChunk.UncompressedSize);
            var logicalHash = Hash(expectedLogical.AsSpan(checked((int)outputChunk.UncompressedOffset), logicalLength));
            evidenceList.Add(new ReplacementChunkWriteEvidence(
                index,
                sourceChunk.UncompressedOffset,
                sourceChunk.UncompressedSize,
                outputChunk.UncompressedOffset,
                outputChunk.UncompressedSize,
                sourceChunk.CompressedOffset,
                sourceChunk.CompressedSize,
                outputChunk.CompressedOffset,
                outputChunk.CompressedSize,
                outputChunk.LostArkFlags,
                rebuilt ? plan.Chunks[index].ReplacementAction : "Copied source chunk byte-for-byte.",
                rebuilt,
                !rebuilt,
                Hash(physicalBytes),
                logicalHash,
                blockEvidence));
            previousSourceEnd = checked((long)sourceChunk.CompressedEnd);
        }

        var tailBytes = summary.PhysicalFileSize - previousSourceEnd;
        if (tailBytes > 0)
        {
            sourceStream.Position = previousSourceEnd;
            await CopyAsync(sourceStream, output, tailBytes, cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new BuildEvidence(expectedLogical, evidenceList, patches);
    }

    private EncodedChunk EncodeChunk(
        PackageSummary summary,
        CompressedChunk sourceChunk,
        PhysicalChunkPlan plan,
        ReadOnlySpan<byte> logical,
        bool forceLiteralOnlyLz4 = false)
    {
        if (sourceChunk.LostArkFlags == 0 && plan.StorageKind.StartsWith("Raw", StringComparison.Ordinal))
        {
            var raw = logical.ToArray();
            return new EncodedChunk(raw, raw.Length, []);
        }
        if (!plan.BlockSize.HasValue || plan.BlockSize.Value <= 0)
        {
            Refuse("RUB025", $"Affected chunk {plan.ChunkIndex} has no reusable block geometry.");
        }
        if (sourceChunk.LostArkFlags != 0 && sourceChunk.LostArkFlags != 1)
        {
            Refuse("RUB026", $"Affected chunk {plan.ChunkIndex} uses unsupported protection flags.");
        }
        if (sourceChunk.LostArkFlags == 1 && !_protectionProvider.CanProtect(summary, sourceChunk))
        {
            Refuse("RUB027", $"Affected chunk {plan.ChunkIndex} is protected but no write provider is configured.");
        }

        var blocks = new List<(byte[] Physical, byte[] Plain, int RawBytes, bool StoredRaw)>();
        var evidence = new List<ReplacementChunkBlockEvidence>();
        for (var offset = 0; offset < logical.Length; offset += plan.BlockSize.Value)
        {
            var count = Math.Min(plan.BlockSize.Value, logical.Length - offset);
            var raw = logical.Slice(offset, count);
            var compressed = forceLiteralOnlyLz4
                ? Lz4BlockEncoder.EncodeLiteralOnly(raw)
                : Lz4BlockEncoder.Encode(raw);
            var storedRaw = compressed.Length >= raw.Length;
            var plain = storedRaw ? raw.ToArray() : compressed;
            var physical = plain.ToArray();
            if (sourceChunk.LostArkFlags == 1)
            {
                _protectionProvider.ProtectBlock(summary, sourceChunk, blocks.Count, physical);
            }
            var decodedStored = physical.ToArray();
            if (sourceChunk.LostArkFlags == 1)
            {
                _protectionProvider.UnprotectBlock(summary, sourceChunk, blocks.Count, decodedStored);
            }
            var decoded = storedRaw
                ? decodedStored
                : Lz4BlockDecoder.Decode(decodedStored, count);
            var verified = decoded.AsSpan().SequenceEqual(raw);
            if (!verified)
            {
                Refuse("RUB028", $"Encoded block {blocks.Count} in chunk {plan.ChunkIndex} failed decode-back.");
            }
            var protectedPrefix = sourceChunk.LostArkFlags == 1
                ? Math.Min(AesEcbPrefixProtectionProvider.ProtectedPrefixBytes, physical.Length)
                : 0;
            protectedPrefix -= protectedPrefix % 16;
            evidence.Add(new ReplacementChunkBlockEvidence(
                blocks.Count, count, plain.Length, protectedPrefix, storedRaw,
                Hash(plain), Hash(physical), true));
            blocks.Add((physical, plain, count, storedRaw));
        }

        var storedTotal = blocks.Sum(block => block.Physical.Length);
        using var stream = new MemoryStream(16 + blocks.Count * 8 + storedTotal);
        using var writer = new BinaryWriter(stream);
        writer.Write(PackageConstants.UnrealPackageTag);
        writer.Write(plan.BlockSize.Value);
        writer.Write(storedTotal);
        writer.Write(logical.Length);
        foreach (var block in blocks)
        {
            writer.Write(block.Physical.Length);
            writer.Write(block.RawBytes);
        }
        foreach (var block in blocks) writer.Write(block.Physical);
        return new EncodedChunk(stream.ToArray(), logical.Length, evidence);
    }

    private async Task<byte[]> ReadLogicalAsync(
        PackageSummary summary,
        CancellationToken cancellationToken)
    {
        using var logical = new LogicalPackageStream(summary, _protectionProvider);
        var output = new byte[checked((int)logical.Length)];
        await logical.ReadExactlyAsync(output, cancellationToken).ConfigureAwait(false);
        return output;
    }

    private static bool ExportsEquivalent(UnrealObjectExport left, UnrealObjectExport right) =>
        left.TableIndex == right.TableIndex &&
        left.ClassIndex == right.ClassIndex &&
        left.SuperIndex == right.SuperIndex &&
        left.OuterIndex == right.OuterIndex &&
        left.ObjectName == right.ObjectName &&
        left.ArchetypeIndex == right.ArchetypeIndex &&
        left.ObjectFlags == right.ObjectFlags &&
        left.SerialSize == right.SerialSize &&
        left.SerialOffset == right.SerialOffset &&
        left.ExportFlags == right.ExportFlags &&
        left.NetObjectCounts.SequenceEqual(right.NetObjectCounts) &&
        left.PackageGuid == right.PackageGuid &&
        left.UnknownTail == right.UnknownTail;

    private static bool AssetIdentitiesEquivalent(AssetCatalogEntry left, AssetCatalogEntry right) =>
        left.ExportIndex == right.ExportIndex &&
        left.ObjectName.Equals(right.ObjectName, StringComparison.Ordinal) &&
        left.ClassName.Equals(right.ClassName, StringComparison.Ordinal) &&
        left.Kind == right.Kind;

    private static async Task CopyAsync(
        Stream source,
        Stream output,
        long bytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferBytes];
        long remaining = bytes;
        while (remaining > 0)
        {
            var count = checked((int)Math.Min(buffer.Length, remaining));
            var read = await source.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("A source package region was truncated.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
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

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
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
        throw new ReplacementUpkBuildRefusalException(code, message);

    private sealed record EncodedChunk(
        byte[] PhysicalBytes,
        int LogicalBytes,
        IReadOnlyList<ReplacementChunkBlockEvidence> Blocks);

    private sealed record BuildEvidence(
        byte[] ExpectedLogicalBytes,
        IReadOnlyList<ReplacementChunkWriteEvidence> Chunks,
        IReadOnlyList<ReplacementChunkTablePatchEvidence> TablePatches);
}
