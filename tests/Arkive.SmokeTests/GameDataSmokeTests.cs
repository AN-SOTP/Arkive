using System.Runtime.InteropServices;
using Arkive.Core.GameData;
using Arkive.Core.Languages;
using Microsoft.Data.Sqlite;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Arkive.SmokeTests;

internal static class GameDataSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("writes game table edits and a language into data2.lpk together and restores it byte for byte", TestCombinedWrite),
        new("imports a LostArkPatcher .sql patch as table cell edits and refuses anything but UPDATE", TestSqlImport),
        new("plans, applies, rebases and restores a Game Data mod through the mod library", TestModWorkflow),
    ];

    private const string TableFolder = "\\EFGame_Extra\\ClientData\\TableData\\";
    private const int PageSize = 1024;
    private const int Reserved = 16;

    private sealed class Game : IModDeploymentProcessGuard
    {
        public IReadOnlyList<string> FindBlockingProcesses() => [];
    }

    private const string Text = """
        CREATE TABLE GameMsg_English ([KEY] TEXT NOT NULL, [MSG] TEXT NOT NULL, PRIMARY KEY ([KEY]));
        INSERT INTO GameMsg_English VALUES ('sys.welcome','Welcome to Lost Ark!'), ('item.name','Robe');
        CREATE TABLE GameMsg_Spanish ([KEY] TEXT NOT NULL, [MSG] TEXT NOT NULL, PRIMARY KEY ([KEY]));
        INSERT INTO GameMsg_Spanish VALUES ('sys.welcome','¡Te damos la bienvenida a Lost Ark!'), ('item.name','Túnica');
        """;

    private static string Items(string upper = "EFDLItem_PC_A_Upper.PC_A_Upper") => $"""
        CREATE TABLE Item ([PrimaryKey] INTEGER NOT NULL, [SecondaryKey] INTEGER NOT NULL, [Name] TEXT NOT NULL,
            [Model] TEXT NOT NULL, [Grade] INTEGER NOT NULL, [Scale] REAL NOT NULL, PRIMARY KEY ([PrimaryKey]));
        INSERT INTO Item VALUES (325600323, 0, 'item.upper', '{upper}', 1, 1.0),
            (335600323, 0, 'item.lower', 'EFDLItem_PC_A_Lower.PC_A_Lower', 1, 1.0),
            (100, 0, 'item.other', 'EFDLItem_Other.Other', 7, 2.5);
        WITH RECURSIVE n(v) AS (SELECT 1 UNION ALL SELECT v + 1 FROM n WHERE v < 40)
        INSERT INTO Item SELECT 1000 + v, 0, 'filler', printf('%.*c', 200, 'x'), 0, 0.0 FROM n;
        """;

    private static void TestCombinedWrite() => InWorkspace((efGame, data) =>
    {
        var archive = Path.Combine(efGame, "data2.lpk");
        CreateArchive(archive, data, Items());
        var original = File.ReadAllBytes(archive);
        var originalWritten = File.GetLastWriteTimeUtc(archive);
        var tables = new GameDataArchive(efGame, data, new Game());
        var languages = new GameLanguageService(efGame, data, new Game());
        var edits = new[]
        {
            Edit("Model", 325600323, "EFDLItem_PC_B_Upper.PC_B_Upper", "EFDLItem_PC_A_Upper.PC_A_Upper"),
            Edit("Scale", 100, 1.75, 2.5),
            Edit("Grade", 100, 6L, 7L),
        };

        tables.WriteAsync(new GameDataTarget(null, edits)).GetAwaiter().GetResult();
        var npc = Payload(archive, "EFTable_Npc.db");
        Assert(Cell(archive, "Item", "Model", 325600323) == "EFDLItem_PC_B_Upper.PC_B_Upper" &&
               Cell(archive, "Item", "Scale", 100) == "1.75" && Cell(archive, "Item", "Grade", 100) == "6",
            "the table edits were not written");
        AssertPagesMarked(archive, "EFTable_Item.db");
        Assert(npc.SequenceEqual(Payload(original, "EFTable_Npc.db")), "a database nobody changed was rewritten");

        // The Language tab keeps the mods' edits, and the mods keep the language.
        Assert(languages.ApplyAsync("Spanish").GetAwaiter().GetResult() == 2, "the language was not written");
        Assert(Cell(archive, "GameMsg_English", "MSG", "sys.welcome") == "¡Te damos la bienvenida a Lost Ark!" &&
               Cell(archive, "Item", "Model", 325600323) == "EFDLItem_PC_B_Upper.PC_B_Upper",
            "choosing a language dropped the table edits");
        AssertPagesMarked(archive, "EFTable_GameMsg.db");
        tables.WriteAsync(tables.Current() with { Edits = edits[..1] }).GetAwaiter().GetResult();
        Assert(languages.CurrentLanguage() == "Spanish" && Cell(archive, "Item", "Grade", 100) == "7" &&
               Cell(archive, "GameMsg_English", "MSG", "item.name") == "Túnica",
            "changing the table edits dropped the language or kept a removed edit");
        languages.RestoreAsync().GetAwaiter().GetResult();
        Assert(Cell(archive, "GameMsg_English", "MSG", "item.name") == "Robe" &&
               Cell(archive, "Item", "Model", 325600323) == "EFDLItem_PC_B_Upper.PC_B_Upper",
            "putting back the game's text dropped the table edits");

        tables.WriteAsync(GameDataTarget.GameOwn).GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(archive).SequenceEqual(original) && File.GetLastWriteTimeUtc(archive) == originalWritten,
            "the game's data2.lpk was not put back exactly");
        Assert(tables.LoadState() is null && tables.Current().IsGameOwn, "restoring did not clear Arkive's record");

        // A row the game does not have is refused, and the file is left alone.
        try
        {
            tables.WriteAsync(new GameDataTarget(null, [Edit("Model", 999, "x", "y")])).GetAwaiter().GetResult();
            throw new InvalidOperationException("an edit of a missing row was written");
        }
        catch (InvalidDataException)
        {
        }
        Assert(File.ReadAllBytes(archive).SequenceEqual(original), "a refused write changed data2.lpk");
    });

    private static void TestSqlImport() => InWorkspace((efGame, data) =>
    {
        var archive = Path.Combine(efGame, "data2.lpk");
        CreateArchive(archive, data, Items());
        var original = File.ReadAllBytes(archive);
        var tables = new GameDataArchive(efGame, data, new Game());
        var import = GameDataAuthoring.ImportSqlAsync(tables, """
            --File:EFTable_Item.db
            UPDATE Item
            SET Model = 'EFDLItem_PC_GDH_F_HR_03_Upper.PC_GDH_F_HR_03_Upper' -- the swap; a ; in a comment
            WHERE PrimaryKey = 325600323;
            update Item set Grade = 6, Scale = "3.5" where Grade = 7 or Name = 'it''s; not here';
            UPDATE Item SET Grade = 1 WHERE PrimaryKey = 1;
            """).GetAwaiter().GetResult();
        var cells = import.Edits.ToDictionary(edit => $"{edit.Column}:{GameDataValues.Describe(edit.Row["PrimaryKey"])}", edit => edit);
        Assert(import.Edits.Count == 3 &&
               GameDataValues.Describe(cells["Model:325600323"].Value) == "EFDLItem_PC_GDH_F_HR_03_Upper.PC_GDH_F_HR_03_Upper" &&
               GameDataValues.Describe(cells["Model:325600323"].Original) == "EFDLItem_PC_A_Upper.PC_A_Upper" &&
               GameDataValues.Describe(cells["Grade:100"].Value) == "6" && GameDataValues.Describe(cells["Scale:100"].Value) == "3.5",
            "the patch's cell changes were not found: " + string.Join(", ", cells.Keys));
        Assert(import.Notes.Count == 1 && import.Notes[0].Contains("changed no rows", StringComparison.Ordinal),
            "a statement that changed nothing was not noted");
        Assert(File.ReadAllBytes(archive).SequenceEqual(original), "importing a patch changed data2.lpk");

        foreach (var refused in new[]
                 {
                     "--File:EFTable_Item.db\nATTACH DATABASE 'x.db' AS x;",
                     "--File:EFTable_Item.db\nUPDATE Item SET Grade = 1; DELETE FROM Item;",
                     "--File:EFTable_Item.db\nVACUUM INTO 'copy.db';",
                     "--File:EFTable_Item.db\nUPDATE Item SET PrimaryKey = PrimaryKey + 1 WHERE PrimaryKey = 100;",
                     "UPDATE Item SET Grade = 1;",
                     "--File:..\\EFTable_Item.db\nUPDATE Item SET Grade = 1;",
                 })
        {
            try
            {
                GameDataAuthoring.ImportSqlAsync(tables, refused).GetAwaiter().GetResult();
                throw new InvalidOperationException("a patch that should be refused was imported: " + refused);
            }
            catch (InvalidDataException)
            {
            }
        }
        Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(archive)!, "copy.db")), "a refused patch wrote a file");
    });

    private static void TestModWorkflow() => InWorkspace((efGame, data) =>
    {
        var archive = Path.Combine(efGame, "data2.lpk");
        CreateArchive(archive, data, Items());
        var original = File.ReadAllBytes(archive);
        var packages = Path.Combine(efGame, "ReleasePC", "Packages");
        Directory.CreateDirectory(packages);
        var environment = ArkiveModTestFixture.CreateEnvironment(data);
        var apply = new ArkiveModApplyService(
            ArkiveModTestFixture.Context(environment, packages) with { GameDataRoot = data, GameProcessGuard = new Game() },
            ArkiveModTestFixture.CreateMaterializer(environment), ArkiveModTestFixture.Composer());
        var tables = new GameDataArchive(efGame, data, new Game());

        var swap = GameDataAuthoring.SaveModAsync(environment.Library, null, new ArkiveModMetadata("Outfit swap", "1.0.0"),
            [Edit("Model", 325600323, "EFDLItem_PC_B_Upper.PC_B_Upper", "EFDLItem_PC_A_Upper.PC_A_Upper")]).GetAwaiter().GetResult();
        var grades = GameDataAuthoring.SaveModAsync(environment.Library, null, new ArkiveModMetadata("Grades", "1.0.0"),
            [Edit("Grade", 100, 6L, 7L), Edit("Model", 325600323, "EFDLItem_PC_C_Upper.PC_C_Upper", "EFDLItem_PC_A_Upper.PC_A_Upper")])
            .GetAwaiter().GetResult();
        var read = ArkiveModFileService.ReadAsync(environment.Library.GetModFilePath(swap)).GetAwaiter().GetResult();
        Assert(read.Manifest.FormatVersion == ArkiveModFileFormat.GameDataFormatVersion && read.Manifest.Assets.Count == 0 &&
               read.Manifest.GameData?.Edits.Count == 1, "the Game Data mod file was not written as format 2");

        environment.Library.SetEnabledAsync(swap.ModId, true).GetAwaiter().GetResult();
        environment.Library.SetEnabledAsync(grades.ModId, true).GetAwaiter().GetResult();
        var tied = apply.PlanAsync().GetAwaiter().GetResult();
        Assert(!tied.CanApply && tied.BlockingProblems.Any(problem => problem.Contains("same priority", StringComparison.Ordinal)),
            "two mods writing different values at one priority were not stopped");
        environment.Library.SetPriorityAsync(swap.ModId, 10).GetAwaiter().GetResult();

        var plan = apply.PlanAsync().GetAwaiter().GetResult();
        var row = plan.Packages.Single(package => package.PhysicalPackageName == ArkiveModApplyService.GameDataPackageName);
        Assert(plan.CanApply && row.Action == ArkiveModPackageAction.Rebuild && row.Contributors.Count == 2,
            $"the plan does not write the table edits: {row.Action} {row.Reason}");
        var result = apply.ApplyAsync(plan.Fingerprint).GetAwaiter().GetResult();
        Assert(result.Succeeded && Cell(archive, "Item", "Model", 325600323) == "EFDLItem_PC_B_Upper.PC_B_Upper" &&
               Cell(archive, "Item", "Grade", 100) == "6", "applying did not write the winning values");
        var library = environment.Library.LoadAsync().GetAwaiter().GetResult();
        var snapshot = ArkiveModEditSnapshot.LoadAsync(environment.Edits).GetAwaiter().GetResult();
        var statuses = ArkiveModApplyService.DescribeAsync(environment.Library, library, snapshot, installedGameData: tables.Current())
            .GetAwaiter().GetResult();
        Assert(statuses.Single(status => status.Mod.ModId == swap.ModId) is { InGame: true, State: "Applied" } &&
               statuses.Single(status => status.Mod.ModId == grades.ModId) is { InGame: true, OverriddenCount: 1 },
            "the mods' states do not show the tables in game: " + string.Join(", ", statuses.Select(status => status.State)));
        Assert(apply.PlanAsync().GetAwaiter().GetResult().Packages.Single(package => package.PhysicalPackageName == "data2.lpk").Action ==
               ArkiveModPackageAction.None, "an applied plan still wants to change data2.lpk");

        // A game update changes a cell the mod was made against: the plan stops and the mod's update keeps its value.
        var updated = Path.Combine(data, "update");
        Directory.CreateDirectory(updated);
        CreateArchive(archive, updated, Items(upper: "EFDLItem_PC_A2_Upper.PC_A2_Upper"));
        File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddMinutes(5));
        var patched = File.ReadAllBytes(archive);
        var stale = apply.PlanAsync().GetAwaiter().GetResult();
        Assert(!stale.CanApply && stale.ModsNeedingClientUpdate.SetEquals([swap.ModId]) && tables.GameUpdated(),
            "the game update to a changed cell was not noticed");
        foreach (var mod in new[] { swap, grades })
        {
            var outcomes = apply.UpdateModForCurrentClientAsync(mod.ModId).GetAwaiter().GetResult();
            Assert(outcomes.Single() is { Succeeded: true, Changed: true }, "the mod was not updated: " + outcomes.Single().Message);
        }
        library = environment.Library.LoadAsync().GetAwaiter().GetResult();
        Assert(library.Find(swap.ModId)!.Manifest.GameData!.Edits.Single() is var rebased &&
               GameDataValues.Describe(rebased.Original) == "EFDLItem_PC_A2_Upper.PC_A2_Upper" &&
               GameDataValues.Describe(rebased.Value) == "EFDLItem_PC_B_Upper.PC_B_Upper", "the update did not keep the mod's value");
        plan = apply.PlanAsync().GetAwaiter().GetResult();
        Assert(plan.CanApply && apply.ApplyAsync(plan.Fingerprint).GetAwaiter().GetResult().Succeeded &&
               Cell(archive, "Item", "Model", 325600323) == "EFDLItem_PC_B_Upper.PC_B_Upper", "the updated mods were not applied");

        // Turning every Game Data mod off puts back the updated game's own file.
        environment.Library.SetEnabledAsync(swap.ModId, false).GetAwaiter().GetResult();
        environment.Library.SetEnabledAsync(grades.ModId, false).GetAwaiter().GetResult();
        plan = apply.PlanAsync().GetAwaiter().GetResult();
        Assert(plan.Packages.Single(package => package.PhysicalPackageName == "data2.lpk").Action == ArkiveModPackageAction.Restore &&
               apply.ApplyAsync(plan.Fingerprint).GetAwaiter().GetResult().Succeeded &&
               File.ReadAllBytes(archive).SequenceEqual(patched), "disabling the mods did not put back the game's data2.lpk");
        Assert(!File.ReadAllBytes(archive).SequenceEqual(original) && apply.PlanAsync().GetAwaiter().GetResult().Packages
            .All(package => package.PhysicalPackageName != "data2.lpk"), "the plan still lists data2.lpk with nothing to do");
    });

    private static GameDataCellEdit Edit(string column, long key, object value, object original) => new(
        "EFTable_Item.db", "Item", new Dictionary<string, JsonElement> { ["PrimaryKey"] = GameDataValues.From(key) }, column,
        GameDataValues.From(value), GameDataValues.From(original));

    private static string Cell(string archive, string table, string column, object key)
    {
        var database = table.StartsWith("GameMsg", StringComparison.Ordinal) ? "EFTable_GameMsg.db" : "EFTable_Item.db";
        var path = archive + ".check.db";
        File.WriteAllBytes(path, Decrypted(archive, database));
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT [{column}] FROM [{table}] WHERE {(key is string ? "[KEY]" : "PrimaryKey")} = $key";
            command.Parameters.AddWithValue("$key", key);
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The game's databases end every page with 16 bytes of 0x10, and the game cannot read a page that does not.
    private static void AssertPagesMarked(string archive, string name)
    {
        var database = Decrypted(archive, name);
        var pages = database.Length / PageSize;
        Assert(database.Length % PageSize == 0 && database[20] == Reserved && pages > 2 && Enumerable.Range(1, pages)
                .All(page => database.AsSpan(page * PageSize - Reserved, Reserved).IndexOfAnyExcept((byte)Reserved) < 0),
            $"a page of the changed {name} does not end with the game's marker");
    }

    private static byte[] Decrypted(string archive, string name)
    {
        var payload = Payload(File.ReadAllBytes(archive), name, out var size);
        using var aes = Cipher(name);
        var result = new byte[payload.Length];
        for (var offset = 0; offset < payload.Length; offset += 0x400)
        {
            var length = Math.Min(0x400, payload.Length - offset);
            aes.DecryptCbc(payload.AsSpan(offset, length), new byte[16], result.AsSpan(offset, length), PaddingMode.None);
        }
        return result[..size];
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
            if (entry == TableFolder + name)
            {
                size = BinaryPrimitives.ReadInt32LittleEndian(record[^12..]);
                return archive.AsSpan((int)offset, encrypted).ToArray();
            }
            offset += encrypted;
        }
        throw new InvalidDataException(name + " is not in the archive.");
    }

    // data2.lpk with the game text, the item table and an NPC table nobody changes, each encrypted with its own key.
    private static void CreateArchive(string path, string scratch, string items)
    {
        var npc = new byte[3000];
        new Random(7).NextBytes(npc);
        (string Name, byte[] Data)[] entries =
        [
            ("EFTable_GameMsg.db", Database(scratch, Text)), ("EFTable_Item.db", Database(scratch, items)), ("EFTable_Npc.db", npc),
        ];
        var table = new byte[528 * entries.Length];
        var payloads = new List<byte[]>();
        for (var index = 0; index < entries.Length; index++)
        {
            var record = table.AsSpan(index * 528, 528);
            var name = Encoding.UTF8.GetBytes(TableFolder + entries[index].Name + '\0');
            BinaryPrimitives.WriteInt32LittleEndian(record, name.Length);
            name.CopyTo(record[4..]);
            var payload = Encrypt(entries[index].Name, entries[index].Data);
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

    // The per-database key: the name after its last underscore (EFTable_Item.db -> Item).
    private static Aes Cipher(string name)
    {
        var digest = MD5.HashData(Encoding.Unicode.GetBytes(name[(name.LastIndexOf('_') + 1)..].Split('.')[0]));
        var mixed = Convert.FromHexString("1069D88738C5C75F82B44A1F0A382768");
        for (var index = 0; index < 16; index++) mixed[index] ^= digest[15 - index];
        var aes = Aes.Create();
        aes.Key = SHA256.HashData(Encoding.ASCII.GetBytes(Convert.ToHexString(mixed).ToLowerInvariant()));
        return aes;
    }

    private static byte[] Encrypt(string name, byte[] data)
    {
        using var aes = Cipher(name);
        var result = new byte[(data.Length + 15) / 16 * 16];
        data.CopyTo(result, 0);
        for (var offset = 0; offset < result.Length; offset += 0x400)
        {
            var length = Math.Min(0x400, result.Length - offset);
            aes.EncryptCbc(result.AsSpan(offset, length), new byte[16], result.AsSpan(offset, length), PaddingMode.None);
        }
        return result;
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
        for (var end = PageSize; end <= bytes.Length; end += PageSize) bytes.AsSpan(end - Reserved, Reserved).Fill(Reserved);
        return bytes;
    }

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_file_control")]
    private static extern int FileControl(IntPtr database, byte[] name, int operation, IntPtr argument);

    private static void InWorkspace(Action<string, string> test)
    {
        var root = Path.Combine(fixtureDirectory, "game-data-" + Guid.NewGuid().ToString("N"));
        var efGame = Path.Combine(root, "EFGame");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(efGame);
        Directory.CreateDirectory(data);
        try
        {
            test(efGame, data);
            var work = Path.Combine(data, "game-data");
            Assert(!Directory.Exists(work) || !Directory.EnumerateDirectories(work).Any(), "a working folder was left behind");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
