using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Interface;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.StaticMeshes;

namespace Arkive.Core.Mods;

/// <summary>
/// Stands in for a working file's record in an <see cref="ArkiveModLocalEdit"/> built from the
/// <see cref="WorkingFileIndex"/>: the values the Mods list asks of the record, as they were when it was last read in
/// full. Only the Mods list's status and the install state see such an edit; anything that needs the record itself
/// reads it again (<see cref="ArkiveModEditSnapshot.ReloadAsync"/>).
/// </summary>
/// <param name="PortableSha256">What <see cref="ArkiveModAuthoringService.IsModifiedAsync"/> compares with the mod.</param>
/// <param name="BuildSha256">What <see cref="ArkiveModEditSnapshot.GetBuildContentSha256Async"/> answers.</param>
/// <param name="ChangesNothing">Whether the edit leaves the game's asset as it is.</param>
public sealed record ArkiveModIndexedEdit(string? PortableSha256, string? BuildSha256, bool ChangesNothing);

/// <summary>A file's length and write time when an index entry was made, or that it did not exist.</summary>
/// <param name="Path">Relative to the data folder when inside it, otherwise full.</param>
public sealed record WorkingFileStamp(string Path, bool Exists, long Length, long WriteTicks);

/// <summary>
/// One working-file record as the last full read saw it. <see cref="Files"/> holds the record first, then every file
/// the values depend on (the edited file whose hash is compared, the source record a mesh names); the entry stands only
/// while every one of them keeps its length and write time. The object-path note beside a record is not kept: nothing
/// the index serves uses it, and an edit made from the index reads it when asked.
/// </summary>
/// <param name="HasStatus">
/// The status values were worked out. They are only for edits some mod was bound to when the index was made: working
/// them out for the others would hash thousands of edited files nothing shows.
/// </param>
/// <param name="Settled">
/// False when a file changed during or just before the read, so the values might not match the stamps: the record is
/// then read again next time.
/// </param>
public sealed record WorkingFileIndexEntry(
    UnifiedPackageAssetKind Kind,
    string EditId,
    IReadOnlyList<WorkingFileStamp> Files,
    string DisplayName,
    string SourcePackagePath,
    string ObjectName,
    bool Included,
    bool HasStatus,
    string? PortableSha256,
    string? BuildSha256,
    bool ChangesNothing,
    bool Settled);

/// <param name="MissingIds">Edit ids a mod was bound to that the full read did not find.</param>
/// <param name="LibrarySha256">The mod library file the index was made with; <see cref="MissingIds"/> holds only for it.</param>
public sealed record WorkingFileIndexDocument(
    int FormatVersion,
    string ArkiveVersion,
    string DataRoot,
    DateTimeOffset BuiltAt,
    string? LibrarySha256,
    IReadOnlyList<string> MissingIds,
    IReadOnlyList<WorkingFileIndexEntry> Entries);

/// <summary>How a snapshot for launch was made (see <see cref="WorkingFileIndex.SnapshotAsync"/>).</summary>
/// <param name="FullRead">Every store was read, because the index was missing, unusable or incomplete.</param>
/// <param name="Verified">Entries whose files were checked unchanged.</param>
/// <param name="ReadAgain">Entries whose files changed, read again through their store.</param>
/// <param name="Gone">Entries whose record had gone or could no longer be read.</param>
/// <param name="FileChecks">Files whose length and write time were looked at.</param>
/// <param name="FallbackReason">Why every store was read, when one was.</param>
public sealed record WorkingFileIndexSnapshot(
    ArkiveModEditSnapshot Snapshot,
    bool FullRead,
    int Verified,
    int ReadAgain,
    int Gone,
    int FileChecks,
    string? FallbackReason,
    DateTime ReadStartUtc);

/// <summary>
/// One file under the data folder's cache that lets launch show the Mods list without opening every working-file record
/// (about 9,200 on a large library, 6 ms each when Windows has none cached). Each entry holds what the Mods list asks of
/// a record: what <see cref="ArkiveModLocalEdit"/> carries, whether the edit differs from its mod, the content its
/// build would hold and whether it changes nothing. An entry is used only while the record and every file its values
/// depend on keep their length and write time; otherwise that record is read again.
/// Nothing that writes (Apply, Save, Edit, install, restore, clean-up, release) uses it: they read the records as before.
/// </summary>
public static class WorkingFileIndex
{
    public const int FormatVersion = 1;
    public const string FileName = "working-file-index.json";

    // Write times move on with the file system's clock tick; a file written this close to the read may have changed
    // during it without its time showing that.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    private const int CheckParallelism = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, "cache", FileName);

