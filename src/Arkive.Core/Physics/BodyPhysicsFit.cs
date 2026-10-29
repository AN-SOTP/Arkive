using System.Numerics;
using System.Text.Json.Serialization;
using Arkive.Core.Proportions;

namespace Arkive.Core.Physics;

/// <summary>
/// How one body shape changes to match a reshaped body: its radius grows by <see cref="RadiusChange"/> and its centre
/// moves by <see cref="CentreShift"/> (bone space, centimetres). Named by bone, kind and place, so it can be written
/// again into the game's asset after an update moves its bytes.
/// </summary>
public sealed record BodyPhysicsShapeChange(
    string Bone,
    PhysicsShapeKind Kind,
    int Element,
    float RadiusChange,
    float ShiftX,
    float ShiftY,
    float ShiftZ,
    int Vertices)
{
    [JsonIgnore]
    public Vector3 CentreShift => new(ShiftX, ShiftY, ShiftZ);
}

/// <summary>The body shapes of one physics asset matched to a Simple upscale's new proportions.</summary>
public sealed record BodyPhysicsFit(IReadOnlyList<BodyPhysicsShapeChange> Shapes)
{
    public bool IsEmpty => Shapes.Count == 0;
}

/// <summary>
/// Fits a physics asset's body capsules (pelvis, spine, chest, thighs, arms…) to reshaped outfits, so hair, skirts and
/// capes rest on the new body instead of sinking into it. Every body vertex of every piece added is put on the
/// capsule of the bone it hangs from most; how far the reshape pushed it out from that capsule's axis is fitted, by
/// least squares over all of them, as a grown radius plus a moved centre (a body made fuller to the back moves its
/// capsule back). Hanging cloth and hair are left out: they are what collides, not what is collided with. Safe to
/// feed from several threads.
/// </summary>
public sealed class BodyPhysicsFitter
{
    // Vertices further than this from a capsule's surface (cm) belong to something else (an armour plate, a wing).
    private const float MaximumSurfaceDistance = 6f;
    private const int MinimumVertices = 24;

    private readonly IReadOnlyList<PhysicsShape> _shapes;
    private readonly Dictionary<string, PhysicsShape[]> _byBone;
    private readonly Accumulator[] _sums;
    private readonly Lock _lock = new();

