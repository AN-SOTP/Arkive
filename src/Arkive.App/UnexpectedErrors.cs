using System.Windows;
using Arkive.Core.Validation;

namespace Arkive.App;

/// <summary>
/// Turns an unhandled exception into a message instead of a silent close. On the UI thread (where the async void
/// event handlers' errors land) Arkive keeps running and offers Report a problem; before the main window is up it
/// explains that Arkive could not start and closes; on another thread, where .NET always ends the process, it explains
/// why Arkive is closing. Every error is written to the session log first.
/// </summary>
internal static class UnexpectedErrors
{
    public static ArkiveUnexpectedErrorGate Gate { get; set; } = new();

    /// <summary>Shows the notice and returns true when the user asks for a problem report. Tests replace it so nothing opens on screen.</summary>
    public static Func<ArkiveUnexpectedErrorNotice, Window?, bool> Prompt { get; set; } = ShowMessage;

    /// <summary>Creates the problem report the user asked for. Tests replace it so no Explorer window opens.</summary>
    public static Func<MainWindow, Task> Report { get; set; } = window => window.ReportProblemAsync();

    public static async void OnUiThreadError(Exception exception)
    {
        var log = SessionLog.WriteUnhandled("WPF dispatcher", exception);
        var windows = Application.Current?.Windows.OfType<Window>().ToArray() ?? [];
        var main = windows.OfType<MainWindow>().FirstOrDefault(window => window.IsLoaded);
        var owner = windows.FirstOrDefault(window => window.IsActive && window.IsLoaded) ?? main;
        if (main is null || exception is OutOfMemoryException)
        {
            if (Gate.TryShow(exception)) Show(main is null
                ? ArkiveUnexpectedErrorText.StartupFailed(exception, log)
                : ArkiveUnexpectedErrorText.Closing(exception, log), owner);
            Application.Current?.Shutdown(1);
            return;
        }
        if (!Gate.TryShow(exception)) return;
        if (!Show(ArkiveUnexpectedErrorText.Recovered(exception, log), owner)) return;
        await Report(main);
    }

    public static void OnOtherThreadError(Exception exception, bool terminating)
    {
        var log = SessionLog.WriteUnhandled(terminating ? "AppDomain, terminating" : "AppDomain", exception);
        if (terminating && Gate.TryShow(exception)) Show(ArkiveUnexpectedErrorText.Closing(exception, log), owner: null);
    }

    private static bool Show(ArkiveUnexpectedErrorNotice notice, Window? owner)
    {
        try { return Prompt(notice, owner); }
        catch (Exception exception)
        {
            // The message itself failed (no desktop, say); the error is already logged, so carry on without it.
            SessionLog.WriteUnhandled("unexpected-error message", exception);
            return false;
        }
        finally { Gate.Closed(); }
    }

    private static bool ShowMessage(ArkiveUnexpectedErrorNotice notice, Window? owner)
    {
        var buttons = notice.OffersReport ? MessageBoxButton.YesNo : MessageBoxButton.OK;
        var image = notice.Kind == ArkiveUnexpectedErrorKind.Recovered ? MessageBoxImage.Warning : MessageBoxImage.Error;
        var answer = owner is null
            ? MessageBox.Show(notice.Message, notice.Title, buttons, image)
            : MessageBox.Show(owner, notice.Message, notice.Title, buttons, image);
        return notice.OffersReport && answer == MessageBoxResult.Yes;
    }
}
