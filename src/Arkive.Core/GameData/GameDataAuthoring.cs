using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arkive.Core.Mods;
using Microsoft.Data.Sqlite;

namespace Arkive.Core.GameData;

/// <summary>What reading a LostArkPatcher-style .sql patch found: the cell changes, and lines it could not use.</summary>
public sealed record GameDataSqlImport(IReadOnlyList<GameDataCellEdit> Edits, IReadOnlyList<string> Notes);

/// <summary>Makes and changes Game Data mods: table cell edits saved as .arkive files in the mod library.</summary>
public static partial class GameDataAuthoring
{
    /// <summary>
    /// Saves <paramref name="edits"/> as the table changes of mod <paramref name="modId"/> (keeping its other assets and
    /// metadata unless <paramref name="metadata"/> is given), or as a new, disabled mod when <paramref name="modId"/> is null.
    /// </summary>
    public static async Task<ArkiveModLibraryEntry> SaveModAsync(
        ArkiveModLibrary library,
        string? modId,
        ArkiveModMetadata? metadata,
        IReadOnlyList<GameDataCellEdit> edits,
        CancellationToken cancellationToken = default)
    {
        var gameData = edits.Count == 0 ? null : new ArkiveModGameData(edits.ToArray());
        var manifest = await library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (modId is null)
        {
            if (gameData is null) throw new ArkiveModRefusalException("GDM001", "Change at least one table cell first.");
            if (metadata is null) throw new ArgumentNullException(nameof(metadata));
            var created = ArkiveModAuthoringService.CreateManifest(Guid.NewGuid().ToString("N"), metadata, now, now) with { GameData = gameData };
            var fileName = library.UniqueFileName(ArkiveModLibrary.SuggestFileName(metadata.Name), manifest);
            var written = await ArkiveModFileService.WriteAsync(Path.Combine(library.ModsDirectory, fileName), created, [], cancellationToken)
                .ConfigureAwait(false);
            return await library.RegisterWrittenFileAsync(written, ArkiveModOrigin.Created, enabled: false, priority: 0, [], null, cancellationToken)
                .ConfigureAwait(false);
        }

        var entry = manifest.Find(modId) ?? throw new ArkiveModRefusalException("GDM002", "The mod is missing from the library.");
        if (gameData is null && entry.Manifest.Assets.Count == 0)
            throw new ArkiveModRefusalException("GDM003", "A mod needs at least one change. Remove the mod instead.");
        var updated = (metadata is null
                ? entry.Manifest
                : ArkiveModAuthoringService.CreateManifest(entry.ModId, metadata, entry.Manifest.CreatedAt, now) with { Assets = entry.Manifest.Assets })
            with { GameData = gameData, UpdatedAt = now };
        var rewritten = await ArkiveModFileService.RewriteManifestAsync(library.GetModFilePath(entry), updated, cancellationToken).ConfigureAwait(false);
        return await library.RegisterWrittenFileAsync(rewritten, entry.Origin, entry.Enabled, entry.Priority, entry.Bindings,
            entry.AuthoredForPackageProjectId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An edit of one cell of <paramref name="row"/>, a row read from <paramref name="schema"/>'s table in the game's own database.</summary>
    public static GameDataCellEdit Edit(string database, GameDataTableSchema schema, GameDataRow row, string column, JsonElement value)
    {
        var target = schema.Find(column) ?? throw new ArgumentException($"{schema.Name} has no column {column}.", nameof(column));
        if (schema.Key.Count == 0) throw new NotSupportedException($"{schema.Name} has no primary key, so its rows cannot be named.");
        if (target.KeyOrder > 0) throw new NotSupportedException($"{column} names the row, so it cannot be changed.");
        return new GameDataCellEdit(database, schema.Name,
            schema.Key.ToDictionary(item => item.Name, item => row.Cells[item.Name], StringComparer.OrdinalIgnoreCase),
            target.Name, value, row.Cells[target.Name]);
    }

    /// <summary>
    /// Reads a LostArkPatcher-style patch: "--File:EFTable_Npc.db" lines naming the database, then UPDATE statements.
    /// The statements run on a scratch copy of the game's own database and every cell they change becomes an edit; any
    /// other kind of statement is refused, so a patch cannot add or remove rows or reach outside its database.
    /// </summary>
    public static async Task<GameDataSqlImport> ImportSqlAsync(GameDataArchive archive, string sql, CancellationToken cancellationToken = default)
    {
        var sections = new List<(string Database, List<string> Statements)>();
        var notes = new List<string>();
        foreach (var (text, header) in Statements(sql))
        {
            if (header is not null)
            {
                if (!GameDataValues.IsDatabaseName(header))
                    throw new InvalidDataException($"'{header}' is not a game table database (EFTable_Name.db).");
                sections.Add((header, []));
                continue;
            }
            if (!UpdatePattern().IsMatch(text))
                throw new InvalidDataException($"Only UPDATE statements can be imported; this patch has: {Shorten(text)}");
            if (sections.Count == 0) throw new InvalidDataException("The patch does not say which database it changes (a --File:EFTable_Name.db line).");
            sections[^1].Statements.Add(text);
        }
        if (sections.All(section => section.Statements.Count == 0)) throw new InvalidDataException("The patch has no UPDATE statements.");

        var edits = new List<GameDataCellEdit>();
        using var work = new GameDataArchive.WorkFolder(Path.Combine(Path.GetTempPath(), "Arkive"));
        foreach (var group in sections.GroupBy(section => section.Database, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clean = await archive.GameDatabaseAsync(group.Key, cancellationToken).ConfigureAwait(false);
            var copy = work.File(group.Key.Replace('/', '_'));
            File.Copy(clean, copy, overwrite: true);
            using (var connection = GameDataTables.Open(copy, readOnly: false))
            {
                // Patches written for other tools quote text in double quotes, which SQLite reads as text only when told to.
                SQLitePCL.raw.sqlite3_db_config(connection.Handle, SqliteDoubleQuotedStrings, 1, out _);
                foreach (var statement in group.SelectMany(section => section.Statements))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    try
                    {
                        var rows = command.ExecuteNonQuery();
                        if (rows == 0) notes.Add($"{group.Key}: changed no rows: {Shorten(statement)}");
                    }
                    catch (SqliteException exception)
                    {
                        throw new InvalidDataException($"{group.Key}: {exception.Message} in {Shorten(statement)}", exception);
                    }
                }
            }
            edits.AddRange(Differences(group.Key, clean, copy, cancellationToken));
        }
        return new GameDataSqlImport(edits, notes);
    }

    // Every cell that differs between the game's database and the changed copy, row by row on the primary key.
    private static IEnumerable<GameDataCellEdit> Differences(string database, string clean, string changed, CancellationToken cancellationToken)
    {
        var edits = new List<GameDataCellEdit>();
        using var connection = GameDataTables.Open(changed, readOnly: false);
        using (var attach = connection.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $path AS clean";
            attach.Parameters.AddWithValue("$path", clean);
            attach.ExecuteNonQuery();
        }
        foreach (var table in GameDataTables.Tables(connection))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schema = GameDataTables.Schema(connection, table)!;
            if (schema.Key.Count == 0) continue;
            var join = string.Join(" AND ", schema.Key.Select(column => $"e.[{column.Name}] = c.[{column.Name}]"));
            var differs = string.Join(" OR ", schema.Columns.Select(column => $"e.[{column.Name}] IS NOT c.[{column.Name}]"));
            using (var count = connection.CreateCommand())
            {
                count.CommandText = $"SELECT (SELECT COUNT(*) FROM main.[{table}]) - (SELECT COUNT(*) FROM clean.[{table}])";
                if (Convert.ToInt64(count.ExecuteScalar()) != 0)
                    throw new InvalidDataException($"The patch adds or removes {table} rows; only changes to existing rows can be imported.");
            }
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT e.*, c.* FROM main.[{table}] e JOIN clean.[{table}] c ON {join} WHERE {differs}";
            using var reader = command.ExecuteReader();
            var columns = schema.Columns.Count;
            var keys = schema.Key.Select(column => (column.Name, Index: schema.Columns.ToList().IndexOf(column))).ToArray();
            while (reader.Read())
            {
                var row = keys.ToDictionary(key => key.Name, key => GameDataValues.From(reader.GetValue(key.Index)), StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < columns; index++)
                {
                    if (reader.GetValue(index) is byte[] || reader.GetValue(columns + index) is byte[])
                        throw new InvalidDataException($"The patch changes binary data in {table}; that cannot be imported.");
                    var value = GameDataValues.From(reader.GetValue(index));
                    var original = GameDataValues.From(reader.GetValue(columns + index));
                    if (GameDataValues.Same(value, original)) continue;
                    if (schema.Columns[index].KeyOrder > 0)
                        throw new InvalidDataException($"The patch changes {table}.{schema.Columns[index].Name}, which names its rows; that cannot be imported.");
                    edits.Add(new GameDataCellEdit(database, schema.Name, row, schema.Columns[index].Name, value, original));
                }
            }
            // A changed key would leave rows the join cannot match; the row count check above covers adds and removes.
            using var unmatched = connection.CreateCommand();
            unmatched.CommandText = $"SELECT COUNT(*) FROM main.[{table}] e WHERE NOT EXISTS (SELECT 1 FROM clean.[{table}] c WHERE {join})";
            if (Convert.ToInt64(unmatched.ExecuteScalar()) != 0)
                throw new InvalidDataException($"The patch changes the keys of {table} rows; that cannot be imported.");
        }
        return edits;
    }

    // Statements split on semicolons outside quotes and comments; "--File:Name.db" comment lines come back as headers.
    private static IEnumerable<(string Text, string? Header)> Statements(string sql)
    {
        var current = new StringBuilder();
        var index = 0;
        while (index < sql.Length)
        {
            var character = sql[index];
            if (character == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                var end = sql.IndexOf('\n', index);
                if (end < 0) end = sql.Length;
                var comment = sql[(index + 2)..end].Trim();
                if (comment.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
                {
                    if (current.ToString().Trim() is { Length: > 0 } pending) yield return (pending, null);
                    current.Clear();
                    yield return (string.Empty, comment[5..].Trim());
                }
                index = end;
                continue;
            }
            if (character == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? sql.Length : end + 2;
                current.Append(' ');
                continue;
            }
            if (character is '\'' or '"' or '`' or '[')
            {
                var close = character == '[' ? ']' : character;
                var end = index + 1;
                while (true)
                {
                    end = sql.IndexOf(close, end);
                    if (end < 0) throw new InvalidDataException("The patch has a quote that is never closed.");
                    // A doubled quote is part of the text.
                    if (close != ']' && end + 1 < sql.Length && sql[end + 1] == close)
                    {
                        end += 2;
                        continue;
                    }
                    break;
                }
                current.Append(sql, index, end + 1 - index);
                index = end + 1;
                continue;
            }
            if (character == ';')
            {
                if (current.ToString().Trim() is { Length: > 0 } statement) yield return (statement, null);
                current.Clear();
                index++;
                continue;
            }
            current.Append(character);
            index++;
        }
        if (current.ToString().Trim() is { Length: > 0 } last) yield return (last, null);
    }

    private const int SqliteDoubleQuotedStrings = 1013; // SQLITE_DBCONFIG_DQS_DML

    private static string Shorten(string text)
    {
        var line = Regex.Replace(text, @"\s+", " ").Trim();
        return line.Length <= 120 ? line : line[..117] + "...";
    }

    [GeneratedRegex(@"^UPDATE\s", RegexOptions.IgnoreCase)]
    private static partial Regex UpdatePattern();
}