    public BodyPhysicsFitter(PhysicsAssetReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        _shapes = report.Shapes.Where(shape => shape.Fixed && shape.Kind == PhysicsShapeKind.Capsule &&
                                               shape.Bone.StartsWith("bip001", StringComparison.OrdinalIgnoreCase)).ToArray();
        _byBone = _shapes.GroupBy(shape => shape.Bone, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        _sums = _shapes.Select(_ => new Accumulator()).ToArray();
    }

    public int VerticesUsed { get; private set; }

    /// <summary>Adds one piece: its editable GLB before and after the reshape (the same mesh with moved vertices).</summary>
    public void Add(byte[] before, byte[] after)
    {
        var original = GlbContainer.Parse(before);
        var reshaped = GlbContainer.Parse(after);
        var skin = original.Json["skins"]?[0]?.AsObject();
        if (skin is null) return;
        var nodes = original.Json["nodes"]!.AsArray();
        var joints = skin["joints"]!.AsArray()
            .Select(node => BodyProportionEngine.BoneName((string?)nodes[(int)node!]!["name"] ?? "")).ToArray();
        var inverseBinds = original.Accessor((int)skin["inverseBindMatrices"]!);
        // Bone space (the game's axes, cm) of each joint with capsules: glTF is the game's (x, z, y) in metres.
        var toBone = new Matrix4x4?[joints.Length];
        for (var joint = 0; joint < joints.Length; joint++)
        {
            if (!_byBone.ContainsKey(joints[joint])) continue;
            var m = new Matrix4x4(
                original.ReadFloat(inverseBinds, joint, 0), original.ReadFloat(inverseBinds, joint, 1), original.ReadFloat(inverseBinds, joint, 2), original.ReadFloat(inverseBinds, joint, 3),
                original.ReadFloat(inverseBinds, joint, 4), original.ReadFloat(inverseBinds, joint, 5), original.ReadFloat(inverseBinds, joint, 6), original.ReadFloat(inverseBinds, joint, 7),
                original.ReadFloat(inverseBinds, joint, 8), original.ReadFloat(inverseBinds, joint, 9), original.ReadFloat(inverseBinds, joint, 10), original.ReadFloat(inverseBinds, joint, 11),
                original.ReadFloat(inverseBinds, joint, 12), original.ReadFloat(inverseBinds, joint, 13), original.ReadFloat(inverseBinds, joint, 14), original.ReadFloat(inverseBinds, joint, 15));
            toBone[joint] = m;
        }
        // The first skinned mesh is LOD 0; the smaller LODs repeat the same surface.
        var meshes = original.Json["meshes"]?.AsArray() ?? [];
        var reshapedMeshes = reshaped.Json["meshes"]?.AsArray() ?? [];
        for (var meshIndex = 0; meshIndex < meshes.Count && meshIndex < reshapedMeshes.Count; meshIndex++)
        {
            var attributes = meshes[meshIndex]!["primitives"]![0]!["attributes"]!.AsObject();
            var after2 = reshapedMeshes[meshIndex]!["primitives"]![0]!["attributes"]!.AsObject();
            if (attributes["POSITION"] is null || attributes["JOINTS_0"] is null || attributes["WEIGHTS_0"] is null) continue;
            var positions = original.Accessor((int)attributes["POSITION"]!);
            var moved = reshaped.Accessor((int)after2["POSITION"]!);
            if (moved.Count != positions.Count) throw new InvalidDataException("The reshaped piece no longer matches the original.");
            AddMesh(original, reshaped, positions, moved, original.Accessor((int)attributes["JOINTS_0"]!),
                original.Accessor((int)attributes["WEIGHTS_0"]!), joints, toBone);
            break;
        }
    }

    private void AddMesh(GlbContainer original, GlbContainer reshaped, AccessorView positions, AccessorView moved, AccessorView jointView,
        AccessorView weightView, string[] joints, Matrix4x4?[] toBone)
    {
        var local = _shapes.Select(_ => new Accumulator()).ToArray();
        var used = 0;
        var shapeIndex = _shapes.Select((shape, index) => (shape, index)).ToDictionary(item => item.shape, item => item.index);
        for (var vertex = 0; vertex < positions.Count; vertex++)
        {
            var before = Read(original, positions, vertex);
            var after = Read(reshaped, moved, vertex);
            // The bone the vertex hangs from most that has capsules; hanging cloth and hair are not the body.
            int best = -1;
            float bestWeight = 0, hanging = 0;
            for (var slot = 0; slot < 4; slot++)
            {
                var weight = original.ReadWeight(weightView, vertex, slot);
                if (weight <= 0) continue;
                var joint = original.ReadInt(jointView, vertex, slot);
                if (joint >= joints.Length) continue;
                if (BodyProportionEngine.IsHangingBone(joints[joint])) hanging += weight;
                else if (toBone[joint] is not null && weight > bestWeight) (best, bestWeight) = (joint, weight);
            }
            if (best < 0 || hanging > 0.05f) continue;
            var transform = toBone[best]!.Value;
            var from = ToBone(before, transform);
            var to = ToBone(after, transform);
            // The capsule of that bone whose surface the vertex lies closest to.
            PhysicsShape? shape = null;
            float gap = float.MaxValue;
            Vector3 normal = default;
            foreach (var candidate in _byBone[joints[best]])
            {
                if (!Matrix4x4.Invert(candidate.Transform, out var toShape)) continue;
                var point = Vector3.Transform(from, toShape);
                var half = candidate.Length / 2;
                var radial = point - new Vector3(0, 0, Math.Clamp(point.Z, -half, half));
                var distance = radial.Length();
                if (distance < 1e-3f || Math.Abs(distance - candidate.Radius) >= gap) continue;
                (shape, gap, normal) = (candidate, Math.Abs(distance - candidate.Radius), radial / distance);
            }
            if (shape is null || gap > MaximumSurfaceDistance) continue;
            Matrix4x4.Invert(shape.Transform, out var shapeFrame);
            var push = Vector3.Dot(Vector3.TransformNormal(to - from, shapeFrame), normal);
            local[shapeIndex[shape]].Add(normal, push, bestWeight);
            used++;
        }
        lock (_lock)
        {
            for (var index = 0; index < local.Length; index++) _sums[index].Merge(local[index]);
            VerticesUsed += used;
        }
    }

    /// <summary>The fitted change of every capsule enough body vertices moved; capsules nothing moved are left out.</summary>
    public BodyPhysicsFit Result()
    {
        var changes = new List<BodyPhysicsShapeChange>();
        lock (_lock)
        {
            for (var index = 0; index < _shapes.Count; index++)
            {
                var shape = _shapes[index];
                var sums = _sums[index];
                if (sums.Count < MinimumVertices || sums.Solve() is not { } solved) continue;
                var (grow, shift) = solved;
                // A capsule never shrinks below half its radius, nor moves further than its radius.
                grow = Math.Clamp(grow, -shape.Radius * 0.5f, shape.Radius * 1.5f);
                if (shift.Length() > shape.Radius) shift = Vector3.Normalize(shift) * shape.Radius;
                var boneShift = Vector3.TransformNormal(shift, shape.Transform);
                if (Math.Abs(grow) < 0.05f && boneShift.Length() < 0.05f) continue;
                changes.Add(new BodyPhysicsShapeChange(shape.Bone, shape.Kind, shape.Element, grow, boneShift.X, boneShift.Y, boneShift.Z,
                    sums.Count));
            }
        }
        return new BodyPhysicsFit(changes);
    }

    private static Vector3 Read(GlbContainer container, AccessorView view, int vertex) =>
        new(container.ReadFloat(view, vertex, 0), container.ReadFloat(view, vertex, 1), container.ReadFloat(view, vertex, 2));

    private static Vector3 ToBone(Vector3 position, Matrix4x4 inverseBind)
    {
        var local = Vector3.Transform(position, inverseBind);
        return new Vector3(local.X, local.Z, local.Y) * 100f;
    }

    /// <summary>
    /// Least-squares sums for push ≈ grow + shift · normal over a capsule's vertices, each weighted by how much its
    /// bone holds it, with a little damping so a capsule seen from one side only is not thrown along its axis.
    /// </summary>
    private sealed class Accumulator
    {
        private readonly double[,] _matrix = new double[4, 4];
        private readonly double[] _vector = new double[4];
        private double _weight;

        public int Count { get; private set; }

        public void Add(Vector3 normal, float push, float weight)
        {
            Span<double> row = [1, normal.X, normal.Y, normal.Z];
            for (var i = 0; i < 4; i++)
            {
                for (var j = 0; j < 4; j++) _matrix[i, j] += weight * row[i] * row[j];
                _vector[i] += weight * row[i] * push;
            }
            _weight += weight;
            Count++;
        }

        public void Merge(Accumulator other)
        {
            for (var i = 0; i < 4; i++)
            {
                for (var j = 0; j < 4; j++) _matrix[i, j] += other._matrix[i, j];
                _vector[i] += other._vector[i];
            }
            _weight += other._weight;
            Count += other.Count;
        }

        public (float Grow, Vector3 Shift)? Solve()
        {
            var a = (double[,])_matrix.Clone();
            var b = (double[])_vector.Clone();
            for (var i = 1; i < 4; i++) a[i, i] += 0.05 * _weight;
            // Gaussian elimination with partial pivoting on the 4x4 normal equations.
            for (var column = 0; column < 4; column++)
            {
                var pivot = column;
                for (var row = column + 1; row < 4; row++)
                    if (Math.Abs(a[row, column]) > Math.Abs(a[pivot, column])) pivot = row;
                if (Math.Abs(a[pivot, column]) < 1e-9) return null;
                if (pivot != column)
                {
                    for (var k = 0; k < 4; k++) (a[column, k], a[pivot, k]) = (a[pivot, k], a[column, k]);
                    (b[column], b[pivot]) = (b[pivot], b[column]);
                }
                for (var row = column + 1; row < 4; row++)
                {
                    var factor = a[row, column] / a[column, column];
                    for (var k = column; k < 4; k++) a[row, k] -= factor * a[column, k];
                    b[row] -= factor * b[column];
                }
            }
            var x = new double[4];
            for (var row = 3; row >= 0; row--)
            {
                var sum = b[row];
                for (var k = row + 1; k < 4; k++) sum -= a[row, k] * x[k];
                x[row] = sum / a[row, row];
            }
            return ((float)x[0], new Vector3((float)x[1], (float)x[2], (float)x[3]));
        }
    }
}
