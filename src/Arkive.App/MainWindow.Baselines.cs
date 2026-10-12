using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Baselines;
using Arkive.Core.Discovery;
using Arkive.Core.External;
using Arkive.Core.Exports;
using Arkive.Core.Inspection;
using Arkive.Core.Meshes;
using Arkive.Core.Materials;
using Arkive.Core.Mods;
using Arkive.Core.Packages;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;
using Arkive.Core.Validation;
using Microsoft.Win32;

namespace Arkive.App;

public partial class MainWindow : Window
{
    private async void RefreshBaselinesButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshBaselineWorkspaceAsync("refresh button");

    private async Task RefreshBaselineWorkspaceAsync(string trigger)
    {
        if (ClientSnapshotsGrid is null || AssetBaselinesGrid is null)
        {
            return;
        }

        try
        {
            var workspace = await _baselineWorkspaceStore.LoadAsync();
            var snapshotRows = workspace.ClientSnapshots
                .Select(entry => new ClientSnapshotRow(entry))
                .ToArray();
            var assetRows = workspace.AssetBaselines
                .Select(entry => new AssetBaselineRow(entry))
                .ToArray();
            ClientSnapshotsGrid.ItemsSource = snapshotRows;
            AssetBaselinesGrid.ItemsSource = assetRows;
            BaselineWorkspaceSummaryText.Text =
                $"{snapshotRows.Length:N0} client snapshots | {assetRows.Length:N0} asset baselines | " +
                $"{FormatByteSize(workspace.ManifestBytes)} manifests | " +
                $"{workspace.UnreadableManifestCount:N0} unreadable";
            UpdateBaselineSelectionState();
            Log(
                $"Baseline workspace refreshed: trigger={trigger}, snapshots={snapshotRows.Length}, " +
                $"assets={assetRows.Length}, manifestBytes={workspace.ManifestBytes}, " +
                $"unreadable={workspace.UnreadableManifestCount}, root={workspace.RootDirectory}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            BaselineWorkspaceSummaryText.Text = "Baseline workspace could not be read.";
            Log($"Baseline workspace refresh failed: trigger={trigger}, error={exception}");
        }
    }

    private async void CreateClientSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_libraryRootPath) || _baselineOperationInProgress)
        {
            StatusText.Text = "Choose or restore the Packages folder before creating a snapshot.";
            return;
        }

        var cancellation = BeginBaselineOperation("Inventorying the current client packages...");
        if (cancellation is null)
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        Log(
            $"Client snapshot creation started: root={_libraryRootPath}, " +
            $"metadata={_gameMetadataPath ?? "none"}, fullPackageHashing=false.");
        try
        {
            var manifest = await _packageIndexStore.GetManifestAsync(cancellation.Token);
            var entry = await _baselineWorkspaceStore.CreateClientSnapshotAsync(
                _libraryRootPath,
                manifest,
                _gameMetadataPath,
                cancellationToken: cancellation.Token);
            watch.Stop();
            await RefreshBaselineWorkspaceAsync("client snapshot created");
            ClientSnapshotsGrid.SelectedItem =
                (ClientSnapshotsGrid.ItemsSource as IEnumerable<ClientSnapshotRow>)?
                .FirstOrDefault(row => row.Entry.ManifestPath.Equals(
                    entry.ManifestPath,
                    StringComparison.OrdinalIgnoreCase));
            BaselineOperationText.Text =
                $"Created snapshot with {entry.Snapshot.Packages.Count:N0} packages " +
                $"({FormatByteSize(entry.Snapshot.PackageBytes)}) in {watch.Elapsed.TotalSeconds:N1}s.";
            StatusText.Text = "Client snapshot created without hashing every package.";
            Log(
                $"Client snapshot creation completed in {watch.Elapsed.TotalSeconds:N3}s: " +
                $"id={entry.Snapshot.SnapshotId}, packages={entry.Snapshot.Packages.Count}, " +
                $"bytes={entry.Snapshot.PackageBytes}, indexAssets={entry.Snapshot.IndexManifest?.AssetCount ?? 0}, " +
                $"backendSha={entry.Snapshot.IndexManifest?.BackendSha256 ?? "none"}, " +
                $"metadataSha={entry.Snapshot.GameMetadataSha256 ?? "none"}, manifest={entry.ManifestPath}.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            BaselineOperationText.Text = $"Client snapshot cancelled after {watch.Elapsed.TotalSeconds:N1}s.";
            Log($"Client snapshot creation cancelled after {watch.Elapsed.TotalSeconds:N3}s.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            BaselineOperationText.Text = "Client snapshot failed; see Diagnostics.";
            StatusText.Text = "Could not create the client snapshot.";
            Log($"Client snapshot creation failed: {exception}");
        }
        finally
        {
            EndBaselineOperation(cancellation);
        }
    }

    private async void CompareClientSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (ClientSnapshotsGrid.SelectedItem is not ClientSnapshotRow row ||
            _baselineOperationInProgress)
        {
            return;
        }

        var cancellation = BeginBaselineOperation(
            $"Comparing {row.Entry.Snapshot.Label} to the current installation...");
        if (cancellation is null)
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        Log(
            $"Client snapshot comparison started: id={row.Entry.Snapshot.SnapshotId}, " +
            $"root={row.Entry.Snapshot.LibraryRoot}, packages={row.Entry.Snapshot.Packages.Count}.");
        try
        {
            var result = await _baselineWorkspaceStore.CompareToCurrentAsync(
                row.Entry.Snapshot,
                cancellation.Token);
            ClientSnapshotChangesGrid.ItemsSource = result.Changes
                .Select(change => new ClientPackageChangeRow(change))
                .ToArray();
            BaselineOperationText.Text =
                $"{result.UnchangedPackages:N0} unchanged | {result.AddedPackages:N0} added | " +
                $"{result.ChangedPackages:N0} changed | {result.MissingPackages:N0} missing | " +
                $"{result.Duration.TotalSeconds:N1}s";
            StatusText.Text = result.Changes.Count == 0
                ? "The current package inventory matches the selected snapshot."
                : $"Snapshot comparison found {result.Changes.Count:N0} package change(s).";
            Log(
                $"Client snapshot comparison completed in {watch.Elapsed.TotalSeconds:N3}s: " +
                $"id={result.Snapshot.SnapshotId}, current={result.CurrentPackageCount}, " +
                $"unchanged={result.UnchangedPackages}, added={result.AddedPackages}, " +
                $"changed={result.ChangedPackages}, missing={result.MissingPackages}.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            BaselineOperationText.Text = $"Snapshot comparison cancelled after {watch.Elapsed.TotalSeconds:N1}s.";
            Log($"Client snapshot comparison cancelled after {watch.Elapsed.TotalSeconds:N3}s.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            BaselineOperationText.Text = "Snapshot comparison failed; see Diagnostics.";
            Log($"Client snapshot comparison failed: {exception}");
        }
        finally
        {
            EndBaselineOperation(cancellation);
        }
    }

    private void CancelBaselineOperationButton_Click(object sender, RoutedEventArgs e) =>
        _baselineCancellation?.Cancel();

    private void ClientSnapshotsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateBaselineSelectionState();

    private void AssetBaselinesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateBaselineSelectionState();

    private void UpdateBaselineSelectionState()
    {
        if (ClientSnapshotsGrid is null || AssetBaselinesGrid is null)
        {
            return;
        }

        CompareClientSnapshotButton.IsEnabled =
            ClientSnapshotsGrid.SelectedItem is ClientSnapshotRow && !_baselineOperationInProgress;
        var assetSelected = AssetBaselinesGrid.SelectedItem is AssetBaselineRow;
        OpenSelectedAssetBaselineButton.IsEnabled = assetSelected;
        CopySelectedAssetBaselineButton.IsEnabled = assetSelected;
        CreateClientSnapshotButton.IsEnabled =
            !string.IsNullOrEmpty(_libraryRootPath) && !_baselineOperationInProgress;
    }

    private void OpenBaselineWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_baselineWorkspaceStore.RootDirectory);
            OpenFolder(_baselineWorkspaceStore.RootDirectory, "baseline workspace");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not create or open the baseline workspace.";
            Log($"Could not open baseline workspace: {exception}");
        }
    }

    private void OpenSelectedAssetBaselineButton_Click(object sender, RoutedEventArgs e)
    {
        if (AssetBaselinesGrid.SelectedItem is AssetBaselineRow row)
        {
            OpenFolder(Path.GetDirectoryName(row.Entry.ManifestPath)!, "asset baseline manifest folder");
        }
    }

    private void CopySelectedAssetBaselineButton_Click(object sender, RoutedEventArgs e)
    {
        if (AssetBaselinesGrid.SelectedItem is not AssetBaselineRow row)
        {
            return;
        }

        var baseline = row.Entry.Baseline;
        CopyText(
            $"Package: {baseline.PackageAlias}{Environment.NewLine}" +
            $"Physical package: {baseline.PhysicalPackageName}{Environment.NewLine}" +
            $"Object: {baseline.ObjectName}{Environment.NewLine}" +
            $"Class: {baseline.ClassName}{Environment.NewLine}" +
            $"Export index: {baseline.ExportIndex}{Environment.NewLine}" +
            $"Logical range: 0x{baseline.SerialOffset:X}+0x{baseline.SerialSize:X}{Environment.NewLine}" +
            $"Package SHA-256: {baseline.PackageSha256}{Environment.NewLine}" +
            $"Payload SHA-256: {baseline.LogicalPayloadSha256 ?? "unavailable"}{Environment.NewLine}" +
            $"Payload status: {baseline.LogicalPayloadHashStatus}{Environment.NewLine}" +
            $"Manifest: {row.Entry.ManifestPath}",
            "asset baseline hashes and identity");
    }

    private async void BaselineAssetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPackagePath is null || AssetsGrid.SelectedItem is not AssetRow row)
        {
            return;
        }

        var objectName = row.ExternalAsset?.ObjectName ?? row.ObjectPath.Split('.').Last();
        await CreateAssetBaselineAsync(
            new AssetBaselineRequest(
                _currentPackagePath,
                LostArkPackageName.GetDisplayName(_currentPackagePath),
                row.ExportIndex,
                row.Kind,
                row.ClassName,
                objectName,
                row.SerialOffset,
                row.SerialSize,
                row.GameMetadata.Select(item => item.DisplayName).ToArray()),
            "open-package Assets result");
    }

    private async void LibraryBaselineButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is not LibraryAssetRow row)
        {
            return;
        }

        await CreateAssetBaselineAsync(CreateBaselineRequest(row), "Library result");
    }

    private async void RelationshipBaselineButton_Click(object sender, RoutedEventArgs e)
    {
        if (RelationshipGrid.SelectedItem is not RelationshipAssetRow row)
        {
            return;
        }

        await CreateAssetBaselineAsync(CreateBaselineRequest(row), $"Relationship result ({row.Evidence})");
    }

    private async Task<AssetBaselineEntry?> CreateAssetBaselineAsync(
        AssetBaselineRequest request,
        string trigger,
        bool navigateToWorkspace = true)
    {
        if (_baselineOperationInProgress)
        {
            return null;
        }

        var cancellation = BeginBaselineOperation(
            $"Hashing {Path.GetFileName(request.PackagePath)} and baselining {request.ObjectName}...");
        if (cancellation is null)
        {
            return null;
        }

        var watch = Stopwatch.StartNew();
        Log(
            $"Asset baseline creation started: trigger={trigger}, package={request.PackagePath}, " +
            $"index={request.ExportIndex}, class={request.ClassName}, object={request.ObjectName}, " +
            $"logicalOffset=0x{request.SerialOffset:X}, logicalSize={request.SerialSize}, " +
            $"protectionProvider={_protectionProvider.Name}.");
        try
        {
            var entry = await _baselineWorkspaceStore.CreateAssetBaselineAsync(
                request,
                _protectionProvider,
                cancellation.Token);
            await RefreshBaselineWorkspaceAsync("asset baseline created");
            AssetBaselinesGrid.SelectedItem =
                (AssetBaselinesGrid.ItemsSource as IEnumerable<AssetBaselineRow>)?
                .FirstOrDefault(row => row.Entry.ManifestPath.Equals(
                    entry.ManifestPath,
                    StringComparison.OrdinalIgnoreCase));
            BaselineOperationText.Text =
                $"Baselined {request.PackageAlias}.{request.ObjectName} in " +
                $"{watch.Elapsed.TotalSeconds:N1}s. {entry.Baseline.LogicalPayloadHashStatus}";
            StatusText.Text = entry.Baseline.LogicalPayloadSha256 is null
                ? "Package hash and storage ranges saved; logical payload hash was unavailable."
                : "Package and logical asset payload hashes saved.";
            if (navigateToWorkspace)
            {
                BaselinesTab.IsSelected = true;
            }

            Log(
                $"Asset baseline creation completed in {watch.Elapsed.TotalSeconds:N3}s: " +
                $"trigger={trigger}, baselineId={entry.Baseline.BaselineId}, " +
                $"packageSha={entry.Baseline.PackageSha256}, " +
                $"payloadSha={entry.Baseline.LogicalPayloadSha256 ?? "unavailable"}, " +
                $"payloadStatus={entry.Baseline.LogicalPayloadHashStatus}, " +
                $"storageRanges={entry.Baseline.StorageRanges.Count}, manifest={entry.ManifestPath}.");
            return entry;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            BaselineOperationText.Text = $"Asset baseline cancelled after {watch.Elapsed.TotalSeconds:N1}s.";
            Log(
                $"Asset baseline creation cancelled after {watch.Elapsed.TotalSeconds:N3}s: " +
                $"trigger={trigger}, package={request.PackagePath}, object={request.ObjectName}.");
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            BaselineOperationText.Text = "Asset baseline failed; see Diagnostics.";
            StatusText.Text = "Could not create the asset baseline.";
            Log(
                $"Asset baseline creation failed: trigger={trigger}, package={request.PackagePath}, " +
                $"object={request.ObjectName}, error={exception}");
            return null;
        }
        finally
        {
            EndBaselineOperation(cancellation);
        }
    }

    private CancellationTokenSource? BeginBaselineOperation(string status)
    {
        if (_baselineOperationInProgress)
        {
            return null;
        }

        _baselineOperationInProgress = true;
        var cancellation = new CancellationTokenSource();
        _baselineCancellation = cancellation;
        BaselineOperationText.Text = status;
        CancelBaselineOperationButton.IsEnabled = true;
        UpdateBaselineOperationActionState();
        return cancellation;
    }

    private void EndBaselineOperation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_baselineCancellation, cancellation))
        {
            _baselineCancellation = null;
            _baselineOperationInProgress = false;
        }

        cancellation.Dispose();
        CancelBaselineOperationButton.IsEnabled = false;
        UpdateBaselineOperationActionState();
    }

    private void UpdateBaselineOperationActionState()
    {
        UpdateLibraryActionState();
        UpdateRelationshipActionState();
        var hasOpenAsset = _currentPackagePath is not null && AssetsGrid.SelectedItem is AssetRow;
        BaselineAssetButton.IsEnabled = hasOpenAsset && !_baselineOperationInProgress;
        UpdateBaselineSelectionState();
    }

    private static AssetBaselineRequest CreateBaselineRequest(LibraryAssetRow row) => new(
        row.SourcePath,
        row.PackageAlias,
        row.ExportIndex,
        row.Kind,
        row.ClassName,
        row.ObjectName,
        row.SerialOffset,
        row.SerialSize,
        row.GameMetadata.Select(item => item.DisplayName).ToArray());

    private static AssetBaselineRequest CreateBaselineRequest(RelationshipAssetRow row) => new(
        row.SourcePath,
        row.PackageAlias,
        row.ExportIndex,
        row.Kind,
        row.ClassName,
        row.ObjectName,
        row.Match.Asset.SerialOffset,
        row.Match.Asset.SerialSize,
        row.Match.Asset.GameMetadata.Select(item => item.DisplayName).ToArray());

}
