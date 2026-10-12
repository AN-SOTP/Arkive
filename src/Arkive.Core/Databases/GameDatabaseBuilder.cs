using System.Text.Json;
using Arkive.Core.Discovery;
using Microsoft.Data.Sqlite;

namespace Arkive.Core.Databases;

public sealed record GameDatabaseBuildProgress(string Stage, int Completed, int Total, string CurrentFile);

public sealed record GameDatabaseBuildResult(string OutputPath, string Language, int DatabaseCount,
    int AssociationCount, int EntityCount, int CustomizationCount);

public sealed class GameDatabaseBuilder
{
    public Task<GameDatabaseBuildResult> BuildAsync(string archivePath, string outputPath,
        string language = "English", IProgress<GameDatabaseBuildProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(() =>
        Build(archivePath, outputPath, language, progress, cancellationToken), cancellationToken);

    private static GameDatabaseBuildResult Build(string archivePath, string outputPath, string language,
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
        using var archive = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        progress?.Report(new("Reading archive", 0, 1, Path.GetFileName(source)));
        var entries = LostArkDatabaseArchive.ReadEntries(archive, cancellation);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = Path.Combine(Path.GetDirectoryName(output)!, ".database-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            for (var index = 0; index < entries.Count; index++)
            {
                progress?.Report(new("Decrypting databases", index, entries.Count, entries[index].Name));
                LostArkDatabaseArchive.Extract(archive, entries[index], Path.Combine(temporary, entries[index].Name), cancellation);
            }
            var document = GameDatabaseMetadataReader.Read(temporary, source, language, progress, cancellation);
            var candidate = Path.Combine(temporary, "arkive-asset-metadata.json");
            using (var stream = File.Create(candidate))
                JsonSerializer.Serialize(stream, document, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            cancellation.ThrowIfCancellationRequested();
            File.Move(candidate, output, overwrite: true);
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
            Directory.Delete(temporary, recursive: true);
        }
    }
}
