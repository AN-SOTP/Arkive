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
    private void TextureReplacementOpenLayoutButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextureCandidateFile(_selectedTextureLayoutConfirmationPath, "texture layout report");

    private void TextureReplacementOpenPayloadButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextureCandidateFile(_selectedTextureReplacementPayloadPath, "texture payload manifest");

    private void TextureReplacementOpenAssemblyButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextureCandidateFile(_selectedTextureExportAssemblyPath, "texture export-assembly manifest");

    private void TextureReplacementOpenRewritePlanButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextureCandidateFile(_selectedPackageRewritePlanPath, "package rewrite-plan manifest");

    private void TextureReplacementOpenLogicalImageButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextureCandidateFile(_selectedLogicalPackageImagePath, "logical package-image manifest");

    private void TextureReplacementOpenPhysicalPlanButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextureCandidateFile(_selectedPhysicalPackagePlanPath, "physical package-plan manifest");

    private void TextureReplacementOpenNoOpProofButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextureCandidateFile(_selectedNoOpUpkReconstructionPath, "no-op UPK reconstruction manifest");

    private void TextureReplacementOpenStagedUpkManifestButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextureCandidateFile(_selectedReplacementUpkBuildPath, "replacement staging-UPK manifest");

    private async void TextureReplacementBrowseStagedUpkButton_Click(object sender, RoutedEventArgs e) =>
        await InspectStagedReplacementUpkAsync(openSelectedAsset: false);

    private async void TextureReplacementInspectStagedAssetButton_Click(object sender, RoutedEventArgs e) =>
        await InspectStagedReplacementUpkAsync(openSelectedAsset: true);

    private void TextureReplacementOpenModBundleButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextureCandidateFile(_selectedModBundlePath, "Arkive mod manifest");

    private void TextureReplacementOpenReadinessButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextureCandidateFile(_selectedDeploymentReadinessPath, "deployment rehearsal report");

    private void TextureReplacementOpenModFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_selectedModBundlePath))
        {
            OpenFolder(Path.GetDirectoryName(_selectedModBundlePath)!, "mod bundle folder");
        }
    }

    private void TextureReplacementOpenInstallReceiptButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextureCandidateFile(_selectedModInstallationReceiptPath, "Arkive installation receipt");

    private void TextureReplacementOpenBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_selectedModBackupPath))
        {
            OpenFolder(Path.GetDirectoryName(_selectedModBackupPath)!, "verified package backup folder");
        }
    }

    private async void TextureReplacementInstallModButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textureReplacementInProgress ||
            string.IsNullOrWhiteSpace(_selectedModBundlePath) ||
            ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow row)
        {
            return;
        }

        ArkiveModDeploymentRequest request;
        ModDeploymentInspection preflight;
        try
        {
            request = CreateModDeploymentRequest(_selectedModBundlePath);
            preflight = await _modDeploymentService.InspectAsync(request);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log($"Mod install preflight failed: bundle={_selectedModBundlePath}, error={exception}");
            MessageBox.Show(
                this, exception.Message, "Arkive mod installation",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!preflight.CanInstall)
        {
            MessageBox.Show(
                this, preflight.Summary, "Arkive mod installation",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Install '{preflight.Bundle.DisplayName}'?\n\n" +
            $"Arkive will modify this game package:\n{preflight.TargetPath}\n\n" +
            $"The verified original will be retained at:\n{preflight.BackupPath}\n\n" +
            "Close Lost Ark before continuing. Client updates or integrity checks may reject modified packages; " +
            "use Restore original before repairing or updating the game.",
            "Install Arkive mod",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            Log(
                $"Mod install cancelled by user: modId={preflight.Bundle.ModId}, " +
                $"target={preflight.TargetPath}.");
            return;
        }

        _textureReplacementInProgress = true;
        TextureReplacementInstallModButton.IsEnabled = false;
        TextureReplacementRestoreOriginalButton.IsEnabled = false;
        StatusText.Text = $"Backing up and installing {preflight.Bundle.DisplayName}...";
        Log(
            $"Controlled mod install started: modId={preflight.Bundle.ModId}, " +
            $"bundle={preflight.BundleManifestPath}, target={preflight.TargetPath}, " +
            $"targetSha={preflight.CurrentTargetSha256}, payload={preflight.PayloadPath}, " +
            $"payloadSha={preflight.Bundle.PayloadSha256}, backup={preflight.BackupPath}, " +
            $"receipt={preflight.ReceiptPath}. User confirmed the on-disk package replacement.");
        try
        {
            var result = await _modDeploymentService.InstallAsync(request);
            Log(
                $"Controlled mod install completed in {result.Duration.TotalSeconds:N3}s: " +
                $"installationId={result.Receipt.InstallationId}, modId={result.Receipt.ModId}, " +
                $"state={result.Inspection.State}, changedTarget={result.ChangedTarget}, " +
                $"recovered={result.RecoveredInterruptedOperation}, target={result.Receipt.TargetPath}, " +
                $"targetSha={result.Inspection.CurrentTargetSha256}, backup={result.Receipt.BackupPath}, " +
                $"backupVerified={result.Receipt.BackupVerified}, receipt={result.Inspection.ReceiptPath}, " +
                $"gameFilesModified={result.Receipt.GameFilesModified}.");
            foreach (var check in result.Receipt.Checks)
            {
                Log(
                    $"Mod install check: status={check.Status}, code={check.Code}, message={check.Message}");
            }
            StatusText.Text = result.RecoveredInterruptedOperation
                ? "The interrupted installation was recovered and verified. Restore remains available."
                : "Mod installed and verified. The original package backup and restore receipt are ready.";
            MessageBox.Show(
                this,
                $"{result.Receipt.DisplayName} is installed.\n\n" +
                $"Original backup:\n{result.Receipt.BackupPath}\n\n" +
                "Keep the bundle and receipt. Use Restore original before a client repair or update.",
                "Arkive mod installed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Mod installation was refused or failed. Review the message and session log.";
            Log(
                $"Controlled mod install failed: bundle={request.BundleManifestPath}, " +
                $"target={preflight.TargetPath}, backup={preflight.BackupPath}, error={exception}");
            MessageBox.Show(
                this, exception.Message, "Arkive mod installation",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _textureReplacementInProgress = false;
            if (ExportWorkspaceGrid.SelectedItem is ExportWorkspaceRow selected &&
                selected.Entry.Manifest.ExportId.Equals(
                    row.Entry.Manifest.ExportId, StringComparison.OrdinalIgnoreCase))
            {
                await LoadTextureReplacementDetailsAsync(selected);
            }
            UpdateExportWorkspaceActionState();
        }
    }

    private async void TextureReplacementRestoreOriginalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textureReplacementInProgress ||
            string.IsNullOrWhiteSpace(_selectedModBundlePath) ||
            ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow row)
        {
            return;
        }

        ArkiveModDeploymentRequest request;
        ModDeploymentInspection preflight;
        try
        {
            request = CreateModDeploymentRequest(_selectedModBundlePath);
            preflight = await _modDeploymentService.InspectAsync(request);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log($"Mod restore preflight failed: bundle={_selectedModBundlePath}, error={exception}");
            MessageBox.Show(
                this, exception.Message, "Arkive original-package restore",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!preflight.CanRestore)
        {
            MessageBox.Show(
                this, preflight.Summary, "Arkive original-package restore",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Restore the original package for '{preflight.Bundle.DisplayName}'?\n\n" +
            $"Installed mod package:\n{preflight.TargetPath}\n\n" +
            $"Verified original backup:\n{preflight.BackupPath}\n\n" +
            "Arkive will refuse if the target has changed since this mod was installed.",
            "Restore original package",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            Log(
                $"Mod restore cancelled by user: modId={preflight.Bundle.ModId}, " +
                $"target={preflight.TargetPath}.");
            return;
        }

        _textureReplacementInProgress = true;
        TextureReplacementInstallModButton.IsEnabled = false;
        TextureReplacementRestoreOriginalButton.IsEnabled = false;
        StatusText.Text = $"Restoring and verifying the original package for {preflight.Bundle.DisplayName}...";
        Log(
            $"Controlled original restore started: modId={preflight.Bundle.ModId}, " +
            $"bundle={preflight.BundleManifestPath}, target={preflight.TargetPath}, " +
            $"targetSha={preflight.CurrentTargetSha256}, backup={preflight.BackupPath}, " +
            $"receipt={preflight.ReceiptPath}. User confirmed the on-disk package restoration.");
        try
        {
            var result = await _modDeploymentService.RestoreAsync(request);
            Log(
                $"Controlled original restore completed in {result.Duration.TotalSeconds:N3}s: " +
                $"installationId={result.Receipt.InstallationId}, modId={result.Receipt.ModId}, " +
                $"state={result.Inspection.State}, changedTarget={result.ChangedTarget}, " +
                $"recovered={result.RecoveredInterruptedOperation}, target={result.Receipt.TargetPath}, " +
                $"targetSha={result.Inspection.CurrentTargetSha256}, backup={result.Receipt.BackupPath}, " +
                $"receipt={result.Inspection.ReceiptPath}, gameFilesModified={result.Receipt.GameFilesModified}.");
            foreach (var check in result.Receipt.Checks)
            {
                Log(
                    $"Mod restore check: status={check.Status}, code={check.Code}, message={check.Message}");
            }
            StatusText.Text = "The original package was restored and verified. This mod can be installed again.";
            MessageBox.Show(
                this,
                $"The original package was restored and verified.\n\n{result.Receipt.TargetPath}\n\n" +
                "Arkive retained the content-addressed backup and receipt for audit and future reinstall use.",
                "Arkive original restored",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Original-package restore was refused or failed. Review the message and session log.";
            Log(
                $"Controlled original restore failed: bundle={request.BundleManifestPath}, " +
                $"target={preflight.TargetPath}, backup={preflight.BackupPath}, error={exception}");
            MessageBox.Show(
                this, exception.Message, "Arkive original-package restore",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _textureReplacementInProgress = false;
            if (ExportWorkspaceGrid.SelectedItem is ExportWorkspaceRow selected &&
                selected.Entry.Manifest.ExportId.Equals(
                    row.Entry.Manifest.ExportId, StringComparison.OrdinalIgnoreCase))
            {
                await LoadTextureReplacementDetailsAsync(selected);
            }
            UpdateExportWorkspaceActionState();
        }
    }

    private ArkiveModDeploymentRequest CreateModDeploymentRequest(string bundleManifestPath) =>
        new(
            bundleManifestPath,
            Path.Combine(_projectRoot, "backups", "packages"),
            Path.Combine(_projectRoot, "backups", "installations"));

    private async Task InspectStagedReplacementUpkAsync(bool openSelectedAsset)
    {
        if (_umodelBridge is null || string.IsNullOrWhiteSpace(_selectedReplacementUpkBuildPath))
        {
            StatusText.Text = "UModel or the staged-UPK manifest is not available.";
            return;
        }

        TextureReplacementBrowseStagedUpkButton.IsEnabled = false;
        TextureReplacementInspectStagedAssetButton.IsEnabled = false;
        var action = openSelectedAsset ? "selected-asset" : "whole-package";
        string? nativeValidationDirectory = null;
        StatusText.Text = "Revalidating the staged UPK and checking it with UModel...";
        Log(
            $"Staged-UPK UModel inspection started: mode={action}, " +
            $"manifest={_selectedReplacementUpkBuildPath}.");
        try
        {
            var verification = await ReplacementUpkBuildService.VerifyOutputAsync(
                _selectedReplacementUpkBuildPath);
            _selectedReplacementUpkOutputPath = verification.OutputUpkPath;
            Log(
                $"Staged-UPK output reverified before UModel: buildId={verification.Manifest.BuildId}, " +
                $"path={verification.OutputUpkPath}, bytes={verification.OutputBytes}, " +
                $"sha256={verification.OutputSha256}.");

            var list = await _umodelBridge.ListAsync(verification.OutputUpkPath);
            var target = list.Assets.FirstOrDefault(asset =>
                asset.ObjectName.Equals(
                    verification.Manifest.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                asset.ClassName.Equals(
                    verification.Manifest.ClassName, StringComparison.OrdinalIgnoreCase));
            target ??= list.Assets.FirstOrDefault(asset =>
                asset.ObjectName.Equals(
                    verification.Manifest.ObjectName, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                throw new UmodelBridgeException(
                    $"UModel parsed the staged UPK but did not list the expected object " +
                    $"{verification.Manifest.ClassName} {verification.Manifest.ObjectName}.");
            }

            Log(
                $"Staged-UPK UModel preflight passed in {list.Duration.TotalSeconds:N3}s: " +
                $"assets={list.Assets.Count}, diagnostics={list.Diagnostics.Count}, " +
                $"targetIndex={target.ExportIndex}, targetClass={target.ClassName}, " +
                $"targetObject={target.ObjectName}, targetSerial=0x{target.SerialOffset:X}+0x{target.SerialSize:X}.");
            foreach (var diagnostic in list.Diagnostics)
            {
                Log($"Staged-UPK UModel diagnostic: {diagnostic}");
            }

            // -list only reads the package catalogue. Force UModel to deserialize
            // and export the replaced Texture2D before opening an interactive
            // window, otherwise a bad mip can make the viewer flash and exit.
            nativeValidationDirectory = Path.Combine(
                Path.GetTempPath(), "Arkive", "umodel-staged-validation", Guid.NewGuid().ToString("N"));
            var nativeValidation = await _umodelBridge.ExportAsync(
                verification.OutputUpkPath,
                target,
                nativeValidationDirectory,
                allowPackageWideFallback: false);
            if (nativeValidation.OutputFiles.Count == 0)
            {
                throw new UmodelBridgeException(
                    "UModel loaded the target object but did not emit a validation export.");
            }
            Log(
                $"Staged-UPK UModel native-load preflight passed in " +
                $"{nativeValidation.Duration.TotalSeconds:N3}s: object={target.ObjectName}, " +
                $"files={nativeValidation.OutputFiles.Count}, fallback={nativeValidation.UsedPackageWideFallback}.");
            foreach (var diagnostic in nativeValidation.Diagnostics)
            {
                Log($"Staged-UPK UModel native-load diagnostic: {diagnostic}");
            }

            var process = openSelectedAsset
                ? _umodelBridge.StartAssetPreview(verification.OutputUpkPath, target)
                : _umodelBridge.StartPreview(verification.OutputUpkPath);
            Log(
                $"Staged-UPK UModel viewer launched: mode={action}, processId={process.Id}, " +
                $"path={verification.OutputUpkPath}, object={target.ObjectName}.");
            StatusText.Text = openSelectedAsset
                ? $"Opened replaced texture {target.ObjectName} from the staged UPK in UModel."
                : "Opened the staged UPK in UModel. Use Page Up/Down to browse its supported objects.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "The staged UPK did not pass UModel inspection.";
            Log(
                $"Staged-UPK UModel inspection failed: mode={action}, " +
                $"manifest={_selectedReplacementUpkBuildPath}, " +
                $"output={_selectedReplacementUpkOutputPath ?? "none"}, error={exception}");
            MessageBox.Show(
                this,
                exception.Message,
                "Arkive staged-UPK inspection",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(nativeValidationDirectory) &&
                Directory.Exists(nativeValidationDirectory))
            {
                try
                {
                    Directory.Delete(nativeValidationDirectory, recursive: true);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    Log(
                        $"Could not remove staged-UPK UModel validation directory: " +
                        $"path={nativeValidationDirectory}, error={exception.Message}");
                }
            }
            var canRetry = _umodelBridge is not null &&
                           !string.IsNullOrWhiteSpace(_selectedReplacementUpkBuildPath) &&
                           File.Exists(_selectedReplacementUpkBuildPath) &&
                           !string.IsNullOrWhiteSpace(_selectedReplacementUpkOutputPath) &&
                           File.Exists(_selectedReplacementUpkOutputPath);
            TextureReplacementBrowseStagedUpkButton.IsEnabled = canRetry;
            TextureReplacementInspectStagedAssetButton.IsEnabled = canRetry;
        }
    }

    private void TextureCandidateOpenOriginalButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextureCandidateFile(
            _selectedTextureImportCandidate?.OriginalDdsPath,
            "texture candidate original DDS");

    private void TextureCandidateOpenEditedButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextureCandidateFile(
            _selectedTextureImportCandidate?.EditedDdsPath,
            "texture candidate edited image");

    private void TextureCandidateOpenFinalButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextureCandidateFile(
            _selectedTextureImportCandidatePath,
            "texture candidate final DDS");

    private void OpenTextureCandidateFile(string? path, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText.Text = $"The {purpose} is no longer available.";
            Log($"Could not open {purpose}: missing path={path ?? "none"}.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.GetFullPath(path),
                UseShellExecute = true,
            });
            Log($"Opened {purpose}: {Path.GetFullPath(path)}");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"Could not open {purpose}: {exception.Message}";
            Log($"Could not open {purpose}: path={path}, error={exception}");
        }
    }

}
