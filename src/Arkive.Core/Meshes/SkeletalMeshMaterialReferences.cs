using System.Text.Json;
using System.Text.Json.Nodes;
using Arkive.Core.Packages;

namespace Arkive.Core.Meshes;

/// <summary>
/// A material slot of an edited mesh drawn with another package's material. With <see cref="Replaces"/> the slot takes
/// the place of one of the game mesh's own slots (that slot's material is no longer used by the mesh); without it the
/// slot is added after the game mesh's slots.
/// </summary>
public sealed record SkeletalMeshMaterialReference(PackageObjectReference Material, string? Replaces);

/// <summary>
/// How a material reference travels in an edited GLB: the material's extras.arkiveMaterial holds the import chain of
/// the material (package first) and, for a slot that takes an existing slot's place, that slot's name.
/// </summary>
public static class SkeletalMeshMaterialReferences
{
    public const string ExtrasKey = "arkiveMaterial";

    public static JsonObject ToJson(SkeletalMeshMaterialReference reference)
    {
        var chain = new JsonArray();
        foreach (var link in reference.Material.Chain)
            chain.Add(new JsonObject { ["name"] = link.Name, ["class"] = link.ClassName, ["classPackage"] = link.ClassPackage });
        var json = new JsonObject { ["path"] = reference.Material.Path, ["chain"] = chain };
        if (reference.Replaces is not null) json["replaces"] = reference.Replaces;
        return json;
    }

    /// <summary>The reference a GLB material carries, or null for a plain material.</summary>
    public static SkeletalMeshMaterialReference? Read(JsonElement material)
    {
        if (material.ValueKind != JsonValueKind.Object || !material.TryGetProperty("extras", out var extras) ||
            extras.ValueKind != JsonValueKind.Object || !extras.TryGetProperty(ExtrasKey, out var value) ||
            value.ValueKind != JsonValueKind.Object)
            return null;
        if (!value.TryGetProperty("chain", out var chain) || chain.ValueKind != JsonValueKind.Array || chain.GetArrayLength() is < 2 or > 16)
            throw new InvalidDataException("A material's arkiveMaterial extras must list its package, groups and object.");
        var links = new List<PackageObjectLink>();
        foreach (var link in chain.EnumerateArray())
        {
            string Text(string key) => link.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String &&
                                       !string.IsNullOrWhiteSpace(item.GetString())
                ? item.GetString()!
                : throw new InvalidDataException($"A material's arkiveMaterial link has no {key}.");
            links.Add(new PackageObjectLink(Text("name"), Text("class"), Text("classPackage")));
        }
        if (!links[^1].ClassName.StartsWith("material", StringComparison.OrdinalIgnoreCase) ||
            links.Take(links.Count - 1).Any(link => !link.ClassName.Equals("package", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{string.Join('.', links.Select(link => link.Name))} is not a material reached through packages.");
        var replaces = value.TryGetProperty("replaces", out var slot) && slot.ValueKind == JsonValueKind.String ? slot.GetString() : null;
        return new SkeletalMeshMaterialReference(new PackageObjectReference(links), string.IsNullOrWhiteSpace(replaces) ? null : replaces);
    }

    /// <summary>The references in a GLB's materials array, by the slot names the semantic reader gives them.</summary>
    public static IReadOnlyDictionary<string, SkeletalMeshMaterialReference> ReadAll(JsonElement materials, IReadOnlyList<string> names)
    {
        var result = new Dictionary<string, SkeletalMeshMaterialReference>(StringComparer.OrdinalIgnoreCase);
        if (materials.ValueKind != JsonValueKind.Array) return result;
        var index = 0;
        foreach (var material in materials.EnumerateArray())
        {
            if (Read(material) is { } reference && index < names.Count) result[names[index]] = reference;
            index++;
        }
        return result;
    }

    /// <summary>The references an edited GLB's triangles are drawn with (materials no primitive uses are left out).</summary>
    public static IReadOnlyList<SkeletalMeshMaterialReference> ReadUsed(byte[] glb)
    {
        var json = Proportions.GlbContainer.Parse(glb).Json;
        using var document = JsonDocument.Parse(json.ToJsonString());
        var root = document.RootElement;
        if (!root.TryGetProperty("materials", out var materials) || materials.ValueKind != JsonValueKind.Array) return [];
        var used = new HashSet<int>();
        if (root.TryGetProperty("meshes", out var meshes) && meshes.ValueKind == JsonValueKind.Array)
            foreach (var mesh in meshes.EnumerateArray())
                if (mesh.TryGetProperty("primitives", out var primitives) && primitives.ValueKind == JsonValueKind.Array)
                    foreach (var primitive in primitives.EnumerateArray())
                        if (primitive.TryGetProperty("material", out var material) && material.TryGetInt32(out var index))
                            used.Add(index);
        var result = new List<SkeletalMeshMaterialReference>();
        var position = 0;
        foreach (var material in materials.EnumerateArray())
        {
            if (used.Contains(position) && Read(material) is { } reference) result.Add(reference);
            position++;
        }
        return result;
    }

    /// <summary>
    /// The native slot each material name compiles to and the material list that results: a game slot keeps its index,
    /// a replacing reference takes its slot's index, and added references follow the game's slots in GLB order.
    /// </summary>
    public static (IReadOnlyDictionary<string, int> Slots, IReadOnlyList<SkeletalMeshMaterialReference?> Added, IReadOnlyDictionary<int, SkeletalMeshMaterialReference> Replaced)
        Plan(IReadOnlyList<string> gameSlots, IReadOnlyList<string> glbNames, IReadOnlyDictionary<string, SkeletalMeshMaterialReference> references)
    {
        var slots = gameSlots.Select((name, index) => (name, index)).GroupBy(item => item.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        var replaced = new Dictionary<int, SkeletalMeshMaterialReference>();
        var added = new List<SkeletalMeshMaterialReference?>();
        foreach (var name in glbNames)
        {
            if (!references.TryGetValue(name, out var reference)) continue;
            // A reference on one of the game's own slot names takes that slot's place.
            var replaces = reference.Replaces ?? (slots.ContainsKey(name) ? name : null);
            if (replaces is not null)
            {
                if (!slots.TryGetValue(replaces, out var slot) || slot >= gameSlots.Count)
                    throw new InvalidDataException($"{name} takes the place of {replaces}, which is not one of the game mesh's material slots.");
                if (!replaced.TryAdd(slot, reference))
                    throw new InvalidDataException($"More than one material takes the place of {replaces}.");
                slots[name] = slot;
                continue;
            }
            slots[name] = gameSlots.Count + added.Count;
            added.Add(reference);
        }
        return (slots, added, replaced);
    }
}
