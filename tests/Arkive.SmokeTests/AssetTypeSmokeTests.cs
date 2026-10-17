using Arkive.Core.External;
using Arkive.Core.Mods;

namespace Arkive.SmokeTests;

internal static class AssetTypeSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("registers every mod kind and indexed kind once, with names for the Mods list", TestRegistryCoversEveryKind),
        new("files package classes, UModel exports and .arkive targets exactly as before the registry", TestClassRulesUnchanged),
    ];

    private static void TestRegistryCoversEveryKind()
    {
        foreach (var kind in Enum.GetValues<UnifiedPackageAssetKind>())
            Assert(AssetTypes.Moddable.Count(type => type.ModKind == kind) == 1, $"mod kind {kind} is not registered exactly once");
        foreach (var kind in Enum.GetValues<AssetKind>().Where(kind => kind != AssetKind.Object))
            Assert(AssetTypes.Moddable.Count(type => type.CatalogKind == kind) == 1, $"indexed kind {kind} is not registered exactly once");
        Assert(AssetTypes.For(AssetKind.Object) is null, "plain objects were made moddable");
        // Saved by number in the package index and by name in .arkive files: existing values never move.
        Assert((int)AssetKind.Texture == 1 && (int)AssetKind.StaticMesh == 2 && (int)AssetKind.SkeletalMesh == 3 && (int)AssetKind.Material == 4,
            "AssetKind values were reordered; existing package indexes would be read wrongly");
        Assert(AssetTypes.For(UnifiedPackageAssetKind.SkeletalMesh).Count(1) == "1 skeletal mesh" &&
               AssetTypes.For(UnifiedPackageAssetKind.Texture2D).Count(3) == "3 textures" &&
               AssetTypes.For(UnifiedPackageAssetKind.MaterialInstanceConstant).Title == "Material",
            "asset type names changed");
    }

    private static void TestClassRulesUnchanged()
    {
        // Each class: index kind, UModel kind, what Arkive can edit, which mod kinds may target it (the rules each place
        // used on its own before 0.3.8).
        (string Class, AssetKind Catalog, AssetKind Umodel, UnifiedPackageAssetKind? Editable, UnifiedPackageAssetKind[] Accepts)[] cases =
        [
            ("Texture2D", AssetKind.Texture, AssetKind.Texture, UnifiedPackageAssetKind.Texture2D, [UnifiedPackageAssetKind.Texture2D]),
            ("EFTexture2D", AssetKind.Texture, AssetKind.Texture, UnifiedPackageAssetKind.Texture2D, [UnifiedPackageAssetKind.Texture2D]),
            ("LightMapTexture2D", AssetKind.Texture, AssetKind.Texture, UnifiedPackageAssetKind.Texture2D, [UnifiedPackageAssetKind.Texture2D]),
            ("TextureCube", AssetKind.Texture, AssetKind.Texture, null, [UnifiedPackageAssetKind.Texture2D]),
            ("TextureRenderTarget2D", AssetKind.Texture, AssetKind.Object, null, [UnifiedPackageAssetKind.Texture2D]),
            ("SkeletalMesh", AssetKind.SkeletalMesh, AssetKind.SkeletalMesh, UnifiedPackageAssetKind.SkeletalMesh, [UnifiedPackageAssetKind.SkeletalMesh]),
            ("SkeletalMesh3", AssetKind.Object, AssetKind.SkeletalMesh, null, [UnifiedPackageAssetKind.SkeletalMesh]),
            ("StaticMesh", AssetKind.StaticMesh, AssetKind.StaticMesh, UnifiedPackageAssetKind.StaticMesh, [UnifiedPackageAssetKind.StaticMesh]),
            ("StaticMesh3", AssetKind.Object, AssetKind.StaticMesh, null, [UnifiedPackageAssetKind.StaticMesh]),
            ("MaterialInstanceConstant", AssetKind.Material, AssetKind.Material, UnifiedPackageAssetKind.MaterialInstanceConstant,
                [UnifiedPackageAssetKind.MaterialInstanceConstant]),
            ("Material", AssetKind.Material, AssetKind.Material, null, []),
            ("MaterialInstanceTimeVarying", AssetKind.Material, AssetKind.Material, null, []),
            ("PhysicalMaterial", AssetKind.Material, AssetKind.Object, null, []),
            ("ParticleSystem", AssetKind.Object, AssetKind.Object, null, []),
            ("SoundCue", AssetKind.Object, AssetKind.Object, null, []),
            ("efswfmovie", AssetKind.Object, AssetKind.Object, null, []),
            ("particlesystem", AssetKind.Object, AssetKind.Object, null, []),
            ("AnimSet", AssetKind.Object, AssetKind.Object, null, []),
        ];
        foreach (var (className, catalog, umodel, editable, accepts) in cases)
        {
            Assert(AssetTypes.CatalogKind(className) == catalog, $"{className} is indexed as {AssetTypes.CatalogKind(className)}, not {catalog}");
            var entry = new UmodelAssetEntry(1, 0, 0, className, "x");
            Assert(entry.Kind == umodel && entry.IsExportable == (umodel != AssetKind.Object),
                $"UModel files {className} as {entry.Kind} (exportable {entry.IsExportable}), not {umodel}");
            Assert(AssetTypes.Editable(catalog, className)?.ModKind == editable,
                $"{className} is editable as {AssetTypes.Editable(catalog, className)?.ModKind}, not {editable}");
            foreach (var kind in Enum.GetValues<UnifiedPackageAssetKind>())
                Assert(AssetTypes.For(kind).AcceptsClass(className) == accepts.Contains(kind),
                    $"an .arkive {kind} asset {(accepts.Contains(kind) ? "can no longer" : "may now")} target {className}");
        }
        Assert(AssetTypes.SkeletalMesh.Previewable && AssetTypes.StaticMesh.Previewable &&
               !AssetTypes.Texture.Previewable && !AssetTypes.Material.Previewable,
            "the 3D viewer is offered for the wrong types");
        Assert(AssetTypes.SkeletalMesh.DiscoveryScore == 8 && AssetTypes.StaticMesh.DiscoveryScore == 8 &&
               AssetTypes.Texture.DiscoveryScore == 6 && AssetTypes.Material.DiscoveryScore == 4,
            "search ranking by type changed");
    }
}
