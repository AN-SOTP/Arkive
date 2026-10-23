using System.Numerics;
using System.Text.RegularExpressions;

namespace Arkive.Core.Outfits;

/// <summary>
/// Moving an outfit from one body to another: first with the skeleton (where each joint is on the new body), then by the
/// body-shape difference the game's own shared costumes show, smoothed over the outfit so no face tears.
/// </summary>
public static partial class OutfitSwapFitting
{
    // Body bones that turn on their own: the limb and spine segments, and hands and fingers (each class rests its fingers
    // differently). Feet keep the way they stood on the donor (see BoneMaps). The head and twist helpers turn with their
    // parent; outfit bones (skirts, capes, hair)
    // follow their nearest body bone entirely, because they sit differently from one outfit to the next.
    [GeneratedRegex(@"^bip001-(spine\d?|neck|[lr]-(clavicle|upperarm|forearm|thigh|calf|hand|finger\d+))$")]
    private static partial Regex TurnsOnItsOwn();

    [GeneratedRegex(@"^bip001-([lr]-(hand|foot)|head)$")]
    private static partial Regex LimbEnd();

    [GeneratedRegex(@"^bip001|twist")]
    private static partial Regex BodyBone();

    /// <summary>How one donor bone carries its vertices onto the target body.</summary>
    public readonly record struct BoneMap(Quaternion Turn, Vector3 From, Vector3 To)
    {
        public Vector3 Apply(Vector3 point) => To + Vector3.Transform(point - From, Turn);
    }

    public static BoneMap[] BoneMaps(OutfitSwapMesh donor, OutfitSwapSkeleton target)
    {
        var maps = new BoneMap?[donor.BoneNames.Length];
        var children = new List<int>[donor.BoneNames.Length];
        for (var i = 0; i < children.Length; i++) children[i] = [];
        for (var i = 0; i < donor.BoneParents.Length; i++)
            if (donor.BoneParents[i] >= 0) children[donor.BoneParents[i]].Add(i);

        BoneMap Compute(int bone)
        {
            if (maps[bone] is { } known) return known;
            var name = donor.BoneNames[bone];
            var parent = donor.BoneParents[bone];
            if (!BodyBone().IsMatch(name) || !target.TryGet(name, out var targetHead))
            {
                var inherited = parent >= 0 ? Compute(parent) : new BoneMap(Quaternion.Identity, Vector3.Zero, Vector3.Zero);
                maps[bone] = inherited;
                return inherited;
            }
            var head = donor.BonePositions[bone];
            if (FootBone().IsMatch(name))
            {
                // A shoe is made for its own heel height: the foot keeps its angle to the floor and its height above it,
                // and only moves across to where the new body's foot is; toes go with the foot.
                var foot = parent >= 0 && FootBone().IsMatch(donor.BoneNames[parent])
                    ? Compute(parent)
                    : new BoneMap(Quaternion.Identity, head, new Vector3(targetHead.X, head.Y, targetHead.Z));
                maps[bone] = foot;
                return foot;
            }
            Quaternion? turn = null;
            if (TurnsOnItsOwn().IsMatch(name))
            {
                var aim = children[bone]
                    .Where(child => (TurnsOnItsOwn().IsMatch(donor.BoneNames[child]) || LimbEnd().IsMatch(donor.BoneNames[child])) &&
                                    target.TryGet(donor.BoneNames[child], out _) &&
                                    Vector3.Distance(donor.BonePositions[child], head) > 0.02f)
                    .OrderByDescending(child => Vector3.Distance(donor.BonePositions[child], head))
                    .Cast<int?>().FirstOrDefault();
                if (aim is { } child && target.TryGet(donor.BoneNames[child], out var targetChild))
                    turn = Between(donor.BonePositions[child] - head, targetChild - targetHead);
            }
            turn ??= parent >= 0 ? Compute(parent).Turn : Quaternion.Identity;
            var map = new BoneMap(turn.Value, head, targetHead);
            maps[bone] = map;
            return map;
        }

        var result = new BoneMap[maps.Length];
        for (var i = 0; i < maps.Length; i++) result[i] = Compute(i);
        return result;
    }

