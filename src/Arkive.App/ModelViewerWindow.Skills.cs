using System.IO;
using System.Text.Json;
using Arkive.Core.Viewer;

namespace Arkive.App;

/// <summary>The model viewer's skill preview: the page asks for a skill, Arkive prepares it and sends it back.</summary>
public partial class ModelViewerWindow
{
    private static readonly JsonSerializerOptions SkillJson = new(JsonSerializerDefaults.Web);
    private CancellationTokenSource? _skillLoading;

    /// <summary>The classes and skills a mesh can preview (null when it can't); set by Arkive's main window.</summary>
    public Func<ModelViewerRequest, CancellationToken, Task<SkillPreviewOffer?>>? SkillOffer { get; init; }

    /// <summary>Prepares one skill branch for the page (shown mesh, class file, skill id, branch, progress); set by Arkive's main window.</summary>
    public Func<ModelViewerRequest, string, int, int, Action<string, double?>, CancellationToken, Task<SkillPlayback>>? SkillPlayback { get; init; }

    /// <summary>
    /// Adds a model's skill effects to the mod open in Edit mods (shown mesh, action file, one skill or null for all)
    /// and says what it did; set by Arkive's main window.
    /// </summary>
    public Func<ModelViewerRequest, string, int?, Task<string>>? AddSkillEffects { get; init; }

    private async Task AddSkillEffectsAsync(JsonElement root)
    {
        if (AddSkillEffects is null || _request is not { } request) return;
        var classFile = root.GetProperty("classFile").GetString() ?? string.Empty;
        int? skill = root.TryGetProperty("skill", out var skillElement) && skillElement.ValueKind == JsonValueKind.Number
            ? skillElement.GetInt32() : null;
        PostSkill(new { type = "skillStatus", text = "Adding the effects to the mod…", progress = (double?)null });
        string text;
        try
        {
            text = await AddSkillEffects(request, classFile, skill);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or
                                              UnauthorizedAccessException)
        {
            text = "The effects could not be added: " + exception.Message;
            _log($"Adding skill effects failed: class={classFile}, skill={skill}, error={exception}");
        }
        PostSkill(new { type = "skillStatus", text, progress = (double?)null });
    }

    /// <summary>
    /// Adds the shown model's pictures, materials and effects to a mod and opens Recolour on them (shown mesh, action
    /// file); says what it did. Set by Arkive's main window.
    /// </summary>
    public Func<ModelViewerRequest, string, Task<string>>? RecolourModel { get; init; }

    private async Task RecolourModelAsync(JsonElement root)
    {
        if (RecolourModel is null || _request is not { } request) return;
        var classFile = root.GetProperty("classFile").GetString() ?? string.Empty;
        PostSkill(new { type = "skillStatus", text = "Adding the model's pictures, materials and effects to the mod…", progress = (double?)null });
        string text;
        try
        {
            text = await RecolourModel(request, classFile);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or
                                              UnauthorizedAccessException)
        {
            text = "The model could not be prepared for recolouring: " + exception.Message;
            _log($"Recolour model failed: class={classFile}, error={exception}");
        }
        PostSkill(new { type = "skillStatus", text, progress = (double?)null });
    }

    private string? _skillScene;

    /// <summary>The folder the effects' textures and meshes are kept in, served to the page as particles.arkive.local.</summary>
    public string? ParticleAssetRoot { get; init; }

    private void PostSkill(object message)
    {
        if (_pageReady) Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, SkillJson));
    }

    private async Task OfferSkillsAsync(ModelViewerRequest request, CancellationToken cancellationToken)
    {
        if (SkillOffer is null) return;
        try
        {
            _skillScene = null;
            // A player body or NPC model shows the Skills tab at once; the list fills it in once the game's skill data is
            // read (the tab goes away again for a model with no skills).
            if (SkillPreviewService.CanOffer(request))
            {
                await _pageReadyTask.Task.WaitAsync(cancellationToken);
                PostSkill(new { type = "skillsLoading" });
            }
            var offer = await SkillOffer(request, cancellationToken);
            await _pageReadyTask.Task.WaitAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            if (offer is null)
            {
                PostSkill(new { type = "skillsUnavailable" });
                return;
            }
            PostSkill(new { type = "skills", defaultClass = offer.DefaultClass, classes = offer.Classes, npc = offer.Npc });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log($"Skill preview could not be offered: object={request.ObjectName}, error={exception.Message}");
        }
    }

    private async Task PlaySkillAsync(JsonElement root)
    {
        if (SkillPlayback is null || _request is not { } request) return;
        _skillLoading?.Cancel();
        var loading = _skillLoading = new CancellationTokenSource();
        var classFile = root.GetProperty("classFile").GetString() ?? string.Empty;
        var skill = root.GetProperty("skill").GetInt32();
        var branch = root.TryGetProperty("branch", out var branchElement) ? branchElement.GetInt32() : 0;
        try
        {
            var playback = await SkillPlayback(request, classFile, skill, branch,
                (text, progress) => Dispatcher.InvokeAsync(() => PostSkill(new { type = "skillStatus", text, progress })), loading.Token);
            if (loading.IsCancellationRequested) return;
            // The character with its weapon (and any missing outfit half) replaces the shown mesh while skills play.
            if (playback.SceneJson is { } sceneJson && sceneJson != _skillScene)
            {
                if (_sceneLoaded.Task.IsCompleted) _sceneLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Browser.CoreWebView2.PostWebMessageAsJson(
                    $"{{\"type\":\"load\",\"theme\":\"{_theme}\",\"keepView\":true,\"skillScene\":true,\"hasEdits\":false," +
                    $"\"remember\":{(RememberViewSettings ? "true" : "false")}," +
                    $"\"baseUrl\":\"https://{SceneHost}/\",\"scene\":{sceneJson}}}");
                await _sceneLoaded.Task.WaitAsync(TimeSpan.FromMinutes(5), loading.Token);
                _skillScene = sceneJson;
            }
            PostSkill(playback.Message);
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or
                                              UnauthorizedAccessException or Arkive.Core.External.UmodelBridgeException)
        {
            PostSkill(new { type = "skillStatus", text = $"The skill could not be prepared: {exception.Message}", progress = (double?)null });
            _log($"Skill preview failed: class={classFile}, skill={skill}, error={exception}");
        }
    }
}
