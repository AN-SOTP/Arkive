using Arkive.Core.Audio;

namespace Arkive.SmokeTests;

internal static class SoundSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("unscrambles WwiseAudioPackage content at any offset", TestCipherOffsets),
        new("names loose music files the way the client does", TestLooseFileNames),
        new("writes PCM .wem files the format reader understands, and fits music length", TestPcmWem),
        new("names sounds, replaces a packaged clip and a loose music file, and restores both byte for byte", TestReplaceAndRestore),
        new("fits music segments and their intro to a replacement's length and restores the bank byte for byte", TestFitMusicLength),
    ];

    private static void TestCipherOffsets()
    {
        var content = Enumerable.Range(0, 854 * 300).Select(index => (byte)(index * 31 + 7)).ToArray();
        var scrambled = WwiseAudioFile.Scramble(content);
        Assert(WwiseAudioCipher.HasMarker(scrambled), "scrambled file has no marker");
        Assert(!scrambled.AsSpan(4, 64).SequenceEqual(content.AsSpan(0, 64)), "content was not scrambled");
        // Reading a slice must give the same bytes as unscrambling the whole file, across the key and counter wraps.
        foreach (var offset in new[] { 0, 1, 853, 854, 854 * 254 + 3, 854 * 255, 854 * 256 - 1 })
        {
            var slice = scrambled.AsSpan(4 + offset, 900).ToArray();
            WwiseAudioCipher.Apply(slice, offset);
            Assert(slice.AsSpan().SequenceEqual(content.AsSpan(offset, 900)), $"slice at {offset} did not unscramble");
        }
    }

    private static void TestLooseFileNames()
    {
        (uint Id, string Name)[] known =
        [
            (583232, "WHIBIBCQF2F29ZUBUBOQR.wem"),
            (3498061, "IPOHX34CXL8CIP9C0H9FS.wem"),
            (10093369, "4XXOII3OC5CLFFIL9W90U.wem"),
            (543099088, "WPIXOOXHHCDFCLLC889GU.wem"),
        ];
        foreach (var (id, name) in known)
            Assert(LostArkAudioFileNames.ForSource(id) == name, $"{id} was named {LostArkAudioFileNames.ForSource(id)}, not {name}");
    }

    private static void TestPcmWem()
    {
        var samples = Enumerable.Range(0, 22050 * 2).Select(index => (short)(index % 2000 - 1000)).ToArray();
        var wem = WemAudio.WritePcmWem(samples, 2, 22050);
        var format = WemAudio.ReadFormat(wem);
        Assert(format is { IsPcm: true, Channels: 2, SampleRate: 22050, SampleCount: 22050 }, $"PCM .wem read back as {format}");
        var wav = WemAudio.PcmWemToWav(wem);
        Assert(WemAudio.ReadFormat(wav) is { Codec: "PCM", SampleCount: 22050 }, "PCM .wem did not convert to .wav");
        var longer = WemAudio.FitLength(samples, 2, 11025, 22050);
        Assert(longer.Length == 11025 * 2 && longer[^1] == 0 && longer[0] == samples[0], "longer audio was not cut with a fade");
        var shorter = WemAudio.FitLength(samples, 2, 44100, 22050);
        Assert(shorter.Length == 44100 * 2 && shorter[^1] == 0 && shorter[100] == samples[100], "shorter audio was not padded");
    }

    private const uint SoundId = 1001, ActionId = 1002, SourceId = 777001, TrackId = 1101, MusicActionId = 1102, MusicSourceId = 583232;

    private static void TestReplaceAndRestore()
    {
        var root = Path.Combine(fixtureDirectory, "sounds", "WwiseAudioPackage");
        var data = Path.Combine(fixtureDirectory, "sounds", "data");
        Directory.CreateDirectory(root);
        var packagePath = Path.Combine(root, "TESTPACKAGE.pck");
        var loosePath = Path.Combine(root, LostArkAudioFileNames.ForSource(MusicSourceId));
        File.WriteAllBytes(packagePath, WwiseAudioFile.Scramble(BuildPackage()));
        File.WriteAllBytes(loosePath, WwiseAudioFile.Scramble(VorbisWem(44100, 2)));
        var originalPackage = File.ReadAllBytes(packagePath);
        var originalLoose = File.ReadAllBytes(loosePath);

        var catalog = SoundCatalog.Build(root, [("test_event", "s_test"), ("test_music", "s_test"), ("not_in_banks", "s_test")]);
        Assert(catalog.EventCount == 2, $"catalog named {catalog.EventCount} events, not 2");
        var effect = catalog.Search("test event", 10).Single();
        var music = catalog.Search("music", 10).Single();
        Assert(effect.SourceIds.SequenceEqual([SourceId]) && music.SourceIds.SequenceEqual([MusicSourceId]), "events did not lead to their clips");
        var clip = catalog.Clip(SourceId)!;
        var musicClip = catalog.Clip(MusicSourceId)!;
        Assert(!clip.IsMusic && musicClip.IsMusic && musicClip.Files.Single().IsLoose, "clip kinds or locations are wrong");
        Assert(SoundCatalog.ReadFormat(clip) is { Codec: "Vorbis", Duration.TotalSeconds: 1 }, "packaged clip length was not read");

        var service = new SoundReplacementService(data, new NoGame());
        var effectWem = WemAudio.WritePcmWem(new short[22050], 1, 44100);
        service.ReplaceAsync(clip, effect.Name, "short.wav", effectWem).GetAwaiter().GetResult();
        Assert(service.GetStatus(SourceId) == SoundReplacementStatus.Replaced, "packaged clip is not replaced");
        Assert(SoundCatalog.ReadFormat(clip) is { IsPcm: true, SampleCount: 22050 }, "packaged clip does not play the replacement");
        var field = clip.BankFields.Single();
        using (var package = WwiseAudioFile.Open(packagePath))
        {
            Assert(BinaryPrimitives.ReadUInt32LittleEndian(package.Read(field.PluginIdOffset, 4)) == WwiseBank.PcmPluginId &&
                   BinaryPrimitives.ReadUInt32LittleEndian(package.Read(field.InMemorySizeOffset, 4)) == effectWem.Length,
                "the bank still describes the old clip");
        }

        // Replacing again restores first, so the package grows by one copy only.
        service.ReplaceAsync(clip, effect.Name, "short2.wav", effectWem).GetAwaiter().GetResult();
        Assert(new FileInfo(packagePath).Length <= originalPackage.Length + effectWem.Length + 1, "a second replace stacked audio");

        var musicWem = WemAudio.WritePcmWem(WemAudio.FitLength(new short[88200 * 2], 2, 44100, 44100), 2, 44100);
        service.ReplaceAsync(musicClip, music.Name, "song.mp3", musicWem).GetAwaiter().GetResult();
        Assert(service.GetStatus(MusicSourceId) == SoundReplacementStatus.Replaced &&
               SoundCatalog.ReadFormat(musicClip) is { IsPcm: true, SampleCount: 44100 }, "loose music was not replaced at its length");

        service.RestoreAsync(SourceId).GetAwaiter().GetResult();
        service.RestoreAsync(MusicSourceId).GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(packagePath).AsSpan().SequenceEqual(originalPackage), "the package is not byte-identical after restore");
        Assert(File.ReadAllBytes(loosePath).AsSpan().SequenceEqual(originalLoose), "the loose music is not byte-identical after restore");
        Assert(service.LoadState() is { Replacements.Count: 0, Packages.Count: 0 }, "restore left replacement records behind");
    }

    // A song: a playlist that plays an intro segment once, then loops a second segment. Another segment plays two
    // tracks of one clip together, which cannot follow a new length.
    private const uint LoopSourceId = 543099088, IntroSourceId = 3498061, SharedSourceId = 10093369;
    private const uint LoopTrackId = 1201, IntroTrackId = 1202, SharedTrackA = 1203, SharedTrackB = 1204;
    private const uint LoopSegmentId = 1301, IntroSegmentId = 1302, SharedSegmentId = 1303, PlaylistId = 1401, SongActionId = 1402;

    private static void TestFitMusicLength()
    {
        var root = Path.Combine(fixtureDirectory, "sounds-fit", "WwiseAudioPackage");
        var data = Path.Combine(fixtureDirectory, "sounds-fit", "data");
        Directory.CreateDirectory(root);
        var packagePath = Path.Combine(root, "TESTPACKAGE.pck");
        File.WriteAllBytes(packagePath, WwiseAudioFile.Scramble(BuildPackage()));
        foreach (var source in new[] { LoopSourceId, IntroSourceId, SharedSourceId })
            File.WriteAllBytes(Path.Combine(root, LostArkAudioFileNames.ForSource(source)), WwiseAudioFile.Scramble(VorbisWem(88200, 2)));
        var originalPackage = File.ReadAllBytes(packagePath);
        var loopPath = Path.Combine(root, LostArkAudioFileNames.ForSource(LoopSourceId));
        var originalLoop = File.ReadAllBytes(loopPath);

        var catalog = SoundCatalog.Build(root, [("test_song", "s_test")]);
        var song = catalog.Search("test song", 10).Single();
        Assert(song.SourceIds.Order().SequenceEqual(new[] { LoopSourceId, IntroSourceId }.Order()), "the song did not lead to its two clips");
        var loop = catalog.Clip(LoopSourceId)!;
        Assert(loop.MusicFit is { Placements.Count: 1, Intros: [{ SourceId: IntroSourceId }] } &&
               catalog.Clip(IntroSourceId)!.MusicFit is { Placements.Count: 1, Intros.Count: 0 } &&
               catalog.Clip(SharedSourceId) is { IsMusic: true, MusicFit: null },
            "music timing was not found, or a segment playing a clip twice was offered for fitting");

        var service = new SoundReplacementService(data, new NoGame());
        var wem = WemAudio.WritePcmWem(new short[44100 * 3 * 2], 2, 44100);
        try
        {
            service.ReplaceAsync(catalog.Clip(SharedSourceId)!, "shared", "song.wav", wem, fitBankToLength: true).GetAwaiter().GetResult();
            throw new InvalidOperationException("a clip whose segment plays other audio was fitted");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("cannot follow", StringComparison.Ordinal))
        {
        }
        var record = service.ReplaceAsync(loop, song.Name, "song.wav", wem, fitBankToLength: true).GetAwaiter().GetResult();
        var bank = ReadBank(packagePath);
        Assert(record.OriginalMusicLengthMs == 2000 &&
               bank.MusicSegments[LoopSegmentId] is { Duration: 3000, ExitCue: 3000 } &&
               bank.MusicClips[LoopTrackId] is [{ SourceId: LoopSourceId, PlayAt: 0, BeginTrim: 0, EndTrim: 0, SourceDuration: 3000 }],
            "the looping segment was not fitted to the replacement's three seconds");
        Assert(bank.MusicSegments[IntroSegmentId] is { Duration: 3000, ExitCue: 3000 } &&
               bank.MusicClips[IntroTrackId] is [{ SourceId: LoopSourceId, PlayAt: 0, BeginTrim: 0, SourceDuration: 3000 }] &&
               bank.MusicTracks[IntroTrackId] is [{ Source: { SourceId: LoopSourceId, PluginId: WwiseBank.PcmPluginId } }],
            "the intro was not pointed at the replacement and fitted to it");
        Assert(bank.MusicSegments[SharedSegmentId] is { Duration: 2000 }, "a segment the replacement does not play was changed");
        try
        {
            service.ReplaceAsync(catalog.Clip(IntroSourceId)!, song.Name, "intro.wav", wem, fitBankToLength: true).GetAwaiter().GetResult();
            throw new InvalidOperationException("the intro was replaced over the song that now plays in its place");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("Restore it first", StringComparison.Ordinal))
        {
        }

        // A catalog read while the song is fitted sees the intro playing it; replacing again still fits the intro.
        var reread = SoundCatalog.Build(root, [("test_song", "s_test")]).Clip(LoopSourceId)!;
        Assert(reread.MusicFit is { Placements.Count: 2, Intros.Count: 0 }, "the fitted intro was not seen as playing the song");
        service.ReplaceAsync(reread, song.Name, "song2.wav", WemAudio.WritePcmWem(new short[44100 * 2], 2, 44100), fitBankToLength: true)
            .GetAwaiter().GetResult();
        bank = ReadBank(packagePath);
        Assert(bank.MusicSegments[IntroSegmentId] is { Duration: 1000 } &&
               bank.MusicClips[IntroTrackId] is [{ SourceId: LoopSourceId, SourceDuration: 1000 }] &&
               service.LoadState().Replacements.Single().OriginalMusicLengthMs == 2000,
            "replacing a fitted song again did not fit it and its intro to the new length");

        service.RestoreAsync(LoopSourceId).GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(packagePath).AsSpan().SequenceEqual(originalPackage) &&
               File.ReadAllBytes(loopPath).AsSpan().SequenceEqual(originalLoop),
            "the bank or the music is not byte-identical after restoring a fitted replacement");
    }

    private static WwiseBank ReadBank(string packagePath)
    {
        using var audio = WwiseAudioFile.Open(packagePath);
        var entry = WwisePackage.Read(packagePath, audio).Entries.Single(item => item.Section == WwisePackageSection.SoundBank);
        return WwiseBank.Parse(audio.Read(entry.DataOffset, (int)entry.Size));
    }

    private static byte[] MusicTrack(uint id, uint parent, uint sourceId, double playAt, double beginTrim, double duration) =>
        HircObject(11, id,
        [
            0, .. BitConverter.GetBytes(1), .. Source(sourceId, 2, 0),
            .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(id), .. BitConverter.GetBytes(sourceId), .. BitConverter.GetBytes(0),
            .. BitConverter.GetBytes(playAt), .. BitConverter.GetBytes(beginTrim), .. BitConverter.GetBytes(0d), .. BitConverter.GetBytes(duration),
            .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(0), .. NodeBase(parent),
        ]);

    /// <summary>A music segment: node parameters, its tracks, meter, no stingers, its length and entry and exit cues.</summary>
    private static byte[] MusicSegment(uint id, uint parent, uint[] tracks, double duration, double exitCue) =>
        HircObject(10, id,
        [
            0, .. NodeBase(parent), .. BitConverter.GetBytes(tracks.Length), .. tracks.SelectMany(BitConverter.GetBytes),
            .. BitConverter.GetBytes(1000d), .. BitConverter.GetBytes(0d), .. BitConverter.GetBytes(120f), 4, 4, 0,
            .. BitConverter.GetBytes(0), .. BitConverter.GetBytes(duration), .. BitConverter.GetBytes(2),
            .. BitConverter.GetBytes(43573010u), .. BitConverter.GetBytes(0d), .. BitConverter.GetBytes(0),
            .. BitConverter.GetBytes(1539036744u), .. BitConverter.GetBytes(exitCue), .. BitConverter.GetBytes(0),
        ]);

    private static byte[] PlaylistItem(uint segment, uint itemId, uint children, short loop) =>
    [
        .. BitConverter.GetBytes(segment), .. BitConverter.GetBytes(itemId), .. BitConverter.GetBytes(children),
        .. BitConverter.GetBytes(segment == 0 ? 0 : -1), .. BitConverter.GetBytes(loop), .. BitConverter.GetBytes(loop),
        .. BitConverter.GetBytes(loop), .. BitConverter.GetBytes(50000), 0, 0, 0, 0,
    ];

    private static byte[] BuildPackage()
    {
        var bank = BuildBank();
        var wem = VorbisWem(44100, 1);
        const int headerLength = 100;
        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        writer.Write("AKPK"u8);
        writer.Write(headerLength - 8);
        writer.Write(1);
        writer.Write(20); // language map
        writer.Write(24); // banks
        writer.Write(24); // streamed files
        writer.Write(4);  // externals
        writer.Write(1);
        writer.Write(12);
        writer.Write(0);
        writer.Write("s\0f\0x\0\0\0"u8);
        writer.Write(1);
        WriteEntry(writer, 5000, (uint)bank.Length, headerLength);
        writer.Write(1);
        WriteEntry(writer, SourceId, (uint)wem.Length, (uint)(headerLength + bank.Length));
        writer.Write(0);
        Assert(stream.Length == headerLength, "test package header has the wrong size");
        writer.Write(bank);
        writer.Write(wem);
        return stream.ToArray();
    }

    private static void WriteEntry(BinaryWriter writer, uint id, uint size, uint start)
    {
        writer.Write(id);
        writer.Write(1u);
        writer.Write(size);
        writer.Write(start);
        writer.Write(0u);
    }

    private static byte[] BuildBank()
    {
        var objects = new List<byte[]>
        {
            HircObject(2, SoundId, [.. Source(SourceId, 0, (uint)VorbisWem(44100, 1).Length), .. NodeBase()]),
            HircObject(3, ActionId, [.. BitConverter.GetBytes((ushort)0x0403), .. BitConverter.GetBytes(SoundId), 0]),
            HircObject(4, WwiseId("test_event"), [1, .. BitConverter.GetBytes(ActionId)]),
            HircObject(11, TrackId,
            [
                0, .. BitConverter.GetBytes(1), .. Source(MusicSourceId, 2, 0),
                .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(0), .. BitConverter.GetBytes(MusicSourceId), .. BitConverter.GetBytes(0),
                .. BitConverter.GetBytes(0d), .. BitConverter.GetBytes(0d), .. BitConverter.GetBytes(0d), .. BitConverter.GetBytes(1000d),
                .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(0), .. NodeBase(),
            ]),
            HircObject(3, MusicActionId, [.. BitConverter.GetBytes((ushort)0x0403), .. BitConverter.GetBytes(TrackId), 0]),
            HircObject(4, WwiseId("test_music"), [1, .. BitConverter.GetBytes(MusicActionId)]),
            MusicTrack(LoopTrackId, LoopSegmentId, LoopSourceId, 0, 0, 2000),
            MusicTrack(IntroTrackId, IntroSegmentId, IntroSourceId, -500, 500, 2000),
            MusicTrack(SharedTrackA, SharedSegmentId, SharedSourceId, 0, 0, 2000),
            MusicTrack(SharedTrackB, SharedSegmentId, SharedSourceId, 0, 1000, 2000),
            MusicSegment(LoopSegmentId, PlaylistId, [LoopTrackId], 2000, 1800),
            MusicSegment(IntroSegmentId, PlaylistId, [IntroTrackId], 1500, 1500),
            MusicSegment(SharedSegmentId, 0, [SharedTrackA, SharedTrackB], 2000, 2000),
            HircObject(13, PlaylistId,
            [
                0, .. NodeBase(), .. BitConverter.GetBytes(2), .. BitConverter.GetBytes(IntroSegmentId), .. BitConverter.GetBytes(LoopSegmentId),
                .. BitConverter.GetBytes(3),
                .. PlaylistItem(0, 9001, 2, 1), .. PlaylistItem(IntroSegmentId, 9002, 0, 1), .. PlaylistItem(LoopSegmentId, 9003, 0, 0),
            ]),
            HircObject(3, SongActionId, [.. BitConverter.GetBytes((ushort)0x0403), .. BitConverter.GetBytes(PlaylistId), 0]),
            HircObject(4, WwiseId("test_song"), [1, .. BitConverter.GetBytes(SongActionId)]),
        };
        byte[] hirc = [.. BitConverter.GetBytes(objects.Count), .. objects.SelectMany(item => item)];
        return [.. "BKHD"u8, .. BitConverter.GetBytes(8), .. BitConverter.GetBytes(WwiseBank.SupportedVersion), .. BitConverter.GetBytes(5000),
                .. "HIRC"u8, .. BitConverter.GetBytes(hirc.Length), .. hirc];
    }

    private static uint WwiseId(string name) => SoundCatalog.WwiseId(name);

    private static byte[] Source(uint sourceId, byte streamType, uint inMemorySize) =>
        [.. BitConverter.GetBytes(WwiseBank.VorbisPluginId), streamType, .. BitConverter.GetBytes(sourceId), .. BitConverter.GetBytes(inMemorySize), 0];

    private static byte[] NodeBase(uint parent = 0) => [0, 0, 0, .. BitConverter.GetBytes(0), .. BitConverter.GetBytes(parent), 0, 0];

    private static byte[] HircObject(byte type, uint id, byte[] body) =>
        [type, .. BitConverter.GetBytes(body.Length + 4), .. BitConverter.GetBytes(id), .. body];

    /// <summary>A Wwise Vorbis-style header (format 0xFFFF, sample count in the extra data) with filler audio.</summary>
    private static byte[] VorbisWem(int samples, int channels)
    {
        var fmt = new byte[0x42];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(0x18), (uint)samples);
        var audio = Enumerable.Range(0, 3000).Select(index => (byte)(index * 13)).ToArray();
        byte[] body = [.. "WAVE"u8, .. "fmt "u8, .. BitConverter.GetBytes(fmt.Length), .. fmt, .. "data"u8, .. BitConverter.GetBytes(audio.Length), .. audio];
        return [.. "RIFF"u8, .. BitConverter.GetBytes(body.Length), .. body];
    }

    private sealed class NoGame : IModDeploymentProcessGuard
    {
        public IReadOnlyList<string> FindBlockingProcesses() => [];
    }
}
