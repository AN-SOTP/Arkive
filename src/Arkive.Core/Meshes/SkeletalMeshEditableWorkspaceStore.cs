using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkive.Core.Meshes;

public sealed record SkeletalMeshEditableWorkspaceEntry(
    string DirectoryPath,
    string ManifestPath,
    string GlbPath,
    SkeletalMeshEditableExportManifest Manifest,
    bool GlbExists,
    bool GlbLengthMatches,
    bool IsCurrentSchema);

public sealed record SkeletalMeshEditableWorkspaceIssue(
    string ManifestPath,
    string Message);

public sealed record SkeletalMeshEditableWorkspaceSnapshot(
    IReadOnlyList<SkeletalMeshEditableWorkspaceEntry> Entries,
    IReadOnlyList<SkeletalMeshEditableWorkspaceIssue> Issues);

/// <summary>
/// Discovers durable editable-GLB sessions beneath the SkeletalMesh
/// reconnaissance workspace. Closing Arkive never deletes these sessions.
/// </summary>
public sealed class SkeletalMeshEditableWorkspaceStore
{
    public const string ManifestFileName = "arkive-editable-skeletalmesh.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    // Listed at every startup and mesh refresh; each record is parsed again only when its file changes.
    private readonly ConcurrentDictionary<string, (long Length, DateTime WriteTimeUtc, SkeletalMeshEditableExportManifest Manifest)>
        _parsed = new(StringComparer.OrdinalIgnoreCase);

    public SkeletalMeshEditableWorkspaceStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public async Task<SkeletalMeshEditableWorkspaceSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory))
            return new SkeletalMeshEditableWorkspaceSnapshot([], []);

        var paths = Directory.EnumerateFiles(RootDirectory, ManifestFileName, SearchOption.AllDirectories).ToArray();
        var entries = new SkeletalMeshEditableWorkspaceEntry?[paths.Length];
        var issues = new SkeletalMeshEditableWorkspaceIssue?[paths.Length];
        // Records not parsed yet (at startup, all of them; thousands with large mods) are read in parallel.
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Length),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (index, token) =>
        {
            var manifestPath = paths[index];
            try
            {
                var fullManifestPath = EnsureWithinRoot(manifestPath);
                var info = new FileInfo(fullManifestPath);
                SkeletalMeshEditableExportManifest manifest;
                if (_parsed.TryGetValue(fullManifestPath, out var parsed) && parsed.Length == info.Length &&
                    parsed.WriteTimeUtc == info.LastWriteTimeUtc)
                    manifest = parsed.Manifest;
                else
                {
                    await using var stream = new FileStream(
                        fullManifestPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    manifest = await JsonSerializer.DeserializeAsync<SkeletalMeshEditableExportManifest>(
                                   stream, JsonOptions, token).ConfigureAwait(false) ??
                               throw new InvalidDataException("The manifest was empty.");
                    _parsed[fullManifestPath] = (info.Length, info.LastWriteTimeUtc, manifest);
                }
                if (manifest.SchemaVersion <= 0)
                    throw new InvalidDataException($"Invalid editable-mesh schema {manifest.SchemaVersion}.");
                if (string.IsNullOrWhiteSpace(manifest.ExportId) ||
                    string.IsNullOrWhiteSpace(manifest.ObjectName) ||
                    string.IsNullOrWhiteSpace(manifest.GlbRelativePath) ||
                    Path.IsPathRooted(manifest.GlbRelativePath))
                    throw new InvalidDataException("The editable-mesh identity or GLB path is invalid.");

                var directory = Path.GetDirectoryName(fullManifestPath) ??
                                throw new InvalidDataException("The manifest directory is unavailable.");
                var glbPath = EnsureWithinDirectory(
                    directory, Path.Combine(directory, manifest.GlbRelativePath));
                var glb = new FileInfo(glbPath);
                entries[index] = new SkeletalMeshEditableWorkspaceEntry(
                    directory,
                    fullManifestPath,
                    glbPath,
                    manifest,
                    glb.Exists,
                    glb.Exists && glb.Length == manifest.GlbBytes,
                    manifest.SchemaVersion == SkeletalMeshEditableExportService.CurrentSchemaVersion);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    JsonException or InvalidDataException or ArgumentException or
                    NotSupportedException)
            {
                issues[index] = new SkeletalMeshEditableWorkspaceIssue(
                    Path.GetFullPath(manifestPath), exception.Message);
            }
        }).ConfigureAwait(false);

        return new SkeletalMeshEditableWorkspaceSnapshot(
            entries.OfType<SkeletalMeshEditableWorkspaceEntry>()
                .OrderByDescending(entry => entry.Manifest.CreatedAt)
                .ThenBy(entry => entry.Manifest.PackageAlias, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Manifest.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            issues.OfType<SkeletalMeshEditableWorkspaceIssue>().ToArray());
    }

    public Task DeleteAsync(
        SkeletalMeshEditableWorkspaceEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        var manifestPath = EnsureWithinRoot(entry.ManifestPath);
        if (!Path.GetFileName(manifestPath).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected file is not an Arkive editable-mesh manifest.");
        var directory = EnsureWithinRoot(Path.GetDirectoryName(manifestPath) ?? string.Empty);
        if (directory.Equals(RootDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Arkive refuses to delete the workspace root.");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private string EnsureWithinRoot(string path) => EnsureWithinDirectory(RootDirectory, path);

    private static string EnsureWithinDirectory(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) +
                   Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path escapes the editable-mesh workspace: {fullPath}");
        return fullPath;
    }
}
