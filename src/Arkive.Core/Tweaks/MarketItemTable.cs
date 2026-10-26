using System.Text;
using System.Text.RegularExpressions;

namespace Arkive.Core.Tweaks;

/// <summary>
/// The items the market sells, from the game's item table (EFTable_Item: EnableMarket, MarketPackageUnit, EnableAuction)
/// and their English names, for Market Price on Tooltips: an item on this list with no price yet gets a "check it on the
/// market" line. Written to prices/market-items.txt, one "name|bundle|place" line per name (place "m" market, "a"
/// auction house; auction gems only, since other auction items each have their own stats), after a "#stamp" line that
/// names the data2.lpk it came from, so a game update rewrites it.
/// </summary>
public static partial class MarketItemTable
{
    public const string FileName = "market-items.txt";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes the list into <paramref name="folder"/> unless it is already there for this data2.lpk.</summary>
    public static void Write(string data2Path, string folder, string workDirectory)
    {
        var file = new FileInfo(data2Path);
        var stamp = $"#{file.Length}-{file.LastWriteTimeUtc.Ticks}";
        var path = Path.Combine(folder, FileName);
        if (File.Exists(path) && File.ReadLines(path, Utf8).FirstOrDefault() == stamp)
            return;
        var lines = Read(data2Path, workDirectory);
        Directory.CreateDirectory(folder);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, stamp + "\n" + string.Concat(lines.Select(line => line + "\n")), Utf8);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The "name|bundle|place" lines, sorted by name.</summary>
    public static IReadOnlyList<string> Read(string data2Path, string workDirectory) =>
        CombatPowerTable.WithDatabases(data2Path, workDirectory, ["EFTable_Item.db", "EFTable_GameMsg.db"], connection =>
        {
            var items = new SortedDictionary<string, (int Bundle, bool Market)>(StringComparer.Ordinal);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.MSG, i.MarketPackageUnit, i.EnableMarket FROM Item i JOIN db1.GameMsg_English m ON m.KEY = lower(i.Name)
                WHERE i.EnableMarket != 0 OR (i.EnableAuction != 0 AND m.MSG LIKE '%Gem')
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = Tags().Replace(reader.GetString(0), "").Trim();
                if (name.Length == 0 || name.Contains('|') || name.Any(char.IsControl))
                    continue;
                var market = reader.GetInt64(2) != 0;
                var bundle = market ? Math.Max(1, reader.GetInt32(1)) : 1;
                if (items.TryGetValue(name, out var known))
                    items[name] = (Math.Max(known.Bundle, bundle), known.Market || market);
                else
                    items[name] = (bundle, market);
            }
            return items.Select(item => $"{item.Key}|{item.Value.Bundle}|{(item.Value.Market ? "m" : "a")}").ToList();
        });

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();
}
