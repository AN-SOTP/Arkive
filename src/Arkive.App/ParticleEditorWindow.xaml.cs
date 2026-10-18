using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Arkive.Core.Archives;
using Arkive.Core.Particles;
using Microsoft.Web.WebView2.Core;

namespace Arkive.App;

/// <summary>Which particle system the editor shows: a package (its clean copy) and the system in it.</summary>
public sealed record ParticleEditorRequest(string PackagePath, string PackageAlias, string SystemObjectName, string? SystemObjectPath);

/// <summary>A texture the preview can draw for one of the effect's materials, and the material's blend mode.</summary>
public sealed record ParticleEditorTexture(string? RelativePath, string Blend);

/// <summary>
/// Arkive's particle effect window: plays the effect live from the game's own settings and shows every change to
/// its colour, brightness, opacity, size, lifetime, spawn rate and speed as it is made. With a save action (an effect
/// in a mod being edited) Save changes writes the settings into the effect's working copy; without one it previews.
/// </summary>
public partial class ParticleEditorWindow : Window
{
    private const string ViewerHost = "viewer.arkive.local";
    private const string TextureHost = "particles.arkive.local";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ParticleEditorRequest _request;
    private readonly string _webViewDataFolder;
    private readonly string _textureRoot;
    private readonly string _theme;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly Func<ParticleEffectRecipe, Task<string>>? _save;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, ParticleEditorTexture>>>? _textures;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ParticleEffectRecipe _saved;

    public ParticleEditorWindow(
        ParticleEditorRequest request,
        ParticleEffectRecipe recipe,
        string webViewDataFolder,
        string textureRoot,
        string effectiveTheme,
        IChunkProtectionProvider protectionProvider,
        Func<ParticleEffectRecipe, Task<string>>? save,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, ParticleEditorTexture>>>? textures,
        Action<string> log)
    {
        _request = request;
        _webViewDataFolder = webViewDataFolder;
        _textureRoot = textureRoot;
        _theme = effectiveTheme.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        _protectionProvider = protectionProvider;
        _save = save;
        _textures = textures;
        _log = log;
        _saved = recipe;
        Recipe = recipe;
        InitializeComponent();
        Title = $"Arkive particle effect · {request.SystemObjectName}";
        SaveButton.Visibility = save is null ? Visibility.Collapsed : Visibility.Visible;
        SourceInitialized += (_, _) => AppearanceManager.ApplyWindowChrome(this, effectiveTheme);
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _closing.Cancel();
    }

    /// <summary>The settings shown in the window.</summary>
    public ParticleEffectRecipe Recipe { get; private set; }

    private void Status(string text) => StatusText.Text = text;

    private void Post(object message)
    {
        if (Browser.CoreWebView2 is not null) Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    private async Task StartAsync()
    {
        if (!await InitializeBrowserAsync()) return;
        try
        {
            var token = _closing.Token;
            var model = await Task.Run(() => ParticleEffectModels.Build(_request.PackagePath, _request.PackageAlias,
                _request.SystemObjectName, _request.SystemObjectPath, _protectionProvider), token);
            await _pageReady.Task.WaitAsync(token);
            Post(new { type = "load", theme = _theme, model, recipe = Recipe });
            Status(_save is null
                ? "Preview only. To keep changes, add the effect to a mod in Edit mods and open it with Edit effect."
                : "Change the settings on the left; Save changes writes them into this mod's working copy.");
            _log($"Particle editor opened: system={_request.SystemObjectName}, package={_request.PackageAlias}, emitters={model.Emitters.Count}.");
            var materials = model.Emitters.Select(emitter => emitter.Material).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (_textures is not null && materials.Length > 0)
            {
                var found = await _textures(materials, token);
                Post(new
                {
                    type = "textures",
                    items = found.ToDictionary(pair => pair.Key, pair => new
                    {
                        url = pair.Value.RelativePath is null ? null : $"https://{TextureHost}/{pair.Value.RelativePath.Replace('\\', '/')}",
                        blend = pair.Value.Blend,
                    }),
                });
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                                              Arkive.Core.External.UmodelBridgeException)
        {
            _log($"Particle editor could not load {_request.SystemObjectName}: {exception}");
            Post(new { type = "error", text = $"Arkive could not read {_request.SystemObjectName}: {exception.Message}" });
            Status($"The effect could not be read: {exception.Message}");
        }
    }

    private async Task<bool> InitializeBrowserAsync()
    {
        try
        {
            Directory.CreateDirectory(_webViewDataFolder);
            Directory.CreateDirectory(_textureRoot);
            var environment = await CoreWebView2Environment.CreateAsync(null, _webViewDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(ViewerHost, Path.Combine(AppContext.BaseDirectory, "Viewer"),
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.SetVirtualHostNameToFolderMapping(TextureHost, _textureRoot, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.Navigate($"https://{ViewerHost}/particles.html");
            return true;
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or COMException or IOException
                                              or UnauthorizedAccessException)
        {
            _log($"Particle editor could not start WebView2: {exception}");
            UnavailableText.Text = exception is WebView2RuntimeNotFoundException
                ? "The particle editor needs the Microsoft Edge WebView2 Runtime, which is part of Windows 10 and 11 but appears to be missing here."
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
                break;
            case "recipe":
                if (root.GetProperty("recipe").Deserialize<ParticleEffectRecipe>(Json) is { IsValid: true } recipe)
                {
                    Recipe = recipe;
                    SaveButton.IsEnabled = _save is not null && recipe != _saved;
                }
                break;
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_save is null) return;
        SaveButton.IsEnabled = false;
        var recipe = Recipe;
        Status("Writing the settings into the effect's working copy…");
        try
        {
            Status(await _save(recipe));
            _saved = recipe;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or
                                              UnauthorizedAccessException or ArgumentException)
        {
            Status($"The changes were not saved: {exception.Message}");
            SaveButton.IsEnabled = true;
            _log($"Particle editor save failed: {exception}");
        }
    }
}
