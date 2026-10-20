using Arkive.Core.Audio;
using Arkive.Core.LoginScreens;

namespace Arkive.SmokeTests;

internal static class LoginScreenSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("finds each login video's key from the shared header fields", TestVideoKeys),
        new("puts a login screen's video, music and ambience in place of the game's and restores them byte for byte", TestLoginScreens),
        new("puts back data1.lpk changed by Arkive 0.4.5's movie table switch", TestLegacyRestore),
    ];

    private sealed class NoGame : IModDeploymentProcessGuard
    {
        public IReadOnlyList<string> FindBlockingProcesses() => [];
    }

    // The game's hashed names: login_cinema_1end (the screen the game shows), login_cinema_eclipse, login_cinema_kazeroth.
    private const string Live = "2N3HG1GBHGP9X1G6PGIEZ36.ipk";
    private const string Eclipse = "1M2GF0FAGFO8W0FOA1GTEO.ipk";
    private const string Kazeros = "2N3HG1GBHGP9X1GVXSP8NMA.ipk";

    /// <summary>A Bink 2 shaped video: header, frame index (keyframes marked in bit 0), frames of filler.</summary>
    private static byte[] Video(int frames, uint frameRate, uint frameRateBase, int seed)
    {
        var random = new Random(seed);
        var sizes = Enumerable.Range(0, frames).Select(_ => 64 + random.Next(400) * 4).ToArray();
        var start = 48 + 4 * (frames + 1);
        var data = new byte[start + sizes.Sum()];
        random.NextBytes(data);
        "KB2j"u8.CopyTo(data);
        uint[] header = [(uint)(data.Length - 8), (uint)frames, (uint)sizes.Max(), (uint)frames, 3550, 1522, frameRate, frameRateBase, 16, 0];
        for (var index = 0; index < header.Length; index++) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4 + 4 * index), header[index]);
        var offset = start;
        for (var frame = 0; frame < frames; frame++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48 + 4 * frame), (uint)offset | (frame % 30 == 0 ? 1u : 0u));
            offset += sizes[frame];
        }
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48 + 4 * frames), (uint)offset);
        return data;
    }

    private static byte[] KeyFor(int seed)
    {
        var pattern = new byte[16];
        new Random(seed).NextBytes(pattern);
        var key = LoginVideoCipher.ReferenceKey.ToArray();
        for (var index = 0; index < key.Length; index++) key[index] ^= pattern[index % 16];
        return key;
    }

    private static byte[] Scramble(byte[] plain, byte[] key)
    {
        var data = plain.ToArray();
        LoginVideoCipher.Apply(data, key, 0);
        return data;
    }

    private static void TestVideoKeys()
    {
        var root = Path.Combine(fixtureDirectory, "login-video-keys");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        (byte[] Plain, byte[] Key)[] cases = [(Video(150, 10_000_000, 333_333, 1), KeyFor(11)), (Video(90, 30, 1, 2), KeyFor(12))];
        foreach (var (plain, key) in cases)
        {
            var path = Path.Combine(root, "video.ipk");
            File.WriteAllBytes(path, Scramble(plain, key));
            Assert(LoginVideoCipher.FindKey(path) is { } found && found.SequenceEqual(key), "a login video's key was not found");
            var unscrambled = Path.Combine(root, "plain.bk2");
            LoginVideoCipher.Transcode(path, key, unscrambled, null);
            Assert(File.ReadAllBytes(unscrambled).SequenceEqual(plain), "unscrambling did not give the plain video");
        }
        var noise = new byte[200_000];
        new Random(3).NextBytes(noise);
        File.WriteAllBytes(Path.Combine(root, "noise.ipk"), noise);
        Assert(LoginVideoCipher.FindKey(Path.Combine(root, "noise.ipk")) is null, "a file that is no login video got a key");
    }

    private static void TestLoginScreens()
    {
        var root = Path.Combine(fixtureDirectory, "login-screens");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var efGame = Path.Combine(root, "EFGame");
        var movies = Path.Combine(efGame, "Movies");
        var audio = Path.Combine(efGame, "ReleasePC", "WwiseAudioPackage");
        Directory.CreateDirectory(movies);
        Directory.CreateDirectory(audio);
        var livePlain = Video(150, 10_000_000, 333_333, 4);
        var eclipsePlain = Video(120, 10_000_000, 333_333, 5);
        var liveKey = KeyFor(21);
        var eclipseKey = KeyFor(22);
        var livePath = Path.Combine(movies, Live);
        File.WriteAllBytes(livePath, Scramble(livePlain, liveKey));
        File.WriteAllBytes(Path.Combine(movies, Eclipse), Scramble(eclipsePlain, eclipseKey));
        File.WriteAllBytes(Path.Combine(movies, Kazeros), new byte[5000]);
        var packagePath = Path.Combine(audio, "LOGINSOUNDS.pck");
        File.WriteAllBytes(packagePath, WwiseAudioFile.Scramble(SoundPackage()));
        var cleanVideo = File.ReadAllBytes(livePath);
        var cleanPackage = File.ReadAllBytes(packagePath);

        var service = new LoginScreenService(efGame, Path.Combine(root, "data"), new NoGame());
        var screens = service.Find();
        Assert(screens.Select(screen => screen.Movie).SequenceEqual(["login_cinema_eclipse", "login_cinema_kazeroth", "login_cinema_1end"]) &&
               screens[0] is { Title: "Eclipse", Music: "bgm_login_eclipse", Ambience: "amb_intro_eclipse", CanUse: true } &&
               screens[1] is { Title: "Kazeros", CanUse: false } && screens[2].Music == "bgm_login_victorysite" &&
               service.LiveMovie() == "login_cinema_1end",
            "the login screens were not read: " + string.Join(", ", screens.Select(screen => $"{screen.Movie} {screen.CanUse}")));

        var note = service.ApplyAsync("login_cinema_eclipse").GetAwaiter().GetResult();
        Assert(note is null, "the sounds were reported missing: " + note);
        var written = File.ReadAllBytes(livePath);
        LoginVideoCipher.Apply(written, liveKey, 0);
        Assert(written.SequenceEqual(eclipsePlain), "the shown screen's file does not hold the chosen video under its own key");
        Assert(Targets(packagePath).SequenceEqual([MusicEclipse, AmbienceEclipse, OtherStop, AmbienceEclipse, PageEclipse,
                   MusicEclipse, AmbienceEclipse, OtherStop, AmbienceEclipse, PageEclipse]),
            "the shown screen's music and ambience do not start the chosen screen's: " + string.Join(", ", Targets(packagePath)));
        Assert(service.LoadState() is { Movie: "login_cinema_eclipse", Sounds.Count: 4 } &&
               !service.GameUpdatedAsync().GetAwaiter().GetResult() &&
               service.CurrentMovieAsync().GetAwaiter().GetResult() == "login_cinema_eclipse" &&
               File.ReadAllBytes(service.VideoSource(service.Find()[2])).SequenceEqual(cleanVideo),
            "the change was not recorded, or the shown screen's own video is not kept");

        service.RestoreAsync().GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(livePath).SequenceEqual(cleanVideo) && File.ReadAllBytes(packagePath).SequenceEqual(cleanPackage) &&
               service.LoadState() is null, "going back did not put the video and sound bank back byte for byte");

        // A game update replaces the video: Arkive notices, and going back leaves the new file alone.
        service.ApplyAsync("login_cinema_eclipse").GetAwaiter().GetResult();
        var patched = Scramble(Video(150, 10_000_000, 333_333, 6), liveKey);
        File.WriteAllBytes(livePath, patched);
        Assert(service.GameUpdatedAsync().GetAwaiter().GetResult(), "a game update over Arkive's change was not noticed");
        service.RestoreAsync().GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(livePath).SequenceEqual(patched) && File.ReadAllBytes(packagePath).SequenceEqual(cleanPackage) &&
               service.LoadState() is null, "going back after a game update replaced the updated video");
    }

    private static void TestLegacyRestore()
    {
        var root = Path.Combine(fixtureDirectory, "login-screens-legacy");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var efGame = Path.Combine(root, "EFGame");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(Path.Combine(efGame, "Movies"));
        Directory.CreateDirectory(Path.Combine(data, "login-screens"));
        var original = Encoding.ASCII.GetBytes("the game's data1");
        var changed = Encoding.ASCII.GetBytes("data1 with the table changed");
        var backup = Path.Combine(data, "data1-backup.lpk");
        File.WriteAllBytes(backup, original);
        File.WriteAllBytes(Path.Combine(efGame, "data1.lpk"), changed);
        File.WriteAllText(Path.Combine(data, "login-screens", "state.json"), new JsonObject
        {
            ["Movie"] = "login_cinema_eclipse",
            ["OriginalSha256"] = Convert.ToHexString(SHA256.HashData(original)),
            ["AppliedSha256"] = Convert.ToHexString(SHA256.HashData(changed)),
            ["BackupPath"] = backup,
            ["AppliedAt"] = DateTimeOffset.UtcNow,
        }.ToJsonString());
        var service = new LoginScreenService(efGame, data, new NoGame());
        service.RestoreAsync().GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(Path.Combine(efGame, "data1.lpk")).SequenceEqual(original) &&
               !File.Exists(Path.Combine(data, "login-screens", "state.json")), "the old movie table change was not undone");
    }

    private const uint MusicLive = 905796857, MusicEclipse = 105090701;
    private const uint AmbienceLive = 153292767, AmbienceEclipse = 671349971, OtherStop = 703115486;
    private const uint PageLive = 414495972, PageEclipse = 841570391;
    private const int PackageHeaderLength = 80;

    // Every action target in the test bank, in order.
    private static uint[] Targets(string packagePath)
    {
        var bank = WwiseAudioFile.ReadAll(packagePath).AsSpan(PackageHeaderLength).ToArray();
        var targets = new List<uint>();
        const int hirc = 16 + 8;
        var count = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(hirc));
        var at = hirc + 4;
        for (var index = 0; index < count; index++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(at + 1));
            if (bank[at] == 3) targets.Add(BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(at + 11)));
            at += 5 + length;
        }
        return [.. targets];
    }

    /// <summary>A sound package with one bank holding the 1end and Eclipse login sound events.</summary>
    private static byte[] SoundPackage()
    {
        var bank = SoundBank();
        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        writer.Write("AKPK"u8);
        writer.Write(PackageHeaderLength - 8);
        writer.Write(1);
        writer.Write(20); // language map
        writer.Write(24); // banks
        writer.Write(4);  // streamed files
        writer.Write(4);  // externals
        writer.Write(1);
        writer.Write(12);
        writer.Write(0);
        writer.Write("s\0f\0x\0\0\0"u8);
        writer.Write(1);
        writer.Write(5000u);
        writer.Write(1u);
        writer.Write((uint)bank.Length);
        writer.Write((uint)PackageHeaderLength);
        writer.Write(0u);
        writer.Write(0);
        writer.Write(0);
        Assert(stream.Length == PackageHeaderLength, "test package header has the wrong size");
        writer.Write(bank);
        return stream.ToArray();
    }

    private static byte[] SoundBank()
    {
        var actions = new List<byte[]>();
        var events = new List<byte[]>();
        var nextAction = 100u;
        void Event(string name, params (ushort Type, uint Target)[] steps)
        {
            var ids = new List<uint>();
            foreach (var (type, target) in steps)
            {
                var id = nextAction++;
                actions.Add(HircObject(3, id, [.. BitConverter.GetBytes(type), .. BitConverter.GetBytes(target), 0, 0, 0, 0]));
                ids.Add(id);
            }
            events.Add(HircObject(4, SoundCatalog.WwiseId(name), [(byte)ids.Count, .. ids.SelectMany(BitConverter.GetBytes)]));
        }
        foreach (var (name, music, ambience, page) in new[]
                 {
                     ("1end", MusicLive, AmbienceLive, PageLive),
                     ("eclipse", MusicEclipse, AmbienceEclipse, PageEclipse),
                 })
        {
            Event(name == "1end" ? "bgm_login_victorysite" : "bgm_login_eclipse", (0x0403, music));
            Event($"amb_intro_{name}_start", (0x0403, ambience), (0x0102, OtherStop));
            Event($"amb_intro_{name}_stop", (0x0102, ambience));
            Event($"amb_intro_{name}_pagechange1", (0x0403, page));
        }
        byte[][] objects = [.. actions, .. events];
        byte[] hirc = [.. BitConverter.GetBytes(objects.Length), .. objects.SelectMany(item => item)];
        return [.. "BKHD"u8, .. BitConverter.GetBytes(8), .. BitConverter.GetBytes(WwiseBank.SupportedVersion), .. BitConverter.GetBytes(5000),
                .. "HIRC"u8, .. BitConverter.GetBytes(hirc.Length), .. hirc];
    }

    private static byte[] HircObject(byte type, uint id, byte[] body) =>
        [type, .. BitConverter.GetBytes(body.Length + 4), .. BitConverter.GetBytes(id), .. body];
}
