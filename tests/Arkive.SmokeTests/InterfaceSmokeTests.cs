using Arkive.Core.Interface;
using Arkive.Core.Textures;
using static Arkive.SmokeTests.ArkiveModTestFixture;

namespace Arkive.SmokeTests;

internal static class InterfaceSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("reads interface movies and names their pictures after the parts that draw them", TestMovieNames),
        new("finds, replaces and inflates the movie inside an interface movie export", TestMovieExportLayout),
        new("edits an interface movie into an .arkive mod, installs it and restores the clean package", TestMovieModEndToEnd),
        new("recolours one named part of a picture and keeps the rest, its transparency and its shading", TestRecolourPart),
        new("recolours only a picture's gold, or only its light areas, and keeps every other colour", TestRecolourSelection),
    ];

    private static void TestMovieNames()
    {
        foreach (var compressed in new[] { false, true })
        {
            var movie = GfxMovieReader.Read(BuildMovie("QuickSlot", compressed));
            var textures = InterfaceElementNames.Find(movie).ToDictionary(item => item.TextureName);
            var atlas = textures["quickslot_i1"].Elements.Select(element => element.Name).ToArray();
            Assert(movie.Name == "QuickSlot" && InterfaceElementNames.ScreenName(movie, "quickslot") == "Quick slot",
                "the movie's own name was not read for its screen name");
            Assert(atlas.SequenceEqual(["Castbar fill HP (health bar)", "HP gauge (health bar)", "Quick slot player HP (health bar)"]),
                "the HP bar's packed picture was not named after the sprites around it: " + string.Join(" | ", atlas));
            Assert(textures["quickslot_i1"].Elements.All(element => element.Region == new InterfaceRegion(0, 0, 0.125, 0.03125)),
                "the HP bar's rectangle on its atlas was not recorded as a fraction of the atlas");
            Assert(textures["quickslot_i1_nopack"].Elements.Single().Name == "Glow",
                "a standalone picture was not named after its export name, or took the atlas's names");
        }
        Assert(InterfaceElementNames.Humanize("QuickSlot_QuickSlotHPProgress") == "Quick slot HP progress" &&
               InterfaceElementNames.Humanize("instance12") is null && InterfaceElementNames.Humanize("mc_01") is null,
            "identifier names were not made readable");
    }

    private static void TestMovieExportLayout()
    {
        var movie = BuildMovie("QuickSlot", compressed: false);
        var export = BuildExport(movie);
        var location = GfxMovieFile.Locate(export) ?? throw new InvalidOperationException("movie not found");
        Assert(location.DataLength == movie.Length && GfxMovieFile.Extract(export).SequenceEqual(movie),
            "the movie was not found at its RawData bytes");
        var bigger = BuildMovie("QuickSlotWithALongerName", compressed: true);
        var replaced = GfxMovieFile.Replace(export, bigger);
        var again = GfxMovieFile.Locate(replaced)!;
        Assert(replaced.Length == export.Length - movie.Length + bigger.Length &&
               GfxMovieFile.Extract(replaced).SequenceEqual(bigger) &&
               replaced.AsSpan(0, location.PropertySizeOffset).SequenceEqual(export.AsSpan(0, location.PropertySizeOffset)) &&
               replaced.AsSpan(again.DataOffset + again.DataLength).SequenceEqual(export.AsSpan(location.DataOffset + location.DataLength)) &&
               BinaryPrimitives.ReadInt32LittleEndian(replaced.AsSpan(again.PropertySizeOffset)) == bigger.Length + 4,
            "replacing the movie changed bytes outside RawData or left stale length fields");
        Assert(GfxMovieFile.Inflate(bigger).AsSpan(3).SequenceEqual(BuildMovie("QuickSlotWithALongerName", compressed: false).AsSpan(3)),
            "a compressed movie did not inflate to the uncompressed one");
        try
        {
            GfxMovieFile.Replace(export, Encoding.ASCII.GetBytes("FWS-not-a-gfx-movie"));
            throw new InvalidOperationException("a non-Scaleform file replaced the movie");
        }
        catch (InvalidDataException)
        {
        }
    }

    private static void TestMovieModEndToEnd()
    {
        var root = Path.Combine(fixtureDirectory, "interface-movie");
        var packagePath = Path.Combine(root, "client", "EFGame", "ReleasePC", "Packages", "efui_test.upk");
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        var original = BuildMovie("QuickSlot", compressed: false);
        WriteInterfaceMoviePackage(packagePath, BuildExport(original));
        var cleanHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));

        var author = CreateEnvironment(Path.Combine(root, "author"));
        var local = CreateMaterializer(author).CreateEditableAsync(
            new ArkiveModAssetSelection(packagePath, "efui_test", "EFSwfMovie", "TestMovie")).GetAwaiter().GetResult();
        var movie = (InterfaceMovieEditEntry)local.Entry;
        Assert(local.Kind == UnifiedPackageAssetKind.InterfaceMovie &&
               File.ReadAllBytes(movie.Manifest.EditedMoviePath).SequenceEqual(original) &&
               InterfaceMovieEditWorkspaceStore.IsUnchanged(movie),
            "a new interface movie working copy did not start as the game's movie");
        var edited = BuildMovie("QuickSlotEditedByJpexs", compressed: true);
        File.WriteAllBytes(movie.Manifest.EditedMoviePath, edited);
        local = ArkiveModEditSnapshot.LoadAsync(author.Edits).GetAwaiter().GetResult().Find(local.EditId)!;
        var captured = ArkiveModAuthoringService.CaptureAsync(local).GetAwaiter().GetResult();
        var created = new ArkiveModAuthoringService(author.Library, author.Edits).CreateModAsync(
                new ArkiveModMetadata("Bigger HP bar", "1.0.0", "Smoke author"), [captured], ArkiveModOrigin.Created, enabled: true)
            .GetAwaiter().GetResult();
        var arkive = author.Library.GetModFilePath(created);
        var file = ArkiveModFileService.ReadAsync(arkive).GetAwaiter().GetResult();
        Assert(file.Manifest.Assets is [{ Kind: UnifiedPackageAssetKind.InterfaceMovie, Files: [{ Role: "movie" }] }],
            "the .arkive file does not hold the edited movie as an interface movie asset");

        var player = CreateEnvironment(Path.Combine(root, "player"));
        var mod = player.Library.ImportAsync(arkive).GetAwaiter().GetResult().Entry;
        player.Library.SetEnabledAsync(mod.ModId, true).GetAwaiter().GetResult();
        var service = CreateApplyService(player, Path.GetDirectoryName(packagePath)!);
        var plan = service.PlanAsync().GetAwaiter().GetResult();
        var applied = service.ApplyAsync(plan.Fingerprint).GetAwaiter().GetResult();
        Assert(applied.Succeeded && applied.InstalledPackageCount == 1 && ReadInstalledMovie(packagePath).SequenceEqual(edited),
            "applying the mod did not install the edited movie: " + string.Join(" | ", applied.Packages.Select(item => item.Message)));
        Assert(!service.PlanAsync().GetAwaiter().GetResult().HasChanges,
            "an applied interface movie mod still reported pending changes");

        player.Library.SetEnabledAsync(mod.ModId, false).GetAwaiter().GetResult();
        var restore = service.PlanAsync().GetAwaiter().GetResult();
        var restored = service.ApplyAsync(restore.Fingerprint).GetAwaiter().GetResult();
        Assert(restored.Succeeded && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))) == cleanHash,
            "disabling the interface movie mod did not restore the exact clean package");
    }

    private static void TestRecolourPart()
    {
        var image = new RgbaImage(4, 4, new byte[64]);
        for (var index = 0; index < 64; index += 4)
        {
            image.Pixels[index] = (byte)(200 - index);
            image.Pixels[index + 1] = 20;
            image.Pixels[index + 2] = 10;
            image.Pixels[index + 3] = (byte)(index * 4);
        }
        var before = image.DeepCopy();
        // The part is the second column; recolouring it also takes the one-pixel atlas border, the first and third columns.
        new TextureRecolour(0, 0, 1).Apply(image, new InterfaceRegion(0.25, 0, 0.25, 1));
        bool Column(int x) => Enumerable.Range(0, 4).All(y =>
        {
            var offset = (y * 4 + x) * 4;
            return image.Pixels[offset] == 0 && image.Pixels[offset + 1] == 0 &&
                   image.Pixels[offset + 2] == before.Pixels[offset] && image.Pixels[offset + 3] == before.Pixels[offset + 3];
        });
        Assert(Column(0) && Column(1) && Column(2) &&
               Enumerable.Range(0, 4).All(y => image.Pixels.AsSpan((y * 4 + 3) * 4, 4).SequenceEqual(before.Pixels.AsSpan((y * 4 + 3) * 4, 4))),
            "recolouring a part did not keep each pixel's brightness and transparency, or changed pixels outside it");
        var unchanged = before.DeepCopy();
        new TextureRecolour(1, 0, 0, Strength: 0).Apply(unchanged);
        Assert(unchanged.Pixels.SequenceEqual(before.Pixels), "a colour at zero strength changed the picture");
        Assert(!new TextureRecolour(0, 0, 0).IsValid && !new TextureRecolour(1, 1, 1, Strength: 2).IsValid,
            "a colour with no hue or an out-of-range strength was accepted");
    }

    private static void TestRecolourSelection()
    {
        // Gold, grey stone, white, blue and a dark gold shadow.
        byte[][] colours = [[200, 160, 60], [120, 120, 120], [240, 238, 232], [40, 80, 200], [60, 48, 18]];
        RgbaImage Picture()
        {
            var image = new RgbaImage(colours.Length, 1, new byte[colours.Length * 4]);
            for (var index = 0; index < colours.Length; index++)
            {
                colours[index].CopyTo(image.Pixels, index * 4);
                image.Pixels[index * 4 + 3] = 255;
            }
            return image;
        }
        byte[] Pixel(RgbaImage image, int index) => image.Pixels.AsSpan(index * 4, 3).ToArray();

        var gold = Picture();
        new TextureRecolour(1, 0, 0) { Selection = ColourSelection.ColourRange, RangeHue = 43, RangeWidth = 20 }.Apply(gold);
        Assert(Pixel(gold, 0).SequenceEqual(new byte[] { 200, 0, 0 }) && Pixel(gold, 4).SequenceEqual(new byte[] { 60, 0, 0 }),
            "gold pixels, lit and shaded, did not turn red while keeping their brightness: " +
            string.Join(" ", Pixel(gold, 0)) + " / " + string.Join(" ", Pixel(gold, 4)));
        Assert(Enumerable.Range(1, 3).All(index => Pixel(gold, index).SequenceEqual(colours[index])),
            "a gold colour range also changed the grey, the white or the blue");

        var light = Picture();
        new TextureRecolour(1, 0, 0, Brightness: -0.75f) { Selection = ColourSelection.LightAreas }.Apply(light);
        Assert(Pixel(light, 2).SequenceEqual(new byte[] { 60, 0, 0 }),
            "the white pixel was not darkened to a deep red: " + string.Join(" ", Pixel(light, 2)));
        Assert(new[] { 0, 1, 3, 4 }.All(index => Pixel(light, index).SequenceEqual(colours[index])),
            "light areas also changed the gold, the mid grey, the blue or the shadow");
        Assert(TextureRecolour.Hue(0, 0, 255) == 240 && TextureRecolour.Hue(255, 0, 128) is > 329 and < 331,
            "hues were not read in degrees around the colour wheel");
    }

    private static byte[] ReadInstalledMovie(string packagePath)
    {
        var catalogue = new AssetCatalogReader(protectionProvider: NoChunkProtectionProvider.Instance).Read(packagePath);
        var export = catalogue.Assets.Single(asset => asset.ClassName == "EFSwfMovie");
        using var logical = new LogicalPackageStream(catalogue.Summary, NoChunkProtectionProvider.Instance);
        var bytes = new byte[export.SerialSize];
        logical.Position = export.SerialOffset;
        logical.ReadExactly(bytes);
        return GfxMovieFile.Extract(bytes);
    }

    /// <summary>
    /// A small Scaleform movie shaped like the game's HUD: a packed atlas whose sub-image fills a shape inside
    /// "castbarFill_HP", inside "hpGauge", inside the QuickSlot_PlayerHP sprite placed as "playerHP"; and one standalone
    /// ("nopack") picture that shares the atlas's number.
    /// </summary>
    internal static byte[] BuildMovie(string name, bool compressed)
    {
        using var tags = new MemoryStream();
        Tag(tags, 1000, body => { body.Write((ushort)0x0401); body.Write(0); body.Write((ushort)13); Pascal(body, ""); Pascal(body, name); });
        Tag(tags, 1009, body => { body.Write((ushort)0); body.Write((ushort)9); body.Write((ushort)13); body.Write((ushort)256); body.Write((ushort)256); Pascal(body, ""); Pascal(body, "QuickSlot_I1.tga"); });
        Tag(tags, 1009, body => { body.Write((ushort)0); body.Write((ushort)9); body.Write((ushort)13); body.Write((ushort)64); body.Write((ushort)64); Pascal(body, "glow_0001.nopack"); Pascal(body, "QuickSlot_I1.nopack.tga"); });
        Tag(tags, 1008, body => { body.Write((ushort)5); body.Write((ushort)0); body.Write((ushort)0); body.Write((ushort)0); body.Write((ushort)32); body.Write((ushort)8); });
        Tag(tags, 32, body =>
        {
            body.Write((ushort)6);
            body.Write((byte)0);
            body.Write((byte)1); body.Write((byte)0x41); body.Write((ushort)5); body.Write((byte)0);
            body.Write((byte)0);
            body.Write((byte)0x10);
            body.Write((byte)0);
        });
        Tag(tags, 39, body => { body.Write((ushort)7); body.Write((ushort)1); Place(body, 6, "castbarFill_HP"); EndSprite(body); });
        Tag(tags, 39, body => { body.Write((ushort)8); body.Write((ushort)1); Place(body, 7, "hpGauge"); EndSprite(body); });
        Tag(tags, 26, body => PlaceBody(body, 8, "playerHP"));
        Tag(tags, 76, body => { body.Write((ushort)1); body.Write((ushort)8); body.Write(Encoding.ASCII.GetBytes("QuickSlot_PlayerHP\0")); });
        Tag(tags, 1, _ => { });
        Tag(tags, 0, _ => { });
        var header = new byte[] { 0, 0, 24, 1, 0 };
        var bodyBytes = header.Concat(tags.ToArray()).ToArray();
        var length = 8 + bodyBytes.Length;
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes(compressed ? "CFX" : "GFX"));
        output.WriteByte(10);
        output.Write(BitConverter.GetBytes(length));
        if (compressed)
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(bodyBytes);
        else
            output.Write(bodyBytes);
        return output.ToArray();

        static void Tag(Stream stream, int type, Action<BinaryWriter> write)
        {
            using var body = new MemoryStream();
            using (var writer = new BinaryWriter(body, Encoding.ASCII, leaveOpen: true)) write(writer);
            using var header = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            header.Write((ushort)((type << 6) | 0x3F));
            header.Write((int)body.Length);
            header.Write(body.ToArray());
        }
        static void Pascal(BinaryWriter writer, string value)
        {
            writer.Write((byte)value.Length);
            writer.Write(Encoding.ASCII.GetBytes(value));
        }
        static void PlaceBody(BinaryWriter writer, int character, string instance)
        {
            writer.Write((byte)0x22);
            writer.Write((ushort)1);
            writer.Write((ushort)character);
            writer.Write(Encoding.ASCII.GetBytes(instance + "\0"));
        }
        static void Place(BinaryWriter writer, int character, string instance)
        {
            using var body = new MemoryStream();
            using (var placement = new BinaryWriter(body, Encoding.ASCII, leaveOpen: true)) PlaceBody(placement, character, instance);
            writer.Write((ushort)((26 << 6) | 0x3F));
            writer.Write((int)body.Length);
            writer.Write(body.ToArray());
        }
        static void EndSprite(BinaryWriter writer)
        {
            writer.Write((ushort)(1 << 6));
            writer.Write((ushort)0);
        }
    }

    /// <summary>A one-export package holding an EFSwfMovie named TestMovie, laid out like the material fixture.</summary>
    internal static void WriteInterfaceMoviePackage(string path, byte[] payload)
    {
        var names = new[] { "None", "Core", "Class", "EFSwfMovie", "Package", "TestMovie", "RawData", "ArrayProperty" };
        using var namesStream = new MemoryStream();
        using (var writer = new BinaryWriter(namesStream, Encoding.UTF8, leaveOpen: true))
            foreach (var name in names) { WriteUnrealString(writer, name); writer.Write(0UL); }
        using var importStream = new MemoryStream();
        using (var writer = new BinaryWriter(importStream, Encoding.UTF8, leaveOpen: true))
        {
            WriteNameReference(writer, 1); WriteNameReference(writer, 2);
            writer.Write(0); WriteNameReference(writer, 3);
        }
        const int exportRecordSize = 68;
        var nameOffset = 129;
        var importOffset = checked(nameOffset + (int)namesStream.Length);
        var exportOffset = checked(importOffset + (int)importStream.Length);
        var serialOffset = checked(exportOffset + exportRecordSize);
        var total = checked(serialOffset + payload.Length);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var packageWriter = new BinaryWriter(stream);
        WriteSummary(packageWriter, total, names.Length, nameOffset, 1, exportOffset, 1, importOffset, total, total, []);
        while (stream.Position < nameOffset) packageWriter.Write((byte)0);
        packageWriter.Write(namesStream.ToArray());
        packageWriter.Write(importStream.ToArray());
        packageWriter.Write(-1); packageWriter.Write(0); packageWriter.Write(0);
        WriteNameReference(packageWriter, 5); packageWriter.Write(0);
        packageWriter.Write(0U); packageWriter.Write(0U);
        packageWriter.Write(payload.Length); packageWriter.Write(serialOffset);
        packageWriter.Write(0U); packageWriter.Write(0);
        packageWriter.Write(Guid.Empty.ToByteArray()); packageWriter.Write(0);
        packageWriter.Write(payload);
    }

    /// <summary>An EFSwfMovie export: a RawData byte array holding the movie, then the textures it references.</summary>
    internal static byte[] BuildExport(byte[] movie)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0);
        writer.Write(6L);
        writer.Write(7L);
        writer.Write(movie.Length + 4);
        writer.Write(0);
        writer.Write(movie.Length);
        writer.Write(movie);
        writer.Write(0L);
        writer.Write(1);
        writer.Write(0);
        return stream.ToArray();
    }
}
