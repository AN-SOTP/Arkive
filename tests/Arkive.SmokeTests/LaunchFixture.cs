using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Assets;
using Arkive.Core.Interface;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.Mods;
using Arkive.Core.Particles;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;

namespace Arkive.SmokeTests;

/// <summary>What a synthetic launch data folder holds. The shares mirror the owner's data: about 15 % of working files
/// belong to enabled mods, 37 % to disabled mods and 48 % to no mod.</summary>
/// <param name="EnabledShare">Of the working files that belong to a mod, the part whose mod is enabled.</param>
/// <param name="BoundShare">Of all working files, the part that belongs to some mod.</param>
internal sealed record LaunchFixtureOptions(
    int MeshEdits = 200,
    int Textures = 60,
    int Effects = 300,
    int Materials = 80,
    int Packages = 60,
    int BuildsPerPackage = 3,
    int Mods = 20,
    double EnabledShare = 0.3,
    double BoundShare = 0.52,
    int Seed = 1)
{
    /// <summary>About the size of the owner's mesh edit records, most of it the skeleton report.</summary>
    public int MeshRecordBytes { get; init; } = 443_000;

    public int SkeletonBones { get; init; } = 240;
}

internal sealed record LaunchFixtureSummary(
    string Root,
    string GamePackagesDirectory,
    int MeshEdits,
    int Textures,
    int Effects,
    int Materials,
    int Packages,
    int Builds,
    int Mods,
    int EnabledMods,
    int EnabledEdits,
    int DisabledEdits,
    int UnboundEdits,
    long MeshRecordBytes);

/// <summary>
/// Builds a data folder of any size without game files, for tests and benchmarks. Records are Arkive's own manifest
/// types written as their stores write them (mods through the .arkive writer and the library's own update), so their
/// format cannot drift; content files (GLB, PNG, UPK) are small dummies. Every name, ID and time comes from the options,
/// so the same options give the same records wherever the folder is.
/// </summary>
internal static class LaunchFixture
{
    private static readonly DateTime FixedTime = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions Camel = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // How the material workspace writes its records.
    private static readonly JsonSerializerOptions Pascal = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record Package(int Index, string Alias, string Physical, string Path, string Sha256);

    private sealed record Edit(UnifiedPackageAssetKind Kind, int Index, string EditId, Package Package, int Group, int Owner);

