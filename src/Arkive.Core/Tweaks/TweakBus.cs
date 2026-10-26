using System.Globalization;
using System.Text;

namespace Arkive.Core.Tweaks;

/// <summary>
/// The Data Bus: short messages from tweak code to Arkive ("channel", "text"), spelled through the signal files in
/// &lt;data&gt;/tweaks/bus (see <see cref="TweakSignalFolder"/>). Each character is two hex digits ("ff" and four
/// digits for one past 254), then a four-digit checksum (the sum of the character codes, wrapped to 16 bits). The
/// tweak side is eng/tweaks Bus.hx, which sends one message at a time for all of the game's windows.
/// </summary>
public sealed class TweakBus : IDisposable
{
    /// <summary>The longest message accepted, in characters.</summary>
    public const int MaxLength = 2048;

    private readonly TweakSignalFolder _files;
    private readonly StringBuilder _message = new();
    private readonly Lock _gate = new();
    private bool _receiving;
    private string? _lastFile;

    public TweakBus(string folder)
    {
        _files = new TweakSignalFolder(folder, Receive);
        _files.Failed += problem => Failed?.Invoke(problem);
    }

    /// <summary>A complete message: its channel and text.</summary>
    public event Action<string, string>? Received;

    /// <summary>A signal file could not be watched (for the log).</summary>
    public event Action<string>? Failed;

    /// <summary>Writes the signal files and starts watching them.</summary>
    public void Start() => _files.Start();

    /// <summary>The digits Bus.hx spells for a message (for tests).</summary>
    public static string Encode(string channel, string text)
    {
        var message = channel + "\t" + text;
        var builder = new StringBuilder();
        var sum = 0;
        foreach (var character in message)
        {
            sum = (sum + character) & 0xFFFF;
            builder.Append(character < 0xFF
                ? ((int)character).ToString("x2", CultureInfo.InvariantCulture)
                : "ff" + ((int)character).ToString("x4", CultureInfo.InvariantCulture));
        }
        return builder.Append(sum.ToString("x4", CultureInfo.InvariantCulture)).ToString();
    }

    public static bool TryDecode(string digits, out string channel, out string text)
    {
        channel = "";
        text = "";
        if (digits.Length < 6 || digits.Any(digit => !TweakSignalFolder.Digits.Contains(digit)))
            return false;
        var builder = new StringBuilder();
        var sum = 0;
        var at = 0;
        var end = digits.Length - 4;
        while (at < end)
        {
            if (at + 2 > end)
                return false;
            var code = Hex(digits, at, 2);
            at += 2;
            if (code == 0xFF)
            {
                if (at + 4 > end)
                    return false;
                code = Hex(digits, at, 4);
                at += 4;
            }
            sum = (sum + code) & 0xFFFF;
            builder.Append((char)code);
        }
        if (sum != Hex(digits, end, 4))
            return false;
        var message = builder.ToString();
        var tab = message.IndexOf('\t');
        if (tab <= 0)
            return false;
        channel = message[..tab];
        text = message[(tab + 1)..];
        return true;
    }

    private static int Hex(string digits, int at, int length) =>
        int.Parse(digits.AsSpan(at, length), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>
    /// Notes one read of a signal file; "go" starts a message and "end" completes it. A second note from the same file
    /// in a row is dropped: a virus scanner's look at the file during the game's open shows up as a read too.
    /// </summary>
    public void Receive(string symbol, string? file = null)
    {
        string? complete = null;
        lock (_gate)
        {
            if (file is not null && file == _lastFile)
                return;
            _lastFile = file;
            if (symbol == TweakSignalFolder.Begin)
            {
                _message.Clear();
                _receiving = true;
                return;
            }
            if (!_receiving)
                return;
            if (symbol == TweakSignalFolder.End)
            {
                complete = _message.ToString();
                _message.Clear();
                _receiving = false;
            }
            else if (_message.Length < 6 * MaxLength + 4)
                _message.Append(symbol);
        }
        if (complete is not null && TryDecode(complete, out var channel, out var text))
            Received?.Invoke(channel, text);
    }

    public void Dispose() => _files.Dispose();
}
