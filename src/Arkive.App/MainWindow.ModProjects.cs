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
    private async void GuidedRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshGuidedTextureModsAsync("refresh button");
        await RefreshModConflictDependencyAsync("package-project refresh button");
        await RefreshModSetsAsync("package-project refresh button");
        await RefreshModifiedMeshUpksAsync("texture-tab refresh button");
    }

    private async Task<IReadOnlyList<ClientUpdatePackageInput>> CreateClientUpdatePackageInputsAsync()
    {
        var packages = await _guidedTexturePackageWorkspace.LoadAsync();
        var textures = await _guidedTextureModWorkspace.LoadAsync();
        var meshSnapshot = await _skeletalMeshEditWorkspaceStore.LoadAsync();
        var materials = await _materialEditWorkspaceStore.LoadAsync();
        var staticMeshes = await _staticMeshEditWorkspaceStore.LoadAsync();
        var staticMeshSourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in staticMeshes)
        {
            try
            {
                var manifest = await StaticMeshEditableExportService.LoadManifestAsync(
                    mesh.Manifest.SourceEditableManifestPath);
                staticMeshSourcePaths[mesh.Manifest.EditId] = Path.GetFullPath(manifest.SourcePackagePath);
            }
            catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
            {
                Log($"Client-update input ignored damaged StaticMesh project: edit={mesh.Manifest.EditId}, error={exception.Message}");
            }
        }
        var editableSnapshot = await _skeletalMeshEditableWorkspaceStore.LoadAsync();
        var sourcePaths = editableSnapshot.Entries.ToDictionary(
            entry => entry.Manifest.ExportId,
            entry => Path.GetFullPath(entry.Manifest.SourcePackagePath),
            StringComparer.OrdinalIgnoreCase);
        return packages.Select(package =>
        {
            var source = Path.GetFullPath(package.Manifest.SourcePackagePath);
            return new ClientUpdatePackageInput(
                package,
                textures.Where(texture => SamePath(
                    texture.Manifest.SourcePackagePath, source)).ToArray(),
                meshSnapshot.Entries.Where(mesh =>
                    sourcePaths.TryGetValue(mesh.Manifest.SourceExportId, out var meshSource) &&
                    SamePath(meshSource, source)).ToArray())
            {
                Materials = materials.Where(material => SamePath(
                    material.Document.SourcePackagePath, source)).ToArray(),
                StaticMeshes = staticMeshes.Where(mesh =>
                    staticMeshSourcePaths.TryGetValue(mesh.Manifest.EditId, out var meshSource) &&
                    SamePath(meshSource, source)).ToArray(),
            };
        }).ToArray();
    }

    private async Task RefreshClientUpdateAuditAsync(string trigger)
    {
        try
        {
            _latestClientUpdateAudit = await _clientUpdateAuditWorkspace.LoadLatestAsync();
            ApplyClientUpdateAudit(_latestClientUpdateAudit);
            Log($"Client-wide update audit workspace refreshed: trigger={trigger}, " +
                $"latest={_latestClientUpdateAudit?.ManifestPath ?? "none"}, " +
                $"state={_latestClientUpdateAudit?.Report.State.ToString() ?? "none"}, " +
                $"root={_clientUpdateAuditWorkspace.RootDirectory}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or NotSupportedException)
        {
            Log($"Client-wide update audit workspace refresh failed: trigger={trigger}, error={exception}");
            _latestClientUpdateAudit = null;
            ApplyClientUpdateAudit(null);
        }
    }

    private void ApplyClientUpdateAudit(ClientUpdateAuditEntry? entry)
    {
        if (ClientUpdatePackagesGrid is null) return;
        var priorId = (ClientUpdatePackagesGrid.SelectedItem as ClientUpdatePackageAuditRow)?
            .Report.PackageProjectId;
        var packageRows = entry?.Report.Packages.Select(report =>
                new ClientUpdatePackageAuditRow(report)).ToArray() ?? [];
        ClientUpdatePackagesGrid.ItemsSource = packageRows;
        var selected = packageRows.FirstOrDefault(row => row.Report.PackageProjectId.Equals(
                           priorId, StringComparison.OrdinalIgnoreCase)) ?? packageRows.FirstOrDefault();
        ClientUpdatePackagesGrid.SelectedItem = selected;
        if (selected is not null) ClientUpdatePackagesGrid.ScrollIntoView(selected);
        ApplySelectedClientUpdatePackage(selected);
        ClientUpdateInstallationsGrid.ItemsSource = entry?.Report.InstallationTargets
            .Select(status => new ClientUpdateInstallationRow(status)).ToArray() ?? [];
        var formatRows = new List<ClientUpdateFormatCheckRow>();
        if (entry is not null)
        {
            foreach (var report in entry.Report.Packages)
            {
                if (report.FormatProbe is { } probe)
                    formatRows.AddRange(ClientUpdateFormatCheckRow.Create(
                        report.PackageAlias, probe));
            }
            foreach (var probe in entry.Report.AdditionalPackageProbes)
                formatRows.AddRange(ClientUpdateFormatCheckRow.Create(
                    Path.GetFileNameWithoutExtension(probe.SourcePackagePath), probe));
        }
        ClientUpdateFormatChecksGrid.ItemsSource = formatRows;
        ClientUpdateSummaryText.Text = entry is null
            ? "No completed client-wide compatibility check is loaded. Restore installed mods before updating, then run Check all after game update."
            : $"{entry.Report.State} — {entry.Report.Summary} " +
              $"Checked {entry.Report.CheckedAt.ToLocalTime():g}; " +
              $"{entry.Report.ExactPackageCount:N0} exact, " +
              $"{entry.Report.SafeRebasePackageCount:N0} safely rebaseable, " +
              $"{entry.Report.ReviewPackageCount:N0} requiring review.";
        ClientUpdateProgressBar.IsIndeterminate = false;
        ClientUpdateProgressBar.Minimum = 0;
        ClientUpdateProgressBar.Maximum = Math.Max(1, entry?.Report.Packages.Count ?? 1);
        ClientUpdateProgressBar.Value = entry?.Report.Packages.Count ?? 0;
        ClientUpdateProgressText.Text = entry is null
            ? "Checks are read-only; bulk rebasing changes Arkive project manifests only."
            : $"Report: {entry.ManifestPath}";
        UpdateClientUpdateActionState();
    }

    private void ApplySelectedClientUpdatePackage(ClientUpdatePackageAuditRow? row)
    {
        if (ClientUpdateAssetsGrid is null) return;
        ClientUpdateAssetsGrid.ItemsSource = row?.Report.Assets
            .Select(asset => new ClientUpdateAssetAuditRow(asset)).ToArray() ?? [];
        UpdateClientUpdateActionState();
    }

    private void UpdateClientUpdateActionState()
    {
        if (ClientUpdateRunAllButton is null) return;
        ClientUpdateRunAllButton.IsEnabled = !_clientUpdateAuditBusy;
        ClientUpdateRefreshButton.IsEnabled = !_clientUpdateAuditBusy;
        ClientUpdateCancelButton.IsEnabled = _clientUpdateAuditBusy;
        ClientUpdateOpenReportButton.IsEnabled = !_clientUpdateAuditBusy &&
                                                 _latestClientUpdateAudit is not null;
        ClientUpdateGoToPackageButton.IsEnabled = !_clientUpdateAuditBusy &&
                                                  ClientUpdatePackagesGrid.SelectedItem is
                                                      ClientUpdatePackageAuditRow;
        var safeIds = _latestClientUpdateAudit?.Report.Packages
            .Where(report => report.State == ClientPackageCompatibilityState.SafeAutomaticRebase)
            .Select(report => report.PackageProjectId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var currentIds = _guidedPackageRows.Select(package => package.Entry.Manifest.PackageProjectId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ClientUpdateRebaseSafeButton.IsEnabled = !_clientUpdateAuditBusy &&
            _clientUpdateBulkRebaseService is not null &&
            _latestClientUpdateAudit?.Report.CanBulkRebase == true && safeIds.Count > 0 &&
            safeIds.IsSubsetOf(currentIds);
    }

    private async void ClientUpdateRunAllButton_Click(object sender, RoutedEventArgs e) =>
        await RunClientUpdateAuditAsync("user requested full check", showResult: true);

    private async Task RunClientUpdateAuditAsync(string trigger, bool showResult)
    {
        if (_clientUpdateAuditBusy) return;
        _clientUpdateAuditCancellation?.Dispose();
        _clientUpdateAuditCancellation = new CancellationTokenSource();
        var cancellationToken = _clientUpdateAuditCancellation.Token;
        _clientUpdateAuditBusy = true;
        UpdateClientUpdateActionState();
        try
        {
            var inputs = await CreateClientUpdatePackageInputsAsync();
            ClientUpdateProgressBar.Minimum = 0;
            ClientUpdateProgressBar.Maximum = Math.Max(1, inputs.Count);
            ClientUpdateProgressBar.Value = 0;
            ClientUpdateProgressBar.IsIndeterminate = inputs.Count == 0;
            ClientUpdateSummaryText.Text =
                "Running a read-only client-wide compatibility check. Game packages will not be changed...";
            var progress = new Progress<ClientUpdateAuditProgress>(update =>
            {
                ClientUpdateProgressBar.IsIndeterminate = false;
                ClientUpdateProgressBar.Maximum = Math.Max(1, update.TotalPackages);
                ClientUpdateProgressBar.Value = Math.Min(update.CompletedPackages,
                    ClientUpdateProgressBar.Maximum);
                ClientUpdateProgressText.Text = string.IsNullOrWhiteSpace(update.PackageName)
                    ? update.Message
                    : $"{update.PackageName}: {update.Message}";
            });
            Log($"Client-wide update audit started: trigger={trigger}, " +
                $"packageProjects={inputs.Count}, stateRoot={Path.Combine(_projectRoot, "backups", "installations")}, " +
                $"provider={_protectionProvider.Name}, deepChunkDecode=true.");
            var report = await _clientUpdateAuditService.InspectAllAsync(
                inputs, Path.Combine(_projectRoot, "backups", "installations"),
                progress, cancellationToken);
            var entry = await _clientUpdateAuditWorkspace.SaveAsync(report, cancellationToken);
            _latestClientUpdateAudit = entry;
            ApplyClientUpdateAudit(entry);
            Log($"Client-wide update audit completed: auditId={report.AuditId}, state={report.State}, " +
                $"packages={report.Packages.Count}, uniqueTargets={report.UniquePackageCount}, " +
                $"exact={report.ExactPackageCount}, safeRebase={report.SafeRebasePackageCount}, " +
                $"review={report.ReviewPackageCount}, restoreRequired={report.RequiresRestoreBeforeUpdateCheck}, " +
                $"formatCompatible={report.EveryAffectedPackageFormatCompatible}, " +
                $"duration={report.DurationSeconds:N3}s, report={entry.ManifestPath}, summary={report.Summary}");
            if (showResult)
            {
                MessageBox.Show(this,
                    $"State: {report.State}\n\n{report.Summary}\n\n" +
                    $"Exact packages: {report.ExactPackageCount:N0}\n" +
                    $"Safely rebaseable: {report.SafeRebasePackageCount:N0}\n" +
                    $"Requiring review: {report.ReviewPackageCount:N0}\n" +
                    $"Retained installation targets: {report.InstallationTargets.Count:N0}\n\n" +
                    $"Report: {entry.ManifestPath}",
                    "Arkive game-update compatibility",
                    MessageBoxButton.OK,
                    report.State is ClientUpdateAuditState.Compatible or
                        ClientUpdateAuditState.RebaseAvailable
                        ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            ClientUpdateProgressText.Text = "Client-wide compatibility check cancelled. No game files or projects were changed.";
            Log("Client-wide update audit cancelled by the user. GameFilesModified=false.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or NotSupportedException or ArgumentException)
        {
            ClientUpdateProgressText.Text = "The compatibility check failed safely. See the session log.";
            Log($"Client-wide update audit failed: trigger={trigger}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive game-update compatibility",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _clientUpdateAuditBusy = false;
            ClientUpdateProgressBar.IsIndeterminate = false;
            UpdateClientUpdateActionState();
        }
    }

    private async void ClientUpdateRebaseSafeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clientUpdateAuditBusy || _clientUpdateBulkRebaseService is null ||
            _latestClientUpdateAudit is not { } audit || !audit.Report.CanBulkRebase) return;
        var count = audit.Report.SafeRebasePackageCount;
        if (MessageBox.Show(this,
                $"Rebase {count:N0} package project(s) proven safe by the full audit?\n\n" +
                "Arkive will refresh native source evidence while preserving editable PNG and GLB files. " +
                "Existing project manifests are restored if any package fails. Game UPKs are never changed.",
                "Rebase all safe projects", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _clientUpdateAuditCancellation?.Dispose();
        _clientUpdateAuditCancellation = new CancellationTokenSource();
        var cancellationToken = _clientUpdateAuditCancellation.Token;
        _clientUpdateAuditBusy = true;
        UpdateClientUpdateActionState();
        var rerun = false;
        try
        {
            ClientUpdateProgressBar.Minimum = 0;
            ClientUpdateProgressBar.Maximum = Math.Max(1, count);
            ClientUpdateProgressBar.Value = 0;
            var progress = new Progress<ClientUpdateBulkRebaseProgress>(update =>
            {
                ClientUpdateProgressBar.Value = Math.Min(update.CompletedPackages,
                    ClientUpdateProgressBar.Maximum);
                ClientUpdateProgressText.Text = $"{update.PackageName}: {update.Message}";
            });
            var inputs = await CreateClientUpdatePackageInputsAsync();
            var result = await _clientUpdateBulkRebaseService.RebaseSafePackagesAsync(
                audit.Report, inputs,
                Path.Combine(_projectRoot, "staging", "client-update-rebase"),
                progress, cancellationToken);
            Log($"Client-wide safe rebase completed: auditId={result.AuditId}, " +
                $"packages={result.Packages.Count}, textures={result.TextureProjectCount}, " +
                $"materials={result.MaterialProjectCount}, meshes={result.MeshProjectCount}, complete={result.EveryPackageRebased}, " +
                $"rolledBack={result.RolledBack}, gameFilesModified={result.GameFilesModified}, " +
                $"duration={result.Duration.TotalSeconds:N3}s.");
            await RebindRebasedModMeshesAsync(result.Packages);
            await RefreshGuidedTextureModsAsync("client-wide safe rebase");
            await RefreshMaterialProjectsAsync("client-wide safe rebase");
            await RefreshModSetsAsync("client-wide safe rebase");
            rerun = true;
            MessageBox.Show(this,
                $"Rebased {result.Packages.Count:N0} package project(s), " +
                $"{result.TextureProjectCount:N0} texture project(s), and " +
                $"{result.MaterialProjectCount:N0} material project(s), and {result.MeshProjectCount:N0} mesh project(s).\n\n" +
                "Editable files were preserved. Rebuild the affected modified UPKs before installing them.",
                "Arkive game-update rebase", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Log("Client-wide safe rebase cancelled. Existing project manifests were restored by the transaction guard.");
            MessageBox.Show(this,
                "The bulk rebase was cancelled. Arkive restored the pre-operation project manifests and did not change game files.",
                "Arkive game-update rebase", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or NotSupportedException or ArgumentException)
        {
            Log($"Client-wide safe rebase failed and rolled back: auditId={audit.Report.AuditId}, error={exception}");
            MessageBox.Show(this,
                exception.Message + "\n\nArkive restored the snapshotted project manifests. Game files were not changed.",
                "Arkive game-update rebase", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _clientUpdateAuditBusy = false;
            UpdateClientUpdateActionState();
        }
        if (rerun) await RunClientUpdateAuditAsync("automatic post-rebase verification", showResult: false);
    }

    private async Task RebindRebasedModMeshesAsync(IReadOnlyList<ClientUpdateRebaseResult> results)
    {
        var meshes = results.SelectMany(result => result.Meshes).ToArray();
        foreach (var mesh in meshes)
        {
            try
            {
                await _arkiveModLibrary.RebindEditAsync(mesh.PreviousEditId, mesh.CurrentEditId);
            }
            catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
            {
                Log($"Mod binding could not follow a rebased mesh: previous={mesh.PreviousEditId}, " +
                    $"current={mesh.CurrentEditId}, error={exception.Message}");
            }
        }
        if (meshes.Length > 0)
            await ArkiveModSelectionService.SynchronizeAsync(_arkiveModLibrary, _arkiveModEdits);
    }

    private void ClientUpdateCancelButton_Click(object sender, RoutedEventArgs e) =>
        _clientUpdateAuditCancellation?.Cancel();

    private async void ClientUpdateRefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshClientUpdateAuditAsync("reload latest report");

    private void ClientUpdateOpenReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_latestClientUpdateAudit is not null)
            OpenFolder(_latestClientUpdateAudit.DirectoryPath, "client-update compatibility report");
    }

    private void ClientUpdatePackagesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplySelectedClientUpdatePackage(ClientUpdatePackagesGrid.SelectedItem as ClientUpdatePackageAuditRow);

    private void ClientUpdateGoToPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (ClientUpdatePackagesGrid.SelectedItem is not ClientUpdatePackageAuditRow selected) return;
        var package = _guidedPackageRows.FirstOrDefault(row =>
            row.Entry.Manifest.PackageProjectId.Equals(
                selected.Report.PackageProjectId, StringComparison.OrdinalIgnoreCase));
        if (package is null) return;
        ModifiedUpksInnerTabControl.SelectedItem = GuidedPackageProjectsTab;
        GuidedPackagesGrid.SelectedItem = package;
        GuidedPackagesGrid.ScrollIntoView(package);
    }

    private async void GuidedCheckClientUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress ||
            GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow package) return;
        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        try
        {
            GuidedBuildStatusText.Text = "Hashing the current client UPK and checking selected native assets...";
            var report = await _clientUpdateCompatibilityService.InspectAsync(
                package.Entry, package.Assets.Select(row => row.Entry).ToArray(),
                package.MeshAssets.Select(row => row.Entry).ToArray(),
                package.MaterialAssets.Select(row => row.Entry).ToArray(),
                package.StaticMeshAssets.Select(row => row.Entry).ToArray());
            _selectedClientCompatibility = report;
            var directory = Path.Combine(package.Entry.DirectoryPath, "client-update-checks");
            Directory.CreateDirectory(directory);
            var reportPath = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-client-update.json");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }));
            GuidedBuildStatusText.Text = report.Summary;
            Log($"Client-update compatibility completed: packageId={report.PackageProjectId}, " +
                $"state={report.State}, autoRebase={report.CanAutomaticallyRebase}, " +
                $"assets={report.Assets.Count}, report={reportPath}, summary={report.Summary}");
            MessageBox.Show(this,
                $"State: {report.State}\n\n{report.Summary}\n\n" +
                $"Unchanged assets: {report.Assets.Count(asset => asset.State == ClientAssetCompatibilityState.Unchanged):N0}\n" +
                $"Assets needing review: {report.Assets.Count(asset => asset.State != ClientAssetCompatibilityState.Unchanged):N0}\n\n" +
                $"Report: {reportPath}",
                "Arkive client-update check", MessageBoxButton.OK,
                report.CanAutomaticallyRebase || report.CanRebuildWithoutRebase
                    ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           NotSupportedException or ArgumentException)
        {
            _selectedClientCompatibility = null;
            Log($"Client-update compatibility failed: package={package.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive client-update check",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
        }
    }

    private async void GuidedRebaseClientUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress || _clientUpdateRebaseService is null ||
            GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow package ||
            _selectedClientCompatibility?.PackageProjectId != package.Entry.Manifest.PackageProjectId ||
            !_selectedClientCompatibility.CanAutomaticallyRebase) return;
        if (MessageBox.Show(this,
                "Arkive will refresh native source evidence from the updated client UPK. Editable PNG and GLB files are retained unchanged. Continue?",
                "Rebase unchanged assets", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        try
        {
            var output = Path.Combine(_projectRoot, "staging", "client-update-rebase",
                package.Entry.Manifest.PackageProjectId[..8], DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            GuidedBuildStatusText.Text = "Re-exporting unchanged native assets against the updated client package...";
            var result = await _clientUpdateRebaseService.RebaseAsync(
                package.Entry, package.Assets.Select(row => row.Entry).ToArray(),
                package.MeshAssets.Select(row => row.Entry).ToArray(),
                package.MaterialAssets.Select(row => row.Entry).ToArray(), output);
            await _guidedTexturePackageWorkspace.RecordClientBaselineAsync(
                package.Entry.ManifestPath, result.CurrentPackageSha256);
            await RebindRebasedModMeshesAsync([result]);
            _selectedClientCompatibility = null;
            Log($"Client-update rebase completed: packageId={result.PackageProjectId}, " +
                $"previous={result.PreviousPackageSha256}, current={result.CurrentPackageSha256}, " +
                $"textures={result.Textures.Count}, materials={result.Materials.Count}, meshes={result.Meshes.Count}, " +
                $"gameFilesModified={result.GameFilesModified}, output={output}.");
            await RefreshGuidedTextureModsAsync("client update rebased");
            await RefreshMaterialProjectsAsync("client update rebased");
            await RefreshModSetsAsync("client update rebased");
            MessageBox.Show(this,
                $"Rebased {result.Textures.Count:N0} texture, {result.Materials.Count:N0} material, and {result.Meshes.Count:N0} mesh project(s).\n\n" +
                "Your editable files were preserved. Rebuild the affected package project before installing it.",
                "Arkive client update", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           NotSupportedException or ArgumentException)
        {
            Log($"Client-update rebase failed: package={package.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive client-update rebase",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
        }
    }

}
