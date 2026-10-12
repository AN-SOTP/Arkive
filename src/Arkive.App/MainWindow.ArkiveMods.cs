using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Arkive.Core.Mods;
using Microsoft.Win32;

namespace Arkive.App;

public partial class MainWindow : Window
{
    private readonly ArkiveModLibrary _arkiveModLibrary;
    private readonly ArkiveModEditWorkspaces _arkiveModEdits;
    private readonly ArkiveModApplyContext _arkiveModContext;
    private readonly ArkiveModAuthoringService _arkiveModAuthoring;
    private readonly ArkiveModApplyService _arkiveModApply;
    private ArkiveModLibraryManifest _arkiveModLibraryManifest =
        new(ArkiveModLibrary.CurrentSchemaVersion, DateTimeOffset.UtcNow, null, []);
    private bool _modLibraryBusy;

    private bool ModLibraryIdle => !_modLibraryBusy && !_guidedTextureModInProgress && !_modSetBusy && !_authoringBusy;

    private async Task InitializeArkiveModLibraryAsync()
    {
        try
        {
            var scan = await _arkiveModAuthoring.ScanAsync();
            await EnsureGamePackagesDirectoryAsync();
            if (!scan.Library.PackageProjectsMigrated)
            {
                var migration = await new ArkiveModMigrationService(_arkiveModContext)
                    .MigratePackageProjectsAsync(progress: new Progress<string>(message => StatusText.Text = message));
                foreach (var migrated in migration.Migrated)
                    Log($"Package project converted to .arkive mod: package={migrated.PackageName}, " +
                        $"mod={migrated.ModId}, file={migrated.ModFilePath}, enabled={migrated.Enabled}, " +
                        $"assets={migrated.AssetCount}, warnings={string.Join(" | ", migrated.Warnings)}.");
                if (migration.Migrated.Count > 0)
                    StatusText.Text = $"Saved {migration.Migrated.Count:N0} existing package project(s) as .arkive mods in " +
                                      $"{_arkiveModLibrary.ModsDirectory}.";
            }
            foreach (var change in await ArkiveModSelectionService.SynchronizeAsync(_arkiveModLibrary, _arkiveModEdits))
                Log($"Mod selection synchronized at startup: mod={change.ModName}, object={change.ObjectName}, " +
                    $"edit={change.EditId}, included={change.Included}.");
            var library = await _arkiveModLibrary.LoadAsync();
            Log($"Mod library ready: mods={library.Mods.Count}, enabled={library.Mods.Count(mod => mod.Enabled)}, " +
                $"folder={_arkiveModLibrary.ModsDirectory}, state={_arkiveModLibrary.ManifestPath}, " +
                $"gamePackages={library.GamePackagesDirectory ?? "unknown"}.");
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            Log($"Mod library initialization failed: {exception}");
        }
    }

    private async Task EnsureGamePackagesDirectoryAsync()
    {
        var candidates = _guidedPackageRows
            .Select(package => (string?)package.Entry.Manifest.SourcePackagePath)
            .Prepend(_libraryRootPath);
        if (await _arkiveModLibrary.InferGamePackagesDirectoryAsync(candidates) is { } directory)
            Log($"Mod library game package folder: {directory}");
    }

