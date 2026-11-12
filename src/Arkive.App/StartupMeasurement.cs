using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Arkive.Core.Diagnostics;
using Arkive.Core.External;

namespace Arkive.App;

/// <param name="ForcedGc">Collect fully after each step, so the memory column is what Arkive keeps.</param>
/// <param name="WaitBackground">Also wait for and time the restored Library search that startup leaves running.</param>
/// <param name="DataRoot">Measure this data folder instead of the usual one (sets ARKIVE_DATA_ROOT for the run).</param>
internal sealed record StartupMeasurementOptions(string OutputPath, bool ForcedGc, bool WaitBackground, string? DataRoot)
{
    public const string Switch = "--measure-startup";

    /// <summary>The options of a measuring run, or null for a normal launch.</summary>
    public static StartupMeasurementOptions? Parse(IReadOnlyList<string> arguments)
    {
        var index = Find(arguments, Switch);
        if (index < 0) return null;
        var output = Value(arguments, index) ?? throw new ArgumentException($"{Switch} needs the file to write the results to.");
        var dataRoot = Find(arguments, "--data-root") is var rootIndex and >= 0
            ? Value(arguments, rootIndex) ?? throw new ArgumentException("--data-root needs a folder.")
            : null;
        return new StartupMeasurementOptions(Path.GetFullPath(output), Find(arguments, "--forced-gc") >= 0,
            Find(arguments, "--wait-background") >= 0, dataRoot is null ? null : Path.GetFullPath(dataRoot));
    }

    private static int Find(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count; index++)
            if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    private static string? Value(IReadOnlyList<string> arguments, int index) =>
        index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal) ? arguments[index + 1] : null;
}

/// <summary>
/// Arkive.App.exe --measure-startup OUT.json: runs the startup loading steps with the main window created but never
/// shown, writes the per-step results and exits. It writes no user state (see MainWindow.RunStartupAsync for what is
/// skipped) and its session log goes to measure-logs. If the process ever shows a window, the window is hidden at
/// once and the run stops with an error.
/// </summary>
internal static class StartupMeasurement
{
    public const string LogFolderName = "measure-logs";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(60);
    private static readonly object FinishLock = new();
    private static bool _finished;

    /// <summary>The measuring run in progress, or null in a normal launch.</summary>
    public static StartupMeasurementOptions? Current { get; private set; }

    public static void Start(App app, StartupMeasurementOptions options)
    {
        Current = options;
        if (options.DataRoot is { } dataRoot) Environment.SetEnvironmentVariable("ARKIVE_DATA_ROOT", dataRoot);
        SessionLog.FolderName = LogFolderName;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var steps = new StartupSteps(app.Dispatcher, options.ForcedGc);
        var root = ArkiveRuntimePaths.ResolveDataRoot();
        // Error messages would open a window; a measuring run records them and stops instead.
        UnexpectedErrors.Prompt = (notice, _) =>
        {
            Finish(steps, root, options, $"{notice.Title}: {notice.Message}", 1);
            return false;
        };
        WatchForVisibleWindows(() => Finish(steps, root, options, "A window became visible; it was hidden and the run stopped.", 3));
        new Thread(() =>
        {
            Thread.Sleep(Timeout);
            Finish(steps, root, options, $"The run did not finish within {Timeout.TotalMinutes:N0} minutes.", 2);
        }) { IsBackground = true, Name = "Arkive measuring time limit" }.Start();
        app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => RunAsync(steps, root, options));
    }

    private static async Task RunAsync(StartupSteps steps, string root, StartupMeasurementOptions options)
    {
        string? error = null;
        MainWindow? window = null;
        try
        {
            steps.Run("window", () => window = new MainWindow());
            await window!.RunStartupAsync(steps);
            if (options.WaitBackground) await steps.RunAsync("library-search", window.RestoredBackgroundWork);
        }
        catch (Exception exception)
        {
            error = exception.ToString();
        }
        steps.Stop();
        var report = steps.Recorder.Report("startup", root, error);
        // Work startup leaves running may be writing a cache; it is let finish rather than cut off by the exit.
        if (window is not null && error is null && !options.WaitBackground)
        {
            try { await window.RestoredBackgroundWork(); }
            catch (Exception exception) { report = report with { Error = "Background work failed after loading: " + exception.Message }; }
        }
        Finish(report, options, report.Error is null ? 0 : 1);
    }

    private static void Finish(StartupSteps steps, string root, StartupMeasurementOptions options, string error, int exitCode) =>
        Finish(steps.Recorder.Report("startup", root, error), options, exitCode);

    private static void Finish(LaunchMeasurementReport report, StartupMeasurementOptions options, int exitCode)
    {
        lock (FinishLock)
        {
            if (_finished) return;
            _finished = true;
            try
            {
                report.Write(options.OutputPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SessionLog.WriteUnhandled("measurement results", exception);
                exitCode = exitCode == 0 ? 4 : exitCode;
            }
            Environment.Exit(exitCode);
        }
    }

    /// <summary>Checks every 50 ms whether this process owns a visible window; hides it and reports it at once.</summary>
    private static void WatchForVisibleWindows(Action shown)
    {
        var process = (uint)Environment.ProcessId;
        new Thread(() =>
        {
            while (true)
            {
                var found = false;
                EnumWindows((handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out var owner);
                    if (owner != process || !IsWindowVisible(handle)) return true;
                    ShowWindow(handle, 0);
                    found = true;
                    return true;
                }, IntPtr.Zero);
                if (found)
                {
                    shown();
                    return;
                }
                Thread.Sleep(50);
            }
        }) { IsBackground = true, Name = "Arkive measuring window guard" }.Start();
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);
}
