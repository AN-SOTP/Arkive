namespace Arkive.Core.Meshes;

public sealed record SkeletalMeshTopologyCompilationRequest(
    SkeletalMeshEditWorkspaceEntry Edit,
    string OutputDirectory);

public sealed record SkeletalMeshTopologyCompiledLodEvidence(
    int LodIndex,
    int CandidateVertexCount,
    int CompiledVertexCount,
    int CandidateTriangleCount,
    int CompiledTriangleCount,
    int SectionCount,
    int ChunkCount,
    int DuplicatedVertexCount,
    int IndexElementSize,
    int VertexStride,
    int TextureCoordinateCount,
    bool UsesFullPrecisionUvs,
    bool HasVertexColours,
    int MaximumInfluences,
    double MaximumWeightQuantizationError,
    double MaximumPositionDecodeErrorMetres,
    double MaximumNormalDecodeError,
    double MaximumTangentDecodeError,
    double MaximumUvDecodeError,
    double MaximumColourDecodeError,
    double MaximumWeightDecodeError,
    bool JointsDecodeExactly,
    bool TriangleMaterialTopologyMatches,
    string CompiledIndexSha256,
    string CompiledVertexSha256,
    string? CompiledColourSha256);

public sealed record SkeletalMeshTopologyCompilationManifest(
    int SchemaVersion,
    string CompilationId,
    DateTimeOffset CreatedAt,
    string EditId,
    string EditDisplayName,
    string EditManifestPath,
    string SourceExportId,
    string SourceEditableManifestPath,
    string SourcePackagePath,
    string PhysicalPackageName,
    string PackageAlias,
    string ObjectName,
    int NativeExportIndex,
    long SerialOffset,
    long SourceSerialSize,
    long CompiledSerialSize,
    long SerialSizeDelta,
    string SourcePackageSha256,
    string SourceExportSha256,
    string EditedGlbSha256,
    string CompiledExportSha256,
    string CompiledExportRelativePath,
    string DecodedProofGlbSha256,
    string DecodedProofGlbRelativePath,
    string NativeParserProfile,
    string PreservedPrefixSha256,
    string PreservedTrailingSha256,
    long PreservedPrefixBytes,
    long PreservedTrailingBytes,
    bool BoundsUpdated,
    bool PrefixPreservedExceptBounds,
    bool TrailingBytesPreserved,
    bool DecodeBackPassed,
    bool PackageIntegrationEnabled,
    IReadOnlyList<SkeletalMeshTopologyCompiledLodEvidence> Lods,
    SkeletalMeshEditPreflightReport Preflight,
    double DurationSeconds);

public sealed record SkeletalMeshTopologyCompilationResult(
    string OutputDirectory,
    string CompiledExportPath,
    string DecodedProofGlbPath,
    string ManifestPath,
    SkeletalMeshTopologyCompilationManifest Manifest,
    TimeSpan Duration);

public sealed class SkeletalMeshTopologyCompilationRefusalException : NotSupportedException
{
    public SkeletalMeshTopologyCompilationRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}
