using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arkive.Core.Audio;
using Arkive.Core.Mods;

namespace Arkive.App;

/// <summary>One named game sound in the Sounds tab.</summary>
public sealed record SoundRow(SoundEvent Event, string Length, string Kind, string Status)
{
    public string Name => Event.Name;
    public string Set => Event.Set;
    public int ClipCount => Event.SourceIds.Count;
}

public partial class MainWindow
{
    private SoundCatalog? _soundCatalog;
    private string? _soundCatalogRoot;
    private SoundReplacementService? _soundReplacements;
    private SoundPreviewService? _soundPreview;
    private bool _soundBusy;

    private SoundReplacementService SoundReplacements =>
        _soundReplacements ??= new SoundReplacementService(_projectRoot, new LostArkProcessGuard());

    private SoundPreviewService SoundPreview => _soundPreview ??= new SoundPreviewService(_projectRoot);

    private async void SoundSearchButton_Click(object sender, RoutedEventArgs e) => await SearchSoundsAsync();

    private async void SoundSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchSoundsAsync();
    }

    private async void SoundKindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && (_soundCatalog is not null || SelectedSoundKind() == "replaced")) await SearchSoundsAsync();
    }

    private void SoundResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SoundOpenButton.IsEnabled = SoundResultsGrid.SelectedItem is SoundRow && !_soundBusy;

    private void SoundResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && SoundResultsGrid.SelectedItem is SoundRow row) OpenSoundWindow(row);
    }

    private void SoundOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (SoundResultsGrid.SelectedItem is SoundRow row) OpenSoundWindow(row);
    }

    private string SelectedSoundKind() => (SoundKindComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";

    private async Task<SoundCatalog?> EnsureSoundCatalogAsync()
    {
        if (string.IsNullOrWhiteSpace(_libraryRootPath))
        {
            SoundStatusText.Text = "Choose the game's Packages folder in Asset Library first; the sounds are read from the folder beside it.";
            return null;
        }
        var audioRoot = SoundCatalog.AudioRootFor(_libraryRootPath);
        if (_soundCatalog is not null && string.Equals(_soundCatalogRoot, audioRoot, StringComparison.OrdinalIgnoreCase))
            return _soundCatalog;
        if (!Directory.Exists(audioRoot))
        {
            SoundStatusText.Text = $"The game's audio folder was not found at {audioRoot}.";
            return null;
        }

        var progress = new Progress<SoundCatalogProgress>(report =>
            SoundStatusText.Text = report.Total > 0
                ? $"{report.Stage}… {report.Completed:N0} of {report.Total:N0}"
                : $"{report.Stage}…");
        var started = DateTime.UtcNow;
        var names = await SoundEventNames.LoadAsync(
            _packageIndexStore.CacheDirectory, Path.Combine(_projectRoot, "cache", "sounds"), progress);
        if (names.Count == 0)
        {
            SoundStatusText.Text = "Arkive's package index lists no sound names yet. Build the index in Asset Library, then search again.";
            return null;
        }
        var catalog = await Task.Run(() => SoundCatalog.Build(audioRoot, names, progress));
        _soundCatalog = catalog;
        _soundCatalogRoot = audioRoot;
        Log($"Sound catalog read: root={audioRoot}, events={catalog.EventCount}, clips={catalog.ClipCount}, " +
            $"banks={catalog.BankCount}, unsupportedBanks={catalog.UnsupportedBankCount}, " +
            $"seconds={(DateTime.UtcNow - started).TotalSeconds:F1}.");
        return catalog;
    }

    private async Task SearchSoundsAsync()
    {
        if (_soundBusy) return;
        var query = SoundSearchTextBox.Text.Trim();
        var kind = SelectedSoundKind();
        if (query.Length == 0 && kind != "replaced")
        {
            SoundStatusText.Text = "Enter part of a sound's name, for example kazeros battle or bgm login.";
            return;
        }
        _soundBusy = true;
        SoundOpenButton.IsEnabled = false;
        try
        {
            var catalog = await EnsureSoundCatalogAsync();
            if (catalog is null) return;
            SoundStatusText.Text = $"Searching for {(query.Length > 0 ? query : "your replaced sounds")}…";
            var replacements = SoundReplacements;
            var rows = await Task.Run(() => FindSoundRows(catalog, replacements, query, kind));
            SoundResultsGrid.ItemsSource = rows;
            SoundResultsGrid.SelectedItem = rows.FirstOrDefault();
            SoundStatusText.Text = rows.Count == 0
                ? kind == "replaced"
                    ? "You have not replaced any sounds."
                    : "No sound matched. Try fewer or shorter words; names use the developers' terms (bgm for music, vox for voices)."
                : $"{rows.Count:N0} sound(s){(rows.Count >= SoundResultLimit ? $" (the first {SoundResultLimit:N0}; add words to narrow the search)" : string.Empty)}. Double-click one to listen and replace its clips.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SoundStatusText.Text = $"Arkive could not read the game's sounds: {exception.Message}";
            Log($"Sound search failed: query={query}, error={exception}");
        }
        finally
        {
            _soundBusy = false;
            SoundOpenButton.IsEnabled = SoundResultsGrid.SelectedItem is SoundRow;
        }
    }

    private const int SoundResultLimit = 300;

    private static IReadOnlyList<SoundRow> FindSoundRows(
        SoundCatalog catalog, SoundReplacementService replacements, string query, string kind)
    {
        var state = replacements.LoadState();
        var replaced = state.Replacements.Select(record => record.SourceId).ToHashSet();
        IEnumerable<SoundEvent> events;
        if (kind == "replaced")
        {
            var names = state.Replacements.Select(record => record.SoundName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            events = names.SelectMany(name => catalog.Search(name, 50).Where(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                .Where(item => query.Length == 0 || Arkive.Core.Discovery.AssetSearch.Matches(query, item.Name, item.Set));
        }
        else
        {
            events = catalog.Search(query, kind == "all" ? SoundResultLimit : SoundResultLimit * 20);
        }

        var rows = new List<SoundRow>();
        foreach (var item in events)
        {
            var clips = item.SourceIds.Select(catalog.Clip).OfType<SoundClip>().ToArray();
            if (clips.Length == 0) continue;
            var isMusic = clips.Any(clip => clip.IsMusic);
            if (kind == "music" && !isMusic || kind == "effects" && isMusic) continue;
            var lengths = clips.Select(SoundLength).OfType<TimeSpan>().ToArray();
            var replacedCount = clips.Count(clip => replaced.Contains(clip.SourceId));
            rows.Add(new SoundRow(
                item,
                FormatLengths(lengths),
                isMusic ? "Music" : clips.Any(clip => clip.Files.Any(file => file.Language != "sfx")) ? "Voice" : "Effect",
                replacedCount == 0 ? string.Empty : replacedCount == clips.Length ? "Replaced" : $"{replacedCount} of {clips.Length} replaced"));
            if (rows.Count >= SoundResultLimit) break;
        }
        return rows;
    }

    internal static TimeSpan? SoundLength(SoundClip clip)
    {
        try
        {
            return SoundCatalog.ReadFormat(clip)?.Duration ??
                   (clip.MusicDurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string FormatLength(TimeSpan length) =>
        length.TotalSeconds < 60
            ? $"{length.TotalSeconds:0.0} s"
            : $"{(int)length.TotalMinutes}:{length.Seconds:00}";

    private static string FormatLengths(IReadOnlyList<TimeSpan> lengths)
    {
        if (lengths.Count == 0) return "unknown";
        var shortest = lengths.Min();
        var longest = lengths.Max();
        return Math.Abs((longest - shortest).TotalSeconds) < 0.05
            ? FormatLength(longest)
            : $"{FormatLength(shortest)} – {FormatLength(longest)}";
    }

    private void OpenSoundWindow(SoundRow row)
    {
        if (_soundCatalog is not { } catalog) return;
        var window = new SoundWindow(catalog, row.Event, SoundReplacements, SoundPreview, Log) { Owner = this };
        window.Closed += async (_, _) => await RefreshSoundRowsAsync();
        window.Show();
    }

    private async Task RefreshSoundRowsAsync()
    {
        if (_soundCatalog is null || SoundResultsGrid.ItemsSource is not IReadOnlyList<SoundRow> { Count: > 0 } rows) return;
        var selected = (SoundResultsGrid.SelectedItem as SoundRow)?.Name;
        var catalog = _soundCatalog;
        var replacements = SoundReplacements;
        var state = await Task.Run(replacements.LoadState);
        var replaced = state.Replacements.Select(record => record.SourceId).ToHashSet();
        var refreshed = await Task.Run(() => rows.Select(row =>
        {
            var clips = row.Event.SourceIds.Select(catalog.Clip).OfType<SoundClip>().ToArray();
            var count = clips.Count(clip => replaced.Contains(clip.SourceId));
            var lengths = clips.Select(SoundLength).OfType<TimeSpan>().ToArray();
            return row with
            {
                Length = FormatLengths(lengths),
                Status = count == 0 ? string.Empty : count == clips.Length ? "Replaced" : $"{count} of {clips.Length} replaced",
            };
        }).ToArray());
        SoundResultsGrid.ItemsSource = refreshed;
        SoundResultsGrid.SelectedItem = refreshed.FirstOrDefault(row => row.Name == selected);
    }

    private async void SoundRestoreAllButton_Click(object sender, RoutedEventArgs e)
    {
        var records = SoundReplacements.LoadState().Replacements;
        if (records.Count == 0)
        {
            SoundStatusText.Text = "You have not replaced any sounds, so there is nothing to restore.";
            return;
        }
        if (MessageBox.Show(this,
                $"Put the game's original audio back for all {records.Count:N0} clip(s) you replaced?",
                "Restore all sounds", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        SoundRestoreAllButton.IsEnabled = false;
        try
        {
            foreach (var record in records)
            {
                SoundStatusText.Text = $"Restoring {record.SoundName}…";
                await SoundReplacements.RestoreAsync(record.SourceId);
            }
            SoundStatusText.Text = $"Restored {records.Count:N0} clip(s). The game plays its original sounds again.";
            Log($"Sounds restored: clips={records.Count}.");
            await RefreshSoundRowsAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            SoundStatusText.Text = exception.Message;
            Log($"Restoring all sounds failed: {exception}");
        }
        finally
        {
            SoundRestoreAllButton.IsEnabled = true;
        }
    }
}
