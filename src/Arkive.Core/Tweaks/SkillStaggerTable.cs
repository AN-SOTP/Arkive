using Microsoft.Data.Sqlite;

namespace Arkive.Core.Tweaks;

/// <summary>
/// The game's stagger numbers for skill tooltips, from data2.lpk. A skill's tooltip grade (Stagger: Low … Max) is
/// EFTable_Skill.StiffnessTooltipType; each of its hits is an EFTable_SkillEffect row whose AiPointMax is the stagger it
/// deals (the same at every skill level). Hits numbered skill id × 10 + 0-9 are the skill's own; those at + 10-99 come
/// with a tripod: the one whose EFTable_SkillFeature rows name the hit (else the slot in the tens digit, as with most).
/// A tripod that raises stagger has a SkillFeature row of type 48 for the skill and tripod slot: ParamValue1 is the
/// percent, ParamValue0 the one effect it changes (0 for every hit of the skill). Checked against LOA Logs' recorded
/// hits: tripod hits explain 84% of staggering hits, against 62% with the skill's own hits only.
/// </summary>
public static class SkillStaggerTable
{
    /// <summary>SkillFeature's type for a tripod's stagger bonus.</summary>
    private const int StaggerFeature = 48;

    /// <summary>SkillFeature's type for a tripod that raises its hits' weak point level.</summary>
    private const int WeakPointFeature = 38;

    /// <summary>The GameMsg keys of the grade labels, StiffnessTooltipType 1 to 5.</summary>
    private static readonly string[] GradeKeys = ["low", "normal", "high", "higher", "highest"];

    /// <summary>
    /// Everything the stagger tweak reads, as its options: "grades" (the five labels, "Stagger: Low" first), "skills"
    /// (one text per skill with a grade and stagger, "id|grade|name|effect:stagger,…", a tripod's hit as
    /// "effect:stagger:slot", or "slot/slot" when several tripods bring it) and "tripods" (one text per tripod that brings
    /// hits or raises stagger, "skill|slot|name|effect:percent,…", effect 0 for the whole skill). Names and labels are the
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
            {
                var (skills, tripods) = Read(connection);
                return new Dictionary<string, object>
                {
                    ["grades"] = GradeKeys.Select(key => Text(connection, "tip.name.enum_stiffnesstooltiptype_" + key) ?? "").ToArray(),
                    ["skills"] = skills,
                    ["tripods"] = tripods,
                };
            });
        Directory.CreateDirectory(workDirectory);
        foreach (var stale in Directory.EnumerateFiles(workDirectory, "skill-stagger-*.json")) File.Delete(stale);
        var json = System.Text.Json.JsonSerializer.Serialize(options);
        File.WriteAllText(cache, json);
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!;
    }

    /// <summary>Bumped when <see cref="Options"/> changes shape, so caches from older builds are read again.</summary>
    private const int CacheVersion = 2;

    /// <summary>
    /// Skills that show a stagger grade and have hits that stagger, with their names, and their tripods that change
    /// which hits there are or how much they stagger.
    /// </summary>
    private static (string[] Skills, string[] Tripods) Read(SqliteConnection connection)
    {
        var stagger = new Dictionary<int, int>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT PrimaryKey, MAX(AiPointMax) FROM db1.SkillEffect GROUP BY PrimaryKey HAVING MAX(AiPointMax) > 0";
            using var reader = command.ExecuteReader();
            while (reader.Read()) stagger[reader.GetInt32(0)] = reader.GetInt32(1);
        }
        var all = new HashSet<int>();
        var graded = new SortedDictionary<int, (int Grade, string NameKey)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT PrimaryKey, MAX(StiffnessTooltipType), MIN(Name) FROM Skill GROUP BY PrimaryKey";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                all.Add(reader.GetInt32(0));
                if (reader.GetInt32(1) is >= 1 and <= 5) graded[reader.GetInt32(0)] = (reader.GetInt32(1), reader.GetString(2));
            }
        }
        var tripods = new Dictionary<(int Skill, int Slot), Tripod>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT PrimaryKey, SecondaryKey, NameKey, Type, ParamValue0, ParamValue1, ParamValue2, ParamValue3 FROM db2.SkillFeature";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var at = (reader.GetInt32(0), reader.GetInt32(1));
                if (!tripods.TryGetValue(at, out var tripod)) tripods[at] = tripod = new Tripod(reader.GetString(2));
                var type = reader.GetInt32(3);
                if (type == StaggerFeature) tripod.Bonuses.Add($"{reader.GetInt32(4)}:{reader.GetInt32(5)}");
                // Weak point rows name every hit whose weak point level they raise, not hits the tripod adds.
                else if (type != WeakPointFeature)
                    for (var column = 4; column < 8; column++) tripod.Names.Add(reader.GetInt32(column));
            }
        }
        var skills = new List<string>();
        var kept = new List<string>();
        foreach (var (skill, (grade, nameKey)) in graded)
        {
            var hits = new List<string>();
            var owning = new HashSet<int>();
            for (var effect = skill * 10; effect < skill * 10 + 100; effect++)
            {
                // Past the first ten, an id can be a neighbouring skill's own (24031's hits follow 24030's).
                if (!stagger.TryGetValue(effect, out var value) || (effect / 10 != skill && all.Contains(effect / 10))) continue;
                if (effect < skill * 10 + 10)
                {
                    hits.Add($"{effect}:{value}");
                    continue;
                }
                var owners = Enumerable.Range(1, 8).Where(slot => tripods.TryGetValue((skill, slot), out var tripod) && tripod.Names.Contains(effect)).ToList();
                if (owners.Count == 0) owners.Add((effect - skill * 10) / 10);
                owning.UnionWith(owners);
                hits.Add($"{effect}:{value}:{string.Join('/', owners)}");
            }
            if (hits.Count == 0) continue;
            var name = Text(connection, nameKey);
            if (string.IsNullOrWhiteSpace(name)) continue;
            skills.Add($"{skill}|{grade}|{Plain(name)}|{string.Join(',', hits)}");
            for (var slot = 1; slot <= 8; slot++)
            {
                if (!tripods.TryGetValue((skill, slot), out var tripod) || (tripod.Bonuses.Count == 0 && !owning.Contains(slot))) continue;
                var tripodName = Text(connection, tripod.NameKey);
                if (!string.IsNullOrWhiteSpace(tripodName))
                    kept.Add($"{skill}|{slot}|{Plain(tripodName)}|{string.Join(',', tripod.Bonuses)}");
            }
        }
        return (skills.ToArray(), kept.ToArray());
    }

    /// <summary>A tripod's name, its stagger bonuses ("effect:percent") and the numbers its rows name (hits among them).</summary>
    private sealed record Tripod(string NameKey)
    {
        public List<string> Bonuses { get; } = [];
        public HashSet<int> Names { get; } = [];
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
