using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Arkive.Core.Diagnostics;

namespace Arkive.App;

/// <summary>
/// What a measuring run (--measure-startup) can ask of the window after loading: a dump of the Mods tab, to prove a
/// change leaves the list exactly as it was, and opening advanced views one at a time, to time them and count their rows.
/// </summary>
public partial class MainWindow
{
    /// <summary>The views --open-view knows, by the name a measuring run gives them.</summary>
    internal static readonly IReadOnlyList<string> MeasurableViews =
    [
        "exports", "baselines", "mesh", "static-mesh", "materials", "packages", "mod-sets", "modified-meshes",
        "update-audit", "mod-dependencies", "asset-library",
    ];

    // Lets the tab hooks run in a measuring run, whose window is never shown and so never counts as loaded.
    private bool _measuringViews;

    /// <summary>
    /// Writes what the Mods tab holds: every row with each text and flag it shows, its assets, and the two summary
    /// lines. Sorted, with paths relative to the data folder and the game, so the same state gives the same bytes.
    /// </summary>
    internal void WriteModsListDump(string path, string dataRoot)
    {
        var paths = new ArkiveModParityDumps.Relative(dataRoot, _arkiveModLibraryManifest.GamePackagesDirectory);
        var dump = new
        {
            StatusText = paths.Text(ModLibraryStatusText.Text),
            PendingText = paths.Text(ModLibraryPendingText.Text),
            RowCount = _modLibraryRows.Length,
            ShownCount = ModLibraryGrid.Items.Count,
            SelectedModId = (ModLibraryGrid.SelectedItem as ModLibraryRow)?.Entry.ModId,
            OverlapBanner = ModOverlapBanner.Visibility == Visibility.Visible
                ? new
                {
                    Title = ModOverlapTitleText.Text,
                    Lines = (ModOverlapList.ItemsSource as IEnumerable<ModOverlapRow>)?.Select(row => row.Text).ToArray() ?? [],
                    More = ModOverlapMoreText.Text,
                }
                : null,
            Rows = _modLibraryRows.OrderBy(row => row.Entry.ModId, StringComparer.OrdinalIgnoreCase).Select(row => new
            {
                row.Entry.ModId,
                Name = paths.Text(row.DisplayName),
                row.Enabled,
                row.Priority,
                row.Version,
                Author = paths.Text(row.Author),
                AssetSummary = row.AssetSummary,
                Packages = row.Packages,
                State = paths.Text(row.State),
                Conflicts = paths.Text(row.Conflicts),
                row.FileName,
                FilePath = paths.PathOf(row.FilePath),
                row.Origin,
                row.AuthoringPending,
                row.NeedsGameUpdate,
                row.HasActiveAssets,
                row.Status.FileMissing,
                row.Status.InGame,
                row.Status.StillInGame,
                row.Status.OutsideChange,
                row.Status.ModifiedLocally,
                row.Status.WinningCount,
                row.Status.OverriddenCount,
                row.Status.TiedCount,
                row.Status.GameData,
                row.Status.Looks,
                Group = row.Group is { IsPack: true } group
                    ? new { group.Title, group.Summary, group.Warning, group.OnState }
                    : null,
                Assets = row.Status.Assets.OrderBy(asset => asset.Asset.AssetId, StringComparer.OrdinalIgnoreCase)
                    .Select(asset => new ModAssetRow(asset)).Select(asset => new
                    {
                        asset.Status.Asset.AssetId,
                        asset.Kind,
                        DisplayName = paths.Text(asset.DisplayName),
                        asset.ObjectName,
                        asset.Package,
                        Resolution = paths.Text(asset.Resolution),
                        Setup = asset.Setup,
                        InGame = asset.InGame,
                        asset.LocalChanges,
                        asset.Route,
                        asset.Status.Outcome,
                        SetupState = asset.Status.Setup,
                        InGameFlag = asset.Status.InGame,
                        asset.Status.ModifiedLocally,
                        EditId = asset.Status.Binding?.EditId,
                        EditManifest = paths.PathOf(asset.Status.Edit?.ManifestPath),
                        EditIncluded = asset.Status.Edit?.Included,
                    }).ToArray(),
            }).ToArray(),
        };
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(dump, ArkiveModParityDumps.JsonOptions) + "\n");
    }

    /// <summary>
    /// Opens a view the way its entry in the advanced menu does, waits until everything that started has finished
    /// loading, and returns how many rows each of the view's grids then holds.
    /// </summary>
    internal async Task<string> OpenViewForMeasurementAsync(string view)
    {
        if (view == "asset-library") return await OpenAssetLibraryForMeasurementAsync();
        var (tag, grids) = view switch
        {
            "exports" => ("Exports", new DataGrid[] { ExportWorkspaceGrid, ExportArtifactFilesGrid }),
            "baselines" => ("Baselines", [ClientSnapshotsGrid, AssetBaselinesGrid]),
            "mesh" => ("MeshRecon", [SkeletalMeshEditableExportsGrid, SkeletalMeshEditedGlbsGrid]),
            "static-mesh" => ("StaticMeshes", [StaticMeshEditableExportsGrid, StaticMeshEditsGrid]),
            "materials" => ("Materials", [MaterialProjectsGrid, MaterialScalarGrid, MaterialVectorGrid, MaterialTextureGrid,
                MaterialParentGrid]),
            "packages" => ("PackageProjects", [GuidedPackagesGrid, GuidedTextureModsGrid, GuidedMeshModsGrid,
                GuidedStaticMeshModsGrid, GuidedMaterialModsGrid]),
            "mod-sets" => ("ModSets", [ModSetsGrid, ModSetPackagesGrid, ModSetConflictsGrid]),
            "modified-meshes" => ("LegacyBundles", [ModifiedMeshUpksGrid]),
            "update-audit" => ("GameSafety", [ClientUpdatePackagesGrid, ClientUpdateAssetsGrid, ClientUpdateInstallationsGrid,
                ClientUpdateFormatChecksGrid]),
            "mod-dependencies" => ("ModDependencies", [ModGraphConflictsGrid, ModGraphDependenciesGrid]),
            _ => throw new ArgumentException($"Unknown view: {view}"),
        };
        _measuringViews = true;
        try
        {
            AdvancedNavigationMenuItem_Click(new MenuItem { Tag = tag }, new RoutedEventArgs());
            await WaitUntilIdleAsync();
        }
        finally
        {
            _measuringViews = false;
        }
        return string.Join(", ", grids.Select(grid => $"{grid.Name}={grid.Items.Count}"));
    }

    /// <summary>
    /// Shows the Asset Library as choosing its tab does: the game database is loaded (and, in a run that saves caches,
    /// read again from the game when it is out of date), then the search runs: --library-search by in-game name, else
    /// the saved one. Returns the rows found, the database's size and how the index stands against the game folder.
    /// The index itself is never updated by a measuring run.
    /// </summary>
    private async Task<string> OpenAssetLibraryForMeasurementAsync()
    {
        _measuringViews = true;
        try
        {
            if (StartupMeasurement.Current?.LibrarySearch is { } query)
            {
                LibrarySearchTextBox.Text = query;
                SelectComboBoxTag(LibrarySearchScopeComboBox, "InGameName");
            }
            MainTabControl.SelectedItem = LibraryTab;
            await EnsureGameDatabaseAsync("measuring run");
            await WaitUntilIdleAsync();
            await RestoredBackgroundWork();
            await ApplyLibraryFilterAsync("measuring run");
            await WaitUntilIdleAsync();
        }
        finally
        {
            _measuringViews = false;
        }
        var freshness = _libraryRootPath is { } root ? await Task.Run(() => _packageIndexStore.GetFreshness(root)) : null;
        return $"LibraryGrid={LibraryGrid.Items.Count}, associations={_gameMetadataIndex?.AssociationCount ?? 0}, " +
               $"lookReferences={_gameMetadataIndex?.LookReferenceCount ?? 0}, " +
               $"indexNew={freshness?.NewPackages ?? 0}, indexChanged={freshness?.ChangedPackages ?? 0}, indexRemoved={freshness?.RemovedPackages ?? 0}";
    }

    /// <summary>Waits until no busy card is up and no view is loading, three checks in a row 50 ms apart.</summary>
    private async Task WaitUntilIdleAsync()
    {
        for (var quiet = 0; quiet < 3;)
        {
            await Task.Delay(50);
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            quiet = _busyDepth == 0 && !_modGraphBusy && !_skeletalMeshEditableWorkspaceBusy && !ViewsLoading ? quiet + 1 : 0;
        }
    }
}