    internal static Quaternion Between(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        var dot = Vector3.Dot(from, to);
        if (dot > 0.99999f) return Quaternion.Identity;
        if (dot < -0.99999f)
        {
            var axis = Vector3.Cross(from, Vector3.UnitX);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(from, Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var cross = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(cross, 1 + dot));
    }

    /// <summary>Where each vertex goes when the skeleton moves to the target's: blended over its skin weights.</summary>
    public static Vector3[] Retarget(OutfitSwapMesh donor, BoneMap[] maps)
    {
        var result = new Vector3[donor.VertexCount];
        for (var v = 0; v < result.Length; v++)
        {
            var sum = Vector3.Zero;
            var total = 0f;
            for (var k = 0; k < 4; k++)
            {
                var weight = donor.Weights[v * 4 + k];
                if (weight <= 0) continue;
                sum += maps[donor.Joints[v * 4 + k]].Apply(donor.Positions[v]) * weight;
                total += weight;
            }
            result[v] = total > 0 ? sum / total : donor.Positions[v];
        }
        return result;
    }

    /// <summary>
    /// The donor's normals and tangents turned the way the skeleton moved each vertex (blended over its skin weights),
    /// so shading follows a limb that now points another way.
    /// </summary>
    public static (Vector3[] Normals, Vector4[] Tangents) RetargetFrames(OutfitSwapMesh donor, BoneMap[] maps)
    {
        var normals = new Vector3[donor.VertexCount];
        var tangents = new Vector4[donor.VertexCount];
        for (var v = 0; v < normals.Length; v++)
        {
            var sum = new Quaternion(0, 0, 0, 0);
            Quaternion? first = null;
            for (var k = 0; k < 4; k++)
            {
                var weight = donor.Weights[v * 4 + k];
                if (weight <= 0) continue;
                var turn = maps[donor.Joints[v * 4 + k]].Turn;
                first ??= turn;
                if (Quaternion.Dot(turn, first.Value) < 0) turn = Quaternion.Negate(turn);
                sum += turn * weight;
            }
            var rotation = sum.LengthSquared() > 1e-12f ? Quaternion.Normalize(sum) : Quaternion.Identity;
            normals[v] = Vector3.Normalize(Vector3.Transform(donor.Normals[v], rotation) + new Vector3(0, 0, 1e-9f));
            var tangent = Vector3.Transform(new Vector3(donor.Tangents[v].X, donor.Tangents[v].Y, donor.Tangents[v].Z), rotation);
            tangents[v] = new Vector4(tangent, donor.Tangents[v].W);
        }
        return (normals, tangents);
    }

    // What a point hangs from, in broad groups, so a stocking on the thigh is matched with stockings and a skirt over the
    // same spot with skirts.
    private static readonly Regex[] Groups =
    [
        new(@"pelvis$|^bip001$|spine$"), new(@"spine1|spine2"), new(@"neck|head"),
        new(@"l-clavicle|l-upperarm|upperarmtwist_l"), new(@"r-clavicle|r-upperarm|upperarmtwist_r"),
        new(@"l-forearm|forearmtwist_l"), new(@"r-forearm|forearmtwist_r"), new(@"l-hand|l-finger"), new(@"r-hand|r-finger"),
        new(@"l-thigh|thightwist_l|hip_l"), new(@"r-thigh|thightwist_r|hip_r"),
        new(@"l-calf|l-foot|l-toe|calftwist_l"), new(@"r-calf|r-foot|r-toe|calftwist_r"),
    ];

    public const int FeatureCount = 14;
    public const int LeftHand = 7;
    public const int RightHand = 8;

    public static int[] BoneGroups(IReadOnlyList<string> names) =>
        names.Select(name => Array.FindIndex(Groups, group => group.IsMatch(name)) is var g and >= 0 ? g : Groups.Length).ToArray();

    [GeneratedRegex(@"^bip001-[lr]-(foot|toe\d*)$")]
    private static partial Regex FootBone();

    /// <summary>For each vertex, how much of its weight hangs from the feet and toes.</summary>
    public static float[] FootShares(OutfitSwapMesh mesh)
    {
        var foot = mesh.BoneNames.Select(name => FootBone().IsMatch(name)).ToArray();
        var shares = new float[mesh.VertexCount];
        for (var v = 0; v < shares.Length; v++)
            for (var k = 0; k < 4; k++)
                if (foot[mesh.Joints[v * 4 + k]]) shares[v] += mesh.Weights[v * 4 + k];
        return shares;
    }

    /// <summary>For each vertex, how much of its weight hangs from each group.</summary>
    public static float[] Features(OutfitSwapMesh mesh, IReadOnlyList<int> vertices)
    {
        var groups = BoneGroups(mesh.BoneNames);
        var features = new float[vertices.Count * FeatureCount];
        for (var i = 0; i < vertices.Count; i++)
            for (var k = 0; k < 4; k++)
                features[i * FeatureCount + groups[mesh.Joints[vertices[i] * 4 + k]]] += mesh.Weights[vertices[i] * 4 + k];
        return features;
    }

    /// <summary>
    /// Moves the corners of triangles that face against their vertices' normals towards their neighbours, a few times,
    /// until they face the right way (the game draws such faces dark or not at all).
    /// </summary>
    /// <param name="original">The positions before the fit: triangles already facing that way there are the model's own
    /// (thin details such as toenails) and are left alone.</param>
    public static Vector3[] Untangle(Vector3[] positions, Vector3[] normals, IReadOnlyList<int> triangles, Vector3[]? original = null, int rounds = 8)
    {
        bool Flipped(Vector3[] at, int i)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            var face = Vector3.Cross(at[b] - at[a], at[c] - at[a]);
            return face.LengthSquared() > 1e-16f && Vector3.Dot(face, normals[a] + normals[b] + normals[c]) < 0;
        }
        var own = new HashSet<int>();
        if (original is not null)
            for (var i = 0; i + 2 < triangles.Count; i += 3)
                if (Flipped(original, i)) own.Add(i);
        var result = (Vector3[])positions.Clone();
        var neighbours = new List<int>[positions.Length];
        for (var v = 0; v < neighbours.Length; v++) neighbours[v] = [];
        for (var i = 0; i + 2 < triangles.Count; i += 3)
            for (var e = 0; e < 3; e++)
            {
                neighbours[triangles[i + e]].Add(triangles[i + (e + 1) % 3]);
                neighbours[triangles[i + e]].Add(triangles[i + (e + 2) % 3]);
            }
        for (var round = 0; round < rounds; round++)
        {
            var flipped = new HashSet<int>();
            for (var i = 0; i + 2 < triangles.Count; i += 3)
                if (!own.Contains(i) && Flipped(result, i))
                {
                    flipped.Add(triangles[i]); flipped.Add(triangles[i + 1]); flipped.Add(triangles[i + 2]);
                }
            if (flipped.Count == 0) break;
            var next = (Vector3[])result.Clone();
            foreach (var v in flipped)
            {
                if (neighbours[v].Count == 0) continue;
                var sum = Vector3.Zero;
                foreach (var n in neighbours[v]) sum += result[n];
                next[v] = Vector3.Lerp(result[v], sum / neighbours[v].Count, 0.5f);
            }
            result = next;
        }
        return result;
    }

