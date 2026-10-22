using System.Numerics;
using System.Text.RegularExpressions;
using Arkive.Core.Packages;
using Arkive.Core.Meshes;

namespace Arkive.Core.Outfits;

/// <summary>A material as the viewer read it: which pictures and values it uses and how it is drawn.</summary>
public sealed record OutfitSwapMaterial(
    int Slot,
    string Name,
    bool Translucent,
    IReadOnlyDictionary<string, string> Textures,
    IReadOnlyDictionary<string, float[]> Vectors,
    IReadOnlyDictionary<string, float> Scalars);

/// <summary>An outfit piece taking part in a swap.</summary>
public sealed record OutfitSwapPiece(string ObjectName, OutfitSwapMesh Mesh, IReadOnlyList<OutfitSwapMaterial> Materials);

/// <summary>Donor pictures that share one target picture, each in its own square of it.</summary>
public sealed record OutfitSwapCanvas(string Key, IReadOnlyList<(int Piece, int Material)> Tiles)
{
    public int Grid => Tiles.Count == 1 ? 1 : Tiles.Count <= 4 ? 2 : 4;

    /// <summary>Where a tile sits in the picture, as a UV rectangle (u0, v0, u1, v1); null for a picture of its own.</summary>
    public Vector4? Rect(int piece, int material)
    {
        if (Tiles.Count == 1) return null;
        var index = Tiles.ToList().IndexOf((piece, material));
        var n = Grid;
        return new Vector4((index % n) / (float)n, (index / n) / (float)n, (index % n + 1) / (float)n, (index / n + 1) / (float)n);
    }
}

/// <summary>A target piece's new geometry and what it says about the swap.</summary>
public sealed record OutfitSwapPieceResult(string ObjectName, OutfitSwapMesh Mesh, bool Hidden);

public sealed record OutfitSwapPlan(
    IReadOnlyList<OutfitSwapPieceResult> Pieces,
    IReadOnlyList<OutfitSwapCanvas> Canvases,
    IReadOnlyList<string> Notes);

/// <summary>
/// Decides which target piece and material slot draws each part of the donor outfit, and builds the target pieces' new
/// geometry. Only the target's existing materials and skeleton are used; nothing new is added to the game files.
/// </summary>
public static partial class OutfitSwapPlanner
{
    [GeneratedRegex(@"thigh|calf|foot|toe|skirt|hip|knee|leg", RegexOptions.IgnoreCase)]
    private static partial Regex LegBone();

    [GeneratedRegex(@"^(.*)_(\d+)$")]
    private static partial Regex ChainLink();

    private sealed record Slot(int Piece, OutfitSwapMaterial Material, string Key, bool Skin, bool LowerSkin, bool Own);

