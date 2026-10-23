using Arkive.Core.Outfits;
using Arkive.Core.Skills;

namespace Arkive.SmokeTests;

internal static class OutfitLookSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("reads an item look and changes, adds and checks its material slots", TestLookPatch),
        new("writes item looks into data4.lpk and puts the game's file back byte for byte", TestLookArchive),
        new("resolves item looks between mods, notices a game update and rebases the mod", TestLookMods),
        new("points a swapped piece's look at the donor's materials and their dyed versions", TestSwapLooks),
    ];

    private sealed class NoGame : IModDeploymentProcessGuard
    {
        public IReadOnlyList<string> FindBlockingProcesses() => [];
    }

    private const string UpperEntry = OutfitLooks.Folder + "EFDLItem_PC_T_Upper.PC_T_Upper.loa";
    private const string DonorEntry = OutfitLooks.Folder + "EFDLItem_PC_D_Upper.PC_D_Upper.loa";

    private static string Mat(string name) => $"MaterialInstanceConstant'{name.Split('_')[0]}_{name.Split('_')[1]}.Mat.{name}'";

    // A look laid out as the game's are: a header, the mesh, its materials, a few values, a setting per slot, the dyed
    // materials and a tail of flags.
    private static byte[] Look(string mesh, string[] materials, string[] dyes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        void Text(string text)
        {
            if (text.Length == 0) { writer.Write(0); return; }
            writer.Write(text.Length + 1);
            writer.Write(Encoding.ASCII.GetBytes(text));
            writer.Write((byte)0);
        }
        writer.Write(1f); writer.Write(26); writer.Write(0);
        Text(mesh.Replace("_SK", "") + ".look");
        foreach (var value in new[] { 4, 0, 4, 4, 0, 0, 0, 0, 0, 0, 0, 0 }) writer.Write(value);
        Text("None"); writer.Write(1); Text("CEFData_PartsMeshCuttingRule");
        for (var i = 0; i < 4; i++) writer.Write(0);
        Text($"SkeletalMesh'{mesh.Split('_')[0]}_{mesh.Split('_')[1]}.Mesh.{mesh}'");
        writer.Write(materials.Length);
        foreach (var material in materials) Text(material);
        foreach (var value in new[] { 0, 0, 0, 0, 0, 0, 0, 1, 0 }) writer.Write(value);
        writer.Write(1f); writer.Write(1f); writer.Write(0); writer.Write(0);
        writer.Write(materials.Length);
        foreach (var _ in materials) { Text("None"); writer.Write(new byte[16]); }
        writer.Write(dyes.Length);
        foreach (var dye in dyes) Text(dye);
        foreach (var value in new[] { 1, 1, 1, 0, 1, 1, 1, 0 }) writer.Write(value);
        writer.Write(new byte[80]);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] TargetUpper(string extra = "") => Look("PC_T_Upper_SK",
        [Mat("PC_T_Upper_MI"), Mat("PC_T_Upper1_MI" + extra), Mat("PC_B_Base_Upper_MI")],
        [Mat("PC_T_Upper_MI_DYE"), Mat("PC_T_Upper1_MI_DYE"), ""]);

    private static byte[] DonorUpper() => Look("PC_D_Upper_SK",
        [Mat("PC_D_Upper_MI"), Mat("PC_D_Upper1_MI"), Mat("PC_C_Base_Upper_MI")],
        [Mat("PC_D_Upper_MI_DYE"), "", Mat("PC_C_Base_Upper_MI")]);

    private static void TestLookPatch()
    {
        var bytes = TargetUpper();
        var look = OutfitLooks.Read(UpperEntry, bytes) ?? throw new InvalidOperationException("The look was not read.");
        Assert(OutfitLooks.ObjectName(look.Mesh) == "PC_T_Upper_SK" && look.Materials.Count == 3 && look.DyeMaterials[2] == "",
            "the look's mesh, materials and empty dyed entry are read");
        var edits = new[]
        {
            new OutfitLookEdit(UpperEntry, 1, Mat("PC_D_Upper_MI"), Mat("PC_D_Upper_MI_DYE"), look.Materials[1], look.DyeMaterials[1]),
            new OutfitLookEdit(UpperEntry, 3, Mat("PC_D_Upper1_MI"), Mat("PC_D_Upper1_MI"), null, null),
            new OutfitLookEdit(UpperEntry, 4, Mat("PC_C_Base_Lower_MI"), Mat("PC_C_Base_Lower_MI"), null, null),
        };
        var patched = OutfitLooks.Patch(UpperEntry, bytes, edits);
        var read = OutfitLooks.Read(UpperEntry, patched) ?? throw new InvalidOperationException("The changed look was not read.");
        Assert(read.Materials.Count == 5 && read.DyeMaterials.Count == 5 && read.Materials[0] == look.Materials[0] &&
               read.Materials[1] == Mat("PC_D_Upper_MI") && read.DyeMaterials[1] == Mat("PC_D_Upper_MI_DYE") &&
               read.Materials[2] == look.Materials[2] && read.Materials[4] == Mat("PC_C_Base_Lower_MI"),
            "the slot is changed and two slots are added after the look's own: " + string.Join(", ", read.Materials));
        Assert(patched.AsSpan(patched.Length - 112).SequenceEqual(bytes.AsSpan(bytes.Length - 112)) &&
               patched.AsSpan(0, 180).SequenceEqual(bytes.AsSpan(0, 180)), "the look before its materials and after its dyed list is unchanged");
        // Putting the slot back and taking the added ones away is the game's look again, byte for byte (no added slots).
        var back = OutfitLooks.Patch(UpperEntry, bytes, [edits[0]]);
        Assert(OutfitLooks.Patch(UpperEntry, back, [edits[0] with
               {
                   Material = look.Materials[1], DyeMaterial = look.DyeMaterials[1], OriginalMaterial = edits[0].Material,
                   OriginalDyeMaterial = edits[0].DyeMaterial,
               }]).SequenceEqual(bytes), "changing a slot and back gives the game's look byte for byte");
        Refused(() => OutfitLooks.Patch(UpperEntry, TargetUpper("X"), [edits[0]]), "a slot the game changed since the edit was made");
        Refused(() => OutfitLooks.Patch(UpperEntry, bytes, [edits[2]]), "an added slot that does not follow on from the look's own");
        Refused(() => OutfitLooks.Patch(UpperEntry, bytes, [edits[0] with { Slot = 3 }]), "a slot the look does not have");
    }

    private static void TestLookArchive() => InWorkspace((efGame, data) =>
    {
        var path = Path.Combine(efGame, "data4.lpk");
        CreateArchive(path, TargetUpper());
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
        var original = File.ReadAllBytes(path);
        var archive = new OutfitLookArchive(efGame, data, new NoGame());
        var game = archive.GameLooks([UpperEntry])[UpperEntry];
        var edits = new[]
        {
            new OutfitLookEdit(UpperEntry, 1, Mat("PC_D_Upper_MI"), Mat("PC_D_Upper_MI_DYE"), game.Materials[1], game.DyeMaterials[1]),
            new OutfitLookEdit(UpperEntry, 3, Mat("PC_D_Upper1_MI"), Mat("PC_D_Upper1_MI"), null, null),
        };
        archive.WriteAsync(edits).GetAwaiter().GetResult();
        Assert(!File.ReadAllBytes(path).SequenceEqual(original) && LiveLook(path).Materials[1] == Mat("PC_D_Upper_MI") &&
               LiveLook(path).Materials.Count == 4, "the changed look is in data4.lpk");
        Assert(archive.GameLooks([UpperEntry])[UpperEntry].Materials.SequenceEqual(game.Materials) && archive.Current().Count == 2,
            "the game's own looks are still read from Arkive's copy, and the changes are recorded");
        using (var other = File.OpenRead(path))
            Assert(LostArkDataArchive.Read(other, LostArkDataArchive.ReadEntries(other).Single(entry => entry.Path.EndsWith("Other.xml")))
                .SequenceEqual(Encoding.UTF8.GetBytes("<other/>")), "the other files in the archive are copied as they are");

        archive.WriteAsync([]).GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(path).SequenceEqual(original) &&
               File.GetLastWriteTimeUtc(path) == new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc) && archive.LoadState() is null,
            "writing no changes puts the game's data4.lpk back byte for byte, date included");

        // A game update writes a new data4.lpk over Arkive's: that is the game's own file now, and the stale copy goes.
        archive.WriteAsync(edits).GetAwaiter().GetResult();
        var backup = archive.LoadState()!.BackupPath;
        CreateArchive(path, TargetUpper("X"));
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc));
        var updated = File.ReadAllBytes(path);
        Assert(archive.LoadState() is null && archive.Current().Count == 0, "the updated game file is not taken for Arkive's");
        Refused(() => archive.WriteAsync(edits).GetAwaiter().GetResult(), "a change made against the old game look");
        Assert(File.ReadAllBytes(path).SequenceEqual(updated) && !File.Exists(backup), "the refused write left the game's new file, and the old copy went");
        archive.WriteAsync([edits[1]]).GetAwaiter().GetResult();
        archive.WriteAsync([]).GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(path).SequenceEqual(updated), "the updated game file is put back byte for byte too");
    });

    private static void TestLookMods() => InWorkspace((efGame, data) =>
    {
        var path = Path.Combine(efGame, "data4.lpk");
        CreateArchive(path, TargetUpper());
        var service = new OutfitLookModService(new OutfitLookArchive(efGame, data, new NoGame()));
        var game = service.Archive.GameLooks([UpperEntry])[UpperEntry];
        OutfitLookEdit Slot(int slot, string material) =>
            new(UpperEntry, slot, Mat(material), Mat(material + "_DYE"), game.Materials[slot], game.DyeMaterials[slot]);
        var swap = Mod("Swap", 0, [Slot(0, "PC_D_Upper_MI"), new OutfitLookEdit(UpperEntry, 3, Mat("PC_D_Upper1_MI"), Mat("PC_D_Upper1_MI"), null, null)]);
        var other = Mod("Other", 0, [Slot(1, "PC_E_Upper_MI")]);

        var tied = service.Plan([swap, other]);
        Assert(tied.Problems.Any(problem => problem.Contains("same priority", StringComparison.Ordinal)),
            "two mods changing one look at the same priority tie");
        var plan = service.Plan([swap with { Priority = 5 }, other]);
        Assert(plan.Problems.Count == 0 && plan.Changes && plan.Target.Count == 2 && plan.Resolution.Looks.Single().Others.Count == 1,
            "the higher priority mod gets the whole look");
        service.ApplyAsync(plan).GetAwaiter().GetResult();
        var resolution = OutfitLookResolution.Resolve([swap with { Priority = 5 }, other]);
        Assert(resolution.StatusFor(swap.ModId, service.Archive.Current()) is { Winning: 1, InGame: true } &&
               resolution.StatusFor(other.ModId, service.Archive.Current()) is { Overridden: 1 } &&
               !service.Plan([swap with { Priority = 5 }, other]).Changes, "the applied look shows in game and the plan has nothing left to do");

        // The game changes the look's second slot: the mod needs an update, which takes the new originals.
        CreateArchive(path, TargetUpper("X"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        var updated = service.Plan([swap]);
        Assert(updated.Problems.Count == 0 && updated.Changes, "a game update to a slot the mod leaves alone needs no update");
        var stale = service.Plan([other]);
        Assert(stale.ModsNeedingUpdate.SetEquals([other.ModId]), "a game update to a slot the mod changes is noticed");
        var rebased = service.Rebase(other);
        Assert(rebased.Updated == 1 && rebased.Looks!.Edits.Single() is { OriginalMaterial: var material, Material: var kept } &&
               material == Mat("PC_T_Upper1_MIX") && kept == Mat("PC_E_Upper_MI"), "the update keeps the mod's material on the new look");
        Assert(service.Plan([other with { Manifest = other.Manifest with { Looks = rebased.Looks } }]).Problems.Count == 0,
            "the updated mod plans cleanly");
        Assert(service.RestoreAsync().GetAwaiter().GetResult() == false, "nothing is restored when the game wrote its own file");
    });

    private static void TestSwapLooks() => InWorkspace((efGame, data) =>
    {
        var path = Path.Combine(efGame, "data4.lpk");
        LostArkDataArchive.Create([new(UpperEntry, TargetUpper()), new(DonorEntry, DonorUpper())], path);
        var archive = new OutfitLookArchive(efGame, data, new NoGame());
        PackageObjectReference Reference(string package, string material) => new([
            new(package, "package", "core"), new("Mat", "package", "core"), new(material, "MaterialInstanceConstant", "engine")]);
        // The target's three slots; the swap draws its first with the donor's cloth and adds the donor's second cloth and skin.
        var mesh = new OutfitSwapMesh
        {
            Positions = [], Normals = [], Tangents = [], Uv0 = [], Uv1 = [], Colours = [], Joints = [], Weights = [],
            Sections = [new(0, []), new(2, []), new(3, []), new(4, [])],
            Materials = ["PC_D_Upper_MI", "PC_T_Upper1_MI", "PC_B_Base_Upper_MI", "PC_D_Upper1_MI", "PC_C_Base_Upper_MI"],
            BoneNames = [], BonePositions = [], BoneParents = [],
            MaterialReferences = new Dictionary<int, SkeletalMeshMaterialReference>
            {
                [0] = new(Reference("PC_D", "PC_D_Upper_MI"), "PC_T_Upper_MI"),
                [3] = new(Reference("PC_D", "PC_D_Upper1_MI"), null),
                [4] = new(Reference("PC_C", "PC_C_Base_Upper_MI"), null),
            },
        };
        var notes = new List<string>();
        var edits = OutfitSwapLooks.Edits(archive, ["PC_D_Upper_SK"],
            [new OutfitSwapLookPiece("PC_T_Upper_SK", ["PC_T_Upper_MI", "PC_T_Upper1_MI", "PC_B_Base_Upper_MI"], mesh)], notes);
        Assert(edits.Count == 3 && notes.Count == 0, $"one slot changed and two added ({edits.Count}): {string.Join(" ", notes)}");
        Assert(edits[0] is { Slot: 0, Added: false } && edits[0].Material == Mat("PC_D_Upper_MI") && edits[0].DyeMaterial == Mat("PC_D_Upper_MI_DYE") &&
               edits[0].OriginalMaterial == Mat("PC_T_Upper_MI"), "the replaced slot takes the donor's material and its dyed version");
        Assert(edits[1] is { Slot: 3, Added: true } && edits[1].DyeMaterial == Mat("PC_D_Upper1_MI") &&
               edits[2] is { Slot: 4, Added: true } && edits[2].Material == Mat("PC_C_Base_Upper_MI"),
            "added slots follow the look's own, a material with no dyed version stands for it");
        archive.WriteAsync(edits).GetAwaiter().GetResult();
        Assert(LiveLook(path).Materials.Count == 5, "the swap's look changes write into data4.lpk");
    });

    private static void Refused(Action action, string what)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException($"Arkive did not refuse {what}.");
    }

    private static OutfitLook LiveLook(string path)
    {
        using var stream = File.OpenRead(path);
        var entry = LostArkDataArchive.ReadEntries(stream).Single(item => item.Path.Equals(UpperEntry, StringComparison.OrdinalIgnoreCase));
        return OutfitLooks.Read(entry.Path, LostArkDataArchive.Read(stream, entry))!;
    }

    private static void CreateArchive(string path, byte[] upper)
    {
        // The game writes a new file over the old one.
        File.Delete(path);
        LostArkDataArchive.Create([new("/EFGame_Extra/ClientData/Other.xml", Encoding.UTF8.GetBytes("<other/>")), new(UpperEntry, upper)], path);
    }

    private static ArkiveModLibraryEntry Mod(string name, int priority, IReadOnlyList<OutfitLookEdit> edits)
    {
        var id = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var manifest = new ArkiveModFileManifest(ArkiveModFileFormat.FormatId, ArkiveModFileFormat.LooksFormatVersion, id, name, "1.0.0",
            null, null, null, [], ArkiveModBundleService.WesternGlobalProfile, now, now, "smoke", []) { Looks = new ArkiveModLooks(edits) };
        return new ArkiveModLibraryEntry(id, name + ".arkive", "", 0, now, true, priority, ArkiveModOrigin.Created, manifest, []);
    }

    private static void InWorkspace(Action<string, string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "arkive-looks-" + Guid.NewGuid().ToString("N"));
        var efGame = Path.Combine(root, "EFGame");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(efGame);
        Directory.CreateDirectory(data);
        try
        {
            test(efGame, data);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
