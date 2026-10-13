using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Arkive.Core.External;
using Arkive.Core.Viewer;
using Microsoft.Web.WebView2.Core;

namespace Arkive.App;

/// <summary>
/// Hosts Arkive's model viewer (three.js in WebView2). The page is served from the
/// application's Viewer folder; prepared scenes are served from the viewer cache.
/// </summary>
public partial class ModelViewerWindow : Window
{
    private const string ViewerHost = "viewer.arkive.local";
    private const string SceneHost = "scenes.arkive.local";

    private readonly ModelViewerSceneService _service;
    private readonly UmodelBridge _bridge;
    private readonly string _webViewDataFolder;
    private readonly Func<int, ModelViewerRequest?> _browse;
    private readonly Action<string> _log;
    private readonly string _theme;
    private ModelViewerRequest? _request;
    private string? _pendingLoadMessage;
    private bool _pageReady;
    private CancellationTokenSource? _loading;
    private TaskCompletionSource<bool> _sceneLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private BakeJob? _bake;

    /// <summary>A texture baked in texture space from one material, as the viewer shows it.</summary>
    public sealed record BakedTexture(int Slot, string Material, string Name, string Source, int Size, string File);

    private sealed class BakeJob(string requestId, string directory)
    {
        public string RequestId { get; } = requestId;
        public string Directory { get; } = directory;
        public List<BakedTexture> Textures { get; } = [];
        public TaskCompletionSource<IReadOnlyList<BakedTexture>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public ModelViewerWindow(
        ModelViewerSceneService service,
        UmodelBridge bridge,
        string webViewDataFolder,
        string effectiveTheme,
        Func<int, ModelViewerRequest?> browse,
        Action<string> log)
    {
        _service = service;
        _bridge = bridge;
        _webViewDataFolder = webViewDataFolder;
        _theme = effectiveTheme.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        _browse = browse;
        _log = log;
        InitializeComponent();
        SourceInitialized += (_, _) => AppearanceManager.ApplyWindowChrome(this, effectiveTheme);
        Loaded += async (_, _) => await InitializeBrowserAsync();
        Closed += (_, _) => _loading?.Cancel();
    }

    public async Task ShowObjectAsync(ModelViewerRequest request)
    {
        _request = request;
        Title = $"Arkive model viewer · {request.PackageAlias}.{request.ObjectName}";
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        if (_sceneLoaded.Task.IsCompleted) _sceneLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new { type = "progress", text = $"Preparing {request.ObjectName}…" });
        try
        {
            var progress = new Progress<ModelViewerProgress>(update =>
                Post(new { type = "progress", text = $"{update.Stage}: {update.Detail}…" }));
            var result = await Task.Run(() => _service.PrepareAsync(request, progress, loading.Token), loading.Token);
            if (loading.IsCancellationRequested) return;
            var sceneJson = JsonSerializer.Serialize(result.Scene, ModelViewerSceneService.JsonOptions);
            var baseUrl = $"https://{SceneHost}/{Uri.EscapeDataString(Path.GetFileName(result.Directory))}/";
            var message = $"{{\"type\":\"load\",\"theme\":\"{_theme}\",\"baseUrl\":{JsonSerializer.Serialize(baseUrl)},\"scene\":{sceneJson}}}";
            _log($"Model viewer scene ready: object={request.ObjectName}, cached={result.FromCache}, " +
                 $"geometry={result.Scene.GeometrySource}, lods={result.Scene.Lods.Count}, " +
                 $"materials={result.Scene.Materials.Count}, seconds={result.Scene.PreparationSeconds}.");
            if (_pageReady) Browser.CoreWebView2.PostWebMessageAsJson(message);
            else _pendingLoadMessage = message;
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log($"Model viewer preparation failed: object={request.ObjectName}, error={exception}");
            Post(new { type = "error", text = $"Arkive could not prepare {request.ObjectName}: {exception.Message}" });
            _sceneLoaded.TrySetException(exception);
        }
    }

    /// <summary>
    /// Bakes every material of the shown object (LOD 0, UV set 0) to sRGB PNGs, using the same colours the
    /// viewer shows, and writes <c>materials.json</c> listing which file belongs to which material.
    /// </summary>
    public async Task<IReadOnlyList<BakedTexture>> BakeAsync(string directory, CancellationToken cancellationToken = default)
    {
        await _sceneLoaded.Task.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
        Directory.CreateDirectory(directory);
        var job = _bake = new BakeJob(Guid.NewGuid().ToString("N"), directory);
        Post(new { type = "bake", requestId = job.RequestId });
        await using var registration = cancellationToken.Register(() => job.Completion.TrySetCanceled());
        var textures = await job.Completion.Task.WaitAsync(TimeSpan.FromMinutes(10), cancellationToken);
        File.WriteAllText(Path.Combine(directory, "materials.json"), JsonSerializer.Serialize(
            textures.Select(texture => new { material = texture.Material, name = texture.Name, file = texture.File, source = texture.Source, size = texture.Size }),
            new JsonSerializerOptions { WriteIndented = true }));
        return textures;
    }

