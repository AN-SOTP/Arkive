using System.IO.Compression;
using Arkive.Core.LoginScreens;
using Arkive.Core.Skills;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Arkive.SmokeTests;

internal static class LoginScreenSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("switches the login background through the movie table and restores data1.lpk byte for byte", TestLoginScreens),
    ];

    private sealed class NoGame : Arkive.Core.Mods.IModDeploymentProcessGuard
    {
        public IReadOnlyList<string> FindBlockingProcesses() => [];
    }

    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value + "\0");

    private static byte[] Field(string value) => [.. BitConverter.GetBytes(Text(value).Length), .. Text(value)];

    // The shape of Table_MovieData.loa: per entry its key, flags, the player's name, settings, then the movie's name.
    private static byte[] MovieTable(string movie) =>
    [
        .. BitConverter.GetBytes(1f), .. BitConverter.GetBytes(2),
        .. Field("FullScreen.Intro"), 0, 4, 0, 0, 0, .. Field("SGintro"), 0, 1, 0, 0, 0,
        .. Field("LogIn.Background"), 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. Field("loginBackGround"),
        0, 1, 0, 0, 0, 0, 0x0A, 0, 0, 0x38, 4, 0, 0, 1, 0, 0, 0, .. Field(movie), 0, 2, 0, 0, 0,
        .. Field("LogIn.Background_Event"), 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. Field("loginBackGround"),
        0, 1, 0, 0, 0, 0, 0x0A, 0, 0, 0x38, 4, 0, 0, 1, 0, 0, 0, .. Field(movie), 0, 4, 0, 0, 0,
    ];

    private static void Crypt(byte[] data)
    {
        var cipher = new BlowfishEngine();
        cipher.Init(true, new KeyParameter(Encoding.ASCII.GetBytes("83657ea6ffa1e671375c689a2e99a598")));
        for (var offset = 0; offset + 8 <= data.Length; offset += 8)
        {
            var block = data.AsSpan(offset, 8);
            block[..4].Reverse(); block[4..].Reverse();
            cipher.ProcessBlock(data, offset, data, offset);
            block[..4].Reverse(); block[4..].Reverse();
        }
    }

    /// <summary>A data archive the way the game writes one: count, Blowfish index of 528-byte records, then entries.</summary>
    private static void WriteArchive(string path, params (string Name, byte[] Contents)[] files)
    {
        var table = new byte[files.Length * 528];
        var bodies = new List<byte[]>();
        for (var index = 0; index < files.Length; index++)
        {
            using var packed = new MemoryStream();
            using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(files[index].Contents);
            var body = new byte[(packed.Length / 8 + 1) * 8];
            packed.ToArray().CopyTo(body, 0);
            Crypt(body);
            bodies.Add(body);
            var record = table.AsSpan(index * 528, 528);
            var name = Encoding.UTF8.GetBytes(files[index].Name.Replace('/', '\\'));
            BinaryPrimitives.WriteInt32LittleEndian(record, name.Length);
            name.CopyTo(record[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(record[^12..], files[index].Contents.Length);
            BinaryPrimitives.WriteInt32LittleEndian(record[^8..], body.Length);
            BinaryPrimitives.WriteInt32LittleEndian(record[^4..], (int)packed.Length);
        }
        Crypt(table);
        using var output = File.Create(path);
        output.Write(BitConverter.GetBytes(files.Length));
        output.Write(table);
        output.Write(new byte[4]);
        foreach (var body in bodies) output.Write(body);
    }

    private static void TestLoginScreens()
    {
        var root = Path.Combine(fixtureDirectory, "login-screens");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var efGame = Path.Combine(root, "EFGame");
        Directory.CreateDirectory(Path.Combine(efGame, "Movies"));
        // The game's own hashed names: login_cinema and login_cinema_kazeroth.
        File.WriteAllBytes(Path.Combine(efGame, "Movies", "1M2GF0FAGFO8WDE2VYVW2V.ipk"), new byte[64]);
        File.WriteAllBytes(Path.Combine(efGame, "Movies", "2N3HG1GBHGP9X1GVXSP8NMA.ipk"), new byte[48]);
        var other = Enumerable.Range(0, 5000).Select(index => (byte)(index * 31)).ToArray();
        var archive = Path.Combine(efGame, "data1.lpk");
        WriteArchive(archive, ("/../Common/Other.xml", other), ("/EFGame_Extra/Config/Table_MovieData.loa", MovieTable("login_cinema")));
        var clean = File.ReadAllBytes(archive);

        var service = new LoginScreenService(efGame, Path.Combine(root, "data"), new NoGame());
        var screens = service.Find();
        Assert(screens.Select(screen => screen.Movie).SequenceEqual(["login_cinema", "login_cinema_kazeroth"]) &&
               screens[1].Title == "Kazeros" && service.CurrentMovie() == "login_cinema",
            "the login backgrounds or the one in use were not read: " + string.Join(", ", screens.Select(screen => screen.Movie)));

        service.ApplyAsync("login_cinema_kazeroth").GetAwaiter().GetResult();
        using (var stream = File.OpenRead(archive))
        {
            var entries = LostArkDataArchive.ReadEntries(stream);
            var table = LostArkDataArchive.Read(stream, entries[1]);
            Assert(service.CurrentMovie() == "login_cinema_kazeroth" &&
                   LoginScreenService.Read(table, "LogIn.Background_Event") == "login_cinema_kazeroth" &&
                   LostArkDataArchive.Read(stream, entries[0]).SequenceEqual(other) &&
                   table.SequenceEqual(MovieTable("login_cinema_kazeroth")),
                "the chosen background was not written to both login entries, or another file in the archive changed");
        }
        Assert(service.LoadState() is { Movie: "login_cinema_kazeroth" } && !service.GameUpdatedAsync().GetAwaiter().GetResult(),
            "the change was not recorded");

        service.RestoreAsync().GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(archive).SequenceEqual(clean) && service.LoadState() is null,
            "restoring did not put data1.lpk back byte for byte");

        // A game update replaces data1.lpk: Arkive notices, and restoring leaves the new file alone.
        service.ApplyAsync("login_cinema_kazeroth").GetAwaiter().GetResult();
        WriteArchive(archive, ("/../Common/Other.xml", other.Reverse().ToArray()), ("/EFGame_Extra/Config/Table_MovieData.loa", MovieTable("login_cinema")));
        var patched = File.ReadAllBytes(archive);
        Assert(service.GameUpdatedAsync().GetAwaiter().GetResult(), "a game update over Arkive's change was not noticed");
        service.RestoreAsync().GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(archive).SequenceEqual(patched) && service.LoadState() is null,
            "restoring after a game update replaced the updated file");
    }
}
