using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkive.Core.Mods;

public sealed record GuidedTexturePackageAssetRevision(
    string ProjectId,
    string ObjectName,
    int ExportIndex,
    int NativeExportIndex,
    string PixelFormat,
    string EditedImageSha256,
    string ReplacementUpkBuildManifestPath,
    long ReplacementSerialOffset,
    long ReplacementSerialSize);

public sealed record GuidedMeshPackageAssetRevision(
    string EditId,
    string DisplayName,
    string ObjectName,
    int NativeExportIndex,
    string EditedGlbSha256,
    string RebasedEditableManifestPath,
    string RebasedEditManifestPath,
    string NativeEncodingManifestPath,
    string StagedUpkManifestPath,
    long SerialOffset,
    long SerialSize)
{
    public bool VariableSizeReplacement { get; init; }

    public string? TopologyCompilationManifestPath { get; init; }

    public string ReplacementMode => VariableSizeReplacement
        ? "Variable-size append and retarget"
        : "Same-size in-place replacement";
}

public sealed record GuidedStaticMeshPackageAssetRevision(
    string EditId,
    string DisplayName,
    string ObjectName,
    int NativeExportIndex,
    string EditedGlbSha256,
    string RebasedEditableManifestPath,
    string RebasedEditManifestPath,
    string CompilationManifestPath,
    string StagedUpkManifestPath,
    long SerialOffset,
    long SerialSize,
    bool VariableSizeReplacement);

/// <param name="EditedMovieSha256">The edited movie file the package was built from.</param>
public sealed record GuidedInterfaceMoviePackageAssetRevision(
    string EditId,
    string DisplayName,
    string ObjectName,
    int NativeExportIndex,
    string EditedMovieSha256,
    string CandidateExportSha256,
    string StagedUpkManifestPath,
    long SerialOffset,
    long SerialSize);

public sealed record GuidedMaterialPackageAssetRevision(
    string EditId,
    string DisplayName,
    string ObjectName,
    int NativeExportIndex,
    string EditDocumentPath,
    string CandidateManifestPath,
    string CandidateExportSha256,
    string StagedUpkManifestPath,
    long SerialOffset,
    long SerialSize);

/// <summary>One particle effect written into a package build: the exports its colour patches changed.</summary>
public sealed record GuidedParticlePackageAssetRevision(
    string EditId,
    string DisplayName,
    string SystemObjectName,
    int SystemExportIndex,
    string EditDocumentPath,
    string ContentSha256,
    string StagedUpkManifestPath,
    IReadOnlyList<Meshes.SerializedExportsStagedUpkExport> Exports);

public sealed record GuidedTexturePackageRevision(
    string RevisionId,
    DateTimeOffset CreatedAt,
    string SourcePackageSha256,
    IReadOnlyList<GuidedTexturePackageAssetRevision> Assets,
    string OutputUpkPath,
    string FinalReplacementUpkBuildManifestPath,
    string ModBundleManifestPath,
    string DeploymentReadinessManifestPath,
    string PayloadPath,
    long PayloadBytes,
    string PayloadSha256,
    double DurationSeconds)
{
    public IReadOnlyList<GuidedMeshPackageAssetRevision> MeshAssets { get; init; } = [];

    public IReadOnlyList<GuidedMaterialPackageAssetRevision> MaterialAssets { get; init; } = [];

    public IReadOnlyList<GuidedStaticMeshPackageAssetRevision> StaticMeshAssets { get; init; } = [];

    public IReadOnlyList<GuidedParticlePackageAssetRevision> ParticleAssets { get; init; } = [];
    public IReadOnlyList<GuidedInterfaceMoviePackageAssetRevision> InterfaceMovieAssets { get; init; } = [];

    public string? CompositionManifestPath { get; init; }

    public bool PreviewOnly { get; init; }

    public bool ContainsVariableSizeMeshReplacements { get; init; }

    public bool VariableSizeDeploymentApproved { get; init; }

    public string? VariableSizeDeploymentGateManifestPath { get; init; }

    public int TotalAssetCount => Assets.Count + MeshAssets.Count + StaticMeshAssets.Count + MaterialAssets.Count +
                                  ParticleAssets.Count + InterfaceMovieAssets.Count;
}

