using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arkive.Core.LoginScreens;
using Arkive.Core.Mods;

namespace Arkive.App;

/// <summary>One login screen in the Login Screen tab, with its picture once it is decoded.</summary>
public sealed class LoginScreenRow(LoginScreen screen, bool inUse, bool live, string detail) : INotifyPropertyChanged
{
    private ImageSource? _preview;
    private string _previewNote = "Loading picture…";

    public event PropertyChangedEventHandler? PropertyChanged;

    public LoginScreen Screen { get; } = screen;
    public bool InUse { get; } = inUse;
    public bool Live { get; } = live;
    public string Title => Screen.Title;
    public string Detail { get; } = detail;
    public Visibility InUseVisibility => InUse ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LiveVisibility => Live ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? Preview
    {
        get => _preview;
        set
        {
            _preview = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }
    }

    public string PreviewNote
    {
        get => _previewNote;
        set
        {
            _previewNote = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewNote)));
        }
    }
}

public partial class MainWindow
{
    private LoginScreenService? _loginScreens;
    private LoginScreenPreviews? _loginScreenPreviews;
    private bool _loginScreenBusy;
    private bool _loginScreenLiveUpdating;
    private int _loginScreenPreviewPass;

    private async Task<LoginScreenService?> LoginScreensAsync()
    {
        if (_loginScreens is not null) return _loginScreens;
        await EnsureGamePackagesDirectoryAsync();
        var library = await _arkiveModLibrary.LoadAsync();
        var efGame = LoginScreenService.EfGameFromPackages(library.GamePackagesDirectory ?? _libraryRootPath);
        if (efGame is null || !Directory.Exists(Path.Combine(efGame, "Movies"))) return null;
        _loginScreenPreviews = new LoginScreenPreviews(efGame, _projectRoot);
        return _loginScreens = new LoginScreenService(efGame, _projectRoot, new LostArkProcessGuard());
    }

    private async Task RefreshLoginScreensAsync(string? status = null)
    {
        if (_loginScreenBusy) return;
        try
        {
            if (await LoginScreensAsync() is not { } service)
            {
                LoginScreenStatusText.Text = "Choose the game folder first (Mods tab, or index the Asset Library): Arkive needs the game's EFGame folder.";
                LoginScreenList.ItemsSource = null;
                return;
            }
            var (screens, live, current, updated) = await Task.Run(async () =>
                (service.Find(), service.LiveMovie(), await service.CurrentMovieAsync(), await service.GameUpdatedAsync()));
            var rows = screens
                .Where(screen => screen.SameAs is null || screen.Movie.Equals(live, StringComparison.OrdinalIgnoreCase))
                .Select(screen =>
                {
                    var isLive = screen.Movie.Equals(live, StringComparison.OrdinalIgnoreCase);
                    var detail = !screen.CanUse
                        ? "Arkive cannot read this video yet, so it cannot be used."
                        : isLive
                            ? "The screen this game update shows."
                            : "From an earlier update, with its own music and ambience.";
                    return new LoginScreenRow(screen, screen.Movie.Equals(current, StringComparison.OrdinalIgnoreCase), isLive, detail);
                })
                .ToArray();
            var selected = (LoginScreenList.SelectedItem as LoginScreenRow)?.Screen.Movie ?? current;
            LoginScreenList.ItemsSource = rows;
            LoginScreenList.SelectedItem = rows.FirstOrDefault(row => row.Screen.Movie.Equals(selected, StringComparison.OrdinalIgnoreCase));

            _loginScreenLiveUpdating = true;
            var choices = screens.Where(screen => screen.CanUse)
                .Select(screen => new ComboBoxItem { Content = screen.Title, Tag = screen }).ToArray();
            LoginScreenLiveBox.ItemsSource = choices;
            LoginScreenLiveBox.SelectedItem = choices.FirstOrDefault(item => ((LoginScreen)item.Tag).Movie.Equals(live, StringComparison.OrdinalIgnoreCase));
            _loginScreenLiveUpdating = false;

            var inUse = rows.FirstOrDefault(row => row.InUse);
            LoginScreenRestoreButton.IsEnabled = service.LoadState() is not null && !updated;
            LoginScreenStatusText.Text = status ?? (rows.Length == 0
                ? "The game's Movies folder has no login screens."
                : updated
                    ? "The game updated since you last chose a screen, so it is back to its own. Choose one again to use it."
                    : $"In use: {inUse?.Title ?? LoginScreenService.Title(current)}. {rows.Count(row => row.Screen.CanUse && !row.Live):N0} other screens are available.");
            _ = LoadLoginScreenPreviewsAsync(service, rows);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            LoginScreenStatusText.Text = "The login screens could not be read: " + exception.Message;
            Log($"Login screens failed to load: {exception}");
        }
        UpdateLoginScreenActions();
    }

