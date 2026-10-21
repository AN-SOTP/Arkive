using System.Text.RegularExpressions;

namespace Arkive.Core.Discovery;

/// <summary>
/// Names the installed Lost Ark build, so Arkive can notice a game update since it last ran: Steam's build id for the
/// game, or the game executable's size and time when Steam's record of the game is not found.
/// </summary>
public static class GameBuildStamp
{
    public const string SteamAppId = "1599340";

    /// <summary>The stamp of the game whose EFGame\ReleasePC\Packages folder is <paramref name="packagesDirectory"/>, or null.</summary>
    public static string? Read(string? packagesDirectory)
    {
        if (string.IsNullOrWhiteSpace(packagesDirectory)) return null;
        try
        {
            // Packages is <game>\EFGame\ReleasePC\Packages, and Steam keeps its record of the game in <library>\steamapps.
            var game = Directory.GetParent(Path.GetFullPath(packagesDirectory))?.Parent?.Parent;
            if (game is null || !game.Exists) return null;
            if (game.Parent?.Parent is { } steamApps &&
                Path.Combine(steamApps.FullName, $"appmanifest_{SteamAppId}.acf") is var record && File.Exists(record) &&
                Regex.Match(File.ReadAllText(record), "\"buildid\"\\s+\"(\\d+)\"") is { Success: true } build)
                return "steam:" + build.Groups[1].Value;
            var executable = new FileInfo(Path.Combine(game.FullName, "Binaries", "Win64", "LOSTARK.exe"));
            return executable.Exists ? $"exe:{executable.Length}:{executable.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
