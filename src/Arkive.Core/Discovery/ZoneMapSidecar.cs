using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Databases;
using Arkive.Core.Skills;
using Microsoft.Data.Sqlite;

namespace Arkive.Core.Discovery;

/// <summary>A zone of the game (an area, dungeon or raid arena), its in-game name and the map levels it loads.</summary>
/// <param name="Levels">The zone's persistent levels (lv_kur_death_ed_05_ps); its other level packages share their stem.</param>
public sealed record GameZone(long Id, string Name, IReadOnlyList<string> Levels);

public sealed record ZoneMapDocument(
    int Version,
    DateTimeOffset GeneratedAt,
    string ArchivePath,
    long ArchiveLength,
    long ArchiveLastWriteUtcTicks,
    IReadOnlyList<GameZone> Zones);

/// <summary>An asset a zone's map places, holds or probably draws with, and why it is listed.</summary>
public sealed record ZoneMapAsset(string PackageName, string ObjectName, string Field, string Confidence);

/// <summary>
/// In-game names for maps: which zone ("Land of Death and Order") loads which level packages. Zones and their
/// persistent levels come from the game's own map data (leveldata1.lpk MapData\&lt;zone&gt;\MinimapData), names from
/// data2.lpk; a level's streaming levels are the level packages that share its stem (lv_kur_death_ed_05_sl01). What a
/// map uses is read from its level packages only when a zone is searched (see <see cref="FindAssetsAsync"/>), because
/// every zone's assets together are millions of rows. Kept beside the game-name database and rebuilt whenever
/// leveldata1.lpk changes.
/// </summary>
public static partial class ZoneMapSidecar
{
    public const int CurrentVersion = 1;
    public const string EntityType = "Zone";
    public const string Relation = "ZoneMapAsset";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string GetDefaultPath(string metadataPath)
    {
        var fullPath = Path.GetFullPath(metadataPath);
        return Path.Combine(Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(fullPath) + ".zones.json.gz");
    }

    /// <summary>EFGame\leveldata1.lpk for a Packages folder (EFGame\ReleasePC\Packages), or null when it is missing.</summary>
    public static string? FindArchive(string packagesRoot)
    {
        var path = Path.GetFullPath(Path.Combine(packagesRoot, "..", "..", "leveldata1.lpk"));
        return File.Exists(path) ? path : null;
    }

    public static ZoneMapDocument? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var document = JsonSerializer.Deserialize<ZoneMapDocument>(gzip, JsonOptions);
            return document?.Version == CurrentVersion ? document : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    public static bool IsCurrent(string metadataPath, string packagesRoot)
    {
        var archive = FindArchive(packagesRoot);
        if (archive is null) return true;
        var document = TryLoad(GetDefaultPath(metadataPath));
        if (document is null) return false;
        var info = new FileInfo(archive);
        return document.ArchivePath.Equals(info.FullName, StringComparison.OrdinalIgnoreCase) &&
               document.ArchiveLength == info.Length && document.ArchiveLastWriteUtcTicks == info.LastWriteTimeUtc.Ticks;
    }

