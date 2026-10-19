using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Arkive.App;
using Arkive.Core.Materials;
using Arkive.Core.Mods;
using static Program;

/// <summary>A mod pack shows as a heading with one switch, and enabled mods changing the same asset raise a warning.</summary>
internal static class ModPackChecks
{
    public static async Task VerifyAsync(MainWindow window, string root, string? screenshotDirectory)
    {
        var picker = new ModPackWindow(false, ModPackWindow.SuggestedName("UI pack - effects"),
        [
            new ModPackWindow.Choice("a", "UI pack - effects", string.Empty, true),
            new ModPackWindow.Choice("b", "UI pack - sky", string.Empty, ModPackWindow.BelongsWith("UI pack", "UI pack - sky")),
            new ModPackWindow.Choice("c", "UI packaging", string.Empty, ModPackWindow.BelongsWith("UI pack", "UI packaging")),
        ]);
        Check(picker.PackName == "UI pack" && picker.ModIds.SequenceEqual(["a", "b"]) && picker.Problem is null,
            $"New pack did not suggest the shared name and its mods: name={picker.PackName}, mods={string.Join(",", picker.ModIds)}.");
        if (screenshotDirectory is not null)
        {
            picker.WindowStartupLocation = WindowStartupLocation.Manual;
            picker.Left = picker.Top = -20000;
            picker.ShowActivated = false;
            picker.Show();
            Capture(picker, screenshotDirectory, "mod-pack-window.png");
        }
        picker.Close();

        var mods = Path.Combine(root, "mods");
        WriteMod(Path.Combine(mods, "UI pack - effects.arkive"), "UI pack - effects");
        WriteMod(Path.Combine(mods, "UI pack - sky.arkive"), "UI pack - sky");
        var grid = Control<DataGrid>(window, "ModLibraryGrid");
        var status = Control<TextBlock>(window, "ModLibraryStatusText");
        var banner = Control<Border>(window, "ModOverlapBanner");
        Invoke(window, "ModLibraryRefreshButton_Click", null, new RoutedEventArgs());
        await UntilAsync(() => Rows(grid).Count(row => Name(row).StartsWith("UI pack", StringComparison.Ordinal)) == 2,
            () => "The pack's mods did not appear in the Mods list.");
        Check(Control<Button>(window, "ModLibraryNewPackButton").IsEnabled, "New pack… is greyed out with three mods in the list.");

        var store = (ArkiveModPackStore)typeof(MainWindow).GetProperty("ModPacks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var packIds = Rows(grid).Where(row => Name(row).StartsWith("UI pack", StringComparison.Ordinal)).Select(Id).ToArray();
        store.Save(null, "UI pack", packIds);
        Invoke(window, "ModLibraryRefreshButton_Click", null, new RoutedEventArgs());
        await UntilAsync(() => Header(grid) is { IsVisible: true }, () => "The pack heading did not appear in the Mods list.");
        var header = Header(grid)!;
        var view = (ListCollectionView)grid.ItemsSource;
        Check(Rows(grid).Select(Name).Take(2).Order().SequenceEqual(["UI pack - effects", "UI pack - sky"]) ||
              Rows(grid).Select(Name).Skip(1).Take(2).Order().SequenceEqual(["UI pack - effects", "UI pack - sky"]),
            "The pack's mods are not listed together: " + string.Join(", ", Rows(grid).Select(Name)));
        Check(Texts(header).Contains("UI pack") && Texts(header).Contains("Pack of 2 mods · all off") && view.Groups.Count == 2,
            "The pack heading does not show the pack's name and state: " + string.Join(" | ", Texts(header)));

        var toggle = FindVisual<CheckBox>(header)!;
        toggle.IsChecked = true;
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await UntilAsync(() => Rows(grid).Where(row => packIds.Contains(Id(row))).All(Enabled) && banner.IsVisible &&
                               Header(grid) is { } refreshed && Texts(refreshed).Contains("Pack of 2 mods · all on"),
            () => $"Turning the pack on did not enable both mods and warn about their shared asset: status={status.Text}, banner={banner.IsVisible}.");
        var bar = Control<TextBlock>(window, "StatusText");
        Check(bar.Text.StartsWith("Turned on the pack UI pack (2 mods).", StringComparison.Ordinal),
            "Turning the pack on did not say so: " + bar.Text);
        var overlap = Control<ItemsControl>(window, "ModOverlapList").Items.Cast<object>().Single();
        Check(Text(overlap).Contains("same priority", StringComparison.Ordinal) && !Control<Button>(window, "ModOverlapHideButton").IsVisible,
            "Two mods tied on the same asset did not get a warning that cannot be hidden: " + Text(overlap));
        await UntilAsync(() => Control<Panel>(window, "BusyOverlay").Visibility != Visibility.Visible);
        if (screenshotDirectory is not null) Capture(window, screenshotDirectory, "mod-pack.png");
        Console.WriteLine("PASS: a mod pack is one heading with one switch that turns all its mods on");

        var makeWin = FindVisual<Button>(Control<ItemsControl>(window, "ModOverlapList"),
            button => button.Content as string == $"Make {OtherName(overlap)} win")!;
        makeWin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await UntilAsync(() => Control<ItemsControl>(window, "ModOverlapList").Items.Cast<object>().SingleOrDefault() is { } line &&
                               Text(line).Contains("wins them because its priority is higher", StringComparison.Ordinal) &&
                               Control<Button>(window, "ModOverlapHideButton").IsVisible,
            () => "Making one mod win did not turn the tie into a named winner.");
        await UntilAsync(() => Control<Panel>(window, "BusyOverlay").Visibility != Visibility.Visible);
        Control<Button>(window, "ModOverlapHideButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(!banner.IsVisible && store.Load().HiddenOverlapWarning is { Length: > 0 },
            "Hide did not hide the overlap warning and remember it.");
        Invoke(window, "ModLibraryRefreshButton_Click", null, new RoutedEventArgs());
        await UntilAsync(() => Header(grid) is not null);
        Check(!banner.IsVisible, "A hidden overlap warning came back without any change.");
        Console.WriteLine("PASS: the overlap warning names the winner, makes the other mod win in one click, and stays hidden until something changes");

        var fold = FindVisual<ToggleButton>(Header(grid)!, button => button is not CheckBox)!;
        fold.IsChecked = false;
        fold.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        window.UpdateLayout();
        Check(store.Load().Packs.Single().Collapsed &&
              grid.ItemContainerGenerator.ContainerFromItem(Rows(grid).First(row => packIds.Contains(Id(row)))) is not FrameworkElement { IsVisible: true },
            "Folding the pack did not hide its mods and remember it.");
        fold.IsChecked = true;
        fold.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        grid.SelectedItem = Rows(grid).First(row => packIds.Contains(Id(row)));
        var rowMenu = grid.ContextMenu!;
        rowMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, rowMenu));
        var addTo = rowMenu.Items.OfType<MenuItem>().Single(item => item.Name == "ModLibraryRowAddToPackItem");
        var removeFrom = rowMenu.Items.OfType<MenuItem>().Single(item => item.Name == "ModLibraryRowRemoveFromPackItem");
        Check(addTo.Items.OfType<MenuItem>().Any(item => item.Header as string == "New pack…") &&
              removeFrom.Visibility == Visibility.Visible && removeFrom.Header as string == "Remove from UI pack",
            "Right-clicking a pack's mod does not offer the pack actions.");

