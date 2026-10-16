using System.Numerics;
using System.Text.RegularExpressions;

namespace Arkive.Core.Proportions;

/// <summary>A slider: one body area whose size the user can change, in percent.</summary>
public sealed record BodyZone(string Id, string Label, string Description, double Minimum, double Maximum);

/// <summary>
/// A spot on the body, in the skeleton's rest pose, measured from a bone in units of the body's height
/// (the head bone's height above the floor). Forward is the way the character faces, Side points away from
/// the body's centre on either side.
/// </summary>
public readonly record struct BodyLandmark(string Bone, double Forward, double Up, double Side, double Radius);

/// <summary>
/// One of the game's base bodies. Every class of a body shares its skeleton, so a profile is found from the
/// skeleton's rest pose and the tuned landmarks hold for all of its outfits.
/// </summary>
public sealed record BodyProfile(
    string Id,
    string DisplayName,
    bool Female,
    double PelvisHeight,
    double HeadHeight,
    BodyLandmark Chest,
    BodyLandmark Buttocks,
    BodyLandmark Belly)
{
    public IReadOnlyList<BodyZone> Zones => Female ? BodyProfiles.FemaleZones : BodyProfiles.MaleZones;
}

public static partial class BodyProfiles
{
    private static BodyZone Zone(string id, string label, string description, double min = -40, double max = 60) =>
        new(id, label, description, min, max);

    private static readonly BodyZone[] SharedHead =
    [
        Zone("neck", "Neck", "Thickness of the neck."),
        Zone("shoulders", "Shoulders", "Roundness of the shoulder muscles where the arms join the body."),
        Zone("upperArms", "Upper arms", "Thickness of the upper arms."),
        Zone("forearms", "Forearms", "Thickness of the forearms."),
    ];

    private static readonly BodyZone[] SharedBody =
    [
        Zone("waist", "Waist", "Width and depth of the waist."),
        Zone("belly", "Belly", "How far the stomach sticks out."),
        Zone("hips", "Hips", "Width of the hips."),
        Zone("buttocks", "Buttocks", "Size of the buttocks.", -50, 100),
        Zone("thighs", "Thighs", "Thickness of the thighs."),
        Zone("calves", "Calves", "Thickness of the lower legs."),
    ];

    public static readonly IReadOnlyList<BodyZone> FemaleZones =
        [.. SharedHead, Zone("chest", "Breasts", "Size of the breasts.", -50, 100), .. SharedBody];

    public static readonly IReadOnlyList<BodyZone> MaleZones =
        [.. SharedHead, Zone("chest", "Chest", "Size of the chest muscles.", -40, 80), .. SharedBody];

    // Landmarks were measured on several outfits of each body (the most forward point of the chest, the most
    // rearward point of the buttocks) and checked by eye on renders of each body.
    public static readonly IReadOnlyList<BodyProfile> All =
    [
        new("female-specialist", "Female Specialist", true, 0.558, 0.901,
            new("bip001-spine2", 0.062, -0.010, 0.032, 0.050),
            new("bip001-pelvis", -0.075, 0.010, 0.040, 0.060),
            new("bip001-spine", 0.060, -0.010, 0.0, 0.065)),
        new("male-specialist", "Male Specialist", false, 0.590, 0.968,
            new("bip001-spine2", 0.062, -0.040, 0.050, 0.060),
            new("bip001-pelvis", -0.075, 0.000, 0.045, 0.060),
            new("bip001-spine", 0.060, -0.020, 0.0, 0.070)),
        new("male-gunner", "Male Gunner / Male Martial Artist", false, 0.673, 1.153,
            new("bip001-spine2", 0.070, 0.020, 0.055, 0.060),
            new("bip001-pelvis", -0.080, 0.010, 0.045, 0.060),
            new("bip001-spine", 0.065, -0.010, 0.0, 0.070)),
        new("male-warrior", "Male Warrior", false, 0.704, 1.190,
            new("bip001-spine2", 0.080, 0.020, 0.060, 0.065),
            new("bip001-pelvis", -0.085, 0.010, 0.050, 0.060),
            new("bip001-spine", 0.070, -0.010, 0.0, 0.075)),
        new("female-mage", "Female Mage", true, 0.718, 1.133,
            new("bip001-spine2", 0.060, 0.034, 0.040, 0.046),
            new("bip001-pelvis", -0.072, 0.010, 0.040, 0.058),
            new("bip001-spine", 0.058, -0.010, 0.0, 0.060)),
        new("female-martial-artist", "Female Martial Artist", true, 0.725, 1.145,
            new("bip001-spine2", 0.064, 0.030, 0.038, 0.046),
            new("bip001-pelvis", -0.078, 0.010, 0.040, 0.058),
            new("bip001-spine", 0.058, -0.010, 0.0, 0.060)),
        new("female-warrior", "Female Warrior", true, 0.734, 1.152,
            new("bip001-spine2", 0.064, 0.042, 0.038, 0.046),
            new("bip001-pelvis", -0.070, 0.012, 0.040, 0.058),
            new("bip001-spine", 0.058, -0.010, 0.0, 0.060)),
        new("female-assassin", "Female Assassin / Female Gunner", true, 0.738, 1.143,
            new("bip001-spine2", 0.058, 0.034, 0.036, 0.045),
            new("bip001-pelvis", -0.068, 0.010, 0.040, 0.058),
            new("bip001-spine", 0.056, -0.010, 0.0, 0.060)),
    ];

