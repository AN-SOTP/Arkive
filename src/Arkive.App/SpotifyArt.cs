using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arkive.Core.Tweaks;

namespace Arkive.App;

/// <summary>
/// The Spotify Player's pictures (see <see cref="SpotifyPlayer"/>): the track's cover cut square and scaled down, and the
/// song name and artist drawn when the game's fonts can't show them. Runs on the UI thread (WPF imaging).
/// </summary>
internal static class SpotifyArt
{
    /// <summary>The player's song name and artist colours and sizes (white 14 px, grey 12 px), as the tweak draws them.</summary>
    private const double TitleSize = 14, ArtistSize = 12;
    private static readonly Color TitleColor = Color.FromRgb(0xFF, 0xFF, 0xFF), ArtistColor = Color.FromRgb(0x9A, 0x9A, 0x9A);

    /// <summary>Windows fonts that have Japanese, Korean and Chinese letters, tried in turn for each letter.</summary>
    private static readonly FontFamily DrawingFont = new("Malgun Gothic, Yu Gothic UI, Microsoft YaHei UI, Segoe UI");

    /// <summary>The text of art.txt for a cover image file (null: none) and the track's name and artist.</summary>
    public static string Build(byte[]? cover, string title, string artist)
    {
        var text = new StringBuilder();
        if (cover is not null && Cover(cover) is { } pixels)
            text.Append(SpotifyPlayer.ImageLine("cover", SpotifyPlayer.CoverSize, SpotifyPlayer.CoverSize, pixels));
        if (SpotifyPlayer.NeedsDrawing(title))
            text.Append(Words("title", title, TitleSize, TitleColor, SpotifyPlayer.TitleWidth));
        if (SpotifyPlayer.NeedsDrawing(artist))
            text.Append(Words("artist", artist, ArtistSize, ArtistColor, SpotifyPlayer.ArtistWidth));
        return text.ToString();
    }

    /// <summary>The middle square of the cover, scaled smoothly to the player's size; null when it isn't a picture WPF reads.</summary>
    private static uint[]? Cover(byte[] image)
    {
        BitmapSource frame;
        try
        {
            frame = BitmapFrame.Create(new MemoryStream(image), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch (Exception exception) when (exception is NotSupportedException or FileFormatException or ArgumentException or IOException)
        {
            return null;
        }
        var side = Math.Min(frame.PixelWidth, frame.PixelHeight);
        if (side <= 0)
            return null;
        var square = new CroppedBitmap(frame, new Int32Rect((frame.PixelWidth - side) / 2, (frame.PixelHeight - side) / 2, side, side));
        var size = SpotifyPlayer.CoverSize;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var context = visual.RenderOpen())
            context.DrawImage(square, new Rect(0, 0, size, size));
        var pixels = Render(visual, size, size);
        // A cover is opaque: fill any see-through edge left by the scaling.
        for (var index = 0; index < pixels.Length; index++)
            pixels[index] |= 0xFF000000;
        return pixels;
    }

    /// <summary>A line of text in the player's colour and size, cut short with an ellipsis at `width`.</summary>
    private static string Words(string name, string value, double size, Color color, int width)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var formatted = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(DrawingFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, 1.0)
        {
            MaxTextWidth = width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var pixelWidth = Math.Clamp((int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace), 1, width);
        var pixelHeight = (int)Math.Ceiling(size + 6);
        var visual = new DrawingVisual();
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        using (var context = visual.RenderOpen())
            context.DrawText(formatted, new Point(0, (pixelHeight - formatted.Height) / 2));
        return SpotifyPlayer.ImageLine(name, pixelWidth, pixelHeight, Render(visual, pixelWidth, pixelHeight));
    }

    /// <summary>The visual's pixels as straight (not premultiplied) ARGB values, row by row.</summary>
    private static uint[] Render(Visual visual, int width, int height)
    {
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var bytes = new byte[width * height * 4];
        target.CopyPixels(bytes, width * 4, 0);
        var pixels = new uint[width * height];
        for (var index = 0; index < pixels.Length; index++)
        {
            var at = index * 4;
            uint alpha = bytes[at + 3];
            uint Straight(byte value) => alpha == 0 ? 0 : (uint)Math.Min(255, value * 255 / alpha);
            pixels[index] = alpha << 24 | Straight(bytes[at + 2]) << 16 | Straight(bytes[at + 1]) << 8 | Straight(bytes[at]);
        }
        return pixels;
    }
}
