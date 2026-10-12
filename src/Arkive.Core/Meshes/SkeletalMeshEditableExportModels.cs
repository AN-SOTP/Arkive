using Arkive.Core.Inspection;

namespace Arkive.Core.Meshes;

public sealed record SkeletalMeshEditableExportRequest(
    SkeletalMeshReconnaissanceReport Reconnaissance,
    string ReconnaissanceJsonPath,
    string OutputDirectory);

public sealed record SkeletalMeshEditableBounds(
    float MinimumX,
    float MinimumY,
    float MinimumZ,
    float MaximumX,
    float MaximumY,
    float MaximumZ);

public sealed record SkeletalMeshEditableLodManifest(
    int LodIndex,
    string MeshName,
    string NodeName,
    int VertexCount,
    int IndexCount,
    int TriangleCount,
    int SectionCount,
    int ChunkCount,
    int TextureCoordinateCount,
    int VertexStride,
    int NativePositionOffset,
    int NativeBoneIndexWidth,
    bool UsesWideBoneIndices,
    int StableVertexIdTextureCoordinate,
    bool WindingReversedForGltf,
    int FaceOrientationTriangleCount,
    int DegenerateTriangleCount,
    int OpposedFaceNormalTriangleCount,
    double OpposedFaceNormalPercent,
    long NativeIndexDataRelativeOffset,
    long NativeVertexDataRelativeOffset,
    string NativeIndexDataSha256,
    string NativeVertexDataSha256,
    string StableVertexIdSha256,
    string StableVertexIdUvSha256,
    string DecodedPositionSha256,
    string DecodedNormalSha256,
    string DecodedTangentSha256,
    string DecodedJointSha256,
    string DecodedWeightSha256,
    IReadOnlyList<string> DecodedUvSha256,
    string? DecodedColorSha256,
    SkeletalMeshEditableBounds Bounds);

public sealed record SkeletalMeshEditableExportManifest(
    int SchemaVersion,
    string ExportId,
    DateTimeOffset CreatedAt,
    string SourcePackagePath,
    string PhysicalPackageName,
    string PackageAlias,
    long SourcePackageLength,
    long SourcePackageLastWriteUtcTicks,
    string SourcePackageSha256,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    string SourceExportSha256,
    int ReconnaissanceSchemaVersion,
    string ReconnaissanceJsonPath,
    string NativeParserProfile,
    string ReferenceSkeletonSha256,
    int BoneCount,
    bool HasOpaqueTrailingNativeData,
    string CoordinateConversion,
    string StableVertexAttribute,
    string GlbRelativePath,
    string GlbSha256,
    long GlbBytes,
    int GlbInspectionErrors,
    int GlbInspectionWarnings,
    IReadOnlyList<SkeletalMeshEditableLodManifest> Lods,
    double DurationSeconds);

public sealed record SkeletalMeshEditableExportResult(
    string OutputDirectory,
    string GlbPath,
    string ManifestPath,
    SkeletalMeshEditableExportManifest Manifest,
    ArtifactInspectionResult Inspection,
    TimeSpan Duration);

public sealed class SkeletalMeshEditableExportRefusalException : NotSupportedException
{
    public SkeletalMeshEditableExportRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}
