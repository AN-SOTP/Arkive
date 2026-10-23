using System.Numerics;

namespace Arkive.Core.Outfits;

/// <summary>
/// The new body's own bare hands in place of the outfit's. Each class models its hands for its own finger bones and poses
/// (a Summoner's long, thin fingers bent by a Slayer's battle stance splay and pinch however they are fitted), so a swap
/// onto another body takes the hands of one of that body's outfits: modelled, weighted and laid out on its skin picture for
/// it. They meet the outfit's own skin at the wrist, where both lie on the new body.
/// </summary>
public static class OutfitSwapHands
{
    /// <summary>How much of a vertex hangs from the hand and finger bones.</summary>
    public static float[] Shares(OutfitSwapMesh mesh)
    {
        var features = OutfitSwapFitting.Features(mesh, Enumerable.Range(0, mesh.VertexCount).ToArray());
        return Enumerable.Range(0, mesh.VertexCount).Select(v =>
            features[v * OutfitSwapFitting.FeatureCount + OutfitSwapFitting.LeftHand] +
            features[v * OutfitSwapFitting.FeatureCount + OutfitSwapFitting.RightHand]).ToArray();
    }

    /// <summary>
    /// Whether the outfit shows bare hands of its own, with nothing but small pieces (nails, rings) on them: under a glove,
    /// even a fingerless one, hands of another shape would show through it.
    /// </summary>
    public static bool Bare(OutfitSwapMesh mesh, float[] hands)
    {
        int[] On(bool skin) => mesh.Sections.Where(section => OutfitSwapBodies.IsSkin(mesh.Materials[section.Material]) == skin)
            .SelectMany(section => section.Triangles).Distinct().Where(v => hands[v] >= 0.5f).ToArray();
        var bare = On(true);
        if (bare.Length < 200) return false;
        // Cloth lying on the hand's skin (a glove), not hanging past it (a cuff's frill) or small on it (nails, rings).
        var small = SmallOnHands(mesh, hands);
        var tree = new OutfitSwapKdTree(bare.SelectMany(v => new[] { mesh.Positions[v].X, mesh.Positions[v].Y, mesh.Positions[v].Z }).ToArray(), 3);
        Span<int> index = stackalloc int[1];
        Span<float> distance = stackalloc float[1];
        var covered = 0;
        foreach (var v in On(false))
            if (!small[v] && tree.Nearest([mesh.Positions[v].X, mesh.Positions[v].Y, mesh.Positions[v].Z], 1, index, distance) > 0 && distance[0] < 0.006f)
                covered++;
        return covered < 100;
    }

    /// <summary>
    /// The outfit with its bare hands (and what lies on them: nails, rings) replaced by <paramref name="reference"/>'s,
    /// with the positions the rest of it was fitted to (the new hands come at the reference's own, which is the new body).
    /// The new hands' skin keeps its picture layout (<see cref="OutfitSwapMesh.NativeSkin"/>).
    /// </summary>
    public static (OutfitSwapMesh Mesh, Vector3[] Fitted) WithBodyHands(OutfitSwapMesh mesh, Vector3[] fitted, float[] hands, OutfitSwapMesh reference)
    {
        var referenceHands = Shares(reference);
        var bones = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < mesh.BoneNames.Length; i++) bones.TryAdd(mesh.BoneNames[i], i);

        // The outfit's hands go: skin wholly on the hand (a little short of the wrist, so the new hands overlap it there),
        // and small pieces lying on the fingers (nails, rings).
        var small = SmallOnHands(mesh, hands);
        var sections = new List<OutfitSwapSection>();
        foreach (var section in mesh.Sections)
        {
            var skin = OutfitSwapBodies.IsSkin(mesh.Materials[section.Material]);
            var kept = new List<int>();
            var t = section.Triangles;
            for (var i = 0; i + 2 < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                var gone = skin ? hands[a] >= 0.4f && hands[b] >= 0.4f && hands[c] >= 0.4f : small[a] && small[b] && small[c];
                if (!gone) kept.AddRange([a, b, c]);
            }
            sections.Add(new OutfitSwapSection(section.Material, kept.ToArray()));
        }

