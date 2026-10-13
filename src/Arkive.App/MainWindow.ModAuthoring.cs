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
    private readonly ArkiveModAuthoringWorkspace _modAuthoringWorkspace;
    private readonly ArkiveModMaterializationService _modMaterializer;
    private readonly DispatcherTimer _authoringMetadataTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private ArkiveModAuthoringDraft? _authoringDraft;
    private bool _authoringBusy;
    private bool _authoringLoading;
    private bool _authoringCloseApproved;
    private readonly Dictionary<string, string> _modsNeedingGameUpdate = new(StringComparer.OrdinalIgnoreCase);

    private sealed record AuthoringChoice(string Id, string DisplayName, ArkiveModAuthoringDraft? Draft, ArkiveModLibraryEntry? Mod)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record AuthoringAssetRow(
        string Name, string Kind, string Package, string ObjectName, string State,
        bool Included, ArkiveModLocalEdit? Edit, string? AssetId, string? WorkingEditId = null)
    {
        public string WorkingFile => Edit is null ? string.Empty : EditablePath(Edit) ?? "Edited in Arkive";
    }

    private static string? EditablePath(ArkiveModLocalEdit edit) => edit.Entry switch
    {
        GuidedTextureModProjectEntry texture => texture.Manifest.EditableImagePath,
        SkeletalMeshEditWorkspaceEntry mesh => mesh.EditedGlbPath,
        StaticMeshEditEntry mesh => mesh.Manifest.EditedGlbPath,
        _ => null,
    };

    private async Task RefreshModAuthoringAsync(string? selectId = null)
    {
        if (AuthoringModComboBox is null || _authoringBusy || _authoringLoading) return;
        _authoringLoading = true;
        AuthoringModComboBox.IsEnabled = false;
        try
        {
            var drafts = await _modAuthoringWorkspace.LoadAsync();
            foreach (var issue in _modAuthoringWorkspace.LoadIssues) Log($"Authoring draft could not be read: {issue}");
            var library = await _arkiveModLibrary.LoadAsync();
            var reconciled = new List<ArkiveModAuthoringDraft>();
            foreach (var draft in drafts)
            {
                var updated = draft.ModId is { } modId && library.Find(modId) is { } mod
                    ? await _modAuthoringWorkspace.ReconcileAsync(draft, mod) : draft;
                reconciled.Add(updated);
                if (_authoringDraft?.DraftId == updated.DraftId) _authoringDraft = updated;
            }
            drafts = reconciled;
            var choices = drafts.Where(draft => draft.ModId is null || library.Find(draft.ModId) is not null)
                .Select(draft => new AuthoringChoice(draft.DraftId,
                    draft.Metadata.Name + (draft.ModId is null ? " · draft" : string.Empty), draft,
                    draft.ModId is null ? null : library.Find(draft.ModId)))
                .Concat(library.Mods.Where(mod => drafts.All(draft => draft.ModId != mod.ModId))
                    .Select(mod => new AuthoringChoice(mod.ModId, mod.Manifest.Name, null, mod)))
                .OrderBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
            var id = selectId ?? _authoringDraft?.DraftId;
            AuthoringModComboBox.ItemsSource = choices;
            AuthoringModComboBox.SelectedItem = choices.FirstOrDefault(choice => choice.Id == id);
            if (_authoringDraft is not null) await LoadAuthoringAssetsAsync();
            UpdateAuthoringActions();
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            AuthoringStatusText.Text = exception is ArkiveModRefusalException refusal
                ? refusal.Message[(refusal.Code.Length + 2)..] : exception.Message;
            Log($"Authoring refresh failed: {exception}");
        }
        finally { _authoringLoading = false; UpdateAuthoringActions(); }
    }

    private async Task LoadAuthoringAssetsAsync()
    {
        if (_authoringDraft is null) { AuthoringAssetsGrid.ItemsSource = null; return; }
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_arkiveModEdits);
        var library = await _arkiveModLibrary.LoadAsync();
        var mod = _authoringDraft.ModId is null ? null : library.Find(_authoringDraft.ModId);
        var selectedIds = AuthoringAssetsGrid.SelectedItems.OfType<AuthoringAssetRow>()
            .Select(AuthoringRowKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<AuthoringAssetRow>();
        foreach (var id in _authoringDraft.EditIds.Concat(_authoringDraft.ReferenceEditIds).Distinct())
        {
            var edit = snapshot.Find(id);
            var asset = mod?.Manifest.Assets.FirstOrDefault(asset => mod.BindingFor(asset.AssetId)?.EditId == id ||
                edit is not null && ArkiveModTargetKey.From(asset) == edit.TargetKey);
            var changed = edit is not null && asset is not null && mod?.BindingFor(asset.AssetId) is { } binding &&
                (binding.EditId != id || await ArkiveModAuthoringService.IsModifiedAsync(edit, binding));
            rows.Add(new AuthoringAssetRow(edit?.DisplayName ?? asset?.DisplayName ?? "Missing working asset",
                edit?.Kind.ToString() ?? asset?.Kind.ToString() ?? "—",
                asset?.Target.PackageAlias ?? Path.GetFileNameWithoutExtension(edit?.SourcePackagePath ?? string.Empty),
                edit?.ObjectName ?? asset?.Target.ObjectName ?? "—",
                edit is null ? "Prepare for editing" : changed ? "Unsaved edits" : asset is null ? "Draft" : "Saved",
                _authoringDraft.EditIds.Contains(id), edit, asset?.AssetId, id));
        }
        if (mod is not null)
            foreach (var asset in mod.Manifest.Assets.Where(asset => !rows.Any(row => row.AssetId == asset.AssetId) &&
                         !_authoringDraft.RemovedAssetIds.Contains(asset.AssetId)))
                rows.Add(new AuthoringAssetRow(asset.DisplayName, asset.Kind.ToString(), asset.Target.PackageAlias,
                    asset.Target.ObjectName, "Prepare for editing", true, null, asset.AssetId));
        AuthoringAssetsGrid.ItemsSource = rows;
        foreach (var row in rows.Where(row => selectedIds.Contains(AuthoringRowKey(row))))
            AuthoringAssetsGrid.SelectedItems.Add(row);
        if (AuthoringAssetsGrid.SelectedItems.Count == 0) AuthoringAssetsGrid.SelectedItem = rows.FirstOrDefault();
        AuthoringSummaryText.Text = $"{rows.Count(row => row.Included):N0} asset(s) in the mod · " +
                                   $"{rows.Select(row => row.Package).Distinct().Count():N0} package(s)";
        UpdateAuthoringActions();
    }

    private ArkiveModMetadata AuthoringMetadata() => new(
        AuthoringNameTextBox.Text, AuthoringVersionTextBox.Text, AuthoringAuthorTextBox.Text,
        AuthoringDescriptionTextBox.Text, _authoringDraft?.Metadata.Website, _authoringDraft?.Metadata.Tags);

    private async Task PersistAuthoringDraftAsync()
    {
        _authoringMetadataTimer.Stop();
        if (_authoringDraft is not null)
            _authoringDraft = await _modAuthoringWorkspace.SaveAsync(_authoringDraft with { Metadata = AuthoringMetadata() });
        UpdateBannerContext();
    }

    private void LoadAuthoringMetadata()
    {
        _authoringLoading = true;
        AuthoringNameTextBox.Text = _authoringDraft?.Metadata.Name ?? string.Empty;
        AuthoringVersionTextBox.Text = _authoringDraft?.Metadata.Version ?? "1.0.0";
        AuthoringAuthorTextBox.Text = _authoringDraft?.Metadata.Author ?? string.Empty;
        AuthoringDescriptionTextBox.Text = _authoringDraft?.Metadata.Description ?? string.Empty;
        _authoringLoading = false;
        UpdateBannerContext();
    }

    private void AuthoringMetadata_Changed(object sender, TextChangedEventArgs e)
    {
        if (_authoringLoading || _authoringDraft is null) return;
        _authoringMetadataTimer.Stop();
        _authoringMetadataTimer.Start();
        AuthoringStatusText.Text = "Details changed. Save mod to update its .arkive file.";
    }

    private async void AuthoringMetadataTimer_Tick(object? sender, EventArgs e)
    {
        _authoringMetadataTimer.Stop();
        if (_authoringBusy) return;
        try { await PersistAuthoringDraftAsync(); }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        { AuthoringStatusText.Text = $"Draft could not be saved: {exception.Message}"; }
    }

    private async void AuthoringModComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_authoringLoading || _authoringBusy || AuthoringModComboBox.SelectedItem is not AuthoringChoice choice) return;
        await RunAuthoringOperationAsync(async () =>
        {
            await PersistAuthoringDraftAsync();
            _authoringDraft = choice.Draft ?? await _modAuthoringWorkspace.BeginEditAsync(choice.Mod!);
            LoadAuthoringMetadata();
            AuthoringStatusText.Text = "Choose an asset to edit, or add assets to this mod.";
        });
    }

    private async Task RunAuthoringOperationAsync(Func<Task> operation)
    {
        if (_authoringBusy || _modLibraryBusy || _guidedTextureModInProgress || _modSetBusy || _workshopBusy) return;
        _authoringBusy = true;
        _guidedTextureModInProgress = true;
        UpdateAuthoringActions();
        UpdateModLibraryActionState();
        UpdateGuidedTextureModActionState();
        try { await operation(); }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception) ||
                                              exception is System.ComponentModel.Win32Exception)
        {
            AuthoringStatusText.Text = exception is ArkiveModRefusalException refusal
                ? refusal.Message[(refusal.Code.Length + 2)..] : exception.Message;
            if (CreateModTab.IsSelected) CreateModStatusText.Text = AuthoringStatusText.Text;
            Log($"Mod authoring failed: {exception}");
        }
        finally
        {
            _authoringBusy = false;
            _guidedTextureModInProgress = false;
            await RefreshModAuthoringAsync();
            UpdateGuidedTextureModActionState();
            UpdateModLibraryActionState();
            UpdateWorkshopActionState();
        }
    }

    private void UpdateAuthoringActions()
    {
        if (AuthoringSaveButton is null) return;
        var idle = !_authoringBusy && !_modLibraryBusy && !_guidedTextureModInProgress && !_workshopBusy;
        var hasMod = _authoringDraft is not null;
        var selectionCount = AuthoringAssetsGrid.SelectedItems.OfType<AuthoringAssetRow>().Count();
        var selected = selectionCount == 1 ? AuthoringAssetsGrid.SelectedItem as AuthoringAssetRow : null;
        CreateModButton.IsEnabled = idle;
        CreateModNameTextBox.IsEnabled = idle;
        CreateModVersionTextBox.IsEnabled = idle;
        CreateModAuthorTextBox.IsEnabled = idle;
        CreateModDescriptionTextBox.IsEnabled = idle;
        AuthoringModComboBox.IsEnabled = idle && !_authoringLoading;
        AuthoringNameTextBox.IsEnabled = idle && hasMod;
        AuthoringVersionTextBox.IsEnabled = idle && hasMod;
        AuthoringAuthorTextBox.IsEnabled = idle && hasMod;
        AuthoringDescriptionTextBox.IsEnabled = idle && hasMod;
        AuthoringNewButton.IsEnabled = idle;
        AuthoringSaveButton.IsEnabled = idle && hasMod;
        AuthoringApplyButton.IsEnabled = idle && hasMod;
        AuthoringPreviewChangesButton.IsEnabled = idle && hasMod;
        AuthoringShareButton.IsEnabled = idle && _authoringDraft?.ModId is not null;
        AuthoringPrepareButton.IsEnabled = idle && _authoringDraft?.ModId is not null;
        AuthoringPrepareButton.Visibility = _authoringDraft?.ModId is not null &&
            AuthoringAssetsGrid.Items.OfType<AuthoringAssetRow>().Any(row => row.Edit is null)
                ? Visibility.Visible : Visibility.Collapsed;
        AuthoringAddAssetsButton.IsEnabled = idle && hasMod;
        AuthoringRemoveButton.IsEnabled = idle && hasMod && selectionCount > 0;
        AuthoringRemoveButton.Content = selectionCount > 0 ? $"Remove selected ({selectionCount:N0})" : "Remove selected assets";
        AuthoringOpenButton.IsEnabled = idle && selected?.Edit is not null;
        AuthoringReplaceButton.IsEnabled = idle && selected?.Edit is { } edit && EditablePath(edit) is not null;
        AuthoringFolderButton.IsEnabled = idle && selected?.Edit is not null;
        AuthoringCompareButton.IsEnabled = idle && selected?.Edit is not null;
        AuthoringAssetsGrid.IsEnabled = idle || _authoringExtractionCancellation is not null;
        AuthoringAssetsGrid.Tag = idle;
        AuthoringPreviewButton.IsEnabled = (idle || _authoringExtractionCancellation is not null) &&
            _umodelBridge is not null && selected?.Edit is not null;
        AuthoringViewButton.IsEnabled = idle && _umodelBridge is not null &&
            AuthoringAssetsGrid.Items.OfType<AuthoringAssetRow>().Any(row =>
                row.Edit?.Kind is UnifiedPackageAssetKind.SkeletalMesh or UnifiedPackageAssetKind.StaticMesh);
        AuthoringOpenButton.Content = selected?.Edit?.Kind == UnifiedPackageAssetKind.MaterialInstanceConstant
            ? "Edit material" : "Open edited file";
    }

    private void AuthoringAssetsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAuthoringActions();

    private void AuthoringAddAssetsButton_Click(object sender, RoutedEventArgs e)
    { AuthoringSearchExpander.IsExpanded = true; WorkshopSearchTextBox.Focus(); }

    private async Task<ArkiveModLocalEdit> AddAuthoringAssetAsync(LibraryAssetRow row, AuthoringAddSession session, bool reference = false)
    {
        if (_authoringDraft is null) throw new InvalidOperationException("Create or select a mod first.");
        session.CancellationToken.ThrowIfCancellationRequested();
        var key = AuthoringAssetKey(row.SourcePath, row.ObjectName);
        if (session.Existing.TryGetValue(key, out var duplicate))
        {
            if (!reference && _authoringDraft.ReferenceEditIds.Contains(duplicate.EditId))
                _authoringDraft = await _modAuthoringWorkspace.SaveAsync(_authoringDraft with
                {
                    EditIds = _authoringDraft.EditIds.Append(duplicate.EditId).Distinct().ToArray(),
                    ReferenceEditIds = _authoringDraft.ReferenceEditIds.Where(id => id != duplicate.EditId).ToArray(),
                });
            return duplicate;
        }
        if (!session.CleanPackages.TryGetValue(row.SourcePath, out var clean))
        {
            clean = await _arkiveModApply.ResolveCleanPackageAsync(row.SourcePath, session.CancellationToken);
            session.CleanPackages.Add(row.SourcePath, clean);
        }
        var progress = new Progress<ArkiveModAssetProgress>(item =>
        {
            if (_authoringExtractionCancellation is not null && !session.CancellationToken.IsCancellationRequested)
                ReportAuthoringExtraction($"{item.Stage}: {item.ObjectName}");
        });
        var created = await Task.Run(() => _modMaterializer.CreateEditableAsync(
            new ArkiveModAssetSelection(clean, row.PackageAlias, row.ClassName, row.ObjectName),
            session.CancellationToken, progress), session.CancellationToken);
        _authoringDraft = await _modAuthoringWorkspace.SaveAsync(reference
            ? _authoringDraft with { ReferenceEditIds = _authoringDraft.ReferenceEditIds.Append(created.EditId).ToArray() }
            : _authoringDraft with { EditIds = _authoringDraft.EditIds.Append(created.EditId).ToArray() });
        session.Existing.Add(key, created);
        AuthoringStatusText.Text = $"Added {row.ObjectName}. Open its working file to edit it, then save the mod.";
        if (CanPreviewInArkive(row.Kind) && EditablePath(created) is { } glb &&
            Path.GetExtension(glb).Equals(".glb", StringComparison.OrdinalIgnoreCase))
            _ = BakeAuthoringColoursAsync(row, glb);
        return created;
    }

    private async Task PrepareAuthoringAsync()
    {
        if (_authoringDraft?.ModId is not { } modId) return;
        await _arkiveModApply.PrepareModForEditingAsync(modId);
        var mod = (await _arkiveModLibrary.LoadAsync()).Find(modId)!;
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_arkiveModEdits);
        var targets = _authoringDraft.EditIds.Select(snapshot.Find).Where(edit => edit is not null)
            .Select(edit => edit!.TargetKey).ToHashSet();
        var added = mod.Bindings.Where(binding => !_authoringDraft.RemovedAssetIds.Contains(binding.AssetId))
            .Select(binding => snapshot.Find(binding.EditId))
            .Where(edit => edit is not null && !targets.Contains(edit.TargetKey)).Select(edit => edit!.EditId);
        _authoringDraft = await _modAuthoringWorkspace.SaveAsync(_authoringDraft with
        {
            EditIds = _authoringDraft.EditIds.Concat(added).Distinct().ToArray(),
            SavedBindings = mod.Bindings.ToDictionary(binding => binding.AssetId, binding => binding.EditId),
        });
        AuthoringStatusText.Text = "Working assets are ready. No game files were changed.";
    }

    private async void AuthoringPrepareButton_Click(object sender, RoutedEventArgs e) =>
        await RunAuthoringOperationAsync(PrepareAuthoringAsync);

    private async Task<ArkiveModLibraryEntry> SaveAuthoringAsync()
    {
        await PersistAuthoringDraftAsync();
        if (_authoringDraft is null) throw new InvalidOperationException("Create or select a mod first.");
        var saved = await _arkiveModAuthoring.SaveDraftAsync(_authoringDraft);
        _authoringDraft = await _modAuthoringWorkspace.SaveAsync(_authoringDraft with
        {
            ModId = saved.ModId,
            EditIds = saved.Bindings.Select(binding => binding.EditId).ToArray(),
            RemovedAssetIds = [],
            SavedBindings = saved.Bindings.ToDictionary(binding => binding.AssetId, binding => binding.EditId),
        });
        await ArkiveModSelectionService.SynchronizeAsync(_arkiveModLibrary, _arkiveModEdits);
        AuthoringStatusText.Text = $"Saved {saved.FileName}. Enable the mod in Mods to apply it.";
        return saved;
    }

    private async void AuthoringSaveButton_Click(object sender, RoutedEventArgs e) =>
        await RunAuthoringOperationAsync(async () => { await SaveAuthoringAsync(); await RefreshArkiveModsAsync("mod saved"); });

    private async void AuthoringApplyButton_Click(object sender, RoutedEventArgs e)
    {
        string? modId = null;
        await RunAuthoringOperationAsync(async () =>
        {
            var saved = await SaveAuthoringAsync();
            modId = saved.ModId;
            await _arkiveModLibrary.SetEnabledAsync(saved.ModId, true);
        });
        if (modId is null) return;
        await RefreshGuidedTextureModsAsync("authoring saved");
        await RefreshArkiveModsAsync("authoring saved", modId);
        ModifiedUpksTab.IsSelected = true;
        ModLibraryTab.IsSelected = true;
        await RunModApplyAsync(previewOnly: false);
    }

    private async void AuthoringPreviewChangesButton_Click(object sender, RoutedEventArgs e)
    {
        string? modId = null;
        await RunAuthoringOperationAsync(async () => { modId = (await SaveAuthoringAsync()).ModId; });
        if (modId is null) return;
        await RefreshArkiveModsAsync("authoring preview", modId);
        ModifiedUpksTab.IsSelected = true;
        ModLibraryTab.IsSelected = true;
        await RunModApplyAsync(previewOnly: true, previewModId: modId);
    }

    private async void AuthoringShareButton_Click(object sender, RoutedEventArgs e)
    {
        string? modId = null;
        await RunAuthoringOperationAsync(async () => { modId = (await SaveAuthoringAsync()).ModId; });
        if (modId is null) return;
        await RefreshArkiveModsAsync("authoring share", modId);
        ModLibraryExportButton_Click(sender, e);
    }

    private async void AuthoringIncludeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: AuthoringAssetRow row, IsChecked: { } include } || _authoringDraft is null) return;
        await RunAuthoringOperationAsync(async () =>
        {
            var edits = _authoringDraft.EditIds.ToHashSet();
            var references = _authoringDraft.ReferenceEditIds.ToHashSet();
            var removed = _authoringDraft.RemovedAssetIds.ToHashSet();
            if (row.Edit is { } edit)
            {
                if (include) { edits.Add(edit.EditId); references.Remove(edit.EditId); }
                else { edits.Remove(edit.EditId); references.Add(edit.EditId); }
            }
            if (row.AssetId is { } assetId) { if (include) removed.Remove(assetId); else removed.Add(assetId); }
            _authoringDraft = await _modAuthoringWorkspace.SaveAsync(_authoringDraft with
            { EditIds = edits.ToArray(), ReferenceEditIds = references.ToArray(), RemovedAssetIds = removed.ToArray() });
            AuthoringStatusText.Text = "Asset selection changed. Save mod to update its contents.";
        });
    }

    private async void AuthoringRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var rows = AuthoringAssetsGrid.SelectedItems.OfType<AuthoringAssetRow>().ToArray();
        if (rows.Length == 0 || _authoringDraft is null) return;
        await RunAuthoringOperationAsync(async () =>
        {
            await RemoveAuthoringAssetsAsync(rows);
        });
    }

    private static string AuthoringRowKey(AuthoringAssetRow row) => row.Edit?.EditId ?? row.WorkingEditId ?? row.AssetId ?? row.ObjectName;

    private async Task RemoveAuthoringAssetsAsync(IReadOnlyList<AuthoringAssetRow> rows)
    {
        _authoringDraft = await _modAuthoringWorkspace.SaveAsync(_authoringDraft!.RemoveAssets(
            rows.Select(row => row.Edit?.EditId ?? row.WorkingEditId).OfType<string>(),
            rows.Where(row => row.AssetId is not null).Select(row => row.AssetId!)));
        await LoadAuthoringAssetsAsync();
        AuthoringStatusText.Text = AuthoringAssetsGrid.Items.Count == 0
            ? "All assets removed from the draft. Add new assets before saving. Working files and the saved mod are unchanged."
            : $"Removed {rows.Count:N0} asset(s) from the draft. Save mod to confirm; working files are retained.";
    }


}
