namespace Arkive.Core.Mods;

/// <summary>
/// Groups working-copy records by the game package they change. The package projects list used to find each
/// package's records by comparing every record's full path with the package's (millions of comparisons on the
/// window's thread with large mods); one lookup per kind gives the same records in the same order.
/// </summary>
public static class SourcePackageGrouping
{
    /// <summary>The key a package path is filed under: its full path, compared ordinally ignoring case.</summary>
    public static string Key(string sourcePackagePath) => Path.GetFullPath(sourcePackagePath);

    /// <summary>
    /// The items under each package key, each group in the order of <paramref name="items"/>. A package with no items
    /// gives an empty group.
    /// </summary>
    public static ILookup<string, T> BySourcePackage<T>(IEnumerable<T> items, Func<T, string> sourcePackagePath) =>
        items.ToLookup(item => Key(sourcePackagePath(item)), StringComparer.OrdinalIgnoreCase);
}
