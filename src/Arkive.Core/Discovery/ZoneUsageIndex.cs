using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Arkive.Core.Archives;

namespace Arkive.Core.Discovery;

/// <summary>
/// A map: the level packages one or more zones load. Zones that load the same levels (a raid gate and its hard mode,
/// a field and its sub-areas) are one map, so a part they all use is still only this map's.
/// </summary>
/// <param name="Key">The zones' persistent levels, sorted and joined (lv_kur_death_ed_05_ps).</param>
/// <param name="LevelPackages">Every level package of the map: its persistent levels and the ones sharing their stem.</param>
public sealed record ZoneMap(string Key, IReadOnlyList<GameZone> Zones, IReadOnlyList<string> LevelPackages)
{
    /// <summary>The map's name in game: its first zone's (zones of one map nearly always share a name).</summary>
    public string Name => Zones[0].Name;

    /// <summary>
    /// Groups zones by the levels they load, each map with the level packages it reads. Zones whose levels have no
    /// package (removed from the game) are left out.
    /// </summary>
    public static IReadOnlyList<ZoneMap> Group(IEnumerable<GameZone> zones, IEnumerable<string> levelPackages)
    {
        var levels = levelPackages.Where(alias => alias.StartsWith("lv_", StringComparison.OrdinalIgnoreCase))
            .Select(alias => alias.ToLowerInvariant()).Distinct().ToArray();
        return zones.GroupBy(KeyOf, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ZoneMap(group.Key, group.OrderBy(zone => zone.Id).ToArray(),
                group.First().Levels.SelectMany(level => ZoneMapSidecar.PackagesOf(level, levels))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()))
            .Where(map => map.LevelPackages.Count > 0)
            .OrderBy(map => map.Zones[0].Id)
            .ToArray();
    }

    public static string KeyOf(GameZone zone) =>
        string.Join('|', zone.Levels.Select(level => level.ToLowerInvariant()).Order(StringComparer.Ordinal));
}

public sealed record ZoneUsageProgress(int Completed, int Total);

/// <summary>
/// Which maps use which assets, for telling a map's own parts from the ones other maps share: for every map, the
/// objects its level packages import from other packages (meshes, materials, and the textures the levels stream).
/// Reading every map's levels takes about half a minute, so the index is kept beside the game-name database
/// (&lt;metadata&gt;.zone-usage.bin.gz, about 2 MB) and rebuilt only when the zones or a level package change.
/// </summary>
public sealed class ZoneUsageIndex
{
    public const int CurrentVersion = 1;

    private readonly Dictionary<string, int[]> _mapsByObject;
    private readonly Dictionary<string, int[]> _mapsByLevelPackage;
    private readonly Dictionary<long, ZoneMap> _mapsByZone;

