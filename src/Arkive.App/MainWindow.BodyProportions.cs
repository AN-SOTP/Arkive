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
        RefreshProportionPresets();
    }

    private async void ProportionSearchButton_Click(object sender, RoutedEventArgs e) => await SearchProportionAssetsAsync();

    private async void ProportionSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchProportionAssetsAsync();
    }

    private void ProportionBodyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowProportionResults();
        RefreshProportionPresets();
    }

    // Presets are saved from the proportion editor's own window, so the list is read again each time it opens.
    private void ProportionPresetComboBox_DropDownOpened(object? sender, EventArgs e) => RefreshProportionPresets();

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
                requests.Add(new BodyProportionPieceRequest(clean, package.First().Row.PackageAlias, name, Path.GetDirectoryName(package.Key)));
        }
        return requests;
    }

    private BodyProportionPresetStore? _bodyProportionPresets;

    private BodyProportionPresetStore BodyProportionPresets() =>
        _bodyProportionPresets ??= new BodyProportionPresetStore(Path.Combine(_projectRoot, "presets", "body-proportions"));

    /// <summary>Lists the presets of the selected outfits' body, else of the body chosen in the filter, else every preset.</summary>
    private void RefreshProportionPresets()
    {
        if (ProportionPresetComboBox is null || ProportionResultsGrid is null) return;
        var profile = ProportionResultsGrid.SelectedItems.OfType<ProportionRow>().Select(row => row.Profile).Distinct().SingleOrDefault() ??
                      (ProportionBodyComboBox?.SelectedIndex > 0 ? BodyProfiles.All[ProportionBodyComboBox.SelectedIndex - 1] : null);
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

    /// <summary>
    /// About how long one piece takes from start to finish (working copy, reshaping, then the skeleton and animation
    /// checks when the mod is saved; measured at 5 s on 89 Female Warrior pieces), and how much disk its working files and
    /// its share of the mod take (about 7 MB and 1.8 MB).
    /// </summary>
    private const double ProportionSecondsPerPiece = 5.5;
    private const double ProportionMegabytesPerPiece = 9;

    private CancellationTokenSource? _proportionBatchCancellation;

    /// <summary>
    /// Reshapes every player outfit of the chosen preset's body with that preset and saves them all as one new mod,
    /// after saying how many outfits that is and roughly how long it will take.
    /// </summary>
    private async void ProportionApplyBodyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_proportionBusy || ProportionPresetComboBox.SelectedItem is not BodyProportionPreset preset) return;
        if (string.IsNullOrWhiteSpace(_libraryRootPath))
        {
            ProportionStatusText.Text = "Choose or restore the indexed Packages folder in Asset library first.";
            return;
        }
        var profile = BodyProfiles.All.FirstOrDefault(item => item.Id == preset.ProfileId);
        if (profile is null || profile.IsNpc) return;
        _proportionBusy = true;
        UpdateProportionActions();
        try
        {
            ProportionStatusText.Text = $"Finding every {profile.DisplayName} outfit…";
            var found = await _packageIndexStore.SearchAsync(_libraryRootPath, "pc_", "SkeletalMesh", _gameMetadataIndex,
                PackageIndexSearchScope.ObjectName, null, resultLimit: 200_000);
            var pieces = found.Matches
                .Where(match => CanReconnoitreSkeletalMesh(match.Kind, match.ClassName) && BodyProfiles.IsReshapeable(match.ObjectName) &&
                                BodyProfiles.ForObjectName(match.ObjectName)?.Id == profile.Id)
                .ToArray();
            var outfits = pieces.Select(match => BodyProfiles.OutfitStem(match.ObjectName) ?? match.ObjectName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (pieces.Length == 0)
            {
                ProportionStatusText.Text = $"No {profile.DisplayName} outfits were found in the index.";
                return;
            }
            var minutes = Math.Max(1, (int)Math.Round(pieces.Length * ProportionSecondsPerPiece / 60));
            var gigabytes = pieces.Length * ProportionMegabytesPerPiece / 1024;
            var answer = MessageBox.Show(this,
                $"This reshapes all {outfits:N0} {profile.DisplayName} outfits ({pieces.Length:N0} pieces) with {preset.Name} " +
                "and saves them as one new mod.\n\n" +
                $"It takes a long time: about {(minutes < 90 ? $"{minutes:N0} minutes" : $"{minutes / 60.0:N1} hours")} on this PC, " +
                "since every piece gets a working copy and is checked against the game's skeleton and animations. " +
                $"The working files and the mod take about {gigabytes:N1} GB. Arkive stays busy until it finishes; " +
                "Stop ends it after the current piece and keeps the pieces done so far as a draft in Edit mods.\n\n" +
                "Start now?",
                "Apply preset to every outfit", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                ProportionStatusText.Text = "Nothing was changed.";
                return;
            }
            var requests = new List<BodyProportionPieceRequest>();
            foreach (var package in pieces.GroupBy(match => match.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                var clean = await _arkiveModApply.ResolveCleanPackageAsync(package.Key);
                foreach (var name in package.Select(match => match.ObjectName).Distinct(StringComparer.OrdinalIgnoreCase)
                             .Order(StringComparer.OrdinalIgnoreCase))
                    requests.Add(new BodyProportionPieceRequest(clean, package.First().PackageAlias, name, Path.GetDirectoryName(package.Key)));
            }
            _proportionBatchCancellation = new CancellationTokenSource();
            ProportionCancelButton.Visibility = Visibility.Visible;
            Log($"Proportion preset applied to every outfit of a body: preset={preset.Name}, body={profile.Id}, outfits={outfits}, pieces={requests.Count}.");
            var started = DateTime.UtcNow;
            ProportionStatusText.Text = await SaveProportionModAsync(new ProportionSaveRequest(
                $"{preset.Name} · every {profile.DisplayName} outfit", profile, requests,
                new Dictionary<string, double>(preset.Settings), preset.Follow)
            {
                Cancellation = _proportionBatchCancellation.Token,
            });
            Log($"Body-wide proportion mod finished in {(DateTime.UtcNow - started).TotalMinutes:N1} min for {requests.Count} pieces.");
        }
        catch (OperationCanceledException)
        {
            ProportionStatusText.Text = "Stopped. The pieces reshaped so far are in a draft in Edit mods: save it there to keep them as a mod, or remove it.";
            Log("Body-wide proportion mod stopped by the user.");
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            ProportionStatusText.Text = $"The preset was not applied: {exception.Message}";
            Log($"Body-wide proportion preset failed: {exception}");
        }
        finally
        {
            _proportionBatchCancellation?.Dispose();
            _proportionBatchCancellation = null;
            ProportionCancelButton.Visibility = Visibility.Collapsed;
            _proportionBusy = false;
            UpdateProportionActions();
        }
    }

    private void ProportionCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _proportionBatchCancellation?.Cancel();
        ProportionStatusText.Text = "Stopping after the current piece…";
    }

    private const int MaximumEditorPieces = 20;

    /// <summary>Reopens the Simple upscale mod open in Edit mods in the proportion editor, with its sliders.</summary>
    private async void AuthoringProportionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_authoringDraft?.ModId is not { } modId || _umodelBridge is null || _proportionBusy || string.IsNullOrWhiteSpace(_libraryRootPath) ||
            BodyProportionModTags.Read(_authoringDraft.Metadata.Tags, _authoringDraft.Metadata.Description) is not { } settings) return;
        _proportionBusy = true;
        UpdateAuthoringActions();
        try
        {
            await PersistAuthoringDraftAsync();
            var library = await _arkiveModLibrary.LoadAsync();
            var mod = library.Find(modId) ?? throw new InvalidOperationException("The mod is no longer in the library.");
            var requests = new List<BodyProportionPieceRequest>();
            foreach (var asset in mod.Manifest.Assets.Where(asset => asset.Kind == UnifiedPackageAssetKind.SkeletalMesh &&
                                                                    BodyProfiles.IsReshapeable(asset.Target.ObjectName)))
            {
                var gamePackage = Path.Combine(_libraryRootPath, asset.Target.PhysicalPackageName);
                if (!File.Exists(gamePackage))
                    throw new FileNotFoundException($"{asset.Target.PhysicalPackageName} ({asset.Target.PackageAlias}) is not in the game folder.");
                var clean = await _arkiveModApply.ResolveCleanPackageAsync(gamePackage);
                requests.Add(new BodyProportionPieceRequest(clean, asset.Target.PackageAlias, asset.Target.ObjectName, _libraryRootPath));
            }
            if (requests.Count == 0) throw new InvalidOperationException("This mod has no outfit pieces the proportion editor can open.");
            if (requests.Count > MaximumEditorPieces)
                throw new InvalidOperationException($"This mod reshapes {requests.Count:N0} pieces, too many to open in the editor together. " +
                    "To change it, adjust its preset and use Apply preset to every outfit of its body again.");
            var window = new ProportionEditorWindow(ModelViewerService(), BodyProportionPieces(), requests,
                Path.Combine(_projectRoot, "cache", "webview2"), _effectiveAppearanceTheme, SaveProportionModAsync,
                BodyProportionPresets(), Log)
            {
                Animations = FindViewerAnimationsAsync,
                Environments = FindViewerEnvironmentsAsync,
                InitialSettings = settings,
                EditingModId = modId,
                EditingModName = mod.Manifest.Name,
                Owner = this,
            };
            window.Show();
            AuthoringStatusText.Text = $"Opened {mod.Manifest.Name} in the proportion editor. Saving there updates this mod.";
            Log($"Proportion editor opened for an upscale mod: mod={mod.Manifest.Name}, pieces={requests.Count}, settings=" +
                string.Join(", ", settings.Settings.Select(pair => $"{pair.Key}={pair.Value}")) + $", follow={settings.Follow}.");
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            AuthoringStatusText.Text = $"The proportion editor could not open this mod: {exception.Message}";
            Log($"Proportion editor could not open an upscale mod: {exception}");
        }
        finally
        {
            _proportionBusy = false;
            UpdateAuthoringActions();
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
        ProportionApplyBodyButton.IsEnabled = !_proportionBusy &&
            ProportionPresetComboBox.SelectedItem is BodyProportionPreset bodyPreset &&
            BodyProfiles.All.FirstOrDefault(profile => profile.Id == bodyPreset.ProfileId) is { IsNpc: false };
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
                Animations = FindViewerAnimationsAsync,
                Environments = FindViewerEnvironmentsAsync,
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
        return request.ExistingModId is null
            ? $"Saved {saved.FileName}. Enable it in Mods to apply it to the game; it is also open in Edit mods."
            : $"Updated {saved.FileName}. If it is enabled, apply it again in Mods to put the new shape in the game.";
    }

    private async Task<ArkiveModLibraryEntry> CreateProportionModAsync(ProportionSaveRequest request)
    {
        await PersistAuthoringDraftAsync();
        var modSettings = new BodyProportionModSettings(request.Profile, request.Settings, request.Follow);
        var description = BodyProportionModTags.Description(modSettings, request.Pieces.Select(piece => piece.ObjectName).ToArray());
        // The sliders travel with the mod in its tags, so it can be reopened in the proportion editor later.
        var tags = BodyProportionModTags.Write(modSettings);
        if (request.ExistingModId is { } modId)
        {
            // Editing a saved upscale mod: its reshaped pieces are replaced by new ones; anything else in it stays.
            var library = await _arkiveModLibrary.LoadAsync();
            var mod = library.Find(modId) ?? throw new InvalidOperationException("The mod being edited no longer exists.");
            var draft = (await _modAuthoringWorkspace.LoadAsync()).FirstOrDefault(item => item.ModId == modId) ??
                        await _modAuthoringWorkspace.BeginEditAsync(mod);
            var snapshot = await ArkiveModEditSnapshot.LoadAsync(_arkiveModEdits);
            var pieceNames = request.Pieces.Select(piece => piece.ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _authoringDraft = await _modAuthoringWorkspace.SaveAsync(draft with
            {
                Metadata = draft.Metadata with
                {
                    Name = request.ModName,
                    Description = description,
                    Tags = [.. BodyProportionModTags.Others(draft.Metadata.Tags), .. tags],
                },
                EditIds = draft.EditIds.Where(id => snapshot.Find(id) is not { } edit ||
                                                    edit.Kind != UnifiedPackageAssetKind.SkeletalMesh ||
                                                    !pieceNames.Contains(edit.ObjectName)).ToArray(),
            });
        }
        else
            _authoringDraft = await _modAuthoringWorkspace.CreateAsync(new ArkiveModMetadata(request.ModName, "1.0.0", "", description,
                Tags: tags));
        LoadAuthoringMetadata();
        foreach (var (piece, index) in request.Pieces.Select((piece, index) => (piece, index)))
        {
            request.Cancellation.ThrowIfCancellationRequested();
            AuthoringStatusText.Text = $"Reshaping {piece.ObjectName}…";
            if (request.Pieces.Count > 1)
                ProportionStatusText.Text = $"Reshaping piece {index + 1:N0} of {request.Pieces.Count:N0}: {piece.ObjectName}…";
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
        request.Cancellation.ThrowIfCancellationRequested();
        if (request.Pieces.Count > 1)
            ProportionStatusText.Text = $"Checking all {request.Pieces.Count:N0} pieces against the game's skeleton and animations " +
                                        "and saving them as one mod. This is the longest step…";
        var saved = await SaveAuthoringAsync();
        Log($"Proportion mod saved: name={request.ModName}, body={request.Profile.Id}, pieces={request.Pieces.Count}, " +
            $"settings={string.Join(", ", request.Settings.Where(pair => pair.Value != 0).Select(pair => $"{pair.Key}={pair.Value}"))}, " +
            $"follow={request.Follow}.");
        return saved;
    }
}
