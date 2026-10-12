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
    private async void SkeletalMeshReconButton_Click(object sender, RoutedEventArgs e)
    {
        if (AssetsGrid.SelectedItem is not AssetRow row ||
            string.IsNullOrEmpty(_currentPackagePath) ||
            !CanReconnoitreMesh(row.Kind, row.ClassName)) return;
        var objectName = row.ExternalAsset?.ObjectName ?? ObjectNameFromPath(row.ObjectPath);
        if (CanReconnoitreStaticMesh(row.Kind, row.ClassName))
        {
            await RunStaticMeshReconnaissanceAsync(
                _currentPackagePath, LostArkPackageName.GetDisplayName(_currentPackagePath),
                row.ExportIndex, row.Kind, row.ClassName, objectName,
                row.SerialOffset, row.SerialSize, "open-package Assets result");
            return;
        }
        await RunSkeletalMeshReconnaissanceAsync(
            _currentPackagePath,
            LostArkPackageName.GetDisplayName(_currentPackagePath),
            row.ExportIndex, row.Kind, row.ClassName, objectName,
            row.SerialOffset, row.SerialSize, "open-package Assets result");
    }

    private async void LibrarySkeletalMeshReconButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is not LibraryAssetRow row ||
            !CanReconnoitreMesh(row.Kind, row.ClassName)) return;
        if (CanReconnoitreStaticMesh(row.Kind, row.ClassName))
        {
            await RunStaticMeshReconnaissanceAsync(
                row.SourcePath, row.PackageAlias, row.ExportIndex, row.Kind,
                row.ClassName, row.ObjectName, row.SerialOffset, row.SerialSize,
                "Library result");
            return;
        }
        await RunSkeletalMeshReconnaissanceAsync(
            row.SourcePath, row.PackageAlias, row.ExportIndex, row.Kind,
            row.ClassName, row.ObjectName, row.SerialOffset, row.SerialSize,
            "Library result");
    }

    private async void ExtractSkeletalMeshAssetKitButton_Click(object sender, RoutedEventArgs e)
    {
        if (AssetsGrid.SelectedItem is not AssetRow row ||
            string.IsNullOrWhiteSpace(_currentPackagePath) ||
            !CanReconnoitreSkeletalMesh(row.Kind, row.ClassName)) return;
        await ExtractSkeletalMeshAssetKitAsync(new SkeletalMeshAssetKitSelection(
            _currentPackagePath,
            LostArkPackageName.GetDisplayName(_currentPackagePath),
            Path.GetFileName(_currentPackagePath),
            row.ExportIndex,
            row.Kind,
            row.ClassName,
            row.ExternalAsset?.ObjectName ?? ObjectNameFromPath(row.ObjectPath),
            row.SerialOffset,
            row.SerialSize,
            row.GameMetadata.Select(item => item.DisplayName).ToArray()),
            "open-package Assets result");
    }

    private async void LibraryExtractSkeletalMeshAssetKitButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is not LibraryAssetRow row ||
            !CanReconnoitreSkeletalMesh(row.Kind, row.ClassName)) return;
        await ExtractSkeletalMeshAssetKitAsync(new SkeletalMeshAssetKitSelection(
            row.SourcePath, row.PackageAlias, row.PhysicalPackageName,
            row.ExportIndex, row.Kind, row.ClassName, row.ObjectName,
            row.SerialOffset, row.SerialSize,
            row.GameMetadata.Select(item => item.DisplayName).ToArray()), "Library result");
    }

    private async void EntityBrowserExtractSkeletalMeshAssetKitButton_Click(
        object sender, RoutedEventArgs e)
    {
        if (EntityBrowserAssetsGrid.SelectedItem is not EntityBrowserAssetRow row ||
            !CanReconnoitreSkeletalMesh(row.Kind, row.ClassName)) return;
        await ExtractSkeletalMeshAssetKitAsync(new SkeletalMeshAssetKitSelection(
            row.SourcePath, row.PackageAlias, row.PhysicalPackageName,
            row.ExportIndex, row.Kind, row.ClassName, row.ObjectName,
            row.Match.Asset.SerialOffset, row.Match.Asset.SerialSize,
            row.Match.Asset.GameMetadata.Select(item => item.DisplayName).ToArray()),
            "Characters & NPCs linked asset");
    }

    private async Task<SkeletalMeshAssetKitResult?> ExtractSkeletalMeshAssetKitAsync(
        SkeletalMeshAssetKitSelection selection,
        string trigger,
        bool quiet = false)
    {
        if (_skeletalMeshAssetKitInProgress || _skeletalMeshAssetKitService is null) return null;
        SkeletalMeshAssetKitResult? completed = null;
        _skeletalMeshAssetKitInProgress = true;
        UpdateSkeletalMeshReconnaissanceActionState();
        UpdateLibraryActionState();
        UpdateEntityBrowserAssetActions();
        StatusText.Text = $"Extracting the complete asset kit for {selection.ObjectName}...";
        Log($"SkeletalMesh asset-kit extraction started: trigger={trigger}, " +
            $"package={selection.PackagePath}, alias={selection.PackageAlias}, " +
            $"index={selection.ExportIndex}, class={selection.ClassName}, " +
            $"object={selection.ObjectName}, logical=0x{selection.SerialOffset:X}+0x{selection.SerialSize:X}.");
        try
        {
            var resolved = ResolveCurrentSkeletalMeshSelection(
                selection.PackagePath, selection.ObjectName, selection.ExportIndex,
                selection.SerialOffset, selection.SerialSize);
            if (resolved.ExportIndex != selection.ExportIndex ||
                resolved.SerialOffset != selection.SerialOffset ||
                resolved.SerialSize != selection.SerialSize)
            {
                Log($"SkeletalMesh asset-kit selection refreshed from the current export table: " +
                    $"object={selection.ObjectName}, cached={selection.ExportIndex}/" +
                    $"0x{selection.SerialOffset:X}+0x{selection.SerialSize:X}, current={resolved.ExportIndex}/" +
                    $"0x{resolved.SerialOffset:X}+0x{resolved.SerialSize:X}.");
                selection = selection with
                {
                    ExportIndex = resolved.ExportIndex,
                    Kind = resolved.Kind,
                    ClassName = resolved.ClassName,
                    SerialOffset = resolved.SerialOffset,
                    SerialSize = resolved.SerialSize,
                };
            }

            var packageSha256 = await _baselineWorkspaceStore.GetPackageSha256Async(selection.PackagePath);
            var report = await _skeletalMeshReconnaissanceService.InspectAsync(
                new SkeletalMeshReconnaissanceRequest(
                    selection.PackagePath, selection.PackageAlias, selection.ExportIndex,
                    selection.Kind, selection.ClassName, selection.ObjectName,
                    selection.SerialOffset, selection.SerialSize, packageSha256));
            var recon = await _skeletalMeshReconnaissanceWorkspaceStore.SaveAsync(report);
            _currentSkeletalMeshReconnaissance = recon;
            if (report.Readiness != SkeletalMeshReadiness.NoOpReady)
            {
                var reason = report.Issues.FirstOrDefault(issue =>
                    issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))?.Message ??
                             "Arkive did not prove a safe native SkeletalMesh layout.";
                throw new NotSupportedException(reason);
            }

            var editableDirectory = Path.Combine(recon.DirectoryPath, "editable-glb",
                $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
            var editable = await _skeletalMeshEditableExportService.ExportAsync(
                new SkeletalMeshEditableExportRequest(report, recon.JsonPath, editableDirectory));
            _currentSkeletalMeshEditableExport = editable;

            var kitLeaf = $"{SafeObjectFileName(selection.ObjectName)}-" +
                          $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}";
            var output = Path.Combine(_projectRoot, "staging", "skeletal-mesh-asset-kits",
                SafeObjectFileName(selection.PackageAlias), kitLeaf);
            var result = await _skeletalMeshAssetKitService.ExtractAsync(
                new SkeletalMeshAssetKitRequest(
                    recon, editable, output, selection.InGameNames));
            completed = result;
            await RefreshSkeletalMeshEditableWorkspaceAsync(
                "complete SkeletalMesh asset kit extracted", editable.Manifest.ExportId);

            var materials = result.Manifest.Dependencies.Count(dependency =>
                dependency.ClassName.Contains("Material", StringComparison.OrdinalIgnoreCase));
            var textures = result.Manifest.Dependencies.Count(dependency =>
                dependency.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase));
            StatusText.Text = $"Complete asset kit extracted for {selection.ObjectName}.";
            Log($"SkeletalMesh asset-kit extraction completed in {result.Duration.TotalSeconds:N3}s: " +
                $"object={result.Manifest.ObjectName}, lods={result.Manifest.LodCount}, " +
                $"bones={result.Manifest.BoneCount}, materials={materials}, textures={textures}, " +
                $"files={result.Manifest.Files.Count}, unresolvedMaterials={result.Manifest.UnresolvedMaterialSlots.Count}, " +
                $"manifest={result.ManifestPath}, output={result.OutputDirectory}.");
            if (!quiet) OpenFolder(result.OutputDirectory, "complete SkeletalMesh asset kit");
            if (!quiet) MessageBox.Show(this,
                $"Complete asset kit extracted.\n\n" +
                $"Editable mesh: 1 GLB\nExact dependencies: {materials} material(s), {textures} texture(s)\n" +
                $"Unresolved shared material slots: {result.Manifest.UnresolvedMaterialSlots.Count}\n\n" +
                $"The folder has been opened. No game file or mod project was changed.",
                "Arkive complete asset kit", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is PackageAccessException or PackageFormatException or UmodelBridgeException or
                SkeletalMeshEditableExportRefusalException or IOException or UnauthorizedAccessException or
                NotSupportedException or InvalidDataException or CryptographicException or ArgumentException)
        {
            StatusText.Text = "Complete SkeletalMesh asset-kit extraction failed; see Diagnostics.";
            Log($"SkeletalMesh asset-kit extraction failed: trigger={trigger}, package={selection.PackagePath}, object={selection.ObjectName}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive complete asset kit",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshAssetKitInProgress = false;
            UpdateSkeletalMeshReconnaissanceActionState();
            UpdateLibraryActionState();
            UpdateEntityBrowserAssetActions();
        }
        return completed;
    }

    private async Task RunSkeletalMeshReconnaissanceAsync(
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
        if (_skeletalMeshReconnaissanceInProgress) return;
        _skeletalMeshReconnaissanceInProgress = true;
        _currentSkeletalMeshEditableExport = null;
        UpdateSkeletalMeshReconnaissanceActionState();
        UpdateLibraryActionState();
        StatusText.Text = $"Reading native SkeletalMesh evidence for {packageAlias}.{objectName}...";
        Log(
            $"SkeletalMesh reconnaissance started: trigger={trigger}, package={packagePath}, " +
            $"alias={packageAlias}, index={exportIndex}, class={className}, object={objectName}, " +
            $"logical=0x{serialOffset:X}+0x{serialSize:X}, provider={_protectionProvider.Name}, " +
            $"oracle={(_umodelBridge is null ? "unavailable" : _umodelBridge.ExecutablePath)}.");
        try
        {
            var resolved = ResolveCurrentSkeletalMeshSelection(
                packagePath, objectName, exportIndex, serialOffset, serialSize);
            if (resolved.ExportIndex != exportIndex || resolved.SerialOffset != serialOffset ||
                resolved.SerialSize != serialSize)
            {
                Log($"SkeletalMesh reconnaissance selection refreshed from the current export table: " +
                    $"object={objectName}, cached={exportIndex}/0x{serialOffset:X}+0x{serialSize:X}, " +
                    $"current={resolved.ExportIndex}/0x{resolved.SerialOffset:X}+0x{resolved.SerialSize:X}.");
                exportIndex = resolved.ExportIndex;
                kind = resolved.Kind;
                className = resolved.ClassName;
                serialOffset = resolved.SerialOffset;
                serialSize = resolved.SerialSize;
            }
            var packageSha256 = await _baselineWorkspaceStore.GetPackageSha256Async(packagePath);
            var report = await _skeletalMeshReconnaissanceService.InspectAsync(
                new SkeletalMeshReconnaissanceRequest(
                    packagePath, packageAlias, exportIndex, kind, className, objectName,
                    serialOffset, serialSize, packageSha256));
            var entry = await _skeletalMeshReconnaissanceWorkspaceStore.SaveAsync(report);
            _currentSkeletalMeshReconnaissance = entry;
            DisplaySkeletalMeshReconnaissance(entry);
            var oracle = report.Oracle;
            var native = report.NativeLayout;
            Log(
                $"SkeletalMesh reconnaissance completed in {report.Duration.TotalSeconds:N3}s: " +
                $"readiness={report.Readiness}, selectionIndex={report.ExportIndex}, " +
                $"nativeIndex={report.NativeExportIndex?.ToString() ?? "unresolved"}, " +
                $"propertyStart={FormatOptionalHex(report.PropertyStreamRelativeOffset)}, " +
                $"nativeStart={FormatOptionalHex(report.NativeDataRelativeOffset)}, nativeBytes={report.NativeDataSize?.ToString("N0") ?? "-"}, " +
                $"properties={report.Properties.Count}, lods={oracle?.LodCount.ToString() ?? "-"}, " +
                $"bones={oracle?.BoneCount.ToString() ?? "-"}, sockets={oracle?.SocketCount.ToString() ?? "-"}, " +
                $"vertexColors={oracle?.HasVertexColors?.ToString() ?? "-"}, sections={oracle?.Lods.Sum(lod => lod.Sections.Count).ToString() ?? "-"}, " +
                $"packageSha={report.PackageSha256}, exportSha={report.ExportPayloadSha256}, " +
                $"nativeSha={report.NativeDataSha256 ?? "-"}, nativeProfile={native?.ParserProfile ?? "unresolved"}, " +
                $"nativeRanges={native?.Ranges.Count.ToString() ?? "-"}, parsedToEnd={native?.ParsedToEnd.ToString() ?? "-"}, json={entry.JsonPath}.");
            if (oracle is not null)
            {
                foreach (var lod in oracle.Lods)
                    Log($"SkeletalMesh oracle LOD {lod.LodIndex}: vertices={lod.VertexCount}, indices={lod.IndexCount}, triangles={lod.TriangleCount}, UVs={lod.TextureCoordinateCount}, sections={lod.Sections.Count}.");
            }
            if (native is not null)
            {
                Log($"SkeletalMesh native skeleton: bones={native.BoneCount}, depth={native.SkeletalDepth}, materials={native.MaterialReferenceCount}, extensionVersion={native.LostArkExtensionVersion}, extensionValues=[{string.Join(",", native.LostArkExtensionValues)}].");
                foreach (var lod in native.Lods)
                    Log($"SkeletalMesh native LOD {lod.LodIndex}: vertices={lod.VertexCount}, indices={lod.IndexBuffer.ElementCount}, triangles={lod.Sections.Sum(section => section.TriangleCount)}, UVs={lod.TextureCoordinateCount}, sections={lod.Sections.Count}, chunks={lod.Chunks.Count}, stride={lod.VertexBuffer.ElementSize}, positionOffset={lod.VertexPositionOffset}, requiredBoneWidth={lod.RequiredBoneElementSize}, vertexRange=+0x{lod.VertexBuffer.DataRelativeOffset:X}+0x{lod.VertexBuffer.DataBytes:X}.");
            }
            foreach (var issue in report.Issues)
                Log($"SkeletalMesh recon {issue.Severity} {issue.Code}: {issue.Message}");
            StatusText.Text = report.Readiness == SkeletalMeshReadiness.NoOpReady
                ? $"Mesh report saved for {objectName}; no-op proof and editable GLB export are available."
                : $"Mesh report saved for {objectName}, but no-op reconstruction was refused.";
        }
        catch (Exception exception) when (
            exception is PackageAccessException or PackageFormatException or UmodelBridgeException or
                IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            StatusText.Text = "Native SkeletalMesh reconnaissance failed; see Diagnostics.";
            Log($"SkeletalMesh reconnaissance failed: trigger={trigger}, package={packagePath}, object={objectName}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive SkeletalMesh reconnaissance error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshReconnaissanceInProgress = false;
            UpdateSkeletalMeshReconnaissanceActionState();
            UpdateLibraryActionState();
        }
    }

}
