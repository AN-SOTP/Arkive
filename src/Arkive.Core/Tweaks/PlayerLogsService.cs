using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Arkive.Core.Tweaks;

public enum PlayerLogsStatus
{
    /// <summary>The character's page was found; its public logs are in the lookup (possibly none).</summary>
    Found,

    /// <summary>The character exists but its owner keeps their logs private.</summary>
    Hidden,

    /// <summary>No region has a character by this name.</summary>
    NotFound,
}

/// <summary>One log of a character, as lostark.bible lists it.</summary>
public sealed record PlayerLog(string Boss, string Difficulty, string ClassName, string Spec, double Dps, double? Percentile,
    long DurationMs, DateTimeOffset When, bool Died, IReadOnlyList<double>? Buffs = null)
{
    /// <summary>A support's log (lostark.bible tells them by spec), which shows buff uptimes instead of DPS.</summary>
    public bool IsSupport => Spec is "Desperate Salvation" or "Full Bloom" or "Blessed Aura" or "Liberator";
}

/// <summary>A character's logs: its region and ids on lostark.bible (for raid filters), and the logs found.</summary>
public sealed record PlayerLogsLookup(string Name, string? Region, PlayerLogsStatus Status, IReadOnlyList<PlayerLog> Logs,
    long CharacterId = 0, long RosterId = 0);

/// <summary>A raid as lostark.bible's raid filter knows it: its bosses, and which gate each boss is.</summary>
public sealed record PlayerLogsRaid(string Name, IReadOnlyList<string> Bosses);

/// <summary>
/// Reads a character's logs from lostark.bible for the Logs tab: EU Central first, then North America. The site has
/// no public API; this reads the data file its own logs page loads (SvelteKit's __data.json, "devalue" encoded: each
/// node's data is a flat array whose objects hold indexes into it), and for one raid the request its raid filter
/// sends. One or two requests, only when a player opens the tab.
/// </summary>
public sealed class PlayerLogsService(HttpClient client)
{
    private const string Site = "https://lostark.bible";

    public static IReadOnlyList<string> Regions { get; } = ["CE", "NA"];

    /// <summary>The raids of the site's raid filter, in its order (the tab's raid number is the position + 1).</summary>
    public static IReadOnlyList<PlayerLogsRaid> Raids { get; } =
    [
        new("Belgardin", ["Belgardin, Arbiter of Death", "Phaetus An Kragma"]),
        new("Horizon Cathedral", ["Archbishop Arcenos", "Arcenos, Vanguard of Fanaticism"]),
        new("Serca", ["Corvus Tul Rak", "Witch of Agony, Serca"]),
        new("Kazeros", ["Death Incarnate Kazeros", "Archdemon Kazeros", "Abyss Lord Kazeros"]),
        new("Armoche", ["Brelshaza, Ember in the Ashes", "Armoche, Sentinel of the Abyss"]),
        new("Tarkal", ["Flame of Darkness, Tarkal"]),
        new("Mordum", ["Mordum, the Abyssal Punisher", "Flash of Punishment", "Blossoming Fear, Naitreya", "Infernas"]),
        new("Act 2: Brelshaza", ["Phantom Manifester Brelshaza", "Narok the Butcher"]),
        new("Aegir", ["Aegir, the Oppressor", "Akkan, Lord of Death"]),
        new("Behemoth", ["Behemoth, Cruel Storm Slayer", "Behemoth, the Storm Commander"]),
        new("Echidna", ["Covetous Master Echidna", "Red Doom Narkiel"]),
        new("Thaemine", ["Thaemine, Conqueror of Stars", "Thaemine the Lightqueller", "Valinak, Herald of the End", "Killineza the Dark Worshipper"]),
        new("Ivory Tower", ["Lazaram, the Trailblazer", "Firehorn, Trampler of Earth", "Rakathus, the Lurking Arrogance", "Kaltaya, the Blooming Chaos"]),
        new("Akkan", ["Lord of Kartheon Akkan", "Plague Legion Commander Akkan", "Lord of Degradation Akkan", "Griefbringer Maurug"]),
        new("Kayangel", ["Lauriel", "Prunya", "Tienis"]),
        new("Brelshaza", ["Phantom Legion Commander Brelshaza", "Brelshaza, Monarch of Nightmares", "Primordial Nightmare", "Ashtarot", "Gehenna Helkasirs"]),
        new("Kakul-Saydon", ["Kakul-Saydon", "Kakul", "Saydon", "Encore-Desiring Kakul-Saydon"]),
        new("Vykas", ["Covetous Legion Commander Vykas", "Covetous Devourer Vykas", "Nightmarish Morphe", "Incubus Morphe"]),
        new("Valtan", ["Ravaged Tyrant of Beasts", "Dark Mountain Predator"]),
    ];

