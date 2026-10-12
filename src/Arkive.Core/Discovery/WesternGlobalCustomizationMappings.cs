using System.Text.RegularExpressions;

namespace Arkive.Core.Discovery;

/// <summary>
/// Empirically verified customization-to-asset mappings for the Western/global client.
/// These mappings deliberately live outside the extracted game database because the
/// database ordering is not the runtime face-texture ordering.
/// </summary>
public static partial class WesternGlobalCustomizationMappings
{
    public const long SlayerEntityId = 112;
    public const long FemaleMartialArtistEntityId = 301;
    public const long DragonHumanEntityId = 701;

    private static readonly IReadOnlyDictionary<int, int?> SlayerFaceTextureByUiPosition =
        new Dictionary<int, int?>
        {
            [1] = 18, [2] = 15, [3] = 0, [4] = 22, [5] = 21,
            [6] = 23, [7] = null, [8] = 24, [9] = 25, [10] = 24,
            [11] = 0, [12] = 20, [13] = null, [14] = 18, [15] = 16,
            [16] = 14, [17] = 19, [18] = 8, [19] = 17, [20] = null,
            [21] = 6, [22] = 21, [23] = 8, [24] = 18, [25] = 0,
            [26] = 22, [27] = 2, [28] = 17, [29] = 5, [30] = 18,
        };

    private static readonly IReadOnlyDictionary<int, int?> DragonHumanFaceTextureByUiPosition =
        new Dictionary<int, int?>
        {
            [1] = 28, [2] = 30, [3] = 29, [4] = 27, [5] = 34,
            [6] = 26, [7] = 0, [8] = 32, [9] = 1, [10] = 33,
            [11] = null, [12] = 34, [13] = 30, [14] = 0, [15] = 31,
            [16] = 13, [17] = 26, [18] = 1, [19] = 16, [20] = 4,
            [21] = 28, [22] = 30, [23] = 9, [24] = 27, [25] = 29,
        };

    private static readonly IReadOnlyDictionary<int, int?> FemaleMartialArtistFaceTextureByUiPosition =
        new Dictionary<int, int?>
        {
            [1] = 5, [2] = 7, [3] = 11, [4] = 15, [5] = 14,
            [6] = 15, [7] = 3, [8] = 11, [9] = 6, [10] = 1,
            [11] = 9, [12] = 8, [13] = 15, [14] = 2, [15] = 1,
            [16] = 6, [17] = 15, [18] = 8, [19] = 8, [20] = 9,
            [21] = 12, [22] = 11, [23] = 13, [24] = 14, [25] = 10,
        };

    public static bool TryGetSlayerFaceTextureNumber(int uiPosition, out int? textureNumber) =>
        SlayerFaceTextureByUiPosition.TryGetValue(uiPosition, out textureNumber);

