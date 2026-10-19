using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Arkive.App;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Materials;
using Arkive.Core.Textures;
using static Program;

/// <summary>
/// The Asset Library's map cards and the Zone Viewer, off screen and with made-up map data (no game files): groups,
/// shared and only-this-map parts, filters, previews, the live recolour and the add-to-mod hand-off.
/// </summary>
internal static class ZoneViewerChecks
{
    internal static async Task VerifyAsync(string root, string? screenshotDirectory)
    {
        await VerifyLibraryCardAsync(root, screenshotDirectory);
        await VerifyViewerAsync(root, screenshotDirectory);
    }

    private static async Task VerifyLibraryCardAsync(string root, string? screenshotDirectory)
    {
        var library = Path.Combine(root, "zone-library");
        var packages = Path.Combine(library, "Packages");
        var cache = Path.Combine(library, "cache", "library-index");
        Directory.CreateDirectory(packages);
        Directory.CreateDirectory(cache);
        var levelPath = Path.Combine(packages, "level-package.upk");
        File.WriteAllBytes(levelPath, []);
        File.WriteAllText(Path.Combine(cache, "level.json"), JsonSerializer.Serialize(new PackageIndexDocument(levelPath,
            "level-package", "lv_fixture_ed_01_ps", 0, File.GetLastWriteTimeUtc(levelPath).Ticks, DateTimeOffset.UtcNow,
            [new IndexedAsset(1, AssetKind.Material, "MaterialInstanceConstant", "fogvolumemi_0", 20, 40)], null)));
        var metadataPath = Path.Combine(library, "metadata.json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(new GameAssetMetadataDocument(1, "English", library, DateTimeOffset.UtcNow,
            [new GameAssetAssociation("UI fixture boss", "tip.name.fixture.boss", "Npc", 1, "Model",
                "lv_fixture_ed_01_ps.fogvolumemi_0", "lv_fixture_ed_01_ps", "fogvolumemi_0")])));
        File.WriteAllText(Path.Combine(library, "cache", "arkive-settings.json"), JsonSerializer.Serialize(new
        {
            LibraryRootPath = packages,
            GameMetadataPath = metadataPath,
            LibrarySearchText = "",
        }));
        // Saved zone names, as Arkive keeps them beside the game database (there is no leveldata1.lpk to read them from).
        await ZoneMapSidecar.SaveAsync(ZoneMapSidecar.GetDefaultPath(metadataPath), new ZoneMapDocument(ZoneMapSidecar.CurrentVersion,
            DateTimeOffset.UnixEpoch, "leveldata1.lpk", 0, 0,
            [
                new GameZone(41001, "Fixture Land of Order", ["lv_fixture_ed_01_ps"]),
                new GameZone(41002, "Fixture Land of Order", ["lv_fixture_ed_01_ps"]),
                new GameZone(41003, "Unrelated Field", ["lv_unrelated_ps"]),
            ]));
        var window = new MainWindow(library)
        {
            Width = 1440, Height = 900, Left = -5000, Top = -5000, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        try
        {
            await UntilAsync(() => Control<TextBlock>(window, "GameMetadataText").Text.Contains("relationships", StringComparison.Ordinal),
                () => $"The game database mapping did not load: {Control<TextBlock>(window, "GameMetadataText").Text}");
            var zoneMap = typeof(MainWindow).GetField("_zoneMap", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await UntilAsync(() => zoneMap.GetValue(window) is not null, () => "The saved zone names were not loaded.");
            Control<TabControl>(window, "MainTabControl").SelectedItem = Control<TabItem>(window, "LibraryTab");
            var panel = Control<Border>(window, "LibraryZonePanel");
            Check(panel.Visibility == Visibility.Collapsed, "The map cards show before any search.");
            Control<TextBox>(window, "LibrarySearchTextBox").Text = "land of order";
            await Invoke(window, "ApplyLibraryFilterAsync", "ui test");
            var cards = Control<ItemsControl>(window, "LibraryZoneCards");
            await UntilAsync(() => panel.Visibility == Visibility.Visible && cards.Items.Count == 1,
                () => $"Searching a zone name did not show its map card: visible={panel.Visibility}, cards={cards.Items.Count}.");
            window.UpdateLayout();
            var card = FindVisual<Button>(cards).FirstOrDefault() ?? throw new InvalidOperationException("The map card has no button.");
            var texts = FindVisual<TextBlock>(card).Select(text => text.Text).ToArray();
            Check(texts.Contains("Fixture Land of Order") && texts.Contains(ZoneViewerTextFor("CardAction")) &&
                  texts.Any(text => text.Contains("2 zones share this map", StringComparison.Ordinal)),
                "The map card does not name the map, its two zones and its action: " + string.Join(" | ", texts));
            Check(Control<TextBlock>(window, "LibraryZoneHeaderText").Text.StartsWith("Map matching your search", StringComparison.Ordinal),
                "The map card header is missing.");
            if (screenshotDirectory is not null) Capture(window, screenshotDirectory, "zone-card.png");
            Control<TextBox>(window, "LibrarySearchTextBox").Text = "fogvolumemi";
            await Invoke(window, "ApplyLibraryFilterAsync", "ui test");
            await UntilAsync(() => panel.Visibility == Visibility.Collapsed, () => "The map card stayed for a search that names no zone.");
            Console.WriteLine("PASS: searching a zone name shows one card per map above the Asset Library results, and other searches hide it");
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task VerifyViewerAsync(string root, string? screenshotDirectory)
    {
        var directory = Path.Combine(root, "zone-viewer");
        Directory.CreateDirectory(directory);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in new[] { "lv_fixture_ed_01_ps", "lv_fixture_ed_01_sl01", "lv_fixture_ed_01_sl01_b" })
            File.WriteAllBytes(files[alias] = Path.Combine(directory, alias + ".upk"), [1]);
        // The second zone's map is one of the first map's streaming levels, so parts in it are shared.
        var zones = new ZoneMapDocument(1, DateTimeOffset.UnixEpoch, "leveldata1.lpk", 0, 0,
        [
            new GameZone(41001, "Fixture Land of Order", ["lv_fixture_ed_01_ps"]),
            new GameZone(41004, "Fixture Neighbour", ["lv_fixture_ed_01_sl01_b"]),
        ]);
        var usage = await ZoneUsageIndex.BuildAsync(Path.Combine(directory, "usage.bin.gz"), zones, files, NoChunkProtectionProvider.Instance);
        var map = usage.MapOf(41001)!;
        var parts = new[]
        {
            Part("lv_fixture_ed_01_ps", "fogvolumemi_0", "MaterialInstanceConstant", AssetKind.Material, ZonePartGroup.Fog, true),
            Part("lv_fixture_ed_01_ps", "fixture_land_mi_01", "MaterialInstanceConstant", AssetKind.Material, ZonePartGroup.Terrain, true),
            Part("lv_fixture_ed_01_ps", "fixture_sky_d", "Texture2D", AssetKind.Texture, ZonePartGroup.Sky, true),
            Part("lv_fixture_ed_01_sl01_b", "fixture_shared_rock_d", "Texture2D", AssetKind.Texture, ZonePartGroup.Textures, true),
            Part("bg_fixture_stone", "fixture_rock_sm", "StaticMesh", AssetKind.StaticMesh, ZonePartGroup.Meshes, false),
            Part("lv_fixture_ed_01_sl01", "directionalmaxcomponent0_1", "LightMapTexture2D", AssetKind.Texture, ZonePartGroup.GeneratedMaps, true),
        };
        var report = new ZoneMapReport(map, parts, new ZoneLevelSettings(1, 3, 1, 2), 0, 0);
        var added = new List<(IReadOnlyList<PackageIndexMatch> Parts, TextureRecolour? Tint)>();
        var opened = new List<string>();
        var host = new ZoneViewerHost(
            (_, _) => Task.FromResult(report),
            (_, _) => Task.FromResult<ZoneUsageIndex?>(usage),
            (_, _) => Task.FromResult(Picture()),
            (asset, _) => Task.FromResult(Material(asset)),
            asset => opened.Add(asset.ObjectName),
            () => "UI zone mod",
            () => { },
            (chosen, tint, _, _) =>
            {
                added.Add((chosen, tint));
                return Task.FromResult($"Added {chosen.Count} parts.");
            },
            (_, _, _, _) => Task.FromResult("Exported."),
            _ => { },
            _ => { },
            (_, _) => Task.FromResult(WriteScene(Path.Combine(directory, "scene"), map.Key)),
            Path.Combine(directory, "webview2"));
        var viewer = new ZoneViewerWindow(map, host, "Light")
        {
            Left = -5000, Top = -5000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        viewer.Show();
        try
        {
            await UntilAsync(() => viewer.PartsLoaded && viewer.SharingLoaded, () => "The Zone Viewer did not finish reading the map.");
            T Find<T>(string name) where T : class =>
                viewer.FindName(name) as T ?? throw new InvalidOperationException($"Missing Zone Viewer control: {name}");
            Check(viewer.Title == "Arkive Zone Viewer · Fixture Land of Order" &&
                  Find<TextBlock>("HeaderText").Text == "Fixture Land of Order" &&
                  Find<TextBlock>("SettingsText").Text.Contains("can't change yet", StringComparison.Ordinal),
                "The Zone Viewer header does not name the map or explain its level settings.");
            var groups = Find<ListBox>("GroupsList");
            var titles = groups.Items.Cast<object>().Select(item => (string)item.GetType().GetProperty("Title")!.GetValue(item)!).ToArray();
            Check(titles.SequenceEqual(["All Parts", "Terrain", "Sky", "Fog", "Meshes", "Textures", "Lightmaps and Masks"]),
                "The Zone Viewer's groups are wrong or not in Title Case: " + string.Join(", ", titles));
            var grid = Find<DataGrid>("PartsGrid");
            Check(grid.Items.Count == 5, $"All Parts should list every part except lightmaps and masks ({grid.Items.Count} shown).");
            var shared = viewer.Rows.Single(row => row.Name == "fixture_shared_rock_d");
            var own = viewer.Rows.Single(row => row.Name == "fogvolumemi_0");
            Check(shared.UsedBy == "Shared with 1 other map" && shared.UsedByTip == "Fixture Neighbour" && own.UsedBy == "Only this map",
                $"Parts are not marked as shared or only this map's: {shared.UsedBy} ({shared.UsedByTip}); {own.UsedBy}.");
            Check(Find<TextBlock>("SharingText").Text.StartsWith("4 of 5 parts are only this map's", StringComparison.Ordinal),
                "The header does not say how many parts are only this map's: " + Find<TextBlock>("SharingText").Text);
            Find<ComboBox>("ShowComboBox").SelectedIndex = 1;
            Check(grid.Items.Count == 4 && !grid.Items.Contains(shared), "Only this map's parts still lists a shared part.");
            Find<ComboBox>("ShowComboBox").SelectedIndex = 2;
            Check(grid.Items.Count == 1 && grid.Items.Contains(shared), "Parts shared with other maps does not list just the shared part.");
            Find<ComboBox>("ShowComboBox").SelectedIndex = 0;
            Find<TextBox>("FilterTextBox").Text = "sky";
            Check(grid.Items.Count == 1, "The filter box does not narrow the list by name.");
            Find<TextBox>("FilterTextBox").Text = string.Empty;
            groups.SelectedIndex = titles.ToList().IndexOf("Lightmaps and Masks");
            Check(grid.Items.Count == 1, "The Lightmaps and Masks group does not list the lightmap.");
            groups.SelectedIndex = 0;
            Console.WriteLine("PASS: the Zone Viewer groups a map's parts in Title Case, marks shared and only-this-map parts, and filters them");

            var add = Find<Button>("AddButton");
            var tint = Find<Button>("TintButton");
            Check(!add.IsEnabled && !tint.IsEnabled && add.Content.ToString() == "Add selected to mod",
                "Add or recolour is offered with nothing selected.");
            var sky = viewer.Rows.Single(row => row.Name == "fixture_sky_d");
            grid.SelectedItem = sky;
            var image = Find<Image>("PreviewImage");
            await UntilAsync(() => image.Source is not null, () => "The selected texture was not previewed.");
            Check(add.IsEnabled && add.Content.ToString() == "Add 1 part to mod" && !tint.IsEnabled,
                "Add is not offered for a selected part, or recolour is offered before a colour is set.");
            var before = image.Source;
            Find<Slider>("BlueSlider").Value = 1;
            Find<Slider>("RedSlider").Value = 0;
            Find<Slider>("GreenSlider").Value = 0;
            Find<Slider>("StrengthSlider").Value = 1;
            await UntilAsync(() => !ReferenceEquals(image.Source, before), () => "The preview did not show the recolour as it was set up.");
            Check(tint.IsEnabled && tint.Content.ToString() == "Recolour 1 part and add to mod", "Recolour is not offered once a colour is set.");
            Check(Find<TextBlock>("TargetModText").Text.Contains("UI zone mod", StringComparison.Ordinal) &&
                  Find<Button>("ShowEditModsButton").Visibility == Visibility.Collapsed,
                "The viewer does not say which mod parts are added to.");
            if (screenshotDirectory is not null) Capture(viewer, screenshotDirectory, "zone-viewer.png");

            grid.SelectedItem = own;
            var vectors = Find<ItemsControl>("VectorsList");
            await UntilAsync(() => vectors.Items.Count == 1, () => "The selected material's colour parameters were not shown.");
            var swatch = vectors.Items[0]!;
            var original = (SolidColorBrush)swatch.GetType().GetProperty("Original")!.GetValue(swatch)!;
            var tinted = (SolidColorBrush)swatch.GetType().GetProperty("Tinted")!.GetValue(swatch)!;
            Check(tinted.Color.B > tinted.Color.R && original.Color.R > original.Color.B,
                $"The material's colour swatch does not preview the recolour: {original.Color} → {tinted.Color}.");
            tint.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => added.Count == 1 && Find<TextBlock>("StatusText").Text == "Added 1 parts.",
                () => "Recolour and add did not hand the part and the colour to the mod.");
            Check(added[0].Parts.Single().ObjectName == "fogvolumemi_0" && added[0].Tint is { Blue: 1, Red: 0, Strength: 1 },
                "Recolour and add passed the wrong part or colour.");
            var mesh = viewer.Rows.Single(row => row.Name == "fixture_rock_sm");
            grid.SelectedItem = mesh;
            await UntilAsync(() => Find<TextBlock>("PreviewMessage").Text.Contains("Asset Viewer", StringComparison.Ordinal),
                () => "A mesh does not point to the Asset Viewer.");
            Check(add.IsEnabled && !tint.IsEnabled && tint.Content.ToString() == "Recolour and add to mod",
                "A mesh can be recoloured here, or cannot be added to the mod.");
            Find<Button>("OpenButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(opened.SequenceEqual(["fixture_rock_sm"]), "Open in viewer did not open the selected part.");
            var menu = grid.ContextMenu!;
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
            var headers = menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray();
            Check(headers.SequenceEqual(["Open in viewer", "Recolour and add to mod", "Add 1 part to mod", "Export selected…",
                      "Select only this map's parts in the list", "Select all in the list", "Find in Asset Library", "Copy name"]),
                "The parts' right-click menu is wrong: " + string.Join(", ", headers));
            var selectOnly = menu.Items.OfType<MenuItem>().Single(item => item.Name == "SelectOnlyMenuItem");
            selectOnly.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(grid.SelectedItems.Count == 4 && !grid.SelectedItems.Contains(shared),
                "Select only this map's parts did not pick just the parts no other map uses.");
            Console.WriteLine("PASS: the Zone Viewer previews parts, recolours live, and adds the chosen parts to the mod with the colour");

            var tabs = Find<TabControl>("ViewTabs");
            Check(tabs.Items.OfType<TabItem>().Select(tab => tab.Header.ToString()).SequenceEqual(["Parts List", "3D Map"]),
                "The Zone Viewer's tabs are not Parts List and 3D Map.");
            tabs.SelectedItem = Find<TabItem>("MapTab");
            await UntilAsync(() => viewer.MapLoaded, () => "The 3D map did not draw: " + Find<TextBlock>("MapMessage").Text + " " + Find<TextBlock>("StatusText").Text,
                TimeSpan.FromSeconds(60));
            Check(Find<TextBlock>("StatusText").Text.StartsWith("The 3D map shows 2 placed meshes", StringComparison.Ordinal),
                "The 3D map does not say what it shows: " + Find<TextBlock>("StatusText").Text);
            typeof(ZoneViewerWindow).GetMethod("OnMapMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewer, ["{\"type\":\"pick\",\"mesh\":\"bg_fixture_stone.fixture_rock_sm\",\"material\":\"lv_fixture_ed_01_ps.fogvolumemi_0\"}"]);
            Check(grid.SelectedItems.Count == 2 && grid.SelectedItems.Contains(mesh) && grid.SelectedItems.Contains(own),
                "Clicking a piece of the 3D map did not select its mesh and material in the list.");
            var browser = Find<Microsoft.Web.WebView2.Wpf.WebView2>("MapBrowser");
            var highlighted = "";
            for (var attempt = 0; attempt < 100 && highlighted != "1"; attempt++)
            {
                highlighted = await browser.CoreWebView2.ExecuteScriptAsync("arkiveMap.highlighted()");
                if (highlighted != "1") await Task.Delay(50);
            }
            Check(highlighted == "1", $"The selected parts were not highlighted on the 3D map ({highlighted}).");
            Console.WriteLine("PASS: the 3D Map tab draws the map, and clicking a piece selects its mesh and material, which the map highlights");
        }
        finally
        {
            viewer.Close();
        }
    }

    /// <summary>A tiny map scene: one triangle mesh placed twice, in the format the scene builder writes.</summary>
    private static string WriteScene(string directory, string mapKey)
    {
        Directory.CreateDirectory(directory);
        using (var data = new BinaryWriter(File.Create(Path.Combine(directory, ZoneMapScene.DataFileName))))
        {
            foreach (var value in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) data.Write(value);
            foreach (var value in new[] { 0f, 0f, 1f, 0f, 0f, 1f }) data.Write((Half)value);
            foreach (var value in new ushort[] { 0, 1, 2, 0 }) data.Write(value);
            for (var instance = 0; instance < 2; instance++)
                foreach (var value in new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, instance * 3, 0, 0, 1 }) data.Write(value);
        }
        var scene = new ZoneMapScene(ZoneMapScene.CurrentVersion, mapKey, [0, 0, 0], [4, 1, 0],
            [new ZoneSceneMesh("bg_fixture_stone.fixture_rock_sm", new ZoneSceneRange(0, 3), new ZoneSceneRange(36, 3),
                new ZoneSceneRange(48, 3), [new ZoneSceneSection(0, 3, 0)], ["lv_fixture_ed_01_ps.fogvolumemi_0"])],
            [new ZoneSceneMaterial("lv_fixture_ed_01_ps.fogvolumemi_0", [0.8f, 0.5f, 0.3f], null, null)],
            [new ZoneSceneBatch(0, [0], new ZoneSceneRange(56, 2))], [], 2, 0);
        File.WriteAllText(Path.Combine(directory, ZoneMapScene.FileName), JsonSerializer.Serialize(scene, ZoneMapScene.Json));
        return directory;
    }

    private static ZoneMapPart Part(string package, string name, string className, AssetKind kind, ZonePartGroup group, bool inLevel) =>
        new(new PackageIndexMatch(Path.Combine("fixture", package + ".upk"), package, package, 1, 1, kind, className, name, 10, 20, []),
            group, [package.StartsWith("lv_", StringComparison.Ordinal) ? package : "lv_fixture_ed_01_sl01"], inLevel);

    private static RgbaImage Picture()
    {
        var pixels = new byte[64 * 64 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 220;
            pixels[index + 1] = (byte)(index / 64 % 255);
            pixels[index + 2] = 40;
            pixels[index + 3] = 255;
        }
        return new RgbaImage(64, 64, pixels);
    }

    private static MaterialInstanceReconnaissanceReport Material(PackageIndexMatch asset) => new(1, DateTimeOffset.UnixEpoch,
        asset.SourcePath, asset.PhysicalPackageName, asset.PackageAlias, 0, 0, new string('0', 64), "none", asset.ExportIndex, asset.ExportIndex,
        AssetKind.Material, asset.ClassName, asset.ObjectName, asset.SerialOffset, asset.SerialSize, new string('0', 64), 0, 0, null,
        "material'fogvolumematerial'", [], [],
        [new MaterialVectorParameterEvidence(0, "emissivecolor", 1, 0.61f, 0.39f, 1, 0, 0, Guid.Empty)],
        [new MaterialTextureParameterEvidence(0, "diffuse", 1, "texture2d'fixture_sky_d'", 0, 0, Guid.Empty)],
        MaterialInstanceReadiness.Editable, []);

    private static async Task Invoke(MainWindow window, string method, params object?[] arguments)
    {
        var result = typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
        if (result is Task task) await task;
    }

    private static string ZoneViewerTextFor(string field) =>
        (string)typeof(MainWindow).Assembly.GetType("Arkive.App.ZoneViewerText")!.GetField(field)!.GetValue(null)!;

    private static IEnumerable<T> FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in FindVisual<T>(child)) yield return nested;
        }
    }
}
