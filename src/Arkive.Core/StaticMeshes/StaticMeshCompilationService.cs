using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Packages;

namespace Arkive.Core.StaticMeshes;

public sealed class StaticMeshCompilationService
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "arkive-staticmesh-compilation.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly StaticMeshEditPreflightService _preflight;

    public StaticMeshCompilationService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(summaryReader, _protectionProvider);
        _preflight = new StaticMeshEditPreflightService(summaryReader, _protectionProvider);
    }

    public async Task<StaticMeshCompilationResult> CompileAsync(
        string editManifestPath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var editPath = Path.GetFullPath(editManifestPath);
        var edit = await LoadEditAsync(editPath, cancellationToken).ConfigureAwait(false);
        var source = await StaticMeshEditableExportService.LoadManifestAsync(
            edit.SourceEditableManifestPath, cancellationToken).ConfigureAwait(false);
        var recon = await StaticMeshReconnaissanceWorkspaceStore.LoadReportAsync(
            source.ReconnaissanceJsonPath, cancellationToken).ConfigureAwait(false);
        var preflight = await _preflight.InspectAsync(
            edit.SourceEditableManifestPath, edit.EditedGlbPath, cancellationToken).ConfigureAwait(false);
        if (preflight.Readiness == StaticMeshEditReadiness.Refused)
            Refuse("STC001", "The edited StaticMesh failed preflight: " + string.Join(" | ", preflight.Issues));
        if (recon.NativeLayout is null || recon.NativeDataRelativeOffset is null)
            Refuse("STC002", "The source StaticMesh native layout is unavailable.");
        var editedHash = ComputeFileSha256(edit.EditedGlbPath, cancellationToken);
        if (!editedHash.Equals(edit.EditedGlbSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("STC003", "The registered edited GLB changed; register or revalidate it again.");

        var sourceBytes = ReadSourceBytes(recon, cancellationToken);
        var candidate = StaticMeshGlbSemanticReader.Read(edit.EditedGlbPath, source);
        var compiled = CompileExport(sourceBytes, recon, candidate, preflight.TopologyChanged);
        var collisionStart = checked((int)recon.NativeDataRelativeOffset.Value + 28);
        var firstLodStart = checked((int)recon.NativeLayout.Lods[0].RelativeOffset);
        if (collisionStart < 0 || firstLodStart < collisionStart || firstLodStart > sourceBytes.Length ||
            firstLodStart > compiled.Length ||
            !sourceBytes.AsSpan(collisionStart, firstLodStart - collisionStart)
                .SequenceEqual(compiled.AsSpan(collisionStart, firstLodStart - collisionStart)))
            Refuse("STC024", "BodySetup, kDOP collision, or pre-LOD metadata changed during render compilation.");
        var sourceTailStart = checked((int)(recon.NativeLayout.Lods[^1].RelativeOffset +
                                                recon.NativeLayout.Lods[^1].SerializedBytes));
        var sourceTailBytes = sourceBytes.Length - sourceTailStart;
        if (sourceTailBytes < 0 || compiled.Length < sourceTailBytes ||
            !sourceBytes.AsSpan(sourceTailStart, sourceTailBytes)
                .SequenceEqual(compiled.AsSpan(compiled.Length - sourceTailBytes, sourceTailBytes)))
            Refuse("STC025", "Opaque trailing StaticMesh metadata changed during render compilation.");
        var parser = new StaticMeshNativeLayoutParser();
        if (!parser.TryParse(
                compiled,
                recon.SerialOffset,
                checked((int)recon.NativeDataRelativeOffset.Value),
                out var compiledLayout,
                out var refusal) || compiledLayout is null)
            Refuse("STC004", "The compiled StaticMesh failed native decode-back: " + refusal);
        var decoded = new StaticMeshSemanticDecoder().Decode(compiled, compiledLayout);
        ValidateDecodeBack(candidate, decoded, preflight.TopologyChanged);

        Directory.CreateDirectory(outputDirectory);
        var compiledPath = Path.Combine(outputDirectory, "arkive-staticmesh-export.bin");
        var proofPath = Path.Combine(outputDirectory, Safe(recon.ObjectName) + ".decoded-proof.glb");
        var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        WriteBytesAtomic(compiledPath, compiled);
        var proofReport = recon with
        {
            SerialSize = compiled.LongLength,
            ExportPayloadSha256 = StaticMeshSemanticDecoder.Sha(compiled),
            NativeLayout = compiledLayout,
            Readiness = StaticMeshReadiness.Editable,
        };
        var proof = new ArkiveStaticMeshGlbWriter().Build(proofReport, decoded);
        WriteBytesAtomic(proofPath, proof.Bytes);
        var checks = new List<string>
        {
            $"Native StaticMesh3 parsed after compilation ({compiledLayout.Lods.Count} LOD(s)).",
            "Every compiled render stream decoded back within its native quantization tolerance.",
            "The source BodySetup and kDOP collision tree were retained byte-for-byte.",
            preflight.TopologyChanged
                ? "Render topology changed; legacy LOD bulk, wireframe, and auxiliary index buffers were regenerated or cleared without altering collision."
                : "Render topology and all non-editable LOD bytes were retained.",
        };
        watch.Stop();
        var manifest = new StaticMeshCompilationManifest(
            CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            editPath,
            edit.SourceEditableManifestPath,
            source.SourcePackagePath,
            source.PhysicalPackageName,
            source.PackageAlias,
            source.ObjectName,
            source.ClassName,
            source.NativeExportIndex,
            source.SerialOffset,
            source.SerialSize,
            compiled.LongLength,
            compiled.LongLength - source.SerialSize,
            source.SourcePackageSha256,
            source.SourceExportSha256,
            editedHash,
            Path.GetFileName(compiledPath),
            StaticMeshSemanticDecoder.Sha(compiled),
            Path.GetFileName(proofPath),
            StaticMeshSemanticDecoder.Sha(proof.Bytes),
            preflight.TopologyChanged,
            true,
            true,
            checks,
            watch.Elapsed.TotalSeconds);
        WriteJsonAtomic(manifestPath, manifest);
        return new StaticMeshCompilationResult(
            outputDirectory, compiledPath, proofPath, manifestPath, manifest, watch.Elapsed);
    }

    public static async Task<StaticMeshCompilationManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var manifest = await JsonSerializer.DeserializeAsync<StaticMeshCompilationManifest>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new JsonException("The StaticMesh compilation manifest was empty.");
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"StaticMesh compilation schema {manifest.SchemaVersion} is unsupported.");
        return manifest;
    }

    private static byte[] CompileExport(
        byte[] source,
        StaticMeshReconnaissanceReport report,
        StaticMeshGlbDocument candidate,
        bool topologyChanged)
    {
        var layout = report.NativeLayout!;
        var sourceDecoded = new StaticMeshSemanticDecoder().Decode(source, layout)
            .ToDictionary(lod => lod.Evidence.LodIndex);
        using var output = new MemoryStream();
        var firstLod = checked((int)layout.Lods[0].RelativeOffset);
        output.Write(source, 0, firstLod);
        foreach (var sourceLod in layout.Lods)
        {
            if (!candidate.Lods.TryGetValue(sourceLod.LodIndex, out var candidateLod))
                Refuse("STC010", $"Candidate is missing LOD {sourceLod.LodIndex}.");
            var bytes = BuildLod(
                source, sourceLod, sourceDecoded[sourceLod.LodIndex], candidateLod, topologyChanged);
            output.Write(bytes);
        }
        var sourceTail = checked((int)(layout.Lods[^1].RelativeOffset + layout.Lods[^1].SerializedBytes));
        output.Write(source, sourceTail, source.Length - sourceTail);
        var compiled = output.ToArray();
        if (topologyChanged || MaximumPositionDelta(
                sourceDecoded[0].Positions, candidate.Lods[0].Vertices) > 1e-7f)
            WriteBounds(compiled, checked((int)layout.NativeDataRelativeOffset), candidate.Lods[0].Vertices);
        return compiled;
    }

    private static byte[] BuildLod(
        byte[] source,
        StaticMeshLodEvidence sourceLod,
        DecodedStaticMeshLod sourceDecoded,
        StaticMeshGlbLod candidate,
        bool topologyChanged)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        if (topologyChanged)
        {
            writer.Write(0x20u);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
        }
        else
        {
            writer.Write(source, checked((int)sourceLod.LegacyBulkRelativeOffset), sourceLod.LegacyBulkSerializedBytes);
        }

        writer.Write(sourceLod.Sections.Count);
        var combinedIndices = new List<ushort>();
        foreach (var sourceSection in sourceLod.Sections)
        {
            var section = candidate.Sections.Single(item => item.SectionIndex == sourceSection.SectionIndex);
            var raw = source.AsSpan(
                checked((int)sourceSection.RelativeOffset), sourceSection.SerializedBytes).ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(16), combinedIndices.Count);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(20), section.Indices.Count / 3);
            var minimum = checked((int)section.Indices.Min());
            var maximum = checked((int)section.Indices.Max());
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(24), minimum);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(28), maximum);
            writer.Write(raw);
            foreach (var index in section.Indices)
            {
                if (index >= candidate.Vertices.Count || index > ushort.MaxValue)
                    Refuse("STC011", $"LOD {sourceLod.LodIndex} section {section.SectionIndex} has an out-of-range vertex index {index}.");
                combinedIndices.Add(checked((ushort)index));
            }
        }

        var positions = topologyChanged
            ? EncodePositions(candidate.Vertices)
            : EncodePositionsPreservingSource(source, sourceLod, sourceDecoded, candidate.Vertices);
        writer.Write(12);
        writer.Write(candidate.Vertices.Count);
        WriteBulk(writer, 12, candidate.Vertices.Count, positions);

        var attributeSize = checked(8 + sourceLod.TextureCoordinateCount * (sourceLod.UsesFullPrecisionUvs ? 8 : 4));
        var attributes = topologyChanged
            ? EncodeAttributes(candidate.Vertices, sourceLod.TextureCoordinateCount, sourceLod.UsesFullPrecisionUvs)
            : EncodeAttributesPreservingSource(source, sourceLod, sourceDecoded, candidate.Vertices);
        writer.Write(sourceLod.TextureCoordinateCount);
        writer.Write(attributeSize);
        writer.Write(candidate.Vertices.Count);
        writer.Write(sourceLod.UsesFullPrecisionUvs ? 1 : 0);
        WriteBulk(writer, attributeSize, candidate.Vertices.Count, attributes);

        var hasColours = candidate.Vertices.All(vertex => vertex.Colour is not null);
        if (candidate.Vertices.Any(vertex => vertex.Colour is not null) && !hasColours)
            Refuse("STC012", $"LOD {sourceLod.LodIndex} has only partial vertex colours.");
        if (hasColours)
        {
            var colours = !topologyChanged && sourceLod.Colours is not null
                ? EncodeColoursPreservingSource(source, sourceLod, sourceDecoded, candidate.Vertices)
                : EncodeColours(candidate.Vertices);
            writer.Write(4);
            writer.Write(candidate.Vertices.Count);
            WriteBulk(writer, 4, candidate.Vertices.Count, colours);
        }
        else
        {
            writer.Write(0);
            writer.Write(0);
        }
        writer.Write(candidate.Vertices.Count);
        WriteBulk(writer, 2, combinedIndices.Count, StaticMeshSemanticDecoder.EncodeUInt16(combinedIndices));
        if (topologyChanged)
        {
            var wireframe = BuildWireframeIndices(combinedIndices);
            WriteBulk(writer, 2, wireframe.Count, StaticMeshSemanticDecoder.EncodeUInt16(wireframe));
            WriteBulk(writer, 2, 0, []);
        }
        else
        {
            CopyBulk(source, sourceLod.WireframeIndices, writer);
            CopyBulk(source, sourceLod.ExtraIndices, writer);
        }
        return stream.ToArray();
    }

    private static byte[] EncodePositions(IReadOnlyList<StaticMeshGlbVertex> vertices)
    {
        var bytes = new byte[checked(vertices.Count * 12)];
        for (var index = 0; index < vertices.Count; index++)
        {
            var value = vertices[index].Position;
            WriteSingle(bytes, index * 12, value.X * 100f);
            WriteSingle(bytes, index * 12 + 4, value.Z * 100f);
            WriteSingle(bytes, index * 12 + 8, value.Y * 100f);
        }
        return bytes;
    }

    private static byte[] EncodePositionsPreservingSource(
        byte[] source,
        StaticMeshLodEvidence lod,
        DecodedStaticMeshLod decoded,
        IReadOnlyList<StaticMeshGlbVertex> vertices)
    {
        var bytes = source.AsSpan(
            checked((int)lod.Positions.DataRelativeOffset), checked((int)lod.Positions.DataBytes)).ToArray();
        for (var index = 0; index < vertices.Count; index++)
        {
            if (Vector3.Distance(decoded.Positions[index], vertices[index].Position) <= 1e-7f) continue;
            var value = vertices[index].Position;
            WriteSingle(bytes, index * 12, value.X * 100f);
            WriteSingle(bytes, index * 12 + 4, value.Z * 100f);
            WriteSingle(bytes, index * 12 + 8, value.Y * 100f);
        }
        return bytes;
    }

    private static byte[] EncodeAttributes(
        IReadOnlyList<StaticMeshGlbVertex> vertices,
        int uvCount,
        bool fullPrecision)
    {
        var stride = checked(8 + uvCount * (fullPrecision ? 8 : 4));
        var bytes = new byte[checked(vertices.Count * stride)];
        for (var index = 0; index < vertices.Count; index++)
        {
            var vertex = vertices[index];
            var tangent = Vector3.Normalize(new Vector3(vertex.Tangent.X, vertex.Tangent.Z, vertex.Tangent.Y));
            var normal = Vector3.Normalize(new Vector3(vertex.Normal.X, vertex.Normal.Z, vertex.Normal.Y));
            var offset = index * stride;
            bytes[offset] = PackNormal(tangent.X);
            bytes[offset + 1] = PackNormal(tangent.Y);
            bytes[offset + 2] = PackNormal(tangent.Z);
            bytes[offset + 3] = 255;
            bytes[offset + 4] = PackNormal(normal.X);
            bytes[offset + 5] = PackNormal(normal.Y);
            bytes[offset + 6] = PackNormal(normal.Z);
            bytes[offset + 7] = vertex.Tangent.W < 0 ? (byte)0 : (byte)255;
            for (var uv = 0; uv < uvCount; uv++)
            {
                var uvOffset = offset + 8 + uv * (fullPrecision ? 8 : 4);
                if (fullPrecision)
                {
                    WriteSingle(bytes, uvOffset, vertex.TextureCoordinates[uv].X);
                    WriteSingle(bytes, uvOffset + 4, vertex.TextureCoordinates[uv].Y);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(uvOffset),
                        BitConverter.HalfToUInt16Bits((Half)vertex.TextureCoordinates[uv].X));
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(uvOffset + 2),
                        BitConverter.HalfToUInt16Bits((Half)vertex.TextureCoordinates[uv].Y));
                }
            }
        }
        return bytes;
    }

    private static byte[] EncodeAttributesPreservingSource(
        byte[] source,
        StaticMeshLodEvidence lod,
        DecodedStaticMeshLod decoded,
        IReadOnlyList<StaticMeshGlbVertex> vertices)
    {
        var stride = lod.VertexAttributes.ElementSize;
        var bytes = source.AsSpan(
            checked((int)lod.VertexAttributes.DataRelativeOffset),
            checked((int)lod.VertexAttributes.DataBytes)).ToArray();
        var uvTolerance = lod.UsesFullPrecisionUvs ? 1e-7f : 0.0005f;
        for (var index = 0; index < vertices.Count; index++)
        {
            var vertex = vertices[index];
            var tangent = new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);
            var sourceTangent = new Vector3(
                decoded.Tangents[index].X, decoded.Tangents[index].Y, decoded.Tangents[index].Z);
            if (Vector3.Distance(tangent, sourceTangent) > 0.005f ||
                (vertex.Tangent.W < 0) != (decoded.Tangents[index].W < 0))
            {
                var native = Vector3.Normalize(new Vector3(tangent.X, tangent.Z, tangent.Y));
                var offset = index * stride;
                bytes[offset] = PackNormal(native.X);
                bytes[offset + 1] = PackNormal(native.Y);
                bytes[offset + 2] = PackNormal(native.Z);
                bytes[offset + 7] = vertex.Tangent.W < 0 ? (byte)0 : (byte)255;
            }
            if (Vector3.Distance(vertex.Normal, decoded.Normals[index]) > 0.005f)
            {
                var native = Vector3.Normalize(new Vector3(vertex.Normal.X, vertex.Normal.Z, vertex.Normal.Y));
                var offset = index * stride + 4;
                bytes[offset] = PackNormal(native.X);
                bytes[offset + 1] = PackNormal(native.Y);
                bytes[offset + 2] = PackNormal(native.Z);
            }
            for (var uv = 0; uv < lod.TextureCoordinateCount; uv++)
            {
                if (Vector2.Distance(vertex.TextureCoordinates[uv], decoded.TextureCoordinates[uv][index]) <= uvTolerance)
                    continue;
                var offset = index * stride + 8 + uv * (lod.UsesFullPrecisionUvs ? 8 : 4);
                if (lod.UsesFullPrecisionUvs)
                {
                    WriteSingle(bytes, offset, vertex.TextureCoordinates[uv].X);
                    WriteSingle(bytes, offset + 4, vertex.TextureCoordinates[uv].Y);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset),
                        BitConverter.HalfToUInt16Bits((Half)vertex.TextureCoordinates[uv].X));
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2),
                        BitConverter.HalfToUInt16Bits((Half)vertex.TextureCoordinates[uv].Y));
                }
            }
        }
        return bytes;
    }

    private static byte[] EncodeColours(IReadOnlyList<StaticMeshGlbVertex> vertices)
    {
        var bytes = new byte[checked(vertices.Count * 4)];
        for (var index = 0; index < vertices.Count; index++)
        {
            var colour = vertices[index].Colour!.Value;
            bytes[index * 4] = PackColour(colour.Z);
            bytes[index * 4 + 1] = PackColour(colour.Y);
            bytes[index * 4 + 2] = PackColour(colour.X);
            bytes[index * 4 + 3] = PackColour(colour.W);
        }
        return bytes;
    }

    private static byte[] EncodeColoursPreservingSource(
        byte[] source,
        StaticMeshLodEvidence lod,
        DecodedStaticMeshLod decoded,
        IReadOnlyList<StaticMeshGlbVertex> vertices)
    {
        var bytes = source.AsSpan(
            checked((int)lod.Colours!.DataRelativeOffset), checked((int)lod.Colours.DataBytes)).ToArray();
        if (decoded.Colours is null) return EncodeColours(vertices);
        for (var index = 0; index < vertices.Count; index++)
        {
            var colour = vertices[index].Colour!.Value;
            var sourceColour = new Vector4(
                decoded.Colours[index * 4] / 255f,
                decoded.Colours[index * 4 + 1] / 255f,
                decoded.Colours[index * 4 + 2] / 255f,
                decoded.Colours[index * 4 + 3] / 255f);
            if (Vector4.Distance(colour, sourceColour) <= 0.5f / 255f) continue;
            bytes[index * 4] = PackColour(colour.Z);
            bytes[index * 4 + 1] = PackColour(colour.Y);
            bytes[index * 4 + 2] = PackColour(colour.X);
            bytes[index * 4 + 3] = PackColour(colour.W);
        }
        return bytes;
    }

    private static float MaximumPositionDelta(
        IReadOnlyList<Vector3> source,
        IReadOnlyList<StaticMeshGlbVertex> candidate)
    {
        if (source.Count != candidate.Count) return float.PositiveInfinity;
        var maximum = 0f;
        for (var index = 0; index < source.Count; index++)
            maximum = Math.Max(maximum, Vector3.Distance(source[index], candidate[index].Position));
        return maximum;
    }

    private static IReadOnlyList<ushort> BuildWireframeIndices(IReadOnlyList<ushort> triangleIndices)
    {
        var edges = new HashSet<(ushort A, ushort B)>();
        for (var index = 0; index < triangleIndices.Count; index += 3)
        {
            Add(triangleIndices[index], triangleIndices[index + 1]);
            Add(triangleIndices[index + 1], triangleIndices[index + 2]);
            Add(triangleIndices[index + 2], triangleIndices[index]);
        }
        return edges.OrderBy(edge => edge.A).ThenBy(edge => edge.B)
            .SelectMany(edge => new[] { edge.A, edge.B }).ToArray();

        void Add(ushort first, ushort second)
        {
            var edge = first <= second ? (first, second) : (second, first);
            edges.Add(edge);
        }
    }

    private static void WriteBounds(
        Span<byte> bytes,
        int nativeOffset,
        IReadOnlyList<StaticMeshGlbVertex> vertices)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var native = new Vector3[vertices.Count];
        for (var index = 0; index < vertices.Count; index++)
        {
            var value = vertices[index].Position;
            native[index] = new Vector3(value.X * 100f, value.Z * 100f, value.Y * 100f);
            minimum = Vector3.Min(minimum, native[index]);
            maximum = Vector3.Max(maximum, native[index]);
        }
        var origin = (minimum + maximum) * 0.5f;
        var extent = (maximum - minimum) * 0.5f;
        var radius = native.Max(value => Vector3.Distance(origin, value));
        WriteSingle(bytes, nativeOffset, origin.X);
        WriteSingle(bytes, nativeOffset + 4, origin.Y);
        WriteSingle(bytes, nativeOffset + 8, origin.Z);
        WriteSingle(bytes, nativeOffset + 12, extent.X);
        WriteSingle(bytes, nativeOffset + 16, extent.Y);
        WriteSingle(bytes, nativeOffset + 20, extent.Z);
        WriteSingle(bytes, nativeOffset + 24, radius);
    }

    private static void ValidateDecodeBack(
        StaticMeshGlbDocument candidate,
        IReadOnlyList<DecodedStaticMeshLod> decoded,
        bool topologyChanged)
    {
        foreach (var result in decoded)
        {
            var expected = candidate.Lods[result.Evidence.LodIndex];
            if (result.Positions.Length != expected.Vertices.Count)
                Refuse("STC020", $"LOD {result.Evidence.LodIndex} decode-back vertex count differs.");
            var maximumPosition = 0f;
            var maximumNormal = 0f;
            var maximumTangent = 0f;
            var maximumUv = 0f;
            for (var vertex = 0; vertex < result.Positions.Length; vertex++)
            {
                maximumPosition = Math.Max(maximumPosition,
                    Vector3.Distance(result.Positions[vertex], expected.Vertices[vertex].Position));
                maximumNormal = Math.Max(maximumNormal,
                    Vector3.Distance(result.Normals[vertex], expected.Vertices[vertex].Normal));
                maximumTangent = Math.Max(maximumTangent,
                    Vector3.Distance(new Vector3(result.Tangents[vertex].X, result.Tangents[vertex].Y, result.Tangents[vertex].Z),
                        new Vector3(expected.Vertices[vertex].Tangent.X, expected.Vertices[vertex].Tangent.Y, expected.Vertices[vertex].Tangent.Z)));
                for (var uv = 0; uv < result.TextureCoordinates.Length; uv++)
                    maximumUv = Math.Max(maximumUv,
                        Vector2.Distance(result.TextureCoordinates[uv][vertex], expected.Vertices[vertex].TextureCoordinates[uv]));
            }
            if (maximumPosition > 1e-5f || maximumNormal > 0.025f || maximumTangent > 0.025f ||
                maximumUv > (result.Evidence.UsesFullPrecisionUvs ? 1e-6f : 0.002f))
                Refuse("STC021",
                    $"LOD {result.Evidence.LodIndex} decode-back exceeded tolerance: position={maximumPosition}, normal={maximumNormal}, tangent={maximumTangent}, UV={maximumUv}.");
            var expectedIndices = expected.Sections.SelectMany(section => section.Indices).Select(value => (ushort)value);
            if (!result.Indices.SequenceEqual(expectedIndices))
                Refuse("STC022", $"LOD {result.Evidence.LodIndex} decode-back triangle topology differs.");
            if (!topologyChanged && result.Evidence.SerializedBytes <= 0)
                Refuse("STC023", "Same-topology compilation lost its native LOD evidence.");
        }
    }

    private byte[] ReadSourceBytes(StaticMeshReconnaissanceReport report, CancellationToken cancellationToken)
    {
        var catalog = _catalogReader.Read(report.PackagePath);
        using var logical = new LogicalPackageStream(catalog.Summary, _protectionProvider);
        var bytes = new byte[checked((int)report.SerialSize)];
        logical.Position = report.SerialOffset;
        logical.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (!StaticMeshSemanticDecoder.Sha(bytes).Equals(report.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("STC030", "The source StaticMesh export changed.");
        return bytes;
    }

    private static async Task<StaticMeshEditManifest> LoadEditAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var edit = await JsonSerializer.DeserializeAsync<StaticMeshEditManifest>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                   throw new JsonException("The StaticMesh edit manifest was empty.");
        if (edit.SchemaVersion != 1) throw new NotSupportedException($"StaticMesh edit schema {edit.SchemaVersion} is unsupported.");
        return edit;
    }

    private static void WriteBulk(BinaryWriter writer, int elementSize, int count, byte[] data)
    {
        if (data.Length != checked(elementSize * count)) Refuse("STC040", "A native bulk buffer size is inconsistent.");
        writer.Write(elementSize);
        writer.Write(count);
        writer.Write(data);
    }

    private static void CopyBulk(byte[] source, StaticMeshBufferEvidence evidence, BinaryWriter writer)
    {
        var size = checked((int)(8 + evidence.DataBytes));
        writer.Write(source, checked((int)evidence.HeaderRelativeOffset), size);
    }

    private static byte PackNormal(float value) => checked((byte)Math.Clamp(
        (int)MathF.Round(Math.Clamp(value, -1f, 1f) * 127f + 128f), 0, 255));

    private static byte PackColour(float value) => checked((byte)Math.Clamp(
        (int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255));

    private static void WriteSingle(Span<byte> bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], BitConverter.SingleToInt32Bits(value));

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
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    [DoesNotReturn]
    private static void Refuse(string code, string message) => throw new StaticMeshRefusalException(code, message);
}
