using System.IO;
using System.Windows;
using System.Windows.Controls;
using Arkive.Core.Databases;
using Arkive.Core.Discovery;
using Arkive.Core.GameData;
using Arkive.Core.LoginScreens;
using Arkive.Core.Mods;
using Arkive.Core.Outfits;
using Microsoft.Win32;

namespace Arkive.App;

public partial class MainWindow
{
    private CancellationTokenSource? _databaseBuildCancellation;

    // The one load of the game database the pages share, while it runs or once it has succeeded.
    private Task<bool>? _gameDatabaseLoad;
    private bool _restoredLibrarySearchStarted;
    private bool _gameDatabaseMissingLogged;

    /// <summary>The game database launch used to load: the one last loaded, otherwise the one Build game database makes.</summary>
    private string RestoredGameMetadataPath() =>
        !string.IsNullOrWhiteSpace(_restoredSettings.GameMetadataPath) && File.Exists(_restoredSettings.GameMetadataPath)
            ? _restoredSettings.GameMetadataPath
            : Path.Combine(_projectRoot, "staging", "lpk", "data2-fixed", "arkive-asset-metadata.json");

    /// <summary>
    /// The game database (in-game names, class types, characters and NPCs, zones), loaded once, the first time a page or
    /// action needs it, instead of at launch: about 220 MB and several seconds that the Mods tab never uses. Every user
    /// of <c>_gameMetadataIndex</c> gets it through here or sits on a page that asks here when shown. False when there is
    /// no database yet or it could not be read; it is tried again the next time it is asked for.
    /// </summary>
    private async Task<bool> EnsureGameDatabaseAsync(string trigger)
    {
        if (_gameMetadataIndex is not null) return true;
        // The viewers ask from their own work; the load shows its card on the window's thread.
        if (!Dispatcher.CheckAccess()) return await Dispatcher.InvokeAsync(() => EnsureGameDatabaseAsync(trigger)).Task.Unwrap();
        var load = _gameDatabaseLoad ??= LoadGameDatabaseOnDemandAsync(trigger);
        var loaded = await load;
        if (!loaded && ReferenceEquals(_gameDatabaseLoad, load)) _gameDatabaseLoad = null;
        return loaded;
    }

    private async Task<bool> LoadGameDatabaseOnDemandAsync(string trigger)
    {
        var path = RestoredGameMetadataPath();
        using (ShowBusy("Loading the game database…",
                   "Arkive is reading the names of the game's items, characters and NPCs. This happens once, the first time a page needs them."))
        {
            // Read from the game when there is none yet, or the game has been updated since it was read.
            await RefreshGameNamesAsync(path);
            if (!File.Exists(path))
            {
                if (!_gameDatabaseMissingLogged)
                    Log($"Game database not loaded: trigger={trigger}, the file is unavailable: {path}.");
                _gameDatabaseMissingLogged = true;
                return false;
            }
            await LoadGameMetadataAsync(path, showErrors: false, loadTrigger: "on demand");
        }
        var loaded = _gameMetadataIndex is not null;
        Log($"Game database loaded on first use: trigger={trigger}, loaded={loaded}.");
        // The package workspace's views show in-game names: one read before the database is read again when next shown.
        if (loaded) MarkPackageWorkspaceStale();
        return loaded;
    }

    /// <summary>The game database Arkive builds and keeps up to date itself (one loaded from elsewhere is left as it is).</summary>
    private string OwnGameMetadataPath => Path.Combine(_projectRoot, "staging", "lpk", "data2-fixed", "arkive-asset-metadata.json");

    private async Task<string?> EfGameDirectoryAsync()
    {
        var library = await _arkiveModLibrary.LoadAsync();
        return LoginScreenService.EfGameFromPackages(library.GamePackagesDirectory ?? _libraryRootPath);
    }

