using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Arkive.Core.Assets;
using Arkive.Core.External;

namespace Arkive.Core.Discovery;

public sealed partial class PackageIndexStore
{
    public const int CurrentSchemaVersion = 1;

    public static int RecommendedMaxParallelism =>
        Math.Clamp(Math.Max(2, Environment.ProcessorCount / 2), 2, 6);

    public static int RecommendedSearchParallelism =>
        Math.Clamp(Math.Max(2, Environment.ProcessorCount / 2), 2, 4);

    private const string BackendProfile = "lostark-na-nameresolve-noanim-list-v1";
    private const string ManifestFileName = "index-manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _cacheDirectory;

    public PackageIndexStore(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    public string CacheDirectory => _cacheDirectory;

    public string ManifestPath => Path.Combine(_cacheDirectory, ManifestFileName);

    public Task<PackageIndexManifest?> GetManifestAsync(
        CancellationToken cancellationToken = default) =>
        LoadManifestAsync(cancellationToken);

    public async Task<PackageIndexReport> BuildAsync(
        string rootPath,
        UmodelBridge bridge,
        IProgress<PackageIndexProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int? maxParallelism = null,
        string? hardLinkScratchRoot = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        InvalidateSearchRouting();
        var fullRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"The package folder was not found: {fullRoot}");
        }

        Directory.CreateDirectory(_cacheDirectory);
        var packages = Directory
            .EnumerateFiles(fullRoot, "*.upk", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var parallelism = maxParallelism ?? RecommendedMaxParallelism;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parallelism);
        var backend = CreateBackendIdentity(bridge.ExecutablePath);
        var existingManifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        var cacheCompatible = existingManifest is null
            ? !File.Exists(ManifestPath)
            : IsCompatible(existingManifest, backend);
        var compressedCache = LoadCompressedCacheEntries();
        var completed = 0;
        var indexed = 0;
        var cached = 0;
        var failed = 0;
        var assetCount = 0;

