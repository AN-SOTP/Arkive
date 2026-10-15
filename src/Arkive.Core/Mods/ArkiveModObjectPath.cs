using Arkive.Core.Assets;

namespace Arkive.Core.Mods;

/// <summary>
/// Some packages hold two objects with the same name and class in different groups (pc_dl_av_214 has
/// <c>tex.pc_dl_av_214_head_orm</c> and a second <c>pc_dl_av_214_head_orm</c>). Such a target is identified by
/// its path inside the package, for example <c>tex.pc_dl_av_214_head_orm</c>. Targets with a unique name keep
/// no path, so their identity is unchanged.
/// </summary>
public static class ArkiveModObjectPath
{
    private const string SidecarSuffix = ".object-path";

    /// <summary>The export's path inside its package, without the package name.</summary>
    public static string Of(AssetCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var separator = entry.ObjectPath.IndexOf('.');
        return separator < 0 ? entry.ObjectPath : entry.ObjectPath[(separator + 1)..];
    }

    /// <summary>The path to record for <paramref name="entry"/>, or null when its name and class are unique.</summary>
    public static string? IfAmbiguous(IReadOnlyList<AssetCatalogEntry> assets, AssetCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(entry);
        return assets.Count(asset => SameName(asset, entry)) > 1 ? Of(entry) : null;
    }

    /// <summary>
    /// The exports <paramref name="objectPath"/> names. UModel writes material references relative to their
    /// package (Texture2D'tex.name'), even across packages, so the path must match exactly.
    /// </summary>
    public static AssetCatalogEntry[] Select(IEnumerable<AssetCatalogEntry> entries, string objectPath) =>
        entries.Where(entry => objectPath.Equals(Of(entry), StringComparison.OrdinalIgnoreCase)).ToArray();

    public static bool IsValid(string objectPath, string objectName) =>
        objectPath.Length <= 600 &&
        (objectPath.Equals(objectName, StringComparison.OrdinalIgnoreCase) ||
         objectPath.EndsWith("." + objectName, StringComparison.OrdinalIgnoreCase)) &&
        objectPath.Split('.').All(segment => segment.Length > 0 && segment.Trim() == segment);

    /// <summary>Reads the path recorded beside a working copy's manifest, if any.</summary>
    public static string? Read(string manifestPath)
    {
        try
        {
            var sidecar = manifestPath + SidecarSuffix;
            return File.Exists(sidecar) && File.ReadAllText(sidecar).Trim() is { Length: > 0 } path ? path : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(string manifestPath, string? objectPath)
    {
        if (objectPath is not null) File.WriteAllText(manifestPath + SidecarSuffix, objectPath);
    }

    private static bool SameName(AssetCatalogEntry left, AssetCatalogEntry right) =>
        left.ObjectName.Equals(right.ObjectName, StringComparison.OrdinalIgnoreCase) &&
        left.ClassName.Equals(right.ClassName, StringComparison.OrdinalIgnoreCase);
}
