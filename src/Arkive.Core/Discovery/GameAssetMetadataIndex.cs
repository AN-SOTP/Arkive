using System.Text.Json;

namespace Arkive.Core.Discovery;

public sealed record GameAssetAssociation(
    string DisplayName,
    string NameKey,
    string EntityType,
    long EntityId,
    string Field,
    string AssetReference,
    string PackageName,
    string ObjectName)
{
    public string Confidence { get; init; } = GameAssetAssociationConfidence.Authoritative;

    public string Relation { get; init; } = GameAssetAssociationRelation.DirectDatabaseReference;

    public string? SourceAssetReference { get; init; }

    /// <summary>
    /// Base playable archetype that owns this relationship, such as
    /// "Female Warrior". Null means the relationship is not class-scoped.
    /// </summary>
    public string? Archetype { get; init; }
}

public static class GameAssetAssociationConfidence
{
    public const string Authoritative = "Authoritative";
    public const string Verified = "Verified";
    public const string High = "High";
    public const string Inferred = "Inferred";
}

public static class GameAssetAssociationRelation
{
    public const string DirectDatabaseReference = "DirectDatabaseReference";
    public const string DeterministicItemAsset = "DeterministicItemAsset";
    public const string DeterministicModelAsset = "DeterministicModelAsset";
    public const string VerifiedSerializedDependency = "VerifiedSerializedDependency";
    public const string NamingFamilyInference = "NamingFamilyInference";
}

public sealed record GameAssetMetadataDocument(
    int Version,
    string Language,
    string SourceRoot,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<GameAssetAssociation> Associations,
    IReadOnlyList<GameEntityDefinition>? Entities = null,
    IReadOnlyList<CharacterCustomizationDefinition>? Customizations = null);

