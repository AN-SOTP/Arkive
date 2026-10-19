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
    public ColourSelection Selection { get; init; } = ColourSelection.All;

    /// <summary>For <see cref="ColourSelection.ColourRange"/>: the middle of the hues that change, in degrees (45 is gold).</summary>
    public float RangeHue { get; init; } = 45;

    /// <summary>For <see cref="ColourSelection.ColourRange"/>: how far either side of <see cref="RangeHue"/> still changes fully, in degrees.</summary>
    public float RangeWidth { get; init; } = 25;

    /// <summary>
    /// How changed pixels are coloured: <see cref="TextureRecolourStyle.Tint"/> keeps each pixel's brightness and takes
    /// the colour's hue; <see cref="TextureRecolourStyle.TwoTone"/> repaints the part in the colour (dark details stay
    /// darker, as shading) and puts the highlight colour on its lightest, raised details.
    /// </summary>
    public TextureRecolourStyle Style { get; init; } = TextureRecolourStyle.Tint;

    /// <summary>For two-tone: the colour of the lightest details (gold trim on white armour, say), 0 to 1 per channel.</summary>
    public float HighlightRed { get; init; } = 1;

    public float HighlightGreen { get; init; } = 0.78f;

    public float HighlightBlue { get; init; } = 0.36f;

    /// <summary>For two-tone: how much of the part, from its lightest details down, takes the highlight colour (0 none).</summary>
    public float HighlightAmount { get; init; } = 0.25f;

    /// <summary>For two-tone: how light the darkest parts become, 0 (as dark as the original shading) to 1 (flat colour).</summary>
    public float Lift { get; init; } = 0.5f;

    public bool IsValid => float.IsFinite(Red) && float.IsFinite(Green) && float.IsFinite(Blue) &&
                           Red >= 0 && Green >= 0 && Blue >= 0 && Math.Max(Red, Math.Max(Green, Blue)) > 0 &&
                           Strength is >= 0 and <= 1 && Brightness is >= -1 and <= 1 &&
                           float.IsFinite(RangeHue) && RangeWidth is >= 0 and <= 180 &&
                           (Style == TextureRecolourStyle.Tint ||
                            (float.IsFinite(HighlightRed) && float.IsFinite(HighlightGreen) && float.IsFinite(HighlightBlue) &&
                             HighlightRed >= 0 && HighlightGreen >= 0 && HighlightBlue >= 0 &&
                             Math.Max(HighlightRed, Math.Max(HighlightGreen, HighlightBlue)) > 0 &&
                             HighlightAmount is >= 0 and <= 1 && Lift is >= 0 and <= 1));

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
        if (Style == TextureRecolourStyle.TwoTone)
        {
            ApplyTwoTone(image, left, top, right, bottom, scale);
            return;
        }
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

    private void ApplyTwoTone(RgbaImage image, int left, int top, int right, int bottom, float scale)
    {
        var pixels = image.Pixels;
        // The part's own light range (ignoring the darkest and lightest 2%), so a dull grey picture still spans dark to light.
        var histogram = new int[256];
        var counted = 0;
        for (var y = top; y < bottom; y++)
            for (int x = left, offset = (y * image.Width + left) * 4; x < right; x++, offset += 4)
            {
                if (Weight(pixels[offset], pixels[offset + 1], pixels[offset + 2]) <= 0) continue;
                histogram[Luminance(pixels, offset)]++;
                counted++;
            }
        if (counted == 0) return;
        var (low, high) = (Percentile(histogram, counted, 0.02), Percentile(histogram, counted, 0.98));
        var span = Math.Max(high - low, 1);
        var basePeak = Math.Max(Red, Math.Max(Green, Blue));
        var highPeak = Math.Max(HighlightRed, Math.Max(HighlightGreen, HighlightBlue));
        float[] baseColour = [Red / basePeak, Green / basePeak, Blue / basePeak];
        float[] highColour = [HighlightRed / highPeak, HighlightGreen / highPeak, HighlightBlue / highPeak];
        var bandStart = 1 - HighlightAmount;
        for (var y = top; y < bottom; y++)
            for (int x = left, offset = (y * image.Width + left) * 4; x < right; x++, offset += 4)
            {
                var weight = Weight(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
                if (weight <= 0) continue;
                var t = Math.Clamp((Luminance(pixels, offset) - low) / (float)span, 0, 1);
                var shade = (Lift + (1 - Lift) * MathF.Pow(t, 0.8f)) * 255;
                var highlight = HighlightAmount <= 0 ? 0 : SmoothStep(bandStart, Math.Min(1, bandStart + 0.15f), t);
                var highlightShade = (0.7f + 0.3f * t) * 255;
                for (var channel = 0; channel < 3; channel++)
                {
                    var repainted = baseColour[channel] * shade * (1 - highlight) + highColour[channel] * highlightShade * highlight;
                    var original = pixels[offset + channel];
                    var changed = (original + (repainted - original) * Strength) * scale;
                    pixels[offset + channel] = (byte)Math.Clamp(MathF.Round(original + (changed - original) * weight), 0, 255);
                }
            }
    }

    private static int Luminance(byte[] pixels, int offset) =>
        (pixels[offset] * 77 + pixels[offset + 1] * 151 + pixels[offset + 2] * 28) >> 8;

    private static int Percentile(int[] histogram, int count, double fraction)
    {
        var target = (long)Math.Ceiling(count * fraction);
        long seen = 0;
        for (var value = 0; value < histogram.Length; value++)
            if ((seen += histogram[value]) >= Math.Max(target, 1)) return value;
        return histogram.Length - 1;
    }

    private static float SmoothStep(float from, float to, float value)
    {
        if (to <= from) return value >= from ? 1 : 0;
        var t = Math.Clamp((value - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>How much a pixel belongs to the selection, from 0 (unchanged) to 1 (fully changed).</summary>
    public float Weight(byte red, byte green, byte blue) =>
        ColourSelections.Weight(Selection, red, green, blue, RangeHue, RangeWidth, Math.Max(red, Math.Max(green, blue)) / 255f);

    /// <summary>A pixel's hue in degrees (0 red, 60 yellow, 120 green, 240 blue); 0 for greys.</summary>
    public static float Hue(byte red, byte green, byte blue) => ColourSelections.Hue(red, green, blue);

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

/// <summary>How a recolour paints the pixels it changes.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<TextureRecolourStyle>))]
public enum TextureRecolourStyle
{
    /// <summary>Keep each pixel's brightness and take the new colour's hue.</summary>
    Tint,
    /// <summary>Repaint in one colour with the original shading, and a second colour on the lightest details.</summary>
    TwoTone,
}
