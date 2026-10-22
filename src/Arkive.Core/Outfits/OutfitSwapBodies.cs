using System.Numerics;
using System.Text.RegularExpressions;
using Arkive.Core.Proportions;

namespace Arkive.Core.Outfits;

/// <summary>What Arkive needs to know about each player body for outfit swaps.</summary>
/// <param name="AvatarCode">The class code of the body's shared costumes (pc_wr_f_av_103_dress_sk → wr_f).</param>
/// <param name="BaseBody">The game's bare base body meshes, used to keep cloth off the skin and to lay skin on; empty when the
/// game has none. They must share the skin picture layout of the class's outfits (some only ship in the Chinese build's
/// _chn meshes, which the global client still carries).</param>
/// <param name="Head">The face and hair shown with a swap preview.</param>
public sealed record OutfitSwapBodyInfo(BodyProfile Profile, string AvatarCode, IReadOnlyList<string> BaseBody, IReadOnlyList<string> Head)
{
    public bool Female => Profile.Female;
}

public static partial class OutfitSwapBodies
{
    public static readonly IReadOnlyList<OutfitSwapBodyInfo> All =
    [
        Info("female-warrior", "wr_f", ["pc_wr_f_basebody_upper_sk", "pc_wr_f_basebody_lower_sk", "pc_wr_f_basebody_arm_sk"]),
        Info("female-assassin", "dl", ["pc_dk_av_base_upper_sk", "pc_dk_av_base_lower_sk"]),
        Info("guardianknight", "dl", ["pc_dk_av_base_upper_sk", "pc_dk_av_base_lower_sk"]),
        Info("female-mage", "mg", ["pc_mg_av_basebody_sk_chn"]),
        Info("female-martial-artist", "ft", ["pc_ft_av_body_sk_chn"]),
        Info("female-specialist", "sp", ["pc_sp_00_body_sk"]),
        Info("male-warrior", "wr", ["pc_wr_basebody_upper_00_sk", "pc_wr_basebody_lower_00_sk", "pc_wr_basebody_arm_00_sk"]),
        Info("male-gunner", "gn", ["pc_ft_m_av_base_upper_sk", "pc_ft_m_av_base_lower_sk"]),
        Info("male-specialist", "sp_m", ["pc_sp_m_av_base_upper_sk", "pc_sp_m_av_base_lower_sk"]),
    ];

    private static OutfitSwapBodyInfo Info(string id, string code, string[] baseBody) =>
        new(BodyProfiles.Players.Single(profile => profile.Id == id), code, baseBody, [$"pc_{code}_00_face_sk", $"pc_{code}_00_hair_sk"]);

    public static OutfitSwapBodyInfo? For(BodyProfile profile) => All.FirstOrDefault(info => info.Profile.Id == profile.Id);

    /// <summary>The body an outfit piece was made for: from its skeleton's rest pose, or its name when the skeleton is partial.</summary>
    public static BodyProfile? Detect(OutfitSwapMesh mesh, string objectName)
    {
        var bones = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        for (var i = 0; i < mesh.BoneNames.Length; i++) bones.TryAdd(mesh.BoneNames[i], mesh.BonePositions[i]);
        return BodyProfiles.Detect(bones, objectName) ?? BodyProfiles.ForObjectName(objectName);
    }

    /// <summary>Skin sections (the body showing between clothes) use the class's base body material.</summary>
    public static bool IsSkin(string material)
    {
        var name = OutfitSwapMesh.ShortMaterialName(material);
        return name.Contains("_base_", StringComparison.Ordinal) || name.Contains("basebody", StringComparison.Ordinal) ||
               name.EndsWith("_base_mi", StringComparison.Ordinal);
    }

    /// <summary>Lower-body skin (legs) or upper-body skin (chest, arms).</summary>
    public static bool IsLowerSkin(string material) => OutfitSwapMesh.ShortMaterialName(material).Contains("lower", StringComparison.Ordinal);

    /// <summary>
    /// Whether a picture belongs to the piece's own class (pc_wbk_f_… for a Slayer piece), so repainting it changes nothing
    /// of another class's outfits that happen to share it. Pictures shared with the whole game do not start with pc_.
    /// </summary>
    public static bool OwnsPicture(string objectName, string texture) =>
        ClassPrefix().Match(StripLocale(objectName)) is { Success: true } match &&
        texture.StartsWith(match.Value, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^pc_[a-z]{2,3}(_[mf])?_", RegexOptions.IgnoreCase)]
    private static partial Regex ClassPrefix();

    /// <summary>The pieces of one outfit by their part (upper, lower, dress…), from object names.</summary>
    public static string? OutfitStem(string objectName) => BodyProfiles.OutfitStem(StripLocale(objectName));

    /// <summary>pc_mbd_hr_00a_upper_sk_loc_int → pc_mbd_hr_00a_upper_sk: the worn version of some class skins carries a region suffix.</summary>
    public static string StripLocale(string objectName) => LocaleSuffix().Replace(objectName, "");

    [GeneratedRegex(@"_(loc_[a-z]+|chn)$", RegexOptions.IgnoreCase)]
    private static partial Regex LocaleSuffix();

    /// <summary>The part an outfit piece covers: dress, upper or lower; other parts (arms, helmets) are not swapped.</summary>
    public static string? Part(string objectName)
    {
        var match = PartName().Match(StripLocale(objectName));
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"_(dress|upper|lower)\d*_sk$", RegexOptions.IgnoreCase)]
    private static partial Regex PartName();
}
