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

    /// <summary>Gems: ValueA is the tier, ValueB the level, ValueC the value.</summary>
    public const int GemType = 22;

    /// <summary>The stat a tier 4 gem's bonus effect raises: Basic Attack Power, in hundredths of a percent.</summary>
    private const int BasicAttackStat = 150;

    /// <summary>The gem rows as [tier, level, dps, support, ...], read from the game's data2.lpk.</summary>
    public static int[] Gems(string data2Path, string workDirectory) =>
        WithDatabases(data2Path, workDirectory, ["EFTable_BattlePoint.db"], connection =>
        {
            var rows = new SortedDictionary<(int Tier, int Level), int[]>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT PrimaryKey, ValueA, ValueB, ValueC FROM BattlePoint WHERE Type = $type";
            command.Parameters.AddWithValue("$type", GemType);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetInt32(0);
                if (key is not (1 or 2)) continue;
                var gem = (reader.GetInt32(1), reader.GetInt32(2));
                if (!rows.TryGetValue(gem, out var values)) rows[gem] = values = new int[2];
                values[key - 1] = reader.GetInt32(3);
            }
            return rows.SelectMany(row => new[] { row.Key.Tier, row.Key.Level, row.Value[0], row.Value[1] }).ToArray();
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
    /// Extracts the named databases from data2.lpk, opens the first and attaches the rest as db1, db2…, runs
    /// <paramref name="read"/>, then deletes the copies.
    /// </summary>
    private static T WithDatabases<T>(string data2Path, string workDirectory, string[] databases, Func<SqliteConnection, T> read)
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
