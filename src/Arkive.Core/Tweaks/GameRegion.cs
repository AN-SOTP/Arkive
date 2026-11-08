using System.Xml;
using System.Xml.Linq;

namespace Arkive.Core.Tweaks;

/// <summary>
/// The region the game client plays on, as the game saved it when the player chose a region:
/// EFGame\Config\UserOption.xml, &lt;SaveAccountOptionData&gt;&lt;RegionID&gt; ("CE" for EU Central).
/// </summary>
public static class GameRegion
{
    /// <summary>The game's saved region id, or null when the file or the value isn't there.</summary>
    public static string? Read(string? efGameDirectory)
    {
        if (string.IsNullOrWhiteSpace(efGameDirectory))
            return null;
        var path = Path.Combine(efGameDirectory, "Config", "UserOption.xml");
        try
        {
            if (!File.Exists(path))
                return null;
            var value = XDocument.Load(path).Descendants("RegionID").FirstOrDefault()?.Value.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// lostark.bible's region for a game region id: "CE" for EU Central, "NA" for any North America id, and other ids
    /// as they are (the site's own codes); null for none.
    /// </summary>
    public static string? LogsRegion(string? gameRegion)
    {
        var region = gameRegion?.Trim().ToUpperInvariant();
        return region switch
        {
            null or "" => null,
            "CE" or "EUC" or "EU" => "CE",
            _ when region.StartsWith("NA", StringComparison.Ordinal) => "NA",
            _ => region,
        };
    }
}
