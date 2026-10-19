using System.Collections.Concurrent;
using System.Security.Cryptography;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Materials;

namespace Arkive.Core.Discovery;

public sealed partial class PackageIndexStore
{
    /// <summary>
    /// The indexed assets the zones' maps use (see <see cref="ZoneMapSidecar.FindAssetsAsync"/>), each shown with its
    /// zone's name ("Land of Death and Order (map)") and what links it to the map. Reads the zones' level packages, so
    /// it takes a moment per zone.
    /// </summary>
    public async Task<PackageIndexSearchResult> SearchZonesAsync(
        string rootPath,
        IReadOnlyList<GameZone> zones,
        GameAssetMetadataIndex metadata,
        IChunkProtectionProvider protectionProvider,
        string? category = null,
        int resultLimit = 5_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resultLimit);
        if (zones.Count == 0) return new PackageIndexSearchResult([], 0, 0, 0);
        var files = await PackageFilesByAliasAsync(rootPath, metadata, cancellationToken).ConfigureAwait(false);
        if (files is null) return new PackageIndexSearchResult([], 0, 0, 0);
        var found = await ZoneMapSidecar.FindAssetsAsync(zones, files,
            async (packages, token) =>
            {
                var documents = await LoadPackagesByAliasAsync(rootPath, packages, metadata, cancellationToken: token)
                    .ConfigureAwait(false);
                return documents.GroupBy(document => document.PackageAlias, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group
                        .SelectMany(document => document.Assets)
                        .Where(asset => AssetTypes.IndexedKind(asset.Kind, asset.ClassName) is AssetKind.Texture or AssetKind.Material &&
                                        AssetTypes.Editable(AssetTypes.IndexedKind(asset.Kind, asset.ClassName), asset.ClassName) is not null)
                        .Select(asset => asset.ObjectName)
                        .ToArray(), StringComparer.OrdinalIgnoreCase);
            }, protectionProvider, cancellationToken).ConfigureAwait(false);

