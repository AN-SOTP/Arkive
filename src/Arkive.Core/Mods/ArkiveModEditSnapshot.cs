using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Arkive.Core.Interface;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.Particles;
using Arkive.Core.Physics;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

public sealed record ArkiveModEditWorkspaces(
    GuidedTextureModWorkspace Textures,
    SkeletalMeshEditWorkspaceStore SkeletalMeshEdits,
    StaticMeshEditWorkspaceStore StaticMeshEdits,
    MaterialInstanceEditWorkspaceStore MaterialEdits)
{
    /// <summary>Particle effect working copies, in staging\particle-edits beside the material edits.</summary>
    public ParticleEffectEditWorkspaceStore ParticleEdits { get; init; } = new(
        Path.Combine(Path.GetDirectoryName(MaterialEdits.RootDirectory)!, "particle-edits"));
    /// <summary>Physics asset working copies (Simple upscale's matched body shapes), in staging\physics-edits.</summary>
    public PhysicsAssetEditWorkspaceStore PhysicsEdits { get; init; } = new(
        Path.Combine(Path.GetDirectoryName(MaterialEdits.RootDirectory)!, "physics-edits"));
    /// <summary>Interface movie working copies; null where a caller does not edit interface movies.</summary>
    public InterfaceMovieEditWorkspaceStore? InterfaceMovies { get; init; }
}

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
    /// <summary>The target's path inside its package when its name is not unique there; see ArkiveModObjectPath.</summary>
    public string? ObjectPath { get; init; } = ArkiveModObjectPath.Read(ManifestPath);

    public ArkiveModTargetKey TargetKey => ArkiveModTargetKey.From(
        Path.GetFileName(SourcePackagePath), Kind, ObjectPath ?? ObjectName);
}

public sealed class ArkiveModEditSnapshot
{
    private static readonly ConcurrentDictionary<string, (long Length, DateTime WriteTime, string Sha256)> HashCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Every edit's source record is read each time a snapshot is built; it is parsed again only when its file changes.
    private static readonly ConcurrentDictionary<string, (long Length, DateTime WriteTime, SkeletalMeshEditableExportManifest Manifest)>
        SkeletalSourceCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ArkiveModLocalEdit> _byId;
    private readonly Lazy<ILookup<string, ArkiveModLocalEdit>> _byPackage;