    private void ReceiveBaked(JsonElement root)
    {
        if (_bake is not { } job || root.GetProperty("requestId").GetString() != job.RequestId) return;
        const string prefix = "data:image/png;base64,";
        var data = root.GetProperty("dataUrl").GetString() ?? string.Empty;
        if (!data.StartsWith(prefix, StringComparison.Ordinal)) return;
        var slot = root.GetProperty("slot").GetInt32();
        var name = root.GetProperty("name").GetString() ?? $"material_{slot}";
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        var file = $"{slot:D2}_{safe}.png";
        File.WriteAllBytes(Path.Combine(job.Directory, file), Convert.FromBase64String(data[prefix.Length..]));
        job.Textures.Add(new BakedTexture(slot, root.GetProperty("material").GetString() ?? name, name,
            root.GetProperty("source").GetString() ?? "", root.GetProperty("size").GetInt32(), file));
    }

    private async Task BakeToChosenFolderAsync()
    {
        if (_request is null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder for the coloured textures",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(this) != true) return;
        var directory = Path.Combine(dialog.FolderName, $"{_request.ObjectName}-colours");
        try
        {
            var textures = await BakeAsync(directory);
            _log($"Model viewer baked {textures.Count} texture(s) to {directory}.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException
                                              or OperationCanceledException)
        {
            MessageBox.Show(this, exception.Message, "Arkive model viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitializeBrowserAsync()
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
            Directory.CreateDirectory(_service.Cache.RootDirectory);
            core.SetVirtualHostNameToFolderMapping(SceneHost,
                _service.Cache.RootDirectory, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.Navigate($"https://{ViewerHost}/index.html");
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or COMException or IOException
                                              or UnauthorizedAccessException)
        {
            _log($"Model viewer could not start WebView2: {exception}");
            UnavailableText.Text = exception is WebView2RuntimeNotFoundException
                ? "Arkive's model viewer needs the Microsoft Edge WebView2 Runtime, which is part of Windows 10 and 11 " +
                  "but appears to be missing here. Install it from Microsoft, or open the object in UModel."
                : $"WebView2 could not start: {exception.Message}";
            UnavailablePanel.Visibility = Visibility.Visible;
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        using var document = JsonDocument.Parse(args.WebMessageAsJson);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        switch (type)
        {
            case "ready":
                _pageReady = true;
                if (_pendingLoadMessage is not null)
                {
                    Browser.CoreWebView2.PostWebMessageAsJson(_pendingLoadMessage);
                    _pendingLoadMessage = null;
                }
                else if (_request is not null) Post(new { type = "progress", text = $"Preparing {_request.ObjectName}…" });
                break;
            case "openUmodel":
                OpenInUmodel();
                break;
            case "loaded":
                _sceneLoaded.TrySetResult(true);
                break;
            case "baked":
                ReceiveBaked(root);
                break;
            case "bakeDone":
                if (_bake is { } job && root.GetProperty("requestId").GetString() == job.RequestId)
                {
                    _bake = null;
                    if (root.TryGetProperty("error", out var error))
                        job.Completion.TrySetException(new IOException($"Baking failed: {error.GetString()}"));
                    else
                        job.Completion.TrySetResult(job.Textures.ToArray());
                }
                break;
            case "requestBake":
                _ = BakeToChosenFolderAsync();
                break;
            case "screenshot":
                SaveScreenshot(root.GetProperty("dataUrl").GetString(), root.GetProperty("name").GetString());
                break;
            case "browse":
                if (_browse(root.GetProperty("delta").GetInt32()) is { } next) _ = ShowObjectAsync(next);
                break;
        }
    }

    private void Post(object message)
    {
        if (_pageReady) Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void OpenInUmodel()
    {
        if (_request is null) return;
        try
        {
            _bridge.StartAssetPreview(_request.PackagePath, new UmodelAssetEntry(_request.ExportIndex,
                _request.SerialOffset, _request.SerialSize, _request.ClassName, _request.ObjectName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.ComponentModel.Win32Exception or UmodelBridgeException)
        {
            MessageBox.Show(this, exception.Message, "Arkive model viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UnavailableUmodelButton_Click(object sender, RoutedEventArgs e) => OpenInUmodel();

    private void SaveScreenshot(string? dataUrl, string? name)
    {
        const string prefix = "data:image/png;base64,";
        if (dataUrl is null || !dataUrl.StartsWith(prefix, StringComparison.Ordinal)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{name ?? "arkive-view"}.png",
            Filter = "PNG image (*.png)|*.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllBytes(dialog.FileName, Convert.FromBase64String(dataUrl[prefix.Length..]));
        _log($"Model viewer screenshot saved: {dialog.FileName}");
    }
}
