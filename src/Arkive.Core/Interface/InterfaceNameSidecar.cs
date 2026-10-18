using System.IO.Compression;
using System.Text.Json;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Packages;

namespace Arkive.Core.Interface;

public sealed record InterfaceNamePackage(string FileName, long Length, long LastWriteUtcTicks);

public sealed record InterfaceNameDocument(
    int Version,
    DateTimeOffset GeneratedAt,
    string PackagesRoot,
    IReadOnlyList<InterfaceNamePackage> Packages,
    IReadOnlyList<GameAssetAssociation> Associations);

public sealed record InterfaceNameProgress(int Completed, int Total, string Package);

/// <summary>One place on an interface texture and what the game's movies call it.</summary>
/// <param name="Region">The rectangle it covers, as fractions of the texture; null for the whole texture.</param>
public sealed record InterfaceTexturePart(string Name, IReadOnlyList<string> Names, string Screen, InterfaceRegion? Region);

/// <summary>
/// In-game names for interface textures, read from the game's own interface movies (see
/// <see cref="InterfaceElementNames"/>) and kept beside the game-name database so searching "health bar" finds the
/// texture the HUD draws its HP bar from. Rebuilt whenever an interface package changes.
/// </summary>
public static class InterfaceNameSidecar
{
    public const int CurrentVersion = 1;
    public const string EntityType = "Interface";
    public const string Relation = "InterfaceMovieReference";
    private const string PackagePrefix = "efui_";
    private const string MovieClass = "efswfmovie";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string GetDefaultPath(string metadataPath)
    {
        var fullPath = Path.GetFullPath(metadataPath);
        return Path.Combine(Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(fullPath) + ".interface.json.gz");
    }

    public static IReadOnlyList<GameAssetAssociation> LoadAssociations(string metadataPath) =>
        TryLoad(GetDefaultPath(metadataPath))?.Associations ?? [];

    public static InterfaceNameDocument? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var document = JsonSerializer.Deserialize<InterfaceNameDocument>(gzip, JsonOptions);
            return document?.Version == CurrentVersion ? document : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>The interface packages in a Packages folder, by their decoded names ("efui_quickslot").</summary>
    public static IReadOnlyList<FileInfo> FindPackages(string packagesRoot) =>
        new DirectoryInfo(packagesRoot).EnumerateFiles("*.upk")
            .Where(file => LostArkPackageName.TryDecodeFileName(file.Name, out var name) &&
                           name.StartsWith(PackagePrefix, StringComparison.Ordinal))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>True when the saved names were read from exactly the interface packages the folder holds now.</summary>
    public static bool IsCurrent(string metadataPath, string packagesRoot)
    {
        var document = TryLoad(GetDefaultPath(metadataPath));
        if (document is null ||
            !Path.GetFullPath(document.PackagesRoot).Equals(Path.GetFullPath(packagesRoot), StringComparison.OrdinalIgnoreCase))
            return false;
        var packages = FindPackages(packagesRoot);
        return packages.Count == document.Packages.Count &&
               packages.Zip(document.Packages).All(pair =>
                   pair.First.Name.Equals(pair.Second.FileName, StringComparison.OrdinalIgnoreCase) &&
                   pair.First.Length == pair.Second.Length &&
                   pair.First.LastWriteTimeUtc.Ticks == pair.Second.LastWriteUtcTicks);
    }

    public static async Task<InterfaceNameDocument> BuildAsync(
        string metadataPath,
        string packagesRoot,
        IChunkProtectionProvider protectionProvider,
        IProgress<InterfaceNameProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var packages = FindPackages(packagesRoot);
        var reader = new UpkPackageReader();
        var catalogReader = new AssetCatalogReader(reader, protectionProvider);
        var associations = new List<GameAssetAssociation>();
        var completed = 0;
        await Parallel.ForEachAsync(packages,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), CancellationToken = cancellationToken },
            (package, token) =>
            {
                var found = ReadPackage(package.FullName, reader, catalogReader, protectionProvider, token);
                lock (associations) associations.AddRange(found);
                progress?.Report(new InterfaceNameProgress(Interlocked.Increment(ref completed), packages.Count, package.Name));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        var document = new InterfaceNameDocument(
            CurrentVersion, DateTimeOffset.UtcNow, Path.GetFullPath(packagesRoot),
            packages.Select(file => new InterfaceNamePackage(file.Name, file.Length, file.LastWriteTimeUtc.Ticks)).ToArray(),
            associations.OrderBy(item => item.PackageName, StringComparer.Ordinal)
                .ThenBy(item => item.ObjectName, StringComparer.Ordinal)
                .ThenBy(item => item.DisplayName, StringComparer.Ordinal)
                .ToArray());
        await SaveAsync(GetDefaultPath(metadataPath), document, cancellationToken).ConfigureAwait(false);
        return document;
    }

