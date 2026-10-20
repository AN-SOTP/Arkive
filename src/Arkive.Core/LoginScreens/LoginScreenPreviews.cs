using System.Runtime.InteropServices;
using Arkive.Core.Textures;

namespace Arkive.Core.LoginScreens;

/// <summary>
/// Still pictures of the login videos, decoded with the game's own Bink 2 library (Binaries\Win64\bink2w64.dll).
/// The video is unscrambled into a temporary file, one frame is decoded and scaled down, and the picture is kept in
/// Arkive's cache. Nothing in the game folder is written.
/// </summary>
public sealed class LoginScreenPreviews
{
    private const int PreviewWidth = 640;
    private const int Frame = 30;
    private static readonly object BinkGate = new();
    private static Bink? _bink;

    private readonly string _gameRoot;
    private readonly string _cacheDirectory;

    /// <param name="efGameDirectory">The game's EFGame folder; the library is beside it in Binaries\Win64.</param>
    public LoginScreenPreviews(string efGameDirectory, string dataRoot)
    {
        _gameRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(efGameDirectory)))!;
        _cacheDirectory = Path.Combine(dataRoot, "cache", "login-screens");
    }

    public string LibraryPath => Path.Combine(_gameRoot, "Binaries", "Win64", "bink2w64.dll");

    public bool Available => OperatingSystem.IsWindows() && File.Exists(LibraryPath);

    /// <summary>The cached picture of <paramref name="videoPath"/>, made first if needed; null when it cannot be decoded.</summary>
    /// <param name="name">The video's game name, for the cache file.</param>
    public async Task<string?> GetAsync(string name, string videoPath, CancellationToken cancellationToken = default)
    {
        if (!Available || !File.Exists(videoPath)) return null;
        var info = new FileInfo(videoPath);
        var cached = Path.Combine(_cacheDirectory, $"{name}-{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}.png");
        if (File.Exists(cached)) return cached;
        var image = await Task.Run(() => Decode(videoPath, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (image is null) return null;
        Directory.CreateDirectory(_cacheDirectory);
        foreach (var old in Directory.EnumerateFiles(_cacheDirectory, name + "-*.png")) File.Delete(old);
        var writing = cached + ".tmp";
        await PngRgbaCodec.WriteAsync(writing, image, cancellationToken).ConfigureAwait(false);
        File.Move(writing, cached, overwrite: true);
        return cached;
    }

    private RgbaImage? Decode(string videoPath, CancellationToken cancellationToken)
    {
        var key = LoginVideoCipher.FindKey(videoPath);
        if (key is null) return null;
        // Bink opens files by an ANSI path, so the plain copy goes in the system temp folder under a plain name.
        var plain = Path.Combine(Path.GetTempPath(), $"arkive-login-{Guid.NewGuid():N}.bk2");
        try
        {
            LoginVideoCipher.Transcode(videoPath, key, plain, null, cancellationToken);
            lock (BinkGate)
            {
                _bink ??= Bink.Load(LibraryPath);
                return _bink?.Grab(plain, Frame, PreviewWidth);
            }
        }
        finally
        {
            try { File.Delete(plain); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The few Bink calls a still needs.</summary>
    private sealed class Bink
    {
        // BINKSURFACE32: 8-bit blue, green, red, unused.
        private const uint Surface32 = 3;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr OpenFn([MarshalAs(UnmanagedType.LPStr)] string name, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DoFrameFn(IntPtr bink);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void BinkFn(IntPtr bink);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SoundTrackFn(uint count, IntPtr tracks);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CopyFn(IntPtr bink, IntPtr destination, int pitch, uint height, uint x, uint y, uint flags);

        private readonly OpenFn _open;
        private readonly DoFrameFn _doFrame;
        private readonly BinkFn _nextFrame;
        private readonly BinkFn _close;
        private readonly SoundTrackFn _soundTrack;
        private readonly CopyFn _copy;

        private Bink(IntPtr library)
        {
            T Export<T>(string name) where T : Delegate =>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            _open = Export<OpenFn>("BinkOpen");
            _doFrame = Export<DoFrameFn>("BinkDoFrame");
            _nextFrame = Export<BinkFn>("BinkNextFrame");
            _close = Export<BinkFn>("BinkClose");
            _soundTrack = Export<SoundTrackFn>("BinkSetSoundTrack");
            _copy = Export<CopyFn>("BinkCopyToBuffer");
        }

        public static Bink? Load(string path)
        {
            try
            {
                return new Bink(NativeLibrary.Load(path));
            }
            catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        public RgbaImage? Grab(string path, int frame, int width)
        {
            _soundTrack(0, IntPtr.Zero);
            var bink = _open(path, 0);
            if (bink == IntPtr.Zero) return null;
            var buffer = IntPtr.Zero;
            try
            {
                // The BINK structure starts with its width, height and frame count.
                var videoWidth = Marshal.ReadInt32(bink, 0);
                var videoHeight = Marshal.ReadInt32(bink, 4);
                var frames = Marshal.ReadInt32(bink, 8);
                if (videoWidth <= 0 || videoHeight <= 0 || videoWidth > 16384 || videoHeight > 16384) return null;
                var last = Math.Clamp(frame, 1, Math.Max(1, frames));
                for (var index = 1; index <= last; index++)
                {
                    _doFrame(bink);
                    if (index < last) _nextFrame(bink);
                }
                var pitch = videoWidth * 4;
                buffer = Marshal.AllocHGlobal(pitch * videoHeight);
                _copy(bink, buffer, pitch, (uint)videoHeight, 0, 0, Surface32);
                var pixels = new byte[pitch * videoHeight];
                Marshal.Copy(buffer, pixels, 0, pixels.Length);
                return Shrink(pixels, videoWidth, videoHeight, width);
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                _close(bink);
            }
        }

        // Box-filters the BGRx frame down to the preview width, as opaque RGBA.
        private static RgbaImage Shrink(byte[] bgrx, int width, int height, int targetWidth)
        {
            targetWidth = Math.Min(targetWidth, width);
            var targetHeight = Math.Max(1, (int)Math.Round((double)height * targetWidth / width));
            var result = new byte[targetWidth * targetHeight * 4];
            for (var y = 0; y < targetHeight; y++)
            {
                var y0 = y * height / targetHeight;
                var y1 = Math.Max(y0 + 1, (y + 1) * height / targetHeight);
                for (var x = 0; x < targetWidth; x++)
                {
                    var x0 = x * width / targetWidth;
                    var x1 = Math.Max(x0 + 1, (x + 1) * width / targetWidth);
                    long blue = 0, green = 0, red = 0;
                    for (var sy = y0; sy < y1; sy++)
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var at = (sy * width + sx) * 4;
                        blue += bgrx[at];
                        green += bgrx[at + 1];
                        red += bgrx[at + 2];
                    }
                    var count = (y1 - y0) * (x1 - x0);
                    var to = (y * targetWidth + x) * 4;
                    result[to] = (byte)(red / count);
                    result[to + 1] = (byte)(green / count);
                    result[to + 2] = (byte)(blue / count);
                    result[to + 3] = 255;
                }
            }
            return new RgbaImage(targetWidth, targetHeight, result);
        }
    }
}
