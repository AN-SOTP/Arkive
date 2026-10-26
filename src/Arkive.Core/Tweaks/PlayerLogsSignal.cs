using System.Globalization;
using System.Text;

namespace Arkive.Core.Tweaks;

/// <summary>
/// How the Logs tab tells Arkive which player to look up: it spells a message by reading Arkive's signal files in
/// order (see <see cref="TweakSignalFolder"/>). The message is the click's request id (4 hex digits), the raid wanted
/// (4 hex digits, 0 for recent logs), the name (4 hex digits per UTF-16 character) and a checksum (the sum of the
/// 4-digit groups, 4 hex digits).
/// </summary>
public sealed class PlayerLogsSignal : IDisposable
{
    public const string Begin = TweakSignalFolder.Begin;
    public const string End = TweakSignalFolder.End;
    public static IReadOnlyList<string> Sets => TweakSignalFolder.Sets;
    private const string Digits = TweakSignalFolder.Digits;
    private const int RequestDigits = 4;
    private const int HeadDigits = RequestDigits + 4;
    private const int MaxNameLength = 32;

    private readonly TweakSignalFolder _files;
    private readonly StringBuilder _message = new();
    private readonly Lock _gate = new();
    private bool _receiving;
    private string? _lastFile;

    public PlayerLogsSignal(string folder)
    {
        _files = new TweakSignalFolder(folder, Receive);
        _files.Failed += problem => Failed?.Invoke(problem);
    }

    /// <summary>A complete message: the player's name, the click's request id and the raid wanted (0: recent logs).</summary>
    public event Action<string, string, int>? Requested;

    /// <summary>A signal file could not be watched (for the log).</summary>
    public event Action<string>? Failed;

    public static IEnumerable<string> Symbols => TweakSignalFolder.Symbols;

    public static string FileName(string set, string symbol) => TweakSignalFolder.FileName(set, symbol);

    /// <summary>The hex digits the tab sends for a click: request id, raid, name, checksum.</summary>
    public static string Encode(string request, int raid, string name)
    {
        var builder = new StringBuilder(request.ToLowerInvariant()).Append(raid.ToString("x4", CultureInfo.InvariantCulture));
        foreach (var character in name)
            builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
        return builder.Append(Checksum(builder.ToString()).ToString("x4", CultureInfo.InvariantCulture)).ToString();
    }

    public static bool TryDecode(string digits, out string request, out int raid, out string name)
    {
        request = "";
        raid = 0;
        name = "";
        if (digits.Length < HeadDigits + 8 || digits.Length % 4 != 0 ||
            (digits.Length - HeadDigits - 4) / 4 > MaxNameLength || digits.Any(digit => !Digits.Contains(digit)))
            return false;
        var body = digits[..^4];
        if (Checksum(body) != int.Parse(digits.AsSpan(digits.Length - 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            return false;
        request = body[..RequestDigits];
        raid = int.Parse(body.AsSpan(RequestDigits, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var builder = new StringBuilder();
        for (var at = HeadDigits; at < body.Length; at += 4)
            builder.Append((char)int.Parse(body.AsSpan(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        name = builder.ToString();
        return name.All(character => !char.IsControl(character) && character is not ('/' or '\\' or '#' or '?'));
    }

    /// <summary>The sum of the 4-digit groups, wrapped to 16 bits (the tab computes the same).</summary>
    private static int Checksum(string digits)
    {
        var sum = 0;
        for (var at = 0; at + 4 <= digits.Length; at += 4)
            sum = (sum + int.Parse(digits.AsSpan(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)) & 0xFFFF;
        return sum;
    }

    /// <summary>Writes the signal files and starts watching them.</summary>
    public void Start() => _files.Start();

    /// <summary>
    /// Notes one read of a signal file; "go" starts a message and "end" completes it. The tab never reads the same file
    /// twice in a row, so a second note from the same file is dropped: a virus scanner's look at the file during the
    /// tab's open shows up as a read too.
    /// </summary>
    public void Receive(string symbol, string? file = null)
    {
        string? complete = null;
        lock (_gate)
        {
            if (file is not null && file == _lastFile)
                return;
            _lastFile = file;
            if (symbol == Begin)
            {
                _message.Clear();
                _receiving = true;
                return;
            }
            if (!_receiving)
                return;
            if (symbol == End)
            {
                complete = _message.ToString();
                _message.Clear();
                _receiving = false;
            }
            else if (_message.Length < HeadDigits + 4 * MaxNameLength + 4)
                _message.Append(symbol);
        }
        if (complete is not null && TryDecode(complete, out var request, out var raid, out var name))
            Requested?.Invoke(name, request, raid);
    }

    public void Dispose() => _files.Dispose();
}
