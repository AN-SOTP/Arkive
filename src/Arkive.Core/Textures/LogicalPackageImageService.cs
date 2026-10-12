using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Packages;

namespace Arkive.Core.Textures;

public sealed record LogicalPackageImageRequest(
    string PackageRewritePlanManifestPath,
    string OutputDirectory);

public sealed record LogicalPackageImageTablePatchEvidence(
    int ExportIndex,
    string Field,
    long FieldLogicalOffset,
    int ExpectedOriginalValue,
    int PlannedValue,
    int VerifiedImageValue);

public sealed record LogicalPackageImageBulkRebaseEvidence(
    int MipIndex,
    long FieldRelativeOffset,
    long FieldLogicalOffset,
    int AssemblyValue,
    int PlannedValue,
    int VerifiedImageValue,
    long PayloadRelativeOffset,
    long VerifiedPayloadLogicalOffset);

public sealed record LogicalPackageImageRangeEvidence(
    string Label,
    long LogicalOffset,
    long Bytes,
    string? SourceSha256,
    string ImageSha256,
    bool Verified,
    string Meaning);

public sealed record LogicalPackageImageManifest(
    int SchemaVersion,
    string ImageId,
    DateTimeOffset CreatedAt,
    string PackageRewritePlanManifestPath,
    string PlanId,
    string TextureExportAssemblyManifestPath,
    string AssemblyId,
    string SourcePackagePath,
    string SourcePackageSha256,
    string SourceLogicalPackageSha256,
    string PackageAlias,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    string PixelFormat,
    long SourceLogicalPackageBytes,
    long LogicalImageBytes,
    string LogicalImageRelativePath,
    string LogicalImageSha256,
    long OriginalSerialOffset,
    long OriginalSerialSize,
    string OriginalExportSha256,
    bool OriginalExportBytesRetained,
    long AppendedSerialOffset,
    long AppendedSerialSize,
    string RebasedReplacementExportSha256,
    int AppendPaddingBytes,
    bool AppendPaddingZeroFilled,
    long AuditedPatchBytes,
    long UnplannedSourceBytesCompared,
    bool AllUnplannedSourceBytesIdentical,
    bool AllTablePatchesApplied,
    bool AllBulkOffsetsRebased,
    bool AppendedReplacementVerified,
    bool SourcePackageIdentityRetained,
    bool LogicalImageReadyForPhysicalWriter,
    bool PhysicalChunkWriterReady,
    bool PackageFileWriterReady,
    bool PackageWasModified,
    IReadOnlyList<TextureLayoutCheck> Checks,
    IReadOnlyList<LogicalPackageImageTablePatchEvidence> TablePatches,
    IReadOnlyList<LogicalPackageImageBulkRebaseEvidence> BulkOffsetRebases,
    IReadOnlyList<LogicalPackageImageRangeEvidence> Ranges,
    double DurationSeconds);

public sealed record LogicalPackageImageResult(
    string OutputDirectory,
    string ManifestPath,
    string LogicalImagePath,
    LogicalPackageImageManifest Manifest,
    IReadOnlyList<string> OutputFiles,
    TimeSpan Duration);

public sealed class LogicalPackageImageRefusalException : NotSupportedException
{
    public LogicalPackageImageRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Replays an approved append-and-retarget plan into a standalone, uncompressed
/// logical package image. The source UPK is always opened read-only. The result is
/// staging evidence for a future compressed-chunk writer, not a game-loadable UPK.
/// </summary>
public sealed class LogicalPackageImageService
{
    public const int CurrentManifestSchemaVersion = 1;
    public const string ManifestFileName = "arkive-logical-package-image.json";
    public const string LogicalImageFileName = "arkive-logical-package-image.bin";

    private const int CopyBufferBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly UpkPackageReader _summaryReader;
    private readonly IChunkProtectionProvider _protectionProvider;

