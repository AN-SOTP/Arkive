using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Arkive.Core.GameData;

/// <summary>
/// One changed cell of a game table in data2.lpk: the database (EFTable_Item.db), the table (Item), the row by its
/// primary key columns ({"PrimaryKey": 325600323}), the column (Model), the value to write and the game's own value
/// when the change was made. A game update that changes <see cref="Original"/> is how Arkive notices a mod needs
/// updating for the new game version.
/// </summary>
public sealed record GameDataCellEdit(
    string Database,
    string Table,
    IReadOnlyDictionary<string, JsonElement> Row,
    string Column,
    JsonElement Value,
    JsonElement Original)
{
    /// <summary>The cell this edit changes, as text that is the same for every edit of it.</summary>
    [JsonIgnore]
    public string CellKey => string.Join("|",
        new[] { Database.ToUpperInvariant(), Table.ToUpperInvariant(), Column.ToUpperInvariant() }
            .Concat(Row.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Key.ToUpperInvariant() + "=" + GameDataValues.Describe(item.Value))));

    /// <summary>The row as people read it: PrimaryKey 325600323.</summary>
    [JsonIgnore]
    public string RowLabel => string.Join(", ", Row.Select(item => $"{item.Key} {GameDataValues.Describe(item.Value)}"));
}

/// <summary>Cell values as the game's tables hold them (text, whole numbers, decimals) and as mod files store them (JSON).</summary>
public static partial class GameDataValues
{
    public const string TableFolder = "EFGame_Extra/ClientData/TableData/";

    public static JsonElement Null { get; } = JsonDocument.Parse("null").RootElement.Clone();

    /// <summary>The data2.lpk entry holding <paramref name="database"/> (EFTable_Item.db, or a subfolder's jss/EFTable_Item.db).</summary>
    public static string Entry(string database) => TableFolder + database;

    public static bool IsDatabaseName(string? name) => name is not null && DatabasePattern().IsMatch(name);

    public static bool IsIdentifier(string? name) => name is not null && IdentifierPattern().IsMatch(name);

    public static JsonElement From(object? value) => value switch
    {
        null or DBNull => Null,
        string text => JsonSerializer.SerializeToElement(text),
        long number => JsonSerializer.SerializeToElement(number),
        int number => JsonSerializer.SerializeToElement((long)number),
        double number when double.IsFinite(number) => JsonSerializer.SerializeToElement(number),
        float number when float.IsFinite(number) => JsonSerializer.SerializeToElement((double)number),
        bool flag => JsonSerializer.SerializeToElement(flag ? 1L : 0L),
        byte[] => throw new NotSupportedException("Binary cells cannot be changed."),
        _ => throw new NotSupportedException($"A {value.GetType().Name} cannot be stored in a game table."),
    };

    /// <summary>The value to hand SQLite: text, a whole number, a decimal or null.</summary>
    public static object? ToSql(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        JsonValueKind.Number => value.TryGetInt64(out var whole) ? whole : value.GetDouble(),
        _ => throw new NotSupportedException("Only text, numbers and empty values can be stored in a game table."),
    };

    /// <summary>
    /// What a person typed for a cell whose current value is <paramref name="like"/>: a number when the cell holds one
    /// (refused when the text is not a number), else the text as it is.
    /// </summary>
    public static JsonElement Parse(string text, JsonElement like)
    {
        if (like.ValueKind != JsonValueKind.Number) return JsonSerializer.SerializeToElement(text);
        var trimmed = text.Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) &&
            like.TryGetInt64(out _))
            return JsonSerializer.SerializeToElement(whole);
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
            return like.TryGetInt64(out _) && number == Math.Floor(number) && Math.Abs(number) < 9e15
                ? JsonSerializer.SerializeToElement((long)number)
                : JsonSerializer.SerializeToElement(number);
        throw new FormatException($"'{text}' is not a number.");
    }

    public static bool Same(JsonElement left, JsonElement right)
    {
        var a = ToSql(left);
        var b = ToSql(right);
        return (a, b) switch
        {
            (null, null) => true,
            (string x, string y) => string.Equals(x, y, StringComparison.Ordinal),
            (long x, long y) => x == y,
            (long or double, long or double) => Convert.ToDouble(a, CultureInfo.InvariantCulture) ==
                                                Convert.ToDouble(b, CultureInfo.InvariantCulture),
            _ => false,
        };
    }

    /// <summary>The value as a person reads it: text as it is, numbers without exponents, (empty) for null.</summary>
    public static string Describe(JsonElement value) => ToSql(value) switch
    {
        null => "(empty)",
        string text => text,
        long whole => whole.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        var other => other.ToString() ?? string.Empty,
    };

    [GeneratedRegex(@"^(?:[A-Za-z0-9_]{1,32}/)?EFTable_[A-Za-z0-9_]{1,80}\.db$")]
    private static partial Regex DatabasePattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex IdentifierPattern();
}
