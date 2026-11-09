using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Arkive.App;

/// <summary>A light-header banner (Marian) recolours the header, and choosing another banner gives the theme's header back.</summary>
internal static class BannerHeaderChecks
{
    public static void Verify()
    {
        var manager = typeof(MainWindow).Assembly.GetType("Arkive.App.BannerArtworkManager")!;
        var all = ((System.Collections.IEnumerable)manager.GetProperty("Artwork", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!)
            .Cast<object>().ToArray();
        bool Light(object artwork) => (bool)artwork.GetType().GetProperty("LightHeader")!.GetValue(artwork)!;
        string Id(object artwork) => (string)artwork.GetType().GetProperty("Id")!.GetValue(artwork)!;
        var apply = manager.GetMethod("ApplyPalette", BindingFlags.Static | BindingFlags.Public)!;
        Color Brush(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;
        Color HeaderText() => Brush("HeaderTextBrush");

        var other = all.First(artwork => !Light(artwork));
        apply.Invoke(null, [other, "Light"]);
        var themeHeader = HeaderText();
        var themeBorder = Brush("HeaderButtonBorderBrush");
        var marian = all.Single(artwork => Id(artwork) == "MarianLastAffection");
        apply.Invoke(null, [marian, "Light"]);
        Program.Check(Light(marian) && HeaderText() != themeHeader,
            "The Marian banner did not give the header its light colours.");
        apply.Invoke(null, [other, "Light"]);
        Program.Check(HeaderText() == themeHeader && Brush("HeaderButtonBorderBrush") == themeBorder,
            "Choosing another banner after Marian kept Marian's light header colours.");
        Console.WriteLine("PASS: the Marian banner lights the header, and another banner gives the theme's header back");
    }
}
