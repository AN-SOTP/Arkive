using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Arkive.Core.Meshes;

/// <summary>
/// Produces a deterministic, weight-aware comparison of two Arkive reference
/// skeletons. Exact target-skeleton preservation is the only automatic import
/// route. Cross-rig results are authoring evidence, never permission to collapse
/// unmatched weights to an ancestor.
/// </summary>
public sealed class SkeletalMeshSkeletonCompatibilityService
{
    public const int CurrentSchemaVersion = 1;
    private const double MatrixTolerance = 2e-4d;

    public async Task<SkeletalMeshSkeletonCompatibilityReport> AnalyzeAsync(
        SkeletalMeshEditableWorkspaceEntry source,
        SkeletalMeshEditableWorkspaceEntry target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.GlbExists || !File.Exists(source.GlbPath))
            throw new FileNotFoundException("The source editable GLB is missing.", source.GlbPath);
        if (!target.GlbExists || !File.Exists(target.GlbPath))
            throw new FileNotFoundException("The target editable GLB is missing.", target.GlbPath);
        var sourceHash = await HashFileAsync(source.GlbPath, cancellationToken).ConfigureAwait(false);
        var targetHash = await HashFileAsync(target.GlbPath, cancellationToken).ConfigureAwait(false);
        if (!sourceHash.Equals(source.Manifest.GlbSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "SKC002: The donor GLB no longer matches its clean editable-export manifest.");
        if (!targetHash.Equals(target.Manifest.GlbSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "SKC003: The target GLB no longer matches its clean editable-export manifest.");
        var sourceDocument = SkeletalMeshGlbSemanticReader.Read(
            source.GlbPath, source.Manifest, SkeletalMeshGlbReadProfile.TopologyCandidate);
        var targetDocument = SkeletalMeshGlbSemanticReader.Read(
            target.GlbPath, target.Manifest, SkeletalMeshGlbReadProfile.TopologyCandidate);
        return Analyze(
            sourceDocument, SourceIdentity(source) with { GlbSha256 = sourceHash },
            targetDocument, SourceIdentity(target) with { GlbSha256 = targetHash });
    }

    internal SkeletalMeshSkeletonCompatibilityReport Analyze(
        SkeletalMeshGlbSemanticDocument sourceDocument,
        SkeletalMeshSkeletonIdentity sourceIdentity,
        SkeletalMeshGlbSemanticDocument targetDocument,
        SkeletalMeshSkeletonIdentity targetIdentity)
    {
        var source = BuildProfile(sourceDocument, sourceIdentity);
        var target = BuildProfile(targetDocument, targetIdentity);
        var sourceBones = source.Bones.ToDictionary(bone => bone.BoneIndex);
        var targetNames = target.Bones.GroupBy(bone => bone.CanonicalName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var mappings = new List<SkeletalMeshBoneCompatibilityMapping>(source.BoneCount);
        var mappedIndex = new Dictionary<int, int>();

        foreach (var bone in source.Bones.OrderBy(item => item.BoneIndex))
        {
            if (!targetNames.TryGetValue(bone.CanonicalName, out var candidates))
            {
                mappings.Add(Mapping(bone, null, SkeletalMeshBoneMappingKind.MissingFromTarget,
                    false, 0d, 0d, "No target bone has this exact canonical name."));
                continue;
            }
            if (candidates.Length != 1)
            {
                mappings.Add(Mapping(bone, null, SkeletalMeshBoneMappingKind.AmbiguousName,
                    false, 0d, 0d, $"{candidates.Length:N0} target bones share this canonical name."));
                continue;
            }
            mappedIndex[bone.BoneIndex] = candidates[0].BoneIndex;
        }

        foreach (var bone in source.Bones.OrderBy(item => item.BoneIndex))
        {
            if (!mappedIndex.TryGetValue(bone.BoneIndex, out var targetIndex)) continue;
            var targetBone = target.Bones.Single(item => item.BoneIndex == targetIndex);
            var parentChainMatches = ParentChainMatches(
                bone.BoneIndex, targetIndex, sourceBones,
                target.Bones.ToDictionary(item => item.BoneIndex), mappedIndex);
            var (translation, rotation) = BindDelta(sourceDocument.Skeleton, bone.BoneIndex,
                targetDocument.Skeleton, targetIndex);
            mappings.Add(Mapping(
                bone, targetBone,
                parentChainMatches
                    ? SkeletalMeshBoneMappingKind.ExactNameAndHierarchy
                    : SkeletalMeshBoneMappingKind.ExactNameHierarchyChanged,
                parentChainMatches, translation, rotation,
                parentChainMatches
                    ? "Exact canonical name and mapped parent chain."
                    : "The name matches, but the mapped parent chain differs."));
        }

        mappings = mappings.OrderBy(item => item.SourceBoneIndex).ToList();
        var exactIdentity = SkeletonEquivalent(sourceDocument.Skeleton, targetDocument.Skeleton);
        var mapped = mappings.Where(item => item.TargetBoneIndex is not null).ToArray();
        var exactNameCount = mapped.Length;
        var hierarchyCount = mapped.Count(item => item.ParentChainMatches);
        var missing = mappings.Count(item => item.MappingKind == SkeletalMeshBoneMappingKind.MissingFromTarget);
        var ambiguous = mappings.Count(item => item.MappingKind == SkeletalMeshBoneMappingKind.AmbiguousName);
        var missingCore = mappings.Count(item => item.IsCoreDeformationBone && item.TargetBoneIndex is null);
        var mappedWeight = mapped.Sum(item => item.SourceWeightMassPercent);
        var hierarchyWeight = mapped.Where(item => item.ParentChainMatches)
            .Sum(item => item.SourceWeightMassPercent);
        var missingWeight = Math.Max(0d, 100d - mappedWeight);
        var targetExtent = BindExtent(targetDocument.Skeleton);
        var maxTranslation = mapped.Select(item => item.BindTranslationDeltaMetres).DefaultIfEmpty(0d).Max();
        var maxRotation = mapped.Select(item => item.BindRotationDeltaDegrees).DefaultIfEmpty(0d).Max();
        var relativeTranslation = maxTranslation / Math.Max(targetExtent, 1e-9d);
        var sameNamedHierarchy = missing == 0 && ambiguous == 0 &&
                                 source.BoneCount == target.BoneCount &&
                                 hierarchyCount == source.BoneCount;

        var compatibility = exactIdentity
            ? SkeletalMeshSkeletonCompatibilityKind.ExactTargetSkeleton
            : sameNamedHierarchy
                ? SkeletalMeshSkeletonCompatibilityKind.SameNamedHierarchyAlteredBindPose
                : missingCore == 0 && ambiguous == 0 && mappedWeight >= 99d && hierarchyWeight >= 98d
                    ? SkeletalMeshSkeletonCompatibilityKind.RetargetAuthoringCandidate
                    : mappedWeight >= 50d && exactNameCount > 0
                        ? SkeletalMeshSkeletonCompatibilityKind.ArtistRiggingRequired
                        : SkeletalMeshSkeletonCompatibilityKind.Incompatible;

        var issues = BuildIssues(
            compatibility, missing, ambiguous, missingCore, missingWeight,
            hierarchyWeight, maxTranslation, relativeTranslation, maxRotation);
        var summary = compatibility switch
        {
            SkeletalMeshSkeletonCompatibilityKind.ExactTargetSkeleton =>
                "Exact target skeleton identity, hierarchy and bind pose are preserved.",
            SkeletalMeshSkeletonCompatibilityKind.SameNamedHierarchyAlteredBindPose =>
                "Bone names and hierarchy match, but the bind pose changed; automatic import is refused.",
            SkeletalMeshSkeletonCompatibilityKind.RetargetAuthoringCandidate =>
                "The rigs have complete weighted/core name coverage; a target-armature Blender re-rig is viable.",
            SkeletalMeshSkeletonCompatibilityKind.ArtistRiggingRequired =>
                "The rigs overlap, but unmatched or structurally different bones require manual fitting and weight painting.",
            _ => "The rigs do not have enough proven correspondence for a bounded retarget workflow.",
        };

        return new SkeletalMeshSkeletonCompatibilityReport(
            CurrentSchemaVersion, DateTimeOffset.UtcNow, compatibility,
            exactIdentity, exactIdentity,
            compatibility != SkeletalMeshSkeletonCompatibilityKind.Incompatible,
            summary, source, target, exactNameCount, hierarchyCount, missing, ambiguous,
            missingCore, mappedWeight, hierarchyWeight, missingWeight,
            maxTranslation, relativeTranslation, maxRotation, mappings, issues);
    }

    internal static SkeletalMeshSkeletonIdentity SourceIdentity(
        SkeletalMeshEditableWorkspaceEntry entry) => new(
        entry.Manifest.ExportId, entry.Manifest.PackageAlias, entry.Manifest.ObjectName,
        entry.ManifestPath, entry.GlbPath, entry.Manifest.GlbSha256);

    private static SkeletalMeshSkeletonProfile BuildProfile(
        SkeletalMeshGlbSemanticDocument document,
        SkeletalMeshSkeletonIdentity identity)
    {
        var skeleton = document.Skeleton;
        var world = BindWorld(skeleton);
        var mass = BoneMass(document);
        var total = mass.Values.Sum();
        var bones = skeleton.ParentByBone.Keys.Order().Select(index =>
        {
            var name = skeleton.NameByBone.GetValueOrDefault(index, $"bone_{index:D4}");
            var parent = skeleton.ParentByBone.GetValueOrDefault(index, -1);
            return new SkeletalMeshSkeletonBoneProfile(
                index, name, Canonical(name), parent,
                parent >= 0 ? skeleton.NameByBone.GetValueOrDefault(parent, $"bone_{parent:D4}") : "-",
                Depth(index, skeleton.ParentByBone), Path(index, skeleton), IsCore(name),
                total <= 1e-12d ? 0d : 100d * mass.GetValueOrDefault(index) / total,
                world[index].M41, world[index].M42, world[index].M43);
        }).ToArray();
        var hierarchyText = string.Join("\n", bones.Select(bone =>
            $"{bone.BoneIndex}|{bone.CanonicalName}|{bone.ParentIndex}|{bone.HierarchyPath}"));
        var bindText = string.Join("\n", world.OrderBy(pair => pair.Key).Select(pair =>
            $"{pair.Key}|{string.Join(',', MatrixValues(pair.Value).Select(value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}"));
        return new SkeletalMeshSkeletonProfile(
            identity.ExportId, identity.PackageAlias, identity.ObjectName,
            identity.ManifestPath, identity.GlbPath, identity.GlbSha256,
            bones.Length, bones.Count(bone => bone.ParentIndex < 0),
            bones.Count(bone => bone.WeightedVertexMassPercent > 1e-7d),
            bones.Count(bone => bone.IsCoreDeformationBone),
            Sha(hierarchyText), Sha(bindText), bones);
    }

    private static IReadOnlyList<SkeletalMeshEditIssue> BuildIssues(
        SkeletalMeshSkeletonCompatibilityKind compatibility,
        int missing, int ambiguous, int missingCore, double missingWeight,
        double hierarchyWeight, double translation, double relativeTranslation, double rotation)
    {
        var issues = new List<SkeletalMeshEditIssue>();
        if (compatibility == SkeletalMeshSkeletonCompatibilityKind.ExactTargetSkeleton)
            issues.Add(new SkeletalMeshEditIssue(SkeletalMeshEditIssueSeverity.Info, "SKC099",
                "Exact target skeleton names, stable IDs, hierarchy and inverse bind matrices match."));
        else
            issues.Add(new SkeletalMeshEditIssue(SkeletalMeshEditIssueSeverity.Error, "SKC001",
                "Automatic native import requires the exact target skeleton. Use the retarget authoring workspace and export with the untouched target armature."));
        if (missing > 0)
            issues.Add(new SkeletalMeshEditIssue(missingCore > 0 || missingWeight > 1d
                    ? SkeletalMeshEditIssueSeverity.Error : SkeletalMeshEditIssueSeverity.Warning,
                "SKC010", $"{missing:N0} source bone(s) are absent from the target; affected source weight mass is {missingWeight:N3}%."));
        if (ambiguous > 0)
            issues.Add(new SkeletalMeshEditIssue(SkeletalMeshEditIssueSeverity.Error, "SKC011",
                $"{ambiguous:N0} source bone name(s) are ambiguous in the target skeleton."));
        if (missingCore > 0)
            issues.Add(new SkeletalMeshEditIssue(SkeletalMeshEditIssueSeverity.Error, "SKC012",
                $"{missingCore:N0} core deformation bone(s) have no exact target-name mapping."));
        if (hierarchyWeight < 99.999d)
            issues.Add(new SkeletalMeshEditIssue(
                hierarchyWeight < 98d ? SkeletalMeshEditIssueSeverity.Error : SkeletalMeshEditIssueSeverity.Warning,
                "SKC020", $"Only {hierarchyWeight:N3}% of source weight mass maps through a consistent parent chain."));
        if (translation > 2e-4d || rotation > 0.1d)
            issues.Add(new SkeletalMeshEditIssue(SkeletalMeshEditIssueSeverity.Warning, "SKC021",
                $"Mapped bind poses differ: maximum translation={translation:N6} m ({relativeTranslation:P2} of target extent), rotation={rotation:N3}°. Geometry must be fitted in the target bind pose."));
        issues.Add(new SkeletalMeshEditIssue(SkeletalMeshEditIssueSeverity.Info, "SKC090",
            "Arkive never maps an unmatched donor bone to an ancestor automatically; unresolved groups remain an artist task."));
        return issues;
    }

    private static SkeletalMeshBoneCompatibilityMapping Mapping(
        SkeletalMeshSkeletonBoneProfile source,
        SkeletalMeshSkeletonBoneProfile? target,
        SkeletalMeshBoneMappingKind kind,
        bool hierarchy,
        double translation,
        double rotation,
        string diagnostic) => new(
        source.BoneIndex, source.Name, target?.BoneIndex, target?.Name, kind, hierarchy,
        source.IsCoreDeformationBone, source.WeightedVertexMassPercent,
        translation, rotation, diagnostic);

    private static bool ParentChainMatches(
        int sourceIndex,
        int targetIndex,
        IReadOnlyDictionary<int, SkeletalMeshSkeletonBoneProfile> source,
        IReadOnlyDictionary<int, SkeletalMeshSkeletonBoneProfile> target,
        IReadOnlyDictionary<int, int> mapped)
    {
        var sourceParent = source[sourceIndex].ParentIndex;
        while (sourceParent >= 0 && !mapped.ContainsKey(sourceParent))
            sourceParent = source.GetValueOrDefault(sourceParent)?.ParentIndex ?? -1;
        var targetParent = target[targetIndex].ParentIndex;
        if (sourceParent < 0) return targetParent < 0;
        var expected = mapped[sourceParent];
        while (targetParent >= 0)
        {
            if (targetParent == expected) return true;
            targetParent = target.GetValueOrDefault(targetParent)?.ParentIndex ?? -1;
        }
        return false;
    }

    private static (double Translation, double Rotation) BindDelta(
        SkeletalMeshGlbSkeleton source, int sourceIndex,
        SkeletalMeshGlbSkeleton target, int targetIndex)
    {
        var sourceWorld = BindWorld(source)[sourceIndex];
        var targetWorld = BindWorld(target)[targetIndex];
        var translation = Vector3.Distance(sourceWorld.Translation, targetWorld.Translation);
        var sourceRotation = Rotation(sourceWorld);
        var targetRotation = Rotation(targetWorld);
        var dot = Math.Clamp(Math.Abs(Quaternion.Dot(sourceRotation, targetRotation)), 0f, 1f);
        var rotation = 2d * Math.Acos(dot) * 180d / Math.PI;
        return (translation, rotation);
    }

    private static bool SkeletonEquivalent(SkeletalMeshGlbSkeleton source, SkeletalMeshGlbSkeleton target)
    {
        if (!source.ParentByBone.Keys.Order().SequenceEqual(target.ParentByBone.Keys.Order())) return false;
        foreach (var bone in source.ParentByBone.Keys)
        {
            if (source.ParentByBone[bone] != target.ParentByBone.GetValueOrDefault(bone) ||
                !Canonical(source.NameByBone.GetValueOrDefault(bone, string.Empty)).Equals(
                    Canonical(target.NameByBone.GetValueOrDefault(bone, string.Empty)), StringComparison.Ordinal) ||
                !source.InverseBindByBone.TryGetValue(bone, out var left) ||
                !target.InverseBindByBone.TryGetValue(bone, out var right) || left.Length != right.Length)
                return false;
            for (var index = 0; index < left.Length; index++)
                if (Math.Abs(left[index] - right[index]) > MatrixTolerance) return false;
        }
        return true;
    }

    private static Dictionary<int, Matrix4x4> BindWorld(SkeletalMeshGlbSkeleton skeleton)
    {
        var result = new Dictionary<int, Matrix4x4>();
        foreach (var (bone, values) in skeleton.InverseBindByBone)
        {
            var inverse = Matrix(values);
            if (!Matrix4x4.Invert(inverse, out var world))
                throw new InvalidDataException($"Bone {bone} has a non-invertible inverse bind matrix.");
            result[bone] = world;
        }
        return result;
    }

    private static Dictionary<int, double> BoneMass(SkeletalMeshGlbSemanticDocument document)
    {
        var lod = document.Lods.OrderBy(pair => pair.Key).First().Value;
        var result = new Dictionary<int, double>();
        foreach (var vertex in lod.Vertices.Values)
            foreach (var (bone, weight) in vertex.SkinWeights)
                if (weight > 0f) result[bone] = result.GetValueOrDefault(bone) + weight;
        return result;
    }

    private static double BindExtent(SkeletalMeshGlbSkeleton skeleton)
    {
        var positions = BindWorld(skeleton).Values.Select(value => value.Translation).ToArray();
        if (positions.Length == 0) return 0d;
        var minimum = positions.Aggregate(Vector3.Min);
        var maximum = positions.Aggregate(Vector3.Max);
        return Vector3.Distance(minimum, maximum);
    }

    private static int Depth(int bone, IReadOnlyDictionary<int, int> parents)
    {
        var depth = 0;
        var seen = new HashSet<int>();
        while (parents.GetValueOrDefault(bone, -1) is var parent && parent >= 0)
        {
            if (!seen.Add(bone)) throw new InvalidDataException("Skeleton contains a parent cycle.");
            bone = parent;
            depth++;
        }
        return depth;
    }

    private static string Path(int bone, SkeletalMeshGlbSkeleton skeleton)
    {
        var names = new Stack<string>();
        var seen = new HashSet<int>();
        while (bone >= 0)
        {
            if (!seen.Add(bone)) throw new InvalidDataException("Skeleton contains a parent cycle.");
            names.Push(Canonical(skeleton.NameByBone.GetValueOrDefault(bone, $"bone_{bone:D4}")));
            bone = skeleton.ParentByBone.GetValueOrDefault(bone, -1);
        }
        return string.Join('/', names);
    }

    internal static string Canonical(string name)
    {
        var marker = name.LastIndexOf("__ARKIVE_", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) name = name[..marker];
        return name.Trim().ToLowerInvariant();
    }

    private static bool IsCore(string name)
    {
        var value = Canonical(name).Replace('_', '-');
        string[] tokens = ["root", "pelvis", "spine", "neck", "head", "clavicle", "upperarm",
            "forearm", "hand", "thigh", "calf", "foot", "toe"];
        return tokens.Any(token => value.Contains(token, StringComparison.Ordinal));
    }

    private static Quaternion Rotation(Matrix4x4 matrix) =>
        Matrix4x4.Decompose(matrix, out _, out var rotation, out _)
            ? Quaternion.Normalize(rotation)
            : Quaternion.Identity;

    private static Matrix4x4 Matrix(IReadOnlyList<float> values) => new(
        values[0], values[1], values[2], values[3],
        values[4], values[5], values[6], values[7],
        values[8], values[9], values[10], values[11],
        values[12], values[13], values[14], values[15]);

    private static IEnumerable<float> MatrixValues(Matrix4x4 value)
    {
        yield return value.M11; yield return value.M12; yield return value.M13; yield return value.M14;
        yield return value.M21; yield return value.M22; yield return value.M23; yield return value.M24;
        yield return value.M31; yield return value.M32; yield return value.M33; yield return value.M34;
        yield return value.M41; yield return value.M42; yield return value.M43; yield return value.M44;
    }

    private static string Sha(string text) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal sealed record SkeletalMeshSkeletonIdentity(
    string ExportId,
    string PackageAlias,
    string ObjectName,
    string ManifestPath,
    string GlbPath,
    string GlbSha256);
