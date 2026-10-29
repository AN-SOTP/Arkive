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
    private async Task RefreshGuidedTextureModsAsync(string trigger, string? selectProjectId = null)
    {
        try
        {
            await ImportExistingModifiedUpksAsync();
            try
            {
                _arkiveModLibraryManifest = await _arkiveModLibrary.LoadAsync();
            }
            catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
            {
                Log($"Mod library ownership could not be loaded for package projects: {exception.Message}");
            }
            var modOwnedEditIds = ModOwnersByEditId();
            var entries = await _guidedTextureModWorkspace.LoadAsync();
            var materialEntries = (await _materialEditWorkspaceStore.LoadAsync()).ToList();
            foreach (var versions in materialEntries
                         .Where(entry => entry.Document.IncludedInPackageBuild &&
                                         !modOwnedEditIds.ContainsKey(entry.Document.EditId))
                         .GroupBy(entry => Path.GetFullPath(entry.Document.SourcePackagePath).ToUpperInvariant() + "\0" +
                                           entry.Document.ExportIndex)
                         .Where(group => group.Count() > 1))
            {
                var keep = versions.OrderByDescending(entry => entry.Document.UpdatedAt).First();
                foreach (var stale in versions.Where(entry => !entry.Document.EditId.Equals(
                             keep.Document.EditId, StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    var index = materialEntries.FindIndex(entry => entry.Document.EditId.Equals(
                        stale.Document.EditId, StringComparison.OrdinalIgnoreCase));
                    materialEntries[index] = await _materialEditWorkspaceStore.SetIncludedAsync(stale, false);
                    Log($"Unified material selection migrated: export={stale.Document.ExportIndex}, " +
                        $"kept={keep.Document.EditId}, excludedOlder={stale.Document.EditId}.");
                }
            }
            var meshSnapshot = await _skeletalMeshEditWorkspaceStore.LoadAsync();
            var editableMeshSnapshot = await _skeletalMeshEditableWorkspaceStore.LoadAsync();
            var meshSourcesByExportId = editableMeshSnapshot.Entries.ToDictionary(
                entry => entry.Manifest.ExportId, StringComparer.OrdinalIgnoreCase);
            var meshEntries = meshSnapshot.Entries
                .Where(entry => meshSourcesByExportId.ContainsKey(entry.Manifest.SourceExportId))
                .ToList();
            foreach (var versions in meshEntries
                         .Where(entry => entry.Manifest.IncludedInPackageBuild &&
                                         !modOwnedEditIds.ContainsKey(entry.Manifest.EditId))
                         .GroupBy(entry => entry.Manifest.SourceExportId, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
            {
                var keep = versions.OrderByDescending(entry => entry.Manifest.CreatedAt).First();
                foreach (var stale in versions.Where(entry => !entry.Manifest.EditId.Equals(
                             keep.Manifest.EditId, StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    var index = meshEntries.FindIndex(entry => entry.Manifest.EditId.Equals(
                        stale.Manifest.EditId, StringComparison.OrdinalIgnoreCase));
                    meshEntries[index] = await _skeletalMeshEditWorkspaceStore.SetIncludedAsync(
                        stale, false);
                    Log($"Unified mesh selection migrated: sourceExport={stale.Manifest.SourceExportId}, " +
                        $"kept={keep.Manifest.EditId}, excludedOlder={stale.Manifest.EditId}.");
                }
            }
            var staticMeshEntries = (await _staticMeshEditWorkspaceStore.LoadAsync()).ToList();
            var staticMeshSources = new Dictionary<string, StaticMeshEditableExportManifest>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in staticMeshEntries.ToArray())
            {
                try
                {
                    staticMeshSources[entry.Manifest.EditId] =
                        await StaticMeshEditableExportService.LoadManifestAsync(
                            entry.Manifest.SourceEditableManifestPath);
                }
                catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
                {
                    staticMeshEntries.Remove(entry);
                    Log($"StaticMesh package project ignored: edit={entry.Manifest.EditId}, error={exception.Message}");
                }
            }
            foreach (var versions in staticMeshEntries
                         .Where(entry => entry.Manifest.IncludedInPackageBuild &&
                                         !modOwnedEditIds.ContainsKey(entry.Manifest.EditId))
                         .GroupBy(entry => staticMeshSources[entry.Manifest.EditId].NativeExportIndex)
                         .Where(group => group.Count() > 1))
            {
                var keep = versions.OrderByDescending(entry => entry.Manifest.CreatedAt).First();
                foreach (var stale in versions.Where(entry => !entry.Manifest.EditId.Equals(
                             keep.Manifest.EditId, StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    var index = staticMeshEntries.FindIndex(entry => entry.Manifest.EditId.Equals(
                        stale.Manifest.EditId, StringComparison.OrdinalIgnoreCase));
                    staticMeshEntries[index] = await _staticMeshEditWorkspaceStore.SetIncludedAsync(stale, false);
                    Log($"Unified StaticMesh selection migrated: export={staticMeshSources[stale.Manifest.EditId].NativeExportIndex}, kept={keep.Manifest.EditId}, excludedOlder={stale.Manifest.EditId}.");
                }
            }
            var packageEntries = new Dictionary<string, GuidedTexturePackageEntry>(
                StringComparer.OrdinalIgnoreCase);
            // Read the package projects once; looking each one up separately re-read all of them per package.
            var knownPackages = new Dictionary<string, GuidedTexturePackageEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var known in await _guidedTexturePackageWorkspace.LoadAsync())
                knownPackages.TryAdd(Path.GetFullPath(known.Manifest.SourcePackagePath), known);
            async Task<GuidedTexturePackageEntry> PackageFor(string alias, string physicalName, string sourcePath)
            {
                if (knownPackages.TryGetValue(Path.GetFullPath(sourcePath), out var known)) return known;
                var created = await _guidedTexturePackageWorkspace.GetOrCreateAsync(alias, physicalName, sourcePath);
                knownPackages[Path.GetFullPath(created.Manifest.SourcePackagePath)] = created;
                return created;
            }
            foreach (var group in entries.GroupBy(entry =>
                         Path.GetFullPath(entry.Manifest.SourcePackagePath),
                         StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                var package = await PackageFor(
                    first.Manifest.PackageAlias,
                    first.Manifest.PhysicalPackageName,
                    first.Manifest.SourcePackagePath);
                packageEntries[Path.GetFullPath(package.Manifest.SourcePackagePath)] = package;
            }
            foreach (var group in meshEntries.GroupBy(entry => Path.GetFullPath(
                         meshSourcesByExportId[entry.Manifest.SourceExportId].Manifest.SourcePackagePath),
                         StringComparer.OrdinalIgnoreCase))
            {
                var source = meshSourcesByExportId[group.First().Manifest.SourceExportId].Manifest;
                var package = await PackageFor(source.PackageAlias, source.PhysicalPackageName, source.SourcePackagePath);
                packageEntries[Path.GetFullPath(package.Manifest.SourcePackagePath)] = package;
            }
            foreach (var group in materialEntries.GroupBy(entry =>
                         Path.GetFullPath(entry.Document.SourcePackagePath),
                         StringComparer.OrdinalIgnoreCase))
            {
                var source = group.First().Document;
                var package = await PackageFor(source.PackageAlias, Path.GetFileName(source.SourcePackagePath), source.SourcePackagePath);
                packageEntries[Path.GetFullPath(package.Manifest.SourcePackagePath)] = package;
            }
            foreach (var group in staticMeshEntries.GroupBy(entry => Path.GetFullPath(
                         staticMeshSources[entry.Manifest.EditId].SourcePackagePath),
                         StringComparer.OrdinalIgnoreCase))
            {
                var source = staticMeshSources[group.First().Manifest.EditId];
                var package = await PackageFor(source.PackageAlias, source.PhysicalPackageName, source.SourcePackagePath);
                packageEntries[Path.GetFullPath(package.Manifest.SourcePackagePath)] = package;
            }
            // Effect, physics and interface movie edits are built into packages too; without a row, a package they alone
            // change has no install state, and the Mods list never counts their mod as applied.
            var otherEditSources = (await _arkiveModEdits.ParticleEdits.LoadAsync())
                .Select(edit => (Alias: edit.Document.PackageAlias, Physical: edit.Document.PhysicalPackageName,
                    Source: edit.Document.SourcePackagePath))
                .Concat((await _arkiveModEdits.PhysicsEdits.LoadAsync())
                    .Select(edit => (Alias: edit.Document.PackageAlias, Physical: edit.Document.PhysicalPackageName,
                        Source: edit.Document.SourcePackagePath)))
                .Concat(_arkiveModEdits.InterfaceMovies is { } movies
                    ? (await movies.LoadAsync()).Select(movie => (Alias: movie.Manifest.PackageAlias,
                        Physical: Path.GetFileName(movie.Manifest.SourcePackagePath), Source: movie.Manifest.SourcePackagePath))
                    : []);
            foreach (var source in otherEditSources.DistinctBy(source => Path.GetFullPath(source.Source),
                         StringComparer.OrdinalIgnoreCase))
            {
                if (packageEntries.ContainsKey(Path.GetFullPath(source.Source))) continue;
                var package = await PackageFor(source.Alias, source.Physical, source.Source);
                packageEntries[Path.GetFullPath(package.Manifest.SourcePackagePath)] = package;
            }
            var packageRows = new List<GuidedTexturePackageRow>(packageEntries.Count);
            // Each package's install state reads its builds, receipts and game file; hundreds of packages took 5-20 s
            // one after another, so they are read a few at a time.
            var deploymentGate = new SemaphoreSlim(8);
            var deployments = packageEntries.Values.ToDictionary(package => package, async package =>
            {
                await deploymentGate.WaitAsync();
                try { return await ResolveGuidedDeploymentStateAsync(package); }
                finally { deploymentGate.Release(); }
            });
            foreach (var package in packageEntries.Values)
            {
                var projectEntries = entries.Where(entry => Path.GetFullPath(entry.Manifest.SourcePackagePath)
                        .Equals(Path.GetFullPath(package.Manifest.SourcePackagePath),
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var packageMeshEntries = meshEntries.Where(entry => SamePath(
                        meshSourcesByExportId[entry.Manifest.SourceExportId].Manifest.SourcePackagePath,
                        package.Manifest.SourcePackagePath))
                    .ToArray();
                var packageMaterialEntries = materialEntries.Where(entry => SamePath(
                        entry.Document.SourcePackagePath, package.Manifest.SourcePackagePath))
                    .ToArray();
                var packageStaticMeshEntries = staticMeshEntries.Where(entry => SamePath(
                        staticMeshSources[entry.Manifest.EditId].SourcePackagePath,
                        package.Manifest.SourcePackagePath))
                    .ToArray();
                packageRows.Add(await CreateGuidedPackageRowAsync(
                    package, await deployments[package], projectEntries, packageMeshEntries, packageStaticMeshEntries,
                    staticMeshSources, packageMaterialEntries));
            }
            var orderedPackageRows = packageRows
                .OrderByDescending(row => row.Entry.Manifest.IsFavorite)
                .ThenByDescending(row => row.Entry.Manifest.UpdatedAt)
                .ThenBy(row => row.PackageAlias, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _guidedPackageRows = orderedPackageRows;
            GuidedPackagesGrid.ItemsSource = orderedPackageRows;

            var selectedPackage = !string.IsNullOrWhiteSpace(selectProjectId)
                ? orderedPackageRows.FirstOrDefault(row =>
                    row.Assets.Any(asset => asset.Entry.Manifest.ProjectId.Equals(
                        selectProjectId, StringComparison.OrdinalIgnoreCase)) ||
                    row.MeshAssets.Any(asset => asset.Entry.Manifest.EditId.Equals(
                        selectProjectId, StringComparison.OrdinalIgnoreCase)) ||
                    row.StaticMeshAssets.Any(asset => asset.Entry.Manifest.EditId.Equals(
                        selectProjectId, StringComparison.OrdinalIgnoreCase)) ||
                    row.MaterialAssets.Any(asset => asset.Entry.Document.EditId.Equals(
                        selectProjectId, StringComparison.OrdinalIgnoreCase)))
                : GuidedPackagesGrid.SelectedItem is GuidedTexturePackageRow previous
                    ? orderedPackageRows.FirstOrDefault(row => row.Entry.Manifest.PackageProjectId.Equals(
                        previous.Entry.Manifest.PackageProjectId, StringComparison.OrdinalIgnoreCase))
                    : orderedPackageRows.FirstOrDefault();
            if (selectedPackage is not null)
            {
                GuidedPackagesGrid.SelectedItem = selectedPackage;
                GuidedPackagesGrid.ScrollIntoView(selectedPackage);
                ApplyGuidedProjectFilter(selectedPackage);
                var selectedAsset = !string.IsNullOrWhiteSpace(selectProjectId)
                    ? selectedPackage.Assets.FirstOrDefault(asset => asset.Entry.Manifest.ProjectId.Equals(
                        selectProjectId, StringComparison.OrdinalIgnoreCase))
                    : selectedPackage.Assets.FirstOrDefault();
                GuidedTextureModsGrid.SelectedItem = selectedAsset;
                if (selectedAsset is not null) GuidedTextureModsGrid.ScrollIntoView(selectedAsset);
                var selectedMesh = !string.IsNullOrWhiteSpace(selectProjectId)
                    ? selectedPackage.MeshAssets.FirstOrDefault(asset => asset.Entry.Manifest.EditId.Equals(
                        selectProjectId, StringComparison.OrdinalIgnoreCase))
                    : selectedPackage.MeshAssets.FirstOrDefault();
                GuidedMeshModsGrid.SelectedItem = selectedMesh;
                if (selectedMesh is not null) GuidedMeshModsGrid.ScrollIntoView(selectedMesh);
                var selectedMaterial = !string.IsNullOrWhiteSpace(selectProjectId)
                    ? selectedPackage.MaterialAssets.FirstOrDefault(asset => asset.Entry.Document.EditId.Equals(
                        selectProjectId, StringComparison.OrdinalIgnoreCase))
                    : selectedPackage.MaterialAssets.FirstOrDefault();
                GuidedMaterialModsGrid.SelectedItem = selectedMaterial;
                if (selectedMaterial is not null) GuidedMaterialModsGrid.ScrollIntoView(selectedMaterial);
                var selectedStaticMesh = !string.IsNullOrWhiteSpace(selectProjectId)
                    ? selectedPackage.StaticMeshAssets.FirstOrDefault(asset => asset.Entry.Manifest.EditId.Equals(
                        selectProjectId, StringComparison.OrdinalIgnoreCase))
                    : selectedPackage.StaticMeshAssets.FirstOrDefault();
                GuidedStaticMeshModsGrid.SelectedItem = selectedStaticMesh;
                if (selectedStaticMesh is not null) GuidedStaticMeshModsGrid.ScrollIntoView(selectedStaticMesh);
            }
            else
            {
                _guidedVisibleAssets = [];
                _guidedVisibleMeshes = [];
                _guidedVisibleStaticMeshes = [];
                _guidedVisibleMaterials = [];
                GuidedTextureModsGrid.ItemsSource = null;
                GuidedMeshModsGrid.ItemsSource = null;
                GuidedStaticMeshModsGrid.ItemsSource = null;
                GuidedMaterialModsGrid.ItemsSource = null;
            }
            UpdateGuidedTextureModActionState();
            UpdateClientUpdateActionState();
            if (ModSetsGrid?.SelectedItem is ModSetRow selectedSet)
                ApplySelectedModSet(selectedSet);
            RefreshWorkshopViews(selectProjectId);
            if (!_modLibraryBusy) await RefreshArkiveModsAsync(trigger);
            Log(
                $"Guided texture-mod workspace refreshed: trigger={trigger}, " +
                $"packages={orderedPackageRows.Length}, textureProjects={entries.Count}, " +
                $"materialProjects={materialEntries.Count}, skeletalMeshProjects={meshEntries.Count}, staticMeshProjects={staticMeshEntries.Count}, selectedAssets={orderedPackageRows.Sum(row => row.IncludedAssetCount)}, " +
                $"root={_guidedTextureModWorkspace.RootDirectory}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Guided texture-mod workspace refresh failed: trigger={trigger}, error={exception}");
        }
    }

    private async Task<GuidedTexturePackageRow> CreateGuidedPackageRowAsync(
        GuidedTexturePackageEntry package,
        GuidedDeploymentRevisionState? deployment,
        IReadOnlyList<GuidedTextureModProjectEntry> projects,
        IReadOnlyList<SkeletalMeshEditWorkspaceEntry> meshProjects,
        IReadOnlyList<StaticMeshEditEntry> staticMeshProjects,
        IReadOnlyDictionary<string, StaticMeshEditableExportManifest> staticMeshSources,
        IReadOnlyList<MaterialInstanceEditEntry> materialProjects)
    {
        var latest = package.Manifest.LatestRevisionId is null
            ? package.Manifest.Revisions.LastOrDefault()
            : package.Manifest.Revisions.FirstOrDefault(revision => revision.RevisionId.Equals(
                package.Manifest.LatestRevisionId, StringComparison.OrdinalIgnoreCase));
        var inspection = deployment?.Inspection;
        var inspectionError = deployment?.Error;

        var rows = new List<GuidedTextureModRow>(projects.Count);
        foreach (var project in projects)
        {
            string? editableHash = null;
            try
            {
                if (File.Exists(project.Manifest.EditableImagePath))
                {
                    editableHash = await HashFileForPreviewAsync(project.Manifest.EditableImagePath);
                }
            }
            catch (IOException)
            {
            }

            var metadata = _gameMetadataIndex?.Find(
                project.Manifest.PackageAlias,
                project.Manifest.PhysicalPackageName,
                project.Manifest.ObjectName) ?? [];
            rows.Add(new GuidedTextureModRow(project, latest, editableHash, metadata, inspection));
        }

        var meshRows = new List<GuidedMeshModRow>(meshProjects.Count);
        foreach (var mesh in meshProjects)
        {
            string? editedHash = null;
            try
            {
                if (File.Exists(mesh.EditedGlbPath))
                    editedHash = await HashFileForPreviewAsync(mesh.EditedGlbPath);
            }
            catch (IOException)
            {
            }
            meshRows.Add(new GuidedMeshModRow(mesh, latest, editedHash, inspection));
        }

        var materialRows = materialProjects.Select(material =>
            new GuidedMaterialModRow(material, latest, inspection)).ToArray();

        var staticMeshRows = new List<GuidedStaticMeshModRow>(staticMeshProjects.Count);
        foreach (var mesh in staticMeshProjects)
        {
            string? editedHash = null;
            try
            {
                if (File.Exists(mesh.Manifest.EditedGlbPath))
                    editedHash = await HashFileForPreviewAsync(mesh.Manifest.EditedGlbPath);
            }
            catch (IOException)
            {
            }
            staticMeshRows.Add(new GuidedStaticMeshModRow(
                mesh, staticMeshSources[mesh.Manifest.EditId], latest, editedHash, inspection));
        }

        var owners = ModOwnersByEditId();
        foreach (var row in rows.Concat<GuidedAssetRow>(meshRows).Concat(materialRows).Concat(staticMeshRows))
            row.OwnerModName = owners.GetValueOrDefault(row.EditId);
        return new GuidedTexturePackageRow(
            package,
            rows.OrderByDescending(row => row.Entry.Manifest.IsFavorite)
                .ThenByDescending(row => row.Entry.Manifest.LastOpenedAt)
                .ThenBy(row => row.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            meshRows.OrderByDescending(row => row.Entry.Manifest.CreatedAt)
                .ThenBy(row => row.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            staticMeshRows.OrderByDescending(row => row.Entry.Manifest.CreatedAt)
                .ThenBy(row => row.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            materialRows.OrderByDescending(row => row.Entry.Document.UpdatedAt)
                .ThenBy(row => row.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            deployment?.Revision,
            inspection,
            inspectionError);
    }

    private async Task<GuidedDeploymentRevisionState?> ResolveGuidedDeploymentStateAsync(
        GuidedTexturePackageEntry package)
    {
        var revisions = package.Manifest.Revisions
            .Where(revision => File.Exists(revision.ModBundleManifestPath))
            .OrderByDescending(revision => revision.CreatedAt)
            .ToArray();
        if (revisions.Length == 0) return null;

        var latest = package.Manifest.LatestRevisionId is null
            ? revisions[0]
            : revisions.FirstOrDefault(revision => revision.RevisionId.Equals(
                package.Manifest.LatestRevisionId, StringComparison.OrdinalIgnoreCase)) ?? revisions[0];
        GuidedDeploymentRevisionState? latestState = null;
        try
        {
            var inspection = await _modDeploymentService.InspectForRecoveryAsync(
                CreateModDeploymentRequest(latest.ModBundleManifestPath) with { ReuseKnownHashes = true });
            latestState = new GuidedDeploymentRevisionState(latest, inspection, null);
            if (HasActiveDeployment(inspection)) return latestState;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            latestState = new GuidedDeploymentRevisionState(latest, null, exception.Message);
            Log(
                $"Guided package refresh latest-revision inspection failed: " +
                $"packageId={package.Manifest.PackageProjectId}, revision={latest.RevisionId}, error={exception}");
        }

        foreach (var revision in revisions.Where(revision => !revision.RevisionId.Equals(
                     latest.RevisionId, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var bundle = await ArkiveModBundleService.LoadBundleAsync(revision.ModBundleManifestPath);
                var receiptPath = Path.Combine(
                    _projectRoot, "backups", "installations", bundle.ModId,
                    ArkiveModDeploymentService.ReceiptFileName);
                if (!File.Exists(receiptPath)) continue;
                var receipt = await ArkiveModDeploymentService.LoadReceiptAsync(receiptPath);
                if (!receipt.GameFilesModified && receipt.State is not (
                        ModInstallationState.Installed or ModInstallationState.Prepared)) continue;

                var inspection = await _modDeploymentService.InspectForDeletionIdentityAsync(
                    CreateModDeploymentRequest(revision.ModBundleManifestPath) with { ReuseKnownHashes = true });
                if (!HasActiveDeployment(inspection)) continue;
                Log(
                    $"Guided package active earlier revision resolved: " +
                    $"packageId={package.Manifest.PackageProjectId}, revision={revision.RevisionId}, " +
                    $"latestRevision={latest.RevisionId}, state={inspection.State}, " +
                    $"targetSha={inspection.CurrentTargetSha256}.");
                return new GuidedDeploymentRevisionState(revision, inspection, null);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Log(
                    $"Guided package earlier-revision inspection was inconclusive: " +
                    $"packageId={package.Manifest.PackageProjectId}, revision={revision.RevisionId}, error={exception.Message}");
            }
        }

        var lineage = new List<(GuidedTexturePackageRevision Revision, ArkiveModBundleManifest Bundle)>();
        foreach (var revision in revisions)
        {
            try
            {
                lineage.Add((revision,
                    await ArkiveModBundleService.LoadBundleAsync(revision.ModBundleManifestPath)));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Log($"Guided package lineage manifest was unreadable: packageId={package.Manifest.PackageProjectId}, " +
                    $"revision={revision.RevisionId}, error={exception.Message}");
            }
        }
        var payloadHashes = lineage.Select(item => item.Bundle.PayloadSha256)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var root in lineage.Where(item =>
                     !payloadHashes.Contains(item.Bundle.RequiredSourcePackageSha256)))
        {
            try
            {
                var inspection = await _modDeploymentService.InspectForRecoveryAsync(
                    CreateModDeploymentRequest(root.Revision.ModBundleManifestPath) with { ReuseKnownHashes = true });
                if (inspection.State is not (
                        ModInstallationState.NotInstalled or ModInstallationState.Restored) ||
                    !inspection.CurrentTargetSha256!.Equals(
                        root.Bundle.RequiredSourcePackageSha256,
                        StringComparison.OrdinalIgnoreCase)) continue;
                Log(
                    $"Guided package clean lineage root resolved: packageId={package.Manifest.PackageProjectId}, " +
                    $"revision={root.Revision.RevisionId}, rootSha={root.Bundle.RequiredSourcePackageSha256}, " +
                    $"latestRevision={latest.RevisionId}.");
                return new GuidedDeploymentRevisionState(root.Revision, inspection, null);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Log($"Guided package root-revision inspection was inconclusive: " +
                    $"packageId={package.Manifest.PackageProjectId}, revision={root.Revision.RevisionId}, error={exception.Message}");
            }
        }

        return latestState;
    }

    private static bool HasActiveDeployment(ModDeploymentInspection inspection) =>
        inspection.HasActiveDeployment;

    private async Task ImportExistingModifiedUpksAsync()
    {
        var snapshot = await _exportWorkspaceStore.LoadAsync();
        var candidates = snapshot.Entries
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Manifest.ModBundleManifestPath) &&
                !string.IsNullOrWhiteSpace(entry.Manifest.DeploymentReadinessManifestPath) &&
                !string.IsNullOrWhiteSpace(entry.Manifest.TextureImportCandidateManifestPath) &&
                !string.IsNullOrWhiteSpace(entry.Manifest.TextureReplacementPayloadManifestPath) &&
                !string.IsNullOrWhiteSpace(entry.Manifest.TextureExportAssemblyManifestPath) &&
                !string.IsNullOrWhiteSpace(entry.Manifest.PackageRewritePlanManifestPath) &&
                !string.IsNullOrWhiteSpace(entry.Manifest.LogicalPackageImageManifestPath) &&
                !string.IsNullOrWhiteSpace(entry.Manifest.PhysicalPackagePlanManifestPath) &&
                !string.IsNullOrWhiteSpace(entry.Manifest.NoOpUpkReconstructionManifestPath) &&
                !string.IsNullOrWhiteSpace(entry.Manifest.ReplacementUpkBuildManifestPath))
            .GroupBy(entry =>
                Path.GetFullPath(entry.Manifest.SourcePackagePath).ToUpperInvariant() + "\0" +
                entry.Manifest.ExportIndex)
            .Select(group => group.OrderByDescending(entry => entry.Manifest.CreatedAt).First())
            .ToArray();

        foreach (var source in candidates)
        {
            if (await _guidedTextureModWorkspace.IsLegacyImportSuppressedAsync(
                    source.Manifest.ModBundleManifestPath!)) continue;
            if (await _guidedTextureModWorkspace.FindAsync(
                    source.Manifest.SourcePackagePath,
                    source.Manifest.ObjectName,
                    source.Manifest.ExportIndex) is not null) continue;
            try
            {
                var import = await TextureImportCandidateService.LoadManifestAsync(
                    source.Manifest.TextureImportCandidateManifestPath!);
                if (!File.Exists(import.EditedDdsPath) ||
                    !File.Exists(import.NativeTextureManifestPath)) continue;
                var bundle = await ArkiveModBundleService.LoadBundleAsync(
                    source.Manifest.ModBundleManifestPath!);
                var replacement = await ReplacementUpkBuildService.VerifyOutputAsync(
                    source.Manifest.ReplacementUpkBuildManifestPath!);
                var friendlyName = source.Manifest.InGameNames.FirstOrDefault();
                var project = await _guidedTextureModWorkspace.CreateAsync(
                    string.IsNullOrWhiteSpace(friendlyName)
                        ? $"{source.Manifest.ObjectName} texture mod"
                        : $"{friendlyName} - {source.Manifest.ObjectName}",
                    source.Manifest.PackageAlias,
                    source.Manifest.PhysicalPackageName,
                    source.Manifest.SourcePackagePath,
                    source.Manifest.ClassName,
                    source.Manifest.ObjectName,
                    source.Manifest.ExportIndex,
                    import.PixelFormat,
                    import.Width,
                    import.Height,
                    import.NativeTextureManifestPath,
                    import.EditedDdsPath);
                var payloadPath = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(source.Manifest.ModBundleManifestPath!)!,
                    bundle.PayloadRelativePath));
                var revision = new GuidedTextureModRevision(
                    "imported-" + bundle.ModId[..Math.Min(16, bundle.ModId.Length)],
                    bundle.CreatedAt,
                    import.EditedDdsSha256,
                    source.Manifest.TextureImportCandidateManifestPath!,
                    source.Manifest.TextureReplacementPayloadManifestPath!,
                    source.Manifest.TextureExportAssemblyManifestPath!,
                    source.Manifest.PackageRewritePlanManifestPath!,
                    source.Manifest.LogicalPackageImageManifestPath!,
                    source.Manifest.PhysicalPackagePlanManifestPath!,
                    source.Manifest.NoOpUpkReconstructionManifestPath!,
                    source.Manifest.ReplacementUpkBuildManifestPath!,
                    replacement.OutputUpkPath,
                    source.Manifest.ModBundleManifestPath!,
                    source.Manifest.DeploymentReadinessManifestPath!,
                    payloadPath,
                    bundle.PayloadBytes,
                    bundle.PayloadSha256,
                    source.Manifest.DurationSeconds);
                await _guidedTextureModWorkspace.CommitRevisionAsync(project.ManifestPath, revision);
                Log(
                    $"Existing advanced modified UPK imported into guided workspace: " +
                    $"projectId={project.Manifest.ProjectId}, object={project.Manifest.ObjectName}, " +
                    $"bundle={source.Manifest.ModBundleManifestPath}.");
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Log(
                    $"Existing advanced modified UPK was not imported into the guided workspace: " +
                    $"exportId={source.Manifest.ExportId}, object={source.Manifest.ObjectName}, error={exception}");
            }
        }
    }

    private async void GuidedTextureModsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateGuidedTextureModActionState();
        if (GuidedTextureModsGrid.SelectedItems.Count == 1 &&
            GuidedTextureModsGrid.SelectedItem is GuidedTextureModRow row)
        {
            GuidedImpactText.Text = row.ImpactSummary;
            GuidedFavoriteAssetButton.Content = row.Entry.Manifest.IsFavorite
                ? "★ Favourite texture"
                : "☆ Favourite texture";
        }
        else
        {
            GuidedImpactText.Text = GuidedTextureModsGrid.SelectedItems.Count > 1
                ? $"Impact: {GuidedTextureModsGrid.SelectedItems.Count:N0} projects selected. Modifications are global on this client for every actor using these assets."
                : "Impact: select a texture to see verified in-game uses.";
        }
        await ResolveGuidedModelPreviewCandidatesAsync();
    }

    private async void GuidedPackagesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedClientCompatibility = null;
        if (GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow package)
        {
            ClearGuidedDeploymentInspection();
            GuidedModNameTextBox.Text = string.Empty;
            GuidedModNotesTextBox.Text = string.Empty;
            GuidedTextureModsGrid.ItemsSource = null;
            GuidedMeshModsGrid.ItemsSource = null;
            GuidedStaticMeshModsGrid.ItemsSource = null;
            GuidedMaterialModsGrid.ItemsSource = null;
            UpdateGuidedTextureModActionState();
            return;
        }
        GuidedModNameTextBox.Text = package.DisplayName;
        GuidedModNotesTextBox.Text = package.Entry.Manifest.Notes ?? string.Empty;
        GuidedFavoritePackageButton.Content = package.Entry.Manifest.IsFavorite
            ? "★ Favourite UPK"
            : "☆ Favourite UPK";
        ApplyGuidedProjectFilter(package);
        if (package.Assets.Count > 0 && GuidedTextureModsGrid.SelectedItem is not GuidedTextureModRow)
        {
            GuidedTextureModsGrid.SelectedItem = package.Assets[0];
        }
        if (package.MeshAssets.Count > 0 && GuidedMeshModsGrid.SelectedItem is not GuidedMeshModRow)
            GuidedMeshModsGrid.SelectedItem = package.MeshAssets[0];
        if (package.StaticMeshAssets.Count > 0 &&
            GuidedStaticMeshModsGrid.SelectedItem is not GuidedStaticMeshModRow)
            GuidedStaticMeshModsGrid.SelectedItem = package.StaticMeshAssets[0];
        if (package.MaterialAssets.Count > 0 && GuidedMaterialModsGrid.SelectedItem is not GuidedMaterialModRow)
            GuidedMaterialModsGrid.SelectedItem = package.MaterialAssets[0];
        UpdateGuidedTextureModActionState();
        var latest = package.LatestRevision;
        if (latest is null)
        {
            ClearGuidedDeploymentInspection();
            UpdateGuidedTextureModActionState();
            GuidedBuildStatusText.Text =
                $"{package.IncludedAssetCount:N0} of {package.Assets.Count + package.MaterialAssets.Count + package.MeshAssets.Count + package.StaticMeshAssets.Count:N0} asset projects selected. " +
                "Edit the source PNGs, material parameters, or GLBs, then rebuild one cumulative modified UPK.";
            return;
        }
        var deploymentRevision = package.DeploymentRevision ?? latest;
        if (!package.Entry.Manifest.PackageProjectId.Equals(
                _guidedDeploymentPackageProjectId, StringComparison.OrdinalIgnoreCase) ||
            !deploymentRevision.RevisionId.Equals(
                _guidedDeploymentRevisionId, StringComparison.OrdinalIgnoreCase))
        {
            ClearGuidedDeploymentInspection();
            UpdateGuidedTextureModActionState();
        }
        GuidedBuildStatusText.Text =
            $"Latest package build contains {latest.Assets.Count:N0} texture(s), {latest.MaterialAssets.Count:N0} material(s), {latest.MeshAssets.Count:N0} skeletal mesh(es), and {latest.StaticMeshAssets.Count:N0} StaticMesh(es), and passed all gates on " +
            $"{latest.CreatedAt.LocalDateTime:g}. " +
            (latest.PreviewOnly
                ? "This variable-size build is preview-only; checking its retained validation receipt..."
                : "Checking install state...");
        try
        {
            var inspection = await _modDeploymentService.InspectForRecoveryAsync(
                CreateModDeploymentRequest(deploymentRevision.ModBundleManifestPath));
            if (GuidedPackagesGrid.SelectedItem is GuidedTexturePackageRow current &&
                current.Entry.Manifest.PackageProjectId == package.Entry.Manifest.PackageProjectId)
            {
                _guidedDeploymentInspection = inspection;
                _guidedDeploymentPackageProjectId = package.Entry.Manifest.PackageProjectId;
                _guidedDeploymentRevisionId = deploymentRevision.RevisionId;
                UpdateGuidedTextureModActionState();
                var earlierRevisionActive = HasActiveDeployment(inspection) &&
                                            !deploymentRevision.RevisionId.Equals(
                                                latest.RevisionId, StringComparison.OrdinalIgnoreCase);
                var cleanLineageRoot = !HasActiveDeployment(inspection) &&
                                       inspection.State is
                                           ModInstallationState.NotInstalled or ModInstallationState.Restored &&
                                       !deploymentRevision.RevisionId.Equals(
                                           latest.RevisionId, StringComparison.OrdinalIgnoreCase);
                GuidedBuildStatusText.Text = earlierRevisionActive
                    ? $"An earlier package revision ({deploymentRevision.RevisionId}) remains installed. Restore it before deleting or rebuilding this project."
                    : cleanLineageRoot
                        ? "The byte-exact clean project-root package is restored. Later descendant builds are not installed; rebuild before installing again."
                    : latest.PreviewOnly
                    ? "This older variable-size revision passed preview validation but remains locked. Rebuild it to run the second-batch controlled deployment gate."
                    : latest.ContainsVariableSizeMeshReplacements && latest.VariableSizeDeploymentApproved
                        ? $"Variable-size deployment gate passed. {inspection.Summary}"
                        : inspection.Summary;
                Log(
                    $"Guided package deployment state retained: " +
                    $"packageId={package.Entry.Manifest.PackageProjectId}, revision={deploymentRevision.RevisionId}, " +
                    $"latestRevision={latest.RevisionId}, " +
                    $"state={inspection.State}, canInstall={inspection.CanInstall}, " +
                    $"canRestore={inspection.CanRestore}, target={inspection.TargetPath}.");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            ClearGuidedDeploymentInspection();
            UpdateGuidedTextureModActionState();
            GuidedBuildStatusText.Text = $"The latest UPK is retained, but install preflight needs attention: {exception.Message}";
            Log($"Guided package-mod deployment inspection failed: package={package.Entry.ManifestPath}, error={exception}");
        }
    }

    private void ClearGuidedDeploymentInspection()
    {
        _guidedDeploymentInspection = null;
        _guidedDeploymentPackageProjectId = null;
        _guidedDeploymentRevisionId = null;
    }

    private async void GuidedAssetIncludeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: GuidedTextureModRow row }) return;
        try
        {
            await _guidedTextureModWorkspace.SetIncludedAsync(row.Entry.ManifestPath, row.Included);
            Log(
                $"Guided texture package selection changed: projectId={row.Entry.Manifest.ProjectId}, " +
                $"object={row.ObjectName}, included={row.Included}.");
            await RefreshGuidedTextureModsAsync("package asset selection changed", row.Entry.Manifest.ProjectId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Guided texture package selection failed: project={row.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Select package asset", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void GuidedMeshIncludeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: GuidedMeshModRow row }) return;
        try
        {
            if (row.Included && GuidedPackagesGrid.SelectedItem is GuidedTexturePackageRow package)
            {
                foreach (var competing in package.MeshAssets.Where(asset =>
                             asset.Entry.Manifest.SourceExportId.Equals(
                                 row.Entry.Manifest.SourceExportId, StringComparison.OrdinalIgnoreCase) &&
                             !asset.Entry.Manifest.EditId.Equals(
                                 row.Entry.Manifest.EditId, StringComparison.OrdinalIgnoreCase) &&
                             asset.Included && asset.OwnerModName is null))
                {
                    await _skeletalMeshEditWorkspaceStore.SetIncludedAsync(
                        competing.Entry, false);
                    Log($"Guided mesh variant automatically unchecked: " +
                        $"selected={row.Entry.Manifest.EditId}, excluded={competing.Entry.Manifest.EditId}, " +
                        $"sourceExport={row.Entry.Manifest.SourceExportId}.");
                }
            }
            await _skeletalMeshEditWorkspaceStore.SetIncludedAsync(row.Entry, row.Included);
            Log(
                $"Guided mesh package selection changed: editId={row.Entry.Manifest.EditId}, " +
                $"object={row.ObjectName}, included={row.Included}.");
            await RefreshGuidedTextureModsAsync(
                "package mesh selection changed", row.Entry.Manifest.EditId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Guided mesh package selection failed: edit={row.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Select package mesh",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void GuidedMaterialIncludeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: GuidedMaterialModRow row }) return;
        try
        {
            if (row.Included && GuidedPackagesGrid.SelectedItem is GuidedTexturePackageRow package)
            {
                foreach (var competing in package.MaterialAssets.Where(asset =>
                             asset.Entry.Document.ExportIndex == row.Entry.Document.ExportIndex &&
                             !asset.Entry.Document.EditId.Equals(
                                 row.Entry.Document.EditId, StringComparison.OrdinalIgnoreCase) &&
                             asset.Included && asset.OwnerModName is null))
                    await _materialEditWorkspaceStore.SetIncludedAsync(competing.Entry, false);
            }
            await _materialEditWorkspaceStore.SetIncludedAsync(row.Entry, row.Included);
            Log($"Guided material package selection changed: editId={row.Entry.Document.EditId}, " +
                $"object={row.ObjectName}, included={row.Included}.");
            await RefreshMaterialProjectsAsync("package material selection changed", row.Entry.Document.EditId);
            await RefreshGuidedTextureModsAsync("package material selection changed", row.Entry.Document.EditId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Guided material package selection failed: edit={row.Entry.DocumentPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Select package material",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void GuidedStaticMeshIncludeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: GuidedStaticMeshModRow row }) return;
        try
        {
            if (row.Included && GuidedPackagesGrid.SelectedItem is GuidedTexturePackageRow package)
            {
                foreach (var competing in package.StaticMeshAssets.Where(asset =>
                             asset.Source.NativeExportIndex == row.Source.NativeExportIndex &&
                             !asset.Entry.Manifest.EditId.Equals(
                                 row.Entry.Manifest.EditId, StringComparison.OrdinalIgnoreCase) &&
                             asset.Included && asset.OwnerModName is null))
                    await _staticMeshEditWorkspaceStore.SetIncludedAsync(competing.Entry, false);
            }
            await _staticMeshEditWorkspaceStore.SetIncludedAsync(row.Entry, row.Included);
            Log($"Guided StaticMesh package selection changed: editId={row.Entry.Manifest.EditId}, " +
                $"object={row.ObjectName}, included={row.Included}.");
            await RefreshGuidedTextureModsAsync(
                "package StaticMesh selection changed", row.Entry.Manifest.EditId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Guided StaticMesh selection failed: edit={row.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Select package StaticMesh",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void GuidedMeshModsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateGuidedTextureModActionState();
        if (GuidedMeshModsGrid.SelectedItems.Count == 1 &&
            GuidedMeshModsGrid.SelectedItem is GuidedMeshModRow row)
        {
            GuidedImpactText.Text =
                $"Impact: global on this client — every actor using SkeletalMesh {row.ObjectName} will use this edit.";
        }
    }

    private void GuidedMaterialModsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateGuidedTextureModActionState();
        if (GuidedMaterialModsGrid.SelectedItems.Count == 1 &&
            GuidedMaterialModsGrid.SelectedItem is GuidedMaterialModRow row)
            GuidedImpactText.Text =
                $"Impact: global on this client — every material using {row.ObjectName} will use these parameter values.";
    }

    private void GuidedStaticMeshModsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateGuidedTextureModActionState();
        if (GuidedStaticMeshModsGrid.SelectedItems.Count == 1 &&
            GuidedStaticMeshModsGrid.SelectedItem is GuidedStaticMeshModRow row)
            GuidedImpactText.Text =
                $"Impact: global on this client — every object using StaticMesh {row.ObjectName} will use this edit. {row.Collision}.";
    }

    private void GuidedGoToMeshEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (GuidedMeshModsGrid.SelectedItem is not GuidedMeshModRow selected) return;
        var source = _skeletalMeshEditableExports.FirstOrDefault(row =>
            row.Entry.Manifest.ExportId.Equals(
                selected.Entry.Manifest.SourceExportId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            MessageBox.Show(this,
                "The editable source export for this mesh is unavailable. Refresh Mesh recon or export the clean mesh again.",
                "Go to mesh edit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SkeletalMeshReconTab.IsSelected = true;
        SkeletalMeshEditableExportsTab.IsSelected = true;
        SkeletalMeshEditableExportsGrid.SelectedItem = source;
        SkeletalMeshEditableExportsGrid.ScrollIntoView(source);
        DisplayEditedSkeletalMeshesForSelectedExport(selected.Entry.Manifest.EditId);
        StatusText.Text = $"Selected the {selected.ObjectName} mesh edit.";
    }

    private void GuidedGoToMaterialEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (GuidedMaterialModsGrid.SelectedItem is not GuidedMaterialModRow selected) return;
        MaterialEditorTab.IsSelected = true;
        var row = _materialProjectRows.FirstOrDefault(candidate => candidate.Entry.Document.EditId.Equals(
            selected.Entry.Document.EditId, StringComparison.OrdinalIgnoreCase));
        MaterialProjectsGrid.SelectedItem = row;
        if (row is not null) MaterialProjectsGrid.ScrollIntoView(row);
        StatusText.Text = $"Selected the {selected.ObjectName} material project.";
    }

    private void GuidedProjectFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (GuidedPackagesGrid.SelectedItem is GuidedTexturePackageRow package)
        {
            ApplyGuidedProjectFilter(package);
        }
    }

    private void GuidedProjectFilterChanged(object sender, RoutedEventArgs e)
    {
        if (GuidedPackagesGrid.SelectedItem is GuidedTexturePackageRow package)
        {
            ApplyGuidedProjectFilter(package);
        }
    }

    private void ApplyGuidedProjectFilter(GuidedTexturePackageRow package)
    {
        var query = GuidedProjectFilterTextBox?.Text?.Trim() ?? string.Empty;
        var favoritesOnly = GuidedFavoritesOnlyCheckBox?.IsChecked == true;
        _guidedVisibleAssets = package.Assets
            .Where(row => !favoritesOnly || row.Entry.Manifest.IsFavorite)
            .Where(row => string.IsNullOrWhiteSpace(query) || AssetSearch.Matches(
                query, row.DisplayName, row.ObjectName, row.ImpactSummary, row.State))
            .ToArray();
        var previousId = (GuidedTextureModsGrid.SelectedItem as GuidedTextureModRow)?
            .Entry.Manifest.ProjectId;
        GuidedTextureModsGrid.ItemsSource = _guidedVisibleAssets;
        var selected = _guidedVisibleAssets.FirstOrDefault(row => row.Entry.Manifest.ProjectId.Equals(
                           previousId, StringComparison.OrdinalIgnoreCase)) ??
                       _guidedVisibleAssets.FirstOrDefault();
        GuidedTextureModsGrid.SelectedItem = selected;
        _guidedVisibleMeshes = package.MeshAssets
            .Where(row => string.IsNullOrWhiteSpace(query) || AssetSearch.Matches(
                query, row.DisplayName, row.ObjectName, row.State))
            .ToArray();
        var previousMeshId = (GuidedMeshModsGrid.SelectedItem as GuidedMeshModRow)?
            .Entry.Manifest.EditId;
        GuidedMeshModsGrid.ItemsSource = _guidedVisibleMeshes;
        GuidedMeshModsGrid.SelectedItem = _guidedVisibleMeshes.FirstOrDefault(row =>
            row.Entry.Manifest.EditId.Equals(previousMeshId, StringComparison.OrdinalIgnoreCase)) ??
                                         _guidedVisibleMeshes.FirstOrDefault();
        _guidedVisibleStaticMeshes = package.StaticMeshAssets
            .Where(row => string.IsNullOrWhiteSpace(query) || AssetSearch.Matches(
                query, row.DisplayName, row.ObjectName, row.State, row.Collision))
            .ToArray();
        var previousStaticId = (GuidedStaticMeshModsGrid.SelectedItem as GuidedStaticMeshModRow)?
            .Entry.Manifest.EditId;
        GuidedStaticMeshModsGrid.ItemsSource = _guidedVisibleStaticMeshes;
        GuidedStaticMeshModsGrid.SelectedItem = _guidedVisibleStaticMeshes.FirstOrDefault(row =>
            row.Entry.Manifest.EditId.Equals(previousStaticId, StringComparison.OrdinalIgnoreCase)) ??
                                              _guidedVisibleStaticMeshes.FirstOrDefault();
        _guidedVisibleMaterials = package.MaterialAssets
            .Where(row => string.IsNullOrWhiteSpace(query) || AssetSearch.Matches(
                query, row.DisplayName, row.ObjectName, row.State))
            .ToArray();
        var previousMaterialId = (GuidedMaterialModsGrid.SelectedItem as GuidedMaterialModRow)?
            .Entry.Document.EditId;
        GuidedMaterialModsGrid.ItemsSource = _guidedVisibleMaterials;
        GuidedMaterialModsGrid.SelectedItem = _guidedVisibleMaterials.FirstOrDefault(row =>
            row.Entry.Document.EditId.Equals(previousMaterialId, StringComparison.OrdinalIgnoreCase)) ??
                                             _guidedVisibleMaterials.FirstOrDefault();
        GuidedBuildStatusText.Text =
            $"Showing {_guidedVisibleAssets.Count:N0} texture, {_guidedVisibleMaterials.Count:N0} material, " +
            $"{_guidedVisibleMeshes.Count:N0} skeletal mesh, and {_guidedVisibleStaticMeshes.Count:N0} StaticMesh project(s); " +
            $"{package.IncludedAssetCount:N0} asset(s) checked for the next UPK build.";
        UpdateGuidedTextureModActionState();
    }

    private async void GuidedCheckVisibleButton_Click(object sender, RoutedEventArgs e) =>
        await SetVisibleGuidedProjectsIncludedAsync(true);

    private async void GuidedUncheckVisibleButton_Click(object sender, RoutedEventArgs e) =>
        await SetVisibleGuidedProjectsIncludedAsync(false);

    private async Task SetVisibleGuidedProjectsIncludedAsync(bool included)
    {
        if (_guidedTextureModInProgress ||
            (_guidedVisibleAssets.Count == 0 && _guidedVisibleMeshes.Count == 0 &&
             _guidedVisibleStaticMeshes.Count == 0 && _guidedVisibleMaterials.Count == 0)) return;
        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        try
        {
            foreach (var row in _guidedVisibleAssets.Where(row => row.CanToggleInclude))
            {
                await _guidedTextureModWorkspace.SetIncludedAsync(row.Entry.ManifestPath, included);
            }
            foreach (var row in _guidedVisibleMeshes.Where(row => row.CanToggleInclude))
            {
                await _skeletalMeshEditWorkspaceStore.SetIncludedAsync(row.Entry, included);
            }
            foreach (var row in _guidedVisibleStaticMeshes.Where(row => row.CanToggleInclude))
                await _staticMeshEditWorkspaceStore.SetIncludedAsync(row.Entry, included);
            foreach (var row in _guidedVisibleMaterials.Where(row => row.CanToggleInclude))
                await _materialEditWorkspaceStore.SetIncludedAsync(row.Entry, included);
            Log(
                $"Guided visible project selection changed: included={included}, " +
                $"textures={_guidedVisibleAssets.Count}, materials={_guidedVisibleMaterials.Count}, skeletalMeshes={_guidedVisibleMeshes.Count}, staticMeshes={_guidedVisibleStaticMeshes.Count}, " +
                $"objects={string.Join(",", _guidedVisibleAssets.Select(row => row.ObjectName).Concat(_guidedVisibleMaterials.Select(row => row.ObjectName)).Concat(_guidedVisibleMeshes.Select(row => row.ObjectName)).Concat(_guidedVisibleStaticMeshes.Select(row => row.ObjectName)))}.");
            await RefreshGuidedTextureModsAsync(
                included ? "checked visible projects" : "unchecked visible projects",
                _guidedVisibleAssets.FirstOrDefault()?.Entry.Manifest.ProjectId ??
                _guidedVisibleMaterials.FirstOrDefault()?.Entry.Document.EditId ??
                _guidedVisibleMeshes.FirstOrDefault()?.Entry.Manifest.EditId ??
                _guidedVisibleStaticMeshes.FirstOrDefault()?.Entry.Manifest.EditId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Guided visible project selection failed: included={included}, error={exception}");
            MessageBox.Show(this, exception.Message, "Update checked projects", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
        }
    }

    private void UpdateGuidedTextureModActionState()
    {
        if (GuidedOpenEditableButton is null) return;
        var selectedPackages = GuidedPackagesGrid.SelectedItems
            .OfType<GuidedTexturePackageRow>()
            .ToArray();
        var activePackageCount = _guidedPackageRows.Count(HasActiveGuidedDeployment);
        var selectedAssets = GuidedTextureModsGrid.SelectedItems
            .OfType<GuidedTextureModRow>()
            .ToArray();
        var selectedMeshes = GuidedMeshModsGrid.SelectedItems
            .OfType<GuidedMeshModRow>()
            .ToArray();
        var selectedMaterials = GuidedMaterialModsGrid.SelectedItems
            .OfType<GuidedMaterialModRow>()
            .ToArray();
        var asset = selectedAssets.Length == 1 ? selectedAssets[0] : null;
        var package = GuidedPackagesGrid.SelectedItem as GuidedTexturePackageRow;
        var singleSelected = asset is not null;
        var hasEditable = singleSelected && File.Exists(asset!.Entry.Manifest.EditableImagePath);
        var hasBuild = package?.LatestRevision is { } latest &&
                       File.Exists(latest.OutputUpkPath) && File.Exists(latest.ModBundleManifestPath) &&
                       (File.Exists(latest.CompositionManifestPath) ||
                        File.Exists(latest.FinalReplacementUpkBuildManifestPath));
        GuidedOpenEditableButton.IsEnabled = !_guidedTextureModInProgress && hasEditable;
        GuidedCompareEditButton.IsEnabled = !_guidedTextureModInProgress && hasEditable;
        GuidedFavoriteAssetButton.IsEnabled = !_guidedTextureModInProgress && singleSelected;
        GuidedSaveMetadataButton.IsEnabled = !_guidedTextureModInProgress && package is not null;
        GuidedFavoritePackageButton.IsEnabled = !_guidedTextureModInProgress && package is not null;
        GuidedInspectSourcePackageButton.IsEnabled = !_guidedTextureModInProgress &&
                                                     package is not null && _umodelBridge is not null &&
                                                     File.Exists(package.Entry.Manifest.SourcePackagePath);
        GuidedInspectSelectedAssetButton.IsEnabled = !_guidedTextureModInProgress && singleSelected &&
                                                     _umodelBridge is not null;
        GuidedInspectSelectedMaterialButton.IsEnabled = !_guidedTextureModInProgress &&
                                                        selectedMaterials.Length == 1 && _umodelBridge is not null;
        GuidedModelPreviewComboBox.IsEnabled = !_guidedTextureModInProgress && singleSelected &&
                                               GuidedModelPreviewComboBox.Items.Count > 0;
        GuidedPreviewOnModelButton.IsEnabled = !_guidedTextureModInProgress && singleSelected &&
                                               _umodelBridge is not null &&
                                               _guidedTexturePackageBuildService is not null &&
                                               GuidedModelPreviewComboBox.SelectedItem is GuidedModelPreviewCandidate;
        GuidedResetAssetButton.IsEnabled = !_guidedTextureModInProgress && hasEditable;
        GuidedBuildButton.IsEnabled = !_guidedTextureModInProgress && package is not null &&
                                      package.IncludedAssetCount > 0 &&
                                      package.MaterialAssets.Where(item => item.Included).All(item =>
                                          !string.IsNullOrWhiteSpace(item.Entry.Document.LatestCandidateManifestPath) &&
                                          File.Exists(item.Entry.Document.LatestCandidateManifestPath)) &&
                                      _unifiedPackageComposerService is not null;
        GuidedBuildSelectedButton.Content = selectedPackages.Length <= 1
            ? "Rebuild selected"
            : $"Rebuild selected ({selectedPackages.Length:N0})";
        GuidedBuildSelectedButton.IsEnabled = !_guidedTextureModInProgress && !_modSetBusy &&
                                              _unifiedPackageComposerService is not null &&
                                              selectedPackages.Length > 0;
        GuidedSaveArkiveButton.IsEnabled = !_guidedTextureModInProgress && package is not null &&
                                           package.AllAssets.Any(item => item.Included && item.CanToggleInclude);
        GuidedCreateFaceMapButton.IsEnabled = !_guidedTextureModInProgress &&
                                              package is not null &&
                                              _nativeTextureExportService is not null &&
                                              package.Assets.Any(asset =>
                                                  TryGetFaceMapPrefix(asset.ObjectName, out _));
        GuidedInspectButton.IsEnabled = !_guidedTextureModInProgress && hasBuild && _umodelBridge is not null;
        var deploymentRevision = package?.DeploymentRevision;
        var deploymentStateIsCurrent = package is not null && deploymentRevision is not null &&
                                       _guidedDeploymentInspection is not null &&
                                       package.Entry.Manifest.PackageProjectId.Equals(
                                           _guidedDeploymentPackageProjectId,
                                           StringComparison.OrdinalIgnoreCase) &&
                                       deploymentRevision.RevisionId.Equals(
                                           _guidedDeploymentRevisionId,
                                           StringComparison.OrdinalIgnoreCase);
        var deploymentStateIsLatest = deploymentStateIsCurrent && package?.LatestRevision is { } latestRevision &&
                                      deploymentRevision!.RevisionId.Equals(
                                          latestRevision.RevisionId, StringComparison.OrdinalIgnoreCase);
        GuidedInstallButton.IsEnabled = !_guidedTextureModInProgress && hasBuild &&
                                        deploymentStateIsLatest &&
                                        package is not null &&
                                        package.LatestRevision?.PreviewOnly != true &&
                                        (package.LatestRevision?.ContainsVariableSizeMeshReplacements != true ||
                                         package.LatestRevision.VariableSizeDeploymentApproved) &&
                                        !package.BuildOutdated &&
                                        _guidedDeploymentInspection!.CanInstall;
        GuidedRestoreButton.IsEnabled = !_guidedTextureModInProgress && hasBuild &&
                                        deploymentStateIsCurrent &&
                                        _guidedDeploymentInspection!.CanRestore;
        GuidedInstallSelectedButton.Content = selectedPackages.Length <= 1
            ? "Install selected"
            : $"Install selected ({selectedPackages.Length:N0})";
        GuidedInstallSelectedButton.IsEnabled = !_guidedTextureModInProgress && !_modSetBusy &&
                                                selectedPackages.Length > 0;
        GuidedRestoreAllButton.Content = activePackageCount == 0
            ? "Restore all active"
            : $"Restore all active ({activePackageCount:N0})";
        GuidedRestoreAllButton.IsEnabled = !_guidedTextureModInProgress && !_modSetBusy &&
                                           activePackageCount > 0;
        GuidedDeletePackageButton.IsEnabled = !_guidedTextureModInProgress && package is not null;
        GuidedCleanBuildsButton.IsEnabled = !_guidedTextureModInProgress &&
                                            package?.Entry.Manifest.Revisions.Count > 1;
        GuidedCleanPreviewsButton.IsEnabled = !_guidedTextureModInProgress;
        GuidedCopyDiagnosticsButton.IsEnabled = package is not null;
        GuidedCheckClientUpdateButton.IsEnabled = !_guidedTextureModInProgress && package is not null &&
                                                  File.Exists(package.Entry.Manifest.SourcePackagePath);
        GuidedRebaseClientUpdateButton.IsEnabled = !_guidedTextureModInProgress &&
                                                   _clientUpdateRebaseService is not null && package is not null &&
                                                   _selectedClientCompatibility?.PackageProjectId ==
                                                   package.Entry.Manifest.PackageProjectId &&
                                                   _selectedClientCompatibility.CanAutomaticallyRebase;
        GuidedOpenFolderButton.IsEnabled = !_guidedTextureModInProgress && singleSelected;
        GuidedDeleteButton.IsEnabled = !_guidedTextureModInProgress && selectedAssets.Length > 0;
        GuidedGoToMeshEditButton.IsEnabled = !_guidedTextureModInProgress && selectedMeshes.Length == 1;
        GuidedGoToMaterialEditButton.IsEnabled = !_guidedTextureModInProgress && selectedMaterials.Length == 1;
        UpdateGuidedNextAction(package, asset, deploymentStateIsCurrent);
        UpdateModLibraryActionState();
    }

    private void UpdateGuidedNextAction(
        GuidedTexturePackageRow? package,
        GuidedTextureModRow? asset,
        bool deploymentStateIsCurrent)
    {
        _guidedNextAction = GuidedNextAction.None;
        GuidedNextActionButton.IsEnabled = false;
        GuidedNextActionButton.Content = "Continue";
        if (_guidedTextureModInProgress)
        {
            GuidedNextActionText.Text = "Arkive is completing the current operation…";
            return;
        }
        if (package is null)
        {
            GuidedNextActionText.Text = "Select a modified UPK and an asset project to continue.";
            return;
        }
        if (deploymentStateIsCurrent && _guidedDeploymentInspection?.CanRestore == true)
        {
            GuidedNextActionText.Text =
                $"{package.DisplayName} is installed. Edits made now require restoring and rebuilding before installation.";
            GuidedNextActionButton.Content = "Restore original";
            GuidedNextActionButton.IsEnabled = true;
            _guidedNextAction = GuidedNextAction.Restore;
            return;
        }
        if (package.BuildOutdated)
        {
            GuidedNextActionText.Text =
                "One or more checked texture, material, or mesh edits are not in the latest UPK. Rebuild the checked assets next.";
            GuidedNextActionButton.Content = "Rebuild now";
            GuidedNextActionButton.IsEnabled = GuidedBuildButton.IsEnabled;
            _guidedNextAction = GuidedNextAction.Build;
            return;
        }
        if (package.LatestRevision?.PreviewOnly == true)
        {
            GuidedNextActionText.Text =
                "This older variable-size revision remains preview-only. Rebuild the checked assets to run the second-batch controlled deployment gate.";
            GuidedNextActionButton.Content = "Rebuild for install";
            GuidedNextActionButton.IsEnabled = GuidedBuildButton.IsEnabled;
            _guidedNextAction = GuidedNextAction.Build;
            return;
        }
        if (deploymentStateIsCurrent && _guidedDeploymentInspection?.CanInstall == true)
        {
            GuidedNextActionText.Text =
                "The latest checked edits are built and verified. Review the impact summary, then install the mod.";
            GuidedNextActionButton.Content = "Install now";
            GuidedNextActionButton.IsEnabled = true;
            _guidedNextAction = GuidedNextAction.Install;
            return;
        }
        if (asset is not null && File.Exists(asset.Entry.Manifest.EditableImagePath))
        {
            GuidedNextActionText.Text = "Open the selected editable image, save your changes, then return to Arkive.";
            GuidedNextActionButton.Content = "Open editable image";
            GuidedNextActionButton.IsEnabled = true;
            _guidedNextAction = GuidedNextAction.OpenEdit;
            return;
        }
        GuidedNextActionText.Text = package.LatestRevision is null
            ? "Select a texture or skeletal-mesh project to edit."
            : "The current package is verified; choose an asset project or an advanced action.";
    }

}
