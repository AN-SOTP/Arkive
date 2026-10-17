using Arkive.Core.Assets;

namespace Arkive.Core.External;

public sealed record UmodelAssetEntry(
    int ExportIndex,
    long SerialOffset,
    long SerialSize,
    string ClassName,
    string ObjectName)
{
    public AssetKind Kind => AssetTypes.UmodelKind(ClassName);

    public bool IsExportable => AssetTypes.UmodelExportable(Kind);

    public string DisplayClassName => ClassName.ToLowerInvariant() switch
    {
        "texture" => "Texture",
        "texture2d" => "Texture2D",
        "eftexture2d" => "EFTexture2D",
        "texturecube" => "TextureCube",
        "lightmaptexture2d" => "LightMapTexture2D",
        "shadowmaptexture2d" => "ShadowMapTexture2D",
        "staticmesh" => "StaticMesh",
        "skeletalmesh" => "SkeletalMesh",
        "material" => "Material",
        "material3" => "Material3",
        "materialinstanceconstant" => "MaterialInstanceConstant",
        "materialinstancetimevarying" => "MaterialInstanceTimeVarying",
        _ => ClassName,
    };
}
