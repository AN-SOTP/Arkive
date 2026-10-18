using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Compression;
using Arkive.Core.Inspection;
using Arkive.Core.Packages;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Validation;

namespace Arkive.Core.Meshes;

public sealed record SkeletalMeshStagedUpkRequest(
    string NativeEncodingManifestPath,
    string OutputDirectory);

public sealed record SkeletalMeshStagedUpkBlockEvidence(
    int BlockIndex,
    int LogicalBytes,
    int StoredBytes,
    int ProtectedPrefixBytes,
    bool StoredRaw,
    string PhysicalSha256,
    bool DecodeBackPassed);

public sealed record SkeletalMeshStagedUpkChunkEvidence(
    int ChunkIndex,
    uint LogicalOffset,
    uint LogicalBytes,
    uint SourcePhysicalOffset,
    uint SourcePhysicalBytes,
    uint OutputPhysicalOffset,
    uint OutputPhysicalBytes,
    uint LostArkFlags,
    bool ContainsTargetExport,
    bool Rebuilt,
    bool SourcePhysicalBytesRetained,
    string OutputPhysicalSha256,
    string OutputLogicalSha256,
    IReadOnlyList<SkeletalMeshStagedUpkBlockEvidence> Blocks);

public sealed record SkeletalMeshStagedUpkTablePatchEvidence(
    int ChunkIndex,
    string Field,
    long PhysicalOffset,
    uint SourceValue,
    uint OutputValue,
    bool Changed);

public sealed record SkeletalMeshStagedUpkCheck(
    string Code,
    string Status,
    string Message);

public sealed record SkeletalMeshStagedUpkManifest(
    int SchemaVersion,
    string BuildId,
    DateTimeOffset CreatedAt,
    string NativeEncodingManifestPath,
    string NativeEncodingId,
    string SourcePackagePath,
    string SourcePackageSha256,
    long SourcePhysicalPackageBytes,
    string OutputUpkRelativePath,
    string OutputPackageSha256,
    long OutputPhysicalPackageBytes,
    string SourceLogicalPackageSha256,
    string ExpectedLogicalPackageSha256,
    string OutputLogicalPackageSha256,
    long LogicalPackageBytes,
    string PhysicalPackageName,
    string PackageAlias,
    string ClassName,
    string ObjectName,
    int NativeExportIndex,
    long SerialOffset,
    long SerialSize,
    string SourceExportSha256,
    string EncodedExportSha256,
    int ChunkCount,
    int RebuiltChunkCount,
    int ByteIdenticalChunkCount,
    int ProtectedRebuiltChunkCount,
    int EncodedBlockCount,
    bool SameSizeInPlaceReplacement,
    bool ExportTableUnchanged,
    bool AllUnrelatedLogicalBytesIdentical,
    bool AllUnaffectedChunksByteIdentical,
    bool AllEncodedBlocksDecodeBack,
    bool ExpectedLogicalImageMatched,
    bool AssetCatalogueEquivalent,
    bool StructuralValidationPassed,
    bool TargetNativeDecodePassed,
    bool SourcePackageIdentityRetained,
    bool OutputContainsReplacement,
    bool StagingPackageDiffersFromSource,
    bool UmodelPreviewReady,
    bool DeploymentReady,
    bool PackageWasModified,
    IReadOnlyList<SkeletalMeshStagedUpkCheck> Checks,
    IReadOnlyList<SkeletalMeshStagedUpkChunkEvidence> Chunks,
    IReadOnlyList<SkeletalMeshStagedUpkTablePatchEvidence> ChunkTablePatches,
    IReadOnlyList<ValidationIssue> ValidationIssues,
    double DurationSeconds);

public sealed record SkeletalMeshStagedUpkResult(
    string OutputDirectory,
    string ManifestPath,
    string OutputUpkPath,
    SkeletalMeshStagedUpkManifest Manifest,
    TimeSpan Duration);

public sealed record SkeletalMeshStagedUpkVerification(
    string ManifestPath,
    string OutputUpkPath,
    SkeletalMeshStagedUpkManifest Manifest);

public sealed record SerializedExportStagedUpkRequest(
    string SourcePackagePath,
    string ExpectedSourcePackageSha256,
    string PhysicalPackageName,
    string PackageAlias,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    string SourceExportSha256,
    string CandidateExportPath,
    string CandidateExportSha256,
    string VerificationEvidencePath,
    string OutputDirectory);

public sealed record SerializedExportStagedUpkManifest(
    int SchemaVersion,
    string BuildId,
    DateTimeOffset CreatedAt,
    string VerificationEvidencePath,
    string SourcePackagePath,
    string SourcePackageSha256,
    long SourcePhysicalPackageBytes,
    string OutputUpkRelativePath,
    string OutputPackageSha256,
    long OutputPhysicalPackageBytes,
    string PhysicalPackageName,
    string PackageAlias,
    string ClassName,
    string ObjectName,
    int NativeExportIndex,
    long SerialOffset,
    long SerialSize,
    string SourceExportSha256,
    string CandidateExportSha256,
    int ChangedByteCount,
    bool SameSizeInPlaceReplacement,
    bool ExportTableUnchanged,
    bool AllUnrelatedLogicalBytesIdentical,
    bool AllUnaffectedChunksByteIdentical,
    bool AllEncodedBlocksDecodeBack,
    bool ExpectedLogicalImageMatched,
    bool AssetCatalogueEquivalent,
    bool StructuralValidationPassed,
    bool SourcePackageIdentityRetained,
    bool OutputContainsReplacement,
    bool StagingPackageDiffersFromSource,
    bool UmodelPreviewReady,
    bool DeploymentReady,
    bool PackageWasModified,
    IReadOnlyList<SkeletalMeshStagedUpkChunkEvidence> Chunks,
    IReadOnlyList<SkeletalMeshStagedUpkTablePatchEvidence> ChunkTablePatches,
    IReadOnlyList<ValidationIssue> ValidationIssues,
    double DurationSeconds);

/// <summary>One export of a multi-export same-size replacement and the bytes it gets.</summary>
public sealed record SerializedExportReplacement(
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    string SourceExportSha256,
    byte[] Candidate);

/// <summary>Several exports of one package replaced by same-size candidates in one staged copy.</summary>
public sealed record SerializedExportsStagedUpkRequest(
    string SourcePackagePath,
    string ExpectedSourcePackageSha256,
    string PhysicalPackageName,
    string PackageAlias,
    IReadOnlyList<SerializedExportReplacement> Exports,
    string VerificationEvidencePath,
    string OutputDirectory);

public sealed record SerializedExportsStagedUpkExport(
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    string SourceExportSha256,
    string CandidateExportSha256,
    int ChangedByteCount);

public sealed record SerializedExportsStagedUpkManifest(
    int SchemaVersion,
    string BuildId,
    DateTimeOffset CreatedAt,
    string VerificationEvidencePath,
    string SourcePackagePath,
    string SourcePackageSha256,
    string OutputUpkRelativePath,
    string OutputPackageSha256,
    string PhysicalPackageName,
    string PackageAlias,
    IReadOnlyList<SerializedExportsStagedUpkExport> Exports,
    bool AllUnrelatedLogicalBytesIdentical,
    bool AllEncodedBlocksDecodeBack,
    bool AssetCatalogueEquivalent,
    bool StructuralValidationPassed,
    IReadOnlyList<SkeletalMeshStagedUpkChunkEvidence> Chunks,
    IReadOnlyList<SkeletalMeshStagedUpkTablePatchEvidence> ChunkTablePatches,
    IReadOnlyList<ValidationIssue> ValidationIssues,
    double DurationSeconds);

public sealed record SerializedExportsStagedUpkResult(
    string OutputDirectory,
    string ManifestPath,
    string OutputUpkPath,
    SerializedExportsStagedUpkManifest Manifest);

public sealed record SerializedExportStagedUpkResult(
    string OutputDirectory,
    string ManifestPath,
    string OutputUpkPath,
    SerializedExportStagedUpkManifest Manifest,
    TimeSpan Duration);

public sealed record SkeletalMeshTopologyStagedUpkRequest(
    string TopologyCompilationManifestPath,
    string OutputDirectory);

/// <summary>
/// Asset-neutral request for a verified variable-length serialized export. The
/// caller owns semantic compilation; this service owns package append/retarget,
/// compression decode-back, catalogue isolation, and native StaticMesh decode.
/// </summary>
public sealed record VariableSerializedExportStagedUpkRequest(
    string SourcePackagePath,
    string ExpectedSourcePackageSha256,
    string PhysicalPackageName,
    string PackageAlias,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    string SourceExportSha256,
    string CandidateExportPath,
    string CandidateExportSha256,
    string VerificationEvidencePath,
    long NativeDataRelativeOffset,
    string OutputDirectory);

public sealed record SkeletalMeshTopologyExportTablePatchEvidence(
    int ExportIndex,
    string Field,
    long LogicalOffset,
    int SourceValue,
    int OutputValue,
    bool Changed);

public sealed record SkeletalMeshTopologyStagedUpkManifest(
    int SchemaVersion,
    string BuildId,
    DateTimeOffset CreatedAt,
    string TopologyCompilationManifestPath,
    string TopologyCompilationId,
    string SourcePackagePath,
    string SourcePackageSha256,
    long SourcePhysicalPackageBytes,
    long SourceLogicalPackageBytes,
    string SourceLogicalPackageSha256,
    string OutputUpkRelativePath,
    string OutputPackageSha256,
    long OutputPhysicalPackageBytes,
    long OutputLogicalPackageBytes,
    string OutputLogicalPackageSha256,
    string PhysicalPackageName,
    string PackageAlias,
    string ClassName,
    string ObjectName,
    int NativeExportIndex,
    long OriginalSerialOffset,
    long OriginalSerialSize,
    long AppendedSerialOffset,
    long AppendedSerialSize,
    long SerialSizeDelta,
    int AppendPaddingBytes,
    string SourceExportSha256,
    string CompiledExportSha256,
    int ChunkCount,
    int RebuiltChunkCount,
    int ByteIdenticalChunkCount,
    int ProtectedRebuiltChunkCount,
    int EncodedBlockCount,
    bool AppendAndRetargetReplacement,
    bool ExportTablePatched,
    bool OriginalExportBytesRetained,
    bool TargetExportRetargeted,
    bool ReplacementExportBytesVerified,
    bool AllUnplannedSourceBytesIdentical,
    bool UnrelatedExportsEquivalent,
    bool AssetCatalogueShapeRetained,
    bool AllUnaffectedChunksByteIdentical,
    bool AllEncodedBlocksDecodeBack,
    bool ExpectedLogicalImageMatched,
    bool StructuralValidationPassed,
    bool TargetNativeDecodePassed,
    bool SourcePackageIdentityRetained,
    bool OutputContainsReplacement,
    bool StagingPackageDiffersFromSource,
    bool UmodelPreviewReady,
    bool DeploymentReady,
    bool PackageWasModified,
    IReadOnlyList<SkeletalMeshStagedUpkCheck> Checks,
    IReadOnlyList<SkeletalMeshTopologyExportTablePatchEvidence> ExportTablePatches,
    IReadOnlyList<SkeletalMeshStagedUpkChunkEvidence> Chunks,
    IReadOnlyList<SkeletalMeshStagedUpkTablePatchEvidence> ChunkTablePatches,
    IReadOnlyList<ValidationIssue> ValidationIssues,
    double DurationSeconds);

public sealed record SkeletalMeshTopologyStagedUpkResult(
    string OutputDirectory,
    string ManifestPath,
    string OutputUpkPath,
    SkeletalMeshTopologyStagedUpkManifest Manifest,
    TimeSpan Duration);

public sealed record SkeletalMeshTopologyStagedUpkVerification(
    string ManifestPath,
    string OutputUpkPath,
    SkeletalMeshTopologyStagedUpkManifest Manifest);

public sealed class SkeletalMeshStagedUpkRefusalException : NotSupportedException
{
    public SkeletalMeshStagedUpkRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Replaces one verified, same-length native SkeletalMesh export inside an isolated
/// UPK. The export table is deliberately left untouched. Only chunks overlapping
/// the export are recompressed; all other stored chunks are copied byte-for-byte.
/// The result is for inspection only and is never written into the game directory.
/// </summary>
public sealed class SkeletalMeshStagedUpkService
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "arkive-skeletalmesh-staged-upk.json";
    public const string SerializedExportManifestFileName = "arkive-serialized-export-staged-upk.json";
    public const string SerializedExportsManifestFileName = "arkive-serialized-exports-staged-upk.json";
    public const string TopologyManifestFileName = "arkive-skeletalmesh-topology-staged-upk.json";
    private const int BufferBytes = 1024 * 1024;
    private const int MaximumBlockSize = 256 * 1024 * 1024;

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

