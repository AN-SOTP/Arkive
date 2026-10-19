using System.Text.RegularExpressions;

namespace Arkive.Core.Discovery;

/// <summary>What part of a map an asset is, for listing a map's parts the way a recolour is planned.</summary>
public enum ZonePartGroup
{
    /// <summary>The ground: landscape materials, the layer materials they draw with and those layers' textures.</summary>
    Terrain,
    /// <summary>Sky and cloud textures, materials and meshes.</summary>
    Sky,
    /// <summary>Fog volume materials and fog textures.</summary>
    Fog,
    /// <summary>Colour grading (LUT) textures and light function materials.</summary>
    Lighting,
    /// <summary>Effects the map places (mist, embers, clouds) and the materials and textures of their packages.</summary>
    Effects,
    /// <summary>Monsters and characters the map's scenes place, with their materials and textures.</summary>
    Characters,
    Meshes,
    Materials,
    Textures,
    /// <summary>Lightmaps, shadow maps and terrain blend masks the editor generated; changing them shifts lighting.</summary>
    GeneratedMaps,
}

/// <summary>An asset of a map, the group it belongs to and which of the map's level packages use it.</summary>
/// <param name="InLevel">True when the asset is held by one of the map's own level packages, not imported.</param>
public sealed record ZoneMapPart(PackageIndexMatch Asset, ZonePartGroup Group, IReadOnlyList<string> Levels, bool InLevel);

/// <summary>
/// How many lights, fogs and environment volumes the map's levels place. Their colours are level settings, which
/// mods cannot change yet, so they are counted rather than listed.
/// </summary>
public sealed record ZoneLevelSettings(int Suns, int Lights, int Fogs, int EnvironmentVolumes)
{
    public int Total => Suns + Lights + Fogs + EnvironmentVolumes;

    public static ZoneLevelSettings Count(IEnumerable<IReadOnlyDictionary<string, int>> classCounts)
    {
        int suns = 0, lights = 0, fogs = 0, environments = 0;
        foreach (var counts in classCounts)
            foreach (var (name, count) in counts)
            {
                if (name.Contains("directionallightcomponent", StringComparison.Ordinal)) suns += count;
                else if (name.EndsWith("lightcomponent", StringComparison.Ordinal) && !name.Contains("environment", StringComparison.Ordinal))
                    lights += count;
                else if (name.Contains("fog", StringComparison.Ordinal) && name.EndsWith("component", StringComparison.Ordinal))
                    fogs += count;
                else if (name.StartsWith("efenvironment", StringComparison.Ordinal) && name.EndsWith("volume", StringComparison.Ordinal))
                    environments += count;
            }
        return new ZoneLevelSettings(suns, lights, fogs, environments);
    }
}

/// <param name="UnreadLevelPackages">Level packages that could not be read (their parts are missing).</param>
/// <param name="NotIndexed">Assets the levels use that are not in the Asset Library's index.</param>
public sealed record ZoneMapReport(
    ZoneMap Map,
    IReadOnlyList<ZoneMapPart> Parts,
    ZoneLevelSettings Settings,
    int UnreadLevelPackages,
    int NotIndexed);

/// <summary>Sorts a map's assets into <see cref="ZonePartGroup"/>s.</summary>
public static partial class ZoneParts
{
    /// <param name="terrain">Objects (package.object, lower case) the landscape draws with.</param>
    /// <param name="generated">Objects the landscape uses as blend masks.</param>
    /// <param name="characterPackages">Packages holding skeletal meshes the map places and no static mesh it places.</param>
    /// <param name="effectPackages">Packages holding effects the map places and no static mesh it places.</param>
    public static ZonePartGroup Classify(
        string packageName,
        string objectName,
        string className,
        IReadOnlySet<string> terrain,
        IReadOnlySet<string> generated,
        IReadOnlySet<string> characterPackages,
        IReadOnlySet<string> effectPackages)
    {
        var key = (packageName + "." + objectName).ToLowerInvariant();
        var type = className.ToLowerInvariant();
        var name = objectName.ToLowerInvariant();
        if (type is "lightmaptexture2d" or "shadowmaptexture2d" || generated.Contains(key)) return ZonePartGroup.GeneratedMaps;
        if (type == "landscapematerialinstanceconstant" || terrain.Contains(key)) return ZonePartGroup.Terrain;
        if (type == "particlesystem") return ZonePartGroup.Effects;
        if (type == "skeletalmesh") return ZonePartGroup.Characters;
        if (name.Contains("fog", StringComparison.Ordinal)) return ZonePartGroup.Fog;
        if (name.Contains("lightfunction", StringComparison.Ordinal) || type == "texture2d" && ColourGrading().IsMatch(name))
            return ZonePartGroup.Lighting;
        if (name.Contains("sky", StringComparison.Ordinal) || name.Contains("cloud", StringComparison.Ordinal) ||
            packageName.Contains("sky", StringComparison.OrdinalIgnoreCase))
            return ZonePartGroup.Sky;
        // Level packages also hold the materials of the meshes a map places, so only other packages go by what they hold.
        var levelPackage = packageName.StartsWith("lv_", StringComparison.OrdinalIgnoreCase);
        if (!levelPackage && characterPackages.Contains(packageName)) return ZonePartGroup.Characters;
        if (!levelPackage && effectPackages.Contains(packageName)) return ZonePartGroup.Effects;
        return type switch
        {
            "staticmesh" => ZonePartGroup.Meshes,
            _ when type.Contains("material", StringComparison.Ordinal) => ZonePartGroup.Materials,
            _ => ZonePartGroup.Textures,
        };
    }

    // lv_kur_death_lut_07, lv_kur_death_04_lut_01: the game's colour-grading look-up tables (bg_lut_* is Luterra).
    [GeneratedRegex(@"(?:^|_)lut_\d")]
    private static partial Regex ColourGrading();

    /// <summary>The object name inside a material reference (texture2d'lv_x.name' or materialinstanceconstant'name').</summary>
    public static string? ReferencedName(string? resolved)
    {
        if (string.IsNullOrWhiteSpace(resolved) || resolved.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
        var start = resolved.IndexOf('\'');
        var inner = start >= 0 && resolved.EndsWith('\'') ? resolved[(start + 1)..^1] : resolved;
        var dot = inner.LastIndexOf('.');
        return dot >= 0 ? inner[(dot + 1)..] : inner;
    }
}
