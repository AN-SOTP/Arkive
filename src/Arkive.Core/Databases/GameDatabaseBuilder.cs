using System.Text.Json;
using Arkive.Core.Discovery;
using Arkive.Core.GameData;
using Microsoft.Data.Sqlite;

namespace Arkive.Core.Databases;

public sealed record GameDatabaseBuildProgress(string Stage, int Completed, int Total, string CurrentFile);

public sealed record GameDatabaseBuildResult(string OutputPath, string Language, int DatabaseCount,
    int AssociationCount, int EntityCount, int CustomizationCount);

/// <summary>
/// Which game data a game-name database was read from, kept in a small file beside it so that checking it does not
/// mean reading the database: the size and date of the game's own data2.lpk, the language, and what the reader knew.
/// </summary>
public sealed record GameDatabaseStamp(long SourceLength, long SourceLastWriteUtcTicks, string Language, int ContentVersion);

public sealed class GameDatabaseBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public Task<GameDatabaseBuildResult> BuildAsync(string archivePath, string outputPath,
        string language = "English", IProgress<GameDatabaseBuildProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(() =>
        Build(archivePath, null, outputPath, language, progress, cancellationToken), cancellationToken);

    /// <summary>
    /// Builds the game-name database from the game's own tables: where Arkive has written a language or table mods into
    /// data2.lpk, the copies of the game's tables Arkive kept are read instead, so the names stay the game's.
    /// </summary>
    public Task<GameDatabaseBuildResult> BuildAsync(GameDataArchive archive, string outputPath,
        string language = "English", IProgress<GameDatabaseBuildProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(() =>
        Build(archive.ArchivePath, archive, outputPath, language, progress, cancellationToken), cancellationToken);

    public static string StampPath(string metadataPath)
    {
        var fullPath = Path.GetFullPath(metadataPath);
        return Path.Combine(Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(fullPath) + ".stamp.json");
    }

    public static GameDatabaseStamp? ReadStamp(string metadataPath)
    {
        try
        {
            var path = StampPath(metadataPath);
            return File.Exists(path) ? JsonSerializer.Deserialize<GameDatabaseStamp>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The language the game-name database at <paramref name="metadataPath"/> was built in: from its stamp, else from
    /// the start of the database itself; English when neither says.
    /// </summary>
    public static string ReadLanguage(string metadataPath)
    {
        if (ReadStamp(metadataPath) is { Language.Length: > 0 } stamp) return stamp.Language;
        try
        {
            using var reader = new StreamReader(metadataPath);
            var head = new char[512];
            var read = reader.ReadBlock(head, 0, head.Length);
            var match = System.Text.RegularExpressions.Regex.Match(new string(head, 0, read), "\"language\"\\s*:\\s*\"(?<name>[A-Za-z]{1,32})\"");
            return match.Success ? match.Groups["name"].Value : "English";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "English";
        }
    }

    /// <summary>
    /// True when the game-name database at <paramref name="metadataPath"/> was read from the game data
    /// <paramref name="archive"/> holds now, by a reader that knew every table this Arkive reads. A database from before
    /// stamps were kept counts as out of date.
    /// </summary>
    public static bool IsCurrent(string metadataPath, GameDataArchive archive)
    {
        if (!File.Exists(metadataPath) || ReadStamp(metadataPath) is not { } stamp) return false;
        var (length, ticks) = archive.GameStamp();
        return stamp.SourceLength == length && stamp.SourceLastWriteUtcTicks == ticks &&
               stamp.ContentVersion == GameDatabaseMetadataReader.ContentVersion;
    }

    private static GameDatabaseBuildResult Build(string archivePath, GameDataArchive? gameData, string outputPath, string language,
        IProgress<GameDatabaseBuildProgress>? progress, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (string.IsNullOrWhiteSpace(language) || language.Length > 32 || !language.All(char.IsAsciiLetter))
            throw new ArgumentException("Language must be a database language name such as English.", nameof(language));
        var source = Path.GetFullPath(archivePath);
        var output = Path.GetFullPath(outputPath);
        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The mapping output cannot overwrite the source archive.");
        cancellation.ThrowIfCancellationRequested();
        // Read before the tables, so a game update that lands meanwhile leaves the stamp out of date, not the names.
        var (sourceLength, sourceTicks) = gameData?.GameStamp() ?? (new FileInfo(source).Length, new FileInfo(source).LastWriteTimeUtc.Ticks);
        using var archive = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        progress?.Report(new("Reading archive", 0, 1, Path.GetFileName(source)));
        var entries = LostArkDatabaseArchive.ReadEntries(archive, cancellation);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var buildId = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(Path.GetTempPath(), "Arkive", "database-build-" + buildId);
        var candidate = Path.Combine(Path.GetDirectoryName(output)!, ".arkive-database-" + buildId + ".tmp");
        Directory.CreateDirectory(temporary);
        try
        {
            for (var index = 0; index < entries.Count; index++)
            {
                progress?.Report(new("Decrypting databases", index, entries.Count, entries[index].Name));
                var database = Path.Combine(temporary, entries[index].Name);
                if (gameData is null) LostArkDatabaseArchive.Extract(archive, entries[index], database, cancellation);
                else gameData.ExtractGameDatabaseAsync(entries[index].Name, database, cancellation).GetAwaiter().GetResult();
            }
            var document = GameDatabaseMetadataReader.Read(temporary, source, language, progress, cancellation);
            using (var stream = File.Create(candidate))
                JsonSerializer.Serialize(stream, document, JsonOptions);
            cancellation.ThrowIfCancellationRequested();
            // The stamp goes first: a database without its stamp is only read again, never trusted as current.
            File.Delete(StampPath(output));
            File.Move(candidate, output, overwrite: true);
            File.WriteAllText(StampPath(output), JsonSerializer.Serialize(
                new GameDatabaseStamp(sourceLength, sourceTicks, language, GameDatabaseMetadataReader.ContentVersion), JsonOptions));
            progress?.Report(new("Complete", 1, 1, Path.GetFileName(output)));
            return new(output, language, entries.Count, document.Associations.Count,
                document.Entities?.Count ?? 0, document.Customizations?.Count ?? 0);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The game database schema could not be read: " + exception.Message, exception);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            throw new InvalidDataException("A game database contains an unsupported field value.", exception);
        }
        finally
        {
            File.Delete(candidate);
            Directory.Delete(temporary, recursive: true);
        }
    }
}
