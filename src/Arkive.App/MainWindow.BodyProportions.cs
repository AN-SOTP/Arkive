using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Inspection;
using Arkive.Core.Mods;
using Arkive.Core.Proportions;

namespace Arkive.App;

/// <summary>
/// Simple upscale tab: finds player bodies and outfit pieces and the tuned NPCs (and nothing else), opens them in the proportion
/// editor and saves the reshaped pieces as a new mod through the normal mod authoring route.
/// </summary>
public partial class MainWindow
{
    private sealed record ProportionRow(LibraryAssetRow Row, BodyProfile Profile)
    {
        public string Body => Profile.DisplayName;
        public string Piece => BodyProfiles.PieceName(Row.ObjectName) ?? "";
    }

    private BodyProportionPieceService? _bodyProportionPieces;
    private ProportionRow[] _proportionResults = [];
    private bool _proportionBusy;
    private bool _proportionSearched;

    private BodyProportionPieceService BodyProportionPieces() => _bodyProportionPieces ??= new BodyProportionPieceService(
        _reader, _protectionProvider, _skeletalMeshReconnaissanceService, _skeletalMeshReconnaissanceWorkspaceStore,
        _skeletalMeshEditableExportService, Path.Combine(_projectRoot, "cache", "body-proportions"));

    private void InitializeBodyProportionsTab()
    {
        ProportionBodyComboBox.ItemsSource = new[] { "All bodies" }.Concat(BodyProfiles.All.Select(profile => profile.DisplayName)).ToArray();
        ProportionBodyComboBox.SelectedIndex = 0;
    }

    private async void ProportionSearchButton_Click(object sender, RoutedEventArgs e) => await SearchProportionAssetsAsync();

