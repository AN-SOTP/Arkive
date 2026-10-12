using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Arkive.App;

internal sealed record BannerArtworkDefinition(
    string Id,
    string DisplayName,
    string Caption,
    string ResourcePath,
    Color Accent,
    Color SecondaryAccent,
    Rect Viewbox,
    string SourcePage);

internal static class BannerArtworkManager
{
    public const string RandomPreference = "Random";

    public static IReadOnlyList<BannerArtworkDefinition> Artwork { get; } =
    [
        new(
            "Thaemine",
            "Thaemine",
            "DARKNESS UNLEASHED",
            "Assets/Banners/thaemine.jpg",
            // Sampled from the artwork's dominant violet storm field and its deeper
            // indigo shadows; the former cyan palette was not present in this image.
            Color.FromRgb(84, 84, 204),
            Color.FromRgb(40, 40, 120),
            new Rect(0, 0.05, 1, 0.90),
            "https://www.playlostark.com/en-us/game/releases/thaemine-extreme"),
        new(
            "ThaemineCommander",
            "Thaemine — Legion Commander",
            "DARKNESS LEGION COMMANDER",
            "Assets/Banners/thaemine-commander.jpg",
            // This composition is storm-slate with small royal-blue armour effects,
            // rather than the bright azure used by the previous palette.
            Color.FromRgb(84, 108, 180),
            Color.FromRgb(56, 72, 88),
            new Rect(0, 0, 1, 1),
            "https://lostark.game.onstove.com/Artwork"),
        new(
            "Kazeros",
            "Kazeros",
            "ENDS OF THE ABYSS",
            "Assets/Banners/kazeros.jpg",
            Color.FromRgb(245, 195, 91),
            Color.FromRgb(198, 72, 51),
            new Rect(0, 0.05, 1, 0.90),
            "https://www.playlostark.com/en-us/game/releases/ends-of-the-abyss"),
        new(
            "KazerosAscendant",
            "Kazeros — Ascendant",
            "LORD OF THE ABYSS",
            "Assets/Banners/kazeros-ascendant.jpg",
            Color.FromRgb(255, 103, 72),
            Color.FromRgb(184, 40, 33),
            new Rect(0, 0, 1, 1),
            "https://lostark.game.onstove.com/Artwork"),
        new(
            "KazerosFinalDay",
            "Kazeros — Final Day",
            "THE FINAL DAY",
            "Assets/Banners/kazeros-final-day.jpg",
            Color.FromRgb(255, 104, 70),
            Color.FromRgb(126, 35, 31),
            new Rect(0, 0, 1, 1),
            "https://www.playlostark.com/en-us/game/releases/day-of-prophecy"),
        new(
            "Echidna",
            "Echidna",
            "DESIRE IN FULL BLOOM",
            "Assets/Banners/echidna.jpg",
            Color.FromRgb(244, 81, 137),
            Color.FromRgb(192, 51, 49),
            new Rect(0, 0.05, 1, 0.90),
            "https://www.playlostark.com/en-us/game/releases/summer-heat"),
        new(
            "Brelshaza",
            "Brelshaza",
            "THE BITTER COLD",
            "Assets/Banners/brelshaza.jpg",
            Color.FromRgb(185, 207, 255),
            Color.FromRgb(132, 94, 207),
            new Rect(0, 0.05, 1, 0.90),
            "https://www.playlostark.com/en-us/game/releases/the-bitter-cold"),
        new(
            "BrelshazaClassic",
            "Brelshaza — Classic",
            "PHANTOM LEGION COMMANDER",
            "Assets/Banners/brelshaza-classic.jpg",
            Color.FromRgb(169, 143, 255),
            Color.FromRgb(61, 58, 117),
            new Rect(0, 0, 1, 1),
            "https://www.playlostark.com/en-us/news/articles/2023-roadmap-part1"),
        new(
            "Nineveh",
            "Nineveh",
            "LIGHT OF ELGACIA",
            "Assets/Banners/nineveh.jpg",
            Color.FromRgb(112, 196, 226),
            Color.FromRgb(220, 181, 103),
            new Rect(0, 0, 1, 1),
            "https://www.playlostark.com/en-us/news/articles/elgacia-release-notes"),
        new(
            "Beatrice",
            "Beatrice",
            "KEEPER OF TRIXION",
            "Assets/Banners/beatrice.jpg",
            Color.FromRgb(92, 201, 235),
            Color.FromRgb(219, 188, 118),
            new Rect(0, 0, 1, 1),
            "https://lostark.ru/news/1665-podrobnosti-obnovleniya-izmeneniya-v-blagoslovenii-beatris"),
        new(
            "Akkan",
            "Akkan",
            "PLAGUE LEGION COMMANDER",
            "Assets/Banners/akkan.jpg",
            Color.FromRgb(105, 200, 75),
            Color.FromRgb(171, 62, 50),
            new Rect(0, 0, 1, 1),
            "https://www.playlostark.com/en-gb/news/articles/designing-horror-bosses"),
        new(
            "Vykas",
            "Vykas",
            "COVETOUS LEGION COMMANDER",
            "Assets/Banners/vykas.jpg",
            Color.FromRgb(240, 72, 91),
            Color.FromRgb(128, 42, 63),
            new Rect(0, 0, 1, 1),
            "https://www.playlostark.com/en-gb/news/articles/lost-ark-academy-preparing-for-vykas"),
        new(
            "Behemoth",
            "Behemoth",
            "CHASE THE STORM",
            "Assets/Banners/behemoth.jpg",
            Color.FromRgb(67, 151, 225),
            Color.FromRgb(190, 145, 86),
            new Rect(0, 0.05, 1, 0.90),
            "https://www.playlostark.com/en-us/game/releases/chase-the-storm"),
        new(
            "Aegir",
            "Aegir",
            "EARTH-SHATTERING HELLFIRE",
            "Assets/Banners/aegir.jpg",
            Color.FromRgb(225, 91, 52),
            Color.FromRgb(123, 105, 85),
            new Rect(0, 0.05, 1, 0.90),
            "https://www.playlostark.com/en-us/news/articles/aegir-release-notes"),
    ];

