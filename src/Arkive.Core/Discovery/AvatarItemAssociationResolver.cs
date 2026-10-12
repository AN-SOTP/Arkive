using System.Text.RegularExpressions;

namespace Arkive.Core.Discovery;

internal static partial class AvatarItemAssociationResolver
{
    private static readonly IReadOnlyDictionary<string, string> KnownBaseArchetypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PC_WR"] = "Male Warrior",
            ["PC_WR_F"] = "Female Warrior",
            ["PC_FT"] = "Female Martial Artist",
            ["PC_FT_M"] = "Male Martial Artist",
            ["PC_GN"] = "Male Gunner",
            ["PC_GN_F"] = "Female Gunner",
            ["PC_MG"] = "Female Mage",
            ["PC_SP"] = "Female Specialist",
            ["PC_DL"] = "Female Assassin",
        };

    public static IReadOnlyList<GameAssetAssociation> AttachArchetypes(
        IReadOnlyList<GameAssetAssociation> associations) =>
        associations.Select(item =>
        {
            if (!item.EntityType.Equals("PC", StringComparison.OrdinalIgnoreCase) ||
                !item.Field.Equals("LookInfo", StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }

            var code = GameAssetMetadataIndex.NormalizeObjectName(item.ObjectName);
            var archetype = KnownBaseArchetypes.TryGetValue(code, out var known)
                ? known
                : FormatArchetype(item.DisplayName);
            return item with { Archetype = archetype };
        }).ToArray();

    public static GameAssetAssociation NormalizeArchetype(GameAssetAssociation association)
    {
        if (!association.EntityType.Equals("Item", StringComparison.OrdinalIgnoreCase) ||
            association.Relation is not (
                GameAssetAssociationRelation.DeterministicItemAsset or
                GameAssetAssociationRelation.VerifiedSerializedDependency))
        {
            return association;
        }

        var source = association.SourceAssetReference ?? association.AssetReference;
        var match = AvatarClassReferenceRegex().Match(source);
        if (!match.Success ||
            !KnownBaseArchetypes.TryGetValue(match.Groups["class"].Value, out var archetype))
        {
            return association;
        }

        var displayName = association.DisplayName;
        var suffix = TrailingArchetypeRegex().Match(displayName);
        if (suffix.Success)
        {
            displayName = displayName[..suffix.Index] + $" ({archetype})";
        }

        return association with
        {
            DisplayName = displayName,
            Archetype = archetype,
        };
    }

    public static IReadOnlyList<GameAssetAssociation> Expand(
        IReadOnlyList<GameAssetAssociation> associations)
    {
        var archetypes = associations
            .Where(item =>
                item.EntityType.Equals("PC", StringComparison.OrdinalIgnoreCase) &&
                item.Field.Equals("LookInfo", StringComparison.OrdinalIgnoreCase))
            .Select(item => new
            {
                Code = GameAssetMetadataIndex.NormalizeObjectName(item.ObjectName),
                Label = item.Archetype ?? FormatArchetype(item.DisplayName),
            })
            .Where(item => item.Code.StartsWith("PC_", StringComparison.Ordinal))
            .GroupBy(item => item.Code, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().Label,
                StringComparer.Ordinal);

        var expanded = new List<GameAssetAssociation>();
        foreach (var association in associations)
        {
            if (!association.EntityType.Equals("Item", StringComparison.OrdinalIgnoreCase) ||
                !association.Field.Equals("Model", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var packageName = GameAssetMetadataIndex.NormalizePackageName(association.PackageName);
            var match = AvatarItemPackageRegex().Match(packageName);
            if (!match.Success)
            {
                continue;
            }

            var family = match.Groups["family"].Value;
            var classCode = match.Groups["class"].Value;
            var slot = match.Groups["slot"].Value;
            var archetype = archetypes.TryGetValue(classCode, out var label)
                ? label
                : KnownBaseArchetypes.TryGetValue(classCode, out var known)
                    ? known
                    : FormatArchetype(classCode[3..].Replace('_', ' '));
            var setName = RemoveItemSlot(association.DisplayName);
            var displayName = string.IsNullOrWhiteSpace(setName)
                ? $"{association.DisplayName} ({archetype})"
                : $"{setName} ({archetype})";
            var packageFamily = VariantSuffixRegex().Replace(family, string.Empty);
            var objectName = $"{family}_{slot}";
            expanded.Add(association with
            {
                DisplayName = displayName,
                Field = $"Model.{TitleCase(slot)}",
                AssetReference = $"{packageFamily}.{objectName}",
                PackageName = packageFamily,
                ObjectName = objectName,
                Confidence = GameAssetAssociationConfidence.High,
                Relation = GameAssetAssociationRelation.DeterministicItemAsset,
                SourceAssetReference = association.AssetReference,
                Archetype = archetype,
            });
        }

        return expanded;
    }

    private static string RemoveItemSlot(string displayName)
    {
        var value = AvailabilitySuffixRegex().Replace(displayName.Trim(), string.Empty);
        value = SlotLabelRegex().Replace(value, string.Empty);
        return value.Trim();
    }

    private static string FormatArchetype(string value)
    {
        var cleaned = value.Replace('_', ' ').Trim();
        var match = ParenthesizedGenderRegex().Match(cleaned);
        if (match.Success)
        {
            return $"{TitleCase(match.Groups["gender"].Value)} {match.Groups["name"].Value.Trim()}";
        }

        var suffix = GenderSuffixRegex().Match(cleaned);
        return suffix.Success
            ? $"{TitleCase(suffix.Groups["gender"].Value)} {suffix.Groups["name"].Value.Trim()}"
            : cleaned;
    }

    private static string TitleCase(string value) =>
        string.Concat(
            char.ToUpperInvariant(value[0]),
            value.AsSpan(1).ToString().ToLowerInvariant());

    [GeneratedRegex(
        @"^EFDLITEM_(?<family>(?<class>PC_.+?)_AV_[A-Z0-9-]+)_(?<slot>HEAD|UPPER|LOWER|DRESS|FACE1|FACE2)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AvatarItemPackageRegex();

    [GeneratedRegex(
        @"\s+\((?:Bound|Event|Untradable|Tradable|Selection|Bonus)\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AvailabilitySuffixRegex();

    [GeneratedRegex(
        @"\s+(?:Headwear|Chestpiece(?:\s*&\s*Pants)?|Pants|Facewear\s*[12])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlotLabelRegex();

    [GeneratedRegex(
        @"^(?<name>.+?)\s*\((?<gender>Female|Male)\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParenthesizedGenderRegex();

    [GeneratedRegex(
        @"^(?<name>.+?)\s+(?<gender>Female|Male)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenderSuffixRegex();

    [GeneratedRegex(@"-\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VariantSuffixRegex();

    [GeneratedRegex(
        @"(?<class>PC_[A-Z]+(?:_[A-Z]+)?)_AV_",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AvatarClassReferenceRegex();

    [GeneratedRegex(
        @"\s+\([^)]+\)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TrailingArchetypeRegex();
}
