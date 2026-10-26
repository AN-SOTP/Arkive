using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using Arkive.Core.Mods;
using Arkive.Core.Tweaks;

namespace Arkive.App;

/// <summary>One thing to pick in a tweak option with choices.</summary>
public sealed class TweakChoiceRow(TweakChoice choice, bool selected)
{
    public TweakChoice Choice { get; } = choice;
    public string Label => Choice.Label;
    public bool Selected { get; set; } = selected;
}

/// <summary>One option of a tweak: a switch, or a set of choices.</summary>
public sealed class TweakOptionRow
{
    public TweakOptionRow(TweakOption option, JsonElement? saved)
    {
        Option = option;
        if (option.Choices is { } choices)
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
    public Visibility ChoicesVisibility => Option.Choices is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The value saved and written into the game: true/false, or the chosen values.</summary>
    public JsonElement ToJson() => Option.Choices is null
        ? JsonSerializer.SerializeToElement(Value)
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
        }
    }
}

public partial class MainWindow
{
    private TweakModService? _tweaks;
    private bool _tweaksBusy;

    private TweakModService Tweaks => _tweaks ??= new TweakModService(
        _arkiveModLibrary, Path.Combine(_projectRoot, "tweaks"), _protectionProvider,
        (path, token) => _arkiveModApply.ResolveCleanPackageAsync(path, token));

    private void RefreshTweaks(string? status = null)
    {
        var state = Tweaks.LoadState();
        TweakList.ItemsSource = TweakCatalog.All.Where(tweak => tweak.Available).Select(tweak =>
        {
            var saved = state.Settings.Enabled.GetValueOrDefault(tweak.Id);
            var options = tweak.OptionList.Select(option => new TweakOptionRow(option,
                saved is not null && saved.TryGetValue(option.Key, out var value) ? value : null)).ToArray();
            return new TweakRow(tweak, saved is not null, options);
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
        TweaksRemoveButton.IsEnabled = !_tweaksBusy && ModLibraryIdle && Tweaks.LoadState().ModId is not null;
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

    /// <summary>Rebuilds the Arkive Tweaks mod for these settings (none removes it), then applies mods as the Mods tab does.</summary>
    private async Task BuildTweaksAsync(TweakSettings settings)
    {
        if (_tweaksBusy || !ModLibraryIdle) return;
        _tweaksBusy = true;
        UpdateTweakActions();
        string status;
        try
        {
            await EnsureGamePackagesDirectoryAsync();
            var library = await _arkiveModLibrary.LoadAsync();
            if (library.GamePackagesDirectory is not { } packages)
            {
                RefreshTweaks("Choose the game folder first (Mods tab): Arkive needs the game's Packages folder.");
                return;
            }
            TweakBuildResult result;
            var skipIntro = settings.Enabled.TryGetValue("fastlaunch", out var launch) &&
                            (!launch.TryGetValue("skipIntro", out var skip) || skip.ValueKind == JsonValueKind.True);
            int introFiles;
            using (ShowBusy("Building tweaks…", "Arkive is adding the tweaks to the game's own windows. Please wait until it finishes."))
            {
                introFiles = await Task.Run(() => new TweakIntroMovies(Path.Combine(_projectRoot, "backups", "tweaks"), new LostArkProcessGuard())
                    .Apply(packages, skipIntro));
                result = await Task.Run(() => Tweaks.BuildAsync(settings, packages, TweakCode.LoadSwf()));
            }
            if (introFiles > 0) Log($"Tweaks: {(skipIntro ? "moved" : "restored")} {introFiles} intro movie file(s).");
            Log($"Tweaks built: tweaks={string.Join(",", settings.Enabled.Keys)}, mod={result.Mod?.ModId ?? "-"}, " +
                $"movies={string.Join(",", result.Movies)}, missing={string.Join(" | ", result.MissingHooks)}");
            status = settings.Enabled.Count == 0
                ? "Tweaks removed from the mod library. Apply below takes them out of the game."
                : $"Built Arkive Tweaks ({result.Movies.Count:N0} window(s)).";
            if (result.MissingHooks.Count > 0)
                status += $" This game version is missing {result.MissingHooks.Count:N0} place(s) some tweaks hook, so those may not work: " +
                          string.Join(", ", result.MissingHooks.Take(4)) + (result.MissingHooks.Count > 4 ? "…" : string.Empty);
            await RefreshArkiveModsAsync("tweaks built", result.Mod?.ModId);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                                              InvalidOperationException or ArkiveModRefusalException)
        {
            Log($"Building tweaks failed: {exception}");
            RefreshTweaks("The tweaks could not be built: " + exception.Message);
            return;
        }
        finally
        {
            _tweaksBusy = false;
        }
        RefreshTweaks(status);
        MainTabControl.SelectedItem = ModifiedUpksTab;
        await RunModApplyAsync(previewOnly: false);
    }
}