    public static IReadOnlyList<int> FindSlayerFacePositions(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName)) return [];
        var match = SlayerFaceTextureName().Match(objectName);
        if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var textureNumber))
        {
            return [];
        }

        return SlayerFaceTextureByUiPosition
            .Where(pair => pair.Value == textureNumber)
            .Select(pair => pair.Key)
            .Order()
            .ToArray();
    }

    public static bool TryGetDragonHumanFaceTextureNumber(int uiPosition, out int? textureNumber) =>
        DragonHumanFaceTextureByUiPosition.TryGetValue(uiPosition, out textureNumber);

    public static IReadOnlyList<int> FindDragonHumanFacePositions(string objectName) =>
        FindFacePositions(objectName, DragonHumanFaceTextureName(), DragonHumanFaceTextureByUiPosition);

    public static bool TryGetFemaleMartialArtistFaceTextureNumber(
        int uiPosition,
        out int? textureNumber) =>
        FemaleMartialArtistFaceTextureByUiPosition.TryGetValue(uiPosition, out textureNumber);

    public static IReadOnlyList<int> FindFemaleMartialArtistFacePositions(string objectName) =>
        FindFacePositions(
            objectName,
            FemaleMartialArtistFaceTextureName(),
            FemaleMartialArtistFaceTextureByUiPosition);

    public static string DescribeVerifiedUses(string objectName)
    {
        var descriptions = new List<string>(3);
        var slayerPositions = FindSlayerFacePositions(objectName);
        if (slayerPositions.Count > 0)
        {
            descriptions.Add(slayerPositions.Count == 1
                ? $"Slayer face {slayerPositions[0]}"
                : $"Slayer faces {string.Join(", ", slayerPositions)}");
        }
        var dragonHumanPositions = FindDragonHumanFacePositions(objectName);
        if (dragonHumanPositions.Count > 0)
        {
            descriptions.Add(dragonHumanPositions.Count == 1
                ? $"DragonHuman face {dragonHumanPositions[0]}"
                : $"DragonHuman faces {string.Join(", ", dragonHumanPositions)}");
        }
        var femaleMartialArtistPositions = FindFemaleMartialArtistFacePositions(objectName);
        if (femaleMartialArtistPositions.Count > 0)
        {
            descriptions.Add(femaleMartialArtistPositions.Count == 1
                ? $"Female Martial Artist face {femaleMartialArtistPositions[0]}"
                : $"Female Martial Artist faces {string.Join(", ", femaleMartialArtistPositions)}");
        }
        return descriptions.Count == 0 ? "-" : string.Join("; ", descriptions);
    }

    public static IReadOnlyList<GameAssetAssociation> CreateAssociations()
    {
        var associations = new List<GameAssetAssociation>();
        foreach (var pair in SlayerFaceTextureByUiPosition.Where(pair => pair.Value.HasValue))
        {
            var face = pair.Key;
            var suffix = pair.Value!.Value;
            foreach (var channel in new[] { "d", "n", "s" })
            {
                var objectName = $"pc_wr_f_{suffix:00}_face_{channel}";
                associations.Add(new GameAssetAssociation(
                    $"Slayer face {face}",
                    $"arkive.verified.slayer.face.{face}",
                    "PC",
                    SlayerEntityId,
                    "Verified face mapping",
                    objectName,
                    "",
                    objectName)
                {
                    Archetype = "Female Warrior",
                });
            }
        }
        AddFaceAssociations(
            associations,
            DragonHumanFaceTextureByUiPosition,
            "Guardianknight / DragonHuman",
            "dragonhuman",
            "pc_dk",
            DragonHumanEntityId,
            "DragonHuman",
            numberAfterFace: false);
        AddFaceAssociations(
            associations,
            FemaleMartialArtistFaceTextureByUiPosition,
            "Female Martial Artist",
            "female-martial-artist",
            "pc_ft",
            FemaleMartialArtistEntityId,
            "Female Martial Artist",
            numberAfterFace: true);
        return associations;
    }

    public static IReadOnlyList<string> CreateSlayerFaceCandidates(int uiPosition)
    {
        if (!SlayerFaceTextureByUiPosition.TryGetValue(uiPosition, out var suffix) || suffix is null)
        {
            return [];
        }

        return new[] { "d", "n", "s" }
            .Select(channel => $"pc_wr_f_{suffix.Value:00}_face_{channel}")
            .ToArray();
    }

    public static IReadOnlyList<string> CreateDragonHumanFaceCandidates(int uiPosition)
    {
        if (!DragonHumanFaceTextureByUiPosition.TryGetValue(uiPosition, out var suffix) || suffix is null)
        {
            return [];
        }

        return new[] { "d", "n", "s" }
            .Select(channel => $"pc_dk_{suffix.Value:00}_face_{channel}")
            .ToArray();
    }

    public static IReadOnlyList<string> CreateFemaleMartialArtistFaceCandidates(int uiPosition)
    {
        if (!FemaleMartialArtistFaceTextureByUiPosition.TryGetValue(
                uiPosition, out var suffix) || suffix is null)
        {
            return [];
        }

        return new[] { "d", "n", "s" }
            .Select(channel => $"pc_ft_face_{suffix.Value:00}_{channel}")
            .ToArray();
    }

    private static IReadOnlyList<int> FindFacePositions(
        string objectName,
        Regex objectNamePattern,
        IReadOnlyDictionary<int, int?> textureByUiPosition)
    {
        if (string.IsNullOrWhiteSpace(objectName)) return [];
        var match = objectNamePattern.Match(objectName);
        if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var textureNumber))
        {
            return [];
        }

        return textureByUiPosition
            .Where(pair => pair.Value == textureNumber)
            .Select(pair => pair.Key)
            .Order()
            .ToArray();
    }

    private static void AddFaceAssociations(
        ICollection<GameAssetAssociation> associations,
        IReadOnlyDictionary<int, int?> textureByUiPosition,
        string displayName,
        string stableName,
        string objectPrefix,
        long entityId,
        string archetype,
        bool numberAfterFace)
    {
        foreach (var pair in textureByUiPosition.Where(pair => pair.Value.HasValue))
        {
            var face = pair.Key;
            var suffix = pair.Value!.Value;
            foreach (var channel in new[] { "d", "n", "s" })
            {
                var objectName = numberAfterFace
                    ? $"{objectPrefix}_face_{suffix:00}_{channel}"
                    : $"{objectPrefix}_{suffix:00}_face_{channel}";
                associations.Add(new GameAssetAssociation(
                    $"{displayName} face {face}",
                    $"arkive.verified.{stableName}.face.{face}",
                    "PC",
                    entityId,
                    "Verified face mapping",
                    objectName,
                    "",
                    objectName)
                {
                    Archetype = archetype,
                });
            }
        }
    }

    [GeneratedRegex(
        @"^pc_wr_f_(?<number>\d{1,2})_face_[dns]$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlayerFaceTextureName();

    [GeneratedRegex(
        @"^pc_dk_(?<number>\d{1,2})_face_[dns]$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DragonHumanFaceTextureName();

    [GeneratedRegex(
        @"^pc_ft_face_(?<number>\d{1,2})_[dns]$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FemaleMartialArtistFaceTextureName();
}
