using System.Security.Cryptography;
using System.Text.Json;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Packages;

namespace Arkive.Core.Baselines;

public sealed record ClientPackageSnapshot(string RelativePath, long Length, long LastWriteUtcTicks);

public sealed record ClientSnapshotDocument(
    int SchemaVersion,
    string SnapshotId,
    string Label,
    DateTimeOffset CreatedAt,
    string LibraryRoot,
    IReadOnlyList<ClientPackageSnapshot> Packages,
    long PackageBytes,
    PackageIndexManifest? IndexManifest,
    string? GameMetadataPath,
    string? GameMetadataSha256);

public enum ClientPackageChangeKind { Added, Changed, Missing }

public sealed record ClientPackageChange(
    ClientPackageChangeKind Kind,
    string RelativePath,
    long? PreviousLength,
    long? CurrentLength,
    long? PreviousLastWriteUtcTicks,
    long? CurrentLastWriteUtcTicks);

public sealed record ClientSnapshotComparison(
    ClientSnapshotDocument Snapshot,
    int CurrentPackageCount,
    int UnchangedPackages,
    int AddedPackages,
    int ChangedPackages,
    int MissingPackages,
    IReadOnlyList<ClientPackageChange> Changes,
    TimeSpan Duration);

public sealed record AssetBaselineRequest(
    string PackagePath,
    string PackageAlias,
    int ExportIndex,
    AssetKind Kind,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    IReadOnlyList<string> InGameNames);

public sealed record AssetStorageRange(
    string StorageKind,
    int? ChunkNumber,
    long LogicalOffset,
    long LogicalSize,
    long PhysicalOffset,
    long PhysicalSize,
    uint? LostArkFlags);

public sealed record AssetPackageProfile(
    ushort FileVersion,
    ushort LicenseeVersion,
    int EngineVersion,
    int CookerVersion,
    uint PackageFlags,
    uint CompressionFlags,
    Guid PackageGuid,
    int ChunkCount,
    long LogicalLength);

public sealed record AssetBaselineDocument(
    int SchemaVersion,
    string BaselineId,
    DateTimeOffset CreatedAt,
    string PackagePath,
    string PhysicalPackageName,
    string PackageAlias,
    long PackageLength,
    long PackageLastWriteUtcTicks,
    string PackageSha256,
    int ExportIndex,
    AssetKind Kind,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    IReadOnlyList<string> InGameNames,
    AssetPackageProfile? PackageProfile,
    IReadOnlyList<AssetStorageRange> StorageRanges,
    string? LogicalPayloadSha256,
    string LogicalPayloadHashStatus,
    string ProtectionProviderName);

public sealed record ClientSnapshotEntry(string ManifestPath, ClientSnapshotDocument Snapshot);

public sealed record AssetBaselineEntry(string ManifestPath, AssetBaselineDocument Baseline);

public sealed record BaselineWorkspaceSnapshot(
    string RootDirectory,
    IReadOnlyList<ClientSnapshotEntry> ClientSnapshots,
    IReadOnlyList<AssetBaselineEntry> AssetBaselines,
    int UnreadableManifestCount,
    long ManifestBytes);

public sealed class BaselineWorkspaceStore
{
    public const int CurrentSchemaVersion = 1;
    private const string SnapshotPrefix = "arkive-client-snapshot-";
    private const string AssetBaselineFileName = "arkive-asset-baseline.json";
    private const string HashCacheFileName = "package-hashes.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _hashCacheGate = new(1, 1);

    public BaselineWorkspaceStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public Task<string> GetPackageSha256Async(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var file = new FileInfo(Path.GetFullPath(packagePath));
        if (!file.Exists)
        {
            throw new FileNotFoundException("The package to hash was not found.", file.FullName);
        }

        return GetPackageHashAsync(file, cancellationToken);
    }

