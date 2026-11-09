using System.Globalization;
using System.Text;

namespace Arkive.Core.Tweaks;

/// <summary>
/// A message from the Spotify Player tweak: a button (<see cref="Command"/>), Spotify's volume in percent, where the
/// player was dragged to and whether it is folded ("x&lt;tab&gt;y&lt;tab&gt;0|1"), a point of the track to go to (seconds),
/// whether the game could draw the pictures Arkive sent ("shown" or "failed", for the log), or a line for the log about
/// what the player found in the game (<see cref="Note"/>) or what went wrong there (<see cref="Error"/>).
/// </summary>
public sealed record SpotifyRequest(SpotifyCommand? Command = null, int? Volume = null, string? Layout = null, int? Seek = null,
    string? Art = null, string? Note = null, string? Error = null);

/// <summary>
/// The Arkive half of the Spotify Player tweak's files. While the game runs, Arkive writes Spotify's state to
/// &lt;tweaks&gt;/spotify/now.txt every second (the tweak reads it as a plain file), and the tweak
/// sends its buttons on the Data Bus channel "spotify". The file is lines of tab-separated fields:
/// "spotify, sequence" (the sequence goes up with every write, so the tweak sees when Arkive stops), "state, none |
/// playing | paused | stopped", "title", "artist", "album", "time, seconds in, seconds long", "can, letters" (p play and
/// pause, b back, n next, s stop, h shuffle, r repeat, k jump to a point), "shuffle, on | off", "repeat, off | track | list" (empty when
/// Spotify doesn't say), "volume, percent" (empty when Spotify plays no sound yet), "art, id", "layout, x, y, 0 | 1" and
/// "end, sequence" (so the tweak can tell a file it got only part of).
/// <para>
/// The file is plain ASCII: the game lost every line after a Japanese title and artist, so the title, artist and album
/// spell any other character (and the backslash) as "\u" and four hex digits, which the tweak turns back.
/// </para>
/// <para>
/// The pictures are PNG files beside it ("name-id.png", <see cref="PictureFile"/>), which the tweak loads the way the
/// game loads its own images, and again in art.txt for when the game won't load them: a first line "pictures, id,
/// names..." and then one line each: "image, name, width, height, pixels" with the pixels as eight hex digits per
/// pixel (alpha, red, green, blue), row by row. "cover" is the track's cover; "title" and "artist" are the song's name
/// and artist drawn by Arkive when they have letters the game's fonts lack (Japanese, Korean...). The art id changes
/// with them, so the tweak reads art.txt again only for a new track.
/// </para>
/// </summary>
public static class SpotifyPlayer
{
    /// <summary>The tweak's id, and its Data Bus channel.</summary>
    public const string Tweak = "spotify";

    public const string Channel = "spotify";

    /// <summary>The file the tweak reads, in <see cref="TweakModService.SpotifyFolder"/>.</summary>
    public const string StateFile = "now.txt";

    /// <summary>
    /// Where the player was last left, in the same folder ("x&lt;tab&gt;y&lt;tab&gt;0|1"). Not layout.txt: the first
    /// drag code saved the minimap's corner there, so a player kept from then would open in the wrong place.
    /// </summary>
    public const string LayoutFile = "place.txt";

    /// <summary>The cover and drawn titles, in the same folder.</summary>
    public const string ArtFile = "art.txt";

    /// <summary>The cover's size in pixels; the player shows it a little smaller.</summary>
    public const int CoverSize = 64;

    /// <summary>
    /// The widest the drawn song name and artist may be: their columns beside the cover when the player is as wide as
    /// the game's default minimap (327 px). The tweak cuts them at its own columns when it is narrower.
    /// </summary>
    public const int TitleWidth = 219, ArtistWidth = 241;

    /// <summary>The farthest a dragged player may sit from its spot under the minimap, in interface pixels.</summary>
    private const int MaxOffset = 8000;

    /// <summary>The file's text for Spotify's state (null: Spotify isn't running), its volume and the saved layout.</summary>
    public static string Describe(SpotifyNowPlaying? now, int? volume, string layout, long sequence, string art = "")
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
            Line("title", Escape(Clean(now.Title)));
            Line("artist", Escape(Clean(now.Artist)));
            Line("album", Escape(Clean(now.Album)));
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
            if (now.Controls.HasFlag(SpotifyControls.Seek)) can.Append('k');
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
        if (now is not null)
            Line("art", art);
        text.Append("layout\t").Append(ReadLayout(layout) ?? "\t\t0").Append('\n');
        Line("end", sequence.ToString(CultureInfo.InvariantCulture));
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
            ["seek", var value] when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds < 24 * 3600
                => new(Seek: seconds),
            ["art", "shown" or "failed"] => new(Art: parts[1]),
            ["note", _, ..] => new(Note: Clean(string.Join(' ', parts[1..]))),
            ["error", _, ..] => new(Error: Clean(string.Join(' ', parts[1..]))),
            ["layout", _, _, _] when ReadLayout(string.Join('\t', parts[1..])) is { } layout => new(Layout: layout),
            _ => null,
        };
    }

    /// <summary>True when a check last made at <paramref name="last"/> (null: never) is due again, every <paramref name="every"/> ms.</summary>
    public static bool IsDue(long now, long? last, long every) => last is not { } at || now - at >= every;

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

    /// <summary>
    /// True when the text has letters past Latin (the game's English, French, German and Spanish fonts have Latin only),
    /// so Arkive draws it as a picture rather than leave the game to show empty boxes.
    /// </summary>
    public static bool NeedsDrawing(string text) =>
        text.Any(character => character is > '\u017f' and not (>= '\u2010' and <= '\u203a') and not '\u20ac');

    /// <summary>The first line of art.txt: the art id and the names of the pictures that follow (and have PNG files).</summary>
    public static string PicturesLine(string id, IEnumerable<string> names) =>
        "pictures\t" + id + string.Concat(names.Select(name => "\t" + name)) + "\n";

    /// <summary>A picture's PNG file name in the Spotify folder; the art id in it means the game never shows a cached old one.</summary>
    public static string PictureFile(string name, string id) => name + "-" + id + ".png";

    /// <summary>One "image" line of art.txt: pixels as ARGB values, row by row.</summary>
    public static string ImageLine(string name, int width, int height, ReadOnlySpan<uint> pixels)
    {
        if (pixels.Length != width * height)
            throw new ArgumentException("The picture's pixels don't match its size.", nameof(pixels));
        var text = new StringBuilder(name.Length + 24 + pixels.Length * 8);
        text.Append("image\t").Append(name).Append('\t').Append(width.ToString(CultureInfo.InvariantCulture))
            .Append('\t').Append(height.ToString(CultureInfo.InvariantCulture)).Append('\t');
        foreach (var pixel in pixels)
            text.Append(pixel.ToString("x8", CultureInfo.InvariantCulture));
        return text.Append('\n').ToString();
    }

    private static string Seconds(TimeSpan time) =>
        ((long)Math.Max(0, time.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    /// <summary>The text with every character past printable ASCII, and the backslash, as "\u" and four hex digits.</summary>
    public static string Escape(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is >= ' ' and <= '~' and not '\\')
                text.Append(character);
            else
                text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    /// <summary>A field without the tabs and line breaks that separate fields and lines.</summary>
    private static string Clean(string value) =>
        value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
