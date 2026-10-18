using System.Diagnostics;
using System.IO;
using Arkive.Core.Discovery;

namespace Arkive.App;

public partial class MainWindow
{
    // A query that names more zones than this ("the") is not a map search.
    private const int MaximumZonesPerSearch = 12;

    private ZoneMapDocument? _zoneMap;

    /// <summary>
    /// Reads the game's zones and the levels each loads when leveldata1.lpk changed since they were last read, then
    /// keeps them for map searches. Reading them takes about half a minute after a game update; until then a search
    /// finds no maps. Failures only reach the log.
    /// </summary>
    private async Task RefreshZoneNamesAsync(string metadataPath)
    {
        var root = _libraryRootPath;
        if (root is null || !Directory.Exists(root)) return;
        try
        {
            if (!await Task.Run(() => ZoneMapSidecar.IsCurrent(metadataPath, root)))
            {
                var watch = Stopwatch.StartNew();
                var built = await ZoneMapSidecar.BuildAsync(metadataPath, root);
                Log($"Zone names read: zones={built?.Zones.Count ?? 0}, seconds={watch.Elapsed.TotalSeconds:N1}, " +
                    $"path={ZoneMapSidecar.GetDefaultPath(metadataPath)}.");
            }
            _zoneMap = await Task.Run(() => ZoneMapSidecar.TryLoad(ZoneMapSidecar.GetDefaultPath(metadataPath)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                              System.Text.Json.JsonException)
        {
            Log($"Zone names were not read: {exception.Message}");
        }
    }

    /// <summary>
    /// Adds the assets of the maps whose zone name the query matches ("Land of Death and Order") to a search's results,
    /// before them. Object-name searches and queries naming many zones are left as they are.
    /// </summary>
    private async Task<PackageIndexSearchResult> AddZoneMatchesAsync(
        PackageIndexSearchResult result, string query, string? category, PackageIndexSearchScope scope, int resultLimit,
        Action<string> showStatus, CancellationToken cancellationToken)
    {
        if (scope == PackageIndexSearchScope.ObjectName || _gameMetadataIndex is not { } metadata ||
            string.IsNullOrEmpty(_libraryRootPath)) return result;
        var zones = ZoneMapSidecar.Match(_zoneMap, query);
        if (zones.Count is 0 or > MaximumZonesPerSearch) return result;
        var watch = Stopwatch.StartNew();
        showStatus(zones.Count == 1 ? $"Searching the map of {zones[0].Name}..." : $"Searching the maps of {zones.Count} zones...");
        var maps = await _packageIndexStore.SearchZonesAsync(_libraryRootPath, zones, metadata, _protectionProvider, category,
            resultLimit, cancellationToken);
        var seen = maps.Matches.Select(match => (match.SourcePath, match.ExportIndex)).ToHashSet();
        var others = result.Matches.Where(match => !seen.Contains((match.SourcePath, match.ExportIndex))).ToArray();
        Log($"Zone search: query={query}, zones={string.Join("; ", zones.Select(zone => $"{zone.Id} {zone.Name}"))}, " +
            $"mapAssets={maps.TotalMatches}, seconds={watch.Elapsed.TotalSeconds:N1}.");
        return result with
        {
            Matches = maps.Matches.Concat(others).Take(resultLimit).ToArray(),
            TotalMatches = maps.TotalMatches + others.Length,
            PackageCount = result.PackageCount + maps.PackageCount,
        };
    }
}
