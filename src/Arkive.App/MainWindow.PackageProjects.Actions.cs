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
    private void GuidedNextActionButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_guidedNextAction)
        {
            case GuidedNextAction.OpenEdit:
                GuidedOpenEditableButton_Click(sender, e);
                break;
            case GuidedNextAction.Build:
                GuidedBuildButton_Click(sender, e);
                break;
            case GuidedNextAction.Install:
                GuidedInstallButton_Click(sender, e);
                break;
            case GuidedNextAction.Restore:
                GuidedRestoreButton_Click(sender, e);
                break;
        }
    }

    private async void GuidedOpenEditableButton_Click(object sender, RoutedEventArgs e)
    {
        if (GuidedTextureModsGrid.SelectedItem is GuidedTextureModRow row)
        {
            try
            {
                await _guidedTextureModWorkspace.TouchAsync(row.Entry.ManifestPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log($"Guided recent-project timestamp update failed: project={row.Entry.ManifestPath}, error={exception}");
            }
            OpenTextureCandidateFile(row.Entry.Manifest.EditableImagePath, "guided editable texture image");
            GuidedBuildStatusText.Text =
                "Save your changes in the editor, then choose Import edit & rebuild UPK.";
            Log($"Guided editable texture opened: projectId={row.Entry.Manifest.ProjectId}, path={row.Entry.Manifest.EditableImagePath}.");
        }
    }

    private async void GuidedSaveMetadataButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress ||
            GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow package) return;
        try
        {
            await _guidedTexturePackageWorkspace.UpdateMetadataAsync(
                package.Entry.ManifestPath,
                GuidedModNameTextBox.Text,
                GuidedModNotesTextBox.Text);
            Log($"Guided package metadata saved: packageId={package.Entry.Manifest.PackageProjectId}, name={GuidedModNameTextBox.Text.Trim()}, notesLength={GuidedModNotesTextBox.Text.Trim().Length}.");
            await RefreshGuidedTextureModsAsync("package metadata saved", package.Assets.FirstOrDefault()?.Entry.Manifest.ProjectId);
            GuidedBuildStatusText.Text = "Mod name and notes saved. Rebuild to embed a changed name in the install bundle.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log($"Guided package metadata save failed: package={package.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Save mod details", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void GuidedFavoritePackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress ||
            GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow package) return;
        try
        {
            await _guidedTexturePackageWorkspace.UpdateMetadataAsync(
                package.Entry.ManifestPath,
                GuidedModNameTextBox.Text,
                GuidedModNotesTextBox.Text,
                !package.Entry.Manifest.IsFavorite);
            Log($"Guided package favourite changed: packageId={package.Entry.Manifest.PackageProjectId}, favourite={!package.Entry.Manifest.IsFavorite}.");
            await RefreshGuidedTextureModsAsync("package favourite changed", package.Assets.FirstOrDefault()?.Entry.Manifest.ProjectId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Guided package favourite update failed: package={package.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Favourite modified UPK", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void GuidedFavoriteAssetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress ||
            GuidedTextureModsGrid.SelectedItem is not GuidedTextureModRow asset) return;
        try
        {
            await _guidedTextureModWorkspace.SetFavoriteAsync(
                asset.Entry.ManifestPath,
                !asset.Entry.Manifest.IsFavorite);
            Log($"Guided texture favourite changed: projectId={asset.Entry.Manifest.ProjectId}, favourite={!asset.Entry.Manifest.IsFavorite}.");
            await RefreshGuidedTextureModsAsync("texture favourite changed", asset.Entry.Manifest.ProjectId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Guided texture favourite update failed: project={asset.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Favourite texture", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void GuidedCompareEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress ||
            GuidedTextureModsGrid.SelectedItem is not GuidedTextureModRow asset) return;
        try
        {
            var originalImage = await ResolveOriginalEditableImageAsync(asset);
            var result = await TextureEditComparisonService.CompareAsync(
                originalImage,
                asset.Entry.Manifest.EditableImagePath,
                Path.Combine(asset.Entry.DirectoryPath, "comparison"));
            new TextureComparisonWindow(result) { Owner = this }.Show();
            Log($"Guided texture comparison completed: projectId={asset.Entry.Manifest.ProjectId}, changedPixels={result.ChangedPixels}, changedPercent={result.ChangedPercent:F6}, maxDelta={result.MaximumChannelDelta}, output={result.DifferenceImagePath}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log($"Guided texture comparison failed: project={asset.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Compare texture edit", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task<string> ResolveOriginalEditableImageAsync(GuidedTextureModRow asset)
    {
        var native = await NativeTextureExportService.LoadManifestAsync(
            asset.Entry.Manifest.NativeTextureManifestPath);
        if (string.IsNullOrWhiteSpace(native.EditImageRelativePath))
        {
            throw new NotSupportedException("The native export has no original editable image.");
        }
        var nativeDirectory = Path.GetDirectoryName(asset.Entry.Manifest.NativeTextureManifestPath)!;
        var originalImage = Path.GetFullPath(Path.Combine(nativeDirectory, native.EditImageRelativePath));
        var relative = Path.GetRelativePath(nativeDirectory, originalImage);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException("The original editable image path escapes its native export session.");
        }
        return originalImage;
    }

    private async void GuidedCreateFaceMapButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress || _nativeTextureExportService is null ||
            GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow package) return;

        var prefix = package.Assets
            .Select(asset => TryGetFaceMapPrefix(asset.ObjectName, out var candidatePrefix)
                ? candidatePrefix
                : null)
            .FirstOrDefault(candidatePrefix => !string.IsNullOrWhiteSpace(candidatePrefix));
        if (string.IsNullOrWhiteSpace(prefix)) return;
        var blockingMod = await FindActiveGuidedModForPackageAsync(
            package.Entry.Manifest.SourcePackagePath);
        if (blockingMod is not null)
        {
            MessageBox.Show(
                this,
                $"Restore '{blockingMod}' before creating a numbered face map. " +
                "The mapping set must start from the clean source UPK.",
                "Arkive face mapping",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var decision = MessageBox.Show(
            this,
            $"Arkive will find every numbered diffuse face Texture2D for {prefix} in this UPK and place its " +
            "two-digit suffix prominently over the face. Existing editable images will be backed up, " +
            "then reset to their pristine exports before numbering. All mapped textures will be checked " +
            "for the next cumulative build.\n\nContinue?",
            "Create numbered face map",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (decision != MessageBoxResult.Yes) return;

        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        UpdateLibraryActionState();
        UpdateEntityBrowserAssetActions();
        UpdateTextureReconnaissanceActionState();
        GuidedBuildProgressBar.Minimum = 0;
        GuidedBuildProgressBar.Maximum = 1;
        GuidedBuildProgressBar.Value = 0;
        StatusText.Text = $"Creating numbered {prefix} face-identification textures...";
        Log(
            $"Guided numbered face-map creation started: packageId={package.Entry.Manifest.PackageProjectId}, " +
            $"package={package.Entry.Manifest.SourcePackagePath}, prefix={prefix}.");
        try
        {
            var service = new FaceTextureMappingService(
                _catalogReader,
                _textureReconnaissanceService,
                _textureReconnaissanceWorkspaceStore,
                _nativeTextureExportService,
                _exportWorkspaceStore,
                _guidedTextureModWorkspace,
                _baselineWorkspaceStore);
            var progress = new Progress<(int Completed, int Total, string ObjectName)>(value =>
            {
                GuidedBuildProgressBar.Maximum = value.Total;
                GuidedBuildProgressBar.Value = value.Completed;
                GuidedBuildStatusText.Text =
                    $"Numbered {value.Completed:N0} of {value.Total:N0}: {value.ObjectName}";
                StatusText.Text = GuidedBuildStatusText.Text;
                Log(
                    $"Guided numbered face-map progress: completed={value.Completed}, " +
                    $"total={value.Total}, object={value.ObjectName}.");
            });
            var result = await service.CreateAsync(
                package.Entry.Manifest.SourcePackagePath,
                package.Entry.Manifest.PackageAlias,
                package.Entry.Manifest.PhysicalPackageName,
                prefix,
                progress);
            await RefreshExportWorkspaceAsync("numbered face-map exports completed");
            await RefreshGuidedTextureModsAsync(
                "numbered face map created", result.Projects.FirstOrDefault()?.Manifest.ProjectId);
            GuidedBuildProgressBar.Maximum = result.Manifest.CandidateCount;
            GuidedBuildProgressBar.Value = result.Manifest.CandidateCount;
            GuidedBuildStatusText.Text =
                $"Created {result.Manifest.CandidateCount:N0} numbered face textures. " +
                "They are all checked; choose Rebuild checked assets, then Install mod.";
            StatusText.Text = GuidedBuildStatusText.Text;
            Log(
                $"Guided numbered face-map creation completed: mappingId={result.Manifest.MappingId}, " +
                $"assets={result.Manifest.CandidateCount}, manifest={result.ManifestPath}, " +
                $"backupDirectory={Path.Combine(result.SessionDirectory, "previous-edits")}.");
            MessageBox.Show(
                this,
                $"Created {result.Manifest.CandidateCount:N0} numbered face textures and checked all of them.\n\n" +
                "Next: choose Rebuild checked assets, install the resulting mod, and note the number " +
                "shown on each face in character creation. Existing edits were backed up under:\n" +
                Path.Combine(result.SessionDirectory, "previous-edits"),
                "Numbered face map ready",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            GuidedBuildStatusText.Text = "Arkive could not create the numbered face map.";
            StatusText.Text = GuidedBuildStatusText.Text;
            Log(
                $"Guided numbered face-map creation failed: package={package.Entry.Manifest.SourcePackagePath}, " +
                $"prefix={prefix}, error={exception}");
            MessageBox.Show(
                this, exception.Message, "Arkive face mapping",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
            UpdateLibraryActionState();
            UpdateEntityBrowserAssetActions();
            UpdateTextureReconnaissanceActionState();
        }
    }

    private static bool TryGetFaceMapPrefix(string objectName, out string prefix)
    {
        prefix = "";
        if (string.IsNullOrWhiteSpace(objectName)) return false;

        var match = Regex.Match(
            objectName,
            @"^(?<prefix>.+)_\d{2}_face_d$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            match = Regex.Match(
                objectName,
                @"^(?<prefix>.+)_face_\d{2}_d$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        if (!match.Success) return false;

        prefix = match.Groups["prefix"].Value;
        return !string.IsNullOrWhiteSpace(prefix);
    }

    private void GuidedInspectSourcePackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_umodelBridge is null ||
            GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow package) return;
        try
        {
            var process = _umodelBridge.StartPreview(package.Entry.Manifest.SourcePackagePath);
            StatusText.Text = $"Opened source UPK {package.PackageAlias} in UModel.";
            Log(
                $"Guided source package viewer launched: packageId={package.Entry.Manifest.PackageProjectId}, " +
                $"process={process.Id}, path={package.Entry.Manifest.SourcePackagePath}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or ArgumentException)
        {
            Log($"Guided source package viewer failed: package={package.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Inspect source UPK", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void GuidedInspectSelectedAssetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress || _umodelBridge is null ||
            GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow package ||
            GuidedTextureModsGrid.SelectedItem is not GuidedTextureModRow asset) return;
        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        try
        {
            var built = package.LatestRevision is { } latest && latest.Assets.Any(item =>
                item.ProjectId.Equals(asset.Entry.Manifest.ProjectId, StringComparison.OrdinalIgnoreCase));
            var packagePath = package.Entry.Manifest.SourcePackagePath;
            if (built)
            {
                packagePath = await ResolveVerifiedGuidedOutputAsync(package.LatestRevision!);
            }
            var list = await _umodelBridge.ListAsync(packagePath);
            var target = list.Assets.FirstOrDefault(item => item.ObjectName.Equals(
                asset.Entry.Manifest.ObjectName, StringComparison.OrdinalIgnoreCase)) ??
                         throw new UmodelBridgeException(
                             $"UModel did not list {asset.Entry.Manifest.ObjectName} in the selected package.");
            var process = _umodelBridge.StartAssetPreview(packagePath, target);
            StatusText.Text = built
                ? $"Opened the built {target.ObjectName} texture in UModel."
                : $"Opened the source {target.ObjectName} texture in UModel.";
            Log(
                $"Guided selected texture viewer launched: projectId={asset.Entry.Manifest.ProjectId}, " +
                $"source={(built ? "built" : "clean")}, process={process.Id}, " +
                $"package={packagePath}, object={target.ObjectName}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or System.ComponentModel.Win32Exception)
        {
            Log($"Guided selected texture viewer failed: project={asset.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Inspect selected texture", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
        }
    }

    private async void GuidedInspectSelectedMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress || _umodelBridge is null ||
            GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow package ||
            GuidedMaterialModsGrid.SelectedItem is not GuidedMaterialModRow material) return;
        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        try
        {
            var built = package.LatestRevision is { } latest && latest.MaterialAssets.Any(item =>
                item.EditId.Equals(material.Entry.Document.EditId, StringComparison.OrdinalIgnoreCase));
            var packagePath = built
                ? await ResolveVerifiedGuidedOutputAsync(package.LatestRevision!)
                : package.Entry.Manifest.SourcePackagePath;
            var list = await _umodelBridge.ListAsync(packagePath);
            var target = list.Assets.FirstOrDefault(item => item.ObjectName.Equals(
                material.Entry.Document.ObjectName, StringComparison.OrdinalIgnoreCase)) ??
                         throw new UmodelBridgeException(
                             $"UModel did not list {material.Entry.Document.ObjectName} in the selected package.");
            var process = _umodelBridge.StartAssetPreview(packagePath, target);
            StatusText.Text = built
                ? $"Opened the built {target.ObjectName} material in UModel."
                : $"Opened the source {target.ObjectName} material in UModel.";
            Log($"Guided selected material viewer launched: editId={material.Entry.Document.EditId}, " +
                $"source={(built ? "built" : "clean")}, process={process.Id}, package={packagePath}, object={target.ObjectName}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or System.ComponentModel.Win32Exception)
        {
            Log($"Guided selected material viewer failed: project={material.Entry.DocumentPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Inspect selected material",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
        }
    }

    private async Task ResolveGuidedModelPreviewCandidatesAsync()
    {
        _modelPreviewResolutionCancellation?.Cancel();
        _modelPreviewResolutionCancellation?.Dispose();
        _modelPreviewResolutionCancellation = new CancellationTokenSource();
        var token = _modelPreviewResolutionCancellation.Token;
        GuidedModelPreviewComboBox.ItemsSource = null;
        GuidedModelPreviewComboBox.IsEnabled = false;
        GuidedPreviewOnModelButton.IsEnabled = false;

        var selected = GuidedTextureModsGrid.SelectedItems
            .OfType<GuidedTextureModRow>()
            .ToArray();
        if (selected.Length != 1 || _gameMetadataIndex is null ||
            string.IsNullOrWhiteSpace(_libraryRootPath) || !Directory.Exists(_libraryRootPath))
        {
            return;
        }

        var asset = selected[0];
        var references = TextureModelPreviewResolver.Resolve(
            _gameMetadataIndex,
            asset.Entry.Manifest.PackageAlias,
            asset.Entry.Manifest.PhysicalPackageName,
            asset.Entry.Manifest.ObjectName);
        if (references.Count == 0)
        {
            GuidedBuildStatusText.Text =
                $"No verified source model is linked to {asset.ObjectName} yet. Texture editing and UPK rebuilding remain available.";
            Log(
                $"Guided model-preview resolution returned no verified dependency: " +
                $"projectId={asset.Entry.Manifest.ProjectId}, object={asset.ObjectName}.");
            return;
        }

        try
        {
            var candidates = new List<GuidedModelPreviewCandidate>();
            foreach (var reference in references)
            {
                token.ThrowIfCancellationRequested();
                var result = await _packageIndexStore.SearchAsync(
                    _libraryRootPath,
                    reference.DisplayName,
                    category: null,
                    gameMetadata: _gameMetadataIndex,
                    searchScope: PackageIndexSearchScope.InGameName,
                    archetype: reference.Archetype,
                    resultLimit: 250,
                    cancellationToken: token);
                candidates.AddRange(result.Matches
                    .Where(match => match.Kind is AssetKind.SkeletalMesh or AssetKind.StaticMesh)
                    .Where(match => PreviewPackageMatches(match.PackageAlias, reference.PackageName))
                    .Where(match => PreviewObjectMatches(match.ObjectName, reference.ObjectName))
                    .Select(match => new GuidedModelPreviewCandidate(reference, match)));
            }

            var resolved = candidates
                .DistinctBy(item => item.Match.SourcePath.ToUpperInvariant() + "\0" +
                                    item.Match.ObjectName.ToUpperInvariant())
                .OrderByDescending(item => item.Match.ObjectName.Equals(
                    item.Reference.ObjectName, StringComparison.OrdinalIgnoreCase))
                .ThenBy(item => item.Reference.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Match.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (token.IsCancellationRequested ||
                GuidedTextureModsGrid.SelectedItem is not GuidedTextureModRow current ||
                !current.Entry.Manifest.ProjectId.Equals(
                    asset.Entry.Manifest.ProjectId, StringComparison.OrdinalIgnoreCase)) return;

            GuidedModelPreviewComboBox.ItemsSource = resolved;
            GuidedModelPreviewComboBox.SelectedItem = resolved.FirstOrDefault();
            GuidedModelPreviewComboBox.IsEnabled = resolved.Length > 0;
            GuidedPreviewOnModelButton.IsEnabled = resolved.Length > 0 &&
                                                   _umodelBridge is not null &&
                                                   _guidedTexturePackageBuildService is not null;
            GuidedBuildStatusText.Text = resolved.Length > 0
                ? $"Found {resolved.Length:N0} verified linked model preview option(s) for {asset.ObjectName}. Choose Preview on model; the game files will not be changed."
                : $"Arkive found the verified source reference for {asset.ObjectName}, but its mesh is absent from the current package index. Build/update the library index and try again.";
            Log(
                $"Guided model-preview candidates resolved: projectId={asset.Entry.Manifest.ProjectId}, " +
                $"object={asset.ObjectName}, references={references.Count}, candidates={resolved.Length}, " +
                $"models={string.Join(",", resolved.Select(item => item.Match.PackageAlias + "." + item.Match.ObjectName))}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log(
                $"Guided model-preview candidate resolution failed: projectId={asset.Entry.Manifest.ProjectId}, " +
                $"object={asset.ObjectName}, error={exception}");
            GuidedBuildStatusText.Text = $"Linked-model lookup needs attention: {exception.Message}";
        }
    }

    private void GuidedModelPreviewComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateGuidedTextureModActionState();

    private async void GuidedPreviewOnModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress || _umodelBridge is null ||
            _guidedTexturePackageBuildService is null ||
            GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow package ||
            GuidedTextureModsGrid.SelectedItem is not GuidedTextureModRow asset ||
            GuidedModelPreviewComboBox.SelectedItem is not GuidedModelPreviewCandidate model) return;

        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        var sessionRoot = Path.Combine(
            _projectRoot,
            "staging",
            "model-previews",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var temporaryBuildRoot = Path.Combine(sessionRoot, "package-build");
        string? disposableBuildRoot = null;
        try
        {
            Directory.CreateDirectory(sessionRoot);
            var editableHash = await HashFileForPreviewAsync(asset.Entry.Manifest.EditableImagePath);
            var reusable = package.LatestRevision is { } latest &&
                           File.Exists(latest.OutputUpkPath) &&
                           latest.Assets.Any(item =>
                               item.ProjectId.Equals(asset.Entry.Manifest.ProjectId,
                                   StringComparison.OrdinalIgnoreCase) &&
                               item.EditedImageSha256.Equals(editableHash,
                                   StringComparison.OrdinalIgnoreCase));
            string replacementPath;
            if (reusable)
            {
                replacementPath = await ResolveVerifiedGuidedOutputAsync(package.LatestRevision!);
                GuidedBuildStatusText.Text = "Using the current verified modified UPK for the model preview...";
            }
            else
            {
                disposableBuildRoot = temporaryBuildRoot;
                var totalSteps = 10;
                GuidedBuildProgressBar.Maximum = totalSteps;
                GuidedBuildProgressBar.Value = 0;
                var progress = new Progress<GuidedTexturePackageBuildProgress>(update =>
                {
                    GuidedBuildProgressBar.Value = update.Step;
                    GuidedBuildStatusText.Text =
                        $"Preparing model preview {update.Step} of {update.TotalSteps} · {update.Message}";
                    Log(
                        $"Guided model-preview build progress: projectId={asset.Entry.Manifest.ProjectId}, " +
                        $"step={update.Step}/{update.TotalSteps}, message={update.Message}");
                });
                Log(
                    $"Guided model-preview temporary build started: projectId={asset.Entry.Manifest.ProjectId}, " +
                    $"texture={asset.ObjectName}, model={model.Match.PackageAlias}.{model.Match.ObjectName}, " +
                    $"root={temporaryBuildRoot}.");
                var built = await _guidedTexturePackageBuildService.BuildAsync(
                    package.Entry,
                    [asset.Entry],
                    temporaryBuildRoot,
                    Path.Combine(_projectRoot, "backups", "packages"),
                    progress);
                replacementPath = built.Revision.OutputUpkPath;
            }

            var target = new UmodelAssetEntry(
                model.Match.ExportIndex,
                model.Match.SerialOffset,
                model.Match.SerialSize,
                model.Match.ClassName,
                model.Match.ObjectName);
            GuidedBuildStatusText.Text = "Tracing the model's actual UModel package dependencies...";
            var packageRoot = Path.GetDirectoryName(model.Match.SourcePath)!;
            var dependencyProbe = await _umodelBridge.ProbeAssetDependenciesAsync(
                model.Match.SourcePath,
                target,
                packageRoot);
            GuidedBuildStatusText.Text =
                $"Staging an isolated {dependencyProbe.PhysicalPackageNames.Count:N0}-package preview shadow...";
            var shadow = await UmodelPackageShadowBuilder.BuildAsync(
                packageRoot,
                model.Match.SourcePath,
                dependencyProbe.PhysicalPackageNames,
                replacementPath,
                Path.GetFileName(package.Entry.Manifest.SourcePackagePath),
                Path.Combine(sessionRoot, "package-shadow"));
            var process = _umodelBridge.StartAssetPreviewInPackageRoot(
                shadow.ModelPackagePath,
                target,
                shadow.ShadowDirectory,
                Path.Combine(sessionRoot, "umodel-model-preview.log"));
            var cleanupPaths = new[] { shadow.ShadowDirectory, disposableBuildRoot }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToArray();
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _ = CleanupModelPreviewDirectoriesAsync(cleanupPaths);
            if (process.HasExited) _ = CleanupModelPreviewDirectoriesAsync(cleanupPaths);

            GuidedBuildStatusText.Text =
                $"Opened {model.Match.ObjectName} in UModel with the current {asset.ObjectName} edit overlaid. Close UModel when finished.";
            StatusText.Text = $"Previewing {asset.ObjectName} on {model.Match.ObjectName}.";
            Log(
                $"Guided texture-on-model preview launched: projectId={asset.Entry.Manifest.ProjectId}, " +
                $"process={process.Id}, texture={asset.ObjectName}, replacement={replacementPath}, " +
                $"modelPackage={model.Match.SourcePath}, model={model.Match.ObjectName}, " +
                $"sourceReference={model.Reference.SourceAssetReference}, session={sessionRoot}, " +
                $"dependencyPackages={dependencyProbe.PhysicalPackageNames.Count}, " +
                $"dependencyNames={string.Join(",", dependencyProbe.PhysicalPackageNames)}, " +
                $"shadowBytes={shadow.CopiedBytes}, shadowReplacement={shadow.ReplacementPackagePath}, " +
                $"temporaryBuild={disposableBuildRoot is not null}, gameFilesChanged=false.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            if (disposableBuildRoot is not null)
            {
                await CleanupModelPreviewDirectoriesAsync([disposableBuildRoot]);
            }
            await CleanupModelPreviewDirectoriesAsync([Path.Combine(sessionRoot, "package-shadow")]);
            Log(
                $"Guided texture-on-model preview failed: project={asset.Entry.ManifestPath}, " +
                $"model={model.Match.SourcePath}:{model.Match.ObjectName}, session={sessionRoot}, error={exception}");
            MessageBox.Show(this, exception.Message, "Preview texture on model", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
        }
    }

    private static bool PreviewPackageMatches(string candidate, string expected)
    {
        var left = Path.GetFileNameWithoutExtension(candidate).ToUpperInvariant();
        var right = Path.GetFileNameWithoutExtension(expected).ToUpperInvariant();
        return left.Equals(right, StringComparison.Ordinal) ||
               left.EndsWith("_" + right, StringComparison.Ordinal) ||
               right.EndsWith("_" + left, StringComparison.Ordinal);
    }

    private static bool PreviewObjectMatches(string candidate, string expected) =>
        candidate.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(expected + "_", StringComparison.OrdinalIgnoreCase) ||
        expected.StartsWith(candidate + "_", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> HashFileForPreviewAsync(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ResolveVerifiedGuidedOutputAsync(
        GuidedTexturePackageRevision revision)
    {
        string verifiedStagingPath;
        if (!string.IsNullOrWhiteSpace(revision.CompositionManifestPath) &&
            File.Exists(revision.CompositionManifestPath))
        {
            var verification = await UnifiedPackageComposerService.VerifyOutputAsync(
                revision.CompositionManifestPath);
            verifiedStagingPath = verification.OutputUpkPath;
        }
        else
        {
            var legacy = await ReplacementUpkBuildService.VerifyOutputAsync(
                revision.FinalReplacementUpkBuildManifestPath);
            verifiedStagingPath = legacy.OutputUpkPath;
        }

        // The internal staging UPK deliberately has a generic filename. Lost Ark's
        // UModel fork uses the physical hashed filename while resolving cross-package
        // materials, so previewing that staging path can show false missing/incorrect
        // textures. The bundle payload is byte-identical to the verified staging UPK
        // and retains the source physical filename; require and return that copy.
        var bundlePath = Path.GetFullPath(revision.ModBundleManifestPath);
        var bundle = await ArkiveModBundleService.LoadBundleAsync(bundlePath);
        if (Path.IsPathRooted(bundle.PayloadRelativePath))
            throw new InvalidDataException("The modified-UPK payload path is not relative to its bundle.");
        var bundleDirectory = Path.GetDirectoryName(bundlePath) ??
                              throw new InvalidDataException("The modified-UPK bundle directory is unavailable.");
        var payloadPath = Path.GetFullPath(Path.Combine(bundleDirectory, bundle.PayloadRelativePath));
        var prefix = Path.GetFullPath(bundleDirectory).TrimEnd(Path.DirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        if (!payloadPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The modified-UPK payload escapes its bundle directory.");
        if (!File.Exists(payloadPath))
            throw new FileNotFoundException("The physical-name-safe modified-UPK preview payload was not found.", payloadPath);
        var payloadInfo = new FileInfo(payloadPath);
        var payloadHash = await HashFileForPreviewAsync(payloadPath);
        var stagingHash = await HashFileForPreviewAsync(verifiedStagingPath);
        if (!Path.GetFileName(payloadPath).Equals(
                bundle.SourcePhysicalPackageName, StringComparison.OrdinalIgnoreCase) ||
            payloadInfo.Length != bundle.PayloadBytes ||
            !payloadHash.Equals(bundle.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            !payloadHash.Equals(stagingHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The physical-name-safe preview payload no longer matches the verified modified UPK.");
        return payloadPath;
    }

    private static async Task CleanupModelPreviewDirectoriesAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    break;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1));
                }
            }
        }
    }

    private async void GuidedResetAssetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress ||
            GuidedTextureModsGrid.SelectedItem is not GuidedTextureModRow asset) return;
        if (MessageBox.Show(
                this,
                $"Reset '{asset.ObjectName}' to Arkive's untouched exported image?\n\n" +
                "This changes only the editable project. Rebuild the UPK afterward to apply the reset.",
                "Reset editable texture",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        try
        {
            var native = await NativeTextureExportService.LoadManifestAsync(
                asset.Entry.Manifest.NativeTextureManifestPath);
            if (string.IsNullOrWhiteSpace(native.EditImageRelativePath))
            {
                throw new NotSupportedException("The native export has no original editable image.");
            }
            var nativeDirectory = Path.GetDirectoryName(asset.Entry.Manifest.NativeTextureManifestPath)!;
            var originalImage = Path.GetFullPath(Path.Combine(nativeDirectory, native.EditImageRelativePath));
            var relativeOriginal = Path.GetRelativePath(nativeDirectory, originalImage);
            if (Path.IsPathRooted(relativeOriginal) || relativeOriginal == ".." ||
                relativeOriginal.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new IOException("The original editable image path escapes its native export session.");
            }
            await _guidedTextureModWorkspace.ResetEditableAsync(asset.Entry.ManifestPath, originalImage);
            Log(
                $"Guided editable texture reset: projectId={asset.Entry.Manifest.ProjectId}, " +
                $"object={asset.ObjectName}, original={originalImage}, editable={asset.EditableImage}.");
            await RefreshGuidedTextureModsAsync("editable texture reset", asset.Entry.Manifest.ProjectId);
            GuidedBuildStatusText.Text =
                "Editable image reset to the original export. Rebuild the checked assets to update the modified UPK.";
            StatusText.Text = $"Reset {asset.ObjectName} to its original editable image.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log($"Guided editable texture reset failed: project={asset.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Reset editable texture", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
        }
    }

    private void GuidedOpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (GuidedTextureModsGrid.SelectedItem is GuidedTextureModRow row)
        {
            OpenFolder(row.Entry.DirectoryPath, "guided texture-mod project");
        }
    }

    private async void GuidedDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress) return;
        var rows = GuidedTextureModsGrid.SelectedItems
            .OfType<GuidedTextureModRow>()
            .ToArray();
        if (rows.Length == 0 && GuidedTextureModsGrid.SelectedItem is GuidedTextureModRow fallback)
        {
            rows = [fallback];
        }
        if (rows.Length == 0) return;
        var owned = rows.Where(row => row.OwnerModName is not null).ToArray();
        if (owned.Length > 0)
        {
            MessageBox.Show(this,
                "These texture projects belong to .arkive mods and cannot be deleted here:\n\n" +
                string.Join("\n", owned.Take(12).Select(row => $"  • {row.ObjectName} — {row.OwnerModName}")) +
                "\n\nRemove the asset from its mod (Mod library > Assets) or remove the mod instead.",
                "Delete texture projects", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var package = GuidedPackagesGrid.SelectedItem as GuidedTexturePackageRow;
        if (package?.LatestRevision is { } latest &&
            latest.Assets.Any(built => rows.Any(row => built.ProjectId.Equals(
                row.Entry.Manifest.ProjectId, StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                var inspection = await _modDeploymentService.InspectAsync(
                    CreateModDeploymentRequest(latest.ModBundleManifestPath));
                var receiptConfirmsRestored = inspection.Receipt is
                {
                    State: ModInstallationState.Restored,
                    GameFilesModified: false,
                };
                if (!receiptConfirmsRestored &&
                    (inspection.CanRestore ||
                     inspection.State is ModInstallationState.Installed or
                         ModInstallationState.Prepared or ModInstallationState.Conflict))
                {
                    MessageBox.Show(
                        this,
                        "Restore the original game package before deleting this project. Arkive will not remove the project or its restore information while its mod may still be installed.",
                        "Restore before deleting",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                if (receiptConfirmsRestored && inspection.State == ModInstallationState.Conflict)
                {
                    var restoredReceipt = inspection.Receipt!;
                    Log(
                        $"Guided project deletion accepted a layered-restore conflict: " +
                        $"projects={rows.Length}, receipt={restoredReceipt.InstallationId}, " +
                        $"receiptState={restoredReceipt.State}, currentTargetSha={inspection.CurrentTargetSha256}. " +
                        "This project previously completed its restore and no longer owns the installed target.");
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Log(
                    $"Guided project deletion install-state preflight failed: " +
                    $"projects={rows.Length}, package={package.Entry.ManifestPath}, error={exception}");
                MessageBox.Show(
                    this,
                    "Arkive could not prove that this mod is safely uninstalled, so the project was not deleted. " +
                    "Review Diagnostics or restore the original package first.\n\n" + exception.Message,
                    "Deletion safety check failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        var names = string.Join("\n", rows.Take(10).Select(row => $"  • {row.ObjectName}"));
        if (rows.Length > 10) names += $"\n  • …and {rows.Length - 10:N0} more";
        var revisionCount = rows.Sum(row => row.RevisionCount);
        var confirmation = MessageBox.Show(
            this,
            $"Delete {rows.Length:N0} selected texture project(s) from Modified UPKs?\n\n" +
            $"{names}\n\n" +
            $"Legacy single-asset build revisions: {revisionCount:N0}\n\n" +
            "The restored game package and Arkive backups will not be changed. Advanced diagnostic exports are retained. " +
            "The latest package build is retained but must be rebuilt before it reflects this removal.",
            "Delete selected texture projects",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            Log($"Guided multi-project deletion cancelled: projects={rows.Length}.");
            return;
        }

        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        try
        {
            Log($"Guided multi-project deletion started: projects={rows.Length}, " +
                $"objects={string.Join(",", rows.Select(row => row.ObjectName))}, revisions={revisionCount}.");
            foreach (var row in rows)
            {
                await _guidedTextureModWorkspace.DeleteAsync(row.Entry);
                Log($"Guided texture project deleted: projectId={row.Entry.Manifest.ProjectId}, " +
                    $"object={row.ObjectName}, manifest={row.Entry.ManifestPath}.");
            }
            await RefreshGuidedTextureModsAsync("guided projects deleted");
            GuidedBuildProgressBar.Value = 0;
            GuidedBuildStatusText.Text =
                $"Deleted {rows.Length:N0} texture project(s). Select any texture and choose Start texture mod for a fresh copy.";
            StatusText.Text = $"Deleted {rows.Length:N0} texture-mod project(s).";
            Log($"Guided multi-project deletion completed: projects={rows.Length}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = "The texture-mod project could not be deleted.";
            Log($"Guided multi-project deletion failed: projects={rows.Length}, error={exception}");
            MessageBox.Show(this, exception.Message, "Delete texture-mod projects", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
        }
    }

    private async void GuidedCleanBuildsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress ||
            GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow package ||
            package.Entry.Manifest.Revisions.Count <= 1) return;

        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(package.Entry.Manifest.LatestRevisionId))
        {
            retained.Add(package.Entry.Manifest.LatestRevisionId);
        }
        var removable = new List<GuidedTexturePackageRevision>();
        foreach (var revision in package.Entry.Manifest.Revisions)
        {
            if (retained.Contains(revision.RevisionId)) continue;
            try
            {
                var inspection = await _modDeploymentService.InspectForRecoveryAsync(
                    CreateModDeploymentRequest(revision.ModBundleManifestPath));
                var safe = inspection.State is ModInstallationState.NotInstalled or ModInstallationState.Restored &&
                           !inspection.CanRestore &&
                           inspection.Receipt is not { GameFilesModified: true };
                if (safe) removable.Add(revision);
                else retained.Add(revision.RevisionId);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                retained.Add(revision.RevisionId);
                Log($"Guided old-build cleanup retained revision after inconclusive deployment inspection: packageId={package.Entry.Manifest.PackageProjectId}, revision={revision.RevisionId}, error={exception.Message}.");
            }
        }
        if (removable.Count == 0)
        {
            MessageBox.Show(this, "No old build can be safely removed. Arkive retained the latest build and every revision with active or uncertain restore state.", "Clean old builds", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var bytes = removable.Sum(revision => GetDirectorySizeSafe(Path.GetDirectoryName(revision.OutputUpkPath)!));
        if (MessageBox.Show(
                this,
                $"Remove {removable.Count:N0} superseded build(s) ({FormatBytes(bytes)})?\n\n" +
                "The latest build, installed revisions, restore data, editable textures, and game backups are retained.",
                "Clean old builds",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            await _guidedTexturePackageWorkspace.PruneRevisionsAsync(package.Entry.ManifestPath, retained);
            Log($"Guided old-build cleanup completed: packageId={package.Entry.Manifest.PackageProjectId}, removed={removable.Count}, retained={retained.Count}, estimatedBytes={bytes}.");
            await RefreshGuidedTextureModsAsync("old builds cleaned", package.Assets.FirstOrDefault()?.Entry.Manifest.ProjectId);
            StatusText.Text = $"Removed {removable.Count:N0} old build(s); freed approximately {FormatBytes(bytes)}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log($"Guided old-build cleanup failed: package={package.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Clean old builds", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void GuidedCleanPreviewsButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await CleanupGuidedPreviewCacheAsync(TimeSpan.FromMinutes(15), interactive: true);
        StatusText.Text = result.Count == 0
            ? "No abandoned model-preview cache was found."
            : $"Removed {result.Count:N0} abandoned preview session(s); freed {FormatBytes(result.Bytes)}.";
    }

    private async Task<(int Count, long Bytes)> CleanupGuidedPreviewCacheAsync(
        TimeSpan minimumAge,
        bool interactive)
    {
        var root = Path.GetFullPath(Path.Combine(_projectRoot, "staging", "model-previews"));
        if (!Directory.Exists(root)) return (0, 0);
        var cutoff = DateTime.UtcNow - minimumAge;
        var candidates = Directory.EnumerateDirectories(root)
            .Where(path => Directory.GetLastWriteTimeUtc(path) < cutoff)
            .ToArray();
        if (candidates.Length == 0) return (0, 0);
        var bytes = candidates.Sum(GetDirectorySizeSafe);
        if (interactive && MessageBox.Show(
                this,
                $"Remove {candidates.Length:N0} abandoned preview session(s) ({FormatBytes(bytes)})?\n\n" +
                "Only disposable UModel shadows and temporary preview builds older than 15 minutes are affected.",
                "Clean preview cache",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return (0, 0);
        var removed = 0;
        long removedBytes = 0;
        foreach (var path in candidates)
        {
            try
            {
                var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
                if (Path.IsPathRooted(relative) || relative == ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                var size = GetDirectorySizeSafe(path);
                Directory.Delete(path, recursive: true);
                removed++;
                removedBytes += size;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log($"Guided preview-cache entry retained because it is active or inaccessible: path={path}, error={exception.Message}.");
            }
        }
        if (removed > 0)
        {
            Log($"Guided preview-cache cleanup completed: automatic={!interactive}, removed={removed}, bytes={removedBytes}, minimumAge={minimumAge}.");
        }
        return (removed, removedBytes);
    }

    private void GuidedCopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow package) return;
        var latest = package.LatestRevision;
        var inspection = package.Inspection ?? _guidedDeploymentInspection;
        var assets = string.Join(
            Environment.NewLine,
            package.Assets.Select(asset =>
                $"  [Texture {(asset.Included ? "x" : " ")}] {asset.ObjectName} | {asset.State} | {asset.ImpactNames}")
                .Concat(package.MaterialAssets.Select(asset =>
                    $"  [Material {(asset.Included ? "x" : " ")}] {asset.ObjectName} | {asset.State} | {asset.Parameters}"))
                .Concat(package.MeshAssets.Select(asset =>
                    $"  [SkeletalMesh {(asset.Included ? "x" : " ")}] {asset.ObjectName} | {asset.State} | {asset.DisplayName}"))
                .Concat(package.StaticMeshAssets.Select(asset =>
                    $"  [StaticMesh {(asset.Included ? "x" : " ")}] {asset.ObjectName} | {asset.State} | {asset.Collision}")));
        var report =
            $"Arkive modified-UPK diagnostics{Environment.NewLine}" +
            $"Mod name: {package.DisplayName}{Environment.NewLine}" +
            $"Notes: {package.Entry.Manifest.Notes ?? "-"}{Environment.NewLine}" +
            $"Package project: {package.Entry.Manifest.PackageProjectId}{Environment.NewLine}" +
            $"Source: {package.Entry.Manifest.SourcePackagePath}{Environment.NewLine}" +
            $"State: {package.State}{Environment.NewLine}" +
            $"Conflict: {package.Conflict}{Environment.NewLine}" +
            $"Latest revision: {latest?.RevisionId ?? "none"}{Environment.NewLine}" +
            $"Composition: {latest?.CompositionManifestPath ?? "legacy"}{Environment.NewLine}" +
            $"Variable-size deployment: contains={latest?.ContainsVariableSizeMeshReplacements.ToString() ?? "-"} | approved={latest?.VariableSizeDeploymentApproved.ToString() ?? "-"} | gate={latest?.VariableSizeDeploymentGateManifestPath ?? "none"}{Environment.NewLine}" +
            $"Output: {latest?.OutputUpkPath ?? "none"}{Environment.NewLine}" +
            $"Bundle: {latest?.ModBundleManifestPath ?? "none"}{Environment.NewLine}" +
            $"Deployment: {inspection?.State.ToString() ?? "unavailable"} | install={inspection?.CanInstall.ToString() ?? "-"} | restore={inspection?.CanRestore.ToString() ?? "-"}{Environment.NewLine}" +
            $"Target: {inspection?.TargetPath ?? "unavailable"}{Environment.NewLine}" +
            $"Backup: {inspection?.BackupPath ?? "unavailable"}{Environment.NewLine}" +
            $"Global client impact: {package.ImpactSummary}{Environment.NewLine}" +
            $"Assets:{Environment.NewLine}{assets}{Environment.NewLine}" +
            $"Session log: {_sessionLog.FilePath}";
        CopyText(report, "modified-UPK diagnostics");
        Log($"Guided package diagnostics copied: packageId={package.Entry.Manifest.PackageProjectId}, revision={latest?.RevisionId ?? "none"}, textures={package.Assets.Count}, materials={package.MaterialAssets.Count}, skeletalMeshes={package.MeshAssets.Count}, staticMeshes={package.StaticMeshAssets.Count}.");
    }

    private static long GetDirectorySizeSafe(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length)
                : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d:N2} GiB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024d:N2} MiB",
        >= 1024L => $"{bytes / 1024d:N2} KiB",
        _ => $"{bytes:N0} B",
    };

    private async void GuidedDeletePackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress ||
            GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow package) return;
        var owningMods = package.AllAssets.Select(row => row.OwnerModName)
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (owningMods.Length > 0)
        {
            MessageBox.Show(this,
                "This UPK contains assets that belong to .arkive mods:\n\n" +
                string.Join("\n", owningMods.Select(name => $"  • {name}")) +
                "\n\nRemove those mods in Mod library first. Deleting the package project would remove their working copies.",
                "Delete modified UPK", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var revisionInspections = new List<(
            GuidedTexturePackageRevision Revision,
            ModDeploymentInspection Inspection)>();
        foreach (var revision in package.Entry.Manifest.Revisions.OrderByDescending(item => item.CreatedAt))
        {
            try
            {
                var inspection = await _modDeploymentService.InspectForDeletionIdentityAsync(
                    CreateModDeploymentRequest(revision.ModBundleManifestPath));
                revisionInspections.Add((revision, inspection));
                if (HasActiveDeployment(inspection))
                {
                    MessageBox.Show(
                        this,
                        $"Revision {revision.RevisionId} of this Package Project is still installed, even though its newest build is not.\n\n" +
                        "Select Restore original to recover its verified source package, then delete the modified UPK. " +
                        "Arkive will not remove the project or its restore evidence while any revision remains active.",
                        "Restore before deleting",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    Log($"Guided package deletion refused: packageId={package.Entry.Manifest.PackageProjectId}, " +
                        $"revision={revision.RevisionId}, state={inspection.State}, canRestore={inspection.CanRestore}.");
                    return;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Log($"Guided package deletion install-state preflight failed: " +
                    $"package={package.Entry.ManifestPath}, revision={revision.RevisionId}, error={exception}");
                MessageBox.Show(
                    this,
                    "Arkive could not prove that this modified UPK is safely uninstalled, so nothing was deleted.\n\n" +
                    exception.Message,
                    "Deletion safety check failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        var unresolved = revisionInspections.Where(item =>
                item.Inspection.State == ModInstallationState.Conflict &&
                item.Inspection.Receipt is not
                {
                    State: ModInstallationState.Restored,
                    GameFilesModified: false,
                })
            .ToArray();
        if (unresolved.Length > 0)
        {
            var payloadHashes = revisionInspections.Select(item => item.Inspection.Bundle.PayloadSha256)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var lineageRootProof = revisionInspections.FirstOrDefault(item =>
                !payloadHashes.Contains(item.Inspection.Bundle.RequiredSourcePackageSha256) &&
                item.Inspection.State is ModInstallationState.NotInstalled or ModInstallationState.Restored &&
                item.Inspection.CurrentTargetSha256?.Equals(
                    item.Inspection.Bundle.RequiredSourcePackageSha256,
                    StringComparison.OrdinalIgnoreCase) == true);
            if (lineageRootProof.Inspection is null)
            {
                var conflict = unresolved[0];
                MessageBox.Show(
                    this,
                    $"Revision {conflict.Revision.RevisionId} has an unresolved game-package identity conflict. " +
                    "Arkive cannot safely delete its restore evidence until the package is repaired or restored.",
                    "Deletion safety check failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Log($"Guided package deletion refused by unresolved identity: packageId={package.Entry.Manifest.PackageProjectId}, " +
                    $"revision={conflict.Revision.RevisionId}, targetSha={conflict.Inspection.CurrentTargetSha256}.");
                return;
            }

            Log(
                $"Guided package deletion accepted clean lineage root across abandoned descendants: " +
                $"packageId={package.Entry.Manifest.PackageProjectId}, " +
                $"rootRevision={lineageRootProof.Revision.RevisionId}, " +
                $"rootSha={lineageRootProof.Inspection.CurrentTargetSha256}, " +
                $"ignoredDescendants={string.Join(",", unresolved.Select(item => item.Revision.RevisionId))}.");
        }

        foreach (var mesh in package.MeshAssets)
        {
            var legacyBundle = Path.Combine(
                mesh.Entry.DirectoryPath, "deployment-bundle",
                ArkiveModBundleService.BundleManifestFileName);
            if (!File.Exists(legacyBundle)) continue;
            try
            {
                var inspection = await _modDeploymentService.InspectForDeletionIdentityAsync(
                    CreateModDeploymentRequest(legacyBundle));
                var receiptConfirmsRestored = inspection.Receipt is
                {
                    State: ModInstallationState.Restored,
                    GameFilesModified: false,
                };
                if (!receiptConfirmsRestored &&
                    (inspection.CanRestore || inspection.State is ModInstallationState.Installed or
                         ModInstallationState.Prepared or ModInstallationState.Conflict))
                {
                    MessageBox.Show(this,
                        $"Restore the standalone mesh bundle for {mesh.ObjectName} before deleting this package project.",
                        "Restore before deleting", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException or
                    ArgumentException)
            {
                Log($"Guided package mesh deletion preflight failed: edit={mesh.Entry.ManifestPath}, error={exception}");
                MessageBox.Show(this,
                    "Arkive could not prove that a legacy mesh bundle is safely uninstalled, so nothing was deleted.\n\n" +
                    exception.Message,
                    "Deletion safety check failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var confirmation = MessageBox.Show(
            this,
            $"Permanently delete the modified-UPK project '{package.PackageAlias}'?\n\n" +
            $"This deletes all {package.Assets.Count:N0} contained texture project(s), " +
            $"{package.MaterialAssets.Count:N0} material project(s), {package.MeshAssets.Count:N0} edited skeletal-mesh project(s), " +
            $"{package.StaticMeshAssets.Count:N0} edited StaticMesh project(s), their editable files, " +
            $"and {package.Entry.Manifest.Revisions.Count:N0} staged package revision(s).\n\n" +
            "The clean game package, backups, and installation receipts are retained.",
            "Delete modified UPK",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            Log($"Guided package deletion cancelled: packageId={package.Entry.Manifest.PackageProjectId}.");
            return;
        }

        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        try
        {
            Log($"Guided package deletion started: packageId={package.Entry.Manifest.PackageProjectId}, " +
                $"package={package.Entry.Manifest.SourcePackagePath}, textures={package.Assets.Count}, " +
                $"materials={package.MaterialAssets.Count}, skeletalMeshes={package.MeshAssets.Count}, staticMeshes={package.StaticMeshAssets.Count}, " +
                $"revisions={package.Entry.Manifest.Revisions.Count}.");

            var exportSnapshot = await _exportWorkspaceStore.LoadAsync();
            var legacyBundlePaths = exportSnapshot.Entries
                .Where(entry => Path.GetFullPath(entry.Manifest.SourcePackagePath).Equals(
                    Path.GetFullPath(package.Entry.Manifest.SourcePackagePath),
                    StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Manifest.ModBundleManifestPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToArray();
            await _guidedTextureModWorkspace.SuppressLegacyImportsAsync(legacyBundlePaths);

            foreach (var asset in package.Assets)
            {
                await _guidedTextureModWorkspace.DeleteAsync(asset.Entry);
            }
            foreach (var mesh in package.MeshAssets)
            {
                await _skeletalMeshEditWorkspaceStore.DeleteAsync(mesh.Entry);
            }
            foreach (var material in package.MaterialAssets)
                await _materialEditWorkspaceStore.DeleteAsync(material.Entry);
            foreach (var mesh in package.StaticMeshAssets)
                await _staticMeshEditWorkspaceStore.DeleteAsync(mesh.Entry);
            await _guidedTexturePackageWorkspace.DeleteAsync(package.Entry);
            await RefreshMaterialProjectsAsync("guided package project deleted");
            await RefreshStaticMeshWorkspaceAsync("guided package project deleted");
            await RefreshGuidedTextureModsAsync("guided package project deleted");
            GuidedBuildProgressBar.Value = 0;
            GuidedBuildStatusText.Text =
                "Modified UPK deleted. Start a texture or mesh mod to create a fresh package project.";
            StatusText.Text = $"Deleted the modified-UPK project for {package.PackageAlias}.";
            Log($"Guided package deletion completed: packageId={package.Entry.Manifest.PackageProjectId}, " +
                $"textures={package.Assets.Count}, materials={package.MaterialAssets.Count}, skeletalMeshes={package.MeshAssets.Count}, staticMeshes={package.StaticMeshAssets.Count}, " +
                $"suppressedLegacyImports={legacyBundlePaths.Length}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            StatusText.Text = "The modified-UPK project could not be deleted.";
            Log($"Guided package deletion failed: package={package.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Delete modified UPK", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
        }
    }

}
