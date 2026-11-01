using Microsoft.Data.Sqlite;

namespace Arkive.Core.Tweaks;

/// <summary>
/// The game's stagger numbers for skill tooltips, from data2.lpk. A skill's tooltip grade (Stagger: Low … Max) is
/// EFTable_Skill.StiffnessTooltipType; each of its hits is an EFTable_SkillEffect row numbered skill id × 10 + n whose
/// AiPointMax is the stagger it deals (the same at every skill level). A tripod that raises stagger is an
/// EFTable_SkillFeature row of type 48 for the skill and tripod slot: ParamValue1 is the percent, ParamValue0 the one
/// effect it changes (0 for every hit of the skill).
/// </summary>
public static class SkillStaggerTable
{
    /// <summary>SkillFeature's type for a tripod's stagger bonus.</summary>
    private const int StaggerFeature = 48;

    /// <summary>The GameMsg keys of the grade labels, StiffnessTooltipType 1 to 5.</summary>
    private static readonly string[] GradeKeys = ["low", "normal", "high", "higher", "highest"];

    /// <summary>
    /// Everything the stagger tweak reads, as its options: "grades" (the five labels, "Stagger: Low" first), "skills"
    /// (one text per skill with a grade and stagger, "id|grade|name|effect:stagger,…") and "tripods" (one text per tripod
    /// that changes stagger, "skill|name|effect:percent,…", effect 0 for the whole skill). Names and labels are the
    /// game's English text table, which is the client's language (Arkive's Language tab writes over it). Kept in
    /// <paramref name="workDirectory"/> per data2.lpk size and time, since reading the game's text takes a while.
    /// </summary>
    public static Dictionary<string, System.Text.Json.JsonElement> Options(string data2Path, string workDirectory)
    {
        var file = new FileInfo(data2Path);
        var cache = Path.Combine(workDirectory, $"skill-stagger-{CacheVersion}-{file.Length}-{file.LastWriteTimeUtc.Ticks}.json");
        if (File.Exists(cache))
        {
            var kept = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(File.ReadAllText(cache));
            if (kept is not null) return kept;
        }
        var options = CombatPowerTable.WithDatabases(data2Path, workDirectory,
            ["EFTable_Skill.db", "EFTable_SkillEffect.db", "EFTable_SkillFeature.db", "EFTable_GameMsg.db"], connection =>
                new Dictionary<string, object>
                {
                    ["grades"] = GradeKeys.Select(key => Text(connection, "tip.name.enum_stiffnesstooltiptype_" + key) ?? "").ToArray(),
                    ["skills"] = Skills(connection),
                    ["tripods"] = Tripods(connection),
                });
        Directory.CreateDirectory(workDirectory);
        foreach (var stale in Directory.EnumerateFiles(workDirectory, "skill-stagger-*.json")) File.Delete(stale);
        var json = System.Text.Json.JsonSerializer.Serialize(options);
        File.WriteAllText(cache, json);
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!;
    }

    /// <summary>Bumped when <see cref="Options"/> changes shape, so caches from older builds are read again.</summary>
    private const int CacheVersion = 1;

    /// <summary>Skills that show a stagger grade and have hits that stagger, with their names.</summary>
    private static string[] Skills(SqliteConnection connection)
    {
        var hits = new Dictionary<int, SortedDictionary<int, int>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT PrimaryKey, MAX(AiPointMax) FROM db1.SkillEffect GROUP BY PrimaryKey HAVING MAX(AiPointMax) > 0";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var effect = reader.GetInt32(0);
                if (!hits.TryGetValue(effect / 10, out var skill)) hits[effect / 10] = skill = [];
                skill[effect] = reader.GetInt32(1);
            }
        }
        var found = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT PrimaryKey, MAX(StiffnessTooltipType), MIN(Name) FROM Skill GROUP BY PrimaryKey " +
                                  "HAVING MAX(StiffnessTooltipType) BETWEEN 1 AND 5 ORDER BY PrimaryKey";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!hits.TryGetValue(reader.GetInt32(0), out var skill)) continue;
                var name = Text(connection, reader.GetString(2));
                if (string.IsNullOrWhiteSpace(name)) continue;
                found.Add($"{reader.GetInt32(0)}|{reader.GetInt32(1)}|{Plain(name)}|{string.Join(',', skill.Select(hit => $"{hit.Key}:{hit.Value}"))}");
            }
        }
        return found.ToArray();
    }

    /// <summary>Tripods with a stagger bonus, by skill and name.</summary>
    private static string[] Tripods(SqliteConnection connection)
    {
        var tripods = new SortedDictionary<(int Skill, int Slot), (string NameKey, List<string> Bonuses)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT PrimaryKey, SecondaryKey, NameKey, ParamValue0, ParamValue1 FROM db2.SkillFeature WHERE Type = $type";
            command.Parameters.AddWithValue("$type", StaggerFeature);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var at = (reader.GetInt32(0), reader.GetInt32(1));
                if (!tripods.TryGetValue(at, out var tripod)) tripods[at] = tripod = (reader.GetString(2), []);
                tripod.Bonuses.Add($"{reader.GetInt32(3)}:{reader.GetInt32(4)}");
            }
        }
        var found = new List<string>();
        foreach (var ((skill, _), (nameKey, bonuses)) in tripods)
        {
            var name = Text(connection, nameKey);
            if (!string.IsNullOrWhiteSpace(name))
                found.Add($"{skill}|{Plain(name)}|{string.Join(',', bonuses)}");
        }
        return found.ToArray();
    }

    /// <summary>A name as the tooltip shows it as text: tags removed, trimmed, and without the "|" the options use.</summary>
    private static string Plain(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "").Replace('|', ' ').Trim();

    private static string? Text(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MSG FROM db3.GameMsg_English WHERE KEY = $key";
        command.Parameters.AddWithValue("$key", key.ToLowerInvariant());
        return command.ExecuteScalar() as string;
    }
}
