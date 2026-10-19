using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arkive.Core.LoginScreens;
using Arkive.Core.Mods;

namespace Arkive.App;

/// <summary>One login background in the Login Screen tab.</summary>
public sealed record LoginScreenRow(LoginScreen Screen, bool InUse, string Detail)
{
    public string Title => Screen.Title;
    public Visibility InUseVisibility => InUse ? Visibility.Visible : Visibility.Collapsed;
}

public partial class MainWindow
{
    private LoginScreenService? _loginScreens;
    private bool _loginScreenBusy;

    private async Task<LoginScreenService?> LoginScreensAsync()
    {
        if (_loginScreens is not null) return _loginScreens;
        await EnsureGamePackagesDirectoryAsync();
        var library = await _arkiveModLibrary.LoadAsync();
        var efGame = LoginScreenService.EfGameFromPackages(library.GamePackagesDirectory ?? _libraryRootPath);
        if (efGame is null || !File.Exists(Path.Combine(efGame, "data1.lpk"))) return null;
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
            var (screens, current, updated) = await Task.Run(async () =>
                (service.Find(), service.CurrentMovie(), await service.GameUpdatedAsync()));
            var own = screens.FirstOrDefault(screen => screen.Movie.Equals(LoginScreenService.GameMovie, StringComparison.OrdinalIgnoreCase));
            var rows = screens.Select(screen => new LoginScreenRow(screen,
                screen.Movie.Equals(current, StringComparison.OrdinalIgnoreCase),
                screen == own
                    ? "The background this game update plays."
                    : $"From an earlier update · {screen.Movie}" +
                      (own is not null && own.Length == screen.Length ? " · looks the same as the game's own" : string.Empty)))
                .ToArray();
            var selected = (LoginScreenList.SelectedItem as LoginScreenRow)?.Screen.Movie ?? current;
            LoginScreenList.ItemsSource = rows;
            LoginScreenList.SelectedItem = rows.FirstOrDefault(row => row.Screen.Movie.Equals(selected, StringComparison.OrdinalIgnoreCase));
            var inUse = rows.FirstOrDefault(row => row.InUse);
            LoginScreenRestoreButton.IsEnabled = !current.Equals(LoginScreenService.GameMovie, StringComparison.OrdinalIgnoreCase);
            LoginScreenStatusText.Text = status ?? (rows.Length == 0
                ? "The game's Movies folder has no login backgrounds."
                : updated
                    ? "The game updated since you last chose a background, so it is back to its own. Choose one again to use it."
                    : $"In use: {inUse?.Title ?? current}. {rows.Length - 1:N0} earlier backgrounds are available.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            LoginScreenStatusText.Text = "The login backgrounds could not be read: " + exception.Message;
            Log($"Login screens failed to load: {exception}");
        }
        UpdateLoginScreenActions();
    }

    private void UpdateLoginScreenActions() =>
        LoginScreenUseButton.IsEnabled = !_loginScreenBusy && LoginScreenList.SelectedItem is LoginScreenRow { InUse: false };

    private void LoginScreenList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateLoginScreenActions();

    private async void LoginScreenList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && LoginScreenUseButton.IsEnabled) await UseLoginScreenAsync();
    }

    private async void LoginScreenUseButton_Click(object sender, RoutedEventArgs e) => await UseLoginScreenAsync();

    private async void LoginScreenRestoreButton_Click(object sender, RoutedEventArgs e) =>
        await ChangeLoginScreenAsync(LoginScreenService.GameMovie);

    private async Task UseLoginScreenAsync()
    {
        if (LoginScreenList.SelectedItem is LoginScreenRow row) await ChangeLoginScreenAsync(row.Screen.Movie);
    }

    private async Task ChangeLoginScreenAsync(string movie)
    {
        if (_loginScreenBusy || await LoginScreensAsync() is not { } service) return;
        _loginScreenBusy = true;
        UpdateLoginScreenActions();
        LoginScreenRestoreButton.IsEnabled = false;
        string status;
        try
        {
            using var busy = ShowBusy("Changing the login screen…", "Arkive is updating the game's movie table. Please wait until it finishes.");
            await Task.Run(() => movie == LoginScreenService.GameMovie ? service.RestoreAsync() : service.ApplyAsync(movie));
            var title = LoginScreenService.Title(movie);
            status = movie == LoginScreenService.GameMovie
                ? "Back to the game's own background. It shows the next time you start Lost Ark."
                : $"{title} is now the login background. It shows the next time you start Lost Ark.";
            Log($"Login screen set to {movie}.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                                              InvalidOperationException)
        {
            status = exception.Message;
            Log($"Login screen change to {movie} failed: {exception}");
        }
        finally
        {
            _loginScreenBusy = false;
        }
        await RefreshLoginScreensAsync(status);
    }
}
