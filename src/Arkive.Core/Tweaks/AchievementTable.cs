using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Arkive.Core.Tweaks;

/// <summary>
/// The game's hidden achievements (EFTable_Achievement rows with Hidden set), with their English names, conditions,
/// objectives (EFTable_AchievementObjective) and rewards, for Hidden Achievements Revealed. The game ships all of this
/// text; its Achievements window only swaps the condition for "Complete the hidden achievement to see this info."
/// </summary>
public static partial class AchievementTable
{
    /// <summary>
    /// The tweak's options: "hidden", one text per hidden achievement, "name|condition|shared|reward|objectives" (shared
    /// "1" when a shown achievement has the same name; objectives by level split with "^", within a level with "~"), and
    /// "guide", the game's text in place of a hidden condition. Kept in <paramref name="workDirectory"/> per data2.lpk.
    /// </summary>
    public static Dictionary<string, JsonElement> Options(string data2Path, string workDirectory)
    {
        var file = new FileInfo(data2Path);
        var cache = Path.Combine(workDirectory, $"achievements-{CacheVersion}-{file.Length}-{file.LastWriteTimeUtc.Ticks}.json");
        if (File.Exists(cache))
        {
            var kept = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(cache));
            if (kept is not null) return kept;
        }
        var options = CombatPowerTable.WithDatabases(data2Path, workDirectory,
            ["EFTable_Achievement.db", "EFTable_AchievementObjective.db", "EFTable_GameMsg.db", "EFTable_Item.db", "EFTable_Npc.db"], connection =>
                new Dictionary<string, object>
                {
                    ["hidden"] = Hidden(connection),
                    ["guide"] = Text(connection, "sys.achievement.ui_guide_text_hidden") ?? "",
                });
        Directory.CreateDirectory(workDirectory);
        foreach (var stale in Directory.EnumerateFiles(workDirectory, "achievements-*.json")) File.Delete(stale);
        var json = JsonSerializer.Serialize(options);
        File.WriteAllText(cache, json);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }

    private const int CacheVersion = 2;

    private static string[] Hidden(SqliteConnection connection)
    {
        var shown = new HashSet<string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT Name FROM Achievement WHERE Hidden = 0";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (Text(connection, reader.GetString(0)) is { } name) shown.Add(Clean(Macros(connection, name)));
        }
        var found = new List<string>();
        using (var command = connection.CreateCommand())
        {
            // Levels share the name and condition; the first level carries the reward.
            command.CommandText = """
                SELECT PrimaryKey, Name, Desc, RewardTitleId, RewardItemId FROM Achievement
                WHERE Hidden = 1 AND Enabled = 1 AND SecondaryKey = 1 ORDER BY SortOrder, PrimaryKey
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = Clean(Macros(connection, Text(connection, reader.GetString(1))));
                var condition = Clean(Macros(connection, Text(connection, reader.GetString(2))));
                if (name.Length == 0 || condition.Length == 0) continue;
                var reward = Reward(connection, reader.GetInt32(3), reader.GetInt32(4));
                found.Add(string.Join('|', name, condition, shown.Contains(name) ? "1" : "", reward, Objectives(connection, reader.GetInt32(0))));
            }
        }
        return found.ToArray();
    }

    private static string Reward(SqliteConnection connection, int title, int item)
    {
        if (title > 0 && Clean(Text(connection, $"tip.name.honortitle_{title}")) is { Length: > 0 } honour)
            return $"Title [{honour}]";
        if (item <= 0) return "";
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM db3.Item WHERE PrimaryKey = $id LIMIT 1";
        command.Parameters.AddWithValue("$id", item);
        return command.ExecuteScalar() is string key ? Clean(Text(connection, key)) : "";
    }

    private static string Objectives(SqliteConnection connection, int achievement)
    {
        var levels = new SortedDictionary<int, List<string>>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SecondaryKey, Name FROM db1.AchievementObjective WHERE PrimaryKey = $id ORDER BY SecondaryKey, [Index]";
        command.Parameters.AddWithValue("$id", achievement);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var text = Clean(Macros(connection, Text(connection, reader.GetString(1))));
            if (text.Length == 0) continue;
            if (!levels.TryGetValue(reader.GetInt32(0), out var level)) levels[reader.GetInt32(0)] = level = [];
            level.Add(text);
        }
        return string.Join('^', levels.Values.Select(level => string.Join('~', level)));
    }

    /// <summary>
    /// The game's text macros filled in: Achievement_Npc (the objective's monster name), Achievement_Count (its count) and
    /// Achievement_Quest (quest names aren't in data2, so "a quest"), each naming the achievement (@1), level (@2) and
    /// objective (@3, default 0).
    /// </summary>
    private static string? Macros(SqliteConnection connection, string? text)
    {
        if (text is null || !text.Contains("<$MACRO", StringComparison.Ordinal)) return text;
        const string Quest = "\u0001";
        return Macro().Replace(text, match =>
        {
            int Argument(string name) =>
                Regex.Match(match.Groups[2].Value, $@"@{name}:(\d+)") is { Success: true } found ? int.Parse(found.Groups[1].Value) : 0;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ObjNpcId, ObjCount FROM db1.AchievementObjective WHERE PrimaryKey = $id AND SecondaryKey = $level AND [Index] = $index";
            command.Parameters.AddWithValue("$id", Argument("1"));
            command.Parameters.AddWithValue("$level", Math.Max(1, Argument("2")));
            command.Parameters.AddWithValue("$index", Argument("3"));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return "";
            switch (match.Groups[1].Value)
            {
                case "Achievement_Count":
                    return reader.GetInt32(1).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
                case "Achievement_Npc":
                    using (var npc = connection.CreateCommand())
                    {
                        npc.CommandText = "SELECT Name FROM db4.Npc WHERE PrimaryKey = $id LIMIT 1";
                        npc.Parameters.AddWithValue("$id", reader.GetInt32(0));
                        return npc.ExecuteScalar() is string key ? Text(connection, key) ?? "" : "";
                    }
                case "Achievement_Quest":
                    return Quest;
                default:
                    return "";
            }
        }).Replace($"the {Quest} quest", "a quest").Replace($"'{Quest}' quest", "A quest").Replace($"\"{Quest}\" quest", "A quest")
            .Replace(Quest, "a quest");
    }

    [GeneratedRegex(@"<\$MACRO\s+(\w+)([^>]*)>")]
    private static partial Regex Macro();

    private static string? Text(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MSG FROM db2.GameMsg_English WHERE KEY = $key";
        command.Parameters.AddWithValue("$key", key.ToLowerInvariant());
        return command.ExecuteScalar() as string;
    }

    /// <summary>One line of plain text: no tags, no line breaks, none of the separators the options use.</summary>
    private static string Clean(string? text) =>
        text is null ? "" : Spaces().Replace(Tags().Replace(text, "").Replace('|', '/').Replace('^', ' ').Replace('~', '-'), " ").Trim();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
