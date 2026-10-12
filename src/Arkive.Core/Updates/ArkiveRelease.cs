using System.Text.RegularExpressions;

namespace Arkive.Core.Updates;

public sealed record ArkiveRelease(Version Version, long AssetId, string FileName, long Size, string Sha256);

public static partial class ArkiveReleasePolicy
{
    public const string Repository = "AN-SOTP/Arkive";
    public const long MaximumInstallerSize = 512L * 1024 * 1024;

    public static Version CurrentVersion => typeof(ArkiveReleasePolicy).Assembly.GetName().Version ?? new Version(0, 1, 0);

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        return text is not null && VersionPattern().IsMatch(text) &&
            Version.TryParse(text.TrimStart('v'), out version!);
    }

    public static string InstallerName(Version version) => $"Arkive-Setup-{version.ToString(3)}-win-x64.exe";

    public static bool IsAllowedDownload(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    [GeneratedRegex("^v?[0-9]+\\.[0-9]+\\.[0-9]+$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$")]
    public static partial Regex Sha256Pattern();
}
