using System.Net;
using System.Text.RegularExpressions;
using Arkive.Core.Discovery;
using Microsoft.Data.Sqlite;

namespace Arkive.Core.Databases;

internal static class GameDatabaseMetadataReader
{
    private sealed record Table(string Name, params string[] Fields);
    private static readonly Table[] Tables =
    [
        new("PC", "LookInfo"), new("Npc", "Model", "Icon", "MapIcon"),
        new("Item", "Model", "InstrumentModel", "Icon"), new("Vehicle", "Model", "RealmTemporaryModel", "Icon"),
        new("Polymorph", "Model"), new("Prop", "Model"), new("Tower", "Model"), new("TownBuilding", "Model"),
        new("TownNpcDress", "Model", "IconName"), new("Transit", "Model"), new("Trap", "Model"),
        new("VoyageShip", "Model"), new("VoyageShipAvatar", "Model", "Icon"), new("DropEther", "Model"),
        new("Money", "DropModel", "Icon"), new("Emoticon", "IconPath", "ResourcePath"), new("Pet", "Icon"),
        new("Skill", "Icon"), new("SkillBuff", "Icon"), new("Ability", "Icon"), new("QuestItem", "Icon"),
    ];
    internal static readonly HashSet<string> RequiredFiles = Tables.Select(table => $"EFTable_{table.Name}.db")
        .Append("EFTable_GameMsg.db").Append("EFTable_CharacterCustomizing.db")
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex Markup = new("<[^>]+>|\\$\\([^)]*\\)|\\{[^}]*\\}",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex WrappedReference = new("^[A-Za-z0-9_]+['\"](?<value>[^'\"]+)['\"]$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static GameAssetMetadataDocument Read(string root, string source, string language,
        IProgress<GameDatabaseBuildProgress>? progress, CancellationToken cancellation)
    {
        var messages = new Dictionary<string, string>(StringComparer.Ordinal);
        VisitRows(Path.Combine(root, "EFTable_GameMsg.db"), "GameMsg_" + language, ["KEY", "MSG"],
            cancellation, reader =>
            {
                var key = Text(reader, "KEY");
                var value = Clean(Text(reader, "MSG"));
                if (key.Length > 0 && value.Length > 0) messages[key] = value;
            }, required: true);
        var associations = new List<GameAssetAssociation>();
        for (var index = 0; index < Tables.Length; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var table = Tables[index];
            var path = Path.Combine(root, $"EFTable_{table.Name}.db");
            progress?.Report(new("Building name mapping", index, Tables.Length + 2, Path.GetFileName(path)));
            var columns = GetColumns(path, table.Name);
            var fields = table.Fields.Where(columns.Contains).ToArray();
            if (fields.Length == 0) continue;
            VisitRows(path, table.Name, ["PrimaryKey", "Name", .. fields], cancellation, reader =>
            {
                var nameKey = Text(reader, "Name");
                var displayName = DisplayName(messages, nameKey);
                if (table.Name == "PC")
                    displayName = messages.GetValueOrDefault("tip.name.enum_playerclass_" + nameKey.ToLowerInvariant(), displayName);
                if (nameKey.Length == 0 || displayName.Length == 0) return;
                foreach (var field in fields)
                {
                    var reference = Text(reader, field).Trim();
                    var wrapped = WrappedReference.Match(reference);
                    if (wrapped.Success) reference = wrapped.Groups["value"].Value;
                    reference = reference.Replace('\\', '/').Split('/')[^1];
                    var separator = reference.LastIndexOf('.');
                    var package = separator < 0 ? "" : reference[..separator];
                    var objectName = separator < 0 ? reference : reference[(separator + 1)..];
                    if (objectName.Length == 0 || objectName.Where(value => value != '_').All(char.IsDigit)) continue;
                    associations.Add(new(displayName, nameKey, table.Name, Number(reader, "PrimaryKey"),
                        field, reference, package, objectName));
                }
            });
        }
        var objectCounts = associations.Where(item => item.PackageName.Length == 0)
            .GroupBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var filtered = associations.Distinct()
            .Where(item => item.PackageName.Length > 0 || objectCounts[item.ObjectName] <= 250)
            .OrderBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.EntityType, StringComparer.Ordinal)
            .ThenBy(item => item.EntityId).ToArray();
        if (filtered.Length == 0)
            throw new InvalidDataException("No supported asset references were found in the game databases.");
        progress?.Report(new("Building character browser", Tables.Length, Tables.Length + 2, "PC / Npc"));
        var entities = ReadEntities(root, messages, cancellation);
        progress?.Report(new("Building customization mapping", Tables.Length + 1, Tables.Length + 2, "CharacterCustomizing"));
        var customizations = ReadCustomizations(root, cancellation);
        return new(2, language, source, DateTimeOffset.UtcNow, filtered, entities, customizations);
    }

