using System.Numerics;

namespace Arkive.Core.Meshes;

internal sealed record SkeletalMeshAnimationValidationResult(
    SkeletalMeshAnimationPreflight Report,
    IReadOnlyList<SkeletalMeshEditIssue> Issues);

/// <summary>
/// Exercises an edited skinned mesh against deterministic synthetic joint poses.
/// This is deliberately independent of the native compiler: it catches invalid
/// skinning semantics before any replacement payload is produced. It is not a
/// claim that every game animation clip has been tested.
/// </summary>
internal static class SkeletalMeshAnimationValidationService
{
    private const string Profile = "DeterministicBindSpaceStressPosesV1";
    private const int MaximumSampledVertices = 100_000;
    private const int MaximumSampledTriangles = 60_000;
    private const int MaximumSingleBonePoses = 10;
    private const double MinimumSignificantBoneMass = 0.002;
    private const double MinimumEdgeRelativeToExtent = 1e-7;

    public static SkeletalMeshAnimationValidationResult Validate(
        SkeletalMeshGlbSemanticDocument source,
        SkeletalMeshGlbSemanticDocument candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!source.Lods.TryGetValue(0, out var sourceLod) ||
            !candidate.Lods.TryGetValue(0, out var candidateLod))
            throw new InvalidDataException("Animation-aware validation requires source and candidate LOD0.");

        var issues = new List<SkeletalMeshEditIssue>();
        var sourceVertices = SampleVertices(sourceLod.Vertices, MaximumSampledVertices);
        var identical = ReferenceEquals(source, candidate);
        var candidateVertices = identical ? sourceVertices : SampleVertices(candidateLod.Vertices, MaximumSampledVertices);
        var sourceTriangles = SampleTriangles(sourceLod.Triangles.Keys, MaximumSampledTriangles);
        var candidateTriangles = identical ? sourceTriangles : SampleTriangles(candidateLod.Triangles.Keys, MaximumSampledTriangles);
        var skeleton = BuildSkeleton(source.Skeleton);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceMass = BoneMass(sourceVertices);
        var candidateMass = identical ? sourceMass : BoneMass(candidateVertices);
        var allBones = sourceMass.Keys.Union(candidateMass.Keys).Order().ToArray();
        var distributionDistance = 0.5d * allBones.Sum(bone =>
            Math.Abs(sourceMass.GetValueOrDefault(bone) - candidateMass.GetValueOrDefault(bone)));
        var significantBones = sourceMass
            .Where(pair => pair.Value >= MinimumSignificantBoneMass)
            .Select(pair => pair.Key)
            .Order()
            .ToArray();
        var missingSignificant = significantBones.Where(bone =>
            candidateMass.GetValueOrDefault(bone) < Math.Max(1e-6, sourceMass[bone] * 0.1d)).ToArray();
        var missingMass = missingSignificant.Sum(bone => sourceMass[bone]);

        var sourceCentroids = BoneCentroids(sourceVertices);
        var candidateCentroids = identical ? sourceCentroids : BoneCentroids(candidateVertices);
        var sourceExtent = Extent(sourceVertices.Select(pair => pair.Value.Position));
        var candidateExtent = identical ? sourceExtent : Extent(candidateVertices.Select(pair => pair.Value.Position));
        var maximumCentroidShift = significantBones
            .Where(bone => sourceCentroids.ContainsKey(bone) && candidateCentroids.ContainsKey(bone))
            .Select(bone => Vector3.Distance(sourceCentroids[bone], candidateCentroids[bone]))
            .DefaultIfEmpty(0f)
            .Max();
        var centroidShiftRelative = maximumCentroidShift / Math.Max(sourceExtent, 1e-9d);
        var candidateBindExtentRatio = candidateExtent / Math.Max(sourceExtent, 1e-9d);

        var sourceBindError = BindReconstructionError(sourceVertices, skeleton);
        var candidateBindError = identical ? sourceBindError : BindReconstructionError(candidateVertices, skeleton);
        if (sourceBindError > 0.002d || candidateBindError > 0.002d)
            Error("SMA001",
                $"Bind-pose CPU skinning did not reconstruct the exported geometry " +
                $"(source={sourceBindError:G6} m, candidate={candidateBindError:G6} m).");

