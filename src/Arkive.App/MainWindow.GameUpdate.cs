using System.IO;
using System.Windows;
using System.Windows.Controls;
using Arkive.Core.Discovery;
using Arkive.Core.Mods;

namespace Arkive.App;

/// <summary>
/// What a Lost Ark update means for the mods: the check, the rebase and the notice at startup, all from the Mod
/// Library page, plus the way back to that page from the advanced Mods pages.
/// </summary>
public partial class MainWindow
{
    private ArkiveModApplyPlan? _lastModApplyPlan;
    // True once this session's check found package projects the update left unchanged and they are not rebased yet;
    // an older saved check may describe projects that have been rebased since.
    private bool _safeRebaseOffered;

    private string KnownGameBuildPath => Path.Combine(_projectRoot, "cache", "game-build.txt");

    private ArkiveModLibraryEntry[] ModsNeedingRebase() => _arkiveModLibraryManifest.Mods
        .Where(mod => mod.Enabled && _modsNeedingGameUpdate.GetValueOrDefault(mod.ModId) == mod.FileSha256)
        .ToArray();

    private void UpdateGameUpdateActions()
    {
        if (ModLibraryRebaseButton is null) return;
        var idle = ModLibraryIdle && !_clientUpdateAuditBusy;
        ModLibraryCheckUpdateButton.IsEnabled = idle;
        var mods = ModsNeedingRebase().Length;
        ModLibraryRebaseButton.Visibility = mods > 0 || (_safeRebaseOffered && CanRebaseSafeClientProjects)
            ? Visibility.Visible : Visibility.Collapsed;
        ModLibraryRebaseButton.Content = mods switch { 0 => "Rebase mods", 1 => "Rebase 1 mod", _ => $"Rebase {mods:N0} mods" };
        ModLibraryRebaseButton.IsEnabled = idle;
    }

    private async void ModLibraryCheckUpdateButton_Click(object sender, RoutedEventArgs e) =>
        await CheckAfterGameUpdateAsync();

