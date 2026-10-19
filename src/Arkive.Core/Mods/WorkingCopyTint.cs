using Arkive.Core.Materials;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

/// <summary>
/// A plain <see cref="IMaterialTint"/>: recolours texture working copies saved as PNG, the way the Texture Viewer does,
/// and the colour parameters of material working copies. Other working copies are skipped with a reason.
/// </summary>
public sealed class WorkingCopyTint : IMaterialTint
{
    public async Task<MaterialTintResult> TintAsync(
        MaterialTintRequest request,
        IProgress<MaterialTintProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Tint.IsValid) throw new ArgumentException("The colour change is not valid.", nameof(request));
        int changed = 0, unchanged = 0, completed = 0;
        var skipped = new List<MaterialTintSkip>();
        foreach (var edit in request.Edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new MaterialTintProgress(completed, request.Edits.Count, edit.ObjectName));
            var outcome = edit.Entry switch
            {
                GuidedTextureModProjectEntry texture => await TintTextureAsync(texture, request.Tint, cancellationToken).ConfigureAwait(false),
                MaterialInstanceEditEntry material => await TintMaterialAsync(material, request.Tint, cancellationToken).ConfigureAwait(false),
                _ => "Only textures and materials are recoloured here; open this one in its own editor.",
            };
            if (outcome is null) changed++;
            else if (outcome.Length == 0) unchanged++;
            else skipped.Add(new MaterialTintSkip(edit.ObjectName, outcome));
            completed++;
        }
        progress?.Report(new MaterialTintProgress(completed, request.Edits.Count, string.Empty));
        return new MaterialTintResult(changed, unchanged, skipped);
    }

    // Null when changed, empty when nothing in it was selected, otherwise why it was skipped.
    private static async Task<string?> TintTextureAsync(GuidedTextureModProjectEntry texture, TextureRecolour tint,
        CancellationToken cancellationToken)
    {
        var path = texture.Manifest.EditableImagePath;
        if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            return "Its working file is a DDS; recolour it in your image editor.";
        var picture = (await TextureEditableImageService.LoadAsync(path, cancellationToken).ConfigureAwait(false)).Image;
        var before = picture.DeepCopy();
        tint.Apply(picture);
        if (picture.Pixels.AsSpan().SequenceEqual(before.Pixels)) return string.Empty;
        var temporary = path + ".saving.png";
        File.Delete(temporary);
        await PngRgbaCodec.WriteAsync(temporary, picture, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
        return null;
    }

    private static async Task<string?> TintMaterialAsync(MaterialInstanceEditEntry material, TextureRecolour tint,
        CancellationToken cancellationToken)
    {
        var any = false;
        var vectors = material.Document.Vectors.Select(vector =>
        {
            if (!IsColour(vector.Name)) return vector;
            var (red, green, blue) = Recolour(tint, vector.Red, vector.Green, vector.Blue);
            if (red == vector.Red && green == vector.Green && blue == vector.Blue) return vector;
            any = true;
            return vector with { Red = red, Green = green, Blue = blue };
        }).ToArray();
        if (material.Document.Vectors.Count == 0 || !material.Document.Vectors.Any(vector => IsColour(vector.Name)))
            return "It has no colour parameters; its look comes from its textures.";
        if (!any) return string.Empty;
        await MaterialInstanceWorkspaceStore.SaveJsonAtomicAsync(material.DocumentPath,
            material.Document with { Vectors = vectors, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// A colour parameter takes the tint the way a pixel does: it keeps its strength (its strongest channel, which
    /// can be above 1 for glowing colours) and takes the new colour's hue.
    /// </summary>
    public static (float Red, float Green, float Blue) Recolour(TextureRecolour tint, float red, float green, float blue)
    {
        var strongest = Math.Max(red, Math.Max(green, blue));
        if (strongest <= 0) return (red, green, blue);
        var weight = ColourSelections.Weight(tint.Selection, red, green, blue, tint.RangeHue, tint.RangeWidth,
            Math.Clamp(strongest, 0, 1));
        if (weight <= 0) return (red, green, blue);
        var peak = Math.Max(tint.Red, Math.Max(tint.Green, tint.Blue));
        var scale = 1 + tint.Brightness;
        return (Mix(red, strongest * tint.Red / peak), Mix(green, strongest * tint.Green / peak), Mix(blue, strongest * tint.Blue / peak));

        float Mix(float original, float tinted) =>
            original + ((original + (tinted - original) * tint.Strength) * scale - original) * weight;
    }

    // Masks, tiling and directions are vectors too; only colours take a tint. Words, not letters: dirt_color is a colour.
    private static readonly string[] NotColours = ["mask", "uv", "tiling", "tile", "offset", "pan", "panner", "panning", "dir",
        "direction", "scale", "position", "pos", "speed", "size", "rotation"];

    private static bool IsColour(string name) =>
        !name.ToLowerInvariant().Split('_', ' ', '-').Any(word =>
            NotColours.Contains(word) || word.StartsWith("layermask", StringComparison.Ordinal) ||
            word.EndsWith("mask", StringComparison.Ordinal) || word.StartsWith("uv", StringComparison.Ordinal));
}
