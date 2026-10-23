using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Inspection;
using Arkive.Core.Materials;
using Arkive.Core.Mods;
using Arkive.Core.Textures;

namespace Arkive.App;

public partial class MainWindow
{
    // A query that names more zones than this ("the") is not a map search.
    private const int MaximumZonesPerSearch = 12;
    // A search result lists at most this many maps it is used in; the Zone Viewer shows them all.
    private const int MapNamesPerResult = 3;

    private ZoneMapDocument? _zoneMap;
    private ZoneUsageIndex? _zoneUsage;
    private readonly SemaphoreSlim _zoneUsageGate = new(1, 1);

    private sealed record ZoneCardRow(ZoneMap Map, string Title, string Detail, string Tip)
    {
        public string Action => ZoneViewerText.CardAction;
    }

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
    /// before them. Object-name searches, queries naming many zones and searches filtered to a class (map assets
    /// belong to no class) are left as they are.
    /// </summary>
    private async Task<PackageIndexSearchResult> AddZoneMatchesAsync(
        PackageIndexSearchResult result, string query, string? category, string? archetype, PackageIndexSearchScope scope,
        int resultLimit, Action<string> showStatus, CancellationToken cancellationToken)
    {
        if (scope == PackageIndexSearchScope.ObjectName || !string.IsNullOrEmpty(archetype) || !MapsHave(category) ||
            _gameMetadataIndex is not { } metadata || string.IsNullOrEmpty(_libraryRootPath)) return result;
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

    /// <summary>
    /// The saved index of which maps use which assets, or null when it has not been built for these game files yet
    /// (the Zone Viewer builds it). Reading it takes about a second the first time.
    /// </summary>
    private async Task<ZoneUsageIndex?> LoadSavedZoneUsageAsync(CancellationToken cancellationToken)
    {
        if (_zoneUsage is not null || _zoneUsageLookedFor == _zoneMap) return _zoneUsage;
        if (_zoneMap is not { } zones || _gameMetadataIndex is not { } metadata || string.IsNullOrEmpty(_libraryRootPath) ||
            string.IsNullOrEmpty(_gameMetadataPath)) return null;
        var files = await _packageIndexStore.PackageFilesByAliasAsync(_libraryRootPath, metadata, cancellationToken);
        if (files is null) return null;
        var path = ZoneUsageIndex.GetDefaultPath(_gameMetadataPath);
        _zoneUsage ??= await Task.Run(() => ZoneUsageIndex.TryLoad(path,
            ZoneUsageIndex.ComputeFingerprint(zones, ZoneMap.Group(zones.Zones, files.Keys), files)), cancellationToken);
        // Checking the game files' dates takes a moment, so a missing index is looked for once per set of zone names.
        _zoneUsageLookedFor = zones;
        return _zoneUsage;
    }

    private ZoneMapDocument? _zoneUsageLookedFor;

    /// <summary>The index of which maps use which assets, built (about half a minute) when no saved one fits.</summary>
    private async Task<ZoneUsageIndex?> ZoneUsageAsync(IProgress<ZoneUsageProgress>? progress, CancellationToken cancellationToken)
    {
        await _zoneUsageGate.WaitAsync(cancellationToken);
        try
        {
            if (await LoadSavedZoneUsageAsync(cancellationToken) is { } saved) return saved;
            if (_zoneMap is not { } zones || _gameMetadataIndex is not { } metadata || string.IsNullOrEmpty(_libraryRootPath) ||
                string.IsNullOrEmpty(_gameMetadataPath)) return null;
            var files = await _packageIndexStore.PackageFilesByAliasAsync(_libraryRootPath, metadata, cancellationToken);
            if (files is null) return null;
            var watch = Stopwatch.StartNew();
            var path = ZoneUsageIndex.GetDefaultPath(_gameMetadataPath);
            _zoneUsage = await ZoneUsageIndex.BuildAsync(path, zones, files, _protectionProvider, progress, cancellationToken);
            Log($"Map usage index built: maps={_zoneUsage.Maps.Count}, seconds={watch.Elapsed.TotalSeconds:N1}, " +
                $"bytes={new FileInfo(path).Length}, path={path}.");
            return _zoneUsage;
        }
        finally
        {
            _zoneUsageGate.Release();
        }
    }

    /// <summary>
    /// Names, in the In-game name column, the maps each result is used in ("Land of Death and Order (map)"), once the
    /// map usage index exists. Results the zone search already linked to a map keep that link.
    /// </summary>
    private async Task<PackageIndexSearchResult> AddMapNamesAsync(PackageIndexSearchResult result, CancellationToken cancellationToken)
    {
        ZoneUsageIndex? usage;
        try
        {
            usage = await LoadSavedZoneUsageAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log($"Map names for search results were not read: {exception.Message}");
            return result;
        }
        if (usage is null) return result;
        return result with
        {
            Matches = result.Matches.Select(match =>
            {
                if (match.GameMetadata.Any(item => item.Relation == ZoneMapSidecar.Relation)) return match;
                var maps = usage.MapsUsing(match.PackageAlias, match.ObjectName);
                if (maps.Count == 0) return match;
                var asset = new ZoneMapAsset(match.PackageAlias, match.ObjectName,
                    maps.Count > MapNamesPerResult ? $"Used in {maps.Count:N0} maps" : "Used in map",
                    GameAssetAssociationConfidence.Verified);
                return match with
                {
                    GameMetadata = match.GameMetadata
                        .Concat(maps.Take(MapNamesPerResult).Select(map => ZoneMapSidecar.Association(map.Zones[0], asset)))
                        .ToArray(),
                };
            }).ToArray(),
        };
    }

    /// <summary>Shows a card for each map whose zone name the query matches, above the search results.</summary>
    /// <summary>Whether a map can hold assets of the category the search is filtered to (all, or textures, materials, static meshes).</summary>
    private static bool MapsHave(string? category) =>
        string.IsNullOrEmpty(category) || category is "Texture" or "Material" or "MaterialInstanceConstant" or "StaticMesh";

    private async Task ShowZoneCardsAsync(string query, string? category, string? archetype)
    {
        var zones = !string.IsNullOrEmpty(archetype) || !MapsHave(category) ? [] : ZoneMapSidecar.Match(_zoneMap, query);
        if (zones.Count is 0 or > MaximumZonesPerSearch || _gameMetadataIndex is not { } metadata ||
            string.IsNullOrEmpty(_libraryRootPath))
        {
            LibraryZoneCards.ItemsSource = null;
            LibraryZonePanel.Visibility = Visibility.Collapsed;
            return;
        }
        IReadOnlyDictionary<string, string>? files;
        try
        {
            files = await _packageIndexStore.PackageFilesByAliasAsync(_libraryRootPath, metadata);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log($"Map cards were not shown: {exception.Message}");
            LibraryZonePanel.Visibility = Visibility.Collapsed;
            return;
        }
        // A matched zone's map includes every other zone that loads the same levels (a gate and its hard mode).
        var allZones = _zoneMap!.Zones;
        var keys = zones.Select(ZoneMap.KeyOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maps = ZoneMap.Group(allZones.Where(zone => keys.Contains(ZoneMap.KeyOf(zone))), files?.Keys ?? []);
        var cards = maps.Select(map => new ZoneCardRow(map, ZoneViewerText.CardTitle(map, zones), ZoneViewerText.CardDetail(map),
            string.Join(Environment.NewLine, map.Zones.Select(zone => $"{zone.Id} {zone.Name}")))).ToArray();
        LibraryZoneCards.ItemsSource = cards;
        LibraryZoneHeaderText.Text = ZoneViewerText.CardsHeader(cards.Length);
        LibraryZonePanel.Visibility = cards.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LibraryZoneCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ZoneCardRow card }) OpenZoneViewer(card.Map);
    }

