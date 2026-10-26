using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.Mods;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;
using Microsoft.Win32;

namespace Arkive.App;

public partial class MainWindow : Window
{
    private async Task OpenModForAuthoringAsync(string modId, bool prepare)
    {
        await PersistAuthoringDraftAsync();
        var mod = (await _arkiveModLibrary.LoadAsync()).Find(modId) ?? throw new InvalidOperationException("The mod is missing.");
        _authoringDraft = await _modAuthoringWorkspace.BeginEditAsync(mod);
        LoadAuthoringMetadata();
        WorkshopTab.IsSelected = true;
        if (prepare) await PrepareAuthoringAsync();
    }

    private async void ModLibraryEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is not ModLibraryRow row) return;
        // A mod that only changes game tables is edited in the Game tables window.
        if (row.Entry.Manifest is { GameData: not null, Assets.Count: 0 })
        {
            await OpenGameTablesAsync(row.Entry);
            return;
        }
        await RunAuthoringOperationAsync(() => OpenModForAuthoringAsync(row.Entry.ModId, prepare: true));
    }

    private async void ModLibraryPrepareButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is not ModLibraryRow row) return;
        await RunAuthoringOperationAsync(() => OpenModForAuthoringAsync(row.Entry.ModId, prepare: true));
    }

    private async void ModLibraryEditAssetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ModLibraryEditAssetButton.IsEnabled || ModLibraryGrid.SelectedItem is not ModLibraryRow mod ||
            ModLibraryAssetsGrid.SelectedItem is not ModAssetRow asset) return;
        await RunAuthoringOperationAsync(() => OpenModForAuthoringAsync(mod.Entry.ModId, prepare: true));
        AuthoringAssetsGrid.SelectedItem = (AuthoringAssetsGrid.ItemsSource as IEnumerable<AuthoringAssetRow>)?
            .FirstOrDefault(row => row.AssetId == asset.Status.Asset.AssetId);
        AuthoringOpenButton_Click(sender, e);
    }

    private void ModLibraryAssetsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    { if (e.ChangedButton == MouseButton.Left) ModLibraryEditAssetButton_Click(sender, e); }

    private async Task<ArkiveModLibraryEntry> SaveLibraryModAsync(string modId)
    {
        var library = await _arkiveModLibrary.LoadAsync();
        var mod = library.Find(modId) ?? throw new InvalidOperationException("The mod is missing.");
        var draft = (await _modAuthoringWorkspace.LoadAsync()).FirstOrDefault(draft => draft.ModId == modId);
        if (draft is not null)
        {
            if (_authoringDraft?.DraftId == draft.DraftId)
            {
                await PersistAuthoringDraftAsync();
                draft = _authoringDraft;
            }
            var saved = await _arkiveModAuthoring.SaveDraftAsync(draft!);
            var updated = await _modAuthoringWorkspace.SaveAsync(draft! with
            {
                EditIds = saved.Bindings.Select(binding => binding.EditId).ToArray(),
                RemovedAssetIds = [],
                SavedBindings = saved.Bindings.ToDictionary(binding => binding.AssetId, binding => binding.EditId),
            });
            if (_authoringDraft?.DraftId == updated.DraftId) _authoringDraft = updated;
            return saved;
        }
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_arkiveModEdits);
        foreach (var binding in mod.Bindings)
            if (snapshot.Find(binding.EditId) is { } edit && await ArkiveModAuthoringService.IsModifiedAsync(edit, binding))
                await _arkiveModAuthoring.ValidateEditAsync(edit);
        return await _arkiveModAuthoring.SaveModAsync(modId);
    }

    /// <returns>The working files as read, for the plan that follows, or null once a mod was saved (saving may change them).</returns>
    private async Task<ArkiveModEditSnapshot?> SaveEnabledModChangesAsync()
    {
        var library = await _arkiveModLibrary.LoadAsync();
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_arkiveModEdits);
        var drafts = await _modAuthoringWorkspace.LoadAsync();
        var saved = false;
        foreach (var mod in library.Mods.Where(mod => mod.Enabled))
        {
            var draft = drafts.FirstOrDefault(draft => draft.ModId == mod.ModId);
            var pending = draft is not null && DraftHasChanges(draft, mod);
            var changed = false;
            foreach (var binding in mod.Bindings)
                if (snapshot.Find(binding.EditId) is { } edit && await ArkiveModAuthoringService.IsModifiedAsync(edit, binding))
                { changed = true; break; }
            if (!pending && !changed) continue;
            await SaveLibraryModAsync(mod.ModId);
            saved = true;
        }
        return saved ? null : snapshot;
    }

    private static bool DraftHasChanges(ArkiveModAuthoringDraft draft, ArkiveModLibraryEntry mod) =>
        draft.RemovedAssetIds.Count > 0 ||
        !draft.EditIds.ToHashSet().SetEquals(mod.Bindings.Select(binding => binding.EditId)) ||
        draft.Metadata.Name.Trim() != mod.Manifest.Name || draft.Metadata.Version.Trim() != mod.Manifest.Version ||
        (draft.Metadata.Author ?? string.Empty).Trim() != (mod.Manifest.Author ?? string.Empty) ||
        (draft.Metadata.Description ?? string.Empty).Trim() != (mod.Manifest.Description ?? string.Empty);

    private void ModLibraryMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