    private ArkiveModEditSnapshot(IReadOnlyList<ArkiveModLocalEdit> edits)
    {
        Edits = edits;
        _byId = edits.GroupBy(edit => edit.EditId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        _byPackage = new(() => edits.ToLookup(edit => Path.GetFullPath(edit.SourcePackagePath), StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ArkiveModLocalEdit> Edits { get; }

    public ArkiveModLocalEdit? Find(string editId) => _byId.GetValueOrDefault(editId);

    /// <summary>This snapshot with the given edits, as just saved, in place of the ones read from the same files.</summary>
    internal ArkiveModEditSnapshot Replace(IReadOnlyCollection<ArkiveModLocalEdit> saved)
    {
        if (saved.Count == 0) return this;
        var byPath = saved.ToDictionary(edit => edit.ManifestPath, StringComparer.OrdinalIgnoreCase);
        return new ArkiveModEditSnapshot(Edits.Select(edit => byPath.GetValueOrDefault(edit.ManifestPath) ?? edit).ToArray());
    }

    public IReadOnlyList<ArkiveModLocalEdit> ForPackage(string sourcePackagePath) =>
        _byPackage.Value[Path.GetFullPath(sourcePackagePath)].ToArray();

    public IReadOnlyList<ArkiveModLocalEdit> DraftsFor(string sourcePackagePath, ArkiveModLibraryManifest library) =>
        ForPackage(sourcePackagePath).Where(edit => edit.Included && library.FindOwner(edit.EditId) is null).ToArray();

    public static async Task<ArkiveModEditSnapshot> LoadAsync(
        ArkiveModEditWorkspaces workspaces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        var edits = new List<ArkiveModLocalEdit>();
        foreach (var texture in await workspaces.Textures.LoadAsync(cancellationToken).ConfigureAwait(false))
            edits.Add(From(texture));
        foreach (var mesh in (await workspaces.SkeletalMeshEdits.LoadAsync(cancellationToken).ConfigureAwait(false)).Entries)
        {
            try
            {
                edits.Add(await FromAsync(mesh, cancellationToken).ConfigureAwait(false));
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
                edits.Add(await FromAsync(mesh, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or
                                                  NotSupportedException or UnauthorizedAccessException)
            {
            }
        }
        foreach (var material in await workspaces.MaterialEdits.LoadAsync(cancellationToken).ConfigureAwait(false))
            edits.Add(From(material));
        foreach (var particle in await workspaces.ParticleEdits.LoadAsync(cancellationToken).ConfigureAwait(false))
            edits.Add(From(particle));
        foreach (var physics in await workspaces.PhysicsEdits.LoadAsync(cancellationToken).ConfigureAwait(false))
            edits.Add(From(physics));
        if (workspaces.InterfaceMovies is { } movies)
            foreach (var movie in await movies.LoadAsync(cancellationToken).ConfigureAwait(false))
                edits.Add(From(movie));
        return new ArkiveModEditSnapshot(edits);
    }

    /// <summary>
    /// <paramref name="edit"/>'s working file read again from disk, or null when it is gone or can no longer be read
    /// (as a full load would leave it out).
    /// </summary>
    public static async Task<ArkiveModLocalEdit?> ReloadAsync(
        ArkiveModEditWorkspaces workspaces,
        ArkiveModLocalEdit edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(edit);
        if (!File.Exists(edit.ManifestPath)) return null;
        try
        {
            return edit.Entry switch
            {
                GuidedTextureModProjectEntry => From(await GuidedTextureModWorkspace.LoadProjectAsync(
                    edit.ManifestPath, cancellationToken).ConfigureAwait(false)),
                SkeletalMeshEditWorkspaceEntry => await FromAsync(await workspaces.SkeletalMeshEdits.LoadEntryAsync(
                    edit.ManifestPath, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false),
                StaticMeshEditEntry => await FromAsync(await workspaces.StaticMeshEdits.LoadEntryAsync(
                    edit.ManifestPath, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false),
                MaterialInstanceEditEntry => From(await workspaces.MaterialEdits.LoadEntryAsync(
                    edit.ManifestPath, cancellationToken).ConfigureAwait(false)),
                ParticleEffectEditEntry => From(await workspaces.ParticleEdits.LoadEntryAsync(
                    edit.ManifestPath, cancellationToken).ConfigureAwait(false)),
                PhysicsAssetEditEntry => From(await workspaces.PhysicsEdits.LoadEntryAsync(
                    edit.ManifestPath, cancellationToken).ConfigureAwait(false)),
                InterfaceMovieEditEntry => From(await InterfaceMovieEditWorkspaceStore.LoadEntryAsync(
                    edit.ManifestPath, cancellationToken).ConfigureAwait(false)),
                _ => null,
            };
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or
                                              NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static ArkiveModLocalEdit From(GuidedTextureModProjectEntry texture) => new(
        UnifiedPackageAssetKind.Texture2D, texture.Manifest.ProjectId, texture.ManifestPath,
        texture.Manifest.DisplayName, Path.GetFullPath(texture.Manifest.SourcePackagePath),
        texture.Manifest.ObjectName, texture.Manifest.IncludedInPackageBuild is not false, texture);

    private static async Task<ArkiveModLocalEdit> FromAsync(SkeletalMeshEditWorkspaceEntry mesh, CancellationToken cancellationToken)
    {
        var source = await LoadSkeletalSourceAsync(mesh, cancellationToken).ConfigureAwait(false);
        return new ArkiveModLocalEdit(
            UnifiedPackageAssetKind.SkeletalMesh, mesh.Manifest.EditId, mesh.ManifestPath,
            mesh.Manifest.DisplayName, Path.GetFullPath(source.SourcePackagePath), source.ObjectName,
            mesh.Manifest.IncludedInPackageBuild, mesh);
    }

    private static async Task<ArkiveModLocalEdit> FromAsync(StaticMeshEditEntry mesh, CancellationToken cancellationToken)
    {
        var source = await StaticMeshEditableExportService.LoadManifestAsync(
            mesh.Manifest.SourceEditableManifestPath, cancellationToken).ConfigureAwait(false);
        return new ArkiveModLocalEdit(
            UnifiedPackageAssetKind.StaticMesh, mesh.Manifest.EditId, mesh.ManifestPath,
            mesh.Manifest.DisplayName, Path.GetFullPath(source.SourcePackagePath), source.ObjectName,
            mesh.Manifest.IncludedInPackageBuild, mesh);
    }

    private static ArkiveModLocalEdit From(MaterialInstanceEditEntry material) => new(
        UnifiedPackageAssetKind.MaterialInstanceConstant, material.Document.EditId, material.DocumentPath,
        material.Document.DisplayName, Path.GetFullPath(material.Document.SourcePackagePath),
        material.Document.ObjectName, material.Document.IncludedInPackageBuild, material);

    private static ArkiveModLocalEdit From(ParticleEffectEditEntry particle) => new(
        UnifiedPackageAssetKind.ParticleSystem, particle.Document.EditId, particle.DocumentPath,
        particle.Document.DisplayName, Path.GetFullPath(particle.Document.SourcePackagePath),
        particle.Document.SystemObjectName, particle.Document.IncludedInPackageBuild, particle);

    private static ArkiveModLocalEdit From(PhysicsAssetEditEntry physics) => new(
        UnifiedPackageAssetKind.PhysicsAsset, physics.Document.EditId, physics.DocumentPath,
        physics.Document.DisplayName, Path.GetFullPath(physics.Document.SourcePackagePath),
        physics.Document.AssetObjectName, physics.Document.IncludedInPackageBuild, physics);

    private static ArkiveModLocalEdit From(InterfaceMovieEditEntry movie) => new(
        UnifiedPackageAssetKind.InterfaceMovie, movie.Manifest.EditId, movie.ManifestPath,
        movie.Manifest.DisplayName, Path.GetFullPath(movie.Manifest.SourcePackagePath),
        movie.Manifest.ObjectName, movie.Manifest.IncludedInPackageBuild, movie);

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
            ParticleEffectEditEntry particle => particle.Document.ContentSha256,
            PhysicsAssetEditEntry physics => physics.Document.ContentSha256,
            InterfaceMovieEditEntry movie => File.Exists(movie.Manifest.EditedMoviePath)
                ? await HashFileCachedAsync(movie.Manifest.EditedMoviePath, cancellationToken).ConfigureAwait(false)
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
                ParticleEffectEditEntry particle => particle.Document.SourcePackageSha256.ToUpperInvariant(),
                PhysicsAssetEditEntry physics => physics.Document.SourcePackageSha256.ToUpperInvariant(),
                InterfaceMovieEditEntry movie => movie.Manifest.SourcePackageSha256.ToUpperInvariant(),
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
        var full = Path.GetFullPath(mesh.Manifest.SourceManifestPath);
        var info = new FileInfo(full);
        if (SkeletalSourceCache.TryGetValue(full, out var cached) &&
            cached.Length == info.Length && cached.WriteTime == info.LastWriteTimeUtc)
            return cached.Manifest;
        await using var stream = new FileStream(
            full, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<SkeletalMeshEditableExportManifest>(
                           stream, ArkiveModFileService.JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new InvalidDataException("The editable SkeletalMesh source manifest was empty.");
        SkeletalSourceCache[full] = (info.Length, info.LastWriteTimeUtc, manifest);
        return manifest;
    }

    /// <summary>A file's SHA-256, hashed again only when its length or write time changes.</summary>
    public static async Task<string> HashFileCachedAsync(string path, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Records a hash a caller has just computed by reading the whole file, if the file still has the length and write
    /// time it had before it was read (so a file changed while it was read is not recorded).
    /// </summary>
    public static void RememberHash(string path, long length, DateTime writeTimeUtc, string sha256)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (info.Exists && info.Length == length && info.LastWriteTimeUtc == writeTimeUtc)
            HashCache[full] = (length, writeTimeUtc, sha256);
    }

    private sealed record SavedHash(string Path, long Length, long WriteTimeTicks, string Sha256);

    /// <summary>
    /// Loads hashes saved by <see cref="SaveHashCache"/>, so a restart does not read every working file and game
    /// package again. A saved hash is used only while its file keeps the same length and write time.
    /// </summary>
    public static void LoadHashCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            foreach (var saved in JsonSerializer.Deserialize<SavedHash[]>(File.ReadAllBytes(path)) ?? [])
                if (saved.Sha256?.Length == 64)
                    HashCache.TryAdd(saved.Path, (saved.Length, new DateTime(saved.WriteTimeTicks, DateTimeKind.Utc), saved.Sha256));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
    }

    /// <summary>Saves the hashes of files that still exist unchanged.</summary>
    public static void SaveHashCache(string path)
    {
        var current = HashCache
            .Where(pair => new FileInfo(pair.Key) is { Exists: true } info &&
                           info.Length == pair.Value.Length && info.LastWriteTimeUtc == pair.Value.WriteTime)
            .Select(pair => new SavedHash(pair.Key, pair.Value.Length, pair.Value.WriteTime.Ticks, pair.Value.Sha256))
            .ToArray();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(current));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    internal static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
