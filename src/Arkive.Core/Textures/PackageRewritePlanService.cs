using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Packages;

namespace Arkive.Core.Textures;

public sealed record PackageRewritePlanRequest(
    string TextureExportAssemblyManifestPath,
    string OutputDirectory);

public sealed record PackageExportRelocationPlan(
    int ExportIndex,
    string ObjectName,
    string ClassName,
    int SerialSize,
    int OriginalSerialOffset,
    long InlineSpliceSerialOffset,
    long SelectedSerialOffset,
    bool WouldRelocateInline,
    bool IsTarget,
    bool OverlapsTargetRange,
    long SerialSizeFieldLogicalOffset,
    long SerialOffsetFieldLogicalOffset);

public sealed record PackageTableFieldPatchPlan(
    int ExportIndex,
    string Field,
    long FieldLogicalOffset,
    int OriginalValue,
    int PlannedValue,
    string LogicalStorage,
    string Reason);

public sealed record TextureBulkOffsetRebasePlan(
    int MipIndex,
    long FieldRelativeOffset,
    int AssemblyValue,
    int PlannedValue,
    long PayloadRelativeOffset,
    long PlannedPayloadLogicalOffset);

public sealed record PackageChunkRewriteImpact(
    int? ChunkIndex,
    long LogicalOffset,
    long LogicalBytes,
    string Impact,
    string RequiredAction);

public sealed record PackageRewritePlanManifest(
    int SchemaVersion,
    string PlanId,
    DateTimeOffset CreatedAt,
    string TextureExportAssemblyManifestPath,
    string AssemblyId,
    string TextureReplacementPayloadManifestPath,
    string SourcePackagePath,
    string SourcePackageSha256,
    string PackageAlias,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    string PixelFormat,
    long SourceLogicalPackageBytes,
    long SourcePhysicalPackageBytes,
    int SourceCompressedChunkCount,
    long SourceSerialOffset,
    long SourceSerialSize,
    long ReplacementSerialSize,
    long InlineSpliceSizeDelta,
    string InlineSpliceDisposition,
    int InlineSpliceRelocatedExportCount,
    int InlineSpliceOverlappingExportCount,
    string SelectedStrategy,
    string SelectedStrategyReason,
    int AppendAlignment,
    int AppendPaddingBytes,
    long PlannedSerialOffset,
    long PlannedSerialSize,
    long PlannedLogicalPackageBytes,
    long PlannedLogicalGrowthBytes,
    bool OriginalExportBytesRetained,
    int ExistingExportsRelocated,
    int ExistingExportPayloadsModified,
    bool ExportTablePatchLocationsConfirmed,
    bool AllBulkOffsetsRebased,
    bool AllExistingTablePointersUnchanged,
    bool PlanReadyForLogicalImageWriter,
    bool PhysicalChunkPlanReady,
    bool PackageFileWriterReady,
    bool PackageWasModified,
    IReadOnlyList<TextureLayoutCheck> Checks,
    IReadOnlyList<PackageTableFieldPatchPlan> SelectedTablePatches,
    IReadOnlyList<TextureBulkOffsetRebasePlan> BulkOffsetRebases,
    IReadOnlyList<PackageExportRelocationPlan> ExportRelocations,
    IReadOnlyList<PackageChunkRewriteImpact> ChunkImpacts,
    double DurationSeconds);

public sealed record PackageRewritePlanResult(
    string OutputDirectory,
    string ManifestPath,
    PackageRewritePlanManifest Manifest,
    IReadOnlyList<string> OutputFiles,
    TimeSpan Duration);

public sealed class PackageRewritePlanRefusalException : NotSupportedException
{
    public PackageRewritePlanRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Produces a deterministic, read-only package rewrite plan. The selected strategy
/// appends the replacement export and retargets one export table entry so unrelated
/// serialized exports and their unknown internal absolute offsets remain untouched.
/// </summary>
public sealed class PackageRewritePlanService
{
    public const int CurrentManifestSchemaVersion = 1;
    public const string ManifestFileName = "arkive-package-rewrite-plan.json";
    public const int AppendAlignment = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly UpkPackageReader _summaryReader;
    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;

