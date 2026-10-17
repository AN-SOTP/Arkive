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
    private async void RelationshipTextureReconButton_Click(object sender, RoutedEventArgs e)
    {
        if (RelationshipGrid.SelectedItem is not RelationshipAssetRow row ||
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
            row.Match.Asset.SerialOffset,
            row.Match.Asset.SerialSize,
            $"Relationship result ({row.Evidence})");
    }

    private async Task RunTextureReconnaissanceAsync(
        string packagePath,
        string packageAlias,
        int exportIndex,
        AssetKind kind,
        string className,
        string objectName,
        long serialOffset,
        long serialSize,
        string trigger)
    {
        if (_textureReconnaissanceInProgress)
        {
            return;
        }

        _textureReconnaissanceInProgress = true;
        UpdateTextureReconnaissanceActionState();
        UpdateLibraryActionState();
        UpdateRelationshipActionState();
        StatusText.Text = $"Reading native Texture2D evidence for {packageAlias}.{objectName}...";
        Log(
            $"Texture reconnaissance started: trigger={trigger}, package={packagePath}, " +
            $"alias={packageAlias}, index={exportIndex}, class={className}, object={objectName}, " +
            $"logical=0x{serialOffset:X}+0x{serialSize:X}, provider={_protectionProvider.Name}.");
        try
        {
            var packageSha256 = await _baselineWorkspaceStore.GetPackageSha256Async(packagePath);
            var report = await _textureReconnaissanceService.InspectAsync(
                new TextureReconnaissanceRequest(
                    packagePath,
                    packageAlias,
                    exportIndex,
                    kind,
                    className,
                    objectName,
                    serialOffset,
                    serialSize,
                    packageSha256));
            var entry = await _textureReconnaissanceWorkspaceStore.SaveAsync(report);
            _currentTextureReconnaissance = entry;
            DisplayTextureReconnaissance(entry);
            var best = report.MipCandidates.FirstOrDefault();
            var crunchCompression = report.Properties.FirstOrDefault(property =>
                property.Name.Equals("bUseCrunchCompression", StringComparison.OrdinalIgnoreCase))
                ?.ValuePreview ?? "not reported";
            Log(
                $"Texture reconnaissance completed in {report.Duration.TotalSeconds:N3}s: " +
                $"properties={report.Properties.Count}, candidates={report.MipCandidates.Count}, " +
                $"selectionIndex={report.ExportIndex}, nativeIndex={report.NativeExportIndex?.ToString() ?? "unresolved"}, " +
                $"bestConfidence={best?.Confidence.ToString() ?? "Unresolved"}, " +
                $"bestScore={best?.Score.ToString() ?? "-"}, format={report.PixelFormatEvidence}, " +
                $"crunchCompression={crunchCompression}, " +
                $"issues={report.Issues.Count}, packageSha={report.PackageSha256}, " +
                $"payloadSha={report.ExportPayloadSha256}, json={entry.JsonPath}, text={entry.TextPath}.");
            foreach (var candidate in report.MipCandidates)
            {
                Log(
                    $"Texture recon mip candidate {candidate.CandidateNumber}: " +
                    $"relative=0x{candidate.ArrayRelativeOffset:X}, count={candidate.DeclaredMipCount}, " +
                    $"score={candidate.Score}, confidence={candidate.Confidence}, " +
                    $"mips={string.Join(", ", candidate.Mips.Select(mip =>
                        $"{mip.MipIndex}:{mip.Width}x{mip.Height}/{mip.Storage}/0x{mip.BulkDataFlags:X}"))}.");
            }
            foreach (var issue in report.Issues)
            {
                Log($"Texture recon {issue.Severity} {issue.Code}: {issue.Message}");
            }

            StatusText.Text = best is null
                ? $"Texture report saved, but no mip layout was resolved for {objectName}."
                : $"Texture report saved: best mip layout is {best.Confidence} ({best.DeclaredMipCount} mip(s)).";
        }
        catch (Exception exception) when (
            exception is PackageAccessException or PackageFormatException or IOException or
                UnauthorizedAccessException or OutOfMemoryException)
        {
            StatusText.Text = "Native texture reconnaissance failed; see Diagnostics.";
            Log(
                $"Texture reconnaissance failed: trigger={trigger}, package={packagePath}, " +
                $"object={objectName}, error={exception}");
            MessageBox.Show(
                this,
                exception.Message,
                "Arkive texture reconnaissance error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _textureReconnaissanceInProgress = false;
            UpdateTextureReconnaissanceActionState();
            UpdateLibraryActionState();
            UpdateRelationshipActionState();
        }
    }

    private void DisplayTextureReconnaissance(TextureReconnaissanceEntry entry)
    {
        var report = entry.Report;
        var best = report.MipCandidates.FirstOrDefault();
        TextureReconSummaryText.Text = best is null
            ? $"{report.PackageAlias}.{report.ObjectName} | {report.Properties.Count:N0} properties | no mip candidate"
            : $"{report.PackageAlias}.{report.ObjectName} | {report.PixelFormatEvidence} | " +
              $"best: {best.Confidence}, {best.DeclaredMipCount:N0} mip(s), score {best.Score}";
        TextureReconPathText.Text = entry.JsonPath;
        TextureReconPropertiesGrid.ItemsSource = report.Properties
            .Select(property => new TextureReconPropertyRow(property))
            .ToArray();
        TextureReconMipsGrid.ItemsSource = report.MipCandidates
            .SelectMany(candidate => candidate.Mips.Select(mip => new TextureReconMipRow(candidate, mip)))
            .ToArray();
        TextureReconStorageGrid.ItemsSource = report.ExportStorage
            .Select(storage => new TextureReconStorageRow(storage))
            .ToArray();
        TextureReconIssuesGrid.ItemsSource = report.Issues;
        OpenTextureReconFolderButton.IsEnabled = true;
        CopyTextureReconPathButton.IsEnabled = true;
        UpdateNativeTextureExportActionState();
        TextureReconTab.IsSelected = true;
    }

    private void OpenTextureReconFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTextureReconnaissance is not null)
        {
            OpenFolder(_currentTextureReconnaissance.DirectoryPath, "texture reconnaissance report");
        }
    }

    private void CopyTextureReconPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTextureReconnaissance is not null)
        {
            CopyText(_currentTextureReconnaissance.JsonPath, "texture reconnaissance JSON path");
        }
    }

    private async void NativeTextureExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTextureReconnaissance is not { } entry ||
            _nativeTextureExportService is null ||
            _exportInProgress)
        {
            return;
        }

        _exportInProgress = true;
        _nativeTextureExportInProgress = true;
        SetActionsEnabled(previewEnabled: false, exportEnabled: false);
        UpdateLibraryActionState();
        UpdateRelationshipActionState();
        UpdateNativeTextureExportActionState();
        var report = entry.Report;
        var outputDirectory = _exportWorkspaceStore.CreateSessionDirectory(
            report.PackageAlias,
            report.ObjectName + "-native-texture");
        StatusText.Text = $"Extracting and decoding {report.PackageAlias}.{report.ObjectName}...";
        Log(
            $"Native texture export started: package={report.PackagePath}, alias={report.PackageAlias}, " +
            $"object={report.ObjectName}, selectionIndex={report.ExportIndex}, " +
            $"nativeIndex={report.NativeExportIndex?.ToString() ?? "unresolved"}, " +
            $"logical=0x{report.SerialOffset:X}+0x{report.SerialSize:X}, " +
            $"reconPayloadSha={report.ExportPayloadSha256}, decoder={_nativeTextureExportService.DecoderName}, " +
            $"reconJson={entry.JsonPath}, output={outputDirectory}.");
        try
        {
            var result = await _nativeTextureExportService.ExportAsync(
                new NativeTextureExportRequest(report, outputDirectory, entry.JsonPath));
            var inGameNames = CurrentReconnaissanceInGameNames(report);
            var assetBaseline = await CreateAssetBaselineAsync(
                new AssetBaselineRequest(
                    report.PackagePath,
                    report.PackageAlias,
                    report.ExportIndex,
                    report.Kind,
                    report.ClassName,
                    report.ObjectName,
                    report.SerialOffset,
                    report.SerialSize,
                    inGameNames),
                "automatic native texture export baseline",
                navigateToWorkspace: false);
            var workspaceEntry = await _exportWorkspaceStore.CommitNativeTextureAsync(
                result,
                report,
                inGameNames,
                assetBaseline: assetBaseline);
            Log(
                $"Native texture export completed in {result.Duration.TotalSeconds:N3}s: " +
                $"dds={result.DdsPath}, ddsBytes={result.Manifest.DdsBytes}, " +
                $"ddsSha={result.Manifest.DdsSha256}, mips={result.Manifest.MipCount}, " +
                $"dimensions={result.Manifest.Width}x{result.Manifest.Height}, " +
                $"original={result.Manifest.OriginalWidth}x{result.Manifest.OriginalHeight}, " +
                $"format={result.Manifest.PixelFormat}, compression={result.Manifest.Compression}, " +
                $"ddsErrors={result.DdsInspection.ErrorCount}, " +
                $"ddsWarnings={result.DdsInspection.WarningCount}, nativeManifest={result.ManifestPath}, " +
                $"editImage={result.Manifest.EditImageRelativePath ?? "unavailable"}, " +
                $"editImageSha={result.Manifest.EditImageSha256 ?? "unavailable"}, " +
                $"workspaceManifest={workspaceEntry.ManifestPath}, " +
                $"baseline={assetBaseline?.ManifestPath ?? "unavailable"}.");
            foreach (var mip in result.Manifest.Mips)
            {
                Log(
                    $"Native texture mip exported: mip={mip.MipIndex}, dimensions={mip.Width}x{mip.Height}, " +
                    $"sourceLogical=0x{mip.SourcePayloadLogicalOffset:X}, flags=0x{mip.BulkDataFlags:X8}, " +
                    $"codec={mip.StorageCodec}, storedBytes={mip.StoredPayloadBytes}, " +
                    $"storageHeader={mip.StorageHeaderBytes}, storedSha={mip.StoredPayloadSha256}, " +
                    $"gpuBytes={mip.DecodedGpuBytes}, gpuSha={mip.DecodedGpuSha256}, " +
                    $"ddsOffset=0x{mip.DdsPayloadOffset:X}, raw={mip.StoredPayloadRelativePath}.");
            }

            foreach (var issue in result.DdsInspection.Issues)
            {
                Log(
                    $"Native DDS validation: severity={issue.Severity}, code={issue.Code}, " +
                    $"message={issue.Message}");
            }

            await RefreshExportWorkspaceAsync("native texture export completed");
            ExportsTab.IsSelected = true;
            StatusText.Text =
                $"Exported a fully visible edit PNG for {result.Manifest.ObjectName}; " +
                $"Arkive retained the original {result.Manifest.PixelFormat} DDS and " +
                $"{result.Manifest.MipCount:N0}-level mip layout internally.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            StatusText.Text = "Native texture export was refused or failed.";
            Log(
                $"Native texture export failed: package={report.PackagePath}, object={report.ObjectName}, " +
                $"output={outputDirectory}, error={exception}");
            try
            {
                if (Directory.Exists(outputDirectory) &&
                    !File.Exists(Path.Combine(outputDirectory, ExportWorkspaceStore.ManifestFileName)))
                {
                    Directory.Delete(outputDirectory, recursive: true);
                    Log($"Incomplete native texture export directory removed: {outputDirectory}");
                }
            }
            catch (Exception cleanupException) when (
                cleanupException is IOException or UnauthorizedAccessException)
            {
                Log(
                    $"Incomplete native texture export cleanup failed: directory={outputDirectory}, " +
                    $"error={cleanupException}");
            }

            MessageBox.Show(
                this,
                exception.Message,
                "Arkive native texture export",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _nativeTextureExportInProgress = false;
            _exportInProgress = false;
            var selected = AssetsGrid.SelectedItem as AssetRow;
            SetActionsEnabled(
                _usingUmodelCatalogue && _currentPackagePath is not null,
                selected?.ExternalAsset?.IsExportable == true && _currentPackagePath is not null);
            UpdateLibraryActionState();
            UpdateRelationshipActionState();
            UpdateNativeTextureExportActionState();
        }
    }

    private IReadOnlyList<string> CurrentReconnaissanceInGameNames(
        TextureReconnaissanceReport report)
    {
        IEnumerable<GameAssetAssociation> metadata = [];
        if (LibraryGrid.SelectedItem is LibraryAssetRow library &&
            library.SourcePath.Equals(report.PackagePath, StringComparison.OrdinalIgnoreCase) &&
            library.ObjectName.Equals(report.ObjectName, StringComparison.OrdinalIgnoreCase))
        {
            metadata = library.GameMetadata;
        }
        else if (RelationshipGrid.SelectedItem is RelationshipAssetRow relationship &&
                 relationship.SourcePath.Equals(report.PackagePath, StringComparison.OrdinalIgnoreCase) &&
                 relationship.ObjectName.Equals(report.ObjectName, StringComparison.OrdinalIgnoreCase))
        {
            metadata = relationship.Match.Asset.GameMetadata;
        }
        else if (AssetsGrid.SelectedItem is AssetRow asset &&
                 ObjectNameFromPath(asset.ObjectPath).Equals(
                     report.ObjectName,
                     StringComparison.OrdinalIgnoreCase))
        {
            metadata = asset.GameMetadata;
        }

        return metadata
            .Select(item => item.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void UpdateNativeTextureExportActionState()
    {
        if (NativeTextureExportButton is null)
        {
            return;
        }

        NativeTextureExportButton.IsEnabled =
            !_nativeTextureExportInProgress &&
            !_exportInProgress &&
            _nativeTextureExportService is not null &&
            _currentTextureReconnaissance is { Report: var report } &&
            report.SchemaVersion == TextureReconnaissanceService.CurrentSchemaVersion &&
            report.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
            NativeTextureFormats.TryResolve(report, out _) &&
            report.Properties.Any(property =>
                property.Name.Equals("bUseCrunchCompression", StringComparison.OrdinalIgnoreCase) &&
                property.NumericValue == 1) &&
            report.MipCandidates.Count == 1;
    }

    private void UpdateTextureReconnaissanceActionState()
    {
        if (TextureReconButton is not null)
        {
            var row = AssetsGrid?.SelectedItem as AssetRow;
            TextureReconButton.IsEnabled = !_textureReconnaissanceInProgress &&
                                           _currentPackagePath is not null &&
                                           row is not null &&
                                           CanReconnoitreTexture(row.Kind, row.ClassName);
        }

        UpdateNativeTextureExportActionState();
    }

    private void UpdateSkeletalMeshReconnaissanceActionState()
    {
        if (SkeletalMeshReconButton is not null)
        {
            var row = AssetsGrid?.SelectedItem as AssetRow;
            SkeletalMeshReconButton.IsEnabled = !_skeletalMeshReconnaissanceInProgress &&
                                                _currentPackagePath is not null &&
                                                row is not null &&
                                                CanReconnoitreMesh(row.Kind, row.ClassName);
        }

        if (ExtractSkeletalMeshAssetKitButton is not null)
        {
            var row = AssetsGrid?.SelectedItem as AssetRow;
            ExtractSkeletalMeshAssetKitButton.IsEnabled =
                !_skeletalMeshAssetKitInProgress &&
                !_skeletalMeshReconnaissanceInProgress &&
                !_skeletalMeshEditableExportInProgress &&
                _skeletalMeshAssetKitService is not null &&
                _currentPackagePath is not null && row is not null &&
                CanReconnoitreSkeletalMesh(row.Kind, row.ClassName);
        }

        if (BuildSkeletalMeshNoOpButton is not null)
        {
            BuildSkeletalMeshNoOpButton.IsEnabled = !_skeletalMeshReconnaissanceInProgress &&
                                                    !_skeletalMeshNoOpInProgress &&
                                                    _currentSkeletalMeshReconnaissance?.Report.Readiness ==
                                                    SkeletalMeshReadiness.NoOpReady;
        }

        if (ExportEditableSkeletalMeshButton is not null)
        {
            ExportEditableSkeletalMeshButton.IsEnabled = !_skeletalMeshReconnaissanceInProgress &&
                                                         !_skeletalMeshNoOpInProgress &&
                                                         !_skeletalMeshEditableExportInProgress &&
                                                         _currentSkeletalMeshReconnaissance?.Report.Readiness ==
                                                         SkeletalMeshReadiness.NoOpReady &&
                                                         _currentSkeletalMeshReconnaissance.Report.SchemaVersion ==
                                                         SkeletalMeshReconnaissanceService.CurrentSchemaVersion;
        }
        if (OpenEditableSkeletalMeshFolderButton is not null)
            OpenEditableSkeletalMeshFolderButton.IsEnabled = !_skeletalMeshEditableExportInProgress &&
                                                             (_currentSkeletalMeshEditableExport is not null ||
                                                              SkeletalMeshEditableExportsGrid?.SelectedItem is
                                                                  SkeletalMeshEditableExportRow);
        UpdateSkeletalMeshEditableWorkspaceActionState();
    }

    private void UpdateSkeletalMeshEditableWorkspaceActionState()
    {
        var selected = SkeletalMeshEditableExportsGrid?.SelectedItems
            .OfType<SkeletalMeshEditableExportRow>()
            .ToArray() ?? [];
        var first = selected.FirstOrDefault();
        var enabled = !_skeletalMeshEditableWorkspaceBusy &&
                      !_skeletalMeshEditableExportInProgress && first is not null;
        if (OpenSelectedSkeletalMeshEditableFolderButton is not null)
            OpenSelectedSkeletalMeshEditableFolderButton.IsEnabled = enabled &&
                                                                      Directory.Exists(first!.Entry.DirectoryPath);
        if (RevealSelectedSkeletalMeshGlbButton is not null)
            RevealSelectedSkeletalMeshGlbButton.IsEnabled = enabled && first!.Entry.GlbExists;
        if (CopySelectedSkeletalMeshGlbPathButton is not null)
            CopySelectedSkeletalMeshGlbPathButton.IsEnabled = enabled;
        if (AddEditedSkeletalMeshGlbButton is not null)
            AddEditedSkeletalMeshGlbButton.IsEnabled = enabled && selected.Length == 1 &&
                                                       first!.Entry.GlbExists;
        if (CompareSelectedSkeletalMeshSkeletonsButton is not null)
            CompareSelectedSkeletalMeshSkeletonsButton.IsEnabled = enabled && selected.Length == 2 &&
                                                                    selected.All(row => row.Entry.GlbExists);
        if (CreateSkeletalMeshRetargetWorkspaceButton is not null)
            CreateSkeletalMeshRetargetWorkspaceButton.IsEnabled = enabled && selected.Length == 2 &&
                                                                   selected.All(row => row.Entry.GlbExists);
        if (DeleteSelectedSkeletalMeshEditableExportsButton is not null)
            DeleteSelectedSkeletalMeshEditableExportsButton.IsEnabled = enabled && selected.Length > 0;

        var selectedEdits = SkeletalMeshEditedGlbsGrid?.SelectedItems
            .OfType<SkeletalMeshEditedGlbRow>()
            .ToArray() ?? [];
        var firstEdit = selectedEdits.FirstOrDefault();
        var editEnabled = !_skeletalMeshEditableWorkspaceBusy &&
                          !_skeletalMeshEditableExportInProgress &&
                          !_skeletalMeshDeploymentBusy && firstEdit is not null;
        var installedMeshBundle = _selectedSkeletalMeshDeploymentInspection?.CanRestore == true;
        if (RevalidateEditedSkeletalMeshGlbButton is not null)
            RevalidateEditedSkeletalMeshGlbButton.IsEnabled = editEnabled && selectedEdits.Length == 1 &&
                                                              !installedMeshBundle;
        if (BuildNativeSkeletalMeshCandidateButton is not null)
            BuildNativeSkeletalMeshCandidateButton.IsEnabled = editEnabled && selectedEdits.Length == 1 &&
                !installedMeshBundle &&
                first!.Entry.IsCurrentSchema && firstEdit!.Entry.Manifest.Preflight.Readiness ==
                SkeletalMeshEditReadiness.ReadyForPositionTangentEncoding;
        if (CompileSkeletalMeshTopologyProofButton is not null)
            CompileSkeletalMeshTopologyProofButton.IsEnabled = editEnabled && selectedEdits.Length == 1 &&
                !installedMeshBundle &&
                first!.Entry.IsCurrentSchema && firstEdit!.Entry.Manifest.Preflight.Readiness ==
                SkeletalMeshEditReadiness.ReadyForTopologyCompilation;
        if (OpenSkeletalMeshTopologyProofButton is not null)
            OpenSkeletalMeshTopologyProofButton.IsEnabled = editEnabled && selectedEdits.Length == 1 &&
                Directory.Exists(firstEdit!.TopologyCompilationDirectory) &&
                File.Exists(firstEdit.TopologyCompilationManifestPath) &&
                File.Exists(firstEdit.TopologyCompilationPayloadPath) &&
                File.Exists(firstEdit.TopologyCompilationProofGlbPath);
        if (OpenNativeSkeletalMeshCandidateButton is not null)
            OpenNativeSkeletalMeshCandidateButton.IsEnabled = editEnabled && selectedEdits.Length == 1 &&
                Directory.Exists(firstEdit!.NativeEncodingDirectory) &&
                File.Exists(firstEdit.NativeEncodingManifestPath) &&
                File.Exists(firstEdit.NativeEncodingPayloadPath) &&
                File.Exists(firstEdit.NativeEncodingProofGlbPath);
        if (BuildStagedSkeletalMeshUpkButton is not null)
            BuildStagedSkeletalMeshUpkButton.IsEnabled = editEnabled && selectedEdits.Length == 1 &&
                !installedMeshBundle &&
                ((firstEdit!.Entry.Manifest.Preflight.Readiness ==
                      SkeletalMeshEditReadiness.ReadyForPositionTangentEncoding &&
                  File.Exists(firstEdit.NativeEncodingManifestPath) &&
                  File.Exists(firstEdit.NativeEncodingPayloadPath) &&
                  File.Exists(firstEdit.NativeEncodingProofGlbPath)) ||
                 (firstEdit.Entry.Manifest.Preflight.Readiness ==
                      SkeletalMeshEditReadiness.ReadyForTopologyCompilation &&
                  File.Exists(firstEdit.TopologyCompilationManifestPath) &&
                  File.Exists(firstEdit.TopologyCompilationPayloadPath) &&
                  File.Exists(firstEdit.TopologyCompilationProofGlbPath)));
        if (PreviewStagedSkeletalMeshUpkButton is not null)
            PreviewStagedSkeletalMeshUpkButton.IsEnabled = editEnabled && selectedEdits.Length == 1 &&
                _umodelBridge is not null &&
                ((File.Exists(firstEdit!.StagedUpkManifestPath) && firstEdit.StagedUpkOutputExists) ||
                 (File.Exists(firstEdit.TopologyStagedUpkManifestPath) &&
                  firstEdit.TopologyStagedUpkOutputExists));
        if (OpenStagedSkeletalMeshUpkButton is not null)
            OpenStagedSkeletalMeshUpkButton.IsEnabled = editEnabled && selectedEdits.Length == 1 &&
                (Directory.Exists(firstEdit!.StagedUpkDirectory) ||
                 Directory.Exists(firstEdit.TopologyStagedUpkDirectory));
        if (PrepareSkeletalMeshModButton is not null)
            PrepareSkeletalMeshModButton.IsEnabled = editEnabled && selectedEdits.Length == 1 &&
                !installedMeshBundle && _meshModBundleService is not null &&
                firstEdit!.Entry.Manifest.Preflight.SchemaVersion ==
                    SkeletalMeshEditPreflightService.CurrentSchemaVersion &&
                firstEdit.Entry.Manifest.Preflight.AnimationValidation is { Passed: true } &&
                firstEdit!.Entry.Manifest.Preflight.Readiness ==
                    SkeletalMeshEditReadiness.ReadyForPositionTangentEncoding &&
                File.Exists(firstEdit.StagedUpkManifestPath) && firstEdit.StagedUpkOutputExists;
        if (InstallSkeletalMeshModButton is not null)
            InstallSkeletalMeshModButton.IsEnabled = editEnabled && selectedEdits.Length == 1 &&
                firstEdit!.Entry.Manifest.Preflight.SchemaVersion ==
                    SkeletalMeshEditPreflightService.CurrentSchemaVersion &&
                firstEdit.Entry.Manifest.Preflight.AnimationValidation is { Passed: true } &&
                _selectedSkeletalMeshDeploymentInspection?.CanInstall == true;
        if (RestoreSkeletalMeshModButton is not null)
            RestoreSkeletalMeshModButton.IsEnabled = editEnabled && selectedEdits.Length == 1 &&
                _selectedSkeletalMeshDeploymentInspection?.CanRestore == true;
        if (RefreshSkeletalMeshDeploymentButton is not null)
            RefreshSkeletalMeshDeploymentButton.IsEnabled = editEnabled && selectedEdits.Length == 1 &&
                File.Exists(firstEdit!.MeshBundleManifestPath);
        if (OpenSkeletalMeshDeploymentFolderButton is not null)
            OpenSkeletalMeshDeploymentFolderButton.IsEnabled = editEnabled && selectedEdits.Length == 1 &&
                Directory.Exists(firstEdit!.MeshDeploymentDirectory);
        if (OpenEditedSkeletalMeshFolderButton is not null)
            OpenEditedSkeletalMeshFolderButton.IsEnabled = editEnabled &&
                                                            Directory.Exists(firstEdit!.Entry.DirectoryPath);
        if (RevealEditedSkeletalMeshGlbButton is not null)
            RevealEditedSkeletalMeshGlbButton.IsEnabled = editEnabled && File.Exists(firstEdit!.Entry.EditedGlbPath);
        if (CopyEditedSkeletalMeshGlbPathButton is not null)
            CopyEditedSkeletalMeshGlbPathButton.IsEnabled = editEnabled;
        if (DeleteEditedSkeletalMeshGlbButton is not null)
            DeleteEditedSkeletalMeshGlbButton.IsEnabled = editEnabled && selectedEdits.Length > 0 &&
                                                          !installedMeshBundle;
    }

    private static bool CanReconnoitreTexture(AssetKind kind, string className) =>
        AssetTypes.Editable(kind, className) == AssetTypes.Texture;

    private static bool CanReconnoitreSkeletalMesh(AssetKind kind, string className) =>
        AssetTypes.Editable(kind, className) == AssetTypes.SkeletalMesh;

    private static bool CanReconnoitreStaticMesh(AssetKind kind, string className) =>
        AssetTypes.Editable(kind, className) == AssetTypes.StaticMesh;

    private static bool CanReconnoitreMesh(AssetKind kind, string className) =>
        CanReconnoitreSkeletalMesh(kind, className) || CanReconnoitreStaticMesh(kind, className);

    private AssetCatalogEntry ResolveCurrentSkeletalMeshSelection(
        string packagePath,
        string objectName,
        int preferredExportIndex,
        long preferredSerialOffset,
        long preferredSerialSize)
    {
        var catalog = _catalogReader.Read(packagePath);
        return AssetCatalogSelectionResolver.ResolveSkeletalMesh(
            catalog.Assets, objectName, preferredExportIndex,
            preferredSerialOffset, preferredSerialSize);
    }

    private static string ObjectNameFromPath(string objectPath)
    {
        var separator = objectPath.LastIndexOf('.');
        return separator >= 0 && separator < objectPath.Length - 1
            ? objectPath[(separator + 1)..]
            : objectPath;
    }

    private static string SafeObjectFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(result) ? "mesh" : result.Length <= 60 ? result : result[..60];
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetExternalSelection(out var row, out var packagePath))
        {
            return;
        }

        await ExportAssetToWorkspaceAsync(
            packagePath,
            LostArkPackageName.GetDisplayName(packagePath),
            row.ExternalAsset!,
            row.GameMetadata,
            allowPackageWideFallback:
                _assets.Count <= UmodelBridge.DefaultMaxPackageWideFallbackExports,
            trigger: "open-package Assets result");
    }

    private async Task ExportAssetToWorkspaceAsync(
        string packagePath,
        string packageAlias,
        UmodelAssetEntry asset,
        IReadOnlyList<GameAssetAssociation> gameMetadata,
        bool allowPackageWideFallback,
        string trigger)
    {
        if (_umodelBridge is null || _exportInProgress)
        {
            return;
        }

        _exportInProgress = true;
        SetActionsEnabled(previewEnabled: false, exportEnabled: false);
        UpdateLibraryActionState();
        UpdateRelationshipActionState();
        StatusText.Text = $"Exporting {packageAlias}.{asset.ObjectName} through UModel...";
        var outputDirectory = _exportWorkspaceStore.CreateSessionDirectory(
            packageAlias,
            asset.ObjectName);
        Log(
            $"Export workspace session started: trigger={trigger}, index={asset.ExportIndex}, " +
            $"class={asset.ClassName}, object={asset.ObjectName}, package={packagePath}, " +
            $"allowPackageWideFallback={allowPackageWideFallback}, output={outputDirectory}.");
        try
        {
            var result = await _umodelBridge.ExportAsync(
                packagePath,
                asset,
                outputDirectory,
                allowPackageWideFallback);
            var assetBaseline = await CreateAssetBaselineAsync(
                new AssetBaselineRequest(
                    packagePath,
                    packageAlias,
                    asset.ExportIndex,
                    asset.Kind,
                    asset.ClassName,
                    asset.ObjectName,
                    asset.SerialOffset,
                    asset.SerialSize,
                    gameMetadata.Select(item => item.DisplayName).ToArray()),
                $"automatic export baseline ({trigger})",
                navigateToWorkspace: false);
            StatusText.Text = $"Recording export workspace manifest for {packageAlias}.{asset.ObjectName}...";
            var workspaceEntry = await _exportWorkspaceStore.CommitAsync(
                result,
                packageAlias,
                gameMetadata.Select(item => item.DisplayName).ToArray(),
                _umodelBridge.ExecutablePath,
                assetBaseline: assetBaseline);
            Log(
                $"Export workspace session completed in {result.Duration.TotalSeconds:N3}s: " +
                $"files={result.OutputFiles.Count}, " +
                $"bytes={workspaceEntry.Manifest.OutputBytes}, " +
                $"packageWideFallback={result.UsedPackageWideFallback}, " +
                $"assetBaseline={assetBaseline?.ManifestPath ?? "unavailable"}, " +
                $"manifest={workspaceEntry.ManifestPath}, output={result.OutputDirectory}.");
            foreach (var diagnostic in result.Diagnostics)
            {
                Log($"UModel export: {diagnostic}");
            }

            await RefreshExportWorkspaceAsync("export completed");
            StatusText.Text = result.OutputFiles.Count == 0
                ? $"UModel completed with no output files; the session manifest is in {result.OutputDirectory}"
                : result.UsedPackageWideFallback
                    ? $"Backend could not select by name; exported {result.OutputFiles.Count:N0} package file(s) " +
                      $"to {result.OutputDirectory}"
                : $"Exported {result.OutputFiles.Count:N0} file(s) to {result.OutputDirectory}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Asset export failed.";
            Log(
                $"Export workspace session failed: trigger={trigger}, package={packagePath}, " +
                $"object={asset.ObjectName}, output={outputDirectory}, error={exception}");
            MessageBox.Show(
                this,
                exception.Message,
                "Arkive export error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _exportInProgress = false;
            var selected = AssetsGrid.SelectedItem as AssetRow;
            SetActionsEnabled(
                _usingUmodelCatalogue && _currentPackagePath is not null,
                selected?.ExternalAsset?.IsExportable == true && _currentPackagePath is not null);
            UpdateLibraryActionState();
            UpdateRelationshipActionState();
        }
    }

    private bool TryGetExternalSelection(out AssetRow row, out string packagePath)
    {
        row = AssetsGrid.SelectedItem as AssetRow ?? null!;
        packagePath = _currentPackagePath ?? string.Empty;
        return row?.ExternalAsset?.IsExportable == true &&
               _umodelBridge is not null &&
               !string.IsNullOrEmpty(packagePath);
    }

    private void SetActionsEnabled(bool previewEnabled, bool exportEnabled)
    {
        if (PreviewButton is not null)
        {
            PreviewButton.IsEnabled = previewEnabled;
        }

        if (ExportButton is not null)
        {
            ExportButton.IsEnabled = exportEnabled;
        }

        if (InspectAssetButton is not null)
        {
            InspectAssetButton.IsEnabled = exportEnabled;
        }

        if (!previewEnabled && BaselineAssetButton is not null)
        {
            BaselineAssetButton.IsEnabled = false;
        }

        UpdateTextureReconnaissanceActionState();
    }

    private void DisplayNativeSummaryUnavailable(string packagePath, string diagnostic)
    {
        PathText.Text = packagePath;
        DisplayPackageAlias(packagePath);
        ProfileText.Text = "Native summary unavailable - using UModel fallback";
        PhysicalSizeText.Text = $"{new FileInfo(packagePath).Length:N0} bytes";
        EngineText.Text = "-";
        HeaderSizeText.Text = "-";
        PackageFlagsText.Text = "-";
        CompressionText.Text = "-";
        NamesText.Text = "-";
        ImportsText.Text = "-";
        ExportsText.Text = "-";
        GuidText.Text = "-";
        ChunksGrid.ItemsSource = Array.Empty<ChunkRow>();
        SetValidationIssues(
        [
            new ValidationIssue(
                ValidationSeverity.Warning,
                "UPK900",
                $"Arkive did not recognize this native summary: {diagnostic}"),
        ]);
    }

}
