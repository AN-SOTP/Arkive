using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Arkive.Core.GameData;

/// <summary>A column of a game table: its name, declared type and place in the primary key (0 when not part of it).</summary>
public sealed record GameDataColumn(string Name, string Type, int KeyOrder);

/// <summary>A game table's columns, in order.</summary>
public sealed record GameDataTableSchema(string Name, IReadOnlyList<GameDataColumn> Columns)
{
    public IReadOnlyList<GameDataColumn> Key => Columns.Where(column => column.KeyOrder > 0).OrderBy(column => column.KeyOrder).ToArray();

    public GameDataColumn? Find(string column) =>
        Columns.FirstOrDefault(item => item.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One row of a game table, cell values by column name.</summary>
public sealed record GameDataRow(IReadOnlyDictionary<string, JsonElement> Cells);

/// <summary>Reads and changes the game's table databases once they are out of data2.lpk.</summary>
public static class GameDataTables
{
    public static SqliteConnection Open(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public static IReadOnlyList<string> Tables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            if (GameDataValues.IsIdentifier(reader.GetString(0))) names.Add(reader.GetString(0));
        return names;
    }

    public static GameDataTableSchema? Schema(SqliteConnection connection, string table)
    {
        if (!GameDataValues.IsIdentifier(table)) return null;
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info([{table}])";
        using var reader = command.ExecuteReader();
        var columns = new List<GameDataColumn>();
        while (reader.Read()) columns.Add(new GameDataColumn(reader.GetString(1), reader.GetString(2), reader.GetInt32(5)));
        if (columns.Count == 0) return null;
        return new GameDataTableSchema(Tables(connection).First(name => name.Equals(table, StringComparison.OrdinalIgnoreCase)), columns);
    }

    /// <summary>The cell's value, or null when the table, column or row is gone.</summary>
    public static JsonElement? Read(SqliteConnection connection, GameDataCellEdit edit, Dictionary<string, GameDataTableSchema?>? schemas = null)
    {
        var schema = SchemaCached(connection, edit.Table, schemas);
        if (schema is null || schema.Find(edit.Column) is null || !SameKey(schema, edit.Row)) return null;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT [{schema.Find(edit.Column)!.Name}] FROM [{schema.Name}] WHERE {Where(command, edit.Row)} LIMIT 2";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var value = GameDataValues.From(reader.GetValue(0));
        return reader.Read() ? null : value;
    }

    /// <summary>Writes <paramref name="edits"/> into the database at <paramref name="path"/>, refusing an edit whose row is not there.</summary>
    /// <returns>How many cells were written.</returns>
    public static int Apply(string path, IEnumerable<GameDataCellEdit> edits, CancellationToken cancellationToken)
    {
        using var connection = Open(path, readOnly: false);
        using var transaction = connection.BeginTransaction();
        var schemas = new Dictionary<string, GameDataTableSchema?>(StringComparer.OrdinalIgnoreCase);
        var written = 0;
        foreach (var edit in edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schema = SchemaCached(connection, edit.Table, schemas)
                         ?? throw new InvalidDataException($"{edit.Database} has no table {edit.Table}.");
            var column = schema.Find(edit.Column) ?? throw new InvalidDataException($"{edit.Table} has no column {edit.Column}.");
            if (!SameKey(schema, edit.Row))
                throw new InvalidDataException($"{edit.Table} rows are named by {string.Join(", ", schema.Key.Select(item => item.Name))}, not {string.Join(", ", edit.Row.Keys)}.");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE [{schema.Name}] SET [{column.Name}] = $value WHERE {Where(command, edit.Row)}";
            command.Parameters.AddWithValue("$value", GameDataValues.ToSql(edit.Value) ?? DBNull.Value);
            var rows = command.ExecuteNonQuery();
            if (rows != 1)
                throw new InvalidDataException($"{edit.Table} has {(rows == 0 ? "no" : rows.ToString())} row {edit.RowLabel}.");
            written++;
        }
        transaction.Commit();
        return written;
    }

    /// <summary>
    /// Rows of <paramref name="table"/> where any text or number column contains <paramref name="search"/> (every row when
    /// it is empty), in table order, at most <paramref name="limit"/>.
    /// </summary>
    public static IReadOnlyList<GameDataRow> Rows(SqliteConnection connection, string table, string? search, int limit, CancellationToken cancellationToken)
    {
        var schema = Schema(connection, table) ?? throw new InvalidDataException($"There is no table {table}.");
        using var command = connection.CreateCommand();
        var where = string.Empty;
        if (!string.IsNullOrWhiteSpace(search))
        {
            command.Parameters.AddWithValue("$search", "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            where = " WHERE " + string.Join(" OR ", schema.Columns.Select(column => $"CAST([{column.Name}] AS TEXT) LIKE $search ESCAPE '\\'"));
        }
        command.CommandText = $"SELECT * FROM [{schema.Name}]{where} LIMIT {Math.Clamp(limit, 1, 10_000)}";
        using var reader = command.ExecuteReader();
        var rows = new List<GameDataRow>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cells = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var value = reader.GetValue(index);
                cells[reader.GetName(index)] = value is byte[] ? GameDataValues.Null : GameDataValues.From(value);
            }
            rows.Add(new GameDataRow(cells));
        }
        return rows;
    }

    private static GameDataTableSchema? SchemaCached(SqliteConnection connection, string table, Dictionary<string, GameDataTableSchema?>? schemas)
    {
        if (schemas is null) return Schema(connection, table);
        if (!schemas.TryGetValue(table, out var schema)) schemas[table] = schema = Schema(connection, table);
        return schema;
    }

    private static bool SameKey(GameDataTableSchema schema, IReadOnlyDictionary<string, JsonElement> row) =>
        row.Count > 0 && schema.Key.Count == row.Count &&
        schema.Key.All(column => row.Keys.Contains(column.Name, StringComparer.OrdinalIgnoreCase));

    private static string Where(SqliteCommand command, IReadOnlyDictionary<string, JsonElement> row)
    {
        var parts = new List<string>();
        var index = 0;
        foreach (var (column, value) in row)
        {
            if (!GameDataValues.IsIdentifier(column)) throw new InvalidDataException($"'{column}' is not a column name.");
            var name = "$key" + index++;
            command.Parameters.AddWithValue(name, GameDataValues.ToSql(value) ?? DBNull.Value);
            parts.Add($"[{column}] = {name}");
        }
        return string.Join(" AND ", parts);
    }
}
