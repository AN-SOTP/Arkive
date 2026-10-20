using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arkive.Core.Databases;
using Arkive.Core.Mods;
using Microsoft.Data.Sqlite;

namespace Arkive.Core.Languages;

/// <summary>One language the game's text can be shown in.</summary>
/// <param name="Name">The game's name for it (Spanish), as in its GameMsg_Spanish table.</param>
/// <param name="Title">A readable name (Español (Spanish)).</param>
/// <param name="Translated">English lines that have a translation in this language.</param>
/// <param name="Lines">English lines in the game's text.</param>
/// <param name="InGame">False when the game no longer ships this language and only Arkive's saved copy has it.</param>
public sealed record GameLanguage(string Name, string Title, int Translated, int Lines, bool InGame)
{
    public double Coverage => Lines == 0 ? 0 : Math.Min(1, (double)Translated / Lines);
}

/// <summary>What Arkive has changed: the game's English text in <paramref name="ArchivePath"/> now holds <paramref name="Language"/>.</summary>
public sealed record GameLanguageState(
    int Version, string Language, string ArchivePath, string OriginalSha256, DateTime OriginalWriteTimeUtc, string AppliedSha256,
    long AppliedLength, DateTime AppliedWriteTimeUtc, string BackupPath, int Lines, DateTimeOffset AppliedAt);

/// <summary>
/// The language of the game's text. Every line the game shows is in one database in data2.lpk
/// (EFTable_GameMsg.db), which has a table per language: GameMsg_English, GameMsg_Spanish and so on, keyed by the
/// same names. The game shows its English table (the other languages are being withdrawn), so Arkive writes the chosen
/// language's lines over the English ones; lines without a translation stay English. Translations are copied to
/// Arkive's data folder whenever the game's files change, so a language the game later drops can still be used.
/// The game's database is kept as a backup, and going back writes it in place again, giving the game's file exactly.
/// </summary>
public sealed class GameLanguageService
{
    public const string English = "English";
    public const string TextEntry = "EFGame_Extra/ClientData/TableData/EFTable_GameMsg.db";

    private const int StateVersion = 1;
    private const int CatalogVersion = 1;
    private const string TablePrefix = "GameMsg_";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly (string Name, string Title)[] Known =
    [
        (English, "English"),
        ("Spanish", "Español (Spanish)"),
        ("French", "Français (French)"),
        ("German", "Deutsch (German)"),
    ];

    private readonly string _directory;
    private readonly string _statePath;
    private readonly string _catalogPath;
    private readonly string _savedPath;
    private readonly string _backupDirectory;
    private readonly IModDeploymentProcessGuard _processGuard;

    /// <param name="efGameDirectory">The game's EFGame folder (the one holding data2.lpk).</param>
    /// <param name="dataRoot">Arkive's data folder, for the saved translations, the backup and the record of what was changed.</param>
    public GameLanguageService(string efGameDirectory, string dataRoot, IModDeploymentProcessGuard processGuard)
    {
        ArchivePath = Path.Combine(Path.GetFullPath(efGameDirectory), "data2.lpk");
        _directory = Path.Combine(dataRoot, "languages");
        _statePath = Path.Combine(_directory, "state.json");
        _catalogPath = Path.Combine(_directory, "saved-text.json");
        _savedPath = Path.Combine(_directory, "saved-text.db.gz");
        _backupDirectory = Path.Combine(dataRoot, "backups", "languages");
        _processGuard = processGuard;
    }

    /// <summary>The game's data2.lpk.</summary>
    public string ArchivePath { get; }

    public static string Title(string language) =>
        Known.FirstOrDefault(item => item.Name.Equals(language, StringComparison.OrdinalIgnoreCase)).Title ?? language;