    /// <summary>The gate each boss is, as the site labels it.</summary>
    private static readonly Dictionary<string, string> Gates = new(StringComparer.Ordinal)
    {
        ["Belgardin, Arbiter of Death"] = "G1", ["Phaetus An Kragma"] = "G2",
        ["Witch of Agony, Serca"] = "G1", ["Corvus Tul Rak"] = "G2",
        ["Archbishop Arcenos"] = "G1", ["Arcenos, Vanguard of Fanaticism"] = "G2",
        ["Abyss Lord Kazeros"] = "G1", ["Archdemon Kazeros"] = "G2", ["Death Incarnate Kazeros"] = "G2",
        ["Brelshaza, Ember in the Ashes"] = "G1", ["Armoche, Sentinel of the Abyss"] = "G2",
        ["Flame of Darkness, Tarkal"] = "G1",
        ["Infernas"] = "G1", ["Blossoming Fear, Naitreya"] = "G2", ["Flash of Punishment"] = "G3", ["Mordum, the Abyssal Punisher"] = "G3",
        ["Narok the Butcher"] = "G1", ["Phantom Manifester Brelshaza"] = "G2",
        ["Akkan, Lord of Death"] = "G1", ["Aegir, the Oppressor"] = "G2",
        ["Behemoth, the Storm Commander"] = "G1", ["Behemoth, Cruel Storm Slayer"] = "G2",
        ["Red Doom Narkiel"] = "G1", ["Covetous Master Echidna"] = "G2",
        ["Killineza the Dark Worshipper"] = "G1", ["Valinak, Herald of the End"] = "G2", ["Thaemine the Lightqueller"] = "G3", ["Thaemine, Conqueror of Stars"] = "G4",
        ["Kaltaya, the Blooming Chaos"] = "G1", ["Rakathus, the Lurking Arrogance"] = "G2", ["Firehorn, Trampler of Earth"] = "G3", ["Lazaram, the Trailblazer"] = "G4",
        ["Griefbringer Maurug"] = "G1", ["Lord of Degradation Akkan"] = "G2", ["Plague Legion Commander Akkan"] = "G3", ["Lord of Kartheon Akkan"] = "G3",
        ["Tienis"] = "G1", ["Prunya"] = "G2", ["Lauriel"] = "G3",
        ["Gehenna Helkasirs"] = "G1", ["Ashtarot"] = "G2", ["Primordial Nightmare"] = "G3", ["Brelshaza, Monarch of Nightmares"] = "G5", ["Phantom Legion Commander Brelshaza"] = "G6",
        ["Saydon"] = "G1", ["Kakul"] = "G2", ["Kakul-Saydon"] = "G3", ["Encore-Desiring Kakul-Saydon"] = "G3",
        ["Nightmarish Morphe"] = "G1", ["Incubus Morphe"] = "G1", ["Covetous Devourer Vykas"] = "G2", ["Covetous Legion Commander Vykas"] = "G3",
        ["Dark Mountain Predator"] = "G1", ["Ravaged Tyrant of Beasts"] = "G2",
    };

    public static string RaidOf(string boss) => Raids.FirstOrDefault(raid => raid.Bosses.Contains(boss))?.Name ?? "Other";

    public static string GateOf(string boss) => Gates.GetValueOrDefault(boss, "");

    /// <summary>The character's most recent logs (the logs page), EU Central first, then North America.</summary>
    public async Task<PlayerLogsLookup> FetchAsync(string name, CancellationToken cancellationToken = default)
    {
        PlayerLogsLookup? empty = null;
        string? suggested = null;
        foreach (var region in Regions)
        {
            var page = await GetAsync(region, name, cancellationToken);
            if (page is null)
                continue;
            var lookup = Read(name, region, page.Value);
            suggested ??= SuggestedRegion(name, page.Value);
            if (lookup is null)
                continue;
            if (lookup.Status == PlayerLogsStatus.Found && lookup.Logs.Count == 0)
            {
                empty ??= lookup;
                continue;
            }
            return lookup;
        }
        // A page for a name that isn't in that region suggests where it is (the NA page says CE for an EU character).
        if (empty is null && suggested is not null && !Regions.Contains(suggested))
        {
            var page = await GetAsync(suggested, name, cancellationToken);
            if (page is not null && Read(name, suggested, page.Value) is { } lookup)
                return lookup;
        }
        return empty ?? new PlayerLogsLookup(name, null, PlayerLogsStatus.NotFound, []);
    }

