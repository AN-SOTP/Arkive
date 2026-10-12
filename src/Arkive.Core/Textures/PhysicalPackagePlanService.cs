using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Packages;

namespace Arkive.Core.Textures;

public sealed record PhysicalPackagePlanRequest(
    string LogicalPackageImageManifestPath,
    string OutputDirectory);

public sealed record PhysicalChunkBlockPlan(
    int BlockIndex,
    long DescriptorPhysicalOffset,
    long PayloadPhysicalOffset,
    int StoredBytes,
    int UncompressedBytes,
    int ProtectedPrefixBytes,
    string StoredSha256,
    string PlainCompressedSha256,
    bool ProtectionReplayed);

public sealed record PhysicalChunkPlan(
    int ChunkIndex,
    long RecordPhysicalOffset,
    long UncompressedOffsetFieldPhysicalOffset,
    long UncompressedSizeFieldPhysicalOffset,
    long CompressedOffsetFieldPhysicalOffset,
    long CompressedSizeFieldPhysicalOffset,
    long LostArkFlagsFieldPhysicalOffset,
    uint LogicalOffset,
    uint LogicalBytes,
    uint PhysicalOffset,
    uint PhysicalBytes,
    uint LostArkFlags,
    string StorageKind,
    int? BlockSize,
    int BlockCount,
    string PhysicalSha256,
    string LogicalSha256,
    bool EnvelopeParsed,
    bool ProtectionReadable,
    bool ContainsReplacementTablePatch,
    bool ReceivesLogicalAppend,
    long PlannedReplacementLogicalBytes,
    string ReplacementAction,
    IReadOnlyList<PhysicalChunkBlockPlan> Blocks);

public sealed record PhysicalCopyRegionPlan(
    int RegionIndex,
    string Kind,
    long PhysicalOffset,
    long Bytes,
    string Sha256,
    int? ChunkIndex,
    string Meaning);

public sealed record PhysicalPackagePlanManifest(
    int SchemaVersion,
    string PhysicalPlanId,
    DateTimeOffset CreatedAt,
    string LogicalPackageImageManifestPath,
    string LogicalImageId,
    string PackageRewritePlanManifestPath,
    string RewritePlanId,
    string SourcePackagePath,
    string SourcePackageSha256,
    long SourcePhysicalPackageBytes,
    long SourceLogicalPackageBytes,
    string SourceLogicalPackageSha256,
    string PackageAlias,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    string PixelFormat,
    uint CompressionFlags,
    long CompressionFlagsPhysicalOffset,
    long CompressedChunkCountPhysicalOffset,
    long CompressedChunkTablePhysicalOffset,
    long PackageSourcePhysicalOffset,
    int CompressedChunkCount,
    int ProtectedChunkCount,
    int RawChunkCount,
    int TotalBlockCount,
    long PhysicalPrefixBytes,
    string PhysicalPrefixSha256,
    long SourceLogicalPrefixBytes,
    string SourceLogicalPrefixSha256,
    long ReplacementLogicalImageBytes,
    long ReplacementLogicalGrowthBytes,
    int ReplacementAffectedChunkCount,
    bool AllChunkEnvelopesParsed,
    bool AllProtectedBlocksReadable,
    bool LogicalSourceIdentityConfirmed,
    bool CopyRegionsCoverPhysicalFile,
    bool PlanReadyForNoOpReconstruction,
    bool ReplacementChunkLayoutUnderstood,
    bool ReplacementPhysicalWriterReady,
    bool PackageWasModified,
    IReadOnlyList<TextureLayoutCheck> Checks,
    IReadOnlyList<PhysicalChunkPlan> Chunks,
    IReadOnlyList<PhysicalCopyRegionPlan> CopyRegions,
    double DurationSeconds);

public sealed record PhysicalPackagePlanResult(
    string OutputDirectory,
    string ManifestPath,
    PhysicalPackagePlanManifest Manifest,
    IReadOnlyList<string> OutputFiles,
    TimeSpan Duration);

