using System.Numerics;
using System.Text;
using System.Text.Json;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Meshes;
using Arkive.Core.Outfits;
using Arkive.Core.Packages;

namespace Arkive.SmokeTests;

internal static class MaterialReferenceSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("splits a trailing number off a name the way the game stores it", TestNameSplitting),
        new("adds imports of another package's material, keeping every original name, import and export byte", TestImportGrowth),
        new("reads material references from a GLB and plans which slots they take or add", TestReferencePlan),
        new("swaps an outfit with its own materials: donor cloth takes the target's cloth slots, then added ones", TestOwnMaterialsPlan),
    ];

    private static void TestNameSplitting()
    {
        Assert(PackageObjectReference.SplitName("pc_msm_av_137") == ("pc_msm_av", 138), "pc_msm_av_137 is pc_msm_av number 138");
        Assert(PackageObjectReference.SplitName("hdr07_1") == ("hdr07", 2), "a single digit splits");
        Assert(PackageObjectReference.SplitName("pc_xx_01") == ("pc_xx_01", 0), "a leading zero keeps the name whole");
        Assert(PackageObjectReference.SplitName("pc_msm_av_137_upper_mi") == ("pc_msm_av_137_upper_mi", 0), "no trailing number");
        Assert(PackageObjectReference.SplitName("pc_wbk_f_av_137-1_head_mi") == ("pc_wbk_f_av_137-1_head_mi", 0), "a hyphen is no split");
        Assert(PackageObjectReference.SplitName("_5") == ("_5", 0), "nothing before the underscore keeps the name whole");
    }

    private static void TestImportGrowth()
    {
        var directory = Path.Combine(Path.GetTempPath(), "arkive-imports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "TESTPACKAGE.upk");
            var (micBytes, meshBytes) = WriteMaterialPackage(path);
            var reader = new AssetCatalogReader();
            var source = reader.Read(path);
            var reference = new PackageObjectReference([
                new("pc_donor_137", "package", "core"), new("mat", "package", "core"),
                new("pc_donor_137_upper_mi", "materialinstanceconstant", "engine")]);
            Assert(reference.FindImport(source) is null, "the clean package does not import the donor material");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            var result = new SkeletalMeshStagedUpkService().BuildImportsAsync(new PackageImportsStagedUpkRequest(
                    path, hash, "TESTPACKAGE.upk", "pc_test", [reference], Path.Combine(directory, "staged")))
                .GetAwaiter().GetResult() ?? throw new InvalidOperationException("Nothing was added.");
            var output = reader.Read(result.OutputUpkPath);
            Assert(output.Names.Take(source.Names.Count).SequenceEqual(source.Names) &&
                   output.Imports.Take(source.Imports.Count).SequenceEqual(source.Imports), "the original names and imports keep their places");
            Assert(result.Manifest.AddedImports == 3 && result.Manifest.AddedNames == 2,
                $"the donor package, its group and the material are imported ({result.Manifest.AddedImports} imports, {result.Manifest.AddedNames} names)");
            Assert(output.Names.Any(name => name.Value == "pc_donor") && output.Names.All(name => name.Value != "pc_donor_137"),
                "the donor package's name is stored with its number apart");
            var index = reference.FindImport(output);
            Assert(index is not null && index == result.Manifest.Imports[reference.Path], "the material is imported where Arkive planned it");
            var logical = File.ReadAllBytes(result.OutputUpkPath);
            var mic = output.Exports[0];
            Assert(mic.SerialOffset > source.Exports[1].SerialOffset && logical.AsSpan(mic.SerialOffset, mic.SerialSize).SequenceEqual(micBytes),
                "the material instance the header grew over moved to the end unchanged");
            var mesh = output.Exports[1];
            Assert(mesh.SerialOffset == source.Exports[1].SerialOffset && logical.AsSpan(mesh.SerialOffset, mesh.SerialSize).SequenceEqual(meshBytes),
                "the next export kept its place and bytes");
            Assert(output.Summary.HeaderSize == source.Summary.HeaderSize + result.Manifest.OutputHeaderEnd - result.Manifest.SourceHeaderEnd &&
                   output.Summary.Generations[^1].NameCount == output.Summary.NameCount, "the summary describes the grown header");
            var again = new SkeletalMeshStagedUpkService().BuildImportsAsync(new PackageImportsStagedUpkRequest(
                    result.OutputUpkPath, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(logical)), "TESTPACKAGE.upk",
                    "pc_test", [reference], Path.Combine(directory, "again")))
                .GetAwaiter().GetResult();
            Assert(again is null, "a package that already imports the material is left alone");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestReferencePlan()
    {
        var reference = new SkeletalMeshMaterialReference(new PackageObjectReference([
            new("pc_donor_137", "package", "core"), new("mat", "package", "core"),
            new("pc_donor_137_upper_mi", "materialinstanceconstant", "engine")]), "MaterialInstanceConstant'mat.pc_test_a_mi'");
        var added = reference with { Replaces = null, Material = new PackageObjectReference([
            new("pc_donor_137", "package", "core"), new("mat", "package", "core"),
            new("pc_donor_137_lower_mi", "materialinstanceconstant", "engine")]) };
        var json = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["name"] = "MaterialInstanceConstant'mat.pc_test_a_mi'" },
            new System.Text.Json.Nodes.JsonObject { ["name"] = "MaterialInstanceConstant'mat.pc_test_base_upper_mi'" },
            new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "upper", ["extras"] = new System.Text.Json.Nodes.JsonObject { [SkeletalMeshMaterialReferences.ExtrasKey] = SkeletalMeshMaterialReferences.ToJson(reference) },
            },
            new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "lower", ["extras"] = new System.Text.Json.Nodes.JsonObject { [SkeletalMeshMaterialReferences.ExtrasKey] = SkeletalMeshMaterialReferences.ToJson(added) },
            });
        using var document = JsonDocument.Parse(json.ToJsonString());
        var names = new[] { "MaterialInstanceConstant'mat.pc_test_a_mi'", "MaterialInstanceConstant'mat.pc_test_base_upper_mi'", "upper", "lower" };
        var read = SkeletalMeshMaterialReferences.ReadAll(document.RootElement, names);
        Assert(read.Count == 2 && read["upper"].Material.Path == "pc_donor_137.mat.pc_donor_137_upper_mi" &&
               read["upper"].Replaces == reference.Replaces && read["lower"].Replaces is null, "the references read back from the GLB");
        var (slots, more, replaced) = SkeletalMeshMaterialReferences.Plan(names[..2], names, read);
        Assert(slots["upper"] == 0 && replaced.ContainsKey(0), "the replacing material takes the game slot's index");
        Assert(slots["lower"] == 2 && more.Count == 1, "the other material is added after the game's slots");
        var twice = new Dictionary<string, SkeletalMeshMaterialReference>(read) { ["lower"] = read["upper"] };
        Assert(Throws(() => SkeletalMeshMaterialReferences.Plan(names[..2], names, twice)), "two materials cannot take one slot's place");
    }

    private static void TestOwnMaterialsPlan()
    {
        var bones = new[] { "bip001-pelvis" };
        OutfitSwapMesh Mesh(int vertices, OutfitSwapSection[] sections, string[] materials) => new()
        {
            Positions = Enumerable.Range(0, vertices).Select(i => new Vector3(i * 0.1f, 1, i % 2 * 0.1f)).ToArray(),
            Normals = Enumerable.Repeat(Vector3.UnitZ, vertices).ToArray(),
            Tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), vertices).ToArray(),
            Uv0 = Enumerable.Range(0, vertices).Select(i => new Vector2(i * 0.1f, 0.3f)).ToArray(), Uv1 = new Vector2[vertices],
            Colours = Enumerable.Repeat((byte)255, vertices * 4).ToArray(),
            Joints = new int[vertices * 4], Weights = Enumerable.Range(0, vertices).SelectMany(_ => new[] { 1f, 0, 0, 0 }).ToArray(),
            Sections = sections, Materials = materials, BoneNames = bones, BonePositions = [new(0, 1, 0)], BoneParents = [-1],
        };
        PackageObjectReference Donor(string name) => new([new("pc_xx_av_1", "package", "core"), new("mat", "package", "core"),
            new(name, "materialinstanceconstant", "engine")]);
        var donor = Mesh(5, [new(0, [0, 1, 2]), new(1, [1, 2, 3]), new(2, [2, 3, 4]), new(3, [0, 2, 4])],
            ["MaterialInstanceConstant'mat.pc_xx_a_mi'", "MaterialInstanceConstant'mat.pc_xx_b_mi'",
             "MaterialInstanceConstant'mat.pc_xx_c_mi'", "MaterialInstanceConstant'mat.pc_xx_base_upper_mi'"]);
        var target = Mesh(3, [new(0, [0, 1, 2])],
            ["MaterialInstanceConstant'mat.pc_yy_a_mi'", "MaterialInstanceConstant'mat.pc_yy_base_upper_mi'", "MaterialInstanceConstant'mat.pc_yy_b_mi'"]);
        var sources = new OutfitSwapMaterialSources([[Donor("pc_xx_a_mi"), Donor("pc_xx_b_mi"), Donor("pc_xx_c_mi"), null]], null, null);
        var plan = OutfitSwapPlanner.Plan([new OutfitSwapPiece("pc_xx_av_1_upper_sk", donor, [])], [donor.Positions],
            [new OutfitSwapPiece("pc_yy_av_1_upper_sk", target, [])], null, sources);
        var mesh = plan.Pieces[0].Mesh;
        Assert(plan.Canvases.Count == 0, "nothing is repainted");
        Assert(mesh.MaterialReferences.Count == 3, "each donor cloth material is drawn as itself");
        var replaced = mesh.MaterialReferences.Values.Where(reference => reference.Replaces is not null).Select(reference => reference.Replaces).ToArray();
        Assert(replaced.Length == 2 && replaced.Contains("MaterialInstanceConstant'mat.pc_yy_a_mi'") &&
               replaced.Contains("MaterialInstanceConstant'mat.pc_yy_b_mi'"), "two take the target's cloth slots, never its skin slot");
        Assert(mesh.MaterialReferences.Values.Count(reference => reference.Replaces is null) == 1 && mesh.Materials.Count == 6,
            "the third is added after the target's slots");
        Assert(mesh.Sections.Any(section => section.Material == 1), "the donor's skin stays on the target's skin slot");
        Assert(mesh.Sections.Where(section => section.Material != 1).All(section => mesh.MaterialReferences.ContainsKey(section.Material)),
            "every cloth section is drawn with a donor material");
        Assert(mesh.Uv0.Take(3).SequenceEqual(donor.Uv0.Take(3)), "donor cloth keeps its own picture coordinates");
        Assert(Throws(() => OutfitSwapPlanner.Plan([new OutfitSwapPiece("pc_xx_av_1_upper_sk", donor, [])], [donor.Positions],
            [new OutfitSwapPiece("pc_yy_av_1_upper_sk", target, [])], null, sources with { Donors = [[null, null, null, null]] })),
            "a donor material Arkive cannot find refuses rather than painting");
        // Repainted instead, onto a piece with no skin slot: the bare skin gets the body's skin material, not the cloth picture.
        static OutfitSwapMaterial Picture(int slot, string name, string colour) => new(slot, OutfitSwapMesh.ShortMaterialName(name), false,
            new Dictionary<string, string> { ["texture_diffuse"] = colour }, new Dictionary<string, float[]>(), new Dictionary<string, float>());
        var bare = Mesh(3, [new(0, [0, 1, 2])], ["MaterialInstanceConstant'mat.pc_yy_av_1_lower_mi'"]);
        var bodySkin = new PackageObjectReference([new("pc_yy_basebody", "package", "core"), new("mat", "package", "core"),
            new("pc_yy_basebody_upper_mi", "materialinstanceconstant", "engine")]);
        var painted = OutfitSwapPlanner.Plan([new OutfitSwapPiece("pc_xx_av_1_upper_sk", donor, Enumerable.Range(0, 4)
                .Select(slot => Picture(slot, donor.Materials[slot], $"pc_xx_{slot}_d")).ToArray())], [donor.Positions],
            [new OutfitSwapPiece("pc_yy_av_1_upper_sk", bare, [Picture(0, bare.Materials[0], "pc_yy_av_1_lower_d")])], null,
            new OutfitSwapMaterialSources([], bodySkin, null) { KeepOwn = false });
        var repainted = painted.Pieces[0].Mesh;
        Assert(painted.Canvases.Count == 1 && repainted.Materials.Count == 2 && repainted.MaterialReferences.Single().Value.Material == bodySkin &&
               repainted.Sections.Any(section => section.Material == 1), "the repainted piece gains the body's skin slot for the bare skin");
        var glb = OutfitSwapMesh.WriteInto(MinimalGlb(target.Materials), mesh);
        var used = SkeletalMeshMaterialReferences.ReadUsed(glb);
        Assert(used.Count == 3 && used.All(reference => reference.Material.PackageName == "pc_xx_av_1"),
            "the swapped GLB carries each donor material's import chain");
    }

    private static byte[] MinimalGlb(IReadOnlyList<string> materials)
    {
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["asset"] = new System.Text.Json.Nodes.JsonObject { ["version"] = "2.0" },
            ["buffers"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["byteLength"] = 0 }),
            ["bufferViews"] = new System.Text.Json.Nodes.JsonArray(), ["accessors"] = new System.Text.Json.Nodes.JsonArray(),
            ["materials"] = new System.Text.Json.Nodes.JsonArray(materials.Select(name => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["name"] = name }).ToArray()),
            ["meshes"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["primitives"] = new System.Text.Json.Nodes.JsonArray() }),
        };
        return BuildSyntheticGlb(Encoding.UTF8.GetBytes(json.ToJsonString()), []);
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException) { return true; }
    }

    /// <summary>
    /// An uncompressed package: names, imports, a material instance export right after the header and a second export
    /// after it, laid out like a cooked outfit package.
    /// </summary>
    private static (byte[] Mic, byte[] Mesh) WriteMaterialPackage(string path)
    {
        string[] names = ["None", "core", "package", "class", "engine", "materialinstanceconstant", "skeletalmesh", "mat",
            "pc_test_mi", "pc_test_sk"];
        using var names_ = new MemoryStream();
        using (var writer = new BinaryWriter(names_, Encoding.UTF8, leaveOpen: true))
            foreach (var name in names) { WriteUnrealString(writer, name); writer.Write(0x0007001000000000UL); }
        // core, engine (packages), materialinstanceconstant and skeletalmesh (classes in engine).
        (int Package, int Class, int Outer, int Name)[] imports = [(1, 2, 0, 1), (1, 2, 0, 4), (1, 3, -2, 5), (1, 3, -2, 6)];
        using var imports_ = new MemoryStream();
        using (var writer = new BinaryWriter(imports_, Encoding.UTF8, leaveOpen: true))
            foreach (var (package, @class, outer, name) in imports)
            {
                WriteNameReference(writer, package); WriteNameReference(writer, @class); writer.Write(outer); WriteNameReference(writer, name);
            }
        const int nameOffset = 160;
        var importOffset = nameOffset + (int)names_.Length;
        var exportOffset = importOffset + (int)imports_.Length;
        const int exportRecord = 68;
        var dependsOffset = exportOffset + 2 * exportRecord;
        var headerSize = dependsOffset + 2 * 4;
        var mic = Enumerable.Range(0, 400).Select(i => (byte)(i * 7)).ToArray();
        var mesh = Enumerable.Range(0, 300).Select(i => (byte)(i * 13 + 1)).ToArray();
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var output = new BinaryWriter(stream);
        WriteSummary(output, headerSize, names.Length, nameOffset, 2, exportOffset, imports.Length, importOffset, dependsOffset, headerSize, []);
        while (stream.Position < nameOffset) output.Write((byte)0);
        output.Write(names_.ToArray());
        output.Write(imports_.ToArray());
        void Export(int @class, int name, int size, int offset)
        {
            output.Write(@class); output.Write(0); output.Write(0); WriteNameReference(output, name); output.Write(0);
            output.Write(0U); output.Write(0x000F0005U); output.Write(size); output.Write(offset); output.Write(0U); output.Write(0);
            output.Write(Guid.Empty.ToByteArray()); output.Write(0);
        }
        Export(-3, 8, mic.Length, headerSize);
        Export(-4, 9, mesh.Length, headerSize + mic.Length);
        output.Write(new byte[8]);
        output.Write(mic);
        output.Write(mesh);
        return (mic, mesh);
    }
}
