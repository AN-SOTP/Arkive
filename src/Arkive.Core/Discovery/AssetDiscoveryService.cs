using Arkive.Core.Assets;

namespace Arkive.Core.Discovery;

public enum AssetDiscoverySuggestionKind
{
    CharacterOrNpc,
    EquipmentOrItem,
    DatabaseEntity,
}

public sealed record AssetDiscoverySuggestion(
    AssetDiscoverySuggestionKind Kind,
    string DisplayName,
    string SearchText,
    string? Archetype,
    string EntityType,
    int LinkedReferenceCount,
    string Hint)
{
    public string QualifiedDisplayName => string.IsNullOrWhiteSpace(Archetype)
        ? DisplayName
        : $"{DisplayName} ({Archetype})";
}

public sealed record RankedPackageIndexMatch(
    PackageIndexMatch Match,
    int Relevance,
    string MatchReason);

public sealed record AssetDiscoveryFacet(
    string Group,
    string Value,
    int Count);

/// <summary>
/// Turns Arkive's exact package catalogue and database associations into a
/// user-facing discovery layer. It never invents asset relationships: ranking
/// changes presentation only and every suggestion retains its database origin.
/// </summary>
public sealed class AssetDiscoveryService
{
    public IReadOnlyList<AssetDiscoverySuggestion> Suggest(
        GameAssetMetadataIndex metadata,
        string query,
        string? archetype = null,
        int limit = 80)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var normalizedQuery = query.Trim();
        var entitySuggestions = metadata.Entities
            .Where(entity => MatchesArchetype(metadata, entity.Associations, archetype))
            .Where(entity => AssetSearch.MatchesDisplayNames(
                normalizedQuery,
                [entity.DisplayName, entity.Category, entity.Detail]))
            .Select(entity => new AssetDiscoverySuggestion(
                AssetDiscoverySuggestionKind.CharacterOrNpc,
                entity.DisplayName,
                entity.DisplayName,
                ResolveArchetype(entity.Associations),
                entity.Kind == GameEntityKind.PlayableCharacter ? "Playable character" : "NPC",
                entity.Associations.Count,
                $"{entity.Category} · {entity.Associations.Count:N0} database link(s)"));

        var associationSuggestions = metadata.Associations
            .Where(association => string.IsNullOrWhiteSpace(archetype) ||
                                  metadata.MatchesArchetype(association.Archetype, archetype))
            .Where(association => AssetSearch.MatchesDisplayNames(
                normalizedQuery,
                [association.DisplayName]))
            .GroupBy(association => new SuggestionKey(
                association.DisplayName.Trim(),
                association.Archetype?.Trim() ?? string.Empty,
                association.EntityType), SuggestionKeyComparer.Instance)
            .Select(group =>
            {
                var type = IsCharacterEntity(group.Key.EntityType)
                    ? AssetDiscoverySuggestionKind.DatabaseEntity
                    : AssetDiscoverySuggestionKind.EquipmentOrItem;
                var authoritative = group.Count(item =>
                    item.Confidence.Equals(GameAssetAssociationConfidence.Authoritative,
                        StringComparison.OrdinalIgnoreCase) ||
                    item.Confidence.Equals(GameAssetAssociationConfidence.Verified,
                        StringComparison.OrdinalIgnoreCase));
                return new AssetDiscoverySuggestion(
                    type,
                    group.Key.Name,
                    group.Key.Name,
                    string.IsNullOrWhiteSpace(group.Key.Archetype) ? null : group.Key.Archetype,
                    group.Key.EntityType,
                    group.Count(),
                    $"{group.Key.EntityType} · {authoritative:N0} verified/direct of {group.Count():N0} link(s)");
            });

        return entitySuggestions
            .Concat(associationSuggestions)
            .GroupBy(item => new SuggestionKey(
                item.DisplayName,
                item.Archetype ?? string.Empty,
                item.EntityType), SuggestionKeyComparer.Instance)
            .Select(group => group
                .OrderByDescending(item => item.LinkedReferenceCount)
                .First())
            .OrderByDescending(item => SuggestionScore(normalizedQuery, item.DisplayName))
            .ThenByDescending(item => item.LinkedReferenceCount)
            .ThenBy(item => item.QualifiedDisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    public IReadOnlyList<RankedPackageIndexMatch> Rank(
        IEnumerable<PackageIndexMatch> matches,
        string query,
        PackageIndexSearchScope scope)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var terms = PositiveTerms(query);
        return matches.Select(match => RankOne(match, terms, scope))
            .OrderByDescending(item => item.Relevance)
            .ThenBy(item => item.Match.PackageAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Match.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Match.ExportIndex)
            .ToArray();
    }

