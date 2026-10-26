using Arkive.Core.Databases;
using Arkive.Core.GameData;

namespace Arkive.Core.Tweaks;

/// <summary>
/// The game's Combat Power coefficients, EFTable_BattlePoint in data2.lpk. Key 1 prices a DPS character and key 2 a
/// support; a row's Type says what it prices, and its value is hundredths of a percent. The factors multiply.
/// </summary>
public static class CombatPowerTable
{
    public const string Entry = "EFGame_Extra/ClientData/TableData/EFTable_BattlePoint.db";

    /// <summary>Gems: ValueA is the tier, ValueB the level, ValueC the value.</summary>
    public const int GemType = 22;

    /// <summary>The gem rows as [tier, level, dps, support, ...], read from the game's data2.lpk.</summary>
    public static int[] Gems(string data2Path, string workDirectory)
    {
        Directory.CreateDirectory(workDirectory);
        var work = Path.Combine(workDirectory, "battlepoint-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            LostArkDatabaseArchive.Extract(data2Path, Entry, work, CancellationToken.None);
            var rows = new SortedDictionary<(int Tier, int Level), int[]>();
            using (var connection = GameDataTables.Open(work, readOnly: true))
            using (var command = connection.CreateCommand())
            {
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
            }
            return rows.SelectMany(row => new[] { row.Key.Tier, row.Key.Level, row.Value[0], row.Value[1] }).ToArray();
        }
        finally
        {
            File.Delete(work);
        }
    }
}