    /// <summary>
    /// The character's logs of one raid, as the site's raid filter shows them (its first page). Only for a character
    /// whose logs were found; otherwise the lookup itself.
    /// </summary>
    public async Task<PlayerLogsLookup> FetchRaidAsync(PlayerLogsLookup character, PlayerLogsRaid raid, CancellationToken cancellationToken = default)
    {
        if (character.Status != PlayerLogsStatus.Found || character.CharacterId == 0)
            return character;
        using var response = await client.PostAsJsonAsync($"{Site}/api/character/logs",
            new { region = character.Region, cid = character.CharacterId, rid = character.RosterId, bosses = raid.Bosses, page = 1 },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("lostark.bible returned more data than a logs page holds.");
        using var document = JsonDocument.Parse(bytes);
        var rows = new List<PlayerLog>();
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var row in document.RootElement.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Object)
                    rows.Add(Log(property => row.TryGetProperty(property, out var value) ? value : default, item => item));
        return character with { Logs = rows };
    }

    private async Task<JsonElement?> GetAsync(string region, string name, CancellationToken cancellationToken)
    {
        var url = $"{Site}/character/{region}/{Uri.EscapeDataString(name)}/logs/__data.json";
        using var response = await client.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("lostark.bible returned more data than a logs page holds.");
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The character and its logs in a logs page's data, or null when the page has no character by that name. A page
    /// whose logs node says the logs are off or not public is a character with hidden logs (Zurmos, for one).
    /// </summary>
    public static PlayerLogsLookup? Read(string name, string region, JsonElement page)
    {
        long characterId = 0, rosterId = 0;
        var exists = false;
        foreach (var node in Nodes(page))
        {
            var root = node[0];
            if (root.ValueKind == JsonValueKind.Object && Resolve(node, root, "header") is { ValueKind: JsonValueKind.Object } header)
            {
                exists = true;
                characterId = (long)(Number(node, header, "id") ?? 0);
                rosterId = (long)(Number(node, header, "rid") ?? 0);
            }
        }
        foreach (var node in Nodes(page))
        {
            var root = node[0];
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("logsEnabled", out _))
                continue;
            var enabled = Resolve(node, root, "logsEnabled");
            var isPublic = Resolve(node, root, "isPublic");
            if (enabled.ValueKind != JsonValueKind.True || isPublic.ValueKind == JsonValueKind.False || !root.TryGetProperty("logs", out var logsIndex))
                return exists || enabled.ValueKind == JsonValueKind.False
                    ? new PlayerLogsLookup(name, region, PlayerLogsStatus.Hidden, [], characterId, rosterId)
                    : null;
            var logs = At(node, logsIndex);
            var rows = new List<PlayerLog>();
            if (logs.ValueKind == JsonValueKind.Array)
                foreach (var item in logs.EnumerateArray())
                {
                    var row = At(node, item);
                    if (row.ValueKind == JsonValueKind.Object)
                        rows.Add(Log(property => Resolve(node, row, property), item => At(node, item)));
                }
            return new PlayerLogsLookup(name, region, PlayerLogsStatus.Found, rows, characterId, rosterId);
        }
        return null;
    }

    /// <summary>A log row from its fields (devalue-resolved on the logs page, plain in the raid filter's answer).</summary>
    /// <param name="item">Resolves an array item (a devalue index on the logs page, the value itself in the raid filter's answer).</param>
    private static PlayerLog Log(Func<string, JsonElement> field, Func<JsonElement, JsonElement> item)
    {
        string Text(string property) => field(property) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";
        double? Number(string property) => field(property) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;
        var buffs = field("buffs") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Select(item).Select(value => value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0).ToArray()
            : null;
        return new PlayerLog(Text("boss"), Text("difficulty"), Text("class"), Text("spec"), Number("dps") ?? 0, Number("percentile"),
            (long)(Number("duration") ?? 0), DateTimeOffset.FromUnixTimeMilliseconds((long)(Number("timestamp") ?? 0)),
            field("isDead").ValueKind == JsonValueKind.True, buffs);
    }

