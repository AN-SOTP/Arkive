using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Inspection;
using Arkive.Core.Mods;
using Arkive.Core.Outfits;
using Arkive.Core.Proportions;

namespace Arkive.App;

/// <summary>
/// Outfit swap tab: finds player outfits, lets the player pick one to wear and one to replace (any two bodies), opens
/// the swap preview and saves the result as a mod through the normal mod authoring route.
/// </summary>
public partial class MainWindow
{
    /// <summary>One player outfit: its pieces Arkive swaps (a dress, or a top and pants), grouped by the name they share.</summary>
    private sealed record OutfitSwapRow(string Stem, BodyProfile Profile, IReadOnlyList<LibraryAssetRow> Pieces)
    {
        public string Body => Profile.DisplayName;
        public string Parts => string.Join(" + ", Pieces.Select(piece => OutfitSwapBodies.Part(piece.ObjectName)));
        public string InGameNames => string.Join(", ", Pieces.Select(piece => piece.InGameNames).Where(name => name.Length > 0).Distinct());
    }

    private OutfitSwapPackageLibrary? _outfitSwapLibrary;
    private string? _outfitSwapLibraryRoot;
    private bool _outfitSwapBusy;

    private OutfitSwapPackageLibrary OutfitSwapLibrary()
    {
        if (_outfitSwapLibrary is null || _outfitSwapLibraryRoot != _libraryRootPath)
        {
            _outfitSwapLibraryRoot = _libraryRootPath;
            _outfitSwapLibrary = new OutfitSwapPackageLibrary(_packageIndexStore, _libraryRootPath!, _reader, _protectionProvider,
                _skeletalMeshReconnaissanceService, _skeletalMeshReconnaissanceWorkspaceStore, _skeletalMeshEditableExportService,
                Path.Combine(_projectRoot, "cache", "outfit-swap", "pieces"),
                (package, token) => _arkiveModApply.ResolveCleanPackageAsync(package, token));
        }
        return _outfitSwapLibrary;
    }

    private async void OutfitSwapWearSearchButton_Click(object sender, RoutedEventArgs e) =>
        await SearchOutfitsAsync(OutfitSwapWearSearchTextBox, OutfitSwapWearGrid);

    private async void OutfitSwapReplaceSearchButton_Click(object sender, RoutedEventArgs e) =>
        await SearchOutfitsAsync(OutfitSwapReplaceSearchTextBox, OutfitSwapReplaceGrid);

