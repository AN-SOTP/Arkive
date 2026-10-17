using System.Numerics;
using Arkive.Core.Proportions;

namespace Arkive.SmokeTests;

internal static class ProportionSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("lists only player outfit pieces and the tuned NPCs for proportion editing", TestPlayerPieceNames),
        new("reshapes a player mesh by body area and keeps unaffected vertices", TestBodyAreas),
        new("saves, reloads and limits body proportion presets per body", TestPresets),
        new("keeps an upscale mod's sliders in its tags and reads older mods from their description", TestModSettings),
    ];

    private static void TestPlayerPieceNames()
    {
        Assert(BodyProfiles.IsReshapeable("pc_wr_f_av_226a_upper_sk"), "a Slayer top is a player outfit piece");
        Assert(BodyProfiles.IsReshapeable("pc_sp_av_236b_dress_sk"), "a one-piece outfit is a player outfit piece");
        Assert(BodyProfiles.IsReshapeable("pc_sp_av_108-1_lower_sk"), "a variant bottom is a player outfit piece");
        Assert(!BodyProfiles.IsReshapeable("pc_wr_f_av_226a_head_sk"), "headwear is not offered");
        Assert(!BodyProfiles.IsReshapeable("pc_sp_av_213c_face1_sk"), "faces are not offered");
        Assert(!BodyProfiles.IsReshapeable("np_lrsa_01_sk"), "other NPCs are not offered");
        Assert(BodyProfiles.IsReshapeable("mn_sden_00_sk") && BodyProfiles.ForObjectName("mn_sden_00_sk")?.Id == "npc-echidna",
            "Echidna is offered with her own tuning");
        Assert(BodyProfiles.IsReshapeable("mn_cuas_00-5_sk"), "a Brelshaza variant is offered");
        Assert(!BodyProfiles.IsReshapeable("mn_cuas_00_sk_fx") && !BodyProfiles.IsReshapeable("mn_sden_00_wing_sk"),
            "effect and wing meshes of those NPCs are not offered");
        Assert(!BodyProfiles.IsReshapeable("mn_bogg_00_sk"), "monsters are not offered");
        Assert(BodyProfiles.ForObjectName("pc_wbk_f_av_137_upper_sk")?.Id == "female-warrior", "Slayer outfits use the female warrior body");
        Assert(BodyProfiles.ForObjectName("pc_gn_f_av_141a_dress_sk")?.Id == "female-assassin", "female gunners share the assassin body");
        Assert(BodyProfiles.ForObjectName("pc_ft_m_av_008_upper_sk")?.Id == "male-gunner", "male martial artists share the male gunner body");
        Assert(BodyProfiles.OutfitStem("pc_sp_av_108-1_upper_sk") == BodyProfiles.OutfitStem("pc_sp_av_108-1_lower_sk"),
            "a top and bottom of one outfit share a stem");
        Assert(BodyProfiles.OutfitStem("pc_sp_av_108-1_upper_sk") != BodyProfiles.OutfitStem("pc_sp_av_108-2_upper_sk"),
            "alternative versions of an outfit do not share a stem");
    }

    private static void TestModSettings()
    {
        var warrior = BodyProfiles.All.First(profile => profile.Id == "female-warrior");
        var tags = BodyProportionModTags.Write(new BodyProportionModSettings(warrior,
            new Dictionary<string, double> { ["chest"] = 25, ["buttocksProjection"] = -12.5, ["waist"] = 0 }, new BodyProportionFollow(true, false)));
        Assert(tags.All(tag => tag.Length <= 40) && !tags.Any(tag => tag.Contains("waist", StringComparison.Ordinal)),
            "upscale tags are too long or keep sliders at 0: " + string.Join(", ", tags));
        var read = BodyProportionModTags.Read([.. tags, "outfit"], "anything");
        Assert(read is { Profile.Id: "female-warrior", Follow: { LooseClothing: true, Hair: false } } &&
               read.Settings.Count == 2 && read.Settings["chest"] == 25 && read.Settings["buttocksProjection"] == -12.5,
            "upscale settings did not survive the tags");
        Assert(BodyProportionModTags.Others([.. tags, "outfit"]).SequenceEqual(["outfit"]), "other tags were not kept apart");
        var old = BodyProportionModTags.Read(null,
            "Body proportions for Female Warrior: Size +40%, Height +50%, Belly +30%, Buttocks size +10%; " +
            "loose clothing follows the body; hair follows the body. Pieces: pc_wr_f_av_173b_lower_sk, pc_wr_f_av_173b_upper_sk.");
        Assert(old is { Profile.Id: "female-warrior", Follow: { LooseClothing: true, Hair: true } } && old.Settings["chest"] == 40 &&
               old.Settings["chestHeight"] == 50 && old.Settings["belly"] == 30 && old.Settings["buttocks"] == 10 && old.Settings.Count == 4,
            "an older upscale mod's sliders were not read from its description");
        Assert(BodyProportionModTags.Read(["outfit"], "A recoloured dress.") is null, "an ordinary mod was taken for an upscale mod");
    }

    private static void TestPresets()
    {
        var store = new BodyProportionPresetStore(Path.Combine(fixtureDirectory, "presets"));
        var saved = store.Save(new BodyProportionPreset(" My Slayer ", "female-warrior",
            new Dictionary<string, double> { ["chest"] = 30, ["thighs"] = 500, ["neck"] = 0, ["nonsense"] = 5 },
            LooseClothingFollows: true, HairFollows: false, DateTimeOffset.Now));
        store.Save(new BodyProportionPreset("Other body", "male-warrior", new Dictionary<string, double> { ["chest"] = 10 },
            true, true, DateTimeOffset.Now));
        var loaded = store.Load("female-warrior");
        Assert(loaded is [{ Name: "My Slayer" } preset] && preset.Settings.Count == 2 && preset.Settings["thighs"] == 60 &&
               preset.Follow == new BodyProportionFollow(true, false),
            "a preset was not saved trimmed, limited to its sliders and with its follow options");
        Assert(store.Load().Count == 2, "presets of other bodies were lost");
        store.Save(saved with { Settings = new Dictionary<string, double> { ["chest"] = 45 } });
        Assert(store.Load("female-warrior") is [{ } updated] && updated.Settings["chest"] == 45,
            "saving under the same name did not replace the preset");
        store.Delete(saved);
        Assert(store.Load("female-warrior").Count == 0, "deleting a preset left it behind");
    }

    private static void TestBodyAreas()
    {
        // A tiny female warrior mesh: a vertex on the chest, one on the left thigh and one on the foot.
        var glb = BuildMesh(
            [("bip001-pelvis", new(0, 0.734f, 0)), ("bip001-spine", new(0, 0.80f, 0)), ("bip001-spine2", new(0.02f, 0.90f, 0)),
             ("bip001-head", new(0, 1.152f, 0)), ("bip001-l-thigh", new(0, 0.734f, -0.06f)), ("bip001-l-calf", new(0, 0.40f, -0.06f)),
             ("bip001-l-foot", new(0, 0.07f, -0.06f))],
            [(new(0.10f, 0.948f, -0.044f), 2), (new(0, 0.55f, -0.12f), 4), (new(0.02f, 0.05f, -0.06f), 6)]);
        var model = BodyProportionEngine.Build(glb, default);
        Assert(model.Profile.Id == "female-warrior", $"the skeleton was detected as {model.Profile.Id}");
        Assert(model.AffectedZones.Contains("chest") && model.AffectedZones.Contains("thighs"), "chest and thighs are affected");

        var neutral = Positions(BodyProportionEngine.Apply(glb, model, new Dictionary<string, double> { ["chest"] = 0 }));
        var original = Positions(glb);
        Assert(neutral.SequenceEqual(original), "zero settings change nothing");

        var half = Positions(BodyProportionEngine.Apply(glb, model, new Dictionary<string, double> { ["chest"] = 50, ["thighs"] = 30 }));
        var full = Positions(BodyProportionEngine.Apply(glb, model, new Dictionary<string, double> { ["chest"] = 100, ["thighs"] = 60 }));
        Assert(full[0].X > original[0].X + 0.005f, "a bigger chest moves the chest forward");
        Assert(Vector3.Distance(full[1], original[1]) > 0.01f, "thicker thighs move the thigh outward");
        Assert(full[2] == original[2], "the foot does not move");
        for (var vertex = 0; vertex < 3; vertex++)
            Assert(Vector3.Distance(full[vertex] - original[vertex], (half[vertex] - original[vertex]) * 2) < 1e-5f,
                "offsets scale linearly with the setting");
        var clamped = Positions(BodyProportionEngine.Apply(glb, model, new Dictionary<string, double> { ["thighs"] = 1000 }));
        var maximum = Positions(BodyProportionEngine.Apply(glb, model, new Dictionary<string, double> { ["thighs"] = 60 }));
        Assert(clamped[1] == maximum[1], "settings are limited to the slider's range");
    }

    private static Vector3[] Positions(byte[] glb)
    {
        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
        var json = JsonNode.Parse(glb.AsSpan(20, jsonLength))!;
        var accessor = json["accessors"]![(int)json["meshes"]![0]!["primitives"]![0]!["attributes"]!["POSITION"]!]!;
        var view = json["bufferViews"]![(int)accessor["bufferView"]!]!;
        var bin = 20 + jsonLength + 8 + (int)view["byteOffset"]!;
        return Enumerable.Range(0, (int)accessor["count"]!).Select(vertex => new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(glb.AsSpan(bin + vertex * 12)),
            BinaryPrimitives.ReadSingleLittleEndian(glb.AsSpan(bin + vertex * 12 + 4)),
            BinaryPrimitives.ReadSingleLittleEndian(glb.AsSpan(bin + vertex * 12 + 8)))).ToArray();
    }

    /// <summary>A one-triangle skinned GLB; each vertex is fully weighted to one bone.</summary>
    private static byte[] BuildMesh((string Name, Vector3 Position)[] bones, (Vector3 Position, int Bone)[] vertices)
    {
        var bin = new MemoryStream();
        var writer = new BinaryWriter(bin);
        foreach (var (_, rest) in bones.Select(bone => (bone.Name, bone.Position)))
            foreach (var value in new[] { 1f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -rest.X, -rest.Y, -rest.Z, 1 }) writer.Write(value);
        var positions = (int)bin.Length;
        foreach (var (position, _) in vertices) { writer.Write(position.X); writer.Write(position.Y); writer.Write(position.Z); }
        var joints = (int)bin.Length;
        foreach (var (_, bone) in vertices) { writer.Write((ushort)bone); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); }
        var weights = (int)bin.Length;
        foreach (var _ in vertices) { writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(0f); }
        var indices = (int)bin.Length;
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2); writer.Write((ushort)0);
        var length = (int)bin.Length;
        var nodes = new JsonArray(bones.Select(bone => (JsonNode)new JsonObject { ["name"] = bone.Name }).ToArray());
        nodes.Add(new JsonObject { ["name"] = "mesh", ["mesh"] = 0, ["skin"] = 0 });
        JsonObject View(int offset, int size) => new() { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = size };
        JsonObject Accessor(int view, int type, string shape, int count) => new() { ["bufferView"] = view, ["componentType"] = type, ["type"] = shape, ["count"] = count };
        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["nodes"] = nodes,
            ["skins"] = new JsonArray(new JsonObject { ["joints"] = new JsonArray(Enumerable.Range(0, bones.Length).Select(i => (JsonNode)i).ToArray()), ["inverseBindMatrices"] = 0 }),
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["name"] = "test_LOD0",
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = 1, ["JOINTS_0"] = 2, ["WEIGHTS_0"] = 3 },
                    ["indices"] = 4,
                }),
            }),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = length }),
            ["bufferViews"] = new JsonArray(View(0, positions), View(positions, joints - positions), View(joints, weights - joints),
                View(weights, indices - weights), View(indices, length - indices)),
            ["accessors"] = new JsonArray(Accessor(0, 5126, "MAT4", bones.Length), Accessor(1, 5126, "VEC3", vertices.Length),
                Accessor(2, 5123, "VEC4", vertices.Length), Accessor(3, 5126, "VEC4", vertices.Length), Accessor(4, 5123, "SCALAR", 3)),
        };
        var jsonBytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        var jsonPadded = (jsonBytes.Length + 3) & ~3;
        var binPadded = (length + 3) & ~3;
        var glb = new byte[12 + 8 + jsonPadded + 8 + binPadded];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)glb.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A);
        jsonBytes.CopyTo(glb, 20);
        glb.AsSpan(20 + jsonBytes.Length, jsonPadded - jsonBytes.Length).Fill((byte)' ');
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(20 + jsonPadded), (uint)binPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(24 + jsonPadded), 0x004E4942);
        bin.ToArray().CopyTo(glb, 28 + jsonPadded);
        return glb;
    }
}
