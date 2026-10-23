using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using Arkive.Core.Meshes;
using Arkive.Core.Proportions;

namespace Arkive.Core.Outfits;

/// <summary>One material section of an outfit piece: its material slot and its triangles (three vertex numbers each).</summary>
public sealed record OutfitSwapSection(int Material, int[] Triangles);

/// <summary>
/// The first level of detail of an outfit piece's editable GLB: its vertices with their skin, its sections and its
/// skeleton as the game binds it. Vertices are shared by every section, as in Arkive's editable exports.
/// </summary>
public sealed class OutfitSwapMesh
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector4[] Tangents { get; init; }
    public required Vector2[] Uv0 { get; init; }
    public required Vector2[] Uv1 { get; init; }
    /// <summary>RGBA, one byte each.</summary>
    public required byte[] Colours { get; init; }
    /// <summary>Four bone numbers per vertex.</summary>
    public required int[] Joints { get; init; }
    /// <summary>Four weights per vertex.</summary>
    public required float[] Weights { get; init; }
    public required IReadOnlyList<OutfitSwapSection> Sections { get; init; }
    /// <summary>Material names as the GLB stores them (MaterialInstanceConstant'group.name').</summary>
    public required IReadOnlyList<string> Materials { get; init; }

    /// <summary>
    /// Skin materials (by index) whose sections keep the outfit's own skin picture rather than taking the new body's
    /// skin material: bare feet and hands, which keep the outfit's own shape (toes, nails) and so cannot take the new
    /// body's skin picture layout.
    /// </summary>
    public IReadOnlySet<int> OwnPictureSkin { get; init; } = new HashSet<int>();
    /// <summary>Bone names without Arkive's numbering suffix, lowercase.</summary>
    public required string[] BoneNames { get; init; }
    /// <summary>Each bone's rest position in the model.</summary>
    public required Vector3[] BonePositions { get; init; }
    public required int[] BoneParents { get; init; }

    /// <summary>Materials (by index in <see cref="Materials"/>) that are another package's material, drawn as it is there.</summary>
    public IReadOnlyDictionary<int, SkeletalMeshMaterialReference> MaterialReferences { get; init; } =
        new Dictionary<int, SkeletalMeshMaterialReference>();

    public int VertexCount => Positions.Length;

    /// <summary>The same mesh with other normals and tangents.</summary>
    public OutfitSwapMesh WithFrames(Vector3[] normals, Vector4[] tangents) => new()
    {
        Positions = Positions, Normals = normals, Tangents = tangents, Uv0 = Uv0, Uv1 = Uv1, Colours = Colours, Joints = Joints,
        Weights = Weights, Sections = Sections, Materials = Materials, BoneNames = BoneNames, BonePositions = BonePositions,
        BoneParents = BoneParents, MaterialReferences = MaterialReferences, OwnPictureSkin = OwnPictureSkin,
    };

    public bool HasBodySkeleton => Array.IndexOf(BoneNames, "bip001-pelvis") >= 0;

    /// <summary>The material's short name (pc_wr_f_av_002_dress_mi from MaterialInstanceConstant'mat.pc_wr_f_av_002_dress_mi').</summary>
    public static string ShortMaterialName(string material)
    {
        var name = material.Trim().TrimEnd('\'');
        var quote = name.IndexOf('\'');
        if (quote >= 0) name = name[(quote + 1)..];
        var dot = name.LastIndexOf('.');
        return (dot >= 0 ? name[(dot + 1)..] : name).ToLowerInvariant();
    }

    public static string CanonicalBoneName(string name)
    {
        var marker = name.LastIndexOf("__ARKIVE_", StringComparison.OrdinalIgnoreCase);
        return (marker >= 0 ? name[..marker] : name).Trim().ToLowerInvariant();
    }

    public static OutfitSwapMesh Read(byte[] glb)
    {
        var container = GlbContainer.Parse(glb);
        var json = container.Json;
        var mesh = json["meshes"]?.AsArray().FirstOrDefault()?.AsObject()
            ?? throw new InvalidDataException("The GLB has no mesh.");
        var primitives = mesh["primitives"]!.AsArray();
        var attributes = primitives[0]!["attributes"]!.AsObject();
        int Accessor(string name) => (int?)attributes[name] ?? throw new InvalidDataException($"The mesh has no {name}.");
        var positionView = container.Accessor(Accessor("POSITION"));
        var count = positionView.Count;
        foreach (var primitive in primitives)
            if ((int?)primitive!["attributes"]!["POSITION"] != (int)attributes["POSITION"]!)
                throw new InvalidDataException("The mesh's sections do not share their vertices.");

        Vector3[] Vec3(string name)
        {
            var view = container.Accessor(Accessor(name));
            var result = new Vector3[count];
            for (var i = 0; i < count; i++)
                result[i] = new Vector3(container.ReadFloat(view, i, 0), container.ReadFloat(view, i, 1), container.ReadFloat(view, i, 2));
            return result;
        }
        Vector2[] Vec2(string name)
        {
            var view = container.Accessor(Accessor(name));
            var result = new Vector2[count];
            for (var i = 0; i < count; i++) result[i] = new Vector2(container.ReadFloat(view, i, 0), container.ReadFloat(view, i, 1));
            return result;
        }

        var tangents = new Vector4[count];
        if (attributes["TANGENT"] is not null)
        {
            var view = container.Accessor(Accessor("TANGENT"));
            for (var i = 0; i < count; i++)
                tangents[i] = new Vector4(container.ReadFloat(view, i, 0), container.ReadFloat(view, i, 1), container.ReadFloat(view, i, 2),
                    view.Components > 3 ? container.ReadFloat(view, i, 3) : 1);
        }
        else Array.Fill(tangents, new Vector4(1, 0, 0, 1));

        var colours = new byte[count * 4];
        if (attributes["COLOR_0"] is not null)
        {
            var view = container.Accessor(Accessor("COLOR_0"));
            for (var i = 0; i < count; i++)
                for (var c = 0; c < 4; c++)
                    colours[i * 4 + c] = c < view.Components
                        ? (byte)Math.Clamp(MathF.Round(view.ComponentType == 5126 ? container.ReadFloat(view, i, c) * 255 : container.ReadWeight(view, i, c) * 255), 0, 255)
                        : (byte)255;
        }
        else Array.Fill(colours, (byte)255);

        var jointView = container.Accessor(Accessor("JOINTS_0"));
        var weightView = container.Accessor(Accessor("WEIGHTS_0"));
        var joints = new int[count * 4];
        var weights = new float[count * 4];
        for (var i = 0; i < count; i++)
            for (var c = 0; c < 4; c++)
            {
                joints[i * 4 + c] = container.ReadInt(jointView, i, c);
                weights[i * 4 + c] = container.ReadWeight(weightView, i, c);
            }

        var materials = (json["materials"]?.AsArray() ?? []).Select(item => (string?)item?["name"] ?? "").ToArray();
        var references = new Dictionary<int, SkeletalMeshMaterialReference>();
        var materialIndex = 0;
        foreach (var item in json["materials"]?.AsArray() ?? [])
        {
            if (item is not null && SkeletalMeshMaterialReferences.Read(System.Text.Json.JsonSerializer.SerializeToElement(item)) is { } reference)
                references[materialIndex] = reference;
            materialIndex++;
        }
        var sections = new List<OutfitSwapSection>();
        foreach (var primitive in primitives)
        {
            var view = container.Accessor((int)primitive!["indices"]!);
            var indices = new int[view.Count];
            for (var i = 0; i < view.Count; i++) indices[i] = container.ReadInt(view, i, 0);
            sections.Add(new OutfitSwapSection((int?)primitive["material"] ?? 0, indices));
        }

        var (names, positions, parents) = ReadSkeleton(container);
        return new OutfitSwapMesh
        {
            Positions = Vec3("POSITION"),
            Normals = attributes["NORMAL"] is null ? new Vector3[count] : Vec3("NORMAL"),
            Tangents = tangents,
            Uv0 = Vec2("TEXCOORD_0"),
            Uv1 = attributes["TEXCOORD_1"] is null ? Vec2("TEXCOORD_0") : Vec2("TEXCOORD_1"),
            Colours = colours,
            Joints = joints,
            Weights = weights,
            Sections = sections,
            Materials = materials,
            BoneNames = names,
            BonePositions = positions,
            BoneParents = parents,
            MaterialReferences = references,
        };
    }

    private static (string[] Names, Vector3[] Positions, int[] Parents) ReadSkeleton(GlbContainer container)
    {
        var skin = container.Json["skins"]?.AsArray().FirstOrDefault()?.AsObject()
            ?? throw new InvalidDataException("The GLB has no skeleton.");
        var nodes = container.Json["nodes"]!.AsArray();
        var joints = skin["joints"]!.AsArray().Select(node => (int)node!).ToArray();
        var view = container.Accessor((int)skin["inverseBindMatrices"]!);
        var names = new string[joints.Length];
        var positions = new Vector3[joints.Length];
        for (var j = 0; j < joints.Length; j++)
        {
            names[j] = CanonicalBoneName((string?)nodes[joints[j]]!["name"] ?? $"bone{j}");
            // glTF matrices are column-major, which read in order is System.Numerics' row-vector layout.
            var m = new float[16];
            for (var c = 0; c < 16; c++) m[c] = container.ReadFloat(view, j, c);
            var inverseBind = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
            positions[j] = Matrix4x4.Invert(inverseBind, out var world) ? world.Translation : Vector3.Zero;
        }
        var nodeParent = new Dictionary<int, int>();
        for (var n = 0; n < nodes.Count; n++)
            foreach (var child in nodes[n]?["children"]?.AsArray() ?? [])
                nodeParent[(int)child!] = n;
        var jointOf = joints.Select((node, index) => (node, index)).ToDictionary(item => item.node, item => item.index);
        var parents = joints.Select(node => nodeParent.TryGetValue(node, out var parent) && jointOf.TryGetValue(parent, out var joint) ? joint : -1).ToArray();
        return (names, positions, parents);
    }

    /// <summary>
    /// Writes a new first level of detail into a copy of <paramref name="target"/>'s editable GLB: the target's skeleton,
    /// materials and nodes stay as they are, and every LOD mesh shows the new geometry (the topology compiler copies LOD 0
    /// into each LOD slot anyway). With <paramref name="sharedVertices"/> all sections share one vertex list, as in
    /// Arkive's own exports (what the proportion engine reads); without it each section has its own, as a modelling
    /// program writes them (what the mesh import counts vertices by).
    /// </summary>
    public static byte[] WriteInto(byte[] target, OutfitSwapMesh mesh, bool sharedVertices = false)
    {
        var container = GlbContainer.Parse(target);
        var json = container.Json;
        // Tells Arkive's mesh checks that this mesh is another outfit on the same skeleton (its bone use differs by design).
        if (json["asset"] is JsonObject asset) asset["extras"] = new JsonObject { ["arkiveOutfitSwap"] = true };
        var bin = new List<byte>(container.Bin);
        var views = json["bufferViews"]!.AsArray();
        var accessors = json["accessors"]!.AsArray();

        int Add(ReadOnlySpan<byte> data, int componentType, string type, int count, int? target = null, JsonArray? min = null,
            JsonArray? max = null, bool normalized = false)
        {
            while (bin.Count % 4 != 0) bin.Add(0);
            var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Count, ["byteLength"] = data.Length };
            if (target is { } t) view["target"] = t;
            bin.AddRange(data.ToArray());
            views.Add(view);
            var accessor = new JsonObject
            {
                ["bufferView"] = views.Count - 1, ["componentType"] = componentType, ["count"] = count, ["type"] = type,
            };
            if (normalized) accessor["normalized"] = true;
            if (min is not null) accessor["min"] = min;
            if (max is not null) accessor["max"] = max;
            accessors.Add(accessor);
            return accessors.Count - 1;
        }

        static byte[] Floats(IEnumerable<float> values) => values.SelectMany(BitConverter.GetBytes).ToArray();
        JsonObject Attributes(int[] vertices)
        {
            var count = vertices.Length;
            var low = new Vector3(float.MaxValue);
            var high = new Vector3(float.MinValue);
            foreach (var v in vertices) { low = Vector3.Min(low, mesh.Positions[v]); high = Vector3.Max(high, mesh.Positions[v]); }
            var joints = new byte[count * 8];
            for (var i = 0; i < count; i++)
                for (var k = 0; k < 4; k++)
                    BinaryPrimitives.WriteUInt16LittleEndian(joints.AsSpan((i * 4 + k) * 2), checked((ushort)mesh.Joints[vertices[i] * 4 + k]));
            var colours = new byte[count * 4];
            for (var i = 0; i < count; i++) Array.Copy(mesh.Colours, vertices[i] * 4, colours, i * 4, 4);
            return new JsonObject
            {
                ["POSITION"] = Add(Floats(vertices.SelectMany(v => new[] { mesh.Positions[v].X, mesh.Positions[v].Y, mesh.Positions[v].Z })),
                    5126, "VEC3", count, 34962, new JsonArray(low.X, low.Y, low.Z), new JsonArray(high.X, high.Y, high.Z)),
                ["NORMAL"] = Add(Floats(vertices.SelectMany(v => new[] { mesh.Normals[v].X, mesh.Normals[v].Y, mesh.Normals[v].Z })), 5126, "VEC3", count, 34962),
                ["TANGENT"] = Add(Floats(vertices.SelectMany(v => new[] { mesh.Tangents[v].X, mesh.Tangents[v].Y, mesh.Tangents[v].Z, mesh.Tangents[v].W })),
                    5126, "VEC4", count, 34962),
                ["JOINTS_0"] = Add(joints, 5123, "VEC4", count, 34962),
                ["WEIGHTS_0"] = Add(Floats(vertices.SelectMany(v => Enumerable.Range(0, 4).Select(k => mesh.Weights[v * 4 + k]))), 5126, "VEC4", count, 34962),
                ["TEXCOORD_0"] = Add(Floats(vertices.SelectMany(v => new[] { mesh.Uv0[v].X, mesh.Uv0[v].Y })), 5126, "VEC2", count, 34962),
                ["TEXCOORD_1"] = Add(Floats(vertices.SelectMany(v => new[] { mesh.Uv1[v].X, mesh.Uv1[v].Y })), 5126, "VEC2", count, 34962),
                ["COLOR_0"] = Add(colours, 5121, "VEC4", count, 34962, normalized: true),
            };
        }

        var all = Enumerable.Range(0, mesh.VertexCount).ToArray();
        var shared = sharedVertices ? Attributes(all) : null;
        var primitives = new JsonArray();
        foreach (var section in mesh.Sections)
        {
            var vertices = sharedVertices ? all : section.Triangles.Distinct().ToArray();
            var local = sharedVertices ? null : vertices.Select((v, i) => (v, i)).ToDictionary(item => item.v, item => item.i);
            var indices = new byte[section.Triangles.Length * 4];
            for (var i = 0; i < section.Triangles.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(indices.AsSpan(i * 4), (uint)(local is null ? section.Triangles[i] : local[section.Triangles[i]]));
            primitives.Add(new JsonObject
            {
                ["attributes"] = shared?.DeepClone() ?? Attributes(vertices),
                ["indices"] = Add(indices, 5125, "SCALAR", section.Triangles.Length, 34963),
                ["material"] = section.Material,
                ["mode"] = 4,
            });
        }
        // Materials of other packages follow the target's own, each naming the material it draws with.
        if (mesh.Materials.Count > 0 || mesh.MaterialReferences.Count > 0)
        {
            if (json["materials"] is not JsonArray materials) json["materials"] = materials = [];
            for (var index = materials.Count; index < mesh.Materials.Count; index++)
                materials.Add(new JsonObject { ["name"] = mesh.Materials[index] });
            foreach (var (index, reference) in mesh.MaterialReferences)
                if (materials[index] is JsonObject material)
                    material["extras"] = new JsonObject { [SkeletalMeshMaterialReferences.ExtrasKey] = SkeletalMeshMaterialReferences.ToJson(reference) };
        }
        foreach (var item in json["meshes"]!.AsArray())
        {
            var entry = item!.AsObject();
            entry["primitives"] = primitives.DeepClone();
            // The stable-vertex numbering of the game mesh no longer applies to new geometry.
            var lod = (int?)entry["extras"]?["arkiveLod"] ?? 0;
            entry["extras"] = new JsonObject { ["arkiveLod"] = lod };
        }
        json["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Count });
        var rebuilt = GlbContainer.Parse(Assemble(json, bin.ToArray()));
        return rebuilt.ToBytes();
    }

    private static byte[] Assemble(JsonObject json, byte[] bin)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
        var jsonPadded = (text.Length + 3) & ~3;
        var binPadded = (bin.Length + 3) & ~3;
        var output = new byte[12 + 8 + jsonPadded + 8 + binPadded];
        BinaryPrimitives.WriteUInt32LittleEndian(output, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), (uint)output.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), 0x4E4F534A);
        text.CopyTo(output, 20);
        output.AsSpan(20 + text.Length, jsonPadded - text.Length).Fill((byte)' ');
        var at = 20 + jsonPadded;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), (uint)binPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at + 4), 0x004E4942);
        bin.CopyTo(output, at + 8);
        return output;
    }
}
