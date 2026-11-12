using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Arkive.SmokeTests;

/// <summary>
/// Starts a program on a desktop of its own that is never switched to, so nothing it opens can reach the screen or take
/// focus, and watches that desktop for any visible window the program owns.
/// </summary>
internal static class HiddenDesktop
{
    internal sealed record Result(int ExitCode, bool TimedOut, IReadOnlyList<string> VisibleWindows);

    public static Result Run(string executable, string arguments, TimeSpan timeout)
    {
        var name = "ArkiveSmoke-" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = name };
            // CreateProcess may write to the command line, so it gets a buffer of its own.
            var commandLine = new StringBuilder($"\"{executable}\" {arguments}");
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero,
                    Path.GetDirectoryName(executable), ref startup, out var process))
                throw new Win32Exception();
            try
            {
                var visible = new List<string>();
                var clock = Stopwatch.StartNew();
                while (WaitForSingleObject(process.Process, 50) == WaitTimeout)
                {
                    EnumDesktopWindows(desktop, (handle, _) =>
                    {
                        GetWindowThreadProcessId(handle, out var owner);
                        if (owner == process.ProcessId && IsWindowVisible(handle)) visible.Add($"window 0x{handle.ToInt64():X}");
                        return true;
                    }, IntPtr.Zero);
                    if (clock.Elapsed <= timeout) continue;
                    TerminateProcess(process.Process, 99);
                    WaitForSingleObject(process.Process, 10_000);
                    return new Result(99, true, visible.Distinct().ToArray());
                }
                GetExitCodeProcess(process.Process, out var exitCode);
                return new Result((int)exitCode, false, visible.Distinct().ToArray());
            }
            finally
            {
                CloseHandle(process.Process);
                CloseHandle(process.Thread);
            }
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    private const uint GenericAll = 0x10000000;
    private const uint WaitTimeout = 0x102;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int CharactersWide;
        public int CharactersHigh;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDesktopW")]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr attributes);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfo startup, out ProcessInformation information);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
