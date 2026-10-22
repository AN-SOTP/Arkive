using Arkive.Core.Archives;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.Mods;
using Arkive.Core.Proportions;
using Arkive.Core.Textures;
using Arkive.Core.Viewer;

namespace Arkive.Core.Outfits;

/// <summary>
/// Writes a swap into a mod's working copies: each replaced piece's working GLB gets the new geometry (reshaped with the
/// player's Simple upscale settings first, when given), each repainted picture its new image with its cut-out mask, and
/// each material its new colour values with a fresh verified candidate. Saving the mod then checks and builds everything
/// the usual way.
/// </summary>
public sealed class OutfitSwapModWriter(ArkiveModEditWorkspaces edits, IChunkProtectionProvider protection, string materialCandidateRoot)
{
    private readonly MaterialInstanceEditService _materials = new(protection);

    public async Task WriteMeshAsync(ArkiveModLocalEdit edit, OutfitSwapMesh mesh, BodyProportionModSettings? upscale,
        CancellationToken cancellationToken)
    {
        if (edit.Entry is not SkeletalMeshEditWorkspaceEntry entry) throw new InvalidOperationException($"{edit.ObjectName} is not a skeletal mesh copy.");
        var working = await File.ReadAllBytesAsync(entry.EditedGlbPath, cancellationToken).ConfigureAwait(false);
        var shaped = Reshape(working, mesh, upscale, edit.ObjectName);
        var temporary = entry.EditedGlbPath + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporary, OutfitSwapMesh.WriteInto(working, shaped), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, entry.EditedGlbPath, overwrite: true);
    }

    /// <summary>The swapped piece reshaped by Simple upscale sliders, as the proportion editor would reshape a game piece.</summary>
    public static OutfitSwapMesh Reshape(byte[] workingGlb, OutfitSwapMesh mesh, BodyProportionModSettings? upscale, string objectName)
    {
        if (upscale is null || BodyProportionEngine.IsNeutral(upscale.Settings)) return mesh;
        var shared = OutfitSwapMesh.WriteInto(workingGlb, mesh, sharedVertices: true);
        var model = BodyProportionEngine.Build(shared, upscale.Follow, upscale.Profile, objectName);
        var reshaped = OutfitSwapMesh.Read(BodyProportionEngine.Apply(shared, model, upscale.Settings));
        return new OutfitSwapMesh
        {
            Positions = reshaped.Positions, Normals = reshaped.Normals, Tangents = reshaped.Tangents, Uv0 = mesh.Uv0, Uv1 = mesh.Uv1,
            Colours = mesh.Colours, Joints = mesh.Joints, Weights = mesh.Weights, Sections = mesh.Sections, Materials = mesh.Materials,
            BoneNames = mesh.BoneNames, BonePositions = mesh.BonePositions, BoneParents = mesh.BoneParents,
            MaterialReferences = mesh.MaterialReferences,
        };
    }

    public async Task WritePictureAsync(ArkiveModLocalEdit edit, string picture, CancellationToken cancellationToken)
    {
        if (edit.Entry is not GuidedTextureModProjectEntry texture) throw new InvalidOperationException($"{edit.ObjectName} is not a picture copy.");
        var target = texture.Manifest.EditableImagePath;
        if (!Path.GetExtension(target).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{edit.ObjectName}'s working file is not a PNG.");
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        File.Copy(picture, temporary, overwrite: true);
        File.Move(temporary, target, overwrite: true);
        // The swapped outfit's own cut-out mask replaces the old outfit's.
        await edits.Textures.SetAlphaPolicyAsync(texture.ManifestPath, TextureImportAlphaPolicy.UseEdited, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteMaterialAsync(ArkiveModLocalEdit edit, ModelViewerMaterialEdit values, CancellationToken cancellationToken)
    {
        if (edit.Entry is not MaterialInstanceEditEntry material) throw new InvalidOperationException($"{edit.ObjectName} is not a material copy.");
        var document = material.Document with
        {
            Vectors = material.Document.Vectors.Select(vector => values.Vectors.TryGetValue(vector.Name, out var value) && value.Length >= 4
                ? vector with { Red = value[0], Green = value[1], Blue = value[2], Alpha = value[3] }
                : vector).ToArray(),
            Scalars = material.Document.Scalars.Select(scalar => values.Scalars.TryGetValue(scalar.Name, out var value)
                ? scalar with { Value = value } : scalar).ToArray(),
        };
        if (!document.Vectors.Any(vector => vector.Changed) && !document.Scalars.Any(scalar => scalar.Changed)) return;
        var saved = await edits.MaterialEdits.SaveAsync(material with { Document = document }, cancellationToken).ConfigureAwait(false);
        var candidate = await _materials.BuildCandidateAsync(saved.DocumentPath, materialCandidateRoot, cancellationToken).ConfigureAwait(false);
        await edits.MaterialEdits.RecordCandidateAsync(saved, candidate, cancellationToken).ConfigureAwait(false);
    }
}
