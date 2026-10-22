using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

public enum GuidedTextureModState
{
    Editing,
    Ready,
}

public sealed record GuidedTextureModRevision(
    string RevisionId,
    DateTimeOffset CreatedAt,
    string EditedImageSha256,
    string TextureImportCandidateManifestPath,
    string TextureReplacementPayloadManifestPath,
    string TextureExportAssemblyManifestPath,
    string PackageRewritePlanManifestPath,
    string LogicalPackageImageManifestPath,
    string PhysicalPackagePlanManifestPath,
    string NoOpUpkReconstructionManifestPath,
    string ReplacementUpkBuildManifestPath,
    string OutputUpkPath,
    string ModBundleManifestPath,
    string DeploymentReadinessManifestPath,
    string PayloadPath,
    long PayloadBytes,
    string PayloadSha256,
    double DurationSeconds);

public sealed record GuidedTextureModProjectManifest(
    int SchemaVersion,
    string ProjectId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string DisplayName,
    string PackageAlias,
    string PhysicalPackageName,
    string SourcePackagePath,
    string ClassName,
    string ObjectName,
    int ExportIndex,
    string PixelFormat,
    int Width,
    int Height,
    string NativeTextureManifestPath,
    string EditableImagePath,
    GuidedTextureModState State,
    string? LatestRevisionId,
    IReadOnlyList<GuidedTextureModRevision> Revisions,
    bool? IncludedInPackageBuild = null)
{
    public TextureImportAlphaPolicy AlphaPolicy { get; init; } = TextureImportAlphaPolicy.PreserveOriginal;

    public bool IsFavorite { get; init; }

    public DateTimeOffset? LastOpenedAt { get; init; }
}

public sealed record GuidedTextureModProjectEntry(
    string ManifestPath,
    string DirectoryPath,
    GuidedTextureModProjectManifest Manifest);

