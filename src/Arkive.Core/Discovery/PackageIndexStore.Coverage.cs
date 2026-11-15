using System.Collections.Concurrent;
using System.Text.Json;

namespace Arkive.Core.Discovery;

/// <summary>One kind of game entry (items, NPCs...) in a search coverage audit.</summary>
/// <param name="Kind">"Model" for entries the tables give a model or look, "Icon" for entries with an icon only.</param>
/// <param name="Reachable">How many of them an in-game-name search leads to at least one indexed asset for.</param>
/// <param name="Names">Their distinct in-game names, and how many of those a search finds something for.</param>
public sealed record SearchCoverageGroup(string EntityType, string Kind, int Entities, int Reachable, int Names, int ReachableNames);

/// <summary>A game entry whose in-game name finds nothing, with the references the tables give it.</summary>
public sealed record SearchCoverageMiss(string EntityType, long EntityId, string DisplayName, string Kind, IReadOnlyList<string> References);

public sealed record SearchCoverageReport(
    int Associations,
    int PackagesRead,
    IReadOnlyList<SearchCoverageGroup> Groups,
    IReadOnlyList<SearchCoverageMiss> Misses);

public sealed partial class PackageIndexStore
{
    private static readonly HashSet<string> IconFields = new(StringComparer.OrdinalIgnoreCase)
        { "Icon", "MapIcon", "IconName", "IconPath", "ResourcePath" };

    /// <summary>
    /// Checks which named game entries (items, NPCs, props...) an in-game-name search can find: an entry counts as found
    /// when one of its references leads to an asset in the index, by the same rules the search uses. Read-only, apart
    /// from the search routing index, which is built here if it is missing as a search would build it.
    /// </summary>
    public async Task<SearchCoverageReport> AuditSearchCoverageAsync(
        string rootPath,
        GameAssetMetadataIndex metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var routing = await PrepareSearchRoutingAsync(rootPath, metadata, null, cancellationToken).ConfigureAwait(false);
        var associations = metadata.AllReferences().ToArray();
        var packageNames = associations.Where(item => !string.IsNullOrWhiteSpace(item.PackageName))
            .SelectMany(item => GameAssetMetadataIndex.PackageNameVariants(item.PackageName))
            .ToHashSet(StringComparer.Ordinal);
        var files = routing.Index.ResolvePackages(packageNames, _cacheDirectory);
        // Each package's object names, sorted, under every name the search knows the package by.
        var packages = new ConcurrentDictionary<string, ConcurrentBag<string[]>>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = RecommendedSearchParallelism, CancellationToken = cancellationToken },
            async (file, token) =>
            {
                try
                {
                    await using var stream = OpenCacheRead(file);
                    var document = await JsonSerializer.DeserializeAsync<PackageIndexDocument>(stream, JsonOptions, token).ConfigureAwait(false);
                    if (document is null || document.Error is not null) return;
                    var names = document.Assets.Select(asset => GameAssetMetadataIndex.NormalizeObjectName(asset.ObjectName)).ToArray();
                    Array.Sort(names, StringComparer.Ordinal);
                    foreach (var name in GameAssetMetadataIndex.PackageNameVariants(document.PackageAlias)
                                 .Concat(GameAssetMetadataIndex.PackageNameVariants(document.PhysicalPackageName))
                                 .Distinct(StringComparer.Ordinal))
                        packages.GetOrAdd(name, _ => []).Add(names);
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
                {
                    // An unreadable document finds nothing, in a search as here.
                }
            }).ConfigureAwait(false);

        var reached = new Dictionary<(string Package, string Object), bool>();
        bool Reaches(GameAssetAssociation association)
        {
            var objectName = GameAssetMetadataIndex.NormalizeObjectName(association.ObjectName);
            if (objectName.Length == 0) return false;
            if (string.IsNullOrWhiteSpace(association.PackageName))
                return routing.Index.Document.ObjectRoutes.TryGetValue(objectName, out var routes) && routes.Length > 0;
            var package = GameAssetMetadataIndex.NormalizePackageName(association.PackageName);
            if (reached.TryGetValue((package, objectName), out var known)) return known;
            var found = GameAssetMetadataIndex.PackageNameVariants(association.PackageName).Any(name =>
                packages.TryGetValue(name, out var documents) && documents.Any(names => HasFamilyMember(names, objectName)));
            reached[(package, objectName)] = found;
            return found;
        }

        var groups = new Dictionary<(string Type, string Kind), (int Entities, int Reachable, Dictionary<string, bool> Names)>();
        var misses = new List<SearchCoverageMiss>();
        foreach (var entity in associations.GroupBy(item => (Type: item.EntityType, item.EntityId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var models = entity.Where(item => !IconFields.Contains(item.Field)).ToArray();
            var kind = models.Length > 0 ? "Model" : "Icon";
            var references = models.Length > 0 ? models : entity.ToArray();
            var found = references.Any(Reaches);
            var key = (entity.Key.Type, kind);
            if (!groups.TryGetValue(key, out var group)) group = (0, 0, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
            foreach (var name in entity.Select(item => item.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase))
                group.Names[name] = found || group.Names.GetValueOrDefault(name);
            groups[key] = (group.Entities + 1, group.Reachable + (found ? 1 : 0), group.Names);
            if (!found)
                misses.Add(new SearchCoverageMiss(entity.Key.Type, entity.Key.EntityId, entity.First().DisplayName, kind,
                    references.Select(item => $"{item.Field}={item.AssetReference}").Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }

        return new SearchCoverageReport(
            associations.Length,
            files.Count,
            groups.OrderBy(pair => pair.Key.Type, StringComparer.Ordinal).ThenBy(pair => pair.Key.Kind, StringComparer.Ordinal)
                .Select(pair => new SearchCoverageGroup(pair.Key.Type, pair.Key.Kind, pair.Value.Entities, pair.Value.Reachable,
                    pair.Value.Names.Count, pair.Value.Names.Values.Count(value => value)))
                .ToArray(),
            misses.OrderBy(miss => miss.EntityType, StringComparer.Ordinal).ThenBy(miss => miss.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(miss => miss.EntityId).ToArray());
    }

    // Whether the sorted names hold the reference itself or a name that continues it with _ or -.
    private static bool HasFamilyMember(string[] sortedNames, string reference)
    {
        var index = Array.BinarySearch(sortedNames, reference, StringComparer.Ordinal);
        if (index >= 0) return true;
        for (index = ~index; index < sortedNames.Length && sortedNames[index].StartsWith(reference, StringComparison.Ordinal); index++)
            if (GameAssetMetadataIndex.IsAssetFamilyMember(sortedNames[index], reference)) return true;
        return false;
    }
}
