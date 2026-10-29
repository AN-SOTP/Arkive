using System.Numerics;
using Arkive.Core.Particles;
using Arkive.Core.Physics;
using Arkive.Core.Proportions;

namespace Arkive.SmokeTests;

internal static class PhysicsSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("fits a body capsule to a reshaped body: grown all round, or moved toward a one-sided change", TestFit),
        new("writes a fitted capsule's radius and centre as same-size value patches", TestPatches),
        new("finds the physics asset each class's outfit pieces are worn with", TestTargets),
        new("keeps Match physics in an upscale mod's tags and marks physics mods as format 4", TestSettingsAndFormat),
        new("lets the most recently saved upscale's physics win at the same priority instead of blocking", TestPhysicsTie),
    ];

    private const float Radius = 10;

    // A pelvis capsule 10 cm round, its axis along the bone's Z, at the bone itself.
    private static PhysicsAssetReport Report() => new("x.upk", new string('0', 64),
        new AssetCatalogEntry(4, "pc_test_00_physics", "mesh.pc_test_00_physics", "physicsasset", AssetKind.PhysicsAsset, 100, 0),
        new string('0', 64),
        [
            new PhysicsShape(0, "bip001-pelvis", true, PhysicsShapeKind.Capsule, 0,
                new AssetCatalogEntry(5, "rb_bodysetup_0", "mesh.rb_bodysetup_0", "rb_bodysetup", AssetKind.Object, 636, 1000),
                new string('A', 64), Matrix4x4.Identity, Radius, 0, 245, 333),
            new PhysicsShape(1, "b_skirt_f_02", false, PhysicsShapeKind.Box, 0,
                new AssetCatalogEntry(6, "rb_bodysetup_1", "mesh.rb_bodysetup_1", "rb_bodysetup", AssetKind.Object, 636, 2000),
                new string('B', 64), Matrix4x4.Identity, 0, 0, 245, -1),
        ]);

    /// <summary>A ring of pelvis vertices around the capsule, each pushed out by <paramref name="push"/> (cm) at its angle.</summary>
    private static (byte[] Before, byte[] After) Ring(Func<float, float> push, string bone = "bip001-pelvis")
    {
        var before = new List<(Vector3, int)>();
        var after = new List<(Vector3, int)>();
        for (var step = 0; step < 48; step++)
        {
            var angle = step * MathF.Tau / 48;
            var radial = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);
            // The game's bone space in cm; the GLB is (x, z, y) in metres.
            static Vector3 Glb(Vector3 game) => new Vector3(game.X, game.Z, game.Y) / 100;
            before.Add((Glb(radial * Radius), 0));
            after.Add((Glb(radial * (Radius + push(angle))), 0));
        }
        (string, Vector3)[] bones = [(bone, Vector3.Zero)];
        return (ProportionSmokeTests.BuildMesh(bones, [.. before]), ProportionSmokeTests.BuildMesh(bones, [.. after]));
    }

    private static void TestFit()
    {
        var uniform = new BodyPhysicsFitter(Report());
        var (before, after) = Ring(_ => 2);
        uniform.Add(before, after);
        var grown = uniform.Result().Shapes.Single();
        Assert(grown.Bone == "bip001-pelvis" && Math.Abs(grown.RadiusChange - 2) < 0.05f && grown.CentreShift.Length() < 0.05f,
            $"a body made 2 cm fuller all round grew its capsule by {grown.RadiusChange} and moved it {grown.CentreShift}");

        // Fuller only towards -Y (one side, as buttocks pushed back are): the capsule moves that way as well as growing.
        var oneSided = new BodyPhysicsFitter(Report());
        (before, after) = Ring(angle => MathF.Sin(angle) < 0 ? 3 * -MathF.Sin(angle) : 0);
        oneSided.Add(before, after);
        var moved = oneSided.Result().Shapes.Single();
        Assert(moved.ShiftY < -0.5f && Math.Abs(moved.ShiftX) < 0.1f && moved.RadiusChange > 0.3f,
            $"a one-sided change moved the capsule by {moved.CentreShift} and grew it by {moved.RadiusChange}");

        var unchanged = new BodyPhysicsFitter(Report());
        (before, _) = Ring(_ => 0);
        unchanged.Add(before, before);
        Assert(unchanged.Result().IsEmpty, "an unchanged body changed its physics");

        // Hair and hanging cloth collide with the body; they are not part of it.
        var hanging = new BodyPhysicsFitter(Report());
        (before, after) = Ring(_ => 2, "b_skirt_f_02");
        hanging.Add(before, after);
        Assert(hanging.Result().IsEmpty, "skirt vertices were fitted as the body");
    }

    private static void TestPatches()
    {
        var report = Report();
        var fit = new BodyPhysicsFit([new BodyPhysicsShapeChange("bip001-pelvis", PhysicsShapeKind.Capsule, 0, 2, 0, -1.5f, 0, 40)]);
        var patches = PhysicsAssets.Patches(report, fit);
        Assert(patches.Count == 2, $"{patches.Count} patches were written for one capsule");
        var radius = patches.Single(patch => patch.Property == "radius");
        var centre = patches.Single(patch => patch.Property == "tm");
        Assert(radius.ValueOffset == 333 && radius.Original.SequenceEqual([Radius]) && radius.Value.SequenceEqual([Radius + 2]) &&
               radius.ExportIndex == 5 && radius.SourceExportSha256 == new string('A', 64),
            "the radius patch does not write the grown radius into its body");
        // Each matrix row is stored with its fourth value first: the centre is floats 13 to 15.
        Assert(centre.ValueOffset == 245 + 13 * 4 && centre.Value.SequenceEqual([0, -1.5f, 0]), "the centre patch is not at the transform's last row");
        var shrink = new BodyPhysicsFit([new BodyPhysicsShapeChange("bip001-pelvis", PhysicsShapeKind.Capsule, 0, -9, 0, 0, 0, 40)]);
        Assert(PhysicsAssets.Patches(report, shrink).Single().Value[0] == Radius / 2, "a capsule was shrunk below half its radius");
        var bytes = new byte[636];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(333), Radius);
        var written = ParticleEffects.ApplyPatches(bytes, [radius]);
        Assert(written.Length == bytes.Length && BinaryPrimitives.ReadSingleLittleEndian(written.AsSpan(333)) == Radius + 2,
            "the radius was not written in place");
    }

    private static void TestTargets()
    {
        Assert(BodyPhysicsTargets.ForPiece("pc_wr_f_av_226a_lower_sk") == new BodyPhysicsTarget("pc_wr_f_00", "pc_wr_f_00_physics") &&
               BodyPhysicsTargets.ForPiece("pc_wbk_f_00_upper_sk")?.ObjectName == "pc_wr_f_00_physics" &&
               BodyPhysicsTargets.ForPiece("pc_dl_av_151_upper_sk")?.ObjectName == "pc_dl_00_physics" &&
               BodyPhysicsTargets.ForPiece("pc_gn_f_av_120_lower_sk")?.ObjectName == "pc_gn_f_00_physics" &&
               BodyPhysicsTargets.ForPiece("pc_fbm_m_00_upper_sk")?.ObjectName == "pc_ft_m_00_physics" &&
               BodyPhysicsTargets.ForPiece("pc_dk_av_101_dress_sk")?.ObjectName == "pc_dk_00_physics" &&
               BodyPhysicsTargets.ForPiece("pc_sp_m_av_010_upper_sk")?.ObjectName == "pc_sp_m_00_physics",
            "a class's outfit pieces were matched to another class's physics");
        Assert(BodyPhysicsTargets.ForPiece("mn_cuas_01_sk") is null, "an NPC was given a player physics asset");
    }

    private static void TestSettingsAndFormat()
    {
        var warrior = BodyProfiles.All.First(profile => profile.Id == "female-warrior");
        var settings = new BodyProportionModSettings(warrior, new Dictionary<string, double> { ["chest"] = 25 }, default, MatchPhysics: true);
        var tags = BodyProportionModTags.Write(settings);
        Assert(BodyProportionModTags.Read(tags, null) is { MatchPhysics: true } &&
               BodyProportionModTags.Read(BodyProportionModTags.Write(settings with { MatchPhysics = false }), null) is { MatchPhysics: false },
            "Match physics did not survive the mod's tags");
        Assert(BodyProportionModTags.Description(settings, ["pc_wr_f_av_226a_upper_sk"]).Contains("physics match", StringComparison.Ordinal),
            "the description does not say the physics match");
        var physics = new ArkiveModFileAsset("a", UnifiedPackageAssetKind.PhysicsAsset, "physics",
            new ArkiveModAssetTarget("pc_wr_f_00", "x.upk", "physicsasset", "pc_wr_f_00_physics", 1, new string('0', 64), new string('0', 64)), []);
        var manifest = ArkiveModAuthoringService.CreateManifest(new string('a', 32), new ArkiveModMetadata("m", "1.0.0"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) with { Assets = [physics] };
        Assert(ArkiveModFileFormat.VersionFor(manifest) == ArkiveModFileFormat.PhysicsFormatVersion &&
               ArkiveModFileFormat.VersionFor(manifest with { Assets = [] }) == 1,
            "physics mods are not written as format 4");
    }

    private static void TestPhysicsTie()
    {
        ArkiveModLibraryEntry Mod(string name, int priority, DateTimeOffset saved, UnifiedPackageAssetKind kind)
        {
            var asset = new ArkiveModFileAsset("a", kind, name,
                new ArkiveModAssetTarget("pc_wr_f_00", "VC2N892NX2H00FGREHFEB00X.upk", kind == UnifiedPackageAssetKind.PhysicsAsset ? "physicsasset" : "skeletalmesh",
                    "pc_wr_f_00_physics", 2161, new string('0', 64), new string('0', 64)), []);
            var id = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
            var manifest = ArkiveModAuthoringService.CreateManifest(id, new ArkiveModMetadata(name, "1.0.0"), saved, saved) with { Assets = [asset] };
            return new ArkiveModLibraryEntry(id, name + ".arkive", "", 0, saved, true, priority, ArkiveModOrigin.Created, manifest, []);
        }
        var older = Mod("upscale 226a", 0, DateTimeOffset.UnixEpoch, UnifiedPackageAssetKind.PhysicsAsset);
        var newer = Mod("upscale 142c", 0, DateTimeOffset.UnixEpoch.AddDays(1), UnifiedPackageAssetKind.PhysicsAsset);
        var target = ArkiveModConflictResolver.Resolve([older, newer]).Targets.Single();
        Assert(!target.IsBlockingTie && target.Winner?.ModId == newer.ModId, "two upscale mods' physics at one priority block each other");
        var higher = ArkiveModConflictResolver.Resolve([older with { Priority = 2 }, newer]).Targets.Single();
        Assert(higher.Winner?.ModId == older.ModId, "a higher priority no longer wins over a newer upscale's physics");
    }
}
