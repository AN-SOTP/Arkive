using System.Text;
using System.Text.RegularExpressions;

namespace Arkive.Core.Discovery;

/// <summary>
/// How the library index stands against the game's package folder: packages the index has never listed, packages
/// whose file changed since it was listed (a game update, or a mod put in or taken out), and listed packages the game
/// no longer has. Searches miss the first, show old contents for the second and still offer the third.
/// </summary>
public sealed record PackageIndexFreshness(int PackageCount, int NewPackages, int ChangedPackages, int RemovedPackages)
{
    /// <summary>True when a Build/update would list anything again.</summary>
    public bool NeedsUpdate => NewPackages + ChangedPackages > 0;

    public bool IsCurrent => NewPackages + ChangedPackages + RemovedPackages == 0;
}

public sealed partial class PackageIndexStore
{
    /// <summary>
    /// Compares the game's package folder with the index by file size and date alone: a folder listing and the index
    /// documents' names, well under a second for the whole game. Nothing is read from a package and nothing is written.
    /// </summary>
    public PackageIndexFreshness GetFreshness(string rootPath)
    {
        var root = new DirectoryInfo(Path.GetFullPath(rootPath));
        if (!root.Exists || !Directory.Exists(_cacheDirectory)) return new PackageIndexFreshness(0, 0, 0, 0);
        var listed = new Dictionary<string, (string Path, long Length, long Ticks)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "*.json.gz"))
        {
            var parts = Path.GetFileName(path).Split('.');
            if (parts.Length == 7 &&
                long.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var length) &&
                long.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var ticks))
                listed[parts[0]] = (path, length, ticks);
        }

        var packages = 0;
        var added = 0;
        var changed = 0;
        foreach (var file in root.EnumerateFiles("*.upk", SearchOption.AllDirectories))
        {
            packages++;
            if (!listed.Remove(GetCachePrefix(file.FullName), out var entry)) added++;
            else if (entry.Length != file.Length || entry.Ticks != file.LastWriteTimeUtc.Ticks) changed++;
        }
        // What is left was listed from a package that is gone, or from another game folder (left alone).
        var removed = listed.Values.Count(entry => IsUnder(ReadSourcePath(entry.Path), root.FullName));
        return new PackageIndexFreshness(packages, added, changed, removed);
    }

    /// <summary>
    /// Deletes the index documents of packages under <paramref name="fullRoot"/> that the game no longer has, so
    /// searches stop offering them. Documents listed from another game folder are left alone.
    /// </summary>
    private int RemoveDocumentsOfMissingPackages(string fullRoot, IReadOnlyCollection<string> packages)
    {
        var current = packages.Select(GetCachePrefix).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "*.json.gz"))
        {
            var parts = Path.GetFileName(path).Split('.');
            if (parts.Length != 7 || current.Contains(parts[0])) continue;
            if (!IsUnder(ReadSourcePath(path), fullRoot)) continue;
            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Still offered until the next update; nothing else depends on it.
            }
        }
        if (removed > 0) InvalidateSearchRouting();
        return removed;
    }

    private static bool IsUnder(string? path, string root) =>
        path is not null && path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    // An index document starts with the package it was listed from: {"SourcePath":"D:\\...\\X.upk",...
    private static string? ReadSourcePath(string cacheFile)
    {
        try
        {
            using var stream = OpenCacheRead(cacheFile);
            var head = new byte[1024];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            var match = SourcePathPattern().Match(Encoding.UTF8.GetString(head, 0, read));
            return match.Success ? Regex.Unescape(match.Groups["path"].Value) : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex("^\\s*\\{\\s*\"SourcePath\"\\s*:\\s*\"(?<path>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex SourcePathPattern();
}
