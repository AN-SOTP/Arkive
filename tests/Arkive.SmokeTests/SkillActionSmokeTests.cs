using Arkive.Core.Skills;

namespace Arkive.SmokeTests;

internal static class SkillActionSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("reads a skill action's stages, effect timings, sockets and effect parameters", TestReader),
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
        Text("Wing"); Text("FX"); Zeros(88); Text("CEFActionParticleData"); Zeros(36); Text("CEFParticleData"); Zeros(12);
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
               effect.Asset == "FX_PC_DDK_03.Par_S_DDK_NotosCrash_Wing_01" && effect.Socket == "B_Wing_Root",
            $"the effect notify was not read: start={effect.Start} name={effect.Name} asset={effect.Asset} socket={effect.Socket}");
        Assert(effect.Parameters.Count == 2 && effect.Parameters[0] is { Name: "Color", IsVector: true, X: 4, Y: 6, Z: 10 } &&
               effect.Parameters[1] is { Name: "Lifetime", IsVector: false } && Math.Abs(effect.Parameters[1].Value - 0.4f) < 1e-6,
            "the effect's parameters were not read");
        Assert(actions[0].ParticleSystems.SequenceEqual(["FX_PC_DDK_03.Par_S_DDK_NotosCrash_Wing_01"]), "the action's effects were not listed");
    }
}
