using System.Numerics;

namespace Arkive.Core.Outfits;

/// <summary>
/// The new body keeps its own feet: the outfit's bare feet are cut away and the new body's base-body feet are put in their
/// place, so toes and soles are the class's own modelling (the outfit's shoes are wrapped around them during the fit).
/// </summary>
public static class OutfitSwapFeet
{
    /// <summary>How far above the ankle joint the outfit's skin gives way to the new body's feet.</summary>
    private const float Cut = 0.03f;

    /// <summary>A foot's shape as a box along it: heel-to-toe, up and across axes from the heel, and its extent on each.</summary>
    public readonly record struct FootBox(Vector3 Origin, Vector3 Forward, Vector3 Up, Vector3 Across, Vector3 Low, Vector3 High)
    {
        public Vector3 Local(Vector3 point)
        {
            var d = point - Origin;
            return new Vector3(Vector3.Dot(d, Forward), Vector3.Dot(d, Up), Vector3.Dot(d, Across));
        }

        public Vector3 World(Vector3 local) => Origin + Forward * local.X + Up * local.Y + Across * local.Z;

        /// <summary>The foot of the skin points below the ankle on one side (the sign of Z), or null when there are too few.</summary>
        public static FootBox? Of(IEnumerable<Vector3> skin, float side, float ankle)
        {
            var points = skin.Where(p => p.Y < ankle && MathF.Sign(p.Z) == MathF.Sign(side)).ToArray();
            if (points.Length < 30) return null;
            // Bodies face +X: the heel is the rearmost point, the toe tip the foremost.
            var heel = points.MinBy(p => p.X);
            var toe = points.MaxBy(p => p.X);
            var forward = Vector3.Normalize(toe - heel);
            var across = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            var up = Vector3.Normalize(Vector3.Cross(across, forward));
            var box = new FootBox(heel, forward, up, across, new Vector3(float.MaxValue), new Vector3(float.MinValue));
            var low = new Vector3(float.MaxValue);
            var high = new Vector3(float.MinValue);
            foreach (var p in points)
            {
                var local = box.Local(p);
                low = Vector3.Min(low, local);
                high = Vector3.Max(high, local);
            }
            return box with { Low = low, High = high };
        }
    }

    /// <summary>
    /// How the new body's feet stand in the outfit's shoes: each foot turned as a whole about its ankle joint so it points
    /// down at the angle the old foot did (heel heights differ a little between classes; the foot's own modelling is kept,
    /// only its angle changes, as an animation would turn it).
    /// </summary>
    public sealed class Stance
    {
        private readonly (Vector3 Pivot, Quaternion Turn)[] _sides;
        private readonly float _ankle;

        public Stance(IReadOnlyList<Vector3> donorFoot, float donorAnkle, IReadOnlyList<Vector3> targetFoot, float targetAnkle,
            Vector3 leftJoint, Vector3 rightJoint)
        {
            _ankle = targetAnkle;
            _sides = new (Vector3, Quaternion)[2];
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                var joint = MathF.Sign(leftJoint.Z) == MathF.Sign(side) ? leftJoint : rightJoint;
                _sides[i] = FootBox.Of(donorFoot, side, donorAnkle) is { } from && FootBox.Of(targetFoot, side, targetAnkle) is { } onto
                    ? (joint, OutfitSwapFitting.Between(onto.Forward, from.Forward))
                    : (joint, Quaternion.Identity);
            }
        }

        /// <summary>A point of the new body's skin after its foot has turned; above the ankle it fades back to where it was.</summary>
        public Vector3 Apply(Vector3 point)
        {
            var (pivot, turn) = _sides[point.Z < 0 ? 0 : 1];
            var share = Math.Clamp((_ankle + Cut - point.Y) / 0.04f, 0, 1);
            return share <= 0 ? point : Vector3.Lerp(point, pivot + Vector3.Transform(point - pivot, turn), share);
        }

        /// <summary>A foot box of the new body turned with its foot.</summary>
        public FootBox Turn(FootBox box, float side)
        {
            var (pivot, turn) = _sides[side < 0 ? 0 : 1];
            return box with
            {
                Origin = pivot + Vector3.Transform(box.Origin - pivot, turn), Forward = Vector3.Transform(box.Forward, turn),
                Up = Vector3.Transform(box.Up, turn), Across = Vector3.Transform(box.Across, turn),
            };
        }

