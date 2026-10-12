using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkive.Core.Inspection;

public sealed class SkeletalMeshReconnaissanceWorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public SkeletalMeshReconnaissanceWorkspaceStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public async Task<SkeletalMeshReconnaissanceEntry> SaveAsync(
        SkeletalMeshReconnaissanceReport report,
        CancellationToken cancellationToken = default)
    {
        var id = $"{report.CreatedAt:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var directory = Path.Combine(RootDirectory, Safe(report.PackageAlias), $"{Safe(report.ObjectName)}-{id}");
        Directory.CreateDirectory(directory);
        var jsonPath = Path.Combine(directory, "arkive-skeletalmesh-recon.json");
        var textPath = Path.Combine(directory, "arkive-skeletalmesh-recon.txt");
        var oraclePath = report.Oracle is null ? null : Path.Combine(directory, "umodel-semantic-evidence.txt");
        await WriteJsonAtomicAsync(jsonPath, report, cancellationToken).ConfigureAwait(false);
        await WriteTextAtomicAsync(textPath, Render(report), cancellationToken).ConfigureAwait(false);
        if (oraclePath is not null)
        {
            var oracle = report.Oracle!;
            var content = $"Provider: {oracle.Provider}{Environment.NewLine}" +
                          $"Raw output SHA-256: {oracle.RawOutputSha256}{Environment.NewLine}" +
                          "The complete raw output hash is retained in the JSON report; this normalized file contains parsed evidence." +
                          Environment.NewLine + Environment.NewLine + RenderOracle(oracle);
            await WriteTextAtomicAsync(oraclePath, content, cancellationToken).ConfigureAwait(false);
        }
        return new(directory, jsonPath, textPath, oraclePath, report);
    }

    private static string Render(SkeletalMeshReconnaissanceReport report)
    {
        var b = new StringBuilder();
        b.AppendLine("Arkive native SkeletalMesh reconnaissance");
        b.AppendLine($"Created: {report.CreatedAt:O}");
        b.AppendLine($"Package: {report.PackagePath}");
        b.AppendLine($"Package SHA-256: {report.PackageSha256}");
        b.AppendLine($"Object: {report.ObjectName} ({report.ClassName})");
        b.AppendLine($"Selection/native export: {report.ExportIndex}/{report.NativeExportIndex?.ToString() ?? "unresolved"}");
        b.AppendLine($"Logical export: 0x{report.SerialOffset:X}+0x{report.SerialSize:X}");
        b.AppendLine($"Export SHA-256: {report.ExportPayloadSha256}");
        b.AppendLine($"Property stream: {(report.PropertyStreamRelativeOffset is null ? "unresolved" : $"+0x{report.PropertyStreamRelativeOffset:X}")}");
        b.AppendLine($"Native data: {(report.NativeDataRelativeOffset is null ? "unresolved" : $"+0x{report.NativeDataRelativeOffset:X}+0x{report.NativeDataSize:X}")}");
        b.AppendLine($"Native SHA-256: {report.NativeDataSha256 ?? "unresolved"}");
        b.AppendLine($"Readiness: {report.Readiness}");
        b.AppendLine($"Provider: {report.ProtectionProviderName}");
        if (report.DeformationDependencies is { } dependencies)
        {
            b.AppendLine($"Cloth dependencies: {dependencies.ClothState}; active refs={dependencies.ActiveClothReferenceCount}");
            b.AppendLine($"Morph dependencies: {dependencies.MorphState}; sets={dependencies.MorphTargetSetCount}; targets={dependencies.MorphTargetCount}; deltas={dependencies.MorphDeltaCount:N0}");
            b.AppendLine($"Topology-preserving support: {dependencies.TopologyPreservingSupported}");
            b.AppendLine($"Topology-changing support: {dependencies.TopologyChangingSupported} ({dependencies.TopologyChangingReason})");
        }
        b.AppendLine();
        b.AppendLine($"Properties ({report.Properties.Count})");
        foreach (var property in report.Properties)
            b.AppendLine($"  +0x{property.RelativeOffset:X} {property.Name} : {property.Type} size={property.Size} value={property.ValuePreview}");
        b.AppendLine();
        if (report.NativeLayout is { } native)
        {
            b.AppendLine($"Native layout: {native.ParserProfile}; bones={native.BoneCount}; LODs={native.Lods.Count}; ranges={native.Ranges.Count}; parsedToEnd={native.ParsedToEnd}");
            b.AppendLine($"Lost Ark extension: version={native.LostArkExtensionVersion}; values=[{string.Join(", ", native.LostArkExtensionValues)}]");
            foreach (var lod in native.Lods)
            {
                b.AppendLine(
                    $"  LOD {lod.LodIndex}: verts={lod.VertexCount}, indices={lod.IndexBuffer.ElementCount}, " +
                    $"triangles={lod.Sections.Sum(section => section.TriangleCount)}, UVs={lod.TextureCoordinateCount}, " +
                    $"sections={lod.Sections.Count}, chunks={lod.Chunks.Count}, stride={lod.VertexBuffer.ElementSize}, " +
                    $"position=+{lod.VertexPositionOffset}, requiredBoneWidth={lod.RequiredBoneElementSize}");
                b.AppendLine(
                    $"    index +0x{lod.IndexBuffer.DataRelativeOffset:X}+0x{lod.IndexBuffer.DataBytes:X}; " +
                    $"vertices +0x{lod.VertexBuffer.DataRelativeOffset:X}+0x{lod.VertexBuffer.DataBytes:X}; " +
                    $"colors {(lod.VertexColorBuffer is null ? "absent" : $"+0x{lod.VertexColorBuffer.DataRelativeOffset:X}+0x{lod.VertexColorBuffer.DataBytes:X}")}");
            }
            b.AppendLine("Native ranges");
            foreach (var range in native.Ranges)
                b.AppendLine($"  +0x{range.RelativeOffset:X}+0x{range.Size:X} parsed={range.IsParsed} editableCandidate={range.IsEditableCandidate} {range.Name} sha256={range.Sha256}");
        }
        else b.AppendLine("Native layout: unresolved");
        b.AppendLine();
        if (report.Oracle is not null) b.Append(RenderOracle(report.Oracle));
        else b.AppendLine("Independent semantic oracle: unavailable");
        b.AppendLine();
        b.AppendLine($"Issues ({report.Issues.Count})");
        foreach (var issue in report.Issues) b.AppendLine($"  [{issue.Severity}] {issue.Code}: {issue.Message}");
        return b.ToString();
    }

    private static string RenderOracle(SkeletalMeshOracleEvidence oracle)
    {
        var b = new StringBuilder();
        b.AppendLine($"Semantic oracle: {oracle.Provider}");
        b.AppendLine($"Class: {oracle.ReportedClassName}; LODs: {oracle.LodCount}; bones: {oracle.BoneCount}; sockets: {oracle.SocketCount}; vertex colors: {oracle.HasVertexColors?.ToString() ?? "unresolved"}");
        b.AppendLine($"Materials ({oracle.Materials.Count}): {string.Join(", ", oracle.Materials)}");
        foreach (var lod in oracle.Lods)
        {
            b.AppendLine($"  LOD {lod.LodIndex}: verts={lod.VertexCount}, indices={lod.IndexCount}, triangles={lod.TriangleCount}, UVs={lod.TextureCoordinateCount}, sections={lod.Sections.Count}");
            foreach (var section in lod.Sections)
                b.AppendLine($"    section {section.SectionIndex}: firstIndex={section.FirstIndex}, triangles={section.TriangleCount}, material={section.Material}");
        }
        return b.ToString();
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WriteTextAtomicAsync(string path, string value, CancellationToken token)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, value,
                new UTF8Encoding(false), token).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(result)) result = "unnamed";
        return result.Length <= 80 ? result : result[..80];
    }
}
