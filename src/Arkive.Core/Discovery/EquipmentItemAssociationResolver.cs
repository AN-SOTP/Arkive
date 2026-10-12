using System.Text.RegularExpressions;

namespace Arkive.Core.Discovery;

/// <summary>
/// Maps numbered equipment database variants to the shared inspectable mesh
/// package. The August Wave equipment uses -1/-2 wrapper objects for style
/// variants while the UPK stores one WP_*/IT_* base mesh and texture family.
/// </summary>
internal static partial class EquipmentItemAssociationResolver
{
    public static IReadOnlyList<GameAssetAssociation> Expand(
        IReadOnlyList<GameAssetAssociation> associations)
    {
        var expanded = new List<GameAssetAssociation>();
        foreach (var association in associations)
        {
            if (!association.EntityType.Equals("Item", StringComparison.OrdinalIgnoreCase) ||
                !(association.Field.Equals("Model", StringComparison.OrdinalIgnoreCase) ||
                  association.Field.Equals("InstrumentModel", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var objectName = GameAssetMetadataIndex.NormalizeObjectName(association.ObjectName);
            var match = NumberedEquipmentRegex().Match(objectName);
            if (!match.Success)
            {
                continue;
            }

            var family = match.Groups["family"].Value;
            expanded.Add(association with
            {
                Field = $"{association.Field}.SharedVariant",
                AssetReference = $"{family}.{family}",
                PackageName = family,
                ObjectName = family,
                Confidence = GameAssetAssociationConfidence.High,
                Relation = GameAssetAssociationRelation.DeterministicItemAsset,
                SourceAssetReference = association.AssetReference,
            });
        }

        return expanded;
    }

    [GeneratedRegex(
        @"^(?<family>(?:WP|IT)_.+_AV_[A-Z0-9]+)-\d+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedEquipmentRegex();
}
