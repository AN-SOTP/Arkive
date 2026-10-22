using System.Text.Json;
using Arkive.Core.Mods;

namespace Arkive.Core.GameData;

/// <summary>A table cell changed by one or more enabled mods: who wins (highest priority), and any tie.</summary>
public sealed record GameDataCellContest(
    string CellKey,
    (ArkiveModLibraryEntry Mod, GameDataCellEdit Edit) Winner,
    IReadOnlyList<(ArkiveModLibraryEntry Mod, GameDataCellEdit Edit)> Others,
    bool Tied);

/// <summary>Which enabled mod's value each table cell gets. Mods at the same priority writing different values tie.</summary>
public sealed class GameDataResolution
{
    private GameDataResolution(IReadOnlyList<GameDataCellContest> cells) => Cells = cells;

    public IReadOnlyList<GameDataCellContest> Cells { get; }

    public IEnumerable<GameDataCellContest> Ties => Cells.Where(cell => cell.Tied);

    public static GameDataResolution Resolve(IEnumerable<ArkiveModLibraryEntry> mods)
    {
        var cells = mods
            .Where(mod => mod.Enabled && mod.Manifest.GameData is not null)
            .SelectMany(mod => mod.Manifest.GameData!.Edits.Select(edit => (Mod: mod, Edit: edit)))
            .GroupBy(item => item.Edit.CellKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group.OrderByDescending(item => item.Mod.Priority)
                    .ThenBy(item => item.Mod.Manifest.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                var top = ordered.TakeWhile(item => item.Mod.Priority == ordered[0].Mod.Priority).ToArray();
                return new GameDataCellContest(group.Key, ordered[0], ordered[1..],
                    top.Any(item => !GameDataValues.Same(item.Edit.Value, ordered[0].Edit.Value)));
            })
            .OrderBy(cell => cell.CellKey, StringComparer.Ordinal)
            .ToArray();
        return new GameDataResolution(cells);
    }

    /// <summary>The lowest priority at which <paramref name="modId"/> wins every table cell it shares with an enabled mod.</summary>
    public static int PriorityToWinAll(IReadOnlyList<ArkiveModLibraryEntry> mods, string modId)
    {
        if (mods.FirstOrDefault(mod => mod.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase)) is not { } mine) return 0;
        var cells = (mine.Manifest.GameData?.Edits ?? []).Select(edit => edit.CellKey).ToHashSet(StringComparer.Ordinal);
        var competitors = mods
            .Where(mod => mod.Enabled && !mod.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase) &&
                          mod.Manifest.GameData?.Edits.Any(edit => cells.Contains(edit.CellKey)) == true)
            .Select(mod => mod.Priority)
            .ToArray();
        return competitors.Length == 0 ? mine.Priority : Math.Max(mine.Priority, competitors.Max() + 1);
    }

    /// <summary>The cells <paramref name="modId"/> changes: how many it wins, loses to a higher priority mod, or ties.</summary>
    public ArkiveModGameDataStatus StatusFor(string modId, GameDataTarget installed)
    {
        int winning = 0, overridden = 0, tied = 0, inGame = 0;
        var installedValues = installed.Edits.ToDictionary(edit => edit.CellKey, edit => edit.Value, StringComparer.Ordinal);
        foreach (var cell in Cells)
        {
            var mine = cell.Winner.Mod.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase);
            if (!mine && !cell.Others.Any(item => item.Mod.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))) continue;
            if (cell.Tied) tied++;
            else if (!mine) overridden++;
            else
            {
                winning++;
                if (installedValues.TryGetValue(cell.CellKey, out var value) && GameDataValues.Same(value, cell.Winner.Edit.Value)) inGame++;
            }
        }
        return new ArkiveModGameDataStatus(winning, overridden, tied, winning > 0 && inGame == winning);
    }
}

/// <summary>How a mod's table changes stand: cells it wins, loses or ties, and whether every winning cell is in the game.</summary>
public sealed record ArkiveModGameDataStatus(int Winning, int Overridden, int Tied, bool InGame);

