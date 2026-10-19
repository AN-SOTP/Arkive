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

/// <summary>One mesh of the map, at its least detailed level of detail (a 3D overview needs no more).</summary>
/// <param name="Materials">The mesh's own materials (package.object) by slot; a placement can override them.</param>
public sealed record ZoneSceneMesh(string Key, ZoneSceneRange Positions, ZoneSceneRange Uvs, ZoneSceneRange Indices,
    IReadOnlyList<ZoneSceneSection> Sections, IReadOnlyList<string?> Materials);

/// <param name="Texture">A small copy of the material's colour texture (a file beside the scene), or null.</param>
/// <param name="TextureKey">The texture's package.object, for tinting it with the textures the user picked.</param>
public sealed record ZoneSceneMaterial(string Key, float[] Colour, string? Texture, string? TextureKey);

/// <summary>Every placement of one mesh with the same materials: one 4x4 matrix each (row-major, viewer space).</summary>
/// <param name="Materials">Indices into <see cref="ZoneMapScene.Materials"/>, one per mesh slot (-1 for none).</param>
/// <param name="Layer">"solid" for the map itself, "sky" for sky domes and cloud planes, "effect" for glows, water and
/// light shafts (see off by default: drawn solid they would cover the map).</param>
public sealed record ZoneSceneBatch(int Mesh, IReadOnlyList<int> Materials, ZoneSceneRange Matrices, string Layer = "solid");

/// <summary>One ground layer of a landscape tile: its colour (texture average x layer colour x brightness).</summary>
/// <param name="Texture">The layer's colour texture (package.object), so picking it in the list tints this layer.</param>
public sealed record ZoneSceneLayer(string Name, float[] Colour, string? Texture);

/// <summary>A landscape tile: a height grid already in viewer space, drawn with its layer material.</summary>
/// <param name="Layers">The ground layers the tile blends, when its blend masks could be read.</param>
/// <param name="Weights">Each vertex's weight of each layer (bytes, vertex by vertex, layer by layer).</param>
public sealed record ZoneSceneTerrain(ZoneSceneRange Positions, ZoneSceneRange Uvs, ZoneSceneRange Indices, int Material,
    IReadOnlyList<ZoneSceneLayer>? Layers = null, ZoneSceneRange? Weights = null);

