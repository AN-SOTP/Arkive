using System.Text.Json;

namespace Arkive.Core.Particles;

/// <summary>
/// A particle effect working copy: which system of which package it changes, the recipe (a tint) and the value
/// patches the recipe produced for that package version.
/// </summary>
public sealed record ParticleEffectEditDocument(
    int SchemaVersion,
    string EditId,
    string DisplayName,
    string SourcePackagePath,
    string PhysicalPackageName,
    string PackageAlias,
    string SourcePackageSha256,
    string SystemObjectName,
    string SystemObjectPath,
    int SystemExportIndex,
    string SystemExportSha256,
    ParticleEffectTint Tint,
    IReadOnlyList<ParticleEffectPatch> Patches,
    bool IncludedInPackageBuild,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Identifies what the edit writes: changes whenever any patched value does.</summary>
    public string ContentSha256 => ParticleEffects.ContentSha256(Patches);
}

public sealed record ParticleEffectEditEntry(string DirectoryPath, string DocumentPath, ParticleEffectEditDocument Document);

/// <summary>Particle effect working copies, one folder each under the data folder's staging\particle-edits.</summary>
public sealed class ParticleEffectEditWorkspaceStore(string rootDirectory)
{
    public const string DocumentFileName = "arkive-particle-edit.json";
    public const int CurrentSchemaVersion = 1;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);

    public async Task<IReadOnlyList<ParticleEffectEditEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var entries = new List<ParticleEffectEditEntry>();
        foreach (var path in Directory.EnumerateFiles(RootDirectory, DocumentFileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { entries.Add(await LoadEntryAsync(path, cancellationToken).ConfigureAwait(false)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                // One damaged working copy must not hide the others.
            }
        }
        return entries.OrderByDescending(entry => entry.Document.UpdatedAt).ToArray();
    }

    public async Task<ParticleEffectEditEntry> LoadEntryAsync(string documentPath, CancellationToken cancellationToken = default)
    {
        var path = RequireWithinRoot(documentPath);
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<ParticleEffectEditDocument>(stream, JsonOptions, cancellationToken)
                           .ConfigureAwait(false) ?? throw new InvalidDataException("The particle effect working copy was empty.");
        if (document.SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Particle edit schema {document.SchemaVersion} is unsupported.");
        return new ParticleEffectEditEntry(Path.GetDirectoryName(path)!, path, document);
    }

    /// <summary>A new working copy that tints <paramref name="report"/>'s system; excluded from builds until selected.</summary>
    public async Task<ParticleEffectEditEntry> CreateAsync(
        ParticleEffectReport report, string packageAlias, string displayName, ParticleEffectTint tint,
        CancellationToken cancellationToken = default)
    {
        var editId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var document = new ParticleEffectEditDocument(
            CurrentSchemaVersion, editId, displayName, report.PackagePath, Path.GetFileName(report.PackagePath), packageAlias,
            report.PackageSha256, report.System.ObjectName, report.System.ObjectPath, report.System.ExportIndex,
            report.SystemExportSha256, tint, ParticleEffects.TintPatches(report, tint), false, now, now);
        var directory = Path.Combine(RootDirectory, editId);
        Directory.CreateDirectory(directory);
        return await SaveAsync(new ParticleEffectEditEntry(directory, Path.Combine(directory, DocumentFileName), document),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ParticleEffectEditEntry> SaveAsync(ParticleEffectEditEntry entry, CancellationToken cancellationToken = default)
    {
        var path = RequireWithinRoot(entry.DocumentPath);
        var updated = entry.Document with { UpdatedAt = DateTimeOffset.UtcNow };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
        return entry with { Document = updated };
    }

    public Task<ParticleEffectEditEntry> SetIncludedAsync(ParticleEffectEditEntry entry, bool included,
        CancellationToken cancellationToken = default) =>
        SaveAsync(entry with { Document = entry.Document with { IncludedInPackageBuild = included } }, cancellationToken);

    public Task DeleteAsync(ParticleEffectEditEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = RequireWithinRoot(entry.DirectoryPath);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private string RequireWithinRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(RootDirectory, full);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The particle edit path is outside Arkive's workspace.");
        return full;
    }
}
