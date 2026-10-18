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
    public bool IsValid => float.IsFinite(Red) && float.IsFinite(Green) && float.IsFinite(Blue) &&
                           Red >= 0 && Green >= 0 && Blue >= 0 && Math.Max(Red, Math.Max(Green, Blue)) > 0 &&
                           Strength is >= 0 and <= 1 && Brightness is >= -1 and <= 1;

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
                var strongest = Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2]));
                pixels[offset] = Mix(pixels[offset], strongest * red);
                pixels[offset + 1] = Mix(pixels[offset + 1], strongest * green);
                pixels[offset + 2] = Mix(pixels[offset + 2], strongest * blue);
            }
        }

        byte Mix(byte original, float tinted) =>
            (byte)Math.Clamp(MathF.Round((original + (tinted - original) * Strength) * scale), 0, 255);
    }

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