    /// <summary>Reads every named zone and its persistent levels and saves them beside the game-name database.</summary>
    public static async Task<ZoneMapDocument?> BuildAsync(
        string metadataPath, string packagesRoot, CancellationToken cancellationToken = default)
    {
        var archive = FindArchive(packagesRoot);
        if (archive is null) return null;
        var zones = await Task.Run(() => ReadZones(packagesRoot, cancellationToken: cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        var info = new FileInfo(archive);
        var document = new ZoneMapDocument(CurrentVersion, DateTimeOffset.UtcNow, info.FullName, info.Length,
            info.LastWriteTimeUtc.Ticks, zones);
        await SaveAsync(GetDefaultPath(metadataPath), document, cancellationToken).ConfigureAwait(false);
        return document;
    }

    /// <summary>The zones whose in-game name the query matches, the way the asset library matches in-game names.</summary>
    public static IReadOnlyList<GameZone> Match(ZoneMapDocument? document, string query) =>
        document is null || string.IsNullOrWhiteSpace(query)
            ? []
            : document.Zones.Where(zone => AssetSearch.MatchesDisplayNames(query, [zone.Name])).ToArray();

    /// <summary>Every zone with map data and a name, with its persistent levels.</summary>
    public static IReadOnlyList<GameZone> ReadZones(string packagesRoot, string language = "English",
        CancellationToken cancellationToken = default)
    {
        var archive = FindArchive(packagesRoot) ??
                      throw new FileNotFoundException("The game's leveldata1.lpk was not found beside its Packages folder.");
        var levels = new Dictionary<long, IReadOnlyList<string>>();
        using (var stream = File.OpenRead(archive))
        {
            foreach (var entry in LostArkDataArchive.ReadEntries(stream, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var match = MinimapEntry().Match(entry.Path);
                if (!match.Success) continue;
                byte[] data;
                try
                {
                    data = LostArkDataArchive.Read(stream, entry);
                }
                catch (InvalidDataException)
                {
                    continue;
                }
                var found = PersistentLevels(data);
                if (found.Count > 0) levels[long.Parse(match.Groups[1].Value)] = found;
            }
        }
        var data2 = Path.GetFullPath(Path.Combine(packagesRoot, "..", "..", "data2.lpk"));
        var names = File.Exists(data2) ? ReadZoneNames(data2, language, cancellationToken) : new Dictionary<long, string>();
        return levels.Where(pair => names.ContainsKey(pair.Key))
            .Select(pair => new GameZone(pair.Key, names[pair.Key], pair.Value))
            .OrderBy(zone => zone.Id)
            .ToArray();
    }

    /// <summary>
    /// What the zones' maps use, read from their level packages: the level packages' own editable assets and the ones
    /// they import from other packages, and, as less certain, the other editable assets in the packages of the meshes a
    /// map places (where those meshes' materials and textures usually are; a level only names the meshes). Level
    /// packages are read in parallel; one that cannot be read is skipped.
    /// </summary>
    /// <param name="packageFiles">Every package's file by its name (PackageIndexStore.PackageFilesByAliasAsync).</param>
    /// <param name="packageAssets">The editable assets (object names) of each package asked for, from the package index.</param>
    public static async Task<IReadOnlyDictionary<GameZone, IReadOnlyList<ZoneMapAsset>>> FindAssetsAsync(
        IReadOnlyList<GameZone> zones,
        IReadOnlyDictionary<string, string> packageFiles,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyDictionary<string, IReadOnlyList<string>>>> packageAssets,
        IChunkProtectionProvider protectionProvider,
        CancellationToken cancellationToken = default)
    {
        var levelPackages = packageFiles.Where(pair => pair.Key.StartsWith("lv_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var levelsByZone = zones.ToDictionary(zone => zone, zone => zone.Levels
            .SelectMany(level => PackagesOf(level, levelPackages.Keys)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var contents = new ConcurrentDictionary<string, IReadOnlyList<LevelReference>>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(levelsByZone.Values.SelectMany(aliases => aliases).Distinct(StringComparer.OrdinalIgnoreCase),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8), CancellationToken = cancellationToken },
            (alias, _) =>
            {
                try
                {
                    contents[alias] = LevelContents(levelPackages[alias], alias, protectionProvider);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or
                                                      UnauthorizedAccessException)
                {
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        var meshPackages = contents.Values.SelectMany(items => items)
            .Where(item => item.Imported && (item.ClassName.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) ||
                                             item.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.PackageName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mates = meshPackages.Count == 0
            ? new Dictionary<string, IReadOnlyList<string>>()
            : await packageAssets(meshPackages, cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<GameZone, IReadOnlyList<ZoneMapAsset>>();
        foreach (var (zone, aliases) in levelsByZone)
        {
            var assets = new Dictionary<(string, string), ZoneMapAsset>();
            var zoneMeshPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var alias in aliases)
            {
                if (!contents.TryGetValue(alias, out var items)) continue;
                foreach (var item in items)
                {
                    assets.TryAdd((item.PackageName.ToLowerInvariant(), item.ObjectName.ToLowerInvariant()),
                        new ZoneMapAsset(item.PackageName, item.ObjectName,
                            item.Imported ? $"Used in map ({alias})" : $"Part of map ({alias})",
                            GameAssetAssociationConfidence.Verified));
                    if (meshPackages.Contains(item.PackageName) && item.Imported) zoneMeshPackages.Add(item.PackageName);
                }
            }
            foreach (var package in zoneMeshPackages)
            {
                if (!mates.TryGetValue(package, out var objects)) continue;
                foreach (var objectName in objects)
                    assets.TryAdd((package.ToLowerInvariant(), objectName.ToLowerInvariant()),
                        new ZoneMapAsset(package, objectName, $"In a package of the map's meshes ({package})",
                            GameAssetAssociationConfidence.Inferred));
            }
            result[zone] = assets.Values.ToArray();
        }
        return result;
    }

    /// <summary>The in-game name an asset of a zone's map is shown with in the asset library.</summary>
    public static GameAssetAssociation Association(GameZone zone, ZoneMapAsset asset) =>
        new($"{zone.Name} (map)", $"tip.name.zonebase_{zone.Id}", EntityType, zone.Id, asset.Field,
            $"{asset.PackageName}.{asset.ObjectName}", asset.PackageName, asset.ObjectName)
        {
            Confidence = asset.Confidence,
            Relation = Relation,
        };

    /// <summary>A persistent level's package and the streaming levels that share its stem (lv_x_ps -> lv_x_*).</summary>
    public static IEnumerable<string> PackagesOf(string persistentLevel, IEnumerable<string> levelPackages)
    {
        var stem = persistentLevel.EndsWith("_ps", StringComparison.OrdinalIgnoreCase)
            ? persistentLevel[..^3]
            : persistentLevel;
        return levelPackages.Where(alias =>
            alias.Equals(persistentLevel, StringComparison.OrdinalIgnoreCase) ||
            alias.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An editable asset a level package holds (Imported false) or imports from another package.</summary>
    public sealed record LevelReference(string PackageName, string ObjectName, string ClassName, bool Imported);

    /// <summary>
    /// What a level package holds and refers to that Arkive can edit: its own textures, meshes, materials and effects,
    /// and the ones it imports from other packages (the outermost package of an import is its package).
    /// </summary>
    public static IReadOnlyList<LevelReference> LevelContents(
        string packagePath, string alias, IChunkProtectionProvider protectionProvider)
    {
        var catalog = new AssetCatalogReader(protectionProvider: protectionProvider).Read(packagePath);
        var results = new List<LevelReference>();
        foreach (var asset in catalog.Assets)
            if (AssetTypes.Editable(AssetTypes.CatalogKind(asset.ClassName), asset.ClassName) is not null)
                results.Add(new LevelReference(alias.ToLowerInvariant(), asset.ObjectName, asset.ClassName, false));
        var imports = catalog.Imports;
        foreach (var import in imports)
        {
            var className = import.ClassName.Resolve(catalog.Names);
            if (AssetTypes.Editable(AssetTypes.CatalogKind(className), className) is null) continue;
            var outer = import;
            for (var guard = 0; outer.OuterIndex < 0 && -outer.OuterIndex <= imports.Count && guard < 16; guard++)
                outer = imports[-outer.OuterIndex - 1];
            if (ReferenceEquals(outer, import)) continue;
            results.Add(new LevelReference(outer.ObjectName.Resolve(catalog.Names).ToLowerInvariant(),
                import.ObjectName.Resolve(catalog.Names), className, true));
        }
        return results.Distinct().ToArray();
    }

    public static async Task SaveAsync(string path, ZoneMapDocument document, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        await using (var file = File.Create(temporary))
        await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            await JsonSerializer.SerializeAsync(gzip, document, JsonOptions, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    // A minimap names its persistent level and that level's tiles (LV_KUR_DEATH_ED_05_PS, LV_KUR_DEATH_ED_05_PS_0_0x0).
    private static IReadOnlyList<string> PersistentLevels(byte[] data) =>
        AsciiRun().Matches(Encoding.ASCII.GetString(data))
            .Select(match => match.Value)
            .Where(value => PersistentLevel().IsMatch(value))
            .Select(value => value.ToLowerInvariant())
            .Distinct()
            .ToArray();

    // Zone names are tip.name.zonebase_<id> in the game's text (one zone's key is misspelt zonebaes_).
    private static Dictionary<long, string> ReadZoneNames(string data2, string language, CancellationToken cancellationToken)
    {
        using var archive = new FileStream(data2, FileMode.Open, FileAccess.Read, FileShare.Read);
        var entry = LostArkDatabaseArchive.ReadEntries(archive, cancellationToken)
            .FirstOrDefault(item => item.Name == "EFTable_GameMsg.db");
        var names = new Dictionary<long, string>();
        if (entry is null) return names;
        var temporary = Path.Combine(Path.GetTempPath(), "Arkive", "zone-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var database = Path.Combine(temporary, entry.Name);
            LostArkDatabaseArchive.Extract(archive, entry, database, cancellationToken);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                       { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                var table = "GameMsg_" + (LanguageName().IsMatch(language) ? language : "English");
                command.CommandText = $"SELECT [KEY], [MSG] FROM [{table}] WHERE [KEY] LIKE 'tip.name.zoneba%'";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var match = ZoneNameKey().Match(reader.GetString(0));
                    var text = reader.GetString(1).Trim();
                    if (match.Success && text.Length > 0) names.TryAdd(long.Parse(match.Groups[1].Value), text);
                }
            }
            SqliteConnection.ClearAllPools();
            return names;
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The game's zone names could not be read: " + exception.Message, exception);
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [GeneratedRegex(@"(?:^|/)MapData/(\d+)/MinimapData\.loa$", RegexOptions.IgnoreCase)]
    private static partial Regex MinimapEntry();

    [GeneratedRegex(@"[\x20-\x7e]{6,}")]
    private static partial Regex AsciiRun();

    [GeneratedRegex(@"^LV_[A-Z0-9_]+_PS$", RegexOptions.IgnoreCase)]
    private static partial Regex PersistentLevel();

    [GeneratedRegex(@"^tip\.name\.zoneba(?:se|es)_(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ZoneNameKey();

    [GeneratedRegex("^[A-Za-z]+$")]
    private static partial Regex LanguageName();
}
