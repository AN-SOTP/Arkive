using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.GameData;

namespace Arkive.Core.Mods;

public sealed record RememberedEdit(
    UnifiedPackageAssetKind Kind,
    string EditId,
    string ManifestPath,
    string DisplayName,
    string SourcePackagePath,
    string ObjectName,
    bool Included,
    bool ChangesNothing);

public sealed record RememberedAsset(
    string AssetId,
    ArkiveModSetupState Setup,
    bool ModifiedLocally,
    bool? InGame,
    RememberedEdit? Edit);

/// <param name="FileSha256">The mod file the status was worked out for.</param>
/// <param name="AuthoringPending">Its Edit mods draft held changes not saved into the mod.</param>
public sealed record RememberedMod(
    string ModId,
    string FileSha256,
    bool FileMissing,
    bool OutsideChange,
    bool AuthoringPending,
    ArkiveModGameDataStatus? GameData,
    ArkiveModGameDataStatus? Looks,
    IReadOnlyList<RememberedAsset> Assets);

/// <param name="LibrarySha256">The mod library file the list was worked out from.</param>
/// <param name="OutsideChangeCount">Game files found changed outside this Arkive.</param>
public sealed record RememberedModList(
    int FormatVersion,
    string ArkiveVersion,
    string DataRoot,
    int IndexFormatVersion,
    string LibrarySha256,
    int OutsideChangeCount,
    IReadOnlyList<RememberedMod> Mods);

/// <summary>A remembered Mods list put back together for the library it was made from.</summary>
public sealed record RecalledModList(
    IReadOnlyList<ArkiveModStatus> Statuses,
    IReadOnlySet<string> AuthoringPending,
    int OutsideChangeCount);

/// <summary>
/// The Mods list as Arkive last verified it, kept in the data folder's cache so the next launch can show it at once,
/// before the working files are checked again. It is only shown: every action that writes or needs verified state waits
/// for the check, which replaces it. It is ignored when the mod library file, the working-file index format or the
/// Arkive version has changed since.
/// </summary>
public static class ArkiveModListMemory
{
    public const int FormatVersion = 1;
    public const string FileName = "mods-list-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, "cache", FileName);

    /// <summary>What to remember of a verified Mods list.</summary>
    public static RememberedModList Remember(
        IReadOnlyList<ArkiveModStatus> statuses,
        IReadOnlySet<string> authoringPending,
        int outsideChangeCount,
        string librarySha256,
        string arkiveVersion,
        string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(authoringPending);
        return new RememberedModList(FormatVersion, arkiveVersion, Path.GetFullPath(dataRoot), WorkingFileIndex.FormatVersion,
            librarySha256, outsideChangeCount,
            statuses.Select(status => new RememberedMod(
                status.Mod.ModId, status.Mod.FileSha256, status.FileMissing, status.OutsideChange,
                authoringPending.Contains(status.Mod.ModId), status.GameData, status.Looks,
                status.Assets.Select(asset => new RememberedAsset(
                    asset.Asset.AssetId, asset.Setup, asset.ModifiedLocally, asset.InGame,
                    asset.Edit is not { } edit ? null : new RememberedEdit(
                        edit.Kind, edit.EditId, edit.ManifestPath, edit.DisplayName, edit.SourcePackagePath,
                        edit.ObjectName, edit.Included, ArkiveModApplyService.ChangesNothing(edit))))
                    .ToArray()))
                .ToArray());
    }

    public static void Save(string dataRoot, RememberedModList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var path = PathFor(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(list, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>The remembered list, or null when there is none or it was made by another version or for another folder.</summary>
    public static RememberedModList? Load(string dataRoot, string arkiveVersion)
    {
        try
        {
            var path = PathFor(dataRoot);
            if (!File.Exists(path)) return null;
            var list = JsonSerializer.Deserialize<RememberedModList>(File.ReadAllBytes(path), JsonOptions);
            return list is { FormatVersion: FormatVersion, IndexFormatVersion: WorkingFileIndex.FormatVersion, Mods: not null,
                       LibrarySha256: not null } &&
                   list.ArkiveVersion == arkiveVersion &&
                   Path.GetFullPath(list.DataRoot).TrimEnd(Path.DirectorySeparatorChar).Equals(
                       Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                ? list
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                              NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The statuses the remembered list held, for <paramref name="library"/>: null unless the list was made from this
    /// very library file (<paramref name="librarySha256"/>) and holds every mod and asset it has. Conflict outcomes are
    /// worked out again from the library, as the Mods list does.
    /// </summary>
    public static RecalledModList? Recall(RememberedModList? list, ArkiveModLibraryManifest library, string? librarySha256)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (list is null || librarySha256 is null ||
            !list.LibrarySha256.Equals(librarySha256, StringComparison.OrdinalIgnoreCase) ||
            list.Mods.Count != library.Mods.Count)
            return null;
        var byId = list.Mods.GroupBy(mod => mod.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var resolution = ArkiveModConflictResolver.Resolve(library.Mods);
        var statuses = new List<ArkiveModStatus>(library.Mods.Count);
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in library.Mods)
        {
            if (!byId.TryGetValue(mod.ModId, out var remembered) ||
                !remembered.FileSha256.Equals(mod.FileSha256, StringComparison.OrdinalIgnoreCase))
                return null;
            var rememberedAssets = remembered.Assets.GroupBy(asset => asset.AssetId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var assets = new List<ArkiveModAssetStatus>(mod.Manifest.Assets.Count);
            foreach (var asset in mod.Manifest.Assets)
            {
                if (!rememberedAssets.TryGetValue(asset.AssetId, out var state)) return null;
                var edit = state.Edit is not { } known ? null : new ArkiveModLocalEdit(
                    known.Kind, known.EditId, known.ManifestPath, known.DisplayName, known.SourcePackagePath,
                    known.ObjectName, known.Included, new ArkiveModIndexedEdit(null, null, known.ChangesNothing));
                assets.Add(new ArkiveModAssetStatus(
                    asset, resolution.OutcomeFor(mod.ModId, asset.AssetId), resolution.StrongestCompetitor(mod.ModId, asset.AssetId),
                    mod.BindingFor(asset.AssetId), edit, state.Setup, state.ModifiedLocally, state.InGame));
            }
            if (remembered.AuthoringPending) pending.Add(mod.ModId);
            statuses.Add(new ArkiveModStatus(mod, remembered.FileMissing, assets)
            {
                OutsideChange = remembered.OutsideChange,
                GameData = remembered.GameData,
                Looks = remembered.Looks,
            });
        }
        return new RecalledModList(statuses, pending, list.OutsideChangeCount);
    }
}