/// <summary>
/// A whole map for the Zone Viewer's 3D view: its meshes (decoded by Arkive itself, least detailed level), where the
/// levels place each one (static meshes, collections and foliage), the landscape's height grids, and each material
/// as a colour and a 64-pixel texture. Saved as scene.json, scene.bin and small PNGs in one folder (5 to 30 MB).
/// Distances are metres, Y up, as in the Asset Viewer. In scene.bin positions and matrices are 32-bit floats,
/// texture coordinates 16-bit floats and indices 16-bit integers, each range starting on a 4-byte boundary.
/// </summary>
public sealed record ZoneMapScene(
    int Version,
    string Map,
    float[] Minimum,
    float[] Maximum,
    IReadOnlyList<ZoneSceneMesh> Meshes,
    IReadOnlyList<ZoneSceneMaterial> Materials,
    IReadOnlyList<ZoneSceneBatch> Batches,
    IReadOnlyList<ZoneSceneTerrain> Terrain,
    int Placements,
    int UnreadMeshes)
{
    public const int CurrentVersion = 1;
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
    private const int TextureSize = 64;
    // A placeholder for the package hash material and texture reports carry; the scene keeps no report.
    private static readonly string NoHash = new('0', 64);

    private sealed record Placement(string Mesh, string?[] Materials, Matrix4x4 World);

    private sealed record Tile(Vector3[] Positions, Vector2[] Uvs, uint[] Indices, string? Material, string[] Layers, byte[]? Weights);

    private readonly ConcurrentBag<Placement> _placements = [];
    private readonly ConcurrentBag<Tile> _tiles = [];

    public async Task<ZoneMapScene> BuildAsync(ZoneMap map, string directory, IProgress<ZoneSceneProgress>? progress,
        CancellationToken cancellationToken)
    {
        var levels = map.LevelPackages.Where(packageFiles.ContainsKey).ToArray();
        var completed = 0;
        await Parallel.ForEachAsync(levels, Options(cancellationToken), async (alias, token) =>
        {
            try
            {
                await ReadLevelAsync(alias, token).ConfigureAwait(false);
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
        Directory.CreateDirectory(Path.Combine(directory, "t"));
        var textures = new ConcurrentDictionary<string, Lazy<Task<(string? File, float[] Colour)>>>(StringComparer.OrdinalIgnoreCase);
        completed = 0;
        await Parallel.ForEachAsync(materialKeys, Options(cancellationToken), async (key, token) =>
        {
            materials[key] = await ReadMaterialAsync(key, directory, textures, token).ConfigureAwait(false);
            progress?.Report(new ZoneSceneProgress("Reading the materials' colours", Interlocked.Increment(ref completed), materialKeys.Length));
        }).ConfigureAwait(false);

        progress?.Report(new ZoneSceneProgress("Colouring the ground layers", 0, 0));
        var layers = new Dictionary<string, IReadOnlyList<ZoneSceneLayer>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _tiles.Where(tile => tile.Layers.Length > 0).Select(tile => tile.Material).OfType<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            layers[key] = await ReadLayersAsync(key, directory, textures, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ZoneSceneProgress("Saving the map", 0, 0));
        return Write(map, directory, meshes, materials, layers);
    }

    private static ParallelOptions Options(CancellationToken cancellationToken) =>
        new() { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 6), CancellationToken = cancellationToken };

    // ---- Placements ----

    private async Task ReadLevelAsync(string alias, CancellationToken cancellationToken)
    {
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
            if (!type.EndsWith("staticmeshcomponent", StringComparison.Ordinal)) continue;
            var properties = LevelProperties.Read(Bytes(logical, asset), catalog.Names, out var end);
            if (properties is null || properties.Bool("hiddengame")) continue;
            if (Reference(catalog, properties.Object("staticmesh"), alias) is not { } mesh || IsHelper(mesh)) continue;
            var overrides = properties.Objects("materials").Select(reference => Reference(catalog, reference, alias)).ToArray();
            var local = ComponentTransform(properties);
            var outer = catalog.Exports[asset.ExportIndex - 1].OuterIndex;
            if (!actors.TryGetValue(outer, out var actor))
                actors[outer] = actor = byIndex.TryGetValue(outer, out var owner) ? ActorTransform(catalog, logical, owner) : Matrix4x4.Identity;
            if (actor is null) continue;
            if (type.Contains("instanced", StringComparison.Ordinal))
            {
                var bytes = Bytes(logical, asset);
                foreach (var instance in Instances(bytes.AsSpan(end)))
                    _placements.Add(new Placement(mesh, overrides, ToViewer(instance * local * actor.Value)));
            }
            else _placements.Add(new Placement(mesh, overrides, ToViewer(local * actor.Value)));
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
        var scale = (properties.Vector("drawscale3d") ?? Vector3.One) * (properties.Float("drawscale") ?? 1);
        return Matrix4x4.CreateScale(scale) * Rotation(properties.Rotator("rotation")) *
               Matrix4x4.CreateTranslation(properties.Vector("location") ?? Vector3.Zero);
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

    /// <summary>
    /// A foliage component's instances: after its properties, an array of 80-byte entries (a 4x4 matrix relative to
    /// the component, then lightmap and shadow map offsets), found by its element size and count.
    /// </summary>
    public static IEnumerable<Matrix4x4> Instances(ReadOnlySpan<byte> tail)
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
            var matrices = new Matrix4x4[count];
            for (var index = 0; index < count; index++)
            {
                var floats = data.Slice(index * entry, 64);
                matrices[index] = new Matrix4x4(
                    F(floats, 0), F(floats, 1), F(floats, 2), F(floats, 3), F(floats, 4), F(floats, 5), F(floats, 6), F(floats, 7),
                    F(floats, 8), F(floats, 9), F(floats, 10), F(floats, 11), F(floats, 12), F(floats, 13), F(floats, 14), F(floats, 15));
            }
            return matrices;
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
        var picture = await DecodeTextureAsync(packageFiles[alias], alias, heightmap, mip: 0, cancellationToken).ConfigureAwait(false);
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
                var local = new Vector3(baseX + x, baseY + y, height / 128f);
                // The same swap and scale the mesh decoder gives a vertex, then the landscape actor's placement.
                positions[y * size + x] = Vector3.Transform(new Vector3(local.X, local.Z, local.Y) * 0.01f, world);
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
                pictures[index] = await DecodeTextureAsync(packageFiles[alias], alias, texture, 0, cancellationToken, opaque: false).ConfigureAwait(false);
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

    // ---- Meshes ----

    private sealed record DecodedMesh(Vector3[] Positions, Vector2[] Uvs, ushort[] Indices, IReadOnlyList<ZoneSceneSection> Sections,
        IReadOnlyList<string?> Materials);

    private void ReadMeshes(IReadOnlyList<string> keys, ConcurrentDictionary<string, DecodedMesh> meshes, CancellationToken cancellationToken)
    {
        var first = keys.Select(key => indexed.GetValueOrDefault(key)).FirstOrDefault(item => item.Document is not null);
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
            if (!indexed.TryGetValue(key, out var found) || found.Document.SourcePath != first.Document.SourcePath ||
                !found.Asset.ClassName.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var bytes = new byte[found.Asset.SerialSize];
                logical.Position = found.Asset.SerialOffset;
                logical.ReadExactly(bytes);
                foreach (var candidate in Ue3PropertyStreamProbe.FindCandidates(bytes, found.Asset.SerialOffset, catalog.Names))
                {
                    if (!parser.TryParse(bytes, found.Asset.SerialOffset, candidate.EndOffset, out var layout, out _) || layout is null) continue;
                    var lod = layout.Lods[^1];
                    var decoded = new StaticMeshSemanticDecoder().DecodeOne(bytes, lod);
                    var slots = lod.Sections.Select(section => Reference(catalog, section.MaterialReference, found.Document.PackageAlias))
                        .ToArray();
                    meshes[key] = new DecodedMesh(decoded.Positions, decoded.TextureCoordinates.Length > 0 ? decoded.TextureCoordinates[0] : new Vector2[decoded.Positions.Length],
                        decoded.Indices, lod.Sections.Select((section, index) => new ZoneSceneSection(section.FirstIndex, section.TriangleCount * 3, index)).ToArray(),
                        slots);
                    break;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or
                                                  ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            {
            }
        }
    }

    // ---- Materials ----

    /// <summary>
    /// A landscape material's ground layers (layer01_diffuse, layer01_color, layer01_brightness and so on): each
    /// layer's colour is its texture's average, tinted by its colour and scaled by its brightness.
    /// </summary>
    private async Task<IReadOnlyList<ZoneSceneLayer>> ReadLayersAsync(string key, string directory,
        ConcurrentDictionary<string, Lazy<Task<(string? File, float[] Colour)>>> textures, CancellationToken cancellationToken)
    {
        if (await InspectMaterialAsync(key, cancellationToken).ConfigureAwait(false) is not { } report) return [];
        var package = indexed[key].Document.PackageAlias;
        var result = new List<ZoneSceneLayer>();
        foreach (var texture in report.TextureParameters)
        {
            var name = texture.Name.ToLowerInvariant();
            if (!name.EndsWith("_diffuse", StringComparison.Ordinal) || ZoneParts.ReferencedName(texture.ResolvedObject) is not { } objectName) continue;
            var layer = name[..^"_diffuse".Length];
            var textureKey = FindTexture(package, objectName);
            float[] colour = [0.55f, 0.55f, 0.55f];
            if (textureKey is not null)
                (_, colour) = await textures.GetOrAdd(textureKey, item => new Lazy<Task<(string?, float[])>>(() =>
                    SmallTextureAsync(item, directory, cancellationToken))).Value.ConfigureAwait(false);
            var tint = report.VectorParameters.FirstOrDefault(vector => vector.Name.Equals(layer + "_color", StringComparison.OrdinalIgnoreCase));
            var brightness = report.ScalarParameters.FirstOrDefault(scalar => scalar.Name.Equals(layer + "_brightness", StringComparison.OrdinalIgnoreCase))?.Value ?? 1;
            result.Add(new ZoneSceneLayer(layer, [
                Math.Clamp(colour[0] * (tint?.Red ?? 1) * brightness, 0, 1),
                Math.Clamp(colour[1] * (tint?.Green ?? 1) * brightness, 0, 1),
                Math.Clamp(colour[2] * (tint?.Blue ?? 1) * brightness, 0, 1)], textureKey?.ToLowerInvariant()));
        }
        return result;
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

    private async Task<ZoneSceneMaterial> ReadMaterialAsync(string key, string directory,
        ConcurrentDictionary<string, Lazy<Task<(string? File, float[] Colour)>>> textures, CancellationToken cancellationToken)
    {
        float[] grey = [0.55f, 0.55f, 0.55f];
        if (await InspectMaterialAsync(key, cancellationToken).ConfigureAwait(false) is not { } report)
            return new ZoneSceneMaterial(key, grey, null, null);
        var tint = report.VectorParameters.FirstOrDefault(vector => ColourParameter().IsMatch(vector.Name));
        // An instance that sets no colour texture draws with its parent's; follow a few parents up.
        string? textureKey = null;
        var current = (Key: key, Report: report);
        for (var depth = 0; depth < 4; depth++)
        {
            var package = indexed[current.Key].Document.PackageAlias;
            var textureName = current.Report.TextureParameters
                .OrderBy(texture => DiffuseParameter().IsMatch(texture.Name) ? 0 : DiffuseTexture().IsMatch(ZoneParts.ReferencedName(texture.ResolvedObject) ?? "") ? 1 : 2)
                .Where(texture => !NotColour().IsMatch(texture.Name))
                .Select(texture => ZoneParts.ReferencedName(texture.ResolvedObject))
                .FirstOrDefault(name => name is not null);
            textureKey = textureName is null ? null : FindTexture(package, textureName);
            if (textureKey is not null || current.Report.ParentObject is not { } parent ||
                !parent.StartsWith("materialinstanceconstant'", StringComparison.OrdinalIgnoreCase) ||
                ZoneParts.ReferencedName(parent) is not { } parentName ||
                FindMaterial(package, parentName) is not { } parentKey ||
                await InspectMaterialAsync(parentKey, cancellationToken).ConfigureAwait(false) is not { } parentReport) break;
            current = (parentKey, parentReport);
        }
        if (textureKey is null)
            return new ZoneSceneMaterial(key, tint is null ? grey : [Math.Clamp(tint.Red, 0, 1), Math.Clamp(tint.Green, 0, 1), Math.Clamp(tint.Blue, 0, 1)], null, null);
        var (file, colour) = await textures.GetOrAdd(textureKey, texture => new Lazy<Task<(string?, float[])>>(() =>
            SmallTextureAsync(texture, directory, cancellationToken))).Value.ConfigureAwait(false);
        return new ZoneSceneMaterial(key, colour, file, textureKey);
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

    private int _textureCount;

    private async Task<(string? File, float[] Colour)> SmallTextureAsync(string key, string directory, CancellationToken cancellationToken)
    {
        float[] grey = [0.55f, 0.55f, 0.55f];
        if (!indexed.TryGetValue(key, out var found)) return (null, grey);
        var asset = new AssetCatalogEntry(found.Asset.ExportIndex, found.Asset.ObjectName, found.Asset.ObjectName, found.Asset.ClassName,
            found.Asset.Kind, (int)found.Asset.SerialSize, (int)found.Asset.SerialOffset);
        var picture = await DecodeTextureAsync(found.Document.SourcePath, found.Document.PackageAlias, asset, TextureSize, cancellationToken)
            .ConfigureAwait(false);
        if (picture is null) return (null, grey);
        double red = 0, green = 0, blue = 0;
        for (var index = 0; index < picture.Pixels.Length; index += 4)
        {
            red += picture.Pixels[index];
            green += picture.Pixels[index + 1];
            blue += picture.Pixels[index + 2];
        }
        var pixels = picture.Pixels.Length / 4 * 255.0;
        var file = $"t/{Interlocked.Increment(ref _textureCount)}.png";
        await PngRgbaCodec.WriteAsync(Path.Combine(directory, file), picture, cancellationToken).ConfigureAwait(false);
        return (file, [(float)(red / pixels), (float)(green / pixels), (float)(blue / pixels)]);
    }

    /// <summary>
    /// A texture decoded by Arkive's own reader, at the first level of detail no larger than <paramref name="mip"/>
    /// pixels across (0 for the full picture), in memory.
    /// </summary>
    private async Task<RgbaImage?> DecodeTextureAsync(string packagePath, string alias, AssetCatalogEntry texture, int mip,
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

    [GeneratedRegex("(normal|mask|spec|rough|metal|emiss|height|weight|_n$|noise|flow)", RegexOptions.IgnoreCase)]
    private static partial Regex NotColour();

    [GeneratedRegex("^(diffuse_?colou?r|basecolou?r|colou?r|tint|diffuse_tint)$", RegexOptions.IgnoreCase)]
    private static partial Regex ColourParameter();

    // ---- Output ----

    private ZoneMapScene Write(ZoneMap map, string directory, IReadOnlyDictionary<string, DecodedMesh> meshes,
        IReadOnlyDictionary<string, ZoneSceneMaterial> materials, IReadOnlyDictionary<string, IReadOnlyList<ZoneSceneLayer>> layers)
    {
        var materialList = materials.Values.OrderBy(material => material.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var materialIndex = materialList.Select((material, index) => (material.Key, index))
            .ToDictionary(item => item.Key, item => item.index, StringComparer.OrdinalIgnoreCase);
        var meshList = new List<ZoneSceneMesh>();
        var meshIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var batches = new List<ZoneSceneBatch>();
        var terrain = new List<ZoneSceneTerrain>();
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);
        var placements = 0;
        using var data = new BinaryWriter(File.Create(Path.Combine(directory, ZoneMapScene.DataFileName)));
        foreach (var (key, mesh) in meshes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            meshIndex[key] = meshList.Count;
            meshList.Add(new ZoneSceneMesh(key, WriteVectors(data, mesh.Positions), WriteUvs(data, mesh.Uvs),
                WriteIndices(data, mesh.Indices.Select(index => (uint)index)), mesh.Sections, mesh.Materials));
        }
        foreach (var group in _placements.Where(placement => meshes.ContainsKey(placement.Mesh))
                     .GroupBy(placement => placement.Mesh + "|" + string.Join(",", Slots(placement))))
        {
            var first = group.First();
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
                var at = new Vector3(world.M41, world.M42, world.M43);
                minimum = Vector3.Min(minimum, at);
                maximum = Vector3.Max(maximum, at);
                count++;
            }
            placements += count;
            batches.Add(new ZoneSceneBatch(meshIndex[first.Mesh], slots, new ZoneSceneRange(offset, count),
                Layer(first.Mesh, Slots(first).OfType<string>().ToArray())));
        }
        foreach (var tile in _tiles)
        {
            foreach (var position in tile.Positions)
            {
                minimum = Vector3.Min(minimum, position);
                maximum = Vector3.Max(maximum, position);
            }
            var positions = WriteVectors(data, tile.Positions);
            var uvs = WriteUvs(data, tile.Uvs);
            var indices = WriteIndices(data, tile.Indices);
            var known = tile.Material is not null && layers.TryGetValue(tile.Material, out var materialLayers) ? materialLayers : [];
            // The tile's layers in its own order, each with the colour its material gives that layer.
            var tileLayers = tile.Layers.Select(name => known.FirstOrDefault(layer => layer.Name == name) ?? new ZoneSceneLayer(name, [0.55f, 0.55f, 0.55f], null)).ToArray();
            ZoneSceneRange? weights = null;
            if (tile.Weights is { } values && tileLayers.Length > 0 && known.Count > 0)
            {
                weights = new ZoneSceneRange(data.BaseStream.Position, values.Length);
                data.Write(values);
                Align(data);
            }
            terrain.Add(new ZoneSceneTerrain(positions, uvs, indices, tile.Material is null ? -1 : materialIndex.GetValueOrDefault(tile.Material, -1),
                weights is null ? null : tileLayers, weights));
        }
        if (minimum.X > maximum.X) (minimum, maximum) = (Vector3.Zero, Vector3.Zero);
        var scene = new ZoneMapScene(ZoneMapScene.CurrentVersion, map.Key, [minimum.X, minimum.Y, minimum.Z], [maximum.X, maximum.Y, maximum.Z],
            meshList, materialList, batches, terrain, placements, _placements.Select(placement => placement.Mesh)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(key => !meshes.ContainsKey(key)));
        File.WriteAllText(Path.Combine(directory, ZoneMapScene.FileName), JsonSerializer.Serialize(scene, ZoneMapScene.Json));
        return scene;

        IEnumerable<string?> Slots(Placement placement) =>
            meshes[placement.Mesh].Materials.Select((slot, index) => index < placement.Materials.Length && placement.Materials[index] is { } chosen ? chosen : slot);
    }

    /// <summary>Which of the 3D map's layers a mesh with these materials belongs to (see <see cref="ZoneSceneBatch.Layer"/>).</summary>
    public static string Layer(string mesh, IReadOnlyList<string> materials)
    {
        string Name(string key) => key[(key.IndexOf('.') + 1)..].ToLowerInvariant();
        string Package(string key) => key[..Math.Max(0, key.IndexOf('.'))].ToLowerInvariant();
        if (SkyName().IsMatch(Name(mesh)) || materials.Any(material => SkyName().IsMatch(Name(material)))) return "sky";
        if (EffectPackage().IsMatch(Package(mesh)) || EffectMesh().IsMatch(Name(mesh)) ||
            materials.Any(material => EffectPackage().IsMatch(Package(material)) || BlendedMaterial().IsMatch(Name(material))))
            return "effect";
        return "solid";
    }

    [GeneratedRegex("sky|cloud")]
    private static partial Regex SkyName();

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

    // Texture coordinates as half floats: plenty for 64-pixel textures, and half the size.
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
