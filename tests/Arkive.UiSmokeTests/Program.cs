using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arkive.App;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "Arkive.UiSmokeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var app = new App();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Arkive.App;component/Themes/Light.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Arkive.App;component/Themes/Base.xaml") });
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup += async (_, _) =>
        {
            try
            {
                await RunAsync(root, args.FirstOrDefault());
                Console.WriteLine("All Arkive authoring UI smoke tests passed.");
                app.Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                app.Shutdown(1);
            }
        };
        try { return app.Run(); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task RunAsync(string root, string? screenshotDirectory)
    {
        CreateLibraryFixture(root);
        var window = CreateWindow(root);
        window.Show();
        await UntilAsync(() => Control<DataGrid>(window, "LibraryGrid").Items.Count == 3);
        var tabs = Control<TabControl>(window, "MainTabControl");
        Check(tabs.Items.OfType<TabItem>().Where(tab => tab.Visibility == Visibility.Visible)
                .Select(tab => tab.Header.ToString()).SequenceEqual(["Asset library", "Create mod", "Edit mods", "Mods"]),
            "The main navigation should expose Asset library, Create mod, Edit mods, and Mods.");
        Check(tabs.SelectedItem == Control<TabItem>(window, "ModifiedUpksTab"), "Mods was not the initial destination.");
        Check(Control<Button>(window, "ModLibraryNewButton").IsVisible, "The initial mod library has no visible content.");
        Console.WriteLine("PASS: asset discovery, creation, editing, and deployment have separate main tabs");
        tabs.SelectedItem = Control<TabItem>(window, "LibraryTab");
        Check((Control<ComboBox>(window, "LibrarySearchScopeComboBox").SelectedItem as ComboBoxItem)?.Tag?.ToString() == "AllFields",
            "Filename search is not the default library scope.");
        await SearchLibraryAsync(window, "UI fixture boss");
        var results = Control<DataGrid>(window, "LibraryGrid");
        Check(results.Items.Count == 1, "The asset library cannot search by in-game name.");
        results.SelectedIndex = 0;
        Check(Control<Button>(window, "LibraryInspectButton").IsVisible &&
              Control<Button>(window, "LibraryCopyIdentityButton").IsEnabled,
            "UModel preview or asset information controls are no longer available.");
        await SearchLibraryAsync(window, "fixture_sound");
        Check(results.Items.Count == 1, "The asset library hides assets that cannot be modded.");
        await SearchLibraryAsync(window, "fixture-package.upk");
        Check(results.Items.Count == 3, "The asset library cannot search by package filename.");
        await SearchLibraryAsync(window, "fixture_alias.upk");
        Check(results.Items.Count == 3, "The asset library cannot search by decoded filename.");
        if (screenshotDirectory is not null) Capture(window, screenshotDirectory, "asset-library.png");
        Console.WriteLine("PASS: full asset library searches filenames, game names, and metadata-only assets with preview and information controls");
        var advanced = Control<Button>(window, "AdvancedSectionsButton").ContextMenu;
        var modTools = advanced.Items.OfType<MenuItem>().Single(item => item.Header.ToString() == "Mod tools");
        modTools.Items.OfType<MenuItem>().Single(item => item.Tag?.ToString() == "PackageProjects")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(Control<TabControl>(window, "ModifiedUpksInnerTabControl").SelectedItem ==
              Control<TabItem>(window, "GuidedPackageProjectsTab"), "Advanced package projects became inaccessible.");
        Control<TabControl>(window, "ModifiedUpksInnerTabControl").SelectedItem = Control<TabItem>(window, "ModLibraryTab");
        Check(Control<ProgressBar>(window, "ModLibraryProgressBar").Visibility == Visibility.Collapsed,
            "An idle progress bar is still shown in the main mod workflow.");
        Console.WriteLine("PASS: advanced package navigation remains reachable and idle progress is hidden");
        Control<Button>(window, "ModLibraryNewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(tabs.SelectedItem == Control<TabItem>(window, "CreateModTab"), "New mod did not open the creation tab.");
        Check(Control<ComboBox>(window, "AuthoringModComboBox").Items.Count == 0, "Opening Create mod created an unwanted draft.");
        Control<Button>(window, "CreateModButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Control<TextBlock>(window, "CreateModStatusText").Text.Contains("Enter a name", StringComparison.Ordinal),
            "An unnamed mod was not refused inline.");
        Console.WriteLine("PASS: creation requires a name and navigation alone does not create a draft");
        Control<TextBox>(window, "CreateModNameTextBox").Text = "UI test outfit";
        Control<TextBox>(window, "CreateModAuthorTextBox").Text = "UI test author";
        Control<TextBox>(window, "CreateModDescriptionTextBox").Text = "UI test description";
        if (screenshotDirectory is not null) Capture(window, screenshotDirectory, "create-mod.png");
        Control<Button>(window, "CreateModButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Control<Button>(window, "AuthoringSaveButton").IsEnabled,
            () => $"Draft creation did not finish: {Control<TextBlock>(window, "CreateModStatusText").Text}; " +
                  $"{Control<TextBlock>(window, "AuthoringStatusText").Text}; " +
                  $"create enabled={Control<Button>(window, "CreateModButton").IsEnabled}");
        Check(tabs.SelectedItem == Control<TabItem>(window, "WorkshopTab"), "A created mod did not open the editing tab.");
        Check(Control<TextBox>(window, "AuthoringNameTextBox").Text == "UI test outfit" &&
              Control<TextBox>(window, "AuthoringAuthorTextBox").Text == "UI test author" &&
              Control<TextBox>(window, "AuthoringDescriptionTextBox").Text == "UI test description",
            "The editing tab did not retain creation details.");
        Check(Control<Button>(window, "AuthoringAddAssetsButton").IsEnabled, "A named draft cannot add assets.");
        Check(!Control<Button>(window, "AuthoringShareButton").IsEnabled, "An unsaved draft can be shared.");
        Control<Button>(window, "AuthoringNewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(tabs.SelectedItem == Control<TabItem>(window, "CreateModTab") &&
              Control<ComboBox>(window, "AuthoringModComboBox").Items.Count == 1,
            "Opening creation again replaced or duplicated the existing draft.");
        tabs.SelectedItem = Control<TabItem>(window, "WorkshopTab");
        Console.WriteLine("PASS: named creation hands off to editing without publishing, installing, or replacing an existing draft");
        WorkflowLayoutChecks.Exercise(window);
        await AuthoringInteractionChecks.VerifyAsync(window, screenshotDirectory);
        await MultiSelectionChecks.VerifyAsync(window, screenshotDirectory);
        FileAssociationChecks.Verify(window, screenshotDirectory);
        Control<Button>(window, "AuthoringSaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Control<TextBlock>(window, "AuthoringStatusText").Text.Contains("at least one asset", StringComparison.OrdinalIgnoreCase));
        await UntilAsync(() => Control<Button>(window, "AuthoringSaveButton").IsEnabled);
        Check(!Directory.Exists(Path.Combine(root, "mods")) || !Directory.EnumerateFiles(Path.Combine(root, "mods"), "*.arkive").Any(),
            "An empty draft became a portable mod.");
        Console.WriteLine("PASS: incomplete authoring produces an inline actionable message");
        if (screenshotDirectory is not null) Capture(window, screenshotDirectory, "authoring.png");
        window.Close();
        await UntilAsync(() => !window.IsVisible, () => "The initial test window did not finish closing.");
        window = CreateWindow(root);
        window.Show();
        await UntilAsync(() => Control<ComboBox>(window, "AuthoringModComboBox") is { Items.Count: > 0, IsEnabled: true },
            () => "The restored draft did not appear after reopening.");
        Control<ComboBox>(window, "AuthoringModComboBox").SelectedIndex = 0;
        await UntilAsync(() => Control<TextBox>(window, "AuthoringNameTextBox").Text == "UI test outfit" &&
            Control<Button>(window, "AuthoringSaveButton").IsEnabled,
            () => $"The reopened draft did not load: name={Control<TextBox>(window, "AuthoringNameTextBox").Text}, " +
                  $"status={Control<TextBlock>(window, "AuthoringStatusText").Text}, save={Control<Button>(window, "AuthoringSaveButton").IsEnabled}.");
        Check(Control<TextBox>(window, "AuthoringAuthorTextBox").Text == "UI test author", "Draft details were lost on reopening.");
        Check((Control<ComboBox>(window, "LibrarySearchScopeComboBox").SelectedItem as ComboBoxItem)?.Tag?.ToString() == "AllFields",
            "The all-fields search scope was not restored.");
        Console.WriteLine("PASS: reopening restores authoring identity and metadata");
        WorkflowLayoutChecks.VerifyRestored(window, root);
        if (screenshotDirectory is not null)
        {
            tabs = Control<TabControl>(window, "MainTabControl");
            tabs.SelectedItem = Control<TabItem>(window, "ModifiedUpksTab");
            window.UpdateLayout();
            Capture(window, screenshotDirectory, "mods.png");
        }
        window.Close();
        await UntilAsync(() => !window.IsVisible);
    }

    private static void CreateLibraryFixture(string root)
    {
        var packages = Path.Combine(root, "Packages");
        var cache = Path.Combine(root, "cache", "library-index");
        Directory.CreateDirectory(packages);
        Directory.CreateDirectory(cache);
        var packagePath = Path.Combine(packages, "fixture-package.upk");
        File.WriteAllBytes(packagePath, []);
        var package = new PackageIndexDocument(packagePath, "fixture-package", "fixture_alias", 0,
            File.GetLastWriteTimeUtc(packagePath).Ticks, DateTimeOffset.UtcNow,
            [
                new IndexedAsset(1, AssetKind.SkeletalMesh, "SkeletalMesh", "fixture_boss_sk", 42, 12),
                new IndexedAsset(2, AssetKind.Texture, "Texture2D", "fixture_cloth_d", 20, 54),
                new IndexedAsset(3, AssetKind.Object, "SoundCue", "fixture_sound", 10, 74),
            ], null);
        File.WriteAllText(Path.Combine(cache, "fixture.json"), JsonSerializer.Serialize(package));
        var metadataPath = Path.Combine(root, "metadata.json");
        var metadata = new GameAssetMetadataDocument(1, "English", root, DateTimeOffset.UtcNow,
            [new GameAssetAssociation("UI fixture boss", "tip.name.fixture.boss", "Npc", 1, "Model",
                "fixture_alias.fixture_boss_sk", "fixture_alias", "fixture_boss_sk")]);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata));
        File.WriteAllText(Path.Combine(root, "cache", "arkive-settings.json"), JsonSerializer.Serialize(new
        {
            LibraryRootPath = packages,
            GameMetadataPath = metadataPath,
            LibrarySearchText = "fixture-package.upk",
        }));
    }

    private static async Task SearchLibraryAsync(MainWindow window, string query)
    {
        Control<TextBox>(window, "LibrarySearchTextBox").Text = query;
        Control<Button>(window, "LibrarySearchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !Control<TextBlock>(window, "LibraryCountText").Text.StartsWith("Searching", StringComparison.Ordinal));
    }

    private static MainWindow CreateWindow(string root) => new(root)
    {
        Width = 1440,
        Height = 900,
        Left = -5000,
        Top = -5000,
        WindowStartupLocation = WindowStartupLocation.Manual,
    };

    internal static T Control<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing UI control: {name}");

    internal static async Task UntilAsync(Func<bool> condition, Func<string>? failureMessage = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(failureMessage?.Invoke() ?? "The UI operation did not complete.");
            await Task.Delay(25);
        }
    }

    internal static void Capture(Window window, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    internal static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
