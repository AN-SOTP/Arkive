using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arkive.Core.Databases;
using Arkive.Core.Languages;
using Arkive.Core.Mods;
using Arkive.Core.Skills;

namespace Arkive.Core.GameData;

/// <summary>What Arkive wants data2.lpk to hold: the language of the game's text (null for the game's own) and the mods' table edits.</summary>
public sealed record GameDataTarget(string? Language, IReadOnlyList<GameDataCellEdit> Edits)
{
    public static GameDataTarget GameOwn { get; } = new(null, []);

    public bool IsGameOwn => Language is null && Edits.Count == 0;
}

/// <summary>A database Arkive has changed in data2.lpk: the game's copy, kept to undo the change, and what was written.</summary>
public sealed record GameDataEntryBackup(string Entry, string BackupPath, string Fingerprint);

/// <summary>What Arkive has changed in <paramref name="ArchivePath"/>, and how to put the game's file back.</summary>
public sealed record GameDataArchiveState(
    int Version, string ArchivePath, string OriginalSha256, long OriginalLength, DateTime OriginalWriteTimeUtc, string AppliedSha256,
    long AppliedLength, DateTime AppliedWriteTimeUtc, IReadOnlyList<GameDataEntryBackup> Entries, string? Language,
    int LanguageLines, IReadOnlyList<GameDataCellEdit> Edits, DateTimeOffset AppliedAt);

public sealed record GameDataWriteResult(int LanguageLines, int Cells, IReadOnlyList<string> Databases);

/// <summary>
/// The one writer of the game's data2.lpk, which holds every game table (items, NPCs, cameras, the game's text...)
/// as encrypted SQLite databases. The Language tab and Game Data mods both change it, so every write starts from the
/// game's own databases and applies both again: changing one never drops the other. The game's copy of each
/// database Arkive touches is kept as a backup, and writing nothing puts back the game's file exactly, date included.
/// A game update replaces data2.lpk; Arkive notices by its size and date and starts again from the new file.
/// </summary>
public sealed class GameDataArchive
{
    private const int StateVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    // One write at a time from any window of this Arkive.
    private static readonly SemaphoreSlim Writing = new(1, 1);

    private readonly string _directory;
    private readonly string _statePath;
    private readonly string _legacyLanguageStatePath;
    private readonly string _backupDirectory;
    private readonly string _cacheDirectory;
    private readonly string _savedTextPath;
    private readonly IModDeploymentProcessGuard _processGuard;

    /// <param name="efGameDirectory">The game's EFGame folder (the one holding data2.lpk).</param>
    /// <param name="dataRoot">Arkive's data folder, for the backups and the record of what was changed.</param>
    public GameDataArchive(string efGameDirectory, string dataRoot, IModDeploymentProcessGuard processGuard)
    {
        ArchivePath = Path.Combine(Path.GetFullPath(efGameDirectory), "data2.lpk");
        _directory = Path.Combine(dataRoot, "game-data");
        _statePath = Path.Combine(_directory, "state.json");
        _legacyLanguageStatePath = Path.Combine(dataRoot, "languages", "state.json");
        _backupDirectory = Path.Combine(dataRoot, "backups", "game-data");
        _cacheDirectory = Path.Combine(dataRoot, "cache", "game-data");
        _savedTextPath = Path.Combine(dataRoot, "languages", "saved-text.db.gz");
        _processGuard = processGuard;
    }

    /// <summary>The game's data2.lpk.</summary>
    public string ArchivePath { get; }

    /// <summary>Arkive's change to data2.lpk, while the game has not replaced the file since.</summary>
    public GameDataArchiveState? LoadState()
    {
        var state = ReadState();
        return state is not null && !Replaced(state) ? state : null;
    }

    /// <summary>True when Arkive changed data2.lpk and the game has since replaced it (an update or a file check).</summary>
    public bool GameUpdated() => ReadState() is { } state && Replaced(state);

    /// <summary>What data2.lpk holds now: Arkive's change while it is installed, else the game's own.</summary>
    public GameDataTarget Current() =>
        LoadState() is { } state ? new GameDataTarget(state.Language, state.Edits) : GameDataTarget.GameOwn;

