using System.IO;
using System.Windows;
using Arkive.Core.Assets;
using Arkive.Core.Viewer;

namespace Arkive.App;

public partial class MainWindow
{
    private ModelViewerSceneService? _modelViewerService;
    private ModelViewerWindow? _modelViewerWindow;

    private static bool CanPreviewInArkive(AssetKind kind) => kind is AssetKind.SkeletalMesh or AssetKind.StaticMesh;

    private void LibraryPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is LibraryAssetRow row) OpenModelViewer(row);
    }

    private void OpenModelViewer(LibraryAssetRow row)
    {
        if (_umodelBridge is null) return;
        try
        {
            _modelViewerService ??= new ModelViewerSceneService(
                _umodelBridge,
                new ModelViewerCache(Path.Combine(_projectRoot, "cache", "model-viewer")),
                protectionProvider: _protectionProvider);
            if (_modelViewerWindow is null || !_modelViewerWindow.IsLoaded)
            {
                _modelViewerWindow = new ModelViewerWindow(
                    _modelViewerService,
                    _umodelBridge,
                    Path.Combine(_projectRoot, "cache", "webview2"),
                    _effectiveAppearanceTheme,
                    BrowseModelViewer,
                    Log) { Owner = this };
                _modelViewerWindow.Closed += (_, _) => _modelViewerWindow = null;
                _modelViewerWindow.Show();
            }
            _modelViewerWindow.Activate();
            _ = _modelViewerWindow.ShowObjectAsync(ToModelViewerRequest(row));
            StatusText.Text = $"Opened {row.PackageAlias}.{row.ObjectName} in Arkive's model viewer.";
            Log($"Model viewer opened: package={row.SourcePath}, class={row.ClassName}, object={row.ObjectName}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"Model viewer could not open: {exception}");
            MessageBox.Show(this, exception.Message, "Arkive model viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

    /// <summary>PgUp/PgDn in the viewer: moves the library selection to the previous or next mesh result.</summary>
    private ModelViewerRequest? BrowseModelViewer(int delta)
    {
        var rows = LibraryGrid.Items.OfType<LibraryAssetRow>().ToList();
        var index = LibraryGrid.SelectedItem is LibraryAssetRow current ? rows.IndexOf(current) : -1;
        for (var step = 1; step <= rows.Count; step++)
        {
            var candidate = rows[((index + Math.Sign(delta) * step) % rows.Count + rows.Count) % rows.Count];
            if (!CanPreviewInArkive(candidate.Kind)) continue;
            LibraryGrid.SelectedItem = candidate;
            LibraryGrid.ScrollIntoView(candidate);
            return ToModelViewerRequest(candidate);
        }
        return null;
    }
}