        public Vector3 ApplyDirection(Vector3 point, Vector3 direction)
        {
            var (_, turn) = _sides[point.Z < 0 ? 0 : 1];
            var share = Math.Clamp((_ankle + Cut - point.Y) / 0.04f, 0, 1);
            return Vector3.Normalize(Vector3.Lerp(direction, Vector3.Transform(direction, turn), share));
        }
    }

    /// <summary>
    /// After the new feet are in (vertices from <paramref name="firstFootVertex"/> on): shoes and socks are moved off them
    /// wherever the feet still show through (keeping the gap they had around the old feet), and skin the shoes and socks
    /// cover anyway is left out, so it can never show through them.
    /// </summary>
    /// <param name="shownFoot">Where the outfit showed its own bare foot: its artists cut the skin away under shoes and
    /// socks, so the new feet show only near there too (a toe or heel that would poke through a shoe is left out).</param>
    public static (OutfitSwapMesh Mesh, Vector3[] Fitted) Finish(OutfitSwapMesh mesh, Vector3[] fitted, int firstFootVertex,
        Vector3[] donorPositions, bool[] footCloth, OutfitSwapBody donorBody, Vector3[] shownFoot)
    {
        if (fitted.Length <= firstFootVertex) return (mesh, fitted);
        var footSections = mesh.Sections.Select(section => new OutfitSwapSection(section.Material,
            Triangles(section.Triangles, (a, b, c) => a >= firstFootVertex && b >= firstFootVertex && c >= firstFootVertex))).Where(section => section.Triangles.Length > 0).ToArray();
        if (OutfitSwapClearance.Surface(mesh, fitted, footSections) is not { } feet) return (mesh, fitted);
        var moved = OutfitSwapClearance.PushOut(donorPositions, fitted[..firstFootVertex], donorBody, feet, footCloth,
            margin: 0.002f, radius: 0.006f, rounds: 24, all: true);
        var result = (Vector3[])fitted.Clone();
        Array.Copy(moved, result, firstFootVertex);

        var clothSections = mesh.Sections.Select(section => new OutfitSwapSection(section.Material,
            Triangles(section.Triangles, (a, b, c) => a < firstFootVertex && footCloth[a] && footCloth[b] && footCloth[c]))).Where(section => section.Triangles.Length > 0).ToArray();
        if (clothSections.Length == 0) return (mesh, result);
        var caster = new Caster(result, clothSections);
        TuckUnder(result, mesh.Normals, firstFootVertex, caster);
        caster = new Caster(result, clothSections);
        var shown = new OutfitSwapKdTree(shownFoot.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray(), 3);
        var index = new int[1];
        var distance = new float[1];
        bool Covered(int v)
        {
            if (v < firstFootVertex) return false;
            // Under a shoe or sock: one is right in front of it.
            var p = result[v];
            // (A cloth surface facing the same way as the skin, like a sock over it; not an insole the toes rest on.)
            if (mesh.Normals[v].LengthSquared() > 1e-8f && caster.Hit(p, Vector3.Normalize(mesh.Normals[v]), 0.01f,
                    Vector3.Normalize(mesh.Normals[v]), mesh.Normals) is not null) return true;
            // Far beyond any bare foot the outfit showed (it hid that part in its own design, so it is hidden here too).
            return shownFoot.Length > 0 && shown.Nearest([p.X, p.Y, p.Z], 1, index, distance) > 0 && distance[0] > 0.05f;
        }
        var sections = mesh.Sections.Select(section => new OutfitSwapSection(section.Material,
            Triangles(section.Triangles, (a, b, c) => !(Covered(a) && Covered(b) && Covered(c))))).ToArray();
        return (new OutfitSwapMesh
        {
            Positions = result, Normals = mesh.Normals, Tangents = mesh.Tangents, Uv0 = mesh.Uv0, Uv1 = mesh.Uv1, Colours = mesh.Colours,
            Joints = mesh.Joints, Weights = mesh.Weights, Sections = sections, Materials = mesh.Materials,
            BoneNames = mesh.BoneNames, BonePositions = mesh.BonePositions, BoneParents = mesh.BoneParents, OwnPictureSkin = mesh.OwnPictureSkin,
            MaterialReferences = mesh.MaterialReferences,
        }, result);
    }

    /// <summary>Rays against a set of triangles (shoes and socks), nearest hit only.</summary>
    private sealed class Caster
    {
        private readonly Vector3[] _positions;
        private readonly int[] _triangles;
        private readonly OutfitSwapKdTree? _tree;
        private readonly List<int> _near = [];

        public Caster(Vector3[] positions, OutfitSwapSection[] sections)
        {
            _positions = positions;
            _triangles = sections.SelectMany(section => section.Triangles).ToArray();
            if (_triangles.Length == 0) return;
            var centres = new float[_triangles.Length];
            for (var i = 0; i + 2 < _triangles.Length; i += 3)
            {
                var c = (positions[_triangles[i]] + positions[_triangles[i + 1]] + positions[_triangles[i + 2]]) / 3;
                centres[i] = c.X; centres[i + 1] = c.Y; centres[i + 2] = c.Z;
            }
            _tree = new OutfitSwapKdTree(centres, 3);
        }

        /// <summary>The nearest hit along the ray; with <paramref name="facing"/>, only surfaces whose outside (by the
        /// vertex normals) faces that way.</summary>
        public float? Hit(Vector3 origin, Vector3 direction, float reach, Vector3? facing = null, Vector3[]? normals = null)
        {
            if (_tree is null) return null;
            _tree.Within([origin.X, origin.Y, origin.Z], reach + 0.02f, _near);
            float? best = null;
            foreach (var t in _near)
            {
                Vector3 a = _positions[_triangles[t * 3]], b = _positions[_triangles[t * 3 + 1]], c = _positions[_triangles[t * 3 + 2]];
                Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(direction, e2);
                var det = Vector3.Dot(e1, p);
                if (MathF.Abs(det) < 1e-12f) continue;
                var inverse = 1 / det;
                var s = origin - a;
                var u = Vector3.Dot(s, p) * inverse;
                if (u < 0 || u > 1) continue;
                var q = Vector3.Cross(s, e1);
                var v = Vector3.Dot(direction, q) * inverse;
                if (v < 0 || u + v > 1) continue;
                var distance = Vector3.Dot(e2, q) * inverse;
                if (facing is { } wanted && normals is not null &&
                    Vector3.Dot(normals[_triangles[t * 3]] + normals[_triangles[t * 3 + 1]] + normals[_triangles[t * 3 + 2]], wanted) < 0.5f)
                    continue;
                if (distance > 0 && distance <= reach && (best is null || distance < best)) best = distance;
            }
            return best;
        }
    }

    /// <summary>
    /// Skin just outside a shoe's surface (it would show through it) is moved in under it: a vertex whose inward ray meets
    /// the shoe within a few millimetres, with no shoe in front of it, goes 2 mm past that surface.
    /// </summary>
    private static void TuckUnder(Vector3[] positions, Vector3[] normals, int firstFootVertex, Caster shoes)
    {
        for (var v = firstFootVertex; v < positions.Length; v++)
        {
            if (normals[v].LengthSquared() < 1e-8f) continue;
            var normal = Vector3.Normalize(normals[v]);
            // Only through a shoe's outside facing the same way (the back of a heel cup), not onto an insole or strap below.
            if (shoes.Hit(positions[v], -normal, 0.005f, normal, normals) is { } inward && shoes.Hit(positions[v], normal, 0.01f) is null)
                positions[v] -= normal * (inward + 0.002f);
        }
    }

    private static int[] Triangles(int[] triangles, Func<int, int, int, bool> keep)
    {
        var kept = new List<int>(triangles.Length);
        for (var i = 0; i + 2 < triangles.Length; i += 3)
            if (keep(triangles[i], triangles[i + 1], triangles[i + 2])) kept.AddRange([triangles[i], triangles[i + 1], triangles[i + 2]]);
        return kept.ToArray();
    }

    /// <summary>
    /// Shoes (and socks) reshaped around the new body's own feet: each point keeps its place relative to the old foot's
    /// box (along it, above it, across it), laid into the new foot's box. The boxes differ by a few percent at most, so
    /// the change is subtle; how much a point follows is its share of foot weight.
    /// </summary>
    public static Vector3[] FitShoes(Vector3[] donorPositions, Vector3[] fitted, float[] footShares, bool[] cloth,
        IReadOnlyList<Vector3> donorFoot, float donorAnkle, IReadOnlyList<Vector3> targetFoot, float targetAnkle, Stance? stance = null)
    {
        var result = (Vector3[])fitted.Clone();
        foreach (var side in new[] { -1f, 1f })
        {
            if (FootBox.Of(donorFoot, side, donorAnkle) is not { } from || FootBox.Of(targetFoot, side, targetAnkle) is not { } still) continue;
            var onto = stance?.Turn(still, side) ?? still;
            var fromSize = from.High - from.Low;
            var ontoSize = onto.High - onto.Low;
            // Length and width from the feet; height (which depends on where each class's ankle joint sits) from those two.
            var length = Math.Clamp(ontoSize.X / Math.Max(fromSize.X, 1e-3f), 0.8f, 1.25f);
            var width = Math.Clamp(ontoSize.Z / Math.Max(fromSize.Z, 1e-3f), 0.8f, 1.25f);
            var scale = new Vector3(length, (length + width) / 2, width);
            for (var v = 0; v < fitted.Length; v++)
            {
                if (!cloth[v] || footShares[v] < 0.1f || MathF.Sign(donorPositions[v].Z) != MathF.Sign(side)) continue;
                var local = from.Local(donorPositions[v]);
                var placed = onto.World(onto.Low + (local - from.Low) * scale);
                result[v] = Vector3.Lerp(fitted[v], placed, Math.Clamp((footShares[v] - 0.1f) / 0.4f, 0, 1));
            }
        }
        return result;
    }

    /// <summary>
    /// The piece with its bare feet replaced by the base body's (an overlapping band at the ankle hides the join), and the
    /// fitted positions to go with it; unchanged when the piece shows no bare feet.
    /// </summary>
    /// <param name="ankle">The height of the new body's ankle joints: skin below it is foot. Base bodies often carry no foot
    /// bones, so the feet are told by height, and the new feet take the skin weights of the outfit's skin next to them.</param>
    public static (OutfitSwapMesh Mesh, Vector3[] Fitted, Vector3[] ShownFoot) WithBodyFeet(OutfitSwapMesh mesh, Vector3[] fitted, IReadOnlyList<OutfitSwapMesh> baseBody,
        float ankle, Stance? stance = null)
    {
        var sections = new List<OutfitSwapSection>();
        var removed = 0;
        var removedPoints = new List<Vector3>();
        foreach (var section in mesh.Sections)
        {
            if (!OutfitSwapBodies.IsSkin(mesh.Materials[section.Material])) { sections.Add(section); continue; }
            var kept = new List<int>();
            var t = section.Triangles;
            for (var i = 0; i + 2 < t.Length; i += 3)
            {
                // Any triangle reaching below the ankle goes; the new feet reach 2 cm above it, so the two overlap on the
                // body's surface (same shape, picture and shading) and no edge shows.
                // Up to where the new feet stop turning (Stance), so the outfit's skin and the new feet meet where both lie
                // on the new body unturned.
                if (fitted[t[i]].Y < ankle + Cut || fitted[t[i + 1]].Y < ankle + Cut || fitted[t[i + 2]].Y < ankle + Cut)
                {
                    removed++;
                    removedPoints.AddRange([fitted[t[i]], fitted[t[i + 1]], fitted[t[i + 2]]]);
                    continue;
                }
                kept.AddRange([t[i], t[i + 1], t[i + 2]]);
            }
            sections.Add(new OutfitSwapSection(section.Material, kept.ToArray()));
        }
        if (removed == 0) return (mesh, fitted, []);

        var materials = mesh.Materials.ToList();
        var lowerSkin = sections.FindIndex(section => OutfitSwapBodies.IsSkin(materials[section.Material]) && OutfitSwapBodies.IsLowerSkin(materials[section.Material]));
        var bones = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < mesh.BoneNames.Length; i++) bones.TryAdd(mesh.BoneNames[i], i);
        var positions = new List<Vector3>(fitted);
        var normals = mesh.Normals.ToList();
        var tangents = mesh.Tangents.ToList();
        var uv0 = mesh.Uv0.ToList();
        var uv1 = mesh.Uv1.ToList();
        var colours = mesh.Colours.ToList();
        var joints = mesh.Joints.ToList();
        var weights = mesh.Weights.ToList();
        var added = new List<int>();
        string? bodyMaterial = null;
        // The outfit's own feet and shoes (fitted), to borrow their skin weights (foot and toe bones) for the new feet.
        var footShares = OutfitSwapFitting.FootShares(mesh);
        var skinVertices = mesh.Sections.SelectMany(section => section.Triangles).Distinct().Where(v => footShares[v] >= 0.5f).ToArray();
        if (skinVertices.Length == 0)
            skinVertices = mesh.Sections.Where(section => OutfitSwapBodies.IsSkin(mesh.Materials[section.Material]))
                .SelectMany(section => section.Triangles).Distinct().ToArray();
        var skinTree = new OutfitSwapKdTree(skinVertices.SelectMany(v => new[] { fitted[v].X, fitted[v].Y, fitted[v].Z }).ToArray(), 3);
        var nearest = new int[1];
        var distance = new float[1];
        foreach (var body in baseBody)
        {
            var index = new Dictionary<int, int>();
            foreach (var section in body.Sections)
            {
                if (!OutfitSwapBodies.IsSkin(body.Materials[section.Material])) continue;
                var t = section.Triangles;
                for (var i = 0; i + 2 < t.Length; i += 3)
                {
                    if (body.Positions[t[i]].Y >= ankle + Cut + 0.015f && body.Positions[t[i + 1]].Y >= ankle + Cut + 0.015f &&
                        body.Positions[t[i + 2]].Y >= ankle + Cut + 0.015f) continue;
                    bodyMaterial ??= body.Materials[section.Material];
                    for (var c = 0; c < 3; c++)
                    {
                        var v = t[i + c];
                        if (!index.TryGetValue(v, out var at))
                        {
                            at = index[v] = positions.Count;
                            positions.Add(stance?.Apply(body.Positions[v]) ?? body.Positions[v]);
                            normals.Add(stance?.ApplyDirection(body.Positions[v], body.Normals[v]) ?? body.Normals[v]);
                            tangents.Add(body.Tangents[v]);
                            uv0.Add(body.Uv0[v]);
                            uv1.Add(body.Uv1[v]);
                            for (var k = 0; k < 4; k++) colours.Add(body.Colours[v * 4 + k]);
                            var p = positions[^1];
                            skinTree.Nearest([p.X, p.Y, p.Z], 1, nearest, distance);
                            var from = skinVertices[nearest[0]];
                            for (var k = 0; k < 4; k++)
                            {
                                joints.Add(mesh.Joints[from * 4 + k]);
                                weights.Add(mesh.Weights[from * 4 + k]);
                            }
                        }
                        added.Add(at);
                    }
                }
            }
        }
        if (added.Count == 0 || bodyMaterial is null) return (mesh, fitted, []);
        if (lowerSkin >= 0)
            sections[lowerSkin] = new OutfitSwapSection(sections[lowerSkin].Material, [.. sections[lowerSkin].Triangles, .. added]);
        else
        {
            materials.Add(bodyMaterial);
            sections.Add(new OutfitSwapSection(materials.Count - 1, added.ToArray()));
        }
        var result = new OutfitSwapMesh
        {
            Positions = positions.ToArray(), Normals = normals.ToArray(), Tangents = tangents.ToArray(), Uv0 = uv0.ToArray(), Uv1 = uv1.ToArray(),
            Colours = colours.ToArray(), Joints = joints.ToArray(), Weights = weights.ToArray(), Sections = sections, Materials = materials,
            BoneNames = mesh.BoneNames, BonePositions = mesh.BonePositions, BoneParents = mesh.BoneParents, OwnPictureSkin = mesh.OwnPictureSkin,
            MaterialReferences = mesh.MaterialReferences,
        };
        // The outfit's skin was laid on the new body standing as it does unturned; turned with the feet, it marks where
        // the turned feet show.
        return (result, positions.ToArray(), removedPoints.Select(p => stance?.Apply(p) ?? p).ToArray());
    }
}
