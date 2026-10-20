using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arkive.Core.Discovery;
using Arkive.Core.Mods;

namespace Arkive.Core.LoginScreens;

/// <summary>One login and server select screen the game still ships: a video in EFGame\Movies and its sounds.</summary>
/// <param name="Movie">The game's name for the video (login_cinema_kazeroth).</param>
/// <param name="Title">A readable name (Kazeros).</param>
/// <param name="FilePath">The video's file.</param>
/// <param name="Music">The screen's music event (bgm_login_*).</param>
/// <param name="Ambience">The screen's ambience events' stem (amb_intro_*: _start, _stop, _pagechange1).</param>
/// <param name="CanUse">False when Arkive cannot read the video (a format it does not know).</param>
/// <param name="SameAs">Another screen with the very same video, when there is one listed before it.</param>
public sealed record LoginScreen(
    string Movie, string Title, string FilePath, long Length, string Music, string Ambience, bool CanUse, string? SameAs = null);

/// <summary>The video file Arkive replaced: the game's copy, kept as a backup, and what Arkive wrote.</summary>
public sealed record LoginScreenVideoChange(string Path, string OriginalSha256, string AppliedSha256, string BackupPath);

/// <summary>What Arkive has changed: the screen the game shows (<paramref name="Live"/>) now plays <paramref name="Movie"/>.</summary>
public sealed record LoginScreenState(
    int Version, string Live, string Movie, LoginScreenVideoChange Video, IReadOnlyList<LoginSoundPatch> Sounds, DateTimeOffset AppliedAt);

/// <summary>
/// The login and server select screen. The game picks the screen it shows itself (the current update's, not a file
/// Arkive can change), so Arkive gives that screen another one's video, music and ambience: the chosen video is
/// written over the shown one's file (rescrambled with that file's key, <see cref="LoginVideoCipher"/>) and the shown
/// screen's sound events are pointed at the chosen screen's (<see cref="LoginScreenSounds"/>). The game's video is
/// kept as a backup and every changed sound id is recorded, so going back puts the game's files back exactly.
/// Which screen the game shows is <see cref="LiveMovie"/>: Act 1 ending in late 2026, changeable when an update brings
/// a new one.
/// </summary>
public sealed class LoginScreenService
{
    /// <summary>The game's default video name; the game shows its own update's screen instead.</summary>
    public const string GameMovie = "login_cinema";

    /// <summary>The screen the game shows, until the person says otherwise (the current one as of October 2026).</summary>
    public const string DefaultLiveMovie = "login_cinema_1end";

    private const int StateVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Every known screen in the order the game added them: video name ending, title, music event, ambience stem.
    private static readonly (string Name, string Title, string Music, string Ambience)[] Known =
    [
        ("ocean", "Ocean (launch)", "bgm_login_ocean", "ocean"),
        ("", "Ocean (launch)", "bgm_login_ocean", "ocean"),
        ("papunika", "Papunika", "bgm_login_papunika", "papunika"),
        ("commander", "Legion Commanders", "bgm_login_commander", "commander"),
        ("bern_south_wide", "South Vern", "bgm_login_commander", "commander"),
        ("bern_south", "South Vern (narrow)", "bgm_login_commander", "commander"),
        ("voldaik", "Voldis", "bgm_login_voldaik", "voldaik"),
        ("ergasia", "Elgacia", "bgm_login_elgacia", "ergasia"),
        ("eclipse", "Eclipse", "bgm_login_eclipse", "eclipse"),
        ("kazeroth", "Kazeros", "bgm_login_kazeroth_finalactraid", "kazeroth"),
        ("faton", "Faton", "bgm_login_faton", "paten"),
        ("kurzan_south", "South Kurzan", "bgm_login_kurzan_south", "kurzan_south"),
        ("kurzan_north", "North Kurzan", "bgm_login_kurzan_north", "kurzan_north"),
        ("limlake_south", "South Rimeria", "bgm_login_limlake_south", "limlake_south"),
        ("1end", "Act 1 ending", "bgm_login_victorysite", "1end"),
    ];

    private readonly string _efGame;
    private readonly string _statePath;
    private readonly string _settingsPath;
    private readonly string _backupDirectory;
    private readonly IModDeploymentProcessGuard _processGuard;

