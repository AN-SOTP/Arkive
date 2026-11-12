using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Arkive.SmokeTests;

/// <summary>Edited-mesh records without the full skeleton report: loading, reading it on demand, compaction.</summary>
internal static class MeshRecordSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("loads an older edited-mesh record without its skeleton report and every other value unchanged", TestFatRecordLoad),
        new("reads the skeleton report on demand from an older record and from a compacted one alike", TestReportOnDemand),
        new("compacts an edited-mesh record exactly and leaves nothing behind when any step fails", TestCompactionFailures),
        new("does not compact an edited-mesh record changed in the last hour", TestRecentRecordSkipped),
        new("keeps a compacted edited-mesh record complete for Arkive 0.7.5", TestOldVersionReadsCompacted),
    ];

    // As Arkive 0.7.5 writes and reads records: every property, with no knowledge of the summary.
    private static readonly JsonSerializerOptions OldVersionOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                typeInfo =>
                {
                    if (typeInfo.Type != typeof(SkeletalMeshEditPreflightReport)) return;
                    var summary = typeInfo.Properties.Single(property => property.Name == "skeletonCompatibilitySummary");
                    typeInfo.Properties.Remove(summary);
                },
            },
        },
    };

    private static readonly JsonSerializerOptions CompareOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static bool SameJson<T>(T left, T right) =>
        JsonSerializer.Serialize(left, CompareOptions) == JsonSerializer.Serialize(right, CompareOptions);

    /// <summary>A record or build manifest on disk holds no report, and the file beside it holds the full one.</summary>
    internal static void AssertSlimWithReportBeside(string manifestPath, SkeletalMeshEditPreflightReport preflight, string what)
    {
        var node = JsonNode.Parse(File.ReadAllText(manifestPath))!["preflight"]!.AsObject();
        Assert(preflight.SkeletonCompatibility is not null &&
               node["skeletonCompatibility"] is null &&
               node["skeletonCompatibilitySummary"]?["compatibility"]?.GetValue<string>() ==
               preflight.SkeletonCompatibility.Compatibility.ToString(),
            $"{what} still holds its skeleton report, or lacks its summary");
        var beside = SkeletalMeshSkeletonCompatibilityFile.ReadAsync(
                Path.GetDirectoryName(manifestPath)!, preflight.CreatedAt, preflight.CandidateGlbSha256)
            .GetAwaiter().GetResult();
        Assert(SameJson(beside, preflight.SkeletonCompatibility),
            $"{what} has no identical skeleton report beside it");
    }

    private static void TestFatRecordLoad()
    {
        var root = NewRoot("fat-load");
        var (path, original) = WriteFatRecord(root, "fat");
        var store = new SkeletalMeshEditWorkspaceStore(root);
        var snapshot = store.LoadAsync().GetAwaiter().GetResult();
        Assert(snapshot.Issues.Count == 0 && snapshot.Entries is [{ } entry] &&
               entry.ManifestPath.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase),
            "an older edited-mesh record did not load");
        var loaded = snapshot.Entries[0].Manifest;
        Assert(loaded.Preflight.SkeletonCompatibility is null,
            "loading an older record built its full skeleton report");
        Assert(SameJson(loaded.Preflight.SkeletonCompatibilitySummary,
                SkeletalMeshSkeletonCompatibilitySummary.From(original.Preflight.SkeletonCompatibility!)),
            "the skeleton values the list shows were not read from an older record");
        Assert(SameJson(loaded, WithoutReport(original)),
            "an older record loaded with a value changed besides its skeleton report");
        Directory.Delete(root, recursive: true);
    }

    private static void TestReportOnDemand()
    {
        var root = NewRoot("on-demand");
        var (path, original) = WriteFatRecord(root, "fat");
        var report = original.Preflight.SkeletonCompatibility!;
        var store = new SkeletalMeshEditWorkspaceStore(root);
        var fat = store.LoadEntryAsync(path).GetAwaiter().GetResult();
        Assert(SameJson(store.LoadSkeletonCompatibilityAsync(fat).GetAwaiter().GetResult(), report),
            "the skeleton report read on demand from an older record differs from the original");

        var compacted = store.CompactAsync(path).GetAwaiter().GetResult();
        Assert(compacted.Outcome == SkeletalMeshEditCompactionOutcome.Compacted &&
               compacted.BytesAfter < compacted.BytesBefore,
            $"an older record was not compacted: {compacted.Outcome} {compacted.Problem}");
        AssertSlimWithReportBeside(path, original.Preflight, "a compacted record");
        var slim = store.LoadEntryAsync(path).GetAwaiter().GetResult();
        Assert(SameJson(slim.Manifest, fat.Manifest),
            "a compacted record loads differently from the record it replaced");
        Assert(SameJson(store.LoadSkeletonCompatibilityAsync(slim).GetAwaiter().GetResult(), report) &&
               SameJson(new SkeletalMeshEditWorkspaceStore(root).LoadSkeletonCompatibilityAsync(slim).GetAwaiter().GetResult(), report),
            "the skeleton report read on demand from a compacted record differs from the original");
        Assert(store.CompactAsync(path).GetAwaiter().GetResult().Outcome == SkeletalMeshEditCompactionOutcome.AlreadySlim,
            "a compacted record was compacted again");

        // Saving a loaded older record moves its report beside it instead of dropping it.
        var (otherPath, other) = WriteFatRecord(root, "saved-again");
        var otherEntry = store.LoadEntryAsync(otherPath).GetAwaiter().GetResult();
        var excluded = store.SetIncludedAsync(otherEntry, false).GetAwaiter().GetResult();
        AssertSlimWithReportBeside(otherPath, other.Preflight, "an older record saved again");
        Assert(SameJson(store.LoadSkeletonCompatibilityAsync(excluded).GetAwaiter().GetResult(), other.Preflight.SkeletonCompatibility) &&
               store.LoadEntryAsync(otherPath).GetAwaiter().GetResult().Manifest is { IncludedInPackageBuild: false },
            "saving a loaded older record lost its skeleton report or its change");

        // Arkive 0.7.5 saving a compacted record drops the summary it does not know: the list reads it from beside.
        var asOldVersion = JsonSerializer.Deserialize<SkeletalMeshEditRegistrationManifest>(File.ReadAllText(path), OldVersionOptions)!;
        File.WriteAllText(path, JsonSerializer.Serialize(asOldVersion with { IncludedInPackageBuild = false }, OldVersionOptions));
        var savedByOldVersion = new SkeletalMeshEditWorkspaceStore(root).LoadEntryAsync(path).GetAwaiter().GetResult();
        Assert(SameJson(savedByOldVersion.Manifest.Preflight.SkeletonCompatibilitySummary,
                   SkeletalMeshSkeletonCompatibilitySummary.From(report)) &&
               SameJson(store.LoadSkeletonCompatibilityAsync(savedByOldVersion).GetAwaiter().GetResult(), report),
            "a compacted record saved again by Arkive 0.7.5 lost its skeleton values");

        // A report beside a record belongs to one check: a later check without one must not show it.
        var rechecked = asOldVersion with { Preflight = asOldVersion.Preflight with { CreatedAt = DateTimeOffset.UtcNow } };
        File.WriteAllText(path, JsonSerializer.Serialize(rechecked, OldVersionOptions));
        var stale = new SkeletalMeshEditWorkspaceStore(root).LoadEntryAsync(path).GetAwaiter().GetResult();
        Assert(stale.Manifest.Preflight.SkeletonCompatibilitySummary is null &&
               store.LoadSkeletonCompatibilityAsync(stale).GetAwaiter().GetResult() is null,
            "an earlier check's skeleton report was shown for a later check");
        Directory.Delete(root, recursive: true);
    }

    private static void TestCompactionFailures()
    {
        var root = NewRoot("compaction-failures");
        var store = new SkeletalMeshEditWorkspaceStore(root);
        foreach (var step in Enum.GetValues<SkeletalMeshEditCompactionStep>())
        {
            var (path, _) = WriteFatRecord(root, "step-" + step);
            var directory = Path.GetDirectoryName(path)!;
            var before = Listing(directory);
            var folderTime = Directory.GetLastWriteTimeUtc(directory);
            var result = store.CompactAsync(path, afterStep: reached =>
            {
                if (reached == step) throw new IOException($"Simulated failure after {step}.");
            }).GetAwaiter().GetResult();
            Assert(result.Outcome == SkeletalMeshEditCompactionOutcome.Failed &&
                   result.Problem?.Contains(step.ToString(), StringComparison.Ordinal) == true,
                $"a failure after {step} was not reported: {result.Outcome} {result.Problem}");
            Assert(Listing(directory) == before && Directory.GetLastWriteTimeUtc(directory) == folderTime,
                $"a failure after {step} left the record changed or a file behind");
        }

        // The dry run changes nothing and predicts what a real run saves.
        var (dryPath, _) = WriteFatRecord(root, "dry-run");
        var dryDirectory = Path.GetDirectoryName(dryPath)!;
        var unchanged = Listing(dryDirectory);
        var dry = store.CompactAsync(dryPath, dryRun: true).GetAwaiter().GetResult();
        Assert(dry.Outcome == SkeletalMeshEditCompactionOutcome.Compacted && Listing(dryDirectory) == unchanged,
            "a dry run changed a record");

        // Exact: every value but the report is unchanged, in the file as written, and the write time is kept.
        var writeTime = File.GetLastWriteTimeUtc(dryPath);
        var dryFolderTime = Directory.GetLastWriteTimeUtc(dryDirectory);
        var fatNode = JsonNode.Parse(File.ReadAllText(dryPath))!;
        var real = store.CompactAsync(dryPath).GetAwaiter().GetResult();
        var slimNode = JsonNode.Parse(File.ReadAllText(dryPath))!;
        fatNode["preflight"]!.AsObject().Remove("skeletonCompatibility");
        slimNode["preflight"]!.AsObject().Remove("skeletonCompatibilitySummary");
        Assert(real.Outcome == SkeletalMeshEditCompactionOutcome.Compacted && JsonNode.DeepEquals(fatNode, slimNode),
            "compaction changed a value besides the skeleton report");
        Assert(File.GetLastWriteTimeUtc(dryPath) == writeTime && Directory.GetLastWriteTimeUtc(dryDirectory) == dryFolderTime &&
               File.GetLastWriteTimeUtc(SkeletalMeshSkeletonCompatibilityFile.PathIn(dryDirectory)) == writeTime,
            "compaction made the record or its folder look recently changed");
        Assert(Math.Abs(real.BytesAfter - dry.BytesAfter) < 4096 && real.BytesBefore == dry.BytesBefore,
            $"the dry run predicted {dry.BytesAfter:N0} bytes after; the real run left {real.BytesAfter:N0}");
        Assert(Directory.GetFiles(dryDirectory).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal)
                   .SequenceEqual(new[] { SkeletalMeshEditWorkspaceStore.ManifestFileName, "edit.edited.glb",
                       SkeletalMeshSkeletonCompatibilityFile.FileName }.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "compaction left a temporary file behind");

        var turns = 0;
        var summary = store.CompactAllAsync(dryRun: false, _ =>
        {
            turns++;
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
        Assert(summary is { Records: 6, Compacted: 5, AlreadySlim: 1, Failed: 0, Stopped: false } && summary.BytesSaved > 0 &&
               turns == 5,
            "compacting the whole store did not compact the records left: " + JsonSerializer.Serialize(summary));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert(store.CompactAllAsync(false, null, cancelled.Token).GetAwaiter().GetResult() is { Stopped: true, Compacted: 0 },
            "a cancelled compaction did not stop cleanly");
        Directory.Delete(root, recursive: true);
    }

    private static void TestRecentRecordSkipped()
    {
        var root = NewRoot("compaction-recent");
        var (path, _) = WriteFatRecord(root, "recent", age: TimeSpan.FromMinutes(20));
        var bytes = File.ReadAllBytes(path);
        var store = new SkeletalMeshEditWorkspaceStore(root);
        Assert(store.CompactAsync(path).GetAwaiter().GetResult().Outcome == SkeletalMeshEditCompactionOutcome.RecentlyChanged &&
               store.CompactAllAsync(false).GetAwaiter().GetResult() is { RecentlyChanged: 1, Compacted: 0 } &&
               File.ReadAllBytes(path).SequenceEqual(bytes) &&
               !File.Exists(SkeletalMeshSkeletonCompatibilityFile.PathIn(Path.GetDirectoryName(path)!)),
            "a record changed in the last hour was compacted");
        Directory.Delete(root, recursive: true);
    }

    private static void TestOldVersionReadsCompacted()
    {
        var root = NewRoot("compaction-old-version");
        var (path, original) = WriteFatRecord(root, "old-version");
        var fatAsOld = JsonSerializer.Deserialize<SkeletalMeshEditRegistrationManifest>(File.ReadAllText(path), OldVersionOptions)!;
        var store = new SkeletalMeshEditWorkspaceStore(root);
        Assert(store.CompactAsync(path).GetAwaiter().GetResult().Outcome == SkeletalMeshEditCompactionOutcome.Compacted,
            "the record was not compacted");
        var slimText = File.ReadAllText(path);
        var slimAsOld = JsonSerializer.Deserialize<SkeletalMeshEditRegistrationManifest>(slimText, OldVersionOptions)!;
        Assert(slimAsOld.Preflight.SkeletonCompatibility is null &&
               SameJson(slimAsOld, fatAsOld with { Preflight = fatAsOld.Preflight with { SkeletonCompatibility = null } }),
            "Arkive 0.7.5 would read a compacted record differently besides its skeleton report");
        // Every property 0.7.5 knows is still written; the only one it does not know is the summary.
        var known = Names(JsonNode.Parse(JsonSerializer.Serialize(
            original with { Preflight = original.Preflight with { SkeletonCompatibility = null } }, OldVersionOptions))!).Where(name => !name.EndsWith(".skeletonCompatibility", StringComparison.Ordinal));
        var written = Names(JsonNode.Parse(slimText)!).ToHashSet(StringComparer.Ordinal);
        Assert(known.All(written.Contains) &&
               written.Except(known).All(name => name.Contains("skeletonCompatibilitySummary", StringComparison.Ordinal)),
            "a compacted record is missing a property Arkive 0.7.5 reads");
        Directory.Delete(root, recursive: true);
    }

    private static IEnumerable<string> Names(JsonNode node, string prefix = "")
    {
        if (node is not JsonObject value) yield break;
        foreach (var (name, child) in value)
        {
            yield return prefix + "." + name;
            if (child is not null)
                foreach (var nested in Names(child, prefix + "." + name))
                    yield return nested;
        }
    }

    private static SkeletalMeshEditRegistrationManifest WithoutReport(SkeletalMeshEditRegistrationManifest manifest) =>
        manifest with
        {
            Preflight = manifest.Preflight with
            {
                SkeletonCompatibility = null,
                SkeletonCompatibilitySummary = SkeletalMeshSkeletonCompatibilitySummary.From(manifest.Preflight.SkeletonCompatibility!),
            },
        };

    private static string NewRoot(string name)
    {
        var root = Path.Combine(fixtureDirectory, "mesh-records", name);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        return root;
    }

    // The folder's files, sizes, contents and write times, to prove a failed compaction changed nothing.
    private static string Listing(string directory) => string.Join("\n", Directory.GetFiles(directory)
        .Order(StringComparer.Ordinal)
        .Select(file => $"{Path.GetFileName(file)}|{new FileInfo(file).Length}|{File.GetLastWriteTimeUtc(file).Ticks}|" +
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));

    /// <summary>A record as Arkive 0.7.5 wrote it, full skeleton report inside, last changed two hours ago.</summary>
    private static (string Path, SkeletalMeshEditRegistrationManifest Manifest) WriteFatRecord(
        string root, string name, TimeSpan? age = null)
    {
        var directory = Path.Combine(root, "export-" + name, "20261029-185353997-" + name);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "edit.edited.glb"), [1, 2, 3, 4]);
        var created = new DateTimeOffset(2026, 10, 29, 18, 53, 53, TimeSpan.Zero).AddTicks(9_971_234);
        var issues = new[]
        {
            new SkeletalMeshEditIssue(SkeletalMeshEditIssueSeverity.Info, "SKC001", "Exact target skeleton — “quoted” é."),
            new SkeletalMeshEditIssue(SkeletalMeshEditIssueSeverity.Warning, "SMI038", "Ignored 2 shape key target(s)."),
        };
        var bones = Enumerable.Range(0, 240).Select(index => new SkeletalMeshSkeletonBoneProfile(
            index, $"bip001_bone_{index}", $"bone_{index}", index - 1, index == 0 ? "" : $"bip001_bone_{index - 1}",
            index % 12, $"root/bone_{index}", index % 3 == 0, index * 0.0123456789, index * 0.1, -index * 0.2,
            index / 3.0)).ToArray();
        var profile = new SkeletalMeshSkeletonProfile(
            "export-" + name, "pc_wr_f_av_178b", "pc_wr_f_av_178b_dress_sk", @"D:\staging\source.json",
            @"D:\staging\source.glb", new string('A', 64), bones.Length, 1, 180, 60, new string('B', 64),
            new string('C', 64), bones);
        var report = new SkeletalMeshSkeletonCompatibilityReport(
            1, created.AddSeconds(-1), SkeletalMeshSkeletonCompatibilityKind.ExactTargetSkeleton, true, true, false,
            "The candidate uses the exact target skeleton.", profile, profile with { ExportId = "candidate" },
            240, 240, 0, 0, 0, 99.99999999, 99.9, 0.0000001, 1e-7, 2.5e-9, 0.000123,
            bones.Select(bone => new SkeletalMeshBoneCompatibilityMapping(
                bone.BoneIndex, bone.Name, bone.BoneIndex, bone.Name, SkeletalMeshBoneMappingKind.ExactNameAndHierarchy,
                true, bone.IsCoreDeformationBone, bone.WeightedVertexMassPercent, 1.5e-8, 0.001, "Exact.")).ToArray(),
            issues[..1]);
        var preflight = new SkeletalMeshEditPreflightReport(
            1, created, "export-" + name, "pc_wr_f_av_178b", "pc_wr_f_av_178b_dress_sk", @"D:\staging\source.json",
            @"D:\staging\source.glb", new string('A', 64), Path.Combine(directory, "edit.edited.glb"),
            new string('D', 64), 4, SkeletalMeshEditReadiness.ReadyForPositionTangentEncoding, true, true, true, 0,
            12, 3, 0, 0.0125, 0, 1,
            [new SkeletalMeshEditLodPreflight(0, 100, 100, 50, 50, 1, 1, "TEXCOORD_7", 12, 0.0125, 0.003, 3, 0, false,
                true, true, true, true, true)],
            issues, 1.25)
        {
            AnimationValidation = new SkeletalMeshAnimationPreflight(
                "synthetic", true, false, false, 2, 10, 10, 8, 0, 0, 0.01, 0.002, 0.01, 1e-6, 1e-6, 1, 0, 1.01, 1.02,
                0, 0, [1, 2], []),
            SkeletonCompatibility = report,
        };
        var manifest = new SkeletalMeshEditRegistrationManifest(
            1, Guid.NewGuid().ToString("N"), created, name, "export-" + name, @"D:\staging\source.json",
            new string('A', 64), "pc_wr_f_av_178b", "pc_wr_f_av_178b_dress_sk", @"C:\Users\someone\edit.glb",
            "edit.edited.glb", new string('D', 64), 4, preflight);
        var path = Path.Combine(directory, SkeletalMeshEditWorkspaceStore.ManifestFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, OldVersionOptions));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - (age ?? TimeSpan.FromHours(2)));
        return (path, manifest);
    }
}
