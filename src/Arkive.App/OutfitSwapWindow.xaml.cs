using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Arkive.Core.Outfits;
using Arkive.Core.Proportions;
using Arkive.Core.Viewer;
using Microsoft.Web.WebView2.Core;

namespace Arkive.App;

/// <summary>A body shape the swapped outfit can take: none, the player's Simple upscale mod for the replaced outfit, or a preset.</summary>
public sealed record OutfitSwapUpscaleChoice(string Label, BodyProportionModSettings? Settings);

/// <summary>What the outfit swap window asks Arkive to save as a mod.</summary>
public sealed record OutfitSwapSaveRequest(
    string ModName,
    IReadOnlyList<string> Donors,
    IReadOnlyList<OutfitSwapSource> Targets,
    IReadOnlyList<OutfitSwapMesh> Meshes,
    OutfitSwapPictureResult Pictures,
    IReadOnlyList<OutfitSwapScene> TargetScenes,
    OutfitSwapReport Report,
    OutfitSwapUpscaleChoice Upscale)
{
    /// <summary>Where saving reports its steps (the window's status line).</summary>
    public IProgress<string>? Progress { get; init; }

    /// <summary>The outfit keeps its own materials (nothing is repainted).</summary>
    public bool OwnMaterials { get; init; }

    /// <summary>The replaced pieces as the game has them (their own material slots), in the order of <see cref="Targets"/>.</summary>
    public IReadOnlyList<OutfitSwapPiece> TargetPieces { get; init; } = [];
}

/// <summary>
/// Arkive's model viewer showing one player outfit swapped onto another body's outfit: fits it, bakes the donor's
/// colours with the game's shaders, repaints the replaced outfit's pictures, previews the result with the target body's
/// face, hair and animations, and hands it to Arkive to save as a mod.
/// </summary>
public partial class OutfitSwapWindow : Window
{
    private const string ViewerHost = "viewer.arkive.local";
    private const string SceneHost = "scenes.arkive.local";

    private readonly ModelViewerSceneService _scenes;
    private readonly OutfitSwapPackageLibrary _library;
    private readonly OutfitSwapService _service;
    private readonly IReadOnlyList<string> _donorNames;
    private readonly IReadOnlyList<string> _targetNames;
    private readonly string _workDirectory;
    private readonly string _webViewDataFolder;
    private readonly string _theme;
    private readonly string _gameBuild;
    private readonly Func<OutfitSwapSaveRequest, Task<string>> _save;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _sceneLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private BakeJob? _bake;

    private IReadOnlyList<OutfitSwapPiece> _targets = [];
    private IReadOnlyList<OutfitSwapScene> _targetScenes = [];
    private IReadOnlyList<OutfitSwapSource> _targetSources = [];
    private IReadOnlyList<string> _targetGlbs = [];
    private IReadOnlyList<ModelViewerRequest> _heads = [];
    private OutfitSwapFit? _fit;
    private OutfitSwapPictureResult? _pictures;
    private IReadOnlyList<OutfitSwapMesh> _shown = [];
    private IReadOnlyList<ModelViewerSceneResult> _materialScenes = [];
    private bool _shownOnce;
    private bool _busy;