    /// <summary>
    /// The index of <paramref name="dataRoot"/>, or null when it is missing, cannot be read, or was made by another
    /// version of Arkive or for another data folder: then launch reads every store and makes it again.
    /// </summary>
    public static WorkingFileIndexDocument? Load(string dataRoot, string arkiveVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        try
        {
            var path = PathFor(dataRoot);
            if (!File.Exists(path)) return null;
            var document = JsonSerializer.Deserialize<WorkingFileIndexDocument>(File.ReadAllBytes(path), JsonOptions);
            return document is { FormatVersion: FormatVersion, Entries: not null, MissingIds: not null } &&
                   document.ArkiveVersion == arkiveVersion &&
                   SameFolder(document.DataRoot, dataRoot) &&
                   document.Entries.All(entry => entry is { EditId: not null, Files.Count: > 0, DisplayName: not null,
                       SourcePackagePath: not null, ObjectName: not null })
                ? document
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                              NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Writes the index to a temporary file and moves it into place, so a reader never sees half of it.</summary>
    public static void Save(string dataRoot, WorkingFileIndexDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var path = PathFor(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// The index of a full read of every store (<paramref name="full"/>, begun at <paramref name="readStartUtc"/>).
    /// Status values are worked out for the edits in <paramref name="boundEditIds"/> (those some mod is bound to), the
    /// same way the Mods list works them out from the records.
    /// </summary>
    public static async Task<WorkingFileIndexDocument> BuildAsync(
        ArkiveModEditSnapshot full,
        DateTime readStartUtc,
        string dataRoot,
        string arkiveVersion,
        string? librarySha256,
        IReadOnlyCollection<string> boundEditIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(boundEditIds);
        if (full.FromIndex) throw new ArgumentException("An index is made only from a full read of the records.", nameof(full));
        var bound = boundEditIds as IReadOnlySet<string> ?? boundEditIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = new WorkingFileIndexEntry?[full.Edits.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, full.Edits.Count),
            new ParallelOptions { MaxDegreeOfParallelism = CheckParallelism, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var edit = full.Edits[index];
                if (edit.Entry is ArkiveModIndexedEdit) return;
                entries[index] = await EntryForAsync(edit, readStartUtc, dataRoot, bound.Contains(edit.EditId), token)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
        var found = full.Edits.Select(edit => edit.EditId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new WorkingFileIndexDocument(
            FormatVersion, arkiveVersion, Path.GetFullPath(dataRoot), DateTimeOffset.UtcNow, librarySha256,
            bound.Where(id => !found.Contains(id)).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            entries.OfType<WorkingFileIndexEntry>().ToArray());
    }

    /// <summary>
    /// The working files for launch: every edit the index holds, with the ones in <paramref name="verifyEditIds"/> (the
    /// edits bound to enabled mods) checked first. A checked entry whose files are unchanged is used as it is; one whose
    /// files changed is read again through its store, and left out when its record has gone, as a full read would.
    /// The rest are as last read in full; a later full read brings them up to date, except that an edit in
    /// <paramref name="boundEditIds"/> the index holds no status for is read again now. With no usable index, or an id in
    /// <paramref name="verifyEditIds"/> the index does not know (a working file added since), every store is read.
    /// </summary>
    public static async Task<WorkingFileIndexSnapshot> SnapshotAsync(
        WorkingFileIndexDocument? index,
        ArkiveModEditWorkspaces workspaces,
        IReadOnlyCollection<string> verifyEditIds,
        IReadOnlyCollection<string> boundEditIds,
        string dataRoot,
        string? librarySha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(verifyEditIds);
        ArgumentNullException.ThrowIfNull(boundEditIds);
        var readStart = DateTime.UtcNow;
        var verify = verifyEditIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bound = boundEditIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? fallback = null;
        if (index is null) fallback = "no usable index";
        else
        {
            var known = index.Entries.Select(entry => entry.EditId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // A bound id that was already missing at the last full read is still missing, unless the library has changed.
            var stillMissing = index.LibrarySha256 is not null && index.LibrarySha256 == librarySha256
                ? index.MissingIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            if (verify.FirstOrDefault(id => !known.Contains(id) && !stillMissing.Contains(id)) is { } unknown)
                fallback = $"the index does not know working file {unknown}";
        }
        if (fallback is not null)
        {
            var full = await ArkiveModEditSnapshot.LoadAsync(workspaces, cancellationToken).ConfigureAwait(false);
            return new WorkingFileIndexSnapshot(full, true, 0, 0, 0, 0, fallback, readStart);
        }

        var entries = index!.Entries;
        var edits = new ArkiveModLocalEdit?[entries.Count];
        int verified = 0, readAgain = 0, gone = 0, checks = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count),
            new ParallelOptions { MaxDegreeOfParallelism = CheckParallelism, CancellationToken = cancellationToken },
            async (position, token) =>
            {
                var entry = entries[position];
                var cached = EditFrom(entry, dataRoot);
                var check = verify.Contains(entry.EditId);
                // A mod bound since the index was made has no status values in it; its record is read for them.
                if (!check && (entry.HasStatus || !bound.Contains(entry.EditId)))
                {
                    edits[position] = cached;
                    return;
                }
                if (check)
                {
                    Interlocked.Add(ref checks, entry.Files.Count);
                    if (entry is { Settled: true, HasStatus: true } && entry.Files.All(file => Unchanged(file, dataRoot)))
                    {
                        edits[position] = cached;
                        Interlocked.Increment(ref verified);
                        return;
                    }
                }
                var fresh = await ArkiveModEditSnapshot.ReloadAsync(workspaces, cached, token).ConfigureAwait(false);
                edits[position] = fresh;
                if (fresh is null) Interlocked.Increment(ref gone);
                else Interlocked.Increment(ref readAgain);
            }).ConfigureAwait(false);
        var snapshot = ArkiveModEditSnapshot.FromIndexedEdits(edits.OfType<ArkiveModLocalEdit>().ToArray());
        return new WorkingFileIndexSnapshot(snapshot, false, verified, readAgain, gone, checks, null, readStart);
    }

    /// <summary>The edit an entry describes, with its values standing in for the record.</summary>
    public static ArkiveModLocalEdit EditFrom(WorkingFileIndexEntry entry, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ArkiveModLocalEdit(entry.Kind, entry.EditId, FullPath(entry.Files[0].Path, dataRoot), entry.DisplayName,
            entry.SourcePackagePath, entry.ObjectName, entry.Included,
            new ArkiveModIndexedEdit(entry.PortableSha256, entry.BuildSha256, entry.ChangesNothing));
    }

    private static async Task<WorkingFileIndexEntry> EntryForAsync(
        ArkiveModLocalEdit edit, DateTime readStartUtc, string dataRoot, bool withStatus, CancellationToken cancellationToken)
    {
        var paths = DependencyPaths(edit).Prepend(edit.ManifestPath).ToArray();
        var before = paths.Select(path => Stamp(path, dataRoot)).ToArray();
        string? portable = null, build = null;
        var nothing = false;
        if (withStatus)
        {
            portable = await ArkiveModAuthoringService.PortableContentSha256Async(edit, cancellationToken).ConfigureAwait(false);
            build = await ArkiveModEditSnapshot.GetBuildContentSha256Async(edit, cancellationToken).ConfigureAwait(false);
            nothing = ArkiveModApplyService.ChangesNothing(edit);
        }
        var after = paths.Select(path => Stamp(path, dataRoot)).ToArray();
        // Values were read from the record before 'before' was taken, so a file that changed after the read began could
        // hold something other than what they say; such an entry is read again next time.
        var settledBefore = (readStartUtc - SettleTime).Ticks;
        var settled = before.SequenceEqual(after) && after.All(stamp => !stamp.Exists || stamp.WriteTicks < settledBefore);
        return new WorkingFileIndexEntry(
            edit.Kind, edit.EditId, after, edit.DisplayName, edit.SourcePackagePath, edit.ObjectName,
            edit.Included, withStatus, portable, build, nothing, settled);
    }

    /// <summary>
    /// The files besides the record whose state the edit's values come from: the source records a mesh is read with,
    /// the edited file whose hash says whether it differs from its mod, the files whose presence decides what its build
    /// would hold, and an interface movie's original.
    /// </summary>
    internal static IEnumerable<string> DependencyPaths(ArkiveModLocalEdit edit)
    {
        switch (edit.Entry)
        {
            case GuidedTextureModProjectEntry texture:
                yield return texture.Manifest.EditableImagePath;
                break;
            case SkeletalMeshEditWorkspaceEntry mesh:
                yield return Path.GetFullPath(mesh.Manifest.SourceManifestPath);
                yield return mesh.EditedGlbPath;
                break;
            case StaticMeshEditEntry mesh:
                yield return mesh.Manifest.SourceEditableManifestPath;
                yield return mesh.Manifest.EditedGlbPath;
                break;
            case MaterialInstanceEditEntry material when !string.IsNullOrWhiteSpace(material.Document.LatestCandidateManifestPath):
                yield return material.Document.LatestCandidateManifestPath;
                break;
            case InterfaceMovieEditEntry movie:
                yield return movie.Manifest.EditedMoviePath;
                yield return movie.Manifest.OriginalMoviePath;
                break;
        }
    }

    private static WorkingFileStamp Stamp(string path, string dataRoot)
    {
        var info = new FileInfo(path);
        var stored = StoredPath(path, dataRoot);
        return info.Exists
            ? new WorkingFileStamp(stored, true, info.Length, info.LastWriteTimeUtc.Ticks)
            : new WorkingFileStamp(stored, false, 0, 0);
    }

    private static bool Unchanged(WorkingFileStamp stamp, string dataRoot)
    {
        var info = new FileInfo(FullPath(stamp.Path, dataRoot));
        return info.Exists == stamp.Exists &&
               (!stamp.Exists || info.Length == stamp.Length && info.LastWriteTimeUtc.Ticks == stamp.WriteTicks);
    }

    private static string StoredPath(string path, string dataRoot)
    {
        var root = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path[root.Length..] : path;
    }

    private static string FullPath(string stored, string dataRoot) =>
        Path.IsPathRooted(stored) ? stored : Path.Join(Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar), stored);

    private static bool SameFolder(string? left, string right) =>
        left is not null && Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
