using System.Numerics;
using System.Text.RegularExpressions;

namespace Arkive.Core.Proportions;

/// <summary>A slider: one body area or one detail of it (width, lift, sag…), in percent.</summary>
public sealed record BodyZone(string Id, string Group, string Label, string Description, double Minimum, double Maximum);

/// <summary>
/// A spot on the body, in the skeleton's rest pose, measured from a bone in units of the body's height
/// (the head bone's height above the floor). Forward is the way the character faces, Side points away from
/// the body's centre on either side.
/// </summary>
public readonly record struct BodyLandmark(string Bone, double Forward, double Up, double Side, double Radius);

/// <summary>
/// A body Arkive has tuned: one of the game's base player bodies (every class of a body shares its skeleton,
/// so it is found from the skeleton's rest pose) or a named NPC (found from its mesh names).
/// </summary>
public sealed record BodyProfile(
    string Id,
    string DisplayName,
    bool Female,
    double PelvisHeight,
    double HeadHeight,
    BodyLandmark Chest,
    BodyLandmark Buttocks,
    BodyLandmark Belly,
    string? NpcMeshPattern = null)
{
    public bool IsNpc => NpcMeshPattern is not null;

    public IReadOnlyList<BodyZone> Zones => Female ? BodyProfiles.FemaleZones : BodyProfiles.MaleZones;
}

public static partial class BodyProfiles
{
    private static BodyZone Zone(string group, string id, string label, string description, double min = -40, double max = 60) =>
        new(id, group, label, description, min, max);

    private static IEnumerable<BodyZone> Zones(bool female)
    {
        var chest = female ? "Breasts" : "Chest";
        yield return Zone("Arms and neck", "neck", "Neck", "Thickness of the neck.");
        yield return Zone("Arms and neck", "shoulders", "Shoulders", "Roundness of the shoulder muscles where the arms join the body.");
        yield return Zone("Arms and neck", "upperArms", "Upper arms", "Thickness of the upper arms.");
        yield return Zone("Arms and neck", "forearms", "Forearms", "Thickness of the forearms.");
        yield return Zone(chest, "chest", "Size", female ? "Overall size of the breasts." : "Overall size of the chest muscles.", -50, female ? 100 : 80);
        yield return Zone(chest, "chestWidth", "Width", "Wider or narrower, side to side.", -50, 80);
        yield return Zone(chest, "chestProjection", "Projection", "How far they stand out from the body.", -50, 80);
        yield return Zone(chest, "chestHeight", "Height", "Taller or shorter, top to bottom.", -50, 80);
        yield return Zone(chest, "chestLift", "Lift", "Moves them up (positive) or down (negative) on the body.", -60, 60);
        yield return Zone(chest, "chestSpacing", "Spacing", "Moves them apart (positive) or together (negative).", -60, 60);
        if (female) yield return Zone(chest, "chestSag", "Sag", "Lets them hang lower (positive) or makes them perkier (negative).", -60, 80);
        yield return Zone("Waist and belly", "waist", "Waist", "Width and depth of the waist.");
        yield return Zone("Waist and belly", "waistWidth", "Waist width", "Width of the waist only, side to side.");
        yield return Zone("Waist and belly", "waistDepth", "Waist depth", "Depth of the waist only, front to back.");
        yield return Zone("Waist and belly", "belly", "Belly", "How far the stomach sticks out.");
        yield return Zone("Hips and buttocks", "hips", "Hips", "Width of the hips.");
        yield return Zone("Hips and buttocks", "buttocks", "Buttocks size", "Overall size of the buttocks.", -50, 100);
        yield return Zone("Hips and buttocks", "buttocksWidth", "Buttocks width", "Wider or narrower, side to side.", -50, 80);
        yield return Zone("Hips and buttocks", "buttocksProjection", "Buttocks projection", "How far they stand out from the body.", -50, 80);
        yield return Zone("Hips and buttocks", "buttocksLift", "Buttocks lift", "Moves them up (positive) or down (negative).", -60, 60);
        yield return Zone("Hips and buttocks", "buttocksSag", "Buttocks sag", "Lets them hang lower (positive) or makes them rounder and higher (negative).", -60, 80);
        yield return Zone("Legs", "thighs", "Thighs", "Thickness of the thighs.");
        yield return Zone("Legs", "thighWidth", "Thigh width", "Thickness of the thighs side to side only.");
        yield return Zone("Legs", "thighDepth", "Thigh depth", "Thickness of the thighs front to back only.");
        yield return Zone("Legs", "calves", "Calves", "Thickness of the lower legs.");
    }

    public static readonly IReadOnlyList<BodyZone> FemaleZones = Zones(true).ToArray();

    public static readonly IReadOnlyList<BodyZone> MaleZones = Zones(false).ToArray();

    // Landmarks were measured on several outfits of each body (the most forward point of the chest, the most
    // rearward point of the buttocks) and checked by eye on renders of each body.
    public static readonly IReadOnlyList<BodyProfile> Players =
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