        var wanted = new Dictionary<string, List<GameAssetAssociation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (zone, assets) in found)
            foreach (var asset in assets)
            {
                var key = asset.PackageName + "." + asset.ObjectName;
                if (!wanted.TryGetValue(key, out var list)) wanted[key] = list = [];
                list.Add(ZoneMapSidecar.Association(zone, asset));
            }

        var documents = await LoadPackagesByAliasAsync(rootPath,
            wanted.Keys.Select(key => key[..key.IndexOf('.')]).Distinct(StringComparer.OrdinalIgnoreCase), metadata,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var matches = new List<PackageIndexMatch>();
        var total = 0;
        foreach (var document in documents)
            foreach (var asset in document.Assets)
            {
                if (!wanted.TryGetValue(document.PackageAlias + "." + asset.ObjectName, out var zoneNames)) continue;
                // A name can also belong to another kind of object in the package (a redirector, a component).
                if (AssetTypes.Editable(AssetTypes.IndexedKind(asset.Kind, asset.ClassName), asset.ClassName) is null) continue;
                if (!string.IsNullOrEmpty(category) && !MatchesCategory(asset, category)) continue;
                total++;
                if (matches.Count >= resultLimit) continue;
                matches.Add(new PackageIndexMatch(document.SourcePath, document.PackageAlias, document.PhysicalPackageName,
                    document.Assets.Count, asset.ExportIndex, AssetTypes.IndexedKind(asset.Kind, asset.ClassName), asset.ClassName,
                    asset.ObjectName, asset.SerialSize, asset.SerialOffset,
                    zoneNames.Concat(metadata.Find(document.PackageAlias, document.PhysicalPackageName, asset.ObjectName)).ToArray()));
            }
        matches.Sort((left, right) =>
        {
            var confidence = Certain(right).CompareTo(Certain(left));
            if (confidence != 0) return confidence;
            var package = StringComparer.OrdinalIgnoreCase.Compare(left.PackageAlias, right.PackageAlias);
            return package != 0 ? package : StringComparer.OrdinalIgnoreCase.Compare(left.ObjectName, right.ObjectName);
        });
        return new PackageIndexSearchResult(matches, total, documents.Count, 0);

        static bool Certain(PackageIndexMatch match) => match.GameMetadata.Any(item =>
            item.Relation == ZoneMapSidecar.Relation && item.Confidence == GameAssetAssociationConfidence.Verified);
    }

    /// <summary>
    /// Every part of a map Arkive can edit, sorted into <see cref="ZonePartGroup"/>s: what its level packages hold and
    /// what they import (meshes, materials, effects and the textures the levels stream). The landscape's materials
    /// are read to find the layer materials and textures the ground draws with. Takes a few seconds.
    /// </summary>
    public async Task<ZoneMapReport> ReadZoneMapAsync(
        string rootPath,
        ZoneMap map,
        GameAssetMetadataIndex metadata,
        IChunkProtectionProvider protectionProvider,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = await PackageFilesByAliasAsync(rootPath, metadata, cancellationToken).ConfigureAwait(false) ??
                    throw new InvalidOperationException("The Asset Library's index is not ready yet.");
        progress?.Report("Reading the map's level packages");
        var reads = new ConcurrentDictionary<string, ZoneMapSidecar.LevelRead>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(map.LevelPackages.Where(files.ContainsKey),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8), CancellationToken = cancellationToken },
            (alias, _) =>
            {
                try
                {
                    reads[alias] = ZoneMapSidecar.ReadLevel(files[alias], alias, protectionProvider);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or
                                                      UnauthorizedAccessException)
                {
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        // Each referenced object once, with the level packages that use it.
        var references = new Dictionary<string, (ZoneMapSidecar.LevelReference Reference, SortedSet<string> Levels)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, read) in reads)
            foreach (var reference in read.References)
            {
                var key = reference.PackageName + "." + reference.ObjectName;
                if (!references.TryGetValue(key, out var entry))
                    references[key] = entry = (reference, new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
                entry.Levels.Add(alias);
            }

        progress?.Report("Finding the map's assets in the Asset Library");
        var documents = await LoadPackagesByAliasAsync(rootPath,
            references.Values.Select(entry => entry.Reference.PackageName).Distinct(StringComparer.OrdinalIgnoreCase), metadata,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var indexed = new Dictionary<string, (PackageIndexDocument Document, IndexedAsset Asset)>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
            foreach (var asset in document.Assets)
                if (AssetTypes.Editable(AssetTypes.IndexedKind(asset.Kind, asset.ClassName), asset.ClassName) is not null)
                    indexed.TryAdd(document.PackageAlias + "." + asset.ObjectName, (document, asset));

        progress?.Report("Reading the landscape's materials");
        var (terrain, generated) = await ReadLandscapeAsync(reads, files, references, indexed, protectionProvider, cancellationToken)
            .ConfigureAwait(false);

        var placed = references.Values.Where(entry => entry.Reference.Imported).Select(entry => entry.Reference).ToArray();
        var meshPackages = Packages(placed, "staticmesh");
        var characterPackages = Packages(placed, "skeletalmesh").Except(meshPackages, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var effectPackages = Packages(placed, "particlesystem").Except(meshPackages, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parts = new List<ZoneMapPart>();
        var notIndexed = 0;
        foreach (var (key, (reference, levels)) in references)
        {
            if (!indexed.TryGetValue(key, out var found))
            {
                notIndexed++;
                continue;
            }
            var (document, asset) = found;
            var kind = AssetTypes.IndexedKind(asset.Kind, asset.ClassName);
            parts.Add(new ZoneMapPart(
                new PackageIndexMatch(document.SourcePath, document.PackageAlias, document.PhysicalPackageName,
                    document.Assets.Count, asset.ExportIndex, kind, asset.ClassName, asset.ObjectName, asset.SerialSize,
                    asset.SerialOffset, metadata.Find(document.PackageAlias, document.PhysicalPackageName, asset.ObjectName)),
                ZoneParts.Classify(document.PackageAlias, asset.ObjectName, asset.ClassName, terrain, generated,
                    characterPackages, effectPackages),
                levels.ToArray(), !reference.Imported));
        }
        parts.Sort((left, right) =>
        {
            var group = left.Group.CompareTo(right.Group);
            return group != 0 ? group : StringComparer.OrdinalIgnoreCase.Compare(left.Asset.ObjectName, right.Asset.ObjectName);
        });
        return new ZoneMapReport(map, parts, ZoneLevelSettings.Count(reads.Values.Select(read => read.ClassCounts)),
            map.LevelPackages.Count(alias => !reads.ContainsKey(alias)), notIndexed);

        static HashSet<string> Packages(IEnumerable<ZoneMapSidecar.LevelReference> items, string className) =>
            items.Where(item => item.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.PackageName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The landscape's layer materials and their textures (Terrain), and its blend masks (generated): read from each
    /// landscape material's parent and texture parameters, and each layer material's textures.
    /// </summary>
    private static async Task<(IReadOnlySet<string> Terrain, IReadOnlySet<string> Generated)> ReadLandscapeAsync(
        IReadOnlyDictionary<string, ZoneMapSidecar.LevelRead> reads,
        IReadOnlyDictionary<string, string> files,
        Dictionary<string, (ZoneMapSidecar.LevelReference Reference, SortedSet<string> Levels)> references,
        Dictionary<string, (PackageIndexDocument Document, IndexedAsset Asset)> indexed,
        IChunkProtectionProvider protectionProvider,
        CancellationToken cancellationToken)
    {
        var terrain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (reads.Values.All(read => read.Landscapes.Count == 0)) return (terrain, generated);
        var service = new MaterialInstanceReconnaissanceService(protectionProvider: protectionProvider);
        var hashes = new ConcurrentDictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);
        // Imported objects by level package and object name: a material names its parent and textures by name only.
        var byName = references.Values.Where(entry => entry.Reference.Imported)
            .SelectMany(entry => entry.Levels.Select(level => (Level: level, entry.Reference)))
            .ToLookup(item => item.Level + "|" + item.Reference.ObjectName, item => item.Reference, StringComparer.OrdinalIgnoreCase);
        var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, read) in reads)
            foreach (var landscape in read.Landscapes)
            {
                var report = await InspectAsync(files[alias], alias, landscape.ExportIndex, landscape.ObjectName,
                    landscape.SerialOffset, landscape.SerialSize).ConfigureAwait(false);
                if (report is null) continue;
                foreach (var texture in report.TextureParameters)
                    if (ZoneParts.ReferencedName(texture.ResolvedObject) is { } name)
                        generated.Add(alias + "." + name);
                if (ZoneParts.ReferencedName(report.ParentObject) is not { } parent) continue;
                foreach (var layer in byName[alias + "|" + parent])
                    layers.Add(layer.PackageName + "." + layer.ObjectName);
            }
        foreach (var layer in layers)
        {
            terrain.Add(layer);
            if (!indexed.TryGetValue(layer, out var found)) continue;
            var (document, asset) = found;
            var report = await InspectAsync(document.SourcePath, document.PackageAlias, asset.ExportIndex, asset.ObjectName,
                asset.SerialOffset, asset.SerialSize).ConfigureAwait(false);
            if (report is null) continue;
            IEnumerable<string> levels = references.TryGetValue(layer, out var entry) ? entry.Levels : [];
            foreach (var texture in report.TextureParameters)
                if (ZoneParts.ReferencedName(texture.ResolvedObject) is { } name)
                {
                    // A layer's own textures sit beside it; others are imported by the levels that draw the layer.
                    if (indexed.ContainsKey(document.PackageAlias + "." + name)) terrain.Add(document.PackageAlias + "." + name);
                    foreach (var level in levels)
                        foreach (var other in byName[level + "|" + name])
                            terrain.Add(other.PackageName + "." + other.ObjectName);
                }
        }
        return (terrain, generated);

        async Task<MaterialInstanceReconnaissanceReport?> InspectAsync(string packagePath, string alias, int exportIndex,
            string objectName, long serialOffset, long serialSize)
        {
            try
            {
                // The report carries the package's hash; one hash per package keeps this from reading it per material.
                var hash = await hashes.GetOrAdd(packagePath, path => Task.Run(() =>
                {
                    using var stream = File.OpenRead(path);
                    return Convert.ToHexString(SHA256.HashData(stream));
                }, cancellationToken)).ConfigureAwait(false);
                return await service.InspectAsync(new MaterialInstanceReconnaissanceRequest(packagePath, alias, exportIndex,
                    AssetKind.Material, "MaterialInstanceConstant", objectName, serialOffset, serialSize, hash),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or
                                                  UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