    public static async Task<LaunchFixtureSummary> CreateAsync(
        string root,
        LaunchFixtureOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException($"The fixture folder must be new or empty: {root}");
        if (options.Packages <= 0) throw new ArgumentException("A launch fixture needs at least one package.");
        var staging = Path.Combine(root, "staging");
        var gamePackages = Path.Combine(root, "game", "EFGame", "ReleasePC", "Packages");
        Directory.CreateDirectory(gamePackages);

        progress?.Report($"Writing {options.Packages:N0} game packages...");
        var packages = Enumerable.Range(0, options.Packages).Select(index =>
        {
            var alias = $"fx_fixture_{index:D4}";
            var physical = $"FXF{index:D4}.upk";
            var path = Path.Combine(gamePackages, physical);
            var bytes = Bytes(options.Seed, "package", index, 4096);
            File.WriteAllBytes(path, bytes);
            return new Package(index, alias, physical, path, Sha(bytes));
        }).ToArray();

        // Which working files belong to an enabled mod (group 0), a disabled mod (1) or none (2), and which mod.
        var enabledMods = options.Mods <= 0 ? 0 : Math.Clamp((int)Math.Round(options.Mods * options.EnabledShare), 1, options.Mods);
        var disabledMods = options.Mods - enabledMods;
        var edits = new List<Edit>();
        void Plan(UnifiedPackageAssetKind kind, int count)
        {
            for (var index = 0; index < count; index++)
            {
                var roll = Unit(options.Seed, kind + "-group", index);
                var group = options.Mods <= 0 || roll >= options.BoundShare ? 2
                    : roll < options.BoundShare * options.EnabledShare || disabledMods == 0 ? 0 : 1;
                var owner = group switch
                {
                    0 => (int)(Unit(options.Seed, kind + "-owner", index) * enabledMods),
                    1 => enabledMods + (int)(Unit(options.Seed, kind + "-owner", index) * disabledMods),
                    _ => -1,
                };
                edits.Add(new Edit(kind, index, Id(options.Seed, kind.ToString(), index), packages[index % packages.Length], group, owner));
            }
        }
        Plan(UnifiedPackageAssetKind.SkeletalMesh, options.MeshEdits);
        Plan(UnifiedPackageAssetKind.Texture2D, options.Textures);
        Plan(UnifiedPackageAssetKind.ParticleSystem, options.Effects);
        Plan(UnifiedPackageAssetKind.MaterialInstanceConstant, options.Materials);

        progress?.Report($"Writing {options.MeshEdits:N0} mesh edits...");
        var meshRecordBytes = WriteMeshes(staging, options, edits.Where(edit => edit.Kind == UnifiedPackageAssetKind.SkeletalMesh).ToArray(), cancellationToken);
        progress?.Report($"Writing {options.Textures:N0} texture projects...");
        WriteTextures(staging, options, edits.Where(edit => edit.Kind == UnifiedPackageAssetKind.Texture2D).ToArray());
        progress?.Report($"Writing {options.Materials:N0} material edits...");
        WriteMaterials(staging, options, edits.Where(edit => edit.Kind == UnifiedPackageAssetKind.MaterialInstanceConstant).ToArray());
        progress?.Report($"Writing {options.Effects:N0} effect edits...");
        var effects = new ParticleEffectEditWorkspaceStore(Path.Combine(staging, "particle-edits"));
        foreach (var edit in edits.Where(edit => edit.Kind == UnifiedPackageAssetKind.ParticleSystem))
            await effects.SaveAsync(Effect(effects, options, edit), cancellationToken).ConfigureAwait(false);

        var workspaces = new ArkiveModEditWorkspaces(
            new GuidedTextureModWorkspace(Path.Combine(staging, "texture-mods")),
            new SkeletalMeshEditWorkspaceStore(Path.Combine(staging, "skeletal-mesh-edits")),
            new StaticMeshEditWorkspaceStore(Path.Combine(staging, "static-mesh-edits")),
            new MaterialInstanceEditWorkspaceStore(Path.Combine(staging, "material-edits")))
        {
            InterfaceMovies = new InterfaceMovieEditWorkspaceStore(Path.Combine(staging, "interface-movie-edits")),
        };
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(workspaces, cancellationToken).ConfigureAwait(false);
        if (snapshot.Edits.Count != edits.Count)
            throw new InvalidDataException($"The fixture wrote {edits.Count:N0} working files but the stores read {snapshot.Edits.Count:N0}.");

        progress?.Report($"Writing {options.Mods:N0} mods...");
        var library = new ArkiveModLibrary(Path.Combine(root, "mods"), Path.Combine(staging, "mod-library"));
        var entries = new List<ArkiveModLibraryEntry>();
        for (var mod = 0; mod < options.Mods; mod++)
        {
            var owned = edits.Where(edit => edit.Owner == mod).ToArray();
            if (owned.Length == 0) continue;
            entries.Add(await WriteModAsync(library, options, mod, mod < enabledMods, owned, snapshot, cancellationToken)
                .ConfigureAwait(false));
        }
        await library.UpdateAsync(current => current with
        {
            GamePackagesDirectory = gamePackages,
            Mods = entries,
            PackageProjectsMigrated = true,
        }, cancellationToken).ConfigureAwait(false);
        // Working files are checked for the build exactly when their mod wins, as Arkive leaves them.
        await ArkiveModSelectionService.SynchronizeAsync(library, workspaces, cancellationToken).ConfigureAwait(false);

        progress?.Report($"Writing {options.Packages:N0} package projects with {options.BuildsPerPackage:N0} build(s) each...");
        var builds = WritePackageProjects(staging, options, packages, edits);
        Write(Path.Combine(root, "cache", "arkive-settings.json"), new
        {
            Version = 8,
            LibraryRootPath = gamePackages,
            CheckForUpdatesOnStartup = false,
        }, new JsonSerializerOptions { WriteIndented = true });

        // One write time for every file, so two fixtures made from the same options look the same.
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, FixedTime);
        return new LaunchFixtureSummary(
            root, gamePackages, options.MeshEdits, options.Textures, options.Effects, options.Materials, packages.Length, builds,
            entries.Count, entries.Count(entry => entry.Enabled),
            edits.Count(edit => edit.Group == 0),
            edits.Count(edit => edit.Group == 1), edits.Count(edit => edit.Group == 2), meshRecordBytes);
    }

    private static long WriteMeshes(string staging, LaunchFixtureOptions options, IReadOnlyList<Edit> edits, CancellationToken cancellationToken)
    {
        if (edits.Count == 0) return 0;
        var bones = Enumerable.Range(0, options.SkeletonBones).Select(BoneName).ToArray();
        long total = 0;
        // The first record decides how many issue lines bring each record to the wanted size.
        var size = JsonSerializer.SerializeToUtf8Bytes(MeshRecord(staging, options, edits[0], bones, 0).Record, Camel).Length;
        var line = JsonSerializer.SerializeToUtf8Bytes(MeshRecord(staging, options, edits[0], bones, 1).Record, Camel).Length - size;
        var padding = Math.Max(0, (options.MeshRecordBytes - size) / Math.Max(1, line));
        Parallel.ForEach(edits, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount), CancellationToken = cancellationToken },
            edit =>
            {
                var mesh = MeshRecord(staging, options, edit, bones, padding);
                Directory.CreateDirectory(Path.GetDirectoryName(mesh.ExportManifestPath)!);
                File.WriteAllBytes(mesh.SourceGlbPath, mesh.SourceGlb);
                File.WriteAllBytes(mesh.ExportManifestPath, JsonSerializer.SerializeToUtf8Bytes(mesh.Export, Camel));
                Directory.CreateDirectory(Path.GetDirectoryName(mesh.RecordPath)!);
                File.WriteAllBytes(mesh.EditedGlbPath, mesh.EditedGlb);
                var record = JsonSerializer.SerializeToUtf8Bytes(mesh.Record, Camel);
                File.WriteAllBytes(mesh.RecordPath, record);
                Interlocked.Add(ref total, record.Length);
            });
        return total;
    }

    private sealed record MeshFiles(
        string ExportManifestPath, string SourceGlbPath, byte[] SourceGlb, SkeletalMeshEditableExportManifest Export,
        string RecordPath, string EditedGlbPath, byte[] EditedGlb, SkeletalMeshEditRegistrationManifest Record);

    private static MeshFiles MeshRecord(string staging, LaunchFixtureOptions options, Edit edit, IReadOnlyList<string> bones, int padding)
    {
        var seed = options.Seed;
        var objectName = $"pc_fixture_{edit.Index:D5}_sk";
        var exportId = Id(seed, "mesh-export", edit.Index);
        var created = Time(edit.Index);
        var exportDirectory = Path.Combine(staging, "skeletal-mesh-reconnaissance", edit.Package.Alias,
            $"{objectName}-{created:yyyyMMdd}-{exportId[..8]}", "editable-glb", exportId);
        var sourceGlb = Bytes(seed, "mesh-source-glb", edit.Index, 2048);
        var editedGlb = Bytes(seed, "mesh-edited-glb", edit.Index, 2048);
        var lods = Enumerable.Range(0, 3).Select(lod => new SkeletalMeshEditableLodManifest(
            lod, $"{objectName}_LOD{lod}", $"{objectName}_LOD{lod}_node", 24_000 >> lod, 96_000 >> lod, 32_000 >> lod, 4, 6, 2, 32,
            0, 1, false, 1, true, 0, 0, 0, 0, 1024L * lod, 65_536L * lod, Hash(seed, "lod-index", edit.Index * 8 + lod),
            Hash(seed, "lod-vertex", edit.Index * 8 + lod), Hash(seed, "lod-id", edit.Index * 8 + lod),
            Hash(seed, "lod-id-uv", edit.Index * 8 + lod), Hash(seed, "lod-position", edit.Index * 8 + lod),
            Hash(seed, "lod-normal", edit.Index * 8 + lod), Hash(seed, "lod-tangent", edit.Index * 8 + lod),
            Hash(seed, "lod-joint", edit.Index * 8 + lod), Hash(seed, "lod-weight", edit.Index * 8 + lod),
            [Hash(seed, "lod-uv0", edit.Index * 8 + lod), Hash(seed, "lod-uv1", edit.Index * 8 + lod)], null,
            new SkeletalMeshEditableBounds(-0.5f, -0.3f, 0, 0.5f, 0.3f, 1.8f))).ToArray();
        var exportManifestPath = Path.Combine(exportDirectory, SkeletalMeshEditableWorkspaceStore.ManifestFileName);
        var export = new SkeletalMeshEditableExportManifest(
            SkeletalMeshEditableExportService.CurrentSchemaVersion, exportId, new DateTimeOffset(created), edit.Package.Path,
            edit.Package.Physical, edit.Package.Alias, 4096, FixedTime.Ticks, edit.Package.Sha256, 100 + edit.Index,
            "SkeletalMesh", objectName, 10_000 + edit.Index, 50_000, Hash(seed, "mesh-source-export", edit.Index), 2,
            Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(exportDirectory)!)!, "arkive-skeletalmesh-recon.json"),
            "lost-ark-western-868-16", Hash(seed, "skeleton", 0), bones.Count, false, "ue3-to-gltf-y-up-metres", "TEXCOORD_1",
            "mesh.glb", Sha(sourceGlb), sourceGlb.Length, 0, 0, lods, 1.25);

        var editId = edit.EditId;
        var recordDirectory = Path.Combine(staging, "skeletal-mesh-edits", exportId, $"{created:yyyyMMdd-HHmmssfff}-{editId}");
        var editedGlbPath = Path.Combine(recordDirectory, objectName + ".edited.glb");
        var target = Profile(seed, exportId, edit, objectName, exportManifestPath, Path.Combine(exportDirectory, "mesh.glb"), Sha(sourceGlb), bones, "target");
        var source = Profile(seed, exportId + "-donor", edit, objectName, exportManifestPath, editedGlbPath, Sha(editedGlb), bones, "source");
        var mappings = bones.Select((bone, index) => new SkeletalMeshBoneCompatibilityMapping(
            index, bone, index, bone, SkeletalMeshBoneMappingKind.ExactNameAndHierarchy, true, index % 3 == 0,
            Math.Round(100d / bones.Count, 6), Unit(seed, "bind-translation", index) * 0.001, Unit(seed, "bind-rotation", index) * 0.05,
            "Same name and parent chain."))
            .ToArray();
        var compatibility = new SkeletalMeshSkeletonCompatibilityReport(
            1, new DateTimeOffset(created), SkeletalMeshSkeletonCompatibilityKind.ExactTargetSkeleton, true, false, true,
            "The edited mesh uses the target's exact skeleton: every bone maps by name and hierarchy.", source, target,
            bones.Count, bones.Count, 0, 0, 0, 100, 100, 0, 0.0004, 0.0002, 0.03, mappings, []);
        var issues = Enumerable.Range(0, padding).Select(index => new SkeletalMeshEditIssue(
            SkeletalMeshEditIssueSeverity.Info, $"SKP{index % 1000:D3}",
            $"LOD0 section {index % 12} chunk {index % 7}: {bones[index % bones.Count]} keeps its influence on " +
            $"{1000 + index % 997} vertices; skin weights sum to 1 within 0.0001.")).ToArray();
        var preflight = new SkeletalMeshEditPreflightReport(
            2, new DateTimeOffset(created), exportId, edit.Package.Alias, objectName, exportManifestPath,
            Path.Combine(exportDirectory, "mesh.glb"), Sha(sourceGlb), editedGlbPath, Sha(editedGlb), editedGlb.Length,
            SkeletalMeshEditReadiness.ReadyForPositionTangentEncoding, true, true, true, 0, 18_000, 18_000, 18_000, 0.012, 0, 0,
            lods.Select(lod => new SkeletalMeshEditLodPreflight(
                lod.LodIndex, lod.VertexCount, lod.VertexCount, lod.TriangleCount, lod.TriangleCount, lod.SectionCount,
                lod.SectionCount, "TEXCOORD_1", lod.VertexCount * 3 / 4, 0.012, 0.004, lod.VertexCount / 2, lod.VertexCount / 2,
                true, true, true, true, true, true)).ToArray(),
            issues, 3.5)
        {
            AnimationValidation = new SkeletalMeshAnimationPreflight(
                "pose-stress-v2", true, false, false, 12, bones.Count, bones.Count, 80, 0, 0, 0.001, 0.002, 0.001, 0.0001,
                0.0001, 1, 0, 1.02, 1.1, 0, 0, Enumerable.Range(0, 80).ToArray(),
                Enumerable.Range(0, 12).Select(pose => new SkeletalMeshAnimationPosePreflight(
                    $"stress-{pose:D2}", 20, 24_000, 24_000, 32_000, 32_000, 0, 0.2, 0.2, 1.05, 1.05, 1.3, 1.3, 0, 0, 0.1, 0.1))
                    .ToArray()),
            SkeletonCompatibility = compatibility,
        };
        var record = new SkeletalMeshEditRegistrationManifest(
            SkeletalMeshEditWorkspaceStore.CurrentSchemaVersion, editId, new DateTimeOffset(created),
            $"Fixture mesh {edit.Index:D5}", exportId, exportManifestPath, Sha(sourceGlb), edit.Package.Alias, objectName,
            Path.Combine("C:\\", "Users", "fixture", "Documents", objectName + ".glb"), Path.GetFileName(editedGlbPath),
            Sha(editedGlb), editedGlb.Length, preflight)
        {
            IncludedInPackageBuild = false,
        };
        return new MeshFiles(exportManifestPath, Path.Combine(exportDirectory, "mesh.glb"), sourceGlb, export,
            Path.Combine(recordDirectory, SkeletalMeshEditWorkspaceStore.ManifestFileName), editedGlbPath, editedGlb, record);
    }

    private static SkeletalMeshSkeletonProfile Profile(
        int seed, string exportId, Edit edit, string objectName, string manifestPath, string glbPath, string glbSha256,
        IReadOnlyList<string> bones, string side) =>
        new(exportId, edit.Package.Alias, objectName, manifestPath, glbPath, glbSha256, bones.Count, 1, bones.Count, bones.Count / 3,
            Hash(seed, "hierarchy", 0), Hash(seed, side + "-bind", edit.Index),
            bones.Select((bone, index) => new SkeletalMeshSkeletonBoneProfile(
                index, bone, bone.ToLowerInvariant(), index == 0 ? -1 : (index - 1) / 2, index == 0 ? string.Empty : bones[(index - 1) / 2],
                Depth(index), HierarchyPath(bones, index), index % 3 == 0, Math.Round(100d / bones.Count, 6),
                Math.Round(Unit(seed, side + "-x", index) - 0.5, 6), Math.Round(Unit(seed, side + "-y", index) - 0.5, 6),
                Math.Round(Unit(seed, side + "-z", index) * 1.8, 6))).ToArray());

    private static int Depth(int index)
    {
        var depth = 0;
        for (; index > 0; index = (index - 1) / 2) depth++;
        return depth;
    }

    private static string HierarchyPath(IReadOnlyList<string> bones, int index)
    {
        var chain = new List<string>();
        for (var at = index; ; at = (at - 1) / 2)
        {
            chain.Add(bones[at]);
            if (at == 0) break;
        }
        chain.Reverse();
        return string.Join("/", chain);
    }

    private static readonly string[] BoneParts =
        ["Root", "Pelvis", "Spine", "Chest", "Neck", "Head", "Clavicle", "UpperArm", "Forearm", "Hand", "Finger", "Thigh", "Calf", "Foot", "Toe", "Skirt", "Hair", "Cloth"];

    private static string BoneName(int index) => $"{BoneParts[index % BoneParts.Length]}_{index}";

    private static void WriteTextures(string staging, LaunchFixtureOptions options, IReadOnlyList<Edit> edits)
    {
        foreach (var edit in edits)
        {
            var objectName = $"tex_fixture_{edit.Index:D5}_d";
            var directory = Path.Combine(staging, "texture-mods", edit.Package.Alias, $"{objectName}-{edit.EditId[..8]}");
            var native = Path.Combine(directory, "native", NativeTextureExportService.ManifestFileName);
            var image = Path.Combine(directory, "editable", objectName + "-edit.png");
            Directory.CreateDirectory(Path.GetDirectoryName(image)!);
            File.WriteAllBytes(image, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Bytes(options.Seed, "png", edit.Index, 1024)]);
            var created = new DateTimeOffset(Time(edit.Index));
            Write(native, new NativeTextureExportManifest(
                NativeTextureExportService.CurrentManifestSchemaVersion, Id(options.Seed, "texture-export", edit.Index), created,
                edit.Package.Path, edit.Package.Physical, edit.Package.Alias, 4096, FixedTime.Ticks, edit.Package.Sha256,
                "lost-ark-western-global", 200 + edit.Index, 201 + edit.Index, "Texture2D", objectName, 20_000 + edit.Index, 1_400_000,
                Hash(options.Seed, "texture-payload", edit.Index), 40, 120, "PF_DXT5", "crn", 1024, 1024, 1024, 1024, 1,
                "crunch", "texture.dds", 1_398_256, Hash(options.Seed, "texture-dds", edit.Index), [],
                Path.Combine(directory, "native", "arkive-texture-recon.json"), 0.4), Camel);
            Write(Path.Combine(directory, GuidedTextureModWorkspace.ManifestFileName), new GuidedTextureModProjectManifest(
                GuidedTextureModWorkspace.CurrentSchemaVersion, edit.EditId, created, created, $"Fixture texture {edit.Index:D5}",
                edit.Package.Alias, edit.Package.Physical, edit.Package.Path, "Texture2D", objectName, 200 + edit.Index, "PF_DXT5",
                1024, 1024, native, image, GuidedTextureModState.Editing, null, [], false), Camel);
        }
    }

    private static void WriteMaterials(string staging, LaunchFixtureOptions options, IReadOnlyList<Edit> edits)
    {
        foreach (var edit in edits)
        {
            var objectName = $"mi_fixture_{edit.Index:D5}";
            var created = new DateTimeOffset(Time(edit.Index));
            var recon = Path.Combine(staging, "material-reconnaissance", edit.Package.Alias, $"{300 + edit.Index:D6}-{objectName}", "arkive-material-recon.json");
            Write(recon, new MaterialInstanceReconnaissanceReport(
                1, created, edit.Package.Path, edit.Package.Physical, edit.Package.Alias, 4096, FixedTime.Ticks, edit.Package.Sha256,
                "lost-ark-western-global", 300 + edit.Index, 301 + edit.Index, AssetKind.Material, "MaterialInstanceConstant",
                objectName, 30_000 + edit.Index, 900, Hash(options.Seed, "material-export", edit.Index), 16, 880, 12, "Material'base'",
                [], [], [], [], MaterialInstanceReadiness.Editable, []), Pascal);
            var document = new MaterialInstanceEditDocument(
                MaterialInstanceEditService.CurrentEditSchemaVersion, edit.EditId, created, $"Fixture material {edit.Index:D5}", recon,
                edit.Package.Path, edit.Package.Sha256, edit.Package.Alias, 300 + edit.Index, "MaterialInstanceConstant", objectName,
                30_000 + edit.Index, 900, Hash(options.Seed, "material-export", edit.Index),
                [new MaterialScalarEdit("Roughness", 0.5f, 0.25f + edit.Index % 50 / 100f), new MaterialScalarEdit("Metallic", 0, 0)],
                [new MaterialVectorEdit("Tint", 1, 1, 1, 1, 1, 0.2f, 0.2f, 1)], [])
            {
                IncludedInPackageBuild = false,
            };
            Write(Path.Combine(staging, "material-edits", edit.EditId, MaterialInstanceEditWorkspaceStore.DocumentFileName), document, Pascal);
        }
    }

    private static ParticleEffectEditEntry Effect(ParticleEffectEditWorkspaceStore store, LaunchFixtureOptions options, Edit edit)
    {
        var systemName = $"ps_fixture_{edit.Index:D5}";
        var patches = Enumerable.Range(0, 8).Select(patch =>
        {
            var original = Enumerable.Range(0, 32).Select(value => (float)Math.Round(Unit(options.Seed, "effect-value", edit.Index * 1000 + patch * 32 + value), 4)).ToArray();
            return new ParticleEffectPatch(
                $"{systemName}.Emitter_{patch}.ColorOverLife", "DistributionVectorConstantCurve", 400 + patch, 40_000 + patch * 512, 512,
                Hash(options.Seed, "effect-export", edit.Index * 16 + patch), "ColorOverLife", 64, original,
                original.Select(value => value * 0.5f).ToArray());
        }).ToArray();
        var created = new DateTimeOffset(Time(edit.Index));
        var directory = Path.Combine(store.RootDirectory, edit.EditId);
        return new ParticleEffectEditEntry(directory, Path.Combine(directory, ParticleEffectEditWorkspaceStore.DocumentFileName),
            new ParticleEffectEditDocument(
                ParticleEffectEditWorkspaceStore.CurrentSchemaVersion, edit.EditId, $"Fixture effect {edit.Index:D5}", edit.Package.Path,
                edit.Package.Physical, edit.Package.Alias, edit.Package.Sha256, systemName, systemName, 400 + edit.Index,
                Hash(options.Seed, "effect-system", edit.Index), new ParticleEffectRecipe(new ParticleEffectTint(1, 0.1f, 0.1f)),
                patches, false, created, created));
    }

    private static async Task<ArkiveModLibraryEntry> WriteModAsync(
        ArkiveModLibrary library, LaunchFixtureOptions options, int index, bool enabled, IReadOnlyList<Edit> owned,
        ArkiveModEditSnapshot snapshot, CancellationToken cancellationToken)
    {
        var captured = new List<ArkiveModCapturedAsset>();
        foreach (var edit in owned)
        {
            var local = snapshot.Find(edit.EditId) ??
                        throw new InvalidDataException($"The fixture's working file {edit.EditId} could not be read back.");
            captured.Add(await ArkiveModAuthoringService.CaptureAsync(local, Id(options.Seed, "asset-" + edit.Kind, edit.Index), cancellationToken)
                .ConfigureAwait(false));
        }
        var modId = Id(options.Seed, "mod", index);
        var name = $"Fixture mod {index:D3}";
        var time = new DateTimeOffset(FixedTime);
        var path = Path.Combine(library.ModsDirectory, ArkiveModLibrary.SuggestFileName(name));
        var written = await ArkiveModFileService.WriteAsync(
                path, ArkiveModAuthoringService.CreateManifest(modId, new ArkiveModMetadata(name, "1.0.0", "Fixture"), time, time),
                captured.Select(asset => new ArkiveModPackAsset(asset.Descriptor, asset.Files)).ToArray(), cancellationToken)
            .ConfigureAwait(false);
        File.SetLastWriteTimeUtc(path, FixedTime);
        var bindings = captured.Select(item =>
        {
            var asset = written.Manifest.Assets.Single(candidate => candidate.AssetId == item.Descriptor.AssetId);
            return new ArkiveModAssetBinding(asset.AssetId, asset.Kind, item.Edit.EditId, item.Edit.SourcePackagePath,
                ArkiveModFileService.ComputeContentSha256(asset));
        }).ToArray();
        return new ArkiveModLibraryEntry(modId, Path.GetFileName(path), written.Sha256, written.Bytes, time, enabled,
            index * 10, ArkiveModOrigin.Created, written.Manifest, bindings);
    }

    private static int WritePackageProjects(string staging, LaunchFixtureOptions options, IReadOnlyList<Package> packages, IReadOnlyList<Edit> edits)
    {
        var workspace = Path.Combine(staging, "texture-mods", "_packages");
        var builds = 0;
        foreach (var package in packages)
        {
            var projectId = Id(options.Seed, "package-project", package.Index);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(package.Path.ToUpperInvariant())))[..12];
            var meshes = edits.Where(edit => edit.Package == package && edit.Group == 0 && edit.Kind == UnifiedPackageAssetKind.SkeletalMesh).ToArray();
            var revisions = new List<GuidedTexturePackageRevision>();
            for (var build = 0; build < options.BuildsPerPackage; build++)
            {
                var created = Time(package.Index * 10 + build);
                var revisionId = $"{created:yyyyMMdd-HHmmss}-{Id(options.Seed, "revision", package.Index * 100 + build)}"[..25];
                var directory = Path.Combine(workspace, "_r", projectId[..8], revisionId);
                var payload = Path.Combine(directory, "bundle", "payload", "EFGame", "ReleasePC", "Packages", package.Physical);
                var payloadBytes = Bytes(options.Seed, "payload", package.Index * 100 + build, 4096);
                Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
                File.WriteAllBytes(payload, payloadBytes);
                var bundle = Path.Combine(directory, "bundle", ArkiveModBundleService.BundleManifestFileName);
                Write(bundle, new ArkiveModBundleManifest(
                    ArkiveModBundleService.CurrentManifestSchemaVersion, Id(options.Seed, "bundle", package.Index * 100 + build),
                    new DateTimeOffset(created), $"{package.Alias} build {build}", ArkiveModBundleService.WesternGlobalProfile,
                    revisionId, Path.Combine("EFGame", "ReleasePC", "Packages", package.Physical), package.Physical, 4096,
                    package.Sha256, Path.Combine("payload", "EFGame", "ReleasePC", "Packages", package.Physical), payloadBytes.Length,
                    Sha(payloadBytes), ArkiveModBundleService.ReadinessManifestFileName, [], true, true, true, true, true, false, []), Camel);
                revisions.Add(new GuidedTexturePackageRevision(
                    revisionId, new DateTimeOffset(created), package.Sha256, [], Path.Combine(directory, "05-staged-upk", package.Physical),
                    Path.Combine(directory, "arkive-replacement-upk.json"), bundle,
                    Path.Combine(directory, "bundle", ArkiveModBundleService.ReadinessManifestFileName), payload, payloadBytes.Length,
                    Sha(payloadBytes), 12.5)
                {
                    MeshAssets = meshes.Select(edit => new GuidedMeshPackageAssetRevision(
                        edit.EditId, $"Fixture mesh {edit.Index:D5}", $"pc_fixture_{edit.Index:D5}_sk", 100 + edit.Index,
                        Sha(Bytes(options.Seed, "mesh-edited-glb", edit.Index, 2048)), string.Empty, string.Empty, string.Empty,
                        string.Empty, 10_000 + edit.Index, 50_000)).ToArray(),
                });
                builds++;
            }
            var time = new DateTimeOffset(Time(package.Index));
            Write(Path.Combine(workspace, $"{package.Alias}-{key}", GuidedTexturePackageWorkspace.ManifestFileName),
                new GuidedTexturePackageManifest(
                    GuidedTexturePackageWorkspace.CurrentSchemaVersion, projectId, time, time, package.Alias, package.Physical,
                    package.Path, revisions.LastOrDefault()?.RevisionId, revisions)
                {
                    ClientBaselineSha256 = package.Sha256,
                }, Camel);
        }
        return builds;
    }

    private static void Write<T>(string path, T value, JsonSerializerOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, options));
    }

    private static DateTime Time(int index) => FixedTime.AddMinutes(-index);

    private static string Id(int seed, string kind, int index) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{seed}:{kind}:{index}"))).ToLowerInvariant();

    private static string Hash(int seed, string kind, int index) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{kind}:{index}")));

    private static double Unit(int seed, string kind, int index) =>
        BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{kind}:{index}"))) / (uint.MaxValue + 1d);

    private static byte[] Bytes(int seed, string kind, int index, int length)
    {
        // A seeded Random gives the same bytes in every process; string hash codes would not.
        var bytes = new byte[length];
        new Random(StableHash($"{seed}:{kind}:{index}")).NextBytes(bytes);
        return bytes;
    }

    private static int StableHash(string text) => BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string Describe(LaunchFixtureSummary summary) => string.Create(CultureInfo.InvariantCulture,
        $"{summary.Root}: {summary.MeshEdits:N0} mesh edits ({summary.MeshRecordBytes / 1048576d:N0} MB of records), " +
        $"{summary.Textures:N0} textures, {summary.Effects:N0} effects, {summary.Materials:N0} materials, " +
        $"{summary.Packages:N0} package projects with {summary.Builds:N0} builds, {summary.Mods:N0} mods " +
        $"({summary.EnabledMods:N0} enabled); working files: {summary.EnabledEdits:N0} in enabled mods, " +
        $"{summary.DisabledEdits:N0} in disabled mods, {summary.UnboundEdits:N0} in none.");
}
