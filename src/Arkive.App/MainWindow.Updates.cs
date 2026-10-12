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

    private async Task CheckApplicationUpdatesAsync(bool interactive)
    {
        if (_applicationUpdateChecking) return;
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
            }
            else if (interactive) MessageBox.Show(this, "Arkive is up to date.", "Application updates", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { if (interactive) StatusText.Text = "Application update check timed out or was cancelled."; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            if (interactive) MessageBox.Show(this, exception.Message, "Could not check application updates", MessageBoxButton.OK, MessageBoxImage.Information);
            Log("Application update check unavailable; check repository access and connectivity in Settings.");
        }
        finally { _applicationUpdateChecking = false; }
    }

    private void DismissUpdateButton_Click(object sender, RoutedEventArgs e)
    { if (!_applicationUpdateDownloading) UpdateNotice.Visibility = Visibility.Collapsed; }

    private void CancelUpdateButton_Click(object sender, RoutedEventArgs e) => _updateDownloadCancellation?.Cancel();

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableApplicationUpdate is not { } release || _applicationUpdateDownloading) return;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, ArkiveRuntimePaths.InstallationMarker)))
        {
            MessageBox.Show(this, "Automatic installation is available in the installed version. Install the release from the private repository first. Source and portable builds are never overwritten.", "Application updates");
            return;
        }
        if (_authoringBusy || _modLibraryBusy || _workshopBusy || _guidedTextureModInProgress ||
            _modSetBusy || _staticMeshWorkspaceBusy || _skeletalMeshEditableWorkspaceBusy ||
            _skeletalMeshDeploymentBusy || _exportInProgress || _baselineOperationInProgress ||
            _clientUpdateAuditBusy || _textureReplacementInProgress || _libraryIndexCancellation is not null ||
            _databaseBuildCancellation is not null || _assetLinkCancellation is not null)
        {
            StatusText.Text = "Finish the current operation before updating Arkive.";
            return;
        }
        if (MessageBox.Show(this, $"Download and install Arkive {release.Version.ToString(3)}? Arkive will close and reopen. The installer is SHA-256 verified; this preview release is not code-signed.",
            "Install application update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _applicationUpdateDownloading = true;
        InstallUpdateButton.IsEnabled = false;
        CancelUpdateButton.Visibility = Visibility.Visible;
        WorkspaceContent.IsEnabled = false;
        try
        {
            await PersistAuthoringDraftAsync();
            TrySaveSettings("application update");
            using var client = CreateUpdateClient();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationUpdateCancellation.Token);
            cancellation.CancelAfter(TimeSpan.FromMinutes(10));
            _updateDownloadCancellation = cancellation;
            var directory = Path.Combine(_projectRoot, "updates", Guid.NewGuid().ToString("N"));
            var path = await new GitHubUpdateService(client).DownloadAsync(release, directory, UpdateCredentialStore.Read(),
                new Progress<double>(value => UpdateNoticeText.Text = $"Downloading verified installer: {value:P0}"), cancellation.Token);
            var helper = Path.Combine(directory, "Arkive.Updater.exe");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Arkive.Updater.exe"), helper);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var value in new[] { path, release.Sha256, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Path.GetFullPath(AppContext.BaseDirectory), _projectRoot }) start.ArgumentList.Add(value);
            using var process = Process.Start(start) ?? throw new IOException("The update helper could not start.");
            _authoringCloseApproved = true;
            _applicationUpdateDownloading = false;
            Close();
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { UpdateNoticeText.Text = $"Update not installed: {exception.Message}"; }
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