    /// <summary>Vertices at the same place share one number, so a seam in the UV layout never tears when smoothed.</summary>
    public static int[] Weld(Vector3[] positions, out int count)
    {
        var ids = new int[positions.Length];
        var seen = new Dictionary<(long, long, long), int>();
        for (var i = 0; i < positions.Length; i++)
        {
            var key = ((long)MathF.Round(positions[i].X * 1e5f), (long)MathF.Round(positions[i].Y * 1e5f), (long)MathF.Round(positions[i].Z * 1e5f));
            if (!seen.TryGetValue(key, out var id)) seen[key] = id = seen.Count;
            ids[i] = id;
        }
        count = seen.Count;
        return ids;
    }

    /// <summary>Evens out a per-point movement over the mesh's edges, so neighbouring points move together.</summary>
    public static Vector3[] SmoothOnMesh(Vector3[] movement, IReadOnlyList<int> triangles, int rounds = 6)
    {
        var count = movement.Length;
        var degree = new int[count];
        for (var t = 0; t < triangles.Count; t++) degree[triangles[t]] += 2;
        var current = (Vector3[])movement.Clone();
        var sum = new Vector3[count];
        for (var round = 0; round < rounds; round++)
        {
            Array.Clear(sum);
            for (var t = 0; t + 2 < triangles.Count; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                sum[a] += current[b] + current[c];
                sum[b] += current[a] + current[c];
                sum[c] += current[a] + current[b];
            }
            for (var i = 0; i < count; i++)
                if (degree[i] > 0) current[i] = 0.5f * current[i] + 0.5f * sum[i] / degree[i];
        }
        return current;
    }
}

/// <summary>A target skeleton's rest joint positions by bone name (all target pieces of one body together).</summary>
public sealed class OutfitSwapSkeleton
{
    private readonly Dictionary<string, Vector3> _joints = new(StringComparer.Ordinal);

    public OutfitSwapSkeleton(IEnumerable<OutfitSwapMesh> pieces)
    {
        foreach (var piece in pieces)
            for (var i = 0; i < piece.BoneNames.Length; i++)
                _joints.TryAdd(piece.BoneNames[i], piece.BonePositions[i]);
    }

