using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Arkive.Core.Mods;

namespace Arkive.App;

/// <summary>
/// Mod packs (one switch for a set of mods, shown as a heading in the Mods list) and the warning shown when enabled
/// mods change the same assets.
/// </summary>
public partial class MainWindow : Window
{
    private ArkiveModPackStore? _modPackStore;
    private ArkiveModPackManifest _modPackManifest = new(ArkiveModPackStore.CurrentSchemaVersion, []);
    private IReadOnlyList<ArkiveModOverlap> _modOverlaps = [];

    private ArkiveModPackStore ModPacks => _modPackStore ??= new ArkiveModPackStore(_arkiveModLibrary.StateDirectory);

    /// <summary>
    /// Lists the mods with each pack's mods together under the pack's heading, at the place of the pack's first mod in
    /// the usual order (enabled first, then by priority).
    /// </summary>
    private ListCollectionView GroupModRows(IReadOnlyList<ModLibraryRow> ordered, ArkiveModResolution resolution)
    {
        _modPackManifest = ModPacks.Load(_arkiveModLibraryManifest.Mods.Select(mod => mod.ModId));
        var packGroups = new Dictionary<string, ModListGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in _modPackManifest.Packs) packGroups[pack.PackId] = new ModListGroup(pack);
        var listed = new List<ModLibraryRow>(ordered.Count);
        ModListGroup? loose = null;
        foreach (var row in ordered)
        {
            if (_modPackManifest.PackOf(row.Entry.ModId) is { } pack)
            {
                loose = null;
                var group = packGroups[pack.PackId];
                if (group.Rows.Count > 0) continue;
                foreach (var member in ordered.Where(item => pack.Contains(item.Entry.ModId)))
                {
                    member.Group = group;
                    group.Rows.Add(member);
                    listed.Add(member);
                }
                continue;
            }
            loose ??= new ModListGroup(null);
            row.Group = loose;
            loose.Rows.Add(row);
            listed.Add(row);
        }

        _modOverlaps = resolution.EnabledOverlaps();
        foreach (var group in packGroups.Values)
        {
            var outside = _modOverlaps
                .Select(overlap => (Pack: group.Pack!, Overlap: overlap))
                .Where(item => item.Pack.Contains(item.Overlap.WinnerModId) != item.Pack.Contains(item.Overlap.OtherModId))
                .Select(item => item.Pack.Contains(item.Overlap.WinnerModId) ? item.Overlap.OtherName : item.Overlap.WinnerName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            group.Warning = outside.Length == 0 ? string.Empty : $"⚠ Changes the same assets as {NameList(outside)}";
        }

        var view = new ListCollectionView(listed);
        view.GroupDescriptions.Add(new ModListGrouping());
        return view;
    }