public sealed class PhysicalPackagePlanRefusalException : NotSupportedException
{
    public PhysicalPackagePlanRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Inventories the exact source-UPK physical layout required for a copy-preserving
/// no-op reconstruction and identifies which existing chunks a later replacement
/// writer must rebuild. This stage is read-only and emits planning evidence only.
/// </summary>
public sealed class PhysicalPackagePlanService
{
    public const int CurrentManifestSchemaVersion = 1;
    public const string ManifestFileName = "arkive-physical-package-plan.json";

    private const int BufferBytes = 1024 * 1024;
    private const int MaximumBlockCount = 1_000_000;
    private const int MaximumBlockSize = 256 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly UpkPackageReader _summaryReader;
    private readonly IChunkProtectionProvider _protectionProvider;

    public PhysicalPackagePlanService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _summaryReader = summaryReader ?? new UpkPackageReader();
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
    }

    public static async Task<PhysicalPackagePlanManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = RequireFile(path, "PPP001", "physical package-plan manifest");
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<PhysicalPackagePlanManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new PhysicalPackagePlanRefusalException(
                           "PPP002", "The physical package-plan manifest was empty.");
        if (manifest.SchemaVersion != CurrentManifestSchemaVersion)
        {
            Refuse("PPP003", $"Physical package-plan schema {manifest.SchemaVersion} is unsupported.");
        }
        return manifest;
    }

    public async Task<PhysicalPackagePlanResult> PlanAsync(
        PhysicalPackagePlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var logicalManifestPath = RequireFile(
            request.LogicalPackageImageManifestPath, "PPP004", "logical package-image manifest");
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            Refuse("PPP005", "The physical package-plan output directory must be empty.");
        }

        var logicalManifest = await LogicalPackageImageService.LoadManifestAsync(
            logicalManifestPath, cancellationToken).ConfigureAwait(false);
        if (!logicalManifest.LogicalImageReadyForPhysicalWriter ||
            !logicalManifest.AllUnplannedSourceBytesIdentical ||
            !logicalManifest.AllTablePatchesApplied ||
            !logicalManifest.AllBulkOffsetsRebased ||
            !logicalManifest.OriginalExportBytesRetained ||
            !logicalManifest.AppendedReplacementVerified ||
            !logicalManifest.SourcePackageIdentityRetained ||
            logicalManifest.PackageWasModified)
        {
            Refuse("PPP006", "A fully verified, staging-only logical package image is required.");
        }

        var logicalImagePath = ResolveWithin(
            Path.GetDirectoryName(logicalManifestPath)!,
            logicalManifest.LogicalImageRelativePath,
            "PPP007");
        var logicalImageInfo = new FileInfo(logicalImagePath);
        var logicalImageHash = await HashFileAsync(
            logicalImagePath, cancellationToken).ConfigureAwait(false);
        if (logicalImageInfo.Length != logicalManifest.LogicalImageBytes ||
            !logicalImageHash.Equals(
                logicalManifest.LogicalImageSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("PPP008", "The logical staging image changed after construction.");
        }

        var rewritePlan = await PackageRewritePlanService.LoadManifestAsync(
            logicalManifest.PackageRewritePlanManifestPath, cancellationToken).ConfigureAwait(false);
        if (!rewritePlan.PlanId.Equals(logicalManifest.PlanId, StringComparison.OrdinalIgnoreCase) ||
            rewritePlan.NativeExportIndex != logicalManifest.NativeExportIndex ||
            rewritePlan.SourceLogicalPackageBytes != logicalManifest.SourceLogicalPackageBytes ||
            rewritePlan.PlannedLogicalPackageBytes != logicalManifest.LogicalImageBytes)
        {
            Refuse("PPP009", "The logical image no longer agrees with its rewrite plan.");
        }

        var packagePath = RequireFile(logicalManifest.SourcePackagePath, "PPP010", "source package");
        var packageHash = await HashFileAsync(packagePath, cancellationToken).ConfigureAwait(false);
        if (!packageHash.Equals(logicalManifest.SourcePackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("PPP011", "The source package SHA-256 changed after logical-image construction.");
        }

        var summary = _summaryReader.Read(packagePath);
        if (summary.PhysicalFileSize != new FileInfo(packagePath).Length ||
            summary.CompressedChunks.Count != rewritePlan.SourceCompressedChunkCount)
        {
            Refuse("PPP012", "The current physical package layout no longer matches the rewrite plan.");
        }

        var logicalEvidence = await HashLogicalLayoutAsync(
            summary, cancellationToken).ConfigureAwait(false);
        if (logicalEvidence.LogicalBytes != logicalManifest.SourceLogicalPackageBytes ||
            !logicalEvidence.FullSha256.Equals(
                logicalManifest.SourceLogicalPackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("PPP013", "The decoded source logical image changed after staging.");
        }

        var chunks = new List<PhysicalChunkPlan>(summary.CompressedChunks.Count);
        await using var physical = new FileStream(
            packagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
        for (var index = 0; index < summary.CompressedChunks.Count; index++)
        {
            var chunk = summary.CompressedChunks[index];
            var logicalHash = logicalEvidence.ChunkSha256[index];
            var recordOffset = summary.CompressedChunkTableOffset + index * 20L;
            var containsPatch = logicalManifest.TablePatches.Any(patch =>
                patch.FieldLogicalOffset >= chunk.UncompressedOffset &&
                patch.FieldLogicalOffset < (long)chunk.UncompressedEnd);
            var receivesAppend = index == summary.CompressedChunks.Count - 1 &&
                                 logicalManifest.LogicalImageBytes > logicalManifest.SourceLogicalPackageBytes;
            var plannedLogicalBytes = chunk.UncompressedSize +
                                      (receivesAppend ? logicalManifest.LogicalImageBytes -
                                                        logicalManifest.SourceLogicalPackageBytes : 0);
            var parsed = await ParsePhysicalChunkAsync(
                physical, summary, chunk, index, cancellationToken).ConfigureAwait(false);
            chunks.Add(new PhysicalChunkPlan(
                index,
                recordOffset,
                recordOffset,
                recordOffset + 4,
                recordOffset + 8,
                recordOffset + 12,
                recordOffset + 16,
                chunk.UncompressedOffset,
                chunk.UncompressedSize,
                chunk.CompressedOffset,
                chunk.CompressedSize,
                chunk.LostArkFlags,
                parsed.StorageKind,
                parsed.BlockSize,
                parsed.Blocks.Count,
                parsed.PhysicalSha256,
                logicalHash,
                parsed.EnvelopeParsed,
                parsed.ProtectionReadable,
                containsPatch,
                receivesAppend,
                plannedLogicalBytes,
                DescribeReplacementAction(containsPatch, receivesAppend, parsed.StorageKind),
                parsed.Blocks));
        }

        var regions = await BuildCopyRegionsAsync(
            physical, summary, cancellationToken).ConfigureAwait(false);
        var coveredBytes = regions.Sum(region => region.Bytes);
        var orderedCoverage = regions.Count == 0
            ? summary.PhysicalFileSize == 0
            : regions[0].PhysicalOffset == 0 &&
              regions.Zip(regions.Skip(1), (left, right) =>
                  left.PhysicalOffset + left.Bytes == right.PhysicalOffset).All(value => value) &&
              regions[^1].PhysicalOffset + regions[^1].Bytes == summary.PhysicalFileSize;
        if (!orderedCoverage || coveredBytes != summary.PhysicalFileSize)
        {
            Refuse("PPP014", "The no-op copy-region inventory does not cover the physical package exactly.");
        }

        var firstPhysicalOffset = summary.CompressedChunks.Count == 0
            ? summary.PhysicalFileSize
            : summary.CompressedChunks[0].CompressedOffset;
        var prefixHash = await HashFileRangeAsync(
            packagePath, 0, firstPhysicalOffset, cancellationToken).ConfigureAwait(false);
        var protectedChunks = chunks.Count(chunk => chunk.LostArkFlags == 1);
        var rawChunks = chunks.Count(chunk =>
            chunk.LostArkFlags == 0 && chunk.PhysicalBytes == chunk.LogicalBytes);
        var allEnvelopes = chunks.All(chunk => chunk.EnvelopeParsed);
        var allProtectedReadable = chunks.All(chunk => chunk.ProtectionReadable);
        var affected = chunks.Count(chunk =>
            chunk.ContainsReplacementTablePatch || chunk.ReceivesLogicalAppend);
        var replacementLayoutUnderstood = affected > 0 &&
                                          chunks.Any(chunk => chunk.ContainsReplacementTablePatch) &&
                                          chunks.Any(chunk => chunk.ReceivesLogicalAppend);

        var checks = new List<TextureLayoutCheck>
        {
            new("PPP_SOURCE_IDENTITY", TextureLayoutCheckStatus.Passed,
                $"Source UPK remains {packageHash} across {summary.PhysicalFileSize:N0} physical bytes."),
            new("PPP_LOGICAL_IDENTITY", TextureLayoutCheckStatus.Passed,
                $"Decoded {logicalEvidence.LogicalBytes:N0} logical bytes to {logicalEvidence.FullSha256}."),
            new("PPP_SUMMARY_OFFSETS", TextureLayoutCheckStatus.Passed,
                $"Located {summary.CompressedChunks.Count:N0} chunk records at physical 0x{summary.CompressedChunkTableOffset:X}; package source is at 0x{summary.PackageSourceOffset:X}."),
            new("PPP_CHUNK_ENVELOPES", TextureLayoutCheckStatus.Passed,
                $"Parsed {chunks.Count:N0} chunk envelope(s) and {chunks.Sum(chunk => chunk.BlockCount):N0} compressed block descriptor(s)."),
            new("PPP_PROTECTION", TextureLayoutCheckStatus.Passed,
                $"Verified readable protection metadata for all {protectedChunks:N0} protected chunk(s) with {_protectionProvider.Name}."),
            new("PPP_COPY_COVERAGE", TextureLayoutCheckStatus.Passed,
                $"The {regions.Count:N0} ordered no-op copy region(s) cover every physical source byte exactly once."),
            new("PPP_REPLACEMENT_IMPACT", TextureLayoutCheckStatus.Passed,
                $"A later replacement writer must change {affected:N0} existing chunk(s): table-field chunks are rebuilt and the final logical chunk receives the append."),
            new("PPP_NOOP_SCOPE", TextureLayoutCheckStatus.Information,
                "The approved no-op writer will reassemble audited source regions byte-for-byte; changed-chunk recompression is deliberately a later gate."),
            new("PPP_NO_PACKAGE_WRITE", TextureLayoutCheckStatus.Information,
                "Planning opened the source UPK and logical image read-only and emitted JSON evidence only."),
        };
        watch.Stop();
        var manifest = new PhysicalPackagePlanManifest(
            CurrentManifestSchemaVersion,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            logicalManifestPath,
            logicalManifest.ImageId,
            logicalManifest.PackageRewritePlanManifestPath,
            logicalManifest.PlanId,
            packagePath,
            packageHash,
            summary.PhysicalFileSize,
            logicalEvidence.LogicalBytes,
            logicalEvidence.FullSha256,
            logicalManifest.PackageAlias,
            logicalManifest.NativeExportIndex,
            logicalManifest.ClassName,
            logicalManifest.ObjectName,
            logicalManifest.PixelFormat,
            summary.CompressionFlags,
            summary.CompressionFlagsOffset,
            summary.CompressedChunkCountOffset,
            summary.CompressedChunkTableOffset,
            summary.PackageSourceOffset,
            chunks.Count,
            protectedChunks,
            rawChunks,
            chunks.Sum(chunk => chunk.BlockCount),
            firstPhysicalOffset,
            prefixHash,
            logicalEvidence.LogicalPrefixBytes,
            logicalEvidence.LogicalPrefixSha256,
            logicalManifest.LogicalImageBytes,
            logicalManifest.LogicalImageBytes - logicalManifest.SourceLogicalPackageBytes,
            affected,
            allEnvelopes,
            allProtectedReadable,
            LogicalSourceIdentityConfirmed: true,
            CopyRegionsCoverPhysicalFile: true,
            PlanReadyForNoOpReconstruction: true,
            ReplacementChunkLayoutUnderstood: replacementLayoutUnderstood,
            ReplacementPhysicalWriterReady: false,
            PackageWasModified: false,
            checks,
            chunks,
            regions,
            watch.Elapsed.TotalSeconds);
        var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return new PhysicalPackagePlanResult(
            outputDirectory, manifestPath, manifest, [manifestPath], watch.Elapsed);
    }

    private async Task<ParsedChunk> ParsePhysicalChunkAsync(
        FileStream physical,
        PackageSummary summary,
        CompressedChunk chunk,
        int chunkIndex,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[checked((int)chunk.CompressedSize)];
        physical.Position = chunk.CompressedOffset;
        await physical.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var physicalHash = Hash(bytes);
        if (chunk.LostArkFlags == 0 && chunk.CompressedSize == chunk.UncompressedSize)
        {
            return new ParsedChunk(
                "Raw logical bytes", null, physicalHash, true, true, []);
        }

        var cursor = 0;
        var tag = ReadUInt32(bytes, ref cursor, $"chunk {chunkIndex} tag");
        var blockSize = ReadInt32(bytes, ref cursor, $"chunk {chunkIndex} block size");
        var summaryCompressed = ReadInt32(bytes, ref cursor, $"chunk {chunkIndex} compressed size");
        var summaryUncompressed = ReadInt32(bytes, ref cursor, $"chunk {chunkIndex} uncompressed size");
        if (tag != PackageConstants.UnrealPackageTag || blockSize <= 0 ||
            blockSize > MaximumBlockSize || summaryCompressed <= 0 ||
            summaryUncompressed != chunk.UncompressedSize)
        {
            Refuse("PPP015", $"Chunk {chunkIndex} has an invalid UE3 compression envelope.");
        }

        var descriptors = new List<(int Stored, int Raw, long Offset)>();
        long totalStored = 0;
        long totalRaw = 0;
        while (totalStored < summaryCompressed && totalRaw < summaryUncompressed)
        {
            if (descriptors.Count >= MaximumBlockCount)
            {
                Refuse("PPP016", $"Chunk {chunkIndex} contains too many blocks.");
            }
            var descriptorOffset = chunk.CompressedOffset + cursor;
            var stored = ReadInt32(bytes, ref cursor, $"chunk {chunkIndex} block stored size");
            var raw = ReadInt32(bytes, ref cursor, $"chunk {chunkIndex} block raw size");
            if (stored <= 0 || raw <= 0 || stored > MaximumBlockSize || raw > MaximumBlockSize)
            {
                Refuse("PPP017", $"Chunk {chunkIndex} contains an invalid block descriptor.");
            }
            totalStored += stored;
            totalRaw += raw;
            if (totalStored > summaryCompressed || totalRaw > summaryUncompressed)
            {
                Refuse("PPP018", $"Chunk {chunkIndex} block totals exceed its summary.");
            }
            descriptors.Add((stored, raw, descriptorOffset));
        }
        if (totalStored != summaryCompressed || totalRaw != summaryUncompressed ||
            cursor + totalStored != bytes.Length)
        {
            Refuse("PPP019", $"Chunk {chunkIndex} block totals do not cover its physical envelope.");
        }

        var blocks = new List<PhysicalChunkBlockPlan>(descriptors.Count);
        var protectionReadable = true;
        for (var blockIndex = 0; blockIndex < descriptors.Count; blockIndex++)
        {
            var descriptor = descriptors[blockIndex];
            var payloadOffset = cursor;
            var stored = bytes.AsSpan(payloadOffset, descriptor.Stored).ToArray();
            cursor += descriptor.Stored;
            var storedHash = Hash(stored);
            var replayed = chunk.LostArkFlags != 1;
            var protectedPrefix = 0;
            if (chunk.LostArkFlags == 1)
            {
                if (!_protectionProvider.CanUnprotect(summary, chunk))
                {
                    protectionReadable = false;
                    Refuse("PPP020", $"Chunk {chunkIndex} is protected but no readable provider is configured.");
                }
                protectedPrefix = Math.Min(
                    AesEcbPrefixProtectionProvider.ProtectedPrefixBytes, stored.Length);
                protectedPrefix -= protectedPrefix % 16;
                _protectionProvider.UnprotectBlock(summary, chunk, blockIndex, stored);
                replayed = true;
            }
            blocks.Add(new PhysicalChunkBlockPlan(
                blockIndex,
                descriptor.Offset,
                chunk.CompressedOffset + payloadOffset,
                descriptor.Stored,
                descriptor.Raw,
                protectedPrefix,
                storedHash,
                Hash(stored),
                replayed));
        }
        return new ParsedChunk(
            "UE3 block envelope / Lost Ark LZ4",
            blockSize,
            physicalHash,
            true,
            protectionReadable,
            blocks);
    }

    private async Task<LogicalEvidence> HashLogicalLayoutAsync(
        PackageSummary summary,
        CancellationToken cancellationToken)
    {
        using var logical = new LogicalPackageStream(summary, _protectionProvider);
        using var fullHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunkHashes = new List<string>(summary.CompressedChunks.Count);
        var buffer = new byte[BufferBytes];
        var prefixBytes = summary.CompressedChunks.Count == 0
            ? logical.Length
            : summary.CompressedChunks[0].UncompressedOffset;
        var prefixHash = await HashLogicalSegmentAsync(
            logical, prefixBytes, fullHash, buffer, cancellationToken).ConfigureAwait(false);
        long expectedOffset = prefixBytes;
        foreach (var chunk in summary.CompressedChunks)
        {
            if (chunk.UncompressedOffset != expectedOffset)
            {
                Refuse("PPP021", "Physical planning currently requires contiguous logical chunks.");
            }
            chunkHashes.Add(await HashLogicalSegmentAsync(
                logical, chunk.UncompressedSize, fullHash, buffer, cancellationToken).ConfigureAwait(false));
            expectedOffset += chunk.UncompressedSize;
        }
        if (expectedOffset != logical.Length)
        {
            Refuse("PPP022", "The chunk inventory does not reach the logical package end.");
        }
        return new LogicalEvidence(
            logical.Length,
            Convert.ToHexString(fullHash.GetHashAndReset()),
            prefixBytes,
            prefixHash,
            chunkHashes);
    }

    private static async Task<string> HashLogicalSegmentAsync(
        Stream logical,
        long bytes,
        IncrementalHash fullHash,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var segmentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long remaining = bytes;
        while (remaining > 0)
        {
            var count = checked((int)Math.Min(buffer.Length, remaining));
            var read = await logical.ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The logical package ended during hashing.");
            segmentHash.AppendData(buffer, 0, read);
            fullHash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(segmentHash.GetHashAndReset());
    }

    private static async Task<IReadOnlyList<PhysicalCopyRegionPlan>> BuildCopyRegionsAsync(
        FileStream physical,
        PackageSummary summary,
        CancellationToken cancellationToken)
    {
        var ranges = new List<(string Kind, long Offset, long Bytes, int? Chunk, string Meaning)>();
        long cursor = 0;
        foreach (var pair in summary.CompressedChunks.Select((chunk, index) => (chunk, index)))
        {
            if (pair.chunk.CompressedOffset < cursor)
            {
                Refuse("PPP023", "Physical chunks overlap or are out of order.");
            }
            if (pair.chunk.CompressedOffset > cursor)
            {
                ranges.Add((
                    cursor == 0 ? "Physical prefix" : "Physical gap",
                    cursor,
                    pair.chunk.CompressedOffset - cursor,
                    null,
                    cursor == 0
                        ? "Package summary, chunk table, header tail, and pre-chunk bytes."
                        : "Bytes between physical chunk envelopes."));
            }
            ranges.Add((
                "Physical chunk",
                pair.chunk.CompressedOffset,
                pair.chunk.CompressedSize,
                pair.index,
                "Exact stored chunk envelope or raw logical range."));
            cursor = checked((long)pair.chunk.CompressedEnd);
        }
        if (cursor < summary.PhysicalFileSize)
        {
            ranges.Add((
                cursor == 0 ? "Physical file" : "Physical tail",
                cursor,
                summary.PhysicalFileSize - cursor,
                null,
                cursor == 0
                    ? "Uncompressed package bytes."
                    : "Bytes following the final physical chunk."));
        }

        var output = new List<PhysicalCopyRegionPlan>(ranges.Count);
        for (var index = 0; index < ranges.Count; index++)
        {
            var range = ranges[index];
            output.Add(new PhysicalCopyRegionPlan(
                index,
                range.Kind,
                range.Offset,
                range.Bytes,
                await HashStreamRangeAsync(
                    physical, range.Offset, range.Bytes, cancellationToken).ConfigureAwait(false),
                range.Chunk,
                range.Meaning));
        }
        return output;
    }

    private static string DescribeReplacementAction(
        bool containsPatch,
        bool receivesAppend,
        string storageKind)
    {
        if (containsPatch && receivesAppend)
        {
            return $"Rebuild {storageKind}; apply table fields and extend with appended logical bytes.";
        }
        if (containsPatch) return $"Rebuild {storageKind} from the verified logical image for table-field changes.";
        if (receivesAppend) return $"Extend {storageKind} with the verified appended logical range.";
        return "Copy the existing physical bytes unchanged.";
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int cursor, string label)
    {
        EnsureAvailable(data, cursor, sizeof(uint), label);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
        cursor += sizeof(uint);
        return value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int cursor, string label)
    {
        EnsureAvailable(data, cursor, sizeof(int), label);
        var value = BinaryPrimitives.ReadInt32LittleEndian(data[cursor..]);
        cursor += sizeof(int);
        return value;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> data, int cursor, int count, string label)
    {
        if (cursor < 0 || count < 0 || data.Length - cursor < count)
        {
            Refuse("PPP024", $"The {label} is truncated.");
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<string> HashFileRangeAsync(
        string path,
        long offset,
        long bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
        return await HashStreamRangeAsync(stream, offset, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> HashStreamRangeAsync(
        Stream stream,
        long offset,
        long bytes,
        CancellationToken cancellationToken)
    {
        stream.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferBytes];
        long remaining = bytes;
        while (remaining > 0)
        {
            var count = checked((int)Math.Min(buffer.Length, remaining));
            var read = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("A physical range ended during hashing.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ResolveWithin(string directory, string relativePath, string code)
    {
        var path = Path.GetFullPath(Path.Combine(directory, relativePath));
        var prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            Refuse(code, $"Manifest file is missing or escapes its session: {relativePath}");
        }
        return path;
    }

    private static string RequireFile(string path, string code, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) Refuse(code, $"The {label} path is empty.");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) Refuse(code, $"The {label} was not found: {fullPath}");
        return fullPath;
    }

    private static async Task SaveJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new PhysicalPackagePlanRefusalException(code, message);

    private sealed record ParsedChunk(
        string StorageKind,
        int? BlockSize,
        string PhysicalSha256,
        bool EnvelopeParsed,
        bool ProtectionReadable,
        IReadOnlyList<PhysicalChunkBlockPlan> Blocks);

    private sealed record LogicalEvidence(
        long LogicalBytes,
        string FullSha256,
        long LogicalPrefixBytes,
        string LogicalPrefixSha256,
        IReadOnlyList<string> ChunkSha256);
}