    /// <param name="ownMaterials">When given, the outfit keeps its own materials: each donor material is drawn as the game draws
    /// it (the replaced pieces take its place in one of their slots, or gain a slot), and nothing is repainted.</param>
    public static OutfitSwapPlan Plan(
        IReadOnlyList<OutfitSwapPiece> donors,
        IReadOnlyList<Vector3[]> fitted,
        IReadOnlyList<OutfitSwapPiece> targets,
        OutfitSwapBody? targetBody,
        OutfitSwapMaterialSources? ownMaterials = null)
    {
        var notes = new List<string>();
        // Which target piece draws which donor triangles.
        var assign = targets.Select(_ => new List<(int Piece, int Section, int[] Triangles)>()).ToArray();
        var sameParts = donors.Count == targets.Count && donors.Zip(targets).All(pair =>
            OutfitSwapBodies.Part(pair.First.ObjectName) == OutfitSwapBodies.Part(pair.Second.ObjectName));
        for (var k = 0; k < donors.Count; k++)
        {
            var mesh = donors[k].Mesh;
            for (var s = 0; s < mesh.Sections.Count; s++)
            {
                if (sameParts || targets.Count == 1) { assign[sameParts ? k : 0].Add((k, s, mesh.Sections[s].Triangles)); continue; }
                foreach (var (lower, triangles) in SplitByLegs(mesh, mesh.Sections[s]))
                {
                    var target = Enumerable.Range(0, targets.Count).FirstOrDefault(t => (OutfitSwapBodies.Part(targets[t].ObjectName) == "lower") == lower);
                    assign[target].Add((k, s, triangles));
                }
            }
        }

        if (ownMaterials is not null) return PlanOwnMaterials(donors, fitted, targets, targetBody, assign, ownMaterials, notes);

        var slots = new List<Slot>();
        for (var t = 0; t < targets.Count; t++)
            foreach (var material in targets[t].Materials)
            {
                var colour = Texture(material, Role.Colour);
                slots.Add(new Slot(t, material, (colour ?? material.Name).ToLowerInvariant(), OutfitSwapBodies.IsSkin(material.Name),
                    OutfitSwapBodies.IsLowerSkin(material.Name), colour is not null && OutfitSwapBodies.OwnsPicture(targets[t].ObjectName, colour)));
            }

        // A piece with no picture of its own to repaint hands its part to one that has, and is hidden.
        var usable = Enumerable.Range(0, targets.Count).Where(t => slots.Any(slot => slot.Piece == t && !slot.Skin && slot.Own)).ToArray();
        if (usable.Length == 0)
            throw new InvalidDataException("None of the outfit you are replacing has pictures of its own that Arkive can repaint.");
        var hidden = new bool[targets.Count];
        for (var t = 0; t < targets.Count; t++)
        {
            if (usable.Contains(t)) continue;
            hidden[t] = true;
            assign[usable[0]].AddRange(assign[t]);
            assign[t].Clear();
            notes.Add($"{targets[t].ObjectName} only uses pictures shared with the whole game, so its part moves to {targets[usable[0]].ObjectName} and it is hidden.");
        }

        // Pictures: each donor material gets a square of a target picture; biggest first, so they get pictures of their own.
        var canvases = new Dictionary<string, List<(int Piece, int Material)>>(StringComparer.Ordinal);
        var where = new Dictionary<(int Target, int Piece, int Material), (Slot Slot, string? Canvas)>();
        var sizes = new Dictionary<(int Target, int Piece, int Material), int>();
        for (var t = 0; t < targets.Count; t++)
            foreach (var (piece, section, triangles) in assign[t])
            {
                var key = (t, piece, donors[piece].Mesh.Sections[section].Material);
                sizes[key] = sizes.GetValueOrDefault(key) + triangles.Length;
            }
        foreach (var ((t, piece, material), _) in sizes.OrderByDescending(pair => pair.Value))
        {
            var donorMaterial = donors[piece].Mesh.Materials[material];
            if (OutfitSwapBodies.IsSkin(donorMaterial))
            {
                var skin = slots.FirstOrDefault(slot => slot.Piece == t && slot.Skin && slot.LowerSkin == OutfitSwapBodies.IsLowerSkin(donorMaterial));
                if (skin is not null) { where[(t, piece, material)] = (skin, null); continue; }
            }
            var translucent = donors[piece].Materials.FirstOrDefault(m => m.Slot == material)?.Translucent ?? false;
            var options = slots.Where(slot => slot.Piece == t && !slot.Skin && slot.Own).Select(slot => slot.Key).Distinct().ToArray();
            var tile = (piece, material);
            var canvas = options.FirstOrDefault(option => canvases.TryGetValue(option, out var tiles) && tiles.Contains(tile));
            if (canvas is null)
            {
                // A see-through slot for see-through cloth and a solid one for solid cloth, then the emptiest picture.
                canvas = options.OrderBy(option => !slots.Any(slot => slot.Piece == t && slot.Key == option && slot.Material.Translucent == translucent))
                    .ThenBy(option => canvases.TryGetValue(option, out var tiles) ? tiles.Count : 0).First();
                if (!canvases.TryGetValue(canvas, out var list)) canvases[canvas] = list = [];
                if (list.Count >= 16) throw new InvalidDataException("The outfit has too many materials to fit into the one it replaces.");
                list.Add(tile);
            }
            var candidates = slots.Where(slot => slot.Piece == t && slot.Key == canvas && !slot.Skin && slot.Own).ToArray();
            where[(t, piece, material)] = (candidates.FirstOrDefault(slot => slot.Material.Translucent == translucent) ?? candidates[0], canvas);
        }
        var finished = canvases.Select(pair => new OutfitSwapCanvas(pair.Key, pair.Value)).ToArray();
        var byKey = finished.ToDictionary(canvas => canvas.Key, StringComparer.Ordinal);

        var results = new List<OutfitSwapPieceResult>();
        for (var t = 0; t < targets.Count; t++)
        {
            var mesh = hidden[t] || assign[t].Count == 0
                ? HiddenMesh(targets[t].Mesh)
                : Build(donors, fitted, targets[t].Mesh, assign[t], (piece, material) =>
                {
                    var (slot, canvas) = where[(t, piece, material)];
                    return (slot.Material.Slot, canvas is null ? null : byKey[canvas].Rect(piece, material), slot.Skin ? slot.LowerSkin : null);
                }, lower => slots.FirstOrDefault(slot => slot.Piece == t && slot.Skin && slot.LowerSkin == lower)?.Material.Slot, targetBody);
            results.Add(new OutfitSwapPieceResult(targets[t].ObjectName, mesh, hidden[t] || assign[t].Count == 0));
        }
        return new OutfitSwapPlan(results, finished, notes);
    }