    /// <summary>Every skill's name and the class that learns it (0 for none), from EFTable_Skill and GameMsg.</summary>
    internal static Dictionary<long, (string Name, long LearnClass)> ReadSkills(string root, string language,
        CancellationToken cancellation)
    {
        var messages = new Dictionary<string, string>(StringComparer.Ordinal);
        VisitRows(Path.Combine(root, "EFTable_GameMsg.db"), "GameMsg_" + language, ["KEY", "MSG"], cancellation, reader =>
        {
            var key = Text(reader, "KEY");
            if (key.StartsWith("tip.name.skill_", StringComparison.Ordinal) || key.StartsWith("tip.name.enum_playerclass_", StringComparison.Ordinal))
            {
                var value = Clean(Text(reader, "MSG"));
                if (value.Length > 0) messages[key] = value;
            }
        }, required: true);
        var skills = new Dictionary<long, (string Name, long LearnClass)>();
        VisitRows(Path.Combine(root, "EFTable_Skill.db"), "Skill", ["PrimaryKey", "Name", "LearnClass"], cancellation, reader =>
        {
            var name = DisplayName(messages, Text(reader, "Name"));
            if (name.Length > 0) skills.TryAdd(Number(reader, "PrimaryKey"), (name, Number(reader, "LearnClass")));
        });
        return skills;
    }

