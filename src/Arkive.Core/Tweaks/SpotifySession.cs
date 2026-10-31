using System.Runtime.InteropServices;

namespace Arkive.Core.Tweaks;

/// <summary>Spotify's playback state, as its Windows media session reports it (the values are Windows').</summary>
public enum SpotifyPlayback
{
    Closed = 0,
    Opened = 1,
    Changing = 2,
    Stopped = 3,
    Playing = 4,
    Paused = 5,
}

/// <summary>Spotify's repeat setting, as Windows names it: off, the track, the whole list.</summary>
public enum SpotifyRepeat
{
    Off = 0,
    Track = 1,
    List = 2,
}

/// <summary>What the media session lets another app do right now.</summary>
[Flags]
public enum SpotifyControls
{
    None = 0,
    PlayPause = 1,
    Previous = 2,
    Next = 4,
    Stop = 8,
    Shuffle = 16,
    Repeat = 32,
    Seek = 64,
}

/// <summary>
/// The track Spotify has, how far in it is (worked out to now while it plays), what can be pressed, and shuffle and
/// repeat (null when the session doesn't say).
/// </summary>
public sealed record SpotifyNowPlaying(
    string Title,
    string Artist,
    string Album,
    SpotifyPlayback Playback,
    TimeSpan Position,
    TimeSpan Length,
    SpotifyControls Controls,
    bool? Shuffle,
    SpotifyRepeat? Repeat);

/// <summary>A button of the Spotify Player tweak.</summary>
public enum SpotifyCommand
{
    PlayPause,
    Next,
    Previous,
    Stop,
    Shuffle,
    Repeat,
}