/// <summary>
/// Resolves high-confidence database references to the matching UModel package
/// and object names using the generated game database mapping.
/// </summary>
public sealed class GameAssetMetadataIndex
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, IReadOnlyList<GameAssetAssociation>> _fullReferences;
    private readonly Dictionary<string, IReadOnlyList<GameAssetAssociation>> _packageReferences;
    private readonly Dictionary<string, IReadOnlyList<GameAssetAssociation>> _objectReferences;
    private readonly Dictionary<string, IReadOnlyList<GameAssetAssociation>> _entityReferences;
    private readonly IReadOnlyList<GameAssetAssociation> _associations;
    private readonly IReadOnlyList<GameEntityRecord> _entities;
    private readonly IReadOnlyDictionary<long, IReadOnlyList<CharacterCustomizationDefinition>>
        _customizations;

    private GameAssetMetadataIndex(
        string sourcePath,
        GameAssetMetadataDocument document,
        Dictionary<string, IReadOnlyList<GameAssetAssociation>> fullReferences,
        Dictionary<string, IReadOnlyList<GameAssetAssociation>> packageReferences,
        Dictionary<string, IReadOnlyList<GameAssetAssociation>> objectReferences,
        Dictionary<string, IReadOnlyList<GameAssetAssociation>> entityReferences,
        IReadOnlyList<GameEntityRecord> entities,
        IReadOnlyDictionary<long, IReadOnlyList<CharacterCustomizationDefinition>> customizations)
    {
        SourcePath = sourcePath;
        var source = new FileInfo(sourcePath);
        DependencySidecarPath = GameAssetDependencySidecar.GetDefaultPath(sourcePath);
        var dependencySource = File.Exists(DependencySidecarPath)
            ? new FileInfo(DependencySidecarPath)
            : null;
        SourceLength = source.Length + (dependencySource?.Length ?? 0);
        SourceLastWriteUtcTicks = Math.Max(
            source.LastWriteTimeUtc.Ticks,
            dependencySource?.LastWriteTimeUtc.Ticks ?? 0);
        Language = document.Language;
        SourceDatabaseRoot = document.SourceRoot;
        GeneratedAt = document.GeneratedAt;
        AssociationCount = document.Associations.Count;
        VerifiedDependencyCount = document.Associations.Count(item => item.Relation.Equals(
            GameAssetAssociationRelation.VerifiedSerializedDependency,
            StringComparison.OrdinalIgnoreCase));
        DeterministicItemCount = document.Associations.Count(item => item.Relation.Equals(
            GameAssetAssociationRelation.DeterministicItemAsset,
            StringComparison.OrdinalIgnoreCase));
        Archetypes = document.Associations
            .Where(item => item.Relation.Equals(
                               GameAssetAssociationRelation.DeterministicItemAsset,
                               StringComparison.OrdinalIgnoreCase) ||
                           item.Relation.Equals(
                               GameAssetAssociationRelation.VerifiedSerializedDependency,
                               StringComparison.OrdinalIgnoreCase) ||
                           item.NameKey.StartsWith(
                               "arkive.verified.",
                               StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Archetype)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _fullReferences = fullReferences;
        _packageReferences = packageReferences;
        _objectReferences = objectReferences;
        _entityReferences = entityReferences;
        _associations = document.Associations;
        _entities = entities;
        _customizations = customizations;
    }

    public string SourcePath { get; }

    public long SourceLength { get; }

    public long SourceLastWriteUtcTicks { get; }

    public string DependencySidecarPath { get; }

    public string Language { get; }

    public string SourceDatabaseRoot { get; }

    public DateTimeOffset GeneratedAt { get; }

    public int AssociationCount { get; }

    public int VerifiedDependencyCount { get; }

    public int DeterministicItemCount { get; }

    public IReadOnlyList<string> Archetypes { get; }

    /// <summary>
    /// All high-confidence database associations. Consumers must preserve the
    /// entity type when distinguishing playable PC rows from NPC lookalikes.
    /// </summary>
    public IReadOnlyList<GameAssetAssociation> Associations => _associations;

    public IReadOnlyList<GameEntityRecord> Entities => _entities;

    public int EntityCount => _entities.Count;

    public int CustomizationOptionCount => _customizations.Values.Sum(items => items.Count);

    internal IReadOnlyCollection<string> ObjectOnlyReferenceNames => _objectReferences.Keys;

    public static GameAssetMetadataIndex Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        using var stream = File.OpenRead(fullPath);
        var document = JsonSerializer.Deserialize<GameAssetMetadataDocument>(stream, JsonOptions) ??
                       throw new InvalidDataException("The game asset metadata document is empty.");
        if (document.Version is not (1 or 2))
        {
            throw new InvalidDataException(
                $"Unsupported game asset metadata version {document.Version}; expected version 1 or 2.");
        }

        var annotatedAssociations = AvatarItemAssociationResolver.AttachArchetypes(
            document.Associations);
        var associations = annotatedAssociations
            .Concat(WesternGlobalCustomizationMappings.CreateAssociations())
            .Concat(AvatarItemAssociationResolver.Expand(annotatedAssociations))
            .Concat(EquipmentItemAssociationResolver.Expand(annotatedAssociations))
            .Concat(ModelPackageAssociationResolver.Expand(annotatedAssociations))
            .Concat(GameAssetDependencySidecar.LoadAssociations(fullPath))
            .Concat(Interface.InterfaceNameSidecar.LoadAssociations(fullPath))
            .Concat(Skills.SkillEffectNameSidecar.LoadAssociations(fullPath))
            .DistinctBy(item => new
            {
                Type = item.EntityType.ToUpperInvariant(),
                item.EntityId,
                item.Field,
                Asset = item.AssetReference.ToUpperInvariant(),
                item.DisplayName,
                item.Relation,
            })
            .ToArray();
        document = document with { Associations = associations };

        var full = new Dictionary<string, List<GameAssetAssociation>>(StringComparer.Ordinal);
        var packages = new Dictionary<string, List<GameAssetAssociation>>(StringComparer.Ordinal);
        var objects = new Dictionary<string, List<GameAssetAssociation>>(StringComparer.Ordinal);
        var entities = new Dictionary<string, List<GameAssetAssociation>>(StringComparer.Ordinal);
        foreach (var association in associations)
        {
            Add(entities, EntityKey(association.EntityType, association.EntityId), association);
            var objectName = NormalizeObjectName(association.ObjectName);
            if (string.IsNullOrEmpty(objectName))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(association.PackageName))
            {
                Add(objects, objectName, association);
            }
            else
            {
                foreach (var packageName in PackageNameVariants(association.PackageName))
                {
                    Add(full, FullKey(packageName, objectName), association);
                    Add(packages, packageName, association);
                }
            }
        }

        var definitions = document.Entities is { Count: > 0 }
            ? document.Entities
            : DeriveEntityDefinitions(associations);
        var entityRecords = definitions
            .Where(definition =>
                definition.EntityType.Equals("PC", StringComparison.OrdinalIgnoreCase) ||
                definition.EntityType.Equals("Npc", StringComparison.OrdinalIgnoreCase))
            .Select(definition => new GameEntityRecord(
                definition.DisplayName,
                definition.NameKey,
                definition.EntityType,
                definition.EntityId,
                definition.EntityType.Equals("PC", StringComparison.OrdinalIgnoreCase)
                    ? GameEntityKind.PlayableCharacter
                    : GameEntityKind.Npc,
                definition.Category,
                definition.Detail,
                definition.BaseEntityId,
                definition.PrimaryAssetReference,
                entities.TryGetValue(
                    EntityKey(definition.EntityType, definition.EntityId),
                    out var entityAssociations)
                    ? entityAssociations
                    : []))
            .OrderBy(entity => entity.Kind)
            .ThenBy(entity => entity.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entity => entity.EntityId)
            .ToArray();
        var customizations = (document.Customizations ?? [])
            .GroupBy(item => item.PlayerClassId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CharacterCustomizationDefinition>)group
                    .OrderBy(item => item.CategoryId)
                    .ThenBy(item => item.UiPosition)
                    .ToArray());

        return new GameAssetMetadataIndex(
            fullPath,
            document,
            full.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<GameAssetAssociation>)pair.Value),
            packages.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<GameAssetAssociation>)pair.Value),
            objects.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<GameAssetAssociation>)pair.Value),
            entities.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<GameAssetAssociation>)pair.Value),
            entityRecords,
            customizations);
    }

    public IReadOnlyList<CharacterCustomizationOption> FindCustomizationOptions(
        GameEntityRecord entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.Kind != GameEntityKind.PlayableCharacter)
        {
            return [];
        }

        var ownerId = entity.EntityId;
        if (!_customizations.TryGetValue(ownerId, out var definitions) &&
            entity.BaseEntityId is { } baseId)
        {
            ownerId = baseId;
            _customizations.TryGetValue(ownerId, out definitions);
        }

        if (definitions is null)
        {
            return [];
        }

        var playerCode = (ownerId == entity.EntityId
                ? FindEntityAssociations(entity.EntityType, entity.EntityId)
                : FindEntityAssociations(entity.EntityType, ownerId)
                    .Concat(FindEntityAssociations(entity.EntityType, entity.EntityId)))
            .Where(item => item.Field.Equals("LookInfo", StringComparison.OrdinalIgnoreCase))
            .Select(item => NormalizeObjectName(item.ObjectName).ToLowerInvariant())
            .FirstOrDefault(code => code.StartsWith("pc_", StringComparison.Ordinal));

        return definitions
            .Select(definition => CreateCustomizationOption(definition, playerCode, entity))
            .ToArray();
    }

    private static IReadOnlyList<GameEntityDefinition> DeriveEntityDefinitions(
        IReadOnlyList<GameAssetAssociation> associations) =>
        associations
            .Where(item =>
                item.EntityType.Equals("PC", StringComparison.OrdinalIgnoreCase) ||
                item.EntityType.Equals("Npc", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => new
            {
                Type = item.EntityType.ToUpperInvariant(),
                item.EntityId,
            })
            .Select(group =>
            {
                var first = group.First();
                var primary = group.FirstOrDefault(item =>
                    item.Field.Equals("LookInfo", StringComparison.OrdinalIgnoreCase) ||
                    item.Field.Equals("Model", StringComparison.OrdinalIgnoreCase));
                return new GameEntityDefinition(
                    first.DisplayName,
                    first.NameKey,
                    first.EntityType,
                    first.EntityId,
                    first.EntityType.Equals("PC", StringComparison.OrdinalIgnoreCase)
                        ? "Playable class"
                        : "NPC",
                    "Legacy metadata; rebuild the game DB mapping for richer entity details.",
                    null,
                    primary?.AssetReference);
            })
            .ToArray();

    private static CharacterCustomizationOption CreateCustomizationOption(
        CharacterCustomizationDefinition definition,
        string? playerCode,
        GameEntityRecord entity)
    {
        var candidates = new List<string>();
        var evidence = "No asset-bearing value is stored for this option.";
        if (entity.EntityId == WesternGlobalCustomizationMappings.SlayerEntityId &&
            definition.CategoryId == 1 &&
            WesternGlobalCustomizationMappings.TryGetSlayerFaceTextureNumber(
                definition.UiPosition, out var verifiedTextureNumber))
        {
            candidates.AddRange(
                WesternGlobalCustomizationMappings.CreateSlayerFaceCandidates(definition.UiPosition));
            evidence = verifiedTextureNumber is { } suffix
                ? $"Verified in the Western/global client with Arkive's numbered texture test: " +
                  $"Slayer face UI #{definition.UiPosition} uses pc_wr_f_{suffix:00}_face textures."
                : $"The Western/global numbered texture test found no pc_wr_f_NN_face diffuse " +
                  $"texture for Slayer face UI #{definition.UiPosition}; this face remains unresolved.";
        }
        else if (entity.EntityId == WesternGlobalCustomizationMappings.DragonHumanEntityId &&
                 definition.CategoryId == 1 &&
                 WesternGlobalCustomizationMappings.TryGetDragonHumanFaceTextureNumber(
                     definition.UiPosition, out var verifiedDragonHumanTextureNumber))
        {
            candidates.AddRange(
                WesternGlobalCustomizationMappings.CreateDragonHumanFaceCandidates(
                    definition.UiPosition));
            evidence = verifiedDragonHumanTextureNumber is { } suffix
                ? $"Verified in the Western/global client with Arkive's numbered texture test: " +
                  $"DragonHuman face UI #{definition.UiPosition} uses pc_dk_{suffix:00}_face textures."
                : $"The Western/global numbered texture test found no pc_dk_NN_face diffuse " +
                  $"texture for DragonHuman face UI #{definition.UiPosition}; this face remains unresolved.";
        }
        else if (definition.PlayerClassId ==
                     WesternGlobalCustomizationMappings.FemaleMartialArtistEntityId &&
                 definition.CategoryId == 1 &&
                 WesternGlobalCustomizationMappings.TryGetFemaleMartialArtistFaceTextureNumber(
                     definition.UiPosition, out var verifiedFemaleMartialArtistTextureNumber))
        {
            candidates.AddRange(
                WesternGlobalCustomizationMappings.CreateFemaleMartialArtistFaceCandidates(
                    definition.UiPosition));
            evidence = verifiedFemaleMartialArtistTextureNumber is { } suffix
                ? $"Verified in the Western/global client with Arkive's numbered texture test: " +
                  $"Female Martial Artist face UI #{definition.UiPosition} uses pc_ft_face_{suffix:00} textures."
                : $"The Western/global numbered texture test found no pc_ft_face_NN diffuse " +
                  $"texture for Female Martial Artist face UI #{definition.UiPosition}; this face remains unresolved.";
        }
        else if (!string.IsNullOrWhiteSpace(definition.ObjectReference) &&
            !definition.ObjectReference.Equals("Null", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(NormalizeObjectName(definition.ObjectReference).ToLowerInvariant());
            evidence = "Direct CharacterCustomizing.Object_String reference.";
        }
        else if (definition.CategoryId == 1 &&
                 definition.ObjectUnit is >= 2 and <= 24 &&
                 !string.IsNullOrWhiteSpace(playerCode))
        {
            var suffix = definition.ObjectUnit - 2;
            candidates.Add($"{playerCode}_{suffix:00}_face_d");
            candidates.Add($"{playerCode}_{suffix:00}_face_n");
            candidates.Add($"{playerCode}_{suffix:00}_face_s");
            candidates.Add($"{playerCode}_face_{suffix:00}_d");
            candidates.Add($"{playerCode}_face_{suffix:00}_n");
            candidates.Add($"{playerCode}_face_{suffix:00}_s");
            evidence =
                "Inferred legacy face mapping: customization unit minus two, with both known Lost Ark face-name layouts. Verify in game; newly inserted face units are excluded.";
        }

        return new CharacterCustomizationOption(
            definition.PlayerClassId,
            definition.CategoryId,
            CustomizationCategoryName(definition.CategoryId),
            definition.UiPosition,
            definition.ObjectUnit,
            definition.ObjectReference,
            definition.IconReference,
            definition.IconIndex,
            definition.MenuName,
            evidence,
            candidates);
    }

    private static string CustomizationCategoryName(int categoryId) => categoryId switch
    {
        0 => "Presets",
        1 => "Faces",
        4 => "Hair",
        9 => "Iris",
        10 => "Eye makeup",
        11 => "Cheek makeup",
        12 => "Lip makeup",
        13 or 14 or 15 or 16 => "Face decoration",
        17 => "Voice",
        _ => $"Customization {categoryId}",
    };

    /// <summary>
    /// Returns every asset-bearing database field recorded for the same game entity.
    /// This is the authoritative expansion used by the relationship explorer.
    /// </summary>
    public IReadOnlyList<GameAssetAssociation> FindEntityAssociations(
        string entityType,
        long entityId) =>
        _entityReferences.TryGetValue(EntityKey(entityType, entityId), out var matches)
            ? matches
            : [];

    public IReadOnlyList<GameAssetAssociation> ExpandEntities(
        IReadOnlyList<GameAssetAssociation> seeds)
    {
        if (seeds.Count == 0)
        {
            return [];
        }

        return seeds
            .SelectMany(seed => FindEntityAssociations(seed.EntityType, seed.EntityId))
            .DistinctBy(association => new
            {
                EntityType = association.EntityType.ToUpperInvariant(),
                association.EntityId,
                association.Field,
                association.AssetReference,
            })
            .OrderBy(association => association.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(association => association.EntityType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(association => association.EntityId)
            .ThenBy(association => association.Field, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<GameAssetAssociation> Find(
        string packageAlias,
        string physicalPackageName,
        string objectName)
    {
        var normalizedObject = NormalizeObjectName(objectName);
        if (string.IsNullOrEmpty(normalizedObject))
        {
            return [];
        }

        var matches = new List<GameAssetAssociation>();
        var alias = NormalizePackageName(packageAlias);
        AddPackageMatches(matches, alias, normalizedObject);
        var physical = NormalizePackageName(physicalPackageName);
        if (!physical.Equals(alias, StringComparison.Ordinal))
        {
            AddPackageMatches(matches, physical, normalizedObject);
        }

        AddMatches(matches, _objectReferences, normalizedObject);
        return matches.Count == 0
            ? []
            : matches
                .DistinctBy(association => new
                {
                    association.DisplayName,
                    association.EntityType,
                    association.EntityId,
                    association.Field,
                    association.AssetReference,
                })
                .ToArray();
    }

    public static string FormatNames(IReadOnlyList<GameAssetAssociation> associations, int limit = 4)
    {
        var names = associations
            .Select(association => association.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length <= limit)
        {
            return string.Join("; ", names);
        }

        return $"{string.Join("; ", names.Take(limit))} (+{names.Length - limit:N0})";
    }

    public static string BuildSearchText(IReadOnlyList<GameAssetAssociation> associations) =>
        string.Join(
            ' ',
            associations.SelectMany(association => new[]
            {
                association.DisplayName,
                association.NameKey,
                association.EntityType,
                association.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                association.Field,
                association.AssetReference,
            }));

    public SearchPlan CreateSearchPlan(
        string query,
        bool displayNamesOnly,
        string? archetype = null)
    {
        var terms = query
            .Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => new
            {
                Excluded = term.Length > 1 && term[0] == '-',
                Value = term.Length > 1 && term[0] == '-' ? term[1..] : term,
            })
            .Where(term => term.Value.Length > 0)
            .DistinctBy(term => (term.Excluded, term.Value.ToUpperInvariant()))
            .ToArray();
        if (terms.Length == 0 && string.IsNullOrWhiteSpace(archetype))
        {
            return SearchPlan.Empty;
        }

        var packages = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var objects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var association in _associations)
        {
            if (!string.IsNullOrWhiteSpace(archetype) &&
                !string.Equals(
                    association.Archetype,
                    archetype,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var searchableFields = displayNamesOnly
                ? new[] { association.DisplayName }
                : new[]
                {
                    association.DisplayName,
                    association.NameKey,
                    association.EntityType,
                    association.EntityId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    association.Field,
                    association.AssetReference,
                };
            if (!terms.All(term => term.Excluded != searchableFields.Any(field =>
                    !string.IsNullOrEmpty(field) &&
                    (displayNamesOnly
                        ? AssetSearch.ContainsAtTokenStart(field, term.Value)
                        : field.Contains(term.Value, StringComparison.OrdinalIgnoreCase)))))
            {
                continue;
            }

            var objectName = NormalizeObjectName(association.ObjectName);
            if (string.IsNullOrEmpty(objectName))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(association.PackageName))
            {
                objects.Add(objectName);
                continue;
            }

            foreach (var packageName in PackageNameVariants(association.PackageName))
            {
                if (!packages.TryGetValue(packageName, out var packageObjects))
                {
                    packageObjects = new HashSet<string>(StringComparer.Ordinal);
                    packages.Add(packageName, packageObjects);
                }

                packageObjects.Add(objectName);
            }
        }

        return new SearchPlan(
            packages.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlySet<string>)pair.Value,
                StringComparer.Ordinal),
            objects);
    }

    private static void Add(
        Dictionary<string, List<GameAssetAssociation>> index,
        string key,
        GameAssetAssociation association)
    {
        if (!index.TryGetValue(key, out var matches))
        {
            matches = [];
            index.Add(key, matches);
        }

        matches.Add(association);
    }

    private static void AddMatches(
        List<GameAssetAssociation> destination,
        Dictionary<string, IReadOnlyList<GameAssetAssociation>> index,
        string key)
    {
        if (index.TryGetValue(key, out var matches))
        {
            destination.AddRange(matches);
        }
    }

    private void AddPackageMatches(
        List<GameAssetAssociation> destination,
        string packageName,
        string objectName)
    {
        AddMatches(destination, _fullReferences, FullKey(packageName, objectName));
        if (!_packageReferences.TryGetValue(packageName, out var packageAssociations))
        {
            return;
        }

        destination.AddRange(packageAssociations.Where(association =>
            IsAssetFamilyMember(objectName, NormalizeObjectName(association.ObjectName))));
    }

    internal static bool IsAssetFamilyMember(string candidate, string reference) =>
        candidate.Equals(reference, StringComparison.Ordinal) ||
        (candidate.Length > reference.Length &&
         candidate.StartsWith(reference, StringComparison.Ordinal) &&
         candidate[reference.Length] is '_' or '-');

    private static string FullKey(string packageName, string objectName) =>
        packageName + "\0" + objectName;

    private static string EntityKey(string entityType, long entityId) =>
        entityType.Trim().ToUpperInvariant() + "\0" +
        entityId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static string NormalizePackageName(string value) =>
        Path.GetFileNameWithoutExtension(value.Trim()).ToUpperInvariant();

    internal static IEnumerable<string> PackageNameVariants(string value)
    {
        var normalized = NormalizePackageName(value);
        yield return normalized;
        if (!normalized.StartsWith("EFDL", StringComparison.Ordinal))
        {
            yield break;
        }

        var separator = normalized.IndexOf('_', 4);
        if (separator >= 0 && separator + 1 < normalized.Length)
        {
            yield return normalized[(separator + 1)..];
        }
    }

    internal static string NormalizeObjectName(string value)
    {
        var normalized = value.Trim().Trim('\'', '"');
        var dot = normalized.LastIndexOf('.');
        if (dot >= 0)
        {
            normalized = normalized[(dot + 1)..];
        }

        return normalized.ToUpperInvariant();
    }

    public sealed class SearchPlan
    {
        internal static readonly SearchPlan Empty = new(
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

        private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _packageObjects;
        private readonly IReadOnlySet<string> _objectOnly;

        internal SearchPlan(
            IReadOnlyDictionary<string, IReadOnlySet<string>> packageObjects,
            IReadOnlySet<string> objectOnly)
        {
            _packageObjects = packageObjects;
            _objectOnly = objectOnly;
        }

        internal IReadOnlyCollection<string> PackageNames => _packageObjects.Keys.ToArray();

        internal IReadOnlyCollection<string> ObjectOnlyReferences => _objectOnly;

        internal bool IsEmpty => _packageObjects.Count == 0 && _objectOnly.Count == 0;

        public bool MayContain(
            string packageAlias,
            string physicalPackageName,
            string objectName)
        {
            var normalizedObject = NormalizeObjectName(objectName);
            if (MatchesAny(_objectOnly, normalizedObject) ||
                MatchesPackage(packageAlias, normalizedObject))
            {
                return true;
            }

            var alias = NormalizePackageName(packageAlias);
            var physical = NormalizePackageName(physicalPackageName);
            return !physical.Equals(alias, StringComparison.Ordinal) &&
                   MatchesPackage(physical, normalizedObject);
        }

        private bool MatchesPackage(string packageName, string objectName)
        {
            var normalizedPackage = NormalizePackageName(packageName);
            return _packageObjects.TryGetValue(normalizedPackage, out var candidates) &&
                   MatchesAny(candidates, objectName);
        }

        private static bool MatchesAny(IReadOnlySet<string> candidates, string objectName) =>
            candidates.Any(reference => IsAssetFamilyMember(objectName, reference));
    }
}
