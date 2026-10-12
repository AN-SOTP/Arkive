using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arkive.App;
using static Program;

internal static class WorkflowLayoutChecks
{
    internal static void Exercise(MainWindow window)
    {
        var tabs = Control<TabControl>(window, "MainTabControl");
        var search = Control<Expander>(window, "AuthoringSearchExpander");
        window.UpdateLayout();
        Check(!Control<Expander>(window, "AuthoringMetadataExpander").IsExpanded &&
              !Control<Expander>(window, "LibrarySetupExpander").IsExpanded,
            "Setup and metadata occupy the tables' workspace by default.");
        var searchRow = Control<RowDefinition>(window, "AuthoringSearchRow");
        var assetsRow = Control<RowDefinition>(window, "AuthoringAssetsRow");
        var workspaceHeight = Control<Grid>(window, "AuthoringPaneGrid").ActualHeight;
        var minimumTableHeight = Math.Clamp(workspaceHeight * 0.25, 45, 180);
        var tableHeight = Control<DataGrid>(window, "WorkshopSearchResultsGrid").ActualHeight;
        Check(searchRow.ActualHeight > assetsRow.ActualHeight &&
              tableHeight >= minimumTableHeight,
            $"The default search pane does not use its available workspace: " +
            $"table={tableHeight:N0}, required={minimumTableHeight:N0}, " +
            $"search={searchRow.ActualHeight:N0}, assets={assetsRow.ActualHeight:N0}, workspace={workspaceHeight:N0}.");
        var before = searchRow.ActualHeight;
        Resize(window, "AuthoringPaneSplitter", Key.Up);
        Check(searchRow.ActualHeight > before + 10, "The authoring tables cannot be resized with their splitter.");
        var resizedRatio = searchRow.ActualHeight / (searchRow.ActualHeight + assetsRow.ActualHeight);
        before = assetsRow.ActualHeight;
        search.IsExpanded = false;
        window.UpdateLayout();
        Check(assetsRow.ActualHeight > before &&
              Control<GridSplitter>(window, "AuthoringPaneSplitter").Visibility == Visibility.Collapsed,
            "Collapsing search does not return its space to working assets.");
        search.IsExpanded = true;
        window.UpdateLayout();
        Check(Math.Abs(resizedRatio - searchRow.ActualHeight / (searchRow.ActualHeight + assetsRow.ActualHeight)) < 0.02,
            "Expanding search loses the user's pane size.");
        Check(Control<DataGrid>(window, "WorkshopSearchResultsGrid").CanUserResizeColumns &&
              Control<DataGrid>(window, "AuthoringAssetsGrid").CanUserResizeColumns,
            "Authoring table columns cannot be resized.");
        Console.WriteLine("PASS: authoring tables resize, collapse without dead space, and retain their split");

        tabs.SelectedItem = Control<TabItem>(window, "ModifiedUpksTab");
        window.UpdateLayout();
        var detailsRow = Control<RowDefinition>(window, "ModLibraryDetailsRow");
        before = detailsRow.ActualHeight;
        Resize(window, "ModLibraryPaneSplitter", Key.Down);
        Check(detailsRow.ActualHeight < before - 10, "The mod and detail tables cannot be resized.");
        Check(!Control<TabItem>(window, "ModLibraryTab").IsVisible,
            "The redundant inner Mod library tab strip still occupies space.");
        Console.WriteLine("PASS: mod details resize without a redundant inner tab strip");

        tabs.SelectedItem = Control<TabItem>(window, "WorkshopTab");
        var width = window.Width;
        var height = window.Height;
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.UpdateLayout();
        var grid = Control<Grid>(window, "AuthoringPaneGrid");
        Check(assetsRow.ActualHeight + searchRow.ActualHeight + 8 <= grid.ActualHeight + 1,
            "Authoring panes overflow at the minimum window size.");
        Check(Control<DataGrid>(window, "WorkshopSearchResultsGrid").ActualHeight >= 45,
            $"Search results become inaccessible at the minimum window size: " +
            $"table={Control<DataGrid>(window, "WorkshopSearchResultsGrid").ActualHeight:N0}, " +
            $"pane={searchRow.ActualHeight:N0}, workspace={grid.ActualHeight:N0}.");
        window.Width = width;
        window.Height = height;
        window.UpdateLayout();
        Console.WriteLine("PASS: resizable panes remain bounded at the minimum window size");
    }

    internal static void VerifyRestored(MainWindow window, string root)
    {
        Check(Control<Button>(window, "ModLibraryNewButton").IsVisible,
            "The mod library has no visible content after restarting.");
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "cache", "arkive-settings.json")));
        foreach (var (upper, lower, property) in new[]
        {
            ("AuthoringAssetsRow", "AuthoringSearchRow", "AuthoringSearchPaneRatio"),
            ("ModLibraryModsRow", "ModLibraryDetailsRow", "ModDetailsPaneRatio"),
        })
        {
            var top = Control<RowDefinition>(window, upper).Height.Value;
            var bottom = Control<RowDefinition>(window, lower).Height.Value;
            var expected = settings.RootElement.GetProperty(property).GetDouble();
            Check(Math.Abs(bottom / (top + bottom) - expected) < 0.001,
                $"The saved {property} was not restored.");
        }
        Check(Control<Expander>(window, "AuthoringSearchExpander").IsExpanded,
            "The search panel's expanded state was not restored.");
        Console.WriteLine("PASS: workflow pane sizes and expanded states survive restarting");
    }

    private static void Resize(MainWindow window, string name, Key key)
    {
        var splitter = Control<GridSplitter>(window, name);
        splitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
        { RoutedEvent = Keyboard.KeyDownEvent });
        window.UpdateLayout();
    }
}
