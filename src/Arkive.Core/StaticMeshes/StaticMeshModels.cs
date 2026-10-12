using Arkive.Core.Assets;
using Arkive.Core.Inspection;

namespace Arkive.Core.StaticMeshes;

public enum StaticMeshReadiness
{
    Refused,
    EvidenceOnly,
    Editable,
}

public sealed record StaticMeshReconnaissanceRequest(
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

public sealed record StaticMeshBufferEvidence(
    string Kind,
    long HeaderRelativeOffset,
    long DataRelativeOffset,
    int ElementSize,
    int ElementCount,
    long DataBytes,
    string DataSha256);

public sealed record StaticMeshSectionEvidence(
    int SectionIndex,
    int MaterialReference,
    string MaterialName,
    int FirstIndex,
    int TriangleCount,
    int MinimumVertexIndex,
    int MaximumVertexIndex,
    int ShadowCasting,
    int SerializedBytes,
    long RelativeOffset);

public sealed record StaticMeshLodEvidence(
    int LodIndex,
    long RelativeOffset,
    long SerializedBytes,
    int VertexCount,
    int TextureCoordinateCount,
    bool UsesFullPrecisionUvs,
    IReadOnlyList<StaticMeshSectionEvidence> Sections,
    StaticMeshBufferEvidence Positions,
    StaticMeshBufferEvidence VertexAttributes,
    StaticMeshBufferEvidence? Colours,
    StaticMeshBufferEvidence Indices,
    StaticMeshBufferEvidence WireframeIndices,
    StaticMeshBufferEvidence ExtraIndices,
    long LegacyBulkRelativeOffset,
    int LegacyBulkSerializedBytes,
    string SerializedSha256);

public sealed record StaticMeshNativeLayoutEvidence(
    string ParserProfile,
    long NativeDataRelativeOffset,
    long ParsedEndRelativeOffset,
    int InternalVersion,
    int BodySetupReference,
    int CollisionNodeCount,
    int CollisionTriangleCount,
    bool CollisionPreservedAsOpaqueSourceData,
    bool HasExtraLod,
    IReadOnlyList<StaticMeshLodEvidence> Lods,
    string PrefixSha256,
    string TrailingDataSha256,
    int TrailingDataBytes,
    bool ParsedToEnd);

public sealed record StaticMeshOracleEvidence(
    string Provider,
    string ReportedClassName,
    int LodCount,
    IReadOnlyList<string> Materials,
    IReadOnlyList<SkeletalMeshLodEvidence> Lods,
    string RawOutputSha256,
    double DurationSeconds);

public sealed record StaticMeshReconnaissanceReport(
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
    IReadOnlyList<SkeletalMeshPropertyEvidence> Properties,
    StaticMeshOracleEvidence? Oracle,
    StaticMeshNativeLayoutEvidence? NativeLayout,
    IReadOnlyList<SkeletalMeshStorageEvidence> ExportStorage,
    StaticMeshReadiness Readiness,
    IReadOnlyList<SkeletalMeshReconnaissanceIssue> Issues,
    TimeSpan Duration);

public sealed record StaticMeshReconnaissanceEntry(
    string DirectoryPath,
    string JsonPath,
    string TextPath,
    string? OracleTextPath,
    StaticMeshReconnaissanceReport Report);

public sealed record StaticMeshDecodedVertex(
    float PositionX,
    float PositionY,
    float PositionZ,
    float TangentX,
    float TangentY,
    float TangentZ,
    float TangentW,
    float NormalX,
    float NormalY,
    float NormalZ,
    float NormalW,
    IReadOnlyList<(float U, float V)> TextureCoordinates,
    byte ColourR,
    byte ColourG,
    byte ColourB,
    byte ColourA);

public sealed record StaticMeshDecodedLod(
    StaticMeshLodEvidence Evidence,
    IReadOnlyList<StaticMeshDecodedVertex> Vertices,
    IReadOnlyList<ushort> Indices);

public sealed record StaticMeshEditableExportRequest(
    StaticMeshReconnaissanceReport Reconnaissance,
    string ReconnaissanceJsonPath,
    string OutputDirectory);

public sealed record StaticMeshEditableLodManifest(
    int LodIndex,
    int VertexCount,
    int IndexCount,
    int TriangleCount,
    int SectionCount,
    int TextureCoordinateCount,
    bool UsesFullPrecisionUvs,
    int StableVertexIdTextureCoordinate,
    string NativePositionSha256,
    string NativeAttributeSha256,
    string NativeIndexSha256,
    string DecodedPositionSha256,
    string StableVertexIdSha256);

public sealed record StaticMeshEditableExportManifest(
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
    string ReconnaissanceJsonPath,
    string NativeParserProfile,
    string CoordinateConversion,
    string StableVertexAttribute,
    string GlbRelativePath,
    string GlbSha256,
    long GlbBytes,
    IReadOnlyList<StaticMeshEditableLodManifest> Lods,
    double DurationSeconds);

public sealed record StaticMeshEditableExportResult(
    string OutputDirectory,
    string GlbPath,
    string ManifestPath,
    StaticMeshEditableExportManifest Manifest,
    TimeSpan Duration);

public enum StaticMeshEditReadiness
{
    Refused,
    ReadyForSameTopologyCompilation,
    ReadyForTopologyCompilation,
}

public sealed record StaticMeshEditPreflightReport(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    StaticMeshEditReadiness Readiness,
    bool TopologyChanged,
    bool MaterialSlotsMatch,
    bool EveryVertexMapped,
    bool GeometryFinite,
    bool BoundsReasonable,
    bool CollisionWillRemainFromSource,
    IReadOnlyList<string> Issues);

public sealed record StaticMeshEditManifest(
    int SchemaVersion,
    string EditId,
    DateTimeOffset CreatedAt,
    string DisplayName,
    string SourceEditableManifestPath,
    string EditedGlbPath,
    string EditedGlbSha256,
    StaticMeshEditPreflightReport Preflight,
    string? CompilationManifestPath = null,
    string? StagedUpkManifestPath = null)
{
    public bool IncludedInPackageBuild { get; init; } = true;
}

public sealed record StaticMeshEditEntry(
    string DirectoryPath,
    string ManifestPath,
    StaticMeshEditManifest Manifest);

public sealed record StaticMeshCompilationManifest(
    int SchemaVersion,
    string CompilationId,
    DateTimeOffset CreatedAt,
    string EditManifestPath,
    string SourceEditableManifestPath,
    string SourcePackagePath,
    string PhysicalPackageName,
    string PackageAlias,
    string ObjectName,
    string ClassName,
    int NativeExportIndex,
    long SerialOffset,
    long SourceSerialSize,
    long CompiledSerialSize,
    long SerialSizeDelta,
    string SourcePackageSha256,
    string SourceExportSha256,
    string EditedGlbSha256,
    string CompiledExportRelativePath,
    string CompiledExportSha256,
    string DecodedProofGlbRelativePath,
    string DecodedProofGlbSha256,
    bool TopologyChanged,
    bool CollisionPreservedFromSource,
    bool DecodeBackPassed,
    IReadOnlyList<string> Checks,
    double DurationSeconds);

public sealed record StaticMeshCompilationResult(
    string OutputDirectory,
    string CompiledExportPath,
    string DecodedProofGlbPath,
    string ManifestPath,
    StaticMeshCompilationManifest Manifest,
    TimeSpan Duration);

public sealed class StaticMeshRefusalException : NotSupportedException
{
    public StaticMeshRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}