    /// <summary>The profile whose rest pose matches the skeleton, or null for a body Arkive has not tuned.</summary>
    public static BodyProfile? Detect(IReadOnlyDictionary<string, Vector3> restBones)
    {
        if (!restBones.TryGetValue("bip001-pelvis", out var pelvis) || !restBones.TryGetValue("bip001-head", out var head))
            return null;
        return All.Where(profile => Math.Abs(profile.PelvisHeight - pelvis.Y) < 0.006 &&
                                    Math.Abs(profile.HeadHeight - head.Y) < 0.006)
            .OrderBy(profile => Math.Abs(profile.PelvisHeight - pelvis.Y) + Math.Abs(profile.HeadHeight - head.Y))
            .FirstOrDefault();
    }

    /// <summary>
    /// The base body a player mesh name belongs to, from its class code (pc_wr_f_…, pc_wbk_f_…). Class codes start
    /// with their base class's letter; _f and _m mark the less common sex of that class.
    /// </summary>
    public static BodyProfile? ForObjectName(string objectName)
    {
        var match = PlayerMeshName().Match(objectName);
        if (!match.Success) return null;
        var code = match.Groups["code"].Value;
        var sex = match.Groups["sex"].Value;
        var id = (code[0], sex) switch
        {
            ('w', "f") => "female-warrior",
            ('w', _) => "male-warrior",
            ('f', "m") => "male-gunner",
            ('f', _) => "female-martial-artist",
            ('g', "f") => "female-assassin",
            ('g', _) => "male-gunner",
            ('d', _) => "female-assassin",
            ('m', _) => "female-mage",
            ('s', "m") => "male-specialist",
            ('s', _) => "female-specialist",
            _ => null,
        };
        return All.FirstOrDefault(profile => profile.Id == id);
    }

    /// <summary>
    /// True for a player character's body or outfit piece (tops, bottoms, one-piece outfits, arms, shoulders):
    /// the meshes the proportion sliders can shape. Faces, hair, headwear, weapons, NPCs and objects are not.
    /// </summary>
    public static bool IsPlayerBodyOrOutfit(string objectName)
    {
        var match = PlayerMeshName().Match(objectName);
        return match.Success && BodyPart().IsMatch(match.Groups["rest"].Value) && ForObjectName(objectName) is not null;
    }

    /// <summary>The piece's part (upper, lower, dress…), or null when it is not a body or outfit piece.</summary>
    public static string? PieceName(string objectName)
    {
        var match = PlayerMeshName().Match(objectName);
        if (!match.Success) return null;
        var part = BodyPart().Match(match.Groups["rest"].Value);
        return part.Success ? part.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// The name every piece of one outfit shares (pc_sp_av_108-1_upper_sk and pc_sp_av_108-1_lower_sk both give
    /// pc_sp_av_108-1), so its top, bottom and arms can be opened together without its alternative versions.
    /// </summary>
    public static string? OutfitStem(string objectName)
    {
        var match = PlayerMeshName().Match(objectName);
        if (!match.Success) return null;
        var part = BodyPart().Match(match.Groups["rest"].Value);
        return part.Success ? objectName[..(match.Groups["rest"].Index + part.Index)].ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"^pc_(?<code>[a-z]{2,3})(?:_(?<sex>[mf]))?_(?<rest>.+)_sk$", RegexOptions.IgnoreCase)]
    private static partial Regex PlayerMeshName();

    // The part name is the last word of the name; misspellings found in the game files are included.
    [GeneratedRegex(@"(?:^|[_-])(upper|lower|dress|arm|arms|shoulder|shoudler|shouder|body|base|all)\d*-?$", RegexOptions.IgnoreCase)]
    private static partial Regex BodyPart();
}
