using System.IO;
using System.Windows.Controls;

namespace Arkive.App;

/// <summary>
/// The advanced views read their data when first shown or when an action needs it, not at launch: launch only waits for
/// what the Mods tab shows. A view's refresh asked for while the view has not been read (an operation elsewhere that
/// used to refresh it) only leaves it to be read when it is next shown.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// One advanced view's data. It starts stale, is read in full by <see cref="EnsureAsync"/>, and is marked stale again
    /// when a change elsewhere leaves it behind (enabling mods, say). Two callers asking at once share one read.
    /// </summary>
    private sealed class DeferredView(string title, Func<string, Task> load)
    {
        private Task? _running;

        /// <summary>What the busy card says while the view is read.</summary>
        public string Title { get; } = title;

        /// <summary>Not read since launch, or changed since it was last read.</summary>
        public bool Stale { get; private set; } = true;

        public bool Loading => _running is not null;

        public void MarkStale() => Stale = true;

        /// <summary>Reads the view if it is stale; waits for a read already running.</summary>
        public async Task EnsureAsync(string trigger)
        {
            if (_running is { } running) await running;
            if (!Stale) return;
            Stale = false;
            var read = load(trigger);
            _running = read;
            try
            {
                await read;
            }
            catch
            {
                // A read that failed is tried again the next time the view is needed.
                Stale = true;
                throw;
            }
            finally
            {
                if (ReferenceEquals(_running, read)) _running = null;
            }
        }
    }

    private DeferredView? _exportsView;
    private DeferredView? _baselinesView;
    private DeferredView? _meshView;
    private DeferredView? _staticMeshView;
    private DeferredView? _materialsView;
    private DeferredView? _packagesView;
    private DeferredView? _modSetsView;
    private DeferredView? _modifiedMeshesView;
    private DeferredView? _updateAuditView;

    private DeferredView ExportsView => _exportsView ??= new("Loading the export sessions…", RefreshExportWorkspaceAsync);
    private DeferredView BaselinesView => _baselinesView ??= new("Loading the baselines…", RefreshBaselineWorkspaceAsync);
    private DeferredView MeshView => _meshView ??= new("Loading the editable meshes…",
        trigger => RefreshSkeletalMeshEditableWorkspaceAsync(trigger));
    private DeferredView StaticMeshView => _staticMeshView ??= new("Loading the static meshes…",
        trigger => RefreshStaticMeshWorkspaceAsync(trigger));
    private DeferredView MaterialsView => _materialsView ??= new("Loading the material projects…",
        trigger => RefreshMaterialProjectsAsync(trigger));
    private DeferredView PackagesView => _packagesView ??= new("Loading the package workspace…",
        trigger => RefreshGuidedTextureModsAsync(trigger));
    private DeferredView ModSetsView => _modSetsView ??= new("Loading the mod sets…", trigger => RefreshModSetsAsync(trigger));
    private DeferredView ModifiedMeshesView => _modifiedMeshesView ??= new("Loading the mesh packages…",
        trigger => RefreshModifiedMeshUpksAsync(trigger));
    private DeferredView UpdateAuditView => _updateAuditView ??= new("Loading the game update check…", RefreshClientUpdateAuditAsync);

    private IEnumerable<DeferredView?> AllViews =>
    [
        _exportsView, _baselinesView, _meshView, _staticMeshView, _materialsView, _packagesView, _modSetsView,
        _modifiedMeshesView, _updateAuditView,
    ];

    /// <summary>True while a view loads on demand.</summary>
    private bool ViewsLoading => AllViews.Any(view => view?.Loading == true);

    /// <summary>Reads the views that are stale, one after another, under the busy card.</summary>
    private async Task EnsureViewsAsync(string trigger, params DeferredView[] views)
    {
        var stale = views.Where(view => view.Stale || view.Loading).ToArray();
        if (stale.Length == 0) return;
        // Inside an operation that has its own card, that card and its text stay.
        var own = _busyDepth == 0;
        using var busy = own ? ShowBusy(stale[0].Title, "Arkive is reading the mods' working files. Please wait until it finishes.") : null;
        foreach (var view in stale)
        {
            if (own) UpdateBusy(view.Title);
            await view.EnsureAsync(trigger);
        }
    }

    /// <summary>
    /// For an action that goes on to use a view's rows (selecting what it just made): reads the view if it has not been
    /// read, which shows the new state too, and otherwise refreshes it as before.
    /// </summary>
    private async Task ReadOrRefreshAsync(DeferredView view, string trigger, Func<Task> refresh)
    {
        if (view.Stale || view.Loading) await EnsureViewsAsync(trigger, view);
        else await refresh();
    }

    /// <summary>
    /// The package workspace: package projects and the material projects listed with them. Every advanced view reads
    /// it, as it did when enabling mods had left it to be read again.
    /// </summary>
    private Task EnsurePackageWorkspaceAsync(string trigger) => EnsureViewsAsync(trigger, PackagesView, MaterialsView);

    /// <summary>Marks the package workspace to be read again when a view shows it (see EnsurePackageWorkspaceAsync).</summary>
    private void MarkPackageWorkspaceStale()
    {
        PackagesView.MarkStale();
        MaterialsView.MarkStale();
    }

    /// <summary>The views an advanced main tab shows; everyday tabs show none.</summary>
    private DeferredView[] ViewsFor(TabItem tab)
    {
        if (tab == LibraryTab || tab == CreateModTab || tab == WorkshopTab || tab == ModifiedUpksTab ||
            tab == BodyProportionsTab || tab == SoundsTab || tab == LoginScreenTab || tab == FontsTab ||
            tab == LanguageTab || tab == OutfitSwapTab || tab == TweaksTab)
            return [];
        DeferredView[] own = tab == ExportsTab ? [ExportsView]
            : tab == BaselinesTab ? [BaselinesView]
            : tab == SkeletalMeshReconTab ? [MeshView]
            : tab == StaticMeshTab ? [StaticMeshView]
            : [];
        return [PackagesView, MaterialsView, .. own];
    }

    /// <summary>The views an advanced page of the Mods tab shows.</summary>
    private DeferredView[] ViewsForModsPage(TabItem page) =>
        page == ModLibraryTab || page == ModConflictDependencyTab ? []
        : page == ClientUpdateSafetyTab ? [PackagesView, MaterialsView, UpdateAuditView]
        : page == ModSetsTab ? [PackagesView, MaterialsView, ModSetsView]
        : page == LegacyMeshBundlesTab ? [PackagesView, MaterialsView, ModifiedMeshesView]
        : [PackagesView, MaterialsView];

    private async Task ShowViewsAsync(string trigger, DeferredView[] views)
    {
        if (views.Length == 0) return;
        try
        {
            // Every advanced view lists package projects with in-game names, so the game database comes first.
            await EnsureGameDatabaseAsync(trigger);
            await EnsureViewsAsync(trigger, views);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or System.Text.Json.JsonException)
        {
            StatusText.Text = $"This view could not be read: {exception.Message}";
            Log($"View read failed: trigger={trigger}, error={exception}");
        }
    }
}
