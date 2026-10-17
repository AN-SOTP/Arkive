using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Arkive.Core.Meshes;

internal sealed record SkeletalMeshGlbVertex(
    Vector3 Position,
    Vector3? Normal,
    Vector4? Tangent,
    IReadOnlyList<Vector2> TextureCoordinates,
    IReadOnlyDictionary<int, float> SkinWeights,
    Vector4? Colour,
    int InfluenceCount,
    float OriginalSkinWeightSum);

internal readonly record struct SkeletalMeshGlbTriangle(
    uint A,
    uint B,
    uint C,
    string Material);

internal readonly record struct SkeletalMeshGlbTrianglePositions(Vector3 A, Vector3 B, Vector3 C);

internal sealed record SkeletalMeshGlbLod(
    int LodIndex,
    IReadOnlyDictionary<uint, SkeletalMeshGlbVertex> Vertices,
    IReadOnlyDictionary<SkeletalMeshGlbTriangle, int> Triangles,
    int TriangleCount,
    int PrimitiveCount,
    string StableVertexIdCarrier,
    int TextureCoordinateCount,
    bool HasNormals,
    bool HasTangents,
    bool HasColours,
    IReadOnlySet<string> UsedMaterials)
{
    public int MaximumInfluenceCount { get; init; }
    public double MaximumSkinWeightSumError { get; init; }
    public int DegenerateTriangleCount { get; init; }
    public int IgnoredMorphTargetCount { get; init; }
    public IReadOnlyList<SkeletalMeshGlbTrianglePositions> DegenerateTriangles { get; init; } = [];
    public int FaceOrientationTriangleCount { get; init; }
    public int OpposedFaceNormalTriangleCount { get; init; }
    public SkeletalMeshEditableBounds Bounds { get; init; } = new(0, 0, 0, 0, 0, 0);
}

internal sealed record SkeletalMeshGlbSkeleton(
    IReadOnlyDictionary<int, int> ParentByBone,
    IReadOnlyDictionary<int, float[]> InverseBindByBone,
    IReadOnlyDictionary<int, string> NameByBone);

internal sealed record SkeletalMeshGlbSemanticDocument(
    IReadOnlyDictionary<int, SkeletalMeshGlbLod> Lods,
    SkeletalMeshGlbSkeleton Skeleton,
    int IgnoredUnskinnedMeshCount)
{
    public bool MeshNodeTransformsIdentity { get; init; } = true;
    public IReadOnlyList<string> MaterialNames { get; init; } = [];
}

internal enum SkeletalMeshGlbReadProfile
{
    StableVertexIdentity,
    TopologyCandidate,
}