    private ZoneUsageIndex(string fingerprint, IReadOnlyList<ZoneMap> maps, Dictionary<string, int[]> mapsByObject)
    {
        Fingerprint = fingerprint;
        Maps = maps;
        _mapsByObject = mapsByObject;
        _mapsByLevelPackage = maps.SelectMany((map, index) => map.LevelPackages.Select(alias => (alias, index)))
            .GroupBy(item => item.alias, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.index).Distinct().ToArray(),
                StringComparer.OrdinalIgnoreCase);
        _mapsByZone = maps.SelectMany(map => map.Zones.Select(zone => (zone.Id, map)))
            .GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First().map);
    }

    public string Fingerprint { get; }

    public IReadOnlyList<ZoneMap> Maps { get; }

    public ZoneMap? MapOf(long zoneId) => _mapsByZone.GetValueOrDefault(zoneId);

    /// <summary>
    /// The maps that use an object: those whose levels import it, and those that hold it in one of their own level
    /// packages.
    /// </summary>
    public IReadOnlyList<ZoneMap> MapsUsing(string packageName, string objectName)
    {
        IEnumerable<int> maps = _mapsByObject.GetValueOrDefault(Key(packageName, objectName)) ?? [];
        if (_mapsByLevelPackage.TryGetValue(packageName, out var holders)) maps = maps.Concat(holders);
        return maps.Distinct().Order().Select(index => Maps[index]).ToArray();
    }

    public static string GetDefaultPath(string metadataPath)
    {
        var fullPath = Path.GetFullPath(metadataPath);
        return Path.Combine(Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(fullPath) + ".zone-usage.bin.gz");
    }

    /// <summary>
    /// What the index was built from: the zones (their archive's size and date) and every map level package's size
    /// and date. Any change (a game update) makes a saved index stale.
    /// </summary>
    public static string ComputeFingerprint(ZoneMapDocument zones, IReadOnlyList<ZoneMap> maps,
        IReadOnlyDictionary<string, string> packageFiles)
    {
        var text = new StringBuilder($"{CurrentVersion}|{zones.ArchiveLength}|{zones.ArchiveLastWriteUtcTicks}|{zones.Zones.Count}");
        foreach (var alias in maps.SelectMany(map => map.LevelPackages).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = packageFiles.TryGetValue(alias, out var path) ? new FileInfo(path) : null;
            text.Append('|').Append(alias).Append(':').Append(file is { Exists: true } ? file.Length : -1)
                .Append(':').Append(file is { Exists: true } ? file.LastWriteTimeUtc.Ticks : 0);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    /// <summary>The saved index when it was built from the same game files, otherwise null.</summary>
    public static ZoneUsageIndex? TryLoad(string path, string fingerprint)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip, Encoding.UTF8);
            if (reader.ReadInt32() != CurrentVersion || reader.ReadString() != fingerprint) return null;
            var maps = new ZoneMap[reader.ReadInt32()];
            for (var index = 0; index < maps.Length; index++)
            {
                var key = reader.ReadString();
                var zones = new GameZone[reader.ReadInt32()];
                for (var zone = 0; zone < zones.Length; zone++)
                {
                    var id = reader.ReadInt64();
                    var name = reader.ReadString();
                    zones[zone] = new GameZone(id, name, ReadStrings(reader));
                }
                maps[index] = new ZoneMap(key, zones, ReadStrings(reader));
            }
            var objects = ReadStrings(reader);
            var users = new List<int>[objects.Length];
            for (var map = 0; map < maps.Length; map++)
            {
                var count = reader.ReadInt32();
                var previous = 0;
                for (var item = 0; item < count; item++)
                {
                    previous += reader.Read7BitEncodedInt();
                    (users[previous] ??= []).Add(map);
                }
            }
            var byObject = new Dictionary<string, int[]>(objects.Length, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < objects.Length; index++)
                if (users[index] is { } list) byObject[objects[index]] = list.ToArray();
            return new ZoneUsageIndex(fingerprint, maps, byObject);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or
                                              IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads every map's level packages (in parallel; one that cannot be read is skipped) and saves the index at
    /// <paramref name="path"/>.
    /// </summary>
    public static async Task<ZoneUsageIndex> BuildAsync(
        string path,
        ZoneMapDocument zones,
        IReadOnlyDictionary<string, string> packageFiles,
        IChunkProtectionProvider protectionProvider,
        IProgress<ZoneUsageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var maps = ZoneMap.Group(zones.Zones, packageFiles.Keys);
        var fingerprint = await Task.Run(() => ComputeFingerprint(zones, maps, packageFiles), cancellationToken)
            .ConfigureAwait(false);
        var packages = maps.SelectMany(map => map.LevelPackages).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var imports = new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        await Parallel.ForEachAsync(packages,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8), CancellationToken = cancellationToken },
            (alias, _) =>
            {
                try
                {
                    imports[alias] = ZoneMapSidecar.LevelContents(packageFiles[alias], alias, protectionProvider)
                        .Where(item => item.Imported)
                        .Select(item => Key(item.PackageName, item.ObjectName))
                        .Distinct(StringComparer.Ordinal).ToArray();
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or
                                                      UnauthorizedAccessException)
                {
                }
                var done = Interlocked.Increment(ref completed);
                if (done % 50 == 0 || done == packages.Length) progress?.Report(new ZoneUsageProgress(done, packages.Length));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var objects = new List<string>();
        var perMap = maps.Select(map => map.LevelPackages
                .SelectMany(alias => imports.GetValueOrDefault(alias) ?? [])
                .Select(key =>
                {
                    if (!ids.TryGetValue(key, out var id))
                    {
                        ids[key] = id = objects.Count;
                        objects.Add(key);
                    }
                    return id;
                })
                .Distinct().Order().ToArray())
            .ToArray();
        await SaveAsync(path, fingerprint, maps, objects, perMap, cancellationToken).ConfigureAwait(false);
        var byObject = new Dictionary<string, int[]>(objects.Count, StringComparer.OrdinalIgnoreCase);
        var users = new List<int>[objects.Count];
        for (var map = 0; map < perMap.Length; map++)
            foreach (var id in perMap[map])
                (users[id] ??= []).Add(map);
        for (var index = 0; index < objects.Count; index++)
            if (users[index] is { } list) byObject[objects[index]] = list.ToArray();
        return new ZoneUsageIndex(fingerprint, maps, byObject);
    }

    private static async Task SaveAsync(string path, string fingerprint, IReadOnlyList<ZoneMap> maps,
        IReadOnlyList<string> objects, IReadOnlyList<int[]> perMap, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        await using (var file = File.Create(temporary))
        await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        await using (var writer = new BinaryWriter(gzip, Encoding.UTF8))
        {
            writer.Write(CurrentVersion);
            writer.Write(fingerprint);
            writer.Write(maps.Count);
            foreach (var map in maps)
            {
                writer.Write(map.Key);
                writer.Write(map.Zones.Count);
                foreach (var zone in map.Zones)
                {
                    writer.Write(zone.Id);
                    writer.Write(zone.Name);
                    WriteStrings(writer, zone.Levels);
                }
                WriteStrings(writer, map.LevelPackages);
            }
            WriteStrings(writer, objects);
            foreach (var ids in perMap)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(ids.Length);
                var previous = 0;
                foreach (var id in ids)
                {
                    writer.Write7BitEncodedInt(id - previous);
                    previous = id;
                }
            }
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static string Key(string packageName, string objectName) =>
        (packageName + "." + objectName).ToLowerInvariant();

    private static void WriteStrings(BinaryWriter writer, IReadOnlyList<string> values)
    {
        writer.Write(values.Count);
        foreach (var value in values) writer.Write(value);
    }

    private static string[] ReadStrings(BinaryReader reader)
    {
        var values = new string[reader.ReadInt32()];
        for (var index = 0; index < values.Length; index++) values[index] = reader.ReadString();
        return values;
    }
}
