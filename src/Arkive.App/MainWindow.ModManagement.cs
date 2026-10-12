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
    private async Task RefreshModConflictDependencyAsync(string trigger)
    {
        if (ModGraphConflictsGrid is null || _modGraphBusy) return;
        _modGraphBusy = true;
        ModGraphRefreshButton.IsEnabled = false;
        ModGraphSummaryText.Text = "Reading committed Package Projects and dependency evidence...";
        try
        {
            var packages = await _guidedTexturePackageWorkspace.LoadAsync();
            _modConflictDependencyReport = await _modConflictDependencyService.AnalyzeAsync(packages);
            var priorId = (ModGraphPackageFilterComboBox.SelectedItem as ModGraphPackageFilterRow)
                ?.PackageProjectId ?? string.Empty;
            var filters = new[] { new ModGraphPackageFilterRow(string.Empty, "All package projects") }
                .Concat(packages.OrderBy(item => item.Manifest.PackageAlias, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new ModGraphPackageFilterRow(
                        item.Manifest.PackageProjectId,
                        string.IsNullOrWhiteSpace(item.Manifest.DisplayName)
                            ? item.Manifest.PackageAlias
                            : $"{item.Manifest.DisplayName} — {item.Manifest.PackageAlias}")))
                .ToArray();
            ModGraphPackageFilterComboBox.ItemsSource = filters;
            ModGraphPackageFilterComboBox.SelectedItem = filters.FirstOrDefault(item =>
                item.PackageProjectId.Equals(priorId, StringComparison.OrdinalIgnoreCase)) ?? filters[0];
            ApplyModGraphFilter();
            Log($"Mod conflict/dependency audit completed: trigger={trigger}, " +
                $"packages={_modConflictDependencyReport.PackageProjectCount}, " +
                $"assets={_modConflictDependencyReport.AssetCount}, " +
                $"blocking={_modConflictDependencyReport.BlockingConflictCount}, " +
                $"warnings={_modConflictDependencyReport.WarningCount}, " +
                $"dependencies={_modConflictDependencyReport.Dependencies.Count}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               JsonException or InvalidDataException or ArgumentException)
        {
            ModGraphSummaryText.Text = "The package graph could not be loaded. See the session log.";
            Log($"Mod conflict/dependency audit failed: trigger={trigger}, error={exception}");
        }
        finally
        {
            _modGraphBusy = false;
            ModGraphRefreshButton.IsEnabled = true;
            UpdateModGraphActionState();
        }
    }

    private void ApplyModGraphFilter()
    {
        if (_modConflictDependencyReport is null) return;
        var projectId = (ModGraphPackageFilterComboBox.SelectedItem as ModGraphPackageFilterRow)
            ?.PackageProjectId ?? string.Empty;
        var conflicts = _modConflictDependencyReport.Conflicts
            .Where(item => string.IsNullOrEmpty(projectId) || item.PackageProjectIds.Contains(
                projectId, StringComparer.OrdinalIgnoreCase))
            .Select(item => new ModGraphConflictRow(item)).ToArray();
        var dependencies = _modConflictDependencyReport.Dependencies
            .Where(item => string.IsNullOrEmpty(projectId) || item.PackageProjectId.Equals(
                projectId, StringComparison.OrdinalIgnoreCase))
            .Select(item => new ModGraphDependencyRow(item)).ToArray();
        ModGraphConflictsGrid.ItemsSource = conflicts;
        ModGraphDependenciesGrid.ItemsSource = dependencies;
        ModGraphSummaryText.Text = _modConflictDependencyReport.Summary +
                                   (string.IsNullOrEmpty(projectId)
                                       ? string.Empty
                                       : $" Showing {conflicts.Length:N0} finding(s) and {dependencies.Length:N0} dependency edge(s) for the selected project.");
        UpdateModGraphActionState();
    }

    private async void ModGraphRefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshModConflictDependencyAsync("refresh button");

    private void ModGraphPackageFilterComboBox_SelectionChanged(
        object sender, SelectionChangedEventArgs e) => ApplyModGraphFilter();

    private void ModGraphGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ModGraphConflictsGrid) && ModGraphConflictsGrid.SelectedItem is not null)
            ModGraphDependenciesGrid.SelectedItem = null;
        else if (ReferenceEquals(sender, ModGraphDependenciesGrid) &&
                 ModGraphDependenciesGrid.SelectedItem is not null)
            ModGraphConflictsGrid.SelectedItem = null;
        UpdateModGraphActionState();
    }

    private void UpdateModGraphActionState()
    {
        if (ModGraphGoToPackageButton is null) return;
        ModGraphGoToPackageButton.IsEnabled = !_modGraphBusy && SelectedModGraphPackageId() is not null;
        ModGraphCopyButton.IsEnabled = !_modGraphBusy && _modConflictDependencyReport is not null;
    }

    private string? SelectedModGraphPackageId()
    {
        var selected = (ModGraphConflictsGrid.SelectedItem as ModGraphConflictRow)?.PackageProjectId ??
                       (ModGraphDependenciesGrid.SelectedItem as ModGraphDependencyRow)?.PackageProjectId;
        if (!string.IsNullOrWhiteSpace(selected)) return selected;
        return ModGraphPackageFilterComboBox.SelectedItem is ModGraphPackageFilterRow filter &&
               !string.IsNullOrWhiteSpace(filter.PackageProjectId)
            ? filter.PackageProjectId
            : null;
    }

    private void ModGraphGoToPackageButton_Click(object sender, RoutedEventArgs e)
    {
        var packageId = SelectedModGraphPackageId();
        if (packageId is null) return;
        var package = _guidedPackageRows.FirstOrDefault(item =>
            item.Entry.Manifest.PackageProjectId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
        if (package is null)
        {
            StatusText.Text = "The selected Package Project is no longer loaded; refresh Modified UPKs.";
            return;
        }
        ModifiedUpksInnerTabControl.SelectedItem = GuidedPackageProjectsTab;
        GuidedPackagesGrid.SelectedItem = package;
        GuidedPackagesGrid.ScrollIntoView(package);
        Log($"Mod graph navigation opened Package Project: packageId={packageId}, alias={package.PackageAlias}.");
    }

    private void ModGraphCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_modConflictDependencyReport is null) return;
        var conflictText = string.Join(Environment.NewLine,
            _modConflictDependencyReport.Conflicts.Select(item =>
                $"[{item.Severity}] {item.Code} | {item.PackageAlias} | {item.Message} | {item.Target}"));
        var dependencyText = string.Join(Environment.NewLine,
            _modConflictDependencyReport.Dependencies.Select(item =>
                $"{item.PackageAlias} | {item.SourceKind}:{item.SourceObject} -> {item.Relation} -> " +
                $"{item.TargetKind}:{item.TargetObject} | {item.Resolution} | {item.Evidence}"));
        CopyText(
            $"Arkive mod conflict and dependency report{Environment.NewLine}" +
            $"Created: {_modConflictDependencyReport.CreatedAt:O}{Environment.NewLine}" +
            $"{_modConflictDependencyReport.Summary}{Environment.NewLine}{Environment.NewLine}" +
            $"Findings{Environment.NewLine}{conflictText}{Environment.NewLine}{Environment.NewLine}" +
            $"Dependencies{Environment.NewLine}{dependencyText}",
            "mod conflict and dependency report");
    }

    private async Task RefreshModSetsAsync(string trigger, string? selectSetId = null)
    {
        if (ModSetsGrid is null) return;
        var priorId = selectSetId ?? (ModSetsGrid.SelectedItem as ModSetRow)?.Entry.Manifest.ModSetId;
        var rows = new List<ModSetRow>();
        foreach (var entry in await _modSetWorkspace.LoadAsync())
        {
            ModSetInspection? inspection = null;
            string? error = null;
            try { inspection = await _modSetService.InspectAsync(CreateModSetDeploymentRequest(entry.ManifestPath)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               NotSupportedException or ArgumentException)
            { error = exception.Message; }
            rows.Add(new ModSetRow(entry, inspection, error));
        }
        _modSetRows = rows.OrderByDescending(row => row.Entry.Manifest.IsFavorite)
            .ThenByDescending(row => row.Entry.Manifest.UpdatedAt).ToArray();
        ModSetsGrid.ItemsSource = _modSetRows;
        var selected = _modSetRows.FirstOrDefault(row => row.Entry.Manifest.ModSetId.Equals(
                           priorId, StringComparison.OrdinalIgnoreCase)) ?? _modSetRows.FirstOrDefault();
        ModSetsGrid.SelectedItem = selected;
        if (selected is not null) ModSetsGrid.ScrollIntoView(selected);
        ApplySelectedModSet(selected);
        Log($"Mod Set workspace refreshed: trigger={trigger}, sets={rows.Count}, " +
            $"packages={rows.Sum(row => row.Entry.Manifest.Members.Count)}, root={_modSetWorkspace.RootDirectory}.");
    }

    private ModSetDeploymentRequest CreateModSetDeploymentRequest(string manifestPath) => new(
        manifestPath, Path.Combine(_projectRoot, "backups", "packages"),
        Path.Combine(_projectRoot, "backups", "installations"));

    private void ApplySelectedModSet(ModSetRow? row)
    {
        ModSetNameTextBox.Text = row?.Entry.Manifest.DisplayName ?? string.Empty;
        ModSetNotesTextBox.Text = row?.Entry.Manifest.Notes ?? string.Empty;
        var memberIds = row?.Entry.Manifest.Members.Where(member => member.Included)
            .Select(member => member.PackageProjectId).ToHashSet(StringComparer.OrdinalIgnoreCase) ??
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ModSetPackagesGrid.ItemsSource = _guidedPackageRows.Select(package =>
            new ModSetPackageRow(package, memberIds.Contains(package.Entry.Manifest.PackageProjectId))).ToArray();
        ModSetConflictsGrid.ItemsSource = row?.Inspection?.Conflicts.Select(conflict =>
            new ModSetConflictRow(conflict)).ToArray();
        ModSetStatusText.Text = row?.Summary ??
                                "Create a set, then check the built package projects it should manage.";
        UpdateModSetActionState();
    }

    private void UpdateModSetActionState()
    {
        if (ModSetSaveButton is null) return;
        var row = ModSetsGrid.SelectedItem as ModSetRow;
        ModSetSaveButton.IsEnabled = !_modSetBusy && row is not null;
        ModSetDeleteButton.IsEnabled = !_modSetBusy && row is not null && row.Inspection?.CanRestore != true;
        ModSetInstallButton.IsEnabled = !_modSetBusy && row?.Inspection?.CanInstall == true;
        ModSetRestoreButton.IsEnabled = !_modSetBusy && row?.Inspection?.CanRestore == true;
    }

    private void ModSetsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplySelectedModSet(ModSetsGrid.SelectedItem as ModSetRow);

    private async void ModSetRefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshModSetsAsync("refresh button");

    private async void ModSetCreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_modSetBusy) return;
        var name = string.IsNullOrWhiteSpace(ModSetNameTextBox.Text)
            ? $"Mod Set {_modSetRows.Count + 1:N0}" : ModSetNameTextBox.Text.Trim();
        try
        {
            var entry = await _modSetWorkspace.CreateAsync(name, ModSetNotesTextBox.Text);
            Log($"Mod Set created: id={entry.Manifest.ModSetId}, name={entry.Manifest.DisplayName}, manifest={entry.ManifestPath}.");
            await RefreshModSetsAsync("set created", entry.Manifest.ModSetId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, exception.Message, "Create Mod Set", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void ModSetSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_modSetBusy || ModSetsGrid.SelectedItem is not ModSetRow row) return;
        try
        {
            var updated = await _modSetWorkspace.UpdateMetadataAsync(row.Entry.ManifestPath,
                ModSetNameTextBox.Text, ModSetNotesTextBox.Text);
            Log($"Mod Set metadata saved: id={updated.Manifest.ModSetId}, name={updated.Manifest.DisplayName}.");
            await RefreshModSetsAsync("metadata saved", updated.Manifest.ModSetId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, exception.Message, "Save Mod Set", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void ModSetPackageIncludeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_modSetBusy || ModSetsGrid.SelectedItem is not ModSetRow set ||
            sender is not CheckBox { DataContext: ModSetPackageRow package }) return;
        try
        {
            var updated = await _modSetWorkspace.SetPackageAsync(set.Entry.ManifestPath,
                package.Package.Entry, package.Included);
            Log($"Mod Set membership changed: set={updated.Manifest.ModSetId}, " +
                $"package={package.Package.Entry.Manifest.PackageProjectId}, included={package.Included}.");
            await RefreshModSetsAsync("package membership changed", updated.Manifest.ModSetId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, exception.Message, "Update Mod Set", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void ModSetDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_modSetBusy || ModSetsGrid.SelectedItem is not ModSetRow row || row.Inspection?.CanRestore == true) return;
        if (MessageBox.Show(this, $"Delete Mod Set '{row.DisplayName}'? Package projects and backups are retained.",
                "Delete Mod Set", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _modSetWorkspace.DeleteAsync(row.Entry);
        Log($"Mod Set deleted: id={row.Entry.Manifest.ModSetId}, name={row.DisplayName}.");
        await RefreshModSetsAsync("set deleted");
    }

    private async void ModSetInstallButton_Click(object sender, RoutedEventArgs e) =>
        await DeploySelectedModSetAsync(restore: false);

    private async void ModSetRestoreButton_Click(object sender, RoutedEventArgs e) =>
        await DeploySelectedModSetAsync(restore: true);

    private async Task DeploySelectedModSetAsync(bool restore)
    {
        if (_modSetBusy || ModSetsGrid.SelectedItem is not ModSetRow row) return;
        var inspection = await _modSetService.InspectAsync(CreateModSetDeploymentRequest(row.Entry.ManifestPath));
        if (restore ? !inspection.CanRestore : !inspection.CanInstall)
        {
            MessageBox.Show(this, inspection.Summary, "Arkive Mod Set", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var verb = restore ? "restore" : "install";
        if (MessageBox.Show(this,
                $"{(restore ? "Restore" : "Install")} '{row.DisplayName}' across {inspection.Members.Count:N0} physical UPK(s)?\n\n{inspection.Summary}",
                $"Arkive Mod Set {verb}", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _modSetBusy = true;
        UpdateModSetActionState();
        try
        {
            ModSetStatusText.Text = $"Running transactional Mod Set {verb}...";
            var request = CreateModSetDeploymentRequest(row.Entry.ManifestPath);
            var result = restore
                ? await _modSetService.RestoreAsync(request)
                : await _modSetService.InstallAsync(request);
            Log($"Mod Set deployment completed: action={verb}, set={row.Entry.Manifest.ModSetId}, " +
                $"state={result.Inspection.State}, changedPackages={result.ChangedPackageCount}, " +
                $"rolledBack={result.RolledBack}, duration={result.Duration.TotalSeconds:N3}s.");
            await RefreshGuidedTextureModsAsync($"Mod Set {verb} changed targets");
            await RefreshModSetsAsync($"{verb} completed", row.Entry.Manifest.ModSetId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           NotSupportedException or ArgumentException)
        {
            Log($"Mod Set deployment failed: action={verb}, set={row.Entry.Manifest.ModSetId}, error={exception}");
            MessageBox.Show(this, exception.Message, $"Arkive Mod Set {verb}",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await RefreshModSetsAsync($"{verb} failed", row.Entry.Manifest.ModSetId);
        }
        finally { _modSetBusy = false; UpdateModSetActionState(); }
    }

    private async Task RefreshModifiedMeshUpksAsync(string trigger, string? selectEditId = null)
    {
        if (ModifiedMeshUpksGrid is null) return;
        var priorId = selectEditId ??
                      (ModifiedMeshUpksGrid.SelectedItem as ModifiedMeshUpkRow)
                      ?.Entry.Manifest.EditId;
        try
        {
            var snapshot = await _skeletalMeshEditWorkspaceStore.LoadAsync();
            var rows = new List<ModifiedMeshUpkRow>();
            foreach (var entry in snapshot.Entries)
            {
                var bundlePath = Path.Combine(
                    entry.DirectoryPath, "deployment-bundle", ArkiveModBundleService.BundleManifestFileName);
                if (!File.Exists(bundlePath)) continue;
                ArkiveModBundleManifest? bundle = null;
                ModDeploymentInspection? inspection = null;
                string? error = null;
                try
                {
                    bundle = await ArkiveModBundleService.LoadBundleAsync(bundlePath);
                    inspection = await _modDeploymentService.InspectAsync(
                        CreateModDeploymentRequest(bundlePath));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or NotSupportedException or
                        ArgumentException or JsonException)
                {
                    error = exception.Message;
                    Log($"Modified mesh-package inspection failed: editId={entry.Manifest.EditId}, " +
                        $"bundle={bundlePath}, error={exception}");
                }
                rows.Add(new ModifiedMeshUpkRow(entry, bundlePath, bundle, inspection, error));
            }

            _modifiedMeshUpkRows = rows
                .OrderByDescending(row => row.Inspection?.CanRestore == true)
                .ThenByDescending(row => row.Entry.Manifest.CreatedAt)
                .ToArray();
            ModifiedMeshUpksGrid.ItemsSource = _modifiedMeshUpkRows;
            var selected = priorId is null
                ? _modifiedMeshUpkRows.FirstOrDefault()
                : _modifiedMeshUpkRows.FirstOrDefault(row => row.Entry.Manifest.EditId.Equals(
                    priorId, StringComparison.OrdinalIgnoreCase)) ?? _modifiedMeshUpkRows.FirstOrDefault();
            ModifiedMeshUpksGrid.SelectedItem = selected;
            if (selected is not null) ModifiedMeshUpksGrid.ScrollIntoView(selected);
            ModifiedMeshStatusText.Text = selected is null
                ? "No prepared mesh packages were found. Prepare one from Mesh recon > Editable exports."
                : selected.Status;
            UpdateModifiedMeshUpkActionState();
            Log($"Modified mesh-package workspace refreshed: trigger={trigger}, " +
                $"bundles={_modifiedMeshUpkRows.Count}, installed={_modifiedMeshUpkRows.Count(row => row.Inspection?.CanRestore == true)}, " +
                $"errors={_modifiedMeshUpkRows.Count(row => row.Error is not null)}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _modifiedMeshUpkRows = [];
            ModifiedMeshUpksGrid.ItemsSource = null;
            ModifiedMeshStatusText.Text = $"Mesh packages could not be loaded: {exception.Message}";
            UpdateModifiedMeshUpkActionState();
            Log($"Modified mesh-package workspace refresh failed: trigger={trigger}, error={exception}");
        }
    }

    private void ModifiedMeshUpksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModifiedMeshStatusText is not null)
            ModifiedMeshStatusText.Text = ModifiedMeshUpksGrid.SelectedItem is ModifiedMeshUpkRow row
                ? row.Status
                : "Select a prepared mesh package.";
        UpdateModifiedMeshUpkActionState();
    }

    private void UpdateModifiedMeshUpkActionState()
    {
        var row = ModifiedMeshUpksGrid?.SelectedItem as ModifiedMeshUpkRow;
        var enabled = row is not null && !_skeletalMeshDeploymentBusy;
        if (ModifiedMeshGoToEditButton is not null)
            ModifiedMeshGoToEditButton.IsEnabled = enabled;
        if (ModifiedMeshInspectButton is not null)
            ModifiedMeshInspectButton.IsEnabled = enabled && _umodelBridge is not null &&
                                                   row!.Inspection is not null &&
                                                   File.Exists(row.Inspection.PayloadPath);
        if (ModifiedMeshInstallButton is not null)
            ModifiedMeshInstallButton.IsEnabled = enabled && row!.Inspection?.CanInstall == true;
        if (ModifiedMeshRestoreButton is not null)
            ModifiedMeshRestoreButton.IsEnabled = enabled && row!.Inspection?.CanRestore == true;
        if (ModifiedMeshOpenBundleButton is not null)
            ModifiedMeshOpenBundleButton.IsEnabled = enabled && Directory.Exists(
                Path.GetDirectoryName(row!.BundleManifestPath));
    }

    private async void ModifiedMeshRefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshModifiedMeshUpksAsync("mesh-tab refresh button");

    private void ModifiedMeshOpenBundleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModifiedMeshUpksGrid.SelectedItem is ModifiedMeshUpkRow row)
            OpenFolder(Path.GetDirectoryName(row.BundleManifestPath)!, "SkeletalMesh mod bundle");
    }

    private void ModifiedMeshGoToEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModifiedMeshUpksGrid.SelectedItem is not ModifiedMeshUpkRow row) return;
        var source = _skeletalMeshEditableExports.FirstOrDefault(item =>
            item.Entry.Manifest.ExportId.Equals(
                row.Entry.Manifest.SourceExportId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            MessageBox.Show(this,
                "The clean editable export for this mesh project is no longer available.",
                "Go to mesh edit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SkeletalMeshReconTab.IsSelected = true;
        SkeletalMeshEditableExportsTab.IsSelected = true;
        SkeletalMeshEditableExportsGrid.SelectedItem = source;
        SkeletalMeshEditableExportsGrid.ScrollIntoView(source);
        DisplayEditedSkeletalMeshesForSelectedExport(row.Entry.Manifest.EditId);
        StatusText.Text = $"Selected the mesh edit for {row.ObjectName}.";
    }

    private async void ModifiedMeshInspectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_umodelBridge is null || _skeletalMeshDeploymentBusy ||
            ModifiedMeshUpksGrid.SelectedItem is not ModifiedMeshUpkRow row) return;
        _skeletalMeshDeploymentBusy = true;
        UpdateModifiedMeshUpkActionState();
        StatusText.Text = $"Revalidating and opening {row.ObjectName} from the modified UPK...";
        try
        {
            var inspection = await _modDeploymentService.InspectAsync(
                CreateModDeploymentRequest(row.BundleManifestPath));
            var list = await _umodelBridge.ListAsync(inspection.PayloadPath);
            var asset = list.Assets.FirstOrDefault(item =>
                item.ObjectName.Equals(row.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                item.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
            if (asset is null)
                throw new UmodelBridgeException(
                    $"UModel listed the modified UPK but did not find SkeletalMesh {row.ObjectName}.");
            var process = _umodelBridge.StartAssetPreviewInPackageRoot(
                inspection.PayloadPath, asset, Path.GetDirectoryName(inspection.TargetPath)!);
            StatusText.Text = $"Opened modified mesh {row.ObjectName} in UModel.";
            Log($"Modified mesh-package preview launched: editId={row.Entry.Manifest.EditId}, " +
                $"modId={inspection.Bundle.ModId}, process={process.Id}, payload={inspection.PayloadPath}, " +
                $"object={asset.ObjectName}, state={inspection.State}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or JsonException or UmodelBridgeException or
                System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "The modified mesh UPK could not be opened in UModel.";
            Log($"Modified mesh-package preview failed: editId={row.Entry.Manifest.EditId}, " +
                $"bundle={row.BundleManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Inspect modified mesh UPK",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshDeploymentBusy = false;
            UpdateModifiedMeshUpkActionState();
        }
    }

    private async void ModifiedMeshInstallButton_Click(object sender, RoutedEventArgs e) =>
        await RunModifiedMeshDeploymentAsync(restore: false);

    private async void ModifiedMeshRestoreButton_Click(object sender, RoutedEventArgs e) =>
        await RunModifiedMeshDeploymentAsync(restore: true);

    private async Task RunModifiedMeshDeploymentAsync(bool restore)
    {
        if (_skeletalMeshDeploymentBusy ||
            ModifiedMeshUpksGrid.SelectedItem is not ModifiedMeshUpkRow row) return;
        ArkiveModDeploymentRequest request;
        ModDeploymentInspection preflight;
        try
        {
            request = CreateModDeploymentRequest(row.BundleManifestPath);
            preflight = await _modDeploymentService.InspectAsync(request);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or JsonException)
        {
            Log($"Modified mesh deployment preflight failed: restore={restore}, " +
                $"bundle={row.BundleManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message,
                restore ? "Restore original mesh package" : "Install mesh mod",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (restore ? !preflight.CanRestore : !preflight.CanInstall)
        {
            MessageBox.Show(this, preflight.Summary,
                restore ? "Restore original mesh package" : "Install mesh mod",
                MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshModifiedMeshUpksAsync("deployment preflight refused", row.Entry.Manifest.EditId);
            return;
        }

        var question = restore
            ? $"Restore the original package for '{preflight.Bundle.DisplayName}'?\n\n" +
              $"Target:\n{preflight.TargetPath}\n\nVerified backup:\n{preflight.BackupPath}\n\n" +
              "Arkive will refuse if the installed package no longer matches this mesh mod."
            : $"Install '{preflight.Bundle.DisplayName}'?\n\n" +
              $"Arkive will modify:\n{preflight.TargetPath}\n\n" +
              $"The byte-exact original will be retained at:\n{preflight.BackupPath}\n\n" +
              "Close Lost Ark, its launcher, and Easy Anti-Cheat before continuing.";
        if (MessageBox.Show(this, question,
                restore ? "Restore original mesh package" : "Install Arkive mesh mod",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) !=
            MessageBoxResult.Yes) return;

        _skeletalMeshDeploymentBusy = true;
        UpdateModifiedMeshUpkActionState();
        StatusText.Text = restore
            ? $"Restoring the original package for {preflight.Bundle.DisplayName}..."
            : $"Backing up and installing {preflight.Bundle.DisplayName}...";
        Log($"Modified-UPKs mesh deployment started: restore={restore}, modId={preflight.Bundle.ModId}, " +
            $"target={preflight.TargetPath}, targetSha={preflight.CurrentTargetSha256}, " +
            $"payload={preflight.PayloadPath}, backup={preflight.BackupPath}. User confirmed operation.");
        try
        {
            var result = restore
                ? await _modDeploymentService.RestoreAsync(request)
                : await _modDeploymentService.InstallAsync(request);
            Log($"Modified-UPKs mesh deployment completed in {result.Duration.TotalSeconds:N3}s: " +
                $"restore={restore}, installationId={result.Receipt.InstallationId}, " +
                $"state={result.Inspection.State}, changedTarget={result.ChangedTarget}, " +
                $"targetSha={result.Inspection.CurrentTargetSha256}, backupVerified={result.Receipt.BackupVerified}, " +
                $"receipt={result.Inspection.ReceiptPath}, gameFilesModified={result.Receipt.GameFilesModified}.");
            foreach (var check in result.Receipt.Checks)
                Log($"Modified-UPKs mesh deployment check: status={check.Status}, " +
                    $"code={check.Code}, message={check.Message}");
            StatusText.Text = restore
                ? "The original mesh package was restored and verified."
                : "The mesh mod was installed and verified; restoration is ready.";
            MessageBox.Show(this,
                restore
                    ? $"The original package was restored byte-for-byte.\n\n{result.Receipt.TargetPath}"
                    : $"{result.Receipt.DisplayName} is installed.\n\nOriginal backup:\n{result.Receipt.BackupPath}",
                restore ? "Arkive mesh original restored" : "Arkive mesh mod installed",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or JsonException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = restore
                ? "Mesh original restore was refused or failed."
                : "Mesh mod installation was refused or failed.";
            Log($"Modified-UPKs mesh deployment failed: restore={restore}, " +
                $"bundle={request.BundleManifestPath}, target={preflight.TargetPath}, error={exception}");
            MessageBox.Show(this, exception.Message,
                restore ? "Arkive mesh original restore" : "Arkive mesh mod installation",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshDeploymentBusy = false;
            await RefreshModifiedMeshUpksAsync("mesh deployment completed", row.Entry.Manifest.EditId);
            await RefreshGuidedTextureModsAsync("mesh deployment changed target");
            if (SkeletalMeshEditedGlbsGrid?.SelectedItem is SkeletalMeshEditedGlbRow selected &&
                selected.Entry.Manifest.EditId.Equals(
                    row.Entry.Manifest.EditId, StringComparison.OrdinalIgnoreCase))
                await RefreshSelectedSkeletalMeshDeploymentAsync("Modified UPKs deployment completed");
        }
    }

}
