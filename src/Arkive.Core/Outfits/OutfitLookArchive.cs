using System.Security.Cryptography;
using System.Text.Json;
using Arkive.Core.Mods;
using Arkive.Core.Skills;

namespace Arkive.Core.Outfits;

/// <summary>What Arkive has changed in the game's data4.lpk (the item looks), and the copy that puts the game's file back.</summary>
public sealed record OutfitLookArchiveState(
    int Version, string ArchivePath, string OriginalSha256, long OriginalLength, DateTime OriginalWriteTimeUtc, string BackupPath,
    string AppliedSha256, long AppliedLength, DateTime AppliedWriteTimeUtc, IReadOnlyList<OutfitLookEdit> Edits, DateTimeOffset AppliedAt);

/// <summary>
/// The one writer of the game's data4.lpk for outfit looks. Every write starts from a copy of the game's own file, kept
/// before Arkive first changes it, and writing no edits puts that copy back, date included, so the game's file comes
/// back byte for byte. A game update replaces data4.lpk; Arkive notices by its size and date and starts from the new file.
/// </summary>
public sealed class OutfitLookArchive
{
    private const int StateVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Writing = new(1, 1);

    private readonly string _statePath;
    private readonly string _backupDirectory;
    private readonly IModDeploymentProcessGuard _processGuard;

    /// <param name="efGameDirectory">The game's EFGame folder (the one holding data4.lpk).</param>
    /// <param name="dataRoot">Arkive's data folder, for the copy of the game's file and the record of what was changed.</param>
    public OutfitLookArchive(string efGameDirectory, string dataRoot, IModDeploymentProcessGuard processGuard)
    {
        ArchivePath = Path.Combine(Path.GetFullPath(efGameDirectory), "data4.lpk");
        _statePath = Path.Combine(dataRoot, "outfit-looks", "state.json");
        _backupDirectory = Path.Combine(dataRoot, "backups", "outfit-looks");
        _processGuard = processGuard;
    }

    public string ArchivePath { get; }

    /// <summary>Arkive's change to data4.lpk, while the game has not replaced the file since.</summary>
    public OutfitLookArchiveState? LoadState() => ReadState() is { } state && !Replaced(state) ? state : null;

    /// <summary>The look edits data4.lpk holds now (none when it is the game's own).</summary>
    public IReadOnlyList<OutfitLookEdit> Current() => LoadState()?.Edits ?? [];

    /// <summary>The game's own data4.lpk: Arkive's copy while Arkive has changed it.</summary>
    public string GameArchivePath() => LoadState()?.BackupPath ?? ArchivePath;

    /// <summary>The game's own item looks whose mesh is <paramref name="meshObject"/>.</summary>
    public IReadOnlyList<OutfitLook> GameLooks(string meshObject, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(GameArchivePath());
        return OutfitLooks.ForMesh(stream, LostArkDataArchive.ReadEntries(stream, cancellationToken), meshObject, cancellationToken);
    }

