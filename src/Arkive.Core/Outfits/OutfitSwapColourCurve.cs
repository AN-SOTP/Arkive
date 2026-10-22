using Arkive.Core.Textures;

namespace Arkive.Core.Outfits;

/// <summary>
/// How much brighter or darker a repainted picture must be for the replaced outfit's shader to light it the way the donor's
/// shader lit the original: one gain in linear light, from the median lit brightness the donor had against the median the
/// first painting got. One gain for all three channels, so hues never shift, kept between 0.6 and 3.
/// </summary>
public sealed class OutfitSwapColourCurve
{
    private const float Least = 0.6f;
    private const float Most = 3f;
    private readonly byte[] _table = new byte[256];

    private OutfitSwapColourCurve(float gain)
    {
        Gain = gain;
        for (var v = 0; v < 256; v++) _table[v] = Encode(Math.Min(1, Linear((byte)v) * gain));
    }

    public float Gain { get; }

    /// <summary>The gain from the donor's lit colours painted into the picture's layout and a lit bake of the first painting.</summary>
    public static OutfitSwapColourCurve? Learn(RgbaImage wanted, RgbaImage drawn)
    {
        var want = new List<float>();
        var got = new List<float>();
        for (var i = 0; i < drawn.Pixels.Length; i += 4 * 3)
        {
            if (drawn.Pixels[i + 3] < 200 || wanted.Pixels[i + 3] < 200) continue;
            want.Add(Luminance(wanted.Pixels, i));
            got.Add(Luminance(drawn.Pixels, i));
        }
        if (want.Count < 500) return null;
        want.Sort();
        got.Sort();
        var drawnMedian = got[got.Count / 2];
        if (drawnMedian < 0.01f) return null;
        return new OutfitSwapColourCurve(Math.Clamp(want[want.Count / 2] / drawnMedian, Least, Most));
    }

    /// <summary>Brightens or darkens a picture by the gain.</summary>
    public void Apply(RgbaImage colour)
    {
        var pixels = colour.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
            for (var c = 0; c < 3; c++) pixels[i + c] = _table[pixels[i + c]];
    }

    private static float Luminance(byte[] pixels, int i) =>
        0.2126f * Linear(pixels[i]) + 0.7152f * Linear(pixels[i + 1]) + 0.0722f * Linear(pixels[i + 2]);

    private static float Linear(byte value)
    {
        var c = value / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private static byte Encode(float linear)
    {
        var c = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(c * 255), 0, 255);
    }
}
