using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Arkive.Core.Tweaks;

public enum PlayerLogsStatus
{
    /// <summary>The character's page was found; its public logs are in the lookup (possibly none).</summary>
    Found,

    /// <summary>The character exists but its owner hides their logs.</summary>
    Hidden,

    /// <summary>No region has a character by this name.</summary>
    NotFound,
}

/// <summary>One recent log of a character, as lostark.bible lists it.</summary>
public sealed record PlayerLog(string Boss, string Difficulty, string ClassName, string Spec, double Dps, double? Percentile,
    long DurationMs, DateTimeOffset When, bool Died);

public sealed record PlayerLogsLookup(string Name, string? Region, PlayerLogsStatus Status, IReadOnlyList<PlayerLog> Logs);

/// <summary>
/// Reads a character's recent logs from lostark.bible for the Logs tab: EU Central first, then North America. The site
/// has no public API; this reads the data file its own logs page loads (SvelteKit's __data.json, "devalue" encoded:
/// each node's data is a flat array whose objects hold indexes into it). One request per region, only when a player
/// opens the tab.
/// </summary>
public sealed class PlayerLogsService(HttpClient client)
{
    public static IReadOnlyList<string> Regions { get; } = ["CE", "NA"];

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

    private async Task<JsonElement?> GetAsync(string region, string name, CancellationToken cancellationToken)
    {
        var url = $"https://lostark.bible/character/{region}/{Uri.EscapeDataString(name)}/logs/__data.json";
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

    /// <summary>The logs in a page's data, or null when the page has no logs node (no character by that name there).</summary>
    public static PlayerLogsLookup? Read(string name, string region, JsonElement page)
    {
        foreach (var node in Nodes(page))
        {
            var root = node[0];
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("logs", out var logsIndex))
                continue;
            var enabled = Resolve(node, root, "logsEnabled");
            var isPublic = Resolve(node, root, "isPublic");
            if (enabled.ValueKind == JsonValueKind.False || isPublic.ValueKind == JsonValueKind.False)
                return new PlayerLogsLookup(name, region, PlayerLogsStatus.Hidden, []);
            var logs = At(node, logsIndex);
            var rows = new List<PlayerLog>();
            if (logs.ValueKind == JsonValueKind.Array)
                foreach (var item in logs.EnumerateArray())
                {
                    var row = At(node, item);
                    if (row.ValueKind != JsonValueKind.Object)
                        continue;
                    rows.Add(new PlayerLog(
                        Text(node, row, "boss"), Text(node, row, "difficulty"), Text(node, row, "class"), Text(node, row, "spec"),
                        Number(node, row, "dps") ?? 0, Number(node, row, "percentile"),
                        (long)(Number(node, row, "duration") ?? 0),
                        DateTimeOffset.FromUnixTimeMilliseconds((long)(Number(node, row, "timestamp") ?? 0)),
                        Resolve(node, row, "isDead").ValueKind == JsonValueKind.True));
                }
            return new PlayerLogsLookup(name, region, PlayerLogsStatus.Found, rows);
        }
        return null;
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
    /// The file the Logs tab reads for a character, under Arkive's data folder. The name is written as hex character
    /// codes, which the tab's code builds the same way, so names with accents never reach the game's file reader.
    /// </summary>
    public static string SummaryFileName(string name)
    {
        var builder = new StringBuilder(name.Length * 4 + 4);
        foreach (var character in name.ToLowerInvariant())
            builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
        return builder.Append(".txt").ToString();
    }

    /// <summary>
    /// The summary the Logs tab shows, one line per row with tab-separated fields, already formatted so the tab's code
    /// only splits and draws. First line: status, region, name, count, and the tab's request id (so it never shows an
    /// answer to an earlier click). Then: boss, difficulty, class, DPS, percentile, date, duration, died.
    /// </summary>
    public static string Summary(PlayerLogsLookup lookup, string request = "")
    {
        var builder = new StringBuilder();
        var status = lookup.Status switch
        {
            PlayerLogsStatus.Found => "found",
            PlayerLogsStatus.Hidden => "hidden",
            _ => "notfound",
        };
        builder.Append(status).Append('\t').Append(RegionLabel(lookup.Region)).Append('\t').Append(Clean(lookup.Name))
            .Append('\t').Append(lookup.Logs.Count.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Clean(request)).Append('\n');
        foreach (var log in lookup.Logs)
        {
            builder.Append(Clean(log.Boss)).Append('\t')
                .Append(Clean(log.Difficulty)).Append('\t')
                .Append(Clean(log.ClassName)).Append('\t')
                .Append(Dps(log.Dps)).Append('\t')
                .Append(log.Percentile is { } percentile ? Math.Round(percentile * 100).ToString(CultureInfo.InvariantCulture) : "").Append('\t')
                .Append(log.When.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture)).Append('\t')
                .Append(TimeSpan.FromMilliseconds(log.DurationMs).ToString(@"m\:ss", CultureInfo.InvariantCulture)).Append('\t')
                .Append(log.Died ? "died" : "").Append('\n');
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
