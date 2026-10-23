using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arkive.App;
using Arkive.Core.Mods;
using static Program;

internal static class MultiSelectionChecks
{
    internal static async Task VerifyAsync(MainWindow window, string? screenshotDirectory)
    {
        foreach (var name in new[] { "AuthoringAssetsGrid", "WorkshopSearchResultsGrid", "ModLibraryAssetsGrid" })
            Check(Control<DataGrid>(window, name) is { SelectionMode: DataGridSelectionMode.Extended, SelectionUnit: DataGridSelectionUnit.FullRow },
                $"{name} does not use Windows-style full-row multi-selection.");
        var results = Control<DataGrid>(window, "WorkshopSearchResultsGrid");
        var previousResults = results.ItemsSource;
        results.ItemsSource = Control<DataGrid>(window, "LibraryGrid").ItemsSource;
        results.SelectAll();
        Check(results.SelectedItems.Count == 3 && Control<Button>(window, "WorkshopBeginButton").IsEnabled &&
              !Control<Button>(window, "WorkshopExtractAssetKitButton").IsEnabled &&
              !Control<Button>(window, "WorkshopPreviewButton").IsEnabled,
            "Bulk add selection permits a mixed-kind complete kit or an ambiguous preview.");
        results.ItemsSource = previousResults;
        Console.WriteLine("PASS: working, saved, and search asset lists support extended selection with safe bulk-add actions");

        var draftField = typeof(MainWindow).GetField("_authoringDraft", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (ArkiveModAuthoringDraft)draftField.GetValue(window)!;
        var workspace = (ArkiveModAuthoringWorkspace)typeof(MainWindow)
            .GetField("_modAuthoringWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var draft = await workspace.SaveAsync(original with
        {
            EditIds = ["included-one", "included-two"], ReferenceEditIds = ["reference"],
            SavedBindings = new Dictionary<string, string> { ["saved-asset"] = "included-one" },
        });
        draftField.SetValue(window, draft);
        var rowType = typeof(MainWindow).GetNestedType("AuthoringAssetRow", BindingFlags.NonPublic)!;
        var rows = Array.CreateInstance(rowType, 3);
        rows.SetValue(Row("included-one", "saved-asset", true), 0);
        rows.SetValue(Row("included-two", null, true), 1);
        rows.SetValue(Row("reference", null, false), 2);
        var grid = Control<DataGrid>(window, "AuthoringAssetsGrid");
        grid.ItemsSource = rows;
        grid.SelectedItems.Add(rows.GetValue(0)!);
        grid.SelectedItems.Add(rows.GetValue(2)!);
        Check(Control<Button>(window, "AuthoringRemoveButton").IsEnabled &&
              !Control<Button>(window, "AuthoringReplaceButton").IsEnabled &&
              !Control<Button>(window, "AuthoringOpenButton").IsEnabled,
            "Multiple selection disables removal or leaves single-file actions enabled.");
        if (screenshotDirectory is not null)
        {
            var search = Control<Expander>(window, "AuthoringSearchExpander");
            var expanded = search.IsExpanded;
            search.IsExpanded = false;
            Capture(window, screenshotDirectory, "multi-selection.png");
            search.IsExpanded = expanded;
        }
        Control<Button>(window, "AuthoringRemoveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Control<Button>(window, "AuthoringSaveButton").IsEnabled && grid.Items.Count == 1);
        draft = (ArkiveModAuthoringDraft)draftField.GetValue(window)!;
        Check(draft.EditIds.SequenceEqual(["included-two"]) && draft.ReferenceEditIds.Count == 0 &&
              draft.RemovedAssetIds.Contains("saved-asset"), "Remove selected did not remove exactly the selected rows.");
        Console.WriteLine("PASS: Remove selected stages every chosen asset and preserves the unselected working row");
        grid.Focus();
        ApplicationCommands.SelectAll.Execute(null, grid);
        Check(grid.SelectedItems.Count == grid.Items.Count, "The native Select All command did not select all asset rows.");
        Control<Button>(window, "AuthoringRemoveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Control<Button>(window, "AuthoringSaveButton").IsEnabled && grid.Items.Count == 0);
        draft = (ArkiveModAuthoringDraft)draftField.GetValue(window)!;
        Check(draft.EditIds.Count == 0 && draft.ReferenceEditIds.Count == 0 &&
              Control<TextBlock>(window, "AuthoringStatusText").Text.Contains("Add new assets before saving", StringComparison.Ordinal),
            "Removing all assets leaves stale membership or unclear empty-draft guidance.");
        Control<Button>(window, "AuthoringRemoveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!Control<Button>(window, "AuthoringRemoveButton").IsEnabled, "An empty selection still permits removal.");
        draft = await workspace.SaveAsync(original);
        draftField.SetValue(window, draft);
        await (Task)typeof(MainWindow).GetMethod("LoadAuthoringAssetsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null])!;
        Console.WriteLine("PASS: native Select All can clear a mod draft, including missing working copies, without deleting saved content");

        object Row(string id, string? assetId, bool include) => Activator.CreateInstance(rowType,
            [id, "Texture2D", "fixture", id, "Draft", include, null, assetId, id])!;
    }
}
