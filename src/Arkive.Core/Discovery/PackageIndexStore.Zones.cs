using Arkive.Core.Archives;
using Arkive.Core.Assets;

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
}
