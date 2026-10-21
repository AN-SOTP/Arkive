using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Baselines;
using Arkive.Core.Discovery;
using Arkive.Core.Interface;
using Arkive.Core.External;
using Arkive.Core.Exports;
using Arkive.Core.Inspection;
using Arkive.Core.Meshes;
using Arkive.Core.Materials;
using Arkive.Core.Mods;
using Arkive.Core.Packages;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;
using Arkive.Core.Validation;
using Microsoft.Win32;

namespace Arkive.App;

public partial class MainWindow : Window
{
    /// <summary>Hashes of working files and game packages, kept between sessions (see <see cref="ArkiveModEditSnapshot.LoadHashCache"/>).</summary>
    private string FileHashCachePath => Path.Combine(_projectRoot, "cache", "file-hashes.json");

    private StartupProgressWindow? _startupWindow;
    private (string Text, bool Done) _startupMods = ("Loading your working files…", false);
    private (string Text, bool Done) _startupDatabase = ("Waiting for the mods…", false);
    private bool _startupFinished;

    /// <summary>Updates the startup window's lines (kept for when it appears).</summary>
    private void ReportStartup(string? mods = null, bool modsDone = false, string? database = null, bool databaseDone = false)
    {
        if (mods is not null) _startupMods = (mods, modsDone);
        if (database is not null) _startupDatabase = (database, databaseDone);
        _startupWindow?.ShowMods(_startupMods.Text, _startupMods.Done);
        _startupWindow?.ShowDatabase(_startupDatabase.Text, _startupDatabase.Done);
    }

