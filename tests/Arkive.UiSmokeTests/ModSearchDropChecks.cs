using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arkive.App;
using Arkive.Core.Mods;
using static Program;

/// <summary>The Mods list search box filters as you type, and a .arkive file dropped on the window is imported after the usual question.</summary>
internal static class ModSearchDropChecks
{
    public static async Task VerifyAsync(MainWindow window, string root, string? screenshotDirectory)
    {
        var grid = Control<DataGrid>(window, "ModLibraryGrid");
        var search = Control<TextBox>(window, "ModLibrarySearchTextBox");
        var count = Control<TextBlock>(window, "ModLibrarySearchCountText");
        var clear = Control<Button>(window, "ModLibrarySearchClearButton");
        var status = Control<TextBlock>(window, "ModLibraryStatusText");
        await UntilAsync(() => Control<Button>(window, "ModLibraryApplyButton").IsEnabled);
        var all = Names(grid);
        Check(all.Length >= 3 && search.Text.Length == 0 && count.Text.Length == 0 && !clear.IsEnabled,
            $"The Mods list search did not start empty with every mod shown: mods={string.Join(", ", all)}, search={search.Text}.");

        search.Text = "SKY";
        Check(Names(grid).SequenceEqual(["UI pack - sky"]) && count.Text == $"Showing 1 of {all.Length:N0} mods." && clear.IsEnabled &&
              grid.SelectedItem is not null && Name(grid.SelectedItem) == "UI pack - sky",
            $"Typing part of a name did not filter the Mods list to that mod: shown={string.Join(", ", Names(grid))}, count={count.Text}.");
        search.Text = "UI author";
        Check(Names(grid).Length == 1 && Author(grid.Items[0]) == "UI author",
            "Typing an author did not show only that author's mod: " + string.Join(", ", Names(grid)));
        search.Text = "ui -pack";
        Check(Names(grid).SequenceEqual(["UI renamed mod"]), "A word starting with - did not leave out matching mods: " + string.Join(", ", Names(grid)));
        search.Text = "zzz";
        Check(grid.Items.Count == 0 && count.Text == "No mods match “zzz”." && grid.SelectedItem is null,
            $"A search with no match did not say so: shown={grid.Items.Count}, count={count.Text}.");

        search.Text = "sky";
        Invoke(window, "ModLibraryRefreshButton_Click", null, new RoutedEventArgs());
        await UntilAsync(() => Control<Button>(window, "ModLibraryApplyButton").IsEnabled && Control<Panel>(window, "BusyOverlay").Visibility != Visibility.Visible);
        Check(Names(grid).SequenceEqual(["UI pack - sky"]) && count.Text == $"Showing 1 of {all.Length:N0} mods.",
            "Refreshing the Mods list lost the search: " + string.Join(", ", Names(grid)));
        if (screenshotDirectory is not null) Capture(window, screenshotDirectory, "mod-search.png");
        Check(window.GetType().GetField("_modLibraryRows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is Array { Length: var total } &&
              total == all.Length && Control<Button>(window, "ModLibraryNewPackButton").IsEnabled,
            "A search changed what the rest of the Mods page counts as the library.");
        typeof(MainWindow).GetMethod("ModLibrarySearchTextBox_KeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [search, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search)!, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            }]);
        Check(search.Text.Length == 0 && Names(grid).Length == all.Length && count.Text.Length == 0 && !clear.IsEnabled,
            $"Esc did not clear the search and show every mod again: search={search.Text}, shown={grid.Items.Count}.");
        Console.WriteLine("PASS: the Mods list search filters by name, author or pack as you type, keeps its filter on refresh, and Esc clears it");

        var downloads = Path.Combine(root, "downloads");
        var dropped = Path.Combine(downloads, "UI dropped mod.arkive");
        ModPackChecks.WriteMod(dropped, "UI dropped mod");
        var notes = Path.Combine(downloads, "notes.txt");
        File.WriteAllText(notes, "not a mod");
        var data = new DataObject(DataFormats.FileDrop, new[] { dropped, notes, Path.Combine(downloads, "missing.arkive"), downloads });
        var files = (IReadOnlyList<string>)typeof(MainWindow).GetMethod("DroppedModFiles", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [data])!;
        Check(files.SequenceEqual([Path.GetFullPath(dropped)]),
            "Dropping files took more than the .arkive mod that exists: " + string.Join(", ", files));
        Check(((IReadOnlyList<string>)typeof(MainWindow).GetMethod("DroppedModFiles", BindingFlags.Static | BindingFlags.NonPublic)!
                  .Invoke(null, [new DataObject(DataFormats.FileDrop, new[] { notes })])!).Count == 0,
            "Dropping a file that is not a mod offered to import it.");

        var answer = typeof(MainWindow).GetProperty("AnswerImportQuestion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var questions = new List<string>();
        answer.SetValue(window, (Func<string, bool>)(question => { questions.Add(question); return false; }));
        var mods = Path.Combine(root, "mods");
        var before = Directory.GetFiles(mods, "*.arkive").Length;
        try
        {
            await Drop(window, files);
            Check(questions.Count == 1 && questions[0].StartsWith("Import UI dropped mod?", StringComparison.Ordinal) &&
                  Directory.GetFiles(mods, "*.arkive").Length == before && !Names(grid).Contains("UI dropped mod"),
                $"Answering No to a dropped mod still imported it, or was not asked: asked={questions.Count}, status={status.Text}.");

            search.Text = "sky";
            answer.SetValue(window, (Func<string, bool>)(question => { questions.Add(question); return true; }));
            await Drop(window, files);
            await UntilAsync(() => Rows(grid).FirstOrDefault(row => Name(row) == "UI dropped mod") is { } row && Enabled(row) &&
                                   grid.SelectedItem == row && Control<Button>(window, "ModLibraryApplyButton").IsEnabled,
                () => $"Answering Yes to a dropped mod did not import it, turn it on and show it: shown={string.Join(", ", Names(grid))}, " +
                      $"search={search.Text}, status={status.Text}.");
            Check(questions.Count == 2 && search.Text.Length == 0 && Directory.GetFiles(mods, "*.arkive").Length == before + 1,
                $"The dropped mod was not asked about once, or the search still hid it: asked={questions.Count}, search={search.Text}.");
        }
        finally
        {
            answer.SetValue(window, null);
        }
        Console.WriteLine("PASS: a .arkive file dropped on Arkive is imported after the usual question, and No leaves the library as it was");
    }

    private static async Task Drop(MainWindow window, IReadOnlyList<string> files)
    {
        await UntilAsync(() => Control<Button>(window, "ModLibraryApplyButton").IsEnabled && Control<Panel>(window, "BusyOverlay").Visibility != Visibility.Visible);
        await (Task)typeof(MainWindow).GetMethod("ImportDroppedModFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [files])!;
    }

    private static IEnumerable<object> Rows(DataGrid grid) => grid.Items.Cast<object>();

    private static string[] Names(DataGrid grid) => Rows(grid).Select(Name).ToArray();

    private static string Name(object row) => (string)row.GetType().GetProperty("DisplayName")!.GetValue(row)!;

    private static string Author(object row) => (string)row.GetType().GetProperty("Author")!.GetValue(row)!;

    private static bool Enabled(object row) => (bool)row.GetType().GetProperty("Enabled")!.GetValue(row)!;

    private static void Invoke(MainWindow window, string method, object? sender, RoutedEventArgs e) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [sender, e]);
}
