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
    private void AssetsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = AssetsGrid.SelectedItem as AssetRow;
        var canExport = row?.ExternalAsset?.IsExportable == true && _currentPackagePath is not null;
        SetActionsEnabled(_usingUmodelCatalogue && _currentPackagePath is not null, canExport);
        BaselineAssetButton.IsEnabled = row is not null &&
                                        _currentPackagePath is not null &&
                                        !_baselineOperationInProgress;
        UpdateTextureReconnaissanceActionState();
        UpdateSkeletalMeshReconnaissanceActionState();
        MaterialReconButton.IsEnabled = row is not null && _currentPackagePath is not null &&
                                        row.Kind == AssetKind.Material &&
                                        row.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase);
        AssetStartTextureModButton.IsEnabled = !_guidedTextureModInProgress &&
                                               row is not null &&
                                               _currentPackagePath is not null &&
                                               CanReconnoitreTexture(row.Kind, row.ClassName) &&
                                               _nativeTextureExportService is not null;
        if (row?.ExternalAsset is not null)
        {
            Log(
                $"Row selected: index={row.ExportIndex}, category={row.Kind}, class={row.ClassName}, " +
                $"object={row.ObjectPath}, use={row.Capability}.");
        }

        if (row?.ExternalAsset is { IsExportable: false })
        {
            StatusText.Text =
                row.Capability == "Scene reference"
                    ? $"{row.ClassName} is a scene-placement/reference object, not embedded mesh geometry."
                    : $"{row.ClassName} is UModel-reported metadata, not a directly exportable texture, mesh, or material.";
        }
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_umodelBridge is null || !_usingUmodelCatalogue || string.IsNullOrEmpty(_currentPackagePath))
        {
            return;
        }

        try
        {
            _umodelBridge.StartPreview(_currentPackagePath);
            Log($"UModel package viewer launched for: {_currentPackagePath}");
            StatusText.Text =
                "Opened the whole package in UModel at its first supported object. Use Page Up/Down to browse.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Package viewer launch failed: {exception}");
            MessageBox.Show(
                this,
                exception.Message,
                "Arkive preview error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void InspectAssetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetExternalSelection(out var row, out var packagePath))
        {
            return;
        }

        try
        {
            _umodelBridge!.StartAssetPreview(packagePath, row.ExternalAsset!);
            Log(
                $"UModel selected-asset viewer launched: class={row.ClassName}, " +
                $"object={row.ExternalAsset!.ObjectName}, package={packagePath}.");
            StatusText.Text = $"Opened selected {row.ClassName} {row.ExternalAsset.ObjectName} in UModel.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Selected-asset viewer launch failed: {exception}");
            MessageBox.Show(
                this,
                exception.Message,
                "Arkive selected-asset preview error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void TextureReconButton_Click(object sender, RoutedEventArgs e)
    {
        if (AssetsGrid.SelectedItem is not AssetRow row ||
            string.IsNullOrEmpty(_currentPackagePath) ||
            !CanReconnoitreTexture(row.Kind, row.ClassName))
        {
            return;
        }

        var objectName = row.ExternalAsset?.ObjectName ?? ObjectNameFromPath(row.ObjectPath);
        await RunTextureReconnaissanceAsync(
            _currentPackagePath,
            LostArkPackageName.GetDisplayName(_currentPackagePath),
            row.ExportIndex,
            row.Kind,
            row.ClassName,
            objectName,
            row.SerialOffset,
            row.SerialSize,
            "open-package Assets result");
    }

}
