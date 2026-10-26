using Arkive.Core.Databases;
using Arkive.Core.GameData;
using Microsoft.Data.Sqlite;

namespace Arkive.Core.Tweaks;

/// <summary>
/// The game's Combat Power coefficients, EFTable_BattlePoint in data2.lpk. Key 1 prices a DPS character and key 2 a
/// support; a row's Type says what it prices, and its value is hundredths of a percent. The factors multiply.
/// </summary>
public static class CombatPowerTable
{
    private const string Tables = "EFGame_Extra/ClientData/TableData/";
    public const string Entry = Tables + "EFTable_BattlePoint.db";

    /// <summary>
    /// The row types the Combat Power tweak prices with: 4 weapon quality (ValueA quality, ValueB its bonus),
    /// 5-7 Ark Passive points (Evolution, Enlightenment, Leap: ValueA per point), 15 refinement stats (ValueA 1, ValueB
    /// stat, ValueC weight), 19 bracelet stats (same shape), 22 gems (ValueA tier, ValueB level, ValueC value) and 26
    /// combat stats (ValueA stat, ValueB per point) and 31 astrogem options (ValueA option, ValueB total level, ValueC value).
    /// </summary>
    public static readonly int[] PricedTypes = [4, 5, 6, 7, 15, 19, 22, 26, 31];

    /// <summary>The stat a tier 4 gem's bonus effect raises: Basic Attack Power, in hundredths of a percent.</summary>
    private const int BasicAttackStat = 150;

    /// <summary>The rows of <see cref="PricedTypes"/> as [key, type, ValueA, ValueB, ValueC, ...] (key 1 DPS, 2 support).</summary>
    public static int[] Rows(string data2Path, string workDirectory) =>
        WithDatabases(data2Path, workDirectory, ["EFTable_BattlePoint.db"], connection =>
        {
            var rows = new List<int>();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT PrimaryKey, Type, ValueA, ValueB, ValueC FROM BattlePoint WHERE Type IN ({string.Join(',', PricedTypes)}) " +
                                  "ORDER BY Type, PrimaryKey, SecondaryKey";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                for (var column = 0; column < 5; column++)
                    rows.Add(reader.GetInt32(column));
            return rows.ToArray();
        });

