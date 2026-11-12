using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Arkive.Core.Diagnostics;
using Arkive.Core.Mods;
using Arkive.Core.Tweaks;

namespace Arkive.App;

/// <summary>
/// Launch in two moments. Ready: the window is up and the Mods list shows what Arkive last verified, remembered in the
/// data folder's cache (ArkiveModListMemory); the user can browse, search, select and read. Checked: the working files of
/// enabled mods (from the working-file index, each checked unchanged or read again), the install state of installed
/// packages and the game files have been checked again off the window's thread, and the verified list replaces the
/// remembered one; only then can anything that writes start. A background pass then reads every working file once, at
/// low priority, to bring the index, the disabled mods' rows and the remembered list up to date.
/// </summary>
public partial class MainWindow
{
    /// <summary>A remembered list was shown at launch, so the startup progress window is not needed.</summary>
    private bool _readyFromMemory;

    /// <summary>The working-file index and remembered list are saved (not in a measuring run without --save-caches).</summary>
    private bool _saveLaunchCaches;

    private readonly CancellationTokenSource _backgroundCheckCancellation = new();
    private Task? _backgroundCheck;

    /// <summary>Set only by the window tests: how long launch waits before it checks the mods.</summary>
    internal static TimeSpan ModsCheckDelayForTests { get; set; }

    private static readonly TimeSpan BackgroundCheckPollTime = TimeSpan.FromSeconds(1);
    private const int BackgroundCheckAttempts = 3;

    /// <summary>Arkive's version and build, which the launch caches are keyed on.</summary>
    private static string LaunchCacheVersion
    {
        get
        {
            var (version, commit) = ProcessCounters.ArkiveVersion();
            return commit is null ? version : $"{version}+{commit}";
        }
    }

