using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkive.Core.Mods;

/// <summary>What the Mods list reads of a package project's install state (<see cref="PackageDeploymentState"/>).</summary>
/// <param name="Revision">The build the state is about, from the package project record.</param>
/// <param name="Active">The check proved this build is in the game now.</param>
/// <param name="InstalledByThisArkive">What <see cref="ArkiveModApplyService.ProvesInstalledByThisArkive"/> says of the state.</param>
/// <param name="ForDeletionIdentityOnly">An earlier build than the latest.</param>
public sealed record PackageInstallSummary(
    GuidedTexturePackageRevision Revision,
    bool Active,
    bool InstalledByThisArkive,
    bool ForDeletionIdentityOnly)
{
    public static PackageInstallSummary? Of(PackageDeploymentState? state) => state is null
        ? null
        : new PackageInstallSummary(state.Revision, state.IsActive, ArkiveModApplyService.ProvesInstalledByThisArkive(state),
            state.ForDeletionIdentityOnly);

    /// <summary>The same build and answers, for comparing a remembered state with one checked again.</summary>
    public static bool Same(PackageInstallSummary? left, PackageInstallSummary? right) =>
        left is null || right is null
            ? left is null && right is null
            : left.Revision.RevisionId.Equals(right.Revision.RevisionId, StringComparison.OrdinalIgnoreCase) &&
              left.Active == right.Active && left.InstalledByThisArkive == right.InstalledByThisArkive &&
              left.ForDeletionIdentityOnly == right.ForDeletionIdentityOnly;

    public static string Describe(PackageInstallSummary? summary) => summary is null
        ? "no build"
        : $"build={summary.Revision.RevisionId}, inGame={summary.Active}, installedHere={summary.InstalledByThisArkive}" +
          (summary.ForDeletionIdentityOnly ? ", earlier build" : string.Empty);
}

/// <summary>
/// One package project's install state as a real check found it, with every file it was worked out from.
/// </summary>
/// <param name="ManifestPath">The package project record, relative to the data folder when inside it.</param>
/// <param name="RecordFingerprint">
/// The parts of the record the check reads (<see cref="PackageInstallMemo.Fingerprint"/>). Launch reads every record
/// anyway, so its content is compared instead of its length and write time: no extra look, and exact.
/// </param>
/// <param name="HasBuild">False when the project had no build file, so it has no state.</param>
/// <param name="Files">
/// Each file the state depends on (<see cref="PackageInstallStateService.DependencyPathsAsync"/>) with its length and
/// write time, or that it did not exist; the entry stands only while every one is unchanged.
/// </param>
public sealed record PackageInstallMemoEntry(
    string ManifestPath,
    string RecordFingerprint,
    bool HasBuild,
    string? RevisionId,
    bool Active,
    bool InstalledByThisArkive,
    bool ForDeletionIdentityOnly,
    IReadOnlyList<WorkingFileStamp> Files);

public sealed record PackageInstallMemoDocument(
    int FormatVersion,
    string ArkiveVersion,
    string DataRoot,
    string? GameDirectory,
    DateTimeOffset BuiltAt,
    IReadOnlyList<PackageInstallMemoEntry> Entries);

/// <summary>A package project's install state for the Mods list, and where it came from.</summary>
/// <param name="FromMemo">Taken from the memo, its files checked unchanged; otherwise checked by the service now.</param>
/// <param name="Entry">
/// What the memo holds for the project after this: the entry used, or the one the check now made; null when the state
/// cannot be remembered (a check was inconclusive, or a file changed while it ran or just before).
/// </param>
public sealed record PackageInstallCheck(
    GuidedTexturePackageEntry Package,
    PackageInstallSummary? Summary,
    bool FromMemo,
    PackageInstallMemoEntry? Entry);

/// <param name="FileChecks">Files whose length and write time were looked at to decide whether an entry still holds.</param>
public sealed record PackageInstallChecks(
    IReadOnlyList<PackageInstallCheck> Packages,
    int FromMemo,
    int Inspected,
    int NotRemembered,
    int FileChecks);

