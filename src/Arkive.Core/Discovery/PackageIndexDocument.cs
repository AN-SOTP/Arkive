using Arkive.Core.Assets;

namespace Arkive.Core.Discovery;

public sealed record PackageIndexDocument(
    string SourcePath,
    string PhysicalPackageName,
    string PackageAlias,
    long FileLength,
    long LastWriteUtcTicks,
    DateTimeOffset IndexedAt,
    IReadOnlyList<IndexedAsset> Assets,
    string? Error);

public sealed record IndexedAsset(
    int ExportIndex,
    AssetKind Kind,
    string ClassName,
    string ObjectName,
    long SerialSize,
    long SerialOffset);

public sealed record PackageIndexProgress(
    int CompletedPackages,
    int TotalPackages,
    int CachedPackages,
    int FailedPackages,
    string CurrentPackage);

public sealed record PackageIndexReport(
    string RootPath,
    int TotalPackages,
    int IndexedPackages,
    int CachedPackages,
    int FailedPackages,
    int IndexedAssets,
    TimeSpan Duration);

public sealed record PackageIndexMatch(
    string SourcePath,
    string PackageAlias,
    string PhysicalPackageName,
    int PackageAssetCount,
    int ExportIndex,
    AssetKind Kind,
    string ClassName,
    string ObjectName,
    long SerialSize,
    long SerialOffset,
    IReadOnlyList<GameAssetAssociation> GameMetadata);

public sealed record PackageIndexSearchResult(
    IReadOnlyList<PackageIndexMatch> Matches,
    int TotalMatches,
    int PackageCount,
    int FailedPackageCount,
    bool WasCancelled = false,
    bool UsedRoutingIndex = false,
    bool RoutingIndexBuilt = false,
    int RoutedPackageCount = 0,
    double RoutingPreparationSeconds = 0);

public enum PackageIndexSearchScope
{
    AllFields,
    ObjectName,
    InGameName,
    ObjectAndInGameName,
}

public sealed record PackageSearchRoutingProgress(
    int CompletedPackages,
    int TotalPackages,
    string CurrentCacheFile);

public sealed record PackageIndexManifest(
    int SchemaVersion,
    string BackendProfile,
    string BackendPath,
    string BackendSha256,
    long BackendLength,
    long BackendLastWriteUtcTicks,
    string LastRootPath,
    DateTimeOffset CompletedAt,
    int PackageCount,
    int SuccessfulPackageCount,
    int FailedPackageCount,
    long AssetCount);

public sealed record PackageIndexFailure(
    string SourcePath,
    string PhysicalPackageName,
    long FileLength,
    string Error);

public sealed record PackageIndexHealth(
    string CacheDirectory,
    long CacheBytes,
    int CachedDocumentCount,
    int SuccessfulDocumentCount,
    int FailedDocumentCount,
    int CorruptDocumentCount,
    long IndexedAssetCount,
    PackageIndexManifest? Manifest,
    bool ProvenanceMatches,
    string ProvenanceStatus,
    IReadOnlyList<PackageIndexFailure> Failures);
