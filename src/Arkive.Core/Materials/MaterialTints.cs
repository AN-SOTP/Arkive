using Arkive.Core.Textures;

namespace Arkive.Core.Materials;

/// <summary>
/// One tint for many materials at once, chosen the way effect and picture tints are: a colour, which colours it
/// changes, how strongly, and how bright. Each changed colour setting keeps its brightness (its strongest channel)
/// and takes the tint's hue, so a glow stays as bright and a dark tint stays dark.
/// </summary>
/// <param name="Red">The tint's channels, 0 to 1; at least one above 0.</param>
/// <param name="Strength">0 keeps each colour, 1 replaces its hue fully.</param>
/// <param name="Brightness">Multiplies the changed colours: 1 unchanged, 2 twice as bright, 0.5 half.</param>
/// <param name="Selection">Which colours change: all of them, one range of hues, or the light, nearly white ones.</param>
/// <param name="Hue">For a colour range: its middle, in degrees (45 is gold).</param>
/// <param name="Range">For a colour range: how far either side of <paramref name="Hue"/> still changes fully.</param>
public sealed record MaterialTintRecipe(
    float Red, float Green, float Blue,
    float Strength = 1, float Brightness = 1,
    ColourSelection Selection = ColourSelection.All, float Hue = 45, float Range = 25)
{
    /// <summary>
    /// 0 to 1: how far to lift surfaces a material deliberately darkens (diffuse or layer brightness below 1) towards
    /// full brightness. Level materials often dim bright stone this way; 0 leaves them as they are.
    /// </summary>
    public float LightenSurfaces { get; init; }

    public bool IsValid => float.IsFinite(Red) && float.IsFinite(Green) && float.IsFinite(Blue) &&
                           Red >= 0 && Green >= 0 && Blue >= 0 && Math.Max(Red, Math.Max(Green, Blue)) > 0 &&
                           Strength is >= 0 and <= 1 && Brightness is >= 0 and <= 4 &&
                           float.IsFinite(Hue) && Range is >= 0 and <= 180 && LightenSurfaces is >= 0 and <= 1;
}

public static class MaterialTints
{
    /// <summary>
    /// The edited document: every colour setting the recipe selects takes its hue, and darkened surfaces are lifted as
    /// <see cref="MaterialTintRecipe.LightenSurfaces"/> asks. Settings that are not colours (tiling, panning, masks,
    /// directions) and occlusion colours are never touched. The original values stay recorded as the source.
    /// </summary>
    public static MaterialInstanceEditDocument Apply(MaterialInstanceEditDocument document, MaterialTintRecipe recipe) =>
        Apply(document, recipe, recipe);

    /// <summary>
    /// Two colours at once: surface colours (diffuse, base, overlay, terrain layers) take <paramref name="surfaces"/>,
    /// and glows (emissive, rim light, panning, edge and light colours) take <paramref name="glows"/>, the way a white
    /// armour with gold light is made. Darkened surfaces are lifted as the surface recipe asks.
    /// </summary>
    public static MaterialInstanceEditDocument Apply(MaterialInstanceEditDocument document, MaterialTintRecipe surfaces,
        MaterialTintRecipe glows)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(glows);
        if (!surfaces.IsValid || !glows.IsValid)
            throw new ArgumentException("A tint needs channels from 0 to 1 with some colour, a strength from 0 to 1, " +
                                        "a brightness from 0 to 4 and a lift from 0 to 1.", nameof(surfaces));
        return document with
        {
            Vectors = document.Vectors.Select(vector => Tint(vector, IsSurfaceColour(vector.Name) ? surfaces : glows)).ToArray(),
            Scalars = document.Scalars.Select(scalar => Lighten(scalar, surfaces.LightenSurfaces)).ToArray(),
        };
    }

    /// <summary>True for a colour that tints a surface (diffuse, base colour, overlay, terrain layer, sand, cloud, fog).</summary>
    public static bool IsSurfaceColour(string name)
    {
        var lower = name.ToLowerInvariant();
        if (new[] { "emissive", "rim", "panning", "edge", "trans", "light", "glow", "flash" }.Any(lower.Contains)) return false;
        return new[] { "diffuse", "base", "overlay", "layer", "sand", "cloud", "reflection", "specular", "albedo" }.Any(lower.Contains);
    }

    /// <summary>True for a vector setting that is a colour (its name says colour or tint, or it is an ambient light).</summary>
    public static bool IsColour(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower is "ambient_light" or "ambientlight") return true;
        return (lower.Contains("color") || lower.Contains("colour") || lower.Contains("tint")) &&
               !new[] { "occlusion", "mask", "tiling", "speed", "direction", "/" }.Any(lower.Contains);
    }

    /// <summary>The colour settings of a document, with their original colour, for previews.</summary>
    public static IReadOnlyList<MaterialVectorEdit> Colours(MaterialInstanceEditDocument document) =>
        document.Vectors.Where(vector => IsColour(vector.Name)).ToArray();

    private static MaterialVectorEdit Tint(MaterialVectorEdit vector, MaterialTintRecipe recipe)
    {
        if (!IsColour(vector.Name)) return vector;
        float red = vector.SourceRed, green = vector.SourceGreen, blue = vector.SourceBlue;
        var peak = Math.Max(red, Math.Max(green, blue));
        if (!(peak > 0) || Math.Min(red, Math.Min(green, blue)) < 0 || !float.IsFinite(peak)) return vector;
        var weight = ColourSelections.Weight(recipe.Selection, red, green, blue, recipe.Hue, recipe.Range);
        if (weight <= 0) return vector;
        var top = Math.Max(recipe.Red, Math.Max(recipe.Green, recipe.Blue));
        float Channel(float source, float tint)
        {
            var mixed = source + (peak * tint / top - source) * recipe.Strength;
            return source + (mixed * recipe.Brightness - source) * weight;
        }
        return vector with
        {
            Red = Channel(red, recipe.Red),
            Green = Channel(green, recipe.Green),
            Blue = Channel(blue, recipe.Blue),
        };
    }

    private static MaterialScalarEdit Lighten(MaterialScalarEdit scalar, float amount)
    {
        if (amount <= 0) return scalar;
        var name = scalar.Name.ToLowerInvariant();
        // Surface brightness only (diffuse_brightness, layer01_brightness, overlay_brightness); never glows or shadows.
        if (!name.EndsWith("brightness") || name.Contains("emissive") || name.Contains("occlusion") || name.Contains("rim") ||
            name.Contains("reflection") || name.Contains("light")) return scalar;
        if (scalar.SourceValue is <= 0 or >= 1 || !float.IsFinite(scalar.SourceValue)) return scalar;
        return scalar with { Value = scalar.SourceValue + (1 - scalar.SourceValue) * amount };
    }
}