    /// <param name="efGameDirectory">The game's EFGame folder (the one holding Movies and ReleasePC).</param>
    /// <param name="dataRoot">Arkive's data folder, for the backups and the record of what was changed.</param>
    public LoginScreenService(string efGameDirectory, string dataRoot, IModDeploymentProcessGuard processGuard)
    {
        _efGame = Path.GetFullPath(efGameDirectory);
        _statePath = Path.Combine(dataRoot, "login-screens", "state.json");
        _settingsPath = Path.Combine(dataRoot, "login-screens", "settings.json");
        _backupDirectory = Path.Combine(dataRoot, "backups", "login-screens");
        _processGuard = processGuard;
    }

    public string MoviesDirectory => Path.Combine(_efGame, "Movies");

    public string AudioDirectory => Path.Combine(_efGame, "ReleasePC", "WwiseAudioPackage");

    /// <summary>The EFGame folder for a game Packages folder (EFGame\ReleasePC\Packages).</summary>
    public static string? EfGameFromPackages(string? packagesDirectory) =>
        packagesDirectory is null ? null : Directory.GetParent(packagesDirectory)?.Parent?.FullName;

    /// <summary>Every login screen in the Movies folder, in the order the game added them.</summary>
    public IReadOnlyList<LoginScreen> Find()
    {
        if (!Directory.Exists(MoviesDirectory)) return [];
        var state = LoadState();
        var screens = Directory.EnumerateFiles(MoviesDirectory, "*.ipk")
            .Select(path => LostArkPackageName.TryDecodeFileName(path, out var name) ? (path, name) : (path, name: string.Empty))
            .Where(item => item.name.StartsWith("login_", StringComparison.OrdinalIgnoreCase))
            .Select(item => Describe(item.name, item.path))
            .OrderBy(screen => Order(screen.Movie))
            .ThenBy(screen => screen.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Copies of one video under two names are listed once (the screen listed first keeps it).
        var seen = new Dictionary<string, string>();
        for (var index = 0; index < screens.Count; index++)
        {
            var screen = screens[index];
            if (!screen.CanUse || Fingerprint(VideoSource(screen, state)) is not { } print) continue;
            if (seen.TryGetValue(print, out var first)) screens[index] = screen with { SameAs = first };
            else seen[print] = screen.Movie;
        }
        return screens;
    }

    private static LoginScreen Describe(string movie, string path)
    {
        var name = Ending(movie);
        var known = Known.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        bool canUse;
        try
        {
            canUse = LoginVideoCipher.FindKey(path) is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            canUse = false;
        }
        return new LoginScreen(movie, Title(movie), path, new FileInfo(path).Length,
            known.Music ?? "bgm_login_" + name, "amb_intro_" + (known.Ambience ?? name), canUse);
    }

    // login_cinema_kazeroth -> kazeroth, login_bern_south -> bern_south, login_cinema -> "".
    private static string Ending(string movie)
    {
        foreach (var prefix in new[] { "login_cinema_", "login_cinema", "login_" })
            if (movie.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return movie[prefix.Length..];
        return movie;
    }

    private static int Order(string movie)
    {
        var index = Array.FindIndex(Known, item => item.Name.Equals(Ending(movie), StringComparison.OrdinalIgnoreCase));
        return index < 0 ? Known.Length : index;
    }

    /// <summary>The title of a screen by its video name.</summary>
    public static string Title(string movie)
    {
        var name = Ending(movie);
        var known = Known.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var title = known.Title ?? string.Join(" ", name.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
        return title.Length == 0 ? movie : title;
    }

    /// <summary>The file holding <paramref name="screen"/>'s own video: the backup when Arkive has replaced it.</summary>
    public string VideoSource(LoginScreen screen) => VideoSource(screen, LoadState());

    private static string VideoSource(LoginScreen screen, LoginScreenState? state) =>
        state is not null && state.Video.Path.Equals(screen.FilePath, StringComparison.OrdinalIgnoreCase) && File.Exists(state.Video.BackupPath)
            ? state.Video.BackupPath : screen.FilePath;

    private static string? Fingerprint(string path)
    {
        try
        {
            if (LoginVideoCipher.FindKey(path) is not { } key) return null;
            using var stream = File.OpenRead(path);
            var head = new byte[(int)Math.Min(stream.Length, 1 << 16)];
            stream.ReadExactly(head);
            LoginVideoCipher.Apply(head, key, 0);
            return $"{stream.Length}:{Convert.ToHexString(SHA256.HashData(head))}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>The screen the game shows on its own: the one the person set, else <see cref="DefaultLiveMovie"/>.</summary>
    public string LiveMovie()
    {
        var movies = Directory.Exists(MoviesDirectory) ? Find().Select(screen => screen.Movie).ToArray() : [];
        var set = LoadSettings();
        if (set is not null && movies.Contains(set, StringComparer.OrdinalIgnoreCase)) return set;
        return movies.FirstOrDefault(movie => movie.Equals(DefaultLiveMovie, StringComparison.OrdinalIgnoreCase)) ?? GameMovie;
    }

    /// <summary>Records which screen the game shows (after an update brings a new one). Arkive's change is undone first.</summary>
    public async Task SetLiveMovieAsync(string movie, CancellationToken cancellationToken = default)
    {
        if (LoadState() is not null) await RestoreAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        await File.WriteAllTextAsync(_settingsPath, new JsonObject { ["LiveMovie"] = movie }.ToJsonString(JsonOptions), cancellationToken)
            .ConfigureAwait(false);
    }

    private string? LoadSettings()
    {
        try
        {
            return File.Exists(_settingsPath) ? JsonNode.Parse(File.ReadAllText(_settingsPath))?["LiveMovie"]?.GetValue<string>() : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>The screen the game will play: Arkive's choice while it is installed, else the game's own.</summary>
    public async Task<string> CurrentMovieAsync(CancellationToken cancellationToken = default) =>
        LoadState() is { } state && !await GameUpdatedAsync(cancellationToken).ConfigureAwait(false) ? state.Movie : LiveMovie();

    public LoginScreenState? LoadState()
    {
        if (!File.Exists(_statePath)) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(_statePath));
            return node?["Version"]?.GetValue<int>() == StateVersion ? node.Deserialize<LoginScreenState>(JsonOptions) : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the game has replaced Arkive's change since it was made (an update or a file check): the game's
    /// own screen is back, and choosing again applies it to the new files.
    /// </summary>
    public async Task<bool> GameUpdatedAsync(CancellationToken cancellationToken = default)
    {
        if (LoadState() is not { } state) return false;
        if (!File.Exists(state.Video.Path) ||
            !(await HashAsync(state.Video.Path, cancellationToken).ConfigureAwait(false))
                .Equals(state.Video.AppliedSha256, StringComparison.OrdinalIgnoreCase)) return true;
        return !LoginScreenSounds.InPlace(state.Sounds);
    }

    /// <summary>Makes the screen the game shows play <paramref name="movie"/>'s video, music and ambience.</summary>
    /// <returns>What could not be linked (music or ambience the game does not have for one of the screens), if anything.</returns>
    public async Task<string?> ApplyAsync(string movie, CancellationToken cancellationToken = default)
    {
        RefuseWhileRunning();
        await RestoreAsync(cancellationToken).ConfigureAwait(false);
        var screens = Find();
        var live = LiveMovie();
        if (movie.Equals(live, StringComparison.OrdinalIgnoreCase)) return null;
        var chosen = screens.FirstOrDefault(screen => screen.Movie.Equals(movie, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException($"{movie} is not in the game's Movies folder.");
        var shown = screens.First(screen => screen.Movie.Equals(live, StringComparison.OrdinalIgnoreCase));
        var chosenKey = LoginVideoCipher.FindKey(chosen.FilePath)
                        ?? throw new InvalidDataException($"Arkive cannot read the {chosen.Title} video, so it cannot be used.");
        var shownKey = LoginVideoCipher.FindKey(shown.FilePath)
                       ?? throw new InvalidDataException($"Arkive cannot read the {shown.Title} video the game shows, so it cannot be replaced.");

        // The game's video is kept first, so the change can always be undone.
        var original = await HashAsync(shown.FilePath, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_backupDirectory);
        var backup = Path.Combine(_backupDirectory, $"{shown.Movie}-{original[..16]}.ipk");
        if (!File.Exists(backup) || !(await HashAsync(backup, cancellationToken).ConfigureAwait(false))
                .Equals(original, StringComparison.OrdinalIgnoreCase))
        {
            var copying = backup + ".copying";
            File.Copy(shown.FilePath, copying, overwrite: true);
            File.Move(copying, backup, overwrite: true);
        }

        var staged = shown.FilePath + ".arkive-new";
        LoginVideoCipher.Transcode(chosen.FilePath, chosenKey, staged, shownKey, cancellationToken);
        if (LoginVideoCipher.FindKey(staged) is not { } check || !check.AsSpan().SequenceEqual(shownKey))
        {
            File.Delete(staged);
            throw new InvalidDataException("The rewritten video did not read back as a login video.");
        }
        var applied = await HashAsync(staged, cancellationToken).ConfigureAwait(false);
        var video = new LoginScreenVideoChange(shown.FilePath, original, applied, backup);
        SaveState(new LoginScreenState(StateVersion, live, chosen.Movie, video, [], DateTimeOffset.UtcNow));
        File.Move(staged, shown.FilePath, overwrite: true);

        // Then the sounds: the shown screen's music and ambience events start the chosen screen's.
        var pairs = new[]
        {
            (shown.Music, chosen.Music),
            (shown.Ambience + "_start", chosen.Ambience + "_start"),
            (shown.Ambience + "_stop", chosen.Ambience + "_stop"),
            (shown.Ambience + "_pagechange1", chosen.Ambience + "_pagechange1"),
        };
        var actions = LoginScreenSounds.FindActions(AudioDirectory, pairs.SelectMany(pair => new[] { pair.Item1, pair.Item2 }).ToArray(),
            cancellationToken);
        var patches = LoginScreenSounds.Plan(actions, pairs);
        SaveState(new LoginScreenState(StateVersion, live, chosen.Movie, video, patches, DateTimeOffset.UtcNow));
        LoginScreenSounds.Write(patches, restore: false);

        bool Has(string name) => actions.Any(action => action.IsPlay && action.Event.Equals(name, StringComparison.OrdinalIgnoreCase));
        var missing = new List<string>();
        if (!Has(shown.Music) || !Has(chosen.Music)) missing.Add("music");
        if (!Has(shown.Ambience + "_start") || !Has(chosen.Ambience + "_start")) missing.Add("ambience");
        return missing.Count == 0 ? null : $"The game has no {string.Join(" or ", missing)} to link for {chosen.Title}, so that stays as it was.";
    }

    /// <summary>Puts the game's own video and sounds back (only what Arkive's change still holds).</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        RefuseWhileRunning();
        await RestoreLegacyTableAsync(cancellationToken).ConfigureAwait(false);
        if (LoadState() is not { } state)
        {
            if (File.Exists(_statePath)) File.Delete(_statePath);
            return;
        }
        var video = state.Video;
        if (File.Exists(video.Path) &&
            (await HashAsync(video.Path, cancellationToken).ConfigureAwait(false)).Equals(video.AppliedSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(video.BackupPath) || !(await HashAsync(video.BackupPath, cancellationToken).ConfigureAwait(false))
                    .Equals(video.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Arkive's copy of the game's login video is missing or damaged. Verify the game files in Steam to put the original back.");
            var restoring = video.Path + ".arkive-restore";
            File.Copy(video.BackupPath, restoring, overwrite: true);
            File.Move(restoring, video.Path, overwrite: true);
        }
        LoginScreenSounds.Write(state.Sounds.Where(patch => File.Exists(patch.PackagePath)), restore: true);
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

    /// <summary>
    /// Arkive 0.4.5 and 0.4.6 changed the movie table in data1.lpk instead (the game ignores it). If that change is
    /// still installed, the game's data1.lpk is put back.
    /// </summary>
    private async Task RestoreLegacyTableAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath)) return;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(await File.ReadAllTextAsync(_statePath, cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return;
        }
        if (node?["Version"] is not null || node?["BackupPath"]?.GetValue<string>() is not { } backup ||
            node["AppliedSha256"]?.GetValue<string>() is not { } appliedSha || node["OriginalSha256"]?.GetValue<string>() is not { } originalSha)
            return;
        var archive = Path.Combine(_efGame, "data1.lpk");
        if (File.Exists(archive) && File.Exists(backup) &&
            (await HashAsync(archive, cancellationToken).ConfigureAwait(false)).Equals(appliedSha, StringComparison.OrdinalIgnoreCase) &&
            (await HashAsync(backup, cancellationToken).ConfigureAwait(false)).Equals(originalSha, StringComparison.OrdinalIgnoreCase))
        {
            var restoring = archive + ".arkive-restore";
            File.Copy(backup, restoring, overwrite: true);
            File.Move(restoring, archive, overwrite: true);
        }
        File.Delete(_statePath);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
