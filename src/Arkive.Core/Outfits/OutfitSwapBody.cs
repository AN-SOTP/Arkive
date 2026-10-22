using System.Numerics;

namespace Arkive.Core.Outfits;

/// <summary>
/// A bare player body (the game's own base body meshes) as a dense cloud of surface points with their outward
/// directions: how far a point is outside it (negative inside), and which bones the skin there hangs from.
/// </summary>
public sealed class OutfitSwapBody
{
    private const int SamplesPerTriangle = 12;
    private readonly OutfitSwapKdTree _surface;
    private readonly Vector3[] _points;
    private readonly Vector3[] _normals;
    private readonly OutfitSwapKdTree _vertices;
    private readonly Vector3[] _vertexPoints;
    private readonly Dictionary<string, float>[] _vertexWeights;

    public OutfitSwapBody(IEnumerable<OutfitSwapMesh> pieces)
    {
        var random = new Random(1);
        var points = new List<Vector3>();
        var normals = new List<Vector3>();
        var vertexPoints = new List<Vector3>();
        var vertexWeights = new List<Dictionary<string, float>>();
        foreach (var piece in pieces)
        {
            var used = new HashSet<int>();
            foreach (var section in piece.Sections)
            {
                var t = section.Triangles;
                for (var i = 0; i + 2 < t.Length; i += 3)
                {
                    int a = t[i], b = t[i + 1], c = t[i + 2];
                    used.Add(a); used.Add(b); used.Add(c);
                    for (var s = 0; s < SamplesPerTriangle; s++)
                    {
                        var u = (float)random.NextDouble();
                        var v = (float)random.NextDouble();
                        if (u + v > 1) { u = 1 - u; v = 1 - v; }
                        var w = 1 - u - v;
                        points.Add(piece.Positions[a] * w + piece.Positions[b] * u + piece.Positions[c] * v);
                        normals.Add(Unit(piece.Normals[a] * w + piece.Normals[b] * u + piece.Normals[c] * v));
                    }
                }
            }
            foreach (var vertex in used)
            {
                points.Add(piece.Positions[vertex]);
                normals.Add(Unit(piece.Normals[vertex]));
                vertexPoints.Add(piece.Positions[vertex]);
                var weights = new Dictionary<string, float>(StringComparer.Ordinal);
                for (var k = 0; k < 4; k++)
                {
                    var weight = piece.Weights[vertex * 4 + k];
                    if (weight <= 0) continue;
                    var name = piece.BoneNames[piece.Joints[vertex * 4 + k]];
                    weights[name] = weights.GetValueOrDefault(name) + weight;
                }
                vertexWeights.Add(weights);
            }
        }
        if (points.Count == 0) throw new InvalidDataException("The base body has no surface.");
        _points = points.ToArray();
        _normals = normals.ToArray();
        _surface = new OutfitSwapKdTree(Flatten(_points), 3);
        _vertexPoints = vertexPoints.ToArray();
        _vertexWeights = vertexWeights.ToArray();
        _vertices = new OutfitSwapKdTree(Flatten(_vertexPoints), 3);
    }

    private static Vector3 Unit(Vector3 value) => value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.UnitY;

    private static float[] Flatten(Vector3[] points) => points.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();

    /// <summary>How far each point is outside the body (negative inside), how far the nearest surface point is, and that point's outward direction.</summary>
    public (float[] Signed, float[] Nearest, Vector3[] Outward) Measure(Vector3[] points)
    {
        var signed = new float[points.Length];
        var nearest = new float[points.Length];
        var outward = new Vector3[points.Length];
        Parallel.For(0, points.Length, () => (Index: new int[4], Distance: new float[4], Query: new float[3]), (i, _, buffers) =>
        {
            buffers.Query[0] = points[i].X; buffers.Query[1] = points[i].Y; buffers.Query[2] = points[i].Z;
            var found = _surface.Nearest(buffers.Query, 4, buffers.Index, buffers.Distance);
            float sum = 0, total = 0;
            for (var n = 0; n < found; n++)
            {
                var weight = 1 / (buffers.Distance[n] + 1e-4f);
                sum += Vector3.Dot(points[i] - _points[buffers.Index[n]], _normals[buffers.Index[n]]) * weight;
                total += weight;
            }
            signed[i] = sum / total;
            nearest[i] = buffers.Distance[0];
            outward[i] = _normals[buffers.Index[0]];
            return buffers;
        }, _ => { });
        return (signed, nearest, outward);
    }

    /// <summary>The body's skin weights near a point, limited to <paramref name="allowed"/> bones.</summary>
    public Dictionary<string, float> WeightsAt(Vector3 point, Func<string, bool> allowed)
    {
        Span<int> index = stackalloc int[4];
        Span<float> distance = stackalloc float[4];
        var found = _vertices.Nearest([point.X, point.Y, point.Z], 4, index, distance);
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        for (var n = 0; n < found; n++)
            foreach (var (name, weight) in _vertexWeights[index[n]])
                if (allowed(name)) result[name] = result.GetValueOrDefault(name) + weight / (distance[n] + 1e-3f);
        var total = result.Values.Sum();
        foreach (var name in result.Keys.ToArray()) result[name] /= total;
        return result;
    }
}

