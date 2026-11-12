using Arkive.Core.Interface;
using Arkive.Core.Particles;
using Arkive.Core.Physics;

namespace Arkive.SmokeTests;

/// <summary>Launch speed-ups that must not change what is loaded: record stores read in parallel, package grouping.</summary>
internal static class LaunchLoadSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("groups working files by package exactly as the per-package path filter did", TestSourcePackageGrouping),
        new("reads material working copies in parallel in the same order as one at a time", TestMaterialStoreLoad),
        new("reads effect working copies in parallel in the same order as one at a time", TestParticleStoreLoad),
        new("reads physics working copies in parallel in the same order as one at a time", TestPhysicsStoreLoad),
        new("reads interface movie working copies in parallel in the same order as one at a time", TestInterfaceStoreLoad),
        new("reads texture projects in parallel in the same order as one at a time", TestGuidedTextureStoreLoad),
        new("reads package projects in parallel in the same order as one at a time", TestGuidedPackageStoreLoad),
    ];

    private sealed record Item(int Id, string SourcePackagePath);

    private static void TestSourcePackageGrouping()
    {
        var game = Path.Combine(fixtureDirectory, "Game", "Packages");
        var packages = Enumerable.Range(0, 20).Select(index => Path.Combine(game, $"PKG{index:D2}.upk")).ToArray();
        var random = new Random(75);
        // Spellings of one package that the old filter treated as the same file: case, "..", "." and separators.
        string Spelling(string path) => random.Next(6) switch
        {
            0 => path.ToUpperInvariant(),
            1 => path.ToLowerInvariant(),
            2 => Path.Combine(Path.GetDirectoryName(path)!, "Sub", "..", Path.GetFileName(path)),
            3 => Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path)),
            4 => path.Replace('\\', '/'),
            _ => path,
        };
        var items = Enumerable.Range(0, 2_000)
            .Select(id => new Item(id, Spelling(packages[random.Next(packages.Length - 1)])))
            .ToArray();
        var lookup = SourcePackageGrouping.BySourcePackage(items, item => item.SourcePackagePath);
        // The last package has no items; every package is also asked for by a different spelling.
        foreach (var package in packages.Concat(packages.Select(Spelling)))
        {
            var expected = items.Where(item => Path.GetFullPath(item.SourcePackagePath).Equals(
                Path.GetFullPath(package), StringComparison.OrdinalIgnoreCase)).ToArray();
            var actual = lookup[SourcePackageGrouping.Key(package)].ToArray();
            Assert(actual.SequenceEqual(expected), $"package {package} got {actual.Length} items, the filter {expected.Length}");
        }
        Assert(lookup.Sum(group => group.Count()) == items.Length, "an item was lost or counted twice");
        Assert(lookup[SourcePackageGrouping.Key(packages[^1])].Any() == false, "a package with no items got some");
    }

    private static void TestMaterialStoreLoad()
    {
        var root = RecordRoot("material-edits");
        WriteRecords(root, MaterialInstanceEditWorkspaceStore.DocumentFileName);
        var store = new MaterialInstanceEditWorkspaceStore(root);
        AssertSameAsSequential(
            () => store.LoadAsync(),
            Paths(root, MaterialInstanceEditWorkspaceStore.DocumentFileName),
            path => store.LoadEntryAsync(path),
            exception => exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException,
            entries => entries.OrderByDescending(entry => entry.Document.UpdatedAt).ToArray());
    }

    private static void TestParticleStoreLoad()
    {
        var root = RecordRoot("particle-edits");
        WriteRecords(root, ParticleEffectEditWorkspaceStore.DocumentFileName);
        var store = new ParticleEffectEditWorkspaceStore(root);
        AssertSameAsSequential(
            () => store.LoadAsync(),
            Paths(root, ParticleEffectEditWorkspaceStore.DocumentFileName),
            path => store.LoadEntryAsync(path),
            exception => exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException,
            entries => entries.OrderByDescending(entry => entry.Document.UpdatedAt).ToArray());
    }

    private static void TestPhysicsStoreLoad()
    {
        var root = RecordRoot("physics-edits");
        WriteRecords(root, PhysicsAssetEditWorkspaceStore.DocumentFileName);
        var store = new PhysicsAssetEditWorkspaceStore(root);
        AssertSameAsSequential(
            () => store.LoadAsync(),
            Paths(root, PhysicsAssetEditWorkspaceStore.DocumentFileName),
            path => store.LoadEntryAsync(path),
            exception => exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException,
            entries => entries.OrderByDescending(entry => entry.Document.UpdatedAt).ToArray());
    }

    private static void TestInterfaceStoreLoad()
    {
        var root = RecordRoot("interface-movie-edits");
        WriteRecords(root, InterfaceMovieEditWorkspaceStore.ManifestFileName);
        var store = new InterfaceMovieEditWorkspaceStore(root);
        AssertSameAsSequential(
            () => store.LoadAsync(),
            Paths(root, InterfaceMovieEditWorkspaceStore.ManifestFileName),
            path => InterfaceMovieEditWorkspaceStore.LoadEntryAsync(path),
            exception => exception is IOException or UnauthorizedAccessException or JsonException or
                NotSupportedException or InvalidDataException,
            entries => entries.OrderByDescending(entry => entry.Manifest.CreatedAt).ToArray());
    }

    private static void TestGuidedTextureStoreLoad()
    {
        var root = RecordRoot("texture-mods");
        WriteRecords(root, GuidedTextureModWorkspace.ManifestFileName, packageFolder: true);
        var store = new GuidedTextureModWorkspace(root);
        AssertSameAsSequential(
            () => store.LoadAsync(),
            ProjectPaths(root, GuidedTextureModWorkspace.ManifestFileName),
            path => GuidedTextureModWorkspace.LoadProjectAsync(path),
            exception => exception is IOException or JsonException or NotSupportedException,
            entries => entries.OrderByDescending(entry => entry.Manifest.UpdatedAt)
                .ThenBy(entry => entry.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void TestGuidedPackageStoreLoad()
    {
        var root = RecordRoot("package-mods");
        WriteRecords(root, GuidedTexturePackageWorkspace.ManifestFileName, packageFolder: true);
        var store = new GuidedTexturePackageWorkspace(root);
        AssertSameAsSequential(
            () => store.LoadAsync(),
            ProjectPaths(root, GuidedTexturePackageWorkspace.ManifestFileName),
            path => store.LoadManifestAsync(path),
            exception => exception is IOException or JsonException or NotSupportedException,
            entries => entries.OrderByDescending(entry => entry.Manifest.UpdatedAt).ToArray());
    }

    private static string RecordRoot(string name)
    {
        var root = Path.Combine(fixtureDirectory, "parallel-load", name);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// More records than there are processors, many sharing a time so their order rests on the folder order; one that
    /// is not JSON, one of an unsupported schema, and for project stores one at the top level and one in a folder
    /// starting with an underscore (not a project).
    /// </summary>
    private static void WriteRecords(string root, string fileName, bool packageFolder = false)
    {
        var start = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        var count = Math.Max(150, Environment.ProcessorCount * 6);
        for (var index = 0; index < count; index++)
        {
            var time = start.AddMinutes(index % 7).ToString("O");
            var nested = index % 5 == 0 ? Path.Combine($"edit-{index:D3}", "inner") : $"edit-{index:D3}";
            Write(Path.Combine(root, nested, fileName),
                $$"""{"SchemaVersion":1,"EditId":"edit-{{index}}","ProjectId":"edit-{{index}}","PackageProjectId":"edit-{{index}}","DisplayName":"Edit {{index % 3}}","CreatedAt":"{{time}}","UpdatedAt":"{{time}}"}""");
        }
        Write(Path.Combine(root, "broken", fileName), """{"SchemaVersion":1,"EditId":""");
        Write(Path.Combine(root, "future", fileName), $$"""{"SchemaVersion":99,"EditId":"future","CreatedAt":"{{start:O}}","UpdatedAt":"{{start:O}}"}""");
        if (!packageFolder) return;
        Write(Path.Combine(root, fileName),
            $$"""{"SchemaVersion":1,"ProjectId":"top","PackageProjectId":"top","DisplayName":"Top","CreatedAt":"{{start:O}}","UpdatedAt":"{{start:O}}"}""");
        Write(Path.Combine(root, "_packages", "build", fileName),
            $$"""{"SchemaVersion":1,"ProjectId":"hidden","PackageProjectId":"hidden","CreatedAt":"{{start:O}}","UpdatedAt":"{{start:O}}"}""");

        static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }

    private static string[] Paths(string root, string fileName) =>
        Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).ToArray();

    /// <summary>The project stores' own walk: the top level, then every folder not starting with an underscore.</summary>
    private static string[] ProjectPaths(string root, string fileName) =>
        Directory.EnumerateFiles(root, fileName, SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateDirectories(root)
                .Where(directory => !Path.GetFileName(directory).StartsWith('_'))
                .SelectMany(directory => Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories)))
            .ToArray();

    /// <summary>The store's load, fresh and again from its cache, equals reading each record in turn and sorting.</summary>
    private static void AssertSameAsSequential<T>(
        Func<Task<IReadOnlyList<T>>> load,
        IReadOnlyList<string> paths,
        Func<string, Task<T>> readOne,
        Func<Exception, bool> skip,
        Func<IEnumerable<T>, T[]> order)
    {
        var sequential = new List<T>();
        foreach (var path in paths)
        {
            try { sequential.Add(readOne(path).GetAwaiter().GetResult()); }
            catch (Exception exception) when (skip(exception)) { }
        }
        var expected = order(sequential);
        Assert(expected.Length == paths.Count - 2, $"the reference read kept {expected.Length} of {paths.Count} records");
        for (var pass = 0; pass < 2; pass++)
        {
            var actual = load().GetAwaiter().GetResult();
            Assert(actual.SequenceEqual(expected),
                $"load {pass + 1} returned {actual.Count} records that differ in content or order from the sequential read");
        }
    }
}
