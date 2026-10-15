using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Arkive.Core.External;
using Arkive.Core.Updates;

namespace Arkive.App;

public partial class MainWindow
{
    private readonly CancellationTokenSource _applicationUpdateCancellation = new();
    private ArkiveRelease? _availableApplicationUpdate;
    private bool _applicationUpdateChecking;
    private bool _applicationUpdateDownloading;
    private bool? _checkForUpdatesOnStartup;
    private CancellationTokenSource? _updateDownloadCancellation;

    private static HttpClient CreateUpdateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromMinutes(10) };

    private async Task CleanUpUpdateDownloadsAsync()
    {
        try
        {
            var result = await Task.Run(() => UpdateDownloadCleanup.CleanUp(
                Path.Combine(_projectRoot, "updates"), ArkiveReleasePolicy.CurrentVersion));
            if (result.DeletedFolders + result.KeptFolders > 0)
                Log($"Old update downloads cleaned: deleted={result.DeletedFolders}, kept={result.KeptFolders}, freedBytes={result.FreedBytes}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Log($"Old update downloads could not be cleaned: {exception.Message}"); }
    }

    // Never throws: every failure is explained to the user (interactive) or the session log (startup).
    private async Task<string> CheckApplicationUpdatesAsync(bool interactive)
    {
        if (_applicationUpdateChecking) return "An update check is already running.";
        _applicationUpdateChecking = true;
        try
        {
            using var client = CreateUpdateClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_applicationUpdateCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var release = await new GitHubUpdateService(client).CheckAsync(
                ArkiveReleasePolicy.CurrentVersion, UpdateCredentialStore.Read(), timeout.Token);
            _availableApplicationUpdate = release;
            if (release is not null)
            {
                UpdateNoticeText.Text = $"Arkive {release.Version.ToString(3)} is available. Your mods, drafts and backups will be kept.";
                UpdateNotice.Visibility = Visibility.Visible;
                return $"Arkive {release.Version.ToString(3)} is available; use Install update and restart in the main window.";
            }
            if (interactive) MessageBox.Show(this, "Arkive is up to date.", "Application updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return "Arkive is up to date.";
        }
        catch (Exception exception)
        {
            if (_applicationUpdateCancellation.IsCancellationRequested) return "Update check cancelled.";
            var failure = ArkiveUpdateFailure.Describe(ArkiveUpdateStage.Check, exception);
            Log($"Application update check failed: {failure.Reason} [{exception.GetType().Name}]");
            if (interactive) MessageBox.Show(this, failure.Text, "Could not check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
            return $"Update check failed: {failure.Reason}";
        }
        finally { _applicationUpdateChecking = false; }
    }

    private void DismissUpdateButton_Click(object sender, RoutedEventArgs e)
    { if (!_applicationUpdateDownloading) UpdateNotice.Visibility = Visibility.Collapsed; }

    private void CancelUpdateButton_Click(object sender, RoutedEventArgs e) => _updateDownloadCancellation?.Cancel();

    private void ReportApplicationUpdateFailure(Exception exception, bool cancelledByUser)
    {
        var failure = ArkiveUpdateFailure.Describe(ArkiveUpdateStage.Download, exception, cancelledByUser);
        UpdateNotice.Visibility = Visibility.Visible;
        UpdateNoticeText.Text = cancelledByUser
            ? "Update download cancelled. Nothing was installed."
            : $"Update not installed: {failure.Reason}";
        Log($"Application update not installed: {failure.Reason} [{exception.GetType().Name}]");
        if (!cancelledByUser && !_applicationUpdateCancellation.IsCancellationRequested)
            MessageBox.Show(this, failure.Text, "Arkive update failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableApplicationUpdate is not { } release || _applicationUpdateDownloading) return;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, ArkiveRuntimePaths.InstallationMarker)))
        {
            MessageBox.Show(this, $"Automatic updates work only in Arkive installed with Arkive-Setup. Source and portable builds are never overwritten; download the installer from {ArkiveUpdateFailure.ReleasesPage}.", "Application updates");
            return;
        }
        if (_authoringBusy || _modLibraryBusy || _workshopBusy || _guidedTextureModInProgress ||
            _modSetBusy || _staticMeshWorkspaceBusy || _skeletalMeshEditableWorkspaceBusy ||
            _skeletalMeshDeploymentBusy || _exportInProgress || _baselineOperationInProgress ||
            _clientUpdateAuditBusy || _textureReplacementInProgress || _libraryIndexCancellation is not null ||
            _databaseBuildCancellation is not null || _assetLinkCancellation is not null)
        {
            StatusText.Text = UpdateNoticeText.Text = "Update not started: finish the current Arkive operation, then choose Install update and restart again.";
            return;
        }
        if (MessageBox.Show(this, $"Download and install Arkive {release.Version.ToString(3)}? Arkive will close and reopen. The installer is SHA-256 verified; this preview release is not code-signed.",
            "Install application update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _applicationUpdateDownloading = true;
        InstallUpdateButton.IsEnabled = false;
        CancelUpdateButton.Visibility = Visibility.Visible;
        WorkspaceContent.IsEnabled = false;
        using var userCancellation = new CancellationTokenSource();
        _updateDownloadCancellation = userCancellation;
        try
        {
            await PersistAuthoringDraftAsync();
            TrySaveSettings("application update");
            using var client = CreateUpdateClient();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationUpdateCancellation.Token, userCancellation.Token);
            cancellation.CancelAfter(TimeSpan.FromMinutes(10));
            var directory = Path.Combine(_projectRoot, "updates", Guid.NewGuid().ToString("N"));
            var path = await new GitHubUpdateService(client).DownloadAsync(release, directory, UpdateCredentialStore.Read(),
                new Progress<double>(value => UpdateNoticeText.Text = $"Downloading verified installer: {value:P0}"), cancellation.Token);
            var installedHelper = Path.Combine(AppContext.BaseDirectory, "Arkive.Updater.exe");
            if (!File.Exists(installedHelper))
                throw new ArkiveUpdateException("The update helper (Arkive.Updater.exe) is missing from the Arkive folder.",
                    $"Run the downloaded installer yourself: {path}. It restores the helper and keeps your data.");
            var helper = Path.Combine(directory, "Arkive.Updater.exe");
            File.Copy(installedHelper, helper);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var value in new[] { path, release.Sha256, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Path.GetFullPath(AppContext.BaseDirectory), _projectRoot }) start.ArgumentList.Add(value);
            using var process = Process.Start(start) ?? throw new ArkiveUpdateException("Windows did not start the update helper.",
                $"Security software may have blocked Arkive.Updater.exe. Allow it and try again, or run the downloaded installer yourself: {path}");
            _authoringCloseApproved = true;
            _applicationUpdateDownloading = false;
            Close();
        }
        catch (Exception exception)
        { ReportApplicationUpdateFailure(exception, userCancellation.IsCancellationRequested); }
        finally
        {
            _updateDownloadCancellation = null;
            _applicationUpdateDownloading = false;
            InstallUpdateButton.IsEnabled = true;
            CancelUpdateButton.Visibility = Visibility.Collapsed;
            WorkspaceContent.IsEnabled = true;
        }
    }
}
