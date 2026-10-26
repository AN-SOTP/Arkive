using System.Security.Cryptography;
using System.Text.Json;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Interface;
using Arkive.Core.Mods;
using Arkive.Core.Textures;

namespace Arkive.Core.Tweaks;

/// <summary>What a tweak build made: the mod, and the hooks this game version no longer has (their tweaks may not work).</summary>
public sealed record TweakBuildResult(ArkiveModLibraryEntry? Mod, IReadOnlyList<string> MissingHooks, IReadOnlyList<string> Movies);

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
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>When set, each built movie is also written here (for decompiling while developing tweaks).</summary>
    public string? KeepMoviesIn { get; init; }

    private string StatePath => Path.Combine(root, "state.json");

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
        if (state.ModId is { } oldModId && (await library.LoadAsync(cancellationToken).ConfigureAwait(false)).Find(oldModId) is not null)
            await library.RemoveAsync(oldModId, deleteFile: true, cancellationToken).ConfigureAwait(false);
        state.ModId = null;
        state.Settings = settings;
        if (targets.Length == 0)
        {
            SaveState(state);
            return new TweakBuildResult(null, [], []);
        }

        var tweakCode = TweakCode.ForSettings(tweakSwf, settings);
        var packages = FindPackages(packagesDirectory);
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
                if (!packages.TryGetValue(alias, out var installed))
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
                var built = TweakMovieBuilder.Build(GfxMovieFile.Extract(original), group.Select(hook => hook.Hook), tweakCode);
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
                        source, alias, Sha256(source), export, $"Tweaks: {alias} {movieName}", editedPath, cancellationToken)
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
                return new TweakBuildResult(null, missing, movies);
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
                    new ArkiveModMetadata(ModName, DateTimeOffset.UtcNow.ToString("yyyy.MM.dd.HHmm"), "Arkive", description, Tags: ["tweaks"]),
                    captured, ArkiveModOrigin.Created, enabled: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            state.ModId = mod.ModId;
            SaveState(state);
            return new TweakBuildResult(mod, missing, movies);
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
