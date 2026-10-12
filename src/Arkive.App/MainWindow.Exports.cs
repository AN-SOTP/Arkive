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
    private async void RefreshExportWorkspaceButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshExportWorkspaceAsync("refresh button");

    private async Task RefreshExportWorkspaceAsync(string trigger)
    {
        if (ExportWorkspaceGrid is null || ExportWorkspaceSummaryText is null)
        {
            return;
        }

        ExportWorkspaceSummaryText.Text = "Reading export workspace...";
        ExportWorkspacePathText.Text = $"Workspace: {_exportWorkspaceStore.RootDirectory}";
        try
        {
            var snapshot = await _exportWorkspaceStore.LoadAsync();
            var rows = snapshot.Entries
                .Select(entry => new ExportWorkspaceRow(entry))
                .ToArray();
            ExportWorkspaceGrid.ItemsSource = rows;
            ClearExportArtifactInspection("Select an export session.");
            ExportWorkspaceSummaryText.Text =
                $"{rows.Length:N0} completed export sessions | " +
                $"{rows.Sum(row => row.FileCount):N0} files | " +
                $"{FormatByteSize(snapshot.OutputBytes)} | " +
                $"{snapshot.UnreadableManifestCount:N0} unreadable manifests";
            UpdateExportWorkspaceActionState();
            Log(
                $"Export workspace refreshed: trigger={trigger}, sessions={rows.Length}, " +
                $"files={rows.Sum(row => row.FileCount)}, bytes={snapshot.OutputBytes}, " +
                $"unreadableManifests={snapshot.UnreadableManifestCount}, " +
                $"root={snapshot.RootDirectory}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ExportWorkspaceSummaryText.Text = "Export workspace could not be read.";
            Log($"Export workspace refresh failed: trigger={trigger}, error={exception}");
        }
    }

    private void ExportWorkspaceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SelectExportWorkspaceRow();

    private void SelectExportWorkspaceRow()
    {
        _artifactInspectionCancellation?.Cancel();
        ClearTextureImportCandidateDetails();
        ClearTextureReplacementDetails();
        UpdateExportWorkspaceActionState();
        if (ExportWorkspaceGrid.SelectedItem is ExportWorkspaceRow row)
        {
            var files = row.Entry.Manifest.OutputFiles
                .Select(file => new ExportArtifactFileRow(
                    row.Entry.OutputDirectory,
                    file))
                .ToArray();
            ExportArtifactFilesGrid.ItemsSource = files;
            ExportArtifactFileSummaryText.Text =
                $"{files.Length:N0} files | {files.Count(file => file.IsSupported):N0} inspectable";
            var initialSelection = files.FirstOrDefault(file => file.IsSupported) ?? files.FirstOrDefault();
            ExportArtifactFilesGrid.SelectedItem = initialSelection;
            Log(
                $"Export workspace session selected: exportId={row.Entry.Manifest.ExportId}, " +
                $"package={row.Entry.Manifest.SourcePackagePath}, " +
                $"object={row.Entry.Manifest.ObjectName}, output={row.Entry.OutputDirectory}.");
            _ = LoadTextureImportCandidateDetailsAsync(row);
            _ = LoadTextureReplacementDetailsAsync(row);
        }
        else
        {
            ClearExportArtifactInspection("Select an export session.");
        }
    }

    private async void ExportArtifactFilesGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _artifactInspectionCancellation?.Cancel();
        _artifactInspectionCancellation = null;
        ExportArtifactPropertiesGrid.ItemsSource = Array.Empty<ArtifactInspectionProperty>();
        ExportArtifactDependenciesGrid.ItemsSource = Array.Empty<ExportArtifactDependencyRow>();
        ExportArtifactIssuesGrid.ItemsSource = Array.Empty<ArtifactInspectionIssue>();

        if (ExportArtifactFilesGrid.SelectedItem is not ExportArtifactFileRow row)
        {
            ExportArtifactOpenFileButton.IsEnabled = false;
            ExportArtifactSummaryText.Text = "Select a PNG, DDS, glTF, or GLB file to inspect.";
            return;
        }

        ExportArtifactOpenFileButton.IsEnabled = row.Exists;
        if (!row.Exists)
        {
            ExportArtifactSummaryText.Text = $"Missing exported file: {row.RelativePath}";
            ExportArtifactIssuesGrid.ItemsSource = new[]
            {
                new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "EXPORT_FILE_MISSING",
                    "The file recorded by the export manifest is no longer present."),
            };
            Log($"Export artifact inspection refused because the file is missing: {row.FullPath}");
            return;
        }

        if (!row.IsSupported)
        {
            ExportArtifactSummaryText.Text =
                $"{row.Type} files are retained as export dependencies but are not inspected directly yet.";
            Log($"Export artifact selected without an inspector: type={row.Type}, path={row.FullPath}.");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _artifactInspectionCancellation = cancellation;
        var watch = Stopwatch.StartNew();
        ExportArtifactSummaryText.Text = $"Inspecting {row.Name}...";
        Log($"Export artifact inspection started: type={row.Type}, bytes={row.ActualLength}, path={row.FullPath}.");
        try
        {
            var result = await _artifactInspector.InspectAsync(row.FullPath, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            watch.Stop();
            ExportArtifactPropertiesGrid.ItemsSource = result.Properties;
            ExportArtifactDependenciesGrid.ItemsSource = result.Dependencies
                .Select(dependency => new ExportArtifactDependencyRow(dependency))
                .ToArray();
            ExportArtifactIssuesGrid.ItemsSource = result.Issues;
            ExportArtifactSummaryText.Text =
                $"{result.Format}: {result.Summary} | " +
                $"{result.ErrorCount:N0} errors, {result.WarningCount:N0} warnings | " +
                $"{watch.Elapsed.TotalMilliseconds:N0} ms";
            StatusText.Text = result.ErrorCount > 0
                ? $"Inspected {row.Name}; structural errors were found."
                : $"Inspected {row.Name}; {result.WarningCount:N0} warnings.";
            Log(
                $"Export artifact inspection completed in {watch.Elapsed.TotalMilliseconds:N1}ms: " +
                $"format={result.Format}, properties={result.Properties.Count}, " +
                $"dependencies={result.Dependencies.Count}, errors={result.ErrorCount}, " +
                $"warnings={result.WarningCount}, path={result.FilePath}.");
            foreach (var issue in result.Issues)
            {
                Log($"Export artifact validation: severity={issue.Severity}, code={issue.Code}, message={issue.Message}");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            watch.Stop();
            Log(
                $"Export artifact inspection cancelled after {watch.Elapsed.TotalMilliseconds:N1}ms: " +
                $"path={row.FullPath}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            watch.Stop();
            ExportArtifactSummaryText.Text = $"Inspection failed: {exception.Message}";
            ExportArtifactIssuesGrid.ItemsSource = new[]
            {
                new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "ARTIFACT_INSPECTION_FAILED",
                    exception.Message),
            };
            Log(
                $"Export artifact inspection failed after {watch.Elapsed.TotalMilliseconds:N1}ms: " +
                $"path={row.FullPath}, error={exception}");
        }
        finally
        {
            if (ReferenceEquals(_artifactInspectionCancellation, cancellation))
            {
                _artifactInspectionCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void OpenSelectedExportArtifactButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExportArtifactFilesGrid.SelectedItem is not ExportArtifactFileRow row || !row.Exists)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = row.FullPath,
                UseShellExecute = true,
            });
            Log($"Opened exported artifact with its registered application: {row.FullPath}");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"Could not open exported artifact: {exception.Message}";
            Log($"Could not open exported artifact: path={row.FullPath}, error={exception}");
        }
    }

    private void ClearExportArtifactInspection(string message)
    {
        _artifactInspectionCancellation?.Cancel();
        if (ExportArtifactFilesGrid is null)
        {
            return;
        }

        ExportArtifactFilesGrid.ItemsSource = Array.Empty<ExportArtifactFileRow>();
        ExportArtifactPropertiesGrid.ItemsSource = Array.Empty<ArtifactInspectionProperty>();
        ExportArtifactDependenciesGrid.ItemsSource = Array.Empty<ExportArtifactDependencyRow>();
        ExportArtifactIssuesGrid.ItemsSource = Array.Empty<ArtifactInspectionIssue>();
        ExportArtifactFileSummaryText.Text = message;
        ExportArtifactSummaryText.Text = "Select a PNG, DDS, glTF, or GLB file to inspect.";
        ExportArtifactOpenFileButton.IsEnabled = false;
        ClearTextureImportCandidateDetails();
        ClearTextureReplacementDetails();
    }

    private async Task LoadTextureImportCandidateDetailsAsync(ExportWorkspaceRow row)
    {
        var reportPath = row.Entry.Manifest.TextureImportCandidateManifestPath;
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return;
        }

        try
        {
            var fullReportPath = Path.GetFullPath(reportPath);
            var manifest = await TextureImportCandidateService.LoadManifestAsync(fullReportPath);
            if (ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow selected ||
                !selected.Entry.Manifest.ExportId.Equals(
                    row.Entry.Manifest.ExportId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var reportDirectory = Path.GetDirectoryName(fullReportPath)!;
            var candidatePath = Path.GetFullPath(
                Path.Combine(reportDirectory, manifest.CandidateDdsRelativePath));
            _selectedTextureImportCandidate = manifest;
            _selectedTextureImportCandidatePath = candidatePath;
            TextureImportCandidateDetailsSummaryText.Text =
                $"{manifest.PackageAlias}.{manifest.ObjectName} | {manifest.PixelFormat} | " +
                $"{manifest.Width}x{manifest.Height} | {manifest.MipCount:N0} mip(s) | " +
                $"{manifest.AlphaPolicy}";
            var alphaSource = manifest.AlphaPolicy switch
            {
                TextureImportAlphaPolicy.PreserveOriginal => manifest.OriginalDdsPath,
                TextureImportAlphaPolicy.UseEdited => manifest.EditedDdsPath,
                TextureImportAlphaPolicy.ForceOpaque => "Generated fully opaque BC3 alpha blocks",
                _ => "Not independently addressable for this DDS format",
            };
            var rows = new List<TextureImportCandidateDetailRow>
            {
                new("Identity", "Parent native export", manifest.SourceExportId),
                new("Identity", "Object", $"{manifest.PackageAlias}.{manifest.ObjectName}"),
                new("Layout", "Format", $"{manifest.PixelFormat} ({manifest.DdsFourCc})"),
                new("Layout", "Dimensions / mips", $"{manifest.Width}x{manifest.Height} / {manifest.MipCount:N0}"),
                new("Layout", "Semantic", manifest.TextureSemantic),
                new("Inputs", "Original DDS (baseline)", manifest.OriginalDdsPath),
                new("Inputs", "Original SHA-256", manifest.OriginalDdsSha256),
                new("Inputs", "Edited image", manifest.EditedDdsPath),
                new("Inputs", "Editor output format", manifest.EditedInputFormat),
                new("Inputs", "Editor-declared mips", manifest.EditedInputMipCount.ToString("N0")),
                new("Inputs", "Edited SHA-256", manifest.EditedDdsSha256),
                new("Alpha", "Policy", manifest.AlphaPolicy.ToString()),
                new("Alpha", "Source", alphaSource),
                new("Alpha", "Action", manifest.AlphaAction),
                new("Output", "Final candidate", candidatePath),
                new("Output", "Candidate SHA-256", manifest.CandidateDdsSha256),
                new("Differences", "Edited blocks", $"{manifest.EditedBlocksDifferentFromOriginal:N0} / {manifest.TotalBlocks:N0}"),
                new("Differences", "Edited colour/channel blocks", manifest.EditedColourBlocksDifferentFromOriginal.ToString("N0")),
                new("Differences", "Edited alpha blocks", manifest.EditedAlphaBlocksDifferentFromOriginal.ToString("N0")),
                new("Differences", "Edited top-level pixels", manifest.EditedPixels.ToString("N0")),
                new("Normalization", "Generated mip levels", manifest.GeneratedMipCount.ToString("N0")),
                new("Normalization", "Touched blocks", manifest.TouchedBlocks.ToString("N0")),
                new("Normalization", "Unchanged blocks copied", manifest.UnchangedBlocksCopied.ToString("N0")),
                new("Differences", "Final changed blocks", manifest.CandidateBlocksDifferentFromOriginal.ToString("N0")),
                new("Validation", "Candidate inspection", $"{manifest.CandidateInspectionErrors:N0} error(s), {manifest.CandidateInspectionWarnings:N0} warning(s)"),
            };
            rows.AddRange(manifest.PreflightChecks.Select(check =>
                new TextureImportCandidateDetailRow(
                    "Preflight",
                    $"{check.Status}: {check.Code}",
                    check.Message)));
            TextureImportCandidateDetailsGrid.ItemsSource = rows;
            TextureCandidateOpenOriginalButton.IsEnabled = File.Exists(manifest.OriginalDdsPath);
            TextureCandidateOpenEditedButton.IsEnabled = File.Exists(manifest.EditedDdsPath);
            TextureCandidateOpenFinalButton.IsEnabled = File.Exists(candidatePath);
            TextureImportCandidateDetailsTab.IsSelected = true;
            Log(
                $"Texture import candidate details loaded: candidateId={manifest.CandidateId}, " +
                $"parentExportId={manifest.SourceExportId}, report={fullReportPath}, " +
                $"original={manifest.OriginalDdsPath}, edited={manifest.EditedDdsPath}, " +
                $"candidate={candidatePath}, alphaPolicy={manifest.AlphaPolicy}, " +
                $"preflightChecks={manifest.PreflightChecks.Count}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (ExportWorkspaceGrid.SelectedItem is ExportWorkspaceRow selected &&
                selected.Entry.Manifest.ExportId.Equals(
                    row.Entry.Manifest.ExportId,
                    StringComparison.OrdinalIgnoreCase))
            {
                TextureImportCandidateDetailsSummaryText.Text =
                    $"Import-candidate details could not be loaded: {exception.Message}";
                TextureImportCandidateDetailsGrid.ItemsSource = new[]
                {
                    new TextureImportCandidateDetailRow(
                        "Validation",
                        "Manifest load failed",
                        exception.Message),
                };
            }

            Log(
                $"Texture import candidate details load failed: exportId={row.Entry.Manifest.ExportId}, " +
                $"report={reportPath}, error={exception}");
        }
    }

    private void ClearTextureImportCandidateDetails()
    {
        _selectedTextureImportCandidate = null;
        _selectedTextureImportCandidatePath = null;
        if (TextureImportCandidateDetailsGrid is null)
        {
            return;
        }

        TextureImportCandidateDetailsGrid.ItemsSource = Array.Empty<TextureImportCandidateDetailRow>();
        TextureImportCandidateDetailsSummaryText.Text =
            "Select a texture import-candidate session.";
        TextureCandidateOpenOriginalButton.IsEnabled = false;
        TextureCandidateOpenEditedButton.IsEnabled = false;
        TextureCandidateOpenFinalButton.IsEnabled = false;
    }

    private async Task LoadTextureReplacementDetailsAsync(ExportWorkspaceRow row)
    {
        var payloadPath = row.Entry.Manifest.TextureReplacementPayloadManifestPath;
        var layoutPath = row.Entry.Manifest.TextureLayoutConfirmationManifestPath;
        var assemblyPath = row.Entry.Manifest.TextureExportAssemblyManifestPath;
        var rewritePlanPath = row.Entry.Manifest.PackageRewritePlanManifestPath;
        var logicalImagePath = row.Entry.Manifest.LogicalPackageImageManifestPath;
        var physicalPlanPath = row.Entry.Manifest.PhysicalPackagePlanManifestPath;
        var noOpProofPath = row.Entry.Manifest.NoOpUpkReconstructionManifestPath;
        var replacementUpkPath = row.Entry.Manifest.ReplacementUpkBuildManifestPath;
        var modBundlePath = row.Entry.Manifest.ModBundleManifestPath;
        var deploymentReadinessPath = row.Entry.Manifest.DeploymentReadinessManifestPath;
        if (string.IsNullOrWhiteSpace(payloadPath) && string.IsNullOrWhiteSpace(layoutPath) &&
            string.IsNullOrWhiteSpace(assemblyPath) && string.IsNullOrWhiteSpace(rewritePlanPath) &&
            string.IsNullOrWhiteSpace(logicalImagePath) && string.IsNullOrWhiteSpace(physicalPlanPath) &&
            string.IsNullOrWhiteSpace(noOpProofPath) && string.IsNullOrWhiteSpace(replacementUpkPath) &&
            string.IsNullOrWhiteSpace(modBundlePath) && string.IsNullOrWhiteSpace(deploymentReadinessPath))
        {
            return;
        }

        try
        {
            var details = new List<TextureImportCandidateDetailRow>();
            TextureLayoutConfirmationManifest? layout = null;
            TextureReplacementPayloadManifest? payload = null;
            TextureExportAssemblyManifest? assembly = null;
            PackageRewritePlanManifest? rewritePlan = null;
            LogicalPackageImageManifest? logicalImage = null;
            PhysicalPackagePlanManifest? physicalPlan = null;
            NoOpUpkReconstructionManifest? noOpProof = null;
            ReplacementUpkBuildManifest? replacementUpk = null;
            ArkiveModBundleManifest? modBundle = null;
            ModDeploymentReadinessManifest? deploymentReadiness = null;
            ModDeploymentInspection? deploymentInspection = null;
            if (!string.IsNullOrWhiteSpace(modBundlePath))
            {
                var fullModBundlePath = Path.GetFullPath(modBundlePath);
                modBundle = await ArkiveModBundleService.LoadBundleAsync(fullModBundlePath);
                _selectedModBundlePath = fullModBundlePath;
                var bundleDirectory = Path.GetDirectoryName(fullModBundlePath)!;
                _selectedModPayloadPath = Path.GetFullPath(
                    Path.Combine(bundleDirectory, modBundle.PayloadRelativePath));
                deploymentReadinessPath ??= Path.Combine(
                    bundleDirectory, modBundle.DeploymentReadinessRelativePath);
                details.AddRange(new[]
                {
                    new TextureImportCandidateDetailRow("Mod bundle", "Display name", modBundle.DisplayName),
                    new TextureImportCandidateDetailRow("Mod bundle", "Mod ID", modBundle.ModId),
                    new TextureImportCandidateDetailRow("Mod bundle", "Game profile", modBundle.GameProfile),
                    new TextureImportCandidateDetailRow("Mod bundle", "Install-relative package", modBundle.SourcePackageRelativePath),
                    new TextureImportCandidateDetailRow("Mod bundle", "Payload", _selectedModPayloadPath),
                    new TextureImportCandidateDetailRow("Mod bundle", "Payload bytes", modBundle.PayloadBytes.ToString("N0")),
                    new TextureImportCandidateDetailRow("Mod bundle", "Payload SHA-256", modBundle.PayloadSha256),
                    new TextureImportCandidateDetailRow("Compatibility", "Exact client required", modBundle.ExactClientMatchRequired.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Backup required", modBundle.BackupRequired.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Reversible install planned", modBundle.ReversibleInstallPlanned.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "Controlled install audit", modBundle.ReadyForControlledInstall.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Installation enabled", modBundle.InstallationEnabled.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Game files modified", modBundle.GameFilesModified.ToString()),
                });
            }
            if (!string.IsNullOrWhiteSpace(deploymentReadinessPath))
            {
                var fullReadinessPath = Path.GetFullPath(deploymentReadinessPath);
                deploymentReadiness = await ArkiveModBundleService.LoadReadinessAsync(fullReadinessPath);
                _selectedDeploymentReadinessPath = fullReadinessPath;
                details.AddRange(new[]
                {
                    new TextureImportCandidateDetailRow("Deployment rehearsal", "Audit ID", deploymentReadiness.AuditId),
                    new TextureImportCandidateDetailRow("Deployment rehearsal", "Install target", deploymentReadiness.InstallTargetPath),
                    new TextureImportCandidateDetailRow("Deployment rehearsal", "Proposed backup", deploymentReadiness.ProposedBackupPath),
                    new TextureImportCandidateDetailRow("Deployment rehearsal", "Required backup bytes", deploymentReadiness.RequiredBackupBytes.ToString("N0")),
                    new TextureImportCandidateDetailRow("Deployment rehearsal", "Available backup bytes", deploymentReadiness.AvailableBackupDriveBytes.ToString("N0")),
                    new TextureImportCandidateDetailRow("Validation", "Source identity", deploymentReadiness.SourceIdentityMatches.ToString()),
                    new TextureImportCandidateDetailRow("Validation", "Payload identity", deploymentReadiness.PayloadIdentityMatchesManifest.ToString()),
                    new TextureImportCandidateDetailRow("Validation", "Original physical name", deploymentReadiness.PayloadUsesOriginalPhysicalName.ToString()),
                    new TextureImportCandidateDetailRow("Validation", "Package summary identity", deploymentReadiness.PackageSummaryIdentityRetained.ToString()),
                    new TextureImportCandidateDetailRow("Validation", "Structural", deploymentReadiness.StructuralValidationPassed.ToString()),
                    new TextureImportCandidateDetailRow("UModel", "Catalogue", deploymentReadiness.ExternalCataloguePassed.ToString()),
                    new TextureImportCandidateDetailRow("UModel", "Native object load", deploymentReadiness.ExternalNativeLoadPassed.ToString()),
                    new TextureImportCandidateDetailRow("UModel", "Assets / validation files", $"{deploymentReadiness.ExternalAssetCount:N0} / {deploymentReadiness.ExternalValidationFileCount:N0}"),
                    new TextureImportCandidateDetailRow("Readiness", "Controlled install audit", deploymentReadiness.ReadyForControlledInstall.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Installation performed", deploymentReadiness.InstallationPerformed.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Game files modified", deploymentReadiness.GameFilesModified.ToString()),
                });
                details.AddRange(deploymentReadiness.Checks.Select(check =>
                    new TextureImportCandidateDetailRow(
                        "Deployment check", $"{check.Status}: {check.Code}", check.Message)));
                details.AddRange(deploymentReadiness.PlannedInstallSteps.Select((step, index) =>
                    new TextureImportCandidateDetailRow(
                        "Future install plan", $"{index + 1:N0}", step)));
            }
            if (!string.IsNullOrWhiteSpace(_selectedModBundlePath))
            {
                try
                {
                    deploymentInspection = await _modDeploymentService.InspectAsync(
                        CreateModDeploymentRequest(_selectedModBundlePath));
                    _selectedModDeploymentInspection = deploymentInspection;
                    _selectedModInstallationReceiptPath = deploymentInspection.ReceiptPath;
                    _selectedModBackupPath = deploymentInspection.BackupPath;
                    details.AddRange(new[]
                    {
                        new TextureImportCandidateDetailRow("Installed state", "Current state", deploymentInspection.State.ToString()),
                        new TextureImportCandidateDetailRow("Installed state", "Summary", deploymentInspection.Summary),
                        new TextureImportCandidateDetailRow("Installed state", "Target", deploymentInspection.TargetPath),
                        new TextureImportCandidateDetailRow("Installed state", "Current target SHA-256", deploymentInspection.CurrentTargetSha256 ?? "missing"),
                        new TextureImportCandidateDetailRow("Installed state", "Can install", deploymentInspection.CanInstall.ToString()),
                        new TextureImportCandidateDetailRow("Installed state", "Can restore", deploymentInspection.CanRestore.ToString()),
                        new TextureImportCandidateDetailRow("Backup", "Path", deploymentInspection.BackupPath),
                        new TextureImportCandidateDetailRow("Backup", "Exists / verified", $"{deploymentInspection.BackupExists} / {deploymentInspection.BackupVerified}"),
                        new TextureImportCandidateDetailRow("Install receipt", "Path", deploymentInspection.ReceiptPath),
                        new TextureImportCandidateDetailRow("Install receipt", "Exists", (deploymentInspection.Receipt is not null).ToString()),
                    });
                    if (deploymentInspection.Receipt is not null)
                    {
                        details.AddRange(new[]
                        {
                            new TextureImportCandidateDetailRow("Install receipt", "Installation ID", deploymentInspection.Receipt.InstallationId),
                            new TextureImportCandidateDetailRow("Install receipt", "Installed at", deploymentInspection.Receipt.InstalledAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "not committed"),
                            new TextureImportCandidateDetailRow("Install receipt", "Restored at", deploymentInspection.Receipt.RestoredAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "not restored"),
                            new TextureImportCandidateDetailRow("Install receipt", "Game files modified", deploymentInspection.Receipt.GameFilesModified.ToString()),
                        });
                        details.AddRange(deploymentInspection.Receipt.Checks.Select(check =>
                            new TextureImportCandidateDetailRow(
                                "Install check", $"{check.Status}: {check.Code}", check.Message)));
                    }
                    Log(
                        $"Mod deployment state inspected: modId={deploymentInspection.Bundle.ModId}, " +
                        $"state={deploymentInspection.State}, canInstall={deploymentInspection.CanInstall}, " +
                        $"canRestore={deploymentInspection.CanRestore}, payloadVerified={deploymentInspection.PayloadVerified}, " +
                        $"backupExists={deploymentInspection.BackupExists}, backupVerified={deploymentInspection.BackupVerified}, " +
                        $"target={deploymentInspection.TargetPath}, targetSha={deploymentInspection.CurrentTargetSha256 ?? "missing"}, " +
                        $"backup={deploymentInspection.BackupPath}, receipt={deploymentInspection.ReceiptPath}.");
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    details.Add(new TextureImportCandidateDetailRow(
                        "Installed state", "Deployment unavailable", exception.Message));
                    Log(
                        $"Mod deployment state inspection failed: bundle={_selectedModBundlePath}, error={exception}");
                }
            }
            if (!string.IsNullOrWhiteSpace(replacementUpkPath))
            {
                var fullReplacementPath = Path.GetFullPath(replacementUpkPath);
                var outputVerification = await ReplacementUpkBuildService.VerifyOutputAsync(
                    fullReplacementPath);
                replacementUpk = outputVerification.Manifest;
                _selectedReplacementUpkBuildPath = fullReplacementPath;
                _selectedReplacementUpkOutputPath = outputVerification.OutputUpkPath;
                noOpProofPath = replacementUpk.NoOpUpkReconstructionManifestPath;
                details.AddRange(new[]
                {
                    new TextureImportCandidateDetailRow("Staged mod UPK", "Build ID", replacementUpk.BuildId),
                    new TextureImportCandidateDetailRow("Staged mod UPK", "Output file", replacementUpk.OutputUpkRelativePath),
                    new TextureImportCandidateDetailRow("Staged mod UPK", "Physical bytes", replacementUpk.OutputPhysicalPackageBytes.ToString("N0")),
                    new TextureImportCandidateDetailRow("Staged mod UPK", "Output SHA-256", replacementUpk.OutputPackageSha256),
                    new TextureImportCandidateDetailRow("External inspection", "Output path", outputVerification.OutputUpkPath),
                    new TextureImportCandidateDetailRow("External inspection", "Manifest size / SHA-256 reverified", "True"),
                    new TextureImportCandidateDetailRow("Chunk writer", "Rebuilt / retained", $"{replacementUpk.RebuiltChunkCount:N0} / {replacementUpk.ByteIdenticalChunkCount:N0}"),
                    new TextureImportCandidateDetailRow("Chunk writer", "Protected rebuilt", replacementUpk.ProtectedRebuiltChunkCount.ToString("N0")),
                    new TextureImportCandidateDetailRow("Chunk writer", "Encoded blocks", replacementUpk.EncodedBlockCount.ToString("N0")),
                    new TextureImportCandidateDetailRow("Verification", "Logical image exact", replacementUpk.ExpectedLogicalImageMatched.ToString()),
                    new TextureImportCandidateDetailRow("Verification", "Target export retargeted", replacementUpk.TargetExportRetargeted.ToString()),
                    new TextureImportCandidateDetailRow("Verification", "Unrelated exports equivalent", replacementUpk.UnrelatedExportsEquivalent.ToString()),
                    new TextureImportCandidateDetailRow("Verification", "Original / replacement bytes", $"{replacementUpk.OriginalExportBytesRetained} / {replacementUpk.ReplacementExportBytesVerified}"),
                    new TextureImportCandidateDetailRow("Verification", "Structural validation", replacementUpk.StructuralValidationPassed.ToString()),
                    new TextureImportCandidateDetailRow("Verification", "Target native mip decode", replacementUpk.TargetTextureNativeDecodePassed.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "Replacement build passed", replacementUpk.ReplacementPackageBuildPassed.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Contains replacement", replacementUpk.OutputContainsReplacement.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Deployment enabled", replacementUpk.DeploymentReady.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Source package modified", replacementUpk.PackageWasModified.ToString()),
                });
                details.AddRange(replacementUpk.Checks.Select(check =>
                    new TextureImportCandidateDetailRow(
                        "Staged-UPK check", $"{check.Status}: {check.Code}", check.Message)));
                details.AddRange(replacementUpk.Chunks.Select(chunk =>
                    new TextureImportCandidateDetailRow(
                        $"Output chunk {chunk.ChunkIndex}",
                        $"L 0x{chunk.OutputLogicalOffset:X}+0x{chunk.OutputLogicalBytes:X}; P 0x{chunk.OutputPhysicalOffset:X}+0x{chunk.OutputPhysicalBytes:X}",
                        $"rebuilt={chunk.Rebuilt}; sourceExact={chunk.SourcePhysicalBytesRetained}; flags=0x{chunk.LostArkFlags:X}; {chunk.Action}")));
            }
            if (!string.IsNullOrWhiteSpace(noOpProofPath))
            {
                var fullProofPath = Path.GetFullPath(noOpProofPath);
                noOpProof = await NoOpUpkReconstructionService.LoadManifestAsync(fullProofPath);
                _selectedNoOpUpkReconstructionPath = fullProofPath;
                physicalPlanPath = noOpProof.PhysicalPackagePlanManifestPath;
                details.AddRange(new[]
                {
                    new TextureImportCandidateDetailRow("No-op UPK", "Reconstruction ID", noOpProof.ReconstructionId),
                    new TextureImportCandidateDetailRow("No-op UPK", "Output file", noOpProof.OutputUpkRelativePath),
                    new TextureImportCandidateDetailRow("No-op UPK", "Physical bytes", noOpProof.OutputPhysicalPackageBytes.ToString("N0")),
                    new TextureImportCandidateDetailRow("No-op UPK", "Output SHA-256", noOpProof.OutputPackageSha256),
                    new TextureImportCandidateDetailRow("Proof", "Physical byte-identical", noOpProof.PhysicalFileByteIdentical.ToString()),
                    new TextureImportCandidateDetailRow("Proof", "Logical byte-identical", noOpProof.LogicalImageByteIdentical.ToString()),
                    new TextureImportCandidateDetailRow("Proof", "Summary equivalent", noOpProof.PackageSummaryEquivalent.ToString()),
                    new TextureImportCandidateDetailRow("Proof", "Chunk table equivalent", noOpProof.ChunkTableEquivalent.ToString()),
                    new TextureImportCandidateDetailRow("Proof", "Catalogue equivalent", noOpProof.AssetCatalogueEquivalent.ToString()),
                    new TextureImportCandidateDetailRow("Proof", "Structural validation", noOpProof.StructuralValidationPassed.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "No-op proof passed", noOpProof.NoOpReconstructionProofPassed.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "Replacement writer", noOpProof.ReplacementPhysicalWriterReady.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Contains replacement", noOpProof.OutputContainsReplacement.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Package modified", noOpProof.PackageWasModified.ToString()),
                });
                details.AddRange(noOpProof.Checks.Select(check =>
                    new TextureImportCandidateDetailRow(
                        "No-op check", $"{check.Status}: {check.Code}", check.Message)));
                details.AddRange(noOpProof.CopyRegions.Select(region =>
                    new TextureImportCandidateDetailRow(
                        "No-op region",
                        $"{region.RegionIndex}: {region.Kind} @ 0x{region.PhysicalOffset:X}+0x{region.Bytes:X}",
                        $"byte-identical={region.ByteIdentical}; SHA-256={region.WrittenSha256}")));
            }
            if (!string.IsNullOrWhiteSpace(physicalPlanPath))
            {
                var fullPhysicalPlanPath = Path.GetFullPath(physicalPlanPath);
                physicalPlan = await PhysicalPackagePlanService.LoadManifestAsync(fullPhysicalPlanPath);
                _selectedPhysicalPackagePlanPath = fullPhysicalPlanPath;
                logicalImagePath = physicalPlan.LogicalPackageImageManifestPath;
                details.AddRange(new[]
                {
                    new TextureImportCandidateDetailRow("Physical plan", "Plan ID", physicalPlan.PhysicalPlanId),
                    new TextureImportCandidateDetailRow("Physical plan", "Physical bytes", physicalPlan.SourcePhysicalPackageBytes.ToString("N0")),
                    new TextureImportCandidateDetailRow("Physical plan", "Chunks", physicalPlan.CompressedChunkCount.ToString("N0")),
                    new TextureImportCandidateDetailRow("Physical plan", "Protected / raw chunks", $"{physicalPlan.ProtectedChunkCount:N0} / {physicalPlan.RawChunkCount:N0}"),
                    new TextureImportCandidateDetailRow("Physical plan", "Compression blocks", physicalPlan.TotalBlockCount.ToString("N0")),
                    new TextureImportCandidateDetailRow("Physical plan", "Physical prefix", $"{physicalPlan.PhysicalPrefixBytes:N0} bytes | {physicalPlan.PhysicalPrefixSha256}"),
                    new TextureImportCandidateDetailRow("Physical plan", "Copy regions", physicalPlan.CopyRegions.Count.ToString("N0")),
                    new TextureImportCandidateDetailRow("Replacement impact", "Affected chunks", physicalPlan.ReplacementAffectedChunkCount.ToString("N0")),
                    new TextureImportCandidateDetailRow("Readiness", "Chunk envelopes parsed", physicalPlan.AllChunkEnvelopesParsed.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "Protected blocks readable", physicalPlan.AllProtectedBlocksReadable.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "No-op reconstruction", physicalPlan.PlanReadyForNoOpReconstruction.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "Replacement layout understood", physicalPlan.ReplacementChunkLayoutUnderstood.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "Replacement writer", physicalPlan.ReplacementPhysicalWriterReady.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Package modified", physicalPlan.PackageWasModified.ToString()),
                });
                details.AddRange(physicalPlan.Checks.Select(check =>
                    new TextureImportCandidateDetailRow(
                        "Physical check", $"{check.Status}: {check.Code}", check.Message)));
                details.AddRange(physicalPlan.Chunks.Select(chunk =>
                    new TextureImportCandidateDetailRow(
                        $"Physical chunk {chunk.ChunkIndex}",
                        $"L 0x{chunk.LogicalOffset:X}+0x{chunk.LogicalBytes:X}; P 0x{chunk.PhysicalOffset:X}+0x{chunk.PhysicalBytes:X}",
                        $"{chunk.StorageKind}; flags=0x{chunk.LostArkFlags:X}; blocks={chunk.BlockCount}; {chunk.ReplacementAction}")));
            }
            if (!string.IsNullOrWhiteSpace(logicalImagePath))
            {
                var fullImagePath = Path.GetFullPath(logicalImagePath);
                logicalImage = await LogicalPackageImageService.LoadManifestAsync(fullImagePath);
                _selectedLogicalPackageImagePath = fullImagePath;
                rewritePlanPath = logicalImage.PackageRewritePlanManifestPath;
                details.AddRange(new[]
                {
                    new TextureImportCandidateDetailRow("Logical image", "Image ID", logicalImage.ImageId),
                    new TextureImportCandidateDetailRow("Logical image", "Staging file", logicalImage.LogicalImageRelativePath),
                    new TextureImportCandidateDetailRow("Logical image", "Logical bytes", $"{logicalImage.SourceLogicalPackageBytes:N0} -> {logicalImage.LogicalImageBytes:N0}"),
                    new TextureImportCandidateDetailRow("Logical image", "Image SHA-256", logicalImage.LogicalImageSha256),
                    new TextureImportCandidateDetailRow("Verification", "Unplanned source bytes exact", logicalImage.AllUnplannedSourceBytesIdentical.ToString()),
                    new TextureImportCandidateDetailRow("Verification", "Compared source bytes", logicalImage.UnplannedSourceBytesCompared.ToString("N0")),
                    new TextureImportCandidateDetailRow("Verification", "Table patches applied", logicalImage.AllTablePatchesApplied.ToString()),
                    new TextureImportCandidateDetailRow("Verification", "Bulk offsets rebased", logicalImage.AllBulkOffsetsRebased.ToString()),
                    new TextureImportCandidateDetailRow("Verification", "Original export retained", logicalImage.OriginalExportBytesRetained.ToString()),
                    new TextureImportCandidateDetailRow("Verification", "Appended export verified", logicalImage.AppendedReplacementVerified.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "Logical image", logicalImage.LogicalImageReadyForPhysicalWriter.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "Physical chunk writer", logicalImage.PhysicalChunkWriterReady.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "Package-file writer", logicalImage.PackageFileWriterReady.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Source identity retained", logicalImage.SourcePackageIdentityRetained.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Package modified", logicalImage.PackageWasModified.ToString()),
                });
                details.AddRange(logicalImage.Checks.Select(check =>
                    new TextureImportCandidateDetailRow(
                        "Image check", $"{check.Status}: {check.Code}", check.Message)));
                details.AddRange(logicalImage.TablePatches.Select(patch =>
                    new TextureImportCandidateDetailRow(
                        "Verified table patch",
                        $"Export {patch.ExportIndex} {patch.Field} @ 0x{patch.FieldLogicalOffset:X}",
                        $"{patch.ExpectedOriginalValue} -> {patch.VerifiedImageValue}")));
                details.AddRange(logicalImage.BulkOffsetRebases.Select(rebase =>
                    new TextureImportCandidateDetailRow(
                        $"Verified mip {rebase.MipIndex}",
                        $"Logical 0x{rebase.FieldLogicalOffset:X}",
                        $"0x{rebase.AssemblyValue:X} -> 0x{rebase.VerifiedImageValue:X}")));
                details.AddRange(logicalImage.Ranges.Select(range =>
                    new TextureImportCandidateDetailRow(
                        "Verified range",
                        $"{range.Label} @ 0x{range.LogicalOffset:X}+0x{range.Bytes:X}",
                        $"verified={range.Verified}; SHA-256={range.ImageSha256}")));
            }
            if (!string.IsNullOrWhiteSpace(rewritePlanPath))
            {
                var fullPlanPath = Path.GetFullPath(rewritePlanPath);
                rewritePlan = await PackageRewritePlanService.LoadManifestAsync(fullPlanPath);
                _selectedPackageRewritePlanPath = fullPlanPath;
                assemblyPath = rewritePlan.TextureExportAssemblyManifestPath;
                details.AddRange(new[]
                {
                    new TextureImportCandidateDetailRow("Rewrite plan", "Plan ID", rewritePlan.PlanId),
                    new TextureImportCandidateDetailRow("Rewrite plan", "Selected strategy", rewritePlan.SelectedStrategy),
                    new TextureImportCandidateDetailRow("Rewrite plan", "Strategy reason", rewritePlan.SelectedStrategyReason),
                    new TextureImportCandidateDetailRow("Rewrite plan", "Original serial range", $"0x{rewritePlan.SourceSerialOffset:X}+0x{rewritePlan.SourceSerialSize:X}"),
                    new TextureImportCandidateDetailRow("Rewrite plan", "Planned serial range", $"0x{rewritePlan.PlannedSerialOffset:X}+0x{rewritePlan.PlannedSerialSize:X}"),
                    new TextureImportCandidateDetailRow("Rewrite plan", "Logical package", $"{rewritePlan.SourceLogicalPackageBytes:N0} -> {rewritePlan.PlannedLogicalPackageBytes:N0} bytes"),
                    new TextureImportCandidateDetailRow("Rewrite plan", "Append padding / growth", $"{rewritePlan.AppendPaddingBytes:N0} / {rewritePlan.PlannedLogicalGrowthBytes:N0} bytes"),
                    new TextureImportCandidateDetailRow("Rejected inline", "Size delta", $"{rewritePlan.InlineSpliceSizeDelta:+#,0;-#,0;0} bytes"),
                    new TextureImportCandidateDetailRow("Rejected inline", "Exports that would move", rewritePlan.InlineSpliceRelocatedExportCount.ToString("N0")),
                    new TextureImportCandidateDetailRow("Safety", "Existing exports relocated", rewritePlan.ExistingExportsRelocated.ToString("N0")),
                    new TextureImportCandidateDetailRow("Safety", "Existing payloads modified", rewritePlan.ExistingExportPayloadsModified.ToString("N0")),
                    new TextureImportCandidateDetailRow("Readiness", "Logical image plan", rewritePlan.PlanReadyForLogicalImageWriter.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "Physical chunk plan", rewritePlan.PhysicalChunkPlanReady.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "Package-file writer", rewritePlan.PackageFileWriterReady.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Package modified", rewritePlan.PackageWasModified.ToString()),
                });
                details.AddRange(rewritePlan.Checks.Select(check =>
                    new TextureImportCandidateDetailRow(
                        "Rewrite check", $"{check.Status}: {check.Code}", check.Message)));
                details.AddRange(rewritePlan.SelectedTablePatches.Select(patch =>
                    new TextureImportCandidateDetailRow(
                        "Table patch", $"Export {patch.ExportIndex} {patch.Field} @ 0x{patch.FieldLogicalOffset:X}",
                        $"{patch.OriginalValue} -> {patch.PlannedValue}; {patch.LogicalStorage}")));
                details.AddRange(rewritePlan.BulkOffsetRebases.Select(rebase =>
                    new TextureImportCandidateDetailRow(
                        $"Mip {rebase.MipIndex} rebase", $"Blob +0x{rebase.FieldRelativeOffset:X}",
                        $"0x{rebase.AssemblyValue:X} -> 0x{rebase.PlannedValue:X}")));
                details.AddRange(rewritePlan.ChunkImpacts.Select(impact =>
                    new TextureImportCandidateDetailRow(
                        "Chunk impact",
                        impact.ChunkIndex is null ? "Append/raw region" : $"Chunk {impact.ChunkIndex}",
                        $"0x{impact.LogicalOffset:X}+0x{impact.LogicalBytes:X}; {impact.RequiredAction}")));
            }
            if (!string.IsNullOrWhiteSpace(assemblyPath))
            {
                var fullAssemblyPath = Path.GetFullPath(assemblyPath);
                assembly = await TextureExportAssemblyService.LoadManifestAsync(fullAssemblyPath);
                _selectedTextureExportAssemblyPath = fullAssemblyPath;
                payloadPath = assembly.TextureReplacementPayloadManifestPath;
                details.AddRange(new[]
                {
                    new TextureImportCandidateDetailRow("Assembly", "Assembly ID", assembly.AssemblyId),
                    new TextureImportCandidateDetailRow("Assembly", "No-op byte-identical", assembly.NoOpExportByteIdentical.ToString()),
                    new TextureImportCandidateDetailRow("Assembly", "Source export", $"{assembly.SourceSerialSize:N0} bytes | {assembly.SourceExportSha256}"),
                    new TextureImportCandidateDetailRow("Assembly", "Replacement export", $"{assembly.ReplacementExportBytes:N0} bytes | {assembly.ReplacementExportSha256}"),
                    new TextureImportCandidateDetailRow("Assembly", "Replacement size delta", $"{assembly.ReplacementSizeDelta:+#,0;-#,0;0} bytes"),
                    new TextureImportCandidateDetailRow("Preservation", "Prefix / suffix", $"{assembly.PreservedPrefixBytes:N0} / {assembly.PreservedSuffixBytes:N0} bytes"),
                    new TextureImportCandidateDetailRow("Preservation", "All non-mip bytes", assembly.AllNonMipBytesPreserved.ToString()),
                    new TextureImportCandidateDetailRow("Validation", "All descriptors replayed", assembly.AllDescriptorsReplayed.ToString()),
                    new TextureImportCandidateDetailRow("Validation", "All payloads decode", assembly.AllStoragePayloadsDecode.ToString()),
                    new TextureImportCandidateDetailRow("Validation", "All quality retained", assembly.AllQualityGatesRetained.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "Ready for rewrite plan", assembly.ReplacementBlobReadyForRewritePlan.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Package modified", assembly.PackageWasModified.ToString()),
                });
                details.AddRange(assembly.Checks.Select(check =>
                    new TextureImportCandidateDetailRow(
                        "Assembly check", $"{check.Status}: {check.Code}", check.Message)));
                details.AddRange(assembly.Mips.Select(mip =>
                    new TextureImportCandidateDetailRow(
                        $"Mip {mip.MipIndex} assembly",
                        $"{mip.Width}x{mip.Height} {mip.StorageCodec}",
                        $"header +0x{mip.HeaderRelativeOffset:X}; payload +0x{mip.PayloadRelativeOffset:X}; " +
                        $"stored {mip.SourceStoredBytes:N0} -> {mip.ReplacementStoredBytes:N0}; " +
                        $"descriptor={mip.DescriptorReplayed}; decode={mip.DecodeBackVerified}")));
            }
            if (!string.IsNullOrWhiteSpace(payloadPath))
            {
                var fullPayloadPath = Path.GetFullPath(payloadPath);
                payload = await TextureReplacementPayloadService.LoadManifestAsync(fullPayloadPath);
                _selectedTextureReplacementPayloadPath = fullPayloadPath;
                layoutPath = payload.LayoutConfirmationManifestPath;
                details.AddRange(new[]
                {
                    new TextureImportCandidateDetailRow("Identity", "Payload set", payload.PayloadSetId),
                    new TextureImportCandidateDetailRow("Identity", "Candidate", payload.CandidateId),
                    new TextureImportCandidateDetailRow("Layout", "Object", $"{payload.PackageAlias}.{payload.ObjectName}"),
                    new TextureImportCandidateDetailRow("Layout", "Format", payload.PixelFormat),
                    new TextureImportCandidateDetailRow("Layout", "Dimensions / mips", $"{payload.Width}x{payload.Height} / {payload.MipCount:N0}"),
                    new TextureImportCandidateDetailRow("Encoder", "Unity Crunch", payload.CrunchEncoder),
                    new TextureImportCandidateDetailRow("Encoder", "Source payloads copied", payload.SourcePayloadsCopied.ToString("N0")),
                    new TextureImportCandidateDetailRow("Encoder", "CRN payloads encoded", payload.CrunchPayloadsEncoded.ToString("N0")),
                    new TextureImportCandidateDetailRow("Encoder", "UE3/LZ4 payloads encoded", payload.Lz4PayloadsEncoded.ToString("N0")),
                    new TextureImportCandidateDetailRow("Size", "Source stored bytes", $"{payload.SourceStoredBytes:N0}"),
                    new TextureImportCandidateDetailRow("Size", "Replacement stored bytes", $"{payload.ReplacementStoredBytes:N0}"),
                    new TextureImportCandidateDetailRow("Readiness", "All payloads decode", payload.AllStoragePayloadsDecode.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "All quality gates", payload.AllQualityGatesPassed.ToString()),
                    new TextureImportCandidateDetailRow("Readiness", "Ready for future writer", payload.PayloadSetReadyForWriter.ToString()),
                    new TextureImportCandidateDetailRow("Safety", "Package modified", payload.PackageWasModified.ToString()),
                });
                details.AddRange(payload.Checks.Select(check =>
                    new TextureImportCandidateDetailRow(
                        "Payload check", $"{check.Status}: {check.Code}", check.Message)));
                details.AddRange(payload.Mips.Select(mip =>
                    new TextureImportCandidateDetailRow(
                        $"Mip {mip.MipIndex}",
                        $"{mip.Width}x{mip.Height} {mip.SourceStorageCodec}",
                        $"{mip.EncodingAction}; stored {mip.SourceStoredBytes:N0} -> " +
                        $"{mip.ReplacementStoredBytes:N0} bytes; exactGPU={mip.Quality.ExactGpuBlocks}; " +
                        $"PSNR={(mip.Quality.ExactGpuBlocks ? "exact" : FormatQualityNumber(mip.Quality.PsnrDb))} dB; " +
                        $"quality={mip.Quality.QualityGatePassed}")));
            }
            if (!string.IsNullOrWhiteSpace(layoutPath))
            {
                var fullLayoutPath = Path.GetFullPath(layoutPath);
                layout = await TextureLayoutConfirmationService.LoadManifestAsync(fullLayoutPath);
                _selectedTextureLayoutConfirmationPath = fullLayoutPath;
                details.AddRange(new[]
                {
                    new TextureImportCandidateDetailRow("Confirmation", "Confirmation ID", layout.ConfirmationId),
                    new TextureImportCandidateDetailRow("Confirmation", "Scope", layout.Scope),
                    new TextureImportCandidateDetailRow("Confirmation", "Mip payload layout", layout.MipPayloadLayoutConfirmed.ToString()),
                    new TextureImportCandidateDetailRow("Confirmation", "Package rewrite", layout.PackageRewriteConfirmed.ToString()),
                    new TextureImportCandidateDetailRow("Confirmation", "Mip array range", $"+0x{layout.MipArrayRelativeOffset:X}..+0x{layout.MipArrayEndRelativeOffset:X}"),
                    new TextureImportCandidateDetailRow("Confirmation", "Opaque prefix", $"{layout.OpaqueNativePrefixBytes:N0} bytes | {layout.OpaqueNativePrefixSha256}"),
                    new TextureImportCandidateDetailRow("Confirmation", "Opaque suffix", $"{layout.OpaqueNativeSuffixBytes:N0} bytes | {layout.OpaqueNativeSuffixSha256}"),
                });
                details.AddRange(layout.Checks.Select(check =>
                    new TextureImportCandidateDetailRow(
                        "Layout check", $"{check.Status}: {check.Code}", check.Message)));
            }
            if (ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow selected ||
                !selected.Entry.Manifest.ExportId.Equals(
                    row.Entry.Manifest.ExportId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            TextureReplacementDetailsGrid.ItemsSource = details;
            TextureReplacementOpenLayoutButton.IsEnabled =
                !string.IsNullOrWhiteSpace(_selectedTextureLayoutConfirmationPath) &&
                File.Exists(_selectedTextureLayoutConfirmationPath);
            TextureReplacementOpenPayloadButton.IsEnabled =
                !string.IsNullOrWhiteSpace(_selectedTextureReplacementPayloadPath) &&
                File.Exists(_selectedTextureReplacementPayloadPath);
            TextureReplacementOpenAssemblyButton.IsEnabled =
                !string.IsNullOrWhiteSpace(_selectedTextureExportAssemblyPath) &&
                File.Exists(_selectedTextureExportAssemblyPath);
            TextureReplacementOpenRewritePlanButton.IsEnabled =
                !string.IsNullOrWhiteSpace(_selectedPackageRewritePlanPath) &&
                File.Exists(_selectedPackageRewritePlanPath);
            TextureReplacementOpenLogicalImageButton.IsEnabled =
                !string.IsNullOrWhiteSpace(_selectedLogicalPackageImagePath) &&
                File.Exists(_selectedLogicalPackageImagePath);
            TextureReplacementOpenPhysicalPlanButton.IsEnabled =
                !string.IsNullOrWhiteSpace(_selectedPhysicalPackagePlanPath) &&
                File.Exists(_selectedPhysicalPackagePlanPath);
            TextureReplacementOpenNoOpProofButton.IsEnabled =
                !string.IsNullOrWhiteSpace(_selectedNoOpUpkReconstructionPath) &&
                File.Exists(_selectedNoOpUpkReconstructionPath);
            TextureReplacementOpenStagedUpkManifestButton.IsEnabled =
                !string.IsNullOrWhiteSpace(_selectedReplacementUpkBuildPath) &&
                File.Exists(_selectedReplacementUpkBuildPath);
            var canInspectStagedUpk = _umodelBridge is not null &&
                                      !string.IsNullOrWhiteSpace(_selectedReplacementUpkBuildPath) &&
                                      !string.IsNullOrWhiteSpace(_selectedReplacementUpkOutputPath) &&
                                      File.Exists(_selectedReplacementUpkOutputPath);
            TextureReplacementBrowseStagedUpkButton.IsEnabled = canInspectStagedUpk;
            TextureReplacementInspectStagedAssetButton.IsEnabled = canInspectStagedUpk;
            TextureReplacementOpenModBundleButton.IsEnabled =
                !string.IsNullOrWhiteSpace(_selectedModBundlePath) &&
                File.Exists(_selectedModBundlePath);
            TextureReplacementOpenReadinessButton.IsEnabled =
                !string.IsNullOrWhiteSpace(_selectedDeploymentReadinessPath) &&
                File.Exists(_selectedDeploymentReadinessPath);
            TextureReplacementOpenModFolderButton.IsEnabled =
                !string.IsNullOrWhiteSpace(_selectedModBundlePath) &&
                Directory.Exists(Path.GetDirectoryName(_selectedModBundlePath));
            TextureReplacementInstallModButton.IsEnabled =
                !_textureReplacementInProgress && deploymentInspection?.CanInstall == true;
            TextureReplacementRestoreOriginalButton.IsEnabled =
                !_textureReplacementInProgress && deploymentInspection?.CanRestore == true;
            TextureReplacementOpenInstallReceiptButton.IsEnabled =
                !string.IsNullOrWhiteSpace(_selectedModInstallationReceiptPath) &&
                File.Exists(_selectedModInstallationReceiptPath);
            TextureReplacementOpenBackupFolderButton.IsEnabled =
                !string.IsNullOrWhiteSpace(_selectedModBackupPath) &&
                Directory.Exists(Path.GetDirectoryName(_selectedModBackupPath));
            TextureReplacementDetailsSummaryText.Text = deploymentInspection is not null
                ? $"{deploymentInspection.Bundle.DisplayName} | state={deploymentInspection.State} | " +
                  $"install={deploymentInspection.CanInstall} | restore={deploymentInspection.CanRestore}"
                : modBundle is not null && deploymentReadiness is not null
                ? $"{modBundle.DisplayName} | deployment rehearsal ready=" +
                  $"{deploymentReadiness.ReadyForControlledInstall} | installed state unavailable"
                : replacementUpk is not null
                ? $"{replacementUpk.PackageAlias}.{replacementUpk.ObjectName} | staged replacement UPK=" +
                  $"{replacementUpk.ReplacementPackageBuildPassed} | deployment enabled: no | source modified: no"
                : noOpProof is not null
                ? $"{noOpProof.PackageAlias}.{noOpProof.ObjectName} | no-op UPK proof=" +
                  $"{noOpProof.NoOpReconstructionProofPassed} | replacement included: no | source modified: no"
                : physicalPlan is not null
                ? $"{physicalPlan.PackageAlias}.{physicalPlan.ObjectName} | physical plan ready=" +
                  $"{physicalPlan.PlanReadyForNoOpReconstruction} | replacement writer ready: no | package modified: no"
                : logicalImage is not null
                ? $"{logicalImage.PackageAlias}.{logicalImage.ObjectName} | logical image verified=" +
                  $"{logicalImage.LogicalImageReadyForPhysicalWriter} | physical writer ready: no | package modified: no"
                : rewritePlan is not null
                ? $"{rewritePlan.PackageAlias}.{rewritePlan.ObjectName} | logical plan ready=" +
                  $"{rewritePlan.PlanReadyForLogicalImageWriter} | physical writer ready: no | package modified: no"
                : assembly is not null
                    ? $"{assembly.PackageAlias}.{assembly.ObjectName} | no-op identical=" +
                      $"{assembly.NoOpExportByteIdentical} | replacement blob ready=" +
                      $"{assembly.ReplacementBlobReadyForRewritePlan} | package modified: no"
                    : payload is not null
                        ? $"{payload.PackageAlias}.{payload.ObjectName} | {payload.PixelFormat} | " +
                          $"payload-ready={payload.PayloadSetReadyForWriter} | package modified: no"
                        : $"{layout!.PackageAlias}.{layout.ObjectName} | {layout.PixelFormat} | " +
                          "mip-payload layout confirmed; package writer not yet confirmed";
            TextureReplacementDetailsTab.IsSelected = true;
            Log(
                $"Texture replacement details loaded: exportId={row.Entry.Manifest.ExportId}, " +
                $"layout={_selectedTextureLayoutConfirmationPath ?? "none"}, " +
                $"payload={_selectedTextureReplacementPayloadPath ?? "none"}, " +
                $"assembly={_selectedTextureExportAssemblyPath ?? "none"}, " +
                $"rewritePlan={_selectedPackageRewritePlanPath ?? "none"}, " +
                $"logicalImage={_selectedLogicalPackageImagePath ?? "none"}, " +
                $"physicalPlan={_selectedPhysicalPackagePlanPath ?? "none"}, " +
                $"noOpProof={_selectedNoOpUpkReconstructionPath ?? "none"}, " +
                $"replacementUpk={_selectedReplacementUpkBuildPath ?? "none"}, " +
                $"replacementOutput={_selectedReplacementUpkOutputPath ?? "none"}, " +
                $"modBundle={_selectedModBundlePath ?? "none"}, " +
                $"deploymentReadiness={_selectedDeploymentReadinessPath ?? "none"}, " +
                $"installationReceipt={_selectedModInstallationReceiptPath ?? "none"}, " +
                $"deploymentState={deploymentInspection?.State.ToString() ?? "unavailable"}, " +
                $"umodelInspectionReady={canInspectStagedUpk}, rows={details.Count}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TextureReplacementDetailsSummaryText.Text =
                $"Replacement-readiness details could not be loaded: {exception.Message}";
            TextureReplacementDetailsGrid.ItemsSource = new[]
            {
                new TextureImportCandidateDetailRow("Validation", "Manifest load failed", exception.Message),
            };
            Log(
                $"Texture replacement details load failed: exportId={row.Entry.Manifest.ExportId}, " +
                $"layout={layoutPath ?? "none"}, payload={payloadPath ?? "none"}, " +
                $"assembly={assemblyPath ?? "none"}, rewritePlan={rewritePlanPath ?? "none"}, " +
                $"logicalImage={logicalImagePath ?? "none"}, physicalPlan={physicalPlanPath ?? "none"}, " +
                $"noOpProof={noOpProofPath ?? "none"}, replacementUpk={replacementUpkPath ?? "none"}, " +
                $"modBundle={modBundlePath ?? "none"}, readiness={deploymentReadinessPath ?? "none"}, error={exception}");
        }
    }

    private void ClearTextureReplacementDetails()
    {
        _selectedTextureLayoutConfirmationPath = null;
        _selectedTextureReplacementPayloadPath = null;
        _selectedTextureExportAssemblyPath = null;
        _selectedPackageRewritePlanPath = null;
        _selectedLogicalPackageImagePath = null;
        _selectedPhysicalPackagePlanPath = null;
        _selectedNoOpUpkReconstructionPath = null;
        _selectedReplacementUpkBuildPath = null;
        _selectedReplacementUpkOutputPath = null;
        _selectedModBundlePath = null;
        _selectedDeploymentReadinessPath = null;
        _selectedModPayloadPath = null;
        _selectedModInstallationReceiptPath = null;
        _selectedModBackupPath = null;
        _selectedModDeploymentInspection = null;
        if (TextureReplacementDetailsGrid is null) return;
        TextureReplacementDetailsGrid.ItemsSource = Array.Empty<TextureImportCandidateDetailRow>();
        TextureReplacementDetailsSummaryText.Text =
            "Select a layout-confirmation or native-payload session.";
        TextureReplacementOpenLayoutButton.IsEnabled = false;
        TextureReplacementOpenPayloadButton.IsEnabled = false;
        TextureReplacementOpenAssemblyButton.IsEnabled = false;
        TextureReplacementOpenRewritePlanButton.IsEnabled = false;
        TextureReplacementOpenLogicalImageButton.IsEnabled = false;
        TextureReplacementOpenPhysicalPlanButton.IsEnabled = false;
        TextureReplacementOpenNoOpProofButton.IsEnabled = false;
        TextureReplacementOpenStagedUpkManifestButton.IsEnabled = false;
        TextureReplacementBrowseStagedUpkButton.IsEnabled = false;
        TextureReplacementInspectStagedAssetButton.IsEnabled = false;
        TextureReplacementOpenModBundleButton.IsEnabled = false;
        TextureReplacementOpenReadinessButton.IsEnabled = false;
        TextureReplacementOpenModFolderButton.IsEnabled = false;
        TextureReplacementInstallModButton.IsEnabled = false;
        TextureReplacementRestoreOriginalButton.IsEnabled = false;
        TextureReplacementOpenInstallReceiptButton.IsEnabled = false;
        TextureReplacementOpenBackupFolderButton.IsEnabled = false;
    }

    private static string FormatQualityNumber(double value) =>
        double.IsPositiveInfinity(value) ? "exact" : value.ToString("N2");

}
