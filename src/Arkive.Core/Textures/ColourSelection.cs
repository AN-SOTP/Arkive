using System.Text.Json.Serialization;

namespace Arkive.Core.Textures;

/// <summary>Which colours a recolour changes: all of them, one range of hues, or the light, nearly white ones.</summary>
/// <remarks>Saved by name ("ColourRange"), as the effect editor's page sends it.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ColourSelection>))]
public enum ColourSelection
{
    /// <summary>Every colour.</summary>
    All,
    /// <summary>Colours whose hue is near a chosen hue (gold, say), keeping greys and other colours.</summary>
    ColourRange,
    /// <summary>Light, nearly white colours (white stone, pale highlights), keeping coloured and dark ones.</summary>
    LightAreas,
}

/// <summary>
/// How much a colour belongs to a <see cref="ColourSelection"/>, shared by the Texture Viewer's recolour and the effect
/// editor's tint so both pick colours the same way. Colours fade out of a range over a few degrees of hue past its
/// width, so its edges do not look cut out.
/// </summary>
public static class ColourSelections
{
    private const float HueFeather = 12;

    /// <summary>
    /// The weight, from 0 (unchanged) to 1 (fully changed), of a colour with channels <paramref name="red"/>,
    /// <paramref name="green"/>, <paramref name="blue"/> (any scale; negative channels are never selected).
    /// </summary>
    /// <param name="lightness">How light the colour is, 0 to 1, for <see cref="ColourSelection.LightAreas"/>; effect colours
    /// carry no fixed white point, so they pass 1.</param>
    public static float Weight(ColourSelection selection, float red, float green, float blue, float hue, float width,
        float lightness = 1)
    {
        if (selection == ColourSelection.All) return 1;
        var strongest = Math.Max(red, Math.Max(green, blue));
        var weakest = Math.Min(red, Math.Min(green, blue));
        if (strongest <= 0 || weakest < 0) return 0;
        var saturation = (strongest - weakest) / strongest;
        if (selection == ColourSelection.LightAreas)
            // Nearly white or pale grey: little colour, and bright enough to read as light.
            return Ramp(0.25f - saturation, 0, 0.12f) * Ramp(lightness, 0.5f, 0.75f);
        var distance = Math.Abs(((Hue(red, green, blue) - hue) % 360 + 540) % 360 - 180);
        // A hue only means something with some colour; greys and near-greys are never in a colour range.
        return Ramp(width + HueFeather - distance, 0, HueFeather) * Ramp(saturation, 0.08f, 0.2f);
    }

    /// <summary>A colour's hue in degrees (0 red, 60 yellow, 120 green, 240 blue); 0 for greys.</summary>
    public static float Hue(float red, float green, float blue)
    {
        var max = Math.Max(red, Math.Max(green, blue));
        var delta = max - Math.Min(red, Math.Min(green, blue));
        if (delta <= 0) return 0;
        var hue = max == red ? (green - blue) / delta % 6 : max == green ? (blue - red) / delta + 2 : (red - green) / delta + 4;
        return (hue * 60 + 360) % 360;
    }

    private static float Ramp(float value, float from, float to) => Math.Clamp((value - from) / (to - from), 0, 1);
}
