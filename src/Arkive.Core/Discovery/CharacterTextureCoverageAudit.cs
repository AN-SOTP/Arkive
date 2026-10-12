using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Arkive.Core.Assets;
using Arkive.Core.Archives;
using Arkive.Core.Inspection;
using Arkive.Core.Textures;

namespace Arkive.Core.Discovery;

public enum CharacterTextureCoverageStatus
{
    NotReconnoitred,
    NativeExportSupported,
    NativeExportGateNotSatisfied,
    ReconnaissanceFailed,
    NativeAssetNotFound,
}

public sealed record CharacterTextureCoverageItem(
    long PlayerClassId,
    string PlayerClassName,
    string PlayerCode,
    string LookInfoReference,
    string PackageAlias,
    string PhysicalPackageName,
    string SourcePath,
    int ExportIndex,
    string ObjectName,
    long SerialSize,
    long SerialOffset,
    string TextureRole,
    string Evidence,
    CharacterTextureCoverageStatus CoverageStatus,
    string? PixelFormat,
    string CoverageDetail);

public sealed record CharacterTextureCoverageReport(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    string Scope,
    string MetadataPath,
    string IndexCacheDirectory,
    string LibraryRoot,
    int PlayerClassCount,
    int AppearancePackageCount,
    int TextureCount,
    int ReconnoitredTextureCount,
    int NativeExportSupportedCount,
    int NativeExportGateNotSatisfiedCount,
    int FailedCount,
    IReadOnlyList<CharacterTextureCoverageItem> Textures,
    double DurationSeconds,
    IReadOnlyList<string> Notes);

public sealed record CharacterTextureCoverageRequest(
    GameAssetMetadataIndex Metadata,
    string IndexCacheDirectory,
    string LibraryRoot,
    bool RunNativeReconnaissance = false,
    int? ReconnaissanceLimit = null);

/// <summary>
/// Builds an evidence-labelled inventory of playable-character textures. PC.LookInfo
/// supplies the authoritative class code; package-family matching is deliberately
/// limited to appearance packages and never promotes an NPC row into PC scope.
/// </summary>
public sealed class CharacterTextureCoverageAuditService
{
    public const int CurrentSchemaVersion = 1;
    private const string RoutingFileName = "search-routing-v1.json.gz";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IChunkProtectionProvider _protectionProvider;

    public CharacterTextureCoverageAuditService(IChunkProtectionProvider? protectionProvider = null)
    {
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
    }

