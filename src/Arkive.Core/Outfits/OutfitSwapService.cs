using System.Numerics;
using Arkive.Core.Proportions;

namespace Arkive.Core.Outfits;

/// <summary>How well a swap fitted: clipping is cloth that sat outside its own body but ends up inside the new one.</summary>
public sealed record OutfitSwapReport(
    BodyProfile From,
    BodyProfile To,
    int LearnedCostumes,
    double ClippingBeforePercent,
    double ClippingPercent,
    double StretchedPercent,
    IReadOnlyList<string> Notes)
{
    public bool CrossesSex => From.Female != To.Female;
}

public sealed record OutfitSwapFit(OutfitSwapPlan Plan, OutfitSwapReport Report);

/// <summary>
/// Fits one player outfit onto another body's outfit: the donor's pieces move with the skeleton onto the target body,
/// take on its shape as the game's own shared costumes show it, are kept off its skin, and are then drawn by the target
/// pieces with the target's own materials and skeleton.
/// </summary>
public sealed class OutfitSwapService(IOutfitSwapLibrary library, string cacheDirectory)
{
    private readonly OutfitSwapLearning _learning = new(library, Path.Combine(cacheDirectory, "fields"));

    public async Task<OutfitSwapFit> FitAsync(
        IReadOnlyList<OutfitSwapPiece> donors,
        IReadOnlyList<OutfitSwapPiece> targets,
        string gameBuild,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (donors.Count == 0 || targets.Count == 0) throw new ArgumentException("Choose the outfit to wear and the outfit to replace.");
        foreach (var piece in donors.Concat(targets))
            if (!piece.Mesh.HasBodySkeleton)
                throw new InvalidDataException($"{piece.ObjectName} has no body skeleton (only a placeholder bone), so it cannot be swapped. " +
                                               "Its worn version usually has a different name (often ending in a or _loc_int).");
        var from = Body(donors, "outfit to wear");
        var to = Body(targets, "outfit to replace");
        var notes = new List<string>();
        var field = from.Profile.Id == to.Profile.Id ? null
            : await _learning.LearnAsync(from, to, gameBuild, progress, cancellationToken).ConfigureAwait(false);
        if (from.Profile.Id != to.Profile.Id && field is null)
            notes.Add($"The game has no costumes shared by {from.Profile.DisplayName} and {to.Profile.DisplayName}, so only the skeleton guides the fit.");
        var targetSkeleton = new OutfitSwapSkeleton(targets.Select(piece => piece.Mesh));
        progress?.Report("Reading the bare bodies");
        var donorBody = await BodyAsync(from, donors.Select(piece => piece.Mesh).ToArray(), gameBuild, cancellationToken).ConfigureAwait(false);
        var targetBody = await BodyAsync(to, targets.Select(piece => piece.Mesh).ToArray(), gameBuild, cancellationToken).ConfigureAwait(false);

        // Where the new body's face starts, for the outfit's bare neck to meet.
        IReadOnlyList<(Vector3 A, Vector3 B)>? seam = null;
        if (targetBody is { Native: true } && targetSkeleton.TryGet("bip001-neck", out var neck) && targetSkeleton.TryGet("bip001-head", out var head))
            seam = targetBody.HeadSeam(neck, head);
        else
            (neck, head) = (Vector3.Zero, Vector3.Zero);
        var fitted = new List<Vector3[]>();
        var shaded = new List<OutfitSwapPiece>();
        long judged = 0, clippedBefore = 0, clippedAfter = 0, stretched = 0, faces = 0;
        foreach (var donor in donors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Fitting {donor.ObjectName}");
            var mesh = donor.Mesh;
            var maps = OutfitSwapFitting.BoneMaps(mesh, targetSkeleton);
            var moved = OutfitSwapFitting.Retarget(mesh, maps);
            var (normals, tangents) = OutfitSwapFitting.RetargetFrames(mesh, maps);
            var ids = OutfitSwapFitting.Weld(mesh.Positions, out var weldedCount);
            var triangles = mesh.Sections.SelectMany(section => section.Triangles).Select(v => ids[v]).ToArray();
            var cloth = new bool[mesh.VertexCount];
            foreach (var section in mesh.Sections)
                if (!OutfitSwapBodies.IsSkin(mesh.Materials[section.Material]))
                    foreach (var v in section.Triangles) cloth[v] = true;
            // Feet and shoes keep the shape the donor's artists gave them and only follow the skeleton: a shoe is made
            // around its own foot, and bending either onto the new body's foot crumples soles and toes.
            var feet = OutfitSwapFitting.FootShares(mesh);
            var movable = cloth.Select((value, v) => value && feet[v] < 0.1f).ToArray();
            if (field is not null)
            {
                var all = Enumerable.Range(0, mesh.VertexCount).ToArray();
                var features = OutfitSwapFitting.Features(mesh, all);
                var correction = field.Correct(moved, features);
                // Hands follow the skeleton alone too: costumes rarely cover them, so what the field says there is guesswork.
                for (var v = 0; v < correction.Length; v++)
                    correction[v] *= Math.Clamp(1 - feet[v] - features[v * OutfitSwapFitting.FeatureCount + OutfitSwapFitting.LeftHand]
                                                  - features[v * OutfitSwapFitting.FeatureCount + OutfitSwapFitting.RightHand], 0, 1);
                // Smoothed widely: neighbouring small details (lace, hems) given different corrections would fold over.
                moved = Add(moved, Expand(OutfitSwapFitting.SmoothOnMesh(Average(correction, ids, weldedCount), triangles, 64), ids));
            }
            var weldedCloth = new bool[weldedCount];
            var weldedFeet = new float[weldedCount];
            for (var v = 0; v < ids.Length; v++)
            {
                weldedCloth[ids[v]] |= cloth[v];
                weldedFeet[ids[v]] = Math.Max(weldedFeet[ids[v]], feet[v]);
            }
            if (from.Female != to.Female && donorBody is not null && targetBody is not null)
            {
                var wrapped = OutfitSwapClearance.Conform(Average(mesh.Positions, ids, weldedCount), Average(moved, ids, weldedCount),
                    donorBody, targetBody, weldedCloth, triangles);
                moved = Expand(wrapped, ids);
            }
            if (targetBody is { Native: true })
            {
                // Skin in and just above the shoes stays with them (they keep the donor's shape), fading into the new
                // body's shape up the calf, so the shoe's rim and the ankle meet without a kink.
                var welded = Average(moved, ids, weldedCount);
                var ankle = targetSkeleton.TryGet("bip001-l-foot", out var foot) ? foot.Y : 0.1f;
                // Hands too: base bodies' hands rest elsewhere (or are missing), and hands hanging by the hips would be
                // laid onto the thighs.
                var handFeatures = OutfitSwapFitting.Features(mesh, Enumerable.Range(0, mesh.VertexCount).ToArray());
                var hands = new float[weldedCount];
                for (var v = 0; v < ids.Length; v++)
                    hands[ids[v]] = Math.Max(hands[ids[v]], handFeatures[v * OutfitSwapFitting.FeatureCount + OutfitSwapFitting.LeftHand] +
                                                          handFeatures[v * OutfitSwapFitting.FeatureCount + OutfitSwapFitting.RightHand]);
                var hold = welded.Select((point, v) => Math.Max(Math.Max(Math.Clamp((weldedFeet[v] - 0.1f) / 0.4f, 0, 1),
                    Math.Clamp((ankle + 0.06f - point.Y) / 0.05f, 0, 1)), Math.Clamp(hands[v] / 0.3f, 0, 1))).ToArray();
                moved = Expand(OutfitSwapClearance.SnapSkin(welded, weldedCloth, hold, targetBody), ids);
            }
            if (seam is not null)
                moved = Expand(OutfitSwapClearance.MeetHead(Average(moved, ids, weldedCount), weldedCloth, triangles, seam, neck, head), ids);
            if (OutfitSwapClearance.ShoeSurface(mesh, mesh.Positions, feet) is { } donorShoes &&
                OutfitSwapClearance.ShoeSurface(mesh, moved, feet) is { } shoes)
                moved = OutfitSwapClearance.KeepInShoes(mesh.Positions, moved, cloth, donorShoes, shoes);
            // Cloth that lay over the outfit's own bare skin stays over it now the skin has moved.
            if (OutfitSwapClearance.SkinSurface(mesh, mesh.Positions) is { } donorSkin &&
                OutfitSwapClearance.SkinSurface(mesh, moved) is { } movedSkin)
                moved = OutfitSwapClearance.PushOut(mesh.Positions, moved, donorSkin, movedSkin, movable, reach: 0.015f);
            if (donorBody is not null && targetBody is not null)
            {
                var (donorGap, donorNear, _) = donorBody.Measure(mesh.Positions);
                var before = targetBody.Measure(moved).Signed;
                moved = OutfitSwapClearance.PushOut(mesh.Positions, moved, donorBody, targetBody, movable);
                var after = targetBody.Measure(moved).Signed;
                for (var v = 0; v < moved.Length; v++)
                {
                    if (!movable[v] || donorNear[v] >= 0.08f || donorGap[v] <= (donorBody.Native ? -0.001f : -0.01f)) continue;
                    judged++;
                    if (before[v] < -0.002f) clippedBefore++;
                    if (after[v] < -0.002f) clippedAfter++;
                }
            }
            foreach (var section in mesh.Sections)
                for (var i = 0; i + 2 < section.Triangles.Length; i += 3)
                {
                    faces++;
                    if (Stretched(mesh.Positions, moved, section.Triangles[i], section.Triangles[i + 1], section.Triangles[i + 2])) stretched++;
                }
            // Small folds the moves pressed inside out (lace edges, hems) are smoothed back the right way round.
            moved = Expand(OutfitSwapFitting.Untangle(Average(moved, ids, weldedCount), Average(normals, ids, weldedCount), triangles), ids);
            // Bare skin lying on the new body shades like it.
            var handShares = OutfitSwapFitting.Features(mesh, Enumerable.Range(0, mesh.VertexCount).ToArray());
            if (targetBody is { Native: true })
                for (var v = 0; v < moved.Length; v++)
                {
                    if (cloth[v] || feet[v] >= 0.5f || handShares[v * OutfitSwapFitting.FeatureCount + OutfitSwapFitting.LeftHand] +
                        handShares[v * OutfitSwapFitting.FeatureCount + OutfitSwapFitting.RightHand] > 0.1f) continue;
                    var surface = targetBody.Closest(moved[v]);
                    if (surface.Triangle >= 0 && Vector3.Distance(surface.Position, moved[v]) < 0.003f)
                        normals[v] = targetBody.NormalAt(surface.Triangle, surface.Position);
                }
            fitted.Add(moved);
            shaded.Add(donor with { Mesh = mesh.WithFrames(normals, tangents) });
        }
        progress?.Report("Placing the outfit in the new materials");
        var plan = OutfitSwapPlanner.Plan(shaded, fitted, targets, targetBody);
        notes.AddRange(plan.Notes);
        var report = new OutfitSwapReport(from.Profile, to.Profile, field?.CostumeCount ?? 0,
            judged == 0 ? 0 : 100.0 * clippedBefore / judged, judged == 0 ? 0 : 100.0 * clippedAfter / judged,
            faces == 0 ? 0 : 100.0 * stretched / faces, notes);
        return new OutfitSwapFit(plan, report);
    }

