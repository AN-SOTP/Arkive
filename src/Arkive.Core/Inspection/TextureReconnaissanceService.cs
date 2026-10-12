using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Packages;

namespace Arkive.Core.Inspection;

public sealed class TextureReconnaissanceService
{
    public const int CurrentSchemaVersion = 2;
    private const int MaximumExportBytes = 512 * 1024 * 1024;
    private const int MaximumPropertyCount = 4_096;
    private const int MaximumMipCount = 32;
    private const int MaximumMipScanBytes = 64 * 1024;
    private const int MaximumMipCandidates = 16;

    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;

    public TextureReconnaissanceService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(summaryReader, _protectionProvider);
    }

    public Task<TextureReconnaissanceReport> InspectAsync(
        TextureReconnaissanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Inspect(request, cancellationToken), cancellationToken);
    }

    private TextureReconnaissanceReport Inspect(
        TextureReconnaissanceRequest request,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var packagePath = Path.GetFullPath(request.PackagePath);
        var file = new FileInfo(packagePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The texture source package was not found.", packagePath);
        }
        var initialPackageLength = file.Length;
        var initialPackageLastWriteUtcTicks = file.LastWriteTimeUtc.Ticks;

        if (request.SerialOffset < 0 || request.SerialSize < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Texture reconnaissance requires a non-negative logical export range.");
        }

        if (request.SerialSize > MaximumExportBytes)
        {
            throw new PackageFormatException(
                $"Export size {request.SerialSize:N0} exceeds the reconnaissance safety limit " +
                $"of {MaximumExportBytes:N0} bytes.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var catalog = _catalogReader.Read(packagePath);
        var summary = catalog.Summary;
        using var logical = new LogicalPackageStream(summary, _protectionProvider);
        if (request.SerialOffset > logical.Length - request.SerialSize)
        {
            throw new PackageFormatException(
                $"The selected logical range 0x{request.SerialOffset:X}+0x{request.SerialSize:X} " +
                $"is outside the package length 0x{logical.Length:X}.");
        }

        var exportBytes = new byte[checked((int)request.SerialSize)];
        logical.Position = request.SerialOffset;
        logical.ReadExactly(exportBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var issues = new List<TextureReconnaissanceIssue>();
        var nativeMatches = catalog.Assets
            .Where(asset =>
                asset.SerialOffset == request.SerialOffset &&
                asset.SerialSize == request.SerialSize &&
                asset.ObjectName.Equals(request.ObjectName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var nativeExportIndex = nativeMatches.Length == 1
            ? nativeMatches[0].ExportIndex
            : (int?)null;
        if (nativeExportIndex is null)
        {
            issues.Add(new TextureReconnaissanceIssue(
                "Warning",
                "TEX004",
                "The selected object/range did not resolve uniquely to the native UE export table."));
        }
        else if (nativeExportIndex != request.ExportIndex)
        {
            issues.Add(new TextureReconnaissanceIssue(
                "Information",
                "TEX005",
                $"The selection reports export index {request.ExportIndex}; the native UE table index " +
                $"is {nativeExportIndex}. This is the expected zero-based UModel versus one-based UE " +
                "index convention."));
        }

        if (request.Kind != AssetKind.Texture)
        {
            issues.Add(new TextureReconnaissanceIssue(
                "Warning",
                "TEX001",
                $"The selected catalogue row is categorized as {request.Kind}, not Texture."));
        }

        if (!request.ClassName.Contains("Texture2D", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new TextureReconnaissanceIssue(
                "Warning",
                "TEX002",
                $"The reported class {request.ClassName} is not a recognized Texture2D class name."));
        }
        else if (!request.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new TextureReconnaissanceIssue(
                "Information",
                "TEX003",
                $"{request.ClassName} is a Texture2D variant. Its native tail remains candidate evidence " +
                "until this class is verified independently."));
        }

        var propertyParse = FindPropertyStream(
            exportBytes,
            request.SerialOffset,
            catalog.Names);
        IReadOnlyList<TexturePropertyEvidence> properties;
        long? propertyStart;
        long? nativeStart;
        if (propertyParse is null)
        {
            properties = [];
            propertyStart = null;
            nativeStart = null;
            issues.Add(new TextureReconnaissanceIssue(
                "Warning",
                "TEX100",
                "Arkive could not prove the beginning and terminator of the UObject property stream. " +
                "Mip scanning was limited to the beginning of the export."));
        }
        else
        {
            properties = propertyParse.Properties;
            propertyStart = propertyParse.StartOffset;
            nativeStart = propertyParse.EndOffset;
            if (propertyParse.StartOffset != 0)
            {
                issues.Add(new TextureReconnaissanceIssue(
                    "Information",
                    "TEX101",
                    $"The verified property stream begins at relative offset " +
                    $"0x{propertyParse.StartOffset:X}; the preceding bytes were retained as unknown."));
            }
        }

        var format = ResolvePixelFormat(properties);
        var usesCrunchCompression = properties.Any(property =>
            property.Name.Equals("bUseCrunchCompression", StringComparison.OrdinalIgnoreCase) &&
            (property.NumericValue == 1 ||
             property.ValuePreview.Equals("True", StringComparison.OrdinalIgnoreCase)));
        if (usesCrunchCompression)
        {
            issues.Add(new TextureReconnaissanceIssue(
                "Information",
                "TEX102",
                "bUseCrunchCompression is True. Stored mip sizes are compressed payload sizes and " +
                "are not expected to equal raw pixel-format block geometry."));
        }

        var scanStart = checked((int)(nativeStart ?? 0));
        var candidates = ScanMipCandidates(
            exportBytes,
            request.SerialOffset,
            scanStart,
            format,
            usesCrunchCompression,
            summary,
            cancellationToken);
        if (candidates.Count == 0)
        {
            issues.Add(new TextureReconnaissanceIssue(
                "Warning",
                "TEX200",
                $"No bounded mip-array candidate was found in the first " +
                $"{Math.Min(MaximumMipScanBytes, Math.Max(0, exportBytes.Length - scanStart)):N0} " +
                "bytes of the native texture region."));
        }
        else
        {
            var best = candidates[0];
            if (best.Confidence != TextureReconnaissanceConfidence.Confirmed)
            {
                issues.Add(new TextureReconnaissanceIssue(
                    "Warning",
                    "TEX201",
                    $"The best mip layout is {best.Confidence}; Arkive will not treat its ranges as " +
                    "replacement-safe."));
            }

            if (candidates.Count > 1 && candidates[1].Score == best.Score)
            {
                issues.Add(new TextureReconnaissanceIssue(
                    "Warning",
                    "TEX202",
                    "Multiple mip layouts have the same evidence score. No layout is uniquely proven."));
            }
        }

        var exportHash = Convert.ToHexString(SHA256.HashData(exportBytes));
        var packageHash = string.IsNullOrWhiteSpace(request.KnownPackageSha256)
            ? ComputeFileHash(packagePath, cancellationToken)
            : NormalizeSha256(request.KnownPackageSha256);
        var currentFile = new FileInfo(packagePath);
        if (!currentFile.Exists || currentFile.Length != initialPackageLength ||
            currentFile.LastWriteTimeUtc.Ticks != initialPackageLastWriteUtcTicks)
        {
            throw new IOException(
                "The source package changed while Texture2D reconnaissance was running; " +
                "the mixed-state report was discarded.");
        }
        watch.Stop();
        return new TextureReconnaissanceReport(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            packagePath,
            file.Name,
            request.PackageAlias,
            initialPackageLength,
            initialPackageLastWriteUtcTicks,
            packageHash,
            new TexturePackageProfile(
                summary.FileVersion,
                summary.LicenseeVersion,
                summary.EngineVersion,
                summary.CookerVersion,
                summary.PackageFlags,
                summary.CompressionFlags,
                summary.PackageGuid,
                summary.CompressedChunks.Count,
                logical.Length),
            logical.ProtectionProviderName,
            request.ExportIndex,
            nativeExportIndex,
            request.Kind,
            request.ClassName,
            request.ObjectName,
            request.SerialOffset,
            request.SerialSize,
            exportHash,
            propertyStart,
            nativeStart,
            format.Evidence,
            properties,
            candidates,
            BuildStorageEvidence(summary, request.SerialOffset, request.SerialSize),
            issues,
            watch.Elapsed);
    }

    private static PropertyParseResult? FindPropertyStream(
        ReadOnlySpan<byte> exportBytes,
        long serialOffset,
        IReadOnlyList<UnrealNameEntry> names)
    {
        PropertyParseResult? best = null;
        var maximumStart = Math.Min(64, Math.Max(0, exportBytes.Length - 8));
        for (var start = 0; start <= maximumStart; start += 4)
        {
            foreach (var lostArkIntTagExtension in new[] { false, true })
            {
                if (!TryParsePropertyStream(
                        exportBytes,
                        serialOffset,
                        names,
                        start,
                        lostArkIntTagExtension,
                        out var candidate))
                {
                    continue;
                }

                if (best is null || candidate.Properties.Count > best.Properties.Count ||
                    (candidate.Properties.Count == best.Properties.Count &&
                     candidate.StartOffset < best.StartOffset))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private static bool TryParsePropertyStream(
        ReadOnlySpan<byte> bytes,
        long serialOffset,
        IReadOnlyList<UnrealNameEntry> names,
        int start,
        bool lostArkIntTagExtension,
        out PropertyParseResult result)
    {
        result = null!;
        var cursor = start;
        var properties = new List<TexturePropertyEvidence>();
        for (var propertyIndex = 0; propertyIndex < MaximumPropertyCount; propertyIndex++)
        {
            var tagStart = cursor;
            if (!TryReadName(bytes, ref cursor, names, out var propertyName))
            {
                return false;
            }

            if (propertyName.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                result = new PropertyParseResult(start, cursor, properties);
                return start == 0 || properties.Count > 0;
            }

            if (!TryReadName(bytes, ref cursor, names, out var propertyType) ||
                cursor > bytes.Length - 8)
            {
                return false;
            }

            if (!propertyType.EndsWith("Property", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var size = ReadInt32(bytes, cursor);
            var arrayIndex = ReadInt32(bytes, cursor + 4);
            cursor += 8;
            if (size < 0 || arrayIndex < 0)
            {
                return false;
            }

            string? typeMetadata = null;
            byte? taggedBool = null;
            if (propertyType.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) ||
                propertyType.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadName(bytes, ref cursor, names, out typeMetadata))
                {
                    return false;
                }
            }
            else if (lostArkIntTagExtension &&
                     propertyType.Equals("IntProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadName(bytes, ref cursor, names, out var tagMarker))
                {
                    return false;
                }

                typeMetadata = $"Lost Ark tag marker: {tagMarker}";
            }
            else if (propertyType.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (cursor >= bytes.Length)
                {
                    return false;
                }

                taggedBool = bytes[cursor++];
            }

            var valueOffset = cursor;
            if (size > bytes.Length - valueOffset)
            {
                return false;
            }

            var value = bytes.Slice(valueOffset, size);
            var preview = PreviewPropertyValue(
                propertyType,
                typeMetadata,
                value,
                taggedBool,
                names,
                out var numericValue);
            properties.Add(new TexturePropertyEvidence(
                tagStart,
                serialOffset + tagStart,
                propertyName,
                propertyType,
                size,
                arrayIndex,
                typeMetadata,
                valueOffset,
                serialOffset + valueOffset,
                preview,
                numericValue,
                Convert.ToHexString(value[..Math.Min(16, value.Length)])));
            cursor += size;
        }

        return false;
    }

    private static string PreviewPropertyValue(
        string type,
        string? typeMetadata,
        ReadOnlySpan<byte> value,
        byte? taggedBool,
        IReadOnlyList<UnrealNameEntry> names,
        out long? numericValue)
    {
        numericValue = null;
        if (type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase) && taggedBool is not null)
        {
            numericValue = taggedBool.Value == 0 ? 0 : 1;
            return taggedBool.Value == 0 ? "False" : "True";
        }

        if (type.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(typeMetadata) &&
            value.Length >= 8)
        {
            var cursor = 0;
            if (TryReadName(value, ref cursor, names, out var enumValue))
            {
                numericValue = PixelFormatValue(enumValue);
                return enumValue;
            }
        }

        if (type.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase) && value.Length >= 1)
        {
            numericValue = value[0];
            return string.IsNullOrWhiteSpace(typeMetadata)
                ? value[0].ToString(CultureInfo.InvariantCulture)
                : $"{typeMetadata}({value[0]})";
        }

        if ((type.Equals("IntProperty", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("ObjectProperty", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("ComponentProperty", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("InterfaceProperty", StringComparison.OrdinalIgnoreCase)) &&
            value.Length >= 4)
        {
            numericValue = ReadInt32(value, 0);
            return numericValue.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (type.Equals("FloatProperty", StringComparison.OrdinalIgnoreCase) && value.Length >= 4)
        {
            var raw = ReadInt32(value, 0);
            return BitConverter.Int32BitsToSingle(raw).ToString("R", CultureInfo.InvariantCulture);
        }

        if (type.Equals("NameProperty", StringComparison.OrdinalIgnoreCase) && value.Length >= 8)
        {
            var cursor = 0;
            if (TryReadName(value, ref cursor, names, out var name))
            {
                return name;
            }
        }

        if (type.Equals("StrProperty", StringComparison.OrdinalIgnoreCase) &&
            TryReadUnrealString(value, out var text))
        {
            return text;
        }

        return value.IsEmpty ? "(empty)" : Convert.ToHexString(value[..Math.Min(16, value.Length)]);
    }

    private static PixelFormatEvidence ResolvePixelFormat(
        IReadOnlyList<TexturePropertyEvidence> properties)
    {
        var property = properties.FirstOrDefault(item =>
            (item.Name.Equals("Format", StringComparison.OrdinalIgnoreCase) ||
             item.Name.Equals("PixelFormat", StringComparison.OrdinalIgnoreCase)) &&
            item.NumericValue is not null);
        if (property is null)
        {
            return new PixelFormatEvidence(null, null, "Not found in verified properties");
        }

        var value = checked((int)property.NumericValue!.Value);
        var name = PixelFormatName(value);
        return new PixelFormatEvidence(
            value,
            name,
            name is null
                ? $"Value {value} from {property.Name}; enum mapping unverified"
                : $"{name} ({value}) from {property.Name}");
    }

    private static IReadOnlyList<TextureMipChainEvidence> ScanMipCandidates(
        ReadOnlySpan<byte> bytes,
        long serialOffset,
        int nativeStart,
        PixelFormatEvidence format,
        bool usesCrunchCompression,
        PackageSummary summary,
        CancellationToken cancellationToken)
    {
        if (nativeStart < 0 || nativeStart > bytes.Length - 4)
        {
            return [];
        }

        var end = Math.Min(bytes.Length - 4, nativeStart + MaximumMipScanBytes);
        var candidates = new List<TextureMipChainEvidence>();
        for (var offset = nativeStart; offset <= end; offset++)
        {
            if ((offset & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var count = ReadInt32(bytes, offset);
            if (count is < 1 or > MaximumMipCount)
            {
                continue;
            }

            var candidate = TryParseMipChain(
                bytes,
                serialOffset,
                offset,
                count,
                format,
                usesCrunchCompression,
                summary);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        var ordered = candidates
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.ArrayRelativeOffset)
            .Take(MaximumMipCandidates)
            .Select((item, index) => item with { CandidateNumber = index + 1 })
            .ToArray();
        return ordered;
    }

    private static TextureMipChainEvidence? TryParseMipChain(
        ReadOnlySpan<byte> bytes,
        long serialOffset,
        int arrayOffset,
        int count,
        PixelFormatEvidence format,
        bool usesCrunchCompression,
        PackageSummary summary)
    {
        var states = new List<MipParseState>
        {
            new(arrayOffset + 4, [], 0, 0, 0, []),
        };

        for (var mipIndex = 0; mipIndex < count; mipIndex++)
        {
            var next = new List<MipParseState>();
            foreach (var state in states)
            {
                if (state.Cursor > bytes.Length - 24)
                {
                    continue;
                }

                var flags = ReadUInt32(bytes, state.Cursor);
                var elementCount = ReadInt32(bytes, state.Cursor + 4);
                var storedSize = ReadInt32(bytes, state.Cursor + 8);
                var bulkOffset = ReadInt32(bytes, state.Cursor + 12);
                if (elementCount < 0 || storedSize < 0 || bulkOffset < 0 ||
                    elementCount > MaximumExportBytes || storedSize > MaximumExportBytes)
                {
                    continue;
                }

                var headerEnd = state.Cursor + 16;
                AddMipVariant(
                    bytes,
                    serialOffset,
                    summary,
                    format,
                    mipIndex,
                    state,
                    next,
                    flags,
                    elementCount,
                    storedSize,
                    bulkOffset,
                    headerEnd,
                    payloadOffset: null);
                if (storedSize > 0 && storedSize <= bytes.Length - headerEnd - 8)
                {
                    AddMipVariant(
                        bytes,
                        serialOffset,
                        summary,
                        format,
                        mipIndex,
                        state,
                        next,
                        flags,
                        elementCount,
                        storedSize,
                        bulkOffset,
                        headerEnd + storedSize,
                        payloadOffset: headerEnd);
                }
            }

            states = next
                .OrderByDescending(item => item.Score)
                .Take(64)
                .ToList();
            if (states.Count == 0)
            {
                return null;
            }
        }

        var best = states.OrderByDescending(item => item.Score).First();
        if (best.Score < Math.Max(8, count * 5))
        {
            return null;
        }

        var confidence = best.OffsetMatches == count && best.FormatMatches == count
            ? TextureReconnaissanceConfidence.Confirmed
            : best.OffsetMatches >= Math.Max(1, count - 1) || (count > 1 && best.Score >= count * 8)
                ? TextureReconnaissanceConfidence.StrongCandidate
                : TextureReconnaissanceConfidence.Candidate;
        var reasons = best.Reasons
            .Append($"{best.OffsetMatches}/{count} bulk offsets match inline payload positions.")
            .Append(format.Name is null
                ? "Pixel format was not independently resolved from the verified property stream."
                : usesCrunchCompression
                    ? $"{format.Name} was independently resolved; Crunch compression means stored " +
                      "sizes are not expected to match raw block geometry."
                    : $"{best.FormatMatches}/{count} mip sizes match {format.Name} block geometry.")
            .ToArray();
        return new TextureMipChainEvidence(
            0,
            arrayOffset,
            serialOffset + arrayOffset,
            count,
            best.Score,
            confidence,
            format.Evidence,
            reasons,
            best.Mips);
    }

    private static void AddMipVariant(
        ReadOnlySpan<byte> bytes,
        long serialOffset,
        PackageSummary summary,
        PixelFormatEvidence format,
        int mipIndex,
        MipParseState state,
        List<MipParseState> output,
        uint flags,
        int elementCount,
        int storedSize,
        int bulkOffset,
        int dimensionsOffset,
        int? payloadOffset)
    {
        if (dimensionsOffset < 0 || dimensionsOffset > bytes.Length - 8)
        {
            return;
        }

        var width = ReadInt32(bytes, dimensionsOffset);
        var height = ReadInt32(bytes, dimensionsOffset + 4);
        if (!IsReasonableDimension(width) || !IsReasonableDimension(height))
        {
            return;
        }

        if (state.Mips.Count > 0)
        {
            var previous = state.Mips[^1];
            if (width > previous.Width || height > previous.Height)
            {
                return;
            }
        }

        var score = state.Score + 4;
        var reasons = new List<string>(state.Reasons);
        if (IsPowerOfTwo(width) && IsPowerOfTwo(height))
        {
            score += 1;
        }

        if (state.Mips.Count > 0)
        {
            var previous = state.Mips[^1];
            if (width == Math.Max(1, previous.Width / 2) &&
                height == Math.Max(1, previous.Height / 2))
            {
                score += 2;
            }
        }

        long? payloadLogical = payloadOffset is null ? null : serialOffset + payloadOffset.Value;
        var offsetMatches = payloadLogical is not null && bulkOffset == payloadLogical.Value;
        if (offsetMatches)
        {
            score += 5;
        }

        var expectedSize = ExpectedMipSize(format.Name, width, height);
        var sizeMatches = expectedSize is not null &&
                          (storedSize == expectedSize.Value || elementCount == expectedSize.Value);
        if (sizeMatches)
        {
            score += 5;
        }

        var storage = payloadOffset is not null
            ? "Inline"
            : storedSize == 0
                ? "Empty"
                : (flags & 0x1) != 0
                    ? "External file/cache"
                    : bulkOffset > 0
                        ? "Referenced/non-inline"
                        : "Unresolved";
        string? hash = null;
        IReadOnlyList<int> chunks = [];
        if (payloadOffset is not null)
        {
            hash = Convert.ToHexString(SHA256.HashData(bytes.Slice(payloadOffset.Value, storedSize)));
            chunks = FindChunkNumbers(summary, payloadLogical!.Value, storedSize);
        }

        if ((flags & 0xFFFF0000u) != 0)
        {
            reasons.Add($"Mip {mipIndex} uses high/custom bulk flags 0x{flags:X8}.");
        }

        var mip = new TextureMipEvidence(
            mipIndex,
            state.Cursor,
            serialOffset + state.Cursor,
            flags,
            elementCount,
            storedSize,
            bulkOffset,
            width,
            height,
            storage,
            payloadOffset,
            payloadLogical,
            hash,
            chunks,
            offsetMatches,
            sizeMatches);
        var mips = state.Mips.Append(mip).ToArray();
        output.Add(new MipParseState(
            dimensionsOffset + 8,
            mips,
            score,
            state.OffsetMatches + (offsetMatches ? 1 : 0),
            state.FormatMatches + (sizeMatches ? 1 : 0),
            reasons));
    }

    private static IReadOnlyList<TextureStorageEvidence> BuildStorageEvidence(
        PackageSummary summary,
        long logicalOffset,
        long logicalSize)
    {
        if (logicalSize == 0)
        {
            return [];
        }

        if (summary.CompressedChunks.Count == 0)
        {
            return
            [
                new TextureStorageEvidence(
                    "Raw physical bytes", null, logicalOffset, logicalSize,
                    logicalOffset, logicalSize, null),
            ];
        }

        var end = checked(logicalOffset + logicalSize);
        var cursor = logicalOffset;
        var result = new List<TextureStorageEvidence>();
        for (var index = 0; index < summary.CompressedChunks.Count && cursor < end; index++)
        {
            var chunk = summary.CompressedChunks[index];
            var chunkStart = (long)chunk.UncompressedOffset;
            var chunkEnd = checked(chunkStart + chunk.UncompressedSize);
            if (chunkEnd <= cursor)
            {
                continue;
            }

            if (cursor < chunkStart)
            {
                var rawEnd = Math.Min(end, chunkStart);
                result.Add(new TextureStorageEvidence(
                    "Raw physical bytes", null, cursor, rawEnd - cursor,
                    cursor, rawEnd - cursor, null));
                cursor = rawEnd;
                if (cursor >= end)
                {
                    break;
                }
            }

            if (cursor < chunkEnd && end > chunkStart)
            {
                var overlapStart = Math.Max(cursor, chunkStart);
                var overlapEnd = Math.Min(end, chunkEnd);
                result.Add(new TextureStorageEvidence(
                    "Compressed package chunk", index + 1,
                    overlapStart, overlapEnd - overlapStart,
                    chunk.CompressedOffset, chunk.CompressedSize, chunk.LostArkFlags));
                cursor = overlapEnd;
            }
        }

        if (cursor < end)
        {
            result.Add(new TextureStorageEvidence(
                "Raw physical bytes", null, cursor, end - cursor, cursor, end - cursor, null));
        }

        return result;
    }

    private static IReadOnlyList<int> FindChunkNumbers(
        PackageSummary summary,
        long logicalOffset,
        long logicalSize)
    {
        if (logicalSize <= 0)
        {
            return [];
        }

        var end = checked(logicalOffset + logicalSize);
        return summary.CompressedChunks
            .Select((chunk, index) => new { chunk, index })
            .Where(item => logicalOffset < (long)item.chunk.UncompressedEnd &&
                           end > item.chunk.UncompressedOffset)
            .Select(item => item.index + 1)
            .ToArray();
    }

    private static string ComputeFileHash(string path, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024, FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The supplied package SHA-256 is not 64 hexadecimal characters.");
        }

        return normalized;
    }

    private static bool TryReadName(
        ReadOnlySpan<byte> bytes,
        ref int cursor,
        IReadOnlyList<UnrealNameEntry> names,
        out string value)
    {
        value = string.Empty;
        if (cursor < 0 || cursor > bytes.Length - 8)
        {
            return false;
        }

        var index = ReadInt32(bytes, cursor);
        var number = ReadInt32(bytes, cursor + 4);
        cursor += 8;
        if (index < 0 || index >= names.Count || number < 0)
        {
            return false;
        }

        value = new UnrealNameReference(index, number).Resolve(names);
        return true;
    }

    private static bool TryReadUnrealString(ReadOnlySpan<byte> bytes, out string value)
    {
        value = string.Empty;
        if (bytes.Length < 4)
        {
            return false;
        }

        var count = ReadInt32(bytes, 0);
        if (count > 0 && count <= bytes.Length - 4)
        {
            var data = bytes.Slice(4, count);
            var length = data.Length > 0 && data[^1] == 0 ? data.Length - 1 : data.Length;
            value = Encoding.Latin1.GetString(data[..length]);
            return true;
        }

        if (count < 0 && count != int.MinValue)
        {
            var characters = -count;
            if (characters <= (bytes.Length - 4) / 2)
            {
                var data = bytes.Slice(4, characters * 2);
                var length = data.Length >= 2 && data[^2] == 0 && data[^1] == 0
                    ? data.Length - 2
                    : data.Length;
                value = Encoding.Unicode.GetString(data[..length]);
                return true;
            }
        }

        return false;
    }

    private static string? PixelFormatName(int value) => value switch
    {
        2 => "PF_A8R8G8B8",
        3 => "PF_G8",
        4 => "PF_G16",
        5 => "PF_DXT1",
        6 => "PF_DXT3",
        7 => "PF_DXT5",
        24 => "PF_BC5",
        25 => "PF_V8U8",
        29 => "PF_R8G8B8A8",
        30 => "PF_A8",
        _ => null,
    };

    private static int? PixelFormatValue(string name) => name.ToUpperInvariant() switch
    {
        "PF_A8R8G8B8" => 2,
        "PF_G8" => 3,
        "PF_G16" => 4,
        "PF_DXT1" => 5,
        "PF_DXT3" => 6,
        "PF_DXT5" => 7,
        "PF_BC5" => 24,
        "PF_V8U8" => 25,
        "PF_R8G8B8A8" => 29,
        "PF_A8" => 30,
        _ => null,
    };

    private static long? ExpectedMipSize(string? format, int width, int height)
    {
        try
        {
            return format switch
            {
                "PF_DXT1" => checked((long)Math.Max(1, (width + 3) / 4) *
                                     Math.Max(1, (height + 3) / 4) * 8),
                "PF_DXT3" or "PF_DXT5" or "PF_BC5" =>
                    checked((long)Math.Max(1, (width + 3) / 4) *
                            Math.Max(1, (height + 3) / 4) * 16),
                "PF_A8R8G8B8" or "PF_R8G8B8A8" => checked((long)width * height * 4),
                "PF_G16" or "PF_V8U8" => checked((long)width * height * 2),
                "PF_G8" or "PF_A8" => checked((long)width * height),
                _ => null,
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool IsReasonableDimension(int value) => value is >= 1 and <= 65_536;

    private static bool IsPowerOfTwo(int value) => (value & (value - 1)) == 0;

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

    private sealed record PropertyParseResult(
        int StartOffset,
        int EndOffset,
        IReadOnlyList<TexturePropertyEvidence> Properties);

    private sealed record PixelFormatEvidence(int? Value, string? Name, string Evidence);

    private sealed record MipParseState(
        int Cursor,
        IReadOnlyList<TextureMipEvidence> Mips,
        int Score,
        int OffsetMatches,
        int FormatMatches,
        IReadOnlyList<string> Reasons);
}