    private async void ProportionSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchProportionAssetsAsync();
    }

    private void ProportionBodyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowProportionResults();

    private async Task SearchProportionAssetsAsync()
    {
        if (_proportionBusy) return;
        if (string.IsNullOrWhiteSpace(_libraryRootPath))
        {
            ProportionStatusText.Text = "Choose or restore the indexed Packages folder in Asset library first.";
            return;
        }
        var query = ProportionSearchTextBox.Text.Trim();
        if (query.Length == 0)
        {
            ProportionStatusText.Text = "Enter part of an outfit's in-game name or an object name, for example pc_wr_f_av.";
            return;
        }
        _proportionBusy = true;
        UpdateProportionActions();
        ProportionStatusText.Text = $"Searching player outfits and supported NPCs for {query}…";
        try
        {
            var technical = query.Contains('_') || query.Any(char.IsDigit);
            var scope = technical || _gameMetadataIndex is null ? PackageIndexSearchScope.ObjectAndInGameName : PackageIndexSearchScope.InGameName;
            var result = await _packageIndexStore.SearchAsync(_libraryRootPath, query, "SkeletalMesh", _gameMetadataIndex, scope, null,
                resultLimit: 2000);
            if (result.TotalMatches == 0 && scope == PackageIndexSearchScope.InGameName)
            {
                scope = PackageIndexSearchScope.ObjectName;
                result = await _packageIndexStore.SearchAsync(_libraryRootPath, query, "SkeletalMesh", _gameMetadataIndex, scope, null,
                    resultLimit: 2000);
            }
            // Only player bodies, outfit pieces and the tuned NPCs are reachable from this tab.
            _proportionResults = _assetDiscoveryService.Rank(result.Matches, query, scope)
                .Where(item => CanReconnoitreSkeletalMesh(item.Match.Kind, item.Match.ClassName) &&
                               BodyProfiles.IsReshapeable(item.Match.ObjectName))
                .Select(item => new ProportionRow(new LibraryAssetRow(
                    item.Match.SourcePath, item.Match.PackageAlias, item.Match.PhysicalPackageName, item.Match.PackageAssetCount,
                    item.Match.ExportIndex, item.Match.Kind, item.Match.ClassName, item.Match.ObjectName, item.Match.SerialSize,
                    item.Match.SerialOffset, item.Match.GameMetadata, item.Relevance, item.MatchReason),
                    BodyProfiles.ForObjectName(item.Match.ObjectName)!))
                .ToArray();
            _proportionSearched = true;
            ShowProportionResults();
            Log($"Body proportion search: query={query}, scope={scope}, matches={result.TotalMatches}, player pieces={_proportionResults.Length}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            ProportionStatusText.Text = "The search failed safely. See the session log.";
            Log($"Body proportion search failed: query={query}, error={exception}");
        }
        finally
        {
            _proportionBusy = false;
            UpdateProportionActions();
        }
    }

    private void ShowProportionResults()
    {
        if (ProportionResultsGrid is null || !_proportionSearched) return;
        var body = ProportionBodyComboBox.SelectedIndex > 0 ? BodyProfiles.All[ProportionBodyComboBox.SelectedIndex - 1] : null;
        var rows = _proportionResults.Where(row => body is null || row.Profile == body).ToArray();
        ProportionResultsGrid.ItemsSource = rows;
        ProportionResultsGrid.SelectedItem = rows.FirstOrDefault();
        ProportionStatusText.Text = rows.Length == 0
            ? _proportionResults.Length == 0
                ? "No player outfit pieces or supported NPCs matched. Try a shorter name or an object name such as pc_wr_f_av."
                : "No pieces of that body matched. Choose All bodies to see the others."
            : $"Found {rows.Length:N0} piece(s). Select one and choose Open in proportion editor.";
    }

    private void ProportionResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshProportionPresets();
        UpdateProportionActions();
    }

    private void ProportionResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && ProportionResultsGrid.SelectedItem is ProportionRow) OpenProportionEditor();
    }

    /// <summary>
    /// The pieces to reshape for the selected rows, read from clean packages (a mod already applied to a package must
    /// not be reshaped again) and, with Open the whole outfit, joined by each outfit's other pieces.
    /// </summary>
    private async Task<List<BodyProportionPieceRequest>> ProportionRequestsAsync(IReadOnlyList<ProportionRow> rows)
    {
        var requests = new List<BodyProportionPieceRequest>();
        foreach (var package in rows.GroupBy(row => row.Row.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var clean = await _arkiveModApply.ResolveCleanPackageAsync(package.Key);
            var names = package.Select(row => row.Row.ObjectName).ToList();
            if (ProportionWholeOutfitCheckBox.IsChecked == true)
            {
                var stems = names.Select(BodyProfiles.OutfitStem).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                var catalogue = await Task.Run(() => new AssetCatalogReader(_reader, _protectionProvider).Read(clean));
                names.AddRange(catalogue.Assets
                    .Where(asset => asset.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
                                    BodyProfiles.IsReshapeable(asset.ObjectName) &&
                                    BodyProfiles.OutfitStem(asset.ObjectName) is { } stem && stems.Contains(stem))
                    .Select(asset => asset.ObjectName));
            }
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                requests.Add(new BodyProportionPieceRequest(clean, package.First().Row.PackageAlias, name));
        }
        return requests;
    }

    private BodyProportionPresetStore? _bodyProportionPresets;

    private BodyProportionPresetStore BodyProportionPresets() =>
        _bodyProportionPresets ??= new BodyProportionPresetStore(Path.Combine(_projectRoot, "presets", "body-proportions"));

    /// <summary>Lists the selected body's presets (or every preset when nothing is selected).</summary>
    private void RefreshProportionPresets()
    {
        if (ProportionPresetComboBox is null) return;
        var profile = ProportionResultsGrid.SelectedItems.OfType<ProportionRow>().Select(row => row.Profile).Distinct().SingleOrDefault();
        var previous = (ProportionPresetComboBox.SelectedItem as BodyProportionPreset)?.Name;
        var presets = BodyProportionPresets().Load(profile?.Id);
        ProportionPresetComboBox.ItemsSource = presets;
        ProportionPresetComboBox.SelectedItem = presets.FirstOrDefault(preset => preset.Name == previous) ?? presets.FirstOrDefault();
    }

    private void ProportionPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProportionActions();

    /// <summary>
    /// Reshapes every selected outfit with one preset and saves them as a single new mod, without opening the editor.
    /// </summary>
    private async void ProportionApplyPresetButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProportionResultsGrid.SelectedItems.OfType<ProportionRow>().ToArray();
        if (selected.Length == 0 || _proportionBusy || ProportionPresetComboBox.SelectedItem is not BodyProportionPreset preset) return;
        var profile = selected.Select(row => row.Profile).Distinct().ToArray();
        if (profile.Length != 1 || profile[0].Id != preset.ProfileId)
        {
            ProportionStatusText.Text = "Select outfits of the body the preset was made for.";
            return;
        }
        _proportionBusy = true;
        UpdateProportionActions();
        try
        {
            ProportionStatusText.Text = "Finding the outfits' pieces…";
            var requests = await ProportionRequestsAsync(selected);
            var outfits = requests.Select(request => request.PackageAlias).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var name = $"{preset.Name} · {(outfits == 1 ? requests[0].PackageAlias : $"{outfits} outfits")}";
            ProportionStatusText.Text = $"Reshaping {requests.Count:N0} piece(s) of {outfits:N0} outfit(s) with {preset.Name}. " +
                                        "Each piece is checked against the game's animations, which takes a few seconds per piece…";
            var result = await SaveProportionModAsync(new ProportionSaveRequest(name, profile[0], requests,
                new Dictionary<string, double>(preset.Settings), preset.Follow));
            ProportionStatusText.Text = result;
            Log($"Proportion preset applied to outfits: preset={preset.Name}, outfits={outfits}, pieces={requests.Count}.");
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception) || exception is InvalidOperationException)
        {
            ProportionStatusText.Text = $"The preset was not applied: {exception.Message}";
            Log($"Proportion preset batch failed: {exception}");
        }
        finally
        {
            _proportionBusy = false;
            UpdateProportionActions();
        }
    }

    private void UpdateProportionActions()
    {
        if (ProportionOpenButton is null) return;
        var selected = ProportionResultsGrid.SelectedItems.OfType<ProportionRow>().ToArray();
        ProportionOpenButton.IsEnabled = !_proportionBusy && _umodelBridge is not null && selected.Length > 0;
        ProportionPreviewButton.IsEnabled = !_proportionBusy && _umodelBridge is not null && selected.Length == 1;
        ProportionApplyPresetButton.IsEnabled = !_proportionBusy && selected.Length > 0 &&
            ProportionPresetComboBox.SelectedItem is BodyProportionPreset preset &&
            selected.All(row => row.Profile.Id == preset.ProfileId);
    }

    private void ProportionPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProportionResultsGrid.SelectedItem is ProportionRow row) OpenModelViewer(row.Row, ProportionResultsGrid);
    }

    private void ProportionOpenButton_Click(object sender, RoutedEventArgs e) => OpenProportionEditor();

    private async void OpenProportionEditor()
    {
        var selected = ProportionResultsGrid.SelectedItems.OfType<ProportionRow>().ToArray();
        if (selected.Length == 0 || _umodelBridge is null || _proportionBusy) return;
        if (selected.Select(row => row.Profile).Distinct().Count() > 1)
        {
            ProportionStatusText.Text = "The selected pieces belong to different bodies. Select pieces of one body.";
            return;
        }
        _proportionBusy = true;
        UpdateProportionActions();
        try
        {
            var requests = await ProportionRequestsAsync(selected);
            var window = new ProportionEditorWindow(ModelViewerService(), BodyProportionPieces(), requests,
                Path.Combine(_projectRoot, "cache", "webview2"), _effectiveAppearanceTheme, SaveProportionModAsync,
                BodyProportionPresets(), Log)
            {
                Owner = this,
            };
            window.Show();
            ProportionStatusText.Text = $"Opened {requests.Count:N0} piece(s) in the proportion editor.";
            Log($"Proportion editor opened: pieces={string.Join(", ", requests.Select(request => request.ObjectName))}.");
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            ProportionStatusText.Text = $"The proportion editor could not open: {exception.Message}";
            Log($"Proportion editor could not open: {exception}");
        }
        finally
        {
            _proportionBusy = false;
            UpdateProportionActions();
        }
    }

    /// <summary>
    /// Creates a new mod holding every reshaped piece: each piece gets an ordinary working copy (as Add asset
    /// does), the sliders' offsets are applied to that copy, and the mod is saved, which checks each piece against
    /// the game's skeleton and animation stress poses. No game files change.
    /// </summary>
    private async Task<string> SaveProportionModAsync(ProportionSaveRequest request)
    {
        if (_authoringBusy || _modLibraryBusy || _guidedTextureModInProgress || _modSetBusy || _workshopBusy)
            throw new InvalidOperationException("Arkive is busy with another mod task. Try again when it finishes.");
        ArkiveModLibraryEntry? saved = null;
        Exception? failure = null;
        await RunAuthoringOperationAsync(async () =>
        {
            try { saved = await CreateProportionModAsync(request); }
            catch (Exception exception) { failure = exception; throw; }
        });
        if (failure is not null) throw failure;
        if (saved is null) throw new InvalidOperationException(AuthoringStatusText.Text);
        await RefreshArkiveModsAsync("proportion mod saved", saved.ModId);
        return $"Saved {saved.FileName}. Enable it in Mods to apply it to the game; it is also open in Edit mods.";
    }

    private async Task<ArkiveModLibraryEntry> CreateProportionModAsync(ProportionSaveRequest request)
    {
        await PersistAuthoringDraftAsync();
        var changes = request.Profile.Zones.Where(zone => request.Settings.TryGetValue(zone.Id, out var value) && value != 0)
            .Select(zone => $"{zone.Label} {request.Settings[zone.Id]:+0;-0}%");
        var description = $"Body proportions for {request.Profile.DisplayName}: {string.Join(", ", changes)}" +
                          (request.Follow.LooseClothing ? "; loose clothing follows the body" : "") +
                          (request.Follow.Hair ? "; hair follows the body" : "") +
                          $". Pieces: {string.Join(", ", request.Pieces.Select(piece => piece.ObjectName))}.";
        _authoringDraft = await _modAuthoringWorkspace.CreateAsync(new ArkiveModMetadata(request.ModName, "1.0.0", "", description));
        LoadAuthoringMetadata();
        foreach (var piece in request.Pieces)
        {
            AuthoringStatusText.Text = $"Reshaping {piece.ObjectName}…";
            var created = await Task.Run(() => _modMaterializer.CreateEditableAsync(
                new ArkiveModAssetSelection(piece.CleanPackagePath, piece.PackageAlias, "SkeletalMesh", piece.ObjectName)));
            var glb = EditablePath(created) ?? throw new InvalidDataException($"{piece.ObjectName} has no working GLB.");
            await Task.Run(async () =>
            {
                var bytes = await File.ReadAllBytesAsync(glb);
                var model = BodyProportionEngine.Build(bytes, request.Follow, request.Profile, piece.ObjectName);
                await File.WriteAllBytesAsync(glb, BodyProportionEngine.Apply(bytes, model, request.Settings));
            });
            _authoringDraft = await _modAuthoringWorkspace.SaveAsync(_authoringDraft with
            {
                EditIds = _authoringDraft.EditIds.Append(created.EditId).ToArray(),
            });
        }
        var saved = await SaveAuthoringAsync();
        Log($"Proportion mod saved: name={request.ModName}, body={request.Profile.Id}, pieces={request.Pieces.Count}, " +
            $"settings={string.Join(", ", request.Settings.Where(pair => pair.Value != 0).Select(pair => $"{pair.Key}={pair.Value}"))}, " +
            $"follow={request.Follow}.");
        return saved;
    }
}
