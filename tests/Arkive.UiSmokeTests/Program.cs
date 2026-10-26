using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arkive.App;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Mods;
using Arkive.Core.Proportions;
using Arkive.Core.Validation;

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
        UnexpectedErrorChecks.Install();
        app.Startup += async (_, _) =>
        {
            try
            {
                if (args is ["--public-screenshots", var destination])
                    await PublicScreenshotCapture.RunAsync(root, destination);
                else if (args is ["--zone-viewer", ..])
                    await ZoneViewerChecks.VerifyAsync(root, args.ElementAtOrDefault(1));
                else if (args is ["--fonts", ..])
                    await FontChecks.VerifyAsync(root, args.ElementAtOrDefault(1));
                else
                    await RunAsync(root, args.FirstOrDefault());
                UnexpectedErrorChecks.ThrowIfAny();
                Console.WriteLine(args is ["--public-screenshots", _]
                    ? "Arkive public GUI screenshots rendered with fictional data."
                    : "All Arkive authoring UI smoke tests passed.");
                app.Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                app.Shutdown(1);
            }
        };
        try { return app.Run(); }
        finally { DeleteFixture(root); }
    }

    // A 3D map's browser processes can hold their data folder (or deny access to it) for a while after the window
    // closes. The folder is temporary, so a file still held after a few seconds must not fail the run.
    private static void DeleteFixture(string root)
    {
        for (var attempt = 0; Directory.Exists(root) && attempt < 40; attempt++)
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        if (Directory.Exists(root)) Console.WriteLine($"Note: a browser process still held part of {root}; it was left for Windows to clean up.");
    }

    private static async Task RunAsync(string root, string? screenshotDirectory)
    {
        CreateLibraryFixture(root);
        var window = CreateWindow(root);
        window.Show();
        await UntilAsync(() => Control<DataGrid>(window, "LibraryGrid").Items.Count == 3);
        // In-game names come from the game database mapping, which loads after the first search on slow machines.
        await UntilAsync(() => Control<TextBlock>(window, "GameMetadataText").Text.Contains("relationships", StringComparison.Ordinal),
            () => $"The game database mapping did not load: {Control<TextBlock>(window, "GameMetadataText").Text}");
        var tabs = Control<TabControl>(window, "MainTabControl");
        Check(tabs.Items.OfType<TabItem>().Where(tab => tab.Visibility == Visibility.Visible)
                .Select(tab => tab.Header.ToString()).SequenceEqual(["Asset Library", "Create Mod", "Edit Mods", "Simple Upscale", "Outfit Swap", "Sounds", "Login Screen", "Fonts", "Language", "Tweaks", "Mods"]),
            "The main navigation should expose Asset library, Create mod, Edit mods, Simple upscale, Outfit swap, Sounds, Login screen, Fonts, Language, Tweaks, and Mods.");
        Check(tabs.SelectedItem == Control<TabItem>(window, "ModifiedUpksTab"), "Mods was not the initial destination.");
        var kinds = Control<ComboBox>(window, "WorkshopKindComboBox").Items.OfType<ComboBoxItem>()
            .Select(item => $"{item.Content}={item.Tag}").ToArray();
        Check(kinds.SequenceEqual(["Textures=Texture", "Skeletal meshes=SkeletalMesh", "Static meshes=StaticMesh",
                  "Materials=MaterialInstanceConstant", "Particle effects=ParticleSystem", "Interface movies=InterfaceMovie",
                  "All moddable assets="]) &&
              Control<ComboBox>(window, "WorkshopKindComboBox").SelectedIndex == 6,
            "Edit mods does not offer the moddable asset types: " + string.Join(", ", kinds));
        Check(Control<Button>(window, "ModLibraryNewButton").IsVisible, "The initial mod library has no visible content.");
        GameUpdateChecks.Verify(window);
        await UnexpectedErrorChecks.VerifyAsync(window);
        SkillPreviewEditChecks.Verify();
        OutfitSwapClassChecks.Verify(window, root);
        // Without the game's EFGame folder the Login screen tab says what it needs instead of failing.
        tabs.SelectedItem = Control<TabItem>(window, "LoginScreenTab");
        await UntilAsync(() => Control<TextBlock>(window, "LoginScreenStatusText").Text.Contains("game folder", StringComparison.Ordinal),
            () => $"The Login screen tab did not explain that it needs the game folder: {Control<TextBlock>(window, "LoginScreenStatusText").Text}");
        Check(!Control<Button>(window, "LoginScreenUseButton").IsEnabled, "Use this screen was enabled with nothing to use.");
        Console.WriteLine("PASS: the Login screen tab explains what it needs when the game folder is unknown");
        tabs.SelectedItem = Control<TabItem>(window, "FontsTab");
        await UntilAsync(() => Control<TextBlock>(window, "FontStatusText").Text.Contains("game folder", StringComparison.Ordinal),
            () => $"The Fonts tab did not explain that it needs the game folder: {Control<TextBlock>(window, "FontStatusText").Text}");
        Check(!Control<Button>(window, "FontApplyButton").IsEnabled && !Control<ItemsControl>(window, "FontSlotList").IsEnabled,
            "The Fonts tab offers choices without a game folder.");
        Console.WriteLine("PASS: the Fonts tab explains what it needs when the game folder is unknown");
        tabs.SelectedItem = Control<TabItem>(window, "LanguageTab");
        await UntilAsync(() => Control<TextBlock>(window, "LanguageStatusText").Text.Contains("game folder", StringComparison.Ordinal),
            () => $"The Language tab did not explain that it needs the game folder: {Control<TextBlock>(window, "LanguageStatusText").Text}");
        Check(!Control<Button>(window, "LanguageUseButton").IsEnabled && !Control<Button>(window, "LanguageRestoreButton").IsEnabled,
            "The Language tab offered a change with no game to change.");
        Console.WriteLine("PASS: the Language tab explains what it needs when the game folder is unknown");
        tabs.SelectedItem = Control<TabItem>(window, "TweaksTab");
        await UntilAsync(() => Control<ItemsControl>(window, "TweakList").Items.Count > 0,
            () => "The Tweaks tab listed no tweaks.");
        var tweakRows = Control<ItemsControl>(window, "TweakList").Items.OfType<TweakRow>().ToArray();
        Check(tweakRows.Length == Arkive.Core.Tweaks.TweakCatalog.All.Count(tweak => tweak.Available) &&
              tweakRows.All(row => row.On is null && !string.IsNullOrWhiteSpace(row.Description)),
            "The Tweaks tab does not list every finished tweak, with its description and no on/off state while none is installed.");
        await UntilAsync(() => Control<Button>(window, "TweaksEnableAllButton").IsVisible, () => "The Tweaks tab shows no Enable All Tweaks button.");
        Check(!Control<Button>(window, "TweaksDisableAllButton").IsEnabled, "The Tweaks tab offers Disable All Tweaks with nothing installed.");
        Console.WriteLine($"PASS: the Tweaks tab lists {tweakRows.Length} tweaks with Enable All and Disable All");
        tabs.SelectedItem = Control<TabItem>(window, "ModifiedUpksTab");
        Console.WriteLine("PASS: asset discovery, creation, editing, and deployment have separate main tabs");
        tabs.SelectedItem = Control<TabItem>(window, "LibraryTab");
        Check(Control<Button>(window, "BuildGameDatabaseButton").IsEnabled &&
              Control<ComboBox>(window, "GameDatabaseLanguageComboBox").SelectedIndex == 0 &&
              Control<FrameworkElement>(window, "GameDatabaseProgressPanel").Visibility == Visibility.Collapsed,
            "Fresh-install database controls or their initial state are missing.");
        Check(Control<Button>(window, "PackageActionsButton").ContextMenu.Items.OfType<MenuItem>()
            .Any(item => item.Header.ToString() == "About / licence"), "The application licence notice is missing.");
        Check((Control<ComboBox>(window, "LibrarySearchScopeComboBox").SelectedItem as ComboBoxItem)?.Tag?.ToString() == "AllFields",
            "Filename search is not the default library scope.");
        await SearchLibraryAsync(window, "UI fixture boss");
        var results = Control<DataGrid>(window, "LibraryGrid");
        Check(results.Items.Count == 1,
            $"The asset library cannot search by in-game name ({results.Items.Count} results: {Control<TextBlock>(window, "LibraryCountText").Text}).");
        results.SelectedIndex = 0;
        await UntilAsync(
            () => Control<Button>(window, "LibraryInspectButton").IsVisible &&
                  Control<Button>(window, "LibraryCopyIdentityButton").IsEnabled,
            () => "UModel preview or asset information controls are no longer available: " +
                  $"tab={(tabs.SelectedItem as TabItem)?.Name}, results={results.Items.Count}, " +
                  $"selected={results.SelectedIndex}, windowVisible={window.IsVisible}, " +
                  $"inspectVisible={Control<Button>(window, "LibraryInspectButton").IsVisible}, " +
                  $"copyEnabled={Control<Button>(window, "LibraryCopyIdentityButton").IsEnabled}.");
        await SearchLibraryAsync(window, "fixture_sound");
        Check(results.Items.Count == 1, "The asset library hides assets that cannot be modded.");
        await SearchLibraryAsync(window, "fixture-package.upk");
        Check(results.Items.Count == 3, "The asset library cannot search by package filename.");
        await SearchLibraryAsync(window, "fixture_alias.upk");
        Check(results.Items.Count == 3, "The asset library cannot search by decoded filename.");
        if (screenshotDirectory is not null) Capture(window, screenshotDirectory, "asset-library.png");
        Console.WriteLine("PASS: full asset library searches filenames, game names, and metadata-only assets with preview and information controls");
        await CheckProblemReportAsync(window, root);
        CheckSimpleUpscalePresets(window, root);
        CheckEditModsUpscaleAndMeshChecks(window);
        CheckEditableComboBox();
        await CheckCleanUpWorkingFilesAsync(window);
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
        // Opening an advanced view can reload the package workspace behind the busy card, which refuses clicks until done.
        await UntilAsync(() => Control<Panel>(window, "BusyOverlay").Visibility != Visibility.Visible,
            () => "The package workspace did not finish loading after an advanced view opened.");
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
        Check(Control<Expander>(window, "AuthoringSearchExpander").IsEnabled, "A named draft cannot add assets.");
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
        await UntilAsync(() => Control<DataGrid>(window, "LibraryGrid").Items.Count == 3,
            () => "The reopened workspace did not finish restoring its asset library.");
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
        await ModLibraryRowChecks.VerifyAsync(window, root);
        await ModPackChecks.VerifyAsync(window, root, screenshotDirectory);
        await ModSearchDropChecks.VerifyAsync(window, root, screenshotDirectory);
        await ZoneViewerChecks.VerifyAsync(root, screenshotDirectory);
        await FontChecks.VerifyAsync(root, screenshotDirectory);
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

    private static T Invoke<T>(MainWindow window, string method, params object?[] arguments) =>
        (T)typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments)!;

    // The button itself shows a message box and Explorer, so the report is made from the same content directly.
    private static async Task CheckProblemReportAsync(MainWindow window, string root)
    {
        Check(Control<Button>(window, "ReportProblemButton").IsVisible, "Report a problem is not in the top bar.");
        var content = await Invoke<Task<ArkiveProblemReportContent>>(window, "ProblemReportContentAsync");
        var path = await ArkiveProblemReport.CreateAsync(Path.Combine(root, "reports"), content);
        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(entry => entry.FullName).ToArray();
        Check(names.Contains("summary.txt") && names.Contains("mods.txt") && names.Contains("mesh-checks.txt") &&
              names.Any(name => name.StartsWith("logs/arkive-", StringComparison.Ordinal)),
            "The problem report is missing parts: " + string.Join(", ", names));
        Check(content.Summary.StartsWith("Arkive ", StringComparison.Ordinal) && content.Summary.Contains("Game Packages folder:", StringComparison.Ordinal) &&
              content.Summary.Contains("Last messages shown:", StringComparison.Ordinal),
            "The problem report summary is incomplete: " + content.Summary);
        Check(!names.Any(name => name.Contains("settings", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".upk", StringComparison.OrdinalIgnoreCase)),
            "The problem report holds settings or game files.");
        Console.WriteLine("PASS: Report a problem packs the summary, recent logs, mod list and mesh checks into one zip");
    }

    private static void CheckSimpleUpscalePresets(MainWindow window, string root)
    {
        var presets = Control<ComboBox>(window, "ProportionPresetComboBox");
        var body = Control<Button>(window, "ProportionApplyBodyButton");
        Check(!body.IsEnabled && !Control<Button>(window, "ProportionApplyPresetButton").IsEnabled &&
              Control<Button>(window, "ProportionCancelButton").Visibility == Visibility.Collapsed,
            "Simple upscale offers preset actions before a preset is chosen.");
        // A preset saved in the proportion editor's own window appears when the list opens.
        new BodyProportionPresetStore(Path.Combine(root, "presets", "body-proportions")).Save(new BodyProportionPreset(
            "UI preset", "female-warrior", new Dictionary<string, double> { ["chest"] = 30 }, true, true, DateTimeOffset.Now));
        Check(Control<ComboBox>(window, "ProportionBodyComboBox").Items.OfType<string>().Contains("Guardianknight"),
            "Simple upscale does not offer the Guardianknight body.");
        Control<ComboBox>(window, "ProportionBodyComboBox").SelectedIndex =
            1 + BodyProfiles.All.ToList().FindIndex(profile => profile.Id == "female-warrior");
        Invoke<object?>(window, "ProportionPresetComboBox_DropDownOpened", presets, EventArgs.Empty);
        Check((presets.SelectedItem as BodyProportionPreset)?.Name == "UI preset", "A preset saved in the editor did not appear in Simple upscale.");
        Check(body.IsEnabled && body.Content.ToString() == "Apply preset to every outfit of its body",
            "Apply preset to every outfit of its body is not offered for a player body's preset.");
        var together = (int)typeof(MainWindow).GetProperty("ProportionParallelism", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Check(together == (Environment.ProcessorCount < 4 ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 2, 4)),
            $"Body-wide upscales would run {together} pieces at once on {Environment.ProcessorCount} processor threads.");
        Console.WriteLine("PASS: Simple upscale lists presets saved in the editor and offers them for every outfit of their body");
    }

    private static void CheckEditModsUpscaleAndMeshChecks(MainWindow window)
    {
        Check(Control<Border>(window, "AuthoringCheckPanel").Visibility == Visibility.Collapsed,
            "Edit mods shows a mesh check panel with nothing failing.");
        var proportions = Control<Button>(window, "AuthoringProportionsButton");
        Check(proportions.Visibility == Visibility.Collapsed, "Edit in proportion editor is offered without an upscale mod.");
        var field = typeof(MainWindow).GetField("_authoringDraft", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = field.GetValue(window);
        var warrior = BodyProfiles.All.First(profile => profile.Id == "female-warrior");
        var settings = new BodyProportionModSettings(warrior, new Dictionary<string, double> { ["chest"] = 30 }, BodyProportionFollow.All);
        field.SetValue(window, new ArkiveModAuthoringDraft(1, "ui-upscale-draft", "ui-upscale-mod",
            new ArkiveModMetadata("UI upscale", "1.0.0", Description: BodyProportionModTags.Description(settings, ["pc_wr_f_av_226a_upper_sk"]),
                Tags: BodyProportionModTags.Write(settings)), [], DateTimeOffset.Now));
        Invoke<object?>(window, "UpdateAuthoringActions");
        var shown = proportions.Visibility == Visibility.Visible;
        field.SetValue(window, previous);
        Invoke<object?>(window, "UpdateAuthoringActions");
        Check(shown, "Edit in proportion editor is not offered for a saved upscale mod.");
        Console.WriteLine("PASS: Edit mods offers Edit in proportion editor for upscale mods and keeps the mesh check panel hidden until a mesh fails");
    }

    // Nothing in a fresh library qualifies (and anything under an hour old is kept), so no confirmation opens.
    private static async Task CheckCleanUpWorkingFilesAsync(MainWindow window)
    {
        var field = (string name) => (MenuItem)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var cleanUp = field("ModLibraryCleanUpWorkingFilesButton");
        Check(cleanUp.Header.ToString() == "Clean up working files…" &&
              ReferenceEquals(cleanUp.Parent, field("ModLibraryRestoreOtherFolderButton").Parent),
            "Clean up working files is not in Mods > More actions.");
        var status = Control<TextBlock>(window, "ModLibraryStatusText");
        Invoke<object?>(window, "ModLibraryCleanUpWorkingFilesButton_Click", cleanUp, new RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(() => status.Text.StartsWith("No other working files", StringComparison.Ordinal) ||
                               status.Text.StartsWith("Freed", StringComparison.Ordinal) ||
                               status.Text.StartsWith("Working files were not", StringComparison.Ordinal),
            () => $"Clean up working files did not finish: {status.Text}");
        Check(status.Text.Contains("No other working files to clean up", StringComparison.Ordinal),
            "Clean up working files found something to delete in a fresh library: " + status.Text);
        Console.WriteLine("PASS: Mods > More actions > Clean up working files finds nothing to delete in a fresh library without asking");
    }

    private static void CheckEditableComboBox()
    {
        var editable = new ComboBox { IsEditable = true, Width = 200 };
        var fixedChoice = new ComboBox { Width = 200 };
        var host = new Window
        {
            Content = new StackPanel { Children = { editable, fixedChoice } },
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, Width = 300, Height = 200,
        };
        host.Show();
        try
        {
            editable.ApplyTemplate();
            fixedChoice.ApplyTemplate();
            var text = (TextBox)editable.Template.FindName("PART_EditableTextBox", editable);
            text.Text = "Typed name";
            host.UpdateLayout();
            Check(text.IsVisible && editable.Text == "Typed name", "An editable combo box (the Preset box) cannot be typed into.");
            Check(fixedChoice.Template.FindName("PART_EditableTextBox", fixedChoice) is TextBox { IsVisible: false },
                "A choice-only combo box shows a text box.");
            // Typing a new preset name must not pick (and apply) an existing preset whose name starts the same way.
            editable.ItemsSource = new[] { "preset 1" };
            editable.IsTextSearchEnabled = false;
            text.Text = "";
            // Auto-complete only runs while the box has keyboard focus, as when someone types into it.
            host.Activate();
            text.Focus();
            var selections = 0;
            editable.SelectionChanged += (_, _) => selections++;
            foreach (var character in "preset 3")
                System.Windows.Input.TextCompositionManager.StartComposition(
                    new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, text, character.ToString()));
            host.UpdateLayout();
            Check(selections == 0 && editable.Text == "preset 3",
                $"Typing a new preset name selected (and would apply) an existing preset: selections={selections}, text={editable.Text}.");
        }
        finally { host.Close(); }
        Console.WriteLine("PASS: editable combo boxes take typed text, choice-only ones do not, and typing a name selects no preset");
    }

    private static async Task SearchLibraryAsync(MainWindow window, string query)
    {
        Control<TextBox>(window, "LibrarySearchTextBox").Text = query;
        Control<Button>(window, "LibrarySearchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Control<TextBlock>(window, "LibraryCountText").Text is var text &&
                               !text.StartsWith("Searching", StringComparison.Ordinal) &&
                               !text.StartsWith("Preparing", StringComparison.Ordinal));
    }

    private static MainWindow CreateWindow(string root) => new(root)
    {
        Width = 1440,
        Height = 900,
        Left = -5000,
        Top = -5000,
        // Off screen and never taking focus, so a local run does not get in the way of whoever uses the PC.
        ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
    };

    internal static T Control<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing UI control: {name}");

    internal static async Task UntilAsync(Func<bool> condition, Func<string>? failureMessage = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
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
