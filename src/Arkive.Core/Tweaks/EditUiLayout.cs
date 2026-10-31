using System.Globalization;
using System.Text;

namespace Arkive.Core.Tweaks;

/// <summary>
/// The HUD layout made with Edit UI, kept between game sessions. The tweak sends each change on the Data Bus ("editui"
/// channel): "set\tpiece\tx\ty\tscale" (offset from the game's own place in 1920x1080 units, and a size), "clear\tpiece",
/// or "reset". Arkive keeps one line per moved piece in edit-ui.txt under the tweak folder ("piece\tx\ty\tscale"), which
/// the tweak reads when the game starts.
/// </summary>
public static class EditUiLayout
{
    public const string Tweak = "editui";
    public const string Channel = "editui";
    public const string FileName = "edit-ui.txt";
    private const int MaxPieces = 500;
    private const double MaxOffset = 4000;
    private static readonly Lock Gate = new();
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>One moved piece: its offset in 1920x1080 units and its size (1 = the game's).</summary>
    public sealed record Piece(string Id, double X, double Y, double Scale);

    /// <summary>Applies a bus message to the layout in <paramref name="folder"/>; returns a log line, or null when the text isn't one.</summary>
    public static string? Save(string folder, string text)
    {
        var parts = text.Split('\t');
        lock (Gate)
        {
            var path = Path.Combine(folder, FileName);
            var layout = Load(path);
            string change;
            switch (parts)
            {
                case ["reset"]:
                    layout.Clear();
                    change = "every piece back where the game puts it";
                    break;
                case ["clear", var id] when ValidId(id):
                    layout.Remove(id);
                    change = $"{id} back where the game puts it";
                    break;
                case ["set", var id, var x, var y, var scale] when ValidId(id) && TryParse(x, y, scale, out var piece):
                    if (!layout.ContainsKey(id) && layout.Count >= MaxPieces) return null;
                    layout[id] = piece with { Id = id };
                    change = $"{id} moved";
                    break;
                default:
                    return null;
            }
            Directory.CreateDirectory(folder);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, string.Concat(layout.Values.OrderBy(piece => piece.Id, StringComparer.Ordinal).Select(piece => Format(piece) + "\n")), Utf8);
            File.Move(temporary, path, overwrite: true);
            return change;
        }
    }

    /// <summary>The pieces kept in an edit-ui.txt, by id; empty when there is none.</summary>
    public static Dictionary<string, Piece> Load(string path)
    {
        var layout = new Dictionary<string, Piece>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return layout;
        foreach (var line in File.ReadAllLines(path, Utf8))
            if (line.Split('\t') is [var id, var x, var y, var scale] && ValidId(id) && TryParse(x, y, scale, out var piece))
                layout[id] = piece with { Id = id };
        return layout;
    }

    /// <summary>A frame name or "frame/part": letters, digits, '_' and one '/'.</summary>
    private static bool ValidId(string id) =>
        id.Length is > 0 and <= 80 && id.Count(c => c == '/') <= 1 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '/');

    private static bool TryParse(string x, string y, string scale, out Piece piece)
    {
        piece = null!;
        if (!double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) ||
            !double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var py) ||
            !double.TryParse(scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var ps) ||
            !double.IsFinite(px) || !double.IsFinite(py) || !double.IsFinite(ps) ||
            Math.Abs(px) > MaxOffset || Math.Abs(py) > MaxOffset || ps is < 0.5 or > 2)
            return false;
        piece = new Piece("", px, py, ps);
        return true;
    }

    private static string Format(Piece piece) => string.Join('\t', piece.Id,
        piece.X.ToString("0.#", CultureInfo.InvariantCulture),
        piece.Y.ToString("0.#", CultureInfo.InvariantCulture),
        piece.Scale.ToString("0.##", CultureInfo.InvariantCulture));
}
