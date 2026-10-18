using Microsoft.Data.Sqlite;

namespace Arkive.Core.Databases;

public sealed record GameSkillName(long Id, string Name, long LearnClass);

/// <summary>Skill names and the classes that learn them, read straight from the game's data2.lpk.</summary>
public static class GameSkillNames
{
    public static IReadOnlyDictionary<long, GameSkillName> Read(string archivePath, string language = "English",
        CancellationToken cancellationToken = default)
    {
        using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var entries = LostArkDatabaseArchive.ReadEntries(archive, cancellationToken)
            .Where(entry => entry.Name is "EFTable_GameMsg.db" or "EFTable_Skill.db").ToArray();
        var temporary = Path.Combine(Path.GetTempPath(), "Arkive", "skill-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var entry in entries)
                LostArkDatabaseArchive.Extract(archive, entry, Path.Combine(temporary, entry.Name), cancellationToken);
            var skills = GameDatabaseMetadataReader.ReadSkills(temporary, language, cancellationToken);
            SqliteConnection.ClearAllPools();
            return skills.ToDictionary(pair => pair.Key, pair => new GameSkillName(pair.Key, pair.Value.Name, pair.Value.LearnClass));
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
