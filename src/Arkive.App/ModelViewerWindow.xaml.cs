using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
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
    private Func<CancellationToken, Task<ModelViewerEdits?>>? _edits;
    private bool _showEdits = true;
    private readonly List<FileSystemWatcher> _watchers = [];
    private HashSet<string> _watchedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    private string? _pendingLoadMessage;
    private bool _pageReady;
    private CancellationTokenSource? _loading;
    private TaskCompletionSource<bool> _sceneLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private BakeJob? _bake;

    /// <summary>Prepares the game's environment cubemaps (null when unavailable); set by Arkive's main window.</summary>
    public Func<CancellationToken, Task<ModelViewerEnvironmentSet?>>? Environments { get; init; }

    /// <summary>
    /// Whether the page uses and saves the choices remembered between sessions (lighting, colour mode, toggles,
    /// animation). Off for the hidden windows that bake colours, so a bake never depends on them.
    /// </summary>
    public bool RememberViewSettings { get; set; } = true;

    /// <summary>Finds the animations a mesh can play (null when it has none); set by Arkive's main window.</summary>
    public Func<ModelViewerRequest, CancellationToken, Task<ModelViewerAnimationOffer?>>? Animations { get; init; }

    private string SceneCacheRoot => _service.Cache.RootDirectory;

    private void AnimationsLog(string text) => _log(text);

    /// <summary>A texture baked in texture space from one material, as the viewer shows it.</summary>
    public sealed record BakedTexture(int Slot, string Material, string Name, string Source, int Size, string File, string? NormalFile);

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
        // Saving a working file or applying a mod often writes several times; refresh once it settles.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _refreshTimer.Tick += async (_, _) =>
        {
            _refreshTimer.Stop();
            if (_request is not null) await ShowObjectAsync(_request, _edits, keepView: true);
        };
        Loaded += async (_, _) => await InitializeBrowserAsync();
        Closed += (_, _) =>
        {
            _loading?.Cancel();
            _refreshTimer.Stop();
            StopWatching();
        };
    }

    /// <summary>
    /// Shows an object as the game files have it and, when <paramref name="edits"/> is given, with a mod's
    /// working files on top. The view refreshes by itself when the object's packages, the packages its
    /// materials and textures come from, or any working file changes.
    /// </summary>
    public async Task ShowObjectAsync(
        ModelViewerRequest request,
        Func<CancellationToken, Task<ModelViewerEdits?>>? edits = null,
        bool keepView = false)
    {
        keepView &= _request == request && _edits == edits;
        _request = request;
        _edits = edits;
        Title = $"Arkive Asset Viewer · {request.PackageAlias}.{request.ObjectName}" + (edits is null ? "" : " · with your edits");
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        if (_sceneLoaded.Task.IsCompleted) _sceneLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new { type = "progress", text = $"Preparing {request.ObjectName}…" });
        try
        {
            var progress = new Progress<ModelViewerProgress>(update =>
                Post(new { type = "progress", text = $"{update.Stage}: {update.Detail}…" }));
            var workingFiles = edits is null ? null : await edits(loading.Token);
            var shown = _showEdits ? workingFiles : null;
            var result = await Task.Run(() => _service.PrepareAsync(request, shown, progress, loading.Token), loading.Token);
            if (loading.IsCancellationRequested) return;
            Watch(result.Scene, workingFiles);
            var sceneJson = JsonSerializer.Serialize(result.Scene, ModelViewerSceneService.JsonOptions);
            var baseUrl = $"https://{SceneHost}/{Uri.EscapeDataString(Path.GetFileName(result.Directory))}/";
            var message = $"{{\"type\":\"load\",\"theme\":\"{_theme}\",\"keepView\":{(keepView ? "true" : "false")}," +
                          $"\"remember\":{(RememberViewSettings ? "true" : "false")}," +
                          $"\"hasEdits\":{(workingFiles is not null ? "true" : "false")}," +
                          $"\"baseUrl\":{JsonSerializer.Serialize(baseUrl)},\"scene\":{sceneJson}}}";
            _log($"Model viewer scene ready: object={request.ObjectName}, cached={result.FromCache}, " +
                 $"geometry={result.Scene.GeometrySource}, lods={result.Scene.Lods.Count}, " +
                 $"materials={result.Scene.Materials.Count}, seconds={result.Scene.PreparationSeconds}.");
            if (_pageReady) Browser.CoreWebView2.PostWebMessageAsJson(message);
            else _pendingLoadMessage = message;
            if (!keepView) _ = OfferAnimationsAsync(request, loading.Token);
            if (!keepView) _ = OfferSkillsAsync(request, loading.Token);
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
                skies = (set.Skies?.Skies ?? []).Select(item => new { name = item.Name, label = item.Label, file = item.File, colour = item.Colour }),
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
            // A cached set can be ready before the page is; messages sent before then are lost.
            await _pageReadyTask.Task.WaitAsync(cancellationToken);
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

    /// <summary>Watches the scene's package files and the working files, one watcher per folder.</summary>
    private void Watch(ModelViewerScene scene, ModelViewerEdits? edits)
    {
        var files = (scene.Packages?.Select(stamp => stamp.Path) ?? [])
            .Concat(edits?.Files ?? [])
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (files.SetEquals(_watchedFiles)) return;
        StopWatching();
        _watchedFiles = files;
        foreach (var folder in files.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                };
                watcher.Changed += OnWatchedFileChanged;
                watcher.Created += OnWatchedFileChanged;
                watcher.Renamed += OnWatchedFileChanged;
                watcher.Deleted += OnWatchedFileChanged;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
            {
                _log($"Model viewer cannot watch {folder} for changes: {exception.Message}");
            }
        }
    }

    private void OnWatchedFileChanged(object sender, FileSystemEventArgs args)
    {
        var path = args is RenamedEventArgs renamed && !_watchedFiles.Contains(renamed.FullPath) ? renamed.OldFullPath : args.FullPath;
        if (!_watchedFiles.Contains(path) && !_watchedFiles.Contains(args.FullPath)) return;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    private void StopWatching()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        _watchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            textures.Select(texture => new { material = texture.Material, name = texture.Name, file = texture.File, normal = texture.NormalFile, source = texture.Source, size = texture.Size }),
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
        string? normalFile = null;
        if (root.TryGetProperty("normalDataUrl", out var normal) && normal.GetString() is { } normalData &&
            normalData.StartsWith(prefix, StringComparison.Ordinal))
        {
            normalFile = $"{slot:D2}_{safe}_normal.png";
            File.WriteAllBytes(Path.Combine(job.Directory, normalFile), Convert.FromBase64String(normalData[prefix.Length..]));
        }
        job.Textures.Add(new BakedTexture(slot, root.GetProperty("material").GetString() ?? name, name,
            root.GetProperty("source").GetString() ?? "", root.GetProperty("size").GetInt32(), file, normalFile));
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
            MessageBox.Show(this, exception.Message, "Arkive Asset Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (ParticleAssetRoot is not null)
            {
                Directory.CreateDirectory(ParticleAssetRoot);
                core.SetVirtualHostNameToFolderMapping("particles.arkive.local", ParticleAssetRoot, CoreWebView2HostResourceAccessKind.Allow);
            }
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.Navigate($"https://{ViewerHost}/index.html");
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or COMException or IOException
                                              or UnauthorizedAccessException)
        {
            _log($"Model viewer could not start WebView2: {exception}");
            UnavailableText.Text = exception is WebView2RuntimeNotFoundException
                ? "Arkive's Asset Viewer needs the Microsoft Edge WebView2 Runtime, which is part of Windows 10 and 11 " +
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
                _pageReadyTask.TrySetResult();
                _ = OfferEnvironmentsAsync();
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
            case "playSkill":
                _ = PlaySkillAsync(root.Clone());
                break;
            case "showEdits":
                _showEdits = root.GetProperty("value").GetBoolean();
                if (_request is not null) _ = ShowObjectAsync(_request, _edits, keepView: true);
                break;
        }
    }

    private readonly TaskCompletionSource _pageReadyTask = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            MessageBox.Show(this, exception.Message, "Arkive Asset Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
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
