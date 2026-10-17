using System.Windows;
using System.Windows.Controls;

namespace Arkive.App;

/// <summary>
/// A small window shown while Arkive loads its mods and the game database at startup, which takes a while with
/// many mods or large drafts. It closes itself when loading finishes; closing it early only hides it.
/// </summary>
public partial class StartupProgressWindow : Window
{
    public StartupProgressWindow() => InitializeComponent();

    public void ShowMods(string text, bool done = false) => Show(ModsText, ModsProgress, text, done);

    public void ShowDatabase(string text, bool done = false) => Show(DatabaseText, DatabaseProgress, text, done);

    private static void Show(TextBlock label, ProgressBar bar, string text, bool done)
    {
        label.Text = text;
        bar.IsIndeterminate = !done;
        bar.Value = done ? bar.Maximum : 0;
    }
}
