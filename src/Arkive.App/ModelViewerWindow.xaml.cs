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
