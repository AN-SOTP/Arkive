namespace Arkive.Core.Meshes;

public enum SkeletalMeshDependencyState
{
    None,
    InactivePlaceholder,
    ActiveVertexBoundData,
    Unresolved,
}

public sealed record SkeletalMeshClothBindingEvidence(
    string PropertyName,
    string PropertyType,
    int DeclaredReferenceCount,
    IReadOnlyList<int> ObjectReferences,
    IReadOnlyList<string> ResolvedObjects,
    bool IsActive,
    string Detail);

public sealed record SkeletalMeshMorphLodEvidence(
    int LodIndex,
    int DeltaCount,
    int BaseMeshVertexCount,
    int MaximumSourceVertexIndex,
    bool SourceIndicesInRange);

public sealed record SkeletalMeshMorphTargetEvidence(
    int ExportIndex,
    string ObjectName,
    string ObjectPath,
    long SerialSize,
    bool Parsed,
    int DeltaCount,
    IReadOnlyList<SkeletalMeshMorphLodEvidence> Lods,
    string Detail);

public sealed record SkeletalMeshMorphSetEvidence(
    int ExportIndex,
    string ObjectName,
    string ObjectPath,
    int? BaseSkeletalMeshReference,
    bool BaseSkeletalMeshMatches,
    int DeclaredTargetCount,
    int ResolvedTargetCount,
    IReadOnlyList<SkeletalMeshMorphTargetEvidence> Targets);

public sealed record SkeletalMeshDeformationDependencyIssue(
    string Severity,
    string Code,
    string Message);

public sealed record SkeletalMeshDeformationDependencyReport(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string PackagePath,
    string PackageSha256,
    int MeshExportIndex,
    string ObjectName,
    SkeletalMeshDependencyState ClothState,
    SkeletalMeshDependencyState MorphState,
    int ActiveClothReferenceCount,
    int MorphTargetSetCount,
    int MorphTargetCount,
    int MorphDeltaCount,
    bool TopologyPreservingSupported,
    bool TopologyChangingSupported,
    string TopologyChangingReason,
    IReadOnlyList<SkeletalMeshClothBindingEvidence> ClothBindings,
    IReadOnlyList<SkeletalMeshMorphSetEvidence> MorphSets,
    IReadOnlyList<SkeletalMeshDeformationDependencyIssue> Issues)
{
    public bool HasActiveClothData => ClothState == SkeletalMeshDependencyState.ActiveVertexBoundData;

    public bool HasActiveMorphTargets => MorphState == SkeletalMeshDependencyState.ActiveVertexBoundData;

    public bool IsResolved => ClothState != SkeletalMeshDependencyState.Unresolved &&
                              MorphState != SkeletalMeshDependencyState.Unresolved;
}