    private async void OutfitSwapWearSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchOutfitsAsync(OutfitSwapWearSearchTextBox, OutfitSwapWearGrid);
    }

    private async void OutfitSwapReplaceSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchOutfitsAsync(OutfitSwapReplaceSearchTextBox, OutfitSwapReplaceGrid);
    }

    private void OutfitSwapGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOutfitSwapActions();

    private void UpdateOutfitSwapActions()
    {
        var wear = OutfitSwapWearGrid.SelectedItem as OutfitSwapRow;
        var replace = OutfitSwapReplaceGrid.SelectedItem as OutfitSwapRow;
        OutfitSwapPreviewButton.IsEnabled = !_outfitSwapBusy && wear is not null && replace is not null && wear.Stem != replace.Stem;
        OutfitSwapChoiceText.Text = wear is null || replace is null
            ? "Choose an outfit to wear on the left and the outfit it replaces on the right."
            : $"Wear {wear.Stem} ({wear.Body}) in place of {replace.Stem} ({replace.Body})" +
              (wear.Profile.Female != replace.Profile.Female ? ". Male and female bodies are experimental." : ".");
    }

    private async Task SearchOutfitsAsync(TextBox box, DataGrid grid)
    {
        if (_outfitSwapBusy) return;
        if (string.IsNullOrWhiteSpace(_libraryRootPath))
        {
            OutfitSwapStatusText.Text = "Choose or restore the indexed Packages folder in Asset library first.";
            return;
        }
        var query = box.Text.Trim();
        if (query.Length == 0)
        {
            OutfitSwapStatusText.Text = "Enter part of an outfit's in-game name or an object name, for example Anniversary or pc_wbk_f_hr.";
            return;
        }
        _outfitSwapBusy = true;
        UpdateOutfitSwapActions();
        OutfitSwapStatusText.Text = $"Searching player outfits for {query}…";
        try
        {
            var technical = query.Contains('_') || query.Any(char.IsDigit);
            var scope = technical || _gameMetadataIndex is null ? PackageIndexSearchScope.ObjectAndInGameName : PackageIndexSearchScope.InGameName;
            var result = await _packageIndexStore.SearchAsync(_libraryRootPath, query, "SkeletalMesh", _gameMetadataIndex, scope, null, resultLimit: 2000);
            if (result.TotalMatches == 0 && scope == PackageIndexSearchScope.InGameName)
                result = await _packageIndexStore.SearchAsync(_libraryRootPath, query, "SkeletalMesh", _gameMetadataIndex,
                    PackageIndexSearchScope.ObjectName, null, resultLimit: 2000);
            var rows = result.Matches
                .Where(match => OutfitSwapBodies.Part(match.ObjectName) is not null &&
                                BodyProfiles.ForObjectName(OutfitSwapBodies.StripLocale(match.ObjectName)) is not null)
                .Select(match => new LibraryAssetRow(match.SourcePath, match.PackageAlias, match.PhysicalPackageName, match.PackageAssetCount,
                    match.ExportIndex, match.Kind, match.ClassName, match.ObjectName, match.SerialSize, match.SerialOffset, match.GameMetadata))
                .GroupBy(row => OutfitSwapBodies.OutfitStem(row.ObjectName)!, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    // One piece per part; the worn international version (_loc_int) wins over its plain twin.
                    var pieces = group.GroupBy(row => OutfitSwapBodies.Part(row.ObjectName))
                        .Select(part => part.OrderByDescending(row => row.ObjectName.EndsWith("_loc_int", StringComparison.OrdinalIgnoreCase)).First())
                        .ToArray();
                    // A dress is the whole outfit; otherwise the top and the pants.
                    var dress = pieces.FirstOrDefault(row => OutfitSwapBodies.Part(row.ObjectName) == "dress");
                    var chosen = dress is not null ? [dress] : pieces.Where(row => OutfitSwapBodies.Part(row.ObjectName) is "upper" or "lower")
                        .OrderBy(row => OutfitSwapBodies.Part(row.ObjectName) == "lower").ToArray();
                    return new OutfitSwapRow(group.Key, BodyProfiles.ForObjectName(OutfitSwapBodies.StripLocale(chosen[0].ObjectName))!, chosen);
                })
                .OrderBy(row => row.Stem, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            grid.ItemsSource = rows;
            OutfitSwapStatusText.Text = rows.Length == 0
                ? $"No player outfits match {query}."
                : $"{rows.Length:N0} outfit(s) match {query}.";
            Log($"Outfit swap search: query={query}, scope={scope}, matches={result.TotalMatches}, outfits={rows.Length}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            OutfitSwapStatusText.Text = "The search failed safely. See the session log.";
            Log($"Outfit swap search failed: query={query}, error={exception}");
        }
        finally
        {
            _outfitSwapBusy = false;
            UpdateOutfitSwapActions();
        }
    }

    private async void OutfitSwapPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (OutfitSwapWearGrid.SelectedItem is not OutfitSwapRow wear || OutfitSwapReplaceGrid.SelectedItem is not OutfitSwapRow replace ||
            _umodelBridge is null || string.IsNullOrWhiteSpace(_libraryRootPath)) return;
        try
        {
            var window = new OutfitSwapWindow(ModelViewerService(), OutfitSwapLibrary(),
                new OutfitSwapService(OutfitSwapLibrary(), Path.Combine(_projectRoot, "cache", "outfit-swap")),
                wear.Pieces.Select(piece => piece.ObjectName).ToArray(), replace.Pieces.Select(piece => piece.ObjectName).ToArray(),
                await OutfitSwapUpscaleChoicesAsync(replace),
                Path.Combine(_projectRoot, "cache", "outfit-swap", "work"), Path.Combine(_projectRoot, "cache", "webview2"),
                _effectiveAppearanceTheme, await CurrentGameBuildAsync() ?? "unknown", SaveOutfitSwapModAsync, Log)
            {
                Animations = FindViewerAnimationsAsync,
                Environments = FindViewerEnvironmentsAsync,
                Owner = this,
            };
            window.Show();
            OutfitSwapStatusText.Text = $"Opened the swap of {wear.Stem} onto {replace.Stem}. Save it as a mod from that window.";
            Log($"Outfit swap opened: wear={string.Join(", ", wear.Pieces.Select(piece => piece.ObjectName))}, " +
                $"replace={string.Join(", ", replace.Pieces.Select(piece => piece.ObjectName))}.");
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            OutfitSwapStatusText.Text = $"Could not open the swap: {exception.Message}";
            Log($"Outfit swap could not open: {exception}");
        }
    }

    /// <summary>
    /// The body shapes offered for a swap: the player's Simple upscale mods that reshape the outfit being replaced (so the
    /// swapped outfit keeps the same shape), none, and the saved presets of its body. The upscale applied in game comes
    /// first, so a swap follows it unless the player picks otherwise: the swap replaces those pieces, and without it the
    /// body would go back to the game's shape.
    /// </summary>
    private async Task<IReadOnlyList<OutfitSwapUpscaleChoice>> OutfitSwapUpscaleChoicesAsync(OutfitSwapRow replace)
    {
        var choices = new List<OutfitSwapUpscaleChoice>();
        var pieces = replace.Pieces.Select(piece => piece.ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var library = await _arkiveModLibrary.LoadAsync();
        foreach (var mod in library.Mods.OrderByDescending(mod => mod.Enabled).ThenByDescending(mod => mod.Priority))
        {
            if (!mod.Enabled && !choices.Any(choice => choice.Settings is null)) choices.Add(new("Game body (no reshaping)", null));
            if (!mod.Manifest.Assets.Any(asset => pieces.Contains(asset.Target.ObjectName))) continue;
            if (BodyProportionModTags.Read(mod.Manifest.Tags, mod.Manifest.Description) is { } settings && settings.Profile.Id == replace.Profile.Id)
                choices.Add(new OutfitSwapUpscaleChoice($"Your mod: {mod.Manifest.Name}" + (mod.Enabled ? "" : " (off)"), settings));
        }
        if (!choices.Any(choice => choice.Settings is null)) choices.Add(new("Game body (no reshaping)", null));
        foreach (var preset in BodyProportionPresets().Load(replace.Profile.Id))
            choices.Add(new OutfitSwapUpscaleChoice($"Preset: {preset.Name}",
                new BodyProportionModSettings(replace.Profile, preset.Settings, preset.Follow)));
        return choices;
    }

    /// <summary>
    /// Saves a swap as a new mod: the replaced pieces get the new geometry, their own pictures the repainted images and
    /// their materials the new colour values; saving then checks and builds them like any other mod.
    /// </summary>
    private async Task<string> SaveOutfitSwapModAsync(OutfitSwapSaveRequest request)
    {
        if (_authoringBusy || _modLibraryBusy || _guidedTextureModInProgress || _modSetBusy || _workshopBusy)
            throw new InvalidOperationException("Arkive is busy with another mod task. Try again when it finishes.");
        ArkiveModLibraryEntry? saved = null;
        Exception? failure = null;
        await RunAuthoringOperationAsync(async () =>
        {
            try { saved = await CreateOutfitSwapModAsync(request); }
            catch (Exception exception) { failure = exception; throw; }
        }, busyTitle: null);
        if (failure is not null) throw failure;
        if (saved is null) throw new InvalidOperationException(AuthoringStatusText.Text);
        // The swap replaces whole pieces that other mods (a Simple upscale over every outfit) may change too: it goes above
        // them, or the game would show theirs instead. The chosen upscale is already in its shape.
        var overlapping = await _arkiveModLibrary.LoadAsync();
        await _arkiveModLibrary.SetPriorityAsync(saved.ModId, ArkiveModConflictResolver.Resolve(overlapping.Mods).PriorityToWinAll(saved.ModId));
        await RefreshArkiveModsAsync("outfit swap saved", saved.ModId);
        var text = $"Saved {saved.FileName}. Enable it in Mods to apply it to the game; it is also open in Edit mods.";
        if (request.Upscale.Settings is not null && request.Upscale.Label.StartsWith("Your mod:", StringComparison.Ordinal))
            text += " It is shaped by your Simple upscale mod and placed above it in Mods, so keep that one where it is or turn it off.";
        return text;
    }

    private async Task<ArkiveModLibraryEntry> CreateOutfitSwapModAsync(OutfitSwapSaveRequest request)
    {
        await PersistAuthoringDraftAsync();
        var report = request.Report;
        var description = $"Outfit swap: {string.Join(", ", request.Donors)} ({report.From.DisplayName}) worn in place of " +
                          $"{string.Join(", ", request.Targets.Select(target => target.ObjectName))} ({report.To.DisplayName})" +
                          (request.Upscale.Settings is null ? "." : $", shaped by {request.Upscale.Label}.");
        _authoringDraft = await _modAuthoringWorkspace.CreateAsync(new ArkiveModMetadata(request.ModName, "1.0.0", "", description,
            Tags: ["outfit-swap", $"outfit-swap:from={report.From.Id}", $"outfit-swap:to={report.To.Id}"]));
        LoadAuthoringMetadata();

        // Every asset the swap changes: the replaced pieces, their repainted pictures and their materials.
        var rows = new List<(LibraryAssetRow Row, Func<ArkiveModLocalEdit, CancellationToken, Task> Write)>();
        var writer = new OutfitSwapModWriter(_arkiveModEdits, _protectionProvider, Path.Combine(_projectRoot, "staging", "material-candidates"));
        for (var i = 0; i < request.Targets.Count; i++)
        {
            var source = request.Targets[i];
            var mesh = request.Meshes[i];
            var row = await OutfitSwapRowAsync(source.CleanPackagePath, source.ObjectName, "SkeletalMesh");
            // The meshes are already reshaped by the chosen body shape in the preview.
            rows.Add((row, (edit, token) => writer.WriteMeshAsync(edit, mesh, null, token)));
        }
        var packages = request.TargetScenes.SelectMany(scene => scene.Scene.ObjectPackages ?? new Dictionary<string, string>())
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (texture, picture) in request.Pictures.Pictures)
        {
            if (!packages.TryGetValue(texture, out var package)) throw new InvalidDataException($"Arkive cannot tell which package holds {texture}.");
            var row = await OutfitSwapRowAsync(Path.Combine(_libraryRootPath!, package), texture, "Texture2D");
            rows.Add((row, (edit, token) => writer.WritePictureAsync(edit, picture, token)));
        }
        foreach (var (material, values) in request.Pictures.Materials)
        {
            if (!packages.TryGetValue(material, out var package)) throw new InvalidDataException($"Arkive cannot tell which package holds {material}.");
            var row = await OutfitSwapRowAsync(Path.Combine(_libraryRootPath!, package), material, "MaterialInstanceConstant");
            rows.Add((row, (edit, token) => writer.WriteMaterialAsync(edit, values, token)));
        }

        // Already inside the save's authoring operation, so the assets are added here rather than through another one.
        var session = await CreateAuthoringAddSessionAsync(CancellationToken.None);
        for (var index = 0; index < rows.Count; index++)
        {
            request.Progress?.Report($"Adding the swapped outfit's pieces, pictures and materials ({index + 1} of {rows.Count})");
            var edit = await AddAuthoringAssetAsync(rows[index].Row, session);
            await rows[index].Write(edit, CancellationToken.None);
        }
        request.Progress?.Report("Checking the meshes against the game's skeleton and animations and rebuilding the pictures");
        var saved = await SaveAuthoringAsync();
        Log($"Outfit swap mod saved: name={request.ModName}, from={report.From.Id}, to={report.To.Id}, pieces={request.Targets.Count}, " +
            $"pictures={request.Pictures.Pictures.Count}, materials={request.Pictures.Materials.Count}, shape={request.Upscale.Label}.");
        return saved;
    }

    /// <summary>A library row for one export of a package, read from the package itself.</summary>
    private async Task<LibraryAssetRow> OutfitSwapRowAsync(string packagePath, string objectName, string className)
    {
        var catalogue = await Task.Run(() => _catalogReader.Read(packagePath));
        var export = catalogue.Assets.FirstOrDefault(asset => asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase) &&
                                                              asset.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidDataException($"{objectName} is not in {Path.GetFileName(packagePath)}.");
        return new LibraryAssetRow(packagePath, catalogue.Summary.FolderName, Path.GetFileName(packagePath), catalogue.Assets.Count,
            export.ExportIndex, export.Kind, export.ClassName, export.ObjectName, export.SerialSize, export.SerialOffset, []);
    }
}