    public static string NormalizePreference(string? preference) =>
        string.Equals(preference, RandomPreference, StringComparison.OrdinalIgnoreCase)
            ? RandomPreference
            : Artwork.FirstOrDefault(
                    item => string.Equals(item.Id, preference, StringComparison.OrdinalIgnoreCase))
                ?.Id ?? RandomPreference;

    public static BannerArtworkDefinition ResolveLaunchSelection(string? preference)
    {
        var normalized = NormalizePreference(preference);
        if (!string.Equals(normalized, RandomPreference, StringComparison.Ordinal))
        {
            return Artwork.First(item => string.Equals(item.Id, normalized, StringComparison.Ordinal));
        }

        return Artwork[RandomNumberGenerator.GetInt32(Artwork.Count)];
    }

    public static BannerArtworkDefinition ResolveSettingsSelection(
        string? preference,
        BannerArtworkDefinition current)
    {
        var normalized = NormalizePreference(preference);
        return string.Equals(normalized, RandomPreference, StringComparison.Ordinal)
            ? current
            : Artwork.First(item => string.Equals(item.Id, normalized, StringComparison.Ordinal));
    }

    public static ImageSource LoadImage(BannerArtworkDefinition artwork)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(
            $"pack://application:,,,/Arkive.App;component/{artwork.ResourcePath}",
            UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static void ApplyPalette(BannerArtworkDefinition artwork, string? effectiveTheme)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        var resources = Application.Current.Resources;
        var light = string.Equals(effectiveTheme, "Light", StringComparison.OrdinalIgnoreCase);
        var pureBlack = string.Equals(effectiveTheme, "Black", StringComparison.OrdinalIgnoreCase);
        var canvas = pureBlack ? Colors.Black : Color.FromRgb(12, 12, 13);
        var accent = artwork.Accent;

        resources["AccentBrush"] = FrozenBrush(accent);
        resources["AccentHoverBrush"] = FrozenBrush(Mix(accent, Colors.White, light ? 0.08 : 0.16));
        resources["AccentPressedBrush"] = FrozenBrush(Mix(accent, Colors.Black, light ? 0.16 : 0.20));
        resources["AccentTextBrush"] = FrozenBrush(ContrastText(accent));

        resources["SelectionBrush"] = FrozenBrush(light
            ? Mix(accent, Colors.White, 0.78)
            : Mix(canvas, accent, 0.38));
        resources["SelectionTextBrush"] = FrozenBrush(light
            ? Color.FromRgb(19, 20, 23)
            : Colors.White);
        resources["InactiveSelectionBrush"] = FrozenBrush(light
            ? Mix(accent, Colors.White, 0.89)
            : Mix(canvas, accent, 0.20));
        resources["InactiveSelectionTextBrush"] = FrozenBrush(light
            ? Color.FromRgb(48, 49, 54)
            : Color.FromRgb(232, 232, 235));

        resources["InfoSurfaceBrush"] = FrozenBrush(light
            ? Mix(accent, Colors.White, 0.91)
            : Mix(canvas, accent, 0.13));
        resources["InfoBorderBrush"] = FrozenBrush(light
            ? Mix(accent, Colors.White, 0.30)
            : Mix(canvas, accent, 0.68));
        resources["InfoTextBrush"] = FrozenBrush(light
            ? Mix(accent, Colors.Black, 0.50)
            : Mix(accent, Colors.White, 0.55));
        resources["HeaderButtonHoverBrush"] = FrozenBrush(Mix(canvas, accent, 0.18));
        resources["HeaderButtonPressedBrush"] = FrozenBrush(Mix(canvas, accent, 0.28));
        resources["BannerBackdropBrush"] = CreateBannerBackdrop(artwork);
        resources["BannerAccentGlowBrush"] = CreateBannerGlow(artwork);
        AppearanceManager.RefreshSystemSelectionBrushes();
    }

    private static LinearGradientBrush CreateBannerBackdrop(BannerArtworkDefinition artwork)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };
        brush.GradientStops.Add(new GradientStop(Mix(artwork.Accent, Colors.Black, 0.84), 0));
        brush.GradientStops.Add(new GradientStop(Mix(artwork.Accent, Colors.Black, 0.78), 0.28));
        brush.GradientStops.Add(new GradientStop(Mix(artwork.Accent, Colors.Black, 0.62), 0.46));
        brush.GradientStops.Add(new GradientStop(Mix(artwork.Accent, Colors.Black, 0.46), 0.62));
        brush.GradientStops.Add(new GradientStop(Mix(artwork.Accent, Colors.Black, 0.34), 0.80));
        brush.GradientStops.Add(new GradientStop(Mix(artwork.Accent, Colors.Black, 0.25), 1));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateBannerGlow(BannerArtworkDefinition artwork)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(80, artwork.SecondaryAccent.R, artwork.SecondaryAccent.G,
                artwork.SecondaryAccent.B), 0));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(54, artwork.Accent.R, artwork.Accent.G, artwork.Accent.B), 0.48));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(0, artwork.Accent.R, artwork.Accent.G, artwork.Accent.B), 0.86));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    private static Color ContrastText(Color background)
    {
        var luminance = ((0.2126 * background.R) + (0.7152 * background.G) +
                         (0.0722 * background.B)) / 255d;
        return luminance > 0.62 ? Color.FromRgb(8, 8, 9) : Colors.White;
    }
}
