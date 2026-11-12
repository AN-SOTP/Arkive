using System.Windows;
using Arkive.Core.Mods;

namespace Arkive.App;

public partial class MainWindow
{
    /// <summary>Release working files is offered for one mod that is off, out of the game and has its .arkive file.</summary>
    private static bool CanReleaseWorkingFiles(ModLibraryRow? row, bool idle, int chosen) =>
        idle && chosen == 1 && row is { Enabled: false, HasActiveAssets: false, Status.FileMissing: false };

    private void UpdateReleaseWorkingFilesItems(ModLibraryRow? row, bool idle, int chosen)
    {
        var enabled = CanReleaseWorkingFiles(row, idle, chosen);
        ModLibraryReleaseWorkingFilesButton.IsEnabled = enabled;
        ModLibraryRowReleaseWorkingFilesItem.IsEnabled = enabled;
    }

    /// <summary>
    /// Deletes a disabled mod's working files to free space; Apply sets them up again from the .arkive file when the mod
    /// is next turned on (see <see cref="ArkiveModAuthoringService.ReleaseWorkingFilesAsync"/>).
    /// </summary>
    private async void ModLibraryReleaseWorkingFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedModRows();
        if (rows.Length != 1 || !CanReleaseWorkingFiles(rows[0], ModLibraryIdle, rows.Length)) return;
        var row = rows[0];
        ArkiveModReleasePlan plan;
        try
        {
            plan = await Task.Run(() => _arkiveModAuthoring.PlanReleaseAsync(row.Entry.ModId, _arkiveModContext));
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or
                                              ArkiveModRefusalException or System.Text.Json.JsonException or
                                              InvalidOperationException or NotSupportedException)
        {
            Log($"Release working files could not be planned: mod={row.Entry.ModId}, error={exception}");
            MessageBox.Show(this, exception.Message, "Release working files", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Log($"Release working files planned: mod={row.Entry.ModId}, workingFiles={plan.EditIds.Count}, folders={plan.Folders.Count}, " +
            $"bytes={plan.Bytes}, problems={plan.Problems.Count}.");
        if (plan.Problems.Count > 0)
        {
            MessageBox.Show(this,
                $"Arkive did not release the working files of '{row.DisplayName}':\n\n" +
                string.Join("\n", plan.Problems.Select(problem => "• " + problem)),
                "Release working files", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"Delete the working files of '{row.DisplayName}', freeing {FormatByteSize(plan.Bytes)}?\n\n" +
                $"These are the {plan.EditIds.Count:N0} edited texture, mesh, material or effect file(s) Arkive unpacked from " +
                "the mod for this computer. The mod itself stays in your library, unchanged.\n\n" +
                "Turning the mod on again then needs it set up first: Apply changes does that from its .arkive file " +
                "before it builds the game files, which takes a little longer that one time.",
                "Release working files", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) !=
            MessageBoxResult.Yes) return;
        await RunModLibraryOperationAsync("Release working files", async () =>
        {
            var release = await Task.Run(() => _arkiveModAuthoring.ReleaseWorkingFilesAsync(row.Entry.ModId, _arkiveModContext));
            if (_authoringDraft?.ModId == row.Entry.ModId) _authoringDraft = null;
            // The package views list working files; they read them again when next opened.
            MarkPackageWorkspaceStale();
            MeshView.MarkStale();
            Log($"Working files released: mod={row.Entry.ModId}, name={row.DisplayName}, deletedWorkingFiles={release.DeletedEditIds.Count}, " +
                $"keptWorkingFiles={release.KeptEditIds.Count}, deletedExports={release.DeletedExports.Count}, " +
                $"deletedDrafts={release.DeletedDraftIds.Count}, deletedUnpacked={release.DeletedUnpackedFolders.Count}, plannedBytes={plan.Bytes}.");
            await SynchronizeModSelectionsAsync("working files released");
            return release.KeptEditIds.Count == 0
                ? $"Released the working files of {row.DisplayName}, freeing {FormatByteSize(plan.Bytes)}. Apply sets them up again when the mod is turned on."
                : $"Released {release.DeletedEditIds.Count:N0} working file(s) of {row.DisplayName}; {release.KeptEditIds.Count:N0} could not be deleted and stay set up.";
        }, row.Entry.ModId);
    }
}