public sealed record GuidedTexturePackageManifest(
    int SchemaVersion,
    string PackageProjectId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string PackageAlias,
    string PhysicalPackageName,
    string SourcePackagePath,
    string? LatestRevisionId,
    IReadOnlyList<GuidedTexturePackageRevision> Revisions)
{
    public string? DisplayName { get; init; }

    public string? Notes { get; init; }

    public bool IsFavorite { get; init; }

    public string? ClientBaselineSha256 { get; init; }

    public DateTimeOffset? ClientBaselineAt { get; init; }
}

public sealed record GuidedTexturePackageEntry(
    string ManifestPath,
    string DirectoryPath,
    GuidedTexturePackageManifest Manifest);

public sealed class GuidedTexturePackageWorkspace
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "arkive-package-mod.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public GuidedTexturePackageWorkspace(string rootDirectory) =>
        RootDirectory = Path.GetFullPath(rootDirectory);

    public string RootDirectory { get; }

    public Task<GuidedTexturePackageEntry> LoadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken = default) =>
        LoadEntryAsync(manifestPath, cancellationToken);

    public async Task<IReadOnlyList<GuidedTexturePackageEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var entries = new List<GuidedTexturePackageEntry>();
        foreach (var path in Directory.EnumerateFiles(
                     RootDirectory, ManifestFileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                entries.Add(await LoadEntryAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
            {
                // A damaged package project must not hide healthy package projects.
            }
        }

        return entries.OrderByDescending(entry => entry.Manifest.UpdatedAt).ToArray();
    }

    public async Task<GuidedTexturePackageEntry> GetOrCreateAsync(
        string packageAlias,
        string physicalPackageName,
        string sourcePackagePath,
        CancellationToken cancellationToken = default)
    {
        var fullSource = Path.GetFullPath(sourcePackagePath);
        var existing = (await LoadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(entry =>
            Path.GetFullPath(entry.Manifest.SourcePackagePath)
                .Equals(fullSource, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        Directory.CreateDirectory(RootDirectory);
        var id = Guid.NewGuid().ToString("N");
        var key = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(fullSource.ToUpperInvariant())))[..12];
        var directory = Path.Combine(RootDirectory, $"{Sanitize(packageAlias)}-{key}");
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        var manifest = new GuidedTexturePackageManifest(
            CurrentSchemaVersion, id, now, now, packageAlias, physicalPackageName,
            fullSource, null, []);
        var path = Path.Combine(directory, ManifestFileName);
        await SaveAtomicAsync(path, manifest, cancellationToken).ConfigureAwait(false);
        return new GuidedTexturePackageEntry(path, directory, manifest);
    }

    public async Task<GuidedTexturePackageEntry> CommitRevisionAsync(
        string manifestPath,
        GuidedTexturePackageRevision revision,
        CancellationToken cancellationToken = default)
    {
        var entry = await LoadEntryAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var revisions = entry.Manifest.Revisions
            .Where(item => !item.RevisionId.Equals(revision.RevisionId, StringComparison.OrdinalIgnoreCase))
            .Append(revision)
            .OrderBy(item => item.CreatedAt)
            .ToArray();
        var updated = entry.Manifest with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            LatestRevisionId = revision.RevisionId,
            Revisions = revisions,
            // A committed revision has already been built and validated against
            // this exact clean package identity. Advance the project baseline
            // with it so a successful post-update rebuild is not immediately
            // presented as stale and prevented from installation.
            ClientBaselineSha256 = revision.SourcePackageSha256,
            ClientBaselineAt = DateTimeOffset.UtcNow,
        };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        // The build is verified and recorded; drop the package copies nothing reads again.
        try
        {
            await PackageRevisionTrimmer.TrimAsync(RevisionDirectory(updated, revision), revision, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return entry with { Manifest = updated };
    }

    /// <summary>Trims every revision of every package project (see <see cref="PackageRevisionTrimmer"/>); returns the bytes freed.</summary>
    public async Task<long> TrimRevisionsAsync(CancellationToken cancellationToken = default)
    {
        var freed = 0L;
        foreach (var entry in await LoadAsync(cancellationToken).ConfigureAwait(false))
        foreach (var revision in entry.Manifest.Revisions)
        {
            try
            {
                freed += await PackageRevisionTrimmer.TrimAsync(RevisionDirectory(entry.Manifest, revision), revision, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return freed;
    }

    private string RevisionDirectory(GuidedTexturePackageManifest manifest, GuidedTexturePackageRevision revision) =>
        RequireWithinRoot(Path.GetFullPath(Path.Combine(RootDirectory, "_r", manifest.PackageProjectId[..8], revision.RevisionId)),
            "package revision directory");

    public async Task<GuidedTexturePackageEntry> UpdateMetadataAsync(
        string manifestPath,
        string? displayName,
        string? notes,
        bool? favorite = null,
        CancellationToken cancellationToken = default)
    {
        var entry = await LoadEntryAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var updated = entry.Manifest with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            IsFavorite = favorite ?? entry.Manifest.IsFavorite,
        };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public async Task<GuidedTexturePackageEntry> RecordClientBaselineAsync(
        string manifestPath,
        string packageSha256,
        CancellationToken cancellationToken = default)
    {
        var normalized = packageSha256.Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("The client baseline SHA-256 is invalid.", nameof(packageSha256));
        var entry = await LoadEntryAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var updated = entry.Manifest with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            ClientBaselineSha256 = normalized,
            ClientBaselineAt = DateTimeOffset.UtcNow,
        };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public async Task<GuidedTexturePackageEntry> PruneRevisionsAsync(
        string manifestPath,
        IReadOnlySet<string> retainedRevisionIds,
        CancellationToken cancellationToken = default)
    {
        var entry = await LoadEntryAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var latestId = entry.Manifest.LatestRevisionId;
        var retained = entry.Manifest.Revisions
            .Where(revision => retainedRevisionIds.Contains(revision.RevisionId) ||
                               revision.RevisionId.Equals(latestId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var revision in entry.Manifest.Revisions.Except(retained))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetFullPath(Path.Combine(
                RootDirectory, "_r", entry.Manifest.PackageProjectId[..8], revision.RevisionId));
            _ = RequireWithinRoot(directory, "package revision directory");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        var updated = entry.Manifest with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Revisions = retained,
        };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public string CreateRevisionDirectory(GuidedTexturePackageEntry entry)
    {
        var revisionId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..25];
        var path = Path.Combine(RootDirectory, "_r", entry.Manifest.PackageProjectId[..8], revisionId);
        Directory.CreateDirectory(path);
        return path;
    }

    public Task DeleteAsync(
        GuidedTexturePackageEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        var manifestPath = RequireWithinRoot(entry.ManifestPath, "package project manifest");
        var projectDirectory = RequireWithinRoot(entry.DirectoryPath, "package project directory");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("The guided package project manifest is missing.", manifestPath);
        }

        var revisionDirectory = RequireWithinRoot(
            Path.Combine(RootDirectory, "_r", entry.Manifest.PackageProjectId[..8]),
            "package revision directory");
        if (Directory.Exists(revisionDirectory))
        {
            Directory.Delete(revisionDirectory, recursive: true);
        }
        if (Directory.Exists(projectDirectory))
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
        return Task.CompletedTask;
    }

    public void DeleteFailedRevisionDirectory(string revisionDirectory)
    {
        var fullPath = Path.GetFullPath(revisionDirectory);
        var revisionRoot = Path.GetFullPath(Path.Combine(RootDirectory, "_r"));
        var relative = Path.GetRelativePath(revisionRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries).Length < 2)
        {
            throw new IOException("The failed package revision is outside Arkive's revision workspace.");
        }
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }

    private static async Task<GuidedTexturePackageEntry> LoadEntryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<GuidedTexturePackageManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new JsonException("The package-mod manifest was empty.");
        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Package-mod schema {manifest.SchemaVersion} is unsupported; Arkive requires {CurrentSchemaVersion}.");
        }
        return new GuidedTexturePackageEntry(fullPath, Path.GetDirectoryName(fullPath)!, manifest);
    }

    private static async Task SaveAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(
                         temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private string RequireWithinRoot(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(RootDirectory, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException($"The {description} is outside Arkive's package workspace.");
        }
        return fullPath;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "package" : safe;
    }
}
