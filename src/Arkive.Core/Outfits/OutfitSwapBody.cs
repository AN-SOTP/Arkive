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
    private readonly Vector3[] _corners;
    private readonly Vector2[] _cornerUvs;
    private readonly Vector3[] _cornerNormals;
    private readonly bool[] _lower;
    private readonly int[] _charts;
    private readonly Dictionary<int, int> _chartSize;
    private readonly int _largeChart;
    private readonly OutfitSwapKdTree _triangles;

    /// <param name="native">False for a body borrowed from another class and moved onto this skeleton: its shape is
    /// close, but its skin picture layout is not this body's.</param>
    public OutfitSwapBody(IEnumerable<OutfitSwapMesh> pieces, bool native = true)
    {
        Native = native;
        var corners = new List<Vector3>();
        var cornerUvs = new List<Vector2>();
        var cornerNormals = new List<Vector3>();
        var lower = new List<bool>();
        var charts = new List<int>();
        var random = new Random(1);
        var points = new List<Vector3>();
        var normals = new List<Vector3>();
        var vertexPoints = new List<Vector3>();
        var vertexWeights = new List<Dictionary<string, float>>();
        foreach (var piece in pieces)
        {
            var used = new HashSet<int>();
            // Triangles sharing a vertex share its picture coordinates: connected triangles are one piece (chart) of the layout.
            var parent = Enumerable.Range(0, piece.VertexCount).ToArray();
            int Find(int x)
            {
                while (parent[x] != x) x = parent[x] = parent[parent[x]];
                return x;
            }
            foreach (var section in piece.Sections)
                for (var i = 0; i + 2 < section.Triangles.Length; i += 3)
                {
                    parent[Find(section.Triangles[i])] = Find(section.Triangles[i + 1]);
                    parent[Find(section.Triangles[i + 1])] = Find(section.Triangles[i + 2]);
                }
            var chartBase = charts.Count == 0 ? 0 : charts.Max() + 1;
            foreach (var section in piece.Sections)
            {
                var t = section.Triangles;
                for (var i = 0; i + 2 < t.Length; i += 3)
                {
                    int a = t[i], b = t[i + 1], c = t[i + 2];
                    used.Add(a); used.Add(b); used.Add(c);
                    corners.AddRange([piece.Positions[a], piece.Positions[b], piece.Positions[c]]);
                    cornerUvs.AddRange([piece.Uv0[a], piece.Uv0[b], piece.Uv0[c]]);
                    cornerNormals.AddRange([piece.Normals[a], piece.Normals[b], piece.Normals[c]]);
                    lower.Add(OutfitSwapBodies.IsLowerSkin(piece.Materials[section.Material]));
                    charts.Add(chartBase + Find(a));
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
        _corners = corners.ToArray();
        _cornerUvs = cornerUvs.ToArray();
        _cornerNormals = cornerNormals.ToArray();
        _lower = lower.ToArray();
        _charts = charts.ToArray();
        _chartSize = new Dictionary<int, int>();
        foreach (var chart in _charts) _chartSize[chart] = _chartSize.GetValueOrDefault(chart) + 1;
        _largeChart = _chartSize.Count == 0 ? 0 : _chartSize.Values.Max() / 4;
        // How many picture units one metre of skin covers, typically: a triangle stretched far beyond it straddles a seam.
        var ratios = Enumerable.Range(0, _lower.Length).Select(t =>
        {
            float uv = 0, size = 0;
            for (var e = 0; e < 3; e++)
            {
                uv = MathF.Max(uv, Vector2.Distance(_cornerUvs[t * 3 + e], _cornerUvs[t * 3 + (e + 1) % 3]));
                size = MathF.Max(size, Vector3.Distance(_corners[t * 3 + e], _corners[t * 3 + (e + 1) % 3]));
            }
            return size > 1e-5f ? uv / size : 0;
        }).Where(ratio => ratio > 0).OrderBy(ratio => ratio).ToArray();
        PictureScale = ratios.Length > 0 ? ratios[ratios.Length / 2] : 1;
        _triangles = new OutfitSwapKdTree(Flatten(Enumerable.Range(0, _lower.Length)
            .Select(i => (_corners[i * 3] + _corners[i * 3 + 1] + _corners[i * 3 + 2]) / 3).ToArray()), 3);
    }

    /// <summary>
    /// The nearest point like <see cref="Closest"/>, but on the largest piece of the layout (a whole leg or the torso)
    /// when that is nearly as close: small pieces (feet, hands) are drawn only where skin really lies on them.
    /// </summary>
    public SurfacePoint ClosestPreferringLarge(Vector3 point, bool? lower, float slack = 0.006f)
    {
        var best = Closest(point, lower);
        if (best.Triangle < 0 || _chartSize[best.Chart] >= _largeChart) return best;
        var large = Closest(point, lower, LargestChart(best.Lower));
        return large.Triangle >= 0 && Vector3.Distance(large.Position, point) <= Vector3.Distance(best.Position, point) + slack ? large : best;
    }

    private int LargestChart(bool lower) =>
        Enumerable.Range(0, _lower.Length).Where(t => _lower[t] == lower).GroupBy(t => _charts[t]).MaxBy(group => group.Count())?.Key ?? -1;

    /// <summary>Picture units per metre of skin on a typical triangle.</summary>
    public float PictureScale { get; }

    /// <summary>Whether the body is the class's own (its skin picture layout is the one the class's outfits use).</summary>
    public bool Native { get; }

    /// <summary>A point on the body's surface: which triangle, where on it, whether it is lower-body skin, and which
    /// connected piece of the skin picture layout it lies in.</summary>
    public readonly record struct SurfacePoint(int Triangle, Vector3 Position, bool Lower, int Chart);

    /// <summary>
    /// The nearest point on the body's surface, optionally only on upper or lower skin, on one piece of the layout, or
    /// where the skin picture is near <paramref name="nearUv"/> (one side of a seam).
    /// </summary>
    public SurfacePoint Closest(Vector3 point, bool? lower = null, int? chart = null, Vector2? nearUv = null, float uvReach = 0.06f)
    {
        var count = chart is not null || nearUv is not null ? 128 : lower is null ? 16 : 64;
        Span<int> index = stackalloc int[count];
        Span<float> distance = stackalloc float[count];
        var found = _triangles.Nearest([point.X, point.Y, point.Z], count, index, distance);
        var best = new SurfacePoint(-1, point, false, -1);
        var bestDistance = float.MaxValue;
        for (var n = 0; n < found; n++)
        {
            var triangle = index[n];
            if (lower is { } wanted && _lower[triangle] != wanted) continue;
            if (chart is { } piece && _charts[triangle] != piece) continue;
            if (nearUv is { } uv && Vector2.Distance((_cornerUvs[triangle * 3] + _cornerUvs[triangle * 3 + 1] + _cornerUvs[triangle * 3 + 2]) / 3, uv) > uvReach)
                continue;
            var at = ClosestOnTriangle(point, _corners[triangle * 3], _corners[triangle * 3 + 1], _corners[triangle * 3 + 2]);
            var d = Vector3.DistanceSquared(point, at);
            if (d < bestDistance) { bestDistance = d; best = new SurfacePoint(triangle, at, _lower[triangle], _charts[triangle]); }
        }
        return best;
    }

    /// <summary>The body's skin picture coordinates at a point, read off one triangle's plane (beyond its edges too).</summary>
    public Vector2 UvAt(int triangle, Vector3 point)
    {
        var (u, v, w) = Barycentric(triangle, point);
        return _cornerUvs[triangle * 3] * u + _cornerUvs[triangle * 3 + 1] * v + _cornerUvs[triangle * 3 + 2] * w;
    }

    /// <summary>The body's smooth surface direction at a point on one of its triangles.</summary>
    public Vector3 NormalAt(int triangle, Vector3 point)
    {
        var (u, v, w) = Barycentric(triangle, point);
        return Unit(_cornerNormals[triangle * 3] * u + _cornerNormals[triangle * 3 + 1] * v + _cornerNormals[triangle * 3 + 2] * w);
    }

    private (float U, float V, float W) Barycentric(int triangle, Vector3 point)
    {
        Vector3 a = _corners[triangle * 3], b = _corners[triangle * 3 + 1], c = _corners[triangle * 3 + 2];
        Vector3 ab = b - a, ac = c - a, ap = point - a;
        float d00 = Vector3.Dot(ab, ab), d01 = Vector3.Dot(ab, ac), d11 = Vector3.Dot(ac, ac);
        float d20 = Vector3.Dot(ap, ab), d21 = Vector3.Dot(ap, ac);
        var denominator = d00 * d11 - d01 * d01;
        if (MathF.Abs(denominator) < 1e-14f) return (1, 0, 0);
        var v = (d11 * d20 - d01 * d21) / denominator;
        var w = (d00 * d21 - d01 * d20) / denominator;
        return (1 - v - w, v, w);
    }

    /// <summary>
    /// Where the body's skin ends under the head: its open edges above the neck joint and close around the head joint.
    /// The class's face mesh starts exactly there.
    /// </summary>
    public IReadOnlyList<(Vector3 A, Vector3 B)> HeadSeam(Vector3 neck, Vector3 head, float reach = 0.1f)
    {
        static (long, long, long) Key(Vector3 p) => ((long)MathF.Round(p.X * 5000), (long)MathF.Round(p.Y * 5000), (long)MathF.Round(p.Z * 5000));
        var edges = new Dictionary<((long, long, long), (long, long, long)), (Vector3 A, Vector3 B, int Count)>();
        for (var t = 0; t < _lower.Length; t++)
            for (var e = 0; e < 3; e++)
            {
                Vector3 a = _corners[t * 3 + e], b = _corners[t * 3 + (e + 1) % 3];
                var (ka, kb) = (Key(a), Key(b));
                var key = ka.CompareTo(kb) < 0 ? (ka, kb) : (kb, ka);
                edges[key] = edges.TryGetValue(key, out var found) ? found with { Count = found.Count + 1 } : (a, b, 1);
            }
        return edges.Values.Where(edge => edge.Count == 1).Select(edge => (edge.A, edge.B)).Where(edge =>
        {
            var middle = (edge.A + edge.B) / 2;
            return middle.Y > neck.Y && MathF.Sqrt((middle.X - head.X) * (middle.X - head.X) + (middle.Z - head.Z) * (middle.Z - head.Z)) < reach;
        }).ToArray();
    }

    private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
        var denominator = 1 / (va + vb + vc);
        return a + ab * (vb * denominator) + ac * (vc * denominator);
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
        bool[] cloth, float margin = 0.0008f, float radius = 0.015f, int rounds = 8, float keep = 0.004f, float most = 0.02f,
        float reach = 0.08f)
    {
        var (donorGap, donorNear, _) = donorBody.Measure(donorPositions);
        var judged = new bool[fitted.Length];
        var want = new float[fitted.Length];
        // A borrowed body only roughly matches the donor's, so tight cloth may seem to sink into it a little.
        var inside = donorBody.Native ? -0.001f : -0.01f;
        for (var i = 0; i < fitted.Length; i++)
        {
            judged[i] = cloth[i] && donorNear[i] < reach && donorGap[i] > inside;
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
                    // Bare skin stays on the body; only cloth moves.
                    if (!cloth[j]) continue;
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

    /// <summary>
    /// The outfit's own bare skin (sleeves and stockings often cover skin the outfit draws itself) as a surface, with its
    /// vertices at <paramref name="positions"/>; null when the outfit draws no skin.
    /// </summary>
    public static OutfitSwapBody? SkinSurface(OutfitSwapMesh mesh, Vector3[] positions) =>
        Surface(mesh, positions, mesh.Sections.Where(section => OutfitSwapBodies.IsSkin(mesh.Materials[section.Material])).ToArray());

    /// <summary>The outfit's shoes (cloth hanging from the feet) as a surface, with vertices at <paramref name="positions"/>.</summary>
    public static OutfitSwapBody? ShoeSurface(OutfitSwapMesh mesh, Vector3[] positions, float[] feet) =>
        Surface(mesh, positions, mesh.Sections.Where(section => !OutfitSwapBodies.IsSkin(mesh.Materials[section.Material]))
            .Select(section =>
            {
                var t = section.Triangles;
                var kept = new List<int>();
                for (var i = 0; i + 2 < t.Length; i += 3)
                    if (feet[t[i]] >= 0.1f && feet[t[i + 1]] >= 0.1f && feet[t[i + 2]] >= 0.1f) kept.AddRange([t[i], t[i + 1], t[i + 2]]);
                return new OutfitSwapSection(section.Material, kept.ToArray());
            }).Where(section => section.Triangles.Length > 0).ToArray());

    /// <summary>
    /// Skin that was inside the outfit's shoes stays inside them, as deep as it was: where the leg above takes the new
    /// body's shape, the skin at the shoe's rim would otherwise poke through it.
    /// </summary>
    public static Vector3[] KeepInShoes(Vector3[] donorPositions, Vector3[] fitted, bool[] cloth, OutfitSwapBody donorShoes, OutfitSwapBody shoes)
    {
        var (before, nearBefore, _) = donorShoes.Measure(donorPositions);
        var (now, _, outward) = shoes.Measure(fitted);
        var result = (Vector3[])fitted.Clone();
        for (var v = 0; v < fitted.Length; v++)
            if (!cloth[v] && nearBefore[v] < 0.015f && before[v] < 0.004f)
            {
                // At least 2 mm in, so the rim's own thickness never lets it show.
                var depth = Math.Min(before[v], -0.002f);
                if (now[v] > depth) result[v] -= outward[v] * (now[v] - depth);
            }
        return result;
    }

    private static OutfitSwapBody? Surface(OutfitSwapMesh mesh, Vector3[] positions, OutfitSwapSection[] sections)
    {
        if (sections.Length == 0) return null;
        var normals = new Vector3[positions.Length];
        foreach (var section in sections)
            for (var i = 0; i + 2 < section.Triangles.Length; i += 3)
            {
                int a = section.Triangles[i], b = section.Triangles[i + 1], c = section.Triangles[i + 2];
                var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                normals[a] += normal; normals[b] += normal; normals[c] += normal;
            }
        // Faces point the way the outfit's own normals do (outward), whatever their winding.
        for (var v = 0; v < normals.Length; v++)
            if (Vector3.Dot(normals[v], mesh.Normals[v]) < 0) normals[v] = -normals[v];
        return new OutfitSwapBody([new OutfitSwapMesh
        {
            Positions = positions, Normals = normals, Tangents = mesh.Tangents, Uv0 = mesh.Uv0, Uv1 = mesh.Uv1, Colours = mesh.Colours,
            Joints = mesh.Joints, Weights = mesh.Weights, Sections = sections, Materials = mesh.Materials,
            BoneNames = mesh.BoneNames, BonePositions = mesh.BonePositions, BoneParents = mesh.BoneParents,
        }]);
    }

    /// <summary>
    /// Lays the outfit's bare skin onto the new body's own skin, so arms, legs, feet and neck take its exact shape (and
    /// meet its head), and carries the move to the cloth around it. Skin far from the body (a hand the base body lacks)
    /// stays where the fit put it, and so does skin as far as <paramref name="hold"/> says (0 moves fully, 1 stays).
    /// </summary>
    public static Vector3[] SnapSkin(Vector3[] fitted, bool[] cloth, float[] hold, OutfitSwapBody body, float near = 0.03f,
        float far = 0.06f, float radius = 0.03f)
    {
        var result = (Vector3[])fitted.Clone();
        var moved = new List<int>();
        var movement = new Vector3[fitted.Length];
        for (var i = 0; i < fitted.Length; i++)
        {
            if (cloth[i]) continue;
            var surface = body.Closest(fitted[i]);
            if (surface.Triangle < 0) continue;
            var share = Math.Clamp((far - Vector3.Distance(surface.Position, fitted[i])) / (far - near), 0, 1) * (1 - hold[i]);
            if (share <= 0) continue;
            movement[i] = (surface.Position - fitted[i]) * share;
            result[i] += movement[i];
            moved.Add(i);
        }
        if (moved.Count == 0) return result;
        var tree = new OutfitSwapKdTree(moved.SelectMany(i => new[] { fitted[i].X, fitted[i].Y, fitted[i].Z }).ToArray(), 3);
        var index = new int[16];
        var distance = new float[16];
        for (var i = 0; i < fitted.Length; i++)
        {
            if (!cloth[i]) continue;
            var found = tree.Nearest([fitted[i].X, fitted[i].Y, fitted[i].Z], 16, index, distance);
            var sum = Vector3.Zero;
            var total = 0f;
            for (var n = 0; n < found; n++)
            {
                if (distance[n] > radius) break;
                var w = MathF.Exp(-MathF.Pow(distance[n] / (radius / 2), 2));
                sum += movement[moved[index[n]]] * w;
                total += w;
            }
            if (total > 1e-6f) result[i] += sum / Math.Max(total, 1) * (1 - hold[i]);
        }
        return result;
    }

    /// <summary>
    /// Brings the open top edge of the outfit's bare neck onto the new body's head seam, so the neck meets the head
    /// without a gap whatever the two bodies' neck lengths, and blends the move down the neck and into nearby cloth.
    /// </summary>
    public static Vector3[] MeetHead(Vector3[] fitted, bool[] cloth, IReadOnlyList<int> triangles, IReadOnlyList<(Vector3 A, Vector3 B)> seam,
        Vector3 neck, Vector3 head, float reach = 0.1f, float radius = 0.02f, float overlap = 0.005f)
    {
        if (seam.Count == 0) return fitted;
        var count = new Dictionary<(int, int), int>();
        for (var i = 0; i + 2 < triangles.Count; i += 3)
            for (var e = 0; e < 3; e++)
            {
                int a = triangles[i + e], b = triangles[i + (e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        var ring = count.Where(pair => pair.Value == 1).SelectMany(pair => new[] { pair.Key.Item1, pair.Key.Item2 }).Distinct()
            .Where(v => !cloth[v] && fitted[v].Y > neck.Y - 0.02f &&
                        MathF.Sqrt((fitted[v].X - head.X) * (fitted[v].X - head.X) + (fitted[v].Z - head.Z) * (fitted[v].Z - head.Z)) < reach)
            .ToArray();
        // The seam as dense points ordered by their angle around the neck, so each ring vertex goes to the seam point at
        // its own angle: the whole ring then meets the whole seam, even where the two necks differ in height.
        static float Angle(Vector3 point, Vector3 axis) => MathF.Atan2(point.Z - axis.Z, point.X - axis.X);
        var samples = new List<(float Angle, Vector3 Point)>();
        foreach (var (a, b) in seam)
            for (var k = 0; k <= 4; k++)
            {
                var point = a + (b - a) * (k / 4f);
                samples.Add((Angle(point, head), point));
            }
        samples.Sort((x, y) => x.Angle.CompareTo(y.Angle));
        var angles = samples.Select(sample => sample.Angle).ToArray();
        var moves = new List<(int Vertex, Vector3 Move)>();
        foreach (var v in ring)
        {
            var angle = Angle(fitted[v], head);
            var at = Array.BinarySearch(angles, angle);
            if (at < 0) at = ~at;
            // The nearer of the two neighbours in angle (wrapping around the circle).
            var before = samples[(at - 1 + samples.Count) % samples.Count];
            var after = samples[at % samples.Count];
            static float Gap(float a, float b) { var d = MathF.Abs(a - b); return MathF.Min(d, 2 * MathF.PI - d); }
            // Between the two seam points at either side of its angle, so neighbouring ring vertices never meet.
            var span = Gap(before.Angle, angle) + Gap(after.Angle, angle);
            var best = span < 1e-6f ? before.Point : Vector3.Lerp(before.Point, after.Point, Gap(before.Angle, angle) / span);
            // A little past the seam and in, tucked under the face, so no sliver shows between them.
            var inward = new Vector3(head.X - best.X, 0, head.Z - best.Z);
            best += new Vector3(0, overlap, 0) + (inward.LengthSquared() > 1e-8f ? Vector3.Normalize(inward) * (overlap / 4) : Vector3.Zero);
            if (Vector3.Distance(best, fitted[v]) < 0.05f) moves.Add((v, best - fitted[v]));
        }
        if (moves.Count == 0) return fitted;
        var tree = new OutfitSwapKdTree(moves.SelectMany(item => new[] { fitted[item.Vertex].X, fitted[item.Vertex].Y, fitted[item.Vertex].Z }).ToArray(), 3);
        var result = (Vector3[])fitted.Clone();
        var index = new int[8];
        var distance = new float[8];
        var onRing = moves.ToDictionary(item => item.Vertex, item => item.Move);
        for (var v = 0; v < fitted.Length; v++)
        {
            if (onRing.TryGetValue(v, out var exact)) { result[v] += exact; continue; }
            var found = tree.Nearest([fitted[v].X, fitted[v].Y, fitted[v].Z], 8, index, distance);
            var sum = Vector3.Zero;
            var total = 0f;
            for (var n = 0; n < found; n++)
            {
                if (distance[n] > radius * 2.5f) break;
                var w = MathF.Exp(-MathF.Pow(distance[n] / radius, 2));
                sum += moves[index[n]].Move * w;
                total += w;
            }
            if (total > 1e-6f) result[v] += sum / Math.Max(total, 1);
        }
        return result;
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
