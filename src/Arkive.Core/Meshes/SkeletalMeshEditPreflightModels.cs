namespace Arkive.Core.Meshes;

public enum SkeletalMeshEditReadiness
{
    Refused,
    ReadyNoChanges,
    ReadyForPositionTangentEncoding,
    ReadyForTopologyCompilation,
}

public enum SkeletalMeshImportProfile
{
    TopologyPreserving,
    ReplaceLod0AndDuplicate,
}

public enum SkeletalMeshEditIssueSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record SkeletalMeshEditIssue(
    SkeletalMeshEditIssueSeverity Severity,
    string Code,
    string Message);

public sealed record SkeletalMeshEditLodPreflight(
    int LodIndex,
    int SourceVertexCount,
    int CandidateVertexCount,
    int SourceTriangleCount,
    int CandidateTriangleCount,
    int SourcePrimitiveCount,
    int CandidatePrimitiveCount,
    string StableVertexIdCarrier,
    int ChangedPositionVertexCount,
    double MaximumPositionDeltaMetres,
    double AverageChangedPositionDeltaMetres,
    int ChangedNormalVertexCount,
    int ChangedTangentVertexCount,
    bool CandidateTangentsPresent,
    bool StableVertexIdsMatch,
    bool TopologyMatches,
    bool TextureCoordinatesMatch,
    bool SkinningMatches,
    bool VertexColoursMatch);

public sealed record SkeletalMeshTopologyLodPlan(
    int TargetLodIndex,
    int SourceVertexCount,
    int SourceTriangleCount,
    int CompiledVertexCount,
    int CompiledTriangleCount,
    string Source);

public sealed record SkeletalMeshTopologyPreflight(
    int ImportedLodIndex,
    string LodStrategy,
    int SourceLodCount,
    int CandidateLodCount,
    int IgnoredCandidateLodCount,
    int CandidateVertexCount,
    int CandidateTriangleCount,
    int CandidatePrimitiveCount,
    int CandidateTextureCoordinateCount,
    int CandidateMaterialCount,
    int CandidateUsedBoneCount,
    int MaximumInfluencesPerVertex,
    double MaximumSkinWeightSumError,
    int DegenerateTriangleCount,
    int FaceOrientationTriangleCount,
    int OpposedFaceNormalTriangleCount,
    double OpposedFaceNormalPercent,
    bool CandidateNormalsPresent,
    bool CandidateTangentsPresent,
    bool CandidateVertexColoursPresent,
    bool Requires32BitIndices,
    bool SourceHasClothData,
    bool SourceMayHaveLinkedMorphTargets,
    SkeletalMeshEditableBounds CandidateBounds,
    IReadOnlyList<string> CandidateMaterials,
    IReadOnlyList<SkeletalMeshTopologyLodPlan> TargetLods);

public sealed record SkeletalMeshAnimationPosePreflight(
    string PoseName,
    int StressedBoneCount,
    int SourceTestedVertexCount,
    int CandidateTestedVertexCount,
    int SourceTestedTriangleCount,
    int CandidateTestedTriangleCount,
    int NonFiniteCandidateVertexCount,
    double SourceMaximumDisplacementMetres,
    double CandidateMaximumDisplacementMetres,
    double SourceBoundsExpansionRatio,
    double CandidateBoundsExpansionRatio,
    double SourceMaximumEdgeStretchRatio,
    double CandidateMaximumEdgeStretchRatio,
    double SourceCollapsedEdgePercent,
    double CandidateCollapsedEdgePercent,
    double SourceOverstretchedEdgePercent,
    double CandidateOverstretchedEdgePercent);

public sealed record SkeletalMeshAnimationPreflight(
    string ValidationProfile,
    bool Passed,
    bool StaticOnly,
    bool UsesGameAnimationClips,
    int PoseCount,
    int SourceUsedBoneCount,
    int CandidateUsedBoneCount,
    int SignificantSourceBoneCount,
    int MissingSignificantCandidateBoneCount,
    double MissingSignificantSourceWeightPercent,
    double BoneWeightDistributionDistance,
    double MaximumWeightedCentroidShiftMetres,
    double MaximumWeightedCentroidShiftRelativeToSourceExtent,
    double SourceBindReconstructionErrorMetres,
    double CandidateBindReconstructionErrorMetres,
    double CandidateBindExtentRatio,
    int NonFiniteCandidateVertexCount,
    double MaximumCandidateBoundsExpansionRatio,
    double MaximumCandidateEdgeStretchRatio,
    double MaximumCandidateCollapsedEdgePercent,
    double MaximumCandidateOverstretchedEdgePercent,
    IReadOnlyList<int> StressedBones,
    IReadOnlyList<SkeletalMeshAnimationPosePreflight> Poses);

public sealed record SkeletalMeshEditPreflightReport(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string SourceExportId,
    string PackageAlias,
    string ObjectName,
    string SourceManifestPath,
    string SourceGlbPath,
    string SourceGlbSha256,
    string CandidateGlbPath,
    string CandidateGlbSha256,
    long CandidateGlbBytes,
    SkeletalMeshEditReadiness Readiness,
    bool LodStructureMatches,
    bool MaterialsMatch,
    bool SkeletonMatches,
    int IgnoredUnskinnedMeshCount,
    int ChangedPositionVertexCount,
    int ChangedNormalVertexCount,
    int ChangedTangentVertexCount,
    double MaximumPositionDeltaMetres,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<SkeletalMeshEditLodPreflight> Lods,
    IReadOnlyList<SkeletalMeshEditIssue> Issues,
    double DurationSeconds)
{
    public SkeletalMeshImportProfile ImportProfile { get; init; } =
        SkeletalMeshImportProfile.TopologyPreserving;

    public bool TopologyChangeDetected { get; init; }

    public SkeletalMeshTopologyPreflight? Topology { get; init; }

    public SkeletalMeshAnimationPreflight? AnimationValidation { get; init; }

    public SkeletalMeshSkeletonCompatibilityReport? SkeletonCompatibility { get; init; }

    public SkeletalMeshDeformationDependencyReport? DeformationDependencies { get; init; }
}

public sealed record SkeletalMeshEditRegistrationManifest(
    int SchemaVersion,
    string EditId,
    DateTimeOffset CreatedAt,
    string DisplayName,
    string SourceExportId,
    string SourceManifestPath,
    string SourceGlbSha256,
    string PackageAlias,
    string ObjectName,
    string ImportedFromPath,
    string EditedGlbRelativePath,
    string EditedGlbSha256,
    long EditedGlbBytes,
    SkeletalMeshEditPreflightReport Preflight)
{
    public bool IncludedInPackageBuild { get; init; } = true;
}

public sealed record SkeletalMeshEditWorkspaceEntry(
    string DirectoryPath,
    string ManifestPath,
    string EditedGlbPath,
    SkeletalMeshEditRegistrationManifest Manifest);

public sealed record SkeletalMeshEditWorkspaceIssue(
    string ManifestPath,
    string Message);

public sealed record SkeletalMeshEditWorkspaceSnapshot(
    IReadOnlyList<SkeletalMeshEditWorkspaceEntry> Entries,
    IReadOnlyList<SkeletalMeshEditWorkspaceIssue> Issues);

public sealed record SkeletalMeshEditRegistrationResult(
    SkeletalMeshEditWorkspaceEntry Entry,
    SkeletalMeshEditPreflightReport Preflight);
