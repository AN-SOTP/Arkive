using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Arkive.Core.Proportions;

/// <summary>
/// How far each vertex of one glTF mesh (one LOD) moves per area, for a +100% setting. A setting of s percent
/// moves every vertex by s/100 times its offset; offsets of several areas add up.
/// </summary>
public sealed record BodyProportionMeshBasis(int MeshIndex, string MeshName, int VertexCount, IReadOnlyDictionary<string, float[]> Offsets);

/// <summary>The proportion offsets of one editable skeletal mesh GLB.</summary>
public sealed record BodyProportionModel(
    BodyProfile Profile,
    bool LooseClothingFollows,
    IReadOnlyList<BodyProportionMeshBasis> Meshes,
    int LooseVertexCount)
{
    /// <summary>The areas this mesh has vertices in; sliders for other areas change nothing on it.</summary>
    public IReadOnlyCollection<string> AffectedZones =>
        Meshes.SelectMany(mesh => mesh.Offsets.Keys).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// Reshapes a player mesh by moving its vertices in the skeleton's rest pose. Each body area is found from the
/// bones that move it (the same skin weights the game uses), so tight clothing and the body under it move
/// together and pieces of one outfit keep meeting at their seams. Bones never move, so animations still fit.
/// Nothing is made longer: the skeleton is the game's and lengths belong to it.
/// </summary>
public static partial class BodyProportionEngine
{
    public sealed class UnknownBodyException(string message) : Exception(message);

    /// <param name="objectName">The mesh's object name, which identifies tuned NPCs.</param>
    public static BodyProportionModel Build(byte[] glb, bool looseClothingFollows, BodyProfile? profile = null, string? objectName = null)
    {
        var container = GlbContainer.Parse(glb);
        var (bones, jointNames) = RestBones(container);
        profile ??= BodyProfiles.Detect(bones, objectName)
            ?? throw new UnknownBodyException("This mesh's skeleton is not one of the player bodies Arkive has tuned.");
        var meshes = new List<BodyProportionMeshBasis>();
        var seen = new HashSet<int>();
        var looseVertices = 0;
        var meshArray = container.Json["meshes"]?.AsArray() ?? [];
        for (var meshIndex = 0; meshIndex < meshArray.Count; meshIndex++)
        {
            var mesh = meshArray[meshIndex]!.AsObject();
            var attributes = mesh["primitives"]![0]!["attributes"]!.AsObject();
            if (attributes["POSITION"] is null || attributes["JOINTS_0"] is null || attributes["WEIGHTS_0"] is null) continue;
            var positionAccessor = (int)attributes["POSITION"]!;
            if (!seen.Add(positionAccessor)) continue;
            var vertices = ReadVertices(container, attributes, jointNames);
            var shaper = new Shaper(profile, bones, vertices, looseClothingFollows);
            looseVertices += vertices.Count(vertex => vertex.Loose > 0.01f);
            var offsets = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var zone in profile.Zones)
            {
                var offset = shaper.Offsets(zone.Id);
                if (offset is not null) offsets[zone.Id] = offset;
            }
            meshes.Add(new BodyProportionMeshBasis(meshIndex, (string?)mesh["name"] ?? $"mesh {meshIndex}", vertices.Length, offsets));
        }
        if (meshes.Count == 0) throw new InvalidDataException("The GLB has no skinned meshes to reshape.");
        return new BodyProportionModel(profile, looseClothingFollows, meshes, looseVertices);
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
            for (var vertex = 0; vertex < view.Count; vertex++)
            {
                var position = new Vector3(container.ReadFloat(view, vertex, 0), container.ReadFloat(view, vertex, 1),
                    container.ReadFloat(view, vertex, 2));
                foreach (var (offsets, scale) in active)
                    position += new Vector3(offsets[vertex * 3], offsets[vertex * 3 + 1], offsets[vertex * 3 + 2]) * scale;
                container.WriteFloat(view, vertex, 0, position.X);
                container.WriteFloat(view, vertex, 1, position.Y);
                container.WriteFloat(view, vertex, 2, position.Z);
                minimum = Vector3.Min(minimum, position);
                maximum = Vector3.Max(maximum, position);
            }
            view.Accessor["min"] = new JsonArray(minimum.X, minimum.Y, minimum.Z);
            view.Accessor["max"] = new JsonArray(maximum.X, maximum.Y, maximum.Z);
        }
        return container.ToBytes();
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

    private sealed record Vertex(Vector3 Position, (string Bone, float Weight)[] Influences, float Loose);

    private static Vertex[] ReadVertices(GlbContainer container, JsonObject attributes, string[] jointNames)
    {
        var positions = container.Accessor((int)attributes["POSITION"]!);
        var joints = container.Accessor((int)attributes["JOINTS_0"]!);
        var weights = container.Accessor((int)attributes["WEIGHTS_0"]!);
        var vertices = new Vertex[positions.Count];
        for (var vertex = 0; vertex < vertices.Length; vertex++)
        {
            var influences = new List<(string, float)>(4);
            var loose = 0f;
            for (var slot = 0; slot < 4; slot++)
            {
                var weight = container.ReadWeight(weights, vertex, slot);
                if (weight <= 0) continue;
                var joint = container.ReadInt(joints, vertex, slot);
                if (joint >= jointNames.Length) continue;
                var bone = jointNames[joint];
                influences.Add((bone, weight));
                if (LooseBone().IsMatch(bone)) loose += weight;
            }
            vertices[vertex] = new Vertex(new Vector3(container.ReadFloat(positions, vertex, 0),
                container.ReadFloat(positions, vertex, 1), container.ReadFloat(positions, vertex, 2)), influences.ToArray(), loose);
        }
        return vertices;
    }

    /// <summary>Computes each area's offsets for one mesh. Arkive's GLBs face +X with +Y up; the left side is -Z.</summary>
    private sealed class Shaper(BodyProfile profile, IReadOnlyDictionary<string, Vector3> bones, Vertex[] vertices, bool loose)
    {
        private readonly float _height = bones.TryGetValue("bip001-head", out var head) ? head.Y : 1f;

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
            "shoulders" => Sides(side => Bulge(vertex, Bone($"bip001-{side}-upperarm"), 0.07f, Bone($"bip001-{side}-upperarm"),
                [$"bip001-{side}-clavicle", $"bip001-{side}-upperarm", $"b_shoulder_{side}_01", $"b_upperarmtwist_{side}_01"])),
            "upperArms" => Sides(side => Girth(vertex, $"bip001-{side}-upperarm", $"bip001-{side}-forearm",
                [$"bip001-{side}-upperarm", $"b_upperarmtwist_{side}_01", $"b_upperarmtwist_{side}_02", $"b_shoulder_{side}_01"])),
            "forearms" => Sides(side => Girth(vertex, $"bip001-{side}-forearm", $"bip001-{side}-hand",
                [$"bip001-{side}-forearm", $"b_forearmtwist_{side}_01", $"b_forearmtwist_{side}_02", $"b_elbow_{side}_01"])),
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
            "buttocks" => Buttocks(vertex, Shape.Size) + LooseRing(vertex,
                Bone("bip001-pelvis").Y + (float)profile.Buttocks.Up * _height, 0.07f, 0.04f, backOnly: true),
            "buttocksWidth" => Buttocks(vertex, Shape.Width),
            "buttocksProjection" => Buttocks(vertex, Shape.Projection),
            "buttocksLift" => Buttocks(vertex, Shape.Lift),
            "buttocksSag" => Buttocks(vertex, Shape.Sag),
            "thighs" => Thighs(vertex, Vector3.One) + LooseRing(vertex, Bone("bip001-l-thigh").Y - 0.11f * _height, 0.1f, 0.04f),
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
            Sides(side => Landmark(vertex, profile.Chest, side, depth: profile.Female ? 1.5f : 1.1f, shape));

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
            // Some NPCs (Echidna) have breast bones of their own.
            "b_l_breast_00", "b_l_breast_01", "b_l_breast_02", "b_r_breast_00", "b_r_breast_01", "b_r_breast_02",
        ];

        /// <summary>
        /// How much a vertex belongs to the trunk. Pieces of one outfit can weight the same spot differently (a top's
        /// hem to the spine, the trousers' waistband to the pelvis), so any clear trunk weight counts in full and
        /// trunk areas are shaped by place alone; that keeps overlapping pieces moving together.
        /// </summary>
        private float TorsoWeight(Vertex vertex)
        {
            var weight = Weight(vertex, TorsoBones);
            var t = Math.Clamp((weight - 0.05f) / 0.45f, 0f, 1f);
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

        /// <summary>Swells a round area around a centre, fading out over its radius (in body heights).</summary>
        private Vector3 Bulge(Vertex vertex, Vector3 centre, float radius, Vector3 pivot, IReadOnlyList<string>? zoneBones)
        {
            var weight = zoneBones is null ? TorsoWeight(vertex) : Weight(vertex, zoneBones);
            if (weight <= 0) return Vector3.Zero;
            var falloff = Falloff(vertex.Position - centre, radius * _height);
            return falloff < 0.002f ? Vector3.Zero : (vertex.Position - pivot) * (weight * falloff);
        }

        /// <summary>
        /// Shapes the area around a landmark: swells it outwards from a point behind it, inside the body (all
        /// directions or one), moves it, or swings it down around its top edge (sag).
        /// </summary>
        private Vector3 Landmark(Vertex vertex, BodyLandmark landmark, string? side, float depth, Shape shape)
        {
            var sign = side switch { "l" => -1f, "r" => 1f, _ => 0f };
            var origin = Bone(landmark.Bone);
            var radius = (float)landmark.Radius * _height;
            var centre = origin + new Vector3((float)landmark.Forward, (float)landmark.Up, sign * (float)landmark.Side) * _height;
            var outwards = landmark.Forward >= 0 ? 1f : -1f;
            var pivot = centre - new Vector3(outwards * depth * radius, 0, 0);
            var weight = TorsoWeight(vertex) * Falloff(vertex.Position - centre, radius);
            if (weight < 0.002f) return Vector3.Zero;
            switch (shape)
            {
                case Shape.Size: return (vertex.Position - pivot) * weight;
                case Shape.Width: return (vertex.Position - pivot) * Vector3.UnitZ * weight;
                case Shape.Projection: return (vertex.Position - pivot) * Vector3.UnitX * weight;
                case Shape.Height: return (vertex.Position - pivot) * Vector3.UnitY * weight;
                // +100% moves the area by its radius.
                case Shape.Lift: return Vector3.UnitY * (radius * weight);
                case Shape.Spacing: return new Vector3(0, 0, sign * radius * weight);
                case Shape.Sag:
                    // Swings the area about a hinge at its top edge (+100% is about 35 degrees), so the lower part drops most.
                    var hinge = centre + new Vector3(-outwards * radius * 0.6f, radius * 0.9f, 0);
                    var arm = vertex.Position - hinge;
                    // Only what hangs below the hinge swings; the skin above it stays put.
                    var below = arm.Y >= 0 ? MathF.Exp(-MathF.Pow(arm.Y / (0.3f * radius), 2)) : 1f;
                    var turn = new Vector3(outwards * arm.Y, -outwards * arm.X, 0);
                    return (turn * 0.6f - Vector3.UnitY * (0.15f * radius)) * (weight * below);
                default: return Vector3.Zero;
            }
        }

        /// <summary>Narrower or wider at the waist, around the spine, fading out above and below.</summary>
        private Vector3 Waist(Vertex vertex, Vector3 axes)
        {
            var pelvis = Bone("bip001-pelvis");
            var chest = bones.TryGetValue("bip001-spine2", out var upper) ? upper : Bone("bip001-spine1");
            var height = pelvis.Y + (chest.Y - pelvis.Y) * 0.55f;
            var band = Band(vertex.Position.Y, height, 0.055f * _height);
            if (band < 0.002f) return Vector3.Zero;
            var axis = Bone("bip001-spine");
            var outward = new Vector3(vertex.Position.X - axis.X, 0, vertex.Position.Z - axis.Z) * axes;
            return outward * (TorsoWeight(vertex) * band) + LooseRing(vertex, height, 0.055f, 0.05f) * axes;
        }

        /// <summary>Wider or narrower hips, side to side, around the pelvis height.</summary>
        private Vector3 Hips(Vertex vertex)
        {
            var pelvis = Bone("bip001-pelvis");
            var band = Band(vertex.Position.Y, pelvis.Y + 0.02f * _height, 0.085f * _height);
            if (band < 0.002f) return Vector3.Zero;
            return new Vector3(0, 0, (vertex.Position.Z - pelvis.Z) * TorsoWeight(vertex) * band) +
                   LooseRing(vertex, pelvis.Y + 0.02f * _height, 0.085f, 0.06f, sideways: true);
        }

        /// <summary>
        /// With "loose clothing follows", skirts and coat tails (which hang from their own bones) move out from the
        /// body's centre line by about as much as the body under them grows (<paramref name="reach"/>, in body
        /// heights at +100%), however wide the skirt is, instead of staying put.
        /// </summary>
        private Vector3 LooseRing(Vertex vertex, float height, float spread, float reach, bool backOnly = false, bool sideways = false)
        {
            if (!loose || vertex.Loose <= 0) return Vector3.Zero;
            var pelvis = Bone("bip001-pelvis");
            if (backOnly && vertex.Position.X > pelvis.X) return Vector3.Zero;
            var band = Band(vertex.Position.Y, height, spread * _height);
            var outward = new Vector3(sideways ? 0 : vertex.Position.X - pelvis.X, 0, vertex.Position.Z - pelvis.Z);
            var length = outward.Length();
            if (length < 1e-4f) return Vector3.Zero;
            // Cloth near the centre line moves less, so a skirt's front and back stay where they were.
            var share = Math.Min(1f, length / (0.08f * _height));
            return outward / length * (reach * _height * vertex.Loose * band * share);
        }

        private static float Falloff(Vector3 offset, float radius) => MathF.Exp(-offset.LengthSquared() / (radius * radius));

        private static float Band(float y, float centre, float spread) => MathF.Exp(-MathF.Pow((y - centre) / spread, 2));
    }

    [GeneratedRegex(@"__ARKIVE_\d+$")]
    private static partial Regex ArkiveSuffix();

    // Bones of hanging cloth: skirts, capes, coat tails, neck cloths and belt plates.
    [GeneratedRegex(@"skirt|cape|capat|cloth|pelat|coat|robe|ribbon|mantle|cloak", RegexOptions.IgnoreCase)]
    private static partial Regex LooseBone();
}
