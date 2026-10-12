using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

public sealed record ArkiveModEditWorkspaces(
    GuidedTextureModWorkspace Textures,
    SkeletalMeshEditWorkspaceStore SkeletalMeshEdits,
    StaticMeshEditWorkspaceStore StaticMeshEdits,
    MaterialInstanceEditWorkspaceStore MaterialEdits);

public sealed record ArkiveModLocalEdit(
    UnifiedPackageAssetKind Kind,
    string EditId,
    string ManifestPath,
    string DisplayName,
    string SourcePackagePath,
    string ObjectName,
    bool Included,
    object Entry)
{
    public ArkiveModTargetKey TargetKey => ArkiveModTargetKey.From(
        Path.GetFileName(SourcePackagePath), Kind, ObjectName);
}

public sealed class ArkiveModEditSnapshot
{
    private static readonly ConcurrentDictionary<string, (long Length, DateTime WriteTime, string Sha256)> HashCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ArkiveModLocalEdit> _byId;

    private ArkiveModEditSnapshot(IReadOnlyList<ArkiveModLocalEdit> edits)
    {
        Edits = edits;
        _byId = edits.GroupBy(edit => edit.EditId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ArkiveModLocalEdit> Edits { get; }

    public ArkiveModLocalEdit? Find(string editId) => _byId.GetValueOrDefault(editId);

    public IReadOnlyList<ArkiveModLocalEdit> ForPackage(string sourcePackagePath) => Edits
        .Where(edit => SamePath(edit.SourcePackagePath, sourcePackagePath)).ToArray();

    public IReadOnlyList<ArkiveModLocalEdit> DraftsFor(string sourcePackagePath, ArkiveModLibraryManifest library) =>
        ForPackage(sourcePackagePath).Where(edit => edit.Included && library.FindOwner(edit.EditId) is null).ToArray();

    public static async Task<ArkiveModEditSnapshot> LoadAsync(
        ArkiveModEditWorkspaces workspaces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        var edits = new List<ArkiveModLocalEdit>();
        foreach (var texture in await workspaces.Textures.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            edits.Add(new ArkiveModLocalEdit(
                UnifiedPackageAssetKind.Texture2D, texture.Manifest.ProjectId, texture.ManifestPath,
                texture.Manifest.DisplayName, Path.GetFullPath(texture.Manifest.SourcePackagePath),
                texture.Manifest.ObjectName, texture.Manifest.IncludedInPackageBuild is not false, texture));
        }
        foreach (var mesh in (await workspaces.SkeletalMeshEdits.LoadAsync(cancellationToken).ConfigureAwait(false)).Entries)
        {
            try
            {
                var source = await LoadSkeletalSourceAsync(mesh, cancellationToken).ConfigureAwait(false);
                edits.Add(new ArkiveModLocalEdit(
                    UnifiedPackageAssetKind.SkeletalMesh, mesh.Manifest.EditId, mesh.ManifestPath,
                    mesh.Manifest.DisplayName, Path.GetFullPath(source.SourcePackagePath), source.ObjectName,
                    mesh.Manifest.IncludedInPackageBuild, mesh));
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or
                                                  UnauthorizedAccessException)
            {
            }
        }
        foreach (var mesh in await workspaces.StaticMeshEdits.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var source = await StaticMeshEditableExportService.LoadManifestAsync(
                    mesh.Manifest.SourceEditableManifestPath, cancellationToken).ConfigureAwait(false);
                edits.Add(new ArkiveModLocalEdit(
                    UnifiedPackageAssetKind.StaticMesh, mesh.Manifest.EditId, mesh.ManifestPath,
                    mesh.Manifest.DisplayName, Path.GetFullPath(source.SourcePackagePath), source.ObjectName,
                    mesh.Manifest.IncludedInPackageBuild, mesh));
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or
                                                  NotSupportedException or UnauthorizedAccessException)
            {
            }
        }
        foreach (var material in await workspaces.MaterialEdits.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            edits.Add(new ArkiveModLocalEdit(
                UnifiedPackageAssetKind.MaterialInstanceConstant, material.Document.EditId, material.DocumentPath,
                material.Document.DisplayName, Path.GetFullPath(material.Document.SourcePackagePath),
                material.Document.ObjectName, material.Document.IncludedInPackageBuild, material));
        }
        return new ArkiveModEditSnapshot(edits);
    }

    public static async Task<string?> GetBuildContentSha256Async(
        ArkiveModLocalEdit edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return edit.Entry switch
        {
            GuidedTextureModProjectEntry texture => File.Exists(texture.Manifest.EditableImagePath)
                ? await HashFileCachedAsync(texture.Manifest.EditableImagePath, cancellationToken).ConfigureAwait(false)
                : null,
            SkeletalMeshEditWorkspaceEntry mesh => File.Exists(mesh.EditedGlbPath)
                ? mesh.Manifest.EditedGlbSha256.ToUpperInvariant()
                : null,
            StaticMeshEditEntry mesh => File.Exists(mesh.Manifest.EditedGlbPath)
                ? mesh.Manifest.EditedGlbSha256.ToUpperInvariant()
                : null,
            MaterialInstanceEditEntry material =>
                !string.IsNullOrWhiteSpace(material.Document.LatestCandidateManifestPath) &&
                File.Exists(material.Document.LatestCandidateManifestPath)
                    ? material.Document.LatestCandidateSha256?.ToUpperInvariant()
                    : null,
            _ => null,
        };
    }

    public static async Task<string?> GetSourcePackageSha256Async(
        ArkiveModLocalEdit edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        try
        {
            return edit.Entry switch
            {
                GuidedTextureModProjectEntry texture => (await NativeTextureExportService.LoadManifestAsync(
                    texture.Manifest.NativeTextureManifestPath, cancellationToken).ConfigureAwait(false))
                    .SourcePackageSha256.ToUpperInvariant(),
                SkeletalMeshEditWorkspaceEntry mesh => (await LoadSkeletalSourceAsync(mesh, cancellationToken)
                    .ConfigureAwait(false)).SourcePackageSha256.ToUpperInvariant(),
                StaticMeshEditEntry mesh => (await StaticMeshEditableExportService.LoadManifestAsync(
                    mesh.Manifest.SourceEditableManifestPath, cancellationToken).ConfigureAwait(false))
                    .SourcePackageSha256.ToUpperInvariant(),
                MaterialInstanceEditEntry material => material.Document.SourcePackageSha256.ToUpperInvariant(),
                _ => null,
            };
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or
                                              NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static async Task<SkeletalMeshEditableExportManifest> LoadSkeletalSourceAsync(
        SkeletalMeshEditWorkspaceEntry mesh,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            Path.GetFullPath(mesh.Manifest.SourceManifestPath), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<SkeletalMeshEditableExportManifest>(
                   stream, ArkiveModFileService.JsonOptions, cancellationToken).ConfigureAwait(false) ??
               throw new InvalidDataException("The editable SkeletalMesh source manifest was empty.");
    }

    internal static async Task<string> HashFileCachedAsync(string path, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (HashCache.TryGetValue(full, out var cached) &&
            cached.Length == info.Length && cached.WriteTime == info.LastWriteTimeUtc)
            return cached.Sha256;
        await using var stream = new FileStream(
            full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        HashCache[full] = (info.Length, info.LastWriteTimeUtc, hash);
        return hash;
    }

    internal static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
