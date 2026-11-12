using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Arkive.Core.Meshes;

public sealed class SkeletalMeshEditWorkspaceStore
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "arkive-edited-skeletalmesh.json";

    /// <summary>Records changed this recently are not compacted, in case another Arkive is still writing them.</summary>
    public static readonly TimeSpan CompactionMinimumAge = TimeSpan.FromHours(1);

    private const string SkeletonReportProperty = "skeletonCompatibility";
    private const string SkeletonSummaryProperty = "skeletonCompatibilitySummary";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitEmptySkeletonReport } },
    };

    /// <summary>
    /// How records are read when listed: an older record's full skeleton report (about 300 KB of its 450 KB) is
    /// skipped while reading, keeping only the few values the list shows. Read in full, 3,500 records held about
    /// 1.1 GB after every launch.
    /// </summary>
    private static readonly JsonSerializerOptions LoadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ReadSkeletonSummaryOnly } },
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SkeletalMeshEditPreflightService _preflight;

    // Edit records are listed by every mod refresh; each one is parsed again only when its file changes.
    private readonly Mods.ParsedRecordCache<SkeletalMeshEditRegistrationManifest> _parsed = new();

    // Record writes from this store (a user's change or the background compaction) never overlap.
    private readonly SemaphoreSlim _writes = new(1, 1);

    public SkeletalMeshEditWorkspaceStore(
        string rootDirectory,
        SkeletalMeshEditPreflightService? preflight = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        _preflight = preflight ?? new SkeletalMeshEditPreflightService();
    }

    public string RootDirectory { get; }

    public async Task<SkeletalMeshEditRegistrationResult> RegisterAsync(
        SkeletalMeshEditableWorkspaceEntry source,
        string candidateGlbPath,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateGlbPath);
        var candidate = Path.GetFullPath(candidateGlbPath);
        var preflight = await _preflight.InspectAsync(source, candidate, cancellationToken)
            .ConfigureAwait(false);
        var editId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(
            RootDirectory, Safe(source.Manifest.ExportId),
            $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{editId}");
        Directory.CreateDirectory(directory);
        var outputName = Safe(Path.GetFileNameWithoutExtension(candidate)) + ".edited.glb";
        var outputPath = Path.Combine(directory, outputName);
        try
        {
            await CopyAtomicAsync(candidate, outputPath, cancellationToken).ConfigureAwait(false);
            var durablePreflight = preflight with { CandidateGlbPath = outputPath };
            var manifest = new SkeletalMeshEditRegistrationManifest(
                CurrentSchemaVersion,
                editId,
                DateTimeOffset.UtcNow,
                string.IsNullOrWhiteSpace(displayName)
                    ? Path.GetFileNameWithoutExtension(candidate)
                    : displayName.Trim(),
                source.Manifest.ExportId,
                source.ManifestPath,
                source.Manifest.GlbSha256,
                source.Manifest.PackageAlias,
                source.Manifest.ObjectName,
                candidate,
                Path.GetFileName(outputPath),
                preflight.CandidateGlbSha256,
                preflight.CandidateGlbBytes,
                durablePreflight);
            var manifestPath = Path.Combine(directory, ManifestFileName);
            await WriteRecordAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            // The caller keeps the full report in memory, to show and log what this check found.
            var entry = new SkeletalMeshEditWorkspaceEntry(
                directory, manifestPath, outputPath, manifest);
            return new SkeletalMeshEditRegistrationResult(entry, durablePreflight);
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public async Task<SkeletalMeshEditWorkspaceEntry> RevalidateAsync(
        SkeletalMeshEditWorkspaceEntry entry,
        SkeletalMeshEditableWorkspaceEntry source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(source);
        var directory = EnsureWithinRoot(entry.DirectoryPath);
        var editedGlb = EnsureWithinDirectory(directory, entry.EditedGlbPath);
        var preflight = await _preflight.InspectAsync(source, editedGlb, cancellationToken)
            .ConfigureAwait(false);
        var manifest = entry.Manifest with
        {
            EditedGlbSha256 = preflight.CandidateGlbSha256,
            EditedGlbBytes = preflight.CandidateGlbBytes,
            Preflight = preflight,
        };
        var manifestPath = EnsureWithinDirectory(directory, entry.ManifestPath);
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteRecordAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false); }
        finally { _writes.Release(); }
        return new SkeletalMeshEditWorkspaceEntry(
            directory, manifestPath, editedGlb, manifest);
    }

    public async Task<SkeletalMeshEditWorkspaceEntry> SetIncludedAsync(
        SkeletalMeshEditWorkspaceEntry entry,
        bool included,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var directory = EnsureWithinRoot(entry.DirectoryPath);
        var manifestPath = EnsureWithinDirectory(directory, entry.ManifestPath);
        var manifest = entry.Manifest with { IncludedInPackageBuild = included };
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("The edited mesh manifest is missing.", manifestPath);
            await WriteRecordAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
        return entry with { Manifest = manifest };
    }

    public async Task<SkeletalMeshEditWorkspaceSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        // On the thread pool: the folder walk ran before the first await and held a caller on the window's thread.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (!Directory.Exists(RootDirectory))
            return new SkeletalMeshEditWorkspaceSnapshot([], []);
        var paths = Directory.EnumerateFiles(RootDirectory, ManifestFileName, SearchOption.AllDirectories).ToArray();
        var entries = new SkeletalMeshEditWorkspaceEntry?[paths.Length];
        var issues = new SkeletalMeshEditWorkspaceIssue?[paths.Length];
        // Records not parsed yet (at startup, all of them) are read in parallel: large drafts hold hundreds.
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Length),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var path = paths[index];
                try
                {
                    entries[index] = await LoadEntryAsync(path, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException or
                        InvalidDataException or ArgumentException or NotSupportedException)
                {
                    issues[index] = new SkeletalMeshEditWorkspaceIssue(Path.GetFullPath(path), exception.Message);
                }
            }).ConfigureAwait(false);
        return new SkeletalMeshEditWorkspaceSnapshot(
            entries.OfType<SkeletalMeshEditWorkspaceEntry>().OrderByDescending(entry => entry.Manifest.CreatedAt).ToArray(),
            issues.OfType<SkeletalMeshEditWorkspaceIssue>().ToArray());
    }

    /// <summary>
    /// One edit's record read from disk (parsed again only if its file changed), checked as LoadAsync checks it. The
    /// full skeleton report is never read here, so the entry's <c>Preflight.SkeletonCompatibility</c> is null;
    /// <see cref="LoadSkeletonCompatibilityAsync"/> reads it when it is wanted.
    /// </summary>
    public async Task<SkeletalMeshEditWorkspaceEntry> LoadEntryAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        manifestPath = EnsureWithinRoot(manifestPath);
        var manifest = await _parsed.GetAsync(manifestPath, async (file, ct) =>
        {
            SkeletalMeshEditRegistrationManifest read;
            await using (var stream = new FileStream(
                             file, FileMode.Open, FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
                read = await JsonSerializer.DeserializeAsync<SkeletalMeshEditRegistrationManifest>(
                           stream, LoadJsonOptions, ct).ConfigureAwait(false) ??
                       throw new InvalidDataException("The edited-mesh manifest was empty.");
            // A slim record saved again by Arkive 0.7.5 loses the summary that version does not know; the report
            // beside the record still has those values.
            if (read.Preflight is { SkeletonCompatibilitySummary: null } preflight &&
                await SkeletalMeshSkeletonCompatibilityFile.ReadAsync(
                    Path.GetDirectoryName(file)!, preflight.CreatedAt, preflight.CandidateGlbSha256, ct)
                    .ConfigureAwait(false) is { } beside)
                read = read with
                {
                    Preflight = preflight with
                    {
                        SkeletonCompatibilitySummary = SkeletalMeshSkeletonCompatibilitySummary.From(beside),
                    },
                };
            return read;
        }, cancellationToken).ConfigureAwait(false);
        if (manifest.SchemaVersion != CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(manifest.EditId) ||
            string.IsNullOrWhiteSpace(manifest.SourceExportId) ||
            string.IsNullOrWhiteSpace(manifest.EditedGlbRelativePath) ||
            Path.IsPathRooted(manifest.EditedGlbRelativePath))
            throw new InvalidDataException("The edited-mesh manifest identity or schema is invalid.");
        var directory = Path.GetDirectoryName(manifestPath) ??
                        throw new InvalidDataException("The edited-mesh directory is unavailable.");
        var editedGlb = EnsureWithinDirectory(
            directory, Path.Combine(directory, manifest.EditedGlbRelativePath));
        if (!File.Exists(editedGlb))
            throw new FileNotFoundException("The registered edited GLB is missing.", editedGlb);
        return new SkeletalMeshEditWorkspaceEntry(directory, manifestPath, editedGlb, manifest);
    }

    /// <summary>
    /// The full skeleton report of an edit's latest check, read from disk: from inside the record when it still holds
    /// one (records from before this change, or saved again by Arkive 0.7.5), otherwise from the file beside it.
    /// Null when the check has no report, as it had none before.
    /// </summary>
    public async Task<SkeletalMeshSkeletonCompatibilityReport?> LoadSkeletonCompatibilityAsync(
        SkeletalMeshEditWorkspaceEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Manifest.Preflight.SkeletonCompatibility is { } inMemory) return inMemory;
        var manifestPath = EnsureWithinRoot(entry.ManifestPath);
        var stored = await ReadStoredReportAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (stored.Preflight is not { } preflight) return null;
        return preflight.SkeletonCompatibility ??
               await SkeletalMeshSkeletonCompatibilityFile.ReadAsync(
                   Path.GetDirectoryName(manifestPath)!, preflight.CreatedAt, preflight.CandidateGlbSha256 ?? string.Empty,
                   cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        SkeletalMeshEditWorkspaceEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        var manifestPath = EnsureWithinRoot(entry.ManifestPath);
        if (!Path.GetFileName(manifestPath).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected file is not an Arkive edited-mesh manifest.");
        var directory = EnsureWithinRoot(Path.GetDirectoryName(manifestPath) ?? string.Empty);
        if (directory.Equals(RootDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Arkive refuses to delete the edited-mesh workspace root.");
        // A compaction of this record holds files open in the folder until it finishes.
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        finally { _writes.Release(); }
    }

    /// <summary>
    /// Compacts every record of the store in turn (see <see cref="CompactAsync"/>). <paramref name="beforeEachRecord"/>
    /// runs before each record that still holds a report, so a caller can wait there while the user is busy; records
    /// already compact are only looked at, so a run over a compacted store is quick. Cancelling stops between steps, leaves
    /// the record being worked on as it was, and returns what was done so far.
    /// </summary>
    public async Task<SkeletalMeshEditCompactionSummary> CompactAllAsync(
        bool dryRun,
        Func<CancellationToken, Task>? beforeEachRecord = null,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var paths = Directory.Exists(RootDirectory)
            ? Directory.EnumerateFiles(RootDirectory, ManifestFileName, SearchOption.AllDirectories).ToArray()
            : [];
        int compacted = 0, slim = 0, recent = 0, failed = 0;
        long before = 0, after = 0;
        var problems = new List<SkeletalMeshEditCompactionResult>();
        var stopped = false;
        foreach (var path in paths)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await MayHoldSkeletonReportAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    slim++;
                    continue;
                }
                if (beforeEachRecord is not null) await beforeEachRecord(cancellationToken).ConfigureAwait(false);
                var result = await CompactAsync(path, dryRun, null, cancellationToken).ConfigureAwait(false);
                switch (result.Outcome)
                {
                    case SkeletalMeshEditCompactionOutcome.Compacted:
                        compacted++;
                        before += result.BytesBefore;
                        after += result.BytesAfter;
                        break;
                    case SkeletalMeshEditCompactionOutcome.AlreadySlim: slim++; break;
                    case SkeletalMeshEditCompactionOutcome.RecentlyChanged: recent++; break;
                    default:
                        failed++;
                        problems.Add(result);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopped = true;
                break;
            }
        }
        return new SkeletalMeshEditCompactionSummary(
            paths.Length, compacted, slim, recent, failed, before, after, dryRun, stopped, problems);
    }

    /// <summary>
    /// Moves an older record's full skeleton report into the file beside it and rewrites the record without it. The
    /// report is written and read back equal first; the slim record is written to a temporary file and checked to
    /// read back equal to the original in every other field; only then is the record replaced, keeping its write
    /// time. On any failure the original is left as it was and nothing new stays behind. Records changed in the last
    /// hour are left alone. <paramref name="afterStep"/> runs after each stage (a failure there is handled like any
    /// other, which is how the smoke tests prove each stage cleans up).
    /// </summary>
    public async Task<SkeletalMeshEditCompactionResult> CompactAsync(
        string manifestPath,
        bool dryRun = false,
        Action<SkeletalMeshEditCompactionStep>? afterStep = null,
        CancellationToken cancellationToken = default)
    {
        manifestPath = EnsureWithinRoot(manifestPath);
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        var directory = Path.GetDirectoryName(manifestPath)!;
        var siblingPath = SkeletalMeshSkeletonCompatibilityFile.PathIn(directory);
        var siblingTemporary = SkeletalMeshSkeletonCompatibilityFile.TemporaryPath(siblingPath);
        var recordTemporary = manifestPath + $".{Guid.NewGuid():N}.tmp";
        var siblingExisted = File.Exists(siblingPath);
        var siblingPlaced = false;
        var replaced = false;
        DateTime? directoryWriteTime = null;
        long bytesBefore = 0;
        try
        {
            var original = new FileInfo(manifestPath);
            if (!original.Exists)
                return Result(SkeletalMeshEditCompactionOutcome.Failed, 0, 0, "The record is missing.");
            bytesBefore = original.Length;
            var writeTime = original.LastWriteTimeUtc;
            var creationTime = original.CreationTimeUtc;
            if (DateTime.UtcNow - writeTime < CompactionMinimumAge)
                return Result(SkeletalMeshEditCompactionOutcome.RecentlyChanged, bytesBefore, bytesBefore, null);

            var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(new MemoryStream(bytes, writable: false), NodeOptions) as JsonObject ??
                       throw new InvalidDataException("The record is not a JSON object.");
            if (root["preflight"] is not JsonObject preflightNode ||
                preflightNode[SkeletonReportProperty] is not JsonObject)
                return Result(SkeletalMeshEditCompactionOutcome.AlreadySlim, bytesBefore, bytesBefore, null);

            var full = Deserialize(bytes, JsonOptions);
            var report = full.Preflight.SkeletonCompatibility ??
                         throw new InvalidDataException("The record's skeleton report could not be read.");
            var listed = Deserialize(bytes, LoadJsonOptions);
            var summary = SkeletalMeshSkeletonCompatibilitySummary.From(report);

            // Every value but the report, exactly as in the original, plus the summary the list shows.
            preflightNode.Remove(SkeletonReportProperty);
            preflightNode.Remove(SkeletonSummaryProperty);
            preflightNode[SkeletonSummaryProperty] = JsonSerializer.SerializeToNode(summary, JsonOptions);
            var slimBytes = JsonSerializer.SerializeToUtf8Bytes(root, JsonOptions);
            if (dryRun)
            {
                using var buffer = new MemoryStream();
                using (var gzip = new System.IO.Compression.GZipStream(
                           buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                    JsonSerializer.Serialize(gzip, report, SkeletalMeshSkeletonCompatibilityFile.JsonOptions);
                return Result(SkeletalMeshEditCompactionOutcome.Compacted, bytesBefore,
                    slimBytes.LongLength + buffer.Length, null);
            }

            // The folder and the new file keep the record's age: working-file clean-up judges folders by it.
            directoryWriteTime = Directory.GetLastWriteTimeUtc(directory);
            SkeletalMeshSkeletonCompatibilityFile.WriteTo(siblingTemporary, full.Preflight);
            File.SetLastWriteTimeUtc(siblingTemporary, writeTime);
            afterStep?.Invoke(SkeletalMeshEditCompactionStep.ReportWritten);
            var readBack = await SkeletalMeshSkeletonCompatibilityFile.ReadFromAsync(
                    siblingTemporary, full.Preflight.CreatedAt, full.Preflight.CandidateGlbSha256, cancellationToken)
                .ConfigureAwait(false);
            if (readBack is null || !SameJson(readBack, report, SkeletalMeshSkeletonCompatibilityFile.JsonOptions))
                throw new InvalidDataException("The skeleton report did not read back equal to the original.");
            afterStep?.Invoke(SkeletalMeshEditCompactionStep.ReportVerified);

            await using (var stream = new FileStream(
                             recordTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                             FileOptions.Asynchronous))
            {
                await stream.WriteAsync(slimBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.SetLastWriteTimeUtc(recordTemporary, writeTime);
            afterStep?.Invoke(SkeletalMeshEditCompactionStep.RecordWritten);
            var slimRead = await File.ReadAllBytesAsync(recordTemporary, cancellationToken).ConfigureAwait(false);
            var slimRoot = JsonNode.Parse(new MemoryStream(slimRead, writable: false), NodeOptions) as JsonObject;
            var originalRoot = JsonNode.Parse(new MemoryStream(bytes, writable: false), NodeOptions) as JsonObject;
            (originalRoot?["preflight"] as JsonObject)?.Remove(SkeletonReportProperty);
            (originalRoot?["preflight"] as JsonObject)?.Remove(SkeletonSummaryProperty);
            (slimRoot?["preflight"] as JsonObject)?.Remove(SkeletonSummaryProperty);
            if (!JsonNode.DeepEquals(originalRoot, slimRoot) ||
                !SameJson(Deserialize(slimRead, LoadJsonOptions), listed, JsonOptions) ||
                Deserialize(slimRead, JsonOptions).Preflight.SkeletonCompatibility is not null)
                throw new InvalidDataException("The slim record did not read back equal to the original.");
            afterStep?.Invoke(SkeletalMeshEditCompactionStep.RecordVerified);

            cancellationToken.ThrowIfCancellationRequested();
            original.Refresh();
            if (!original.Exists || original.Length != bytesBefore || original.LastWriteTimeUtc != writeTime)
                throw new IOException("The record changed while it was being compacted.");
            File.Move(siblingTemporary, siblingPath, true);
            siblingPlaced = true;
            afterStep?.Invoke(SkeletalMeshEditCompactionStep.ReportPlaced);
            File.Move(recordTemporary, manifestPath, true);
            replaced = true;
            try
            {
                if (File.GetLastWriteTimeUtc(manifestPath) != writeTime) File.SetLastWriteTimeUtc(manifestPath, writeTime);
                File.SetCreationTimeUtc(manifestPath, creationTime);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
            return Result(SkeletalMeshEditCompactionOutcome.Compacted, bytesBefore,
                slimBytes.LongLength + new FileInfo(siblingPath).Length, null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or ArgumentException or NotSupportedException)
        {
            return Result(SkeletalMeshEditCompactionOutcome.Failed, bytesBefore, bytesBefore, exception.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(siblingTemporary)) File.Delete(siblingTemporary);
                if (File.Exists(recordTemporary)) File.Delete(recordTemporary);
                // The original record still holds its report; a report beside it that this step added goes again.
                if (siblingPlaced && !replaced && !siblingExisted && File.Exists(siblingPath)) File.Delete(siblingPath);
                if (directoryWriteTime is { } folderTime) Directory.SetLastWriteTimeUtc(directory, folderTime);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
            _writes.Release();
        }

        SkeletalMeshEditCompactionResult Result(
            SkeletalMeshEditCompactionOutcome outcome, long before, long after, string? problem) =>
            new(manifestPath, outcome, before, after, problem);
    }

    // A quick look for a report object in the record's text; when in doubt the record is handed to CompactAsync, which
    // decides on the parsed record.
    private static async Task<bool> MayHoldSkeletonReportAsync(string path, CancellationToken token)
    {
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
        ReadOnlySpan<byte> text = bytes;
        var marker = "\"skeletonCompatibility\""u8;
        var whitespace = " \t\r\n"u8;
        for (var at = text.IndexOf(marker); at >= 0; at = text.IndexOf(marker))
        {
            text = text[(at + marker.Length)..].TrimStart(whitespace);
            if (text.IsEmpty || text[0] != (byte)':') continue;
            text = text[1..].TrimStart(whitespace);
            if (!text.IsEmpty && text[0] == (byte)'{') return true;
        }
        return false;
    }

    private static SkeletalMeshEditRegistrationManifest Deserialize(byte[] bytes, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<SkeletalMeshEditRegistrationManifest>(
            new MemoryStream(bytes, writable: false), options) ??
        throw new InvalidDataException("The edited-mesh manifest was empty.");

    private static bool SameJson<T>(T left, T right, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(left, options).Equals(JsonSerializer.Serialize(right, options), StringComparison.Ordinal);

    // Records are written without the full report: it goes beside them (WriteRecordAsync), and a record whose report
    // is not loaded must not be written with "skeletonCompatibility": null.
    private static void OmitEmptySkeletonReport(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(SkeletalMeshEditPreflightReport)) return;
        foreach (var property in typeInfo.Properties)
            if (property.Name.Equals(SkeletonReportProperty, StringComparison.OrdinalIgnoreCase))
                property.ShouldSerialize = static (_, value) => value is not null;
    }

    // Reads the report property of an older record as a summary: its few named values are kept and the bone lists
    // skipped, so nothing large is built. A summary stored in the record is kept when both are present.
    private static void ReadSkeletonSummaryOnly(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(SkeletalMeshEditPreflightReport)) return;
        var report = typeInfo.Properties.Single(property =>
            property.Name.Equals(SkeletonReportProperty, StringComparison.OrdinalIgnoreCase));
        var summary = typeInfo.Properties.Single(property =>
            property.Name.Equals(SkeletonSummaryProperty, StringComparison.OrdinalIgnoreCase));
        var getSummary = summary.Get ?? throw new InvalidOperationException("The skeleton summary cannot be read.");
        var setSummary = summary.Set ?? throw new InvalidOperationException("The skeleton summary cannot be set.");
        typeInfo.Properties.Remove(report);
        var fromReport = typeInfo.CreateJsonPropertyInfo(typeof(SkeletalMeshSkeletonCompatibilitySummary), report.Name);
        fromReport.Set = (target, value) =>
        {
            if (value is not null && getSummary(target) is null) setSummary(target, value);
        };
        typeInfo.Properties.Add(fromReport);
    }

    private sealed record StoredPreflight(
        DateTimeOffset CreatedAt,
        string? CandidateGlbSha256,
        SkeletalMeshSkeletonCompatibilityReport? SkeletonCompatibility);

    private sealed record StoredRecord(StoredPreflight? Preflight);

    // Only the check's identity and report are built; the rest of the record is skipped.
    private static async Task<StoredRecord> ReadStoredReportAsync(string manifestPath, CancellationToken token)
    {
        await using var stream = new FileStream(
            manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<StoredRecord>(stream, JsonOptions, token).ConfigureAwait(false) ??
               throw new InvalidDataException("The edited-mesh manifest was empty.");
    }

    /// <summary>
    /// Writes a record without its full skeleton report, which goes into the file beside it. An entry that was
    /// loaded carries no report: the one on disk is kept, moved beside the record when the record still holds it.
    /// </summary>
    private static async Task WriteRecordAsync(
        string manifestPath,
        SkeletalMeshEditRegistrationManifest manifest,
        CancellationToken token)
    {
        var directory = Path.GetDirectoryName(manifestPath)!;
        var preflight = manifest.Preflight;
        if (preflight.SkeletonCompatibility is null && File.Exists(manifestPath) &&
            await ReadStoredReportAsync(manifestPath, token).ConfigureAwait(false) is
            {
                Preflight: { SkeletonCompatibility: { } stored } storedPreflight,
            } &&
            storedPreflight.CreatedAt == preflight.CreatedAt &&
            string.Equals(storedPreflight.CandidateGlbSha256, preflight.CandidateGlbSha256,
                StringComparison.OrdinalIgnoreCase))
            preflight = preflight with { SkeletonCompatibility = stored };
        SkeletalMeshSkeletonCompatibilityFile.Write(directory, preflight);
        await WriteJsonAtomicAsync(
                manifestPath, manifest with { Preflight = SkeletalMeshSkeletonCompatibilityFile.Slim(preflight) }, token)
            .ConfigureAwait(false);
    }

    private string EnsureWithinRoot(string path) => EnsureWithinDirectory(RootDirectory, path);

    private static string EnsureWithinDirectory(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) +
                   Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path escapes the edited-mesh workspace: {fullPath}");
        return fullPath;
    }

    private static async Task CopyAtomicAsync(string source, string destination, CancellationToken token)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var input = new FileStream(
                             source, FileMode.Open, FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete, 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .Trim();
        if (string.IsNullOrWhiteSpace(safe)) safe = "edited-mesh";
        return safe.Length <= 80 ? safe : safe[..80];
    }
}

public enum SkeletalMeshEditCompactionOutcome
{
    Compacted,
    AlreadySlim,
    RecentlyChanged,
    Failed,
}

/// <summary>The stages of compacting one record, in order (see <see cref="SkeletalMeshEditWorkspaceStore.CompactAsync"/>).</summary>
public enum SkeletalMeshEditCompactionStep
{
    ReportWritten,
    ReportVerified,
    RecordWritten,
    RecordVerified,
    ReportPlaced,
}

/// <summary>One record's compaction. Bytes after count the slim record and the compressed report beside it.</summary>
public sealed record SkeletalMeshEditCompactionResult(
    string ManifestPath,
    SkeletalMeshEditCompactionOutcome Outcome,
    long BytesBefore,
    long BytesAfter,
    string? Problem);

public sealed record SkeletalMeshEditCompactionSummary(
    int Records,
    int Compacted,
    int AlreadySlim,
    int RecentlyChanged,
    int Failed,
    long BytesBefore,
    long BytesAfter,
    bool DryRun,
    bool Stopped,
    IReadOnlyList<SkeletalMeshEditCompactionResult> Problems)
{
    public long BytesSaved => BytesBefore - BytesAfter;
}