    private void ShowModOverlapWarning()
    {
        var rows = OverlapRows();
        var tied = _modOverlaps.Any(overlap => overlap.Tied);
        var hidden = !tied && _modPackManifest.HiddenOverlapWarning == ArkiveModOverlap.Signature(_modOverlaps);
        ModOverlapBanner.Visibility = rows.Length == 0 || hidden ? Visibility.Collapsed : Visibility.Visible;
        if (ModOverlapBanner.Visibility != Visibility.Visible) return;
        ModOverlapTitleText.Text = rows.Length == 1
            ? "Enabled mods change the same assets."
            : $"Enabled mods change the same assets in {rows.Length:N0} places.";
        ModOverlapList.ItemsSource = rows.Take(3).ToArray();
        ModOverlapMoreText.Text = rows.Length > 3
            ? $"{rows.Length - 3:N0} more overlap(s). Each mod's Conflicts tab lists the assets it shares."
            : string.Empty;
        ModOverlapMoreText.Visibility = rows.Length > 3 ? Visibility.Visible : Visibility.Collapsed;
        ModOverlapHideButton.Visibility = tied ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The overlap warning's lines: overlaps between the same two sides (a pack counts as one side) are summed.</summary>
    private ModOverlapRow[] OverlapRows()
    {
        ModOverlapSide Side(string modId, string name, int priority, string otherModId) =>
            _modPackManifest.PackOf(modId) is { } pack && !pack.Contains(otherModId)
                ? new ModOverlapSide(modId, name, priority, pack)
                : new ModOverlapSide(modId, name, priority, null);
        return _modOverlaps
            .Select(overlap => (Overlap: overlap,
                Winner: overlap.Tied ? new ModOverlapSide(overlap.WinnerModId, overlap.WinnerName, overlap.WinnerPriority, null)
                    : Side(overlap.WinnerModId, overlap.WinnerName, overlap.WinnerPriority, overlap.OtherModId),
                Other: overlap.Tied ? new ModOverlapSide(overlap.OtherModId, overlap.OtherName, overlap.OtherPriority, null)
                    : Side(overlap.OtherModId, overlap.OtherName, overlap.OtherPriority, overlap.WinnerModId)))
            .GroupBy(item => (item.Overlap.Tied,
                Winner: item.Winner.Pack?.PackId ?? item.Winner.ModId, Other: item.Other.Pack?.PackId ?? item.Other.ModId,
                Single: item.Overlap.Tied ? item.Overlap.WinnerModId + ">" + item.Overlap.OtherModId : string.Empty))
            .Select(group => new ModOverlapRow(group.First().Winner, group.First().Other, group.Key.Tied,
                group.SelectMany(item => item.Overlap.SharedAssets).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderByDescending(row => row.Tied)
            .ThenByDescending(row => row.SharedAssets.Count)
            .ToArray();
    }

    /// <summary>A sentence for the status line when mods were just turned on and now overlap other enabled mods.</summary>
    private string OverlapNotice(IReadOnlyList<ArkiveModLibraryEntry> mods, IReadOnlyCollection<string> turnedOn)
    {
        var ids = turnedOn.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Label(string modId, string name) =>
            _modPackManifest.PackOf(modId) is { } pack && !pack.ModIds.Any(ids.Contains) ? $"the {pack.Name} pack" : name;
        var others = ArkiveModConflictResolver.Resolve(mods).EnabledOverlaps()
            .Where(overlap => ids.Contains(overlap.WinnerModId) != ids.Contains(overlap.OtherModId))
            .Select(overlap => ids.Contains(overlap.WinnerModId)
                ? Label(overlap.OtherModId, overlap.OtherName)
                : Label(overlap.WinnerModId, overlap.WinnerName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return others.Length == 0
            ? string.Empty
            : $" It changes some of the same assets as {NameList(others)}; the warning above the list shows which mod wins.";
    }

    private static string NameList(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{names[0]}, {names[1]} and {names.Count - 2:N0} more",
    };

    private static ModListGroup? PackGroupOf(object sender) =>
        (sender as FrameworkElement)?.DataContext is CollectionViewGroup { Name: ModListGroup { IsPack: true } group } ? group : null;

    private void ModLibraryNewPackButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = ModLibraryGrid.SelectedItem as ModLibraryRow;
        var name = selected is null ? string.Empty : ModPackWindow.SuggestedName(selected.DisplayName);
        OpenModPackWindow(null, name, row =>
            row == selected || selected is not null && row.Group?.Pack is null &&
            ModPackWindow.BelongsWith(name, row.DisplayName));
    }

    private void ModPackEditItem_Click(object sender, RoutedEventArgs e)
    {
        if (PackGroupOf(sender) is { Pack: { } pack }) OpenModPackWindow(pack, pack.Name, row => pack.Contains(row.Entry.ModId));
    }

    private void OpenModPackWindow(ArkiveModPack? pack, string name, Func<ModLibraryRow, bool> include)
    {
        if (!ModLibraryIdle) return;
        var rows = ModLibraryGrid.Items.OfType<ModLibraryRow>()
            .OrderBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (rows.Length < 2)
        {
            ModLibraryStatusText.Text = StatusText.Text = "A pack needs at least two mods. Create or import more mods first.";
            return;
        }
        var choices = rows.Select(row => new ModPackWindow.Choice(row.Entry.ModId, row.DisplayName,
            row.Group?.Pack is { } other && other.PackId != pack?.PackId ? $"Now in the pack {other.Name}; ticking moves it here." : string.Empty,
            include(row))).ToArray();
        var window = new ModPackWindow(pack is not null, name, choices) { Owner = this };
        if (window.ShowDialog() != true) return;
        ArkiveModPack saved = null!;
        if (!TryChangePacks(() => saved = ModPacks.Save(pack?.PackId, window.PackName, window.ModIds))) return;
        Log($"Mod pack {(pack is null ? "created" : "edited")}: pack={saved.PackId}, name={saved.Name}, mods={string.Join(", ", saved.ModIds)}.");
        _ = RefreshArkiveModsAsync(pack is null ? "mod pack created" : "mod pack edited",
            (ModLibraryGrid.SelectedItem as ModLibraryRow)?.Entry.ModId);
        ModLibraryStatusText.Text = StatusText.Text = pack is null
            ? $"Created the pack {saved.Name} with {saved.ModIds.Count:N0} mods. Its box turns them all on or off."
            : $"Saved the pack {saved.Name}.";
    }

    private async void ModPackSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (PackGroupOf(sender) is { } group) await SetPackEnabledAsync(group, group.OnState != true);
    }

    private async void ModPackTurnOnItem_Click(object sender, RoutedEventArgs e)
    {
        if (PackGroupOf(sender) is { } group) await SetPackEnabledAsync(group, group.OnState != true);
    }

    private Task SetPackEnabledAsync(ModListGroup group, bool enabled) =>
        RunModLibraryOperationAsync(enabled ? "Turn pack on" : "Turn pack off", async () =>
        {
            var ids = group.Rows.Select(row => row.Entry.ModId).ToArray();
            var library = await _arkiveModLibrary.SetEnabledAsync(ids, enabled);
            Log($"Mod pack {(enabled ? "turned on" : "turned off")}: pack={group.Pack!.PackId}, name={group.Title}, mods={string.Join(", ", ids)}.");
            await SynchronizeModSelectionsAsync(enabled ? "mod pack turned on" : "mod pack turned off", onlyIfChanged: true, deferWorkspace: true);
            return enabled
                ? $"Turned on the pack {group.Title} ({ids.Length:N0} mods).{OverlapNotice(library.Mods, ids)} Choose Apply changes to update the game."
                : $"Turned off the pack {group.Title} ({ids.Length:N0} mods). Choose Apply changes to update the game.";
        }, (ModLibraryGrid.SelectedItem as ModLibraryRow)?.Entry.ModId);

    private void ModPackToggle_Click(object sender, RoutedEventArgs e)
    {
        if (PackGroupOf(sender) is not { Pack: { } pack } group) return;
        TryChangePacks(() => ModPacks.SetCollapsed(pack.PackId, !group.IsExpanded));
    }

    private void ModPackMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || PackGroupOf(menu) is not { } group) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsEnabled = ModLibraryIdle;
            if (item.Name == "ModPackTurnOnItem") item.Header = group.OnState == true ? "Turn pack off" : "Turn pack on";
        }
    }

