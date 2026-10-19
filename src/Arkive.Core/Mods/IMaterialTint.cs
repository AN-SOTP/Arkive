using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

/// <summary>
/// Tints many working copies at once with one colour choice: pictures, materials and particle effects alike. Each kind
/// takes the tint the way its own editor would (a picture's pixels, a material's colour settings, an effect's colour
/// tables), so a mixed selection reads as one recolour.
/// </summary>
public interface IMaterialTint
{
    Task<MaterialTintResult> TintAsync(MaterialTintRequest request, IProgress<MaterialTintProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <param name="Edits">The working copies to tint; kinds that cannot take a tint are reported as skipped.</param>
/// <param name="Tint">The colour, which colours change, strength and brightness, as the Texture Viewer sets them.</param>
public sealed record MaterialTintRequest(IReadOnlyList<ArkiveModLocalEdit> Edits, TextureRecolour Tint)
{
    /// <summary>0 to 1: how far to lift surfaces materials deliberately darken (dimmed stone) towards full brightness.</summary>
    public float LightenSurfaces { get; init; }

    /// <summary>Also tint the materials of selected effects (which hold the colour of effects without colour settings).</summary>
    public bool IncludeEffectMaterials { get; init; }

    /// <summary>
    /// True (the default) recolours pictures from the game's own pixels, replacing earlier recolours; false tints the
    /// working picture as it is now, on top of earlier changes. Materials and effects always start from the game's values.
    /// </summary>
    public bool FromOriginal { get; init; } = true;
}

public sealed record MaterialTintProgress(int Completed, int Total, string ObjectName);

/// <param name="Changed">Working copies whose colours changed.</param>
/// <param name="Unchanged">Working copies the tint selected nothing in (no colour in the chosen range, say).</param>
public sealed record MaterialTintResult(int Changed, int Unchanged, IReadOnlyList<MaterialTintSkip> Skipped)
{
    /// <summary>Effect materials added to the mod as new working copies so their colour could be tinted.</summary>
    public IReadOnlyList<ArkiveModLocalEdit> AddedEdits { get; init; } = [];
}

public sealed record MaterialTintSkip(string ObjectName, string Reason);
