namespace Arkive.SmokeTests;

internal static class ZoneSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("groups zones that load the same levels into one map", TestZoneMapGrouping),
        new("sorts a map's assets into terrain, sky, fog, lighting and other parts", TestZonePartGroups),
        new("saves and reloads which maps use which assets, and rejects a stale index", TestZoneUsageIndex),
        new("recolours material colour parameters the way the Texture Viewer picks colours", TestWorkingCopyTintColours),
        new("recolours material and texture working copies and leaves masks alone", TestWorkingCopyTintMaterial),
        new("reads where a level places a mesh from its lower-case, Lost Ark-style property tags", TestLevelProperties),
        new("places map meshes in the 3D view's space and reads foliage instances", TestZoneScenePlacement),
        new("keeps sky domes and glow planes out of the 3D map's solid layer", TestZoneSceneLayers),
    ];

    private static void TestLevelProperties()
    {
        UnrealNameEntry[] names = new[] { "none", "location", "structproperty", "vector", "staticmesh", "objectproperty", "visibilityid",
            "intproperty", "materials", "arrayproperty", "hiddengame", "boolproperty", "layername", "nameproperty", "layer01" }
            .Select((name, index) => new UnrealNameEntry(index, name, 0)).ToArray();
        var bytes = new List<byte> { 0xEC, 0xFF, 0xFF, 0xFF, 0x09 };  // an odd-length header before the tags
        void Name(int index) { bytes.AddRange(BitConverter.GetBytes(index)); bytes.AddRange(BitConverter.GetBytes(0)); }
        void Int(int value) => bytes.AddRange(BitConverter.GetBytes(value));
        void Tag(int name, int type, int size) { Name(name); Name(type); Int(size); Int(0); }
        Tag(1, 2, 12); Name(3); bytes.AddRange(BitConverter.GetBytes(150f)); bytes.AddRange(BitConverter.GetBytes(-20f)); bytes.AddRange(BitConverter.GetBytes(4.5f));
        Tag(4, 5, 4); Int(-7);
        Tag(6, 7, 4); Name(0); Int(183);  // Lost Ark's IntProperty tag carries one extra name
        Tag(8, 9, 12); Int(2); Int(-3); Int(0);
        Tag(10, 11, 0); bytes.Add(1);
        Name(0);
        var properties = LevelProperties.Read(bytes.ToArray(), names, out var end);
        Assert(properties is not null && end == bytes.Count, $"the property tags were not read to their end ({end} of {bytes.Count})");
        properties ??= [];
        Assert(properties.Vector("location") == new System.Numerics.Vector3(150, -20, 4.5f) && properties.Object("staticmesh") == -7 &&
               properties.Int("visibilityid") == 183 && properties.Objects("materials").SequenceEqual([-3, 0]) && properties.Bool("hiddengame"),
            "a level object's location, mesh, ids, materials or flags were misread");
        var element = new List<byte>();
        bytes = element;
        Tag(12, 13, 8); Name(14); Name(0);
        var array = new List<byte>(BitConverter.GetBytes(1));
        array.AddRange(element);
        var holder = new List<LevelProperty> { new("weightmaplayerallocations", "arrayproperty", null, 0, array.ToArray(), 0) };
        Assert(holder.Structs("weightmaplayerallocations", names).Single().Text("layername") == "layer01",
            "an array of structs (a landscape's layer allocations) was misread");
    }

    private static void TestZoneScenePlacement()
    {
        var yaw = ZoneMapSceneBuilder.Rotation((0, 16384, 0));
        Assert(Math.Abs(yaw.M11) < 1e-5 && Math.Abs(yaw.M12 - 1) < 1e-5, "a quarter turn of yaw did not turn X into Y");
        var world = System.Numerics.Matrix4x4.CreateScale(2) * System.Numerics.Matrix4x4.CreateTranslation(100, 200, 300);
        var viewer = ZoneMapSceneBuilder.ToViewer(world);
        Assert(Math.Abs(viewer.M41 - 1) < 1e-5 && Math.Abs(viewer.M42 - 3) < 1e-5 && Math.Abs(viewer.M43 - 2) < 1e-5 && Math.Abs(viewer.M22 - 2) < 1e-5,
            $"Unreal centimetres with Z up did not become metres with Y up: {viewer}");
        var tail = new List<byte>(new byte[12]);
        tail.AddRange(BitConverter.GetBytes(80));
        tail.AddRange(BitConverter.GetBytes(2));
        for (var instance = 0; instance < 2; instance++)
        {
            var matrix = System.Numerics.Matrix4x4.CreateTranslation(instance * 500, 10, 0);
            foreach (var value in new[] { matrix.M11, matrix.M12, matrix.M13, matrix.M14, matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                         matrix.M31, matrix.M32, matrix.M33, matrix.M34, matrix.M41, matrix.M42, matrix.M43, matrix.M44 })
                tail.AddRange(BitConverter.GetBytes(value));
            tail.AddRange(new byte[16]);
        }
        var instances = ZoneMapSceneBuilder.Instances(tail.ToArray()).ToArray();
        Assert(instances.Length == 2 && instances[1].M41 == 500 && instances[1].M42 == 10, "foliage instances were not read from a component's data");
        Assert(!ZoneMapSceneBuilder.Instances(new byte[64]).Any(), "foliage instances were found in data with none");
        var picture = new RgbaImage(4, 4, Enumerable.Repeat((byte)200, 64).ToArray());
        Assert(ZoneMapSceneBuilder.Shrink(picture, 2) is { Width: 2, Height: 2 } small && small.Pixels.All(value => value == 200),
            "a texture was not shrunk to the 3D map's size");
    }

    private static void TestZoneSceneLayers()
    {
        Assert(ZoneMapSceneBuilder.Layer("lv_kur_death_06.sky_01", []) == "sky" &&
               ZoneMapSceneBuilder.Layer("bfx_sm_00.bfm_planbottom_01", ["lv_kur_death_05.preset_cloud_trn_new_06_10"]) == "sky",
            "a sky dome or cloud plane was not on the sky layer");
        Assert(ZoneMapSceneBuilder.Layer("fx_sm_00.fm_a_hemisphere_008", []) == "effect" &&
               ZoneMapSceneBuilder.Layer("lv_module.lv_module_water01_1024", []) == "effect" &&
               ZoneMapSceneBuilder.Layer("bg_x.plane_sm", ["lv_kur_death_05_b.bfx_d_me_ringmaster_01_03_tr_02"]) == "effect",
            "a glow, water plane or translucent effect mesh was not on the effect layer");
        Assert(ZoneMapSceneBuilder.Layer("bg_rad_kazeroth_o.bg_rad_kazeroth_stone_wave01_sm", ["bg_rad_kazeroth_o.bg_rad_kazeroth_stone_wave01_mi_hhk"]) == "solid" &&
               ZoneMapSceneBuilder.Layer("bg_rad_kazeroth_m.bg_rad_kazeroth_statue04_sm", ["lv_kur_death_04.bg_rad_kazeroth_statue_mi_trim"]) == "solid",
            "a rock or statue was taken off the map's solid layer");
    }

    private static void TestZoneMapGrouping()
    {
        GameZone[] zones =
        [
            new(37545, "Land of Death and Order", ["lv_kur_death_ed_05_ps"]),
            new(37555, "Land of Death and Order", ["LV_KUR_DEATH_ED_05_PS"]),
            new(37546, "Collapsed Diaspero", ["lv_kur_death_ed_06_ps"]),
            new(99999, "Removed Zone", ["lv_removed_ps"]),
        ];
        var maps = ZoneMap.Group(zones,
            ["lv_kur_death_ed_05_ps", "lv_kur_death_ed_05_sl01", "lv_kur_death_ed_06_ps", "lv_kur_death_04", "bg_rad_kazeroth_m"]);
        Assert(maps.Count == 2, $"zones were not grouped into the two maps that exist: {maps.Count}");
        Assert(maps[0].Zones.Select(zone => zone.Id).SequenceEqual([37545L, 37555L]) && maps[0].Name == "Land of Death and Order",
            "a gate and its hard mode, which load the same levels, were not one map");
        Assert(maps[0].LevelPackages.SequenceEqual(["lv_kur_death_ed_05_ps", "lv_kur_death_ed_05_sl01"]),
            "a map's level packages were wrong: " + string.Join(", ", maps[0].LevelPackages));
        Assert(maps.All(map => map.Zones.All(zone => zone.Id != 99999)), "a zone whose levels are gone was listed as a map");
    }

    private static void TestZonePartGroups()
    {
        var terrain = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lv_kur_death_04.lv_kur_death_04_land_mi_01", "lv_ground_01.lv_common_ground_103_d" };
        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lv_kur_death_ed_04_land01.texture2d_151" };
        var characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mn_cncu_00" };
        var effects = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bfx_high_00", "lv_kur_death_04_d" };
        ZonePartGroup Group(string package, string name, string className) =>
            ZoneParts.Classify(package, name, className, terrain, generated, characters, effects);

        Assert(Group("lv_kur_death_04", "lv_kur_death_04_land_mi_01", "MaterialInstanceConstant") == ZonePartGroup.Terrain &&
               Group("lv_ground_01", "lv_common_ground_103_d", "Texture2D") == ZonePartGroup.Terrain,
            "the landscape's layer materials and textures were not Terrain");
        Assert(Group("lv_kur_death_ed_04_land01", "texture2d_151", "Texture2D") == ZonePartGroup.GeneratedMaps &&
               Group("lv_kur_death_ed_05_sl01", "directionalmaxcomponent0_1", "LightMapTexture2D") == ZonePartGroup.GeneratedMaps &&
               Group("lv_kur_death_ed_05_sl01", "shadowmaptexture2d_2384", "ShadowMapTexture2D") == ZonePartGroup.GeneratedMaps,
            "lightmaps, shadow maps and blend masks were not kept apart");
        Assert(Group("lv_kur_death_06", "lv_kur_death_lut_07", "Texture2D") == ZonePartGroup.Lighting &&
               Group("lv_kur_death_04", "lv_kur_death_04_lut_01", "Texture2D") == ZonePartGroup.Lighting &&
               Group("lv_kur_death_04", "lv_fmd_berna_lightfunction01_mi_02", "MaterialInstanceConstant") == ZonePartGroup.Lighting,
            "colour grading or light functions were not Lighting");
        Assert(Group("bg_lut_zamount_a", "bg_lut_zamount_brokenfortresscrack01_n_lkj", "Texture2D") == ZonePartGroup.Textures,
            "a Luterra (bg_lut_) texture was taken for colour grading");
        Assert(Group("lv_kur_death_ed_05_ps", "fogvolumemi_0", "MaterialInstanceConstant") == ZonePartGroup.Fog,
            "the fog volume material was not Fog");
        Assert(Group("lv_kur_death_04_b", "test_lv_sky_0092_d", "Texture2D") == ZonePartGroup.Sky &&
               Group("lv_kur_death_05", "preset_cloud_trn_new_06_10", "MaterialInstanceConstant") == ZonePartGroup.Sky,
            "sky and cloud parts were not Sky");
        Assert(Group("mn_cncu_00", "mn_cncu_00_sk", "SkeletalMesh") == ZonePartGroup.Characters &&
               Group("mn_cncu_00", "mn_cncu_00-01_mi", "MaterialInstanceConstant") == ZonePartGroup.Characters,
            "a boss model and its material were not Characters");
        Assert(Group("bfx_high_00", "par_d_heavymist_02", "ParticleSystem") == ZonePartGroup.Effects &&
               Group("bfx_high_00", "bfx_mist_mi", "MaterialInstanceConstant") == ZonePartGroup.Effects,
            "an effect and its package's material were not Effects");
        Assert(Group("lv_kur_death_04_d", "bg_ber_chaosth_pillar05_mi_ksr_03", "MaterialInstanceConstant") == ZonePartGroup.Materials,
            "a mesh material in a level package that also holds effects was taken for an effect");
        Assert(Group("bg_kurzan_stone_a", "bg_kur_stone_rock03p_sm", "StaticMesh") == ZonePartGroup.Meshes &&
               Group("bg_kurzan_stone_a", "bg_kur_stone_rock03d_d_ksr", "Texture2D") == ZonePartGroup.Textures,
            "rocks and their textures were not Meshes and Textures");
        Assert(ZoneParts.ReferencedName("materialinstanceconstant'lv_kur_death_04_land_mi_04'") == "lv_kur_death_04_land_mi_04" &&
               ZoneParts.ReferencedName("texture2d'978T20F8XPW4TH8TWP8T6Y8T94NP6D.texture2d_151'") == "texture2d_151" &&
               ZoneParts.ReferencedName("None") is null,
            "a material's references were not read as object names");
        var settings = ZoneLevelSettings.Count([new Dictionary<string, int>
        {
            ["dominantdirectionallightcomponent"] = 1, ["pointlightcomponent"] = 3, ["spotlightcomponent"] = 2,
            ["exponentialheightfogcomponent"] = 1, ["fogvolumeconstantdensitycomponent"] = 1, ["efenvironmentinfovolume"] = 2,
            ["dynamiclightenvironmentcomponent"] = 5, ["staticmeshcomponent"] = 40,
        }]);
        Assert(settings == new ZoneLevelSettings(1, 5, 2, 2), $"level settings were miscounted: {settings}");
    }

    private static void TestZoneUsageIndex()
    {
        var directory = Path.Combine(fixtureDirectory, "zone-usage");
        Directory.CreateDirectory(directory);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["lv_kur_death_ed_05_ps"] = Path.Combine(directory, "a.upk"),
            ["lv_kur_death_ed_05_sl01"] = Path.Combine(directory, "b.upk"),
            ["lv_kur_death_ed_06_ps"] = Path.Combine(directory, "c.upk"),
        };
        foreach (var file in files.Values) File.WriteAllBytes(file, [1, 2, 3]);
        var zones = new ZoneMapDocument(1, DateTimeOffset.UnixEpoch, "leveldata1.lpk", 10, 20,
        [
            new GameZone(37545, "Land of Death and Order", ["lv_kur_death_ed_05_ps"]),
            new GameZone(37546, "Collapsed Diaspero", ["lv_kur_death_ed_06_ps"]),
        ]);
        var path = Path.Combine(directory, "metadata.zone-usage.bin.gz");
        // The fixture files are not packages: each level is skipped, as an unreadable one is in the game.
        var built = ZoneUsageIndex.BuildAsync(path, zones, files, NoChunkProtectionProvider.Instance).GetAwaiter().GetResult();
        Assert(built.Maps.Count == 2 && File.Exists(path), "the map usage index was not built and saved");
        Assert(built.MapOf(37545)?.Name == "Land of Death and Order", "a zone's map was not found");
        var holders = built.MapsUsing("lv_kur_death_ed_05_sl01", "directionalmaxcomponent0_1");
        Assert(holders.Count == 1 && holders[0].Zones[0].Id == 37545, "an object in a map's own level package was not that map's");
        Assert(built.MapsUsing("bg_rad_kazeroth_m", "anything").Count == 0, "an object no map imports had users");
        var maps = ZoneMap.Group(zones.Zones, files.Keys);
        var loaded = ZoneUsageIndex.TryLoad(path, ZoneUsageIndex.ComputeFingerprint(zones, maps, files));
        Assert(loaded is not null && loaded.Maps.Count == 2 && loaded.MapOf(37546)?.LevelPackages.SequenceEqual(["lv_kur_death_ed_06_ps"]) == true,
            "the saved map usage index did not load back");
        File.WriteAllBytes(files["lv_kur_death_ed_06_ps"], [1, 2, 3, 4]);
        Assert(ZoneUsageIndex.TryLoad(path, ZoneUsageIndex.ComputeFingerprint(zones, maps, files)) is null,
            "an index built before a level package changed was still used");
    }

    private static void TestWorkingCopyTintColours()
    {
        var red = new TextureRecolour(1, 0, 0, Strength: 1);
        var (r, g, b) = WorkingCopyTint.Recolour(red, 0.2f, 0.4f, 1.6f);
        Assert(Math.Abs(r - 1.6f) < 1e-4 && g == 0 && b == 0, $"a glowing blue did not keep its strength as red: {r}, {g}, {b}");
        var gold = new TextureRecolour(0, 0, 1, Strength: 1) { Selection = ColourSelection.ColourRange, RangeHue = 45, RangeWidth = 20 };
        Assert(WorkingCopyTint.Recolour(gold, 0.2f, 0.4f, 1) == (0.2f, 0.4f, 1f), "a colour outside the chosen hues changed");
        var (gr, gg, gb) = WorkingCopyTint.Recolour(gold, 1, 0.75f, 0.1f);
        Assert(gb > 0.9f && gr < 0.05f, $"a gold colour inside the chosen hues did not change: {gr}, {gg}, {gb}");
        var none = new TextureRecolour(1, 0, 0, Strength: 0);
        Assert(WorkingCopyTint.Recolour(none, 0.3f, 0.6f, 0.9f) == (0.3f, 0.6f, 0.9f), "a tint with no strength changed a colour");
        var darker = new TextureRecolour(1, 1, 1, Strength: 0, Brightness: -0.5f);
        var (dr, _, _) = WorkingCopyTint.Recolour(darker, 0.8f, 0.4f, 0.2f);
        Assert(Math.Abs(dr - 0.4f) < 1e-4, $"Brightness did not darken a colour: {dr}");
    }

    private static void TestWorkingCopyTintMaterial()
    {
        var directory = Path.Combine(fixtureDirectory, "zone-tint");
        Directory.CreateDirectory(directory);
        var documentPath = Path.Combine(directory, MaterialInstanceEditWorkspaceStore.DocumentFileName);
        var document = new MaterialInstanceEditDocument(MaterialInstanceEditService.CurrentEditSchemaVersion, "edit-1",
            DateTimeOffset.UnixEpoch, "Fog", "recon.json", "package.upk", new string('0', 64), "lv_kur_death_ed_05_ps", 1,
            "MaterialInstanceConstant", "fogvolumemi_0", 10, 20, new string('0', 64), [],
            [
                new MaterialVectorEdit("emissivecolor", 1, 0.61f, 0.39f, 1, 1, 0.61f, 0.39f, 1),
                new MaterialVectorEdit("layermask_layer01", 1, 0, 0, 0, 1, 0, 0, 0),
            ], []);
        File.WriteAllText(documentPath, JsonSerializer.Serialize(document));
        var edit = new ArkiveModLocalEdit(UnifiedPackageAssetKind.MaterialInstanceConstant, "edit-1", documentPath, "Fog",
            "package.upk", "fogvolumemi_0", true, new MaterialInstanceEditEntry(directory, documentPath, document));
        var result = new WorkingCopyTint().TintAsync(new MaterialTintRequest([edit], new TextureRecolour(0.2f, 0.4f, 1, Strength: 1)))
            .GetAwaiter().GetResult();
        Assert(result.Changed == 1 && result.Skipped.Count == 0, $"the material was not recoloured: {result}");
        var saved = JsonSerializer.Deserialize<MaterialInstanceEditDocument>(File.ReadAllText(documentPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var fog = saved.Vectors.Single(vector => vector.Name == "emissivecolor");
        var mask = saved.Vectors.Single(vector => vector.Name == "layermask_layer01");
        Assert(fog.Blue > fog.Red && fog.SourceRed == 1 && fog.Changed, $"the fog colour did not turn blue: {fog}");
        Assert(!mask.Changed, "a layer mask vector was recoloured");
        var picturePath = Path.Combine(directory, "sky.png");
        // Two gold pixels and two blue ones; a gold-only recolour turns only the gold ones red.
        PngRgbaCodec.WriteAsync(picturePath, new RgbaImage(2, 2, [230, 180, 40, 255, 220, 170, 30, 255, 40, 80, 220, 255, 30, 60, 200, 255]))
            .GetAwaiter().GetResult();
        var texture = new ArkiveModLocalEdit(UnifiedPackageAssetKind.Texture2D, "edit-3", Path.Combine(directory, "texture.json"), "Sky",
            "package.upk", "sky_d", true, new GuidedTextureModProjectEntry(Path.Combine(directory, "texture.json"), directory,
                new GuidedTextureModProjectManifest(1, "edit-3", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Sky", "lv_sky",
                    "lv_sky", "package.upk", "Texture2D", "sky_d", 1, "PF_DXT1", 2, 2, "native.json", picturePath,
                    GuidedTextureModState.Editing, null, [])));
        var goldToRed = new TextureRecolour(1, 0, 0, Strength: 1) { Selection = ColourSelection.ColourRange, RangeHue = 45, RangeWidth = 20 };
        var textured = new WorkingCopyTint().TintAsync(new MaterialTintRequest([texture], goldToRed)).GetAwaiter().GetResult();
        var pixels = PngRgbaCodec.ReadAsync(picturePath).GetAwaiter().GetResult().Pixels;
        Assert(textured.Changed == 1 && pixels[0] == 230 && pixels[1] == 0 && pixels[8] == 40 && pixels[10] == 220,
            $"the texture working copy was not recoloured in its gold pixels only: {string.Join(",", pixels)}");
        var unchanged = new WorkingCopyTint().TintAsync(new MaterialTintRequest([texture],
            goldToRed with { RangeHue = 120 })).GetAwaiter().GetResult();
        Assert(unchanged.Unchanged == 1 && unchanged.Changed == 0, "a texture with none of the chosen colours was counted as changed");
        var other = new ArkiveModLocalEdit(UnifiedPackageAssetKind.StaticMesh, "edit-2", documentPath, "Rock", "package.upk", "rock_sm",
            true, new object());
        var skipped = new WorkingCopyTint().TintAsync(new MaterialTintRequest([other], new TextureRecolour(1, 0, 0, Strength: 1)))
            .GetAwaiter().GetResult();
        Assert(skipped.Skipped.Count == 1 && skipped.Changed == 0, "a mesh working copy was not skipped with a reason");
    }
}