    /// <summary>
    /// Shows the rows of <paramref name="statuses"/> in the Mods list with its summary lines, keeping the selection.
    /// Used for the remembered list at launch and for every verified list, so both show the same way.
    /// </summary>
    /// <param name="onlyIfChanged">
    /// Leave the list as it is (selection, scrolling, open groups) when every row and summary would read the same.
    /// </param>
    private ModLibraryRow[] ShowModList(ArkiveModLibraryManifest library, IReadOnlyList<ArkiveModStatus> statuses,
        IReadOnlySet<string> authoringPending, int outsideCount, int issueCount, string? selectModId, bool onlyIfChanged = false)
    {
        // The Tweaks tab installs and removes its own mod; it isn't one to manage here.
        var rows = statuses
            .Where(status => !TweakModService.IsTweakMod(status.Mod))
            .Select(status => new ModLibraryRow(status, _arkiveModLibrary.GetModFilePath(status.Mod))
            {
                AuthoringPending = authoringPending.Contains(status.Mod.ModId),
                NeedsGameUpdate = _modsNeedingGameUpdate.GetValueOrDefault(status.Mod.ModId) == status.Mod.FileSha256,
            })
            .OrderByDescending(row => row.Enabled)
            .ThenByDescending(row => row.Priority)
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var signature = ModListSignature(rows, outsideCount, issueCount);
        if (onlyIfChanged && signature == _shownModListSignature && selectModId is null)
        {
            Log("Mod library refresh changed no row; the list is left as it is.");
            return rows;
        }
        _shownModListSignature = signature;
        var previous = selectModId ?? (ModLibraryGrid.SelectedItem as ModLibraryRow)?.Entry.ModId;
        // Mods chosen together stay chosen when the list is rebuilt after acting on them.
        var previousGroup = SelectedModRows().Select(row => row.Entry.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _modLibraryRows = rows;
        var view = GroupModRows(rows, ArkiveModConflictResolver.Resolve(library.Mods));
        ModLibraryGrid.ItemsSource = view;
        // A mod just imported, created or renamed is shown even when the search would hide it.
        if (selectModId is not null && rows.FirstOrDefault(row => row.Entry.ModId.Equals(
                selectModId, StringComparison.OrdinalIgnoreCase)) is { } wanted && !ShowsInModSearch(wanted))
            ModLibrarySearchTextBox.Clear();
        ShowModOverlapWarning();
        ShowModSearchCount(view.Count);
        // Only mods the search box shows can be chosen.
        var shown = view.Cast<ModLibraryRow>().ToArray();
        var selected = shown.FirstOrDefault(row => row.Entry.ModId.Equals(
                           previous, StringComparison.OrdinalIgnoreCase)) ?? shown.FirstOrDefault();
        ModLibraryGrid.SelectedItem = selected;
        if (previousGroup.Count > 1 && selected is not null && previousGroup.Contains(selected.Entry.ModId))
            foreach (var row in shown.Where(row => row != selected && previousGroup.Contains(row.Entry.ModId)))
                ModLibraryGrid.SelectedItems.Add(row);
        if (selected is not null) ModLibraryGrid.ScrollIntoView(selected);
        ApplySelectedModLibraryRow();

        var enabled = rows.Count(row => row.Enabled);
        var inGame = rows.Count(row => row.Status.InGame);
        var ties = rows.Count(row => row.Status.TiedCount > 0);
        var pending = rows.Count(row => row.Enabled && !row.Status.InGame &&
                                        row.Status.WinningCount > 0 && row.Status.TiedCount == 0);
        var disabledActive = rows.Count(row => !row.Enabled && row.HasActiveAssets);
        var unsaved = rows.Count(row => row.Enabled && (row.Status.ModifiedLocally || row.AuthoringPending));
        ModLibraryPendingText.Text = rows.Length == 0 ? string.Empty : outsideCount > 0
            ? $"{outsideCount:N0} game file(s) were changed outside this Arkive (another Arkive data folder, another tool or a game patch). " +
              "Restore them with More actions → Restore from another Arkive folder… or Steam's Verify integrity before applying."
            : ties > 0
            ? $"{ties:N0} conflict(s) need a choice before applying. Select a mod and review Conflicts."
            : pending + disabledActive + unsaved > 0
                ? "Changes pending · Apply changes will save edits, review the plan, and update the game."
                : "Applied selection is up to date.";
        ModLibraryStatusText.Text = rows.Length == 0
            ? "No mods yet. Choose New mod to create one, or Import to add a shared .arkive file."
            : $"{rows.Length:N0} mod(s), {enabled:N0} enabled, {inGame:N0} fully in game. " +
              (ties > 0 ? $"{ties:N0} mod(s) have tied priorities. " : string.Empty) +
              (pending + disabledActive > 0 ? "Apply changes to update the game. " : "The game matches the enabled mods. ") +
              (issueCount > 0 ? $"{issueCount:N0} file(s) in the mods folder are not valid .arkive mods (see Diagnostics)." : string.Empty);
        return rows;
    }

    /// <summary>What the Mods list shows now, as ModListSignature gives it; null before it first shows.</summary>
    private string? _shownModListSignature;

    /// <summary>Everything a row and its assets show, and the counts the summary lines come from.</summary>
    private static string ModListSignature(IEnumerable<ModLibraryRow> rows, int outsideCount, int issueCount) =>
        $"{outsideCount}|{issueCount}\n" + string.Join("\n", rows.Select(row =>
            $"{row.Entry.ModId}|{row.Entry.FileSha256}|{row.Enabled}|{row.Priority}|{row.State}|{row.Conflicts}|" +
            $"{row.AuthoringPending}|{row.NeedsGameUpdate}|{row.HasActiveAssets}|{row.Status.FileMissing}|{row.Status.InGame}|" +
            $"{row.Status.OutsideChange}|{row.Status.ModifiedLocally}|{row.Status.GameData}|{row.Status.Looks}|" +
            string.Join(";", row.Status.Assets.Select(asset =>
                $"{asset.Asset.AssetId},{asset.Outcome},{asset.Competitor?.ModId},{asset.Competitor?.Priority},{asset.Setup}," +
                $"{asset.InGame},{asset.ModifiedLocally},{asset.Edit?.ManifestPath},{asset.Edit?.Included}"))));

    /// <summary>
    /// Ready: shows the Mods list as Arkive last verified it, when what was remembered was made from this very library
    /// file by this version of Arkive. Returns false (nothing shown) otherwise.
    /// </summary>
    private async Task<bool> ShowRememberedModListAsync()
    {
        try
        {
            var library = _startupScan?.Library ?? await Task.Run(() => _arkiveModLibrary.LoadAsync());
            var sha = _arkiveModLibrary.Sha256Of(library);
            var root = _projectRoot;
            var recalled = await Task.Run(() => ArkiveModListMemory.Recall(
                ArkiveModListMemory.Load(root, LaunchCacheVersion), library, sha));
            if (recalled is null)
            {
                Log("Remembered Mods list not used: there is none, or the library, the working-file index format or Arkive changed since.");
                return false;
            }
            _arkiveModLibraryManifest = library;
            ShowModList(library, recalled.Statuses, recalled.AuthoringPending, recalled.OutsideChangeCount,
                _startupScan?.Issues.Count ?? 0, null);
            UpdateModLibraryActionState();
            Log($"Remembered Mods list shown: mods={recalled.Statuses.Count}, outsideChanges={recalled.OutsideChangeCount}.");
            return true;
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            Log($"Remembered Mods list not shown: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checked: the working files of enabled mods, from the working-file index with each one checked unchanged or read
    /// again (every store is read when the index is missing or does not know one); their check boxes set as their mods'
    /// selection says, rewriting only records read again; then the Mods list as every refresh builds it, with the install
    /// state and the outside-change check, off the window's thread.
    /// </summary>
    private async Task CheckModsAsync()
    {
        // Window tests hold the check back to see the remembered list and the closed actions before it ends.
        if (ModsCheckDelayForTests > TimeSpan.Zero) await Task.Delay(ModsCheckDelayForTests);
        var scan = _startupScan;
        _startupScan = null;
        try
        {
            var library = scan?.Library ?? await Task.Run(() => _arkiveModLibrary.LoadAsync());
            var sha = _arkiveModLibrary.Sha256Of(library);
            var enabled = library.Mods.Where(mod => mod.Enabled).SelectMany(mod => mod.Bindings)
                .Select(binding => binding.EditId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bound = library.Mods.SelectMany(mod => mod.Bindings).Select(binding => binding.EditId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var root = _projectRoot;
            var clock = Stopwatch.StartNew();
            var io = ProcessCounters.Sample();
            var (snapshot, made, changes) = await Task.Run(async () =>
            {
                var made = await WorkingFileIndex.SnapshotAsync(WorkingFileIndex.Load(root, LaunchCacheVersion),
                    _arkiveModEdits, enabled, bound, root, sha);
                ArkiveModSelectionResult sync;
                if (!made.FullRead)
                    sync = await ArkiveModSelectionService.SynchronizeSomeAsync(library, _arkiveModEdits, made.Snapshot, enabled);
                // With every store just read, as launch did before: the full synchronisation reads them again only
                // when there is something to change.
                else if (ArkiveModSelectionService.CountPending(library, made.Snapshot) > 0)
                    sync = await ArkiveModSelectionService.SynchronizeWithSnapshotAsync(_arkiveModLibrary, _arkiveModEdits);
                else sync = new ArkiveModSelectionResult([], made.Snapshot);
                return (sync.Snapshot, made, sync.Changes);
            });
            foreach (var change in changes)
                Log($"Mod selection synchronized at startup: mod={change.ModName}, object={change.ObjectName}, " +
                    $"edit={change.EditId}, included={change.Included}.");
            Log($"Working files checked for launch: source={(made.FullRead ? "full read" : "index")}, " +
                $"edits={snapshot.Edits.Count}, checked={enabled.Count}, unchanged={made.Verified}, readAgain={made.ReadAgain}, " +
                $"gone={made.Gone}, fileChecks={made.FileChecks}, seconds={clock.Elapsed.TotalSeconds:N2}, " +
                $"fileOps={ProcessCounters.Sample().OtherOperations - io.OtherOperations}, " +
                $"readMB={(ProcessCounters.Sample().ReadBytes - io.ReadBytes) / 1048576d:N1}" +
                (made.FallbackReason is null ? "." : $", reason={made.FallbackReason}."));
            // The working files as this left them, for the Mods list that follows, so launch reads them once.
            _selectionOnlyChange = snapshot;
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            Log($"Working files could not be checked for launch; the Mods list reads them all: {exception}");
            _selectionOnlyChange = null;
        }
        // A scan that changed the library is made again, as the refresh after a change always did.
        var unchanged = scan is { RegisteredModIds.Count: 0, RefreshedModIds.Count: 0, Released.Count: 0 } ? scan : null;
        await RefreshArkiveModsAsync("workspace restore", knownScan: unchanged);
    }

    /// <summary>Ends the checking state: the note goes and every action is available again.</summary>
    private void FinishModsCheck()
    {
        _modsChecking = false;
        ModLibraryCheckingText.Visibility = Visibility.Collapsed;
        UpdateModLibraryActionState();
    }

    /// <summary>Records a launch moment: the session log line, the measuring run's mark and its Mods tab dump.</summary>
    private void ReachLaunchMoment(string id, StartupSteps steps, Stopwatch startup, string? note = null)
    {
        steps.Recorder.Mark(id, note);
        Log(id switch
        {
            "ready" => $"Startup ready in {startup.Elapsed.TotalSeconds:N1}s" + (note is null ? "." : $" ({note})."),
            "checked" => $"Startup checked in {startup.Elapsed.TotalSeconds:N1}s.",
            _ => $"Startup {id} in {startup.Elapsed.TotalSeconds:N1}s.",
        });
        if (StartupMeasurement.Current is { DumpModsListPath: { } path } options && options.DumpAt == id)
            WriteModsListDump(path, _projectRoot);
    }

    /// <summary>Starts the background pass once launch has checked the mods (see RunBackgroundCheckAsync).</summary>
    private void StartBackgroundCheck() => _backgroundCheck ??= RunBackgroundCheckAsync(_backgroundCheckCancellation.Token);

    /// <summary>
    /// After Checked, at low priority: reads every working file once (waiting before each store while the user runs an
    /// operation), sets any check box the mods' selection says differently (with the full synchronisation, which reads
    /// the records again itself), and lets the Mods list refresh with what was read when that changes a row. The read
    /// also brings the working-file index up to date (OnWorkingFilesRead), and the refresh saves the remembered list. A
    /// pass overtaken by a change the user made is begun again, up to three times. Stops when Arkive closes.
    /// </summary>
    private async Task RunBackgroundCheckAsync(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            for (var attempt = 1; attempt <= BackgroundCheckAttempts; attempt++)
            {
                await WaitForBackgroundCheckTurnAsync(token);
                var refreshes = _modListRefreshes;
                var snapshot = await Task.Run(() => ArkiveModEditSnapshot.LoadAsync(_arkiveModEdits, token,
                    WaitForBackgroundCheckTurnAsync), token);
                await WaitForBackgroundCheckTurnAsync(token);
                if (refreshes != _modListRefreshes)
                {
                    Log($"Background check of the working files overtaken by a change; reading again (attempt {attempt}).");
                    continue;
                }
                var library = await Task.Run(() => _arkiveModLibrary.LoadAsync(token), token);
                if (ArkiveModSelectionService.CountPending(library, snapshot) > 0)
                {
                    // Writes records, so it runs as an operation: nothing else starts meanwhile. Nothing is awaited
                    // between the wait and marking the library busy, so no operation can start in between.
                    await WaitForBackgroundCheckTurnAsync(token);
                    if (refreshes != _modListRefreshes) continue;
                    _modLibraryBusy = true;
                    UpdateModLibraryActionState();
                    try
                    {
                        var sync = await Task.Run(() => ArkiveModSelectionService.SynchronizeWithSnapshotAsync(
                            _arkiveModLibrary, _arkiveModEdits, token), token);
                        foreach (var change in sync.Changes)
                            Log($"Mod selection synchronized by the background check: mod={change.ModName}, " +
                                $"object={change.ObjectName}, edit={change.EditId}, included={change.Included}.");
                        snapshot = sync.Snapshot;
                    }
                    finally
                    {
                        _modLibraryBusy = false;
                        UpdateModLibraryActionState();
                    }
                }
                if (refreshes != _modListRefreshes) continue;
                _selectionOnlyChange = snapshot;
                await RefreshArkiveModsAsync("background check", onlyIfRowsChange: true);
                if (refreshes + 1 != _modListRefreshes)
                {
                    Log($"Background check of the working files overtaken by a change; reading again (attempt {attempt}).");
                    continue;
                }
                Log($"Background check of the working files finished in {clock.Elapsed.TotalSeconds:N1}s: edits={snapshot.Edits.Count}.");
                await WaitForLaunchCachesAsync();
                return;
            }
            Log("Background check of the working files stopped: changes kept overtaking it; the next refresh reads every working file.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            Log($"Background check of the working files failed: {exception.Message}");
        }
    }

    // Waits while the user runs an operation, so the background pass never competes with one.
    private async Task WaitForBackgroundCheckTurnAsync(CancellationToken token)
    {
        while (await Dispatcher.InvokeAsync(() => BackgroundCheckShouldWait, DispatcherPriority.Background, token))
            await Task.Delay(BackgroundCheckPollTime, token);
    }

    private bool BackgroundCheckShouldWait =>
        !ModLibraryIdle || _skeletalMeshEditableWorkspaceBusy || _skeletalMeshDeploymentBusy || _proportionBusy ||
        _workshopBusy || _outfitSwapBusy || _staticMeshWorkspaceBusy || ViewsLoading;

    /// <summary>Called as the window closes: stops the background pass between steps.</summary>
    private void StopBackgroundCheck()
    {
        _backgroundCheckCancellation.Cancel();
        ArkiveModEditSnapshot.FullReadCompleted -= OnWorkingFilesRead;
    }

    // The newest full read waiting to be indexed, and the update running, if any.
    private readonly object _launchCacheLock = new();
    private (ArkiveModEditSnapshot Snapshot, DateTime ReadStartUtc)? _pendingIndexRead;
    private Task _indexUpdate = Task.CompletedTask;
    private Task _rememberSave = Task.CompletedTask;

    /// <summary>
    /// Every full read of the working files brings the working-file index up to date, in the background; reads close
    /// together are indexed once, from the newest.
    /// </summary>
    private void OnWorkingFilesRead(ArkiveModEditWorkspaces workspaces, ArkiveModEditSnapshot snapshot, DateTime readStartUtc)
    {
        // Only this window's own stores: another window (a test's, say) reads another data folder.
        if (!ReferenceEquals(workspaces, _arkiveModEdits)) return;
        lock (_launchCacheLock)
        {
            var idle = _pendingIndexRead is null && _indexUpdate.IsCompleted;
            _pendingIndexRead = (snapshot, readStartUtc);
            if (idle) _indexUpdate = Task.Run(UpdateWorkingFileIndexAsync);
        }
    }

    private async Task UpdateWorkingFileIndexAsync()
    {
        while (true)
        {
            (ArkiveModEditSnapshot Snapshot, DateTime ReadStartUtc) read;
            lock (_launchCacheLock)
            {
                if (_pendingIndexRead is not { } pending) return;
                read = pending;
                _pendingIndexRead = null;
            }
            try
            {
                var clock = Stopwatch.StartNew();
                var library = await _arkiveModLibrary.LoadAsync();
                // Ids a mod names that the read did not find are kept as missing only for a library file that was not
                // written during or after the read.
                var libraryFile = new FileInfo(_arkiveModLibrary.ManifestPath);
                var sha = libraryFile.Exists && libraryFile.LastWriteTimeUtc < read.ReadStartUtc - TimeSpan.FromSeconds(2)
                    ? _arkiveModLibrary.Sha256Of(library)
                    : null;
                var bound = library.Mods.SelectMany(mod => mod.Bindings).Select(binding => binding.EditId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var index = await WorkingFileIndex.BuildAsync(read.Snapshot, read.ReadStartUtc, _projectRoot, LaunchCacheVersion,
                    sha, bound, _backgroundCheckCancellation.Token);
                WorkingFileIndex.Save(_projectRoot, index);
                Log($"Working-file index saved: entries={index.Entries.Count}, unsettled={index.Entries.Count(entry => !entry.Settled)}, " +
                    $"withStatus={index.Entries.Count(entry => entry.HasStatus)}, missing={index.MissingIds.Count}, " +
                    $"seconds={clock.Elapsed.TotalSeconds:N1}.");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
            {
                Log($"Working-file index not saved: {exception.Message}");
            }
        }
    }

    /// <summary>Keeps a verified Mods list for the next launch to show at once (in the background).</summary>
    private void RememberModList(ArkiveModLibraryManifest library, IReadOnlyList<ArkiveModStatus> statuses,
        IReadOnlySet<string> authoringPending, int outsideCount)
    {
        if (!_saveLaunchCaches || _arkiveModLibrary.Sha256Of(library) is not { } sha) return;
        var root = _projectRoot;
        lock (_launchCacheLock)
            _rememberSave = _rememberSave.ContinueWith(_ =>
            {
                try
                {
                    ArkiveModListMemory.Save(root, ArkiveModListMemory.Remember(statuses, authoringPending, outsideCount, sha,
                        LaunchCacheVersion, root));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Log($"Remembered Mods list not saved: {exception.Message}");
                }
            }, TaskScheduler.Default);
    }

    /// <summary>Waits for the index and remembered list being written (a measuring run exits right after).</summary>
    private async Task WaitForLaunchCachesAsync()
    {
        Task index, remember;
        lock (_launchCacheLock)
        {
            index = _indexUpdate;
            remember = _rememberSave;
        }
        await Task.WhenAll(index, remember);
        lock (_launchCacheLock)
            if (_pendingIndexRead is not null || !_indexUpdate.IsCompleted) index = _indexUpdate;
        await index;
    }
}
