using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Inspection;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.Packages;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

public enum ArkiveModTargetState
{
    Exact,
    Changed,
    Missing,
    Ambiguous,
    PackageMissing,
}

public sealed record ArkiveModTargetCheck(
    ArkiveModTargetState State,
    string PackagePath,
    string? PackageSha256,
    AssetCatalogEntry? Export,
    string Message);

public sealed record ArkiveModAssetSelection(
    string PackagePath, string PackageAlias, string ClassName, string ObjectName);

public sealed record ArkiveModAssetProgress(string Stage, string ObjectName);

public sealed record ArkiveModMaterializationServices(
    UpkPackageReader Reader,
    IChunkProtectionProvider ProtectionProvider,
    TextureReconnaissanceService TextureReconnaissance,
    TextureReconnaissanceWorkspaceStore TextureReconnaissanceStore,
    NativeTextureExportService? NativeTextureExport,
    SkeletalMeshReconnaissanceService SkeletalMeshReconnaissance,
    SkeletalMeshReconnaissanceWorkspaceStore SkeletalMeshReconnaissanceStore,
    SkeletalMeshEditableExportService SkeletalMeshEditableExport,
    StaticMeshReconnaissanceService StaticMeshReconnaissance,
    StaticMeshReconnaissanceWorkspaceStore StaticMeshReconnaissanceStore,
    StaticMeshEditableExportService StaticMeshEditableExport,
    MaterialInstanceReconnaissanceService MaterialReconnaissance,
    MaterialInstanceWorkspaceStore MaterialReconnaissanceStore,
    MaterialInstanceEditService MaterialEdit,
    string MaterialCandidateRoot);

public sealed class ArkiveModMaterializationService
{
    private readonly ArkiveModLibrary _library;
    private readonly ArkiveModEditWorkspaces _edits;
    private readonly ArkiveModMaterializationServices _services;
    private readonly AssetCatalogReader _catalogReader;

