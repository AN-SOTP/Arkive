using System.IO;
using System.Reflection;
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
        // Arkive's own launch opens the main window here (StartupUri would always show it); a host that builds the App
        // itself, such as the UI smoke tests, opens its own windows.
        var arkiveLaunch = Assembly.GetEntryAssembly() == typeof(App).Assembly;
        StartupMeasurementOptions? measuring = null;
        try
        {
            if (arkiveLaunch) measuring = StartupMeasurementOptions.Parse(e.Args);
        }
        catch (ArgumentException)
        {
            // A measuring run with incomplete options; it must not open any window, an error message included.
            Environment.Exit(2);
        }
        if (arkiveLaunch && measuring is null) StartupModFiles = ModFileArguments(e.Args);
        base.OnStartup(e);
        if (measuring is not null) StartupMeasurement.Start(this, measuring);
        else if (arkiveLaunch) new MainWindow().Show();
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

    // Errors on the UI thread (async void event handlers included) are shown and Arkive keeps running; an error on
    // another thread always ends the process, so it is logged and explained before Windows closes Arkive.
    private static void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        UnexpectedErrors.OnUiThreadError(e.Exception);
    }

    private static void CurrentDomain_UnhandledException(
        object? sender,
        UnhandledExceptionEventArgs e) =>
        UnexpectedErrors.OnOtherThreadError(
            e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()), e.IsTerminating);

    private static void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        SessionLog.WriteUnhandled("unobserved task", e.Exception);
        e.SetObserved();
    }
}
