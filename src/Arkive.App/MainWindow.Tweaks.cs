using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using Arkive.Core.Mods;
using Arkive.Core.Tweaks;

namespace Arkive.App;

/// <summary>One thing to pick in a tweak option with choices; <paramref name="group"/> names the set when only one may be picked.</summary>
public sealed class TweakChoiceRow(TweakChoice choice, bool selected, string? group = null)
{
    public TweakChoice Choice { get; } = choice;
    public string Label => Choice.Label;
    public bool Selected { get; set; } = selected;
    public string? Group { get; } = group;
    public Visibility CheckVisibility => Group is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RadioVisibility => Group is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>One option of a tweak: a switch, or a set of choices.</summary>
public sealed class TweakOptionRow
{
    public TweakOptionRow(TweakOption option, JsonElement? saved, string tweakId = "")
    {
        Option = option;
        if (option.Choices is { } single && option.Single)
        {
            var chosen = saved is { ValueKind: JsonValueKind.String } text && single.Any(choice => choice.Value == text.GetString())
                ? text.GetString()
                : (single.FirstOrDefault(choice => choice.Default) ?? single[0]).Value;
            Choices = single.Select(choice => new TweakChoiceRow(choice, choice.Value == chosen, tweakId + "." + option.Key)).ToArray();
        }
        else if (option.Choices is { } choices)
        {
            var chosen = saved is { ValueKind: JsonValueKind.Array } list
                ? list.EnumerateArray().Select(item => item.ToString()).ToHashSet()
                : null;
            Choices = choices.Select(choice => new TweakChoiceRow(choice, chosen?.Contains(choice.Value) ?? choice.Default)).ToArray();
        }
        else
        {
            Value = saved is { ValueKind: JsonValueKind.True or JsonValueKind.False } flag ? flag.GetBoolean() : option.Default;
        }
    }

    public TweakOption Option { get; }
    public string Label => Option.Label;
    public bool Value { get; set; }
    public IReadOnlyList<TweakChoiceRow> Choices { get; } = [];
    public Visibility SwitchVisibility => Option.Choices is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Short sets of choices show inline; long pick-lists (such as raids) fold into an expander to keep cards compact.</summary>
    private bool LongList => Option.Choices is { Count: > 6 } && !Option.Single;

    public Visibility InlineChoicesVisibility => Option.Choices is not null && !LongList ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ListVisibility => LongList ? Visibility.Visible : Visibility.Collapsed;
    public string ListHeader => $"{Label} ({Choices.Count:N0})";

    /// <summary>The value saved and written into the game: true/false, the chosen value (single), or the chosen values.</summary>
    public JsonElement ToJson() => Option.Choices is null
        ? JsonSerializer.SerializeToElement(Value)
        : Option.Single
            ? JsonSerializer.SerializeToElement((Choices.FirstOrDefault(choice => choice.Selected) ?? Choices[0]).Choice.Value)
            : JsonSerializer.SerializeToElement(Choices.Where(choice => choice.Selected).Select(choice => choice.Choice.Value).ToArray());
}

/// <summary>One tweak in the Tweaks tab.</summary>
public sealed class TweakRow(TweakDefinition tweak, bool enabled, IReadOnlyList<TweakOptionRow> options) : INotifyPropertyChanged
{
    private bool _enabled = enabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TweakDefinition Tweak { get; } = tweak;
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public string? Note => Tweak.Note;
    public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
    public IReadOnlyList<TweakOptionRow> Options { get; } = options;

    /// <summary>A tweak's options show once it is ticked, which keeps the cards small.</summary>
    public Visibility OptionsVisibility => Enabled && Options.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public System.Windows.Media.Brush CardBorder => (System.Windows.Media.Brush)Application.Current.FindResource(
        Enabled ? "AccentBrush" : "BorderStrongBrush");

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

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OptionsVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorder)));
        }
    }
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
        TweakList.ItemsSource = TweakCatalog.All.Where(tweak => tweak.Available).Select(tweak =>
        {
            // A tweak switched off in game keeps its options, ready for when it's switched on again.
            var saved = state.Settings.Enabled.GetValueOrDefault(tweak.Id) ?? state.Kept.GetValueOrDefault(tweak.Id);
            var options = tweak.OptionList.Select(option => new TweakOptionRow(option,
                saved is not null && saved.TryGetValue(option.Key, out var value) ? value : null, tweak.Id)).ToArray();
            return new TweakRow(tweak, state.Settings.Enabled.ContainsKey(tweak.Id), options);
        }).ToArray();
        var on = state.Settings.Enabled.Count;
        TweaksStatusText.Text = status ?? (on == 0
            ? "No tweaks are on. Turn on the ones you want, then apply."
            : $"{on:N0} tweak(s) saved. After changing them, apply again; a game update may need Apply tweaks too.");
        UpdateTweakActions();
    }

    private void UpdateTweakActions()
    {
        TweaksApplyButton.IsEnabled = !_tweaksBusy && ModLibraryIdle;
        TweaksRemoveButton.IsEnabled = !_tweaksBusy && ModLibraryIdle &&
                                       (Tweaks.LoadState().ModId is not null || _arkiveModLibraryManifest?.Mods.Any(TweakModService.IsTweakMod) == true);
    }

    private async void TweaksApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = new TweakSettings();
        foreach (var row in TweakList.ItemsSource?.OfType<TweakRow>() ?? [])
            if (row.Enabled)
                settings.Enabled[row.Tweak.Id] = row.Options.ToDictionary(option => option.Option.Key, option => option.ToJson());
        await BuildTweaksAsync(settings);
    }

    private async void TweaksRemoveButton_Click(object sender, RoutedEventArgs e) => await BuildTweaksAsync(new TweakSettings());

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
        var skipIntro = settings.Enabled.TryGetValue("fastlaunch", out var launch) &&
                        (!launch.TryGetValue("skipIntro", out var skip) || skip.ValueKind == JsonValueKind.True);

        TweakBuildResult built;
        ArkiveModApplyPlan plan;
        using (ShowBusy(title, "Arkive is preparing the game's interface files. Please wait until it finishes."))
        {
            var introFiles = await Task.Run(() => new TweakIntroMovies(Path.Combine(_projectRoot, "backups", "tweaks"), new LostArkProcessGuard())
                .Apply(packages, skipIntro));
            if (introFiles > 0) Log($"Tweaks: {(skipIntro ? "moved" : "restored")} {introFiles} intro movie file(s).");
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
                    removing ? "Remove tweaks" : "Install tweaks", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) !=
                MessageBoxResult.Yes)
                return "Nothing was changed in the game yet, because other mods have changes waiting. Apply tweaks again, or apply from the Mods tab.";
        }
        if (!plan.HasChanges) return removing ? "No tweaks are in the game." : "These tweaks are already in the game.";

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
        var status = $"Installed {settings.Enabled.Count:N0} tweak(s). They show the next time you start Lost Ark.";
        if (built.MissingHooks.Count > 0)
            status += $" This game version is missing {built.MissingHooks.Count:N0} place(s) some tweaks hook, so those may not work: " +
                      string.Join(", ", built.MissingHooks.Take(4)) + (built.MissingHooks.Count > 4 ? "…" : string.Empty);
        return status;
    }
}
