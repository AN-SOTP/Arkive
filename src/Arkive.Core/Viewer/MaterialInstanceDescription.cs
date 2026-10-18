namespace Arkive.Core.Viewer;

/// <summary>
/// A material instance with its parameters flattened along the parent chain
/// (the leaf instance wins), as read from UModel's exported property text.
/// </summary>
public sealed record MaterialInstanceDescription(
    string Name,
    string ClassName,
    IReadOnlyList<string> ParentChain,
    string RootMaterial,
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, float[]> Vectors,
    IReadOnlyDictionary<string, string> Textures,
    IReadOnlyDictionary<string, bool> StaticSwitches,
    bool TwoSided,
    bool Masked,
    string BlendMode,
    float OpacityMaskClipValue,
    IReadOnlyList<string>? ReferencedTextures = null);

public static class MaterialInstanceDescriptionReader
{
    private const int MaximumParentDepth = 16;

    /// <summary>
    /// Reads every "*.props.txt" under a UModel export folder and resolves each
    /// material's parent chain against the other exported materials.
    /// </summary>
    public static IReadOnlyDictionary<string, MaterialInstanceDescription> ReadExportFolder(string exportRoot)
    {
        var raw = new Dictionary<string, (string ClassName, UmodelPropertyNode Tree)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(exportRoot, "*.props.txt", SearchOption.AllDirectories))
        {
            var className = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
            if (!className.Contains("Material", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(path)[..^".props.txt".Length];
            // A master instance often shares its base material's name, so the class is part of the key.
            raw[Key(className, name)] = (className, UmodelPropertyTree.Parse(File.ReadAllText(path)));
        }
        return raw.Keys.ToDictionary(key => key, key => Resolve(key, raw), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Lookup key for a material, e.g. <c>MaterialInstanceConstant'mn_hair_mi'</c>.</summary>
    public static string Key(string className, string name) => $"{className}'{name}'";

    /// <summary>Key for an object reference such as <c>MaterialInstanceConstant'mat.mn_hair_mi'</c>.</summary>
    public static string? KeyForReference(string? reference) =>
        UmodelPropertyTree.ParseObjectReference(reference) is { } parsed ? Key(parsed.ClassName, parsed.Name) : null;

    public static MaterialInstanceDescription Resolve(
        string key,
        IReadOnlyDictionary<string, (string ClassName, UmodelPropertyNode Tree)> materials)
    {
        var chain = new List<(string Name, string ClassName, UmodelPropertyNode Tree)>();
        var current = key;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (current is not null && visited.Add(current) && chain.Count < MaximumParentDepth &&
               materials.TryGetValue(current, out var entry))
        {
            chain.Add((current[(current.IndexOf('\'') + 1)..^1], entry.ClassName, entry.Tree));
            current = KeyForReference(entry.Tree.ChildValue("Parent"));
        }
        if (chain.Count == 0)
            return new MaterialInstanceDescription(key, "Unknown", [], key,
                new Dictionary<string, float>(), new Dictionary<string, float[]>(),
                new Dictionary<string, string>(), new Dictionary<string, bool>(), false, false, "BLEND_Opaque", 0.3333f);

        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var vectors = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var switches = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // Lost Ark's base materials list every parameter's default under Collected*Parameters. An instance that leaves a
        // texture parameter unset draws with that default; the cooked shader data holds no other record of it.
        foreach (var element in chain[^1].Tree.Elements("CollectedTextureParameters"))
            if (element.ChildValue("Name") is { } parameter &&
                UmodelPropertyTree.ParseObjectReference(element.ChildValue("Texture")) is { } texture)
                textures[parameter] = texture.Name;
        // Apply from the root towards the leaf so the most specific instance wins.
        for (var index = chain.Count - 1; index >= 0; index--)
        {
            var tree = chain[index].Tree;
            foreach (var element in tree.Elements("ScalarParameterValues"))
                if (element.ChildValue("ParameterName") is { } parameter &&
                    UmodelPropertyTree.TryParseFloat(element.ChildValue("ParameterValue"), out var value))
                    scalars[parameter] = value;
            foreach (var element in tree.Elements("VectorParameterValues"))
                if (element.ChildValue("ParameterName") is { } parameter &&
                    UmodelPropertyTree.ParseColor(element.Child("ParameterValue")) is { } color)
                    vectors[parameter] = color;
            foreach (var element in tree.Elements("TextureParameterValues"))
                if (element.ChildValue("ParameterName") is { } parameter &&
                    UmodelPropertyTree.ParseObjectReference(element.ChildValue("ParameterValue")) is { } texture)
                    textures[parameter] = texture.Name;
            if (tree.Child("StaticParameters") is { } statics)
                foreach (var element in statics.Elements("StaticSwitchParameters"))
                    if (element.ChildValue("ParameterName") is { } parameter)
                        switches[parameter] = string.Equals(element.ChildValue("Value"), "true", StringComparison.OrdinalIgnoreCase);
        }

        var root = chain[^1];
        var blendMode = root.Tree.ChildValue("BlendMode") ?? "BLEND_Opaque";
        var clip = UmodelPropertyTree.TryParseFloat(root.Tree.ChildValue("OpacityMaskClipValue"), out var parsedClip)
            ? parsedClip
            : 0.3333f;
        return new MaterialInstanceDescription(
            chain[0].Name,
            chain[0].ClassName,
            chain.Skip(1).Select(item => item.Name).ToArray(),
            root.Name,
            scalars,
            vectors,
            textures,
            switches,
            IsTrue(root.Tree.ChildValue("TwoSided")),
            IsTrue(root.Tree.ChildValue("bIsMasked")) || blendMode.Contains("Masked", StringComparison.OrdinalIgnoreCase),
            blendMode.Split(' ')[0],
            clip,
            root.Tree.Elements("ReferencedTextures")
                .Select(element => UmodelPropertyTree.ParseObjectReference(element.Value)?.Name ?? "")
                .ToArray());
    }

    private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