    // NPCs have their own skeletons; each is tuned from its own meshes and offered only for those meshes.
    // Echidna's mn_sden_02 is a 12 m cinematic copy whose clothing materials do not render, so it is left out.
    // Brelshaza's and Nineveh's skeletons have no upper-chest bone, so their chest is measured from spine1.
    public static readonly IReadOnlyList<BodyProfile> Npcs =
    [
        new("npc-brelshaza", "Brelshaza (NPC)", true, 0.752, 1.162,
            new("bip001-spine1", 0.064, 0.040, 0.036, 0.046),
            new("bip001-pelvis", -0.074, -0.015, 0.040, 0.058),
            new("bip001-spine", 0.056, -0.010, 0.0, 0.060), @"^mn_cuas_\d+(-\d+)?_sk$"),
        new("npc-nineveh", "Nineveh (NPC)", true, 0.739, 1.085,
            new("bip001-spine1", 0.064, 0.020, 0.035, 0.046),
            new("bip001-pelvis", -0.068, -0.010, 0.040, 0.058),
            new("bip001-spine", 0.056, -0.010, 0.0, 0.060), @"^mn_ppnn_\d+(-\d+)?(_body)?_sk$"),
        new("npc-echidna", "Echidna (NPC)", true, 1.414, 2.173,
            new("bip001-spine2", 0.054, 0.020, 0.035, 0.046),
            new("bip001-pelvis", -0.074, 0.010, 0.040, 0.058),
            new("bip001-spine", 0.056, -0.010, 0.0, 0.060), @"^mn_sden_(00|01|05)(-\d+)?_sk$"),
        new("npc-serca", "Serca (NPC)", true, 1.276, 1.988,
            new("bip001-spine2", 0.062, -0.005, 0.035, 0.046),
            new("bip001-pelvis", -0.076, 0.000, 0.040, 0.058),
            new("bip001-spine", 0.056, -0.010, 0.0, 0.060), @"^mn_cnpe_\d+(-\d+)?_sk$"),
        new("npc-beatrice", "Beatrice (NPC)", true, 0.674, 1.158,
            new("bip001-spine2", 0.066, 0.045, 0.036, 0.046),
            new("bip001-pelvis", -0.078, 0.015, 0.045, 0.058),
            new("bip001-spine", 0.058, -0.010, 0.0, 0.060), @"^np_pabs_\d+(-\d+)?_sk$"),
    ];

    public static readonly IReadOnlyList<BodyProfile> All = [.. Players, .. Npcs];

    /// <summary>The AnimSet holding a player body's animations (in its class's base package), or null for NPCs.</summary>
    public static string? AnimSetName(BodyProfile profile) => profile.Id switch
    {
        "female-warrior" => "pc_wr_f_00_ani",
        "male-warrior" => "pc_wr_00_ani",
        "female-assassin" => "pc_dl_00_ani",
        "female-mage" => "pc_mg_00_ani",
        "female-martial-artist" => "pc_ft_00_ani",
        "male-gunner" => "pc_gn_00_ani",
        "female-specialist" => "pc_sp_00_ani",
        "male-specialist" => "pc_sp_m_00_ani",
        _ => null,
    };

    /// <summary>
    /// The profile for a mesh: a named NPC by its mesh name, otherwise the player body whose rest pose matches the
    /// skeleton. Null for a body Arkive has not tuned.
    /// </summary>
    public static BodyProfile? Detect(IReadOnlyDictionary<string, Vector3> restBones, string? objectName = null)
    {
        if (!restBones.TryGetValue("bip001-pelvis", out var pelvis) || !restBones.TryGetValue("bip001-head", out var head))
            return null;
        if (objectName is not null && ForNpc(objectName) is { } npc) return npc;
        return Players.Where(profile => Math.Abs(profile.PelvisHeight - pelvis.Y) < 0.006 &&
                                        Math.Abs(profile.HeadHeight - head.Y) < 0.006)
            .OrderBy(profile => Math.Abs(profile.PelvisHeight - pelvis.Y) + Math.Abs(profile.HeadHeight - head.Y))
            .FirstOrDefault();
    }

    private static BodyProfile? ForNpc(string objectName) =>
        Npcs.FirstOrDefault(profile => Regex.IsMatch(objectName, profile.NpcMeshPattern!, RegexOptions.IgnoreCase));

    /// <summary>
    /// The body a mesh name belongs to: a named NPC, or a player mesh by its class code (pc_wr_f_…, pc_wbk_f_…).
    /// Class codes start with their base class's letter; _f and _m mark the less common sex of that class.
    /// </summary>
    public static BodyProfile? ForObjectName(string objectName)
    {
        if (ForNpc(objectName) is { } npc) return npc;
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
        return Players.FirstOrDefault(profile => profile.Id == id);
    }

    /// <summary>
    /// True for a mesh the proportion sliders can shape: a player character's body or outfit piece (tops, bottoms,
    /// one-piece outfits, arms, shoulders) or one of the tuned NPCs. Faces, hair, headwear, weapons, other NPCs and
    /// objects are not.
    /// </summary>
    public static bool IsReshapeable(string objectName)
    {
        if (ForNpc(objectName) is not null) return true;
        var match = PlayerMeshName().Match(objectName);
        return match.Success && BodyPart().IsMatch(match.Groups["rest"].Value) && ForObjectName(objectName) is not null;
    }

    /// <summary>The piece's part (upper, lower, dress…), "npc" for a tuned NPC, or null when it cannot be reshaped.</summary>
    public static string? PieceName(string objectName)
    {
        if (ForNpc(objectName) is not null) return "npc";
        var match = PlayerMeshName().Match(objectName);
        if (!match.Success) return null;
        var part = BodyPart().Match(match.Groups["rest"].Value);
        return part.Success ? part.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// The name every piece of one player outfit shares (pc_sp_av_108-1_upper_sk and pc_sp_av_108-1_lower_sk both
    /// give pc_sp_av_108-1), so its top, bottom and arms can be opened together without its alternative versions.
    /// NPC meshes are whole bodies and have none.
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
