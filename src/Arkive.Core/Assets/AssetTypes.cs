using Arkive.Core.Mods;

namespace Arkive.Core.Assets;

/// <summary>
/// One kind of game object Arkive can put in a mod: which package classes it covers, what it is called, and what Arkive
/// can do with it.
/// </summary>
/// <param name="CatalogKind">The kind the package index files these objects under.</param>
/// <param name="ModKind">The kind a mod (and an .arkive file) stores them as.</param>
/// <param name="Title">"Skeletal mesh", for a list column.</param>
/// <param name="Singular">"skeletal mesh", inside a sentence.</param>
/// <param name="Plural">"skeletal meshes", inside a sentence.</param>
/// <param name="CanEditClass">
/// True for a class Arkive can give a working copy (an editable file it can rebuild into the package).
/// </param>
/// <param name="AcceptsClass">
/// True for a class an .arkive file may target with this kind. Looser than <paramref name="CanEditClass"/> so mods made
/// by earlier versions keep loading.
/// </param>
/// <param name="Previewable">Arkive's own 3D viewer can show it.</param>
/// <param name="DiscoveryScore">How far search results of this type are lifted (meshes first, then textures, materials).</param>
/// <param name="SearchCategory">What the package index search filters this type by (PackageIndexStore.MatchesCategory).</param>
public sealed record AssetType(
    AssetKind CatalogKind,
    UnifiedPackageAssetKind ModKind,
    string Title,
    string Singular,
    string Plural,
    Func<string, bool> CanEditClass,
    Func<string, bool> AcceptsClass,
    bool Previewable,
    int DiscoveryScore,
    string SearchCategory)
{
    /// <summary>"Skeletal meshes", for a list of types.</summary>
    public string PluralTitle => char.ToUpperInvariant(Plural[0]) + Plural[1..];

    /// <summary>"1 texture", "3 skeletal meshes".</summary>
    public string Count(int count) => $"{count:N0} {(count == 1 ? Singular : Plural)}";
}

/// <summary>
/// Every asset type Arkive can mod, in one place. Code that asks "what is this object" or "what can Arkive do with it"
/// reads it from here instead of matching class names itself.
///
/// Adding a type (particle systems, sounds): append its value to <see cref="AssetKind"/> and
/// <see cref="UnifiedPackageAssetKind"/> (both are saved, AssetKind by number in the package index and
/// UnifiedPackageAssetKind by name in .arkive files, so never reorder or rename them), add its entry to
/// <see cref="Moddable"/> and its classes to <see cref="CatalogKind"/> and <see cref="UmodelKind"/>, then give it the
/// per-type steps every type has: a working copy (ArkiveModMaterializationService.CreateEditableAsync and its
/// ArkiveModEditWorkspaces store, listed by ArkiveModEditSnapshot), capture into a mod (ArkiveModAuthoringService.
/// CaptureAsync), its files and checks in the .arkive format (ArkiveModFileService.ValidateAssetData), and rebuilding
/// it into the package (UnifiedPackageComposerService). The smoke tests fail until the registry covers every kind.
/// </summary>
public static class AssetTypes
{
    public static readonly AssetType Texture = new(
        AssetKind.Texture, UnifiedPackageAssetKind.Texture2D, "Texture", "texture", "textures",
        CanEditClass: className => className.Contains("Texture2D", StringComparison.OrdinalIgnoreCase),
        AcceptsClass: className => className.Contains("Texture", StringComparison.OrdinalIgnoreCase),
        Previewable: false, DiscoveryScore: 6, SearchCategory: "Texture");

    public static readonly AssetType SkeletalMesh = new(
        AssetKind.SkeletalMesh, UnifiedPackageAssetKind.SkeletalMesh, "Skeletal mesh", "skeletal mesh", "skeletal meshes",
        CanEditClass: IsSkeletalMeshClass, AcceptsClass: IsSkeletalMeshClass,
        Previewable: true, DiscoveryScore: 8, SearchCategory: "SkeletalMesh");

    public static readonly AssetType StaticMesh = new(
        AssetKind.StaticMesh, UnifiedPackageAssetKind.StaticMesh, "Static mesh", "static mesh", "static meshes",
        CanEditClass: IsStaticMeshClass, AcceptsClass: IsStaticMeshClass,
        Previewable: true, DiscoveryScore: 8, SearchCategory: "StaticMesh");

    public static readonly AssetType Material = new(
        AssetKind.Material, UnifiedPackageAssetKind.MaterialInstanceConstant, "Material", "material", "materials",
        CanEditClass: IsMaterialInstanceConstant, AcceptsClass: IsMaterialInstanceConstant,
        Previewable: false, DiscoveryScore: 4, SearchCategory: "MaterialInstanceConstant");

