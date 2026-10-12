using Arkive.Core.Assets;
using Arkive.Core.Inspection;
using Arkive.Core.Meshes;
using Arkive.Core.Materials;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

public sealed record ClientUpdateTextureRebase(
    string ProjectId,
    string ObjectName,
    string PreviousNativeManifestPath,
    string CurrentNativeManifestPath,
    int CurrentNativeExportIndex,
    string CurrentExportSha256);

public sealed record ClientUpdateMeshRebase(
    string PreviousEditId,
    string CurrentEditId,
    string ObjectName,
    string CurrentEditableManifestPath,
    string CurrentEditManifestPath,
    string CurrentExportSha256);

public sealed record ClientUpdateMaterialRebase(
    string EditId,
    string ObjectName,
    string PreviousReconnaissancePath,
    string CurrentReconnaissancePath,
    int CurrentNativeExportIndex,
    string CurrentExportSha256);

public sealed record ClientUpdateRebaseResult(
    string PackageProjectId,
    string PackageAlias,
    string PreviousPackageSha256,
    string CurrentPackageSha256,
    IReadOnlyList<ClientUpdateTextureRebase> Textures,
    IReadOnlyList<ClientUpdateMeshRebase> Meshes,
    bool EveryAssetRebased,
    bool GameFilesModified,
    TimeSpan Duration)
{
    public IReadOnlyList<ClientUpdateMaterialRebase> Materials { get; init; } = [];
}

