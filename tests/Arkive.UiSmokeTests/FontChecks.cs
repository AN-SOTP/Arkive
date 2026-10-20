using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Arkive.App;
using Arkive.Core.Fonts;
using Arkive.Core.Skills;
using static Program;

/// <summary>The Fonts tab shows the game's own fonts, applies a chosen one to font.lpk, and puts the game's back.</summary>
internal static class FontChecks
{
    internal static async Task VerifyAsync(string root, string? screenshotDirectory)
    {
        var library = Path.Combine(root, "font-library");
        var efGame = Path.Combine(library, "EFGame");
        var packages = Path.Combine(efGame, "ReleasePC", "Packages");
        Directory.CreateDirectory(packages);
        Directory.CreateDirectory(Path.Combine(library, "cache"));
        File.WriteAllText(Path.Combine(library, "cache", "arkive-settings.json"), JsonSerializer.Serialize(new { LibraryRootPath = packages }));
        // Windows' own fonts stand in for the game's, so the previews have real fonts to show.
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var archive = Path.Combine(efGame, "font.lpk");
        LostArkDataArchive.Create(
        [
            new("/Binaries/Fonts/English/ExuberanceF-Regular.ttf", File.ReadAllBytes(Path.Combine(fonts, "arial.ttf"))),
            new("/Binaries/Fonts/English/FontMap.xml", Encoding.UTF8.GetBytes("""
                <FontMap>
                    <Item Key="$YG760" File="ExuberanceF-Regular.ttf" />
                    <Item Key="$YoonGasiIIM" File="MonarchaF-Regular.ttf" />
                    <Item Key="$eventDamageFont" File="MonarchaF-Regular.ttf" />
                </FontMap>
                """)),
            new("/Binaries/Fonts/English/MonarchaF-Regular.ttf", File.ReadAllBytes(Path.Combine(fonts, "times.ttf"))),
        ], archive);
        var clean = File.ReadAllBytes(archive);
        var bad = Path.Combine(root, "not-a-font.ttf");
        File.WriteAllText(bad, "not a font");

        var window = new MainWindow(library)
        {
            Width = 1440, Height = 900, Left = -5000, Top = -5000, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        try
        {
            var status = Control<TextBlock>(window, "FontStatusText");
            var apply = Control<Button>(window, "FontApplyButton");
            var restore = Control<Button>(window, "FontRestoreButton");
            await UntilAsync(() => window.IsLoaded, () => "The window did not finish loading.");
            Control<TabControl>(window, "MainTabControl").SelectedItem = Control<TabItem>(window, "FontsTab");
            await UntilAsync(() => status.Text.StartsWith("The game uses its own fonts", StringComparison.Ordinal),
                () => $"The Fonts tab did not read the game's fonts: {status.Text}", TimeSpan.FromSeconds(30));
            var service = (GameFontService)typeof(MainWindow).GetField("_gameFonts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(service.ArchivePath.Equals(archive, StringComparison.OrdinalIgnoreCase), $"The Fonts tab found another font.lpk: {service.ArchivePath}");
            var rows = (FontSlotRow[])Control<ItemsControl>(window, "FontSlotList").ItemsSource;
            Check(rows.Select(row => row.Title).SequenceEqual(["Body Text", "Titles", "Damage Numbers"]) &&
                  rows[0].Choice == "The game's own (Arial)" && rows[1].Choice == "The game's own (Times New Roman)" &&
                  rows[2].Choice == "Same as titles (Times New Roman)" && rows.All(row => row.Preview is not null && !row.Chosen),
                "The Fonts tab does not show the game's own fonts: " + string.Join(" | ", rows.Select(row => $"{row.Title}: {row.Choice}")));
            Check(!apply.IsEnabled && !restore.IsEnabled, "Apply or Back to the game's own is enabled with nothing to do.");
            Console.WriteLine("PASS: the Fonts tab shows the game's own body text, titles and damage number fonts with previews");

            Choose(window, GameFontSlot.BodyText, bad);
            Check(status.Text.Contains("cannot be used", StringComparison.Ordinal) && !rows[0].Chosen && !apply.IsEnabled,
                "A file that is no font was accepted: " + status.Text);
            Choose(window, GameFontSlot.BodyText, Path.Combine(fonts, "georgia.ttf"));
            Choose(window, GameFontSlot.DamageNumbers, Path.Combine(fonts, "impact.ttf"));
            Check(rows[0] is { Chosen: true, Choice: "Georgia (georgia.ttf)" } && rows[2].Choice == "Impact (impact.ttf)" && apply.IsEnabled,
                "A chosen font is not shown or cannot be applied: " + string.Join(" | ", rows.Select(row => row.Choice)));
            if (screenshotDirectory is not null) Capture(window, screenshotDirectory, "fonts.png");
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => status.Text.StartsWith("Your fonts are applied", StringComparison.Ordinal) && restore.IsEnabled,
                () => $"Applying fonts did not finish: {status.Text}", TimeSpan.FromSeconds(30));
            using (var stream = File.OpenRead(archive))
            {
                var entries = LostArkDataArchive.ReadEntries(stream);
                Check(LostArkDataArchive.Read(stream, entries.First(entry => entry.Path.EndsWith("ExuberanceF-Regular.ttf", StringComparison.Ordinal)))
                          .SequenceEqual(File.ReadAllBytes(Path.Combine(fonts, "georgia.ttf"))) &&
                      entries.Any(entry => entry.Path.EndsWith("ArkiveDamageNumbers.ttf", StringComparison.Ordinal)),
                    "The applied font.lpk does not hold the chosen fonts.");
            }
            Check(!apply.IsEnabled && restore.IsEnabled && rows[0].Choice == "Georgia (georgia.ttf)",
                "After applying, the tab does not offer going back, or lost the chosen font's name.");
            Console.WriteLine("PASS: choosing fonts refuses non-fonts, shows previews, and Apply fonts writes them into font.lpk");

            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => status.Text.StartsWith("Back to the game's own fonts", StringComparison.Ordinal) && !restore.IsEnabled && Control<ItemsControl>(window, "FontSlotList").IsEnabled,
                () => $"Going back did not finish: {status.Text}", TimeSpan.FromSeconds(30));
            Check(File.ReadAllBytes(archive).SequenceEqual(clean) && !restore.IsEnabled && rows.All(row => !row.Chosen) &&
                  rows[0].Choice == "The game's own (Arial)", "Back to the game's own did not put font.lpk back exactly, or the tab did not update.");
            Console.WriteLine("PASS: Back to the game's own puts font.lpk back byte for byte and the tab shows the game's fonts again");
        }
        finally
        {
            window.Close();
        }
    }

    // What Choose font… does once a file is picked (the file dialog itself cannot be driven).
    private static void Choose(MainWindow window, GameFontSlot slot, string path) =>
        typeof(MainWindow).GetMethod("ChooseFont", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [slot, path]);
}
