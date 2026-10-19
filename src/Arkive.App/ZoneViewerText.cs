using Arkive.Core.Assets;
using Arkive.Core.Discovery;

namespace Arkive.App;

/// <summary>
/// Every sentence the Zone Viewer and the Asset Library's map cards show, in one place so they can be translated.
/// Each is a whole sentence or label; none is built from fragments of another.
/// </summary>
internal static class ZoneViewerText
{
    public static string WindowTitle(string map) => $"Arkive Zone Viewer · {map}";

    public static string MapDetail(ZoneMap map) => map.Zones.Count == 1
        ? $"Zone {map.Zones[0].Id} · {Of(map.LevelPackages.Count, "level package", "level packages")}"
        : $"Zones {string.Join(", ", map.Zones.Select(zone => zone.Id))} · {Of(map.LevelPackages.Count, "level package", "level packages")}";

    public static string MapDetail(ZoneMap map, ZoneMapReport report)
    {
        var text = $"{MapDetail(map)} · {report.Parts.Count:N0} parts";
        var others = map.Zones.Select(zone => zone.Name).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).ToArray();
        if (others.Length > 0) text += $" · Also shown in game as {string.Join(", ", others)}";
        if (report.UnreadLevelPackages > 0) text += $" · {report.UnreadLevelPackages:N0} level packages could not be read";
        return text;
    }

    public const string ReadingMap = "Reading the map's level packages…";
    public const string CheckingSharing = "Checking which other maps share these parts…";
    public const string SharingUnknown = "Which other maps share these parts could not be read; see the session log.";
    public const string SharingNotKnown = "Not known";

    public static string ReadingOtherMaps(int completed, int total) =>
        $"Reading every map once to see which parts they share ({completed:N0} of {total:N0} level packages). Arkive keeps the result, so this only happens again after a game update.";

    public static string MapFailed(string reason) => $"The map could not be read: {reason}";

    public static string Sharing(IReadOnlyList<ZonePartRow> rows)
    {
        var listed = rows.Where(row => row.Group != ZonePartGroup.GeneratedMaps).ToArray();
        var only = listed.Count(row => row.OtherMaps == 0);
        var shared = listed.Length - only;
        return shared == 0
            ? $"All {Of(listed.Length, "part", "parts")} are only this map's, so changing them changes no other map."
            : $"{only:N0} of {Of(listed.Length, "part", "parts")} are only this map's. {Of(shared, "part is", "parts are")} shared: changing one changes every map that uses it.";
    }

    public static string Settings(ZoneLevelSettings settings) => settings.Total == 0
        ? "Fog volumes, colour grading and sky materials are listed below."
        : $"Its lights ({Of(settings.Suns, "sun", "suns")}, {Of(settings.Lights, "other light", "other lights")}), {Of(settings.Fogs, "fog", "fogs")} and {Of(settings.EnvironmentVolumes, "environment volume", "environment volumes")} are level settings, which mods can't change yet. Fog volume materials, colour grading and sky materials are listed below.";

    public const string AllParts = "All Parts";
    public const string AllPartsDescription = "Every part of the map except lightmaps and masks.";

    public static string GroupTitle(ZonePartGroup group) => group switch
    {
        ZonePartGroup.Terrain => "Terrain",
        ZonePartGroup.Sky => "Sky",
        ZonePartGroup.Fog => "Fog",
        ZonePartGroup.Lighting => "Lighting and Colour Grading",
        ZonePartGroup.Effects => "Effects",
        ZonePartGroup.Characters => "Characters",
        ZonePartGroup.Meshes => "Meshes",
        ZonePartGroup.Materials => "Materials",
        ZonePartGroup.Textures => "Textures",
        ZonePartGroup.GeneratedMaps => "Lightmaps and Masks",
        _ => group.ToString(),
    };

    public static string GroupDescription(ZonePartGroup group) => group switch
    {
        ZonePartGroup.Terrain => "The ground: the landscape's layer materials (their layer colours tint the ground) and the textures they draw with.",
        ZonePartGroup.Sky => "Sky and cloud textures, materials and meshes.",
        ZonePartGroup.Fog => "Fog volume materials (their emissive colour tints the fog) and fog meshes.",
        ZonePartGroup.Lighting => "Colour grading look-up tables, which tint the whole screen, and light function materials.",
        ZonePartGroup.Effects => "Effects the map places (mist, embers, glows) and the materials of their packages.",
        ZonePartGroup.Characters => "Monsters and characters the map's scenes place, with their materials.",
        ZonePartGroup.Meshes => "Rocks, buildings and props the map places.",
        ZonePartGroup.Materials => "Other materials the map's meshes draw with.",
        ZonePartGroup.Textures => "Other textures the map's materials draw with.",
        ZonePartGroup.GeneratedMaps => "Baked lighting, shadow maps and terrain blend masks the game's editor generated. Changing them shifts the lighting or the ground's layers, so they are left out of All Parts.",
        _ => string.Empty,
    };

    public static string GroupSummary(IReadOnlyList<ZonePartRow> rows)
    {
        var parts = rows.Count == 1 ? "1 part" : $"{rows.Count:N0} parts";
        if (rows.Any(row => row.OtherMaps is null)) return parts;
        var only = rows.Count(row => row.OtherMaps == 0);
        return $"{parts} · {only:N0} only this map's";
    }

    public static string Count(int shown, int selected) => selected == 0
        ? $"{shown:N0} parts shown"
        : $"{shown:N0} parts shown · {selected:N0} selected";

    public const string NoMod = "Parts are added to the mod open in Edit Mods. Choose or create one there first.";

    public static string AddsTo(string mod) => $"Parts are added to {mod}, the mod open in Edit Mods.";

    public static string AddLabel(int count) => count switch
    {
        0 => "Add selected to mod",
        1 => "Add 1 part to mod",
        _ => $"Add {count:N0} parts to mod",
    };

    public static string TintLabel(int count) => count switch
    {
        0 => "Recolour and add to mod",
        1 => "Recolour 1 part and add to mod",
        _ => $"Recolour {count:N0} parts and add to mod",
    };

    public const string TintNeedsColour = "Raise Strength or change Brightness first; the preview shows the result.";
    public const string TintTip = "Adds the selected textures and materials to the mod as working copies, then recolours them. Nothing in the game changes until you save and apply the mod.";

    public const string ChoosePart = "Choose a part to preview it.";
    public const string LoadingPicture = "Reading the texture…";
    public const string ReadingMaterial = "Reading the material…";
    public const string MaterialColours = "A material: its colours are below.";
    public const string MaterialWithoutColours = "This material has no colour parameters; its look comes from its textures.";

    public static string OpenToSee(AssetKind kind) => kind switch
    {
        AssetKind.ParticleSystem => "An effect. Open it in the effect editor to watch it and change its colours.",
        AssetKind.SkeletalMesh or AssetKind.StaticMesh => "A mesh. Open it in the Asset Viewer to see it with the game's shader.",
        _ => "Open it to see it.",
    };

    public static string PreviewFailed(string reason) => $"This part could not be previewed: {reason}";

    public static string PartDetail(ZonePartRow row) => $"{row.Type} · {row.Package} · used in {row.Levels}";

    public static string TextureNotInMap(string parameter, string objectName) => $"{parameter}: {objectName} (from its parent material)";

    public const string GreyPick = "That spot is grey, so it has no hue to pick. Use Light and white areas for greys and whites.";

    public static string Picked(double hue) => $"Picked hue {hue:0}° from the preview.";

    public static string ConfirmAdd(int count, int shared, string mod, long bytes)
    {
        var text = $"Add {count:N0} parts to {mod}? Each becomes a working copy on disk (roughly {Size(bytes * 3)} in all).";
        if (shared > 0)
            text += $"\n\n{shared:N0} of them are shared with other maps, so once the mod is applied those maps change too. Use \"Only this map's parts\" to leave them out.";
        return text;
    }

    public const string Adding = "Adding the parts to the mod…";
    public const string Exporting = "Exporting the parts…";
    public const string Stopped = "Stopped. Parts already added or exported were kept.";

    public static string ExportTitle(int count) => count == 1 ? "Choose a folder to export 1 part into" : $"Choose a folder to export {count:N0} parts into";

    public static string Step(ZoneViewerProgress progress) => progress.Total > 0
        ? $"{progress.Stage} ({progress.Completed:N0} of {progress.Total:N0})"
        : progress.Stage;

    public static string TypeOf(PackageIndexMatch asset) => asset.ClassName.ToLowerInvariant() switch
    {
        "lightmaptexture2d" => "Lightmap",
        "shadowmaptexture2d" => "Shadow map",
        "landscapematerialinstanceconstant" => "Landscape material",
        _ => AssetTypes.For(AssetTypes.IndexedKind(asset.Kind, asset.ClassName))?.Title ?? asset.ClassName,
    };

    public static string ShortLevel(ZoneMap map, string level)
    {
        foreach (var persistent in map.Zones.SelectMany(zone => zone.Levels))
        {
            var stem = persistent.EndsWith("_ps", StringComparison.OrdinalIgnoreCase) ? persistent[..^2] : persistent + "_";
            if (level.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && level.Length > stem.Length) return level[stem.Length..];
        }
        return level;
    }

    public static string UsedBy(int? otherMaps) => otherMaps switch
    {
        null => "Checking…",
        0 => "Only this map",
        1 => "Shared with 1 other map",
        var count => $"Shared with {count:N0} other maps",
    };

    public const string OnlyThisMapDetail = "Only this map uses it, so changing it changes nothing else.";

    public static string SharedDetail(IReadOnlyList<ZoneMap> others) =>
        $"Also used by {SharedNames(others, 6)}. Changing it changes those maps too.";

    public static string SharedNames(IReadOnlyList<ZoneMap> others, int limit)
    {
        var names = others.Select(map => map.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var shown = string.Join(", ", names.Take(limit));
        return names.Length > limit ? $"{shown} and {names.Length - limit:N0} more" : shown;
    }

    // ---- 3D map ----

    public const string BuildingMap = "Reading the whole map to draw it in 3D. The first time takes a few seconds; Arkive keeps the result.";
    public const string NoWebView = "The 3D map needs the Microsoft Edge WebView2 Runtime, which is part of Windows 10 and 11 but appears to be missing here.";
    public const string PickedNothing = "That piece is not in this map's parts list.";

    public static string MapStep(ZoneSceneProgress progress) => progress.Total > 0
        ? $"{progress.Stage} ({progress.Completed:N0} of {progress.Total:N0})…"
        : $"{progress.Stage}…";

    public static string MapFailed3D(string reason) => $"The 3D map could not be drawn: {reason}";

    public static string MapLoaded(int placements, int meshes, int terrain) => terrain > 0
        ? $"The 3D map shows {Of(placements, "placed mesh", "placed meshes")} ({Of(meshes, "mesh", "different meshes")}) and {Of(terrain, "terrain tile", "terrain tiles")}. Click a piece to select it in the list; the recolour previews on it."
        : $"The 3D map shows {Of(placements, "placed mesh", "placed meshes")} ({Of(meshes, "mesh", "different meshes")}). Click a piece to select it in the list; the recolour previews on it.";

    // ---- Asset Library map cards ----

    public static string CardTitle(ZoneMap map, IReadOnlyList<GameZone> matched) =>
        matched.FirstOrDefault(zone => map.Zones.Contains(zone))?.Name ?? map.Name;

    public static string CardDetail(ZoneMap map) => map.Zones.Count == 1
        ? $"Zone {map.Zones[0].Id} · {Of(map.LevelPackages.Count, "level package", "level packages")}"
        : $"{map.Zones.Count:N0} zones share this map · {Of(map.LevelPackages.Count, "level package", "level packages")}";

    public const string CardAction = "Open in Zone Viewer";

    public static string CardsHeader(int count) => count == 1
        ? "Map matching your search · open it to see every part and recolour it"
        : $"{count:N0} maps matching your search · open one to see every part and recolour it";

    public static string MapNames(IReadOnlyList<ZoneMap> maps, int limit)
    {
        var names = maps.Select(map => map.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var shown = string.Join(", ", names.Take(limit).Select(name => $"{name} (map)"));
        return names.Length > limit ? $"{shown} and {names.Length - limit:N0} more maps" : shown;
    }

    /// <summary>"1 part", "3 parts".</summary>
    private static string Of(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count:N0} {many}";

    private static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024):N0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024 * 1024):N0} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):N1} GB",
    };
}