public sealed class GuidedTextureModWorkspace
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "arkive-texture-mod.json";
    public const string SuppressedImportsFileName = "arkive-suppressed-imports.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public GuidedTextureModWorkspace(string rootDirectory) =>
        RootDirectory = Path.GetFullPath(rootDirectory);

    public string RootDirectory { get; }

    public async Task<bool> IsLegacyImportSuppressedAsync(
        string bundleManifestPath,
        CancellationToken cancellationToken = default)
    {
        var suppressed = await LoadSuppressedImportsAsync(cancellationToken).ConfigureAwait(false);
        return suppressed.Contains(Path.GetFullPath(bundleManifestPath), StringComparer.OrdinalIgnoreCase);
    }

    public async Task SuppressLegacyImportsAsync(
        IEnumerable<string> bundleManifestPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundleManifestPaths);
        var additions = bundleManifestPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath);
        var suppressed = (await LoadSuppressedImportsAsync(cancellationToken).ConfigureAwait(false))
            .Concat(additions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Directory.CreateDirectory(RootDirectory);
        await SaveAtomicAsync(
            Path.Combine(RootDirectory, SuppressedImportsFileName),
            suppressed,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        GuidedTextureModProjectEntry project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var manifestPath = RequireWithinRoot(project.ManifestPath, "guided project manifest");
        var projectDirectory = RequireWithinRoot(project.DirectoryPath, "guided project directory");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("The guided texture-mod project manifest is missing.", manifestPath);
        }

        // Imported advanced-workflow bundles still exist in the Exports workspace.
        // Retain a tombstone so deleting their guided view remains durable.
        await SuppressLegacyImportsAsync(
            project.Manifest.Revisions.Select(revision => revision.ModBundleManifestPath),
            cancellationToken).ConfigureAwait(false);

        var revisionRoot = Path.Combine(RootDirectory, "_r");
        foreach (var revision in project.Manifest.Revisions)
        {
            var relative = Path.GetRelativePath(revisionRoot, revision.TextureImportCandidateManifestPath);
            var parts = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                parts.Length < 2)
            {
                continue;
            }

            var ownedRevisionDirectory = Path.GetFullPath(
                Path.Combine(revisionRoot, parts[0], parts[1]));
            _ = RequireWithinRoot(ownedRevisionDirectory, "guided revision directory");
            if (Directory.Exists(ownedRevisionDirectory))
            {
                Directory.Delete(ownedRevisionDirectory, recursive: true);
            }
        }

        if (Directory.Exists(projectDirectory))
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    public async Task<IReadOnlyList<GuidedTextureModProjectEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var entries = new List<GuidedTextureModProjectEntry>();
        foreach (var path in ProjectManifestPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                entries.Add(await LoadProjectAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
            {
                // A partially copied or obsolete project must not hide healthy projects.
            }
        }

        return entries
            .OrderByDescending(entry => entry.Manifest.UpdatedAt)
            .ThenBy(entry => entry.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Every project manifest under the root. Folders whose name starts with an underscore are not projects: the
    /// package workspace (_packages) keeps its build revisions there, thousands of folders that a full search walked
    /// on every load (about 2 s each time with large mods).
    /// </summary>
    private IEnumerable<string> ProjectManifestPaths()
    {
        foreach (var path in Directory.EnumerateFiles(RootDirectory, ManifestFileName, SearchOption.TopDirectoryOnly))
            yield return path;
        foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
        {
            if (Path.GetFileName(directory).StartsWith('_')) continue;
            foreach (var path in Directory.EnumerateFiles(directory, ManifestFileName, SearchOption.AllDirectories))
                yield return path;
        }
    }

    public async Task<GuidedTextureModProjectEntry?> FindAsync(
        string sourcePackagePath,
        string objectName,
        int exportIndex,
        CancellationToken cancellationToken = default)
    {
        var fullSource = Path.GetFullPath(sourcePackagePath);
        return (await LoadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(entry =>
            Path.GetFullPath(entry.Manifest.SourcePackagePath)
                .Equals(fullSource, StringComparison.OrdinalIgnoreCase) &&
            entry.Manifest.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase) &&
            entry.Manifest.ExportIndex == exportIndex);
    }

    public async Task<GuidedTextureModProjectEntry> CreateAsync(
        string displayName,
        string packageAlias,
        string physicalPackageName,
        string sourcePackagePath,
        string className,
        string objectName,
        int exportIndex,
        string pixelFormat,
        int width,
        int height,
        string nativeTextureManifestPath,
        string sourceEditableImagePath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);
        var projectId = Guid.NewGuid().ToString("N");
        var safeAlias = Sanitize(packageAlias);
        var safeObject = Sanitize(objectName);
        var directory = Path.Combine(RootDirectory, safeAlias, $"{safeObject}-{projectId[..8]}");
        Directory.CreateDirectory(directory);
        var editableDirectory = Path.Combine(directory, "editable");
        Directory.CreateDirectory(editableDirectory);
        var extension = Path.GetExtension(sourceEditableImagePath);
        var editablePath = Path.Combine(editableDirectory, safeObject + "-edit" + extension);
        File.Copy(sourceEditableImagePath, editablePath, overwrite: false);

        var now = DateTimeOffset.UtcNow;
        var manifest = new GuidedTextureModProjectManifest(
            CurrentSchemaVersion,
            projectId,
            now,
            now,
            displayName,
            packageAlias,
            physicalPackageName,
            Path.GetFullPath(sourcePackagePath),
            className,
            objectName,
            exportIndex,
            pixelFormat,
            width,
            height,
            Path.GetFullPath(nativeTextureManifestPath),
            editablePath,
            GuidedTextureModState.Editing,
            null,
            []);
        var manifestPath = Path.Combine(directory, ManifestFileName);
        await SaveAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return new GuidedTextureModProjectEntry(manifestPath, directory, manifest);
    }

    public async Task<GuidedTextureModProjectEntry> CreateAsync(
        string displayName,
        string packageAlias,
        string physicalPackageName,
        string sourcePackagePath,
        string className,
        string objectName,
        int exportIndex,
        string pixelFormat,
        int width,
        int height,
        string nativeTextureManifestPath,
        string sourceEditableImagePath,
        TextureImportAlphaPolicy alphaPolicy,
        CancellationToken cancellationToken = default)
    {
        var entry = await CreateAsync(
            displayName, packageAlias, physicalPackageName, sourcePackagePath,
            className, objectName, exportIndex, pixelFormat, width, height,
            nativeTextureManifestPath, sourceEditableImagePath, cancellationToken)
            .ConfigureAwait(false);
        var updated = entry.Manifest with { AlphaPolicy = alphaPolicy };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public async Task<GuidedTextureModProjectEntry> CommitRevisionAsync(
        string manifestPath,
        GuidedTextureModRevision revision,
        CancellationToken cancellationToken = default)
    {
        var entry = await LoadProjectAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var revisions = entry.Manifest.Revisions
            .Where(item => !item.RevisionId.Equals(revision.RevisionId, StringComparison.OrdinalIgnoreCase))
            .Append(revision)
            .OrderBy(item => item.CreatedAt)
            .ToArray();
        var updated = entry.Manifest with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            State = GuidedTextureModState.Ready,
            LatestRevisionId = revision.RevisionId,
            Revisions = revisions,
        };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public async Task<GuidedTextureModProjectEntry> SetIncludedAsync(
        string manifestPath,
        bool included,
        CancellationToken cancellationToken = default)
    {
        var entry = await LoadProjectAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var updated = entry.Manifest with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            IncludedInPackageBuild = included,
        };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public async Task<GuidedTextureModProjectEntry> RebaseSourceAsync(
        string manifestPath,
        NativeTextureExportManifest source,
        string sourceManifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var entry = await LoadProjectAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (!entry.Manifest.ObjectName.Equals(source.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            !entry.Manifest.ClassName.Equals(source.ClassName, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "The refreshed native texture does not match this project's class/object identity.");
        if (entry.Manifest.Width != source.Width || entry.Manifest.Height != source.Height ||
            !entry.Manifest.PixelFormat.Equals(source.PixelFormat, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"The updated texture layout changed from {entry.Manifest.PixelFormat} " +
                $"{entry.Manifest.Width}x{entry.Manifest.Height} to {source.PixelFormat} " +
                $"{source.Width}x{source.Height}; automatic rebase is unsafe.");
        var updated = entry.Manifest with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            PackageAlias = source.PackageAlias,
            PhysicalPackageName = source.PhysicalPackageName,
            SourcePackagePath = Path.GetFullPath(source.SourcePackagePath),
            ExportIndex = source.SourceExportIndex,
            NativeTextureManifestPath = Path.GetFullPath(sourceManifestPath),
        };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public async Task<GuidedTextureModProjectEntry> SetFavoriteAsync(
        string manifestPath,
        bool favorite,
        CancellationToken cancellationToken = default)
    {
        var entry = await LoadProjectAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var updated = entry.Manifest with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            IsFavorite = favorite,
        };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    /// <summary>Whether a rebuilt picture keeps the game's alpha or takes the working image's (a cut-out mask that changed).</summary>
    public async Task<GuidedTextureModProjectEntry> SetAlphaPolicyAsync(
        string manifestPath,
        TextureImportAlphaPolicy alphaPolicy,
        CancellationToken cancellationToken = default)
    {
        var entry = await LoadProjectAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var updated = entry.Manifest with { UpdatedAt = DateTimeOffset.UtcNow, AlphaPolicy = alphaPolicy };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public async Task<GuidedTextureModProjectEntry> TouchAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var entry = await LoadProjectAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var updated = entry.Manifest with { UpdatedAt = now, LastOpenedAt = now };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public async Task<GuidedTextureModProjectEntry> NormalizeSelectionExportIndexAsync(
        string manifestPath,
        int exportIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(exportIndex);
        var entry = await LoadProjectAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (entry.Manifest.ExportIndex == exportIndex) return entry;
        var updated = entry.Manifest with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            ExportIndex = exportIndex,
        };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public async Task<GuidedTextureModProjectEntry> ResetEditableAsync(
        string manifestPath,
        string originalEditableImagePath,
        CancellationToken cancellationToken = default)
    {
        var entry = await LoadProjectAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var sourcePath = Path.GetFullPath(originalEditableImagePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The original exported editable image is missing.", sourcePath);
        }
        var editablePath = RequireWithinRoot(entry.Manifest.EditableImagePath, "guided editable image");
        var temporaryPath = editablePath + $".{Guid.NewGuid():N}.reset";
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            File.Move(temporaryPath, editablePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        var updated = entry.Manifest with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            State = GuidedTextureModState.Editing,
        };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public static async Task<GuidedTextureModProjectEntry> LoadProjectAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<GuidedTextureModProjectManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new JsonException("The guided texture-mod manifest was empty.");
        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Guided texture-mod schema {manifest.SchemaVersion} is unsupported; " +
                $"Arkive requires {CurrentSchemaVersion}.");
        }
        return new GuidedTextureModProjectEntry(fullPath, Path.GetDirectoryName(fullPath)!, manifest);
    }

    public string CreateRevisionDirectory(GuidedTextureModProjectEntry project)
    {
        // The pinned Unity Crunch encoder is not long-path aware. Keep heavy build
        // outputs in a compact central tree; the stable project still references
        // every revision through its manifest.
        var revisionId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..25];
        var directory = Path.Combine(
            RootDirectory,
            "_r",
            project.Manifest.ProjectId[..8],
            revisionId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task SaveAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }

    private async Task<IReadOnlyList<string>> LoadSuppressedImportsAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(RootDirectory, SuppressedImportsFileName);
        if (!File.Exists(path)) return [];
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<string[]>(
                       stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (JsonException exception)
        {
            throw new IOException("The suppressed guided-import list could not be read.", exception);
        }
    }

    private string RequireWithinRoot(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(RootDirectory, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException($"The {description} is outside Arkive's guided texture-mod workspace.");
        }
        return fullPath;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "texture" : safe;
    }
}
