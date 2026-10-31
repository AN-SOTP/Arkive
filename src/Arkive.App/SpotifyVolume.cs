using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Arkive.App;

/// <summary>
/// Spotify's own volume in the Windows volume mixer: the audio sessions of Spotify's processes on every active output.
/// Spotify's in-app volume isn't reachable without signing in, so the Spotify Player tweak's slider moves this one.
/// </summary>
internal sealed class SpotifyVolume : IDisposable
{
    private readonly MMDeviceEnumerator _devices = new();

    /// <summary>Spotify's mixer volume in percent, or null when it has no audio session yet (nothing played since it started).</summary>
    public int? Read()
    {
        int? found = null;
        Visit(volume =>
        {
            found ??= (int)Math.Round(volume.Volume * 100);
        });
        return found;
    }

    /// <summary>Sets every Spotify audio session to the volume; false when there is none.</summary>
    public bool Set(int percent)
    {
        var any = false;
        Visit(volume =>
        {
            volume.Volume = Math.Clamp(percent, 0, 100) / 100f;
            any = true;
        });
        return any;
    }

    private void Visit(Action<SimpleAudioVolume> action)
    {
        var spotify = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("Spotify"))
            using (process)
                spotify.Add((uint)process.Id);
        if (spotify.Count == 0)
            return;
        foreach (var device in _devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                SessionCollection sessions;
                try
                {
                    sessions = device.AudioSessionManager.Sessions;
                }
                catch (COMException)
                {
                    continue;
                }
                for (var index = 0; index < sessions.Count; index++)
                {
                    using var session = sessions[index];
                    if (spotify.Contains(session.GetProcessID))
                        action(session.SimpleAudioVolume);
                }
            }
        }
    }

    public void Dispose() => _devices.Dispose();
}
