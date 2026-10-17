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

    /// <summary>
    /// A key that changes whenever the source package file changes, or the folder its linked packages are searched in
    /// (when that is not the package's own folder, as for a clean backup copy).
    /// </summary>
    public static string Key(string packagePath, int exportIndex, string objectName, int schemaVersion, string? searchRoot = null)
    {
        var file = new FileInfo(packagePath);
        var identity = string.Join('|', Path.GetFullPath(packagePath).ToUpperInvariant(), file.Length,
            file.LastWriteTimeUtc.Ticks, exportIndex, objectName.ToUpperInvariant(), schemaVersion);
        if (searchRoot is not null && !string.Equals(Path.GetFullPath(searchRoot).TrimEnd('\\', '/'),
                Path.GetDirectoryName(Path.GetFullPath(packagePath)), StringComparison.OrdinalIgnoreCase))
            identity += "|" + Path.GetFullPath(searchRoot).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        var safe = new string(objectName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return $"{safe[..Math.Min(48, safe.Length)]}-{hash}";
    }

    public string DirectoryFor(string key) => Path.Combine(RootDirectory, key);

    public bool IsReady(string key) => File.Exists(Path.Combine(DirectoryFor(key), ReadyMarker));

    public void Touch(string key) => TouchDirectory(DirectoryFor(key));

    /// <summary>Creates an empty folder for a scene, discarding any half-written attempt.</summary>
    public string Prepare(string key)
    {
        var directory = DirectoryFor(key);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Folders shared by every scene. Each set inside them (one class's animations, one version's lighting or skies)
    /// is its own cache entry, so a large shared folder is never evicted as a whole like one old scene.
    /// </summary>
    public static readonly string[] SharedFolders = ["animations", "environments"];

    /// <summary>Work folders and half-written sets untouched this long are leftovers of a closed or killed Arkive.</summary>
    public static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(2);

    /// <summary>
    /// Deletes leftover work folders, then the least recently used scenes and shared sets until the cache fits its cap.
    /// </summary>
    public long Trim(string? keepKey = null)
    {
        if (!Directory.Exists(RootDirectory)) return 0;
        var freed = 0L;
        var entries = new List<(DirectoryInfo Directory, long Bytes)>();
        var root = new DirectoryInfo(RootDirectory);
        var children = root.EnumerateDirectories()
            .SelectMany(directory => SharedFolders.Contains(directory.Name, StringComparer.OrdinalIgnoreCase)
                ? directory.EnumerateDirectories()
                : [directory]);
        foreach (var directory in children)
        {
            if (IsWorkFolder(directory.Name))
            {
                // A running export keeps writing files somewhere inside its folder.
                var files = directory.EnumerateFiles("*", SearchOption.AllDirectories).ToArray();
                var touched = files.Select(file => file.LastWriteTimeUtc).Append(directory.LastWriteTimeUtc).Max();
                if (DateTime.UtcNow - touched > LeftoverAge && TryDelete(directory))
                    freed += files.Sum(file => file.Length);
                continue;
            }
            entries.Add((directory, Size(directory)));
        }

        var total = entries.Sum(entry => entry.Bytes);
        foreach (var entry in entries.OrderBy(entry => entry.Directory.LastWriteTimeUtc))
        {
            if (total <= MaximumBytes) break;
            if (keepKey is not null && entry.Directory.Name.Equals(keepKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryDelete(entry.Directory)) continue;
            total -= entry.Bytes;
            freed += entry.Bytes;
        }
        return freed;
    }

    /// <summary>Marks a scene or shared set as just used, so it is evicted last.</summary>
    public static void TouchDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public long CurrentBytes() =>
        Directory.Exists(RootDirectory) ? new DirectoryInfo(RootDirectory).EnumerateDirectories().Sum(Size) : 0;

    private static bool IsWorkFolder(string name) =>
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("export-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("skeleton-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("proportions-", StringComparison.OrdinalIgnoreCase);

    private static bool TryDelete(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static long Size(DirectoryInfo directory) =>
        directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
}