/// <summary>
/// Spotify's Windows media session (the one the volume flyout and media keys use), read and pressed through the
/// Windows Runtime's GlobalSystemMediaTransportControlsSessionManager. Nothing here signs in to Spotify or brings its
/// window forward: Windows passes the presses to the running app. Calls go straight through the Windows Runtime's COM
/// interfaces (their ids and method order from Windows.Media.winmd), so Arkive needs no Windows SDK projection; the
/// asynchronous calls are waited on by polling IAsyncInfo. Use it from one thread.
/// </summary>
public sealed class SpotifyMediaSession : IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    private static readonly Guid ManagerStatics = new("2050c4ee-11a0-57de-aed7-c97c70338245");
    private static readonly Guid AsyncInfo = new("00000036-0000-0000-c000-000000000046");
    private const string ManagerClass = "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager";

    // Slots in each interface's method table: IUnknown and IInspectable take the first six.
    private const int ManagerGetSessions = 7;
    private const int VectorGetAt = 6, VectorSize = 7;
    private const int SessionAppId = 6, SessionMediaProperties = 7, SessionTimeline = 8, SessionPlaybackInfo = 9;
    private const int SessionPause = 11, SessionStop = 12, SessionNext = 16, SessionPrevious = 17, SessionTogglePlayPause = 20;
    private const int SessionChangeRepeat = 21, SessionChangeShuffle = 23, SessionChangePosition = 24;
    private const int PropertiesTitle = 6, PropertiesArtist = 9, PropertiesAlbum = 10;
    private const int TimelineStart = 6, TimelineEnd = 7, TimelinePosition = 10, TimelineUpdated = 11;
    private const int InfoControls = 6, InfoStatus = 7, InfoRepeat = 9, InfoShuffle = 11;
    private const int ControlsPlay = 6, ControlsPause = 7, ControlsStop = 8, ControlsNext = 12, ControlsPrevious = 13;
    private const int ControlsPlayPause = 16, ControlsShuffle = 17, ControlsRepeat = 18, ControlsPosition = 20;
    private const int ReferenceValue = 6;
    private const int OperationGetResults = 8;
    private const int InfoGetStatus = 7, InfoCancel = 9;

    private IntPtr _manager;

    /// <summary>Spotify's session as it is now, or null when Spotify isn't running or has no session.</summary>
    public SpotifyNowPlaying? Read()
    {
        var session = FindSpotify();
        if (session == IntPtr.Zero)
            return null;
        try
        {
            var (title, artist, album) = ReadProperties(session);
            var info = Pointer(session, SessionPlaybackInfo);
            SpotifyPlayback playback;
            SpotifyControls controls;
            bool? shuffle;
            SpotifyRepeat? repeat;
            try
            {
                playback = (SpotifyPlayback)Int32(info, InfoStatus);
                controls = ReadControls(info);
                shuffle = Reference(info, InfoShuffle, boolean: true) is { } shuffleValue ? shuffleValue != 0 : null;
                var repeatValue = Reference(info, InfoRepeat, boolean: false);
                repeat = repeatValue is >= 0 and <= 2 ? (SpotifyRepeat)repeatValue : null;
            }
            finally
            {
                Marshal.Release(info);
            }
            var timeline = Pointer(session, SessionTimeline);
            TimeSpan position, length;
            try
            {
                var start = Int64(timeline, TimelineStart);
                length = TimeSpan.FromTicks(Math.Max(0, Int64(timeline, TimelineEnd) - start));
                position = TimeSpan.FromTicks(Int64(timeline, TimelinePosition) - start);
                // The position is as of the session's last update; while playing, it has moved on since.
                var updated = Int64(timeline, TimelineUpdated);
                if (playback == SpotifyPlayback.Playing && updated > 0)
                    position += DateTime.UtcNow - DateTime.FromFileTimeUtc(updated);
            }
            finally
            {
                Marshal.Release(timeline);
            }
            if (position < TimeSpan.Zero)
                position = TimeSpan.Zero;
            if (length > TimeSpan.Zero && position > length)
                position = length;
            return new SpotifyNowPlaying(title, artist, album, playback, position, length, controls, shuffle, repeat);
        }
        finally
        {
            Marshal.Release(session);
        }
    }

    /// <summary>
    /// Presses a button on Spotify's session. Stop pauses and goes back to the start when the session has no stop.
    /// Shuffle and repeat change only when the session allows it. False when Spotify isn't there or didn't take it.
    /// </summary>
    public bool Send(SpotifyCommand command)
    {
        var session = FindSpotify();
        if (session == IntPtr.Zero)
            return false;
        try
        {
            var info = Pointer(session, SessionPlaybackInfo);
            SpotifyControls controls;
            bool? shuffle;
            int? repeat;
            try
            {
                controls = ReadControls(info);
                shuffle = Reference(info, InfoShuffle, boolean: true) is { } shuffleValue ? shuffleValue != 0 : null;
                repeat = Reference(info, InfoRepeat, boolean: false);
            }
            finally
            {
                Marshal.Release(info);
            }
            switch (command)
            {
                case SpotifyCommand.PlayPause:
                    return Press(session, SessionTogglePlayPause);
                case SpotifyCommand.Next:
                    return Press(session, SessionNext);
                case SpotifyCommand.Previous:
                    return Press(session, SessionPrevious);
                case SpotifyCommand.Stop:
                    if (controls.HasFlag(SpotifyControls.Stop))
                        return Press(session, SessionStop);
                    var paused = Press(session, SessionPause);
                    if (controls.HasFlag(SpotifyControls.Seek))
                        paused &= Finish(Method<WithInt64>(session, SessionChangePosition)(session, 0, out var seek), seek);
                    return paused;
                case SpotifyCommand.Shuffle:
                    if (!controls.HasFlag(SpotifyControls.Shuffle))
                        return false;
                    return Finish(Method<WithByte>(session, SessionChangeShuffle)(session, shuffle == true ? (byte)0 : (byte)1, out var shuffled), shuffled);
                case SpotifyCommand.Repeat:
                    if (!controls.HasFlag(SpotifyControls.Repeat))
                        return false;
                    // Spotify's own order: off, the whole list, the track.
                    var next = repeat switch
                    {
                        (int)SpotifyRepeat.List => SpotifyRepeat.Track,
                        (int)SpotifyRepeat.Track => SpotifyRepeat.Off,
                        _ => SpotifyRepeat.List,
                    };
                    return Finish(Method<WithInt32>(session, SessionChangeRepeat)(session, (int)next, out var repeated), repeated);
                default:
                    return false;
            }
        }
        finally
        {
            Marshal.Release(session);
        }
    }

    public void Dispose()
    {
        if (_manager != IntPtr.Zero)
            Marshal.Release(_manager);
        _manager = IntPtr.Zero;
    }

    /// <summary>The first media session whose app id names Spotify (desktop "Spotify.exe" or the Store app), or zero.</summary>
    private IntPtr FindSpotify()
    {
        var manager = Manager();
        IntPtr sessions;
        try
        {
            sessions = Pointer(manager, ManagerGetSessions);
        }
        catch (COMException)
        {
            // The media service went away (it restarts with Explorer): ask for a new manager next time.
            Dispose();
            throw;
        }
        try
        {
            var count = Int32(sessions, VectorSize);
            for (var index = 0; index < count; index++)
            {
                Check(Method<GetAt>(sessions, VectorGetAt)(sessions, (uint)index, out var session));
                if (session == IntPtr.Zero)
                    continue;
                if (String(session, SessionAppId).Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                    return session;
                Marshal.Release(session);
            }
            return IntPtr.Zero;
        }
        finally
        {
            Marshal.Release(sessions);
        }
    }

    private IntPtr Manager()
    {
        if (_manager != IntPtr.Zero)
            return _manager;
        // Joins the multithreaded apartment; a thread already in one (or in another) is fine as it is.
        _ = RoInitialize(1);
        Check(WindowsCreateString(ManagerClass, ManagerClass.Length, out var name));
        IntPtr statics;
        try
        {
            var id = ManagerStatics;
            Check(RoGetActivationFactory(name, ref id, out statics));
        }
        finally
        {
            WindowsDeleteString(name);
        }
        try
        {
            var operation = Pointer(statics, 6);
            try
            {
                Wait(operation);
                Check(Method<GetPointer>(operation, OperationGetResults)(operation, out _manager));
            }
            finally
            {
                Marshal.Release(operation);
            }
        }
        finally
        {
            Marshal.Release(statics);
        }
        return _manager;
    }

    private static (string Title, string Artist, string Album) ReadProperties(IntPtr session)
    {
        var operation = Pointer(session, SessionMediaProperties);
        try
        {
            Wait(operation);
            Check(Method<GetPointer>(operation, OperationGetResults)(operation, out var properties));
            if (properties == IntPtr.Zero)
                return ("", "", "");
            try
            {
                return (String(properties, PropertiesTitle), String(properties, PropertiesArtist), String(properties, PropertiesAlbum));
            }
            finally
            {
                Marshal.Release(properties);
            }
        }
        finally
        {
            Marshal.Release(operation);
        }
    }

    private static SpotifyControls ReadControls(IntPtr info)
    {
        var controls = Pointer(info, InfoControls);
        try
        {
            var found = SpotifyControls.None;
            if (Bool(controls, ControlsPlayPause) || Bool(controls, ControlsPlay) || Bool(controls, ControlsPause))
                found |= SpotifyControls.PlayPause;
            if (Bool(controls, ControlsPrevious))
                found |= SpotifyControls.Previous;
            if (Bool(controls, ControlsNext))
                found |= SpotifyControls.Next;
            if (Bool(controls, ControlsStop))
                found |= SpotifyControls.Stop;
            if (Bool(controls, ControlsShuffle))
                found |= SpotifyControls.Shuffle;
            if (Bool(controls, ControlsRepeat))
                found |= SpotifyControls.Repeat;
            if (Bool(controls, ControlsPosition))
                found |= SpotifyControls.Seek;
            return found;
        }
        finally
        {
            Marshal.Release(controls);
        }
    }

    /// <summary>Calls a method that takes nothing and returns an IAsyncOperation&lt;bool&gt;, and waits for its answer.</summary>
    private static bool Press(IntPtr session, int slot)
    {
        Check(Method<GetPointer>(session, slot)(session, out var operation));
        return Finish(0, operation);
    }

    /// <summary>Waits for an IAsyncOperation&lt;bool&gt; the call returned and reads its answer.</summary>
    private static bool Finish(int result, IntPtr operation)
    {
        Check(result);
        try
        {
            Wait(operation);
            Check(Method<GetByte>(operation, OperationGetResults)(operation, out var done));
            return done != 0;
        }
        finally
        {
            Marshal.Release(operation);
        }
    }

    /// <summary>Waits for an asynchronous call to finish; throws when it failed, was cancelled or took too long.</summary>
    private static void Wait(IntPtr operation)
    {
        var id = AsyncInfo;
        Check(Marshal.QueryInterface(operation, in id, out var info));
        try
        {
            var started = Environment.TickCount64;
            while (true)
            {
                var status = Int32(info, InfoGetStatus);
                if (status == 1)
                    return;
                if (status != 0)
                    throw new COMException($"A Windows media call ended with status {status}.");
                if (Environment.TickCount64 - started > CallTimeout.TotalMilliseconds)
                {
                    Method<Call>(info, InfoCancel)(info);
                    throw new TimeoutException("A Windows media call didn't answer.");
                }
                Thread.Sleep(2);
            }
        }
        finally
        {
            Marshal.Release(info);
        }
    }

    private static IntPtr Pointer(IntPtr self, int slot)
    {
        Check(Method<GetPointer>(self, slot)(self, out var value));
        if (value == IntPtr.Zero)
            throw new COMException("A Windows media call returned nothing.");
        return value;
    }

    private static int Int32(IntPtr self, int slot)
    {
        Check(Method<GetInt32>(self, slot)(self, out var value));
        return value;
    }

    private static long Int64(IntPtr self, int slot)
    {
        Check(Method<GetInt64>(self, slot)(self, out var value));
        return value;
    }

    private static bool Bool(IntPtr self, int slot)
    {
        Check(Method<GetByte>(self, slot)(self, out var value));
        return value != 0;
    }

    /// <summary>An IReference&lt;bool&gt; or IReference&lt;enum&gt; property's value, or null when the session leaves it unset.</summary>
    private static int? Reference(IntPtr self, int slot, bool boolean)
    {
        Check(Method<GetPointer>(self, slot)(self, out var reference));
        if (reference == IntPtr.Zero)
            return null;
        try
        {
            if (boolean)
            {
                Check(Method<GetByte>(reference, ReferenceValue)(reference, out var flag));
                return flag;
            }
            Check(Method<GetInt32>(reference, ReferenceValue)(reference, out var value));
            return value;
        }
        finally
        {
            Marshal.Release(reference);
        }
    }

    private static string String(IntPtr self, int slot)
    {
        Check(Method<GetPointer>(self, slot)(self, out var text));
        if (text == IntPtr.Zero)
            return "";
        try
        {
            var buffer = WindowsGetStringRawBuffer(text, out var length);
            return Marshal.PtrToStringUni(buffer, (int)length) ?? "";
        }
        finally
        {
            WindowsDeleteString(text);
        }
    }

    private static T Method<T>(IntPtr self, int slot) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(self);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, slot * IntPtr.Size));
    }

    private static void Check(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Call(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPointer(IntPtr self, out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetByte(IntPtr self, out byte value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInt32(IntPtr self, out int value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInt64(IntPtr self, out long value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAt(IntPtr self, uint index, out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WithByte(IntPtr self, byte value, out IntPtr operation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WithInt32(IntPtr self, int value, out IntPtr operation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WithInt64(IntPtr self, long value, out IntPtr operation);

    [DllImport("combase.dll")]
    private static extern int RoInitialize(int type);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string text, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
}