    /// <summary>
    /// A gem's Basic Attack Power by level as [level, hundredths of a percent, ...]: the bonus effect of its static
    /// option (only tier 4 gems have one). Every gem of a level has the same value.
    /// </summary>
    public static int[] GemBasicAttack(string data2Path, string workDirectory) =>
        WithDatabases(data2Path, workDirectory, ["EFTable_ItemGemBase.db", "EFTable_ItemGradeOptionStatic.db"], connection =>
        {
            var levels = new SortedDictionary<int, int>();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT g.Level, s.AddonValue00 FROM ItemGemBase g JOIN db1.ItemGradeOptionStatic s ON s.PrimaryKey = g.StaticOptionId
                WHERE s.AddonStat00 = $stat
                UNION SELECT g.Level, s.AddonValue01 FROM ItemGemBase g JOIN db1.ItemGradeOptionStatic s ON s.PrimaryKey = g.StaticOptionId
                WHERE s.AddonStat01 = $stat
                """;
            command.Parameters.AddWithValue("$stat", BasicAttackStat);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                levels[reader.GetInt32(0)] = Math.Max(levels.GetValueOrDefault(reader.GetInt32(0)), reader.GetInt32(1));
            return levels.SelectMany(level => new[] { level.Key, level.Value }).ToArray();
        });

    /// <summary>
    /// Everything the Combat Power tweak reads, as its options: "rows" and "gemBase" (above), "engravings" (one text per
    /// engraving and role, "Name|key|v0,v1,…,v93": the value at index 20 × stone level + 1 + 4 × (grade − 1) + level, grade
    /// 1 heroic to 3 relic; the penalty Atk. Power Reduction by its level) and "bracelet" (one text per bracelet effect,
    /// "text|dps|support", the text with whitespace, slashes and commas removed and lower-cased, as <see cref="Plain"/>)
    /// and "orbs" (one text per paradise orb and role, "Name|key|B|C": type 33 for DPS, 34 for support).
    /// Kept in <paramref name="workDirectory"/> per data2.lpk size and time, since reading the game's text takes a while.
    /// </summary>
    public static Dictionary<string, System.Text.Json.JsonElement> Options(string data2Path, string workDirectory)
    {
        var file = new FileInfo(data2Path);
        var cache = Path.Combine(workDirectory, $"combat-power-{CacheVersion}-{file.Length}-{file.LastWriteTimeUtc.Ticks}.json");
        if (File.Exists(cache))
        {
            var kept = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(File.ReadAllText(cache));
            if (kept is not null) return kept;
        }
        var options = new Dictionary<string, object>
        {
            ["rows"] = Rows(data2Path, workDirectory),
            ["gemBase"] = GemBasicAttack(data2Path, workDirectory),
        };
        WithDatabases(data2Path, workDirectory,
            ["EFTable_BattlePoint.db", "EFTable_Ability.db", "EFTable_CombatEffect.db", "EFTable_GameMsg.db", "EFTable_Item.db"], connection =>
            {
                options["engravings"] = Engravings(connection);
                options["bracelet"] = Bracelet(connection);
                options["orbs"] = Orbs(connection);
                return 0;
            });
        Directory.CreateDirectory(workDirectory);
        foreach (var stale in Directory.EnumerateFiles(workDirectory, "combat-power-*.json")) File.Delete(stale);
        var json = System.Text.Json.JsonSerializer.Serialize(options);
        File.WriteAllText(cache, json);
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!;
    }

    /// <summary>Bumped when <see cref="Options"/> gains a table, so caches from older builds are read again.</summary>
    private const int CacheVersion = 3;

    /// <summary>Paradise orbs (types 33 and 34) by their item names: ValueA is the orb, B and C its values.</summary>
    private static string[] Orbs(SqliteConnection connection)
    {
        var found = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.PrimaryKey, b.ValueB, b.ValueC, i.Name FROM BattlePoint b JOIN db4.Item i ON i.PrimaryKey = b.ValueA
            WHERE b.Type IN (33, 34) ORDER BY b.PrimaryKey, b.SecondaryKey
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = Text(connection, reader.GetString(3));
            if (string.IsNullOrWhiteSpace(name)) continue;
            name = System.Text.RegularExpressions.Regex.Replace(name, "<[^>]*>", "").Trim();
            found.Add($"{name}|{reader.GetInt32(0)}|{reader.GetInt32(1)}|{reader.GetInt32(2)}");
        }
        return found.ToArray();
    }

    /// <summary>Game text as the tweak compares it: tags, whitespace, slashes and commas removed, lower-cased.</summary>
    public static string Plain(string text) =>
        new(System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "")
            .Where(c => !char.IsWhiteSpace(c) && c != '/' && c != ',').Select(char.ToLowerInvariant).ToArray());

    private static string? Text(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MSG FROM db3.GameMsg_English WHERE KEY = $key";
        command.Parameters.AddWithValue("$key", key.ToLowerInvariant());
        return command.ExecuteScalar() as string;
    }

    private static string[] Engravings(SqliteConnection connection)
    {
        var values = new Dictionary<(int Ability, int Key), int[]>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT PrimaryKey, ValueA, ValueB, ValueC FROM BattlePoint WHERE Type IN (10, 11)";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var at = (reader.GetInt32(1), reader.GetInt32(0));
                if (!values.TryGetValue(at, out var row)) values[at] = row = new int[94];
                var index = reader.GetInt32(2);
                if (index is >= 0 and < 94) row[index] = reader.GetInt32(3);
            }
        }
        var found = new List<string>();
        foreach (var ((ability, key), row) in values.OrderBy(pair => pair.Key))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Name FROM db1.Ability WHERE PrimaryKey = $id ORDER BY SecondaryKey LIMIT 1";
            command.Parameters.AddWithValue("$id", ability);
            var name = command.ExecuteScalar() is string nameKey ? Text(connection, nameKey) : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            name = System.Text.RegularExpressions.Regex.Replace(name, "<[^>]*>", "").Trim();
            found.Add($"{name}|{key}|{string.Join(',', row)}");
        }
        return found.ToArray();
    }

    /// <summary>Bracelet effects: type 20 (DPS) and 21 (support); ValueA 4 names a CombatEffect, 3 an Ability.</summary>
    private static string[] Bracelet(SqliteConnection connection)
    {
        var effects = new Dictionary<string, int[]>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PrimaryKey, ValueA, ValueB, ValueC FROM BattlePoint WHERE Type IN (20, 21)";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetInt32(0);
            using var lookup = connection.CreateCommand();
            lookup.CommandText = reader.GetInt32(1) == 4
                ? "SELECT Desc FROM db2.CombatEffect WHERE PrimaryKey = $id LIMIT 1"
                : "SELECT Desc FROM db1.Ability WHERE PrimaryKey = $id ORDER BY SecondaryKey LIMIT 1";
            lookup.Parameters.AddWithValue("$id", reader.GetInt32(2));
            var text = lookup.ExecuteScalar() is string descKey ? Text(connection, descKey) : null;
            if (string.IsNullOrWhiteSpace(text) || key is not (1 or 2)) continue;
            var plain = Plain(text);
            if (!effects.TryGetValue(plain, out var value)) effects[plain] = value = new int[2];
            value[key - 1] = reader.GetInt32(3);
        }
        return effects.Select(effect => $"{effect.Key}|{effect.Value[0]}|{effect.Value[1]}").ToArray();
    }

    /// <summary>
    /// Extracts the named databases from data2.lpk, opens the first and attaches the rest as db1, db2…, runs
    /// <paramref name="read"/>, then deletes the copies.
    /// </summary>
    internal static T WithDatabases<T>(string data2Path, string workDirectory, string[] databases, Func<SqliteConnection, T> read)
    {
        Directory.CreateDirectory(workDirectory);
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var copies = databases.Select(name => Path.Combine(workDirectory, $"{stamp}-{name}")).ToArray();
        try
        {
            for (var i = 0; i < databases.Length; i++)
                LostArkDatabaseArchive.Extract(data2Path, Tables + databases[i], copies[i], CancellationToken.None);
            using var connection = GameDataTables.Open(copies[0], readOnly: true);
            for (var i = 1; i < copies.Length; i++)
            {
                using var attach = connection.CreateCommand();
                attach.CommandText = $"ATTACH DATABASE $path AS db{i}";
                attach.Parameters.AddWithValue("$path", copies[i]);
                attach.ExecuteNonQuery();
            }
            return read(connection);
        }
        finally
        {
            foreach (var copy in copies) File.Delete(copy);
        }
    }
}
