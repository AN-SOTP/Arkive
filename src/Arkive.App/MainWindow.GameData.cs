using System.Windows;
using Arkive.Core.GameData;
using Arkive.Core.Mods;

namespace Arkive.App;

// Game Data mods in the Mods tab: the Game tables window that makes them and changes their table cells.
public partial class MainWindow
{
    private async void ModLibraryGameTablesButton_Click(object sender, RoutedEventArgs e) => await OpenGameTablesAsync(null);

    private async void ModLibraryEditTablesButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is ModLibraryRow row) await OpenGameTablesAsync(row.Entry);
    }

    /// <summary>Opens the Game tables window for <paramref name="mod"/>, or for a new mod, and saves what it returns.</summary>
    private async Task OpenGameTablesAsync(ArkiveModLibraryEntry? mod)
    {
        if (!ModLibraryIdle) return;
        if (_arkiveModContext.GameDataFor(_arkiveModLibraryManifest) is not { } tables)
        {
            MessageBox.Show(this,
                "Arkive needs to know where Lost Ark is installed first. Choose More actions → Game folder… and pick the game's Packages folder.",
                "Game tables", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var window = new GameDataWindow(tables.Archive, mod?.Manifest.Name ?? string.Empty, newMod: mod is null,
            mayBeEmpty: mod is { Manifest.Assets.Count: > 0 }, mod?.Manifest.GameData?.Edits ?? []) { Owner = this };
        if (window.ShowDialog() != true) return;
        await RunModLibraryOperationAsync("Game tables", async () =>
        {
            var saved = await GameDataAuthoring.SaveModAsync(_arkiveModLibrary, mod?.ModId,
                mod is null ? new ArkiveModMetadata(window.ModName, "1.0.0") : null, window.Edits);
            Log($"Game Data mod saved: mod={saved.ModId}, name={saved.Manifest.Name}, cells={window.Edits.Count}, " +
                $"databases={string.Join(", ", window.Edits.Select(edit => edit.Database).Distinct())}.");
            return mod is null
                ? $"Created {saved.Manifest.Name} with {window.Edits.Count:N0} table cell(s). Turn it on, then choose Apply changes."
                : $"Saved {window.Edits.Count:N0} table cell(s) in {saved.Manifest.Name}. Choose Apply changes to update the game.";
        }, mod?.ModId);
    }
}