    private static IReadOnlyList<GameEntityDefinition> ReadEntities(string root,
        IReadOnlyDictionary<string, string> messages, CancellationToken cancellation)
    {
        var entities = new List<GameEntityDefinition>();
        VisitRows(Path.Combine(root, "EFTable_PC.db"), "PC",
            ["PrimaryKey", "Name", "BaseClass", "Creatable", "Released", "GenderType", "LookInfo"], cancellation, reader =>
            {
                var key = Text(reader, "Name");
                var name = messages.GetValueOrDefault("tip.name.enum_playerclass_" + key.ToLowerInvariant(), DisplayName(messages, key));
                if (name.Length == 0) return;
                var id = Number(reader, "PrimaryKey");
                var baseId = Number(reader, "BaseClass");
                entities.Add(new(name, key, "PC", id,
                    Number(reader, "Released") != 0 ? "Playable class" : "Player archetype",
                    $"base={(baseId == 0 ? "-" : baseId)}; creatable={Number(reader, "Creatable")}; released={Number(reader, "Released")}; gender={Number(reader, "GenderType")}",
                    baseId != 0 && baseId != id ? baseId : null, NullIfEmpty(Text(reader, "LookInfo"))));
            });
        VisitRows(Path.Combine(root, "EFTable_Npc.db"), "Npc",
            ["PrimaryKey", "Name", "Model", "Grade", "Type", "Function", "FunctionType", "TitleName"], cancellation, reader =>
            {
                var key = Text(reader, "Name");
                var name = DisplayName(messages, key);
                if (name.Length == 0) return;
                var function = Number(reader, "Function");
                var functionType = Number(reader, "FunctionType");
                var grade = Number(reader, "Grade");
                var category = function != 0 || functionType != 0 ? "Functional NPC / vendor" : grade != 0 ? "Combat NPC" : "NPC";
                var detail = $"grade={grade}; type={Number(reader, "Type")}";
                if (function != 0 || functionType != 0) detail += $"; function={function}/{functionType}";
                var title = DisplayName(messages, Text(reader, "TitleName"));
                if (title.Length > 0) detail += "; title=" + title;
                entities.Add(new(name, key, "Npc", Number(reader, "PrimaryKey"), category, detail,
                    null, NullIfEmpty(Text(reader, "Model"))));
            });
        return entities.OrderBy(item => item.EntityType == "PC" ? 0 : 1)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.EntityId).ToArray();
    }

    private static IReadOnlyList<CharacterCustomizationDefinition> ReadCustomizations(string root, CancellationToken cancellation)
    {
        var result = new List<CharacterCustomizationDefinition>();
        VisitRows(Path.Combine(root, "EFTable_CharacterCustomizing.db"), "CharacterCustomizing",
            ["PrimaryKey", "SecondaryKey", "SortOrder", "Object_Unit", "Object_String", "Icon", "IconIndex", "MenuName"],
            cancellation, reader => result.Add(new(Number(reader, "PrimaryKey"), checked((int)Number(reader, "SecondaryKey")),
                checked((int)Number(reader, "SortOrder")), checked((int)Number(reader, "Object_Unit")),
                Text(reader, "Object_String"), Text(reader, "Icon"), checked((int)Number(reader, "IconIndex")), Text(reader, "MenuName"))));
        return result.OrderBy(item => item.PlayerClassId).ThenBy(item => item.CategoryId).ThenBy(item => item.UiPosition).ToArray();
    }

    private static HashSet<string> GetColumns(string path, string table)
    {
        if (!File.Exists(path)) return [];
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE name=$name AND type='table'";
        command.Parameters.AddWithValue("$name", table);
        if (command.ExecuteScalar() is null) return [];
        command.Parameters.Clear();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(1));
        return result;
    }

    private static void VisitRows(string path, string table, string[] fields, CancellationToken cancellation,
        Action<SqliteDataReader> visit, bool required = false)
    {
        var columns = GetColumns(path, table);
        if (!fields.All(columns.Contains))
        {
            if (required) throw new InvalidDataException($"The database does not contain the required {table} table/columns. Check the selected language and client version.");
            return;
        }
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + string.Join(",", fields.Select(field => $"\"{field}\"")) + $" FROM \"{table}\"";
        using var reader = command.ExecuteReader();
        var rows = 0;
        while (reader.Read())
        {
            cancellation.ThrowIfCancellationRequested();
            if (++rows > 2_000_000) throw new InvalidDataException($"The {table} table exceeds the supported row limit.");
            visit(reader);
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA query_only=ON;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static string Text(SqliteDataReader reader, string field)
    {
        var ordinal = reader.GetOrdinal(field);
        if (reader.IsDBNull(ordinal)) return "";
        var value = reader.GetString(ordinal);
        if (value.Length > 65_536) throw new InvalidDataException($"The {field} value exceeds the supported text limit.");
        return value;
    }
    private static long Number(SqliteDataReader reader, string field) => reader.IsDBNull(reader.GetOrdinal(field)) ? 0 : reader.GetInt64(reader.GetOrdinal(field));
    private static string Clean(string value) => string.Join(' ', WebUtility.HtmlDecode(Markup.Replace(value, "")).Replace("\\n", " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string DisplayName(IReadOnlyDictionary<string, string> messages, string key) =>
        messages.GetValueOrDefault(key, key.StartsWith("tip.", StringComparison.Ordinal) || key.StartsWith("ui.", StringComparison.Ordinal) || key.StartsWith('$') ? "" : Clean(key));
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