    /// <summary>
    /// Each replaced piece draws the donor's cloth with the donor's own materials: a material takes the place of one of the
    /// piece's cloth slots while it has one free, and is added after them when it has none. Bare skin stays on the piece's
    /// skin slots, or on the replaced body's base body skin material when the piece has none, laid out like that body.
    /// </summary>
    private static OutfitSwapPlan PlanOwnMaterials(
        IReadOnlyList<OutfitSwapPiece> donors,
        IReadOnlyList<Vector3[]> fitted,
        IReadOnlyList<OutfitSwapPiece> targets,
        OutfitSwapBody? targetBody,
        IReadOnlyList<(int Piece, int Section, int[] Triangles)>[] assign,
        OutfitSwapMaterialSources sources,
        List<string> notes)
    {
        var results = new List<OutfitSwapPieceResult>();
        for (var t = 0; t < targets.Count; t++)
        {
            var target = targets[t].Mesh;
            if (assign[t].Count == 0)
            {
                results.Add(new OutfitSwapPieceResult(targets[t].ObjectName, HiddenMesh(target), true));
                continue;
            }
            var materials = target.Materials.ToList();
            var references = new Dictionary<int, SkeletalMeshMaterialReference>();
            var free = new Queue<int>(Enumerable.Range(0, materials.Count).Where(slot => !OutfitSwapBodies.IsSkin(materials[slot])));
            var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int SlotFor(PackageObjectReference material)
            {
                if (byPath.TryGetValue(material.Path, out var at)) return at;
                var replaces = free.Count > 0 ? target.Materials[free.Dequeue()] : null;
                materials.Add($"{material.ClassName}'{material.Path}'");
                at = materials.Count - 1;
                references[at] = new SkeletalMeshMaterialReference(material, replaces);
                byPath[material.Path] = at;
                return at;
            }
            // A piece without any skin slot gets the body's own skin material for the skin it shows (only that kind, so the
            // skin is laid out as on a piece with one skin slot).
            (bool Lower, int Slot)? added = null;
            int? SkinSlot(bool lower)
            {
                var any = false;
                for (var slot = 0; slot < target.Materials.Count; slot++)
                {
                    if (!OutfitSwapBodies.IsSkin(target.Materials[slot])) continue;
                    if (OutfitSwapBodies.IsLowerSkin(target.Materials[slot]) == lower) return slot;
                    any = true;
                }
                if (added is { } skin) return skin.Lower == lower ? skin.Slot : null;
                if (any || (lower ? sources.LowerSkin : sources.UpperSkin) is not { } material) return null;
                added = (lower, SlotFor(material));
                return added.Value.Slot;
            }
            var mesh = Build(donors, fitted, target, assign[t], (piece, material) =>
            {
                var name = donors[piece].Mesh.Materials[material];
                if (OutfitSwapBodies.IsSkin(name) && SkinSlot(OutfitSwapBodies.IsLowerSkin(name)) is { } skin)
                    return (skin, null, OutfitSwapBodies.IsLowerSkin(name));
                var reference = material < sources.Donors[piece].Count ? sources.Donors[piece][material] : null;
                if (reference is null)
                    throw new InvalidDataException($"Arkive cannot tell which game material {donors[piece].ObjectName} draws {name} with.");
                return (SlotFor(reference), null, null);
            }, SkinSlot, targetBody);
            var replaced = references.Values.Count(reference => reference.Replaces is not null);
            if (references.Count > replaced)
                notes.Add($"{targets[t].ObjectName} gains {references.Count - replaced} material slot(s) for the outfit's own materials.");
            results.Add(new OutfitSwapPieceResult(targets[t].ObjectName, new OutfitSwapMesh
            {
                Positions = mesh.Positions, Normals = mesh.Normals, Tangents = mesh.Tangents, Uv0 = mesh.Uv0, Uv1 = mesh.Uv1,
                Colours = mesh.Colours, Joints = mesh.Joints, Weights = mesh.Weights, Sections = mesh.Sections, Materials = materials,
                BoneNames = mesh.BoneNames, BonePositions = mesh.BonePositions, BoneParents = mesh.BoneParents,
                MaterialReferences = references,
            }, false));
        }
        notes.Add("The outfit keeps its own materials, so nothing is repainted.");
        return new OutfitSwapPlan(results, [], notes);
    }

