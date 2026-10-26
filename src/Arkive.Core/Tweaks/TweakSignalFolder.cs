using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Arkive.Core.Tweaks;

/// <summary>
/// The files tweak code "reads" to send Arkive a message. The game's interface can't write files, go online or reach
/// the Windows clipboard; it can only read files by their full path. So it spells a message by reading small files in a
/// folder one after another: "go", one file per hex digit, then "end", cycling through sixteen sets (a- to p-) so a
/// file is read again only sixteen reads later, by when Arkive has locked it again. Arkive holds an opportunistic lock
/// on each file; Windows tells it when another program opens one, and <see cref="Read"/> reports the symbol in order.
/// To the game this is an ordinary file read, the same as loading an answer file.
/// </summary>
public sealed class TweakSignalFolder : IDisposable
{
    public const string Begin = "go";
    public const string End = "end";
    public static IReadOnlyList<string> Sets { get; } = [.. "abcdefghijklmnop".Select(set => set.ToString())];
    public const string Digits = "0123456789abcdef";

    private readonly string _folder;
    private readonly Action<string, string> _read;
    private readonly List<Watch> _watches = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <param name="read">Called with the symbol and the file's path for every read of a signal file.</param>
    public TweakSignalFolder(string folder, Action<string, string> read)
    {
        _folder = folder;
        _read = read;
    }

    /// <summary>A signal file could not be watched (for the log).</summary>
    public event Action<string>? Failed;

    public static IEnumerable<string> Symbols => [Begin, End, .. Digits.Select(digit => digit.ToString())];

    public static string FileName(string set, string symbol) => $"{set}-{symbol}.txt";

    /// <summary>Writes the signal files and starts watching them.</summary>
    public void Start()
    {
        Directory.CreateDirectory(_folder);
        foreach (var set in Sets)
            foreach (var symbol in Symbols)
            {
                var path = Path.Combine(_folder, FileName(set, symbol));
                if (!File.Exists(path))
                    File.WriteAllText(path, "1");
                var watch = new Watch(this, path, symbol);
                _watches.Add(watch);
                watch.Arm();
            }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        foreach (var watch in _watches)
            watch.Dispose();
    }

    private bool Disposed
    {
        get
        {
            lock (_gate)
                return _disposed;
        }
    }

    /// <summary>
    /// One signal file under a level 1 oplock. Another program's open breaks it: the open waits until Arkive closes its
    /// handle, which it does straight after noting the read, then Arkive opens the file again and asks for a new lock.
    /// </summary>
    private sealed class Watch(TweakSignalFolder owner, string path, string symbol) : IDisposable
    {
        private const uint GenericRead = 0x80000000;
        private const uint ShareAll = 0x7;
        private const uint OpenExisting = 3;
        private const uint FlagOverlapped = 0x40000000;
        private const uint RequestOplockLevel1 = 0x00090000;
        private const int IoPending = 997;
        private static readonly int OverlappedSize = 3 * IntPtr.Size + 8;
        private static readonly int EventOffset = 2 * IntPtr.Size + 8;

        private readonly ManualResetEvent _broken = new(false);
        private readonly IntPtr _overlapped = Marshal.AllocHGlobal(OverlappedSize);
        private SafeFileHandle? _handle;
        private RegisteredWaitHandle? _wait;
        private Timer? _retry;

        public void Arm() => TryArm(retryLater: true);

        /// <summary>Opens the file and asks for the lock; false when the lock isn't granted yet.</summary>
        private bool TryArm(bool retryLater)
        {
            if (owner.Disposed)
                return true;
            _broken.Reset();
            for (var at = 0; at < OverlappedSize; at++)
                Marshal.WriteByte(_overlapped, at, 0);
            Marshal.WriteIntPtr(_overlapped, EventOffset, _broken.SafeWaitHandle.DangerousGetHandle());
            var handle = CreateFileW(path, GenericRead, ShareAll, IntPtr.Zero, OpenExisting, FlagOverlapped, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                RetryLater($"cannot open {Path.GetFileName(path)} (error {Marshal.GetLastWin32Error()})");
                return true;
            }
            if (DeviceIoControl(handle, RequestOplockLevel1, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, _overlapped) ||
                Marshal.GetLastWin32Error() is var error && error != IoPending)
            {
                handle.Dispose();
                if (retryLater)
                    RetryLater(null);
                return false;
            }
            _handle = handle;
            _wait = ThreadPool.RegisterWaitForSingleObject(_broken, (_, _) => OnBroken(), null, Timeout.Infinite, executeOnlyOnce: true);
            return true;
        }

        /// <summary>Someone opened the file: note it, let their open go ahead, and lock the file again.</summary>
        private void OnBroken()
        {
            if (owner.Disposed)
                return;
            owner._read(symbol, path);
            _handle?.Dispose();
            _handle = null;
            // The reader's open goes ahead now; the lock is granted again once its handle closes, normally well under a
            // millisecond later. Ask again straight away (yielding, since Sleep(1) can take 15 ms) for a short while,
            // then fall back to the slower retry.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 50)
            {
                if (TryArm(retryLater: false))
                    return;
                if (!Thread.Yield())
                    Thread.SpinWait(200);
            }
            Arm();
        }

        /// <summary>The lock wasn't granted (another program had the file open); try again shortly.</summary>
        private void RetryLater(string? problem)
        {
            if (problem is not null)
                owner.Failed?.Invoke(problem);
            _retry?.Dispose();
            _retry = new Timer(_ => Arm(), null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            _retry?.Dispose();
            _wait?.Unregister(null);
            if (_handle is { } handle)
            {
                // Closing the handle completes the pending lock request; wait for that before freeing its OVERLAPPED.
                handle.Dispose();
                _broken.WaitOne(TimeSpan.FromSeconds(1));
            }
            Marshal.FreeHGlobal(_overlapped);
            _broken.Dispose();
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation,
            uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize, IntPtr output,
            int outputSize, IntPtr returned, IntPtr overlapped);
    }
}
