using Arkive.Core.Interface;
using Arkive.Core.Interface.Abc;

namespace Arkive.SmokeTests;

internal static class TweakSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("reads and writes ActionScript 3 code and movie tags byte for byte", TestAbcRoundTrip),
        new("routes a movie method through the tweak dispatcher and keeps the original under a new name", TestHookInjection),
    ];

    private static void TestAbcRoundTrip()
    {
        var abc = BuildGreeter().Write();
        Assert(AbcFile.Read(abc).Write().AsSpan().SequenceEqual(abc), "the code did not write back unchanged");
        var movie = BuildMovie(abc);
        var tags = GfxMovieTags.Read(movie);
        Assert(tags.Write().AsSpan().SequenceEqual(movie), "the movie's tags did not write back unchanged");
        Assert(tags.MainAbc.AbcData.SequenceEqual(abc), "the movie's code block was not found");
        tags.AppendAbc(GfxTag.NewDoAbc("arkive/tweaks", abc));
        var grown = GfxMovieTags.Read(tags.Write());
        Assert(grown.Tags.Count(tag => tag.Code == GfxTag.DoAbc) == 2 && grown.Tags[^1].Code == GfxTag.End &&
               grown.Tags.Last(tag => tag.Code == GfxTag.DoAbc).AbcName == "arkive/tweaks",
            "an added code block was not placed after the movie's own code");
    }

    private static void TestHookInjection()
    {
        var abc = BuildGreeter();
        var originalMethod = abc.Instances[0].Traits.Single().Index;
        var missing = AbcHookInjector.Inject(abc, [new AbcHook("demo.Greeter", "greet"), new AbcHook("demo.Greeter", "wave")]);
        Assert(missing.SequenceEqual(["demo.Greeter.wave"]), "a method the class doesn't have was not reported: " + string.Join(", ", missing));

        var reread = AbcFile.Read(abc.Write());
        var traits = reread.Instances[0].Traits;
        var kept = traits.Single(trait => reread.Pool.NameOf(trait.Name) == "greet" + AbcHookInjector.RenamedSuffix);
        var wrapper = traits.Single(trait => reread.Pool.NameOf(trait.Name) == "greet");
        Assert(kept.Index == originalMethod, "the original body was not kept under its new name");
        Assert(reread.Pool.NamespaceNameOf(kept.Name) == "", "the kept method did not stay in the original's namespace");
        var method = reread.Methods[wrapper.Index];
        Assert(method.ParameterTypes.Count == 1 && reread.Pool.NameOf(method.ReturnType) == "String",
            "the wrapper does not have the original's signature");
        var body = reread.BodyOf(wrapper.Index) ?? throw new InvalidOperationException("wrapper has no body");
        Assert(body.Code[^1] == 0x48 && body.LocalCount == 2, "the wrapper does not return the dispatcher's value");
        Assert(reread.Pool.Strings.Contains("demo.Greeter.greet") && reread.Pool.Strings.Contains(AbcHookInjector.DispatcherClass),
            "the wrapper does not call the dispatcher with the hook's id");
        var again = AbcHookInjector.Inject(reread, [new AbcHook("demo.Greeter", "greet")]);
        Assert(again.Count == 0 && reread.Instances[0].Traits.Count == 2, "hooking twice added a second wrapper");
    }

    /// <summary>package demo { public class Greeter { public function greet(name:String):String { return name; } } }</summary>
    internal static AbcFile BuildGreeter()
    {
        var abc = new AbcFile { MinorVersion = 16, MajorVersion = 46 };
        var pool = abc.Pool;
        var publicNs = pool.AddNamespace(AbcNamespace.PackageNamespace, "");
        var demo = pool.AddNamespace(AbcNamespace.PackageNamespace, "demo");
        var objectName = pool.AddQName(publicNs, "Object");
        var stringName = pool.AddQName(publicNs, "String");
        var greeter = pool.AddQName(demo, "Greeter");
        var greet = pool.AddQName(publicNs, "greet");
        for (var index = 0; index < 4; index++) abc.Methods.Add(new AbcMethod());
        abc.Methods[2].ParameterTypes.Add(stringName);
        abc.Methods[2].ReturnType = stringName;
        var instance = new AbcInstance { Name = greeter, SuperName = objectName, Flags = AbcInstance.Sealed, Initializer = 0 };
        instance.Traits.Add(new AbcTrait { Name = greet, KindByte = AbcTrait.Method, Index = 2 });
        abc.Instances.Add(instance);
        abc.Classes.Add(new AbcClass { Initializer = 1 });
        var script = new AbcScript { Initializer = 3 };
        script.Traits.Add(new AbcTrait { Name = greeter, KindByte = AbcTrait.Class, Index = 0 });
        abc.Scripts.Add(script);
        byte[] returnVoid = [0xD0, 0x30, 0x47];
        abc.Bodies.Add(new AbcMethodBody { Method = 0, MaxStack = 1, LocalCount = 1, InitScopeDepth = 4, MaxScopeDepth = 5, Code = [0xD0, 0x30, 0xD0, 0x49, 0x00, 0x47] });
        abc.Bodies.Add(new AbcMethodBody { Method = 1, MaxStack = 1, LocalCount = 1, InitScopeDepth = 3, MaxScopeDepth = 4, Code = returnVoid });
        abc.Bodies.Add(new AbcMethodBody { Method = 2, MaxStack = 1, LocalCount = 2, InitScopeDepth = 4, MaxScopeDepth = 5, Code = [0xD0, 0x30, 0xD1, 0x48] });
        abc.Bodies.Add(new AbcMethodBody { Method = 3, MaxStack = 2, LocalCount = 1, InitScopeDepth = 1, MaxScopeDepth = 3, Code = returnVoid });
        return abc;
    }

    /// <summary>A one-frame uncompressed movie holding the given code.</summary>
    internal static byte[] BuildMovie(byte[] abc)
    {
        var tags = new MemoryStream();
        var doAbc = GfxTag.NewDoAbc("", abc, lazy: true);
        tags.Write(BitConverter.GetBytes((ushort)((GfxTag.DoAbc << 6) | 0x3F)));
        tags.Write(BitConverter.GetBytes(doAbc.Body.Length));
        tags.Write(doAbc.Body);
        tags.Write([0x40, 0x00]); // ShowFrame
        tags.Write([0x00, 0x00]); // End
        byte[] header = [(byte)'G', (byte)'F', (byte)'X', 41, 0, 0, 0, 0, 0x08, 0x00, 0x00, 0x18, 0x01, 0x00];
        var movie = header.Concat(tags.ToArray()).ToArray();
        BitConverter.GetBytes(movie.Length).CopyTo(movie, 4);
        return movie;
    }
}