    public SkeletalMeshStagedUpkService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _summaryReader = summaryReader ?? new UpkPackageReader();
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(_summaryReader, _protectionProvider);
    }

    public static async Task<SkeletalMeshStagedUpkManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = RequireFile(path, "SMS001", "staged mesh UPK manifest");
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<SkeletalMeshStagedUpkManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new SkeletalMeshStagedUpkRefusalException(
                           "SMS002", "The staged mesh UPK manifest was empty.");
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            Refuse("SMS003", $"Staged mesh UPK schema {manifest.SchemaVersion} is unsupported.");
        return manifest;
    }

    public static async Task<SkeletalMeshStagedUpkVerification> VerifyOutputAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var fullManifest = RequireFile(manifestPath, "SMS004", "staged mesh UPK manifest");
        var manifest = await LoadManifestAsync(fullManifest, cancellationToken).ConfigureAwait(false);
        if (!manifest.SameSizeInPlaceReplacement || !manifest.ExportTableUnchanged ||
            !manifest.AllUnrelatedLogicalBytesIdentical ||
            !manifest.AllUnaffectedChunksByteIdentical || !manifest.AllEncodedBlocksDecodeBack ||
            !manifest.ExpectedLogicalImageMatched || !manifest.AssetCatalogueEquivalent ||
            !manifest.StructuralValidationPassed || !manifest.TargetNativeDecodePassed ||
            !manifest.SourcePackageIdentityRetained || !manifest.OutputContainsReplacement ||
            !manifest.StagingPackageDiffersFromSource || !manifest.UmodelPreviewReady ||
            manifest.DeploymentReady || manifest.PackageWasModified)
            Refuse("SMS005", "The manifest is not a complete staging-only SkeletalMesh proof.");

        var outputPath = ResolveWithin(
            Path.GetDirectoryName(fullManifest)!, manifest.OutputUpkRelativePath, "SMS006");
        var info = new FileInfo(outputPath);
        if (info.Length != manifest.OutputPhysicalPackageBytes)
            Refuse("SMS007", "The staged mesh UPK length changed after validation.");
        var hash = await HashFileAsync(outputPath, cancellationToken).ConfigureAwait(false);
        if (!hash.Equals(manifest.OutputPackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SMS008", "The staged mesh UPK SHA-256 changed after validation.");
        return new SkeletalMeshStagedUpkVerification(fullManifest, outputPath, manifest);
    }

    public static async Task<SkeletalMeshTopologyStagedUpkManifest> LoadTopologyManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = RequireFile(path, "SMV001", "topology staged-UPK manifest");
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<SkeletalMeshTopologyStagedUpkManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new SkeletalMeshStagedUpkRefusalException(
                           "SMV002", "The topology staged-UPK manifest was empty.");
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            Refuse("SMV003", $"Topology staged-UPK schema {manifest.SchemaVersion} is unsupported.");
        return manifest;
    }

    public static async Task<SkeletalMeshTopologyStagedUpkVerification> VerifyTopologyOutputAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var fullManifest = RequireFile(manifestPath, "SMV004", "topology staged-UPK manifest");
        var manifest = await LoadTopologyManifestAsync(fullManifest, cancellationToken)
            .ConfigureAwait(false);
        if (!manifest.AppendAndRetargetReplacement || !manifest.ExportTablePatched ||
            !manifest.OriginalExportBytesRetained || !manifest.TargetExportRetargeted ||
            !manifest.ReplacementExportBytesVerified || !manifest.AllUnplannedSourceBytesIdentical ||
            !manifest.UnrelatedExportsEquivalent || !manifest.AssetCatalogueShapeRetained ||
            !manifest.AllUnaffectedChunksByteIdentical || !manifest.AllEncodedBlocksDecodeBack ||
            !manifest.ExpectedLogicalImageMatched || !manifest.StructuralValidationPassed ||
            !manifest.TargetNativeDecodePassed || !manifest.SourcePackageIdentityRetained ||
            !manifest.OutputContainsReplacement || !manifest.StagingPackageDiffersFromSource ||
            !manifest.UmodelPreviewReady || manifest.DeploymentReady || manifest.PackageWasModified)
            Refuse("SMV005", "The manifest is not a complete staging-only topology replacement proof.");

        var outputPath = ResolveWithin(
            Path.GetDirectoryName(fullManifest)!, manifest.OutputUpkRelativePath, "SMV006");
        var info = new FileInfo(outputPath);
        if (info.Length != manifest.OutputPhysicalPackageBytes)
            Refuse("SMV007", "The topology staged UPK length changed after validation.");
        var hash = await HashFileAsync(outputPath, cancellationToken).ConfigureAwait(false);
        if (!hash.Equals(manifest.OutputPackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SMV008", "The topology staged UPK SHA-256 changed after validation.");
        return new SkeletalMeshTopologyStagedUpkVerification(fullManifest, outputPath, manifest);
    }

    /// <summary>
    /// Applies an already verified same-size serialized export candidate. This is
    /// the package-layer primitive used by bounded material parameter projects;
    /// semantic candidate proof remains the responsibility of the asset-specific
    /// producer named by VerificationEvidencePath.
    /// </summary>
    public async Task<SerializedExportStagedUpkResult> BuildSerializedExportAsync(
        SerializedExportStagedUpkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var sourcePath = RequireFile(request.SourcePackagePath, "SXS010", "source package");
        var candidatePath = RequireFile(request.CandidateExportPath, "SXS011", "serialized export candidate");
        var evidencePath = RequireFile(request.VerificationEvidencePath, "SXS012", "candidate verification manifest");
        var sourceHashBefore = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!sourceHashBefore.Equals(request.ExpectedSourcePackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SXS013", "The cumulative source UPK does not match the requested package stage.");

        var candidate = await File.ReadAllBytesAsync(candidatePath, cancellationToken).ConfigureAwait(false);
        if (candidate.LongLength != request.SerialSize ||
            !Hash(candidate).Equals(request.CandidateExportSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SXS014", "The serialized export candidate changed after asset-specific verification.");

        var sourceSummary = _summaryReader.Read(sourcePath);
        if (!sourceSummary.IsWesternLostArkProfile)
            Refuse("SXS015", "Only the Western Lost Ark 868/16 package profile is supported.");
        var sourceCatalog = _catalogReader.Read(sourceSummary);
        var targetSource = sourceCatalog.Exports.SingleOrDefault(item =>
            item.TableIndex == request.NativeExportIndex);
        var targetAsset = sourceCatalog.Assets.SingleOrDefault(item =>
            item.ExportIndex == request.NativeExportIndex);
        if (targetSource is null || targetAsset is null ||
            targetSource.SerialOffset != request.SerialOffset ||
            targetSource.SerialSize != request.SerialSize ||
            !targetAsset.ObjectName.Equals(request.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            !targetAsset.ClassName.Equals(request.ClassName, StringComparison.OrdinalIgnoreCase))
            Refuse("SXS016", "The current export table no longer identifies the requested serialized export.");

        var sourceLogical = await ReadLogicalAsync(sourceSummary, cancellationToken).ConfigureAwait(false);
        var serialOffset = checked((int)request.SerialOffset);
        var serialSize = checked((int)request.SerialSize);
        if (serialOffset < 0 || serialOffset > sourceLogical.Length - serialSize ||
            !Hash(sourceLogical.AsSpan(serialOffset, serialSize)).Equals(
                request.SourceExportSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SXS017", "The source export bytes no longer match the candidate's proven baseline.");
        var changedBytes = sourceLogical.AsSpan(serialOffset, serialSize)
            .ToArray().Where((value, index) => value != candidate[index]).Count();
        if (changedBytes == 0)
            Refuse("SXS018", "The serialized export candidate is byte-identical to its source; change at least one parameter first.");

        var desiredLogical = sourceLogical.ToArray();
        candidate.CopyTo(desiredLogical, serialOffset);
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var outputName = SafePhysicalName(request.PhysicalPackageName);
        var outputPath = Path.Combine(outputDirectory, outputName);
        if (Path.GetFullPath(outputPath).Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            Refuse("SXS019", "The staging output cannot be the source UPK.");
        var temporaryPath = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var build = sourceSummary.CompressedChunks.Count == 0
                ? await BuildUncompressedAsync(desiredLogical, temporaryPath, cancellationToken).ConfigureAwait(false)
                : await BuildCompressedAsync(sourcePath, sourceSummary, desiredLogical,
                    request.SerialOffset, request.SerialSize, temporaryPath, cancellationToken).ConfigureAwait(false);
            var outputSummary = _summaryReader.Read(temporaryPath);
            var validation = PackageValidator.Validate(outputSummary);
            if (validation.Any(issue => issue.Severity == ValidationSeverity.Error))
                Refuse("SXS020", "The staged serialized-export UPK failed structural validation.");
            await VerifyPhysicalChunksAsync(sourcePath, temporaryPath, outputSummary, build.Chunks, cancellationToken)
                .ConfigureAwait(false);
            var outputLogical = await ReadLogicalAsync(outputSummary, cancellationToken).ConfigureAwait(false);
            if (!outputLogical.AsSpan().SequenceEqual(build.ExpectedLogical))
                Refuse("SXS021", "The staged UPK did not decode to the expected logical image.");
            if (!Hash(outputLogical.AsSpan(serialOffset, serialSize)).Equals(
                    request.CandidateExportSha256, StringComparison.OrdinalIgnoreCase))
                Refuse("SXS022", "The staged target export does not contain the verified candidate.");
            var unrelatedIdentical = VerifyUnrelatedLogicalBytes(sourceLogical, outputLogical,
                serialOffset, serialSize, sourceSummary.CompressedChunks.Count == 0
                    ? []
                    : build.TablePatches.Where(item => item.Changed)
                        .Select(item => (item.PhysicalOffset, 4)).ToArray());
            if (!unrelatedIdentical)
                Refuse("SXS023", "A logical byte outside the target export and chunk-table metadata changed.");
            var outputCatalog = _catalogReader.Read(outputSummary);
            if (!CataloguesEquivalent(sourceCatalog, outputCatalog))
                Refuse("SXS024", "The asset catalogue changed during same-size export staging.");
            var outputHash = await HashFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (outputHash.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
                Refuse("SXS025", "The staged package unexpectedly matches its source package.");
            var sourceHashAfter = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
                Refuse("SXS026", "The source package changed while Arkive built the staged copy.");
            File.Move(temporaryPath, outputPath, overwrite: true);
            watch.Stop();
            var manifest = new SerializedExportStagedUpkManifest(
                CurrentSchemaVersion, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                evidencePath, sourcePath, sourceHashAfter, new FileInfo(sourcePath).Length,
                outputName, outputHash, new FileInfo(outputPath).Length,
                request.PhysicalPackageName, request.PackageAlias, request.ClassName,
                request.ObjectName, request.NativeExportIndex, request.SerialOffset,
                request.SerialSize, request.SourceExportSha256, request.CandidateExportSha256,
                changedBytes, SameSizeInPlaceReplacement: true, ExportTableUnchanged: true,
                AllUnrelatedLogicalBytesIdentical: true,
                AllUnaffectedChunksByteIdentical: build.Chunks.Where(item => !item.Rebuilt)
                    .All(item => item.SourcePhysicalBytesRetained),
                AllEncodedBlocksDecodeBack: build.Chunks.SelectMany(item => item.Blocks)
                    .All(item => item.DecodeBackPassed),
                ExpectedLogicalImageMatched: true, AssetCatalogueEquivalent: true,
                StructuralValidationPassed: true, SourcePackageIdentityRetained: true,
                OutputContainsReplacement: true, StagingPackageDiffersFromSource: true,
                UmodelPreviewReady: true, DeploymentReady: false, PackageWasModified: false,
                build.Chunks, build.TablePatches, validation, watch.Elapsed.TotalSeconds);
            var manifestPath = Path.Combine(outputDirectory, SerializedExportManifestFileName);
            await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            return new SerializedExportStagedUpkResult(
                outputDirectory, manifestPath, outputPath, manifest, watch.Elapsed);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Writes several same-size export candidates (the modules of one particle effect, for example) into one staged
    /// copy with the same checks as a single export: each export still holds its proven source bytes, only those
    /// exports and the chunk table change, every rebuilt block decodes back, and the catalogue is unchanged.
    /// </summary>
    public async Task<SerializedExportsStagedUpkResult> BuildSerializedExportsAsync(
        SerializedExportsStagedUpkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        if (request.Exports.Count == 0) Refuse("SXM010", "No exports were given to replace.");
        var sourcePath = RequireFile(request.SourcePackagePath, "SXM011", "source package");
        var evidencePath = RequireFile(request.VerificationEvidencePath, "SXM012", "candidate verification manifest");
        var sourceHashBefore = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!sourceHashBefore.Equals(request.ExpectedSourcePackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SXM013", "The cumulative source UPK does not match the requested package stage.");
        if (request.Exports.GroupBy(item => item.NativeExportIndex).Any(group => group.Count() > 1))
            Refuse("SXM014", "An export was given more than once.");

        var sourceSummary = _summaryReader.Read(sourcePath);
        if (!sourceSummary.IsWesternLostArkProfile)
            Refuse("SXM015", "Only the Western Lost Ark 868/16 package profile is supported.");
        var sourceCatalog = _catalogReader.Read(sourceSummary);
        var sourceLogical = await ReadLogicalAsync(sourceSummary, cancellationToken).ConfigureAwait(false);
        var desiredLogical = sourceLogical.ToArray();
        var evidence = new List<SerializedExportsStagedUpkExport>(request.Exports.Count);
        foreach (var item in request.Exports.OrderBy(item => item.SerialOffset))
        {
            var targetSource = sourceCatalog.Exports.SingleOrDefault(export => export.TableIndex == item.NativeExportIndex);
            var targetAsset = sourceCatalog.Assets.SingleOrDefault(asset => asset.ExportIndex == item.NativeExportIndex);
            if (targetSource is null || targetAsset is null ||
                targetSource.SerialOffset != item.SerialOffset || targetSource.SerialSize != item.SerialSize ||
                !targetAsset.ObjectName.Equals(item.ObjectName, StringComparison.OrdinalIgnoreCase) ||
                !targetAsset.ClassName.Equals(item.ClassName, StringComparison.OrdinalIgnoreCase))
                Refuse("SXM016", $"The current export table no longer identifies {item.ObjectName}.");
            var offset = checked((int)item.SerialOffset);
            var size = checked((int)item.SerialSize);
            if (item.Candidate.LongLength != item.SerialSize || offset < 0 || offset > sourceLogical.Length - size)
                Refuse("SXM017", $"{item.ObjectName}'s candidate does not fit its export.");
            if (!Hash(sourceLogical.AsSpan(offset, size)).Equals(item.SourceExportSha256, StringComparison.OrdinalIgnoreCase))
                Refuse("SXM018", $"{item.ObjectName}'s source bytes no longer match the candidate's proven baseline.");
            var changed = 0;
            for (var index = 0; index < size; index++)
                if (sourceLogical[offset + index] != item.Candidate[index]) changed++;
            item.Candidate.CopyTo(desiredLogical, offset);
            evidence.Add(new(item.NativeExportIndex, item.ClassName, item.ObjectName, item.SerialOffset, item.SerialSize,
                item.SourceExportSha256.ToUpperInvariant(), Hash(item.Candidate), changed));
        }
        if (evidence.All(item => item.ChangedByteCount == 0))
            Refuse("SXM019", "Every candidate is byte-identical to its source; there is nothing to write.");

        var rangeStart = request.Exports.Min(item => item.SerialOffset);
        var rangeEnd = request.Exports.Max(item => item.SerialOffset + item.SerialSize);
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var outputName = SafePhysicalName(request.PhysicalPackageName);
        var outputPath = Path.Combine(outputDirectory, outputName);
        if (Path.GetFullPath(outputPath).Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            Refuse("SXM020", "The staging output cannot be the source UPK.");
        var temporaryPath = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var build = sourceSummary.CompressedChunks.Count == 0
                ? await BuildUncompressedAsync(desiredLogical, temporaryPath, cancellationToken).ConfigureAwait(false)
                : await BuildCompressedAsync(sourcePath, sourceSummary, desiredLogical,
                    rangeStart, rangeEnd - rangeStart, temporaryPath, cancellationToken).ConfigureAwait(false);
            var outputSummary = _summaryReader.Read(temporaryPath);
            var validation = PackageValidator.Validate(outputSummary);
            if (validation.Any(issue => issue.Severity == ValidationSeverity.Error))
                Refuse("SXM021", "The staged UPK failed structural validation.");
            await VerifyPhysicalChunksAsync(sourcePath, temporaryPath, outputSummary, build.Chunks, cancellationToken)
                .ConfigureAwait(false);
            var outputLogical = await ReadLogicalAsync(outputSummary, cancellationToken).ConfigureAwait(false);
            if (!outputLogical.AsSpan().SequenceEqual(build.ExpectedLogical))
                Refuse("SXM022", "The staged UPK did not decode to the expected logical image.");
            foreach (var item in evidence)
                if (!Hash(outputLogical.AsSpan(checked((int)item.SerialOffset), checked((int)item.SerialSize)))
                        .Equals(item.CandidateExportSha256, StringComparison.OrdinalIgnoreCase))
                    Refuse("SXM023", $"The staged {item.ObjectName} does not contain its verified candidate.");
            var metadata = sourceSummary.CompressedChunks.Count == 0
                ? []
                : build.TablePatches.Where(item => item.Changed).Select(item => (item.PhysicalOffset, 4)).ToArray();
            if (sourceLogical.Length != outputLogical.Length)
                Refuse("SXM024", "The staged UPK changed length.");
            var ranges = evidence.Select(item => (Start: item.SerialOffset, End: item.SerialOffset + item.SerialSize)).ToArray();
            for (var index = 0; index < sourceLogical.Length; index++)
            {
                if (sourceLogical[index] == outputLogical[index]) continue;
                if (ranges.Any(range => index >= range.Start && index < range.End)) continue;
                if (metadata.Any(range => index >= range.PhysicalOffset && index < range.PhysicalOffset + range.Item2)) continue;
                Refuse("SXM025", "A logical byte outside the replaced exports and chunk-table metadata changed.");
            }
            var outputCatalog = _catalogReader.Read(outputSummary);
            if (!CataloguesEquivalent(sourceCatalog, outputCatalog))
                Refuse("SXM026", "The asset catalogue changed during same-size export staging.");
            var outputHash = await HashFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            var sourceHashAfter = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
                Refuse("SXM027", "The source package changed while Arkive built the staged copy.");
            File.Move(temporaryPath, outputPath, overwrite: true);
            watch.Stop();
            var manifest = new SerializedExportsStagedUpkManifest(
                CurrentSchemaVersion, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, evidencePath, sourcePath,
                sourceHashAfter, outputName, outputHash, request.PhysicalPackageName, request.PackageAlias, evidence,
                AllUnrelatedLogicalBytesIdentical: true,
                AllEncodedBlocksDecodeBack: build.Chunks.SelectMany(item => item.Blocks).All(item => item.DecodeBackPassed),
                AssetCatalogueEquivalent: true, StructuralValidationPassed: true,
                build.Chunks, build.TablePatches, validation, watch.Elapsed.TotalSeconds);
            var manifestPath = Path.Combine(outputDirectory, SerializedExportsManifestFileName);
            await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            return new SerializedExportsStagedUpkResult(outputDirectory, manifestPath, outputPath, manifest);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Appends a verified topology-compiled SkeletalMesh export to an isolated
    /// package copy and retargets only the selected export-table record. Existing
    /// serialized exports keep their original offsets and bytes. The result is a
    /// staging/UModel proof; deployment remains disabled.
    /// </summary>
    public async Task<SkeletalMeshTopologyStagedUpkResult> BuildTopologyAsync(
        SkeletalMeshTopologyStagedUpkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var compilationPath = RequireFile(
            request.TopologyCompilationManifestPath, "SMV010", "topology compilation manifest");
        var compilation = await SkeletalMeshTopologyCompilerService.LoadManifestAsync(
            compilationPath, cancellationToken).ConfigureAwait(false);
        if (!compilation.DecodeBackPassed || !compilation.PrefixPreservedExceptBounds ||
            !compilation.TrailingBytesPreserved || !compilation.PackageIntegrationEnabled ||
            compilation.CompiledSerialSize <= 0 || compilation.SourceSerialSize <= 0 ||
            compilation.Preflight.Readiness != SkeletalMeshEditReadiness.ReadyForTopologyCompilation ||
            compilation.Preflight.ErrorCount != 0 ||
            compilation.Preflight.SchemaVersion != SkeletalMeshEditPreflightService.CurrentSchemaVersion ||
            compilation.Preflight.AnimationValidation is not { Passed: true })
            Refuse("SMV011", "A package-enabled, decode-back-verified topology compilation is required.");

        var compilationDirectory = Path.GetDirectoryName(compilationPath)!;
        var compiledPath = ResolveWithin(
            compilationDirectory, compilation.CompiledExportRelativePath, "SMV012");
        var compiled = await File.ReadAllBytesAsync(compiledPath, cancellationToken).ConfigureAwait(false);
        if (compiled.LongLength != compilation.CompiledSerialSize ||
            !Hash(compiled).Equals(compilation.CompiledExportSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SMV013", "The compiled topology export changed after decode-back verification.");

        var editablePath = RequireFile(
            compilation.SourceEditableManifestPath, "SMV014", "source editable manifest");
        var editable = await ReadJsonAsync<SkeletalMeshEditableExportManifest>(
            editablePath, cancellationToken).ConfigureAwait(false);
        var reconPath = RequireFile(editable.ReconnaissanceJsonPath, "SMV015", "mesh reconnaissance report");
        var recon = await ReadJsonAsync<SkeletalMeshReconnaissanceReport>(
            reconPath, cancellationToken).ConfigureAwait(false);
        if (editable.SchemaVersion != SkeletalMeshEditableExportService.CurrentSchemaVersion ||
            recon.SchemaVersion != SkeletalMeshReconnaissanceService.CurrentSchemaVersion ||
            recon.Readiness != SkeletalMeshReadiness.NoOpReady || recon.NativeLayout is null ||
            editable.NativeExportIndex != compilation.NativeExportIndex ||
            recon.NativeExportIndex != compilation.NativeExportIndex ||
            recon.SerialOffset != compilation.SerialOffset ||
            recon.SerialSize != compilation.SourceSerialSize ||
            !editable.SourceExportSha256.Equals(compilation.SourceExportSha256, StringComparison.OrdinalIgnoreCase) ||
            !recon.ExportPayloadSha256.Equals(compilation.SourceExportSha256, StringComparison.OrdinalIgnoreCase) ||
            !recon.ObjectName.Equals(compilation.ObjectName, StringComparison.OrdinalIgnoreCase))
            Refuse("SMV016", "The topology compilation, editable export, and reconnaissance identities disagree.");

        var sourcePath = RequireFile(compilation.SourcePackagePath, "SMV017", "source package");
        var sourceInfo = new FileInfo(sourcePath);
        var sourceHashBefore = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (sourceInfo.Length != recon.PackageLength ||
            sourceInfo.LastWriteTimeUtc.Ticks != recon.PackageLastWriteUtcTicks ||
            !sourceHashBefore.Equals(compilation.SourcePackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SMV018", "The source UPK changed after topology compilation.");

        var sourceSummary = _summaryReader.Read(sourcePath);
        if (!sourceSummary.IsWesternLostArkProfile)
            Refuse("SMV019", "Only the Western Lost Ark 868/16 package profile is supported.");
        var sourceCatalog = _catalogReader.Read(sourceSummary);
        var targetSource = sourceCatalog.Exports.SingleOrDefault(item =>
            item.TableIndex == compilation.NativeExportIndex);
        var targetAsset = sourceCatalog.Assets.SingleOrDefault(item =>
            item.ExportIndex == compilation.NativeExportIndex);
        if (targetSource is null || targetAsset is null ||
            targetSource.SerialOffset != compilation.SerialOffset ||
            targetSource.SerialSize != compilation.SourceSerialSize ||
            !targetAsset.ObjectName.Equals(compilation.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            !targetAsset.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            Refuse("SMV020", "The source export table no longer identifies the compiled SkeletalMesh.");

        var sourceLogical = await ReadLogicalAsync(sourceSummary, cancellationToken).ConfigureAwait(false);
        var sourceLogicalHash = Hash(sourceLogical);
        var originalOffset = checked((int)compilation.SerialOffset);
        var originalSize = checked((int)compilation.SourceSerialSize);
        if (originalOffset < 0 || originalOffset > sourceLogical.Length - originalSize ||
            !Hash(sourceLogical.AsSpan(originalOffset, originalSize)).Equals(
                compilation.SourceExportSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SMV021", "The source logical export no longer matches topology compilation evidence.");

        var rawLayout = ReadExportTableLayouts(sourceLogical, sourceSummary, sourceCatalog.Exports)
            [compilation.NativeExportIndex - 1];
        if (rawLayout.SerialOffset != compilation.SerialOffset ||
            rawLayout.SerialSize != compilation.SourceSerialSize)
            Refuse("SMV022", "The raw export-table fields disagree with the parsed target export.");

        const int appendAlignment = 4;
        var appendedOffset = AlignUp(sourceLogical.LongLength, appendAlignment);
        var appendPadding = checked((int)(appendedOffset - sourceLogical.LongLength));
        var outputLogicalBytes = checked(appendedOffset + compiled.LongLength);
        if (appendedOffset > int.MaxValue || compiled.LongLength > int.MaxValue ||
            outputLogicalBytes > int.MaxValue)
            Refuse("SMV023", "The append-and-retarget layout exceeds UE3 Int32 serial storage.");

        var desiredLogical = new byte[checked((int)outputLogicalBytes)];
        sourceLogical.CopyTo(desiredLogical, 0);
        compiled.CopyTo(desiredLogical, checked((int)appendedOffset));
        var exportPatches = new[]
        {
            new SkeletalMeshTopologyExportTablePatchEvidence(
                compilation.NativeExportIndex, "SerialSize", rawLayout.SerialSizeFieldOffset,
                rawLayout.SerialSize, checked((int)compiled.LongLength), true),
            new SkeletalMeshTopologyExportTablePatchEvidence(
                compilation.NativeExportIndex, "SerialOffset", rawLayout.SerialOffsetFieldOffset,
                rawLayout.SerialOffset, checked((int)appendedOffset), true),
        };
        BinaryPrimitives.WriteInt32LittleEndian(
            desiredLogical.AsSpan(checked((int)rawLayout.SerialSizeFieldOffset), sizeof(int)),
            checked((int)compiled.LongLength));
        BinaryPrimitives.WriteInt32LittleEndian(
            desiredLogical.AsSpan(checked((int)rawLayout.SerialOffsetFieldOffset), sizeof(int)),
            checked((int)appendedOffset));

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var outputName = SafePhysicalName(compilation.PhysicalPackageName);
        var outputPath = Path.Combine(outputDirectory, outputName);
        if (Path.GetFullPath(outputPath).Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            Refuse("SMV024", "The topology staging output cannot be the source UPK.");
        var temporaryPath = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var patchOffsets = exportPatches.Select(item => item.LogicalOffset).ToArray();
            var build = sourceSummary.CompressedChunks.Count == 0
                ? await BuildUncompressedAsync(desiredLogical, temporaryPath, cancellationToken)
                    .ConfigureAwait(false)
                : await BuildCompressedAppendAsync(
                    sourcePath, sourceSummary, desiredLogical, patchOffsets,
                    temporaryPath, cancellationToken).ConfigureAwait(false);

            var outputSummary = _summaryReader.Read(temporaryPath);
            var validation = PackageValidator.Validate(outputSummary);
            if (validation.Any(issue => issue.Severity == ValidationSeverity.Error))
                Refuse("SMV025", "The topology staged UPK failed structural package validation.");
            await VerifyPhysicalChunksAsync(
                sourcePath, temporaryPath, outputSummary, build.Chunks, cancellationToken)
                .ConfigureAwait(false);
            var outputLogical = await ReadLogicalAsync(outputSummary, cancellationToken).ConfigureAwait(false);
            if (!outputLogical.AsSpan().SequenceEqual(build.ExpectedLogical))
                Refuse("SMV026", "The topology staged UPK did not decode to its expected logical image.");

            var outputCatalog = _catalogReader.Read(outputSummary);
            var targetOutput = outputCatalog.Exports.SingleOrDefault(item =>
                item.TableIndex == compilation.NativeExportIndex);
            if (targetOutput is null || targetOutput.SerialOffset != appendedOffset ||
                targetOutput.SerialSize != compiled.LongLength)
                Refuse("SMV027", "The selected export was not retargeted to the appended topology payload.");

            var originalRetained = Hash(outputLogical.AsSpan(originalOffset, originalSize)).Equals(
                compilation.SourceExportSha256, StringComparison.OrdinalIgnoreCase);
            var replacementVerified = Hash(outputLogical.AsSpan(
                    checked((int)appendedOffset), checked((int)compiled.LongLength)))
                .Equals(compilation.CompiledExportSha256, StringComparison.OrdinalIgnoreCase);
            if (!originalRetained || !replacementVerified)
                Refuse("SMV028", "The retained original or appended topology export failed hash verification.");

            var metadataRanges = exportPatches.Select(item => (item.LogicalOffset, sizeof(int)))
                .Concat(build.TablePatches.Where(item => item.Changed &&
                                                         item.PhysicalOffset < sourceLogical.LongLength)
                    .Select(item => (item.PhysicalOffset, sizeof(uint))))
                .ToArray();
            if (!VerifyRetainedSourceBytes(sourceLogical, outputLogical, metadataRanges))
                Refuse("SMV029", "A source logical byte outside audited table fields changed.");

            var unrelatedEquivalent = sourceCatalog.Exports
                .Where(item => item.TableIndex != compilation.NativeExportIndex)
                .Zip(outputCatalog.Exports.Where(item =>
                    item.TableIndex != compilation.NativeExportIndex), ExportsEquivalent)
                .All(value => value) && sourceCatalog.Exports.Count == outputCatalog.Exports.Count;
            var catalogueShape = sourceCatalog.Names.SequenceEqual(outputCatalog.Names) &&
                                 sourceCatalog.Imports.SequenceEqual(outputCatalog.Imports) &&
                                 sourceCatalog.Assets.Zip(outputCatalog.Assets, (left, right) =>
                                         left.ExportIndex == right.ExportIndex &&
                                         left.ObjectName == right.ObjectName &&
                                         left.ClassName == right.ClassName && left.Kind == right.Kind)
                                     .All(value => value) &&
                                 sourceCatalog.Assets.Count == outputCatalog.Assets.Count;
            if (!unrelatedEquivalent || !catalogueShape)
                Refuse("SMV030", "An unrelated export or catalogue identity changed during topology staging.");

            var outputHash = await HashFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            var nativeReport = await new SkeletalMeshReconnaissanceService(
                    _summaryReader, _protectionProvider)
                .InspectAsync(new SkeletalMeshReconnaissanceRequest(
                    temporaryPath, compilation.PackageAlias, compilation.NativeExportIndex,
                    AssetKind.SkeletalMesh, targetAsset.ClassName, compilation.ObjectName,
                    appendedOffset, compiled.LongLength, outputHash), cancellationToken)
                .ConfigureAwait(false);
            var nativeDecodePassed = nativeReport.Readiness == SkeletalMeshReadiness.NoOpReady &&
                                     nativeReport.NativeLayout is not null &&
                                     nativeReport.NativeLayout.Lods.Count == compilation.Lods.Count &&
                                     !nativeReport.Issues.Any(issue =>
                                         issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
            if (!nativeDecodePassed)
                Refuse("SMV031", "The retargeted topology export failed independent native mesh parsing.");

            var outputInfo = new FileInfo(temporaryPath);
            var stagingDiffers = outputInfo.Length != sourceInfo.Length ||
                                 !outputHash.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase);
            if (!stagingDiffers)
                Refuse("SMV032", "The topology staging package unexpectedly matches its source.");
            var sourceHashAfter = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
                Refuse("SMV033", "The source UPK changed while Arkive built the topology staging copy.");

            File.Move(temporaryPath, outputPath, overwrite: true);
            watch.Stop();
            var rebuiltCount = build.Chunks.Count(item => item.Rebuilt);
            var blockCount = build.Chunks.Sum(item => item.Blocks.Count);
            var protectedCount = build.Chunks.Count(item => item.Rebuilt && item.LostArkFlags == 1);
            var checks = new[]
            {
                new SkeletalMeshStagedUpkCheck("SMV_SOURCE", "Passed",
                    $"Source UPK remained read-only at SHA-256 {sourceHashAfter}."),
                new SkeletalMeshStagedUpkCheck("SMV_APPEND", "Passed",
                    $"Retained the source export and appended {compiled.LongLength:N0} compiled bytes at logical 0x{appendedOffset:X}."),
                new SkeletalMeshStagedUpkCheck("SMV_EXPORT_TABLE", "Passed",
                    $"Patched only export {compilation.NativeExportIndex}'s SerialSize and SerialOffset fields."),
                new SkeletalMeshStagedUpkCheck("SMV_ISOLATION", "Passed",
                    $"All {sourceCatalog.Exports.Count - 1:N0} unrelated export records and all unaudited source bytes are unchanged."),
                new SkeletalMeshStagedUpkCheck("SMV_CHUNKS", "Passed",
                    $"Rebuilt {rebuiltCount:N0} affected chunk(s) and retained {build.Chunks.Count - rebuiltCount:N0} chunk(s) byte-for-byte."),
                new SkeletalMeshStagedUpkCheck("SMV_DECODE_BACK", "Passed",
                    $"All {blockCount:N0} encoded compression blocks and the retargeted native mesh decoded successfully."),
                new SkeletalMeshStagedUpkCheck("SMV_STAGING_ONLY", "Information",
                    "The topology UPK is isolated for UModel preview. It may enter unified package composition, but direct installation remains disabled during acceptance testing."),
            };
            var manifest = new SkeletalMeshTopologyStagedUpkManifest(
                CurrentSchemaVersion, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                compilationPath, compilation.CompilationId, sourcePath, sourceHashAfter,
                sourceInfo.Length, sourceLogical.LongLength, sourceLogicalHash,
                outputName, outputHash, outputInfo.Length, outputLogical.LongLength, Hash(outputLogical),
                compilation.PhysicalPackageName, compilation.PackageAlias, "SkeletalMesh",
                compilation.ObjectName, compilation.NativeExportIndex,
                compilation.SerialOffset, compilation.SourceSerialSize,
                appendedOffset, compiled.LongLength, compilation.SerialSizeDelta, appendPadding,
                compilation.SourceExportSha256, compilation.CompiledExportSha256,
                build.Chunks.Count, rebuiltCount, build.Chunks.Count - rebuiltCount,
                protectedCount, blockCount,
                AppendAndRetargetReplacement: true, ExportTablePatched: true,
                OriginalExportBytesRetained: true, TargetExportRetargeted: true,
                ReplacementExportBytesVerified: true, AllUnplannedSourceBytesIdentical: true,
                UnrelatedExportsEquivalent: true, AssetCatalogueShapeRetained: true,
                AllUnaffectedChunksByteIdentical: build.Chunks.Where(item => !item.Rebuilt)
                    .All(item => item.SourcePhysicalBytesRetained),
                AllEncodedBlocksDecodeBack: build.Chunks.SelectMany(item => item.Blocks)
                    .All(item => item.DecodeBackPassed),
                ExpectedLogicalImageMatched: true, StructuralValidationPassed: true,
                TargetNativeDecodePassed: true, SourcePackageIdentityRetained: true,
                OutputContainsReplacement: true, StagingPackageDiffersFromSource: true,
                UmodelPreviewReady: true, DeploymentReady: false, PackageWasModified: false,
                checks, exportPatches, build.Chunks, build.TablePatches, validation,
                watch.Elapsed.TotalSeconds);
            var manifestPath = Path.Combine(outputDirectory, TopologyManifestFileName);
            await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            return new SkeletalMeshTopologyStagedUpkResult(
                outputDirectory, manifestPath, outputPath, manifest, watch.Elapsed);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Appends a verified variable-length StaticMesh export and retargets only
    /// its export-table record. The old serialized export remains present and
    /// every unrelated logical byte is retained. This is deliberately the same
    /// physical package primitive used by topology-changing SkeletalMesh edits.
    /// </summary>
    public async Task<SkeletalMeshTopologyStagedUpkResult> BuildVariableSerializedExportAsync(
        VariableSerializedExportStagedUpkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        if (!request.ClassName.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase) ||
            request.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            Refuse("SVX010", "The variable serialized-export route currently accepts StaticMesh exports only.");

        var evidencePath = RequireFile(request.VerificationEvidencePath, "SVX011", "StaticMesh compilation manifest");
        var evidence = await StaticMeshCompilationService.LoadManifestAsync(evidencePath, cancellationToken)
            .ConfigureAwait(false);
        if (!evidence.DecodeBackPassed || !evidence.CollisionPreservedFromSource ||
            evidence.NativeExportIndex != request.NativeExportIndex ||
            evidence.SerialOffset != request.SerialOffset || evidence.SourceSerialSize != request.SerialSize ||
            !evidence.ObjectName.Equals(request.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            !evidence.ClassName.Equals(request.ClassName, StringComparison.OrdinalIgnoreCase) ||
            !evidence.SourceExportSha256.Equals(request.SourceExportSha256, StringComparison.OrdinalIgnoreCase) ||
            !evidence.CompiledExportSha256.Equals(request.CandidateExportSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SVX012", "The StaticMesh compilation evidence does not identify this replacement exactly.");

        var candidatePath = RequireFile(request.CandidateExportPath, "SVX013", "compiled StaticMesh export");
        var candidate = await File.ReadAllBytesAsync(candidatePath, cancellationToken).ConfigureAwait(false);
        if (candidate.LongLength <= 0 ||
            !Hash(candidate).Equals(request.CandidateExportSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SVX014", "The compiled StaticMesh export changed after decode-back verification.");
        var nativeParser = new StaticMeshNativeLayoutParser();
        if (!nativeParser.TryParse(candidate, request.SerialOffset,
                checked((int)request.NativeDataRelativeOffset), out var candidateLayout, out var candidateRefusal) ||
            candidateLayout is null)
            Refuse("SVX015", "The compiled StaticMesh failed an independent native parse: " + candidateRefusal);

        return await AppendAndRetargetAsync(
            request, candidate, evidencePath, evidence.CompilationId, evidence.SourcePackageSha256, "StaticMesh",
            replacement => nativeParser.TryParse(replacement, request.SerialOffset,
                               checked((int)request.NativeDataRelativeOffset), out var outputNative, out var outputRefusal) &&
                           outputNative is not null && outputNative.Lods.Count == candidateLayout.Lods.Count
                ? null
                : "The retargeted StaticMesh failed independent native decode: " + outputRefusal,
            new SkeletalMeshStagedUpkCheck("SVX_COLLISION", "Passed", "The compiler retained BodySetup and kDOP collision data from the source export."),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends an edited interface movie export (the game's export with only its RawData movie replaced, see
    /// Interface.GfxMovieFile) and retargets its export-table record, as for variable-length StaticMeshes.
    /// </summary>
    public async Task<SkeletalMeshTopologyStagedUpkResult> BuildInterfaceMovieAsync(
        VariableSerializedExportStagedUpkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AssetTypes.InterfaceMovie.CanEditClass(request.ClassName))
            Refuse("SVX040", "The interface movie route accepts interface movie exports only.");
        var evidencePath = RequireFile(request.VerificationEvidencePath, "SVX041", "interface movie candidate manifest");
        var evidence = await Interface.InterfaceMovieCandidateService.LoadManifestAsync(evidencePath, cancellationToken)
            .ConfigureAwait(false);
        if (!evidence.MovieReadBack || evidence.NativeExportIndex != request.NativeExportIndex ||
            evidence.SerialOffset != request.SerialOffset || evidence.SourceSerialSize != request.SerialSize ||
            !evidence.ObjectName.Equals(request.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            !evidence.SourceExportSha256.Equals(request.SourceExportSha256, StringComparison.OrdinalIgnoreCase) ||
            !evidence.CandidateExportSha256.Equals(request.CandidateExportSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SVX042", "The interface movie candidate does not identify this replacement exactly.");
        var candidatePath = RequireFile(request.CandidateExportPath, "SVX043", "interface movie export");
        var candidate = await File.ReadAllBytesAsync(candidatePath, cancellationToken).ConfigureAwait(false);
        if (!Hash(candidate).Equals(request.CandidateExportSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SVX044", "The interface movie export changed after it was checked.");
        return await AppendAndRetargetAsync(
            request, candidate, evidencePath, evidence.CandidateId, evidence.SourcePackageSha256, "interface movie",
            replacement => Interface.InterfaceMovieCandidateService.HoldsMovie(replacement, evidence.EditedMovieSha256)
                ? null
                : "The retargeted export no longer holds the edited interface movie.",
            new SkeletalMeshStagedUpkCheck("SVX_MOVIE", "Passed", "Only the movie bytes and their two length fields differ from the game's export."),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The package side of a variable-length replacement: appends the verified export, retargets only its export-table
    /// record, then proves every other byte, export and chunk unchanged and the appended export readable.
    /// </summary>
    private async Task<SkeletalMeshTopologyStagedUpkResult> AppendAndRetargetAsync(
        VariableSerializedExportStagedUpkRequest request,
        byte[] candidate,
        string evidencePath,
        string evidenceId,
        string evidenceSourcePackageSha256,
        string label,
        Func<byte[], string?> verifyReplacement,
        SkeletalMeshStagedUpkCheck typeCheck,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var sourcePath = RequireFile(request.SourcePackagePath, "SVX016", "source package");
        var sourceInfo = new FileInfo(sourcePath);
        var sourceHashBefore = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!sourceHashBefore.Equals(request.ExpectedSourcePackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !sourceHashBefore.Equals(evidenceSourcePackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SVX017", $"The source UPK changed after the {label} was checked.");
        var sourceSummary = _summaryReader.Read(sourcePath);
        if (!sourceSummary.IsWesternLostArkProfile)
            Refuse("SVX018", "Only the Western Lost Ark 868/16 package profile is supported.");
        var sourceCatalog = _catalogReader.Read(sourceSummary);
        var targetSource = sourceCatalog.Exports.SingleOrDefault(item => item.TableIndex == request.NativeExportIndex);
        var targetAsset = sourceCatalog.Assets.SingleOrDefault(item => item.ExportIndex == request.NativeExportIndex);
        if (targetSource is null || targetAsset is null ||
            targetSource.SerialOffset != request.SerialOffset || targetSource.SerialSize != request.SerialSize ||
            !targetAsset.ObjectName.Equals(request.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            !targetAsset.ClassName.Equals(request.ClassName, StringComparison.OrdinalIgnoreCase))
            Refuse("SVX019", $"The source export table no longer identifies the compiled {label}.");

        var sourceLogical = await ReadLogicalAsync(sourceSummary, cancellationToken).ConfigureAwait(false);
        var sourceLogicalHash = Hash(sourceLogical);
        var originalOffset = checked((int)request.SerialOffset);
        var originalSize = checked((int)request.SerialSize);
        if (originalOffset < 0 || originalOffset > sourceLogical.Length - originalSize ||
            !Hash(sourceLogical.AsSpan(originalOffset, originalSize)).Equals(
                request.SourceExportSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SVX020", $"The source logical export no longer matches what the {label} was checked against.");
        var rawLayout = ReadExportTableLayouts(sourceLogical, sourceSummary, sourceCatalog.Exports)
            [request.NativeExportIndex - 1];
        if (rawLayout.SerialOffset != request.SerialOffset || rawLayout.SerialSize != request.SerialSize)
            Refuse("SVX021", "The raw export-table fields disagree with the parsed target export.");

        const int appendAlignment = 4;
        var appendedOffset = AlignUp(sourceLogical.LongLength, appendAlignment);
        var appendPadding = checked((int)(appendedOffset - sourceLogical.LongLength));
        var outputLogicalBytes = checked(appendedOffset + candidate.LongLength);
        if (appendedOffset > int.MaxValue || candidate.LongLength > int.MaxValue || outputLogicalBytes > int.MaxValue)
            Refuse("SVX022", "The append-and-retarget layout exceeds UE3 Int32 serial storage.");
        var desiredLogical = new byte[checked((int)outputLogicalBytes)];
        sourceLogical.CopyTo(desiredLogical, 0);
        candidate.CopyTo(desiredLogical, checked((int)appendedOffset));
        var exportPatches = new[]
        {
            new SkeletalMeshTopologyExportTablePatchEvidence(
                request.NativeExportIndex, "SerialSize", rawLayout.SerialSizeFieldOffset,
                rawLayout.SerialSize, checked((int)candidate.LongLength), true),
            new SkeletalMeshTopologyExportTablePatchEvidence(
                request.NativeExportIndex, "SerialOffset", rawLayout.SerialOffsetFieldOffset,
                rawLayout.SerialOffset, checked((int)appendedOffset), true),
        };
        BinaryPrimitives.WriteInt32LittleEndian(
            desiredLogical.AsSpan(checked((int)rawLayout.SerialSizeFieldOffset), sizeof(int)),
            checked((int)candidate.LongLength));
        BinaryPrimitives.WriteInt32LittleEndian(
            desiredLogical.AsSpan(checked((int)rawLayout.SerialOffsetFieldOffset), sizeof(int)),
            checked((int)appendedOffset));

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var outputName = SafePhysicalName(request.PhysicalPackageName);
        var outputPath = Path.Combine(outputDirectory, outputName);
        if (Path.GetFullPath(outputPath).Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            Refuse("SVX023", $"The {label} staging output cannot be the source UPK.");
        var temporaryPath = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var patchOffsets = exportPatches.Select(item => item.LogicalOffset).ToArray();
            var build = sourceSummary.CompressedChunks.Count == 0
                ? await BuildUncompressedAsync(desiredLogical, temporaryPath, cancellationToken).ConfigureAwait(false)
                : await BuildCompressedAppendAsync(sourcePath, sourceSummary, desiredLogical, patchOffsets,
                    temporaryPath, cancellationToken).ConfigureAwait(false);
            var outputSummary = _summaryReader.Read(temporaryPath);
            var validation = PackageValidator.Validate(outputSummary);
            if (validation.Any(issue => issue.Severity == ValidationSeverity.Error))
                Refuse("SVX024", $"The {label} staged UPK failed structural package validation.");
            await VerifyPhysicalChunksAsync(sourcePath, temporaryPath, outputSummary, build.Chunks, cancellationToken)
                .ConfigureAwait(false);
            var outputLogical = await ReadLogicalAsync(outputSummary, cancellationToken).ConfigureAwait(false);
            if (!outputLogical.AsSpan().SequenceEqual(build.ExpectedLogical))
                Refuse("SVX025", $"The {label} staged UPK did not decode to its expected logical image.");
            var outputCatalog = _catalogReader.Read(outputSummary);
            var targetOutput = outputCatalog.Exports.SingleOrDefault(item => item.TableIndex == request.NativeExportIndex);
            if (targetOutput is null || targetOutput.SerialOffset != appendedOffset ||
                targetOutput.SerialSize != candidate.LongLength)
                Refuse("SVX026", $"The selected export was not retargeted to the appended {label} payload.");
            var originalRetained = Hash(outputLogical.AsSpan(originalOffset, originalSize)).Equals(
                request.SourceExportSha256, StringComparison.OrdinalIgnoreCase);
            var replacementVerified = Hash(outputLogical.AsSpan(
                checked((int)appendedOffset), checked((int)candidate.LongLength))).Equals(
                request.CandidateExportSha256, StringComparison.OrdinalIgnoreCase);
            if (!originalRetained || !replacementVerified)
                Refuse("SVX027", $"The retained original or appended {label} export failed hash verification.");
            var metadataRanges = exportPatches.Select(item => (item.LogicalOffset, sizeof(int)))
                .Concat(build.TablePatches.Where(item => item.Changed && item.PhysicalOffset < sourceLogical.LongLength)
                    .Select(item => (item.PhysicalOffset, sizeof(uint)))).ToArray();
            if (!VerifyRetainedSourceBytes(sourceLogical, outputLogical, metadataRanges))
                Refuse("SVX028", "A source logical byte outside audited table fields changed.");
            var unrelatedEquivalent = sourceCatalog.Exports
                .Where(item => item.TableIndex != request.NativeExportIndex)
                .Zip(outputCatalog.Exports.Where(item => item.TableIndex != request.NativeExportIndex), ExportsEquivalent)
                .All(value => value) && sourceCatalog.Exports.Count == outputCatalog.Exports.Count;
            var catalogueShape = sourceCatalog.Names.SequenceEqual(outputCatalog.Names) &&
                                 sourceCatalog.Imports.SequenceEqual(outputCatalog.Imports) &&
                                 sourceCatalog.Assets.Zip(outputCatalog.Assets, (left, right) =>
                                         left.ExportIndex == right.ExportIndex && left.ObjectName == right.ObjectName &&
                                         left.ClassName == right.ClassName && left.Kind == right.Kind)
                                     .All(value => value) &&
                                 sourceCatalog.Assets.Count == outputCatalog.Assets.Count;
            if (!unrelatedEquivalent || !catalogueShape)
                Refuse("SVX029", $"An unrelated export or catalogue identity changed during {label} staging.");
            var replacementBytes = outputLogical.AsSpan(
                checked((int)appendedOffset), checked((int)candidate.LongLength)).ToArray();
            if (verifyReplacement(replacementBytes) is { } replacementRefusal)
                Refuse("SVX030", replacementRefusal);
            var outputHash = await HashFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            var outputInfo = new FileInfo(temporaryPath);
            if (outputInfo.Length == sourceInfo.Length &&
                outputHash.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
                Refuse("SVX031", $"The {label} staging package unexpectedly matches its source.");
            var sourceHashAfter = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
                Refuse("SVX032", $"The source UPK changed while Arkive built the {label} staging copy.");
            File.Move(temporaryPath, outputPath, overwrite: true);
            watch.Stop();
            var rebuiltCount = build.Chunks.Count(item => item.Rebuilt);
            var blockCount = build.Chunks.Sum(item => item.Blocks.Count);
            var protectedCount = build.Chunks.Count(item => item.Rebuilt && item.LostArkFlags == 1);
            var checks = new[]
            {
                new SkeletalMeshStagedUpkCheck("SVX_SOURCE", "Passed", "The clean source UPK remained read-only and hash-identical."),
                new SkeletalMeshStagedUpkCheck("SVX_APPEND", "Passed", $"Retained the original export and appended {candidate.LongLength:N0} bytes at logical 0x{appendedOffset:X}."),
                new SkeletalMeshStagedUpkCheck("SVX_EXPORT_TABLE", "Passed", $"Patched only export {request.NativeExportIndex}'s SerialSize and SerialOffset."),
                typeCheck,
                new SkeletalMeshStagedUpkCheck("SVX_DECODE_BACK", "Passed", $"All {blockCount:N0} encoded blocks and the retargeted {label} decoded successfully."),
            };
            var manifest = new SkeletalMeshTopologyStagedUpkManifest(
                CurrentSchemaVersion, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                evidencePath, evidenceId, sourcePath, sourceHashAfter, sourceInfo.Length,
                sourceLogical.LongLength, sourceLogicalHash, outputName, outputHash, outputInfo.Length,
                outputLogical.LongLength, Hash(outputLogical), request.PhysicalPackageName, request.PackageAlias,
                request.ClassName, request.ObjectName, request.NativeExportIndex, request.SerialOffset,
                request.SerialSize, appendedOffset, candidate.LongLength,
                candidate.LongLength - request.SerialSize, appendPadding, request.SourceExportSha256,
                request.CandidateExportSha256, build.Chunks.Count, rebuiltCount,
                build.Chunks.Count - rebuiltCount, protectedCount, blockCount,
                AppendAndRetargetReplacement: true, ExportTablePatched: true,
                OriginalExportBytesRetained: true, TargetExportRetargeted: true,
                ReplacementExportBytesVerified: true, AllUnplannedSourceBytesIdentical: true,
                UnrelatedExportsEquivalent: true, AssetCatalogueShapeRetained: true,
                AllUnaffectedChunksByteIdentical: build.Chunks.Where(item => !item.Rebuilt)
                    .All(item => item.SourcePhysicalBytesRetained),
                AllEncodedBlocksDecodeBack: build.Chunks.SelectMany(item => item.Blocks)
                    .All(item => item.DecodeBackPassed),
                ExpectedLogicalImageMatched: true, StructuralValidationPassed: true,
                TargetNativeDecodePassed: true, SourcePackageIdentityRetained: true,
                OutputContainsReplacement: true, StagingPackageDiffersFromSource: true,
                UmodelPreviewReady: true, DeploymentReady: false, PackageWasModified: false,
                checks, exportPatches, build.Chunks, build.TablePatches, validation,
                watch.Elapsed.TotalSeconds);
            var manifestPath = Path.Combine(outputDirectory, TopologyManifestFileName);
            await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            return new SkeletalMeshTopologyStagedUpkResult(
                outputDirectory, manifestPath, outputPath, manifest, watch.Elapsed);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task<SkeletalMeshStagedUpkResult> BuildAsync(
        SkeletalMeshStagedUpkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var encodingPath = RequireFile(
            request.NativeEncodingManifestPath, "SMS010", "native encoding manifest");
        var encoding = await SkeletalMeshNativeEncodingService.LoadManifestAsync(
            encodingPath, cancellationToken).ConfigureAwait(false);
        if (!encoding.LockedBytesPreserved || !encoding.DecodeBackPassed ||
            encoding.ChangedLockedByteCount != 0 || encoding.ActualChangedByteCount <= 0 ||
            encoding.SerialSize <= 0 ||
            encoding.Preflight.SchemaVersion != SkeletalMeshEditPreflightService.CurrentSchemaVersion ||
            encoding.Preflight.AnimationValidation is not { Passed: true })
            Refuse("SMS011", "A complete changed, locked-byte-preserving native encoding is required.");

        var encodingDirectory = Path.GetDirectoryName(encodingPath)!;
        var encodedPath = ResolveWithin(
            encodingDirectory, encoding.EncodedExportRelativePath, "SMS012");
        var encoded = await File.ReadAllBytesAsync(encodedPath, cancellationToken).ConfigureAwait(false);
        if (encoded.LongLength != encoding.SerialSize ||
            !Hash(encoded).Equals(encoding.EncodedExportSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SMS013", "The encoded native export payload changed after verification.");

        var sourceEditablePath = RequireFile(
            encoding.SourceEditableManifestPath, "SMS014", "source editable manifest");
        var sourceEditable = await ReadJsonAsync<SkeletalMeshEditableExportManifest>(
            sourceEditablePath, cancellationToken).ConfigureAwait(false);
        var reconPath = RequireFile(
            sourceEditable.ReconnaissanceJsonPath, "SMS015", "mesh reconnaissance report");
        var recon = await ReadJsonAsync<SkeletalMeshReconnaissanceReport>(
            reconPath, cancellationToken).ConfigureAwait(false);
        ValidateIdentity(encoding, sourceEditable, recon);

        var sourcePath = RequireFile(encoding.SourcePackagePath, "SMS016", "source package");
        var sourceInfo = new FileInfo(sourcePath);
        var sourceHashBefore = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (sourceInfo.Length != recon.PackageLength ||
            sourceInfo.LastWriteTimeUtc.Ticks != recon.PackageLastWriteUtcTicks ||
            !sourceHashBefore.Equals(encoding.SourcePackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SMS017", "The source UPK changed after native mesh reconnaissance.");

        var sourceSummary = _summaryReader.Read(sourcePath);
        if (!sourceSummary.IsWesternLostArkProfile)
            Refuse("SMS018", "Only the Western Lost Ark 868/16 package profile is supported.");
        var sourceCatalog = _catalogReader.Read(sourceSummary);
        var targetSource = sourceCatalog.Exports.SingleOrDefault(item =>
            item.TableIndex == encoding.NativeExportIndex);
        var targetAsset = sourceCatalog.Assets.SingleOrDefault(item =>
            item.ExportIndex == encoding.NativeExportIndex);
        if (targetSource is null || targetAsset is null ||
            targetSource.SerialOffset != encoding.SerialOffset ||
            targetSource.SerialSize != encoding.SerialSize ||
            !targetAsset.ObjectName.Equals(encoding.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            !targetAsset.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            Refuse("SMS019", "The current export table no longer identifies the encoded SkeletalMesh.");

        var sourceLogical = await ReadLogicalAsync(sourceSummary, cancellationToken).ConfigureAwait(false);
        var sourceLogicalHash = Hash(sourceLogical);
        var serialOffset = checked((int)encoding.SerialOffset);
        var serialSize = checked((int)encoding.SerialSize);
        if (serialOffset < 0 || serialOffset > sourceLogical.Length - serialSize ||
            !Hash(sourceLogical.AsSpan(serialOffset, serialSize)).Equals(
                encoding.SourceExportSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SMS020", "The source logical export bytes no longer match native encoding evidence.");

        var desiredLogical = sourceLogical.ToArray();
        encoded.CopyTo(desiredLogical, serialOffset);
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var outputName = SafePhysicalName(encoding.PhysicalPackageName);
        var outputPath = Path.Combine(outputDirectory, outputName);
        if (Path.GetFullPath(outputPath).Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            Refuse("SMS021", "The staging output cannot be the installed source UPK.");
        var temporaryPath = outputPath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            var build = sourceSummary.CompressedChunks.Count == 0
                ? await BuildUncompressedAsync(desiredLogical, temporaryPath, cancellationToken)
                    .ConfigureAwait(false)
                : await BuildCompressedAsync(
                    sourcePath, sourceSummary, desiredLogical, encoding.SerialOffset,
                    encoding.SerialSize, temporaryPath, cancellationToken).ConfigureAwait(false);

            var outputSummary = _summaryReader.Read(temporaryPath);
            var validation = PackageValidator.Validate(outputSummary);
            if (validation.Any(issue => issue.Severity == ValidationSeverity.Error))
                Refuse("SMS022", "The staged mesh UPK failed structural package validation.");
            await VerifyPhysicalChunksAsync(
                sourcePath, temporaryPath, outputSummary, build.Chunks, cancellationToken)
                .ConfigureAwait(false);
            var outputLogical = await ReadLogicalAsync(outputSummary, cancellationToken).ConfigureAwait(false);
            if (!outputLogical.AsSpan().SequenceEqual(build.ExpectedLogical))
                Refuse("SMS023", "The staged mesh UPK did not decode to its expected logical image.");
            if (!Hash(outputLogical.AsSpan(serialOffset, serialSize)).Equals(
                    encoding.EncodedExportSha256, StringComparison.OrdinalIgnoreCase))
                Refuse("SMS024", "The staged target export does not contain the encoded native payload.");

            var unrelatedIdentical = VerifyUnrelatedLogicalBytes(
                sourceLogical, outputLogical, serialOffset, serialSize,
                sourceSummary.CompressedChunks.Count == 0
                    ? []
                    : build.TablePatches.Where(item => item.Changed)
                        .Select(item => (item.PhysicalOffset, 4)).ToArray());
            if (!unrelatedIdentical)
                Refuse("SMS025", "A logical byte outside the target export and chunk-table metadata changed.");

            var outputCatalog = _catalogReader.Read(outputSummary);
            var catalogueEquivalent = CataloguesEquivalent(sourceCatalog, outputCatalog);
            if (!catalogueEquivalent)
                Refuse("SMS026", "The export table or asset catalogue changed during in-place staging.");

            ValidateNativeDecode(encoded, recon.NativeLayout!);
            var outputHash = await HashFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            var outputInfo = new FileInfo(temporaryPath);
            var outputLength = outputInfo.Length;
            if (outputHash.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
                Refuse("SMS027", "The staged package unexpectedly matches the source package.");
            var sourceHashAfter = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
                Refuse("SMS028", "The source package changed while Arkive built the staged copy.");

            File.Move(temporaryPath, outputPath, overwrite: true);
            watch.Stop();
            var rebuiltCount = build.Chunks.Count(item => item.Rebuilt);
            var blockCount = build.Chunks.Sum(item => item.Blocks.Count);
            var protectedCount = build.Chunks.Count(item => item.Rebuilt && item.LostArkFlags == 1);
            var checks = new[]
            {
                new SkeletalMeshStagedUpkCheck("SMS_SOURCE", "Passed",
                    $"Source UPK remained read-only at SHA-256 {sourceHashAfter}."),
                new SkeletalMeshStagedUpkCheck("SMS_IN_PLACE", "Passed",
                    $"Replaced export {encoding.NativeExportIndex} in place at 0x{encoding.SerialOffset:X}+0x{encoding.SerialSize:X}; the export table is unchanged."),
                new SkeletalMeshStagedUpkCheck("SMS_CHUNKS", "Passed",
                    $"Rebuilt {rebuiltCount:N0} affected chunk(s) and retained {build.Chunks.Count - rebuiltCount:N0} chunk(s) byte-for-byte."),
                new SkeletalMeshStagedUpkCheck("SMS_DECODE_BACK", "Passed",
                    $"All {blockCount:N0} newly encoded compression block(s) decoded back exactly."),
                new SkeletalMeshStagedUpkCheck("SMS_LOGICAL", "Passed",
                    $"Decoded staged logical image matches SHA-256 {Hash(outputLogical)}."),
                new SkeletalMeshStagedUpkCheck("SMS_ISOLATION", "Passed",
                    "Every unrelated logical byte and every catalogue/export record is unchanged."),
                new SkeletalMeshStagedUpkCheck("SMS_NATIVE", "Passed",
                    $"The staged {encoding.ObjectName} payload passed native semantic mesh decoding across {recon.NativeLayout!.Lods.Count:N0} LOD(s)."),
                new SkeletalMeshStagedUpkCheck("SMS_STAGING_ONLY", "Information",
                    "The UPK is isolated in Arkive staging and approved only for UModel preview; game installation is disabled."),
            };
            var manifest = new SkeletalMeshStagedUpkManifest(
                CurrentSchemaVersion, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                encodingPath, encoding.EncodingId, sourcePath, sourceHashAfter,
                sourceInfo.Length, outputName, outputHash, outputLength,
                sourceLogicalHash, Hash(build.ExpectedLogical), Hash(outputLogical), outputLogical.LongLength,
                encoding.PhysicalPackageName, encoding.PackageAlias, "SkeletalMesh", encoding.ObjectName,
                encoding.NativeExportIndex, encoding.SerialOffset, encoding.SerialSize,
                encoding.SourceExportSha256, encoding.EncodedExportSha256,
                build.Chunks.Count, rebuiltCount, build.Chunks.Count - rebuiltCount,
                protectedCount, blockCount,
                SameSizeInPlaceReplacement: true, ExportTableUnchanged: true,
                AllUnrelatedLogicalBytesIdentical: true,
                AllUnaffectedChunksByteIdentical: build.Chunks.Where(item => !item.Rebuilt)
                    .All(item => item.SourcePhysicalBytesRetained),
                AllEncodedBlocksDecodeBack: build.Chunks.SelectMany(item => item.Blocks)
                    .All(item => item.DecodeBackPassed),
                ExpectedLogicalImageMatched: true, AssetCatalogueEquivalent: true,
                StructuralValidationPassed: true, TargetNativeDecodePassed: true,
                SourcePackageIdentityRetained: true, OutputContainsReplacement: true,
                StagingPackageDiffersFromSource: true, UmodelPreviewReady: true,
                DeploymentReady: false, PackageWasModified: false,
                checks, build.Chunks, build.TablePatches, validation, watch.Elapsed.TotalSeconds);
            var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
            await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            return new SkeletalMeshStagedUpkResult(
                outputDirectory, manifestPath, outputPath, manifest, watch.Elapsed);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void ValidateIdentity(
        SkeletalMeshNativeEncodingManifest encoding,
        SkeletalMeshEditableExportManifest editable,
        SkeletalMeshReconnaissanceReport recon)
    {
        if (editable.SchemaVersion != SkeletalMeshEditableExportService.CurrentSchemaVersion ||
            recon.SchemaVersion != SkeletalMeshReconnaissanceService.CurrentSchemaVersion ||
            recon.Readiness != SkeletalMeshReadiness.NoOpReady || recon.NativeLayout is null ||
            recon.NativeExportIndex != encoding.NativeExportIndex ||
            editable.NativeExportIndex != encoding.NativeExportIndex ||
            recon.SerialOffset != encoding.SerialOffset || recon.SerialSize != encoding.SerialSize ||
            !recon.PackageSha256.Equals(encoding.SourcePackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !recon.ExportPayloadSha256.Equals(encoding.SourceExportSha256, StringComparison.OrdinalIgnoreCase) ||
            !editable.SourceExportSha256.Equals(encoding.SourceExportSha256, StringComparison.OrdinalIgnoreCase) ||
            !recon.ObjectName.Equals(encoding.ObjectName, StringComparison.OrdinalIgnoreCase))
            Refuse("SMS029", "Native encoding, editable export, and reconnaissance identities disagree.");
    }

    private static void ValidateNativeDecode(
        ReadOnlySpan<byte> encoded,
        SkeletalMeshNativeLayoutEvidence layout)
    {
        var updatedLods = new SkeletalMeshNativeLodEvidence[layout.Lods.Count];
        for (var index = 0; index < updatedLods.Length; index++)
        {
            var lod = layout.Lods[index];
            updatedLods[index] = lod with
            {
                VertexBuffer = lod.VertexBuffer with
                {
                    DataSha256 = Hash(encoded.Slice(
                        checked((int)lod.VertexBuffer.DataRelativeOffset),
                        checked((int)lod.VertexBuffer.DataBytes))),
                },
            };
        }
        var updated = layout with
        {
            Lods = updatedLods,
        };
        var decoded = new SkeletalMeshSemanticDecoder().Decode(encoded, updated);
        if (decoded.Count != updated.Lods.Count || decoded.Any(item =>
                item.Positions.Length != item.Evidence.VertexCount || item.Indices.Length == 0))
            Refuse("SMS030", "The staged target failed native SkeletalMesh semantic decoding.");
    }

    private static bool VerifyUnrelatedLogicalBytes(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> output,
        int serialOffset,
        int serialSize,
        IReadOnlyList<(long Offset, int Size)> metadataRanges)
    {
        if (source.Length != output.Length) return false;
        for (var index = 0; index < source.Length; index++)
        {
            if (index >= serialOffset && index < serialOffset + serialSize) continue;
            if (metadataRanges.Any(range => index >= range.Offset && index < range.Offset + range.Size)) continue;
            if (source[index] != output[index]) return false;
        }
        return true;
    }

    private static bool VerifyRetainedSourceBytes(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> output,
        IReadOnlyList<(long Offset, int Size)> metadataRanges)
    {
        if (output.Length < source.Length) return false;
        for (var index = 0; index < source.Length; index++)
        {
            if (metadataRanges.Any(range =>
                    index >= range.Offset && index < range.Offset + range.Size)) continue;
            if (source[index] != output[index]) return false;
        }
        return true;
    }

    private static IReadOnlyList<RawExportLayout> ReadExportTableLayouts(
        byte[] logical,
        PackageSummary summary,
        IReadOnlyList<UnrealObjectExport> parsed)
    {
        using var stream = new MemoryStream(logical, writable: false);
        stream.Position = summary.ExportOffset;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var result = new List<RawExportLayout>(summary.ExportCount);
        for (var index = 0; index < summary.ExportCount; index++)
        {
            var start = stream.Position;
            if (stream.Length - stream.Position < 48)
                Refuse("SMV050", $"Export-table record {index + 1} is truncated.");
            _ = reader.ReadInt32(); // class
            _ = reader.ReadInt32(); // super
            _ = reader.ReadInt32(); // outer
            _ = reader.ReadInt32(); // object name index
            _ = reader.ReadInt32(); // object name number
            _ = reader.ReadInt32(); // archetype
            _ = reader.ReadUInt32(); // flags low
            _ = reader.ReadUInt32(); // flags high
            var sizeField = stream.Position;
            var serialSize = reader.ReadInt32();
            var offsetField = stream.Position;
            var serialOffset = reader.ReadInt32();
            _ = reader.ReadUInt32(); // export flags
            var netCount = reader.ReadInt32();
            if (netCount < 0 || netCount > PackageConstants.MaxReasonableTableEntries)
                Refuse("SMV051", $"Export {index + 1} has invalid net-object count {netCount:N0}.");
            var remaining = checked(netCount * 4L + 16L + 4L);
            if (stream.Position > stream.Length - remaining)
                Refuse("SMV052", $"Export {index + 1} table record is truncated.");
            stream.Position += remaining;
            var expected = parsed[index];
            if (serialSize != expected.SerialSize || serialOffset != expected.SerialOffset)
                Refuse("SMV053", $"Raw export {index + 1} fields disagree with the parsed catalogue.");
            result.Add(new RawExportLayout(
                index + 1, start, stream.Position, sizeField, offsetField,
                serialSize, serialOffset));
        }
        return result;
    }

    private static long AlignUp(long value, int alignment)
    {
        if (alignment <= 0) throw new ArgumentOutOfRangeException(nameof(alignment));
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static bool CataloguesEquivalent(AssetCatalog source, AssetCatalog output) =>
        source.Names.SequenceEqual(output.Names) &&
        source.Imports.SequenceEqual(output.Imports) &&
        source.Exports.Count == output.Exports.Count &&
        source.Exports.Zip(output.Exports, ExportsEquivalent).All(value => value) &&
        source.Assets.Count == output.Assets.Count &&
        source.Assets.Zip(output.Assets, (left, right) =>
                left.ExportIndex == right.ExportIndex && left.ObjectName == right.ObjectName &&
                left.ClassName == right.ClassName && left.Kind == right.Kind &&
                left.SerialOffset == right.SerialOffset && left.SerialSize == right.SerialSize)
            .All(value => value);

    private static bool ExportsEquivalent(UnrealObjectExport left, UnrealObjectExport right) =>
        left.TableIndex == right.TableIndex && left.ClassIndex == right.ClassIndex &&
        left.SuperIndex == right.SuperIndex && left.OuterIndex == right.OuterIndex &&
        left.ObjectName == right.ObjectName && left.ArchetypeIndex == right.ArchetypeIndex &&
        left.ObjectFlags == right.ObjectFlags && left.SerialSize == right.SerialSize &&
        left.SerialOffset == right.SerialOffset && left.ExportFlags == right.ExportFlags &&
        left.NetObjectCounts.SequenceEqual(right.NetObjectCounts) &&
        left.PackageGuid == right.PackageGuid && left.UnknownTail == right.UnknownTail;

    private static async Task<BuildEvidence> BuildUncompressedAsync(
        byte[] logical,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(outputPath, logical, cancellationToken).ConfigureAwait(false);
        return new BuildEvidence(logical, [], []);
    }

    private async Task<BuildEvidence> BuildCompressedAsync(
        string sourcePath,
        PackageSummary summary,
        byte[] desiredLogical,
        long serialOffset,
        long serialSize,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var chunks = summary.CompressedChunks;
        var firstLogicalOffset = chunks[0].UncompressedOffset;
        var firstPhysicalOffset = chunks[0].CompressedOffset;
        var chunkTableEnd = summary.CompressedChunkTableOffset + chunks.Count * 20L;
        if (chunkTableEnd > firstPhysicalOffset || firstPhysicalOffset < firstLogicalOffset)
            Refuse("SMS040", "The package prefix/chunk-table geometry is unsupported.");

        var serialEnd = checked(serialOffset + serialSize);
        var affected = chunks.Select((chunk, index) => (chunk, index))
            .Where(item => serialOffset < (long)item.chunk.UncompressedEnd &&
                           serialEnd > item.chunk.UncompressedOffset)
            .Select(item => item.index).ToHashSet();
        if (affected.Count == 0)
            Refuse("SMS041", "The target export does not overlap any physical chunk.");

        var blockSizes = new Dictionary<int, int?>();
        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete, BufferBytes,
                         FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            foreach (var index in affected)
                blockSizes[index] = await ReadBlockSizeAsync(
                    source, chunks[index], index, cancellationToken).ConfigureAwait(false);
        }

        Dictionary<int, EncodedChunk> encoded = [];
        IReadOnlyList<CompressedChunk>? previousPlan = null;
        IReadOnlyList<CompressedChunk> planned = [];
        var expected = desiredLogical.ToArray();
        var converged = false;
        for (var pass = 0; pass < 32; pass++)
        {
            encoded = [];
            foreach (var index in affected)
            {
                var chunk = chunks[index];
                encoded[index] = EncodeChunk(summary, chunk, index, blockSizes[index],
                    expected.AsSpan(checked((int)chunk.UncompressedOffset),
                        checked((int)chunk.UncompressedSize)));
            }
            planned = PlanChunks(chunks, encoded, firstPhysicalOffset);
            var next = desiredLogical.ToArray();
            ApplyChunkTable(next, summary, planned, null, firstLogicalOffset, logicalPrefixOnly: true);
            if (previousPlan is not null && previousPlan.SequenceEqual(planned) &&
                next.AsSpan().SequenceEqual(expected))
            {
                expected = next;
                converged = true;
                break;
            }
            previousPlan = planned.ToArray();
            expected = next;
        }
        if (!converged) Refuse("SMS042", "The chunk-table layout did not converge.");

        var prefix = new byte[checked((int)firstPhysicalOffset)];
        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete, BufferBytes,
                         FileOptions.Asynchronous | FileOptions.RandomAccess))
            await source.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var patches = new List<SkeletalMeshStagedUpkTablePatchEvidence>();
        ApplyChunkTable(prefix, summary, planned, patches, firstLogicalOffset, logicalPrefixOnly: false);

        var evidence = new List<SkeletalMeshStagedUpkChunkEvidence>(chunks.Count);
        await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferBytes,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        long previousSourceEnd = firstPhysicalOffset;
        for (var index = 0; index < chunks.Count; index++)
        {
            var sourceChunk = chunks[index];
            var outputChunk = planned[index];
            var gap = checked((long)sourceChunk.CompressedOffset - previousSourceEnd);
            if (gap > 0)
            {
                sourceStream.Position = previousSourceEnd;
                await CopyAsync(sourceStream, output, gap, cancellationToken).ConfigureAwait(false);
            }
            if (output.Position != outputChunk.CompressedOffset)
                Refuse("SMS043", $"Chunk {index} does not begin at its planned output offset.");

            byte[] physical;
            IReadOnlyList<SkeletalMeshStagedUpkBlockEvidence> blocks;
            var rebuilt = encoded.TryGetValue(index, out var rebuiltChunk);
            if (rebuilt)
            {
                physical = rebuiltChunk!.PhysicalBytes;
                blocks = rebuiltChunk.Blocks;
            }
            else
            {
                physical = new byte[checked((int)sourceChunk.CompressedSize)];
                sourceStream.Position = sourceChunk.CompressedOffset;
                await sourceStream.ReadExactlyAsync(physical, cancellationToken).ConfigureAwait(false);
                blocks = [];
            }
            await output.WriteAsync(physical, cancellationToken).ConfigureAwait(false);
            evidence.Add(new SkeletalMeshStagedUpkChunkEvidence(
                index, sourceChunk.UncompressedOffset, sourceChunk.UncompressedSize,
                sourceChunk.CompressedOffset, sourceChunk.CompressedSize,
                outputChunk.CompressedOffset, outputChunk.CompressedSize, sourceChunk.LostArkFlags,
                affected.Contains(index), rebuilt, !rebuilt, Hash(physical),
                Hash(expected.AsSpan(checked((int)sourceChunk.UncompressedOffset),
                    checked((int)sourceChunk.UncompressedSize))), blocks));
            previousSourceEnd = checked((long)sourceChunk.CompressedEnd);
        }
        var tail = summary.PhysicalFileSize - previousSourceEnd;
        if (tail > 0)
        {
            sourceStream.Position = previousSourceEnd;
            await CopyAsync(sourceStream, output, tail, cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new BuildEvidence(expected, evidence, patches);
    }

    private async Task<BuildEvidence> BuildCompressedAppendAsync(
        string sourcePath,
        PackageSummary summary,
        byte[] desiredLogical,
        IReadOnlyList<long> exportTablePatchOffsets,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var chunks = summary.CompressedChunks;
        if (chunks.Count == 0) throw new InvalidOperationException("No compressed chunks were supplied.");
        var firstLogicalOffset = chunks[0].UncompressedOffset;
        var firstPhysicalOffset = chunks[0].CompressedOffset;
        var chunkTableEnd = summary.CompressedChunkTableOffset + chunks.Count * 20L;
        if (chunkTableEnd > firstPhysicalOffset || firstPhysicalOffset < firstLogicalOffset)
            Refuse("SMV040", "The package prefix/chunk-table geometry is unsupported.");
        for (var index = 1; index < chunks.Count; index++)
        {
            if (chunks[index].UncompressedOffset != chunks[index - 1].UncompressedEnd)
                Refuse("SMV041", "Variable-size staging requires contiguous logical chunks.");
        }
        var sourceLogicalBytes = checked((long)chunks[^1].UncompressedEnd);
        if (desiredLogical.LongLength <= sourceLogicalBytes)
            Refuse("SMV042", "The topology logical image does not append beyond the source package.");

        var affected = new HashSet<int> { chunks.Count - 1 };
        foreach (var patchOffset in exportTablePatchOffsets)
        {
            for (var index = 0; index < chunks.Count; index++)
            {
                var chunk = chunks[index];
                if ((ulong)patchOffset >= chunk.UncompressedOffset &&
                    (ulong)patchOffset < chunk.UncompressedEnd)
                {
                    affected.Add(index);
                    break;
                }
            }
        }

        var outputLogicalSizes = chunks.Select((chunk, index) =>
                index == chunks.Count - 1
                    ? checked((int)(desiredLogical.LongLength - chunk.UncompressedOffset))
                    : checked((int)chunk.UncompressedSize))
            .ToArray();
        if (outputLogicalSizes[^1] <= 0)
            Refuse("SMV043", "The extended final chunk has an invalid logical size.");

        var blockSizes = new Dictionary<int, int?>();
        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete, BufferBytes,
                         FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            foreach (var index in affected)
                blockSizes[index] = await ReadBlockSizeAsync(
                    source, chunks[index], index, cancellationToken).ConfigureAwait(false);
        }

        Dictionary<int, EncodedChunk> encoded = [];
        IReadOnlyList<CompressedChunk>? previousPlan = null;
        IReadOnlyList<CompressedChunk> planned = [];
        var expected = desiredLogical.ToArray();
        var converged = false;
        for (var pass = 0; pass < 32; pass++)
        {
            encoded = [];
            foreach (var index in affected)
            {
                var chunk = chunks[index];
                encoded[index] = EncodeChunk(summary, chunk, index, blockSizes[index],
                    expected.AsSpan(checked((int)chunk.UncompressedOffset), outputLogicalSizes[index]));
            }
            planned = PlanChunks(
                chunks, encoded, outputLogicalSizes, firstPhysicalOffset);
            var next = desiredLogical.ToArray();
            ApplyChunkTable(next, summary, planned, null, firstLogicalOffset, logicalPrefixOnly: true);
            if (previousPlan is not null && previousPlan.SequenceEqual(planned) &&
                next.AsSpan().SequenceEqual(expected))
            {
                expected = next;
                converged = true;
                break;
            }
            previousPlan = planned.ToArray();
            expected = next;
        }
        if (!converged) Refuse("SMV044", "The variable-size chunk-table layout did not converge.");

        var prefix = new byte[checked((int)firstPhysicalOffset)];
        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete, BufferBytes,
                         FileOptions.Asynchronous | FileOptions.RandomAccess))
            await source.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        foreach (var patchOffset in exportTablePatchOffsets.Where(offset => offset + 4 <= firstLogicalOffset))
            expected.AsSpan(checked((int)patchOffset), 4)
                .CopyTo(prefix.AsSpan(checked((int)patchOffset), 4));
        var patches = new List<SkeletalMeshStagedUpkTablePatchEvidence>();
        ApplyChunkTable(prefix, summary, planned, patches, firstLogicalOffset, logicalPrefixOnly: false);

        var evidence = new List<SkeletalMeshStagedUpkChunkEvidence>(chunks.Count);
        await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferBytes,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        long previousSourceEnd = firstPhysicalOffset;
        for (var index = 0; index < chunks.Count; index++)
        {
            var sourceChunk = chunks[index];
            var outputChunk = planned[index];
            var gap = checked((long)sourceChunk.CompressedOffset - previousSourceEnd);
            if (gap > 0)
            {
                sourceStream.Position = previousSourceEnd;
                await CopyAsync(sourceStream, output, gap, cancellationToken).ConfigureAwait(false);
            }
            if (output.Position != outputChunk.CompressedOffset)
                Refuse("SMV045", $"Chunk {index} does not begin at its planned output offset.");

            byte[] physical;
            IReadOnlyList<SkeletalMeshStagedUpkBlockEvidence> blocks;
            var rebuilt = encoded.TryGetValue(index, out var rebuiltChunk);
            if (rebuilt)
            {
                physical = rebuiltChunk!.PhysicalBytes;
                blocks = rebuiltChunk.Blocks;
            }
            else
            {
                physical = new byte[checked((int)sourceChunk.CompressedSize)];
                sourceStream.Position = sourceChunk.CompressedOffset;
                await sourceStream.ReadExactlyAsync(physical, cancellationToken).ConfigureAwait(false);
                blocks = [];
            }
            await output.WriteAsync(physical, cancellationToken).ConfigureAwait(false);
            evidence.Add(new SkeletalMeshStagedUpkChunkEvidence(
                index, outputChunk.UncompressedOffset, outputChunk.UncompressedSize,
                sourceChunk.CompressedOffset, sourceChunk.CompressedSize,
                outputChunk.CompressedOffset, outputChunk.CompressedSize, sourceChunk.LostArkFlags,
                affected.Contains(index), rebuilt, !rebuilt, Hash(physical),
                Hash(expected.AsSpan(checked((int)outputChunk.UncompressedOffset),
                    checked((int)outputChunk.UncompressedSize))), blocks));
            previousSourceEnd = checked((long)sourceChunk.CompressedEnd);
        }
        var tail = summary.PhysicalFileSize - previousSourceEnd;
        if (tail > 0)
        {
            sourceStream.Position = previousSourceEnd;
            await CopyAsync(sourceStream, output, tail, cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new BuildEvidence(expected, evidence, patches);
    }

    private async Task<int?> ReadBlockSizeAsync(
        FileStream stream,
        CompressedChunk chunk,
        int chunkIndex,
        CancellationToken cancellationToken)
    {
        if (chunk.LostArkFlags == 0 && chunk.CompressedSize == chunk.UncompressedSize)
            return null;
        var header = new byte[16];
        stream.Position = chunk.CompressedOffset;
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var blockSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var rawBytes = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        if (tag != PackageConstants.UnrealPackageTag || blockSize <= 0 ||
            blockSize > MaximumBlockSize || rawBytes != chunk.UncompressedSize)
            Refuse("SMS044", $"Chunk {chunkIndex} has an unsupported compression envelope.");
        return blockSize;
    }

    private EncodedChunk EncodeChunk(
        PackageSummary summary,
        CompressedChunk sourceChunk,
        int chunkIndex,
        int? blockSize,
        ReadOnlySpan<byte> logical)
    {
        if (blockSize is null)
            return new EncodedChunk(logical.ToArray(), []);
        if (sourceChunk.LostArkFlags is not 0 and not 1)
            Refuse("SMS045", $"Chunk {chunkIndex} uses unsupported protection flags.");
        if (sourceChunk.LostArkFlags == 1 && !_protectionProvider.CanProtect(summary, sourceChunk))
            Refuse("SMS046", $"Chunk {chunkIndex} is protected but no write provider is configured.");

        var encodedBlocks = new List<(byte[] Physical, int RawBytes)>();
        var evidence = new List<SkeletalMeshStagedUpkBlockEvidence>();
        for (var offset = 0; offset < logical.Length; offset += blockSize.Value)
        {
            var raw = logical.Slice(offset, Math.Min(blockSize.Value, logical.Length - offset));
            var compressed = Lz4BlockEncoder.Encode(raw);
            var storedRaw = compressed.Length >= raw.Length;
            var physical = (storedRaw ? raw.ToArray() : compressed).ToArray();
            if (sourceChunk.LostArkFlags == 1)
                _protectionProvider.ProtectBlock(summary, sourceChunk, encodedBlocks.Count, physical);
            var decodedStored = physical.ToArray();
            if (sourceChunk.LostArkFlags == 1)
                _protectionProvider.UnprotectBlock(summary, sourceChunk, encodedBlocks.Count, decodedStored);
            var decoded = storedRaw ? decodedStored : Lz4BlockDecoder.Decode(decodedStored, raw.Length);
            if (!decoded.AsSpan().SequenceEqual(raw))
                Refuse("SMS047", $"Chunk {chunkIndex} block {encodedBlocks.Count} failed decode-back.");
            var protectedPrefix = sourceChunk.LostArkFlags == 1
                ? Math.Min(AesEcbPrefixProtectionProvider.ProtectedPrefixBytes, physical.Length)
                : 0;
            protectedPrefix -= protectedPrefix % 16;
            evidence.Add(new SkeletalMeshStagedUpkBlockEvidence(
                encodedBlocks.Count, raw.Length, physical.Length, protectedPrefix,
                storedRaw, Hash(physical), true));
            encodedBlocks.Add((physical, raw.Length));
        }
        var storedBytes = encodedBlocks.Sum(item => item.Physical.Length);
        using var output = new MemoryStream(16 + encodedBlocks.Count * 8 + storedBytes);
        using var writer = new BinaryWriter(output);
        writer.Write(PackageConstants.UnrealPackageTag);
        writer.Write(blockSize.Value);
        writer.Write(storedBytes);
        writer.Write(logical.Length);
        foreach (var block in encodedBlocks)
        {
            writer.Write(block.Physical.Length);
            writer.Write(block.RawBytes);
        }
        foreach (var block in encodedBlocks) writer.Write(block.Physical);
        return new EncodedChunk(output.ToArray(), evidence);
    }

    private static IReadOnlyList<CompressedChunk> PlanChunks(
        IReadOnlyList<CompressedChunk> source,
        IReadOnlyDictionary<int, EncodedChunk> encoded,
        uint firstPhysicalOffset)
    {
        var planned = new List<CompressedChunk>(source.Count);
        long outputCursor = firstPhysicalOffset;
        long previousSourceEnd = firstPhysicalOffset;
        for (var index = 0; index < source.Count; index++)
        {
            var chunk = source[index];
            outputCursor = checked(outputCursor + ((long)chunk.CompressedOffset - previousSourceEnd));
            var physicalBytes = encoded.TryGetValue(index, out var rebuilt)
                ? rebuilt.PhysicalBytes.LongLength
                : chunk.CompressedSize;
            planned.Add(new CompressedChunk(
                chunk.UncompressedOffset, chunk.UncompressedSize, checked((uint)outputCursor),
                checked((uint)physicalBytes), chunk.LostArkFlags));
            outputCursor = checked(outputCursor + physicalBytes);
            previousSourceEnd = checked((long)chunk.CompressedEnd);
        }
        return planned;
    }

    private static IReadOnlyList<CompressedChunk> PlanChunks(
        IReadOnlyList<CompressedChunk> source,
        IReadOnlyDictionary<int, EncodedChunk> encoded,
        IReadOnlyList<int> outputLogicalSizes,
        uint firstPhysicalOffset)
    {
        if (source.Count != outputLogicalSizes.Count)
            throw new ArgumentException("Output logical-size plan does not match the chunk count.");
        var planned = new List<CompressedChunk>(source.Count);
        long outputCursor = firstPhysicalOffset;
        long previousSourceEnd = firstPhysicalOffset;
        for (var index = 0; index < source.Count; index++)
        {
            var chunk = source[index];
            outputCursor = checked(outputCursor + ((long)chunk.CompressedOffset - previousSourceEnd));
            var physicalBytes = encoded.TryGetValue(index, out var rebuilt)
                ? rebuilt.PhysicalBytes.LongLength
                : chunk.CompressedSize;
            planned.Add(new CompressedChunk(
                chunk.UncompressedOffset, checked((uint)outputLogicalSizes[index]),
                checked((uint)outputCursor), checked((uint)physicalBytes), chunk.LostArkFlags));
            outputCursor = checked(outputCursor + physicalBytes);
            previousSourceEnd = checked((long)chunk.CompressedEnd);
        }
        return planned;
    }

    private static void ApplyChunkTable(
        byte[] destination,
        PackageSummary summary,
        IReadOnlyList<CompressedChunk> outputChunks,
        List<SkeletalMeshStagedUpkTablePatchEvidence>? evidence,
        long firstLogicalOffset,
        bool logicalPrefixOnly)
    {
        for (var index = 0; index < outputChunks.Count; index++)
        {
            var source = summary.CompressedChunks[index];
            var output = outputChunks[index];
            var offset = summary.CompressedChunkTableOffset + index * 20L;
            Patch("UncompressedOffset", offset, source.UncompressedOffset, output.UncompressedOffset);
            Patch("UncompressedSize", offset + 4, source.UncompressedSize, output.UncompressedSize);
            Patch("CompressedOffset", offset + 8, source.CompressedOffset, output.CompressedOffset);
            Patch("CompressedSize", offset + 12, source.CompressedSize, output.CompressedSize);
            Patch("LostArkFlags", offset + 16, source.LostArkFlags, output.LostArkFlags);

            void Patch(string field, long fieldOffset, uint sourceValue, uint outputValue)
            {
                if (logicalPrefixOnly && fieldOffset + 4 > firstLogicalOffset) return;
                BinaryPrimitives.WriteUInt32LittleEndian(
                    destination.AsSpan(checked((int)fieldOffset), 4), outputValue);
                evidence?.Add(new SkeletalMeshStagedUpkTablePatchEvidence(
                    index, field, fieldOffset, sourceValue, outputValue, sourceValue != outputValue));
            }
        }
    }

    private async Task<byte[]> ReadLogicalAsync(
        PackageSummary summary,
        CancellationToken cancellationToken)
    {
        using var logical = new LogicalPackageStream(summary, _protectionProvider);
        var bytes = new byte[checked((int)logical.Length)];
        await logical.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static async Task VerifyPhysicalChunksAsync(
        string sourcePath,
        string outputPath,
        PackageSummary outputSummary,
        IReadOnlyList<SkeletalMeshStagedUpkChunkEvidence> evidence,
        CancellationToken cancellationToken)
    {
        if (outputSummary.CompressedChunks.Count != evidence.Count)
            Refuse("SMS049", "The rebuilt physical chunk count changed after reopening.");
        if (evidence.Count == 0) return;
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferBytes,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        await using var output = new FileStream(outputPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
        for (var index = 0; index < evidence.Count; index++)
        {
            var item = evidence[index];
            var reopened = outputSummary.CompressedChunks[index];
            if (reopened.UncompressedOffset != item.LogicalOffset ||
                reopened.UncompressedSize != item.LogicalBytes ||
                reopened.CompressedOffset != item.OutputPhysicalOffset ||
                reopened.CompressedSize != item.OutputPhysicalBytes ||
                reopened.LostArkFlags != item.LostArkFlags)
                Refuse("SMS049", $"Chunk {index} no longer matches its written table evidence.");
            var outputBytes = new byte[checked((int)item.OutputPhysicalBytes)];
            output.Position = item.OutputPhysicalOffset;
            await output.ReadExactlyAsync(outputBytes, cancellationToken).ConfigureAwait(false);
            if (!Hash(outputBytes).Equals(item.OutputPhysicalSha256, StringComparison.OrdinalIgnoreCase))
                Refuse("SMS049", $"Chunk {index} physical bytes changed after writing.");
            if (item.Rebuilt) continue;
            var sourceBytes = new byte[checked((int)item.SourcePhysicalBytes)];
            source.Position = item.SourcePhysicalOffset;
            await source.ReadExactlyAsync(sourceBytes, cancellationToken).ConfigureAwait(false);
            if (!sourceBytes.AsSpan().SequenceEqual(outputBytes))
                Refuse("SMS049", $"Unaffected chunk {index} is not byte-identical to its source.");
        }
    }

    private static async Task CopyAsync(
        Stream source,
        Stream output,
        long bytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferBytes];
        while (bytes > 0)
        {
            var count = checked((int)Math.Min(bytes, buffer.Length));
            var read = await source.ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The source UPK was truncated.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            bytes -= read;
        }
    }

    private static string SafePhysicalName(string value)
    {
        var name = Path.GetFileName(value);
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".upk", StringComparison.OrdinalIgnoreCase))
            Refuse("SMS048", "The source physical package name is invalid.");
        return name;
    }

    private static string ResolveWithin(string directory, string relativePath, string code)
    {
        var path = Path.GetFullPath(Path.Combine(directory, relativePath));
        var prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            Refuse(code, $"A manifest artifact is missing or escapes its session: {relativePath}");
        return path;
    }

    private static string RequireFile(string path, string code, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) Refuse(code, $"The {label} path is empty.");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) Refuse(code, $"The {label} was not found: {fullPath}");
        return fullPath;
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                   .ConfigureAwait(false) ?? throw new InvalidDataException($"JSON was empty: {path}");
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false));
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
                             temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    [DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new SkeletalMeshStagedUpkRefusalException(code, message);

    private sealed record EncodedChunk(
        byte[] PhysicalBytes,
        IReadOnlyList<SkeletalMeshStagedUpkBlockEvidence> Blocks);

    private sealed record BuildEvidence(
        byte[] ExpectedLogical,
        IReadOnlyList<SkeletalMeshStagedUpkChunkEvidence> Chunks,
        IReadOnlyList<SkeletalMeshStagedUpkTablePatchEvidence> TablePatches);

    private sealed record RawExportLayout(
        int ExportIndex,
        long StartOffset,
        long EndOffset,
        long SerialSizeFieldOffset,
        long SerialOffsetFieldOffset,
        int SerialSize,
        int SerialOffset);
}
