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
/// Compiles a preflight-approved replacement LOD0 into every existing native
/// LOD slot. The variable-size export is decoded back through Arkive's
/// independent parser/decoder and may then enter the isolated append-and-retarget
/// package writer. Installation remains a later, separately gated operation.
/// </summary>
public sealed class SkeletalMeshTopologyCompilerService
{
    public const int CurrentSchemaVersion = 2;
    public const string ManifestFileName = "arkive-skeletalmesh-topology-compilation.json";
    private const double PositionTolerance = 2e-6;
    private const double FrameTolerance = 0.025;
    private const double FullPrecisionUvTolerance = 2e-5;
    private const double HalfPrecisionUvTolerance = 0.02;
    private const double WeightTolerance = 2e-6;

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

    public SkeletalMeshTopologyCompilerService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(summaryReader, _protectionProvider);
    }

    public static async Task<SkeletalMeshTopologyCompilationManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadJsonAsync<SkeletalMeshTopologyCompilationManifest>(
            Path.GetFullPath(path), cancellationToken).ConfigureAwait(false);
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            Refuse("SMC082", $"Topology compiler schema {manifest.SchemaVersion} is unsupported; " +
                              $"Arkive requires schema {CurrentSchemaVersion}.");
        return manifest;
    }

    public async Task<SkeletalMeshTopologyCompilationResult> CompileAsync(
        SkeletalMeshTopologyCompilationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Edit);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var edit = request.Edit;
        if (edit.Manifest.SchemaVersion != SkeletalMeshEditWorkspaceStore.CurrentSchemaVersion)
            Refuse("SMC001", "The edited-GLB registration schema is stale; register the edit again.");

        var sourceManifestPath = Path.GetFullPath(edit.Manifest.SourceManifestPath);
        var sourceManifest = await ReadJsonAsync<SkeletalMeshEditableExportManifest>(
            sourceManifestPath, cancellationToken).ConfigureAwait(false);
        if (sourceManifest.SchemaVersion != SkeletalMeshEditableExportService.CurrentSchemaVersion)
            Refuse("SMC002", "Topology compilation requires a current editable GLB export.");
        if (!sourceManifest.ExportId.Equals(edit.Manifest.SourceExportId, StringComparison.OrdinalIgnoreCase) ||
            !sourceManifest.PackageAlias.Equals(edit.Manifest.PackageAlias, StringComparison.OrdinalIgnoreCase) ||
            !sourceManifest.ObjectName.Equals(edit.Manifest.ObjectName, StringComparison.OrdinalIgnoreCase))
            Refuse("SMC003", "The edit registration no longer matches its source editable export.");

        var sourceDirectory = Path.GetDirectoryName(sourceManifestPath) ??
                              throw new InvalidDataException("The source manifest directory is unavailable.");
        var sourceGlbPath = ContainedPath(sourceDirectory,
            Path.Combine(sourceDirectory, sourceManifest.GlbRelativePath));
        var sourceGlb = new FileInfo(sourceGlbPath);
        var sourceEntry = new SkeletalMeshEditableWorkspaceEntry(
            sourceDirectory, sourceManifestPath, sourceGlbPath, sourceManifest,
            sourceGlb.Exists, sourceGlb.Exists && sourceGlb.Length == sourceManifest.GlbBytes, true);
        if (!sourceEntry.GlbExists || !sourceEntry.GlbLengthMatches)
            Refuse("SMC004", "The source editable GLB is missing or its length changed.");

        var preflight = await _preflight.InspectAsync(
            sourceEntry, edit.EditedGlbPath, cancellationToken).ConfigureAwait(false);
        if (preflight.Readiness != SkeletalMeshEditReadiness.ReadyForTopologyCompilation ||
            preflight.ImportProfile != SkeletalMeshImportProfile.ReplaceLod0AndDuplicate ||
            preflight.ErrorCount != 0 || preflight.Topology is null ||
            preflight.SchemaVersion != SkeletalMeshEditPreflightService.CurrentSchemaVersion ||
            preflight.AnimationValidation is not { Passed: true })
            Refuse("SMC005", "The edited GLB no longer passes the topology-capable LOD0 replacement preflight.");
        if (!preflight.CandidateGlbSha256.Equals(
                edit.Manifest.EditedGlbSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SMC006", "The registered edited GLB changed; register it again before compilation.");

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
            Refuse("SMC010", "The source package identity metadata changed; rerun Native mesh recon.");
        var packageHash = await ComputeFileSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
        if (!packageHash.Equals(recon.PackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SMC011", "The source package SHA-256 changed; rerun Native mesh recon.");

        var catalog = _catalogReader.Read(packagePath);
        var asset = catalog.Assets.SingleOrDefault(item => item.ExportIndex == recon.NativeExportIndex);
        if (asset is null || asset.SerialOffset != recon.SerialOffset || asset.SerialSize != recon.SerialSize ||
            !asset.ObjectName.Equals(recon.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            !asset.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            Refuse("SMC012", "The current export table no longer matches the mesh reconnaissance report.");

        var sourceBytes = new byte[checked((int)recon.SerialSize)];
        using (var logical = new LogicalPackageStream(catalog.Summary, _protectionProvider))
        {
            logical.Position = recon.SerialOffset;
            logical.ReadExactly(sourceBytes);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var sourceHash = Sha(sourceBytes);
        if (!sourceHash.Equals(recon.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            !sourceHash.Equals(sourceManifest.SourceExportSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SMC013", "The source native export hash changed; rerun Native mesh recon and export.");

        var candidateDocument = SkeletalMeshGlbSemanticReader.Read(
            edit.EditedGlbPath, sourceManifest, SkeletalMeshGlbReadProfile.TopologyCandidate);
        if (!candidateDocument.Lods.TryGetValue(0, out var candidate))
            Refuse("SMC020", "The topology candidate has no compiled LOD0.");
        candidate = FitCandidateToSource(candidate, layout, sourceBytes);
        var materialNames = BuildMaterialNames(recon, layout);
        var usedReferences = candidateDocument.MaterialReferences.Where(pair => candidate.UsedMaterials.Contains(pair.Key))
            .ToDictionary(StringComparer.OrdinalIgnoreCase);
        (IReadOnlyDictionary<string, int> Slots, IReadOnlyList<SkeletalMeshMaterialReference?> Added,
            IReadOnlyDictionary<int, SkeletalMeshMaterialReference> Replaced) materialPlan;
        try
        {
            materialPlan = SkeletalMeshMaterialReferences.Plan(materialNames,
                candidateDocument.MaterialNames.Where(usedReferences.ContainsKey).ToArray(), usedReferences);
        }
        catch (InvalidDataException exception)
        {
            throw new SkeletalMeshTopologyCompilationRefusalException("SMC024", exception.Message);
        }
        var materialSlots = materialPlan.Slots;
        var tangentFrames = BuildTangentFrames(candidate);

        var lodCountRange = layout.Ranges.SingleOrDefault(range => range.Name == "LOD array count") ??
                            throw new SkeletalMeshTopologyCompilationRefusalException(
                                "SMC021", "The parsed source layout has no LOD array boundary.");
        var boundsRange = layout.Ranges.SingleOrDefault(range => range.Name == "Bounds") ??
                          throw new SkeletalMeshTopologyCompilationRefusalException(
                              "SMC022", "The parsed source layout has no native bounds range.");
        if (boundsRange.Size != 28)
            Refuse("SMC022", $"The source bounds range is {boundsRange.Size} bytes, not the proven 28 bytes.");
        // The compiled mesh is appended at the end of this package; its LOD offsets are written for that place.
        var plannedSerialOffset = AlignUp(catalog.Summary.CompressedChunks.Count > 0
            ? (long)catalog.Summary.CompressedChunks[^1].UncompressedEnd
            : catalog.Summary.PhysicalFileSize, 4);
        var prefix = PlanPrefix(sourceBytes, recon, layout, catalog, materialPlan.Added, materialPlan.Replaced, lodCountRange);
        using var compiledStream = new MemoryStream();
        compiledStream.Write(prefix.Bytes);
        using (var writer = new BinaryWriter(compiledStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(layout.Lods.Count);
            var plans = new List<CompiledLodPlan>(layout.Lods.Count);
            var lodStarts = new List<long>(layout.Lods.Count);
            foreach (var sourceLod in layout.Lods.OrderBy(item => item.LodIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plan = BuildLodPlan(candidate, tangentFrames, sourceLod, layout, materialSlots, recon);
                writer.Flush();
                lodStarts.Add(compiledStream.Position);
                WriteLod(writer, sourceBytes, sourceLod, plan);
                plans.Add(plan);
            }

            var trailingOffset = checked((int)layout.ParsedEndRelativeOffset);
            var trailing = TrailingWithMaterials(sourceBytes.AsSpan(trailingOffset), layout.MaterialReferenceCount,
                materialPlan.Added.Count) ?? sourceBytes.AsSpan(trailingOffset).ToArray();
            writer.Write(trailing);
            writer.Flush();
            var compiledBytes = compiledStream.ToArray();
            var compiledBoundsOffset = checked((int)boundsRange.RelativeOffset + prefix.PropertyGrowth);
            WriteBounds(compiledBytes, compiledBoundsOffset,
                candidate.Vertices.Values.Select(vertex => vertex.Position).ToArray());
            var lodOffsetsRewritten = prefix.ExtensionValuesOffset is not null;
            if (lodOffsetsRewritten)
                for (var index = 0; index < lodStarts.Count; index++)
                    BinaryPrimitives.WriteInt32LittleEndian(compiledBytes.AsSpan(prefix.ExtensionValuesOffset!.Value + index * 4),
                        checked((int)(plannedSerialOffset + lodStarts[index])));

            var hasVertexColours = layout.Lods[0].VertexColorBuffer is not null;
            var compiledNativeOffset = checked((int)layout.NativeDataRelativeOffset + prefix.PropertyGrowth);
            if (!new SkeletalMeshNativeLayoutParser().TryParse(
                    compiledBytes, plannedSerialOffset, compiledNativeOffset,
                    catalog.Names, hasVertexColours, out var compiledLayout, out var parseRefusal) ||
                compiledLayout is null)
                Refuse("SMC030", "The compiler output did not parse as a supported native SkeletalMesh: " + parseRefusal);
            if (compiledLayout.ParserProfile != layout.ParserProfile ||
                compiledLayout.Lods.Count != layout.Lods.Count)
                Refuse("SMC031", "Decode-back changed the native parser profile or LOD-slot count.");
            if (!compiledLayout.MaterialReferences.SequenceEqual(prefix.Materials))
                Refuse("SMC034", "Decode-back did not find the planned material list.");
            if (lodOffsetsRewritten && !compiledLayout.LostArkExtensionValues.SequenceEqual(
                    compiledLayout.Lods.Select(lod => checked((int)(plannedSerialOffset + lod.RelativeOffset)))))
                Refuse("SMC035", "Decode-back did not find each LOD where the mesh's LOD offsets point.");

            var decoded = new SkeletalMeshSemanticDecoder().Decode(compiledBytes, compiledLayout);
            var lodEvidence = new List<SkeletalMeshTopologyCompiledLodEvidence>(plans.Count);
            for (var index = 0; index < plans.Count; index++)
                lodEvidence.Add(VerifyDecodedLod(candidate, plans[index], decoded[index]));

            var sourceTrailing = trailing.AsSpan();
            var compiledTrailingOffset = checked((int)compiledLayout.ParsedEndRelativeOffset);
            var compiledTrailing = compiledBytes.AsSpan(compiledTrailingOffset);
            var trailingPreserved = sourceTrailing.SequenceEqual(compiledTrailing);
            if (!trailingPreserved)
                Refuse("SMC032", "Opaque trailing native bytes were not preserved exactly.");
            // The prefix is the source's, except the bounds and the planned material list, cloth list and LOD offsets.
            var expectedPrefix = prefix.Bytes.ToArray();
            if (lodOffsetsRewritten)
                compiledBytes.AsSpan(prefix.ExtensionValuesOffset!.Value, lodStarts.Count * 4)
                    .CopyTo(expectedPrefix.AsSpan(prefix.ExtensionValuesOffset.Value));
            var prefixPreserved = PrefixEqualsExceptBounds(
                expectedPrefix, compiledBytes.AsSpan(0, expectedPrefix.Length), compiledBoundsOffset, checked((int)boundsRange.Size));
            if (!prefixPreserved)
                Refuse("SMC033", "The native prefix changed outside the bounds and the planned material and LOD offset fields.");
            var prefixBytes = expectedPrefix.Length;

            var compiledHash = Sha(compiledBytes);
            var updatedRecon = recon with
            {
                SerialSize = compiledBytes.Length,
                ExportPayloadSha256 = compiledHash,
                NativeDataRelativeOffset = compiledNativeOffset,
                NativeDataSize = compiledBytes.Length - compiledNativeOffset,
                NativeDataSha256 = Sha(compiledBytes.AsSpan(compiledNativeOffset)),
                NativeLayout = compiledLayout,
            };
            var proofBuild = new ArkiveSkeletalMeshGlbWriter().Build(updatedRecon, decoded);

            var outputDirectory = Path.GetFullPath(request.OutputDirectory);
            Directory.CreateDirectory(outputDirectory);
            var safeName = Safe(recon.ObjectName);
            var compiledPath = Path.Combine(outputDirectory, safeName + ".skeletalmesh.topology.bin");
            var proofPath = Path.Combine(outputDirectory, safeName + ".topology-decode-proof.glb");
            var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
            WriteBytesAtomic(compiledPath, compiledBytes);
            WriteBytesAtomic(proofPath, proofBuild.Bytes);
            watch.Stop();

            var lockedPrefix = expectedPrefix.ToArray();
            Array.Clear(lockedPrefix, compiledBoundsOffset, checked((int)boundsRange.Size));
            var manifest = new SkeletalMeshTopologyCompilationManifest(
                CurrentSchemaVersion, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                edit.Manifest.EditId, edit.Manifest.DisplayName, Path.GetFullPath(edit.ManifestPath),
                sourceManifest.ExportId, sourceManifestPath, packagePath, recon.PhysicalPackageName,
                recon.PackageAlias, recon.ObjectName, recon.NativeExportIndex!.Value,
                recon.SerialOffset, sourceBytes.Length, compiledBytes.Length,
                compiledBytes.LongLength - sourceBytes.LongLength, recon.PackageSha256, sourceHash,
                preflight.CandidateGlbSha256, compiledHash, Path.GetFileName(compiledPath),
                Sha(proofBuild.Bytes), Path.GetFileName(proofPath), compiledLayout.ParserProfile,
                Sha(lockedPrefix), Sha(sourceTrailing), prefixBytes, sourceTrailing.Length,
                true, prefixPreserved, trailingPreserved, true, true,
                lodEvidence, preflight, watch.Elapsed.TotalSeconds)
            {
                PlannedSerialOffset = plannedSerialOffset,
                LodOffsetsRewritten = lodOffsetsRewritten,
                MaterialSlots = materialPlan.Replaced.Select(pair => new SkeletalMeshCompiledMaterialSlot(
                        pair.Key, pair.Value.Material.Path, prefix.Materials[pair.Key], false))
                    .Concat(materialPlan.Added.Select((reference, index) => new SkeletalMeshCompiledMaterialSlot(
                        layout.MaterialReferenceCount + index, reference!.Material.Path,
                        prefix.Materials[layout.MaterialReferenceCount + index], true)))
                    .OrderBy(slot => slot.Slot).ToArray(),
            };
            WriteJsonAtomic(manifestPath, manifest);
            return new SkeletalMeshTopologyCompilationResult(
                outputDirectory, compiledPath, proofPath, manifestPath, manifest, watch.Elapsed);
        }
    }

    private static CompiledLodPlan BuildLodPlan(
        SkeletalMeshGlbLod candidate,
        IReadOnlyDictionary<uint, Vector4> tangentFrames,
        SkeletalMeshNativeLodEvidence source,
        SkeletalMeshNativeLayoutEvidence layout,
        IReadOnlyDictionary<string, int> materialSlots,
        SkeletalMeshReconnaissanceReport recon)
    {
        if (source.AdjacencyBuffer.ElementCount != 0)
            Refuse("SMC042", $"Source LOD {source.LodIndex} has a non-empty adjacency buffer; adjacency generation is not proven.");
        var legacyRange = source.Ranges.SingleOrDefault(range =>
            range.Name.StartsWith($"LOD {source.LodIndex} legacy bulk", StringComparison.Ordinal));
        if (legacyRange is null || legacyRange.Size != 16)
            Refuse("SMC043", $"Source LOD {source.LodIndex} has an unsupported legacy bulk payload.");

        var capacity = source.VertexPositionOffset == 20 ? 4_096 : 256;
        // Within a material the triangles keep the GLB's order: a translucent material draws them in that order, and the
        // layers of a sheer garment are authored inside to outside.
        var triangles = candidate.Triangles
            .SelectMany(pair => Enumerable.Repeat(pair.Key, pair.Value))
            .Select(triangle => new CandidateTriangle(
                triangle.A, triangle.B, triangle.C, triangle.Material,
                materialSlots.TryGetValue(triangle.Material, out var slot) ? slot : -1))
            .OrderBy(item => item.MaterialIndex).ToArray();
        if (triangles.Any(item => item.MaterialIndex < 0))
            Refuse("SMC044", "A candidate material no longer maps to an original native slot.");

        var partitions = new List<TrianglePartition>();
        foreach (var materialGroup in triangles.GroupBy(item => (item.MaterialIndex, item.Material)))
        {
            TrianglePartition? current = null;
            foreach (var triangle in materialGroup)
            {
                var bones = TriangleBones(triangle, candidate);
                if (bones.Count > capacity)
                    Refuse("SMC045", $"A triangle assigned to {triangle.Material} needs {bones.Count} bones; " +
                                      $"the target LOD's chunk-local capacity is {capacity}.");
                if (current is null || current.Bones.Union(bones).Count() > capacity)
                {
                    current = new TrianglePartition(triangle.MaterialIndex, triangle.Material, [], []);
                    partitions.Add(current);
                }
                current.Triangles.Add(triangle);
                current.Bones.UnionWith(bones);
            }
        }
        if (partitions.Count > 1_024 || partitions.Count > short.MaxValue)
            Refuse("SMC046", $"Compilation requires {partitions.Count:N0} chunks/sections, exceeding the bounded profile.");

        var sourceFlags = source.Sections.GroupBy(section => (int)section.MaterialIndex)
            .ToDictionary(group => group.Key, group => group.First().Flags);
        var compiledVertices = new List<CompiledVertex>();
        var indices = new List<uint>();
        var sections = new List<CompiledSection>();
        var chunks = new List<CompiledChunk>();
        var maximumQuantizationError = 0d;
        var maximumInfluences = 0;
        foreach (var partition in partitions)
        {
            var ids = partition.Triangles.SelectMany(item => new[] { item.A, item.B, item.C })
                .Distinct().OrderBy(id => candidate.Vertices[id].SkinWeights.Count == 1 ? 0 : 1)
                .ThenBy(id => id).ToArray();
            var firstVertex = compiledVertices.Count;
            var compiledByCandidate = new Dictionary<uint, uint>();
            var rigid = 0;
            var soft = 0;
            foreach (var id in ids)
            {
                var vertex = candidate.Vertices[id];
                var quantized = QuantizeWeights(vertex.SkinWeights);
                var error = WeightQuantizationError(vertex.SkinWeights, quantized);
                maximumQuantizationError = Math.Max(maximumQuantizationError, error);
                maximumInfluences = Math.Max(maximumInfluences, quantized.Count);
                if (quantized.Count == 1) rigid++; else soft++;
                var normal = Normalize(vertex.Normal ?? default, "normal", source.LodIndex, id);
                var tangent = OrthonormalizeTangent(normal, tangentFrames[id]);
                // FitCandidateToSource gave every vertex a colour when the game mesh has a colour stream.
                var color = source.VertexColorBuffer is null || vertex.Colour is null ? null : EncodeColour(vertex.Colour.Value);
                var compiled = new CompiledVertex(id, vertex.Position, normal, tangent,
                    FitTextureCoordinates(vertex.TextureCoordinates, source.TextureCoordinateCount), quantized, color);
                compiledByCandidate[id] = checked((uint)compiledVertices.Count);
                compiledVertices.Add(compiled);
            }
            var firstIndex = indices.Count;
            foreach (var triangle in partition.Triangles)
            {
                indices.Add(compiledByCandidate[triangle.A]);
                indices.Add(compiledByCandidate[triangle.B]);
                indices.Add(compiledByCandidate[triangle.C]);
            }
            var chunkIndex = chunks.Count;
            chunks.Add(new CompiledChunk(
                chunkIndex, firstVertex, rigid, soft, ids
                    .Max(id => candidate.Vertices[id].SkinWeights.Count), partition.Bones.Order().ToArray()));
            sections.Add(new CompiledSection(
                sections.Count, checked((short)partition.MaterialIndex), checked((short)chunkIndex),
                firstIndex, partition.Triangles.Count, sourceFlags.GetValueOrDefault(partition.MaterialIndex),
                partition.Material));
        }
        if (compiledVertices.Count <= 0 || indices.Count != candidate.TriangleCount * 3)
            Refuse("SMC047", "The compiler did not produce a complete vertex/index stream.");

        var closure = BoneClosure(
            compiledVertices.SelectMany(vertex => vertex.Influences.Select(influence => influence.Bone)), layout);
        var active = source.ActiveBones.Select(value => (int)value).Concat(closure).Distinct().Order().ToArray();
        var required = source.RequiredBones.Select(value => (int)value).Concat(closure).Distinct().Order().ToArray();
        if (active.Any(value => value < 0 || value >= layout.BoneCount) ||
            required.Any(value => value < 0 || value >= layout.BoneCount))
            Refuse("SMC048", $"Source LOD {source.LodIndex} contains an invalid active/required bone.");
        if (source.RequiredBoneElementSize == 1 && required.Any(value => value > byte.MaxValue))
            Refuse("SMC049", $"Source LOD {source.LodIndex}'s byte-wide required-bone list cannot represent the candidate skeleton use.");
        return new CompiledLodPlan(source, compiledVertices, indices, sections, chunks,
            active.Select(value => checked((short)value)).ToArray(),
            required.Select(value => checked((short)value)).ToArray(),
            maximumQuantizationError, maximumInfluences,
            checked(compiledVertices.Count - candidate.Vertices.Count));
    }

    private static void WriteLod(
        BinaryWriter writer,
        ReadOnlySpan<byte> sourceBytes,
        SkeletalMeshNativeLodEvidence source,
        CompiledLodPlan plan)
    {
        writer.Write(plan.Sections.Count);
        foreach (var section in plan.Sections)
        {
            writer.Write(section.MaterialIndex); writer.Write(section.ChunkIndex);
            writer.Write(section.FirstIndex); writer.Write(section.TriangleCount); writer.Write(section.Flags);
        }

        var indexElementSize = plan.Vertices.Count > ushort.MaxValue ? 4 : 2;
        var indexRevision = ReadInt32(sourceBytes, checked((int)source.IndexBuffer.HeaderRelativeOffset));
        writer.Write(indexRevision); writer.Write((byte)indexElementSize);
        writer.Write(indexElementSize); writer.Write(plan.Indices.Count);
        foreach (var index in plan.Indices)
        {
            if (indexElementSize == 2) writer.Write(checked((ushort)index));
            else writer.Write(index);
        }

        writer.Write(plan.ActiveBones.Count);
        foreach (var bone in plan.ActiveBones) writer.Write(bone);
        writer.Write(plan.Chunks.Count);
        foreach (var chunk in plan.Chunks)
        {
            writer.Write(chunk.FirstVertex);
            writer.Write(0); writer.Write(0); // no CPU rigid/soft arrays
            writer.Write(chunk.BoneMap.Count);
            foreach (var bone in chunk.BoneMap) writer.Write(checked((short)bone));
            writer.Write(chunk.RigidVertexCount); writer.Write(chunk.SoftVertexCount);
            writer.Write(chunk.MaximumInfluences);
        }

        var counts = source.Ranges.Single(range =>
            range.Name == $"LOD {source.LodIndex} counts and required bones");
        writer.Write(ReadInt32(sourceBytes, checked((int)counts.RelativeOffset))); // preserve LOD size field
        writer.Write(plan.Vertices.Count);
        if (source.LostArkPreRequiredBonesValue is { } preRequired) writer.Write(preRequired);
        writer.Write(plan.RequiredBones.Count);
        foreach (var bone in plan.RequiredBones)
        {
            if (source.RequiredBoneElementSize == 1) writer.Write(checked((byte)bone));
            else writer.Write(bone);
        }

        var legacy = source.Ranges.Single(range =>
            range.Name.StartsWith($"LOD {source.LodIndex} legacy bulk", StringComparison.Ordinal));
        var legacyBytes = sourceBytes.Slice(checked((int)legacy.RelativeOffset), checked((int)legacy.Size));
        if (ReadInt32(legacyBytes, 4) != 0 || ReadInt32(legacyBytes, 8) != 0)
            Refuse("SMC050", $"Source LOD {source.LodIndex} has a non-empty legacy bulk payload.");
        writer.Write(legacyBytes);

        writer.Write(source.TextureCoordinateCount);
        writer.Write(source.TextureCoordinateCount);
        writer.Write(source.UsesFullPrecisionUvs ? 1 : 0);
        writer.Write(source.SerializedPackedPosition ? 1 : 0);
        var gpuMetadata = source.Ranges.Single(range => range.Name == $"LOD {source.LodIndex} GPU metadata");
        if (gpuMetadata.Size != 36)
            Refuse("SMC051", $"Source LOD {source.LodIndex} has an unsupported GPU metadata header.");
        writer.Write(sourceBytes.Slice(checked((int)gpuMetadata.RelativeOffset + 12), 24));

        var stride = 28 + source.TextureCoordinateCount * (source.UsesFullPrecisionUvs ? 8 : 4) +
                     (source.VertexPositionOffset == 20 ? 4 : 0);
        writer.Write(stride); writer.Write(plan.Vertices.Count);
        for (var vertexIndex = 0; vertexIndex < plan.Vertices.Count; vertexIndex++)
            WriteVertex(writer, plan.Vertices[vertexIndex], vertexIndex, plan, source);

        if (source.VertexColorBuffer is not null)
        {
            writer.Write(4); writer.Write(plan.Vertices.Count);
            foreach (var vertex in plan.Vertices)
            {
                var rgba = vertex.Colour ?? throw new SkeletalMeshTopologyCompilationRefusalException(
                    "SMC052", "A required vertex colour is unavailable.");
                writer.Write(rgba[2]); writer.Write(rgba[1]); writer.Write(rgba[0]); writer.Write(rgba[3]);
            }
        }
        writer.Write(0); // no extra influence sets

        var adjacencyRevision = ReadInt32(sourceBytes, checked((int)source.AdjacencyBuffer.HeaderRelativeOffset));
        writer.Write(adjacencyRevision);
        writer.Write(checked((byte)source.AdjacencyBuffer.ElementSize));
        writer.Write(source.AdjacencyBuffer.ElementSize);
        writer.Write(0);
    }

    private static void WriteVertex(
        BinaryWriter writer,
        CompiledVertex vertex,
        int compiledIndex,
        CompiledLodPlan plan,
        SkeletalMeshNativeLodEvidence source)
    {
        var nativeTangent = new Vector3(vertex.Tangent.X, vertex.Tangent.Z, vertex.Tangent.Y);
        var nativeNormal = new Vector3(vertex.Normal.X, vertex.Normal.Z, vertex.Normal.Y);
        writer.Write(PackNormal(nativeTangent.X)); writer.Write(PackNormal(nativeTangent.Y));
        writer.Write(PackNormal(nativeTangent.Z)); writer.Write((byte)255);
        writer.Write(PackNormal(nativeNormal.X)); writer.Write(PackNormal(nativeNormal.Y));
        writer.Write(PackNormal(nativeNormal.Z)); writer.Write(vertex.Tangent.W < 0f ? (byte)0 : (byte)255);

        var chunk = plan.Chunks.Single(item => compiledIndex >= item.FirstVertex &&
            compiledIndex < item.FirstVertex + item.RigidVertexCount + item.SoftVertexCount);
        Span<ushort> localBones = stackalloc ushort[4];
        Span<byte> weights = stackalloc byte[4];
        for (var index = 0; index < vertex.Influences.Count; index++)
        {
            var influence = vertex.Influences[index];
            var localBone = -1;
            for (var boneIndex = 0; boneIndex < chunk.BoneMap.Count; boneIndex++)
                if (chunk.BoneMap[boneIndex] == influence.Bone) { localBone = boneIndex; break; }
            if (localBone < 0)
                Refuse("SMC054", $"Compiled vertex {compiledIndex} references a bone outside its chunk map.");
            localBones[index] = checked((ushort)localBone);
            weights[index] = influence.Weight;
        }
        if (source.VertexPositionOffset == 20)
            for (var index = 0; index < 4; index++) writer.Write(localBones[index]);
        else
            for (var index = 0; index < 4; index++) writer.Write(checked((byte)localBones[index]));
        for (var index = 0; index < 4; index++) writer.Write(weights[index]);

        writer.Write(vertex.Position.X * 100f);
        writer.Write(vertex.Position.Z * 100f);
        writer.Write(vertex.Position.Y * 100f);
        foreach (var uv in vertex.TextureCoordinates)
        {
            if (source.UsesFullPrecisionUvs)
            {
                writer.Write(uv.X); writer.Write(uv.Y);
            }
            else
            {
                var x = (Half)uv.X; var y = (Half)uv.Y;
                if (!Half.IsFinite(x) || !Half.IsFinite(y))
                    Refuse("SMC053", "A candidate UV cannot be represented by the target half-precision stream.");
                writer.Write(BitConverter.HalfToUInt16Bits(x));
                writer.Write(BitConverter.HalfToUInt16Bits(y));
            }
        }
    }

    private static SkeletalMeshTopologyCompiledLodEvidence VerifyDecodedLod(
        SkeletalMeshGlbLod candidate,
        CompiledLodPlan plan,
        DecodedSkeletalMeshLod decoded)
    {
        if (decoded.Positions.Length != plan.Vertices.Count || decoded.Indices.Length != plan.Indices.Count ||
            decoded.Evidence.Sections.Count != plan.Sections.Count ||
            decoded.Evidence.Chunks.Count != plan.Chunks.Count)
            Refuse("SMC060", $"LOD {decoded.Evidence.LodIndex} decode-back counts differ from the compiler plan.");
        var maxPosition = 0d; var maxNormal = 0d; var maxTangent = 0d;
        var maxUv = 0d; var maxColour = 0d; var maxWeight = 0d;
        var jointsExact = true;
        for (var index = 0; index < plan.Vertices.Count; index++)
        {
            var expected = plan.Vertices[index];
            maxPosition = Math.Max(maxPosition, Vector3.Distance(expected.Position, decoded.Positions[index]));
            maxNormal = Math.Max(maxNormal, Vector3.Distance(expected.Normal, decoded.Normals[index]));
            maxTangent = Math.Max(maxTangent, TangentDistance(expected.Tangent, decoded.Tangents[index]));
            for (var uv = 0; uv < expected.TextureCoordinates.Count; uv++)
                maxUv = Math.Max(maxUv, Vector2.Distance(
                    expected.TextureCoordinates[uv], decoded.TextureCoordinates[uv][index]));
            var expectedWeights = new float[4];
            var expectedJoints = new ushort[4];
            for (var influence = 0; influence < expected.Influences.Count; influence++)
            {
                expectedWeights[influence] = expected.Influences[influence].Weight / 255f;
                expectedJoints[influence] = checked((ushort)expected.Influences[influence].Bone);
            }
            var actualWeights = decoded.Weights[index];
            for (var influence = 0; influence < 4; influence++)
            {
                maxWeight = Math.Max(maxWeight, Math.Abs(actualWeights[influence] - expectedWeights[influence]));
                // A native chunk-local joint index in a zero-weight lane is
                // semantically inert. Depending on the chunk bone map it may
                // decode to a non-zero global joint even though both the
                // encoded and decoded weights remain zero. Compare identity
                // only for live influences; otherwise valid vertices with
                // fewer than four weights are spuriously refused.
                if (expectedWeights[influence] > 0f || actualWeights[influence] > 0f)
                    jointsExact &= decoded.Joints[index * 4 + influence] == expectedJoints[influence];
            }
            if (expected.Colour is { } expectedColour && decoded.Colors is { } actualColours)
                for (var channel = 0; channel < 4; channel++)
                    maxColour = Math.Max(maxColour,
                        Math.Abs(actualColours[index * 4 + channel] - expectedColour[channel]) / 255d);
        }
        var uvTolerance = plan.Source.UsesFullPrecisionUvs
            ? FullPrecisionUvTolerance : HalfPrecisionUvTolerance;
        if (maxPosition > PositionTolerance || maxNormal > FrameTolerance ||
            maxTangent > FrameTolerance || maxUv > uvTolerance || maxColour > 1e-9 ||
            maxWeight > WeightTolerance || !jointsExact)
            Refuse("SMC061", $"LOD {decoded.Evidence.LodIndex} decode-back exceeded tolerance: " +
                              $"position={maxPosition:G6}m, normal={maxNormal:G6}, tangent={maxTangent:G6}, " +
                              $"UV={maxUv:G6}, colour={maxColour:G6}, weight={maxWeight:G6}, joints={jointsExact}.");

        var actualTriangles = new Dictionary<SkeletalMeshGlbTriangle, int>();
        foreach (var section in plan.Sections)
        {
            for (var offset = section.FirstIndex; offset < section.FirstIndex + section.TriangleCount * 3; offset += 3)
            {
                var a = plan.Vertices[checked((int)decoded.Indices[offset])].CandidateId;
                var b = plan.Vertices[checked((int)decoded.Indices[offset + 1])].CandidateId;
                var c = plan.Vertices[checked((int)decoded.Indices[offset + 2])].CandidateId;
                var triangle = CanonicalTriangle(a, b, c, section.MaterialName);
                actualTriangles[triangle] = actualTriangles.GetValueOrDefault(triangle) + 1;
            }
        }
        var topologyMatches = candidate.Triangles.Count == actualTriangles.Count &&
            candidate.Triangles.All(pair => actualTriangles.TryGetValue(pair.Key, out var count) && count == pair.Value);
        if (!topologyMatches)
            Refuse("SMC062", $"LOD {decoded.Evidence.LodIndex} triangle/material membership changed during decode-back.");
        return new SkeletalMeshTopologyCompiledLodEvidence(
            decoded.Evidence.LodIndex, candidate.Vertices.Count, plan.Vertices.Count,
            candidate.TriangleCount, plan.Indices.Count / 3, plan.Sections.Count, plan.Chunks.Count,
            plan.DuplicatedVertexCount, decoded.Evidence.IndexBuffer.ElementSize,
            decoded.Evidence.VertexBuffer.ElementSize, decoded.Evidence.TextureCoordinateCount,
            decoded.Evidence.UsesFullPrecisionUvs, decoded.Evidence.VertexColorBuffer is not null,
            plan.MaximumInfluences, plan.MaximumWeightQuantizationError,
            maxPosition, maxNormal, maxTangent, maxUv, maxColour, maxWeight,
            jointsExact, topologyMatches, decoded.Evidence.IndexBuffer.DataSha256,
            decoded.Evidence.VertexBuffer.DataSha256, decoded.Evidence.VertexColorBuffer?.DataSha256);
    }

    /// <summary>
    /// Fits an editor's export to the game mesh's vertex streams: every vertex gets the
    /// candidate's full set of UV maps (copying the first into any it lacks), and, when the game
    /// mesh has vertex colours, a vertex without one takes the colour of the nearest game vertex.
    /// </summary>
    private static SkeletalMeshGlbLod FitCandidateToSource(
        SkeletalMeshGlbLod candidate,
        SkeletalMeshNativeLayoutEvidence layout,
        byte[] sourceBytes)
    {
        var uvCount = candidate.TextureCoordinateCount;
        if (uvCount == 0 && layout.Lods.Any(lod => lod.TextureCoordinateCount > 0))
            Refuse("SMC040", "The edited mesh has no UV map. Unwrap it in your editor before export.");
        var needsColours = layout.Lods.Any(lod => lod.VertexColorBuffer is not null) &&
                           candidate.Vertices.Values.Any(vertex => vertex.Colour is null);
        if (!needsColours && candidate.Vertices.Values.All(vertex => vertex.TextureCoordinates.Count == uvCount))
            return candidate;

        var nearest = needsColours ? NearestSourceColour.Create(sourceBytes, layout) : null;
        var vertices = candidate.Vertices.ToDictionary(pair => pair.Key, pair => pair.Value with
        {
            TextureCoordinates = FitTextureCoordinates(pair.Value.TextureCoordinates, uvCount),
            Colour = pair.Value.Colour ?? nearest?.Find(pair.Value.Position),
        });
        return candidate with { Vertices = vertices, HasColours = candidate.HasColours || needsColours };
    }

    private static IReadOnlyList<Vector2> FitTextureCoordinates(IReadOnlyList<Vector2> uvs, int count)
    {
        if (uvs.Count == count) return uvs;
        var first = uvs.Count > 0 ? uvs[0] : Vector2.Zero;
        return Enumerable.Range(0, count).Select(index => index < uvs.Count ? uvs[index] : first).ToArray();
    }

    /// <summary>Finds the colour of the game mesh's LOD0 vertex nearest to a position, on a uniform grid.</summary>
    private sealed class NearestSourceColour
    {
        private const int CellsPerAxis = 64;
        private const int MaximumRing = 4;
        private readonly Vector3[] _positions;
        private readonly byte[] _colours;
        private readonly Dictionary<(int, int, int), List<int>> _cells = [];
        private readonly Vector3 _minimum;
        private readonly float _cellSize;

        private NearestSourceColour(Vector3[] positions, byte[] colours)
        {
            _positions = positions;
            _colours = colours;
            _minimum = positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var extent = positions.Aggregate(new Vector3(float.MinValue), Vector3.Max) - _minimum;
            _cellSize = Math.Max(1e-4f, Math.Max(extent.X, Math.Max(extent.Y, extent.Z)) / CellsPerAxis);
            for (var index = 0; index < positions.Length; index++)
            {
                var cell = Cell(positions[index]);
                if (!_cells.TryGetValue(cell, out var members)) _cells[cell] = members = [];
                members.Add(index);
            }
        }

        public static NearestSourceColour? Create(byte[] sourceBytes, SkeletalMeshNativeLayoutEvidence layout)
        {
            var lod = new SkeletalMeshSemanticDecoder().Decode(sourceBytes, layout)
                .Where(item => item.Colors is not null && item.Positions.Length > 0)
                .MinBy(item => item.Evidence.LodIndex);
            return lod is null ? null : new NearestSourceColour(lod.Positions, lod.Colors!);
        }

        public Vector4 Find(Vector3 position)
        {
            var (x, y, z) = Cell(position);
            var best = -1;
            var bestDistance = float.MaxValue;
            // Search shells of cells outwards; a vertex in a further shell is at least ring cells away.
            for (var ring = 0; ring <= MaximumRing; ring++)
            {
                for (var dx = -ring; dx <= ring; dx++)
                for (var dy = -ring; dy <= ring; dy++)
                for (var dz = -ring; dz <= ring; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != ring ||
                        !_cells.TryGetValue((x + dx, y + dy, z + dz), out var members)) continue;
                    foreach (var index in members)
                    {
                        var distance = Vector3.DistanceSquared(position, _positions[index]);
                        if (distance < bestDistance) (best, bestDistance) = (index, distance);
                    }
                }
                if (bestDistance <= ring * _cellSize * (ring * _cellSize)) break;
            }
            // Far from every game vertex (new geometry outside the original's shape): check them all.
            if (best < 0)
            {
                for (var index = 0; index < _positions.Length; index++)
                {
                    var distance = Vector3.DistanceSquared(position, _positions[index]);
                    if (distance < bestDistance) (best, bestDistance) = (index, distance);
                }
            }
            var colour = _colours.AsSpan(best * 4, 4);
            return new Vector4(colour[0], colour[1], colour[2], colour[3]) / 255f;
        }

        private (int, int, int) Cell(Vector3 position)
        {
            var offset = (position - _minimum) / _cellSize;
            return ((int)MathF.Floor(offset.X), (int)MathF.Floor(offset.Y), (int)MathF.Floor(offset.Z));
        }
    }

    private static IReadOnlyDictionary<uint, Vector4> BuildTangentFrames(SkeletalMeshGlbLod candidate)
    {
        var normals = candidate.Vertices.ToDictionary(pair => pair.Key,
            pair => Normalize(pair.Value.Normal ?? default, "normal", candidate.LodIndex, pair.Key));
        if (candidate.HasTangents)
            return candidate.Vertices.ToDictionary(pair => pair.Key,
                pair => OrthonormalizeTangent(normals[pair.Key], pair.Value.Tangent ?? default));
        var tangents = candidate.Vertices.Keys.ToDictionary(id => id, _ => Vector3.Zero);
        var bitangents = candidate.Vertices.Keys.ToDictionary(id => id, _ => Vector3.Zero);
        foreach (var (triangle, multiplicity) in candidate.Triangles)
        {
            for (var repeat = 0; repeat < multiplicity; repeat++)
            {
                var a = candidate.Vertices[triangle.A]; var b = candidate.Vertices[triangle.B];
                var c = candidate.Vertices[triangle.C];
                var edge1 = b.Position - a.Position; var edge2 = c.Position - a.Position;
                var uv1 = b.TextureCoordinates[0] - a.TextureCoordinates[0];
                var uv2 = c.TextureCoordinates[0] - a.TextureCoordinates[0];
                var determinant = uv1.X * uv2.Y - uv1.Y * uv2.X;
                if (!float.IsFinite(determinant) || MathF.Abs(determinant) < 1e-12f) continue;
                var inverse = 1f / determinant;
                var tangent = (edge1 * uv2.Y - edge2 * uv1.Y) * inverse;
                var bitangent = (edge2 * uv1.X - edge1 * uv2.X) * inverse;
                foreach (var id in new[] { triangle.A, triangle.B, triangle.C })
                {
                    tangents[id] += tangent; bitangents[id] += bitangent;
                }
            }
        }
        return candidate.Vertices.Keys.ToDictionary(id => id, id =>
        {
            var normal = normals[id];
            var tangent = tangents[id] - normal * Vector3.Dot(normal, tangents[id]);
            if (!TryNormalize(tangent, out tangent)) tangent = AnyPerpendicular(normal);
            var sign = Vector3.Dot(Vector3.Cross(normal, tangent), bitangents[id]) < 0f ? -1f : 1f;
            return new Vector4(tangent, sign);
        });
    }

    private static IReadOnlyList<QuantizedInfluence> QuantizeWeights(IReadOnlyDictionary<int, float> source)
    {
        var ordered = source.Where(pair => pair.Value > 1e-7f)
            .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).ToArray();
        if (ordered.Length is < 1 or > 4) Refuse("SMC070", "A candidate vertex has an unsupported influence count.");
        var sum = ordered.Sum(pair => pair.Value);
        var remaining = 255 - ordered.Length;
        var exact = ordered.Select(pair => pair.Value / sum * remaining).ToArray();
        var weights = exact.Select(value => 1 + (int)Math.Floor(value)).ToArray();
        var unassigned = 255 - weights.Sum();
        foreach (var index in exact.Select((value, index) => (index, fraction: value - Math.Floor(value)))
                     .OrderByDescending(item => item.fraction).ThenBy(item => ordered[item.index].Key)
                     .Take(unassigned).Select(item => item.index))
            weights[index]++;
        return ordered.Select((pair, index) =>
            new QuantizedInfluence(pair.Key, checked((byte)weights[index]))).ToArray();
    }

    private static double WeightQuantizationError(
        IReadOnlyDictionary<int, float> source,
        IReadOnlyList<QuantizedInfluence> quantized)
    {
        var sum = source.Values.Sum();
        return source.Keys.Union(quantized.Select(item => item.Bone)).Max(bone =>
        {
            var compiled = quantized.FirstOrDefault(item => item.Bone == bone);
            return Math.Abs(source.GetValueOrDefault(bone) / sum -
                            (compiled?.Weight ?? 0) / 255d);
        });
    }

    private static HashSet<int> TriangleBones(CandidateTriangle triangle, SkeletalMeshGlbLod candidate) =>
        new[] { triangle.A, triangle.B, triangle.C }
            .SelectMany(id => candidate.Vertices[id].SkinWeights.Keys).ToHashSet();

    private static IReadOnlyList<int> BoneClosure(
        IEnumerable<int> bones,
        SkeletalMeshNativeLayoutEvidence layout)
    {
        var result = bones.ToHashSet();
        foreach (var bone in result.ToArray())
        {
            if (bone < 0 || bone >= layout.BoneCount)
                Refuse("SMC071", $"Candidate references invalid skeleton bone {bone}.");
            var current = bone;
            var guard = 0;
            while (current >= 0 && current < layout.BoneCount && guard++ <= layout.BoneCount)
            {
                result.Add(current);
                var parent = layout.Bones[current].ParentIndex;
                if (parent < 0 || parent == current) break;
                current = parent;
            }
            if (guard > layout.BoneCount) Refuse("SMC072", "Reference skeleton contains a parent cycle.");
        }
        return result.Order().ToArray();
    }

    private static IReadOnlyList<string> BuildMaterialNames(
        SkeletalMeshReconnaissanceReport report,
        SkeletalMeshNativeLayoutEvidence layout)
    {
        var count = Math.Max(layout.MaterialReferenceCount,
            layout.Lods.SelectMany(lod => lod.Sections)
                .Select(section => section.MaterialIndex + 1).DefaultIfEmpty().Max());
        var names = Enumerable.Range(0, count).Select(index => $"material_slot_{index:D2}").ToArray();
        if (report.Oracle is { } oracle)
            foreach (var oracleLod in oracle.Lods)
            {
                var nativeLod = layout.Lods.SingleOrDefault(lod => lod.LodIndex == oracleLod.LodIndex);
                if (nativeLod is null) continue;
                foreach (var oracleSection in oracleLod.Sections)
                {
                    var nativeSection = nativeLod.Sections.SingleOrDefault(section =>
                        section.SectionIndex == oracleSection.SectionIndex);
                    if (nativeSection is not null && nativeSection.MaterialIndex >= 0 &&
                        nativeSection.MaterialIndex < names.Length && !string.IsNullOrWhiteSpace(oracleSection.Material))
                        names[nativeSection.MaterialIndex] = oracleSection.Material;
                }
            }
        var slotNames = SkeletalMeshGlbSemanticReader.WithBlenderSuffixes(names);
        if (slotNames.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            Refuse("SMC073", "The source material table contains ambiguous duplicate names.");
        return slotNames;
    }

    private static void ValidateIdentity(
        SkeletalMeshEditableExportManifest source,
        SkeletalMeshReconnaissanceReport recon)
    {
        if (recon.SchemaVersion != SkeletalMeshReconnaissanceService.CurrentSchemaVersion ||
            recon.Readiness != SkeletalMeshReadiness.NoOpReady || recon.NativeLayout is null ||
            recon.NativeExportIndex is null || recon.NativeDataRelativeOffset is null)
            Refuse("SMC080", "The source reconnaissance report is not ready for topology compilation.");
        if (recon.PackageProfile.FileVersion != PackageConstants.WesternFileVersion ||
            recon.PackageProfile.LicenseeVersion != PackageConstants.WesternLicenseeVersion ||
            recon.PackageProfile.CompressionFlags != PackageConstants.WesternCompressionFlags)
            Refuse("SMC081", "Only the Western Lost Ark 868/16 package profile is authorized.");
        if (source.NativeExportIndex != recon.NativeExportIndex || source.SerialOffset != recon.SerialOffset ||
            source.SerialSize != recon.SerialSize ||
            !source.SourcePackageSha256.Equals(recon.PackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !source.SourceExportSha256.Equals(recon.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            !source.NativeParserProfile.Equals(recon.NativeLayout.ParserProfile, StringComparison.Ordinal))
            Refuse("SMC083", "The editable export and native reconnaissance identities differ.");
    }

    private static Vector3 Normalize(Vector3 value, string label, int lod, uint vertex)
    {
        if (!TryNormalize(value, out var result))
            Refuse("SMC090", $"LOD {lod} vertex {vertex} has no usable {label}.");
        return result;
    }

    private static Vector4 OrthonormalizeTangent(Vector3 normal, Vector4 value)
    {
        var tangent = new Vector3(value.X, value.Y, value.Z);
        tangent -= normal * Vector3.Dot(normal, tangent);
        // Editors leave a zero tangent on collapsed UV islands; any tangent along the surface serves there.
        if (!TryNormalize(tangent, out tangent)) tangent = AnyPerpendicular(normal);
        return new Vector4(tangent, value.W < 0f ? -1f : 1f);
    }

    private static Vector3 AnyPerpendicular(Vector3 normal) => Vector3.Normalize(MathF.Abs(normal.Z) < 0.999f
        ? Vector3.Cross(Vector3.UnitZ, normal)
        : Vector3.Cross(Vector3.UnitY, normal));

    private static bool TryNormalize(Vector3 value, out Vector3 normalized)
    {
        var length = value.LengthSquared();
        if (!float.IsFinite(length) || length < 1e-12f)
        {
            normalized = default;
            return false;
        }
        normalized = value / MathF.Sqrt(length);
        return float.IsFinite(normalized.X) && float.IsFinite(normalized.Y) && float.IsFinite(normalized.Z);
    }

    private static byte[] EncodeColour(Vector4 value) =>
    [
        ToColourByte(value.X), ToColourByte(value.Y),
        ToColourByte(value.Z), ToColourByte(value.W),
    ];

    private static byte ToColourByte(float value) => checked((byte)Math.Clamp(
        (int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255));

    private static byte PackNormal(float value) => checked((byte)Math.Clamp(
        (int)MathF.Round(Math.Clamp(value, -1f, 1f) * 127f + 128f), 0, 255));

    private static void WriteBounds(Span<byte> bytes, int offset, IReadOnlyList<Vector3> positions)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var native = positions.Select(value => new Vector3(
            value.X * 100f, value.Z * 100f, value.Y * 100f)).ToArray();
        foreach (var value in native) { minimum = Vector3.Min(minimum, value); maximum = Vector3.Max(maximum, value); }
        var origin = (minimum + maximum) * 0.5f;
        var extent = (maximum - minimum) * 0.5f;
        var radius = native.Max(value => Vector3.Distance(origin, value));
        WriteSingle(bytes, offset, origin.X); WriteSingle(bytes, offset + 4, origin.Y);
        WriteSingle(bytes, offset + 8, origin.Z); WriteSingle(bytes, offset + 12, extent.X);
        WriteSingle(bytes, offset + 16, extent.Y); WriteSingle(bytes, offset + 20, extent.Z);
        WriteSingle(bytes, offset + 24, radius);
    }

    private static SkeletalMeshGlbTriangle CanonicalTriangle(uint a, uint b, uint c, string material)
    {
        if (b < a && b <= c) return new SkeletalMeshGlbTriangle(b, c, a, material);
        if (c < a && c < b) return new SkeletalMeshGlbTriangle(c, a, b, material);
        return new SkeletalMeshGlbTriangle(a, b, c, material);
    }

    private sealed record PlannedPrefix(byte[] Bytes, int PropertyGrowth, int[] Materials, int? ExtensionValuesOffset);

    /// <summary>
    /// The export up to its LOD array as the compiled mesh starts: the source's bytes, with slots taken by other packages'
    /// materials pointing at their imports, added slots appended to the material list (and to the per-material cloth list
    /// before it), and, when the mesh's LOD offsets are proven to be absolute LOD positions, a note of where they sit.
    /// </summary>
    private static PlannedPrefix PlanPrefix(
        byte[] source,
        SkeletalMeshReconnaissanceReport recon,
        SkeletalMeshNativeLayoutEvidence layout,
        AssetCatalog catalog,
        IReadOnlyList<SkeletalMeshMaterialReference?> added,
        IReadOnlyDictionary<int, SkeletalMeshMaterialReference> replaced,
        SkeletalMeshNativeRangeEvidence lodCountRange)
    {
        var bytes = new List<byte>(source.AsSpan(0, checked((int)lodCountRange.RelativeOffset)).ToArray());
        int Import(SkeletalMeshMaterialReference reference) => reference.Material.FindImport(catalog) ??
            throw new SkeletalMeshTopologyCompilationRefusalException("SMC036",
                $"The package does not import {reference.Material.Path} yet; Arkive adds the imports when it builds the mod.");
        var materials = layout.MaterialReferences.ToArray();
        foreach (var (slot, reference) in replaced) materials[slot] = Import(reference);
        var appended = added.Select(reference => Import(reference!)).ToArray();
        void Write(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[at..], value);
        int Read(int at) => BinaryPrimitives.ReadInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[at..]);

        // Per-material lists in the properties grow with the material list. Sizes and counts are written in the source's
        // places first, then the new entries go in from the back so the earlier places stay put.
        var inserts = new List<(int At, byte[] Bytes)>();
        if (appended.Length > 0 && recon.Properties.SingleOrDefault(property =>
                property.Name.Equals("clothingassets", StringComparison.OrdinalIgnoreCase)) is { } cloth)
        {
            // One cloth entry per material slot; added slots have none.
            var valueAt = checked((int)cloth.ValueRelativeOffset);
            var count = Read(valueAt);
            if (!cloth.Type.Equals("arrayproperty", StringComparison.OrdinalIgnoreCase) || count != materials.Length ||
                cloth.Size != 4 + 4 * count)
                Refuse("SMC037", "The mesh's cloth list does not follow its material list, so Arkive cannot add material slots to it.");
            Write(checked((int)cloth.RelativeOffset) + 16, cloth.Size + appended.Length * 4);
            Write(valueAt, count + appended.Length);
            inserts.Add((valueAt + 4 + 4 * count, new byte[appended.Length * 4]));
        }
        if (appended.Length > 0 && recon.Properties.SingleOrDefault(property =>
                property.Name.Equals("lodinfo", StringComparison.OrdinalIgnoreCase)) is { } lodInfo &&
            ShadowCastingGrowth(bytes.ToArray(), lodInfo, catalog, materials.Length)
                is { } shadows)
        {
            // Each LOD casts shadows per material slot; added slots cast them like the slot before them.
            foreach (var (listCountAt, sizeAt, valueEnd, flag) in shadows)
            {
                Write(sizeAt, Read(sizeAt) + appended.Length);
                Write(listCountAt, Read(listCountAt) + appended.Length);
                inserts.Add((valueEnd, Enumerable.Repeat(flag, appended.Length).ToArray()));
            }
            Write(checked((int)lodInfo.RelativeOffset) + 16, lodInfo.Size + shadows.Count * appended.Length);
        }
        foreach (var (at, insert) in inserts.OrderByDescending(item => item.At)) bytes.InsertRange(at, insert);
        var growth = inserts.Sum(item => item.Bytes.Length);
        var materialsRange = layout.Ranges.Single(range => range.Name == "Material references");
        var countAt = checked((int)materialsRange.RelativeOffset) + growth;
        if (Read(countAt) != materials.Length) Refuse("SMC038", "The material list is not where the layout says.");
        for (var slot = 0; slot < materials.Length; slot++) Write(countAt + 4 + slot * 4, materials[slot]);
        if (appended.Length > 0)
        {
            Write(countAt, materials.Length + appended.Length);
            bytes.InsertRange(countAt + 4 + materials.Length * 4, appended.SelectMany(BitConverter.GetBytes));
        }

        // Lost Ark meshes store each LOD's absolute position in the package; written once the LODs are laid out.
        int? extensionValues = null;
        var extension = layout.Ranges.SingleOrDefault(range => range.Name == "Lost Ark mesh extension");
        if (extension is not null && layout.LostArkExtensionValues.Count == layout.Lods.Count &&
            layout.LostArkExtensionValues.Select((value, index) => value == recon.SerialOffset + layout.Lods[index].RelativeOffset).All(value => value))
            extensionValues = checked((int)extension.RelativeOffset) + growth + appended.Length * 4 + 8;
        return new PlannedPrefix(bytes.ToArray(), growth, materials.Concat(appended).ToArray(), extensionValues);
    }

    // The bEnableShadowCasting lists of a mesh's LODInfo array (one flag byte per material slot): where each list's count,
    // its property size and its end are, and its last flag. Null when the LODInfo does not read as expected, in which case
    // it is left as it is.
    private static List<(int CountAt, int SizeAt, int ValueEnd, byte Flag)>? ShadowCastingGrowth(
        byte[] bytes, SkeletalMeshPropertyEvidence lodInfo, AssetCatalog catalog, int materials)
    {
        if (!lodInfo.Type.Equals("arrayproperty", StringComparison.OrdinalIgnoreCase)) return null;
        string Name(int at)
        {
            var index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
            return index >= 0 && index < catalog.Names.Count ? catalog.Names[index].Value : "";
        }
        var start = checked((int)lodInfo.ValueRelativeOffset);
        var end = start + lodInfo.Size;
        var lods = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(start));
        var at = start + 4;
        var lists = new List<(int, int, int, byte)>();
        for (var lod = 0; lod < lods; lod++)
        {
            var found = false;
            while (true)
            {
                if (at + 8 > end) return null;
                if (Name(at).Equals("None", StringComparison.OrdinalIgnoreCase)) { at += 8; break; }
                if (at + 24 > end) return null;
                var type = Name(at + 8);
                var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 16));
                var valueAt = at + 24 + (type.Equals("structproperty", StringComparison.OrdinalIgnoreCase) ? 8
                    : type.Equals("boolproperty", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
                if (size < 0 || valueAt + size > end) return null;
                if (Name(at).Equals("benableshadowcasting", StringComparison.OrdinalIgnoreCase) &&
                    type.Equals("arrayproperty", StringComparison.OrdinalIgnoreCase))
                {
                    var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(valueAt));
                    if (count != materials || size != 4 + count || found) return null;
                    lists.Add((valueAt, at + 16, valueAt + size, count > 0 ? bytes[valueAt + size - 1] : (byte)1));
                    found = true;
                }
                at = valueAt + size;
            }
            if (!found) return null;
        }
        return at == end && lists.Count == lods ? lists : null;
    }

    /// <summary>
    /// The bytes after the LODs: the bone name map, three empty lists, then one 4-byte entry per material slot. Added slots
    /// get zero entries like the game's own (the open world misdraws a mesh whose list is shorter than its material list).
    /// Null when the bytes do not read that way, in which case they are kept.
    /// </summary>
    public static byte[]? TrailingWithMaterials(ReadOnlySpan<byte> trailing, int materials, int added)
    {
        if (added == 0 || trailing.Length < 4) return null;
        var names = BinaryPrimitives.ReadInt32LittleEndian(trailing);
        if (names < 0 || names > (trailing.Length - 4) / 12) return null;
        var at = 4 + names * 12;
        if (at + 16 > trailing.Length || trailing.Slice(at, 12).IndexOfAnyExcept((byte)0) >= 0 ||
            BinaryPrimitives.ReadInt32LittleEndian(trailing[(at + 12)..]) != materials || at + 16 + materials * 4 > trailing.Length)
            return null;
        var end = at + 16 + materials * 4;
        var grown = new byte[trailing.Length + added * 4];
        trailing[..end].CopyTo(grown);
        trailing[end..].CopyTo(grown.AsSpan(end + added * 4));
        BinaryPrimitives.WriteInt32LittleEndian(grown.AsSpan(at + 12), materials + added);
        return grown;
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static bool PrefixEqualsExceptBounds(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> compiled,
        int boundsOffset,
        int boundsBytes) =>
        source.Length == compiled.Length &&
        source[..boundsOffset].SequenceEqual(compiled[..boundsOffset]) &&
        source[(boundsOffset + boundsBytes)..].SequenceEqual(compiled[(boundsOffset + boundsBytes)..]);

    private static double TangentDistance(Vector4 left, Vector4 right)
    {
        var direction = Vector3.Distance(new Vector3(left.X, left.Y, left.Z),
            new Vector3(right.X, right.Y, right.Z));
        return Math.Max(direction, (left.W < 0f) == (right.W < 0f) ? 0d : 2d);
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);

    private static void WriteSingle(Span<byte> bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], BitConverter.SingleToInt32Bits(value));

    private static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken token)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The manifest was not found.", path);
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
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            Refuse("SMC092", $"A manifest path escapes its source directory: {full}");
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
        throw new SkeletalMeshTopologyCompilationRefusalException(code, message);

    private sealed record CandidateTriangle(uint A, uint B, uint C, string Material, int MaterialIndex);

    private sealed record TrianglePartition(
        int MaterialIndex,
        string Material,
        List<CandidateTriangle> Triangles,
        HashSet<int> Bones);

    private sealed record QuantizedInfluence(int Bone, byte Weight);

    private sealed record CompiledVertex(
        uint CandidateId,
        Vector3 Position,
        Vector3 Normal,
        Vector4 Tangent,
        IReadOnlyList<Vector2> TextureCoordinates,
        IReadOnlyList<QuantizedInfluence> Influences,
        byte[]? Colour);

    private sealed record CompiledChunk(
        int ChunkIndex,
        int FirstVertex,
        int RigidVertexCount,
        int SoftVertexCount,
        int MaximumInfluences,
        IReadOnlyList<int> BoneMap);

    private sealed record CompiledSection(
        int SectionIndex,
        short MaterialIndex,
        short ChunkIndex,
        int FirstIndex,
        int TriangleCount,
        byte Flags,
        string MaterialName);

    private sealed record CompiledLodPlan(
        SkeletalMeshNativeLodEvidence Source,
        IReadOnlyList<CompiledVertex> Vertices,
        IReadOnlyList<uint> Indices,
        IReadOnlyList<CompiledSection> Sections,
        IReadOnlyList<CompiledChunk> Chunks,
        IReadOnlyList<short> ActiveBones,
        IReadOnlyList<short> RequiredBones,
        double MaximumWeightQuantizationError,
        int MaximumInfluences,
        int DuplicatedVertexCount);
}
