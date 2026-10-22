using Arkive.Core.Assets;

namespace Arkive.Core.Packages;

/// <summary>One object on the way to another package's object: its name as the game shows it and its class.</summary>
public sealed record PackageObjectLink(string Name, string ClassName, string ClassPackage);

/// <summary>
/// An object in another package, written the way an import names it: the package first, then each group, then the
/// object, every link with its class. A package that references the object adds these links to its import table.
/// </summary>
public sealed record PackageObjectReference(IReadOnlyList<PackageObjectLink> Chain)
{
    public string PackageName => Chain[0].Name;

    public string ObjectName => Chain[^1].Name;

    public string ClassName => Chain[^1].ClassName;

    /// <summary>The object's path, package first (for example pc_msm_av_137.mat.pc_msm_av_137_upper_mi).</summary>
    public string Path => string.Join('.', Chain.Select(link => link.Name));

    /// <summary>
    /// The object a package index points at, as another package would import it: an import keeps its own chain, and an
    /// export of the package itself is reached through the package's alias.
    /// </summary>
    public static PackageObjectReference? FromCatalog(AssetCatalog catalog, string packageAlias, int packageIndex)
    {
        var links = new List<PackageObjectLink>();
        var index = packageIndex;
        for (var guard = 0; index != 0; guard++)
        {
            if (guard > 64) return null;
            if (index < 0)
            {
                if (-index > catalog.Imports.Count) return null;
                var import = catalog.Imports[-index - 1];
                links.Add(new PackageObjectLink(import.ObjectName.Resolve(catalog.Names), import.ClassName.Resolve(catalog.Names),
                    import.ClassPackage.Resolve(catalog.Names)));
                index = import.OuterIndex;
                continue;
            }
            if (index > catalog.Exports.Count) return null;
            var export = catalog.Exports[index - 1];
            var className = ExportClass(catalog, export.ClassIndex);
            if (className is null) return null;
            links.Add(new PackageObjectLink(export.ObjectName.Resolve(catalog.Names), className.Value.Name, className.Value.Package));
            index = export.OuterIndex;
            if (index == 0) links.Add(new PackageObjectLink(packageAlias.ToLowerInvariant(), "package", "core"));
        }
        if (links.Count < 2) return null;
        links.Reverse();
        return new PackageObjectReference(links);
    }

    /// <summary>The import index of this object in a package, or null when the package does not import it.</summary>
    public int? FindImport(AssetCatalog catalog)
    {
        var outer = 0;
        foreach (var link in Chain)
        {
            var found = 0;
            for (var i = 0; i < catalog.Imports.Count && found == 0; i++)
                if (Matches(catalog, catalog.Imports[i], link, outer)) found = -(i + 1);
            if (found == 0) return null;
            outer = found;
        }
        return outer;
    }

    internal static bool Matches(AssetCatalog catalog, UnrealObjectImport import, PackageObjectLink link, int outer) =>
        import.OuterIndex == outer &&
        import.ObjectName.Resolve(catalog.Names).Equals(link.Name, StringComparison.OrdinalIgnoreCase) &&
        import.ClassName.Resolve(catalog.Names).Equals(link.ClassName, StringComparison.OrdinalIgnoreCase) &&
        import.ClassPackage.Resolve(catalog.Names).Equals(link.ClassPackage, StringComparison.OrdinalIgnoreCase);

    private static (string Name, string Package)? ExportClass(AssetCatalog catalog, int classIndex)
    {
        if (classIndex == 0) return ("class", "core");
        if (classIndex > 0) return null; // classes defined in an outfit package do not occur
        if (-classIndex > catalog.Imports.Count) return null;
        var import = catalog.Imports[-classIndex - 1];
        if (import.OuterIndex >= 0 || -import.OuterIndex > catalog.Imports.Count) return null;
        var package = catalog.Imports[-import.OuterIndex - 1];
        return (import.ObjectName.Resolve(catalog.Names), package.ObjectName.Resolve(catalog.Names));
    }

    /// <summary>
    /// A name as the game stores it: a trailing _number (without a leading zero) is kept apart, plus one, so
    /// pc_msm_av_137 is the name pc_msm_av with number 138.
    /// </summary>
    public static (string Base, int Number) SplitName(string name)
    {
        var digits = 0;
        while (digits < name.Length && char.IsAsciiDigit(name[name.Length - 1 - digits])) digits++;
        var underscore = name.Length - digits - 1;
        if (digits == 0 || digits > 9 || underscore <= 0 || name[underscore] != '_' ||
            (digits > 1 && name[underscore + 1] == '0'))
            return (name, 0);
        return (name[..underscore], int.Parse(name.AsSpan(underscore + 1), System.Globalization.CultureInfo.InvariantCulture) + 1);
    }
}
