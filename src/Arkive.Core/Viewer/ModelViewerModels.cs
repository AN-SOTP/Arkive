using Arkive.Core.Assets;

namespace Arkive.Core.Viewer;

public sealed record ModelViewerRequest(
    string PackagePath,
    string PackageAlias,
    string? PackageSearchRoot,
    int ExportIndex,
    long SerialOffset,
    long SerialSize,
    string ClassName,
    string ObjectName);

public sealed record ModelViewerProgress(string Stage, string Detail);

public sealed record ModelViewerObject(
    string PackagePath,
    string PackageFile,
    string PackageAlias,
    string ClassName,
    string ObjectName,
    int ExportIndex,
    AssetKind Kind);

public sealed record ModelViewerSection(
    int Index,
    int MaterialSlot,
    string? Material,
    int FirstIndex,
    int Triangles);

public sealed record ModelViewerLod(
    int Index,
    int Vertices,
    int Triangles,
    int UvSets,
    IReadOnlyList<ModelViewerSection> Sections);

public sealed record ModelViewerBone(int Index, string Name, int Parent);

/// <summary>An attach socket; offsets are in the bone's space, in Unreal units (centimetres, Unreal rotation units).</summary>
public sealed record ModelViewerSocket(string Name, string Bone, float[]? Location, float[]? Rotation, float[]? Scale);

public sealed record ModelViewerTexture(
    string Name,
    string Url,
    int Width,
    int Height,
    string PixelFormat,
    long Bytes);

public sealed record ModelViewerMaterial(
    int Slot,
    string Name,
    string? Reference,
    string ClassName,
    IReadOnlyList<string> ParentChain,
    string RootMaterial,
    string BlendMode,
    bool TwoSided,
    bool Masked,
    float OpacityMaskClipValue,
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, float[]> Vectors,
    IReadOnlyDictionary<string, string> Textures,
    IReadOnlyDictionary<string, string> TextureUrls,
    MaterialRecipePlan Recipe,
    MaterialRecipePlan StoredTexture,
    Arkive.Core.Viewer.Shaders.GameShaderPlan? GameShader = null,
    string? GameShaderFailure = null);

public sealed record ModelViewerScene(
    int SchemaVersion,
    DateTimeOffset CreatedUtc,
    ModelViewerObject Object,
    string GeometrySource,
    string? ModelUrl,
    string GeometryUnits,
    IReadOnlyList<ModelViewerLod> Lods,
    IReadOnlyList<ModelViewerBone> Bones,
    IReadOnlyList<ModelViewerSocket> Sockets,
    bool? HasVertexColors,
    IReadOnlyList<ModelViewerMaterial> Materials,
    IReadOnlyList<ModelViewerTexture> Textures,
    string? PropertiesUrl,
    IReadOnlyList<string> Diagnostics,
    double PreparationSeconds,
    string GlslPrelude,
    IReadOnlyDictionary<string, double>? StageSeconds = null);

public sealed record ModelViewerSceneResult(
    string Directory,
    string SceneJsonPath,
    ModelViewerScene Scene,
    bool FromCache);