    public bool TryGet(string name, out Vector3 position) => _joints.TryGetValue(name, out position);
}

/// <summary>
/// The body-shape difference between two bodies, learned from the game's own shared costumes: for points of those
/// costumes after the skeleton has moved them, how much further the game's version for the other body sits.
/// </summary>
public sealed class OutfitSwapShapeField
{
    private const float FeatureScale = 0.06f;
    private const int Neighbours = 24;
    private const float Sigma = 0.02f;
    private readonly OutfitSwapKdTree _tree;
    private readonly Vector3[] _corrections;

    public int SampleCount => _corrections.Length;
    public int CostumeCount { get; }

    // Feature groups of the left side and their right-side twins (see OutfitSwapFitting.Groups).
    private static readonly int[] MirrorGroup = [0, 1, 2, 4, 3, 6, 5, 8, 7, 10, 9, 12, 11, 13];

    /// <summary>
    /// Every sample also counts mirrored to the other side (bodies are symmetric, Z is left to right), so a costume
    /// that one side of the training pair fits oddly cannot bend only one arm or leg.
    /// </summary>
    public OutfitSwapShapeField(Vector3[] moved, float[] features, Vector3[] corrections, int costumes)
    {
        var count = moved.Length;
        var mirroredFeatures = new float[features.Length * 2];
        features.CopyTo(mirroredFeatures, 0);
        for (var i = 0; i < count; i++)
            for (var f = 0; f < OutfitSwapFitting.FeatureCount; f++)
                mirroredFeatures[(count + i) * OutfitSwapFitting.FeatureCount + MirrorGroup[f]] = features[i * OutfitSwapFitting.FeatureCount + f];
        static Vector3 Mirror(Vector3 value) => new(value.X, value.Y, -value.Z);
        moved = [.. moved, .. moved.Select(Mirror)];
        corrections = [.. corrections, .. corrections.Select(Mirror)];
        features = mirroredFeatures;
        const int dimensions = 3 + OutfitSwapFitting.FeatureCount;
        var points = new float[moved.Length * dimensions];
        for (var i = 0; i < moved.Length; i++)
        {
            points[i * dimensions] = moved[i].X;
            points[i * dimensions + 1] = moved[i].Y;
            points[i * dimensions + 2] = moved[i].Z;
            for (var f = 0; f < OutfitSwapFitting.FeatureCount; f++)
                points[i * dimensions + 3 + f] = features[i * OutfitSwapFitting.FeatureCount + f] * FeatureScale;
        }
        _tree = new OutfitSwapKdTree(points, dimensions);
        _corrections = corrections;
        CostumeCount = costumes;
    }

    /// <summary>
    /// The correction at each point: full where training cloth was within <paramref name="near"/>, fading to nothing by
    /// <paramref name="far"/>, and never more than <paramref name="limit"/> (after the skeleton, body shapes differ by a few
    /// centimetres at most).
    /// </summary>
    public Vector3[] Correct(Vector3[] points, float[] features, float near = 0.04f, float far = 0.10f, float limit = 0.04f)
    {
        var result = new Vector3[points.Length];
        Parallel.For(0, points.Length, () => (Query: new float[3 + OutfitSwapFitting.FeatureCount], Index: new int[Neighbours], Distance: new float[Neighbours]),
            (i, _, buffers) =>
            {
                var query = buffers.Query;
                query[0] = points[i].X; query[1] = points[i].Y; query[2] = points[i].Z;
                for (var f = 0; f < OutfitSwapFitting.FeatureCount; f++) query[3 + f] = features[i * OutfitSwapFitting.FeatureCount + f] * FeatureScale;
                var found = _tree.Nearest(query, Neighbours, buffers.Index, buffers.Distance);
                if (found == 0) return buffers;
                var sum = Vector3.Zero;
                var total = 0f;
                for (var n = 0; n < found; n++)
                {
                    var weight = MathF.Exp(-MathF.Pow(buffers.Distance[n] / Sigma, 2)) + 1e-12f;
                    sum += _corrections[buffers.Index[n]] * weight;
                    total += weight;
                }
                var correction = sum / total * Math.Clamp((far - buffers.Distance[0]) / (far - near), 0, 1);
                var length = correction.Length();
                result[i] = length > limit ? correction * (limit / length) : correction;
                return buffers;
            }, _ => { });
        return result;
    }
}