public enum GameDataEditState
{
    /// <summary>The game still has the value the mod was made against.</summary>
    Exact,
    /// <summary>A game update changed the cell's value; the mod needs an update for this game version.</summary>
    Changed,
    /// <summary>The game no longer has the table, column or row.</summary>
    Missing,
}

public sealed record GameDataEditCheck(ArkiveModLibraryEntry Mod, GameDataCellEdit Edit, GameDataEditState State, JsonElement? GameValue)
{
    public string Message => State switch
    {
        GameDataEditState.Changed =>
            $"{Edit.Table}.{Edit.Column} for {Edit.RowLabel} is {GameDataValues.Describe(GameValue!.Value)} in this game version " +
            $"(the mod was made when it was {GameDataValues.Describe(Edit.Original)}).",
        GameDataEditState.Missing => $"{Edit.Database} no longer has {Edit.Table}.{Edit.Column} for {Edit.RowLabel}.",
        _ => $"{Edit.Table}.{Edit.Column} for {Edit.RowLabel} is unchanged.",
    };
}

/// <summary>What applying the enabled mods does to data2.lpk.</summary>
public sealed record GameDataPlan(
    GameDataTarget Target,
    bool Changes,
    GameDataResolution Resolution,
    IReadOnlyList<GameDataEditCheck> Checks,
    IReadOnlyList<string> Problems,
    IReadOnlySet<string> ModsNeedingUpdate)
{
    public bool Restores => Changes && Target.Edits.Count == 0;
}

/// <summary>What updating a mod's table changes for a new game version did.</summary>
public sealed record GameDataRebaseResult(ArkiveModGameData? GameData, int Updated, int Removed, IReadOnlyList<string> Messages);

/// <summary>
/// Game Data mods: lists of table cell changes in data2.lpk that go through the same plan, apply, restore and
/// game-update steps as package mods. Each change records the game's value when it was made, so a game update that
/// changes that cell (or removes its row) marks the mod as needing an update for the new game version.
/// </summary>
public sealed class GameDataModService
{
    public GameDataModService(GameDataArchive archive) => Archive = archive;

    public GameDataArchive Archive { get; }