    /// <summary>
    /// Finds which enabled mods are ready for the installed game and which need rebasing (shown in the Apply Plan),
    /// and checks that Arkive can read and change the updated game files. No game file is changed.
    /// </summary>
    private async Task CheckAfterGameUpdateAsync()
    {
        if (!ModLibraryIdle || _clientUpdateAuditBusy) return;
        await RunModApplyAsync(previewOnly: true);
        // Without a plan the check stopped, and the status line says why.
        if (_lastModApplyPlan is not { } plan) return;

        ClientUpdateAuditEntry? audit;
        _modLibraryBusy = true;
        UpdateModLibraryActionState();
        using (ShowBusy("Checking the updated game files…", "Arkive is reading the game files your mods change. No game file is changed."))
        {
            try
            {
                // The check reads the package projects, which the plan above does not reload.
                await RefreshStalePackageWorkspaceAsync("check after game update");
                audit = await RunClientUpdateAuditAsync("check after game update", showResult: false, (done, total, _) =>
                {
                    ModLibraryProgressBar.Maximum = Math.Max(1, total);
                    ModLibraryProgressBar.Value = Math.Min(done, total);
                    UpdateBusy("Checking the updated game files…", $"{done:N0} of {total:N0} game files checked. No game file is changed.");
                });
            }
            finally
            {
                _modLibraryBusy = false;
            }
        }
        _safeRebaseOffered = audit is not null && CanRebaseSafeClientProjects;
        UpdateModLibraryActionState();
        if (audit is not null) await RecordGameBuildAsync();

        var enabled = _arkiveModLibraryManifest.Mods.Count(mod => mod.Enabled);
        var rebase = ModsNeedingRebase();
        var lines = new List<string>();
        var warn = false;
        if (audit?.Report.State == ClientUpdateAuditState.UnsupportedClientFormat)
        {
            lines.Add("Arkive cannot safely read or change the updated game files yet. Don't apply mods until an Arkive update supports this game version.");
            warn = true;
        }
        else if (audit?.Report.RequiresRestoreBeforeUpdateCheck == true)
        {
            lines.Add("Some game files still hold Arkive mods from before the update. Choose Restore all game files, then check again.");
            warn = true;
        }
        else if (audit is not null)
            lines.Add("The updated game files are ones Arkive can read and change.");

        if (enabled == 0)
            lines.Add("No mods are enabled, so no mod needs anything.");
        else if (rebase.Length > 0)
        {
            lines.Add((rebase.Length == 1 ? "1 mod needs" : $"{rebase.Length:N0} mods need") +
                      " rebasing because the update changed what they replace:\n" +
                      string.Join("\n", rebase.Take(10).Select(mod => $"  • {mod.Manifest.Name}")) +
                      (rebase.Length > 10 ? $"\n  • and {rebase.Length - 10:N0} more" : string.Empty) +
                      "\nChoose " + ModLibraryRebaseButton.Content + ", then Apply changes.");
            warn = true;
        }
        else if (!plan.CanApply)
        {
            lines.Add("Some mods can't be applied yet. The Apply Plan below says why.");
            warn = true;
        }
        else if (_safeRebaseOffered)
            lines.Add("Your mods are ready. Choose Rebase mods to move their working files onto the updated game files, then Apply changes.");
        else
            lines.Add(plan.HasChanges
                ? "Your mods are ready. Choose Apply changes to put them in the game."
                : "Your mods are ready and already in the game. Nothing needs doing.");

        var summary = string.Join("\n\n", lines);
        ModLibraryStatusText.Text = summary.Replace("\n", " ");
        Log($"Check after game update: enabled={enabled}, needRebase={rebase.Length}, canApply={plan.CanApply}, " +
            $"changes={plan.HasChanges}, audit={audit?.Report.State.ToString() ?? "none"}, safeProjects={_safeRebaseOffered}.");
        MessageBox.Show(this, summary, "Check after game update", MessageBoxButton.OK,
            warn ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    /// <summary>
    /// Sets up the mods the game update changed against the updated game files (each change is kept only if it passes
    /// every native safety check), and moves the working files the check proved unchanged onto them. No game file changes.
    /// </summary>
    private async void ModLibraryRebaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ModLibraryIdle || _clientUpdateAuditBusy) return;
        var mods = ModsNeedingRebase();
        var projects = _safeRebaseOffered && CanRebaseSafeClientProjects ? _latestClientUpdateAudit!.Report.SafeRebasePackageCount : 0;
        if (mods.Length == 0 && projects == 0)
        {
            _safeRebaseOffered = false;
            UpdateModLibraryActionState();
            return;
        }
        await RebaseModsAsync(mods, projects, "Rebase mods", "Rebase for this game update?",
            "These mods replace things the update changed.");
    }

