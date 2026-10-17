namespace Arkive.Core.Discovery;

/// <summary>
/// The class names players know. The game's database still calls many classes by the English rendering of their
/// original Korean names (LanceMaster, Kimaster…); those keep their database name as the class type and are shown with
/// their localised name beside it.
/// </summary>
public static class GameClassNames
{
    private static readonly IReadOnlyDictionary<string, string> Localised =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Battlemaster"] = "Wardancer",
            ["Male Battlemaster"] = "Striker",
            ["Kimaster"] = "Soulfist",
            ["LanceMaster"] = "Glaivier",
            ["ElementalMaster"] = "Sorceress",
            ["Hawkeye"] = "Sharpshooter",
            ["Devilhunter"] = "Deadeye",
            ["Female Devilhunter"] = "Gunslinger",
            ["WeatherArtist"] = "Aeromancer",
            ["SoulEater"] = "Souleater",
            ["DragonKnight"] = "Guardianknight",
            // The race body every Guardianknight outfit is made for, as Female Assassin is for the Assassin classes.
            ["DragonHuman"] = "Guardianknight base",
        };

    /// <summary>The localised name of a class type, or null when the database name is already the one players know.</summary>
    public static string? LocalisedName(string archetype) => Localised.GetValueOrDefault(archetype);

    /// <summary>"Glaivier (LanceMaster)" for a class with a localised name, otherwise the class type itself.</summary>
    public static string Label(string archetype) => LocalisedName(archetype) switch
    {
        null => archetype,
        var localised when localised.Equals(archetype, StringComparison.OrdinalIgnoreCase) => localised,
        var localised => $"{localised} ({archetype})",
    };
}
