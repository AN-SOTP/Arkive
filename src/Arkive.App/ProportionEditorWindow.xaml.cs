using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Arkive.Core.Proportions;
using Arkive.Core.Viewer;
using Microsoft.Web.WebView2.Core;

namespace Arkive.App;

/// <summary>What the proportion editor asks Arkive to save as a mod.</summary>
public sealed record ProportionSaveRequest(
    string ModName,
    BodyProfile Profile,
    IReadOnlyList<BodyProportionPieceRequest> Pieces,
    IReadOnlyDictionary<string, double> Settings,
    BodyProportionFollow Follow)
{
    /// <summary>Stops reshaping between pieces; the pieces done so far stay in the mod's draft.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>The Simple upscale mod to update instead of creating a new one.</summary>
    public string? ExistingModId { get; init; }

    /// <summary>How many pieces to reshape and check at the same time (1: one after another).</summary>
    public int Parallelism { get; init; } = 1;
}

/// <summary>
/// Arkive's model viewer with body proportion sliders: shows every chosen piece of an outfit together, reshapes
/// them live as sliders move, and hands the settings to Arkive to save as a mod.
/// </summary>
public partial class ProportionEditorWindow : Window
{
    private const string ViewerHost = "viewer.arkive.local";
    private const string SceneHost = "scenes.arkive.local";

    private readonly ModelViewerSceneService _scenes;
    private readonly BodyProportionPieceService _pieceService;
    private readonly IReadOnlyList<BodyProportionPieceRequest> _requests;
    private readonly string _webViewDataFolder;
    private readonly string _theme;
    private readonly Func<ProportionSaveRequest, Task<string>> _save;
    private readonly Action<string> _log;
    private readonly BodyProportionPresetStore _presets;

    /// <summary>Prepares the game's environment cubemaps (null when unavailable); set by Arkive's main window.</summary>
    public Func<CancellationToken, Task<ModelViewerEnvironmentSet?>>? Environments { get; init; }

    /// <summary>A saved Simple upscale mod being edited again: its slider settings, and the mod saving updates.</summary>
    public BodyProportionModSettings? InitialSettings { get; init; }

    public string? EditingModId { get; init; }

    public string? EditingModName { get; init; }

    /// <summary>Finds the animations a mesh can play (null when it has none); set by Arkive's main window.</summary>
    public Func<ModelViewerRequest, CancellationToken, Task<ModelViewerAnimationOffer?>>? Animations { get; init; }

    private string SceneCacheRoot => _scenes.Cache.RootDirectory;

    private void AnimationsLog(string text) => _log(text);

