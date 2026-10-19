using Arkive.Core.Interface;

namespace Arkive.Core.Textures;

/// <summary>
/// A colour change for part of a picture. Like a particle effect's tint, each pixel keeps its brightness (its
/// strongest channel) and takes the colour's hue; <paramref name="Strength"/> blends that with the pixel's own colour,
/// and <paramref name="Brightness"/> then scales it (-1 black, 0 unchanged, +1 twice as bright).
/// </summary>
/// <param name="Red">The colour's channels, 0 to 1; at least one above 0.</param>
public sealed record TextureRecolour(float Red, float Green, float Blue, float Strength = 1, float Brightness = 0)
{
    /// <summary>Which pixels change: all of them (the default), one range of hues, or the light, nearly white ones.</summary>
    public TextureColourSelection Selection { get; init; } = TextureColourSelection.All;

    /// <summary>For <see cref="TextureColourSelection.ColourRange"/>: the middle of the hues that change, in degrees (45 is gold).</summary>
    public float RangeHue { get; init; } = 45;

    /// <summary>For <see cref="TextureColourSelection.ColourRange"/>: how far either side of <see cref="RangeHue"/> still changes fully, in degrees.</summary>
    public float RangeWidth { get; init; } = 25;

    public bool IsValid => float.IsFinite(Red) && float.IsFinite(Green) && float.IsFinite(Blue) &&
                           Red >= 0 && Green >= 0 && Blue >= 0 && Math.Max(Red, Math.Max(Green, Blue)) > 0 &&
                           Strength is >= 0 and <= 1 && Brightness is >= -1 and <= 1 &&
                           float.IsFinite(RangeHue) && RangeWidth is >= 0 and <= 180;

    // Colours fade out of a selection over this many degrees of hue past its width, so its edges do not look cut out.
    private const float HueFeather = 12;

    /// <summary>
    /// Recolours <paramref name="image"/> in place, inside <paramref name="region"/> only (the whole picture when
    /// null). Transparency is left as it is. A region also takes the one-pixel border atlases copy around each packed
    /// element, which the game blends into the element's edge.
    /// </summary>
    public void Apply(RgbaImage image, InterfaceRegion? region = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!IsValid) throw new ArgumentException("A colour needs channels from 0 to 1 with some colour, a strength from 0 to 1 and a brightness from -1 to 1.");
        var (left, top, right, bottom) = Bounds(image, region);
        if (region is not null)
            (left, top, right, bottom) = (Math.Max(0, left - 1), Math.Max(0, top - 1),
                Math.Min(image.Width, right + 1), Math.Min(image.Height, bottom + 1));
        var peak = Math.Max(Red, Math.Max(Green, Blue));
        var (red, green, blue) = (Red / peak, Green / peak, Blue / peak);
        var scale = 1 + Brightness;
        for (var y = top; y < bottom; y++)
        {
            var offset = (y * image.Width + left) * 4;
            for (var x = left; x < right; x++, offset += 4)
            {
                var pixels = image.Pixels;
                var weight = Weight(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
                if (weight <= 0) continue;
                var strongest = Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2]));
                pixels[offset] = Mix(pixels[offset], strongest * red, weight);
                pixels[offset + 1] = Mix(pixels[offset + 1], strongest * green, weight);
                pixels[offset + 2] = Mix(pixels[offset + 2], strongest * blue, weight);
            }
        }

        byte Mix(byte original, float tinted, float weight)
        {
            var changed = (original + (tinted - original) * Strength) * scale;
            return (byte)Math.Clamp(MathF.Round(original + (changed - original) * weight), 0, 255);
        }
    }

    /// <summary>How much a pixel belongs to the selection, from 0 (unchanged) to 1 (fully changed).</summary>
    public float Weight(byte red, byte green, byte blue)
    {
        if (Selection == TextureColourSelection.All) return 1;
        var strongest = Math.Max(red, Math.Max(green, blue)) / 255f;
        var weakest = Math.Min(red, Math.Min(green, blue)) / 255f;
        var saturation = strongest <= 0 ? 0 : (strongest - weakest) / strongest;
        if (Selection == TextureColourSelection.LightAreas)
            // Nearly white or pale grey: little colour, and bright enough to read as light.
            return Ramp(0.25f - saturation, 0, 0.12f) * Ramp(strongest, 0.5f, 0.75f);
        var hue = Hue(red, green, blue);
        var distance = Math.Abs((hue - RangeHue) % 360 + 540) % 360 - 180;
        // A hue only means something with some colour; greys and near-greys are never in a colour range.
        return Ramp(RangeWidth + HueFeather - Math.Abs(distance), 0, HueFeather) * Ramp(saturation, 0.08f, 0.2f);
    }

    /// <summary>A pixel's hue in degrees (0 red, 60 yellow, 120 green, 240 blue); 0 for greys.</summary>
    public static float Hue(byte red, byte green, byte blue)
    {
        float r = red, g = green, b = blue;
        var max = Math.Max(r, Math.Max(g, b));
        var delta = max - Math.Min(r, Math.Min(g, b));
        if (delta <= 0) return 0;
        var hue = max == r ? (g - b) / delta % 6 : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        return (hue * 60 + 360) % 360;
    }

    private static float Ramp(float value, float from, float to) => Math.Clamp((value - from) / (to - from), 0, 1);

    /// <summary>The pixel rectangle a region covers on an image of this size (left, top, right, bottom; right and bottom exclusive).</summary>
    public static (int Left, int Top, int Right, int Bottom) Bounds(RgbaImage image, InterfaceRegion? region)
    {
        if (region is null) return (0, 0, image.Width, image.Height);
        var left = Math.Clamp((int)Math.Floor(region.Left * image.Width), 0, image.Width);
        var top = Math.Clamp((int)Math.Floor(region.Top * image.Height), 0, image.Height);
        var right = Math.Clamp((int)Math.Ceiling((region.Left + region.Width) * image.Width), left, image.Width);
        var bottom = Math.Clamp((int)Math.Ceiling((region.Top + region.Height) * image.Height), top, image.Height);
        return (left, top, right, bottom);
    }
}

/// <summary>Which pixels a <see cref="TextureRecolour"/> changes.</summary>
public enum TextureColourSelection
{
    /// <summary>Every pixel.</summary>
    All,
    /// <summary>Pixels whose hue is near <see cref="TextureRecolour.RangeHue"/> (gold, say), keeping greys and other colours.</summary>
    ColourRange,
    /// <summary>Light, nearly white pixels (white stone, pale highlights), keeping coloured and dark ones.</summary>
    LightAreas,
}
