using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Inspection;
using Arkive.Core.Materials;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;

namespace Arkive.Core.Discovery;

/// <summary>A range of <see cref="ZoneMapScene"/>'s binary file: its byte offset and element count.</summary>
public sealed record ZoneSceneRange(long Offset, int Count);

/// <param name="Material">The section's slot, an index into the mesh's <see cref="ZoneSceneMesh.Materials"/>.</param>
public sealed record ZoneSceneSection(int FirstIndex, int IndexCount, int Material);

/// <summary>One mesh of the map at the most detailed level within the scene's vertex budget.</summary>
/// <param name="Normals">Four signed bytes a vertex (x, y, z, unused), in the viewer's space.</param>
/// <param name="Materials">The mesh's own materials (package.object) by slot; a placement can override them.</param>
public sealed record ZoneSceneMesh(string Key, ZoneSceneRange Positions, ZoneSceneRange? Normals, ZoneSceneRange Uvs, ZoneSceneRange Indices,
    IReadOnlyList<ZoneSceneSection> Sections, IReadOnlyList<string?> Materials)
{
    /// <summary>The mesh's second set of texture coordinates, which lay it on its placements' baked lightmaps.</summary>
    public ZoneSceneRange? LightmapUvs { get; init; }
}

public sealed record ZoneSceneTexture(string Key, string File, string Format, int Width, int Height, float[] Colour);

/// <summary>
/// A material as the overview draws it: its colour texture times its tint (diffuse_color x diffuse_brightness), plus
/// its glow (texture_emissive x emissive_color x emissive_intensity), the parameters Lost Ark's background materials
/// use, read from the instance and the instances it derives from.
/// </summary>
/// <param name="Diffuse">An index into <see cref="ZoneMapScene.Textures"/>, or -1.</param>
/// <param name="Masked">Leaves, grass and other cut-out pieces: the texture's alpha cuts them out.</param>
public sealed record ZoneSceneMaterial(string Key, int Diffuse, float[] Tint, int Emissive, float[] EmissiveColour, float Tiling,
    bool Masked, float[] Colour)
{
    /// <summary>The normal map (an index into <see cref="ZoneMapScene.Textures"/>), or -1.</summary>
    public int Normal { get; init; } = -1;

    public float NormalStrength { get; init; } = 1;

    /// <summary>How rough (0 mirror to 1 matt) and how metallic the surface reads, from its PBR parameters.</summary>
    public float Roughness { get; init; } = 0.85f;

    public float Metalness { get; init; }
}

/// <summary>Every placement of one mesh with the same materials: one 4x4 matrix each (row-major, viewer space).</summary>
/// <param name="Materials">Indices into <see cref="ZoneMapScene.Materials"/>, one per mesh slot (-1 for none).</param>
/// <param name="Layer">"solid" for the map itself; "sky" for sky domes, planets and painted backdrops; "effect" for
/// glows, water and light shafts; "cutscene" for sets the game only streams in during cinematics. All but solid start
/// hidden: drawn as plain meshes they would cover the map.</param>
public sealed record ZoneSceneBatch(int Mesh, IReadOnlyList<int> Materials, ZoneSceneRange Matrices, string Layer = "solid")
{
    /// <summary>The page of baked light the placements use (an index into <see cref="ZoneMapScene.Lightmaps"/>), or -1.</summary>
    public int Lightmap { get; init; } = -1;

    /// <summary>Each placement's place on that page: four floats (scale x, y, then offset x, y) for its lightmap coordinates.</summary>
    public ZoneSceneRange? LightmapPlaces { get; init; }
}

/// <summary>One ground layer of a landscape tile, as its landscape material sets it (layer01_diffuse and so on).</summary>
/// <param name="Texture">An index into <see cref="ZoneMapScene.Textures"/>, or -1.</param>
/// <param name="TextureKey">The texture's package.object, so picking it in the list tints this layer.</param>
public sealed record ZoneSceneLayer(string Name, int Texture, string? TextureKey, float[] Tint, float Tiling);

/// <summary>A landscape tile: a height grid already in viewer space, drawn with its ground layers.</summary>
/// <param name="Weights">Each vertex's weight of each layer (bytes, vertex by vertex, layer by layer).</param>
public sealed record ZoneSceneTerrain(ZoneSceneRange Positions, ZoneSceneRange Uvs, ZoneSceneRange Indices, int Material,
    IReadOnlyList<ZoneSceneLayer> Layers, ZoneSceneRange? Weights);

/// <summary>
/// One of the map's lighting set-ups (an environment volume: raid phases often have several), as the game's
/// environment settings give it: the sun, the height fog and the post-process colour grading.
/// </summary>
/// <param name="SunDirection">The way the sunlight travels, in the viewer's space.</param>
/// <param name="Lut">A colour-grading look-up table (a 256x16 PNG beside the scene), or null.</param>
/// <param name="Minimum">The volume's box (viewer space): where in the map these settings apply.</param>
public sealed record ZoneSceneEnvironment(
    string Name,
    float[] Centre,
    float[] Minimum,
    float[] Maximum,
    float[] SunDirection,
    float[] SunColour,
    float SunBrightness,
    float[] AmbientColour,
    float[] FogColour,
    float FogDensity,
    float FogHeightFalloff,
    float FogStart,
    float FogMaxOpacity,
    string? Lut,
    float[] Shadows,
    float[] Midtones,
    float[] Highlights,
    float Desaturation)
{
    /// <summary>The game's environment cubemap for these settings (efpbrenvcubemaps), which lights and reflects.</summary>
    public string? Cubemap { get; init; }

    /// <summary>How far the cubemap is turned around the vertical, in degrees.</summary>
    public float CubemapAngle { get; init; }
}

/// <summary>One of the map's arenas: a busy area of solid pieces (raid gates often have several, apart).</summary>
/// <param name="Environment">The lighting that applies there (an index into <see cref="ZoneMapScene.Environments"/>).</param>
public sealed record ZoneSceneArena(float[] Centre, float Size, int Pieces, int Environment);

/// <summary>
/// A whole map for the Zone Viewer's 3D view: its meshes (decoded by Arkive itself), where the levels place each one
/// (static meshes, collections and foliage), the landscape's height grids with their ground layers, every material's
/// textures (in the game's own compressed formats, at most 512 pixels across, 1024 for ground layers and 256 for
/// normal maps), the levels' baked lightmaps packed into pages, and the map's lighting, fog and colour grading. Saved as scene.json, scene.bin and DDS files in one folder.
/// Distances are metres, Y up, as in the Asset Viewer. In scene.bin positions and matrices are 32-bit floats,
/// normals 8-bit, texture coordinates 16-bit floats and indices 16-bit integers, each range on a 4-byte boundary.
/// </summary>
/// <param name="Focus">Where the play area is: the camera starts looking at it.</param>
public sealed record ZoneMapScene(
    int Version,
    string Map,
    float[] Focus,
    float FocusSize,
    IReadOnlyList<ZoneSceneMesh> Meshes,
    IReadOnlyList<ZoneSceneTexture> Textures,
    IReadOnlyList<ZoneSceneMaterial> Materials,
    IReadOnlyList<ZoneSceneBatch> Batches,
    IReadOnlyList<ZoneSceneTerrain> Terrain,
    IReadOnlyList<ZoneSceneEnvironment> Environments,
    int Environment,
    int Placements,
    int UnreadMeshes)
{
    /// <summary>The map's arenas, busiest first; the first is <see cref="Focus"/>.</summary>
    public IReadOnlyList<ZoneSceneArena> Arenas { get; init; } = [];

    /// <summary>
    /// The baked light of the placements (Lost Ark bakes the lighting of static pieces into lightmaps): pages of
    /// the levels' lightmaps packed together, each an index into <see cref="Textures"/>. A texel is the light's
    /// colour times its strength, in the lightmaps' own encoding.
    /// </summary>
    public IReadOnlyList<int> Lightmaps { get; init; } = [];

    public const int CurrentVersion = 4;
    public const string FileName = "scene.json";
    public const string DataFileName = "scene.bin";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ZoneMapScene? TryLoad(string directory, string mapKey)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path) || !File.Exists(Path.Combine(directory, DataFileName))) return null;
        try
        {
            var scene = JsonSerializer.Deserialize<ZoneMapScene>(File.ReadAllText(path), Json);
            return scene is { Version: CurrentVersion } && scene.Map == mapKey ? scene : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }
}

public sealed record ZoneSceneProgress(string Stage, int Completed, int Total);

