using Arkive.Core.Meshes;
using Arkive.Core.Mods;

namespace Arkive.Core.Outfits;

/// <summary>A replaced piece of a swap as its item look sees it: the game mesh's own slots and the mesh the swap made.</summary>
public sealed record OutfitSwapLookPiece(string ObjectName, IReadOnlyList<string> GameSlots, OutfitSwapMesh Mesh);

/// <summary>
/// The item look changes an own-material swap needs. The game draws each piece with its item look's materials, so every
/// slot the swap gives another package's material gets that material in the replaced piece's look too, with the dyed
/// version the donor's own look pairs it with; slots the swap adds are added to the look after its own.
/// </summary>
public static class OutfitSwapLooks
{
    public static IReadOnlyList<OutfitLookEdit> Edits(OutfitLookArchive archive, IReadOnlyList<string> donors,
        IReadOnlyList<OutfitSwapLookPiece> pieces, ICollection<string> notes, CancellationToken cancellationToken = default)
    {
        // The materials the donors' looks (then the replaced pieces' own) name, by object, with the dyed version each pairs it with.
        var known = new Dictionary<string, (string Material, string Dye)>(StringComparer.OrdinalIgnoreCase);
        var targetLooks = pieces.ToDictionary(piece => piece.ObjectName, piece => archive.GameLooks(piece.ObjectName, cancellationToken),
            StringComparer.OrdinalIgnoreCase);
        foreach (var look in donors.SelectMany(donor => archive.GameLooks(donor, cancellationToken)).Concat(targetLooks.Values.SelectMany(looks => looks)))
            for (var slot = 0; slot < look.Materials.Count; slot++)
                if (look.Materials[slot].Length > 0)
                    known.TryAdd(OutfitLooks.ObjectName(look.Materials[slot]),
                        (look.Materials[slot], look.DyeMaterials[slot].Length > 0 ? look.DyeMaterials[slot] : look.Materials[slot]));
        (string Material, string Dye) For(SkeletalMeshMaterialReference reference) =>
            known.TryGetValue(reference.Material.ObjectName, out var found)
                ? found
                : (OutfitLooks.MaterialPath(reference.Material.ClassName, reference.Material.Path),
                    OutfitLooks.MaterialPath(reference.Material.ClassName, reference.Material.Path));

        var edits = new List<OutfitLookEdit>();
        foreach (var piece in pieces)
        {
            var mesh = piece.Mesh;
            if (mesh.MaterialReferences.Count == 0) continue;
            // The slots the compiler gives the mesh: the materials its triangles use, by name, as in the compiled mesh.
            var used = mesh.Sections.Select(section => section.Material).ToHashSet();
            var references = new Dictionary<string, SkeletalMeshMaterialReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var (index, reference) in mesh.MaterialReferences)
                if (used.Contains(index) && index < mesh.Materials.Count) references[mesh.Materials[index]] = reference;
            var (_, added, replaced) = SkeletalMeshMaterialReferences.Plan(piece.GameSlots,
                mesh.Materials.Where(references.ContainsKey).ToArray(), references);
            var looks = targetLooks[piece.ObjectName];
            if (looks.Count == 0)
            {
                notes.Add($"The game has no item look for {piece.ObjectName}, so it is drawn with the mesh's own materials.");
                continue;
            }
            foreach (var look in looks)
            {
                var name = Path.GetFileNameWithoutExtension(look.Entry.Split('/')[^1]);
                if (look.Materials.Count != piece.GameSlots.Count)
                {
                    notes.Add($"The item look {name} lists {look.Materials.Count} materials for the {piece.GameSlots.Count} slots of " +
                              $"{piece.ObjectName}, so Arkive left it as it is; the game may draw those slots with its own materials.");
                    continue;
                }
                foreach (var (slot, reference) in replaced.OrderBy(pair => pair.Key))
                {
                    var (material, dye) = For(reference);
                    if (material.Equals(look.Materials[slot], StringComparison.OrdinalIgnoreCase) &&
                        dye.Equals(look.DyeMaterials[slot], StringComparison.OrdinalIgnoreCase))
                        continue;
                    edits.Add(new OutfitLookEdit(look.Entry, slot, material, dye, look.Materials[slot], look.DyeMaterials[slot]));
                }
                for (var index = 0; index < added.Count; index++)
                {
                    var (material, dye) = For(added[index]!);
                    edits.Add(new OutfitLookEdit(look.Entry, piece.GameSlots.Count + index, material, dye, null, null));
                }
            }
        }
        return edits;
    }

    /// <summary>Writes <paramref name="edits"/> into the saved mod's file as its item look changes.</summary>
    public static async Task<ArkiveModLibraryEntry> AttachAsync(ArkiveModLibrary library, string modId, IReadOnlyList<OutfitLookEdit> edits,
        CancellationToken cancellationToken = default)
    {
        var manifest = await library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var mod = manifest.Find(modId) ?? throw new InvalidOperationException($"Mod {modId} is not in the library.");
        if (edits.Count == 0 && mod.Manifest.Looks is null) return mod;
        var written = await ArkiveModFileService.RewriteManifestAsync(library.GetModFilePath(mod),
            mod.Manifest with { Looks = edits.Count == 0 ? null : new ArkiveModLooks(edits) }, cancellationToken).ConfigureAwait(false);
        return await library.RegisterWrittenFileAsync(written, mod.Origin, mod.Enabled, mod.Priority, mod.Bindings,
            mod.AuthoredForPackageProjectId, cancellationToken).ConfigureAwait(false);
    }
}