    private void Post(object message)
    {
        if (Browser.CoreWebView2 is not null)
            Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    private bool _loadingPresets;
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _sceneLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<BodyProportionPiece> _pieces = [];
    private Dictionary<string, double> _values = new(StringComparer.Ordinal);
    // Loose cloth and hair follow the body unless the user turns it off.
    private BodyProportionFollow _follow = BodyProportionFollow.All;
    private bool _busy;
    private string? _workDirectory;
    private int _basesVersion;

    public ProportionEditorWindow(
        ModelViewerSceneService scenes,
        BodyProportionPieceService pieceService,
        IReadOnlyList<BodyProportionPieceRequest> requests,
        string webViewDataFolder,
        string effectiveTheme,
        Func<ProportionSaveRequest, Task<string>> save,
        BodyProportionPresetStore presets,
        Action<string> log)
    {
        _scenes = scenes;
        _pieceService = pieceService;
        _requests = requests;
        _webViewDataFolder = webViewDataFolder;
        _theme = effectiveTheme.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        _save = save;
        _presets = presets;
        _log = log;
        InitializeComponent();
        Title = $"Arkive simple upscale · {requests[0].PackageAlias}";
        ModNameTextBox.Text = $"{requests[0].PackageAlias} upscale";
        SourceInitialized += (_, _) => AppearanceManager.ApplyWindowChrome(this, effectiveTheme);
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            _closing.Cancel();
            if (_workDirectory is not null)
                try { Directory.Delete(_workDirectory, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        };
    }

    /// <summary>The current settings, for tests and the host.</summary>
    public IReadOnlyDictionary<string, double> Settings => _values;

    private async Task StartAsync()
    {
        if (!await InitializeBrowserAsync()) return;
        if (InitialSettings is { } initial)
        {
            _values = new Dictionary<string, double>(initial.Settings, StringComparer.Ordinal);
            _follow = initial.Follow;
        }
        if (EditingModName is { } modName)
        {
            ModNameTextBox.Text = modName;
            Title = $"Arkive simple upscale · editing {modName}";
        }
        try
        {
            var token = _closing.Token;
            var pieces = new List<BodyProportionPiece>();
            foreach (var request in _requests)
            {
                Status($"Preparing {request.ObjectName} ({pieces.Count + 1} of {_requests.Count})…");
                pieces.Add(await Task.Run(() => _pieceService.PrepareAsync(request, token), token));
            }
            var profiles = pieces.Select(piece => piece.Profile).Distinct().ToArray();
            if (profiles.Length > 1)
                throw new InvalidDataException("These pieces belong to different bodies (" +
                    string.Join(", ", profiles.Select(profile => profile.DisplayName)) + "). Open one body's outfit at a time.");
            _pieces = pieces;

            var scenes = new List<ModelViewerSceneResult>();
            foreach (var piece in pieces)
            {
                Status($"Loading {piece.Request.ObjectName}'s materials…");
                var edits = new ModelViewerEdits("Body proportions", piece.EditableGlbPath,
                    new Dictionary<string, string>(), new Dictionary<string, ModelViewerMaterialEdit>(), []);
                scenes.Add(await Task.Run(() => _scenes.PrepareAsync(piece.ViewerRequest, edits, null, token), token));
            }
            var root = _scenes.Cache.RootDirectory;
            _workDirectory = Path.Combine(root, $"proportions-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_workDirectory);
            var combined = BodyProportionSceneCombiner.Combine(scenes, root);
            await _pageReady.Task.WaitAsync(token);
            Browser.CoreWebView2.PostWebMessageAsJson(
                $"{{\"type\":\"load\",\"theme\":\"{_theme}\",\"keepView\":false,\"hasEdits\":false," +
                $"\"baseUrl\":\"https://{SceneHost}/\",\"scene\":{combined.SceneJson}}}");
            await _sceneLoaded.Task.WaitAsync(TimeSpan.FromMinutes(5), token);
            await SendProportionsAsync(keepTab: false);
            SaveButton.IsEnabled = true;
            PresetComboBox.IsEnabled = SavePresetButton.IsEnabled = true;
            _ = OfferAnimationsAsync(pieces[0].ViewerRequest, token);
            LoadPresets(null);
            Status(EditingModId is null
                ? $"{profiles[0].DisplayName} · {pieces.Count} piece(s). Move the sliders, then save as a mod."
                : $"{profiles[0].DisplayName} · {pieces.Count} piece(s) with {EditingModName}'s sliders. Saving updates that mod.");
            _log($"Proportion editor ready: body={profiles[0].Id}, pieces={string.Join(", ", pieces.Select(piece => piece.Request.ObjectName))}.");
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var message = exception is BodyProportionEngine.UnknownBodyException
                ? "This piece's body has not been tuned for proportion editing yet."
                : exception.Message;
            Status($"Could not open the outfit: {message}");
            _log($"Proportion editor failed: {exception}");
        }
    }

    /// <summary>Computes every piece's offsets (for the current loose-clothing choice) and sends them to the page.</summary>
    private async Task SendProportionsAsync(bool keepTab)
    {
        var token = _closing.Token;
        var follow = _follow;
        var models = await Task.Run(async () =>
        {
            var built = new List<BodyProportionModel>();
            foreach (var piece in _pieces)
                built.Add(BodyProportionEngine.Build(await File.ReadAllBytesAsync(piece.EditableGlbPath, token), follow, piece.Profile,
                    piece.Request.ObjectName));
            return built;
        }, token);
        var layout = new List<object>();
        var file = Path.Combine(_workDirectory!, $"bases-{++_basesVersion}.bin");
        await using (var stream = File.Create(file))
        {
            var offset = 0L;
            for (var part = 0; part < models.Count; part++)
            {
                foreach (var mesh in models[part].Meshes)
                {
                    var zones = new Dictionary<string, long>();
                    foreach (var (zone, offsets) in mesh.Offsets)
                    {
                        zones[zone] = offset;
                        stream.Write(MemoryMarshal.AsBytes(offsets.AsSpan()));
                        offset += offsets.Length;
                    }
                    layout.Add(new { part, mesh = mesh.MeshIndex, vertexCount = mesh.VertexCount, zones });
                }
            }
        }
        foreach (var stale in Directory.EnumerateFiles(_workDirectory!, "bases-*.bin").Where(path => path != file))
            try { File.Delete(stale); }
            catch (IOException) { }
        var profile = _pieces[0].Profile;
        var message = new
        {
            type = "proportions",
            keepTab,
            bodyName = profile.DisplayName,
            pieces = _pieces.Select(piece => piece.Request.ObjectName),
            zones = profile.Zones.Select(zone => new { zone.Id, zone.Group, zone.Label, zone.Description, zone.Minimum, zone.Maximum }),
            values = _values,
            loose = follow.LooseClothing,
            hair = follow.Hair,
            looseVertices = models.Sum(model => model.LooseVertexCount),
            hairVertices = models.Sum(model => model.HairVertexCount),
            layout,
            basesUrl = $"https://{SceneHost}/{Path.GetFileName(_workDirectory)}/{Path.GetFileName(file)}",
        };
        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _pieces.Count == 0) return;
        var name = ModNameTextBox.Text.Trim();
        if (name.Length == 0)
        {
            Status("Enter a name for the mod.");
            ModNameTextBox.Focus();
            return;
        }
        if (BodyProportionEngine.IsNeutral(_values))
        {
            Status("Every slider is at 0, so there is nothing to save yet.");
            return;
        }
        _busy = true;
        SaveButton.IsEnabled = false;
        Status("Saving the mod: preparing each piece, reshaping it and checking it against the game's animations…");
        try
        {
            var result = await _save(new ProportionSaveRequest(name, _pieces[0].Profile,
                _pieces.Select(piece => piece.Request).ToArray(), new Dictionary<string, double>(_values), _follow)
            {
                ExistingModId = EditingModId,
            });
            Status(result);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Status($"The mod was not saved: {exception.Message}");
            _log($"Proportion mod save failed: {exception}");
        }
        finally
        {
            _busy = false;
            SaveButton.IsEnabled = true;
        }
    }

    private void Status(string text) => StatusText.Text = text;

    /// <summary>Offers the game's environment cubemaps to the page's Lighting panel, once they are prepared.</summary>
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
            AnimationsLog($"Viewer environments could not be offered: {exception.Message}");
        }
    }