    public async Task<CharacterTextureCoverageReport> AuditAsync(
        CharacterTextureCoverageRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var cacheDirectory = Path.GetFullPath(request.IndexCacheDirectory);
        var libraryRoot = Path.GetFullPath(request.LibraryRoot);
        var routingPath = Path.Combine(cacheDirectory, RoutingFileName);
        if (!File.Exists(routingPath))
        {
            throw new FileNotFoundException(
                "The package search routing index is missing. Search by in-game name once or rebuild the library index first.",
                routingPath);
        }

        var seeds = request.Metadata.Associations
            .Where(item => item.EntityType.Equals("PC", StringComparison.OrdinalIgnoreCase) &&
                           item.Field.Equals("LookInfo", StringComparison.OrdinalIgnoreCase) &&
                           item.ObjectName.StartsWith("PC_", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(item => item.EntityId)
            .OrderBy(item => item.EntityId)
            .ToArray();
        if (seeds.Length == 0)
        {
            throw new InvalidDataException(
                "The metadata index has no PC.LookInfo associations. Rebuild it with the current build_game_asset_metadata.py first.");
        }

        var seedCodes = seeds
            .Select(seed => new PlayerSeed(seed, seed.ObjectName.ToLowerInvariant()))
            .OrderByDescending(seed => seed.Code.Length)
            .ToArray();
        await using var routingFile = File.OpenRead(routingPath);
        await using var routingGzip = new GZipStream(routingFile, CompressionMode.Decompress);
        var routing = await JsonSerializer.DeserializeAsync<PackageSearchRoutingDocument>(
            routingGzip,
            JsonOptions,
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("The package search routing index is empty.");
        if (!routing.LibraryRoot.Equals(libraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The routing index belongs to '{routing.LibraryRoot}', not '{libraryRoot}'.");
        }

        var packageSeeds = routing.Packages
            .Select(package => (Package: package, Seed: MatchAppearancePackage(package.PackageAlias, seedCodes)))
            .Where(item => item.Seed is not null && !item.Package.HasError)
            .Select(item => (item.Package, Seed: item.Seed!))
            .ToArray();
        progress?.Report(
            $"Player texture inventory: {seeds.Length:N0} PC rows, {packageSeeds.Length:N0} appearance packages.");

        var items = new List<CharacterTextureCoverageItem>();
        var completedPackages = 0;
        foreach (var (package, seed) in packageSeeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cachePath = Path.Combine(cacheDirectory, package.CacheFileName);
            PackageIndexDocument? document;
            await using (var cacheFile = File.OpenRead(cachePath))
            await using (var cacheGzip = new GZipStream(cacheFile, CompressionMode.Decompress))
            {
                document = await JsonSerializer.DeserializeAsync<PackageIndexDocument>(
                    cacheGzip,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            if (document is { Error: null })
            {
                foreach (var asset in document.Assets.Where(asset =>
                             asset.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase)))
                {
                    items.Add(CreateItem(seed, document, asset));
                }
            }

            completedPackages++;
            if (completedPackages == packageSeeds.Length || completedPackages % 100 == 0)
            {
                progress?.Report(
                    $"Player texture inventory: {completedPackages:N0}/{packageSeeds.Length:N0} packages, " +
                    $"{items.Count:N0} Texture2D assets.");
            }
        }

        if (request.RunNativeReconnaissance)
        {
            var limit = request.ReconnaissanceLimit is > 0
                ? Math.Min(request.ReconnaissanceLimit.Value, items.Count)
                : items.Count;
            var grouped = items.Take(limit)
                .Select((item, index) => (Item: item, Index: index))
                .GroupBy(entry => entry.Item.SourcePath, StringComparer.OrdinalIgnoreCase);
            var processed = 0;
            foreach (var packageItems in grouped)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyDictionary<string, AssetCatalogEntry>? nativeAssets = null;
                string? catalogueError = null;
                try
                {
                    nativeAssets = new AssetCatalogReader(protectionProvider: _protectionProvider)
                        .Read(packageItems.Key)
                        .Assets
                        .Where(asset => asset.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                        .GroupBy(asset => asset.ObjectName, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidDataException or NotSupportedException)
                {
                    catalogueError = exception.Message;
                }

                foreach (var entry in packageItems)
                {
                    items[entry.Index] = await ReconnoitreAsync(
                        entry.Item,
                        nativeAssets,
                        catalogueError,
                        cancellationToken).ConfigureAwait(false);
                    processed++;
                    if (processed == limit || processed % 25 == 0)
                    {
                        progress?.Report($"Native format audit: {processed:N0}/{limit:N0} textures.");
                    }
                }
            }
        }

        watch.Stop();
        var reconnoitred = items.Count(item =>
            item.CoverageStatus != CharacterTextureCoverageStatus.NotReconnoitred);
        return new CharacterTextureCoverageReport(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            "Western/global playable characters from EFTable_PC.LookInfo; NPC-only rows excluded",
            request.Metadata.SourcePath,
            cacheDirectory,
            libraryRoot,
            seeds.Length,
            packageSeeds.Length,
            items.Count,
            reconnoitred,
            items.Count(item => item.CoverageStatus == CharacterTextureCoverageStatus.NativeExportSupported),
            items.Count(item => item.CoverageStatus == CharacterTextureCoverageStatus.NativeExportGateNotSatisfied),
            items.Count(item => item.CoverageStatus is CharacterTextureCoverageStatus.ReconnaissanceFailed or
                CharacterTextureCoverageStatus.NativeAssetNotFound),
            items.OrderBy(item => item.PlayerClassId)
                .ThenBy(item => item.PackageAlias, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            watch.Elapsed.TotalSeconds,
            [
                "PC.LookInfo is direct DB evidence for the playable class code; individual textures are strong package-family inferences.",
                "Only aliases beginning pc_<class-code> or facecust_pc_<class-code> are included; FX and NPC packages are excluded.",
                request.RunNativeReconnaissance
                    ? "Native support requires the same exact format/property/mip gates used by Arkive's DDS exporter."
                    : "Format coverage was not run; use native reconnaissance to measure export support.",
            ]);
    }

    private async Task<CharacterTextureCoverageItem> ReconnoitreAsync(
        CharacterTextureCoverageItem item,
        IReadOnlyDictionary<string, AssetCatalogEntry>? nativeAssets,
        string? catalogueError,
        CancellationToken cancellationToken)
    {
        if (nativeAssets is null)
        {
            return item with
            {
                CoverageStatus = CharacterTextureCoverageStatus.ReconnaissanceFailed,
                CoverageDetail = "Native package catalogue failed: " + catalogueError,
            };
        }

        if (!nativeAssets.TryGetValue(item.ObjectName, out var asset))
        {
            return item with
            {
                CoverageStatus = CharacterTextureCoverageStatus.NativeAssetNotFound,
                CoverageDetail = "Texture2D was listed by UModel but not found in Arkive's native catalogue.",
            };
        }

        try
        {
            var report = await new TextureReconnaissanceService(protectionProvider: _protectionProvider)
                .InspectAsync(new TextureReconnaissanceRequest(
                    item.SourcePath,
                    item.PackageAlias,
                    asset.ExportIndex,
                    asset.Kind,
                    asset.ClassName,
                    asset.ObjectName,
                    asset.SerialOffset,
                    asset.SerialSize), cancellationToken).ConfigureAwait(false);
            var formatProperty = report.Properties.SingleOrDefault(property =>
                property.Name.Equals("Format", StringComparison.OrdinalIgnoreCase));
            var format = formatProperty?.ValuePreview;
            var supported = NativeTextureExportService.TryValidateReconnaissanceGate(
                report,
                out var profile,
                out var gateDetail);
            return item with
            {
                ExportIndex = asset.ExportIndex,
                SerialSize = asset.SerialSize,
                SerialOffset = asset.SerialOffset,
                CoverageStatus = supported
                    ? CharacterTextureCoverageStatus.NativeExportSupported
                    : CharacterTextureCoverageStatus.NativeExportGateNotSatisfied,
                PixelFormat = format,
                CoverageDetail = supported
                    ? gateDetail
                    : $"{gateDetail} Observed format={format ?? "unknown"}.",
            };
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            return item with
            {
                CoverageStatus = CharacterTextureCoverageStatus.ReconnaissanceFailed,
                CoverageDetail = exception.Message,
            };
        }
    }

    private static CharacterTextureCoverageItem CreateItem(
        PlayerSeed seed,
        PackageIndexDocument document,
        IndexedAsset asset) => new(
        seed.Association.EntityId,
        seed.Association.DisplayName,
        seed.Association.ObjectName,
        seed.Association.AssetReference,
        document.PackageAlias,
        document.PhysicalPackageName,
        document.SourcePath,
        asset.ExportIndex,
        asset.ObjectName,
        asset.SerialSize,
        asset.SerialOffset,
        ClassifyTextureRole(asset.ObjectName),
        $"PC.LookInfo={seed.Association.AssetReference}; appearance package family '{document.PackageAlias}'",
        CharacterTextureCoverageStatus.NotReconnoitred,
        null,
        "Inventory only; native format reconnaissance not run.");

    private static PlayerSeed? MatchAppearancePackage(
        string packageAlias,
        IReadOnlyList<PlayerSeed> seeds)
    {
        var alias = packageAlias.ToLowerInvariant();
        foreach (var seed in seeds)
        {
            if (alias.Equals(seed.Code, StringComparison.Ordinal) ||
                alias.StartsWith(seed.Code + "_", StringComparison.Ordinal) ||
                alias.Equals("facecust_" + seed.Code, StringComparison.Ordinal) ||
                alias.StartsWith("facecust_" + seed.Code + "_", StringComparison.Ordinal))
            {
                return seed;
            }
        }

        return null;
    }

    private static string ClassifyTextureRole(string objectName)
    {
        var name = objectName.ToLowerInvariant();
        if (name.EndsWith("_n", StringComparison.Ordinal)) return "Normal";
        if (name.EndsWith("_d", StringComparison.Ordinal)) return "Diffuse/Base color";
        if (name.EndsWith("_e", StringComparison.Ordinal)) return "Emissive";
        if (name.EndsWith("_orm", StringComparison.Ordinal)) return "Occlusion/Roughness/Metallic";
        if (name.EndsWith("_cm", StringComparison.Ordinal)) return "Color mask";
        if (name.EndsWith("_m", StringComparison.Ordinal)) return "Mask/Metallic";
        if (name.EndsWith("_s", StringComparison.Ordinal)) return "Specular/packed";
        return "Other/customization";
    }

    private sealed record PlayerSeed(GameAssetAssociation Association, string Code);
}