    public static readonly AssetType ParticleEffect = new(
        AssetKind.ParticleSystem, UnifiedPackageAssetKind.ParticleSystem, "Particle effect", "particle effect", "particle effects",
        CanEditClass: IsParticleSystem, AcceptsClass: IsParticleSystem,
        Previewable: false, DiscoveryScore: 5, SearchCategory: "ParticleSystem");

    /// <summary>
    /// A screen of the game's interface: a Scaleform movie (Flash) holding its layout, text and scripts. Its pictures
    /// are separate textures. Arkive gives the movie file to an editor such as JPEXS and puts the saved file back.
    /// </summary>
    public static readonly AssetType InterfaceMovie = new(
        AssetKind.InterfaceMovie, UnifiedPackageAssetKind.InterfaceMovie, "Interface movie", "interface movie", "interface movies",
        CanEditClass: IsInterfaceMovieClass, AcceptsClass: IsInterfaceMovieClass,
        Previewable: false, DiscoveryScore: 3, SearchCategory: "InterfaceMovie");

    /// <summary>Every type that can be put in a mod.</summary>
    public static IReadOnlyList<AssetType> Moddable { get; } = [Texture, SkeletalMesh, StaticMesh, Material, ParticleEffect, InterfaceMovie];

    /// <summary>The type a mod stores as <paramref name="kind"/>.</summary>
    public static AssetType For(UnifiedPackageAssetKind kind) =>
        Moddable.FirstOrDefault(type => type.ModKind == kind) ??
        throw new ArgumentOutOfRangeException(nameof(kind), kind, "No asset type is registered for this mod kind.");

    /// <summary>The type of an indexed object, or null when Arkive cannot mod objects of its kind.</summary>
    public static AssetType? For(AssetKind kind) => Moddable.FirstOrDefault(type => type.CatalogKind == kind);

    /// <summary>The type Arkive can make a working copy of for this object, or null when it cannot edit it.</summary>
    public static AssetType? Editable(AssetKind kind, string className) =>
        For(IndexedKind(kind, className)) is { } type && type.CanEditClass(className) ? type : null;

    /// <summary>
    /// How the package index files a class. Its rules are saved with every index, so existing rules never change; a
    /// new type only files classes that used to be plain objects (see <see cref="IndexedKind"/>).
    /// </summary>
    public static AssetKind CatalogKind(string className)
    {
        if (className.Contains("Texture", StringComparison.OrdinalIgnoreCase)) return AssetKind.Texture;
        if (className.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)) return AssetKind.StaticMesh;
        if (className.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) return AssetKind.SkeletalMesh;
        if (className.Contains("Material", StringComparison.OrdinalIgnoreCase)) return AssetKind.Material;
        if (IsParticleSystem(className)) return AssetKind.ParticleSystem;
        if (IsInterfaceMovieClass(className)) return AssetKind.InterfaceMovie;
        return AssetKind.Object;
    }

    /// <summary>
    /// The kind of an object read from a package index. Indexes made before a kind existed filed its objects as plain
    /// objects; their class names still say what they are. Test builds filed particle effects and interface movies
    /// under each other's numbers before both were added, so those kinds are also read from the class name.
    /// </summary>
    public static AssetKind IndexedKind(AssetKind stored, string className) =>
        stored is AssetKind.Object or AssetKind.ParticleSystem or AssetKind.InterfaceMovie ? CatalogKind(className) : stored;

    /// <summary>How UModel's export list (and so its exports to files) files a class.</summary>
    public static AssetKind UmodelKind(string className) => className.ToLowerInvariant() switch
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

    /// <summary>True for a kind UModel can export to a file Arkive can show or edit.</summary>
    public static bool UmodelExportable(AssetKind kind) =>
        kind is AssetKind.Texture or AssetKind.StaticMesh or AssetKind.SkeletalMesh or AssetKind.Material;

    private static bool IsSkeletalMeshClass(string className) =>
        className.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase);

    private static bool IsStaticMeshClass(string className) =>
        className.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase) &&
        !className.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase);

    private static bool IsParticleSystem(string className) =>
        className.Equals("ParticleSystem", StringComparison.OrdinalIgnoreCase);

    private static bool IsInterfaceMovieClass(string className) =>
        className.Equals("EFSwfMovie", StringComparison.OrdinalIgnoreCase);

    private static bool IsMaterialInstanceConstant(string className) =>
        className.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase);
}
