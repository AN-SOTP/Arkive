using System.IO;
using System.Windows;
using Arkive.Core.Mods;
using Arkive.Core.Tweaks;

namespace Arkive.App;

/// <summary>
/// One tweak in the Tweaks tab's list: what it does, and whether it acts in game (<paramref name="on"/> is null while no
/// tweaks are installed).
/// </summary>
public sealed class TweakRow(TweakDefinition tweak, bool? on)
{
    public TweakDefinition Tweak { get; } = tweak;
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public string? Note => Tweak.Note;
    public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
    public bool? On { get; } = on;
    public string StateLabel => On == true ? "Enabled" : "Disabled";
    public string StateDetail => On == true
        ? "Installed and enabled. Disable it in game from Arkive Tweaks in the escape menu."
        : "Installed and disabled. Enable it in game from Arkive Tweaks in the escape menu.";
    public Visibility StateVisibility => On is null ? Visibility.Collapsed : Visibility.Visible;

    public System.Windows.Media.Brush StateBackground => (System.Windows.Media.Brush)Application.Current.FindResource(
        On == true ? "SuccessSurfaceBrush" : "SurfaceAltBrush");

    public System.Windows.Media.Brush StateForeground => (System.Windows.Media.Brush)Application.Current.FindResource(
        On == true ? "SuccessTextBrush" : "TextSecondaryBrush");

    public string RiskLabel => Tweak.Risk switch
    {
        TweakRisk.Speed => "Speed",
        TweakRisk.Batch => "Batch",
        TweakRisk.Automation => "Automation",
        _ => "Display",
    };

    public string RiskDetail => Tweak.Risk switch
    {
        TweakRisk.Speed => "Skips waits or animations.",
        TweakRisk.Batch => "Sends several game actions from one click.",
        TweakRisk.Automation => "Acts without a click, or turns a key into clicks.",
        _ => "Only changes what you see.",
    };
}

public partial class MainWindow
{
    private TweakModService? _tweaks;
    private bool _tweaksBusy;

    private TweakModService Tweaks => _tweaks ??= new TweakModService(
        _arkiveModLibrary, Path.Combine(_projectRoot, "tweaks"), _protectionProvider,
        (path, token) => _arkiveModApply.ResolveCleanPackageAsync(path, token))
    {
        Edits = _arkiveModEdits,
    };

    private void RefreshTweaks(string? status = null)
    {
        var state = Tweaks.LoadState();
        var installed = state.ModId is not null;
        var rows = TweakCatalog.All.Where(tweak => tweak.Available && !tweak.RestartOnly)
            .Select(tweak => new TweakRow(tweak, installed ? state.Settings.Enabled.ContainsKey(tweak.Id) : null))
            .ToArray();
        TweakList.ItemsSource = rows;
        TweaksEscapeMenuImage.Source ??= EscapeMenuPicture;
        TweakListHeader.Text = $"All Tweaks ({rows.Length:N0})";
        var on = rows.Count(row => row.On == true);
        TweaksStatusText.Text = status ?? (!installed
            ? "No tweaks are installed."
            : $"All tweaks are installed; {on:N0} of {rows.Length:N0} are enabled. Enable, disable and change them in game.");
        var launch = FasterLaunchInstalled;
        FasterLaunchStateText.Text = launch ? "Installed" : "Not Installed";
        FasterLaunchState.Background = (System.Windows.Media.Brush)FindResource(launch ? "SuccessSurfaceBrush" : "SurfaceAltBrush");
        FasterLaunchStateText.Foreground = (System.Windows.Media.Brush)FindResource(launch ? "SuccessTextBrush" : "TextSecondaryBrush");
        TweaksFasterLaunchButton.Content = launch ? "Remove Faster Launch" : "Install Faster Launch";
        UpdateTweakActions();
    }

    private TweakIntroMovies IntroMovies => new(Path.Combine(_projectRoot, "backups", "tweaks"), new LostArkProcessGuard());

    /// <summary>Faster Launch is installed while Arkive holds the intro movies in its backups.</summary>
    private bool FasterLaunchInstalled => IntroMovies.Skipping;

