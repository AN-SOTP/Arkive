using System.Text.Json.Serialization;
using Arkive.Core.Materials;
using Arkive.Core.Particles;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

public static class ArkiveModFileFormat
{
    public const string Extension = ".arkive";
    public const string FormatId = "arkive.mod";
    public const int CurrentFormatVersion = 1;
    public const string ManifestEntryName = "arkive.json";
    public const string AssetDirectory = "assets";
    public const string ImageRole = "image";
    public const string GlbRole = "glb";
    public const string MovieRole = "movie";

    // Size limits only guard against damaged or hostile archives; real mods (a whole zone, every effect of a boss) stay far
    // below them. The manifest is read into memory in one piece, so its limit is what a PC can parse comfortably.
    public const long MaximumArchiveBytes = 64L * 1024 * 1024 * 1024;
    public const long MaximumManifestBytes = 1024L * 1024 * 1024;
    public const long MaximumEntryBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumTotalUncompressedBytes = 256L * 1024 * 1024 * 1024;
    public const int MaximumEntries = 1_000_000;
    public const int MaximumAssets = 500_000;
    public const int MaximumEntryNameLength = 240;
    public const double MaximumCompressionRatio = 1000d;

    public const long CompressionRatioCheckMinimumEntryBytes = 128L * 1024 * 1024;
}

public sealed record ArkiveModAssetTarget(
    string PackageAlias,
    string PhysicalPackageName,
    string ClassName,
    string ObjectName,
    int? NativeExportIndex,
    string SourcePackageSha256,
    string SourceExportSha256,
    // Set only when the package holds another object with this name and class; see ArkiveModObjectPath.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ObjectPath = null);

public sealed record ArkiveModFileEntry(
    string Role,
    string Path,
    long Bytes,
    string Sha256);

public sealed record ArkiveModTextureData(
    string PixelFormat,
    int Width,
    int Height,
    TextureImportAlphaPolicy AlphaPolicy);

public sealed record ArkiveModMeshData(
    string ImportRoute,
    string? SourceGlbSha256);

public sealed record ArkiveModMaterialReference(
    string Name,
    string SourceIdentity,
    string SourceResolvedObject,
    string TargetIdentity,
    string TargetResolvedObject)
{
    [JsonIgnore]
    public bool Changed => !SourceIdentity.Equals(TargetIdentity, StringComparison.OrdinalIgnoreCase);
}

public sealed record ArkiveModMaterialData(
    IReadOnlyList<MaterialScalarEdit> Scalars,
    IReadOnlyList<MaterialVectorEdit> Vectors,
    IReadOnlyList<ArkiveModMaterialReference> Textures,
    ArkiveModMaterialReference? Parent);

public sealed record ArkiveModFileAsset(
    string AssetId,
    UnifiedPackageAssetKind Kind,
    string DisplayName,
    ArkiveModAssetTarget Target,
    IReadOnlyList<ArkiveModFileEntry> Files)
{
    public ArkiveModTextureData? Texture { get; init; }

    public ArkiveModMeshData? Mesh { get; init; }

    public ArkiveModMaterialData? Material { get; init; }

    public ArkiveModParticleData? Particle { get; init; }
}

/// <summary>
/// A particle effect change: the recipe it was made with and the value tables it writes. On a client whose effect is
/// byte-identical the patches are written as they are; after a game update the recipe is applied again to the new effect.
/// </summary>
public sealed record ArkiveModParticleData(ParticleEffectRecipe Recipe, IReadOnlyList<ParticleEffectPatch> Patches);

public sealed record ArkiveModFileManifest(
    string Format,
    int FormatVersion,
    string ModId,
    string Name,
    string Version,
    string? Author,
    string? Description,
    string? Website,
    IReadOnlyList<string> Tags,
    string GameProfile,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CreatedWith,
    IReadOnlyList<ArkiveModFileAsset> Assets)
{
    [JsonIgnore]
    public IReadOnlyList<string> PhysicalPackageNames => Assets
        .Select(asset => asset.Target.PhysicalPackageName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public sealed record ArkiveModMetadata(
    string Name,
    string Version,
    string? Author = null,
    string? Description = null,
    string? Website = null,
    IReadOnlyList<string>? Tags = null);

public sealed record ArkiveModPackFile(string Role, string SourcePath, string FileName);

public sealed record ArkiveModPackAsset(ArkiveModFileAsset Descriptor, IReadOnlyList<ArkiveModPackFile> Files);

public sealed record ArkiveModFileReadResult(
    string Path,
    long Bytes,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256,
    ArkiveModFileManifest Manifest);

public sealed class ArkiveModRefusalException : NotSupportedException
{
    public ArkiveModRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}
