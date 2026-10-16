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
    bool LooseClothingFollows);

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
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _sceneLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<BodyProportionPiece> _pieces = [];
    private Dictionary<string, double> _values = new(StringComparer.Ordinal);
    private bool _loose;
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
        Action<string> log)
    {
        _scenes = scenes;
        _pieceService = pieceService;
        _requests = requests;
        _webViewDataFolder = webViewDataFolder;
        _theme = effectiveTheme.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        _save = save;
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
            Status($"{profiles[0].DisplayName} · {pieces.Count} piece(s). Move the sliders, then save as a mod.");
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
        var loose = _loose;
        var models = await Task.Run(async () =>
        {
            var built = new List<BodyProportionModel>();
            foreach (var piece in _pieces)
                built.Add(BodyProportionEngine.Build(await File.ReadAllBytesAsync(piece.EditableGlbPath, token), loose, piece.Profile));
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
            loose,
            looseVertices = models.Sum(model => model.LooseVertexCount),
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
                _pieces.Select(piece => piece.Request).ToArray(), new Dictionary<string, double>(_values), _loose));
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
                break;
            case "loaded":
                _sceneLoaded.TrySetResult();
                break;
            case "proportionValues":
                _values = root.GetProperty("values").EnumerateObject()
                    .Where(property => property.Value.ValueKind == JsonValueKind.Number)
                    .ToDictionary(property => property.Name, property => property.Value.GetDouble(), StringComparer.Ordinal);
                break;
            case "proportionLoose":
                _loose = root.GetProperty("value").GetBoolean();
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
