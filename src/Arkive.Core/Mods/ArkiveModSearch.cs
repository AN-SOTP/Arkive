using Arkive.Core.Discovery;

namespace Arkive.Core.Mods;

/// <summary>
/// The Mods list's search box. A mod matches when every word typed is in its name, author or pack name, ignoring case;
/// a word starting with - leaves out the mods that contain it, as in the other search boxes.
/// </summary>
public static class ArkiveModSearch
{
    public static bool Matches(string query, ArkiveModFileManifest manifest, ArkiveModPack? pack = null) =>
        AssetSearch.Matches(query, manifest.Name, manifest.Author, pack?.Name);
}
