using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arkive.App;
using Arkive.Core.External;
using static Program;

internal static class AuthoringInteractionChecks
{
    internal static async Task VerifyAsync(MainWindow window, string? screenshotDirectory)
    {
        Check(Control<Grid>(window, "WorkshopSuggestionsPanel").Visibility == Visibility.Collapsed,
            "The empty suggested-name dropdown is still shown.");
        Check(Control<Button>(window, "WorkshopBeginButton").Content.ToString() == "Add asset only" &&
              Control<Button>(window, "WorkshopExtractAssetKitButton").ToolTip.ToString()!.Contains("excluded", StringComparison.Ordinal),
            "The two add routes do not explain working copies and optional references.");
        foreach (var name in new[] { "AuthoringAssetsGrid", "WorkshopSearchResultsGrid" })
            Check(Control<DataGrid>(window, name).HeadersVisibility == DataGridHeadersVisibility.Column,
                "A native row-header gutter remains on an authoring table.");
        Console.WriteLine("PASS: empty suggestions are hidden, add routes are explained, and white row-header gutters are removed");

        var button = Control<Button>(window, "PackageActionsButton");
        var menu = button.ContextMenu!;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
        await UntilAsync(() => menu.ActualWidth > 0);
        var expected = (SolidColorBrush)window.FindResource("SurfaceRaisedBrush");
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.ApplyTemplate();
            Check(item.Template.FindName("MenuSurface", item) is Border border &&
                  border.Background is SolidColorBrush background && background.Color == expected.Color,
                "A menu item still uses a native unthemed gutter.");
        }
        if (screenshotDirectory is not null) CaptureMenu(menu, screenshotDirectory, expected.Color);
        menu.IsOpen = false;
        var advanced = Control<Button>(window, "AdvancedSectionsButton");
        advanced.ContextMenu!.PlacementTarget = advanced;
        advanced.ContextMenu.IsOpen = true;
        await UntilAsync(() => advanced.ContextMenu.ActualWidth > 0);
        var submenu = advanced.ContextMenu.Items.OfType<MenuItem>().First(item => item.HasItems);
        submenu.ApplyTemplate();
        submenu.IsSubmenuOpen = true;
        Check(submenu.Template.FindName("PART_Popup", submenu) is Popup { IsOpen: true },
            "The themed menu template broke submenus.");
        submenu.IsSubmenuOpen = false;
        advanced.ContextMenu.IsOpen = false;
        Console.WriteLine("PASS: context menus use themed surfaces and retain working submenus");

        var backendField = typeof(MainWindow).GetField("_umodelBridge", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var backend = backendField.GetValue(window);
        var results = Control<DataGrid>(window, "WorkshopSearchResultsGrid");
        var previousItems = results.ItemsSource;
        backendField.SetValue(window, new UmodelBridge(Environment.ProcessPath!));
        results.ItemsSource = Control<DataGrid>(window, "LibraryGrid").ItemsSource;
        results.SelectedIndex = 0;
        try
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<CancellationToken, Task> operation = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            };
            var run = typeof(MainWindow).GetMethod("RunAuthoringExtractionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = (Task)run.Invoke(window, [operation])!;
            await entered.Task;
            Check(Control<Border>(window, "AuthoringOperationPanel").Visibility == Visibility.Visible &&
                  Control<ProgressBar>(window, "AuthoringOperationProgress").IsIndeterminate,
                "Extraction has no visible stage/progress indicator.");
            Check(Control<Button>(window, "WorkshopPreviewButton").IsEnabled &&
                  !Control<Button>(window, "AuthoringSaveButton").IsEnabled && results.IsEnabled,
                "Extraction blocks read-only preview or allows unsafe draft mutation.");
            var assets = Control<DataGrid>(window, "AuthoringAssetsGrid");
            assets.ItemsSource = new[] { new { Included = true, Name = "UI reference", Kind = "Texture2D", Package = "fixture", State = "Draft", WorkingFile = "" } };
            window.UpdateLayout();
            Check(FindVisual<CheckBox>(assets) is { IsEnabled: false },
                "A selectable working-asset table permits inclusion changes during extraction.");
            typeof(MainWindow).GetMethod("ReportAuthoringExtraction", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, ["Preparing referenced textures", (int?)1, (int?)3]);
            Check(Control<ProgressBar>(window, "AuthoringOperationProgress") is { IsIndeterminate: false, Value: 1, Maximum: 3 },
                "Reference preparation does not report real dependency counts.");
            await UntilAsync(() => !Control<TextBlock>(window, "AuthoringOperationText").Text.EndsWith("00:00", StringComparison.Ordinal),
                () => "Extraction elapsed time is not updating.");
            if (screenshotDirectory is not null) Capture(window, screenshotDirectory, "extraction-progress.png");
            Control<Button>(window, "AuthoringCancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending;
            Check(Control<Border>(window, "AuthoringOperationPanel").Visibility == Visibility.Collapsed &&
                  Control<TextBlock>(window, "AuthoringStatusText").Text.StartsWith("Cancelled", StringComparison.Ordinal) &&
                  Control<Button>(window, "AuthoringSaveButton").IsEnabled,
                "Cancelling extraction leaves the workspace blocked.");
            Console.WriteLine("PASS: extraction shows live progress, allows source previews, cancels safely, and releases the workspace");
        }
        finally
        {
            backendField.SetValue(window, backend);
            results.ItemsSource = previousItems;
        }
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }

    private static void CaptureMenu(ContextMenu menu, string directory, Color background)
    {
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(menu);
        if (background.R + background.G + background.B < 400)
        {
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var white = 0;
            for (var y = 0; y < bitmap.PixelHeight; y++)
                for (var x = 0; x < Math.Min(20, bitmap.PixelWidth); x++)
                {
                    var offset = (y * bitmap.PixelWidth + x) * 4;
                    if (pixels[offset] > 240 && pixels[offset + 1] > 240 && pixels[offset + 2] > 240) white++;
                }
            Check(white == 0, "White native menu-gutter pixels remain in the dark menu.");
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, "more-menu.png"));
        encoder.Save(stream);
    }
}
