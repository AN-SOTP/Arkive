using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Arkive.Core.Outfits;

namespace Arkive.SmokeTests;

internal static class OutfitSwapSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("names an outfit swap's pieces, parts and skin sections", TestNames),
        new("moves an outfit with the new body's joints, turning limbs to where the new skeleton points them", TestSkeletonFit),
        new("applies the learned body difference only near what it was learned from, and never more than a few centimetres", TestShapeField),
        new("pushes cloth that ended up inside the new body back out to the gap it had", TestPushOut),
        new("packs donor materials into the target's own pictures and hides a piece with none", TestPlanner),
        new("writes a swapped piece into the target's GLB with shared or separate section vertices", TestGlbRoundTrip),
        new("learns how much brighter and which roughness values the replaced outfit's shaders need", TestCalibration),
        new("keeps an inner avatar piece within its own material slots, its cloth going to a piece with room", TestInnerAvatarSlots),
    ];

    private static void TestInnerAvatarSlots()
    {
        // As Afterimage onto Sympathetic Eternity: the pants (one slot, an inner avatar) get cloth in two materials and skin,
        // the top has four cloth slots and its skin, two of them taken by the donor's top.
        (string, Vector3, int)[] bones = [("bip001-pelvis", Vector3.Zero, -1)];
        var vertices = Enumerable.Range(0, 15).Select(i => (new Vector3(i, 0, 0), 0)).ToArray();
        OutfitSwapSection Section(int material, int first) => new(material, [first, first + 1, first + 2]);
        var donorLower = Mesh(bones, vertices, sections: [Section(0, 0), Section(1, 3), Section(2, 6)],
            materials: ["mat.pc_dl_av_188b_lower_mi", "mat.pc_dl_av_188b_lower_trn_mi", "mat.pc_dl_av_base_lower_mi"]);
        var donorUpper = Mesh(bones, vertices, sections: [Section(0, 0), Section(1, 3)],
            materials: ["mat.pc_dl_av_188b_upper_mi", "mat.pc_dl_av_188b_upper1_mi"]);
        var targetLower = Mesh(bones, vertices, materials: ["mat.pc_wbk_f_av_137_lower_mi"]);
        var targetUpper = Mesh(bones, vertices,
            materials: ["mat.pc_wbk_f_av_137_upper3_mi", "mat.pc_wr_f_basebody_upper_mi", "mat.pc_wbk_f_av_137_upper_mi", "mat.pc_wbk_f_av_137_upper1_mi", "mat.pc_wbk_f_av_137_upper2_mi"]);
        OutfitSwapPiece[] donors = [new("pc_dl_av_188b_upper_sk", donorUpper, []), new("pc_dl_av_188b_lower_sk", donorLower, [])];
        OutfitSwapPiece[] targets = [new("pc_wbk_f_av_137_upper_sk", targetUpper, []), new("pc_wbk_f_av_137_lower_sk", targetLower, [])];
        IReadOnlyList<(int Piece, int Section, int[] Triangles)>[] assign =
        [
            [(0, 0, donorUpper.Sections[0].Triangles), (0, 1, donorUpper.Sections[1].Triangles)],
            [(1, 0, donorLower.Sections[0].Triangles), (1, 1, donorLower.Sections[1].Triangles), (1, 2, donorLower.Sections[2].Triangles)],
        ];
        var sources = new OutfitSwapMaterialSources([], null, null)
        {
            InnerAvatarPieces = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pc_wbk_f_av_137_lower_sk", "pc_wbk_f_av_137_upper_sk" },
        };
        var notes = new List<string>();
        var kept = OutfitSwapPlanner.KeepInnerAvatarsInTheirSlots(donors, targets, assign, sources, notes);
        Assert(kept[1] is [(1, 2, _)] && kept[0].Count == 4 && kept[0].Contains(assign[1][0]) && kept[0].Contains(assign[1][1]) &&
               notes.Count == 1, "the pants keep only the body's skin and the top draws their cloth");
        var plain = OutfitSwapPlanner.KeepInnerAvatarsInTheirSlots(donors, targets, assign, new OutfitSwapMaterialSources([], null, null), []);
        Assert(plain[1].Count == 3 && plain[0].Count == 2, "pieces that are not inner avatars keep their cloth and may gain slots");
    }

    private static void TestNames()
    {
        Assert(OutfitSwapBodies.StripLocale("pc_mbd_hr_00a_upper_sk_loc_int") == "pc_mbd_hr_00a_upper_sk", "the region suffix is dropped");
        Assert(OutfitSwapBodies.Part("pc_mbd_hr_00a_upper_sk_loc_int") == "upper", "a worn class skin's part is found under its suffix");
        Assert(OutfitSwapBodies.OwnsPicture("pc_wbk_f_av_137_upper_sk", "pc_wbk_f_av_137_upper_d") &&
               !OutfitSwapBodies.OwnsPicture("pc_wbk_f_av_137_upper_sk", "pc_sdm_av_137_gem_d") &&
               !OutfitSwapBodies.OwnsPicture("pc_wbk_f_av_137_upper_sk", "flat_black"), "only the piece's own class's pictures are repainted");
        Assert(OutfitSwapBodies.Part("pc_wbk_f_hr_02_dress_sk") == "dress" && OutfitSwapBodies.Part("pc_wr_f_av_226a_head_sk") is null,
            "dresses are swapped, headwear is not");
        Assert(OutfitSwapBodies.OutfitStem("pc_dl_av_217a_upper_sk") == OutfitSwapBodies.OutfitStem("pc_dl_av_217a_lower_sk"),
            "a top and its pants share an outfit");
        Assert(OutfitSwapBodies.IsSkin("MaterialInstanceConstant'mat.pc_dl_av_base_upper_mi'") &&
               OutfitSwapBodies.IsSkin("MaterialInstanceConstant'mat.pc_wr_f_basebody_upper_mi'") &&
               !OutfitSwapBodies.IsSkin("MaterialInstanceConstant'mat.pc_dl_av_217a_upper1_mi'"), "skin sections use the base body materials");
        Assert(OutfitSwapBodies.IsLowerSkin("pc_wr_f_av_basebody_lower_mi") && !OutfitSwapBodies.IsLowerSkin("pc_wr_f_basebody_upper_mi"),
            "leg skin is told from chest and arm skin");
        Assert(OutfitSwapBodies.All.Single(body => body.Profile.Id == "female-warrior").AvatarCode == "wr_f", "the Slayer body learns from wr_f costumes");
    }

    private static void TestSkeletonFit()
    {
        // A leg: pelvis, thigh, calf, foot. On the new body the leg points 10 cm further out at the foot.
        var donor = Mesh(
            [("bip001-pelvis", new(0, 0.7f, 0), -1), ("bip001-l-thigh", new(0, 0.7f, -0.1f), 0), ("bip001-l-calf", new(0, 0.4f, -0.1f), 1),
             ("bip001-l-foot", new(0, 0.1f, -0.1f), 2), ("b_skirt_l_01", new(0, 0.65f, -0.15f), 0)],
            [(new(0.05f, 0.7f, 0), 0), (new(0.05f, 0.25f, -0.1f), 2), (new(0.02f, 0.6f, -0.17f), 4)]);
        var target = Mesh(
            [("bip001-pelvis", new(0, 0.7f, 0), -1), ("bip001-l-thigh", new(0, 0.7f, -0.1f), 0), ("bip001-l-calf", new(0, 0.4f, -0.15f), 1),
             ("bip001-l-foot", new(0, 0.1f, -0.2f), 2)],
            [(new(0, 0.7f, 0), 0)]);
        var moved = OutfitSwapFitting.Retarget(donor, OutfitSwapFitting.BoneMaps(donor, new OutfitSwapSkeleton([target])));
        Assert(Vector3.Distance(moved[0], donor.Positions[0]) < 1e-5f, "a point on the pelvis stays where it is");
        Assert(moved[1].Z < -0.16f, $"a point on the calf follows the leg outward ({moved[1].Z:N3})");
        Assert(Vector3.Distance(moved[2], donor.Positions[2]) < 1e-5f, "a skirt bone the target lacks follows its body bone (the pelvis)");
    }

    private static void TestShapeField()
    {
        var field = new OutfitSwapShapeField([new(0, 1, 0)], new float[OutfitSwapFitting.FeatureCount], [new(0.01f, 0, 0)], 1);
        var near = field.Correct([new(0, 1.005f, 0)], new float[OutfitSwapFitting.FeatureCount]);
        var far = field.Correct([new(0, 1.3f, 0)], new float[OutfitSwapFitting.FeatureCount]);
        Assert(Math.Abs(near[0].X - 0.01f) < 1e-4f, "close to a learned point the correction is that point's");
        Assert(far[0] == Vector3.Zero, "far from anything learned there is no correction");
        var big = new OutfitSwapShapeField([new(0, 1, 0)], new float[OutfitSwapFitting.FeatureCount], [new(0.2f, 0, 0)], 1);
        Assert(Math.Abs(big.Correct([new(0, 1, 0)], new float[OutfitSwapFitting.FeatureCount])[0].X - 0.04f) < 1e-5f,
            "a correction is limited to 4 cm");
        var oneSided = new OutfitSwapShapeField([new(0, 1, -0.3f)], new float[OutfitSwapFitting.FeatureCount], [new(0, 0, -0.01f)], 1);
        var mirrored = oneSided.Correct([new(0, 1, 0.3f)], new float[OutfitSwapFitting.FeatureCount]);
        Assert(Math.Abs(mirrored[0].Z - 0.01f) < 1e-4f, "what was learned on one side applies mirrored to the other");
    }

    private static void TestPushOut()
    {
        // A flat patch of skin facing up at height 1, the same on both bodies; cloth 2 mm above it on the old body and
        // 5 mm below it after fitting.
        var skin = Mesh([("bip001-pelvis", new(0, 1, 0), -1)],
            [(new(-0.05f, 1, -0.05f), 0), (new(0.05f, 1, -0.05f), 0), (new(-0.05f, 1, 0.05f), 0), (new(0.05f, 1, 0.05f), 0)], triangles: [0, 2, 1, 1, 2, 3], normal: Vector3.UnitY);
        var body = new OutfitSwapBody([skin]);
        Vector3[] donor = [new(0, 1.002f, 0)];
        Vector3[] fitted = [new(0, 0.995f, 0)];
        var result = OutfitSwapClearance.PushOut(donor, fitted, body, body, [true]);
        Assert(result[0].Y > 1.0015f && result[0].Y < 1.0035f, $"the cloth is back 2 mm above the skin ({result[0].Y:N4})");
        var (signed, _, _) = body.Measure([new(0, 0.99f, 0), new(0, 1.01f, 0)]);
        Assert(signed[0] < -0.009f && signed[1] > 0.009f, "inside and outside are told apart");
        // Bare skin 1 cm above the patch lands on it and the cloth next to it moves along; held skin stays.
        var snapped = OutfitSwapClearance.SnapSkin([new(0, 1.01f, 0), new(0.01f, 1.013f, 0), new(-0.01f, 1.01f, 0)], [false, true, false],
            [0, 0, 1], body);
        Assert(Math.Abs(snapped[0].Y - 1) < 1e-4f, $"the skin lies on the body ({snapped[0].Y:N4})");
        Assert(snapped[1].Y < 1.012f && snapped[1].Y > 1.0025f, $"the cloth beside it follows part of the way ({snapped[1].Y:N4})");
        Assert(Math.Abs(snapped[2].Y - 1.01f) < 1e-5f, "held skin (feet) keeps its place");
        var surface = body.Closest(new(0.02f, 1.05f, 0.01f));
        Assert(surface.Triangle >= 0 && Math.Abs(surface.Position.Y - 1) < 1e-5f && Math.Abs(surface.Position.X - 0.02f) < 1e-5f,
            "the closest point on the body is straight below");
    }

    private static void TestPlanner()
    {
        var bones = new (string, Vector3, int)[] { ("bip001-pelvis", new(0, 1, 0), -1) };
        var donor = Mesh(bones, [(new(0, 1, 0.1f), 0), (new(0.1f, 1, 0), 0), (new(0, 1.1f, 0), 0), (new(0, 1, -0.1f), 0)],
            sections: [new(0, [0, 1, 2]), new(1, [1, 2, 3]), new(2, [0, 2, 3]), new(3, [0, 1, 3])],
            materials: ["mat.pc_xx_a_mi", "mat.pc_xx_b_mi", "mat.pc_xx_c_mi", "mat.pc_xx_base_upper_mi"]);
        var donorMaterials = Enumerable.Range(0, 4).Select(slot => Material(slot, donor.Materials[slot], $"pc_xx_{slot}_d")).ToArray();
        var target = Mesh(bones, [(new(0, 1, 0), 0), (new(0.1f, 1, 0), 0), (new(0, 1.1f, 0), 0)], sections: [new(0, [0, 1, 2])],
            materials: ["mat.pc_yy_a_mi", "mat.pc_yy_b_mi", "mat.pc_yy_base_upper_mi"]);
        var targetMaterials = new[] { Material(0, "pc_yy_a_mi", "pc_yy_a_d"), Material(1, "pc_yy_b_mi", "pc_yy_b_d"), Material(2, "pc_yy_base_upper_mi", "pc_yy_body_d") };
        var engine = Mesh(bones, [(new(0, 1, 0), 0), (new(0.1f, 1, 0), 0), (new(0, 1.1f, 0), 0)], sections: [new(0, [0, 1, 2])], materials: ["mat.pc_zz_mi"]);
        var plan = OutfitSwapPlanner.Plan(
            [new OutfitSwapPiece("pc_xx_av_1_dress_sk", donor, donorMaterials)], [donor.Positions],
            [new OutfitSwapPiece("pc_yy_av_1_upper_sk", target, targetMaterials),
             new OutfitSwapPiece("pc_yy_av_1_lower_sk", engine, [Material(0, "pc_zz_mi", "pc_zz_av_1_d")])], null);
        Assert(plan.Canvases.Count == 2 && plan.Canvases.Sum(canvas => canvas.Tiles.Count) == 3,
            "three cloth materials share the two pictures of their own");
        Assert(plan.Canvases.Any(canvas => canvas.Grid == 2), "two materials share one picture in a grid");
        var upper = plan.Pieces[0].Mesh;
        Assert(upper.Sections.Any(section => section.Material == 2), "the skin section uses the target's own skin slot");
        Assert(plan.Pieces[1].Hidden && plan.Pieces[1].Mesh.Sections.Single().Triangles.Length == 3,
            "a piece drawn only with pictures it does not own (another class's) is hidden with one tiny triangle");
        var packed = plan.Canvases.Single(canvas => canvas.Grid == 2);
        var rect = packed.Rect(packed.Tiles[1].Piece, packed.Tiles[1].Material)!.Value;
        Assert(rect == new Vector4(0.5f, 0, 1, 0.5f), "the second tile takes the top right square");
    }

    private static void TestCalibration()
    {
        // A shader that draws a quarter of the light the donor's did: the gain brings it back (within its limit of 3).
        static Arkive.Core.Textures.RgbaImage Grey(int size, Func<int, byte> value, byte alpha = 255)
        {
            var pixels = new byte[size * size * 4];
            for (var i = 0; i < size * size; i++) { pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = value(i); pixels[i * 4 + 3] = alpha; }
            return new Arkive.Core.Textures.RgbaImage(size, size, pixels);
        }
        var gain = OutfitSwapColourCurve.Learn(Grey(64, _ => 180), Grey(64, _ => 100));
        Assert(gain is not null && gain.Gain > 2.5f && gain.Gain <= 3f, $"a dull painting is brightened ({gain?.Gain:N2})");
        // A shader computing roughness = 1 - (gloss in alpha): the curve finds the alpha that gives a wanted roughness.
        var picture = Grey(128, i => (byte)(i % 256));
        for (var i = 0; i < 128 * 128; i++) picture.Pixels[i * 4 + 3] = (byte)(i % 256);
        var baked = Grey(128, i => (byte)(255 - i % 256));
        var curve = OutfitSwapChannelCurve.Learn(picture, 3, baked, 0);
        Assert(curve is not null && Math.Abs(curve.Map(55) - 200) <= 2, $"wanted roughness 55 needs gloss 200 ({curve?.Map(55)})");
    }

    private static void TestGlbRoundTrip()
    {
        var bones = new (string, Vector3, int)[] { ("bip001-pelvis", new(0, 1, 0), -1), ("bip001-spine", new(0, 1.1f, 0), 0) };
        var template = Glb(bones);
        var original = OutfitSwapMesh.Read(template);
        Assert(original.BoneNames.SequenceEqual(["bip001-pelvis", "bip001-spine"]) && Vector3.Distance(original.BonePositions[1], new(0, 1.1f, 0)) < 1e-5f,
            "the skeleton's names and rest positions are read from the bind matrices");
        var mesh = Mesh(bones, [(new(0, 1, 0), 0), (new(0.1f, 1, 0), 1), (new(0, 1.1f, 0), 1), (new(0.1f, 1.1f, 0), 0)],
            sections: [new(0, [0, 1, 2]), new(0, [1, 2, 3])], materials: original.Materials);
        var separate = OutfitSwapMesh.WriteInto(template, mesh);
        var shared = OutfitSwapMesh.WriteInto(template, mesh, sharedVertices: true);
        var back = OutfitSwapMesh.Read(shared);
        Assert(back.VertexCount == 4 && back.Sections.Count == 2 && back.Positions[3] == new Vector3(0.1f, 1.1f, 0) &&
               back.Joints[4] == 1, "shared vertices come back as written");
        var json = JsonNode.Parse(separate.AsSpan(20, BinaryPrimitives.ReadInt32LittleEndian(separate.AsSpan(12))))!;
        var primitives = json["meshes"]![0]!["primitives"]!.AsArray();
        Assert(primitives.Count == 2 && (int)json["accessors"]![(int)primitives[0]!["attributes"]!["POSITION"]!]!["count"]! == 3,
            "separate sections carry only their own three vertices");
    }

    private static OutfitSwapMaterial Material(int slot, string name, string colour) =>
        new(slot, OutfitSwapMesh.ShortMaterialName(name), false,
            new Dictionary<string, string> { ["texture_diffuse"] = colour }, new Dictionary<string, float[]>(), new Dictionary<string, float>());

    private static OutfitSwapMesh Mesh((string Name, Vector3 Position, int Parent)[] bones, (Vector3 Position, int Bone)[] vertices,
        int[]? triangles = null, OutfitSwapSection[]? sections = null, IReadOnlyList<string>? materials = null, Vector3? normal = null)
    {
        var count = vertices.Length;
        return new OutfitSwapMesh
        {
            Positions = vertices.Select(vertex => vertex.Position).ToArray(),
            Normals = Enumerable.Repeat(normal ?? Vector3.UnitZ, count).ToArray(),
            Tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), count).ToArray(),
            Uv0 = vertices.Select((_, i) => new Vector2(i * 0.1f, 0.5f)).ToArray(),
            Uv1 = new Vector2[count],
            Colours = Enumerable.Repeat((byte)255, count * 4).ToArray(),
            Joints = vertices.SelectMany(vertex => new[] { vertex.Bone, 0, 0, 0 }).ToArray(),
            Weights = vertices.SelectMany(_ => new[] { 1f, 0, 0, 0 }).ToArray(),
            Sections = sections ?? [new OutfitSwapSection(0, triangles ?? Enumerable.Range(0, count - count % 3).ToArray())],
            Materials = materials ?? ["mat.pc_test_mi"],
            BoneNames = bones.Select(bone => bone.Name).ToArray(),
            BonePositions = bones.Select(bone => bone.Position).ToArray(),
            BoneParents = bones.Select(bone => bone.Parent).ToArray(),
        };
    }

    /// <summary>A minimal editable-style GLB: a skeleton, one material, and one triangle sharing its vertices.</summary>
    private static byte[] Glb((string Name, Vector3 Position, int Parent)[] bones)
    {
        var bin = new MemoryStream();
        var writer = new BinaryWriter(bin);
        foreach (var bone in bones)
            foreach (var value in new[] { 1f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -bone.Position.X, -bone.Position.Y, -bone.Position.Z, 1 }) writer.Write(value);
        var positions = (int)bin.Length;
        foreach (var p in new[] { new Vector3(0, 1, 0), new Vector3(0.1f, 1, 0), new Vector3(0, 1.1f, 0) }) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
        var uvs = (int)bin.Length;
        for (var i = 0; i < 3; i++) { writer.Write(i * 0.5f); writer.Write(0f); }
        var joints = (int)bin.Length;
        for (var i = 0; i < 3; i++) { writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); }
        var weights = (int)bin.Length;
        for (var i = 0; i < 3; i++) { writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(0f); }
        var indices = (int)bin.Length;
        writer.Write(0u); writer.Write(1u); writer.Write(2u);
        var length = (int)bin.Length;
        var nodes = new JsonArray(bones.Select((bone, i) => (JsonNode)new JsonObject
        {
            ["name"] = $"{bone.Name}__ARKIVE_{i:D4}",
            ["children"] = new JsonArray(bones.Select((child, c) => (child, c)).Where(item => item.child.Parent == i).Select(item => (JsonNode)item.c).ToArray()),
        }).ToArray());
        nodes.Add(new JsonObject { ["name"] = "test_LOD0_EDIT", ["mesh"] = 0, ["skin"] = 0 });
        JsonObject View(int offset, int size) => new() { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = size };
        JsonObject Accessor(int view, int type, string shape, int count) => new() { ["bufferView"] = view, ["componentType"] = type, ["type"] = shape, ["count"] = count };
        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["nodes"] = nodes,
            ["skins"] = new JsonArray(new JsonObject { ["joints"] = new JsonArray(Enumerable.Range(0, bones.Length).Select(i => (JsonNode)i).ToArray()), ["inverseBindMatrices"] = 0 }),
            ["materials"] = new JsonArray(new JsonObject { ["name"] = "MaterialInstanceConstant'mat.pc_test_mi'" }),
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["name"] = "test_LOD0",
                ["extras"] = new JsonObject { ["arkiveLod"] = 0 },
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = 1, ["TEXCOORD_0"] = 2, ["JOINTS_0"] = 3, ["WEIGHTS_0"] = 4 },
                    ["indices"] = 5,
                    ["material"] = 0,
                }),
            }),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = length }),
            ["bufferViews"] = new JsonArray(View(0, positions), View(positions, uvs - positions), View(uvs, joints - uvs), View(joints, weights - joints),
                View(weights, indices - weights), View(indices, length - indices)),
            ["accessors"] = new JsonArray(Accessor(0, 5126, "MAT4", bones.Length), Accessor(1, 5126, "VEC3", 3), Accessor(2, 5126, "VEC2", 3),
                Accessor(3, 5123, "VEC4", 3), Accessor(4, 5126, "VEC4", 3), Accessor(5, 5125, "SCALAR", 3)),
        };
        var text = Encoding.UTF8.GetBytes(json.ToJsonString());
        var jsonPadded = (text.Length + 3) & ~3;
        var binPadded = (length + 3) & ~3;
        var glb = new byte[12 + 8 + jsonPadded + 8 + binPadded];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)glb.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A);
        text.CopyTo(glb, 20);
        glb.AsSpan(20 + text.Length, jsonPadded - text.Length).Fill((byte)' ');
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(20 + jsonPadded), (uint)binPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(24 + jsonPadded), 0x004E4942);
        bin.ToArray().CopyTo(glb, 28 + jsonPadded);
        return glb;
    }
}