    /// <summary>Compares each winning cell with the game's own databases and works out what data2.lpk should hold.</summary>
    public async Task<GameDataPlan> PlanAsync(IReadOnlyList<ArkiveModLibraryEntry> mods, CancellationToken cancellationToken = default)
    {
        var resolution = GameDataResolution.Resolve(mods);
        var problems = new List<string>();
        var needUpdate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tie in resolution.Ties)
            problems.Add($"{tie.Winner.Edit.Table}.{tie.Winner.Edit.Column} for {tie.Winner.Edit.RowLabel} is changed by " +
                         $"{string.Join(" and ", new[] { tie.Winner }.Concat(tie.Others).Where(item => item.Mod.Priority == tie.Winner.Mod.Priority).Select(item => item.Mod.Manifest.Name).Distinct())} " +
                         $"at the same priority {tie.Winner.Mod.Priority}. Give one of them a higher priority.");
        var checks = await CheckAsync(resolution.Cells.Select(cell => cell.Winner), cancellationToken).ConfigureAwait(false);
        foreach (var check in checks.Where(check => check.State != GameDataEditState.Exact))
        {
            needUpdate.Add(check.Mod.ModId);
            problems.Add($"{check.Mod.Manifest.Name}: {check.Message} Select the mod and choose 'Update for this game version'.");
        }
        var target = new GameDataTarget(Archive.Current().Language,
            resolution.Cells.Select(cell => cell.Winner.Edit).ToArray());
        return new GameDataPlan(target, !Same(Archive.Current(), target), resolution, checks, problems.Distinct().ToArray(), needUpdate);
    }

    /// <summary>Compares <paramref name="edits"/> with the game's own databases (Arkive's backups where it has changed them).</summary>
    public async Task<IReadOnlyList<GameDataEditCheck>> CheckAsync(
        IEnumerable<(ArkiveModLibraryEntry Mod, GameDataCellEdit Edit)> edits, CancellationToken cancellationToken = default)
    {
        var checks = new List<GameDataEditCheck>();
        foreach (var database in edits.GroupBy(item => item.Edit.Database, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path;
            try
            {
                path = await Archive.GameDatabaseAsync(database.Key, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("not in the game's data archive", StringComparison.Ordinal))
            {
                checks.AddRange(database.Select(item => new GameDataEditCheck(item.Mod, item.Edit, GameDataEditState.Missing, null)));
                continue;
            }
            using var connection = GameDataTables.Open(path, readOnly: true);
            var schemas = new Dictionary<string, GameDataTableSchema?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (mod, edit) in database)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = GameDataTables.Read(connection, edit, schemas);
                var state = value is null ? GameDataEditState.Missing
                    : GameDataValues.Same(value.Value, edit.Original) ? GameDataEditState.Exact : GameDataEditState.Changed;
                checks.Add(new GameDataEditCheck(mod, edit, state, value));
            }
        }
        return checks;
    }

    /// <summary>Writes the plan's table changes into data2.lpk, keeping the Language tab's choice.</summary>
    public Task<GameDataWriteResult> ApplyAsync(GameDataPlan plan, CancellationToken cancellationToken = default) =>
        Archive.WriteAsync(plan.Target with { Language = Archive.Current().Language }, cancellationToken);

    /// <summary>Takes every mod's table changes out of data2.lpk, keeping the Language tab's choice.</summary>
    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        var current = Archive.Current();
        if (current.Edits.Count == 0) return false;
        await Archive.WriteAsync(current with { Edits = [] }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Updates a mod's table changes for this game version: each change keeps the mod's value and takes the game's new
    /// value as its original; changes whose row the game removed are dropped.
    /// </summary>
    public async Task<GameDataRebaseResult> RebaseAsync(ArkiveModLibraryEntry mod, CancellationToken cancellationToken = default)
    {
        if (mod.Manifest.GameData is not { } data) return new GameDataRebaseResult(null, 0, 0, []);
        var checks = await CheckAsync(data.Edits.Select(edit => (mod, edit)), cancellationToken).ConfigureAwait(false);
        var edits = new List<GameDataCellEdit>();
        var messages = new List<string>();
        int updated = 0, removed = 0;
        foreach (var check in checks)
        {
            switch (check.State)
            {
                case GameDataEditState.Exact:
                    edits.Add(check.Edit);
                    break;
                case GameDataEditState.Changed:
                    edits.Add(check.Edit with { Original = check.GameValue!.Value });
                    messages.Add(check.Message + $" It keeps the mod's {GameDataValues.Describe(check.Edit.Value)}.");
                    updated++;
                    break;
                default:
                    messages.Add(check.Message + " The change was removed from the mod.");
                    removed++;
                    break;
            }
        }
        // Keep the mod's own order.
        var order = data.Edits.Select((edit, index) => (edit.CellKey, index)).ToDictionary(item => item.CellKey, item => item.index, StringComparer.Ordinal);
        edits.Sort((left, right) => order[left.CellKey].CompareTo(order[right.CellKey]));
        return new GameDataRebaseResult(edits.Count == 0 ? null : new ArkiveModGameData(edits), updated, removed, messages);
    }

    private static bool Same(GameDataTarget left, GameDataTarget right) =>
        left.Language == right.Language && left.Edits.Count == right.Edits.Count &&
        left.Edits.OrderBy(edit => edit.CellKey, StringComparer.Ordinal)
            .Zip(right.Edits.OrderBy(edit => edit.CellKey, StringComparer.Ordinal))
            .All(pair => pair.First.CellKey == pair.Second.CellKey && GameDataValues.Same(pair.First.Value, pair.Second.Value));
}