    private ZoneViewerWindow OpenZoneViewer(ZoneMap map)
    {
        var existing = OwnedWindows.OfType<ZoneViewerWindow>().FirstOrDefault(window => window.Title == ZoneViewerText.WindowTitle(map.Name));
        if (existing is not null)
        {
            existing.Activate();
            return existing;
        }
        var window = new ZoneViewerWindow(map, new ZoneViewerHost(
            (progress, token) => _packageIndexStore.ReadZoneMapAsync(_libraryRootPath!, map, _gameMetadataIndex!, _protectionProvider,
                progress, token),
            ZoneUsageAsync,
            LoadZonePictureAsync,
            ReadZoneMaterialAsync,
            OpenZonePart,
            () => _authoringDraft?.Metadata.Name,
            ShowEditModsForZone,
            AddZonePartsToModAsync,
            ExportZonePartsAsync,
            FindInLibraryFromZone,
            Log,
            (progress, token) => BuildZoneSceneAsync(map, progress, token),
            Path.Combine(_projectRoot, "cache", "webview2")), _effectiveAppearanceTheme)
        {
            Owner = this,
        };
        Log($"Zone viewer opened: map={map.Key}, zones={string.Join(", ", map.Zones.Select(zone => zone.Id))}.");
        window.Show();
        return window;
    }

