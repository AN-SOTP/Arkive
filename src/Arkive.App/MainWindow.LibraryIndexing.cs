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
    private async void LibraryBuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (_umodelBridge is null || string.IsNullOrEmpty(_libraryRootPath))
        {
            return;
        }

        _libraryIndexCancellation?.Dispose();
        _libraryIndexCancellation = new CancellationTokenSource();
        var workers = int.TryParse(
            (LibraryWorkersComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            out var selectedWorkers)
            ? selectedWorkers
            : PackageIndexStore.RecommendedMaxParallelism;
        LibraryBuildButton.IsEnabled = false;
        LibraryCancelButton.IsEnabled = true;
        LibraryWorkersComboBox.IsEnabled = false;
        LibraryProgressBar.Value = 0;
        StatusText.Text = "Discovering packages for the library index...";
        Log(
            $"Library index build started with {workers} workers: {_libraryRootPath}; " +
            $"fastScratch={_libraryScratchPath ?? "disabled"}");

        var progress = new Progress<PackageIndexProgress>(value =>
        {
            LibraryProgressBar.Maximum = Math.Max(1, value.TotalPackages);
            LibraryProgressBar.Value = Math.Max(LibraryProgressBar.Value, value.CompletedPackages);
            LibraryCountText.Text =
                $"{value.CompletedPackages:N0}/{value.TotalPackages:N0} packages | " +
                $"cached {value.CachedPackages:N0} | failed {value.FailedPackages:N0}";
            if (!string.IsNullOrEmpty(value.CurrentPackage))
            {
                StatusText.Text = $"Indexing with {workers} workers: {value.CurrentPackage}";
            }
        });

        try
        {
            var report = await _packageIndexStore.BuildAsync(
                _libraryRootPath,
                _umodelBridge,
                progress,
                _libraryIndexCancellation.Token,
                workers,
                _libraryScratchPath);
            StatusText.Text =
                $"Library ready: {report.IndexedAssets:N0} assets from {report.TotalPackages:N0} packages.";
            Log(
                $"Library index completed in {report.Duration.TotalMinutes:N2} minutes: " +
                $"packages={report.TotalPackages}, indexed={report.IndexedPackages}, " +
                $"cached={report.CachedPackages}, failed={report.FailedPackages}, " +
                $"assets={report.IndexedAssets}.");
            Log($"Index provenance manifest committed: {_packageIndexStore.ManifestPath}");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Library indexing cancelled. Completed package entries remain cached.";
            Log("Library index build cancelled; completed cache entries were preserved.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Library indexing failed.";
            Log($"Library index failed: {exception}");
            MessageBox.Show(
                this,
                exception.Message,
                "Arkive library index error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            LibraryBuildButton.IsEnabled = _umodelBridge is not null;
            LibraryCancelButton.IsEnabled = false;
            LibraryWorkersComboBox.IsEnabled = true;
            await LoadLibraryDocumentsAsync("index build/update completion");
            await RefreshIndexHealthAsync();
            TrySaveSettings("index build/update finished");
        }
    }

    private void LibraryCancelButton_Click(object sender, RoutedEventArgs e)
    {
        LibraryCancelButton.IsEnabled = false;
        _libraryIndexCancellation?.Cancel();
        StatusText.Text = "Stopping library indexing after the current UModel operation...";
    }

    private async void RefreshIndexStatusButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshIndexHealthAsync();

    private void OpenIndexCacheButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_packageIndexStore.CacheDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _packageIndexStore.CacheDirectory,
                UseShellExecute = true,
            });
            Log($"Opened index cache folder: {_packageIndexStore.CacheDirectory}");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"Could not open index cache: {exception.Message}";
            Log($"Could not open index cache folder: {exception}");
        }
    }

    private async Task RefreshIndexHealthAsync()
    {
        if (IndexHealthSummaryText is null)
        {
            return;
        }

        IndexHealthSummaryText.Text = "Reading index status...";
        try
        {
            var health = await _packageIndexStore.GetHealthAsync(
                _libraryRootPath,
                _umodelBridge);
            IndexHealthSummaryText.Text =
                $"{health.CachedDocumentCount:N0} cached packages | " +
                $"{health.IndexedAssetCount:N0} objects | {FormatByteSize(health.CacheBytes)} | " +
                $"{health.FailedDocumentCount:N0} failed | {health.CorruptDocumentCount:N0} unreadable";
            IndexProvenanceText.Text = health.ProvenanceStatus;
            IndexManifestText.Text = health.Manifest is null
                ? $"Manifest: not present | Cache: {health.CacheDirectory}"
                : $"Last completed build: {health.Manifest.CompletedAt.ToLocalTime():g} | " +
                  $"{health.Manifest.PackageCount:N0} packages, " +
                  $"{health.Manifest.AssetCount:N0} objects | " +
                  $"Backend SHA-256: {health.Manifest.BackendSha256[..Math.Min(16, health.Manifest.BackendSha256.Length)]}... | " +
                  $"Root: {health.Manifest.LastRootPath}";
            IndexFailuresGrid.ItemsSource = health.Failures;
            Log(
                $"Index health refreshed: documents={health.CachedDocumentCount}, " +
                $"assets={health.IndexedAssetCount}, failed={health.FailedDocumentCount}, " +
                $"corrupt={health.CorruptDocumentCount}, bytes={health.CacheBytes}, " +
                $"provenanceMatches={health.ProvenanceMatches}, " +
                $"provenanceStatus={health.ProvenanceStatus}, " +
                $"manifestPresent={health.Manifest is not null}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            IndexHealthSummaryText.Text = "Index status could not be read.";
            IndexProvenanceText.Text = exception.Message;
            Log($"Index health refresh failed: {exception}");
        }
    }

    private static string FormatByteSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):N2} GiB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):N2} MiB",
        >= 1024L => $"{bytes / 1024d:N2} KiB",
        _ => $"{bytes:N0} bytes",
    };

}