    /// <summary>The names one interface package's movies give its textures. A package that can't be read gives none.</summary>
    public static IReadOnlyList<GameAssetAssociation> ReadPackage(
        string packagePath,
        UpkPackageReader reader,
        AssetCatalogReader catalogReader,
        IChunkProtectionProvider protectionProvider,
        CancellationToken cancellationToken = default)
    {
        var alias = LostArkPackageName.GetDisplayName(packagePath);
        var results = new List<GameAssetAssociation>();
        foreach (var (movieName, screen, texture) in ReadTextures(packagePath, reader, catalogReader, protectionProvider, cancellationToken))
        {
            results.Add(Create($"{screen} (interface)", movieName, movieName, alias, texture.TextureName));
            foreach (var name in texture.Elements.Select(element => (element.Name, element.Identifier)).Distinct())
                results.Add(Create($"{name.Name} · {screen}", name.Identifier, movieName, alias, texture.TextureName));
        }
        return results;
    }

    /// <summary>
    /// The named parts of one texture of an interface package, one per place on the texture, so an editor can pick
    /// "Castbar fill HP HUD" and change only that rectangle. Empty when no movie of the package draws the texture.
    /// </summary>
    public static IReadOnlyList<InterfaceTexturePart> FindParts(
        string packagePath, string textureName, IChunkProtectionProvider protectionProvider, CancellationToken cancellationToken = default)
    {
        var reader = new UpkPackageReader();
        var parts = new List<InterfaceTexturePart>();
        foreach (var (_, screen, texture) in ReadTextures(packagePath, reader, new AssetCatalogReader(reader, protectionProvider),
                     protectionProvider, cancellationToken))
        {
            if (!texture.TextureName.Equals(textureName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var group in texture.Elements.GroupBy(element => element.Region))
            {
                var names = group.Select(element => element.Name).Distinct().ToArray();
                if (parts.Any(part => part.Region == group.Key && part.Names.SequenceEqual(names))) continue;
                parts.Add(new InterfaceTexturePart(names[0], names, screen, group.Key));
            }
        }
        return parts;
    }

    private static IEnumerable<(string MovieName, string Screen, InterfaceTextureNames Texture)> ReadTextures(
        string packagePath,
        UpkPackageReader reader,
        AssetCatalogReader catalogReader,
        IChunkProtectionProvider protectionProvider,
        CancellationToken cancellationToken)
    {
        var results = new List<(string, string, InterfaceTextureNames)>();
        try
        {
            var summary = reader.Read(packagePath);
            var catalog = catalogReader.Read(summary);
            var textures = catalog.Assets.Where(asset => asset.Kind == AssetKind.Texture)
                .Select(asset => asset.ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var logical = new LogicalPackageStream(summary, protectionProvider);
            foreach (var export in catalog.Assets.Where(asset => asset.ClassName.Equals(MovieClass, StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = new byte[export.SerialSize];
                logical.Position = export.SerialOffset;
                logical.ReadExactly(bytes);
                GfxMovieStructure movie;
                try
                {
                    movie = GfxMovieReader.Read(GfxMovieFile.Extract(bytes));
                }
                catch (InvalidDataException)
                {
                    continue;
                }
                var screen = InterfaceElementNames.ScreenName(movie, export.ObjectName);
                foreach (var texture in InterfaceElementNames.Find(movie).Where(item => textures.Contains(item.TextureName)))
                    results.Add((export.ObjectName, screen, texture));
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or PackageFormatException or
                                              UnauthorizedAccessException or EndOfStreamException)
        {
        }
        return results;
    }

    private static GameAssetAssociation Create(string displayName, string identifier, string movie, string alias, string texture) =>
        new(displayName, identifier, EntityType, 0, movie, $"{alias}.{texture}", alias, texture)
        {
            Confidence = GameAssetAssociationConfidence.Verified,
            Relation = Relation,
        };

    private static async Task SaveAsync(string path, InterfaceNameDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                await JsonSerializer.SerializeAsync(gzip, document, JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
