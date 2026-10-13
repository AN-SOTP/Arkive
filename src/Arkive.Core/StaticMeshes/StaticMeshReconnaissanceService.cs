using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.External;
using Arkive.Core.Inspection;
using Arkive.Core.Packages;

namespace Arkive.Core.StaticMeshes;

public sealed class StaticMeshReconnaissanceService
{
    public const int CurrentSchemaVersion = 1;
    private const int MaximumExportBytes = 768 * 1024 * 1024;

    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly UmodelBridge? _umodelBridge;

    public StaticMeshReconnaissanceService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null,
        UmodelBridge? umodelBridge = null)
    {
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(summaryReader, _protectionProvider);
        _umodelBridge = umodelBridge;
    }

    public async Task<StaticMeshReconnaissanceReport> InspectAsync(
        StaticMeshReconnaissanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var report = await Task.Run(() => InspectNative(request, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        var issues = report.Issues.ToList();
        StaticMeshOracleEvidence? oracle = null;
        if (request.SkipUmodelOracle)
        {
            issues.Add(new SkeletalMeshReconnaissanceIssue(
                "Information", "STM300",
                "The UModel oracle was intentionally deferred; final package validation is still required."));
        }
        else if (_umodelBridge is null)
        {
            issues.Add(new SkeletalMeshReconnaissanceIssue(
                "Information", "STM301", "UModel is unavailable, so the independent StaticMesh oracle was skipped."));
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
                oracle = ParseDump(dump.RawOutput, dump.Duration, dump.Asset.ClassName);
                ValidateAgainstOracle(report.NativeLayout, oracle, issues);
            }
            catch (Exception exception) when (exception is UmodelBridgeException or IOException)
            {
                issues.Add(new SkeletalMeshReconnaissanceIssue(
                    "Warning", "STM302", $"The independent UModel StaticMesh probe failed: {exception.Message}"));
            }
        }

        watch.Stop();
        var readiness = issues.Any(issue => issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
            ? StaticMeshReadiness.Refused
            : report.NativeLayout is null
                ? StaticMeshReadiness.EvidenceOnly
                : StaticMeshReadiness.Editable;
        return report with
        {
            Oracle = oracle,
            Issues = issues,
            Readiness = readiness,
            Duration = watch.Elapsed,
        };
    }

    private StaticMeshReconnaissanceReport InspectNative(
        StaticMeshReconnaissanceRequest request,
        CancellationToken cancellationToken)
    {
        var packagePath = Path.GetFullPath(request.PackagePath);
        var file = new FileInfo(packagePath);
        if (!file.Exists) throw new FileNotFoundException("The StaticMesh source package was not found.", packagePath);
        if (request.SerialOffset < 0 || request.SerialSize <= 0 || request.SerialSize > MaximumExportBytes)
            throw new PackageFormatException("StaticMesh reconnaissance requires a positive, bounded export range.");

        var initialLength = file.Length;
        var initialWriteTicks = file.LastWriteTimeUtc.Ticks;
        var catalog = _catalogReader.Read(packagePath);
        var summary = catalog.Summary;
        using var logical = new LogicalPackageStream(summary, _protectionProvider);
        if (request.SerialOffset > logical.Length - request.SerialSize)
            throw new PackageFormatException("The StaticMesh export range is outside the logical package.");
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
            issues.Add(new("Error", "STM004", "The selected object/range did not resolve uniquely to the export table."));
        if (request.Kind != AssetKind.StaticMesh ||
            !request.ClassName.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase))
            issues.Add(new("Error", "STM001", $"The selected row is {request.Kind}/{request.ClassName}, not StaticMesh."));
        if (summary.FileVersion != 868 || summary.LicenseeVersion != 16)
            issues.Add(new("Error", "STM002",
                $"StaticMesh editing currently requires Western package version 868/16; this package is {summary.FileVersion}/{summary.LicenseeVersion}."));

        var parser = new StaticMeshNativeLayoutParser();
        Ue3PropertyStreamCandidate? property = null;
        StaticMeshNativeLayoutEvidence? native = null;
        var failures = new List<string>();
        foreach (var candidate in Ue3PropertyStreamProbe.FindCandidates(
                     bytes, request.SerialOffset, catalog.Names))
        {
            if (!parser.TryParse(bytes, request.SerialOffset, candidate.EndOffset, out var parsed, out var refusal))
            {
                if (failures.Count < 6) failures.Add($"+0x{candidate.EndOffset:X}: {refusal}");
                continue;
            }
            property = candidate;
            native = parsed;
            break;
        }
        if (property is null || native is null)
        {
            issues.Add(new("Error", "STM210",
                "Arkive could not prove the StaticMesh3 native boundary/layout. " +
                (failures.Count == 0 ? "No tagged-property candidate was found." : string.Join(" | ", failures))));
        }
        else if (native.InternalVersion != 18)
        {
            issues.Add(new("Warning", "STM211",
                $"The native StaticMesh internal version is {native.InternalVersion}; version 18 is the validated client profile."));
        }

        var packageHash = string.IsNullOrWhiteSpace(request.KnownPackageSha256)
            ? ComputeFileHash(packagePath, cancellationToken)
            : NormalizeSha256(request.KnownPackageSha256);
        var current = new FileInfo(packagePath);
        if (!current.Exists || current.Length != initialLength || current.LastWriteTimeUtc.Ticks != initialWriteTicks)
            throw new IOException("The source package changed during StaticMesh reconnaissance; the report was discarded.");

        return new StaticMeshReconnaissanceReport(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            packagePath,
            Path.GetFileName(packagePath),
            request.PackageAlias,
            initialLength,
            initialWriteTicks,
            packageHash,
            new SkeletalMeshPackageProfile(
                summary.FileVersion,
                summary.LicenseeVersion,
                summary.EngineVersion,
                summary.CookerVersion,
                summary.PackageFlags,
                summary.CompressionFlags,
                summary.PackageGuid,
                summary.CompressedChunks.Count,
                logical.Length),
            _protectionProvider.Name,
            request.ExportIndex,
            nativeExportIndex,
            request.Kind,
            request.ClassName,
            request.ObjectName,
            request.SerialOffset,
            request.SerialSize,
            Sha(bytes),
            property?.StartOffset,
            property?.EndOffset,
            property?.Properties ?? [],
            null,
            native,
            BuildStorageEvidence(summary, request.SerialOffset, request.SerialSize),
            native is not null && nativeExportIndex is not null
                ? StaticMeshReadiness.Editable
                : StaticMeshReadiness.Refused,
            issues,
            TimeSpan.Zero);
    }

    public static StaticMeshOracleEvidence ParseDump(
        string rawOutput,
        TimeSpan duration,
        string fallbackClassName = "StaticMesh3")
    {
        ArgumentNullException.ThrowIfNull(rawOutput);
        var lines = SkeletalMeshReconnaissanceService.SplitDumpLines(rawOutput);
        var className = MatchValue(lines, @"^ClassName:\s*(?<v>\S+)") ?? fallbackClassName;
        var lodCount = MatchInt(lines, @"^LodCount\s*=\s*(?<v>\d+)");
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
            if (lod is null) continue;
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
            else if (TryAssignmentInt(line, "FirstIndex", out var first) && section is not null) section.FirstIndex = first;
            else if (TryAssignmentInt(line, "NumFaces", out var faces) && section is not null) section.TriangleCount = faces;
            else if (TryAssignmentInt(line, "NumVerts", out var vertices)) lod.VertexCount = vertices;
            else if (TryAssignmentInt(line, "IndexCount", out var indices)) lod.IndexCount = indices;
            else if (TryAssignmentInt(line, "NumTexCoords", out var uvCount)) lod.TextureCoordinateCount = uvCount;
        }
        CommitSection();
        CommitLod();
        return new StaticMeshOracleEvidence(
            "UModel Lost Ark fork -dump",
            className,
            lodCount,
            materials.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            lods,
            Sha(Encoding.UTF8.GetBytes(rawOutput)),
            duration.TotalSeconds);

        void CommitSection()
        {
            if (lod is not null && section is not null &&
                (section.TriangleCount > 0 || section.FirstIndex > 0 ||
                 !section.Material.Equals("unresolved", StringComparison.OrdinalIgnoreCase)))
                lod.Sections.Add(new SkeletalMeshSectionEvidence(
                    section.Index, section.Material, section.FirstIndex, section.TriangleCount));
            section = null;
        }

        void CommitLod()
        {
            if (lod is not null && (lod.VertexCount > 0 || lod.IndexCount > 0 || lod.Sections.Count > 0))
                lods.Add(new SkeletalMeshLodEvidence(
                    lod.Index,
                    lod.VertexCount,
                    lod.IndexCount,
                    lod.Sections.Sum(item => item.TriangleCount),
                    lod.TextureCoordinateCount,
                    lod.Sections));
            lod = null;
        }
    }

    private static void ValidateAgainstOracle(
        StaticMeshNativeLayoutEvidence? native,
        StaticMeshOracleEvidence oracle,
        ICollection<SkeletalMeshReconnaissanceIssue> issues)
    {
        if (native is null) return;
        if (!oracle.ReportedClassName.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase))
            Add("STM310", $"UModel reported {oracle.ReportedClassName}, not StaticMesh.");
        if (native.Lods.Count != oracle.LodCount || native.Lods.Count != oracle.Lods.Count)
            Add("STM311", $"LOD count differs: native={native.Lods.Count}, UModel={oracle.LodCount}/{oracle.Lods.Count}.");
        foreach (var lod in native.Lods)
        {
            var other = oracle.Lods.SingleOrDefault(item => item.LodIndex == lod.LodIndex);
            if (other is null)
            {
                Add("STM312", $"UModel did not report LOD {lod.LodIndex}.");
                continue;
            }
            if (lod.VertexCount != other.VertexCount) Add("STM313", $"LOD {lod.LodIndex} vertex count differs: {lod.VertexCount}/{other.VertexCount}.");
            if (lod.Indices.ElementCount != other.IndexCount) Add("STM314", $"LOD {lod.LodIndex} index count differs: {lod.Indices.ElementCount}/{other.IndexCount}.");
            if (lod.TextureCoordinateCount != other.TextureCoordinateCount) Add("STM315", $"LOD {lod.LodIndex} UV count differs: {lod.TextureCoordinateCount}/{other.TextureCoordinateCount}.");
            if (lod.Sections.Count != other.Sections.Count) Add("STM316", $"LOD {lod.LodIndex} section count differs: {lod.Sections.Count}/{other.Sections.Count}.");
        }
        return;

        void Add(string code, string message) => issues.Add(new(
            "Error", code, "Native parser/UModel StaticMesh cross-check failed. " + message));
    }

    private static IReadOnlyList<SkeletalMeshStorageEvidence> BuildStorageEvidence(
        PackageSummary summary,
        long logicalOffset,
        long logicalSize)
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
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("The supplied package SHA-256 is invalid.");
        return normalized;
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
        int.TryParse(MatchValue(lines, pattern), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

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

    private static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

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
