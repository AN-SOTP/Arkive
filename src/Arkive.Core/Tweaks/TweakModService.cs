using System.Security.Cryptography;
using System.Text.Json;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Interface;
using Arkive.Core.Mods;
using Arkive.Core.Textures;

namespace Arkive.Core.Tweaks;

/// <summary>
/// What a tweak build made: the mod, the hooks this game version no longer has (their tweaks may not work), and every
/// game package (physical file name) the new or the replaced tweak mods touch, which is what installing must change.
/// </summary>
public sealed record TweakBuildResult(
    ArkiveModLibraryEntry? Mod,
    IReadOnlyList<string> MissingHooks,
    IReadOnlyList<string> Movies,
    IReadOnlySet<string> Packages);

/// <summary>
/// Saved choices: which tweaks are on, their options, and the library mod that holds them. <see cref="Kept"/> keeps the
/// options of tweaks switched off in game, so switching one back on brings them back; <see cref="Built"/> lists the
/// tweaks the installed mod has hooks for.
/// </summary>
public sealed class TweakState
{
    public TweakSettings Settings { get; set; } = new();
    public string? ModId { get; set; }
    public Dictionary<string, Dictionary<string, JsonElement>> Kept { get; set; } = [];
    public List<string> Built { get; set; } = [];

    /// <summary>The tweaks that were on when Remove All Tweaks took them out, switched on again by Install All Tweaks.</summary>
    public List<string> OnBeforeRemove { get; set; } = [];
}

