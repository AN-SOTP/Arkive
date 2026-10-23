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
        var result = new OutfitSwapMesh
        {
            Positions = positions.ToArray(), Normals = normals.ToArray(), Tangents = tangents.ToArray(), Uv0 = uv0.ToArray(), Uv1 = uv1.ToArray(),
            Colours = colours.ToArray(), Joints = joints.ToArray(), Weights = weights.ToArray(), Sections = sections, Materials = materials,
            BoneNames = mesh.BoneNames, BonePositions = mesh.BonePositions, BoneParents = mesh.BoneParents,
            OwnPictureSkin = mesh.OwnPictureSkin, NativeSkin = native,
        };
        return (result, positions.ToArray());
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
