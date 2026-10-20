using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Arkive.Core.Fonts;
using Arkive.Core.LoginScreens;
using Arkive.Core.Mods;

namespace Arkive.App;

/// <summary>One kind of text in the Fonts tab: the font chosen for it and a line of text in that font.</summary>
public sealed class FontSlotRow(GameFontSlot slot, string title, string detail, string sample, string resetLabel) : INotifyPropertyChanged
{
    private string _choice = string.Empty;
    private FontFamily? _preview;
    private bool _chosen;

    public event PropertyChangedEventHandler? PropertyChanged;

    public GameFontSlot Slot { get; } = slot;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public string Sample { get; } = sample;
    public string ResetLabel { get; } = resetLabel;

    /// <summary>The chosen font's name and file, or what the game uses on its own.</summary>
    public string Choice
    {
        get => _choice;
        set => Set(ref _choice, value, nameof(Choice));
    }

    public FontFamily? Preview
    {
        get => _preview;
        set => Set(ref _preview, value, nameof(Preview));
    }

    /// <summary>True when a font file is chosen (rather than the game's own).</summary>
    public bool Chosen
    {
        get => _chosen;
        set => Set(ref _chosen, value, nameof(Chosen));
    }

    private void Set<T>(ref T field, T value, string name)
    {
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public partial class MainWindow
{
    private GameFontService? _gameFonts;
    private bool _fontsBusy;
    private bool _fontsRefreshing;
    private bool _fontsUpdated;
    private string? _fontsForeignChange;
    private IReadOnlyDictionary<GameFontSlot, string> _gameOwnFonts = new Dictionary<GameFontSlot, string>();
    // The font file chosen for each kind of text, applied or not yet; a kind left out keeps the game's own.
    private Dictionary<GameFontSlot, string>? _fontChoices;
    private readonly Dictionary<string, (FontFamily Family, string Name)> _fontPreviews = new(StringComparer.OrdinalIgnoreCase);

    private FontSlotRow[] FontRows => FontSlotList.ItemsSource as FontSlotRow[] ?? [];

    private async Task<GameFontService?> GameFontsAsync()
    {
        if (_gameFonts is not null) return _gameFonts;
        await EnsureGamePackagesDirectoryAsync();
        var library = await _arkiveModLibrary.LoadAsync();
        var efGame = LoginScreenService.EfGameFromPackages(library.GamePackagesDirectory ?? _libraryRootPath);
        if (efGame is null || !File.Exists(Path.Combine(efGame, GameFontService.ArchiveName))) return null;
        return _gameFonts = new GameFontService(efGame, _projectRoot, new LostArkProcessGuard());
    }

    private async Task RefreshFontsAsync(string? status = null)
    {
        // Switching tabs while a refresh is still reading the game's fonts must not start a second one.
        if (_fontsBusy || _fontsRefreshing) return;
        _fontsRefreshing = true;
        try
        {
            await LoadFontsAsync(status);
        }
        finally
        {
            _fontsRefreshing = false;
        }
    }

    private async Task LoadFontsAsync(string? status)
    {
        FontSlotList.ItemsSource ??= new[]
        {
            new FontSlotRow(GameFontSlot.BodyText, "Body Text", "Chat, quests, menus, tooltips and most other text.",
                "The quick brown fox jumps over the lazy dog. 0123456789", "Game's own"),
            new FontSlotRow(GameFontSlot.Titles, "Titles", "Headings, names, the chronicle and other titles.",
                "Welcome to Arkesia", "Game's own"),
            new FontSlotRow(GameFontSlot.DamageNumbers, "Damage Numbers", "The numbers over enemies. They use the titles font unless you choose one.",
                "12,345,678  987,654", "Same as titles"),
        };
        try
        {
            if (await GameFontsAsync() is not { } service)
            {
                FontStatusText.Text = "Choose the game folder first (Mods tab, or index the Asset Library): Arkive needs the game's EFGame folder with font.lpk.";
                UpdateFontActions();
                return;
            }
            var (state, updated, foreign, own) = await Task.Run(async () =>
                (service.LoadState(), await service.GameUpdatedAsync(), await service.ForeignChangeAsync(), await service.GameFontsAsync()));
            _fontsUpdated = updated;
            _fontsForeignChange = foreign;
            _gameOwnFonts = own;
            _fontChoices ??= state?.Fonts.Where(font => File.Exists(font.StoredPath)).ToDictionary(font => font.Slot, font => font.StoredPath) ?? [];
            ShowFontChoices();
            FontStatusText.Text = status ?? foreign ?? (updated
                ? "The game updated font.lpk since you applied your fonts, so it is back to its own. Apply again to use them."
                : state is null
                    ? "The game uses its own fonts. Choose a font for any kind of text, then apply."
                    : $"In use: {string.Join(", ", state.Fonts.Select(font => $"{font.Name} for {GameFontService.Describe(font.Slot).ToLowerInvariant()}"))}.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            FontStatusText.Text = "The game's fonts could not be read: " + exception.Message;
            Log($"Fonts failed to load: {exception}");
        }
        UpdateFontActions();
    }

    private void ShowFontChoices()
    {
        var choices = _fontChoices ?? [];
        var titles = choices.GetValueOrDefault(GameFontSlot.Titles);
        foreach (var row in FontRows)
        {
            var chosen = choices.GetValueOrDefault(row.Slot);
            var font = chosen ?? (row.Slot == GameFontSlot.DamageNumbers ? titles : null) ?? _gameOwnFonts.GetValueOrDefault(row.Slot);
            var preview = font is null ? null : PreviewFont(font);
            row.Chosen = chosen is not null;
            row.Preview = preview?.Family;
            var stored = _gameFonts?.LoadState()?.Fonts.FirstOrDefault(item => item.StoredPath.Equals(chosen, StringComparison.OrdinalIgnoreCase));
            row.Choice = chosen is not null
                ? $"{preview?.Name ?? "Unreadable font"} ({stored?.Name ?? Path.GetFileName(chosen)})"
                : row.Slot == GameFontSlot.DamageNumbers
                    ? $"Same as titles ({preview?.Name ?? "the game's own"})"
                    : $"The game's own ({preview?.Name ?? "not found"})";
        }
    }

    // WPF keeps a font file open while it is shown, so previews read a copy Arkive never changes or deletes. Each copy
    // has a folder of its own: WPF fails to read a second font file from a folder it has already read one from.
    private (FontFamily Family, string Name)? PreviewFont(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var id = Convert.ToHexString(SHA256.HashData(bytes))[..16];
            var copy = Path.Combine(_projectRoot, "cache", "fonts", "preview", id, id + ".ttf");
            if (_fontPreviews.TryGetValue(copy, out var known)) return known;
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            if (!File.Exists(copy)) File.WriteAllBytes(copy, bytes);
            var names = new GlyphTypeface(new Uri(copy)).FamilyNames;
            var name = names.FirstOrDefault(pair => pair.Key.IetfLanguageTag == "en-us").Value ?? names.Values.First();
            var family = new FontFamily(new Uri(Path.GetDirectoryName(copy) + Path.DirectorySeparatorChar), $"./{Path.GetFileName(copy)}#{name}");
            return _fontPreviews[copy] = (family, name);
        }
        // Windows' font reader can fail in many ways on a damaged or unusual file; any failure just means no preview.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log($"Font preview for {path} failed: {exception.Message}");
            return null;
        }
    }

    private bool FontChoicesDiffer()
    {
        var applied = _gameFonts?.LoadState()?.Fonts.ToDictionary(font => font.Slot, font => font.StoredPath) ?? [];
        var choices = _fontChoices ?? [];
        return applied.Count != choices.Count ||
               choices.Any(choice => !applied.TryGetValue(choice.Key, out var path) || !path.Equals(choice.Value, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateFontActions()
    {
        var ready = !_fontsBusy && _gameFonts is not null && _fontsForeignChange is null;
        var applied = _gameFonts?.LoadState() is not null;
        FontApplyButton.IsEnabled = ready && (FontChoicesDiffer() || (_fontsUpdated && (_fontChoices?.Count ?? 0) > 0));
        FontApplyButton.Content = _fontsUpdated && !FontChoicesDiffer() ? "Apply again" : "Apply fonts";
        FontRestoreButton.IsEnabled = ready && applied && !_fontsUpdated;
        FontSlotList.IsEnabled = !_fontsBusy && _gameFonts is not null;
    }

    private void FontChooseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FontSlotRow row }) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose a font for {row.Title.ToLowerInvariant()}",
            Filter = "TrueType fonts (*.ttf)|*.ttf",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true) ChooseFont(row.Slot, dialog.FileName);
    }

