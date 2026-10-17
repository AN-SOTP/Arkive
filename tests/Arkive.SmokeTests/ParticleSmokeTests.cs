using Arkive.Core.Particles;

namespace Arkive.SmokeTests;

internal static class ParticleSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("reads Lost Ark particle property streams with their one-byte bools and named int tags", TestPropertyStream),
        new("tints particle colour tables by hue, keeping brightness, the header and darkening colours", TestTint),
        new("writes particle patches only over the values they were made for", TestPatches),
    ];

    private static readonly UnrealNameEntry[] Names = new[] { "none", "lifetime", "structproperty", "rawdistributionfloat",
        "distribution", "objectproperty", "lookuptable", "arrayproperty", "bkillondeactivate", "boolproperty",
        "emitterloops", "intproperty", "lodvalidity", "byteproperty" }
        .Select((value, index) => new UnrealNameEntry(index, value, 0)).ToArray();

    private static int Name(string value) => Array.FindIndex(Names, name => name.Value == value);

    // A ParticleModuleLifetime as the client stores it: net index, a RawDistributionFloat whose LookupTable holds
    // [header, header, 0.14, 0.14], a one-byte bool, an int tag with its extra name, a byte enum and None.
    private static byte[] LifetimeModule()
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        void Tag(string name, string type, int size) { writer.Write(Name(name)); writer.Write(0); writer.Write(Name(type)); writer.Write(0); writer.Write(size); writer.Write(0); }
        void NameRef(string name) { writer.Write(Name(name)); writer.Write(0); }
        writer.Write(14887);
        var nested = new MemoryStream();
        var inner = new BinaryWriter(nested);
        void InnerTag(string name, string type, int size) { inner.Write(Name(name)); inner.Write(0); inner.Write(Name(type)); inner.Write(0); inner.Write(size); inner.Write(0); }
        InnerTag("distribution", "objectproperty", 4); inner.Write(0);
        InnerTag("lookuptable", "arrayproperty", 20); inner.Write(4); inner.Write(0.14f); inner.Write(0.14f); inner.Write(0.14f); inner.Write(0.14f);
        inner.Write(Name("none")); inner.Write(0);
        Tag("lifetime", "structproperty", (int)nested.Length); NameRef("rawdistributionfloat"); writer.Write(nested.ToArray());
        Tag("bkillondeactivate", "boolproperty", 0); writer.Write((byte)1);
        Tag("emitterloops", "intproperty", 4); NameRef("none"); writer.Write(1);
        Tag("lodvalidity", "byteproperty", 1); NameRef("none"); writer.Write((byte)3);
        NameRef("none");
        return stream.ToArray();
    }

    private static void TestPropertyStream()
    {
        var bytes = LifetimeModule();
        var properties = ParticlePropertyStream.Read(bytes, Names) ?? throw new InvalidOperationException("the stream was not read");
        Assert(properties.Select(property => property.Name).SequenceEqual(["lifetime", "bkillondeactivate", "emitterloops", "lodvalidity"]),
            "properties were not read in order: " + string.Join(", ", properties.Select(property => property.Name)));
        var table = properties[0].Child("lookuptable") ?? throw new InvalidOperationException("the nested table is missing");
        Assert(properties[0].StructName == "rawdistributionfloat" && table.Size == 20 &&
               BitConverter.ToSingle(bytes, table.ValueOffset + 4) == 0.14f, "the LookupTable value offset is wrong");
        Assert(bytes[properties[1].ValueOffset] == 1 && BitConverter.ToInt32(bytes, properties[2].ValueOffset) == 1 &&
               bytes[properties[3].ValueOffset] == 3, "bool, int or byte values were read from the wrong place");
        Assert(ParticlePropertyStream.Read(bytes.AsSpan(0, bytes.Length - 1), Names) is null, "a cut-off stream was accepted");
    }

    private static void TestTint()
    {
        // Header, then an orange sample, a white HDR sample, a darkening sample and a red-with-negative sample.
        float[] table = [0.3f, 9f, 1f, 0.5f, 0f, 3f, 3f, 3f, -1f, -1f, -1f, 9f, -1f, 0.5f];
        var red = ParticleEffects.Tint(table, new ParticleEffectTint(2, 0.1f, 0));
        Assert(red[0] == 0.3f && red[1] == 9f, "the table header was changed");
        static bool Near(IEnumerable<float> actual, float[] expected) => actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < 1e-5f);
        Assert(Near(red.Skip(2).Take(3), [1f, 0.05f, 0f]) && Near(red.Skip(5).Take(3), [3f, 0.15f, 0f]),
            "colours did not take the hue at their own brightness: " + string.Join(", ", red));
        Assert(red.Skip(8).Take(3).SequenceEqual([-1f, -1f, -1f]), "a darkening colour was tinted");
        Assert(Near(red.Skip(11).Take(3), [9f, 0.45f, 0f]), "a colour with a negative channel was not tinted by its strongest channel");
        Assert(!new ParticleEffectTint(0, 0, 0).IsValid && !new ParticleEffectTint(-1, 1, 1).IsValid && new ParticleEffectTint(1, 0, 0).IsValid,
            "tints without colour or with negative channels were accepted");
    }

    private static void TestPatches()
    {
        var bytes = LifetimeModule();
        var table = ParticlePropertyStream.Read(bytes, Names)![0].Child("lookuptable")!;
        var patch = new ParticleEffectPatch("pkg.system.module", "particlemodulelifetime", 3, 0, bytes.Length, new string('0', 64),
            "lifetime", table.ValueOffset + 4, [0.14f, 0.14f, 0.14f, 0.14f], [0.14f, 0.14f, 0.5f, 0.5f]);
        var patched = ParticleEffects.ApplyPatches(bytes, [patch]);
        Assert(patched.Length == bytes.Length && BitConverter.ToSingle(patched, table.ValueOffset + 12) == 0.5f &&
               patched.Take(table.ValueOffset).SequenceEqual(bytes.Take(table.ValueOffset)),
            "the patch did not write only its own values");
        Assert(ParticlePropertyStream.Read(patched, Names) is not null, "the patched export no longer reads through");
        try
        {
            ParticleEffects.ApplyPatches(patched, [patch]);
            throw new InvalidOperationException("a patch was written over values it was not made for");
        }
        catch (InvalidDataException)
        {
        }
        Assert(ParticleEffects.ContentSha256([patch]) != ParticleEffects.ContentSha256([patch with { Value = [0.14f, 0.14f, 0.6f, 0.5f] }]),
            "the content hash ignores patched values");
    }
}
