using Arkive.Core.Fonts;
using Arkive.Core.Skills;

namespace Arkive.SmokeTests;

internal static class FontSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("rewrites a data archive with entries replaced and added, copying the others byte for byte", TestArchiveRewrite),
        new("puts chosen fonts in font.lpk for body text, titles and damage numbers and restores it byte for byte", TestFonts),
        new("refuses fonts the game cannot read and a font.lpk changed by another tool", TestFontRefusals),
    ];

    private sealed class NoGame : IModDeploymentProcessGuard
    {
        public IReadOnlyList<string> FindBlockingProcesses() => [];
    }

    private const string WesternMap = """
        <?xml version="1.0" encoding="utf-8"?>
        <FontMap>
            <Item Key="$YG760" File="ExuberanceF-Regular.ttf" />
            <Item Key="$YG760_12pt" File="ExuberanceF-Regular.ttf" SizeCorrectionRatio="120" />
            <Item Key="$YG330" File="ExuberanceF-Regular.ttf" />
            <Item Key="$YoonGasiIIM" File="MonarchaF-Regular.ttf" LeadingCorrection="-200" />
        	<Item Key="$YoonGasiIIM_Small2pt" File="MonarchaF-Regular.ttf" LeadingCorrection="-200" SizeCorrectionRatio="120" />
        	<Item Key="$ChronologyFont" File="MonarchaF-Regular.ttf" LeadingCorrection="-200" />
        	<Item Key="$eventDamageFont" File="MonarchaF-Regular.ttf" />
        </FontMap>
        """;

    private static byte[] Font(int seed, int length = 3000)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        new byte[] { 0, 1, 0, 0 }.CopyTo(data, 0);
        return data;
    }

    private static Dictionary<string, byte[]> Contents(string archive)
    {
        using var stream = File.OpenRead(archive);
        return LostArkDataArchive.ReadEntries(stream).ToDictionary(entry => entry.Path, entry => LostArkDataArchive.Read(stream, entry));
    }

    // The game's font.lpk in small: Korean fonts at the root, English and French folders, and Russian with its own font.
    private static void WriteGameFonts(string path, byte[]? extra = null, int koreanSeed = 1)
    {
        var map = Encoding.UTF8.GetBytes(WesternMap);
        var files = new List<KeyValuePair<string, byte[]>>
        {
            new("/Binaries/Fonts/BMKkubulimTTF.ttf", Font(koreanSeed)),
            new("/Binaries/Fonts/English/ExuberanceF-Regular.ttf", Font(2)),
            new("/Binaries/Fonts/English/FontMap.xml", [.. "﻿"u8, .. map]),
            new("/Binaries/Fonts/English/MonarchaF-Regular.ttf", Font(3)),
            new("/Binaries/Fonts/FontMap.xml", "<FontMap />"u8.ToArray()),
            new("/Binaries/Fonts/French/ExuberanceF-Regular.ttf", Font(2)),
            new("/Binaries/Fonts/French/FontMap.xml", map),
            new("/Binaries/Fonts/French/MonarchaF-Regular.ttf", Font(3)),
            new("/Binaries/Fonts/Russian/FontMap.xml", "<FontMap><Item Key=\"$YG760\" File=\"FrizQuadrataC.ttf\" /></FontMap>"u8.ToArray()),
            new("/Binaries/Fonts/Russian/FrizQuadrataC.ttf", Font(4)),
        };
        if (extra is not null) files.Add(new("/Binaries/Fonts/English/1_1_eranceF-Regular.ttf", extra));
        LostArkDataArchive.Create(files, path);
    }

    private static void TestArchiveRewrite()
    {
        var root = Path.Combine(fixtureDirectory, "data-archive-rewrite");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.lpk");
        LostArkDataArchive.Create([new("/a/one.txt", Font(10, 500)), new("/a/three.txt", Font(11, 9000)), new("/b/four.txt", new byte[64])], source);
        var output = Path.Combine(root, "output.lpk");
        var replaced = Font(12, 4000);
        LostArkDataArchive.Rewrite(source, new Dictionary<string, byte[]>
        {
            ["\\a\\THREE.txt"] = replaced,
            ["/a/two.txt"] = Font(13, 100),
            ["/c/five.txt"] = Font(14, 7),
        }, output);

        using var before = File.OpenRead(source);
        using var after = File.OpenRead(output);
        var old = LostArkDataArchive.ReadEntries(before);
        var entries = LostArkDataArchive.ReadEntries(after);
        Assert(entries.Select(entry => entry.Path).SequenceEqual(["/a/one.txt", "/a/three.txt", "/a/two.txt", "/b/four.txt", "/c/five.txt"]),
            "the rewritten archive's entries are not the old ones with the new ones in name order: " + string.Join(", ", entries.Select(entry => entry.Path)));
        Assert(LostArkDataArchive.Read(after, entries[1]).SequenceEqual(replaced) && LostArkDataArchive.Read(after, entries[2]).SequenceEqual(Font(13, 100)) &&
               LostArkDataArchive.Read(after, entries[4]).SequenceEqual(Font(14, 7)), "the replaced or added entries do not read back");
        foreach (var (was, now) in new[] { (old[0], entries[0]), (old[2], entries[3]) })
        {
            var a = new byte[was.EncryptedSize];
            var b = new byte[now.EncryptedSize];
            before.Position = was.Offset;
            before.ReadExactly(a);
            after.Position = now.Offset;
            after.ReadExactly(b);
            Assert(a.SequenceEqual(b) && was.Size == now.Size && was.CompressedSize == now.CompressedSize, $"{was.Path} was not copied byte for byte");
        }
        Assert(entries.All(entry => entry.EncryptedSize == (entry.CompressedSize / 8 + 1) * 8),
            "an entry is not padded to the next whole 8-byte block the way the game stores them");
        Assert(after.Length == entries[^1].Offset + entries[^1].EncryptedSize, "the archive has bytes after its last entry");
    }

    private static void TestFonts()
    {
        var root = Path.Combine(fixtureDirectory, "game-fonts");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var efGame = Path.Combine(root, "EFGame");
        Directory.CreateDirectory(efGame);
        var archive = Path.Combine(efGame, "font.lpk");
        WriteGameFonts(archive);
        var clean = File.ReadAllBytes(archive);
        var chosen = Path.Combine(root, "chosen");
        Directory.CreateDirectory(chosen);
        var body = Path.Combine(chosen, "Body.ttf");
        var titles = Path.Combine(chosen, "Titles.ttf");
        var damage = Path.Combine(chosen, "Damage.ttf");
        File.WriteAllBytes(body, Font(20));
        File.WriteAllBytes(titles, Font(21));
        File.WriteAllBytes(damage, Font(22));

        var service = new GameFontService(efGame, Path.Combine(root, "data"), new NoGame());
        var own = service.GameFontsAsync().GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(own[GameFontSlot.BodyText]).SequenceEqual(Font(2)) && File.ReadAllBytes(own[GameFontSlot.Titles]).SequenceEqual(Font(3)) &&
               File.ReadAllBytes(own[GameFontSlot.DamageNumbers]).SequenceEqual(Font(3)), "the game's own fonts were not saved for previews");

        // Body text and titles only: the damage numbers follow the titles, as the game's own FontMap says.
        service.ApplyAsync(new Dictionary<GameFontSlot, string> { [GameFontSlot.BodyText] = body, [GameFontSlot.Titles] = titles }).GetAwaiter().GetResult();
        var files = Contents(archive);
        File.WriteAllBytes(Path.Combine(root, "clean.lpk"), clean);
        var game = Contents(Path.Combine(root, "clean.lpk"));
        foreach (var language in new[] { "English", "French" })
            Assert(files[$"/Binaries/Fonts/{language}/ExuberanceF-Regular.ttf"].SequenceEqual(Font(20)) &&
                   files[$"/Binaries/Fonts/{language}/MonarchaF-Regular.ttf"].SequenceEqual(Font(21)) &&
                   files[$"/Binaries/Fonts/{language}/FontMap.xml"].SequenceEqual(game[$"/Binaries/Fonts/{language}/FontMap.xml"]),
                $"the {language} body text and titles fonts were not replaced under the game's file names");
        Assert(files.Count == game.Count && files["/Binaries/Fonts/Russian/FrizQuadrataC.ttf"].SequenceEqual(Font(4)) &&
               files["/Binaries/Fonts/BMKkubulimTTF.ttf"].SequenceEqual(Font(1)), "fonts outside the Western languages were changed");
        Assert(service.LoadState() is { Fonts.Count: 2 } state && state.Fonts[0] is { Slot: GameFontSlot.BodyText, Name: "Body.ttf" } &&
               File.Exists(state.Fonts[0].StoredPath) && !service.GameUpdatedAsync().GetAwaiter().GetResult(),
            "the change was not recorded with Arkive's copies of the chosen fonts");

        // Applying again starts from the game's copy: damage numbers get their own file, and nothing stacks.
        File.Delete(body);
        service.ApplyAsync(new Dictionary<GameFontSlot, string>
        {
            [GameFontSlot.BodyText] = service.LoadState()!.Fonts[0].StoredPath,
            [GameFontSlot.DamageNumbers] = damage,
        }).GetAwaiter().GetResult();
        files = Contents(archive);
        var englishMap = files["/Binaries/Fonts/English/FontMap.xml"];
        var expectedMap = WesternMap.Replace("<Item Key=\"$eventDamageFont\" File=\"MonarchaF-Regular.ttf\"", "<Item Key=\"$eventDamageFont\" File=\"ArkiveDamageNumbers.ttf\"");
        Assert(files.Count == game.Count + 2 && files["/Binaries/Fonts/English/ArkiveDamageNumbers.ttf"].SequenceEqual(Font(22)) &&
               files["/Binaries/Fonts/French/ArkiveDamageNumbers.ttf"].SequenceEqual(Font(22)) &&
               files["/Binaries/Fonts/English/MonarchaF-Regular.ttf"].SequenceEqual(Font(3)) &&
               files["/Binaries/Fonts/English/ExuberanceF-Regular.ttf"].SequenceEqual(Font(20)),
            "applying again stacked on the last change, or damage numbers did not get their own font file");
        Assert(englishMap.AsSpan(0, 3).SequenceEqual("﻿"u8) && Encoding.UTF8.GetString(englishMap, 3, englishMap.Length - 3) == expectedMap &&
               Encoding.UTF8.GetString(files["/Binaries/Fonts/French/FontMap.xml"]) == expectedMap,
            "the FontMap changed more than the damage number font's file:\n" + Encoding.UTF8.GetString(englishMap));
        Assert(service.LoadState()!.Fonts.Select(font => font.Name).SequenceEqual(["Body.ttf", "Damage.ttf"]),
            "applying Arkive's own copy of a font lost the name it was chosen by");

        service.RestoreAsync().GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(archive).SequenceEqual(clean) && service.LoadState() is null, "going back did not put font.lpk back byte for byte");

        // A game update replaces font.lpk: Arkive notices, applies the same fonts to the new file, and going back leaves it alone.
        service.ApplyAsync(new Dictionary<GameFontSlot, string> { [GameFontSlot.Titles] = titles }).GetAwaiter().GetResult();
        WriteGameFonts(archive + ".patch", koreanSeed: 6);
        File.Move(archive + ".patch", archive, overwrite: true);
        var patched = File.ReadAllBytes(archive);
        Assert(service.GameUpdatedAsync().GetAwaiter().GetResult(), "a game update over Arkive's font.lpk was not noticed");
        service.ReapplyAsync().GetAwaiter().GetResult();
        Assert(Contents(archive)["/Binaries/Fonts/English/MonarchaF-Regular.ttf"].SequenceEqual(Font(21)) &&
               !service.GameUpdatedAsync().GetAwaiter().GetResult(), "applying again after an update did not put the chosen fonts back");
        File.WriteAllBytes(archive, patched);
        service.RestoreAsync().GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(archive).SequenceEqual(patched) && service.LoadState() is null, "going back after a game update replaced the updated font.lpk");
        Assert(Directory.GetFiles(Path.Combine(root, "data", "backups", "fonts")).Length == 1, "copies of font.lpk from before the update were kept");
    }

    private static void TestFontRefusals()
    {
        var root = Path.Combine(fixtureDirectory, "game-font-refusals");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        var font = Path.Combine(root, "font.ttf");
        File.WriteAllBytes(font, Font(30));
        Assert(GameFontService.Problem(font) is null, "a TrueType font was refused");
        foreach (var (head, expected) in new[] { ("OTTO", ".otf"), ("ttcf", ".ttc"), ("PK\u0003\u0004", "not a TrueType") })
        {
            var path = Path.Combine(root, "bad.ttf");
            File.WriteAllBytes(path, [.. Encoding.ASCII.GetBytes(head), .. new byte[100]]);
            Assert(GameFontService.Problem(path)?.Contains(expected, StringComparison.Ordinal) == true, $"a {head} file was not refused as {expected}");
        }
        Assert(GameFontService.Problem(Path.Combine(root, "missing.ttf")) is not null, "a missing font was not refused");

        var efGame = Path.Combine(root, "EFGame");
        Directory.CreateDirectory(efGame);
        WriteGameFonts(Path.Combine(efGame, "font.lpk"), extra: Font(5));
        var changed = File.ReadAllBytes(Path.Combine(efGame, "font.lpk"));
        var service = new GameFontService(efGame, Path.Combine(root, "data"), new NoGame());
        Assert(service.ForeignChangeAsync().GetAwaiter().GetResult()?.Contains("another font tool", StringComparison.Ordinal) == true,
            "a font.lpk changed by FontChanger was not recognised");
        try
        {
            service.ApplyAsync(new Dictionary<GameFontSlot, string> { [GameFontSlot.BodyText] = font }).GetAwaiter().GetResult();
            Assert(false, "fonts were applied over another tool's font.lpk");
        }
        catch (InvalidDataException)
        {
        }
        Assert(File.ReadAllBytes(Path.Combine(efGame, "font.lpk")).SequenceEqual(changed) && service.LoadState() is null,
            "a refused apply changed font.lpk or recorded a change");
    }
}
