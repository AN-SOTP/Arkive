using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;

namespace Arkive.Core.Discovery;

internal sealed record PackageSearchRoute(
    string CacheFileName,
    string PackageAlias,
    string PhysicalPackageName,
    int AssetCount,
    bool HasError);

internal sealed record PackageSearchRoutingDocument(
    int SchemaVersion,
    DateTimeOffset BuiltAt,
    string LibraryRoot,
    string IndexIdentity,
    string MetadataSourcePath,
    long MetadataSourceLength,
    long MetadataSourceLastWriteUtcTicks,
    int MetadataAssociationCount,
    IReadOnlyList<PackageSearchRoute> Packages,
    Dictionary<string, string[]> ObjectRoutes);

internal sealed record SearchRoutingPreparation(
    PackageSearchRoutingIndex Index,
    bool WasBuilt,
    TimeSpan Duration);

internal sealed class PackageSearchRoutingIndex
{
    private readonly Dictionary<string, HashSet<string>> _packageRoutes =
        new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, string[]> _objectRoutes;

    public PackageSearchRoutingIndex(PackageSearchRoutingDocument document)
    {
        Document = document;
        _objectRoutes = document.ObjectRoutes;
        foreach (var package in document.Packages)
        {
            foreach (var name in GameAssetMetadataIndex.PackageNameVariants(package.PackageAlias)
                         .Concat(GameAssetMetadataIndex.PackageNameVariants(package.PhysicalPackageName))
                         .Distinct(StringComparer.Ordinal))
            {
                if (!_packageRoutes.TryGetValue(name, out var cacheFiles))
                {
                    cacheFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _packageRoutes.Add(name, cacheFiles);
                }

                cacheFiles.Add(package.CacheFileName);
            }
        }
    }

    public PackageSearchRoutingDocument Document { get; }

    public bool Matches(string rootPath, GameAssetMetadataIndex metadata) =>
        Document.LibraryRoot.Equals(Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase) &&
        Document.MetadataSourcePath.Equals(metadata.SourcePath, StringComparison.OrdinalIgnoreCase) &&
        Document.MetadataSourceLength == metadata.SourceLength &&
        Document.MetadataSourceLastWriteUtcTicks == metadata.SourceLastWriteUtcTicks &&
        Document.MetadataAssociationCount == metadata.AssociationCount;

    public IReadOnlyList<string> Resolve(
        GameAssetMetadataIndex.SearchPlan plan,
        string cacheDirectory)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageName in plan.PackageNames)
        {
            if (_packageRoutes.TryGetValue(packageName, out var cacheFiles))
            {
                candidates.UnionWith(cacheFiles);
            }
        }

        foreach (var objectName in plan.ObjectOnlyReferences)
        {
            if (_objectRoutes.TryGetValue(objectName, out var cacheFiles))
            {
                candidates.UnionWith(cacheFiles);
            }
        }

        return candidates
            .Select(fileName => Path.Combine(cacheDirectory, fileName))
            .Where(File.Exists)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> ResolvePackageFamilies(
        IEnumerable<string> packageNames,
        string cacheDirectory)
    {
        var anchors = packageNames
            .Select(GameAssetMetadataIndex.NormalizePackageName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (anchors.Length == 0)
        {
            return [];
        }

        return Document.Packages
            .Where(route => !route.HasError && anchors.Any(anchor =>
            {
                var alias = GameAssetMetadataIndex.NormalizePackageName(route.PackageAlias);
                var physical = GameAssetMetadataIndex.NormalizePackageName(route.PhysicalPackageName);
                return IsFamily(alias, anchor) || IsFamily(physical, anchor);
            }))
            .Select(route => Path.Combine(cacheDirectory, route.CacheFileName))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> ResolvePackages(
        IEnumerable<string> packageNames,
        string cacheDirectory)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageName in packageNames.Select(GameAssetMetadataIndex.NormalizePackageName))
        {
            if (_packageRoutes.TryGetValue(packageName, out var cacheFiles))
            {
                candidates.UnionWith(cacheFiles);
            }
        }

        return candidates
            .Select(fileName => Path.Combine(cacheDirectory, fileName))
            .Where(File.Exists)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsFamily(string candidate, string anchor) =>
        candidate.Equals(anchor, StringComparison.Ordinal) ||
        candidate.StartsWith(anchor + "_", StringComparison.Ordinal) ||
        candidate.StartsWith("FACECUST_" + anchor, StringComparison.Ordinal);
}