        // An outfit swap draws another outfit with this skeleton: it hangs from other bones by design (a dress where
        // there were trousers), so differing bone use is only worth a look.
        var outfitSwap = candidate.OutfitSwap;
        if (distributionDistance > 0.55d && !outfitSwap)
            Error("SMA010",
                $"Candidate bone-weight distribution diverges catastrophically from the source " +
                $"(distance={distributionDistance:P2}). This commonly indicates an incompatible skeleton retarget.");
        else if (distributionDistance > 0.30d)
            Warning("SMA010",
                $"Candidate bone-weight distribution differs substantially from the source " +
                $"(distance={distributionDistance:P2}); inspect animated poses carefully.");

        if (missingMass > 0.45d && !outfitSwap)
            Error("SMA011",
                $"Candidate effectively omits {missingSignificant.Length:N0} significant source bone(s), " +
                $"representing {missingMass:P2} of sampled source influence.");
        else if (missingMass > 0.08d || missingSignificant.Length > Math.Max(4, significantBones.Length / 3))
            Warning("SMA011",
                $"Candidate effectively omits {missingSignificant.Length:N0} significant source bone(s), " +
                $"representing {missingMass:P2} of sampled source influence.");

        if (centroidShiftRelative > 0.90d)
            Error("SMA012",
                $"A significantly weighted bone's candidate vertex centroid moved {maximumCentroidShift:N4} m " +
                $"({centroidShiftRelative:P1} of the source extent), indicating incompatible bone semantics or scale.");
        else if (centroidShiftRelative > 0.45d)
            Warning("SMA012",
                $"A significantly weighted bone's candidate vertex centroid moved {maximumCentroidShift:N4} m " +
                $"({centroidShiftRelative:P1} of the source extent).");

        if (candidateBindExtentRatio > 4d || candidateBindExtentRatio < 0.25d)
            Error("SMA013",
                $"Candidate bind-pose extent is {candidateBindExtentRatio:N3}x the source extent; " +
                "the replacement is probably authored at an incompatible scale.");
        else if (candidateBindExtentRatio > 2.5d || candidateBindExtentRatio < 0.4d)
            Warning("SMA013",
                $"Candidate bind-pose extent is {candidateBindExtentRatio:N3}x the source extent.");

        var stressBones = SelectStressBones(sourceMass, candidateMass, source.Skeleton.ParentByBone);
        var poses = new List<SkeletalMeshAnimationPosePreflight>();
        var poseDefinitions = stressBones.Select((bone, index) =>
                new PoseDefinition($"bone-{bone:D4}-{AxisName(bone)}-{(index % 2 == 0 ? "+30" : "-30")}",
                    new Dictionary<int, Quaternion>
                    {
                        [bone] = Quaternion.CreateFromAxisAngle(Axis(bone),
                            Degrees(index % 2 == 0 ? 30f : -30f)),
                    }))
            .ToList();
        if (stressBones.Count > 1)
        {
            var combined = new Dictionary<int, Quaternion>();
            for (var index = 0; index < stressBones.Count; index++)
                combined[stressBones[index]] = Quaternion.CreateFromAxisAngle(
                    Axis(stressBones[index] + 1), Degrees(index % 2 == 0 ? 18f : -18f));
            poseDefinitions.Add(new PoseDefinition("combined-balanced", combined));
        }

        foreach (var pose in poseDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var posedWorld = PoseWorldMatrices(skeleton, pose.Rotations);
            var sourceStats = PoseStats(
                sourceVertices, sourceTriangles, sourceLod.Vertices, skeleton, posedWorld, sourceExtent);
            var candidateStats = identical ? sourceStats : PoseStats(
                candidateVertices, candidateTriangles, candidateLod.Vertices, skeleton, posedWorld, candidateExtent);
            poses.Add(new SkeletalMeshAnimationPosePreflight(
                pose.Name, pose.Rotations.Count,
                sourceVertices.Count, candidateVertices.Count,
                sourceTriangles.Count, candidateTriangles.Count,
                candidateStats.NonFiniteVertexCount,
                sourceStats.MaximumDisplacement, candidateStats.MaximumDisplacement,
                sourceStats.BoundsExpansionRatio, candidateStats.BoundsExpansionRatio,
                sourceStats.MaximumEdgeStretchRatio, candidateStats.MaximumEdgeStretchRatio,
                sourceStats.CollapsedEdgePercent, candidateStats.CollapsedEdgePercent,
                sourceStats.OverstretchedEdgePercent, candidateStats.OverstretchedEdgePercent));
        }

