using Arkive.Core.GameData;
using Arkive.Core.Mods;

namespace Arkive.Core.Outfits;

/// <summary>
/// An item look changed by one or more enabled mods. A look goes with the mesh its mod puts in the game, so the whole
/// look is one mod's: the highest priority one. Mods at the same priority changing it differently tie.
/// </summary>
public sealed record OutfitLookContest(
    string Entry,
    (ArkiveModLibraryEntry Mod, IReadOnlyList<OutfitLookEdit> Edits) Winner,
    IReadOnlyList<(ArkiveModLibraryEntry Mod, IReadOnlyList<OutfitLookEdit> Edits)> Others,
    bool Tied);

/// <summary>Which enabled mod's slots each item look gets.</summary>
public sealed class OutfitLookResolution
{
    private OutfitLookResolution(IReadOnlyList<OutfitLookContest> looks) => Looks = looks;

    public IReadOnlyList<OutfitLookContest> Looks { get; }

    public IEnumerable<OutfitLookContest> Ties => Looks.Where(look => look.Tied);

    public IReadOnlyList<OutfitLookEdit> Edits => Looks.SelectMany(look => look.Winner.Edits).ToArray();

    public static OutfitLookResolution Resolve(IEnumerable<ArkiveModLibraryEntry> mods)
    {
        var looks = mods
            .Where(mod => mod.Enabled && mod.Manifest.Looks is not null)
            .SelectMany(mod => mod.Manifest.Looks!.Edits.GroupBy(edit => edit.Entry, StringComparer.OrdinalIgnoreCase)
                .Select(group => (Mod: mod, Entry: group.Key, Edits: (IReadOnlyList<OutfitLookEdit>)group.OrderBy(edit => edit.Slot).ToArray())))
            .GroupBy(item => item.Entry, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderByDescending(item => item.Mod.Priority)
                    .ThenBy(item => item.Mod.Manifest.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                var top = ordered.TakeWhile(item => item.Mod.Priority == ordered[0].Mod.Priority).ToArray();
                return new OutfitLookContest(group.Key, (ordered[0].Mod, ordered[0].Edits),
                    ordered[1..].Select(item => (item.Mod, item.Edits)).ToArray(), top.Any(item => !Same(item.Edits, ordered[0].Edits)));
            })
            .OrderBy(look => look.Entry, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new OutfitLookResolution(looks);
    }

    /// <summary>The looks <paramref name="modId"/> changes: how many it wins, loses to a higher priority mod, or ties.</summary>
    public ArkiveModGameDataStatus StatusFor(string modId, IReadOnlyList<OutfitLookEdit> installed)
    {
        int winning = 0, overridden = 0, tied = 0, inGame = 0;
        foreach (var look in Looks)
        {
            var mine = look.Winner.Mod.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase);
            if (!mine && !look.Others.Any(item => item.Mod.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))) continue;
            if (look.Tied) tied++;
            else if (!mine) overridden++;
            else
            {
                winning++;
                if (Same(installed.Where(edit => edit.Entry.Equals(look.Entry, StringComparison.OrdinalIgnoreCase)).ToArray(), look.Winner.Edits))
                    inGame++;
            }
        }
        return new ArkiveModGameDataStatus(winning, overridden, tied, winning > 0 && inGame == winning);
    }

    public static bool Same(IReadOnlyList<OutfitLookEdit> left, IReadOnlyList<OutfitLookEdit> right) =>
        left.Count == right.Count &&
        left.OrderBy(edit => edit.Key, StringComparer.Ordinal).Zip(right.OrderBy(edit => edit.Key, StringComparer.Ordinal)).All(pair =>
            pair.First.Key == pair.Second.Key &&
            pair.First.Material.Equals(pair.Second.Material, StringComparison.OrdinalIgnoreCase) &&
            pair.First.DyeMaterial.Equals(pair.Second.DyeMaterial, StringComparison.OrdinalIgnoreCase));
}

/// <summary>How a mod's change to one item look compares with the game's look now.</summary>
public sealed record OutfitLookCheck(ArkiveModLibraryEntry Mod, string Entry, IReadOnlyList<OutfitLookEdit> Edits, OutfitLook? GameLook)
{
    public bool Exact => GameLook is { } look && Edits.All(edit => edit.Added
        ? edit.Slot >= look.Materials.Count && Edits.Where(item => item.Added).Min(item => item.Slot) == look.Materials.Count
        : edit.Slot < look.Materials.Count &&
          look.Materials[edit.Slot].Equals(edit.OriginalMaterial, StringComparison.OrdinalIgnoreCase) &&
          look.DyeMaterials[edit.Slot].Equals(edit.OriginalDyeMaterial ?? "", StringComparison.OrdinalIgnoreCase));

    public string Name => Path.GetFileNameWithoutExtension(Entry.Split('/')[^1]);

    public string Message => GameLook is null
        ? $"This game version no longer has the item look {Name}."
        : Exact ? $"The item look {Name} is unchanged." : $"The game changed the item look {Name} since the mod was made.";
}

/// <summary>What applying the enabled mods does to data4.lpk.</summary>
public sealed record OutfitLookPlan(
    IReadOnlyList<OutfitLookEdit> Target,
    bool Changes,
    OutfitLookResolution Resolution,
    IReadOnlyList<string> Problems,
    IReadOnlySet<string> ModsNeedingUpdate)
{
    public bool Restores => Changes && Target.Count == 0;
}

