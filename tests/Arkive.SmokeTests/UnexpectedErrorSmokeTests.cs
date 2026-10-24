namespace Arkive.SmokeTests;

internal static class UnexpectedErrorSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("explains an unexpected error with what went wrong, Report a problem and the log file", TestNotices),
        new("shows one unexpected-error message at a time and the same error once a minute", TestGate),
    ];

    private static void TestNotices()
    {
        var log = @"C:\Users\someone\AppData\Local\Arkive\logs\arkive-20261025-120000.log";
        var recovered = ArkiveUnexpectedErrorText.Recovered(new InvalidOperationException("The mod list is empty."), log);
        Assert(recovered is { Kind: ArkiveUnexpectedErrorKind.Recovered, OffersReport: true, Title: ArkiveUnexpectedErrorText.Title } &&
               recovered.Message.Contains("Arkive is still open", StringComparison.Ordinal) &&
               recovered.Message.Contains("The mod list is empty.", StringComparison.Ordinal) &&
               recovered.Message.Contains("Report a problem", StringComparison.Ordinal) &&
               recovered.Message.EndsWith(log, StringComparison.Ordinal),
            "the message for an error Arkive survived does not say what went wrong, offer Report a problem and name the log: " + recovered.Message);

        var startup = ArkiveUnexpectedErrorText.StartupFailed(new IOException("Settings are locked."), null);
        var closing = ArkiveUnexpectedErrorText.Closing(new IOException("Disk full."), log);
        Assert(startup is { Kind: ArkiveUnexpectedErrorKind.StartupFailed, OffersReport: false } &&
               startup.Message.Contains("could not start", StringComparison.Ordinal) && !startup.Message.Contains("saved in", StringComparison.Ordinal) &&
               closing is { Kind: ArkiveUnexpectedErrorKind.Closing, OffersReport: false } &&
               closing.Message.Contains("has to close", StringComparison.Ordinal) && closing.Message.Contains("Report a problem", StringComparison.Ordinal),
            "the startup or closing message offers a report Arkive cannot make, or does not say what happens next");

        Assert(ArkiveUnexpectedErrorText.Reason(new AggregateException(new System.Reflection.TargetInvocationException(new FileNotFoundException("mods.json is missing."))))
               == "mods.json is missing.", "an error wrapped by a task or a reflection call shows the wrapper instead of the cause");
        var reason = ArkiveUnexpectedErrorText.Reason(new Exception(new string('x', 2000)));
        Assert(reason.Length == ArkiveUnexpectedErrorText.MaximumReasonLength && reason.EndsWith('…'), "a very long error is not cut short");
        Assert(ArkiveUnexpectedErrorText.Reason(new NullReferenceException(" ")) == nameof(NullReferenceException), "an error without a message shows nothing");
    }

    private static void TestGate()
    {
        var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var gate = new ArkiveUnexpectedErrorGate(() => now);
        var first = new InvalidOperationException("first");
        Assert(gate.TryShow(first), "the first unexpected error was not shown");
        Assert(!gate.TryShow(new IOException("while the message is open")), "a second message opened over the first");
        gate.Closed();
        Assert(!gate.TryShow(new InvalidOperationException("first")), "the same error was shown again at once");
        Assert(gate.TryShow(new InvalidOperationException("second")), "a different error was not shown");
        gate.Closed();
        now += ArkiveUnexpectedErrorGate.RepeatWindow;
        Assert(gate.TryShow(first), "the same error a minute later was not shown");
        gate.Closed();
        Assert(gate.Count == 5 && gate.LastReason == "InvalidOperationException: first",
            $"the problem report would count {gate.Count} errors with last '{gate.LastReason}'");
    }
}
