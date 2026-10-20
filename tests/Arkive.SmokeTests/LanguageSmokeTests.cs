using Arkive.Core.Languages;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Arkive.SmokeTests;

internal static class LanguageSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("writes a language's lines over the game's English text and restores data2.lpk byte for byte", TestApplyRestore),
        new("keeps a language the game drops and uses Arkive's saved copy after a game update", TestRemovedLanguage),
        new("refuses to change the game's language while Lost Ark runs", TestRefusesWhileRunning),
    ];

    private const string TextPath = "\\EFGame_Extra\\ClientData\\TableData\\EFTable_GameMsg.db";
    private const string OtherPath = "\\EFGame_Extra\\ClientData\\TableData\\EFTable_Npc.db";

    private sealed class Game(params string[] running) : IModDeploymentProcessGuard
    {
        public IReadOnlyList<string> FindBlockingProcesses() => running;
    }

    private const int PageSize = 1024;
    private const int Reserved = 16;

    private const string Columns = "([KEY] TEXT NOT NULL, [MSG] TEXT NOT NULL, [SOUND] TEXT NOT NULL, PRIMARY KEY ([KEY]))";

    private static string GameText(bool withSpanish) =>
        $"""
        CREATE TABLE GameMsg_English {Columns};
        INSERT INTO GameMsg_English VALUES ('sys.welcome','Welcome to Lost Ark!',''), ('sys.yorn','Welcome to Yorn!',''),
            ('sys.new','A new line',''), ('sys.empty','','');
        CREATE TABLE GameMsg_French {Columns};
        INSERT INTO GameMsg_French VALUES ('sys.welcome','Bienvenue dans Lost Ark !',''), ('sys.yorn','',''), ('sys.new','',''), ('sys.empty','','');
        """ + (withSpanish
            ? $"""
              CREATE TABLE GameMsg_Spanish {Columns};
              INSERT INTO GameMsg_Spanish VALUES ('sys.welcome','¡Te damos la bienvenida a Lost Ark!',''),
                  ('sys.yorn','¡{"{"}Gender{"}"}|gender(Bienvenido,Bienvenida) a Yorn!',''), ('sys.new','Una línea nueva' || printf('%.*c', 3000, '.'),''), ('sys.empty','x','');
              """
            : string.Empty);

    private static void TestApplyRestore() => InWorkspace((efGame, data) =>
    {
        var archive = Path.Combine(efGame, "data2.lpk");
        CreateArchive(archive, Database(data, GameText(withSpanish: true)));
        var original = File.ReadAllBytes(archive);
        var originalWritten = File.GetLastWriteTimeUtc(archive);
        var service = new GameLanguageService(efGame, data, new Game());

        Assert(service.NeedsSaving(), "a game whose text was never saved did not ask to be saved");
        service.SaveAsync().GetAwaiter().GetResult();
        Assert(!service.NeedsSaving(), "the saved text was not recorded");
        var languages = service.Languages();
        Assert(languages.Select(language => language.Name).SequenceEqual(["English", "Spanish", "French"]) &&
               languages[1] is { Translated: 3, Lines: 3, InGame: true, Title: "Español (Spanish)" } && languages[2].Translated == 1,
            "the languages were not counted: " + string.Join(", ", languages.Select(language => $"{language.Name} {language.Translated}/{language.Lines}")));

        var lines = service.ApplyAsync("French").GetAwaiter().GetResult();
        Assert(lines == 1 && Text(archive)["sys.welcome"] == "Bienvenue dans Lost Ark !" && Text(archive)["sys.yorn"] == "Welcome to Yorn!",
            "untranslated lines did not stay English");
        lines = service.ApplyAsync("Spanish").GetAwaiter().GetResult();
        var text = Text(archive);
        Assert(lines == 3 && text["sys.welcome"] == "¡Te damos la bienvenida a Lost Ark!" &&
               text["sys.yorn"] == "¡{Gender}|gender(Bienvenido,Bienvenida) a Yorn!" && text["sys.empty"] == string.Empty,
            "the English table does not hold the Spanish lines: " + string.Join(" | ", text.Values));
        Assert(Payload(archive, OtherPath).SequenceEqual(Payload(original, OtherPath)), "another database in data2.lpk changed");
        AssertPagesMarked(archive);
        Assert(service.CurrentLanguage() == "Spanish" && !service.GameUpdated() && !service.NeedsSaving(),
            "Arkive's own change was mistaken for a game update");

        service.RestoreAsync().GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(archive).SequenceEqual(original) && File.GetLastWriteTimeUtc(archive) == originalWritten,
            "the game's data2.lpk was not put back exactly");
        Assert(service.CurrentLanguage() == "English" && service.LoadState() is null && !service.NeedsSaving(),
            "restoring did not clear the change");
    });

    private static void TestRemovedLanguage() => InWorkspace((efGame, data) =>
    {
        var archive = Path.Combine(efGame, "data2.lpk");
        CreateArchive(archive, Database(data, GameText(withSpanish: true)));
        var service = new GameLanguageService(efGame, data, new Game());
        service.ApplyAsync("Spanish").GetAwaiter().GetResult();

        // A game update replaces data2.lpk with one that no longer has Spanish.
        CreateArchive(archive, Database(data, GameText(withSpanish: false)));
        File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddMinutes(5));
        var patched = File.ReadAllBytes(archive);
        Assert(service.GameUpdated() && service.CurrentLanguage() == "English" && service.NeedsSaving(), "the game update was not noticed");
        service.SaveAsync().GetAwaiter().GetResult();
        Assert(service.Languages().FirstOrDefault(language => language.Name == "Spanish") is { InGame: false, Translated: 3 },
            "the Spanish lines the game dropped were not kept");

        var lines = service.ApplyAsync("Spanish").GetAwaiter().GetResult();
        Assert(lines == 3 && Text(archive)["sys.new"].StartsWith("Una línea nueva", StringComparison.Ordinal), "the saved Spanish lines were not used");
        AssertPagesMarked(archive);
        service.RestoreAsync().GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(archive).SequenceEqual(patched), "the updated game's data2.lpk was not put back exactly");
    });

    private static void TestRefusesWhileRunning() => InWorkspace((efGame, data) =>
    {
        var archive = Path.Combine(efGame, "data2.lpk");
        CreateArchive(archive, Database(data, GameText(withSpanish: true)));
        var original = File.ReadAllBytes(archive);
        var service = new GameLanguageService(efGame, data, new Game("LOSTARK.exe"));
        try
        {
            service.ApplyAsync("Spanish").GetAwaiter().GetResult();
            throw new InvalidOperationException("the language was changed while the game ran");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("Close Lost Ark", StringComparison.Ordinal))
        {
        }
        Assert(File.ReadAllBytes(archive).SequenceEqual(original), "data2.lpk changed while the game ran");
    });

    // The game's databases end every page with 16 bytes of 0x10, and the game cannot read a page that does not.
    private static void AssertPagesMarked(string archive)
    {
        var bytes = File.ReadAllBytes(archive);
        var database = Decrypt(Payload(bytes, TextPath, out var size), size);
        var pages = database.Length / PageSize;
        Assert(database.Length % PageSize == 0 && database[20] == Reserved && pages > 3 && Enumerable.Range(1, pages)
                .All(page => database.AsSpan(page * PageSize - Reserved, Reserved).IndexOfAnyExcept((byte)Reserved) < 0),
            "a page of the changed game text does not end with the game's marker");
    }

    private static Dictionary<string, string> Text(string archive)
    {
        var path = archive + ".check.db";
        var bytes = File.ReadAllBytes(archive);
        File.WriteAllBytes(path, Decrypt(Payload(bytes, TextPath, out var size), size));
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT KEY, MSG FROM GameMsg_English";
            using var reader = command.ExecuteReader();
            var text = new Dictionary<string, string>();
            while (reader.Read()) text[reader.GetString(0)] = reader.GetString(1);
            return text;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Payload(string archive, string name) => Payload(File.ReadAllBytes(archive), name, out _);

    private static byte[] Payload(byte[] archive, string name) => Payload(archive, name, out _);

    private static byte[] Payload(byte[] archive, string name, out int size)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(archive);
        var table = archive.AsSpan(4, count * 528).ToArray();
        Blowfish(table, encrypt: false);
        long offset = 4 + table.Length + 4;
        for (var index = 0; index < count; index++)
        {
            var record = table.AsSpan(index * 528, 528);
            var entry = Encoding.UTF8.GetString(record.Slice(4, BinaryPrimitives.ReadInt32LittleEndian(record))).TrimEnd('\0');
            var encrypted = BinaryPrimitives.ReadInt32LittleEndian(record[^8..]);
            if (entry == name)
            {
                size = BinaryPrimitives.ReadInt32LittleEndian(record[^12..]);
                return archive.AsSpan((int)offset, encrypted).ToArray();
            }
            offset += encrypted;
        }
        throw new InvalidDataException(name + " is not in the archive.");
    }

    private static void CreateArchive(string path, byte[] text)
    {
        var npc = new byte[2048];
        new Random(7).NextBytes(npc);
        (string Name, byte[] Data)[] entries = [(OtherPath, npc), (TextPath, text)];
        var table = new byte[528 * entries.Length];
        var payloads = new List<byte[]>();
        for (var index = 0; index < entries.Length; index++)
        {
            var record = table.AsSpan(index * 528, 528);
            var name = Encoding.UTF8.GetBytes(entries[index].Name + '\0');
            BinaryPrimitives.WriteInt32LittleEndian(record, name.Length);
            name.CopyTo(record[4..]);
            var payload = Encrypt(entries[index].Data);
            BinaryPrimitives.WriteInt32LittleEndian(record[^12..], entries[index].Data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(record[^8..], payload.Length);
            payloads.Add(payload);
        }
        Blowfish(table, encrypt: true);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(entries.Length);
        writer.Write(table);
        writer.Write(0);
        foreach (var payload in payloads) writer.Write(payload);
    }

    private static void Blowfish(byte[] data, bool encrypt)
    {
        var cipher = new BlowfishEngine();
        cipher.Init(encrypt, new KeyParameter(Encoding.ASCII.GetBytes("83657ea6ffa1e671375c689a2e99a598")));
        for (var offset = 0; offset < data.Length; offset += 8)
        {
            data.AsSpan(offset, 4).Reverse(); data.AsSpan(offset + 4, 4).Reverse();
            cipher.ProcessBlock(data, offset, data, offset);
            data.AsSpan(offset, 4).Reverse(); data.AsSpan(offset + 4, 4).Reverse();
        }
    }

    private static Aes GameMsgCipher()
    {
        var digest = MD5.HashData(Encoding.Unicode.GetBytes("GameMsg"));
        var mixed = Convert.FromHexString("1069D88738C5C75F82B44A1F0A382768");
        for (var index = 0; index < 16; index++) mixed[index] ^= digest[15 - index];
        var aes = Aes.Create();
        aes.Key = SHA256.HashData(Encoding.ASCII.GetBytes(Convert.ToHexString(mixed).ToLowerInvariant()));
        return aes;
    }

    // Both entries use the GameMsg key: the archive reader only needs a well-formed payload for the other one.
    private static byte[] Encrypt(byte[] data)
    {
        using var aes = GameMsgCipher();
        var result = new byte[(data.Length + 15) / 16 * 16];
        data.CopyTo(result, 0);
        for (var offset = 0; offset < result.Length; offset += 0x400)
        {
            var length = Math.Min(0x400, result.Length - offset);
            aes.EncryptCbc(result.AsSpan(offset, length), new byte[16], result.AsSpan(offset, length), PaddingMode.None);
        }
        return result;
    }

    private static byte[] Decrypt(byte[] data, int size)
    {
        using var aes = GameMsgCipher();
        var result = new byte[data.Length];
        for (var offset = 0; offset < data.Length; offset += 0x400)
        {
            var length = Math.Min(0x400, data.Length - offset);
            aes.DecryptCbc(data.AsSpan(offset, length), new byte[16], result.AsSpan(offset, length), PaddingMode.None);
        }
        return result[..size];
    }

    private static byte[] Database(string root, string sql)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            // Shaped like the game's: 1 KB pages with 16 reserved bytes each.
            var reserve = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(reserve, Reserved);
                using (var pageSize = connection.CreateCommand())
                {
                    pageSize.CommandText = $"PRAGMA page_size = {PageSize}";
                    pageSize.ExecuteNonQuery();
                }
                Assert(FileControl(connection.Handle!.DangerousGetHandle(), "main"u8.ToArray(), 38 /* SQLITE_FCNTL_RESERVE_BYTES */, reserve) == 0,
                    "the fixture database's reserved bytes could not be set");
            }
            finally
            {
                Marshal.FreeHGlobal(reserve);
            }
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        Assert(bytes[20] == Reserved, "the fixture database has no reserved bytes");
        for (var end = PageSize; end <= bytes.Length; end += PageSize) bytes.AsSpan(end - Reserved, Reserved).Fill(Reserved);
        return bytes;
    }

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_file_control")]
    private static extern int FileControl(IntPtr database, byte[] name, int operation, IntPtr argument);

    private static void InWorkspace(Action<string, string> test)
    {
        var root = Path.Combine(fixtureDirectory, "languages-" + Guid.NewGuid().ToString("N"));
        var efGame = Path.Combine(root, "EFGame");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(efGame);
        Directory.CreateDirectory(data);
        try
        {
            test(efGame, data);
            var languages = Path.Combine(data, "languages");
            Assert(!Directory.Exists(languages) || !Directory.EnumerateDirectories(languages).Any(), "a working folder was left behind");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
