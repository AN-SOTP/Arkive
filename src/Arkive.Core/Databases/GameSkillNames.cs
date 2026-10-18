using Microsoft.Data.Sqlite;

namespace Arkive.Core.Databases;

public sealed record GameSkillName(long Id, string Name, long LearnClass);

/// <summary>An NPC's name (empty when the game's text has none), its model's look and its action file.</summary>
public sealed record GameNpcAction(long Id, string Name, string Model, string ActionGroup);

/// <summary>Skill names and the classes that learn them, read straight from the game's data2.lpk.</summary>
public static class GameSkillNames
{
    public static IReadOnlyDictionary<long, GameSkillName> Read(string archivePath, string language = "English",
        CancellationToken cancellationToken = default) => ReadWithNpcs(archivePath, language, false, cancellationToken).Skills;

    /// <summary>The skill names and, when asked, every NPC's model and action file (for NPC skill previews).</summary>
    public static (IReadOnlyDictionary<long, GameSkillName> Skills, IReadOnlyList<GameNpcAction> Npcs) ReadWithNpcs(
        string archivePath, string language = "English", bool npcs = true, CancellationToken cancellationToken = default)
    {
        using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var entries = LostArkDatabaseArchive.ReadEntries(archive, cancellationToken)
            .Where(entry => entry.Name is "EFTable_GameMsg.db" or "EFTable_Skill.db" || (npcs && entry.Name == "EFTable_Npc.db")).ToArray();
        var temporary = Path.Combine(Path.GetTempPath(), "Arkive", "skill-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var entry in entries)
                LostArkDatabaseArchive.Extract(archive, entry, Path.Combine(temporary, entry.Name), cancellationToken);
            var skills = GameDatabaseMetadataReader.ReadSkills(temporary, language, cancellationToken);
            var npcActions = npcs && File.Exists(Path.Combine(temporary, "EFTable_Npc.db"))
                ? GameDatabaseMetadataReader.ReadNpcActions(temporary, language, cancellationToken) : [];
            SqliteConnection.ClearAllPools();
            return (skills.ToDictionary(pair => pair.Key, pair => new GameSkillName(pair.Key, pair.Value.Name, pair.Value.LearnClass)), npcActions);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The game's skill table could not be read: " + exception.Message, exception);
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
