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

    /// <summary>Prepares one skill branch for the page (class file, skill id, branch, progress); set by Arkive's main window.</summary>
    public Func<string, int, int, Action<string>, CancellationToken, Task<object>>? SkillPlayback { get; init; }

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
            var offer = await SkillOffer(request, cancellationToken);
            await _pageReadyTask.Task.WaitAsync(cancellationToken);
            if (offer is null || cancellationToken.IsCancellationRequested) return;
            PostSkill(new { type = "skills", defaultClass = offer.DefaultClass, classes = offer.Classes });
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
        if (SkillPlayback is null) return;
        _skillLoading?.Cancel();
        var loading = _skillLoading = new CancellationTokenSource();
        var classFile = root.GetProperty("classFile").GetString() ?? string.Empty;
        var skill = root.GetProperty("skill").GetInt32();
        var branch = root.TryGetProperty("branch", out var branchElement) ? branchElement.GetInt32() : 0;
        try
        {
            var message = await SkillPlayback(classFile, skill, branch,
                text => Dispatcher.Invoke(() => PostSkill(new { type = "skillStatus", text })), loading.Token);
            if (!loading.IsCancellationRequested) PostSkill(message);
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or
                                              UnauthorizedAccessException or Arkive.Core.External.UmodelBridgeException)
        {
            PostSkill(new { type = "skillStatus", text = $"The skill could not be prepared: {exception.Message}" });
            _log($"Skill preview failed: class={classFile}, skill={skill}, error={exception}");
        }
    }
}
