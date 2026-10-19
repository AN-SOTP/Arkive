using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arkive.Core.Discovery;
using Arkive.Core.Mods;
using Arkive.Core.Skills;

namespace Arkive.Core.LoginScreens;

/// <summary>One login and server select background the game still ships: a video in EFGame\Movies.</summary>
/// <param name="Movie">The game's name for it (login_cinema_kazeroth); the one the game plays is <see cref="LoginScreenService.GameMovie"/>.</param>
/// <param name="Title">A readable name (Kazeros).</param>
/// <param name="FilePath">The video's file.</param>
public sealed record LoginScreen(string Movie, string Title, string FilePath, long Length);

/// <summary>What Arkive has changed: which background it set, and the game file it changed, before and after.</summary>
public sealed record LoginScreenState(string Movie, string OriginalSha256, string AppliedSha256, string BackupPath, DateTimeOffset AppliedAt);

/// <summary>
/// The login and server select background. The game plays the video its movie table (Table_MovieData.loa in
/// data1.lpk) names for LogIn.Background, which is always login_cinema, the current patch's. Older backgrounds stay in
/// the Movies folder under their own names (login_cinema_kazeroth...). Choosing one points the table at it: Arkive
/// rewrites that one table inside data1.lpk, keeping a clean copy of the file to put back.
/// </summary>
public sealed class LoginScreenService
{
    /// <summary>The video the game plays when nothing is changed.</summary>
    public const string GameMovie = "login_cinema";

    private const string TableEntry = "/EFGame_Extra/Config/Table_MovieData.loa";
    private static readonly string[] Keys = ["LogIn.Background", "LogIn.Background_Event"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Readable names for the developers' ones; anything new is shown from its own name.
    private static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kazeroth"] = "Kazeros", ["bern"] = "Vern", ["kurzan"] = "Kurzan", ["voldaik"] = "Voldis", ["limlake"] = "Rimlake",
        ["papunika"] = "Papunika", ["commander"] = "Legion Commanders", ["ocean"] = "Ocean", ["eclipse"] = "Eclipse",
        ["ergasia"] = "Ergasia", ["faton"] = "Faton", ["south"] = "South", ["north"] = "North", ["wide"] = "(wide)",
        ["1end"] = "Act 1 ending",
    };

    private readonly string _efGame;
    private readonly string _statePath;
    private readonly string _backupDirectory;
    private readonly IModDeploymentProcessGuard _processGuard;

    /// <param name="efGameDirectory">The game's EFGame folder (the one holding data1.lpk and Movies).</param>
    /// <param name="dataRoot">Arkive's data folder, for the backup and the record of what was changed.</param>
    public LoginScreenService(string efGameDirectory, string dataRoot, IModDeploymentProcessGuard processGuard)
    {
        _efGame = Path.GetFullPath(efGameDirectory);
        _statePath = Path.Combine(dataRoot, "login-screens", "state.json");
        _backupDirectory = Path.Combine(dataRoot, "backups", "login-screens");
        _processGuard = processGuard;
    }

    public string ArchivePath => Path.Combine(_efGame, "data1.lpk");

    /// <summary>The EFGame folder for a game Packages folder (EFGame\ReleasePC\Packages).</summary>
    public static string? EfGameFromPackages(string? packagesDirectory) =>
        packagesDirectory is null ? null : Directory.GetParent(packagesDirectory)?.Parent?.FullName;

