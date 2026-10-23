namespace Arkive.SmokeTests;

internal static class DiscoverySmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("matches multi-term asset searches", TestAssetSearch),
        new("lets a base body's class filter take in the classes built on it, and finds launch class skins", TestArchetypeFamilies),
        new("searches a persistent package index", TestPackageIndexSearch),
        new("audits playable-character texture families without NPC leakage", TestCharacterTextureCoverageAudit),
        new("limits indexed searches to selected fields", TestPackageIndexSearchScopes),
        new("searches a compressed package index", TestCompressedPackageIndexSearch),
        new("skips packages that cannot match an object-name search", TestPackageNameFilter),
        new("reports package index health", TestPackageIndexHealth),
        new("resolves localized game names to assets", TestGameAssetMetadata),
        new("ranks and qualifies evidence-backed asset discovery", TestAssetDiscovery),
        new("resolves qualified avatar items and verified dependencies", TestAvatarItemDependencies),
        new("browses playable customization and NPC asset relationships", TestEntityBrowser),
        new("explores evidence-labelled asset relationships", TestAssetRelationships),
        new("creates and compares compact client snapshots", TestClientSnapshots),
        new("creates lazy package and asset baselines", TestAssetBaselines),
        new("finds a zone by its in-game name and the level packages its map loads", TestZoneMaps),
    ];

    private static void TestZoneMaps()
    {
        var zones = new ZoneMapDocument(1, DateTimeOffset.UnixEpoch, "leveldata1.lpk", 0, 0,
        [
            new GameZone(37545, "Land of Death and Order", ["lv_kur_death_ed_05_ps"]),
            new GameZone(37546, "Collapsed Diaspero", ["lv_kur_death_ed_06_ps"]),
        ]);
        Assert(ZoneMapSidecar.Match(zones, "death order").Select(zone => zone.Id).SequenceEqual([37545L]),
            "a zone was not found by the words of its name");
        Assert(ZoneMapSidecar.Match(zones, "").Count == 0, "an empty query matched every zone");
        var packages = ZoneMapSidecar.PackagesOf("lv_kur_death_ed_05_ps",
            ["lv_kur_death_ed_05_ps", "lv_kur_death_ed_05_sl01", "LV_KUR_DEATH_ED_05_SCENE03A", "lv_kur_death_ed_06_sl01",
             "lv_kur_death_ed_051_sl01", "lv_kur_death_05"]).ToArray();
        Assert(packages.SequenceEqual(["lv_kur_death_ed_05_ps", "lv_kur_death_ed_05_sl01", "LV_KUR_DEATH_ED_05_SCENE03A"]),
            "a map's level packages were not the ones sharing its persistent level's stem: " + string.Join(", ", packages));
        var association = ZoneMapSidecar.Association(zones.Zones[0],
            new ZoneMapAsset("bg_rad_kazeroth_m", "bg_rad_kazeroth_statue05_d", "Used in map (lv_kur_death_ed_05_sl01)",
                GameAssetAssociationConfidence.Verified));
        Assert(association.DisplayName == "Land of Death and Order (map)" && association.EntityId == 37545 &&
               association.AssetReference == "bg_rad_kazeroth_m.bg_rad_kazeroth_statue05_d" &&
               association.Relation == ZoneMapSidecar.Relation,
            "a map asset was not shown with its zone's name");
    }

    private static void TestAssetSearch()
    {
        Assert(
            AssetSearch.Matches("character mesh", "pc_character_01", "SkeletalMesh"),
            "multi-term query did not match across fields");
        Assert(
            AssetSearch.Matches("texture -normal", "pc_face_texture_d", "Texture2D"),
            "negative term rejected a valid result");
        Assert(
            !AssetSearch.Matches("texture -normal", "pc_face_normal", "Texture2D"),
            "negative term did not exclude a result");
        Assert(
            AssetSearch.MatchesDisplayNames("Romantic Wear", ["Romantic Wear (Female Warrior)"]),
            "display-name token matching rejected the intended set name");
        Assert(
            !AssetSearch.MatchesDisplayNames("Romantic Wear", ["Romantic First Step Facewear"]),
            "display-name token matching treated the suffix of Facewear as the word Wear");
        Assert(
            AssetSearch.MatchesDisplayNames("Kazer", ["Archdemon Kazeros"]),
            "display-name token matching lost useful prefix search behavior");
    }

    private static void TestPackageIndexSearch()
    {
        var root = Path.Combine(fixtureDirectory, "library-root");
        var cache = Path.Combine(fixtureDirectory, "library-cache");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(cache);
        var source = Path.Combine(root, "encoded.upk");
        File.WriteAllBytes(source, []);
        var document = new PackageIndexDocument(
            source,
            "ENCODED",
            "character_package",
            0,
            File.GetLastWriteTimeUtc(source).Ticks,
            DateTimeOffset.UtcNow,
            [
                new IndexedAsset(7, AssetKind.SkeletalMesh, "SkeletalMesh", "pc_warrior_sk", 42, 12),
                new IndexedAsset(8, AssetKind.Texture, "Texture2D", "pc_warrior_d", 20, 54),
                new IndexedAsset(9, AssetKind.Material, "Material", "pc_warrior_mat", 16, 74),
                new IndexedAsset(10, AssetKind.Material, "MaterialInstanceConstant", "pc_warrior_mi", 16, 90),
            ],
            Error: null);
        File.WriteAllText(
            Path.Combine(cache, "fixture.json"),
            JsonSerializer.Serialize(document));

        var result = new PackageIndexStore(cache)
            .SearchAsync(root, "character warrior", "SkeletalMesh")
            .GetAwaiter()
            .GetResult();
        Assert(result.PackageCount == 1, "cached package was not discovered");
        Assert(result.TotalMatches == 1, "indexed asset was not found");
        Assert(result.Matches[0].ObjectName == "pc_warrior_sk", "wrong indexed asset was returned");

        var textureResult = new PackageIndexStore(cache)
            .SearchAsync(root, "character warrior", "texture")
            .GetAwaiter()
            .GetResult();
        Assert(textureResult.TotalMatches == 1, "category filter was not applied case-insensitively");
        Assert(textureResult.Matches[0].Kind == AssetKind.Texture, "category filter returned the wrong kind");

        var materialResult = new PackageIndexStore(cache)
            .SearchAsync(root, "character warrior", "Material")
            .GetAwaiter().GetResult();
        var materialInstanceResult = new PackageIndexStore(cache)
            .SearchAsync(root, "character warrior", "MaterialInstanceConstant")
            .GetAwaiter().GetResult();
        Assert(materialResult.TotalMatches == 2,
            "broad material category did not retain all material asset types");
        Assert(materialInstanceResult.TotalMatches == 1 &&
               materialInstanceResult.Matches[0].ClassName == "MaterialInstanceConstant",
            "MaterialInstanceConstant category did not filter by concrete UE class");
    }

    private static void TestCharacterTextureCoverageAudit()
    {
        var root = Path.Combine(fixtureDirectory, "character-audit-root");
        var cache = Path.Combine(fixtureDirectory, "character-audit-cache");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(cache);
        var metadataPath = Path.Combine(fixtureDirectory, "character-audit-metadata.json");
        var metadata = new GameAssetMetadataDocument(
            1,
            "English",
            fixtureDirectory,
            DateTimeOffset.UtcNow,
            [
                new GameAssetAssociation(
                    "Warrior", "Warrior", "PC", 101, "LookInfo",
                    "EFDLChar_PC_WR.PC_WR", "EFDLChar_PC_WR", "PC_WR"),
                new GameAssetAssociation(
                    "Warrior Female", "Warrior_Female", "PC", 111, "LookInfo",
                    "EFDLChar_PC_WR_F.PC_WR_F", "EFDLChar_PC_WR_F", "PC_WR_F"),
                new GameAssetAssociation(
                    "NPC Warrior", "npc.warrior", "Npc", 999, "Model",
                    "MN_PC_WR.MN_PC_WR", "MN_PC_WR", "MN_PC_WR"),
            ]);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata));

        var packages = new[]
        {
            WriteAuditCache("wr.json.gz", "pc_wr_00_face", "male.upk", "pc_wr_00_face_d"),
            WriteAuditCache("wrf.json.gz", "pc_wr_f_00_face", "female.upk", "pc_wr_f_00_face_d"),
            WriteAuditCache("fx.json.gz", "fx_pc_wr", "fx.upk", "pc_wr_fx_d"),
            WriteAuditCache("npc.json.gz", "mn_pc_wr", "npc.upk", "mn_pc_wr_d"),
        };
        using (var file = File.Create(Path.Combine(cache, "search-routing-v1.json.gz")))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            JsonSerializer.Serialize(gzip, new
            {
                SchemaVersion = 1,
                BuiltAt = DateTimeOffset.UtcNow,
                LibraryRoot = Path.GetFullPath(root),
                IndexIdentity = "fixture",
                MetadataSourcePath = metadataPath,
                MetadataSourceLength = new FileInfo(metadataPath).Length,
                MetadataSourceLastWriteUtcTicks = File.GetLastWriteTimeUtc(metadataPath).Ticks,
                MetadataAssociationCount = 3,
                Packages = packages,
                ObjectRoutes = new Dictionary<string, string[]>(),
            });
        }

        var report = new CharacterTextureCoverageAuditService()
            .AuditAsync(new CharacterTextureCoverageRequest(
                GameAssetMetadataIndex.Load(metadataPath), cache, root))
            .GetAwaiter()
            .GetResult();
        Assert(report.PlayerClassCount == 2, "PC.LookInfo seed count was incorrect");
        Assert(report.AppearancePackageCount == 2, "non-appearance/NPC packages leaked into the audit");
        Assert(report.TextureCount == 2, "playable-character texture count was incorrect");
        Assert(report.Textures.Single(item => item.ObjectName == "pc_wr_00_face_d").PlayerClassId == 101,
            "male warrior texture was assigned to the wrong PC row");
        Assert(report.Textures.Single(item => item.ObjectName == "pc_wr_f_00_face_d").PlayerClassId == 111,
            "longest player code did not protect the female package from the PC_WR prefix");
        Assert(report.Textures.All(item => item.CoverageStatus == CharacterTextureCoverageStatus.NotReconnoitred),
            "inventory-only audit unexpectedly claimed native format coverage");

        object WriteAuditCache(string cacheName, string alias, string fileName, string texture)
        {
            var source = Path.Combine(root, fileName);
            File.WriteAllBytes(source, []);
            var document = new PackageIndexDocument(
                source,
                Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant(),
                alias,
                0,
                File.GetLastWriteTimeUtc(source).Ticks,
                DateTimeOffset.UtcNow,
                [new IndexedAsset(1, AssetKind.Texture, "Texture2D", texture, 16, 4)],
                Error: null);
            using (var file = File.Create(Path.Combine(cache, cacheName)))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                JsonSerializer.Serialize(gzip, document);
            }

            return new
            {
                CacheFileName = cacheName,
                PackageAlias = alias,
                PhysicalPackageName = Path.GetFileNameWithoutExtension(fileName),
                AssetCount = 1,
                HasError = false,
            };
        }
    }

    private static void TestPackageIndexSearchScopes()
    {
        var root = Path.Combine(fixtureDirectory, "scoped-index-root");
        var cache = Path.Combine(fixtureDirectory, "scoped-index-cache");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(cache);
        var source = Path.Combine(root, "echidna_package.upk");
        var unrelatedSource = Path.Combine(root, "unrelated_package.upk");
        File.WriteAllBytes(source, []);
        File.WriteAllBytes(unrelatedSource, []);
        var package = new PackageIndexDocument(
            source,
            "ECHIDNA_PACKAGE",
            "raid_package",
            0,
            File.GetLastWriteTimeUtc(source).Ticks,
            DateTimeOffset.UtcNow,
            [new IndexedAsset(1, AssetKind.SkeletalMesh, "SkeletalMesh", "boss_snake_sk", 42, 12)],
            Error: null);
        File.WriteAllText(Path.Combine(cache, "fixture.json"), JsonSerializer.Serialize(package));
        var unrelatedPackage = new PackageIndexDocument(
            unrelatedSource,
            "UNRELATED_PACKAGE",
            "unrelated_package",
            0,
            File.GetLastWriteTimeUtc(unrelatedSource).Ticks,
            DateTimeOffset.UtcNow,
            [
                new IndexedAsset(1, AssetKind.StaticMesh, "StaticMesh", "environment_rock", 20, 4),
                new IndexedAsset(2, AssetKind.Texture, "Texture2D", "global_portrait", 20, 24),
            ],
            Error: null);
        File.WriteAllText(
            Path.Combine(cache, "unrelated.json"),
            JsonSerializer.Serialize(unrelatedPackage));
        var metadataPath = Path.Combine(fixtureDirectory, "scoped-index-metadata.json");
        var metadata = new GameAssetMetadataDocument(
            1,
            "English",
            fixtureDirectory,
            DateTimeOffset.UtcNow,
            [
                new GameAssetAssociation(
                    "Echidna",
                    "tip.name.npc.echidna",
                    "Npc",
                    1,
                    "Model",
                    "raid_package.boss_snake_sk",
                    "raid_package",
                    "boss_snake_sk"),
                new GameAssetAssociation(
                    "Unpackaged Portrait",
                    "tip.name.fixture.portrait",
                    "Npc",
                    2,
                    "Portrait",
                    "global_portrait",
                    "",
                    "global_portrait"),
            ]);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata));
        var metadataIndex = GameAssetMetadataIndex.Load(metadataPath);
        var store = new PackageIndexStore(cache);

        var allFieldsBuiltRouting = false;
        foreach (var query in new[] { "raid_package", "raid_package.upk", "echidna_package.upk", "boss_snake", "Echidna" })
        {
            var allFields = store.SearchAsync(root, query, null, metadataIndex,
                PackageIndexSearchScope.AllFields).GetAwaiter().GetResult();
            Assert(allFields.TotalMatches == 1 && allFields.Matches[0].ObjectName == "boss_snake_sk",
                $"all-fields search did not match package, filename, object, or game name: {query}");
            allFieldsBuiltRouting |= allFields.RoutingIndexBuilt;
        }
        // A game-name term in an all-field search is routed through the game database too.
        Assert(allFieldsBuiltRouting, "the all-field search for a game name did not build routing");

        var objectOnly = store.SearchAsync(
                root,
                "boss_snake",
                null,
                metadataIndex,
                PackageIndexSearchScope.ObjectName)
            .GetAwaiter()
            .GetResult();
        Assert(objectOnly.TotalMatches == 1, "object-name-only search did not match the object field");

        var objectExcludesGameName = store.SearchAsync(
                root,
                "Echidna",
                null,
                metadataIndex,
                PackageIndexSearchScope.ObjectName)
            .GetAwaiter()
            .GetResult();
        Assert(objectExcludesGameName.TotalMatches == 0, "object-only search matched an in-game name");

        var gameNameOnly = store.SearchAsync(
                root,
                "Echidna",
                null,
                metadataIndex,
                PackageIndexSearchScope.InGameName)
            .GetAwaiter()
            .GetResult();
        Assert(gameNameOnly.TotalMatches == 1, "in-game-name-only search did not match the display name");
        Assert(gameNameOnly.UsedRoutingIndex, "in-game-name-only search did not use routing");
        Assert(!gameNameOnly.RoutingIndexBuilt, "the in-game-name search rebuilt routing it could reuse");
        Assert(gameNameOnly.RoutedPackageCount == 1, "routing did not exclude the unrelated package");
        Assert(File.Exists(store.SearchRoutingPath), "routing sidecar was not persisted");
        var dependencyPackages = store.LoadPackagesByAliasAsync(root, ["RAID_PACKAGE"], metadataIndex)
            .GetAwaiter().GetResult();
        Assert(dependencyPackages.Count == 1 && dependencyPackages[0].SourcePath == source &&
               dependencyPackages[0].Assets.Any(asset => asset.ObjectName == "boss_snake_sk"),
            "exact dependency lookup failed to restrict decoding to the requested package");

        var gameNameExcludesObject = store.SearchAsync(
                root,
                "boss_snake",
                null,
                metadataIndex,
                PackageIndexSearchScope.InGameName)
            .GetAwaiter()
            .GetResult();
        Assert(gameNameExcludesObject.TotalMatches == 0, "in-game-name-only search matched the object field");

        var objectAndGameName = store.SearchAsync(
                root,
                "boss_snake Echidna",
                null,
                metadataIndex,
                PackageIndexSearchScope.ObjectAndInGameName)
            .GetAwaiter()
            .GetResult();
        Assert(objectAndGameName.TotalMatches == 1,
            "combined search did not match terms across object and in-game name fields");

        var objectOnlyRoute = new PackageIndexStore(cache).SearchAsync(
                root,
                "Unpackaged Portrait",
                "Texture",
                metadataIndex,
                PackageIndexSearchScope.InGameName)
            .GetAwaiter()
            .GetResult();
        Assert(objectOnlyRoute.TotalMatches == 1,
            "package-less database object reference was not routed");
        Assert(objectOnlyRoute.Matches[0].ObjectName == "global_portrait",
            "package-less routing returned the wrong object");
        Assert(!objectOnlyRoute.RoutingIndexBuilt,
            "persisted routing sidecar was rebuilt unnecessarily");

        var updatedMetadata = metadata with
        {
            Associations = metadata.Associations.Concat(
            [
                new GameAssetAssociation(
                    "Routing Invalidation Fixture",
                    "tip.name.fixture.changed",
                    "Npc",
                    3,
                    "Model",
                    "unrelated_package.environment_rock",
                    "unrelated_package",
                    "environment_rock"),
            ]).ToArray(),
        };
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(updatedMetadata));
        var updatedMetadataIndex = GameAssetMetadataIndex.Load(metadataPath);
        var rebuiltRoute = new PackageIndexStore(cache).SearchAsync(
                root,
                "Routing Invalidation Fixture",
                "StaticMesh",
                updatedMetadataIndex,
                PackageIndexSearchScope.InGameName)
            .GetAwaiter()
            .GetResult();
        Assert(rebuiltRoute.RoutingIndexBuilt,
            "routing sidecar was not invalidated after the metadata mapping changed");
        Assert(rebuiltRoute.TotalMatches == 1,
            "rebuilt routing sidecar did not return the new metadata association");
    }

    private static void TestCompressedPackageIndexSearch()
    {
        var root = Path.Combine(fixtureDirectory, "compressed-index-root");
        var cache = Path.Combine(fixtureDirectory, "compressed-index-cache");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(cache);
        var source = Path.Combine(root, "compressed.upk");
        File.WriteAllBytes(source, []);
        var document = new PackageIndexDocument(
            source,
            "COMPRESSED",
            "compressed_package",
            0,
            File.GetLastWriteTimeUtc(source).Ticks,
            DateTimeOffset.UtcNow,
            [new IndexedAsset(3, AssetKind.Texture, "Texture2D", "compressed_texture", 20, 4)],
            Error: null);
        var cachePath = Path.Combine(cache, "fixture.json.gz");
        using (var file = File.Create(cachePath))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            JsonSerializer.Serialize(gzip, document);
        }

        var result = new PackageIndexStore(cache)
            .SearchAsync(root, "compressed texture", "Texture")
            .GetAwaiter()
            .GetResult();
        Assert(result.TotalMatches == 1, "compressed cached asset was not found");
        Assert(result.Matches[0].PackageAssetCount == 1, "package asset count was not attached to the match");
    }

    private static void TestPackageNameFilter()
    {
        var root = Path.Combine(fixtureDirectory, "name-filter-root");
        var cache = Path.Combine(fixtureDirectory, "name-filter-cache");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(cache);
        void WritePackage(int index, params string[] objects)
        {
            var source = Path.Combine(root, $"package_{index}.upk");
            File.WriteAllBytes(source, []);
            var document = new PackageIndexDocument(
                source, $"PHYS{index}", $"package_{index}", 0, 0, DateTimeOffset.UtcNow,
                objects.Select((name, export) => new IndexedAsset(export, AssetKind.Texture, "Texture2D", name, 20, 4)).ToArray(),
                Error: null);
            File.WriteAllText(Path.Combine(cache, $"package_{index}.json"), JsonSerializer.Serialize(document));
        }
        for (var index = 0; index < 20; index++) WritePackage(index, $"filler_{index}_d", $"filler_{index}_n");
        WritePackage(20, "boss_snake_d", "boss_snake_n");
        WritePackage(21, "boss_spider_d");

        PackageIndexSearchResult Search(PackageIndexStore store, string query, PackageIndexSearchScope scope) =>
            store.SearchAsync(root, query, null, searchScope: scope).GetAwaiter().GetResult();
        var first = Search(new PackageIndexStore(cache), "boss_snake", PackageIndexSearchScope.ObjectName);
        Assert(first.TotalMatches == 2 && first.PackageCount == 22, "the first search did not read every package to build the filter");

        // A fresh store loads the saved filter and reads only packages that can contain every term.
        var store = new PackageIndexStore(cache);
        var routed = Search(store, "boss_snake", PackageIndexSearchScope.ObjectName);
        Assert(routed.TotalMatches == 2 && routed.UsedRoutingIndex && routed.PackageCount == 1,
            $"the name filter did not skip unrelated packages: read {routed.PackageCount}");
        Assert(Search(store, "BOSS -spider", PackageIndexSearchScope.ObjectName) is { TotalMatches: 2, PackageCount: 2 },
            "case-insensitive or excluded terms were filtered wrongly");
        Assert(Search(store, "package_21", PackageIndexSearchScope.AllFields) is { TotalMatches: 1, PackageCount: 1 },
            "an all-field search did not match through the package name");
        Assert(Search(store, "_d", PackageIndexSearchScope.ObjectName) is { TotalMatches: 22, PackageCount: 22 },
            "a term shorter than a trigram was filtered");
        Assert(Search(store, "snake texture", PackageIndexSearchScope.AllFields).TotalMatches == 2,
            "an asset-kind term was filtered out of an all-field search");

        // A rewritten package has a new length, so its stale entry is ignored until it is read again.
        WritePackage(5, "filler_5_d", "boss_snake_s");
        var changed = Search(store, "boss_snake", PackageIndexSearchScope.ObjectName);
        Assert(changed.TotalMatches == 3 && changed.PackageCount == 2, "a changed package was skipped by its stale filter entry");
        Assert(Search(new PackageIndexStore(cache), "boss_snake", PackageIndexSearchScope.ObjectName).PackageCount == 2,
            "the refreshed filter entry was not saved");
    }

    private static void TestPackageIndexHealth()
    {
        var root = Path.Combine(fixtureDirectory, "health-index-root");
        var cache = Path.Combine(fixtureDirectory, "health-index-cache");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(cache);
        var goodSource = Path.Combine(root, "good.upk");
        var badSource = Path.Combine(root, "stub.upk");
        File.WriteAllBytes(goodSource, []);
        File.WriteAllBytes(badSource, new byte[129]);
        var good = new PackageIndexDocument(
            goodSource,
            "good",
            "good",
            0,
            File.GetLastWriteTimeUtc(goodSource).Ticks,
            DateTimeOffset.UtcNow,
            [new IndexedAsset(1, AssetKind.Texture, "Texture2D", "good_d", 20, 4)],
            Error: null);
        var bad = new PackageIndexDocument(
            badSource,
            "stub",
            "stub",
            129,
            File.GetLastWriteTimeUtc(badSource).Ticks,
            DateTimeOffset.UtcNow,
            [],
            "fixture failure");
        File.WriteAllText(Path.Combine(cache, "good.json"), JsonSerializer.Serialize(good));
        File.WriteAllText(Path.Combine(cache, "bad.json"), JsonSerializer.Serialize(bad));

        var health = new PackageIndexStore(cache)
            .GetHealthAsync(root)
            .GetAwaiter()
            .GetResult();
        Assert(health.CachedDocumentCount == 2, "health report returned the wrong document count");
        Assert(health.SuccessfulDocumentCount == 1, "health report returned the wrong success count");
        Assert(health.FailedDocumentCount == 1, "health report returned the wrong failure count");
        Assert(health.IndexedAssetCount == 1, "health report returned the wrong asset count");
        Assert(health.Failures.Count == 1, "health report omitted the package failure");
        Assert(!health.ProvenanceMatches, "legacy health report incorrectly claimed verified provenance");

        var backendPath = Path.Combine(fixtureDirectory, "umodel-fixture.exe");
        File.WriteAllBytes(backendPath, [1, 2, 3, 4]);
        var backendFile = new FileInfo(backendPath);
        var manifest = new PackageIndexManifest(
            PackageIndexStore.CurrentSchemaVersion,
            "lostark-na-nameresolve-noanim-list-v1",
            backendPath,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backendPath))),
            backendFile.Length,
            backendFile.LastWriteTimeUtc.Ticks,
            root,
            DateTimeOffset.UtcNow,
            2,
            1,
            1,
            1);
        File.WriteAllText(
            Path.Combine(cache, "index-manifest.json"),
            JsonSerializer.Serialize(manifest));
        var verified = new PackageIndexStore(cache)
            .GetHealthAsync(root, new UmodelBridge(backendPath))
            .GetAwaiter()
            .GetResult();
        Assert(verified.ProvenanceMatches, "matching index provenance was not verified");
    }

    private static void TestGameAssetMetadata()
    {
        var metadataPath = Path.Combine(fixtureDirectory, "game-asset-metadata.json");
        var document = new GameAssetMetadataDocument(
            1,
            "English",
            fixtureDirectory,
            DateTimeOffset.UtcNow,
            [
                new GameAssetAssociation(
                    "Demon Dog",
                    "tip.name.monster_500010",
                    "Npc",
                    500010,
                    "Model",
                    "EFDLChar_MN_PADD_00.MN_PADD_00",
                    "EFDLChar_MN_PADD_00",
                    "MN_PADD_00"),
                new GameAssetAssociation(
                    "Angel the Frog",
                    "tip.name.npc_townsfolk_28128",
                    "Npc",
                    28128,
                    "Model",
                    "EFDLChar_PR_LHEN_00.PR_LHEN_01",
                    "EFDLChar_PR_LHEN_00",
                    "PR_LHEN_01"),
            ]);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(document));
        var index = GameAssetMetadataIndex.Load(metadataPath);
        var matches = index.Find("mn_padd_00", "encoded", "MN_PADD_00_sk");
        Assert(matches.Count == 1, "the exact package/object database reference was not resolved");
        Assert(matches[0].DisplayName == "Demon Dog", "the localized name was not returned");

        var root = Path.Combine(fixtureDirectory, "metadata-library-root");
        var cache = Path.Combine(fixtureDirectory, "metadata-library-cache");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(cache);
        var source = Path.Combine(root, "mn_padd_00.upk");
        File.WriteAllBytes(source, []);
        var package = new PackageIndexDocument(
            source,
            "mn_padd_00",
            "mn_padd_00",
            0,
            File.GetLastWriteTimeUtc(source).Ticks,
            DateTimeOffset.UtcNow,
            [new IndexedAsset(1, AssetKind.SkeletalMesh, "SkeletalMesh", "MN_PADD_00_sk", 42, 12)],
            Error: null);
        File.WriteAllText(Path.Combine(cache, "fixture.json"), JsonSerializer.Serialize(package));
        var siblingSource = Path.Combine(root, "pr_lhen_01.upk");
        File.WriteAllBytes(siblingSource, []);
        File.WriteAllText(
            Path.Combine(cache, "sibling-fixture.json"),
            JsonSerializer.Serialize(new PackageIndexDocument(
                siblingSource,
                "pr_lhen_01",
                "pr_lhen_01",
                0,
                File.GetLastWriteTimeUtc(siblingSource).Ticks,
                DateTimeOffset.UtcNow,
                [new IndexedAsset(
                    1, AssetKind.SkeletalMesh, "SkeletalMesh", "pr_lhen_01_sk", 42, 12)],
                Error: null)));
        var result = new PackageIndexStore(cache)
            .SearchAsync(root, "Demon Dog", "SkeletalMesh", index)
            .GetAwaiter()
            .GetResult();
        Assert(result.TotalMatches == 1, "localized-name search did not find the indexed asset");
        Assert(result.Matches[0].GameMetadata.Count == 1, "metadata was not included in the result");
        var siblingResult = new PackageIndexStore(cache)
            .SearchAsync(
                root,
                "Angel the Frog",
                "SkeletalMesh",
                index,
                PackageIndexSearchScope.InGameName)
            .GetAwaiter()
            .GetResult();
        Assert(siblingResult.TotalMatches == 1 &&
               siblingResult.Matches[0].GameMetadata.Any(item => item.Relation ==
                   GameAssetAssociationRelation.DeterministicModelAsset),
            "the EFDL model wrapper was not routed to its sibling object package");
    }

    private static void TestAssetDiscovery()
    {
        var metadataPath = Path.Combine(fixtureDirectory, "asset-discovery-metadata.json");
        var association = new GameAssetAssociation(
            "Romantic Wear",
            "item.romanticwear",
            "Item",
            237,
            "Model",
            "PC_WR_F_AV_237B.PC_WR_F_AV_237B_UPPER",
            "PC_WR_F_AV_237B",
            "PC_WR_F_AV_237B_UPPER")
        {
            Archetype = "Female Warrior",
            Confidence = GameAssetAssociationConfidence.Verified,
            Relation = GameAssetAssociationRelation.VerifiedSerializedDependency,
        };
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(new GameAssetMetadataDocument(
            1, "English", fixtureDirectory, DateTimeOffset.UtcNow, [association])));
        var metadata = GameAssetMetadataIndex.Load(metadataPath);
        var service = new AssetDiscoveryService();
        var suggestions = service.Suggest(metadata, "Roman", "Female Warrior");
        Assert(suggestions.Any(item => item.DisplayName == "Romantic Wear" &&
                                       item.Archetype == "Female Warrior"),
            "discovery did not qualify a partial in-game item name by archetype");

        PackageIndexMatch[] matches =
        [
            new("a.upk", "PC_WR_F_AV_237B", "AAA.upk", 2, 1,
                AssetKind.SkeletalMesh, "SkeletalMesh", "PC_WR_F_AV_237B_UPPER", 500, 20,
                [association]),
            new("b.upk", "misc", "BBB.upk", 1, 1,
                AssetKind.Texture, "Texture2D", "romantic_noise", 100, 10, []),
        ];
        var ranked = service.Rank(matches, "Romantic Wear", PackageIndexSearchScope.ObjectAndInGameName);
        Assert(ranked[0].Match.PackageAlias == "PC_WR_F_AV_237B" &&
               ranked[0].MatchReason.Contains("in-game name", StringComparison.OrdinalIgnoreCase),
            "discovery relevance did not prefer the verified in-game-name match");
        var facets = service.BuildFacets(matches);
        Assert(facets.Any(item => item.Group == "Category" && item.Value == "SkeletalMesh") &&
               facets.Any(item => item.Group == "Class type" && item.Value == "Female Warrior") &&
               facets.Any(item => item.Group == "Link evidence" && item.Value == "verified dependency"),
            "discovery facets omitted category, class type, or evidence");
    }

    private static void TestAvatarItemDependencies()
    {
        var metadataPath = Path.Combine(fixtureDirectory, "avatar-item-metadata.json");
        var document = new GameAssetMetadataDocument(
            2,
            "English",
            fixtureDirectory,
            DateTimeOffset.UtcNow,
            [
                new GameAssetAssociation(
                    "Warrior (Female)", "Warrior_Female", "PC", 111, "LookInfo",
                    "EFDLChar_PC_WR_F.PC_WR_F", "EFDLChar_PC_WR_F", "PC_WR_F"),
                new GameAssetAssociation(
                    "Romantic Wear Chestpiece & Pants", "tip.name.item_321530410", "Item",
                    321530410, "Model",
                    "EFDLItem_PC_WR_F_AV_178B_Dress.PC_WR_F_AV_178B_Dress",
                    "EFDLItem_PC_WR_F_AV_178B_Dress", "PC_WR_F_AV_178B_Dress"),
                new GameAssetAssociation(
                    "Plantit Headwear", "tip.name.item_311544025", "Item",
                    311544025, "Model",
                    "EFDLItem_PC_WR_F_AV_208-1_Head.PC_WR_F_AV_208-1_Head",
                    "EFDLItem_PC_WR_F_AV_208-1_Head", "PC_WR_F_AV_208-1_Head"),
                new GameAssetAssociation(
                    "Heat Wave Greatsword", "tip.name.item.wave", "Item",
                    321000001, "Model",
                    "EFDLItem_WP_WBK_AV_237-1.WP_WBK_AV_237-1",
                    "EFDLItem_WP_WBK_AV_237-1", "WP_WBK_AV_237-1"),
            ]);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(document));

        var translated = GameAssetMetadataIndex.Load(metadataPath).Find(
            "pc_wr_f_av_178b", "physical", "pc_wr_f_av_178b_dress_sk");
        Assert(translated.Any(item =>
                item.DisplayName == "Romantic Wear (Female Warrior)" &&
                item.Archetype == "Female Warrior" &&
                item.Confidence == GameAssetAssociationConfidence.High &&
                item.Relation == GameAssetAssociationRelation.DeterministicItemAsset),
            "the item wrapper was not translated into a qualified avatar package relationship");
        var variant = GameAssetMetadataIndex.Load(metadataPath).Find(
            "pc_wr_f_av_208", "physical", "pc_wr_f_av_208-1_head_sk");
        Assert(variant.Any(item =>
                item.DisplayName == "Plantit (Female Warrior)" &&
                item.PackageName.Equals("PC_WR_F_AV_208", StringComparison.OrdinalIgnoreCase)),
            "the numbered item variant was not routed through its containing avatar package");
        var equipmentVariant = GameAssetMetadataIndex.Load(metadataPath).Find(
            "wp_wbk_av_237", "physical", "wp_wbk_av_237_sk");
        Assert(equipmentVariant.Any(item =>
                item.DisplayName == "Heat Wave Greatsword" &&
                item.Relation == GameAssetAssociationRelation.DeterministicItemAsset),
            "the numbered equipment variant was not routed to its shared mesh package");

        var sidecarPath = GameAssetDependencySidecar.GetDefaultPath(metadataPath);
        var dependency = new GameAssetAssociation(
            "Romantic Wear (Female Warrior)",
            "tip.name.item_321530410",
            "Item",
            321530410,
            "Dependency.Texture2D",
            "PC_DL_AV_178B.pc_dl_av_178b_dress_d",
            "PC_DL_AV_178B",
            "pc_dl_av_178b_dress_d")
        {
            Confidence = GameAssetAssociationConfidence.Verified,
            Relation = GameAssetAssociationRelation.VerifiedSerializedDependency,
            SourceAssetReference = "PC_WR_F_AV_178B.PC_WR_F_AV_178B_DRESS",
            Archetype = "Female Warrior",
        };
        var otherArchetypeDependency = dependency with
        {
            DisplayName = "Romantic Wear (Female Martial Artist)",
            EntityId = 321530420,
            Archetype = "Female Martial Artist",
            SourceAssetReference = "PC_FT_AV_178B.PC_FT_AV_178B_DRESS",
        };
        GameAssetDependencySidecar.SaveAsync(
                sidecarPath,
                new GameAssetDependencyDocument(
                    GameAssetDependencySidecar.CurrentVersion,
                    metadataPath,
                    DateTimeOffset.UtcNow,
                    [dependency, otherArchetypeDependency],
                    []))
            .GetAwaiter()
            .GetResult();

        var root = Path.Combine(fixtureDirectory, "avatar-item-root");
        var cache = Path.Combine(fixtureDirectory, "avatar-item-cache");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(cache);
        var source = Path.Combine(root, "romantic-textures.upk");
        File.WriteAllBytes(source, []);
        File.WriteAllText(
            Path.Combine(cache, "romantic.json"),
            JsonSerializer.Serialize(new PackageIndexDocument(
                source,
                "ROMANTICPHYSICAL",
                "pc_dl_av_178b",
                0,
                File.GetLastWriteTimeUtc(source).Ticks,
                DateTimeOffset.UtcNow,
                [new IndexedAsset(
                    1, AssetKind.Texture, "Texture2D", "pc_dl_av_178b_dress_d", 64, 4)],
                Error: null)));
        var metadata = GameAssetMetadataIndex.Load(metadataPath);
        var result = new PackageIndexStore(cache)
            .SearchAsync(
                root,
                "Romantic Wear Female Warrior",
                "Texture",
                metadata,
                PackageIndexSearchScope.InGameName)
            .GetAwaiter()
            .GetResult();
        Assert(result.TotalMatches == 1,
            "qualified in-game-name search did not return the verified dependency texture");
        Assert(result.Matches[0].GameMetadata.Any(item =>
                item.Confidence == GameAssetAssociationConfidence.Verified),
            "verified dependency provenance was not retained in the search result");

        var filtered = new PackageIndexStore(cache)
            .SearchAsync(
                root,
                "Romantic Wear",
                "Texture",
                metadata,
                PackageIndexSearchScope.InGameName,
                archetype: "Female Warrior")
            .GetAwaiter()
            .GetResult();
        Assert(filtered.TotalMatches == 1,
            "the archetype filter did not retain the shared Female Warrior texture");
        Assert(filtered.Matches[0].GameMetadata.All(item =>
                item.Archetype == "Female Warrior"),
            "the archetype filter leaked another playable class into result metadata");
        Assert(metadata.Archetypes.Contains("Female Warrior") &&
               metadata.Archetypes.Contains("Female Martial Artist"),
            "the metadata index did not expose its available playable archetypes");
    }

    private static void TestEntityBrowser()
    {
        var metadataPath = Path.Combine(fixtureDirectory, "entity-browser-metadata.json");
        var associations = new[]
        {
            new GameAssetAssociation(
                "Warrior Female", "Warrior_Female", "PC", 111, "LookInfo",
                "EFDLChar_PC_WR_F.PC_WR_F", "EFDLChar_PC_WR_F", "PC_WR_F"),
            new GameAssetAssociation(
                "Slayer", "Berserker_Female", "PC", 112, "LookInfo",
                "EFDLChar_PC_WBK_F.PC_WBK_F", "EFDLChar_PC_WBK_F", "PC_WBK_F"),
            new GameAssetAssociation(
                "Martial Artist Female", "Fighter", "PC", 301, "LookInfo",
                "EFDLChar_PC_FT.PC_FT", "EFDLChar_PC_FT", "PC_FT"),
            new GameAssetAssociation(
                "LanceMaster", "LanceMaster", "PC", 305, "LookInfo",
                "EFDLChar_PC_FLM.PC_FLM", "EFDLChar_PC_FLM", "PC_FLM"),
            new GameAssetAssociation(
                "DragonHuman", "DragonHuman", "PC", 701, "LookInfo",
                "EFDLChar_PC_DK.PC_DK", "EFDLChar_PC_DK", "PC_DK"),
            new GameAssetAssociation(
                "Shadowhunter", "Demonic", "PC", 403, "LookInfo",
                "EFDLChar_PC_DMN.PC_DMN", "EFDLChar_PC_DMN", "PC_DMN"),
            new GameAssetAssociation(
                "Gladiator Chestpiece", "tip.name.item_1", "Item", 1, "Model",
                "EFDLItem_PC_DDM_AV_001_Upper.PC_DDM_AV_001_Upper", "EFDLItem_PC_DDM_AV_001_Upper", "PC_DDM_AV_001_Upper"),
            new GameAssetAssociation(
                "Lance Chestpiece", "tip.name.item_2", "Item", 2, "Model",
                "EFDLItem_PC_FLM_AV_001_Upper.PC_FLM_AV_001_Upper", "EFDLItem_PC_FLM_AV_001_Upper", "PC_FLM_AV_001_Upper"),
            new GameAssetAssociation(
                "Raid Boss", "tip.name.raid_boss", "Npc", 9001, "Model",
                "EFDLChar_RAID_BOSS.RAID_BOSS", "EFDLChar_RAID_BOSS", "RAID_BOSS"),
        };
        var document = new GameAssetMetadataDocument(
            2,
            "English",
            fixtureDirectory,
            DateTimeOffset.UtcNow,
            associations,
            [
                new GameEntityDefinition(
                    "Warrior Female", "Warrior_Female", "PC", 111, "Player archetype",
                    "base=-", null, "EFDLChar_PC_WR_F.PC_WR_F"),
                new GameEntityDefinition(
                    "Slayer", "Berserker_Female", "PC", 112, "Playable class",
                    "base=111", 111, "EFDLChar_PC_WBK_F.PC_WBK_F"),
                new GameEntityDefinition(
                    "Martial Artist Female", "Fighter", "PC", 301, "Player archetype",
                    "base=-", null, "EFDLChar_PC_FT.PC_FT"),
                new GameEntityDefinition(
                    "LanceMaster", "LanceMaster", "PC", 305, "Playable class",
                    "base=301", 301, "EFDLChar_PC_FLM.PC_FLM"),
                new GameEntityDefinition(
                    "DragonHuman", "DragonHuman", "PC", 701, "Playable class",
                    "base=701", null, "EFDLChar_PC_DK.PC_DK"),
                new GameEntityDefinition(
                    "Raid Boss", "tip.name.raid_boss", "Npc", 9001, "Combat NPC",
                    "grade=7", null, "EFDLChar_RAID_BOSS.RAID_BOSS"),
            ],
            [
                new CharacterCustomizationDefinition(
                    111, 1, 28, 22, "", "CharacterPreset_BK_F", 47, ""),
                new CharacterCustomizationDefinition(
                    301, 1, 3, 2, "", "CharacterPreset_Fighter", 254, ""),
                new CharacterCustomizationDefinition(
                    701, 1, 16, 15, "", "CharacterPreset_DK", 41, ""),
            ]);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(document));
        var metadata = GameAssetMetadataIndex.Load(metadataPath);
        Assert(metadata.Archetypes.Contains("Shadowhunter") && !metadata.Archetypes.Contains("DDM"),
            "Shadowhunter gear (PC_DDM) was not listed under its class");
        Assert(metadata.Archetypes.Contains("LanceMaster") && GameClassNames.Label("LanceMaster") == "Glaivier (LanceMaster)" &&
               GameClassNames.Label("Shadowhunter") == "Shadowhunter" && GameClassNames.Label("SoulEater") == "Souleater",
            "class types were not shown with their localised names");
        Assert(metadata.EntityCount == 6, "entity definitions were not loaded");
        Assert(metadata.CustomizationOptionCount == 3, "customization definitions were not loaded");
        var slayer = metadata.Entities.Single(entity => entity.EntityId == 112);
        var face = metadata.FindCustomizationOptions(slayer).Single();
        Assert(face.UiPosition == 28 && face.ObjectUnit == 22,
            "ordered inherited customization identity was not preserved");
        Assert(face.CandidateObjectNames.Contains("pc_wr_f_17_face_d") &&
               face.Evidence.Contains("Verified", StringComparison.OrdinalIgnoreCase),
            "Slayer face 28 did not use the verified Western/global face mapping");
        Assert(WesternGlobalCustomizationMappings.FindSlayerFacePositions("pc_wr_f_18_face_d")
                   .SequenceEqual([1, 14, 24, 30]),
            "the verified repeated Slayer face-to-texture mapping was not preserved");
        Assert(WesternGlobalCustomizationMappings.DescribeVerifiedUses("pc_wr_f_20_face_d") ==
               "Slayer face 12",
            "the verified Slayer texture label was not resolved");
        Assert(WesternGlobalCustomizationMappings.TryGetSlayerFaceTextureNumber(7, out var faceSeven) &&
               faceSeven is null,
            "the explicitly unresolved Slayer face was not preserved");
        Assert(WesternGlobalCustomizationMappings.FindFemaleMartialArtistFacePositions(
                   "pc_ft_face_15_d").SequenceEqual([4, 6, 13, 17]),
            "the verified repeated Female Martial Artist face mapping was not preserved");
        Assert(WesternGlobalCustomizationMappings.DescribeVerifiedUses("pc_ft_face_14_d") ==
               "Female Martial Artist faces 5, 24",
            "the verified Female Martial Artist texture label was not resolved");
        Assert(WesternGlobalCustomizationMappings.CreateFemaleMartialArtistFaceCandidates(25)
                   .SequenceEqual(["pc_ft_face_10_d", "pc_ft_face_10_n", "pc_ft_face_10_s"]),
            "Female Martial Artist face candidates were not generated from the verified mapping");
        Assert(WesternGlobalCustomizationMappings.FindDragonHumanFacePositions("pc_dk_30_face_d")
                   .SequenceEqual([2, 13, 22]),
            "the verified repeated DragonHuman face-to-texture mapping was not preserved");
        Assert(WesternGlobalCustomizationMappings.DescribeVerifiedUses("pc_dk_13_face_d") ==
               "DragonHuman face 16",
            "the verified DragonHuman texture label was not resolved");
        Assert(WesternGlobalCustomizationMappings.TryGetDragonHumanFaceTextureNumber(
                   11, out var dragonHumanFaceEleven) && dragonHumanFaceEleven is null,
            "the explicitly unresolved DragonHuman face was not preserved");
        Assert(WesternGlobalCustomizationMappings.CreateDragonHumanFaceCandidates(20)
                   .SequenceEqual(["pc_dk_04_face_d", "pc_dk_04_face_n", "pc_dk_04_face_s"]),
            "DragonHuman face candidates were not generated from the verified mapping");
        var dragonHuman = metadata.Entities.Single(entity => entity.EntityId == 701);
        var dragonHumanFace = metadata.FindCustomizationOptions(dragonHuman).Single();
        Assert(dragonHumanFace.UiPosition == 16 &&
               dragonHumanFace.CandidateObjectNames.Contains("pc_dk_13_face_d") &&
               dragonHumanFace.Evidence.Contains("Verified", StringComparison.OrdinalIgnoreCase),
            "DragonHuman face 16 did not use the verified Western/global face mapping");
        var faceMetadata = metadata.Find("", "", "pc_wr_f_18_face_d");
        Assert(faceMetadata.Select(item => item.DisplayName).SequenceEqual(
                   ["Slayer face 1", "Slayer face 14", "Slayer face 24", "Slayer face 30"]),
            "verified Slayer face names were not attached to indexed texture assets");
        var dragonHumanMetadata = metadata.Find("", "", "pc_dk_28_face_d");
        Assert(dragonHumanMetadata.Select(item => item.DisplayName).SequenceEqual(
                   ["Guardianknight / DragonHuman face 1", "Guardianknight / DragonHuman face 21"]),
            "verified DragonHuman face names were not attached to indexed texture assets");
        var lanceMaster = metadata.Entities.Single(entity => entity.EntityId == 305);
        var lanceFace = metadata.FindCustomizationOptions(lanceMaster).Single();
        Assert(lanceFace.CandidateObjectNames.Contains("pc_ft_face_11_d") &&
               lanceFace.Evidence.Contains("Verified", StringComparison.OrdinalIgnoreCase),
            "LanceMaster did not inherit the verified Female Martial Artist face mapping");
        var femaleMartialArtistMetadata = metadata.Find("", "", "pc_ft_face_11_d");
        Assert(femaleMartialArtistMetadata.Select(item => item.DisplayName).SequenceEqual(
                   ["Female Martial Artist face 3", "Female Martial Artist face 8", "Female Martial Artist face 22"]),
            "verified Female Martial Artist face names were not attached to indexed texture assets");

        var root = Path.Combine(fixtureDirectory, "entity-browser-root");
        var cache = Path.Combine(fixtureDirectory, "entity-browser-cache");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(cache);
        var facePackage = Path.Combine(root, "face.upk");
        var lanceFacePackage = Path.Combine(root, "lance-face.upk");
        var bossPackage = Path.Combine(root, "boss.upk");
        File.WriteAllBytes(facePackage, []);
        File.WriteAllBytes(lanceFacePackage, []);
        File.WriteAllBytes(bossPackage, []);
        var faceDocument = new PackageIndexDocument(
            facePackage, "FACEPHYSICAL", "pc_wr_f_face", 0,
            File.GetLastWriteTimeUtc(facePackage).Ticks, DateTimeOffset.UtcNow,
            [
                new IndexedAsset(1, AssetKind.Texture, "Texture2D", "pc_wr_f_17_face_d", 100, 20),
                new IndexedAsset(2, AssetKind.Texture, "Texture2D", "pc_wr_f_17_face_n", 100, 120),
                new IndexedAsset(3, AssetKind.Texture, "Texture2D", "pc_wr_f_17_face_s", 100, 220),
            ],
            Error: null);
        var lanceFaceDocument = new PackageIndexDocument(
            lanceFacePackage, "LANCEFACEPHYSICAL", "pc_ft_00_face", 0,
            File.GetLastWriteTimeUtc(lanceFacePackage).Ticks, DateTimeOffset.UtcNow,
            [
                new IndexedAsset(4, AssetKind.Texture, "Texture2D", "pc_ft_face_11_d", 100, 320),
                new IndexedAsset(5, AssetKind.Texture, "Texture2D", "pc_ft_face_11_n", 100, 420),
                new IndexedAsset(6, AssetKind.Texture, "Texture2D", "pc_ft_face_11_s", 100, 520),
            ],
            Error: null);
        var bossDocument = new PackageIndexDocument(
            bossPackage, "BOSSPHYSICAL", "raid_boss", 0,
            File.GetLastWriteTimeUtc(bossPackage).Ticks, DateTimeOffset.UtcNow,
            [new IndexedAsset(1, AssetKind.SkeletalMesh, "SkeletalMesh", "raid_boss_sk", 100, 20)],
            Error: null);
        void WriteIndexDocument(string path, PackageIndexDocument value)
        {
            using var file = File.Create(path);
            using var gzip = new GZipStream(file, CompressionLevel.Fastest);
            JsonSerializer.Serialize(gzip, value);
        }

        WriteIndexDocument(Path.Combine(cache, "face.json.gz"), faceDocument);
        WriteIndexDocument(Path.Combine(cache, "lance-face.json.gz"), lanceFaceDocument);
        WriteIndexDocument(Path.Combine(cache, "boss.json.gz"), bossDocument);
        var store = new PackageIndexStore(cache);
        var faceResult = store.BrowseEntityAssetsAsync(
                root, slayer, metadata, face.CandidateObjectNames)
            .GetAwaiter().GetResult();
        Assert(faceResult.Matches.Count == 3 &&
               faceResult.Matches.All(match => match.Evidence == EntityAssetEvidence.CustomizationCandidate),
            "customization candidates were not resolved through routed appearance packages");
        var lanceResult = store.BrowseEntityAssetsAsync(
                root, lanceMaster, metadata, lanceFace.CandidateObjectNames)
            .GetAwaiter().GetResult();
        Assert(lanceResult.Matches.Count == 3 &&
               lanceResult.Matches.All(match => match.Asset.ObjectName.StartsWith(
                   "pc_ft_face_11_", StringComparison.OrdinalIgnoreCase)),
            $"LanceMaster verified face candidates were not resolved: " +
            $"count={lanceResult.Matches.Count}, names={string.Join(',', lanceResult.Matches.Select(match => match.Asset.ObjectName))}");

        var boss = metadata.Entities.Single(entity => entity.EntityId == 9001);
        var bossResult = store.BrowseEntityAssetsAsync(root, boss, metadata)
            .GetAwaiter().GetResult();
        Assert(bossResult.Matches.Count == 1 &&
               bossResult.Matches[0].Evidence == EntityAssetEvidence.DatabaseAssetFamily,
            "NPC model-family assets were not resolved from their direct database model reference");
    }

    private static void TestAssetRelationships()
    {
        var root = Path.Combine(fixtureDirectory, "relationship-root");
        var cache = Path.Combine(fixtureDirectory, "relationship-cache");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(cache);
        var bossPath = Path.Combine(root, "raid_boss.upk");
        var uiPath = Path.Combine(root, "raid_ui.upk");
        var texturePath = Path.Combine(root, "raid_boss_tex.upk");
        var unrelatedPath = Path.Combine(root, "environment.upk");
        foreach (var path in new[] { bossPath, uiPath, texturePath, unrelatedPath })
        {
            File.WriteAllBytes(path, []);
        }

        PackageIndexDocument Package(
            string path,
            string alias,
            params IndexedAsset[] assets) => new(
            path,
            Path.GetFileNameWithoutExtension(path),
            alias,
            0,
            File.GetLastWriteTimeUtc(path).Ticks,
            DateTimeOffset.UtcNow,
            assets,
            Error: null);

        var documents = new[]
        {
            Package(
                bossPath,
                "raid_boss",
                new IndexedAsset(1, AssetKind.SkeletalMesh, "SkeletalMesh", "boss_snake_sk", 100, 10),
                new IndexedAsset(2, AssetKind.Material, "MaterialInstanceConstant", "boss_snake_mat", 40, 110),
                new IndexedAsset(3, AssetKind.Object, "PhysicsAsset", "unrelated_local", 20, 150)),
            Package(
                uiPath,
                "raid_ui",
                new IndexedAsset(1, AssetKind.Texture, "Texture2D", "echidna_portrait", 50, 10)),
            Package(
                texturePath,
                "raid_boss_tex",
                new IndexedAsset(1, AssetKind.Texture, "Texture2D", "boss_snake_d", 50, 10)),
            Package(
                unrelatedPath,
                "environment",
                new IndexedAsset(1, AssetKind.StaticMesh, "StaticMesh", "tree_01", 50, 10)),
        };
        for (var index = 0; index < documents.Length; index++)
        {
            File.WriteAllText(
                Path.Combine(cache, $"relationship-{index}.json"),
                JsonSerializer.Serialize(documents[index]));
        }

        var metadataPath = Path.Combine(fixtureDirectory, "relationship-metadata.json");
        var metadata = new GameAssetMetadataDocument(
            1,
            "English",
            fixtureDirectory,
            DateTimeOffset.UtcNow,
            [
                new GameAssetAssociation(
                    "Echidna",
                    "tip.name.npc.echidna",
                    "Npc",
                    77,
                    "Model",
                    "raid_boss.boss_snake",
                    "raid_boss",
                    "boss_snake"),
                new GameAssetAssociation(
                    "Echidna",
                    "tip.name.npc.echidna",
                    "Npc",
                    77,
                    "Portrait",
                    "raid_ui.echidna_portrait",
                    "raid_ui",
                    "echidna_portrait"),
            ]);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata));
        var metadataIndex = GameAssetMetadataIndex.Load(metadataPath);
        var seedMetadata = metadataIndex.Find("raid_boss", "raid_boss", "boss_snake_sk");
        var seed = new PackageIndexMatch(
            bossPath,
            "raid_boss",
            "raid_boss",
            3,
            1,
            AssetKind.SkeletalMesh,
            "SkeletalMesh",
            "boss_snake_sk",
            100,
            10,
            seedMetadata);
        var result = new PackageIndexStore(cache)
            .ExploreRelationshipsAsync(root, seed, metadataIndex)
            .GetAwaiter()
            .GetResult();

        Assert(result.EntityAssociations.Count == 2, "related fields for the DB entity were not expanded");
        Assert(result.Matches.Any(match =>
            match.Asset.ExportIndex == 1 &&
            match.Asset.SourcePath == bossPath &&
            match.Evidence == AssetRelationshipEvidence.Seed), "selected seed was not retained");
        Assert(result.Matches.Any(match =>
            match.Asset.ObjectName == "echidna_portrait" &&
            match.Evidence == AssetRelationshipEvidence.DirectDatabaseReference),
            "direct database reference was not resolved");
        Assert(result.Matches.Any(match =>
            match.Asset.ObjectName == "boss_snake_mat" &&
            match.Evidence == AssetRelationshipEvidence.DatabaseAssetFamily),
            "database asset-family member was not identified");
        Assert(result.Matches.Any(match =>
            match.Asset.ObjectName == "unrelated_local" &&
            match.Evidence == AssetRelationshipEvidence.SamePackage),
            "same-package context was not included");
        Assert(result.Matches.Any(match =>
            match.Asset.ObjectName == "boss_snake_d" &&
            match.Evidence == AssetRelationshipEvidence.PackageFamily),
            "package-family candidate was not identified");
        Assert(result.Matches.All(match => match.Asset.ObjectName != "tree_01"),
            "unrelated indexed assets leaked into the relationship result");
    }

    private static void TestClientSnapshots()
    {
        var root = Path.Combine(fixtureDirectory, "snapshot-client");
        var workspace = Path.Combine(fixtureDirectory, "snapshot-workspace");
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "first.upk");
        var second = Path.Combine(root, "nested", "second.upk");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllBytes(first, [1, 2, 3]);
        File.WriteAllBytes(second, [4, 5]);
        var metadataPath = Path.Combine(fixtureDirectory, "snapshot-metadata.json");
        File.WriteAllText(metadataPath, "{\"fixture\":true}");
        var manifest = new PackageIndexManifest(
            PackageIndexStore.CurrentSchemaVersion,
            "fixture-profile",
            "fixture-umodel.exe",
            new string('A', 64),
            123,
            456,
            root,
            DateTimeOffset.UtcNow,
            2,
            2,
            0,
            10);
        var store = new BaselineWorkspaceStore(workspace);
        var entry = store.CreateClientSnapshotAsync(root, manifest, metadataPath, "Fixture client")
            .GetAwaiter()
            .GetResult();
        Assert(entry.Snapshot.Packages.Count == 2, "snapshot did not inventory both packages");
        Assert(entry.Snapshot.IndexManifest?.AssetCount == 10, "snapshot lost index provenance");
        Assert(entry.Snapshot.GameMetadataSha256?.Length == 64, "snapshot did not fingerprint metadata");

        using (var stream = new FileStream(first, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.WriteByte(9);
        }
        File.Delete(second);
        File.WriteAllBytes(Path.Combine(root, "third.upk"), [7]);
        var comparison = store.CompareToCurrentAsync(entry.Snapshot).GetAwaiter().GetResult();
        Assert(comparison.ChangedPackages == 1, "changed package was not detected");
        Assert(comparison.MissingPackages == 1, "missing package was not detected");
        Assert(comparison.AddedPackages == 1, "added package was not detected");
        Assert(comparison.UnchangedPackages == 0, "changed fixture packages were reported unchanged");
        Assert(comparison.Changes.Count == 3, "snapshot comparison returned the wrong change count");
    }

    private static void TestAssetBaselines()
    {
        var packagePath = Path.Combine(fixtureDirectory, "baseline-source.upk");
        WritePackage(packagePath, chunks: []);
        var workspace = Path.Combine(fixtureDirectory, "asset-baseline-workspace");
        var store = new BaselineWorkspaceStore(workspace);
        var request = new AssetBaselineRequest(
            packagePath,
            "baseline_source",
            1,
            AssetKind.Texture,
            "Texture2D",
            "fixture_texture_d",
            0,
            16,
            ["Fixture Texture"]);
        var first = store.CreateAssetBaselineAsync(request).GetAwaiter().GetResult();
        var second = store.CreateAssetBaselineAsync(request).GetAwaiter().GetResult();
        Assert(first.Baseline.PackageSha256.Length == 64, "package SHA-256 was not recorded");
        Assert(first.Baseline.LogicalPayloadSha256?.Length == 64, "logical payload SHA-256 was not recorded");
        Assert(first.Baseline.StorageRanges.Count == 1, "raw logical storage coverage was not recorded");
        Assert(first.Baseline.StorageRanges[0].StorageKind == "Raw physical bytes",
            "uncompressed payload was assigned the wrong storage kind");
        Assert(first.Baseline.PackageSha256 == second.Baseline.PackageSha256,
            "cached package hash changed for an unchanged source");
        var loaded = store.LoadAsync().GetAwaiter().GetResult();
        Assert(loaded.AssetBaselines.Count == 2, "asset baseline manifests were not reloaded");
        Assert(loaded.UnreadableManifestCount == 0, "valid baseline manifests were unreadable");
    }


    private static void TestArchetypeFamilies()
    {
        // Female Warrior (111) is the body Slayer (112) and Valkyrie (113) are built on; Destroyer (103) is not.
        var path = Path.Combine(fixtureDirectory, "archetype-family-metadata.json");
        GameAssetAssociation Look(string name, long id, string code) =>
            new(name, name, "PC", id, "LookInfo", $"EFDLChar_{code}.{code}", $"EFDLChar_{code}", code);
        var document = new GameAssetMetadataDocument(2, "English", "fixture", DateTimeOffset.UtcNow,
            [Look("Warrior (Female)", 111, "PC_WR_F"), Look("Slayer", 112, "PC_WBK_F"), Look("Valkyrie", 113, "PC_WHK_F"), Look("Destroyer", 103, "PC_WDR")],
            [
                new GameEntityDefinition("Warrior (Female)", "Warrior_Female", "PC", 111, "Playable class", "base=111", 111, "EFDLChar_PC_WR_F.PC_WR_F"),
                new GameEntityDefinition("Slayer", "Berserker_Female", "PC", 112, "Playable class", "base=111", 111, "EFDLChar_PC_WBK_F.PC_WBK_F"),
                new GameEntityDefinition("Valkyrie", "HolyKnight_Female", "PC", 113, "Playable class", "base=111", 111, "EFDLChar_PC_WHK_F.PC_WHK_F"),
                new GameEntityDefinition("Destroyer", "Destroyer", "PC", 103, "Playable class", "base=101", 101, "EFDLChar_PC_WDR.PC_WDR"),
            ]);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(document));
        var index = GameAssetMetadataIndex.Load(path);
        Assert(index.MatchesArchetype("Slayer", "Female Warrior") && index.MatchesArchetype("Valkyrie", "Female Warrior") &&
               index.MatchesArchetype("Female Warrior", "Female Warrior") && !index.MatchesArchetype("Destroyer", "Female Warrior") &&
               index.MatchesArchetype("Slayer", "Slayer") && !index.MatchesArchetype("Valkyrie", "Slayer") && !index.MatchesArchetype(null, "Slayer"),
            "a base body's class filter did not take in exactly the classes built on it");

        // A launch class skin (an HR item, not an AV one) is found by its in-game name on the meshes it shows.
        var skinPath = Path.Combine(fixtureDirectory, "class-skin-metadata.json");
        File.WriteAllText(skinPath, System.Text.Json.JsonSerializer.Serialize(new GameAssetMetadataDocument(2, "English", "fixture",
            DateTimeOffset.UtcNow,
            [
                Look("Slayer", 112, "PC_WBK_F"),
                new GameAssetAssociation("Cold Fury Chestpiece", "item.cold_fury", "Item", 1, "Model",
                    "EFDLItem_PC_WBK_F_HR_00_Upper.PC_WBK_F_HR_00_Upper", "EFDLItem_PC_WBK_F_HR_00_Upper", "PC_WBK_F_HR_00_Upper"),
            ],
            [new GameEntityDefinition("Slayer", "Berserker_Female", "PC", 112, "Playable class", "base=111", 111, "EFDLChar_PC_WBK_F.PC_WBK_F")])));
        var skins = GameAssetMetadataIndex.Load(skinPath).Find("pc_wbk_f_hr_00", "pc_wbk_f_hr_00.upk", "pc_wbk_f_hr_00_upper_sk");
        Assert(skins.Any(item => item.DisplayName == "Cold Fury (Slayer)" && item.Archetype == "Slayer"),
            "a launch class skin was not linked to its mesh: " + string.Join(", ", skins.Select(item => item.DisplayName)));
    }
}
