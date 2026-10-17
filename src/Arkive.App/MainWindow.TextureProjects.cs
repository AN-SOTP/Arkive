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
    private async void AssetStartTextureModButton_Click(object sender, RoutedEventArgs e)
    {
        if (AssetsGrid.SelectedItem is not AssetRow row ||
            string.IsNullOrWhiteSpace(_currentPackagePath) ||
            !CanReconnoitreTexture(row.Kind, row.ClassName)) return;
        await StartGuidedTextureModAsync(new GuidedTextureSelection(
            _currentPackagePath,
            LostArkPackageName.GetDisplayName(_currentPackagePath),
            Path.GetFileName(_currentPackagePath),
            row.ExportIndex,
            row.Kind,
            row.ClassName,
            ObjectNameFromPath(row.ObjectPath),
            row.SerialOffset,
            row.SerialSize,
            row.GameMetadata.Select(item => item.DisplayName).ToArray()),
            "open-package Assets result");
    }


    private async void EntityBrowserStartTextureModButton_Click(object sender, RoutedEventArgs e)
    {
        if (EntityBrowserAssetsGrid.SelectedItem is not EntityBrowserAssetRow row ||
            !CanReconnoitreTexture(row.Kind, row.ClassName)) return;
        await StartGuidedTextureModAsync(new GuidedTextureSelection(
            row.SourcePath,
            row.PackageAlias,
            row.PhysicalPackageName,
            row.ExportIndex,
            row.Kind,
            row.ClassName,
            row.ObjectName,
            row.Match.Asset.SerialOffset,
            row.Match.Asset.SerialSize,
            row.Match.Asset.GameMetadata.Select(item => item.DisplayName).ToArray()),
            "Characters & NPCs result");
    }

    private async Task StartGuidedTextureModAsync(GuidedTextureSelection selection, string trigger)
    {
        if (_guidedTextureModInProgress || _nativeTextureExportService is null) return;
        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        UpdateLibraryActionState();
        UpdateEntityBrowserAssetActions();
        UpdateTextureReconnaissanceActionState();
        GuidedBuildProgressBar.Value = 0;
        StatusText.Text = $"Preparing an editable image for {selection.ObjectName}...";
        Log(
            $"Guided texture mod start requested: trigger={trigger}, package={selection.PackagePath}, " +
            $"alias={selection.PackageAlias}, index={selection.ExportIndex}, object={selection.ObjectName}.");
        try
        {
            var existing = await _guidedTextureModWorkspace.FindAsync(
                selection.PackagePath, selection.ObjectName, selection.ExportIndex);
            if (existing is not null)
            {
                await RefreshGuidedTextureModsAsync("existing guided project selected", existing.Manifest.ProjectId);
                ModifiedUpksTab.IsSelected = true;
                RevealFileInExplorer(existing.Manifest.EditableImagePath, "guided editable texture image");
                GuidedBuildStatusText.Text =
                    "Existing project selected in File Explorer. Open it in your editor, save it, then rebuild the UPK.";
                StatusText.Text = $"Reopened the existing texture mod for {selection.ObjectName}.";
                Log($"Guided texture mod existing project reopened: project={existing.ManifestPath}.");
                return;
            }

            var blockingMod = await FindActiveGuidedModForPackageAsync(selection.PackagePath);
            if (blockingMod is not null)
            {
                throw new NotSupportedException(
                    $"Restore '{blockingMod}' before starting another texture project from this package. " +
                    "Arkive will not use an installed modified UPK as a new project's source.");
            }

            var packageSha256 = await _baselineWorkspaceStore.GetPackageSha256Async(selection.PackagePath);
            var report = await _textureReconnaissanceService.InspectAsync(
                new TextureReconnaissanceRequest(
                    selection.PackagePath,
                    selection.PackageAlias,
                    selection.ExportIndex,
                    selection.Kind,
                    selection.ClassName,
                    selection.ObjectName,
                    selection.SerialOffset,
                    selection.SerialSize,
                    packageSha256));
            var reconEntry = await _textureReconnaissanceWorkspaceStore.SaveAsync(report);
            var nativeDirectory = _exportWorkspaceStore.CreateSessionDirectory(
                selection.PackageAlias, selection.ObjectName + "-guided-native-texture");
            var native = await _nativeTextureExportService.ExportAsync(
                new NativeTextureExportRequest(report, nativeDirectory, reconEntry.JsonPath));
            await _exportWorkspaceStore.CommitNativeTextureAsync(
                native, report, selection.InGameNames);
            if (string.IsNullOrWhiteSpace(native.Manifest.EditImageRelativePath))
            {
                throw new NotSupportedException("The native export did not produce an editable PNG.");
            }
            var sourceEditable = Path.Combine(native.OutputDirectory, native.Manifest.EditImageRelativePath);
            var friendlyName = selection.InGameNames.FirstOrDefault();
            var displayName = string.IsNullOrWhiteSpace(friendlyName)
                ? $"{selection.ObjectName} texture mod"
                : $"{friendlyName} - {selection.ObjectName}";
            var project = await _guidedTextureModWorkspace.CreateAsync(
                displayName,
                selection.PackageAlias,
                selection.PhysicalPackageName,
                selection.PackagePath,
                selection.ClassName,
                selection.ObjectName,
                selection.ExportIndex,
                native.Manifest.PixelFormat,
                native.Manifest.Width,
                native.Manifest.Height,
                native.ManifestPath,
                sourceEditable);
            await RefreshExportWorkspaceAsync("guided native texture export completed");
            await RefreshGuidedTextureModsAsync("guided texture project created", project.Manifest.ProjectId);
            ModifiedUpksTab.IsSelected = true;
            RevealFileInExplorer(project.Manifest.EditableImagePath, "guided editable texture image");
            GuidedBuildStatusText.Text =
                "Editable image selected in File Explorer. Open it in your editor, paint normally, save it, then rebuild the UPK.";
            StatusText.Text = $"Texture mod ready for editing: {selection.ObjectName}.";
            Log(
                $"Guided texture mod project created: projectId={project.Manifest.ProjectId}, " +
                $"manifest={project.ManifestPath}, nativeManifest={native.ManifestPath}, " +
                $"editable={project.Manifest.EditableImagePath}, format={native.Manifest.PixelFormat}, " +
                $"dimensions={native.Manifest.Width}x{native.Manifest.Height}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            StatusText.Text = "Arkive could not start the guided texture mod.";
            Log($"Guided texture mod start failed: object={selection.ObjectName}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive texture mod", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
            UpdateLibraryActionState();
            UpdateEntityBrowserAssetActions();
            UpdateTextureReconnaissanceActionState();
        }
    }

    private async Task<string?> FindActiveGuidedModForPackageAsync(string packagePath)
    {
        var fullPackagePath = Path.GetFullPath(packagePath);
        foreach (var package in await _guidedTexturePackageWorkspace.LoadAsync())
        {
            if (!Path.GetFullPath(package.Manifest.SourcePackagePath)
                    .Equals(fullPackagePath, StringComparison.OrdinalIgnoreCase)) continue;
            var latest = package.Manifest.LatestRevisionId is null
                ? package.Manifest.Revisions.LastOrDefault()
                : package.Manifest.Revisions.FirstOrDefault(revision => revision.RevisionId.Equals(
                    package.Manifest.LatestRevisionId, StringComparison.OrdinalIgnoreCase));
            if (latest is null || !File.Exists(latest.ModBundleManifestPath)) continue;
            try
            {
                var inspection = await _modDeploymentService.InspectAsync(
                    CreateModDeploymentRequest(latest.ModBundleManifestPath));
                if (inspection.CanRestore ||
                    inspection.State is ModInstallationState.Installed or ModInstallationState.Prepared ||
                    inspection.Receipt is { GameFilesModified: true })
                {
                    Log(
                        $"Guided texture-mod source guard found an active cumulative package: " +
                        $"packageId={package.Manifest.PackageProjectId}, alias={package.Manifest.PackageAlias}, " +
                        $"assets={latest.Assets.Count}, state={inspection.State}, target={inspection.TargetPath}, " +
                        $"targetSha={inspection.CurrentTargetSha256 ?? "missing"}.");
                    return package.Manifest.PackageAlias + " texture pack";
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Log(
                    $"Guided texture-mod source guard could not inspect package " +
                    $"{package.Manifest.PackageProjectId}: {exception.Message}");
            }
        }
        foreach (var project in await _guidedTextureModWorkspace.LoadAsync())
        {
            if (!Path.GetFullPath(project.Manifest.SourcePackagePath)
                    .Equals(fullPackagePath, StringComparison.OrdinalIgnoreCase)) continue;
            var latest = project.Manifest.LatestRevisionId is null
                ? project.Manifest.Revisions.LastOrDefault()
                : project.Manifest.Revisions.FirstOrDefault(revision => revision.RevisionId.Equals(
                    project.Manifest.LatestRevisionId, StringComparison.OrdinalIgnoreCase));
            if (latest is null || !File.Exists(latest.ModBundleManifestPath)) continue;
            try
            {
                var inspection = await _modDeploymentService.InspectAsync(
                    CreateModDeploymentRequest(latest.ModBundleManifestPath));
                if (inspection.CanRestore ||
                    inspection.State is ModInstallationState.Installed or ModInstallationState.Prepared ||
                    inspection.Receipt is { GameFilesModified: true })
                {
                    Log(
                        $"Guided texture-mod source guard found an active package modification: " +
                        $"projectId={project.Manifest.ProjectId}, displayName={project.Manifest.DisplayName}, " +
                        $"state={inspection.State}, target={inspection.TargetPath}, " +
                        $"targetSha={inspection.CurrentTargetSha256 ?? "missing"}.");
                    return project.Manifest.DisplayName;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Log(
                    $"Guided texture-mod source guard could not inspect project " +
                    $"{project.Manifest.ProjectId}: {exception.Message}");
            }
        }
        return null;
    }

    private async void LibraryTextureReconButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is not LibraryAssetRow row ||
            !CanReconnoitreTexture(row.Kind, row.ClassName))
        {
            return;
        }

        await RunTextureReconnaissanceAsync(
            row.SourcePath,
            row.PackageAlias,
            row.ExportIndex,
            row.Kind,
            row.ClassName,
            row.ObjectName,
            row.SerialOffset,
            row.SerialSize,
            "Library result");
    }

}
