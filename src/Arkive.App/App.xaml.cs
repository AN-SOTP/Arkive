using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Arkive.App;

public partial class App : Application
{
    private Mutex? _installationMutex;
    internal IReadOnlyList<string> StartupModFiles { get; private set; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        _installationMutex = new Mutex(false, "Arkive.Application.Update");
        if (StartupUri is not null) StartupModFiles = ModFileArguments(e.Args);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _installationMutex?.Dispose();
        base.OnExit(e);
    }

    internal static IReadOnlyList<string> ModFileArguments(IEnumerable<string> arguments)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            try
            {
                if (Path.GetExtension(argument).Equals(".arkive", StringComparison.OrdinalIgnoreCase))
                    files.Add(Path.GetFullPath(argument));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            { SessionLog.WriteUnhandled("invalid mod-file argument", exception); }
        }
        return files.ToArray();
    }

    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    private static void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e) =>
        SessionLog.WriteUnhandled("WPF dispatcher", e.Exception);

    private static void CurrentDomain_UnhandledException(
        object? sender,
        UnhandledExceptionEventArgs e) =>
        SessionLog.WriteUnhandled(
            e.IsTerminating ? "AppDomain, terminating" : "AppDomain",
            e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

    private static void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e) =>
        SessionLog.WriteUnhandled("unobserved task", e.Exception);
}
