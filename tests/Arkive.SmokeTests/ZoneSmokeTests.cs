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
    ];

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
