using System.IO;
using System.Windows;
using System.Windows.Controls;
using Arkive.Core.Assets;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.Mods;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;
using Arkive.Core.Viewer;

namespace Arkive.App;

public partial class MainWindow
{
    private ModelViewerSceneService? _modelViewerService;
    private ModelViewerWindow? _modelViewerWindow;
    private DataGrid? _modelViewerBrowseGrid;
    private readonly SemaphoreSlim _colourBakeGate = new(1, 1);

    private static bool CanPreviewInArkive(AssetKind kind) => kind is AssetKind.SkeletalMesh or AssetKind.StaticMesh;

    private void LibraryPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is LibraryAssetRow row) OpenLibraryModelViewer(row);
    }

    private ModelViewerSceneService ModelViewerService() => _modelViewerService ??= new ModelViewerSceneService(
        _umodelBridge!,
        new ModelViewerCache(Path.Combine(_projectRoot, "cache", "model-viewer")),
        protectionProvider: _protectionProvider);

    private ModelViewerWindow CreateModelViewerWindow(Func<int, ModelViewerRequest?> browse) => new(
        ModelViewerService(),
        _umodelBridge!,
        Path.Combine(_projectRoot, "cache", "webview2"),
        _effectiveAppearanceTheme,
        browse,
        Log)
    {
        Animations = FindViewerAnimationsAsync,
        Environments = FindViewerEnvironmentsAsync,
    };

    /// <summary>
    /// Bakes the game colours of a newly added mesh next to its working GLB (<c>name.arkive-colours</c>), so
    /// Open edited file shows them in Blender. Runs in an off-screen viewer; failures only reach the log.
    /// </summary>
    private async Task BakeAuthoringColoursAsync(LibraryAssetRow row, string glb)
    {
        if (_umodelBridge is null) return;
        var folder = Path.Combine(Path.GetDirectoryName(glb)!, Path.GetFileNameWithoutExtension(glb) + ".arkive-colours");
        // A complete kit reports its references after this bake starts; keep that newer message.
        var status = AuthoringStatusText.Text;
        await _colourBakeGate.WaitAsync();
        ModelViewerWindow? window = null;
        try
        {
            window = CreateModelViewerWindow(_ => null);
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.Width = 1024;
            window.Height = 768;
            window.Show();
            await window.ShowObjectAsync(ToModelViewerRequest(row));
            var textures = await window.BakeAsync(folder);
            Log($"Baked {textures.Count} game colour texture(s) for {row.ObjectName}: {folder}");
            if (AuthoringStatusText.Text == status)
                AuthoringStatusText.Text = $"Baked {row.ObjectName}'s game colours for Blender. Open edited file shows them.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException
                                              or InvalidOperationException or OperationCanceledException)
        {
            Log($"Game colour baking skipped for {row.ObjectName}: {exception.Message}");
        }
        finally
        {
            window?.Close();
            _colourBakeGate.Release();
        }
    }

    private void OpenLibraryModelViewer(LibraryAssetRow row)
    {
        if (OpenModelViewer(row, LibraryGrid))
            StatusText.Text = $"Opened {row.PackageAlias}.{row.ObjectName} in Arkive's model viewer.";
    }

    /// <summary>Opens a search result; PgUp/PgDn in the viewer then move through <paramref name="results"/>.</summary>
    private bool OpenModelViewer(LibraryAssetRow row, DataGrid results) =>
        OpenModelViewer(ToModelViewerRequest(row), edits: null, results);

    private bool OpenModelViewer(ModelViewerRequest request, Func<CancellationToken, Task<ModelViewerEdits?>>? edits,
        DataGrid? results = null)
    {
        if (_umodelBridge is null) return false;
        _modelViewerBrowseGrid = results;
        try
        {
            if (_modelViewerWindow is null || !_modelViewerWindow.IsLoaded)
            {
                _modelViewerWindow = CreateModelViewerWindow(BrowseModelViewer);
                _modelViewerWindow.Owner = this;
                _modelViewerWindow.Closed += (_, _) => _modelViewerWindow = null;
                _modelViewerWindow.Show();
            }
            _modelViewerWindow.Activate();
            _ = _modelViewerWindow.ShowObjectAsync(request, edits);
            Log($"Model viewer opened: package={request.PackagePath}, class={request.ClassName}, object={request.ObjectName}, " +
                $"edits={edits is not null}.");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"Model viewer could not open: {exception}");
            MessageBox.Show(this, exception.Message, "Arkive model viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Mod authoring: shows the selected mesh (or the mod's first mesh) from its clean package with the mod's
    /// working files on top: its edited GLB, and every included texture and material edit it uses. The view
    /// follows saves to those files.
    /// </summary>
    private async void AuthoringViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_umodelBridge is null || _authoringDraft is not { } draft) return;
        static bool IsMesh(AuthoringAssetRow row) =>
            row.Edit?.Kind is UnifiedPackageAssetKind.SkeletalMesh or UnifiedPackageAssetKind.StaticMesh;
        var rows = AuthoringAssetsGrid.Items.OfType<AuthoringAssetRow>().ToList();
        var mesh = AuthoringAssetsGrid.SelectedItem is AuthoringAssetRow selected && IsMesh(selected)
            ? selected
            : rows.FirstOrDefault(IsMesh);
        if (mesh?.Edit is not { } edit)
        {
            AuthoringStatusText.Text = "Add a mesh to this mod to see it with your edits.";
            return;
        }
        try
        {
            var clean = await _arkiveModApply.ResolveCleanPackageAsync(edit.SourcePackagePath);
            var catalogue = await Task.Run(() => new AssetCatalogReader(_reader, _protectionProvider).Read(clean));
            var match = catalogue.Assets.SingleOrDefault(asset => asset.ObjectName.Equals(edit.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                    asset.ClassName.Equals(edit.Kind.ToString(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The original mesh is unavailable or ambiguous in its clean package.");
            var request = new ModelViewerRequest(clean, mesh.Package,
                _libraryRootPath ?? Path.GetDirectoryName(edit.SourcePackagePath), match.ExportIndex, match.SerialOffset,
                match.SerialSize, match.ClassName, match.ObjectName);
            var draftId = draft.DraftId;
            if (OpenModelViewer(request, cancellationToken => AuthoringViewerEditsAsync(draftId, edit.EditId, cancellationToken)))
                AuthoringStatusText.Text = $"Showing {edit.ObjectName} with this mod's edits. Saving a working file refreshes the view.";
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            AuthoringStatusText.Text = $"Preview failed: {exception.Message}";
            Log($"Authoring model viewer failed: {exception}");
        }
    }

    /// <summary>Reads the draft's working files afresh; unticked references are not part of the mod and are left out.</summary>
    private async Task<ModelViewerEdits?> AuthoringViewerEditsAsync(string draftId, string meshEditId, CancellationToken cancellationToken)
    {
        var draft = _authoringDraft?.DraftId == draftId
            ? _authoringDraft
            : (await _modAuthoringWorkspace.LoadAsync()).FirstOrDefault(candidate => candidate.DraftId == draftId);
        if (draft is null) return null;
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_arkiveModEdits, cancellationToken);
        var included = draft.EditIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var materials = new Dictionary<string, ModelViewerMaterialEdit>(StringComparer.OrdinalIgnoreCase);
        var watched = new List<string>();
        string? glb = null;
        foreach (var id in draft.EditIds.Concat(draft.ReferenceEditIds).Append(meshEditId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (snapshot.Find(id) is not { } edit) continue;
            watched.Add(edit.ManifestPath);
            var isMesh = id.Equals(meshEditId, StringComparison.OrdinalIgnoreCase);
            if (!isMesh && !included.Contains(id)) continue;
            switch (edit.Entry)
            {
                case GuidedTextureModProjectEntry texture when File.Exists(texture.Manifest.EditableImagePath):
                    textures[edit.ObjectName] = texture.Manifest.EditableImagePath;
                    break;
                case SkeletalMeshEditWorkspaceEntry skeletal when isMesh && File.Exists(skeletal.EditedGlbPath):
                    glb = skeletal.EditedGlbPath;
                    break;
                case StaticMeshEditEntry staticMesh when isMesh && File.Exists(staticMesh.Manifest.EditedGlbPath):
                    glb = staticMesh.Manifest.EditedGlbPath;
                    break;
                case MaterialInstanceEditEntry material when ToViewerEdit(material.Document) is { } changed:
                    materials[edit.ObjectName] = changed;
                    break;
            }
        }
        return new ModelViewerEdits(draft.Metadata.Name, glb, textures, materials, watched);
    }

    private static ModelViewerMaterialEdit? ToViewerEdit(MaterialInstanceEditDocument document)
    {
        // Texture references read like Texture2D'Package.Group.Name'; the viewer knows textures by name.
        static string Leaf(string resolved)
        {
            var quote = resolved.IndexOf('\'');
            var path = quote >= 0 ? resolved[(quote + 1)..].TrimEnd('\'') : resolved;
            return path[(path.LastIndexOf('.') + 1)..];
        }
        var edit = new ModelViewerMaterialEdit(
            document.Scalars.Where(scalar => scalar.Changed)
                .ToDictionary(scalar => scalar.Name, scalar => scalar.Value, StringComparer.OrdinalIgnoreCase),
            document.Vectors.Where(vector => vector.Changed).ToDictionary(vector => vector.Name,
                vector => new[] { vector.Red, vector.Green, vector.Blue, vector.Alpha }, StringComparer.OrdinalIgnoreCase),
            document.Textures.Where(texture => texture.ObjectReference != texture.SourceObjectReference && texture.ObjectReference != 0)
                .ToDictionary(texture => texture.Name, texture => Leaf(texture.ResolvedObject), StringComparer.OrdinalIgnoreCase));
        return edit.Scalars.Count + edit.Vectors.Count + edit.Textures.Count == 0 ? null : edit;
    }

    private ModelViewerRequest ToModelViewerRequest(LibraryAssetRow row) => new(
        row.SourcePath,
        row.PackageAlias,
        _libraryRootPath ?? Path.GetDirectoryName(row.SourcePath),
        row.ExportIndex,
        row.SerialOffset,
        row.SerialSize,
        row.ClassName,
        row.ObjectName);

    /// <summary>PgUp/PgDn in the viewer: moves the opening search's selection to the previous or next mesh result.</summary>
    private ModelViewerRequest? BrowseModelViewer(int delta)
    {
        if (_modelViewerBrowseGrid is not { } grid) return null;
        var rows = grid.Items.OfType<LibraryAssetRow>().ToList();
        var index = grid.SelectedItem is LibraryAssetRow current ? rows.IndexOf(current) : -1;
        for (var step = 1; step <= rows.Count; step++)
        {
            var candidate = rows[((index + Math.Sign(delta) * step) % rows.Count + rows.Count) % rows.Count];
            if (!CanPreviewInArkive(candidate.Kind)) continue;
            grid.SelectedItem = candidate;
            grid.ScrollIntoView(candidate);
            return ToModelViewerRequest(candidate);
        }
        return null;
    }
}
