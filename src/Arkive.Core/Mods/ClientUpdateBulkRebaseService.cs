using Arkive.Core.Meshes;

namespace Arkive.Core.Mods;

public sealed record ClientUpdateBulkRebaseProgress(
    int CompletedPackages,
    int TotalPackages,
    string PackageName,
    string Message);

public sealed record ClientUpdateBulkRebaseResult(
    string AuditId,
    DateTimeOffset CompletedAt,
    IReadOnlyList<ClientUpdateRebaseResult> Packages,
    int TextureProjectCount,
    int MeshProjectCount,
    bool EveryPackageRebased,
    bool RolledBack,
    bool GameFilesModified,
    TimeSpan Duration)
{
    public int MaterialProjectCount { get; init; }
}

/// <summary>
/// Applies only the SafeAutomaticRebase subset of a complete client audit. All
/// existing project manifests are snapshotted first. A failure in any package
/// restores every prior package/texture/mesh manifest, while newly generated
/// mesh registrations are explicitly excluded from package composition.
/// </summary>
public sealed class ClientUpdateBulkRebaseService
{
    private readonly IClientUpdateRebaseService _rebase;
    private readonly GuidedTexturePackageWorkspace _packageWorkspace;
    private readonly SkeletalMeshEditWorkspaceStore _meshWorkspace;

    public ClientUpdateBulkRebaseService(
        IClientUpdateRebaseService rebase,
        GuidedTexturePackageWorkspace packageWorkspace,
        SkeletalMeshEditWorkspaceStore meshWorkspace)
    {
        _rebase = rebase ?? throw new ArgumentNullException(nameof(rebase));
        _packageWorkspace = packageWorkspace ?? throw new ArgumentNullException(nameof(packageWorkspace));
        _meshWorkspace = meshWorkspace ?? throw new ArgumentNullException(nameof(meshWorkspace));
    }

    public async Task<ClientUpdateBulkRebaseResult> RebaseSafePackagesAsync(
        ClientUpdateAuditReport audit,
        IReadOnlyList<ClientUpdatePackageInput> packageInputs,
        string outputRootDirectory,
        IProgress<ClientUpdateBulkRebaseProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(packageInputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRootDirectory);
        if (!audit.CanBulkRebase || audit.RequiresRestoreBeforeUpdateCheck ||
            !audit.EveryAffectedPackageFormatCompatible)
            throw new NotSupportedException(
                $"Bulk client-update rebase is unavailable: {audit.Summary}");

        var safeIds = audit.Packages
            .Where(report => report.State == ClientPackageCompatibilityState.SafeAutomaticRebase &&
                             report.CanAutomaticallyRebase)
            .Select(report => report.PackageProjectId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inputs = packageInputs.Where(input => safeIds.Contains(
                input.Package.Manifest.PackageProjectId))
            .OrderBy(input => input.Package.Manifest.PackageAlias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (inputs.Length != safeIds.Count)
            throw new NotSupportedException(
                "The current package-project workspace no longer matches the completed client audit. Run the full check again.");
        if (inputs.Length == 0)
            throw new NotSupportedException("The completed audit contains no safely rebaseable packages.");

        var started = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetFullPath(outputRootDirectory),
            $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{audit.AuditId[..8]}");
        Directory.CreateDirectory(root);
        var snapshots = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            await SnapshotAsync(input.Package.ManifestPath).ConfigureAwait(false);
            foreach (var texture in input.Textures.Where(texture =>
                         texture.Manifest.IncludedInPackageBuild is not false))
                await SnapshotAsync(texture.ManifestPath).ConfigureAwait(false);
            foreach (var mesh in input.Meshes.Where(mesh => mesh.Manifest.IncludedInPackageBuild))
                await SnapshotAsync(mesh.ManifestPath).ConfigureAwait(false);
            foreach (var material in input.Materials.Where(material => material.Document.IncludedInPackageBuild))
                await SnapshotAsync(material.DocumentPath).ConfigureAwait(false);
        }
        var priorMeshPaths = (await _meshWorkspace.LoadAsync(cancellationToken).ConfigureAwait(false))
            .Entries.Select(entry => Path.GetFullPath(entry.ManifestPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<ClientUpdateRebaseResult>(inputs.Length);
        try
        {
            for (var index = 0; index < inputs.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var input = inputs[index];
                progress?.Report(new ClientUpdateBulkRebaseProgress(
                    index, inputs.Length, input.Package.Manifest.PackageAlias,
                    "Refreshing verified unchanged texture, material, and mesh source evidence..."));
                var output = Path.Combine(root,
                    $"{index + 1:000}-{Safe(input.Package.Manifest.PackageAlias)}");
                var result = await _rebase.RebaseAsync(
                    input.Package, input.Textures, input.Meshes, input.Materials, output, cancellationToken)
                    .ConfigureAwait(false);
                await _packageWorkspace.RecordClientBaselineAsync(
                    input.Package.ManifestPath, result.CurrentPackageSha256, cancellationToken)
                    .ConfigureAwait(false);
                results.Add(result);
                progress?.Report(new ClientUpdateBulkRebaseProgress(
                    index + 1, inputs.Length, input.Package.Manifest.PackageAlias,
                    $"Rebased {result.Textures.Count:N0} texture, {result.Materials.Count:N0} material, and {result.Meshes.Count:N0} mesh project(s)."));
            }
            return new ClientUpdateBulkRebaseResult(
                audit.AuditId, DateTimeOffset.UtcNow, results.ToArray(),
                results.Sum(result => result.Textures.Count),
                results.Sum(result => result.Meshes.Count),
                results.Count == inputs.Length && results.All(result => result.EveryAssetRebased),
                RolledBack: false, GameFilesModified: false,
                DateTimeOffset.UtcNow - started)
            {
                MaterialProjectCount = results.Sum(result => result.Materials.Count),
            };
        }
        catch
        {
            foreach (var snapshot in snapshots)
            {
                try { await RestoreAtomicAsync(snapshot.Key, snapshot.Value).ConfigureAwait(false); }
                catch { /* Preserve primary failure; the snapshots remain in memory until exit. */ }
            }
            try
            {
                var currentMeshes = await _meshWorkspace.LoadAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                foreach (var created in currentMeshes.Entries.Where(entry =>
                             !priorMeshPaths.Contains(Path.GetFullPath(entry.ManifestPath)) &&
                             entry.Manifest.IncludedInPackageBuild))
                    await _meshWorkspace.SetIncludedAsync(created, false, CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch
            {
                // Existing manifests were restored. New diagnostic candidates are
                // retained but a secondary exclusion failure must not mask the cause.
            }
            throw;
        }

        async Task SnapshotAsync(string path)
        {
            var full = Path.GetFullPath(path);
            if (!snapshots.ContainsKey(full))
                snapshots[full] = await File.ReadAllBytesAsync(full, cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    private static async Task RestoreAtomicAsync(string path, byte[] bytes)
    {
        var temporary = path + $".{Guid.NewGuid():N}.bulk-rebase-rollback";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, CancellationToken.None)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "package" : safe[..Math.Min(80, safe.Length)];
    }
}
