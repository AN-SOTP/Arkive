using System.Globalization;
using System.Text.RegularExpressions;

namespace Arkive.Core.Proportions;

/// <summary>
/// The slider settings a Simple upscale mod was made with, so it can be reopened in the proportion editor. EveryOutfit
/// marks a body-wide mod (Apply preset to every outfit of its body), which is saved again over every outfit of its body.
/// </summary>
public sealed record BodyProportionModSettings(
    BodyProfile Profile,
    IReadOnlyDictionary<string, double> Settings,
    BodyProportionFollow Follow,
    bool EveryOutfit = false);

/// <summary>
/// Stores a Simple upscale mod's settings in its tags ("upscale:body=female-warrior", "upscale:chest=25",
/// "upscale:follow=clothing+hair", "upscale:scope=every-outfit"), which travel with the .arkive file, and reads them back. Mods made before the tags
/// existed are read from the description Arkive wrote for them ("Body proportions for Female Warrior: Chest size +25%, …").
/// </summary>
public static partial class BodyProportionModTags
{
    private const string Prefix = "upscale:";

    /// <summary>
    /// The description of a Simple upscale mod: the changed sliders, what follows the body and its pieces (only a count
    /// past 20, since a mod's description holds at most 8,000 characters and body-wide mods have hundreds of pieces).
    /// </summary>
    public static string Description(BodyProportionModSettings settings, IReadOnlyList<string> pieceNames)
    {
        var changes = settings.Profile.Zones
            .Where(zone => settings.Settings.TryGetValue(zone.Id, out var value) && value != 0)
            .Select(zone => $"{zone.Label} {settings.Settings[zone.Id].ToString("+0;-0", CultureInfo.InvariantCulture)}%");
        var outfits = pieceNames.Select(name => BodyProfiles.OutfitStem(name) ?? name).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return $"Body proportions for {settings.Profile.DisplayName}: {string.Join(", ", changes)}" +
               (settings.Follow.LooseClothing ? "; loose clothing follows the body" : "") +
               (settings.Follow.Hair ? "; hair follows the body" : "") +
               (pieceNames.Count <= 20
                   ? $". Pieces: {string.Join(", ", pieceNames)}."
                   : $". {pieceNames.Count.ToString("N0", CultureInfo.InvariantCulture)} pieces of {outfits.ToString("N0", CultureInfo.InvariantCulture)} outfits.");
    }

    public static IReadOnlyList<string> Write(BodyProportionModSettings settings)
    {
        var follow = string.Join('+', new[] { settings.Follow.LooseClothing ? "clothing" : null, settings.Follow.Hair ? "hair" : null }
            .OfType<string>().DefaultIfEmpty("none"));
        return [
            $"{Prefix}body={settings.Profile.Id}",
            $"{Prefix}follow={follow}",
            .. settings.EveryOutfit ? [$"{Prefix}scope=every-outfit"] : Array.Empty<string>(),
            .. settings.Profile.Zones
                .Where(zone => settings.Settings.TryGetValue(zone.Id, out var value) && Math.Abs(value) > 1e-6)
                .Select(zone => $"{Prefix}{zone.Id}={settings.Settings[zone.Id].ToString("0.##", CultureInfo.InvariantCulture)}"),
        ];
    }

    /// <summary>The mod's other tags, without its upscale settings.</summary>
    public static IReadOnlyList<string> Others(IReadOnlyList<string>? tags) =>
        (tags ?? []).Where(tag => !tag.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>
    /// The settings of a Simple upscale mod, or null for any other mod. Body-wide mods saved before the scope tag
    /// existed are known by the name Arkive gave them ("preset 1 · every Female Warrior outfit").
    /// </summary>
    public static BodyProportionModSettings? Read(IReadOnlyList<string>? tags, string? description, string? name)
    {
        var settings = Read(tags, description);
        return settings is { EveryOutfit: false, Profile.IsNpc: false } && name is not null &&
               name.EndsWith($"every {settings.Profile.DisplayName} outfit", StringComparison.OrdinalIgnoreCase)
            ? settings with { EveryOutfit = true }
            : settings;
    }

    /// <summary>The settings of a Simple upscale mod, or null for any other mod.</summary>
    public static BodyProportionModSettings? Read(IReadOnlyList<string>? tags, string? description)
    {
        var values = (tags ?? []).Where(tag => tag.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            .Select(tag => tag[Prefix.Length..].Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
        if (values.TryGetValue("body", out var bodyId) && BodyProfiles.All.FirstOrDefault(profile => profile.Id == bodyId) is { } body)
        {
            var follow = values.GetValueOrDefault("follow", "clothing+hair").Split('+');
            var settings = body.Zones
                .Where(zone => values.TryGetValue(zone.Id, out var text) &&
                               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                .ToDictionary(zone => zone.Id, zone => double.Parse(values[zone.Id], CultureInfo.InvariantCulture), StringComparer.Ordinal);
            return new BodyProportionModSettings(body, settings,
                new BodyProportionFollow(follow.Contains("clothing", StringComparer.OrdinalIgnoreCase), follow.Contains("hair", StringComparer.OrdinalIgnoreCase)),
                values.GetValueOrDefault("scope")?.Equals("every-outfit", StringComparison.OrdinalIgnoreCase) == true);
        }
        return description is null ? null : ReadDescription(description);
    }

    private static BodyProportionModSettings? ReadDescription(string description)
    {
        var match = Description().Match(description);
        if (!match.Success || BodyProfiles.All.FirstOrDefault(profile =>
                profile.DisplayName.Equals(match.Groups["body"].Value, StringComparison.OrdinalIgnoreCase)) is not { } body)
            return null;
        var settings = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var part in match.Groups["changes"].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var change = Change().Match(part.Trim());
            if (change.Success && body.Zones.FirstOrDefault(zone => zone.Label.Equals(change.Groups["label"].Value, StringComparison.OrdinalIgnoreCase)) is { } zone)
                settings[zone.Id] = double.Parse(change.Groups["value"].Value, CultureInfo.InvariantCulture);
        }
        var rest = match.Groups["rest"].Value;
        return settings.Count == 0 ? null : new BodyProportionModSettings(body, settings, new BodyProportionFollow(
            rest.Contains("loose clothing follows the body", StringComparison.OrdinalIgnoreCase),
            rest.Contains("hair follows the body", StringComparison.OrdinalIgnoreCase)));
    }

    [GeneratedRegex(@"^Body proportions for (?<body>[^:]+): (?<changes>[^;.]*)(?<rest>[^.]*)\.")]
    private static partial Regex Description();

    [GeneratedRegex(@"^(?<label>.+) (?<value>[+-]\d+(?:\.\d+)?)%$")]
    private static partial Regex Change();
}