/// <summary>
/// The install state of each package project as the last real check found it, kept in the data folder's cache, so
/// launch need not look at every build, receipt, backup and game file of about a thousand package projects before the
/// Mods list is checked (about 25,000 file operations, each costing milliseconds when Windows has none cached). An
/// entry is used only while the package project record reads the same and every file the state was worked out from
/// keeps its length and write time (and an absent one is still absent); otherwise the package is checked again by
/// <see cref="PackageInstallStateService"/> and the entry made anew.
/// It is only ever shown: launch's Mods list status uses it, and the background pass after launch checks every
/// package again with the service and corrects the list. Nothing that writes (Apply, install, restore, clean-up,
/// release, delete, mod sets) and no package view reads it; they run their own checks as before.
/// </summary>
public static class PackageInstallMemo
{
    public const int FormatVersion = 1;
    public const string FileName = "package-install-state.json";

    // Write times only move on with the file system's clock tick, so a file written this close to a check may have
    // changed during it without its time showing that; such a state is not remembered.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, "cache", FileName);

    /// <summary>
    /// The memo of <paramref name="dataRoot"/>, or null when it is missing, cannot be read, or was made by another
    /// format or version of Arkive, for another data folder or another game folder.
    /// </summary>
    public static PackageInstallMemoDocument? Load(string dataRoot, string arkiveVersion, string? gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        try
        {
            var path = PathFor(dataRoot);
            if (!File.Exists(path)) return null;
            var document = JsonSerializer.Deserialize<PackageInstallMemoDocument>(File.ReadAllBytes(path), JsonOptions);
            return document is { FormatVersion: FormatVersion, Entries: not null } &&
                   document.ArkiveVersion == arkiveVersion &&
                   WorkingFileIndex.SameFolder(document.DataRoot, dataRoot) &&
                   (document.GameDirectory is null
                       ? gameDirectory is null
                       : gameDirectory is not null && WorkingFileIndex.SameFolder(document.GameDirectory, gameDirectory)) &&
                   document.Entries.All(entry => entry is { ManifestPath: not null, RecordFingerprint: not null, Files: not null } &&
                                                 entry.Files.All(file => file is { Path: not null }) &&
                                                 (!entry.HasBuild || entry.RevisionId is not null))
                ? document
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                              NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Writes the memo to a temporary file and moves it into place, so a reader never sees half of it.</summary>
    public static void Save(string dataRoot, PackageInstallMemoDocument document)
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

    /// <summary>The memo holding <paramref name="checks"/>' entries.</summary>
    public static PackageInstallMemoDocument Document(
        PackageInstallChecks checks, string dataRoot, string arkiveVersion, string? gameDirectory)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return new PackageInstallMemoDocument(FormatVersion, arkiveVersion, Path.GetFullPath(dataRoot),
            gameDirectory is null ? null : Path.GetFullPath(gameDirectory), DateTimeOffset.UtcNow,
            checks.Packages.Select(check => check.Entry).OfType<PackageInstallMemoEntry>().ToArray());
    }

    /// <summary>
    /// What <see cref="PackageInstallStateService.ResolveAsync"/> reads of a package project record: which builds it
    /// lists, when each was made, where its build file is, and which is the latest.
    /// </summary>
    public static string Fingerprint(GuidedTexturePackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var text = new StringBuilder().Append(manifest.LatestRevisionId).Append('\n');
        foreach (var revision in manifest.Revisions)
            text.Append(revision.RevisionId).Append('|').Append(revision.CreatedAt.UtcTicks).Append('|')
                .Append(revision.ModBundleManifestPath).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// The install state of each of <paramref name="packages"/> (in that order), <paramref name="parallelism"/> at a
    /// time. With <paramref name="useMemo"/>, a package whose entry in <paramref name="memo"/> still holds takes it;
    /// every other package is checked by <paramref name="service"/> and gets a new entry when its state can be
    /// remembered. Without, every package is checked (the background pass), and <paramref name="memo"/> only helps
    /// notice a file that changed while its package was checked.
    /// </summary>
    public static async Task<PackageInstallChecks> CheckAllAsync(
        PackageInstallStateService service,
        IReadOnlyList<GuidedTexturePackageEntry> packages,
        PackageInstallMemoDocument? memo,
        string dataRoot,
        bool useMemo,
        int parallelism = PackageInstallStateService.Parallelism,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(packages);
        var known = new Dictionary<string, PackageInstallMemoEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in memo?.Entries ?? [])
            known.TryAdd(Path.GetFullPath(WorkingFileIndex.FullPath(entry.ManifestPath, dataRoot)), entry);
        var results = new PackageInstallCheck[packages.Count];
        int fromMemo = 0, inspected = 0, notRemembered = 0, checks = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, packages.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken },
            async (position, token) =>
            {
                var package = packages[position];
                known.TryGetValue(Path.GetFullPath(package.ManifestPath), out var old);
                if (useMemo && old is not null)
                {
                    var (holds, summary, looked) = Recall(old, package, dataRoot);
                    Interlocked.Add(ref checks, looked);
                    if (holds)
                    {
                        results[position] = new PackageInstallCheck(package, summary, true, old);
                        Interlocked.Increment(ref fromMemo);
                        return;
                    }
                }
                var check = await InspectAsync(service, package, old, dataRoot, token).ConfigureAwait(false);
                results[position] = check;
                Interlocked.Increment(ref inspected);
                if (check.Entry is null) Interlocked.Increment(ref notRemembered);
            }).ConfigureAwait(false);
        return new PackageInstallChecks(results, fromMemo, inspected, notRemembered, checks);
    }

    /// <summary>
    /// The state <paramref name="entry"/> holds for <paramref name="package"/>, when the record reads the same and every
    /// stamped file is unchanged; with the number of files looked at (it stops at the first change).
    /// </summary>
    public static (bool Holds, PackageInstallSummary? Summary, int FileChecks) Recall(
        PackageInstallMemoEntry entry, GuidedTexturePackageEntry package, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(package);
        if (!entry.RecordFingerprint.Equals(Fingerprint(package.Manifest), StringComparison.Ordinal)) return (false, null, 0);
        PackageInstallSummary? summary = null;
        if (entry.HasBuild)
        {
            var named = package.Manifest.Revisions.Where(revision => revision.RevisionId.Equals(
                entry.RevisionId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (named.Length != 1) return (false, null, 0);
            summary = new PackageInstallSummary(named[0], entry.Active, entry.InstalledByThisArkive, entry.ForDeletionIdentityOnly);
        }
        var looked = 0;
        foreach (var file in entry.Files)
        {
            looked++;
            if (!WorkingFileIndex.Unchanged(file, dataRoot)) return (false, null, looked);
        }
        return (true, summary, looked);
    }

    /// <summary>
    /// Checks <paramref name="package"/> with the service and makes its entry. The files are stamped after the check;
    /// the entry is kept only if none was written in the settle time before the check began or since, and none the
    /// previous entry or the record names changed between just before the check and just after it.
    /// </summary>
    private static async Task<PackageInstallCheck> InspectAsync(
        PackageInstallStateService service,
        GuidedTexturePackageEntry package,
        PackageInstallMemoEntry? old,
        string dataRoot,
        CancellationToken cancellationToken)
    {
        var before = new Dictionary<string, WorkingFileStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in (old?.Files.Select(file => WorkingFileIndex.FullPath(file.Path, dataRoot)) ?? [])
                 .Concat(package.Manifest.Revisions.Select(revision => revision.ModBundleManifestPath)))
        {
            var full = Path.GetFullPath(path);
            if (!before.ContainsKey(full)) before[full] = WorkingFileIndex.Stamp(full, dataRoot);
        }
        var settledBefore = (DateTime.UtcNow - SettleTime).Ticks;
        var (state, files) = await service.ResolveWithFilesAsync(package, cancellationToken).ConfigureAwait(false);
        var summary = PackageInstallSummary.Of(state);
        if (files is null) return new PackageInstallCheck(package, summary, false, null);
        var stamps = files.Select(path => WorkingFileIndex.Stamp(path, dataRoot)).ToArray();
        var steady = stamps.All(stamp =>
            (!stamp.Exists || stamp.WriteTicks < settledBefore) &&
            (!before.TryGetValue(Path.GetFullPath(WorkingFileIndex.FullPath(stamp.Path, dataRoot)), out var earlier) ||
             earlier.Exists == stamp.Exists && earlier.Length == stamp.Length && earlier.WriteTicks == stamp.WriteTicks));
        if (!steady) return new PackageInstallCheck(package, summary, false, null);
        var root = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var manifestPath = Path.GetFullPath(package.ManifestPath);
        var entry = new PackageInstallMemoEntry(
            manifestPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? manifestPath[root.Length..] : manifestPath,
            Fingerprint(package.Manifest), summary is not null, summary?.Revision.RevisionId,
            summary?.Active == true, summary?.InstalledByThisArkive == true, summary?.ForDeletionIdentityOnly == true, stamps);
        return new PackageInstallCheck(package, summary, false, entry);
    }
}