    public PackageRewritePlanService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _summaryReader = summaryReader ?? new UpkPackageReader();
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(_summaryReader, _protectionProvider);
    }

    public static async Task<PackageRewritePlanManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = RequireFile(path, "PRP001", "package rewrite-plan manifest");
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<PackageRewritePlanManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new PackageRewritePlanRefusalException(
                           "PRP002", "The package rewrite-plan manifest was empty.");
        if (manifest.SchemaVersion != CurrentManifestSchemaVersion)
        {
            Refuse("PRP003", $"Package rewrite-plan schema {manifest.SchemaVersion} is unsupported.");
        }
        return manifest;
    }

    public async Task<PackageRewritePlanResult> PlanAsync(
        PackageRewritePlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var assemblyPath = RequireFile(
            request.TextureExportAssemblyManifestPath, "PRP004", "texture export-assembly manifest");
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            Refuse("PRP005", "The package rewrite-plan output directory must be empty.");
        }
        var assembly = await TextureExportAssemblyService.LoadManifestAsync(
            assemblyPath, cancellationToken).ConfigureAwait(false);
        if (!assembly.ReplacementBlobReadyForRewritePlan || !assembly.NoOpExportByteIdentical ||
            !assembly.AllNonMipBytesPreserved || !assembly.AllDescriptorsReplayed ||
            !assembly.AllStoragePayloadsDecode || !assembly.AllQualityGatesRetained ||
            assembly.PackageWasModified)
        {
            Refuse("PRP006", "A fully verified, staging-only Texture2D export assembly is required.");
        }

        var assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
        var replacementPath = ResolveWithin(
            assemblyDirectory, assembly.ReplacementExportRelativePath, "PRP007");
        var replacement = await File.ReadAllBytesAsync(replacementPath, cancellationToken).ConfigureAwait(false);
        if (replacement.LongLength != assembly.ReplacementExportBytes ||
            !Hash(replacement).Equals(assembly.ReplacementExportSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("PRP008", "The replacement export blob changed after assembly.");
        }

        var packagePath = RequireFile(assembly.SourcePackagePath, "PRP009", "source package");
        var packageHash = await HashFileAsync(packagePath, cancellationToken).ConfigureAwait(false);
        if (!packageHash.Equals(assembly.SourcePackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("PRP010", "The source package SHA-256 changed after export assembly.");
        }
        var summary = _summaryReader.Read(packagePath);
        var catalog = _catalogReader.Read(packagePath);
        var target = catalog.Exports.SingleOrDefault(item => item.TableIndex == assembly.NativeExportIndex);
        var targetAsset = catalog.Assets.SingleOrDefault(item => item.ExportIndex == assembly.NativeExportIndex);
        if (target is null || targetAsset is null || target.SerialOffset != assembly.SerialOffset ||
            target.SerialSize != assembly.SourceSerialSize ||
            !targetAsset.ObjectName.Equals(assembly.ObjectName, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("PRP011", "The target export table entry no longer matches the assembled Texture2D.");
        }

        using var logical = new LogicalPackageStream(summary, _protectionProvider);
        var layouts = ReadExportTableLayouts(logical, summary, catalog.Exports);
        var targetLayout = layouts[assembly.NativeExportIndex - 1];
        if (targetLayout.SerialSize != assembly.SourceSerialSize ||
            targetLayout.SerialOffset != assembly.SerialOffset)
        {
            Refuse("PRP012", "The raw export-table size/offset fields do not match the parsed catalogue.");
        }

        var inlineDelta = assembly.ReplacementExportBytes - assembly.SourceSerialSize;
        var targetStart = assembly.SerialOffset;
        var targetEnd = checked(assembly.SerialOffset + assembly.SourceSerialSize);
        var exportPlans = new List<PackageExportRelocationPlan>();
        var inlineRelocated = 0;
        var overlaps = 0;
        foreach (var item in catalog.Exports)
        {
            var raw = layouts[item.TableIndex - 1];
            var isTarget = item.TableIndex == assembly.NativeExportIndex;
            var hasPayload = item.SerialSize > 0;
            var end = checked((long)item.SerialOffset + item.SerialSize);
            var overlapsTarget = !isTarget && hasPayload && item.SerialOffset < targetEnd && end > targetStart;
            if (overlapsTarget) overlaps++;
            var wouldRelocate = !isTarget && hasPayload && item.SerialOffset >= targetEnd && inlineDelta != 0;
            if (wouldRelocate) inlineRelocated++;
            var inlineOffset = isTarget
                ? item.SerialOffset
                : wouldRelocate
                    ? checked(item.SerialOffset + inlineDelta)
                    : item.SerialOffset;
            var asset = catalog.Assets[item.TableIndex - 1];
            exportPlans.Add(new PackageExportRelocationPlan(
                item.TableIndex,
                asset.ObjectName,
                asset.ClassName,
                item.SerialSize,
                item.SerialOffset,
                inlineOffset,
                item.SerialOffset,
                wouldRelocate,
                isTarget,
                overlapsTarget,
                raw.SerialSizeFieldOffset,
                raw.SerialOffsetFieldOffset));
        }
        if (overlaps > 0)
        {
            Refuse("PRP013", $"{overlaps:N0} non-target export(s) overlap the target serialized range.");
        }

        var plannedOffset = AlignUp(logical.Length, AppendAlignment);
        var padding = checked((int)(plannedOffset - logical.Length));
        var plannedEnd = checked(plannedOffset + replacement.LongLength);
        if (plannedOffset > int.MaxValue || replacement.LongLength > int.MaxValue || plannedEnd > int.MaxValue)
        {
            Refuse("PRP014", "The append strategy exceeds UE3 Int32 serial-offset storage.");
        }

        var targetPlanIndex = exportPlans.FindIndex(item => item.IsTarget);
        exportPlans[targetPlanIndex] = exportPlans[targetPlanIndex] with
        {
            SelectedSerialOffset = plannedOffset,
        };
        var tablePatches = new[]
        {
            new PackageTableFieldPatchPlan(
                assembly.NativeExportIndex,
                "SerialSize",
                targetLayout.SerialSizeFieldOffset,
                targetLayout.SerialSize,
                checked((int)replacement.LongLength),
                DescribeStorage(summary, targetLayout.SerialSizeFieldOffset),
                "Retarget the selected export to the complete replacement blob."),
            new PackageTableFieldPatchPlan(
                assembly.NativeExportIndex,
                "SerialOffset",
                targetLayout.SerialOffsetFieldOffset,
                targetLayout.SerialOffset,
                checked((int)plannedOffset),
                DescribeStorage(summary, targetLayout.SerialOffsetFieldOffset),
                "Point the selected export at the aligned append location."),
        };

        var bulkRebases = new List<TextureBulkOffsetRebasePlan>(assembly.Mips.Count);
        foreach (var mip in assembly.Mips)
        {
            if (mip.BulkOffsetFieldRelativeOffset < 0 ||
                mip.BulkOffsetFieldRelativeOffset > replacement.Length - 4)
            {
                Refuse("PRP015", $"Mip {mip.MipIndex} bulk-offset field is outside the assembled blob.");
            }
            var current = BinaryPrimitives.ReadInt32LittleEndian(
                replacement.AsSpan(checked((int)mip.BulkOffsetFieldRelativeOffset), 4));
            if (current != mip.PayloadLogicalOffset)
            {
                Refuse("PRP016", $"Mip {mip.MipIndex} assembly bulk offset does not match its manifest.");
            }
            var rebased = checked(plannedOffset + mip.PayloadRelativeOffset);
            if (rebased > int.MaxValue) Refuse("PRP017", $"Mip {mip.MipIndex} rebased offset exceeds Int32.");
            bulkRebases.Add(new TextureBulkOffsetRebasePlan(
                mip.MipIndex,
                mip.BulkOffsetFieldRelativeOffset,
                current,
                checked((int)rebased),
                mip.PayloadRelativeOffset,
                rebased));
        }

        var impacts = new List<PackageChunkRewriteImpact>();
        foreach (var group in tablePatches.GroupBy(patch => FindChunkIndex(summary, patch.FieldLogicalOffset)))
        {
            if (group.Key is int chunkIndex)
            {
                var chunk = summary.CompressedChunks[chunkIndex];
                impacts.Add(new PackageChunkRewriteImpact(
                    chunkIndex,
                    chunk.UncompressedOffset,
                    chunk.UncompressedSize,
                    "Target export-table fields lie in this existing logical chunk.",
                    "Patch the decoded chunk, recompress it, and reproduce its required protection."));
            }
            else
            {
                impacts.Add(new PackageChunkRewriteImpact(
                    null,
                    group.Min(item => item.FieldLogicalOffset),
                    group.Count() * 4L,
                    "Target export-table fields lie in the raw package prefix.",
                    "Patch the fixed-size fields without shifting any existing table bytes."));
            }
        }
        impacts.Add(new PackageChunkRewriteImpact(
            null,
            plannedOffset,
            replacement.LongLength,
            "The rebased replacement Texture2D export will be appended here.",
            "Create or extend logical package chunks; physical chunk construction remains a later milestone."));

        var tablePointersUnchanged = new[]
        {
            summary.NameOffset,
            summary.ExportOffset,
            summary.ImportOffset,
            summary.DependsOffset,
            summary.ImportExportGuidsOffset,
            summary.ThumbnailTableOffset,
        }.All(offset => offset >= 0 && offset < plannedOffset);
        if (!tablePointersUnchanged)
        {
            Refuse("PRP018", "A package table pointer is outside the retained logical image.");
        }

        var checks = new List<TextureLayoutCheck>
        {
            new("PRP_SOURCE_IDENTITY", TextureLayoutCheckStatus.Passed,
                $"Source package and target export {assembly.NativeExportIndex} still match their recorded identities."),
            new("PRP_EXPORT_TABLE_FIELDS", TextureLayoutCheckStatus.Passed,
                $"Located exact SerialSize/SerialOffset fields at logical 0x{targetLayout.SerialSizeFieldOffset:X} and 0x{targetLayout.SerialOffsetFieldOffset:X}."),
            new("PRP_INLINE_STRATEGY", TextureLayoutCheckStatus.Warning,
                $"Inline replacement would change size by {inlineDelta:+#;-#;0} byte(s) and relocate {inlineRelocated:N0} later export(s); it is rejected because unknown internal absolute offsets in those exports are not proven."),
            new("PRP_APPEND_STRATEGY", TextureLayoutCheckStatus.Passed,
                $"Selected append-and-retarget at logical 0x{plannedOffset:X}; no existing export payload moves or changes."),
            new("PRP_BULK_REBASE", TextureLayoutCheckStatus.Passed,
                $"Calculated all {bulkRebases.Count:N0} replacement mip bulk-offset patches for the append location."),
            new("PRP_PHYSICAL_SCOPE", TextureLayoutCheckStatus.Information,
                "Logical rewrite planning is complete; compressed-chunk creation, protection, and physical UPK emission remain deliberately unconfirmed."),
            new("PRP_NO_PACKAGE_WRITE", TextureLayoutCheckStatus.Information,
                "The package was opened read-only and no logical or physical package image was written."),
        };
        watch.Stop();
        var manifest = new PackageRewritePlanManifest(
            CurrentManifestSchemaVersion,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            assemblyPath,
            assembly.AssemblyId,
            assembly.TextureReplacementPayloadManifestPath,
            packagePath,
            packageHash,
            assembly.PackageAlias,
            assembly.NativeExportIndex,
            assembly.ClassName,
            assembly.ObjectName,
            assembly.PixelFormat,
            logical.Length,
            summary.PhysicalFileSize,
            summary.CompressedChunks.Count,
            assembly.SerialOffset,
            assembly.SourceSerialSize,
            replacement.LongLength,
            inlineDelta,
            "Rejected: would relocate existing exports with unaudited internal absolute offsets",
            inlineRelocated,
            overlaps,
            "Append replacement export and retarget one export-table entry",
            "Preserves every existing export payload and offset; only the selected export-table fields and new blob require mutation.",
            AppendAlignment,
            padding,
            plannedOffset,
            replacement.LongLength,
            plannedEnd,
            plannedEnd - logical.Length,
            OriginalExportBytesRetained: true,
            ExistingExportsRelocated: 0,
            ExistingExportPayloadsModified: 0,
            ExportTablePatchLocationsConfirmed: true,
            AllBulkOffsetsRebased: bulkRebases.Count == assembly.MipCount,
            AllExistingTablePointersUnchanged: tablePointersUnchanged,
            PlanReadyForLogicalImageWriter: true,
            PhysicalChunkPlanReady: false,
            PackageFileWriterReady: false,
            PackageWasModified: false,
            checks,
            tablePatches,
            bulkRebases,
            exportPlans,
            impacts,
            watch.Elapsed.TotalSeconds);
        var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return new PackageRewritePlanResult(
            outputDirectory, manifestPath, manifest, [manifestPath], watch.Elapsed);
    }

    private static IReadOnlyList<RawExportLayout> ReadExportTableLayouts(
        LogicalPackageStream logical,
        PackageSummary summary,
        IReadOnlyList<UnrealObjectExport> parsed)
    {
        logical.Position = summary.ExportOffset;
        using var reader = new BinaryReader(logical, Encoding.UTF8, leaveOpen: true);
        var result = new List<RawExportLayout>(summary.ExportCount);
        for (var index = 0; index < summary.ExportCount; index++)
        {
            var start = logical.Position;
            _ = reader.ReadInt32(); // class
            _ = reader.ReadInt32(); // super
            _ = reader.ReadInt32(); // outer
            _ = reader.ReadInt32(); // object name index
            _ = reader.ReadInt32(); // object name number
            _ = reader.ReadInt32(); // archetype
            _ = reader.ReadUInt32(); // flags low
            _ = reader.ReadUInt32(); // flags high
            var sizeField = logical.Position;
            var serialSize = reader.ReadInt32();
            var offsetField = logical.Position;
            var serialOffset = reader.ReadInt32();
            _ = reader.ReadUInt32(); // export flags
            var netCount = reader.ReadInt32();
            if (netCount < 0 || netCount > PackageConstants.MaxReasonableTableEntries)
            {
                Refuse("PRP019", $"Export {index + 1} has invalid net-object count {netCount:N0}.");
            }
            var remaining = checked(netCount * 4L + 16L + 4L);
            if (logical.Position > logical.Length - remaining)
            {
                Refuse("PRP020", $"Export {index + 1} table record is truncated.");
            }
            logical.Position += remaining;
            var expected = parsed[index];
            if (serialSize != expected.SerialSize || serialOffset != expected.SerialOffset)
            {
                Refuse("PRP021", $"Raw export {index + 1} fields disagree with the parsed catalogue.");
            }
            result.Add(new RawExportLayout(
                index + 1, start, logical.Position, sizeField, offsetField, serialSize, serialOffset));
        }
        return result;
    }

    private static int? FindChunkIndex(PackageSummary summary, long logicalOffset)
    {
        for (var index = 0; index < summary.CompressedChunks.Count; index++)
        {
            var chunk = summary.CompressedChunks[index];
            if ((ulong)logicalOffset >= chunk.UncompressedOffset &&
                (ulong)logicalOffset < chunk.UncompressedEnd) return index;
        }
        return null;
    }

    private static string DescribeStorage(PackageSummary summary, long logicalOffset)
    {
        var index = FindChunkIndex(summary, logicalOffset);
        return index is null ? "Raw logical/physical prefix" : $"Compressed logical chunk {index.Value}";
    }

    private static long AlignUp(long value, int alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string ResolveWithin(string directory, string relativePath, string code)
    {
        var path = Path.GetFullPath(Path.Combine(directory, relativePath));
        var prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
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

    private static async Task SaveJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
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
        throw new PackageRewritePlanRefusalException(code, message);

    private sealed record RawExportLayout(
        int ExportIndex,
        long StartOffset,
        long EndOffset,
        long SerialSizeFieldOffset,
        long SerialOffsetFieldOffset,
        int SerialSize,
        int SerialOffset);
}
