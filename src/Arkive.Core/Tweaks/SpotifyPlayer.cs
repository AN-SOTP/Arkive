using System.Globalization;
using System.Text;

namespace Arkive.Core.Tweaks;

/// <summary>
/// A message from the Spotify Player tweak: a button (<see cref="Command"/>), Spotify's volume in percent, or where the
/// player was dragged to and whether it is folded ("x&lt;tab&gt;y&lt;tab&gt;0|1").
/// </summary>
public sealed record SpotifyRequest(SpotifyCommand? Command = null, int? Volume = null, string? Layout = null);

/// <summary>
/// The Arkive half of the Spotify Player tweak's files. While the game runs, Arkive writes Spotify's state to
/// &lt;tweaks&gt;/spotify/now.txt every second (the tweak reads it the way the Logs tab reads its answers), and the tweak
/// sends its buttons on the Data Bus channel "spotify". The file is lines of tab-separated fields:
/// "spotify, sequence" (the sequence goes up with every write, so the tweak sees when Arkive stops), "state, none |
/// playing | paused | stopped", "title", "artist", "album", "time, seconds in, seconds long", "can, letters" (p play and
/// pause, b back, n next, s stop, h shuffle, r repeat), "shuffle, on | off", "repeat, off | track | list" (empty when
/// Spotify doesn't say), "volume, percent" (empty when Spotify plays no sound yet) and "layout, x, y, 0 | 1".
/// </summary>
public static class SpotifyPlayer
{
    /// <summary>The tweak's id, and its Data Bus channel.</summary>
    public const string Tweak = "spotify";

    public const string Channel = "spotify";

    /// <summary>The file the tweak reads, in <see cref="TweakModService.SpotifyFolder"/>.</summary>
    public const string StateFile = "now.txt";

    /// <summary>Where the player was last left, in the same folder ("x&lt;tab&gt;y&lt;tab&gt;0|1").</summary>
    public const string LayoutFile = "layout.txt";

    /// <summary>The farthest a dragged player may sit from its spot under the minimap, in interface pixels.</summary>
    private const int MaxOffset = 8000;

    /// <summary>The file's text for Spotify's state (null: Spotify isn't running), its volume and the saved layout.</summary>
    public static string Describe(SpotifyNowPlaying? now, int? volume, string layout, long sequence)
    {
        var text = new StringBuilder();
        void Line(params string[] fields) => text.Append(string.Join('\t', fields.Select(Clean))).Append('\n');
        Line("spotify", sequence.ToString(CultureInfo.InvariantCulture));
        if (now is null)
        {
            Line("state", "none");
        }
        else
        {
            Line("state", now.Playback switch
            {
                SpotifyPlayback.Playing => "playing",
                SpotifyPlayback.Paused => "paused",
                _ => "stopped",
            });
            Line("title", now.Title);
            Line("artist", now.Artist);
            Line("album", now.Album);
            Line("time", Seconds(now.Position), Seconds(now.Length));
            var can = new StringBuilder();
            if (now.Controls.HasFlag(SpotifyControls.PlayPause)) can.Append('p');
            if (now.Controls.HasFlag(SpotifyControls.Previous)) can.Append('b');
            if (now.Controls.HasFlag(SpotifyControls.Next)) can.Append('n');
            // Without a stop of its own, Arkive pauses and goes back to the start, which needs seeking.
            if (now.Controls.HasFlag(SpotifyControls.Stop) ||
                (now.Controls.HasFlag(SpotifyControls.PlayPause) && now.Controls.HasFlag(SpotifyControls.Seek))) can.Append('s');
            if (now.Controls.HasFlag(SpotifyControls.Shuffle)) can.Append('h');
            if (now.Controls.HasFlag(SpotifyControls.Repeat)) can.Append('r');
            Line("can", can.ToString());
            Line("shuffle", now.Shuffle switch { true => "on", false => "off", null => "" });
            Line("repeat", now.Repeat switch
            {
                SpotifyRepeat.Off => "off",
                SpotifyRepeat.Track => "track",
                SpotifyRepeat.List => "list",
                _ => "",
            });
        }
        Line("volume", volume is { } percent ? percent.ToString(CultureInfo.InvariantCulture) : "");
        text.Append("layout\t").Append(ReadLayout(layout) ?? "\t\t0").Append('\n');
        return text.ToString();
    }

    /// <summary>A message from the tweak, or null when it isn't one Arkive knows.</summary>
    public static SpotifyRequest? ReadRequest(string text)
    {
        var parts = text.Split('\t');
        return parts switch
        {
            ["playpause"] => new(SpotifyCommand.PlayPause),
            ["next"] => new(SpotifyCommand.Next),
            ["previous"] => new(SpotifyCommand.Previous),
            ["stop"] => new(SpotifyCommand.Stop),
            ["shuffle"] => new(SpotifyCommand.Shuffle),
            ["repeat"] => new(SpotifyCommand.Repeat),
            ["volume", var value] when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) && percent <= 100
                => new(Volume: percent),
            ["layout", _, _, _] when ReadLayout(string.Join('\t', parts[1..])) is { } layout => new(Layout: layout),
            _ => null,
        };
    }

    /// <summary>"x&lt;tab&gt;y&lt;tab&gt;0|1" checked and tidied, or null when it isn't a layout.</summary>
    public static string? ReadLayout(string text)
    {
        var parts = text.Trim().Split('\t');
        if (parts.Length != 3 || parts[2] is not ("0" or "1") ||
            !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x) ||
            !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var y) ||
            Math.Abs(x) > MaxOffset || Math.Abs(y) > MaxOffset)
            return null;
        return string.Create(CultureInfo.InvariantCulture, $"{x}\t{y}\t{parts[2]}");
    }

    private static string Seconds(TimeSpan time) =>
        ((long)Math.Max(0, time.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    /// <summary>A field without the tabs and line breaks that separate fields and lines.</summary>
    private static string Clean(string value) =>
        value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
