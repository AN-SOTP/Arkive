using System.IO;
using System.Windows;
using Arkive.Core.Mods;

namespace Arkive.App;

public partial class MainWindow
{
    /// <summary>Working files changed this recently are left alone, in case another Arkive is still making them.</summary>
    private static readonly TimeSpan WorkingFileMinimumAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Drops the extra copies of game packages that finished builds keep (see <see cref="PackageRevisionTrimmer"/>).
    /// Each build is trimmed once; failures only reach the log.
    /// </summary>
    private async Task<long> TrimPackageBuildsAsync(string trigger)
    {
        try
        {
            var freed = await Task.Run(() => _guidedTexturePackageWorkspace.TrimRevisionsAsync());
            if (freed > 0) Log($"Package builds trimmed: trigger={trigger}, freedBytes={freed}.");
            return freed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Log($"Package builds could not be trimmed: {exception.Message}");
            return 0;
        }
    }

    private ArkiveWorkingFileCleanup WorkingFileCleanup() =>
        new(_projectRoot, _skeletalMeshEditWorkspaceStore, _skeletalMeshEditableWorkspaceStore);

    /// <summary>Superseded package builds and every kind of unused working file, as one clean-up.</summary>
    private ArkiveStorageCleanup StorageCleanup() => new(
        _projectRoot, _arkiveModEdits, _skeletalMeshEditableWorkspaceStore, _guidedTexturePackageWorkspace,
        _modDeploymentService);

    private async void ModLibraryCleanUpWorkingFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ModLibraryIdle || _proportionBusy || _workshopBusy) return;
        _modLibraryBusy = true;
        UpdateModLibraryActionState();
        try
        {
            ModLibraryStatusText.Text = "Looking for old package builds and working files that nothing uses…";
            var cleanup = StorageCleanup();
            var trimmed = await TrimPackageBuildsAsync("clean up working files");
            var plan = await Task.Run(() => cleanup.PlanAsync(WorkingFileMinimumAge));
            Log($"Working-file cleanup planned: items={plan.Count}, bytes={plan.Bytes}, " +
                $"categories={string.Join(", ", plan.Categories.Select(category => $"{category.Name} {category.Count}/{category.Bytes}"))}, " +
                $"keptBuilds={plan.Builds.Kept.Count}, keptWorkingFiles={plan.WorkingFiles.Kept.Count}.");
            if (plan.Count == 0)
            {
                ModLibraryStatusText.Text = (trimmed > 0 ? $"Freed {FormatByteSize(trimmed)} of extra package copies kept by earlier builds. " : "") +
                                            "No other working files to clean up: every package build and working file is still in use.";
                return;
            }
            var report = cleanup.WriteReport(plan, "Clean up working files in the Mods tab", dryRun: false);
            Log($"Working-file cleanup report written: {report}.");
            var lines = string.Join("\n", plan.Categories.Select(category =>
                $"  • {category.Name}: {category.Count:N0} ({FormatByteSize(category.Bytes)})"));
            if (MessageBox.Show(this,
                    $"Delete {plan.Count:N0} item(s) that nothing uses, freeing {FormatByteSize(plan.Bytes)}?\n\n{lines}\n\n" +
                    "These are package builds a newer build has replaced, and working files that no mod, draft or kept build uses: " +
                    "left behind by replaced Simple upscale pieces, assets removed from drafts, deleted mods and proportion " +
                    "editor previews.\n\n" +
                    "Your mods, drafts, game backups, the latest and any installed build of each package, and anything changed " +
                    "in the last hour are not touched. Nothing is kept aside, so this cannot be undone. The full list, with the " +
                    $"reason for each item, is in:\n{report}",
                    "Clean up working files", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                ModLibraryStatusText.Text = "Nothing was deleted.";
                return;
            }
            ModLibraryStatusText.Text = "Deleting old package builds and unused working files…";
            var result = await Task.Run(() => cleanup.RunAsync(plan));
            ArkiveStorageCleanup.AppendResult(report, result);
            // The package views list builds and texture projects; they read them again when next opened.
            if (result.Count > 0) _packageWorkspaceStale = true;
            var freed = trimmed + result.Bytes;
            var left = result.Builds.NotRemoved.Count + result.WorkingFiles.NotRemoved.Count;
            ModLibraryStatusText.Text = $"Cleaned up working files: freed {FormatByteSize(freed)}." +
                                        (left > 0 ? $" {left:N0} item(s) were kept because they changed or came into use; see {report}." : string.Empty);
            Log($"Working files cleaned up: freedBytes={freed}, removed={result.Count}, planned={plan.Count}, left={left}, report={report}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                                              InvalidDataException or System.Text.Json.JsonException or NotSupportedException)
        {
            ModLibraryStatusText.Text = $"Working files were not cleaned up: {exception.Message}";
            Log($"Working-file cleanup failed: {exception}");
        }
        finally
        {
            _modLibraryBusy = false;
            UpdateModLibraryActionState();
            await RefreshSkeletalMeshEditableWorkspaceAsync("working files cleaned up");
        }
    }

    /// <summary>
    /// After a successful Apply, removes the superseded builds of the packages it rebuilt, with no question asked: each
    /// is checked with full inspections first and listed in a report. Failures only reach the log.
    /// </summary>
    private async Task CleanUpAfterApplyAsync(ArkiveModApplyResult result)
    {
        if (!result.Succeeded) return;
        try
        {
            var cleaned = await Task.Run(() => StorageCleanup().AfterApplyAsync(result));
            if (cleaned is { } done)
                Log($"Superseded builds removed after Apply: removed={done.Result.Removed.Count}, freedBytes={done.Result.Bytes}, " +
                    $"left={done.Result.NotRemoved.Count}, report={done.ReportPath}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                                              InvalidDataException or System.Text.Json.JsonException or NotSupportedException)
        {
            Log($"Superseded builds were not removed after Apply: {exception}");
        }
    }

    /// <summary>
    /// The edited meshes a saved Simple upscale mod (and its open draft) holds for these pieces, and its matched
    /// physics. Saving the mod again replaces them with newly reshaped ones, so they can be deleted once the save has
    /// succeeded.
    /// </summary>
    private async Task<IReadOnlyList<string>> ProportionPieceEditIdsAsync(string modId, IEnumerable<string> pieceNames)
    {
        var names = pieceNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var library = await _arkiveModLibrary.LoadAsync();
        var draft = (await _modAuthoringWorkspace.LoadAsync()).FirstOrDefault(item => item.ModId == modId);
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_arkiveModEdits);
        return (library.Find(modId)?.Bindings.Select(binding => binding.EditId) ?? [])
            .Concat(draft?.EditIds ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => snapshot.Find(id) is { Kind: UnifiedPackageAssetKind.SkeletalMesh } edit && names.Contains(edit.ObjectName) ||
                         snapshot.Find(id) is { Kind: UnifiedPackageAssetKind.PhysicsAsset })
            .ToArray();
    }

    /// <summary>Deletes the replaced pieces' working files, if nothing uses them any more. Failures only reach the log.</summary>
    private async Task CleanUpReplacedProportionPiecesAsync(IReadOnlyList<string> editIds)
    {
        if (editIds.Count == 0) return;
        try
        {
            var cleanup = WorkingFileCleanup();
            var freed = await Task.Run(async () =>
                await cleanup.DeleteAsync(await cleanup.PlanAsync(TimeSpan.Zero, editIds), TimeSpan.Zero, editIds));
            Log($"Replaced Simple upscale pieces cleaned up: pieces={editIds.Count}, freedBytes={freed}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"Replaced Simple upscale pieces could not be cleaned up: {exception.Message}");
        }
    }
}
