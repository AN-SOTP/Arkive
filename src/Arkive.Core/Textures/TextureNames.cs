using System.Text.RegularExpressions;

namespace Arkive.Core.Textures;

/// <summary>
/// What a Lost Ark texture holds, read from its name (name_&lt;type&gt;_&lt;artist&gt;: mn_cnct_00a_d, bg_rad_kazeroth_floor14_em_ksr).
/// </summary>
public static class TextureNames
{
    // Kinds that are not colour: normal maps, packed masks, distortion, opacity. Recolouring them breaks shading.
    private static readonly HashSet<string> NonColour = new(StringComparer.OrdinalIgnoreCase)
    {
        "n", "nx", "na", "normal", "orm", "rm", "m", "mask", "mk", "s", "sp", "spec", "p", "dist", "h", "ao", "eff",
        "alpha", "a", "op", "opacity", "rf", "cm", "rgh", "mtl", "backup", "lut",
    };

    private static readonly HashSet<string> Glow = new(StringComparer.OrdinalIgnoreCase) { "e", "em", "emissive", "glow", "pan" };

    private static readonly HashSet<string> Colour = new(StringComparer.OrdinalIgnoreCase) { "d", "da", "diffuse", "basecolor", "albedo" };

    private static readonly Regex Word = new(@"^[a-z]+", RegexOptions.CultureInvariant);

    /// <summary>The texture's type word (d, em, orm…), the last word after the first that names a known kind; null when none does.</summary>
    public static string? Type(string objectName)
    {
        foreach (var word in objectName.ToLowerInvariant().Split('_').Skip(1).Reverse())
        {
            var type = Word.Match(word).Value;
            if (NonColour.Contains(type) || Glow.Contains(type) || Colour.Contains(type)) return type;
        }
        return null;
    }

    /// <summary>True for a texture that holds colour (diffuse, emissive, a panning glow), not shading data; unknown kinds count as colour.</summary>
    public static bool IsColour(string objectName) => Type(objectName) is not { } type || !NonColour.Contains(type);

    /// <summary>True for a glow texture (emissive or a panning glow): black means no light, so it is tinted, never lifted.</summary>
    public static bool IsGlow(string objectName) => Type(objectName) is { } type && Glow.Contains(type);

    /// <summary>
    /// The recolour a picture should take: for two-tone, a glow picture takes the highlight colour as a tint (its dark
    /// stays dark, its light becomes the highlight), and every other picture the two-tone repaint.
    /// </summary>
    public static TextureRecolour ForPicture(TextureRecolour recolour, string objectName) =>
        recolour.Style == TextureRecolourStyle.TwoTone && IsGlow(objectName)
            ? recolour with
            {
                Style = TextureRecolourStyle.Tint,
                Red = recolour.HighlightRed, Green = recolour.HighlightGreen, Blue = recolour.HighlightBlue,
            }
            : recolour;
}
