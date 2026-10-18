using System.Collections.Concurrent;

namespace Arkive.Core.Assets;

/// <summary>
/// Package catalogues read once and shared, for work that visits the same packages many times (a skill's effects share
/// modules, materials and textures across a few dozen packages). A package that changes on disk is read again.
/// Thread-safe.
/// </summary>
public sealed class AssetCatalogCache(AssetCatalogReader reader, int capacity = 512)
{
    private readonly ConcurrentDictionary<string, Lazy<AssetCatalog>> _catalogs = new(StringComparer.OrdinalIgnoreCase);

    public AssetCatalog Read(string path)
    {
        var file = new FileInfo(path);
        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        if (_catalogs.Count > capacity) _catalogs.Clear();
        var entry = _catalogs.GetOrAdd(key, _ => new Lazy<AssetCatalog>(() => reader.Read(file.FullName)));
        try
        {
            return entry.Value;
        }
        catch
        {
            _catalogs.TryRemove(key, out _);
            throw;
        }
    }
}
