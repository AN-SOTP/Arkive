using Arkive.Core.Assets;
using Arkive.Core.Packages;

namespace Arkive.Core.Outfits;

/// <summary>
/// The game materials a swap draws with when the outfit keeps its own materials: each donor piece's materials (by slot)
/// and the replaced body's own skin materials, as imports the replaced outfit's package can add.
/// </summary>
public sealed record OutfitSwapMaterialSources(
    IReadOnlyList<IReadOnlyList<PackageObjectReference?>> Donors,
    PackageObjectReference? UpperSkin,
    PackageObjectReference? LowerSkin)
{
    /// <summary>
    /// The outfit keeps its own materials. When false, a repainted swap only uses the body's skin materials, for a
    /// replaced piece that has no skin slot of its own.
    /// </summary>
    public bool KeepOwn { get; init; } = true;

    /// <summary>The material a piece's slot names (MaterialInstanceConstant'mat.name' as the GLB has it), read from its package.</summary>
    public static PackageObjectReference? Resolve(AssetCatalog catalog, string packageAlias, string slotName)
    {
        var text = slotName.Trim();
        // Blender's numbering of repeated slots (name.001) is not part of the material.
        if (text.Length > 4 && text[^4] == '.' && text[^3..].All(char.IsAsciiDigit)) text = text[..^4];
        var quote = text.IndexOf('\'');
        if (quote <= 0 || !text.EndsWith('\'')) return null;
        var className = text[..quote];
        var path = text[(quote + 1)..^1].Split('.');
        var objectName = path[^1];
        bool Named(UnrealNameReference name) => name.Resolve(catalog.Names).Equals(objectName, StringComparison.OrdinalIgnoreCase);
        var export = catalog.Assets.Where(asset => asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase) &&
                                                   asset.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(asset => path.Length < 2 || asset.ObjectPath.Contains($".{path[^2]}.", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (export is not null) return PackageObjectReference.FromCatalog(catalog, packageAlias, export.ExportIndex);
        for (var i = 0; i < catalog.Imports.Count; i++)
            if (Named(catalog.Imports[i].ObjectName) &&
                catalog.Imports[i].ClassName.Resolve(catalog.Names).Equals(className, StringComparison.OrdinalIgnoreCase))
                return PackageObjectReference.FromCatalog(catalog, packageAlias, -(i + 1));
        return null;
    }

    /// <summary>
    /// Reads the donor pieces' materials from their packages, and the replaced body's skin materials from its base body.
    /// </summary>
    public static async Task<OutfitSwapMaterialSources> ReadAsync(
        OutfitSwapPackageLibrary library,
        IReadOnlyList<OutfitSwapPiece> donors,
        OutfitSwapBodyInfo? targetBody,
        CancellationToken cancellationToken)
    {
        var result = new List<IReadOnlyList<PackageObjectReference?>>();
        foreach (var donor in donors)
            result.Add(await PieceAsync(donor.ObjectName, donor.Mesh).ConfigureAwait(false));
        PackageObjectReference? upper = null, lower = null;
        foreach (var name in targetBody?.BaseBody ?? [])
        {
            if (await library.LoadAsync(name, cancellationToken).ConfigureAwait(false) is not { } mesh) continue;
            var references = await PieceAsync(name, mesh).ConfigureAwait(false);
            for (var slot = 0; slot < mesh.Materials.Count; slot++)
            {
                if (references[slot] is not { } reference || !OutfitSwapBodies.IsSkin(mesh.Materials[slot])) continue;
                if (OutfitSwapBodies.IsLowerSkin(mesh.Materials[slot])) lower ??= reference;
                else upper ??= reference;
            }
        }
        return new OutfitSwapMaterialSources(result, upper, lower);

        async Task<IReadOnlyList<PackageObjectReference?>> PieceAsync(string objectName, OutfitSwapMesh mesh)
        {
            var source = await library.LocateAsync(objectName, cancellationToken).ConfigureAwait(false) ??
                         throw new InvalidDataException($"{objectName} is not in the library.");
            var catalog = await Task.Run(() => library.Catalog(source.CleanPackagePath), cancellationToken).ConfigureAwait(false);
            return mesh.Materials.Select(slot => Resolve(catalog, source.PackageAlias, slot)).ToArray();
        }
    }
}