        grid.SelectedItem = Rows(grid).First(row => !packIds.Contains(Id(row)));
        Invoke(window, "ModLibraryEnableButton_Click", null, new RoutedEventArgs());
        await UntilAsync(() => Header(grid) is { } heading && Texts(heading).Any(text => text.StartsWith("⚠ Changes the same assets as", StringComparison.Ordinal)) &&
                               banner.IsVisible,
            () => "Enabling a mod outside the pack that changes the same asset did not mark the pack and show the warning again.");
        Check(Control<ItemsControl>(window, "ModOverlapList").Items.Cast<object>().Single() is var merged &&
              Text(merged).StartsWith("UI renamed mod and the UI pack pack both change 1 asset.", StringComparison.Ordinal) &&
              OtherName(merged) == "the UI pack pack",
            "The pack's mods overlapping one outside mod were not summed into one line: " +
            string.Join(" | ", Control<ItemsControl>(window, "ModOverlapList").Items.Cast<object>().Select(Text)));
        await UntilAsync(() => Control<Panel>(window, "BusyOverlay").Visibility != Visibility.Visible);
        if (screenshotDirectory is not null) Capture(window, screenshotDirectory, "mod-pack-overlap.png");
        var packHeader = (Border)Header(grid)!;
        packHeader.ContextMenu!.PlacementTarget = packHeader; // as opening it by right-click does
        packHeader.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, packHeader.ContextMenu));
        Check(packHeader.ContextMenu.Items.OfType<MenuItem>().First().Header as string == "Turn pack off",
            "The pack's right-click menu does not offer to turn the pack off while it is on.");
        var remove = packHeader.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Remove pack (keeps its mods)");
        remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(() => Header(grid) is null && Rows(grid).Count(row => packIds.Contains(Id(row))) == 2,
            () => $"Removing the pack did not leave its mods in the list without a heading: context={remove.DataContext?.GetType().Name}, status={status.Text}.");
        Check(store.Load().Packs.Count == 0, "Removing the pack did not remove it from the packs file.");
        Console.WriteLine("PASS: packs fold, offer their actions on right-click, and removing a pack keeps its mods");
    }

    private static IEnumerable<object> Rows(DataGrid grid) => grid.Items.Cast<object>();

    private static string Name(object row) => (string)row.GetType().GetProperty("DisplayName")!.GetValue(row)!;

    private static string Id(object row) => ((ArkiveModLibraryEntry)row.GetType().GetProperty("Entry")!.GetValue(row)!).ModId;

    private static bool Enabled(object row) => (bool)row.GetType().GetProperty("Enabled")!.GetValue(row)!;

    private static string Text(object overlapRow) => (string)overlapRow.GetType().GetProperty("Text")!.GetValue(overlapRow)!;

    private static string OtherName(object overlapRow)
    {
        var other = overlapRow.GetType().GetProperty("Other")!.GetValue(overlapRow)!;
        return (string)other.GetType().GetProperty("Label")!.GetValue(other)!;
    }

    /// <summary>The visible pack heading, or null when the list has none.</summary>
    private static FrameworkElement? Header(DataGrid grid)
    {
        grid.UpdateLayout();
        return FindVisual<GroupItem>(grid, item => item.Content is CollectionViewGroup { Name: { } name } &&
                                                   name.GetType().GetProperty("IsPack")!.GetValue(name) is true) is { } group
            ? FindVisual<Border>(group, border => border.Name == "PackHeader" && border.IsVisible)
            : null;
    }

    private static string[] Texts(DependencyObject parent)
    {
        var texts = new List<string>();
        void Walk(DependencyObject node)
        {
            if (node is TextBlock { Text.Length: > 0 } text) texts.Add(text.Text);
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) Walk(VisualTreeHelper.GetChild(node, index));
        }
        Walk(parent);
        return [.. texts];
    }

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

    private static void WriteMod(string path, string name)
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
            new ArkiveModMetadata(name, "1.0.0"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        ArkiveModFileService.WriteAsync(path, manifest, [new ArkiveModPackAsset(material, [])]).GetAwaiter().GetResult();
    }
}
