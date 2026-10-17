using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Arkive.Core.Proportions;

/// <summary>
/// How far each vertex of one glTF mesh (one LOD) moves per area, for a +100% setting. A setting of s percent
/// moves every vertex by s/100 times its offset; offsets of several areas add up.
/// </summary>
public sealed record BodyProportionMeshBasis(int MeshIndex, string MeshName, int VertexCount, IReadOnlyDictionary<string, float[]> Offsets);

/// <summary>Which hanging parts follow the body under them instead of staying where they are.</summary>
/// <param name="LooseClothing">Skirts, capes, coat tails, sashes and ribbons (cloth with bones of its own).</param>
/// <param name="Hair">Hair.</param>
public readonly record struct BodyProportionFollow(bool LooseClothing, bool Hair)
{
    public static BodyProportionFollow All => new(true, true);
}

/// <summary>The proportion offsets of one editable skeletal mesh GLB.</summary>
public sealed record BodyProportionModel(
    BodyProfile Profile,
    BodyProportionFollow Follow,
    IReadOnlyList<BodyProportionMeshBasis> Meshes,
    int LooseVertexCount,
    int HairVertexCount)
{
    /// <summary>The areas this mesh has vertices in; sliders for other areas change nothing on it.</summary>
    public IReadOnlyCollection<string> AffectedZones =>
        Meshes.SelectMany(mesh => mesh.Offsets.Keys).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// Reshapes a player or NPC mesh by moving its vertices in the skeleton's rest pose. Each body area is found from
/// the bones that move it (the same skin weights the game uses) and from where it sits, so tight clothing and the
/// body under it move together and pieces of one outfit keep meeting at their seams. Cloth and hair that hang from
/// bones of their own can follow the nearest body surface. Bones never move, so animations still fit. Nothing is
/// made longer: the skeleton is the game's and lengths belong to it.
/// </summary>
public static partial class BodyProportionEngine
{
    public sealed class UnknownBodyException(string message) : Exception(message);

    /// <param name="objectName">The mesh's object name, which identifies tuned NPCs.</param>
    public static BodyProportionModel Build(byte[] glb, BodyProportionFollow follow, BodyProfile? profile = null, string? objectName = null)
    {
        var container = GlbContainer.Parse(glb);
        var (bones, jointNames) = RestBones(container);
        profile ??= BodyProfiles.Detect(bones, objectName)
            ?? throw new UnknownBodyException("This mesh's skeleton is not one of the bodies Arkive has tuned.");
        var meshes = new List<BodyProportionMeshBasis>();
        var seen = new HashSet<int>();
        int looseVertices = 0, hairVertices = 0;
        var meshArray = container.Json["meshes"]?.AsArray() ?? [];
        for (var meshIndex = 0; meshIndex < meshArray.Count; meshIndex++)
        {
            var mesh = meshArray[meshIndex]!.AsObject();
            var attributes = mesh["primitives"]![0]!["attributes"]!.AsObject();
            if (attributes["POSITION"] is null || attributes["JOINTS_0"] is null || attributes["WEIGHTS_0"] is null) continue;
            var positionAccessor = (int)attributes["POSITION"]!;
            if (!seen.Add(positionAccessor)) continue;
            var vertices = ReadVertices(container, attributes, jointNames);
            looseVertices += vertices.Count(vertex => vertex.Loose > 0.01f);
            hairVertices += vertices.Count(vertex => vertex.Hair > 0.01f);
            var shaper = new Shaper(profile, bones, vertices);
            var followers = vertices.Select(vertex => Math.Min(1f, (follow.LooseClothing ? vertex.Loose : 0) + (follow.Hair ? vertex.Hair : 0))).ToArray();
            var transfer = followers.Any(weight => weight > 0.01f) ? new FollowTransfer(vertices, followers, shaper.Height) : null;
            var offsets = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var zone in profile.Zones)
            {
                var offset = shaper.Offsets(zone.Id);
                if (offset is null) continue;
                transfer?.Apply(offset);
                offsets[zone.Id] = offset;
            }
            meshes.Add(new BodyProportionMeshBasis(meshIndex, (string?)mesh["name"] ?? $"mesh {meshIndex}", vertices.Length, offsets));
        }
        if (meshes.Count == 0) throw new InvalidDataException("The GLB has no skinned meshes to reshape.");
        return new BodyProportionModel(profile, follow, meshes, looseVertices, hairVertices);
    }

    /// <summary>Returns the GLB with every LOD reshaped by <paramref name="settings"/> (percent per area).</summary>
    public static byte[] Apply(byte[] glb, BodyProportionModel model, IReadOnlyDictionary<string, double> settings)
    {
        var container = GlbContainer.Parse(glb);
        var zones = model.Profile.Zones.ToDictionary(zone => zone.Id, StringComparer.Ordinal);
        var meshArray = container.Json["meshes"]!.AsArray();
        foreach (var basis in model.Meshes)
        {
            var attributes = meshArray[basis.MeshIndex]!["primitives"]![0]!["attributes"]!.AsObject();
            var view = container.Accessor((int)attributes["POSITION"]!);
            if (view.Count != basis.VertexCount || view.ComponentType != 5126 || view.Components != 3)
                throw new InvalidDataException($"{basis.MeshName} no longer matches the mesh the sliders were prepared from.");
            var active = basis.Offsets
                .Select(pair => (Offsets: pair.Value, Scale: zones.TryGetValue(pair.Key, out var zone) &&
                    settings.TryGetValue(pair.Key, out var value)
                        ? (float)(Math.Clamp(value, zone.Minimum, zone.Maximum) / 100.0) : 0f))
                .Where(item => item.Scale != 0).ToArray();
            if (active.Length == 0) continue;
            var minimum = new Vector3(float.MaxValue);
            var maximum = new Vector3(float.MinValue);
            var before = new Vector3[view.Count];
            var after = new Vector3[view.Count];
            for (var vertex = 0; vertex < view.Count; vertex++)
            {
                var position = new Vector3(container.ReadFloat(view, vertex, 0), container.ReadFloat(view, vertex, 1),
                    container.ReadFloat(view, vertex, 2));
                before[vertex] = position;
                foreach (var (offsets, scale) in active)
                    position += new Vector3(offsets[vertex * 3], offsets[vertex * 3 + 1], offsets[vertex * 3 + 2]) * scale;
                after[vertex] = position;
            }
            KeepEdgesFromCollapsing(container, meshArray[basis.MeshIndex]!.AsObject(), before, after);
            for (var vertex = 0; vertex < view.Count; vertex++)
            {
                var position = after[vertex];
                container.WriteFloat(view, vertex, 0, position.X);
                container.WriteFloat(view, vertex, 1, position.Y);
                container.WriteFloat(view, vertex, 2, position.Z);
                minimum = Vector3.Min(minimum, position);
                maximum = Vector3.Max(maximum, position);
            }
            view.Accessor["min"] = new JsonArray(minimum.X, minimum.Y, minimum.Z);
            view.Accessor["max"] = new JsonArray(maximum.X, maximum.Y, maximum.Z);
            TurnNormals(container, meshArray[basis.MeshIndex]!.AsObject(), before, after);
        }
        return container.ToBytes();
    }

    /// <summary>Shortest share of its game length an edge may keep after reshaping.</summary>
    public const float MinimumEdgeShare = 0.5f;

    /// <summary>
    /// Keeps reshaping from folding the surface. Where neighbouring points would end up much closer than the game
    /// placed them (pushes meeting at a crease, such as the crotch where both thighs widen), they keep only part of
    /// their move, easing back to the full move over the next two rings of points. A folded edge stretches many times
    /// over once the body animates, which the stress poses checked when a mod is saved refuse.
    /// </summary>
    private static void KeepEdgesFromCollapsing(GlbContainer container, JsonObject mesh, Vector3[] before, Vector3[] after)
    {
        // Split vertices (UV seams, hard edges) share a position and must keep moving together.
        var groups = new int[before.Length];
        var keys = new Dictionary<(int, int, int), int>();
        for (var vertex = 0; vertex < before.Length; vertex++)
        {
            var p = before[vertex];
            var key = ((int)MathF.Round(p.X * 1e5f), (int)MathF.Round(p.Y * 1e5f), (int)MathF.Round(p.Z * 1e5f));
            if (!keys.TryGetValue(key, out var group)) keys[key] = group = keys.Count;
            groups[vertex] = group;
        }
        var start = new Vector3[keys.Count];
        var move = new Vector3[keys.Count];
        for (var vertex = before.Length - 1; vertex >= 0; vertex--)
        {
            start[groups[vertex]] = before[vertex];
            move[groups[vertex]] = after[vertex] - before[vertex];
        }
        var edges = new HashSet<(int, int)>();
        foreach (var primitive in mesh["primitives"]!.AsArray())
        {
            if (primitive?["indices"] is null) continue;
            var indices = container.Accessor((int)primitive["indices"]!);
            for (var corner = 0; corner + 2 < indices.Count; corner += 3)
            {
                var a = groups[container.ReadInt(indices, corner, 0)];
                var b = groups[container.ReadInt(indices, corner + 1, 0)];
                var c = groups[container.ReadInt(indices, corner + 2, 0)];
                foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                    if (x != y) edges.Add(x < y ? (x, y) : (y, x));
            }
        }
        var factor = new float[keys.Count];
        Array.Fill(factor, 1f);
        var limited = false;
        for (var pass = 0; pass < 12; pass++)
        {
            var collapsed = false;
            foreach (var (a, b) in edges)
            {
                var length = Vector3.Distance(start[a], start[b]);
                if (length < 1e-5f) continue;
                var reshaped = Vector3.Distance(start[a] + move[a] * factor[a], start[b] + move[b] * factor[b]);
                if (reshaped >= length * MinimumEdgeShare) continue;
                factor[a] *= 0.6f;
                factor[b] *= 0.6f;
                collapsed = limited = true;
            }
            if (!collapsed) break;
        }
        if (!limited) return;
        // Ease back to the full move over two rings, so the held-back points don't leave a dent.
        for (var ring = 0; ring < 2; ring++)
        {
            var eased = (float[])factor.Clone();
            foreach (var (a, b) in edges)
            {
                eased[a] = Math.Min(eased[a], factor[b] + 0.35f);
                eased[b] = Math.Min(eased[b], factor[a] + 0.35f);
            }
            factor = eased;
        }
        for (var vertex = 0; vertex < before.Length; vertex++)
            after[vertex] = before[vertex] + move[groups[vertex]] * factor[groups[vertex]];
    }

    /// <summary>
    /// Turns each moved vertex's normal and tangent with the surface around it, so lighting and shadows follow the
    /// new shape. The surface direction is taken from the faces around each position (split vertices along UV seams
    /// and hard edges share it), and the stored normal is turned by the same amount the surface turned, which keeps
    /// the game's own smoothing, hard edges and normal-map alignment.
    /// </summary>
    private static void TurnNormals(GlbContainer container, JsonObject mesh, Vector3[] before, Vector3[] after)
    {
        var primitives = mesh["primitives"]!.AsArray();
        var attributes = primitives[0]!["attributes"]!.AsObject();
        if (attributes["NORMAL"] is null) return;
        var normals = container.Accessor((int)attributes["NORMAL"]!);
        var tangents = attributes["TANGENT"] is null ? null : container.Accessor((int)attributes["TANGENT"]!);
        if (normals.ComponentType != 5126 || normals.Count != before.Length) return;
        if (tangents is not null && (tangents.ComponentType != 5126 || tangents.Count != before.Length)) tangents = null;

        // Split vertices share a position; give them one surface direction.
        var groups = new int[before.Length];
        var keys = new Dictionary<(int, int, int), int>();
        for (var vertex = 0; vertex < before.Length; vertex++)
        {
            var p = before[vertex];
            var key = ((int)MathF.Round(p.X * 1e5f), (int)MathF.Round(p.Y * 1e5f), (int)MathF.Round(p.Z * 1e5f));
            if (!keys.TryGetValue(key, out var group)) keys[key] = group = keys.Count;
            groups[vertex] = group;
        }
        var oldSurface = new Vector3[keys.Count];
        var newSurface = new Vector3[keys.Count];
        var moved = new bool[keys.Count];
        for (var vertex = 0; vertex < before.Length; vertex++)
            if (Vector3.DistanceSquared(before[vertex], after[vertex]) > 1e-12f) moved[groups[vertex]] = true;
        foreach (var primitive in primitives)
        {
            if (primitive?["indices"] is null) continue;
            var indices = container.Accessor((int)primitive["indices"]!);
            for (var corner = 0; corner + 2 < indices.Count; corner += 3)
            {
                var a = container.ReadInt(indices, corner, 0);
                var b = container.ReadInt(indices, corner + 1, 0);
                var c = container.ReadInt(indices, corner + 2, 0);
                if (a >= before.Length || b >= before.Length || c >= before.Length) continue;
                if (!moved[groups[a]] && !moved[groups[b]] && !moved[groups[c]]) continue;
                // Area-weighted face directions.
                var oldFace = Vector3.Cross(before[b] - before[a], before[c] - before[a]);
                var newFace = Vector3.Cross(after[b] - after[a], after[c] - after[a]);
                foreach (var corner3 in new[] { a, b, c })
                {
                    oldSurface[groups[corner3]] += oldFace;
                    newSurface[groups[corner3]] += newFace;
                }
            }
        }
        for (var vertex = 0; vertex < before.Length; vertex++)
        {
            var group = groups[vertex];
            if (!moved[group]) continue;
            var from = oldSurface[group];
            var to = newSurface[group];
            if (from.LengthSquared() < 1e-20f || to.LengthSquared() < 1e-20f) continue;
            from = Vector3.Normalize(from);
            to = Vector3.Normalize(to);
            // Tiny or folded faces (stitching, buckles) can flip while barely moving; leave those as they were and
            // limit any turn to 45 degrees.
            var dot = Vector3.Dot(from, to);
            if (dot < 0) continue;
            var turn = Quaternion.Slerp(Quaternion.Identity, Turn(from, to), Math.Min(1f, (MathF.PI / 4) / MathF.Acos(Math.Min(1f, dot)) ));
            var normal = new Vector3(container.ReadFloat(normals, vertex, 0), container.ReadFloat(normals, vertex, 1),
                container.ReadFloat(normals, vertex, 2));
            if (normal.LengthSquared() > 1e-12f)
            {
                normal = Vector3.Normalize(Vector3.Transform(normal, turn));
                container.WriteFloat(normals, vertex, 0, normal.X);
                container.WriteFloat(normals, vertex, 1, normal.Y);
                container.WriteFloat(normals, vertex, 2, normal.Z);
            }
            if (tangents is null) continue;
            var tangent = new Vector3(container.ReadFloat(tangents, vertex, 0), container.ReadFloat(tangents, vertex, 1),
                container.ReadFloat(tangents, vertex, 2));
            if (tangent.LengthSquared() <= 1e-12f) continue;
            tangent = Vector3.Normalize(Vector3.Transform(tangent, turn));
            container.WriteFloat(tangents, vertex, 0, tangent.X);
            container.WriteFloat(tangents, vertex, 1, tangent.Y);
            container.WriteFloat(tangents, vertex, 2, tangent.Z);
        }
    }

    /// <summary>The smallest rotation that turns unit vector <paramref name="from"/> onto <paramref name="to"/>.</summary>
    private static Quaternion Turn(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot > 0.999999f) return Quaternion.Identity;
        if (dot < -0.999999f)
        {
            // Opposite directions: any axis at right angles to them.
            var axis = Vector3.Cross(Vector3.UnitX, from);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitY, from);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var cross = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(cross, 1 + dot));
    }

    /// <summary>True when every setting is zero, so applying them would change nothing.</summary>
    public static bool IsNeutral(IReadOnlyDictionary<string, double> settings) => settings.Values.All(value => Math.Abs(value) < 1e-6);

    private static (Dictionary<string, Vector3> Bones, string[] Joints) RestBones(GlbContainer container)
    {
        var skin = container.Json["skins"]?[0]?.AsObject()
            ?? throw new InvalidDataException("The GLB has no skeleton.");
        var joints = skin["joints"]!.AsArray().Select(node => BoneName((string?)container.Json["nodes"]![(int)node!]!["name"] ?? "")).ToArray();
        var view = container.Accessor((int)skin["inverseBindMatrices"]!);
        var bones = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        for (var joint = 0; joint < joints.Length; joint++)
        {
            // glTF matrices are column-major; System.Numerics uses row vectors, which is the same memory layout.
            var m = new Matrix4x4(
                container.ReadFloat(view, joint, 0), container.ReadFloat(view, joint, 1), container.ReadFloat(view, joint, 2), container.ReadFloat(view, joint, 3),
                container.ReadFloat(view, joint, 4), container.ReadFloat(view, joint, 5), container.ReadFloat(view, joint, 6), container.ReadFloat(view, joint, 7),
                container.ReadFloat(view, joint, 8), container.ReadFloat(view, joint, 9), container.ReadFloat(view, joint, 10), container.ReadFloat(view, joint, 11),
                container.ReadFloat(view, joint, 12), container.ReadFloat(view, joint, 13), container.ReadFloat(view, joint, 14), container.ReadFloat(view, joint, 15));
            if (Matrix4x4.Invert(m, out var rest)) bones.TryAdd(joints[joint], rest.Translation);
        }
        return (bones, joints);
    }

    // Arkive's GLBs make repeated bone names unique with a __ARKIVE_n suffix.
    private static string BoneName(string node) => ArkiveSuffix().Replace(node, "");

    private sealed record Vertex(Vector3 Position, (string Bone, float Weight)[] Influences, float Loose, float Hair);

    private static Vertex[] ReadVertices(GlbContainer container, JsonObject attributes, string[] jointNames)
    {
        var positions = container.Accessor((int)attributes["POSITION"]!);
        var joints = container.Accessor((int)attributes["JOINTS_0"]!);
        var weights = container.Accessor((int)attributes["WEIGHTS_0"]!);
        var vertices = new Vertex[positions.Count];
        for (var vertex = 0; vertex < vertices.Length; vertex++)
        {
            var influences = new List<(string, float)>(4);
            float loose = 0, hair = 0;
            for (var slot = 0; slot < 4; slot++)
            {
                var weight = container.ReadWeight(weights, vertex, slot);
                if (weight <= 0) continue;
                var joint = container.ReadInt(joints, vertex, slot);
                if (joint >= jointNames.Length) continue;
                var bone = jointNames[joint];
                influences.Add((bone, weight));
                if (HairBone().IsMatch(bone)) hair += weight;
                else if (LooseBone().IsMatch(bone)) loose += weight;
            }
            vertices[vertex] = new Vertex(new Vector3(container.ReadFloat(positions, vertex, 0),
                container.ReadFloat(positions, vertex, 1), container.ReadFloat(positions, vertex, 2)), influences.ToArray(), loose, hair);
        }
        return vertices;
    }

    /// <summary>
    /// Moves cloth and hair that hang from their own bones with the body surface nearest to them: each such vertex
    /// takes the distance-weighted average offset of its closest body vertices, fading out the further it hangs
    /// from the body. Being a fixed average of other offsets, it keeps every slider linear.
    /// </summary>
    private sealed class FollowTransfer
    {
        private const int Neighbours = 8;
        private readonly float[] _followers;
        private readonly (int Vertex, float Weight)[][] _sources;

        public FollowTransfer(Vertex[] vertices, float[] followers, float height)
        {
            _followers = followers;
            _sources = new (int, float)[vertices.Length][];
            var cell = 0.03f * height;
            var grid = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Key(Vector3 position) =>
                ((int)MathF.Floor(position.X / cell), (int)MathF.Floor(position.Y / cell), (int)MathF.Floor(position.Z / cell));
            for (var index = 0; index < vertices.Length; index++)
            {
                if (followers[index] > 0.5f) continue;
                var key = Key(vertices[index].Position);
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
                list.Add(index);
            }
            var reach = 0.25f * height;
            var maximumRings = (int)MathF.Ceiling(reach / cell);
            var nearest = new List<(int Vertex, float Distance)>();
            for (var index = 0; index < vertices.Length; index++)
            {
                if (followers[index] <= 0.01f) continue;
                var position = vertices[index].Position;
                var (x, y, z) = Key(position);
                nearest.Clear();
                // Widen the search ring by ring until enough body vertices are found (or the reach runs out).
                for (var ring = 0; ring <= maximumRings && (nearest.Count < Neighbours || ring <= 1); ring++)
                {
                    for (var dx = -ring; dx <= ring; dx++)
                    for (var dy = -ring; dy <= ring; dy++)
                    for (var dz = -ring; dz <= ring; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != ring) continue;
                        if (!grid.TryGetValue((x + dx, y + dy, z + dz), out var list)) continue;
                        foreach (var candidate in list) nearest.Add((candidate, Vector3.Distance(position, vertices[candidate].Position)));
                    }
                }
                if (nearest.Count == 0) { _sources[index] = []; continue; }
                nearest.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                var closest = nearest.Take(Neighbours).ToArray();
                var spread = Math.Max(closest[0].Distance * 1.5f, 0.01f * height);
                var fade = MathF.Exp(-MathF.Pow(closest[0].Distance / (0.12f * height), 2));
                var weights = closest.Select(item => MathF.Exp(-MathF.Pow(item.Distance / spread, 2))).ToArray();
                var total = weights.Sum();
                _sources[index] = closest.Select((item, i) => (item.Vertex, weights[i] / total * fade)).ToArray();
            }
        }

        /// <summary>Replaces each follower's own offset by the blend of its own and its body neighbours' offsets.</summary>
        public void Apply(float[] offsets)
        {
            var source = (float[])offsets.Clone();
            for (var index = 0; index < _followers.Length; index++)
            {
                var follow = _followers[index];
                if (follow <= 0.01f || _sources[index] is not { } sources) continue;
                var moved = Vector3.Zero;
                foreach (var (vertex, weight) in sources)
                    moved += new Vector3(source[vertex * 3], source[vertex * 3 + 1], source[vertex * 3 + 2]) * weight;
                var own = new Vector3(source[index * 3], source[index * 3 + 1], source[index * 3 + 2]);
                var blended = own * (1 - follow) + moved * follow;
                offsets[index * 3] = blended.X;
                offsets[index * 3 + 1] = blended.Y;
                offsets[index * 3 + 2] = blended.Z;
            }
        }
    }

    /// <summary>Computes each area's offsets for one mesh. Arkive's GLBs face +X with +Y up; the left side is -Z.</summary>
    private sealed class Shaper(BodyProfile profile, IReadOnlyDictionary<string, Vector3> bones, Vertex[] vertices)
    {
        public float Height { get; } = bones.TryGetValue("bip001-head", out var head) ? head.Y : 1f;

        private readonly bool _breastBones = bones.ContainsKey("b_l_breast_00") && bones.ContainsKey("b_r_breast_00");

        public float[]? Offsets(string zone)
        {
            var offsets = new float[vertices.Length * 3];
            var moved = false;
            for (var index = 0; index < vertices.Length; index++)
            {
                var offset = Offset(zone, vertices[index]);
                if (offset == Vector3.Zero) continue;
                offsets[index * 3] = offset.X;
                offsets[index * 3 + 1] = offset.Y;
                offsets[index * 3 + 2] = offset.Z;
                moved = true;
            }
            return moved ? offsets : null;
        }

        private Vector3 Offset(string zone, Vertex vertex) => zone switch
        {
            "neck" => Girth(vertex, "bip001-neck", "bip001-head", ["bip001-neck", "b_necktwist_01", "b_neck_01"]),
            "shoulders" => Sides(side => Bulge(vertex, Bone($"bip001-{side}-upperarm"), 0.07f,
                [$"bip001-{side}-clavicle", $"bip001-{side}-upperarm", $"b_shoulder_{side}_01", $"b_upperarmtwist_{side}_01",
                 $"bone-{side}-upperarm-twist"])),
            "upperArms" => Sides(side => Girth(vertex, $"bip001-{side}-upperarm", $"bip001-{side}-forearm",
                [$"bip001-{side}-upperarm", $"b_upperarmtwist_{side}_01", $"b_upperarmtwist_{side}_02", $"b_shoulder_{side}_01",
                 $"bone-{side}-upperarm-twist"])),
            "forearms" => Sides(side => Girth(vertex, $"bip001-{side}-forearm", $"bip001-{side}-hand",
                [$"bip001-{side}-forearm", $"b_forearmtwist_{side}_01", $"b_forearmtwist_{side}_02", $"b_elbow_{side}_01",
                 $"bone-{side}-forearm-twist"])),
            "chest" => Chest(vertex, Shape.Size),
            "chestWidth" => Chest(vertex, Shape.Width),
            "chestProjection" => Chest(vertex, Shape.Projection),
            "chestHeight" => Chest(vertex, Shape.Height),
            "chestLift" => Chest(vertex, Shape.Lift),
            "chestSpacing" => Chest(vertex, Shape.Spacing),
            "chestSag" => Chest(vertex, Shape.Sag),
            "waist" => Waist(vertex, new Vector3(1, 0, 1)),
            "waistWidth" => Waist(vertex, Vector3.UnitZ),
            "waistDepth" => Waist(vertex, Vector3.UnitX),
            "belly" => Landmark(vertex, profile.Belly, null, depth: 1.2f, Shape.Size),
            "hips" => Hips(vertex),
            "buttocks" => Buttocks(vertex, Shape.Size),
            "buttocksWidth" => Buttocks(vertex, Shape.Width),
            "buttocksProjection" => Buttocks(vertex, Shape.Projection),
            "buttocksLift" => Buttocks(vertex, Shape.Lift),
            "buttocksSag" => Buttocks(vertex, Shape.Sag),
            "thighs" => Thighs(vertex, Vector3.One),
            "thighWidth" => Thighs(vertex, Vector3.UnitZ),
            "thighDepth" => Thighs(vertex, Vector3.UnitX),
            "calves" => Sides(side => Girth(vertex, $"bip001-{side}-calf", $"bip001-{side}-foot",
                [$"bip001-{side}-calf", $"b_calftwist_{side}_01", $"b_{side}_calf01"])),
            _ => Vector3.Zero,
        };

        /// <summary>
        /// What a landmark slider does: grow or shrink (all directions, or one), move the area, or let it hang lower.
        /// Every one of them is a fixed offset per vertex times the slider, so they add up freely.
        /// </summary>
        private enum Shape { Size, Width, Projection, Height, Lift, Spacing, Sag }

        private Vector3 Chest(Vertex vertex, Shape shape) =>
            Sides(side => _breastBones
                ? BreastBones(vertex, side, shape)
                : Landmark(vertex, profile.Chest, side, depth: profile.Female ? 1.5f : 1.1f, shape));

        private Vector3 Buttocks(Vertex vertex, Shape shape) =>
            Sides(side => Landmark(vertex, profile.Buttocks, side, depth: 1.3f, shape));

        private Vector3 Thighs(Vertex vertex, Vector3 axes) =>
            Sides(side => Girth(vertex, $"bip001-{side}-thigh", $"bip001-{side}-calf",
                [$"bip001-{side}-thigh", $"b_thightwist_{side}_01", $"b_hip_{side}_01"])) * axes;

        private Vector3 Bone(string name) => bones.TryGetValue(name, out var position) ? position : Vector3.Zero;

        private static Vector3 Sides(Func<string, Vector3> side) => side("l") + side("r");

        private static readonly string[] TorsoBones =
        [
            "bip001-pelvis", "bip001-spine", "bip001-spine1", "bip001-spine2", "bip001-l-clavicle", "bip001-r-clavicle",
            "bip001-l-thigh", "bip001-r-thigh", "b_thightwist_l_01", "b_thightwist_r_01", "b_hip_l_01", "b_hip_r_01",
            "b_l_breast_00", "b_l_breast_01", "b_l_breast_02", "b_r_breast_00", "b_r_breast_01", "b_r_breast_02",
        ];

        /// <summary>
        /// How much a vertex belongs to the trunk. Pieces of one outfit can weight the same spot differently (a top's
        /// hem to the spine, the trousers' waistband to the pelvis), so any clear trunk weight counts in full and
        /// trunk areas are shaped by place alone; that keeps overlapping pieces moving together.
        /// </summary>
        private static float TorsoWeight(Vertex vertex) => Smooth(Weight(vertex, TorsoBones), 0.05f, 0.5f);

        private static float Smooth(float value, float from, float to)
        {
            var t = Math.Clamp((value - from) / (to - from), 0f, 1f);
            return t * t * (3 - 2 * t);
        }

        private static float Weight(Vertex vertex, IReadOnlyList<string> zoneBones)
        {
            var weight = 0f;
            foreach (var (bone, value) in vertex.Influences)
                foreach (var zoneBone in zoneBones)
                    if (bone.Equals(zoneBone, StringComparison.OrdinalIgnoreCase)) { weight += value; break; }
            return weight;
        }

        /// <summary>Thicker or thinner around the line from one bone to the next (a limb or the neck).</summary>
        private Vector3 Girth(Vertex vertex, string from, string to, IReadOnlyList<string> zoneBones)
        {
            var weight = Weight(vertex, zoneBones);
            if (weight <= 0 || !bones.TryGetValue(from, out var start) || !bones.TryGetValue(to, out var end)) return Vector3.Zero;
            var axis = end - start;
            var t = Math.Clamp(Vector3.Dot(vertex.Position - start, axis) / axis.LengthSquared(), 0f, 1f);
            var nearest = start + axis * t;
            return (vertex.Position - nearest) * weight;
        }

        /// <summary>Swells a round area around a point, fading out over its radius (in body heights).</summary>
        private Vector3 Bulge(Vertex vertex, Vector3 centre, float radius, IReadOnlyList<string> zoneBones)
        {
            var weight = Weight(vertex, zoneBones);
            if (weight <= 0) return Vector3.Zero;
            var falloff = Falloff(vertex.Position - centre, radius * Height);
            return falloff < 0.002f ? Vector3.Zero : (vertex.Position - centre) * (weight * falloff);
        }

        /// <summary>
        /// Shapes the area around a landmark: swells it outwards from a point behind it, inside the body (all
        /// directions or one), moves it, or lets it hang (sag). The fade-out is measured across the body only, not in
        /// depth, so every layer stacked over the spot (skin, a top, a coat) gets the same share and outer layers move
        /// a little more than inner ones; clothing stays outside the body.
        /// </summary>
        private Vector3 Landmark(Vertex vertex, BodyLandmark landmark, string? side, float depth, Shape shape)
        {
            var sign = side switch { "l" => -1f, "r" => 1f, _ => 0f };
            var origin = Bone(landmark.Bone);
            var radius = (float)landmark.Radius * Height;
            var centre = origin + new Vector3((float)landmark.Forward, (float)landmark.Up, sign * (float)landmark.Side) * Height;
            var outwards = landmark.Forward >= 0 ? 1f : -1f;
            var across = vertex.Position - centre;
            across.X = 0;
            // The underside ends sooner than the top (the fold under the buttocks, the crease under the breasts),
            // so a skirt or top hanging below does not flare out with them.
            if (across.Y < 0) across.Y /= outwards < 0 ? 0.65f : 0.85f;
            // Only the facing side of the body: in front of the spine for the chest, behind the hips for the buttocks.
            var facing = Smooth((vertex.Position.X - origin.X) * outwards, 0, 0.6f * radius);
            var weight = TorsoWeight(vertex) * Falloff(across, radius) * facing;
            if (weight < 0.002f) return Vector3.Zero;
            var pivot = centre - new Vector3(outwards * depth * radius, 0, 0);
            return Shaped(vertex.Position, centre, pivot, outwards, sign, radius, depth, weight, shape);
        }

        /// <summary>
        /// Bodies with breast bones (Echidna): each breast is exactly what its bones move, so the chest and shoulders
        /// around it stay as they are. The root bone sits inside the chest and serves as the pivot.
        /// </summary>
        private Vector3 BreastBones(Vertex vertex, string side, Shape shape)
        {
            var root = Bone($"b_{side}_breast_00");
            var tip = bones.TryGetValue($"b_{side}_breast_02", out var end) ? end : Bone($"b_{side}_breast_01");
            var weight = Smooth(Weight(vertex, [$"b_{side}_breast_00", $"b_{side}_breast_01", $"b_{side}_breast_02"]), 0, 0.6f);
            if (weight < 0.002f) return Vector3.Zero;
            var radius = Math.Max(Vector3.Distance(root, tip), 0.02f * Height);
            var sign = side == "l" ? -1f : 1f;
            var depth = 1.0f;
            var centre = root + new Vector3(depth * radius, 0, 0);
            return Shaped(vertex.Position, centre, root, 1f, sign, radius, depth, weight, shape);
        }

        private static Vector3 Shaped(Vector3 position, Vector3 centre, Vector3 pivot, float outwards, float sign, float radius,
            float depth, float weight, Shape shape)
        {
            switch (shape)
            {
                case Shape.Size: return (position - pivot) * weight;
                case Shape.Width: return (position - pivot) * Vector3.UnitZ * weight;
                case Shape.Projection: return (position - pivot) * Vector3.UnitX * weight;
                case Shape.Height: return (position - pivot) * Vector3.UnitY * weight;
                // +100% moves the area by its radius.
                case Shape.Lift: return Vector3.UnitY * (radius * weight);
                case Shape.Spacing: return new Vector3(0, 0, sign * radius * weight);
                case Shape.Sag:
                {
                    // Hanging: the further a point stands out from the body, the further it drops, so the tip and lower
                    // curve sink while the base stays attached; the upper slope flattens a little.
                    var standOut = Math.Clamp((position.X - pivot.X) * outwards / (depth * radius), 0f, 1.25f);
                    var drop = 0.55f * radius * standOut * standOut;
                    var above = Smooth((position.Y - centre.Y) / radius, 0, 1);
                    var flatten = 0.25f * radius * standOut * above;
                    return new Vector3(-outwards * flatten, -drop, 0) * weight;
                }
                default: return Vector3.Zero;
            }
        }

        /// <summary>Narrower or wider at the waist, around the spine, fading out above and below.</summary>
        private Vector3 Waist(Vertex vertex, Vector3 axes)
        {
            var pelvis = Bone("bip001-pelvis");
            var chest = bones.TryGetValue("bip001-spine2", out var upper) ? upper : Bone("bip001-spine1");
            var height = pelvis.Y + (chest.Y - pelvis.Y) * 0.55f;
            var band = Band(vertex.Position.Y, height, 0.055f * Height);
            if (band < 0.002f) return Vector3.Zero;
            var axis = Bone("bip001-spine");
            var outward = new Vector3(vertex.Position.X - axis.X, 0, vertex.Position.Z - axis.Z) * axes;
            return outward * (TorsoWeight(vertex) * band);
        }

        /// <summary>Wider or narrower hips, side to side, around the pelvis height.</summary>
        private Vector3 Hips(Vertex vertex)
        {
            var pelvis = Bone("bip001-pelvis");
            var band = Band(vertex.Position.Y, pelvis.Y + 0.02f * Height, 0.085f * Height);
            if (band < 0.002f) return Vector3.Zero;
            return new Vector3(0, 0, (vertex.Position.Z - pelvis.Z) * TorsoWeight(vertex) * band);
        }

        private static float Falloff(Vector3 offset, float radius) => MathF.Exp(-offset.LengthSquared() / (radius * radius));

        private static float Band(float y, float centre, float spread) => MathF.Exp(-MathF.Pow((y - centre) / spread, 2));
    }

    [GeneratedRegex(@"__ARKIVE_\d+$")]
    private static partial Regex ArkiveSuffix();

    // Bones of hanging cloth: skirts, capes, coat tails, neck cloths, belt plates, sashes and ribbons.
    [GeneratedRegex(@"skirt|cape|capat|cloth|pelat|coat|robe|ribbon|mantle|cloak|duandai|sash", RegexOptions.IgnoreCase)]
    private static partial Regex LooseBone();

    [GeneratedRegex(@"hair", RegexOptions.IgnoreCase)]
    private static partial Regex HairBone();
}
