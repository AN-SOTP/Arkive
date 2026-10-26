using System.Globalization;
using System.Text;

namespace Arkive.Core.Tweaks;

/// <summary>
/// The market prices Market Price on Tooltips has seen, kept between game sessions. The tweak sends what the player
/// browsed on the Data Bus ("prices" channel), one item per line: name|lowest|avg|last|bundle|source, prices in
/// hundredths of a gold per bundle (-1 when the market showed none). Arkive stamps each line with the time it arrived and
/// keeps the newest per item in prices.txt (the same fields plus the time in Unix milliseconds), which the tweak reads
/// once per session; every sighting is also added to history.txt for later trend lines.
/// </summary>
public static class MarketPriceStore
{
    public const string Channel = "prices";
    public const string FileName = "prices.txt";
    public const string HistoryName = "history.txt";
    private const int MaxItems = 20000;
    private const int MaxNameLength = 120;
    private static readonly Lock Gate = new();
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>One item's newest sighting: prices in hundredths of a gold per bundle, -1 when not shown.</summary>
    public sealed record Price(string Name, long Lowest, long Average, long Last, int Bundle, string Source, long SeenAt);

    /// <summary>Saves a bus message's prices in <paramref name="folder"/>; returns how many lines were taken.</summary>
    public static int Save(string folder, string text, DateTimeOffset now)
    {
        var seenAt = now.ToUnixTimeMilliseconds();
        var arrived = new List<Price>();
        foreach (var line in text.Split('\n'))
            if (TryParse(line, seenAt, out var price))
                arrived.Add(price);
        if (arrived.Count == 0)
            return 0;
        lock (Gate)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName);
            var prices = Load(path);
            foreach (var price in arrived)
                prices[price.Name] = price;
            var kept = prices.Values.OrderByDescending(price => price.SeenAt).Take(MaxItems).OrderBy(price => price.Name, StringComparer.Ordinal);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, string.Concat(kept.Select(price => Format(price) + "\n")), Utf8);
            File.Move(temporary, path, overwrite: true);
            File.AppendAllText(Path.Combine(folder, HistoryName), string.Concat(arrived.Select(price => Format(price) + "\n")), Utf8);
        }
        return arrived.Count;
    }

    /// <summary>The prices kept in a prices.txt, by item name; empty when there is none.</summary>
    public static Dictionary<string, Price> Load(string path)
    {
        var prices = new Dictionary<string, Price>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return prices;
        foreach (var line in File.ReadAllLines(path, Utf8))
        {
            var fields = line.Split('|');
            if (fields.Length == 7 && long.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seenAt) &&
                TryParse(string.Join('|', fields[..6]), seenAt, out var price))
                prices[price.Name] = price;
        }
        return prices;
    }

    private static bool TryParse(string line, long seenAt, out Price price)
    {
        price = null!;
        var fields = line.Trim('\r').Split('|');
        if (fields.Length != 6)
            return false;
        var name = fields[0].Trim();
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl) ||
            !TryCents(fields[1], out var lowest) || !TryCents(fields[2], out var average) || !TryCents(fields[3], out var last) ||
            !int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bundle) || bundle is < 1 or > 100000 ||
            fields[5] is not ("market" or "auction") || (lowest <= 0 && average <= 0 && last <= 0))
            return false;
        price = new Price(name, lowest, average, last, bundle, fields[5], seenAt);
        return true;
    }

    private static bool TryCents(string text, out long cents) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out cents) && cents >= -1 && cents < 100_000_000_000;

    private static string Format(Price price) => string.Join('|',
        price.Name, Number(price.Lowest), Number(price.Average), Number(price.Last), Number(price.Bundle), price.Source, Number(price.SeenAt));

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
