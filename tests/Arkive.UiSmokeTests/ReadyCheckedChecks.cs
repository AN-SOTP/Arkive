using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Arkive.App;
using static Program;

/// <summary>
/// Launch shows the Mods list Arkive last verified at once (Ready) and keeps every change closed, with a note saying why,
/// until the mods have been checked again (Checked).
/// </summary>
internal static class ReadyCheckedChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>The game database the window has loaded, or null.</summary>
    public static object? GameDatabase(MainWindow window) =>
        typeof(MainWindow).GetField("_gameMetadataIndex", Private)!.GetValue(window);

    private static bool Checking(MainWindow window) => (bool)typeof(MainWindow).GetField("_modsChecking", Private)!.GetValue(window)!;

    /// <summary>Waits until launch has checked the mods, so the Mods tab's actions are open.</summary>
    public static Task UntilCheckedAsync(MainWindow window) =>
        UntilAsync(() => !Checking(window), () => "Launch did not finish checking the mods.", TimeSpan.FromSeconds(60));

    public static async Task VerifyAsync(string root, Func<string, MainWindow> create)
    {
        var remembered = Path.Combine(root, "cache", "mods-list-state.json");
        // The window just closed remembered its last verified list (written in the background after each refresh).
        await UntilAsync(() => File.Exists(remembered), () => "No Mods list was remembered for the next launch.");
        await Task.Delay(500);
        var delay = typeof(MainWindow).GetProperty("ModsCheckDelayForTests", BindingFlags.Static | BindingFlags.NonPublic)!;
        delay.SetValue(null, TimeSpan.FromSeconds(6));
        MainWindow? window = null;
        try
        {
            window = create(root);
            window.Show();
            var grid = Control<DataGrid>(window, "ModLibraryGrid");
            await UntilAsync(() => grid.Items.Count > 0, () => "The remembered Mods list did not appear before the mods were checked.");
            Check(Checking(window), "The remembered list was not on screen while the mods were being checked.");
            Check(Control<TextBlock>(window, "ModLibraryCheckingText").Visibility == Visibility.Visible,
                "The Mods tab does not say it is checking the mods' files.");
            string[] closed =
            [
                "ModLibraryApplyButton", "ModLibraryNewButton", "ModLibraryImportButton", "ModLibraryRefreshButton",
                "ModLibraryEnableButton", "ModLibraryRemoveButton", "ModLibraryCleanUpWorkingFilesButton",
                "ModLibraryPriorityUpButton", "ModLibraryPriorityDownButton", "ModLibraryEditButton", "ModLibrarySaveButton",
                "ModLibraryRestoreAllButton", "ModDetailsSaveButton",
            ];
            var open = closed.Where(name => Control<UIElement>(window, name).IsEnabled).ToArray();
            Check(open.Length == 0, "Actions that change mods were open before the mods were checked: " + string.Join(", ", open));

            // Browsing works meanwhile: choosing another mod shows its details, and the search box filters.
            var rows = grid.Items.Cast<object>().ToArray();
            var last = rows[^1];
            grid.SelectedItem = last;
            var name = (string)last.GetType().GetProperty("DisplayName")!.GetValue(last)!;
            Check(Control<TextBox>(window, "ModDetailsNameTextBox").Text == name, "Choosing a mod while checking did not show its details.");
            var search = Control<TextBox>(window, "ModLibrarySearchTextBox");
            search.Text = name;
            await UntilAsync(() => grid.Items.Count >= 1 && grid.Items.Count <= rows.Length,
                () => "The search box did not filter the remembered list.");
            search.Text = string.Empty;

            // A mod's On box does nothing while checking: the library file stays as it is and the box shows the mod as it is.
            var library = Path.Combine(root, "staging", "mod-library", "arkive-mod-library.json");
            var before = File.ReadAllBytes(library);
            var box = new CheckBox { DataContext = last, IsChecked = !(bool)last.GetType().GetProperty("Enabled")!.GetValue(last)! };
            typeof(MainWindow).GetMethod("ModLibraryEnabledCheckBox_Click", Private)!.Invoke(window, [box, new RoutedEventArgs()]);
            Check(box.IsChecked == (bool)last.GetType().GetProperty("Enabled")!.GetValue(last)! &&
                  File.ReadAllBytes(library).SequenceEqual(before),
                "Clicking a mod's On box while checking changed it.");
            Console.WriteLine("PASS: launch shows the remembered Mods list at once, with changes closed and a note, while browsing works");

            await UntilCheckedAsync(window);
            await UntilAsync(() => Control<Panel>(window, "BusyOverlay").Visibility != Visibility.Visible);
            Check(Control<TextBlock>(window, "ModLibraryCheckingText").Visibility == Visibility.Collapsed &&
                  new[] { "ModLibraryApplyButton", "ModLibraryNewButton", "ModLibraryRefreshButton", "ModLibraryImportButton",
                      "ModLibraryCleanUpWorkingFilesButton" }.All(name => Control<UIElement>(window, name).IsEnabled),
                "The Mods tab's actions did not open once the mods were checked.");
            Check(grid.Items.Count == rows.Length, "The checked Mods list does not hold the mods the remembered one showed.");
            var logPath = (string)typeof(MainWindow).GetField("_sessionLog", Private)!.GetValue(window)!
                .GetType().GetProperty("FilePath")!.GetValue(typeof(MainWindow).GetField("_sessionLog", Private)!.GetValue(window))!;
            string log;
            using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                log = reader.ReadToEnd();
            var ready = log.IndexOf("Startup ready in", StringComparison.Ordinal);
            var checkedAt = log.IndexOf("Startup checked in", StringComparison.Ordinal);
            Check(ready >= 0 && checkedAt > ready && log.IndexOf("(remembered list)", ready, StringComparison.Ordinal) is var note &&
                  note > ready && note < checkedAt,
                "The session log does not show Ready from the remembered list before Checked.");
            Console.WriteLine("PASS: once the mods are checked the note goes and every action opens; the log has Ready, then Checked");
        }
        finally
        {
            delay.SetValue(null, TimeSpan.Zero);
            if (window is not null)
            {
                window.Close();
                await UntilAsync(() => !window.IsVisible);
            }
        }
    }
}
