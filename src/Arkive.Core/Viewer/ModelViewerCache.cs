using System.Security.Cryptography;
using System.Text;

namespace Arkive.Core.Viewer;

/// <summary>
/// Size-capped cache of prepared viewer scenes. Each scene lives in its own folder;
/// the least recently opened scenes are deleted once the cap is exceeded.
/// </summary>
public sealed class ModelViewerCache
{
    public const long DefaultMaximumBytes = 1L << 30;
    private const string ReadyMarker = "scene.json";

    public ModelViewerCache(string rootDirectory, long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        MaximumBytes = Math.Max(64L << 20, maximumBytes);
    }

    public string RootDirectory { get; }
    public long MaximumBytes { get; }

    /// <summary>A key that changes whenever the source package file changes.</summary>
    public static string Key(string packagePath, int exportIndex, string objectName, int schemaVersion)
    {
        var file = new FileInfo(packagePath);
        var identity = string.Join('|', Path.GetFullPath(packagePath).ToUpperInvariant(), file.Length,
            file.LastWriteTimeUtc.Ticks, exportIndex, objectName.ToUpperInvariant(), schemaVersion);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        var safe = new string(objectName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return $"{safe[..Math.Min(48, safe.Length)]}-{hash}";
    }

    public string DirectoryFor(string key) => Path.Combine(RootDirectory, key);

    public bool IsReady(string key) => File.Exists(Path.Combine(DirectoryFor(key), ReadyMarker));

    public void Touch(string key)
    {
        var directory = DirectoryFor(key);
        if (Directory.Exists(directory)) Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow);
    }

    /// <summary>Creates an empty folder for a scene, discarding any half-written attempt.</summary>
    public string Prepare(string key)
    {
        var directory = DirectoryFor(key);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Deletes the least recently used scenes until the cache fits its cap.</summary>
    public long Trim(string? keepKey = null)
    {
        if (!Directory.Exists(RootDirectory)) return 0;
        var scenes = new DirectoryInfo(RootDirectory).EnumerateDirectories()
            .Select(directory => (Directory: directory, Bytes: Size(directory)))
            .OrderByDescending(scene => scene.Directory.LastWriteTimeUtc)
            .ToList();
        var total = scenes.Sum(scene => scene.Bytes);
        var freed = 0L;
        for (var index = scenes.Count - 1; index >= 0 && total > MaximumBytes; index--)
        {
            var scene = scenes[index];
            if (keepKey is not null && scene.Directory.Name.Equals(keepKey, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                scene.Directory.Delete(recursive: true);
                total -= scene.Bytes;
                freed += scene.Bytes;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return freed;
    }

    public long CurrentBytes() =>
        Directory.Exists(RootDirectory) ? new DirectoryInfo(RootDirectory).EnumerateDirectories().Sum(Size) : 0;

    private static long Size(DirectoryInfo directory) =>
        directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
}
