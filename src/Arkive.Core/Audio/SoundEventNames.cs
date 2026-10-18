using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Arkive.Core.Audio;

/// <summary>
/// The names of the game's sound events, taken from the AkEvent objects Arkive's package index already lists
/// (for example g_kazeroth1_attack1_cast1 in the s_mob_g_kazeroth1 package). Reading them means opening every index
/// file once, so the list is kept in cache/sounds/event-names.tsv.gz until the index changes.
/// </summary>
public static class SoundEventNames
{
    private const string Header = "# arkive-sound-event-names v1 ";

    public static async Task<IReadOnlyList<(string Name, string Set)>> LoadAsync(
        string indexCacheDirectory,
        string soundCacheDirectory,
        IProgress<SoundCatalogProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var indexFiles = Directory.Exists(indexCacheDirectory)
            ? Directory.GetFiles(indexCacheDirectory, "*.json.gz", SearchOption.TopDirectoryOnly)
            : [];
        var stamp = Stamp(indexFiles);
        var cachePath = Path.Combine(soundCacheDirectory, "event-names.tsv.gz");
        if (TryReadCache(cachePath, stamp) is { } cached) return cached;

        var names = new ConcurrentBag<(string Name, string Set)>();
        var completed = 0;
        await Parallel.ForEachAsync(
            indexFiles,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1), CancellationToken = cancellationToken },
            (path, token) =>
            {
                try
                {
                    foreach (var item in ReadEvents(path)) names.Add(item);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
                {
                    // A damaged index file only hides its own sounds; index health reports it.
                }
                var done = Interlocked.Increment(ref completed);
                if (done % 500 == 0)
                    progress?.Report(new SoundCatalogProgress(done, indexFiles.Length, "Collecting sound names from the package index"));
                return ValueTask.CompletedTask;
            });
        var result = names.Distinct().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        WriteCache(cachePath, stamp, result);
        return result;
    }

    private static IEnumerable<(string Name, string Set)> ReadEvents(string path)
    {
        byte[] json;
        using (var input = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
        using (var buffer = new MemoryStream())
        {
            input.CopyTo(buffer);
            json = buffer.ToArray();
        }
        if (json.AsSpan().IndexOf("\"akevent\""u8) < 0) return [];

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var set = root.TryGetProperty("PackageAlias", out var alias) ? alias.GetString() ?? string.Empty : string.Empty;
        if (!root.TryGetProperty("Assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return [];
        var events = new List<(string, string)>();
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("ClassName", out var className) &&
                string.Equals(className.GetString(), "akevent", StringComparison.OrdinalIgnoreCase) &&
                asset.TryGetProperty("ObjectName", out var objectName) &&
                objectName.GetString() is { Length: > 0 } name)
            {
                events.Add((name, set));
            }
        }
        return events;
    }

    private static string Stamp(string[] indexFiles)
    {
        long newest = 0;
        foreach (var file in indexFiles) newest = Math.Max(newest, File.GetLastWriteTimeUtc(file).Ticks);
        return $"{indexFiles.Length}:{newest}";
    }

    private static IReadOnlyList<(string Name, string Set)>? TryReadCache(string path, string stamp)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress), Encoding.UTF8);
            if (reader.ReadLine() != Header + stamp) return null;
            var names = new List<(string, string)>();
            while (reader.ReadLine() is { } line)
            {
                var tab = line.IndexOf('\t');
                if (tab > 0) names.Add((line[..tab], line[(tab + 1)..]));
            }
            return names;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static void WriteCache(string path, string stamp, IReadOnlyList<(string Name, string Set)> names)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var writer = new StreamWriter(new GZipStream(File.Create(temporary), CompressionLevel.Fastest), new UTF8Encoding(false)))
        {
            writer.WriteLine(Header + stamp);
            foreach (var (name, set) in names) writer.WriteLine($"{name}\t{set}");
        }
        File.Move(temporary, path, overwrite: true);
    }
}
