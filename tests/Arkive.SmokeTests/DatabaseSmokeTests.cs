using Arkive.Core.Databases;
using Microsoft.Data.Sqlite;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Arkive.SmokeTests;

internal static class DatabaseSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("builds game databases without external tools", TestBuild),
        new("rejects malformed game database archives without replacing mappings", TestFailures),
        new("cancels game database builds and cleans temporary databases", TestCancellation),
        new("selects canonical game database tables instead of alternate namespaces", TestNamespaces),
    ];

    private static void TestBuild() => InWorkspace(root =>
    {
        var archive = CreateArchive(root);
        var hash = SHA256.HashData(File.ReadAllBytes(archive));
        var stages = new List<GameDatabaseBuildProgress>();
        var output = Path.Combine(root, "mapping.json");
        var result = Build(archive, output, new ProgressSink(stages.Add));
        Assert(result.DatabaseCount == 5 && result.AssociationCount == 3 && result.EntityCount == 2 && result.CustomizationCount == 1,
            "built database mapping has incorrect counts");
        var index = GameAssetMetadataIndex.Load(output);
        Assert(index.Associations.Any(item => item.DisplayName == "Fixture Boss" && item.ObjectName == "fixture_sk"),
            "localized markup or wrapped asset reference was not resolved");
        Assert(index.Associations.Any(item => item.DisplayName == "Fixture Class" && item.EntityType == "PC"),
            "playable class messages were not resolved");
        Assert(!index.Associations.Any(item => item.ObjectName == "ambiguous_shared"), "ambiguous object references were retained");
        Assert(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(archive))), "database builder changed the source archive");
        Assert(stages.Any(item => item.Stage == "Decrypting databases") && stages.Last().Stage == "Complete", "database build progress was not reported");
        Assert(!Directory.EnumerateDirectories(root, ".database-build-*").Any(), "decrypted database files were retained");
        var german = new GameDatabaseBuilder().BuildAsync(archive, output, "German").GetAwaiter().GetResult();
        Assert(german.Language == "German" && GameAssetMetadataIndex.Load(output).Associations.Any(item => item.DisplayName == "Fixture Gegner"),
            "selected database language was ignored");
    });

    private static void TestFailures() => InWorkspace(root =>
    {
        var archive = CreateArchive(root);
        var output = Path.Combine(root, "mapping.json");
        File.WriteAllText(output, "previous mapping");
        Refuses(() => new GameDatabaseBuilder().BuildAsync(archive, output, "English\";DROP TABLE").GetAwaiter().GetResult());
        Refuses(() => new GameDatabaseBuilder().BuildAsync(archive, output, "MissingLanguage").GetAwaiter().GetResult());
        Refuses(() => Build(archive, archive));
        var truncated = Path.Combine(root, "truncated.lpk");
        File.WriteAllBytes(truncated, File.ReadAllBytes(archive)[..^16]);
        Refuses(() => Build(truncated, output));
        var invalidCount = Path.Combine(root, "invalid-count.lpk");
        File.WriteAllBytes(invalidCount, [255, 255, 255, 127]);
        Refuses(() => Build(invalidCount, output));
        var corrupt = Path.Combine(root, "corrupt.lpk");
        var bytes = File.ReadAllBytes(archive);
        bytes[4] ^= 0x80;
        File.WriteAllBytes(corrupt, bytes);
        Refuses(() => Build(corrupt, output));
        var payload = Path.Combine(root, "bad-payload.lpk");
        bytes = File.ReadAllBytes(archive);
        bytes[BinaryPrimitives.ReadInt32LittleEndian(bytes) * 528 + 8] ^= 0x80;
        File.WriteAllBytes(payload, bytes);
        Refuses(() => Build(payload, output));
        Assert(File.ReadAllText(output) == "previous mapping", "a failed database build replaced the previous mapping");
        Assert(!Directory.EnumerateDirectories(root, ".database-build-*").Any(), "a failed database build retained temporary databases");
    });

    private static void TestCancellation() => InWorkspace(root =>
    {
        var archive = CreateArchive(root);
        var output = Path.Combine(root, "mapping.json");
        File.WriteAllText(output, "previous mapping");
        using var cancellation = new CancellationTokenSource();
        var progress = new ProgressSink(value => { if (value.Stage == "Building name mapping") cancellation.Cancel(); });
        try
        {
            new GameDatabaseBuilder().BuildAsync(archive, output, progress: progress, cancellationToken: cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("database cancellation was ignored");
        }
        catch (OperationCanceledException) { }
        Assert(File.ReadAllText(output) == "previous mapping", "cancelled database build replaced the previous mapping");
        Assert(!Directory.EnumerateDirectories(root, ".database-build-*").Any(), "cancelled database build retained decrypted files");
    });

    private static void TestNamespaces() => InWorkspace(root =>
    {
        var archive = CreateArchive(root, includeAlternate: true);
        Assert(Build(archive, Path.Combine(root, "mapping.json")).DatabaseCount == 5, "an alternate namespace shadowed the main tables");
        var duplicate = CreateArchive(root, duplicate: true);
        Refuses(() => Build(duplicate, Path.Combine(root, "mapping.json")));
    });

    private static GameDatabaseBuildResult Build(string archive, string output, IProgress<GameDatabaseBuildProgress>? progress = null) =>
        new GameDatabaseBuilder().BuildAsync(archive, output, progress: progress).GetAwaiter().GetResult();

    private static string CreateArchive(string root, bool includeAlternate = false, bool duplicate = false)
    {
        var databases = new Dictionary<string, byte[]>
        {
            ["EFTable_GameMsg.db"] = Database(root, """
                CREATE TABLE GameMsg_English (KEY TEXT, MSG TEXT);
                INSERT INTO GameMsg_English VALUES ('tip.fixture_boss','<b>Fixture &amp; Boss</b>'), ('tip.name.enum_playerclass_fixture','Fixture Class');
                UPDATE GameMsg_English SET MSG='<b>Fixture Boss</b>' WHERE KEY='tip.fixture_boss';
                WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<3000)
                INSERT INTO GameMsg_English SELECT 'padding.'||n, printf('%0800d',n) FROM seq;
                CREATE TABLE GameMsg_German (KEY TEXT, MSG TEXT);
                INSERT INTO GameMsg_German VALUES ('tip.fixture_boss','Fixture Gegner'), ('tip.name.enum_playerclass_fixture','Fixture Klasse');
                """),
            ["EFTable_PC.db"] = Database(root, """
                CREATE TABLE PC (PrimaryKey INTEGER, Name TEXT, BaseClass INTEGER, Creatable INTEGER, Released INTEGER, GenderType INTEGER, LookInfo TEXT);
                INSERT INTO PC VALUES (1,'Fixture',1,1,1,1,'fixture.pc_sk');
                """),
            ["EFTable_Npc.db"] = Database(root, """
                CREATE TABLE Npc (PrimaryKey INTEGER, Name TEXT, Model TEXT, Icon TEXT, Grade INTEGER, Type INTEGER, Function INTEGER, FunctionType INTEGER, TitleName TEXT);
                INSERT INTO Npc VALUES (2,'tip.fixture_boss','SkeletalMesh''fixture.fixture_sk''','fixture.fixture_icon',5,1,0,0,'');
                """),
            ["EFTable_Item.db"] = Database(root, """
                CREATE TABLE Item (PrimaryKey INTEGER, Name TEXT, Model TEXT);
                WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<251)
                INSERT INTO Item SELECT n,'tip.fixture_boss','ambiguous_shared' FROM seq;
                """),
            ["EFTable_CharacterCustomizing.db"] = Database(root, """
                CREATE TABLE CharacterCustomizing (PrimaryKey INTEGER, SecondaryKey INTEGER, SortOrder INTEGER, Object_Unit INTEGER, Object_String TEXT, Icon TEXT, IconIndex INTEGER, MenuName TEXT);
                INSERT INTO CharacterCustomizing VALUES (1,1,0,2,'fixture.fixture_face','','0','face');
                """),
        };
        var entries = databases.Select(pair => (Name: "\\EFGame_Extra\\ClientData\\TableData\\" + pair.Key, Data: pair.Value)).ToList();
        if (includeAlternate) entries.Insert(0, ("\\EFGame_Extra\\ClientData\\TableData\\jss\\EFTable_Item.db", databases["EFTable_Item.db"]));
        if (duplicate) entries.Add(entries[0]);
        var table = new byte[528 * entries.Count];
        var payloads = new List<byte[]>();
        for (var index = 0; index < entries.Count; index++)
        {
            var record = table.AsSpan(index * 528, 528);
            var name = Encoding.UTF8.GetBytes(entries[index].Name + '\0');
            BinaryPrimitives.WriteInt32LittleEndian(record, name.Length);
            name.CopyTo(record[4..]);
            var data = entries[index].Data;
            var payload = EncryptDatabase(data, Path.GetFileName(entries[index].Name.Replace('\\', '/')));
            BinaryPrimitives.WriteInt32LittleEndian(record[^12..], data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(record[^8..], payload.Length);
            payloads.Add(payload);
        }
        var cipher = new BlowfishEngine();
        cipher.Init(true, new KeyParameter(Encoding.ASCII.GetBytes("83657ea6ffa1e671375c689a2e99a598")));
        for (var offset = 0; offset < table.Length; offset += 8)
        {
            table.AsSpan(offset, 4).Reverse(); table.AsSpan(offset + 4, 4).Reverse();
            cipher.ProcessBlock(table, offset, table, offset);
            table.AsSpan(offset, 4).Reverse(); table.AsSpan(offset + 4, 4).Reverse();
        }
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".lpk");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(entries.Count); writer.Write(table); writer.Write(0);
        foreach (var payload in payloads) writer.Write(payload);
        return path;
    }

    private static byte[] Database(string root, string sql)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        return File.ReadAllBytes(path);
    }

    private static byte[] EncryptDatabase(byte[] data, string name)
    {
        var digest = MD5.HashData(Encoding.Unicode.GetBytes(name[(name.LastIndexOf('_') + 1)..].Split('.')[0]));
        var mixed = Convert.FromHexString("1069D88738C5C75F82B44A1F0A382768");
        for (var index = 0; index < 16; index++) mixed[index] ^= digest[15 - index];
        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(Encoding.ASCII.GetBytes(Convert.ToHexString(mixed).ToLowerInvariant()));
        var result = new byte[data.Length];
        for (var offset = 0; offset < data.Length; offset += 0x400)
            aes.EncryptCbc(data.AsSpan(offset, Math.Min(0x400, data.Length - offset)), new byte[16], result.AsSpan(offset), PaddingMode.None);
        return result;
    }

    private static void InWorkspace(Action<string> test)
    {
        var root = Path.Combine(fixtureDirectory, "database-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root); } finally { Directory.Delete(root, recursive: true); }
    }

    private static void Refuses(Action action)
    {
        try { action(); } catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException) { return; }
        throw new InvalidOperationException("invalid game database input was accepted");
    }

    private sealed class ProgressSink(Action<GameDatabaseBuildProgress> report) : IProgress<GameDatabaseBuildProgress>
    {
        public void Report(GameDatabaseBuildProgress value) => report(value);
    }
}
