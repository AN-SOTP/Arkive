using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Inspection;
using Arkive.Core.Packages;

namespace Arkive.Core.StaticMeshes;

public sealed class StaticMeshEditableExportService
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "arkive-editable-staticmesh.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly ExportArtifactInspector _artifactInspector = new();

    public StaticMeshEditableExportService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(summaryReader, _protectionProvider);
    }

    public async Task<StaticMeshEditableExportResult> ExportAsync(
        StaticMeshEditableExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var report = request.Reconnaissance;
        ValidateReport(report);
        var packagePath = Path.GetFullPath(report.PackagePath);
        var file = new FileInfo(packagePath);
        if (!file.Exists) throw new FileNotFoundException("The StaticMesh source package was not found.", packagePath);
        if (file.Length != report.PackageLength || file.LastWriteTimeUtc.Ticks != report.PackageLastWriteUtcTicks ||
            !ComputeFileSha256(packagePath, cancellationToken).Equals(
                report.PackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("STM500", "The source package identity changed; rerun Static mesh recon.");

        var catalog = _catalogReader.Read(packagePath);
        var asset = catalog.Assets.SingleOrDefault(item => item.ExportIndex == report.NativeExportIndex);
        if (asset is null || asset.SerialOffset != report.SerialOffset || asset.SerialSize != report.SerialSize ||
            !asset.ObjectName.Equals(report.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            !asset.ClassName.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase))
            Refuse("STM501", "The export table no longer matches StaticMesh reconnaissance.");
        using var logical = new LogicalPackageStream(catalog.Summary, _protectionProvider);
        var exportBytes = new byte[checked((int)report.SerialSize)];
        logical.Position = report.SerialOffset;
        logical.ReadExactly(exportBytes);
        if (!StaticMeshSemanticDecoder.Sha(exportBytes).Equals(
                report.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("STM502", "The source StaticMesh export changed; rerun reconnaissance.");

        var decoded = new StaticMeshSemanticDecoder().Decode(exportBytes, report.NativeLayout!);
        var build = new ArkiveStaticMeshGlbWriter().Build(report, decoded);
        Directory.CreateDirectory(request.OutputDirectory);
        var glbPath = Path.Combine(request.OutputDirectory, Safe(report.ObjectName) + ".arkive-static-edit.glb");
        var manifestPath = Path.Combine(request.OutputDirectory, ManifestFileName);
        WriteBytesAtomic(glbPath, build.Bytes);
        ValidateStableGlb(glbPath, build.Lods);
        var inspection = await _artifactInspector.InspectAsync(glbPath, cancellationToken).ConfigureAwait(false);
        if (inspection.ErrorCount != 0)
            Refuse("STM503", $"The exported GLB has {inspection.ErrorCount} structural error(s): " +
                string.Join(" | ", inspection.Issues
                    .Where(issue => issue.Severity == ArtifactInspectionSeverity.Error)
                    .Select(issue => $"{issue.Code}: {issue.Message}")));

        watch.Stop();
        var manifest = new StaticMeshEditableExportManifest(
            CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            packagePath,
            report.PhysicalPackageName,
            report.PackageAlias,
            report.PackageLength,
            report.PackageLastWriteUtcTicks,
            report.PackageSha256,
            report.NativeExportIndex!.Value,
            report.ClassName,
            report.ObjectName,
            report.SerialOffset,
            report.SerialSize,
            report.ExportPayloadSha256,
            Path.GetFullPath(request.ReconnaissanceJsonPath),
            report.NativeLayout!.ParserProfile,
            "Unreal centimetres (X,Y,Z) -> glTF metres (X,Z,Y)",
            "_ARKIVE_VERTEX_ID plus protected TEXCOORD sentinel",
            Path.GetFileName(glbPath),
            StaticMeshSemanticDecoder.Sha(build.Bytes),
            build.Bytes.LongLength,
            build.Lods,
            watch.Elapsed.TotalSeconds);
        WriteJsonAtomic(manifestPath, manifest);
        return new StaticMeshEditableExportResult(
            request.OutputDirectory,
            glbPath,
            manifestPath,
            manifest,
            watch.Elapsed);
    }

    public static async Task<StaticMeshEditableExportManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var manifest = await JsonSerializer.DeserializeAsync<StaticMeshEditableExportManifest>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new JsonException("The editable StaticMesh manifest was empty.");
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Editable StaticMesh schema {manifest.SchemaVersion} is unsupported.");
        return manifest;
    }

    private static void ValidateReport(StaticMeshReconnaissanceReport report)
    {
        if (report.SchemaVersion != StaticMeshReconnaissanceService.CurrentSchemaVersion ||
            report.Readiness != StaticMeshReadiness.Editable || report.NativeLayout is null ||
            report.NativeExportIndex is null || report.NativeLayout.Lods.Count == 0)
            Refuse("STM510", "The report does not prove an editable StaticMesh layout.");
        if (report.PackageProfile.FileVersion != PackageConstants.WesternFileVersion ||
            report.PackageProfile.LicenseeVersion != PackageConstants.WesternLicenseeVersion ||
            report.PackageProfile.CompressionFlags != PackageConstants.WesternCompressionFlags)
            Refuse("STM511", "Only the Western Lost Ark 868/16 package profile is supported.");
        if (report.NativeLayout!.Lods.Any(lod => lod.VertexCount > ushort.MaxValue))
            Refuse("STM512", "UE3 StaticMesh3 uses 16-bit render indices; a LOD exceeds 65,535 vertices.");
    }

    private static void ValidateStableGlb(
        string path,
        IReadOnlyList<StaticMeshEditableLodManifest> lods)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546C67 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != bytes.Length)
            Refuse("STM520", "The generated GLB header did not round-trip.");
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        if (20 + jsonLength + 8 > bytes.Length) Refuse("STM521", "The generated GLB JSON is truncated.");
        using var document = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var root = document.RootElement;
        var meshes = root.GetProperty("meshes");
        var accessors = root.GetProperty("accessors");
        if (meshes.GetArrayLength() != lods.Count)
            Refuse("STM522", "The generated GLB LOD count changed during reopen.");
        for (var lodIndex = 0; lodIndex < lods.Count; lodIndex++)
        {
            var primitives = meshes[lodIndex].GetProperty("primitives");
            if (primitives.GetArrayLength() != lods[lodIndex].SectionCount)
                Refuse("STM523", $"LOD {lodIndex} section count changed during GLB reopen.");
            long indices = 0;
            foreach (var primitive in primitives.EnumerateArray())
            {
                var attributes = primitive.GetProperty("attributes");
                foreach (var required in new[] { "POSITION", "NORMAL", "TANGENT", "_ARKIVE_VERTEX_ID" })
                    if (!attributes.TryGetProperty(required, out _))
                        Refuse("STM524", $"LOD {lodIndex} is missing {required}.");
                indices += accessors[primitive.GetProperty("indices").GetInt32()].GetProperty("count").GetInt64();
            }
            if (indices != lods[lodIndex].IndexCount)
                Refuse("STM525", $"LOD {lodIndex} index count changed during GLB reopen.");
        }
    }

    private static string ComputeFileSha256(string path, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void WriteBytesAtomic(string path, byte[] bytes)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "static-mesh" : result;
    }

    private static void Refuse(string code, string message) => throw new StaticMeshRefusalException(code, message);
}
