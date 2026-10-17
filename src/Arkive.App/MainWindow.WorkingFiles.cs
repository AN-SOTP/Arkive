using System.IO;
using System.Windows;
using Arkive.Core.Mods;

namespace Arkive.App;

public partial class MainWindow
{
    /// <summary>Working files changed this recently are left alone, in case another Arkive is still making them.</summary>
    private static readonly TimeSpan WorkingFileMinimumAge = TimeSpan.FromHours(1);

    private ArkiveWorkingFileCleanup WorkingFileCleanup() =>
        new(_projectRoot, _skeletalMeshEditWorkspaceStore, _skeletalMeshEditableWorkspaceStore);

    private async void ModLibraryCleanUpWorkingFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ModLibraryIdle || _proportionBusy || _workshopBusy) return;
        _modLibraryBusy = true;
        UpdateModLibraryActionState();
        try
        {
            ModLibraryStatusText.Text = "Looking for working files that nothing uses…";
            var cleanup = WorkingFileCleanup();
            var plan = await Task.Run(() => cleanup.PlanAsync(WorkingFileMinimumAge));
            Log($"Working-file cleanup planned: items={plan.Items.Count}, bytes={plan.Bytes}.");
            if (plan.Items.Count == 0)
            {
                ModLibraryStatusText.Text = "No working files to clean up: every edited mesh belongs to a draft, a mod or a package build.";
                return;
            }
            var meshes = plan.Items.Count(item => item.Description.StartsWith("Edited mesh", StringComparison.Ordinal));
            if (MessageBox.Show(this,
                    $"Delete {plan.Items.Count:N0} working folder(s) that no draft, mod or package build uses, freeing " +
                    $"{FormatByteSize(plan.Bytes)}?\n\n" +
                    $"They are {meshes:N0} edited mesh(es) and {plan.Items.Count - meshes:N0} game-mesh export(s), left behind by " +
                    "replaced Simple upscale pieces, assets removed from drafts and proportion editor previews.\n\n" +
                    "Your mods, drafts, package builds and backups are not touched, nor is any exported mesh changed since " +
                    "Arkive wrote it. This cannot be undone.",
                    "Clean up working files", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                ModLibraryStatusText.Text = "Nothing was deleted.";
                return;
            }
            var freed = await Task.Run(() => cleanup.DeleteAsync(plan, WorkingFileMinimumAge));
            ModLibraryStatusText.Text = $"Cleaned up working files: freed {FormatByteSize(freed)}.";
            Log($"Working files cleaned up: freedBytes={freed}, planned={plan.Items.Count}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
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
    /// The edited meshes a saved Simple upscale mod (and its open draft) holds for these pieces. Saving the mod again
    /// replaces them with newly reshaped ones, so they can be deleted once the save has succeeded.
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
            .Where(id => snapshot.Find(id) is { Kind: UnifiedPackageAssetKind.SkeletalMesh } edit && names.Contains(edit.ObjectName))
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
