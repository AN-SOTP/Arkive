using Arkive.Core.Interface;
using Arkive.Core.Interface.Abc;

namespace Arkive.SmokeTests;

internal static class TweakSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("reads and writes ActionScript 3 code and movie tags byte for byte", TestAbcRoundTrip),
        new("hooks a movie method: keeps the original under a new name and calls the tweak handler after it", TestHookInjection),
        new("hooks before a method (able to skip it) and after it (able to change its result)", TestHookKinds),
        new("hooks one method for two tweaks: the second wrapper wraps the first, so both handlers run", TestTwoHooksOneMethod),
        new("exposes a private method to tweak code under its plain public name, bound to the same method", TestExpose),
        new("exposes a private field to tweak code through a public getter and setter", TestExposeField),
        new("skips the intro movies by moving them to a backup and puts them back exactly", TestIntroMovies),
        new("ships tweak code with no astype and no typed nulls, and with its settings written in", TestShippedTweakCode),
        new("reads a player's logs from a lostark.bible page and writes the summary the Logs tab draws", TestPlayerLogs),
        new("deletes tweak working edits left by replaced tweak mods and keeps everyone else's", TestStaleTweakDrafts),
    ];

    /// <summary>
    /// The Logs tab draws Arkive's summary file. The page data is "devalue" encoded (objects hold indexes into one flat
    /// array); a page for a name that isn't in that region has no logs node but suggests the character's region.
    /// </summary>
    private static void TestPlayerLogs()
    {
        const string page = """
            {"type":"data","nodes":[{"type":"data","data":[{"userData":-1}]},null,
            {"type":"data","data":[{"logsEnabled":1,"isOwner":2,"isPublic":1,"logs":3},true,false,[4],
            {"boss":5,"difficulty":6,"class":7,"spec":8,"dps":9,"percentile":10,"duration":11,"timestamp":12,"isDead":1},
            "Archbishop Arcenos","Level 3","Slayer","Punisher",1681234567.5,0.888,327000,1792875478152]}]}
            """;
        using var document = System.Text.Json.JsonDocument.Parse(page);
        var lookup = Arkive.Core.Tweaks.PlayerLogsService.Read("Chillidari", "CE", document.RootElement);
        Assert(lookup is { Status: Arkive.Core.Tweaks.PlayerLogsStatus.Found, Logs.Count: 1 } &&
               lookup.Logs[0] is { Boss: "Archbishop Arcenos", Difficulty: "Level 3", ClassName: "Slayer", Died: true },
            "the logs page was not read");
        var summary = Arkive.Core.Tweaks.PlayerLogsService.Summary(lookup!, "123x45").Split('\n');
        Assert(summary[0] == "found\tEU Central\tChillidari\t1\t123x45\t", $"summary head: {summary[0]}");
        Assert(summary[1].StartsWith("#raids\tBelgardin\tHorizon Cathedral\t", StringComparison.Ordinal), $"summary raids: {summary[1]}");
        Assert(summary[2].StartsWith("Horizon Cathedral\tG1\tArchbishop Arcenos\tLevel 3\tSlayer\t1.68b\t88\t", StringComparison.Ordinal) &&
               summary[2].EndsWith("\t5:27\tdied", StringComparison.Ordinal), $"summary row: {summary[2]}");

        // A character whose owner keeps logs private (Zurmos on EU Central): the page has the character but no logs.
        const string hidden = """
            {"type":"data","nodes":[{"type":"data","data":[{"userData":-1}]},{"type":"data","data":[
            {"header":1,"redirectedFrom":4,"suggestions":5},{"id":2,"rid":3},270889558,27179,null,[]]},
            {"type":"data","data":[{"logsEnabled":1,"isOwner":1},false]}]}
            """;
        using var hiddenPage = System.Text.Json.JsonDocument.Parse(hidden);
        Assert(Arkive.Core.Tweaks.PlayerLogsService.Read("Zurmos", "CE", hiddenPage.RootElement) is
               { Status: Arkive.Core.Tweaks.PlayerLogsStatus.Hidden, CharacterId: 270889558, RosterId: 27179 },
            "a character with private logs was not told apart from a missing one");

        // The tab spells request id, raid, name (4 hex digits per character) and a checksum; Arkive reads it back and
        // rejects a message with a missed or extra read.
        var spelled = Arkive.Core.Tweaks.PlayerLogsSignal.Encode("0a1f", 3, "Améthyst");
        Assert(Arkive.Core.Tweaks.PlayerLogsSignal.TryDecode(spelled, out var request, out var raid, out var name) &&
               request == "0a1f" && raid == 3 && name == "Améthyst",
            $"the signal message did not decode: {spelled}");
        Assert(!Arkive.Core.Tweaks.PlayerLogsSignal.TryDecode(spelled.Remove(9, 1) + "0", out _, out _, out _) &&
               !Arkive.Core.Tweaks.PlayerLogsSignal.TryDecode(spelled[..^4] + "0000", out _, out _, out _),
            "a damaged signal message was accepted");
        var signal = new Arkive.Core.Tweaks.PlayerLogsSignal(Path.GetTempPath());
        string? heard = null;
        signal.Requested += (who, _, _) => heard = who;
        foreach (var symbol in (string[])["5", "go", .. spelled.Select(digit => digit.ToString()), "end"])
            signal.Receive(symbol);
        Assert(heard == "Améthyst", "the reads between go and end were not put together");

        const string missing = """
            {"type":"data","nodes":[{"type":"data","data":[{"userData":-1}]},{"type":"data","data":[
            {"header":-1,"redirectedFrom":1,"suggestions":2},null,[3],{"name":4,"region":5},"Chillidari","CE"]}]}
            """;
        using var other = System.Text.Json.JsonDocument.Parse(missing);
        Assert(Arkive.Core.Tweaks.PlayerLogsService.Read("Chillidari", "NA", other.RootElement) is null &&
               Arkive.Core.Tweaks.PlayerLogsService.SuggestedRegion("Chillidari", other.RootElement) == "CE",
            "a page without the character should give no logs and suggest its region");
    }

    /// <summary>
    /// Installing a tweak mod sets its movies up as working edits; older builds removed the mod but left those edits
    /// ticked, and the next build's mod then clashed with them ("The checked draft 'Tweaks: …' and mod 'Arkive Tweaks'
    /// both replace …"). Releasing deletes every tweak edit no other mod is bound to and leaves a player's own edits.
    /// </summary>
    private static void TestStaleTweakDrafts()
    {
        var root = Path.Combine(fixtureDirectory, "tweak-drafts");
        var packagePath = Path.Combine(root, "client", "EFGame", "ReleasePC", "Packages", "efui_test.upk");
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        InterfaceSmokeTests.WriteInterfaceMoviePackage(packagePath, InterfaceSmokeTests.BuildExport(InterfaceSmokeTests.BuildMovie("QuickSlot", compressed: false)));
        var environment = ArkiveModTestFixture.CreateEnvironment(Path.Combine(root, "data"));
        var store = environment.Edits.InterfaceMovies!;
        var export = new AssetCatalogReader(null, NoChunkProtectionProvider.Instance).Read(packagePath).Assets
            .Single(asset => asset.ClassName.Equals("EFSwfMovie", StringComparison.OrdinalIgnoreCase));
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));
        var stale = store.CreateAsync(packagePath, "efui_test", hash, export, Arkive.Core.Tweaks.TweakModService.DraftPrefix + "efui_test TestMovie")
            .GetAwaiter().GetResult();
        var own = store.CreateAsync(packagePath, "efui_test", hash, export, "My own HP bar edit").GetAwaiter().GetResult();
        var service = new Arkive.Core.Tweaks.TweakModService(environment.Library, Path.Combine(root, "tweaks"), NoChunkProtectionProvider.Instance)
        {
            Edits = environment.Edits,
        };
        var released = service.ReleaseDraftsAsync([]).GetAwaiter().GetResult();
        var left = ArkiveModEditSnapshot.LoadAsync(environment.Edits).GetAwaiter().GetResult();
        Assert(released == 1 && left.Find(stale.Manifest.EditId) is null && left.Find(own.Manifest.EditId) is { Included: true },
            $"stale tweak edits were not deleted, or the player's own edit was touched (released {released})");
    }

    /// <summary>
    /// The game's player froze on Haxe's typed nulls (pushnull then coerce/astype); the game's own code never has
    /// them. Every block Arkive adds must be free of astype and of pushnull followed by coerce.
    /// </summary>
    private static void TestShippedTweakCode()
    {
        var settings = new Arkive.Core.Tweaks.TweakSettings();
        settings.Enabled["lobbycp"] = [];
        var blocks = Arkive.Core.Tweaks.TweakCode.ForSettings(Arkive.Core.Tweaks.TweakCode.LoadSwf(), settings);
        Assert(blocks.Count > 0 && blocks.All(block => block.AbcName.StartsWith(Arkive.Core.Tweaks.TweakCode.AbcName + "/")),
            "the tweak code blocks are missing or unnamed");
        var withSettings = 0;
        foreach (var block in blocks)
        {
            var abc = AbcFile.Read(block.AbcData);
            if (abc.Pool.Strings.Contains(settings.ToGameJson())) withSettings++;
            foreach (var body in abc.Bodies)
            {
                var previous = -1;
                foreach (var start in AbcInstructions.Starts(body.Code))
                {
                    Assert(body.Code[start] != 0x86, $"{block.AbcName} still has an astype instruction");
                    Assert(!(previous >= 0 && body.Code[previous] == 0x20 && body.Code[start] == 0x80),
                        $"{block.AbcName} still has a typed null");
                    previous = start;
                }
            }
        }
        Assert(withSettings == 1, "the settings were not written into exactly one block");
    }

    private static void TestIntroMovies()
    {
        var root = Path.Combine(fixtureDirectory, "tweak-intro");
        var packages = Path.Combine(root, "client", "EFGame", "ReleasePC", "Packages");
        var movies = Path.Combine(root, "client", "EFGame", "Movies");
        Directory.CreateDirectory(packages);
        Directory.CreateDirectory(movies);
        var english = Path.Combine(movies, Arkive.Core.Tweaks.TweakIntroMovies.IntroMovie + ".English.ipk");
        var login = Path.Combine(movies, "LOGIN.English.ipk");
        File.WriteAllBytes(english, [1, 2, 3]);
        File.WriteAllBytes(login, [4]);
        var intro = new Arkive.Core.Tweaks.TweakIntroMovies(Path.Combine(root, "backups"));
        Assert(intro.Apply(packages, skip: true) == 1 && !File.Exists(english) && File.Exists(login) && intro.Skipping,
            "skipping the intro did not move only the intro movie aside");
        Assert(intro.Apply(packages, skip: true) == 0, "skipping twice moved files again");
        Assert(intro.Apply(packages, skip: false) == 1 && File.ReadAllBytes(english).SequenceEqual(new byte[] { 1, 2, 3 }) && !intro.Skipping,
            "bringing the intro back did not restore the exact file");
    }

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
        var missing = AbcHookInjector.Inject(abc, [new AbcHook("demo.Greeter", "greet", "demo.Tweak.onGreet"), new AbcHook("demo.Greeter", "wave", "demo.Tweak.onWave")]);
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
        Assert(body.Code[^1] == 0x48 && body.Exceptions.Count == 1, "the wrapper does not return the game's result");
        Assert(reread.Pool.Strings.Contains("onGreet") && reread.Pool.Strings.Contains("Tweak"),
            "the wrapper does not call the tweak's handler");
        var again = AbcHookInjector.Inject(reread, [new AbcHook("demo.Greeter", "greet", "demo.Tweak.onGreet")]);
        Assert(again.Count == 0 && reread.Instances[0].Traits.Count == 2, "hooking twice added a second wrapper");
    }

    private static void TestTwoHooksOneMethod()
    {
        var abc = BuildGreeter();
        var originalMethod = abc.Instances[0].Traits.Single().Index;
        AbcHook[] hooks = [new("demo.Greeter", "greet", "demo.One.onGreet"), new("demo.Greeter", "greet", "demo.Two.onGreet")];
        Assert(AbcHookInjector.Inject(abc, hooks).Count == 0, "a hook was reported missing");

        var reread = AbcFile.Read(abc.Write());
        var p = reread.Pool;
        var traits = reread.Instances[0].Traits;
        AbcTrait Named(string name) => traits.Single(trait => p.NameOf(trait.Name) == name);
        string Calls(AbcTrait trait) => string.Join(",", AbcInstructions.Starts(reread.BodyOf(trait.Index)!.Code)
            .Select(start => (Code: reread.BodyOf(trait.Index)!.Code, Start: start))
            .Where(item => item.Code[item.Start] is 0x46 or 0x4F or 0x60)
            .Select(item => { var at = item.Start + 1; var value = 0; var shift = 0; byte b; do { b = item.Code[at++]; value |= (b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0); return p.QualifiedName(value); }));
        Assert(traits.Count == 3 && Named("greet" + AbcHookInjector.RenamedSuffix).Index == originalMethod,
            "the original is not kept under the first name");
        Assert(Calls(Named("greet")) == "greet" + AbcHookInjector.RenamedSuffix + "2,demo.Two,onGreet",
            "the outer wrapper does not call the inner one, then the second handler: " + Calls(Named("greet")));
        Assert(Calls(Named("greet" + AbcHookInjector.RenamedSuffix + "2")) == "greet" + AbcHookInjector.RenamedSuffix + ",demo.One,onGreet",
            "the inner wrapper does not call the original, then the first handler");
        AbcHookInjector.Inject(reread, hooks);
        Assert(reread.Instances[0].Traits.Count == 3, "hooking both again added wrappers");
    }

    /// <summary>The three wrapper shapes, read back instruction by instruction (Mari's shapes A/B and C/D).</summary>
    private static void TestExpose()
    {
        var abc = BuildGreeter();
        var pool = abc.Pool;
        var secret = pool.AddQName(pool.AddNamespace(AbcNamespace.PrivateNs, "demo:Greeter"), "secret");
        abc.Instances[0].Traits.Add(new AbcTrait { Name = secret, KindByte = AbcTrait.Method, Index = 2 });
        var hook = new AbcHook("demo.Greeter", "greet", "demo.Tweak.onGreet") { Expose = ["secret", "nothing"] };
        var missing = AbcHookInjector.Inject(abc, [hook]);
        Assert(missing.SequenceEqual(["demo.Greeter.nothing"]), "a method to expose that the class doesn't have was not reported: " + string.Join(", ", missing));

        var reread = AbcFile.Read(abc.Write());
        var named = reread.Instances[0].Traits.Where(trait => reread.Pool.NameOf(trait.Name) == "secret").ToArray();
        Assert(named.Length == 2 && named.All(trait => trait.Index == 2 && trait.Kind == AbcTrait.Method),
            "the exposed trait is missing or bound to another method");
        Assert(named.Count(trait => reread.Pool.NamespaceNameOf(trait.Name) == "" &&
                                    reread.Pool.Namespaces[reread.Pool.Multinames[trait.Name].Namespace].Kind == AbcNamespace.PackageNamespace) == 1,
            "the exposed trait is not public");
        var count = reread.Instances[0].Traits.Count;
        AbcHookInjector.Inject(reread, [hook]);
        Assert(reread.Instances[0].Traits.Count == count, "exposing twice added another trait");
    }

    private static void TestExposeField()
    {
        var abc = BuildGreeter();
        var pool = abc.Pool;
        var secret = pool.AddQName(pool.AddNamespace(AbcNamespace.PrivateNs, "demo:Greeter"), "count");
        var intType = pool.AddQName(pool.AddNamespace(AbcNamespace.PackageNamespace, ""), "int");
        abc.Instances[0].Traits.Add(new AbcTrait { Name = secret, KindByte = AbcTrait.Slot, Index = intType });
        var hook = new AbcHook("demo.Greeter", "greet", "demo.Tweak.onGreet") { Expose = ["count"] };
        Assert(AbcHookInjector.Inject(abc, [hook]).Count == 0, "the field to expose was not found");

        var reread = AbcFile.Read(abc.Write());
        var p = reread.Pool;
        var traits = reread.Instances[0].Traits;
        var field = traits.Single(trait => trait.Kind == AbcTrait.Slot);
        var accessors = traits.Where(trait => p.NameOf(trait.Name) == "count" && trait.Kind is AbcTrait.Getter or AbcTrait.Setter).ToArray();
        Assert(accessors.Length == 2 && accessors.All(trait => p.Namespaces[p.Multinames[trait.Name].Namespace].Kind == AbcNamespace.PackageNamespace),
            "the field did not get a public getter and setter");
        var getter = accessors.Single(trait => trait.Kind == AbcTrait.Getter);
        var setter = accessors.Single(trait => trait.Kind == AbcTrait.Setter);
        byte[] U30(int value) => new AbcCode().U30(value).ToArray();
        Assert(reread.BodyOf(getter.Index)!.Code.SequenceEqual(new byte[] { 0xD0, 0x30, 0xD0, 0x66 }.Concat(U30(field.Name)).Append((byte)0x48)) &&
               reread.Methods[getter.Index].ReturnType == field.Index,
            "the getter does not return the private field");
        Assert(reread.BodyOf(setter.Index)!.Code.SequenceEqual(new byte[] { 0xD0, 0x30, 0xD0, 0xD1, 0x61 }.Concat(U30(field.Name)).Append((byte)0x47)) &&
               reread.Methods[setter.Index].ParameterTypes.SequenceEqual([field.Index]),
            "the setter does not write the private field");
        Assert(reread.BodyOf(getter.Index)!.InitScopeDepth == reread.BodyOf(2)!.InitScopeDepth, "the accessors do not start at the class's scope depth");
        var count = traits.Count;
        AbcHookInjector.Inject(reread, [hook]);
        Assert(reread.Instances[0].Traits.Count == count, "exposing the field twice added more traits");
    }

    private static void TestHookKinds()
    {
        string Shape(AbcHookKind kind)
        {
            var abc = BuildGreeter();
            AbcHookInjector.Inject(abc, [new AbcHook("demo.Greeter", "greet", "demo.Tweak.onGreet", Kind: kind)]);
            var reread = AbcFile.Read(abc.Write());
            var wrapper = reread.Instances[0].Traits.Single(trait => reread.Pool.NameOf(trait.Name) == "greet");
            var body = reread.BodyOf(wrapper.Index) ?? throw new InvalidOperationException("wrapper has no body");
            Assert(body.Exceptions.Count == 1 && body.LocalCount == 6, $"{kind}: the wrapper's guard or locals are wrong");
            foreach (var exception in body.Exceptions)
                Assert(exception.Target < body.Code.Length && AbcInstructions.Starts(body.Code).Contains(exception.Target),
                    $"{kind}: the guard does not land on an instruction");
            // Each call or property read as "op:name/count"; branches checked to land on instructions.
            var steps = new List<string>();
            var starts = AbcInstructions.Starts(body.Code).ToHashSet();
            foreach (var start in AbcInstructions.Starts(body.Code))
            {
                var op = body.Code[start];
                var at = start + 1;
                int U30()
                {
                    var value = 0;
                    for (var shift = 0; ; shift += 7)
                    {
                        var b = body.Code[at++];
                        value |= (b & 0x7F) << shift;
                        if ((b & 0x80) == 0) return value;
                    }
                }
                switch (op)
                {
                    case 0x46 or 0x4F:
                        var name = reread.Pool.NameOf(U30());
                        steps.Add((op == 0x46 ? "call:" : "callvoid:") + name + "/" + U30());
                        break;
                    case 0x66:
                        var multiname = U30();
                        steps.Add("get:" + (reread.Pool.Multinames[multiname].Kind == AbcMultiname.MultinameL ? "[i]" : reread.Pool.NameOf(multiname)));
                        break;
                    case 0x12:
                        var offset = body.Code[at] | (body.Code[at + 1] << 8) | ((sbyte)body.Code[at + 2] << 16);
                        Assert(starts.Contains(start + 4 + offset), $"{kind}: a branch does not land on an instruction");
                        steps.Add("iffalse");
                        break;
                    case 0x47 or 0x48:
                        steps.Add("return");
                        break;
                }
            }
            return string.Join(" ", steps);
        }

        var after = Shape(AbcHookKind.After);
        Assert(after == "call:greet$arkive/1 callvoid:onGreet/3 return", "after hook: " + after);
        var afterValue = Shape(AbcHookKind.AfterValue);
        Assert(afterValue == "call:greet$arkive/1 call:onGreet/3 iffalse get:value return", "after hook with value: " + afterValue);
        var before = Shape(AbcHookKind.Before);
        Assert(before == "call:onGreet/2 iffalse get:return iffalse get:value return get:[i] call:greet$arkive/1 return",
            "before hook: " + before);
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