public interface IClientUpdateRebaseService
{
    Task<ClientUpdateRebaseResult> RebaseAsync(
        GuidedTexturePackageEntry package,
        IReadOnlyList<GuidedTextureModProjectEntry> textures,
        IReadOnlyList<SkeletalMeshEditWorkspaceEntry> meshes,
        IReadOnlyList<MaterialInstanceEditEntry> materials,
        string outputRootDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Refreshes source manifests after a client update only when the compatibility
/// audit proved that every selected serialized export is byte-identical or that
/// a Texture2D retains identical properties, format, layout, and stored mip
/// payloads. User PNGs and edited GLBs are never replaced.
/// </summary>
public sealed class ClientUpdateRebaseService : IClientUpdateRebaseService
{
    private readonly ClientUpdateCompatibilityService _compatibility;
    private readonly TextureReconnaissanceService _textureReconnaissance;
    private readonly TextureReconnaissanceWorkspaceStore _textureReconWorkspace;
    private readonly NativeTextureExportService _nativeTextureExport;
    private readonly GuidedTextureModWorkspace _textureWorkspace;
    private readonly SkeletalMeshReconnaissanceService _meshReconnaissance;
    private readonly SkeletalMeshReconnaissanceWorkspaceStore _meshReconWorkspace;
    private readonly SkeletalMeshEditableExportService _meshEditableExport;
    private readonly SkeletalMeshEditWorkspaceStore _meshEditWorkspace;
    private readonly MaterialInstanceReconnaissanceService _materialReconnaissance;
    private readonly MaterialInstanceWorkspaceStore _materialReconWorkspace;
    private readonly MaterialInstanceEditWorkspaceStore _materialEditWorkspace;

    public ClientUpdateRebaseService(
        ClientUpdateCompatibilityService compatibility,
        TextureReconnaissanceService textureReconnaissance,
        TextureReconnaissanceWorkspaceStore textureReconWorkspace,
        NativeTextureExportService nativeTextureExport,
        GuidedTextureModWorkspace textureWorkspace,
        SkeletalMeshReconnaissanceService meshReconnaissance,
        SkeletalMeshReconnaissanceWorkspaceStore meshReconWorkspace,
        SkeletalMeshEditableExportService meshEditableExport,
        SkeletalMeshEditWorkspaceStore meshEditWorkspace,
        MaterialInstanceReconnaissanceService materialReconnaissance,
        MaterialInstanceWorkspaceStore materialReconWorkspace,
        MaterialInstanceEditWorkspaceStore materialEditWorkspace)
    {
        _compatibility = compatibility;
        _textureReconnaissance = textureReconnaissance;
        _textureReconWorkspace = textureReconWorkspace;
        _nativeTextureExport = nativeTextureExport;
        _textureWorkspace = textureWorkspace;
        _meshReconnaissance = meshReconnaissance;
        _meshReconWorkspace = meshReconWorkspace;
        _meshEditableExport = meshEditableExport;
        _meshEditWorkspace = meshEditWorkspace;
        _materialReconnaissance = materialReconnaissance;
        _materialReconWorkspace = materialReconWorkspace;
        _materialEditWorkspace = materialEditWorkspace;
    }

    public async Task<ClientUpdateRebaseResult> RebaseAsync(
        GuidedTexturePackageEntry package,
        IReadOnlyList<GuidedTextureModProjectEntry> textures,
        IReadOnlyList<SkeletalMeshEditWorkspaceEntry> meshes,
        IReadOnlyList<MaterialInstanceEditEntry> materials,
        string outputRootDirectory,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var compatibility = await _compatibility.InspectAsync(
            package, textures, meshes, materials, cancellationToken).ConfigureAwait(false);
        if (compatibility.State != ClientPackageCompatibilityState.SafeAutomaticRebase ||
            !compatibility.CanAutomaticallyRebase ||
            string.IsNullOrWhiteSpace(compatibility.CurrentPackageSha256) ||
            string.IsNullOrWhiteSpace(compatibility.ExpectedSourcePackageSha256))
            throw new NotSupportedException(
                $"Automatic client-update rebase is unavailable: {compatibility.Summary}");

        var root = Path.Combine(Path.GetFullPath(outputRootDirectory),
            $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var pendingTextures = new List<(GuidedTextureModProjectEntry Project,
            NativeTextureExportResult Native)>();
        var pendingMeshes = new List<(SkeletalMeshEditWorkspaceEntry Previous,
            SkeletalMeshEditRegistrationResult Current, SkeletalMeshEditableExportResult Editable)>();
        var pendingMaterials = new List<(MaterialInstanceEditEntry Previous,
            MaterialInstanceReconnaissanceEntry Current)>();
        var manifestRollback = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var project in textures.Where(project =>
                         project.Manifest.IncludedInPackageBuild is not false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var evidence = RequireEvidence(
                    compatibility, project.Manifest.ProjectId, project.Manifest.ObjectName);
                var report = await _textureReconnaissance.InspectAsync(
                    new TextureReconnaissanceRequest(
                        package.Manifest.SourcePackagePath, package.Manifest.PackageAlias,
                        checked(evidence.CurrentNativeExportIndex!.Value - 1), AssetKind.Texture,
                        project.Manifest.ClassName, project.Manifest.ObjectName,
                        evidence.CurrentSerialOffset!.Value, evidence.CurrentSerialSize!.Value,
                        compatibility.CurrentPackageSha256), cancellationToken).ConfigureAwait(false);
                var recon = await _textureReconWorkspace.SaveAsync(
                    report, cancellationToken).ConfigureAwait(false);
                var directory = Path.Combine(root, "textures", project.Manifest.ProjectId);
                var native = await _nativeTextureExport.ExportAsync(
                    new NativeTextureExportRequest(report, directory, recon.JsonPath),
                    cancellationToken).ConfigureAwait(false);
                if (native.Manifest.Width != project.Manifest.Width ||
                    native.Manifest.Height != project.Manifest.Height ||
                    !native.Manifest.PixelFormat.Equals(
                        project.Manifest.PixelFormat, StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException(
                        $"{project.Manifest.ObjectName} changed native layout during update rebase.");
                pendingTextures.Add((project, native));
            }

            foreach (var previous in meshes.Where(mesh => mesh.Manifest.IncludedInPackageBuild))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var evidence = RequireEvidence(
                    compatibility, previous.Manifest.EditId, previous.Manifest.ObjectName);
                var report = await _meshReconnaissance.InspectAsync(
                    new SkeletalMeshReconnaissanceRequest(
                        package.Manifest.SourcePackagePath, package.Manifest.PackageAlias,
                        checked(evidence.CurrentNativeExportIndex!.Value - 1), AssetKind.SkeletalMesh,
                        "SkeletalMesh", previous.Manifest.ObjectName,
                        evidence.CurrentSerialOffset!.Value, evidence.CurrentSerialSize!.Value,
                        compatibility.CurrentPackageSha256), cancellationToken).ConfigureAwait(false);
                var recon = await _meshReconWorkspace.SaveAsync(
                    report, cancellationToken).ConfigureAwait(false);
                var editableDirectory = Path.Combine(
                    recon.DirectoryPath, "editable-glb",
                    $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
                var editable = await _meshEditableExport.ExportAsync(
                    new SkeletalMeshEditableExportRequest(
                        report, recon.JsonPath, editableDirectory), cancellationToken).ConfigureAwait(false);
                var sourceEntry = new SkeletalMeshEditableWorkspaceEntry(
                    editable.OutputDirectory, editable.ManifestPath, editable.GlbPath,
                    editable.Manifest, GlbExists: true, GlbLengthMatches: true,
                    IsCurrentSchema: true);
                var registration = await _meshEditWorkspace.RegisterAsync(
                    sourceEntry, previous.EditedGlbPath,
                    $"{previous.Manifest.DisplayName} (client rebase {DateTime.Now:yyyy-MM-dd})",
                    cancellationToken).ConfigureAwait(false);
                if (registration.Preflight.Readiness == SkeletalMeshEditReadiness.Refused)
                    throw new NotSupportedException(
                        $"{previous.Manifest.ObjectName} failed semantic preflight against the updated client.");
                pendingMeshes.Add((previous, registration, editable));
            }
            foreach (var previous in materials.Where(material => material.Document.IncludedInPackageBuild))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var evidence = RequireEvidence(
                    compatibility, previous.Document.EditId, previous.Document.ObjectName);
                var report = await _materialReconnaissance.InspectAsync(
                    new MaterialInstanceReconnaissanceRequest(
                        package.Manifest.SourcePackagePath, package.Manifest.PackageAlias,
                        checked(evidence.CurrentNativeExportIndex!.Value - 1), AssetKind.Material,
                        previous.Document.ClassName, previous.Document.ObjectName,
                        evidence.CurrentSerialOffset!.Value, evidence.CurrentSerialSize!.Value,
                        compatibility.CurrentPackageSha256), cancellationToken).ConfigureAwait(false);
                var recon = await _materialReconWorkspace.SaveAsync(report, cancellationToken)
                    .ConfigureAwait(false);
                pendingMaterials.Add((previous, recon));
            }

            foreach (var pending in pendingTextures)
                manifestRollback[Path.GetFullPath(pending.Project.ManifestPath)] =
                    await File.ReadAllBytesAsync(pending.Project.ManifestPath, cancellationToken).ConfigureAwait(false);
            foreach (var pending in pendingMeshes)
            {
                manifestRollback[Path.GetFullPath(pending.Previous.ManifestPath)] =
                    await File.ReadAllBytesAsync(pending.Previous.ManifestPath, cancellationToken).ConfigureAwait(false);
                manifestRollback[Path.GetFullPath(pending.Current.Entry.ManifestPath)] =
                    await File.ReadAllBytesAsync(pending.Current.Entry.ManifestPath, cancellationToken).ConfigureAwait(false);
            }
            foreach (var pending in pendingMaterials)
                manifestRollback[Path.GetFullPath(pending.Previous.DocumentPath)] =
                    await File.ReadAllBytesAsync(pending.Previous.DocumentPath, cancellationToken).ConfigureAwait(false);

            var textureResults = new List<ClientUpdateTextureRebase>();
            foreach (var pending in pendingTextures)
            {
                await _textureWorkspace.RebaseSourceAsync(
                    pending.Project.ManifestPath, pending.Native.Manifest,
                    pending.Native.ManifestPath, cancellationToken).ConfigureAwait(false);
                textureResults.Add(new ClientUpdateTextureRebase(
                    pending.Project.Manifest.ProjectId, pending.Project.Manifest.ObjectName,
                    pending.Project.Manifest.NativeTextureManifestPath,
                    pending.Native.ManifestPath,
                    pending.Native.Manifest.NativeExportIndex ?? 0,
                    pending.Native.Manifest.SourcePayloadSha256));
            }
            var meshResults = new List<ClientUpdateMeshRebase>();
            foreach (var pending in pendingMeshes)
            {
                await _meshEditWorkspace.SetIncludedAsync(
                    pending.Previous, false, cancellationToken).ConfigureAwait(false);
                await _meshEditWorkspace.SetIncludedAsync(
                    pending.Current.Entry, true, cancellationToken).ConfigureAwait(false);
                meshResults.Add(new ClientUpdateMeshRebase(
                    pending.Previous.Manifest.EditId, pending.Current.Entry.Manifest.EditId,
                    pending.Previous.Manifest.ObjectName, pending.Editable.ManifestPath,
                    pending.Current.Entry.ManifestPath,
                    pending.Editable.Manifest.SourceExportSha256));
            }
            var materialResults = new List<ClientUpdateMaterialRebase>();
            foreach (var pending in pendingMaterials)
            {
                var updated = await _materialEditWorkspace.RebaseSourceAsync(
                    pending.Previous, pending.Current.Report, pending.Current.JsonPath, cancellationToken)
                    .ConfigureAwait(false);
                materialResults.Add(new ClientUpdateMaterialRebase(
                    updated.Document.EditId, updated.Document.ObjectName,
                    pending.Previous.Document.SourceReconnaissancePath,
                    updated.Document.SourceReconnaissancePath,
                    pending.Current.Report.NativeExportIndex!.Value,
                    pending.Current.Report.ExportPayloadSha256));
            }
            return new ClientUpdateRebaseResult(
                package.Manifest.PackageProjectId, package.Manifest.PackageAlias,
                compatibility.ExpectedSourcePackageSha256,
                compatibility.CurrentPackageSha256,
                textureResults, meshResults,
                textureResults.Count == pendingTextures.Count &&
                meshResults.Count == pendingMeshes.Count &&
                materialResults.Count == pendingMaterials.Count,
                GameFilesModified: false,
                DateTimeOffset.UtcNow - started)
            {
                Materials = materialResults,
            };
        }
        catch
        {
            foreach (var snapshot in manifestRollback)
            {
                try
                {
                    var temporary = snapshot.Key + ".rebase-rollback.tmp";
                    await File.WriteAllBytesAsync(temporary, snapshot.Value, CancellationToken.None).ConfigureAwait(false);
                    File.Move(temporary, snapshot.Key, overwrite: true);
                }
                catch
                {
                    // Preserve every failed artifact for diagnostics; the original
                    // exception remains the primary refusal.
                }
            }
            foreach (var pending in pendingMeshes)
            {
                if (manifestRollback.ContainsKey(Path.GetFullPath(pending.Current.Entry.ManifestPath)))
                    continue;
                try
                {
                    await _meshEditWorkspace.SetIncludedAsync(
                        pending.Current.Entry, false, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the failed candidate for diagnostics but never select it.
                }
            }
            throw;
        }
    }

    private static ClientAssetCompatibility RequireEvidence(
        ClientPackageCompatibilityReport report, string id, string objectName)
    {
        var evidence = report.Assets.FirstOrDefault(item =>
            item.AssetId.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (evidence is null || evidence.State != ClientAssetCompatibilityState.Unchanged ||
            evidence.CurrentNativeExportIndex is null || evidence.CurrentSerialOffset is null ||
            evidence.CurrentSerialSize is null)
            throw new NotSupportedException(
                $"{objectName} does not have byte-identical or semantically unchanged client-update evidence.");
        return evidence;
    }
}