internal sealed partial class SkeletalMeshGlbSemanticReader
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;
    private const float StableIdSentinel = 0.375f;
    private const float StableIdAlternateSentinel = 0.625f;

    private readonly byte[] _binary;
    private readonly JsonElement _root;
    private readonly JsonElement _accessors;
    private readonly JsonElement _views;
    private readonly JsonElement _nodes;
    private readonly JsonElement _materials;
    private readonly IReadOnlyList<string> _materialNames;
    private readonly int[] _parents;
    private readonly IReadOnlyDictionary<int, SkeletalMeshEditableLodManifest> _sourceLods;
    private readonly SkeletalMeshGlbReadProfile _profile;

    private SkeletalMeshGlbSemanticReader(
        byte[] binary,
        JsonElement root,
        IReadOnlyDictionary<int, SkeletalMeshEditableLodManifest> sourceLods,
        SkeletalMeshGlbReadProfile profile)
    {
        _binary = binary;
        _root = root;
        _accessors = root.GetProperty("accessors");
        _views = root.GetProperty("bufferViews");
        _nodes = root.GetProperty("nodes");
        _materials = root.TryGetProperty("materials", out var materials)
            ? materials
            : default;
        _materialNames = ReadMaterialNames();
        _sourceLods = sourceLods;
        _profile = profile;
        _parents = Enumerable.Repeat(-1, _nodes.GetArrayLength()).ToArray();
        for (var index = 0; index < _nodes.GetArrayLength(); index++)
        {
            var node = _nodes[index];
            if (!node.TryGetProperty("children", out var children) ||
                children.ValueKind != JsonValueKind.Array) continue;
            foreach (var child in children.EnumerateArray())
            {
                var childIndex = child.GetInt32();
                if (childIndex < 0 || childIndex >= _parents.Length)
                    throw Error("SMI110", $"Node {index} has an out-of-range child {childIndex}.");
                if (_parents[childIndex] >= 0)
                    throw Error("SMI111", $"Node {childIndex} has more than one parent.");
                _parents[childIndex] = index;
            }
        }
    }

    public static SkeletalMeshGlbSemanticDocument Read(
        string path,
        SkeletalMeshEditableExportManifest sourceManifest,
        SkeletalMeshGlbReadProfile profile = SkeletalMeshGlbReadProfile.StableVertexIdentity)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != GlbMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != bytes.Length)
            throw Error("SMI100", "The file is not a complete glTF 2.0 GLB.");
        JsonElement? root = null;
        byte[]? binary = null;
        var offset = 12;
        while (offset <= bytes.Length - 8)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            offset += 8;
            if (length < 0 || offset > bytes.Length - length)
                throw Error("SMI101", "A GLB chunk is truncated.");
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
        if (root is null || binary is null)
            throw Error("SMI102", "The GLB does not contain both JSON and embedded binary chunks.");
        if (!root.Value.TryGetProperty("buffers", out var buffers) ||
            buffers.GetArrayLength() != 1 ||
            buffers[0].TryGetProperty("uri", out _))
            throw Error("SMI103", "Only a single embedded Arkive GLB buffer is supported.");
        var sourceLods = sourceManifest.Lods.ToDictionary(lod => lod.LodIndex);
        return new SkeletalMeshGlbSemanticReader(binary, root.Value, sourceLods, profile).ReadDocument();
    }

    private SkeletalMeshGlbSemanticDocument ReadDocument()
    {
        if (!_root.TryGetProperty("meshes", out var meshes) ||
            !_root.TryGetProperty("skins", out var skins) || skins.GetArrayLength() == 0)
            throw Error("SMI104", "The candidate has no skinned mesh data.");
        var meshSkins = new Dictionary<int, int>();
        var meshNodeTransformsIdentity = true;
        for (var nodeIndex = 0; nodeIndex < _nodes.GetArrayLength(); nodeIndex++)
        {
            var node = _nodes[nodeIndex];
            if (!node.TryGetProperty("mesh", out var meshElement) ||
                !node.TryGetProperty("skin", out var skinElement)) continue;
            meshNodeTransformsIdentity &= NodeTransformIsIdentity(node);
            var mesh = meshElement.GetInt32();
            var skin = skinElement.GetInt32();
            if (meshSkins.TryGetValue(mesh, out var prior) && prior != skin)
                throw Error("SMI105", $"Mesh {mesh} is instanced with multiple skins.");
            meshSkins[mesh] = skin;
        }
        var ignored = meshes.GetArrayLength() - meshSkins.Count;
        var lods = new Dictionary<int, SkeletalMeshGlbLod>();
        int? commonSkin = null;
        uint nextSyntheticVertexId = 0;
        foreach (var (meshIndex, skinIndex) in meshSkins.OrderBy(pair => pair.Key))
        {
            if (meshIndex < 0 || meshIndex >= meshes.GetArrayLength() ||
                skinIndex < 0 || skinIndex >= skins.GetArrayLength())
                throw Error("SMI106", "A mesh or skin index is out of range.");
            commonSkin ??= skinIndex;
            if (commonSkin != skinIndex)
            {
                if (_profile == SkeletalMeshGlbReadProfile.StableVertexIdentity ||
                    !SkeletonEquivalent(ReadSkeleton(skins[commonSkin.Value]), ReadSkeleton(skins[skinIndex])))
                    throw Error("SMI107",
                        "All editable skinned objects must use the same reference skeleton contract.");
            }
            var mesh = meshes[meshIndex];
            var lodIndex = ReadLodIndex(
                mesh, meshIndex, throwOnFailure: _profile == SkeletalMeshGlbReadProfile.StableVertexIdentity);
            if (lodIndex < 0 && _profile == SkeletalMeshGlbReadProfile.TopologyCandidate)
                lodIndex = 0;
            if (!_sourceLods.TryGetValue(lodIndex, out var sourceLod))
                throw Error("SMI108", $"Candidate contains unexpected skinned LOD {lodIndex}.");
            var parsed = ReadLod(mesh, lodIndex, sourceLod, skins[skinIndex], ref nextSyntheticVertexId);
            if (lods.TryGetValue(lodIndex, out var priorLod) &&
                _profile == SkeletalMeshGlbReadProfile.StableVertexIdentity)
                throw Error("SMI109", $"Candidate contains more than one skinned mesh for LOD {lodIndex}.");
            if (priorLod is null) lods.Add(lodIndex, parsed);
            else lods[lodIndex] = MergeTopologyLods(priorLod, parsed);
        }
        if (commonSkin is null) throw Error("SMI104", "The candidate has no skinned mesh nodes.");
        return new SkeletalMeshGlbSemanticDocument(
            lods,
            ReadSkeleton(skins[commonSkin.Value]),
            Math.Max(0, ignored))
        {
            MeshNodeTransformsIdentity = meshNodeTransformsIdentity,
            MaterialNames = _materialNames,
        };
    }

    private SkeletalMeshGlbLod ReadLod(
        JsonElement mesh,
        int lodIndex,
        SkeletalMeshEditableLodManifest sourceLod,
        JsonElement skin,
        ref uint nextSyntheticVertexId)
    {
        if (!mesh.TryGetProperty("primitives", out var primitives) || primitives.GetArrayLength() == 0)
            throw Error("SMI120", $"LOD {lodIndex} contains no primitives.");
        var vertices = new Dictionary<uint, SkeletalMeshGlbVertex>();
        var duplicateCounts = new Dictionary<uint, int>();
        var triangles = new Dictionary<SkeletalMeshGlbTriangle, int>();
        var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalTriangles = 0;
        var maximumInfluences = 0;
        var maximumWeightSumError = 0d;
        var degenerateTriangles = 0;
        var degenerateTrianglePositions = new List<SkeletalMeshGlbTrianglePositions>();
        var orientedTriangles = 0;
        var opposedTriangles = 0;
        string? carrierDescription = null;
        var uvCount = -1;
        bool? hasNormals = null;
        bool? hasTangents = null;
        bool? hasColours = null;
        var morphTargets = 0;
        // Objects joined from different sources may differ in UV maps, colours and tangents;
        // the topology compiler fits every vertex to the game mesh's streams.
        var topologyCandidate = _profile == SkeletalMeshGlbReadProfile.TopologyCandidate;
        foreach (var primitive in primitives.EnumerateArray())
        {
            if (primitive.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4)
                throw Error("SMI121", $"LOD {lodIndex} contains a non-triangle primitive.");
            // Shape keys have no counterpart in the game mesh: the base shape is imported and they are ignored.
            if (primitive.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
                morphTargets += targets.GetArrayLength();
            var attributes = primitive.GetProperty("attributes");
            if (attributes.TryGetProperty("JOINTS_1", out _) ||
                attributes.TryGetProperty("WEIGHTS_1", out _))
                throw Error("SMI131",
                    $"LOD {lodIndex} has more than four skin-influence channels; JOINTS_1/WEIGHTS_1 are not supported.");
            var positionAccessor = RequiredAttribute(attributes, "POSITION", lodIndex);
            var count = Accessor(positionAccessor, "VEC3").Count;
            var (stableIds, carrierKey, carrier) =
                _profile == SkeletalMeshGlbReadProfile.StableVertexIdentity
                    ? ReadStableIds(attributes, count, lodIndex, sourceLod)
                    : (ReadSyntheticIds(count, ref nextSyntheticVertexId), "", "compiler-local candidate identities");
            carrierDescription ??= carrier;
            if (!carrierDescription.Equals(carrier, StringComparison.OrdinalIgnoreCase))
                throw Error("SMI123", $"LOD {lodIndex} primitives use inconsistent stable-ID carriers.");
            var reservedCarrierKey = string.IsNullOrWhiteSpace(sourceLod.StableVertexIdUvSha256)
                ? null
                : $"TEXCOORD_{sourceLod.StableVertexIdTextureCoordinate}";
            var uvKeys = attributes.EnumerateObject()
                .Where(property => property.Name.StartsWith("TEXCOORD_", StringComparison.Ordinal) &&
                                   !property.Name.Equals(carrierKey, StringComparison.Ordinal) &&
                                   !property.Name.Equals(reservedCarrierKey, StringComparison.Ordinal))
                .OrderBy(property => TextureCoordinateNumber(property.Name))
                .ToArray();
            if (uvCount >= 0 && uvCount != uvKeys.Length && !topologyCandidate)
                throw Error("SMI124", $"LOD {lodIndex} primitives have inconsistent UV channel counts.");
            uvCount = Math.Max(uvCount, uvKeys.Length);
            var primitiveHasNormals = attributes.TryGetProperty("NORMAL", out var normalAccessor);
            var primitiveHasTangents = attributes.TryGetProperty("TANGENT", out var tangentAccessor);
            var primitiveHasColours = attributes.TryGetProperty("COLOR_0", out var colourAccessor);
            if ((hasNormals is { } normalsSoFar && normalsSoFar != primitiveHasNormals) ||
                (!topologyCandidate && ((hasTangents is { } tangentsSoFar && tangentsSoFar != primitiveHasTangents) ||
                                        (hasColours is { } coloursSoFar && coloursSoFar != primitiveHasColours))))
                throw Error("SMI125", $"LOD {lodIndex} primitives have inconsistent semantic attributes.");
            hasNormals ??= primitiveHasNormals;
            hasTangents = (hasTangents ?? true) && primitiveHasTangents;
            hasColours = (hasColours ?? false) || primitiveHasColours;
            var jointAccessor = RequiredAttribute(attributes, "JOINTS_0", lodIndex);
            var weightAccessor = RequiredAttribute(attributes, "WEIGHTS_0", lodIndex);
            var jointMap = ReadJointMap(skin);
            for (var vertex = 0; vertex < count; vertex++)
            {
                var id = stableIds[vertex];
                var position = ReadVector3(positionAccessor, vertex);
                EnsureFinite(position, "position", lodIndex, id);
                Vector3? normal = primitiveHasNormals ? ReadVector3(normalAccessor.GetInt32(), vertex) : null;
                Vector4? tangent = primitiveHasTangents ? ReadVector4(tangentAccessor.GetInt32(), vertex) : null;
                if (normal is { } actualNormal) EnsureFinite(actualNormal, "normal", lodIndex, id);
                if (tangent is { } actualTangent) EnsureFinite(actualTangent, "tangent", lodIndex, id);
                var uvs = uvKeys.Select(key => ReadVector2(key.Value.GetInt32(), vertex)).ToArray();
                foreach (var uv in uvs) EnsureFinite(uv, "UV", lodIndex, id);
                var joints = ReadUnsignedVector4(jointAccessor, vertex);
                var weights = ReadVector4(weightAccessor, vertex);
                var influences = new Dictionary<int, float>();
                for (var influence = 0; influence < 4; influence++)
                {
                    var weight = weights[influence];
                    if (!float.IsFinite(weight) || weight < -1e-6f)
                        throw Error("SMI126", $"LOD {lodIndex} vertex {id} has an invalid skin weight.");
                    if (weight <= 1e-7f) continue;
                    if (!jointMap.TryGetValue(checked((int)joints[influence]), out var bone))
                        throw Error("SMI127", $"LOD {lodIndex} vertex {id} references an unmapped joint.");
                    influences[bone] = influences.GetValueOrDefault(bone) + weight;
                }
                var sum = influences.Values.Sum();
                if (sum <= 1e-7f)
                    throw Error("SMI128", $"LOD {lodIndex} vertex {id} has no effective skin weight.");
                maximumInfluences = Math.Max(maximumInfluences, influences.Count);
                maximumWeightSumError = Math.Max(maximumWeightSumError, Math.Abs(sum - 1d));
                foreach (var bone in influences.Keys.ToArray()) influences[bone] /= sum;
                Vector4? colour = primitiveHasColours ? ReadColour(colourAccessor.GetInt32(), vertex) : null;
                if (colour is { } actualColour) EnsureFinite(actualColour, "colour", lodIndex, id);
                var value = new SkeletalMeshGlbVertex(
                    position, normal, tangent, uvs, influences, colour, influences.Count, sum);
                if (vertices.TryGetValue(id, out var duplicate))
                {
                    if (!VertexEquivalentOutsideEditableFrame(duplicate, value))
                        throw Error("SMI129",
                            $"LOD {lodIndex} stable vertex {id} has conflicting duplicated data: " +
                            DescribeVertexDifference(duplicate, value));
                    var countSoFar = duplicateCounts.GetValueOrDefault(id, 1);
                    vertices[id] = MergeEditableFrame(duplicate, value, countSoFar);
                    duplicateCounts[id] = countSoFar + 1;
                }
                else
                {
                    vertices.Add(id, value);
                    duplicateCounts.Add(id, 1);
                }
            }

            var material = MaterialName(primitive);
            materials.Add(material);
            var indices = ReadIndices(primitive, count);
            if (indices.Length % 3 != 0)
                throw Error("SMI130", $"LOD {lodIndex} primitive index count is not divisible by three.");
            for (var index = 0; index < indices.Length; index += 3)
            {
                var a = StableIndex(stableIds, indices[index], lodIndex);
                var b = StableIndex(stableIds, indices[index + 1], lodIndex);
                var c = StableIndex(stableIds, indices[index + 2], lodIndex);
                var triangle = CanonicalTriangle(a, b, c, material);
                triangles[triangle] = triangles.GetValueOrDefault(triangle) + 1;
                totalTriangles++;
                var pa = vertices[a].Position;
                var pb = vertices[b].Position;
                var pc = vertices[c].Position;
                var face = Vector3.Cross(pb - pa, pc - pa);
                if (a == b || b == c || a == c || face.LengthSquared() <= 1e-16f)
                {
                    degenerateTriangles++;
                    degenerateTrianglePositions.Add(new(pa, pb, pc));
                }
                else
                {
                    var normals = new[] { vertices[a].Normal, vertices[b].Normal, vertices[c].Normal };
                    if (normals.All(value => value is not null))
                    {
                        var average = normals.Aggregate(Vector3.Zero,
                            (current, value) => current + value!.Value);
                        if (average.LengthSquared() > 1e-12f)
                        {
                            orientedTriangles++;
                            if (Vector3.Dot(Vector3.Normalize(face), Vector3.Normalize(average)) < -0.05f)
                                opposedTriangles++;
                        }
                    }
                }
            }
        }
        var bounds = Bounds(vertices.Values.Select(vertex => vertex.Position));
        return new SkeletalMeshGlbLod(
            lodIndex, vertices, triangles, totalTriangles, primitives.GetArrayLength(),
            carrierDescription ?? "unresolved", uvCount,
            hasNormals == true, hasTangents == true, hasColours == true, materials)
        {
            MaximumInfluenceCount = maximumInfluences,
            MaximumSkinWeightSumError = maximumWeightSumError,
            DegenerateTriangleCount = degenerateTriangles,
            DegenerateTriangles = degenerateTrianglePositions,
            IgnoredMorphTargetCount = morphTargets,
            FaceOrientationTriangleCount = orientedTriangles,
            OpposedFaceNormalTriangleCount = opposedTriangles,
            Bounds = bounds,
        };
    }

    private static uint[] ReadSyntheticIds(int count, ref uint next)
    {
        if (count < 0 || (ulong)next + (ulong)count > uint.MaxValue)
            throw Error("SMI132", "The topology candidate exceeds Arkive's bounded vertex-identity space.");
        var result = new uint[count];
        for (var index = 0; index < count; index++) result[index] = next++;
        return result;
    }

    private static SkeletalMeshGlbLod MergeTopologyLods(
        SkeletalMeshGlbLod left,
        SkeletalMeshGlbLod right)
    {
        if (left.HasNormals != right.HasNormals)
            throw Error("SMI133",
                $"LOD {left.LodIndex} joins skinned objects where only some have vertex normals; export normals for every object.");
        var vertices = left.Vertices.Concat(right.Vertices).ToDictionary(pair => pair.Key, pair => pair.Value);
        var triangles = left.Triangles.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var pair in right.Triangles)
            triangles[pair.Key] = triangles.GetValueOrDefault(pair.Key) + pair.Value;
        var materials = left.UsedMaterials.Concat(right.UsedMaterials)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SkeletalMeshGlbLod(
            left.LodIndex, vertices, triangles,
            checked(left.TriangleCount + right.TriangleCount),
            checked(left.PrimitiveCount + right.PrimitiveCount),
            "compiler-local candidate identities",
            Math.Max(left.TextureCoordinateCount, right.TextureCoordinateCount),
            left.HasNormals,
            left.HasTangents && right.HasTangents,
            left.HasColours || right.HasColours,
            materials)
        {
            MaximumInfluenceCount = Math.Max(left.MaximumInfluenceCount, right.MaximumInfluenceCount),
            MaximumSkinWeightSumError = Math.Max(
                left.MaximumSkinWeightSumError, right.MaximumSkinWeightSumError),
            DegenerateTriangleCount = checked(left.DegenerateTriangleCount + right.DegenerateTriangleCount),
            DegenerateTriangles = [.. left.DegenerateTriangles, .. right.DegenerateTriangles],
            IgnoredMorphTargetCount = checked(left.IgnoredMorphTargetCount + right.IgnoredMorphTargetCount),
            FaceOrientationTriangleCount = checked(
                left.FaceOrientationTriangleCount + right.FaceOrientationTriangleCount),
            OpposedFaceNormalTriangleCount = checked(
                left.OpposedFaceNormalTriangleCount + right.OpposedFaceNormalTriangleCount),
            Bounds = Union(left.Bounds, right.Bounds),
        };
    }

    private static SkeletalMeshEditableBounds Bounds(IEnumerable<Vector3> positions)
    {
        using var enumerator = positions.GetEnumerator();
        if (!enumerator.MoveNext()) return new SkeletalMeshEditableBounds(0, 0, 0, 0, 0, 0);
        var minimum = enumerator.Current;
        var maximum = enumerator.Current;
        while (enumerator.MoveNext())
        {
            minimum = Vector3.Min(minimum, enumerator.Current);
            maximum = Vector3.Max(maximum, enumerator.Current);
        }
        return new SkeletalMeshEditableBounds(
            minimum.X, minimum.Y, minimum.Z, maximum.X, maximum.Y, maximum.Z);
    }

    private static SkeletalMeshEditableBounds Union(
        SkeletalMeshEditableBounds left,
        SkeletalMeshEditableBounds right) => new(
        Math.Min(left.MinimumX, right.MinimumX),
        Math.Min(left.MinimumY, right.MinimumY),
        Math.Min(left.MinimumZ, right.MinimumZ),
        Math.Max(left.MaximumX, right.MaximumX),
        Math.Max(left.MaximumY, right.MaximumY),
        Math.Max(left.MaximumZ, right.MaximumZ));

    private IReadOnlyList<string> ReadMaterialNames()
    {
        if (_materials.ValueKind != JsonValueKind.Array) return [];
        return WithBlenderSuffixes(_materials.EnumerateArray().Select((material, index) =>
            material.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString())
                ? name.GetString()!
                : $"material_{index}").ToArray());
    }

    // Some game meshes give several slots the same material (pc_sp_av_236b_dress_sk uses
    // pc_sp_av_base_body_mi twice). Blender imports the repeats as name.001, name.002, ..., so each
    // repeated slot is known by that name and maps back to its own slot.
    internal static IReadOnlyList<string> WithBlenderSuffixes(IReadOnlyList<string> names)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return names.Select(name =>
        {
            var count = seen.GetValueOrDefault(name);
            seen[name] = count + 1;
            return count == 0 ? name : $"{name}.{count:D3}";
        }).ToArray();
    }

    private static bool NodeTransformIsIdentity(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix))
        {
            var values = matrix.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            var identity = new[] { 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f };
            if (values.Length != identity.Length || values.Where((value, index) =>
                    Math.Abs(value - identity[index]) > 2e-5f).Any()) return false;
        }
        if (node.TryGetProperty("translation", out var translation) &&
            translation.EnumerateArray().Any(value => Math.Abs(value.GetSingle()) > 2e-5f)) return false;
        if (node.TryGetProperty("scale", out var scale) &&
            scale.EnumerateArray().Any(value => Math.Abs(value.GetSingle() - 1f) > 2e-5f)) return false;
        if (node.TryGetProperty("rotation", out var rotation))
        {
            var values = rotation.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            if (values.Length != 4 || Math.Abs(values[0]) > 2e-5f || Math.Abs(values[1]) > 2e-5f ||
                Math.Abs(values[2]) > 2e-5f || Math.Abs(Math.Abs(values[3]) - 1f) > 2e-5f) return false;
        }
        return true;
    }

    private SkeletalMeshGlbSkeleton ReadSkeleton(JsonElement skin)
    {
        var joints = skin.GetProperty("joints").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var jointMap = ReadJointMap(skin);
        if (jointMap.Count != joints.Length)
            throw Error("SMI140", "Reference skeleton stable bone IDs are incomplete or duplicated.");
        var parents = new Dictionary<int, int>();
        var names = new Dictionary<int, string>();
        foreach (var (skinJoint, bone) in jointMap)
        {
            var node = joints[skinJoint];
            var parent = _parents[node];
            var parentBone = -1;
            while (parent >= 0)
            {
                var parentJoint = Array.IndexOf(joints, parent);
                if (parentJoint >= 0 && jointMap.TryGetValue(parentJoint, out parentBone)) break;
                parent = _parents[parent];
            }
            parents[bone] = parentBone;
            var jointNode = _nodes[node];
            var displayName = jointNode.TryGetProperty("extras", out var extras) &&
                              extras.TryGetProperty("nativeBoneName", out var nativeName)
                ? nativeName.GetString()
                : jointNode.TryGetProperty("name", out var nodeName)
                    ? nodeName.GetString()
                    : null;
            names[bone] = string.IsNullOrWhiteSpace(displayName)
                ? $"bone_{bone:D4}"
                : BoneIdRegex().Replace(displayName!, "");
        }
        if (!skin.TryGetProperty("inverseBindMatrices", out var accessorElement))
            throw Error("SMI141", "Reference skeleton has no inverse bind matrices.");
        var accessor = Accessor(accessorElement.GetInt32(), "MAT4");
        if (accessor.Count != joints.Length)
            throw Error("SMI142", "Inverse-bind matrix count differs from the joint count.");
        var inverse = new Dictionary<int, float[]>();
        foreach (var (skinJoint, bone) in jointMap)
            inverse[bone] = ReadComponents(accessor, skinJoint, 16).Select(value => (float)value).ToArray();
        return new SkeletalMeshGlbSkeleton(parents, inverse, names);
    }

    private static bool SkeletonEquivalent(SkeletalMeshGlbSkeleton left, SkeletalMeshGlbSkeleton right)
    {
        if (!left.ParentByBone.Keys.Order().SequenceEqual(right.ParentByBone.Keys.Order())) return false;
        foreach (var bone in left.ParentByBone.Keys)
        {
            if (left.ParentByBone[bone] != right.ParentByBone.GetValueOrDefault(bone) ||
                !left.NameByBone.GetValueOrDefault(bone, string.Empty).Equals(
                    right.NameByBone.GetValueOrDefault(bone, string.Empty),
                    StringComparison.OrdinalIgnoreCase) ||
                !left.InverseBindByBone.TryGetValue(bone, out var leftBind) ||
                !right.InverseBindByBone.TryGetValue(bone, out var rightBind) ||
                !BindPoseMatches(leftBind, rightBind)) return false;
        }
        return true;
    }

    // Blender rebuilds each rest bone from its head, tail and roll, which tilts some axis-aligned bones by
    // about 2.4e-4 rad (0.014 degrees) on an untouched import/export. Allow for that in the rotation, and
    // compare the rest position directly, because the inverse-bind translation scales the same tilt by the
    // bone's distance from the origin.
    internal static bool BindPoseMatches(IReadOnlyList<float> leftInverseBind, IReadOnlyList<float> rightInverseBind)
    {
        if (leftInverseBind.Count != 16 || rightInverseBind.Count != 16 ||
            !Matrix4x4.Invert(InverseBindMatrix(leftInverseBind), out var left) ||
            !Matrix4x4.Invert(InverseBindMatrix(rightInverseBind), out var right)) return false;
        var rotation = new[]
        {
            left.M11 - right.M11, left.M12 - right.M12, left.M13 - right.M13,
            left.M21 - right.M21, left.M22 - right.M22, left.M23 - right.M23,
            left.M31 - right.M31, left.M32 - right.M32, left.M33 - right.M33,
        };
        return rotation.All(delta => Math.Abs(delta) <= 1e-3f) &&
               Vector3.Distance(left.Translation, right.Translation) <= 1e-4f;
    }

    private static Matrix4x4 InverseBindMatrix(IReadOnlyList<float> values) => new(
        values[0], values[1], values[2], values[3],
        values[4], values[5], values[6], values[7],
        values[8], values[9], values[10], values[11],
        values[12], values[13], values[14], values[15]);

    private JsonElement FindSkinForMeshLod(int lodIndex)
    {
        var meshes = _root.GetProperty("meshes");
        for (var nodeIndex = 0; nodeIndex < _nodes.GetArrayLength(); nodeIndex++)
        {
            var node = _nodes[nodeIndex];
            if (!node.TryGetProperty("mesh", out var meshIndex) ||
                !node.TryGetProperty("skin", out var skinIndex)) continue;
            var mesh = meshes[meshIndex.GetInt32()];
            if (ReadLodIndex(mesh, meshIndex.GetInt32(), throwOnFailure: false) == lodIndex)
                return _root.GetProperty("skins")[skinIndex.GetInt32()];
        }
        throw Error("SMI143", $"LOD {lodIndex} skin could not be resolved.");
    }

    private Dictionary<int, int> ReadJointMap(JsonElement skin)
    {
        var result = new Dictionary<int, int>();
        var joints = skin.GetProperty("joints").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        for (var joint = 0; joint < joints.Length; joint++)
        {
            var nodeIndex = joints[joint];
            if (nodeIndex < 0 || nodeIndex >= _nodes.GetArrayLength())
                throw Error("SMI144", $"Skin joint node {nodeIndex} is out of range.");
            var node = _nodes[nodeIndex];
            int? bone = null;
            if (node.TryGetProperty("extras", out var extras) &&
                extras.TryGetProperty("arkiveBoneIndex", out var boneElement) &&
                boneElement.TryGetInt32(out var extraBone)) bone = extraBone;
            var name = node.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
            var match = BoneIdRegex().Match(name);
            if (bone is null && match.Success && int.TryParse(match.Groups[1].Value, out var namedBone))
                bone = namedBone;
            if (bone is null)
                throw Error("SMI145", name.Equals("neutral_bone", StringComparison.OrdinalIgnoreCase)
                    ? "Some vertices have no bone weights, so Blender added 'neutral_bone' for them (usually new or joined geometry). " +
                      "Weight them to the skeleton (for example Data Transfer from the original mesh, or Parent > With Automatic Weights) before export."
                    : $"Skin joint '{name}' has no Arkive stable bone identity.");
            if (result.ContainsValue(bone.Value))
                throw Error("SMI146", $"Skin contains duplicate Arkive bone ID {bone.Value}.");
            result.Add(joint, bone.Value);
        }
        return result;
    }

    private (uint[] Ids, string CarrierKey, string Description) ReadStableIds(
        JsonElement attributes,
        int count,
        int lodIndex,
        SkeletalMeshEditableLodManifest sourceLod)
    {
        if (attributes.TryGetProperty("_ARKIVE_VERTEX_ID", out var stableAccessor))
            return (ReadStableIdAccessor(stableAccessor.GetInt32(), count, lodIndex),
                "_ARKIVE_VERTEX_ID", "_ARKIVE_VERTEX_ID");

        var preferred = $"TEXCOORD_{sourceLod.StableVertexIdTextureCoordinate}";
        var candidates = attributes.EnumerateObject()
            .Where(property => property.Name.StartsWith("TEXCOORD_", StringComparison.Ordinal))
            .OrderByDescending(property => property.Name.Equals(preferred, StringComparison.Ordinal))
            .ThenBy(property => TextureCoordinateNumber(property.Name));
        foreach (var candidate in candidates)
        {
            var accessor = Accessor(candidate.Value.GetInt32(), "VEC2");
            if (accessor.Count != count) continue;
            var ids = new uint[count];
            var valid = true;
            for (var vertex = 0; vertex < count; vertex++)
            {
                var values = ReadComponents(accessor, vertex, 2);
                var rounded = Math.Round(values[0]);
                var sentinel = values[1];
                if (!double.IsFinite(values[0]) || rounded < 0 || rounded > uint.MaxValue ||
                    Math.Abs(values[0] - rounded) > 1e-3 ||
                    Math.Min(Math.Abs(sentinel - StableIdSentinel),
                        Math.Abs(sentinel - StableIdAlternateSentinel)) > 1e-3)
                {
                    valid = false;
                    break;
                }
                ids[vertex] = checked((uint)rounded);
            }
            if (valid) return (ids, candidate.Name, $"Blender-safe {candidate.Name}");
        }
        throw Error("SMI147",
            $"LOD {lodIndex} has no stable vertex IDs. Re-export it with current Arkive before editing in Blender.");
    }

    private uint[] ReadStableIdAccessor(int accessorIndex, int count, int lodIndex)
    {
        var accessor = Accessor(accessorIndex, "SCALAR");
        if (accessor.Count != count)
            throw Error("SMI148", $"LOD {lodIndex} stable-ID count differs from POSITION.");
        var result = new uint[count];
        for (var index = 0; index < count; index++)
        {
            var value = ReadComponents(accessor, index, 1)[0];
            var rounded = Math.Round(value);
            if (!double.IsFinite(value) || rounded < 0 || rounded > uint.MaxValue ||
                Math.Abs(value - rounded) > 1e-6)
                throw Error("SMI149", $"LOD {lodIndex} contains an invalid stable vertex ID.");
            result[index] = checked((uint)rounded);
        }
        return result;
    }

    private uint[] ReadIndices(JsonElement primitive, int vertexCount)
    {
        if (!primitive.TryGetProperty("indices", out var indexElement))
            return Enumerable.Range(0, vertexCount).Select(value => checked((uint)value)).ToArray();
        var accessor = Accessor(indexElement.GetInt32(), "SCALAR");
        var result = new uint[accessor.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var value = ReadComponents(accessor, index, 1)[0];
            if (value < 0 || value > uint.MaxValue || value != Math.Truncate(value))
                throw Error("SMI150", "Primitive contains a non-integral index.");
            result[index] = checked((uint)value);
        }
        return result;
    }

    private static uint StableIndex(IReadOnlyList<uint> ids, uint index, int lodIndex)
    {
        if (index >= ids.Count)
            throw Error("SMI151", $"LOD {lodIndex} primitive index {index} exceeds its vertex accessor.");
        return ids[checked((int)index)];
    }

    private string MaterialName(JsonElement primitive)
    {
        if (!primitive.TryGetProperty("material", out var materialElement)) return "<no material>";
        var index = materialElement.GetInt32();
        if (index < 0 || index >= _materialNames.Count)
            throw Error("SMI152", $"Primitive material {index} is out of range.");
        return _materialNames[index];
    }

    private AccessorView Accessor(int accessorIndex, string expectedType)
    {
        if (accessorIndex < 0 || accessorIndex >= _accessors.GetArrayLength())
            throw Error("SMI160", $"Accessor {accessorIndex} is out of range.");
        var accessor = _accessors[accessorIndex];
        if (accessor.TryGetProperty("sparse", out _))
            throw Error("SMI161", "Sparse accessors are not supported by the bounded mesh importer.");
        var type = accessor.GetProperty("type").GetString() ?? "";
        if (!type.Equals(expectedType, StringComparison.Ordinal))
            throw Error("SMI162", $"Accessor {accessorIndex} is {type}, expected {expectedType}.");
        var componentType = accessor.GetProperty("componentType").GetInt32();
        var componentBytes = componentType switch
        {
            5120 or 5121 => 1,
            5122 or 5123 => 2,
            5125 or 5126 => 4,
            _ => throw Error("SMI163", $"Accessor {accessorIndex} uses unsupported component type {componentType}."),
        };
        var components = type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT4" => 16,
            _ => throw Error("SMI164", $"Accessor {accessorIndex} uses unsupported shape {type}."),
        };
        var viewIndex = accessor.GetProperty("bufferView").GetInt32();
        if (viewIndex < 0 || viewIndex >= _views.GetArrayLength())
            throw Error("SMI165", $"Accessor {accessorIndex} buffer view is out of range.");
        var view = _views[viewIndex];
        if (view.TryGetProperty("buffer", out var buffer) && buffer.GetInt32() != 0)
            throw Error("SMI166", "Only embedded buffer zero is supported.");
        var packed = checked(componentBytes * components);
        var stride = view.TryGetProperty("byteStride", out var strideElement)
            ? strideElement.GetInt32()
            : packed;
        if (stride < packed)
            throw Error("SMI167", $"Accessor {accessorIndex} has an invalid byte stride.");
        var count = accessor.GetProperty("count").GetInt32();
        var offset = checked(
            (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) +
            (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0));
        if (count < 0 || offset < 0 ||
            (count > 0 && (long)offset + (long)(count - 1) * stride + packed > _binary.LongLength))
            throw Error("SMI168", $"Accessor {accessorIndex} exceeds the embedded buffer.");
        return new AccessorView(offset, stride, count, componentType, components,
            accessor.TryGetProperty("normalized", out var normalized) &&
            normalized.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            normalized.GetBoolean());
    }

    private double[] ReadComponents(AccessorView accessor, int element, int components)
    {
        if (element < 0 || element >= accessor.Count || components > accessor.Components)
            throw Error("SMI169", "Accessor element request is out of range.");
        var values = new double[components];
        var size = accessor.ComponentType is 5120 or 5121 ? 1 :
            accessor.ComponentType is 5122 or 5123 ? 2 : 4;
        var start = checked(accessor.Offset + element * accessor.Stride);
        for (var component = 0; component < components; component++)
        {
            var span = _binary.AsSpan(start + component * size);
            values[component] = accessor.ComponentType switch
            {
                5120 => accessor.Normalized ? Math.Max(-1d, unchecked((sbyte)span[0]) / 127d) : unchecked((sbyte)span[0]),
                5121 => accessor.Normalized ? span[0] / 255d : span[0],
                5122 => accessor.Normalized ? Math.Max(-1d, BinaryPrimitives.ReadInt16LittleEndian(span) / 32767d) : BinaryPrimitives.ReadInt16LittleEndian(span),
                5123 => accessor.Normalized ? BinaryPrimitives.ReadUInt16LittleEndian(span) / 65535d : BinaryPrimitives.ReadUInt16LittleEndian(span),
                5125 => BinaryPrimitives.ReadUInt32LittleEndian(span),
                5126 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span)),
                _ => throw Error("SMI163", "Unsupported accessor component type."),
            };
        }
        return values;
    }

    private Vector2 ReadVector2(int accessor, int element)
    {
        var values = ReadComponents(Accessor(accessor, "VEC2"), element, 2);
        return new Vector2((float)values[0], (float)values[1]);
    }

    private Vector3 ReadVector3(int accessor, int element)
    {
        var values = ReadComponents(Accessor(accessor, "VEC3"), element, 3);
        return new Vector3((float)values[0], (float)values[1], (float)values[2]);
    }

    private Vector4 ReadVector4(int accessor, int element)
    {
        var values = ReadComponents(Accessor(accessor, "VEC4"), element, 4);
        return new Vector4((float)values[0], (float)values[1], (float)values[2], (float)values[3]);
    }

    private Vector4 ReadColour(int accessorIndex, int element)
    {
        var accessorElement = _accessors[accessorIndex];
        var type = accessorElement.GetProperty("type").GetString();
        if (type == "VEC4") return ReadVector4(accessorIndex, element);
        if (type == "VEC3")
        {
            var values = ReadComponents(Accessor(accessorIndex, "VEC3"), element, 3);
            return new Vector4((float)values[0], (float)values[1], (float)values[2], 1f);
        }
        throw Error("SMI172", $"Colour accessor {accessorIndex} is {type}, expected VEC3 or VEC4.");
    }

    private uint[] ReadUnsignedVector4(int accessor, int element)
    {
        var values = ReadComponents(Accessor(accessor, "VEC4"), element, 4);
        return values.Select(value => checked((uint)Math.Round(value))).ToArray();
    }

    private static int RequiredAttribute(JsonElement attributes, string name, int lodIndex)
    {
        if (!attributes.TryGetProperty(name, out var value))
            throw Error("SMI170", $"LOD {lodIndex} is missing required {name} data.");
        return value.GetInt32();
    }

    private static int TextureCoordinateNumber(string name) =>
        int.TryParse(name.AsSpan("TEXCOORD_".Length), out var value) ? value : int.MaxValue;

    private static int ReadLodIndex(JsonElement mesh, int meshIndex, bool throwOnFailure = true)
    {
        if (mesh.TryGetProperty("extras", out var extras) &&
            extras.TryGetProperty("arkiveLod", out var lod) && lod.TryGetInt32(out var value)) return value;
        var name = mesh.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
        var match = LodRegex().Match(name);
        if (match.Success && int.TryParse(match.Groups[1].Value, out value)) return value;
        if (!throwOnFailure) return -1;
        throw Error("SMI171", $"Skinned mesh {meshIndex} ('{name}') has no resolvable LOD identity.");
    }

    private static SkeletalMeshGlbTriangle CanonicalTriangle(uint a, uint b, uint c, string material)
    {
        if (b < a && b <= c) return new SkeletalMeshGlbTriangle(b, c, a, material);
        if (c < a && c < b) return new SkeletalMeshGlbTriangle(c, a, b, material);
        return new SkeletalMeshGlbTriangle(a, b, c, material);
    }

    private static bool VertexEquivalentOutsideEditableFrame(
        SkeletalMeshGlbVertex left,
        SkeletalMeshGlbVertex right)
    {
        if (!Near(left.Position, right.Position, 1e-6f) ||
            left.TextureCoordinates.Count != right.TextureCoordinates.Count ||
            left.TextureCoordinates.Where((value, index) => !Near(value, right.TextureCoordinates[index], 1e-6f)).Any() ||
            !WeightsNear(left.SkinWeights, right.SkinWeights, 1e-5f) ||
            !NullableNear(left.Colour, right.Colour, 1e-5f)) return false;
        return true;
    }

    private static SkeletalMeshGlbVertex MergeEditableFrame(
        SkeletalMeshGlbVertex accumulated,
        SkeletalMeshGlbVertex next,
        int accumulatedCount)
    {
        Vector3? normal = accumulated.Normal;
        if (accumulated.Normal is { } leftNormal && next.Normal is { } rightNormal)
        {
            var sum = leftNormal * accumulatedCount + rightNormal;
            normal = sum.LengthSquared() > 1e-12f ? Vector3.Normalize(sum) : leftNormal;
        }
        else if (accumulated.Normal is null) normal = next.Normal;
        Vector4? tangent = accumulated.Tangent;
        if (accumulated.Tangent is { } leftTangent && next.Tangent is { } rightTangent)
        {
            var xyz = new Vector3(leftTangent.X, leftTangent.Y, leftTangent.Z) * accumulatedCount +
                      new Vector3(rightTangent.X, rightTangent.Y, rightTangent.Z);
            if (xyz.LengthSquared() > 1e-12f) xyz = Vector3.Normalize(xyz);
            tangent = new Vector4(
                xyz,
                Math.Sign(leftTangent.W * accumulatedCount + rightTangent.W));
        }
        else if (accumulated.Tangent is null) tangent = next.Tangent;
        return accumulated with { Normal = normal, Tangent = tangent };
    }

    private static string DescribeVertexDifference(
        SkeletalMeshGlbVertex left,
        SkeletalMeshGlbVertex right)
    {
        var normal = left.Normal is { } leftNormal && right.Normal is { } rightNormal
            ? Vector3.Distance(leftNormal, rightNormal)
            : left.Normal is null && right.Normal is null ? 0f : float.PositiveInfinity;
        var tangent = left.Tangent is { } leftTangent && right.Tangent is { } rightTangent
            ? Vector4.Distance(leftTangent, rightTangent)
            : left.Tangent is null && right.Tangent is null ? 0f : float.PositiveInfinity;
        var uv = left.TextureCoordinates.Count == right.TextureCoordinates.Count
            ? left.TextureCoordinates.Select((value, index) =>
                Vector2.Distance(value, right.TextureCoordinates[index])).DefaultIfEmpty().Max()
            : float.PositiveInfinity;
        var colour = left.Colour is { } leftColour && right.Colour is { } rightColour
            ? Vector4.Distance(leftColour, rightColour)
            : left.Colour is null && right.Colour is null ? 0f : float.PositiveInfinity;
        var weight = left.SkinWeights.Keys.Concat(right.SkinWeights.Keys).Distinct()
            .Select(bone => Math.Abs(left.SkinWeights.GetValueOrDefault(bone) -
                                     right.SkinWeights.GetValueOrDefault(bone)))
            .DefaultIfEmpty().Max();
        return $"positionDelta={Vector3.Distance(left.Position, right.Position):G6}, " +
               $"normalDelta={normal:G6}, tangentDelta={tangent:G6}, uvDelta={uv:G6}, " +
               $"weightDelta={weight:G6}, colourDelta={colour:G6}.";
    }

    internal static bool WeightsNear(
        IReadOnlyDictionary<int, float> left,
        IReadOnlyDictionary<int, float> right,
        float epsilon)
    {
        var bones = left.Keys.Concat(right.Keys).Distinct();
        return bones.All(bone => Math.Abs(left.GetValueOrDefault(bone) - right.GetValueOrDefault(bone)) <= epsilon);
    }

    internal static bool Near(Vector2 left, Vector2 right, float epsilon) =>
        Math.Abs(left.X - right.X) <= epsilon && Math.Abs(left.Y - right.Y) <= epsilon;

    internal static bool Near(Vector3 left, Vector3 right, float epsilon) =>
        Vector3.DistanceSquared(left, right) <= epsilon * epsilon;

    internal static bool Near(Vector4 left, Vector4 right, float epsilon) =>
        Vector4.DistanceSquared(left, right) <= epsilon * epsilon;

    private static bool NullableNear(Vector3? left, Vector3? right, float epsilon) =>
        left is null && right is null || left is { } a && right is { } b && Near(a, b, epsilon);

    private static bool NullableNear(Vector4? left, Vector4? right, float epsilon) =>
        left is null && right is null || left is { } a && right is { } b && Near(a, b, epsilon);

    private static void EnsureFinite(Vector2 value, string semantic, int lod, uint vertex)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            throw Error("SMI180", $"LOD {lod} vertex {vertex} contains a non-finite {semantic}.");
    }

    private static void EnsureFinite(Vector3 value, string semantic, int lod, uint vertex)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw Error("SMI180", $"LOD {lod} vertex {vertex} contains a non-finite {semantic}.");
    }

    private static void EnsureFinite(Vector4 value, string semantic, int lod, uint vertex)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) || !float.IsFinite(value.W))
            throw Error("SMI180", $"LOD {lod} vertex {vertex} contains a non-finite {semantic}.");
    }

    private static SkeletalMeshGlbSemanticException Error(string code, string message) => new(code, message);

    private readonly record struct AccessorView(
        int Offset,
        int Stride,
        int Count,
        int ComponentType,
        int Components,
        bool Normalized);

    [GeneratedRegex("_LOD(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LodRegex();

    [GeneratedRegex("__ARKIVE_(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoneIdRegex();
}

internal sealed class SkeletalMeshGlbSemanticException : Exception
{
    public SkeletalMeshGlbSemanticException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}
