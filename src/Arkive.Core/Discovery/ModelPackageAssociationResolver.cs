namespace Arkive.Core.Discovery;

/// <summary>
/// Resolves EFDL model wrappers whose serialized object points at a sibling
/// package rather than the wrapper package's own family. Lost Ark uses this for
/// some NPC variants, for example EFDLChar_PR_LHEN_00.PR_LHEN_01 while the
/// inspectable mesh lives in package PR_LHEN_01.
/// </summary>
internal static class ModelPackageAssociationResolver
{
    public static IReadOnlyList<GameAssetAssociation> Expand(
        IReadOnlyList<GameAssetAssociation> associations)
    {
        var expanded = new List<GameAssetAssociation>();
        foreach (var association in associations)
        {
            if (!association.Field.Contains("Model", StringComparison.OrdinalIgnoreCase) ||
                association.EntityType.Equals("Item", StringComparison.OrdinalIgnoreCase) ||
                !GameAssetMetadataIndex.NormalizePackageName(association.PackageName)
                    .StartsWith("EFDL", StringComparison.Ordinal))
            {
                continue;
            }

            var objectName = GameAssetMetadataIndex.NormalizeObjectName(association.ObjectName);
            if (string.IsNullOrWhiteSpace(objectName))
            {
                continue;
            }

            var wrapperFamily = GameAssetMetadataIndex.PackageNameVariants(
                    association.PackageName)
                .Last();
            if (GameAssetMetadataIndex.IsAssetFamilyMember(objectName, wrapperFamily) ||
                GameAssetMetadataIndex.IsAssetFamilyMember(wrapperFamily, objectName))
            {
                continue;
            }

            expanded.Add(association with
            {
                Field = $"{association.Field}.ResolvedPackage",
                AssetReference = $"{objectName}.{objectName}",
                PackageName = objectName,
                ObjectName = objectName,
                Confidence = GameAssetAssociationConfidence.High,
                Relation = GameAssetAssociationRelation.DeterministicModelAsset,
                SourceAssetReference = association.AssetReference,
            });
        }

        return expanded;
    }
}
