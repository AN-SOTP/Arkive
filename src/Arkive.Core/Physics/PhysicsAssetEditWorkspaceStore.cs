using System.Text.Json;
using Arkive.Core.Particles;

namespace Arkive.Core.Physics;

/// <summary>
/// A physics asset working copy: which character physics asset of which package it changes, the body shape changes
/// (the fit) and the value patches they make in that package version.
/// </summary>
public sealed record PhysicsAssetEditDocument(
    int SchemaVersion,
    string EditId,
    string DisplayName,
    string SourcePackagePath,
    string PhysicalPackageName,
    string PackageAlias,
    string SourcePackageSha256,
    string AssetObjectName,
    string AssetObjectPath,
    int AssetExportIndex,
    string AssetExportSha256,
    BodyPhysicsFit Fit,
    IReadOnlyList<ParticleEffectPatch> Patches,
    bool IncludedInPackageBuild,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Identifies what the edit writes: changes whenever any patched value does.</summary>
    public string ContentSha256 => ParticleEffects.ContentSha256(Patches);
}

public sealed record PhysicsAssetEditEntry(string DirectoryPath, string DocumentPath, PhysicsAssetEditDocument Document);

/// <summary>Physics asset working copies, one folder each under the data folder's staging\physics-edits.</summary>
public sealed class PhysicsAssetEditWorkspaceStore(string rootDirectory)
{
    public const string DocumentFileName = "arkive-physics-edit.json";
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);

    private readonly Mods.ParsedRecordCache<PhysicsAssetEditEntry> _parsed = new();

    public async Task<IReadOnlyList<PhysicsAssetEditEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var entries = new List<PhysicsAssetEditEntry>();
        foreach (var path in Directory.EnumerateFiles(RootDirectory, DocumentFileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { entries.Add(await _parsed.GetAsync(path, LoadEntryAsync, cancellationToken).ConfigureAwait(false)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                // One damaged working copy must not hide the others.
            }
        }
        return entries.OrderByDescending(entry => entry.Document.UpdatedAt).ToArray();
    }

    public async Task<PhysicsAssetEditEntry> LoadEntryAsync(string documentPath, CancellationToken cancellationToken = default)
    {
        var path = RequireWithinRoot(documentPath);
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<PhysicsAssetEditDocument>(stream, JsonOptions, cancellationToken)
                           .ConfigureAwait(false) ?? throw new InvalidDataException("The physics working copy was empty.");
        if (document.SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Physics edit schema {document.SchemaVersion} is unsupported.");
        return new PhysicsAssetEditEntry(Path.GetDirectoryName(path)!, path, document);
    }

    /// <summary>A new working copy that writes <paramref name="fit"/> into <paramref name="report"/>'s asset; excluded from builds until selected.</summary>
    public async Task<PhysicsAssetEditEntry> CreateAsync(
        PhysicsAssetReport report, string packageAlias, string displayName, BodyPhysicsFit fit,
        CancellationToken cancellationToken = default)
    {
        var editId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var document = new PhysicsAssetEditDocument(
            CurrentSchemaVersion, editId, displayName, report.PackagePath, Path.GetFileName(report.PackagePath), packageAlias,
            report.PackageSha256, report.Asset.ObjectName, report.Asset.ObjectPath, report.Asset.ExportIndex,
            report.AssetExportSha256, fit, PhysicsAssets.Patches(report, fit), false, now, now);
        var directory = Path.Combine(RootDirectory, editId);
        Directory.CreateDirectory(directory);
        return await SaveAsync(new PhysicsAssetEditEntry(directory, Path.Combine(directory, DocumentFileName), document),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PhysicsAssetEditEntry> SaveAsync(PhysicsAssetEditEntry entry, CancellationToken cancellationToken = default)
    {
        var path = RequireWithinRoot(entry.DocumentPath);
        var updated = entry.Document with { UpdatedAt = DateTimeOffset.UtcNow };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
        return entry with { Document = updated };
    }

    public Task<PhysicsAssetEditEntry> SetIncludedAsync(PhysicsAssetEditEntry entry, bool included,
        CancellationToken cancellationToken = default) =>
        SaveAsync(entry with { Document = entry.Document with { IncludedInPackageBuild = included } }, cancellationToken);

    public Task DeleteAsync(PhysicsAssetEditEntry entry, CancellationToken cancellationToken = default)
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
            throw new UnauthorizedAccessException("The physics edit path is outside Arkive's workspace.");
        return full;
    }
}
