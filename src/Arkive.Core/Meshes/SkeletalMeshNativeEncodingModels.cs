namespace Arkive.Core.Meshes;

public sealed record SkeletalMeshNativeEncodingRequest(
    SkeletalMeshEditWorkspaceEntry Edit,
    string OutputDirectory);

public sealed record SkeletalMeshNativeEncodingLodEvidence(
    int LodIndex,
    int VertexCount,
    int PositionWriteCount,
    int TangentFrameWriteCount,
    bool TangentsReconstructed,
    string SourceVertexBufferSha256,
    string EncodedVertexBufferSha256,
    long VertexBufferRelativeOffset,
    long VertexBufferBytes,
    double MaximumPositionDecodeErrorMetres,
    double MaximumNormalDecodeError,
    double MaximumTangentDecodeError);

public sealed record SkeletalMeshNativeEncodingManifest(
    int SchemaVersion,
    string EncodingId,
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
    long SerialSize,
    string SourcePackageSha256,
    string SourceExportSha256,
    string EditedGlbSha256,
    string EncodedExportSha256,
    string EncodedExportRelativePath,
    string DecodedProofGlbSha256,
    string DecodedProofGlbRelativePath,
    bool BoundsUpdated,
    long AllowlistedByteCount,
    long ActualChangedByteCount,
    long ChangedLockedByteCount,
    string LockedSourceSha256,
    string LockedEncodedSha256,
    bool LockedBytesPreserved,
    bool DecodeBackPassed,
    IReadOnlyList<SkeletalMeshNativeEncodingLodEvidence> Lods,
    SkeletalMeshEditPreflightReport Preflight,
    double DurationSeconds);

public sealed record SkeletalMeshNativeEncodingResult(
    string OutputDirectory,
    string EncodedExportPath,
    string DecodedProofGlbPath,
    string ManifestPath,
    SkeletalMeshNativeEncodingManifest Manifest,
    TimeSpan Duration);

public sealed class SkeletalMeshNativeEncodingRefusalException : NotSupportedException
{
    public SkeletalMeshNativeEncodingRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}