        var nonFinite = poses.Sum(pose => pose.NonFiniteCandidateVertexCount);
        var maximumBoundsExpansion = poses.Select(pose => pose.CandidateBoundsExpansionRatio).DefaultIfEmpty(1d).Max();
        var maximumEdgeStretch = poses.Select(pose => pose.CandidateMaximumEdgeStretchRatio).DefaultIfEmpty(1d).Max();
        var maximumCollapsedEdges = poses.Select(pose => pose.CandidateCollapsedEdgePercent).DefaultIfEmpty(0d).Max();
        var maximumOverstretchedEdges = poses.Select(pose => pose.CandidateOverstretchedEdgePercent).DefaultIfEmpty(0d).Max();
        var maximumSourceBoundsExpansion = poses.Select(pose => pose.SourceBoundsExpansionRatio).DefaultIfEmpty(1d).Max();
        var maximumSourceEdgeStretch = poses.Select(pose => pose.SourceMaximumEdgeStretchRatio).DefaultIfEmpty(1d).Max();
        var maximumSourceCollapsedEdges = poses.Select(pose => pose.SourceCollapsedEdgePercent).DefaultIfEmpty(0d).Max();

        if (nonFinite > 0)
            Error("SMA020", $"Synthetic poses produced {nonFinite:N0} non-finite candidate vertex result(s).");
        if (maximumBoundsExpansion > Math.Max(4d, maximumSourceBoundsExpansion * 3d))
            Error("SMA021",
                $"Synthetic poses expanded candidate bounds by {maximumBoundsExpansion:N3}x " +
                $"(source maximum {maximumSourceBoundsExpansion:N3}x).");
        if (maximumEdgeStretch > Math.Max(8d, maximumSourceEdgeStretch * 3d) &&
            maximumOverstretchedEdges > 0.25d)
            Error("SMA022",
                $"Synthetic poses caused catastrophic candidate edge stretching: maximum={maximumEdgeStretch:N3}x, " +
                $"edges over 4x={maximumOverstretchedEdges:N3}%.");
        if (maximumCollapsedEdges > Math.Max(10d, maximumSourceCollapsedEdges + 8d))
            Error("SMA023",
                $"Synthetic poses collapsed {maximumCollapsedEdges:N3}% of sampled candidate edges " +
                $"(source maximum {maximumSourceCollapsedEdges:N3}%).");

        var passed = issues.All(issue => issue.Severity != SkeletalMeshEditIssueSeverity.Error);
        if (poseDefinitions.Count == 0)
            Info("SMA090",
                "The reference skeleton has no weighted articulating bone; bind-space validation passed without stress poses.");
        else if (passed)
            Info("SMA099",
                $"Passed {poseDefinitions.Count:N0} deterministic CPU-skinned stress pose(s). " +
                "This validates deformation invariants but does not execute the game's animation clips.");

        var report = new SkeletalMeshAnimationPreflight(
            Profile, passed, poseDefinitions.Count == 0, false, poseDefinitions.Count,
            sourceMass.Count(pair => pair.Value > 1e-6), candidateMass.Count(pair => pair.Value > 1e-6),
            significantBones.Length, missingSignificant.Length, missingMass * 100d,
            distributionDistance, maximumCentroidShift, centroidShiftRelative,
            sourceBindError, candidateBindError, candidateBindExtentRatio, nonFinite,
            maximumBoundsExpansion, maximumEdgeStretch, maximumCollapsedEdges,
            maximumOverstretchedEdges, stressBones, poses);
        return new SkeletalMeshAnimationValidationResult(report, issues);

