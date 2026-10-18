using System.IO.Compression;
using System.Text.Json;
using Arkive.Core.Discovery;

namespace Arkive.Core.Skills;

public sealed record SkillEffectNameDocument(
    int Version,
    DateTimeOffset GeneratedAt,
    string ArchivePath,
    long ArchiveLength,
    long ArchiveLastWriteUtcTicks,
    IReadOnlyList<GameAssetAssociation> Associations);

/// <summary>
/// In-game names for skill effects: which class's skill starts each particle system, read from the skills' own
/// action files in data3.lpk (see <see cref="SkillActionReader"/>), so searching "Quake Smash" finds its effects. Kept
/// beside the game-name database and rebuilt whenever data3.lpk changes.
/// </summary>
public static class SkillEffectNameSidecar
{
    public const int CurrentVersion = 1;
    public const string EntityType = "Skill";
    public const string Relation = "SkillActionEffect";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string GetDefaultPath(string metadataPath)
    {
        var fullPath = Path.GetFullPath(metadataPath);
        return Path.Combine(Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(fullPath) + ".skills.json.gz");
    }

    /// <summary>EFGame\data3.lpk for a Packages folder (EFGame\ReleasePC\Packages), or null when it is missing.</summary>
    public static string? FindArchive(string packagesRoot)
    {
        var path = Path.GetFullPath(Path.Combine(packagesRoot, "..", "..", "data3.lpk"));
        return File.Exists(path) ? path : null;
    }

    public static IReadOnlyList<GameAssetAssociation> LoadAssociations(string metadataPath) =>
        TryLoad(GetDefaultPath(metadataPath))?.Associations ?? [];

    public static SkillEffectNameDocument? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var document = JsonSerializer.Deserialize<SkillEffectNameDocument>(gzip, JsonOptions);
            return document?.Version == CurrentVersion ? document : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>True when the saved names were read from the data3.lpk the game folder holds now.</summary>
    public static bool IsCurrent(string metadataPath, string packagesRoot)
    {
        var archive = FindArchive(packagesRoot);
        if (archive is null) return true;
        var document = TryLoad(GetDefaultPath(metadataPath));
        var info = new FileInfo(archive);
        return document is not null && document.ArchivePath.Equals(archive, StringComparison.OrdinalIgnoreCase) &&
               document.ArchiveLength == info.Length && document.ArchiveLastWriteUtcTicks == info.LastWriteTimeUtc.Ticks;
    }

    /// <summary>The class action files in data3.lpk; the NPC copies of class skills are left out.</summary>
    public static IReadOnlyList<LostArkDataEntry> FindActionFiles(IReadOnlyList<LostArkDataEntry> entries) =>
        entries.Where(entry => entry.Path.Contains("/XmlData/Action/", StringComparison.OrdinalIgnoreCase) &&
                               entry.Path.EndsWith(".loa", StringComparison.OrdinalIgnoreCase) &&
                               !Path.GetFileNameWithoutExtension(entry.Path).EndsWith("_NPC", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public static async Task<SkillEffectNameDocument?> BuildAsync(
        string metadataPath,
        string packagesRoot,
        CancellationToken cancellationToken = default)
    {
        var archive = FindArchive(packagesRoot);
        if (archive is null) return null;
        var (skills, classes) = await ReadNamesAsync(metadataPath, packagesRoot, cancellationToken).ConfigureAwait(false);
        var associations = new List<GameAssetAssociation>();
        await using (var stream = File.OpenRead(archive))
        {
            foreach (var entry in FindActionFiles(LostArkDataArchive.ReadEntries(stream, cancellationToken)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = Path.GetFileNameWithoutExtension(entry.Path);
                IReadOnlyList<SkillAction> actions;
                try
                {
                    actions = SkillActionReader.Read(LostArkDataArchive.Read(stream, entry));
                }
                catch (InvalidDataException)
                {
                    continue;
                }
                foreach (var action in actions)
                {
                    if (!skills.TryGetValue(action.Id, out var skill)) continue;
                    var displayName = classes.TryGetValue(skill.LearnClass, out var className)
                        ? $"{skill.Name} ({className} skill)"
                        : $"{skill.Name} (skill)";
                    foreach (var path in action.ParticleSystems)
                    {
                        var separator = path.IndexOf('.');
                        if (separator <= 0 || separator == path.Length - 1) continue;
                        associations.Add(new GameAssetAssociation(displayName, $"tip.name.skill_{action.Id}", EntityType,
                            action.Id, $"Effect ({file})", path, path[..separator].ToLowerInvariant(), path[(separator + 1)..])
                        {
                            Confidence = GameAssetAssociationConfidence.Verified,
                            Relation = Relation,
                        });
                    }
                }
            }
        }
        var info = new FileInfo(archive);
        var document = new SkillEffectNameDocument(CurrentVersion, DateTimeOffset.UtcNow, archive, info.Length,
            info.LastWriteTimeUtc.Ticks,
            associations.DistinctBy(item => (item.EntityId, item.AssetReference.ToUpperInvariant()))
                .OrderBy(item => item.PackageName, StringComparer.Ordinal)
                .ThenBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.DisplayName, StringComparer.Ordinal)
                .ToArray());
        await SaveAsync(GetDefaultPath(metadataPath), document, cancellationToken).ConfigureAwait(false);
        return document;
    }

    // Skill names and classes come from data2.lpk's skill table; class names from the game-name database's playable
    // classes, keyed by class id (702 -> DragonKnight -> Guardianknight).
    private static async Task<(IReadOnlyDictionary<long, Databases.GameSkillName> Skills, Dictionary<long, string> Classes)> ReadNamesAsync(
        string metadataPath, string packagesRoot, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(metadataPath);
        var document = await JsonSerializer.DeserializeAsync<GameAssetMetadataDocument>(file, JsonOptions, cancellationToken)
                           .ConfigureAwait(false) ??
                       throw new InvalidDataException("The game-name database is empty.");
        var data2 = Path.GetFullPath(Path.Combine(packagesRoot, "..", "..", "data2.lpk"));
        var language = string.IsNullOrWhiteSpace(document.Language) ? "English" : document.Language;
        var skills = File.Exists(data2)
            ? Databases.GameSkillNames.Read(data2, language, cancellationToken)
            : new Dictionary<long, Databases.GameSkillName>();
        var classes = new Dictionary<long, string>();
        foreach (var entity in document.Entities ?? [])
            if (entity.EntityType.Equals("PC", StringComparison.OrdinalIgnoreCase))
                classes.TryAdd(entity.EntityId, GameClassNames.LocalisedName(entity.DisplayName) ?? entity.DisplayName);
        return (skills, classes);
    }

    private static async Task SaveAsync(string path, SkillEffectNameDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                await JsonSerializer.SerializeAsync(gzip, document, JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