/// <summary>What updating a mod's item looks for a new game version did.</summary>
public sealed record OutfitLookRebaseResult(ArkiveModLooks? Looks, int Updated, int Removed, IReadOnlyList<string> Messages);

/// <summary>
/// Item look changes that go with outfit mods (see <see cref="OutfitLooks"/>): they go through the same plan, apply,
/// restore and game-update steps as package mods. Each slot records the game's materials when it was made, so a game
/// update that changes the look marks the mod as needing an update for the new game version.
/// </summary>
public sealed class OutfitLookModService
{
    public OutfitLookModService(OutfitLookArchive archive) => Archive = archive;

    public OutfitLookArchive Archive { get; }

    public OutfitLookPlan Plan(IReadOnlyList<ArkiveModLibraryEntry> mods, CancellationToken cancellationToken = default)
    {
        var resolution = OutfitLookResolution.Resolve(mods);
        var problems = new List<string>();
        var needUpdate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tie in resolution.Ties)
            problems.Add($"The item look {Path.GetFileNameWithoutExtension(tie.Entry.Split('/')[^1])} is changed by " +
                         $"{string.Join(" and ", new[] { tie.Winner }.Concat(tie.Others).Where(item => item.Mod.Priority == tie.Winner.Mod.Priority).Select(item => item.Mod.Manifest.Name).Distinct())} " +
                         $"at the same priority {tie.Winner.Mod.Priority}. Give one of them a higher priority.");
        foreach (var check in Check(resolution.Looks.Select(look => (look.Winner.Mod, look.Entry, look.Winner.Edits)), cancellationToken)
                     .Where(check => !check.Exact))
        {
            needUpdate.Add(check.Mod.ModId);
            problems.Add($"{check.Mod.Manifest.Name}: {check.Message} Select the mod and choose 'Update for this game version'.");
        }
        var target = resolution.Edits;
        return new OutfitLookPlan(target, !OutfitLookResolution.Same(Archive.Current(), target), resolution, problems.Distinct().ToArray(), needUpdate);
    }

    /// <summary>Compares each mod's look changes with the game's own looks (Arkive's copy where it has changed them).</summary>
    public IReadOnlyList<OutfitLookCheck> Check(IEnumerable<(ArkiveModLibraryEntry Mod, string Entry, IReadOnlyList<OutfitLookEdit> Edits)> looks,
        CancellationToken cancellationToken = default)
    {
        var wanted = looks.ToArray();
        if (wanted.Length == 0) return [];
        var game = Archive.GameLooks(wanted.Select(item => item.Entry), cancellationToken);
        return wanted.Select(item => new OutfitLookCheck(item.Mod, item.Entry, item.Edits, game.GetValueOrDefault(item.Entry))).ToArray();
    }

    /// <summary>Writes the plan's look changes into data4.lpk from the game's own file.</summary>
    public Task ApplyAsync(OutfitLookPlan plan, CancellationToken cancellationToken = default) =>
        Archive.WriteAsync(plan.Target, cancellationToken);

    /// <summary>Takes every mod's look changes out of data4.lpk.</summary>
    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (Archive.Current().Count == 0) return false;
        await Archive.WriteAsync([], cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Updates a mod's look changes for this game version: each slot keeps the mod's materials and takes the game's new
    /// ones as its originals, and added slots follow on from the look's own again. Slots and looks the game no longer
    /// has are dropped.
    /// </summary>
    public OutfitLookRebaseResult Rebase(ArkiveModLibraryEntry mod, CancellationToken cancellationToken = default)
    {
        if (mod.Manifest.Looks is not { } looks) return new OutfitLookRebaseResult(null, 0, 0, []);
        var edits = new List<OutfitLookEdit>();
        var messages = new List<string>();
        int updated = 0, removed = 0;
        foreach (var check in Check(looks.Edits.GroupBy(edit => edit.Entry, StringComparer.OrdinalIgnoreCase)
                     .Select(group => (mod, group.Key, (IReadOnlyList<OutfitLookEdit>)group.OrderBy(edit => edit.Slot).ToArray())), cancellationToken))
        {
            if (check.Exact)
            {
                edits.AddRange(check.Edits);
                continue;
            }
            if (check.GameLook is not { } game)
            {
                messages.Add(check.Message + " Its changes were removed from the mod.");
                removed++;
                continue;
            }
            var count = game.Materials.Count;
            var kept = check.Edits.Where(edit => !edit.Added && edit.Slot < count)
                .Select(edit => edit with { OriginalMaterial = game.Materials[edit.Slot], OriginalDyeMaterial = game.DyeMaterials[edit.Slot] })
                .Concat(check.Edits.Where(edit => edit.Added).Select((edit, index) => edit with { Slot = count + index }))
                .ToArray();
            edits.AddRange(kept);
            messages.Add(check.Message + $" The mod keeps its materials on {kept.Length:N0} of its {check.Edits.Count:N0} slots.");
            updated++;
        }
        return new OutfitLookRebaseResult(edits.Count == 0 ? null : new ArkiveModLooks(edits), updated, removed, messages);
    }
}
