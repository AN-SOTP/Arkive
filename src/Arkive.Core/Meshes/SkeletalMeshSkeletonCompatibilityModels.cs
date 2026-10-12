namespace Arkive.Core.Meshes;

public enum SkeletalMeshSkeletonCompatibilityKind
{
    ExactTargetSkeleton,
    SameNamedHierarchyAlteredBindPose,
    RetargetAuthoringCandidate,
    ArtistRiggingRequired,
    Incompatible,
}

public enum SkeletalMeshBoneMappingKind
{
    ExactNameAndHierarchy,
    ExactNameHierarchyChanged,
    AmbiguousName,
    MissingFromTarget,
}

public sealed record SkeletalMeshSkeletonBoneProfile(
    int BoneIndex,
    string Name,
    string CanonicalName,
    int ParentIndex,
    string ParentName,
    int Depth,
    string HierarchyPath,
    bool IsCoreDeformationBone,
    double WeightedVertexMassPercent,
    double BindWorldX,
    double BindWorldY,
    double BindWorldZ);

public sealed record SkeletalMeshSkeletonProfile(
    string ExportId,
    string PackageAlias,
    string ObjectName,
    string ManifestPath,
    string GlbPath,
    string GlbSha256,
    int BoneCount,
    int RootBoneCount,
    int UsedBoneCount,
    int CoreBoneCount,
    string NameHierarchySha256,
    string BindPoseSha256,
    IReadOnlyList<SkeletalMeshSkeletonBoneProfile> Bones);

public sealed record SkeletalMeshBoneCompatibilityMapping(
    int SourceBoneIndex,
    string SourceBoneName,
    int? TargetBoneIndex,
    string? TargetBoneName,
    SkeletalMeshBoneMappingKind MappingKind,
    bool ParentChainMatches,
    bool IsCoreDeformationBone,
    double SourceWeightMassPercent,
    double BindTranslationDeltaMetres,
    double BindRotationDeltaDegrees,
    string Diagnostic);

public sealed record SkeletalMeshSkeletonCompatibilityReport(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    SkeletalMeshSkeletonCompatibilityKind Compatibility,
    bool ExactSkeletonIdentity,
    bool AutomaticRetargetAllowed,
    bool BlenderAuthoringWorkspaceAllowed,
    string Summary,
    SkeletalMeshSkeletonProfile Source,
    SkeletalMeshSkeletonProfile Target,
    int ExactNameMappingCount,
    int HierarchyConsistentMappingCount,
    int MissingSourceBoneCount,
    int AmbiguousSourceBoneCount,
    int MissingCoreBoneCount,
    double MappedSourceWeightPercent,
    double HierarchyConsistentSourceWeightPercent,
    double MissingSourceWeightPercent,
    double MaximumMappedBindTranslationDeltaMetres,
    double MaximumMappedBindTranslationRelativeToTargetExtent,
    double MaximumMappedBindRotationDeltaDegrees,
    IReadOnlyList<SkeletalMeshBoneCompatibilityMapping> BoneMappings,
    IReadOnlyList<SkeletalMeshEditIssue> Issues);

public sealed record SkeletalMeshRetargetWorkspaceRequest(
    SkeletalMeshEditableWorkspaceEntry Donor,
    SkeletalMeshEditableWorkspaceEntry Target,
    string OutputDirectory,
    string? DisplayName = null);

public sealed record SkeletalMeshRetargetWorkspaceManifest(
    int SchemaVersion,
    string WorkspaceId,
    DateTimeOffset CreatedAt,
    string DisplayName,
    string DonorExportId,
    string TargetExportId,
    string DonorPackageAlias,
    string DonorObjectName,
    string TargetPackageAlias,
    string TargetObjectName,
    string DonorManifestPath,
    string TargetManifestPath,
    string DonorGlbRelativePath,
    string TargetGlbRelativePath,
    string CompatibilityReportRelativePath,
    string BlenderSetupScriptRelativePath,
    string InstructionsRelativePath,
    string ExpectedCandidateFileName,
    bool AutomaticRetargetPerformed,
    bool RequiresManualGeometryFitting,
    bool RequiresManualWeightPainting,
    SkeletalMeshSkeletonCompatibilityKind Compatibility,
    double MappedSourceWeightPercent,
    int MissingSourceBoneCount,
    int MissingCoreBoneCount,
    IReadOnlyList<string> SafetyRequirements);

public sealed record SkeletalMeshRetargetWorkspaceResult(
    string OutputDirectory,
    string ManifestPath,
    string CompatibilityReportPath,
    string BlenderSetupScriptPath,
    string InstructionsPath,
    SkeletalMeshRetargetWorkspaceManifest Manifest,
    SkeletalMeshSkeletonCompatibilityReport Compatibility);
