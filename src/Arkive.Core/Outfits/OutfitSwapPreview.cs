using System.Text.Json;
using System.Text.Json.Nodes;
using Arkive.Core.Viewer;

namespace Arkive.Core.Outfits;

/// <summary>Shows a swap that keeps the outfit's own materials with those materials, as the donor's viewer scenes draw them.</summary>
public static class OutfitSwapPreview
{
    /// <summary>
    /// The replaced piece's scene with a material added for each of the swapped mesh's other-package materials, copied
    /// from the donor scene that has it (its files linked from the donor's folder). The viewer finds them by the GLB's
    /// material names.
    /// </summary>
    public static ModelViewerSceneResult WithDonorMaterials(ModelViewerSceneResult target, OutfitSwapMesh mesh,
        IReadOnlyList<ModelViewerSceneResult> donors)
    {
        if (mesh.MaterialReferences.Count == 0) return target;
        var materials = target.Scene.Materials.ToList();
        foreach (var (index, reference) in mesh.MaterialReferences.OrderBy(pair => pair.Key))
        {
            var found = donors.SelectMany(donor => donor.Scene.Materials.Select(material => (Donor: donor, Material: material)))
                .FirstOrDefault(item => item.Material.Name.Equals(reference.Material.ObjectName, StringComparison.OrdinalIgnoreCase));
            if (found.Material is null) continue;
            var node = JsonSerializer.SerializeToNode(found.Material, ModelViewerSceneService.JsonOptions)!;
            var folder = Path.GetRelativePath(target.Directory, found.Donor.Directory).Replace('\\', '/');
            Relink(node, found.Donor.Directory, folder);
            var copy = node.Deserialize<ModelViewerMaterial>(ModelViewerSceneService.JsonOptions)!;
            materials.Add(copy with { Slot = materials.Count, Reference = mesh.Materials[index] });
        }
        return target with { Scene = target.Scene with { Materials = materials } };
    }

    /// <summary>Points every string that names a file in the donor's folder at it from the target's folder.</summary>
    private static void Relink(JsonNode? node, string directory, string folder)
    {
        switch (node)
        {
            case JsonObject entry:
                foreach (var (name, child) in entry.ToArray())
                    if (child is JsonValue value && value.TryGetValue<string>(out var text) && IsFile(directory, text))
                        entry[name] = $"{folder}/{text}";
                    else Relink(child, directory, folder);
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                    if (array[index] is JsonValue value && value.TryGetValue<string>(out var text) && IsFile(directory, text))
                        array[index] = $"{folder}/{text}";
                    else Relink(array[index], directory, folder);
                break;
        }
    }

    private static bool IsFile(string directory, string text)
    {
        if (text.Length is 0 or > 400 || text.Contains('\n') || Path.IsPathRooted(text) || text.Contains("://")) return false;
        try { return File.Exists(Path.Combine(directory, Uri.UnescapeDataString(text))); }
        catch (ArgumentException) { return false; }
    }
}