    /// <summary>Every login background in the Movies folder, the game's own first.</summary>
    public IReadOnlyList<LoginScreen> Find()
    {
        var movies = Path.Combine(_efGame, "Movies");
        if (!Directory.Exists(movies)) return [];
        return Directory.EnumerateFiles(movies, "*.ipk")
            .Select(path => LostArkPackageName.TryDecodeFileName(path, out var name) ? (path, name) : (path, name: string.Empty))
            .Where(item => item.name.StartsWith("login_", StringComparison.OrdinalIgnoreCase))
            .Select(item => new LoginScreen(item.name, Title(item.name), item.path, new FileInfo(item.path).Length))
            .OrderBy(screen => screen.Movie.Equals(GameMovie, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(screen => screen.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string Title(string movie)
    {
        if (movie.Equals(GameMovie, StringComparison.OrdinalIgnoreCase)) return "The game's own (this patch)";
        var words = movie.Split('_').Where(word => word is not ("login" or "cinema")).ToArray();
        return words.Length == 0 ? movie : string.Join(" ", words.Select(word =>
            Titles.TryGetValue(word, out var title) ? title : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    /// <summary>The background the game will play, as its movie table names it now.</summary>
    public string CurrentMovie()
    {
        using var stream = File.OpenRead(ArchivePath);
        var table = ReadTable(stream);
        return Read(table, Keys[0]) ?? GameMovie;
    }

    public LoginScreenState? LoadState()
    {
        if (!File.Exists(_statePath)) return null;
        using var stream = File.OpenRead(_statePath);
        return JsonSerializer.Deserialize<LoginScreenState>(stream, JsonOptions);
    }

    /// <summary>
    /// True when the game has replaced data1.lpk since Arkive changed it (a patch): the change is gone, and choosing
    /// again applies it to the new file.
    /// </summary>
    public async Task<bool> GameUpdatedAsync(CancellationToken cancellationToken = default) =>
        LoadState() is { } state && File.Exists(ArchivePath) &&
        !(await HashAsync(ArchivePath, cancellationToken).ConfigureAwait(false))
            .Equals(state.AppliedSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes the game play <paramref name="movie"/> on the login and server select screen.</summary>
    public async Task ApplyAsync(string movie, CancellationToken cancellationToken = default)
    {
        RefuseWhileRunning();
        if (!Find().Any(screen => screen.Movie.Equals(movie, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{movie} is not in the game's Movies folder.");
        var state = LoadState();
        var current = await HashAsync(ArchivePath, cancellationToken).ConfigureAwait(false);
        // The clean file is the one the game shipped: the backup if Arkive's change is still installed, else the file now.
        var cleanPath = state is not null && current.Equals(state.AppliedSha256, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(state.BackupPath)
            ? state.BackupPath : ArchivePath;
        var cleanSha = cleanPath == ArchivePath ? current : state!.OriginalSha256;
        if (movie.Equals(GameMovie, StringComparison.OrdinalIgnoreCase))
        {
            await RestoreAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        Directory.CreateDirectory(_backupDirectory);
        var backup = Path.Combine(_backupDirectory, $"data1-{cleanSha[..16]}.lpk");
        if (!File.Exists(backup) || !(await HashAsync(backup, cancellationToken).ConfigureAwait(false))
                .Equals(cleanSha, StringComparison.OrdinalIgnoreCase))
        {
            var copying = backup + ".copying";
            File.Copy(cleanPath, copying, overwrite: true);
            File.Move(copying, backup, overwrite: true);
        }

        byte[] table;
        using (var stream = File.OpenRead(backup)) table = ReadTable(stream);
        var changed = Keys.Aggregate(table, (bytes, key) => Write(bytes, key, movie));
        var staged = ArchivePath + ".arkive-new";
        if (File.Exists(staged)) File.Delete(staged);
        LostArkDataArchive.ReplaceEntry(backup, TableEntry, changed, staged, cancellationToken);
        // The rewritten archive must read back with the new table before it replaces the game's.
        using (var check = File.OpenRead(staged))
            if (!string.Equals(Read(ReadTable(check), Keys[0]), movie, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The rewritten data1.lpk did not read back with the chosen background.");
        var applied = await HashAsync(staged, cancellationToken).ConfigureAwait(false);
        File.Move(staged, ArchivePath, overwrite: true);
        SaveState(new LoginScreenState(movie, cleanSha, applied, backup, DateTimeOffset.UtcNow));
    }

    /// <summary>Puts the game's own data1.lpk back (only if Arkive's change is still the one installed).</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        RefuseWhileRunning();
        if (LoadState() is not { } state) return;
        var current = await HashAsync(ArchivePath, cancellationToken).ConfigureAwait(false);
        if (current.Equals(state.AppliedSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(state.BackupPath) || !(await HashAsync(state.BackupPath, cancellationToken).ConfigureAwait(false))
                    .Equals(state.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Arkive's clean copy of data1.lpk is missing or damaged. Verify the game files in Steam to put the original back.");
            var restoring = ArchivePath + ".arkive-restore";
            File.Copy(state.BackupPath, restoring, overwrite: true);
            File.Move(restoring, ArchivePath, overwrite: true);
        }
        File.Delete(_statePath);
    }

    private void RefuseWhileRunning()
    {
        var running = _processGuard.FindBlockingProcesses();
        if (running.Count > 0)
            throw new InvalidOperationException($"Close Lost Ark first: {string.Join(", ", running)} is running.");
    }

    private void SaveState(LoginScreenState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporary = _statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, _statePath, overwrite: true);
    }

    private static byte[] ReadTable(Stream archive)
    {
        var entry = LostArkDataArchive.ReadEntries(archive).FirstOrDefault(item => item.Path.Equals(TableEntry, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("The game's movie table is missing from data1.lpk; the game's files may have changed.");
        return LostArkDataArchive.Read(archive, entry);
    }

    /// <summary>
    /// The movie a table entry plays. Entries are the key, then their settings; the movie is the second
    /// length-prefixed string after the key (the first is the player's name, loginBackGround).
    /// </summary>
    public static string? Read(byte[] table, string key) => FindMovie(table, key) is { } found
        ? Encoding.ASCII.GetString(table, found.Offset + 4, found.Length - 1) : null;

    /// <summary>The table with <paramref name="key"/>'s movie set to <paramref name="movie"/> (the rest unchanged).</summary>
    public static byte[] Write(byte[] table, string key, string movie)
    {
        if (FindMovie(table, key) is not { } found) return table;
        var name = Encoding.ASCII.GetBytes(movie + "\0");
        var result = new byte[table.Length - (4 + found.Length) + 4 + name.Length];
        table.AsSpan(0, found.Offset).CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(found.Offset), name.Length);
        name.CopyTo(result, found.Offset + 4);
        table.AsSpan(found.Offset + 4 + found.Length).CopyTo(result.AsSpan(found.Offset + 4 + name.Length));
        return result;
    }

    private static (int Offset, int Length)? FindMovie(byte[] table, string key)
    {
        var keyBytes = Encoding.ASCII.GetBytes(key + "\0");
        for (var at = 4; at + keyBytes.Length <= table.Length; at++)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(at - 4)) != keyBytes.Length ||
                !table.AsSpan(at, keyBytes.Length).SequenceEqual(keyBytes)) continue;
            var strings = 0;
            for (var scan = at + keyBytes.Length; scan + 4 < table.Length && scan < at + 256; scan++)
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(scan));
                if (length is < 2 or > 128 || scan + 4 + length > table.Length || table[scan + 4 + length - 1] != 0 ||
                    !table.AsSpan(scan + 4, length - 1).ToArray().All(b => b is >= 0x20 and < 0x7F)) continue;
                if (++strings == 2) return (scan, length);
                scan += 3 + length;
            }
            return null;
        }
        return null;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
