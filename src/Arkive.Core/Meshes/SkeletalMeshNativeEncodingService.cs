using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Inspection;
using Arkive.Core.Packages;

namespace Arkive.Core.Meshes;

/// <summary>
/// Produces a same-length replacement export payload from a preflight-approved
/// editable GLB. The proof is deliberately bounded to the native mesh bounds
/// and the packed tangent/normal and position fields in each proven GPU vertex
/// record. Every other byte must remain identical.
/// </summary>
public sealed class SkeletalMeshNativeEncodingService
{
    public const int CurrentSchemaVersion = 2;
    public const string ManifestFileName = "arkive-skeletalmesh-native-encoding.json";
    private const float PositionChangeEpsilon = 1e-6f;
    private const float DecodePositionTolerance = 2e-6f;
    private const float DecodeFrameTolerance = 0.025f;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly SkeletalMeshEditPreflightService _preflight = new();

    public SkeletalMeshNativeEncodingService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(summaryReader, _protectionProvider);
    }

    public static async Task<SkeletalMeshNativeEncodingManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            Refuse("SMN080", $"The native encoding manifest was not found: {fullPath}");
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<SkeletalMeshNativeEncodingManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new SkeletalMeshNativeEncodingRefusalException(
                           "SMN081", "The native encoding manifest was empty.");
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            Refuse("SMN082", $"Native encoding schema {manifest.SchemaVersion} is unsupported; " +
                               $"Arkive requires schema {CurrentSchemaVersion}.");
        return manifest;
    }

    public async Task<SkeletalMeshNativeEncodingResult> EncodeAsync(
        SkeletalMeshNativeEncodingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Edit);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var edit = request.Edit;
        if (edit.Manifest.SchemaVersion != SkeletalMeshEditWorkspaceStore.CurrentSchemaVersion)
            Refuse("SMN001", "The edited-GLB registration schema is stale; register the edit again.");

        var sourceManifestPath = Path.GetFullPath(edit.Manifest.SourceManifestPath);
        var sourceManifest = await ReadJsonAsync<SkeletalMeshEditableExportManifest>(
            sourceManifestPath, cancellationToken).ConfigureAwait(false);
        if (sourceManifest.SchemaVersion != SkeletalMeshEditableExportService.CurrentSchemaVersion)
            Refuse("SMN002", "Native encoding requires a current schema-3 editable GLB export.");
        if (!sourceManifest.ExportId.Equals(edit.Manifest.SourceExportId, StringComparison.OrdinalIgnoreCase) ||
            !sourceManifest.PackageAlias.Equals(edit.Manifest.PackageAlias, StringComparison.OrdinalIgnoreCase) ||
            !sourceManifest.ObjectName.Equals(edit.Manifest.ObjectName, StringComparison.OrdinalIgnoreCase))
            Refuse("SMN003", "The edit registration no longer matches its source editable export.");

        var sourceDirectory = Path.GetDirectoryName(sourceManifestPath) ??
                              throw new InvalidDataException("The source manifest directory is unavailable.");
        var sourceGlbPath = ContainedPath(sourceDirectory,
            Path.Combine(sourceDirectory, sourceManifest.GlbRelativePath));
        var sourceGlb = new FileInfo(sourceGlbPath);
        var sourceEntry = new SkeletalMeshEditableWorkspaceEntry(
            sourceDirectory, sourceManifestPath, sourceGlbPath, sourceManifest,
            sourceGlb.Exists, sourceGlb.Exists && sourceGlb.Length == sourceManifest.GlbBytes, true);
        if (!sourceEntry.GlbExists || !sourceEntry.GlbLengthMatches)
            Refuse("SMN004", "The source editable GLB is missing or its length changed.");

        var preflight = await _preflight.InspectAsync(
            sourceEntry, edit.EditedGlbPath, cancellationToken).ConfigureAwait(false);
        if (preflight.Readiness == SkeletalMeshEditReadiness.Refused || preflight.ErrorCount != 0 ||
            preflight.SchemaVersion != SkeletalMeshEditPreflightService.CurrentSchemaVersion ||
            preflight.AnimationValidation is not { Passed: true })
            Refuse("SMN005", "The edited GLB no longer passes semantic preflight.");
        if (preflight.Readiness == SkeletalMeshEditReadiness.ReadyNoChanges)
            Refuse("SMN006", "The edited GLB contains no encodable position, normal, or tangent changes.");
        if (preflight.Readiness == SkeletalMeshEditReadiness.ReadyForTopologyCompilation)
            Refuse("SMN008",
                "This edit changes topology or compiler-owned streams. Use the variable-size native LOD compiler; " +
                "the same-length position/tangent encoder cannot build it.");
        if (!preflight.CandidateGlbSha256.Equals(
                edit.Manifest.EditedGlbSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SMN007", "The registered edited GLB changed; run preflight again before encoding.");

        var reconPath = Path.GetFullPath(sourceManifest.ReconnaissanceJsonPath);
        var recon = await ReadJsonAsync<SkeletalMeshReconnaissanceReport>(
            reconPath, cancellationToken).ConfigureAwait(false);
        ValidateIdentity(sourceManifest, recon);
        var layout = recon.NativeLayout!;
        var packagePath = Path.GetFullPath(recon.PackagePath);
        var packageFile = new FileInfo(packagePath);
        if (!packageFile.Exists) throw new FileNotFoundException("The source package was not found.", packagePath);
        if (packageFile.Length != recon.PackageLength ||
            packageFile.LastWriteTimeUtc.Ticks != recon.PackageLastWriteUtcTicks)
            Refuse("SMN010", "The source package identity metadata changed; rerun Native mesh recon.");
        var packageHash = await ComputeFileSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
        if (!packageHash.Equals(recon.PackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SMN011", "The source package SHA-256 changed; rerun Native mesh recon.");

        var catalog = _catalogReader.Read(packagePath);
        var asset = catalog.Assets.SingleOrDefault(item => item.ExportIndex == recon.NativeExportIndex);
        if (asset is null || asset.SerialOffset != recon.SerialOffset || asset.SerialSize != recon.SerialSize ||
            !asset.ObjectName.Equals(recon.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            !asset.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            Refuse("SMN012", "The current export table no longer matches the mesh reconnaissance report.");

        var sourceBytes = new byte[checked((int)recon.SerialSize)];
        using (var logical = new LogicalPackageStream(catalog.Summary, _protectionProvider))
        {
            logical.Position = recon.SerialOffset;
            logical.ReadExactly(sourceBytes);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var sourceHash = SkeletalMeshSemanticDecoder.Sha(sourceBytes);
        if (!sourceHash.Equals(recon.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            !sourceHash.Equals(sourceManifest.SourceExportSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SMN013", "The source native export hash changed; rerun Native mesh recon and export.");

        var sourceDocument = SkeletalMeshGlbSemanticReader.Read(sourceGlbPath, sourceManifest);
        var candidateDocument = SkeletalMeshGlbSemanticReader.Read(edit.EditedGlbPath, sourceManifest);
        var decodedSource = new SkeletalMeshSemanticDecoder().Decode(sourceBytes, layout);
        var encoded = sourceBytes.ToArray();
        var allowlisted = new bool[encoded.Length];
        var lodEvidence = new List<SkeletalMeshNativeEncodingLodEvidence>();
        var targetPositions = new Dictionary<int, Vector3[]>();
        var targetNormals = new Dictionary<int, Vector3[]>();
        var targetTangents = new Dictionary<int, Vector4[]>();
        var anyPositionWrites = false;

        foreach (var decoded in decodedSource)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lodIndex = decoded.Evidence.LodIndex;
            if (!candidateDocument.Lods.TryGetValue(lodIndex, out var candidate) ||
                !sourceDocument.Lods.TryGetValue(lodIndex, out _))
                Refuse("SMN020", $"Edited GLB LOD {lodIndex} is unavailable during encoding.");
            var lodPreflight = preflight.Lods.Single(item => item.LodIndex == lodIndex);
            var positions = decoded.Positions.ToArray();
            var normals = decoded.Normals.ToArray();
            var tangents = decoded.Tangents.ToArray();
            var positionWrites = 0;
            foreach (var (stableId, vertex) in candidate.Vertices)
            {
                if (stableId >= positions.Length)
                    Refuse("SMN021", $"LOD {lodIndex} stable vertex {stableId} exceeds the native buffer.");
                var vertexIndex = checked((int)stableId);
                if (Vector3.Distance(positions[vertexIndex], vertex.Position) <= PositionChangeEpsilon) continue;
                positions[vertexIndex] = vertex.Position;
                WriteNativePosition(encoded, decoded.Evidence, vertexIndex, vertex.Position);
                MarkVertexField(allowlisted, decoded.Evidence, vertexIndex,
                    decoded.Evidence.VertexPositionOffset, 12);
                positionWrites++;
            }
            anyPositionWrites |= positionWrites > 0;

            var reconstructTangents = !candidate.HasTangents;
            var writeFrames = lodPreflight.ChangedNormalVertexCount > 0 ||
                              lodPreflight.ChangedTangentVertexCount > 0 || reconstructTangents;
            var frameWrites = 0;
            if (writeFrames)
            {
                foreach (var (stableId, vertex) in candidate.Vertices)
                {
                    var candidateNormal = vertex.Normal ??
                        throw new SkeletalMeshNativeEncodingRefusalException(
                            "SMN022", $"LOD {lodIndex} stable vertex {stableId} has no usable normal.");
                    if (!TryNormalize(candidateNormal, out var normal))
                        throw new SkeletalMeshNativeEncodingRefusalException(
                            "SMN022", $"LOD {lodIndex} stable vertex {stableId} has no usable normal.");
                    normals[checked((int)stableId)] = normal;
                }
                Vector4[] rebuiltTangents = reconstructTangents
                    ? ReconstructTangents(decoded.Indices, positions, normals,
                        BuildUvChannel(candidate, decoded.TextureCoordinates[0], positions.Length), lodIndex)
                    : tangents;
                foreach (var (stableId, vertex) in candidate.Vertices)
                {
                    var vertexIndex = checked((int)stableId);
                    var tangent = reconstructTangents
                        ? rebuiltTangents[vertexIndex]
                        : vertex.Tangent is { } candidateTangent
                            ? OrthonormalizeTangent(
                                normals[vertexIndex], candidateTangent, lodIndex, stableId)
                            : throw new SkeletalMeshNativeEncodingRefusalException(
                                "SMN023", $"LOD {lodIndex} stable vertex {stableId} has no tangent.");
                    tangents[vertexIndex] = tangent;
                    WriteNativeFrame(encoded, decoded.Evidence, vertexIndex,
                        normals[vertexIndex], tangent);
                    MarkVertexField(allowlisted, decoded.Evidence, vertexIndex, 0, 3);
                    MarkVertexField(allowlisted, decoded.Evidence, vertexIndex, 4, 4);
                    frameWrites++;
                }
            }

            targetPositions.Add(lodIndex, positions);
            targetNormals.Add(lodIndex, normals);
            targetTangents.Add(lodIndex, tangents);
            var vertexStart = checked((int)decoded.Evidence.VertexBuffer.DataRelativeOffset);
            var vertexBytes = checked((int)decoded.Evidence.VertexBuffer.DataBytes);
            lodEvidence.Add(new SkeletalMeshNativeEncodingLodEvidence(
                lodIndex, decoded.Evidence.VertexCount, positionWrites, frameWrites,
                reconstructTangents,
                decoded.Evidence.VertexBuffer.DataSha256,
                SkeletalMeshSemanticDecoder.Sha(encoded.AsSpan(vertexStart, vertexBytes)),
                decoded.Evidence.VertexBuffer.DataRelativeOffset,
                decoded.Evidence.VertexBuffer.DataBytes,
                0, 0, 0));
        }

        var boundsUpdated = false;
        if (anyPositionWrites)
        {
            var boundsRange = layout.Ranges.SingleOrDefault(range =>
                range.Name.Equals("Bounds", StringComparison.Ordinal));
            if (boundsRange is null || boundsRange.Size != 28)
                Refuse("SMN030", "The proven 28-byte native bounds range is unavailable.");
            WriteBounds(encoded, checked((int)boundsRange.RelativeOffset),
                targetPositions[layout.Lods[0].LodIndex]);
            Mark(allowlisted, checked((int)boundsRange.RelativeOffset), checked((int)boundsRange.Size));
            boundsUpdated = true;
        }

        var actualChanged = 0L;
        var changedLocked = 0L;
        using var lockedSource = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var lockedEncoded = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var index = 0; index < sourceBytes.Length; index++)
        {
            if (sourceBytes[index] != encoded[index])
            {
                actualChanged++;
                if (!allowlisted[index]) changedLocked++;
            }
            if (allowlisted[index]) continue;
            lockedSource.AppendData(sourceBytes, index, 1);
            lockedEncoded.AppendData(encoded, index, 1);
        }
        var lockedSourceHash = Convert.ToHexString(lockedSource.GetHashAndReset());
        var lockedEncodedHash = Convert.ToHexString(lockedEncoded.GetHashAndReset());
        if (changedLocked != 0 || !lockedSourceHash.Equals(lockedEncodedHash, StringComparison.Ordinal))
            Refuse("SMN031", $"Encoding changed {changedLocked:N0} byte(s) outside the explicit allowlist.");
        if (actualChanged == 0)
            Refuse("SMN032", "Native encoding produced no byte changes.");

        var updatedLods = layout.Lods.Select(lod =>
        {
            var offset = checked((int)lod.VertexBuffer.DataRelativeOffset);
            var bytes = checked((int)lod.VertexBuffer.DataBytes);
            return lod with
            {
                VertexBuffer = lod.VertexBuffer with
                {
                    DataSha256 = SkeletalMeshSemanticDecoder.Sha(encoded.AsSpan(offset, bytes)),
                },
            };
        }).ToArray();
        var decodeLayout = layout with { Lods = updatedLods };
        var decodedEncoded = new SkeletalMeshSemanticDecoder().Decode(encoded, decodeLayout);
        for (var index = 0; index < decodedEncoded.Count; index++)
        {
            var decoded = decodedEncoded[index];
            var lodIndex = decoded.Evidence.LodIndex;
            var maximumPositionError = MaximumDistance(decoded.Positions, targetPositions[lodIndex]);
            var maximumNormalError = MaximumDistance(decoded.Normals, targetNormals[lodIndex]);
            var maximumTangentError = MaximumTangentDistance(decoded.Tangents, targetTangents[lodIndex]);
            if (maximumPositionError > DecodePositionTolerance ||
                maximumNormalError > DecodeFrameTolerance ||
                maximumTangentError > DecodeFrameTolerance)
                Refuse("SMN040",
                    $"LOD {lodIndex} decode-back exceeded tolerance: position={maximumPositionError:G6}m, " +
                    $"normal={maximumNormalError:G6}, tangent={maximumTangentError:G6}.");
            lodEvidence[index] = lodEvidence[index] with
            {
                MaximumPositionDecodeErrorMetres = maximumPositionError,
                MaximumNormalDecodeError = maximumNormalError,
                MaximumTangentDecodeError = maximumTangentError,
            };
        }

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var outputName = Safe(recon.ObjectName) + ".skeletalmesh.edit.bin";
        var outputPath = Path.Combine(outputDirectory, outputName);
        var proofGlbPath = Path.Combine(
            outputDirectory, Safe(recon.ObjectName) + ".native-decode-proof.glb");
        var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        WriteBytesAtomic(outputPath, encoded);
        var proofBuild = new ArkiveSkeletalMeshGlbWriter().Build(recon, decodedEncoded);
        WriteBytesAtomic(proofGlbPath, proofBuild.Bytes);
        watch.Stop();
        var manifest = new SkeletalMeshNativeEncodingManifest(
            CurrentSchemaVersion, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            edit.Manifest.EditId, edit.Manifest.DisplayName, Path.GetFullPath(edit.ManifestPath),
            sourceManifest.ExportId, sourceManifestPath, packagePath, recon.PhysicalPackageName,
            recon.PackageAlias, recon.ObjectName, recon.NativeExportIndex!.Value,
            recon.SerialOffset, recon.SerialSize, recon.PackageSha256, sourceHash,
            preflight.CandidateGlbSha256, SkeletalMeshSemanticDecoder.Sha(encoded),
            Path.GetFileName(outputPath), SkeletalMeshSemanticDecoder.Sha(proofBuild.Bytes),
            Path.GetFileName(proofGlbPath), boundsUpdated, allowlisted.LongCount(value => value),
            actualChanged, changedLocked, lockedSourceHash, lockedEncodedHash, true, true,
            lodEvidence, preflight, watch.Elapsed.TotalSeconds);
        WriteJsonAtomic(manifestPath, manifest);
        return new SkeletalMeshNativeEncodingResult(
            outputDirectory, outputPath, proofGlbPath, manifestPath, manifest, watch.Elapsed);
    }

    private static void ValidateIdentity(
        SkeletalMeshEditableExportManifest source,
        SkeletalMeshReconnaissanceReport recon)
    {
        if (recon.SchemaVersion != SkeletalMeshReconnaissanceService.CurrentSchemaVersion ||
            recon.Readiness != SkeletalMeshReadiness.NoOpReady || recon.NativeLayout is null ||
            recon.NativeExportIndex is null)
            Refuse("SMN050", "The source reconnaissance report is not ready for native encoding.");
        if (recon.PackageProfile.FileVersion != PackageConstants.WesternFileVersion ||
            recon.PackageProfile.LicenseeVersion != PackageConstants.WesternLicenseeVersion ||
            recon.PackageProfile.CompressionFlags != PackageConstants.WesternCompressionFlags)
            Refuse("SMN051", "Only the Western Lost Ark 868/16 package profile is authorized.");
        if (source.NativeExportIndex != recon.NativeExportIndex || source.SerialOffset != recon.SerialOffset ||
            source.SerialSize != recon.SerialSize ||
            !source.SourcePackageSha256.Equals(recon.PackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !source.SourceExportSha256.Equals(recon.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            !source.NativeParserProfile.Equals(recon.NativeLayout.ParserProfile, StringComparison.Ordinal))
            Refuse("SMN052", "The editable export and native reconnaissance identities differ.");
    }

    private static Vector2[] BuildUvChannel(
        SkeletalMeshGlbLod candidate,
        IReadOnlyList<Vector2> source,
        int vertexCount)
    {
        var result = source.ToArray();
        foreach (var (stableId, vertex) in candidate.Vertices)
        {
            if (stableId >= vertexCount || vertex.TextureCoordinates.Count == 0)
                Refuse("SMN060", $"LOD {candidate.LodIndex} stable vertex {stableId} has no native UV0.");
            result[checked((int)stableId)] = vertex.TextureCoordinates[0];
        }
        return result;
    }

    private static Vector4[] ReconstructTangents(
        IReadOnlyList<uint> indices,
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<Vector2> uvs,
        int lodIndex)
    {
        var tangentSum = new Vector3[positions.Count];
        var bitangentSum = new Vector3[positions.Count];
        for (var index = 0; index + 2 < indices.Count; index += 3)
        {
            var a = checked((int)indices[index]);
            var b = checked((int)indices[index + 1]);
            var c = checked((int)indices[index + 2]);
            var edge1 = positions[b] - positions[a];
            var edge2 = positions[c] - positions[a];
            var delta1 = uvs[b] - uvs[a];
            var delta2 = uvs[c] - uvs[a];
            var determinant = delta1.X * delta2.Y - delta1.Y * delta2.X;
            if (!float.IsFinite(determinant) || MathF.Abs(determinant) < 1e-12f) continue;
            var inverse = 1f / determinant;
            var tangent = (edge1 * delta2.Y - edge2 * delta1.Y) * inverse;
            var bitangent = (edge2 * delta1.X - edge1 * delta2.X) * inverse;
            if (!IsFinite(tangent) || !IsFinite(bitangent)) continue;
            tangentSum[a] += tangent; tangentSum[b] += tangent; tangentSum[c] += tangent;
            bitangentSum[a] += bitangent; bitangentSum[b] += bitangent; bitangentSum[c] += bitangent;
        }
        var result = new Vector4[positions.Count];
        for (var vertex = 0; vertex < result.Length; vertex++)
        {
            var normal = normals[vertex];
            var tangent = tangentSum[vertex] - normal * Vector3.Dot(normal, tangentSum[vertex]);
            if (!TryNormalize(tangent, out tangent))
            {
                tangent = MathF.Abs(normal.Z) < 0.999f
                    ? Vector3.Cross(Vector3.UnitZ, normal)
                    : Vector3.Cross(Vector3.UnitY, normal);
                if (!TryNormalize(tangent, out tangent))
                    Refuse("SMN061", $"LOD {lodIndex} vertex {vertex} has no reconstructable tangent.");
            }
            var handedness = Vector3.Dot(Vector3.Cross(normal, tangent), bitangentSum[vertex]) < 0f
                ? -1f : 1f;
            result[vertex] = new Vector4(tangent, handedness);
        }
        return result;
    }

    private static Vector4 OrthonormalizeTangent(
        Vector3 normal,
        Vector4 value,
        int lodIndex,
        uint stableId)
    {
        var tangent = new Vector3(value.X, value.Y, value.Z);
        tangent -= normal * Vector3.Dot(normal, tangent);
        if (!TryNormalize(tangent, out tangent))
            Refuse("SMN062", $"LOD {lodIndex} stable vertex {stableId} has no usable tangent.");
        return new Vector4(tangent, value.W < 0f ? -1f : 1f);
    }

    private static void WriteNativePosition(
        Span<byte> bytes,
        SkeletalMeshNativeLodEvidence lod,
        int vertexIndex,
        Vector3 gltf)
    {
        if (!IsFinite(gltf)) Refuse("SMN070", "A candidate position is non-finite.");
        var offset = checked((int)lod.VertexBuffer.DataRelativeOffset +
                             vertexIndex * lod.VertexBuffer.ElementSize + lod.VertexPositionOffset);
        WriteSingle(bytes, offset, gltf.X * 100f);
        WriteSingle(bytes, offset + 4, gltf.Z * 100f);
        WriteSingle(bytes, offset + 8, gltf.Y * 100f);
    }

    private static void WriteNativeFrame(
        Span<byte> bytes,
        SkeletalMeshNativeLodEvidence lod,
        int vertexIndex,
        Vector3 gltfNormal,
        Vector4 gltfTangent)
    {
        var offset = checked((int)lod.VertexBuffer.DataRelativeOffset +
                             vertexIndex * lod.VertexBuffer.ElementSize);
        var nativeTangent = new Vector3(gltfTangent.X, gltfTangent.Z, gltfTangent.Y);
        var nativeNormal = new Vector3(gltfNormal.X, gltfNormal.Z, gltfNormal.Y);
        bytes[offset] = PackNormal(nativeTangent.X);
        bytes[offset + 1] = PackNormal(nativeTangent.Y);
        bytes[offset + 2] = PackNormal(nativeTangent.Z);
        // TangentX.W is not consumed by Arkive's proven UE3 frame decoder; retain it.
        bytes[offset + 4] = PackNormal(nativeNormal.X);
        bytes[offset + 5] = PackNormal(nativeNormal.Y);
        bytes[offset + 6] = PackNormal(nativeNormal.Z);
        bytes[offset + 7] = gltfTangent.W < 0f ? (byte)0 : (byte)255;
    }

    private static byte PackNormal(float value) => checked((byte)Math.Clamp(
        (int)MathF.Round(Math.Clamp(value, -1f, 1f) * 127f + 128f), 0, 255));

    private static void WriteBounds(Span<byte> bytes, int offset, IReadOnlyList<Vector3> positions)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var native = new Vector3[positions.Count];
        for (var index = 0; index < positions.Count; index++)
        {
            var value = positions[index];
            native[index] = new Vector3(value.X * 100f, value.Z * 100f, value.Y * 100f);
            minimum = Vector3.Min(minimum, native[index]);
            maximum = Vector3.Max(maximum, native[index]);
        }
        var origin = (minimum + maximum) * 0.5f;
        var extent = (maximum - minimum) * 0.5f;
        var radius = 0f;
        foreach (var value in native) radius = Math.Max(radius, Vector3.Distance(origin, value));
        WriteSingle(bytes, offset, origin.X); WriteSingle(bytes, offset + 4, origin.Y);
        WriteSingle(bytes, offset + 8, origin.Z); WriteSingle(bytes, offset + 12, extent.X);
        WriteSingle(bytes, offset + 16, extent.Y); WriteSingle(bytes, offset + 20, extent.Z);
        WriteSingle(bytes, offset + 24, radius);
    }

    private static void MarkVertexField(
        bool[] allowlisted,
        SkeletalMeshNativeLodEvidence lod,
        int vertex,
        int fieldOffset,
        int bytes) =>
        Mark(allowlisted, checked((int)lod.VertexBuffer.DataRelativeOffset +
                                  vertex * lod.VertexBuffer.ElementSize + fieldOffset), bytes);

    private static void Mark(bool[] values, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > values.Length - count)
            Refuse("SMN071", "An encoding allowlist range is outside the export payload.");
        Array.Fill(values, true, offset, count);
    }

    private static double MaximumDistance(IReadOnlyList<Vector3> left, IReadOnlyList<Vector3> right)
    {
        if (left.Count != right.Count) return double.PositiveInfinity;
        var maximum = 0d;
        for (var index = 0; index < left.Count; index++)
            maximum = Math.Max(maximum, Vector3.Distance(left[index], right[index]));
        return maximum;
    }

    private static double MaximumTangentDistance(IReadOnlyList<Vector4> left, IReadOnlyList<Vector4> right)
    {
        if (left.Count != right.Count) return double.PositiveInfinity;
        var maximum = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            var direction = Vector3.Distance(
                new Vector3(left[index].X, left[index].Y, left[index].Z),
                new Vector3(right[index].X, right[index].Y, right[index].Z));
            var sign = (left[index].W < 0f) == (right[index].W < 0f) ? 0d : 2d;
            maximum = Math.Max(maximum, Math.Max(direction, sign));
        }
        return maximum;
    }

    private static bool TryNormalize(Vector3 value, out Vector3 normalized)
    {
        var lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-12f)
        {
            normalized = default;
            return false;
        }
        normalized = value / MathF.Sqrt(lengthSquared);
        return IsFinite(normalized);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static void WriteSingle(Span<byte> bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], BitConverter.SingleToInt32Bits(value));

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, token).ConfigureAwait(false) ??
               throw new InvalidDataException($"{Path.GetFileName(path)} is empty.");
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ContainedPath(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) +
                   Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            Refuse("SMN080", $"A manifest path escapes its source directory: {full}");
        return full;
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
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "skeletal-mesh" : result;
    }

    [DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new SkeletalMeshNativeEncodingRefusalException(code, message);
}
