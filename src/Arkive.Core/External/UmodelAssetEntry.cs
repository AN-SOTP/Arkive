using Arkive.Core.Assets;

namespace Arkive.Core.External;

public sealed record UmodelAssetEntry(
    int ExportIndex,
    long SerialOffset,
    long SerialSize,
    string ClassName,
    string ObjectName)
{
    public AssetKind Kind => ClassName.ToLowerInvariant() switch
    {
        "texture" or
        "texture2d" or
        "eftexture2d" or
        "texturecube" or
        "lightmaptexture2d" or
        "shadowmaptexture2d" => AssetKind.Texture,
        "staticmesh" or "staticmesh3" => AssetKind.StaticMesh,
        "skeletalmesh" or "skeletalmesh3" => AssetKind.SkeletalMesh,
        "material" or
        "material3" or
        "materialinstance" or
        "materialinstanceconstant" or
        "materialinstancetimevarying" => AssetKind.Material,
        _ => AssetKind.Object,
    };

    public bool IsExportable => Kind is
        AssetKind.Texture or
        AssetKind.StaticMesh or
        AssetKind.SkeletalMesh or
        AssetKind.Material;

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
