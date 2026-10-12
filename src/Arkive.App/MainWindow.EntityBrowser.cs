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
    private void EntityBrowserRefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshEntityBrowserEntities("user refresh");

    private void EntityBrowserCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _entityBrowserCancellation?.Cancel();
        Log("Entity browser asset resolution cancellation requested.");
    }

    private void EntityBrowserFilter_Changed(object sender, RoutedEventArgs e) =>
        ApplyEntityBrowserFilter();

    private void RefreshEntityBrowserEntities(string trigger)
    {
        if (EntityBrowserEntitiesGrid is null)
        {
            return;
        }

        _entityBrowserEntities = _gameMetadataIndex?.Entities ?? [];
        ApplyEntityBrowserFilter();
        var playable = _entityBrowserEntities.Count(entity =>
            entity.Kind == GameEntityKind.PlayableCharacter);
        var npcs = _entityBrowserEntities.Count - playable;
        Log(
            $"Entity browser refreshed: trigger={trigger}, entities={_entityBrowserEntities.Count}, " +
            $"playable={playable}, npcs={npcs}, " +
            $"customizations={_gameMetadataIndex?.CustomizationOptionCount ?? 0}.");
    }

    private void ApplyEntityBrowserFilter()
    {
        if (EntityBrowserEntitiesGrid is null || EntityBrowserSearchTextBox is null ||
            EntityBrowserKindComboBox is null || EntityBrowserSummaryText is null)
        {
            return;
        }

        var previousId = (EntityBrowserEntitiesGrid.SelectedItem as EntityBrowserEntityRow)?.EntityId;
        var query = EntityBrowserSearchTextBox.Text.Trim();
        var kind = (EntityBrowserKindComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        var visible = _entityBrowserEntities
            .Where(entity => MatchesEntityKind(entity, kind) && AssetSearch.Matches(
                query,
                entity.DisplayName,
                entity.NameKey,
                entity.EntityType,
                entity.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entity.Category,
                entity.Detail,
                entity.PrimaryAssetReference ?? string.Empty))
            .Select(entity => new EntityBrowserEntityRow(entity))
            .ToArray();
        EntityBrowserEntitiesGrid.ItemsSource = visible;
        if (previousId is { } selectedId)
        {
            EntityBrowserEntitiesGrid.SelectedItem = visible.FirstOrDefault(row => row.EntityId == selectedId);
        }

        var playable = visible.Count(row => row.Record.Kind == GameEntityKind.PlayableCharacter);
        EntityBrowserSummaryText.Text = _gameMetadataIndex is null
            ? "Load the game DB mapping to populate this browser."
            : $"{visible.Length:N0}/{_entityBrowserEntities.Count:N0} entities | " +
              $"playable {playable:N0} | NPC {visible.Length - playable:N0}";
    }

    private static bool MatchesEntityKind(GameEntityRecord entity, string kind) => kind switch
    {
        "PlayableCharacter" => entity.Kind == GameEntityKind.PlayableCharacter,
        "Npc" => entity.Kind == GameEntityKind.Npc,
        "Functional" => entity.Kind == GameEntityKind.Npc &&
                        entity.Category.Contains("Functional", StringComparison.OrdinalIgnoreCase),
        "Combat" => entity.Kind == GameEntityKind.Npc &&
                    entity.Category.Contains("Combat", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private async void EntityBrowserEntitiesGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (EntityBrowserEntitiesGrid.SelectedItem is not EntityBrowserEntityRow row)
        {
            _suppressEntityCustomizationSelection = true;
            EntityBrowserCustomizationGrid.ItemsSource = Array.Empty<EntityBrowserCustomizationRow>();
            _suppressEntityCustomizationSelection = false;
            EntityBrowserAssetsGrid.ItemsSource = Array.Empty<EntityBrowserAssetRow>();
            UpdateEntityBrowserAssetActions();
            return;
        }

        var options = _gameMetadataIndex?.FindCustomizationOptions(row.Record) ?? [];
        _suppressEntityCustomizationSelection = true;
        EntityBrowserCustomizationGrid.ItemsSource = options
            .Select(option => new EntityBrowserCustomizationRow(option))
            .ToArray();
        EntityBrowserCustomizationGrid.SelectedItem = null;
        _suppressEntityCustomizationSelection = false;
        EntityBrowserCustomizationSummaryText.Text = row.Record.Kind == GameEntityKind.PlayableCharacter
            ? options.Count == 0
                ? "No customization table was found for this class or its recorded base class."
                : $"{options.Count:N0} ordered options; inherited base class " +
                  $"{row.Record.BaseEntityId?.ToString() ?? "self"}. Select an option to narrow assets."
            : "NPCs do not use the player CharacterCustomizing table; linked models and presentation assets appear on the right.";
        Log(
            $"Entity browser selection: type={row.Record.EntityType}, id={row.EntityId}, " +
            $"name={row.DisplayName}, category={row.Category}, customizations={options.Count}.");
        await ResolveEntityBrowserAssetsAsync(row.Record, null, "entity selection");
    }

    private async void EntityBrowserCustomizationGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_suppressEntityCustomizationSelection ||
            EntityBrowserEntitiesGrid.SelectedItem is not EntityBrowserEntityRow entityRow ||
            EntityBrowserCustomizationGrid.SelectedItem is not EntityBrowserCustomizationRow optionRow)
        {
            return;
        }

        if (optionRow.Option.CandidateObjectNames.Count == 0)
        {
            EntityBrowserAssetSummaryText.Text =
                $"{optionRow.Category} UI #{optionRow.UiPosition}: no asset name is encoded or safely inferred yet. " +
                optionRow.Evidence;
            Log(
                $"Entity customization selected without candidates: entity={entityRow.EntityId}, " +
                $"category={optionRow.Category}, uiPosition={optionRow.UiPosition}, " +
                $"unit={optionRow.ObjectUnit}, evidence={optionRow.Evidence}");
            return;
        }

        await ResolveEntityBrowserAssetsAsync(
            entityRow.Record,
            optionRow.Option.CandidateObjectNames,
            $"customization {optionRow.Category} UI #{optionRow.UiPosition}");
    }

    private async Task ResolveEntityBrowserAssetsAsync(
        GameEntityRecord entity,
        IReadOnlyList<string>? customizationCandidates,
        string trigger)
    {
        if (_gameMetadataIndex is null || string.IsNullOrWhiteSpace(_libraryRootPath))
        {
            EntityBrowserAssetsGrid.ItemsSource = Array.Empty<EntityBrowserAssetRow>();
            EntityBrowserAssetSummaryText.Text =
                "Load the game DB mapping and restore or choose the indexed Packages folder first.";
            UpdateEntityBrowserAssetActions();
            return;
        }

        _entityBrowserCancellation?.Cancel();
        _entityBrowserCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _entityBrowserCancellation = cancellation;
        EntityBrowserCancelButton.IsEnabled = true;
        EntityBrowserAssetsGrid.ItemsSource = Array.Empty<EntityBrowserAssetRow>();
        EntityBrowserAssetSummaryText.Text = customizationCandidates is { Count: > 0 }
            ? $"Resolving {customizationCandidates.Count:N0} candidate object name(s)..."
            : "Resolving direct database and appearance-package assets...";
        UpdateEntityBrowserAssetActions();
        var watch = Stopwatch.StartNew();
        Log(
            $"Entity asset resolution started: trigger={trigger}, type={entity.EntityType}, " +
            $"id={entity.EntityId}, name={entity.DisplayName}, " +
            $"candidates={customizationCandidates?.Count ?? 0}, root={_libraryRootPath}.");
        try
        {
            var routingProgress = new Progress<PackageSearchRoutingProgress>(progress =>
            {
                EntityBrowserAssetSummaryText.Text =
                    $"Preparing entity routes: {progress.CompletedPackages:N0}/{progress.TotalPackages:N0} packages...";
                if (progress.CompletedPackages == progress.TotalPackages ||
                    progress.CompletedPackages % 1000 == 0)
                {
                    Log(
                        $"Entity browser routing progress: {progress.CompletedPackages}/{progress.TotalPackages}, " +
                        $"cache={progress.CurrentCacheFile}.");
                }
            });
            var result = await _packageIndexStore.BrowseEntityAssetsAsync(
                _libraryRootPath,
                entity,
                _gameMetadataIndex,
                customizationCandidates,
                routingProgress: routingProgress,
                cancellationToken: cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_entityBrowserCancellation, cancellation))
            {
                return;
            }

            var rows = result.Matches.Select(match => new EntityBrowserAssetRow(match)).ToArray();
            EntityBrowserAssetsGrid.ItemsSource = rows;
            var evidence = string.Join(
                ", ",
                rows.GroupBy(row => row.Evidence)
                    .Select(group => $"{group.Key}: {group.Count():N0}"));
            EntityBrowserAssetSummaryText.Text =
                $"{rows.Length:N0} asset(s) from {result.ScannedPackageCount:N0} routed package(s) in " +
                $"{result.DurationSeconds:N1}s" +
                (string.IsNullOrEmpty(evidence) ? string.Empty : $" | {evidence}");
            StatusText.Text =
                $"Resolved {rows.Length:N0} asset(s) for {entity.DisplayName}.";
            Log(
                $"Entity asset resolution completed in {watch.Elapsed.TotalSeconds:N3}s: " +
                $"trigger={trigger}, type={entity.EntityType}, id={entity.EntityId}, " +
                $"matches={rows.Length}, packages={result.ScannedPackageCount}, " +
                $"routingBuilt={result.RoutingIndexBuilt}, evidence={evidence}.");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_entityBrowserCancellation, cancellation))
            {
                EntityBrowserAssetSummaryText.Text = "Entity asset resolution cancelled.";
            }
            Log(
                $"Entity asset resolution cancelled after {watch.Elapsed.TotalSeconds:N3}s: " +
                $"trigger={trigger}, type={entity.EntityType}, id={entity.EntityId}.");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            EntityBrowserAssetSummaryText.Text = "Entity assets could not be resolved.";
            Log(
                $"Entity asset resolution failed after {watch.Elapsed.TotalSeconds:N3}s: " +
                $"trigger={trigger}, type={entity.EntityType}, id={entity.EntityId}, error={exception}");
            MessageBox.Show(
                this,
                exception.Message,
                "Arkive entity browser",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_entityBrowserCancellation, cancellation))
            {
                _entityBrowserCancellation = null;
                EntityBrowserCancelButton.IsEnabled = false;
            }
            cancellation.Dispose();
            UpdateEntityBrowserAssetActions();
        }
    }

    private void EntityBrowserAssetsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateEntityBrowserAssetActions();

    private void UpdateEntityBrowserAssetActions()
    {
        if (EntityBrowserInspectAssetButton is null)
        {
            return;
        }

        var row = EntityBrowserAssetsGrid.SelectedItem as EntityBrowserAssetRow;
        var selected = row is not null;
        EntityBrowserInspectAssetButton.IsEnabled = selected && _umodelBridge is not null;
        EntityBrowserBrowsePackageButton.IsEnabled = selected && _umodelBridge is not null;
        EntityBrowserOpenPackageButton.IsEnabled = selected;
        EntityBrowserExportAssetButton.IsEnabled = selected && _umodelBridge is not null &&
                                                       row!.ExternalAsset.IsExportable &&
                                                       !_exportInProgress;
        EntityBrowserStartTextureModButton.IsEnabled = !_guidedTextureModInProgress &&
                                                       row is not null &&
                                                       CanReconnoitreTexture(row.Kind, row.ClassName) &&
                                                       _nativeTextureExportService is not null;
        EntityBrowserExtractSkeletalMeshAssetKitButton.IsEnabled =
            !_skeletalMeshAssetKitInProgress &&
            !_skeletalMeshReconnaissanceInProgress &&
            !_skeletalMeshEditableExportInProgress &&
            _skeletalMeshAssetKitService is not null &&
            row is not null && CanReconnoitreSkeletalMesh(row.Kind, row.ClassName);
    }

    private void EntityBrowserInspectAssetButton_Click(object sender, RoutedEventArgs e) =>
        InspectEntityBrowserAsset();

    private void EntityBrowserAssetsGrid_MouseDoubleClick(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(EntityBrowserAssetsGrid, source) is not DataGridRow)
        {
            return;
        }

        InspectEntityBrowserAsset();
        e.Handled = true;
    }

    private void InspectEntityBrowserAsset()
    {
        if (_umodelBridge is null ||
            EntityBrowserAssetsGrid.SelectedItem is not EntityBrowserAssetRow row)
        {
            return;
        }

        try
        {
            _umodelBridge.StartAssetPreview(row.SourcePath, row.ExternalAsset);
            StatusText.Text = $"Asked UModel to inspect {row.PackageAlias}.{row.ObjectName}.";
            Log(
                $"Entity browser asset viewer launched: package={row.SourcePath}, " +
                $"class={row.ClassName}, object={row.ObjectName}, evidence={row.Evidence}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log($"Entity browser asset viewer launch failed: {exception}");
            MessageBox.Show(this, exception.Message, "Arkive entity preview", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void EntityBrowserBrowsePackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_umodelBridge is null ||
            EntityBrowserAssetsGrid.SelectedItem is not EntityBrowserAssetRow row)
        {
            return;
        }

        try
        {
            _umodelBridge.StartPreview(row.SourcePath);
            StatusText.Text = $"Opened {row.PackageAlias} in UModel.";
            Log(
                $"Entity browser package viewer launched: package={row.SourcePath}, " +
                $"selectedObject={row.ObjectName}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log($"Entity browser package viewer launch failed: {exception}");
        }
    }

    private async void EntityBrowserOpenPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (EntityBrowserAssetsGrid.SelectedItem is not EntityBrowserAssetRow row)
        {
            return;
        }

        await OpenPackageAsync(row.SourcePath, "Entity browser result action");
        MainTabControl.SelectedIndex = 0;
    }

    private async void EntityBrowserExportAssetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_umodelBridge is null ||
            EntityBrowserAssetsGrid.SelectedItem is not EntityBrowserAssetRow row ||
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
            trigger: $"Entity browser ({row.Evidence})");
    }

    private IReadOnlyList<GameAssetAssociation> FindGameMetadata(
        string packagePath,
        string objectName)
    {
        if (_gameMetadataIndex is null)
        {
            return [];
        }

        return _gameMetadataIndex.Find(
            LostArkPackageName.GetDisplayName(packagePath),
            Path.GetFileNameWithoutExtension(packagePath),
            objectName);
    }

    private void RefreshOpenPackageMetadata()
    {
        if (string.IsNullOrEmpty(_currentPackagePath))
        {
            return;
        }

        _assets = _assets
            .Select(asset => asset with
            {
                GameMetadata = FindGameMetadata(_currentPackagePath, asset.ObjectPath),
            })
            .ToArray();
        ApplyAssetFilter();
    }

}
