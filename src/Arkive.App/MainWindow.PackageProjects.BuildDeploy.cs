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
    private async void GuidedBuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (GuidedPackagesGrid.SelectedItem is GuidedTexturePackageRow package)
            await RunGuidedPackageBuildsAsync([package], batchRequested: false);
    }

    private async void GuidedBuildSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var packages = GuidedPackagesGrid.SelectedItems
            .OfType<GuidedTexturePackageRow>()
            .GroupBy(package => package.Entry.Manifest.PackageProjectId,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        await RunGuidedPackageBuildsAsync(packages, batchRequested: true);
    }

    private static int GuidedPackageBuildStepCount(GuidedTexturePackageRow package) =>
        package.IncludedTextureCount * 9 + package.IncludedMaterialCount * 2 +
        (package.IncludedMeshCount + package.IncludedStaticMeshCount) * 5 + 2;

    private async Task<string?> GetGuidedPackageBuildReadinessProblemAsync(
        GuidedTexturePackageRow package)
    {
        if (package.IncludedAssetCount == 0)
            return "has no checked texture, material, or mesh projects";
        if (!File.Exists(package.Entry.Manifest.SourcePackagePath))
            return "is missing its clean source UPK";
        var incompleteMaterial = package.MaterialAssets.FirstOrDefault(material =>
            material.Included &&
            (string.IsNullOrWhiteSpace(material.Entry.Document.LatestCandidateManifestPath) ||
             !File.Exists(material.Entry.Document.LatestCandidateManifestPath)));
        if (incompleteMaterial is not null)
            return $"material {incompleteMaterial.ObjectName} has no saved replacement candidate";

        foreach (var revision in package.Entry.Manifest.Revisions
                     .OrderByDescending(revision => revision.CreatedAt))
        {
            if (!File.Exists(revision.ModBundleManifestPath)) continue;
            try
            {
                var inspection = await _modDeploymentService.InspectForRecoveryAsync(
                    CreateModDeploymentRequest(revision.ModBundleManifestPath));
                if (inspection.HasActiveDeployment)
                    return "is currently installed; restore its original package before rebuilding";
                if (inspection.State == ModInstallationState.Conflict)
                    return $"has an unresolved deployment conflict: {inspection.Summary}";
                if (inspection.State == ModInstallationState.MissingTarget)
                    return "cannot find its target game package";
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException or
                    ArgumentException or JsonException)
            {
                Log($"Guided package rebuild install-state preflight failed closed: " +
                    $"package={package.Entry.ManifestPath}, revision={revision.RevisionId}, error={exception}");
                return $"could not verify that revision {revision.RevisionId} is safely uninstalled: {exception.Message}";
            }
        }
        return null;
    }

    private sealed record GuidedPackageBuildOutcome(
        GuidedTexturePackageRow Package,
        UnifiedPackageCompositionResult? Result,
        string? Error)
    {
        public bool Succeeded => Result is not null && Error is null;
    }

    private async Task<GuidedPackageBuildOutcome> BuildGuidedPackageAsync(
        GuidedTexturePackageRow package,
        int packageNumber,
        int packageCount,
        int stepOffset,
        int overallSteps)
    {
        var selectedTextures = package.Assets.Where(asset => asset.Included)
            .Select(asset => asset.Entry).ToArray();
        var selectedMeshes = package.MeshAssets.Where(asset => asset.Included)
            .Select(asset => asset.Entry).ToArray();
        var selectedMaterials = package.MaterialAssets.Where(asset => asset.Included)
            .Select(asset => asset.Entry).ToArray();
        var selectedStaticMeshes = package.StaticMeshAssets.Where(asset => asset.Included)
            .Select(asset => asset.Entry).ToArray();
        var revisionDirectory = string.Empty;
        var progress = new Progress<UnifiedPackageBuildProgress>(update =>
        {
            GuidedBuildProgressBar.Value = Math.Min(overallSteps, stepOffset + update.Step);
            GuidedBuildStatusText.Text = packageCount > 1
                ? $"Package {packageNumber:N0} of {packageCount:N0} · overall step " +
                  $"{Math.Min(overallSteps, stepOffset + update.Step):N0} of {overallSteps:N0} · " +
                  $"{update.ObjectName}: {update.Message}"
                : $"Step {update.Step} of {update.TotalSteps} · {update.ObjectName}: {update.Message}";
            StatusText.Text = update.Message;
            Log(
                $"Unified package build progress: packageId={package.Entry.Manifest.PackageProjectId}, " +
                $"batchPackage={packageNumber}/{packageCount}, kind={update.Stage}, object={update.ObjectName}, " +
                $"step={update.Step}/{update.TotalSteps}, overallStep={stepOffset + update.Step}/{overallSteps}, " +
                $"message={update.Message}");
        });
        try
        {
            revisionDirectory = _guidedTexturePackageWorkspace.CreateRevisionDirectory(package.Entry);
            Log(
                $"Unified package composition started: packageId={package.Entry.Manifest.PackageProjectId}, " +
                $"batchPackage={packageNumber}/{packageCount}, package={package.Entry.Manifest.SourcePackagePath}, " +
                $"textures={selectedTextures.Length}, materials={selectedMaterials.Length}, skeletalMeshes={selectedMeshes.Length}, staticMeshes={selectedStaticMeshes.Length}, " +
                $"objects={string.Join(",", selectedTextures.Select(asset => asset.Manifest.ObjectName).Concat(selectedMaterials.Select(asset => asset.Document.ObjectName)).Concat(selectedMeshes.Select(asset => asset.Manifest.ObjectName)).Concat(selectedStaticMeshes.Select(asset => asset.Manifest.DisplayName)))}, " +
                $"revision={revisionDirectory}. Textures precede bounded materials and rebased skeletal/static meshes; topology replacements are appended and retargeted. The final original-name payload receives exact-object validation, and variable-size builds must pass the controlled backup/install/restore gate.");
            var result = await _unifiedPackageComposerService!.BuildAsync(
                new UnifiedPackageCompositionRequest(
                    package.Entry, selectedTextures, selectedMeshes, revisionDirectory,
                    Path.Combine(_projectRoot, "backups", "packages"), package.DisplayName)
                {
                    MaterialEdits = selectedMaterials,
                    StaticMeshEdits = selectedStaticMeshes,
                },
                progress);
            var packageProject = await _guidedTexturePackageWorkspace.CommitRevisionAsync(
                package.Entry.ManifestPath, result.Revision);
            Log(
                $"Unified package composition completed in {result.Duration.TotalSeconds:N3}s: " +
                $"packageId={packageProject.Manifest.PackageProjectId}, batchPackage={packageNumber}/{packageCount}, " +
                $"textures={result.Revision.Assets.Count}, materials={result.Revision.MaterialAssets.Count}, " +
                $"skeletalMeshes={result.Revision.MeshAssets.Count}, staticMeshes={result.Revision.StaticMeshAssets.Count}, revision={result.Revision.RevisionId}, " +
                $"upk={result.Revision.OutputUpkPath}, bundle={result.Revision.ModBundleManifestPath}, " +
                $"composition={result.CompositionManifestPath}, stages={result.Manifest.Stages.Count}, " +
                $"variableSizeMeshes={result.Manifest.VariableSizeMeshCount}, previewOnly={result.Manifest.PreviewOnly}, " +
                $"variableSizeDeploymentApproved={result.Manifest.VariableSizeDeploymentGatePassed}, " +
                $"variableSizeGate={result.Manifest.VariableSizeDeploymentGateManifestPath ?? "none"}, " +
                $"externalValidation={result.Manifest.ExactObjectExternalValidationPassed}, " +
                $"payloadBytes={result.Revision.PayloadBytes}, payloadSha={result.Revision.PayloadSha256}.");
            return new GuidedPackageBuildOutcome(package, result, null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or TimeoutException or System.ComponentModel.Win32Exception or JsonException)
        {
            Log(
                $"Unified package composition failed: packageId={package.Entry.Manifest.PackageProjectId}, " +
                $"batchPackage={packageNumber}/{packageCount}, textures={selectedTextures.Length}, " +
                $"materials={selectedMaterials.Length}, meshes={selectedMeshes.Length}, " +
                $"revision={revisionDirectory}, error={exception}");
            try
            {
                if (!string.IsNullOrWhiteSpace(revisionDirectory))
                {
                    _guidedTexturePackageWorkspace.DeleteFailedRevisionDirectory(revisionDirectory);
                    Log($"Guided failed package revision cleaned: revision={revisionDirectory}.");
                }
            }
            catch (Exception cleanupException) when (
                cleanupException is IOException or UnauthorizedAccessException)
            {
                Log(
                    $"Guided failed package revision cleanup failed: revision={revisionDirectory}, " +
                    $"error={cleanupException}");
            }
            return new GuidedPackageBuildOutcome(package, null, exception.Message);
        }
    }

    private async Task RunGuidedPackageBuildsAsync(
        IReadOnlyList<GuidedTexturePackageRow> requestedPackages,
        bool batchRequested)
    {
        if (_guidedTextureModInProgress || _modSetBusy || _unifiedPackageComposerService is null) return;
        var packages = requestedPackages
            .GroupBy(package => package.Entry.Manifest.PackageProjectId,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (packages.Length == 0)
        {
            MessageBox.Show(this,
                "Select one or more Package Projects first. Use Ctrl or Shift to select multiple rows.",
                "Rebuild selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        UpdateLibraryActionState();
        UpdateEntityBrowserAssetActions();
        try
        {
            GuidedBuildStatusText.Text = $"Preflighting {packages.Length:N0} Package Project(s) before rebuilding...";
            var readinessProblems = new List<(GuidedTexturePackageRow Package, string Problem)>();
            foreach (var package in packages)
            {
                var problem = await GetGuidedPackageBuildReadinessProblemAsync(package);
                if (problem is not null) readinessProblems.Add((package, problem));
            }
            if (readinessProblems.Count > 0)
            {
                var detail = string.Join("\n", readinessProblems.Take(20).Select(item =>
                    $"  • {item.Package.DisplayName}: {item.Problem}."));
                if (readinessProblems.Count > 20)
                    detail += $"\n  • …and {readinessProblems.Count - 20:N0} more";
                GuidedBuildStatusText.Text =
                    "Batch rebuild was blocked before creating any package revision. Resolve the listed projects and retry.";
                StatusText.Text = "Package rebuild preflight needs attention.";
                Log($"Guided package rebuild preflight refused: requested={packages.Length}, " +
                    $"problems={string.Join(" | ", readinessProblems.Select(item => item.Package.Entry.Manifest.PackageProjectId + ":" + item.Problem))}.");
                MessageBox.Show(this,
                    $"Arkive did not rebuild any Package Project:\n\n{detail}\n\n" +
                    "Restore installed packages or resolve the listed project state, then try again.",
                    packages.Length > 1 ? "Rebuild selected — attention required" : "Restore before rebuilding",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (batchRequested)
            {
                var packageLines = string.Join("\n", packages.Take(16).Select(package =>
                    $"  • {package.DisplayName} — {package.IncludedAssetCount:N0} checked asset(s)"));
                if (packages.Length > 16)
                    packageLines += $"\n  • …and {packages.Length - 16:N0} more";
                if (MessageBox.Show(this,
                        $"Rebuild {packages.Length:N0} selected Package Project(s)?\n\n{packageLines}\n\n" +
                        "Each project will run the complete native composition and validation pipeline. " +
                        "Successful independent builds are retained if a later project fails; nothing is installed automatically.",
                        "Rebuild selected", MessageBoxButton.YesNo, MessageBoxImage.Question,
                        MessageBoxResult.No) != MessageBoxResult.Yes) return;
            }

            var totalSteps = packages.Sum(GuidedPackageBuildStepCount);
            GuidedBuildProgressBar.Value = 0;
            GuidedBuildProgressBar.Maximum = Math.Max(1, totalSteps);
            Log($"Guided package rebuild batch started: requested={packages.Length}, totalSteps={totalSteps}, " +
                $"packageIds={string.Join(",", packages.Select(package => package.Entry.Manifest.PackageProjectId))}.");
            var outcomes = new List<GuidedPackageBuildOutcome>(packages.Length);
            var stepOffset = 0;
            for (var index = 0; index < packages.Length; index++)
            {
                var package = packages[index];
                var outcome = await BuildGuidedPackageAsync(
                    package, index + 1, packages.Length, stepOffset, totalSteps);
                outcomes.Add(outcome);
                stepOffset += GuidedPackageBuildStepCount(package);
                GuidedBuildProgressBar.Value = Math.Min(totalSteps, stepOffset);
            }

            var succeeded = outcomes.Where(outcome => outcome.Succeeded).ToArray();
            var failed = outcomes.Where(outcome => !outcome.Succeeded).ToArray();

            var savedMods = new List<string>();
            foreach (var outcome in succeeded)
            {
                try
                {
                    if (await SavePackageProjectToArkiveAsync(outcome.Package.Entry) is { } mod)
                        savedMods.Add(mod.FileName);
                }
                catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
                {
                    Log($"Built package project could not be saved as an .arkive mod: " +
                        $"package={outcome.Package.Entry.Manifest.PackageProjectId}, error={exception}");
                }
            }
            if (savedMods.Count > 0)
                await ArkiveModSelectionService.SynchronizeAsync(_arkiveModLibrary, _arkiveModEdits);
            var selectProjectId = succeeded.FirstOrDefault()?.Package.Assets
                                      .FirstOrDefault(asset => asset.Included)?.Entry.Manifest.ProjectId ??
                                  succeeded.FirstOrDefault()?.Package.MaterialAssets
                                      .FirstOrDefault(asset => asset.Included)?.Entry.Document.EditId ??
                                  succeeded.FirstOrDefault()?.Package.MeshAssets
                                      .FirstOrDefault(asset => asset.Included)?.Entry.Manifest.EditId ??
                                  succeeded.FirstOrDefault()?.Package.StaticMeshAssets
                                      .FirstOrDefault(asset => asset.Included)?.Entry.Manifest.EditId;
            if (succeeded.Length > 0)
                await RefreshGuidedTextureModsAsync(
                    packages.Length > 1
                        ? "selected package compositions completed"
                        : "unified package composition completed",
                    selectProjectId);

            Log($"Guided package rebuild batch completed: requested={packages.Length}, " +
                $"succeeded={succeeded.Length}, failed={failed.Length}, " +
                $"successIds={string.Join(",", succeeded.Select(outcome => outcome.Package.Entry.Manifest.PackageProjectId))}, " +
                $"failures={string.Join(" | ", failed.Select(outcome => outcome.Package.Entry.Manifest.PackageProjectId + ":" + outcome.Error))}.");
            if (failed.Length == 0)
            {
                var singleResult = succeeded[0].Result!;
                GuidedBuildStatusText.Text = packages.Length == 1
                    ? $"Modified UPK is ready with {singleResult.Revision.Assets.Count:N0} texture(s), " +
                      $"{singleResult.Revision.MaterialAssets.Count:N0} material(s), and " +
                      $"{singleResult.Revision.MeshAssets.Count:N0} skeletal mesh(es), plus " +
                      $"{singleResult.Revision.StaticMeshAssets.Count:N0} StaticMesh(es). " +
                      (singleResult.Revision.PreviewOnly
                          ? "It passed exact-object preview validation; installation remains disabled."
                          : "You can inspect or install it.")
                    : $"All {succeeded.Length:N0} selected Package Projects rebuilt and passed validation. Nothing was installed automatically.";
                if (savedMods.Count > 0)
                    GuidedBuildStatusText.Text += $" Saved as {string.Join(", ", savedMods)} in the mods folder.";
                StatusText.Text = packages.Length == 1
                    ? $"Modified UPK ready for {succeeded[0].Package.PackageAlias}."
                    : $"Rebuilt and validated {succeeded.Length:N0} Package Projects.";
                if (batchRequested)
                    MessageBox.Show(this, GuidedBuildStatusText.Text, "Selected projects rebuilt",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                GuidedBuildStatusText.Text =
                    $"Rebuild finished: {succeeded.Length:N0} succeeded and {failed.Length:N0} failed. " +
                    "Previous verified builds remain available for failed projects.";
                StatusText.Text = "Some selected Package Projects failed validation.";
                var failureLines = string.Join("\n", failed.Take(20).Select(outcome =>
                    $"  • {outcome.Package.DisplayName}: {outcome.Error}"));
                if (failed.Length > 20) failureLines += $"\n  • …and {failed.Length - 20:N0} more";
                MessageBox.Show(this,
                    $"{succeeded.Length:N0} project(s) rebuilt successfully; {failed.Length:N0} failed.\n\n" +
                    $"Failed projects:\n{failureLines}\n\nNothing was installed automatically. See Diagnostics and the session log for exact stages.",
                    "Rebuild selected — completed with errors",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
            UpdateLibraryActionState();
            UpdateEntityBrowserAssetActions();
        }
    }

    private async void GuidedInspectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedTextureModInProgress || _umodelBridge is null ||
            GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow { LatestRevision: { } latest } package) return;
        _guidedTextureModInProgress = true;
        UpdateGuidedTextureModActionState();
        try
        {
            var outputUpkPath = await ResolveVerifiedGuidedOutputAsync(latest);
            var process = _umodelBridge.StartPreview(outputUpkPath);
            StatusText.Text = $"Opened the complete built UPK for {package.PackageAlias} in UModel.";
            Log(
                $"Guided cumulative built-UPK browser launched: " +
                $"packageId={package.Entry.Manifest.PackageProjectId}, process={process.Id}, " +
                $"textures={latest.Assets.Count}, meshes={latest.MeshAssets.Count}, upk={outputUpkPath}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or System.ComponentModel.Win32Exception)
        {
            Log($"Guided built-UPK browser failed: package={package.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Inspect built UPK", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
        }
    }

    private async void GuidedInstallButton_Click(object sender, RoutedEventArgs e) =>
        await RunGuidedDeploymentAsync(restore: false);

    private async void GuidedRestoreButton_Click(object sender, RoutedEventArgs e) =>
        await RunGuidedDeploymentAsync(restore: true);

    private async void GuidedInstallSelectedButton_Click(object sender, RoutedEventArgs e) =>
        await RunGuidedQuickDeploymentAsync(restoreAll: false);

    private async void GuidedRestoreAllButton_Click(object sender, RoutedEventArgs e) =>
        await RunGuidedQuickDeploymentAsync(restoreAll: true);

    private static bool HasActiveGuidedDeployment(GuidedTexturePackageRow package) =>
        package.Inspection?.HasActiveDeployment == true;

    private static string? GetQuickInstallReadinessProblem(GuidedTexturePackageRow package)
    {
        var latest = package.LatestRevision;
        if (latest is null) return "has not been built";
        if (!File.Exists(latest.ModBundleManifestPath)) return "is missing its verified mod bundle";
        if (!File.Exists(latest.OutputUpkPath)) return "is missing its built UPK";
        if (latest.PreviewOnly) return "is an older preview-only topology build and must be rebuilt";
        if (latest.ContainsVariableSizeMeshReplacements && !latest.VariableSizeDeploymentApproved)
            return "is missing the variable-size mesh deployment approval and must be rebuilt";
        if (package.BuildOutdated) return "contains edits newer than its latest build and must be rebuilt";
        return null;
    }

    private static string FormatQuickDeploymentConflicts(ModSetInspection inspection)
    {
        var blocking = inspection.Conflicts
            .Where(conflict => conflict.Severity == ModSetConflictSeverity.Blocking)
            .ToArray();
        var lines = blocking.Select(conflict =>
        {
            var packages = inspection.Set.Manifest.Members
                .Where(member => conflict.MemberIds.Contains(member.MemberId,
                    StringComparer.OrdinalIgnoreCase))
                .Select(member => member.PackageAlias)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var suffix = packages.Length == 0
                ? string.Empty
                : $"\n    Package Projects: {string.Join(", ", packages)}";
            return $"  • {conflict.Message}{suffix}";
        });
        var selectionConflict = blocking.Any(conflict => conflict.Code is
            "SET_OBJECT_CONFLICT" or "SET_PACKAGE_CONFLICT");
        return "Arkive found a blocking conflict and changed no game files.\n\n" +
               string.Join("\n", lines) +
               (selectionConflict
                   ? "\n\nSelect only one of the conflicting Package Projects, then click Install selected again."
                   : "\n\nResolve the listed package state before trying again.");
    }

    private async Task RunGuidedQuickDeploymentAsync(bool restoreAll)
    {
        if (_guidedTextureModInProgress || _modSetBusy) return;
        var packages = restoreAll
            ? _guidedPackageRows.Where(HasActiveGuidedDeployment).ToArray()
            : GuidedPackagesGrid.SelectedItems.OfType<GuidedTexturePackageRow>()
                .GroupBy(package => package.Entry.Manifest.PackageProjectId,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        if (packages.Length == 0)
        {
            MessageBox.Show(this,
                restoreAll
                    ? "No active Arkive package mods have a verified original backup to restore."
                    : "Select one or more Package Projects first. Use Ctrl or Shift to select multiple rows.",
                restoreAll ? "Restore all active" : "Install selected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!restoreAll)
        {
            var readinessProblems = packages
                .Select(package => (Package: package, Problem: GetQuickInstallReadinessProblem(package)))
                .Where(item => item.Problem is not null)
                .ToArray();
            if (readinessProblems.Length > 0)
            {
                var detail = string.Join("\n", readinessProblems.Select(item =>
                    $"  • {item.Package.DisplayName}: {item.Problem}."));
                Log($"Guided quick install refused before transactional preflight: " +
                    $"selected={packages.Length}, problems={string.Join(" | ", readinessProblems.Select(item => item.Package.Entry.Manifest.PackageProjectId + ":" + item.Problem))}.");
                MessageBox.Show(this,
                    $"Arkive cannot install the selected Package Projects yet:\n\n{detail}\n\nRebuild the listed projects, then try again.",
                    "Install selected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        _guidedTextureModInProgress = true;
        _modSetBusy = true;
        UpdateGuidedTextureModActionState();
        UpdateModSetActionState();
        var verb = restoreAll ? "restore-all" : "install-selected";
        var displayName = restoreAll ? "Restore all active package mods" : "Install selected Package Projects";
        try
        {
            StatusText.Text = restoreAll
                ? "Preflighting every active Arkive package mod..."
                : $"Preflighting {packages.Length:N0} selected Package Projects...";
            var utilitySet = await _modSetWorkspace.UpsertUtilitySetAsync(
                verb, displayName, packages.Select(package => package.Entry).ToArray());
            var request = CreateModSetDeploymentRequest(utilitySet.ManifestPath);
            var inspection = await _modSetService.InspectAsync(request);
            var blocking = inspection.Conflicts.Any(conflict =>
                conflict.Severity == ModSetConflictSeverity.Blocking);
            Log($"Guided quick deployment preflight completed: action={verb}, " +
                $"packages={packages.Length}, resolved={inspection.Members.Count}, state={inspection.State}, " +
                $"canInstall={inspection.CanInstall}, canRestore={inspection.CanRestore}, " +
                $"blockingConflicts={inspection.Conflicts.Count(conflict => conflict.Severity == ModSetConflictSeverity.Blocking)}, " +
                $"packageIds={string.Join(",", packages.Select(package => package.Entry.Manifest.PackageProjectId))}.");
            if (blocking)
            {
                StatusText.Text = "Quick package deployment was blocked before any game files changed.";
                MessageBox.Show(this,
                    FormatQuickDeploymentConflicts(inspection),
                    restoreAll ? "Restore all active — attention required" : "Package Project conflict",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            if (restoreAll ? !inspection.CanRestore : !inspection.CanInstall)
            {
                StatusText.Text = inspection.Summary;
                MessageBox.Show(this, inspection.Summary,
                    restoreAll ? "Restore all active" : "Install selected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var packageLines = string.Join("\n", packages.Take(16).Select(package =>
                $"  • {package.DisplayName} — {package.PhysicalPackageName}"));
            if (packages.Length > 16)
                packageLines += $"\n  • …and {packages.Length - 16:N0} more";
            var prompt = restoreAll
                ? $"Restore the verified original for every active Arkive mod listed below?\n\n{packageLines}\n\n" +
                  "Arkive will preflight all targets and restore them as one transaction. Close Lost Ark before continuing."
                : $"Install {packages.Length:N0} selected Package Projects?\n\n{packageLines}\n\n" +
                  "Arkive will install them as one transaction. If any package fails, packages changed by this operation are rolled back. Close Lost Ark before continuing.";
            if (MessageBox.Show(this, prompt,
                    restoreAll ? "Restore all active" : "Install selected",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) !=
                MessageBoxResult.Yes) return;

            StatusText.Text = restoreAll
                ? "Restoring and verifying all active package mods..."
                : "Installing and verifying selected Package Projects...";
            var result = restoreAll
                ? await _modSetService.RestoreAsync(request)
                : await _modSetService.InstallAsync(request);
            StatusText.Text = restoreAll
                ? $"Restored {result.ChangedPackageCount:N0} active package mod(s) to their verified originals."
                : $"Installed and verified {packages.Length:N0} Package Project(s).";
            Log($"Guided quick deployment completed: action={verb}, set={utilitySet.Manifest.ModSetId}, " +
                $"state={result.Inspection.State}, selectedPackages={packages.Length}, " +
                $"changedPackages={result.ChangedPackageCount}, rolledBack={result.RolledBack}, " +
                $"duration={result.Duration.TotalSeconds:N3}s, receiptTransaction={result.Receipt.TransactionId}.");
            await RefreshGuidedTextureModsAsync($"quick {verb} changed package targets");
            await RefreshModSetsAsync($"quick {verb} completed");
            MessageBox.Show(this, StatusText.Text,
                restoreAll ? "Originals restored" : "Package Projects installed",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           NotSupportedException or ArgumentException or JsonException)
        {
            Log($"Guided quick deployment failed: action={verb}, packages={packages.Length}, error={exception}");
            MessageBox.Show(this, exception.Message,
                restoreAll ? "Restore all active" : "Install selected",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await RefreshGuidedTextureModsAsync($"quick {verb} failed");
            await RefreshModSetsAsync($"quick {verb} failed");
        }
        finally
        {
            _guidedTextureModInProgress = false;
            _modSetBusy = false;
            UpdateGuidedTextureModActionState();
            UpdateModSetActionState();
        }
    }

    private async Task RunGuidedDeploymentAsync(bool restore)
    {
        if (_guidedTextureModInProgress ||
            GuidedPackagesGrid.SelectedItem is not GuidedTexturePackageRow { LatestRevision: { } latest } package) return;
        var deploymentRevision = restore ? package.DeploymentRevision ?? latest : latest;
        if (!restore && latest.PreviewOnly)
        {
            Log($"Guided deployment refused for preview-only variable-size composition: " +
                $"packageId={package.Entry.Manifest.PackageProjectId}, revision={latest.RevisionId}.");
            MessageBox.Show(this,
                "This is an older preview-only topology revision. Rebuild the currently checked assets to run Arkive's second-batch controlled deployment gate; the existing revision will remain locked.",
                "Preview-only modified UPK", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!restore && latest.ContainsVariableSizeMeshReplacements &&
            !latest.VariableSizeDeploymentApproved)
        {
            Log($"Guided deployment refused because the variable-size gate is absent: " +
                $"packageId={package.Entry.Manifest.PackageProjectId}, revision={latest.RevisionId}.");
            MessageBox.Show(this,
                "This topology build does not retain Arkive's variable-size deployment approval. Rebuild the checked assets before installing.",
                "Variable-size deployment gate missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var request = CreateModDeploymentRequest(deploymentRevision.ModBundleManifestPath);
            var inspection = restore
                ? await _modDeploymentService.InspectForRecoveryAsync(request)
                : await _modDeploymentService.InspectAsync(request);
            if (restore ? !inspection.CanRestore : !inspection.CanInstall)
            {
                MessageBox.Show(this, inspection.Summary, restore ? "Restore original" : "Install mod", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!restore && package.BuildOutdated)
            {
                MessageBox.Show(
                    this,
                    "One or more checked asset projects changed since the latest UPK was built. Rebuild checked assets before installing so Arkive cannot deploy a stale mod.",
                    "Rebuild required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Log($"Guided package install refused as stale: packageId={package.Entry.Manifest.PackageProjectId}, revision={latest.RevisionId}, staleAssets={string.Join(",", package.Assets.Where(asset => asset.NeedsBuild).Select(asset => asset.ObjectName).Concat(package.MeshAssets.Where(asset => asset.NeedsBuild).Select(asset => asset.ObjectName)).Concat(package.StaticMeshAssets.Where(asset => asset.NeedsBuild).Select(asset => asset.ObjectName)))}.");
                return;
            }
            var verb = restore ? "Restore the original package" : "Install this mod";
            var builtAssets = latest.Assets
                .Select(built => package.Assets.FirstOrDefault(asset => asset.Entry.Manifest.ProjectId.Equals(
                    built.ProjectId, StringComparison.OrdinalIgnoreCase)))
                .Where(asset => asset is not null)
                .Cast<GuidedTextureModRow>()
                .ToArray();
            var assetLines = string.Join(
                "\n",
                builtAssets.Take(12).Select(asset => $"  • {asset.ObjectName} — {asset.ImpactNames}"));
            if (builtAssets.Length > 12) assetLines += $"\n  • …and {builtAssets.Length - 12:N0} more";
            var builtMeshes = latest.MeshAssets
                .Select(built => package.MeshAssets.FirstOrDefault(asset => asset.Entry.Manifest.EditId.Equals(
                    built.EditId, StringComparison.OrdinalIgnoreCase)))
                .Where(asset => asset is not null)
                .Cast<GuidedMeshModRow>()
                .ToArray();
            var meshLines = string.Join(
                "\n",
                builtMeshes.Take(12).Select(asset =>
                    $"  • {asset.ObjectName} — {asset.DisplayName} — {asset.ImportProfile}"));
            if (builtMeshes.Length > 12) meshLines += $"\n  • …and {builtMeshes.Length - 12:N0} more";
            var builtStaticMeshes = latest.StaticMeshAssets
                .Select(built => package.StaticMeshAssets.FirstOrDefault(asset =>
                    asset.Entry.Manifest.EditId.Equals(built.EditId, StringComparison.OrdinalIgnoreCase)))
                .Where(asset => asset is not null)
                .Cast<GuidedStaticMeshModRow>()
                .ToArray();
            var staticMeshLines = string.Join(
                "\n",
                builtStaticMeshes.Take(12).Select(asset =>
                    $"  • {asset.ObjectName} — {asset.DisplayName} — {asset.ImportProfile} — {asset.Collision}"));
            if (builtStaticMeshes.Length > 12)
                staticMeshLines += $"\n  • …and {builtStaticMeshes.Length - 12:N0} more";
            var topologyNotice = latest.ContainsVariableSizeMeshReplacements
                ? "\n\nVariable-size mesh deployment:\n" +
                  "Arkive will install a UPK whose physical size and mesh export offsets may differ from the clean source. " +
                  "The native compiler, decode-back, unrelated-export isolation, original-name UModel export, content-addressed backup, atomic replacement, and guarded restore gates have passed."
                : string.Empty;
            var detail = restore
                ? $"Arkive will restore revision {deploymentRevision.RevisionId} to its verified source package.\n\nTarget:\n{inspection.TargetPath}\n\nBackup:\n{inspection.BackupPath}"
                : $"This changes the asset for every player, NPC, and entity using it on this client.\n\n" +
                  $"Built textures ({builtAssets.Length:N0}):\n{assetLines}\n\n" +
                  $"Built skeletal meshes ({builtMeshes.Length:N0}):\n{meshLines}\n\n" +
                  $"Built static meshes ({builtStaticMeshes.Length:N0}):\n{staticMeshLines}\n\n" +
                  $"Verified impact:\n{package.ImpactSummary}\n\n" +
                  $"Arkive will back up and replace:\n{inspection.TargetPath}\n\nBackup:\n{inspection.BackupPath}" +
                  topologyNotice;
            var notes = string.IsNullOrWhiteSpace(package.Entry.Manifest.Notes)
                ? string.Empty
                : $"\n\nNotes:\n{package.Entry.Manifest.Notes}";
            if (MessageBox.Show(this, $"{verb} for '{package.DisplayName}'?\n\n{detail}{notes}\n\nClose Lost Ark before continuing.",
                    verb, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

            _guidedTextureModInProgress = true;
            UpdateGuidedTextureModActionState();
            StatusText.Text = restore ? "Restoring the original package..." : "Installing and verifying the mod...";
            Log(
                $"Guided package deployment started: action={(restore ? "restore" : "install")}, " +
                $"packageId={package.Entry.Manifest.PackageProjectId}, revision={deploymentRevision.RevisionId}, " +
                $"latestRevision={latest.RevisionId}, textures={deploymentRevision.Assets.Count}, meshes={deploymentRevision.MeshAssets.Count}, " +
                $"variableSize={deploymentRevision.ContainsVariableSizeMeshReplacements}, variableSizeApproved={deploymentRevision.VariableSizeDeploymentApproved}, " +
                $"bundle={deploymentRevision.ModBundleManifestPath}, target={inspection.TargetPath}.");
            var result = restore
                ? await _modDeploymentService.RestoreAsync(request)
                : await _modDeploymentService.InstallAsync(request);
            StatusText.Text = restore
                ? "Original package restored and verified."
                : "Mod installed and verified; Restore original is available.";
            Log(
                $"Guided package deployment completed: action={(restore ? "restore" : "install")}, " +
                $"packageId={package.Entry.Manifest.PackageProjectId}, revision={deploymentRevision.RevisionId}, state={result.Inspection.State}, " +
                $"receipt={result.Inspection.ReceiptPath}, changedTarget={result.ChangedTarget}.");
            MessageBox.Show(this, StatusText.Text, restore ? "Original restored" : "Mod installed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or System.ComponentModel.Win32Exception)
        {
            Log($"Guided package deployment failed: action={(restore ? "restore" : "install")}, package={package.Entry.ManifestPath}, error={exception}");
            MessageBox.Show(this, exception.Message, restore ? "Restore original" : "Install mod", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _guidedTextureModInProgress = false;
            UpdateGuidedTextureModActionState();
            await RefreshGuidedTextureModsAsync(
                "guided package deployment state changed",
                package.Assets.FirstOrDefault()?.Entry.Manifest.ProjectId ??
                package.MeshAssets.FirstOrDefault()?.Entry.Manifest.EditId);
        }
    }
}
