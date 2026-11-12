using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Arkive.Core.GameData;
using Arkive.Core.Mods;
using Arkive.Core.Tweaks;
using Arkive.Core.Outfits;
using Microsoft.Win32;

namespace Arkive.App;

public partial class MainWindow : Window
{
    private readonly ArkiveModLibrary _arkiveModLibrary;
    private string? _loggedModLibraryState;
    private readonly ArkiveModEditWorkspaces _arkiveModEdits;
    private readonly ArkiveModApplyContext _arkiveModContext;
    private readonly ArkiveModAuthoringService _arkiveModAuthoring;
    private readonly ArkiveModApplyService _arkiveModApply;
    private ArkiveModLibraryManifest _arkiveModLibraryManifest =
        new(ArkiveModLibrary.CurrentSchemaVersion, DateTimeOffset.UtcNow, null, []);
    private bool _modLibraryBusy;

    /// <summary>
    /// True from launch until the mods' working files, install state and game files have been checked again (see
    /// MainWindow.ReadyChecked): until then the Mods list may show what was remembered, and nothing that writes or
    /// needs verified state can start.
    /// </summary>
    private bool _modsChecking = true;

    private bool ModLibraryIdle => !_modsChecking && !_modLibraryBusy && !_guidedTextureModInProgress && !_modSetBusy && !_authoringBusy;

    /// <summary>The library scan launch made, for the check that follows it; null once anything may have changed it.</summary>
    private ArkiveModLibraryScanResult? _startupScan;

