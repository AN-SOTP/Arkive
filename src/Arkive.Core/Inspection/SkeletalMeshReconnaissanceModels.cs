using Arkive.Core.Assets;
using Arkive.Core.Meshes;

namespace Arkive.Core.Inspection;

public enum SkeletalMeshReadiness
{
    Refused,
    EvidenceOnly,
    NoOpReady,
}

public sealed record SkeletalMeshReconnaissanceRequest(
    string PackagePath,
    string PackageAlias,
    int ExportIndex,
    AssetKind Kind,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    string? KnownPackageSha256 = null)
{
    public bool SkipUmodelOracle { get; init; }
}

public sealed record SkeletalMeshPropertyEvidence(
    long RelativeOffset,
    long LogicalOffset,
    string Name,
    string Type,
    int Size,
    int ArrayIndex,
    string? TypeMetadata,
    long ValueRelativeOffset,
    string ValuePreview,
    string RawValuePrefix);

public sealed record SkeletalMeshSectionEvidence(
    int SectionIndex,
    string Material,
    int FirstIndex,
    int TriangleCount);

public sealed record SkeletalMeshLodEvidence(
    int LodIndex,
    int VertexCount,
    int IndexCount,
    int TriangleCount,
    int TextureCoordinateCount,
    IReadOnlyList<SkeletalMeshSectionEvidence> Sections);

public sealed record SkeletalMeshNativeRangeEvidence(
    string Name,
    long RelativeOffset,
    long Size,
    string Sha256,
    bool IsParsed,
    bool IsEditableCandidate);

public sealed record SkeletalMeshNativeBoneEvidence(
    int BoneIndex,
    string Name,
    int ParentIndex,
    int ChildCount,
    float OrientationX,
    float OrientationY,
    float OrientationZ,
    float OrientationW,
    float PositionX,
    float PositionY,
    float PositionZ,
    long RelativeOffset,
    string RawSha256);

public sealed record SkeletalMeshNativeSectionEvidence(
    int SectionIndex,
    short MaterialIndex,
    short ChunkIndex,
    int FirstIndex,
    int TriangleCount,
    byte Flags,
    long RelativeOffset,
    int SerializedBytes);

public sealed record SkeletalMeshNativeChunkEvidence(
    int ChunkIndex,
    int FirstVertex,
    int RigidVertexCount,
    int SoftVertexCount,
    int DeclaredRigidVertexCount,
    int DeclaredSoftVertexCount,
    int MaxInfluences,
    IReadOnlyList<short> BoneMap,
    long RelativeOffset,
    int SerializedBytes);

public sealed record SkeletalMeshNativeBufferEvidence(
    string BufferKind,
    long HeaderRelativeOffset,
    long DataRelativeOffset,
    int ElementSize,
    int ElementCount,
    long DataBytes,
    string DataSha256);

public sealed record SkeletalMeshNativeLodEvidence(
    int LodIndex,
    long RelativeOffset,
    long SerializedBytes,
    int VertexCount,
    int TextureCoordinateCount,
    int? LostArkPreRequiredBonesValue,
    int RequiredBoneElementSize,
    bool UsesFullPrecisionUvs,
    bool SerializedPackedPosition,
    int VertexPositionOffset,
    IReadOnlyList<SkeletalMeshNativeSectionEvidence> Sections,
    IReadOnlyList<SkeletalMeshNativeChunkEvidence> Chunks,
    SkeletalMeshNativeBufferEvidence IndexBuffer,
    SkeletalMeshNativeBufferEvidence VertexBuffer,
    SkeletalMeshNativeBufferEvidence? VertexColorBuffer,
    SkeletalMeshNativeBufferEvidence AdjacencyBuffer,
    IReadOnlyList<short> ActiveBones,
    IReadOnlyList<short> RequiredBones,
    IReadOnlyList<SkeletalMeshNativeRangeEvidence> Ranges);

public sealed record SkeletalMeshNativeLayoutEvidence(
    string ParserProfile,
    long NativeDataRelativeOffset,
    long ParsedEndRelativeOffset,
    int MaterialReferenceCount,
    IReadOnlyList<int> MaterialReferences,
    int BoneCount,
    int SkeletalDepth,
    int LostArkExtensionVersion,
    IReadOnlyList<int> LostArkExtensionValues,
    IReadOnlyList<SkeletalMeshNativeBoneEvidence> Bones,
    IReadOnlyList<SkeletalMeshNativeLodEvidence> Lods,
    IReadOnlyList<SkeletalMeshNativeRangeEvidence> Ranges,
    bool AllRangesInBounds,
    bool ParsedToEnd,
    string TrailingDataSha256);

public sealed record SkeletalMeshOracleEvidence(
    string Provider,
    string ReportedClassName,
    int LodCount,
    int BoneCount,
    int SocketCount,
    bool? HasVertexColors,
    IReadOnlyList<string> Materials,
    IReadOnlyList<SkeletalMeshLodEvidence> Lods,
    string RawOutputSha256,
    double DurationSeconds);

public sealed record SkeletalMeshStorageEvidence(
    string StorageKind,
    int? ChunkNumber,
    long LogicalOffset,
    long LogicalSize,
    long PhysicalOffset,
    long PhysicalSize,
    uint? LostArkFlags);

public sealed record SkeletalMeshReconnaissanceIssue(
    string Severity,
    string Code,
    string Message);

public sealed record SkeletalMeshPackageProfile(
    ushort FileVersion,
    ushort LicenseeVersion,
    int EngineVersion,
    int CookerVersion,
    uint PackageFlags,
    uint CompressionFlags,
    Guid PackageGuid,
    int ChunkCount,
    long LogicalLength);

public sealed record SkeletalMeshReconnaissanceReport(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string PackagePath,
    string PhysicalPackageName,
    string PackageAlias,
    long PackageLength,
    long PackageLastWriteUtcTicks,
    string PackageSha256,
    SkeletalMeshPackageProfile PackageProfile,
    string ProtectionProviderName,
    int ExportIndex,
    int? NativeExportIndex,
    AssetKind Kind,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    string ExportPayloadSha256,
    long? PropertyStreamRelativeOffset,
    long? NativeDataRelativeOffset,
    long? NativeDataSize,
    string? NativeDataSha256,
    IReadOnlyList<SkeletalMeshPropertyEvidence> Properties,
    SkeletalMeshOracleEvidence? Oracle,
    SkeletalMeshNativeLayoutEvidence? NativeLayout,
    IReadOnlyList<SkeletalMeshStorageEvidence> ExportStorage,
    SkeletalMeshReadiness Readiness,
    IReadOnlyList<SkeletalMeshReconnaissanceIssue> Issues,
    TimeSpan Duration)
{
    public SkeletalMeshDeformationDependencyReport? DeformationDependencies { get; init; }
}

public sealed record SkeletalMeshReconnaissanceEntry(
    string DirectoryPath,
    string JsonPath,
    string TextPath,
    string? OracleTextPath,
    SkeletalMeshReconnaissanceReport Report);
