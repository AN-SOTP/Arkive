using System.IO;
using System.Windows;
using Arkive.Core.Assets;
using Arkive.Core.Interface;
using Arkive.Core.Mods;
using Arkive.Core.Textures;

namespace Arkive.App;

public partial class MainWindow
{
    private static bool CanViewTexture(AssetKind kind) => kind == AssetKind.Texture;

    /// <summary>Shows a texture from the game files in the Texture Viewer, with the parts the interface names on it.</summary>
    private async Task OpenTextureViewerAsync(LibraryAssetRow row)
    {
        if (_nativeTextureExportService is null)
        {
            MessageBox.Show(this, _nativeTextureExportUnavailableReason ?? "Arkive's texture decoder is unavailable.",
                "Arkive Texture Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            StatusText.Text = $"Opening {row.ObjectName} in the Texture Viewer…";
            var directory = Path.Combine(_projectRoot, "cache", "texture-viewer", Guid.NewGuid().ToString("N"));
            var report = await _textureReconnaissanceService.InspectAsync(new Arkive.Core.Inspection.TextureReconnaissanceRequest(
                row.SourcePath, row.PackageAlias, row.ExportIndex - 1, AssetKind.Texture, row.ClassName, row.ObjectName,
                row.SerialOffset, row.SerialSize));
            var recon = await _textureReconnaissanceWorkspaceStore.SaveAsync(report);
            var native = await _nativeTextureExportService.ExportAsync(new NativeTextureExportRequest(report, directory, recon.JsonPath));
            var path = native.Manifest.EditImageRelativePath is { } relative
                ? Path.Combine(native.OutputDirectory, relative)
                : throw new NotSupportedException($"{row.ObjectName} has no picture Arkive can show.");
            var picture = (await TextureEditableImageService.LoadAsync(path)).Image;
            ShowTextureViewer($"{row.PackageAlias}.{row.ObjectName}", picture, null,
                () => Task.FromResult(picture.DeepCopy()), row.SourcePath, row.ObjectName);
            StatusText.Text = $"Opened {row.ObjectName} in the Texture Viewer.";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or
                                              UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"Asset viewer could not open {row.ObjectName}: {exception}");
            MessageBox.Show(this, exception.Message, "Arkive Texture Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Opens a mod's texture working copy in the Texture Viewer to change its colours.</summary>
    private async Task OpenTextureColourEditorAsync(ArkiveModLocalEdit edit)
    {
        if (edit.Entry is not GuidedTextureModProjectEntry texture) return;
        var working = texture.Manifest.EditableImagePath;
        if (!Path.GetExtension(working).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Colours can be changed in Arkive for PNG working files. This one is a DDS; edit it in your image editor.");
        var picture = (await TextureEditableImageService.LoadAsync(working)).Image;
        var originalPath = await ResolveOriginalEditableImageAsync(new GuidedTextureModRow(texture, null, null, [], null));
        var window = ShowTextureViewer($"{texture.Manifest.PackageAlias}.{texture.Manifest.ObjectName}", picture, working,
            async () => (await TextureEditableImageService.LoadAsync(originalPath)).Image,
            texture.Manifest.SourcePackagePath, texture.Manifest.ObjectName);
        window.Saved += () => AuthoringStatusText.Text =
            $"Saved {texture.Manifest.ObjectName}'s new colours to its working file. Save the mod to keep them.";
    }

    private TextureViewerWindow ShowTextureViewer(string title, RgbaImage picture, string? workingPath,
        Func<Task<RgbaImage>> loadOriginal, string packagePath, string textureName)
    {
        var window = new TextureViewerWindow(title, picture, workingPath, loadOriginal,
            () => Task.Run(() => InterfaceNameSidecar.FindParts(packagePath, textureName, _protectionProvider)),
            _effectiveAppearanceTheme, Log)
        {
            Owner = this,
        };
        window.Show();
        return window;
    }

    private async void AuthoringColoursButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AuthoringColoursButton.IsEnabled || AuthoringAssetsGrid.SelectedItem is not AuthoringAssetRow { Edit: { } edit }) return;
        await RunAuthoringOperationAsync(() => OpenTextureColourEditorAsync(edit));
    }
}
