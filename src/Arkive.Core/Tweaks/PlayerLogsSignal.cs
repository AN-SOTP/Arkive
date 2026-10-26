using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Arkive.Core.Tweaks;

/// <summary>
/// How the Logs tab tells Arkive which player to look up. The game's interface can't go online, can't write files and
/// its clipboard never reaches Windows; it can only read files by their full path. So it spells a message by reading
/// small files in Arkive's signal folder one after another: "go", one file per hex digit, then "end" (cycling through
/// eight sets, a- to h-, so a file is read again only eight reads later, by when Arkive has locked it again; the
/// reader's own handle has to close first). Arkive holds an opportunistic lock on each
/// of its own files; Windows tells it when another program opens one, and Arkive notes the order. To the game this is
/// an ordinary file read, the same as loading the answer file. The message is the click's request id (4 hex digits),
/// the raid wanted (4 hex digits, 0 for recent logs), the name (4 hex digits per UTF-16 character) and a checksum
/// (the sum of the 4-digit groups, 4 hex digits).
/// </summary>
public sealed class PlayerLogsSignal : IDisposable
{
    public const string Begin = "go";
    public const string End = "end";
    public static IReadOnlyList<string> Sets { get; } = [.. "abcdefghijklmnop".Select(set => set.ToString())];
    private const string Digits = "0123456789abcdef";
    private const int RequestDigits = 4;
    private const int HeadDigits = RequestDigits + 4;
    private const int MaxNameLength = 32;

    private readonly string _folder;
    private readonly List<Watch> _watches = [];
    private readonly StringBuilder _message = new();
    private readonly Lock _gate = new();
    private bool _receiving;
    private string? _lastFile;
    private bool _disposed;

    public PlayerLogsSignal(string folder) => _folder = folder;

    /// <summary>A complete message: the player's name, the click's request id and the raid wanted (0: recent logs).</summary>
    public event Action<string, string, int>? Requested;

    /// <summary>A signal file could not be watched (for the log).</summary>
    public event Action<string>? Failed;

    public static IEnumerable<string> Symbols => [Begin, End, .. Digits.Select(digit => digit.ToString())];

    public static string FileName(string set, string symbol) => $"{set}-{symbol}.txt";

    /// <summary>The hex digits the tab sends for a click: request id, raid, name, checksum.</summary>
    public static string Encode(string request, int raid, string name)
    {
        var builder = new StringBuilder(request.ToLowerInvariant()).Append(raid.ToString("x4", CultureInfo.InvariantCulture));
        foreach (var character in name)
            builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
        return builder.Append(Checksum(builder.ToString()).ToString("x4", CultureInfo.InvariantCulture)).ToString();
    }

    public static bool TryDecode(string digits, out string request, out int raid, out string name)
    {
        request = "";
        raid = 0;
        name = "";
        if (digits.Length < HeadDigits + 8 || digits.Length % 4 != 0 ||
            (digits.Length - HeadDigits - 4) / 4 > MaxNameLength || digits.Any(digit => !Digits.Contains(digit)))
            return false;
        var body = digits[..^4];
        if (Checksum(body) != int.Parse(digits.AsSpan(digits.Length - 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            return false;
        request = body[..RequestDigits];
        raid = int.Parse(body.AsSpan(RequestDigits, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var builder = new StringBuilder();
        for (var at = HeadDigits; at < body.Length; at += 4)
            builder.Append((char)int.Parse(body.AsSpan(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        name = builder.ToString();
        return name.All(character => !char.IsControl(character) && character is not ('/' or '\\' or '#' or '?'));
    }

    /// <summary>The sum of the 4-digit groups, wrapped to 16 bits (the tab computes the same).</summary>
    private static int Checksum(string digits)
    {
        var sum = 0;
        for (var at = 0; at + 4 <= digits.Length; at += 4)
            sum = (sum + int.Parse(digits.AsSpan(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)) & 0xFFFF;
        return sum;
    }

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

    /// <summary>
    /// Notes one read of a signal file; "go" starts a message and "end" completes it. The tab never reads the same file
    /// twice in a row, so a second note from the same file is dropped: a virus scanner's look at the file during the
    /// tab's open shows up as a read too.
    /// </summary>
    public void Receive(string symbol, string? file = null)
    {
        string? complete = null;
        lock (_gate)
        {
            if (file is not null && file == _lastFile)
                return;
            _lastFile = file;
            if (symbol == Begin)
            {
                _message.Clear();
                _receiving = true;
                return;
            }
            if (!_receiving)
                return;
            if (symbol == End)
            {
                complete = _message.ToString();
                _message.Clear();
                _receiving = false;
            }
            else if (_message.Length < HeadDigits + 4 * MaxNameLength + 4)
                _message.Append(symbol);
        }
        if (complete is not null && TryDecode(complete, out var request, out var raid, out var name))
            Requested?.Invoke(name, request, raid);
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
    private sealed class Watch(PlayerLogsSignal owner, string path, string symbol) : IDisposable
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
            owner.Receive(symbol, path);
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
