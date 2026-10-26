using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkive.Core.Meshes;

public sealed class SkeletalMeshEditWorkspaceStore
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "arkive-edited-skeletalmesh.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SkeletalMeshEditPreflightService _preflight;

    // Edit records carry their full mesh check (hundreds of KB each) and are listed by every mod refresh; each one
    // is parsed again only when its file changes.
    private readonly Mods.ParsedRecordCache<SkeletalMeshEditRegistrationManifest> _parsed = new();

    public SkeletalMeshEditWorkspaceStore(
        string rootDirectory,
        SkeletalMeshEditPreflightService? preflight = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        _preflight = preflight ?? new SkeletalMeshEditPreflightService();
    }

    public string RootDirectory { get; }

    public async Task<SkeletalMeshEditRegistrationResult> RegisterAsync(
        SkeletalMeshEditableWorkspaceEntry source,
        string candidateGlbPath,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateGlbPath);
        var candidate = Path.GetFullPath(candidateGlbPath);
        var preflight = await _preflight.InspectAsync(source, candidate, cancellationToken)
            .ConfigureAwait(false);
        var editId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(
            RootDirectory, Safe(source.Manifest.ExportId),
            $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{editId}");
        Directory.CreateDirectory(directory);
        var outputName = Safe(Path.GetFileNameWithoutExtension(candidate)) + ".edited.glb";
        var outputPath = Path.Combine(directory, outputName);
        try
        {
            await CopyAtomicAsync(candidate, outputPath, cancellationToken).ConfigureAwait(false);
            var durablePreflight = preflight with { CandidateGlbPath = outputPath };
            var manifest = new SkeletalMeshEditRegistrationManifest(
                CurrentSchemaVersion,
                editId,
                DateTimeOffset.UtcNow,
                string.IsNullOrWhiteSpace(displayName)
                    ? Path.GetFileNameWithoutExtension(candidate)
                    : displayName.Trim(),
                source.Manifest.ExportId,
                source.ManifestPath,
                source.Manifest.GlbSha256,
                source.Manifest.PackageAlias,
                source.Manifest.ObjectName,
                candidate,
                Path.GetFileName(outputPath),
                preflight.CandidateGlbSha256,
                preflight.CandidateGlbBytes,
                durablePreflight);
            var manifestPath = Path.Combine(directory, ManifestFileName);
            await WriteJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            var entry = new SkeletalMeshEditWorkspaceEntry(
                directory, manifestPath, outputPath, manifest);
            return new SkeletalMeshEditRegistrationResult(entry, durablePreflight);
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public async Task<SkeletalMeshEditWorkspaceEntry> RevalidateAsync(
        SkeletalMeshEditWorkspaceEntry entry,
        SkeletalMeshEditableWorkspaceEntry source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(source);
        var directory = EnsureWithinRoot(entry.DirectoryPath);
        var editedGlb = EnsureWithinDirectory(directory, entry.EditedGlbPath);
        var preflight = await _preflight.InspectAsync(source, editedGlb, cancellationToken)
            .ConfigureAwait(false);
        var manifest = entry.Manifest with
        {
            EditedGlbSha256 = preflight.CandidateGlbSha256,
            EditedGlbBytes = preflight.CandidateGlbBytes,
            Preflight = preflight,
        };
        var manifestPath = EnsureWithinDirectory(directory, entry.ManifestPath);
        await WriteJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return new SkeletalMeshEditWorkspaceEntry(
            directory, manifestPath, editedGlb, manifest);
    }

    public async Task<SkeletalMeshEditWorkspaceEntry> SetIncludedAsync(
        SkeletalMeshEditWorkspaceEntry entry,
        bool included,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var directory = EnsureWithinRoot(entry.DirectoryPath);
        var manifestPath = EnsureWithinDirectory(directory, entry.ManifestPath);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The edited mesh manifest is missing.", manifestPath);
        var manifest = entry.Manifest with { IncludedInPackageBuild = included };
        await WriteJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = manifest };
    }

    public async Task<SkeletalMeshEditWorkspaceSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory))
            return new SkeletalMeshEditWorkspaceSnapshot([], []);
        var paths = Directory.EnumerateFiles(RootDirectory, ManifestFileName, SearchOption.AllDirectories).ToArray();
        var entries = new SkeletalMeshEditWorkspaceEntry?[paths.Length];
        var issues = new SkeletalMeshEditWorkspaceIssue?[paths.Length];
        // Records not parsed yet (at startup, all of them) are read in parallel: large drafts hold hundreds.
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Length),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var path = paths[index];
                try
                {
                    entries[index] = await LoadEntryAsync(path, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException or
                        InvalidDataException or ArgumentException or NotSupportedException)
                {
                    issues[index] = new SkeletalMeshEditWorkspaceIssue(Path.GetFullPath(path), exception.Message);
                }
            }).ConfigureAwait(false);
        return new SkeletalMeshEditWorkspaceSnapshot(
            entries.OfType<SkeletalMeshEditWorkspaceEntry>().OrderByDescending(entry => entry.Manifest.CreatedAt).ToArray(),
            issues.OfType<SkeletalMeshEditWorkspaceIssue>().ToArray());
    }

    /// <summary>One edit's record read from disk (parsed again only if its file changed), checked as LoadAsync checks it.</summary>
    public async Task<SkeletalMeshEditWorkspaceEntry> LoadEntryAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        manifestPath = EnsureWithinRoot(manifestPath);
        var manifest = await _parsed.GetAsync(manifestPath, async (file, ct) =>
        {
            await using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<SkeletalMeshEditRegistrationManifest>(
                       stream, JsonOptions, ct).ConfigureAwait(false) ??
                   throw new InvalidDataException("The edited-mesh manifest was empty.");
        }, cancellationToken).ConfigureAwait(false);
        if (manifest.SchemaVersion != CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(manifest.EditId) ||
            string.IsNullOrWhiteSpace(manifest.SourceExportId) ||
            string.IsNullOrWhiteSpace(manifest.EditedGlbRelativePath) ||
            Path.IsPathRooted(manifest.EditedGlbRelativePath))
            throw new InvalidDataException("The edited-mesh manifest identity or schema is invalid.");
        var directory = Path.GetDirectoryName(manifestPath) ??
                        throw new InvalidDataException("The edited-mesh directory is unavailable.");
        var editedGlb = EnsureWithinDirectory(
            directory, Path.Combine(directory, manifest.EditedGlbRelativePath));
        if (!File.Exists(editedGlb))
            throw new FileNotFoundException("The registered edited GLB is missing.", editedGlb);
        return new SkeletalMeshEditWorkspaceEntry(directory, manifestPath, editedGlb, manifest);
    }

    public Task DeleteAsync(
        SkeletalMeshEditWorkspaceEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        var manifestPath = EnsureWithinRoot(entry.ManifestPath);
        if (!Path.GetFileName(manifestPath).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected file is not an Arkive edited-mesh manifest.");
        var directory = EnsureWithinRoot(Path.GetDirectoryName(manifestPath) ?? string.Empty);
        if (directory.Equals(RootDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Arkive refuses to delete the edited-mesh workspace root.");
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
            throw new InvalidDataException($"Path escapes the edited-mesh workspace: {fullPath}");
        return fullPath;
    }

    private static async Task CopyAtomicAsync(string source, string destination, CancellationToken token)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var input = new FileStream(
                             source, FileMode.Open, FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete, 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .Trim();
        if (string.IsNullOrWhiteSpace(safe)) safe = "edited-mesh";
        return safe.Length <= 80 ? safe : safe[..80];
    }
}