    /// <summary>Shows the startup window only when loading takes long enough to notice.</summary>
    private async Task ShowStartupWindowWhenSlowAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        if (_startupFinished || !IsVisible || WindowState == WindowState.Minimized) return;
        _startupWindow = new StartupProgressWindow { Owner = this, ShowActivated = false };
        _startupWindow.Closed += (_, _) => _startupWindow = null;
        ReportStartup();
        _startupWindow.Show();
    }

    private void FinishStartup(Stopwatch watch)
    {
        _startupFinished = true;
        _startupWindow?.Close();
        Log($"Startup loading took {watch.Elapsed.TotalSeconds:N1}s.");
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var startup = Stopwatch.StartNew();
        _ = ShowStartupWindowWhenSlowAsync();
        ArkiveModEditSnapshot.LoadHashCache(FileHashCachePath);
        if (_restoredSettings.CheckForUpdatesOnStartup)
            _ = CheckApplicationUpdatesAsync(interactive: false);
        _ = CleanUpUpdateDownloadsAsync();
        _ = CleanUpViewerCacheAsync();
        Log(
            $"Workspace restoration started: root={_libraryRootPath ?? "none"}, " +
            $"savedMetadata={_restoredSettings.GameMetadataPath ?? "default"}, " +
            $"query={LibrarySearchTextBox.Text}, " +
            $"scope={(LibrarySearchScopeComboBox.SelectedItem as ComboBoxItem)?.Tag ?? "InGameName"}, " +
            $"category={(LibraryKindComboBox.SelectedItem as ComboBoxItem)?.Tag ?? "all"}, " +
            $"archetype={(LibraryArchetypeComboBox.SelectedItem as ComboBoxItem)?.Tag ?? "all"}.");
        await RefreshExportWorkspaceAsync("workspace restore");
        await RefreshBaselineWorkspaceAsync("workspace restore");
        await RefreshSkeletalMeshEditableWorkspaceAsync("workspace restore");
        await RefreshStaticMeshWorkspaceAsync("workspace restore");
        await RefreshMaterialProjectsAsync("workspace restore");
        ReportStartup("Reading the mod library…");
        await InitializeArkiveModLibraryAsync();
        ReportStartup("Checking which mods are in the game…");
        await RefreshGuidedTextureModsAsync("workspace restore");
        ReportStartup("Loading drafts and mod sets…");
        await RefreshModAuthoringAsync();
        // The conflict audit is only shown in its advanced view, which runs it when opened.
        await RefreshClientUpdateAuditAsync("workspace restore");
        await RefreshModSetsAsync("workspace restore");
        await RefreshModifiedMeshUpksAsync("workspace restore");
        ReportStartup($"{_arkiveModLibraryManifest.Mods.Count:N0} mod(s), " +
                      $"{_arkiveModLibraryManifest.Mods.Count(mod => mod.Enabled):N0} enabled.", modsDone: true,
            database: "Loading the game database…");
        Log($"Startup mods loaded in {startup.Elapsed.TotalSeconds:N1}s.");
        var defaultMetadataPath = Path.Combine(
            _projectRoot,
            "staging",
            "lpk",
            "data2-fixed",
            "arkive-asset-metadata.json");
        var metadataPath = !string.IsNullOrWhiteSpace(_restoredSettings.GameMetadataPath) &&
                           File.Exists(_restoredSettings.GameMetadataPath)
            ? _restoredSettings.GameMetadataPath
            : defaultMetadataPath;
        var loadedMetadata = false;
        if (File.Exists(metadataPath))
        {
            await LoadGameMetadataAsync(
                metadataPath,
                showErrors: false,
                loadTrigger: "workspace restore");
            loadedMetadata = _gameMetadataIndex is not null;
            // In-game names only show in the package workspace's advanced views; they reload it when opened.
            if (loadedMetadata) _packageWorkspaceStale = true;
            ReportStartup(database: loadedMetadata ? "Loaded." : "Could not be loaded; see Diagnostics.", databaseDone: true);
        }
        else
        {
            ReportStartup(database: "Not set up yet.", databaseDone: true);
            Log($"Workspace metadata was not restored because the candidate file is unavailable: {metadataPath}");
        }

        await RefreshIndexHealthAsync();
        await CleanupGuidedPreviewCacheAsync(TimeSpan.FromHours(12), interactive: false);
        if (!loadedMetadata &&
            _libraryRootPath is not null &&
            (!string.IsNullOrWhiteSpace(LibrarySearchTextBox.Text) ||
             !string.IsNullOrWhiteSpace(
                 (LibraryKindComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()) ||
             !string.IsNullOrWhiteSpace(
                 (LibraryArchetypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString())))
        {
            await ApplyLibraryFilterAsync("workspace restore");
        }

        FinishStartup(startup);
        Log(
            $"Workspace restoration completed: root={_libraryRootPath ?? "none"}, " +
            $"metadataLoaded={_gameMetadataIndex is not null}, tab={MainTabControl.SelectedIndex}.");
        _ = TrimPackageBuildsAsync("startup");
        _ = Task.Run(() => ArkiveModEditSnapshot.SaveHashCache(FileHashCachePath));
        _ = NoticeGameUpdateAsync();
        if (Application.Current is App { StartupModFiles.Count: > 0 } app)
            await ImportModFilesAsync(app.StartupModFiles);
    }

    private void ApplyRestoredSettings(ArkiveSettings settings)
    {
        if (double.IsFinite(settings.WindowWidth) && settings.WindowWidth >= MinWidth)
        {
            Width = settings.WindowWidth;
        }

        if (double.IsFinite(settings.WindowHeight) && settings.WindowHeight >= MinHeight)
        {
            Height = settings.WindowHeight;
        }

        if (settings.WindowLeft is { } left &&
            settings.WindowTop is { } top &&
            double.IsFinite(left) &&
            double.IsFinite(top) &&
            left + Width >= SystemParameters.VirtualScreenLeft &&
            left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            top + Height >= SystemParameters.VirtualScreenTop &&
            top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        _suppressLibrarySearchOptionRefresh = true;
        try
        {
            SelectComboBoxTag(
                LibraryWorkersComboBox,
                settings.LibraryWorkers.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SelectComboBoxTag(LibrarySearchScopeComboBox, settings.LibrarySearchScope);
            SelectComboBoxTag(LibraryKindComboBox, settings.LibraryCategory);
            SelectComboBoxTag(LibraryArchetypeComboBox, settings.LibraryArchetype);
            LibrarySearchTextBox.Text = settings.LibrarySearchText;
        }
        finally
        {
            _suppressLibrarySearchOptionRefresh = false;
        }

        if (!string.IsNullOrWhiteSpace(settings.LibraryRootPath) &&
            Directory.Exists(settings.LibraryRootPath))
        {
            ConfigureLibraryRoot(settings.LibraryRootPath, logSelection: false);
        }
        else if (!string.IsNullOrWhiteSpace(settings.LibraryRootPath))
        {
            LibraryRootTextBox.Text = $"Previous folder unavailable: {settings.LibraryRootPath}";
        }

        if (!string.IsNullOrWhiteSpace(settings.LibraryScratchPath) &&
            Directory.Exists(settings.LibraryScratchPath) &&
            (_libraryRootPath is null ||
             IsSameVolume(_libraryRootPath, settings.LibraryScratchPath)))
        {
            _libraryScratchPath = Path.GetFullPath(settings.LibraryScratchPath);
            LibraryScratchTextBox.Text = $"Fast scratch: {_libraryScratchPath}";
        }

        RestoreWorkflowLayout(settings);
        var ratio = Math.Clamp(settings.ResultsPaneRatio, 0.25, 0.9);
        _lastValidationPaneRatio = 1 - ratio;
        SetValidationPaneVisible(_showValidationPane, updatePreference: false);
        MainTabControl.SelectedItem = ModifiedUpksTab;

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void ConfigureLibraryRoot(string path, bool logSelection)
    {
        _libraryRootPath = Path.GetFullPath(path);
        if (!string.IsNullOrEmpty(_libraryScratchPath) &&
            !IsSameVolume(_libraryRootPath, _libraryScratchPath))
        {
            _libraryScratchPath = null;
            LibraryScratchTextBox.Text = "Fast scratch: disabled (different drive)";
        }

        LibraryRootTextBox.Text = _libraryRootPath;
        LibraryBuildButton.IsEnabled = _umodelBridge is not null;
        UpdateGameAssetLinkActionState();
        LibraryGrid.ItemsSource = Array.Empty<LibraryAssetRow>();
        LibraryCountText.Text = "Existing index ready; enter a search or build/update";
        if (CreateClientSnapshotButton is not null)
        {
            UpdateBaselineSelectionState();
        }
        if (logSelection)
        {
            Log($"Library root selected: {_libraryRootPath}");
        }
    }

    private static void SelectComboBoxTag(ComboBox comboBox, string? tag)
    {
        for (var index = 0; index < comboBox.Items.Count; index++)
        {
            if (comboBox.Items[index] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = index;
                return;
            }
        }
    }

    private async void OpenPackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a UE3 package",
            Filter = "Unreal packages (*.upk)|*.upk|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await OpenPackageAsync(dialog.FileName, "file dialog");
    }

    private async Task OpenPackageAsync(string packagePath, string trigger)
    {
        var fullPackagePath = Path.GetFullPath(packagePath);
        Log($"Package open started: trigger={trigger}, package={fullPackagePath}.");
        DisplayPackageAlias(fullPackagePath);

        _usingUmodelCatalogue = false;
        SetActionsEnabled(previewEnabled: false, exportEnabled: false);
        _assets = [];
        ApplyAssetFilter();

        try
        {
            var summary = _reader.Read(fullPackagePath);
            _currentPackagePath = summary.FilePath;
            Log(
                $"Native summary parsed: version={summary.FileVersion}/{summary.LicenseeVersion}, " +
                $"names={summary.NameCount}, imports={summary.ImportCount}, exports={summary.ExportCount}, " +
                $"chunks={summary.CompressedChunks.Count}, compression=0x{summary.CompressionFlags:X8}.");
            Display(summary);
            await LoadCatalogueAsync(summary);
        }
        catch (PackageFormatException exception) when (_umodelBridge is not null)
        {
            _currentPackagePath = fullPackagePath;
            Log($"Native summary rejected; using direct UModel fallback: {exception.Message}");
            DisplayNativeSummaryUnavailable(_currentPackagePath, exception.Message);
            await LoadUmodelCatalogueAsync(_currentPackagePath, exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            _currentPackagePath = null;
            _usingUmodelCatalogue = false;
            Log($"Package open failed: {exception}");
            StatusText.Text = "Package could not be loaded.";
            MessageBox.Show(
                this,
                exception.Message,
                "Arkive package error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task LoadCatalogueAsync(PackageSummary summary)
    {
        try
        {
            var catalog = _catalogReader.Read(summary);
            _assets = catalog.Assets
                .Select(asset => new AssetRow(
                    asset.ExportIndex,
                    asset.Kind,
                    asset.ClassName,
                    asset.ObjectPath,
                    asset.SerialSize,
                    asset.SerialOffset,
                    FindGameMetadata(summary.FilePath, asset.ObjectName),
                    ExternalAsset: null))
                .ToArray();
            ApplyAssetFilter();
            StatusText.Text =
                $"Native read-only catalogue loaded: {catalog.Assets.Count:N0} exports, " +
                $"{catalog.Imports.Count:N0} imports, {catalog.Names.Count:N0} names.";
            LogAssetSummary("Native catalogue", _assets);
            return;
        }
        catch (Exception exception) when (
            exception is PackageAccessException or PackageFormatException)
        {
            Log($"Native catalogue unavailable: {exception.Message}");
            if (_umodelBridge is null)
            {
                _assets = [];
                ApplyAssetFilter();
                StatusText.Text = exception.Message;
                return;
            }
        }

        await LoadUmodelCatalogueAsync(summary.FilePath);
    }

    private async Task LoadUmodelCatalogueAsync(string packagePath, string? nativeDiagnostic = null)
    {
        StatusText.Text = nativeDiagnostic is null
            ? "Loading protected asset catalogue through the Lost Ark UModel backend..."
            : "Native summary was not recognized; asking the Lost Ark UModel backend directly...";
        try
        {
            var result = await _umodelBridge!.ListAsync(packagePath);
            Log(
                $"UModel list completed in {result.Duration.TotalSeconds:N3}s with " +
                $"{result.Assets.Count} parsed export rows.");
            foreach (var diagnostic in result.Diagnostics)
            {
                Log($"UModel: {diagnostic}");
            }
            var packageName = LostArkPackageName.GetDisplayName(packagePath);
            _assets = result.Assets
                .Select(asset => new AssetRow(
                    asset.ExportIndex,
                    asset.Kind,
                    asset.DisplayClassName,
                    $"{packageName}.{asset.ObjectName}",
                    asset.SerialSize,
                    asset.SerialOffset,
                    FindGameMetadata(packagePath, asset.ObjectName),
                    asset))
                .ToArray();
            _usingUmodelCatalogue = true;
            ApplyAssetFilter();
            LogAssetSummary("UModel catalogue", _assets);
            var renderableCount = _assets.Count(asset => asset.ExternalAsset?.IsExportable == true);
            StatusText.Text = renderableCount == 0
                ? $"UModel listed {_assets.Count:N0} exports, but none are textures, meshes, or materials. See Diagnostics."
                : $"UModel catalogue loaded: {_assets.Count:N0} exports, {renderableCount:N0} exportable, in " +
                  $"{result.Duration.TotalSeconds:N1} seconds.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _assets = [];
            _usingUmodelCatalogue = false;
            Log($"UModel catalogue failed: {exception}");
            ApplyAssetFilter();
            var isTextureCache =
                Path.GetFileName(packagePath).StartsWith("LOD9", StringComparison.OrdinalIgnoreCase) &&
                exception.Message.Contains("Wrong package tag", StringComparison.OrdinalIgnoreCase);
            var diagnostic = isTextureCache
                ? "This LOD9 file is Lost Ark's external texture cache (TFC), not a standalone UPK " +
                  "catalogue. UModel may use it while resolving effect textures, but cannot open it as a package."
                : $"UModel could not catalogue this package: {exception.Message}";
            if (isTextureCache)
            {
                ProfileText.Text = "External texture cache (TFC) - not a standalone UPK";
                Log($"Recognized texture-cache container: {packagePath}");
            }

            StatusText.Text = diagnostic;
            SetValidationIssues(
            [
                new ValidationIssue(
                    isTextureCache ? ValidationSeverity.Information : ValidationSeverity.Error,
                    isTextureCache ? "TFC001" : "EXT001",
                    diagnostic),
            ]);
        }
    }

    private void AssetFilterTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyAssetFilter();

    private void ClearAssetFilterButton_Click(object sender, RoutedEventArgs e)
    {
        AssetFilterTextBox.Clear();
        AssetFilterTextBox.Focus();
    }

    private void AssetKindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyAssetFilter();

    private void ApplyAssetFilter()
    {
        if (AssetsGrid is null || AssetFilterTextBox is null || AssetKindComboBox is null || AssetCountText is null)
        {
            return;
        }

        var filter = AssetFilterTextBox.Text.Trim();
        var category = (AssetKindComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var physicalPackageName = _currentPackagePath is null
            ? string.Empty
            : Path.GetFileNameWithoutExtension(_currentPackagePath);
        var packageAlias = _currentPackagePath is null
            ? string.Empty
            : LostArkPackageName.GetDisplayName(_currentPackagePath);
        var visible = _assets
            .Where(asset =>
                AssetSearch.Matches(
                    filter,
                    asset.ObjectPath,
                    asset.ClassName,
                    asset.Kind.ToString(),
                    asset.ExportIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    physicalPackageName,
                    packageAlias,
                    GameAssetMetadataIndex.BuildSearchText(asset.GameMetadata)) &&
                (string.IsNullOrEmpty(category) ||
                 (string.Equals(category, "Exportable", StringComparison.Ordinal) &&
                  asset.Kind != AssetKind.Object) ||
                 (string.Equals(category, "MaterialInstanceConstant", StringComparison.Ordinal) &&
                  asset.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)) ||
                 string.Equals(asset.Kind.ToString(), category, StringComparison.Ordinal)))
            .ToArray();

        AssetsGrid.ItemsSource = visible;
        var textureCount = _assets.Count(asset => asset.Kind == AssetKind.Texture);
        var staticMeshCount = _assets.Count(asset => asset.Kind == AssetKind.StaticMesh);
        var skeletalMeshCount = _assets.Count(asset => asset.Kind == AssetKind.SkeletalMesh);
        var materialCount = _assets.Count(asset => asset.Kind == AssetKind.Material);
        AssetCountText.Text =
            $"{visible.Length:N0}/{_assets.Count:N0} | Textures {textureCount:N0} | " +
            $"Static {staticMeshCount:N0} | Skeletal {skeletalMeshCount:N0} | Materials {materialCount:N0}";
        SetActionsEnabled(_usingUmodelCatalogue, exportEnabled: false);
    }

    private async void ChooseLibraryFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the Lost Ark Packages folder to catalogue",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ConfigureLibraryRoot(dialog.FolderName, logSelection: true);
        TrySaveSettings("library root changed");
        await RefreshIndexHealthAsync();
    }

    private void ChooseLibraryScratchButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose an Arkive fast-scratch folder on the same drive as Packages",
            Multiselect = false,
            InitialDirectory = _libraryRootPath is null
                ? Environment.CurrentDirectory
                : Path.GetPathRoot(_libraryRootPath),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var selected = Path.GetFullPath(dialog.FolderName);
        if (_libraryRootPath is not null && !IsSameVolume(_libraryRootPath, selected))
        {
            MessageBox.Show(
                this,
                "Fast scratch must be on the same drive as the Packages folder so Arkive can create no-copy hard links.",
                "Arkive fast scratch",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _libraryScratchPath = selected;
        LibraryScratchTextBox.Text = $"Fast scratch: {_libraryScratchPath}";
        Log($"Library fast scratch selected: {_libraryScratchPath}");
        TrySaveSettings("fast scratch changed");
    }

    private void DisableLibraryScratchButton_Click(object sender, RoutedEventArgs e)
    {
        _libraryScratchPath = null;
        LibraryScratchTextBox.Text = "Fast scratch: disabled";
        Log("Library fast scratch disabled.");
        TrySaveSettings("fast scratch disabled");
    }

    private static bool IsSameVolume(string firstPath, string secondPath) =>
        string.Equals(
            Path.GetPathRoot(Path.GetFullPath(firstPath)),
            Path.GetPathRoot(Path.GetFullPath(secondPath)),
            StringComparison.OrdinalIgnoreCase);

    private async void LoadGameMetadataButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load Arkive game database mapping",
            Filter = "Arkive asset metadata (arkive-asset-metadata.json)|arkive-asset-metadata.json|JSON files (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            await LoadGameMetadataAsync(dialog.FileName, showErrors: true);
        }
    }

    /// <summary>
    /// Reads the interface movies' names for their textures ("health bar") when the game's interface packages changed
    /// since they were last read. Takes a few seconds; failures only reach the log.
    /// </summary>
    private async Task RefreshInterfaceNamesAsync(string metadataPath)
    {
        var root = _libraryRootPath;
        if (root is null || !Directory.Exists(root)) return;
        try
        {
            if (await Task.Run(() => InterfaceNameSidecar.IsCurrent(metadataPath, root))) return;
            GameMetadataText.Text = "Game DB mapping: reading the interface's names...";
            var progress = new Progress<InterfaceNameProgress>(value =>
                GameMetadataText.Text = $"Game DB mapping: reading the interface's names ({value.Completed:N0}/{value.Total:N0})...");
            var watch = Stopwatch.StartNew();
            var document = await Task.Run(() => InterfaceNameSidecar.BuildAsync(metadataPath, root, _protectionProvider, progress));
            Log($"Interface names read: packages={document.Packages.Count}, names={document.Associations.Count}, " +
                $"seconds={watch.Elapsed.TotalSeconds:N1}, path={InterfaceNameSidecar.GetDefaultPath(metadataPath)}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Interface names were not read: {exception.Message}");
        }
    }

    /// <summary>
    /// Reads which skill starts each effect from the skills' action files when data3.lpk changed since they were last
    /// read. Takes a few seconds; failures only reach the log.
    /// </summary>
    private async Task RefreshSkillEffectNamesAsync(string metadataPath)
    {
        var root = _libraryRootPath;
        if (root is null || !Directory.Exists(root)) return;
        try
        {
            if (await Task.Run(() => Arkive.Core.Skills.SkillEffectNameSidecar.IsCurrent(metadataPath, root))) return;
            GameMetadataText.Text = "Game DB mapping: reading which skills use each effect...";
            var watch = Stopwatch.StartNew();
            var document = await Task.Run(() => Arkive.Core.Skills.SkillEffectNameSidecar.BuildAsync(metadataPath, root));
            Log($"Skill effect names read: names={document?.Associations.Count ?? 0}, seconds={watch.Elapsed.TotalSeconds:N1}, " +
                $"path={Arkive.Core.Skills.SkillEffectNameSidecar.GetDefaultPath(metadataPath)}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                              System.Text.Json.JsonException)
        {
            Log($"Skill effect names were not read: {exception.Message}");
        }
    }

    private async Task LoadGameMetadataAsync(
        string path,
        bool showErrors,
        string loadTrigger = "user")
    {
        GameMetadataText.Text = "Game DB mapping: loading...";
        try
        {
            await RefreshInterfaceNamesAsync(path);
            await RefreshSkillEffectNamesAsync(path);
            var index = await Task.Run(() => GameAssetMetadataIndex.Load(path));
            _gameMetadataIndex = index;
            _gameMetadataPath = index.SourcePath;
            PopulateLibraryArchetypeFilter(index);
            GameMetadataText.Text =
                $"Game DB mapping: {index.AssociationCount:N0} relationships | " +
                $"verified {index.VerifiedDependencyCount:N0} | " +
                $"{index.Archetypes.Count:N0} class types | {index.EntityCount:N0} entities | " +
                $"{index.CustomizationOptionCount:N0} customization options";
            // Zone names only serve map searches, so reading them never holds up loading.
            _ = RefreshZoneNamesAsync(path);
            RefreshEntityBrowserEntities(loadTrigger);
            RefreshOpenPackageMetadata();
            // At startup the restored Library search runs on its own (minutes when its routing index is rebuilt).
            if (loadTrigger == "workspace restore") _ = LoadLibraryDocumentsAsync(loadTrigger);
            else await LoadLibraryDocumentsAsync(loadTrigger);
            StatusText.Text =
                $"Loaded {index.AssociationCount:N0} searchable in-game-name relationships.";
            Log(
                $"Game DB mapping loaded: path={index.SourcePath}, language={index.Language}, " +
                $"associations={index.AssociationCount}, entities={index.EntityCount}, " +
                $"deterministicItems={index.DeterministicItemCount}, " +
                $"archetypes={index.Archetypes.Count}, " +
                $"verifiedDependencies={index.VerifiedDependencyCount}, " +
                $"dependencySidecar={index.DependencySidecarPath}, " +
                $"customizations={index.CustomizationOptionCount}, databaseRoot={index.SourceDatabaseRoot}.");
            UpdateGameAssetLinkActionState();
            TrySaveSettings("game metadata loaded");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or
                System.Text.Json.JsonException)
        {
            GameMetadataText.Text = "Game DB mapping: load failed";
            Log($"Game DB mapping load failed: {exception}");
            if (showErrors)
            {
                MessageBox.Show(
                    this,
                    exception.Message,
                    "Arkive game DB mapping error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    private void PopulateLibraryArchetypeFilter(GameAssetMetadataIndex index)
    {
        var selected =
            (LibraryArchetypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(selected))
        {
            selected = _restoredSettings.LibraryArchetype;
        }

        _suppressLibrarySearchOptionRefresh = true;
        try
        {
            LibraryArchetypeComboBox.Items.Clear();
            LibraryArchetypeComboBox.Items.Add(new ComboBoxItem
            {
                Content = "All class types",
                Tag = string.Empty,
            });
            foreach (var archetype in ClassTypesByLabel(index))
            {
                LibraryArchetypeComboBox.Items.Add(new ComboBoxItem
                {
                    Content = GameClassNames.Label(archetype),
                    Tag = archetype,
                });
            }

            LibraryArchetypeComboBox.SelectedIndex = 0;
            if (!string.IsNullOrWhiteSpace(selected))
            {
                SelectComboBoxTag(LibraryArchetypeComboBox, selected);
            }
        }
        finally
        {
            _suppressLibrarySearchOptionRefresh = false;
        }

        if (WorkshopArchetypeComboBox is not null)
        {
            WorkshopArchetypeComboBox.Items.Clear();
            WorkshopArchetypeComboBox.Items.Add(new ComboBoxItem
            {
                Content = "All class types",
                Tag = string.Empty,
            });
            foreach (var archetype in ClassTypesByLabel(index))
                WorkshopArchetypeComboBox.Items.Add(new ComboBoxItem
                    { Content = GameClassNames.Label(archetype), Tag = archetype });
            WorkshopArchetypeComboBox.SelectedIndex = 0;
        }
    }

    // Listed by the name shown, which leads with the localised class name; the database name stays the tag.
    private static IEnumerable<string> ClassTypesByLabel(GameAssetMetadataIndex index) =>
        index.Archetypes.OrderBy(GameClassNames.Label, StringComparer.CurrentCultureIgnoreCase);

    private async void GameAssetLinksButton_Click(object sender, RoutedEventArgs e)
    {
        if (_databaseBuildCancellation is not null || _assetLinkCancellation is not null) return;
        if (_umodelBridge is null || _gameMetadataIndex is null ||
            string.IsNullOrWhiteSpace(_gameMetadataPath) ||
            string.IsNullOrWhiteSpace(_libraryRootPath))
        {
            MessageBox.Show(
                this,
                "Load the game DB mapping and select an indexed Packages folder first.",
                "Arkive verified asset links",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var query = LibrarySearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query) && MessageBox.Show(
                this,
                "The search box is empty. Arkive will inspect every recognized avatar item, character, NPC, vehicle, and prop model. " +
                "This is resumable, but the first complete sweep can take a long time. Continue?",
                "Build all verified item links",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _assetLinkCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _assetLinkCancellation = cancellation;
        UpdateGameAssetLinkActionState();
        GameAssetLinksCancelButton.IsEnabled = true;
        LibraryProgressBar.IsIndeterminate = false;
        LibraryProgressBar.Minimum = 0;
        LibraryProgressBar.Maximum = 1;
        LibraryProgressBar.Value = 0;
        var sidecarPath = GameAssetDependencySidecar.GetDefaultPath(_gameMetadataPath);
        var scratch = Path.Combine(_projectRoot, "cache", "dependency-scratch");
        var workers = Math.Clamp(
            int.TryParse(
                (LibraryWorkersComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                out var selectedWorkers)
                ? selectedWorkers
                : 2,
            1,
            4);
        Log(
            $"Verified asset-link build started: query={query}, metadata={_gameMetadataPath}, " +
            $"root={_libraryRootPath}, sidecar={sidecarPath}, scratch={scratch}, workers={workers}.");
        var progress = new Progress<GameAssetDependencyProgress>(value =>
        {
            LibraryProgressBar.Maximum = Math.Max(1, value.Total);
            LibraryProgressBar.Value = Math.Max(LibraryProgressBar.Value, value.Completed);
            GameMetadataText.Text =
                $"Verifying asset links: {value.Completed:N0}/{value.Total:N0} | " +
                $"cached {value.Cached:N0} | failed {value.Failed:N0}";
            StatusText.Text = $"Tracing mesh dependencies: {value.CurrentAsset}";
        });

        try
        {
            var resolver = new GameAssetDependencyResolver(_packageIndexStore, _umodelBridge);
            var report = await resolver.BuildAsync(
                _gameMetadataPath,
                _libraryRootPath,
                sidecarPath,
                scratch,
                string.IsNullOrWhiteSpace(query) ? null : query,
                workers,
                progress,
                cancellation.Token);
            Log(
                $"Verified asset-link build completed in {report.Duration.TotalSeconds:N3}s: " +
                $"query={report.Query ?? "all"}, seeds={report.SeedAssociationCount}, " +
                $"sources={report.SourceAssetCount}, probed={report.ProbedAssetCount}, " +
                $"cached={report.CachedAssetCount}, failed={report.FailedAssetCount}, " +
                $"relationships={report.DependencyAssociationCount}, output={report.OutputPath}.");
            await LoadGameMetadataAsync(
                _gameMetadataPath,
                showErrors: true,
                loadTrigger: "verified asset-link build");
            await ApplyLibraryFilterAsync("verified asset-link build");
            StatusText.Text = report.FailedAssetCount == 0
                ? $"Verified {report.DependencyAssociationCount:N0} item-to-asset relationships."
                : $"Verified links built with {report.FailedAssetCount:N0} unresolved source meshes; see the log.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText.Text = "Verified asset-link build cancelled; the previous sidecar remains usable.";
            Log("Verified asset-link build cancelled; the previous committed sidecar was preserved.");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or
                UmodelBridgeException or System.Text.Json.JsonException)
        {
            StatusText.Text = "Verified asset-link build failed.";
            Log($"Verified asset-link build failed: {exception}");
            MessageBox.Show(
                this,
                exception.Message,
                "Arkive verified asset-link error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_assetLinkCancellation, cancellation))
            {
                _assetLinkCancellation = null;
            }

            cancellation.Dispose();
            GameAssetLinksCancelButton.IsEnabled = false;
            LibraryProgressBar.IsIndeterminate = false;
            UpdateGameAssetLinkActionState();
        }
    }

    private void GameAssetLinksCancelButton_Click(object sender, RoutedEventArgs e)
    {
        GameAssetLinksCancelButton.IsEnabled = false;
        _assetLinkCancellation?.Cancel();
        StatusText.Text = "Stopping verified asset-link inspection after active UModel processes finish...";
        Log("Verified asset-link cancellation requested.");
    }

    private void UpdateGameAssetLinkActionState()
    {
        if (GameAssetLinksButton is null)
        {
            return;
        }

        GameAssetLinksButton.IsEnabled =
            _databaseBuildCancellation is null &&
            _assetLinkCancellation is null &&
            _umodelBridge is not null &&
            _gameMetadataIndex is not null &&
            !string.IsNullOrWhiteSpace(_gameMetadataPath) &&
            !string.IsNullOrWhiteSpace(_libraryRootPath);
        BuildGameDatabaseButton.IsEnabled = _databaseBuildCancellation is null && _assetLinkCancellation is null;
        LoadGameMetadataButton.IsEnabled = BuildGameDatabaseButton.IsEnabled;
        GameDatabaseLanguageComboBox.IsEnabled = BuildGameDatabaseButton.IsEnabled;
    }

}
