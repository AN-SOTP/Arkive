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
        };
        byte[] hirc = [.. BitConverter.GetBytes(objects.Count), .. objects.SelectMany(item => item)];
        return [.. "BKHD"u8, .. BitConverter.GetBytes(8), .. BitConverter.GetBytes(WwiseBank.SupportedVersion), .. BitConverter.GetBytes(5000),
                .. "HIRC"u8, .. BitConverter.GetBytes(hirc.Length), .. hirc];
    }

    private static uint WwiseId(string name) => SoundCatalog.WwiseId(name);

    private static byte[] Source(uint sourceId, byte streamType, uint inMemorySize) =>
        [.. BitConverter.GetBytes(WwiseBank.VorbisPluginId), streamType, .. BitConverter.GetBytes(sourceId), .. BitConverter.GetBytes(inMemorySize), 0];

    private static byte[] NodeBase() => [0, 0, 0, .. BitConverter.GetBytes(0), .. BitConverter.GetBytes(0), 0, 0];

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