    public ArkiveModMaterializationService(
        ArkiveModLibrary library,
        ArkiveModEditWorkspaces edits,
        ArkiveModMaterializationServices services)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _edits = edits ?? throw new ArgumentNullException(nameof(edits));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _catalogReader = new AssetCatalogReader(services.Reader, services.ProtectionProvider);
    }

    public async Task<ArkiveModTargetCheck> CheckTargetAsync(
        ArkiveModFileAsset asset,
        string packagePath,
        string? knownPackageSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var path = Path.GetFullPath(packagePath);
        if (!File.Exists(path))
            return new ArkiveModTargetCheck(ArkiveModTargetState.PackageMissing, path, null, null,
                $"{asset.Target.PhysicalPackageName} was not found in the Lost Ark package directory.");
        var packageHash = knownPackageSha256 ??
                          await ArkiveModEditSnapshot.HashFileCachedAsync(path, cancellationToken).ConfigureAwait(false);
        var catalogue = _catalogReader.Read(path);
        var matches = catalogue.Assets.Where(entry =>
                entry.ObjectName.Equals(asset.Target.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                ArkiveModFileService.KindMatches(asset.Kind, entry.ClassName))
            .ToArray();
        if (matches.Length == 0)
            return new ArkiveModTargetCheck(ArkiveModTargetState.Missing, path, packageHash, null,
                $"{asset.Target.ObjectName} no longer exists in {asset.Target.PhysicalPackageName}.");
        // Export indices move between client versions, so same-named exports are told apart by content.
        using var logical = new LogicalPackageStream(catalogue.Summary, _services.ProtectionProvider);
        var identical = matches.Where(entry => logical.HashRangeSha256(entry.SerialOffset, entry.SerialSize, cancellationToken)
            .Equals(asset.Target.SourceExportSha256, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (identical.Length > 1 || (identical.Length == 0 && matches.Length > 1))
            return new ArkiveModTargetCheck(ArkiveModTargetState.Ambiguous, path, packageHash, null,
                $"{matches.Length:N0} exports in {asset.Target.PhysicalPackageName} are named {asset.Target.ObjectName}.");
        var exact = identical.Length == 1;
        var export = exact ? identical[0] : matches[0];
        return new ArkiveModTargetCheck(
            exact ? ArkiveModTargetState.Exact : ArkiveModTargetState.Changed, path, packageHash, export,
            exact
                ? "The client asset is byte-identical to the one this mod was made for."
                : "The game has changed this asset since the mod was made; it needs an explicit update.");
    }

    public Task<ArkiveModLocalEdit> CreateEditableAsync(
        ArkiveModAssetSelection selection, CancellationToken cancellationToken = default) =>
        CreateEditableAsync(selection, cancellationToken, null);

    public async Task<ArkiveModLocalEdit> CreateEditableAsync(
        ArkiveModAssetSelection selection, CancellationToken cancellationToken,
        IProgress<ArkiveModAssetProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(selection);
        selection = selection with { PackagePath = Path.GetFullPath(selection.PackagePath) };
        progress?.Report(new("Reading clean package", selection.ObjectName));
        var catalogue = _catalogReader.Read(selection.PackagePath);
        var matches = catalogue.Assets.Where(asset =>
            asset.ObjectName.Equals(selection.ObjectName, StringComparison.OrdinalIgnoreCase) &&
            asset.ClassName.Equals(selection.ClassName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) Refuse("AMM050", "The selected asset is missing or ambiguous in the clean package.");
        var export = matches[0];
        var hash = await ArkiveModEditSnapshot.HashFileCachedAsync(selection.PackagePath, cancellationToken)
            .ConfigureAwait(false);
        var work = _library.CreateWorkingDirectory(Guid.NewGuid().ToString("N"), "authoring", "export");
        object entry;
        UnifiedPackageAssetKind kind;
        string editId;
        string manifestPath;
        switch (selection.ClassName.ToLowerInvariant())
        {
            case "texture2d":
                progress?.Report(new("Decoding native texture", selection.ObjectName));
                if (_services.NativeTextureExport is null) Refuse("AMM010", "Native texture export is unavailable.");
                var textureReport = await _services.TextureReconnaissance.InspectAsync(
                    new TextureReconnaissanceRequest(selection.PackagePath, selection.PackageAlias,
                        export.ExportIndex - 1, AssetKind.Texture, export.ClassName, export.ObjectName,
                        export.SerialOffset, export.SerialSize, hash), cancellationToken).ConfigureAwait(false);
                var textureRecon = await _services.TextureReconnaissanceStore.SaveAsync(textureReport, cancellationToken)
                    .ConfigureAwait(false);
                var native = await _services.NativeTextureExport.ExportAsync(
                    new NativeTextureExportRequest(textureReport, work, textureRecon.JsonPath), cancellationToken)
                    .ConfigureAwait(false);
                var image = native.Manifest.EditImageRelativePath is { } imagePath
                    ? Path.Combine(native.OutputDirectory, imagePath) : native.DdsPath;
                var texture = await _edits.Textures.CreateAsync(export.ObjectName, selection.PackageAlias,
                    Path.GetFileName(selection.PackagePath), selection.PackagePath, export.ClassName, export.ObjectName,
                    native.Manifest.SourceExportIndex, native.Manifest.PixelFormat, native.Manifest.Width,
                    native.Manifest.Height, native.ManifestPath, image, TextureImportAlphaPolicy.PreserveOriginal,
                    cancellationToken).ConfigureAwait(false);
                entry = texture;
                kind = UnifiedPackageAssetKind.Texture2D;
                editId = texture.Manifest.ProjectId;
                manifestPath = texture.ManifestPath;
                break;
            case "skeletalmesh":
                progress?.Report(new("Verifying native mesh", selection.ObjectName));
                var meshReport = await _services.SkeletalMeshReconnaissance.InspectAsync(
                    new SkeletalMeshReconnaissanceRequest(selection.PackagePath, selection.PackageAlias,
                        export.ExportIndex - 1, AssetKind.SkeletalMesh, export.ClassName, export.ObjectName,
                        export.SerialOffset, export.SerialSize, hash), cancellationToken).ConfigureAwait(false);
                if (meshReport.Readiness != SkeletalMeshReadiness.NoOpReady)
                    Refuse("AMM020", FirstError(meshReport.Issues.Select(issue => (issue.Severity, issue.Message))));
                var meshRecon = await _services.SkeletalMeshReconnaissanceStore.SaveAsync(meshReport, cancellationToken)
                    .ConfigureAwait(false);
                progress?.Report(new("Exporting editable GLB", selection.ObjectName));
                var meshExport = await _services.SkeletalMeshEditableExport.ExportAsync(
                    new SkeletalMeshEditableExportRequest(meshReport, meshRecon.JsonPath,
                        Path.Combine(meshRecon.DirectoryPath, "editable-glb", Guid.NewGuid().ToString("N"))),
                    cancellationToken).ConfigureAwait(false);
                var source = new SkeletalMeshEditableWorkspaceEntry(meshExport.OutputDirectory, meshExport.ManifestPath,
                    meshExport.GlbPath, meshExport.Manifest, true, true, true);
                progress?.Report(new("Validating the working mesh and animation safety", selection.ObjectName));
                var mesh = await _edits.SkeletalMeshEdits.RegisterAsync(source, meshExport.GlbPath,
                    export.ObjectName, cancellationToken).ConfigureAwait(false);
                entry = mesh.Entry;
                kind = UnifiedPackageAssetKind.SkeletalMesh;
                editId = mesh.Entry.Manifest.EditId;
                manifestPath = mesh.Entry.ManifestPath;
                break;
            case "staticmesh":
                progress?.Report(new("Verifying and exporting static mesh", selection.ObjectName));
                var staticReport = await _services.StaticMeshReconnaissance.InspectAsync(
                    new StaticMeshReconnaissanceRequest(selection.PackagePath, selection.PackageAlias,
                        export.ExportIndex - 1, AssetKind.StaticMesh, export.ClassName, export.ObjectName,
                        export.SerialOffset, export.SerialSize, hash), cancellationToken).ConfigureAwait(false);
                if (staticReport.Readiness != StaticMeshReadiness.Editable)
                    Refuse("AMM030", FirstError(staticReport.Issues.Select(issue => (issue.Severity, issue.Message))));
                var staticRecon = await _services.StaticMeshReconnaissanceStore.SaveAsync(staticReport,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var staticExport = await _services.StaticMeshEditableExport.ExportAsync(
                    new StaticMeshEditableExportRequest(staticReport, staticRecon.JsonPath,
                        Path.Combine(staticRecon.DirectoryPath, "editable-glb", Guid.NewGuid().ToString("N"))),
                    cancellationToken).ConfigureAwait(false);
                var staticMesh = await _edits.StaticMeshEdits.RegisterAsync(staticExport.ManifestPath,
                    staticExport.GlbPath, export.ObjectName, cancellationToken).ConfigureAwait(false);
                entry = staticMesh;
                kind = UnifiedPackageAssetKind.StaticMesh;
                editId = staticMesh.Manifest.EditId;
                manifestPath = staticMesh.ManifestPath;
                break;
            case "materialinstanceconstant":
                progress?.Report(new("Preparing material parameters", selection.ObjectName));
                var materialReport = await _services.MaterialReconnaissance.InspectAsync(
                    new MaterialInstanceReconnaissanceRequest(selection.PackagePath, selection.PackageAlias,
                        export.ExportIndex - 1, AssetKind.Material, export.ClassName, export.ObjectName,
                        export.SerialOffset, export.SerialSize, hash), cancellationToken).ConfigureAwait(false);
                var materialRecon = await _services.MaterialReconnaissanceStore.SaveAsync(materialReport, cancellationToken)
                    .ConfigureAwait(false);
                var material = await _services.MaterialEdit.CreateEditAsync(materialRecon.JsonPath,
                    _edits.MaterialEdits.RootDirectory, export.ObjectName, cancellationToken).ConfigureAwait(false);
                entry = material;
                kind = UnifiedPackageAssetKind.MaterialInstanceConstant;
                editId = material.Document.EditId;
                manifestPath = material.DocumentPath;
                break;
            default:
                throw new ArkiveModRefusalException("AMM051", "This asset type cannot be edited.");
        }
        var local = new ArkiveModLocalEdit(kind, editId, manifestPath, export.ObjectName,
            selection.PackagePath, export.ObjectName, false, entry);
        // A completed working copy must stay excluded even if cancellation arrives at this checkpoint.
        await ArkiveModSelectionService.SetIncludedAsync(_edits, local, false, CancellationToken.None).ConfigureAwait(false);
        return local;
    }

    public async Task<(ArkiveModAssetBinding Binding, ArkiveModTargetState State)> MaterializeAsync(
        ArkiveModLibraryEntry mod,
        ArkiveModFileAsset asset,
        string packagePath,
        bool allowChangedSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(asset);
        var arkivePath = _library.GetModFilePath(mod);
        if (!File.Exists(arkivePath))
            Refuse("AMM001", $"{mod.Manifest.Name}'s .arkive file is missing.");
        var check = await CheckTargetAsync(asset, packagePath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (check.State is not (ArkiveModTargetState.Exact or ArkiveModTargetState.Changed))
            Refuse("AMM002", $"{asset.DisplayName}: {check.Message}");
        if (check.State == ArkiveModTargetState.Changed && !allowChangedSource)
            Refuse("AMM003",
                $"{asset.DisplayName}: {check.Message} Use 'Update for this game version' to revalidate it.");
        var work = _library.CreateWorkingDirectory(mod.ModId, asset.AssetId, asset.Kind.ToString().ToLowerInvariant());
        string editId;
        try
        {
            var files = await ArkiveModFileService.ExtractAssetFilesAsync(
                arkivePath, asset, Path.Combine(work, "source"), cancellationToken).ConfigureAwait(false);
            editId = asset.Kind switch
            {
                UnifiedPackageAssetKind.Texture2D => await MaterializeTextureAsync(
                    asset, check, files, work, cancellationToken).ConfigureAwait(false),
                UnifiedPackageAssetKind.SkeletalMesh => await MaterializeSkeletalMeshAsync(
                    asset, check, files, cancellationToken).ConfigureAwait(false),
                UnifiedPackageAssetKind.StaticMesh => await MaterializeStaticMeshAsync(
                    asset, check, files, cancellationToken).ConfigureAwait(false),
                UnifiedPackageAssetKind.MaterialInstanceConstant => await MaterializeMaterialAsync(
                    asset, check, cancellationToken).ConfigureAwait(false),
                _ => throw new ArkiveModRefusalException("AMM004", $"{asset.Kind} is not supported."),
            };
        }
        catch
        {
            TryDeleteDirectory(work);
            throw;
        }
        TryDeleteDirectory(Path.Combine(work, "source"));
        TryDeleteDirectory(Path.Combine(work, "import-check"));
        if (!Directory.EnumerateFileSystemEntries(work).Any()) TryDeleteDirectory(work);
        return (new ArkiveModAssetBinding(
            asset.AssetId, asset.Kind, editId, check.PackagePath, ArkiveModFileService.ComputeContentSha256(asset)),
            check.State);
    }

    private async Task<string> MaterializeTextureAsync(
        ArkiveModFileAsset asset,
        ArkiveModTargetCheck check,
        IReadOnlyDictionary<string, string> files,
        string work,
        CancellationToken cancellationToken)
    {
        if (_services.NativeTextureExport is null)
            Refuse("AMM010", "Native texture export is unavailable, so texture mods cannot be set up on this computer.");
        var export = check.Export!;
        var texture = asset.Texture!;
        var report = await _services.TextureReconnaissance.InspectAsync(
            new TextureReconnaissanceRequest(
                check.PackagePath, asset.Target.PackageAlias, export.ExportIndex - 1, AssetKind.Texture,
                export.ClassName, export.ObjectName, export.SerialOffset, export.SerialSize, check.PackageSha256),
            cancellationToken).ConfigureAwait(false);
        var recon = await _services.TextureReconnaissanceStore.SaveAsync(report, cancellationToken).ConfigureAwait(false);
        var native = await _services.NativeTextureExport.ExportAsync(
            new NativeTextureExportRequest(report, Path.Combine(work, "native"), recon.JsonPath),
            cancellationToken).ConfigureAwait(false);
        if (native.Manifest.Width != texture.Width || native.Manifest.Height != texture.Height ||
            !native.Manifest.PixelFormat.Equals(texture.PixelFormat, StringComparison.OrdinalIgnoreCase))
            Refuse("AMM011",
                $"{asset.DisplayName} was made for a {texture.PixelFormat} {texture.Width}x{texture.Height} texture, " +
                $"but this client's {export.ObjectName} is {native.Manifest.PixelFormat} " +
                $"{native.Manifest.Width}x{native.Manifest.Height}. The mod author must update it.");
        var image = files[ArkiveModFileFormat.ImageRole];
        _ = await new TextureImportCandidateService().CreateAsync(
            new TextureImportCandidateRequest(native.ManifestPath, image, Path.Combine(work, "import-check"),
                texture.AlphaPolicy),
            cancellationToken).ConfigureAwait(false);
        var project = await _edits.Textures.CreateAsync(
            asset.DisplayName, asset.Target.PackageAlias, Path.GetFileName(check.PackagePath), check.PackagePath,
            export.ClassName, export.ObjectName, native.Manifest.SourceExportIndex, native.Manifest.PixelFormat,
            native.Manifest.Width, native.Manifest.Height, native.ManifestPath, image, texture.AlphaPolicy,
            cancellationToken).ConfigureAwait(false);
        await _edits.Textures.SetIncludedAsync(project.ManifestPath, false, cancellationToken).ConfigureAwait(false);
        return project.Manifest.ProjectId;
    }

    private async Task<string> MaterializeSkeletalMeshAsync(
        ArkiveModFileAsset asset,
        ArkiveModTargetCheck check,
        IReadOnlyDictionary<string, string> files,
        CancellationToken cancellationToken)
    {
        var export = check.Export!;
        var report = await _services.SkeletalMeshReconnaissance.InspectAsync(
            new SkeletalMeshReconnaissanceRequest(
                check.PackagePath, asset.Target.PackageAlias, export.ExportIndex - 1, AssetKind.SkeletalMesh,
                export.ClassName, export.ObjectName, export.SerialOffset, export.SerialSize, check.PackageSha256),
            cancellationToken).ConfigureAwait(false);
        if (report.Readiness != SkeletalMeshReadiness.NoOpReady)
            Refuse("AMM020", $"{asset.DisplayName}: {FirstError(report.Issues.Select(issue => (issue.Severity, issue.Message)))}");
        var recon = await _services.SkeletalMeshReconnaissanceStore.SaveAsync(report, cancellationToken)
            .ConfigureAwait(false);
        var editable = await _services.SkeletalMeshEditableExport.ExportAsync(
            new SkeletalMeshEditableExportRequest(
                report, recon.JsonPath,
                Path.Combine(recon.DirectoryPath, "editable-glb", $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}")),
            cancellationToken).ConfigureAwait(false);
        var source = new SkeletalMeshEditableWorkspaceEntry(
            editable.OutputDirectory, editable.ManifestPath, editable.GlbPath, editable.Manifest,
            GlbExists: true, GlbLengthMatches: true, IsCurrentSchema: true);
        var registration = await _edits.SkeletalMeshEdits.RegisterAsync(
            source, files[ArkiveModFileFormat.GlbRole], asset.DisplayName, cancellationToken).ConfigureAwait(false);
        if (registration.Preflight.Readiness == SkeletalMeshEditReadiness.Refused ||
            registration.Preflight.ErrorCount != 0)
        {
            await _edits.SkeletalMeshEdits.DeleteAsync(registration.Entry, cancellationToken).ConfigureAwait(false);
            Refuse("AMM021",
                $"{asset.DisplayName} failed SkeletalMesh preflight on this client: " +
                FirstError(registration.Preflight.Issues.Select(issue => (issue.Severity.ToString(), issue.Message))));
        }
        await _edits.SkeletalMeshEdits.SetIncludedAsync(registration.Entry, false, cancellationToken).ConfigureAwait(false);
        return registration.Entry.Manifest.EditId;
    }

    private async Task<string> MaterializeStaticMeshAsync(
        ArkiveModFileAsset asset,
        ArkiveModTargetCheck check,
        IReadOnlyDictionary<string, string> files,
        CancellationToken cancellationToken)
    {
        var export = check.Export!;
        var report = await _services.StaticMeshReconnaissance.InspectAsync(
            new StaticMeshReconnaissanceRequest(
                check.PackagePath, asset.Target.PackageAlias, export.ExportIndex - 1, AssetKind.StaticMesh,
                export.ClassName, export.ObjectName, export.SerialOffset, export.SerialSize, check.PackageSha256),
            cancellationToken).ConfigureAwait(false);
        if (report.Readiness != StaticMeshReadiness.Editable)
            Refuse("AMM030", $"{asset.DisplayName}: {FirstError(report.Issues.Select(issue => (issue.Severity, issue.Message)))}");
        var recon = await _services.StaticMeshReconnaissanceStore.SaveAsync(report, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var editable = await _services.StaticMeshEditableExport.ExportAsync(
            new StaticMeshEditableExportRequest(
                report, recon.JsonPath,
                Path.Combine(recon.DirectoryPath, "editable-glb", $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}")),
            cancellationToken).ConfigureAwait(false);
        var registered = await _edits.StaticMeshEdits.RegisterAsync(
            editable.ManifestPath, files[ArkiveModFileFormat.GlbRole], asset.DisplayName, cancellationToken)
            .ConfigureAwait(false);
        if (registered.Manifest.Preflight.Readiness == StaticMeshEditReadiness.Refused)
        {
            await _edits.StaticMeshEdits.DeleteAsync(registered, cancellationToken).ConfigureAwait(false);
            Refuse("AMM031",
                $"{asset.DisplayName} failed StaticMesh preflight on this client: " +
                string.Join(" | ", registered.Manifest.Preflight.Issues.Take(3)));
        }
        await _edits.StaticMeshEdits.SetIncludedAsync(registered, false, cancellationToken).ConfigureAwait(false);
        return registered.Manifest.EditId;
    }

    private async Task<string> MaterializeMaterialAsync(
        ArkiveModFileAsset asset,
        ArkiveModTargetCheck check,
        CancellationToken cancellationToken)
    {
        var export = check.Export!;
        var data = asset.Material!;
        var report = await _services.MaterialReconnaissance.InspectAsync(
            new MaterialInstanceReconnaissanceRequest(
                check.PackagePath, asset.Target.PackageAlias, export.ExportIndex - 1, AssetKind.Material,
                export.ClassName, export.ObjectName, export.SerialOffset, export.SerialSize, check.PackageSha256),
            cancellationToken).ConfigureAwait(false);
        if (report.Readiness != MaterialInstanceReadiness.Editable || report.NativeExportIndex is null)
            Refuse("AMM040", $"{asset.DisplayName}: {FirstError(report.Issues.Select(issue => (issue.Severity, issue.Message)))}");
        var recon = await _services.MaterialReconnaissanceStore.SaveAsync(report, cancellationToken).ConfigureAwait(false);
        var created = await _services.MaterialEdit.CreateEditAsync(
            recon.JsonPath, _edits.MaterialEdits.RootDirectory, asset.DisplayName, cancellationToken).ConfigureAwait(false);
        try
        {
            var document = OverlayChangedMaterialValues(created.Document, data, report, asset.DisplayName);
            var saved = await _edits.MaterialEdits.SaveAsync(
                created with { Document = document with { IncludedInPackageBuild = false } }, cancellationToken)
                .ConfigureAwait(false);
            var candidate = await _services.MaterialEdit.BuildCandidateAsync(
                saved.DocumentPath, _services.MaterialCandidateRoot, cancellationToken).ConfigureAwait(false);
            if (candidate.Manifest.ChangedByteCount <= 0)
                Refuse("AMM041", $"{asset.DisplayName} makes no change to this client's {export.ObjectName}.");
            await _edits.MaterialEdits.RecordCandidateAsync(saved, candidate, cancellationToken).ConfigureAwait(false);
            return saved.Document.EditId;
        }
        catch
        {
            await _edits.MaterialEdits.DeleteAsync(created, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private MaterialInstanceEditDocument OverlayChangedMaterialValues(
        MaterialInstanceEditDocument document,
        ArkiveModMaterialData data,
        MaterialInstanceReconnaissanceReport report,
        string displayName)
    {
        var scalars = document.Scalars.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var scalar in data.Scalars.Where(item => item.Changed))
        {
            if (!scalars.TryGetValue(scalar.Name, out var current))
                Refuse("AMM042", $"{displayName} changes scalar '{scalar.Name}', which this client's material no longer has.");
            scalars[scalar.Name] = current with { Value = scalar.Value };
        }
        var vectors = document.Vectors.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var vector in data.Vectors.Where(item => item.Changed))
        {
            if (!vectors.TryGetValue(vector.Name, out var current))
                Refuse("AMM043", $"{displayName} changes vector '{vector.Name}', which this client's material no longer has.");
            vectors[vector.Name] = current with
            {
                Red = vector.Red,
                Green = vector.Green,
                Blue = vector.Blue,
                Alpha = vector.Alpha,
            };
        }
        var catalog = _services.MaterialEdit.GetReferenceCatalog(report);
        var textures = document.Textures.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var reference in data.Textures.Where(item => item.Changed))
        {
            if (!textures.TryGetValue(reference.Name, out var current))
                Refuse("AMM044", $"{displayName} changes texture parameter '{reference.Name}', which this client's material no longer has.");
            var option = catalog.TextureTargets.FirstOrDefault(item =>
                             item.StableIdentity.Equals(reference.TargetIdentity, StringComparison.OrdinalIgnoreCase)) ??
                         throw new ArkiveModRefusalException("AMM045",
                             $"{displayName}'s texture '{reference.Name}' points to {reference.TargetResolvedObject}, which this client's package does not reference.");
            textures[reference.Name] = current with
            {
                ObjectReference = option.ObjectReference,
                ResolvedObject = option.ResolvedObject,
                TargetIdentity = option.StableIdentity,
            };
        }
        var parent = document.Parent;
        if (data.Parent is { Changed: true } parentReference)
        {
            if (parent is null)
                Refuse("AMM046", $"{displayName} changes the parent material, which this client's material does not expose.");
            var option = catalog.ParentTargets.FirstOrDefault(item =>
                             item.StableIdentity.Equals(parentReference.TargetIdentity, StringComparison.OrdinalIgnoreCase)) ??
                         throw new ArkiveModRefusalException("AMM047",
                             $"{displayName}'s parent material {parentReference.TargetResolvedObject} is not referenced by this client's package.");
            parent = parent with
            {
                ObjectReference = option.ObjectReference,
                ResolvedObject = option.ResolvedObject,
                TargetIdentity = option.StableIdentity,
            };
        }
        return document with
        {
            Scalars = document.Scalars.Select(item => scalars[item.Name]).ToArray(),
            Vectors = document.Vectors.Select(item => vectors[item.Name]).ToArray(),
            Textures = document.Textures.Select(item => textures[item.Name]).ToArray(),
            Parent = parent,
        };
    }

    public async Task<MaterialInstanceEditEntry> EnsureCandidateAsync(
        MaterialInstanceEditEntry material,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(material.Document.LatestCandidateManifestPath) &&
            File.Exists(material.Document.LatestCandidateManifestPath))
            return material;
        var candidate = await _services.MaterialEdit.BuildCandidateAsync(
            material.DocumentPath, _services.MaterialCandidateRoot, cancellationToken).ConfigureAwait(false);
        return await _edits.MaterialEdits.RecordCandidateAsync(material, candidate, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string FirstError(IEnumerable<(string Severity, string Message)> issues) =>
        issues.FirstOrDefault(issue => issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)).Message ??
        "Arkive could not prove a safe native layout for this asset.";

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new ArkiveModRefusalException(code, message);
}
