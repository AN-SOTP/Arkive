using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arkive.Core.Languages;
using Arkive.Core.LoginScreens;
using Arkive.Core.Mods;

namespace Arkive.App;

/// <summary>One language in the Language tab.</summary>
public sealed class LanguageRow(GameLanguage language, bool inUse)
{
    public GameLanguage Language { get; } = language;
    public bool InUse { get; } = inUse;
    public string Title => Language.Title;
    public double Coverage => Language.Coverage;
    public Visibility InUseVisibility => InUse ? Visibility.Visible : Visibility.Collapsed;

    public string Detail
    {
        get
        {
            if (Language.Name == GameLanguageService.English) return "The game's own text.";
            var detail = Language.Translated >= Language.Lines
                ? "Every line is translated."
                : $"{Language.Coverage:P0} of lines are translated; the rest stay in English.";
            return Language.InGame ? detail : detail + " The game no longer has it, so Arkive's saved copy is used.";
        }
    }
}

public partial class MainWindow
{
    private GameLanguageService? _languages;
    private bool _languageBusy;

    private async Task<GameLanguageService?> LanguagesAsync()
    {
        if (_languages is not null) return _languages;
        await EnsureGamePackagesDirectoryAsync();
        var library = await _arkiveModLibrary.LoadAsync();
        var efGame = LoginScreenService.EfGameFromPackages(library.GamePackagesDirectory ?? _libraryRootPath);
        if (efGame is null || !File.Exists(Path.Combine(efGame, "data2.lpk"))) return null;
        return _languages = new GameLanguageService(efGame, _projectRoot, new LostArkProcessGuard());
    }

    private async Task RefreshLanguagesAsync(string? status = null)
    {
        if (_languageBusy) return;
        try
        {
            if (await LanguagesAsync() is not { } service)
            {
                LanguageStatusText.Text = "Choose the game folder first (Mods tab, or index the Asset Library): Arkive needs the game's data2.lpk.";
                LanguageList.ItemsSource = null;
                LanguageRestoreButton.IsEnabled = false;
                return;
            }
            // The first time, and after each game update, the game's translations are copied so they outlive the game's.
            if (await Task.Run(service.NeedsSaving))
            {
                _languageBusy = true;
                try
                {
                    using var busy = ShowBusy("Reading the game's text…",
                        "Arkive is saving a copy of the game's translations. This takes a few seconds the first time and after game updates.");
                    await Task.Run(() => service.SaveAsync());
                    Log("Saved the game's translations.");
                }
                finally
                {
                    _languageBusy = false;
                }
            }
            var (languages, current, updated, savedAt, state) = await Task.Run(() =>
                (service.Languages(), service.CurrentLanguage(), service.GameUpdated(), service.SavedAt(), service.LoadState()));
            var rows = languages.Select(language => new LanguageRow(language, language.Name == current)).ToArray();
            var selected = (LanguageList.SelectedItem as LanguageRow)?.Language.Name ?? current;
            LanguageList.ItemsSource = rows;
            LanguageList.SelectedItem = rows.FirstOrDefault(row => row.Language.Name == selected);
            LanguageSavedText.Text = savedAt is { } saved
                ? $"Translations saved from the game on {saved.LocalDateTime:d MMMM yyyy}. Arkive keeps them, so a language the game removes can still be used."
                : "Arkive keeps a copy of every translation the game ships, so a language the game removes can still be used.";
            LanguageRestoreButton.IsEnabled = state is not null && !updated;
            LanguageStatusText.Text = status ?? (updated && state is not null
                ? $"The game updated since you chose {GameLanguageService.Title(state.Language)}, so its text is back to English. Choose it again to use it."
                : rows.Length <= 1
                    ? "The game's text has no other languages."
                    : $"In use: {GameLanguageService.Title(current)}.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                                              InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            LanguageStatusText.Text = "The game's text could not be read: " + exception.Message;
            Log($"Languages failed to load: {exception}");
        }
        UpdateLanguageActions();
    }

    private void UpdateLanguageActions() =>
        LanguageUseButton.IsEnabled = !_languageBusy && LanguageList.SelectedItem is LanguageRow { InUse: false };

    private void LanguageList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateLanguageActions();

    private async void LanguageList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && LanguageUseButton.IsEnabled) await UseLanguageAsync();
    }

    private async void LanguageUseButton_Click(object sender, RoutedEventArgs e) => await UseLanguageAsync();

    private async void LanguageRestoreButton_Click(object sender, RoutedEventArgs e) => await ChangeLanguageAsync(GameLanguageService.English);

    private async Task UseLanguageAsync()
    {
        if (LanguageList.SelectedItem is LanguageRow row) await ChangeLanguageAsync(row.Language.Name);
    }

    private async Task ChangeLanguageAsync(string language)
    {
        if (_languageBusy || await LanguagesAsync() is not { } service) return;
        _languageBusy = true;
        UpdateLanguageActions();
        LanguageRestoreButton.IsEnabled = false;
        string status;
        try
        {
            using var busy = ShowBusy("Changing the game's language…",
                "Arkive is writing the game's text into data2.lpk (about 1.4 GB). Please wait until it finishes.");
            if (language == GameLanguageService.English)
            {
                await Task.Run(() => service.RestoreAsync());
                status = "Back to the game's own English text. It shows the next time you start Lost Ark.";
                Log("Game text put back to English.");
            }
            else
            {
                var lines = await Task.Run(() => service.ApplyAsync(language));
                status = $"The game's text is now in {GameLanguageService.Title(language)} ({lines:N0} lines). It shows the next time you start Lost Ark.";
                Log($"Game text set to {language}: {lines:N0} lines.");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                                              InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            status = exception.Message;
            Log($"Changing the game text to {language} failed: {exception}");
        }
        finally
        {
            _languageBusy = false;
        }
        await RefreshLanguagesAsync(status);
    }
}
