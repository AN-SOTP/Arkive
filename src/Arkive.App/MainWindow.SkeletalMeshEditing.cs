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
    private void DisplaySkeletalMeshReconnaissance(SkeletalMeshReconnaissanceEntry entry)
    {
        var report = entry.Report;
        var oracle = report.Oracle;
        var native = report.NativeLayout;
        SkeletalMeshReconSummaryText.Text = native is null
            ? $"{report.PackageAlias}.{report.ObjectName} | {report.Readiness} | native layout unresolved"
            : $"{report.PackageAlias}.{report.ObjectName} | {report.Readiness} | {native.Lods.Count} native LODs | {native.BoneCount:N0} bones | {native.Ranges.Count:N0} verified ranges | {(oracle is null ? "UModel unavailable" : "UModel checked")}";
        SkeletalMeshReconPathText.Text = entry.JsonPath;
        SkeletalMeshPropertiesGrid.ItemsSource = report.Properties.Select(property =>
            new SkeletalMeshPropertyRow(property)).ToArray();
        SkeletalMeshLodsGrid.ItemsSource = oracle?.Lods.SelectMany(lod =>
            lod.Sections.Count == 0
                ? new[] { new SkeletalMeshLodRow(lod, null) }
                : lod.Sections.Select(section => new SkeletalMeshLodRow(lod, section))).ToArray() ?? [];
        SkeletalMeshNativeLodsGrid.ItemsSource = native?.Lods.Select(lod =>
            new SkeletalMeshNativeLodRow(lod)).ToArray() ?? [];
        SkeletalMeshNativeRangesGrid.ItemsSource = native?.Ranges.Select(range =>
            new SkeletalMeshNativeRangeRow(range)).ToArray() ?? [];
        SkeletalMeshBonesGrid.ItemsSource = native?.Bones.Select(bone =>
            new SkeletalMeshBoneRow(bone)).ToArray() ?? [];
        var dependencies = report.DeformationDependencies;
        SkeletalMeshDependenciesGrid.ItemsSource = SkeletalMeshDependencyRow.Build(dependencies);
        SkeletalMeshDependencySummaryText.Text = dependencies is null
            ? "Dependency evidence is unavailable. Rerun Native mesh recon before importing changed topology."
            : $"Cloth: {dependencies.ClothState} ({dependencies.ActiveClothReferenceCount:N0} active refs). " +
              $"Morphs: {dependencies.MorphState} ({dependencies.MorphTargetSetCount:N0} sets, " +
              $"{dependencies.MorphTargetCount:N0} targets, {dependencies.MorphDeltaCount:N0} deltas). " +
              $"Same-topology edits: {(dependencies.TopologyPreservingSupported ? "supported" : "refused")}. " +
              $"Changed topology: {(dependencies.TopologyChangingSupported ? "supported" : "refused")} — " +
              dependencies.TopologyChangingReason;
        if (dependencies is not null)
            Log($"Mesh dependency audit: {report.PackageAlias}.{report.ObjectName}; " +
                $"cloth={dependencies.ClothState}/{dependencies.ActiveClothReferenceCount}, " +
                $"morph={dependencies.MorphState}/{dependencies.MorphTargetCount} targets/{dependencies.MorphDeltaCount} deltas, " +
                $"sameTopology={dependencies.TopologyPreservingSupported}, changedTopology={dependencies.TopologyChangingSupported}.");
        SkeletalMeshIssuesGrid.ItemsSource = report.Issues;
        OpenSkeletalMeshReconFolderButton.IsEnabled = true;
        CopySkeletalMeshReconPathButton.IsEnabled = true;
        UpdateSkeletalMeshReconnaissanceActionState();
        SkeletalMeshReconTab.IsSelected = true;
    }

    private void OpenSkeletalMeshReconFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSkeletalMeshReconnaissance is not null)
            OpenFolder(_currentSkeletalMeshReconnaissance.DirectoryPath, "SkeletalMesh reconnaissance report");
    }

    private void CopySkeletalMeshReconPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSkeletalMeshReconnaissance is not null)
            CopyText(_currentSkeletalMeshReconnaissance.JsonPath, "SkeletalMesh reconnaissance JSON path");
    }

    private async void BuildSkeletalMeshNoOpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSkeletalMeshReconnaissance is not { } entry || _skeletalMeshNoOpInProgress) return;
        _skeletalMeshNoOpInProgress = true;
        UpdateSkeletalMeshReconnaissanceActionState();
        StatusText.Text = $"Building byte-identical SkeletalMesh no-op proof for {entry.Report.ObjectName}...";
        Log($"SkeletalMesh no-op reconstruction started: recon={entry.JsonPath}, sourceExportSha={entry.Report.ExportPayloadSha256}.");
        try
        {
            var output = Path.Combine(entry.DirectoryPath, "no-op-proof");
            var result = await _skeletalMeshNoOpService.ReconstructAsync(
                new SkeletalMeshNoOpRequest(entry.Report, entry.JsonPath, output));
            Log(
                $"SkeletalMesh no-op reconstruction completed in {result.Duration.TotalSeconds:N3}s: " +
                $"byteIdentical={result.Manifest.ByteIdentical}, propertyAndPrefixBytes={result.Manifest.PropertyAndPrefixBytes:N0}, " +
                $"nativeBytes={result.Manifest.NativeDataBytes:N0}, sourceSha={result.Manifest.SourceExportSha256}, " +
                $"rebuiltSha={result.Manifest.ReconstructedExportSha256}, firstDifference={result.Manifest.FirstDifferenceOffset}, " +
                $"manifest={result.ManifestPath}, export={result.ExportPath}.");
            StatusText.Text = $"Byte-identical SkeletalMesh no-op proof passed for {entry.Report.ObjectName}.";
            MessageBox.Show(this,
                $"Byte-identical no-op proof passed.\n\nExport SHA-256:\n{result.Manifest.ReconstructedExportSha256}\n\n{result.ManifestPath}",
                "Arkive SkeletalMesh no-op proof", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is SkeletalMeshNoOpRefusalException or PackageAccessException or
                PackageFormatException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "SkeletalMesh no-op proof failed; see Diagnostics.";
            Log($"SkeletalMesh no-op reconstruction failed: recon={entry.JsonPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive SkeletalMesh no-op error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshNoOpInProgress = false;
            UpdateSkeletalMeshReconnaissanceActionState();
        }
    }

    private async void ExportEditableSkeletalMeshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSkeletalMeshReconnaissance is not { } entry ||
            _skeletalMeshEditableExportInProgress) return;
        _skeletalMeshEditableExportInProgress = true;
        UpdateSkeletalMeshReconnaissanceActionState();
        StatusText.Text = $"Decoding {entry.Report.ObjectName} into an editable GLB...";
        Log($"Editable SkeletalMesh export started: recon={entry.JsonPath}, " +
            $"sourceExportSha={entry.Report.ExportPayloadSha256}.");
        try
        {
            var output = Path.Combine(entry.DirectoryPath, "editable-glb",
                $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
            var result = await _skeletalMeshEditableExportService.ExportAsync(
                new SkeletalMeshEditableExportRequest(entry.Report, entry.JsonPath, output));
            _currentSkeletalMeshEditableExport = result;
            foreach (var lod in result.Manifest.Lods)
            {
                Log($"Editable SkeletalMesh LOD {lod.LodIndex}: vertices={lod.VertexCount:N0}, " +
                    $"indices={lod.IndexCount:N0}, sections={lod.SectionCount}, chunks={lod.ChunkCount}, " +
                    $"UVs={lod.TextureCoordinateCount}, stride={lod.VertexStride}, " +
                    $"positionOffset={lod.NativePositionOffset}, boneIndexBytes={lod.NativeBoneIndexWidth}, " +
                    $"reverseWinding={lod.WindingReversedForGltf}, faceNormalOpposed=" +
                    $"{lod.OpposedFaceNormalTriangleCount:N0}/{lod.FaceOrientationTriangleCount:N0} " +
                    $"({lod.OpposedFaceNormalPercent:N4}%), degenerate={lod.DegenerateTriangleCount:N0}, " +
                    $"positionSha={lod.DecodedPositionSha256}, " +
                    $"jointSha={lod.DecodedJointSha256}, stableIdSha={lod.StableVertexIdSha256}, " +
                    $"stableIdUv=TEXCOORD_{lod.StableVertexIdTextureCoordinate}, " +
                    $"stableIdUvSha={lod.StableVertexIdUvSha256}.");
            }
            Log($"Editable SkeletalMesh export completed in {result.Duration.TotalSeconds:N3}s: " +
                $"LODs={result.Manifest.Lods.Count}, bones={result.Manifest.BoneCount}, " +
                $"GLBbytes={result.Manifest.GlbBytes:N0}, inspectionErrors={result.Inspection.ErrorCount}, " +
                $"inspectionWarnings={result.Inspection.WarningCount}, glbSha={result.Manifest.GlbSha256}, " +
                $"glb={result.GlbPath}, manifest={result.ManifestPath}.");
            await RefreshSkeletalMeshEditableWorkspaceAsync(
                "editable SkeletalMesh export completed", result.Manifest.ExportId);
            StatusText.Text = $"Editable GLB passed round-trip validation for {entry.Report.ObjectName}.";
            MessageBox.Show(this,
                $"Editable GLB created and reopened successfully.\n\n" +
                $"{result.Manifest.Lods.Count} LOD(s) | {result.Manifest.BoneCount} bones | " +
                $"{result.Inspection.WarningCount} warning(s)\n\n{result.GlbPath}",
                "Arkive editable SkeletalMesh export", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is SkeletalMeshEditableExportRefusalException or PackageAccessException or
                PackageFormatException or InvalidDataException or IOException or
                UnauthorizedAccessException)
        {
            StatusText.Text = "Editable SkeletalMesh export failed; see Diagnostics.";
            Log($"Editable SkeletalMesh export failed: recon={entry.JsonPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive editable SkeletalMesh export error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshEditableExportInProgress = false;
            UpdateSkeletalMeshReconnaissanceActionState();
        }
    }

    private void OpenEditableSkeletalMeshFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var path = _currentSkeletalMeshEditableExport?.OutputDirectory ??
                   (SkeletalMeshEditableExportsGrid?.SelectedItem as SkeletalMeshEditableExportRow)
                   ?.Entry.DirectoryPath;
        if (!string.IsNullOrWhiteSpace(path))
            OpenFolder(path, "editable SkeletalMesh export");
    }

    private async void RefreshSkeletalMeshEditableExportsButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshSkeletalMeshEditableWorkspaceAsync("refresh button");

    private async Task RefreshSkeletalMeshEditableWorkspaceAsync(
        string trigger,
        string? selectExportId = null,
        string? selectEditId = null)
    {
        if (_skeletalMeshEditableWorkspaceBusy || SkeletalMeshEditableExportsGrid is not { } grid) return;
        _skeletalMeshEditableWorkspaceBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        try
        {
            var priorId = selectExportId ??
                          (grid.SelectedItem as SkeletalMeshEditableExportRow)
                          ?.Entry.Manifest.ExportId;
            var snapshot = await _skeletalMeshEditableWorkspaceStore.LoadAsync();
            var editedSnapshot = await _skeletalMeshEditWorkspaceStore.LoadAsync();
            _skeletalMeshEditableExports = snapshot.Entries
                .Select(entry => new SkeletalMeshEditableExportRow(entry))
                .ToArray();
            _skeletalMeshEditedGlbs = editedSnapshot.Entries;
            grid.ItemsSource = _skeletalMeshEditableExports;
            var selected = priorId is null
                ? _skeletalMeshEditableExports.FirstOrDefault()
                : _skeletalMeshEditableExports.FirstOrDefault(row =>
                    row.Entry.Manifest.ExportId.Equals(priorId, StringComparison.OrdinalIgnoreCase)) ??
                  _skeletalMeshEditableExports.FirstOrDefault();
            if (selected is not null)
            {
                grid.SelectedItem = selected;
                grid.ScrollIntoView(selected);
            }
            DisplayEditedSkeletalMeshesForSelectedExport(selectEditId);
            var totalBytes = snapshot.Entries
                .Where(entry => entry.GlbExists)
                .Sum(entry => new FileInfo(entry.GlbPath).Length);
            SkeletalMeshEditableWorkspaceSummaryText.Text =
                $"{snapshot.Entries.Count:N0} saved editable mesh export(s) | " +
                $"{editedSnapshot.Entries.Count:N0} registered edit(s) | " +
                $"{totalBytes / 1024d / 1024d:N2} MiB | " +
                $"{snapshot.Issues.Count + editedSnapshot.Issues.Count:N0} unreadable manifest(s)";
            Log(
                $"Editable SkeletalMesh workspace refreshed: trigger={trigger}, " +
                $"root={_skeletalMeshEditableWorkspaceStore.RootDirectory}, " +
                $"exports={snapshot.Entries.Count}, registeredEdits={editedSnapshot.Entries.Count}, " +
                $"bytes={totalBytes}, issues={snapshot.Issues.Count + editedSnapshot.Issues.Count}.");
            foreach (var issue in snapshot.Issues)
                Log($"Editable SkeletalMesh workspace issue: manifest={issue.ManifestPath}, error={issue.Message}");
            foreach (var issue in editedSnapshot.Issues)
                Log($"Edited SkeletalMesh workspace issue: manifest={issue.ManifestPath}, error={issue.Message}");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SkeletalMeshEditableWorkspaceSummaryText.Text = "Editable mesh exports could not be loaded.";
            Log($"Editable SkeletalMesh workspace refresh failed: trigger={trigger}, error={exception}");
            StatusText.Text = $"Could not load editable mesh exports: {exception.Message}";
        }
        finally
        {
            _skeletalMeshEditableWorkspaceBusy = false;
            UpdateSkeletalMeshEditableWorkspaceActionState();
            UpdateSkeletalMeshReconnaissanceActionState();
        }
    }

    private void SkeletalMeshEditableExportsGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        DisplayEditedSkeletalMeshesForSelectedExport();
        UpdateSkeletalMeshEditableWorkspaceActionState();
    }

    private void DisplayEditedSkeletalMeshesForSelectedExport(string? selectEditId = null)
    {
        if (SkeletalMeshEditedGlbsGrid is null || SkeletalMeshEditedGlbsSummaryText is null) return;
        if (SkeletalMeshEditableExportsGrid?.SelectedItem is not SkeletalMeshEditableExportRow source)
        {
            SkeletalMeshEditedGlbsGrid.ItemsSource = Array.Empty<SkeletalMeshEditedGlbRow>();
            SkeletalMeshEditedGlbsSummaryText.Text = "Select one clean export to view its registered edits.";
            return;
        }
        var rows = _skeletalMeshEditedGlbs
            .Where(entry => entry.Manifest.SourceExportId.Equals(
                source.Entry.Manifest.ExportId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Manifest.CreatedAt)
            .Select(entry => new SkeletalMeshEditedGlbRow(entry))
            .ToArray();
        SkeletalMeshEditedGlbsGrid.ItemsSource = rows;
        var selected = selectEditId is null
            ? rows.FirstOrDefault()
            : rows.FirstOrDefault(row => row.Entry.Manifest.EditId.Equals(
                selectEditId, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
        if (selected is not null)
        {
            SkeletalMeshEditedGlbsGrid.SelectedItem = selected;
            SkeletalMeshEditedGlbsGrid.ScrollIntoView(selected);
        }
        SkeletalMeshEditedGlbsSummaryText.Text = rows.Length == 0
            ? $"No edited GLBs registered for {source.PackageAlias}.{source.ObjectName}."
            : $"{rows.Length:N0} edited version(s) registered for {source.PackageAlias}.{source.ObjectName}";
    }

    private async void SkeletalMeshEditedGlbsGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _selectedSkeletalMeshDeploymentInspection = null;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        await RefreshSelectedSkeletalMeshDeploymentAsync("edited-mesh selection changed");
    }

    private void OpenSelectedSkeletalMeshEditableFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (SkeletalMeshEditableExportsGrid.SelectedItem is SkeletalMeshEditableExportRow row)
            OpenFolder(row.Entry.DirectoryPath, "saved editable SkeletalMesh export");
    }

    private void RevealSelectedSkeletalMeshGlbButton_Click(object sender, RoutedEventArgs e)
    {
        if (SkeletalMeshEditableExportsGrid.SelectedItem is SkeletalMeshEditableExportRow row)
            RevealFileInExplorer(row.Entry.GlbPath, "editable SkeletalMesh GLB");
    }

    private void CopySelectedSkeletalMeshGlbPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (SkeletalMeshEditableExportsGrid.SelectedItem is SkeletalMeshEditableExportRow row)
            CopyText(row.Entry.GlbPath, "editable SkeletalMesh GLB path");
    }

    private async void CompareSelectedSkeletalMeshSkeletonsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetRetargetPair(out var donor, out var target)) return;
        _skeletalMeshEditableWorkspaceBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        StatusText.Text = $"Comparing {donor.ObjectName} with target {target.ObjectName}...";
        try
        {
            var report = await _skeletalMeshSkeletonCompatibilityService.AnalyzeAsync(
                donor.Entry, target.Entry);
            LogSkeletonCompatibility("manual comparison", report);
            var missing = report.BoneMappings
                .Where(mapping => mapping.TargetBoneIndex is null)
                .OrderByDescending(mapping => mapping.SourceWeightMassPercent)
                .Take(8)
                .Select(mapping => $"{mapping.SourceBoneName} ({mapping.SourceWeightMassPercent:N3}%)")
                .ToArray();
            StatusText.Text = $"Skeleton comparison: {report.Compatibility}.";
            MessageBox.Show(this,
                $"Donor: {donor.PackageAlias}.{donor.ObjectName}\n" +
                $"Target: {target.PackageAlias}.{target.ObjectName}\n\n" +
                $"Classification: {report.Compatibility}\n" +
                $"Exact target identity: {report.ExactSkeletonIdentity}\n" +
                $"Mapped donor weight: {report.MappedSourceWeightPercent:N3}%\n" +
                $"Hierarchy-consistent weight: {report.HierarchyConsistentSourceWeightPercent:N3}%\n" +
                $"Missing bones / core bones: {report.MissingSourceBoneCount:N0} / {report.MissingCoreBoneCount:N0}\n" +
                $"Maximum bind delta: {report.MaximumMappedBindTranslationDeltaMetres:N6} m / " +
                $"{report.MaximumMappedBindRotationDeltaDegrees:N3}°\n\n" +
                report.Summary +
                (missing.Length == 0 ? "" : "\n\nLargest unresolved groups:\n" + string.Join("\n", missing)),
                "Arkive skeleton compatibility", MessageBoxButton.OK,
                report.ExactSkeletonIdentity ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                           NotSupportedException or ArgumentException or JsonException)
        {
            Log($"Skeleton comparison failed: donor={donor.Entry.ManifestPath}, " +
                $"target={target.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Compare skeletons",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshEditableWorkspaceBusy = false;
            UpdateSkeletalMeshEditableWorkspaceActionState();
        }
    }

    private async void CreateSkeletalMeshRetargetWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetRetargetPair(out var donor, out var target)) return;
        var confirmation = MessageBox.Show(this,
            $"Create a cross-skeleton authoring workspace?\n\n" +
            $"Donor geometry:\n  {donor.PackageAlias}.{donor.ObjectName}\n\n" +
            $"Native target skeleton/package:\n  {target.PackageAlias}.{target.ObjectName}\n\n" +
            "This creates clean GLB copies, a compatibility report and a Blender setup script. " +
            "It does not create or install a mod.",
            "Create Arkive retarget workspace", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;
        _skeletalMeshEditableWorkspaceBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        StatusText.Text = $"Creating {donor.ObjectName} → {target.ObjectName} retarget workspace...";
        try
        {
            var output = Path.Combine(_projectRoot, "staging", "skeletal-mesh-retargets",
                $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{SafeObjectFileName(donor.ObjectName)}-to-{SafeObjectFileName(target.ObjectName)}");
            var result = await _skeletalMeshRetargetWorkspaceService.CreateAsync(
                new SkeletalMeshRetargetWorkspaceRequest(donor.Entry, target.Entry, output));
            LogSkeletonCompatibility("retarget workspace", result.Compatibility);
            Log($"SkeletalMesh retarget workspace created: id={result.Manifest.WorkspaceId}, " +
                $"donor={result.Manifest.DonorPackageAlias}.{result.Manifest.DonorObjectName}, " +
                $"target={result.Manifest.TargetPackageAlias}.{result.Manifest.TargetObjectName}, " +
                $"classification={result.Manifest.Compatibility}, mappedWeight={result.Manifest.MappedSourceWeightPercent:N3}%, " +
                $"missingBones={result.Manifest.MissingSourceBoneCount}, missingCore={result.Manifest.MissingCoreBoneCount}, " +
                $"automatic={result.Manifest.AutomaticRetargetPerformed}, output={result.OutputDirectory}, " +
                $"manifest={result.ManifestPath}, report={result.CompatibilityReportPath}, script={result.BlenderSetupScriptPath}.");
            StatusText.Text = $"Retarget workspace created for {target.ObjectName}.";
            OpenFolder(result.OutputDirectory, "SkeletalMesh retarget workspace");
            MessageBox.Show(this,
                $"Retarget workspace created.\n\n" +
                $"Classification: {result.Compatibility.Compatibility}\n" +
                $"Mapped donor weight: {result.Compatibility.MappedSourceWeightPercent:N3}%\n" +
                $"Missing bones / core bones: {result.Compatibility.MissingSourceBoneCount:N0} / " +
                $"{result.Compatibility.MissingCoreBoneCount:N0}\n\n" +
                "Run setup-arkive-retarget.py in Blender and follow README-retarget.md. " +
                "Unresolved weights are deliberately marked ARKIVE_UNMAPPED__; Arkive will not turn them into a package automatically.",
                "Arkive retarget workspace", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException or
                                           NotSupportedException or ArgumentException or JsonException)
        {
            Log($"SkeletalMesh retarget workspace failed: donor={donor.Entry.ManifestPath}, " +
                $"target={target.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Create retarget workspace",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshEditableWorkspaceBusy = false;
            UpdateSkeletalMeshEditableWorkspaceActionState();
        }
    }

    private bool TryGetRetargetPair(
        out SkeletalMeshEditableExportRow donor,
        out SkeletalMeshEditableExportRow target)
    {
        donor = null!;
        target = null!;
        var selected = SkeletalMeshEditableExportsGrid.SelectedItems
            .OfType<SkeletalMeshEditableExportRow>().ToArray();
        var selectedTarget = SkeletalMeshEditableExportsGrid.SelectedItem as SkeletalMeshEditableExportRow;
        var selectedDonor = selected.FirstOrDefault(row => !ReferenceEquals(row, selectedTarget));
        target = selectedTarget!;
        donor = selectedDonor!;
        if (selected.Length == 2 && donor is not null && target is not null) return true;
        MessageBox.Show(this,
            "Select exactly two clean editable exports. The focused row is the native target; the other selected row is the donor.",
            "Select donor and target", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void LogSkeletonCompatibility(string trigger, SkeletalMeshSkeletonCompatibilityReport report)
    {
        Log($"Skeleton compatibility: trigger={trigger}, classification={report.Compatibility}, " +
            $"source={report.Source.PackageAlias}.{report.Source.ObjectName}, " +
            $"target={report.Target.PackageAlias}.{report.Target.ObjectName}, exact={report.ExactSkeletonIdentity}, " +
            $"sourceBones={report.Source.BoneCount}, targetBones={report.Target.BoneCount}, " +
            $"mapped={report.ExactNameMappingCount}, hierarchy={report.HierarchyConsistentMappingCount}, " +
            $"mappedWeight={report.MappedSourceWeightPercent:N3}%, hierarchyWeight={report.HierarchyConsistentSourceWeightPercent:N3}%, " +
            $"missing={report.MissingSourceBoneCount}, ambiguous={report.AmbiguousSourceBoneCount}, " +
            $"missingCore={report.MissingCoreBoneCount}, bindTranslation={report.MaximumMappedBindTranslationDeltaMetres:N6}m, " +
            $"bindRotation={report.MaximumMappedBindRotationDeltaDegrees:N3}deg.");
        foreach (var issue in report.Issues)
            Log($"Skeleton compatibility {issue.Severity}: {issue.Code}: {issue.Message}");
    }

    private async void AddEditedSkeletalMeshGlbButton_Click(object sender, RoutedEventArgs e)
    {
        if (_skeletalMeshEditableWorkspaceBusy ||
            SkeletalMeshEditableExportsGrid.SelectedItem is not SkeletalMeshEditableExportRow source) return;
        var dialog = new OpenFileDialog
        {
            Title = $"Add an edited GLB for {source.PackageAlias}.{source.ObjectName}",
            Filter = "glTF binary models (*.glb)|*.glb|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;
        _skeletalMeshEditableWorkspaceBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        StatusText.Text = $"Comparing edited GLB with {source.ObjectName}...";
        Log(
            $"Edited SkeletalMesh registration started: sourceExport={source.Entry.Manifest.ExportId}, " +
            $"sourceGlb={source.Entry.GlbPath}, candidate={dialog.FileName}.");
        try
        {
            var result = await _skeletalMeshEditWorkspaceStore.RegisterAsync(
                source.Entry, dialog.FileName);
            LogSkeletalMeshEditPreflight("registration", result.Preflight);
            _skeletalMeshEditableWorkspaceBusy = false;
            await RefreshSkeletalMeshEditableWorkspaceAsync(
                "edited GLB registered", source.Entry.Manifest.ExportId, result.Entry.Manifest.EditId);
            await RefreshGuidedTextureModsAsync(
                "edited mesh registered for package composition", result.Entry.Manifest.EditId);
            var ready = result.Preflight.Readiness != SkeletalMeshEditReadiness.Refused;
            StatusText.Text = ready
                ? $"Edited GLB is {result.Preflight.Readiness}."
                : "Edited GLB was registered, but mesh import preflight refused it.";
            var topologySummary = result.Preflight.Topology is { } topology
                ? $"\nImport profile: topology-changing LOD0 replacement\n" +
                  $"Candidate: {topology.CandidateVertexCount:N0} vertices / " +
                  $"{topology.CandidateTriangleCount:N0} triangles / " +
                  $"{topology.CandidateMaterialCount:N0} material(s)\n" +
                  $"LOD plan: duplicate LOD0 into {topology.TargetLods.Count:N0} native LOD slot(s)\n" +
                  "Native topology compilation is the next build stage; the same-length encoder is intentionally disabled.\n"
                : $"\nImport profile: topology-preserving deformation\n" +
                  $"Changed position vertices: {result.Preflight.ChangedPositionVertexCount:N0}\n" +
                  $"Maximum displacement: {result.Preflight.MaximumPositionDeltaMetres:N6} m\n";
            var animationSummary = result.Preflight.AnimationValidation is { } animation
                ? $"Animation safety: {(animation.Passed ? "passed" : "refused")} — " +
                  $"{animation.PoseCount:N0} deterministic stress pose(s), " +
                  $"{animation.MissingSignificantSourceWeightPercent:N2}% missing source influence\n" +
                  "Scope: synthetic CPU skinning; game animation clips are not executed.\n"
                : "Animation safety: unavailable; native compilation is blocked.\n";
            var skeletonSummary = result.Preflight.SkeletonCompatibility is { } skeleton
                ? $"Skeleton: {skeleton.Compatibility} — " +
                  $"{skeleton.MappedSourceWeightPercent:N3}% weighted name coverage\n"
                : "Skeleton: compatibility report unavailable; revalidate this edit.\n";
            MessageBox.Show(
                this,
                $"Edited GLB registered.\n\n" +
                $"Readiness: {result.Preflight.Readiness}\n" +
                topologySummary +
                skeletonSummary +
                animationSummary +
                $"Validation: {result.Preflight.ErrorCount:N0} error(s), " +
                $"{result.Preflight.WarningCount:N0} warning(s)\n\n" +
                string.Join("\n", result.Preflight.Issues
                    .Where(issue => issue.Severity != SkeletalMeshEditIssueSeverity.Info)
                    .Take(8)
                    .Select(issue => $"{issue.Code}: {issue.Message}")),
                ready ? "Arkive mesh import preflight passed" : "Arkive mesh import preflight refused",
                MessageBoxButton.OK,
                ready ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or ArgumentException)
        {
            StatusText.Text = "Edited GLB registration failed; see Diagnostics.";
            Log($"Edited SkeletalMesh registration failed: candidate={dialog.FileName}, error={exception}");
            MessageBox.Show(this, exception.Message, "Add edited GLB",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshEditableWorkspaceBusy = false;
            UpdateSkeletalMeshEditableWorkspaceActionState();
        }
    }

    private async void RevalidateEditedSkeletalMeshGlbButton_Click(object sender, RoutedEventArgs e)
    {
        if (_skeletalMeshEditableWorkspaceBusy ||
            SkeletalMeshEditedGlbsGrid.SelectedItem is not SkeletalMeshEditedGlbRow edit) return;
        var source = _skeletalMeshEditableExports.FirstOrDefault(row =>
            row.Entry.Manifest.ExportId.Equals(
                edit.Entry.Manifest.SourceExportId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            MessageBox.Show(this, "The clean source export is missing.", "Run mesh preflight",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _skeletalMeshEditableWorkspaceBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        try
        {
            var updated = await _skeletalMeshEditWorkspaceStore.RevalidateAsync(edit.Entry, source.Entry);
            LogSkeletalMeshEditPreflight("manual revalidation", updated.Manifest.Preflight);
            _skeletalMeshEditableWorkspaceBusy = false;
            await RefreshSkeletalMeshEditableWorkspaceAsync(
                "edited GLB revalidated", source.Entry.Manifest.ExportId, updated.Manifest.EditId);
            await RefreshGuidedTextureModsAsync(
                "edited mesh revalidated for package composition", updated.Manifest.EditId);
            StatusText.Text = $"Mesh preflight completed: {updated.Manifest.Preflight.Readiness}.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or ArgumentException)
        {
            Log($"Edited SkeletalMesh revalidation failed: edit={edit.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Run mesh preflight",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshEditableWorkspaceBusy = false;
            UpdateSkeletalMeshEditableWorkspaceActionState();
        }
    }

    private async void BuildNativeSkeletalMeshCandidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_skeletalMeshEditableWorkspaceBusy ||
            SkeletalMeshEditedGlbsGrid.SelectedItem is not SkeletalMeshEditedGlbRow edit) return;
        _skeletalMeshEditableWorkspaceBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        StatusText.Text = $"Encoding {edit.Entry.Manifest.ObjectName} into a native candidate...";
        var output = Path.Combine(edit.Entry.DirectoryPath, "native-encoding");
        Log($"Native SkeletalMesh encoding started: editId={edit.Entry.Manifest.EditId}, " +
            $"editManifest={edit.Entry.ManifestPath}, editedGlb={edit.Entry.EditedGlbPath}, output={output}.");
        try
        {
            var result = await _skeletalMeshNativeEncodingService.EncodeAsync(
                new SkeletalMeshNativeEncodingRequest(edit.Entry, output));
            Log($"Native SkeletalMesh encoding completed: encodingId={result.Manifest.EncodingId}, " +
                $"object={result.Manifest.PackageAlias}.{result.Manifest.ObjectName}, " +
                $"positionWrites={result.Manifest.Lods.Sum(lod => lod.PositionWriteCount):N0}, " +
                $"frameWrites={result.Manifest.Lods.Sum(lod => lod.TangentFrameWriteCount):N0}, " +
                $"boundsUpdated={result.Manifest.BoundsUpdated}, changedBytes={result.Manifest.ActualChangedByteCount:N0}, " +
                $"allowlistedBytes={result.Manifest.AllowlistedByteCount:N0}, " +
                $"changedLockedBytes={result.Manifest.ChangedLockedByteCount:N0}, " +
                $"lockedPreserved={result.Manifest.LockedBytesPreserved}, " +
                $"decodeBack={result.Manifest.DecodeBackPassed}, sourceSha={result.Manifest.SourceExportSha256}, " +
                $"encodedSha={result.Manifest.EncodedExportSha256}, payload={result.EncodedExportPath}, " +
                $"proofGlb={result.DecodedProofGlbPath}, proofGlbSha={result.Manifest.DecodedProofGlbSha256}, " +
                $"manifest={result.ManifestPath}, duration={result.Duration.TotalSeconds:N3}s.");
            foreach (var lod in result.Manifest.Lods)
                Log($"Native SkeletalMesh encoding LOD {lod.LodIndex}: vertices={lod.VertexCount:N0}, " +
                    $"positions={lod.PositionWriteCount:N0}, frames={lod.TangentFrameWriteCount:N0}, " +
                    $"tangentsReconstructed={lod.TangentsReconstructed}, " +
                    $"positionError={lod.MaximumPositionDecodeErrorMetres:G6}m, " +
                    $"normalError={lod.MaximumNormalDecodeError:G6}, " +
                    $"tangentError={lod.MaximumTangentDecodeError:G6}, " +
                    $"sourceVertexSha={lod.SourceVertexBufferSha256}, " +
                    $"encodedVertexSha={lod.EncodedVertexBufferSha256}.");
            _skeletalMeshEditableWorkspaceBusy = false;
            DisplayEditedSkeletalMeshesForSelectedExport(edit.Entry.Manifest.EditId);
            UpdateSkeletalMeshEditableWorkspaceActionState();
            StatusText.Text = $"Native candidate passed locked-byte and decode-back verification for {edit.Entry.Manifest.ObjectName}.";
            MessageBox.Show(this,
                $"Verified native SkeletalMesh candidate created.\n\n" +
                $"Position writes: {result.Manifest.Lods.Sum(lod => lod.PositionWriteCount):N0}\n" +
                $"Tangent-frame writes: {result.Manifest.Lods.Sum(lod => lod.TangentFrameWriteCount):N0}\n" +
                $"Changed native bytes: {result.Manifest.ActualChangedByteCount:N0}\n" +
                $"Changed locked bytes: {result.Manifest.ChangedLockedByteCount:N0}\n" +
                $"Decode-back passed: {result.Manifest.DecodeBackPassed}\n\n" +
                "This is a verified export payload, not yet a staged or installable UPK.",
                "Arkive native mesh candidate", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or ArgumentException or JsonException or CryptographicException)
        {
            StatusText.Text = "Native SkeletalMesh encoding failed; see Diagnostics.";
            Log($"Native SkeletalMesh encoding failed: edit={edit.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Build native mesh candidate",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshEditableWorkspaceBusy = false;
            UpdateSkeletalMeshEditableWorkspaceActionState();
        }
    }

    private void OpenNativeSkeletalMeshCandidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (SkeletalMeshEditedGlbsGrid.SelectedItem is SkeletalMeshEditedGlbRow row &&
            Directory.Exists(row.NativeEncodingDirectory))
            OpenFolder(row.NativeEncodingDirectory, "verified native SkeletalMesh candidate");
    }

    private async void CompileSkeletalMeshTopologyProofButton_Click(object sender, RoutedEventArgs e)
    {
        if (_skeletalMeshEditableWorkspaceBusy ||
            SkeletalMeshEditedGlbsGrid.SelectedItem is not SkeletalMeshEditedGlbRow edit) return;
        _skeletalMeshEditableWorkspaceBusy = true;
        UpdateSkeletalMeshEditableWorkspaceActionState();
        StatusText.Text = $"Compiling variable-size native LODs for {edit.Entry.Manifest.ObjectName}...";
        Log($"SkeletalMesh topology compilation started: editId={edit.Entry.Manifest.EditId}, " +
            $"editManifest={edit.Entry.ManifestPath}, editedGlb={edit.Entry.EditedGlbPath}, " +
            $"output={edit.TopologyCompilationDirectory}.");
        try
        {
            var result = await _skeletalMeshTopologyCompilerService.CompileAsync(
                new SkeletalMeshTopologyCompilationRequest(
                    edit.Entry, edit.TopologyCompilationDirectory));
            Log($"SkeletalMesh topology compilation completed: compilationId={result.Manifest.CompilationId}, " +
                $"object={result.Manifest.PackageAlias}.{result.Manifest.ObjectName}, " +
                $"sourceBytes={result.Manifest.SourceSerialSize:N0}, " +
                $"compiledBytes={result.Manifest.CompiledSerialSize:N0}, " +
                $"delta={result.Manifest.SerialSizeDelta:+#,#;-#,#;0}, " +
                $"prefixPreserved={result.Manifest.PrefixPreservedExceptBounds}, " +
                $"trailingPreserved={result.Manifest.TrailingBytesPreserved}, " +
                $"decodeBack={result.Manifest.DecodeBackPassed}, " +
                $"packageIntegration={result.Manifest.PackageIntegrationEnabled}, " +
                $"payload={result.CompiledExportPath}, proofGlb={result.DecodedProofGlbPath}, " +
                $"manifest={result.ManifestPath}, duration={result.Duration.TotalSeconds:N3}s.");
            foreach (var lod in result.Manifest.Lods)
                Log($"SkeletalMesh topology LOD {lod.LodIndex}: " +
                    $"candidate={lod.CandidateVertexCount:N0}v/{lod.CandidateTriangleCount:N0}t, " +
                    $"compiled={lod.CompiledVertexCount:N0}v/{lod.CompiledTriangleCount:N0}t, " +
                    $"sections={lod.SectionCount:N0}, chunks={lod.ChunkCount:N0}, " +
                    $"duplicates={lod.DuplicatedVertexCount:N0}, indexWidth={lod.IndexElementSize}, " +
                    $"positionError={lod.MaximumPositionDecodeErrorMetres:G6}m, " +
                    $"normalError={lod.MaximumNormalDecodeError:G6}, " +
                    $"tangentError={lod.MaximumTangentDecodeError:G6}, " +
                    $"uvError={lod.MaximumUvDecodeError:G6}, " +
                    $"weightError={lod.MaximumWeightDecodeError:G6}, " +
                    $"jointsExact={lod.JointsDecodeExactly}, topologyExact={lod.TriangleMaterialTopologyMatches}.");
            _skeletalMeshEditableWorkspaceBusy = false;
            DisplayEditedSkeletalMeshesForSelectedExport(edit.Entry.Manifest.EditId);
            UpdateSkeletalMeshEditableWorkspaceActionState();
            StatusText.Text = $"Topology compiler and decode-back proof passed for {edit.Entry.Manifest.ObjectName}.";
            MessageBox.Show(this,
                $"Verified variable-size native SkeletalMesh proof created.\n\n" +
                $"Native LOD slots rebuilt: {result.Manifest.Lods.Count:N0}\n" +
                $"Compiled vertices: {result.Manifest.Lods.Sum(lod => lod.CompiledVertexCount):N0}\n" +
                $"Compiled triangles: {result.Manifest.Lods.Sum(lod => lod.CompiledTriangleCount):N0}\n" +
                $"Export size change: {result.Manifest.SerialSizeDelta:+#,#;-#,#;0} bytes\n" +
                $"Decode-back passed: {result.Manifest.DecodeBackPassed}\n\n" +
                "The output and proof GLB are ready for inspection. Use Build staged UPK to append and retarget " +
                "the replacement in an isolated package. Installation remains disabled during real-package acceptance.",
                "Arkive topology compiler proof", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or ArgumentException or JsonException or CryptographicException)
        {
            StatusText.Text = "SkeletalMesh topology compilation failed; see Diagnostics.";
            Log($"SkeletalMesh topology compilation failed: edit={edit.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Compile topology proof",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _skeletalMeshEditableWorkspaceBusy = false;
            UpdateSkeletalMeshEditableWorkspaceActionState();
        }
    }

    private void OpenSkeletalMeshTopologyProofButton_Click(object sender, RoutedEventArgs e)
    {
        if (SkeletalMeshEditedGlbsGrid.SelectedItem is SkeletalMeshEditedGlbRow row &&
            Directory.Exists(row.TopologyCompilationDirectory))
            OpenFolder(row.TopologyCompilationDirectory, "verified SkeletalMesh topology proof");
    }

}