public sealed partial class PackageIndexStore
{
    private const int SearchRoutingSchemaVersion = 1;
    private const string SearchRoutingFileName = "search-routing-v1.json.gz";

    private readonly SemaphoreSlim _searchRoutingGate = new(1, 1);
    private PackageSearchRoutingIndex? _searchRouting;

    public string SearchRoutingPath => Path.Combine(_cacheDirectory, SearchRoutingFileName);

    private void InvalidateSearchRouting() => _searchRouting = null;

    private async Task<SearchRoutingPreparation> PrepareSearchRoutingAsync(
        string rootPath,
        GameAssetMetadataIndex metadata,
        IProgress<PackageSearchRoutingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        if (_searchRouting?.Matches(fullRoot, metadata) == true)
        {
            return new SearchRoutingPreparation(_searchRouting, false, TimeSpan.Zero);
        }

        await _searchRoutingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_searchRouting?.Matches(fullRoot, metadata) == true)
            {
                return new SearchRoutingPreparation(_searchRouting, false, TimeSpan.Zero);
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var cacheFiles = EnumerateCacheFiles().ToArray();
            var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
            var indexIdentity = CreateRoutingIndexIdentity(manifest, cacheFiles);
            var loaded = await TryLoadSearchRoutingAsync(cancellationToken).ConfigureAwait(false);
            if (loaded is not null &&
                loaded.SchemaVersion == SearchRoutingSchemaVersion &&
                loaded.LibraryRoot.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) &&
                loaded.IndexIdentity.Equals(indexIdentity, StringComparison.Ordinal) &&
                loaded.MetadataSourcePath.Equals(metadata.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                loaded.MetadataSourceLength == metadata.SourceLength &&
                loaded.MetadataSourceLastWriteUtcTicks == metadata.SourceLastWriteUtcTicks &&
                loaded.MetadataAssociationCount == metadata.AssociationCount)
            {
                _searchRouting = new PackageSearchRoutingIndex(loaded);
                watch.Stop();
                return new SearchRoutingPreparation(_searchRouting, false, watch.Elapsed);
            }

            var built = await BuildSearchRoutingAsync(
                fullRoot,
                indexIdentity,
                cacheFiles,
                metadata,
                progress,
                cancellationToken).ConfigureAwait(false);
            await SaveSearchRoutingAsync(built, cancellationToken).ConfigureAwait(false);
            _searchRouting = new PackageSearchRoutingIndex(built);
            watch.Stop();
            return new SearchRoutingPreparation(_searchRouting, true, watch.Elapsed);
        }
        finally
        {
            _searchRoutingGate.Release();
        }
    }

    public async Task<IReadOnlyList<PackageIndexDocument>> LoadPackagesByAliasAsync(
        string rootPath,
        IEnumerable<string> packageNames,
        GameAssetMetadataIndex metadata,
        IProgress<PackageSearchRoutingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageNames);
        ArgumentNullException.ThrowIfNull(metadata);
        var routing = await PrepareSearchRoutingAsync(
            rootPath,
            metadata,
            progress,
            cancellationToken).ConfigureAwait(false);
        var files = routing.Index.ResolvePackages(packageNames, _cacheDirectory);
        var documents = new List<PackageIndexDocument>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = OpenCacheRead(file);
                var document = await JsonSerializer.DeserializeAsync<PackageIndexDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                if (document is not null && document.Error is null)
                {
                    documents.Add(document);
                }
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException or IOException)
            {
                // Index health reports corrupt entries; dependency resolution skips them.
            }
        }

        return documents;
    }

    private async Task<PackageSearchRoutingDocument> BuildSearchRoutingAsync(
        string rootPath,
        string indexIdentity,
        IReadOnlyList<string> cacheFiles,
        GameAssetMetadataIndex metadata,
        IProgress<PackageSearchRoutingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var objectReferences = metadata.ObjectOnlyReferenceNames.ToHashSet(StringComparer.Ordinal);
        var referenceTrie = new ObjectReferenceTrie(objectReferences);
        var routes = new ConcurrentBag<PackageSearchRoute>();
        var objectRoutes = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(
            StringComparer.Ordinal);
        foreach (var reference in objectReferences)
        {
            objectRoutes.TryAdd(
                reference,
                new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        }

        var completed = 0;
        await Parallel.ForEachAsync(
            cacheFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = RecommendedSearchParallelism,
                CancellationToken = cancellationToken,
            },
            async (cacheFile, token) =>
            {
                try
                {
                    await using var stream = OpenCacheRead(cacheFile);
                    var document = await JsonSerializer.DeserializeAsync<PackageIndexDocument>(
                        stream,
                        JsonOptions,
                        token).ConfigureAwait(false);
                    if (document is null ||
                        string.IsNullOrEmpty(document.SourcePath) ||
                        !document.SourcePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    var cacheFileName = Path.GetFileName(cacheFile);
                    routes.Add(new PackageSearchRoute(
                        cacheFileName,
                        document.PackageAlias,
                        document.PhysicalPackageName,
                        document.Assets.Count,
                        document.Error is not null));
                    if (document.Error is not null || objectReferences.Count == 0)
                    {
                        return;
                    }

                    foreach (var asset in document.Assets)
                    {
                        token.ThrowIfCancellationRequested();
                        var normalizedObject = GameAssetMetadataIndex.NormalizeObjectName(asset.ObjectName);
                        foreach (var reference in referenceTrie.Match(normalizedObject))
                        {
                            objectRoutes[reference].TryAdd(cacheFileName, 0);
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is JsonException or InvalidDataException or IOException)
                {
                    // Index health owns corrupt-cache reporting. A rebuild will replace it.
                }
                finally
                {
                    var current = Interlocked.Increment(ref completed);
                    if (current == cacheFiles.Count || current % 100 == 0)
                    {
                        progress?.Report(new PackageSearchRoutingProgress(
                            current,
                            cacheFiles.Count,
                            Path.GetFileName(cacheFile)));
                    }
                }
            }).ConfigureAwait(false);

        return new PackageSearchRoutingDocument(
            SearchRoutingSchemaVersion,
            DateTimeOffset.UtcNow,
            rootPath,
            indexIdentity,
            metadata.SourcePath,
            metadata.SourceLength,
            metadata.SourceLastWriteUtcTicks,
            metadata.AssociationCount,
            routes
                .OrderBy(route => route.PackageAlias, StringComparer.OrdinalIgnoreCase)
                .ThenBy(route => route.PhysicalPackageName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            objectRoutes.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.Ordinal));
    }

    private async Task<PackageSearchRoutingDocument?> TryLoadSearchRoutingAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(SearchRoutingPath))
        {
            return null;
        }

        try
        {
            await using var file = File.OpenRead(SearchRoutingPath);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return await JsonSerializer.DeserializeAsync<PackageSearchRoutingDocument>(
                gzip,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    private async Task SaveSearchRoutingAsync(
        PackageSearchRoutingDocument document,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var temporaryPath = SearchRoutingPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var file = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                await JsonSerializer.SerializeAsync(
                    gzip,
                    document,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, SearchRoutingPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string CreateRoutingIndexIdentity(
        PackageIndexManifest? manifest,
        IReadOnlyList<string> cacheFiles)
    {
        long bytes = 0;
        long newestTicks = 0;
        foreach (var path in cacheFiles)
        {
            var file = new FileInfo(path);
            bytes += file.Length;
            newestTicks = Math.Max(newestTicks, file.LastWriteTimeUtc.Ticks);
        }

        return string.Join(
            ':',
            manifest?.SchemaVersion ?? 0,
            manifest?.CompletedAt.UtcTicks ?? 0,
            manifest?.BackendSha256 ?? "none",
            manifest?.AssetCount ?? 0,
            cacheFiles.Count,
            bytes,
            newestTicks);
    }

    private sealed class ObjectReferenceTrie
    {
        private readonly Node _root = new();

        public ObjectReferenceTrie(IEnumerable<string> references)
        {
            foreach (var reference in references)
            {
                var node = _root;
                foreach (var character in reference)
                {
                    if (!node.Children.TryGetValue(character, out var child))
                    {
                        child = new Node();
                        node.Children.Add(character, child);
                    }

                    node = child;
                }

                node.Reference = reference;
            }
        }

        public IReadOnlyList<string> Match(string candidate)
        {
            var node = _root;
            List<string>? matches = null;
            for (var index = 0; index < candidate.Length; index++)
            {
                if (!node.Children.TryGetValue(candidate[index], out node!))
                {
                    break;
                }

                if (node.Reference is not null &&
                    (index + 1 == candidate.Length || candidate[index + 1] is '_' or '-'))
                {
                    matches ??= [];
                    matches.Add(node.Reference);
                }
            }

            return matches ?? [];
        }

        private sealed class Node
        {
            public Dictionary<char, Node> Children { get; } = [];

            public string? Reference { get; set; }
        }
    }
}
