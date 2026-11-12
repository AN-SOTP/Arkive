using System.Text.Json;
using Arkive.Core.Archives;
using Arkive.Core.Discovery;
using Arkive.Core.External;
using Arkive.Core.Interface;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.Mods;
using Arkive.Core.Packages;
using Arkive.Core.StaticMeshes;

namespace Arkive.Core.Diagnostics;

/// <summary>
/// Times the engine-side loads Arkive's startup performs, in the order startup performs them, without a window. Each
/// store is read once by the same kind of object the window uses, so later steps find the records it parsed already,
/// as they do at startup. Only reads: nothing under the data folder is written, moved or deleted.
/// </summary>
public static class LaunchBenchmark
{
    public static async Task<LaunchMeasurementReport> RunAsync(
        string dataRoot,
        bool forcedGc = false,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(dataRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"The Arkive data folder was not found: {root}");
        var recorder = new LaunchStepRecorder(forcedGc);
        string? error = null;
        try
        {
            await RunStepsAsync(root, recorder, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error = $"{exception.GetType().Name}: {exception.Message}";
        }
        return recorder.Report("benchmark", root, error);
    }

    private static async Task RunStepsAsync(string root, LaunchStepRecorder steps, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(root, "staging");
        var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
        var reader = new UpkPackageReader();
        var meshExports = new SkeletalMeshEditableWorkspaceStore(Path.Combine(staging, "skeletal-mesh-reconnaissance"));
        var movies = new InterfaceMovieEditWorkspaceStore(Path.Combine(staging, "interface-movie-edits"), provider);
        var edits = new ArkiveModEditWorkspaces(
            new GuidedTextureModWorkspace(Path.Combine(staging, "texture-mods")),
            new SkeletalMeshEditWorkspaceStore(Path.Combine(staging, "skeletal-mesh-edits")),
            new StaticMeshEditWorkspaceStore(Path.Combine(staging, "static-mesh-edits"), reader, provider),
            new MaterialInstanceEditWorkspaceStore(Path.Combine(staging, "material-edits"), provider))
        {
            InterfaceMovies = movies,
        };
        var library = new ArkiveModLibrary(Path.Combine(root, "mods"), Path.Combine(staging, "mod-library"));
        var packages = new GuidedTexturePackageWorkspace(Path.Combine(staging, "texture-mods", "_packages"));
        var context = new ArkiveModApplyContext(
            library, edits, packages, new ArkiveModSetWorkspace(Path.Combine(staging, "mod-sets")),
            new ArkiveModDeploymentService(reader), Path.Combine(root, "backups", "packages"),
            Path.Combine(root, "backups", "installations"))
        {
            GameDataRoot = root,
        };

        steps.Run("hash-cache", () =>
        {
            ArkiveModEditSnapshot.LoadHashCache(Path.Combine(root, "cache", "file-hashes.json"));
            ArkiveModApplyService.LoadOutsideChangeCache(Path.Combine(root, "cache", "outside-changes.json"));
        });
        await steps.RunAsync("mesh-exports", () => meshExports.LoadAsync(cancellationToken), Count).ConfigureAwait(false);
        await steps.RunAsync("mesh-edits", () => edits.SkeletalMeshEdits.LoadAsync(cancellationToken), Count).ConfigureAwait(false);
        await steps.RunAsync("static-mesh-edits", () => edits.StaticMeshEdits.LoadAsync(cancellationToken), Count).ConfigureAwait(false);
        await steps.RunAsync("materials", () => edits.MaterialEdits.LoadAsync(cancellationToken), Count).ConfigureAwait(false);
        await steps.RunAsync("textures", () => edits.Textures.LoadAsync(cancellationToken), Count).ConfigureAwait(false);
        await steps.RunAsync("effects", () => edits.ParticleEdits.LoadAsync(cancellationToken), Count).ConfigureAwait(false);
        await steps.RunAsync("physics", () => edits.PhysicsEdits.LoadAsync(cancellationToken), Count).ConfigureAwait(false);
        await steps.RunAsync("interface-movies", () => movies.LoadAsync(cancellationToken), Count).ConfigureAwait(false);
        var manifest = await steps.RunAsync("library", () => library.LoadAsync(cancellationToken),
            loaded => $"mods={loaded.Mods.Count}, enabled={loaded.Mods.Count(mod => mod.Enabled)}").ConfigureAwait(false);
        var snapshot = await steps.RunAsync("working-files", () => ArkiveModEditSnapshot.LoadAsync(edits, cancellationToken),
            loaded => $"items={loaded.Edits.Count}").ConfigureAwait(false);
        var projects = await steps.RunAsync("package-projects", () => packages.LoadAsync(cancellationToken),
            loaded => $"items={loaded.Count}, builds={loaded.Sum(project => project.Manifest.Revisions.Count)}").ConfigureAwait(false);
        var active = await steps.RunAsync("install-state", () => ActiveRevisionsAsync(context, manifest, projects, cancellationToken),
            found => $"installed={found.Count(pair => pair.Value is not null)}").ConfigureAwait(false);
        var statuses = await steps.RunAsync("status", () => ArkiveModApplyService.DescribeAsync(
                library, manifest, snapshot, active, cancellationToken),
            described => $"mods={described.Count}, inGame={described.Count(status => status.InGame)}").ConfigureAwait(false);
        _ = statuses;

        var settings = ReadSettings(Path.Combine(root, "cache", "arkive-settings.json"));
        var metadata = settings.GameMetadataPath is { } saved && File.Exists(saved)
            ? saved
            : Path.Combine(staging, "lpk", "data2-fixed", "arkive-asset-metadata.json");
        if (File.Exists(metadata))
            await steps.RunAsync("database", () => Task.Run(() => GameAssetMetadataIndex.Load(metadata), cancellationToken),
                index => $"associations={index.AssociationCount}, entities={index.EntityCount}").ConfigureAwait(false);
        else steps.Skip("database", "no game database in this data folder");

        var indexCache = Path.Combine(root, "cache", "library-index");
        if (Directory.Exists(indexCache))
        {
            var bridge = UmodelLocator.FindExecutable() is { } umodel ? new UmodelBridge(umodel) : null;
            await steps.RunAsync("index-status", () => new PackageIndexStore(indexCache).GetHealthAsync(
                    settings.LibraryRootPath, bridge, cancellationToken),
                health => $"documents={health.CachedDocumentCount}").ConfigureAwait(false);
        }
        else steps.Skip("index-status", "no library index in this data folder");
    }

    /// <summary>Which build of each package project is in the game now, as the Mods list asks.</summary>
    private static async Task<IReadOnlyDictionary<string, GuidedTexturePackageRevision?>> ActiveRevisionsAsync(
        ArkiveModApplyContext context,
        ArkiveModLibraryManifest library,
        IReadOnlyList<GuidedTexturePackageEntry> projects,
        CancellationToken cancellationToken)
    {
        var owners = projects.Select(package => Path.GetFileName(package.Manifest.SourcePackagePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(physical => (Physical: physical,
                Project: ArkiveModApplyService.FindPackageProject(physical, library.GamePackagesDirectory, projects)))
            .Where(item => item.Project is not null)
            .ToArray();
        var found = new GuidedTexturePackageRevision?[owners.Length];
        // The window inspects eight package projects at a time.
        await Parallel.ForEachAsync(Enumerable.Range(0, owners.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (index, token) => found[index] = (await context.FindActiveDeploymentAsync(
                owners[index].Project!, token, reuseKnownHashes: true).ConfigureAwait(false))?.Revision).ConfigureAwait(false);
        var map = new Dictionary<string, GuidedTexturePackageRevision?>(StringComparer.Ordinal);
        for (var index = 0; index < owners.Length; index++)
            map[ArkiveModFileService.PhysicalPackageKey(owners[index].Physical)] = found[index];
        return map;
    }

    private static string? Count<T>(IReadOnlyCollection<T> items) => $"items={items.Count}";

    private static string? Count(SkeletalMeshEditableWorkspaceSnapshot snapshot) =>
        $"items={snapshot.Entries.Count}, unreadable={snapshot.Issues.Count}";

    private static string? Count(SkeletalMeshEditWorkspaceSnapshot snapshot) =>
        $"items={snapshot.Entries.Count}, unreadable={snapshot.Issues.Count}";

    private sealed record SavedSettings(string? GameMetadataPath, string? LibraryRootPath);

    /// <summary>The two settings the loads need, read without the window's settings type.</summary>
    private static SavedSettings ReadSettings(string path)
    {
        try
        {
            if (!File.Exists(path)) return new SavedSettings(null, null);
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            string? Text(string name) => document.RootElement.TryGetProperty(name, out var value) &&
                                         value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return new SavedSettings(Text("GameMetadataPath"), Text("LibraryRootPath"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SavedSettings(null, null);
        }
    }
}