    /// <summary>
    /// Sets <paramref name="mods"/> up against the installed game files, with progress, and saves the ones whose assets
    /// changed. <paramref name="projects"/> working files the check proved unchanged are moved onto the new files first.
    /// </summary>
    private async Task RebaseModsAsync(
        IReadOnlyList<ArkiveModLibraryEntry> mods, int projects, string title, string question, string intro)
    {
        if (MessageBox.Show(this,
                question + "\n\n" +
                (mods.Count == 0 ? string.Empty :
                    intro + " Arkive sets them up again against the updated game files, " +
                    "and keeps each change only if it passes every safety check:\n" +
                    string.Join("\n", mods.Take(10).Select(mod => $"  • {mod.Manifest.Name}")) +
                    (mods.Count > 10 ? $"\n  • and {mods.Count - 10:N0} more" : string.Empty) + "\n\n") +
                (projects == 0 ? string.Empty :
                    $"{projects:N0} working file(s) the update left unchanged move onto the updated game files.\n\n") +
                "No game file changes until you choose Apply changes. Look over rebased mods in game afterwards.",
                title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var problems = new List<string>();
        var unsaved = new List<string>();
        var rebasedIds = new List<string>();
        _modLibraryBusy = true;
        UpdateModLibraryActionState();
        using (ShowBusy("Rebasing mods…", "Arkive is setting up your mods for the updated game files. No game file is changed."))
        {
            try
            {
                if (projects > 0)
                {
                    UpdateBusy("Rebasing working files…");
                    if (await RebaseSafeClientProjectsAsync(showResult: false) is null)
                        problems.Add("Working files: the rebase stopped and was undone. The session log says why.");
                    _safeRebaseOffered = false;
                }
                ModLibraryProgressBar.Maximum = Math.Max(1, mods.Count);
                for (var index = 0; index < mods.Count; index++)
                {
                    var mod = mods[index];
                    ModLibraryProgressBar.Value = index;
                    var number = index;
                    var assets = new Progress<(int Done, int Total)>(update => UpdateBusy($"Rebasing {mod.Manifest.Name}…",
                        $"Mod {number + 1:N0} of {mods.Count:N0}, asset {Math.Min(update.Done + 1, update.Total):N0} of {update.Total:N0}. No game file is changed."));
                    try
                    {
                        var outcomes = await _arkiveModApply.UpdateModForCurrentClientAsync(mod.ModId, assets);
                        foreach (var outcome in outcomes)
                            Log($"Mod asset update: mod={mod.ModId}, asset={outcome.DisplayName}, " +
                                $"succeeded={outcome.Succeeded}, changed={outcome.Changed}, message={outcome.Message}");
                        var failed = outcomes.Where(outcome => !outcome.Succeeded).ToArray();
                        if (failed.Length == 0)
                        {
                            _modsNeedingGameUpdate.Remove(mod.ModId);
                            rebasedIds.Add(mod.ModId);
                        }
                        else
                            problems.Add($"{mod.Manifest.Name}: {failed[0].DisplayName}: {failed[0].Message}" +
                                         (failed.Length > 1 ? $" ({failed.Length - 1:N0} more like it)" : string.Empty));
                    }
                    catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
                    {
                        Log($"Mod rebase failed: mod={mod.ModId}, error={exception}");
                        problems.Add($"{mod.Manifest.Name}: {exception.Message}");
                    }
                }
                if (mods.Count > 0)
                {
                    await SynchronizeModSelectionsAsync("mods rebased for game update");
                    unsaved.AddRange(await SaveRebasedModsAsync(rebasedIds));
                }
            }
            finally
            {
                _modLibraryBusy = false;
                UpdateModLibraryActionState();
            }
        }
        var rebased = rebasedIds.Count;
        Log($"Rebase for game update: mods={mods.Count}, rebased={rebased}, projects={projects}, problems={problems.Count}, unsaved={unsaved.Count}.");
        var message = rebased == 0 ? string.Empty : rebased == 1 ? "Rebased 1 mod." : $"Rebased {rebased:N0} mods.";
        if (unsaved.Count > 0)
            message += "\n\nThese also have your own unsaved changes in Edit mods. Choose Save mod for each to keep them along with the rebased assets:\n" +
                       string.Join("\n", unsaved.Select(name => $"  • {name}"));
        if (problems.Count > 0)
            message += "\n\nThese could not be rebased; they need re-exporting and editing again for this game version, then saving into the mod:\n" +
                       string.Join("\n", problems.Take(12).Select(problem => $"  • {problem}"));
        message += unsaved.Count + problems.Count == 0
            ? " Choose Apply changes to put your mods in the game."
            : "\n\nApply changes still puts every other enabled mod in the game.";
        MessageBox.Show(this, message.Trim(), title, MessageBoxButton.OK,
            unsaved.Count + problems.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        // The Apply Plan shows what Apply changes will now do.
        await RunModApplyAsync(previewOnly: true);
    }

    /// <summary>
    /// Saves rebased mods whose working copies no longer match their .arkive file: an effect tinted again on the
    /// game's changed effect has new values, which Apply changes would otherwise refuse as unsaved edits. A mod whose
    /// copies still match (a mesh set up on its changed outfit, say) needs no save.
    /// </summary>
    /// <returns>The mods left unsaved because they also hold the user's own unsaved changes.</returns>
    private async Task<IReadOnlyList<string>> SaveRebasedModsAsync(IReadOnlyCollection<string> modIds)
    {
        var unsaved = new List<string>();
        if (modIds.Count == 0) return unsaved;
        await RefreshArkiveModsAsync("mods rebased for game update");
        foreach (var row in _modLibraryRows
                     .Where(row => modIds.Contains(row.Entry.ModId, StringComparer.OrdinalIgnoreCase) && row.Status.ModifiedLocally).ToArray())
        {
            // Edits the user made in Edit mods are theirs to save, so the rebase does not save them on their behalf.
            if (row.AuthoringPending)
            {
                unsaved.Add(row.DisplayName);
                continue;
            }
            UpdateBusy($"Saving {row.DisplayName}…", "Keeping the rebased assets in the mod. No game file is changed.");
            try
            {
                var saved = await SaveLibraryModAsync(row.Entry.ModId);
                Log($"Rebased mod saved: mod={saved.ModId}, file={saved.FileName}, sha256={saved.FileSha256}.");
            }
            catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
            {
                Log($"Rebased mod could not be saved: mod={row.Entry.ModId}, error={exception}");
                unsaved.Add($"{row.DisplayName} ({exception.Message})");
            }
        }
        return unsaved;
    }

    private void ModifiedUpksInnerTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, ModifiedUpksInnerTabControl) || ModsAdvancedBar is null) return;
        var page = ModifiedUpksInnerTabControl.SelectedItem as TabItem;
        var advanced = page is not null && page != ModLibraryTab;
        ModsAdvancedBar.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        if (advanced) ModsAdvancedBarText.Text = $"Advanced: {page!.Header}";
    }

