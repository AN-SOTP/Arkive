using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Inspection;
using Arkive.Core.Packages;

namespace Arkive.Core.Meshes;

public sealed class SkeletalMeshEditableExportService
{
    public const int CurrentSchemaVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly ExportArtifactInspector _artifactInspector = new();

    public SkeletalMeshEditableExportService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(summaryReader, _protectionProvider);
    }

    public async Task<SkeletalMeshEditableExportResult> ExportAsync(
        SkeletalMeshEditableExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var report = request.Reconnaissance;
        ValidateReport(report);
        var nativeLayout = report.NativeLayout!;
        var packagePath = Path.GetFullPath(report.PackagePath);
        var file = new FileInfo(packagePath);
        if (!file.Exists) throw new FileNotFoundException("The source package was not found.", packagePath);
        if (file.Length != report.PackageLength || file.LastWriteTimeUtc.Ticks != report.PackageLastWriteUtcTicks)
            Refuse("SME010", "The source package identity metadata changed; rerun Native mesh recon.");
        var packageHash = ComputeFileSha256(packagePath, cancellationToken);
        if (!packageHash.Equals(report.PackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SME011", "The source package SHA-256 changed; rerun Native mesh recon.");

        var catalog = _catalogReader.Read(packagePath);
        var asset = catalog.Assets.SingleOrDefault(item => item.ExportIndex == report.NativeExportIndex);
        if (asset is null || asset.SerialOffset != report.SerialOffset || asset.SerialSize != report.SerialSize ||
            !asset.ObjectName.Equals(report.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            !asset.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            Refuse("SME012", "The native export table no longer matches the reconnaissance report.");

        using var logical = new LogicalPackageStream(catalog.Summary, _protectionProvider);
        var exportBytes = new byte[checked((int)report.SerialSize)];
        logical.Position = report.SerialOffset;
        logical.ReadExactly(exportBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var exportHash = SkeletalMeshSemanticDecoder.Sha(exportBytes);
        if (!exportHash.Equals(report.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SME013", "The source SkeletalMesh export changed; rerun Native mesh recon.");

        var decoded = new SkeletalMeshSemanticDecoder().Decode(exportBytes, nativeLayout);
        var build = new ArkiveSkeletalMeshGlbWriter().Build(report, decoded);
        Directory.CreateDirectory(request.OutputDirectory);
        var safeName = Safe(report.ObjectName);
        var glbPath = Path.Combine(request.OutputDirectory, safeName + ".arkive-edit.glb");
        var manifestPath = Path.Combine(request.OutputDirectory, "arkive-editable-skeletalmesh.json");
        WriteBytesAtomic(glbPath, build.Bytes);
        ValidateStableGlb(glbPath, nativeLayout, build.Lods);
        var inspection = await _artifactInspector.InspectAsync(glbPath, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.ErrorCount != 0)
            Refuse("SME300", $"Arkive reopened the GLB with {inspection.ErrorCount} structural error(s): " +
                string.Join(" | ", inspection.Issues.Where(issue => issue.Severity == ArtifactInspectionSeverity.Error)
                    .Select(issue => $"{issue.Code}: {issue.Message}")));

        watch.Stop();
        var skeletonRange = nativeLayout.Ranges.SingleOrDefault(range =>
            range.Name.Equals("Reference skeleton", StringComparison.Ordinal));
        if (skeletonRange is null) Refuse("SME014", "The native reference-skeleton hash is unavailable.");
        var manifest = new SkeletalMeshEditableExportManifest(
            CurrentSchemaVersion, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            packagePath, report.PhysicalPackageName, report.PackageAlias,
            report.PackageLength, report.PackageLastWriteUtcTicks, report.PackageSha256,
            report.NativeExportIndex!.Value, report.ClassName, report.ObjectName,
            report.SerialOffset, report.SerialSize, report.ExportPayloadSha256,
            report.SchemaVersion, Path.GetFullPath(request.ReconnaissanceJsonPath),
            nativeLayout.ParserProfile, skeletonRange!.Sha256,
            nativeLayout.BoneCount, !nativeLayout.ParsedToEnd,
            "Unreal centimetres (X,Y,Z) -> glTF metres (X,Z,Y)",
            "_ARKIVE_VERTEX_ID", Path.GetFileName(glbPath),
            SkeletalMeshSemanticDecoder.Sha(build.Bytes), build.Bytes.LongLength,
            inspection.ErrorCount, inspection.WarningCount, build.Lods, watch.Elapsed.TotalSeconds);
        WriteJsonAtomic(manifestPath, manifest);
        return new SkeletalMeshEditableExportResult(
            request.OutputDirectory, glbPath, manifestPath, manifest, inspection, watch.Elapsed);
    }

    private static void ValidateReport(SkeletalMeshReconnaissanceReport report)
    {
        if (report.SchemaVersion != SkeletalMeshReconnaissanceService.CurrentSchemaVersion)
            Refuse("SME001", "The Native mesh recon schema is stale; run it again.");
        if (report.Readiness != SkeletalMeshReadiness.NoOpReady || report.NativeLayout is null ||
            report.NativeExportIndex is null)
            Refuse("SME002", "The report did not prove an exportable native SkeletalMesh layout.");
        if (report.PackageProfile.FileVersion != PackageConstants.WesternFileVersion ||
            report.PackageProfile.LicenseeVersion != PackageConstants.WesternLicenseeVersion ||
            report.PackageProfile.CompressionFlags != PackageConstants.WesternCompressionFlags)
            Refuse("SME003", "Only the Western Lost Ark 868/16 package profile is supported.");
        if (!report.NativeLayout!.AllRangesInBounds || report.NativeLayout.Lods.Count == 0 ||
            report.NativeLayout.Bones.Count == 0)
            Refuse("SME004", "The native LOD/skeleton range inventory is incomplete.");
    }

    private static void ValidateStableGlb(
        string path,
        SkeletalMeshNativeLayoutEvidence layout,
        IReadOnlyList<SkeletalMeshEditableLodManifest> manifests)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546C67 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != bytes.Length)
            Refuse("SME310", "The generated GLB header did not round-trip.");
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        if (20 + jsonLength + 8 > bytes.Length) Refuse("SME311", "The generated GLB JSON chunk is truncated.");
        using var document = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var root = document.RootElement;
        var accessors = root.GetProperty("accessors");
        var views = root.GetProperty("bufferViews");
        var meshes = root.GetProperty("meshes");
        var binHeader = 20 + jsonLength;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(binHeader + 4)) != 0x004E4942)
            Refuse("SME312", "The generated GLB BIN chunk is missing.");
        var binStart = binHeader + 8;
        if (meshes.GetArrayLength() != manifests.Count)
            Refuse("SME313", "The generated GLB LOD mesh count changed during reopen.");
        for (var lodIndex = 0; lodIndex < manifests.Count; lodIndex++)
        {
            var mesh = meshes[lodIndex];
            var primitives = mesh.GetProperty("primitives");
            if (primitives.GetArrayLength() != manifests[lodIndex].SectionCount)
                Refuse("SME314", $"LOD {lodIndex} section count changed during GLB reopen.");
            int? idAccessorIndex = null;
            long indexCount = 0;
            JsonElement? firstAttributes = null;
            foreach (var primitive in primitives.EnumerateArray())
            {
                var attributes = primitive.GetProperty("attributes");
                if (firstAttributes is null)
                {
                    firstAttributes = attributes;
                }
                else
                {
                    foreach (var expected in firstAttributes.Value.EnumerateObject())
                        if (!attributes.TryGetProperty(expected.Name, out var actual) ||
                            actual.GetInt32() != expected.Value.GetInt32())
                            Refuse("SME327", $"LOD {lodIndex} primitives do not share the same semantic attribute accessors.");
                }
                var current = attributes.GetProperty("_ARKIVE_VERTEX_ID").GetInt32();
                idAccessorIndex ??= current;
                if (idAccessorIndex != current)
                    Refuse("SME315", $"LOD {lodIndex} primitives do not share one stable vertex-ID accessor.");
                indexCount += accessors[primitive.GetProperty("indices").GetInt32()].GetProperty("count").GetInt64();
            }
            if (indexCount != manifests[lodIndex].IndexCount)
                Refuse("SME316", $"LOD {lodIndex} GLB index count {indexCount} differs from native {manifests[lodIndex].IndexCount}.");
            var accessor = accessors[idAccessorIndex!.Value];
            if (accessor.GetProperty("componentType").GetInt32() != 5125 ||
                accessor.GetProperty("type").GetString() != "SCALAR" ||
                accessor.GetProperty("count").GetInt32() != manifests[lodIndex].VertexCount)
                Refuse("SME317", $"LOD {lodIndex} stable vertex-ID accessor has the wrong shape.");
            var view = views[accessor.GetProperty("bufferView").GetInt32()];
            var offset = binStart + view.GetProperty("byteOffset").GetInt32() +
                         (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0);
            var size = checked(manifests[lodIndex].VertexCount * 4);
            if (offset < binStart || offset > bytes.Length - size)
                Refuse("SME318", $"LOD {lodIndex} stable vertex-ID data is outside the BIN chunk.");
            for (var vertex = 0; vertex < manifests[lodIndex].VertexCount; vertex++)
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + vertex * 4)) != vertex)
                    Refuse("SME319", $"LOD {lodIndex} stable vertex ID {vertex} did not round-trip.");

            var attributesToValidate = firstAttributes!.Value;
            VerifyAttribute("_ARKIVE_VERTEX_ID", 5125, "SCALAR",
                manifests[lodIndex].StableVertexIdSha256);
            VerifyAttribute("POSITION", 5126, "VEC3", manifests[lodIndex].DecodedPositionSha256);
            VerifyAttribute("NORMAL", 5126, "VEC3", manifests[lodIndex].DecodedNormalSha256);
            VerifyAttribute("TANGENT", 5126, "VEC4", manifests[lodIndex].DecodedTangentSha256);
            VerifyAttribute("JOINTS_0", 5123, "VEC4", manifests[lodIndex].DecodedJointSha256);
            VerifyAttribute("WEIGHTS_0", 5126, "VEC4", manifests[lodIndex].DecodedWeightSha256);
            for (var uv = 0; uv < manifests[lodIndex].DecodedUvSha256.Count; uv++)
                VerifyAttribute($"TEXCOORD_{uv}", 5126, "VEC2", manifests[lodIndex].DecodedUvSha256[uv]);
            VerifyAttribute(
                $"TEXCOORD_{manifests[lodIndex].StableVertexIdTextureCoordinate}",
                5126,
                "VEC2",
                manifests[lodIndex].StableVertexIdUvSha256);
            if (manifests[lodIndex].DecodedColorSha256 is { } colorHash)
                VerifyAttribute("COLOR_0", 5121, "VEC4", colorHash);

            void VerifyAttribute(string name, int componentType, string type, string expectedHash)
            {
                if (!attributesToValidate.TryGetProperty(name, out var attributeIndex))
                    Refuse("SME321", $"LOD {lodIndex} is missing required GLB attribute {name}.");
                var attributeAccessor = accessors[attributeIndex.GetInt32()];
                if (attributeAccessor.GetProperty("componentType").GetInt32() != componentType ||
                    attributeAccessor.GetProperty("type").GetString() != type ||
                    attributeAccessor.GetProperty("count").GetInt32() != manifests[lodIndex].VertexCount)
                    Refuse("SME322", $"LOD {lodIndex} attribute {name} has the wrong accessor shape.");
                var payload = ReadAccessorPayload(bytes, binStart, views, attributeAccessor);
                var actualHash = SkeletalMeshSemanticDecoder.Sha(payload);
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    Refuse("SME323", $"LOD {lodIndex} attribute {name} changed during GLB reopen.");
            }
        }
        var skin = root.GetProperty("skins")[0];
        if (skin.GetProperty("joints").GetArrayLength() != layout.BoneCount)
            Refuse("SME320", "The generated GLB reference-skeleton joint count changed during reopen.");
    }

    private static ReadOnlySpan<byte> ReadAccessorPayload(
        byte[] glb,
        int binStart,
        JsonElement views,
        JsonElement accessor)
    {
        var view = views[accessor.GetProperty("bufferView").GetInt32()];
        if (view.TryGetProperty("byteStride", out _))
            Refuse("SME324", "Arkive's semantic GLB accessors unexpectedly use an interleaved buffer view.");
        var componentBytes = accessor.GetProperty("componentType").GetInt32() switch
        {
            5121 => 1,
            5123 => 2,
            5125 or 5126 => 4,
            _ => 0,
        };
        var components = accessor.GetProperty("type").GetString() switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            _ => 0,
        };
        if (componentBytes == 0 || components == 0)
            Refuse("SME325", "Arkive's semantic GLB accessor uses an unsupported component/type pair.");
        var count = accessor.GetProperty("count").GetInt32();
        var size = checked(componentBytes * components * count);
        var offset = checked(binStart +
            (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) +
            (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0));
        if (offset < binStart || offset > glb.Length - size)
            Refuse("SME326", "Arkive's semantic GLB accessor exceeds the BIN chunk.");
        return glb.AsSpan(offset, size);
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
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "skeletal-mesh" : result;
    }

    private static void Refuse(string code, string message) =>
        throw new SkeletalMeshEditableExportRefusalException(code, message);
}
