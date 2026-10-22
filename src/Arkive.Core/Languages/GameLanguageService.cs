using System.Text.Json;
using System.Text.Json.Nodes;
using Arkive.Core.GameData;
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

/// <summary>The language Arkive last wrote over the game's English text.</summary>
public sealed record GameLanguageState(string Language, int Lines, DateTimeOffset AppliedAt);

/// <summary>
/// The language of the game's text. Every line the game shows is in one database in data2.lpk
/// (EFTable_GameMsg.db), which has a table per language: GameMsg_English, GameMsg_Spanish and so on, keyed by the
/// same names. The game shows its English table (the other languages are being withdrawn), so Arkive writes the chosen
/// language's lines over the English ones; lines without a translation stay English. Translations are copied to
/// Arkive's data folder whenever the game's files change, so a language the game later drops can still be used.
/// data2.lpk is written by <see cref="GameDataArchive"/>, which keeps Game Data mods' edits alongside the language.
/// </summary>
public sealed class GameLanguageService
{
    public const string English = "English";
    public const string TextDatabase = "EFTable_GameMsg.db";
    public const string TextEntry = GameDataValues.TableFolder + TextDatabase;

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
    private readonly string _catalogPath;
    private readonly string _savedPath;
    private readonly GameDataArchive _archive;

    /// <param name="efGameDirectory">The game's EFGame folder (the one holding data2.lpk).</param>
    /// <param name="dataRoot">Arkive's data folder, for the saved translations, the backup and the record of what was changed.</param>
    public GameLanguageService(string efGameDirectory, string dataRoot, IModDeploymentProcessGuard processGuard)
    {
        _archive = new GameDataArchive(efGameDirectory, dataRoot, processGuard);
        _directory = Path.Combine(dataRoot, "languages");
        _catalogPath = Path.Combine(_directory, "saved-text.json");
        _savedPath = Path.Combine(_directory, "saved-text.db.gz");
    }

    /// <summary>The game's data2.lpk.</summary>
    public string ArchivePath => _archive.ArchivePath;

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
        var catalog = LoadCatalog();
        if (catalog is null) return true;
        if (catalog["GameVersion"]?.GetValue<string>() is { } version) return version != _archive.GameVersion();
        // Catalogs saved before Game Data mods name the game's own file by its size and date.
        var (length, written) = _archive.LoadState() is { } state
            ? (state.OriginalLength, state.OriginalWriteTimeUtc)
            : GameDataArchive.Stamp(ArchivePath);
        return catalog["ArchiveLength"]?.GetValue<long>() != length || catalog["ArchiveWriteTimeUtc"]?.GetValue<DateTime>() != written;
    }

    /// <summary>
    /// Copies every translation in the game's text to Arkive's data folder, keeping lines saved earlier that the game
    /// no longer has.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ArchivePath)) throw new FileNotFoundException("The game's data2.lpk was not found.", ArchivePath);
        var version = _archive.GameVersion();
        using var work = new GameDataArchive.WorkFolder(_directory);
        var game = work.File("game.db");
        var saved = work.File("saved.db");
        await _archive.ExtractGameDatabaseAsync(TextDatabase, game, cancellationToken).ConfigureAwait(false);
        if (File.Exists(_savedPath)) await GameDataArchive.DecompressAsync(_savedPath, saved, cancellationToken).ConfigureAwait(false);

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
        await GameDataArchive.CompressAsync(saved, _savedPath, cancellationToken).ConfigureAwait(false);
        WriteJson(_catalogPath, new JsonObject
        {
            ["Version"] = CatalogVersion,
            ["GameVersion"] = version,
            ["SavedAt"] = DateTimeOffset.UtcNow,
            ["Lines"] = lines,
            ["Languages"] = catalog,
        });
    }

    /// <summary>The language Arkive last chose for the game's text, even when a game update has since replaced it.</summary>
    public GameLanguageState? LoadState() =>
        _archive.LastWrite() is { Language: { } language } state ? new GameLanguageState(language, state.LanguageLines, state.AppliedAt) : null;

    /// <summary>True when the game has replaced Arkive's text since it was written (an update or a file check).</summary>
    public bool GameUpdated() => LoadState() is not null && _archive.GameUpdated();

    /// <summary>The language the game will show: Arkive's choice while it is installed, else English.</summary>
    public string CurrentLanguage() => _archive.LoadState()?.Language ?? English;

    /// <summary>Writes <paramref name="language"/>'s lines over the game's English text, keeping Game Data mods' edits.</summary>
    /// <returns>How many lines are now in <paramref name="language"/>.</returns>
    public async Task<int> ApplyAsync(string language, CancellationToken cancellationToken = default)
    {
        _archive.RefuseWhileRunning();
        if (language.Equals(English, StringComparison.OrdinalIgnoreCase))
        {
            await RestoreAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        if (NeedsSaving()) await SaveAsync(cancellationToken).ConfigureAwait(false);
        var chosen = Languages().FirstOrDefault(item => item.Name.Equals(language, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException($"Arkive has no {Title(language)} text.");
        var result = await _archive.WriteAsync(_archive.Current() with { Language = chosen.Name }, cancellationToken).ConfigureAwait(false);
        return result.LanguageLines;
    }

    /// <summary>Puts the game's own text back, keeping Game Data mods' edits.</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        _archive.RefuseWhileRunning();
        var current = _archive.Current();
        if (current.Language is null) return;
        await _archive.WriteAsync(current with { Language = null }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="language"/>'s saved lines over the English table of the game text database at
    /// <paramref name="database"/>, leaving lines without a translation as they are.
    /// </summary>
    /// <returns>How many lines changed language.</returns>
    internal static async Task<int> WriteTextAsync(string savedTextPath, string database, string language, CancellationToken cancellationToken)
    {
        var saved = database + ".saved-text";
        try
        {
            await GameDataArchive.DecompressAsync(savedTextPath, saved, cancellationToken).ConfigureAwait(false);
            using var connection = Open(database, readOnly: false);
            Execute(connection, $"ATTACH DATABASE {Quote(saved)} AS saved");
            if (!Tables(connection, "saved", prefix: string.Empty).Contains(language))
                throw new InvalidOperationException($"Arkive has no {Title(language)} text.");
            int lines;
            using (var transaction = connection.BeginTransaction())
            {
                lines = Execute(connection,
                    $"UPDATE {TablePrefix}{English} SET MSG = t.MSG FROM saved.[{language}] t " +
                    $"WHERE t.KEY = {TablePrefix}{English}.KEY AND {TablePrefix}{English}.MSG <> ''", transaction);
                transaction.Commit();
            }
            Execute(connection, "DETACH DATABASE saved");
            return lines;
        }
        finally
        {
            File.Delete(saved);
        }
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
}
