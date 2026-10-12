using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Arkive.Core.Materials;
using Arkive.Core.Discovery;
using Arkive.Core.External;
using Arkive.Core.Assets;
using Arkive.Core.Meshes;
using Arkive.Core.Mods;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;
using Microsoft.Win32;

namespace Arkive.App;

public partial class MainWindow : Window
{
    private async void AuthoringOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AuthoringOpenButton.IsEnabled || AuthoringAssetsGrid.SelectedItem is not AuthoringAssetRow { Edit: { } edit }) return;
        await RunAuthoringOperationAsync(async () =>
        {
            if (edit.Entry is MaterialInstanceEditEntry)
            {
                await RefreshMaterialProjectsAsync("mod asset opened", edit.EditId);
                MaterialEditorTab.IsSelected = true;
            }
            else if (EditablePath(edit) is { } path)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        });
    }

    private void AuthoringAssetsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    { if (e.ChangedButton == MouseButton.Left) AuthoringOpenButton_Click(sender, e); }

    private void AuthoringFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (AuthoringFolderButton.IsEnabled && AuthoringAssetsGrid.SelectedItem is AuthoringAssetRow { Edit: { } edit })
            OpenFolder(Path.GetDirectoryName(EditablePath(edit) ?? edit.ManifestPath)!, "mod working folder");
    }

    private async void AuthoringReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AuthoringReplaceButton.IsEnabled || AuthoringAssetsGrid.SelectedItem is not AuthoringAssetRow { Edit: { } edit } || _authoringDraft is null) return;
        var dialog = new OpenFileDialog
        {
            Title = $"Replace {edit.ObjectName}",
            CheckFileExists = true,
            Filter = edit.Kind == UnifiedPackageAssetKind.Texture2D
                ? "Texture files (*.png;*.dds)|*.png;*.dds" : "Editable mesh (*.glb)|*.glb",
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunAuthoringOperationAsync(async () =>
        {
            var replacement = await ReplaceAuthoringFileAsync(edit, dialog.FileName);
            _authoringDraft = await _modAuthoringWorkspace.SaveAsync(_authoringDraft with
            {
                EditIds = _authoringDraft.EditIds.Select(id => id == edit.EditId ? replacement.EditId : id).ToArray(),
                ReferenceEditIds = _authoringDraft.ReferenceEditIds.Select(id => id == edit.EditId ? replacement.EditId : id).ToArray(),
            });
            AuthoringStatusText.Text = "Replacement passed validation. Save mod to include it.";
        });
    }

    private async Task<ArkiveModLocalEdit> ReplaceAuthoringFileAsync(ArkiveModLocalEdit edit, string path)
    {
        switch (edit.Entry)
        {
            case GuidedTextureModProjectEntry texture:
                var check = _arkiveModLibrary.CreateWorkingDirectory(Guid.NewGuid().ToString("N"), "texture", "validate");
                try
                {
                    await new TextureImportCandidateService().CreateAsync(new TextureImportCandidateRequest(
                        texture.Manifest.NativeTextureManifestPath, path, check, texture.Manifest.AlphaPolicy));
                    var textureManifest = texture.Manifest;
                    var replaced = await _guidedTextureModWorkspace.CreateAsync(textureManifest.DisplayName, textureManifest.PackageAlias,
                        Path.GetFileName(textureManifest.SourcePackagePath), textureManifest.SourcePackagePath, textureManifest.ClassName,
                        textureManifest.ObjectName, textureManifest.ExportIndex, textureManifest.PixelFormat, textureManifest.Width, textureManifest.Height,
                        textureManifest.NativeTextureManifestPath, path, textureManifest.AlphaPolicy);
                    await _guidedTextureModWorkspace.SetIncludedAsync(replaced.ManifestPath, false);
                    return edit with { EditId = replaced.Manifest.ProjectId, ManifestPath = replaced.ManifestPath, Entry = replaced };
                }
                finally { if (Directory.Exists(check)) Directory.Delete(check, recursive: true); }
            case SkeletalMeshEditWorkspaceEntry mesh:
                var manifest = JsonSerializer.Deserialize<SkeletalMeshEditableExportManifest>(
                    await File.ReadAllTextAsync(mesh.Manifest.SourceManifestPath), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                    }) ?? throw new InvalidDataException("The clean mesh manifest is invalid.");
                var directory = Path.GetDirectoryName(mesh.Manifest.SourceManifestPath)!;
                var source = new SkeletalMeshEditableWorkspaceEntry(directory, mesh.Manifest.SourceManifestPath,
                    Path.Combine(directory, manifest.GlbRelativePath), manifest, true, true, true);
                var registered = await _skeletalMeshEditWorkspaceStore.RegisterAsync(source, path, edit.DisplayName);
                if (registered.Preflight.Readiness == SkeletalMeshEditReadiness.Refused)
                {
                    await _skeletalMeshEditWorkspaceStore.DeleteAsync(registered.Entry);
                    throw new InvalidDataException(string.Join("\n", registered.Preflight.Issues
                        .Where(issue => issue.Severity == SkeletalMeshEditIssueSeverity.Error).Select(issue => issue.Message)));
                }
                await _skeletalMeshEditWorkspaceStore.SetIncludedAsync(registered.Entry, false);
                return edit with { EditId = registered.Entry.Manifest.EditId, ManifestPath = registered.Entry.ManifestPath, Entry = registered.Entry };
            case StaticMeshEditEntry mesh:
                var staticEdit = await _staticMeshEditWorkspaceStore.RegisterAsync(mesh.Manifest.SourceEditableManifestPath, path, edit.DisplayName);
                if (staticEdit.Manifest.Preflight.Readiness == StaticMeshEditReadiness.Refused)
                {
                    await _staticMeshEditWorkspaceStore.DeleteAsync(staticEdit);
                    throw new InvalidDataException(string.Join("\n", staticEdit.Manifest.Preflight.Issues));
                }
                await _staticMeshEditWorkspaceStore.SetIncludedAsync(staticEdit, false);
                return edit with { EditId = staticEdit.Manifest.EditId, ManifestPath = staticEdit.ManifestPath, Entry = staticEdit };
            default: throw new NotSupportedException("Edit this asset in Arkive.");
        }
    }

    private async void AuthoringCompareButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AuthoringCompareButton.IsEnabled || AuthoringAssetsGrid.SelectedItem is not AuthoringAssetRow { Edit: { } edit }) return;
        await RunAuthoringOperationAsync(async () =>
        {
            if (edit.Entry is GuidedTextureModProjectEntry texture)
            {
                var original = await ResolveOriginalEditableImageAsync(new GuidedTextureModRow(texture, null, null, [], null));
                var edited = texture.Manifest.EditableImagePath;
                var comparison = _arkiveModLibrary.CreateWorkingDirectory(Guid.NewGuid().ToString("N"), "compare", "texture");
                if (Path.GetExtension(edited).Equals(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    var decoded = await TextureEditableImageService.LoadAsync(edited);
                    edited = Path.Combine(comparison, "edited-preview.png");
                    await PngRgbaCodec.WriteAsync(edited, decoded.Image);
                }
                var result = await TextureEditComparisonService.CompareAsync(
                    original, edited, comparison);
                new TextureComparisonWindow(result) { Owner = this }.Show();
            }
            else if (edit.Entry is MaterialInstanceEditEntry)
            {
                await RefreshMaterialProjectsAsync("mod comparison", edit.EditId);
                MaterialEditorTab.IsSelected = true;
            }
            else
            {
                var source = edit.Entry is SkeletalMeshEditWorkspaceEntry mesh ? mesh.Manifest.SourceManifestPath :
                    ((StaticMeshEditEntry)edit.Entry).Manifest.SourceEditableManifestPath;
                OpenFolder(Path.GetDirectoryName(source)!, "original mesh for comparison");
                AuthoringStatusText.Text = "Original mesh folder opened. Compare its GLB with the working mesh in Blender.";
            }
        });
    }

    private async void AuthoringSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchWorkshopAsync("enter key");
    }

    private async Task AddCompleteAuthoringKitAsync(LibraryAssetRow row, CancellationToken cancellationToken)
    {
        var libraryRoot = _libraryRootPath;
        var metadata = _gameMetadataIndex;
        var session = await CreateAuthoringAddSessionAsync(cancellationToken);
        var mesh = await AddAuthoringAssetAsync(row, session);
        await LoadAuthoringAssetsAsync();
        if (mesh.Entry is not SkeletalMeshEditWorkspaceEntry entry) throw new InvalidOperationException("Select a skeletal mesh for a complete kit.");
        ReportAuthoringExtraction("Exporting exact material and texture references");
        var output = Path.Combine(_projectRoot, "staging", "skeletal-mesh-asset-kits", SafeObjectFileName(row.PackageAlias),
            $"{SafeObjectFileName(row.ObjectName)}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        var kit = await Task.Run(() => _skeletalMeshAssetKitService!.ExtractFromEditableAsync(
            entry.Manifest.SourceManifestPath, output, row.GameMetadata.Select(item => item.DisplayName).ToArray(),
            cancellationToken), cancellationToken);
        Log($"Authoring reference kit exported: object={row.ObjectName}, dependencies={kit.Manifest.Dependencies.Count}, " +
            $"duration={kit.Duration.TotalSeconds:N2}s, manifest={kit.ManifestPath}.");
        if (metadata is null || libraryRoot is null)
        {
            AuthoringStatusText.Text = "Mesh and reference kit exported. Load the game DB mapping and library to add its texture/material working copies.";
            return;
        }
        var dependencies = kit.Manifest.Dependencies.Where(dependency =>
            dependency.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) ||
            dependency.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)).ToArray();
        ReportAuthoringExtraction("Resolving exact dependencies from their packages", 0, dependencies.Length);
        var packages = await Task.Run(() => _packageIndexStore.LoadPackagesByAliasAsync(libraryRoot,
            dependencies.Select(dependency => dependency.PackageName).Distinct(StringComparer.OrdinalIgnoreCase),
            metadata, cancellationToken: cancellationToken), cancellationToken);
        var skipped = new List<string>();
        var completed = 0;
        foreach (var dependency in dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var exact = packages.Where(package => package.PackageAlias.Equals(dependency.PackageName, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(package => package.Assets.Where(asset => asset.ObjectName.Equals(dependency.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                        asset.ClassName.Equals(dependency.ClassName, StringComparison.OrdinalIgnoreCase)).Select(asset => (Package: package, Asset: asset)))
                    .ToArray();
                if (exact.Length != 1) { skipped.Add(dependency.ObjectName); continue; }
                var (package, asset) = exact[0];
                await AddAuthoringAssetAsync(new LibraryAssetRow(package.SourcePath, package.PackageAlias,
                    package.PhysicalPackageName, package.Assets.Count, asset.ExportIndex, asset.Kind,
                    asset.ClassName, asset.ObjectName, asset.SerialSize, asset.SerialOffset,
                    metadata.Find(package.PackageAlias, package.PhysicalPackageName, asset.ObjectName)), session, reference: true);
            }
            catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
            { skipped.Add(dependency.ObjectName); Log($"Kit dependency could not be prepared: {dependency.ObjectName}: {exception.Message}"); }
            finally { ReportAuthoringExtraction($"Processed reference: {dependency.ObjectName}", ++completed); }
        }
        AuthoringStatusText.Text = "Mesh and optional reference assets added. Tick the references you intend to change." +
            (skipped.Count > 0 ? $" {skipped.Count} dependencies remain in the exported kit; see Diagnostics." : string.Empty);
    }

    private async void AuthoringPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AuthoringPreviewButton.IsEnabled || AuthoringAssetsGrid.SelectedItem is not AuthoringAssetRow { Edit: { } edit } || _umodelBridge is null) return;
        try
        {
            var clean = await _arkiveModApply.ResolveCleanPackageAsync(edit.SourcePackagePath);
            var catalogue = await Task.Run(() => new AssetCatalogReader(_reader, _protectionProvider).Read(clean));
            var match = catalogue.Assets.SingleOrDefault(asset => asset.ObjectName.Equals(edit.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                asset.ClassName.Equals(edit.Kind.ToString(), StringComparison.OrdinalIgnoreCase));
            if (match is null) throw new InvalidOperationException("The original asset is unavailable or ambiguous in its clean package.");
            _umodelBridge.StartAssetPreview(clean, new UmodelAssetEntry(match.ExportIndex, match.SerialOffset,
                match.SerialSize, match.ClassName, match.ObjectName));
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception) || exception is System.ComponentModel.Win32Exception)
        { AuthoringStatusText.Text = $"Original preview failed: {exception.Message}"; Log($"Original authoring preview failed: {exception}"); }
    }
}
