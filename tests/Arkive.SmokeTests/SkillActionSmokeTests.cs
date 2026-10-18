using Arkive.Core.Skills;

namespace Arkive.SmokeTests;

internal static class SkillActionSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("reads a skill action's stages, effect timings, sockets and effect parameters", TestReader),
        new("splits a skill action into the versions the preview offers", TestBranches),
    ];

    // A Guardian Clash-like action as the client serialises it: classes and names as length-prefixed strings (a
    // negative length is UTF-16), fields at fixed offsets between them.
    private static byte[] Action()
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        void Text(string value) { writer.Write(value.Length + 1); writer.Write(Encoding.ASCII.GetBytes(value)); writer.Write((byte)0); }
        void Wide(string value) { writer.Write(-(value.Length + 1)); writer.Write(Encoding.Unicode.GetBytes(value)); writer.Write((short)0); }
        void Zeros(int count) => writer.Write(new byte[count]);
        Text("CEFActionObject"); writer.Write(0); writer.Write(49410); Wide("가디언즈 크래쉬");
        Zeros(32);
        Text("CEFActionStage"); Zeros(48); Wide("일반 상태"); writer.Write(1.1f); writer.Write(1f); Zeros(40);
        Text("CEFActionNotify_Anim"); Zeros(16); writer.Write(0f); writer.Write(0f); writer.Write(1.1f); Zeros(12);
        Text("Anim"); Zeros(8); Text("SK_NotosCrash_01"); Zeros(24);
        Text("CEFActionNotify_PlayParticleEffect"); Zeros(16); writer.Write(0.22f); writer.Write(0.22f); writer.Write(0f); Zeros(12);
        Text("Wing"); Text("FX-01"); Zeros(85); Text("CEFActionParticleData"); Zeros(36); Text("CEFParticleData"); Zeros(12);
        Text("ParticleSystem'FX_PC_DDK_03.Group.Par_S_DDK_NotosCrash_Wing_01'"); Zeros(59); Text("B_Wing_Root"); Zeros(100);
        Text("Color"); writer.Write(3); writer.Write(0f); writer.Write(0); writer.Write(4f); writer.Write(6f); writer.Write(10f); Zeros(32);
        Text("None"); writer.Write(0);
        Text("Lifetime"); writer.Write(1); writer.Write(0.4f); Zeros(48);
        Text("None"); writer.Write(0);
        return stream.ToArray();
    }

    private static void TestReader()
    {
        var actions = SkillActionReader.Read(Action());
        Assert(actions.Count == 1 && actions[0].Id == 49410 && actions[0].Name == "가디언즈 크래쉬", "the action's id or name was not read");
        var stage = actions[0].Stages.Single();
        Assert(stage.Name == "일반 상태" && Math.Abs(stage.Duration - 1.1f) < 1e-6 && stage.Rate == 1, "the stage's name or length was not read");
        Assert(stage.Notifies.Count == 2 && stage.Notifies[0] is { Kind: "Anim", Asset: "SK_NotosCrash_01" },
            "the animation notify was not read: " + string.Join(", ", stage.Notifies.Select(notify => $"{notify.Kind}:{notify.Asset}")));
        var effect = stage.Notifies[1];
        Assert(effect.IsParticle && Math.Abs(effect.Start - 0.22f) < 1e-6 && effect.Name == "Wing" &&
               effect.Asset == "FX_PC_DDK_03.Par_S_DDK_NotosCrash_Wing_01" && effect.Socket == "B_Wing_Root" && effect.Group == "FX-01",
            $"the effect notify was not read: start={effect.Start} name={effect.Name} set={effect.Group} asset={effect.Asset} socket={effect.Socket}");
        Assert(effect.Parameters.Count == 2 && effect.Parameters[0] is { Name: "Color", IsVector: true, X: 4, Y: 6, Z: 10 } &&
               effect.Parameters[1] is { Name: "Lifetime", IsVector: false } && Math.Abs(effect.Parameters[1].Value - 0.4f) < 1e-6,
            "the effect's parameters were not read");
        Assert(actions[0].ParticleSystems.SequenceEqual(["FX_PC_DDK_03.Par_S_DDK_NotosCrash_Wing_01"]), "the action's effects were not listed");
    }

    private static void TestBranches()
    {
        static SkillActionStage Stage(string name, string? anim) =>
            new(name, 1, 1, anim is null ? [] : [new SkillActionNotify("Anim", 0, 1, null, anim, null, [])]);
        // A state check, the normal form (three stages), the dragon form (same animations), then a rune version.
        var action = new SkillAction(1, "test", [
            Stage("StageName", null), Stage("Normal", "SK_A_01"), Stage("StageName", "SK_A_02"), Stage("StageName", "SK_A_03"),
            Stage("Dragon", "SK_A_01"), Stage("StageName", "SK_A_02"), Stage("StageName", "SK_A_03"),
            Stage("StageName", null), Stage("Main", "SK_A_Custom_01"), Stage("StageName", "SK_A_03"),
        ]);
        var branches = SkillCatalog.Branches(action);
        Assert(branches.Count == 2 && branches[0].Name == "Normal" && branches[0].Stages.Count == 3 &&
               branches[1].Name == "SK_A_Custom_01" && branches[1].Stages.Count == 2,
            "skill versions were not split as expected: " + string.Join(", ", branches.Select(branch => $"{branch.Name}({branch.Stages.Count})")));

        // A charged skill whose overcharge plays its charge again: the overcharged version keeps the opening.
        var charged = new SkillAction(2, "test", [
            Stage("Main", "SK_B_01"), Stage("Charge", "SK_B_02"), Stage("StageName", "SK_B_03"),
            Stage("OverCharge", "SK_B_02"), Stage("StageName", "SK_B_03"),
        ]);
        var versions = SkillCatalog.Branches(charged);
        Assert(versions.Count == 2 && versions[0].Stages.Count == 3 && versions[1].Name == "OverCharge" &&
               versions[1].Stages.Select(stage => stage.Name).SequenceEqual(["Main", "OverCharge", "StageName"]),
            "an overcharged version was not split off: " + string.Join(", ", versions.Select(branch => $"{branch.Name}({string.Join("/", branch.Stages.Select(stage => stage.Name))})")));
    }
}
