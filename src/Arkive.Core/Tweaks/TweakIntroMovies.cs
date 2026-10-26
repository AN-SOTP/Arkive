using Arkive.Core.LoginScreens;
using Arkive.Core.Mods;

namespace Arkive.Core.Tweaks;

/// <summary>
/// Faster Launch's intro skip: the game plays the publisher logos and the seizure warning from one movie per language,
/// and starts straight at the login screen when it is missing. Arkive moves those files into its backups instead of
/// deleting them, and moves them back to undo.
/// </summary>
public sealed class TweakIntroMovies(string backupRoot, IModDeploymentProcessGuard? guard = null)
{
    public const string IntroMovie = "F3HGM8N1Q7X8GHG31BTFXEF";

    private string BackupDirectory => Path.Combine(backupRoot, "intro-movies");

    /// <summary>Skips (or brings back) the intro; returns how many files moved.</summary>
    public int Apply(string packagesDirectory, bool skip)
    {
        var movies = Path.Combine(LoginScreenService.EfGameFromPackages(packagesDirectory)
                                  ?? throw new DirectoryNotFoundException("The game's EFGame folder was not found."), "Movies");
        if (!Directory.Exists(movies)) throw new DirectoryNotFoundException($"The game's Movies folder was not found: {movies}");
        if (guard?.FindBlockingProcesses() is { Count: > 0 } running)
            throw new InvalidOperationException($"Close {string.Join(", ", running)} first: the game is using its movies.");
        var moved = 0;
        if (skip)
        {
            Directory.CreateDirectory(BackupDirectory);
            foreach (var file in Directory.EnumerateFiles(movies, IntroMovie + ".*.ipk"))
            {
                // A game update that brought the intro back replaces the older backup.
                File.Move(file, Path.Combine(BackupDirectory, Path.GetFileName(file)), overwrite: true);
                moved++;
            }
        }
        else if (Directory.Exists(BackupDirectory))
        {
            foreach (var backup in Directory.EnumerateFiles(BackupDirectory, IntroMovie + ".*.ipk"))
            {
                var target = Path.Combine(movies, Path.GetFileName(backup));
                if (File.Exists(target)) File.Delete(backup);
                else File.Move(backup, target);
                moved++;
            }
        }
        return moved;
    }

    /// <summary>True while Arkive holds intro movies in its backups.</summary>
    public bool Skipping => Directory.Exists(BackupDirectory) && Directory.EnumerateFiles(BackupDirectory, IntroMovie + ".*.ipk").Any();
}