/// <summary>Keeping a moved outfit off the new body's skin.</summary>
public static class OutfitSwapClearance
{
    /// <summary>
    /// Moves cloth that ended up inside the target body back out, keeping the small gap it had on its own body, and
    /// carries the correction to nearby cloth so layers stay in order.
    /// </summary>
    public static Vector3[] PushOut(Vector3[] donorPositions, Vector3[] fitted, OutfitSwapBody donorBody, OutfitSwapBody targetBody,
        bool[] cloth, float margin = 0.0008f, float radius = 0.015f, int rounds = 8, float keep = 0.004f, float most = 0.02f)
    {
        var (donorGap, donorNear, _) = donorBody.Measure(donorPositions);
        var judged = new bool[fitted.Length];
        var want = new float[fitted.Length];
        for (var i = 0; i < fitted.Length; i++)
        {
            judged[i] = cloth[i] && donorNear[i] < 0.08f && donorGap[i] > -0.001f;
            want[i] = Math.Clamp(donorGap[i], margin, keep);
        }
        var result = (Vector3[])fitted.Clone();
        var tree = new OutfitSwapKdTree(result.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray(), 3);
        var near = new List<int>();
        for (var round = 0; round < rounds; round++)
        {
            var (gap, _, outward) = targetBody.Measure(result);
            var movement = new Vector3[result.Length];
            var weight = new float[result.Length];
            var any = false;
            for (var i = 0; i < result.Length; i++)
            {
                if (!judged[i] || gap[i] >= want[i] - 0.0002f) continue;
                any = true;
                var push = outward[i] * Math.Min(want[i] - gap[i], most);
                tree.Within([fitted[i].X, fitted[i].Y, fitted[i].Z], radius, near);
                foreach (var j in near)
                {
                    var w = MathF.Exp(-MathF.Pow(Vector3.Distance(fitted[j], fitted[i]) / (radius / 2), 2));
                    movement[j] += push * w;
                    weight[j] += w;
                }
            }
            if (!any) break;
            for (var i = 0; i < result.Length; i++)
                if (weight[i] > 0) result[i] += movement[i] / Math.Max(weight[i], 1);
        }
        return result;
    }

    /// <summary>
    /// For bodies of the other sex: wraps cloth that hugged the old body around the new one with the gap it had, so what
    /// followed a female chest or hip follows a male one (and the other way round). Loose cloth further out takes the
    /// movement of the cloth near it, and bare skin of the outfit moves with the cloth around it.
    /// </summary>
    public static Vector3[] Conform(Vector3[] donorPositions, Vector3[] fitted, OutfitSwapBody donorBody, OutfitSwapBody targetBody,
        bool[] cloth, IReadOnlyList<int> triangles, float near = 0.012f, float far = 0.05f, int rounds = 3)
    {
        var (donorGap, donorNear, _) = donorBody.Measure(donorPositions);
        var hug = new float[fitted.Length];
        for (var i = 0; i < hug.Length; i++)
            hug[i] = cloth[i] && donorNear[i] < 0.08f ? Math.Clamp((far - Math.Max(donorGap[i], 0)) / (far - near), 0, 1) : 0;
        var result = (Vector3[])fitted.Clone();
        for (var round = 0; round < rounds; round++)
        {
            var (gap, _, outward) = targetBody.Measure(result);
            var movement = new Vector3[result.Length];
            for (var i = 0; i < result.Length; i++)
                movement[i] = outward[i] * ((Math.Clamp(donorGap[i], 0.0008f, 0.02f) - gap[i]) * hug[i]);
            movement = OutfitSwapFitting.SmoothOnMesh(movement, triangles, 8);
            for (var i = 0; i < result.Length; i++) result[i] += movement[i];
        }
        return CarrySkin(fitted, result, cloth);
    }

    private static Vector3[] CarrySkin(Vector3[] before, Vector3[] after, bool[] cloth, float radius = 0.04f)
    {
        var clothPoints = Enumerable.Range(0, before.Length).Where(i => cloth[i]).ToArray();
        if (clothPoints.Length == 0) return after;
        var tree = new OutfitSwapKdTree(clothPoints.SelectMany(i => new[] { before[i].X, before[i].Y, before[i].Z }).ToArray(), 3);
        var result = (Vector3[])after.Clone();
        Span<int> index = stackalloc int[16];
        Span<float> distance = stackalloc float[16];
        for (var i = 0; i < before.Length; i++)
        {
            if (cloth[i]) continue;
            var found = tree.Nearest([before[i].X, before[i].Y, before[i].Z], 16, index, distance);
            var sum = Vector3.Zero;
            var total = 0f;
            for (var n = 0; n < found; n++)
            {
                if (distance[n] > radius) break;
                var w = MathF.Exp(-MathF.Pow(distance[n] / (radius / 2), 2));
                var j = clothPoints[index[n]];
                sum += (after[j] - before[j]) * w;
                total += w;
            }
            if (total > 1e-6f) result[i] = before[i] + sum / total;
        }
        return result;
    }
}
