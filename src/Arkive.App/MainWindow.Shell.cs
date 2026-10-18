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
    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.GetDirectoryName(_sessionLog.FilePath)!,
                UseShellExecute = true,
            });
            Log("Opened the session log folder.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not open log folder: {exception.Message}";
            Log($"Could not open log folder: {exception}");
        }
    }

    private void OpenWorkshopButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedItem = WorkshopTab;
        AuthoringModComboBox.Focus();
        StatusText.Text = "Choose a mod to edit, or open Create mod to start a new one.";
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.Source, MainTabControl)) return;
        UpdateBannerContext();
        // The advanced views list the package workspace, which enabling mods leaves to be reloaded when needed.
        if (_packageWorkspaceStale && MainTabControl.SelectedItem is TabItem tab &&
            tab != LibraryTab && tab != CreateModTab && tab != WorkshopTab && tab != ModifiedUpksTab && tab != BodyProportionsTab &&
            tab != SoundsTab)
            _ = RefreshStalePackageWorkspaceWithNoticeAsync();
    }

    private async Task RefreshStalePackageWorkspaceWithNoticeAsync()
    {
        using var busy = ShowBusy("Loading the package workspace…", "Arkive is reading the mods' working files. Please wait until it finishes.");
        await RefreshStalePackageWorkspaceAsync("advanced view opened");
    }

    private void UpdateBannerContext()
    {
        if (MainTabControl.SelectedItem == LibraryTab)
        {
            PackageAliasText.Text = "Asset Library";
            PathText.Text = "Search filenames and game names; inspect assets in UModel or explore their details.";
        }
        else if (MainTabControl.SelectedItem == CreateModTab)
        {
            PackageAliasText.Text = "Create Mod";
            PathText.Text = "Name a new mod, then add its assets in Edit mods.";
        }
        else if (MainTabControl.SelectedItem == WorkshopTab)
        {
            PackageAliasText.Text = _authoringDraft?.Metadata.Name ?? "Edit Mods";
            PathText.Text = "Edit working assets, save the mod, then review and apply changes.";
        }
        else if (MainTabControl.SelectedItem == ModifiedUpksTab)
        {
            PackageAliasText.Text = "Mod Library";
            PathText.Text = "Enable mods and set priorities; Apply changes updates the game.";
        }
        else if (MainTabControl.SelectedItem == SoundsTab)
        {
            PackageAliasText.Text = "Sounds";
            PathText.Text = "Listen to the game's sounds and replace single clips with your own audio.";
        }
        else if (_currentPackagePath is { } packagePath)
        { DisplayPackageAlias(packagePath); PathText.Text = packagePath; }
    }

    private void PackageActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (PackageActionsButton.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = PackageActionsButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void AdvancedSectionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (AdvancedSectionsButton.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = AdvancedSectionsButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void AdvancedNavigationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: { } tag })
        {
            return;
        }

        var modDestination = tag.ToString() switch
        {
            "PackageProjects" => GuidedPackageProjectsTab,
            "GameSafety" => ClientUpdateSafetyTab,
            "ModDependencies" => ModConflictDependencyTab,
            "ModSets" => ModSetsTab,
            "LegacyBundles" => LegacyMeshBundlesTab,
            _ => null,
        };
        if (modDestination is not null)
        {
            MainTabControl.SelectedItem = ModifiedUpksTab;
            ModifiedUpksInnerTabControl.SelectedItem = modDestination;
            StatusText.Text = $"Advanced section opened: {modDestination.Header}.";
            if (_packageWorkspaceStale && modDestination != ModConflictDependencyTab)
                _ = RefreshStalePackageWorkspaceWithNoticeAsync();
            if (modDestination == ModConflictDependencyTab && _modConflictDependencyReport is null)
                _ = RefreshModConflictDependencyAsync("view opened");
            return;
        }

        var destination = tag.ToString() switch
        {
            "Assets" => AssetsTab,
            "Library" => LibraryTab,
            "Entities" => EntityBrowserTab,
            "CompressedChunks" => CompressedChunksTab,
            "IndexStatus" => IndexStatusTab,
            "Diagnostics" => DiagnosticsTab,
            "TextureRecon" => TextureReconTab,
            "MeshRecon" => SkeletalMeshReconTab,
            "StaticMeshes" => StaticMeshTab,
            "Materials" => MaterialEditorTab,
            "Exports" => ExportsTab,
            "Relationships" => RelationshipsTab,
            "Baselines" => BaselinesTab,
            _ => null,
        };
        if (destination is null)
        {
            return;
        }

        MainTabControl.SelectedItem = destination;
        StatusText.Text = $"Advanced section opened: {destination.Header}.";
        Log($"Advanced navigation opened: {destination.Header}.");
    }

    private void AppearanceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AppearanceSettingsWindow(
            _appearanceTheme,
            _bannerArtworkPreference,
            _compactDensity,
            _showValidationPane,
            _checkForUpdatesOnStartup ?? _restoredSettings.CheckForUpdatesOnStartup,
            () => CheckApplicationUpdatesAsync(interactive: true))
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _appearanceTheme = dialog.SelectedTheme;
        _bannerArtworkPreference = dialog.SelectedBannerArtwork;
        _activeBannerArtwork = BannerArtworkManager.ResolveSettingsSelection(
            _bannerArtworkPreference,
            _activeBannerArtwork);
        _compactDensity = dialog.CompactDensity;
        _checkForUpdatesOnStartup = dialog.CheckForUpdatesOnStartup;
        _effectiveAppearanceTheme = AppearanceManager.Apply(_appearanceTheme, _compactDensity);
        ApplyBannerArtwork(_activeBannerArtwork);
        AppearanceManager.ApplyWindowChrome(this, _effectiveAppearanceTheme);
        UpdateWorkshopActionState();
        SetValidationPaneVisible(dialog.ShowValidationPane, updatePreference: true);
        TrySaveSettings("appearance changed");
        StatusText.Text =
            $"Appearance updated: {_effectiveAppearanceTheme.ToLowerInvariant()} theme, " +
            $"{(_compactDensity ? "compact" : "comfortable")} density.";
        Log(
            $"Appearance updated: requestedTheme={_appearanceTheme}, effectiveTheme={_effectiveAppearanceTheme}, " +
            $"bannerPreference={_bannerArtworkPreference}, banner={_activeBannerArtwork.Id}, " +
            $"density={(_compactDensity ? "compact" : "comfortable")}, " +
            $"validationDrawer={_showValidationPane}.");
    }

    private void ValidationToggleButton_Click(object sender, RoutedEventArgs e) =>
        SetValidationPaneVisible(!_showValidationPane, updatePreference: true);

    private void SetValidationPaneVisible(bool visible, bool updatePreference)
    {
        if (ValidationPanel is null || ValidationSplitter is null)
        {
            _showValidationPane = visible;
            return;
        }

        if (!visible && _showValidationPane)
        {
            var total = ResultsRow.ActualHeight + ValidationRow.ActualHeight;
            if (total > 0 && ValidationRow.ActualHeight > 0)
            {
                _lastValidationPaneRatio = Math.Clamp(ValidationRow.ActualHeight / total, 0.1, 0.6);
            }
        }

        _showValidationPane = visible;
        ValidationPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ValidationSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ValidationSplitterRow.Height = visible ? new GridLength(7) : new GridLength(0);
        if (visible)
        {
            var validationRatio = Math.Clamp(_lastValidationPaneRatio, 0.1, 0.6);
            ResultsRow.Height = new GridLength(1 - validationRatio, GridUnitType.Star);
            ValidationRow.Height = new GridLength(validationRatio, GridUnitType.Star);
        }
        else
        {
            ResultsRow.Height = new GridLength(1, GridUnitType.Star);
            ValidationRow.Height = new GridLength(0);
        }

        ValidationToggleButton.Content = visible ? "Hide validation" : "Validation details";
        if (updatePreference)
        {
            Log($"Technical validation drawer {(visible ? "shown" : "hidden")}.");
        }
    }

    private void SetValidationIssues(IEnumerable<ValidationIssue> issues)
    {
        var materialized = issues.ToArray();
        ValidationGrid.ItemsSource = materialized;
        var errors = materialized.Count(issue => issue.Severity == ValidationSeverity.Error);
        var warnings = materialized.Count(issue => issue.Severity == ValidationSeverity.Warning);
        ValidationToggleButton.Content = _showValidationPane
            ? "Hide validation"
            : materialized.Length == 0
                ? "Validation details"
                : $"Validation  {errors} errors · {warnings} warnings";
    }

    private void LogAssetSummary(string source, IReadOnlyList<AssetRow> assets)
    {
        var categories = string.Join(
            ", ",
            assets
                .GroupBy(asset => asset.Kind)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}={group.Count()}"));
        var classes = string.Join(
            ", ",
            assets
                .GroupBy(asset => asset.ClassName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(30)
                .Select(group => $"{group.Key}={group.Count()}"));
        var exportable = assets.Count(asset => asset.ExternalAsset?.IsExportable == true);

        Log($"{source} summary: total={assets.Count}, exportable={exportable}, categories=[{categories}].");
        Log($"{source} top reported classes: [{classes}].");
        if (assets.Count > 0 && exportable == 0)
        {
            Log(
                "No texture/model/material rows were reported. This package likely contains metadata, " +
                "effects, references, or unsupported object classes; its resources may live in other packages.");
        }
    }

    private void Log(string message)
    {
        // Work running side by side (skill and particle preparation) logs from other threads.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Log(message));
            return;
        }
        var line = _sessionLog.Write(message);
        if (DiagnosticsTextBox is null)
        {
            return;
        }

        DiagnosticsTextBox.AppendText(line + Environment.NewLine);
        DiagnosticsTextBox.ScrollToEnd();
    }

    private void Display(PackageSummary summary)
    {
        PathText.Text = summary.FilePath;
        DisplayPackageAlias(summary.FilePath);
        ProfileText.Text =
            $"{summary.FileVersion}/{summary.LicenseeVersion} - Western profile: {summary.IsWesternLostArkProfile}";
        PhysicalSizeText.Text = $"{summary.PhysicalFileSize:N0} bytes";
        EngineText.Text = $"{summary.EngineVersion} / {summary.CookerVersion}";
        HeaderSizeText.Text = $"{summary.HeaderSize:N0} bytes";
        PackageFlagsText.Text = $"0x{summary.PackageFlags:X8}";
        CompressionText.Text = $"0x{summary.CompressionFlags:X8}";
        NamesText.Text = $"{summary.NameCount:N0} at 0x{summary.NameOffset:X}";
        ImportsText.Text = $"{summary.ImportCount:N0} at 0x{summary.ImportOffset:X}";
        ExportsText.Text = $"{summary.ExportCount:N0} at 0x{summary.ExportOffset:X}";
        GuidText.Text = summary.PackageGuid.ToString();

        ChunksGrid.ItemsSource = summary.CompressedChunks
            .Select((chunk, index) => new ChunkRow(
                index + 1,
                $"0x{chunk.UncompressedOffset:X}",
                $"{chunk.UncompressedSize:N0}",
                $"0x{chunk.CompressedOffset:X}",
                $"{chunk.CompressedSize:N0}",
                $"0x{chunk.LostArkFlags:X8}"))
            .ToArray();
        SetValidationIssues(PackageValidator.Validate(summary));
    }

    private void DisplayPackageAlias(string packagePath)
    {
        var physicalName = Path.GetFileNameWithoutExtension(packagePath);
        var alias = LostArkPackageName.GetDisplayName(packagePath);
        PackageAliasText.Text = alias.Equals(physicalName, StringComparison.OrdinalIgnoreCase)
            ? physicalName
            : alias;
    }

    private void TrySaveSettings(string reason = "state change")
    {
        try
        {
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Math.Max(MinWidth, ActualWidth), Math.Max(MinHeight, ActualHeight))
                : RestoreBounds;
            if (bounds.IsEmpty)
            {
                bounds = new Rect(Left, Top, Width, Height);
            }

            var paneTotal = ResultsRow.ActualHeight + ValidationRow.ActualHeight;
            var paneRatio = !_showValidationPane || paneTotal <= 0
                ? 1 - _lastValidationPaneRatio
                : ResultsRow.ActualHeight / paneTotal;
            var workers = int.TryParse(
                (LibraryWorkersComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                out var selectedWorkers)
                ? selectedWorkers
                : PackageIndexStore.RecommendedMaxParallelism;
            CaptureWorkflowLayout();
            var settings = new ArkiveSettings
            {
                LibraryRootPath = _libraryRootPath,
                LibraryScratchPath = _libraryScratchPath,
                GameMetadataPath = _gameMetadataPath,
                LibraryWorkers = workers,
                LibrarySearchText = LibrarySearchTextBox.Text,
                LibrarySearchScope =
                    (LibrarySearchScopeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
                    "AllFields",
                LibraryCategory =
                    (LibraryKindComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
                    string.Empty,
                LibraryArchetype =
                    (LibraryArchetypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
                    string.Empty,
                SelectedTabIndex = MainTabControl.SelectedIndex,
                ResultsPaneRatio = Math.Clamp(paneRatio, 0.25, 0.9),
                AuthoringSearchPaneRatio = _authoringSearchPaneRatio,
                ModDetailsPaneRatio = _modDetailsPaneRatio,
                LibrarySetupExpanded = LibrarySetupExpander.IsExpanded,
                AuthoringDetailsExpanded = AuthoringMetadataExpander.IsExpanded,
                AuthoringSearchExpanded = AuthoringSearchExpander.IsExpanded,
                WindowWidth = bounds.Width,
                WindowHeight = bounds.Height,
                WindowLeft = bounds.Left,
                WindowTop = bounds.Top,
                WindowMaximized = WindowState == WindowState.Maximized,
                Theme = _appearanceTheme,
                BannerArtwork = _bannerArtworkPreference,
                CompactDensity = _compactDensity,
                ShowValidationPane = _showValidationPane,
                CheckForUpdatesOnStartup = _checkForUpdatesOnStartup ?? _restoredSettings.CheckForUpdatesOnStartup,
                BlenderPath = _blenderPath ?? _restoredSettings.BlenderPath,
            };
            _settingsStore.Save(settings);
            Log(
                $"Settings saved: reason={reason}, file={_settingsStore.FilePath}, " +
                $"root={settings.LibraryRootPath ?? "none"}, " +
                $"scratch={settings.LibraryScratchPath ?? "disabled"}, " +
                $"metadata={settings.GameMetadataPath ?? "none"}, workers={settings.LibraryWorkers}, " +
                $"query={settings.LibrarySearchText}, scope={settings.LibrarySearchScope}, " +
                $"category={settings.LibraryCategory}, archetype={settings.LibraryArchetype}, " +
                $"tab={settings.SelectedTabIndex}, " +
                $"paneRatio={settings.ResultsPaneRatio:N3}, theme={settings.Theme}, " +
                $"bannerPreference={settings.BannerArtwork}, activeBanner={_activeBannerArtwork.Id}, " +
                $"density={(settings.CompactDensity ? "compact" : "comfortable")}, " +
                $"validationDrawer={settings.ShowValidationPane}, " +
                $"window={settings.WindowWidth:N0}x{settings.WindowHeight:N0}, " +
                $"maximized={settings.WindowMaximized}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Settings could not be saved: {exception.Message}");
        }
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_applicationUpdateDownloading || _authoringBusy || _modLibraryBusy || _guidedTextureModInProgress || _modSetBusy || _databaseBuildCancellation is not null)
        {
            e.Cancel = true;
            StatusText.Text = "Finish or cancel the current operation before closing Arkive.";
            base.OnClosing(e);
            return;
        }
        if (!_authoringCloseApproved && _authoringDraft is not null)
        {
            e.Cancel = true;
            base.OnClosing(e);
            try { await PersistAuthoringDraftAsync(); }
            catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
            { Log($"Authoring draft could not be saved on close: {exception.Message}"); }
            _authoringCloseApproved = true;
            Close();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _authoringMetadataTimer.Stop();
        _applicationUpdateCancellation.Cancel();
        _applicationUpdateCancellation.Dispose();
        TrySaveSettings("window closing");
        ArkiveModEditSnapshot.SaveHashCache(FileHashCachePath);
        _libraryIndexCancellation?.Cancel();
        _libraryIndexCancellation?.Dispose();
        _librarySearchCancellation?.Cancel();
        _librarySearchCancellation?.Dispose();
        _artifactInspectionCancellation?.Cancel();
        _artifactInspectionCancellation?.Dispose();
        _relationshipCancellation?.Cancel();
        _relationshipCancellation?.Dispose();
        _entityBrowserCancellation?.Cancel();
        _entityBrowserCancellation?.Dispose();
        _baselineCancellation?.Cancel();
        _baselineCancellation?.Dispose();
        _assetLinkCancellation?.Cancel();
        _assetLinkCancellation?.Dispose();
        _modelPreviewResolutionCancellation?.Cancel();
        _modelPreviewResolutionCancellation?.Dispose();
        _clientUpdateAuditCancellation?.Cancel();
        _clientUpdateAuditCancellation?.Dispose();
        base.OnClosed(e);
    }

}
