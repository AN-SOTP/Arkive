using Arkive.Core.Databases;
using Arkive.Core.GameData;
using Arkive.Core.Skills;

namespace Arkive.SmokeTests;

/// <summary>
/// In-game names reaching the assets the game's looks draw, the game-name database knowing which game data it was
/// read from, and the library index knowing how it stands against the game folder.
/// </summary>
internal static class GameLookSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("reads the meshes, materials and effects a look draws, with or without their class", TestObjectPaths),
        new("finds a crafted outfit piece's shared mesh and its own material by the item's name", TestLookNames),
        new("keeps the looks until the game's look archive changes", TestLookFreshness),
        new("checks search coverage and finds entries by name through their looks", TestCoverage),
        new("tells new, changed and removed game packages from the index, and forgets removed ones", TestIndexFreshness),
    ];

    private sealed class NoGame : IModDeploymentProcessGuard
    {
        public IReadOnlyList<string> FindBlockingProcesses() => [];
    }

    private const string Folder = "/EFGame_Extra/ClientData/XmlData/LookInfo/";

    // A look as the game writes one: a header, its own name, then values and texts back to back.
    private static byte[] Look(string name, params string[] texts)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        void Text(string text)
        {
            writer.Write(text.Length + 1);
            writer.Write(Encoding.ASCII.GetBytes(text));
            writer.Write((byte)0);
        }
        writer.Write(1f); writer.Write(26); writer.Write(0);
        Text(name);
        writer.Write(4); writer.Write(0);
        Text("None"); writer.Write(1); Text("CEFData_PartsMeshCuttingRule");
        writer.Write(0); writer.Write(0);
        // Back to back, as a look's material list is: no gap between one text and the next.
        foreach (var text in texts) Text(text);
        writer.Write(new byte[24]);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] HelmetLook() => Look("EFDLItem_PC_MG_LF_00-5_Helmet.PC_MG_LF_00-5_Helmet",
        "SkeletalMesh'PC_MG_LF_00.Mesh.PC_MG_LF_00_Helmet_SK'",
        "MaterialInstanceConstant'PC_MG_LF_00.Mat.PC_MG_LF_00-5_Helmet_MI'",
        "MaterialInstanceConstant'PC_MG_02_Hair.Mat.PC_MG_02_Hair_Helmet_MI'",
        "PhysicsAsset'PC_MG_LF_00.Mesh.PC_MG_LF_00_Helmet_Physics'",
        "B_Weapon_LHand");

    private static void TestObjectPaths()
    {
        var paths = GameLookSidecar.ObjectPaths(HelmetLook());
        Assert(paths.SequenceEqual([
                "SkeletalMesh'PC_MG_LF_00.Mesh.PC_MG_LF_00_Helmet_SK'",
                "MaterialInstanceConstant'PC_MG_LF_00.Mat.PC_MG_LF_00-5_Helmet_MI'",
                "MaterialInstanceConstant'PC_MG_02_Hair.Mat.PC_MG_02_Hair_Helmet_MI'"]),
            "a look's mesh and both back-to-back materials are read, its physics and socket are not: " + string.Join(" | ", paths));
        // Older looks write the mesh and materials without their class; an animation or sound set is not a mesh.
        var old = GameLookSidecar.ObjectPaths(Look("EFDLItem_PC_GN_02_Upper.PC_GN_02_Upper",
            "PC_GN_02.Mesh.PC_GN_02_Upper_SK", "PC_GN_02.Mat.PC_GN_02_Upper_MI", "efdataps_soundset.PC_LeatherChain_Warrior01",
            "DROP_ITEM.Anim.DROP_PARTS_00_ANI", "ParticleSystem'FX_GN_02.Par_G_GN_02_WP'"));
        Assert(old.SequenceEqual(["PC_GN_02.Mesh.PC_GN_02_Upper_SK", "PC_GN_02.Mat.PC_GN_02_Upper_MI", "ParticleSystem'FX_GN_02.Par_G_GN_02_WP'"]),
            "classless mesh and material paths and an effect are read, a sound set and an animation are not: " + string.Join(" | ", old));
    }

    // data4.lpk with a crafted hat (its mesh shared by every colour, its own material, the wearer's hair), a chest
    // piece's three looks, and an outfit whose material is another class's.
    private static string WriteLookArchive(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "data4.lpk");
        File.Delete(path);
        LostArkDataArchive.Create(new Dictionary<string, byte[]>
        {
            [Folder + "Item/EFDLItem_PC_MG_LF_00-5_Helmet.PC_MG_LF_00-5_Helmet.loa"] = HelmetLook(),
            [Folder + "Item/EFDLItem_PC_MG_LF_00-5_Upper.PC_MG_LF_00-5_Upper.loa"] = Look("EFDLItem_PC_MG_LF_00-5_Upper.PC_MG_LF_00-5_Upper",
                "SkeletalMesh'pc_mg_lf_00.Mesh.PC_MG_LF_00_Upper_SK_LOC_INT'", "MaterialInstanceConstant'PC_MG_LF_00.Mat.PC_MG_LF_00-5_Upper_MI'",
                "MaterialInstanceConstant'PC_MG_AV_BaseBody.Mat.PC_MG_AV_Base_Upper_MI'"),
            [Folder + "Item/EFDLItem_PC_MG_LF_00-5_Arm.PC_MG_LF_00-5_Arm.loa"] = Look("EFDLItem_PC_MG_LF_00-5_Arm.PC_MG_LF_00-5_Arm",
                "SkeletalMesh'PC_MG_LF_00.Mesh.PC_MG_LF_00_Arm_SK'", "MaterialInstanceConstant'PC_MG_LF_00.Mat.PC_MG_LF_00-5_Arm_MI'"),
            [Folder + "Item/EFDLItem_PC_MG_AV_150_Head.PC_MG_AV_150_Head.loa"] = Look("EFDLItem_PC_MG_AV_150_Head.PC_MG_AV_150_Head",
                "SkeletalMesh'PC_MG_AV_150.Mesh.PC_MG_AV_150_Head_SK'", "MaterialInstanceConstant'PC_DL_AV_150.Mat.PC_DL_AV_150_head_MI'"),
            [Folder + "Monster/EFDLChar_MN_FIXT_00.MN_FIXT_00.loa"] = Look("EFDLChar_MN_FIXT_00.MN_FIXT_00",
                "SkeletalMesh'MN_FIXT_00.Mesh.MN_FIXT_00_SK'", "SkeletalMesh'MN_WING_FIXT.Mesh.MN_WING_FIXT_00_SK'"),
        }, path);
        return path;
    }

    private static GameAssetAssociation Reference(string name, string type, long id, string field, string look) =>
        new(name, $"tip.name.{type.ToLowerInvariant()}_{id}", type, id, field, look, look[..look.IndexOf('.')], look[(look.IndexOf('.') + 1)..]);

    private static string WriteMetadata(string folder)
    {
        var path = Path.Combine(folder, "arkive-asset-metadata.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new GameAssetMetadataDocument(2, "English", folder, DateTimeOffset.UtcNow,
        [
            Reference("Mage", "PC", 201, "LookInfo", "EFDLChar_PC_MG.PC_MG"),
            Reference("Crafted Headpiece", "Item", 312020056, "Model", "EFDLItem_PC_MG_LF_00-5_Helmet.PC_MG_LF_00-5_Helmet"),
            // The same hat sold bound: another item, the same look.
            Reference("Crafted Headpiece", "Item", 312020057, "Model", "EFDLItem_PC_MG_LF_00-5_Helmet.PC_MG_LF_00-5_Helmet"),
            Reference("Crafted Chestpiece A", "Item", 322020056, "LookSet.Upper", "EFDLItem_PC_MG_LF_00-5_Upper.PC_MG_LF_00-5_Upper"),
            Reference("Crafted Chestpiece A", "Item", 322020056, "LookSet.Arm", "EFDLItem_PC_MG_LF_00-5_Arm.PC_MG_LF_00-5_Arm"),
            Reference("Shared Headwear", "Item", 312000150, "Model", "EFDLItem_PC_MG_AV_150_Head.PC_MG_AV_150_Head"),
            Reference("Fixture Boss", "Npc", 500, "Model", "EFDLChar_MN_FIXT_00.MN_FIXT_00"),
            Reference("No Look", "Item", 399, "Model", "EFDLItem_PC_MG_LF_99_Helmet.PC_MG_LF_99_Helmet"),
        ])));
        return path;
    }

    private static string NewFolder(string name)
    {
        var folder = Path.Combine(fixtureDirectory, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void TestLookNames()
    {
        var folder = NewFolder("look-names");
        var metadataPath = WriteMetadata(folder);
        var before = GameAssetMetadataIndex.Load(metadataPath);
        Assert(before.Find("pc_mg_lf_00", "physical", "pc_mg_lf_00_helmet_sk").Count == 0 && before.LookReferenceCount == 0,
            "without the looks, the crafted hat's name leads nowhere (the gap this closes)");

        var document = GameLookSidecar.BuildAsync(metadataPath, WriteLookArchive(Path.Combine(folder, "EFGame"))).GetAwaiter().GetResult();
        Assert(document.Looks.Count == 5 && document.Looks.ContainsKey("EFDLITEM_PC_MG_LF_00-5_HELMET.PC_MG_LF_00-5_HELMET") &&
               !document.Targets.Any(target => target.Contains("HAIR") || target.Contains("BASEBODY") || target.Contains("PHYSICS")),
            "every look is saved with what it draws of its own: " + string.Join(", ", document.Targets));

        var index = GameAssetMetadataIndex.Load(metadataPath);
        var mesh = index.Find("pc_mg_lf_00", "physical", "pc_mg_lf_00_helmet_sk");
        Assert(mesh.Count == 2 && mesh.All(item => item.DisplayName == "Crafted Headpiece" &&
                                                 item.Relation == GameAssetAssociationRelation.LookFileReference &&
                                                 item.Confidence == GameAssetAssociationConfidence.Verified &&
                                                 item.Field == "Model.Look.SkeletalMesh" &&
                                                 item.SourceAssetReference == "EFDLItem_PC_MG_LF_00-5_Helmet.PC_MG_LF_00-5_Helmet" &&
                                                 item.Archetype == "Female Mage") &&
               mesh.Select(item => item.EntityId).Order().SequenceEqual([312020056L, 312020057L]),
            "the mesh every colour shares is found under the item's name, once for each item that uses the look");
        Assert(index.Find("pc_mg_lf_00", "physical", "pc_mg_lf_00_helmet_d").Any(item => item.DisplayName == "Crafted Headpiece"),
            "the textures named after the mesh are found with it");
        Assert(index.Find("pc_mg_lf_00", "physical", "pc_mg_lf_00-5_helmet_mi").Any(item => item.Field == "Model.Look.MaterialInstanceConstant") &&
               index.Find("pc_mg_lf_00", "physical", "pc_mg_lf_00-4_helmet_mi").Count == 0,
            "the look's own material is found, another colour's is not");
        Assert(index.Find("pc_mg_02_hair", "physical", "pc_mg_02_hair_helmet_mi").Count == 0 &&
               index.Find("pc_mg_av_basebody", "physical", "pc_mg_av_base_upper_mi").Count == 0,
            "the wearer's hair and body skin are not named after the item");
        var chest = index.Find("pc_mg_lf_00", "physical", "pc_mg_lf_00_upper_sk_loc_int");
        Assert(chest.Any(item => item.DisplayName == "Crafted Chestpiece A" && item.Field == "LookSet.Upper.Look.SkeletalMesh") &&
               index.Find("pc_mg_lf_00", "physical", "pc_mg_lf_00_upper_d_loc_int").Any(item => item.DisplayName == "Crafted Chestpiece A") &&
               index.Find("pc_mg_lf_00", "physical", "pc_mg_lf_00_arm_sk").Any(item => item.DisplayName == "Crafted Chestpiece A"),
            "a chest piece is found through its look set, on the international mesh and its textures too");
        Assert(index.Find("pc_dl_av_150", "physical", "pc_dl_av_150_head_mi").Any(item => item.DisplayName == "Shared Headwear") &&
               index.Find("pc_dl_av_150", "physical", "pc_dl_av_150_head_sk").Count == 0,
            "a material another class's package holds is found, that class's own mesh is not");
        Assert(index.Find("mn_wing_fixt", "physical", "mn_wing_fixt_00_sk").Any(item => item.DisplayName == "Fixture Boss"),
            "a monster's separate part is found under the monster's name");

        var plan = index.CreateSearchPlan("Crafted Headpiece", displayNamesOnly: true);
        Assert(plan.MayContain("pc_mg_lf_00", "physical", "pc_mg_lf_00_helmet_sk") &&
               plan.MayContain("pc_mg_lf_00", "physical", "pc_mg_lf_00-5_helmet_mi") &&
               !plan.MayContain("pc_mg_lf_00", "physical", "pc_mg_lf_00_arm_sk"),
            "a name search is routed to what the named item's look draws, and nothing else");
        Assert(index.CreateSearchPlan("", displayNamesOnly: false, "Female Mage").MayContain("pc_mg_lf_00", "physical", "pc_mg_lf_00_arm_sk") &&
               !index.CreateSearchPlan("", displayNamesOnly: false, "Female Mage").MayContain("mn_fixt_00", "physical", "mn_fixt_00_sk"),
            "the class filter takes in every item made for that body, whatever family its look is from");
        Assert(index.CreateSearchPlan("PC_MG_LF_00_ARM_SK", displayNamesOnly: false).MayContain("pc_mg_lf_00", "physical", "pc_mg_lf_00_arm_sk") &&
               !index.CreateSearchPlan("Crafted -Headpiece", displayNamesOnly: true).MayContain("pc_mg_lf_00", "physical", "pc_mg_lf_00_helmet_sk"),
            "an all-fields search matches what a look draws by its path, and an excluded word still excludes");

        var entity = index.FindEntityAssociations("Item", 322020056);
        Assert(entity.Count(item => item.Relation == GameAssetAssociationRelation.DirectDatabaseReference) == 2 &&
               entity.Count(item => item.Relation == GameAssetAssociationRelation.LookFileReference) == 4,
            "an entry's references come with what its looks draw: " + string.Join(", ", entity.Select(item => item.AssetReference)));
        // The hat's two (for each of its two items), the chest's and arms' two each, the shared headwear's two, the boss's two.
        Assert(index.AssociationCount == before.AssociationCount && index.LookReferenceCount == 12 &&
               index.AllReferences().Count() == index.AssociationCount + 12,
            $"the looks' references are counted apart and kept once per look: {index.LookReferenceCount}");
    }

    private static void TestLookFreshness()
    {
        var folder = NewFolder("look-fresh");
        var metadataPath = WriteMetadata(folder);
        var archive = WriteLookArchive(Path.Combine(folder, "EFGame"));
        Assert(!GameLookSidecar.IsCurrent(metadataPath, archive), "with nothing saved yet the looks are to be read");
        Assert(GameLookSidecar.IsCurrent(metadataPath, Path.Combine(folder, "no-such.lpk")), "without a look archive there is nothing to read");
        GameLookSidecar.BuildAsync(metadataPath, archive).GetAwaiter().GetResult();
        Assert(GameLookSidecar.IsCurrent(metadataPath, archive), "the saved looks are current for the archive they were read from");
        // Arkive's copy of the game's file keeps its size and date, so it counts as the same looks.
        var copy = Path.Combine(folder, "backup.lpk");
        File.Copy(archive, copy);
        File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(archive));
        Assert(GameLookSidecar.IsCurrent(metadataPath, copy), "a copy with the same size and date is the same looks");
        File.SetLastWriteTimeUtc(archive, File.GetLastWriteTimeUtc(archive).AddMinutes(5));
        Assert(!GameLookSidecar.IsCurrent(metadataPath, archive), "a look archive with another date is read again");

        // The game-name database's stamp: current for the game data it was read from, by this Arkive's reader.
        var efGame = Path.Combine(folder, "EFGame");
        var data = new GameDataArchive(efGame, Path.Combine(folder, "data"), new NoGame());
        File.WriteAllBytes(data.ArchivePath, [1, 2, 3]);
        Assert(!GameDatabaseBuilder.IsCurrent(metadataPath, data) && GameDatabaseBuilder.ReadLanguage(metadataPath) == "English",
            "a database without a stamp is out of date, and its language is read from the database itself");
        var (length, ticks) = data.GameStamp();
        File.WriteAllText(GameDatabaseBuilder.StampPath(metadataPath), JsonSerializer.Serialize(
            new { sourceLength = length, sourceLastWriteUtcTicks = ticks, language = "German", contentVersion = GameDatabaseMetadataReader_ContentVersion() }));
        Assert(GameDatabaseBuilder.IsCurrent(metadataPath, data) && GameDatabaseBuilder.ReadLanguage(metadataPath) == "German",
            "a database stamped with the game's data is current");
        File.SetLastWriteTimeUtc(data.ArchivePath, File.GetLastWriteTimeUtc(data.ArchivePath).AddMinutes(5));
        Assert(!GameDatabaseBuilder.IsCurrent(metadataPath, data), "a game update's new data2.lpk puts the database out of date");
        File.WriteAllText(GameDatabaseBuilder.StampPath(metadataPath), JsonSerializer.Serialize(
            new { sourceLength = data.GameStamp().Length, sourceLastWriteUtcTicks = data.GameStamp().WriteUtcTicks, language = "English", contentVersion = 0 }));
        Assert(!GameDatabaseBuilder.IsCurrent(metadataPath, data), "a database read by an older reader is out of date");
    }

    // The reader is internal; its version is what a freshly built stamp carries.
    private static int GameDatabaseMetadataReader_ContentVersion() =>
        (int)typeof(GameDatabaseBuilder).Assembly.GetType("Arkive.Core.Databases.GameDatabaseMetadataReader")!
            .GetField("ContentVersion")!.GetRawConstantValue()!;

    private static void WriteIndexDocument(string cache, string source, string alias, params string[] objects)
    {
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(source, []);
        File.WriteAllText(Path.Combine(cache, alias + ".json"), JsonSerializer.Serialize(new PackageIndexDocument(
            source, alias, alias, 0, File.GetLastWriteTimeUtc(source).Ticks, DateTimeOffset.UtcNow,
            objects.Select((name, index) => new IndexedAsset(index, name.Contains("_sk", StringComparison.Ordinal) ? AssetKind.SkeletalMesh : AssetKind.Material,
                name.Contains("_sk", StringComparison.Ordinal) ? "SkeletalMesh" : "MaterialInstanceConstant", name, 10, 10)).ToArray(), Error: null)));
    }

    private static void TestCoverage()
    {
        var folder = NewFolder("look-coverage");
        var metadataPath = WriteMetadata(folder);
        var root = Path.Combine(folder, "Packages");
        var cache = Path.Combine(folder, "cache");
        Directory.CreateDirectory(root);
        WriteIndexDocument(cache, Path.Combine(root, "pc_mg_lf_00.upk"), "pc_mg_lf_00",
            "pc_mg_lf_00_helmet_sk", "pc_mg_lf_00-5_helmet_mi", "pc_mg_lf_00_upper_sk_loc_int", "pc_mg_lf_00_arm_sk");
        WriteIndexDocument(cache, Path.Combine(root, "mn_fixt_00.upk"), "mn_fixt_00", "mn_fixt_00_sk");
        var store = new PackageIndexStore(cache);

        var before = store.AuditSearchCoverageAsync(root, GameAssetMetadataIndex.Load(metadataPath)).GetAwaiter().GetResult();
        var items = before.Groups.Single(group => group is { EntityType: "Item", Kind: "Model" });
        // Five names: the four items' own and the set name the outfit rule gives the shared headwear.
        Assert(items is { Entities: 5, Reachable: 0, Names: 5, ReachableNames: 0 } &&
               before.Groups.Single(group => group.EntityType == "Npc") is { Entities: 1, Reachable: 1 },
            $"without the looks no item is found by name, the monster is: {items}");
        Assert(store.SearchAsync(root, "Crafted Headpiece", null, GameAssetMetadataIndex.Load(metadataPath), PackageIndexSearchScope.InGameName)
                .GetAwaiter().GetResult().TotalMatches == 0, "the search agrees: the crafted hat finds nothing");

        GameLookSidecar.BuildAsync(metadataPath, WriteLookArchive(Path.Combine(folder, "EFGame"))).GetAwaiter().GetResult();
        var index = GameAssetMetadataIndex.Load(metadataPath);
        var after = new PackageIndexStore(cache).AuditSearchCoverageAsync(root, index).GetAwaiter().GetResult();
        items = after.Groups.Single(group => group is { EntityType: "Item", Kind: "Model" });
        Assert(items is { Entities: 5, Reachable: 3, Names: 5, ReachableNames: 2 } &&
               after.Misses.Where(miss => miss.EntityType == "Item").Select(miss => miss.DisplayName).Order().SequenceEqual(["No Look", "Shared Headwear"]),
            $"with the looks the crafted pieces are found; the look-less item and the one whose package is not indexed are listed: {items}; " +
            string.Join(", ", after.Misses.Select(miss => $"{miss.EntityType} {miss.DisplayName}")));

        var search = new PackageIndexStore(cache).SearchAsync(root, "Crafted Headpiece", null, index, PackageIndexSearchScope.InGameName)
            .GetAwaiter().GetResult();
        Assert(search.Matches.Select(match => match.ObjectName).Order(StringComparer.Ordinal).SequenceEqual(["pc_mg_lf_00-5_helmet_mi", "pc_mg_lf_00_helmet_sk"]) &&
               search.Matches.All(match => GameAssetMetadataIndex.FormatNames(match.GameMetadata) == "Crafted Headpiece"),
            "the search finds the hat's mesh and material by its in-game name: " +
            string.Join(", ", search.Matches.Select(match => $"{match.ObjectName} [{GameAssetMetadataIndex.FormatNames(match.GameMetadata)}]")));
        var mesh = new PackageIndexStore(cache).SearchAsync(root, "Crafted", "SkeletalMesh", index, PackageIndexSearchScope.InGameName, "Female Mage")
            .GetAwaiter().GetResult();
        Assert(mesh.Matches.Select(match => match.ObjectName).Order(StringComparer.Ordinal).SequenceEqual(
                ["pc_mg_lf_00_arm_sk", "pc_mg_lf_00_helmet_sk", "pc_mg_lf_00_upper_sk_loc_int"]),
            "a name search under the class filter finds the crafted set's meshes: " + string.Join(", ", mesh.Matches.Select(match => match.ObjectName)));
    }

    private static void TestIndexFreshness()
    {
        var folder = NewFolder("index-fresh");
        var root = Path.Combine(folder, "Packages");
        var other = Path.Combine(folder, "OtherGame");
        var cache = Path.Combine(folder, "cache");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(other);
        var backend = Path.Combine(folder, "umodel-fixture.exe");
        File.WriteAllBytes(backend, [1, 2, 3, 4]);
        var bridge = new UmodelBridge(backend);
        // Header-only packages are listed without asking UModel.
        foreach (var name in new[] { "one", "two", "three" }) WritePackage(Path.Combine(root, name + ".upk"), []);
        WritePackage(Path.Combine(other, "elsewhere.upk"), []);
        var store = new PackageIndexStore(cache);
        Assert(store.GetFreshness(root) is { PackageCount: 0 }, "with no index there is nothing to compare");
        store.BuildAsync(other, bridge).GetAwaiter().GetResult();
        var built = store.BuildAsync(root, bridge).GetAwaiter().GetResult();
        Assert(built is { TotalPackages: 3, IndexedPackages: 3, FailedPackages: 0 } &&
               store.GetFreshness(root) is { PackageCount: 3, IsCurrent: true, NeedsUpdate: false },
            "a freshly built index is current: " + store.GetFreshness(root));

        WritePackage(Path.Combine(root, "four.upk"), []);
        File.SetLastWriteTimeUtc(Path.Combine(root, "two.upk"), DateTime.UtcNow.AddMinutes(7));
        File.Delete(Path.Combine(root, "three.upk"));
        var behind = store.GetFreshness(root);
        Assert(behind is { PackageCount: 3, NewPackages: 1, ChangedPackages: 1, RemovedPackages: 1, NeedsUpdate: true, IsCurrent: false },
            "a new, a changed and a removed package are each counted, another game folder's are not: " + behind);

        var updated = store.BuildAsync(root, bridge).GetAwaiter().GetResult();
        Assert(updated is { TotalPackages: 3, IndexedPackages: 2, CachedPackages: 1 } && store.GetFreshness(root).IsCurrent,
            $"an update lists only the new and changed packages and forgets the removed one: {updated}, {store.GetFreshness(root)}");
        Assert(Directory.EnumerateFiles(cache, "*.json.gz").Count(file => !Path.GetFileName(file).StartsWith("search-routing", StringComparison.Ordinal)) == 4 &&
               store.GetFreshness(other).IsCurrent,
            "the other game folder's index document is left alone");
    }
}
