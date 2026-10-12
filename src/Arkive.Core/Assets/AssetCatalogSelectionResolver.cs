namespace Arkive.Core.Assets;

/// <summary>
/// Re-resolves cached UI selections against the package's current native export
/// table. Variable-size package reconstruction may retain the old serialized
/// bytes while retargeting the named export to a new logical range, so cached
/// index/range coordinates must not be treated as current authority.
/// </summary>
public static class AssetCatalogSelectionResolver
{
    public static AssetCatalogEntry ResolveSkeletalMesh(
        IReadOnlyList<AssetCatalogEntry> assets,
        string objectName,
        int preferredExportIndex,
        long preferredSerialOffset,
        long preferredSerialSize)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        var matches = assets.Where(asset =>
                asset.Kind == AssetKind.SkeletalMesh &&
                asset.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
                asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
            throw new InvalidDataException(
                $"The current package export table does not contain SkeletalMesh {objectName}.");
        if (matches.Length == 1) return matches[0];

        var exactRange = matches.Where(asset =>
            asset.SerialOffset == preferredSerialOffset &&
            asset.SerialSize == preferredSerialSize).ToArray();
        if (exactRange.Length == 1) return exactRange[0];
        var exactIndex = matches.Where(asset => asset.ExportIndex == preferredExportIndex).ToArray();
        if (exactIndex.Length == 1) return exactIndex[0];
        throw new InvalidDataException(
            $"The current package export table contains {matches.Length:N0} ambiguous SkeletalMesh exports named {objectName}.");
    }
}