    /// <summary>
    /// Scans the mods folder and settles the library (migration, game folder). It no longer reads the working files:
    /// the check that follows reads those of enabled mods, and the background pass the rest (see MainWindow.ReadyChecked).
    /// </summary>
    private async Task InitializeArkiveModLibraryAsync()
    {
        try
        {
            var scan = await _arkiveModAuthoring.ScanAsync();
            _startupScan = scan;
            // A known game folder is kept as it is (the library is not read again for it); otherwise it is looked for,
            // which may change the library.
            if (scan.Library.GamePackagesDirectory is not { } games || !Directory.Exists(games))
            {
                _startupScan = null;
                await EnsureGamePackagesDirectoryAsync();
            }
            if (!scan.Library.PackageProjectsMigrated)
            {
                _startupScan = null;
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
            var library = _startupScan?.Library ?? await _arkiveModLibrary.LoadAsync();
            Log($"Mod library ready: mods={library.Mods.Count}, enabled={library.Mods.Count(mod => mod.Enabled)}, " +
                $"folder={_arkiveModLibrary.ModsDirectory}, state={_arkiveModLibrary.ManifestPath}, " +
                $"gamePackages={library.GamePackagesDirectory ?? "unknown"}.");
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            _startupScan = null;
            Log($"Mod library initialization failed: {exception}");
        }
    }

    private async Task EnsureGamePackagesDirectoryAsync()
    {
        // The package view's projects when it has been read, otherwise the same projects as the Mods list last read them.
        var projects = _guidedPackageRows.Count > 0 ? _guidedPackageRows.Select(row => row.Entry) : _installStateProjects;
        var candidates = projects
            .Select(package => (string?)package.Manifest.SourcePackagePath)
            .Prepend(_libraryRootPath);
        if (await _arkiveModLibrary.InferGamePackagesDirectoryAsync(candidates) is { } directory)
            Log($"Mod library game package folder: {directory}");
    }

    /// <summary>
    /// Counts Mods list refreshes started. Refreshes can overlap (Refresh, then Enable before it has finished, or a
    /// pack change), and one started earlier read the files before the later change; if it finished last it would
    /// show the list as it was, with the enabled mod off and its warning gone. Only the newest refresh shows its list.
    /// </summary>
    private int _modListRefreshes;

    /// <summary>The mod a refresh still running was asked to select (one just imported, say), for a newer one.</summary>
    private string? _modListPendingSelection;

    /// <param name="knownScan">A scan of the mods folder made just now (launch's), so the library is not read again.</param>
    /// <param name="onlyIfRowsChange">Leave the list on screen alone when no row would read differently (a background check).</param>
    /// <param name="installRead">How the install state is read: only launch and the background pass after it differ.</param>
    private async Task RefreshArkiveModsAsync(string trigger, string? selectModId = null, ArkiveModLibraryScanResult? knownScan = null,
        bool onlyIfRowsChange = false, InstallStateRead installRead = InstallStateRead.Usual)
    {
        if (ModLibraryGrid is null) return;
        var refresh = ++_modListRefreshes;
        selectModId ??= _modListPendingSelection;
        _modListPendingSelection = selectModId;
        var released = false;
        bool Superseded()
        {
            if (refresh == _modListRefreshes) return false;
            // The newer refresh may have scanned after these were released and found nothing; reload the package
            // workspace when a view that shows it is opened or mods are applied, as after a deferred change.
            if (released) MarkPackageWorkspaceStale();
            Log($"Mod library refresh left to a newer one: trigger={trigger}.");
            return true;
        }
        var clock = Stopwatch.StartNew();
        var stages = new List<string>();
        // With the files the process opened or looked at in each stage, which decide how long it takes uncached.
        var io = Arkive.Core.Diagnostics.ProcessCounters.Sample();
        void Lap(string stage)
        {
            var now = Arkive.Core.Diagnostics.ProcessCounters.Sample();
            stages.Add($"{stage} {clock.Elapsed.TotalSeconds:N1}s/{now.OtherOperations - io.OtherOperations} file ops/" +
                       $"{(now.ReadBytes - io.ReadBytes) / 1048576d:N0} MB");
            io = now;
            clock.Restart();
        }
        try
        {
            var selectionOnly = _selectionOnlyChange;
            _selectionOnlyChange = null;
            // Reading and checking happen off the window's thread; only showing the list happens on it.
            var scan = knownScan ?? await Task.Run(() => _arkiveModAuthoring.ScanAsync());
            released = scan.Released.Count > 0;
            if (Superseded()) return;
            var library = scan.Library;
            _arkiveModLibraryManifest = library;
            Lap("scan");
            // After enabling, disabling or a priority change the working files are as the selection left them, unless
            // the scan found changed mod files.
            var snapshot = selectionOnly is not null && scan.RegisteredModIds.Count + scan.RefreshedModIds.Count +
                scan.Released.Count == 0
                ? selectionOnly
                : await Task.Run(() => ArkiveModEditSnapshot.LoadAsync(_arkiveModEdits));
            if (Superseded()) return;
            Lap("working files");
            var (activeRevisions, packageRecords, installedHere, installFromMemo, installCorrected) =
                await InstallStatesAsync(snapshot, library.GamePackagesDirectory, installRead);
            if (Superseded()) return;
            Lap("install state");
            // Choosing which mods are on changes no game file, so the last check of the game folder still holds.
            // Applying, restoring and Refresh check again, and every install verifies the game files itself. A launch
            // install state the background pass corrected may have hidden an outside change, so it is checked again.
            var outside = _outsideChanges;
            if (selectionOnly is null || !_outsideChangesChecked || installCorrected)
            {
                outside = [];
                try
                {
                    // The package projects just read for the install state, so they are not read twice, and the builds
                    // it just proved in the game, so they are not inspected twice.
                    outside = await Task.Run(() => _arkiveModApply.FindOutsideChangesAsync(packageRecords,
                        installedByThisArkive: installedHere is null ? null : installedHere.Contains));
                    _outsideChangesChecked = true;
                }
                catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
                { Log($"Checking game files for outside changes failed: {exception.Message}"); }
                foreach (var change in outside)
                    Log($"Game file changed outside this Arkive: {change.PhysicalPackageName} ({change.PackageAlias}), " +
                        $"sha256={change.GameSha256}, changed objects={string.Join(", ", change.ChangedObjects)}.");
                _outsideChanges = outside;
            }
            if (Superseded()) return;
            Lap("game files");
            var outsideNames = outside.Select(change => change.PhysicalPackageName).ToArray();
            var statuses = await Task.Run(async () =>
            {
                var installedTables = _arkiveModContext.GameDataFor(library)?.Archive.Current();
                var installedLooks = library.Mods.Any(mod => mod.Manifest.Looks is not null)
                    ? _arkiveModContext.LooksFor(library)?.Archive.Current()
                    : null;
                return await ArkiveModApplyService.DescribeAsync(
                    _arkiveModLibrary, library, snapshot, activeRevisions,
                    outsideChangedPackages: outsideNames,
                    installedGameData: installedTables, installedLooks: installedLooks);
            });
            if (Superseded()) return;
            Lap("status");
            var drafts = (await Task.Run(() => _modAuthoringWorkspace.LoadAsync())).Where(draft => draft.ModId is not null)
                .ToDictionary(draft => draft.ModId!, StringComparer.OrdinalIgnoreCase);
            if (Superseded()) return;
            var authoringPending = statuses
                .Where(status => drafts.TryGetValue(status.Mod.ModId, out var draft) && DraftHasChanges(draft, status.Mod))
                .Select(status => status.Mod.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = ShowModList(library, statuses, authoringPending, outside.Count, scan.Issues.Count, selectModId,
                onlyIfRowsChange);
            _modListPendingSelection = null;
            foreach (var issue in scan.Issues)
                Log($"Mod library file ignored: path={issue.Path}, reason={issue.Message}");
            Log($"Mod library refreshed: trigger={trigger}, mods={rows.Length}, enabled={rows.Count(row => row.Enabled)}, " +
                $"inGame={rows.Count(row => row.Status.InGame)}, ties={rows.Count(row => row.Status.TiedCount > 0)}, " +
                $"registered={scan.RegisteredModIds.Count}, " +
                $"refreshed={scan.RefreshedModIds.Count}, released={scan.Released.Count}, " +
                $"workingFiles={(snapshot.FromIndex ? "index" : "read")}.");
            // What every row shows is kept for the next launch to show at once, when it was verified in full (not with
            // install states remembered from the last launch).
            if (!snapshot.FromIndex && scan.Released.Count == 0 && !installFromMemo && _launchInstallStates is null)
                RememberModList(library, statuses, authoringPending, outside.Count);
            // Bug reports carry the session log, so record which mods and packages are in game whenever that changes.
            var state = string.Join(Environment.NewLine, rows.Select(row =>
                $"Mod: {row.Entry.Manifest.Name} {row.Entry.Manifest.Version}, enabled={row.Enabled}, " +
                $"priority={row.Entry.Priority}, inGame={row.Status.InGame}, assets={row.Entry.Manifest.Assets.Count}, " +
                $"packages={string.Join(" ", row.Entry.Manifest.PhysicalPackageNames)}"));
            if (state != _loggedModLibraryState)
            {
                _loggedModLibraryState = state;
                foreach (var line in state.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)) Log(line);
            }
            if (scan.Released.Count > 0)
            {
                await RefreshGuidedTextureModsAsync("mod file updated");
                // Done (it refreshes this list again itself unless the library is busy), so nothing is left to hand on.
                released = false;
                if (Superseded()) return;
            }
            _lastEditSnapshot = scan.Released.Count > 0 ? null : snapshot;
            // Edit mods works with the records themselves, so it is never handed edits made from the index.
            await RefreshModAuthoringAsync(snapshot: scan.Released.Count > 0 || snapshot.FromIndex ? null : snapshot);
            Lap("edit mods");
            Log($"Mod library refresh took: {string.Join(", ", stages)}.");
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            ModLibraryStatusText.Text = $"The mod library could not be read: {exception.Message}";
            Log($"Mod library refresh failed: trigger={trigger}, error={exception}");
        }
        finally
        {
            if (refresh == _modListRefreshes) _modListPendingSelection = null;
            UpdateModLibraryActionState();
        }
    }

    /// <summary>
    /// Which build of each game package is in the game, as the Mods list shows it: the package view's own when it has
    /// read the package workspace since, otherwise read again here. Null until read, and again after anything that can
    /// change it (a refresh of the package workspace asked for while it was not read: an install, Apply, a save).
    /// </summary>
    private IReadOnlyDictionary<string, GuidedTexturePackageRevision?>? _installStates;

    // Counts the times _installStates was let go, so a read that began before is not kept.
    private int _installStateGeneration;

    /// <summary>The package projects the Mods list last read the install state of (also the game folder's candidates).</summary>
    private IReadOnlyList<GuidedTexturePackageEntry> _installStateProjects = [];

    private PackageInstallStateService? _packageInstallStateService;

    private PackageInstallStateService PackageInstallStates => _packageInstallStateService ??=
        new PackageInstallStateService(_arkiveModContext) { Log = Log };

    private void ForgetInstallStates()
    {
        _installStates = null;
        _installStateGeneration++;
    }

    /// <summary>How a Mods list refresh reads the install state.</summary>
    private enum InstallStateRead
    {
        /// <summary>What the last read found while it still holds, otherwise the install-state service's checks.</summary>
        Usual,

        /// <summary>
        /// Launch's Checked step: each package's entry in the memo (PackageInstallMemo) while every file it was worked
        /// out from is unchanged, the service's checks for the rest.
        /// </summary>
        LaunchMemo,

        /// <summary>The background pass after a launch that showed remembered states: every package checked again.</summary>
        Recheck,
    }

    /// <summary>
    /// What launch showed of each package's install state, by package project record, when some of it came from the
    /// memo; the background pass checks every package again and corrects the list. Null otherwise.
    /// </summary>
    private Dictionary<string, PackageInstallSummary?>? _launchInstallStates;

    // _installStateGeneration when launch read _launchInstallStates.
    private int _launchInstallGeneration;

    // The memo being written, if any (in the background, one after another).
    private Task _installMemoSave = Task.CompletedTask;

    /// <summary>
    /// The install state the Mods list shows, and the package project records when this read them (null when the last
    /// one still holds). Reading creates the package project of any game package a working file changes that has none,
    /// as the package view does, so the same project is found for each package. With the records, the projects whose
    /// build this read proved in the game with a receipt of this Arkive (ArkiveModApplyService.ProvesInstalledByThisArkive).
    /// FromMemo says some states were remembered rather than checked now (they are only shown, see InstallStateRead);
    /// Corrected that a check again found a package other than launch showed it.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, GuidedTexturePackageRevision?> Active,
        IReadOnlyList<GuidedTexturePackageEntry>? Records, IReadOnlySet<GuidedTexturePackageEntry>? InstalledHere,
        bool FromMemo, bool Corrected)>
        InstallStatesAsync(ArkiveModEditSnapshot snapshot, string? gameDirectory, InstallStateRead how = InstallStateRead.Usual)
    {
        if (how == InstallStateRead.Usual && _installStates is { } known) return (known, null, null, false, false);
        var generation = _installStateGeneration;
        var service = PackageInstallStates;
        var root = _projectRoot;
        var version = LaunchCacheVersion;
        // Off the window's thread: hundreds of packages, each with its builds, receipts and game file looked at.
        var (read, records, projects, summaries, checks) = await Task.Run(async () =>
        {
            var read = await _guidedTexturePackageWorkspace.LoadAsync();
            // Edits made from the working-file index cannot create a project (that needs their records); a package
            // without one is created by the next full read.
            var records = await PackageInstallStateService.EnsureProjectsAsync(_guidedTexturePackageWorkspace, read, snapshot);
            var projects = PackageInstallStateService.ProjectsForWorkingFiles(records,
                snapshot.Edits.Select(edit => edit.SourcePackagePath));
            var summaries = new Dictionary<GuidedTexturePackageEntry, PackageInstallSummary?>(ReferenceEqualityComparer.Instance);
            PackageInstallChecks? checks = null;
            if (how == InstallStateRead.Usual)
            {
                foreach (var (project, state) in await service.ResolveAllAsync(projects))
                    summaries[project] = PackageInstallSummary.Of(state);
            }
            else
            {
                // The background pass checks every package at half the launch's pace; the memo only helps it notice a
                // file that changed while its package was checked.
                checks = await PackageInstallMemo.CheckAllAsync(service, projects,
                    PackageInstallMemo.Load(root, version, gameDirectory), root, useMemo: how == InstallStateRead.LaunchMemo,
                    how == InstallStateRead.Recheck ? PackageInstallStateService.Parallelism / 2 : PackageInstallStateService.Parallelism);
                foreach (var check in checks.Packages) summaries[check.Package] = check.Summary;
            }
            return (read, records, projects, summaries, checks);
        });
        var active = PackageInstallStateService.ActiveRevisionsByPhysicalName(projects, gameDirectory,
            project => summaries.GetValueOrDefault(project));
        IReadOnlySet<GuidedTexturePackageEntry> installedHere = summaries
            .Where(pair => pair.Value?.InstalledByThisArkive == true)
            .Select(pair => pair.Key).ToHashSet<GuidedTexturePackageEntry>(ReferenceEqualityComparer.Instance);
        Log($"Mods list install state read: packages={projects.Count}, installed={active.Count(pair => pair.Value is not null)}" +
            (checks is null ? "." : $", remembered={checks.FromMemo}, checkedNow={checks.Inspected}, " +
                                    $"notRemembered={checks.NotRemembered}, memoFileChecks={checks.FileChecks}."));
        var corrected = false;
        if (how == InstallStateRead.Recheck && _launchInstallStates is { } shown)
        {
            // Every state on screen is now one checked again, so nothing remembered is shown any more. When something
            // (an install, Apply) let launch's states go since, they are no longer on screen to correct.
            _launchInstallStates = null;
            foreach (var project in _launchInstallGeneration == generation ? projects : [])
            {
                if (!shown.TryGetValue(Path.GetFullPath(project.ManifestPath), out var then)) continue;
                var now = summaries.GetValueOrDefault(project);
                if (PackageInstallSummary.Same(then, now)) continue;
                corrected = true;
                Log($"Install state shown at launch corrected by the background check: package={project.Manifest.PackageAlias}, " +
                    $"record={project.ManifestPath}, shown: {PackageInstallSummary.Describe(then)}; " +
                    $"checked: {PackageInstallSummary.Describe(now)}.");
            }
        }
        if (how == InstallStateRead.LaunchMemo && checks is { FromMemo: > 0 })
        {
            _launchInstallStates = projects.ToDictionary(project => Path.GetFullPath(project.ManifestPath),
                project => summaries.GetValueOrDefault(project), StringComparer.OrdinalIgnoreCase);
            _launchInstallGeneration = generation;
        }
        if (generation == _installStateGeneration)
        {
            _installStates = active;
            _installStateProjects = projects;
            // Kept only from checks nothing overtook: no install, Apply or other operation began meanwhile.
            if (checks is not null && (how == InstallStateRead.LaunchMemo ? checks.Inspected > 0 : !BackgroundCheckShouldWait))
                SaveInstallMemo(PackageInstallMemo.Document(checks, root, version, gameDirectory));
        }
        // Projects created just now sort among the others when read from disk; the outside-change check reads them so.
        var fromMemo = checks is { FromMemo: > 0 };
        return ReferenceEquals(records, read)
            ? (active, read, installedHere, fromMemo, corrected)
            : (active, null, null, fromMemo, corrected);
    }

