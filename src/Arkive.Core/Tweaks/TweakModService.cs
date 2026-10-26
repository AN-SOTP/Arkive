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

/// <summary>Saved choices: which tweaks are on, their options, and the library mod that holds them.</summary>
public sealed class TweakState
{
    public TweakSettings Settings { get; set; } = new();
    public string? ModId { get; set; }
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
    /// The settings the tweak code gets: the saved ones, plus the folder the Logs tab reads its answers from and the
    /// game's Combat Power tables (left out when there is no data2.lpk, so that tweak shows nothing).
    /// </summary>
    private TweakSettings ForGame(TweakSettings settings, string packagesDirectory)
    {
        var copy = new TweakSettings { Enabled = new(settings.Enabled) };
        if (settings.Enabled.TryGetValue(PlayerLogsTweak, out var logs))
            copy.Enabled[PlayerLogsTweak] = new(logs) { ["folder"] = JsonSerializer.SerializeToElement(PlayerLogsFolder.Replace('\\', '/')) };
        var data2 = GameDataFile ?? Path.GetFullPath(Path.Combine(packagesDirectory, "..", "..", "data2.lpk"));
        if (settings.Enabled.TryGetValue(CombatPowerTweak, out var power) && File.Exists(data2))
        {
            var options = new Dictionary<string, JsonElement>(power);
            foreach (var (key, value) in CombatPowerTable.Options(data2, Path.Combine(root, "cache")))
                options[key] = value;
            copy.Enabled[CombatPowerTweak] = options;
        }
        return copy;
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

    /// <summary>The tweak that prices gems in Combat Power, from the game's own table.</summary>
    public const string CombatPowerTweak = "cpgems";

    public TweakState LoadState()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<TweakState>(File.ReadAllText(StatePath)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void SaveState(TweakState state)
    {
        Directory.CreateDirectory(root);
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, StatePath, overwrite: true);
    }

    /// <summary>
    /// Replaces the tweak mod with one built for these settings and turns it on; with no tweaks on, removes it.
    /// The caller applies mods afterwards.
    /// </summary>
    public async Task<TweakBuildResult> BuildAsync(
        TweakSettings settings, string packagesDirectory, byte[] tweakSwf, CancellationToken cancellationToken = default)
    {
        var state = LoadState();
        var targets = settings.Enabled.Keys
            .Select(TweakCatalog.Find).OfType<TweakDefinition>().Where(tweak => tweak.Available)
            .SelectMany(tweak => tweak.Hooks)
            .GroupBy(hook => (hook.Package, hook.Movie))
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
        state.Settings = settings;
        if (targets.Length == 0)
        {
            SaveState(state);
            return new TweakBuildResult(null, [], [], packages);
        }

        var tweakCode = TweakCode.ForSettings(tweakSwf, ForGame(settings, packagesDirectory));
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
                var built = TweakMovieBuilder.Build(GfxMovieFile.Extract(original), group.Select(hook => hook.Hook with { Diagnostic = Diagnostic }), tweakCode);
                missing.AddRange(built.MissingHooks);
                var editedPath = Path.Combine(staging, $"{alias}.{movieName}{GfxMovieFile.Extension}");
                Directory.CreateDirectory(staging);
                await File.WriteAllBytesAsync(editedPath, built.Movie, cancellationToken).ConfigureAwait(false);
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
