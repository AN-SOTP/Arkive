using System.IO;
using System.Net.Http;
using Arkive.Core.Tweaks;
using Arkive.Core.Updates;

namespace Arkive.App;

/// <summary>
/// The Arkive half of the Logs Tab tweak. The game's interface can't go online, so its Logs tab spells the player's
/// name by reading Arkive's signal files in order (see <see cref="PlayerLogsSignal"/>). While Arkive runs it watches
/// those files, fetches the player's logs and writes the answer file "&lt;request&gt;.txt" that the tab polls for.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan PlayerLogsCacheAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PlayerLogsAnswerAge = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, (DateTime At, PlayerLogsLookup Lookup)> _playerLogsCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _playerLogsGate = new(1, 1);
    private HttpClient? _playerLogsClient;
    private PlayerLogsSignal? _playerLogsSignal;

    private void StartPlayerLogsListener()
    {
        try
        {
            _playerLogsSignal = new PlayerLogsSignal(Path.Combine(Tweaks.PlayerLogsFolder, "signal"));
            _playerLogsSignal.Requested += (name, request) => _ = AnswerPlayerLogsAsync(name, request);
            _playerLogsSignal.Failed += problem => Dispatcher.InvokeAsync(() => Log($"Logs Tab: {problem}"));
            _playerLogsSignal.Start();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Logs Tab: Arkive could not set up its signal files, so the tab will get no answers: {exception.Message}");
        }
    }

    private async Task AnswerPlayerLogsAsync(string name, string request)
    {
        if (!Tweaks.LoadState().Settings.Enabled.ContainsKey(TweakModService.PlayerLogsTweak))
            return;
        await _playerLogsGate.WaitAsync();
        try
        {
            var lookup = await LookUpPlayerLogsAsync(name);
            var folder = Tweaks.PlayerLogsFolder;
            Directory.CreateDirectory(folder);
            foreach (var old in new DirectoryInfo(folder).EnumerateFiles("*.txt"))
                if (DateTime.UtcNow - old.LastWriteTimeUtc > PlayerLogsAnswerAge)
                    old.Delete();
            var path = Path.Combine(folder, request + ".txt");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, PlayerLogsService.Summary(lookup, request));
            File.Move(temporary, path, overwrite: true);
            await Dispatcher.InvokeAsync(() => Log($"Logs Tab: {name} -> {lookup.Status} {lookup.Region ?? "-"} ({lookup.Logs.Count} logs)."));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or
            UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            await Dispatcher.InvokeAsync(() => Log($"Logs Tab: could not get logs for {name}: {exception.Message}"));
        }
        finally
        {
            _playerLogsGate.Release();
        }
    }

    /// <summary>The player's logs, from a lookup of the last few minutes when there is one.</summary>
    private async Task<PlayerLogsLookup> LookUpPlayerLogsAsync(string name)
    {
        if (_playerLogsCache.TryGetValue(name, out var cached) && DateTime.UtcNow - cached.At < PlayerLogsCacheAge)
            return cached.Lookup;
        _playerLogsClient ??= CreatePlayerLogsClient();
        var lookup = await new PlayerLogsService(_playerLogsClient).FetchAsync(name);
        _playerLogsCache[name] = (DateTime.UtcNow, lookup);
        return lookup;
    }

    private static HttpClient CreatePlayerLogsClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Arkive/{ArkiveReleasePolicy.CurrentVersion.ToString(3)} (+https://github.com/AN-SOTP/Arkive)");
        return client;
    }
}