        void Info(string code, string message) =>
            issues.Add(new SkeletalMeshEditIssue(SkeletalMeshEditIssueSeverity.Info, code, message));
        void Warning(string code, string message) =>
            issues.Add(new SkeletalMeshEditIssue(SkeletalMeshEditIssueSeverity.Warning, code, message));
        void Error(string code, string message) =>
            issues.Add(new SkeletalMeshEditIssue(SkeletalMeshEditIssueSeverity.Error, code, message));
    }

    private static SkeletonMatrices BuildSkeleton(SkeletalMeshGlbSkeleton skeleton)
    {
        var inverseBind = skeleton.InverseBindByBone.ToDictionary(
            pair => pair.Key, pair => Matrix(pair.Value));
        var bindWorld = new Dictionary<int, Matrix4x4>();
        foreach (var (bone, inverse) in inverseBind)
        {
            if (!Matrix4x4.Invert(inverse, out var world) || !Finite(world))
                throw new InvalidDataException($"Reference bone {bone} has a non-invertible inverse bind matrix.");
            bindWorld[bone] = world;
        }
        var bindLocal = new Dictionary<int, Matrix4x4>();
        foreach (var bone in bindWorld.Keys)
        {
            var parent = skeleton.ParentByBone.GetValueOrDefault(bone, -1);
            if (parent >= 0 && bindWorld.TryGetValue(parent, out var parentWorld) &&
                Matrix4x4.Invert(parentWorld, out var inverseParent))
                bindLocal[bone] = bindWorld[bone] * inverseParent;
            else
                bindLocal[bone] = bindWorld[bone];
        }
        return new SkeletonMatrices(skeleton.ParentByBone, inverseBind, bindWorld, bindLocal);
    }

    private static IReadOnlyDictionary<int, Matrix4x4> PoseWorldMatrices(
        SkeletonMatrices skeleton,
        IReadOnlyDictionary<int, Quaternion> rotations)
    {
        var result = new Dictionary<int, Matrix4x4>();
        var visiting = new HashSet<int>();
        Matrix4x4 Resolve(int bone)
        {
            if (result.TryGetValue(bone, out var resolved)) return resolved;
            if (!visiting.Add(bone)) throw new InvalidDataException("Reference skeleton contains a parent cycle.");
            var local = skeleton.BindLocal[bone];
            if (rotations.TryGetValue(bone, out var delta))
            {
                if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var translation))
                    throw new InvalidDataException($"Reference bone {bone} local bind matrix cannot be decomposed.");
                local = Matrix4x4.CreateScale(scale) *
                        Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation * delta)) *
                        Matrix4x4.CreateTranslation(translation);
            }
            var parent = skeleton.ParentByBone.GetValueOrDefault(bone, -1);
            resolved = parent >= 0 && skeleton.BindLocal.ContainsKey(parent)
                ? local * Resolve(parent)
                : local;
            visiting.Remove(bone);
            result[bone] = resolved;
            return resolved;
        }
        foreach (var bone in skeleton.BindLocal.Keys) Resolve(bone);
        return result;
    }

    private static double BindReconstructionError(
        IReadOnlyList<KeyValuePair<uint, SkeletalMeshGlbVertex>> vertices,
        SkeletonMatrices skeleton)
    {
        var maximum = 0d;
        foreach (var pair in vertices)
        {
            var skinned = Skin(pair.Value, skeleton, skeleton.BindWorld);
            if (!Finite(skinned)) return double.PositiveInfinity;
            maximum = Math.Max(maximum, Vector3.Distance(pair.Value.Position, skinned));
        }
        return maximum;
    }

    private static PoseStatistics PoseStats(
        IReadOnlyList<KeyValuePair<uint, SkeletalMeshGlbVertex>> vertices,
        IReadOnlyList<SkeletalMeshGlbTriangle> triangles,
        IReadOnlyDictionary<uint, SkeletalMeshGlbVertex> vertexLookup,
        SkeletonMatrices skeleton,
        IReadOnlyDictionary<int, Matrix4x4> posedWorld,
        double bindExtent)
    {
        var posed = new Dictionary<uint, Vector3>(vertices.Count);
        var nonFinite = 0;
        var maximumDisplacement = 0d;
        foreach (var (id, vertex) in vertices)
        {
            var position = Skin(vertex, skeleton, posedWorld);
            if (!Finite(position)) nonFinite++;
            else maximumDisplacement = Math.Max(maximumDisplacement, Vector3.Distance(vertex.Position, position));
            posed[id] = position;
        }

        Vector3 Posed(uint id, SkeletalMeshGlbVertex vertex)
        {
            if (posed.TryGetValue(id, out var value)) return value;
            value = Skin(vertex, skeleton, posedWorld);
            posed[id] = value;
            return value;
        }

        var finitePositions = posed.Values.Where(Finite).ToArray();
        var posedExtent = Extent(finitePositions);
        var boundsExpansion = posedExtent / Math.Max(bindExtent, 1e-9d);
        var edgeCount = 0;
        var collapsed = 0;
        var overstretched = 0;
        var maximumStretch = 1d;
        foreach (var triangle in triangles)
        {
            if (!vertexLookup.TryGetValue(triangle.A, out var a) ||
                !vertexLookup.TryGetValue(triangle.B, out var b) ||
                !vertexLookup.TryGetValue(triangle.C, out var c)) continue;
            Edge(triangle.A, a, triangle.B, b);
            Edge(triangle.B, b, triangle.C, c);
            Edge(triangle.C, c, triangle.A, a);
        }
        return new PoseStatistics(nonFinite, maximumDisplacement, boundsExpansion, maximumStretch,
            edgeCount == 0 ? 0d : 100d * collapsed / edgeCount,
            edgeCount == 0 ? 0d : 100d * overstretched / edgeCount);

        void Edge(uint leftId, SkeletalMeshGlbVertex left, uint rightId, SkeletalMeshGlbVertex right)
        {
            var before = Vector3.Distance(left.Position, right.Position);
            if (before <= Math.Max(bindExtent * MinimumEdgeRelativeToExtent, 1e-9d)) return;
            var afterLeft = Posed(leftId, left);
            var afterRight = Posed(rightId, right);
            if (!Finite(afterLeft) || !Finite(afterRight)) return;
            var ratio = Vector3.Distance(afterLeft, afterRight) / before;
            maximumStretch = Math.Max(maximumStretch, ratio);
            if (ratio < 0.15d) collapsed++;
            if (ratio > 4d) overstretched++;
            edgeCount++;
        }
    }

    private static Vector3 Skin(
        SkeletalMeshGlbVertex vertex,
        SkeletonMatrices skeleton,
        IReadOnlyDictionary<int, Matrix4x4> posedWorld)
    {
        var result = Vector3.Zero;
        var total = 0f;
        foreach (var (bone, rawWeight) in vertex.SkinWeights)
        {
            if (rawWeight <= 0f || !float.IsFinite(rawWeight) ||
                !skeleton.InverseBind.TryGetValue(bone, out var inverse) ||
                !posedWorld.TryGetValue(bone, out var world)) continue;
            result += Vector3.Transform(vertex.Position, inverse * world) * rawWeight;
            total += rawWeight;
        }
        return total > 1e-8f ? result / total : vertex.Position;
    }

    private static Dictionary<int, double> BoneMass(
        IReadOnlyList<KeyValuePair<uint, SkeletalMeshGlbVertex>> vertices)
    {
        var result = new Dictionary<int, double>();
        foreach (var (_, vertex) in vertices)
        {
            var sum = vertex.SkinWeights.Where(pair => pair.Value > 0f).Sum(pair => pair.Value);
            if (sum <= 1e-8f) continue;
            foreach (var (bone, weight) in vertex.SkinWeights)
                if (weight > 0f) result[bone] = result.GetValueOrDefault(bone) + weight / sum;
        }
        var total = result.Values.Sum();
        if (total > 0d)
            foreach (var bone in result.Keys.ToArray()) result[bone] /= total;
        return result;
    }

    private static Dictionary<int, Vector3> BoneCentroids(
        IReadOnlyList<KeyValuePair<uint, SkeletalMeshGlbVertex>> vertices)
    {
        var sums = new Dictionary<int, Vector3>();
        var masses = new Dictionary<int, double>();
        foreach (var (_, vertex) in vertices)
        {
            var total = vertex.SkinWeights.Where(pair => pair.Value > 0f).Sum(pair => pair.Value);
            if (total <= 1e-8f) continue;
            foreach (var (bone, rawWeight) in vertex.SkinWeights)
            {
                if (rawWeight <= 0f) continue;
                var weight = rawWeight / total;
                sums[bone] = sums.GetValueOrDefault(bone) + vertex.Position * weight;
                masses[bone] = masses.GetValueOrDefault(bone) + weight;
            }
        }
        return sums.Where(pair => masses.GetValueOrDefault(pair.Key) > 1e-9)
            .ToDictionary(pair => pair.Key, pair => pair.Value / (float)masses[pair.Key]);
    }

    private static IReadOnlyList<int> SelectStressBones(
        IReadOnlyDictionary<int, double> sourceMass,
        IReadOnlyDictionary<int, double> candidateMass,
        IReadOnlyDictionary<int, int> parents) =>
        sourceMass.Keys.Union(candidateMass.Keys)
            .Where(bone => parents.GetValueOrDefault(bone, -1) >= 0)
            .Select(bone => new
            {
                Bone = bone,
                Score = Math.Max(sourceMass.GetValueOrDefault(bone), candidateMass.GetValueOrDefault(bone)) +
                        2d * Math.Abs(sourceMass.GetValueOrDefault(bone) - candidateMass.GetValueOrDefault(bone)),
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Bone)
            .Take(MaximumSingleBonePoses)
            .Select(item => item.Bone)
            .ToArray();

    private static IReadOnlyList<KeyValuePair<uint, SkeletalMeshGlbVertex>> SampleVertices(
        IReadOnlyDictionary<uint, SkeletalMeshGlbVertex> vertices,
        int maximum)
    {
        var ordered = vertices.OrderBy(pair => pair.Key).ToArray();
        if (ordered.Length <= maximum) return ordered;
        var stride = (int)Math.Ceiling(ordered.Length / (double)maximum);
        return ordered.Where((_, index) => index % stride == 0).Take(maximum).ToArray();
    }

    private static IReadOnlyList<SkeletalMeshGlbTriangle> SampleTriangles(
        IEnumerable<SkeletalMeshGlbTriangle> triangles,
        int maximum)
    {
        var ordered = triangles.OrderBy(item => item.A).ThenBy(item => item.B).ThenBy(item => item.C)
            .ThenBy(item => item.Material, StringComparer.OrdinalIgnoreCase).ToArray();
        if (ordered.Length <= maximum) return ordered;
        var stride = (int)Math.Ceiling(ordered.Length / (double)maximum);
        return ordered.Where((_, index) => index % stride == 0).Take(maximum).ToArray();
    }

    private static double Extent(IEnumerable<Vector3> positions)
    {
        using var iterator = positions.GetEnumerator();
        if (!iterator.MoveNext()) return 0d;
        var minimum = iterator.Current;
        var maximum = iterator.Current;
        while (iterator.MoveNext())
        {
            if (!Finite(iterator.Current)) continue;
            minimum = Vector3.Min(minimum, iterator.Current);
            maximum = Vector3.Max(maximum, iterator.Current);
        }
        return Vector3.Distance(minimum, maximum);
    }

    private static Matrix4x4 Matrix(IReadOnlyList<float> values)
    {
        if (values.Count != 16) throw new InvalidDataException("Inverse bind matrix does not contain 16 values.");
        return new Matrix4x4(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11],
            values[12], values[13], values[14], values[15]);
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool Finite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    private static Vector3 Axis(int bone) => Math.Abs(bone % 3) switch
    {
        0 => Vector3.UnitX,
        1 => Vector3.UnitY,
        _ => Vector3.UnitZ,
    };

    private static string AxisName(int bone) => Math.Abs(bone % 3) switch
    {
        0 => "x",
        1 => "y",
        _ => "z",
    };

    private static float Degrees(float degrees) => degrees * MathF.PI / 180f;

    private sealed record SkeletonMatrices(
        IReadOnlyDictionary<int, int> ParentByBone,
        IReadOnlyDictionary<int, Matrix4x4> InverseBind,
        IReadOnlyDictionary<int, Matrix4x4> BindWorld,
        IReadOnlyDictionary<int, Matrix4x4> BindLocal);

    private sealed record PoseDefinition(string Name, IReadOnlyDictionary<int, Quaternion> Rotations);

    private sealed record PoseStatistics(
        int NonFiniteVertexCount,
        double MaximumDisplacement,
        double BoundsExpansionRatio,
        double MaximumEdgeStretchRatio,
        double CollapsedEdgePercent,
        double OverstretchedEdgePercent);
}
