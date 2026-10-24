using System.IO;
using System.Reflection;
using System.Windows;
using Arkive.App;
using Arkive.Core.Validation;

/// <summary>
/// An error escaping an event handler shows a message and Arkive keeps running, with Report a problem offered.
/// The messages are recorded instead of shown, so nothing opens on screen, and any error the run did not cause on
/// purpose fails it.
/// </summary>
internal static class UnexpectedErrorChecks
{
    private static readonly Type Errors = typeof(App).Assembly.GetType("Arkive.App.UnexpectedErrors")!;
    private static readonly List<string> Unplanned = [];

    public static void Install()
    {
        Set("Prompt", new Func<ArkiveUnexpectedErrorNotice, Window?, bool>((notice, _) =>
        {
            Unplanned.Add(notice.Message);
            Console.Error.WriteLine($"Unexpected error during the UI tests: {notice.Message}");
            return false;
        }));
        Set("Report", new Func<MainWindow, Task>(_ => Task.CompletedTask));
    }

    public static void ThrowIfAny()
    {
        if (Unplanned.Count > 0) throw new InvalidOperationException($"{Unplanned.Count} unexpected errors reached the error message: {Unplanned[0]}");
    }

    public static async Task VerifyAsync(MainWindow window)
    {
        var prompt = Get("Prompt");
        var report = Get("Report");
        var gate = new ArkiveUnexpectedErrorGate();
        var notices = new List<ArkiveUnexpectedErrorNotice>();
        var reports = new List<MainWindow>();
        var wantReport = false;
        Set("Gate", gate);
        Set("Prompt", new Func<ArkiveUnexpectedErrorNotice, Window?, bool>((notice, _) => { notices.Add(notice); return wantReport; }));
        Set("Report", new Func<MainWindow, Task>(target => { reports.Add(target); return Task.CompletedTask; }));
        try
        {
            FailLikeAnEventHandler("UI test error in a button handler");
            await Program.UntilAsync(() => notices.Count == 1, () => "An error escaping an async event handler showed no message.");
            var notice = notices[0];
            var log = notice.Message[(notice.Message.LastIndexOf("saved in ", StringComparison.Ordinal) + "saved in ".Length)..];
            Program.Check(notice is { Kind: ArkiveUnexpectedErrorKind.Recovered, OffersReport: true } &&
                          notice.Message.Contains("UI test error in a button handler", StringComparison.Ordinal) &&
                          notice.Message.Contains("Report a problem", StringComparison.Ordinal),
                "The unexpected-error message does not say what went wrong or offer Report a problem: " + notice.Message);
            Program.Check(File.Exists(log) && File.ReadAllText(log).Contains("UI test error in a button handler", StringComparison.Ordinal),
                $"The unexpected error is not in the session log the message names ({log}).");
            await Task.Delay(100);
            Program.Check(window.IsLoaded && window.IsVisible && Application.Current.Windows.OfType<MainWindow>().Contains(window) && reports.Count == 0,
                "Arkive closed after an error it can survive, or made a problem report nobody asked for.");
            Console.WriteLine("PASS: an error escaping an event handler shows a message offering Report a problem, is logged, and Arkive keeps running");

            FailLikeAnEventHandler("UI test error in a button handler");
            await Program.UntilAsync(() => gate.Count == 2, () => "The repeated error was not handled.");
            Program.Check(notices.Count == 1, "The same error showed its message again straight away.");
            wantReport = true;
            FailLikeAnEventHandler("UI test error the user reports");
            await Program.UntilAsync(() => reports.Count == 1, () => "Choosing to report the error did not create a problem report.");
            Program.Check(notices.Count == 2 && reports[0] == window, "The problem report was made for another window.");
            var content = await (Task<ArkiveProblemReportContent>)typeof(MainWindow)
                .GetMethod("ProblemReportContentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [])!;
            Program.Check(content.Summary.Contains("Unexpected errors this session: 3 (last: InvalidOperationException: UI test error the user reports)", StringComparison.Ordinal),
                "The problem report summary does not count this session's unexpected errors.");
            Console.WriteLine("PASS: a repeated error is only logged, and answering Yes creates a problem report that counts the errors");
        }
        finally
        {
            Set("Gate", new ArkiveUnexpectedErrorGate());
            Set("Prompt", prompt);
            Set("Report", report);
        }
    }

    private static async void FailLikeAnEventHandler(string message)
    {
        await Task.Yield();
        throw new InvalidOperationException(message);
    }

    private static object? Get(string name) => Errors.GetProperty(name, BindingFlags.Static | BindingFlags.Public)!.GetValue(null);

    private static void Set(string name, object? value) => Errors.GetProperty(name, BindingFlags.Static | BindingFlags.Public)!.SetValue(null, value);
}
