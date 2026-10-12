using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arkive.Core.Assets;
using Arkive.Core.External;

namespace Arkive.Core.Discovery;

public sealed record GameAssetDependencyProgress(
    int Completed,
    int Total,
    int Cached,
    int Failed,
    string CurrentAsset);

public sealed record GameAssetDependencyBuildReport(
    string OutputPath,
    string? Query,
    int SeedAssociationCount,
    int SourceAssetCount,
    int ProbedAssetCount,
    int CachedAssetCount,
    int FailedAssetCount,
    int DependencyAssociationCount,
    TimeSpan Duration);

public sealed partial class GameAssetDependencyResolver
{
    private const string DependencyProfile = "umodel-selected-mesh-material-texture-v4";

    private readonly PackageIndexStore _packageIndexStore;
    private readonly UmodelBridge _bridge;

    public GameAssetDependencyResolver(PackageIndexStore packageIndexStore, UmodelBridge bridge)
    {
        _packageIndexStore = packageIndexStore ?? throw new ArgumentNullException(nameof(packageIndexStore));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public Task<GameAssetDependencyBuildReport> BuildAsync(
        string metadataPath,
        string packageRoot,
        string outputPath,
        string scratchRoot,
        string? query = null,
        int maxParallelism = 2,
        IProgress<GameAssetDependencyProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        BuildManyAsync(
            metadataPath,
            packageRoot,
            outputPath,
            scratchRoot,
            string.IsNullOrWhiteSpace(query) ? null : [query],
            maxParallelism,
            progress,
            cancellationToken);

    public async Task<GameAssetDependencyBuildReport> BuildManyAsync(
        string metadataPath,
        string packageRoot,
        string outputPath,
        string scratchRoot,
        IReadOnlyList<string>? queries,
        int maxParallelism = 2,
        IProgress<GameAssetDependencyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParallelism);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var metadata = GameAssetMetadataIndex.Load(metadataPath);
        var normalizedQueries = (queries ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var queryLabel = normalizedQueries.Length == 0
            ? null
            : string.Join(" | ", normalizedQueries);
        var allSeeds = metadata.Associations
            .Where(IsDependencySeed)
            .ToArray();
        var selectedSeeds = normalizedQueries.Length == 0
            ? allSeeds
            : allSeeds.Where(item => normalizedQueries.Any(query =>
                AssetSearch.MatchesDisplayNames(query, [item.DisplayName]))).ToArray();
        var aliases = selectedSeeds
            .SelectMany(item => GameAssetMetadataIndex.PackageNameVariants(item.PackageName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var documents = await _packageIndexStore.LoadPackagesByAliasAsync(
            packageRoot,
            aliases,
            metadata,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var documentsByAlias = documents
            .GroupBy(
                item => GameAssetMetadataIndex.NormalizePackageName(item.PackageAlias),
                StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var sources = selectedSeeds
            .SelectMany(seed => ResolveSeedSources(seed, documentsByAlias))
            .GroupBy(item => SourceKey(item.Document.SourcePath, item.Asset.ObjectName), StringComparer.OrdinalIgnoreCase)
            .Select(group => new DependencySource(
                group.First().Document,
                group.First().Asset,
                group.SelectMany(item => item.Associations)
                    .DistinctBy(AssociationIdentity)
                    .ToArray()))
            .ToArray();

        var existing = GameAssetDependencySidecar.TryLoad(outputPath);
        var backend = new FileInfo(_bridge.ExecutablePath);
        var probes = new ConcurrentDictionary<string, GameAssetDependencyProbe>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var probe in existing?.Probes ?? [])
        {
            probes[SourceKey(probe.SourcePackagePath, probe.SourceObjectName)] = probe;
        }

        Directory.CreateDirectory(scratchRoot);
        var completed = 0;
        var cached = 0;
        var failed = 0;
        var probed = 0;
        await Parallel.ForEachAsync(
            sources,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelism,
                CancellationToken = cancellationToken,
            },
            async (source, token) =>
            {
                var key = SourceKey(source.Document.SourcePath, source.Asset.ObjectName);
                var sourceFile = new FileInfo(source.Document.SourcePath);
                if (probes.TryGetValue(key, out var prior) &&
                    IsFresh(prior, sourceFile, backend))
                {
                    Interlocked.Increment(ref cached);
                    ReportProgress(source.Asset.ObjectName);
                    return;
                }

                Interlocked.Increment(ref probed);
                var probeDirectory = Path.Combine(
                    Path.GetFullPath(scratchRoot),
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24]);
                try
                {
                    if (Directory.Exists(probeDirectory))
                    {
                        Directory.Delete(probeDirectory, recursive: true);
                    }

                    var export = await _bridge.ExportAsync(
                        source.Document.SourcePath,
                        new UmodelAssetEntry(
                            source.Asset.ExportIndex,
                            source.Asset.SerialOffset,
                            source.Asset.SerialSize,
                            source.Asset.ClassName,
                            source.Asset.ObjectName),
                        probeDirectory,
                        allowPackageWideFallback: false,
                        token).ConfigureAwait(false);
                    var targets = ParseExportTargets(
                        probeDirectory,
                        source.Asset.ObjectName,
                        export.OutputFiles);
                    probes[key] = CreateProbe(source, sourceFile, backend, targets, error: null);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or UmodelBridgeException)
                {
                    Interlocked.Increment(ref failed);
                    probes[key] = CreateProbe(source, sourceFile, backend, [], exception.Message);
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(probeDirectory))
                        {
                            Directory.Delete(probeDirectory, recursive: true);
                        }
                    }
                    catch (IOException)
                    {
                        // Scratch cleanup is best effort and never changes a game package.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Scratch cleanup is best effort and never changes a game package.
                    }

                    ReportProgress(source.Asset.ObjectName);
                }
            }).ConfigureAwait(false);

        var associationsBySource = allSeeds
            .SelectMany(seed => ResolveSeedSourceKeys(seed, documentsByAlias))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Association)
                    .DistinctBy(AssociationIdentity)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var rebuiltAssociations = probes.Values
            .Where(probe => probe.Error is null &&
                            associationsBySource.ContainsKey(
                                SourceKey(probe.SourcePackagePath, probe.SourceObjectName)))
            .SelectMany(probe => CreateAssociations(
                associationsBySource[SourceKey(probe.SourcePackagePath, probe.SourceObjectName)],
                probe))
            .ToArray();
        var preservedAssociations = normalizedQueries.Length == 0
            ? []
            : (existing?.Associations ?? [])
                .Where(item => !normalizedQueries.Any(query =>
                    AssetSearch.MatchesDisplayNames(query, [item.DisplayName])))
                .ToArray();
        var dependencyAssociations = preservedAssociations
            .Concat(rebuiltAssociations)
            .DistinctBy(AssociationIdentity)
            .OrderBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var document = new GameAssetDependencyDocument(
            GameAssetDependencySidecar.CurrentVersion,
            Path.GetFullPath(metadataPath),
            DateTimeOffset.UtcNow,
            dependencyAssociations,
            probes.Values
                .OrderBy(item => item.SourcePackageName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SourceObjectName, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        await GameAssetDependencySidecar.SaveAsync(
            outputPath,
            document,
            cancellationToken).ConfigureAwait(false);
        watch.Stop();
        return new GameAssetDependencyBuildReport(
            Path.GetFullPath(outputPath),
            queryLabel,
            selectedSeeds.Length,
            sources.Length,
            probed,
            cached,
            failed,
            dependencyAssociations.Length,
            watch.Elapsed);

        void ReportProgress(string current)
        {
            var count = Interlocked.Increment(ref completed);
            progress?.Report(new GameAssetDependencyProgress(
                count,
                sources.Length,
                Volatile.Read(ref cached),
                Volatile.Read(ref failed),
                current));
        }
    }

    private static IEnumerable<DependencySource> ResolveSources(
        string packageName,
        string objectName,
        IReadOnlyList<GameAssetAssociation> associations,
        IReadOnlyDictionary<string, PackageIndexDocument[]> documentsByAlias)
    {
        if (!documentsByAlias.TryGetValue(packageName, out var documents))
        {
            yield break;
        }

        foreach (var document in documents)
        {
            var assets = document.Assets
                .Where(asset =>
                    asset.Kind is AssetKind.SkeletalMesh or AssetKind.StaticMesh &&
                    GameAssetMetadataIndex.IsAssetFamilyMember(
                        GameAssetMetadataIndex.NormalizeObjectName(asset.ObjectName),
                        objectName))
                .OrderBy(asset => asset.ObjectName.Length)
                .ToArray();
            foreach (var asset in assets)
            {
                yield return new DependencySource(document, asset, associations);
            }
        }
    }

    private static IEnumerable<DependencySource> ResolveSeedSources(
        GameAssetAssociation seed,
        IReadOnlyDictionary<string, PackageIndexDocument[]> documentsByAlias)
    {
        var objectName = GameAssetMetadataIndex.NormalizeObjectName(seed.ObjectName);
        foreach (var package in GameAssetMetadataIndex.PackageNameVariants(seed.PackageName)
                     .Distinct(StringComparer.Ordinal))
        {
            foreach (var source in ResolveSources(package, objectName, [seed], documentsByAlias))
            {
                yield return source;
            }
        }
    }

    private static IEnumerable<(string Key, GameAssetAssociation Association)> ResolveSeedSourceKeys(
        GameAssetAssociation seed,
        IReadOnlyDictionary<string, PackageIndexDocument[]> documentsByAlias)
    {
        foreach (var source in ResolveSeedSources(seed, documentsByAlias))
        {
            yield return (SourceKey(source.Document.SourcePath, source.Asset.ObjectName), seed);
        }
    }

    private static bool IsDependencySeed(GameAssetAssociation association)
    {
        if (association.Relation.Equals(
                GameAssetAssociationRelation.DeterministicItemAsset,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (association.Relation.Equals(
                GameAssetAssociationRelation.DeterministicModelAsset,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!association.Relation.Equals(
                GameAssetAssociationRelation.DirectDatabaseReference,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (association.EntityType.Equals("Item", StringComparison.OrdinalIgnoreCase))
        {
            var package = GameAssetMetadataIndex.NormalizePackageName(association.PackageName);
            return association.Field.Equals("InstrumentModel", StringComparison.OrdinalIgnoreCase) ||
                   (association.Field.Equals("Model", StringComparison.OrdinalIgnoreCase) &&
                    package.StartsWith("EFDLITEM_WP_", StringComparison.Ordinal));
        }

        return association.Field.Equals("Model", StringComparison.OrdinalIgnoreCase) ||
               association.Field.Equals("LookInfo", StringComparison.OrdinalIgnoreCase) ||
               association.Field.Equals("InstrumentModel", StringComparison.OrdinalIgnoreCase) ||
               association.Field.Equals("RealmTemporaryModel", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the exact material slots used by one UModel-exported mesh and the
    /// Texture2D objects referenced by those materials. Unrelated package exports
    /// are deliberately excluded even when they share the same filename prefix.
    /// </summary>
    public static IReadOnlyList<GameAssetDependencyTarget> ParseExportTargets(
        string outputDirectory,
        string sourceObjectName,
        IReadOnlyList<string> outputFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceObjectName);
        ArgumentNullException.ThrowIfNull(outputFiles);
        var files = outputFiles
            .Select(path => DescribeOutput(outputDirectory, path))
            .Where(item => item is not null)
            .Cast<DependencyOutput>()
            .ToArray();
        var directMaterialNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gltf in outputFiles.Where(path =>
                     Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase) &&
                     Path.GetFileNameWithoutExtension(path).Equals(
                         sourceObjectName,
                         StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var stream = File.OpenRead(gltf);
                using var json = JsonDocument.Parse(stream);
                if (json.RootElement.TryGetProperty("materials", out var materials))
                {
                    foreach (var material in materials.EnumerateArray())
                    {
                        if (material.TryGetProperty("name", out var name) &&
                            !string.IsNullOrWhiteSpace(name.GetString()))
                        {
                            directMaterialNames.Add(name.GetString()!);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // A malformed optional glTF cannot make package inspection unsafe.
            }
        }

        var directMaterials = files
            .Where(item => IsMaterialClass(item.ClassName) &&
                           directMaterialNames.Contains(item.ObjectName))
            .ToArray();
        var textureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in directMaterials.Where(item =>
                     item.Path.EndsWith(".props.txt", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var properties = File.ReadAllText(material.Path);
                foreach (Match match in TextureReferenceRegex().Matches(properties))
                {
                    textureNames.Add(GameAssetMetadataIndex.NormalizeObjectName(
                        match.Groups["reference"].Value));
                }
            }
            catch (IOException)
            {
                // The material itself remains useful even if its text sidecar is unreadable.
            }
        }

        return directMaterials
            .Select(item => new GameAssetDependencyTarget(
                item.PackageName,
                item.ObjectName,
                item.ClassName))
            .Concat(files
                .Where(item => item.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
                               textureNames.Contains(item.ObjectName))
                .Select(item => new GameAssetDependencyTarget(
                    item.PackageName,
                    item.ObjectName,
                    item.ClassName)))
            .DistinctBy(item => new
            {
                Package = item.PackageName.ToUpperInvariant(),
                Object = item.ObjectName.ToUpperInvariant(),
                Class = item.ClassName.ToUpperInvariant(),
            })
            .OrderBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DependencyOutput? DescribeOutput(string outputDirectory, string path)
    {
        var relative = Path.GetRelativePath(outputDirectory, path);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length < 3 || IsSharedEnginePackage(parts[0]))
        {
            return null;
        }

        var className = parts[1];
        if (!className.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
            !IsMaterialClass(className))
        {
            return null;
        }

        var fileName = Path.GetFileName(path);
        var objectName = fileName.EndsWith(".props.txt", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".props.txt".Length]
            : Path.GetFileNameWithoutExtension(fileName);
        return new DependencyOutput(path, parts[0], objectName, className);
    }

    private static bool IsMaterialClass(string className) =>
        className.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("Material3", StringComparison.OrdinalIgnoreCase);

    private static bool IsSharedEnginePackage(string packageName) =>
        packageName.StartsWith("EFMASTER_", StringComparison.OrdinalIgnoreCase) ||
        packageName.StartsWith("EFBASEMATERIAL_", StringComparison.OrdinalIgnoreCase) ||
        packageName.Equals("ENGINE", StringComparison.OrdinalIgnoreCase);

    private static GameAssetDependencyProbe CreateProbe(
        DependencySource source,
        FileInfo sourceFile,
        FileInfo backend,
        IReadOnlyList<GameAssetDependencyTarget> targets,
        string? error) =>
        new(
            sourceFile.FullName,
            sourceFile.Length,
            sourceFile.LastWriteTimeUtc.Ticks,
            source.Document.PackageAlias,
            source.Asset.ObjectName,
            DependencyProfile,
            backend.FullName,
            backend.Length,
            backend.LastWriteTimeUtc.Ticks,
            DateTimeOffset.UtcNow,
            targets,
            error);

    private static bool IsFresh(
        GameAssetDependencyProbe probe,
        FileInfo source,
        FileInfo backend) =>
        probe.SourcePackagePath.Equals(source.FullName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(probe.DependencyProfile, DependencyProfile, StringComparison.Ordinal) &&
        probe.SourceFileLength == source.Length &&
        probe.SourceLastWriteUtcTicks == source.LastWriteTimeUtc.Ticks &&
        probe.BackendPath.Equals(backend.FullName, StringComparison.OrdinalIgnoreCase) &&
        probe.BackendFileLength == backend.Length &&
        probe.BackendLastWriteUtcTicks == backend.LastWriteTimeUtc.Ticks;

    private static IEnumerable<GameAssetAssociation> CreateAssociations(
        IReadOnlyList<GameAssetAssociation> seeds,
        GameAssetDependencyProbe probe)
    {
        foreach (var seed in seeds)
        foreach (var target in probe.Targets)
        {
            yield return seed with
            {
                Field = $"Dependency.{target.ClassName}",
                AssetReference = $"{target.PackageName}.{target.ObjectName}",
                PackageName = target.PackageName,
                ObjectName = target.ObjectName,
                Confidence = GameAssetAssociationConfidence.Verified,
                Relation = GameAssetAssociationRelation.VerifiedSerializedDependency,
                SourceAssetReference = seed.AssetReference,
            };
        }
    }

    private static object AssociationIdentity(GameAssetAssociation item) => new
    {
        Type = item.EntityType.ToUpperInvariant(),
        item.EntityId,
        item.DisplayName,
        item.Field,
        Package = item.PackageName.ToUpperInvariant(),
        Object = item.ObjectName.ToUpperInvariant(),
        item.Relation,
    };

    private static string SourceKey(string path, string objectName) =>
        Path.GetFullPath(path) + "\0" + objectName;

    private sealed record DependencySource(
        PackageIndexDocument Document,
        IndexedAsset Asset,
        IReadOnlyList<GameAssetAssociation> Associations);

    private sealed record DependencyOutput(
        string Path,
        string PackageName,
        string ObjectName,
        string ClassName);

    [GeneratedRegex(@"Texture2D'(?<reference>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex TextureReferenceRegex();
}
