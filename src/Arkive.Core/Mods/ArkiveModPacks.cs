using System.Text.Json;

namespace Arkive.Core.Mods;

/// <summary>A named set of library mods that are switched on and off together. Each mod keeps its own priority.</summary>
public sealed record ArkiveModPack(string PackId, string Name, IReadOnlyList<string> ModIds)
{
    /// <summary>The pack's mods are hidden under its header in the Mods list.</summary>
    public bool Collapsed { get; init; }

    public bool Contains(string modId) => ModIds.Contains(modId, StringComparer.OrdinalIgnoreCase);
}

public sealed record ArkiveModPackManifest(int SchemaVersion, IReadOnlyList<ArkiveModPack> Packs)
{
    /// <summary>The overlap warning the user hid (see ArkiveModOverlap.Signature); it shows again when the overlaps change.</summary>
    public string? HiddenOverlapWarning { get; init; }

    public ArkiveModPack? Find(string packId) => Packs.FirstOrDefault(pack =>
        pack.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase));

    public ArkiveModPack? PackOf(string modId) => Packs.FirstOrDefault(pack => pack.Contains(modId));
}

/// <summary>
/// Mod packs live in their own file beside the mod library manifest, so libraries from earlier versions load unchanged
/// and an earlier Arkive opening this data folder simply shows the mods without their packs. A mod is in at most one pack.
/// </summary>
public sealed class ArkiveModPackStore
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "arkive-mod-packs.json";

    public ArkiveModPackStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        FilePath = Path.Combine(Path.GetFullPath(stateDirectory), FileName);
    }

    public string FilePath { get; }

    /// <summary>Reads the packs, leaving out mods no longer in the library and packs left with no mods.</summary>
    public ArkiveModPackManifest Load(IEnumerable<string>? libraryModIds = null)
    {
        ArkiveModPackManifest manifest;
        try
        {
            manifest = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ArkiveModPackManifest>(File.ReadAllText(FilePath), ArkiveModFileService.JsonOptions) ??
                  Empty
                : Empty;
        }
        catch (JsonException)
        {
            // A damaged packs file must not stop the Mods list from loading; the mods themselves are untouched.
            return Empty;
        }
        if (manifest.SchemaVersion > CurrentSchemaVersion) return Empty;
        var packs = (manifest.Packs ?? []).Where(pack => !string.IsNullOrWhiteSpace(pack.PackId));
        if (libraryModIds is not null)
        {
            var known = libraryModIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            packs = packs.Select(pack => pack with { ModIds = pack.ModIds.Where(known.Contains).ToArray() });
        }
        return manifest with { Packs = packs.Where(pack => pack.ModIds.Count > 0).ToArray() };
    }

    public ArkiveModPackManifest Update(Func<ArkiveModPackManifest, ArkiveModPackManifest> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var updated = mutate(Load());
        // A mod is in one pack only; if a file lists it twice, the later pack keeps it.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packs = updated.Packs.Reverse()
            .Select(pack => pack with
            {
                Name = pack.Name.Trim(),
                ModIds = pack.ModIds.Where(claimed.Add).ToArray(),
            })
            .Where(pack => pack.ModIds.Count > 0)
            .Reverse()
            .ToArray();
        updated = updated with { SchemaVersion = CurrentSchemaVersion, Packs = packs };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(updated, ArkiveModFileService.JsonOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return updated;
    }

    /// <summary>Creates a pack, or replaces the name and mods of an existing one. Mods leave any other pack they were in.</summary>
    public ArkiveModPack Save(string? packId, string name, IReadOnlyList<string> modIds)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArkiveModRefusalException("PCK001", "Give the pack a name.");
        if (modIds.Count == 0)
            throw new ArkiveModRefusalException("PCK002", "Choose at least one mod for the pack.");
        ArkiveModPack? saved = null;
        Update(current =>
        {
            var existing = packId is null ? null : current.Find(packId);
            saved = (existing ?? new ArkiveModPack(Guid.NewGuid().ToString("N"), name, [])) with
            {
                Name = name.Trim(),
                ModIds = modIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            };
            var others = current.Packs
                .Where(pack => !pack.PackId.Equals(saved.PackId, StringComparison.OrdinalIgnoreCase))
                .Select(pack => pack with { ModIds = pack.ModIds.Where(id => !saved.Contains(id)).ToArray() });
            var packs = others.ToList();
            var index = existing is null ? -1 : current.Packs.ToList().FindIndex(pack => pack.PackId == existing.PackId);
            packs.Insert(index < 0 ? packs.Count : Math.Min(index, packs.Count), saved);
            return current with { Packs = packs };
        });
        return saved!;
    }

    public void Remove(string packId) => Update(current => current with
    {
        Packs = current.Packs.Where(pack => !pack.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase)).ToArray(),
    });

    public void RemoveMod(string modId) => Update(current => current with
    {
        Packs = current.Packs.Select(pack => pack with
        {
            ModIds = pack.ModIds.Where(id => !id.Equals(modId, StringComparison.OrdinalIgnoreCase)).ToArray(),
        }).ToArray(),
    });

    public void SetCollapsed(string packId, bool collapsed) => Update(current => current with
    {
        Packs = current.Packs.Select(pack => pack.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase)
            ? pack with { Collapsed = collapsed }
            : pack).ToArray(),
    });

    public void HideOverlapWarning(string? signature) =>
        Update(current => current with { HiddenOverlapWarning = signature });

    /// <summary>
    /// The priority change that makes every mod of a pack beat every enabled mod outside it, keeping the pack's own
    /// order. Zero when the pack already wins.
    /// </summary>
    public static int PriorityRaiseToWin(ArkiveModPack pack, IReadOnlyList<ArkiveModLibraryEntry> mods)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(mods);
        var resolution = ArkiveModConflictResolver.Resolve(mods);
        var raise = 0;
        foreach (var target in resolution.Targets)
        {
            var members = target.Contenders.Where(item => pack.Contains(item.ModId)).ToArray();
            var outside = target.EnabledContenders.Where(item => !pack.Contains(item.ModId)).ToArray();
            if (members.Length == 0 || outside.Length == 0) continue;
            var strongest = outside.Max(item => item.Priority);
            raise = Math.Max(raise, strongest + 1 - members.Min(item => item.Priority));
        }
        return raise;
    }

    private static ArkiveModPackManifest Empty => new(CurrentSchemaVersion, []);
}