    /// <summary>The game's own looks at <paramref name="entries"/> (an entry the game has no look at is left out).</summary>
    public IReadOnlyDictionary<string, OutfitLook> GameLooks(IEnumerable<string> entries, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(GameArchivePath());
        var all = LostArkDataArchive.ReadEntries(stream, cancellationToken)
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var looks = new Dictionary<string, OutfitLook>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Distinct(StringComparer.OrdinalIgnoreCase))
            if (all.TryGetValue(entry, out var found) && OutfitLooks.Read(found.Path, LostArkDataArchive.Read(stream, found)) is { } look)
                looks[entry] = look;
        return looks;
    }

    /// <summary>Makes data4.lpk the game's own looks with <paramref name="edits"/> written in; no edits puts back the game's file.</summary>
    public async Task WriteAsync(IReadOnlyList<OutfitLookEdit> edits, CancellationToken cancellationToken = default)
    {
        await Writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteLockedAsync(edits, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Writing.Release();
        }
    }

    private async Task WriteLockedAsync(IReadOnlyList<OutfitLookEdit> edits, CancellationToken cancellationToken)
    {
        RefuseWhileRunning();
        if (!File.Exists(ArchivePath)) throw new FileNotFoundException("The game's data4.lpk was not found.", ArchivePath);
        if (edits.GroupBy(edit => edit.Key, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidDataException("A look slot was given more than one material.");
        var state = ReadState();
        var current = await HashAsync(ArchivePath, cancellationToken).ConfigureAwait(false);
        if (state is not null && Replaced(state) && !current.Equals(state.AppliedSha256, StringComparison.OrdinalIgnoreCase))
        {
            // The game wrote a new data4.lpk over Arkive's (an update, or Steam's file check): that file is the game's
            // own now, and the copy of the old one is stale. Only the date changing leaves Arkive's file as it is.
            File.Delete(_statePath);
            if (File.Exists(state.BackupPath)) File.Delete(state.BackupPath);
            state = null;
        }
        if (state is null && edits.Count == 0) return;

        var (originalLength, originalWritten) = state is null ? Stamp(ArchivePath) : (state.OriginalLength, state.OriginalWriteTimeUtc);
        var originalSha = state?.OriginalSha256 ?? current;
        var backup = state?.BackupPath ?? Path.Combine(_backupDirectory, $"data4-{originalSha[..16]}.lpk");
        if (state is null && (!File.Exists(backup) || !(await HashAsync(backup, cancellationToken).ConfigureAwait(false))
                .Equals(originalSha, StringComparison.OrdinalIgnoreCase)))
        {
            // The game's own file, kept before anything is changed so the change can always be undone.
            Directory.CreateDirectory(_backupDirectory);
            File.Copy(ArchivePath, backup + ".tmp", overwrite: true);
            File.Move(backup + ".tmp", backup, overwrite: true);
        }
        if (!(await HashAsync(backup, cancellationToken).ConfigureAwait(false)).Equals(originalSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Arkive's copy of the game's data4.lpk is missing or changed. Verify the game files in Steam to put the original back.");

        var staged = ArchivePath + ".arkive-new";
        File.Delete(staged);
        try
        {
            if (edits.Count == 0) File.Copy(backup, staged);
            else
            {
                var changes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                using (var source = File.OpenRead(backup))
                {
                    var entries = LostArkDataArchive.ReadEntries(source, cancellationToken);
                    foreach (var group in edits.GroupBy(edit => edit.Entry, StringComparer.OrdinalIgnoreCase))
                    {
                        var entry = entries.FirstOrDefault(item => item.Path.Equals(group.Key, StringComparison.OrdinalIgnoreCase)) ??
                                    throw new InvalidDataException($"The game has no look {group.Key}.");
                        changes[entry.Path] = OutfitLooks.Patch(entry.Path, LostArkDataArchive.Read(source, entry), group);
                    }
                }
                LostArkDataArchive.Rewrite(backup, changes, staged, cancellationToken);
                using var check = File.OpenRead(staged);
                var written = LostArkDataArchive.ReadEntries(check, cancellationToken);
                foreach (var (entry, bytes) in changes)
                    if (!LostArkDataArchive.Read(check, written.Single(item => item.Path.Equals(entry, StringComparison.OrdinalIgnoreCase)))
                            .AsSpan().SequenceEqual(bytes))
                        throw new InvalidDataException($"The rewritten look {entry} did not read back the same.");
            }
            var applied = await HashAsync(staged, cancellationToken).ConfigureAwait(false);
            if (edits.Count == 0 && !applied.Equals(originalSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Arkive could not put back the game's own data4.lpk exactly. Verify the game files in Steam.");
            RefuseWhileRunning();
            File.Move(staged, ArchivePath, overwrite: true);
            if (edits.Count == 0)
            {
                // The game's own date too, so the file is just as it was.
                File.SetLastWriteTimeUtc(ArchivePath, originalWritten);
                File.Delete(_statePath);
                return;
            }
            var (length, writtenAt) = Stamp(ArchivePath);
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            await File.WriteAllTextAsync(_statePath + ".tmp", JsonSerializer.Serialize(new OutfitLookArchiveState(
                StateVersion, ArchivePath, originalSha, originalLength, originalWritten, backup, applied, length, writtenAt,
                edits.OrderBy(edit => edit.Key, StringComparer.Ordinal).ToArray(), DateTimeOffset.UtcNow), JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            File.Move(_statePath + ".tmp", _statePath, overwrite: true);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    public void RefuseWhileRunning()
    {
        var running = _processGuard.FindBlockingProcesses();
        if (running.Count > 0)
            throw new InvalidOperationException($"Close Lost Ark first: {string.Join(", ", running)} is running.");
    }

    private OutfitLookArchiveState? ReadState()
    {
        if (!File.Exists(_statePath)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<OutfitLookArchiveState>(File.ReadAllText(_statePath), JsonOptions);
            return state?.Version == StateVersion ? state : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    private bool Replaced(OutfitLookArchiveState state)
    {
        if (!File.Exists(ArchivePath)) return true;
        var (length, written) = Stamp(ArchivePath);
        return length != state.AppliedLength || written != state.AppliedWriteTimeUtc;
    }

    private static (long Length, DateTime WrittenUtc) Stamp(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