    /// <summary>The table databases in data2.lpk (EFTable_Item.db, and the jss/... variants), by name.</summary>
    public IReadOnlyList<string> Databases()
    {
        using var stream = File.OpenRead(ArchivePath);
        return LostArkDataArchive.ReadEntries(stream)
            .Select(entry => entry.Path.TrimStart('/'))
            .Where(path => path.StartsWith(GameDataValues.TableFolder, StringComparison.OrdinalIgnoreCase))
            .Select(path => path[GameDataValues.TableFolder.Length..])
            .Where(GameDataValues.IsDatabaseName)
            // The game's own tables first; the subfolders hold variants of them.
            .OrderBy(name => name.Contains('/'))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Arkive's last change to data2.lpk, even when a game update has since replaced it.</summary>
    public GameDataArchiveState? LastWrite() => ReadState();

    /// <summary>
    /// Names the game's own data2.lpk (not Arkive's change to it): the same until a game update replaces the file.
    /// </summary>
    public string GameVersion()
    {
        var (length, written) = LoadState() is { } state ? (state.OriginalLength, state.OriginalWriteTimeUtc) : Stamp(ArchivePath);
        return $"{length}:{written.Ticks}";
    }

    /// <summary>
    /// The game's own copy of <paramref name="database"/> (EFTable_Item.db), from Arkive's backup when Arkive has changed
    /// it. The copy is kept in Arkive's cache until the game's file changes, so treat it as read-only.
    /// </summary>
    public async Task<string> GameDatabaseAsync(string database, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ArchivePath)) throw new FileNotFoundException("The game's data2.lpk was not found.", ArchivePath);
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GameVersion())))[..16];
        var folder = Path.Combine(_cacheDirectory, version);
        var path = Path.Combine(folder, database.Replace('/', '_'));
        if (File.Exists(path)) return path;

        Directory.CreateDirectory(folder);
        foreach (var stale in Directory.EnumerateDirectories(_cacheDirectory).Where(item =>
                     !Path.GetFileName(item).Equals(version, StringComparison.OrdinalIgnoreCase)))
            TryDelete(stale);
        var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await ExtractGameDatabaseAsync(database, temporary, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        return path;
    }

    /// <summary>Writes the game's own copy of <paramref name="database"/> to <paramref name="output"/>.</summary>
    public async Task ExtractGameDatabaseAsync(string database, string output, CancellationToken cancellationToken = default)
    {
        if (!GameDataValues.IsDatabaseName(database)) throw new ArgumentException($"'{database}' is not a game table database.", nameof(database));
        var entry = GameDataValues.Entry(database);
        var backup = LoadState()?.Entries.FirstOrDefault(item => item.Entry.Equals(entry, StringComparison.OrdinalIgnoreCase));
        File.Delete(output);
        if (backup is not null) await DecompressAsync(backup.BackupPath, output, cancellationToken).ConfigureAwait(false);
        else LostArkDatabaseArchive.Extract(ArchivePath, entry, output, cancellationToken);
    }

    /// <summary>
    /// Makes data2.lpk hold <paramref name="target"/>: the game's databases with the chosen language written over the
    /// English text and every edit applied. <see cref="GameDataTarget.GameOwn"/> puts back the game's own file.
    /// </summary>
    public async Task<GameDataWriteResult> WriteAsync(GameDataTarget target, CancellationToken cancellationToken = default)
    {
        await Writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await WriteLockedAsync(target, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Writing.Release();
        }
    }

    private async Task<GameDataWriteResult> WriteLockedAsync(GameDataTarget target, CancellationToken cancellationToken)
    {
        RefuseWhileRunning();
        if (!File.Exists(ArchivePath)) throw new FileNotFoundException("The game's data2.lpk was not found.", ArchivePath);
        foreach (var edit in target.Edits)
            if (!GameDataValues.IsDatabaseName(edit.Database))
                throw new InvalidDataException($"'{edit.Database}' is not a game table database.");

        var state = ReadState();
        if (state is not null && Replaced(state))
        {
            // The game wrote a new data2.lpk over Arkive's: that file is the game's own now.
            File.Delete(_statePath);
            state = null;
        }
        var current = await HashAsync(ArchivePath, cancellationToken).ConfigureAwait(false);
        if (state is not null && !current.Equals(state.AppliedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("data2.lpk was changed outside Arkive since Arkive last wrote it. Verify the game files in Steam, then try again.");

        var wanted = Wanted(target);
        var installed = state?.Entries.ToDictionary(item => item.Entry, StringComparer.OrdinalIgnoreCase) ??
                        new Dictionary<string, GameDataEntryBackup>(StringComparer.OrdinalIgnoreCase);
        var changing = wanted.Keys.Concat(installed.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(entry => !(wanted.TryGetValue(entry, out var want) && installed.TryGetValue(entry, out var have) &&
                              want.Fingerprint == have.Fingerprint))
            .ToArray();
        var languageLines = state is not null && state.Language == target.Language ? state.LanguageLines : 0;
        var cells = target.Edits.Count;
        if (changing.Length == 0) return new GameDataWriteResult(languageLines, cells, []);
        if (target.Language is not null && !File.Exists(_savedTextPath))
            throw new InvalidOperationException("Arkive has no saved copy of the game's translations yet.");

        using var work = new WorkFolder(_directory);
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var backups = new Dictionary<string, GameDataEntryBackup>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, backup) in installed)
            if (!changing.Contains(entry, StringComparer.OrdinalIgnoreCase)) backups[entry] = backup;
        foreach (var entry in changing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var database = work.File(Path.GetFileName(entry));
            string backupPath;
            if (installed.TryGetValue(entry, out var kept))
            {
                if (!File.Exists(kept.BackupPath))
                    throw new InvalidDataException($"Arkive's copy of the game's {Path.GetFileName(entry)} is missing. Verify the game files in Steam to put the original back.");
                await DecompressAsync(kept.BackupPath, database, cancellationToken).ConfigureAwait(false);
                backupPath = kept.BackupPath;
            }
            else
            {
                // The game's own database, kept before anything is changed so the change can always be undone.
                LostArkDatabaseArchive.Extract(ArchivePath, entry, database, cancellationToken);
                var hash = await HashAsync(database, cancellationToken).ConfigureAwait(false);
                backupPath = Path.Combine(_backupDirectory, $"{Path.GetFileNameWithoutExtension(entry)}-{hash[..16]}.db.gz");
                if (!File.Exists(backupPath)) await CompressAsync(database, backupPath, cancellationToken).ConfigureAwait(false);
            }
            replacements[entry] = database;
            if (!wanted.TryGetValue(entry, out var change)) continue;

            var marker = GameDatabasePages.Marker(database);
            if (change.Language is not null)
                languageLines = await GameLanguageService.WriteTextAsync(_savedTextPath, database, change.Language, cancellationToken).ConfigureAwait(false);
            GameDataTables.Apply(database, change.Edits, cancellationToken);
            GameDatabasePages.Mark(database, marker);
            backups[entry] = new GameDataEntryBackup(entry, backupPath, change.Fingerprint);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var originalSha = state?.OriginalSha256 ?? current;
        var originalWritten = state?.OriginalWriteTimeUtc ?? Stamp(ArchivePath).WrittenUtc;
        var originalLength = state?.OriginalLength ?? Stamp(ArchivePath).Length;
        var staged = ArchivePath + ".arkive-new";
        File.Delete(staged);
        try
        {
            LostArkDatabaseArchive.Replace(ArchivePath, replacements, staged, cancellationToken);
            var applied = await HashAsync(staged, cancellationToken).ConfigureAwait(false);
            if (target.IsGameOwn)
            {
                if (!applied.Equals(originalSha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Arkive could not rebuild the game's own data2.lpk exactly. Verify the game files in Steam to put the original back.");
            }
            else
            {
                foreach (var (entry, database) in replacements)
                {
                    var check = work.File("check.db");
                    File.Delete(check);
                    LostArkDatabaseArchive.Extract(staged, entry, check, cancellationToken);
                    if (!(await HashAsync(check, cancellationToken).ConfigureAwait(false))
                            .Equals(await HashAsync(database, cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"The rewritten {Path.GetFileName(entry)} did not read back the same.");
                }
            }
            RefuseWhileRunning();
            File.Move(staged, ArchivePath, overwrite: true);
            if (target.IsGameOwn)
            {
                // The game's own date too, so the file is just as it was.
                File.SetLastWriteTimeUtc(ArchivePath, originalWritten);
                File.Delete(_statePath);
            }
            else
            {
                var (length, written) = Stamp(ArchivePath);
                WriteJson(_statePath, JsonSerializer.SerializeToNode(new GameDataArchiveState(
                    StateVersion, ArchivePath, originalSha, originalLength, originalWritten, applied, length, written,
                    backups.Values.OrderBy(item => item.Entry, StringComparer.OrdinalIgnoreCase).ToArray(), target.Language,
                    target.Language is null ? 0 : languageLines, target.Edits, DateTimeOffset.UtcNow), JsonOptions)!);
            }
        }
        finally
        {
            File.Delete(staged);
        }
        return new GameDataWriteResult(target.Language is null ? 0 : languageLines, cells,
            changing.Select(entry => Path.GetFileName(entry)).ToArray());
    }

    private sealed record EntryChange(string? Language, IReadOnlyList<GameDataCellEdit> Edits, string Fingerprint);

    // The databases the target changes, each with what is written into it; the language goes first, then the edits.
    private static Dictionary<string, EntryChange> Wanted(GameDataTarget target)
    {
        var wanted = new Dictionary<string, EntryChange>(StringComparer.OrdinalIgnoreCase);
        var groups = target.Edits.GroupBy(edit => GameDataValues.Entry(edit.Database), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<GameDataCellEdit>)group.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (target.Language is not null && !groups.ContainsKey(GameLanguageService.TextEntry)) groups[GameLanguageService.TextEntry] = [];
        foreach (var (entry, edits) in groups)
        {
            var language = entry.Equals(GameLanguageService.TextEntry, StringComparison.OrdinalIgnoreCase) ? target.Language : null;
            var description = JsonSerializer.Serialize(new { language, edits = edits.Select(edit => new { edit.Table, edit.Row, edit.Column, edit.Value }) });
            wanted[entry] = new EntryChange(language, edits, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(description)))[..32]);
        }
        return wanted;
    }

    private GameDataArchiveState? ReadState()
    {
        MigrateLanguageState();
        if (!File.Exists(_statePath)) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(_statePath));
            return node?["Version"]?.GetValue<int>() == StateVersion ? node.Deserialize<GameDataArchiveState>(JsonOptions) : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    // Arkive 0.5.0 to 0.5.5 kept the Language tab's change in languages\state.json; it becomes this file's record.
    private void MigrateLanguageState()
    {
        if (!File.Exists(_legacyLanguageStatePath) || File.Exists(_statePath)) return;
        try
        {
            var legacy = JsonNode.Parse(File.ReadAllText(_legacyLanguageStatePath));
            if (legacy?["Version"]?.GetValue<int>() == 1 && File.Exists(ArchivePath))
            {
                var (length, written) = Stamp(ArchivePath);
                if (legacy["AppliedLength"]!.GetValue<long>() == length && legacy["AppliedWriteTimeUtc"]!.GetValue<DateTime>() == written)
                {
                    var language = legacy["Language"]!.GetValue<string>();
                    var fingerprint = Wanted(new GameDataTarget(language, []))[GameLanguageService.TextEntry].Fingerprint;
                    WriteJson(_statePath, JsonSerializer.SerializeToNode(new GameDataArchiveState(
                        // The old record has no original size, so the saved translations are read once more.
                        StateVersion, ArchivePath, legacy["OriginalSha256"]!.GetValue<string>(), -1, legacy["OriginalWriteTimeUtc"]!.GetValue<DateTime>(),
                        legacy["AppliedSha256"]!.GetValue<string>(), length, written,
                        [new GameDataEntryBackup(GameLanguageService.TextEntry, legacy["BackupPath"]!.GetValue<string>(), fingerprint)],
                        language, legacy["Lines"]!.GetValue<int>(), [], legacy["AppliedAt"]!.GetValue<DateTimeOffset>()), JsonOptions)!);
                }
            }
            File.Delete(_legacyLanguageStatePath);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException or NullReferenceException)
        {
        }
    }

    private bool Replaced(GameDataArchiveState state)
    {
        if (!File.Exists(ArchivePath)) return true;
        var (length, written) = Stamp(ArchivePath);
        return length != state.AppliedLength || written != state.AppliedWriteTimeUtc;
    }

    public void RefuseWhileRunning()
    {
        var running = _processGuard.FindBlockingProcesses();
        if (running.Count > 0)
            throw new InvalidOperationException($"Close Lost Ark first: {string.Join(", ", running)} is running.");
    }

    internal static (long Length, DateTime WrittenUtc) Stamp(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private static void WriteJson(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, node.ToJsonString(JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    internal static async Task CompressAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        await using (var input = File.OpenRead(source))
        await using (var output = File.Create(temporary))
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            await input.CopyToAsync(gzip, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, destination, overwrite: true);
    }

    internal static async Task DecompressAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(source);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        await using var output = File.Create(destination);
        await gzip.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A folder for the databases being worked on, deleted afterwards.</summary>
    internal sealed class WorkFolder : IDisposable
    {
        private readonly string _path;

        public WorkFolder(string parent)
        {
            _path = Path.Combine(parent, "work-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_path);
        }

        public string File(string name) => Path.Combine(_path, name);

        public void Dispose() => TryDelete(_path);
    }
}
