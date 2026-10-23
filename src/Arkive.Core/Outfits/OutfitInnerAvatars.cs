using Arkive.Core.GameData;

namespace Arkive.Core.Outfits;

/// <summary>
/// Outfit pieces the game wears as inner avatars (EFTable_Item InnerAvatar, the classes' Eternity sets). The open world
/// misdraws such a piece once its material list has grown, mixing its materials up with the character's other parts.
/// </summary>
public static class OutfitInnerAvatars
{
    /// <summary>Which of <paramref name="pieces"/> (pc_wbk_f_av_137_lower_sk) an inner-avatar item draws, by its look's name.</summary>
    public static async Task<IReadOnlySet<string>> ReadAsync(GameDataArchive archive, IEnumerable<string> pieces,
        CancellationToken cancellationToken = default)
    {
        var path = await archive.GameDatabaseAsync("EFTable_Item.db", cancellationToken).ConfigureAwait(false);
        var looks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var connection = GameDataTables.Open(path, readOnly: true))
        {
            var tables = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                using var reader = command.ExecuteReader();
                while (reader.Read()) tables.Add(reader.GetString(0));
            }
            foreach (var table in tables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT name FROM pragma_table_info('{table.Replace("'", "''")}')";
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) columns.Add(reader.GetString(0));
                if (!columns.Contains("Model") || !columns.Contains("InnerAvatar")) continue;
                command.CommandText = $"SELECT Model FROM \"{table.Replace("\"", "\"\"")}\" WHERE InnerAvatar = 1";
                using var rows = command.ExecuteReader();
                // EFDLItem_PC_WBK_F_AV_137_Lower.PC_WBK_F_AV_137_Lower
                while (rows.Read())
                    if (!rows.IsDBNull(0) && rows.GetString(0).Split('.')[0] is { Length: > 0 } look) looks.Add(look);
            }
        }
        return pieces.Where(piece => looks.Contains("EFDLItem_" + Stem(piece))).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // pc_wbk_f_av_137_lower_sk → pc_wbk_f_av_137_lower, as its look is named.
    private static string Stem(string piece)
    {
        var name = OutfitSwapBodies.StripLocale(piece);
        return name.EndsWith("_sk", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
    }
}