    /// <summary>English, then every language Arkive has text for, in the order the game added them.</summary>
    public IReadOnlyList<GameLanguage> Languages()
    {
        var catalog = LoadCatalog();
        var lines = catalog?["Lines"]?.GetValue<int>() ?? 0;
        var languages = new List<GameLanguage> { new(English, Title(English), lines, lines, true) };
        if (catalog?["Languages"] is JsonArray saved)
            languages.AddRange(saved.OfType<JsonObject>()
                .Select(item => new GameLanguage(item["Name"]!.GetValue<string>(), Title(item["Name"]!.GetValue<string>()),
                    item["Translated"]!.GetValue<int>(), lines, item["InGame"]!.GetValue<bool>()))
                .Where(language => language.Translated > 0)
                .OrderBy(language => Order(language.Name)).ThenBy(language => language.Name, StringComparer.OrdinalIgnoreCase));
        return languages;
    }

    private static int Order(string language)
    {
        var index = Array.FindIndex(Known, item => item.Name.Equals(language, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? Known.Length : index;
    }

    /// <summary>When the translations were last copied from the game, if ever.</summary>
    public DateTimeOffset? SavedAt() => LoadCatalog()?["SavedAt"]?.GetValue<DateTimeOffset>();

    /// <summary>
    /// True when the game's text has changed since Arkive last copied its translations (a new install or a game
    /// update), so <see cref="SaveAsync"/> should run before anything else.
    /// </summary>
    public bool NeedsSaving()
    {
        if (!File.Exists(ArchivePath)) return false;
        var (length, written) = Stamp(ArchivePath);
        if (LoadState() is { } state && state.AppliedLength == length && state.AppliedWriteTimeUtc == written) return false;
        var catalog = LoadCatalog();
        return catalog?["ArchiveLength"]?.GetValue<long>() != length || catalog["ArchiveWriteTimeUtc"]?.GetValue<DateTime>() != written;
    }

    /// <summary>
    /// Copies every translation in the game's text to Arkive's data folder, keeping lines saved earlier that the game
    /// no longer has.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ArchivePath)) throw new FileNotFoundException("The game's data2.lpk was not found.", ArchivePath);
        if (LoadState() is { } applied && !GameUpdated(applied))
            throw new InvalidOperationException("Put the game's own text back before saving its translations.");
        var stamp = Stamp(ArchivePath);
        using var work = new WorkFolder(_directory);
        var game = work.File("game.db");
        var saved = work.File("saved.db");
        LostArkDatabaseArchive.Extract(ArchivePath, TextEntry, game, cancellationToken);
        if (File.Exists(_savedPath)) await DecompressAsync(_savedPath, saved, cancellationToken).ConfigureAwait(false);

        var lines = 0;
        var catalog = new JsonArray();
        using (var connection = Open(saved, readOnly: false))
        {
            Execute(connection, $"ATTACH DATABASE {Quote(game)} AS game");
            var gameTables = Tables(connection, "game");
            if (!gameTables.Contains(English)) throw new InvalidDataException("The game's text has no English table.");
            lines = Count(connection, $"SELECT COUNT(*) FROM game.{TablePrefix}{English} WHERE MSG <> ''");
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var language in gameTables.Where(name => name != English))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Execute(connection, $"CREATE TABLE IF NOT EXISTS main.[{language}] (KEY TEXT NOT NULL PRIMARY KEY, MSG TEXT NOT NULL)", transaction);
                    Execute(connection, $"INSERT OR REPLACE INTO main.[{language}] (KEY, MSG) " +
                                        $"SELECT KEY, MSG FROM game.[{TablePrefix}{language}] WHERE MSG <> ''", transaction);
                }
                transaction.Commit();
            }
            foreach (var language in Tables(connection, "main", prefix: string.Empty))
            {
                var translated = Count(connection, $"SELECT COUNT(*) FROM game.{TablePrefix}{English} e JOIN main.[{language}] t ON t.KEY = e.KEY WHERE e.MSG <> ''");
                var inGame = gameTables.Contains(language) &&
                             Count(connection, $"SELECT COUNT(*) FROM (SELECT 1 FROM game.[{TablePrefix}{language}] WHERE MSG <> '' LIMIT 1)") > 0;
                catalog.Add(new JsonObject { ["Name"] = language, ["Translated"] = translated, ["InGame"] = inGame });
            }
            Execute(connection, "DETACH DATABASE game");
        }
        cancellationToken.ThrowIfCancellationRequested();
        await CompressAsync(saved, _savedPath, cancellationToken).ConfigureAwait(false);
        WriteJson(_catalogPath, new JsonObject
        {
            ["Version"] = CatalogVersion,
            ["ArchiveLength"] = stamp.Length,
            ["ArchiveWriteTimeUtc"] = stamp.WrittenUtc,
            ["SavedAt"] = DateTimeOffset.UtcNow,
            ["Lines"] = lines,
            ["Languages"] = catalog,
        });
    }

    public GameLanguageState? LoadState()
    {
        if (!File.Exists(_statePath)) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(_statePath));
            return node?["Version"]?.GetValue<int>() == StateVersion ? node.Deserialize<GameLanguageState>(JsonOptions) : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True when the game has replaced Arkive's text since it was written (an update or a file check).</summary>
    public bool GameUpdated() => LoadState() is { } state && GameUpdated(state);

    private bool GameUpdated(GameLanguageState state)
    {
        if (!File.Exists(ArchivePath)) return true;
        var (length, written) = Stamp(ArchivePath);
        return length != state.AppliedLength || written != state.AppliedWriteTimeUtc;
    }

    /// <summary>The language the game will show: Arkive's choice while it is installed, else English.</summary>
    public string CurrentLanguage() => LoadState() is { } state && !GameUpdated(state) ? state.Language : English;

    /// <summary>Writes <paramref name="language"/>'s lines over the game's English text.</summary>
    /// <returns>How many lines are now in <paramref name="language"/>.</returns>
    public async Task<int> ApplyAsync(string language, CancellationToken cancellationToken = default)
    {
        RefuseWhileRunning();
        await RestoreAsync(cancellationToken).ConfigureAwait(false);
        if (language.Equals(English, StringComparison.OrdinalIgnoreCase)) return 0;
        if (NeedsSaving()) await SaveAsync(cancellationToken).ConfigureAwait(false);
        var chosen = Languages().FirstOrDefault(item => item.Name.Equals(language, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException($"Arkive has no {Title(language)} text.");

        using var work = new WorkFolder(_directory);
        var text = work.File("text.db");
        var saved = work.File("saved.db");
        LostArkDatabaseArchive.Extract(ArchivePath, TextEntry, text, cancellationToken);

        // The game's database is kept first, so the change can always be undone.
        var original = await HashAsync(ArchivePath, cancellationToken).ConfigureAwait(false);
        var originalWritten = Stamp(ArchivePath).WrittenUtc;
        Directory.CreateDirectory(_backupDirectory);
        var backup = Path.Combine(_backupDirectory, $"GameMsg-{original[..16]}.db.gz");
        if (!File.Exists(backup)) await CompressAsync(text, backup, cancellationToken).ConfigureAwait(false);

        await DecompressAsync(_savedPath, saved, cancellationToken).ConfigureAwait(false);
        int lines;
        using (var connection = Open(text, readOnly: false))
        {
            Execute(connection, $"ATTACH DATABASE {Quote(saved)} AS saved");
            using (var transaction = connection.BeginTransaction())
            {
                lines = Execute(connection,
                    $"UPDATE {TablePrefix}{English} SET MSG = t.MSG FROM saved.[{chosen.Name}] t " +
                    $"WHERE t.KEY = {TablePrefix}{English}.KEY AND {TablePrefix}{English}.MSG <> ''", transaction);
                transaction.Commit();
            }
            Execute(connection, "DETACH DATABASE saved");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var staged = ArchivePath + ".arkive-new";
        File.Delete(staged);
        try
        {
            LostArkDatabaseArchive.Replace(ArchivePath, TextEntry, text, staged, cancellationToken);
            var check = work.File("check.db");
            LostArkDatabaseArchive.Extract(staged, TextEntry, check, cancellationToken);
            if (!(await HashAsync(check, cancellationToken).ConfigureAwait(false))
                    .Equals(await HashAsync(text, cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The rewritten game text did not read back the same.");
            var applied = await HashAsync(staged, cancellationToken).ConfigureAwait(false);
            RefuseWhileRunning();
            File.Move(staged, ArchivePath, overwrite: true);
            var (length, written) = Stamp(ArchivePath);
            WriteJson(_statePath, JsonSerializer.SerializeToNode(new GameLanguageState(StateVersion, chosen.Name, ArchivePath, original, originalWritten, applied,
                length, written, backup, lines, DateTimeOffset.UtcNow), JsonOptions)!);
        }
        finally
        {
            File.Delete(staged);
        }
        return lines;
    }

    /// <summary>Puts the game's own text back (only while Arkive's change is still installed).</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        RefuseWhileRunning();
        if (LoadState() is not { } state)
        {
            if (File.Exists(_statePath)) File.Delete(_statePath);
            return;
        }
        if (!GameUpdated(state) &&
            (await HashAsync(ArchivePath, cancellationToken).ConfigureAwait(false)).Equals(state.AppliedSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(state.BackupPath))
                throw new InvalidDataException("Arkive's copy of the game's text is missing. Verify the game files in Steam to put the original back.");
            using var work = new WorkFolder(_directory);
            var text = work.File("text.db");
            await DecompressAsync(state.BackupPath, text, cancellationToken).ConfigureAwait(false);
            var restoring = ArchivePath + ".arkive-restore";
            File.Delete(restoring);
            try
            {
                LostArkDatabaseArchive.Replace(ArchivePath, TextEntry, text, restoring, cancellationToken);
                if (!(await HashAsync(restoring, cancellationToken).ConfigureAwait(false)).Equals(state.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Arkive could not rebuild the game's own data2.lpk exactly. Verify the game files in Steam to put the original back.");
                RefuseWhileRunning();
                File.Move(restoring, ArchivePath, overwrite: true);
                // The game's own date too, so the file is just as it was and its translations need no saving again.
                File.SetLastWriteTimeUtc(ArchivePath, state.OriginalWriteTimeUtc);
            }
            finally
            {
                File.Delete(restoring);
            }
        }
        File.Delete(_statePath);
    }

    private void RefuseWhileRunning()
    {
        var running = _processGuard.FindBlockingProcesses();
        if (running.Count > 0)
            throw new InvalidOperationException($"Close Lost Ark first: {string.Join(", ", running)} is running.");
    }

    private JsonNode? LoadCatalog()
    {
        try
        {
            var node = File.Exists(_catalogPath) ? JsonNode.Parse(File.ReadAllText(_catalogPath)) : null;
            return node?["Version"]?.GetValue<int>() == CatalogVersion && File.Exists(_savedPath) ? node : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static void WriteJson(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, node.ToJsonString(JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static (long Length, DateTime WrittenUtc) Stamp(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static int Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.CommandTimeout = 0;
        return command.ExecuteNonQuery();
    }

    private static int Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    // Language names from a schema's tables: GameMsg_Spanish -> Spanish (or every table, with no prefix).
    private static List<string> Tables(SqliteConnection connection, string schema, string prefix = TablePrefix)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM {schema}.sqlite_master WHERE type = 'table'";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var language = name[prefix.Length..];
            if (language.Length is > 0 and <= 32 && language.All(char.IsAsciiLetter)) names.Add(language);
        }
        return names;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static async Task CompressAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        await using (var input = File.OpenRead(source))
        await using (var output = File.Create(temporary))
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            await input.CopyToAsync(gzip, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, destination, overwrite: true);
    }

    private static async Task DecompressAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(source);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        await using var output = File.Create(destination);
        await gzip.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>A folder for the databases being worked on, deleted afterwards.</summary>
    private sealed class WorkFolder : IDisposable
    {
        private readonly string _path;

        public WorkFolder(string parent)
        {
            _path = Path.Combine(parent, "work-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_path);
        }

        public string File(string name) => Path.Combine(_path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