/// <summary>Builds a <see cref="ZoneMapScene"/> from a map's level packages.</summary>
public sealed partial class ZoneMapSceneBuilder(
    IReadOnlyDictionary<string, string> packageFiles,
    IReadOnlyDictionary<string, (PackageIndexDocument Document, IndexedAsset Asset)> indexed,
    IChunkProtectionProvider protectionProvider,
    NativeTextureExportService? textureExport)
{
    // A mesh is drawn at its most detailed level with at most this many vertices (else its least detailed).
    private const int VertexBudget = 65_000;
    private const int TextureSize = 512;
    private const int GroundTextureSize = 1024;
    private const int GlowTextureSize = 256;
    private const int NormalTextureSize = 256;
    // Baked light is soft: each lightmap is kept at most a quarter of its size across (64 pixels for the small ones,
    // which are most), packed into pages of this size.
    private const int LightmapPage = 2048;
    // A placeholder for the package hash material and texture reports carry; the scene keeps no report.
    private static readonly string NoHash = new('0', 64);

    /// <summary>
    /// Appended to package.object for the static mesh of that name, which <c>indexed</c> also lists under it: a
    /// package can hold a redirector or a material of the same name, which the plain key may find instead.
    /// </summary>
    public const string StaticMeshSuffix = "|staticmesh";

    private (PackageIndexDocument Document, IndexedAsset Asset) MeshEntry(string key) =>
        indexed.TryGetValue(key + StaticMeshSuffix, out var mesh) ? mesh : indexed.GetValueOrDefault(key);

    private sealed record Placement(string Mesh, string?[] Materials, Matrix4x4 World, bool Cutscene)
    {
        public PlacedLightmap? Lightmap { get; init; }
    }

    /// <summary>A placement's baked lightmap: its two textures in its level and where on them the placement lies.</summary>
    private sealed record PlacedLightmap(string Alias, AssetCatalogEntry Colour, AssetCatalogEntry Directions, Vector3 ColourScale,
        Vector3 DirectionScale, Vector4 Place)
    {
        public string Key => $"{Alias}.{Colour.ObjectName}|{Directions.ObjectName}".ToLowerInvariant();
    }

    private sealed record Tile(Vector3[] Positions, Vector2[] Uvs, uint[] Indices, string? Material, string[] Layers, byte[]? Weights);

    private sealed record Environment(string Name, Vector3 Centre, Vector3 Minimum, Vector3 Maximum, IReadOnlyList<LevelProperty> Data,
        string? Lut, IReadOnlyList<LevelProperty>? Settings)
    {
        public string? Cubemap { get; init; }
    }

    private readonly ConcurrentBag<Placement> _placements = [];
    private readonly ConcurrentBag<Tile> _tiles = [];
    private readonly ConcurrentBag<Environment> _environments = [];
    private readonly ConcurrentBag<(Vector3 Direction, Vector3 Colour, float Brightness)> _suns = [];
    // The map's own settings, used wherever no environment volume applies: WorldInfo's default post-process
    // settings and environment colour, and the level's height fog.
    private (IReadOnlyList<LevelProperty> World, string? Lut, string? Cubemap)? _world;
    private IReadOnlyList<LevelProperty>? _fog;

    public async Task<ZoneMapScene> BuildAsync(ZoneMap map, string directory, IProgress<ZoneSceneProgress>? progress,
        CancellationToken cancellationToken)
    {
        var levels = map.LevelPackages.Where(packageFiles.ContainsKey).ToArray();
        var completed = 0;
        await Parallel.ForEachAsync(levels, Options(cancellationToken), async (alias, token) =>
        {
            try
            {
                await ReadLevelAsync(map, alias, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or
                                                  UnauthorizedAccessException)
            {
            }
            progress?.Report(new ZoneSceneProgress("Reading where the map places its meshes", Interlocked.Increment(ref completed), levels.Length));
        }).ConfigureAwait(false);

        var meshKeys = _placements.Select(placement => placement.Mesh).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var meshes = new ConcurrentDictionary<string, DecodedMesh>(StringComparer.OrdinalIgnoreCase);
        completed = 0;
        await Parallel.ForEachAsync(meshKeys.GroupBy(key => key[..key.IndexOf('.')], StringComparer.OrdinalIgnoreCase),
            Options(cancellationToken), (group, token) =>
            {
                ReadMeshes(group.ToArray(), meshes, token);
                progress?.Report(new ZoneSceneProgress("Reading the meshes", Interlocked.Add(ref completed, group.Count()), meshKeys.Length));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        var materialKeys = _placements.Where(placement => meshes.ContainsKey(placement.Mesh))
            .SelectMany(placement => placement.Materials.Concat(meshes[placement.Mesh].Materials))
            .Concat(_tiles.Select(tile => tile.Material))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var materials = new ConcurrentDictionary<string, ZoneSceneMaterial>(StringComparer.OrdinalIgnoreCase);
        var layers = new ConcurrentDictionary<string, IReadOnlyList<ZoneSceneLayer>>(StringComparer.OrdinalIgnoreCase);
        var terrainMaterials = _tiles.Select(tile => tile.Material).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.Combine(directory, "t"));
        completed = 0;
        await Parallel.ForEachAsync(materialKeys, Options(cancellationToken), async (key, token) =>
        {
            materials[key] = await ReadMaterialAsync(key, directory, token).ConfigureAwait(false);
            if (terrainMaterials.Contains(key)) layers[key] = await ReadLayersAsync(key, directory, token).ConfigureAwait(false);
            progress?.Report(new ZoneSceneProgress("Reading the materials and their textures", Interlocked.Increment(ref completed), materialKeys.Length));
        }).ConfigureAwait(false);

        var bakedKeys = _placements.Where(placement => placement.Lightmap is not null && meshes.TryGetValue(placement.Mesh, out var mesh) &&
                                                       mesh.LightmapUvs is not null)
            .Select(placement => placement.Lightmap!).DistinctBy(lightmap => lightmap.Key).ToArray();
        var light = new ConcurrentDictionary<string, RgbaImage>();
        completed = 0;
        await Parallel.ForEachAsync(bakedKeys, Options(cancellationToken), async (lightmap, token) =>
        {
            if (await BakedLightAsync(lightmap, token).ConfigureAwait(false) is { } picture) light[lightmap.Key] = picture;
            progress?.Report(new ZoneSceneProgress("Reading the baked lighting", Interlocked.Increment(ref completed), bakedKeys.Length));
        }).ConfigureAwait(false);
        var (pages, baked) = PackLightmaps(light, directory);

        progress?.Report(new ZoneSceneProgress("Reading the lighting and colour grading", 0, 0));
        var arenas = Arenas(meshes);
        var focus = arenas.Count > 0 ? (arenas[0].Centre, arenas[0].Size) : Focus(meshes, _environments.Select(environment => environment.Centre).ToArray());
        var environments = new List<ZoneSceneEnvironment>();
        foreach (var environment in _environments.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            environments.Add(await ReadEnvironmentAsync(environment, directory, environments.Count, cancellationToken).ConfigureAwait(false));
        environments.Insert(0, await DefaultEnvironmentAsync(focus.Centre, directory, cancellationToken).ConfigureAwait(false));
        var chosen = LightingAt(environments, focus.Centre);
        progress?.Report(new ZoneSceneProgress("Saving the map", 0, 0));
        var scene = Write(map, directory, meshes, materials, layers, baked, environments, chosen, focus) with
        {
            Arenas = arenas.Select(arena => new ZoneSceneArena(Array(arena.Centre), arena.Size, arena.Pieces, LightingAt(environments, arena.Centre))).ToArray(),
            Lightmaps = pages,
        };
        File.WriteAllText(Path.Combine(directory, ZoneMapScene.FileName), JsonSerializer.Serialize(scene, ZoneMapScene.Json));
        return scene;
    }

    private static ParallelOptions Options(CancellationToken cancellationToken) =>
        new() { MaxDegreeOfParallelism = Math.Clamp(System.Environment.ProcessorCount / 2, 1, 6), CancellationToken = cancellationToken };

    // ---- Placements ----

    /// <summary>A map's cinematic sets are its levels named scene (lv_kur_death_ed_04_scene10a).</summary>
    public static bool IsCutsceneLevel(ZoneMap map, string alias) => map.Zones.SelectMany(zone => zone.Levels).Any(persistent =>
    {
        var stem = persistent.EndsWith("_ps", StringComparison.OrdinalIgnoreCase) ? persistent[..^2] : persistent + "_";
        return alias.StartsWith(stem, StringComparison.OrdinalIgnoreCase) &&
               alias[stem.Length..].StartsWith("scene", StringComparison.OrdinalIgnoreCase);
    });

    private async Task ReadLevelAsync(ZoneMap map, string alias, CancellationToken cancellationToken)
    {
        var cutscene = IsCutsceneLevel(map, alias);
        var catalog = new AssetCatalogReader(protectionProvider: protectionProvider).Read(packageFiles[alias]);
        using var logical = new LogicalPackageStream(catalog.Summary, protectionProvider);
        var byIndex = catalog.Assets.ToDictionary(asset => asset.ExportIndex);
        var actors = new Dictionary<int, Matrix4x4?>();
        foreach (var asset in catalog.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = asset.ClassName.ToLowerInvariant();
            if (type == "landscapecomponent")
            {
                await ReadLandscapeAsync(alias, catalog, logical, byIndex, asset, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (!cutscene && type == "efenvironmentinfodata")
            {
                ReadEnvironment(alias, catalog, logical, byIndex, asset);
                continue;
            }
            if (!cutscene && type == "dominantdirectionallight")
            {
                ReadSun(catalog, logical, byIndex, asset);
                continue;
            }
            if (!cutscene && type == "worldinfo" && LevelProperties.Read(Bytes(logical, asset), catalog.Names, out _) is { } world &&
                world.Find("defaultpostprocesssettings") is not null)
            {
                var defaults = LevelProperties.ReadAt(world.Find("defaultpostprocesssettings")!.Value, catalog.Names, 0, out _) ?? [];
                _world ??= (world.Concat(defaults).ToArray(), Reference(catalog, defaults.Object("colorgrading_lookuptable"), alias),
                    ObjectName(Reference(catalog, world.Object("pbrenvironmentcubemap"), alias)));
                continue;
            }
            if (!cutscene && type == "exponentialheightfogcomponent")
            {
                _fog ??= LevelProperties.Read(Bytes(logical, asset), catalog.Names, out _);
                continue;
            }
            if (!type.EndsWith("staticmeshcomponent", StringComparison.Ordinal)) continue;
            var bytes = Bytes(logical, asset);
            var properties = LevelProperties.Read(bytes, catalog.Names, out var end);
            if (properties is null || properties.Bool("hiddengame")) continue;
            if (Reference(catalog, properties.Object("staticmesh"), alias) is not { } mesh || IsHelper(mesh)) continue;
            var overrides = properties.Objects("materials").Select(reference => Reference(catalog, reference, alias)).ToArray();
            var local = ComponentTransform(properties);
            var tail = bytes[end..];
            var baked = Lightmap(tail, reference => byIndex.TryGetValue(reference, out var texture) &&
                                                    texture.ClassName.Equals("lightmaptexture2d", StringComparison.OrdinalIgnoreCase));
            var lightmap = baked is { } found
                ? new PlacedLightmap(alias, byIndex[found.Colour], byIndex[found.Directions], found.ColourScale, found.DirectionScale, found.Place)
                : null;
            var outer = catalog.Exports[asset.ExportIndex - 1].OuterIndex;
            if (!actors.TryGetValue(outer, out var actor))
                actors[outer] = actor = byIndex.TryGetValue(outer, out var owner) ? ActorTransform(catalog, logical, owner) : Matrix4x4.Identity;
            if (actor is null) continue;
            if (type.Contains("instanced", StringComparison.Ordinal))
            {
                // Each instance has its own corner of the component's lightmap.
                foreach (var (instance, bias) in InstanceEntries(tail))
                    _placements.Add(new Placement(mesh, overrides, ToViewer(instance * local * actor.Value), cutscene)
                    {
                        Lightmap = lightmap is null ? null : lightmap with { Place = new Vector4(lightmap.Place.X, lightmap.Place.Y, bias.X, bias.Y) },
                    });
            }
            else _placements.Add(new Placement(mesh, overrides, ToViewer(local * actor.Value), cutscene) { Lightmap = lightmap });
        }
    }

    // Editor and gameplay helpers the game never draws: culling boxes, navigation and engine shapes.
    private static bool IsHelper(string mesh) =>
        mesh.StartsWith("enginemeshes.", StringComparison.OrdinalIgnoreCase) || mesh.StartsWith("lv_navimesh.", StringComparison.OrdinalIgnoreCase) ||
        mesh.Contains("_cul_", StringComparison.OrdinalIgnoreCase) || mesh.Contains("collision", StringComparison.OrdinalIgnoreCase) ||
        mesh.Contains("blocking", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An actor's placement; components in a static mesh collection carry their own and the collection none. Null
    /// for an actor whose components never draw in game.
    /// </summary>
    private static Matrix4x4? ActorTransform(AssetCatalog catalog, LogicalPackageStream logical, AssetCatalogEntry actor)
    {
        var type = actor.ClassName.ToLowerInvariant();
        if (type.Contains("collection", StringComparison.Ordinal)) return Matrix4x4.Identity;
        if (type.Contains("volume", StringComparison.Ordinal) || type.Contains("brush", StringComparison.Ordinal)) return null;
        var properties = LevelProperties.Read(Bytes(logical, actor), catalog.Names, out _) ?? [];
        if (properties.Bool("bhidden")) return null;
        return ActorMatrix(properties);
    }

    private static Matrix4x4 ActorMatrix(IReadOnlyList<LevelProperty> properties)
    {
        var scale = (properties.Vector("drawscale3d") ?? Vector3.One) * (properties.Float("drawscale") ?? 1);
        // PrePivot moves the actor's own origin before it is scaled, turned and placed.
        return Matrix4x4.CreateTranslation(-(properties.Vector("prepivot") ?? Vector3.Zero)) * Matrix4x4.CreateScale(scale) *
               Rotation(properties.Rotator("rotation")) * Matrix4x4.CreateTranslation(properties.Vector("location") ?? Vector3.Zero);
    }

    private static Matrix4x4 ComponentTransform(IReadOnlyList<LevelProperty> properties)
    {
        var scale = (properties.Vector("scale3d") ?? Vector3.One) * (properties.Float("scale") ?? 1);
        return Matrix4x4.CreateScale(scale) * Rotation(properties.Rotator("rotation")) *
               Matrix4x4.CreateTranslation(properties.Vector("translation") ?? Vector3.Zero);
    }

    /// <summary>Unreal's rotation matrix for a rotator (pitch, yaw, roll), rows as the rotated axes.</summary>
    public static Matrix4x4 Rotation((int Pitch, int Yaw, int Roll)? rotator)
    {
        if (rotator is not { } value) return Matrix4x4.Identity;
        const double unit = Math.PI * 2 / 65536;
        var (sp, cp) = Math.SinCos(value.Pitch * unit);
        var (sy, cy) = Math.SinCos(value.Yaw * unit);
        var (sr, cr) = Math.SinCos(value.Roll * unit);
        return new Matrix4x4(
            (float)(cp * cy), (float)(cp * sy), (float)sp, 0,
            (float)(sr * sp * cy - cr * sy), (float)(sr * sp * sy + cr * cy), (float)(-sr * cp), 0,
            (float)(-(cr * sp * cy + sr * sy)), (float)(cy * sr - cr * sp * sy), (float)(cr * cp), 0,
            0, 0, 0, 1);
    }

    /// <summary>
    /// Unreal's centimetres with Z up to the viewer's metres with Y up (the swap the mesh decoder applies to each
    /// vertex), for a matrix that places such a mesh.
    /// </summary>
    public static Matrix4x4 ToViewer(Matrix4x4 world)
    {
        var swap = new Matrix4x4(1, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        var result = swap * world * swap;
        result.M41 *= 0.01f;
        result.M42 *= 0.01f;
        result.M43 *= 0.01f;
        return result;
    }

    private static Vector3 ToViewer(Vector3 unreal) => new(unreal.X * 0.01f, unreal.Z * 0.01f, unreal.Y * 0.01f);

    /// <summary>
    /// A foliage component's instances: after its properties, an array of 80-byte entries (a 4x4 matrix relative to
    /// the component, then lightmap and shadow map offsets), found by its element size and count.
    /// </summary>
    public static IEnumerable<Matrix4x4> Instances(ReadOnlySpan<byte> tail) => InstanceEntries(tail).Select(entry => entry.Matrix);

    /// <summary>
    /// A static mesh component's baked lightmap, from the data after its properties: per level of detail its shadow
    /// maps, then a lightmap of type 2 (textures), its light GUIDs, three (texture, scale) pairs (normalised colour,
    /// directional strength, none) and the lightmap coordinates' scale and offset. Found by its first texture, which
    /// <paramref name="isLightmapTexture"/> recognises.
    /// </summary>
    public static (int Colour, int Directions, Vector3 ColourScale, Vector3 DirectionScale, Vector4 Place)? Lightmap(
        ReadOnlySpan<byte> tail, Func<int, bool> isLightmapTexture)
    {
        for (var offset = 8; offset + 64 <= tail.Length; offset += 4)
        {
            var colour = BinaryPrimitives.ReadInt32LittleEndian(tail[offset..]);
            if (colour <= 0 || !isLightmapTexture(colour)) continue;
            var directions = BinaryPrimitives.ReadInt32LittleEndian(tail[(offset + 16)..]);
            if (BinaryPrimitives.ReadInt32LittleEndian(tail[(offset - 8)..]) != 2 || !isLightmapTexture(directions)) return null;
            var place = new Vector4(F(tail, offset + 48), F(tail, offset + 52), F(tail, offset + 56), F(tail, offset + 60));
            if (!float.IsFinite(place.X + place.Y + place.Z + place.W) || place.X <= 0 || place.Y <= 0) return null;
            return (colour, directions, new Vector3(F(tail, offset + 4), F(tail, offset + 8), F(tail, offset + 12)),
                new Vector3(F(tail, offset + 20), F(tail, offset + 24), F(tail, offset + 28)), place);
        }
        return null;

        static float F(ReadOnlySpan<byte> bytes, int at) => BitConverter.ToSingle(bytes[at..]);
    }

    /// <summary>A foliage component's instances: each one's matrix and the offset of its corner of the lightmap.</summary>
    public static IEnumerable<(Matrix4x4 Matrix, Vector2 LightmapBias)> InstanceEntries(ReadOnlySpan<byte> tail)
    {
        const int entry = 80;
        for (var offset = 0; offset <= tail.Length - 8; offset += 4)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(tail[offset..]) != entry) continue;
            var count = BinaryPrimitives.ReadInt32LittleEndian(tail[(offset + 4)..]);
            if (count <= 0 || offset + 8L + (long)count * entry > tail.Length) continue;
            var data = tail.Slice(offset + 8, count * entry);
            var valid = true;
            for (var index = 0; index < count && valid; index++)
                valid = BitConverter.ToSingle(data[(index * entry + 60)..]) == 1f && BitConverter.ToSingle(data[(index * entry + 12)..]) == 0f;
            if (!valid) continue;
            var entries = new (Matrix4x4, Vector2)[count];
            for (var index = 0; index < count; index++)
            {
                var floats = data.Slice(index * entry, 72);
                entries[index] = (new Matrix4x4(
                    F(floats, 0), F(floats, 1), F(floats, 2), F(floats, 3), F(floats, 4), F(floats, 5), F(floats, 6), F(floats, 7),
                    F(floats, 8), F(floats, 9), F(floats, 10), F(floats, 11), F(floats, 12), F(floats, 13), F(floats, 14), F(floats, 15)),
                    new Vector2(F(floats, 16), F(floats, 17)));
            }
            return entries;
        }
        return [];

        static float F(ReadOnlySpan<byte> bytes, int index) => BitConverter.ToSingle(bytes[(index * 4)..]);
    }

    /// <summary>
    /// A landscape tile: its height map (a small 32-bit texture in the level; height is red * 256 + green, 32768 at
    /// zero, 1/128 unit a step) laid on its quads from its section base, then placed by the landscape actor.
    /// </summary>
    private async Task ReadLandscapeAsync(string alias, AssetCatalog catalog, LogicalPackageStream logical,
        Dictionary<int, AssetCatalogEntry> byIndex, AssetCatalogEntry component, CancellationToken cancellationToken)
    {
        var properties = LevelProperties.Read(Bytes(logical, component), catalog.Names, out _);
        if (properties is null || !byIndex.TryGetValue(properties.Object("heightmaptexture"), out var heightmap)) return;
        var quads = properties.Int("componentsizequads") ?? 0;
        var subsectionQuads = properties.Int("subsectionsizequads") ?? quads;
        if (quads <= 0 || subsectionQuads <= 0) return;
        var baseX = properties.Int("sectionbasex") ?? 0;
        var baseY = properties.Int("sectionbasey") ?? 0;
        var scaleBias = properties.Vector4("heightmapscalebias") ?? new Vector4(1f / (quads + 1), 1f / (quads + 1), 0, 0);
        var material = Reference(catalog, properties.Object("overridematerial"), alias) ??
                       Reference(catalog, properties.Object("materialinstance"), alias);
        var outer = catalog.Exports[component.ExportIndex - 1].OuterIndex;
        var proxy = byIndex.TryGetValue(outer, out var owner) ? ActorTransform(catalog, logical, owner) ?? Matrix4x4.Identity : Matrix4x4.Identity;
        var picture = await DecodePictureAsync(packageFiles[alias], alias, heightmap, 0, cancellationToken).ConfigureAwait(false);
        if (picture is null) return;
        var width = (int)Math.Round(1 / scaleBias.X);
        var offsetX = (int)Math.Round(scaleBias.Z * width);
        var offsetY = (int)Math.Round(scaleBias.W * (1 / scaleBias.Y));
        var size = quads + 1;
        var positions = new Vector3[size * size];
        var uvs = new Vector2[size * size];
        var world = ToViewer(proxy);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                // Each subsection keeps its own edge row, so the pixel of a shared edge vertex is in the next one.
                var px = offsetX + Math.Min(x / subsectionQuads, Math.Max(0, quads / subsectionQuads - 1)) + x;
                var py = offsetY + Math.Min(y / subsectionQuads, Math.Max(0, quads / subsectionQuads - 1)) + y;
                px = Math.Clamp(px, 0, picture.Width - 1);
                py = Math.Clamp(py, 0, picture.Height - 1);
                var pixel = (py * picture.Width + px) * 4;
                var height = (picture.Pixels[pixel] << 8 | picture.Pixels[pixel + 1]) - 32768;
                // The same swap and scale the mesh decoder gives a vertex, then the landscape actor's placement.
                positions[y * size + x] = Vector3.Transform(ToViewer(new Vector3(baseX + x, baseY + y, height / 128f)), world);
                // Ground textures repeat about every 8 quads before each layer's own tiling.
                uvs[y * size + x] = new Vector2((baseX + x) / 8f, (baseY + y) / 8f);
            }
        var indices = new List<uint>(quads * quads * 6);
        for (var y = 0; y < quads; y++)
            for (var x = 0; x < quads; x++)
            {
                var corner = (uint)(y * size + x);
                indices.AddRange([corner, corner + (uint)size, corner + 1, corner + 1, corner + (uint)size, corner + (uint)size + 1]);
            }
        var (layers, weights) = await ReadBlendAsync(alias, catalog, byIndex, properties, quads, subsectionQuads, cancellationToken)
            .ConfigureAwait(false);
        _tiles.Add(new Tile(positions, uvs, indices.ToArray(), material, layers, weights));
    }

    /// <summary>
    /// How much of each ground layer every vertex of a tile shows: the tile's blend masks (weight maps, one layer per
    /// colour channel) as WeightmapLayerAllocations assigns them. Empty when they cannot be read.
    /// </summary>
    private async Task<(string[] Layers, byte[]? Weights)> ReadBlendAsync(string alias, AssetCatalog catalog,
        Dictionary<int, AssetCatalogEntry> byIndex, IReadOnlyList<LevelProperty> properties, int quads, int subsectionQuads,
        CancellationToken cancellationToken)
    {
        var allocations = properties.Structs("weightmaplayerallocations", catalog.Names)
            .Select(item => (Layer: item.Text("layername"), Texture: item.Byte("weightmaptextureindex") ?? 0, Channel: item.Byte("weightmaptexturechannel") ?? 0))
            .Where(item => item.Layer is not null && item.Channel is >= 0 and < 4).ToArray();
        var textures = properties.Objects("weightmaptextures");
        if (allocations.Length == 0 || textures.Length == 0) return ([], null);
        var pictures = new RgbaImage?[textures.Length];
        for (var index = 0; index < textures.Length; index++)
            if (byIndex.TryGetValue(textures[index], out var texture))
                pictures[index] = await DecodePictureAsync(packageFiles[alias], alias, texture, 0, cancellationToken, opaque: false).ConfigureAwait(false);
        var scaleBias = properties.Vector4("weightmapscalebias") ?? new Vector4(1f / (quads + 1), 1f / (quads + 1), 0, 0);
        var size = quads + 1;
        var weights = new byte[size * size * allocations.Length];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var vertex = y * size + x;
                for (var layer = 0; layer < allocations.Length; layer++)
                {
                    var (_, textureIndex, channel) = allocations[layer];
                    if (textureIndex >= pictures.Length || pictures[textureIndex] is not { } picture) continue;
                    var offsetX = (int)Math.Round(scaleBias.Z * picture.Width - 0.5);
                    var offsetY = (int)Math.Round(scaleBias.W * picture.Height - 0.5);
                    var px = Math.Clamp(offsetX + x + Math.Min(x / subsectionQuads, Math.Max(0, quads / subsectionQuads - 1)), 0, picture.Width - 1);
                    var py = Math.Clamp(offsetY + y + Math.Min(y / subsectionQuads, Math.Max(0, quads / subsectionQuads - 1)), 0, picture.Height - 1);
                    weights[vertex * allocations.Length + layer] = picture.Pixels[(py * picture.Width + px) * 4 + channel];
                }
            }
        return (allocations.Select(item => item.Layer!.ToLowerInvariant()).ToArray(), weights);
    }

    // ---- Lighting ----

    /// <summary>
    /// An environment volume's settings (Lost Ark's EFEnvironmentInfoData: sun, fog, environment colour, and its
    /// post-process settings with the colour-grading table), placed at its volume.
    /// </summary>
    private void ReadEnvironment(string alias, AssetCatalog catalog, LogicalPackageStream logical,
        Dictionary<int, AssetCatalogEntry> byIndex, AssetCatalogEntry data)
    {
        var properties = LevelProperties.Read(Bytes(logical, data), catalog.Names, out _);
        if (properties is null) return;
        var outer = catalog.Exports[data.ExportIndex - 1].OuterIndex;
        // Only an info volume's own settings: blend volumes hold partial overrides for transitions.
        if (!byIndex.TryGetValue(outer, out var volume) ||
            !volume.ClassName.Equals("efenvironmentinfovolume", StringComparison.OrdinalIgnoreCase)) return;
        var volumeProperties = LevelProperties.Read(Bytes(logical, volume), catalog.Names, out _) ?? [];
        // The volume itself holds the post-process settings (colour grading and its look-up table).
        var settings = volumeProperties.Find("settings") is { StructName: "postprocesssettings" } struct1
            ? LevelProperties.ReadAt(struct1.Value, catalog.Names, 0, out _) : null;
        var lut = settings is null ? null : Reference(catalog, settings.Object("colorgrading_lookuptable"), alias);
        var (minimum, maximum) = VolumeBox(catalog, logical, byIndex, volumeProperties);
        _environments.Add(new Environment($"{alias}.{volume.ObjectName}", (minimum + maximum) / 2, minimum, maximum, properties, lut, settings)
        {
            Cubemap = ObjectName(Reference(catalog, properties.Object("pbr_environmentcubemap"), alias)),
        });
    }

    /// <summary>
    /// A volume's box in viewer space: its brush's collision shapes (BrushAggGeom: each convex element's ElemBox),
    /// placed by the volume actor. A volume without them is a 1-metre box at its location.
    /// </summary>
    private static (Vector3 Minimum, Vector3 Maximum) VolumeBox(AssetCatalog catalog, LogicalPackageStream logical,
        Dictionary<int, AssetCatalogEntry> byIndex, IReadOnlyList<LevelProperty> volume)
    {
        var placement = ActorMatrix(volume);
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        if (byIndex.TryGetValue(volume.Object("brushcomponent"), out var component) &&
            LevelProperties.Read(Bytes(logical, component), catalog.Names, out _) is { } brush &&
            brush.Find("brushagggeom") is { } geometry &&
            LevelProperties.ReadAt(geometry.Value, catalog.Names, 0, out _) is { } aggregate)
            foreach (var element in aggregate.Structs("convexelems", catalog.Names))
            {
                // A Box is stored as it is in memory: minimum, maximum (three floats each) and a valid flag.
                if (element.Find("elembox") is not { Value.Length: 25 } box || box.Value[24] == 0) continue;
                var min = new Vector3(BitConverter.ToSingle(box.Value, 0), BitConverter.ToSingle(box.Value, 4), BitConverter.ToSingle(box.Value, 8));
                var max = new Vector3(BitConverter.ToSingle(box.Value, 12), BitConverter.ToSingle(box.Value, 16), BitConverter.ToSingle(box.Value, 20));
                for (var corner = 0; corner < 8; corner++)
                {
                    var point = ToViewer(Vector3.Transform(new Vector3((corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y,
                        (corner & 4) == 0 ? min.Z : max.Z), placement));
                    low = Vector3.Min(low, point);
                    high = Vector3.Max(high, point);
                }
            }
        if (low.X > high.X)
        {
            var at = ToViewer(volume.Vector("location") ?? Vector3.Zero);
            (low, high) = (at - Vector3.One * 0.5f, at + Vector3.One * 0.5f);
        }
        return (low, high);
    }

    private void ReadSun(AssetCatalog catalog, LogicalPackageStream logical, Dictionary<int, AssetCatalogEntry> byIndex, AssetCatalogEntry actor)
    {
        var properties = LevelProperties.Read(Bytes(logical, actor), catalog.Names, out _);
        if (properties is null) return;
        var axis = Rotation(properties.Rotator("rotation"));
        var direction = Vector3.Normalize(ToViewer(new Vector3(axis.M11, axis.M12, axis.M13)) * 100);
        var colour = Vector3.One;
        var brightness = 1f;
        if (byIndex.TryGetValue(properties.Object("lightcomponent"), out var component) &&
            LevelProperties.Read(Bytes(logical, component), catalog.Names, out _) is { } light)
        {
            colour = Colour(light, "lightcolor") ?? colour;
            brightness = light.Float("brightness") ?? brightness;
        }
        _suns.Add((direction, colour, brightness));
    }

    /// <summary>A Color property (stored blue, green, red, alpha) as 0-1 red, green, blue.</summary>
    private static Vector3? Colour(IReadOnlyList<LevelProperty> properties, string name) =>
        properties.Find(name) is { StructName: "color", Value.Length: 4 } property
            ? new Vector3(property.Value[2] / 255f, property.Value[1] / 255f, property.Value[0] / 255f)
            : properties.Find(name) is { StructName: "linearcolor", Value.Length: 16 } linear
                ? new Vector3(BitConverter.ToSingle(linear.Value, 0), BitConverter.ToSingle(linear.Value, 4), BitConverter.ToSingle(linear.Value, 8))
                : null;

    private async Task<ZoneSceneEnvironment> ReadEnvironmentAsync(Environment environment, string directory, int number,
        CancellationToken cancellationToken)
    {
        var data = environment.Data;
        var sun = _suns.FirstOrDefault();
        var direction = sun.Direction == default ? Vector3.Normalize(new Vector3(-0.4f, -1, -0.3f)) : sun.Direction;
        var sunColour = data.Bool("ddl_override") ? Colour(data, "ddl_lightcolor") ?? sun.Colour : sun.Colour == default ? Vector3.One : sun.Colour;
        var sunBrightness = data.Bool("ddl_override") ? data.Float("ddl_brightness") ?? sun.Brightness : sun.Brightness == 0 ? 1 : sun.Brightness;
        var fogColour = Colour(data, "ehf_lightinscatteringcolor") ?? Colour(data, "ehf_oppositelightcolor") ?? new Vector3(0.5f, 0.55f, 0.65f);
        var ambient = Colour(data, "pbr_environmentcolor") ?? Colour(data, "ehf_oppositelightcolor") ?? new Vector3(0.45f, 0.48f, 0.55f);
        string? lut = null;
        if (environment.Lut is { } key && indexed.TryGetValue(key, out var found))
        {
            var picture = await DecodePictureAsync(found.Document.SourcePath, found.Document.PackageAlias, Entry(found), 0, cancellationToken)
                .ConfigureAwait(false);
            if (picture is { Width: 256, Height: 16 })
            {
                lut = $"t/lut{number}.png";
                await PngRgbaCodec.WriteAsync(Path.Combine(directory, lut), picture, cancellationToken).ConfigureAwait(false);
            }
        }
        var settings = environment.Settings ?? [];
        var cubemap = environment.Cubemap;
        return new ZoneSceneEnvironment(environment.Name, Array(environment.Centre), Array(environment.Minimum), Array(environment.Maximum),
            Array(direction), Array(sunColour), sunBrightness,
            Array(ambient), Array(fogColour), data.Float("ehf_fogdensity") ?? 0, data.Float("ehf_fogheightfalloff") ?? 0.2f,
            (data.Float("ehf_startdistance") ?? 0) * 0.01f, data.Float("ehf_fogmaxopacity") ?? 1, lut,
            Array(settings.Vector("scene_shadows") ?? Vector3.Zero), Array(settings.Vector("scene_midtones") ?? Vector3.One),
            Array(settings.Vector("scene_highlights") ?? Vector3.One), settings.Float("scene_desaturation") ?? 0)
        {
            Cubemap = cubemap,
            CubemapAngle = data.Float("pbr_environmentcubemapangle") ?? 0,
        };
    }

    // The settings of the smallest volume around a point, as the game applies the one the player stands in; the map's own elsewhere.
    private static int LightingAt(IReadOnlyList<ZoneSceneEnvironment> environments, Vector3 point) =>
        environments.Select((environment, index) => (Inside: Contains(environment, point), Size: Volume(environment), index))
            .Where(item => item.Inside).OrderBy(item => item.Size).Select(item => item.index).DefaultIfEmpty(0).First();

    /// <summary>
    /// The map's arenas: where it has the most floor, found one after another (the 250-metre square with the most
    /// floor and the floor within 400 metres of it, then the same for what is left once 600 metres around it are set
    /// aside), up to six, each with at least a twentieth of all the floor. Floor is the top of wide, flat solid
    /// pieces (by their area) and the landscape; a map with neither uses all its solid pieces. An arena's height is
    /// its floor's.
    /// </summary>
    private IReadOnlyList<(Vector3 Centre, float Size, int Pieces)> Arenas(IReadOnlyDictionary<string, DecodedMesh> meshes)
    {
        const float cell = 250, reach = 400, apart = 600;
        var boxes = meshes.ToDictionary(pair => pair.Key, pair => (Minimum: pair.Value.Positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min),
            Maximum: pair.Value.Positions.Aggregate(new Vector3(float.MinValue), Vector3.Max)), StringComparer.OrdinalIgnoreCase);
        var solid = _placements.Where(placement => !placement.Cutscene && meshes.ContainsKey(placement.Mesh) &&
                                                   Layer(placement.Mesh, placement.Materials.OfType<string>().ToArray()) == "solid").ToArray();
        var points = new List<(Vector3 Point, float Weight)>();
        foreach (var placement in solid)
        {
            var (minimum, maximum) = boxes[placement.Mesh];
            if (minimum.X > maximum.X) continue;
            var low = new Vector3(float.MaxValue);
            var high = new Vector3(float.MinValue);
            for (var corner = 0; corner < 8; corner++)
            {
                var point = Vector3.Transform(new Vector3((corner & 1) == 0 ? minimum.X : maximum.X, (corner & 2) == 0 ? minimum.Y : maximum.Y,
                    (corner & 4) == 0 ? minimum.Z : maximum.Z), placement.World);
                low = Vector3.Min(low, point);
                high = Vector3.Max(high, point);
            }
            var size = high - low;
            var wide = MathF.Max(size.X, size.Z);
            if (wide > 6 && wide < 400 && size.Y < wide * 0.3f)
                points.Add((new Vector3((low.X + high.X) / 2, high.Y, (low.Z + high.Z) / 2), size.X * size.Z));
        }
        foreach (var tile in _tiles)
            foreach (var point in tile.Positions.Where((_, index) => index % 64 == 0)) points.Add((point, 64));
        if (points.Count == 0)
            points.AddRange(solid.Select(placement => (new Vector3(placement.World.M41, placement.World.M42, placement.World.M43), 1f)));
        points.RemoveAll(item => !float.IsFinite(item.Point.X) || !float.IsFinite(item.Point.Y) || !float.IsFinite(item.Point.Z));
        var total = points.Sum(item => item.Weight);
        var arenas = new List<(Vector3, float, int)>();
        while (arenas.Count < 6 && points.Count > 0)
        {
            var busiest = points.GroupBy(item => (MathF.Floor(item.Point.X / cell), MathF.Floor(item.Point.Z / cell)))
                .MaxBy(group => group.Sum(item => item.Weight))!;
            var middle = Mean(busiest.ToArray());
            var near = points.Where(item => Flat(item.Point - middle) < reach).ToArray();
            if (near.Sum(item => item.Weight) < total / 20) break;
            var centre = Mean(near);
            // The height most of the floor is at, by area.
            var ordered = near.OrderBy(item => item.Point.Y).ToArray();
            var half = 0f;
            centre.Y = ordered.First(item => (half += item.Weight) >= near.Sum(other => other.Weight) / 2).Point.Y;
            var spread = near.Select(item => Flat(item.Point - centre)).Order().ToArray();
            arenas.Add((centre, Math.Max(60, spread[(int)(spread.Length * 0.9)] * 2), near.Length));
            points.RemoveAll(item => Flat(item.Point - middle) < apart);
        }
        return arenas;

        static float Flat(Vector3 offset) => MathF.Sqrt(offset.X * offset.X + offset.Z * offset.Z);

        static Vector3 Mean(IReadOnlyCollection<(Vector3 Point, float Weight)> items) =>
            items.Aggregate(Vector3.Zero, (sum, item) => sum + item.Point * item.Weight) / Math.Max(1e-6f, items.Sum(item => item.Weight));
    }

    private static bool Contains(ZoneSceneEnvironment environment, Vector3 point) => environment.Name != "map" &&
        point.X >= environment.Minimum[0] && point.X <= environment.Maximum[0] && point.Y >= environment.Minimum[1] - 200 &&
        point.Y <= environment.Maximum[1] + 200 && point.Z >= environment.Minimum[2] && point.Z <= environment.Maximum[2];

    private static float Volume(ZoneSceneEnvironment environment) =>
        (environment.Maximum[0] - environment.Minimum[0]) * (environment.Maximum[2] - environment.Minimum[2]);

    /// <summary>
    /// The map's own lighting, used wherever no environment volume applies: WorldInfo's default post-process settings
    /// and environment colour, the level's height fog and its sun.
    /// </summary>
    private async Task<ZoneSceneEnvironment> DefaultEnvironmentAsync(Vector3 centre, string directory, CancellationToken cancellationToken)
    {
        var sun = _suns.FirstOrDefault();
        var world = _world?.World ?? [];
        var fog = _fog ?? [];
        string? lut = null;
        if (_world?.Lut is { } key && indexed.TryGetValue(key, out var found) &&
            await DecodePictureAsync(found.Document.SourcePath, found.Document.PackageAlias, Entry(found), 0, cancellationToken)
                .ConfigureAwait(false) is { Width: 256, Height: 16 } picture)
        {
            lut = "t/lut-map.png";
            await PngRgbaCodec.WriteAsync(Path.Combine(directory, lut), picture, cancellationToken).ConfigureAwait(false);
        }
        var environment = world.Find("pbrenvironmentcolor") is { Value.Length: 16 } colour
            ? new Vector3(BitConverter.ToSingle(colour.Value, 0), BitConverter.ToSingle(colour.Value, 4), BitConverter.ToSingle(colour.Value, 8))
            : new Vector3(0.45f, 0.48f, 0.55f);
        return new ZoneSceneEnvironment("map", Array(centre), Array(centre - Vector3.One * 1e5f), Array(centre + Vector3.One * 1e5f),
            Array(sun.Direction == default ? Vector3.Normalize(new Vector3(-0.4f, -1, -0.3f)) : sun.Direction),
            Array(sun.Colour == default ? Vector3.One : sun.Colour), sun.Brightness == 0 ? 1 : sun.Brightness,
            Array(environment), Array(Colour(fog, "lightinscatteringcolor") ?? Colour(fog, "oppositelightcolor") ?? new Vector3(0.5f, 0.55f, 0.65f)),
            fog.Float("fogdensity") ?? 0, fog.Float("fogheightfalloff") ?? 0.2f, (fog.Float("startdistance") ?? 0) * 0.01f,
            fog.Float("fogmaxopacity") ?? 1, lut, Array(world.Vector("scene_shadows") ?? Vector3.Zero),
            Array(world.Vector("scene_midtones") ?? Vector3.One), Array(world.Vector("scene_highlights") ?? Vector3.One),
            world.Float("scene_desaturation") ?? 0)
        {
            Cubemap = _world?.Cubemap,
            CubemapAngle = world.Float("pbrenvironmentcubemapangle") ?? 0,
        };
    }

    private static float[] Array(Vector3 value) => [value.X, value.Y, value.Z];

    private static string? ObjectName(string? key) => key is null ? null : key[(key.IndexOf('.') + 1)..].ToLowerInvariant();

    // ---- Shared reading ----

    private static byte[] Bytes(LogicalPackageStream logical, AssetCatalogEntry asset)
    {
        var bytes = new byte[asset.SerialSize];
        lock (logical)
        {
            logical.Position = asset.SerialOffset;
            logical.ReadExactly(bytes);
        }
        return bytes;
    }

    /// <summary>An object reference as package.object: an import's outermost package, or this package for an export.</summary>
    private static string? Reference(AssetCatalog catalog, int reference, string alias)
    {
        if (reference > 0 && reference <= catalog.Exports.Count)
            return alias + "." + catalog.Exports[reference - 1].ObjectName.Resolve(catalog.Names);
        if (reference >= 0 || -reference > catalog.Imports.Count) return null;
        var import = catalog.Imports[-reference - 1];
        var outer = import;
        for (var guard = 0; outer.OuterIndex < 0 && -outer.OuterIndex <= catalog.Imports.Count && guard < 16; guard++)
            outer = catalog.Imports[-outer.OuterIndex - 1];
        return ReferenceEquals(outer, import) ? null
            : outer.ObjectName.Resolve(catalog.Names).ToLowerInvariant() + "." + import.ObjectName.Resolve(catalog.Names);
    }

    private static AssetCatalogEntry Entry((PackageIndexDocument Document, IndexedAsset Asset) found) =>
        new(found.Asset.ExportIndex, found.Asset.ObjectName, found.Asset.ObjectName, found.Asset.ClassName, found.Asset.Kind,
            (int)found.Asset.SerialSize, (int)found.Asset.SerialOffset);

    // ---- Meshes ----

    private sealed record DecodedMesh(Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, ushort[] Indices,
        IReadOnlyList<ZoneSceneSection> Sections, IReadOnlyList<string?> Materials)
    {
        public Vector2[]? LightmapUvs { get; init; }
    }

    private void ReadMeshes(IReadOnlyList<string> keys, ConcurrentDictionary<string, DecodedMesh> meshes, CancellationToken cancellationToken)
    {
        var first = keys.Select(MeshEntry).FirstOrDefault(item => item.Document is not null);
        if (first.Document is null) return;
        AssetCatalog catalog;
        try
        {
            catalog = new AssetCatalogReader(protectionProvider: protectionProvider).Read(first.Document.SourcePath);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            return;
        }
        using var logical = new LogicalPackageStream(catalog.Summary, protectionProvider);
        var parser = new StaticMeshNativeLayoutParser();
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = MeshEntry(key);
            if (found.Document is null || found.Document.SourcePath != first.Document.SourcePath ||
                !found.Asset.ClassName.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var bytes = new byte[found.Asset.SerialSize];
                logical.Position = found.Asset.SerialOffset;
                logical.ReadExactly(bytes);
                foreach (var candidate in Ue3PropertyStreamProbe.FindCandidates(bytes, found.Asset.SerialOffset, catalog.Names))
                {
                    if (!parser.TryParse(bytes, found.Asset.SerialOffset, candidate.EndOffset, out var layout, out _) || layout is null) continue;
                    var lod = layout.Lods.FirstOrDefault(item => item.VertexCount <= VertexBudget) ?? layout.Lods[^1];
                    var decoded = new StaticMeshSemanticDecoder().DecodeOne(bytes, lod);
                    var slots = lod.Sections.Select(section => Reference(catalog, section.MaterialReference, found.Document.PackageAlias))
                        .ToArray();
                    meshes[key] = new DecodedMesh(decoded.Positions, decoded.Normals,
                        decoded.TextureCoordinates.Length > 0 ? decoded.TextureCoordinates[0] : new Vector2[decoded.Positions.Length],
                        decoded.Indices, lod.Sections.Select((section, index) => new ZoneSceneSection(section.FirstIndex, section.TriangleCount * 3, index)).ToArray(),
                        slots)
                    {
                        LightmapUvs = decoded.TextureCoordinates.Length > 1 ? decoded.TextureCoordinates[1] : null,
                    };
                    break;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or
                                                  ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            {
            }
        }
    }

    // ---- Materials and textures ----

    /// <summary>A material's parameters merged down its chain of instances: the closest one setting a value wins.</summary>
    private sealed record MaterialValues(
        Dictionary<string, string> Textures,
        Dictionary<string, Vector4> Vectors,
        Dictionary<string, float> Scalars,
        string Package,
        IReadOnlyList<string> Chain);

    private readonly ConcurrentDictionary<string, Lazy<Task<MaterialValues?>>> _values = new(StringComparer.OrdinalIgnoreCase);

    private Task<MaterialValues?> ValuesAsync(string key, CancellationToken cancellationToken) =>
        _values.GetOrAdd(key, item => new Lazy<Task<MaterialValues?>>(() => ReadValuesAsync(item, cancellationToken))).Value;

    private async Task<MaterialValues?> ReadValuesAsync(string key, CancellationToken cancellationToken)
    {
        var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var vectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var chain = new List<string>();
        string? package = null;
        var current = key;
        for (var depth = 0; depth < 6 && current is not null; depth++)
        {
            if (await InspectMaterialAsync(current, cancellationToken).ConfigureAwait(false) is not { } report) break;
            var currentPackage = indexed[current].Document.PackageAlias;
            package ??= currentPackage;
            chain.Add(current);
            foreach (var texture in report.TextureParameters)
                if (ZoneParts.ReferencedName(texture.ResolvedObject) is { } name && FindTexture(currentPackage, name) is { } textureKey)
                    textures.TryAdd(texture.Name, textureKey);
            foreach (var vector in report.VectorParameters)
                vectors.TryAdd(vector.Name, new Vector4(vector.Red, vector.Green, vector.Blue, vector.Alpha));
            foreach (var scalar in report.ScalarParameters) scalars.TryAdd(scalar.Name, scalar.Value);
            if (report.ParentObject is { } parent)
            {
                chain.Add(parent);
                current = parent.StartsWith("materialinstanceconstant'", StringComparison.OrdinalIgnoreCase) &&
                          ZoneParts.ReferencedName(parent) is { } parentName ? FindMaterial(currentPackage, parentName) : null;
            }
            else current = null;
        }
        return package is null ? null : new MaterialValues(textures, vectors, scalars, package, chain);
    }

    private async Task<MaterialInstanceReconnaissanceReport?> InspectMaterialAsync(string key, CancellationToken cancellationToken)
    {
        if (!indexed.TryGetValue(key, out var found) || !found.Asset.ClassName.Contains("MaterialInstance", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return await new MaterialInstanceReconnaissanceService(protectionProvider: protectionProvider).InspectAsync(
                new MaterialInstanceReconnaissanceRequest(found.Document.SourcePath, found.Document.PackageAlias, found.Asset.ExportIndex,
                    AssetKind.Material, "MaterialInstanceConstant", found.Asset.ObjectName, found.Asset.SerialOffset, found.Asset.SerialSize,
                    NoHash), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<ZoneSceneMaterial> ReadMaterialAsync(string key, string directory, CancellationToken cancellationToken)
    {
        float[] grey = [0.55f, 0.55f, 0.55f];
        if (await ValuesAsync(key, cancellationToken).ConfigureAwait(false) is not { } values)
            return new ZoneSceneMaterial(key, -1, [1, 1, 1], -1, [0, 0, 0], 1, false, grey);
        var diffuseKey = values.Textures.Where(pair => DiffuseParameter().IsMatch(pair.Key) && !NotColour().IsMatch(pair.Key))
                             .Select(pair => pair.Value).FirstOrDefault() ??
                         values.Textures.Where(pair => !NotColour().IsMatch(pair.Key) && DiffuseTexture().IsMatch(pair.Value))
                             .Select(pair => pair.Value).FirstOrDefault();
        var diffuse = diffuseKey is null ? -1 : await TextureAsync(diffuseKey, directory, TextureSize, cancellationToken).ConfigureAwait(false);
        var colour = values.Vectors.FirstOrDefault(pair => DiffuseColour().IsMatch(pair.Key)).Value;
        var tint = colour == default ? Vector3.One : new Vector3(colour.X, colour.Y, colour.Z);
        tint *= values.Scalars.FirstOrDefault(pair => DiffuseBrightness().IsMatch(pair.Key)).Value is var brightness and > 0 ? brightness : 1;
        var glowKey = values.Textures.Where(pair => GlowParameter().IsMatch(pair.Key)).Select(pair => pair.Value).FirstOrDefault();
        var glow = glowKey is null ? -1 : await TextureAsync(glowKey, directory, GlowTextureSize, cancellationToken).ConfigureAwait(false);
        var glowColour = values.Vectors.FirstOrDefault(pair => GlowColour().IsMatch(pair.Key)).Value;
        var glowStrength = values.Scalars.FirstOrDefault(pair => GlowStrength().IsMatch(pair.Key)).Value;
        var emissive = glow < 0 ? Vector3.Zero
            : new Vector3(glowColour == default ? 1 : glowColour.X, glowColour == default ? 1 : glowColour.Y, glowColour == default ? 1 : glowColour.Z) *
              (glowStrength > 0 ? glowStrength : 1);
        var tiling = values.Scalars.TryGetValue("uv_tiling", out var uvTiling) && uvTiling > 0 ? uvTiling : 1;
        var normalKey = values.Textures.Where(pair => NormalParameter().IsMatch(pair.Key)).Select(pair => pair.Value).FirstOrDefault();
        var normal = normalKey is null ? -1 : await TextureAsync(normalKey, directory, NormalTextureSize, cancellationToken).ConfigureAwait(false);
        var roughness = values.Scalars.TryGetValue("roughness_intensity", out var rough) ? Math.Clamp(rough * 0.75f, 0.25f, 1) : 0.85f;
        if (values.Scalars.TryGetValue("roughness_power", out var power) && power > 0) roughness = Math.Clamp(roughness * (0.6f + power), 0.25f, 1);
        var metalness = values.Scalars.TryGetValue("metallic_intensity", out var metal) ? Math.Clamp(metal * 0.15f, 0, 0.6f) : 0;
        var masked = diffuse >= 0 && _textures[diffuse].Format == "DXT5" && (CutOut().IsMatch(key) || values.Chain.Any(CutOut().IsMatch));
        var fallback = diffuse >= 0 ? _textures[diffuse].Colour : grey;
        return new ZoneSceneMaterial(key, diffuse, Array(tint), glow, Array(emissive), tiling, masked, fallback)
        {
            Normal = normal,
            NormalStrength = values.Scalars.TryGetValue("normal_intensity", out var strength) ? Math.Clamp(strength, 0, 3) : 1,
            Roughness = roughness,
            Metalness = metalness,
        };
    }

    /// <summary>
    /// A landscape material's ground layers (layer01_diffuse, layer01_color, layer01_brightness, layer01_tiling and so
    /// on, as Lost Ark's landscape_base material names them).
    /// </summary>
    private async Task<IReadOnlyList<ZoneSceneLayer>> ReadLayersAsync(string key, string directory, CancellationToken cancellationToken)
    {
        if (await ValuesAsync(key, cancellationToken).ConfigureAwait(false) is not { } values) return [];
        var result = new List<ZoneSceneLayer>();
        foreach (var (parameter, textureKey) in values.Textures)
        {
            var name = parameter.ToLowerInvariant();
            if (!name.EndsWith("_diffuse", StringComparison.Ordinal)) continue;
            var layer = name[..^"_diffuse".Length];
            var texture = await TextureAsync(textureKey, directory, GroundTextureSize, cancellationToken).ConfigureAwait(false);
            var colour = values.Vectors.TryGetValue(layer + "_color", out var vector) ? new Vector3(vector.X, vector.Y, vector.Z) : Vector3.One;
            var brightness = values.Scalars.TryGetValue(layer + "_brightness", out var value) ? value : 1;
            var tiling = values.Scalars.TryGetValue(layer + "_tiling", out var repeat) && repeat > 0 ? repeat : 1;
            result.Add(new ZoneSceneLayer(layer, texture, textureKey.ToLowerInvariant(), Array(colour * brightness), tiling));
        }
        return result;
    }

    /// <summary>A texture a material names (by object name only): in the material's own package, else any package.</summary>
    private string? FindTexture(string materialPackage, string name)
    {
        if (indexed.ContainsKey(materialPackage + "." + name)) return materialPackage + "." + name;
        return _textureNames.Value.GetValueOrDefault(name);
    }

    private string? FindMaterial(string package, string name) =>
        indexed.ContainsKey(package + "." + name) ? package + "." + name : _materialNames.Value.GetValueOrDefault(name);

    private readonly Lazy<Dictionary<string, string>> _materialNames = new(() =>
        indexed.Where(pair => pair.Value.Asset.ClassName.Contains("MaterialInstance", StringComparison.OrdinalIgnoreCase))
            .GroupBy(pair => pair.Value.Asset.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.OrdinalIgnoreCase));

    private readonly Lazy<Dictionary<string, string>> _textureNames = new(() =>
        indexed.Where(pair => AssetTypes.IndexedKind(pair.Value.Asset.Kind, pair.Value.Asset.ClassName) == AssetKind.Texture)
            .GroupBy(pair => pair.Value.Asset.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.OrdinalIgnoreCase));

    private readonly List<ZoneSceneTexture> _textures = [];
    private readonly ConcurrentDictionary<string, Lazy<Task<int>>> _textureIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The scene's index of a texture, writing it once (the largest size anyone asks for first wins).</summary>
    private Task<int> TextureAsync(string key, string directory, int size, CancellationToken cancellationToken) =>
        _textureIndex.GetOrAdd(key, item => new Lazy<Task<int>>(() => WriteTextureAsync(item, directory, size, cancellationToken))).Value;

    private Task<int> WriteTextureAsync(string key, string directory, int size, CancellationToken cancellationToken) =>
        indexed.TryGetValue(key, out var found)
            ? WriteTextureAsync(key, found.Document.SourcePath, found.Document.PackageAlias, Entry(found), directory, size, cancellationToken)
            : Task.FromResult(-1);

    /// <summary>
    /// A lightmap's light as one picture: its normalised colour times the mean of its three directional strengths
    /// (the light a surface facing straight out of the mesh gets), each times its scale, at most a quarter of its
    /// size across (64 pixels at least).
    /// </summary>
    private async Task<RgbaImage?> BakedLightAsync(PlacedLightmap lightmap, CancellationToken cancellationToken)
    {
        var path = packageFiles[lightmap.Alias];
        var colour = await DecodeLightmapAsync(path, lightmap.Alias, lightmap.Colour, cancellationToken).ConfigureAwait(false);
        var directions = await DecodeLightmapAsync(path, lightmap.Alias, lightmap.Directions, cancellationToken).ConfigureAwait(false);
        if (colour is null || directions is null) return null;
        if (directions.Width != colour.Width || directions.Height != colour.Height)
        {
            var size = Math.Min(Math.Max(colour.Width, colour.Height), Math.Max(directions.Width, directions.Height));
            (colour, directions) = (Shrink(colour, size), Shrink(directions, size));
            if (directions.Width != colour.Width || directions.Height != colour.Height) return null;
        }
        var pixels = new byte[colour.Width * colour.Height * 4];
        var (cs, ds) = (lightmap.ColourScale, lightmap.DirectionScale);
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var strength = (directions.Pixels[index] * ds.X + directions.Pixels[index + 1] * ds.Y + directions.Pixels[index + 2] * ds.Z) / (3 * 255f);
            pixels[index] = (byte)Math.Clamp(colour.Pixels[index] * cs.X * strength, 0, 255);
            pixels[index + 1] = (byte)Math.Clamp(colour.Pixels[index + 1] * cs.Y * strength, 0, 255);
            pixels[index + 2] = (byte)Math.Clamp(colour.Pixels[index + 2] * cs.Z * strength, 0, 255);
            pixels[index + 3] = 255;
        }
        return new RgbaImage(colour.Width, colour.Height, pixels);
    }

    private async Task<RgbaImage?> DecodeLightmapAsync(string packagePath, string alias, AssetCatalogEntry texture, CancellationToken cancellationToken)
    {
        if (textureExport is null) return null;
        try
        {
            var report = await new TextureReconnaissanceService(protectionProvider: protectionProvider).InspectAsync(
                new TextureReconnaissanceRequest(packagePath, alias, texture.ExportIndex - 1, AssetKind.Texture, texture.ClassName,
                    texture.ObjectName, texture.SerialOffset, texture.SerialSize, KnownPackageSha256: NoHash), cancellationToken).ConfigureAwait(false);
            var top = report.MipCandidates.FirstOrDefault()?.Mips.FirstOrDefault();
            var size = top is null ? 64 : Math.Max(64, Math.Max(top.Width, top.Height) / 4);
            return Shrink(await textureExport.DecodePreviewAsync(report, size, cancellationToken, opaque: false).ConfigureAwait(false), size);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or
                                              ArgumentException or InvalidOperationException or UnauthorizedAccessException or
                                              IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Packs the baked light into pages (shelves, tallest first, each on a 4-pixel boundary with its edge repeated
    /// around it so neighbours never bleed together), saved as DXT1. Gives each lightmap its page and how its
    /// coordinates map onto it (scale x, y, offset x, y).
    /// </summary>
    private (int[] Pages, Dictionary<string, (int Page, Vector4 Remap)> Places) PackLightmaps(
        IReadOnlyDictionary<string, RgbaImage> light, string directory)
    {
        const int gutter = 2;
        var places = new Dictionary<string, (int Page, Vector4 Remap)>();
        var pages = new List<byte[]>();
        int x = 0, y = 0, shelf = 0;
        foreach (var (key, picture) in light.OrderByDescending(pair => pair.Value.Height).ThenByDescending(pair => pair.Value.Width)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var width = Math.Min(LightmapPage, (picture.Width + gutter * 2 + 3) / 4 * 4);
            var height = Math.Min(LightmapPage, (picture.Height + gutter * 2 + 3) / 4 * 4);
            if (x + width > LightmapPage) (x, y, shelf) = (0, y + shelf, 0);
            if (pages.Count == 0 || y + height > LightmapPage)
            {
                pages.Add(new byte[LightmapPage * LightmapPage * 4]);
                (x, y, shelf) = (0, 0, 0);
            }
            var page = pages[^1];
            for (var row = 0; row < height; row++)
                for (var column = 0; column < width; column++)
                {
                    var sx = Math.Clamp(column - gutter, 0, picture.Width - 1);
                    var sy = Math.Clamp(row - gutter, 0, picture.Height - 1);
                    picture.Pixels.AsSpan((sy * picture.Width + sx) * 4, 4).CopyTo(page.AsSpan(((y + row) * LightmapPage + x + column) * 4));
                }
            places[key] = (pages.Count - 1, new Vector4(picture.Width / (float)LightmapPage, picture.Height / (float)LightmapPage,
                (x + gutter) / (float)LightmapPage, (y + gutter) / (float)LightmapPage));
            x += width;
            shelf = Math.Max(shelf, height);
        }
        var indices = new int[pages.Count];
        Span<byte> block = stackalloc byte[64];
        for (var number = 0; number < pages.Count; number++)
        {
            var dxt = new byte[LightmapPage * LightmapPage / 2];
            for (var by = 0; by < LightmapPage / 4; by++)
                for (var bx = 0; bx < LightmapPage / 4; bx++)
                {
                    for (var row = 0; row < 4; row++)
                        pages[number].AsSpan(((by * 4 + row) * LightmapPage + bx * 4) * 4, 16).CopyTo(block[(row * 16)..]);
                    BlockCompression.EncodeBc1(block, dxt.AsSpan((by * (LightmapPage / 4) + bx) * 8, 8), allowTransparency: false);
                }
            var file = $"t/light{number}.dds";
            File.WriteAllBytes(Path.Combine(directory, file),
                DdsBlockCompressedWriter.Build(LightmapPage, LightmapPage, NativeTextureFormats.Dxt1.DdsFormat, [dxt]));
            lock (_textures)
            {
                _textures.Add(new ZoneSceneTexture($"baked light {number}", file, "DXT1", LightmapPage, LightmapPage, [0.5f, 0.5f, 0.5f]));
                indices[number] = _textures.Count - 1;
            }
        }
        return (indices, places);
    }

    private async Task<int> WriteTextureAsync(string key, string packagePath, string alias, AssetCatalogEntry texture, string directory,
        int size, CancellationToken cancellationToken)
    {
        if (textureExport is null) return -1;
        try
        {
            var report = await new TextureReconnaissanceService(protectionProvider: protectionProvider).InspectAsync(
                new TextureReconnaissanceRequest(packagePath, alias, texture.ExportIndex - 1, AssetKind.Texture, texture.ClassName,
                    texture.ObjectName, texture.SerialOffset, texture.SerialSize, KnownPackageSha256: NoHash), cancellationToken).ConfigureAwait(false);
            var (dds, width, height, format) = await textureExport.DecodeChainAsync(report, size, cancellationToken).ConfigureAwait(false);
            // Some textures keep only their full-size level in the package (or a format the 3D view cannot draw):
            // shrink that one and compress it again.
            if (Math.Max(width, height) > size * 3 / 2 || format == "BGRA")
            {
                var picture = Shrink(await textureExport.DecodePreviewAsync(report, int.MaxValue, cancellationToken, opaque: false)
                    .ConfigureAwait(false), size);
                (dds, format) = CompressedDds(picture);
                (width, height) = (picture.Width, picture.Height);
            }
            var smallest = DdsBlockTexture.Parse(key, dds);
            var average = Average(smallest.DecodeMip(smallest.Mips.Count - 1, editableView: true));
            lock (_textures)
            {
                var file = $"t/{_textures.Count}.dds";
                File.WriteAllBytes(Path.Combine(directory, file), dds);
                _textures.Add(new ZoneSceneTexture(key.ToLowerInvariant(), file, format, width, height, average));
                return _textures.Count - 1;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or
                                              ArgumentException or InvalidOperationException or UnauthorizedAccessException or
                                              IndexOutOfRangeException)
        {
            return -1;
        }
    }

    /// <summary>
    /// A picture as a DDS with its smaller levels: DXT1 when it is opaque, else DXT5, for textures stored only full size.
    /// </summary>
    private static (byte[] Dds, string Format) CompressedDds(RgbaImage picture)
    {
        var alpha = false;
        for (var index = 3; index < picture.Pixels.Length && !alpha; index += 4) alpha = picture.Pixels[index] < 250;
        var levels = new List<ReadOnlyMemory<byte>>();
        var level = picture;
        Span<byte> block = stackalloc byte[64];
        while (true)
        {
            var wide = (level.Width + 3) / 4;
            var high = (level.Height + 3) / 4;
            var bytes = alpha ? 16 : 8;
            var output = new byte[wide * high * bytes];
            for (var by = 0; by < high; by++)
                for (var bx = 0; bx < wide; bx++)
                {
                    for (var pixel = 0; pixel < 16; pixel++)
                    {
                        var x = Math.Min(level.Width - 1, bx * 4 + pixel % 4);
                        var y = Math.Min(level.Height - 1, by * 4 + pixel / 4);
                        level.Pixels.AsSpan((y * level.Width + x) * 4, 4).CopyTo(block[(pixel * 4)..]);
                    }
                    var target = output.AsSpan((by * wide + bx) * bytes, bytes);
                    if (alpha)
                    {
                        BlockCompression.EncodeBc4Channel(block, 3, target[..8]);
                        BlockCompression.EncodeBc1(block, target[8..], allowTransparency: false);
                    }
                    else BlockCompression.EncodeBc1(block, target, allowTransparency: false);
                }
            levels.Add(output);
            if (level.Width == 1 && level.Height == 1) break;
            level = Halve(level);
        }
        var format = alpha ? NativeTextureFormats.Dxt5 : NativeTextureFormats.Dxt1;
        return (DdsBlockCompressedWriter.Build(picture.Width, picture.Height, format.DdsFormat, levels), alpha ? "DXT5" : "DXT1");
    }

    // The next smaller level: each side halved (never below 1), averaging the pixels it merges.
    private static RgbaImage Halve(RgbaImage picture)
    {
        var width = Math.Max(1, picture.Width / 2);
        var height = Math.Max(1, picture.Height / 2);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var channel = 0; channel < 4; channel++)
                {
                    var sum = 0;
                    for (var dy = 0; dy < 2; dy++)
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var sx = Math.Min(picture.Width - 1, x * 2 + dx);
                            var sy = Math.Min(picture.Height - 1, y * 2 + dy);
                            sum += picture.Pixels[(sy * picture.Width + sx) * 4 + channel];
                        }
                    pixels[(y * width + x) * 4 + channel] = (byte)(sum / 4);
                }
        return new RgbaImage(width, height, pixels);
    }

    private static float[] Average(RgbaImage picture)
    {
        double red = 0, green = 0, blue = 0;
        for (var index = 0; index < picture.Pixels.Length; index += 4)
        {
            red += picture.Pixels[index];
            green += picture.Pixels[index + 1];
            blue += picture.Pixels[index + 2];
        }
        var pixels = Math.Max(1, picture.Pixels.Length / 4) * 255.0;
        return [(float)(red / pixels), (float)(green / pixels), (float)(blue / pixels)];
    }

    /// <summary>A texture decoded to pixels (height maps, blend masks, colour-grading tables), in memory.</summary>
    private async Task<RgbaImage?> DecodePictureAsync(string packagePath, string alias, AssetCatalogEntry texture, int mip,
        CancellationToken cancellationToken, bool opaque = true)
    {
        if (textureExport is null) return null;
        try
        {
            var report = await new TextureReconnaissanceService(protectionProvider: protectionProvider).InspectAsync(
                new TextureReconnaissanceRequest(packagePath, alias, texture.ExportIndex - 1, AssetKind.Texture, texture.ClassName,
                    texture.ObjectName, texture.SerialOffset, texture.SerialSize, KnownPackageSha256: NoHash), cancellationToken).ConfigureAwait(false);
            var picture = await textureExport.DecodePreviewAsync(report, mip == 0 ? int.MaxValue : mip, cancellationToken, opaque)
                .ConfigureAwait(false);
            return mip == 0 ? picture : Shrink(picture, mip);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or
                                              ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A copy no larger than <paramref name="size"/> across, averaging the pixels it merges.</summary>
    public static RgbaImage Shrink(RgbaImage picture, int size)
    {
        var factor = (int)Math.Ceiling(Math.Max(picture.Width, picture.Height) / (double)size);
        if (factor <= 1) return picture;
        var width = Math.Max(1, picture.Width / factor);
        var height = Math.Max(1, picture.Height / factor);
        var pixels = new byte[width * height * 4];
        Span<int> sum = stackalloc int[4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                sum.Clear();
                for (var dy = 0; dy < factor; dy++)
                {
                    var row = (y * factor + dy) * picture.Width;
                    for (var dx = 0; dx < factor; dx++)
                    {
                        var source = (row + x * factor + dx) * 4;
                        for (var channel = 0; channel < 4; channel++) sum[channel] += picture.Pixels[source + channel];
                    }
                }
                var target = (y * width + x) * 4;
                for (var channel = 0; channel < 4; channel++) pixels[target + channel] = (byte)(sum[channel] / (factor * factor));
            }
        return new RgbaImage(width, height, pixels);
    }

    [GeneratedRegex("(diffuse|basecolou?r|base_colou?r|albedo)", RegexOptions.IgnoreCase)]
    private static partial Regex DiffuseParameter();

    [GeneratedRegex(@"_(d|da|diff|diffuse)(_|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DiffuseTexture();

    [GeneratedRegex("(normal|mask|spec|rough|metal|emiss|height|weight|_n$|noise|flow|overlay|detail|reflection|panning|cube)", RegexOptions.IgnoreCase)]
    private static partial Regex NotColour();

    [GeneratedRegex("^(diffuse_?colou?r|basecolou?r|base_colou?r|colou?r|tint|diffuse_tint)$", RegexOptions.IgnoreCase)]
    private static partial Regex DiffuseColour();

    [GeneratedRegex("^(diffuse_?brightness|brightness|base_?brightness)$", RegexOptions.IgnoreCase)]
    private static partial Regex DiffuseBrightness();

    [GeneratedRegex("^texture_emissive$|^emissive(_texture|_tex|map)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GlowParameter();

    [GeneratedRegex("^(texture_normal|normal|normal_?map|normal_texture)$", RegexOptions.IgnoreCase)]
    private static partial Regex NormalParameter();

    [GeneratedRegex("^emissive_?colou?r$", RegexOptions.IgnoreCase)]
    private static partial Regex GlowColour();

    [GeneratedRegex("^emissive_?(intensity|brightness|power)$", RegexOptions.IgnoreCase)]
    private static partial Regex GlowStrength();

    // Cut-out materials: foliage, grass, hair, chains and fences draw their texture's alpha as holes.
    [GeneratedRegex("grass|leaf|leaves|tree|bush|plant|flower|foliage|ivy|vine|fern|weed|hair|chain|fence|net_|_net|masked")]
    private static partial Regex CutOut();

    // ---- Output ----

    /// <summary>
    /// Where the play area is: of the environment volumes' centres, the terrain's middle and the busiest 300-metre
    /// square, the one with the most solid pieces around it (cinematic sets, skies and backdrops stand apart).
    /// </summary>
    private (Vector3 Centre, float Size) Focus(IReadOnlyDictionary<string, DecodedMesh> meshes, IReadOnlyList<Vector3> environments)
    {
        const float reach = 450;
        var solid = _placements.Where(placement => !placement.Cutscene && meshes.ContainsKey(placement.Mesh) &&
                                                   Layer(placement.Mesh, placement.Materials.OfType<string>().ToArray()) == "solid")
            .Select(placement => new Vector3(placement.World.M41, placement.World.M42, placement.World.M43))
            .Where(point => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z)).ToArray();
        var candidates = new List<Vector3>(environments);
        if (!_tiles.IsEmpty)
        {
            var low = _tiles.SelectMany(tile => tile.Positions).Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var high = _tiles.SelectMany(tile => tile.Positions).Aggregate(new Vector3(float.MinValue), Vector3.Max);
            candidates.Add((low + high) / 2);
        }
        if (solid.Length > 0)
        {
            const float cell = 300;
            var best = solid.GroupBy(point => (MathF.Floor(point.X / cell), MathF.Floor(point.Z / cell)))
                .OrderByDescending(group => group.Count()).First();
            candidates.Add(best.Aggregate(Vector3.Zero, (sum, point) => sum + point) / best.Count());
        }
        if (candidates.Count == 0) return (Vector3.Zero, 100);
        var centre = candidates.OrderByDescending(candidate => solid.Count(point => Flat(point - candidate) < reach)).First();
        var near = solid.Where(point => Flat(point - centre) < reach).ToArray();
        if (near.Length == 0) return (centre, 200);
        // The pieces' middle height, so the camera looks at the ground the map stands on.
        var heights = near.Select(point => point.Y).Order().ToArray();
        var middle = new Vector3(centre.X, heights[heights.Length / 2], centre.Z);
        var spread = near.Select(point => Flat(point - middle)).Order().ToArray();
        return (middle, Math.Max(60, spread[(int)(spread.Length * 0.9)] * 2));

        static float Flat(Vector3 offset) => MathF.Sqrt(offset.X * offset.X + offset.Z * offset.Z);
    }

    private ZoneMapScene Write(ZoneMap map, string directory, IReadOnlyDictionary<string, DecodedMesh> meshes,
        IReadOnlyDictionary<string, ZoneSceneMaterial> materials, IReadOnlyDictionary<string, IReadOnlyList<ZoneSceneLayer>> layers,
        IReadOnlyDictionary<string, (int Page, Vector4 Remap)> baked, IReadOnlyList<ZoneSceneEnvironment> environments, int environment,
        (Vector3 Centre, float Size) focus)
    {
        var materialList = materials.Values.OrderBy(material => material.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var materialIndex = materialList.Select((material, index) => (material.Key, index))
            .ToDictionary(item => item.Key, item => item.index, StringComparer.OrdinalIgnoreCase);
        var meshList = new List<ZoneSceneMesh>();
        var meshIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var batches = new List<ZoneSceneBatch>();
        var terrain = new List<ZoneSceneTerrain>();
        var placements = 0;
        using var data = new BinaryWriter(File.Create(Path.Combine(directory, ZoneMapScene.DataFileName)));
        foreach (var (key, mesh) in meshes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            meshIndex[key] = meshList.Count;
            meshList.Add(new ZoneSceneMesh(key, WriteVectors(data, mesh.Positions), WriteNormals(data, mesh.Normals), WriteUvs(data, mesh.Uvs),
                WriteIndices(data, mesh.Indices.Select(index => (uint)index)), mesh.Sections, mesh.Materials)
            {
                LightmapUvs = mesh.LightmapUvs is { } lightmapUvs ? WriteUvs(data, lightmapUvs) : null,
            });
        }
        foreach (var group in _placements.Where(placement => meshes.ContainsKey(placement.Mesh))
                     .GroupBy(placement => placement.Mesh + "|" + placement.Cutscene + "|" + string.Join(",", Slots(placement)) + "|" +
                                           (Baked(placement) is { } key ? baked[key].Page : -1)))
        {
            var first = group.First();
            var lightmap = Baked(first) is { } bakedKey ? baked[bakedKey].Page : -1;
            var slots = Slots(first).Select(slot => slot is null ? -1 : materialIndex.GetValueOrDefault(slot, -1)).ToArray();
            var offset = data.BaseStream.Position;
            var count = 0;
            foreach (var placement in group)
            {
                var world = placement.World;
                if (!float.IsFinite(world.M41) || !float.IsFinite(world.M42) || !float.IsFinite(world.M43)) continue;
                foreach (var value in new[] { world.M11, world.M12, world.M13, world.M14, world.M21, world.M22, world.M23, world.M24,
                             world.M31, world.M32, world.M33, world.M34, world.M41, world.M42, world.M43, world.M44 })
                    data.Write(value);
                count++;
            }
            placements += count;
            ZoneSceneRange? places = null;
            if (lightmap >= 0)
            {
                places = new ZoneSceneRange(data.BaseStream.Position, count);
                foreach (var placement in group.Where(placement => float.IsFinite(placement.World.M41) && float.IsFinite(placement.World.M42) &&
                                                                   float.IsFinite(placement.World.M43)))
                {
                    // Its place on its own lightmap, moved to where that lightmap sits on the page.
                    var place = placement.Lightmap!.Place;
                    var remap = baked[placement.Lightmap.Key].Remap;
                    data.Write(place.X * remap.X);
                    data.Write(place.Y * remap.Y);
                    data.Write(place.Z * remap.X + remap.Z);
                    data.Write(place.W * remap.Y + remap.W);
                }
            }
            var layer = Layer(first.Mesh, Slots(first).OfType<string>().ToArray());
            batches.Add(new ZoneSceneBatch(meshIndex[first.Mesh], slots, new ZoneSceneRange(offset, count),
                first.Cutscene && layer == "solid" ? "cutscene" : layer) { Lightmap = lightmap, LightmapPlaces = places });
        }
        foreach (var tile in _tiles)
        {
            var positions = WriteVectors(data, tile.Positions);
            var uvs = WriteUvs(data, tile.Uvs);
            var indices = WriteIndices(data, tile.Indices);
            var known = tile.Material is not null && layers.TryGetValue(tile.Material, out var materialLayers) ? materialLayers : [];
            // The tile's layers in its own order, each as its material sets it.
            var tileLayers = tile.Layers.Select(name => known.FirstOrDefault(layer => layer.Name == name) ??
                                                        new ZoneSceneLayer(name, -1, null, [0.55f, 0.55f, 0.55f], 1)).ToArray();
            ZoneSceneRange? weights = null;
            if (tile.Weights is { } values && tileLayers.Length > 0 && known.Count > 0)
            {
                weights = new ZoneSceneRange(data.BaseStream.Position, values.Length);
                data.Write(values);
                Align(data);
            }
            terrain.Add(new ZoneSceneTerrain(positions, uvs, indices, tile.Material is null ? -1 : materialIndex.GetValueOrDefault(tile.Material, -1),
                weights is null ? [] : tileLayers, weights));
        }
        var scene = new ZoneMapScene(ZoneMapScene.CurrentVersion, map.Key, Array(focus.Centre), focus.Size, meshList, _textures.ToArray(),
            materialList, batches, terrain, environments, environment, placements,
            _placements.Select(placement => placement.Mesh).Distinct(StringComparer.OrdinalIgnoreCase).Count(key => !meshes.ContainsKey(key)));
        return scene;

        // The lightmap a placement is drawn with: one the scene kept, on a mesh with lightmap coordinates.
        string? Baked(Placement placement) =>
            placement.Lightmap is { } light && baked.ContainsKey(light.Key) && meshes[placement.Mesh].LightmapUvs is not null ? light.Key : null;

        IEnumerable<string?> Slots(Placement placement) =>
            meshes[placement.Mesh].Materials.Select((slot, index) => index < placement.Materials.Length && placement.Materials[index] is { } chosen ? chosen : slot);
    }

    /// <summary>Which of the 3D map's layers a mesh with these materials belongs to (see <see cref="ZoneSceneBatch.Layer"/>).</summary>
    public static string Layer(string mesh, IReadOnlyList<string> materials)
    {
        string Name(string key) => key[(key.IndexOf('.') + 1)..].ToLowerInvariant();
        string Package(string key) => key[..Math.Max(0, key.IndexOf('.'))].ToLowerInvariant();
        if (SkyName().IsMatch(Name(mesh)) || SkyPackage().IsMatch(Package(mesh)) ||
            materials.Any(material => SkyName().IsMatch(Name(material)))) return "sky";
        if (EffectPackage().IsMatch(Package(mesh)) || EffectMesh().IsMatch(Name(mesh)) ||
            materials.Any(material => EffectPackage().IsMatch(Package(material)) || BlendedMaterial().IsMatch(Name(material))))
            return "effect";
        return "solid";
    }

    // Skies, clouds, planets and moons in the distance, and painted backdrops (lv_matte).
    [GeneratedRegex("sky|cloud|planet|moon|_matte|matte_|backdrop|horizon|skybox")]
    private static partial Regex SkyName();

    [GeneratedRegex("^(lv_matte|lv_dynamicsky|lv_sky)")]
    private static partial Regex SkyPackage();

    [GeneratedRegex("^(b?fx_|efmaster|fx$)")]
    private static partial Regex EffectPackage();

    [GeneratedRegex("water|fog|mist|glow|light|shaft|beam|hemisphere|aura|smoke|flare|ray_|_ray")]
    private static partial Regex EffectMesh();

    // Effect materials are named for how they blend: _tr translucent, _ad additive (bfx_d_me_ringmaster_01_03_tr_02).
    [GeneratedRegex("(_tr|_ad|_add|_trans)(_\\d+)*$|_tr_|_ad_")]
    private static partial Regex BlendedMaterial();

    private static ZoneSceneRange WriteVectors(BinaryWriter data, IReadOnlyList<Vector3> values)
    {
        var offset = data.BaseStream.Position;
        foreach (var value in values)
        {
            data.Write(value.X);
            data.Write(value.Y);
            data.Write(value.Z);
        }
        return new ZoneSceneRange(offset, values.Count);
    }

    // Normals as signed bytes (x, y, z and a pad byte): plenty for lighting, a third the size of floats.
    private static ZoneSceneRange? WriteNormals(BinaryWriter data, IReadOnlyList<Vector3> values)
    {
        if (values.Count == 0) return null;
        var offset = data.BaseStream.Position;
        foreach (var value in values)
        {
            var normal = value.LengthSquared() > 0 ? Vector3.Normalize(value) : Vector3.UnitY;
            data.Write((sbyte)Math.Round(normal.X * 127));
            data.Write((sbyte)Math.Round(normal.Y * 127));
            data.Write((sbyte)Math.Round(normal.Z * 127));
            data.Write((sbyte)0);
        }
        return new ZoneSceneRange(offset, values.Count);
    }

    // Texture coordinates as half floats: plenty at these texture sizes, and half the size.
    private static ZoneSceneRange WriteUvs(BinaryWriter data, IReadOnlyList<Vector2> values)
    {
        var offset = data.BaseStream.Position;
        foreach (var value in values)
        {
            data.Write((Half)value.X);
            data.Write((Half)value.Y);
        }
        Align(data);
        return new ZoneSceneRange(offset, values.Count);
    }

    // Every mesh and tile has fewer than 65,536 vertices, so 16-bit indices.
    private static ZoneSceneRange WriteIndices(BinaryWriter data, IEnumerable<uint> values)
    {
        var offset = data.BaseStream.Position;
        var count = 0;
        foreach (var value in values)
        {
            data.Write(checked((ushort)value));
            count++;
        }
        Align(data);
        return new ZoneSceneRange(offset, count);
    }

    // Float arrays read straight from the file need 4-byte aligned offsets.
    private static void Align(BinaryWriter data)
    {
        while (data.BaseStream.Position % 4 != 0) data.Write((byte)0);
    }
}
