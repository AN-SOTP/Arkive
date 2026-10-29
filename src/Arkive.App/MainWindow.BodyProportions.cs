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
    /// checks when the mod is saved; about 3 s on a 536-piece Female Warrior run), and how much disk its working files
    /// and its share of the mod take (about 7 MB and 1.8 MB).
    /// </summary>
    private const double ProportionSecondsPerPiece = 3;
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
            var pieces = await FindEveryOutfitAsync(profile);
            if (pieces.Length == 0)
            {
                ProportionStatusText.Text = $"No {profile.DisplayName} outfits were found in the index.";
                return;
            }
            if (ConfirmUpscaleBatch(this, "Apply preset to every outfit",
                    $"This reshapes all {OutfitCount(pieces.Select(match => match.ObjectName)):N0} {profile.DisplayName} outfits " +
                    $"({pieces.Length:N0} pieces) with {preset.Name} and saves them as one new mod.", pieces.Length) is not { } parallelism)
            {
                ProportionStatusText.Text = "Nothing was changed.";
                return;
            }
            var requests = await CleanPieceRequestsAsync(pieces);
            Log($"Proportion preset applied to every outfit of a body: preset={preset.Name}, body={profile.Id}, " +
                $"pieces={requests.Count}, at once={parallelism}.");
            ProportionStatusText.Text = await RunUpscaleBatchAsync(new ProportionSaveRequest(
                $"{preset.Name} · every {profile.DisplayName} outfit", profile, requests,
                new Dictionary<string, double>(preset.Settings), preset.Follow)
            {
                Parallelism = parallelism,
                EveryOutfit = true,
            }, "The preset was not applied");
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            ProportionStatusText.Text = $"The preset was not applied: {exception.Message}";
            Log($"Body-wide proportion preset failed: {exception}");
        }
        finally
        {
            _proportionBusy = false;
            UpdateProportionActions();
        }
    }

    /// <summary>Every reshapeable piece of a player body's outfits in the index.</summary>
    private async Task<PackageIndexMatch[]> FindEveryOutfitAsync(BodyProfile profile)
    {
        var found = await _packageIndexStore.SearchAsync(_libraryRootPath!, "pc_", "SkeletalMesh", _gameMetadataIndex,
            PackageIndexSearchScope.ObjectName, null, resultLimit: 200_000);
        return found.Matches
            .Where(match => CanReconnoitreSkeletalMesh(match.Kind, match.ClassName) && BodyProfiles.IsReshapeable(match.ObjectName) &&
                            BodyProfiles.ForObjectName(match.ObjectName)?.Id == profile.Id)
            .ToArray();
    }

    private static int OutfitCount(IEnumerable<string> pieceNames) =>
        pieceNames.Select(name => BodyProfiles.OutfitStem(name) ?? name).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>Each piece read from its clean game package (the backup when a mod has changed the package).</summary>
    private async Task<List<BodyProportionPieceRequest>> CleanPieceRequestsAsync(IEnumerable<PackageIndexMatch> pieces)
    {
        var requests = new List<BodyProportionPieceRequest>();
        foreach (var package in pieces.GroupBy(match => match.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var clean = await _arkiveModApply.ResolveCleanPackageAsync(package.Key);
            foreach (var name in package.Select(match => match.ObjectName).Distinct(StringComparer.OrdinalIgnoreCase)
                         .Order(StringComparer.OrdinalIgnoreCase))
                requests.Add(new BodyProportionPieceRequest(clean, package.First().PackageAlias, name, Path.GetDirectoryName(package.Key)));
        }
        return requests;
    }

    /// <summary>
    /// Asks before reshaping many pieces into one mod, saying roughly how long it takes and how much disk it needs,
    /// then whether to work on several pieces at once. Returns how many at once, or null when the answer is no.
    /// </summary>
    private static int? ConfirmUpscaleBatch(Window owner, string title, string what, int pieces)
    {
        var minutes = pieces * ProportionSecondsPerPiece / 60;
        var gigabytes = pieces * ProportionMegabytesPerPiece / 1024;
        var answer = MessageBox.Show(owner,
            $"{what}\n\n" +
            $"It takes a long time: about {Duration(minutes)} on this PC, " +
            "since every piece gets a working copy and is checked against the game's skeleton and animations. " +
            $"The working files and the mod take about {gigabytes:N1} GB. Arkive stays busy until it finishes; " +
            "Stop in Simple upscale ends it after the current piece and keeps the pieces done so far as a draft in Edit mods.\n\n" +
            "Start now?",
            title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return null;
        if (ProportionParallelism <= 1) return 1;
        var together = ProportionParallelism;
        var faster = MessageBox.Show(owner,
            $"Reshape {together} pieces at once?\n\n" +
            $"Working on {together} pieces at the same time takes about {Duration(minutes / ProportionSpeedUp(together))} " +
            $"instead of {Duration(minutes)}. It is more taxing on this PC: it keeps {together} of its " +
            $"{Environment.ProcessorCount} processor threads busy and uses about twice the memory (around 1 GB), so the PC " +
            "and other programs (and the game, if it is open) may feel slower and its fans louder until it finishes.\n\n" +
            $"Yes: {together} at once. No: one piece at a time.",
            title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return faster == MessageBoxResult.Yes ? together : 1;
    }

    /// <summary>
    /// Reshapes many pieces into one mod (a new one, or the upscale mod being edited) with progress and Stop in the
    /// Simple upscale tab, and says what happened.
    /// </summary>
    private async Task<string> RunUpscaleBatchAsync(ProportionSaveRequest request, string failed)
    {
        _proportionBatchCancellation = new CancellationTokenSource();
        ProportionCancelButton.Visibility = Visibility.Visible;
        try
        {
            var started = DateTime.UtcNow;
            var result = await SaveProportionModAsync(request with { Cancellation = _proportionBatchCancellation.Token });
            Log($"Body-wide proportion mod finished in {(DateTime.UtcNow - started).TotalMinutes:N1} min for {request.Pieces.Count} pieces.");
            return result;
        }
        catch (OperationCanceledException)
        {
            Log("Body-wide proportion mod stopped by the user.");
            return request.ExistingModId is null
                ? "Stopped. The pieces reshaped so far are in a draft in Edit mods: save it there to keep them as a mod, or remove it."
                : "Stopped. The mod file is unchanged; the pieces reshaped so far are in its draft in Edit mods. " +
                  "Save it there to keep them, or discard the draft to keep the mod as it was.";
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            Log($"Body-wide proportion mod failed: {exception}");
            return $"{failed}: {exception.Message}";
        }
        finally
        {
            _proportionBatchCancellation?.Dispose();
            _proportionBatchCancellation = null;
            ProportionCancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ProportionCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _proportionBatchCancellation?.Cancel();
        ProportionStatusText.Text = "Stopping after the current piece…";
    }

    private const int MaximumEditorPieces = 20;

    /// <summary>
    /// Shows a batch's progress under the Simple upscale status: the step, a bar and how long is left (from the pace
    /// so far, once a few pieces are done). A total of 0 shows a moving bar for a step without a count.
    /// </summary>
    private void ShowProportionProgress(string text, int done, int total, DateTime started)
    {
        ProportionProgressPanel.Visibility = Visibility.Visible;
        ProportionProgressBar.IsIndeterminate = total <= 0;
        ProportionProgressBar.Value = total <= 0 ? 0 : (double)done / total;
        ProportionProgressPercent.Text = total <= 0 ? "" : $"{100.0 * done / total:N0}%";
        var elapsed = DateTime.UtcNow - started;
        TimeSpan? left = total > 0 && done >= Math.Min(3, total) && done < total && elapsed.TotalSeconds > 5
            ? TimeSpan.FromSeconds(elapsed.TotalSeconds / done * (total - done))
            : null;
        ProportionProgressText.Text = left is { } remaining
            ? $"{text} · about {(remaining.TotalMinutes < 1 ? "less than a minute" : Duration(remaining.TotalMinutes))} left"
            : text;
    }

    /// <summary>
    /// Gives every piece a working copy and reshapes it, one after another or several at the same time (each has its
    /// own working copy, so they share no files), then adds them to the draft in their original order. Stopping keeps
    /// the pieces finished so far in the draft.
    /// </summary>
    private async Task ReshapePiecesAsync(ProportionSaveRequest request)
    {
        var editIds = new string?[request.Pieces.Count];
        // Pieces these settings leave exactly as they were (shoulder pads, arms) are not saved into a body-wide mod.
        var unchanged = new System.Collections.Concurrent.ConcurrentBag<string>();
        var finished = 0;
        var started = DateTime.UtcNow;
        void Report(int done) => ShowProportionProgress(
            $"Step 1 of 2 · Reshaping pieces: {done:N0} of {request.Pieces.Count:N0}" +
            (request.Parallelism > 1 ? $" ({request.Parallelism} at a time)" : ""), done, request.Pieces.Count, started);
        if (request.Pieces.Count > 1) Report(0);
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, request.Pieces.Count),
                new ParallelOptions { MaxDegreeOfParallelism = request.Parallelism, CancellationToken = request.Cancellation },
                async (index, token) =>
                {
                    var piece = request.Pieces[index];
                    if (request.Parallelism == 1 && request.Pieces.Count > 1)
                        _ = Dispatcher.InvokeAsync(() => ProportionStatusText.Text =
                            $"Reshaping piece {index + 1:N0} of {request.Pieces.Count:N0}: {piece.ObjectName}…");
                    var created = await _modMaterializer.CreateEditableAsync(
                        new ArkiveModAssetSelection(piece.CleanPackagePath, piece.PackageAlias, "SkeletalMesh", piece.ObjectName), token);
                    var glb = EditablePath(created) ?? throw new InvalidDataException($"{piece.ObjectName} has no working GLB.");
                    var bytes = await File.ReadAllBytesAsync(glb, token);
                    var model = BodyProportionEngine.Build(bytes, request.Follow, request.Profile, piece.ObjectName);
                    var reshaped = BodyProportionEngine.Apply(bytes, model, request.Settings);
                    if (request.Pieces.Count > 1 && reshaped.AsSpan().SequenceEqual(bytes)) unchanged.Add(created.EditId);
                    else
                    {
                        await File.WriteAllBytesAsync(glb, reshaped, token);
                        editIds[index] = created.EditId;
                    }
                    var count = Interlocked.Increment(ref finished);
                    if (request.Parallelism > 1)
                        _ = Dispatcher.InvokeAsync(() => ProportionStatusText.Text =
                            $"Reshaped {count:N0} of {request.Pieces.Count:N0} pieces ({request.Parallelism} at a time)…");
                    if (request.Pieces.Count > 1) _ = Dispatcher.InvokeAsync(() => Report(count));
                });
        }
        finally
        {
            _authoringDraft = await _modAuthoringWorkspace.SaveAsync(_authoringDraft! with
            {
                EditIds = _authoringDraft.EditIds.Concat(editIds.OfType<string>()).ToArray(),
            });
            if (!unchanged.IsEmpty)
            {
                Log($"Proportion pieces these settings leave unchanged were not added: {unchanged.Count:N0}.");
                await CleanUpReplacedProportionPiecesAsync(unchanged.ToArray());
            }
        }
    }

    /// <summary>
    /// How many pieces a body-wide run can reshape at once on this PC: half its processor cores, two to four. One on
    /// PCs with fewer than four cores, where it would only slow everything else down.
    /// </summary>
    private static int ProportionParallelism => Environment.ProcessorCount < 4 ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    /// <summary>How many times faster several at once is: measured 2.5 times with four on 60 Female Warrior pieces.</summary>
    private static double ProportionSpeedUp(int parallelism) => 1 + (parallelism - 1) * 0.5;

    private static string Duration(double minutes) =>
        minutes < 90 ? $"{Math.Max(1, Math.Round(minutes)):N0} minutes" : $"{minutes / 60:N1} hours";

    /// <summary>Reopens the Simple upscale mod open in Edit mods in the proportion editor, with its sliders.</summary>
    private async void AuthoringProportionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_authoringDraft?.ModId is not { } modId) return;
        await PersistAuthoringDraftAsync();
        await OpenUpscaleModInEditorAsync(modId, text => AuthoringStatusText.Text = text);
    }

    /// <summary>Edit Simple Upscale in a Mods row's menu: the same as Edit in proportion editor, from the list.</summary>
    private async void ModLibraryRowEditUpscaleItem_Click(object sender, RoutedEventArgs e)
    {
        if (ModLibraryGrid.SelectedItem is ModLibraryRow row && ModLibraryIdle)
            await OpenUpscaleModInEditorAsync(row.Entry.ModId, text => ModLibraryStatusText.Text = StatusText.Text = text);
    }

    /// <summary>Whether a mod can be reopened in the proportion editor: a Simple upscale mod with outfit pieces.</summary>
    private static bool IsEditableUpscaleMod(ArkiveModFileManifest manifest) =>
        BodyProportionModTags.Read(manifest.Tags, manifest.Description) is not null && UpscalePieces(manifest).Any();

    private static IEnumerable<ArkiveModFileAsset> UpscalePieces(ArkiveModFileManifest manifest) =>
        manifest.Assets.Where(asset => asset.Kind == UnifiedPackageAssetKind.SkeletalMesh && BodyProfiles.IsReshapeable(asset.Target.ObjectName));

    private bool CanOpenProportionEditor => _umodelBridge is not null && !_proportionBusy && !string.IsNullOrWhiteSpace(_libraryRootPath);

    /// <summary>
    /// The outfit a large upscale mod is previewed with in the editor: Nightwear (costume 142c, which every player
    /// body has and which shows the body's shape best) when the mod has it, otherwise its first one-piece dress, otherwise
    /// its first outfit with a top.
    /// </summary>
    internal static IReadOnlyList<string> PreviewOutfit(IReadOnlyList<string> pieceNames)
    {
        var outfits = pieceNames.Where(name => BodyProfiles.OutfitStem(name) is not null)
            .GroupBy(name => BodyProfiles.OutfitStem(name)!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(outfit => outfit.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var chosen = outfits.FirstOrDefault(outfit => outfit.Key.EndsWith("_av_142c", StringComparison.OrdinalIgnoreCase)) ??
                     outfits.FirstOrDefault(outfit => outfit.Any(name => BodyProfiles.PieceName(name) == "dress")) ??
                     outfits.FirstOrDefault(outfit => outfit.Any(name => BodyProfiles.PieceName(name) == "upper")) ??
                     outfits.FirstOrDefault();
        return chosen is null ? pieceNames.Take(1).ToArray() : chosen.Take(MaximumEditorPieces).ToArray();
    }

    /// <summary>
    /// Opens a saved Simple upscale mod in the proportion editor with the mod's sliders, from the clean game files;
    /// saving there updates the same mod, which Apply changes then puts in the game if it is enabled. A mod of up to
    /// <see cref="MaximumEditorPieces"/> pieces opens whole. A larger one opens one of its outfits as a preview, and
    /// saving reshapes all of it again: every outfit of its body for a body-wide mod (so outfits it left out, or new
    /// ones from a game update, fit the new shape too), otherwise its own pieces.
    /// </summary>
    private async Task OpenUpscaleModInEditorAsync(string modId, Action<string> report)
    {
        if (!CanOpenProportionEditor) return;
        _proportionBusy = true;
        UpdateAuthoringActions();
        try
        {
            var library = await _arkiveModLibrary.LoadAsync();
            var mod = library.Find(modId) ?? throw new InvalidOperationException("The mod is no longer in the library.");
            if (BodyProportionModTags.Read(mod.Manifest.Tags, mod.Manifest.Description, mod.Manifest.Name) is not { } settings)
                throw new InvalidOperationException("This mod was not made with Simple upscale.");
            var pieces = UpscalePieces(mod.Manifest).ToArray();
            if (pieces.Length == 0) throw new InvalidOperationException("This mod has no outfit pieces the proportion editor can open.");
            var large = settings.EveryOutfit || pieces.Length > MaximumEditorPieces;
            var shown = large
                ? PreviewOutfit(pieces.Select(asset => asset.Target.ObjectName).ToArray()).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            var requests = await PieceRequestsAsync(shown is null ? pieces : pieces.Where(asset => shown.Contains(asset.Target.ObjectName)));
            ProportionEditorWindow? window = null;
            window = new ProportionEditorWindow(ModelViewerService(), BodyProportionPieces(), requests,
                Path.Combine(_projectRoot, "cache", "webview2"), _effectiveAppearanceTheme,
                large ? request => SaveLargeUpscaleModAsync(request, window!, settings.EveryOutfit ? null : pieces) : SaveProportionModAsync,
                BodyProportionPresets(), Log)
            {
                Animations = FindViewerAnimationsAsync,
                Environments = FindViewerEnvironmentsAsync,
                InitialSettings = settings,
                EditingModId = modId,
                EditingModName = mod.Manifest.Name,
                EditingNote = !large ? null : settings.EveryOutfit
                    ? $"Previewing one outfit. Saving reshapes every {settings.Profile.DisplayName} outfit again."
                    : $"Previewing one outfit. Saving reshapes all {pieces.Length:N0} pieces of the mod again.",
                Owner = this,
            };
            window.Show();
            report($"Opened {mod.Manifest.Name} in the proportion editor. Saving there updates this mod.");
            Log($"Proportion editor opened for an upscale mod: mod={mod.Manifest.Name}, pieces={pieces.Length}, shown={requests.Count}, " +
                $"every outfit={settings.EveryOutfit}, settings=" +
                string.Join(", ", settings.Settings.Select(pair => $"{pair.Key}={pair.Value}")) + $", follow={settings.Follow}.");
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            report($"The proportion editor could not open this mod: {exception.Message}");
            Log($"Proportion editor could not open an upscale mod: {exception}");
        }
        finally
        {
            _proportionBusy = false;
            UpdateAuthoringActions();
            UpdateProportionActions();
        }
    }

    /// <summary>A mod's pieces read from their clean game packages.</summary>
    private async Task<List<BodyProportionPieceRequest>> PieceRequestsAsync(IEnumerable<ArkiveModFileAsset> pieces)
    {
        var requests = new List<BodyProportionPieceRequest>();
        foreach (var asset in pieces)
        {
            var gamePackage = Path.Combine(_libraryRootPath!, asset.Target.PhysicalPackageName);
            if (!File.Exists(gamePackage))
                throw new FileNotFoundException($"{asset.Target.PhysicalPackageName} ({asset.Target.PackageAlias}) is not in the game folder.");
            var clean = await _arkiveModApply.ResolveCleanPackageAsync(gamePackage);
            requests.Add(new BodyProportionPieceRequest(clean, asset.Target.PackageAlias, asset.Target.ObjectName, _libraryRootPath!));
        }
        return requests;
    }

    /// <summary>
    /// Saving a large upscale mod from the editor: asks as Apply preset to every outfit does, then reshapes every
    /// outfit of the body (ownPieces null) or the mod's own pieces into the same mod. That runs on after this returns,
    /// with progress and Stop in Simple upscale, so the editor can be closed meanwhile.
    /// </summary>
    private async Task<string> SaveLargeUpscaleModAsync(ProportionSaveRequest request, Window owner, IReadOnlyList<ArkiveModFileAsset>? ownPieces)
    {
        if (_proportionBusy) throw new InvalidOperationException("Arkive is already reshaping outfits. Try again when it finishes.");
        if (_authoringBusy || _modLibraryBusy || _guidedTextureModInProgress || _modSetBusy || _workshopBusy)
            throw new InvalidOperationException("Arkive is busy with another mod task. Try again when it finishes.");
        _proportionBusy = true;
        UpdateProportionActions();
        var running = false;
        try
        {
            List<BodyProportionPieceRequest> requests;
            int? parallelism;
            if (ownPieces is null)
            {
                var found = await FindEveryOutfitAsync(request.Profile);
                if (found.Length == 0) return $"No {request.Profile.DisplayName} outfits were found in the index. Nothing was changed.";
                parallelism = ConfirmUpscaleBatch(owner, "Update every outfit",
                    $"This reshapes all {OutfitCount(found.Select(match => match.ObjectName)):N0} {request.Profile.DisplayName} outfits " +
                    $"({found.Length:N0} pieces) with these sliders and updates {request.ModName} with them.", found.Length);
                requests = parallelism is null ? [] : await CleanPieceRequestsAsync(found);
            }
            else
            {
                parallelism = ConfirmUpscaleBatch(owner, "Update the mod",
                    $"This reshapes all {ownPieces.Count:N0} pieces of {request.ModName} with these sliders and updates the mod.", ownPieces.Count);
                requests = parallelism is null ? [] : await PieceRequestsAsync(ownPieces);
            }
            if (parallelism is null) return "Nothing was changed.";
            Log($"Large upscale mod update started: mod={request.ModName}, body={request.Profile.Id}, pieces={requests.Count}, " +
                $"every outfit={ownPieces is null}, at once={parallelism}.");
            running = true;
            _ = FinishLargeUpscaleAsync(request with { Pieces = requests, Parallelism = parallelism.Value, EveryOutfit = ownPieces is null });
            return $"Updating {request.ModName}: reshaping {requests.Count:N0} pieces. Progress and Stop are in Simple upscale, and " +
                   "this window can be closed. When it finishes, choose Apply changes in Mods if the mod is on.";
        }
        finally
        {
            if (!running)
            {
                _proportionBusy = false;
                UpdateProportionActions();
            }
        }
    }

    private async Task FinishLargeUpscaleAsync(ProportionSaveRequest request)
    {
        try
        {
            ProportionStatusText.Text = $"Updating {request.ModName}…";
            ProportionStatusText.Text = StatusText.Text = await RunUpscaleBatchAsync(request, $"{request.ModName} was not updated");
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
        var skipped = new List<string>();
        var replaced = request.ExistingModId is { } existing
            ? await ProportionPieceEditIdsAsync(existing, request.Pieces.Select(piece => piece.ObjectName))
            : [];
        await RunAuthoringOperationAsync(async () =>
        {
            try { saved = await CreateProportionModAsync(request, skipped); }
            catch (Exception exception) { failure = exception; throw; }
            finally { ProportionProgressPanel.Visibility = Visibility.Collapsed; }
        }, busyTitle: null);
        if (failure is not null) throw failure;
        if (saved is null) throw new InvalidOperationException(AuthoringStatusText.Text);
        await RefreshArkiveModsAsync("proportion mod saved", saved.ModId);
        await CleanUpReplacedProportionPiecesAsync(replaced);
        var result = request.ExistingModId is null
            ? $"Saved {saved.FileName}. Enable it in Mods to apply it to the game; it is also open in Edit mods."
            : $"Updated {saved.FileName}. If it is enabled, apply it again in Mods to put the new shape in the game.";
        if (skipped.Count > 0)
            result += $" {skipped.Count:N0} piece(s) were left out because they failed the mesh check with these settings " +
                      $"(see the session log): {string.Join(", ", skipped.Take(5).Select(item => item.Split(':')[0]))}" +
                      (skipped.Count > 5 ? ", …" : "") + ".";
        return result;
    }

    private async Task<ArkiveModLibraryEntry> CreateProportionModAsync(ProportionSaveRequest request, List<string> skipped)
    {
        await PersistAuthoringDraftAsync();
        var modSettings = new BodyProportionModSettings(request.Profile, request.Settings, request.Follow, request.EveryOutfit);
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
        await ReshapePiecesAsync(request);
        request.Cancellation.ThrowIfCancellationRequested();
        IProgress<ArkiveModSaveProgress>? progress = null;
        if (request.Pieces.Count > 1)
        {
            ProportionStatusText.Text = $"Checking all {request.Pieces.Count:N0} pieces against the game's skeleton and animations " +
                                        "and saving them as one mod. This is the longest step…";
            var started = DateTime.UtcNow;
            var stage = ArkiveModSaveStage.Checking;
            progress = new Progress<ArkiveModSaveProgress>(item =>
            {
                if (item.Stage != stage) (stage, started) = (item.Stage, DateTime.UtcNow);
                ShowProportionProgress(item.Stage switch
                {
                    ArkiveModSaveStage.Checking => $"Step 2 of 2 · Checking pieces against the game's skeleton and animations: {item.Done:N0} of {item.Total:N0}",
                    ArkiveModSaveStage.Collecting => $"Step 2 of 2 · Collecting pieces for the mod: {item.Done:N0} of {item.Total:N0}",
                    _ => "Step 2 of 2 · Writing the mod file…",
                }, item.Done, item.Total, started);
            });
        }
        // A batch keeps going past a piece these settings can't reshape safely; a single outfit reports it instead.
        var saved = await SaveAuthoringAsync(request.Pieces.Count > 1 ? skipped : null, request.Parallelism, progress);
        foreach (var piece in skipped) Log($"Proportion piece left out of the mod (failed the mesh check): {piece}");
        Log($"Proportion mod saved: name={request.ModName}, body={request.Profile.Id}, pieces={request.Pieces.Count}, " +
            $"settings={string.Join(", ", request.Settings.Where(pair => pair.Value != 0).Select(pair => $"{pair.Key}={pair.Value}"))}, " +
            $"follow={request.Follow}.");
        return saved;
    }
}