    private sealed class BakeJob(string id, string directory)
    {
        public string Id { get; } = id;
        public string Directory { get; } = directory;
        public Dictionary<int, string> Files { get; } = [];
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public OutfitSwapWindow(
        ModelViewerSceneService scenes,
        OutfitSwapPackageLibrary library,
        OutfitSwapService service,
        IReadOnlyList<string> donors,
        IReadOnlyList<string> targets,
        IReadOnlyList<OutfitSwapUpscaleChoice> upscale,
        string workRoot,
        string webViewDataFolder,
        string effectiveTheme,
        string gameBuild,
        Func<OutfitSwapSaveRequest, Task<string>> save,
        Action<string> log)
    {
        _scenes = scenes;
        _library = library;
        _service = service;
        _donorNames = donors;
        _targetNames = targets;
        _workDirectory = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
        _webViewDataFolder = webViewDataFolder;
        _theme = effectiveTheme.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        _gameBuild = gameBuild;
        _save = save;
        _log = log;
        InitializeComponent();
        var donorStem = OutfitSwapBodies.OutfitStem(donors[0]) ?? donors[0];
        var targetStem = OutfitSwapBodies.OutfitStem(targets[0]) ?? targets[0];
        Title = $"Arkive outfit swap · {donorStem} onto {targetStem}";
        ModNameTextBox.Text = $"{donorStem} on {targetStem}";
        UpscaleComboBox.ItemsSource = upscale;
        UpscaleComboBox.SelectedIndex = 0;
        SourceInitialized += (_, _) => AppearanceManager.ApplyWindowChrome(this, effectiveTheme);
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            _closing.Cancel();
            try { Directory.Delete(_workDirectory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        };
    }

    /// <summary>Prepares the game's environment cubemaps (null when unavailable); set by Arkive's main window.</summary>
    public Func<CancellationToken, Task<ModelViewerEnvironmentSet?>>? Environments { get; init; }

    /// <summary>Finds the animations a mesh can play (null when it has none); set by Arkive's main window.</summary>
    public Func<ModelViewerRequest, CancellationToken, Task<ModelViewerAnimationOffer?>>? Animations { get; init; }

    /// <summary>
    /// The outfit keeps its own materials: the replaced pieces draw it with the donor's game materials (added to their
    /// package when the mod is built) instead of repainting their own pictures. Experimental.
    /// </summary>
    public bool OwnMaterials { get; init; }

    /// <summary>The replaced pieces the game wears as inner avatars (<see cref="OutfitInnerAvatars"/>); set by Arkive's main window.</summary>
    public IReadOnlySet<string> InnerAvatars { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private string SceneCacheRoot => _scenes.Cache.RootDirectory;

    private void Status(string text) => StatusText.Text = text;

    private void Post(object message)
    {
        if (Browser.CoreWebView2 is not null)
            Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private async Task StartAsync()
    {
        if (!await InitializeBrowserAsync()) return;
        var token = _closing.Token;
        try
        {
            Directory.CreateDirectory(_workDirectory);
            var donors = new List<(OutfitSwapPiece Piece, OutfitSwapScene Scene, ModelViewerSceneResult Result)>();
            foreach (var name in _donorNames) donors.Add(await LoadAsync(name, token));
            var targets = new List<(OutfitSwapPiece Piece, OutfitSwapScene Scene, ModelViewerSceneResult Result)>();
            var sources = new List<OutfitSwapSource>();
            var glbs = new List<string>();
            foreach (var name in _targetNames)
            {
                targets.Add(await LoadAsync(name, token));
                sources.Add((await _library.LocateAsync(name, token))!);
                glbs.Add((await _library.EditableGlbAsync(name, token))!);
            }
            _targets = targets.Select(item => item.Piece).ToArray();
            _targetScenes = targets.Select(item => item.Scene).ToArray();
            _targetSources = sources;
            _targetGlbs = glbs;

            var progress = new Progress<string>(Status);
            // The game materials the swap can draw with: the outfit's own (when kept) and the body's skin.
            Status("Finding the outfit's materials…");
            var body = OutfitSwapBodies.Detect(_targets[0].Mesh, _targets[0].ObjectName) is { } profile ? OutfitSwapBodies.For(profile) : null;
            var materials = await OutfitSwapMaterialSources.ReadAsync(_library, donors.Select(item => item.Piece).ToArray(), body, token)
                with { KeepOwn = OwnMaterials, InnerAvatarPieces = InnerAvatars };
            // The preview draws added slots with these scenes' materials: the outfit's own and the body's skin.
            var materialScenes = donors.Select(item => item.Result).ToList();
            foreach (var name in body?.BaseBody ?? [])
                if (await _library.LocateAsync(name, token) is { } baseBody)
                    materialScenes.Add(await Task.Run(() => _scenes.PrepareAsync(baseBody.ViewerRequest, null, token), token));
            _materialScenes = materialScenes;
            if (OwnMaterials)
            {
                _fit = await Task.Run(() => _service.FitAsync(donors.Select(item => item.Piece).ToArray(), _targets, _gameBuild, progress, token,
                    materials), token);
                _pictures = new OutfitSwapPictureResult(new Dictionary<string, string>(), new Dictionary<string, ModelViewerMaterialEdit>());
                await FinishAsync(token);
                return;
            }
            _fit = await Task.Run(() => _service.FitAsync(donors.Select(item => item.Piece).ToArray(), _targets, _gameBuild, progress, token,
                materials), token);

            // The donor as the game's shaders draw it: its colours, its lit colours and its roughness and metalness.
            Status("Baking the outfit's colours with the game's shaders…");
            var combined = BodyProportionSceneCombiner.Combine(donors.Select(item => item.Result).ToArray(), SceneCacheRoot);
            await LoadSceneAsync(combined.SceneJson, keepView: false, token);
            var bakedByPiece = ByPiece(combined, await BakeAsync(Path.Combine(_workDirectory, "colours"), token));
            var litByPiece = ByPiece(combined, await BakeAsync(Path.Combine(_workDirectory, "lit-colours"), token, "lit"));
            var surfaceByPiece = ByPiece(combined, await BakeAsync(Path.Combine(_workDirectory, "surfaces"), token, "surface"));

            Status("Painting the outfit into the replaced outfit's pictures…");
            var firstPainting = await Task.Run(() => OutfitSwapPictures.PaintAsync(_fit.Plan, donors.Select(item => item.Piece).ToArray(),
                donors.Select(item => item.Scene).ToArray(), bakedByPiece, _targetScenes, Path.Combine(_workDirectory, "first-pictures"), token), token);

            // Each outfit shader lights and shines its pictures in its own way: bake the first painting as the replaced
            // outfit draws it, learn that, and paint again so the colours and sheen come out as the original's.
            Status("Matching the colours and sheen to the original…");
            _pictures = firstPainting;
            var painted = new List<ModelViewerSceneResult>();
            for (var i = 0; i < _targets.Count; i++) painted.Add(await PrepareTargetAsync(i, upscale: null, token));
            var paintedCombined = BodyProportionSceneCombiner.Combine(painted, SceneCacheRoot);
            await LoadSceneAsync(paintedCombined.SceneJson, keepView: false, token);
            var paintedLit = ByPiece(paintedCombined, await BakeAsync(Path.Combine(_workDirectory, "painted-lit"), token, "lit"));
            var paintedSurface = ByPiece(paintedCombined, await BakeAsync(Path.Combine(_workDirectory, "painted-surfaces"), token, "surface"));
            var wanted = await Task.Run(() => OutfitSwapPictures.PaintAsync(_fit.Plan, donors.Select(item => item.Piece).ToArray(),
                donors.Select(item => item.Scene).ToArray(), litByPiece, _targetScenes, Path.Combine(_workDirectory, "wanted"), token), token);
            var calibration = await Task.Run(() => OutfitSwapCalibration.LearnAsync(wanted.Pictures, firstPainting.Pictures, _targetScenes,
                paintedLit, paintedSurface, token), token);
            _pictures = await Task.Run(() => OutfitSwapPictures.PaintAsync(_fit.Plan, donors.Select(item => item.Piece).ToArray(),
                donors.Select(item => item.Scene).ToArray(), bakedByPiece, _targetScenes, Path.Combine(_workDirectory, "pictures"), token,
                paintedLit, calibration, surfaceByPiece), token);
            await FinishAsync(token);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status($"Could not swap the outfits: {exception.Message}");
            _log($"Outfit swap failed: {exception}");
        }
    }

    /// <summary>Shows the fitted swap with the new body's face and hair, and lets it be reshaped and saved.</summary>
    private async Task FinishAsync(CancellationToken token)
    {
        var fit = _fit!;
        var heads = new List<ModelViewerRequest>();
        if (OutfitSwapBodies.For(fit.Report.To) is { } body)
            foreach (var name in body.Head)
                if (await _library.LocateAsync(name, token) is { } head) heads.Add(head.ViewerRequest);
        _heads = heads;
        await ShowResultAsync(token);
        _ = OfferAnimationsAsync(_targetSources[0].ViewerRequest, token);
        UpscaleComboBox.IsEnabled = true;
        SaveButton.IsEnabled = true;
        Status(Describe(fit.Report));
        _log($"Outfit swap ready{(OwnMaterials ? " (own materials)" : "")}: {string.Join(", ", _donorNames)} onto {string.Join(", ", _targetNames)}; " +
             $"{fit.Report.From.Id} -> {fit.Report.To.Id}, costumes={fit.Report.LearnedCostumes}, " +
             $"clipping={fit.Report.ClippingBeforePercent:N2}%->{fit.Report.ClippingPercent:N2}%, " +
             $"stretched={fit.Report.StretchedPercent:N2}%, notes={string.Join(" | ", fit.Report.Notes)}.");
    }

    private static string Describe(OutfitSwapReport report)
    {
        var text = $"{report.From.DisplayName} outfit fitted to {report.To.DisplayName}" +
                   (report.LearnedCostumes > 0 ? $" (learned from {report.LearnedCostumes} costumes both bodies share)" : "") +
                   $". Cloth inside the body: {report.ClippingPercent:N2}%. Stretched faces: {report.StretchedPercent:N2}%.";
        if (report.CrossesSex) text += " Male and female bodies differ a lot; check the shape closely (experimental).";
        if (report.Notes.Count > 0) text += " " + string.Join(" ", report.Notes);
        return text;
    }

    private async Task<(OutfitSwapPiece Piece, OutfitSwapScene Scene, ModelViewerSceneResult Result)> LoadAsync(string name, CancellationToken token)
    {
        Status($"Reading {name}…");
        var source = await _library.LocateAsync(name, token) ?? throw new InvalidDataException($"{name} is not in the Asset Library index.");
        var mesh = await _library.LoadAsync(name, token) ?? throw new InvalidDataException($"Arkive cannot rebuild {name}.");
        var result = await Task.Run(() => _scenes.PrepareAsync(source.ViewerRequest, null, token), token);
        var scene = new OutfitSwapScene(result.Directory, result.Scene);
        return (new OutfitSwapPiece(name, mesh, scene.Materials), scene, result);
    }

    /// <summary>The swapped outfit, reshaped by the chosen body shape, shown with the new body's face and hair.</summary>
    private async Task ShowResultAsync(CancellationToken token)
    {
        if (_fit is null || _pictures is null) return;
        var upscale = (UpscaleComboBox.SelectedItem as OutfitSwapUpscaleChoice)?.Settings;
        Status(upscale is null ? "Preparing the preview…" : "Reshaping with the chosen body shape…");
        var meshes = new List<OutfitSwapMesh>();
        var scenes = new List<ModelViewerSceneResult>();
        for (var i = 0; i < _targets.Count; i++)
        {
            var piece = _fit.Plan.Pieces[i];
            meshes.Add(piece.Hidden ? piece.Mesh : OutfitSwapModWriter.Reshape(await File.ReadAllBytesAsync(_targetGlbs[i], token), piece.Mesh, upscale, piece.ObjectName));
            scenes.Add(await PrepareTargetAsync(i, upscale, token, meshes[^1]));
        }
        foreach (var head in _heads) scenes.Add(await Task.Run(() => _scenes.PrepareAsync(head, null, token), token));
        var combined = BodyProportionSceneCombiner.Combine(scenes, SceneCacheRoot);
        await LoadSceneAsync(combined.SceneJson, keepView: _shownOnce, token);
        _shownOnce = true;
        _shown = meshes;
    }

    /// <summary>A replaced piece with the swap's geometry (reshaped when asked) and current pictures, as a viewer scene.</summary>
    private async Task<ModelViewerSceneResult> PrepareTargetAsync(int i, BodyProportionModSettings? upscale, CancellationToken token,
        OutfitSwapMesh? mesh = null)
    {
        var folder = Path.Combine(_workDirectory, "meshes");
        Directory.CreateDirectory(folder);
        var working = await File.ReadAllBytesAsync(_targetGlbs[i], token);
        var piece = _fit!.Plan.Pieces[i];
        mesh ??= piece.Hidden ? piece.Mesh : OutfitSwapModWriter.Reshape(working, piece.Mesh, upscale, piece.ObjectName);
        var glb = Path.Combine(folder, $"{piece.ObjectName}-{Guid.NewGuid():N}.glb");
        await File.WriteAllBytesAsync(glb, OutfitSwapMesh.WriteInto(working, mesh), token);
        var textures = _targetScenes[i].Scene.Textures.Select(texture => texture.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var materials = _targetScenes[i].Scene.Materials.Select(material => material.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edits = new ModelViewerEdits("Outfit swap", glb,
            _pictures!.Pictures.Where(pair => textures.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value),
            _pictures.Materials.Where(pair => materials.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value), []);
        var scene = await Task.Run(() => _scenes.PrepareAsync(_targetSources[i].ViewerRequest, edits, null, token), token);
        return OutfitSwapPreview.WithDonorMaterials(scene, mesh, _materialScenes);
    }

    private async Task LoadSceneAsync(string sceneJson, bool keepView, CancellationToken token)
    {
        await _pageReady.Task.WaitAsync(token);
        _sceneLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Browser.CoreWebView2.PostWebMessageAsJson(
            $"{{\"type\":\"load\",\"theme\":\"{_theme}\",\"keepView\":{(keepView ? "true" : "false")},\"hasEdits\":false," +
            $"\"baseUrl\":\"https://{SceneHost}/\",\"scene\":{sceneJson}}}");
        await _sceneLoaded.Task.WaitAsync(TimeSpan.FromMinutes(5), token);
    }

    /// <summary>
    /// Bakes every material of the shown scene to PNGs, by material slot: its colour ("colour"), its lit colour ("lit"),
    /// or its roughness and metalness ("surface").
    /// </summary>
    private async Task<Dictionary<int, string>> BakeAsync(string directory, CancellationToken token, string kind = "colour")
    {
        Directory.CreateDirectory(directory);
        var job = _bake = new BakeJob(Guid.NewGuid().ToString("N"), directory);
        Post(new { type = "bake", requestId = job.Id, kind });
        await job.Completion.Task.WaitAsync(TimeSpan.FromMinutes(10), token);
        return job.Files;
    }

    /// <summary>A combined scene's bakes split back into its pieces, by each piece's own material slot.</summary>
    private static IReadOnlyDictionary<int, string>[] ByPiece(BodyProportionSceneCombiner.CombinedScene combined, Dictionary<int, string> baked) =>
        combined.Parts.Select(part => (IReadOnlyDictionary<int, string>)baked
            .Where(pair => pair.Key >= part.MaterialOffset && pair.Key < part.MaterialOffset + part.MaterialCount)
            .ToDictionary(pair => pair.Key - part.MaterialOffset, pair => pair.Value)).ToArray();

    private async void UpscaleComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _fit is null || _busy) return;
        _busy = true;
        SaveButton.IsEnabled = false;
        try
        {
            await ShowResultAsync(_closing.Token);
            Status((UpscaleComboBox.SelectedItem as OutfitSwapUpscaleChoice)?.Settings is null
                ? Describe(_fit.Report)
                : $"Reshaped with {((OutfitSwapUpscaleChoice)UpscaleComboBox.SelectedItem).Label}. " + Describe(_fit.Report));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException
                                              or BodyProportionEngine.UnknownBodyException)
        {
            Status($"Could not apply that body shape: {exception.Message}");
            _log($"Outfit swap body shape failed: {exception}");
        }
        finally
        {
            _busy = false;
            SaveButton.IsEnabled = true;
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_fit is null || _pictures is null || _busy) return;
        var name = ModNameTextBox.Text.Trim();
        if (name.Length == 0)
        {
            Status("Type a name for the mod first.");
            ModNameTextBox.Focus();
            return;
        }
        _busy = true;
        SaveButton.IsEnabled = false;
        UpscaleComboBox.IsEnabled = false;
        Status("Saving the swap as a mod: checking the meshes against the game's skeleton and animations and rebuilding the pictures…");
        try
        {
            Status(await _save(new OutfitSwapSaveRequest(name, _donorNames, _targetSources, _shown, _pictures, _targetScenes, _fit.Report,
                (OutfitSwapUpscaleChoice)UpscaleComboBox.SelectedItem)
            {
                Progress = new Progress<string>(text => Status(text + "…")),
                OwnMaterials = OwnMaterials,
                TargetPieces = _targets,
            }));
        }
        catch (Exception exception)
        {
            Status($"The mod was not saved: {exception.Message}");
            _log($"Outfit swap save failed: {exception}");
        }
        finally
        {
            _busy = false;
            SaveButton.IsEnabled = true;
            UpscaleComboBox.IsEnabled = true;
        }
    }

    private async Task<bool> InitializeBrowserAsync()
    {
        try
        {
            Directory.CreateDirectory(_webViewDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, _webViewDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(ViewerHost,
                Path.Combine(AppContext.BaseDirectory, "Viewer"), CoreWebView2HostResourceAccessKind.DenyCors);
            Directory.CreateDirectory(SceneCacheRoot);
            core.SetVirtualHostNameToFolderMapping(SceneHost, SceneCacheRoot, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.Navigate($"https://{ViewerHost}/index.html");
            return true;
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or COMException or IOException
                                              or UnauthorizedAccessException)
        {
            _log($"Outfit swap could not start WebView2: {exception}");
            UnavailableText.Text = exception is WebView2RuntimeNotFoundException
                ? "Outfit swaps need the Microsoft Edge WebView2 Runtime, which is part of Windows 10 and 11 but appears to be missing here."
                : $"WebView2 could not start: {exception.Message}";
            UnavailablePanel.Visibility = Visibility.Visible;
            return false;
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        using var document = JsonDocument.Parse(args.WebMessageAsJson);
        var root = document.RootElement;
        switch (root.TryGetProperty("type", out var type) ? type.GetString() : null)
        {
            case "ready":
                _pageReady.TrySetResult();
                _ = OfferEnvironmentsAsync();
                break;
            case "loaded":
                _sceneLoaded.TrySetResult();
                break;
            case "baked":
                if (_bake is { } job && root.GetProperty("requestId").GetString() == job.Id &&
                    root.GetProperty("dataUrl").GetString() is { } data && data.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
                {
                    var slot = root.GetProperty("slot").GetInt32();
                    var file = Path.Combine(job.Directory, $"{slot:D2}.png");
                    File.WriteAllBytes(file, Convert.FromBase64String(data["data:image/png;base64,".Length..]));
                    job.Files[slot] = file;
                }
                break;
            case "bakeDone":
                if (_bake is { } done && root.GetProperty("requestId").GetString() == done.Id) done.Completion.TrySetResult();
                break;
        }
    }

    private async Task OfferEnvironmentsAsync()
    {
        if (Environments is null) return;
        try
        {
            var set = await Environments(CancellationToken.None);
            if (set is null || set.Environments.Count == 0) return;
            var folder = Path.GetRelativePath(SceneCacheRoot, set.Directory).Replace('\\', '/');
            Post(new
            {
                type = "environments",
                baseUrl = $"https://{SceneHost}/{folder}/",
                environments = set.Environments.Select(item => new { name = item.Name, label = item.Label, faces = item.Faces }),
                skyBaseUrl = set.Skies is null ? null
                    : $"https://{SceneHost}/{Path.GetRelativePath(SceneCacheRoot, set.Skies.Directory).Replace('\\', '/')}/",
                skies = (set.Skies?.Skies ?? []).Select(item => new { name = item.Name, label = item.Label, file = item.File }),
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log($"Viewer environments could not be offered: {exception.Message}");
        }
    }

    /// <summary>Offers the new body's animations, so the swapped outfit can be watched moving.</summary>
    private async Task OfferAnimationsAsync(ModelViewerRequest request, CancellationToken cancellationToken)
    {
        if (Animations is null) return;
        try
        {
            Post(new { type = "animationsStatus", text = "Preparing animations…" });
            var offer = await Animations(request, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            var set = offer?.Set;
            if (set is null)
            {
                Post(new { type = "animationsStatus", text = "" });
                return;
            }
            var folder = Path.GetRelativePath(SceneCacheRoot, set.Directory).Replace('\\', '/');
            Post(new
            {
                type = "animations",
                baseUrl = $"https://{SceneHost}/{folder}/",
                animSet = set.AnimSetName,
                bones = set.Bones,
                restPose = $"https://{SceneHost}/{folder}/{offer!.RestPoseFile}",
                animations = set.Animations.Select(item => new { name = item.Name, group = item.Group, frames = item.Frames, rate = item.Rate, file = item.File }),
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Post(new { type = "animationsStatus", text = "Animations unavailable" });
            _log($"Viewer animations failed: object={request.ObjectName}, error={exception.Message}");
        }
    }
}