    // The 3D scenes of this many maps are kept (5 to 30 MB each); older ones are removed when another is built.
    private const int KeptZoneScenes = 3;

    /// <summary>
    /// The map's 3D scene folder under cache\zone-maps: the saved one when its level packages are unchanged since it
    /// was built, otherwise built again (a few seconds).
    /// </summary>
    private async Task<string> BuildZoneSceneAsync(ZoneMap map, IProgress<ZoneSceneProgress> progress, CancellationToken cancellationToken)
    {
        if (_gameMetadataIndex is not { } metadata || string.IsNullOrEmpty(_libraryRootPath))
            throw new InvalidOperationException("Load the game database and the Asset Library first.");
        var root = Path.Combine(_projectRoot, "cache", "zone-maps");
        var directory = Path.Combine(root, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(map.Key)))[..16]);
        var files = await _packageIndexStore.PackageFilesByAliasAsync(_libraryRootPath, metadata, cancellationToken) ??
                    throw new InvalidOperationException("The Asset Library's index is not ready yet.");
        var stamp = string.Join("|", map.LevelPackages.Where(files.ContainsKey).Select(alias =>
        {
            var file = new FileInfo(files[alias]);
            return $"{alias}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        }));
        var stampPath = Path.Combine(directory, "levels.txt");
        if (ZoneMapScene.TryLoad(directory, map.Key) is { } saved && File.Exists(stampPath) &&
            await File.ReadAllTextAsync(stampPath, cancellationToken) == stamp)
        {
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow);
            if (!File.Exists(Path.Combine(directory, ZoneCubemapsFile))) await CopyZoneCubemapsAsync(saved, directory, cancellationToken);
            return directory;
        }
        var watch = Stopwatch.StartNew();
        var scene = await _packageIndexStore.BuildZoneMapSceneAsync(_libraryRootPath, map, metadata, _protectionProvider, directory,
            _nativeTextureExportService, progress, cancellationToken);
        await File.WriteAllTextAsync(stampPath, stamp, cancellationToken);
        await CopyZoneCubemapsAsync(scene, directory, cancellationToken);
        Log($"Zone 3D map built: map={map.Key}, placements={scene.Placements}, meshes={scene.Meshes.Count}, " +
            $"unreadMeshes={scene.UnreadMeshes}, materials={scene.Materials.Count}, terrain={scene.Terrain.Count}, " +
            $"seconds={watch.Elapsed.TotalSeconds:N1}, folder={directory}.");
        foreach (var old in new DirectoryInfo(root).EnumerateDirectories().OrderByDescending(folder => folder.LastWriteTimeUtc).Skip(KeptZoneScenes))
            try
            {
                old.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log($"An old zone 3D map was not deleted: {exception.Message}");
            }
        return directory;
    }

    private const string ZoneCubemapsFile = "cubemaps.json";

    /// <summary>
    /// Copies the game's environment cubemaps the map's lighting names (exported once for the Asset Viewer, a few
    /// hundred kilobytes each) beside its 3D scene, with cubemaps.json listing each one's six faces. A name the game's
    /// cubemap package lacks gets its first general cubemap instead, so the map is still lit by one.
    /// </summary>
    private async Task CopyZoneCubemapsAsync(ZoneMapScene scene, string directory, CancellationToken cancellationToken)
    {
        var faces = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (await FindViewerEnvironmentsAsync(cancellationToken) is { Environments.Count: > 0 } set)
                foreach (var name in scene.Environments.Select(environment => environment.Cubemap).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var cubemap = set.Environments.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
                                  set.Environments.FirstOrDefault(item => item.Name.Equals("pbr_cubemap_00", StringComparison.OrdinalIgnoreCase)) ??
                                  set.Environments[0];
                    var copied = new string[6];
                    for (var face = 0; face < 6; face++)
                    {
                        copied[face] = $"cube/{cubemap.Name}/{face}.png";
                        var target = Path.Combine(directory, copied[face]);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        if (!File.Exists(target)) File.Copy(Path.Combine(set.Directory, cubemap.Faces[face]), target);
                    }
                    faces[name] = copied;
                }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Zone 3D map cubemaps were not copied: {exception.Message}");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, ZoneCubemapsFile), System.Text.Json.JsonSerializer.Serialize(faces), cancellationToken);
    }

    private static LibraryAssetRow ToLibraryRow(PackageIndexMatch match) => new(match.SourcePath, match.PackageAlias,
        match.PhysicalPackageName, match.PackageAssetCount, match.ExportIndex, match.Kind, match.ClassName, match.ObjectName,
        match.SerialSize, match.SerialOffset, match.GameMetadata);

    /// <summary>
    /// Decodes a texture from the game files for the Zone Viewer's preview, at up to 1024 pixels across, in memory.
    /// The package is not hashed (the preview goes nowhere), which keeps big level packages quick.
    /// </summary>
    private async Task<RgbaImage> LoadZonePictureAsync(PackageIndexMatch asset, CancellationToken cancellationToken)
    {
        if (_nativeTextureExportService is null)
            throw new InvalidOperationException(_nativeTextureExportUnavailableReason ?? "Arkive's texture decoder is unavailable.");
        var report = await _textureReconnaissanceService.InspectAsync(new TextureReconnaissanceRequest(asset.SourcePath,
            asset.PackageAlias, asset.ExportIndex - 1, AssetKind.Texture, asset.ClassName, asset.ObjectName, asset.SerialOffset,
            asset.SerialSize, KnownPackageSha256: new string('0', 64)), cancellationToken);
        return await _nativeTextureExportService.DecodePreviewAsync(report, 1024, cancellationToken);
    }

    // Read for display only: the package is not hashed, which keeps big level packages quick.
    private Task<MaterialInstanceReconnaissanceReport> ReadZoneMaterialAsync(PackageIndexMatch asset, CancellationToken cancellationToken) =>
        new MaterialInstanceReconnaissanceService(_reader, _protectionProvider).InspectAsync(
            new MaterialInstanceReconnaissanceRequest(asset.SourcePath, asset.PackageAlias, asset.ExportIndex, AssetKind.Material,
                "MaterialInstanceConstant", asset.ObjectName, asset.SerialOffset, asset.SerialSize, new string('0', 64)), cancellationToken);

    /// <summary>Opens a map part in its viewer: textures in the Texture Viewer, effects in the effect editor, meshes in
    /// the Asset Viewer, materials in UModel.</summary>
    private void OpenZonePart(PackageIndexMatch asset)
    {
        var row = ToLibraryRow(asset);
        if (IsParticleRow(row)) OpenLibraryParticleEditor(row);
        else if (CanViewTexture(row.Kind)) _ = OpenTextureViewerAsync(row);
        else if (CanPreviewInArkive(row.Kind)) OpenModelViewer(ToModelViewerRequest(row), edits: null);
        else OpenWorkshopRowInUmodel(row);
    }

    private void ShowEditModsForZone()
    {
        MainTabControl.SelectedItem = _authoringDraft is null ? CreateModTab : WorkshopTab;
        Activate();
    }

    private void FindInLibraryFromZone(string objectName)
    {
        MainTabControl.SelectedItem = LibraryTab;
        LibrarySearchTextBox.Text = objectName;
        Activate();
        _ = ApplyLibraryFilterAsync("zone viewer");
    }

    /// <summary>
    /// Adds map parts to the mod open in Edit Mods as working copies (ones already in it are reused), then, with a
    /// tint, recolours them through <see cref="MaterialTint"/>. Returns what happened, for the Zone Viewer.
    /// </summary>
    private async Task<string> AddZonePartsToModAsync(IReadOnlyList<PackageIndexMatch> parts, TextureRecolour? tint,
        IProgress<ZoneViewerProgress> progress, CancellationToken cancellationToken)
    {
        if (_authoringDraft is null) return ZoneViewerText.NoMod;
        var mod = _authoringDraft.Metadata.Name;
        var edits = new List<ArkiveModLocalEdit>();
        var failed = new List<string>();
        int added = 0, already = 0;
        bool ran = false, finished = false;
        await RunAuthoringExtractionAsync(async token =>
        {
            ran = true;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            var session = await CreateAuthoringAddSessionAsync(linked.Token);
            ReportAuthoringExtraction("Adding map parts", 0, parts.Count);
            for (var index = 0; index < parts.Count; index++)
            {
                linked.Token.ThrowIfCancellationRequested();
                var row = ToLibraryRow(parts[index]);
                progress.Report(new ZoneViewerProgress($"Adding {row.ObjectName}", index, parts.Count));
                try
                {
                    var existed = session.Existing.ContainsKey(AuthoringAssetKey(row.SourcePath, row.ObjectName));
                    edits.Add(await AddAuthoringAssetAsync(row, session));
                    if (existed) already++;
                    else added++;
                }
                catch (Exception exception) when (exception is ArkiveModRefusalException or InvalidDataException or IOException or
                                                      NotSupportedException)
                {
                    failed.Add($"{row.ObjectName}: {exception.Message}");
                    Log($"Zone part not added to {mod}: {row.PackageAlias}.{row.ObjectName}: {exception}");
                }
                ReportAuthoringExtraction("Adding map parts", index + 1, parts.Count);
            }
            finished = true;
        });
        if (!ran) return "Edit Mods is busy with another job. Try again when it finishes.";
        cancellationToken.ThrowIfCancellationRequested();
        // Anything but a part that could not be added stops the job, and Edit Mods says why.
        if (!finished) return AuthoringStatusText.Text;
        var text = added == 0 && already == 0 ? $"No parts were added to {mod}."
            : already > 0 ? $"Added {Parts(added)} to {mod} ({already:N0} were already in it)."
            : $"Added {Parts(added)} to {mod}.";
        if (tint is not null && edits.Count > 0)
        {
            progress.Report(new ZoneViewerProgress("Recolouring", 0, edits.Count));
            var result = await MaterialTint.TintAsync(new MaterialTintRequest(edits, tint),
                new Progress<MaterialTintProgress>(item => progress.Report(
                    new ZoneViewerProgress(item.ObjectName.Length == 0 ? "Recolouring" : $"Recolouring {item.ObjectName}", item.Completed, item.Total))),
                cancellationToken);
            text += $" Recoloured {Parts(result.Changed)}";
            if (result.Unchanged > 0) text += $"; {result.Unchanged:N0} had none of the chosen colours";
            if (result.Skipped.Count > 0) text += $"; {result.Skipped.Count:N0} could not be recoloured ({result.Skipped[0].Reason})";
            text += ".";
            Log($"Zone parts recoloured in {mod}: changed={result.Changed}, unchanged={result.Unchanged}, " +
                $"skipped={string.Join("; ", result.Skipped.Select(skip => $"{skip.ObjectName}: {skip.Reason}"))}.");
            await RefreshModAuthoringAsync();
        }
        if (failed.Count > 0) text += $" {failed.Count:N0} could not be added: {failed[0]}";
        text += " Save the mod in Edit Mods to keep the changes; nothing in the game changed.";
        AuthoringStatusText.Text = text;
        return text;

        static string Parts(int count) => count == 1 ? "1 part" : $"{count:N0} parts";
    }

    /// <summary>Exports map parts with UModel into a folder, one subfolder per package.</summary>
    private async Task<string> ExportZonePartsAsync(IReadOnlyList<PackageIndexMatch> parts, string folder,
        IProgress<ZoneViewerProgress> progress, CancellationToken cancellationToken)
    {
        if (_umodelBridge is null) return "UModel is missing, so Arkive cannot export. See Diagnostics.";
        int files = 0, failed = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = ToLibraryRow(parts[index]);
            progress.Report(new ZoneViewerProgress($"Exporting {row.ObjectName}", index, parts.Count));
            try
            {
                var result = await _umodelBridge.ExportAsync(row.SourcePath, CreateExternalAsset(row),
                    Path.Combine(folder, row.PackageAlias), allowPackageWideFallback: false, cancellationToken);
                files += result.OutputFiles.Count;
                if (result.OutputFiles.Count == 0) failed++;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or
                                                  System.ComponentModel.Win32Exception)
            {
                failed++;
                Log($"Zone part export failed: {row.PackageAlias}.{row.ObjectName}: {exception}");
            }
        }
        Log($"Zone parts exported: parts={parts.Count}, files={files}, failed={failed}, folder={folder}.");
        return failed == 0
            ? $"Exported {parts.Count:N0} parts ({files:N0} files) to {folder}."
            : $"Exported {parts.Count - failed:N0} of {parts.Count:N0} parts ({files:N0} files) to {folder}; see the session log for the rest.";
    }
}
