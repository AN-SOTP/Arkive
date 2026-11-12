using Arkive.Core.Diagnostics;
using static Arkive.SmokeTests.ArkiveModTestFixture;

namespace Arkive.SmokeTests;

/// <summary>
/// Which build of a package is in the game, decided from package project records and receipts alone
/// (PackageInstallStateService), and the Mods list status it gives without the package view's rows.
/// </summary>
internal static class PackageInstallStateSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("install state: the latest build is in the game", TestLatestInstalled),
        new("install state: an earlier build is still in the game", TestEarlierStillInstalled),
        new("install state: the clean original is back, so no build is in the game", TestCleanLineageRoot),
        new("install state: a project with no build has none in the game", TestNothingBuilt),
        new("install state: an unreadable receipt leaves that build out and still checks other packages", TestUnreadableReceipt),
        new("Mods list status on the launch fixture is the same without the package view's rows", TestModsListWithoutPackageRows),
    ];

    private sealed record Setup(ArkiveTestEnvironment Environment, string PackagePath, string CleanHash, ArkiveModApplyContext Context)
    {
        public string GameDirectory => Path.GetDirectoryName(PackagePath)!;

        public string Key => ArkiveModFileService.PhysicalPackageKey(Path.GetFileName(PackagePath));

        public ArkiveModDeploymentRequest Request(GuidedTexturePackageRevision revision) =>
            new(revision.ModBundleManifestPath, Environment.Backups, Environment.State);

        public GuidedTexturePackageEntry Project() =>
            Environment.Packages.LoadAsync().GetAwaiter().GetResult().Single(project =>
                Path.GetFileName(project.Manifest.SourcePackagePath) == Path.GetFileName(PackagePath));
    }

    private static Setup NewSetup(string name)
    {
        var root = Path.Combine(fixtureDirectory, "install-state-" + name);
        var packagePath = CreateMaterialClient(root, out var clean);
        var environment = CreateEnvironment(Path.Combine(root, "workspace"));
        return new Setup(environment, packagePath, clean, Context(environment, Path.GetDirectoryName(packagePath)!));
    }

    /// <summary>Builds the package from the clean game file with one changed material and commits it as the latest build.</summary>
    private static GuidedTexturePackageRevision Build(Setup setup, float roughness, string? packagePath = null, string alias = "material_instance")
    {
        var environment = setup.Environment;
        packagePath ??= setup.PackagePath;
        var project = environment.Packages.GetOrCreateAsync(alias, Path.GetFileName(packagePath), packagePath)
            .GetAwaiter().GetResult();
        var edit = CreateMaterialEdit(environment, packagePath, $"Build {roughness}", roughness, null, candidate: true);
        var composition = Composer().BuildAsync(new UnifiedPackageCompositionRequest(
                project, [], [], environment.Packages.CreateRevisionDirectory(project), environment.Backups, $"Build {roughness}")
            {
                MaterialEdits = [environment.Edits.MaterialEdits.LoadEntryAsync(edit.DocumentPath).GetAwaiter().GetResult()],
            }).GetAwaiter().GetResult();
        environment.Packages.CommitRevisionAsync(project.ManifestPath, composition.Revision).GetAwaiter().GetResult();
        return composition.Revision;
    }

    private static PackageDeploymentState? Resolve(Setup setup, GuidedTexturePackageEntry project) =>
        new PackageInstallStateService(setup.Context).ResolveAsync(project).GetAwaiter().GetResult();

    private static GuidedTexturePackageRevision? Active(Setup setup, GuidedTexturePackageEntry project, PackageDeploymentState? state) =>
        PackageInstallStateService.ActiveRevisionsByPhysicalName([project], setup.GameDirectory, _ => state)[setup.Key];

    private static void TestLatestInstalled()
    {
        var setup = NewSetup("latest");
        _ = Build(setup, 0.4f);
        var latest = Build(setup, 0.7f);
        setup.Environment.Deployment.InstallAsync(setup.Request(latest)).GetAwaiter().GetResult();
        var project = setup.Project();
        var state = Resolve(setup, project);
        Assert(state is { IsActive: true, ForDeletionIdentityOnly: false, Error: null } &&
               state.Revision.RevisionId == latest.RevisionId && project.Manifest.LatestRevisionId == latest.RevisionId,
            $"the installed latest build was not found: {state?.Revision.RevisionId ?? "none"}, active={state?.IsActive}");
        Assert(Active(setup, project, state)?.RevisionId == latest.RevisionId, "the Mods list map did not name the installed latest build");
    }

    private static void TestEarlierStillInstalled()
    {
        var setup = NewSetup("earlier");
        var earlier = Build(setup, 0.4f);
        var latest = Build(setup, 0.7f);
        setup.Environment.Deployment.InstallAsync(setup.Request(earlier)).GetAwaiter().GetResult();
        var project = setup.Project();
        var state = Resolve(setup, project);
        Assert(project.Manifest.LatestRevisionId == latest.RevisionId &&
               state is { IsActive: true, ForDeletionIdentityOnly: true } && state.Revision.RevisionId == earlier.RevisionId,
            $"an earlier build still in the game was not found: {state?.Revision.RevisionId ?? "none"}, " +
            $"active={state?.IsActive}, identityOnly={state?.ForDeletionIdentityOnly}");
        Assert(Active(setup, project, state)?.RevisionId == earlier.RevisionId, "the Mods list map did not name the earlier build");
    }

    private static void TestCleanLineageRoot()
    {
        var setup = NewSetup("clean-root");
        var earlier = Build(setup, 0.4f);
        _ = Build(setup, 0.7f);
        setup.Environment.Deployment.InstallAsync(setup.Request(earlier)).GetAwaiter().GetResult();
        setup.Environment.Deployment.RestoreAsync(setup.Request(earlier)).GetAwaiter().GetResult();
        var project = setup.Project();
        var state = Resolve(setup, project);
        Assert(Sha(setup.PackagePath) == setup.CleanHash &&
               state is { IsActive: false, Inspection: { } inspection } &&
               inspection.State is ModInstallationState.NotInstalled or ModInstallationState.Restored &&
               string.Equals(inspection.CurrentTargetSha256, setup.CleanHash, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(inspection.Bundle.RequiredSourcePackageSha256, setup.CleanHash, StringComparison.OrdinalIgnoreCase),
            $"the restored clean original was not recognised as the build history's root: {state?.Revision.RevisionId ?? "none"}, " +
            $"active={state?.IsActive}, state={state?.Inspection?.State}");
        Assert(Active(setup, project, state) is null, "the Mods list map named a build although the clean original is in the game");
    }

    private static void TestNothingBuilt()
    {
        var setup = NewSetup("nothing");
        var project = setup.Environment.Packages.GetOrCreateAsync(
            "material_instance", Path.GetFileName(setup.PackagePath), setup.PackagePath).GetAwaiter().GetResult();
        var state = Resolve(setup, project);
        var map = PackageInstallStateService.ActiveRevisionsByPhysicalName([project], setup.GameDirectory, _ => state);
        Assert(state is null && map.TryGetValue(setup.Key, out var active) && active is null,
            "a package project with no build was given an install state");
    }

    private static void TestUnreadableReceipt()
    {
        var setup = NewSetup("receipt");
        var installed = Build(setup, 0.6f);
        setup.Environment.Deployment.InstallAsync(setup.Request(installed)).GetAwaiter().GetResult();
        // A second package in the same game folder, installed and healthy.
        var otherPath = Path.Combine(setup.GameDirectory, "material-other.upk");
        WriteMaterialInstancePackage(otherPath);
        var other = Build(setup, 0.3f, otherPath, "material_other");
        setup.Environment.Deployment.InstallAsync(setup.Request(other)).GetAwaiter().GetResult();

        var modId = ArkiveModBundleService.LoadBundleAsync(installed.ModBundleManifestPath).GetAwaiter().GetResult().ModId;
        var receipt = Path.Combine(setup.Environment.State, modId, ArkiveModDeploymentService.ReceiptFileName);
        Assert(File.Exists(receipt), "the install wrote no receipt to damage");
        File.WriteAllText(receipt, "{ this is not a receipt");

        var service = new PackageInstallStateService(setup.Context);
        var projects = setup.Environment.Packages.LoadAsync().GetAwaiter().GetResult();
        var states = service.ResolveAllAsync(projects).GetAwaiter().GetResult();
        var damaged = projects.Single(project => Path.GetFileName(project.Manifest.SourcePackagePath) == Path.GetFileName(setup.PackagePath));
        var healthy = projects.Single(project => project != damaged);
        Assert(states[damaged] is { IsActive: false, Inspection: null, Error: not null } &&
               states[damaged]!.Revision.RevisionId == installed.RevisionId,
            $"a build with an unreadable receipt was not left inconclusive: active={states[damaged]?.IsActive}, " +
            $"error={states[damaged]?.Error ?? "none"}");
        Assert(states[healthy] is { IsActive: true } && states[healthy]!.Revision.RevisionId == other.RevisionId,
            "one unreadable receipt stopped another package's install state from being read");
        var map = PackageInstallStateService.ActiveRevisionsByPhysicalName(projects, setup.GameDirectory,
            project => states.GetValueOrDefault(project));
        Assert(map[setup.Key] is null && map[ArkiveModFileService.PhysicalPackageKey("material-other.upk")]?.RevisionId == other.RevisionId,
            "the Mods list map did not leave out the inconclusive build and keep the healthy one");
    }

    /// <summary>
    /// The Mods list used to take install state from the package view's rows: one row per game package a working file
    /// changes, ordered as the view lists them, each with the state its read found. It now reads the same from the
    /// package project records. Both are worked out here on a launch fixture, one project removed first, and the Mods
    /// list statuses compared.
    /// </summary>
    private static void TestModsListWithoutPackageRows()
    {
        var root = Path.Combine(fixtureDirectory, "install-state-launch");
        var summary = LaunchFixture.CreateAsync(root, new LaunchFixtureOptions(
            MeshEdits: 24, Textures: 16, Effects: 30, Materials: 16, Packages: 10, BuildsPerPackage: 2, Mods: 6)
        {
            MeshRecordBytes = 8_000,
            SkeletonBones = 8,
        }).GetAwaiter().GetResult();
        var staging = Path.Combine(root, "staging");
        var reader = new UpkPackageReader();
        var edits = new ArkiveModEditWorkspaces(
            new GuidedTextureModWorkspace(Path.Combine(staging, "texture-mods")),
            new SkeletalMeshEditWorkspaceStore(Path.Combine(staging, "skeletal-mesh-edits")),
            new StaticMeshEditWorkspaceStore(Path.Combine(staging, "static-mesh-edits"), reader, NoChunkProtectionProvider.Instance),
            new MaterialInstanceEditWorkspaceStore(Path.Combine(staging, "material-edits"), NoChunkProtectionProvider.Instance))
        {
            InterfaceMovies = new Arkive.Core.Interface.InterfaceMovieEditWorkspaceStore(Path.Combine(staging, "interface-movie-edits")),
        };
        var library = new ArkiveModLibrary(Path.Combine(root, "mods"), Path.Combine(staging, "mod-library"));
        var packages = new GuidedTexturePackageWorkspace(Path.Combine(staging, "texture-mods", "_packages"));
        var context = new ArkiveModApplyContext(library, edits, packages, new ArkiveModSetWorkspace(Path.Combine(staging, "mod-sets")),
            new ArkiveModDeploymentService(reader), Path.Combine(root, "backups", "packages"), Path.Combine(root, "backups", "installations"));
        var manifest = library.LoadAsync().GetAwaiter().GetResult();

        // A snapshot reads each working-file store once.
        var before = StoreWalks.Current();
        var snapshot = ArkiveModEditSnapshot.LoadAsync(edits).GetAwaiter().GetResult();
        var walks = StoreWalks.Since(before) ?? new Dictionary<string, int>();
        Assert(new[] { StoreWalks.Textures, StoreWalks.SkeletalMeshEdits, StoreWalks.StaticMeshEdits, StoreWalks.MaterialEdits,
                   StoreWalks.ParticleEdits, StoreWalks.PhysicsEdits, StoreWalks.InterfaceMovieEdits }
               .All(store => walks.GetValueOrDefault(store) == 1) && !walks.ContainsKey(StoreWalks.PackageProjects),
            "reading the working files did not walk each working-file store exactly once: " +
            string.Join(", ", walks.Select(pair => $"{pair.Key}={pair.Value}")));

        // A package only effects change, whose project is gone, gets one again before its state is read.
        var records = packages.LoadAsync().GetAwaiter().GetResult();
        var removed = records.First(project => snapshot.Edits.Any(edit =>
            edit.Kind == UnifiedPackageAssetKind.ParticleSystem &&
            SamePath(edit.SourcePackagePath, project.Manifest.SourcePackagePath)));
        Directory.Delete(removed.DirectoryPath, recursive: true);
        records = packages.LoadAsync().GetAwaiter().GetResult();
        var ensured = PackageInstallStateService.EnsureProjectsAsync(packages, records, snapshot).GetAwaiter().GetResult();
        var reread = packages.LoadAsync().GetAwaiter().GetResult();
        Assert(ensured.Count == records.Count + 1 && reread.Count == ensured.Count &&
               reread.Any(project => SamePath(project.Manifest.SourcePackagePath, removed.Manifest.SourcePackagePath)),
            $"the package project of a package only effects change was not created again: before={records.Count}, after={reread.Count}");
        Assert(PackageInstallStateService.EnsureProjectsAsync(packages, reread, snapshot).GetAwaiter().GetResult().Count == reread.Count,
            "creating missing package projects a second time created more");

        var service = new PackageInstallStateService(context);
        var game = manifest.GamePackagesDirectory;

        // Without rows: what the Mods list reads now.
        var listed = PackageInstallStateService.ProjectsForWorkingFiles(reread, snapshot.Edits.Select(edit => edit.SourcePackagePath));
        var states = service.ResolveAllAsync(listed).GetAwaiter().GetResult();
        var withoutRows = PackageInstallStateService.ActiveRevisionsByPhysicalName(listed, game, project => states.GetValueOrDefault(project));

        // With rows, as the package view built them and the Mods list used to read them.
        var rows = snapshot.Edits.Select(edit => Path.GetFullPath(edit.SourcePackagePath)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(source => reread.First(project => Path.GetFullPath(project.Manifest.SourcePackagePath)
                .Equals(source, StringComparison.OrdinalIgnoreCase)))
            .Select(project => (Entry: project, State: service.ResolveAsync(project).GetAwaiter().GetResult()))
            .OrderByDescending(row => row.Entry.Manifest.IsFavorite)
            .ThenByDescending(row => row.Entry.Manifest.UpdatedAt)
            .ThenBy(row => row.Entry.Manifest.PackageAlias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rowProjects = rows.Select(row => row.Entry).ToArray();
        var withRows = new Dictionary<string, GuidedTexturePackageRevision?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var physical = Path.GetFileName(row.Entry.Manifest.SourcePackagePath);
            if (ArkiveModApplyService.FindPackageProject(physical, game, rowProjects) != row.Entry) continue;
            withRows[ArkiveModFileService.PhysicalPackageKey(physical)] = row.State?.Inspection?.HasActiveDeployment == true
                ? row.State.Revision : null;
        }
        Assert(listed.Select(project => project.ManifestPath).SequenceEqual(rowProjects.Select(project => project.ManifestPath)) &&
               withRows.Count == withoutRows.Count && withRows.Count == summary.Packages &&
               withRows.All(pair => withoutRows.TryGetValue(pair.Key, out var other) && other?.RevisionId == pair.Value?.RevisionId),
            $"the install state read from the records differs from the package view's rows: rows={withRows.Count}, records={withoutRows.Count}");

        string Describe(IReadOnlyDictionary<string, GuidedTexturePackageRevision?> active) => string.Join("\n",
            ArkiveModApplyService.DescribeAsync(library, manifest, snapshot, active).GetAwaiter().GetResult()
                .OrderBy(status => status.Mod.ModId, StringComparer.Ordinal)
                .Select(status => $"{status.Mod.ModId}|{status.State}|{status.InGame}|{status.StillInGame}|" +
                                  string.Join(",", status.Assets.Select(asset => $"{asset.Asset.AssetId}:{asset.Setup}:{asset.InGame}"))));
        var described = Describe(withoutRows);
        Assert(described == Describe(withRows) && described.Length > 0,
            "the Mods list statuses differ between the package view's rows and the package project records");
    }

    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string Sha(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
}
