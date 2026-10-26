using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using Arkive.Core.Tweaks;
using Arkive.Core.Updates;

namespace Arkive.App;

/// <summary>
/// The Arkive half of the Logs Tab tweak. The game's interface can't go online, so its Logs tab copies the player's
/// lostark.bible link with a request id (…/logs#arkive-ID) to the clipboard. While Arkive runs it listens for that link,
/// puts back what the clipboard held before, fetches the player's logs and writes the summary file the tab polls for.
/// Ordinary lostark.bible links (without the #arkive- mark) are left alone.
/// </summary>
public partial class MainWindow
{
    private const int ClipboardUpdateMessage = 0x031D;
    private static readonly TimeSpan PlayerLogsCacheAge = TimeSpan.FromMinutes(10);
    private static readonly Regex PlayerLogsRequest = new(
        @"^https://lostark\.bible/character/[A-Za-z]+/(?<name>[^/#?\s]+)/logs#arkive-(?<request>[0-9x]{1,40})$", RegexOptions.CultureInvariant);

    private readonly Dictionary<string, (DateTime At, PlayerLogsLookup Lookup)> _playerLogsCache = new(StringComparer.OrdinalIgnoreCase);
    private HttpClient? _playerLogsClient;
    private string? _clipboardBefore;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr window);

    private void StartPlayerLogsListener()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
            return;
        source.AddHook(PlayerLogsWindowMessage);
        if (!AddClipboardFormatListener(source.Handle))
            Log($"Logs Tab: Arkive could not listen to the clipboard (error {Marshal.GetLastWin32Error()}), so the tab will get no answers.");
        _clipboardBefore = ReadClipboardText();
    }

    private IntPtr PlayerLogsWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == ClipboardUpdateMessage)
            OnClipboardChanged();
        return IntPtr.Zero;
    }

    private async void OnClipboardChanged()
    {
        var text = ReadClipboardText();
        if (text is null || PlayerLogsRequest.Match(text) is not { Success: true } match)
        {
            _clipboardBefore = text;
            return;
        }
        if (!Tweaks.LoadState().Settings.Enabled.ContainsKey(TweakModService.PlayerLogsTweak))
            return;
        if (_clipboardBefore is not null)
            TrySetClipboardText(_clipboardBefore);
        var name = Uri.UnescapeDataString(match.Groups["name"].Value);
        var request = match.Groups["request"].Value;
        try
        {
            var lookup = await LookUpPlayerLogsAsync(name);
            var folder = Tweaks.PlayerLogsFolder;
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, PlayerLogsService.SummaryFileName(name));
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, PlayerLogsService.Summary(lookup, request));
            File.Move(temporary, path, overwrite: true);
            Log($"Logs Tab: {name} -> {lookup.Status} {lookup.Region ?? "-"} ({lookup.Logs.Count} logs).");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or
            UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Log($"Logs Tab: could not get logs for {name}: {exception.Message}");
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

    /// <summary>The clipboard's text, or null when it holds none or another program has it open.</summary>
    private static string? ReadClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static void TrySetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException)
        {
        }
    }
}
