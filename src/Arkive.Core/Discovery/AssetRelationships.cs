namespace Arkive.Core.Discovery;

public enum AssetRelationshipEvidence
{
    Seed = 0,
    DirectDatabaseReference = 1,
    DatabaseAssetFamily = 2,
    SamePackage = 3,
    PackageFamily = 4,
    ObjectNameFamily = 5,
}

public enum AssetRelationshipConfidence
{
    Authoritative,
    High,
    Contextual,
    Inferred,
}

public sealed record AssetRelationshipMatch(
    PackageIndexMatch Asset,
    AssetRelationshipEvidence Evidence,
    AssetRelationshipConfidence Confidence,
    string Reason,
    GameAssetAssociation? EvidenceAssociation);

public sealed record AssetRelationshipResult(
    PackageIndexMatch Seed,
    IReadOnlyList<GameAssetAssociation> EntityAssociations,
    IReadOnlyList<AssetRelationshipMatch> Matches,
    int TotalCandidates,
    int PackageCount,
    int FailedPackageCount,
    bool WasTruncated,
    TimeSpan Duration);