    /// <summary>Splits a section into connected pieces of cloth; a piece mostly hanging from the legs and hips goes to the lower half.</summary>
    private static IEnumerable<(bool Lower, int[] Triangles)> SplitByLegs(OutfitSwapMesh mesh, OutfitSwapSection section)
    {
        if (OutfitSwapBodies.IsSkin(mesh.Materials[section.Material]))
        {
            yield return (OutfitSwapBodies.IsLowerSkin(mesh.Materials[section.Material]), section.Triangles);
            yield break;
        }
        var parent = Enumerable.Range(0, mesh.VertexCount).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        void Join(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b) parent[a] = b;
        }
        var t = section.Triangles;
        for (var i = 0; i + 2 < t.Length; i += 3)
        {
            Join(t[i], t[i + 1]);
            Join(t[i + 1], t[i + 2]);
        }
        var leg = mesh.BoneNames.Select(name => LegBone().IsMatch(name)).ToArray();
        foreach (var group in Enumerable.Range(0, t.Length / 3).GroupBy(triangle => Find(t[triangle * 3])))
        {
            var vertices = group.SelectMany(triangle => new[] { t[triangle * 3], t[triangle * 3 + 1], t[triangle * 3 + 2] }).Distinct().ToArray();
            var share = vertices.Average(v => Enumerable.Range(0, 4).Sum(k => leg[mesh.Joints[v * 4 + k]] ? mesh.Weights[v * 4 + k] : 0));
            yield return (share > 0.5, group.SelectMany(triangle => new[] { t[triangle * 3], t[triangle * 3 + 1], t[triangle * 3 + 2] }).ToArray());
        }
    }

    private static OutfitSwapMesh Build(
        IReadOnlyList<OutfitSwapPiece> donors,
        IReadOnlyList<Vector3[]> fitted,
        OutfitSwapMesh target,
        IReadOnlyList<(int Piece, int Section, int[] Triangles)> parts,
        Func<int, int, (int Slot, Vector4? Rect, bool? SkinLower)> placement,
        Func<bool, int?> skinSlot,
        OutfitSwapBody? targetBody)
    {
        // Variant tells copies of one donor vertex apart: -1 as it is, 0/1 on upper/lower skin, 2+ a corner of one triangle.
        var index = new Dictionary<(int Piece, int Vertex, Vector4? Rect, Vector2 Tile, int Variant), int>();
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var uv0 = new List<Vector2>();
        var uv1 = new List<Vector2>();
        var colours = new List<byte>();
        var joints = new List<int>();
        var weights = new List<float>();
        var sections = new Dictionary<int, List<int>>();
        var targetBones = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < target.BoneNames.Length; i++) targetBones.TryAdd(target.BoneNames[i], i);
        var chains = target.BoneNames.Select((name, i) => (Match: ChainLink().Match(name), i)).Where(item => item.Match.Success)
            .GroupBy(item => item.Match.Groups[1].Value).ToDictionary(group => group.Key, group => group.Select(item => item.i).ToArray(), StringComparer.Ordinal);
        var firstChild = new Dictionary<int, int>();
        for (var i = 0; i < target.BoneParents.Length; i++) if (target.BoneParents[i] >= 0) firstChild.TryAdd(target.BoneParents[i], i);

        foreach (var (piece, section, triangles) in parts)
        {
            var donor = donors[piece].Mesh;
            var (slot, rect, skinLower) = placement(piece, donor.Sections[section].Material);
            var skin = skinLower is not null && targetBody is { Native: true } ? SkinUvs(fitted[piece], triangles, skinLower.Value, skinSlot, targetBody) : null;
            for (var i = 0; i < triangles.Length; i++)
            {
                var vertex = triangles[i];
                var variant = -1;
                var triangleSlot = slot;
                if (skin is not null) (triangleSlot, variant) = (skin.Slots[i / 3], skin.Variants[i]);
                if (!sections.TryGetValue(triangleSlot, out var list)) sections[triangleSlot] = list = [];
                // A picture squeezed into a square of a shared one no longer repeats, so a triangle laid out on a later
                // repeat of it (UVs past 0..1) is moved back by whole repeats first.
                var tile = Vector2.Zero;
                if (rect is not null)
                {
                    var first = i - i % 3;
                    var centre = (donor.Uv0[triangles[first]] + donor.Uv0[triangles[first + 1]] + donor.Uv0[triangles[first + 2]]) / 3;
                    tile = new Vector2(MathF.Floor(centre.X), MathF.Floor(centre.Y));
                }
                if (!index.TryGetValue((piece, vertex, rect, tile, variant), out var at))
                {
                    at = positions.Count;
                    index[(piece, vertex, rect, tile, variant)] = at;
                    var position = fitted[piece][vertex];
                    positions.Add(position);
                    normals.Add(donor.Normals[vertex]);
                    tangents.Add(donor.Tangents[vertex]);
                    var uv = skin is not null ? skin.Uvs[i] : donor.Uv0[vertex] - tile;
                    if (rect is { } r) uv = new Vector2(r.X + Math.Clamp(uv.X, 0, 1) * (r.Z - r.X), r.Y + Math.Clamp(uv.Y, 0, 1) * (r.W - r.Y));
                    uv0.Add(uv);
                    uv1.Add(donor.Uv1[vertex]);
                    for (var c = 0; c < 4; c++) colours.Add(donor.Colours[vertex * 4 + c]);
                    var bones = RemapWeights(donor, vertex, position, target, targetBones, chains, firstChild, targetBody);
                    joints.AddRange(bones.Select(item => item.Bone));
                    weights.AddRange(bones.Select(item => item.Weight));
                }
                list.Add(at);
            }
        }
        return new OutfitSwapMesh
        {
            Positions = positions.ToArray(), Normals = normals.ToArray(), Tangents = tangents.ToArray(),
            Uv0 = uv0.ToArray(), Uv1 = uv1.ToArray(), Colours = colours.ToArray(), Joints = joints.ToArray(), Weights = weights.ToArray(),
            Sections = sections.OrderBy(pair => pair.Key).Select(pair => new OutfitSwapSection(pair.Key, WithArea(pair.Value, positions))).ToArray(),
            Materials = target.Materials, BoneNames = target.BoneNames, BonePositions = target.BonePositions, BoneParents = target.BoneParents,
        };
    }

    private sealed record SkinLayout(Vector2[] Uvs, int[] Variants, int[] Slots);

    /// <summary>
    /// Bare skin drawn with the target's own skin material takes its picture coordinates from the target's base body at the
    /// same spot, since each class lays its skin picture out differently. A triangle goes to the upper or lower skin
    /// material by where it lies on the body, when the piece has both; a triangle across a seam of the body's layout
    /// takes all three corners from the piece of the layout its middle lies on.
    /// </summary>
    private static SkinLayout SkinUvs(Vector3[] positions, int[] triangles, bool lower, Func<bool, int?> skinSlot, OutfitSwapBody body)
    {
        var both = skinSlot(!lower) is not null;
        var uvs = new Vector2[triangles.Length];
        var variants = new int[triangles.Length];
        var slots = new int[triangles.Length / 3];
        var perVertex = new Dictionary<(int Vertex, bool Lower), (Vector2 Uv, int Chart)>();
        for (var t = 0; t * 3 < triangles.Length; t++)
        {
            var corners = new[] { triangles[t * 3], triangles[t * 3 + 1], triangles[t * 3 + 2] };
            var centre = (positions[corners[0]] + positions[corners[1]] + positions[corners[2]]) / 3;
            var home = body.ClosestPreferringLarge(centre, both ? null : lower);
            var kind = home.Triangle < 0 ? lower : home.Lower;
            slots[t] = skinSlot(kind) ?? skinSlot(lower)!.Value;
            var split = false;
            for (var c = 0; c < 3; c++)
            {
                var vertex = corners[c];
                if (!perVertex.TryGetValue((vertex, kind), out var found))
                {
                    var surface = body.Closest(positions[vertex], kind);
                    found = surface.Triangle < 0 ? (Vector2.Zero, -1) : (body.UvAt(surface.Triangle, surface.Position), surface.Chart);
                    perVertex[(vertex, kind)] = found;
                }
                uvs[t * 3 + c] = found.Uv;
                if (home.Triangle >= 0 && found.Chart != home.Chart) split = true;
            }
            // A triangle stretched across the picture spans a seam inside one piece of the layout (around a leg).
            var uvSpan = MathF.Max(Vector2.Distance(uvs[t * 3], uvs[t * 3 + 1]), MathF.Max(Vector2.Distance(uvs[t * 3 + 1], uvs[t * 3 + 2]), Vector2.Distance(uvs[t * 3 + 2], uvs[t * 3])));
            var size = MathF.Max(Vector3.Distance(positions[corners[0]], positions[corners[1]]),
                MathF.Max(Vector3.Distance(positions[corners[1]], positions[corners[2]]), Vector3.Distance(positions[corners[2]], positions[corners[0]])));
            var across = home.Triangle >= 0 && uvSpan > 4 * body.PictureScale * size + 0.01f;
            for (var c = 0; c < 3; c++)
            {
                if (across)
                {
                    // Every corner takes the nearest spot on the side of the seam the triangle's middle lies on.
                    var near = body.UvAt(home.Triangle, home.Position);
                    var surface = body.Closest(positions[corners[c]], kind, home.Chart, near);
                    uvs[t * 3 + c] = surface.Triangle < 0 ? near : body.UvAt(surface.Triangle, surface.Position);
                }
                else if (split)
                {
                    // Corners nearest another piece of the layout take the nearest spot on the triangle's own piece.
                    var surface = body.Closest(positions[corners[c]], kind, home.Chart);
                    uvs[t * 3 + c] = surface.Triangle < 0 ? body.UvAt(home.Triangle, home.Position) : body.UvAt(surface.Triangle, surface.Position);
                }
                variants[t * 3 + c] = split || across ? 2 + t : kind ? 1 : 0;
            }
        }
        return new SkinLayout(uvs, variants, slots);
    }

    /// <summary>
    /// A donor vertex's skin on the target skeleton: the same bones by name; links of a chain (skirt, cape) pick the
    /// target's link nearest the point, since chains differ in length; bones the target lacks take the target body's own
    /// skin weights at that spot. At most four bones, normalised.
    /// </summary>
    private static (int Bone, float Weight)[] RemapWeights(OutfitSwapMesh donor, int vertex, Vector3 position, OutfitSwapMesh target,
        Dictionary<string, int> targetBones, Dictionary<string, int[]> chains, Dictionary<int, int> firstChild, OutfitSwapBody? body)
    {
        var sum = new Dictionary<int, float>();
        for (var k = 0; k < 4; k++)
        {
            var weight = donor.Weights[vertex * 4 + k];
            if (weight <= 0) continue;
            var name = donor.BoneNames[donor.Joints[vertex * 4 + k]];
            if (!targetBones.TryGetValue(name, out var bone))
            {
                if (body is null) continue;
                foreach (var (bodyBone, share) in body.WeightsAt(position, targetBones.ContainsKey))
                    sum[targetBones[bodyBone]] = sum.GetValueOrDefault(targetBones[bodyBone]) + weight * share;
                continue;
            }
            if (ChainLink().Match(name) is { Success: true } link && chains.TryGetValue(link.Groups[1].Value, out var chain) && chain.Length > 1)
                bone = chain.MinBy(candidate => SegmentDistance(position, target.BonePositions[candidate],
                    target.BonePositions[firstChild.GetValueOrDefault(candidate, candidate)]));
            sum[bone] = sum.GetValueOrDefault(bone) + weight;
        }
        var best = sum.OrderByDescending(pair => pair.Value).Take(4).ToArray();
        var total = best.Sum(pair => pair.Value);
        if (total <= 0)
        {
            var root = Math.Max(0, Array.IndexOf(target.BoneNames, "bip001-pelvis"));
            return [(root, 1), (0, 0), (0, 0), (0, 0)];
        }
        var result = new (int, float)[4];
        for (var i = 0; i < 4; i++) result[i] = i < best.Length ? (best[i].Key, best[i].Value / total) : (0, 0);
        return result;
    }

    private static float SegmentDistance(Vector3 point, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var s = ab.LengthSquared() < 1e-12f ? 0 : Math.Clamp(Vector3.Dot(point - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector3.Distance(point, a + ab * s);
    }

    /// <summary>The triangles that have an area: ones squeezed to a line or a point by the fit draw nothing and are dropped.</summary>
    private static int[] WithArea(List<int> triangles, List<Vector3> positions)
    {
        var kept = new List<int>(triangles.Count);
        for (var i = 0; i + 2 < triangles.Count; i += 3)
        {
            var face = Vector3.Cross(positions[triangles[i + 1]] - positions[triangles[i]], positions[triangles[i + 2]] - positions[triangles[i]]);
            if (face.LengthSquared() > 1e-14f) kept.AddRange([triangles[i], triangles[i + 1], triangles[i + 2]]);
        }
        return kept.ToArray();
    }

    /// <summary>A piece left with nothing to show: one tiny triangle tucked inside the pelvis.</summary>
    private static OutfitSwapMesh HiddenMesh(OutfitSwapMesh target)
    {
        var pelvis = Math.Max(0, Array.IndexOf(target.BoneNames, "bip001-pelvis"));
        var at = target.BonePositions[pelvis];
        Vector3[] positions = [at, at + new Vector3(0.001f, 0, 0), at + new Vector3(0, 0.001f, 0)];
        return new OutfitSwapMesh
        {
            Positions = positions, Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Tangents = [new(1, 0, 0, 1), new(1, 0, 0, 1), new(1, 0, 0, 1)], Uv0 = new Vector2[3], Uv1 = new Vector2[3],
            Colours = Enumerable.Repeat((byte)255, 12).ToArray(), Joints = [pelvis, 0, 0, 0, pelvis, 0, 0, 0, pelvis, 0, 0, 0],
            Weights = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0], Sections = [new OutfitSwapSection(0, [0, 1, 2])],
            Materials = target.Materials, BoneNames = target.BoneNames, BonePositions = target.BonePositions, BoneParents = target.BoneParents,
        };
    }

    internal enum Role { Colour, Normal, Mask, Orm, Specular, Emissive, Panning }

    internal static string? Texture(OutfitSwapMaterial material, Role role)
    {
        string[] names = role switch
        {
            Role.Colour => ["texture_basecolor", "texture_diffuse"],
            Role.Normal => ["texture_normal"],
            Role.Mask => ["texture_color_fx_skin"],
            Role.Orm => ["texture_orm"],
            Role.Specular => ["texture_specular"],
            Role.Panning => ["texture_fx_panning", "texutre_rimlight_panning", "texture_rimlight_panning"],
            _ => ["texture_emissive"],
        };
        foreach (var name in names)
            if (material.Textures.TryGetValue(name, out var texture) && !string.IsNullOrWhiteSpace(texture) &&
                !texture.Equals("null", StringComparison.OrdinalIgnoreCase))
                return texture;
        return null;
    }
}
