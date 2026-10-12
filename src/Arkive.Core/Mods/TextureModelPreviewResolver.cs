using Arkive.Core.Discovery;

namespace Arkive.Core.Mods;

public sealed record TextureModelReference(
    string DisplayName,
    string? Archetype,
    string SourceAssetReference,
    string PackageName,
    string ObjectName,
    string Evidence);

/// <summary>
/// Converts Arkive's verified serialized-dependency metadata into source-model
/// references suitable for a texture-on-model preview. It deliberately excludes
/// naming-only guesses: a wrong mesh is worse UX than no preview candidate.
/// </summary>
public static class TextureModelPreviewResolver
{
    public static IReadOnlyList<TextureModelReference> Resolve(
        GameAssetMetadataIndex metadata,
        string packageAlias,
        string physicalPackageName,
        string objectName)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return Resolve(metadata.Find(packageAlias, physicalPackageName, objectName));
    }

    public static IReadOnlyList<TextureModelReference> Resolve(
        IEnumerable<GameAssetAssociation> associations)
    {
        ArgumentNullException.ThrowIfNull(associations);
        return associations
            .Where(item => item.Relation.Equals(
                               GameAssetAssociationRelation.VerifiedSerializedDependency,
                               StringComparison.OrdinalIgnoreCase) &&
                           !string.IsNullOrWhiteSpace(item.SourceAssetReference))
            .Select(item => TryCreate(item, out var reference) ? reference : null)
            .Where(item => item is not null)
            .Cast<TextureModelReference>()
            .DistinctBy(item => new
            {
                Package = item.PackageName.ToUpperInvariant(),
                Object = item.ObjectName.ToUpperInvariant(),
                item.DisplayName,
            })
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryCreate(
        GameAssetAssociation association,
        out TextureModelReference reference)
    {
        reference = null!;
        var value = association.SourceAssetReference!.Trim().Trim('\'', '"');
        var quote = value.IndexOf('\'');
        if (quote >= 0)
        {
            value = value[(quote + 1)..].TrimEnd('\'');
        }

        var dot = value.LastIndexOf('.');
        if (dot <= 0 || dot == value.Length - 1)
        {
            return false;
        }

        var package = Path.GetFileNameWithoutExtension(value[..dot].Replace('/', Path.DirectorySeparatorChar));
        var objectName = value[(dot + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(package) || string.IsNullOrWhiteSpace(objectName))
        {
            return false;
        }

        reference = new TextureModelReference(
            association.DisplayName,
            association.Archetype,
            association.SourceAssetReference,
            package,
            objectName,
            $"Verified serialized dependency from {association.EntityType} " +
            $"{association.EntityId} ({association.Field}).");
        return true;
    }
}