    private async Task RefreshArkiveModsAsync(string trigger, string? selectModId = null)
    {
        if (ModLibraryGrid is null) return;
        try
        {
            var scan = await _arkiveModAuthoring.ScanAsync();
            var library = scan.Library;
            _arkiveModLibraryManifest = library;
            var snapshot = await ArkiveModEditSnapshot.LoadAsync(_arkiveModEdits);
            var statuses = await ArkiveModApplyService.DescribeAsync(
                _arkiveModLibrary, library, snapshot, ActiveRevisionsByPhysicalName());
            var previous = selectModId ?? (ModLibraryGrid.SelectedItem as ModLibraryRow)?.Entry.ModId;
            var drafts = (await _modAuthoringWorkspace.LoadAsync()).Where(draft => draft.ModId is not null)
                .ToDictionary(draft => draft.ModId!, StringComparer.OrdinalIgnoreCase);
            var rows = statuses
                .Select(status => new ModLibraryRow(status, _arkiveModLibrary.GetModFilePath(status.Mod))
                {
                    AuthoringPending = drafts.TryGetValue(status.Mod.ModId, out var draft) && DraftHasChanges(draft, status.Mod),
                    NeedsGameUpdate = _modsNeedingGameUpdate.GetValueOrDefault(status.Mod.ModId) == status.Mod.FileSha256,
                })
                .OrderByDescending(row => row.Enabled)
                .ThenByDescending(row => row.Priority)
                .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ModLibraryGrid.ItemsSource = rows;
            var selected = rows.FirstOrDefault(row => row.Entry.ModId.Equals(
                               previous, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
            ModLibraryGrid.SelectedItem = selected;
            if (selected is not null) ModLibraryGrid.ScrollIntoView(selected);
            ApplySelectedModLibraryRow();

            var enabled = rows.Count(row => row.Enabled);
            var inGame = rows.Count(row => row.Status.InGame);
            var ties = rows.Count(row => row.Status.TiedCount > 0);
            var pending = rows.Count(row => row.Enabled && !row.Status.InGame &&
                                            row.Status.WinningCount > 0 && row.Status.TiedCount == 0);
            var disabledActive = rows.Count(row => !row.Enabled && row.HasActiveAssets);
            var unsaved = rows.Count(row => row.Enabled && (row.Status.ModifiedLocally || row.AuthoringPending));
            ModLibraryPendingText.Text = rows.Length == 0 ? string.Empty : ties > 0
                ? $"{ties:N0} conflict(s) need a choice before applying. Select a mod and review Conflicts."
                : pending + disabledActive + unsaved > 0
                    ? "Changes pending · Apply changes will save edits, review the plan, and update the game."
                    : "Applied selection is up to date.";
            ModLibraryStatusText.Text = rows.Length == 0
                ? "No mods yet. Choose New mod to create one, or Import to add a shared .arkive file."
                : $"{rows.Length:N0} mod(s), {enabled:N0} enabled, {inGame:N0} fully in game. " +
                  (ties > 0 ? $"{ties:N0} mod(s) have tied priorities. " : string.Empty) +
                  (pending + disabledActive > 0 ? "Apply changes to update the game. " : "The game matches the enabled mods. ") +
                  (scan.Issues.Count > 0 ? $"{scan.Issues.Count:N0} file(s) in the mods folder are not valid .arkive mods (see Diagnostics)." : string.Empty);
            foreach (var issue in scan.Issues)
                Log($"Mod library file ignored: path={issue.Path}, reason={issue.Message}");
            Log($"Mod library refreshed: trigger={trigger}, mods={rows.Length}, enabled={enabled}, " +
                $"inGame={inGame}, ties={ties}, registered={scan.RegisteredModIds.Count}, " +
                $"refreshed={scan.RefreshedModIds.Count}, released={scan.Released.Count}.");
            if (scan.Released.Count > 0) await RefreshGuidedTextureModsAsync("mod file updated");
            await RefreshModAuthoringAsync();
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            ModLibraryStatusText.Text = $"The mod library could not be read: {exception.Message}";
            Log($"Mod library refresh failed: trigger={trigger}, error={exception}");
        }
        finally
        {
            UpdateModLibraryActionState();
        }
    }

    private IReadOnlyDictionary<string, GuidedTexturePackageRevision?> ActiveRevisionsByPhysicalName()
    {
        var gameDirectory = _arkiveModLibraryManifest.GamePackagesDirectory;
        var projects = _guidedPackageRows.Select(row => row.Entry).ToArray();
        var map = new Dictionary<string, GuidedTexturePackageRevision?>(StringComparer.Ordinal);
        foreach (var row in _guidedPackageRows)
        {
            var physical = Path.GetFileName(row.Entry.Manifest.SourcePackagePath);
            if (ArkiveModApplyService.FindPackageProject(physical, gameDirectory, projects) != row.Entry) continue;
            map[ArkiveModFileService.PhysicalPackageKey(physical)] =
                HasActiveGuidedDeployment(row) ? row.DeploymentRevision : null;
        }
        return map;
    }

    private void ApplySelectedModLibraryRow()
    {
        if (ModLibraryGrid.SelectedItem is not ModLibraryRow row)
        {
            ModLibraryAssetsGrid.ItemsSource = null;
            ModLibraryConflictsGrid.ItemsSource = null;
            ModDetailsNameTextBox.Text = ModDetailsVersionTextBox.Text = ModDetailsAuthorTextBox.Text =
                ModDetailsWebsiteTextBox.Text = ModDetailsDescriptionTextBox.Text = string.Empty;
            ModDetailsInfoText.Text = string.Empty;
            return;
        }
        var selectedAssets = ModLibraryAssetsGrid.SelectedItems.OfType<ModAssetRow>()
            .Select(asset => asset.Status.Asset.AssetId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assetRows = row.Status.Assets.Select(asset => new ModAssetRow(asset)).ToArray();
        ModLibraryAssetsGrid.ItemsSource = assetRows;
        foreach (var asset in assetRows.Where(asset => selectedAssets.Contains(asset.Status.Asset.AssetId)))
            ModLibraryAssetsGrid.SelectedItems.Add(asset);
        ModLibraryConflictsGrid.ItemsSource = ArkiveModConflictResolver.Resolve(_arkiveModLibraryManifest.Mods)
            .ConflictsFor(row.Entry.ModId).Select(conflict => new ModConflictRow(conflict)).ToArray();
        var manifest = row.Entry.Manifest;
        ModDetailsNameTextBox.Text = manifest.Name;
        ModDetailsVersionTextBox.Text = manifest.Version;
        ModDetailsAuthorTextBox.Text = manifest.Author ?? string.Empty;
        ModDetailsWebsiteTextBox.Text = manifest.Website ?? string.Empty;
        ModDetailsDescriptionTextBox.Text = manifest.Description ?? string.Empty;
        ModDetailsInfoText.Text =
            $"File: {row.FilePath}\n" +
            $"Size: {row.Entry.FileBytes / 1024d / 1024d:N2} MiB · SHA-256 {row.Entry.FileSha256[..16]}…\n" +
            $"Mod ID: {row.Entry.ModId}\n" +
            $"Origin: {row.Origin}\n" +
            $"Created: {manifest.CreatedAt.ToLocalTime():g} · Updated: {manifest.UpdatedAt.ToLocalTime():g}\n" +
            $"Format: {manifest.Format} v{manifest.FormatVersion} · {manifest.GameProfile}\n" +
            $"Target packages: {string.Join(", ", manifest.PhysicalPackageNames)}" +
            (row.Status.FileMissing ? "\n\nThe .arkive file is missing from the mods folder." : string.Empty);
    }

    private void UpdateModLibraryActionState()
    {
        if (ModLibraryApplyButton is null) return;
        var idle = ModLibraryIdle;
        var row = ModLibraryGrid.SelectedItem as ModLibraryRow;
        var hasRow = row is not null && !row.Status.FileMissing;
        ModLibraryApplyButton.IsEnabled = idle;
        ModLibraryProgressBar.Visibility = _modLibraryBusy ? Visibility.Visible : Visibility.Collapsed;
        ModLibraryNewButton.IsEnabled = idle;
        ModLibraryEditButton.IsEnabled = idle && hasRow;
        ModLibraryPrepareButton.IsEnabled = idle && hasRow;
        var assetCount = ModLibraryAssetsGrid.SelectedItems.OfType<ModAssetRow>().Count();
        ModLibraryEditAssetButton.IsEnabled = idle && hasRow && assetCount == 1;
        ModLibraryPreviewButton.IsEnabled = idle;
        ModLibraryRestoreAllButton.IsEnabled = idle;
        ModLibraryImportButton.IsEnabled = idle;
        ModLibraryGameFolderButton.IsEnabled = idle;
        ModLibraryRefreshButton.IsEnabled = idle;
        ModLibraryExportButton.IsEnabled = idle && hasRow;
        ModLibraryEnableButton.IsEnabled = idle && row is not null;
        ModLibraryEnableButton.Header = row?.Enabled == true ? "Disable" : "Enable";
        ModLibraryPriorityUpButton.IsEnabled = idle && row is not null;
        ModLibraryPriorityDownButton.IsEnabled = idle && row is not null;
        ModLibraryMakeWinnerButton.IsEnabled = idle && row is not null &&
                                               row.Status.Assets.Any(asset => asset.Competitor is not null);
        ModLibrarySaveButton.IsEnabled = idle && hasRow && (row!.Status.ModifiedLocally || row.AuthoringPending);
        ModLibraryUpdateButton.IsEnabled = idle && hasRow;
        ModLibraryGoToPackageButton.IsEnabled = idle && row is not null;
        ModLibraryRemoveButton.IsEnabled = idle && row is not null;
        ModLibraryRemoveAssetButton.IsEnabled = idle && hasRow && assetCount > 0;
        ModLibraryRemoveAssetButton.Content = assetCount > 0 ? $"Remove selected ({assetCount:N0})" : "Remove selected assets";
        ModDetailsSaveButton.IsEnabled = idle && hasRow;
        UpdateAuthoringActions();
    }

    private async Task RunModLibraryOperationAsync(
        string title,
        Func<Task<string?>> operation,
        string? selectModId = null)
    {
        if (!ModLibraryIdle) return;
        _modLibraryBusy = true;
        UpdateModLibraryActionState();
        try
        {
            var message = await operation();
            if (!string.IsNullOrWhiteSpace(message))
            {
                ModLibraryStatusText.Text = message;
                StatusText.Text = message;
            }
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            Log($"Mod library operation failed: operation={title}, error={exception}");
            MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _modLibraryBusy = false;
            await RefreshArkiveModsAsync(title, selectModId);
        }
    }

    private async Task SynchronizeModSelectionsAsync(string trigger, bool onlyIfChanged = false)
    {
        var changes = await ArkiveModSelectionService.SynchronizeAsync(_arkiveModLibrary, _arkiveModEdits);
        foreach (var change in changes)
            Log($"Mod selection synchronized: trigger={trigger}, mod={change.ModName}, kind={change.Kind}, " +
                $"object={change.ObjectName}, edit={change.EditId}, included={change.Included}.");
        if (onlyIfChanged && changes.Count == 0) return;
        await RefreshGuidedTextureModsAsync(trigger);
        await RefreshMaterialProjectsAsync(trigger);
    }

    private async void ModLibraryRefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshArkiveModsAsync("refresh requested");

    private void ModLibraryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplySelectedModLibraryRow();
        UpdateModLibraryActionState();
    }

    private void ModLibraryAssetsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateModLibraryActionState();

    private async void ModLibraryEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: ModLibraryRow row })
            await SetModEnabledAsync(row, !row.Enabled);
    }

    private async void ModLibraryEnableButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is ModLibraryRow row)
            await SetModEnabledAsync(row, !row.Enabled);
    }

    private Task SetModEnabledAsync(ModLibraryRow row, bool enabled) =>
        RunModLibraryOperationAsync(enabled ? "Enable mod" : "Disable mod", async () =>
        {
            await _arkiveModLibrary.SetEnabledAsync(row.Entry.ModId, enabled);
            Log($"Mod {(enabled ? "enabled" : "disabled")}: mod={row.Entry.ModId}, name={row.DisplayName}.");
            await SynchronizeModSelectionsAsync(enabled ? "mod enabled" : "mod disabled", onlyIfChanged: true);
            return $"{row.DisplayName} {(enabled ? "enabled" : "disabled")}. Choose Apply changes to update the game.";
        }, row.Entry.ModId);

    private void ModLibraryGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not ModLibraryRow row ||
            e.EditingElement is not TextBox textBox || !"Priority".Equals(e.Column.Header as string, StringComparison.Ordinal))
            return;
        if (!int.TryParse(textBox.Text.Trim().Replace('−', '-'), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.CurrentCulture, out var priority))
        {
            textBox.Text = row.Priority.ToString(System.Globalization.CultureInfo.CurrentCulture);
            MessageBox.Show(this,
                $"Priority must be a whole number between {ArkiveModLibrary.MinimumPriority:N0} and {ArkiveModLibrary.MaximumPriority:N0}.",
                "Mod priority", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (priority == row.Priority) return;
        // Changing ItemsSource while the grid is still committing the edit is not allowed.
        _ = Dispatcher.InvokeAsync(() => SetModPriorityAsync(row, priority), DispatcherPriority.Background);
    }

    private async void ModLibraryPriorityUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is ModLibraryRow row)
            await SetModPriorityAsync(row, Math.Min(ArkiveModLibrary.MaximumPriority, row.Priority + 1));
    }

    private async void ModLibraryPriorityDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is ModLibraryRow row)
            await SetModPriorityAsync(row, Math.Max(ArkiveModLibrary.MinimumPriority, row.Priority - 1));
    }

    private async void ModLibraryMakeWinnerButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is not ModLibraryRow row) return;
        var priority = ArkiveModConflictResolver.Resolve(_arkiveModLibraryManifest.Mods)
            .PriorityToWinAll(row.Entry.ModId);
        if (priority <= row.Priority)
        {
            ModLibraryStatusText.Text = $"{row.DisplayName} already wins every conflict with an enabled mod.";
            return;
        }
        await SetModPriorityAsync(row, priority);
    }

    private Task SetModPriorityAsync(ModLibraryRow row, int priority) =>
        RunModLibraryOperationAsync("Mod priority", async () =>
        {
            await _arkiveModLibrary.SetPriorityAsync(row.Entry.ModId, priority);
            Log($"Mod priority changed: mod={row.Entry.ModId}, name={row.DisplayName}, " +
                $"from={row.Priority}, to={priority}.");
            await SynchronizeModSelectionsAsync("mod priority changed", onlyIfChanged: true);
            return $"{row.DisplayName} now has priority {priority:N0}. Choose Apply changes to update the game.";
        }, row.Entry.ModId);

    private async void ModLibraryImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Arkive mods",
            Filter = "Arkive mods (*.arkive)|*.arkive",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        await ImportModFilesAsync(dialog.FileNames);
    }

    private async Task ImportModFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !ModLibraryIdle) return;
        ModifiedUpksTab.IsSelected = true;
        ModLibraryTab.IsSelected = true;
        string? lastImported = null;
        await RunModLibraryOperationAsync("Import .arkive", async () =>
        {
            var imported = new List<ArkiveModImportResult>();
            var failures = new List<string>();
            foreach (var path in paths)
            {
                try
                {
                    ArkiveModImportResult result;
                    try
                    {
                        result = await _arkiveModAuthoring.ImportAsync(path);
                    }
                    catch (ArkiveModRefusalException refusal) when (refusal.Code == "LIB012")
                    {
                        if (MessageBox.Show(this, $"{refusal.Message}\n\nReplace it with {Path.GetFileName(path)}?",
                                "Import .arkive", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                                MessageBoxResult.No) != MessageBoxResult.Yes) continue;
                        result = await _arkiveModAuthoring.ImportAsync(path, replace: true);
                    }
                    imported.Add(result);
                    Log($"Mod imported: source={path}, mod={result.Entry.ModId}, name={result.Entry.Manifest.Name}, " +
                        $"version={result.Entry.Manifest.Version}, updated={result.UpdatedExistingMod}, " +
                        $"assets={result.Entry.Manifest.Assets.Count}, released={result.Released.Count}.");
                }
                catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
                {
                    failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
                    Log($"Mod import refused: source={path}, error={exception.Message}");
                }
            }
            lastImported = imported.LastOrDefault()?.Entry.ModId;
            if (failures.Count > 0)
                MessageBox.Show(this,
                    $"{failures.Count:N0} file(s) were not imported:\n\n{string.Join("\n", failures.Take(10))}",
                    "Import .arkive", MessageBoxButton.OK, MessageBoxImage.Warning);
            var added = imported.Where(result => !result.Entry.Enabled).ToArray();
            if (added.Length > 0 && MessageBox.Show(this,
                    $"Imported {imported.Count:N0} mod(s):\n\n" +
                    string.Join("\n", imported.Take(12).Select(result =>
                        $"  • {result.Entry.Manifest.Name} {result.Entry.Manifest.Version}" +
                        (result.UpdatedExistingMod ? " (updated)" : string.Empty))) +
                    "\n\nEnable the new mods now? You can set priorities before choosing Apply changes.",
                    "Import .arkive", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                foreach (var result in added)
                    await _arkiveModLibrary.SetEnabledAsync(result.Entry.ModId, true);
            }
            await SynchronizeModSelectionsAsync("mods imported");
            return imported.Count == 0
                ? null
                : $"Imported {imported.Count:N0} mod(s). Review conflicts, then choose Apply changes.";
        });
        if (lastImported is not null) await RefreshArkiveModsAsync("import completed", lastImported);
    }

    private async void ModLibraryExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is not ModLibraryRow row) return;
        if (row.Status.ModifiedLocally || row.AuthoringPending)
        {
            var answer = MessageBox.Show(this,
                $"{row.DisplayName} has local changes that are not in its .arkive file yet. Save them before sharing?",
                "Share mod", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                var saved = false;
                await RunModLibraryOperationAsync("Save mod", async () =>
                {
                    await SaveLibraryModAsync(row.Entry.ModId);
                    saved = true;
                    return null;
                }, row.Entry.ModId);
                if (!saved) return;
            }
        }
        var dialog = new SaveFileDialog
        {
            Title = "Share Arkive mod",
            Filter = "Arkive mods (*.arkive)|*.arkive",
            FileName = row.FileName,
            AddExtension = true,
            DefaultExt = ArkiveModFileFormat.Extension,
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunModLibraryOperationAsync("Share mod", async () =>
        {
            var source = _arkiveModLibrary.GetModFilePath(row.Entry);
            var destination = Path.GetFullPath(dialog.FileName);
            if (destination.Equals(source, StringComparison.OrdinalIgnoreCase))
                return "That is the library's own copy; choose another folder to share it.";
            var verified = await ArkiveModFileService.ReadAsync(source);
            File.Copy(source, destination, overwrite: true);
            Log($"Mod exported: mod={row.Entry.ModId}, destination={destination}, sha256={verified.Sha256}.");
            return $"Saved {Path.GetFileName(destination)} ({verified.Bytes / 1024d / 1024d:N2} MiB). " +
                   "It contains only the edited assets; recipients rebuild it from their own game files.";
        }, row.Entry.ModId);
    }

    private void ModLibraryOpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_arkiveModLibrary.ModsDirectory);
        OpenFolder(_arkiveModLibrary.ModsDirectory, "Arkive mods folder");
    }

    private async void ModLibraryGameFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose Lost Ark's EFGame\\ReleasePC\\Packages folder",
            InitialDirectory = ExistingDirectory(_arkiveModLibraryManifest.GamePackagesDirectory) ?? string.Empty,
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunModLibraryOperationAsync("Game folder", async () =>
        {
            var folder = Path.GetFullPath(dialog.FolderName);
            if (!Directory.EnumerateFiles(folder, "*.upk").Any())
                throw new DirectoryNotFoundException(
                    $"{folder} contains no .upk packages. Choose Lost Ark's EFGame\\ReleasePC\\Packages folder.");
            await _arkiveModLibrary.SetGamePackagesDirectoryAsync(folder);
            Log($"Mod library game package folder set: {folder}");
            return $"Imported mods will be set up from {folder}.";
        });
    }

    private async void ModLibrarySaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is not ModLibraryRow row) return;
        await RunModLibraryOperationAsync("Save mod", async () =>
        {
            var saved = await SaveLibraryModAsync(row.Entry.ModId);
            Log($"Mod saved from local changes: mod={saved.ModId}, file={saved.FileName}, sha256={saved.FileSha256}.");
            return $"Saved local changes into {saved.FileName}.";
        }, row.Entry.ModId);
    }

    private async void ModDetailsSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is not ModLibraryRow row) return;
        var metadata = new ArkiveModMetadata(
            ModDetailsNameTextBox.Text,
            ModDetailsVersionTextBox.Text,
            ModDetailsAuthorTextBox.Text,
            ModDetailsDescriptionTextBox.Text,
            ModDetailsWebsiteTextBox.Text,
            row.Entry.Manifest.Tags);
        await RunModLibraryOperationAsync("Save mod details", async () =>
        {
            var saved = await _arkiveModAuthoring.SaveModAsync(row.Entry.ModId, metadata);
            var drafts = await _modAuthoringWorkspace.LoadAsync();
            if (drafts.FirstOrDefault(draft => draft.ModId == saved.ModId) is { } draft)
            {
                var updated = await _modAuthoringWorkspace.SaveAsync(draft with { Metadata = metadata });
                if (_authoringDraft?.DraftId == updated.DraftId)
                { _authoringDraft = updated; LoadAuthoringMetadata(); }
            }
            Log($"Mod details saved: mod={saved.ModId}, name={saved.Manifest.Name}, version={saved.Manifest.Version}.");
            return $"Saved details for {saved.Manifest.Name} {saved.Manifest.Version}.";
        }, row.Entry.ModId);
    }

    private async void ModLibraryUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is not ModLibraryRow row) return;
        if (MessageBox.Show(this,
                $"Update '{row.DisplayName}' for the installed Lost Ark version?\n\n" +
                "Arkive sets up every asset of this mod against your current clean game files. Assets the game has changed since the mod was made are re-applied only if they pass every native safety check (texture layout, mesh skeleton and preflight, material parameters). " +
                "Review changed assets in UModel before playing. No game file is changed until you choose Apply changes.",
                "Update for this game version", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunModLibraryOperationAsync("Update for this game version", async () =>
        {
            ModLibraryProgressBar.IsIndeterminate = true;
            try
            {
                var outcomes = await _arkiveModApply.UpdateModForCurrentClientAsync(row.Entry.ModId);
                foreach (var outcome in outcomes)
                    Log($"Mod asset update: mod={row.Entry.ModId}, asset={outcome.DisplayName}, " +
                        $"succeeded={outcome.Succeeded}, changed={outcome.Changed}, message={outcome.Message}");
                await SynchronizeModSelectionsAsync("mod updated for client");
                var failed = outcomes.Where(outcome => !outcome.Succeeded).ToArray();
                if (failed.Length == 0) _modsNeedingGameUpdate.Remove(row.Entry.ModId);
                MessageBox.Show(this,
                    string.Join("\n", outcomes.Select(outcome =>
                        $"{(outcome.Succeeded ? "✓" : "✗")} {outcome.DisplayName}: {outcome.Message}")) +
                    (failed.Length > 0
                        ? "\n\nAssets that failed need the mod author (or you) to re-export and re-edit them for this game version, then save them into the mod."
                        : "\n\nChoose Apply changes to rebuild and install the updated mod."),
                    "Update for this game version", MessageBoxButton.OK,
                    failed.Length > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                return failed.Length == 0
                    ? $"{row.DisplayName} is set up for this game version."
                    : $"{failed.Length:N0} asset(s) of {row.DisplayName} still need an update.";
            }
            finally
            {
                ModLibraryProgressBar.IsIndeterminate = false;
            }
        }, row.Entry.ModId);
    }

    private void ModLibraryGoToPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is not ModLibraryRow row) return;
        var physical = (ModLibraryAssetsGrid.SelectedItem as ModAssetRow)?.Status.Asset.Target.PhysicalPackageName ??
                       row.Entry.Manifest.Assets[0].Target.PhysicalPackageName;
        var package = _guidedPackageRows.FirstOrDefault(item => Path.GetFileName(item.Entry.Manifest.SourcePackagePath)
            .Equals(ArkiveModFileService.NormalizePhysicalPackageName(physical), StringComparison.OrdinalIgnoreCase));
        if (package is null)
        {
            ModLibraryStatusText.Text = "This mod's package has no package project yet. Apply changes creates it.";
            return;
        }
        ModifiedUpksInnerTabControl.SelectedItem = GuidedPackageProjectsTab;
        GuidedPackagesGrid.SelectedItem = package;
        GuidedPackagesGrid.ScrollIntoView(package);
    }

    private async void ModLibraryRemoveAssetButton_Click(object sender, RoutedEventArgs e)
    {
        var assets = ModLibraryAssetsGrid.SelectedItems.OfType<ModAssetRow>().ToArray();
        if (!ModLibraryIdle || ModLibraryGrid.SelectedItem is not ModLibraryRow row || assets.Length == 0) return;
        if (assets.Length == row.Entry.Manifest.Assets.Count)
        {
            await RunAuthoringOperationAsync(async () =>
            {
                await OpenModForAuthoringAsync(row.Entry.ModId, prepare: false);
                await LoadAuthoringAssetsAsync();
                var workingRows = AuthoringAssetsGrid.Items.OfType<AuthoringAssetRow>().ToArray();
                await RemoveAuthoringAssetsAsync(assets.Select(asset =>
                    workingRows.FirstOrDefault(working => working.AssetId == asset.Status.Asset.AssetId) ??
                    new AuthoringAssetRow(asset.DisplayName, asset.Kind, asset.Package, asset.ObjectName,
                        "Saved", true, null, asset.Status.Asset.AssetId)).ToArray());
            });
            return;
        }
        if (MessageBox.Show(this,
                $"Remove {assets.Length:N0} selected asset(s) from '{row.DisplayName}'?\n\nThe assets are removed from {row.FileName}. " +
                "Their local working copies are kept as unchecked drafts in their package projects.",
                "Remove assets from mod", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunModLibraryOperationAsync("Remove assets from mod", async () =>
        {
            await _arkiveModAuthoring.SaveModAsync(row.Entry.ModId,
                removeAssetIds: assets.Select(asset => asset.Status.Asset.AssetId).ToArray());
            var draft = (await _modAuthoringWorkspace.LoadAsync()).FirstOrDefault(draft => draft.ModId == row.Entry.ModId);
            if (draft is not null)
            {
                var updated = await _modAuthoringWorkspace.SaveAsync(draft.RemoveAssets([], assets.Select(asset => asset.Status.Asset.AssetId)));
                if (_authoringDraft?.DraftId == updated.DraftId) _authoringDraft = updated;
            }
            await SynchronizeModSelectionsAsync("assets removed from mod");
            return $"Removed {assets.Length:N0} asset(s) from {row.DisplayName}. Apply changes to update the game.";
        }, row.Entry.ModId);
    }

    private async void ModLibraryRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is not ModLibraryRow row) return;
        if (row.HasActiveAssets)
        {
            MessageBox.Show(this,
                $"{row.DisplayName} is in game. Disable it and choose Apply changes first, so Arkive can rebuild or restore the packages it replaces.",
                "Remove mod", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"Remove '{row.DisplayName}' from Arkive?\n\n" +
                $"This deletes {row.FilePath}. Use Share / export first if you want to keep a copy.\n\n" +
                (row.Entry.Origin == ArkiveModOrigin.Imported
                    ? "Its local working copies were created from the .arkive file and are deleted too."
                    : "Its local working copies stay in their package projects as unchecked drafts."),
                "Remove mod", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) !=
            MessageBoxResult.Yes) return;
        await RunModLibraryOperationAsync("Remove mod", async () =>
        {
            await _arkiveModAuthoring.RemoveModAsync(row.Entry.ModId);
            Log($"Mod removed: mod={row.Entry.ModId}, name={row.DisplayName}, origin={row.Entry.Origin}, file={row.FilePath}.");
            await SynchronizeModSelectionsAsync("mod removed");
            return $"Removed {row.DisplayName}.";
        });
    }

    private async void ModLibraryPreviewButton_Click(object sender, RoutedEventArgs e) =>
        await RunModApplyAsync(previewOnly: true);

    private async void ModLibraryApplyButton_Click(object sender, RoutedEventArgs e) =>
        await RunModApplyAsync(previewOnly: false);

    private async void ModLibraryRestoreAllButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuidedQuickDeploymentAsync(restoreAll: true);
        await RefreshArkiveModsAsync("restore all requested");
    }

    private async Task RunModApplyAsync(bool previewOnly, string? previewModId = null)
    {
        if (!ModLibraryIdle) return;
        _modLibraryBusy = true;
        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        var changedGame = false;
        try
        {
            ModLibraryStatusText.Text = "Checking enabled mods, priorities, and installed packages...";
            ModLibraryProgressBar.IsIndeterminate = true;
            await EnsureGamePackagesDirectoryAsync();
            if (!previewOnly) await SaveEnabledModChangesAsync();
            var plan = previewModId is null ? await _arkiveModApply.PlanAsync() : await _arkiveModApply.PreviewModAsync(previewModId);
            ModLibraryProgressBar.IsIndeterminate = false;
            ModLibraryPlanGrid.ItemsSource = plan.Packages.Select(package => new ModPlanRow(package)).ToArray();
            ModLibraryPlanTab.IsSelected = true;
            foreach (var package in plan.Packages)
                foreach (var contributor in package.Contributors.Where(contributor => contributor.ModId is not null))
                    if (package.Action == ArkiveModPackageAction.Blocked &&
                        package.Reason.Contains("Update for this game version", StringComparison.Ordinal) &&
                        package.Reason.Contains(contributor.ModName + ":", StringComparison.Ordinal) &&
                        _arkiveModLibraryManifest.Find(contributor.ModId!) is { } mod)
                        _modsNeedingGameUpdate[mod.ModId] = mod.FileSha256;
                    else if (package.Action != ArkiveModPackageAction.Blocked)
                        _modsNeedingGameUpdate.Remove(contributor.ModId!);
            await RefreshArkiveModsAsync("apply plan reviewed");
            Log($"Mod apply plan: canApply={plan.CanApply}, changes={plan.HasChanges}, packages={plan.Packages.Count}, " +
                $"summary={plan.Summary}, problems={string.Join(" | ", plan.BlockingProblems)}, " +
                $"warnings={string.Join(" | ", plan.Warnings)}.");
            ModLibraryStatusText.Text = previewModId is null ? plan.Summary :
                "Preview if this mod is enabled · nothing was applied. " + plan.Summary;
            if (!plan.CanApply)
            {
                if (!previewOnly)
                    MessageBox.Show(this,
                        "Arkive did not change any game file:\n\n" +
                        string.Join("\n", plan.BlockingProblems.Take(12).Select(problem => $"  • {problem}")),
                        "Apply mod changes", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!plan.HasChanges || previewOnly)
            {
                return;
            }
            var lines = plan.Packages.Where(package => package.Action != ArkiveModPackageAction.None)
                .Take(16)
                .Select(package => $"  • {package.PackageAlias}: {package.Describe}" +
                                   (package.Contributors.Count == 0
                                       ? string.Empty
                                       : $" — {string.Join(", ", package.Contributors.Select(item => item.IsDraft ? item.ObjectName + " (draft)" : item.ModName).Distinct())}"));
            if (MessageBox.Show(this,
                    $"Apply mod changes?\n\n{string.Join("\n", lines)}\n\n{plan.Summary}\n\n" +
                    "Installed packages are restored first, every rebuilt package passes Arkive's native validation, and all installs happen in one transaction that rolls back on failure. " +
                    "Close Lost Ark, its launcher, and Easy Anti-Cheat before continuing.",
                    "Apply mod changes", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) !=
                MessageBoxResult.Yes) return;

            changedGame = true;
            var progress = new Progress<ArkiveModApplyProgress>(update =>
            {
                ModLibraryProgressBar.Maximum = Math.Max(1, update.TotalSteps);
                ModLibraryProgressBar.Value = Math.Min(update.Step, update.TotalSteps);
                ModLibraryStatusText.Text = $"Step {update.Step:N0} of {update.TotalSteps:N0} · {update.Package}: {update.Message}";
                StatusText.Text = update.Message;
                Log($"Mod apply progress: step={update.Step}/{update.TotalSteps}, package={update.Package}, message={update.Message}");
            });
            var result = await _arkiveModApply.ApplyAsync(plan.Fingerprint, progress);
            foreach (var package in result.Packages)
                Log($"Mod apply package result: package={package.PhysicalPackageName}, action={package.Action}, " +
                    $"succeeded={package.Succeeded}, revision={package.RevisionId ?? "-"}, message={package.Message}");
            var summary = $"Set up {result.SetUpAssetCount:N0} asset(s), rebuilt {result.RebuiltPackageCount:N0}, " +
                          $"installed {result.InstalledPackageCount:N0}, and restored {result.RestoredPackageCount:N0} package(s) " +
                          $"in {result.Duration.TotalSeconds:N1}s.";
            ModLibraryStatusText.Text = result.Succeeded ? $"Mods applied. {summary}" : $"Apply finished with errors. {summary}";
            if (!result.Succeeded) MessageBox.Show(this,
                $"Some packages could not be updated; packages that failed were left restored or unchanged.\n\n" +
                      string.Join("\n", result.Packages.Where(package => !package.Succeeded)
                          .Select(package => $"  • {package.PhysicalPackageName}: {package.Message}")) +
                      $"\n\n{summary}",
                "Apply mod changes", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            Log($"Mod apply failed: error={exception}");
            ModLibraryStatusText.Text = $"Apply stopped: {exception.Message}";
            MessageBox.Show(this, exception.Message, "Apply mod changes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ModLibraryProgressBar.IsIndeterminate = false;
            _modLibraryBusy = false;
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
            if (changedGame)
            {
                await RefreshGuidedTextureModsAsync("mods applied");
                await RefreshMaterialProjectsAsync("mods applied");
                await RefreshModSetsAsync("mods applied");
            }
        }
    }

    private async void GuidedSaveArkiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress ||
            GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow package) return;
        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        try
        {
            var mod = await SavePackageProjectToArkiveAsync(package.Entry);
            var message = mod is null
                ? "This UPK has no checked draft edits to save. Assets already in a mod are saved from Mod library."
                : $"Saved to {_arkiveModLibrary.GetModFilePath(mod)}.";
            GuidedBuildStatusText.Text = message;
            StatusText.Text = message;
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            Log($"Package project .arkive save failed: package={package.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Save checked to .arkive mod", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            await SynchronizeModSelectionsAsync("package saved to .arkive");
        }
    }

    private async Task<ArkiveModLibraryEntry?> SavePackageProjectToArkiveAsync(GuidedTexturePackageEntry package)
    {
        var mod = await _arkiveModAuthoring.SavePackageProjectAsync(package);
        if (mod is not null)
            Log($"Package project saved as .arkive mod: package={package.Manifest.PackageProjectId}, " +
                $"mod={mod.ModId}, file={_arkiveModLibrary.GetModFilePath(mod)}, assets={mod.Manifest.Assets.Count}, " +
                $"enabled={mod.Enabled}, priority={mod.Priority}, sha256={mod.FileSha256}.");
        return mod;
    }

    private IReadOnlyDictionary<string, string> ModOwnersByEditId() => _arkiveModLibraryManifest.Mods
        .SelectMany(mod => mod.Bindings.Select(binding => (binding.EditId, mod.Manifest.Name)))
        .GroupBy(item => item.EditId, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
}