    public LogicalPackageImageService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _summaryReader = summaryReader ?? new UpkPackageReader();
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
    }

    public static async Task<LogicalPackageImageManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = RequireFile(path, "LPI001", "logical package-image manifest");
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<LogicalPackageImageManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new LogicalPackageImageRefusalException(
                           "LPI002", "The logical package-image manifest was empty.");
        if (manifest.SchemaVersion != CurrentManifestSchemaVersion)
        {
            Refuse("LPI003", $"Logical package-image schema {manifest.SchemaVersion} is unsupported.");
        }
        return manifest;
    }

    public async Task<LogicalPackageImageResult> BuildAsync(
        LogicalPackageImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var planPath = RequireFile(
            request.PackageRewritePlanManifestPath, "LPI004", "package rewrite-plan manifest");
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            Refuse("LPI005", "The logical package-image output directory must be empty.");
        }

        var temporaryImagePath = Path.Combine(
            outputDirectory, $"{LogicalImageFileName}.{Guid.NewGuid():N}.tmp");
        var imagePath = Path.Combine(outputDirectory, LogicalImageFileName);
        try
        {
            var plan = await PackageRewritePlanService.LoadManifestAsync(
                planPath, cancellationToken).ConfigureAwait(false);
            ValidatePlanReadiness(plan);

            var assemblyPath = RequireFile(
                plan.TextureExportAssemblyManifestPath, "LPI007", "texture export-assembly manifest");
            var assembly = await TextureExportAssemblyService.LoadManifestAsync(
                assemblyPath, cancellationToken).ConfigureAwait(false);
            if (!plan.AssemblyId.Equals(assembly.AssemblyId, StringComparison.OrdinalIgnoreCase) ||
                !plan.TextureReplacementPayloadManifestPath.Equals(
                    assembly.TextureReplacementPayloadManifestPath, StringComparison.OrdinalIgnoreCase) ||
                plan.NativeExportIndex != assembly.NativeExportIndex ||
                plan.SourceSerialOffset != assembly.SerialOffset ||
                plan.SourceSerialSize != assembly.SourceSerialSize ||
                plan.ReplacementSerialSize != assembly.ReplacementExportBytes)
            {
                Refuse("LPI008", "The rewrite plan no longer agrees with its export assembly.");
            }

            var assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
            var replacementPath = ResolveWithin(
                assemblyDirectory, assembly.ReplacementExportRelativePath, "LPI009");
            var replacement = await File.ReadAllBytesAsync(
                replacementPath, cancellationToken).ConfigureAwait(false);
            if (replacement.LongLength != assembly.ReplacementExportBytes ||
                !Hash(replacement).Equals(
                    assembly.ReplacementExportSha256, StringComparison.OrdinalIgnoreCase))
            {
                Refuse("LPI010", "The assembled replacement export changed after rewrite planning.");
            }

            var packagePath = RequireFile(plan.SourcePackagePath, "LPI011", "source package");
            var packageHashBefore = await HashFileAsync(packagePath, cancellationToken).ConfigureAwait(false);
            if (!packageHashBefore.Equals(plan.SourcePackageSha256, StringComparison.OrdinalIgnoreCase) ||
                !packageHashBefore.Equals(assembly.SourcePackageSha256, StringComparison.OrdinalIgnoreCase))
            {
                Refuse("LPI012", "The source package SHA-256 changed after rewrite planning.");
            }

            var summary = _summaryReader.Read(packagePath);
            using var logical = new LogicalPackageStream(summary, _protectionProvider);
            if (logical.Length != plan.SourceLogicalPackageBytes ||
                summary.PhysicalFileSize != plan.SourcePhysicalPackageBytes)
            {
                Refuse("LPI013", "The current package dimensions no longer match the rewrite plan.");
            }
            ValidatePlanArithmetic(plan, replacement.LongLength);

            var tablePatches = ValidateSourceTablePatches(logical, plan);
            var rebasedReplacement = replacement.ToArray();
            ValidateAndApplyBulkRebases(rebasedReplacement, plan, assembly);
            var rebasedReplacementHash = Hash(rebasedReplacement);

            logical.Position = 0;
            string sourceLogicalHash;
            await using (var output = new FileStream(
                             temporaryImagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                             CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                sourceLogicalHash = await CopyAndHashAsync(
                    logical, output, logical.Length, cancellationToken).ConfigureAwait(false);
                if (output.Position != plan.SourceLogicalPackageBytes)
                {
                    Refuse("LPI014", "The logical source copy ended at an unexpected offset.");
                }

                if (plan.AppendPaddingBytes > 0)
                {
                    await output.WriteAsync(
                        new byte[plan.AppendPaddingBytes], cancellationToken).ConfigureAwait(false);
                }
                if (output.Position != plan.PlannedSerialOffset)
                {
                    Refuse("LPI015", "The append padding did not reach the planned serial offset.");
                }
                await output.WriteAsync(rebasedReplacement, cancellationToken).ConfigureAwait(false);

                foreach (var patch in plan.SelectedTablePatches)
                {
                    output.Position = patch.FieldLogicalOffset;
                    var bytes = new byte[sizeof(int)];
                    BinaryPrimitives.WriteInt32LittleEndian(bytes, patch.PlannedValue);
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
                output.SetLength(plan.PlannedLogicalPackageBytes);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var verification = await VerifyImageAsync(
                temporaryImagePath,
                packagePath,
                summary,
                plan,
                assembly,
                tablePatches,
                rebasedReplacement,
                rebasedReplacementHash,
                cancellationToken).ConfigureAwait(false);
            var packageHashAfter = await HashFileAsync(packagePath, cancellationToken).ConfigureAwait(false);
            if (!packageHashAfter.Equals(packageHashBefore, StringComparison.OrdinalIgnoreCase))
            {
                Refuse("LPI016", "The source package identity changed while the logical image was built.");
            }

            File.Move(temporaryImagePath, imagePath);
            watch.Stop();
            var checks = new List<TextureLayoutCheck>
            {
                new("LPI_SOURCE_IDENTITY", TextureLayoutCheckStatus.Passed,
                    $"Source physical package remained {packageHashAfter} before and after construction."),
                new("LPI_PLAN_REPLAY", TextureLayoutCheckStatus.Passed,
                    $"Replayed plan {plan.PlanId} into exactly {plan.PlannedLogicalPackageBytes:N0} logical bytes."),
                new("LPI_UNPLANNED_BYTES", TextureLayoutCheckStatus.Passed,
                    $"Compared {verification.UnplannedSourceBytes:N0} source bytes outside the audited table fields; every byte is identical."),
                new("LPI_TABLE_PATCHES", TextureLayoutCheckStatus.Passed,
                    $"Applied and reread all {verification.TablePatches.Count:N0} export-table field patches."),
                new("LPI_ORIGINAL_EXPORT", TextureLayoutCheckStatus.Passed,
                    $"Retained the original export at 0x{plan.SourceSerialOffset:X}+0x{plan.SourceSerialSize:X} byte-for-byte."),
                new("LPI_APPEND", TextureLayoutCheckStatus.Passed,
                    $"Verified the rebased replacement export at 0x{plan.PlannedSerialOffset:X}+0x{plan.PlannedSerialSize:X}."),
                new("LPI_BULK_REBASE", TextureLayoutCheckStatus.Passed,
                    $"Applied and reread all {verification.BulkRebases.Count:N0} appended mip bulk offsets."),
                new("LPI_IMAGE_HASH", TextureLayoutCheckStatus.Passed,
                    $"Final logical staging image SHA-256 is {verification.ImageSha256}."),
                new("LPI_PHYSICAL_SCOPE", TextureLayoutCheckStatus.Information,
                    "This is an uncompressed logical staging image. Chunk recompression, protection, physical offsets, and final UPK emission remain disabled."),
                new("LPI_NO_PACKAGE_WRITE", TextureLayoutCheckStatus.Information,
                    "The source UPK was opened read-only; only a separate staging .bin and manifest were written."),
            };

            var manifest = new LogicalPackageImageManifest(
                CurrentManifestSchemaVersion,
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                planPath,
                plan.PlanId,
                assemblyPath,
                assembly.AssemblyId,
                packagePath,
                packageHashAfter,
                sourceLogicalHash,
                plan.PackageAlias,
                plan.NativeExportIndex,
                plan.ClassName,
                plan.ObjectName,
                plan.PixelFormat,
                plan.SourceLogicalPackageBytes,
                plan.PlannedLogicalPackageBytes,
                LogicalImageFileName,
                verification.ImageSha256,
                plan.SourceSerialOffset,
                plan.SourceSerialSize,
                verification.OriginalExportSha256,
                OriginalExportBytesRetained: true,
                plan.PlannedSerialOffset,
                plan.PlannedSerialSize,
                rebasedReplacementHash,
                plan.AppendPaddingBytes,
                verification.AppendPaddingZeroFilled,
                verification.AuditedPatchBytes,
                verification.UnplannedSourceBytes,
                AllUnplannedSourceBytesIdentical: true,
                AllTablePatchesApplied: true,
                AllBulkOffsetsRebased: true,
                AppendedReplacementVerified: true,
                SourcePackageIdentityRetained: true,
                LogicalImageReadyForPhysicalWriter: true,
                PhysicalChunkWriterReady: false,
                PackageFileWriterReady: false,
                PackageWasModified: false,
                checks,
                verification.TablePatches,
                verification.BulkRebases,
                verification.Ranges,
                watch.Elapsed.TotalSeconds);
            var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
            await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            return new LogicalPackageImageResult(
                outputDirectory,
                manifestPath,
                imagePath,
                manifest,
                [imagePath, manifestPath],
                watch.Elapsed);
        }
        catch
        {
            if (File.Exists(temporaryImagePath)) File.Delete(temporaryImagePath);
            if (File.Exists(imagePath)) File.Delete(imagePath);
            throw;
        }
    }

    private static void ValidatePlanReadiness(PackageRewritePlanManifest plan)
    {
        if (!plan.PlanReadyForLogicalImageWriter || plan.PackageWasModified ||
            plan.ExistingExportsRelocated != 0 || plan.ExistingExportPayloadsModified != 0 ||
            !plan.OriginalExportBytesRetained || !plan.ExportTablePatchLocationsConfirmed ||
            !plan.AllBulkOffsetsRebased || !plan.AllExistingTablePointersUnchanged ||
            !plan.SelectedStrategy.StartsWith("Append", StringComparison.Ordinal))
        {
            Refuse("LPI006", "A fully verified append-and-retarget rewrite plan is required.");
        }
    }

    private static void ValidatePlanArithmetic(PackageRewritePlanManifest plan, long replacementBytes)
    {
        if (plan.AppendAlignment <= 0 || plan.AppendPaddingBytes < 0 ||
            plan.PlannedSerialOffset != plan.SourceLogicalPackageBytes + plan.AppendPaddingBytes ||
            plan.PlannedSerialSize != replacementBytes ||
            plan.PlannedLogicalPackageBytes != plan.PlannedSerialOffset + replacementBytes ||
            plan.PlannedLogicalGrowthBytes !=
            plan.PlannedLogicalPackageBytes - plan.SourceLogicalPackageBytes ||
            plan.PlannedSerialOffset % plan.AppendAlignment != 0)
        {
            Refuse("LPI017", "The rewrite-plan append arithmetic is inconsistent.");
        }
    }

    private static IReadOnlyList<LogicalPackageImageTablePatchEvidence> ValidateSourceTablePatches(
        LogicalPackageStream logical,
        PackageRewritePlanManifest plan)
    {
        if (plan.SelectedTablePatches.Count != 2 ||
            plan.SelectedTablePatches.Select(item => item.Field).Distinct(
                StringComparer.OrdinalIgnoreCase).Count() != 2 ||
            !plan.SelectedTablePatches.Any(item => item.Field.Equals("SerialSize", StringComparison.OrdinalIgnoreCase)) ||
            !plan.SelectedTablePatches.Any(item => item.Field.Equals("SerialOffset", StringComparison.OrdinalIgnoreCase)))
        {
            Refuse("LPI018", "The plan must contain exactly one SerialSize and one SerialOffset patch.");
        }

        var occupied = new HashSet<long>();
        var result = new List<LogicalPackageImageTablePatchEvidence>();
        foreach (var patch in plan.SelectedTablePatches.OrderBy(item => item.FieldLogicalOffset))
        {
            if (patch.ExportIndex != plan.NativeExportIndex || patch.FieldLogicalOffset < 0 ||
                patch.FieldLogicalOffset > logical.Length - sizeof(int))
            {
                Refuse("LPI019", "An export-table patch is outside the approved target or logical source range.");
            }
            for (var offset = patch.FieldLogicalOffset;
                 offset < patch.FieldLogicalOffset + sizeof(int); offset++)
            {
                if (!occupied.Add(offset)) Refuse("LPI020", "Export-table patch fields overlap.");
            }

            logical.Position = patch.FieldLogicalOffset;
            var bytes = new byte[sizeof(int)];
            logical.ReadExactly(bytes);
            var original = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            if (original != patch.OriginalValue)
            {
                Refuse("LPI021", $"The source {patch.Field} field changed after planning.");
            }
            result.Add(new LogicalPackageImageTablePatchEvidence(
                patch.ExportIndex,
                patch.Field,
                patch.FieldLogicalOffset,
                patch.OriginalValue,
                patch.PlannedValue,
                patch.PlannedValue));
        }
        return result;
    }

    private static void ValidateAndApplyBulkRebases(
        byte[] replacement,
        PackageRewritePlanManifest plan,
        TextureExportAssemblyManifest assembly)
    {
        if (plan.BulkOffsetRebases.Count != assembly.MipCount ||
            plan.BulkOffsetRebases.Select(item => item.MipIndex).Distinct().Count() != assembly.MipCount)
        {
            Refuse("LPI022", "The rewrite plan does not contain one bulk-offset rebase per mip.");
        }

        foreach (var rebase in plan.BulkOffsetRebases)
        {
            if (rebase.FieldRelativeOffset < 0 ||
                rebase.FieldRelativeOffset > replacement.LongLength - sizeof(int) ||
                rebase.PlannedValue != plan.PlannedSerialOffset + rebase.PayloadRelativeOffset ||
                rebase.PlannedPayloadLogicalOffset != rebase.PlannedValue)
            {
                Refuse("LPI023", $"Mip {rebase.MipIndex} has invalid bulk-offset arithmetic.");
            }
            var fieldOffset = checked((int)rebase.FieldRelativeOffset);
            var current = BinaryPrimitives.ReadInt32LittleEndian(
                replacement.AsSpan(fieldOffset, sizeof(int)));
            if (current != rebase.AssemblyValue)
            {
                Refuse("LPI024", $"Mip {rebase.MipIndex} assembled bulk offset changed after planning.");
            }
            BinaryPrimitives.WriteInt32LittleEndian(
                replacement.AsSpan(fieldOffset, sizeof(int)), rebase.PlannedValue);
        }
    }

    private async Task<ImageVerification> VerifyImageAsync(
        string imagePath,
        string packagePath,
        PackageSummary summary,
        PackageRewritePlanManifest plan,
        TextureExportAssemblyManifest assembly,
        IReadOnlyList<LogicalPackageImageTablePatchEvidence> plannedTablePatches,
        byte[] rebasedReplacement,
        string rebasedReplacementHash,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(imagePath);
        if (file.Length != plan.PlannedLogicalPackageBytes)
        {
            Refuse("LPI025", "The staged logical image has an unexpected length.");
        }

        var patchBytes = new Dictionary<long, byte>();
        foreach (var patch in plan.SelectedTablePatches)
        {
            var value = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(value, patch.PlannedValue);
            for (var index = 0; index < value.Length; index++)
            {
                patchBytes.Add(patch.FieldLogicalOffset + index, value[index]);
            }
        }

        using var source = new LogicalPackageStream(summary, _protectionProvider);
        await using var image = new FileStream(
            imagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sourceBuffer = new byte[CopyBufferBytes];
        var imageBuffer = new byte[CopyBufferBytes];
        long position = 0;
        while (position < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(sourceBuffer.Length, source.Length - position));
            source.ReadExactly(sourceBuffer.AsSpan(0, count));
            await image.ReadExactlyAsync(imageBuffer.AsMemory(0, count), cancellationToken)
                .ConfigureAwait(false);
            for (var index = 0; index < count; index++)
            {
                var logicalOffset = position + index;
                var expected = patchBytes.TryGetValue(logicalOffset, out var patched)
                    ? patched
                    : sourceBuffer[index];
                if (imageBuffer[index] != expected)
                {
                    Refuse(
                        "LPI026",
                        $"Logical image differs from the audited plan at 0x{logicalOffset:X}.");
                }
            }
            position += count;
        }

        var tableEvidence = new List<LogicalPackageImageTablePatchEvidence>();
        foreach (var patch in plannedTablePatches)
        {
            image.Position = patch.FieldLogicalOffset;
            var bytes = new byte[sizeof(int)];
            await image.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var actual = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            if (actual != patch.PlannedValue)
            {
                Refuse("LPI027", $"The staged {patch.Field} field did not retain its planned value.");
            }
            tableEvidence.Add(patch with { VerifiedImageValue = actual });
        }

        var paddingZero = true;
        if (plan.AppendPaddingBytes > 0)
        {
            image.Position = plan.SourceLogicalPackageBytes;
            var padding = new byte[plan.AppendPaddingBytes];
            await image.ReadExactlyAsync(padding, cancellationToken).ConfigureAwait(false);
            paddingZero = padding.All(value => value == 0);
            if (!paddingZero) Refuse("LPI028", "The append-alignment padding is not zero-filled.");
        }

        image.Position = plan.PlannedSerialOffset;
        var appended = new byte[rebasedReplacement.Length];
        await image.ReadExactlyAsync(appended, cancellationToken).ConfigureAwait(false);
        if (!appended.AsSpan().SequenceEqual(rebasedReplacement) ||
            !Hash(appended).Equals(rebasedReplacementHash, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("LPI029", "The appended replacement export differs from the rebased assembly.");
        }

        var bulkEvidence = new List<LogicalPackageImageBulkRebaseEvidence>();
        foreach (var rebase in plan.BulkOffsetRebases.OrderBy(item => item.MipIndex))
        {
            var fieldLogicalOffset = plan.PlannedSerialOffset + rebase.FieldRelativeOffset;
            image.Position = fieldLogicalOffset;
            var bytes = new byte[sizeof(int)];
            await image.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var actual = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            if (actual != rebase.PlannedValue)
            {
                Refuse("LPI030", $"Mip {rebase.MipIndex} bulk offset did not retain its planned value.");
            }
            bulkEvidence.Add(new LogicalPackageImageBulkRebaseEvidence(
                rebase.MipIndex,
                rebase.FieldRelativeOffset,
                fieldLogicalOffset,
                rebase.AssemblyValue,
                rebase.PlannedValue,
                actual,
                rebase.PayloadRelativeOffset,
                actual));
        }

        var sourceExportHash = await HashLogicalRangeAsync(
            packagePath,
            summary,
            plan.SourceSerialOffset,
            plan.SourceSerialSize,
            cancellationToken).ConfigureAwait(false);
        var retainedExportHash = await HashFileRangeAsync(
            imagePath,
            plan.SourceSerialOffset,
            plan.SourceSerialSize,
            cancellationToken).ConfigureAwait(false);
        if (!sourceExportHash.Equals(assembly.SourceExportSha256, StringComparison.OrdinalIgnoreCase) ||
            !retainedExportHash.Equals(sourceExportHash, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("LPI031", "The original target export was not retained byte-for-byte.");
        }

        var imageHash = await HashFileAsync(imagePath, cancellationToken).ConfigureAwait(false);
        var appendedHash = await HashFileRangeAsync(
            imagePath,
            plan.PlannedSerialOffset,
            plan.PlannedSerialSize,
            cancellationToken).ConfigureAwait(false);
        var paddingHash = await HashFileRangeAsync(
            imagePath,
            plan.SourceLogicalPackageBytes,
            plan.AppendPaddingBytes,
            cancellationToken).ConfigureAwait(false);
        var ranges = new[]
        {
            new LogicalPackageImageRangeEvidence(
                "Retained original Texture2D export",
                plan.SourceSerialOffset,
                plan.SourceSerialSize,
                sourceExportHash,
                retainedExportHash,
                true,
                "The original serialized export remains available at its old offset."),
            new LogicalPackageImageRangeEvidence(
                "Append alignment padding",
                plan.SourceLogicalPackageBytes,
                plan.AppendPaddingBytes,
                null,
                paddingHash,
                paddingZero,
                "Deterministic zero padding aligns the appended export."),
            new LogicalPackageImageRangeEvidence(
                "Appended rebased Texture2D export",
                plan.PlannedSerialOffset,
                plan.PlannedSerialSize,
                rebasedReplacementHash,
                appendedHash,
                appendedHash.Equals(rebasedReplacementHash, StringComparison.OrdinalIgnoreCase),
                "The replacement export contains the planned logical bulk offsets."),
        };

        return new ImageVerification(
            imageHash,
            sourceExportHash,
            patchBytes.Count,
            source.Length - patchBytes.Count,
            paddingZero,
            tableEvidence,
            bulkEvidence,
            ranges);
    }

    private static async Task<string> CopyAndHashAsync(
        Stream source,
        Stream destination,
        long bytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];
        long remaining = bytes;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(buffer.Length, remaining));
            var read = await source.ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The logical source ended during image construction.");
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task<string> HashLogicalRangeAsync(
        string packagePath,
        PackageSummary summary,
        long offset,
        long bytes,
        CancellationToken cancellationToken)
    {
        using var logical = new LogicalPackageStream(summary, _protectionProvider);
        logical.Position = offset;
        return await HashStreamRangeAsync(logical, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> HashFileRangeAsync(
        string path,
        long offset,
        long bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
        stream.Position = offset;
        return await HashStreamRangeAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> HashStreamRangeAsync(
        Stream stream,
        long bytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];
        long remaining = bytes;
        while (remaining > 0)
        {
            var count = checked((int)Math.Min(buffer.Length, remaining));
            var read = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("A hashed logical range was truncated.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
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
        throw new LogicalPackageImageRefusalException(code, message);

    private sealed record ImageVerification(
        string ImageSha256,
        string OriginalExportSha256,
        long AuditedPatchBytes,
        long UnplannedSourceBytes,
        bool AppendPaddingZeroFilled,
        IReadOnlyList<LogicalPackageImageTablePatchEvidence> TablePatches,
        IReadOnlyList<LogicalPackageImageBulkRebaseEvidence> BulkRebases,
        IReadOnlyList<LogicalPackageImageRangeEvidence> Ranges);
}