        await Parallel.ForEachAsync(
            packages,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = cancellationToken,
            },
            async (packagePath, token) =>
            {
                var fresh = await TryLoadFreshAsync(
                        packagePath,
                        compressedCache,
                        token,
                        cacheCompatible)
                    .ConfigureAwait(false);
                if (fresh is not null)
                {
                    Interlocked.Increment(ref cached);
                    Interlocked.Add(ref assetCount, fresh.AssetCount);
                }
                else
                {
                    PackageIndexDocument document;
                    var file = new FileInfo(packagePath);
                    try
                    {
                        var result = await bridge.ListAsync(
                            packagePath,
                            hardLinkScratchRoot,
                            token).ConfigureAwait(false);
                        document = new PackageIndexDocument(
                            packagePath,
                            Path.GetFileNameWithoutExtension(packagePath),
                            LostArkPackageName.GetDisplayName(packagePath),
                            file.Length,
                            file.LastWriteTimeUtc.Ticks,
                            DateTimeOffset.UtcNow,
                            result.Assets.Select(asset => new IndexedAsset(
                                asset.ExportIndex,
                                asset.Kind,
                                asset.DisplayClassName,
                                asset.ObjectName,
                                asset.SerialSize,
                                asset.SerialOffset)).ToArray(),
                            Error: null);
                        Interlocked.Increment(ref indexed);
                        Interlocked.Add(ref assetCount, document.Assets.Count);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException or UmodelBridgeException)
                    {
                        Interlocked.Increment(ref failed);
                        document = new PackageIndexDocument(
                            packagePath,
                            Path.GetFileNameWithoutExtension(packagePath),
                            LostArkPackageName.GetDisplayName(packagePath),
                            file.Length,
                            file.LastWriteTimeUtc.Ticks,
                            DateTimeOffset.UtcNow,
                            [],
                            exception.Message);
                    }

                    compressedCache.TryGetValue(
                        GetCachePrefix(packagePath),
                        out var staleCompressedEntry);
                    await SaveAsync(document, token, staleCompressedEntry?.Path).ConfigureAwait(false);
                }

                var completedCount = Interlocked.Increment(ref completed);
                progress?.Report(new PackageIndexProgress(
                    completedCount,
                    packages.Length,
                    Volatile.Read(ref cached),
                    Volatile.Read(ref failed),
                    Path.GetFileName(packagePath)));
            }).ConfigureAwait(false);

        progress?.Report(new PackageIndexProgress(
            packages.Length,
            packages.Length,
            cached,
            failed,
            string.Empty));
        await SaveManifestAsync(
            new PackageIndexManifest(
                CurrentSchemaVersion,
                BackendProfile,
                backend.Path,
                backend.Sha256,
                backend.Length,
                backend.LastWriteUtcTicks,
                fullRoot,
                DateTimeOffset.UtcNow,
                packages.Length,
                packages.Length - failed,
                failed,
                assetCount),
            cancellationToken).ConfigureAwait(false);
        return new PackageIndexReport(
            fullRoot,
            packages.Length,
            indexed,
            cached,
            failed,
            assetCount,
            stopwatch.Elapsed);
    }

    public async Task<PackageIndexSearchResult> SearchAsync(
        string rootPath,
        string query,
        string? category,
        GameAssetMetadataIndex? gameMetadata = null,
        PackageIndexSearchScope searchScope = PackageIndexSearchScope.AllFields,
        string? archetype = null,
        int resultLimit = 5_000,
        IProgress<PackageSearchRoutingProgress>? routingProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resultLimit);
        if (!Directory.Exists(_cacheDirectory))
        {
            return new PackageIndexSearchResult([], 0, 0, 0);
        }

        var rootPrefix = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
        var hasArchetypeFilter = !string.IsNullOrWhiteSpace(archetype);
        var metadataPlanQuery = hasArchetypeFilter &&
                                searchScope is not PackageIndexSearchScope.InGameName
            ? string.Empty
            : searchScope is PackageIndexSearchScope.ObjectName
                ? string.Empty
                : query;
        var metadataSearchPlan = gameMetadata?.CreateSearchPlan(
            metadataPlanQuery,
            displayNamesOnly: searchScope is PackageIndexSearchScope.InGameName,
            archetype);
        IEnumerable<string> searchCacheFiles = EnumerateCacheFiles();
        var usedRoutingIndex = false;
        var routingIndexBuilt = false;
        var routedPackageCount = 0;
        var routingPreparationSeconds = 0d;
        if ((searchScope == PackageIndexSearchScope.InGameName &&
             !string.IsNullOrWhiteSpace(query)) || hasArchetypeFilter)
        {
            usedRoutingIndex = true;
            if (gameMetadata is null || metadataSearchPlan?.IsEmpty != false)
            {
                return new PackageIndexSearchResult(
                    [], 0, 0, 0,
                    UsedRoutingIndex: true,
                    RoutedPackageCount: 0);
            }

            var routing = await PrepareSearchRoutingAsync(
                rootPath,
                gameMetadata,
                routingProgress,
                cancellationToken).ConfigureAwait(false);
            var candidates = routing.Index.Resolve(metadataSearchPlan, _cacheDirectory);
            searchCacheFiles = candidates;
            routingIndexBuilt = routing.WasBuilt;
            routedPackageCount = candidates.Count;
            routingPreparationSeconds = routing.Duration.TotalSeconds;
        }

        // Object-name and all-field searches can't be routed through the game database, so a
        // per-package name filter skips the documents that can't hold every query term.
        var nameFilterTerms = usedRoutingIndex
            ? null
            : PackageNameFilter.PlanTerms(query, searchScope);
        PackageNameFilter? nameFilter = null;
        Dictionary<string, FileInfo>? nameFilterMisses = null;
        string[] liveCacheFileNames = [];
        if (nameFilterTerms is not null)
        {
            nameFilter = await LoadNameFilterAsync(cancellationToken).ConfigureAwait(false);
            var metadataCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (gameMetadata is not null && metadataSearchPlan is { IsEmpty: false })
            {
                // Game-database text only matches through the search plan's routed packages.
                var routing = await PrepareSearchRoutingAsync(
                    rootPath,
                    gameMetadata,
                    routingProgress,
                    cancellationToken).ConfigureAwait(false);
                metadataCandidates.UnionWith(routing.Index.Resolve(metadataSearchPlan, _cacheDirectory)
                    .Select(path => Path.GetFileName(path)));
                routingIndexBuilt = routing.WasBuilt;
                routingPreparationSeconds = routing.Duration.TotalSeconds;
            }

            var cacheFileInfos = EnumerateCacheFileInfos().ToArray();
            liveCacheFileNames = cacheFileInfos.Select(file => file.Name).ToArray();
            nameFilterMisses = cacheFileInfos
                .Where(file => !nameFilter.HasCurrentEntry(file))
                .ToDictionary(file => file.FullName, StringComparer.OrdinalIgnoreCase);
            var candidates = cacheFileInfos
                .Where(file => metadataCandidates.Contains(file.Name) ||
                               nameFilter.MayContain(file, nameFilterTerms))
                .Select(file => file.FullName)
                .ToArray();
            searchCacheFiles = candidates;
            usedRoutingIndex = true;
            routedPackageCount = candidates.Length;
        }

        var matches = new List<PackageIndexMatch>();
        var totalMatches = 0;
        var packageCount = 0;
        var failedPackageCount = 0;
        var resultGate = new object();
        await Parallel.ForEachAsync(
            searchCacheFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = RecommendedSearchParallelism,
                CancellationToken = cancellationToken,
            },
            async (cacheFile, token) =>
            {
                PackageIndexDocument? document;
                try
                {
                    await using var stream = OpenCacheRead(cacheFile);
                    document = await JsonSerializer.DeserializeAsync<PackageIndexDocument>(
                        stream,
                        JsonOptions,
                        token).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is JsonException or InvalidDataException or IOException)
                {
                    // Ignore an interrupted, corrupt, or obsolete cache entry. Index health
                    // reports it and a rebuild replaces it.
                    return;
                }

                if (document is not null &&
                    nameFilterMisses?.TryGetValue(cacheFile, out var cacheFileInfo) == true)
                {
                    nameFilter!.Add(cacheFileInfo, document);
                }

                if (document is null ||
                    string.IsNullOrEmpty(document.SourcePath) ||
                    !document.SourcePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Interlocked.Increment(ref packageCount);
                if (document.Error is not null)
                {
                    Interlocked.Increment(ref failedPackageCount);
                    return;
                }

                var localMatches = new List<PackageIndexMatch>();
                var localTotalMatches = 0;
                var physicalFilename = Path.GetFileName(document.SourcePath);
                var decodedFilename = document.PackageAlias + Path.GetExtension(document.SourcePath);
                foreach (var asset in document.Assets)
                {
                    token.ThrowIfCancellationRequested();
                    if (!string.IsNullOrEmpty(category) &&
                        !MatchesCategory(asset, category))
                    {
                        continue;
                    }

                    IReadOnlyList<GameAssetAssociation> metadata = [];
                    bool matchesSearch;
                    if (searchScope == PackageIndexSearchScope.ObjectName &&
                        !hasArchetypeFilter)
                    {
                        // Object-only searches do not need to perform millions of game-DB
                        // relationship resolutions. Metadata is attached only to final rows.
                        matchesSearch = AssetSearch.Matches(query, asset.ObjectName);
                    }
                    else
                    {
                        metadata = gameMetadata is not null &&
                                   metadataSearchPlan?.MayContain(
                                       document.PackageAlias,
                                       document.PhysicalPackageName,
                                       asset.ObjectName) == true
                            ? gameMetadata.Find(
                                document.PackageAlias,
                                document.PhysicalPackageName,
                                asset.ObjectName)
                            : [];
                        if (hasArchetypeFilter)
                        {
                            metadata = metadata
                                .Where(item => gameMetadata!.MatchesArchetype(item.Archetype, archetype!))
                                .ToArray();
                            if (metadata.Count == 0)
                            {
                                continue;
                            }
                        }

                        matchesSearch = searchScope switch
                        {
                            PackageIndexSearchScope.ObjectName => AssetSearch.Matches(
                                query,
                                asset.ObjectName),
                            PackageIndexSearchScope.InGameName => AssetSearch.MatchesDisplayNames(
                                query,
                                metadata.Select(item => item.DisplayName)),
                            PackageIndexSearchScope.ObjectAndInGameName => AssetSearch.Matches(
                                query,
                                metadata.Select(item => item.DisplayName)
                                    .Prepend(asset.ObjectName)
                                    .ToArray()),
                            _ => AssetSearch.Matches(
                                query,
                                document.PackageAlias,
                                document.PhysicalPackageName,
                                physicalFilename,
                                decodedFilename,
                                asset.ObjectName,
                                asset.ClassName,
                                AssetTypes.IndexedKind(asset.Kind, asset.ClassName).ToString(),
                                GameAssetMetadataIndex.BuildSearchText(metadata),
                                asset.ExportIndex.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture)),
                        };
                    }

                    if (!matchesSearch)
                    {
                        continue;
                    }

                    localTotalMatches++;
                    if (localMatches.Count < resultLimit)
                    {
                        localMatches.Add(new PackageIndexMatch(
                            document.SourcePath,
                            document.PackageAlias,
                            document.PhysicalPackageName,
                            document.Assets.Count,
                            asset.ExportIndex,
                            AssetTypes.IndexedKind(asset.Kind, asset.ClassName),
                            asset.ClassName,
                            asset.ObjectName,
                            asset.SerialSize,
                            asset.SerialOffset,
                            metadata));
                    }
                }

                lock (resultGate)
                {
                    totalMatches += localTotalMatches;
                    var remaining = resultLimit - matches.Count;
                    if (remaining > 0)
                    {
                        matches.AddRange(localMatches.Take(remaining));
                    }
                }
            }).ConfigureAwait(false);

        if (nameFilter?.IsDirty == true)
        {
            await SaveNameFilterAsync(nameFilter, liveCacheFileNames, cancellationToken)
                .ConfigureAwait(false);
        }

        if (gameMetadata is not null)
        {
            matches = matches
                .Select(match => match with
                {
                    GameMetadata = match.GameMetadata.Count > 0
                        ? match.GameMetadata
                        : gameMetadata.Find(
                            match.PackageAlias,
                            match.PhysicalPackageName,
                            match.ObjectName),
                })
                .ToList();
        }

        matches.Sort((left, right) =>
        {
            var package = StringComparer.OrdinalIgnoreCase.Compare(
                left.PackageAlias,
                right.PackageAlias);
            if (package != 0)
            {
                return package;
            }

            var objectName = StringComparer.OrdinalIgnoreCase.Compare(
                left.ObjectName,
                right.ObjectName);
            return objectName != 0
                ? objectName
                : left.ExportIndex.CompareTo(right.ExportIndex);
        });

        return new PackageIndexSearchResult(
            matches,
            totalMatches,
            packageCount,
            failedPackageCount,
            UsedRoutingIndex: usedRoutingIndex,
            RoutingIndexBuilt: routingIndexBuilt,
            RoutedPackageCount: routedPackageCount,
            RoutingPreparationSeconds: routingPreparationSeconds);
    }

    private static bool MatchesCategory(IndexedAsset asset, string category) =>
        category.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)
            ? asset.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)
            : AssetTypes.IndexedKind(asset.Kind, asset.ClassName).ToString().Equals(category, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds evidence-labelled candidates related to one indexed asset. Database
    /// associations are authoritative; package and naming families are deliberately
    /// reported as contextual or inferred rather than as serialized UE references.
    /// </summary>
    public async Task<AssetRelationshipResult> ExploreRelationshipsAsync(
        string rootPath,
        PackageIndexMatch seed,
        GameAssetMetadataIndex? gameMetadata = null,
        int resultLimit = 5_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resultLimit);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        if (!Directory.Exists(_cacheDirectory))
        {
            return new AssetRelationshipResult(
                seed, [], [], 0, 0, 0, false, stopwatch.Elapsed);
        }

        var seedMetadata = seed.GameMetadata.Count > 0
            ? seed.GameMetadata
            : gameMetadata?.Find(seed.PackageAlias, seed.PhysicalPackageName, seed.ObjectName) ?? [];
        var entityAssociations = gameMetadata?.ExpandEntities(seedMetadata) ?? seedMetadata;
        var references = entityAssociations.Count > 0 ? entityAssociations : seedMetadata;
        var exactReferences = new Dictionary<string, GameAssetAssociation>(StringComparer.Ordinal);
        var packageReferences = new Dictionary<string, List<(string Object, GameAssetAssociation Association)>>(StringComparer.Ordinal);
        var objectOnlyReferences = new List<(string Object, GameAssetAssociation Association)>();
        foreach (var association in references)
        {
            var objectName = NormalizeRelationshipObject(association.ObjectName);
            if (string.IsNullOrEmpty(objectName))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(association.PackageName))
            {
                objectOnlyReferences.Add((objectName, association));
                continue;
            }

            foreach (var packageName in RelationshipPackageVariants(association.PackageName))
            {
                exactReferences.TryAdd(RelationshipFullKey(packageName, objectName), association);
                if (!packageReferences.TryGetValue(packageName, out var packageObjects))
                {
                    packageObjects = [];
                    packageReferences.Add(packageName, packageObjects);
                }

                packageObjects.Add((objectName, association));
            }
        }

        var packageFamilyAnchors = RelationshipPackageVariants(seed.PackageAlias)
            .Concat(RelationshipPackageVariants(seed.PhysicalPackageName))
            .Concat(references.SelectMany(reference => RelationshipPackageVariants(reference.PackageName)))
            .Select(RelationshipFamilyKey)
            .Where(key => key.Length >= 5)
            .ToHashSet(StringComparer.Ordinal);
        var objectFamilyAnchors = references
            .Select(reference => NormalizeRelationshipObject(reference.ObjectName))
            .Append(NormalizeRelationshipObject(seed.ObjectName))
            .Select(RelationshipFamilyKey)
            .Where(key => key.Length >= 6)
            .ToHashSet(StringComparer.Ordinal);

        var rootPrefix = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
        var candidates = new List<AssetRelationshipMatch>();
        var resultGate = new object();
        var totalCandidates = 0;
        var packageCount = 0;
        var failedPackageCount = 0;
        var perEvidenceLimit = Math.Max(250, resultLimit / 3);
        var evidenceCounts = new Dictionary<AssetRelationshipEvidence, int>();

        await Parallel.ForEachAsync(
            EnumerateCacheFiles(),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = RecommendedSearchParallelism,
                CancellationToken = cancellationToken,
            },
            async (cacheFile, token) =>
            {
                PackageIndexDocument? document;
                try
                {
                    await using var stream = OpenCacheRead(cacheFile);
                    document = await JsonSerializer.DeserializeAsync<PackageIndexDocument>(
                        stream,
                        JsonOptions,
                        token).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is JsonException or InvalidDataException or IOException)
                {
                    return;
                }

                if (document is null ||
                    string.IsNullOrEmpty(document.SourcePath) ||
                    !document.SourcePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Interlocked.Increment(ref packageCount);
                if (document.Error is not null)
                {
                    Interlocked.Increment(ref failedPackageCount);
                    return;
                }

                var samePackage = document.SourcePath.Equals(
                    seed.SourcePath,
                    StringComparison.OrdinalIgnoreCase);
                var documentPackageNames = RelationshipPackageVariants(document.PackageAlias)
                    .Concat(RelationshipPackageVariants(document.PhysicalPackageName))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var packageFamily = documentPackageNames
                    .Select(RelationshipFamilyKey)
                    .Any(packageFamilyAnchors.Contains);

                foreach (var asset in document.Assets)
                {
                    token.ThrowIfCancellationRequested();
                    var relationship = ClassifyRelationship(
                        seed,
                        document,
                        asset,
                        samePackage,
                        packageFamily,
                        documentPackageNames,
                        exactReferences,
                        packageReferences,
                        objectOnlyReferences,
                        objectFamilyAnchors);
                    if (relationship is null)
                    {
                        continue;
                    }

                    Interlocked.Increment(ref totalCandidates);
                    var (evidence, confidence, reason, association) = relationship.Value;
                    lock (resultGate)
                    {
                        evidenceCounts.TryGetValue(evidence, out var evidenceCount);
                        var cap = evidence is AssetRelationshipEvidence.Seed or
                            AssetRelationshipEvidence.DirectDatabaseReference or
                            AssetRelationshipEvidence.DatabaseAssetFamily
                            ? resultLimit
                            : perEvidenceLimit;
                        if (evidenceCount >= cap)
                        {
                            continue;
                        }

                        evidenceCounts[evidence] = evidenceCount + 1;
                        candidates.Add(new AssetRelationshipMatch(
                            new PackageIndexMatch(
                                document.SourcePath,
                                document.PackageAlias,
                                document.PhysicalPackageName,
                                document.Assets.Count,
                                asset.ExportIndex,
                                AssetTypes.IndexedKind(asset.Kind, asset.ClassName),
                                asset.ClassName,
                                asset.ObjectName,
                                asset.SerialSize,
                                asset.SerialOffset,
                                []),
                            evidence,
                            confidence,
                            reason,
                            association));
                    }
                }
            }).ConfigureAwait(false);

        if (gameMetadata is not null)
        {
            candidates = candidates.Select(candidate => candidate with
            {
                Asset = candidate.Asset with
                {
                    GameMetadata = gameMetadata.Find(
                        candidate.Asset.PackageAlias,
                        candidate.Asset.PhysicalPackageName,
                        candidate.Asset.ObjectName),
                },
            }).ToList();
        }

        candidates.Sort((left, right) =>
        {
            var evidence = left.Evidence.CompareTo(right.Evidence);
            if (evidence != 0)
            {
                return evidence;
            }

            var kind = left.Asset.Kind.CompareTo(right.Asset.Kind);
            if (kind != 0)
            {
                return kind;
            }

            var package = StringComparer.OrdinalIgnoreCase.Compare(
                left.Asset.PackageAlias,
                right.Asset.PackageAlias);
            return package != 0
                ? package
                : StringComparer.OrdinalIgnoreCase.Compare(
                    left.Asset.ObjectName,
                    right.Asset.ObjectName);
        });
        if (candidates.Count > resultLimit)
        {
            candidates = candidates.Take(resultLimit).ToList();
        }
        stopwatch.Stop();
        return new AssetRelationshipResult(
            seed with { GameMetadata = seedMetadata },
            entityAssociations,
            candidates,
            totalCandidates,
            packageCount,
            failedPackageCount,
            totalCandidates > candidates.Count,
            stopwatch.Elapsed);
    }

    private static (
        AssetRelationshipEvidence Evidence,
        AssetRelationshipConfidence Confidence,
        string Reason,
        GameAssetAssociation? Association)? ClassifyRelationship(
        PackageIndexMatch seed,
        PackageIndexDocument document,
        IndexedAsset asset,
        bool samePackage,
        bool packageFamily,
        IReadOnlyList<string> documentPackageNames,
        IReadOnlyDictionary<string, GameAssetAssociation> exactReferences,
        IReadOnlyDictionary<string, List<(string Object, GameAssetAssociation Association)>> packageReferences,
        IReadOnlyList<(string Object, GameAssetAssociation Association)> objectOnlyReferences,
        IReadOnlySet<string> objectFamilyAnchors)
    {
        if (samePackage && asset.ExportIndex == seed.ExportIndex)
        {
            return (
                AssetRelationshipEvidence.Seed,
                AssetRelationshipConfidence.Authoritative,
                "Selected indexed asset",
                null);
        }

        var objectName = NormalizeRelationshipObject(asset.ObjectName);
        foreach (var packageName in documentPackageNames)
        {
            if (exactReferences.TryGetValue(
                    RelationshipFullKey(packageName, objectName),
                    out var exactAssociation))
            {
                return (
                    AssetRelationshipEvidence.DirectDatabaseReference,
                    AssetRelationshipConfidence.Authoritative,
                    $"Game DB {exactAssociation.EntityType} {exactAssociation.EntityId} field {exactAssociation.Field} directly references this asset",
                    exactAssociation);
            }

            if (packageReferences.TryGetValue(packageName, out var packageObjects))
            {
                var family = packageObjects.FirstOrDefault(reference =>
                    IsRelationshipFamilyMember(objectName, reference.Object));
                if (family.Association is not null)
                {
                    return (
                        AssetRelationshipEvidence.DatabaseAssetFamily,
                        AssetRelationshipConfidence.High,
                        $"Same package and naming family as game DB field {family.Association.Field} ({family.Association.AssetReference})",
                        family.Association);
                }
            }
        }

        var objectOnly = objectOnlyReferences.FirstOrDefault(reference =>
            objectName.Equals(reference.Object, StringComparison.Ordinal));
        if (objectOnly.Association is not null)
        {
            return (
                AssetRelationshipEvidence.DirectDatabaseReference,
                AssetRelationshipConfidence.Authoritative,
                $"Game DB {objectOnly.Association.EntityType} {objectOnly.Association.EntityId} field {objectOnly.Association.Field} directly references this object name",
                objectOnly.Association);
        }

        if (samePackage)
        {
            return (
                AssetRelationshipEvidence.SamePackage,
                AssetRelationshipConfidence.Contextual,
                "Stored in the selected asset's package; this does not prove a serialized reference",
                null);
        }

        if (packageFamily)
        {
            return (
                AssetRelationshipEvidence.PackageFamily,
                AssetRelationshipConfidence.Inferred,
                "Decoded/physical package name shares the selected asset family's stable stem",
                null);
        }

        var objectFamily = RelationshipFamilyKey(objectName);
        if (objectFamily.Length >= 6 && objectFamilyAnchors.Contains(objectFamily))
        {
            return (
                AssetRelationshipEvidence.ObjectNameFamily,
                AssetRelationshipConfidence.Inferred,
                "Object name shares a conventional asset-family stem; verify in UModel",
                null);
        }

        return null;
    }

    private static string RelationshipFullKey(string packageName, string objectName) =>
        packageName + "\0" + objectName;

    private static IEnumerable<string> RelationshipPackageVariants(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var normalized = Path.GetFileNameWithoutExtension(value.Trim()).ToUpperInvariant();
        yield return normalized;
        if (!normalized.StartsWith("EFDL", StringComparison.Ordinal))
        {
            yield break;
        }

        var separator = normalized.IndexOf('_', 4);
        if (separator >= 0 && separator + 1 < normalized.Length)
        {
            yield return normalized[(separator + 1)..];
        }
    }

    private static string NormalizeRelationshipObject(string value)
    {
        var normalized = value.Trim().Trim('\'', '"');
        var dot = normalized.LastIndexOf('.');
        return (dot >= 0 ? normalized[(dot + 1)..] : normalized).ToUpperInvariant();
    }

    private static bool IsRelationshipFamilyMember(string candidate, string reference) =>
        candidate.Equals(reference, StringComparison.Ordinal) ||
        (candidate.Length > reference.Length &&
         candidate.StartsWith(reference, StringComparison.Ordinal) &&
         candidate[reference.Length] is '_' or '-');

    private static string RelationshipFamilyKey(string value)
    {
        var key = value.ToUpperInvariant();
        string[] suffixes =
        [
            "_SKELETALMESH", "_STATICMESH", "_TEXTURE", "_MATERIAL",
            "_SK", "_SM", "_MIC", "_MI", "_MAT", "_TEX",
            "_DIFF", "_DIFFUSE", "_NORMAL", "_MASK", "_SPEC",
            "_D", "_N", "_M", "_S",
        ];
        bool stripped;
        do
        {
            stripped = false;
            foreach (var suffix in suffixes)
            {
                if (key.Length > suffix.Length + 3 && key.EndsWith(suffix, StringComparison.Ordinal))
                {
                    key = key[..^suffix.Length];
                    stripped = true;
                    break;
                }
            }
        } while (stripped);

        return key.TrimEnd('_', '-');
    }

    public async Task<PackageIndexHealth> GetHealthAsync(
        string? rootPath = null,
        UmodelBridge? bridge = null,
        CancellationToken cancellationToken = default)
    {
        var files = Directory.Exists(_cacheDirectory)
            ? EnumerateCacheFiles().ToArray()
            : [];
        var successful = 0;
        var failed = 0;
        var corrupt = 0;
        long assets = 0;
        long bytes = 0;
        var failures = new List<PackageIndexFailure>();
        var rootPrefix = string.IsNullOrWhiteSpace(rootPath)
            ? null
            : Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) +
              Path.DirectorySeparatorChar;

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            // An index build running at the same time can replace a cache document after it was listed.
            if (!info.Exists) continue;
            bytes += info.Length;
            var parsedFileName = TryParseCompressedCacheFileName(
                path,
                out var assetCount,
                out var hasError);
            if (parsedFileName)
            {
                assets += assetCount;
                if (!hasError)
                {
                    successful++;
                    continue;
                }
            }

            try
            {
                await using var stream = OpenCacheRead(path);
                var document = await JsonSerializer.DeserializeAsync<PackageIndexDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                if (document is null)
                {
                    corrupt++;
                    continue;
                }

                if (!parsedFileName)
                {
                    assets += document.Assets.Count;
                }

                if (document.Error is null)
                {
                    successful++;
                    continue;
                }

                failed++;
                if (rootPrefix is null ||
                    document.SourcePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(new PackageIndexFailure(
                        document.SourcePath,
                        document.PhysicalPackageName,
                        document.FileLength,
                        document.Error));
                }
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException or IOException)
            {
                corrupt++;
                failures.Add(new PackageIndexFailure(
                    path,
                    Path.GetFileName(path),
                    new FileInfo(path).Length,
                    $"Unreadable cache document: {exception.Message}"));
            }
        }

        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        bool provenanceMatches;
        string provenanceStatus;
        if (manifest is null)
        {
            provenanceMatches = files.Length == 0;
            provenanceStatus = files.Length == 0
                ? "No package index has been built yet."
                : File.Exists(ManifestPath)
                    ? "The index manifest is unreadable; rebuild/update is required."
                    : "Legacy cache detected. Build/update once to record its provenance.";
        }
        else if (bridge is null)
        {
            provenanceMatches = false;
            provenanceStatus = "The cached backend cannot be verified because UModel is unavailable.";
        }
        else
        {
            var backend = CreateBackendIdentity(bridge.ExecutablePath);
            provenanceMatches = IsCompatible(manifest, backend);
            provenanceStatus = provenanceMatches
                ? $"Schema {manifest.SchemaVersion}; UModel fingerprint verified."
                : "Arkive schema or UModel fingerprint changed; Build/update must refresh every package.";
        }

        if (File.Exists(ManifestPath))
        {
            bytes += new FileInfo(ManifestPath).Length;
        }

        if (File.Exists(SearchRoutingPath))
        {
            bytes += new FileInfo(SearchRoutingPath).Length;
        }

        return new PackageIndexHealth(
            _cacheDirectory,
            bytes,
            files.Length,
            successful,
            failed,
            corrupt,
            assets,
            manifest,
            provenanceMatches,
            provenanceStatus,
            failures
                .OrderBy(item => item.PhysicalPackageName, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private async Task<CachedPackageInfo?> TryLoadFreshAsync(
        string packagePath,
        IReadOnlyDictionary<string, CompressedCacheEntry> compressedCache,
        CancellationToken cancellationToken,
        bool cacheCompatible = true)
    {
        if (!cacheCompatible)
        {
            return null;
        }

        var file = new FileInfo(packagePath);
        var prefix = GetCachePrefix(packagePath);
        if (compressedCache.TryGetValue(prefix, out var compressed) &&
            compressed.FileLength == file.Length &&
            compressed.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks &&
            !compressed.HasError)
        {
            return new CachedPackageInfo(compressed.AssetCount);
        }

        var legacyPath = Path.Combine(_cacheDirectory, $"{prefix}.json");
        if (!File.Exists(legacyPath))
        {
            return null;
        }

        try
        {
            PackageIndexDocument? document;
            await using (var stream = File.OpenRead(legacyPath))
            {
                document = await JsonSerializer.DeserializeAsync<PackageIndexDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            if (document is null ||
                document.FileLength != file.Length ||
                document.LastWriteUtcTicks != file.LastWriteTimeUtc.Ticks ||
                document.Error is not null)
            {
                return null;
            }

            await SaveAsync(document, cancellationToken).ConfigureAwait(false);
            return new CachedPackageInfo(document.Assets.Count);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task SaveAsync(
        PackageIndexDocument document,
        CancellationToken cancellationToken,
        string? staleCompressedPath = null)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var prefix = GetCachePrefix(document.SourcePath);
        var status = document.Error is null ? "O" : "E";
        var path = Path.Combine(
            _cacheDirectory,
            $"{prefix}.{document.FileLength:X16}.{document.LastWriteUtcTicks:X16}." +
            $"{document.Assets.Count:D10}.{status}.json.gz");
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        await using (var fileStream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         81920,
                         FileOptions.Asynchronous))
        await using (var stream = new GZipStream(
                         fileStream,
                         CompressionLevel.Fastest,
                         leaveOpen: false))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, overwrite: true);
        TryDeleteCacheFile(staleCompressedPath, path);
        TryDeleteCacheFile(Path.Combine(_cacheDirectory, $"{prefix}.json"), path);
    }

    private async Task<PackageIndexManifest?> LoadManifestAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(ManifestPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(ManifestPath);
            return await JsonSerializer.DeserializeAsync<PackageIndexManifest>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task SaveManifestAsync(
        PackageIndexManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var temporaryPath = ManifestPath + $".{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         81920,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    manifest,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, ManifestPath, overwrite: true);
    }

    private static void TryDeleteCacheFile(string? candidate, string currentPath)
    {
        if (string.IsNullOrEmpty(candidate) ||
            candidate.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(candidate);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private Dictionary<string, CompressedCacheEntry> LoadCompressedCacheEntries()
    {
        var entries = new Dictionary<string, CompressedCacheEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "*.json.gz"))
        {
            var parts = Path.GetFileName(path).Split('.');
            if (parts.Length != 7 ||
                !long.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var length) ||
                !long.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var ticks) ||
                !int.TryParse(parts[3], out var assets))
            {
                continue;
            }

            entries[parts[0]] = new CompressedCacheEntry(
                path,
                length,
                ticks,
                assets,
                parts[4].Equals("E", StringComparison.OrdinalIgnoreCase));
        }

        // A completed compressed entry supersedes the legacy JSON file. Cleaning
        // these once at startup avoids duplicate search results and migration peaks.
        foreach (var pair in entries)
        {
            TryDeleteCacheFile(
                Path.Combine(_cacheDirectory, $"{pair.Key}.json"),
                pair.Value.Path);
        }

        return entries;
    }

    private IEnumerable<string> EnumerateCacheFiles() =>
        Directory.EnumerateFiles(_cacheDirectory, "*.json.gz")
            .Where(path => !Path.GetFileName(path).Equals(
                SearchRoutingFileName,
                StringComparison.OrdinalIgnoreCase))
            .Concat(Directory.EnumerateFiles(_cacheDirectory, "*.json")
                .Where(path => !Path.GetFileName(path).Equals(
                    ManifestFileName,
                    StringComparison.OrdinalIgnoreCase)));

    private IEnumerable<FileInfo> EnumerateCacheFileInfos()
    {
        var directory = new DirectoryInfo(_cacheDirectory);
        return directory.EnumerateFiles("*.json.gz")
            .Where(file => !file.Name.Equals(SearchRoutingFileName, StringComparison.OrdinalIgnoreCase))
            .Concat(directory.EnumerateFiles("*.json")
                .Where(file => !file.Name.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool TryParseCompressedCacheFileName(
        string path,
        out int assetCount,
        out bool hasError)
    {
        assetCount = 0;
        hasError = false;
        var parts = Path.GetFileName(path).Split('.');
        if (parts.Length != 7 || !int.TryParse(parts[3], out assetCount))
        {
            return false;
        }

        hasError = parts[4].Equals("E", StringComparison.OrdinalIgnoreCase);
        return parts[4].Equals("O", StringComparison.OrdinalIgnoreCase) || hasError;
    }

    private static Stream OpenCacheRead(string path)
    {
        var stream = File.OpenRead(path);
        return path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(stream, CompressionMode.Decompress)
            : stream;
    }

    private string GetCachePrefix(string packagePath)
    {
        var normalized = Path.GetFullPath(packagePath).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    private static BackendIdentity CreateBackendIdentity(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var file = new FileInfo(fullPath);
        using var stream = File.OpenRead(fullPath);
        return new BackendIdentity(
            fullPath,
            Convert.ToHexString(SHA256.HashData(stream)),
            file.Length,
            file.LastWriteTimeUtc.Ticks);
    }

    private static bool IsCompatible(
        PackageIndexManifest manifest,
        BackendIdentity backend) =>
        manifest.SchemaVersion == CurrentSchemaVersion &&
        manifest.BackendProfile.Equals(BackendProfile, StringComparison.Ordinal) &&
        manifest.BackendSha256.Equals(backend.Sha256, StringComparison.OrdinalIgnoreCase);

    private sealed record CachedPackageInfo(int AssetCount);

    private sealed record CompressedCacheEntry(
        string Path,
        long FileLength,
        long LastWriteUtcTicks,
        int AssetCount,
        bool HasError);

    private sealed record BackendIdentity(
        string Path,
        string Sha256,
        long Length,
        long LastWriteUtcTicks);
}
