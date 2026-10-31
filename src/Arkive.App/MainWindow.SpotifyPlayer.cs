using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Arkive.Core.Tweaks;

namespace Arkive.App;

/// <summary>
/// The Arkive half of the Spotify Player tweak. While the tweak is on and Lost Ark runs, a background thread writes
/// Spotify's state (track, playing or paused, time, shuffle, repeat, mixer volume) to the file the tweak reads every
/// second, and carries out the buttons it sends on the Data Bus. Spotify is driven through its Windows media session and
/// the volume mixer: no sign-in, and its window is never brought forward.
/// </summary>
public partial class MainWindow
{
    private const int SpotifyTickMs = 1000;
    private const int SpotifyCheckMs = 5000;

    /// <summary>How long to let Spotify act on a button before writing its state again.</summary>
    private const int SpotifySettleMs = 150;

    private readonly BlockingCollection<SpotifyRequest> _spotifyRequests = new();

    private void StartSpotifyPlayer()
    {
        var folder = Tweaks.SpotifyFolder;
        var thread = new Thread(() => RunSpotifyPlayer(folder)) { IsBackground = true, Name = "Spotify Player" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>A button or a move from the tweak (Data Bus channel "spotify").</summary>
    private void QueueSpotifyRequest(string text)
    {
        if (SpotifyPlayer.ReadRequest(text) is { } request)
            _spotifyRequests.Add(request);
        else
            Dispatcher.InvokeAsync(() => Log($"Spotify Player: ignored a message Arkive doesn't understand ({text.Replace('\t', '|')})."));
    }

    private void RunSpotifyPlayer(string folder)
    {
        using var media = new SpotifyMediaSession();
        using var volume = new SpotifyVolume();
        var layoutPath = Path.Combine(folder, SpotifyPlayer.LayoutFile);
        var statePath = Path.Combine(folder, SpotifyPlayer.StateFile);
        var layout = "";
        try
        {
            if (File.Exists(layoutPath))
                layout = File.ReadAllText(layoutPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        long sequence = 0;
        var checkedAt = long.MinValue;
        var active = false;
        bool? found = null;
        var problem = "";
        void Report(string message)
        {
            if (message == problem)
                return;
            problem = message;
            if (message.Length > 0)
                Dispatcher.InvokeAsync(() => Log($"Spotify Player: {message}"));
        }
        while (true)
        {
            var request = _spotifyRequests.TryTake(out var taken, SpotifyTickMs) ? taken : null;
            if (Environment.TickCount64 - checkedAt >= SpotifyCheckMs)
            {
                checkedAt = Environment.TickCount64;
                active = SpotifyWanted();
            }
            try
            {
                if (request?.Layout is { } moved)
                {
                    layout = moved;
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(layoutPath, layout);
                }
                if (request?.Command is { } command && !media.Send(command))
                    Report($"Spotify didn't take {command} (is it running and playing something?).");
                if (request?.Volume is { } percent && !volume.Set(percent))
                    Report("Spotify's volume can't be set until it has played something since it started.");
                if (request is not null)
                    Thread.Sleep(SpotifySettleMs);
                if (!active)
                    continue;
                var now = media.Read();
                if (now is not null != found)
                {
                    found = now is not null;
                    var message = now is null ? "Spotify isn't running." : "following Spotify.";
                    Dispatcher.InvokeAsync(() => Log($"Spotify Player: {message}"));
                }
                int? percentNow = null;
                try
                {
                    percentNow = now is null ? null : volume.Read();
                }
                catch (COMException)
                {
                }
                var text = SpotifyPlayer.Describe(now, percentNow, layout, ++sequence);
                Directory.CreateDirectory(folder);
                var temporary = statePath + ".tmp";
                File.WriteAllText(temporary, text);
                File.Move(temporary, statePath, overwrite: true);
                Report("");
            }
            catch (Exception exception) when (exception is COMException or TimeoutException or InvalidCastException or
                IOException or UnauthorizedAccessException)
            {
                Report($"{exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    /// <summary>True while the tweak is switched on and Lost Ark is running (only then is there anyone to read the file).</summary>
    private bool SpotifyWanted()
    {
        try
        {
            if (!Tweaks.LoadState().Settings.Enabled.ContainsKey(SpotifyPlayer.Tweak))
                return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        var game = Process.GetProcessesByName("LOSTARK");
        foreach (var process in game)
            process.Dispose();
        return game.Length > 0;
    }
}
