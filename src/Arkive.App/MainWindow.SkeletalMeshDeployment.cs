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
    private async void BuildStagedSkeletalMeshUpkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_skeletalMeshEditableWorkspaceBusy ||
            SkeletalMeshEditedGlbsGrid.SelectedItem is not SkeletalMeshEditedGlbRow edit) return;
        if (edit.Entry.Manifest.Preflight.Readiness ==
            SkeletalMeshEditReadiness.ReadyForTopologyCompilation)
        {
            if (!File.Exists(edit.TopologyCompilationManifestPath)) return;
            await BuildTopologyStagedSkeletalMeshUpkAsync(edit);
            return;
        }
        if (!File.Exists(edit.NativeEncodingManifestPath)) return;
        _skeletalMeshEditableWorkspaceBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        StatusText.Text = $"Building an isolated staged UPK for {edit.Entry.Manifest.ObjectName}...";
        Log($"Staged SkeletalMesh UPK build started: editId={edit.Entry.Manifest.EditId}, " +
            $"nativeManifest={edit.NativeEncodingManifestPath}, output={edit.StagedUpkDirectory}.");
        try
        {
            var result = await _skeletalMeshStagedUpkService.BuildAsync(
                new SkeletalMeshStagedUpkRequest(
                    edit.NativeEncodingManifestPath, edit.StagedUpkDirectory));
            Log($"Staged SkeletalMesh UPK build completed: buildId={result.Manifest.BuildId}, " +
                $"object={result.Manifest.PackageAlias}.{result.Manifest.ObjectName}, " +
                $"source={result.Manifest.SourcePackagePath}, output={result.OutputUpkPath}, " +
                $"sourceSha={result.Manifest.SourcePackageSha256}, outputSha={result.Manifest.OutputPackageSha256}, " +
                $"logicalSha={result.Manifest.OutputLogicalPackageSha256}, " +
                $"chunks={result.Manifest.ChunkCount}, rebuilt={result.Manifest.RebuiltChunkCount}, " +
                $"retained={result.Manifest.ByteIdenticalChunkCount}, protectedRebuilt={result.Manifest.ProtectedRebuiltChunkCount}, " +
                $"blocks={result.Manifest.EncodedBlockCount}, tableUnchanged={result.Manifest.ExportTableUnchanged}, " +
                $"unrelatedLogicalExact={result.Manifest.AllUnrelatedLogicalBytesIdentical}, " +
                $"catalogueExact={result.Manifest.AssetCatalogueEquivalent}, " +
                $"nativeDecode={result.Manifest.TargetNativeDecodePassed}, " +
                $"previewReady={result.Manifest.UmodelPreviewReady}, deploymentReady={result.Manifest.DeploymentReady}, " +
                $"manifest={result.ManifestPath}, duration={result.Duration.TotalSeconds:N3}s.");
            foreach (var chunk in result.Manifest.Chunks)
                Log($"Staged SkeletalMesh chunk {chunk.ChunkIndex}: target={chunk.ContainsTargetExport}, " +
                    $"rebuilt={chunk.Rebuilt}, logical=0x{chunk.LogicalOffset:X}+0x{chunk.LogicalBytes:X}, " +
                    $"sourcePhysical=0x{chunk.SourcePhysicalOffset:X}+0x{chunk.SourcePhysicalBytes:X}, " +
                    $"outputPhysical=0x{chunk.OutputPhysicalOffset:X}+0x{chunk.OutputPhysicalBytes:X}, " +
                    $"flags={chunk.LostArkFlags}, blocks={chunk.Blocks.Count}, sha={chunk.OutputPhysicalSha256}.");
            _skeletalMeshEditableWorkspaceBusy = false;
            DisplayEditedSkeletalMeshesForSelectedExport(edit.Entry.Manifest.EditId);
            UpdateSkeletalMeshEditableWorkspaceActionState();
            StatusText.Text = $"Verified staged UPK created for {edit.Entry.Manifest.ObjectName}.";
            MessageBox.Show(this,
                $"Verified staged SkeletalMesh UPK created.\n\n" +
                $"Rebuilt chunks: {result.Manifest.RebuiltChunkCount:N0}\n" +
                $"Byte-identical chunks: {result.Manifest.ByteIdenticalChunkCount:N0}\n" +
                $"Export table unchanged: {result.Manifest.ExportTableUnchanged}\n" +
                $"Native decode passed: {result.Manifest.TargetNativeDecodePassed}\n\n" +
                "The package is ready for UModel preview only. Arkive has not changed the game files.",
                "Arkive staged mesh UPK", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or ArgumentException or JsonException or CryptographicException)
        {
            StatusText.Text = "The staged SkeletalMesh UPK build failed; see Diagnostics.";
            Log($"Staged SkeletalMesh UPK build failed: edit={edit.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Build staged mesh UPK",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshEditableWorkspaceBusy = false;
            UpdateSkeletalMeshEditableWorkspaceActionState();
        }
    }

    private async Task BuildTopologyStagedSkeletalMeshUpkAsync(SkeletalMeshEditedGlbRow edit)
    {
        _skeletalMeshEditableWorkspaceBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        StatusText.Text = $"Appending the compiled topology mesh to an isolated UPK for {edit.Entry.Manifest.ObjectName}...";
        Log($"Topology SkeletalMesh UPK build started: editId={edit.Entry.Manifest.EditId}, " +
            $"compilationManifest={edit.TopologyCompilationManifestPath}, " +
            $"output={edit.TopologyStagedUpkDirectory}.");
        try
        {
            var result = await _skeletalMeshStagedUpkService.BuildTopologyAsync(
                new SkeletalMeshTopologyStagedUpkRequest(
                    edit.TopologyCompilationManifestPath, edit.TopologyStagedUpkDirectory));
            Log($"Topology SkeletalMesh UPK build completed: buildId={result.Manifest.BuildId}, " +
                $"object={result.Manifest.PackageAlias}.{result.Manifest.ObjectName}, " +
                $"source={result.Manifest.SourcePackagePath}, output={result.OutputUpkPath}, " +
                $"sourceSha={result.Manifest.SourcePackageSha256}, outputSha={result.Manifest.OutputPackageSha256}, " +
                $"logical={result.Manifest.SourceLogicalPackageBytes:N0}->{result.Manifest.OutputLogicalPackageBytes:N0}, " +
                $"serial={result.Manifest.OriginalSerialSize:N0}->{result.Manifest.AppendedSerialSize:N0}, " +
                $"appendedOffset=0x{result.Manifest.AppendedSerialOffset:X}, " +
                $"padding={result.Manifest.AppendPaddingBytes}, chunks={result.Manifest.ChunkCount}, " +
                $"rebuilt={result.Manifest.RebuiltChunkCount}, retained={result.Manifest.ByteIdenticalChunkCount}, " +
                $"protectedRebuilt={result.Manifest.ProtectedRebuiltChunkCount}, blocks={result.Manifest.EncodedBlockCount}, " +
                $"tablePatched={result.Manifest.ExportTablePatched}, originalRetained={result.Manifest.OriginalExportBytesRetained}, " +
                $"unplannedSourceExact={result.Manifest.AllUnplannedSourceBytesIdentical}, " +
                $"unrelatedExportsExact={result.Manifest.UnrelatedExportsEquivalent}, " +
                $"nativeDecode={result.Manifest.TargetNativeDecodePassed}, " +
                $"previewReady={result.Manifest.UmodelPreviewReady}, deploymentReady={result.Manifest.DeploymentReady}, " +
                $"manifest={result.ManifestPath}, duration={result.Duration.TotalSeconds:N3}s.");
            foreach (var patch in result.Manifest.ExportTablePatches)
                Log($"Topology export-table patch: export={patch.ExportIndex}, field={patch.Field}, " +
                    $"logical=0x{patch.LogicalOffset:X}, source={patch.SourceValue}, output={patch.OutputValue}.");
            foreach (var chunk in result.Manifest.Chunks)
                Log($"Topology staged chunk {chunk.ChunkIndex}: affected={chunk.ContainsTargetExport}, " +
                    $"rebuilt={chunk.Rebuilt}, logical=0x{chunk.LogicalOffset:X}+0x{chunk.LogicalBytes:X}, " +
                    $"sourcePhysical=0x{chunk.SourcePhysicalOffset:X}+0x{chunk.SourcePhysicalBytes:X}, " +
                    $"outputPhysical=0x{chunk.OutputPhysicalOffset:X}+0x{chunk.OutputPhysicalBytes:X}, " +
                    $"flags={chunk.LostArkFlags}, blocks={chunk.Blocks.Count}, sha={chunk.OutputPhysicalSha256}.");
            _skeletalMeshEditableWorkspaceBusy = false;
            DisplayEditedSkeletalMeshesForSelectedExport(edit.Entry.Manifest.EditId);
            UpdateSkeletalMeshEditableWorkspaceActionState();
            StatusText.Text = $"Verified variable-size staged UPK created for {edit.Entry.Manifest.ObjectName}.";
            MessageBox.Show(this,
                $"Verified variable-size SkeletalMesh UPK created.\n\n" +
                $"Serialized export: {result.Manifest.OriginalSerialSize:N0} → {result.Manifest.AppendedSerialSize:N0} bytes\n" +
                $"Rebuilt chunks: {result.Manifest.RebuiltChunkCount:N0}\n" +
                $"Byte-identical chunks: {result.Manifest.ByteIdenticalChunkCount:N0}\n" +
                $"Unrelated exports unchanged: {result.Manifest.UnrelatedExportsEquivalent}\n" +
                $"Native decode passed: {result.Manifest.TargetNativeDecodePassed}\n\n" +
                "The package is ready for UModel preview. Installation remains disabled until this new writer passes a real-package acceptance test.",
                "Arkive topology staged UPK", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or ArgumentException or JsonException or CryptographicException)
        {
            StatusText.Text = "The variable-size SkeletalMesh UPK build failed; see Diagnostics.";
            Log($"Topology SkeletalMesh UPK build failed: edit={edit.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Build topology staged UPK",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshEditableWorkspaceBusy = false;
            UpdateSkeletalMeshEditableWorkspaceActionState();
        }
    }

    private async void PreviewStagedSkeletalMeshUpkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_umodelBridge is null || _skeletalMeshEditableWorkspaceBusy ||
            SkeletalMeshEditedGlbsGrid.SelectedItem is not SkeletalMeshEditedGlbRow edit) return;
        var useTopology = File.Exists(edit.TopologyStagedUpkManifestPath) &&
                          edit.TopologyStagedUpkOutputExists;
        if (!useTopology && (!File.Exists(edit.StagedUpkManifestPath) || !edit.StagedUpkOutputExists))
            return;
        var selectedManifestPath = useTopology
            ? edit.TopologyStagedUpkManifestPath
            : edit.StagedUpkManifestPath;
        var selectedDirectory = useTopology
            ? edit.TopologyStagedUpkDirectory
            : edit.StagedUpkDirectory;
        _skeletalMeshEditableWorkspaceBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        var validationDirectory = Path.Combine(
            Path.GetTempPath(), "Arkive", "umodel-staged-mesh-validation", Guid.NewGuid().ToString("N"));
        StatusText.Text = $"Revalidating and loading {edit.Entry.Manifest.ObjectName} in UModel...";
        Log($"Staged SkeletalMesh UModel preview started: topology={useTopology}, " +
            $"manifest={selectedManifestPath}.");
        try
        {
            string outputUpkPath;
            string objectName;
            string sourcePackagePath;
            string buildId;
            if (useTopology)
            {
                var verification = await SkeletalMeshStagedUpkService.VerifyTopologyOutputAsync(
                    selectedManifestPath);
                outputUpkPath = verification.OutputUpkPath;
                objectName = verification.Manifest.ObjectName;
                sourcePackagePath = verification.Manifest.SourcePackagePath;
                buildId = verification.Manifest.BuildId;
            }
            else
            {
                var verification = await SkeletalMeshStagedUpkService.VerifyOutputAsync(
                    selectedManifestPath);
                outputUpkPath = verification.OutputUpkPath;
                objectName = verification.Manifest.ObjectName;
                sourcePackagePath = verification.Manifest.SourcePackagePath;
                buildId = verification.Manifest.BuildId;
            }
            var list = await _umodelBridge.ListAsync(outputUpkPath);
            var target = list.Assets.FirstOrDefault(asset =>
                asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase) &&
                asset.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
            if (target is null)
                throw new UmodelBridgeException(
                    $"UModel parsed the staged package but did not list SkeletalMesh {objectName}.");
            var sourceRoot = Path.GetDirectoryName(sourcePackagePath)!;
            var nativeLoad = await _umodelBridge.ExportInPackageRootAsync(
                outputUpkPath, target, validationDirectory, sourceRoot,
                allowPackageWideFallback: false);
            var targetValidationFiles = nativeLoad.OutputFiles.Where(path =>
                Path.GetFileNameWithoutExtension(path).Equals(
                    target.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (targetValidationFiles.Length == 0)
                throw new UmodelBridgeException(
                    "UModel loaded the staged package but did not export the target SkeletalMesh as glTF.");
            Log($"Staged SkeletalMesh UModel native-load passed: topology={useTopology}, buildId={buildId}, " +
                $"path={outputUpkPath}, object={target.ObjectName}, targetIndex={target.ExportIndex}, " +
                $"assets={list.Assets.Count}, validationFiles={nativeLoad.OutputFiles.Count}, " +
                $"targetValidationFiles={targetValidationFiles.Length}, " +
                $"listSeconds={list.Duration.TotalSeconds:N3}, exportSeconds={nativeLoad.Duration.TotalSeconds:N3}.");
            foreach (var diagnostic in list.Diagnostics)
                Log($"Staged SkeletalMesh UModel list diagnostic: {diagnostic}");
            foreach (var diagnostic in nativeLoad.Diagnostics)
                Log($"Staged SkeletalMesh UModel export diagnostic: {diagnostic}");
            var logPath = Path.Combine(selectedDirectory, "umodel-staged-mesh-preview.log");
            var process = _umodelBridge.StartAssetPreviewInPackageRoot(
                outputUpkPath, target, sourceRoot, logPath);
            Log($"Staged SkeletalMesh UModel viewer launched: processId={process.Id}, " +
                $"topology={useTopology}, path={outputUpkPath}, object={target.ObjectName}, " +
                $"searchRoot={sourceRoot}, log={logPath}.");
            StatusText.Text = $"Opened modified {target.ObjectName} from the staged UPK in UModel.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or ArgumentException or JsonException or
                System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "The staged SkeletalMesh did not pass UModel preview validation.";
            Log($"Staged SkeletalMesh UModel preview failed: topology={useTopology}, " +
                $"manifest={selectedManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Preview staged mesh in UModel",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (Directory.Exists(validationDirectory))
            {
                try { Directory.Delete(validationDirectory, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Log($"Could not remove staged SkeletalMesh UModel validation directory: " +
                        $"path={validationDirectory}, error={exception.Message}");
                }
            }
            _skeletalMeshEditableWorkspaceBusy = false;
            UpdateSkeletalMeshEditableWorkspaceActionState();
        }
    }

    private void OpenStagedSkeletalMeshUpkButton_Click(object sender, RoutedEventArgs e)
    {
        if (SkeletalMeshEditedGlbsGrid.SelectedItem is not SkeletalMeshEditedGlbRow row) return;
        if (Directory.Exists(row.TopologyStagedUpkDirectory))
            OpenFolder(row.TopologyStagedUpkDirectory, "topology staged SkeletalMesh UPK");
        else if (Directory.Exists(row.StagedUpkDirectory))
            OpenFolder(row.StagedUpkDirectory, "staged SkeletalMesh UPK");
    }

    private async void PrepareSkeletalMeshModButton_Click(object sender, RoutedEventArgs e)
    {
        if (_meshModBundleService is null || _skeletalMeshDeploymentBusy ||
            SkeletalMeshEditedGlbsGrid.SelectedItem is not SkeletalMeshEditedGlbRow edit ||
            !File.Exists(edit.StagedUpkManifestPath)) return;

        if (Directory.Exists(edit.MeshDeploymentDirectory) &&
            Directory.EnumerateFileSystemEntries(edit.MeshDeploymentDirectory).Any())
        {
            if (File.Exists(edit.MeshBundleManifestPath))
            {
                try
                {
                    var existing = await _modDeploymentService.InspectAsync(
                        CreateModDeploymentRequest(edit.MeshBundleManifestPath));
                    if (!existing.CanInstall)
                    {
                        MessageBox.Show(this,
                            $"Arkive cannot replace this bundle in its current state.\n\n{existing.Summary}\n\n" +
                            "Restore the original package first if this mesh mod is installed.",
                            "Prepare mesh mod", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or NotSupportedException or
                        ArgumentException or JsonException)
                {
                    Log($"Existing mesh bundle could not be safely replaced: " +
                        $"bundle={edit.MeshBundleManifestPath}, error={exception}");
                    MessageBox.Show(this,
                        "The existing mesh bundle cannot be verified, so Arkive will not overwrite it automatically.\n\n" +
                        exception.Message,
                        "Prepare mesh mod", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            if (MessageBox.Show(this,
                    "Replace the existing prepared mesh bundle?\n\n" +
                    "This removes only Arkive's staged bundle. The edited GLB, native candidate, staged UPK, " +
                    "source package, and verified backups remain untouched.",
                    "Prepare mesh mod", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) != MessageBoxResult.Yes) return;
            Directory.Delete(edit.MeshDeploymentDirectory, recursive: true);
        }

        _skeletalMeshDeploymentBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        StatusText.Text = $"Preparing and externally validating the mesh mod for {edit.Entry.Manifest.ObjectName}...";
        SkeletalMeshDeploymentStatusText.Text = "Preparing bundle: revalidating source, staged UPK, UModel export, and backup capacity...";
        Log($"Mesh mod-bundle preparation started: editId={edit.Entry.Manifest.EditId}, " +
            $"stagedManifest={edit.StagedUpkManifestPath}, output={edit.MeshDeploymentDirectory}.");
        try
        {
            var result = await _meshModBundleService.BuildAsync(
                new ArkiveMeshModBundleRequest(
                    edit.StagedUpkManifestPath,
                    edit.MeshDeploymentDirectory,
                    Path.Combine(_projectRoot, "backups", "packages"),
                    $"{edit.Entry.Manifest.ObjectName} mesh mod"));
            Log($"Mesh mod-bundle preparation completed in {result.Duration.TotalSeconds:N3}s: " +
                $"modId={result.Bundle.ModId}, source={result.Readiness.SourcePackagePath}, " +
                $"sourceSha={result.Bundle.RequiredSourcePackageSha256}, payload={result.PayloadPath}, " +
                $"payloadSha={result.Bundle.PayloadSha256}, target={result.Readiness.InstallTargetPath}, " +
                $"backup={result.Readiness.ProposedBackupPath}, external={result.Readiness.ExternalValidator}, " +
                $"assets={result.Readiness.ExternalAssetCount:N0}, files={result.Readiness.ExternalValidationFileCount:N0}, " +
                $"ready={result.Readiness.ReadyForControlledInstall}, gameFilesModified={result.Readiness.GameFilesModified}.");
            foreach (var check in result.Readiness.Checks)
                Log($"Mesh mod-bundle check: status={check.Status}, code={check.Code}, message={check.Message}");
            StatusText.Text = $"Mesh mod prepared for {edit.Entry.Manifest.ObjectName}; no game files were changed.";
            MessageBox.Show(this,
                $"The single-mesh mod bundle is prepared and installable.\n\n" +
                $"Object: {edit.Entry.Manifest.ObjectName}\n" +
                $"Payload: {result.PayloadPath}\n\n" +
                "Arkive revalidated the source and staged package, exported the exact mesh through UModel, " +
                "and confirmed backup capacity. No game files were changed.",
                "Arkive mesh mod prepared", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is ArkiveMeshModBundleRefusalException or IOException or
                UnauthorizedAccessException or NotSupportedException or ArgumentException or
                InvalidDataException or JsonException)
        {
            StatusText.Text = "Mesh mod preparation was refused or failed; see Diagnostics.";
            SkeletalMeshDeploymentStatusText.Text = "Preparation failed. The game package was not changed.";
            Log($"Mesh mod-bundle preparation failed: staged={edit.StagedUpkManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive mesh mod preparation",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshDeploymentBusy = false;
            DisplayEditedSkeletalMeshesForSelectedExport(edit.Entry.Manifest.EditId);
            await RefreshModifiedMeshUpksAsync(
                "mesh bundle preparation completed", edit.Entry.Manifest.EditId);
            UpdateSkeletalMeshEditableWorkspaceActionState();
        }
    }

    private async void InstallSkeletalMeshModButton_Click(object sender, RoutedEventArgs e)
    {
        if (_skeletalMeshDeploymentBusy ||
            SkeletalMeshEditedGlbsGrid.SelectedItem is not SkeletalMeshEditedGlbRow edit ||
            !File.Exists(edit.MeshBundleManifestPath)) return;
        var request = CreateModDeploymentRequest(edit.MeshBundleManifestPath);
        ModDeploymentInspection preflight;
        try
        {
            preflight = await _modDeploymentService.InspectAsync(request);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or JsonException)
        {
            Log($"Mesh mod install preflight failed: bundle={edit.MeshBundleManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive mesh mod installation",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (!preflight.CanInstall)
        {
            MessageBox.Show(this, preflight.Summary, "Arkive mesh mod installation",
                MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshSelectedSkeletalMeshDeploymentAsync("install preflight refused");
            return;
        }
        if (MessageBox.Show(this,
                $"Install '{preflight.Bundle.DisplayName}'?\n\n" +
                $"Arkive will modify this game package:\n{preflight.TargetPath}\n\n" +
                $"A byte-exact original will be retained at:\n{preflight.BackupPath}\n\n" +
                "Close Lost Ark, its launcher, and Easy Anti-Cheat before continuing. " +
                "Restore the original before repairing or updating the client.",
                "Install Arkive mesh mod", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;

        _skeletalMeshDeploymentBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        StatusText.Text = $"Backing up and installing {preflight.Bundle.DisplayName}...";
        Log($"Controlled mesh mod install started: modId={preflight.Bundle.ModId}, " +
            $"bundle={preflight.BundleManifestPath}, target={preflight.TargetPath}, " +
            $"targetSha={preflight.CurrentTargetSha256}, payload={preflight.PayloadPath}, " +
            $"payloadSha={preflight.Bundle.PayloadSha256}, backup={preflight.BackupPath}. " +
            "User confirmed the on-disk package replacement.");
        try
        {
            var result = await _modDeploymentService.InstallAsync(request);
            Log($"Controlled mesh mod install completed in {result.Duration.TotalSeconds:N3}s: " +
                $"installationId={result.Receipt.InstallationId}, state={result.Inspection.State}, " +
                $"changedTarget={result.ChangedTarget}, recovered={result.RecoveredInterruptedOperation}, " +
                $"targetSha={result.Inspection.CurrentTargetSha256}, backupVerified={result.Receipt.BackupVerified}, " +
                $"receipt={result.Inspection.ReceiptPath}, gameFilesModified={result.Receipt.GameFilesModified}.");
            foreach (var check in result.Receipt.Checks)
                Log($"Mesh mod install check: status={check.Status}, code={check.Code}, message={check.Message}");
            StatusText.Text = "Mesh mod installed and verified; Restore original is ready.";
            MessageBox.Show(this,
                $"{result.Receipt.DisplayName} is installed.\n\n" +
                $"Original backup:\n{result.Receipt.BackupPath}\n\n" +
                "Use Restore original before a client repair or update.",
                "Arkive mesh mod installed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or System.ComponentModel.Win32Exception or JsonException)
        {
            StatusText.Text = "Mesh mod installation was refused or failed; review the session log.";
            Log($"Controlled mesh mod install failed: bundle={request.BundleManifestPath}, " +
                $"target={preflight.TargetPath}, backup={preflight.BackupPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive mesh mod installation",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshDeploymentBusy = false;
            await RefreshSelectedSkeletalMeshDeploymentAsync("mesh install completed");
            await RefreshModifiedMeshUpksAsync("mesh install completed", edit.Entry.Manifest.EditId);
            await RefreshGuidedTextureModsAsync("mesh install changed target");
        }
    }

    private async void RestoreSkeletalMeshModButton_Click(object sender, RoutedEventArgs e)
    {
        if (_skeletalMeshDeploymentBusy ||
            SkeletalMeshEditedGlbsGrid.SelectedItem is not SkeletalMeshEditedGlbRow edit ||
            !File.Exists(edit.MeshBundleManifestPath)) return;
        var request = CreateModDeploymentRequest(edit.MeshBundleManifestPath);
        ModDeploymentInspection preflight;
        try
        {
            preflight = await _modDeploymentService.InspectAsync(request);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or JsonException)
        {
            Log($"Mesh original restore preflight failed: bundle={edit.MeshBundleManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive mesh original restore",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (!preflight.CanRestore)
        {
            MessageBox.Show(this, preflight.Summary, "Arkive mesh original restore",
                MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshSelectedSkeletalMeshDeploymentAsync("restore preflight refused");
            return;
        }
        if (MessageBox.Show(this,
                $"Restore the original package for '{preflight.Bundle.DisplayName}'?\n\n" +
                $"Installed mesh-mod package:\n{preflight.TargetPath}\n\n" +
                $"Verified original backup:\n{preflight.BackupPath}\n\n" +
                "Arkive will refuse if the installed package no longer matches this mesh mod.",
                "Restore original mesh package", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;

        _skeletalMeshDeploymentBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        StatusText.Text = $"Restoring the original package for {preflight.Bundle.DisplayName}...";
        Log($"Controlled mesh original restore started: modId={preflight.Bundle.ModId}, " +
            $"bundle={preflight.BundleManifestPath}, target={preflight.TargetPath}, " +
            $"targetSha={preflight.CurrentTargetSha256}, backup={preflight.BackupPath}. " +
            "User confirmed the on-disk package restoration.");
        try
        {
            var result = await _modDeploymentService.RestoreAsync(request);
            Log($"Controlled mesh original restore completed in {result.Duration.TotalSeconds:N3}s: " +
                $"installationId={result.Receipt.InstallationId}, state={result.Inspection.State}, " +
                $"changedTarget={result.ChangedTarget}, recovered={result.RecoveredInterruptedOperation}, " +
                $"targetSha={result.Inspection.CurrentTargetSha256}, receipt={result.Inspection.ReceiptPath}, " +
                $"gameFilesModified={result.Receipt.GameFilesModified}.");
            foreach (var check in result.Receipt.Checks)
                Log($"Mesh original restore check: status={check.Status}, code={check.Code}, message={check.Message}");
            StatusText.Text = "The original mesh package was restored and verified.";
            MessageBox.Show(this,
                $"The original package was restored byte-for-byte.\n\n{result.Receipt.TargetPath}\n\n" +
                "The verified backup and receipt were retained for audit and future reinstall use.",
                "Arkive mesh original restored", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or System.ComponentModel.Win32Exception or JsonException)
        {
            StatusText.Text = "Mesh original restore was refused or failed; review the session log.";
            Log($"Controlled mesh original restore failed: bundle={request.BundleManifestPath}, " +
                $"target={preflight.TargetPath}, backup={preflight.BackupPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive mesh original restore",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshDeploymentBusy = false;
            await RefreshSelectedSkeletalMeshDeploymentAsync("mesh restore completed");
            await RefreshModifiedMeshUpksAsync("mesh restore completed", edit.Entry.Manifest.EditId);
            await RefreshGuidedTextureModsAsync("mesh restore changed target");
        }
    }

    private async void RefreshSkeletalMeshDeploymentButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshSelectedSkeletalMeshDeploymentAsync("manual refresh");

    private void OpenSkeletalMeshDeploymentFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (SkeletalMeshEditedGlbsGrid.SelectedItem is SkeletalMeshEditedGlbRow row &&
            Directory.Exists(row.MeshDeploymentDirectory))
            OpenFolder(row.MeshDeploymentDirectory, "SkeletalMesh mod bundle");
    }

    private async Task RefreshSelectedSkeletalMeshDeploymentAsync(string trigger)
    {
        var version = ++_skeletalMeshDeploymentRefreshVersion;
        _skeletalMeshDeploymentCancellation?.Cancel();
        _skeletalMeshDeploymentCancellation?.Dispose();
        _skeletalMeshDeploymentCancellation = new CancellationTokenSource();
        var cancellationToken = _skeletalMeshDeploymentCancellation.Token;
        _skeletalMeshDeploymentBusy = false;
        _selectedSkeletalMeshDeploymentInspection = null;

        var rows = SkeletalMeshEditedGlbsGrid?.SelectedItems
            .OfType<SkeletalMeshEditedGlbRow>().ToArray() ?? [];
        if (rows.Length != 1)
        {
            if (SkeletalMeshDeploymentStatusText is not null)
                SkeletalMeshDeploymentStatusText.Text =
                    "Select exactly one edited mesh to view deployment status.";
            UpdateSkeletalMeshEditableWorkspaceActionState();
            return;
        }
        var row = rows[0];
        if (!File.Exists(row.MeshBundleManifestPath))
        {
            if (SkeletalMeshDeploymentStatusText is not null)
                SkeletalMeshDeploymentStatusText.Text = File.Exists(row.StagedUpkManifestPath)
                    ? "Staged UPK is ready. Prepare mesh mod to run deployment checks; no game files will change."
                    : "Build and preview a staged UPK before preparing this mesh mod.";
            UpdateSkeletalMeshEditableWorkspaceActionState();
            return;
        }

        _skeletalMeshDeploymentBusy = true;
        if (SkeletalMeshDeploymentStatusText is not null)
            SkeletalMeshDeploymentStatusText.Text = "Checking source, payload, backup, receipt, and install state...";
        UpdateSkeletalMeshEditableWorkspaceActionState();
        try
        {
            var inspection = await _modDeploymentService.InspectAsync(
                CreateModDeploymentRequest(row.MeshBundleManifestPath), cancellationToken);
            if (version != _skeletalMeshDeploymentRefreshVersion || cancellationToken.IsCancellationRequested)
                return;
            _selectedSkeletalMeshDeploymentInspection = inspection;
            if (SkeletalMeshDeploymentStatusText is not null)
                SkeletalMeshDeploymentStatusText.Text =
                    $"{inspection.State}: {inspection.Summary} Backup: " +
                    (inspection.BackupExists
                        ? inspection.BackupVerified ? "verified." : "invalid."
                        : "created during install.");
            Log($"Mesh deployment status refreshed ({trigger}): modId={inspection.Bundle.ModId}, " +
                $"object={row.Entry.Manifest.ObjectName}, state={inspection.State}, " +
                $"canInstall={inspection.CanInstall}, canRestore={inspection.CanRestore}, " +
                $"payloadVerified={inspection.PayloadVerified}, backupExists={inspection.BackupExists}, " +
                $"backupVerified={inspection.BackupVerified}, targetSha={inspection.CurrentTargetSha256}, " +
                $"summary={inspection.Summary}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or JsonException)
        {
            if (version != _skeletalMeshDeploymentRefreshVersion) return;
            if (SkeletalMeshDeploymentStatusText is not null)
                SkeletalMeshDeploymentStatusText.Text =
                    $"Deployment status unavailable: {exception.Message}";
            Log($"Mesh deployment status refresh failed ({trigger}): " +
                $"bundle={row.MeshBundleManifestPath}, error={exception}");
        }
        finally
        {
            if (version == _skeletalMeshDeploymentRefreshVersion)
            {
                _skeletalMeshDeploymentBusy = false;
                UpdateSkeletalMeshEditableWorkspaceActionState();
            }
        }
    }

    private void OpenEditedSkeletalMeshFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (SkeletalMeshEditedGlbsGrid.SelectedItem is SkeletalMeshEditedGlbRow row)
            OpenFolder(row.Entry.DirectoryPath, "registered edited SkeletalMesh GLB");
    }

    private void RevealEditedSkeletalMeshGlbButton_Click(object sender, RoutedEventArgs e)
    {
        if (SkeletalMeshEditedGlbsGrid.SelectedItem is SkeletalMeshEditedGlbRow row)
            RevealFileInExplorer(row.Entry.EditedGlbPath, "registered edited SkeletalMesh GLB");
    }

    private void CopyEditedSkeletalMeshGlbPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (SkeletalMeshEditedGlbsGrid.SelectedItem is SkeletalMeshEditedGlbRow row)
            CopyText(row.Entry.EditedGlbPath, "registered edited SkeletalMesh GLB path");
    }

    private async void DeleteEditedSkeletalMeshGlbButton_Click(object sender, RoutedEventArgs e)
    {
        if (_skeletalMeshEditableWorkspaceBusy || _skeletalMeshDeploymentBusy) return;
        var rows = SkeletalMeshEditedGlbsGrid.SelectedItems.OfType<SkeletalMeshEditedGlbRow>().ToArray();
        if (rows.Length == 0) return;
        if (!await MeshEditArtifactsCanBeDeletedAsync(rows.Select(row => row.Entry))) return;
        if (MessageBox.Show(
                this,
                $"Delete {rows.Length:N0} selected edited GLB registration(s)?\n\n" +
                "The clean export, reconnaissance evidence, and source UPK will remain untouched.",
                "Delete edited GLBs", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var sourceId = rows[0].Entry.Manifest.SourceExportId;
        _skeletalMeshEditableWorkspaceBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        try
        {
            foreach (var row in rows)
            {
                await _skeletalMeshEditWorkspaceStore.DeleteAsync(row.Entry);
                Log($"Edited SkeletalMesh registration deleted: editId={row.Entry.Manifest.EditId}, " +
                    $"path={row.Entry.DirectoryPath}.");
            }
            StatusText.Text = $"Deleted {rows.Length:N0} edited GLB registration(s).";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException)
        {
            Log($"Edited SkeletalMesh deletion failed: error={exception}");
            MessageBox.Show(this, exception.Message, "Delete edited GLBs",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshEditableWorkspaceBusy = false;
            await RefreshSkeletalMeshEditableWorkspaceAsync("edited GLBs deleted", sourceId);
            await RefreshModifiedMeshUpksAsync("edited GLBs deleted");
        }
    }

    private void LogSkeletalMeshEditPreflight(string trigger, SkeletalMeshEditPreflightReport report)
    {
        Log(
            $"Edited SkeletalMesh preflight completed: trigger={trigger}, sourceExport={report.SourceExportId}, " +
            $"object={report.PackageAlias}.{report.ObjectName}, readiness={report.Readiness}, " +
            $"importProfile={report.ImportProfile}, topologyChange={report.TopologyChangeDetected}, " +
            $"positions={report.ChangedPositionVertexCount}, normals={report.ChangedNormalVertexCount}, " +
            $"tangents={report.ChangedTangentVertexCount}, maxDelta={report.MaximumPositionDeltaMetres:N6}m, " +
            $"lodMatch={report.LodStructureMatches}, materialsMatch={report.MaterialsMatch}, " +
            $"skeletonMatch={report.SkeletonMatches}, errors={report.ErrorCount}, " +
            $"warnings={report.WarningCount}, candidate={report.CandidateGlbPath}.");
        if (report.Topology is { } topology)
            Log($"Edited SkeletalMesh topology contract: strategy={topology.LodStrategy}, " +
                $"candidateLods={topology.CandidateLodCount}, ignoredLods={topology.IgnoredCandidateLodCount}, " +
                $"vertices={topology.CandidateVertexCount:N0}, triangles={topology.CandidateTriangleCount:N0}, " +
                $"primitives={topology.CandidatePrimitiveCount:N0}, UVs={topology.CandidateTextureCoordinateCount}, " +
                $"materials={topology.CandidateMaterialCount}, usedBones={topology.CandidateUsedBoneCount}, " +
                $"maxInfluences={topology.MaximumInfluencesPerVertex}, " +
                $"weightSumError={topology.MaximumSkinWeightSumError:G6}, " +
                $"degenerate={topology.DegenerateTriangleCount}, " +
                $"opposed={topology.OpposedFaceNormalTriangleCount}/{topology.FaceOrientationTriangleCount}, " +
                $"index32={topology.Requires32BitIndices}, cloth={topology.SourceHasClothData}, " +
                $"activeMorphTargets={topology.SourceMayHaveLinkedMorphTargets}, " +
                $"targetLods=[{string.Join(",", topology.TargetLods.Select(lod => lod.TargetLodIndex))}].");
        if (report.DeformationDependencies is { } dependencies)
            Log($"Edited SkeletalMesh dependency contract: cloth={dependencies.ClothState}, " +
                $"activeClothRefs={dependencies.ActiveClothReferenceCount}, morph={dependencies.MorphState}, " +
                $"morphSets={dependencies.MorphTargetSetCount}, morphTargets={dependencies.MorphTargetCount}, " +
                $"morphDeltas={dependencies.MorphDeltaCount}, resolved={dependencies.IsResolved}, " +
                $"sameTopology={dependencies.TopologyPreservingSupported}, changedTopology={dependencies.TopologyChangingSupported}, " +
                $"reason={dependencies.TopologyChangingReason}.");
        if (report.SkeletonCompatibility is { } skeleton)
            LogSkeletonCompatibility($"mesh preflight/{trigger}", skeleton);
        if (report.AnimationValidation is { } animation)
        {
            Log($"Edited SkeletalMesh animation validation: profile={animation.ValidationProfile}, " +
                $"passed={animation.Passed}, staticOnly={animation.StaticOnly}, gameClips={animation.UsesGameAnimationClips}, " +
                $"poses={animation.PoseCount}, sourceBones={animation.SourceUsedBoneCount}, " +
                $"candidateBones={animation.CandidateUsedBoneCount}, significantSourceBones={animation.SignificantSourceBoneCount}, " +
                $"missingSignificantBones={animation.MissingSignificantCandidateBoneCount}, " +
                $"missingInfluence={animation.MissingSignificantSourceWeightPercent:N3}%, " +
                $"weightDistance={animation.BoneWeightDistributionDistance:N4}, " +
                $"centroidShift={animation.MaximumWeightedCentroidShiftMetres:N5}m/" +
                $"{animation.MaximumWeightedCentroidShiftRelativeToSourceExtent:P2}, " +
                $"bindError={animation.CandidateBindReconstructionErrorMetres:G6}m, " +
                $"bindExtentRatio={animation.CandidateBindExtentRatio:N3}, " +
                $"maxPoseBounds={animation.MaximumCandidateBoundsExpansionRatio:N3}x, " +
                $"maxEdgeStretch={animation.MaximumCandidateEdgeStretchRatio:N3}x, " +
                $"maxCollapsedEdges={animation.MaximumCandidateCollapsedEdgePercent:N3}%, " +
                $"maxOverstretchedEdges={animation.MaximumCandidateOverstretchedEdgePercent:N3}%, " +
                $"stressedBones=[{string.Join(",", animation.StressedBones)}].");
            foreach (var pose in animation.Poses)
                Log($"Edited SkeletalMesh animation pose {pose.PoseName}: bones={pose.StressedBoneCount}, " +
                    $"vertices={pose.CandidateTestedVertexCount:N0}, triangles={pose.CandidateTestedTriangleCount:N0}, " +
                    $"nonFinite={pose.NonFiniteCandidateVertexCount:N0}, " +
                    $"displacement={pose.CandidateMaximumDisplacementMetres:N5}m, " +
                    $"bounds={pose.CandidateBoundsExpansionRatio:N3}x, " +
                    $"edgeStretch={pose.CandidateMaximumEdgeStretchRatio:N3}x, " +
                    $"collapsed={pose.CandidateCollapsedEdgePercent:N3}%, " +
                    $"overstretched={pose.CandidateOverstretchedEdgePercent:N3}%.");
        }
        foreach (var lod in report.Lods)
            Log($"Edited SkeletalMesh preflight LOD {lod.LodIndex}: vertices={lod.CandidateVertexCount:N0}, " +
                $"triangles={lod.CandidateTriangleCount:N0}, idCarrier={lod.StableVertexIdCarrier}, " +
                $"changedPositions={lod.ChangedPositionVertexCount:N0}, maxDelta={lod.MaximumPositionDeltaMetres:N6}m, " +
                $"topology={lod.TopologyMatches}, UVs={lod.TextureCoordinatesMatch}, " +
                $"skinning={lod.SkinningMatches}, colours={lod.VertexColoursMatch}, " +
                $"tangentsPresent={lod.CandidateTangentsPresent}.");
        foreach (var issue in report.Issues)
            Log($"Edited SkeletalMesh preflight {issue.Severity} {issue.Code}: {issue.Message}");
    }

    private async void DeleteSelectedSkeletalMeshEditableExportsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_skeletalMeshEditableWorkspaceBusy) return;
        var rows = SkeletalMeshEditableExportsGrid.SelectedItems
            .OfType<SkeletalMeshEditableExportRow>()
            .ToArray();
        if (rows.Length == 0) return;
        var selectedIds = rows.Select(row => row.Entry.Manifest.ExportId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dependentEdits = _skeletalMeshEditedGlbs
            .Where(entry => selectedIds.Contains(entry.Manifest.SourceExportId))
            .ToArray();
        if (!await MeshEditArtifactsCanBeDeletedAsync(dependentEdits)) return;
        var names = string.Join("\n", rows.Take(10).Select(row =>
            $"  • {row.PackageAlias}.{row.ObjectName} — {row.Created}"));
        if (rows.Length > 10) names += $"\n  • …and {rows.Length - 10:N0} more";
        if (MessageBox.Show(
                this,
                $"Permanently delete {rows.Length:N0} selected editable mesh export(s)?\n\n" +
                $"{names}\n\n" +
                (dependentEdits.Length == 0
                    ? string.Empty
                    : $"This also deletes {dependentEdits.Length:N0} registered edited derivative(s).\n\n") +
                "Source UPKs and native mesh reconnaissance reports will not be changed.",
                "Delete editable mesh exports",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;

        _skeletalMeshEditableWorkspaceBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        try
        {
            foreach (var edit in dependentEdits)
            {
                await _skeletalMeshEditWorkspaceStore.DeleteAsync(edit);
                Log($"Dependent edited SkeletalMesh registration deleted with source export: " +
                    $"editId={edit.Manifest.EditId}, sourceExport={edit.Manifest.SourceExportId}.");
            }
            foreach (var row in rows)
            {
                await _skeletalMeshEditableWorkspaceStore.DeleteAsync(row.Entry);
                Log(
                    $"Editable SkeletalMesh export deleted: exportId={row.Entry.Manifest.ExportId}, " +
                    $"object={row.PackageAlias}.{row.ObjectName}, directory={row.Entry.DirectoryPath}.");
                if (_currentSkeletalMeshEditableExport?.Manifest.ExportId.Equals(
                        row.Entry.Manifest.ExportId, StringComparison.OrdinalIgnoreCase) == true)
                    _currentSkeletalMeshEditableExport = null;
            }
            StatusText.Text = $"Deleted {rows.Length:N0} editable mesh export(s).";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException)
        {
            Log($"Editable SkeletalMesh export deletion failed: error={exception}");
            MessageBox.Show(this, exception.Message, "Delete editable mesh exports",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshEditableWorkspaceBusy = false;
            await RefreshSkeletalMeshEditableWorkspaceAsync("editable exports deleted");
            await RefreshModifiedMeshUpksAsync("editable exports deleted");
        }
    }

    private async Task<bool> MeshEditArtifactsCanBeDeletedAsync(
        IEnumerable<SkeletalMeshEditWorkspaceEntry> entries)
    {
        foreach (var entry in entries)
        {
            var bundlePath = Path.Combine(
                entry.DirectoryPath, "deployment-bundle", ArkiveModBundleService.BundleManifestFileName);
            if (!File.Exists(bundlePath)) continue;
            try
            {
                var inspection = await _modDeploymentService.InspectAsync(
                    CreateModDeploymentRequest(bundlePath));
                if (inspection.CanInstall) continue;
                Log($"Mesh edit deletion refused: editId={entry.Manifest.EditId}, " +
                    $"bundle={bundlePath}, state={inspection.State}, summary={inspection.Summary}");
                MessageBox.Show(this,
                    $"Arkive cannot delete '{entry.Manifest.DisplayName}' while its deployment state " +
                    $"requires attention.\n\n{inspection.Summary}\n\n" +
                    "Restore the original package first if the mesh mod is installed.",
                    "Delete edited mesh", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException or
                    ArgumentException or JsonException)
            {
                Log($"Mesh edit deletion safety inspection failed: editId={entry.Manifest.EditId}, " +
                    $"bundle={bundlePath}, error={exception}");
                MessageBox.Show(this,
                    "Arkive cannot verify whether this mesh bundle is installed, so it will not delete " +
                    $"the edit automatically.\n\n{exception.Message}",
                    "Delete edited mesh", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
        return true;
    }

    private static string FormatOptionalHex(long? value) => value is null ? "unresolved" : $"+0x{value:X}";

}