    // Pictures come one by one (the first time each takes a second or two; then they are cached).
    private async Task LoadLoginScreenPreviewsAsync(LoginScreenService service, IReadOnlyList<LoginScreenRow> rows)
    {
        var pass = ++_loginScreenPreviewPass;
        if (_loginScreenPreviews is not { } previews) return;
        if (!previews.Available)
        {
            foreach (var row in rows) row.PreviewNote = "No picture (the game's video library was not found)";
            return;
        }
        foreach (var row in rows)
        {
            if (pass != _loginScreenPreviewPass) return;
            if (!row.Screen.CanUse)
            {
                row.PreviewNote = "No picture";
                continue;
            }
            try
            {
                var source = service.VideoSource(row.Screen);
                var path = await previews.GetAsync(row.Screen.Movie, source);
                if (path is null)
                {
                    row.PreviewNote = "No picture";
                    continue;
                }
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(Path.GetFullPath(path));
                image.EndInit();
                image.Freeze();
                row.Preview = image;
                row.PreviewNote = string.Empty;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            {
                row.PreviewNote = "No picture";
                Log($"Login screen picture for {row.Screen.Movie} failed: {exception.Message}");
            }
        }
    }

    private void UpdateLoginScreenActions() =>
        LoginScreenUseButton.IsEnabled = !_loginScreenBusy &&
                                         LoginScreenList.SelectedItem is LoginScreenRow { InUse: false, Screen.CanUse: true };

    private void LoginScreenList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateLoginScreenActions();

    private async void LoginScreenList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && LoginScreenUseButton.IsEnabled) await UseLoginScreenAsync();
    }

    private async void LoginScreenUseButton_Click(object sender, RoutedEventArgs e) => await UseLoginScreenAsync();

    private async void LoginScreenRestoreButton_Click(object sender, RoutedEventArgs e) => await ChangeLoginScreenAsync(null);

    private async Task UseLoginScreenAsync()
    {
        if (LoginScreenList.SelectedItem is LoginScreenRow row) await ChangeLoginScreenAsync(row.Screen);
    }

    private async void LoginScreenLiveBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loginScreenLiveUpdating || _loginScreenBusy || LoginScreenLiveBox.SelectedItem is not ComboBoxItem { Tag: LoginScreen screen } ||
            await LoginScreensAsync() is not { } service || screen.Movie == service.LiveMovie()) return;
        _loginScreenBusy = true;
        string status;
        try
        {
            using var busy = ShowBusy("Changing the login screen…", "Arkive is putting the game's own screen back first. Please wait until it finishes.");
            await Task.Run(() => service.SetLiveMovieAsync(screen.Movie));
            status = $"Arkive now puts your choice in place of {screen.Title}. Choose the screen you want to use.";
            Log($"Login screen the game shows set to {screen.Movie}.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                                              InvalidOperationException)
        {
            status = exception.Message;
            Log($"Setting the game's login screen to {screen.Movie} failed: {exception}");
        }
        finally
        {
            _loginScreenBusy = false;
        }
        await RefreshLoginScreensAsync(status);
    }

    /// <summary>Uses <paramref name="screen"/>, or puts the game's own back when it is null.</summary>
    private async Task ChangeLoginScreenAsync(LoginScreen? screen)
    {
        if (_loginScreenBusy || await LoginScreensAsync() is not { } service) return;
        _loginScreenBusy = true;
        UpdateLoginScreenActions();
        LoginScreenRestoreButton.IsEnabled = false;
        string status;
        try
        {
            using var busy = ShowBusy("Changing the login screen…", "Arkive is writing the screen's video and linking its sounds. Please wait until it finishes.");
            if (screen is null || screen.Movie.Equals(service.LiveMovie(), StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() => service.RestoreAsync());
                status = "Back to the game's own screen. It shows the next time you start Lost Ark.";
                Log("Login screen put back to the game's own.");
            }
            else
            {
                var note = await Task.Run(() => service.ApplyAsync(screen.Movie));
                status = $"{screen.Title} is now the login screen, with its music and ambience. It shows the next time you start Lost Ark." +
                         (note is null ? string.Empty : " " + note);
                Log($"Login screen set to {screen.Movie}.{(note is null ? string.Empty : " " + note)}");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                                              InvalidOperationException)
        {
            status = exception.Message;
            Log($"Login screen change to {screen?.Movie ?? "the game's own"} failed: {exception}");
        }
        finally
        {
            _loginScreenBusy = false;
        }
        await RefreshLoginScreensAsync(status);
    }
}
