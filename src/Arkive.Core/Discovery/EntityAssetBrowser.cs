using System.IO.Compression;
using System.Text.Json;
using Arkive.Core.Assets;

namespace Arkive.Core.Discovery;

public enum EntityAssetEvidence
{
    DirectDatabaseReference,
    DatabaseAssetFamily,
    AppearancePackageFamily,
    CustomizationCandidate,
}

public sealed record EntityAssetMatch(
    PackageIndexMatch Asset,
    EntityAssetEvidence Evidence,
    AssetRelationshipConfidence Confidence,
    string Reason);

public sealed record EntityAssetBrowseResult(
    GameEntityRecord Entity,
    IReadOnlyList<EntityAssetMatch> Matches,
    int ScannedPackageCount,
    bool RoutingIndexBuilt,
    double DurationSeconds);

public sealed partial class PackageIndexStore
{
    public async Task<EntityAssetBrowseResult> BrowseEntityAssetsAsync(
        string rootPath,
        GameEntityRecord entity,
        GameAssetMetadataIndex metadata,
        IReadOnlyList<string>? customizationCandidates = null,
        int resultLimit = 5_000,
        IProgress<PackageSearchRoutingProgress>? routingProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resultLimit);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var preparation = await PrepareSearchRoutingAsync(
            rootPath,
            metadata,
            routingProgress,
            cancellationToken).ConfigureAwait(false);

        var directAssociations = entity.Associations.Count > 0
            ? entity.Associations
            : metadata.FindEntityAssociations(entity.EntityType, entity.EntityId);
        var associations = entity.BaseEntityId is { } baseId
            ? metadata.FindEntityAssociations(entity.EntityType, baseId)
                .Concat(directAssociations)
                .DistinctBy(item => new
                {
                    item.EntityType,
                    item.EntityId,
                    item.Field,
                    item.AssetReference,
                })
                .ToArray()
            : directAssociations;
        var packageAnchors = associations
            .SelectMany(item => GameAssetMetadataIndex.PackageNameVariants(item.PackageName))
            .Where(name => name.Length > 0)
            .ToList();
        if (entity.Kind == GameEntityKind.PlayableCharacter)
        {
            packageAnchors.AddRange(associations
                .Where(item => item.Field.Equals("LookInfo", StringComparison.OrdinalIgnoreCase))
                .Select(item => GameAssetMetadataIndex.NormalizeObjectName(item.ObjectName)));
        }

        var cacheFiles = preparation.Index.ResolvePackageFamilies(packageAnchors, _cacheDirectory);
        var candidateNames = (customizationCandidates ?? [])
            .Select(GameAssetMetadataIndex.NormalizeObjectName)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var referenceNames = associations
            .Select(item => GameAssetMetadataIndex.NormalizeObjectName(item.ObjectName))
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var matches = new List<EntityAssetMatch>();
        foreach (var cacheFile in cacheFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageIndexDocument? document;
            try
            {
                await using var stream = OpenCacheRead(cacheFile);
                document = await JsonSerializer.DeserializeAsync<PackageIndexDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException or IOException)
            {
                continue;
            }

            if (document is null || document.Error is not null)
            {
                continue;
            }

            foreach (var asset in document.Assets)
            {
                var objectName = GameAssetMetadataIndex.NormalizeObjectName(asset.ObjectName);
                var classification = ClassifyEntityAsset(
                    entity,
                    objectName,
                    referenceNames,
                    candidateNames,
                    customizationCandidates is { Count: > 0 });
                if (classification is null)
                {
                    continue;
                }

                var packageMatch = new PackageIndexMatch(
                    document.SourcePath,
                    document.PackageAlias,
                    document.PhysicalPackageName,
                    document.Assets.Count,
                    asset.ExportIndex,
                    asset.Kind,
                    asset.ClassName,
                    asset.ObjectName,
                    asset.SerialSize,
                    asset.SerialOffset,
                    metadata.Find(document.PackageAlias, document.PhysicalPackageName, asset.ObjectName));
                matches.Add(new EntityAssetMatch(
                    packageMatch,
                    classification.Value.Evidence,
                    classification.Value.Confidence,
                    classification.Value.Reason));
                if (matches.Count >= resultLimit)
                {
                    break;
                }
            }

            if (matches.Count >= resultLimit)
            {
                break;
            }
        }

        watch.Stop();
        return new EntityAssetBrowseResult(
            entity,
            matches
                .OrderBy(match => match.Confidence)
                .ThenBy(match => match.Asset.PackageAlias, StringComparer.OrdinalIgnoreCase)
                .ThenBy(match => match.Asset.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            cacheFiles.Count,
            preparation.WasBuilt,
            watch.Elapsed.TotalSeconds);
    }

    private static (EntityAssetEvidence Evidence, AssetRelationshipConfidence Confidence, string Reason)?
        ClassifyEntityAsset(
            GameEntityRecord entity,
            string objectName,
            IReadOnlySet<string> references,
            IReadOnlySet<string> candidates,
            bool customizationOnly)
    {
        if (candidates.Contains(objectName))
        {
            return (
                EntityAssetEvidence.CustomizationCandidate,
                AssetRelationshipConfidence.Inferred,
                "Candidate generated from the ordered character-customization record; verify in game.");
        }

        if (customizationOnly)
        {
            return null;
        }

        if (references.Contains(objectName))
        {
            return (
                EntityAssetEvidence.DirectDatabaseReference,
                AssetRelationshipConfidence.Authoritative,
                "Exact object referenced by an asset-bearing game database field.");
        }

        if (references.Any(reference => GameAssetMetadataIndex.IsAssetFamilyMember(objectName, reference)))
        {
            return (
                EntityAssetEvidence.DatabaseAssetFamily,
                AssetRelationshipConfidence.High,
                "Separator-bound object family of a direct database reference.");
        }

        if (entity.Kind == GameEntityKind.PlayableCharacter)
        {
            return (
                EntityAssetEvidence.AppearancePackageFamily,
                AssetRelationshipConfidence.Contextual,
                "Asset is stored in the playable class appearance-package family.");
        }

        return null;
    }
}