    private void ModsAdvancedBackButton_Click(object sender, RoutedEventArgs e) =>
        ModifiedUpksInnerTabControl.SelectedItem = ModLibraryTab;

    private async Task<string?> CurrentGameBuildAsync()
    {
        var library = await _arkiveModLibrary.LoadAsync();
        var packages = library.GamePackagesDirectory ?? _libraryRootPath;
        return await Task.Run(() => GameBuildStamp.Read(packages));
    }

    /// <summary>Remembers the installed game build, so the next start only speaks up after another update.</summary>
    private async Task RecordGameBuildAsync()
    {
        try
        {
            if (await CurrentGameBuildAsync() is not { } build) return;
            Directory.CreateDirectory(Path.GetDirectoryName(KnownGameBuildPath)!);
            await File.WriteAllTextAsync(KnownGameBuildPath, build);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"The game build could not be remembered: {exception.Message}");
        }
    }

    /// <summary>
    /// At startup, tells the user when Lost Ark has been updated since Arkive last ran, and what to do: check and
    /// rebase the mods, then apply them, and choose again the fonts, language or login screen the update put back.
    /// </summary>
    private async Task NoticeGameUpdateAsync()
    {
        try
        {
            if (await CurrentGameBuildAsync() is not { } build) return;
            var known = File.Exists(KnownGameBuildPath) ? (await File.ReadAllTextAsync(KnownGameBuildPath)).Trim() : null;
            if (build == known) return;
            Log($"Game build: remembered={known ?? "none"}, installed={build}.");
            // With nothing remembered yet there is nothing to compare with.
            if (known is null)
            {
                await RecordGameBuildAsync();
                return;
            }

            var undone = new List<string>();
            if (await GameFontsAsync() is { } fonts && await Task.Run(() => fonts.GameUpdatedAsync()))
                undone.Add("Fonts: open the Fonts tab and choose Apply again.");
            if (await LanguagesAsync() is { } languages && await Task.Run(() => languages.GameUpdated()))
                undone.Add("Language: open the Language tab and choose your language again.");
            if (await LoginScreensAsync() is { } screens && await Task.Run(() => screens.GameUpdatedAsync()))
                undone.Add("Login screen: open the Login Screen tab and choose your screen again.");
            var hasMods = _arkiveModLibraryManifest.Mods.Any(mod => mod.Enabled);
            if (!hasMods) await RecordGameBuildAsync();
            if (!hasMods && undone.Count == 0) return;

            var text = "Lost Ark has been updated since you last used Arkive. Updates can replace the game files Arkive changes.\n\n" +
                       (hasMods
                           ? "Mods: in the Mods tab, choose Check after game update. If it finds mods to rebase, choose Rebase mods. Then choose Apply changes.\n\n"
                           : string.Empty) +
                       (undone.Count == 0 ? string.Empty :
                           "The update also put back the game's own:\n" + string.Join("\n", undone.Select(line => $"  • {line}")) + "\n\n");
            if (!hasMods)
            {
                MessageBox.Show(this, text.TrimEnd(), "Lost Ark was updated", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this, text + "Check your mods now? Choose No to be reminded the next time you start Arkive.",
                    "Lost Ark was updated", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            MainTabControl.SelectedItem = ModifiedUpksTab;
            await CheckAfterGameUpdateAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log($"The game update notice could not be prepared: {exception}");
        }
    }
}
