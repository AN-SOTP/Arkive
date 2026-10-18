using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private async void LibrarySearchButton_Click(object sender, RoutedEventArgs e) =>
        await ApplyLibraryFilterAsync("search button");

    private async void LibrarySearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ApplyLibraryFilterAsync("enter key");
    }

    private async void WorkshopSearchButton_Click(object sender, RoutedEventArgs e) =>
        await SearchWorkshopAsync("search button");

    private async Task SearchWorkshopAsync(string trigger)
    {
        if (_workshopBusy || string.IsNullOrWhiteSpace(_libraryRootPath))
        {
            if (string.IsNullOrWhiteSpace(_libraryRootPath))
                WorkshopStatusText.Text = "Choose or restore the indexed Packages folder in Library first.";
            return;
        }
        var query = WorkshopSearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            WorkshopStatusText.Text = "Enter part of an in-game name or a technical object name.";
            return;
        }
        _workshopBusy = true;
        UpdateWorkshopActionState();
        WorkshopStatusText.Text = $"Searching the cached index for {query}...";
        var category = (WorkshopKindComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var archetype = (WorkshopArchetypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var technical = query.Contains('_') || query.Contains('.') || query.Any(char.IsDigit);
        var scope = technical || _gameMetadataIndex is null
            ? PackageIndexSearchScope.ObjectAndInGameName
            : PackageIndexSearchScope.InGameName;
        try
        {
            var result = await _packageIndexStore.SearchAsync(
                _libraryRootPath, query, category, _gameMetadataIndex, scope, archetype,
                resultLimit: 750);
            // A friendly-name search is routed and fast. If it has no database
            // match, fall back once to technical object names so users do not
            // need to understand search scopes.
            if (result.TotalMatches == 0 && scope == PackageIndexSearchScope.InGameName)
            {
                scope = PackageIndexSearchScope.ObjectName;
                result = await _packageIndexStore.SearchAsync(
                    _libraryRootPath, query, category, _gameMetadataIndex, scope, archetype,
                    resultLimit: 750);
            }
            var ranked = _assetDiscoveryService.Rank(result.Matches, query, scope)
                .Where(item => AssetTypes.Editable(item.Match.Kind, item.Match.ClassName) is not null)
                .ToArray();
            var rows = ranked.Select(item => new LibraryAssetRow(
                item.Match.SourcePath, item.Match.PackageAlias, item.Match.PhysicalPackageName,
                item.Match.PackageAssetCount, item.Match.ExportIndex, item.Match.Kind,
                item.Match.ClassName, item.Match.ObjectName, item.Match.SerialSize,
                item.Match.SerialOffset, item.Match.GameMetadata, item.Relevance,
                item.MatchReason)).ToArray();
            WorkshopSearchResultsGrid.ItemsSource = rows;
            WorkshopSearchResultsGrid.SelectedItem = rows.FirstOrDefault();
            WorkshopStatusText.Text = rows.Length == 0
                ? "No moddable assets matched. Try Suggest names, a shorter name, another class type, or All moddable assets."
                : $"Found {result.TotalMatches:N0} matching asset(s). Select one and choose Add to mod.";
            Log($"Unified workshop search completed: trigger={trigger}, query={query}, " +
                $"scope={scope}, category={category ?? "all"}, archetype={archetype ?? "all"}, " +
                $"matches={result.TotalMatches}, returned={rows.Length}, routed={result.UsedRoutingIndex}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            WorkshopStatusText.Text = "The workshop search failed safely. See the session log.";
            Log($"Unified workshop search failed: trigger={trigger}, query={query}, error={exception}");
        }
        finally
        {
            _workshopBusy = false;
            UpdateWorkshopActionState();
        }
    }

    /// <summary>One entry per moddable asset type, then All moddable assets (the default).</summary>
    private void InitializeWorkshopKinds()
    {
        foreach (var type in AssetTypes.Moddable)
            WorkshopKindComboBox.Items.Add(new ComboBoxItem { Content = type.PluralTitle, Tag = type.SearchCategory });
        WorkshopKindComboBox.Items.Add(new ComboBoxItem { Content = "All moddable assets", Tag = string.Empty });
        WorkshopKindComboBox.SelectedIndex = WorkshopKindComboBox.Items.Count - 1;
    }

    private void WorkshopSuggestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gameMetadataIndex is null)
        {
            WorkshopStatusText.Text = "Load the game DB mapping in Library before requesting in-game-name suggestions.";
            return;
        }
        var archetype = (WorkshopArchetypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var suggestions = _assetDiscoveryService.Suggest(
            _gameMetadataIndex, WorkshopSearchTextBox.Text, archetype, 50);
        WorkshopSuggestionComboBox.ItemsSource = suggestions;
        WorkshopSuggestionsPanel.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkshopSuggestionComboBox.IsDropDownOpen = suggestions.Count > 0;
        WorkshopStatusText.Text = suggestions.Count == 0
            ? "No evidence-backed database name matched. Shorten the search text."
            : $"Choose one of {suggestions.Count:N0} qualified database names, then search.";
        Log($"Unified workshop suggestions built: query={WorkshopSearchTextBox.Text.Trim()}, " +
            $"archetype={archetype ?? "all"}, suggestions={suggestions.Count}.");
    }

    private void WorkshopSuggestionComboBox_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (WorkshopSuggestionComboBox.SelectedItem is not AssetDiscoverySuggestion suggestion) return;
        WorkshopSearchTextBox.Text = suggestion.SearchText;
        if (!string.IsNullOrWhiteSpace(suggestion.Archetype))
            SelectComboBoxTag(WorkshopArchetypeComboBox, suggestion.Archetype);
        WorkshopStatusText.Text = $"Ready to search for {suggestion.QualifiedDisplayName}.";
    }

    private void WorkshopSearchResultsGrid_SelectionChanged(
        object sender, SelectionChangedEventArgs e) => UpdateWorkshopActionState();

    private async void WorkshopSearchResultsGrid_MouseDoubleClick(
        object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left ||
            WorkshopSearchResultsGrid.SelectedItems.Count != 1 ||
            WorkshopSearchResultsGrid.SelectedItem is not LibraryAssetRow) return;
        await BeginWorkshopAssetAsync();
    }

    private async void WorkshopBeginButton_Click(object sender, RoutedEventArgs e) =>
        await BeginWorkshopAssetAsync();

    private async void WorkshopExtractAssetKitButton_Click(object sender, RoutedEventArgs e)
    {
        var rows = WorkshopSearchResultsGrid.SelectedItems.OfType<LibraryAssetRow>().ToArray();
        if (!WorkshopExtractAssetKitButton.IsEnabled || rows.Length == 0) return;
        await RunAuthoringExtractionAsync(async token =>
        {
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                await AddCompleteAuthoringKitAsync(row, token);
            }
            if (rows.Length > 1) AuthoringStatusText.Text = $"Added {rows.Length:N0} mesh asset kits. Linked textures/materials are excluded until you tick Include.";
        });
    }

    private async Task BeginWorkshopAssetAsync()
    {
        var rows = WorkshopSearchResultsGrid.SelectedItems.OfType<LibraryAssetRow>().ToArray();
        if (!WorkshopBeginButton.IsEnabled || rows.Length == 0) return;
        await RunAuthoringExtractionAsync(async token =>
        {
            var session = await CreateAuthoringAddSessionAsync(token);
            for (var index = 0; index < rows.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                ReportAuthoringExtraction($"Preparing {rows[index].ObjectName}", index, rows.Length);
                await AddAuthoringAssetAsync(rows[index], session);
            }
            ReportAuthoringExtraction("Selected assets ready", rows.Length, rows.Length);
            AuthoringStatusText.Text = $"Added {rows.Length:N0} asset(s). Edit their working files, then save the mod. No game files changed.";
        });
    }

    private void WorkshopPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!WorkshopPreviewButton.IsEnabled || _umodelBridge is null || WorkshopSearchResultsGrid.SelectedItem is not LibraryAssetRow row) return;
        if (IsParticleRow(row)) OpenLibraryParticleEditor(row);
        else if (CanViewTexture(row.Kind)) _ = OpenTextureViewerAsync(row);
        else if (!CanPreviewInArkive(row.Kind)) OpenWorkshopRowInUmodel(row);
        else if (OpenModelViewer(row, WorkshopSearchResultsGrid))
            WorkshopStatusText.Text = $"Opened {row.PackageAlias}.{row.ObjectName} in Arkive's model viewer.";
    }

    private void WorkshopUmodelButton_Click(object sender, RoutedEventArgs e)
    {
        if (WorkshopUmodelButton.IsEnabled && WorkshopSearchResultsGrid.SelectedItem is LibraryAssetRow row)
            OpenWorkshopRowInUmodel(row);
    }

    private void OpenWorkshopRowInUmodel(LibraryAssetRow row)
    {
        if (_umodelBridge is null) return;
        try
        {
            _umodelBridge.StartAssetPreview(row.SourcePath, CreateExternalAsset(row));
            WorkshopStatusText.Text = $"Opened {row.PackageAlias}.{row.ObjectName} in UModel.";
            Log($"Unified workshop source preview launched: package={row.SourcePath}, object={row.ObjectName}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           System.ComponentModel.Win32Exception)
        {
            Log($"Unified workshop source preview failed: package={row.SourcePath}, object={row.ObjectName}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive workshop preview",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshWorkshopViews(string? selectProjectId = null)
    {
        if (!_authoringBusy) _ = RefreshModAuthoringAsync();
    }

    private void UpdateWorkshopActionState()
    {
        if (WorkshopBeginButton is null) return;
        var selected = WorkshopSearchResultsGrid.SelectedItems.OfType<LibraryAssetRow>().ToArray();
        var result = selected.Length == 1 ? selected[0] : null;
        var idle = !_workshopBusy && !_authoringBusy && !_modLibraryBusy && !_guidedTextureModInProgress;
        WorkshopBeginButton.IsEnabled = idle && _authoringDraft is not null && selected.Length > 0;
        WorkshopBeginButton.Content = selected.Length > 1 ? $"Add {selected.Length:N0} selected assets" : "Add asset only";
        WorkshopExtractAssetKitButton.Content = selected.Length > 1 ? $"Add {selected.Length:N0} complete mesh kits" : "Add mesh + textures/materials";
        WorkshopExtractAssetKitButton.IsEnabled = idle && _authoringDraft is not null &&
            _skeletalMeshAssetKitService is not null && selected.Length > 0 &&
            selected.All(row => CanReconnoitreSkeletalMesh(row.Kind, row.ClassName));
        WorkshopPreviewButton.IsEnabled = (idle || _authoringExtractionCancellation is not null) &&
            _umodelBridge is not null && result is not null;
        WorkshopUmodelButton.IsEnabled = WorkshopPreviewButton.IsEnabled;
        WorkshopAddHelpText.Text = selected.Length > 1
            ? $"{selected.Length:N0} assets selected. Add selected assets creates a working copy of each. Complete kits require only skeletal meshes. Select one asset to preview it."
            : result?.Kind switch
        {
            AssetKind.SkeletalMesh => "Asset only: editable GLB. Full kit: GLB + linked textures/materials, excluded until you tick Include.",
            AssetKind.StaticMesh => "Asset only creates an editable static-mesh GLB. Complete kits currently apply to skeletal meshes.",
            AssetKind.Texture => "Asset only creates an editable PNG/DDS working copy, bound to this mod. It does not install anything.",
            AssetKind.Material => "Asset only opens an editable material parameter project. It does not install anything.",
            _ => "Asset only creates one working copy. Full kit also prepares the mesh's linked textures and materials.",
        };
        UpdateAuthoringActions();
    }

    private async void LibrarySearchOption_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_suppressLibrarySearchOptionRefresh ||
            !IsLoaded ||
            string.IsNullOrEmpty(_libraryRootPath))
        {
            return;
        }

        await ApplyLibraryFilterAsync("search options changed");
    }

    private async void LibrarySearchOption_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressLibrarySearchOptionRefresh || !IsLoaded ||
            string.IsNullOrEmpty(_libraryRootPath)) return;
        await ApplyLibraryFilterAsync("discovery filter changed");
    }

    private void LibrarySuggestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gameMetadataIndex is null)
        {
            MessageBox.Show(
                this,
                "Load the game DB mapping first. Suggestions are evidence-backed names from that database, not guessed labels.",
                "Arkive asset discovery",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var archetype = (LibraryArchetypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var suggestions = _assetDiscoveryService.Suggest(
            _gameMetadataIndex,
            LibrarySearchTextBox.Text,
            archetype);
        LibrarySuggestionsGrid.ItemsSource = suggestions;
        LibraryDiscoveryExpander.IsExpanded = true;
        LibraryFacetText.Text = suggestions.Count == 0
            ? "No database name begins with every search term. Try a shorter item, character, or NPC name."
            : $"{suggestions.Count:N0} evidence-backed name suggestion(s). Double-click one to search its exact in-game name.";
        Log($"Asset discovery suggestions built: query={LibrarySearchTextBox.Text.Trim()}, " +
            $"archetype={archetype ?? "all"}, suggestions={suggestions.Count}.");
    }

    private async void LibrarySuggestionsGrid_MouseDoubleClick(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left ||
            LibrarySuggestionsGrid.SelectedItem is not AssetDiscoverySuggestion suggestion) return;
        LibrarySearchTextBox.Text = suggestion.SearchText;
        _suppressLibrarySearchOptionRefresh = true;
        try
        {
            SelectComboBoxTag(LibrarySearchScopeComboBox, "InGameName");
            if (!string.IsNullOrWhiteSpace(suggestion.Archetype))
                SelectComboBoxTag(LibraryArchetypeComboBox, suggestion.Archetype);
        }
        finally { _suppressLibrarySearchOptionRefresh = false; }
        Log($"Asset discovery suggestion selected: name={suggestion.DisplayName}, " +
            $"archetype={suggestion.Archetype ?? "all"}, type={suggestion.EntityType}, " +
            $"references={suggestion.LinkedReferenceCount}.");
        await ApplyLibraryFilterAsync("discovery suggestion selected");
    }

    private async void ClearLibrarySearchButton_Click(object sender, RoutedEventArgs e)
    {
        LibrarySearchTextBox.Clear();
        _suppressLibrarySearchOptionRefresh = true;
        try
        {
            LibrarySearchScopeComboBox.SelectedIndex = 0;
            LibraryKindComboBox.SelectedIndex = 0;
            LibraryArchetypeComboBox.SelectedIndex = 0;
            LibrarySortComboBox.SelectedIndex = 0;
            LibraryLinkedOnlyCheckBox.IsChecked = false;
        }
        finally
        {
            _suppressLibrarySearchOptionRefresh = false;
        }

        await ApplyLibraryFilterAsync("search cleared");
        LibrarySearchTextBox.Focus();
    }

    private async Task LoadLibraryDocumentsAsync(string trigger = "cache reload")
    {
        if (string.IsNullOrEmpty(_libraryRootPath))
        {
            return;
        }

        LibraryCountText.Text = "Loading cached index...";
        try
        {
            var result = await ApplyLibraryFilterAsync(trigger);
            if (result.WasCancelled)
            {
                Log($"Library cache refresh cancelled: trigger={trigger}, root={_libraryRootPath}.");
                return;
            }

            Log(
                $"Library cache searched: packages={result.PackageCount}, " +
                $"matches={result.TotalMatches}, failed={result.FailedPackageCount}, " +
                $"trigger={trigger}, root={_libraryRootPath}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Library cache load failed: {exception}");
            LibraryCountText.Text = "Could not load cached index";
        }
    }

    private async Task<PackageIndexSearchResult> ApplyLibraryFilterAsync(string trigger = "user")
    {
        if (LibraryGrid is null ||
            LibrarySearchTextBox is null ||
            LibrarySearchScopeComboBox is null ||
            LibraryKindComboBox is null ||
            LibraryArchetypeComboBox is null)
        {
            return new PackageIndexSearchResult([], 0, 0, 0);
        }

        const int resultLimit = 5_000;
        var query = LibrarySearchTextBox.Text.Trim();
        var category = (LibraryKindComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var archetype =
            (LibraryArchetypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var scopeValue =
            (LibrarySearchScopeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var searchScope = Enum.TryParse<PackageIndexSearchScope>(
            scopeValue,
            ignoreCase: true,
            out var selectedScope)
            ? selectedScope
            : PackageIndexSearchScope.AllFields;
        _librarySearchCancellation?.Cancel();
        _librarySearchCancellation?.Dispose();
        _librarySearchCancellation = null;
        if (string.IsNullOrEmpty(_libraryRootPath))
        {
            LibraryGrid.ItemsSource = Array.Empty<LibraryAssetRow>();
            LibraryCountText.Text = "No index loaded";
            return new PackageIndexSearchResult([], 0, 0, 0);
        }

        if (string.IsNullOrEmpty(query) && string.IsNullOrEmpty(category) &&
            string.IsNullOrEmpty(archetype))
        {
            LibraryGrid.ItemsSource = Array.Empty<LibraryAssetRow>();
            LibraryCountText.Text = "Enter a UPK, object, or in-game name to search";
            return new PackageIndexSearchResult([], 0, 0, 0);
        }

        LibraryCountText.Text = "Searching cached index...";
        var searchWatch = Stopwatch.StartNew();
        var searchCancellation = new CancellationTokenSource();
        _librarySearchCancellation = searchCancellation;
        Log(
            $"Library search started: trigger={trigger}, query={query}, scope={searchScope}, " +
            $"category={category ?? "all"}, archetype={archetype ?? "all"}, " +
            $"workers={PackageIndexStore.RecommendedSearchParallelism}, " +
            $"resultLimit={resultLimit}, root={_libraryRootPath}.");
        PackageIndexSearchResult result;
        try
        {
            var routingProgress = new Progress<PackageSearchRoutingProgress>(progress =>
            {
                // Progress reports arrive later on the UI thread; one from a finished search must not replace its count.
                if (!ReferenceEquals(_librarySearchCancellation, searchCancellation)) return;
                LibraryCountText.Text =
                    $"Preparing fast in-game search index: {progress.CompletedPackages:N0}/" +
                    $"{progress.TotalPackages:N0} packages...";
            });
            result = await _packageIndexStore.SearchAsync(
                _libraryRootPath,
                query,
                category,
                gameMetadata: _gameMetadataIndex,
                searchScope: searchScope,
                archetype: archetype,
                resultLimit: resultLimit,
                routingProgress: routingProgress,
                cancellationToken: searchCancellation.Token);
        }
        catch (OperationCanceledException) when (searchCancellation.IsCancellationRequested)
        {
            searchWatch.Stop();
            Log(
                $"Library search cancelled after {searchWatch.Elapsed.TotalSeconds:N3}s: " +
                $"trigger={trigger}, query={query}, scope={searchScope}, " +
                $"category={category ?? "all"}, archetype={archetype ?? "all"}.");
            return new PackageIndexSearchResult([], 0, 0, 0, WasCancelled: true);
        }
        finally
        {
            if (ReferenceEquals(_librarySearchCancellation, searchCancellation))
            {
                _librarySearchCancellation = null;
            }

            searchCancellation.Dispose();
        }

        // A search replaced by a newer one can still finish; its results must not overwrite the newer ones.
        if (searchCancellation.IsCancellationRequested)
            return result with { WasCancelled = true };

        var ranked = _assetDiscoveryService.Rank(result.Matches, query, searchScope);
        if (LibraryLinkedOnlyCheckBox.IsChecked == true)
            ranked = ranked.Where(item => item.Match.GameMetadata.Count > 0).ToArray();
        var sort = (LibrarySortComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
                   "Relevance";
        ranked = sort switch
        {
            "Package" => ranked.OrderBy(item => item.Match.PackageAlias,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Match.ObjectName, StringComparer.OrdinalIgnoreCase).ToArray(),
            "Object" => ranked.OrderBy(item => item.Match.ObjectName,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Match.PackageAlias, StringComparer.OrdinalIgnoreCase).ToArray(),
            "Size" => ranked.OrderByDescending(item => item.Match.SerialSize)
                .ThenByDescending(item => item.Relevance).ToArray(),
            _ => ranked,
        };
        var rows = ranked.Select(item => new LibraryAssetRow(
            item.Match.SourcePath,
            item.Match.PackageAlias,
            item.Match.PhysicalPackageName,
            item.Match.PackageAssetCount,
            item.Match.ExportIndex,
            item.Match.Kind,
            item.Match.ClassName,
            item.Match.ObjectName,
            item.Match.SerialSize,
            item.Match.SerialOffset,
            item.Match.GameMetadata,
            item.Relevance,
            item.MatchReason)).ToArray();
        LibraryGrid.ItemsSource = rows;
        var facets = _assetDiscoveryService.BuildFacets(ranked.Select(item => item.Match));
        LibraryFacetText.Text = facets.Count == 0
            ? "No result facets are available."
            : string.Join(Environment.NewLine, facets
                .GroupBy(item => item.Group)
                .Select(group => $"{group.Key}: " + string.Join(", ", group.Take(6)
                    .Select(item => $"{item.Value} {item.Count:N0}"))));
        searchWatch.Stop();
        var durationText = $"{searchWatch.Elapsed.TotalSeconds:N1}s";
        LibraryCountText.Text = result.TotalMatches > resultLimit
            ? $"Showing {resultLimit:N0} of {result.TotalMatches:N0} matches | {result.PackageCount:N0} packages scanned | {result.FailedPackageCount:N0} failed | {durationText}"
            : $"{result.TotalMatches:N0} matches | {result.PackageCount:N0} packages scanned | {result.FailedPackageCount:N0} failed | {durationText}";
        Log(
            $"Library search completed in {searchWatch.Elapsed.TotalSeconds:N3}s: " +
            $"trigger={trigger}, query={query}, scope={searchScope}, category={category ?? "all"}, " +
            $"archetype={archetype ?? "all"}, " +
            $"matches={result.TotalMatches}, returned={rows.Length}, packages={result.PackageCount}, " +
            $"sort={sort}, linkedOnly={LibraryLinkedOnlyCheckBox.IsChecked == true}, " +
            $"usedRouting={result.UsedRoutingIndex}, routingBuilt={result.RoutingIndexBuilt}, " +
            $"routedPackages={result.RoutedPackageCount}, " +
            $"routingPreparationSeconds={result.RoutingPreparationSeconds:N3}, " +
            $"routingBytes={(File.Exists(_packageIndexStore.SearchRoutingPath) ? new FileInfo(_packageIndexStore.SearchRoutingPath).Length : 0)}, " +
            $"routingPath={_packageIndexStore.SearchRoutingPath}.");
        UpdateLibraryActionState();
        return result;
    }

    private void LibraryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLibraryActionState();
        if (LibraryGrid.SelectedItem is LibraryAssetRow row)
        {
            Log(
                $"Library result selected: package={row.SourcePath}, packageAssets={row.PackageAssetCount}, " +
                $"index={row.ExportIndex}, category={row.Kind}, class={row.ClassName}, " +
                $"object={row.ObjectName}.");
        }
    }

    private void UpdateLibraryActionState()
    {
        var row = LibraryGrid.SelectedItem as LibraryAssetRow;
        var hasSelection = row is not null;
        var backendAvailable = _umodelBridge is not null;
        var canExport = backendAvailable &&
                        !_exportInProgress &&
                        row is not null &&
                        CreateExternalAsset(row).IsExportable;
        LibraryInspectButton.IsEnabled = backendAvailable && hasSelection && !_exportInProgress;
        LibraryPreviewButton.IsEnabled = row is not null &&
                                         (backendAvailable && (CanPreviewInArkive(row.Kind) || IsParticleRow(row)) || CanViewTexture(row.Kind));
        LibraryBrowsePackageButton.IsEnabled = backendAvailable && hasSelection && !_exportInProgress;
        LibraryExportButton.IsEnabled = canExport;
        LibraryOpenPackageFolderButton.IsEnabled = hasSelection;
        LibraryCopyIdentityButton.IsEnabled = hasSelection;
        LibraryExploreRelationshipsButton.IsEnabled = hasSelection && !_exportInProgress;
        LibraryBaselineButton.IsEnabled = hasSelection && !_baselineOperationInProgress;
        LibraryTextureReconButton.IsEnabled = !_textureReconnaissanceInProgress &&
                                              row is not null &&
                                              CanReconnoitreTexture(row.Kind, row.ClassName);
        LibrarySkeletalMeshReconButton.IsEnabled = !_skeletalMeshReconnaissanceInProgress &&
                                                   row is not null &&
                                                   CanReconnoitreMesh(row.Kind, row.ClassName);
    }

    private void LibraryInspectButton_Click(object sender, RoutedEventArgs e) =>
        InspectLibrarySelection();

    private void LibraryGrid_MouseDoubleClick(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(LibraryGrid, source) is not DataGridRow)
        {
            return;
        }

        if (_umodelBridge is not null && LibraryGrid.SelectedItem is LibraryAssetRow particle && IsParticleRow(particle))
            OpenLibraryParticleEditor(particle);
        else if (_umodelBridge is not null && LibraryGrid.SelectedItem is LibraryAssetRow row && CanPreviewInArkive(row.Kind))
            OpenLibraryModelViewer(row);
        else
            InspectLibrarySelection();
        e.Handled = true;
    }

    private void InspectLibrarySelection()
    {
        if (_umodelBridge is null || LibraryGrid.SelectedItem is not LibraryAssetRow row)
        {
            return;
        }

        try
        {
            var asset = CreateExternalAsset(row);
            _umodelBridge.StartAssetPreview(row.SourcePath, asset);
            StatusText.Text = $"Asked UModel to inspect {row.PackageAlias}.{row.ObjectName}.";
            Log(
                $"Library asset viewer launched: package={row.SourcePath}, " +
                $"class={row.ClassName}, object={row.ObjectName}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Library asset viewer launch failed: {exception}");
            MessageBox.Show(
                this,
                exception.Message,
                "Arkive library preview error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void LibraryBrowsePackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_umodelBridge is null || LibraryGrid.SelectedItem is not LibraryAssetRow row)
        {
            return;
        }

        try
        {
            _umodelBridge.StartPreview(row.SourcePath);
            StatusText.Text = $"Opened {row.PackageAlias} in UModel.";
            Log($"Library package viewer launched: package={row.SourcePath}, selectedObject={row.ObjectName}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Could not launch the package viewer.";
            Log($"Library package viewer launch failed: {exception}");
        }
    }


    private async void LibraryExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_umodelBridge is null || LibraryGrid.SelectedItem is not LibraryAssetRow row)
        {
            return;
        }

        var asset = CreateExternalAsset(row);
        if (!asset.IsExportable)
        {
            StatusText.Text = $"{row.ClassName} is metadata or a scene reference and cannot be exported directly.";
            return;
        }

        await ExportAssetToWorkspaceAsync(
            row.SourcePath,
            row.PackageAlias,
            asset,
            row.GameMetadata,
            allowPackageWideFallback:
                row.PackageAssetCount <= UmodelBridge.DefaultMaxPackageWideFallbackExports,
            trigger: "Library result");
    }

    private void LibraryOpenPackageFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is not LibraryAssetRow row)
        {
            return;
        }

        OpenFolder(Path.GetDirectoryName(row.SourcePath)!, "Library source package folder");
    }

    private void LibraryCopyIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is not LibraryAssetRow row)
        {
            return;
        }

        var identity =
            $"Package alias: {row.PackageAlias}{Environment.NewLine}" +
            $"Physical package: {row.PhysicalPackageName}{Environment.NewLine}" +
            $"Class: {row.ClassName}{Environment.NewLine}" +
            $"Object: {row.ObjectName}{Environment.NewLine}" +
            $"Export index: {row.ExportIndex}{Environment.NewLine}" +
            $"Source: {row.SourcePath}";
        CopyText(identity, "Library asset identity");
    }

    private static UmodelAssetEntry CreateExternalAsset(LibraryAssetRow row) => new(
        row.ExportIndex,
        row.SerialOffset,
        row.SerialSize,
        row.ClassName,
        row.ObjectName);

    private async void LibraryExploreRelationshipsButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is not LibraryAssetRow row)
        {
            return;
        }

        await ExploreRelationshipsAsync(ToPackageIndexMatch(row), "Library result action");
    }

    private async Task ExploreRelationshipsAsync(PackageIndexMatch seed, string trigger)
    {
        if (string.IsNullOrEmpty(_libraryRootPath))
        {
            StatusText.Text = "Choose or restore the indexed Packages folder first.";
            return;
        }

        _relationshipCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _relationshipCancellation = cancellation;
        _relationshipSeed = seed;
        RelationshipsTab.IsSelected = true;
        RelationshipGrid.ItemsSource = Array.Empty<RelationshipAssetRow>();
        RelationshipEntitiesGrid.ItemsSource = Array.Empty<RelationshipEntityRow>();
        RelationshipSeedText.Text =
            $"Seed: {seed.PackageAlias}.{seed.ObjectName} | {seed.Kind} | export {seed.ExportIndex}";
        RelationshipSummaryText.Text = "Scanning the cached package index once...";
        RelationshipCancelButton.IsEnabled = true;
        RelationshipRefreshButton.IsEnabled = false;
        UpdateRelationshipActionState();
        var stopwatch = Stopwatch.StartNew();
        Log(
            $"Relationship exploration started: trigger={trigger}, package={seed.SourcePath}, " +
            $"index={seed.ExportIndex}, category={seed.Kind}, class={seed.ClassName}, " +
            $"object={seed.ObjectName}, metadata={seed.GameMetadata.Count}, root={_libraryRootPath}.");
        try
        {
            var relationshipTask = _packageIndexStore.ExploreRelationshipsAsync(
                _libraryRootPath,
                seed,
                _gameMetadataIndex,
                cancellationToken: cancellation.Token);
            var workspaceTask = _exportWorkspaceStore.LoadAsync(cancellation.Token);
            var result = await relationshipTask;
            var workspace = await workspaceTask;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_relationshipCancellation, cancellation))
            {
                return;
            }
            var exportCounts = workspace.Entries
                .GroupBy(entry => RelationshipExportKey(
                    entry.Manifest.SourcePackagePath,
                    entry.Manifest.ExportIndex))
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var rows = result.Matches.Select(match =>
            {
                exportCounts.TryGetValue(
                    RelationshipExportKey(match.Asset.SourcePath, match.Asset.ExportIndex),
                    out var exportCount);
                return new RelationshipAssetRow(match, exportCount);
            }).ToArray();
            RelationshipGrid.ItemsSource = rows;
            RelationshipEntitiesGrid.ItemsSource = result.EntityAssociations
                .Select(association => new RelationshipEntityRow(association))
                .ToArray();
            var evidenceSummary = string.Join(
                ", ",
                rows.GroupBy(row => row.Evidence)
                    .Select(group => $"{group.Key}: {group.Count():N0}"));
            var shown = result.WasTruncated
                ? $"showing {rows.Length:N0} of {result.TotalCandidates:N0} candidates"
                : $"{rows.Length:N0} candidates";
            RelationshipSummaryText.Text =
                $"{shown} | {result.EntityAssociations.Count:N0} DB fields | " +
                $"{result.PackageCount:N0} packages | {result.FailedPackageCount:N0} failed | " +
                $"{result.Duration.TotalSeconds:N1}s" +
                (string.IsNullOrEmpty(evidenceSummary) ? string.Empty : $" | {evidenceSummary}");
            StatusText.Text =
                $"Relationship exploration found {result.TotalCandidates:N0} candidate asset(s).";
            Log(
                $"Relationship exploration completed in {stopwatch.Elapsed.TotalSeconds:N3}s: " +
                $"trigger={trigger}, seed={seed.PackageAlias}.{seed.ObjectName}, " +
                $"candidates={result.TotalCandidates}, returned={rows.Length}, " +
                $"dbFields={result.EntityAssociations.Count}, packages={result.PackageCount}, " +
                $"failed={result.FailedPackageCount}, truncated={result.WasTruncated}, " +
                $"evidence=[{evidenceSummary}], exportManifests={workspace.Entries.Count}.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(_relationshipCancellation, cancellation))
            {
                RelationshipSummaryText.Text =
                    $"Relationship exploration cancelled after {stopwatch.Elapsed.TotalSeconds:N1}s.";
            }
            Log(
                $"Relationship exploration cancelled after {stopwatch.Elapsed.TotalSeconds:N3}s: " +
                $"trigger={trigger}, seed={seed.PackageAlias}.{seed.ObjectName}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (ReferenceEquals(_relationshipCancellation, cancellation))
            {
                RelationshipSummaryText.Text = "Relationship exploration failed; see Diagnostics.";
                StatusText.Text = "Could not explore the selected asset's relationships.";
            }
            Log($"Relationship exploration failed: trigger={trigger}, seed={seed.SourcePath}, error={exception}");
        }
        finally
        {
            stopwatch.Stop();
            var isCurrent = ReferenceEquals(_relationshipCancellation, cancellation);
            if (isCurrent)
            {
                _relationshipCancellation = null;
            }

            cancellation.Dispose();
            if (isCurrent)
            {
                RelationshipCancelButton.IsEnabled = false;
                RelationshipRefreshButton.IsEnabled = _relationshipSeed is not null;
                UpdateRelationshipActionState();
            }
        }
    }

    private async void RelationshipExploreSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (RelationshipGrid.SelectedItem is RelationshipAssetRow row)
        {
            await ExploreRelationshipsAsync(row.Match.Asset, "Relationship result action");
        }
    }

    private async void RelationshipRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_relationshipSeed is not null)
        {
            await ExploreRelationshipsAsync(_relationshipSeed, "Relationship refresh");
        }
    }

    private void RelationshipCancelButton_Click(object sender, RoutedEventArgs e) =>
        _relationshipCancellation?.Cancel();

    private void RelationshipGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateRelationshipActionState();
        if (RelationshipGrid.SelectedItem is RelationshipAssetRow row)
        {
            Log(
                $"Relationship result selected: evidence={row.Evidence}, confidence={row.Confidence}, " +
                $"package={row.SourcePath}, index={row.ExportIndex}, category={row.Kind}, " +
                $"class={row.ClassName}, object={row.ObjectName}, exports={row.ExportCount}.");
        }
    }

    private void RelationshipGrid_MouseDoubleClick(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(RelationshipGrid, source) is not DataGridRow)
        {
            return;
        }

        InspectRelationshipSelection();
        e.Handled = true;
    }

    private void UpdateRelationshipActionState()
    {
        if (RelationshipGrid is null)
        {
            return;
        }

        var row = RelationshipGrid.SelectedItem as RelationshipAssetRow;
        var selected = row is not null;
        var backend = _umodelBridge is not null && !_exportInProgress;
        RelationshipInspectButton.IsEnabled = backend && selected;
        RelationshipBrowseButton.IsEnabled = backend && selected;
        RelationshipOpenPackageButton.IsEnabled = selected && !_exportInProgress;
        RelationshipExportButton.IsEnabled = backend && row?.ExternalAsset.IsExportable == true;
        RelationshipOpenFolderButton.IsEnabled = selected;
        RelationshipCopyButton.IsEnabled = selected;
        RelationshipExploreSelectedButton.IsEnabled = selected && _relationshipCancellation is null;
        RelationshipBaselineButton.IsEnabled = selected && !_baselineOperationInProgress;
        RelationshipTextureReconButton.IsEnabled = !_textureReconnaissanceInProgress &&
                                                   row is not null &&
                                                   CanReconnoitreTexture(row.Kind, row.ClassName);
    }

    private void RelationshipInspectButton_Click(object sender, RoutedEventArgs e) =>
        InspectRelationshipSelection();

    private void InspectRelationshipSelection()
    {
        if (_umodelBridge is null ||
            RelationshipGrid.SelectedItem is not RelationshipAssetRow row)
        {
            return;
        }

        try
        {
            _umodelBridge.StartAssetPreview(row.SourcePath, row.ExternalAsset);
            StatusText.Text = $"Asked UModel to inspect {row.PackageAlias}.{row.ObjectName}.";
            Log(
                $"Relationship asset viewer launched: evidence={row.Evidence}, " +
                $"package={row.SourcePath}, class={row.ClassName}, object={row.ObjectName}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Could not launch the relationship asset in UModel.";
            Log($"Relationship asset viewer launch failed: {exception}");
        }
    }

    private void RelationshipBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_umodelBridge is null ||
            RelationshipGrid.SelectedItem is not RelationshipAssetRow row)
        {
            return;
        }

        try
        {
            _umodelBridge.StartPreview(row.SourcePath);
            StatusText.Text = $"Opened {row.PackageAlias} in UModel.";
            Log(
                $"Relationship package viewer launched: package={row.SourcePath}, " +
                $"selectedObject={row.ObjectName}, evidence={row.Evidence}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Could not launch the package viewer.";
            Log($"Relationship package viewer launch failed: {exception}");
        }
    }

    private async void RelationshipOpenPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (RelationshipGrid.SelectedItem is not RelationshipAssetRow row)
        {
            return;
        }

        await OpenPackageAsync(row.SourcePath, "Relationship result action");
        MainTabControl.SelectedIndex = 0;
    }

    private async void RelationshipExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_umodelBridge is null ||
            RelationshipGrid.SelectedItem is not RelationshipAssetRow row ||
            !row.ExternalAsset.IsExportable)
        {
            return;
        }

        await ExportAssetToWorkspaceAsync(
            row.SourcePath,
            row.PackageAlias,
            row.ExternalAsset,
            row.Match.Asset.GameMetadata,
            allowPackageWideFallback:
                row.Match.Asset.PackageAssetCount <= UmodelBridge.DefaultMaxPackageWideFallbackExports,
            trigger: $"Relationship result ({row.Evidence})");
    }

    private void RelationshipOpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (RelationshipGrid.SelectedItem is RelationshipAssetRow row)
        {
            OpenFolder(Path.GetDirectoryName(row.SourcePath)!, "relationship source package folder");
        }
    }

    private void RelationshipCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (RelationshipGrid.SelectedItem is not RelationshipAssetRow row)
        {
            return;
        }

        var identity =
            $"Evidence: {row.Evidence}{Environment.NewLine}" +
            $"Confidence: {row.Confidence}{Environment.NewLine}" +
            $"Reason: {row.Reason}{Environment.NewLine}" +
            $"Package alias: {row.PackageAlias}{Environment.NewLine}" +
            $"Physical package: {row.PhysicalPackageName}{Environment.NewLine}" +
            $"Class: {row.ClassName}{Environment.NewLine}" +
            $"Object: {row.ObjectName}{Environment.NewLine}" +
            $"Export index: {row.ExportIndex}{Environment.NewLine}" +
            $"Source: {row.SourcePath}";
        CopyText(identity, "relationship evidence and asset identity");
    }

    private static PackageIndexMatch ToPackageIndexMatch(LibraryAssetRow row) => new(
        row.SourcePath,
        row.PackageAlias,
        row.PhysicalPackageName,
        row.PackageAssetCount,
        row.ExportIndex,
        row.Kind,
        row.ClassName,
        row.ObjectName,
        row.SerialSize,
        row.SerialOffset,
        row.GameMetadata);

    private static string RelationshipExportKey(string sourcePath, int exportIndex) =>
        Path.GetFullPath(sourcePath).ToUpperInvariant() + "\0" +
        exportIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

}