    /// <summary>Chooses <paramref name="path"/> for <paramref name="slot"/> (not applied until Apply fonts), or says why it cannot be used.</summary>
    private void ChooseFont(GameFontSlot slot, string path)
    {
        var problem = GameFontService.Problem(path) ?? (PreviewFont(path) is null ? "Windows cannot read it as a font." : null);
        if (problem is not null)
        {
            FontStatusText.Text = $"{Path.GetFileName(path)} cannot be used. {problem}";
            return;
        }
        (_fontChoices ??= [])[slot] = path;
        ShowFontChoices();
        FontStatusText.Text = "Apply fonts to use your choice. It shows the next time you start Lost Ark.";
        UpdateFontActions();
    }

    private void FontResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FontSlotRow row } || _fontChoices is null) return;
        _fontChoices.Remove(row.Slot);
        ShowFontChoices();
        FontStatusText.Text = FontChoicesDiffer() ? "Apply fonts to use your choice." : "No changes to apply.";
        UpdateFontActions();
    }

    private async void FontApplyButton_Click(object sender, RoutedEventArgs e) => await ChangeFontsAsync(restore: false);

    private async void FontRestoreButton_Click(object sender, RoutedEventArgs e) => await ChangeFontsAsync(restore: true);

    private async Task ChangeFontsAsync(bool restore)
    {
        if (_fontsBusy || await GameFontsAsync() is not { } service) return;
        var choices = restore ? new Dictionary<GameFontSlot, string>() : new Dictionary<GameFontSlot, string>(_fontChoices ?? []);
        _fontsBusy = true;
        UpdateFontActions();
        string status;
        try
        {
            using var busy = ShowBusy(restore ? "Putting the game's fonts back…" : "Applying fonts…",
                "Arkive is rebuilding the game's font.lpk. Please wait until it finishes.");
            await Task.Run(() => service.ApplyAsync(choices));
            // Arkive keeps its own copy of each chosen font; the choices now point at those.
            _fontChoices = service.LoadState()?.Fonts.ToDictionary(font => font.Slot, font => font.StoredPath) ?? [];
            status = choices.Count == 0
                ? "Back to the game's own fonts. They show the next time you start Lost Ark."
                : "Your fonts are applied. They show the next time you start Lost Ark.";
            Log(choices.Count == 0 ? "Fonts put back to the game's own." : $"Fonts applied: {string.Join(", ", choices.Select(choice => $"{choice.Key}={choice.Value}"))}.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                                              InvalidOperationException)
        {
            status = exception.Message;
            Log($"Changing the fonts failed: {exception}");
        }
        finally
        {
            _fontsBusy = false;
        }
        FontStatusText.Text = status;
        await RefreshFontsAsync(status);
    }
}
