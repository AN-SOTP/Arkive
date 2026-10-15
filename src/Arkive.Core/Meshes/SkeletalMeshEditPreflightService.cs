using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Inspection;

namespace Arkive.Core.Meshes;

public sealed class SkeletalMeshEditPreflightService
{
    public const int CurrentSchemaVersion = 5;
    private const int MaximumCandidateVertices = 4_000_000;
    private const int MaximumCandidateTriangles = 8_000_000;
    private readonly ExportArtifactInspector _inspector = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<SkeletalMeshEditPreflightReport> InspectAsync(
        SkeletalMeshEditableWorkspaceEntry source,
        string candidateGlbPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateGlbPath);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var issues = new List<SkeletalMeshEditIssue>();
        var candidatePath = Path.GetFullPath(candidateGlbPath);
        if (!File.Exists(source.GlbPath))
            throw new FileNotFoundException("The source editable GLB is missing.", source.GlbPath);
        if (!File.Exists(candidatePath))
            throw new FileNotFoundException("The edited GLB is missing.", candidatePath);
        if (Path.GetExtension(candidatePath).Equals(".gltf", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Register an embedded .glb file; external .gltf dependencies are not accepted.");

        var sourceHash = await ComputeSha256Async(source.GlbPath, cancellationToken).ConfigureAwait(false);
        var candidateHash = await ComputeSha256Async(candidatePath, cancellationToken).ConfigureAwait(false);
        var candidateFile = new FileInfo(candidatePath);
        if (!sourceHash.Equals(source.Manifest.GlbSha256, StringComparison.OrdinalIgnoreCase))
            AddError("SMI001", "The source editable GLB no longer matches its Arkive manifest.");
        if (candidateHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
            AddInfo("SMI002", "The candidate is byte-identical to the source editable GLB.");

        var inspection = await _inspector.InspectAsync(candidatePath, cancellationToken).ConfigureAwait(false);
        foreach (var issue in inspection.Issues)
        {
            if (issue.Severity == ArtifactInspectionSeverity.Error)
                AddError($"SMI-GLB-{issue.Code}", issue.Message);
            else
                AddWarning($"SMI-GLB-{issue.Code}", issue.Message);
        }

        SkeletalMeshGlbSemanticDocument? sourceDocument = null;
        SkeletalMeshGlbSemanticDocument? stableCandidate = null;
        SkeletalMeshGlbSemanticDocument? topologyCandidate = null;
        SkeletalMeshGlbSemanticException? stableCandidateFailure = null;
        try
        {
            sourceDocument = SkeletalMeshGlbSemanticReader.Read(source.GlbPath, source.Manifest);
        }
        catch (SkeletalMeshGlbSemanticException exception)
        {
            AddSemanticError(exception);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or OverflowException)
        {
            AddError("SMI003", $"Source semantic GLB parsing failed: {exception.Message}");
        }

        if (sourceDocument is not null && candidateHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
        {
            stableCandidate = sourceDocument;
            topologyCandidate = sourceDocument;
        }
        else if (sourceDocument is not null)
        {
            try
            {
                stableCandidate = SkeletalMeshGlbSemanticReader.Read(candidatePath, source.Manifest);
            }
            catch (SkeletalMeshGlbSemanticException exception)
            {
                stableCandidateFailure = exception;
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or OverflowException)
            {
                AddError("SMI003", $"Candidate semantic GLB parsing failed: {exception.Message}");
            }
        }

        var importProfile = sourceDocument is not null && stableCandidate is not null &&
                            SatisfiesTopologyPreservingShape(sourceDocument, stableCandidate)
            ? SkeletalMeshImportProfile.TopologyPreserving
            : SkeletalMeshImportProfile.ReplaceLod0AndDuplicate;
        if (sourceDocument is not null && importProfile == SkeletalMeshImportProfile.ReplaceLod0AndDuplicate)
        {
            try
            {
                topologyCandidate = SkeletalMeshGlbSemanticReader.Read(
                    candidatePath, source.Manifest, SkeletalMeshGlbReadProfile.TopologyCandidate);
            }
            catch (SkeletalMeshGlbSemanticException exception)
            {
                AddSemanticError(exception);
                if (stableCandidateFailure is not null && stableCandidateFailure.Code != exception.Code)
                    AddInfo("SMT001",
                        $"Stable-identity parsing also reported {stableCandidateFailure.Code}; " +
                        "the topology reader was used as the authoritative import contract.");
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or OverflowException)
            {
                AddError("SMI003", $"Topology candidate parsing failed: {exception.Message}");
            }
        }

        var lodReports = new List<SkeletalMeshEditLodPreflight>();
        var lodStructureMatches = false;
        var materialsMatch = false;
        var skeletonMatches = false;
        var ignoredUnskinned = 0;
        SkeletalMeshTopologyPreflight? topology = null;
        SkeletalMeshAnimationPreflight? animationValidation = null;
        SkeletalMeshSkeletonCompatibilityReport? skeletonCompatibility = null;
        var dependencies = ReadSourceDependencies(source);

        var skeletonCandidate = importProfile == SkeletalMeshImportProfile.TopologyPreserving
            ? stableCandidate
            : topologyCandidate;
        if (skeletonCandidate?.Lods.Values.Sum(lod => lod.IgnoredMorphTargetCount) is int ignoredMorphTargets and > 0)
            AddWarning("SMI038", $"Ignored {ignoredMorphTargets:N0} shape key target(s); Arkive imports the base shape only.");
        if (sourceDocument is not null && skeletonCandidate is not null)
        {
            var sourceIdentity = SkeletalMeshSkeletonCompatibilityService.SourceIdentity(source);
            var candidateIdentity = new SkeletalMeshSkeletonIdentity(
                "candidate", source.Manifest.PackageAlias, source.Manifest.ObjectName,
                source.ManifestPath, candidatePath, candidateHash);
            skeletonCompatibility = new SkeletalMeshSkeletonCompatibilityService().Analyze(
                sourceDocument, sourceIdentity, skeletonCandidate, candidateIdentity);
            skeletonMatches = skeletonCompatibility.ExactSkeletonIdentity;
            issues.AddRange(skeletonCompatibility.Issues);
        }

        if (sourceDocument is not null && importProfile == SkeletalMeshImportProfile.TopologyPreserving &&
            stableCandidate is not null)
        {
            if (dependencies.Report is { } preserved)
            {
                if (preserved.HasActiveClothData)
                    AddInfo("SMD100",
                        $"{preserved.ActiveClothReferenceCount:N0} active cloth reference(s) will be preserved byte-for-byte; stable vertex and section identity is unchanged.");
                if (preserved.HasActiveMorphTargets)
                    AddInfo("SMD101",
                        $"{preserved.MorphTargetCount:N0} linked morph target(s) with {preserved.MorphDeltaCount:N0} deltas will be preserved byte-for-byte; stable vertex identity is unchanged.");
            }
            ignoredUnskinned = stableCandidate.IgnoredUnskinnedMeshCount;
            lodStructureMatches = sourceDocument.Lods.Keys.Order().SequenceEqual(stableCandidate.Lods.Keys.Order());
            if (!lodStructureMatches)
                AddError("SMI010",
                    $"LOD structure changed: source=[{string.Join(",", sourceDocument.Lods.Keys.Order())}], " +
                    $"candidate=[{string.Join(",", stableCandidate.Lods.Keys.Order())}].");
            if (ignoredUnskinned > 0)
                AddWarning("SMI011", $"Ignored {ignoredUnskinned:N0} unskinned helper mesh(es) added by the editor.");
            var sourceMaterials = UsedMaterials(sourceDocument);
            var candidateMaterials = UsedMaterials(stableCandidate);
            materialsMatch = sourceMaterials.SetEquals(candidateMaterials);
            if (!materialsMatch)
                AddError("SMI013",
                    $"Material slots changed: source=[{string.Join(", ", sourceMaterials.Order())}], " +
                    $"candidate=[{string.Join(", ", candidateMaterials.Order())}].");
            foreach (var lodIndex in sourceDocument.Lods.Keys.Intersect(stableCandidate.Lods.Keys).Order())
                lodReports.Add(ComparePreservingLod(
                    sourceDocument.Lods[lodIndex], stableCandidate.Lods[lodIndex]));
        }
        else if (sourceDocument is not null && topologyCandidate is not null)
        {
            ignoredUnskinned = topologyCandidate.IgnoredUnskinnedMeshCount;
            topology = ValidateTopologyCandidate(sourceDocument, topologyCandidate);
            lodStructureMatches = sourceDocument.Lods.Keys.Order().SequenceEqual(
                topologyCandidate.Lods.Keys.Order());
            materialsMatch = topology.CandidateMaterials.All(material =>
                sourceDocument.MaterialNames.Contains(material, StringComparer.OrdinalIgnoreCase));
            var candidateLod = topologyCandidate.Lods.GetValueOrDefault(0);
            if (candidateLod is not null)
            {
                var sourceLod = sourceDocument.Lods[0];
                lodReports.Add(new SkeletalMeshEditLodPreflight(
                    0, sourceLod.Vertices.Count, candidateLod.Vertices.Count,
                    sourceLod.TriangleCount, candidateLod.TriangleCount,
                    sourceLod.PrimitiveCount, candidateLod.PrimitiveCount,
                    candidateLod.StableVertexIdCarrier, 0, 0, 0, 0, 0,
                    candidateLod.HasTangents, false, false,
                    sourceLod.TextureCoordinateCount == candidateLod.TextureCoordinateCount,
                    skeletonMatches, sourceLod.HasColours == candidateLod.HasColours));
            }
        }
        else if (sourceDocument is not null && issues.All(issue => issue.Severity != SkeletalMeshEditIssueSeverity.Error))
        {
            AddError("SMI003", stableCandidateFailure is null
                ? "The candidate could not be interpreted as a supported skinned GLB."
                : stableCandidateFailure.Message);
        }

        var animationCandidate = importProfile == SkeletalMeshImportProfile.TopologyPreserving
            ? stableCandidate
            : topologyCandidate;
        if (sourceDocument is not null && animationCandidate is not null && skeletonMatches)
        {
            try
            {
                var result = SkeletalMeshAnimationValidationService.Validate(
                    sourceDocument, animationCandidate, cancellationToken);
                animationValidation = result.Report;
                issues.AddRange(result.Issues);
            }
            catch (Exception exception) when (exception is InvalidDataException or OverflowException or
                                               ArithmeticException or ArgumentException)
            {
                AddError("SMA001", $"Animation-aware mesh validation failed: {exception.Message}");
            }
        }

        var changedPositions = lodReports.Sum(lod => lod.ChangedPositionVertexCount);
        var changedNormals = lodReports.Sum(lod => lod.ChangedNormalVertexCount);
        var changedTangents = lodReports.Sum(lod => lod.ChangedTangentVertexCount);
        var maximumDelta = lodReports.Count == 0 ? 0d : lodReports.Max(lod => lod.MaximumPositionDeltaMetres);
        if (importProfile == SkeletalMeshImportProfile.TopologyPreserving)
        {
            if (changedPositions > 0)
                AddInfo("SMI020",
                    $"Position edits detected on {changedPositions:N0} stable vertices; maximum displacement is {maximumDelta:N6} m.");
            if (changedNormals > 0) AddInfo("SMI021", $"Normal edits detected on {changedNormals:N0} stable vertices.");
            if (changedTangents > 0) AddInfo("SMI022", $"Tangent edits detected on {changedTangents:N0} stable vertices.");
        }

        var errorCount = issues.Count(issue => issue.Severity == SkeletalMeshEditIssueSeverity.Error);
        var readiness = errorCount > 0
            ? SkeletalMeshEditReadiness.Refused
            : importProfile == SkeletalMeshImportProfile.ReplaceLod0AndDuplicate
                ? SkeletalMeshEditReadiness.ReadyForTopologyCompilation
                : changedPositions == 0 && changedNormals == 0 && changedTangents == 0
                    ? SkeletalMeshEditReadiness.ReadyNoChanges
                    : SkeletalMeshEditReadiness.ReadyForPositionTangentEncoding;
        if (readiness == SkeletalMeshEditReadiness.ReadyNoChanges)
            AddInfo("SMI023", "No editable position, normal, or tangent changes were detected.");
        else if (readiness == SkeletalMeshEditReadiness.ReadyForPositionTangentEncoding)
            AddInfo("SMI024",
                "The edit satisfies the bounded topology-preserving profile and is ready for native vertex-buffer encoding.");
        else if (readiness == SkeletalMeshEditReadiness.ReadyForTopologyCompilation)
            AddInfo("SMT099",
                "The LOD0 replacement satisfies Arkive's topology-capable import contract. " +
                "It is retained for the variable-size native LOD compiler; the legacy same-length encoder remains disabled.");

        watch.Stop();
        return new SkeletalMeshEditPreflightReport(
            CurrentSchemaVersion, DateTimeOffset.UtcNow, source.Manifest.ExportId,
            source.Manifest.PackageAlias, source.Manifest.ObjectName, source.ManifestPath,
            source.GlbPath, sourceHash, candidatePath, candidateHash, candidateFile.Length,
            readiness, lodStructureMatches, materialsMatch, skeletonMatches, ignoredUnskinned,
            changedPositions, changedNormals, changedTangents, maximumDelta,
            issues.Count(issue => issue.Severity == SkeletalMeshEditIssueSeverity.Error),
            issues.Count(issue => issue.Severity == SkeletalMeshEditIssueSeverity.Warning),
            lodReports, issues.ToArray(), watch.Elapsed.TotalSeconds)
        {
            ImportProfile = importProfile,
            TopologyChangeDetected = importProfile == SkeletalMeshImportProfile.ReplaceLod0AndDuplicate,
            Topology = topology,
            AnimationValidation = animationValidation,
            SkeletonCompatibility = skeletonCompatibility,
            DeformationDependencies = dependencies.Report,
        };

        SkeletalMeshTopologyPreflight ValidateTopologyCandidate(
            SkeletalMeshGlbSemanticDocument original,
            SkeletalMeshGlbSemanticDocument candidate)
        {
            if (!candidate.Lods.TryGetValue(0, out var lod))
            {
                AddError("SMT010", "The topology candidate has no resolvable LOD0 skinned mesh.");
                lod = candidate.Lods.Values.FirstOrDefault() ?? EmptyLod();
            }
            var ignoredLods = candidate.Lods.Keys.Count(index => index != 0);
            if (ignoredLods > 0)
                AddWarning("SMT011",
                    $"Ignored {ignoredLods:N0} edited lower LOD(s). The compiler will duplicate candidate LOD0 into every native LOD slot.");
            if (!candidate.MeshNodeTransformsIdentity)
                AddError("SMT012",
                    "One or more skinned mesh objects have an unapplied transform. Apply location, rotation and scale before export.");
            if (ignoredUnskinned > 0)
                AddWarning("SMI011", $"Ignored {ignoredUnskinned:N0} unskinned helper mesh(es) added by the editor.");

            var allowedMaterials = original.MaterialNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknownMaterials = lod.UsedMaterials.Where(material => !allowedMaterials.Contains(material)).ToArray();
            if (unknownMaterials.Length > 0)
                AddError("SMT013", "Candidate uses material names that do not map to original slots: " +
                    string.Join(", ", unknownMaterials.Order()));
            var unused = allowedMaterials.Where(material => !lod.UsedMaterials.Contains(material)).ToArray();
            if (unused.Length > 0)
                AddWarning("SMT015", $"Candidate does not use {unused.Length:N0} original material slot name(s); their package references will remain available.");

            var sourceLod0 = original.Lods[0];
            if (lod.Vertices.Count is < 3 or > MaximumCandidateVertices)
                AddError("SMT020", $"Candidate LOD0 vertex count {lod.Vertices.Count:N0} is outside 3..{MaximumCandidateVertices:N0}.");
            if (lod.TriangleCount is < 1 or > MaximumCandidateTriangles)
                AddError("SMT021", $"Candidate LOD0 triangle count {lod.TriangleCount:N0} is outside 1..{MaximumCandidateTriangles:N0}.");
            if (!lod.HasNormals) AddError("SMT022", "Candidate LOD0 has no vertex normals.");
            if (!lod.HasTangents)
                AddWarning("SMT023", "Candidate LOD0 has no tangents; the native compiler must reconstruct them from normals and UV0.");
            if (lod.TextureCoordinateCount == 0 && sourceLod0.TextureCoordinateCount > 0)
                AddError("SMT024", "Candidate LOD0 has no UV map. Unwrap the mesh before export.");
            else if (lod.TextureCoordinateCount < sourceLod0.TextureCoordinateCount)
                AddWarning("SMT024",
                    $"Candidate LOD0 has {lod.TextureCoordinateCount} UV map(s); the game mesh uses {sourceLod0.TextureCoordinateCount}. Arkive copies the first UV map into the missing one(s).");
            else if (lod.TextureCoordinateCount > sourceLod0.TextureCoordinateCount)
                AddWarning("SMT024",
                    $"Candidate LOD0 has {lod.TextureCoordinateCount} UV map(s); the game mesh uses {sourceLod0.TextureCoordinateCount}. Arkive keeps the first {sourceLod0.TextureCoordinateCount}.");
            if (lod.HasColours != sourceLod0.HasColours || (lod.HasColours && lod.Vertices.Values.Any(vertex => vertex.Colour is null)))
                AddWarning("SMT025", sourceLod0.HasColours
                    ? "Some or all of the candidate has no vertex colours. Arkive gives those vertices the colour of the nearest vertex in the game mesh."
                    : "Candidate LOD0 has vertex colours, but the game mesh has none. Arkive leaves them out.");
            if (lod.MaximumInfluenceCount is < 1 or > 4)
                AddError("SMT026", $"Candidate uses an unsupported maximum of {lod.MaximumInfluenceCount} influences per vertex.");
            if (lod.MaximumSkinWeightSumError > 0.05d)
                AddError("SMT027", $"Candidate skin weights differ from a normalized sum by up to {lod.MaximumSkinWeightSumError:G6}.");
            else if (lod.MaximumSkinWeightSumError > 0.002d)
                AddWarning("SMT028", $"Candidate skin weights require normalization; maximum sum error is {lod.MaximumSkinWeightSumError:G6}.");
            // Shipped game meshes contain a few zero-area triangles; ones the source already had are kept as-is.
            var newDegenerate = lod.DegenerateTriangles
                .Where(triangle => !sourceLod0.DegenerateTriangles.Any(source => SameTriangle(triangle, source)))
                .ToArray();
            if (newDegenerate.Length > 0)
                AddError("SMT030", $"Candidate contains {newDegenerate.Length:N0} new degenerate triangle(s) " +
                    $"(zero area), e.g. near {Describe(newDegenerate[0].A)}. Delete or dissolve them before export.");
            if (lod.DegenerateTriangleCount > newDegenerate.Length)
                AddWarning("SMT034", $"Kept {lod.DegenerateTriangleCount - newDegenerate.Length:N0} degenerate " +
                    "triangle(s) that already exist in the original game mesh.");
            var opposedPercent = lod.FaceOrientationTriangleCount == 0 ? 0d :
                100d * lod.OpposedFaceNormalTriangleCount / lod.FaceOrientationTriangleCount;
            // Measured against the game mesh, which can have opposed faces of its own (double-sided parts).
            var sourceOpposedPercent = sourceLod0.FaceOrientationTriangleCount == 0 ? 0d :
                100d * sourceLod0.OpposedFaceNormalTriangleCount / sourceLod0.FaceOrientationTriangleCount;
            if (opposedPercent > sourceOpposedPercent + 1d)
                AddError("SMT031",
                    $"Candidate winding opposes its vertex normals on {lod.OpposedFaceNormalTriangleCount:N0} of " +
                    $"{lod.FaceOrientationTriangleCount:N0} checked triangles ({opposedPercent:N3}%; the game mesh has {sourceOpposedPercent:N3}%). Recalculate normals outside, or flip the faces that point inward.");
            else if (lod.OpposedFaceNormalTriangleCount > 0)
                AddWarning("SMT032",
                    $"Candidate has {lod.OpposedFaceNormalTriangleCount:N0} locally opposed face(s) ({opposedPercent:N3}%).");
            var extent = Math.Max(lod.Bounds.MaximumX - lod.Bounds.MinimumX,
                Math.Max(lod.Bounds.MaximumY - lod.Bounds.MinimumY, lod.Bounds.MaximumZ - lod.Bounds.MinimumZ));
            if (extent > 1_000d)
                AddError("SMT033", $"Candidate bounds span {extent:N3} metres; unapplied units or scale are likely.");

            if (!dependencies.IsResolved)
                AddError("SMT053",
                    "Cloth/morph dependency evidence is incomplete. Rerun Native mesh recon from the clean UPK before topology replacement.");
            if (dependencies.HasClothData)
                AddError("SMT050",
                    "The source mesh has an active clothing asset binding. Its vertex/section mapping cannot survive the topology compiler yet; use a topology-preserving deformation.");
            if (dependencies.MayHaveLinkedMorphTargets)
                AddError("SMT051",
                    "The source mesh has external morph deltas indexed against its native LOD vertices. Topology replacement cannot remap those deltas yet; use a topology-preserving deformation.");
            if (dependencies.Report is { } dependencyReport && dependencyReport.IsResolved &&
                !dependencyReport.HasActiveClothData && !dependencyReport.HasActiveMorphTargets &&
                (dependencyReport.ClothState == SkeletalMeshDependencyState.InactivePlaceholder ||
                 dependencyReport.MorphState == SkeletalMeshDependencyState.InactivePlaceholder))
                AddInfo("SMD102",
                    "Serialized cloth/morph placeholders were inspected and contain no active vertex-bound references; topology replacement is permitted.");
            if (source.Manifest.HasOpaqueTrailingNativeData)
                AddWarning("SMT052",
                    "The source export has opaque trailing native data. It must be preserved and internally rebased by the variable-size compiler.");

            var usedBones = lod.Vertices.Values.SelectMany(vertex => vertex.SkinWeights.Keys).Distinct().Count();
            var targets = source.Manifest.Lods.OrderBy(item => item.LodIndex).Select(item =>
                new SkeletalMeshTopologyLodPlan(
                    item.LodIndex, item.VertexCount, item.TriangleCount,
                    lod.Vertices.Count, lod.TriangleCount, "Candidate LOD0 duplicate")).ToArray();
            AddInfo("SMT040",
                $"Topology import plan: {lod.Vertices.Count:N0} vertices, {lod.TriangleCount:N0} triangles, " +
                $"{lod.UsedMaterials.Count:N0} material section(s), {usedBones:N0} used bones; " +
                $"compile the same geometry into {targets.Length:N0} native LOD slot(s).");
            return new SkeletalMeshTopologyPreflight(
                0, "Duplicate candidate LOD0 into every source LOD slot",
                original.Lods.Count, candidate.Lods.Count, ignoredLods,
                lod.Vertices.Count, lod.TriangleCount, lod.PrimitiveCount,
                lod.TextureCoordinateCount, lod.UsedMaterials.Count, usedBones,
                lod.MaximumInfluenceCount, lod.MaximumSkinWeightSumError,
                lod.DegenerateTriangleCount, lod.FaceOrientationTriangleCount,
                lod.OpposedFaceNormalTriangleCount, opposedPercent,
                lod.HasNormals, lod.HasTangents, lod.HasColours,
                lod.Vertices.Count > ushort.MaxValue, dependencies.HasClothData,
                dependencies.MayHaveLinkedMorphTargets, lod.Bounds,
                lod.UsedMaterials.Order().ToArray(), targets);
        }

        SkeletalMeshEditLodPreflight ComparePreservingLod(
            SkeletalMeshGlbLod original,
            SkeletalMeshGlbLod edited)
        {
            var missingIds = original.Vertices.Keys.Except(edited.Vertices.Keys).ToArray();
            var extraIds = edited.Vertices.Keys.Except(original.Vertices.Keys).ToArray();
            var referencedIds = original.Triangles.Keys.SelectMany(triangle =>
                new[] { triangle.A, triangle.B, triangle.C }).ToHashSet();
            var missingReferencedIds = missingIds.Where(referencedIds.Contains).ToArray();
            var stableIdsMatch = extraIds.Length == 0 && missingReferencedIds.Length == 0;
            if (!stableIdsMatch)
                AddError("SMI030",
                    $"LOD {original.LodIndex} stable vertex IDs changed: source={original.Vertices.Count:N0}, " +
                    $"candidate={edited.Vertices.Count:N0}, missingReferenced={missingReferencedIds.Length:N0}, extra={extraIds.Length:N0}.");
            else if (missingIds.Length > 0)
                AddWarning("SMI037",
                    $"LOD {original.LodIndex} editor omitted {missingIds.Length:N0} unreferenced native vertices; their source records will be preserved.");
            var topologyMatches = DictionariesEqual(original.Triangles, edited.Triangles);
            if (!topologyMatches) AddError("SMI031", $"LOD {original.LodIndex} triangle topology or material membership changed.");
            var uvMatches = original.TextureCoordinateCount == edited.TextureCoordinateCount;
            var maximumUvDelta = 0f;
            var skinMatches = true;
            var colourMatches = original.HasColours == edited.HasColours;
            var changedPositionCount = 0;
            var changedNormalCount = 0;
            var changedTangentCount = 0;
            var totalDelta = 0d;
            var maximumPositionDelta = 0d;
            foreach (var id in original.Vertices.Keys.Intersect(edited.Vertices.Keys))
            {
                var before = original.Vertices[id];
                var after = edited.Vertices[id];
                var delta = Vector3.Distance(before.Position, after.Position);
                if (delta > 1e-6f)
                {
                    changedPositionCount++;
                    totalDelta += delta;
                    maximumPositionDelta = Math.Max(maximumPositionDelta, delta);
                }
                if (before.Normal is { } beforeNormal)
                {
                    if (after.Normal is not { } afterNormal)
                    {
                        if (changedNormalCount >= 0)
                        {
                            AddError("SMI032", $"LOD {original.LodIndex} candidate is missing normals.");
                            changedNormalCount = -1;
                        }
                    }
                    else if (!SkeletalMeshGlbSemanticReader.Near(beforeNormal, afterNormal, 2e-5f) && changedNormalCount >= 0)
                        changedNormalCount++;
                }
                if (before.Tangent is { } beforeTangent && after.Tangent is { } afterTangent &&
                    !SkeletalMeshGlbSemanticReader.Near(beforeTangent, afterTangent, 2e-5f)) changedTangentCount++;
                if (before.TextureCoordinates.Count != after.TextureCoordinates.Count) uvMatches = false;
                else for (var uv = 0; uv < before.TextureCoordinates.Count; uv++)
                {
                    maximumUvDelta = Math.Max(maximumUvDelta,
                        Vector2.Distance(before.TextureCoordinates[uv], after.TextureCoordinates[uv]));
                    if (!SkeletalMeshGlbSemanticReader.Near(before.TextureCoordinates[uv], after.TextureCoordinates[uv], 2e-5f))
                        uvMatches = false;
                }
                if (!SkeletalMeshGlbSemanticReader.WeightsNear(before.SkinWeights, after.SkinWeights, 2e-4f))
                    skinMatches = false;
                if (before.Colour is { } beforeColour && after.Colour is { } afterColour)
                {
                    if (!SkeletalMeshGlbSemanticReader.Near(beforeColour, afterColour, 1f / 255f + 1e-5f))
                        colourMatches = false;
                }
                else if (before.Colour is not null || after.Colour is not null) colourMatches = false;
            }
            if (!uvMatches) AddError("SMI033", $"LOD {original.LodIndex} native UV data changed; maximum delta is {maximumUvDelta:G6}.");
            if (!skinMatches) AddError("SMI034", $"LOD {original.LodIndex} joints or skin weights changed.");
            if (!colourMatches) AddWarning("SMI035",
                $"LOD {original.LodIndex} editor changed or omitted vertex colours; source values will be preserved.");
            if (original.HasTangents && !edited.HasTangents)
                AddWarning("SMI036", $"LOD {original.LodIndex} editor omitted tangents; Arkive must reconstruct them.");
            return new SkeletalMeshEditLodPreflight(
                original.LodIndex, original.Vertices.Count, edited.Vertices.Count,
                original.TriangleCount, edited.TriangleCount, original.PrimitiveCount,
                edited.PrimitiveCount, edited.StableVertexIdCarrier, changedPositionCount,
                maximumPositionDelta, changedPositionCount == 0 ? 0d : totalDelta / changedPositionCount,
                Math.Max(0, changedNormalCount), changedTangentCount, edited.HasTangents,
                stableIdsMatch, topologyMatches, uvMatches, skinMatches, colourMatches);
        }

        void AddInfo(string code, string message) =>
            issues.Add(new SkeletalMeshEditIssue(SkeletalMeshEditIssueSeverity.Info, code, message));
        void AddWarning(string code, string message) =>
            issues.Add(new SkeletalMeshEditIssue(SkeletalMeshEditIssueSeverity.Warning, code, message));
        void AddError(string code, string message) =>
            issues.Add(new SkeletalMeshEditIssue(SkeletalMeshEditIssueSeverity.Error, code, message));
        void AddSemanticError(SkeletalMeshGlbSemanticException exception) =>
            AddError(exception.Code, exception.Message[(exception.Code.Length + 2)..]);
    }

    private static bool SatisfiesTopologyPreservingShape(
        SkeletalMeshGlbSemanticDocument source,
        SkeletalMeshGlbSemanticDocument candidate)
    {
        if (!source.Lods.Keys.Order().SequenceEqual(candidate.Lods.Keys.Order())) return false;
        foreach (var index in source.Lods.Keys)
        {
            var original = source.Lods[index];
            var edited = candidate.Lods[index];
            var referenced = original.Triangles.Keys.SelectMany(triangle =>
                new[] { triangle.A, triangle.B, triangle.C }).ToHashSet();
            if (edited.Vertices.Keys.Except(original.Vertices.Keys).Any() ||
                original.Vertices.Keys.Except(edited.Vertices.Keys).Any(referenced.Contains) ||
                !DictionariesEqual(original.Triangles, edited.Triangles) ||
                original.TextureCoordinateCount != edited.TextureCoordinateCount ||
                edited.MaximumSkinWeightSumError > 0.002d) return false;
            foreach (var id in original.Vertices.Keys.Intersect(edited.Vertices.Keys))
            {
                var before = original.Vertices[id];
                var after = edited.Vertices[id];
                if (before.TextureCoordinates.Count != after.TextureCoordinates.Count ||
                    before.TextureCoordinates.Where((uv, uvIndex) =>
                        !SkeletalMeshGlbSemanticReader.Near(uv, after.TextureCoordinates[uvIndex], 2e-5f)).Any() ||
                    !SkeletalMeshGlbSemanticReader.WeightsNear(before.SkinWeights, after.SkinWeights, 2e-4f)) return false;
                if (before.Colour is { } left && after.Colour is { } right &&
                    !SkeletalMeshGlbSemanticReader.Near(left, right, 1f / 255f + 1e-5f)) return false;
                // Colours an exporter left out, or added to a mesh without a colour stream, keep the edit
                // topology-preserving: the game's own colours are kept either way.
            }
        }
        return true;
    }

    /// <summary>Same corner positions within 0.01 mm, in any order (editors may rotate or flip a triangle's corners).</summary>
    private static bool SameTriangle(SkeletalMeshGlbTrianglePositions left, SkeletalMeshGlbTrianglePositions right)
    {
        const float tolerance = 1e-5f;
        Vector3[] corners = [right.A, right.B, right.C];
        return new[] { left.A, left.B, left.C }.All(point =>
            corners.Any(corner => SkeletalMeshGlbSemanticReader.Near(point, corner, tolerance)));
    }

    /// <summary>A glTF (Y-up) point as Blender shows it after import (Z-up).</summary>
    private static string Describe(Vector3 point) => $"Blender X {point.X:N3}, Y {-point.Z:N3}, Z {point.Y:N3} m";

    private static HashSet<string> UsedMaterials(SkeletalMeshGlbSemanticDocument document) =>
        document.Lods.Values.SelectMany(lod => lod.UsedMaterials).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool DictionariesEqual<TKey>(
        IReadOnlyDictionary<TKey, int> left,
        IReadOnlyDictionary<TKey, int> right) where TKey : notnull =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static SkeletalMeshGlbLod EmptyLod() => new(
        0, new Dictionary<uint, SkeletalMeshGlbVertex>(),
        new Dictionary<SkeletalMeshGlbTriangle, int>(), 0, 0, "unresolved", 0,
        false, false, false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static SourceDependencies ReadSourceDependencies(SkeletalMeshEditableWorkspaceEntry source)
    {
        try
        {
            var path = Path.GetFullPath(source.Manifest.ReconnaissanceJsonPath);
            if (File.Exists(path))
            {
                var report = JsonSerializer.Deserialize<SkeletalMeshReconnaissanceReport>(
                    File.ReadAllText(path), JsonOptions);
                if (report is not null)
                {
                    var dependencies = report.DeformationDependencies;
                    if (dependencies is null ||
                        dependencies.SchemaVersion != SkeletalMeshDeformationDependencyService.CurrentSchemaVersion ||
                        !dependencies.PackageSha256.Equals(report.PackageSha256, StringComparison.OrdinalIgnoreCase) ||
                        dependencies.MeshExportIndex != (report.NativeExportIndex ?? report.ExportIndex))
                    {
                        dependencies = new SkeletalMeshDeformationDependencyService().Analyze(report);
                    }
                    return new SourceDependencies(
                        dependencies.HasActiveClothData,
                        dependencies.HasActiveMorphTargets,
                        dependencies.IsResolved,
                        dependencies);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          JsonException or ArgumentException or NotSupportedException)
        {
            // Missing evidence is converted into a conservative unresolved result below.
        }
        return new SourceDependencies(false, false, false, null);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed record SourceDependencies(
        bool HasClothData,
        bool MayHaveLinkedMorphTargets,
        bool IsResolved,
        SkeletalMeshDeformationDependencyReport? Report);
}