    public IReadOnlyList<AssetDiscoveryFacet> BuildFacets(
        IEnumerable<PackageIndexMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var materialized = matches.ToArray();
        return materialized.GroupBy(item => item.Kind.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AssetDiscoveryFacet("Category", group.Key, group.Count()))
            .Concat(materialized
                .SelectMany(item => item.GameMetadata)
                .Select(item => item.Archetype)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .GroupBy(item => item, StringComparer.OrdinalIgnoreCase)
                .Select(group => new AssetDiscoveryFacet("Class type", group.Key, group.Count())))
            .Concat(materialized
                .Select(item => Evidence(item.GameMetadata))
                .GroupBy(item => item, StringComparer.OrdinalIgnoreCase)
                .Select(group => new AssetDiscoveryFacet("Link evidence", group.Key, group.Count())))
            .OrderBy(item => item.Group, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.Count)
            .ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static RankedPackageIndexMatch RankOne(
        PackageIndexMatch match,
        IReadOnlyList<string> terms,
        PackageIndexSearchScope scope)
    {
        var score = 0;
        var reasons = new List<string>();
        var names = match.GameMetadata.Select(item => item.DisplayName).Distinct(
            StringComparer.OrdinalIgnoreCase).ToArray();
        if (terms.Count == 0)
        {
            score = LinkWeight(match.GameMetadata);
            reasons.Add(match.GameMetadata.Count > 0 ? Evidence(match.GameMetadata) : "Filter match");
        }
        else
        {
            foreach (var term in terms)
            {
                var objectScore = FieldScore(match.ObjectName, term);
                var nameScore = names.Select(name => FieldScore(name, term)).DefaultIfEmpty(0).Max();
                if (scope == PackageIndexSearchScope.ObjectName) nameScore = 0;
                if (scope == PackageIndexSearchScope.InGameName) objectScore = 0;
                var best = Math.Max(objectScore, nameScore);
                score += best;
                if (best == nameScore && nameScore > 0) reasons.Add("in-game name");
                else if (objectScore > 0) reasons.Add("object name");
            }
            score += LinkWeight(match.GameMetadata);
        }

        score += AssetTypes.For(match.Kind)?.DiscoveryScore ?? 0;
        var reason = reasons.Count == 0
            ? Evidence(match.GameMetadata)
            : string.Join(" + ", reasons.Distinct(StringComparer.OrdinalIgnoreCase));
        if (match.GameMetadata.Count > 0) reason += $" · {Evidence(match.GameMetadata)}";
        return new RankedPackageIndexMatch(match, score, reason);
    }

    private static int FieldScore(string value, string term)
    {
        if (value.Equals(term, StringComparison.OrdinalIgnoreCase)) return 320;
        if (value.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 240;
        if (AssetSearch.ContainsAtTokenStart(value, term)) return 180;
        if (value.Contains(term, StringComparison.OrdinalIgnoreCase)) return 100;
        return 0;
    }

    private static int SuggestionScore(string query, string value)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        if (value.Equals(query, StringComparison.OrdinalIgnoreCase)) return 4;
        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (AssetSearch.ContainsAtTokenStart(value, query)) return 2;
        return 1;
    }

    private static int LinkWeight(IReadOnlyList<GameAssetAssociation> metadata)
    {
        if (metadata.Any(item => item.Relation.Equals(
                GameAssetAssociationRelation.VerifiedSerializedDependency,
                StringComparison.OrdinalIgnoreCase))) return 45;
        if (metadata.Any(item => item.Confidence.Equals(
                GameAssetAssociationConfidence.Verified,
                StringComparison.OrdinalIgnoreCase))) return 40;
        if (metadata.Any(item => item.Confidence.Equals(
                GameAssetAssociationConfidence.Authoritative,
                StringComparison.OrdinalIgnoreCase))) return 35;
        if (metadata.Count > 0) return 20;
        return 0;
    }

    private static string Evidence(IReadOnlyList<GameAssetAssociation> metadata)
    {
        if (metadata.Any(item => item.Relation.Equals(
                GameAssetAssociationRelation.VerifiedSerializedDependency,
                StringComparison.OrdinalIgnoreCase))) return "verified dependency";
        if (metadata.Any(item => item.Relation.Equals(
                GameAssetAssociationRelation.DeterministicItemAsset,
                StringComparison.OrdinalIgnoreCase))) return "item mapping";
        if (metadata.Any(item => item.Relation.Equals(
                GameAssetAssociationRelation.DeterministicModelAsset,
                StringComparison.OrdinalIgnoreCase))) return "model mapping";
        return metadata.Count > 0 ? "direct database link" : "technical match";
    }

    private static bool MatchesArchetype(
        GameAssetMetadataIndex metadata,
        IReadOnlyList<GameAssetAssociation> associations,
        string? archetype) => string.IsNullOrWhiteSpace(archetype) ||
                             associations.Any(item => metadata.MatchesArchetype(item.Archetype, archetype));

    private static string? ResolveArchetype(IReadOnlyList<GameAssetAssociation> associations) =>
        associations.Select(item => item.Archetype)
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));

    private static bool IsCharacterEntity(string entityType) =>
        entityType.Equals("PC", StringComparison.OrdinalIgnoreCase) ||
        entityType.Equals("Npc", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> PositiveTerms(string query) => query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => item.Length == 1 || item[0] != '-')
        .ToArray();

    private sealed record SuggestionKey(string Name, string Archetype, string EntityType);

    private sealed class SuggestionKeyComparer : IEqualityComparer<SuggestionKey>
    {
        public static SuggestionKeyComparer Instance { get; } = new();

        public bool Equals(SuggestionKey? x, SuggestionKey? y) =>
            x is not null && y is not null &&
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Archetype, y.Archetype, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.EntityType, y.EntityType, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(SuggestionKey obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Archetype),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.EntityType));
    }
}
