using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Arkive.App;
using Arkive.Core.Materials;
using Arkive.Core.Mods;



/// <summary>Priority and rename straight from the Mods list update the list and its buttons at once.</summary>
internal static class ModLibraryRowChecks
{
    public static async Task VerifyAsync(MainWindow window, string root)
    {
        WriteMod(Path.Combine(root, "mods", "UI row mod.arkive"));
        var tabs = Program.Control<TabControl>(window, "MainTabControl");
        tabs.SelectedItem = Program.Control<TabItem>(window, "ModifiedUpksTab");
        Program.Control<TabControl>(window, "ModifiedUpksInnerTabControl").SelectedItem =
            Program.Control<TabItem>(window, "ModLibraryTab");
        var grid = Program.Control<DataGrid>(window, "ModLibraryGrid");
        var apply = Program.Control<Button>(window, "ModLibraryApplyButton");
        Invoke(window, "ModLibraryRefreshButton_Click", null, new RoutedEventArgs());
        await Program.UntilAsync(() => Row(grid) is not null, () => "The added mod did not appear in the Mods list.");
        grid.SelectedItem = Row(grid);
        window.UpdateLayout();

        var menu = grid.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray();
        Program.Check(menu.Contains("Increase priority") && menu.Contains("Decrease priority") && menu.Contains("Rename") &&
                      (menu.Contains("Disable") || menu.Contains("Enable")),
            "Right-clicking a mod does not offer enable/disable, priority and rename: " + string.Join(", ", menu));

        var box = FindVisual<TextBox>(grid.ItemContainerGenerator.ContainerFromItem(grid.SelectedItem)!)
                  ?? throw new InvalidOperationException("The priority box is missing from the mod row.");
        box.Text = "7";
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, Key.Enter)
            { RoutedEvent = UIElement.PreviewKeyDownEvent });
        await Program.UntilAsync(() => Priority(grid) == 7 && apply.IsEnabled,
            () => $"Typing a priority did not update the list and its buttons: priority={Priority(grid)}, apply={apply.IsEnabled}.");
        Console.WriteLine("PASS: a priority typed into the Mods list applies at once and keeps Apply changes available");

        grid.SelectedItem = Row(grid);
        Invoke(window, "ModLibraryRowRenameItem_Click", null, new RoutedEventArgs());
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        var nameColumn = grid.Columns.First(column => column.Header?.ToString() == "Mod");
        var editor = (nameColumn.GetCellContent(grid.SelectedItem) is { } content ? FindVisual<TextBox>(content) : null)
                     ?? throw new InvalidOperationException(
                         $"Rename did not open the mod name for editing: editing={grid.IsKeyboardFocusWithin}, " +
                         $"content={nameColumn.GetCellContent(grid.SelectedItem)?.GetType().Name}.");
        editor.Text = "UI renamed mod";
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        await Program.UntilAsync(() => Row(grid)?.GetType().GetProperty("DisplayName")!.GetValue(Row(grid)) as string == "UI renamed mod" &&
                                       apply.IsEnabled && Priority(grid) == 7,
            () => $"Renaming in the Mods list did not update the list: name={Row(grid)?.GetType().GetProperty("DisplayName")!.GetValue(Row(grid))}, " +
                  $"apply={apply.IsEnabled}, status={Program.Control<TextBlock>(window, "ModLibraryStatusText").Text}.");
        Console.WriteLine("PASS: renaming a mod in the Mods list saves it and refreshes the list at once");
    }

    private static object? Row(DataGrid grid) => grid.Items.Cast<object>().FirstOrDefault();

    private static int? Priority(DataGrid grid) => Row(grid)?.GetType().GetProperty("Priority")!.GetValue(Row(grid)) as int?;

    private static void Invoke(MainWindow window, string method, object? sender, RoutedEventArgs e) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [sender, e]);

    private static T? FindVisual<T>(DependencyObject parent, Func<T, bool>? match = null) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found && (match is null || match(found))) return found;
            if (FindVisual(child, match) is { } nested) return nested;
        }
        return null;
    }

    private static void WriteMod(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var material = new ArkiveModFileAsset(
            Guid.NewGuid().ToString("N"), UnifiedPackageAssetKind.MaterialInstanceConstant, "UI material",
            new ArkiveModAssetTarget("fixture_alias", "fixture-package.upk", "MaterialInstanceConstant", "fixture_mi", 4,
                new string('A', 64), new string('B', 64)), [])
        {
            Material = new ArkiveModMaterialData([new MaterialScalarEdit("Roughness", 0.5f, 0.8f)], [], [], null),
        };
        var manifest = ArkiveModAuthoringService.CreateManifest(Guid.NewGuid().ToString("N"),
            new ArkiveModMetadata("UI row mod", "1.0.0"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        ArkiveModFileService.WriteAsync(path, manifest, [new ArkiveModPackAsset(material, [])]).GetAwaiter().GetResult();
    }
}