    /// <summary>The region of an exact-name suggestion on a page that didn't find the character, or null.</summary>
    public static string? SuggestedRegion(string name, JsonElement page)
    {
        foreach (var node in Nodes(page))
        {
            var root = node[0];
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("suggestions", out var index))
                continue;
            var suggestions = At(node, index);
            if (suggestions.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in suggestions.EnumerateArray())
            {
                var suggestion = At(node, item);
                if (suggestion.ValueKind == JsonValueKind.Object &&
                    string.Equals(Text(node, suggestion, "name"), name, StringComparison.OrdinalIgnoreCase))
                    return Text(node, suggestion, "region") is { Length: > 0 } region ? region : null;
            }
        }
        return null;
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement page)
    {
        if (!page.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var node in nodes.EnumerateArray())
            if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                yield return data;
    }

    /// <summary>A devalue reference: an index into the node's data (negative indexes are undefined, NaN and the like).</summary>
    private static JsonElement At(JsonElement data, JsonElement reference) =>
        reference.ValueKind == JsonValueKind.Number && reference.TryGetInt32(out var index) && index >= 0 && index < data.GetArrayLength()
            ? data[index]
            : default;

    private static JsonElement Resolve(JsonElement data, JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var reference) ? At(data, reference) : default;

    private static string Text(JsonElement data, JsonElement owner, string property) =>
        Resolve(data, owner, property) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";

    private static double? Number(JsonElement data, JsonElement owner, string property) =>
        Resolve(data, owner, property) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    /// <summary>
    /// The answer the Logs tab draws, one line per row with tab-separated fields, already formatted so the tab's code
    /// only splits and draws. First line: status, region, name, count, the tab's request id (so it never shows an
    /// answer to an earlier click) and the raid shown ("" for recent logs). Second line: "#raids" and the raid names
    /// in filter order, for the tab's raid tabs. Then per log: raid, gate, boss, difficulty, class, DPS, parse (whole
    /// percent, empty when the site has none), date, duration, "died", and for a support its buff uptimes in whole
    /// percent as lostark.bible lists them (AP, Brand, Identity, T; "95/96/74/40"), empty for anyone else.
    /// </summary>
    public static string Summary(PlayerLogsLookup lookup, string request = "", string raid = "")
    {
        var builder = new StringBuilder();
        var status = lookup.Status switch
        {
            PlayerLogsStatus.Found => "found",
            PlayerLogsStatus.Hidden => "hidden",
            _ => "notfound",
        };
        builder.Append(status).Append('\t').Append(RegionLabel(lookup.Region)).Append('\t').Append(Clean(lookup.Name))
            .Append('\t').Append(lookup.Logs.Count.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Clean(request))
            .Append('\t').Append(Clean(raid)).Append('\n');
        builder.Append("#raids");
        foreach (var known in Raids)
            builder.Append('\t').Append(known.Name);
        builder.Append('\n');
        foreach (var log in lookup.Logs)
        {
            builder.Append(Clean(RaidOf(log.Boss))).Append('\t')
                .Append(GateOf(log.Boss)).Append('\t')
                .Append(Clean(log.Boss)).Append('\t')
                .Append(Clean(log.Difficulty)).Append('\t')
                .Append(Clean(log.ClassName)).Append('\t')
                .Append(Dps(log.Dps)).Append('\t')
                .Append(log.Percentile is { } percentile ? Math.Floor(percentile * 100).ToString(CultureInfo.InvariantCulture) : "").Append('\t')
                .Append(log.When.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture)).Append('\t')
                .Append(TimeSpan.FromMilliseconds(log.DurationMs).ToString(@"m\:ss", CultureInfo.InvariantCulture)).Append('\t')
                .Append(log.Died ? "died" : "").Append('\t')
                .Append(log.IsSupport && log.Buffs is { Count: > 0 } buffs
                    ? string.Join("/", buffs.Select(buff => Math.Round(buff * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)))
                    : "").Append('\n');
        }
        return builder.ToString();
    }

    public static string RegionLabel(string? region) => region switch
    {
        "CE" => "EU Central",
        "NA" => "North America",
        null => "",
        _ => region,
    };

    /// <summary>DPS the way meters show it: 1.70b, 845.2m.</summary>
    public static string Dps(double dps) => dps switch
    {
        >= 1e9 => (dps / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + "b",
        >= 1e6 => (dps / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "m",
        >= 1e3 => (dps / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => dps.ToString("0", CultureInfo.InvariantCulture),
    };

    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
