using System.ComponentModel;
using System.Net;

namespace Arkive.Core.Updates;

public enum ArkiveUpdateStage { Check, Download, Install }

// A failure whose user-facing reason and next step are already known where it is raised.
public sealed class ArkiveUpdateException(string reason, string advice, Exception? innerException = null)
    : Exception(reason, innerException)
{
    public string Advice { get; } = advice;
}

// Explains an application-update failure to the user: what failed, why, what to do and what was kept.
public sealed record ArkiveUpdateFailure(string Summary, string Reason, string Advice, string Outcome)
{
    public const string ReleasesPage = $"https://github.com/{ArkiveReleasePolicy.Repository}/releases";
    public const string IssuesPage = $"https://github.com/{ArkiveReleasePolicy.Repository}/issues";

    public string Text => $"{Summary}\n\nWhy: {Reason}\n\nWhat to do: {Advice}\n\n{Outcome}";

    public static ArkiveUpdateFailure Describe(ArkiveUpdateStage stage, Exception exception, bool cancelledByUser = false)
    {
        var (reason, advice) = Explain(stage, exception, cancelledByUser);
        return stage switch
        {
            ArkiveUpdateStage.Check => new("Arkive could not check for updates.", reason, advice,
                "Nothing was downloaded or changed."),
            ArkiveUpdateStage.Download => new("The update was not installed.", reason, advice,
                "This version of Arkive is unchanged, and your mods, drafts and backups were not touched."),
            _ => new("The update could not be installed.", reason, advice,
                "Your mods, drafts and backups are stored separately and were not changed by the update."),
        };
    }

    // Inno Setup exit codes: https://jrsoftware.org/ishelp/topic_setupexitcodes.htm
    public static ArkiveUpdateException InstallerExit(int exitCode, string installerPath, string logPath)
    {
        var manual = $"You can also run the downloaded installer yourself: {installerPath}. Installer log: {logPath}";
        var (reason, advice) = exitCode switch
        {
            1 => ("Setup refused to start (exit code 1). Another Arkive window was probably still open, or Windows or security software blocked the installer.",
                  "Close every Arkive window, then choose Install update and restart again."),
            2 => ("Setup was cancelled before it changed anything (exit code 2).",
                  "Choose Install update and restart again."),
            5 => ("Setup stopped because a file in the Arkive folder could not be replaced (exit code 5). The Arkive CLI, UModel or security software was probably using it.",
                  "Close those programs, then try the update again."),
            7 => ("Setup found files in the Arkive folder still in use and could not continue (exit code 7).",
                  "Close the Arkive CLI, UModel and anything else opened from the Arkive folder, then try again."),
            8 => ("Windows must restart before Arkive can be updated (exit code 8).",
                  "Restart your PC, then install the update again."),
            3 or 4 => ($"Setup hit an error while installing files (exit code {exitCode}).",
                  "Check free disk space and the installer log, then try again."),
            _ => ($"Setup ended with unexpected exit code {exitCode}.",
                  "Check the installer log, then try again."),
        };
        return new ArkiveUpdateException(reason, $"{advice} {manual}");
    }

    private static (string Reason, string Advice) Explain(ArkiveUpdateStage stage, Exception exception, bool cancelledByUser)
    {
        var retry = $"Try again later, or download the installer from {ReleasesPage}.";
        return exception switch
        {
            ArkiveUpdateException known => (known.Message, known.Advice),
            OperationCanceledException when cancelledByUser =>
                ("You cancelled the download.", "Choose Install update and restart again when you are ready."),
            OperationCanceledException or TimeoutException when stage == ArkiveUpdateStage.Install =>
                ("A step of the update did not finish in time.", $"Start Arkive and try again. {retry}"),
            OperationCanceledException or TimeoutException =>
                ("GitHub did not respond in time. The connection may be slow, interrupted or blocked.",
                 "Check your internet connection and try again. A VPN, proxy or firewall can also slow or block GitHub."),
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
                ("GitHub rejected the saved access token (HTTP 401); it may have expired or been revoked.",
                 "Open Settings and choose Forget token. Public releases do not need a token."),
            HttpRequestException { StatusCode: { } status } when status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
                ($"GitHub refused the request (HTTP {(int)status}). Its hourly request limit for your network was probably reached, or a saved token lacks access.",
                 "Wait up to an hour and try again. If you saved a token, check it or choose Forget token in Settings."),
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } when stage == ArkiveUpdateStage.Check =>
                ("GitHub reported no published Arkive release (HTTP 404).", retry),
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                ("GitHub no longer has this installer (HTTP 404); the release may have been withdrawn or replaced.",
                 "Check for updates again to find the current release."),
            HttpRequestException { StatusCode: { } status } when (int)status >= 500 =>
                ($"GitHub is temporarily unavailable (HTTP {(int)status}).", "Try again in a few minutes."),
            HttpRequestException { StatusCode: { } status } =>
                ($"GitHub returned an unexpected response (HTTP {(int)status}).", retry),
            HttpRequestException http =>
                ($"Arkive could not connect to GitHub: {http.Message}",
                 "Check your internet connection. A proxy, firewall, VPN or security software may be blocking github.com."),
            InvalidDataException invalid when stage == ArkiveUpdateStage.Check =>
                ($"The latest release did not pass Arkive's safety checks: {invalid.Message}",
                 $"The release may still be uploading. {retry}"),
            InvalidDataException invalid when stage == ArkiveUpdateStage.Download =>
                ($"The download was stopped by Arkive's safety checks: {invalid.Message}",
                 $"The downloaded file was deleted and nothing was run. An interrupted download is the usual cause, so try again. If it keeps failing, download the installer from {ReleasesPage} and compare its SHA-256 with the published .sha256 file."),
            InvalidDataException invalid =>
                ($"The update helper stopped before running the installer: {invalid.Message}",
                 $"Nothing was run. Start Arkive and try again. {retry}"),
            UnauthorizedAccessException denied =>
                ($"Windows denied access to a file or folder: {denied.Message}",
                 "Make sure your account can write to the Arkive folders and that security software is not blocking Arkive."),
            IOException io =>
                ($"A file operation failed: {io.Message}",
                 "Check free disk space and close other programs using the Arkive folders, then try again."),
            Win32Exception win32 =>
                ($"Windows could not complete the operation: {win32.Message}",
                 "Security software may have blocked the update helper or installer. Allow Arkive, then try again."),
            _ => ($"An unexpected error occurred ({exception.GetType().Name}): {exception.Message}",
                  $"Try again. If it keeps happening, report this message at {IssuesPage}."),
        };
    }
}
