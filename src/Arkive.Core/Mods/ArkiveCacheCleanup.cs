namespace Arkive.Core.Mods;

/// <summary>One cache entry a clean-up removes: a folder or file Arkive makes again when it next needs it.</summary>
public sealed record ArkiveCacheItem(string Cache, string Path, long Bytes, DateTime LastUsedUtc);

/// <summary>The size of one cache before a clean-up, and what the clean-up would take from it.</summary>
public sealed record ArkiveCacheSize(string Cache, int Entries, long Bytes, int OldEntries, long OldBytes);

public sealed record ArkiveCacheCleanupPlan(IReadOnlyList<ArkiveCacheItem> Items, IReadOnlyList<ArkiveCacheSize> Sizes)
{
    public long Bytes => Items.Sum(item => item.Bytes);
}

/// <summary>
/// Removes cache entries not used for a while (30 days by default) from the caches Arkive rebuilds on demand: model
/// viewer scenes, particle editor textures and meshes, outfit-swap pieces and learnt shapes, body proportion pieces, sound
/// previews and zone maps. An entry is one folder or file Arkive makes and reads as a whole (see <see cref="Caches"/>);
/// its last use is the newest write time inside it, which the readers renew when they reuse an entry
/// (<see cref="MarkUsed"/>). The game data, library index and hash caches, the settings and every other file directly
/// in the cache folder are not looked at.
/// </summary>
public sealed class ArkiveCacheCleanup
{
    public static readonly TimeSpan DefaultUnusedAge = TimeSpan.FromDays(30);

    /// <summary>
    /// The caches, each with its folder under cache and the folders inside it whose own children are the entries (the
    /// other children of the cache folder are entries themselves). Entries listed in <c>Keep</c> stay.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Folder, string[] Nested, string[] Keep)> Caches =
    [
        ("Model viewer scenes", "model-viewer", ["animations", "environments"], []),
        ("Particle editor textures", "particle-textures", ["meshes"], []),
        ("Outfit swap pieces (command line)", "pieces", [], []),
        ("Outfit swap pieces and shapes", "outfit-swap", ["pieces", "fields", "work"], []),
        ("Sound previews", "sounds", ["preview"], ["event-names.tsv.gz"]),
        ("Body proportion pieces", "body-proportions", [], []),
        ("Zone maps", "zone-maps", [], []),
    ];

    private readonly string _cacheRoot;

    public ArkiveCacheCleanup(string dataRoot) => _cacheRoot = Path.Combine(Path.GetFullPath(dataRoot), "cache");

    public TimeSpan UnusedAge { get; init; } = DefaultUnusedAge;

    /// <summary>Notes that a cache entry was used now, so a clean-up keeps it another <see cref="DefaultUnusedAge"/>.</summary>
    public static void MarkUsed(string entryPath)
    {
        try
        {
            if (Directory.Exists(entryPath)) Directory.SetLastWriteTimeUtc(entryPath, DateTime.UtcNow);
            else if (File.Exists(entryPath)) File.SetLastWriteTimeUtc(entryPath, DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public ArkiveCacheCleanupPlan Plan(CancellationToken cancellationToken = default)
    {
        var items = new List<ArkiveCacheItem>();
        var sizes = new List<ArkiveCacheSize>();
        foreach (var (name, folder, _, _) in Caches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = Entries(folder).Select(path => Measure(name, path)).OfType<ArkiveCacheItem>().ToArray();
            var old = entries.Where(Old).ToArray();
            items.AddRange(old);
            sizes.Add(new ArkiveCacheSize(name, entries.Length, entries.Sum(entry => entry.Bytes), old.Length, old.Sum(entry => entry.Bytes)));
        }
        return new ArkiveCacheCleanupPlan(items, sizes);
    }

    /// <summary>Deletes the planned entries that are still unused now; returns those deleted.</summary>
    public IReadOnlyList<ArkiveCacheItem> Remove(ArkiveCacheCleanupPlan plan, CancellationToken cancellationToken = default)
    {
        var removed = new List<ArkiveCacheItem>();
        var allowed = Caches.SelectMany(cache => Entries(cache.Folder)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Only an entry of a known cache, checked again just before it goes.
            if (!allowed.Contains(item.Path) || Measure(item.Cache, item.Path) is not { } now || !Old(now)) continue;
            try
            {
                if (Directory.Exists(item.Path)) Directory.Delete(item.Path, recursive: true);
                else File.Delete(item.Path);
                removed.Add(now);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    private bool Old(ArkiveCacheItem item) => DateTime.UtcNow - item.LastUsedUtc > UnusedAge;

    private IEnumerable<string> Entries(string folder)
    {
        var (_, _, nested, keep) = Caches.Single(cache => cache.Folder == folder);
        var root = Path.Combine(_cacheRoot, folder);
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(path);
            if (keep.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (!nested.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(path);
                continue;
            }
            if (!Directory.Exists(path)) continue;
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) yield return Path.GetFullPath(child);
        }
    }

    private static ArkiveCacheItem? Measure(string cache, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                return new ArkiveCacheItem(cache, path, file.Length, file.LastWriteTimeUtc);
            }
            if (!Directory.Exists(path)) return null;
            var directory = new DirectoryInfo(path);
            var bytes = 0L;
            var newest = directory.LastWriteTimeUtc;
            foreach (var entry in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (entry.LastWriteTimeUtc > newest) newest = entry.LastWriteTimeUtc;
                if (entry is FileInfo file) bytes += file.Length;
            }
            return new ArkiveCacheItem(cache, path, bytes, newest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
