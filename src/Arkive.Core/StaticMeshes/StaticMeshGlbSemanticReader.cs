using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Arkive.Core.StaticMeshes;

internal sealed record StaticMeshGlbVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector4 Tangent,
    IReadOnlyList<Vector2> TextureCoordinates,
    Vector4? Colour,
    uint? StableVertexId);

internal sealed record StaticMeshGlbSection(
    int SectionIndex,
    string MaterialName,
    IReadOnlyList<uint> Indices);

internal sealed record StaticMeshGlbLod(
    int LodIndex,
    IReadOnlyList<StaticMeshGlbVertex> Vertices,
    IReadOnlyList<StaticMeshGlbSection> Sections,
    bool StableIdentityPresent,
    int TextureCoordinateCount,
    Vector3 Minimum,
    Vector3 Maximum);

internal sealed record StaticMeshGlbDocument(
    IReadOnlyDictionary<int, StaticMeshGlbLod> Lods,
    IReadOnlyList<string> MaterialNames,
    bool MeshNodeTransformsIdentity);

internal sealed class StaticMeshGlbSemanticReader
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;
    private const float StableSentinel = 0.625f;

    private readonly byte[] _binary;
    private readonly JsonElement _root;
    private readonly JsonElement _accessors;
    private readonly JsonElement _views;
    private readonly IReadOnlyDictionary<int, StaticMeshEditableLodManifest> _sourceLods;

    private StaticMeshGlbSemanticReader(
        byte[] binary,
        JsonElement root,
        IReadOnlyDictionary<int, StaticMeshEditableLodManifest> sourceLods)
    {
        _binary = binary;
        _root = root;
        _accessors = root.GetProperty("accessors");
        _views = root.GetProperty("bufferViews");
        _sourceLods = sourceLods;
    }

    public static StaticMeshGlbDocument Read(
        string path,
        StaticMeshEditableExportManifest sourceManifest)
    {
        var bytes = File.ReadAllBytes(Path.GetFullPath(path));
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != GlbMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != bytes.Length)
            Refuse("STI100", "The candidate is not a complete glTF 2.0 GLB.");
        JsonElement? root = null;
        byte[]? binary = null;
        var offset = 12;
        while (offset <= bytes.Length - 8)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            offset += 8;
            if (length < 0 || offset > bytes.Length - length) Refuse("STI101", "A GLB chunk is truncated.");
            if (kind == JsonChunk)
            {
                using var document = JsonDocument.Parse(bytes.AsMemory(offset, length));
                root = document.RootElement.Clone();
            }
            else if (kind == BinChunk)
            {
                binary = bytes.AsSpan(offset, length).ToArray();
            }
            offset += length;
        }
        if (root is null || binary is null) Refuse("STI102", "The GLB must contain JSON and embedded BIN chunks.");
        if (!root.Value.TryGetProperty("buffers", out var buffers) || buffers.GetArrayLength() != 1 ||
            buffers[0].TryGetProperty("uri", out _))
            Refuse("STI103", "Only one embedded GLB buffer is supported.");
        return new StaticMeshGlbSemanticReader(
            binary!, root.Value, sourceManifest.Lods.ToDictionary(lod => lod.LodIndex)).ReadDocument();
    }

    private StaticMeshGlbDocument ReadDocument()
    {
        if (!_root.TryGetProperty("meshes", out var meshes) || meshes.GetArrayLength() == 0)
            Refuse("STI104", "The candidate has no mesh data.");
        var materials = _root.TryGetProperty("materials", out var materialArray)
            ? materialArray.EnumerateArray().Select((item, index) =>
                item.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString())
                    ? name.GetString()!
                    : $"material_{index:D2}").ToArray()
            : [];
        var transformsIdentity = ValidateMeshNodeTransforms();
        var lods = new Dictionary<int, StaticMeshGlbLod>();
        for (var meshIndex = 0; meshIndex < meshes.GetArrayLength(); meshIndex++)
        {
            var mesh = meshes[meshIndex];
            var lodIndex = ResolveLodIndex(mesh, meshIndex, meshes.GetArrayLength());
            if (!_sourceLods.TryGetValue(lodIndex, out var source))
                continue;
            if (lods.ContainsKey(lodIndex)) Refuse("STI105", $"More than one editable mesh resolves to LOD {lodIndex}.");
            lods.Add(lodIndex, ReadLod(mesh, source, materials));
        }
        if (lods.Count == 0) Refuse("STI106", "No candidate mesh maps to an Arkive StaticMesh LOD.");
        return new StaticMeshGlbDocument(lods, materials, transformsIdentity);
    }

    private StaticMeshGlbLod ReadLod(
        JsonElement mesh,
        StaticMeshEditableLodManifest source,
        IReadOnlyList<string> materials)
    {
        if (!mesh.TryGetProperty("primitives", out var primitives) || primitives.GetArrayLength() == 0)
            Refuse("STI110", $"LOD {source.LodIndex} has no primitives.");
        var vertices = new List<StaticMeshGlbVertex>();
        var stableVertices = new Dictionary<uint, StaticMeshGlbVertex>();
        var vertexKeys = new Dictionary<VertexKey, uint>();
        var sections = new List<StaticMeshGlbSection>();
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var stablePresent = true;
        var nextVertex = 0u;
        var sectionOrdinal = 0;
        foreach (var primitive in primitives.EnumerateArray())
        {
            if (primitive.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4)
                Refuse("STI111", $"LOD {source.LodIndex} primitive {sectionOrdinal} is not triangles.");
            var attributes = primitive.GetProperty("attributes");
            var positionAccessor = RequireAccessor(attributes, "POSITION", source.LodIndex);
            var normalAccessor = RequireAccessor(attributes, "NORMAL", source.LodIndex);
            var tangentAccessor = RequireAccessor(attributes, "TANGENT", source.LodIndex);
            var positions = ReadFloatAccessor(positionAccessor, 3, "POSITION");
            var normals = ReadFloatAccessor(normalAccessor, 3, "NORMAL");
            var tangents = ReadFloatAccessor(tangentAccessor, 4, "TANGENT");
            var count = positions.Length / 3;
            if (normals.Length / 3 != count || tangents.Length / 4 != count)
                Refuse("STI112", $"LOD {source.LodIndex} primitive attributes have different vertex counts.");
            var uvAccessors = new List<int>();
            for (var uvIndex = 0; ; uvIndex++)
            {
                if (!attributes.TryGetProperty($"TEXCOORD_{uvIndex}", out var uv)) break;
                uvAccessors.Add(uv.GetInt32());
            }
            if (uvAccessors.Count < source.TextureCoordinateCount)
                Refuse("STI113", $"LOD {source.LodIndex} retains {uvAccessors.Count} UV channel(s); {source.TextureCoordinateCount} are required.");
            var uvValues = uvAccessors.Take(source.TextureCoordinateCount)
                .Select((accessor, channel) => ReadFloatAccessor(accessor, 2, $"TEXCOORD_{channel}"))
                .ToArray();
            if (uvValues.Any(values => values.Length / 2 != count))
                Refuse("STI114", $"LOD {source.LodIndex} UV channels have different vertex counts.");
            var colours = attributes.TryGetProperty("COLOR_0", out var colourElement)
                ? ReadColourAccessor(colourElement.GetInt32())
                : null;
            if (colours is not null && colours.Length / 4 != count)
                Refuse("STI115", $"LOD {source.LodIndex} colour count differs from its vertex count.");

            var stableIds = TryReadStableIds(attributes, source, count);
            stablePresent &= stableIds is not null;
            var localToGlobal = new uint[count];
            for (var local = 0; local < count; local++)
            {
                var position = new Vector3(positions[local * 3], positions[local * 3 + 1], positions[local * 3 + 2]);
                var normal = Normalize(new Vector3(normals[local * 3], normals[local * 3 + 1], normals[local * 3 + 2]),
                    "STI116", $"LOD {source.LodIndex} vertex {local} normal");
                var tangentVector = new Vector3(tangents[local * 4], tangents[local * 4 + 1], tangents[local * 4 + 2]);
                tangentVector = Normalize(tangentVector, "STI117", $"LOD {source.LodIndex} vertex {local} tangent");
                var tangentW = tangents[local * 4 + 3];
                if (!float.IsFinite(tangentW) || MathF.Abs(tangentW) < 0.5f)
                    Refuse("STI118", $"LOD {source.LodIndex} vertex {local} has invalid tangent handedness.");
                RequireFinite(position, "STI119", $"LOD {source.LodIndex} vertex {local} position");
                var textureCoordinates = uvValues.Select(values =>
                    new Vector2(values[local * 2], values[local * 2 + 1])).ToArray();
                foreach (var uv in textureCoordinates)
                    if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
                        Refuse("STI120", $"LOD {source.LodIndex} vertex {local} has a non-finite UV.");
                Vector4? colour = colours is null
                    ? null
                    : new Vector4(colours[local * 4], colours[local * 4 + 1],
                        colours[local * 4 + 2], colours[local * 4 + 3]);
                var stableId = stableIds?[local];
                var vertex = new StaticMeshGlbVertex(
                    position, normal, new Vector4(tangentVector, tangentW < 0 ? -1f : 1f),
                    textureCoordinates, colour, stableId);
                minimum = Vector3.Min(minimum, position);
                maximum = Vector3.Max(maximum, position);
                if (stableId is not null)
                {
                    if (stableVertices.TryGetValue(stableId.Value, out var prior) && !Equivalent(prior, vertex))
                        Refuse("STI121", $"LOD {source.LodIndex} stable vertex {stableId} has conflicting data across primitives.");
                    stableVertices[stableId.Value] = vertex;
                    localToGlobal[local] = stableId.Value;
                }
                else
                {
                    var key = new VertexKey(positionAccessor, normalAccessor, tangentAccessor,
                        uvAccessors.Count == 0 ? -1 : uvAccessors[0], local);
                    if (!vertexKeys.TryGetValue(key, out var global))
                    {
                        global = nextVertex++;
                        vertexKeys.Add(key, global);
                        vertices.Add(vertex);
                    }
                    localToGlobal[local] = global;
                }
            }

            var sourceIndices = ReadIndices(primitive.GetProperty("indices").GetInt32());
            if (sourceIndices.Count == 0 || sourceIndices.Count % 3 != 0)
                Refuse("STI122", $"LOD {source.LodIndex} primitive {sectionOrdinal} has an invalid triangle index count.");
            var indices = new uint[sourceIndices.Count];
            for (var index = 0; index < indices.Length; index++)
            {
                if (sourceIndices[index] >= count)
                    Refuse("STI123", $"LOD {source.LodIndex} primitive {sectionOrdinal} index {index} is out of range.");
                indices[index] = localToGlobal[sourceIndices[index]];
            }
            var materialIndex = primitive.TryGetProperty("material", out var material) ? material.GetInt32() : -1;
            var materialName = materialIndex >= 0 && materialIndex < materials.Count
                ? materials[materialIndex]
                : $"material_{materialIndex}";
            var sectionIndex = primitive.TryGetProperty("extras", out var extras) &&
                               extras.TryGetProperty("arkiveSection", out var section)
                ? section.GetInt32()
                : sectionOrdinal;
            sections.Add(new StaticMeshGlbSection(sectionIndex, materialName, indices));
            sectionOrdinal++;
        }

        if (stablePresent)
        {
            var maximumId = stableVertices.Keys.DefaultIfEmpty().Max();
            if (maximumId >= ushort.MaxValue) Refuse("STI124", "Stable StaticMesh vertex IDs exceed the UE3 index limit.");
            vertices = new List<StaticMeshGlbVertex>(checked((int)maximumId + 1));
            for (uint id = 0; id <= maximumId; id++)
            {
                if (!stableVertices.TryGetValue(id, out var vertex))
                    Refuse("STI125", $"LOD {source.LodIndex} is missing stable vertex ID {id}.");
                vertices.Add(vertex);
            }
        }
        if (vertices.Count == 0 || vertices.Count > ushort.MaxValue)
            Refuse("STI126", $"LOD {source.LodIndex} has {vertices.Count:N0} compiled vertices; UE3 StaticMesh3 supports 1-65,535.");
        return new StaticMeshGlbLod(
            source.LodIndex, vertices, sections.OrderBy(section => section.SectionIndex).ToArray(),
            stablePresent, source.TextureCoordinateCount, minimum, maximum);
    }

    private uint[]? TryReadStableIds(
        JsonElement attributes,
        StaticMeshEditableLodManifest source,
        int count)
    {
        if (attributes.TryGetProperty("_ARKIVE_VERTEX_ID", out var direct))
        {
            var values = ReadUnsignedAccessor(direct.GetInt32());
            if (values.Count == count) return values.ToArray();
        }
        if (attributes.TryGetProperty($"TEXCOORD_{source.StableVertexIdTextureCoordinate}", out var carrier))
        {
            var values = ReadFloatAccessor(carrier.GetInt32(), 2, "stable vertex carrier");
            if (values.Length / 2 != count) return null;
            var ids = new uint[count];
            for (var index = 0; index < count; index++)
            {
                var id = values[index * 2];
                var sentinel = values[index * 2 + 1];
                if (!float.IsFinite(id) || MathF.Abs(id - MathF.Round(id)) > 0.001f || id < 0 ||
                    MathF.Abs(sentinel - StableSentinel) > 0.001f)
                    return null;
                ids[index] = checked((uint)MathF.Round(id));
            }
            return ids;
        }
        return null;
    }

    private bool ValidateMeshNodeTransforms()
    {
        if (!_root.TryGetProperty("nodes", out var nodes)) return true;
        var result = true;
        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("mesh", out _)) continue;
            result &= NodeTransformIsIdentity(node);
        }
        return result;
    }

    private static bool NodeTransformIsIdentity(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix))
        {
            var values = matrix.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            var identity = new[] { 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f };
            if (values.Length != 16 || values.Where((value, index) => MathF.Abs(value - identity[index]) > 1e-5f).Any())
                return false;
        }
        if (node.TryGetProperty("translation", out var translation) &&
            translation.EnumerateArray().Any(value => MathF.Abs(value.GetSingle()) > 1e-5f)) return false;
        if (node.TryGetProperty("rotation", out var rotation))
        {
            var values = rotation.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            if (values.Length != 4 || MathF.Abs(values[0]) > 1e-5f || MathF.Abs(values[1]) > 1e-5f ||
                MathF.Abs(values[2]) > 1e-5f || MathF.Abs(MathF.Abs(values[3]) - 1f) > 1e-5f) return false;
        }
        if (node.TryGetProperty("scale", out var scale) &&
            scale.EnumerateArray().Any(value => MathF.Abs(value.GetSingle() - 1f) > 1e-5f)) return false;
        return true;
    }

    private static int ResolveLodIndex(JsonElement mesh, int meshIndex, int meshCount)
    {
        if (mesh.TryGetProperty("extras", out var extras) &&
            extras.TryGetProperty("arkiveLod", out var lod)) return lod.GetInt32();
        if (mesh.TryGetProperty("name", out var name))
        {
            var match = Regex.Match(name.GetString() ?? string.Empty, @"_LOD(?<v>\d+)", RegexOptions.IgnoreCase);
            if (match.Success) return int.Parse(match.Groups["v"].Value);
        }
        return meshCount == 1 ? 0 : meshIndex;
    }

    private static int RequireAccessor(JsonElement attributes, string name, int lodIndex)
    {
        if (!attributes.TryGetProperty(name, out var accessor))
            Refuse("STI130", $"LOD {lodIndex} is missing {name}.");
        return accessor.GetInt32();
    }

    private float[] ReadFloatAccessor(int accessorIndex, int components, string label)
    {
        var accessor = Accessor(accessorIndex);
        if (Components(accessor) != components)
            Refuse("STI131", $"{label} has the wrong component count.");
        var count = accessor.GetProperty("count").GetInt32();
        var componentType = accessor.GetProperty("componentType").GetInt32();
        var normalized = accessor.TryGetProperty("normalized", out var normal) && normal.GetBoolean();
        var result = new float[checked(count * components)];
        for (var item = 0; item < count; item++)
        for (var component = 0; component < components; component++)
            result[item * components + component] = ReadComponentAsFloat(
                accessor, item, component, componentType, normalized);
        return result;
    }

    private float[] ReadColourAccessor(int accessorIndex)
    {
        var accessor = Accessor(accessorIndex);
        var components = Components(accessor);
        if (components is not 3 and not 4) Refuse("STI132", "COLOR_0 must be VEC3 or VEC4.");
        var count = accessor.GetProperty("count").GetInt32();
        var type = accessor.GetProperty("componentType").GetInt32();
        var normalized = accessor.TryGetProperty("normalized", out var normal) && normal.GetBoolean();
        var result = new float[checked(count * 4)];
        for (var item = 0; item < count; item++)
        {
            for (var component = 0; component < components; component++)
                result[item * 4 + component] = ReadComponentAsFloat(accessor, item, component, type, normalized);
            if (components == 3) result[item * 4 + 3] = 1f;
        }
        return result;
    }

    private IReadOnlyList<uint> ReadIndices(int accessorIndex) => ReadUnsignedAccessor(accessorIndex);

    private IReadOnlyList<uint> ReadUnsignedAccessor(int accessorIndex)
    {
        var accessor = Accessor(accessorIndex);
        if (Components(accessor) != 1) Refuse("STI133", "An index/ID accessor must be SCALAR.");
        var count = accessor.GetProperty("count").GetInt32();
        var type = accessor.GetProperty("componentType").GetInt32();
        if (type is not 5121 and not 5123 and not 5125)
            Refuse("STI134", "An index/ID accessor must use an unsigned integer component.");
        var result = new uint[count];
        for (var item = 0; item < count; item++) result[item] = ReadUnsigned(accessor, item, 0, type);
        return result;
    }

    private JsonElement Accessor(int index)
    {
        if (index < 0 || index >= _accessors.GetArrayLength()) Refuse("STI135", $"Accessor {index} is out of range.");
        var accessor = _accessors[index];
        if (accessor.TryGetProperty("sparse", out _)) Refuse("STI136", "Sparse GLB accessors are not supported.");
        return accessor;
    }

    private float ReadComponentAsFloat(
        JsonElement accessor,
        int item,
        int component,
        int componentType,
        bool normalized)
    {
        var offset = ComponentOffset(accessor, item, component, componentType);
        return componentType switch
        {
            5120 => normalized ? Math.Max(-1f, (sbyte)_binary[offset] / 127f) : (sbyte)_binary[offset],
            5121 => normalized ? _binary[offset] / 255f : _binary[offset],
            5122 => normalized ? Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(_binary.AsSpan(offset)) / 32767f) : BinaryPrimitives.ReadInt16LittleEndian(_binary.AsSpan(offset)),
            5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(_binary.AsSpan(offset)) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(_binary.AsSpan(offset)),
            5125 => BinaryPrimitives.ReadUInt32LittleEndian(_binary.AsSpan(offset)),
            5126 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_binary.AsSpan(offset))),
            _ => throw new StaticMeshRefusalException("STI137", $"Unsupported GLB component type {componentType}."),
        };
    }

    private uint ReadUnsigned(JsonElement accessor, int item, int component, int type)
    {
        var offset = ComponentOffset(accessor, item, component, type);
        return type switch
        {
            5121 => _binary[offset],
            5123 => BinaryPrimitives.ReadUInt16LittleEndian(_binary.AsSpan(offset)),
            5125 => BinaryPrimitives.ReadUInt32LittleEndian(_binary.AsSpan(offset)),
            _ => throw new StaticMeshRefusalException("STI138", $"Unsupported unsigned type {type}."),
        };
    }

    private int ComponentOffset(JsonElement accessor, int item, int component, int componentType)
    {
        if (!accessor.TryGetProperty("bufferView", out var viewIndexElement))
            Refuse("STI139", "Accessors without buffer views are not supported.");
        var viewIndex = viewIndexElement.GetInt32();
        if (viewIndex < 0 || viewIndex >= _views.GetArrayLength()) Refuse("STI140", "A buffer view is out of range.");
        var view = _views[viewIndex];
        if (view.TryGetProperty("buffer", out var buffer) && buffer.GetInt32() != 0)
            Refuse("STI141", "Only embedded buffer 0 is supported.");
        var bytesPerComponent = ComponentBytes(componentType);
        var componentCount = Components(accessor);
        var elementBytes = checked(bytesPerComponent * componentCount);
        var stride = view.TryGetProperty("byteStride", out var byteStride) ? byteStride.GetInt32() : elementBytes;
        if (stride < elementBytes) Refuse("STI142", "A GLB byte stride is smaller than its element.");
        var offset = checked(
            (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) +
            (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0) +
            item * stride + component * bytesPerComponent);
        if (offset < 0 || offset > _binary.Length - bytesPerComponent)
            Refuse("STI143", "An accessor exceeds the embedded GLB buffer.");
        return offset;
    }

    private static int Components(JsonElement accessor) => accessor.GetProperty("type").GetString() switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        _ => throw new StaticMeshRefusalException("STI144", "Unsupported GLB accessor shape."),
    };

    private static int ComponentBytes(int type) => type switch
    {
        5120 or 5121 => 1,
        5122 or 5123 => 2,
        5125 or 5126 => 4,
        _ => throw new StaticMeshRefusalException("STI145", $"Unsupported GLB component type {type}."),
    };

    private static Vector3 Normalize(Vector3 value, string code, string label)
    {
        var length = value.LengthSquared();
        if (!float.IsFinite(length) || length < 1e-12f) Refuse(code, label + " is zero or non-finite.");
        return Vector3.Normalize(value);
    }

    private static void RequireFinite(Vector3 value, string code, string label)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            Refuse(code, label + " is non-finite.");
    }

    private static bool Equivalent(StaticMeshGlbVertex left, StaticMeshGlbVertex right)
    {
        if (Vector3.DistanceSquared(left.Position, right.Position) > 1e-10f ||
            Vector3.DistanceSquared(left.Normal, right.Normal) > 1e-8f ||
            Vector4.DistanceSquared(left.Tangent, right.Tangent) > 1e-8f ||
            left.TextureCoordinates.Count != right.TextureCoordinates.Count) return false;
        return !left.TextureCoordinates.Where((value, index) =>
            Vector2.DistanceSquared(value, right.TextureCoordinates[index]) > 1e-10f).Any();
    }

    [DoesNotReturn]
    private static void Refuse(string code, string message) => throw new StaticMeshRefusalException(code, message);

    private readonly record struct VertexKey(
        int PositionAccessor,
        int NormalAccessor,
        int TangentAccessor,
        int FirstUvAccessor,
        int LocalIndex);
}
