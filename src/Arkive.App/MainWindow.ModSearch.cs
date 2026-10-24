using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Arkive.Core.Mods;

namespace Arkive.App;

public partial class MainWindow : Window
{
    /// <summary>Every mod in the library, including mods the search box hides. Read this, not the grid's items.</summary>
    private ModLibraryRow[] _modLibraryRows = [];

    private string ModSearchText => ModLibrarySearchTextBox?.Text.Trim() ?? string.Empty;

    private bool ShowsInModSearch(object item) =>
        item is ModLibraryRow row && ArkiveModSearch.Matches(ModSearchText, row.Entry.Manifest, row.Group?.Pack);

    private void ModLibrarySearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyModSearch();

    private void ModLibrarySearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ModLibrarySearchTextBox.Text.Length == 0) return;
        ModLibrarySearchTextBox.Clear();
        e.Handled = true;
    }

    private void ModLibrarySearchClearButton_Click(object sender, RoutedEventArgs e)
    {
        ModLibrarySearchTextBox.Clear();
        ModLibrarySearchTextBox.Focus();
    }

    private void ApplyModSearch()
    {
        if (ModLibraryGrid?.ItemsSource is not ListCollectionView view) return;
        view.Refresh();
        if (ModLibraryGrid.SelectedItem is null && view.Count > 0) ModLibraryGrid.SelectedItem = view.GetItemAt(0);
        ShowModSearchCount(view.Count);
    }

    private void ShowModSearchCount(int shown)
    {
        var query = ModSearchText;
        ModLibrarySearchClearButton.IsEnabled = query.Length > 0;
        ModLibrarySearchCountText.Text = query.Length == 0
            ? string.Empty
            : shown == 0
                ? string.Format("No mods match “{0}”.", query)
                : string.Format("Showing {0:N0} of {1:N0} mods.", shown, _modLibraryRows.Length);
    }
}