    private async void ModPackMakeWinItem_Click(object sender, RoutedEventArgs e)
    {
        if (PackGroupOf(sender) is { Pack: { } pack }) await MakePackWinAsync(pack);
    }

    private async Task MakePackWinAsync(ArkiveModPack pack)
    {
        var raise = ArkiveModPackStore.PriorityRaiseToWin(pack, _arkiveModLibraryManifest.Mods);
        if (raise <= 0)
        {
            ModLibraryStatusText.Text = StatusText.Text = $"The pack {pack.Name} already wins every overlap with enabled mods outside it.";
            return;
        }
        await RunModLibraryOperationAsync("Mod priority", async () =>
        {
            await _arkiveModLibrary.RaisePrioritiesAsync(pack.ModIds, raise);
            Log($"Mod pack priorities raised: pack={pack.PackId}, name={pack.Name}, by={raise}.");
            await SynchronizeModSelectionsAsync("mod pack priority raised", onlyIfChanged: true, deferWorkspace: true);
            return $"Raised every mod in {pack.Name} by {raise:N0} priority, so the pack wins its overlaps. Choose Apply changes to update the game.";
        }, (ModLibraryGrid.SelectedItem as ModLibraryRow)?.Entry.ModId);
    }

    private void ModPackRemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (!ModLibraryIdle || PackGroupOf(sender) is not { Pack: { } pack }) return;
        if (!TryChangePacks(() => ModPacks.Remove(pack.PackId))) return;
        Log($"Mod pack removed: pack={pack.PackId}, name={pack.Name}; its mods stay in the library.");
        _ = RefreshArkiveModsAsync("mod pack removed", (ModLibraryGrid.SelectedItem as ModLibraryRow)?.Entry.ModId);
        ModLibraryStatusText.Text = StatusText.Text = $"Removed the pack {pack.Name}. Its mods stay in the list, each with its own On box.";
    }

    /// <summary>Fills the mod row's Add to pack menu with the existing packs.</summary>
    private void FillAddToPackMenu(ModLibraryRow? row)
    {
        ModLibraryRowAddToPackItem.Items.Clear();
        var current = row?.Group?.Pack;
        foreach (var pack in _modPackManifest.Packs.Where(pack => pack.PackId != current?.PackId))
        {
            var item = new MenuItem { Header = pack.Name.Replace("_", "__"), Tag = pack.PackId };
            item.Click += ModLibraryRowAddToPack_Click;
            ModLibraryRowAddToPackItem.Items.Add(item);
        }
        if (ModLibraryRowAddToPackItem.Items.Count > 0) ModLibraryRowAddToPackItem.Items.Add(new Separator());
        var create = new MenuItem { Header = "New pack…" };
        create.Click += ModLibraryNewPackButton_Click;
        ModLibraryRowAddToPackItem.Items.Add(create);
        ModLibraryRowRemoveFromPackItem.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        ModLibraryRowRemoveFromPackItem.Header = current is null ? "Remove from pack" : $"Remove from {current.Name.Replace("_", "__")}";
    }

    private void ModLibraryRowAddToPack_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedModRows();
        if (!ModLibraryIdle || rows.Length == 0 ||
            sender is not MenuItem { Tag: string packId } || _modPackManifest.Find(packId) is not { } pack) return;
        var ids = rows.Select(row => row.Entry.ModId).ToArray();
        if (!TryChangePacks(() => ModPacks.Save(pack.PackId, pack.Name, [.. pack.ModIds, .. ids]))) return;
        Log($"Mods added to pack: mods={string.Join(", ", ids)}, pack={pack.PackId}, name={pack.Name}.");
        _ = RefreshArkiveModsAsync("mod added to pack", ids[0]);
        ModLibraryStatusText.Text = StatusText.Text = rows.Length == 1
            ? $"Added {rows[0].DisplayName} to the pack {pack.Name}."
            : $"Added {rows.Length:N0} mods to the pack {pack.Name}.";
    }

    private void ModLibraryRowRemoveFromPack_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedModRows().Where(row => row.Group?.Pack is not null).ToArray();
        if (!ModLibraryIdle || rows.Length == 0) return;
        if (!TryChangePacks(() => { foreach (var row in rows) ModPacks.RemoveMod(row.Entry.ModId); })) return;
        Log($"Mods removed from packs: mods={string.Join(", ", rows.Select(row => row.Entry.ModId))}.");
        _ = RefreshArkiveModsAsync("mod removed from pack", rows[0].Entry.ModId);
        ModLibraryStatusText.Text = StatusText.Text = rows.Length == 1
            ? $"Took {rows[0].DisplayName} out of the pack {rows[0].Group.Pack!.Name}. The mod stays in the list."
            : $"Took {rows.Length:N0} mods out of their packs. They stay in the list.";
    }

    private void ModOverlapHideButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryChangePacks(() => ModPacks.HideOverlapWarning(ArkiveModOverlap.Signature(_modOverlaps)))) return;
        _modPackManifest = _modPackManifest with { HiddenOverlapWarning = ArkiveModOverlap.Signature(_modOverlaps) };
        ShowModOverlapWarning();
        ModLibraryStatusText.Text = StatusText.Text = "Hid the overlap warning. It shows again if the overlapping mods or their winners change.";
    }

    private async void ModOverlapMakeOtherWin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModOverlapRow { Other: var other }) return;
        if (other.Pack is { } pack) await MakePackWinAsync(pack);
        else await MakeModWinAsync(other.ModId);
    }

    private async void ModOverlapMakeWinnerWin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModOverlapRow overlap) await MakeModWinAsync(overlap.Winner.ModId);
    }

    private async Task MakeModWinAsync(string modId)
    {
        if (ModLibraryGrid.Items.OfType<ModLibraryRow>().FirstOrDefault(row =>
                row.Entry.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase)) is not { } row) return;
        await SetModPriorityAsync(row, ArkiveModConflictResolver.Resolve(_arkiveModLibraryManifest.Mods).PriorityToWinAll(modId));
    }

    /// <summary>Selects the mod that loses the overlap and opens its Conflicts tab, which lists the shared assets.</summary>
    private void ModOverlapShow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModOverlapRow overlap ||
            ModLibraryGrid.Items.OfType<ModLibraryRow>().FirstOrDefault(row =>
                row.Entry.ModId.Equals(overlap.Other.ModId, StringComparison.OrdinalIgnoreCase)) is not { } row) return;
        if (row.Group is { IsPack: true, IsExpanded: false } group)
        {
            group.IsExpanded = true;
            TryChangePacks(() => ModPacks.SetCollapsed(group.Pack!.PackId, false));
            (ModLibraryGrid.ItemsSource as ListCollectionView)?.Refresh();
        }
        ModLibraryGrid.SelectedItem = row;
        ModLibraryGrid.ScrollIntoView(row);
        ModLibraryConflictsTab.IsSelected = true;
    }

    /// <summary>Saves a change to the packs file; packs are a convenience, so a failed write is reported, not thrown.</summary>
    private bool TryChangePacks(Action change)
    {
        try
        {
            change();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArkiveModRefusalException)
        {
            Log($"Mod packs not saved: {exception}");
            ModLibraryStatusText.Text = StatusText.Text = $"The mod packs could not be saved: {exception.Message}";
            return false;
        }
    }
}
