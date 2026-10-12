using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Packages;

namespace Arkive.Core.StaticMeshes;

public sealed class StaticMeshEditWorkspaceStore
{
    public const string ManifestFileName = "arkive-staticmesh-edit.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly StaticMeshEditPreflightService _preflight;

    public StaticMeshEditWorkspaceStore(
        string rootDirectory,
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        _preflight = new StaticMeshEditPreflightService(summaryReader, protectionProvider);
    }

    public string RootDirectory { get; }

    public async Task<StaticMeshEditEntry> RegisterAsync(
        string sourceEditableManifestPath,
        string editedGlbPath,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.GetFullPath(sourceEditableManifestPath);
        var source = await StaticMeshEditableExportService.LoadManifestAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);
        var edited = Path.GetFullPath(editedGlbPath);
        if (!File.Exists(edited)) throw new FileNotFoundException("The edited StaticMesh GLB was not found.", edited);
        var preflight = await _preflight.InspectAsync(sourcePath, edited, cancellationToken).ConfigureAwait(false);
        var editId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(RootDirectory, $"{Sanitize(source.PackageAlias)}-{Sanitize(source.ObjectName)}-{editId[..8]}");
        Directory.CreateDirectory(directory);
        var copiedGlb = Path.Combine(directory, Sanitize(source.ObjectName) + ".edited.glb");
        File.Copy(edited, copiedGlb, overwrite: true);
        var manifest = new StaticMeshEditManifest(
            1,
            editId,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(displayName) ? source.ObjectName + " StaticMesh edit" : displayName.Trim(),
            sourcePath,
            copiedGlb,
            ComputeFileSha256(copiedGlb, cancellationToken),
            preflight);
        var manifestPath = Path.Combine(directory, ManifestFileName);
        await SaveAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return new StaticMeshEditEntry(directory, manifestPath, manifest);
    }

    public async Task<IReadOnlyList<StaticMeshEditEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var entries = new List<StaticMeshEditEntry>();
        foreach (var path in Directory.EnumerateFiles(RootDirectory, ManifestFileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var manifest = await JsonSerializer.DeserializeAsync<StaticMeshEditManifest>(
                    stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                if (manifest is null || manifest.SchemaVersion != 1) continue;
                entries.Add(new StaticMeshEditEntry(Path.GetDirectoryName(path)!, path, manifest));
            }
            catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
            {
                // Ignore damaged entries without hiding healthy edits.
            }
        }
        return entries.OrderByDescending(entry => entry.Manifest.CreatedAt).ToArray();
    }

    public async Task<StaticMeshEditEntry> RevalidateAsync(
        StaticMeshEditEntry entry, CancellationToken cancellationToken = default)
    {
        var relative = Path.GetRelativePath(RootDirectory, Path.GetFullPath(entry.ManifestPath));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new IOException("The StaticMesh edit is outside its workspace.");
        var preflight = await _preflight.InspectAsync(entry.Manifest.SourceEditableManifestPath,
            entry.Manifest.EditedGlbPath, cancellationToken).ConfigureAwait(false);
        var updated = entry.Manifest with
        {
            EditedGlbSha256 = ComputeFileSha256(entry.Manifest.EditedGlbPath, cancellationToken),
            Preflight = preflight,
            CompilationManifestPath = null,
            StagedUpkManifestPath = null,
        };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public async Task<StaticMeshEditEntry> RecordCompilationAsync(
        StaticMeshEditEntry entry,
        string compilationManifestPath,
        CancellationToken cancellationToken = default)
    {
        var updated = entry.Manifest with { CompilationManifestPath = Path.GetFullPath(compilationManifestPath) };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public async Task<StaticMeshEditEntry> RecordStagedUpkAsync(
        StaticMeshEditEntry entry,
        string stagedManifestPath,
        CancellationToken cancellationToken = default)
    {
        var updated = entry.Manifest with { StagedUpkManifestPath = Path.GetFullPath(stagedManifestPath) };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public async Task<StaticMeshEditEntry> SetIncludedAsync(
        StaticMeshEditEntry entry,
        bool included,
        CancellationToken cancellationToken = default)
    {
        var updated = entry.Manifest with { IncludedInPackageBuild = included };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public Task DeleteAsync(StaticMeshEditEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var full = Path.GetFullPath(entry.DirectoryPath);
        var relative = Path.GetRelativePath(RootDirectory, full);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("The StaticMesh edit is outside its workspace.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        return Task.CompletedTask;
    }

    private static string ComputeFileSha256(string path, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = File.OpenRead(path);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task SaveAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "staticmesh" : result;
    }
}