    public async Task<ClientSnapshotEntry> CreateClientSnapshotAsync(
        string libraryRoot,
        PackageIndexManifest? indexManifest,
        string? gameMetadataPath,
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(libraryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The package folder was not found: {root}");
        }

        var packages = await Task.Run(
            () => InventoryPackages(root, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        string? metadataPath = null;
        string? metadataHash = null;
        if (!string.IsNullOrWhiteSpace(gameMetadataPath) && File.Exists(gameMetadataPath))
        {
            metadataPath = Path.GetFullPath(gameMetadataPath);
            metadataHash = await HashFileAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        }

        var created = DateTimeOffset.UtcNow;
        var id = $"{created:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var snapshot = new ClientSnapshotDocument(
            CurrentSchemaVersion,
            id,
            string.IsNullOrWhiteSpace(label) ? created.ToLocalTime().ToString("g") : label.Trim(),
            created,
            root,
            packages,
            packages.Sum(package => package.Length),
            indexManifest,
            metadataPath,
            metadataHash);
        var path = Path.Combine(RootDirectory, "snapshots", SnapshotPrefix + id + ".json");
        await SaveAtomicAsync(path, snapshot, cancellationToken, compact: true).ConfigureAwait(false);
        return new ClientSnapshotEntry(path, snapshot);
    }

    public async Task<ClientSnapshotComparison> CompareToCurrentAsync(
        ClientSnapshotDocument snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var current = await Task.Run(
            () => InventoryPackages(snapshot.LibraryRoot, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var previousByPath = snapshot.Packages.ToDictionary(
            package => package.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        var currentByPath = current.ToDictionary(
            package => package.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        var changes = new List<ClientPackageChange>();
        var unchanged = 0;
        foreach (var previous in snapshot.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!currentByPath.TryGetValue(previous.RelativePath, out var item))
            {
                changes.Add(new ClientPackageChange(
                    ClientPackageChangeKind.Missing, previous.RelativePath,
                    previous.Length, null, previous.LastWriteUtcTicks, null));
            }
            else if (previous.Length != item.Length ||
                     previous.LastWriteUtcTicks != item.LastWriteUtcTicks)
            {
                changes.Add(new ClientPackageChange(
                    ClientPackageChangeKind.Changed, previous.RelativePath,
                    previous.Length, item.Length,
                    previous.LastWriteUtcTicks, item.LastWriteUtcTicks));
            }
            else
            {
                unchanged++;
            }
        }

        foreach (var item in current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!previousByPath.ContainsKey(item.RelativePath))
            {
                changes.Add(new ClientPackageChange(
                    ClientPackageChangeKind.Added, item.RelativePath,
                    null, item.Length, null, item.LastWriteUtcTicks));
            }
        }

        changes.Sort((left, right) =>
        {
            var kind = left.Kind.CompareTo(right.Kind);
            return kind != 0
                ? kind
                : StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath);
        });
        started.Stop();
        return new ClientSnapshotComparison(
            snapshot,
            current.Count,
            unchanged,
            changes.Count(change => change.Kind == ClientPackageChangeKind.Added),
            changes.Count(change => change.Kind == ClientPackageChangeKind.Changed),
            changes.Count(change => change.Kind == ClientPackageChangeKind.Missing),
            changes,
            started.Elapsed);
    }

    public async Task<AssetBaselineEntry> CreateAssetBaselineAsync(
        AssetBaselineRequest request,
        IChunkProtectionProvider? protectionProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var packagePath = Path.GetFullPath(request.PackagePath);
        var file = new FileInfo(packagePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The asset's source package was not found.", packagePath);
        }

        var packageHash = await GetPackageHashAsync(file, cancellationToken).ConfigureAwait(false);
        var provider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        AssetPackageProfile? profile = null;
        IReadOnlyList<AssetStorageRange> storageRanges = [];
        string? payloadHash = null;
        var payloadStatus = "Not attempted";
        try
        {
            var summary = new UpkPackageReader().Read(packagePath);
            using var logical = new LogicalPackageStream(summary, provider);
            profile = new AssetPackageProfile(
                summary.FileVersion, summary.LicenseeVersion,
                summary.EngineVersion, summary.CookerVersion,
                summary.PackageFlags, summary.CompressionFlags,
                summary.PackageGuid, summary.CompressedChunks.Count, logical.Length);
            storageRanges = BuildStorageRanges(summary, request.SerialOffset, request.SerialSize);
            if (request.SerialOffset < 0 || request.SerialSize < 0 ||
                request.SerialOffset > logical.Length - request.SerialSize)
            {
                payloadStatus =
                    $"Logical range 0x{request.SerialOffset:X}+0x{request.SerialSize:X} is outside 0x{logical.Length:X}.";
            }
            else
            {
                payloadHash = await Task.Run(
                    () => HashLogicalRange(logical, request.SerialOffset, request.SerialSize, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                payloadStatus = "Hashed from Arkive's logical package stream";
            }
        }
        catch (Exception exception) when (
            exception is PackageAccessException or PackageFormatException or IOException or UnauthorizedAccessException)
        {
            payloadStatus = $"Unavailable: {exception.Message}";
        }

        var created = DateTimeOffset.UtcNow;
        var id = $"{created:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var baseline = new AssetBaselineDocument(
            CurrentSchemaVersion,
            id,
            created,
            packagePath,
            file.Name,
            request.PackageAlias,
            file.Length,
            file.LastWriteTimeUtc.Ticks,
            packageHash,
            request.ExportIndex,
            request.Kind,
            request.ClassName,
            request.ObjectName,
            request.SerialOffset,
            request.SerialSize,
            request.InGameNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            profile,
            storageRanges,
            payloadHash,
            payloadStatus,
            provider.Name);
        var directory = Path.Combine(
            RootDirectory, "assets", SafePathSegment(request.PackageAlias),
            SafePathSegment(request.ObjectName) + "-" + id);
        var path = Path.Combine(directory, AssetBaselineFileName);
        await SaveAtomicAsync(path, baseline, cancellationToken).ConfigureAwait(false);
        return new AssetBaselineEntry(path, baseline);
    }

    public async Task<BaselineWorkspaceSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory))
        {
            return new BaselineWorkspaceSnapshot(RootDirectory, [], [], 0, 0);
        }

        var snapshots = new List<ClientSnapshotEntry>();
        var baselines = new List<AssetBaselineEntry>();
        var unreadable = 0;
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(RootDirectory, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (name.Equals(HashCacheFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                bytes += new FileInfo(path).Length;
                await using var stream = File.OpenRead(path);
                if (name.StartsWith(SnapshotPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var snapshot = await JsonSerializer.DeserializeAsync<ClientSnapshotDocument>(
                        stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                    if (snapshot is null || snapshot.SchemaVersion != CurrentSchemaVersion)
                    {
                        unreadable++;
                    }
                    else
                    {
                        snapshots.Add(new ClientSnapshotEntry(path, snapshot));
                    }
                }
                else if (name.Equals(AssetBaselineFileName, StringComparison.OrdinalIgnoreCase))
                {
                    var baseline = await JsonSerializer.DeserializeAsync<AssetBaselineDocument>(
                        stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                    if (baseline is null || baseline.SchemaVersion != CurrentSchemaVersion)
                    {
                        unreadable++;
                    }
                    else
                    {
                        baselines.Add(new AssetBaselineEntry(path, baseline));
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                unreadable++;
            }
        }

        return new BaselineWorkspaceSnapshot(
            RootDirectory,
            snapshots.OrderByDescending(item => item.Snapshot.CreatedAt).ToArray(),
            baselines.OrderByDescending(item => item.Baseline.CreatedAt).ToArray(),
            unreadable,
            bytes);
    }

    private static IReadOnlyList<ClientPackageSnapshot> InventoryPackages(
        string root,
        CancellationToken cancellationToken)
    {
        var packages = new List<ClientPackageSnapshot>();
        foreach (var path in Directory.EnumerateFiles(root, "*.upk", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            packages.Add(new ClientPackageSnapshot(
                Path.GetRelativePath(root, file.FullName), file.Length, file.LastWriteTimeUtc.Ticks));
        }

        packages.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        return packages;
    }

    private async Task<string> GetPackageHashAsync(FileInfo file, CancellationToken cancellationToken)
    {
        await _hashCacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cachePath = Path.Combine(RootDirectory, HashCacheFileName);
            var cache = await LoadHashCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
            var key = file.FullName.ToUpperInvariant();
            if (cache.Entries.TryGetValue(key, out var entry) &&
                entry.Length == file.Length &&
                entry.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks)
            {
                return entry.Sha256;
            }

            var hash = await HashFileAsync(file.FullName, cancellationToken).ConfigureAwait(false);
            cache.Entries[key] = new PackageHashCacheEntry(
                file.Length, file.LastWriteTimeUtc.Ticks, hash, DateTimeOffset.UtcNow);
            await SaveAtomicAsync(cachePath, cache, cancellationToken, compact: true).ConfigureAwait(false);
            return hash;
        }
        finally
        {
            _hashCacheGate.Release();
        }
    }

    private static async Task<PackageHashCacheDocument> LoadHashCacheAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new PackageHashCacheDocument(
                new Dictionary<string, PackageHashCacheEntry>(StringComparer.OrdinalIgnoreCase));
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var document = await JsonSerializer.DeserializeAsync<PackageHashCacheDocument>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return new PackageHashCacheDocument(
                new Dictionary<string, PackageHashCacheEntry>(
                    document?.Entries ?? [], StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return new PackageHashCacheDocument(
                new Dictionary<string, PackageHashCacheEntry>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static IReadOnlyList<AssetStorageRange> BuildStorageRanges(
        PackageSummary summary,
        long serialOffset,
        long serialSize)
    {
        if (serialOffset < 0 || serialSize <= 0)
        {
            return [];
        }

        var end = checked(serialOffset + serialSize);
        var ranges = new List<AssetStorageRange>();
        var firstChunkOffset = summary.CompressedChunks.Count == 0
            ? summary.PhysicalFileSize
            : summary.CompressedChunks[0].UncompressedOffset;
        var rawEnd = Math.Min(end, (long)firstChunkOffset);
        if (serialOffset < rawEnd)
        {
            ranges.Add(new AssetStorageRange(
                "Raw physical bytes", null, serialOffset, rawEnd - serialOffset,
                serialOffset, rawEnd - serialOffset, null));
        }

        for (var index = 0; index < summary.CompressedChunks.Count; index++)
        {
            var chunk = summary.CompressedChunks[index];
            var overlapStart = Math.Max(serialOffset, chunk.UncompressedOffset);
            var overlapEnd = Math.Min(end, checked((long)chunk.UncompressedEnd));
            if (overlapStart >= overlapEnd)
            {
                continue;
            }

            ranges.Add(new AssetStorageRange(
                "Compressed chunk", index + 1, overlapStart, overlapEnd - overlapStart,
                chunk.CompressedOffset, chunk.CompressedSize, chunk.LostArkFlags));
        }

        return ranges;
    }

    private static string HashLogicalRange(
        Stream stream,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        stream.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException("The logical asset payload ended before its declared size.");
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false));
    }

    private static async Task SaveAtomicAsync<T>(
        string path,
        T document,
        CancellationToken cancellationToken,
        bool compact = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    compact ? CompactJsonOptions : JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string SafePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "unnamed";
        }

        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }

    private sealed record PackageHashCacheDocument(
        Dictionary<string, PackageHashCacheEntry> Entries);

    private sealed record PackageHashCacheEntry(
        long Length,
        long LastWriteUtcTicks,
        string Sha256,
        DateTimeOffset HashedAt);
}
