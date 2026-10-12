using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.External;
using Arkive.Core.Meshes;
using Arkive.Core.Packages;

namespace Arkive.Core.Inspection;

public sealed class SkeletalMeshReconnaissanceService
{
    public const int CurrentSchemaVersion = 4;
    private const int MaximumExportBytes = 768 * 1024 * 1024;
    private const int MaximumPropertyCount = 4_096;

    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly UmodelBridge? _umodelBridge;

    public SkeletalMeshReconnaissanceService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null,
        UmodelBridge? umodelBridge = null)
    {
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(summaryReader, _protectionProvider);
        _umodelBridge = umodelBridge;
    }

    public async Task<SkeletalMeshReconnaissanceReport> InspectAsync(
        SkeletalMeshReconnaissanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var report = await Task.Run(() => InspectNative(request, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        SkeletalMeshOracleEvidence? oracle = null;
        var issues = report.Issues.ToList();
        if (request.SkipUmodelOracle)
        {
            issues.Add(new SkeletalMeshReconnaissanceIssue(
                "Information", "SKM303",
                "The UModel oracle was intentionally deferred for this cumulative staging filename. The final package must pass exact-object external validation under its original physical name."));
        }
        else if (_umodelBridge is null)
        {
            issues.Add(new SkeletalMeshReconnaissanceIssue(
                "Information", "SKM300",
                "UModel is unavailable, so the independent semantic LOD/bone oracle was skipped."));
        }
        else
        {
            try
            {
                var dump = await _umodelBridge.DumpObjectAsync(
                    report.PackagePath,
                    new UmodelAssetEntry(
                        request.ExportIndex,
                        request.SerialOffset,
                        request.SerialSize,
                        request.ClassName,
                        request.ObjectName),
                    cancellationToken).ConfigureAwait(false);
                oracle = ParseSkeletalMeshDump(dump.RawOutput, dump.Duration, dump.Asset.ClassName);
                if (oracle.Lods.Count == 0 || oracle.BoneCount <= 0)
                {
                    issues.Add(new SkeletalMeshReconnaissanceIssue(
                        "Warning", "SKM301",
                        "UModel loaded the object but its dump did not contain a complete SkeletalMesh LOD/bone summary."));
                }
                else if (report.NativeLayout is not null)
                {
                    ValidateAgainstOracle(report.NativeLayout, oracle, issues);
                }
            }
            catch (Exception exception) when (exception is UmodelBridgeException or IOException)
            {
                issues.Add(new SkeletalMeshReconnaissanceIssue(
                    "Warning", "SKM302",
                    $"The independent UModel semantic probe failed: {exception.Message}"));
            }
        }

        watch.Stop();
        var finalReadiness = issues.Any(issue =>
            issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
            ? SkeletalMeshReadiness.Refused
            : report.Readiness;
        return report with
        {
            Oracle = oracle,
            Issues = issues,
            Readiness = finalReadiness,
            Duration = watch.Elapsed,
        };
    }

    private static void ValidateAgainstOracle(
        SkeletalMeshNativeLayoutEvidence native,
        SkeletalMeshOracleEvidence oracle,
        ICollection<SkeletalMeshReconnaissanceIssue> issues)
    {
        if (native.BoneCount != oracle.BoneCount)
            AddMismatch("SKM310", $"Reference skeleton count differs: native={native.BoneCount}, UModel={oracle.BoneCount}.");
        if (native.Lods.Count != oracle.LodCount || native.Lods.Count != oracle.Lods.Count)
            AddMismatch("SKM311", $"LOD count differs: native={native.Lods.Count}, UModel={oracle.LodCount}/{oracle.Lods.Count}.");

        foreach (var nativeLod in native.Lods)
        {
            var oracleLod = oracle.Lods.SingleOrDefault(item => item.LodIndex == nativeLod.LodIndex);
            if (oracleLod is null)
            {
                AddMismatch("SKM312", $"UModel did not report native LOD {nativeLod.LodIndex}.");
                continue;
            }
            if (nativeLod.VertexCount != oracleLod.VertexCount)
                AddMismatch("SKM313", $"LOD {nativeLod.LodIndex} vertex count differs: native={nativeLod.VertexCount}, UModel={oracleLod.VertexCount}.");
            if (oracleLod.IndexCount > 0 && nativeLod.IndexBuffer.ElementCount != oracleLod.IndexCount)
                AddMismatch("SKM314", $"LOD {nativeLod.LodIndex} index count differs: native={nativeLod.IndexBuffer.ElementCount}, UModel={oracleLod.IndexCount}.");
            if (nativeLod.TextureCoordinateCount != oracleLod.TextureCoordinateCount)
                AddMismatch("SKM315", $"LOD {nativeLod.LodIndex} UV count differs: native={nativeLod.TextureCoordinateCount}, UModel={oracleLod.TextureCoordinateCount}.");
            var nativeTriangles = nativeLod.Sections.Sum(section => section.TriangleCount);
            if (nativeTriangles != oracleLod.TriangleCount)
                AddMismatch("SKM316", $"LOD {nativeLod.LodIndex} triangle count differs: native={nativeTriangles}, UModel={oracleLod.TriangleCount}.");
            if (nativeLod.Sections.Count != oracleLod.Sections.Count)
                AddMismatch("SKM317", $"LOD {nativeLod.LodIndex} section count differs: native={nativeLod.Sections.Count}, UModel={oracleLod.Sections.Count}.");
        }
        return;

        void AddMismatch(string code, string message) => issues.Add(
            new SkeletalMeshReconnaissanceIssue("Error", code,
                "Native parser/UModel cross-check failed. " + message));
    }

    private SkeletalMeshReconnaissanceReport InspectNative(
        SkeletalMeshReconnaissanceRequest request,
        CancellationToken cancellationToken)
    {
        var packagePath = Path.GetFullPath(request.PackagePath);
        var file = new FileInfo(packagePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The SkeletalMesh source package was not found.", packagePath);
        }

        if (request.SerialOffset < 0 || request.SerialSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request),
                "SkeletalMesh reconnaissance requires a positive logical export range.");
        }
        if (request.SerialSize > MaximumExportBytes)
        {
            throw new PackageFormatException(
                $"Export size {request.SerialSize:N0} exceeds the SkeletalMesh safety limit of {MaximumExportBytes:N0} bytes.");
        }

        var initialLength = file.Length;
        var initialWriteTicks = file.LastWriteTimeUtc.Ticks;
        var catalog = _catalogReader.Read(packagePath);
        var summary = catalog.Summary;
        using var logical = new LogicalPackageStream(summary, _protectionProvider);
        if (request.SerialOffset > logical.Length - request.SerialSize)
        {
            throw new PackageFormatException(
                $"The selected logical range 0x{request.SerialOffset:X}+0x{request.SerialSize:X} is outside the package.");
        }

        var bytes = new byte[checked((int)request.SerialSize)];
        logical.Position = request.SerialOffset;
        logical.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();

        var issues = new List<SkeletalMeshReconnaissanceIssue>();
        var matches = catalog.Assets.Where(asset =>
            asset.SerialOffset == request.SerialOffset &&
            asset.SerialSize == request.SerialSize &&
            asset.ObjectName.Equals(request.ObjectName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var nativeExportIndex = matches.Length == 1 ? matches[0].ExportIndex : (int?)null;
        if (nativeExportIndex is null)
        {
            issues.Add(new SkeletalMeshReconnaissanceIssue(
                "Error", "SKM004", "The selected object/range did not resolve uniquely to the native export table."));
        }
        else if (nativeExportIndex != request.ExportIndex)
        {
            issues.Add(new SkeletalMeshReconnaissanceIssue(
                "Information", "SKM005",
                $"Selection index {request.ExportIndex} maps to native UE export index {nativeExportIndex}."));
        }

        if (request.Kind != AssetKind.SkeletalMesh ||
            !request.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new SkeletalMeshReconnaissanceIssue(
                "Error", "SKM001", $"The selected row is {request.Kind}/{request.ClassName}, not SkeletalMesh."));
        }

        var propertyParse = FindPropertyStream(bytes, request.SerialOffset, catalog.Names);
        long? propertyStart = propertyParse?.StartOffset;
        long? nativeStart = propertyParse?.EndOffset;
        var properties = propertyParse?.Properties ?? [];
        SkeletalMeshNativeLayoutEvidence? nativeLayout = null;
        if (propertyParse is null)
        {
            issues.Add(new SkeletalMeshReconnaissanceIssue(
                "Error", "SKM100",
                "Arkive could not prove the UObject property-stream terminator; native-region splitting is refused."));
        }
        else if (propertyParse.StartOffset != 0)
        {
            issues.Add(new SkeletalMeshReconnaissanceIssue(
                "Information", "SKM101",
                $"The verified property stream begins at +0x{propertyParse.StartOffset:X}; its prefix remains opaque and preserved."));
        }

        if (nativeStart is not null)
        {
            var hasVertexColors = properties.Any(property =>
                property.Name.Equals("bHasVertexColors", StringComparison.OrdinalIgnoreCase) &&
                property.ValuePreview.Equals("True", StringComparison.OrdinalIgnoreCase));
            var parser = new SkeletalMeshNativeLayoutParser();
            if (!parser.TryParse(bytes, request.SerialOffset, checked((int)nativeStart.Value),
                    catalog.Names, hasVertexColors, out nativeLayout, out var refusal))
            {
                issues.Add(new SkeletalMeshReconnaissanceIssue(
                    "Error", "SKM210", $"Native SkeletalMesh layout was not proven: {refusal}"));
            }
        }

        var exportHash = Sha256(bytes);
        string? nativeHash = null;
        long? nativeSize = null;
        if (nativeStart is not null)
        {
            nativeSize = bytes.LongLength - nativeStart.Value;
            nativeHash = Sha256(bytes.AsSpan(checked((int)nativeStart.Value)));
        }

        var packageHash = string.IsNullOrWhiteSpace(request.KnownPackageSha256)
            ? ComputeFileHash(packagePath, cancellationToken)
            : NormalizeSha256(request.KnownPackageSha256);
        var current = new FileInfo(packagePath);
        if (!current.Exists || current.Length != initialLength || current.LastWriteTimeUtc.Ticks != initialWriteTicks)
        {
            throw new IOException("The source package changed during SkeletalMesh reconnaissance; the report was discarded.");
        }

        var readiness = nativeExportIndex is not null && nativeStart is not null && nativeLayout is not null &&
                        request.Kind == AssetKind.SkeletalMesh &&
                        request.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase)
            ? SkeletalMeshReadiness.NoOpReady
            : issues.Any(issue => issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
                ? SkeletalMeshReadiness.Refused
                : SkeletalMeshReadiness.EvidenceOnly;

        var report = new SkeletalMeshReconnaissanceReport(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            packagePath,
            file.Name,
            request.PackageAlias,
            initialLength,
            initialWriteTicks,
            packageHash,
            new SkeletalMeshPackageProfile(
                summary.FileVersion, summary.LicenseeVersion, summary.EngineVersion,
                summary.CookerVersion, summary.PackageFlags, summary.CompressionFlags,
                summary.PackageGuid, summary.CompressedChunks.Count, logical.Length),
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
            nativeSize,
            nativeHash,
            properties,
            null,
            nativeLayout,
            BuildStorageEvidence(summary, request.SerialOffset, request.SerialSize),
            readiness,
            issues,
            TimeSpan.Zero);
        var dependencies = new SkeletalMeshDeformationDependencyService(
            _catalogReader, _protectionProvider).Analyze(report, cancellationToken);
        foreach (var dependencyIssue in dependencies.Issues)
            issues.Add(new SkeletalMeshReconnaissanceIssue(
                dependencyIssue.Severity, dependencyIssue.Code, dependencyIssue.Message));
        return report with
        {
            DeformationDependencies = dependencies,
            Issues = issues,
            Readiness = issues.Any(issue => issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
                ? SkeletalMeshReadiness.Refused
                : report.Readiness,
        };
    }

    public static SkeletalMeshOracleEvidence ParseSkeletalMeshDump(
        string rawOutput,
        TimeSpan duration,
        string fallbackClassName = "SkeletalMesh")
    {
        ArgumentNullException.ThrowIfNull(rawOutput);
        var lines = rawOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var className = MatchValue(lines, @"^ClassName:\s*(?<v>\S+)") ?? fallbackClassName;
        var lodCount = MatchInt(lines, @"^LodCount\s*=\s*(?<v>\d+)");
        var boneCount = MatchInt(lines, @"^BoneCount\s*=\s*(?<v>\d+)");
        var socketCount = MatchInt(lines, @"^SocketCount\s*=\s*(?<v>\d+)");
        bool? vertexColors = MatchValue(lines, @"^bHasVertexColors\s*=\s*(?<v>true|false)") is { } colorText
            ? bool.Parse(colorText)
            : null;
        var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lods = new List<SkeletalMeshLodEvidence>();
        LodBuilder? lod = null;
        SectionBuilder? section = null;
        foreach (var sourceLine in lines)
        {
            var line = sourceLine.Trim();
            var lodMatch = Regex.Match(line, @"^Lods\[(?<v>\d+)\]\s*=$", RegexOptions.CultureInvariant);
            if (lodMatch.Success)
            {
                CommitSection();
                CommitLod();
                lod = new LodBuilder(int.Parse(lodMatch.Groups["v"].Value, CultureInfo.InvariantCulture));
                continue;
            }

            if (lod is null)
            {
                continue;
            }
            var sectionMatch = Regex.Match(line, @"^Sections\[(?<v>\d+)\]\s*=$", RegexOptions.CultureInvariant);
            if (sectionMatch.Success)
            {
                CommitSection();
                section = new SectionBuilder(int.Parse(sectionMatch.Groups["v"].Value, CultureInfo.InvariantCulture));
                continue;
            }

            if (TryAssignment(line, "Material", out var material))
            {
                materials.Add(material);
                if (section is not null) section.Material = material;
            }
            else if (TryAssignmentInt(line, "FirstIndex", out var firstIndex) && section is not null) section.FirstIndex = firstIndex;
            else if (TryAssignmentInt(line, "NumFaces", out var faces) && section is not null) section.TriangleCount = faces;
            else if (TryAssignmentInt(line, "NumVerts", out var vertices)) lod.VertexCount = vertices;
            else if (TryAssignmentInt(line, "IndexCount", out var indices)) lod.IndexCount = indices;
            else if (TryAssignmentInt(line, "NumTexCoords", out var uvCount)) lod.TextureCoordinateCount = uvCount;
        }
        CommitSection();
        CommitLod();

        return new SkeletalMeshOracleEvidence(
            "UModel Lost Ark fork -dump",
            className,
            lodCount,
            boneCount,
            socketCount,
            vertexColors,
            materials.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            lods,
            Sha256(Encoding.UTF8.GetBytes(rawOutput)),
            duration.TotalSeconds);

        void CommitSection()
        {
            if (lod is not null && section is not null &&
                (section.TriangleCount > 0 || section.FirstIndex > 0 || section.Material != "unresolved"))
            {
                lod.Sections.Add(new SkeletalMeshSectionEvidence(
                    section.Index, section.Material, section.FirstIndex, section.TriangleCount));
            }
            section = null;
        }

        void CommitLod()
        {
            if (lod is not null &&
                (lod.VertexCount > 0 || lod.IndexCount > 0 || lod.Sections.Count > 0))
            {
                lods.Add(new SkeletalMeshLodEvidence(
                    lod.Index, lod.VertexCount, lod.IndexCount,
                    lod.Sections.Sum(item => item.TriangleCount),
                    lod.TextureCoordinateCount, lod.Sections));
            }
            lod = null;
        }
    }

    private static string? MatchValue(IEnumerable<string> lines, string pattern)
    {
        foreach (var line in lines)
        {
            var match = Regex.Match(line.Trim(), pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success) return match.Groups["v"].Value;
        }
        return null;
    }

    private static int MatchInt(IEnumerable<string> lines, string pattern) =>
        int.TryParse(MatchValue(lines, pattern), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static bool TryAssignment(string line, string name, out string value)
    {
        value = string.Empty;
        if (!line.StartsWith(name + " =", StringComparison.OrdinalIgnoreCase)) return false;
        value = line[(line.IndexOf('=') + 1)..].Trim();
        return true;
    }

    private static bool TryAssignmentInt(string line, string name, out int value)
    {
        value = 0;
        return TryAssignment(line, name, out var text) &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static PropertyParseResult? FindPropertyStream(
        ReadOnlySpan<byte> bytes, long serialOffset, IReadOnlyList<UnrealNameEntry> names)
    {
        PropertyParseResult? best = null;
        // State frames and Lost Ark class-specific prefixes are not guaranteed to
        // fit inside Texture2D's small prefix window. Scan a bounded 64 KiB and
        // require a SkeletalMesh root-property signature to avoid accepting an
        // embedded LODInfo property stream.
        var maximumStart = Math.Min(64 * 1024, Math.Max(0, bytes.Length - 8));
        for (var start = 0; start <= maximumStart; start += 4)
        {
            foreach (var extendedIntTag in new[] { false, true })
            {
                if (!TryParsePropertyStream(bytes, serialOffset, names, start, extendedIntTag, out var candidate)) continue;
                if (RootPropertyScore(candidate) == 0) continue;
                if (best is null || RootPropertyScore(candidate) > RootPropertyScore(best) ||
                    (RootPropertyScore(candidate) == RootPropertyScore(best) &&
                     candidate.StartOffset < best.StartOffset))
                {
                    best = candidate;
                }
            }
        }
        return best;
    }

    private static int RootPropertyScore(PropertyParseResult candidate)
    {
        var score = 0;
        foreach (var property in candidate.Properties)
        {
            if (property.Name.Equals("lodinfo", StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (property.Name.Equals("clothingassets", StringComparison.OrdinalIgnoreCase)) score += 40;
            else if (property.Name.Equals("sockets", StringComparison.OrdinalIgnoreCase)) score += 30;
            else if (property.Name.Equals("bhasbeensimplified", StringComparison.OrdinalIgnoreCase)) score += 20;
            else if (property.Name.Equals("bhasvertexcolors", StringComparison.OrdinalIgnoreCase)) score += 20;
            else if (property.Name.Equals("clothtearfactor", StringComparison.OrdinalIgnoreCase)) score += 10;
            else if (property.Name.Equals("floorconformtype", StringComparison.OrdinalIgnoreCase)) score += 10;
        }
        return score;
    }

    private static bool TryParsePropertyStream(
        ReadOnlySpan<byte> bytes, long serialOffset, IReadOnlyList<UnrealNameEntry> names,
        int start, bool extendedIntTag, out PropertyParseResult result)
    {
        result = null!;
        var cursor = start;
        var properties = new List<SkeletalMeshPropertyEvidence>();
        for (var propertyIndex = 0; propertyIndex < MaximumPropertyCount; propertyIndex++)
        {
            var tagStart = cursor;
            if (!TryReadName(bytes, ref cursor, names, out var propertyName)) return false;
            if (propertyName.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                result = new PropertyParseResult(start, cursor, properties);
                return properties.Count > 0;
            }
            if (!TryReadName(bytes, ref cursor, names, out var propertyType) || cursor > bytes.Length - 8 ||
                !propertyType.EndsWith("Property", StringComparison.OrdinalIgnoreCase)) return false;
            var size = ReadInt32(bytes, cursor);
            var arrayIndex = ReadInt32(bytes, cursor + 4);
            cursor += 8;
            if (size < 0 || arrayIndex < 0) return false;

            string? metadata = null;
            byte? taggedBool = null;
            if (propertyType.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) ||
                propertyType.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadName(bytes, ref cursor, names, out metadata)) return false;
            }
            else if (extendedIntTag && propertyType.Equals("IntProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadName(bytes, ref cursor, names, out var marker)) return false;
                metadata = $"Lost Ark tag marker: {marker}";
            }
            else if (propertyType.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (cursor >= bytes.Length) return false;
                taggedBool = bytes[cursor++];
            }

            if (size > bytes.Length - cursor) return false;
            var value = bytes.Slice(cursor, size);
            properties.Add(new SkeletalMeshPropertyEvidence(
                tagStart, serialOffset + tagStart, propertyName, propertyType, size, arrayIndex,
                metadata, cursor, PreviewValue(propertyType, value, taggedBool, names),
                Convert.ToHexString(value[..Math.Min(16, value.Length)])));
            cursor += size;
        }
        return false;
    }

    private static string PreviewValue(
        string type, ReadOnlySpan<byte> value, byte? taggedBool, IReadOnlyList<UnrealNameEntry> names)
    {
        if (type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase) && taggedBool is not null)
            return taggedBool == 0 ? "False" : "True";
        if ((type.Equals("IntProperty", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("ObjectProperty", StringComparison.OrdinalIgnoreCase)) && value.Length >= 4)
            return ReadInt32(value, 0).ToString(CultureInfo.InvariantCulture);
        if (type.Equals("FloatProperty", StringComparison.OrdinalIgnoreCase) && value.Length >= 4)
            return BitConverter.Int32BitsToSingle(ReadInt32(value, 0)).ToString("R", CultureInfo.InvariantCulture);
        if (type.Equals("NameProperty", StringComparison.OrdinalIgnoreCase) && value.Length >= 8)
        {
            var cursor = 0;
            if (TryReadName(value, ref cursor, names, out var name)) return name;
        }
        return value.IsEmpty ? "(empty)" : Convert.ToHexString(value[..Math.Min(16, value.Length)]);
    }

    private static bool TryReadName(
        ReadOnlySpan<byte> bytes, ref int cursor, IReadOnlyList<UnrealNameEntry> names, out string value)
    {
        value = string.Empty;
        if (cursor < 0 || cursor > bytes.Length - 8) return false;
        var index = ReadInt32(bytes, cursor);
        var number = ReadInt32(bytes, cursor + 4);
        cursor += 8;
        if (index < 0 || index >= names.Count || number < 0) return false;
        value = new UnrealNameReference(index, number).Resolve(names);
        return true;
    }

    private static IReadOnlyList<SkeletalMeshStorageEvidence> BuildStorageEvidence(
        PackageSummary summary, long logicalOffset, long logicalSize)
    {
        if (summary.CompressedChunks.Count == 0)
            return [new("Raw physical bytes", null, logicalOffset, logicalSize, logicalOffset, logicalSize, null)];
        var end = checked(logicalOffset + logicalSize);
        var cursor = logicalOffset;
        var result = new List<SkeletalMeshStorageEvidence>();
        for (var index = 0; index < summary.CompressedChunks.Count && cursor < end; index++)
        {
            var chunk = summary.CompressedChunks[index];
            var chunkStart = (long)chunk.UncompressedOffset;
            var chunkEnd = checked(chunkStart + chunk.UncompressedSize);
            if (chunkEnd <= cursor) continue;
            if (cursor < chunkStart)
            {
                var rawEnd = Math.Min(end, chunkStart);
                result.Add(new("Raw physical bytes", null, cursor, rawEnd - cursor, cursor, rawEnd - cursor, null));
                cursor = rawEnd;
            }
            if (cursor < chunkEnd && end > chunkStart)
            {
                var overlapEnd = Math.Min(end, chunkEnd);
                result.Add(new("Compressed package chunk", index + 1, cursor, overlapEnd - cursor,
                    chunk.CompressedOffset, chunk.CompressedSize, chunk.LostArkFlags));
                cursor = overlapEnd;
            }
        }
        if (cursor < end) result.Add(new("Raw physical bytes", null, cursor, end - cursor, cursor, end - cursor, null));
        return result;
    }

    private static string ComputeFileHash(string path, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
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
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("The supplied package SHA-256 is invalid.");
        return normalized;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);

    private sealed record PropertyParseResult(
        int StartOffset, int EndOffset, IReadOnlyList<SkeletalMeshPropertyEvidence> Properties);
    private sealed class LodBuilder(int index)
    {
        public int Index { get; } = index;
        public int VertexCount { get; set; }
        public int IndexCount { get; set; }
        public int TextureCoordinateCount { get; set; }
        public List<SkeletalMeshSectionEvidence> Sections { get; } = [];
    }
    private sealed class SectionBuilder(int index)
    {
        public int Index { get; } = index;
        public string Material { get; set; } = "unresolved";
        public int FirstIndex { get; set; }
        public int TriangleCount { get; set; }
    }
}
