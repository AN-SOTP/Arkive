using System.Globalization;
using System.Text.RegularExpressions;

namespace Arkive.Core.Particles;

/// <summary>
/// What the particle preview takes from an effect material exported by UModel: the texture that carries its shape,
/// its blend mode and the colour it multiplies the particles by.
/// </summary>
/// <param name="TexturePath">The exported PNG, or null when none was exported.</param>
/// <param name="Additive">True for BLEND_Additive materials; the others draw as translucent.</param>
/// <param name="Tint">The material instance's emissive or main colour, when it sets one.</param>
public sealed record ParticleMaterialInfo(string? TexturePath, bool Additive, float[]? Tint)
{
    private static readonly Regex TextureParameter = new(
        @"ParameterValue = Texture2D'(?<texture>[^']+)'\s*\r?\n\s*ParameterName = (?<name>\S+)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex VectorParameter = new(
        @"ParameterValue = \{ R=(?<r>[-\d.eE]+), G=(?<g>[-\d.eE]+), B=(?<b>[-\d.eE]+), A=[-\d.eE]+ \}\s*\r?\n\s*ParameterName = (?<name>\S+)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // Lost Ark's effect materials name the texture that shapes the particle main_texture, emissive_tex, map_a...
    private static readonly string[] ShapeNames = ["main", "emissive_tex", "emissive_texture", "map_a", "diffuse", "base", "tex"];

    public static ParticleMaterialInfo Read(IReadOnlyList<string> exportedFiles, string material)
    {
        var pngs = exportedFiles.Where(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToArray();
        string? Png(string name) => pngs.FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(name, StringComparison.OrdinalIgnoreCase));
        var props = exportedFiles.Where(path => path.EndsWith(".props.txt", StringComparison.OrdinalIgnoreCase)).ToArray();
        var own = props.FirstOrDefault(path => Path.GetFileName(path).Equals(material + ".props.txt", StringComparison.OrdinalIgnoreCase));
        var additive = props.Any(path => File.ReadAllText(path).Contains("BLEND_Additive", StringComparison.OrdinalIgnoreCase));
        string? texture = null;
        float[]? tint = null;
        if (own is not null)
        {
            var text = File.ReadAllText(own);
            var textures = TextureParameter.Matches(text).Select(match => (Texture: match.Groups["texture"].Value, Name: match.Groups["name"].Value.ToLowerInvariant())).ToArray();
            foreach (var shape in ShapeNames)
            {
                texture = textures.Where(item => item.Name.Contains(shape, StringComparison.Ordinal)).Select(item => Png(item.Texture)).FirstOrDefault(path => path is not null);
                if (texture is not null) break;
            }
            texture ??= textures.Select(item => Png(item.Texture)).FirstOrDefault(path => path is not null);
            var colour = VectorParameter.Matches(text)
                .FirstOrDefault(match => match.Groups["name"].Value.Contains("color", StringComparison.OrdinalIgnoreCase));
            if (colour is not null)
                tint = new[] { "r", "g", "b" }.Select(channel => float.Parse(colour.Groups[channel].Value, CultureInfo.InvariantCulture)).ToArray();
        }
        // Otherwise the first texture UModel lists for the material.
        var mat = exportedFiles.FirstOrDefault(path => Path.GetFileName(path).Equals(material + ".mat", StringComparison.OrdinalIgnoreCase));
        if (texture is null && mat is not null)
            foreach (var line in File.ReadLines(mat))
                if (line.Split('=', 2) is [_, var name] && Png(name.Trim()) is { } png)
                {
                    texture = png;
                    break;
                }
        return new ParticleMaterialInfo(texture ?? pngs.FirstOrDefault(), additive, tint is { Length: 3 } && tint.All(float.IsFinite) ? tint : null);
    }
}