/// <summary>
/// Turns the chosen tweaks into one ordinary Arkive mod ("Arkive Tweaks") of interface movies, so they install, merge
/// with other mods, restore and survive game updates through the normal apply. Every build starts from the game's own
/// movies (through <c>cleanPackage</c>), never from a movie that already holds tweak code.
/// </summary>
public sealed class TweakModService(
    ArkiveModLibrary library,
    string root,
    IChunkProtectionProvider protectionProvider,
    Func<string, CancellationToken, Task<string>>? cleanPackage = null)
{
    public const string ModName = "Arkive Tweaks";
    public const string Tag = "tweaks";

    /// <summary>The display name every tweak movie edit starts with ("Tweaks: efui_tophud tophud").</summary>
    public const string DraftPrefix = "Tweaks: ";

    /// <summary>
    /// A mod the Tweaks tab owns: it builds, installs and removes these itself, so the Mods list doesn't show them.
    /// Test builds of the tweaks made with the CLI count too, so the tab cleans them up.
    /// </summary>
    public static bool IsTweakMod(ArkiveModLibraryEntry mod) =>
        mod.Manifest.Name == ModName && mod.Manifest.Tags.Contains(Tag, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> PackagesOf(ArkiveModLibraryEntry mod) =>
        mod.Manifest.Assets.Select(asset => ArkiveModFileService.NormalizePhysicalPackageName(asset.Target.PhysicalPackageName));
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Developer builds: every hook catches errors and passes them to the tweak code's Diag.note.</summary>
    public bool Diagnostic { get; init; }

    /// <summary>When set, each built movie is also written here (for decompiling while developing tweaks).</summary>
    public string? KeepMoviesIn { get; init; }

    /// <summary>The game's data2.lpk, for tweaks that read game tables. When unset, the one two folders above the packages.</summary>
    public string? GameDataFile { get; init; }

    /// <summary>
    /// The settings the tweak code gets (Options.hx): every built tweak's options ("tweaks"), the ones that act ("on"),
    /// Arkive's tweak folder ("home"), plus the folder the Logs tab reads its answers from and the game's Combat Power
    /// tables (left out when there is no data2.lpk, so that tweak shows nothing). With In-game Tweak Settings built,
    /// "live" tells the code to follow live.json and "panel" lists what the escape menu panel shows.
    /// </summary>
    private string ForGame(TweakState state, IReadOnlyList<TweakDefinition> built, string packagesDirectory)
    {
        var settings = state.Settings;
        var tweaks = built.ToDictionary(tweak => tweak.Id, tweak => OptionsOf(state, tweak));
        if (tweaks.TryGetValue(PlayerLogsTweak, out var logs))
            logs["folder"] = JsonSerializer.SerializeToElement(PlayerLogsFolder.Replace('\\', '/'));
        var data2 = GameDataFile ?? Path.GetFullPath(Path.Combine(packagesDirectory, "..", "..", "data2.lpk"));
        // Combat Power per Gold, the bracelet judge and Compare With Me price with the Combat Power tables. They go in once:
        // under Combat Power's id (baked, not switched on) when it, the judge or Compare is built, else under Combat Power
        // per Gold's.
        var pricedBy = tweaks.ContainsKey(CombatPowerTweak) || tweaks.ContainsKey(BraceletJudgeTweak) || tweaks.ContainsKey(CompareTweak)
            ? CombatPowerTweak
            : tweaks.ContainsKey(CombatPowerPerGoldTweak) ? CombatPowerPerGoldTweak : null;
        if (pricedBy is not null && File.Exists(data2))
        {
            if (!tweaks.TryGetValue(pricedBy, out var power))
                tweaks[pricedBy] = power = [];
            foreach (var (key, value) in CombatPowerTable.Options(data2, Path.Combine(root, "cache")))
                power[key] = value;
        }
        if (tweaks.TryGetValue(AstrogemTweak, out var astrogem) && File.Exists(data2))
            astrogem["tables"] = JsonSerializer.SerializeToElement(
                AstrogemOdds.WriteTables(data2, Path.Combine(root, "cache"), Path.Combine(root, "astrogem"))
                    .ToDictionary(table => table.Key, table => table.Value.Replace('\\', '/')));
        if (tweaks.ContainsKey(MarketPriceTweak) && File.Exists(data2))
            MarketItemTable.Write(data2, PricesFolder, Path.Combine(root, "cache"));
        var live = settings.Enabled.ContainsKey(SettingsTweak) && tweaks.ContainsKey(SettingsTweak);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tweaks"] = tweaks,
            ["on"] = settings.Enabled.Keys.Where(tweaks.ContainsKey).ToDictionary(id => id, _ => true),
            ["home"] = Path.GetFullPath(root).Replace('\\', '/'),
            ["live"] = live,
            ["panel"] = live ? Panel(built) : null,
        }, GameJsonOptions);
    }

    /// <summary>Plain text for the game's JSON reader: only quotes, backslashes and control characters are escaped.</summary>
    private static readonly JsonSerializerOptions GameJsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The tweak this build has In-game Tweak Settings for, and the one that installs every tweak.</summary>
    public const string SettingsTweak = "settings";

    private const string EverythingOption = "everything";

    /// <summary>The tweaks a build hooks: every interface tweak with In-game Tweak Settings' "everything" on, else the ones on.</summary>
    public static IReadOnlyList<TweakDefinition> BuiltTweaks(TweakSettings settings)
    {
        var everything = settings.Enabled.TryGetValue(SettingsTweak, out var panel) &&
                         (!panel.TryGetValue(EverythingOption, out var all) || all.ValueKind != JsonValueKind.False);
        return [.. TweakCatalog.All.Where(tweak => tweak.Available && !tweak.RestartOnly && (everything || settings.Enabled.ContainsKey(tweak.Id)))];
    }

    /// <summary>
    /// What the Tweaks tab's Install All Tweaks installs: In-game Tweak Settings set to build every tweak, and switched on
    /// only the tweaks the player has on (or had on before Remove All Tweaks), each with its saved options. A first
    /// install has every tweak off; the player switches them on in game. Faster Launch isn't part of it (see
    /// <see cref="TweakIntroMovies"/>): it changes game files, so the tab installs it on its own.
    /// </summary>
    public TweakSettings EveryTweak()
    {
        var state = LoadState();
        var on = state.Settings.Enabled.Keys.Concat(state.OnBeforeRemove).Append(SettingsTweak).ToHashSet();
        var settings = new TweakSettings();
        foreach (var tweak in TweakCatalog.All.Where(tweak => tweak.Available && !tweak.RestartOnly && on.Contains(tweak.Id)))
            settings.Enabled[tweak.Id] = OptionsOf(state, tweak);
        settings.Enabled[SettingsTweak][EverythingOption] = JsonSerializer.SerializeToElement(true);
        return settings;
    }

    /// <summary>A tweak's options: as saved (or kept while it is switched off), with defaults for the ones never saved.</summary>
    private static Dictionary<string, JsonElement> OptionsOf(TweakState state, TweakDefinition tweak)
    {
        var saved = state.Settings.Enabled.GetValueOrDefault(tweak.Id) ?? state.Kept.GetValueOrDefault(tweak.Id);
        var options = saved is null ? [] : new Dictionary<string, JsonElement>(saved);
        foreach (var option in tweak.OptionList)
            options.TryAdd(option.Key, option.DefaultValue);
        return options;
    }

    /// <summary>What the escape menu panel lists: the built tweaks, not itself.</summary>
    private static object[] Panel(IReadOnlyList<TweakDefinition> built) =>
    [
        .. TweakCatalog.All
            .Where(tweak => tweak.Id != SettingsTweak && built.Contains(tweak))
            .Select(tweak => new
            {
                id = tweak.Id,
                name = tweak.Name,
                options = tweak.OptionList.Select(option => new
                {
                    key = option.Key,
                    label = option.Label,
                    kind = option.Kind,
                    choices = (option.Choices ?? []).Select(choice => new { value = choice.Value, label = choice.Label }).ToArray(),
                }).ToArray(),
            }),
    ];

    /// <summary>The file the game follows for choices saved since the build (see Options.hx).</summary>
    public string LivePath => Path.Combine(root, "live.json");

    /// <summary>The Data Bus's signal files (see <see cref="TweakBus"/>).</summary>
    public string BusFolder => Path.Combine(root, "bus");

    /// <summary>
    /// Writes live.json: which tweaks act and every interface tweak's options, and the request id of the in-game change
    /// it answers ("ack"), which the panel waits for to show the change as saved.
    /// </summary>
    public void WriteLive(TweakState state, string ack = "")
    {
        var tweaks = TweakCatalog.All.Where(tweak => tweak.Available && !tweak.RestartOnly).ToArray();
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["on"] = tweaks.Where(tweak => state.Settings.Enabled.ContainsKey(tweak.Id)).ToDictionary(tweak => tweak.Id, _ => true),
            ["tweaks"] = tweaks.ToDictionary(tweak => tweak.Id, tweak => OptionsOf(state, tweak)),
            ["ack"] = ack,
        }, GameJsonOptions);
        Directory.CreateDirectory(root);
        var temporary = LivePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, LivePath, overwrite: true);
    }

    /// <summary>The Data Bus channel the escape menu panel sends its changes on.</summary>
    public const string SettingsChannel = "settings";

    /// <summary>The Data Bus channel Auto Pull Up reports on: "on", "off", "pulled\twhere", "stop\treason\twhere".</summary>
    public const string AutoPullUpChannel = "autopullup";

    /// <summary>A log line for an Auto Pull Up report, or null when the text isn't one.</summary>
    public static string? DescribeAutoPullUp(string text)
    {
        var parts = text.Split('\t');
        static string Window(string where) => where switch
        {
            "open" => "Find Party window open",
            "closed" => "Find Party window closed",
            _ => "party finder not loaded",
        };
        return parts switch
        {
            ["on"] => "Auto Pull Up: ticked.",
            ["off"] => "Auto Pull Up: unticked.",
            ["pulled", var where] => $"Auto Pull Up: pulled up ({Window(where)}).",
            ["stop", var reason, var where] => $"Auto Pull Up: stopped, {reason switch
            {
                "full" => "the party is full",
                "time" => "the time limit ran out",
                "off" => "the tweak was switched off",
                "no effect" => "a press didn't grey the button out, so it may not have reached the game",
                _ => "something unexpected happened in the party finder",
            }} ({Window(where)}).",
            _ => null,
        };
    }

    /// <summary>
    /// Saves a change made in the escape menu panel: "request&lt;tab&gt;tweak&lt;tab&gt;&lt;tab&gt;1|0" switches a tweak,
    /// "request&lt;tab&gt;tweak&lt;tab&gt;option&lt;tab&gt;value" sets an option (1|0 for a switch, the choice's value for a single
    /// choice). Updates the saved state and live.json; returns what changed, or null for a message it can't take.
    /// </summary>
    public string? ApplyInGameChange(string text)
    {
        var parts = text.Split('\t');
        if (parts.Length != 4 || TweakCatalog.Find(parts[1]) is not { Available: true, RestartOnly: false } tweak || tweak.Id == SettingsTweak)
            return null;
        var (request, key, value) = (parts[0], parts[2], parts[3]);
        var state = LoadState();
        var enabled = state.Settings.Enabled;
        string change;
        if (key.Length == 0)
        {
            if (value is not ("1" or "0")) return null;
            var on = value == "1";
            if (on && !enabled.ContainsKey(tweak.Id))
            {
                enabled[tweak.Id] = OptionsOf(state, tweak);
                state.Kept.Remove(tweak.Id);
            }
            else if (!on && enabled.Remove(tweak.Id, out var options))
                state.Kept[tweak.Id] = options;
            change = $"{tweak.Name} {(on ? "on" : "off")}";
        }
        else
        {
            if (tweak.OptionList.FirstOrDefault(option => option.Key == key) is not { } option) return null;
            var options = enabled.GetValueOrDefault(tweak.Id) ?? (state.Kept[tweak.Id] = OptionsOf(state, tweak));
            JsonElement element;
            if (option.Kind == "switch" && value is "1" or "0")
                element = JsonSerializer.SerializeToElement(value == "1");
            else if (option.Kind == "single" && option.Choices!.Any(choice => choice.Value == value))
                element = JsonSerializer.SerializeToElement(value);
            else if (option.Kind == "list" && value.Length > 1 && value[0] is '+' or '-' &&
                     option.Choices!.Any(choice => choice.Value == value[1..]))
            {
                // A pick-list changes one choice at a time: "+value" adds it, "-value" takes it out.
                var list = options.TryGetValue(key, out var saved) && saved.ValueKind == JsonValueKind.Array
                    ? saved.EnumerateArray().Select(item => item.ToString()).ToList()
                    : option.Choices!.Where(choice => choice.Default).Select(choice => choice.Value).ToList();
                list.Remove(value[1..]);
                if (value[0] == '+') list.Add(value[1..]);
                element = JsonSerializer.SerializeToElement(list);
            }
            else
                return null;
            options[key] = element;
            change = $"{tweak.Name}: {option.Label} = {value}";
        }
        SaveState(state, request);
        return change;
    }

    /// <summary>
    /// Saves the Tweaks tab's choices without rebuilding when the installed tweaks can already follow them through
    /// live.json: In-game Tweak Settings is built and every interface tweak now on has hooks. Used while the game runs
    /// (installing needs it closed). False when a rebuild is needed.
    /// </summary>
    public bool TrySaveLive(TweakSettings settings)
    {
        var state = LoadState();
        if (state.ModId is null || !state.Built.Contains(SettingsTweak) || !settings.Enabled.ContainsKey(SettingsTweak) ||
            !BuiltTweaks(settings).All(tweak => state.Built.Contains(tweak.Id)))
            return false;
        foreach (var id in settings.Enabled.Keys)
            state.Kept.Remove(id);
        state.Settings = settings;
        SaveState(state);
        return true;
    }

    /// <summary>
    /// The app's working edits. Installing a tweak mod sets its movies up there as edits bound to the mod; when a
    /// build replaces the mod those edits are deleted with it, so none is left ticked to clash with the next build.
    /// </summary>
    public ArkiveModEditWorkspaces? Edits { get; init; }

    /// <summary>
    /// Deletes the working edits of the given tweak mods and every tweak edit no other mod is bound to, such as ones
    /// left by older Arkive test builds (which removed tweak mods without their edits). Returns how many it deleted.
    /// </summary>
    public async Task<int> ReleaseDraftsAsync(IReadOnlyCollection<ArkiveModLibraryEntry> tweakMods, CancellationToken cancellationToken = default)
    {
        if (Edits is not { } edits) return 0;
        var current = await library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var removing = tweakMods.Select(mod => mod.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keptBindings = current.Mods.Where(mod => !removing.Contains(mod.ModId))
            .SelectMany(mod => mod.Bindings).Select(binding => binding.EditId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(edits, cancellationToken).ConfigureAwait(false);
        var stale = tweakMods.SelectMany(mod => mod.Bindings).Select(binding => binding.EditId)
            .Concat(snapshot.Edits
                .Where(edit => edit.Kind == UnifiedPackageAssetKind.InterfaceMovie &&
                               edit.DisplayName.StartsWith(DraftPrefix, StringComparison.Ordinal))
                .Select(edit => edit.EditId))
            .Where(editId => !keptBindings.Contains(editId) && snapshot.Find(editId) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(editId => new ArkiveModReleasedEdit(editId, Delete: true))
            .ToArray();
        await ArkiveModSelectionService.ReleaseAsync(edits, stale, cancellationToken).ConfigureAwait(false);
        return stale.Length;
    }

    private string StatePath => Path.Combine(root, "state.json");

    /// <summary>The tweak whose tab reads the logs Arkive fetches, and the folder it reads them from.</summary>
    public const string PlayerLogsTweak = "logs";

    public string PlayerLogsFolder => Path.Combine(root, "player-logs");

    /// <summary>Where Market Price on Tooltips' prices are kept (the tweak reads prices/prices.txt under "home").</summary>
    public string PricesFolder => Path.Combine(root, "prices");
    /// <summary>The tweak that advises astrogem processing, from odds tables Arkive writes for every target.</summary>
    public const string AstrogemTweak = "astrogem";

    /// <summary>The tweak that shows market prices; its build writes the market's item list beside the prices.</summary>
    public const string MarketPriceTweak = "marketprice";

    /// <summary>The tweak that prices gems in Combat Power, from the game's own table.</summary>
    public const string CombatPowerTweak = "cpgems";

    /// <summary>The tweak that prices auction and market rows per gold with Combat Power's tables.</summary>
    public const string CombatPowerPerGoldTweak = "cpgold";

    /// <summary>The bracelet reroll judge, which prices with the Combat Power tweak's tables (kept under its id).</summary>
    public const string BraceletJudgeTweak = "braceletjudge";

    /// <summary>The inspect window's Compare tab, which prices with the Combat Power tweak's tables (kept under its id).</summary>
    public const string CompareTweak = "compare";

    /// <summary>
    /// The saved choices, without tweaks Arkive no longer has (a removed tweak's saved options are dropped) and without
    /// Faster Launch, which older Arkive builds saved here: whether it is installed is the intro movies' own state.
    /// </summary>
    public TweakState LoadState()
    {
        TweakState state;
        try
        {
            state = File.Exists(StatePath) ? JsonSerializer.Deserialize<TweakState>(File.ReadAllText(StatePath)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
        foreach (var id in state.Settings.Enabled.Keys.Concat(state.Kept.Keys).Concat(state.OnBeforeRemove)
                     .Where(id => TweakCatalog.Find(id) is null or { RestartOnly: true }).ToArray())
        {
            state.Settings.Enabled.Remove(id);
            state.Kept.Remove(id);
            state.OnBeforeRemove.Remove(id);
        }
        return state;
    }

    /// <summary>Saves the choices, and writes live.json so a running game follows them (<paramref name="ack"/>: see WriteLive).</summary>
    public void SaveState(TweakState state, string ack = "")
    {
        Directory.CreateDirectory(root);
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, StatePath, overwrite: true);
        WriteLive(state, ack);
    }

    /// <summary>
    /// Replaces the tweak mod with one built for these settings and turns it on; with no tweaks on, removes it.
    /// The caller applies mods afterwards.
    /// </summary>
    public async Task<TweakBuildResult> BuildAsync(
        TweakSettings settings, string packagesDirectory, byte[] tweakSwf, CancellationToken cancellationToken = default)
    {
        var state = LoadState();
        IReadOnlyList<TweakDefinition> built = settings.Enabled.Count == 0 ? [] : BuiltTweaks(settings);
        // Every hook is gated by its tweak's id, so a tweak switched off (in game, or never ticked) leaves the game alone.
        var targets = built
            .SelectMany(tweak => tweak.Hooks.Select(hook => (Target: hook, Hook: hook.Hook with { Gate = tweak.Id })))
            .GroupBy(item => (item.Target.Package, item.Target.Movie))
            .ToArray();
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var oldMods = (await library.LoadAsync(cancellationToken).ConfigureAwait(false)).Mods.Where(IsTweakMod).ToArray();
        await ReleaseDraftsAsync(oldMods, cancellationToken).ConfigureAwait(false);
        foreach (var old in oldMods)
        {
            packages.UnionWith(PackagesOf(old));
            await library.RemoveAsync(old.ModId, deleteFile: true, cancellationToken).ConfigureAwait(false);
        }
        state.ModId = null;
        state.Built = [];
        // Tweaks taken out keep their options, and Remove All remembers which were on, so installing again brings the
        // player's choices back.
        foreach (var (id, options) in state.Settings.Enabled)
            if (!settings.Enabled.ContainsKey(id))
                state.Kept[id] = options;
        if (settings.Enabled.Count > 0)
            state.OnBeforeRemove = [];
        else if (state.Settings.Enabled.Count > 0)
            state.OnBeforeRemove = [.. state.Settings.Enabled.Keys];
        state.Settings = settings;
        foreach (var id in settings.Enabled.Keys)
            state.Kept.Remove(id);
        if (targets.Length == 0)
        {
            SaveState(state);
            return new TweakBuildResult(null, [], [], packages);
        }

        var tweakCode = TweakCode.ForSettings(tweakSwf, ForGame(state, built, packagesDirectory));
        var gamePackages = FindPackages(packagesDirectory);
        var staging = Path.Combine(root, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        var store = new InterfaceMovieEditWorkspaceStore(staging, protectionProvider);
        var captured = new List<ArkiveModCapturedAsset>();
        var missing = new List<string>();
        var movies = new List<string>();
        try
        {
            foreach (var group in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (alias, movieName) = group.Key;
                if (!gamePackages.TryGetValue(alias, out var installed))
                {
                    missing.AddRange(group.Select(hook => $"{alias}: package not found"));
                    continue;
                }
                var source = cleanPackage is null ? installed : await cleanPackage(installed, cancellationToken).ConfigureAwait(false);
                var catalog = new AssetCatalogReader(null, protectionProvider).Read(source);
                var export = catalog.Assets.FirstOrDefault(asset =>
                    asset.ClassName.Equals("EFSwfMovie", StringComparison.OrdinalIgnoreCase) &&
                    asset.ObjectName.Equals(movieName, StringComparison.OrdinalIgnoreCase));
                if (export is null)
                {
                    missing.AddRange(group.Select(hook => $"{alias}.{movieName}: movie not found"));
                    continue;
                }
                var original = ReadExport(catalog, export);
                var movie = TweakMovieBuilder.Build(GfxMovieFile.Extract(original), group.Select(item => item.Hook with { Diagnostic = Diagnostic }), tweakCode);
                missing.AddRange(movie.MissingHooks);
                var editedPath = Path.Combine(staging, $"{alias}.{movieName}{GfxMovieFile.Extension}");
                Directory.CreateDirectory(staging);
                await File.WriteAllBytesAsync(editedPath, movie.Movie, cancellationToken).ConfigureAwait(false);
                if (KeepMoviesIn is { } keep)
                {
                    Directory.CreateDirectory(keep);
                    File.Copy(editedPath, Path.Combine(keep, Path.GetFileName(editedPath)), overwrite: true);
                }
                // The edit reads the clean package (a backup when the tweaks are installed), whose hash is the game's own;
                // the mod still targets the game's file name.
                var entry = await store.CreateAsync(
                        source, alias, Sha256(source), export, $"{DraftPrefix}{alias} {movieName}", editedPath, cancellationToken)
                    .ConfigureAwait(false);
                var local = new ArkiveModLocalEdit(
                    UnifiedPackageAssetKind.InterfaceMovie, entry.Manifest.EditId, entry.ManifestPath, entry.Manifest.DisplayName,
                    installed, export.ObjectName, Included: true, entry);
                var asset = await ArkiveModAuthoringService.CaptureAsync(local, cancellationToken: cancellationToken).ConfigureAwait(false);
                captured.Add(asset with
                {
                    Descriptor = asset.Descriptor with
                    {
                        Target = asset.Descriptor.Target with
                        {
                            PhysicalPackageName = ArkiveModFileService.NormalizePhysicalPackageName(Path.GetFileName(installed)),
                        },
                    },
                });
                movies.Add($"{alias}.{movieName}");
            }
            if (captured.Count == 0)
            {
                SaveState(state);
                return new TweakBuildResult(null, missing, movies, packages);
            }
            var description = "Built by Arkive's Tweaks tab: " + string.Join(", ",
                settings.Enabled.Keys.Select(id => TweakCatalog.Find(id)?.Name ?? id)) + ". Change it there, not here.";
            var authoring = new ArkiveModAuthoringService(library, new ArkiveModEditWorkspaces(
                new GuidedTextureModWorkspace(Path.Combine(staging, "texture-mods")),
                new Meshes.SkeletalMeshEditWorkspaceStore(Path.Combine(staging, "skeletal-mesh-edits")),
                new StaticMeshes.StaticMeshEditWorkspaceStore(Path.Combine(staging, "static-mesh-edits")),
                new Materials.MaterialInstanceEditWorkspaceStore(Path.Combine(staging, "material-edits")))
            {
                InterfaceMovies = store,
            });
            var mod = await authoring.CreateModAsync(
                    new ArkiveModMetadata(ModName, DateTimeOffset.UtcNow.ToString("yyyy.MM.dd.HHmm"), "Arkive", description, Tags: [Tag]),
                    captured, ArkiveModOrigin.Created, enabled: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            state.ModId = mod.ModId;
            state.Built = [.. built.Select(tweak => tweak.Id)];
            SaveState(state);
            packages.UnionWith(PackagesOf(mod));
            return new TweakBuildResult(mod, missing, movies, packages);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
            }
        }
    }

    private byte[] ReadExport(AssetCatalog catalog, AssetCatalogEntry export)
    {
        using var logical = new LogicalPackageStream(catalog.Summary, protectionProvider);
        var bytes = new byte[export.SerialSize];
        logical.Position = export.SerialOffset;
        logical.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Interface packages by name ("efui_tophud") from the game's scrambled file names.</summary>
    public static Dictionary<string, string> FindPackages(string packagesDirectory) =>
        new DirectoryInfo(packagesDirectory).EnumerateFiles("*.upk")
            .Select(file => LostArkPackageName.TryDecodeFileName(file.Name, out var name) ? (name, file.FullName) : default)
            .Where(item => item.name is not null && item.name.StartsWith("efui_", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().FullName, StringComparer.OrdinalIgnoreCase);

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
