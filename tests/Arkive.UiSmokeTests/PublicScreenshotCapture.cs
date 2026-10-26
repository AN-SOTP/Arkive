using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Arkive.App;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Languages;
using Arkive.Core.Mods;
using Arkive.Core.Textures;
using static Program;

internal static class PublicScreenshotCapture
{
    internal static async Task RunAsync(string root, string destination)
    {
        var packages = Path.Combine(root, "Packages");
        var cache = Path.Combine(root, "cache", "library-index");
        Directory.CreateDirectory(packages);
        Directory.CreateDirectory(cache);
        var packagePath = Path.Combine(packages, "DEMO_CHARACTER.upk");
        File.WriteAllBytes(packagePath, []);
        var assets = new[]
        {
            new IndexedAsset(1, AssetKind.SkeletalMesh, "SkeletalMesh", "demo_guardian_sk", 2457600, 12),
            new IndexedAsset(2, AssetKind.Texture, "Texture2D", "demo_armour_d", 1048576, 54),
            new IndexedAsset(3, AssetKind.Texture, "Texture2D", "demo_armour_e", 1048576, 74),
            new IndexedAsset(4, AssetKind.Texture, "Texture2D", "demo_armour_n", 2097152, 94),
            new IndexedAsset(5, AssetKind.Material, "MaterialInstanceConstant", "demo_armour_mi", 4096, 114),
            new IndexedAsset(6, AssetKind.StaticMesh, "StaticMesh", "demo_weapon_sk", 491520, 134),
        };
        var document = new PackageIndexDocument(packagePath, "DEMO_CHARACTER", "demo_guardian", 0,
            File.GetLastWriteTimeUtc(packagePath).Ticks, DateTimeOffset.UtcNow, assets, null);
        File.WriteAllText(Path.Combine(cache, "demo.json"), JsonSerializer.Serialize(document));
        var metadataPath = Path.Combine(root, "metadata.json");
        var metadata = new GameAssetMetadataDocument(2, "English", "Example client", DateTimeOffset.UtcNow,
            assets.Select(asset => new GameAssetAssociation("Guardian armour (demo)", "demo.guardian", "Npc", 1,
                "Model", "demo_guardian." + asset.ObjectName, "demo_guardian", asset.ObjectName)).ToArray());
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata));
        File.WriteAllText(Path.Combine(root, "cache", "arkive-settings.json"), JsonSerializer.Serialize(new
        {
            Version = 8,
            LibraryRootPath = packages,
            GameMetadataPath = metadataPath,
            LibrarySearchText = "Guardian",
            LibrarySearchScope = "AllFields",
            Theme = "Dark",
            BannerArtwork = "Thaemine",
            CheckForUpdatesOnStartup = false,
            WindowWidth = 1600,
            WindowHeight = 1000,
        }));
        var window = new MainWindow(root)
        { Width = 1600, Height = 1000, Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        window.Show();
        await UntilAsync(() => Control<DataGrid>(window, "LibraryGrid").Items.Count == assets.Length);
        var tabs = Control<TabControl>(window, "MainTabControl");
        tabs.SelectedItem = Control<TabItem>(window, "LibraryTab");
        Control<TextBox>(window, "LibraryRootTextBox").Text = @"E:\Games\Lost Ark\EFGame\ReleasePC\Packages";
        DemoStatus(window);
        ResizeColumns(window, "LibraryGrid", [190, 170, 95, 180, 210, 220, 115, 190, 85]);
        await CaptureAsync(window, destination, "asset-library.png");

        tabs.SelectedItem = Control<TabItem>(window, "CreateModTab");
        Control<TextBox>(window, "CreateModNameTextBox").Text = "Ember armour · example";
        Control<TextBox>(window, "CreateModAuthorTextBox").Text = "Arkive examples";
        Control<Button>(window, "CreateModButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Control<Button>(window, "AuthoringSaveButton").IsEnabled);
        var rowType = typeof(MainWindow).GetNestedType("AuthoringAssetRow", BindingFlags.NonPublic)!;
        var workingRows = Array.CreateInstance(rowType, 3);
        var names = new[] { "Guardian mesh", "Armour base colour", "Armour emissive" };
        for (var index = 0; index < workingRows.Length; index++)
            workingRows.SetValue(Activator.CreateInstance(rowType,
                [names[index], index == 0 ? "SkeletalMesh" : "Texture2D", "demo_guardian", assets[index].ObjectName,
                    "Draft", true, null, "demo-" + index, "working-" + index]), index);
        Control<DataGrid>(window, "AuthoringAssetsGrid").ItemsSource = workingRows;
        Control<TextBlock>(window, "AuthoringSummaryText").Text = "3 asset(s) in the mod · 1 package(s)";
        Control<TextBlock>(window, "AuthoringStatusText").Text = "Fictional demo: edit working copies, save a .arkive file, then preview before applying.";
        Control<Expander>(window, "AuthoringSearchExpander").IsExpanded = true;
        Control<DataGrid>(window, "WorkshopSearchResultsGrid").ItemsSource = Control<DataGrid>(window, "LibraryGrid").ItemsSource;
        Control<TextBox>(window, "WorkshopSearchTextBox").Text = "Guardian";
        DemoStatus(window);
        ResizeColumns(window, "AuthoringAssetsGrid", [70, 310, 150, 190, 160, 550]);
        ResizeColumns(window, "WorkshopSearchResultsGrid", [330, 145, 330, 210, 360]);
        await CaptureAsync(window, destination, "edit-mods.png");

        tabs.SelectedItem = Control<TabItem>(window, "ModifiedUpksTab");
        var statuses = new[]
        {
            DemoMod("Ember armour", 20, true, ArkiveModAssetOutcome.Winning),
            DemoMod("Violet armour", 5, true, ArkiveModAssetOutcome.Overridden),
            DemoMod("Weapon recolour", 0, false, ArkiveModAssetOutcome.Disabled),
        };
        var modType = typeof(MainWindow).GetNestedType("ModLibraryRow", BindingFlags.NonPublic)!;
        var modRows = Array.CreateInstance(modType, statuses.Length);
        for (var index = 0; index < modRows.Length; index++)
            modRows.SetValue(Activator.CreateInstance(modType, [statuses[index], @"E:\ArkiveData\mods\" + statuses[index].Mod.FileName]), index);
        Control<DataGrid>(window, "ModLibraryGrid").ItemsSource = modRows;
        var assetType = typeof(MainWindow).GetNestedType("ModAssetRow", BindingFlags.NonPublic)!;
        var details = Array.CreateInstance(assetType, statuses[0].Assets.Count);
        for (var index = 0; index < details.Length; index++)
            details.SetValue(Activator.CreateInstance(assetType, [statuses[0].Assets[index]]), index);
        Control<DataGrid>(window, "ModLibraryAssetsGrid").ItemsSource = details;
        Control<TextBlock>(window, "ModLibraryPendingText").Text = "Fictional demo · review priority winners before applying changes.";
        Control<TextBlock>(window, "ModLibraryStatusText").Text = "3 example mods · 2 enabled · no game files changed";
        DemoStatus(window);
        ResizeColumns(window, "ModLibraryGrid", [40, 70, 270, 80, 145, 180, 165, 175, 125, 275]);
        ResizeColumns(window, "ModLibraryAssetsGrid", [95, 190, 185, 285, 190, 130, 130, 130, 130]);
        await CaptureAsync(window, destination, "mod-library.png");

        tabs.SelectedItem = Control<TabItem>(window, "LanguageTab");
        await UntilAsync(() => Control<TextBlock>(window, "LanguageStatusText").Text.Contains("game folder", StringComparison.Ordinal));
        const int lines = 620_782;
        Control<ListBox>(window, "LanguageList").ItemsSource = new[]
        {
            new LanguageRow(new GameLanguage("English", "English", lines, lines, true), false),
            new LanguageRow(new GameLanguage("Spanish", "Español (Spanish)", lines, lines, true), true),
            new LanguageRow(new GameLanguage("French", "Français (French)", 550_321, lines, true), false),
            new LanguageRow(new GameLanguage("German", "Deutsch (German)", 550_381, lines, false), false),
        };
        Control<TextBlock>(window, "LanguageSavedText").Text =
            "Translations saved from the game on 20 October 2026. Arkive keeps them, so a language the game removes can still be used.";
        Control<TextBlock>(window, "LanguageStatusText").Text = "In use: Español (Spanish).";
        Control<Button>(window, "LanguageRestoreButton").IsEnabled = true;
        DemoStatus(window);
        await CaptureAsync(window, destination, "language.png");

        tabs.SelectedItem = Control<TabItem>(window, "TweaksTab");
        await UntilAsync(() => Control<ItemsControl>(window, "TweakList").Items.Count > 0);
        var tweakRows = Control<ItemsControl>(window, "TweakList").Items.OfType<TweakRow>()
            .Select(row => new TweakRow(row.Tweak, row.Tweak.Id is not ("readycheck" or "fastfacet" or "exchangemax" or "autopullup"))).ToArray();
        Control<ItemsControl>(window, "TweakList").ItemsSource = tweakRows;
        Control<TextBlock>(window, "TweaksStatusText").Text =
            $"All tweaks are installed; {tweakRows.Count(row => row.On == true):N0} of {tweakRows.Length:N0} are enabled. Enable, disable and change them in game.";
        Control<Button>(window, "TweaksRemoveAllButton").IsEnabled = true;
        DemoStatus(window);
        await CaptureAsync(window, destination, "tweaks.png");
        window.Close();
        await UntilAsync(() => !window.IsVisible);
    }

    private static ArkiveModStatus DemoMod(string name, int priority, bool enabled, ArkiveModAssetOutcome outcome)
    {
        var assets = new[] { "demo_armour_d", "demo_armour_e" }.Select((name, index) => new ArkiveModFileAsset(
            "demo-" + index, UnifiedPackageAssetKind.Texture2D, index == 0 ? "Armour base colour" : "Armour emissive",
            new("demo_guardian", "DEMO_CHARACTER", "Texture2D", name, index + 2, new string('0', 64), new string('0', 64)), [])
        { Texture = new("PF_DXT5 / BC3", 1024, 1024, TextureImportAlphaPolicy.PreserveOriginal) }).ToArray();
        var now = DateTimeOffset.UtcNow;
        var manifest = new ArkiveModFileManifest("arkive.mod", 1, Guid.NewGuid().ToString("N"), name + " · demo",
            "1.0.0", "Arkive examples", "Fictional screenshot data; not an installable mod.", null, ["demo"],
            "western-global-steam-v1", now, now, ArkiveModAuthoringService.CurrentCreatedWith, assets);
        var entry = new ArkiveModLibraryEntry(manifest.ModId, name + ".arkive", new string('0', 64), 0, now,
            enabled, priority, ArkiveModOrigin.Created, manifest, []);
        return new(entry, false, assets.Select(asset => new ArkiveModAssetStatus(asset, outcome, null, null, null,
            ArkiveModSetupState.NotSetUp, false, null)).ToArray());
    }

    private static void DemoStatus(MainWindow window) =>
        Control<TextBlock>(window, "StatusText").Text = "Screenshot demonstration · fictional data · no game packages or mods are distributed";

    private static void ResizeColumns(MainWindow window, string name, double[] widths)
    {
        var grid = Control<DataGrid>(window, name);
        for (var index = 0; index < Math.Min(widths.Length, grid.Columns.Count); index++)
            grid.Columns[index].Width = widths[index];
    }

    private static async Task CaptureAsync(MainWindow window, string directory, string name)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }
}
