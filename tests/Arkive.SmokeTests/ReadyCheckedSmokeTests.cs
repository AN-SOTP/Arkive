using Arkive.Core.GameData;

namespace Arkive.SmokeTests;

/// <summary>
/// Launch in two moments: the working-file index that lets the check of enabled mods skip opening records, and the
/// remembered Mods list shown before it (see WorkingFileIndex and ArkiveModListMemory).
/// </summary>
internal static class ReadyCheckedSmokeTests
{
    public static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("uses a working-file index entry only while its record and files are unchanged, and reads the rest again", TestIndexValidation),
        new("ignores a working-file index from another version, another data folder or a damaged file", TestIndexInvalidation),
        new("makes the enabled mods' working files from the index exactly as a full read does", TestEnabledOnlySnapshot),
        new("shows the same Mods list status from the index as from a full read, also after a change", TestStatusFromIndex),
        new("remembers the Mods list and ignores it when the library, the index format or Arkive changed", TestRememberedList),
        new("synchronises the enabled mods' check boxes from the index only after reading their records again", TestSynchronizeSome),
    ];

    private const string Version = "9.9.9+test";

    private sealed record Fixture(string Root, ArkiveModTestFixture.ArkiveTestEnvironment Environment, ArkiveModLibraryManifest Library)
    {
        public ArkiveModEditWorkspaces Edits => Environment.Edits;

        public IReadOnlySet<string> Enabled => Library.Mods.Where(mod => mod.Enabled).SelectMany(mod => mod.Bindings)
            .Select(binding => binding.EditId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> Bound => Library.Mods.SelectMany(mod => mod.Bindings)
            .Select(binding => binding.EditId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        public string? Sha => Environment.Library.Sha256Of(Library);
    }

    private static Fixture Create(string name)
    {
        var root = Path.Combine(fixtureDirectory, name);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        LaunchFixture.CreateAsync(root, new LaunchFixtureOptions(MeshEdits: 16, Textures: 12, Effects: 20, Materials: 10,
            Packages: 6, BuildsPerPackage: 1, Mods: 6, EnabledShare: 0.5)).GetAwaiter().GetResult();
        var environment = ArkiveModTestFixture.CreateEnvironment(root);
        var library = environment.Library.LoadAsync().GetAwaiter().GetResult();
        return new Fixture(root, environment, library);
    }

    private static WorkingFileIndexDocument Index(Fixture fixture)
    {
        var readStart = DateTime.UtcNow;
        var full = ArkiveModEditSnapshot.LoadAsync(fixture.Edits).GetAwaiter().GetResult();
        return WorkingFileIndex.BuildAsync(full, readStart, fixture.Root, Version, fixture.Sha, fixture.Bound.ToArray())
            .GetAwaiter().GetResult();
    }

    private static WorkingFileIndexSnapshot Snapshot(Fixture fixture, WorkingFileIndexDocument? index) =>
        WorkingFileIndex.SnapshotAsync(index, fixture.Edits, fixture.Enabled.ToArray(), fixture.Bound.ToArray(), fixture.Root,
            fixture.Sha).GetAwaiter().GetResult();

    private static ArkiveModLocalEdit EnabledEdit(Fixture fixture, ArkiveModEditSnapshot full, UnifiedPackageAssetKind kind) =>
        full.Edits.First(edit => edit.Kind == kind && fixture.Enabled.Contains(edit.EditId));

    private static void TestIndexValidation()
    {
        var fixture = Create("ready-index-validation");
        var index = Index(fixture);
        var full = ArkiveModEditSnapshot.LoadAsync(fixture.Edits).GetAwaiter().GetResult();
        Assert(index.Entries.Count == full.Edits.Count && index.Entries.All(entry => entry.Settled) &&
               index.Entries.Where(entry => fixture.Bound.Contains(entry.EditId)).All(entry => entry.HasStatus) &&
               index.Entries.Where(entry => !fixture.Bound.Contains(entry.EditId)).All(entry => !entry.HasStatus),
            $"the index holds {index.Entries.Count} of {full.Edits.Count} working files, or not every entry is settled with status for bound edits only");
        Assert(index.Entries.All(entry => !Path.IsPathRooted(entry.Files[0].Path)), "a record inside the data folder is not kept relative to it");

        var unchanged = Snapshot(fixture, index);
        Assert(!unchanged.FullRead && unchanged.Verified == fixture.Enabled.Count && unchanged.ReadAgain == 0 && unchanged.Gone == 0 &&
               unchanged.Snapshot.FromIndex,
            $"an unchanged folder was not checked from the index: full={unchanged.FullRead}, verified={unchanged.Verified}/{fixture.Enabled.Count}, " +
            $"again={unchanged.ReadAgain}, gone={unchanged.Gone}");
        Assert(unchanged.Snapshot.Edits.Where(edit => fixture.Enabled.Contains(edit.EditId)).All(edit => edit.Entry is ArkiveModIndexedEdit),
            "an unchanged enabled working file was opened");

        // A record rewritten (its check box turned off) is read again, and shows what the record now says.
        var material = EnabledEdit(fixture, full, UnifiedPackageAssetKind.MaterialInstanceConstant);
        ArkiveModSelectionService.SetIncludedAsync(fixture.Edits, material, !material.Included).GetAwaiter().GetResult();
        // An edited file whose hash decides "modified" changes: its record is read again too.
        var mesh = EnabledEdit(fixture, full, UnifiedPackageAssetKind.SkeletalMesh);
        var glb = ((SkeletalMeshEditWorkspaceEntry)mesh.Entry).EditedGlbPath;
        File.AppendAllText(glb, "changed in Blender");
        // A record deleted while Arkive was closed is gone, as a full read would leave it out.
        var texture = EnabledEdit(fixture, full, UnifiedPackageAssetKind.Texture2D);
        File.Delete(texture.ManifestPath);
        var changed = Snapshot(fixture, index);
        Assert(!changed.FullRead && changed.ReadAgain == 2 && changed.Gone == 1 && changed.Verified == fixture.Enabled.Count - 3,
            $"the changes were not found: again={changed.ReadAgain}, gone={changed.Gone}, verified={changed.Verified}");
        Assert(changed.Snapshot.Find(material.EditId) is { Entry: MaterialInstanceEditEntry } read && read.Included == !material.Included,
            "the rewritten record was not read again with its new check box");
        Assert(changed.Snapshot.Find(texture.EditId) is null, "a deleted record is still in the snapshot");
        var binding = fixture.Library.Mods.SelectMany(mod => mod.Bindings).First(item => item.EditId == mesh.EditId);
        Assert(ArkiveModAuthoringService.IsModifiedAsync(changed.Snapshot.Find(mesh.EditId)!, binding).GetAwaiter().GetResult(),
            "a working mesh changed outside Arkive is not shown as modified");

        // An enabled mod's working file the index does not know (added since) means every store is read.
        var missing = index with { Entries = index.Entries.Where(entry => entry.EditId != mesh.EditId).ToArray() };
        var fallback = Snapshot(fixture, missing);
        Assert(fallback.FullRead && !fallback.Snapshot.FromIndex && fallback.FallbackReason!.Contains(mesh.EditId, StringComparison.Ordinal),
            "an index without an enabled mod's working file did not lead to a full read");
        // An id a mod names that was already missing at the last full read stays missing while the library is the same.
        var knownMissing = missing with { MissingIds = [mesh.EditId] };
        Assert(!Snapshot(fixture, knownMissing).FullRead, "a working file already missing at the last full read led to a full read");
        Assert(Snapshot(fixture, knownMissing with { LibrarySha256 = "OTHER" }).FullRead,
            "a working file missing at the last full read of another library did not lead to a full read");
        Assert(Snapshot(fixture, null).FullRead, "no index did not lead to a full read");
        Directory.Delete(fixture.Root, recursive: true);
    }

    private static void TestIndexInvalidation()
    {
        var fixture = Create("ready-index-invalidation");
        var index = Index(fixture);
        WorkingFileIndex.Save(fixture.Root, index);
        Assert(WorkingFileIndex.Load(fixture.Root, Version) is { } loaded && loaded.Entries.Count == index.Entries.Count &&
               JsonSerializer.Serialize(loaded) == JsonSerializer.Serialize(index),
            "the saved index did not read back the same");
        Assert(WorkingFileIndex.Load(fixture.Root, "9.9.8+test") is null, "an index from another Arkive version was used");
        var other = Path.Combine(fixtureDirectory, "ready-index-other-root");
        if (Directory.Exists(other)) Directory.Delete(other, recursive: true);
        Directory.CreateDirectory(Path.Combine(other, "cache"));
        File.Copy(WorkingFileIndex.PathFor(fixture.Root), WorkingFileIndex.PathFor(other));
        Assert(WorkingFileIndex.Load(other, Version) is null, "an index made for another data folder was used");
        var path = WorkingFileIndex.PathFor(fixture.Root);
        File.WriteAllText(path, File.ReadAllText(path)[..(int)(new FileInfo(path).Length / 2)]);
        Assert(WorkingFileIndex.Load(fixture.Root, Version) is null, "a damaged index was used");
        WorkingFileIndex.Save(fixture.Root, index);
        var text = File.ReadAllText(path);
        Assert(text.Contains($"\"formatVersion\":{WorkingFileIndex.FormatVersion},", StringComparison.Ordinal), "the index does not name its format");
        File.WriteAllText(path, text.Replace($"\"formatVersion\":{WorkingFileIndex.FormatVersion},",
            $"\"formatVersion\":{WorkingFileIndex.FormatVersion + 1},", StringComparison.Ordinal));
        Assert(WorkingFileIndex.Load(fixture.Root, Version) is null, "an index of another format was used");
        File.Delete(path);
        Assert(WorkingFileIndex.Load(fixture.Root, Version) is null, "a missing index was not reported as missing");

        // A record written while the index was made (after the read began) is kept unsettled, so it is read next time.
        var readStart = DateTime.UtcNow;
        var full = ArkiveModEditSnapshot.LoadAsync(fixture.Edits).GetAwaiter().GetResult();
        var touched = EnabledEdit(fixture, full, UnifiedPackageAssetKind.ParticleSystem);
        File.SetLastWriteTimeUtc(touched.ManifestPath, DateTime.UtcNow);
        var racing = WorkingFileIndex.BuildAsync(full, readStart, fixture.Root, Version, fixture.Sha, fixture.Bound.ToArray())
            .GetAwaiter().GetResult();
        Assert(racing.Entries.Single(entry => entry.EditId == touched.EditId) is { Settled: false } &&
               racing.Entries.Count(entry => !entry.Settled) == 1,
            "a record written during the read was trusted");
        var again = Snapshot(fixture, racing);
        Assert(again.ReadAgain == 1 && again.Snapshot.Find(touched.EditId)!.Entry is not ArkiveModIndexedEdit,
            "an unsettled entry was used without reading its record");
        Directory.Delete(fixture.Root, recursive: true);
        Directory.Delete(other, recursive: true);
    }

    /// <summary>What a working file shows in the Mods list, from the index or from its record.</summary>
    private static string Describe(ArkiveModLocalEdit edit, ArkiveModLibraryManifest library) =>
        string.Join("|", edit.Kind, edit.EditId, edit.ManifestPath, edit.DisplayName, edit.SourcePackagePath, edit.ObjectName,
            edit.Included, ArkiveModEditSnapshot.GetBuildContentSha256Async(edit).GetAwaiter().GetResult(),
            library.Mods.SelectMany(mod => mod.Bindings).FirstOrDefault(binding => binding.EditId == edit.EditId) is { } binding
                ? ArkiveModAuthoringService.IsModifiedAsync(edit, binding).GetAwaiter().GetResult()
                : null,
            edit.TargetKey);

    private static void TestEnabledOnlySnapshot()
    {
        var fixture = Create("ready-enabled-only");
        var index = Index(fixture);
        var full = ArkiveModEditSnapshot.LoadAsync(fixture.Edits).GetAwaiter().GetResult();
        var made = Snapshot(fixture, index);
        Assert(!made.FullRead && made.Snapshot.Edits.Count == full.Edits.Count,
            $"the launch snapshot holds {made.Snapshot.Edits.Count} of {full.Edits.Count} working files");
        foreach (var id in fixture.Bound)
        {
            var expected = full.Find(id);
            var actual = made.Snapshot.Find(id);
            Assert(expected is not null && actual is not null && Describe(actual, fixture.Library) == Describe(expected, fixture.Library),
                $"working file {id} differs:\n  index {(actual is null ? "missing" : Describe(actual, fixture.Library))}\n" +
                $"  read  {(expected is null ? "missing" : Describe(expected, fixture.Library))}");
        }
        Assert(made.Snapshot.Edits.Select(edit => edit.EditId).SequenceEqual(full.Edits.Select(edit => edit.EditId)),
            "the launch snapshot does not hold the working files in the order a full read does");
        Console.WriteLine($"  {fixture.Enabled.Count} enabled and {fixture.Bound.Count - fixture.Enabled.Count} disabled working files of " +
                          $"{full.Edits.Count}; {made.FileChecks} files looked at");
        Directory.Delete(fixture.Root, recursive: true);
    }

    /// <summary>Everything a status shows, mod by mod and asset by asset.</summary>
    private static string Statuses(IEnumerable<ArkiveModStatus> statuses) => string.Join("\n", statuses.Select(status =>
        $"{status.Mod.ModId}|{status.FileMissing}|{status.State}|{status.ConflictSummary}|{status.InGame}|{status.StillInGame}|" +
        $"{status.ModifiedLocally}|{status.OutsideChange}|{status.GameData}|{status.Looks}|" +
        string.Join(";", status.Assets.Select(asset =>
            $"{asset.Asset.AssetId},{asset.Outcome},{asset.Competitor?.ModId},{asset.Setup},{asset.ModifiedLocally},{asset.InGame}," +
            $"{asset.Edit?.ManifestPath},{asset.Edit?.Included}"))));

    private static IReadOnlyDictionary<string, GuidedTexturePackageRevision?> NothingInstalled(ArkiveModLibraryManifest library) =>
        library.Mods.SelectMany(mod => mod.Manifest.Assets)
            .Select(asset => ArkiveModFileService.PhysicalPackageKey(asset.Target.PhysicalPackageName))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(key => key, _ => (GuidedTexturePackageRevision?)null, StringComparer.Ordinal);

    private static void TestStatusFromIndex()
    {
        var fixture = Create("ready-status");
        var index = Index(fixture);
        var installed = NothingInstalled(fixture.Library);
        string Status(ArkiveModEditSnapshot snapshot) => Statuses(ArkiveModApplyService.DescribeAsync(
            fixture.Environment.Library, fixture.Library, snapshot, installed).GetAwaiter().GetResult());
        var full = ArkiveModEditSnapshot.LoadAsync(fixture.Edits).GetAwaiter().GetResult();
        var fromIndex = Snapshot(fixture, index).Snapshot;
        var before = Status(full);
        Assert(Status(fromIndex) == before, "the Mods list status from the index differs from a full read");

        // After an enabled mod's working mesh changes, the index shows it as a full read does.
        var mesh = EnabledEdit(fixture, full, UnifiedPackageAssetKind.SkeletalMesh);
        File.AppendAllText(((SkeletalMeshEditWorkspaceEntry)mesh.Entry).EditedGlbPath, "changed");
        var fullAfter = ArkiveModEditSnapshot.LoadAsync(fixture.Edits).GetAwaiter().GetResult();
        var indexAfter = Snapshot(fixture, index).Snapshot;
        // (A snapshot of records works out "modified" from the file each time, so the earlier text is compared.)
        Assert(Status(fullAfter) != before, "changing a working mesh changed no status");
        var (fromIndexAfter, fromReadAfter) = (Status(indexAfter), Status(fullAfter));
        Assert(fromIndexAfter == fromReadAfter,
            "after a working mesh changed, the status from the index differs from a full read:\n" + string.Join("\n",
                fromIndexAfter.Split('\n').Zip(fromReadAfter.Split('\n')).Where(pair => pair.First != pair.Second)
                    .Select(pair => $"  index {pair.First}\n  read  {pair.Second}")));
        Directory.Delete(fixture.Root, recursive: true);
    }

    private static void TestRememberedList()
    {
        var fixture = Create("ready-remembered");
        var full = ArkiveModEditSnapshot.LoadAsync(fixture.Edits).GetAwaiter().GetResult();
        var statuses = ArkiveModApplyService.DescribeAsync(fixture.Environment.Library, fixture.Library, full,
            NothingInstalled(fixture.Library), outsideChangedPackages: [fixture.Library.Mods[0].Manifest.Assets[0].Target.PhysicalPackageName])
            .GetAwaiter().GetResult();
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fixture.Library.Mods[1].ModId };
        var sha = fixture.Sha!;
        ArkiveModListMemory.Save(fixture.Root, ArkiveModListMemory.Remember(statuses, pending, 3, sha, Version, fixture.Root));
        var loaded = ArkiveModListMemory.Load(fixture.Root, Version);
        var recalled = ArkiveModListMemory.Recall(loaded, fixture.Library, sha);
        Assert(recalled is not null && Statuses(recalled.Statuses) == Statuses(statuses) &&
               recalled.AuthoringPending.SetEquals(pending) && recalled.OutsideChangeCount == 3,
            "the remembered Mods list did not come back as it was");
        Assert(recalled!.Statuses.SelectMany(status => status.Assets).All(asset => asset.Edit is null or { Entry: ArkiveModIndexedEdit }),
            "a remembered working file was opened");

        Assert(ArkiveModListMemory.Load(fixture.Root, "9.9.8+test") is null, "a list remembered by another Arkive version was used");
        Assert(ArkiveModListMemory.Recall(loaded, fixture.Library, "0" + sha[1..]) is null, "a list remembered for another library file was used");
        Assert(ArkiveModListMemory.Recall(loaded, fixture.Library, null) is null, "a list was used for a library whose file is unknown");
        var replaced = fixture.Library with
        {
            Mods = fixture.Library.Mods.Select((mod, index) => index == 0 ? mod with { FileSha256 = new string('A', 64) } : mod).ToArray(),
        };
        Assert(ArkiveModListMemory.Recall(loaded, replaced, sha) is null, "a list remembered before a mod file was replaced was used");
        var fewer = fixture.Library with { Mods = fixture.Library.Mods.Skip(1).ToArray() };
        Assert(ArkiveModListMemory.Recall(loaded, fewer, sha) is null, "a list remembered with another set of mods was used");
        var saved = File.ReadAllText(ArkiveModListMemory.PathFor(fixture.Root));
        var formatText = $"\"indexFormatVersion\":{WorkingFileIndex.FormatVersion},";
        Assert(saved.Contains(formatText, StringComparison.Ordinal) &&
               WriteAndLoad(fixture.Root, saved.Replace(formatText, "\"indexFormatVersion\":0,", StringComparison.Ordinal)) is null,
            "a list remembered with another working-file index format was used");
        Assert(WriteAndLoad(fixture.Root, saved) is not null, "the remembered list did not read back after it was written again");
        var elsewhere = Path.Combine(fixtureDirectory, "ready-remembered-elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "cache"));
        File.Copy(ArkiveModListMemory.PathFor(fixture.Root), ArkiveModListMemory.PathFor(elsewhere), overwrite: true);
        Assert(ArkiveModListMemory.Load(elsewhere, Version) is null, "a list remembered for another data folder was used");
        File.WriteAllText(ArkiveModListMemory.PathFor(fixture.Root), "{ not json");
        Assert(ArkiveModListMemory.Load(fixture.Root, Version) is null, "a damaged remembered list was used");
        Directory.Delete(fixture.Root, recursive: true);
        Directory.Delete(elsewhere, recursive: true);

        static RememberedModList? WriteAndLoad(string root, string json)
        {
            File.WriteAllText(ArkiveModListMemory.PathFor(root), json);
            return ArkiveModListMemory.Load(root, Version);
        }
    }

    private static void TestSynchronizeSome()
    {
        var fixture = Create("ready-synchronize");
        var index = Index(fixture);
        var full = ArkiveModEditSnapshot.LoadAsync(fixture.Edits).GetAwaiter().GetResult();
        // An enabled mod's working file turned off behind the library's back: the index still says it is on.
        var texture = EnabledEdit(fixture, full, UnifiedPackageAssetKind.Texture2D);
        var effect = EnabledEdit(fixture, full, UnifiedPackageAssetKind.ParticleSystem);
        ArkiveModSelectionService.SetIncludedAsync(fixture.Edits, texture, false).GetAwaiter().GetResult();
        var stale = index with
        {
            // An index made when the effect was off, so its entry disagrees with the record, which is unchanged: the
            // synchronisation must read the record before writing, and leave it alone.
            Entries = index.Entries.Select(entry => entry.EditId == effect.EditId ? entry with { Included = false } : entry).ToArray(),
        };
        var effectWrite = new FileInfo(effect.ManifestPath).LastWriteTimeUtc;
        var made = Snapshot(fixture, stale);
        var result = ArkiveModSelectionService.SynchronizeSomeAsync(fixture.Library, fixture.Edits, made.Snapshot, fixture.Enabled.ToArray())
            .GetAwaiter().GetResult();
        Assert(result.Changes.Count == 1 && result.Changes[0].EditId == texture.EditId && result.Changes[0].Included,
            "the synchronisation did not turn exactly the changed working file back on: " +
            string.Join(", ", result.Changes.Select(change => $"{change.EditId}={change.Included}")));
        Assert(new FileInfo(effect.ManifestPath).LastWriteTimeUtc == effectWrite,
            "a record was written from what the index said, without reading it first");
        var after = ArkiveModEditSnapshot.LoadAsync(fixture.Edits).GetAwaiter().GetResult();
        Assert(after.Find(texture.EditId)!.Included && after.Find(effect.EditId)!.Included && result.Snapshot.Find(texture.EditId)!.Included,
            "the working files are not checked as their mods' selection says after the synchronisation");
        Assert(ArkiveModSelectionService.CountPending(fixture.Library, after) == 0 &&
               ArkiveModSelectionService.SynchronizeAsync(fixture.Environment.Library, fixture.Edits).GetAwaiter().GetResult().Count == 0,
            "the full synchronisation still finds something to change");
        Directory.Delete(fixture.Root, recursive: true);
    }
}