    /// <summary>
    /// Reads the game's names again when the game-name database is missing, was read from an older data2.lpk (a game
    /// update), or was read by an Arkive that knew fewer tables. A few seconds; the names come from the game's own
    /// tables even while Arkive's language or table mods are in the game. A build the user started is left to finish,
    /// and failures only reach the log: the database already there keeps being used.
    /// </summary>
    private async Task RefreshGameNamesAsync(string metadataPath)
    {
        if (_databaseBuildCancellation is not null || StartupMeasurement.Current is { SaveCaches: false } ||
            !Path.GetFullPath(metadataPath).Equals(Path.GetFullPath(OwnGameMetadataPath), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (await EfGameDirectoryAsync() is not { } efGame || !File.Exists(Path.Combine(efGame, "data2.lpk"))) return;
            var archive = new GameDataArchive(efGame, _projectRoot, new LostArkProcessGuard());
            if (await Task.Run(() => GameDatabaseBuilder.IsCurrent(metadataPath, archive))) return;
            var existed = File.Exists(metadataPath);
            GameMetadataText.Text = "Game DB mapping: reading the game's names...";
            UpdateBusy(existed ? "Reading the game's new names…" : "Reading the game's names…",
                existed
                    ? "Lost Ark has been updated (or this Arkive reads more of it) since the names were last read. This takes a few seconds."
                    : "Arkive is reading the names of the game's items, characters and NPCs from the game files. This takes a few seconds.");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var language = await Task.Run(() => existed ? GameDatabaseBuilder.ReadLanguage(metadataPath) : "English");
            var result = await new GameDatabaseBuilder().BuildAsync(archive, metadataPath, language);
            Log($"Game database read again: existed={existed}, language={language}, references={result.AssociationCount}, " +
                $"entities={result.EntityCount}, seconds={watch.Elapsed.TotalSeconds:N1}, output={result.OutputPath}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Log($"The game database could not be read again; the previous one stays in use: {exception.Message}");
        }
    }

    /// <summary>
    /// Reads what the game's looks draw (the mesh, materials and effects behind each item, NPC and prop model) when
    /// data4.lpk changed since they were last read. A few seconds; failures only reach the log.
    /// </summary>
    private async Task RefreshLookNamesAsync(string metadataPath)
    {
        if (StartupMeasurement.Current is { SaveCaches: false }) return;
        try
        {
            if (await EfGameDirectoryAsync() is not { } efGame || !File.Exists(Path.Combine(efGame, "data4.lpk"))) return;
            // The game's own looks: Arkive's copy while Arkive's outfit looks are in the game.
            var archive = await Task.Run(() => new OutfitLookArchive(efGame, _projectRoot, new LostArkProcessGuard()).GameArchivePath());
            if (await Task.Run(() => GameLookSidecar.IsCurrent(metadataPath, archive))) return;
            GameMetadataText.Text = "Game DB mapping: reading what each item and model is drawn with...";
            UpdateBusy("Reading the game's looks…",
                "Arkive is reading which mesh, materials and effects each item, NPC and prop is drawn with. This takes a few seconds after a game update.");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var document = await Task.Run(() => GameLookSidecar.BuildAsync(metadataPath, archive));
            Log($"Game looks read: looks={document.Looks.Count}, drawn={document.Targets.Length}, seconds={watch.Elapsed.TotalSeconds:N1}, " +
                $"path={GameLookSidecar.GetDefaultPath(metadataPath)}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log($"The game's looks were not read: {exception.Message}");
        }
    }

    /// <summary>The main tabs that show or search by in-game names; they load the game database when first shown.</summary>
    private bool UsesGameDatabase(TabItem tab) =>
        tab == LibraryTab || tab == WorkshopTab || tab == BodyProportionsTab || tab == OutfitSwapTab ||
        tab == EntityBrowserTab || tab == AssetsTab || tab == RelationshipsTab;

    /// <summary>
    /// Loads the game database for a tab that uses it; the Asset Library then runs the search it was left with, once
    /// (launch used to run it).
    /// </summary>
    private async Task ShowGameDatabaseForTabAsync(TabItem tab)
    {
        var loaded = await EnsureGameDatabaseAsync($"{tab.Header} opened");
        if (tab != LibraryTab || _libraryRootPath is null) return;
        if (!_restoredLibrarySearchStarted)
        {
            _restoredLibrarySearchStarted = true;
            if (loaded) _restoredLibrarySearch = LoadLibraryDocumentsAsync("workspace restore");
            else if (!string.IsNullOrWhiteSpace(LibrarySearchTextBox.Text) ||
                     !string.IsNullOrWhiteSpace((LibraryKindComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()) ||
                     !string.IsNullOrWhiteSpace((LibraryArchetypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()))
                _restoredLibrarySearch = ApplyLibraryFilterAsync("workspace restore");
        }
        // Every time the Asset Library is shown: packages a game update or applied mods changed are listed again.
        _ = UpdateLibraryIndexIfStaleAsync("Asset Library opened");
    }

    private async void BuildGameDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_databaseBuildCancellation is not null || _assetLinkCancellation is not null) return;
        var gameRoot = string.IsNullOrWhiteSpace(_libraryRootPath) ? null : Directory.GetParent(_libraryRootPath)?.Parent?.FullName;
        var dialog = new OpenFileDialog
        {
            Title = "Build the game-name database from Lost Ark data2.lpk",
            Filter = "Lost Ark database archive (data2.lpk)|data2.lpk",
            CheckFileExists = true,
            FileName = "data2.lpk",
            InitialDirectory = gameRoot ?? "",
        };
        if (dialog.ShowDialog(this) != true) return;
        using var cancellation = new CancellationTokenSource();
        _databaseBuildCancellation = cancellation;
        var output = Path.Combine(_projectRoot, "staging", "lpk", "data2-fixed", "arkive-asset-metadata.json");
        var language = (GameDatabaseLanguageComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "English";
        UpdateGameAssetLinkActionState();
        GameDatabaseProgressPanel.Visibility = Visibility.Visible;
        GameDatabaseCancelButton.IsEnabled = true;
        GameDatabaseProgressBar.Value = 0;
        GameDatabaseProgressText.Text = "Reading the game archive...";
        var progress = new Progress<GameDatabaseBuildProgress>(value =>
        {
            if (!ReferenceEquals(_databaseBuildCancellation, cancellation)) return;
            GameDatabaseProgressBar.Maximum = Math.Max(1, value.Total);
            GameDatabaseProgressBar.Value = value.Completed;
            GameDatabaseProgressText.Text = $"{value.Stage}: {value.Completed}/{value.Total} — {value.CurrentFile}";
        });
        try
        {
            Log($"Game database build started: archive={dialog.FileName}, language={language}.");
            // The game's own data2.lpk is read through Arkive's copies of any table Arkive has changed in it, so the
            // names are the game's; any other file is read as it is.
            var efGame = await EfGameDirectoryAsync();
            var gameArchive = efGame is not null &&
                              Path.GetFullPath(dialog.FileName).Equals(Path.Combine(efGame, "data2.lpk"), StringComparison.OrdinalIgnoreCase)
                ? new GameDataArchive(efGame, _projectRoot, new LostArkProcessGuard())
                : null;
            var result = gameArchive is null
                ? await new GameDatabaseBuilder().BuildAsync(dialog.FileName, output, language, progress, cancellation.Token)
                : await new GameDatabaseBuilder().BuildAsync(gameArchive, output, language, progress, cancellation.Token);
            await LoadGameMetadataAsync(result.OutputPath, showErrors: true, loadTrigger: "game database build");
            StatusText.Text = $"Game database ready: {result.AssociationCount:N0} native references from {result.DatabaseCount:N0} tables.";
            Log($"Game database build completed: references={result.AssociationCount}, entities={result.EntityCount}, customizations={result.CustomizationCount}, output={result.OutputPath}.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText.Text = "Game database build cancelled; the previous mapping was preserved.";
            Log("Game database build cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            StatusText.Text = "Game database build failed; the previous mapping was preserved.";
            Log($"Game database build failed: {exception}");
            MessageBox.Show(this, exception.Message, "Arkive game database", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _databaseBuildCancellation = null;
            GameDatabaseProgressPanel.Visibility = Visibility.Collapsed;
            UpdateGameAssetLinkActionState();
        }
    }

    private void GameDatabaseCancelButton_Click(object sender, RoutedEventArgs e)
    {
        GameDatabaseCancelButton.IsEnabled = false;
        _databaseBuildCancellation?.Cancel();
        GameDatabaseProgressText.Text = "Cancelling database build...";
    }
}
