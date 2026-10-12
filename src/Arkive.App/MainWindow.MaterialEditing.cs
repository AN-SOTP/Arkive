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
    private async void MaterialReconButton_Click(object sender, RoutedEventArgs e)
    {
        if (AssetsGrid.SelectedItem is not AssetRow row || string.IsNullOrWhiteSpace(_currentPackagePath) ||
            row.Kind != AssetKind.Material ||
            !row.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)) return;
        var objectName = row.ExternalAsset?.ObjectName ?? ObjectNameFromPath(row.ObjectPath);
        await RunMaterialProjectAsync(_currentPackagePath,
            LostArkPackageName.GetDisplayName(_currentPackagePath), row.ExportIndex, row.Kind,
            row.ClassName, objectName, row.SerialOffset, row.SerialSize, "open-package Assets result");
    }

    private async void LibraryStartMaterialModButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is not LibraryAssetRow row || row.Kind != AssetKind.Material ||
            !row.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)) return;
        await RunMaterialProjectAsync(row.SourcePath, row.PackageAlias, row.ExportIndex, row.Kind,
            row.ClassName, row.ObjectName, row.SerialOffset, row.SerialSize, "indexed library result");
    }

    private async Task RunMaterialProjectAsync(
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
        MaterialReconButton.IsEnabled = false;
        LibraryStartMaterialModButton.IsEnabled = false;
        MaterialStatusText.Text = "Decoding the native material parameter stream...";
        try
        {
            var existing = (await _materialEditWorkspaceStore.LoadAsync()).FirstOrDefault(entry =>
                SamePath(entry.Document.SourcePackagePath, packagePath) &&
                entry.Document.ExportIndex == exportIndex &&
                entry.Document.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                MainTabControl.SelectedItem = MaterialEditorTab;
                await RefreshMaterialProjectsAsync("existing material project selected", existing.Document.EditId);
                MaterialStatusText.Text = $"Reopened material project {existing.Document.DisplayName}.";
                Log($"Existing material project reopened: trigger={trigger}, editId={existing.Document.EditId}, " +
                    $"package={packagePath}, object={objectName}.");
                return;
            }
            var packageProject = (await _guidedTexturePackageWorkspace.LoadAsync()).FirstOrDefault(entry =>
                SamePath(entry.Manifest.SourcePackagePath, packagePath));
            var latest = packageProject?.Manifest.LatestRevisionId is { } latestId
                ? packageProject.Manifest.Revisions.FirstOrDefault(revision => revision.RevisionId.Equals(
                    latestId, StringComparison.OrdinalIgnoreCase))
                : packageProject?.Manifest.Revisions.LastOrDefault();
            if (latest is not null && File.Exists(latest.ModBundleManifestPath))
            {
                var inspection = await _modDeploymentService.InspectAsync(
                    CreateModDeploymentRequest(latest.ModBundleManifestPath));
                if (inspection.CanRestore || inspection.State is ModInstallationState.Installed or ModInstallationState.Prepared)
                    throw new NotSupportedException(
                        "Restore the currently installed modified UPK before starting a new material project from it.");
            }
            var report = await _materialReconnaissanceService.InspectAsync(
                new MaterialInstanceReconnaissanceRequest(
                    packagePath, packageAlias, exportIndex, kind, className, objectName,
                    serialOffset, serialSize));
            _currentMaterialReconnaissance = await _materialWorkspaceStore.SaveAsync(report);
            _currentMaterialEdit = report.Readiness == MaterialInstanceReadiness.Editable
                ? await _materialEditService.CreateEditAsync(
                    _currentMaterialReconnaissance.JsonPath,
                    _materialEditWorkspaceStore.RootDirectory, objectName)
                : null;
            LoadMaterialEditRows();
            await RefreshMaterialProjectsAsync("material project created", _currentMaterialEdit?.Document.EditId);
            await RefreshGuidedTextureModsAsync("material project created", _currentMaterialEdit?.Document.EditId);
            MainTabControl.SelectedItem = MaterialEditorTab;
            MaterialStatusText.Text = report.Readiness == MaterialInstanceReadiness.Editable
                ? $"{report.PackageAlias}.{report.ObjectName}: {report.ScalarParameters.Count:N0} scalar, " +
                  $"{report.VectorParameters.Count:N0} vector, {report.TextureParameters.Count:N0} texture reference(s), " +
                  $"and {(report.ParentObjectReference is null ? "no" : "one")} parent reference."
                : $"Material editing refused: {report.Issues.FirstOrDefault(issue => issue.Severity == "Error")?.Message ?? "no editable parameters were proven"}.";
            Log($"MaterialInstanceConstant reconnaissance completed: package={report.PackageAlias}, " +
                $"trigger={trigger}, object={report.ObjectName}, readiness={report.Readiness}, scalars={report.ScalarParameters.Count}, " +
                $"vectors={report.VectorParameters.Count}, textures={report.TextureParameters.Count}, " +
                $"report={_currentMaterialReconnaissance.JsonPath}, edit={_currentMaterialEdit?.DocumentPath ?? "none"}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           NotSupportedException or ArgumentException)
        {
            Log($"Material reconnaissance failed: trigger={trigger}, package={packagePath}, object={objectName}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive material editor",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            MaterialReconButton.IsEnabled = AssetsGrid.SelectedItem is AssetRow selected &&
                                            selected.Kind == AssetKind.Material &&
                                            selected.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase);
            UpdateLibraryActionState();
            UpdateMaterialActionState();
        }
    }

    private void LoadMaterialEditRows()
    {
        var report = _currentMaterialReconnaissance?.Report;
        var document = _currentMaterialEdit?.Document;
        _materialScalarRows = document?.Scalars.Select(item => new MaterialScalarEditRow(item)).ToArray() ?? [];
        _materialVectorRows = document?.Vectors.Select(item => new MaterialVectorEditRow(item)).ToArray() ?? [];
        MaterialReferenceCatalog references = new([], []);
        if (report is not null)
        {
            try { references = _materialEditService.GetReferenceCatalog(report); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               NotSupportedException or ArgumentException)
            {
                Log($"Material reference catalogue unavailable: object={report.ObjectName}, error={exception}");
            }
        }
        _materialTextureRows = document?.Textures.Select(item =>
            new MaterialTextureEditRow(item, references.TextureTargets)).ToArray() ?? [];
        _materialParentRows = CreateMaterialParentRows(document, report, references.ParentTargets);
        MaterialScalarGrid.ItemsSource = _materialScalarRows;
        MaterialVectorGrid.ItemsSource = _materialVectorRows;
        MaterialTextureGrid.ItemsSource = _materialTextureRows;
        MaterialParentGrid.ItemsSource = _materialParentRows;
        MaterialIssuesGrid.ItemsSource = report?.Issues;
        UpdateMaterialActionState();
    }

    private static IReadOnlyList<MaterialParentEditRow> CreateMaterialParentRows(
        MaterialInstanceEditDocument? document,
        MaterialInstanceReconnaissanceReport? report,
        IReadOnlyList<MaterialObjectReferenceOption> options)
    {
        if (document is null || report?.ParentObjectReference is not { } reference) return [];
        if (document.Parent is { } parent) return [new(parent, options)];
        var option = options.FirstOrDefault(item => item.ObjectReference == reference);
        var resolved = report.ParentObject ?? option?.ResolvedObject ?? "None";
        var identity = option?.StableIdentity ?? string.Empty;
        return [new(new MaterialParentReference(reference, resolved, identity,
            reference, resolved, identity), options)];
    }

    private async Task RefreshMaterialProjectsAsync(string trigger, string? selectEditId = null)
    {
        var entries = await _materialEditWorkspaceStore.LoadAsync();
        _materialProjectRows = entries.Select(entry => new MaterialProjectRow(entry)).ToArray();
        MaterialProjectsGrid.ItemsSource = _materialProjectRows;
        var selected = _materialProjectRows.FirstOrDefault(row => row.Entry.Document.EditId.Equals(
                           selectEditId, StringComparison.OrdinalIgnoreCase)) ??
                       (MaterialProjectsGrid.SelectedItem as MaterialProjectRow is { } previous
                           ? _materialProjectRows.FirstOrDefault(row => row.Entry.Document.EditId.Equals(
                               previous.Entry.Document.EditId, StringComparison.OrdinalIgnoreCase))
                           : null) ?? _materialProjectRows.FirstOrDefault();
        MaterialProjectsGrid.SelectedItem = selected;
        if (selected is not null) MaterialProjectsGrid.ScrollIntoView(selected);
        Log($"Material project workspace refreshed: trigger={trigger}, projects={entries.Count}, " +
            $"included={entries.Count(entry => entry.Document.IncludedInPackageBuild)}, root={_materialEditWorkspaceStore.RootDirectory}.");
    }

    private async void MaterialProjectsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MaterialProjectsGrid.SelectedItem is not MaterialProjectRow row)
        {
            _currentMaterialEdit = null;
            _currentMaterialReconnaissance = null;
            LoadMaterialEditRows();
            return;
        }
        try
        {
            var json = await File.ReadAllTextAsync(row.Entry.Document.SourceReconnaissancePath);
            var report = JsonSerializer.Deserialize<MaterialInstanceReconnaissanceReport>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
                         throw new InvalidDataException("The material reconnaissance report was empty.");
            _currentMaterialEdit = row.Entry;
            _currentMaterialReconnaissance = new MaterialInstanceReconnaissanceEntry(
                Path.GetDirectoryName(row.Entry.Document.SourceReconnaissancePath)!,
                row.Entry.Document.SourceReconnaissancePath, report);
            LoadMaterialEditRows();
            MaterialStatusText.Text =
                $"{report.PackageAlias}.{report.ObjectName}: {report.ScalarParameters.Count:N0} scalar, " +
                $"{report.VectorParameters.Count:N0} vector, {report.TextureParameters.Count:N0} texture reference(s), " +
                $"and {(report.ParentObjectReference is null ? "no" : "one")} parent reference.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Log($"Material project load failed: project={row.Entry.DocumentPath}, error={exception}");
            MaterialStatusText.Text = $"The selected material project could not be loaded: {exception.Message}";
        }
        UpdateMaterialActionState();
    }

    private void UpdateMaterialActionState()
    {
        if (MaterialSaveCandidateButton is null) return;
        var available = _currentMaterialEdit is not null &&
                        _currentMaterialReconnaissance?.Report.Readiness == MaterialInstanceReadiness.Editable;
        MaterialSaveCandidateButton.IsEnabled = available;
        MaterialResetValuesButton.IsEnabled = available;
        MaterialOpenFolderButton.IsEnabled = _currentMaterialReconnaissance is not null;
        MaterialDeleteProjectButton.IsEnabled = _currentMaterialEdit is not null;
    }

    private async void MaterialResetValuesButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _materialScalarRows) row.Value = row.SourceValue;
        foreach (var row in _materialVectorRows) row.Reset();
        foreach (var row in _materialTextureRows) row.Reset();
        foreach (var row in _materialParentRows) row.Reset();
        MaterialScalarGrid.Items.Refresh();
        MaterialVectorGrid.Items.Refresh();
        MaterialTextureGrid.Items.Refresh();
        MaterialParentGrid.Items.Refresh();
        if (_currentMaterialEdit is not null)
        {
            var reset = _currentMaterialEdit.Document with
            {
                Scalars = _materialScalarRows.Select(row => row.ToEdit()).ToArray(),
                Vectors = _materialVectorRows.Select(row => row.ToEdit()).ToArray(),
                Textures = _materialTextureRows.Select(row => row.ToEdit()).ToArray(),
                Parent = _materialParentRows.SingleOrDefault()?.ToEdit(),
                LatestCandidateManifestPath = null,
                LatestCandidatePath = null,
                LatestCandidateSha256 = null,
            };
            _currentMaterialEdit = await _materialEditWorkspaceStore.SaveAsync(
                _currentMaterialEdit with { Document = reset });
            await RefreshMaterialProjectsAsync("material values reset", reset.EditId);
            await RefreshGuidedTextureModsAsync("material values reset", reset.EditId);
        }
        MaterialStatusText.Text =
            "Values were reset to the clean source. This project now has no material change and will not build until a value is edited and verified.";
    }

    private async void MaterialSaveCandidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMaterialEdit is null || _currentMaterialReconnaissance is null) return;
        MaterialSaveCandidateButton.IsEnabled = false;
        try
        {
            MaterialScalarGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            MaterialVectorGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            MaterialTextureGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            MaterialParentGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            var document = _currentMaterialEdit.Document with
            {
                Scalars = _materialScalarRows.Select(row => row.ToEdit()).ToArray(),
                Vectors = _materialVectorRows.Select(row => row.ToEdit()).ToArray(),
                Textures = _materialTextureRows.Select(row => row.ToEdit()).ToArray(),
                Parent = _materialParentRows.SingleOrDefault()?.ToEdit(),
            };
            var temporary = _currentMaterialEdit.DocumentPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }));
            File.Move(temporary, _currentMaterialEdit.DocumentPath, true);
            _currentMaterialEdit = _currentMaterialEdit with { Document = document };
            var result = await _materialEditService.BuildCandidateAsync(
                _currentMaterialEdit.DocumentPath,
                Path.Combine(_projectRoot, "staging", "material-candidates"));
            _currentMaterialEdit = await _materialEditWorkspaceStore.RecordCandidateAsync(
                _currentMaterialEdit, result);
            MaterialStatusText.Text =
                $"Verified candidate built: {result.Manifest.ChangedScalarCount:N0} scalar, " +
                $"{result.Manifest.ChangedVectorCount:N0} vector, " +
                $"{result.Manifest.ChangedTextureReferenceCount:N0} texture reference(s), " +
                $"parent {(result.Manifest.ParentReferenceChanged ? "changed" : "unchanged")}, " +
                $"{result.Manifest.ChangedByteCount:N0} byte(s) changed; " +
                "all locked bytes preserved. The project is ready to include under Modified UPKs.";
            Log($"Material native candidate built: object={result.Manifest.ObjectName}, " +
                $"changedScalars={result.Manifest.ChangedScalarCount}, changedVectors={result.Manifest.ChangedVectorCount}, " +
                $"changedTextures={result.Manifest.ChangedTextureReferenceCount}, parentChanged={result.Manifest.ParentReferenceChanged}, " +
                $"changedBytes={result.Manifest.ChangedByteCount}, sameSize={result.Manifest.SameSize}, " +
                $"lockedPreserved={result.Manifest.LockedBytesPreserved}, candidate={result.CandidatePath}, manifest={result.ManifestPath}.");
            await RefreshMaterialProjectsAsync("material candidate saved", _currentMaterialEdit.Document.EditId);
            await RefreshGuidedTextureModsAsync("material candidate saved", _currentMaterialEdit.Document.EditId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           NotSupportedException or ArgumentException or JsonException)
        {
            Log($"Material native candidate refused: edit={_currentMaterialEdit.DocumentPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Arkive material candidate",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { UpdateMaterialActionState(); }
    }

    private void MaterialOpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMaterialEdit is not null)
            OpenFolder(_currentMaterialEdit.DirectoryPath, "material project workspace");
    }

    private async void MaterialDeleteProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMaterialEdit is not { } edit) return;
        if (MessageBox.Show(this,
                $"Delete material project '{edit.Document.DisplayName}'?\n\nThe clean game package and prior package builds are retained.",
                "Delete material project", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _materialEditWorkspaceStore.DeleteAsync(edit);
            _currentMaterialEdit = null;
            _currentMaterialReconnaissance = null;
            LoadMaterialEditRows();
            await RefreshMaterialProjectsAsync("material project deleted");
            await RefreshGuidedTextureModsAsync("material project deleted");
            Log($"Material project deleted: editId={edit.Document.EditId}, object={edit.Document.ObjectName}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Material project deletion failed: edit={edit.DocumentPath}, error={exception}");
            MessageBox.Show(this, exception.Message, "Delete material project",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

}
