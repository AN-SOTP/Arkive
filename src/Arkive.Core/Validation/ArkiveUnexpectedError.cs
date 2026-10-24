namespace Arkive.Core.Validation;

/// <summary>How an unexpected error left Arkive: still running, unable to start, or about to close.</summary>
public enum ArkiveUnexpectedErrorKind
{
    Recovered,
    StartupFailed,
    Closing,
}

/// <summary>The message shown for an unexpected error; <see cref="OffersReport"/> asks whether to create a problem report.</summary>
public sealed record ArkiveUnexpectedErrorNotice(ArkiveUnexpectedErrorKind Kind, string Title, string Message, bool OffersReport);

/// <summary>
/// Every sentence the unexpected-error message shows, in one place so they can be translated.
/// Each is a whole sentence; none is built from fragments of another.
/// </summary>
public static class ArkiveUnexpectedErrorText
{
    public const string Title = "Arkive ran into a problem";

    /// <summary>Longest error text shown; the full error is always in the session log.</summary>
    public const int MaximumReasonLength = 300;

    public static ArkiveUnexpectedErrorNotice Recovered(Exception exception, string? logPath) => new(
        ArkiveUnexpectedErrorKind.Recovered, Title,
        "Something went wrong and the last action may not have finished. Arkive is still open, so you can save your work and carry on." +
        $"\n\nWhat went wrong: {Reason(exception)}" +
        "\n\nCreate a problem report now? It is the same as Report a problem in the top bar. Send it with a note of what you were doing so this can be fixed." +
        LogLine(logPath),
        OffersReport: true);

    public static ArkiveUnexpectedErrorNotice StartupFailed(Exception exception, string? logPath) => new(
        ArkiveUnexpectedErrorKind.StartupFailed, Title,
        "Arkive could not start because of an unexpected error." +
        $"\n\nWhat went wrong: {Reason(exception)}" +
        "\n\nStart Arkive again. If it still does not start, send the log file below with a note of what you did before it happened." +
        LogLine(logPath),
        OffersReport: false);

    public static ArkiveUnexpectedErrorNotice Closing(Exception exception, string? logPath) => new(
        ArkiveUnexpectedErrorKind.Closing, Title,
        "Arkive has to close because of an unexpected error. Changes you saved are kept." +
        $"\n\nWhat went wrong: {Reason(exception)}" +
        "\n\nWhen you start Arkive again, use Report a problem in the top bar and send the report with a note of what you were doing. It includes the log of this session." +
        LogLine(logPath),
        OffersReport: false);

    /// <summary>The innermost useful message: an aggregate's first inner error, cut to <see cref="MaximumReasonLength"/>.</summary>
    public static string Reason(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: > 0 } aggregate) exception = aggregate.InnerExceptions[0];
        while (exception is System.Reflection.TargetInvocationException { InnerException: { } inner }) exception = inner;
        var message = string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message.Trim();
        return message.Length <= MaximumReasonLength ? message : message[..(MaximumReasonLength - 1)].TrimEnd() + "…";
    }

    private static string LogLine(string? logPath) => logPath is null ? string.Empty : $"\n\nThe details are saved in {logPath}";
}

/// <summary>
/// Decides which unexpected errors are shown. One message is open at a time, and the same error again within
/// <see cref="RepeatWindow"/> is only logged, so an error that repeats (on every redraw, say) cannot bury the user in messages.
/// </summary>
public sealed class ArkiveUnexpectedErrorGate(Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, DateTimeOffset> _lastShown = new(StringComparer.Ordinal);
    private bool _showing;

    /// <summary>Unexpected errors seen this session, shown or not.</summary>
    public int Count { get; private set; }

    public string? LastReason { get; private set; }

    /// <summary>Counts the error and returns whether to show it; a true answer must be followed by <see cref="Closed"/>.</summary>
    public bool TryShow(Exception exception)
    {
        Count++;
        LastReason = $"{exception.GetType().Name}: {ArkiveUnexpectedErrorText.Reason(exception)}";
        if (_showing) return false;
        var now = _clock();
        if (_lastShown.TryGetValue(LastReason, out var shown) && now - shown < RepeatWindow) return false;
        _lastShown[LastReason] = now;
        _showing = true;
        return true;
    }

    public void Closed() => _showing = false;
}