    /// <summary>
    /// Prepares the shown character's animations in the background and offers them to the page. The first time for a
    /// class takes a while (UModel exports every animation once); later views read the cache.
    /// </summary>
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
            AnimationsLog($"Viewer animations failed: object={request.ObjectName}, error={exception.Message}");
        }
    }


    private static bool SameSettings(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
    {
        var keys = a.Keys.Concat(b.Keys).Distinct(StringComparer.Ordinal);
        return keys.All(key => Math.Abs(a.GetValueOrDefault(key) - b.GetValueOrDefault(key)) < 1e-6);
    }

    private void LoadPresets(string? select)
    {
        _loadingPresets = true;
        var presets = _presets.Load(_pieces[0].Profile.Id);
        PresetComboBox.ItemsSource = presets;
        PresetComboBox.SelectedItem = presets.FirstOrDefault(preset => preset.Name == select);
        if (select is not null) PresetComboBox.Text = select;
        _loadingPresets = false;
    }

    /// <summary>Choosing a preset sets every slider (and the follow options) to its values.</summary>
    private async void PresetComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingPresets || PresetComboBox.SelectedItem is not BodyProportionPreset preset || _pieces.Count == 0) return;
        _values = new Dictionary<string, double>(preset.Settings, StringComparer.Ordinal);
        try
        {
            if (preset.Follow != _follow)
            {
                _follow = preset.Follow;
                await SendProportionsAsync(keepTab: true);
            }
            else
                Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(
                    new { type = "setProportionValues", values = _values }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Status($"Preset {preset.Name} applied. Adjust the sliders or save as a mod.");
            _log($"Proportion preset applied in the editor: {preset.Name} ({preset.ProfileId}).");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException)
        {
            Status($"Could not apply the preset: {exception.Message}");
        }
    }

    private void SavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        var name = PresetComboBox.Text.Trim();
        if (name.Length == 0)
        {
            Status("Type a name for the preset in the Preset box first.");
            PresetComboBox.Focus();
            return;
        }
        if (BodyProportionEngine.IsNeutral(_values))
        {
            Status("Every slider is at 0, so there is nothing to save as a preset.");
            return;
        }
        try
        {
            var saved = _presets.Save(new BodyProportionPreset(name, _pieces[0].Profile.Id, new Dictionary<string, double>(_values),
                _follow.LooseClothing, _follow.Hair, DateTimeOffset.Now));
            LoadPresets(saved.Name);
            Status($"Saved preset {saved.Name} for {_pieces[0].Profile.DisplayName}.");
            _log($"Proportion preset saved: {saved.Name} ({saved.ProfileId}), settings={saved.Settings.Count}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status($"The preset was not saved: {exception.Message}");
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
            Directory.CreateDirectory(_scenes.Cache.RootDirectory);
            core.SetVirtualHostNameToFolderMapping(SceneHost, _scenes.Cache.RootDirectory, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.Navigate($"https://{ViewerHost}/index.html");
            return true;
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or COMException or IOException
                                              or UnauthorizedAccessException)
        {
            _log($"Proportion editor could not start WebView2: {exception}");
            UnavailableText.Text = exception is WebView2RuntimeNotFoundException
                ? "The proportion editor needs the Microsoft Edge WebView2 Runtime, which is part of Windows 10 and 11 but appears to be missing here."
                : $"WebView2 could not start: {exception.Message}";
            UnavailablePanel.Visibility = Visibility.Visible;
            return false;
        }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
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
            case "proportionValues":
                _values = root.GetProperty("values").EnumerateObject()
                    .Where(property => property.Value.ValueKind == JsonValueKind.Number)
                    .ToDictionary(property => property.Name, property => property.Value.GetDouble(), StringComparer.Ordinal);
                // Once the sliders move away from the chosen preset, choosing it again should apply it again.
                if (PresetComboBox.SelectedItem is BodyProportionPreset chosen && !SameSettings(chosen.Settings, _values))
                {
                    // An editable combo box reselects an item whose name is typed in, so the name is cleared too.
                    _loadingPresets = true;
                    PresetComboBox.SelectedItem = null;
                    PresetComboBox.Text = "";
                    _loadingPresets = false;
                    Status($"Sliders changed since preset {chosen.Name}. Choose it again to go back to it.");
                }
                break;
            case "proportionLoose" or "proportionHair":
                _follow = type.GetString() == "proportionHair"
                    ? _follow with { Hair = root.GetProperty("value").GetBoolean() }
                    : _follow with { LooseClothing = root.GetProperty("value").GetBoolean() };
                try { await SendProportionsAsync(keepTab: true); }
                catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException)
                {
                    Status($"Could not update the loose clothing setting: {exception.Message}");
                }
                break;
            case "screenshot":
                SaveScreenshot(root.GetProperty("dataUrl").GetString(), root.GetProperty("name").GetString());
                break;
        }
    }

    private void SaveScreenshot(string? dataUrl, string? name)
    {
        const string prefix = "data:image/png;base64,";
        if (dataUrl is null || !dataUrl.StartsWith(prefix, StringComparison.Ordinal)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{name ?? "arkive-proportions"}.png",
            Filter = "PNG image (*.png)|*.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllBytes(dialog.FileName, Convert.FromBase64String(dataUrl[prefix.Length..]));
    }
}
