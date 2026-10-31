using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Arkive.Core.Tweaks;

/// <summary>
/// The odds of what a box gives, from the game's tables, for Box Odds on Tooltips. A box (EFTable_RandomBoxBase, type 0
/// or 1) names a drop (EFTable_DropBase): each of its rows rolls on its own, Ratio percent of the time, Repetition times,
/// and a roll that hits gives one row of its entity group (EFTable_DropEntity) picked evenly, so a group lists an item
/// several times to make it likelier. A row of a group with no item id gives each class its own item. Rows that go
/// through EFTable_SmartDrop are decided by the server (daily limits and the like); their items are listed as such.
/// Boxes of type 2 and 3 only list their contents: the game files have no odds for them (the server rolls them), so
/// only their names are kept, to say so. Selection boxes (the player picks) are left out.
/// <para>
/// Written to boxodds/boxes-&lt;stamp&gt;.txt: "arkive-boxodds 1", then per box a "B name" line followed by its items,
/// "I name|chance|expected" (the chance to get at least one, and how many on average) or "S name" (decided by the
/// server), and an "N name" line per box the server rolls. Only boxes with something left to chance get items; several
/// boxes can share a name.
/// </para>
/// </summary>
public static partial class BoxOddsTable
{
    public const string Header = "arkive-boxodds 1";
    /// <summary>Goes in the file name, so a table an older Arkive wrote for the same data2.lpk is replaced.</summary>
    private const int Version = 2;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The money types boxes give that have a name players know (others are left out).</summary>
    private static readonly Dictionary<int, string> Money = new() { [1] = "Silver", [2] = "Gold" };