    /// <summary>Writes the install-state memo in the background (not in a measuring run without --save-caches).</summary>
    private void SaveInstallMemo(PackageInstallMemoDocument document)
    {
        if (!_saveLaunchCaches) return;
        var root = _projectRoot;
        lock (_launchCacheLock)
            _installMemoSave = _installMemoSave.ContinueWith(_ =>
            {
                try
                {
                    PackageInstallMemo.Save(root, document);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Log($"Install-state memo not saved: {exception.Message}");
                }
            }, TaskScheduler.Default);
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
        ModLibraryNewPackButton.IsEnabled = idle && _modLibraryRows.Length > 1;
        ModLibraryEditButton.IsEnabled = idle && hasRow;
        ModLibraryPrepareButton.IsEnabled = idle && hasRow;
        ModLibraryGameTablesButton.IsEnabled = idle;
        ModLibraryEditTablesButton.IsEnabled = idle && hasRow;
        var assetCount = ModLibraryAssetsGrid.SelectedItems.OfType<ModAssetRow>().Count();
        ModLibraryEditAssetButton.IsEnabled = idle && hasRow && assetCount == 1;
        ModLibraryPreviewButton.IsEnabled = idle;
        ModLibraryRestoreAllButton.IsEnabled = idle;
        UpdateGameUpdateActions();
        ModLibraryRestoreOtherFolderButton.IsEnabled = idle;
        ModLibraryCleanUpWorkingFilesButton.IsEnabled = idle;
        ModLibraryImportButton.IsEnabled = idle;
        ModLibraryGameFolderButton.IsEnabled = idle;
        ModLibraryRefreshButton.IsEnabled = idle;
        ModLibraryExportButton.IsEnabled = idle && hasRow;
        ModLibraryEnableButton.IsEnabled = idle && row is not null;
        var chosen = SelectedModRows();
        ModLibraryEnableButton.Header = chosen.Length > 1
            ? $"{(chosen.Any(item => !item.Enabled) ? "Enable" : "Disable")} {chosen.Length:N0} mods"
            : row?.Enabled == true ? "Disable" : "Enable";
        ModLibraryUpdateButton.Header = chosen.Length > 1 ? $"Update {chosen.Length:N0} mods for this game version" : "Update for this game version";
        ModLibraryPriorityUpButton.IsEnabled = idle && row is not null;
        ModLibraryPriorityDownButton.IsEnabled = idle && row is not null;
        ModLibraryMakeWinnerButton.IsEnabled = idle && row is not null &&
                                               (row.Status.Assets.Any(asset => asset.Competitor is not null) ||
                                                row.Status.GameData is { Overridden: > 0 } or { Tied: > 0 });
        ModLibrarySaveButton.IsEnabled = idle && hasRow && (row!.Status.ModifiedLocally || row.AuthoringPending);
        ModLibraryUpdateButton.IsEnabled = idle && hasRow;
        ModLibraryGoToPackageButton.IsEnabled = idle && row is not null;
        ModLibraryRemoveButton.IsEnabled = idle && row is not null;
        ModLibraryRemoveButton.Content = chosen.Length > 1 ? $"Delete {chosen.Length:N0} mods" : "Delete mod";
        UpdateReleaseWorkingFilesItems(row, idle, chosen.Length);
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
        _selectionOnlyChange = null;
        UpdateModLibraryActionState();
        using var busy = ShowBusy(BusyTitle(title), "Arkive is updating the mod list. Please wait until it finishes.");
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
            UpdateModLibraryActionState();
        }
    }

    /// <param name="deferWorkspace">
    /// Leave reloading the package workspace (every working file of every mod; minutes with large upscale mods) until
    /// a view that shows it is opened or mods are applied. Enabling, disabling and priorities only change which
    /// working files are selected, which nothing in the Mods list reads from that workspace.
    /// </param>
    /// <param name="changedModIds">
    /// The only mods turned on or off or moved in priority: only the working files competing with them are read again
    /// (see ArkiveModSelectionService.SynchronizeChangedAsync). Apply still synchronizes every working file.
    /// </param>
    private async Task SynchronizeModSelectionsAsync(string trigger, bool onlyIfChanged = false, bool deferWorkspace = false,
        IReadOnlyCollection<string>? changedModIds = null)
    {
        var (changes, snapshot) = changedModIds is not null && _lastEditSnapshot is { } known
            ? await ArkiveModSelectionService.SynchronizeChangedAsync(_arkiveModLibrary, _arkiveModEdits, known, changedModIds)
            : await ArkiveModSelectionService.SynchronizeWithSnapshotAsync(_arkiveModLibrary, _arkiveModEdits);
        if (changes.Count > 0)
            Log($"Mod selection synchronized: trigger={trigger}, changes={changes.Count:N0}, " +
                string.Join("; ", changes.Take(20).Select(change =>
                    $"mod={change.ModName}, kind={change.Kind}, object={change.ObjectName}, edit={change.EditId}, included={change.Included}")) +
                (changes.Count > 20 ? $"; and {changes.Count - 20:N0} more." : "."));
        if (deferWorkspace) _selectionOnlyChange = snapshot;
        if (onlyIfChanged && changes.Count == 0) return;
        if (deferWorkspace)
        {
            MarkPackageWorkspaceStale();
            return;
        }
        // Each is read again now if its view has been read, and otherwise left until a view shows it.
        await RefreshGuidedTextureModsAsync(trigger);
        await RefreshMaterialProjectsAsync(trigger);
    }

    /// <summary>
    /// Set when the last Mods-list operation only changed which mods are on or their priorities (see
    /// SynchronizeModSelectionsAsync): the working files as that left them, for the refresh that follows it.
    /// </summary>
    private ArkiveModEditSnapshot? _selectionOnlyChange;

    /// <summary>The working files as the last Mods list refresh saw them; a toggle re-reads only those it can change.</summary>
    private ArkiveModEditSnapshot? _lastEditSnapshot;

    private static string BusyTitle(string operation) => operation switch
    {
        "Enable mod" => "Enabling the mod…",
        "Disable mod" => "Disabling the mod…",
        "Turn pack on" => "Turning the pack on…",
        "Turn pack off" => "Turning the pack off…",
        "Mod priority" => "Changing the priority…",
        "Rename mod" => "Renaming the mod…",
        "Delete mod" => "Deleting the mod…",
        _ => $"{operation}…",
    };

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
        if (sender is not CheckBox { DataContext: ModLibraryRow row } box) return;
        // While launch checks the mods (or another change runs) the click does nothing, so the box shows the mod as it is.
        if (!ModLibraryIdle)
        {
            box.IsChecked = row.Enabled;
            return;
        }
        await SetModEnabledAsync(row, !row.Enabled);
    }

    private ModLibraryRow[] SelectedModRows() => ModLibraryGrid.SelectedItems.OfType<ModLibraryRow>().ToArray();

    private async void ModLibraryEnableButton_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedModRows();
        if (rows.Length > 1) await SetModsEnabledAsync(rows, rows.Any(row => !row.Enabled));
        else if (ModLibraryGrid.SelectedItem is ModLibraryRow row) await SetModEnabledAsync(row, !row.Enabled);
    }

    /// <summary>Turns the selected mods on, unless every one is on already, in which case it turns them off.</summary>
    private Task SetModsEnabledAsync(IReadOnlyList<ModLibraryRow> rows, bool enabled) =>
        RunModLibraryOperationAsync(enabled ? "Enable mods" : "Disable mods", async () =>
        {
            var ids = rows.Select(row => row.Entry.ModId).ToArray();
            var library = await _arkiveModLibrary.SetEnabledAsync(ids, enabled);
            Log($"Mods {(enabled ? "enabled" : "disabled")}: mods={string.Join(", ", ids)}.");
            await SynchronizeModSelectionsAsync(enabled ? "mods enabled" : "mods disabled", onlyIfChanged: true, deferWorkspace: true,
                changedModIds: ids);
            return enabled
                ? $"Enabled {ids.Length:N0} mods.{OverlapNotice(library.Mods, ids)} Choose Apply changes to update the game."
                : $"Disabled {ids.Length:N0} mods. Choose Apply changes to update the game.";
        });

    private Task SetModEnabledAsync(ModLibraryRow row, bool enabled) =>
        RunModLibraryOperationAsync(enabled ? "Enable mod" : "Disable mod", async () =>
        {
            var library = await _arkiveModLibrary.SetEnabledAsync(row.Entry.ModId, enabled);
            Log($"Mod {(enabled ? "enabled" : "disabled")}: mod={row.Entry.ModId}, name={row.DisplayName}.");
            await SynchronizeModSelectionsAsync(enabled ? "mod enabled" : "mod disabled", onlyIfChanged: true, deferWorkspace: true,
                changedModIds: [row.Entry.ModId]);
            return enabled
                ? $"{row.DisplayName} enabled.{OverlapNotice(library.Mods, [row.Entry.ModId])} Choose Apply changes to update the game."
                : $"{row.DisplayName} disabled. Choose Apply changes to update the game.";
        }, row.Entry.ModId);

    /// <summary>Renames a mod typed into the Mod column (F2, a slow second click or right-click, Rename).</summary>
    private void ModLibraryGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not ModLibraryRow row ||
            (e.Column != ModLibraryNameColumn && e.Column != ModLibraryAuthorColumn) ||
            FindChild<TextBox>(e.EditingElement) is not { } textBox)
            return;
        var text = textBox.Text.Trim();
        var author = e.Column == ModLibraryAuthorColumn;
        if (author ? text == (row.Entry.Manifest.Author ?? string.Empty) : text.Length == 0 || text == row.DisplayName) return;
        // The grid is still inside its edit; finish it before the list is rebuilt, or the rebuild fails and the
        // list and its buttons stay stale until another tab is opened.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            ModLibraryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
            await SaveModMetadataAsync(row,
                author ? ModMetadata(row) with { Author = text.Length == 0 ? null : text } : ModMetadata(row) with { Name = text },
                author ? "Change author" : "Rename mod");
        }, DispatcherPriority.Background);
    }

    private void ModLibraryNameBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        box.Focus();
        box.SelectAll();
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T self) return self;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindChild<T>(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
        return null;
    }

    private void ModLibraryRowRenameItem_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is not ModLibraryRow row || !ModLibraryIdle) return;
        ModLibraryGrid.ScrollIntoView(row, ModLibraryNameColumn);
        ModLibraryGrid.Focus();
        ModLibraryGrid.CurrentCell = new DataGridCellInfo(row, ModLibraryNameColumn);
        ModLibraryGrid.BeginEdit();
    }

    /// <summary>F2 renames the selected mod; Ctrl+Up and Ctrl+Down change its priority.</summary>
    private async void ModLibraryGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || ModLibraryGrid.SelectedItem is not ModLibraryRow row || !ModLibraryIdle) return;
        if (e.Key == Key.F2)
        {
            e.Handled = true;
            ModLibraryRowRenameItem_Click(sender, e);
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            await SetModPriorityAsync(row, Math.Clamp(row.Priority + (e.Key == Key.Up ? 1 : -1),
                ArkiveModLibrary.MinimumPriority, ArkiveModLibrary.MaximumPriority));
        }
    }

    /// <summary>Selects the row under the mouse, so the right-click menu acts on it.</summary>
    private void ModLibraryGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != ModLibraryGrid;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is not DataGridRow { Item: ModLibraryRow row }) continue;
            // Right-clicking one of several chosen mods acts on all of them.
            if (!ModLibraryGrid.SelectedItems.Contains(row)) ModLibraryGrid.SelectedItem = row;
            return;
        }
    }

    private void ModLibraryRowMenu_Opened(object sender, RoutedEventArgs e)
    {
        var row = ModLibraryGrid.SelectedItem as ModLibraryRow;
        var rows = SelectedModRows();
        var several = rows.Length > 1;
        var enabled = ModLibraryIdle && row is not null;
        ModLibraryRowEnableItem.Header = several
            ? $"{(rows.Any(item => !item.Enabled) ? "Enable" : "Disable")} {rows.Length:N0} mods"
            : row?.Enabled == true ? "Disable" : "Enable";
        ModLibraryRowEnableItem.IsEnabled = ModLibraryRowPriorityUpItem.IsEnabled =
            ModLibraryRowPriorityDownItem.IsEnabled = enabled;
        ModLibraryRowRenameItem.IsEnabled = ModLibraryRowAuthorItem.IsEnabled = enabled && !row!.Status.FileMissing;
        ModLibraryRowDeleteItem.IsEnabled = enabled;
        ModLibraryRowDeleteItem.Header = several ? $"Delete {rows.Length:N0} mods" : "Delete mod";
        UpdateReleaseWorkingFilesItems(row, ModLibraryIdle, rows.Length);
        // Priority, name and author belong to one mod; with several chosen only what applies to all of them shows.
        var single = several ? Visibility.Collapsed : Visibility.Visible;
        ModLibraryRowPriorityUpItem.Visibility = ModLibraryRowPriorityDownItem.Visibility =
            ModLibraryRowRenameItem.Visibility = ModLibraryRowAuthorItem.Visibility = single;
        ModLibraryRowUpdateItem.Header = several ? $"Update {rows.Length:N0} mods for this game version" : "Update for this game version";
        ModLibraryRowUpdateItem.IsEnabled = enabled && rows.All(item => !item.Status.FileMissing);
        ModLibraryRowEditUpscaleItem.Visibility = !several && row is not null && IsEditableUpscaleMod(row.Entry.Manifest)
            ? Visibility.Visible : Visibility.Collapsed;
        ModLibraryRowEditUpscaleItem.IsEnabled = enabled && !row!.Status.FileMissing && CanOpenProportionEditor;
        FillAddToPackMenu(several ? null : row);
        if (several && rows.Any(item => item.Group?.Pack is not null))
        {
            ModLibraryRowRemoveFromPackItem.Visibility = Visibility.Visible;
            ModLibraryRowRemoveFromPackItem.Header = "Remove from their packs";
        }
        ModLibraryRowAddToPackItem.IsEnabled = ModLibraryRowRemoveFromPackItem.IsEnabled = enabled;
    }

    /// <summary>Starts typing a new author into the selected mod's Author cell.</summary>
    private void ModLibraryRowAuthorItem_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is not ModLibraryRow row || !ModLibraryIdle) return;
        ModLibraryGrid.ScrollIntoView(row, ModLibraryAuthorColumn);
        ModLibraryGrid.Focus();
        ModLibraryGrid.CurrentCell = new DataGridCellInfo(row, ModLibraryAuthorColumn);
        ModLibraryGrid.BeginEdit();
    }

    /// <summary>The priority box: Enter applies a typed number, Up and Down step it, Escape puts it back.</summary>
    private void ModLibraryPriorityBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ModLibraryRow row } box) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                CommitPriorityBox(box, row);
                break;
            case Key.Escape:
                e.Handled = true;
                box.Text = row.Priority.ToString(System.Globalization.CultureInfo.CurrentCulture);
                break;
            case Key.Up or Key.Down:
                e.Handled = true;
                var current = ReadPriority(box.Text) ?? row.Priority;
                box.Text = Math.Clamp(current + (e.Key == Key.Up ? 1 : -1), ArkiveModLibrary.MinimumPriority,
                    ArkiveModLibrary.MaximumPriority).ToString(System.Globalization.CultureInfo.CurrentCulture);
                CommitPriorityBox(box, row);
                break;
        }
    }

    private void ModLibraryPriorityBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: ModLibraryRow row } box) CommitPriorityBox(box, row);
    }

    private void CommitPriorityBox(TextBox box, ModLibraryRow row)
    {
        if (ReadPriority(box.Text) is not { } priority)
        {
            box.Text = row.Priority.ToString(System.Globalization.CultureInfo.CurrentCulture);
            ModLibraryStatusText.Text =
                $"Priority must be a whole number between {ArkiveModLibrary.MinimumPriority:N0} and {ArkiveModLibrary.MaximumPriority:N0}.";
            return;
        }
        if (priority == row.Priority || !ModLibraryIdle) return;
        _ = Dispatcher.InvokeAsync(() => SetModPriorityAsync(row, priority), DispatcherPriority.Background);
    }

    private static int? ReadPriority(string text) =>
        int.TryParse(text.Trim().Replace('−', '-'), System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.CurrentCulture, out var priority) &&
        priority >= ArkiveModLibrary.MinimumPriority && priority <= ArkiveModLibrary.MaximumPriority
            ? priority : null;

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
        var priority = Math.Max(
            ArkiveModConflictResolver.Resolve(_arkiveModLibraryManifest.Mods).PriorityToWinAll(row.Entry.ModId),
            GameDataResolution.PriorityToWinAll(_arkiveModLibraryManifest.Mods, row.Entry.ModId));
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
            await SynchronizeModSelectionsAsync("mod priority changed", onlyIfChanged: true, deferWorkspace: true,
                changedModIds: [row.Entry.ModId]);
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

    /// <summary>The .arkive files among the files dragged onto the window; other files and folders are left out.</summary>
    private static IReadOnlyList<string> DroppedModFiles(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths
            ? App.ModFileArguments(paths.Where(File.Exists))
            : [];

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        // Text dragged inside a box is left to the box; files are only taken when they are mods to import.
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = ModLibraryIdle && DroppedModFiles(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        await ImportDroppedModFilesAsync(DroppedModFiles(e.Data));
    }

    /// <summary>
    /// Dropped mods are imported as with Import…, after the same question. It is asked once the drop has finished, so
    /// the folder the files came from is not held up while the question is open.
    /// </summary>
    private async Task ImportDroppedModFilesAsync(IReadOnlyList<string> paths)
    {
        await Dispatcher.Yield();
        if (paths.Count == 0) return;
        Log($"Mod files dropped on the window: {string.Join(", ", paths)}.");
        if (!ModLibraryIdle)
        {
            StatusText.Text = "Arkive is busy with the mod list. Drop the files again when it has finished.";
            return;
        }
        await ImportModFilesAsync(paths);
    }

    /// <summary>The UI tests answer the import question here instead of in a message box.</summary>
    private Func<string, bool>? AnswerImportQuestion { get; set; }

    private async Task ImportModFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !ModLibraryIdle) return;
        ModifiedUpksTab.IsSelected = true;
        ModLibraryTab.IsSelected = true;
        // Asked before anything is imported, so No leaves the library as it was.
        var names = paths.Select(path => Path.GetFileNameWithoutExtension(path)).ToArray();
        var question =
            (paths.Count == 1 ? $"Import {names[0]}?" : $"Import these {paths.Count:N0} mods?\n\n" +
                string.Join("\n", names.Take(12).Select(name => "  • " + name)) +
                (names.Length > 12 ? $"\n  …and {names.Length - 12:N0} more" : string.Empty)) +
            "\n\nNew mods are turned on. Nothing changes in the game until you choose Apply changes, " +
            "and you can turn them off or set priorities first.";
        var yes = AnswerImportQuestion?.Invoke(question) ?? MessageBox.Show(this, question,
            "Import .arkive", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
        if (!yes)
        {
            Log($"Mod import cancelled at the question: {string.Join(", ", paths)}.");
            return;
        }
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
            // An updated mod keeps whether it was on.
            foreach (var result in imported.Where(result => !result.UpdatedExistingMod && !result.Entry.Enabled))
                await _arkiveModLibrary.SetEnabledAsync(result.Entry.ModId, true);
            // The package workspace is reloaded when a page needs it (as after turning mods on), instead of holding
            // the window for half a minute on a large library; the list below still checks the game files again.
            await SynchronizeModSelectionsAsync("mods imported", deferWorkspace: true);
            _selectionOnlyChange = null;
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
        await SaveModMetadataAsync(row, metadata, "Save mod details");
    }

    private static ArkiveModMetadata ModMetadata(ModLibraryRow row) => new(
        row.Entry.Manifest.Name, row.Entry.Manifest.Version, row.Entry.Manifest.Author,
        row.Entry.Manifest.Description, row.Entry.Manifest.Website, row.Entry.Manifest.Tags);

    /// <summary>Writes new details into the mod's .arkive file and any open draft of it.</summary>
    private Task SaveModMetadataAsync(ModLibraryRow row, ArkiveModMetadata metadata, string title) =>
        RunModLibraryOperationAsync(title, async () =>
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

    /// <summary>Updates the selected mods for the installed game version (More actions, or right-click for several).</summary>
    private async void ModLibraryUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var mods = SelectedModRows().Where(row => !row.Status.FileMissing).Select(row => row.Entry).ToArray();
        if (!ModLibraryIdle || mods.Length == 0) return;
        await RebaseModsAsync(mods, 0, "Update for this game version",
            mods.Length == 1 ? $"Update '{mods[0].Manifest.Name}' for the installed Lost Ark version?"
                             : $"Update {mods.Length:N0} mods for the installed Lost Ark version?",
            "Arkive checks these mods against your current game files.");
    }

    private async void ModLibraryGoToPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is not ModLibraryRow row) return;
        await EnsurePackageWorkspaceAsync("package view opened");
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
        var rows = SelectedModRows();
        if (rows.Length == 0) return;
        // Whether a mod is still in the game decides below whether it may be deleted, and until the background pass
        // has checked them the rows may show install states remembered from the last launch: they are checked now.
        if (_launchInstallStates is not null)
        {
            var chosen = rows.Select(row => row.Entry.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _modLibraryBusy = true;
            UpdateModLibraryActionState();
            try
            {
                await RefreshArkiveModsAsync("install state checked before deleting", installRead: InstallStateRead.Recheck);
            }
            finally
            {
                _modLibraryBusy = false;
                UpdateModLibraryActionState();
            }
            rows = _modLibraryRows.Where(row => chosen.Contains(row.Entry.ModId)).ToArray();
            if (_launchInstallStates is not null || rows.Length == 0)
            {
                MessageBox.Show(this, "Arkive is still checking which mods are in the game. Try again in a moment.",
                    "Delete mod", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        // Deleting a mod does not take it out of the game, so one still installed would be left there with no mod
        // to restore it by: it is disabled and applied first.
        var inGame = rows.Where(row => row.HasActiveAssets).ToArray();
        if (inGame.Length > 0)
        {
            MessageBox.Show(this,
                (inGame.Length == 1 ? "This mod is" : $"These {inGame.Length:N0} mods are") + " still in the game, so " +
                (inGame.Length == 1 ? "it" : "they") + " can't be deleted yet:\n\n" + ModNameList(inGame) + "\n\n" +
                "1. Disable " + (inGame.Length == 1 ? "it" : "them") + (inGame.All(row => !row.Enabled) ? " (already done)" : string.Empty) + ".\n" +
                "2. Choose Apply changes, so Arkive restores or rebuilds the game files " + (inGame.Length == 1 ? "it changes" : "they change") + ".\n" +
                "3. Delete " + (inGame.Length == 1 ? "it" : "them") + " again.",
                "Delete mod", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var unsaved = rows.Where(row => row.Status.ModifiedLocally || row.AuthoringPending).ToArray();
        var one = rows.Length == 1;
        if (MessageBox.Show(this,
                (one ? $"Delete '{rows[0].DisplayName}'?" : $"Delete these {rows.Length:N0} mods?\n\n" + ModNameList(rows)) + "\n\n" +
                (one ? $"This deletes {rows[0].FilePath}." : "This deletes their .arkive files from the mods folder.") +
                " Use Share / export first if you want to keep a copy.\n\n" +
                (one ? "Its" : "Their") + " working files are deleted too: the edited textures, meshes, materials and effects " +
                (one ? "it was" : "they were") + " made from, and " + (one ? "its" : "their") + " draft. Working files another " +
                "mod or draft also uses are kept. Changes to them that were not saved into the mod are lost." +
                (unsaved.Length == 0 ? string.Empty
                    : "\n\n" + (one ? "This mod has" : unsaved.Length == rows.Length ? "These mods have" : "Some of these mods have") +
                      " unsaved changes, which will be lost" + (one || unsaved.Length == rows.Length ? "." : ":\n" + ModNameList(unsaved))),
                "Delete mod", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) !=
            MessageBoxResult.Yes) return;
        await RunModLibraryOperationAsync("Delete mod", async () =>
        {
            foreach (var row in rows)
            {
                var removal = await _arkiveModAuthoring.RemoveModAsync(row.Entry.ModId);
                if (_authoringDraft?.ModId == row.Entry.ModId) _authoringDraft = null;
                TryChangePacks(() => ModPacks.RemoveMod(row.Entry.ModId));
                Log($"Mod deleted: mod={row.Entry.ModId}, name={row.DisplayName}, origin={row.Entry.Origin}, file={row.FilePath}, " +
                    $"deletedWorkingFiles={removal.DeletedEditIds.Count}, keptWorkingFiles={removal.KeptEditIds.Count}, " +
                    $"deletedExports={removal.DeletedExports.Count}, deletedDrafts={removal.DeletedDraftIds.Count}, " +
                    $"deletedUnpacked={removal.DeletedUnpackedFolders.Count}.");
            }
            await SynchronizeModSelectionsAsync("mod deleted");
            return rows.Length == 1 ? $"Deleted {rows[0].DisplayName}." : $"Deleted {rows.Length:N0} mods.";
        });
    }

    /// <summary>The mods' names one per line, the first ten and how many more.</summary>
    private static string ModNameList(IReadOnlyList<ModLibraryRow> rows) =>
        string.Join("\n", rows.Take(10).Select(row => "• " + row.DisplayName)) +
        (rows.Count > 10 ? $"\n…and {rows.Count - 10:N0} more" : string.Empty);

    private async void ModLibraryPreviewButton_Click(object sender, RoutedEventArgs e) =>
        await RunModApplyAsync(previewOnly: true);

    private async void ModLibraryApplyButton_Click(object sender, RoutedEventArgs e) =>
        await RunModApplyAsync(previewOnly: false);

    private IReadOnlyList<ArkiveModOutsideChange> _outsideChanges = [];
    private bool _outsideChangesChecked;

    /// <summary>
    /// Puts back game files that another Arkive data folder changed, from that folder's verified backups. Only files
    /// this Arkive did not install and whose original is found by hash are touched; each replaced file is kept.
    /// </summary>
    private async void ModLibraryRestoreOtherFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ModLibraryIdle) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the other Arkive data folder (the one holding its backups folder)",
        };
        if (dialog.ShowDialog(this) != true) return;
        _modLibraryBusy = true;
        UpdateModLibraryActionState();
        try
        {
            ModLibraryStatusText.Text = "Looking for verified originals in that folder…";
            var plan = await _arkiveModApply.PlanRestoreFromOtherFolderAsync(dialog.FolderName);
            Log($"Restore from another Arkive folder planned: folder={plan.OtherDataFolder}, restorable={plan.Items.Count}, " +
                $"unresolved={plan.Unresolved.Count}.");
            if (plan.Items.Count == 0)
            {
                var none = plan.Unresolved.Count == 0
                    ? "No game file needs restoring: every file your mods target is either the original or installed by this Arkive."
                    : $"That folder has no verified originals for the {plan.Unresolved.Count:N0} changed game file(s) " +
                      $"({string.Join(", ", plan.Unresolved.Select(change => change.PackageAlias))}). " +
                      "Choose the data folder of the Arkive that installed them, or use Steam's Verify integrity of game files.";
                ModLibraryStatusText.Text = none;
                MessageBox.Show(this, none, "Restore from another Arkive folder", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var list = string.Join("\n", plan.Items.Select(item => $"  • {item.PackageAlias} ({item.PhysicalPackageName})"));
            var rest = plan.Unresolved.Count == 0 ? "" :
                $"\n\n{plan.Unresolved.Count:N0} other changed file(s) have no original in that folder and are left as they are.";
            if (MessageBox.Show(this,
                    $"Restore {plan.Items.Count:N0} game file(s) to their originals from {plan.OtherDataFolder}?\n\n{list}{rest}\n\n" +
                    "Each original is checked by its SHA-256 first, and the files being replaced are kept in Arkive's backups folder. " +
                    "Lost Ark must be closed.",
                    "Restore from another Arkive folder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            var result = await _arkiveModApply.RestoreFromOtherFolderAsync(plan);
            ModLibraryStatusText.Text = $"Restored {result.Restored.Count:N0} game file(s) to their originals. " +
                                        $"The replaced files are kept in {result.SafetyCopyDirectory}. Apply changes to install your enabled mods.";
            Log($"Restored from another Arkive folder: files={string.Join(", ", result.Restored.Select(item => item.PhysicalPackageName))}, " +
                $"safety copies={result.SafetyCopyDirectory}.");
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            ModLibraryStatusText.Text = exception is ArkiveModRefusalException refusal
                ? refusal.Message[(refusal.Code.Length + 2)..] : exception.Message;
            Log($"Restore from another Arkive folder failed: {exception}");
        }
        finally
        {
            _modLibraryBusy = false;
            UpdateModLibraryActionState();
            await RefreshArkiveModsAsync("restore from another folder");
        }
    }

    private async void ModLibraryRestoreAllButton_Click(object sender, RoutedEventArgs e)
    {
        // Restoring every active package works from the package view's rows, which launch no longer reads.
        await EnsurePackageWorkspaceAsync("restore all requested");
        var tables = _arkiveModContext.GameDataFor(_arkiveModLibraryManifest);
        var tableCells = tables is null ? 0 : await Task.Run(() => tables.Archive.Current().Edits.Count);
        var looks = _arkiveModContext.LooksFor(_arkiveModLibraryManifest);
        var lookSlots = looks is null ? 0 : await Task.Run(() => looks.Archive.Current().Count);
        if (tableCells == 0 && lookSlots == 0 || _guidedPackageRows.Any(HasActiveGuidedDeployment))
            await RunGuidedQuickDeploymentAsync(restoreAll: true);
        if (lookSlots > 0) await RestoreItemLooksAsync(looks!, lookSlots);
        if (tableCells > 0) await RestoreGameTablesAsync(tables!, tableCells);
        await RefreshArkiveModsAsync("restore all requested");
    }

    // Takes the outfit mods' item look changes out of data4.lpk, so the game's own outfits get their own materials again.
    private async Task RestoreItemLooksAsync(OutfitLookModService looks, int slots)
    {
        if (_guidedTextureModInProgress || _modSetBusy) return;
        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        try
        {
            using var busy = ShowBusy("Restoring the game's item looks…", "Arkive is putting the game's own data4.lpk back. Please wait until it finishes.");
            await Task.Run(() => looks.RestoreAsync());
            StatusText.Text = "The game's own item looks are back in data4.lpk.";
            Log($"Item looks restored: {slots} slot change(s) removed from {looks.Archive.ArchivePath}.");
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            Log($"Restoring the item looks failed: {exception}");
            MessageBox.Show(this, exception.Message, "Restore item looks", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
        }
    }

    // Takes the Game Data mods' cells out of data2.lpk; the Language tab's choice stays.
    private async Task RestoreGameTablesAsync(GameDataModService tables, int cells)
    {
        if (_guidedTextureModInProgress || _modSetBusy) return;
        if (MessageBox.Show(this,
                $"Put back the game's own tables in data2.lpk? This removes {cells:N0} table cell change(s) made by Game Data mods. " +
                "A language chosen in the Language tab stays.\n\nClose Lost Ark before continuing.",
                "Restore game tables", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        try
        {
            using var busy = ShowBusy("Restoring the game's tables…",
                "Arkive is writing the game's own tables back into data2.lpk (about 1.4 GB). Please wait until it finishes.");
            await Task.Run(() => tables.RestoreAsync());
            StatusText.Text = "The game's own tables are back in data2.lpk.";
            Log($"Game tables restored: {cells} cell(s) removed from {tables.Archive.ArchivePath}.");
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            Log($"Restoring the game tables failed: {exception}");
            MessageBox.Show(this, exception.Message, "Restore game tables", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
        }
    }

    private async Task RunModApplyAsync(bool previewOnly, string? previewModId = null)
    {
        if (!ModLibraryIdle) return;
        _modLibraryBusy = true;
        _guidedTextureModInProgress = true;
        _lastModApplyPlan = null;
        UpdateGuidedTextureModActionState();
        var changedGame = false;
        try
        {
            ModLibraryStatusText.Text = "Checking enabled mods, priorities, and installed packages...";
            ModLibraryProgressBar.IsIndeterminate = true;
            var clock = Stopwatch.StartNew();
            // A package workspace reload left pending by enabling or disabling mods is not needed here: the plan reads
            // the library, working files and package projects itself, and the workspace is reloaded after applying.
            // Before Apply it cost 30-50 s, with the working files no longer in memory.
            await EnsureGamePackagesDirectoryAsync();
            // What reading the package workspace at launch used to settle first (older exports taken in, one working
            // copy per asset outside mods included, a project for every package), now done when it is first needed.
            await PreparePackageWorkspaceOnceAsync();
            // Without a save in between, the plan reuses the working files read to look for unsaved changes.
            var unsavedChecked = previewOnly ? null : await SaveEnabledModChangesAsync();
            var modsSaved = !previewOnly && unsavedChecked is null;
            var planStarted = clock.Elapsed;
            var plan = previewModId is not null ? await _arkiveModApply.PreviewModAsync(previewModId)
                : unsavedChecked is not null ? await _arkiveModApply.PlanAsync(unsavedChecked)
                : await _arkiveModApply.PlanAsync();
            var planSeconds = (clock.Elapsed - planStarted).TotalSeconds;
            ModLibraryProgressBar.IsIndeterminate = false;
            _lastModApplyPlan = plan;
            ModLibraryPlanGrid.ItemsSource = plan.Packages.Select(package => new ModPlanRow(package)).ToArray();
            ModLibraryPlanTab.IsSelected = true;
            // A full plan names every enabled mod the game update changed; a one-mod preview speaks only for that mod.
            if (previewModId is null) _modsNeedingGameUpdate.Clear();
            else _modsNeedingGameUpdate.Remove(previewModId);
            foreach (var modId in plan.ModsNeedingClientUpdate)
                if (_arkiveModLibraryManifest.Find(modId) is { } mod)
                    _modsNeedingGameUpdate[mod.ModId] = mod.FileSha256;
            // A plan that needs an answer asks first and the Mods list is refreshed after (3-5 s), unless mods were
            // just saved: then the list behind the question shows them as saved.
            var asks = plan.CanApply && plan.HasChanges && !previewOnly;
            if (!asks || modsSaved) await RefreshArkiveModsAsync("apply plan reviewed");
            Log($"Mod apply plan: canApply={plan.CanApply}, changes={plan.HasChanges}, packages={plan.Packages.Count}, " +
                $"summary={plan.Summary}, problems={string.Join(" | ", plan.BlockingProblems)}, " +
                $"warnings={string.Join(" | ", plan.Warnings)}, took={clock.Elapsed.TotalSeconds:N1}s (plan {planSeconds:N1}s).");
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
            var overlaps = OverlapRows().Where(overlap => !overlap.Tied).ToArray();
            var overlapLines = overlaps.Length == 0 ? string.Empty :
                "\n\nThese enabled mods change the same assets; only the winner's version goes into the game:\n" +
                string.Join("\n", overlaps.Take(5).Select(overlap =>
                    $"  • {overlap.Winner.SentenceLabel} wins over {overlap.Other.Label} on {overlap.SharedAssets.Count:N0} asset(s)")) +
                (overlaps.Length > 5 ? $"\n  • and {overlaps.Length - 5:N0} more" : string.Empty);
            if (MessageBox.Show(this,
                    $"Apply mod changes?\n\n{string.Join("\n", lines)}{overlapLines}\n\n{plan.Summary}\n\n" +
                    "Installed packages are restored first, every rebuilt package passes Arkive's native validation, and all installs happen in one transaction that rolls back on failure. " +
                    "Close Lost Ark, its launcher, and Easy Anti-Cheat before continuing.",
                    "Apply mod changes", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) !=
                MessageBoxResult.Yes)
            {
                if (!modsSaved)
                {
                    await RefreshArkiveModsAsync("apply plan reviewed");
                    ModLibraryStatusText.Text = plan.Summary;
                }
                return;
            }

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
            Log($"Mod apply timings: {string.Join(" | ", result.Timings)}");
            foreach (var detail in result.ErrorDetails) Log($"Mod apply error details: {detail}");
            if (result.Succeeded)
            {
                ModLibraryStatusText.Text = "Removing package builds the rebuilt packages no longer need…";
                await CleanUpAfterApplyAsync(result);
            }
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
                // Read again now where their views have been read; otherwise the install state is read again for the
                // Mods list and the views when shown.
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
