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
    private string? _objectPath;
    private bool _objectPathRead;

    /// <summary>
    /// The target's path inside its package when its name is not unique there; see ArkiveModObjectPath. Read from the
    /// note beside the record when first asked for, unless given: an edit made from the working-file index already
    /// knows it, and looking for the note would open a file for each of thousands of edits at launch.
    /// </summary>
    public string? ObjectPath
    {
        get
        {
            if (!_objectPathRead)
            {
                _objectPath = ArkiveModObjectPath.Read(ManifestPath);
                _objectPathRead = true;
            }
            return _objectPath;
        }
        init
        {
            _objectPath = value;
            _objectPathRead = true;
        }
    }

    public ArkiveModTargetKey TargetKey => ArkiveModTargetKey.From(
        Path.GetFileName(SourcePackagePath), Kind, ObjectPath ?? ObjectName);

    // Equal edits stay equal whether or not their object path has been read yet.
    public bool Equals(ArkiveModLocalEdit? other) =>
        other is not null && (ReferenceEquals(this, other) ||
                              Kind == other.Kind && EditId == other.EditId && ManifestPath == other.ManifestPath &&
                              DisplayName == other.DisplayName && SourcePackagePath == other.SourcePackagePath &&
                              ObjectName == other.ObjectName && Included == other.Included && Equals(Entry, other.Entry) &&
                              ObjectPath == other.ObjectPath);

    public override int GetHashCode() =>
        HashCode.Combine(Kind, EditId, ManifestPath, DisplayName, SourcePackagePath, ObjectName, Included, Entry);
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

    private ArkiveModEditSnapshot(IReadOnlyList<ArkiveModLocalEdit> edits, bool fromIndex = false)
    {
        Edits = edits;
        FromIndex = fromIndex;
        _byId = edits.GroupBy(edit => edit.EditId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        _byPackage = new(() => edits.ToLookup(edit => Path.GetFullPath(edit.SourcePackagePath), StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ArkiveModLocalEdit> Edits { get; }

    /// <summary>
    /// Some edits come from the working-file index (see <see cref="WorkingFileIndex.SnapshotAsync"/>) instead of their
    /// records. Such a snapshot only feeds what the Mods list shows; anything that writes reads the records itself.
    /// </summary>
    public bool FromIndex { get; }

    public ArkiveModLocalEdit? Find(string editId) => _byId.GetValueOrDefault(editId);

    /// <summary>A snapshot of edits made from the working-file index, some of them read again from their records.</summary>
    internal static ArkiveModEditSnapshot FromIndexedEdits(IReadOnlyList<ArkiveModLocalEdit> edits) => new(edits, fromIndex: true);

    /// <summary>This snapshot with the given edits, as just saved, in place of the ones read from the same files.</summary>
    internal ArkiveModEditSnapshot Replace(IReadOnlyCollection<ArkiveModLocalEdit> saved)
    {
        if (saved.Count == 0) return this;
        var byPath = saved.ToDictionary(edit => edit.ManifestPath, StringComparer.OrdinalIgnoreCase);
        var edits = Edits.Select(edit => byPath.GetValueOrDefault(edit.ManifestPath) ?? edit).ToArray();
        return new ArkiveModEditSnapshot(edits, edits.Any(edit => edit.Entry is ArkiveModIndexedEdit));
    }

    /// <summary>
    /// Told of every full read of the working files (<see cref="LoadAsync"/>): the stores read, the snapshot and when the read
    /// began, so the window can bring its working-file index up to date. Nothing is told when no one listens.
    /// </summary>
    public static event Action<ArkiveModEditWorkspaces, ArkiveModEditSnapshot, DateTime>? FullReadCompleted;

    public IReadOnlyList<ArkiveModLocalEdit> ForPackage(string sourcePackagePath) =>
        _byPackage.Value[Path.GetFullPath(sourcePackagePath)].ToArray();

    public IReadOnlyList<ArkiveModLocalEdit> DraftsFor(string sourcePackagePath, ArkiveModLibraryManifest library) =>
        ForPackage(sourcePackagePath).Where(edit => edit.Included && library.FindOwner(edit.EditId) is null).ToArray();

    /// <param name="beforeEachStore">
    /// Runs before each store is read, so a caller reading in the background can wait there while the user is busy.
    /// </param>
    public static async Task<ArkiveModEditSnapshot> LoadAsync(
        ArkiveModEditWorkspaces workspaces,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? beforeEachStore = null)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        var readStart = DateTime.UtcNow;
        var edits = new List<ArkiveModLocalEdit>();
        Task Turn() => beforeEachStore?.Invoke(cancellationToken) ?? Task.CompletedTask;
        await Turn().ConfigureAwait(false);
        foreach (var texture in await workspaces.Textures.LoadAsync(cancellationToken).ConfigureAwait(false))
            edits.Add(From(texture));
        await Turn().ConfigureAwait(false);
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
        await Turn().ConfigureAwait(false);
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
        await Turn().ConfigureAwait(false);
        foreach (var material in await workspaces.MaterialEdits.LoadAsync(cancellationToken).ConfigureAwait(false))
            edits.Add(From(material));
        await Turn().ConfigureAwait(false);
        foreach (var particle in await workspaces.ParticleEdits.LoadAsync(cancellationToken).ConfigureAwait(false))
            edits.Add(From(particle));
        await Turn().ConfigureAwait(false);
        foreach (var physics in await workspaces.PhysicsEdits.LoadAsync(cancellationToken).ConfigureAwait(false))
            edits.Add(From(physics));
        await Turn().ConfigureAwait(false);
        if (workspaces.InterfaceMovies is { } movies)
            foreach (var movie in await movies.LoadAsync(cancellationToken).ConfigureAwait(false))
                edits.Add(From(movie));
        var snapshot = new ArkiveModEditSnapshot(edits);
        FullReadCompleted?.Invoke(workspaces, snapshot, readStart);
        return snapshot;
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
            // An edit from the working-file index is read from its record by the store of its kind.
            if (edit.Entry is ArkiveModIndexedEdit)
                return edit.Kind switch
                {
                    UnifiedPackageAssetKind.Texture2D => From(await GuidedTextureModWorkspace.LoadProjectAsync(
                        edit.ManifestPath, cancellationToken).ConfigureAwait(false)),
                    UnifiedPackageAssetKind.SkeletalMesh => await FromAsync(await workspaces.SkeletalMeshEdits.LoadEntryAsync(
                        edit.ManifestPath, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false),
                    UnifiedPackageAssetKind.StaticMesh => await FromAsync(await workspaces.StaticMeshEdits.LoadEntryAsync(
                        edit.ManifestPath, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false),
                    UnifiedPackageAssetKind.MaterialInstanceConstant => From(await workspaces.MaterialEdits.LoadEntryAsync(
                        edit.ManifestPath, cancellationToken).ConfigureAwait(false)),
                    UnifiedPackageAssetKind.ParticleSystem => From(await workspaces.ParticleEdits.LoadEntryAsync(
                        edit.ManifestPath, cancellationToken).ConfigureAwait(false)),
                    UnifiedPackageAssetKind.PhysicsAsset => From(await workspaces.PhysicsEdits.LoadEntryAsync(
                        edit.ManifestPath, cancellationToken).ConfigureAwait(false)),
                    UnifiedPackageAssetKind.InterfaceMovie when workspaces.InterfaceMovies is not null => From(
                        await InterfaceMovieEditWorkspaceStore.LoadEntryAsync(edit.ManifestPath, cancellationToken).ConfigureAwait(false)),
                    _ => null,
                };
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
            ArkiveModIndexedEdit indexed => indexed.BuildSha256,
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
        Interlocked.Increment(ref _filesHashed);
        Interlocked.Add(ref _bytesHashed, info.Length);
        return hash;
    }

    private static long _filesHashed;
    private static long _bytesHashed;

    /// <summary>Files <see cref="HashFileCachedAsync"/> had to read in full in this process (a saved hash did not do).</summary>
    public static long FilesHashed => Interlocked.Read(ref _filesHashed);

    public static long BytesHashed => Interlocked.Read(ref _bytesHashed);

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