    /// <summary>Per-class item id columns of EFTable_DropEntity (every column ending in "Id" but NormalId).</summary>
    private static string[] ClassColumns(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var columns = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA db2.table_info(DropEntity)";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (name.EndsWith("Id", StringComparison.Ordinal) && name != "NormalId")
                columns.Add(name);
        }
        return [.. columns];
    }

    /// <summary>Writes the table into <paramref name="folder"/> unless it is already there for this data2.lpk, and returns its path.</summary>
    public static string Write(string data2Path, string folder, string workDirectory)
    {
        var file = new FileInfo(data2Path);
        var stamp = $"{file.Length}-{file.LastWriteTimeUtc.Ticks}";
        var path = Path.Combine(folder, $"boxes-{Version}-{stamp}.txt");
        if (File.Exists(path))
            return path;
        Directory.CreateDirectory(folder);
        foreach (var stale in Directory.EnumerateFiles(folder, "boxes-*.txt"))
            File.Delete(stale);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Encode(Read(data2Path, workDirectory)), Utf8);
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    public sealed record BoxItem(string Name, double Chance, double Expected, bool Server);

    /// <summary>A box and what it gives; no items when the server rolls it (only its contents are in the files).</summary>
    public sealed record Box(int Id, string Name, IReadOnlyList<BoxItem> Items)
    {
        public bool ServerRolled => Items.Count == 0;
    }

    private sealed record Entity(string[] Names, double Count);

    private sealed record DropRow(double Ratio, int Repetition, int EntityIndex, int SmartDrop);

    public static string Encode(IEnumerable<Box> boxes)
    {
        var text = new StringBuilder(Header).Append('\n');
        foreach (var box in boxes)
        {
            if (box.ServerRolled)
            {
                text.Append("N ").Append(box.Name).Append('\n');
                continue;
            }
            text.Append("B ").Append(box.Name).Append('\n');
            foreach (var item in box.Items)
                if (item.Server)
                    text.Append("S ").Append(item.Name).Append('\n');
                else
                    text.Append(CultureInfo.InvariantCulture, $"I {item.Name}|{item.Chance:0.######}|{item.Expected:0.####}\n");
        }
        return text.ToString();
    }

    /// <summary>Every box with something left to chance, by item id, then the boxes the server rolls.</summary>
    public static IReadOnlyList<Box> Read(string data2Path, string workDirectory) =>
        CombatPowerTable.WithDatabases(data2Path, workDirectory,
            ["EFTable_RandomBoxBase.db", "EFTable_DropBase.db", "EFTable_DropEntity.db", "EFTable_SmartDrop.db", "EFTable_Item.db", "EFTable_GameMsg.db"],
            connection =>
            {
                var names = new Dictionary<long, string>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT i.PrimaryKey, m.MSG FROM db4.Item i JOIN db5.GameMsg_English m ON m.KEY = lower(i.Name)";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var name = Tags().Replace(reader.GetString(1), "").Trim();
                        if (name.Length > 0 && !name.Contains('|') && !name.Any(char.IsControl))
                            names[reader.GetInt64(0)] = name;
                    }
                }
                var classColumns = ClassColumns(connection);
                var entities = new Dictionary<int, List<Entity>>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT PrimaryKey, NormalId, NormalMoneyType, NormalMinCount, NormalMaxCount, " +
                                          string.Join(", ", classColumns) + " FROM db2.DropEntity ORDER BY PrimaryKey, SecondaryKey";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var item = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                        var money = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                        string[] rowNames;
                        if (item != 0)
                            rowNames = names.TryGetValue(item, out var name) ? [name] : [];
                        else if (money != 0)
                            rowNames = Money.TryGetValue(money, out var currency) ? [currency] : [];
                        else
                            rowNames = [.. Enumerable.Range(5, classColumns.Length)
                                .Select(column => reader.IsDBNull(column) ? 0 : reader.GetInt64(column))
                                .Where(id => id != 0 && names.ContainsKey(id)).Select(id => names[id]).Distinct()];
                        var count = (reader.GetDouble(3) + reader.GetDouble(4)) / 2;
                        var key = reader.GetInt32(0);
                        if (!entities.TryGetValue(key, out var group))
                            entities[key] = group = [];
                        group.Add(new Entity(rowNames, count));
                    }
                }
                var drops = new Dictionary<int, List<DropRow>>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT PrimaryKey, Ratio, Repetition, EntityIndex, SmartDropIndex, RewardType FROM db1.DropBase ORDER BY PrimaryKey, SecondaryKey";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var key = reader.GetInt32(0);
                        if (!drops.TryGetValue(key, out var rows))
                            drops[key] = rows = [];
                        var smart = reader.GetInt32(4);
                        // RewardType 1 rows are the smart-drop ones; a row marked so without an index still isn't ours to price.
                        rows.Add(new DropRow(reader.GetDouble(1), Math.Max(1, reader.GetInt32(2)), reader.GetInt32(3),
                            smart != 0 ? smart : reader.GetInt32(5) != 0 ? -1 : 0));
                    }
                }
                var smartDrops = new Dictionary<int, int[]>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT PrimaryKey, DropIndex, Fail_DropIndex FROM db3.SmartDrop";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        smartDrops[reader.GetInt32(0)] = [reader.GetInt32(1), reader.GetInt32(2)];
                }
                var boxes = new List<Box>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT PrimaryKey, DropIndex FROM RandomBoxBase WHERE Type IN (0, 1) AND DropIndex != 0 ORDER BY PrimaryKey";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var id = reader.GetInt64(0);
                        if (!names.TryGetValue(id, out var name) || !drops.TryGetValue(reader.GetInt32(1), out var rows))
                            continue;
                        if (Odds(rows, entities, drops, smartDrops) is { } items)
                            boxes.Add(new Box((int)id, name, items));
                    }
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT PrimaryKey FROM RandomBoxBase WHERE Type IN (2, 3) AND RandomBoxEntityId != 0 " +
                                          "AND ComponentDesc NOT LIKE '%selective%' ORDER BY PrimaryKey";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        if (names.TryGetValue(reader.GetInt64(0), out var name))
                            boxes.Add(new Box((int)reader.GetInt64(0), name, []));
                }
                return (IReadOnlyList<Box>)boxes;
            });

    /// <summary>What the rows of one drop give, or null when nothing is left to chance (every row always gives one thing).</summary>
    private static List<BoxItem>? Odds(List<DropRow> rows, Dictionary<int, List<Entity>> entities, Dictionary<int, List<DropRow>> drops,
        Dictionary<int, int[]> smartDrops)
    {
        var miss = new Dictionary<string, double>();
        var expected = new Dictionary<string, double>();
        var order = new List<string>();
        var server = new List<string>();
        var random = false;
        foreach (var row in rows)
        {
            if (row.SmartDrop != 0)
            {
                random = true;
                if (row.SmartDrop > 0 && smartDrops.TryGetValue(row.SmartDrop, out var outcomes))
                    foreach (var drop in outcomes)
                        foreach (var inner in drops.GetValueOrDefault(drop) ?? [])
                            foreach (var entity in entities.GetValueOrDefault(inner.EntityIndex) ?? [])
                                server.AddRange(entity.Names);
                continue;
            }
            if (row.Ratio <= 0 || !entities.TryGetValue(row.EntityIndex, out var group) || group.Count == 0)
                continue;
            var hit = Math.Min(1, row.Ratio / 100);
            if (hit < 1 || group.Select(entity => string.Join('|', entity.Names)).Distinct().Count() > 1)
                random = true;
            // Per name: the share of the group's rows that give it, and how many on average when it's picked.
            var share = new Dictionary<string, (double Rows, double Count)>();
            foreach (var entity in group)
                foreach (var name in entity.Names)
                {
                    var (shareRows, shareCount) = share.GetValueOrDefault(name);
                    share[name] = (shareRows + 1, shareCount + entity.Count);
                }
            foreach (var (name, (shareRows, shareCount)) in share)
            {
                var each = hit * shareRows / group.Count;
                if (!miss.ContainsKey(name))
                {
                    order.Add(name);
                    miss[name] = 1;
                    expected[name] = 0;
                }
                miss[name] *= Math.Pow(1 - each, row.Repetition);
                expected[name] += row.Repetition * hit * shareCount / group.Count;
            }
        }
        if (!random)
            return null;
        var items = order.Select(name => new BoxItem(name, 1 - miss[name], expected[name], false)).ToList();
        foreach (var name in server.Distinct())
            if (!miss.ContainsKey(name))
                items.Add(new BoxItem(name, 0, 0, true));
        return items.Count == 0 ? null : items;
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();
}
