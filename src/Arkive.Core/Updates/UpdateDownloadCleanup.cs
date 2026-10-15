using System.Text.RegularExpressions;

namespace Arkive.Core.Updates;

public sealed record UpdateDownloadCleanupResult(int DeletedFolders, int KeptFolders, long FreedBytes);

/// <summary>
/// Each in-app update downloads its installer and a copy of the update helper into its own
/// folder under the data root's "updates" directory. Once the installer has run, the folder
/// is dead weight (about 170 MB per update), so startup deletes it.
/// </summary>
public static partial class UpdateDownloadCleanup
{
    /// <summary>
    /// Deletes every download folder except one whose installer is newer than the running
    /// version: that update didn't install, and its failure message points the user at it.
    /// Folders still in use (the helper may be finishing) are skipped and retried next start.
    /// </summary>
    public static UpdateDownloadCleanupResult CleanUp(string updatesDirectory, Version currentVersion)
    {
        if (!Directory.Exists(updatesDirectory))
        {
            return new UpdateDownloadCleanupResult(0, 0, 0);
        }

        var deleted = 0;
        var kept = 0;
        long freed = 0;
        foreach (var folder in new DirectoryInfo(updatesDirectory).EnumerateDirectories())
        {
            if (IsPendingInstaller(folder, currentVersion))
            {
                kept++;
                continue;
            }

            try
            {
                var bytes = folder.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
                folder.Delete(recursive: true);
                deleted++;
                freed += bytes;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                kept++;
            }
        }

        return new UpdateDownloadCleanupResult(deleted, kept, freed);
    }

    private static bool IsPendingInstaller(DirectoryInfo folder, Version currentVersion) =>
        folder.EnumerateFiles("Arkive-Setup-*.exe").Any(file =>
            InstallerVersionPattern().Match(file.Name) is { Success: true } match &&
            Version.TryParse(match.Groups[1].Value, out var version) &&
            version > new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build)));

    [GeneratedRegex("^Arkive-Setup-([0-9]+\\.[0-9]+\\.[0-9]+)-win-x64\\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerVersionPattern();
}
