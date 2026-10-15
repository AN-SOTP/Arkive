using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkive.Core.Discovery;

/// <param name="ObjectPath">The reference's path inside its package (tex.name), when the material named one.</param>
public sealed record GameAssetDependencyTarget(
    string PackageName,
    string ObjectName,
    string ClassName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ObjectPath = null);

public sealed record GameAssetDependencyProbe(
    string SourcePackagePath,
    long SourceFileLength,
    long SourceLastWriteUtcTicks,
    string SourcePackageName,
    string SourceObjectName,
    string DependencyProfile,
    string BackendPath,
    long BackendFileLength,
    long BackendLastWriteUtcTicks,
    DateTimeOffset ProbedAt,
    IReadOnlyList<GameAssetDependencyTarget> Targets,
    string? Error);

public sealed record GameAssetDependencyDocument(
    int Version,
    string MetadataPath,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<GameAssetAssociation> Associations,
    IReadOnlyList<GameAssetDependencyProbe> Probes);

public static class GameAssetDependencySidecar
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string GetDefaultPath(string metadataPath)
    {
        var fullPath = Path.GetFullPath(metadataPath);
        var directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        return Path.Combine(
            directory,
            Path.GetFileNameWithoutExtension(fullPath) + ".dependencies.json.gz");
    }

    public static IReadOnlyList<GameAssetAssociation> LoadAssociations(string metadataPath)
    {
        var document = TryLoad(GetDefaultPath(metadataPath));
        return document?.Associations ?? [];
    }

    public static GameAssetDependencyDocument? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var document = JsonSerializer.Deserialize<GameAssetDependencyDocument>(gzip, JsonOptions) ??
                       throw new InvalidDataException("The game-asset dependency sidecar is empty.");
        if (document.Version != CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported game-asset dependency sidecar version {document.Version}; " +
                $"expected {CurrentVersion}.");
        }

        return document with
        {
            Associations = document.Associations
                .Select(AvatarItemAssociationResolver.NormalizeArchetype)
                .ToArray(),
        };
    }

    public static async Task SaveAsync(
        string path,
        GameAssetDependencyDocument document,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var file = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            {
                await JsonSerializer.SerializeAsync(
                    gzip,
                    document,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