    private static OutfitSwapBodyInfo Body(IReadOnlyList<OutfitSwapPiece> pieces, string role)
    {
        foreach (var piece in pieces)
            if (OutfitSwapBodies.Detect(piece.Mesh, piece.ObjectName) is { } profile && OutfitSwapBodies.For(profile) is { } info)
                return info;
        throw new InvalidDataException($"Arkive does not know the body the {role} was made for. Outfit swaps work between player outfits.");
    }

    /// <summary>
    /// The body's bare base body. A body the game ships none for (the female mage) borrows the nearest one of the same sex,
    /// moved onto its skeleton.
    /// </summary>
    private async Task<OutfitSwapBody?> BodyAsync(OutfitSwapBodyInfo body, IReadOnlyList<OutfitSwapMesh> skeleton, string gameBuild,
        CancellationToken cancellationToken)
    {
        if (body.BaseBody.Count > 0)
        {
            var meshes = new List<OutfitSwapMesh>();
            foreach (var name in body.BaseBody)
                if (await library.LoadAsync(name, cancellationToken).ConfigureAwait(false) is { } mesh) meshes.Add(mesh);
            return meshes.Count > 0 ? new OutfitSwapBody(meshes) : null;
        }
        var nearest = OutfitSwapBodies.All.Where(other => other.BaseBody.Count > 0 && other.Female == body.Female)
            .OrderBy(other => Math.Abs(other.Profile.PelvisHeight - body.Profile.PelvisHeight)).FirstOrDefault();
        if (nearest is null) return null;
        var target = new OutfitSwapSkeleton(skeleton);
        var field = await _learning.LearnAsync(nearest, body, gameBuild, null, cancellationToken).ConfigureAwait(false);
        var moved = new List<OutfitSwapMesh>();
        foreach (var name in nearest.BaseBody)
        {
            if (await library.LoadAsync(name, cancellationToken).ConfigureAwait(false) is not { } mesh) continue;
            var positions = OutfitSwapFitting.Retarget(mesh, OutfitSwapFitting.BoneMaps(mesh, target));
            if (field is not null)
                positions = Add(positions, field.Correct(positions, OutfitSwapFitting.Features(mesh, Enumerable.Range(0, mesh.VertexCount).ToArray())));
            moved.Add(new OutfitSwapMesh
            {
                Positions = positions, Normals = mesh.Normals, Tangents = mesh.Tangents, Uv0 = mesh.Uv0, Uv1 = mesh.Uv1, Colours = mesh.Colours,
                Joints = mesh.Joints, Weights = mesh.Weights, Sections = mesh.Sections, Materials = mesh.Materials,
                BoneNames = mesh.BoneNames, BonePositions = mesh.BonePositions, BoneParents = mesh.BoneParents,
            });
        }
        return moved.Count > 0 ? new OutfitSwapBody(moved, native: false) : null;
    }

    private static Vector3[] Add(Vector3[] a, Vector3[] b) => a.Select((value, i) => value + b[i]).ToArray();

    private static Vector3[] Average(Vector3[] values, int[] ids, int count)
    {
        var sum = new Vector3[count];
        var number = new int[count];
        for (var i = 0; i < ids.Length; i++) { sum[ids[i]] += values[i]; number[ids[i]]++; }
        for (var i = 0; i < count; i++) if (number[i] > 0) sum[i] /= number[i];
        return sum;
    }

    private static Vector3[] Expand(Vector3[] welded, int[] ids) => ids.Select(id => welded[id]).ToArray();

    private static bool Stretched(Vector3[] before, Vector3[] after, int a, int b, int c)
    {
        static float Longest(Vector3[] p, int a, int b, int c) =>
            Math.Max(Vector3.Distance(p[a], p[b]), Math.Max(Vector3.Distance(p[b], p[c]), Vector3.Distance(p[c], p[a])));
        return Longest(after, a, b, c) > 1.5f * Longest(before, a, b, c) + 0.01f;
    }
}