        var materials = mesh.Materials.ToList();
        var positions = new List<Vector3>(fitted);
        var normals = mesh.Normals.ToList();
        var tangents = mesh.Tangents.ToList();
        var uv0 = mesh.Uv0.ToList();
        var uv1 = mesh.Uv1.ToList();
        var colours = mesh.Colours.ToList();
        var joints = mesh.Joints.ToList();
        var weights = mesh.Weights.ToList();
        var native = new HashSet<int>(mesh.NativeSkin);
        var hand = bones.GetValueOrDefault("bip001-l-hand");
        foreach (var section in reference.Sections)
        {
            if (!OutfitSwapBodies.IsSkin(reference.Materials[section.Material])) continue;
            var added = new List<int>();
            var index = new Dictionary<int, int>();
            var t = section.Triangles;
            for (var i = 0; i + 2 < t.Length; i += 3)
            {
                if (referenceHands[t[i]] < 0.5f && referenceHands[t[i + 1]] < 0.5f && referenceHands[t[i + 2]] < 0.5f) continue;
                for (var c = 0; c < 3; c++)
                {
                    var v = t[i + c];
                    if (!index.TryGetValue(v, out var at))
                    {
                        at = index[v] = positions.Count;
                        positions.Add(reference.Positions[v]);
                        normals.Add(reference.Normals[v]);
                        tangents.Add(reference.Tangents[v]);
                        uv0.Add(reference.Uv0[v]);
                        uv1.Add(reference.Uv1.Length > v ? reference.Uv1[v] : Vector2.Zero);
                        for (var k = 0; k < 4; k++) colours.Add(reference.Colours.Length > v * 4 + k ? reference.Colours[v * 4 + k] : (byte)255);
                        for (var k = 0; k < 4; k++)
                        {
                            var weight = reference.Weights[v * 4 + k];
                            var bone = weight > 0 && bones.TryGetValue(reference.BoneNames[reference.Joints[v * 4 + k]], out var found) ? found : hand;
                            joints.Add(bone);
                            weights.Add(weight);
                        }
                    }
                    added.Add(at);
                }
            }
            if (added.Count == 0) continue;
            materials.Add(reference.Materials[section.Material]);
            native.Add(materials.Count - 1);
            sections.Add(new OutfitSwapSection(materials.Count - 1, added.ToArray()));
        }
        if (native.Count == mesh.NativeSkin.Count) return (mesh, fitted);
        var joint = new Attributes(positions, normals, tangents, uv0, uv1, colours, joints, weights);
        Join(sections, materials, native, joint, hands);
        var result = new OutfitSwapMesh
        {
            Positions = positions.ToArray(), Normals = normals.ToArray(), Tangents = tangents.ToArray(), Uv0 = uv0.ToArray(), Uv1 = uv1.ToArray(),
            Colours = colours.ToArray(), Joints = joints.ToArray(), Weights = weights.ToArray(), Sections = sections, Materials = materials,
            BoneNames = mesh.BoneNames, BonePositions = mesh.BonePositions, BoneParents = mesh.BoneParents,
            OwnPictureSkin = mesh.OwnPictureSkin, NativeSkin = native,
        };
        return (result, positions.ToArray());
    }

    /// <summary>
    /// Closes the wrist: the new hands' open edge is moved onto the outfit's skin edge it meets (and the hand just above
    /// it with it, fading out over 2 cm), taking that edge's bone weights, so no gap shows there, standing or moving.
    /// </summary>
    private sealed record Attributes(List<Vector3> Positions, List<Vector3> Normals, List<Vector4> Tangents, List<Vector2> Uv0,
        List<Vector2> Uv1, List<byte> Colours, List<int> Joints, List<float> Weights)
    {
        /// <summary>The vertices at the same place as one (split along a picture seam), itself included.</summary>
        public Func<int, IEnumerable<int>> Copies { get; set; } = v => [v];

        /// <summary>A copy of a vertex, with another picture position.</summary>
        public int Copy(int v, Vector2 uv)
        {
            Positions.Add(Positions[v]); Normals.Add(Normals[v]); Tangents.Add(Tangents[v]); Uv0.Add(uv); Uv1.Add(Uv1[v]);
            for (var k = 0; k < 4; k++)
            {
                Colours.Add(Colours[v * 4 + k]);
                Joints.Add(Joints[v * 4 + k]);
                Weights.Add(Weights[v * 4 + k]);
            }
            return Positions.Count - 1;
        }
    }

    private static void Join(List<OutfitSwapSection> sections, List<string> materials, HashSet<int> native, Attributes mesh, float[] hands,
        float reach = 0.03f, float blend = 0.02f)
    {
        var positions = mesh.Positions;
        var joints = mesh.Joints;
        var weights = mesh.Weights;
        var points = positions.ToArray();
        var ids = OutfitSwapFitting.Weld(points, out _);
        // Points split along a picture seam move as one.
        var copies = Enumerable.Range(0, points.Length).GroupBy(v => ids[v]).ToDictionary(group => group.Key, group => group.ToArray());
        mesh.Copies = v => v < ids.Length ? copies[ids[v]] : [v];
        List<(int A, int B)> Edges(IEnumerable<OutfitSwapSection> parts)
        {
            var count = new Dictionary<(int, int), (int Count, int A, int B)>();
            foreach (var section in parts)
                for (var i = 0; i + 2 < section.Triangles.Length; i += 3)
                    for (var e = 0; e < 3; e++)
                    {
                        int a = section.Triangles[i + e], b = section.Triangles[i + (e + 1) % 3];
                        var key = (Math.Min(ids[a], ids[b]), Math.Max(ids[a], ids[b]));
                        var (n, _, _) = count.GetValueOrDefault(key);
                        count[key] = (n + 1, a, b);
                    }
            return count.Values.Where(edge => edge.Count == 1).Select(edge => (edge.A, edge.B)).ToList();
        }
        // The outfit's skin edge where its hand was cut away, and the new hands' edge at the wrist.
        var outfitEdge = Edges(sections.Where(section => OutfitSwapBodies.IsSkin(materials[section.Material]) && !native.Contains(section.Material)))
            .Where(edge => edge.A < hands.Length && edge.B < hands.Length && hands[edge.A] >= 0.15f && hands[edge.B] >= 0.15f).ToList();
        if (outfitEdge.Count == 0) return;
        var handEdge = Edges(sections.Where(section => native.Contains(section.Material))).SelectMany(edge => new[] { edge.A, edge.B })
            .Where(v => v >= hands.Length).Distinct().ToArray();
        var moves = new Dictionary<int, Vector3>();
        foreach (var v in handEdge)
        {
            var best = (Distance: float.MaxValue, Point: Vector3.Zero, Corner: -1);
            foreach (var (a, b) in outfitEdge)
            {
                var ab = points[b] - points[a];
                var t = ab.LengthSquared() > 1e-12f ? Math.Clamp(Vector3.Dot(points[v] - points[a], ab) / ab.LengthSquared(), 0, 1) : 0;
                var at = points[a] + ab * t;
                var d = Vector3.Distance(points[v], at);
                if (d < best.Distance) best = (d, at, t < 0.5f ? a : b);
            }
            if (best.Distance >= reach) continue;
            foreach (var copy in copies[ids[v]].Where(copy => copy >= hands.Length))
            {
                moves[copy] = best.Point - points[copy];
                mesh.Normals[copy] = mesh.Normals[best.Corner];
                mesh.Tangents[copy] = mesh.Tangents[best.Corner];
                for (var k = 0; k < 4; k++)
                {
                    joints[copy * 4 + k] = joints[best.Corner * 4 + k];
                    weights[copy * 4 + k] = weights[best.Corner * 4 + k];
                }
            }
        }
        if (moves.Count == 0) return;
        var edge = moves.Keys.ToArray();
        var tree = new OutfitSwapKdTree(edge.SelectMany(v => new[] { points[v].X, points[v].Y, points[v].Z }).ToArray(), 3);
        Span<int> index = stackalloc int[1];
        Span<float> distance = stackalloc float[1];
        for (var v = hands.Length; v < points.Length; v++)
        {
            if (moves.TryGetValue(v, out var own)) { positions[v] = points[v] + own; continue; }
            if (tree.Nearest([points[v].X, points[v].Y, points[v].Z], 1, index, distance) == 0 || distance[0] >= blend) continue;
            positions[v] = points[v] + moves[edge[index[0]]] * (1 - distance[0] / blend);
        }
        // Where the two edges are cut into different numbers of points, the outfit's points go onto the hands' edge too,
        // or thin slits stay open between them.
        var handSegments = Edges(sections.Where(section => native.Contains(section.Material)))
            .Where(segment => moves.ContainsKey(segment.A) && moves.ContainsKey(segment.B)).ToList();
        foreach (var v in outfitEdge.SelectMany(segment => new[] { segment.A, segment.B }).Distinct())
        {
            var best = (Distance: float.MaxValue, Point: Vector3.Zero);
            foreach (var (a, b) in handSegments)
            {
                var ab = positions[b] - positions[a];
                var t = ab.LengthSquared() > 1e-12f ? Math.Clamp(Vector3.Dot(positions[v] - positions[a], ab) / ab.LengthSquared(), 0, 1) : 0;
                var at = positions[a] + ab * t;
                if (Vector3.Distance(positions[v], at) < best.Distance) best = (Vector3.Distance(positions[v], at), at);
            }
            if (best.Distance < 0.004f)
                foreach (var copy in copies[ids[v]].Where(copy => copy < hands.Length)) positions[copy] = best.Point;
        }
        Zip(sections, native, mesh, outfitEdge.SelectMany(segment => new[] { segment.A, segment.B }).DistinctBy(v => ids[v]).ToArray(),
            handSegments.SelectMany(segment => new[] { segment.A, segment.B }).DistinctBy(v => ids[v]).ToArray());
        // The hands' own bone weights take over from the arm's gradually, over 3 cm from the wrist: switched at once, the
        // twist bones turn the two rows either side of the seam apart and it creases open as the wrist turns.
        var ringTree = new OutfitSwapKdTree(edge.SelectMany(v => new[] { positions[v].X, positions[v].Y, positions[v].Z }).ToArray(), 3);
        for (var v = hands.Length; v < points.Length; v++)
        {
            if (moves.ContainsKey(v) || ringTree.Nearest([positions[v].X, positions[v].Y, positions[v].Z], 1, index, distance) == 0 ||
                distance[0] >= 0.03f) continue;
            var share = 1 - distance[0] / 0.03f;
            var ring = edge[index[0]];
            var sum = new Dictionary<int, float>();
            for (var k = 0; k < 4; k++)
            {
                sum[joints[v * 4 + k]] = sum.GetValueOrDefault(joints[v * 4 + k]) + weights[v * 4 + k] * (1 - share);
                sum[joints[ring * 4 + k]] = sum.GetValueOrDefault(joints[ring * 4 + k]) + weights[ring * 4 + k] * share;
            }
            var best = sum.Where(pair => pair.Value > 0).OrderByDescending(pair => pair.Value).Take(4).ToArray();
            var total = best.Sum(pair => pair.Value);
            for (var k = 0; k < 4; k++)
            {
                joints[v * 4 + k] = k < best.Length ? best[k].Key : 0;
                weights[v * 4 + k] = k < best.Length ? best[k].Value / total : 0;
            }
        }
    }

    /// <summary>
    /// Fills what is left between the two edges with a strip of thin triangles (each wrist's two rings, zipped by their angle
    /// round the wrist), drawn with the new hands' skin: the edges meet along their length only where their points line up.
    /// </summary>
    private static void Zip(List<OutfitSwapSection> sections, HashSet<int> native, Attributes mesh, int[] outfit, int[] hand)
    {
        var strip = new List<int>();
        var positions = mesh.Positions;
        foreach (var side in new[] { -1, 1 })
        {
            var o = outfit.Where(v => Math.Sign(positions[v].Z) == side).ToArray();
            var h = hand.Where(v => Math.Sign(positions[v].Z) == side).ToArray();
            if (o.Length < 3 || h.Length < 3) continue;
            var all = o.Concat(h).Select(v => positions[v]).ToArray();
            var centre = all.Aggregate(Vector3.Zero, (sum, p) => sum + p) / all.Length;
            // The ring's plane: its normal is the direction it spreads least in (power iteration on trace - covariance).
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var p in all)
            {
                var d = p - centre;
                xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
            }
            var trace = xx + yy + zz;
            var normal = Vector3.Normalize(new Vector3(1, 0.7f, 0.3f));
            for (var round = 0; round < 64; round++)
                normal = Vector3.Normalize(new Vector3(
                    (trace - xx) * normal.X - xy * normal.Y - xz * normal.Z,
                    -xy * normal.X + (trace - yy) * normal.Y - yz * normal.Z,
                    -xz * normal.X - yz * normal.Y + (trace - zz) * normal.Z));
            var u = Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
            var w = Vector3.Cross(normal, u);
            float Angle(int v) => MathF.Atan2(Vector3.Dot(positions[v] - centre, w), Vector3.Dot(positions[v] - centre, u));
            var ring = o.OrderBy(Angle).ToArray();
            var other = h.OrderBy(Angle).ToArray();
            if (ring.Length == other.Length)
            {
                // As many points on each edge: each of the hands' goes exactly onto its outfit partner, so the edges are one.
                var shift = Enumerable.Range(0, ring.Length).MinBy(k =>
                    Enumerable.Range(0, ring.Length).Sum(n => Vector3.Distance(positions[ring[n]], positions[other[(n + k) % ring.Length]])));
                for (var n = 0; n < ring.Length; n++)
                {
                    var partner = other[(n + shift) % ring.Length];
                    foreach (var copy in mesh.Copies(partner))
                    {
                        positions[copy] = positions[ring[n]];
                        // Shaded alike too, or a line shows where they meet.
                        mesh.Normals[copy] = mesh.Normals[ring[n]];
                        mesh.Tangents[copy] = mesh.Tangents[ring[n]];
                        for (var k = 0; k < 4; k++)
                        {
                            mesh.Joints[copy * 4 + k] = mesh.Joints[ring[n] * 4 + k];
                            mesh.Weights[copy * 4 + k] = mesh.Weights[ring[n] * 4 + k];
                        }
                    }
                }
                continue;
            }
            // The outfit's ring points copied into the hands' skin, with the picture position of the hands' nearest point.
            var copies = ring.Select(v => mesh.Copy(v, mesh.Uv0[other.MinBy(x => Vector3.Distance(positions[x], positions[v]))])).ToArray();
            int i = 0, j = 0;
            var total = ring.Length + other.Length;
            for (var step = 0; step < total; step++)
            {
                var nextRing = Angle(ring[(i + 1) % ring.Length]) + (i + 1 >= ring.Length ? MathF.Tau : 0);
                var nextOther = Angle(other[(j + 1) % other.Length]) + (j + 1 >= other.Length ? MathF.Tau : 0);
                int a = copies[i % ring.Length], b = other[j % other.Length], c;
                if (i < ring.Length && (j >= other.Length || nextRing <= nextOther)) { c = copies[(i + 1) % ring.Length]; i++; }
                else { c = other[(j + 1) % other.Length]; j++; }
                var face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                var outward = (positions[a] + positions[b] + positions[c]) / 3 - centre;
                outward -= normal * Vector3.Dot(outward, normal);
                strip.AddRange(Vector3.Dot(face, outward) >= 0 ? [a, b, c] : [a, c, b]);
            }
        }
        if (strip.Count == 0) return;
        var at = sections.FindIndex(section => native.Contains(section.Material));
        sections[at] = new OutfitSwapSection(sections[at].Material, [.. sections[at].Triangles, .. strip]);
    }

    /// <summary>Cloth vertices of small connected pieces lying wholly on the fingers and hand (nails, rings).</summary>
    private static bool[] SmallOnHands(OutfitSwapMesh mesh, float[] hands, int largest = 400)
    {
        var parent = Enumerable.Range(0, mesh.VertexCount).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        var cloth = new bool[mesh.VertexCount];
        foreach (var section in mesh.Sections.Where(section => !OutfitSwapBodies.IsSkin(mesh.Materials[section.Material])))
            for (var i = 0; i + 2 < section.Triangles.Length; i += 3)
            {
                int a = section.Triangles[i], b = section.Triangles[i + 1], c = section.Triangles[i + 2];
                cloth[a] = cloth[b] = cloth[c] = true;
                parent[Find(a)] = Find(b);
                parent[Find(b)] = Find(c);
            }
        var parts = Enumerable.Range(0, mesh.VertexCount).Where(v => cloth[v]).GroupBy(Find)
            .Where(part => part.Count() < largest && part.All(v => hands[v] >= 0.8f)).SelectMany(part => part).ToHashSet();
        return Enumerable.Range(0, mesh.VertexCount).Select(parts.Contains).ToArray();
    }
}