    /// <summary>
    /// Installs or removes Faster Launch on its own: it moves the game's intro movies rather than changing interface code,
    /// so it can't be switched in game and isn't part of Install All Tweaks.
    /// </summary>
    private async void TweaksFasterLaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tweaksBusy || !ModLibraryIdle) return;
        if (new LostArkProcessGuard().FindBlockingProcesses() is { Count: > 0 } running)
        {
            RefreshTweaks($"Close {string.Join(", ", running)} first: Faster Launch moves files the game is using.");
            return;
        }
        var install = !FasterLaunchInstalled;
        _tweaksBusy = true;
        UpdateTweakActions();
        string status;
        try
        {
            await EnsureGamePackagesDirectoryAsync();
            if ((await _arkiveModLibrary.LoadAsync()).GamePackagesDirectory is not { } packages)
                status = "Choose the game folder first (Mods tab): Arkive needs the game's Packages folder.";
            else
            {
                int moved;
                using (ShowBusy(install ? "Installing Faster Launch…" : "Removing Faster Launch…",
                           "Arkive is moving the game's intro movies. Please wait until it finishes."))
                    moved = await Task.Run(() => IntroMovies.Apply(packages, install));
                Log($"Faster Launch: {(install ? "moved" : "restored")} {moved} intro movie file(s).");
                status = install
                    ? "Faster Launch is installed: Lost Ark skips the publisher logos and the seizure warning from the next start."
                    : "Faster Launch is removed: the intro plays again from the next start.";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"Faster Launch could not be changed: {exception}");
            status = "Faster Launch could not be changed: " + exception.Message;
        }
        finally
        {
            _tweaksBusy = false;
        }
        RefreshTweaks(status);
    }

    private void UpdateTweakActions()
    {
        TweaksInstallAllButton.IsEnabled = !_tweaksBusy && ModLibraryIdle;
        TweaksFasterLaunchButton.IsEnabled = !_tweaksBusy && ModLibraryIdle;
        TweaksRemoveAllButton.IsEnabled = !_tweaksBusy && ModLibraryIdle &&
                                           (Tweaks.LoadState().ModId is not null || _arkiveModLibraryManifest?.Mods.Any(TweakModService.IsTweakMod) == true);
    }

    private async void TweaksInstallAllButton_Click(object sender, RoutedEventArgs e) => await BuildTweaksAsync(Tweaks.EveryTweak());

    private async void TweaksRemoveAllButton_Click(object sender, RoutedEventArgs e) => await BuildTweaksAsync(new TweakSettings());

    private static System.Windows.Media.Imaging.BitmapImage? _escapeMenuPicture;

    /// <summary>
    /// The escape menu with the Arkive Tweaks button marked, built from the game's own interface movie and art with
    /// placeholder names (see eng/tweaks/escape-menu).
    /// </summary>
    private static System.Windows.Media.Imaging.BitmapImage EscapeMenuPicture
    {
        get
        {
            if (_escapeMenuPicture is null)
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.UriSource = new Uri("pack://application:,,,/Arkive.App;component/Assets/Tweaks/escape-menu.jpg", UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                _escapeMenuPicture = image;
            }
            return _escapeMenuPicture;
        }
    }

    /// <summary>The escape menu picture at full size, for reading its labels.</summary>
    private void TweaksEscapeMenuPicture_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var picture = EscapeMenuPicture;
        new Window
        {
            Owner = this,
            Title = "Arkive Tweaks In The Escape Menu",
            Width = Math.Min(picture.PixelWidth + 40, SystemParameters.WorkArea.Width - 40),
            Height = Math.Min(picture.PixelHeight + 60, SystemParameters.WorkArea.Height - 40),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.Black,
            Content = new System.Windows.Controls.ScrollViewer
            {
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Content = new System.Windows.Controls.Image { Source = picture, Stretch = System.Windows.Media.Stretch.None },
            },
        }.Show();
    }

    /// <summary>
    /// Installs exactly these tweaks (none takes them all out), like the Login Screen and Fonts tabs: Arkive rebuilds its
    /// own tweak mod, which the Mods list doesn't show, and applies the game packages it touches straight away. The mod
    /// system still does the work, so backups, restores and other mods' changes to the same interface packages stay
    /// correct. Other mods' waiting changes are only applied after the player agrees.
    /// </summary>
    private async Task BuildTweaksAsync(TweakSettings settings)
    {
        if (_tweaksBusy || !ModLibraryIdle) return;
        if (new LostArkProcessGuard().FindBlockingProcesses() is { Count: > 0 } running)
        {
            // With In-game Tweak Settings installed, switches and options reach the running game through live.json.
            RefreshTweaks(Tweaks.TrySaveLive(settings)
                ? "Saved. Lost Ark picks the change up within a few seconds; a window that is already open may need reopening."
                : $"Close {string.Join(", ", running)} first, then try again.");
            return;
        }
        _tweaksBusy = true;
        _modLibraryBusy = true;
        UpdateTweakActions();
        string status;
        try
        {
            status = await InstallTweaksAsync(settings);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                                              InvalidOperationException or ArkiveModRefusalException)
        {
            Log($"Installing tweaks failed: {exception}");
            status = "The tweaks could not be installed: " + exception.Message;
        }
        finally
        {
            _tweaksBusy = false;
            _modLibraryBusy = false;
        }
        await RefreshArkiveModsAsync("tweaks installed");
        RefreshTweaks(status);
    }

    private async Task<string> InstallTweaksAsync(TweakSettings settings)
    {
        await EnsureGamePackagesDirectoryAsync();
        var library = await _arkiveModLibrary.LoadAsync();
        if (library.GamePackagesDirectory is not { } packages)
            return "Choose the game folder first (Mods tab): Arkive needs the game's Packages folder.";
        var removing = settings.Enabled.Count == 0;
        var title = removing ? "Removing tweaks…" : "Installing tweaks…";

        TweakBuildResult built;
        ArkiveModApplyPlan plan;
        using (ShowBusy(title, "Arkive is preparing the game's interface files. Please wait until it finishes."))
        {
            built = await Task.Run(() => Tweaks.BuildAsync(settings, packages, TweakCode.LoadSwf()));
            Log($"Tweaks built: tweaks={string.Join(",", settings.Enabled.Keys)}, mod={built.Mod?.ModId ?? "-"}, " +
                $"movies={string.Join(",", built.Movies)}, packages={string.Join(",", built.Packages)}, " +
                $"missing={string.Join(" | ", built.MissingHooks)}");
            plan = await _arkiveModApply.PlanAsync();
        }
        if (!plan.CanApply) return "The tweaks are ready but couldn't be installed: " + plan.Summary;

        var others = plan.Packages.Where(package => package.Action != ArkiveModPackageAction.None &&
                                                    !built.Packages.Contains(package.PhysicalPackageName)).ToArray();
        if (others.Length > 0)
        {
            var names = string.Join("\n", others.Take(8).Select(package => $"  • {package.PackageAlias}: {package.Describe}" +
                (package.Contributors.Count == 0 ? string.Empty
                    : " — " + string.Join(", ", package.Contributors.Select(item => item.ModName).Distinct()))));
            var more = others.Length > 8 ? $"\n  • and {others.Length - 8:N0} more" : string.Empty;
            if (MessageBox.Show(this,
                    $"Other mods have changes waiting too:\n\n{names}{more}\n\nInstall them together with the tweaks? " +
                    "No leaves the game as it is; the tweaks go in the next time you apply.",
                    removing ? "Remove All Tweaks" : "Install All Tweaks", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) !=
                MessageBoxResult.Yes)
                return "Nothing was changed in the game yet, because other mods have changes waiting. Click the button again, or apply from the Mods tab.";
        }
        if (!plan.HasChanges) return removing ? "No tweaks are in the game." : "All tweaks are already in the game.";

        ArkiveModApplyResult result;
        using (ShowBusy(title, "Arkive is writing the game's interface files. Please wait until it finishes."))
        {
            var progress = new Progress<ArkiveModApplyProgress>(update =>
            {
                UpdateBusy(title, $"Step {update.Step:N0} of {update.TotalSteps:N0}: {update.Message}");
                Log($"Tweaks apply progress: step={update.Step}/{update.TotalSteps}, package={update.Package}, message={update.Message}");
            });
            result = await _arkiveModApply.ApplyAsync(plan.Fingerprint, progress);
        }
        foreach (var package in result.Packages)
            Log($"Tweaks apply package result: package={package.PhysicalPackageName}, action={package.Action}, " +
                $"succeeded={package.Succeeded}, message={package.Message}");
        if (!result.Succeeded)
            return "Some interface files could not be changed: " +
                   string.Join(" ", result.Packages.Where(item => !item.Succeeded).Take(2).Select(item => item.Message));
        if (removing) return "Tweaks removed. The game's own windows are back as they were.";
        var status = "All tweaks are installed. Start Lost Ark, press Esc and click Arkive Tweaks to enable the ones you want.";
        if (built.MissingHooks.Count > 0)
            status += $" This game version is missing {built.MissingHooks.Count:N0} place(s) some tweaks hook, so those may not work: " +
                      string.Join(", ", built.MissingHooks.Take(4)) + (built.MissingHooks.Count > 4 ? "…" : string.Empty);
        return status;
    }
}
