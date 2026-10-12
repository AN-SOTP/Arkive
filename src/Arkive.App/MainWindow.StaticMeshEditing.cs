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
    private async Task RunStaticMeshReconnaissanceAsync(
        string packagePath,
        string packageAlias,
        int exportIndex,
        AssetKind kind,
        string className,
        string objectName,
        long serialOffset,
        long serialSize,
        string trigger)
    {
        if (_staticMeshWorkspaceBusy) return;
        _staticMeshWorkspaceBusy = true;
        UpdateStaticMeshActionState();
        StatusText.Text = $"Reading native StaticMesh evidence for {packageAlias}.{objectName}...";
        Log($"StaticMesh reconnaissance started: trigger={trigger}, package={packagePath}, " +
            $"alias={packageAlias}, index={exportIndex}, class={className}, object={objectName}, " +
            $"logical=0x{serialOffset:X}+0x{serialSize:X}, provider={_protectionProvider.Name}.");
        try
        {
            var packageSha256 = await _baselineWorkspaceStore.GetPackageSha256Async(packagePath);
            var report = await _staticMeshReconnaissanceService.InspectAsync(
                new StaticMeshReconnaissanceRequest(
                    packagePath, packageAlias, exportIndex, kind, className, objectName,
                    serialOffset, serialSize, packageSha256));
            var entry = await _staticMeshReconnaissanceWorkspaceStore.SaveAsync(report);
            _currentStaticMeshReconnaissance = entry;
            DisplayStaticMeshReconnaissance(entry);
            Log($"StaticMesh reconnaissance completed in {report.Duration.TotalSeconds:N3}s: " +
                $"readiness={report.Readiness}, nativeIndex={report.NativeExportIndex}, " +
                $"nativeStart={FormatOptionalHex(report.NativeDataRelativeOffset)}, " +
                $"LODs={report.NativeLayout?.Lods.Count ?? 0}, " +
                $"vertices={report.NativeLayout?.Lods.Sum(lod => lod.VertexCount) ?? 0}, " +
                $"indices={report.NativeLayout?.Lods.Sum(lod => lod.Indices.ElementCount) ?? 0}, " +
                $"collisionNodes={report.NativeLayout?.CollisionNodeCount ?? 0}, " +
                $"collisionTriangles={report.NativeLayout?.CollisionTriangleCount ?? 0}, " +
                $"exportSha={report.ExportPayloadSha256}, json={entry.JsonPath}.");
            foreach (var issue in report.Issues)
                Log($"StaticMesh recon {issue.Severity} {issue.Code}: {issue.Message}");
            StatusText.Text = report.Readiness == StaticMeshReadiness.Editable
                ? $"StaticMesh report saved for {objectName}; editable GLB export is available."
                : $"StaticMesh report saved for {objectName}, but native editing was refused.";
        }
        catch (Exception exception) when (exception is PackageAccessException or PackageFormatException or
                                             UmodelBridgeException or IOException or UnauthorizedAccessException or
                                             InvalidDataException or NotSupportedException)
        {
            StatusText.Text = "Native StaticMesh reconnaissance failed; see Diagnostics.";
            Log($"StaticMesh reconnaissance failed: trigger={trigger}, package={packagePath}, object={objectName}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive StaticMesh reconnaissance error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _staticMeshWorkspaceBusy = false;
            UpdateStaticMeshActionState();
            UpdateLibraryActionState();
        }
    }

    private void DisplayStaticMeshReconnaissance(StaticMeshReconnaissanceEntry entry)
    {
        var report = entry.Report;
        var native = report.NativeLayout;
        StaticMeshSummaryText.Text = native is null
            ? $"{report.PackageAlias}.{report.ObjectName} | {report.Readiness} | native layout unresolved"
            : $"{report.PackageAlias}.{report.ObjectName} | {report.Readiness} | {native.Lods.Count} LOD(s) | " +
              $"{native.Lods.Sum(lod => lod.VertexCount):N0} vertices | " +
              $"collision {native.CollisionNodeCount:N0} nodes / {native.CollisionTriangleCount:N0} triangles retained";
        StaticMeshReconPathText.Text = entry.JsonPath;
        StaticMeshLodsGrid.ItemsSource = native?.Lods.Select(lod => new StaticMeshLodRow(lod)).ToArray() ?? [];
        StaticMeshSectionsGrid.ItemsSource = native?.Lods.SelectMany(lod => lod.Sections.Select(section =>
            new StaticMeshSectionRow(lod.LodIndex, section))).ToArray() ?? [];
        StaticMeshIssuesGrid.ItemsSource = report.Issues;
        StaticMeshTab.IsSelected = true;
        UpdateStaticMeshActionState();
    }

    private async void ExportEditableStaticMeshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_staticMeshWorkspaceBusy || _currentStaticMeshReconnaissance is not { } entry) return;
        _staticMeshWorkspaceBusy = true;
        UpdateStaticMeshActionState();
        StatusText.Text = $"Exporting editable StaticMesh GLB for {entry.Report.ObjectName}...";
        try
        {
            var output = Path.Combine(entry.DirectoryPath, "editable-glb",
                $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
            var result = await _staticMeshEditableExportService.ExportAsync(
                new StaticMeshEditableExportRequest(entry.Report, entry.JsonPath, output));
            Log($"Editable StaticMesh export completed: object={result.Manifest.ObjectName}, " +
                $"LODs={result.Manifest.Lods.Count}, bytes={result.Manifest.GlbBytes:N0}, " +
                $"sha={result.Manifest.GlbSha256}, glb={result.GlbPath}, manifest={result.ManifestPath}.");
            await RefreshStaticMeshWorkspaceAsync("editable export completed", result.Manifest.ExportId);
            StatusText.Text = $"Editable StaticMesh GLB created for {entry.Report.ObjectName}.";
            MessageBox.Show(this,
                $"Editable StaticMesh GLB created and structurally reopened.\n\n{result.GlbPath}\n\n" +
                "Edit render geometry in Blender. Do not apply object transforms or rename material slots.",
                "Arkive editable StaticMesh export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             InvalidDataException or NotSupportedException)
        {
            Log($"Editable StaticMesh export failed: recon={entry.JsonPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Export editable StaticMesh",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _staticMeshWorkspaceBusy = false;
            UpdateStaticMeshActionState();
        }
    }

    private async Task RefreshStaticMeshWorkspaceAsync(string trigger, string? selectId = null)
    {
        try
        {
            var exports = new List<StaticMeshEditableExportRow>();
            if (Directory.Exists(_staticMeshReconnaissanceWorkspaceStore.RootDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(
                             _staticMeshReconnaissanceWorkspaceStore.RootDirectory,
                             StaticMeshEditableExportService.ManifestFileName, SearchOption.AllDirectories))
                {
                    try
                    {
                        var manifest = await StaticMeshEditableExportService.LoadManifestAsync(path);
                        var glb = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, manifest.GlbRelativePath));
                        exports.Add(new StaticMeshEditableExportRow(path, glb, manifest));
                    }
                    catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
                    {
                        Log($"StaticMesh editable manifest ignored: path={path}, error={exception.Message}");
                    }
                }
            }
            _staticMeshEditableExports = exports.OrderByDescending(row => row.Manifest.CreatedAt).ToArray();
            _staticMeshEdits = (await _staticMeshEditWorkspaceStore.LoadAsync())
                .Select(entry => new StaticMeshEditRow(entry)).ToArray();
            StaticMeshEditableExportsGrid.ItemsSource = _staticMeshEditableExports;
            StaticMeshEditsGrid.ItemsSource = _staticMeshEdits;
            var selectedExport = !string.IsNullOrWhiteSpace(selectId)
                ? _staticMeshEditableExports.FirstOrDefault(row => row.Manifest.ExportId.Equals(
                    selectId, StringComparison.OrdinalIgnoreCase))
                : StaticMeshEditableExportsGrid.SelectedItem as StaticMeshEditableExportRow ??
                  _staticMeshEditableExports.FirstOrDefault();
            StaticMeshEditableExportsGrid.SelectedItem = selectedExport;
            if (selectedExport is not null) StaticMeshEditableExportsGrid.ScrollIntoView(selectedExport);
            var selectedEdit = !string.IsNullOrWhiteSpace(selectId)
                ? _staticMeshEdits.FirstOrDefault(row => row.Entry.Manifest.EditId.Equals(
                    selectId, StringComparison.OrdinalIgnoreCase))
                : StaticMeshEditsGrid.SelectedItem as StaticMeshEditRow ?? _staticMeshEdits.FirstOrDefault();
            StaticMeshEditsGrid.SelectedItem = selectedEdit;
            if (selectedEdit is not null) StaticMeshEditsGrid.ScrollIntoView(selectedEdit);
            StaticMeshWorkspaceSummaryText.Text =
                $"{_staticMeshEditableExports.Count:N0} editable export(s) | {_staticMeshEdits.Count:N0} registered edit(s)";
            Log($"StaticMesh workspace refreshed: trigger={trigger}, exports={exports.Count}, edits={_staticMeshEdits.Count}.");
            UpdateStaticMeshActionState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"StaticMesh workspace refresh failed: trigger={trigger}, error={exception}");
        }
    }

    private async void RefreshStaticMeshWorkspaceButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshStaticMeshWorkspaceAsync("refresh button");

    private void OpenStaticMeshReconFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStaticMeshReconnaissance is not null)
            OpenFolder(_currentStaticMeshReconnaissance.DirectoryPath, "StaticMesh reconnaissance report");
    }

    private void OpenSelectedStaticMeshExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (StaticMeshEditableExportsGrid.SelectedItem is StaticMeshEditableExportRow row)
            OpenFolder(Path.GetDirectoryName(row.ManifestPath)!, "editable StaticMesh export");
    }

    private async void RegisterEditedStaticMeshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_staticMeshWorkspaceBusy ||
            StaticMeshEditableExportsGrid.SelectedItem is not StaticMeshEditableExportRow source) return;
        var dialog = new OpenFileDialog
        {
            Title = $"Select edited GLB for {source.Manifest.ObjectName}",
            Filter = "glTF binary (*.glb)|*.glb|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        _staticMeshWorkspaceBusy = true;
        UpdateStaticMeshActionState();
        try
        {
            var entry = await _staticMeshEditWorkspaceStore.RegisterAsync(
                source.ManifestPath, dialog.FileName,
                $"{source.Manifest.ObjectName} StaticMesh edit");
            Log($"StaticMesh edit registered: editId={entry.Manifest.EditId}, object={source.Manifest.ObjectName}, " +
                $"readiness={entry.Manifest.Preflight.Readiness}, topologyChanged={entry.Manifest.Preflight.TopologyChanged}, " +
                $"collisionRetained={entry.Manifest.Preflight.CollisionWillRemainFromSource}, glb={entry.Manifest.EditedGlbPath}.");
            await RefreshStaticMeshWorkspaceAsync("edited GLB registered", entry.Manifest.EditId);
            await RefreshGuidedTextureModsAsync("StaticMesh edit registered", entry.Manifest.EditId);
            MessageBox.Show(this,
                $"Edited StaticMesh registered.\n\nReadiness: {entry.Manifest.Preflight.Readiness}\n" +
                $"Collision retained from source: {entry.Manifest.Preflight.CollisionWillRemainFromSource}",
                "Arkive StaticMesh preflight", MessageBoxButton.OK,
                entry.Manifest.Preflight.Readiness == StaticMeshEditReadiness.Refused
                    ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             InvalidDataException or NotSupportedException)
        {
            Log($"StaticMesh edit registration failed: source={source.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Register edited StaticMesh",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _staticMeshWorkspaceBusy = false;
            UpdateStaticMeshActionState();
        }
    }

    private async void CompileStaticMeshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_staticMeshWorkspaceBusy || StaticMeshEditsGrid.SelectedItem is not StaticMeshEditRow row) return;
        _staticMeshWorkspaceBusy = true;
        UpdateStaticMeshActionState();
        try
        {
            var output = Path.Combine(row.Entry.DirectoryPath, "native-compilation");
            var result = await _staticMeshCompilationService.CompileAsync(row.Entry.ManifestPath, output);
            await _staticMeshEditWorkspaceStore.RecordCompilationAsync(row.Entry, result.ManifestPath);
            Log($"StaticMesh compilation completed: object={result.Manifest.ObjectName}, " +
                $"serial={result.Manifest.SourceSerialSize:N0}->{result.Manifest.CompiledSerialSize:N0}, " +
                $"topologyChanged={result.Manifest.TopologyChanged}, collisionRetained={result.Manifest.CollisionPreservedFromSource}, " +
                $"decodeBack={result.Manifest.DecodeBackPassed}, manifest={result.ManifestPath}.");
            await RefreshStaticMeshWorkspaceAsync("native compilation completed", row.Entry.Manifest.EditId);
            StatusText.Text = $"Native StaticMesh candidate compiled for {result.Manifest.ObjectName}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             InvalidDataException or NotSupportedException)
        {
            Log($"StaticMesh compilation failed: edit={row.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Compile StaticMesh",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _staticMeshWorkspaceBusy = false;
            UpdateStaticMeshActionState();
        }
    }

    private async void BuildStagedStaticMeshUpkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_staticMeshWorkspaceBusy || StaticMeshEditsGrid.SelectedItem is not StaticMeshEditRow row ||
            string.IsNullOrWhiteSpace(row.Entry.Manifest.CompilationManifestPath)) return;
        _staticMeshWorkspaceBusy = true;
        UpdateStaticMeshActionState();
        try
        {
            var compilation = await StaticMeshCompilationService.LoadManifestAsync(
                row.Entry.Manifest.CompilationManifestPath);
            var source = await StaticMeshEditableExportService.LoadManifestAsync(
                row.Entry.Manifest.SourceEditableManifestPath);
            var recon = await StaticMeshReconnaissanceWorkspaceStore.LoadReportAsync(
                source.ReconnaissanceJsonPath);
            var output = Path.Combine(row.Entry.DirectoryPath, "staged-upk");
            string manifestPath;
            string outputPath;
            if (compilation.CompiledSerialSize != compilation.SourceSerialSize)
            {
                var result = await _skeletalMeshStagedUpkService.BuildVariableSerializedExportAsync(
                    new VariableSerializedExportStagedUpkRequest(
                        compilation.SourcePackagePath, compilation.SourcePackageSha256,
                        compilation.PhysicalPackageName, compilation.PackageAlias,
                        compilation.NativeExportIndex, compilation.ClassName, compilation.ObjectName,
                        compilation.SerialOffset, compilation.SourceSerialSize,
                        compilation.SourceExportSha256,
                        Path.Combine(Path.GetDirectoryName(row.Entry.Manifest.CompilationManifestPath)!,
                            compilation.CompiledExportRelativePath),
                        compilation.CompiledExportSha256, row.Entry.Manifest.CompilationManifestPath,
                        recon.NativeDataRelativeOffset!.Value, output));
                manifestPath = result.ManifestPath;
                outputPath = result.OutputUpkPath;
            }
            else
            {
                var result = await _skeletalMeshStagedUpkService.BuildSerializedExportAsync(
                    new SerializedExportStagedUpkRequest(
                        compilation.SourcePackagePath, compilation.SourcePackageSha256,
                        compilation.PhysicalPackageName, compilation.PackageAlias,
                        compilation.NativeExportIndex, compilation.ClassName, compilation.ObjectName,
                        compilation.SerialOffset, compilation.SourceSerialSize,
                        compilation.SourceExportSha256,
                        Path.Combine(Path.GetDirectoryName(row.Entry.Manifest.CompilationManifestPath)!,
                            compilation.CompiledExportRelativePath),
                        compilation.CompiledExportSha256, row.Entry.Manifest.CompilationManifestPath, output));
                manifestPath = result.ManifestPath;
                outputPath = result.OutputUpkPath;
            }
            await _staticMeshEditWorkspaceStore.RecordStagedUpkAsync(row.Entry, manifestPath);
            Log($"StaticMesh staged UPK completed: object={compilation.ObjectName}, " +
                $"variableSize={compilation.CompiledSerialSize != compilation.SourceSerialSize}, upk={outputPath}, manifest={manifestPath}.");
            await RefreshStaticMeshWorkspaceAsync("staged UPK completed", row.Entry.Manifest.EditId);
            StatusText.Text = $"Staged StaticMesh UPK created for {compilation.ObjectName}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             InvalidDataException or NotSupportedException or CryptographicException)
        {
            Log($"StaticMesh staged UPK failed: edit={row.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Build staged StaticMesh UPK",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _staticMeshWorkspaceBusy = false;
            UpdateStaticMeshActionState();
        }
    }

    private async void PreviewStagedStaticMeshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_staticMeshWorkspaceBusy || _umodelBridge is null ||
            StaticMeshEditsGrid.SelectedItem is not StaticMeshEditRow row ||
            string.IsNullOrWhiteSpace(row.Entry.Manifest.StagedUpkManifestPath)) return;
        var source = await StaticMeshEditableExportService.LoadManifestAsync(
            row.Entry.Manifest.SourceEditableManifestPath);
        var directory = Path.GetDirectoryName(row.Entry.Manifest.StagedUpkManifestPath)!;
        var upk = Directory.EnumerateFiles(directory, "*.upk", SearchOption.TopDirectoryOnly)
            .SingleOrDefault();
        if (upk is null) return;
        try
        {
            var list = await _umodelBridge.ListAsync(upk);
            var target = list.Assets.FirstOrDefault(asset =>
                asset.ObjectName.Equals(source.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                asset.ClassName.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase) &&
                !asset.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) ??
                         throw new UmodelBridgeException(
                             $"UModel listed the staged package but did not find StaticMesh {source.ObjectName}.");
            var logPath = Path.Combine(directory, "umodel-staticmesh-preview.log");
            var process = _umodelBridge.StartAssetPreviewInPackageRoot(
                upk, target, Path.GetDirectoryName(source.SourcePackagePath)!, logPath);
            Log($"Staged StaticMesh UModel viewer launched: processId={process.Id}, object={target.ObjectName}, upk={upk}, log={logPath}.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                             NotSupportedException or System.ComponentModel.Win32Exception)
        {
            Log($"Staged StaticMesh UModel preview failed: edit={row.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Preview staged StaticMesh",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenStaticMeshEditFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (StaticMeshEditsGrid.SelectedItem is StaticMeshEditRow row)
            OpenFolder(row.Entry.DirectoryPath, "StaticMesh edit");
    }

    private async void DeleteStaticMeshEditButton_Click(object sender, RoutedEventArgs e)
    {
        var rows = StaticMeshEditsGrid.SelectedItems.OfType<StaticMeshEditRow>().ToArray();
        if (_staticMeshWorkspaceBusy || rows.Length == 0) return;
        if (MessageBox.Show(this, $"Delete {rows.Length:N0} selected StaticMesh edit project(s)?\n\n" +
                                  "Clean exports, source UPKs, package revisions, and backups remain untouched.",
                "Delete StaticMesh edits", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var row in rows) await _staticMeshEditWorkspaceStore.DeleteAsync(row.Entry);
        await RefreshStaticMeshWorkspaceAsync("edits deleted");
        await RefreshGuidedTextureModsAsync("StaticMesh edits deleted");
    }

    private void StaticMeshWorkspaceSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateStaticMeshActionState();

    private void UpdateStaticMeshActionState()
    {
        if (ExportEditableStaticMeshButton is null) return;
        var hasRecon = _currentStaticMeshReconnaissance?.Report.Readiness == StaticMeshReadiness.Editable;
        var export = StaticMeshEditableExportsGrid?.SelectedItem as StaticMeshEditableExportRow;
        var edits = StaticMeshEditsGrid?.SelectedItems.OfType<StaticMeshEditRow>().ToArray() ?? [];
        var edit = edits.Length == 1 ? edits[0] : null;
        ExportEditableStaticMeshButton.IsEnabled = !_staticMeshWorkspaceBusy && hasRecon;
        OpenStaticMeshReconFolderButton.IsEnabled = _currentStaticMeshReconnaissance is not null;
        OpenSelectedStaticMeshExportButton.IsEnabled = !_staticMeshWorkspaceBusy && export is not null;
        RegisterEditedStaticMeshButton.IsEnabled = !_staticMeshWorkspaceBusy && export is not null;
        CompileStaticMeshButton.IsEnabled = !_staticMeshWorkspaceBusy && edit is not null &&
                                           edit.Entry.Manifest.Preflight.Readiness != StaticMeshEditReadiness.Refused;
        BuildStagedStaticMeshUpkButton.IsEnabled = !_staticMeshWorkspaceBusy && edit is not null &&
                                                   !string.IsNullOrWhiteSpace(edit.Entry.Manifest.CompilationManifestPath) &&
                                                   File.Exists(edit.Entry.Manifest.CompilationManifestPath);
        PreviewStagedStaticMeshButton.IsEnabled = !_staticMeshWorkspaceBusy && edit is not null &&
                                                  _umodelBridge is not null &&
                                                  !string.IsNullOrWhiteSpace(edit.Entry.Manifest.StagedUpkManifestPath) &&
                                                  File.Exists(edit.Entry.Manifest.StagedUpkManifestPath);
        OpenStaticMeshEditFolderButton.IsEnabled = !_staticMeshWorkspaceBusy && edit is not null;
        DeleteStaticMeshEditButton.IsEnabled = !_staticMeshWorkspaceBusy && edits.Length > 0;
    }

}
