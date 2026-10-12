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
    private void OpenExportWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_exportWorkspaceStore.RootDirectory);
            OpenFolder(_exportWorkspaceStore.RootDirectory, "export workspace");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not create the export workspace: {exception.Message}";
            Log($"Could not create the export workspace: {exception}");
        }
    }

    private void OpenSelectedExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExportWorkspaceGrid.SelectedItem is ExportWorkspaceRow row)
        {
            OpenFolder(row.Entry.OutputDirectory, "selected export session");
        }
    }

    private void InspectSelectedExportSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_umodelBridge is null ||
            ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow row)
        {
            return;
        }

        var manifest = row.Entry.Manifest;
        if (!File.Exists(manifest.SourcePackagePath))
        {
            StatusText.Text = "The source package recorded by this export is no longer available.";
            Log(
                $"Export source inspection refused because the package is unavailable: " +
                $"exportId={manifest.ExportId}, package={manifest.SourcePackagePath}.");
            return;
        }

        try
        {
            _umodelBridge.StartAssetPreview(
                manifest.SourcePackagePath,
                new UmodelAssetEntry(
                    manifest.ExportIndex,
                    0,
                    0,
                    manifest.ClassName,
                    manifest.ObjectName));
            StatusText.Text = $"Asked UModel to inspect {manifest.PackageAlias}.{manifest.ObjectName}.";
            Log(
                $"Export source asset viewer launched: exportId={manifest.ExportId}, " +
                $"package={manifest.SourcePackagePath}, class={manifest.ClassName}, " +
                $"object={manifest.ObjectName}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Could not launch the export's source asset.";
            Log($"Export source asset viewer launch failed: {exception}");
        }
    }

    private void CopySelectedExportPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExportWorkspaceGrid.SelectedItem is ExportWorkspaceRow row)
        {
            CopyText(row.Entry.OutputDirectory, "export output path");
        }
    }

    private void OpenFolder(string path, string purpose)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.GetFullPath(path),
                UseShellExecute = true,
            });
            Log($"Opened {purpose}: {Path.GetFullPath(path)}");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"Could not open {purpose}: {exception.Message}";
            Log($"Could not open {purpose}: {exception}");
        }
    }

    private void RevealFileInExplorer(string? path, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText.Text = $"The {purpose} is no longer available.";
            Log($"Could not reveal {purpose}: missing path={path ?? "none"}.");
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{fullPath}\"",
                UseShellExecute = true,
            });
            Log($"Selected {purpose} in File Explorer: {fullPath}");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"Could not reveal {purpose}: {exception.Message}";
            Log($"Could not reveal {purpose}: path={path}, error={exception}");
        }
    }

    private void CopyText(string value, string purpose)
    {
        try
        {
            Clipboard.SetText(value);
            StatusText.Text = $"Copied {purpose}.";
            Log($"Copied {purpose} to the clipboard.");
        }
        catch (System.Runtime.InteropServices.ExternalException exception)
        {
            StatusText.Text = $"Could not copy {purpose}: {exception.Message}";
            Log($"Could not copy {purpose}: {exception}");
        }
    }

}
