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
        new("deletes tweak working edits left by replaced tweak mods and keeps everyone else's", TestStaleTweakDrafts),
        new("reads Data Bus messages and saves tweak switches and options changed in game", TestInGameSettings),
        new("keeps the market prices Market Price sends, the newest per item, for the next session", TestMarketPrices),
        new("works out astrogem processing odds from the game's weights and writes the table the advisor reads", TestAstrogemOdds),
        new("reads a game crash dump: the exception, where, the names on the stack and the tweak code it points into", TestGameCrash),
    ];

    /// <summary>
    /// A minidump shaped like the game's: an access violation in EFEngine.dll whose stack holds a return address and a
    /// pointer to a heap object that points at a symbol name inside a tweak class's loaded bytecode.
    /// </summary>
    private static void TestGameCrash()
    {
        const ulong engine = 0x1_8000_0000, stack = 0x15_0000, heap = 0x10_0000_0000;
        var symbol = "CharInfo_Equip_AbilityPage_BasicAbilityTitleBG"u8.ToArray();
        var abc = new byte[256];
        symbol.CopyTo(abc, 100);
        var heapBytes = new byte[512];
        BitConverter.GetBytes(heap + 0x100).CopyTo(heapBytes, 16); // object -> string inside the loaded code
        abc.AsSpan(100, 120).CopyTo(heapBytes.AsSpan(0x100));
        var stackBytes = new byte[0x200];
        BitConverter.GetBytes(engine + 0x113eb02).CopyTo(stackBytes, 0x28);
        BitConverter.GetBytes(heap).CopyTo(stackBytes, 0x40);

        var file = new List<byte>();
        var body = new byte[0x1000];
        void Put(int at, params ulong[] values) { foreach (var value in values) { BitConverter.GetBytes(value).CopyTo(body, at); at += 8; } }
        "MDMP"u8.CopyTo(body);
        BitConverter.GetBytes(4u).CopyTo(body, 8);
        BitConverter.GetBytes(0x20u).CopyTo(body, 12);
        BitConverter.GetBytes(1_793_000_000u).CopyTo(body, 20);
        uint[] directory = [3, 48 + 4, 0x100, 4, 108 + 4, 0x200, 6, 168, 0x300, 9, 16 + 32, 0x500];
        for (var index = 0; index < directory.Length; index++) BitConverter.GetBytes(directory[index]).CopyTo(body, 0x20 + index * 4);
        BitConverter.GetBytes(1u).CopyTo(body, 0x100); // one thread, its stack
        Put(0x104 + 24, stack);
        BitConverter.GetBytes((uint)stackBytes.Length).CopyTo(body, 0x104 + 32);
        BitConverter.GetBytes(1u).CopyTo(body, 0x200); // one module named at 0x280
        Put(0x204, engine);
        BitConverter.GetBytes(0x200_0000u).CopyTo(body, 0x204 + 8);
        BitConverter.GetBytes(0x280u).CopyTo(body, 0x204 + 20);
        var name = System.Text.Encoding.Unicode.GetBytes(@"D:\Lost Ark\Binaries\Win64\EFEngine.dll");
        BitConverter.GetBytes((uint)name.Length).CopyTo(body, 0x280);
        name.CopyTo(body, 0x284);
        BitConverter.GetBytes(0xC0000005u).CopyTo(body, 0x308); // exception: read of 0xffffffff7c3e5439
        Put(0x308 + 16, engine + 0x1138318);
        BitConverter.GetBytes(2u).CopyTo(body, 0x308 + 24);
        Put(0x308 + 32, 0, 0xffff_ffff_7c3e_5439);
        BitConverter.GetBytes(0x4D0u).CopyTo(body, 0x300 + 160);
        BitConverter.GetBytes(0x600u).CopyTo(body, 0x300 + 164);
        Put(0x600 + 0x98, stack); // context rsp
        Put(0x500, 2, 0x1000, stack, (ulong)stackBytes.Length, heap, (ulong)heapBytes.Length);
        file.AddRange(body);
        file.AddRange(stackBytes);
        file.AddRange(heapBytes);
        var path = Path.Combine(fixtureDirectory, "game-crash.dmp");
        File.WriteAllBytes(path, file.ToArray());

        var crash = Arkive.Core.Tweaks.GameCrashReader.Read(path, [("arkive/tweaks/Compare", abc)]);
        Assert(crash.Code == 0xC0000005 && crash.Where == "EFEngine.dll+0x1138318" && crash.Access == "read" &&
               crash.BadAddress == 0xffff_ffff_7c3e_5439, $"read the exception as {crash.CodeName} at {crash.Where}");
        Assert(crash.Frames.FirstOrDefault() == "EFEngine.dll+0x113eb02", "missed the return address: " + string.Join(" ", crash.Frames));
        Assert(crash.Tweaks.SequenceEqual(["Compare"]) &&
               crash.Clues.Contains("CharInfo_Equip_AbilityPage_BasicAbilityTitleBG (Compare)"),
            "missed the tweak code on the stack: " + string.Join(", ", crash.Clues));
        Assert(Arkive.Core.Tweaks.GameCrashReader.Describe(crash).Contains("Arkive tweak code on the stack: Compare", StringComparison.Ordinal),
            "the description leaves out the tweak");
    }

    /// <summary>
    /// Market Price sends "name|lowest|avg|last|bundle|source" lines (hundredths of a gold per bundle) on the "prices"
    /// channel; Arkive keeps the newest per item in prices.txt with the time, and every sighting in history.txt.
    /// </summary>
    private static void TestMarketPrices()
    {
        var folder = Path.Combine(fixtureDirectory, "tweak-prices");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        var first = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);
        var taken = Arkive.Core.Tweaks.MarketPriceStore.Save(folder,
            "Destiny Destruction Stone|1200|1250|-1|10|market\nLv. 8 Doomfire Gem|95000000|-1|-1|1|auction\nbad line\n|1|1|1|1|market\nX|-1|-1|-1|1|market", first);
        Assert(taken == 2, $"took {taken} lines, expected the two good ones");
        Arkive.Core.Tweaks.MarketPriceStore.Save(folder, "Destiny Destruction Stone|1100|1240|1150|10|market", first.AddMinutes(5));
        var kept = Arkive.Core.Tweaks.MarketPriceStore.Load(Path.Combine(folder, Arkive.Core.Tweaks.MarketPriceStore.FileName));
        Assert(kept.Count == 2 && kept["Destiny Destruction Stone"].Lowest == 1100 && kept["Destiny Destruction Stone"].Last == 1150 &&
               kept["Destiny Destruction Stone"].SeenAt == first.AddMinutes(5).ToUnixTimeMilliseconds() &&
               kept["Lv. 8 Doomfire Gem"].Bundle == 1 && kept["Lv. 8 Doomfire Gem"].Source == "auction",
            "prices.txt does not hold the newest price per item");
        var text = File.ReadAllText(Path.Combine(folder, Arkive.Core.Tweaks.MarketPriceStore.FileName));
        Assert(text.Contains("Destiny Destruction Stone|1100|1240|1150|10|market|", StringComparison.Ordinal) && !text.Contains('\r'),
            "prices.txt is not in the line format the tweak reads: " + text);
        Assert(File.ReadAllLines(Path.Combine(folder, Arkive.Core.Tweaks.MarketPriceStore.HistoryName)).Length == 3, "history.txt missed a sighting");
        Assert(Arkive.Core.Tweaks.TweakBus.TryDecode(Arkive.Core.Tweaks.TweakBus.Encode("prices", "Oreha Fusion Material|-1|880|900|1|market"),
            out var channel, out var message) && channel == Arkive.Core.Tweaks.MarketPriceStore.Channel && message.EndsWith("|market", StringComparison.Ordinal),
            "a prices message did not survive the bus");
    }

    /// <summary>
    /// The astrogem odds with the game's own weights (group 4000) and grades: a last attempt checked against every
    /// ordered draw of four effects, more attempts and rerolls never hurting, and the encoded table reading back.
    /// </summary>
    private static void TestAstrogemOdds()
    {
        List<Arkive.Core.Tweaks.AstrogemEffect> effects = [];
        var id = 3000;
        foreach (var (type, option) in new[] { (2, 0), (3, 0), (4, 0), (4, 1) })
            foreach (var (delta, weight) in new[] { (1, 1165), (2, 440), (3, 175), (4, 45), (-1, 300) })
                effects.Add(new(++id, type, option, delta, weight));
        effects.AddRange([new(3033, 5, 0, 0, 325), new(3034, 5, 1, 0, 325), new(3036, 6, 0, 10000, 175), new(3038, 6, 0, -10000, 175),
            new(3039, 0, 0, 0, 175), new(3041, 7, 0, 1, 250), new(3042, 7, 0, 2, 75)]);
        var rules = new Arkive.Core.Tweaks.AstrogemRules(effects, [new(5, 0, 1, 900), new(7, 1, 1, 900), new(9, 2, 1, 900)], -10000, 10000);
        var target = Arkive.Core.Tweaks.AstrogemOdds.Target("w4p4");
        var table = Arkive.Core.Tweaks.AstrogemOdds.Compute(rules, target);

        // Last attempt at Willpower 3, Points 4: only Willpower +1 or +2 reaches 4+. Cost changes and extra rerolls
        // aren't offered on the last attempt, nor Willpower +3/+4 or option -1 at these levels.
        var offered = effects.Where(effect => effect.Type switch
        {
            2 => effect.ValueB is 1 or 2 or -1,
            3 => effect.ValueB is 1 or -1,
            4 => effect.ValueB > 0,
            6 or 7 => false,
            _ => true,
        }).ToArray();
        var included = new double[offered.Length];
        var worthIt = 0.0;
        void Draw(int depth, double chance, double left, List<int> taken)
        {
            if (depth == 4)
            {
                foreach (var index in taken) included[index] += chance;
                if (taken.Any(index => offered[index].Type == 2 && offered[index].ValueB > 0)) worthIt += chance;
                return;
            }
            for (var i = 0; i < offered.Length; i++)
            {
                if (taken.Contains(i)) continue;
                taken.Add(i);
                Draw(depth + 1, chance * offered[i].Weight / left, left - offered[i].Weight, taken);
                taken.RemoveAt(taken.Count - 1);
            }
        }
        Draw(0, 1, offered.Sum(effect => effect.Weight), []);
        var expected = Enumerable.Range(0, offered.Length)
            .Where(i => offered[i].Type == 2 && offered[i].ValueB > 0).Sum(i => included[i]) / 4;
        var index = table.Index(1, 0, 1, 3, 4, 1, 1);
        Assert(Math.Abs(table.NoReset.Chance[index] - expected) < 1e-9,
            $"last attempt chance {table.NoReset.Chance[index]:0.000000}, every draw gives {expected:0.000000}");
        // Processing is only worth its gold when the offer holds a way to the target; otherwise the best play is to stop.
        Assert(Math.Abs(table.NoReset.Gold[index] - worthIt) < 1e-9, $"last attempt gold {table.NoReset.Gold[index]:0.000000}, expected {worthIt:0.000000}");

        var fresh = table.Grades.Select(grade => grade.FreshChance).ToArray();
        Assert(fresh[0] is > 0.03 and < 0.06 && fresh[0] < fresh[1] && fresh[1] < fresh[2],
            $"fresh chances by grade: {string.Join(", ", fresh.Select(chance => chance.ToString("0.0000")))}");
        var later = table.Index(3, 0, 1, 2, 2, 1, 1);
        var withRerolls = table.Index(3, 2, 1, 2, 2, 1, 1);
        Assert(table.NoReset.Chance[withRerolls] >= table.NoReset.Chance[later] - 1e-12, "rerolls lowered the chance");
        Assert(table.Grades[2].WithReset!.Chance[later] >= table.NoReset.Chance[later] - 1e-12, "a reset in hand lowered the chance");
        Assert(table.NoReset.Chance[table.Index(2, 0, 1, 4, 4, 1, 1)] == 1, "a reached target isn't certain");

        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_";
        var lines = Arkive.Core.Tweaks.AstrogemOdds.Encode(table).Split('\n');
        Assert(lines[0] == "arkive-astrogem 1" && lines[1] == "target w4p4 4 4 0" && lines[2] == "rules 5 4 3 -1 10000",
            $"table header: {string.Join(" | ", lines.Take(3))}");
        var none = lines.Single(line => line.StartsWith("table none ", StringComparison.Ordinal))["table none ".Length..];
        int Pair(int at) => digits.IndexOf(none[at]) * 64 + digits.IndexOf(none[at + 1]);
        Assert(Math.Abs(Pair(index * 4) / 4095.0 - table.NoReset.Chance[index]) <= 0.5 / 4095 &&
               Math.Abs(Pair(index * 4 + 2) / 200.0 - table.NoReset.Gold[index]) <= 0.5 / 200, "the encoded table reads back wrong");
    }

    /// <summary>
    /// The escape menu panel spells "settings" messages on the Data Bus; Arkive saves them to the tweak state (a tweak
    /// switched off keeps its options) and to live.json, which the game follows. Bad messages change nothing.
    /// </summary>
    private static void TestInGameSettings()
    {
        var spelled = Arkive.Core.Tweaks.TweakBus.Encode("settings", "0a1f\tlobbycp\t\t0 é Ω");
        Assert(Arkive.Core.Tweaks.TweakBus.TryDecode(spelled, out var channel, out var text) && channel == "settings" &&
               text == "0a1f\tlobbycp\t\t0 é Ω", $"the bus message did not decode: {spelled}");
        Assert(!Arkive.Core.Tweaks.TweakBus.TryDecode(spelled.Remove(6, 2), out _, out _), "a bus message with a missed read was accepted");
        var bus = new Arkive.Core.Tweaks.TweakBus(Path.GetTempPath());
        var heard = "";
        bus.Received += (_, message) => heard = message;
        foreach (var symbol in (string[])["go", .. Arkive.Core.Tweaks.TweakBus.Encode("x", "hi").Select(digit => digit.ToString()), "end"])
            bus.Receive(symbol);
        Assert(heard == "hi", "the bus reads between go and end were not put together");

        // With In-game Tweak Settings on, every interface tweak is built (the unticked ones start off); without it, only the ticked ones.
        var choices = new Arkive.Core.Tweaks.TweakSettings();
        choices.Enabled["lobbycp"] = [];
        Assert(Arkive.Core.Tweaks.TweakModService.BuiltTweaks(choices).Select(tweak => tweak.Id).SequenceEqual(["lobbycp"]),
            "a build without In-game Tweak Settings should hook only the ticked tweaks");
        choices.Enabled[Arkive.Core.Tweaks.TweakModService.SettingsTweak] = [];
        var everything = Arkive.Core.Tweaks.TweakModService.BuiltTweaks(choices);
        Assert(everything.Count == Arkive.Core.Tweaks.TweakCatalog.All.Count(tweak => tweak.Available && !tweak.RestartOnly) &&
               everything.All(tweak => tweak.Id != "fastlaunch"),
            "In-game Tweak Settings should build every interface tweak and no game-file one");

        var root = Path.Combine(fixtureDirectory, "tweak-settings");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var library = new ArkiveModLibrary(Path.Combine(root, "mods"), Path.Combine(root, "staging"));
        var service = new Arkive.Core.Tweaks.TweakModService(library, Path.Combine(root, "tweaks"), NoChunkProtectionProvider.Instance);
        var state = new Arkive.Core.Tweaks.TweakState();
        state.Settings.Enabled["extracurrency"] = new() { ["showLeapstones"] = System.Text.Json.JsonSerializer.SerializeToElement(false) };
        service.SaveState(state);
        Assert(service.ApplyInGameChange("0001\textracurrency\t\t0") is not null, "switching a tweak off was refused");
        state = service.LoadState();
        Assert(!state.Settings.Enabled.ContainsKey("extracurrency") && state.Kept["extracurrency"]["showLeapstones"].GetBoolean() == false,
            "a tweak switched off in game did not keep its options");
        Assert(service.ApplyInGameChange("0002\tboxtweaks\tconfirm\ttick") is not null &&
               service.ApplyInGameChange("0003\textracurrency\t\t1") is not null, "a choice or switching back on was refused");
        state = service.LoadState();
        Assert(state.Settings.Enabled["extracurrency"]["showLeapstones"].GetBoolean() == false && state.Kept.Count == 1 &&
               state.Kept["boxtweaks"]["confirm"].GetString() == "tick", "switching back on lost the options, or a choice was not kept");
        Assert(service.ApplyInGameChange("0004\tboxtweaks\tconfirm\tnonsense") is null &&
               service.ApplyInGameChange("0005\tfastlaunch\t\t1") is null && service.ApplyInGameChange("0006\tnope\t\t1") is null,
            "a bad choice, a game-file tweak or an unknown tweak was accepted");
        var live = File.ReadAllText(service.LivePath);
        using var parsed = System.Text.Json.JsonDocument.Parse(live);
        Assert(parsed.RootElement.GetProperty("ack").GetString() == "0003" &&
               parsed.RootElement.GetProperty("on").TryGetProperty("extracurrency", out _) &&
               !parsed.RootElement.GetProperty("on").TryGetProperty("boxtweaks", out _) &&
               parsed.RootElement.GetProperty("tweaks").GetProperty("boxtweaks").GetProperty("confirm").GetString() == "tick" &&
               !live.Contains("\\u", StringComparison.Ordinal),
            "live.json does not hold the switches, options and answered request, or has \\u escapes: " + live);

        // A pick-list message ("+" or "-" a choice) is refused for an option that is not a pick-list.
        Assert(service.ApplyInGameChange("0007\tboxtweaks\tconfirm\t-tick") is null, "a pick-list change to a single choice was accepted");

        // Choices saved for a tweak Arkive no longer has are dropped when the state is read, and left out of live.json.
        state = service.LoadState();
        state.Settings.Enabled["retired"] = new() { ["a"] = System.Text.Json.JsonSerializer.SerializeToElement(true) };
        state.Kept["retired2"] = [];
        service.SaveState(state);
        state = service.LoadState();
        Assert(!state.Settings.Enabled.ContainsKey("retired") && !state.Kept.ContainsKey("retired2") && state.Settings.Enabled.ContainsKey("extracurrency"),
            "a removed tweak's saved choices were kept, or a real one was dropped");
        service.SaveState(state);
        Assert(!File.ReadAllText(service.LivePath).Contains("retired", StringComparison.Ordinal), "live.json lists a removed tweak");

        // Install All Tweaks builds every tweak but switches on only the ones the player has on, with their options; a first
        // install has them all off. Remove All Tweaks keeps the options and which were on, for the next install.
        var all = service.EveryTweak();
        Assert(all.Enabled.Keys.Order().SequenceEqual(["extracurrency", Arkive.Core.Tweaks.TweakModService.SettingsTweak]) &&
               all.Enabled[Arkive.Core.Tweaks.TweakModService.SettingsTweak]["everything"].GetBoolean() &&
               all.Enabled["extracurrency"]["showLeapstones"].GetBoolean() == false,
            "Install All Tweaks should keep the player's switches and options: " + string.Join(", ", all.Enabled.Keys));
        var fresh = new Arkive.Core.Tweaks.TweakModService(library, Path.Combine(root, "fresh"), NoChunkProtectionProvider.Instance);
        Assert(fresh.EveryTweak().Enabled.Keys.SequenceEqual([Arkive.Core.Tweaks.TweakModService.SettingsTweak]),
            "a first Install All Tweaks should start with every tweak off");
        service.BuildAsync(new Arkive.Core.Tweaks.TweakSettings(), root, []).GetAwaiter().GetResult();
        service.BuildAsync(new Arkive.Core.Tweaks.TweakSettings(), root, []).GetAwaiter().GetResult();
        state = service.LoadState();
        Assert(state.Settings.Enabled.Count == 0 && state.Kept["extracurrency"]["showLeapstones"].GetBoolean() == false &&
               service.EveryTweak().Enabled.ContainsKey("extracurrency"),
            "Remove All Tweaks lost the options of the tweaks it took out, or which of them were on");

        // Faster Launch is installed on its own: one saved by an older build is dropped, and Install All never includes it.
        state = service.LoadState();
        state.Settings.Enabled["fastlaunch"] = [];
        state.OnBeforeRemove.Add("fastlaunch");
        service.SaveState(state);
        Assert(!service.LoadState().Settings.Enabled.ContainsKey("fastlaunch") && !service.EveryTweak().Enabled.ContainsKey("fastlaunch"),
            "Faster Launch was kept in the saved choices or put into Install All Tweaks");
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
        // Text like a bracelet effect's: the default JSON writer would turn "+", "'" and "<" into \u escapes.
        settings.Enabled["cpgems"] = new() { ["bracelet"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "crit+5%.can't<x>|450|0" }) };
        Assert(!settings.ToGameJson().Contains("\\u", StringComparison.Ordinal),
            $"the tweak settings have \\u escapes, which the game's JSON reading can't handle: {settings.ToGameJson()}");
        var blocks = Arkive.Core.Tweaks.TweakCode.ForSettings(Arkive.Core.Tweaks.TweakCode.LoadSwf(), settings);
        Assert(blocks.Count > 0 && blocks.All(block => block.AbcName.StartsWith(Arkive.Core.Tweaks.TweakCode.AbcName + "/")),
            "the tweak code blocks are missing or unnamed");
        var withSettings = 0;
        foreach (var block in blocks)
        {
            var abc = AbcFile.Read(block.AbcData);
            if (abc.Pool.Strings.Contains(settings.ToGameJson())) withSettings++;
            // Haxe's Boot adds String.charCodeAtHX (StringTools.trim and isSpace call it); Arkive's empty Boot doesn't.
            Assert(!abc.Pool.Strings.Contains("charCodeAtHX"),
                $"{block.AbcName} calls String.charCodeAtHX, which the game's player doesn't have (use Text.trim)");
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
        string Shape(AbcHookKind kind, string? gate = null, bool setup = false)
        {
            var abc = BuildGreeter();
            AbcHookInjector.Inject(abc, [new AbcHook("demo.Greeter", "greet", "demo.Tweak.onGreet", Kind: kind) { Gate = gate, Setup = setup }]);
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
        // A tweak switched off in game: Options.on(id) is asked first, and the handler is skipped when it says no.
        var gated = Shape(AbcHookKind.Before, "lobbycp");
        Assert(gated == "call:on/1 iffalse call:onGreet/2 iffalse get:return iffalse get:value return get:[i] call:greet$arkive/1 return",
            "gated before hook: " + gated);
        var gatedAfter = Shape(AbcHookKind.AfterValue, "lobbycp");
        Assert(gatedAfter == "call:greet$arkive/1 call:on/1 iffalse call:onGreet/3 iffalse get:value return", "gated after hook: " + gatedAfter);
        // A setup hook (configUI) of a tweak that is off hands its call to Options.defer, to replay when switched on.
        var setup = Shape(AbcHookKind.After, "lobbycp", setup: true);
        Assert(setup == "call:greet$arkive/1 call:on/1 iffalse callvoid:onGreet/3 get:onGreet callvoid:defer/5 return", "setup hook: " + setup);
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
