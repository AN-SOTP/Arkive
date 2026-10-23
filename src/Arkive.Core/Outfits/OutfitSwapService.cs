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

    /// <summary>
    /// Experimental: put the new body's base-body feet in place of the outfit's and reshape the shoes around them. Off by
    /// default: base-body feet are modelled in less detail than outfits' own feet, so the outfit's feet (fitted to the
    /// new legs) look better.
    /// </summary>
    public bool BodyFeet { get; init; } = Environment.GetEnvironmentVariable("ARKIVE_SWAP_BODY_FEET") == "1";

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
        // Bodies sharing a skeleton can lay their skin pictures out differently (the Female Gunner's next to the Female
        // Assassin's base body): bare skin can then only keep the outfit's own picture.
        var bodyClasses = to.BaseBody.Select(OutfitSwapBodies.ClassOf).Append($"pc_{to.AvatarCode}_").ToHashSet();
        var foreignSkin = targets.SelectMany(piece => piece.Mesh.Materials).Where(OutfitSwapBodies.IsSkin)
            .Any(material => OutfitSwapBodies.ClassOf(material) is { } skinClass && !bodyClasses.Contains(skinClass));
        progress?.Report("Reading the bare bodies");
        var donorBody = await BodyAsync(from, donors.Select(piece => piece.Mesh).ToArray(), gameBuild, cancellationToken).ConfigureAwait(false);
        var targetBody = await BodyAsync(to, targets.Select(piece => piece.Mesh).ToArray(), gameBuild, cancellationToken).ConfigureAwait(false);

        // Where the new body's face starts, for the outfit's bare neck to meet.
        IReadOnlyList<(Vector3 A, Vector3 B)>? seam = null;
        if (targetBody is { Native: true } && targetSkeleton.TryGet("bip001-neck", out var neck) && targetSkeleton.TryGet("bip001-head", out var head))
            seam = targetBody.HeadSeam(neck, head);
        else
            (neck, head) = (Vector3.Zero, Vector3.Zero);
        // The new body's own bare feet, to put in place of the outfit's.
        var targetFeet = new List<OutfitSwapMesh>();
        if (BodyFeet && targetBody is { Native: true })
            foreach (var name in to.BaseBody)
                if (await library.LoadAsync(name, cancellationToken).ConfigureAwait(false) is { } part) targetFeet.Add(part);
        var targetFootPoints = targetFeet.SelectMany(part => part.Sections.Where(section => OutfitSwapBodies.IsSkin(part.Materials[section.Material]))
            .SelectMany(section => section.Triangles).Distinct().Select(v => part.Positions[v])).ToArray();
        var targetFootAnkle = targetSkeleton.TryGet("bip001-l-foot", out var targetFootJoint) ? targetFootJoint.Y : 0.1f;
        // The new body's own bare hands, for outfits from another body that show theirs.
        // They draw with the replaced outfit's own upper skin material, so that outfit must have one.
        var upperSkin = targets.Any(piece => piece.Mesh.Materials.Any(material => OutfitSwapBodies.IsSkin(material) && !OutfitSwapBodies.IsLowerSkin(material)));
        var bodyHands = from.Profile.Id != to.Profile.Id && upperSkin && !foreignSkin && to.Hands is { } handsPiece
            ? await library.LoadAsync(handsPiece, cancellationToken).ConfigureAwait(false) : null;
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
            var onHands = OutfitSwapHands.Shares(mesh);
            var newHands = bodyHands is not null && OutfitSwapHands.Bare(mesh, onHands);
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
            // Shoes and socks are reshaped around the new body's own feet, which replace the outfit's (see the end).
            var ankleJoint = DonorAnkle(mesh);
            var newFeet = BodyFeet && targetFeet.Count > 0 && ankleJoint is not null;
            OutfitSwapFeet.Stance? stance = null;
            if (newFeet && feet.Any(share => share >= 0.1f))
            {
                var ownFoot = mesh.Sections.Where(section => OutfitSwapBodies.IsSkin(mesh.Materials[section.Material]))
                    .SelectMany(section => section.Triangles).Distinct().Select(v => mesh.Positions[v]).ToArray();
                // The outfit's own bare foot, which its shoes were made around, when it has the whole foot; otherwise the
                // old body's (an outfit's own skin often holds only the part its shoe shows).
                var bodyFoot = donorBody?.SkinPoints.Count(p => p.Y < ankleJoint) ?? 0;
                var donorFoot = donorBody is null || ownFoot.Count(p => p.Y < ankleJoint) >= bodyFoot * 0.6f ? ownFoot : donorBody.SkinPoints.ToArray();
                stance = new OutfitSwapFeet.Stance(donorFoot, ankleJoint!.Value, targetFootPoints, targetFootAnkle,
                    targetSkeleton.TryGet("bip001-l-foot", out var leftFoot) ? leftFoot : Vector3.Zero,
                    targetSkeleton.TryGet("bip001-r-foot", out var rightFoot) ? rightFoot : Vector3.Zero);
                moved = OutfitSwapFeet.FitShoes(mesh.Positions, moved, feet, cloth, donorFoot, ankleJoint.Value, targetFootPoints, targetFootAnkle, stance);
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
                // With the new body's own feet coming in, all the leg's skin lies on the body down to them.
                // Shoes already sit on the new feet, so the skin's move does not carry them along.
                var hold = welded.Select((point, v) => Math.Max(newFeet
                    ? weldedCloth[v] ? Math.Clamp((weldedFeet[v] - 0.05f) / 0.2f, 0, 1) : 0
                    : Math.Max(Math.Clamp((weldedFeet[v] - 0.1f) / 0.4f, 0, 1), Math.Clamp((ankle + 0.06f - point.Y) / 0.05f, 0, 1)),
                    0)).ToArray();
                // The hand keeps the outfit's shape and the forearm takes the new body's: the change is spread over a few
                // centimetres each side of the wrist, or the wrist shows a step where the thinner forearm meets the hand.
                // With the new body's own hands coming in, the arm's skin lies on the body down to them.
                var wrist = OutfitSwapFitting.SmoothOnMesh(hands.Select(share => new Vector3(Math.Clamp(share / 0.3f, 0, 1), 0, 0)).ToArray(), triangles, 24);
                if (!newHands)
                    for (var v = 0; v < hold.Length; v++) hold[v] = Math.Max(hold[v], Math.Max(wrist[v].X, Math.Clamp((hands[v] - 0.5f) / 0.5f, 0, 1)));
                moved = Expand(OutfitSwapClearance.SnapSkin(welded, weldedCloth, hold, targetBody), ids);
            }
            if (seam is not null)
                moved = Expand(OutfitSwapClearance.MeetHead(Average(moved, ids, weldedCount), weldedCloth, triangles, seam, neck, head), ids);
            if (OutfitSwapClearance.ShoeSurface(mesh, mesh.Positions, feet) is { } donorShoes &&
                OutfitSwapClearance.ShoeSurface(mesh, moved, feet) is { } shoes)
                moved = OutfitSwapClearance.KeepInShoes(mesh.Positions, moved, cloth, donorShoes, shoes);
            // Cloth that lay over the outfit's own bare skin stays over it now the skin has moved: what hugged it (a chain
            // round a bare thigh, a garter) keeps hugging it, and nothing sinks into it.
            var onOwnSkin = new bool[weldedCount];
            if (OutfitSwapClearance.SkinSurface(mesh, mesh.Positions) is { } donorSkin &&
                OutfitSwapClearance.SkinSurface(mesh, moved) is { } movedSkin)
            {
                // Only parts lying on the skin all along (a chain, a garter): wrapping part of a dress or of an ornament
                // hanging from it would pull that part off the rest.
                var donorWelded = Average(mesh.Positions, ids, weldedCount);
                var (skinGap, skinNear, _) = donorSkin.MeasureExact(donorWelded);
                var onSkin = weldedCloth.Select((value, v) => value && weldedFeet[v] < 0.1f && skinNear[v] < 0.006f && skinGap[v] > -0.001f).ToArray();
                var weldedMovable = onOwnSkin = WholeParts(onSkin, weldedCloth, triangles, 0.8f);
                moved = Expand(OutfitSwapClearance.Conform(Average(mesh.Positions, ids, weldedCount), Average(moved, ids, weldedCount),
                    donorSkin, movedSkin, weldedMovable, triangles, near: 0.006f, far: 0.02f, rounds: 8, onSkin: true), ids);
                moved = OutfitSwapClearance.PushOut(mesh.Positions, moved, donorSkin, movedSkin, movable, reach: 0.015f);
            }
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
            moved = Expand(OutfitSwapFitting.Untangle(Average(moved, ids, weldedCount), Average(normals, ids, weldedCount), triangles,
                Average(mesh.Positions, ids, weldedCount)), ids);
            // Small parts hanging from the cloth (bells, knots, tassels at a hem) move with the cloth they hang from: fitted
            // on their own they drift off it.
            var handShare = OutfitSwapFitting.Features(mesh, Enumerable.Range(0, mesh.VertexCount).ToArray());
            var weldedHands = new float[weldedCount];
            for (var v = 0; v < ids.Length; v++)
                weldedHands[ids[v]] = Math.Max(weldedHands[ids[v]], handShare[v * OutfitSwapFitting.FeatureCount + OutfitSwapFitting.LeftHand] +
                                                                    handShare[v * OutfitSwapFitting.FeatureCount + OutfitSwapFitting.RightHand]);
            var small = weldedCloth.Select((value, v) => value && !onOwnSkin[v] && weldedFeet[v] < 0.1f && weldedHands[v] < 0.1f).ToArray();
            moved = Expand(HangFromHosts(Average(mesh.Positions, ids, weldedCount), Average(moved, ids, weldedCount), small, weldedCloth, triangles), ids);
            // Bare skin lying on the new body shades like it.
            var handShares = OutfitSwapFitting.Features(mesh, Enumerable.Range(0, mesh.VertexCount).ToArray());
            if (targetBody is { Native: true })
                for (var v = 0; v < moved.Length; v++)
                {
                    if (cloth[v] || (!newFeet && feet[v] >= 0.5f) || handShares[v * OutfitSwapFitting.FeatureCount + OutfitSwapFitting.LeftHand] +
                        handShares[v * OutfitSwapFitting.FeatureCount + OutfitSwapFitting.RightHand] > 0.1f) continue;
                    var surface = targetBody.Closest(moved[v]);
                    if (surface.Triangle >= 0 && Vector3.Distance(surface.Position, moved[v]) < 0.003f)
                        normals[v] = targetBody.NormalAt(surface.Triangle, surface.Position);
                }
            var finished = mesh.WithFrames(normals, tangents);
            if (newFeet)
            {
                (finished, moved, var shownFoot) = OutfitSwapFeet.WithBodyFeet(finished, moved, targetFeet, targetFootAnkle, stance);
                if (donorBody is not null)
                    (finished, moved) = OutfitSwapFeet.Finish(finished, moved, mesh.VertexCount, mesh.Positions,
                        cloth.Select((value, v) => value && feet[v] >= 0.05f).ToArray(), donorBody, shownFoot);
            }
            if (!newFeet) finished = BendWithShoes(finished, feet);
            if (newHands) (finished, moved) = OutfitSwapHands.WithBodyHands(finished, moved, onHands, bodyHands!);
            fitted.Add(moved);
            shaded.Add(OwnPictureFeetAndHands(donor with { Mesh = finished }, feet, foreignSkin));
        }
        // Layers of one piece (a dress over its underskirt, an overlay over a bodice, a sleeve over the arm) stay in their
        // order: each large connected part of a piece's cloth keeps the piece's other cloth and its bare skin that lay
        // under it, under it (wrist skin that keeps the hand's shape would otherwise show through a sleeve).
        for (var k = 0; k < donors.Count; k++)
        {
            var mesh = donors[k].Mesh;
            var count = mesh.VertexCount;
            // Shoes and toenails keep their own shape (see the fit), so layering leaves the feet alone.
            var feetShares = OutfitSwapFitting.FootShares(mesh);
            var skin = new bool[count];
            foreach (var section in mesh.Sections)
                if (OutfitSwapBodies.IsSkin(mesh.Materials[section.Material]))
                    foreach (var v in section.Triangles) skin[v] = feetShares[v] < 0.1f;
            var layers = Layers(mesh).ToArray();
            // Cloth first, then the skin under where the cloth ended up.
            foreach (var skinTurn in new[] { false, true })
                foreach (var layer in layers)
                {
                    var inner = new bool[count];
                    foreach (var section in mesh.Sections)
                        foreach (var v in section.Triangles) inner[v] = skin[v] == skinTurn && feetShares[v] < 0.1f;
                    foreach (var v in layer.SelectMany(section => section.Triangles)) inner[v] = false;
                    if (OutfitSwapClearance.Surface(mesh, mesh.Positions, layer) is { } before &&
                        OutfitSwapClearance.Surface(mesh, fitted[k][..count], layer) is { } after)
                        Array.Copy(skinTurn
                            ? OutfitSwapClearance.KeepUnder(mesh, fitted[k][..count], inner, before, after, exact: true, rounds: 1)
                            : OutfitSwapClearance.KeepUnder(mesh, fitted[k][..count], inner, before, after), fitted[k], count);
                }
        }
        // Cloth hidden behind the outfit's own bare skin (a hem tucked in at the waist) stays behind it: the skin now
        // lies on the new body, the cloth went with the body-shape correction, and the two can part.
        for (var skinPiece = 0; skinPiece < donors.Count; skinPiece++)
        {
            var skinMesh = donors[skinPiece].Mesh;
            if (OutfitSwapClearance.SkinSurface(skinMesh, skinMesh.Positions) is not { } skinBefore ||
                OutfitSwapClearance.SkinSurface(skinMesh, fitted[skinPiece][..skinMesh.VertexCount]) is not { } skinAfter) continue;
            for (var k = 0; k < donors.Count; k++)
            {
                var mesh = donors[k].Mesh;
                var count = mesh.VertexCount;
                var clothOnly = new bool[count];
                var shares = OutfitSwapFitting.FootShares(mesh);
                foreach (var section in mesh.Sections)
                    if (!OutfitSwapBodies.IsSkin(mesh.Materials[section.Material]))
                        foreach (var v in section.Triangles) clothOnly[v] = shares[v] < 0.1f;
                Array.Copy(OutfitSwapClearance.KeepUnder(mesh, fitted[k][..count], clothOnly, skinBefore, skinAfter, reach: 0.02f),
                    fitted[k], count);
            }
        }
        // Pieces were fitted one by one: cloth of one that lay under another's keeps lying under it. Cloth hugging its own
        // piece's bare skin stays on the skin (it was wrapped onto it): pushed under another piece's skirt that came to
        // lie closer in, a chain round the thigh would stand away from it.
        var hugging = donors.Select(donor =>
        {
            if (OutfitSwapClearance.SkinSurface(donor.Mesh, donor.Mesh.Positions) is not { } skin) return new bool[donor.Mesh.VertexCount];
            var (gap, near, _) = skin.MeasureExact(donor.Mesh.Positions);
            var cloth = new bool[donor.Mesh.VertexCount];
            foreach (var section in donor.Mesh.Sections)
                if (!OutfitSwapBodies.IsSkin(donor.Mesh.Materials[section.Material]))
                    foreach (var v in section.Triangles) cloth[v] = true;
            return WholeParts(gap.Select((g, v) => near[v] < 0.01f && g > -0.001f).ToArray(), cloth,
                donor.Mesh.Sections.SelectMany(section => section.Triangles).ToArray(), 0.8f);
        }).ToArray();
        for (var inner = 0; inner < donors.Count; inner++)
            for (var outer = 0; outer < donors.Count; outer++)
            {
                if (inner == outer) continue;
                var outerMesh = donors[outer].Mesh;
                var outerCloth = outerMesh.Sections.Where(section => !OutfitSwapBodies.IsSkin(outerMesh.Materials[section.Material])).ToArray();
                if (OutfitSwapClearance.Surface(outerMesh, outerMesh.Positions, outerCloth) is not { } before ||
                    OutfitSwapClearance.Surface(outerMesh, fitted[outer][..outerMesh.VertexCount], outerCloth) is not { } after) continue;
                var innerMesh = donors[inner].Mesh;
                var count = innerMesh.VertexCount;
                var innerCloth = new bool[count];
                foreach (var section in innerMesh.Sections)
                    if (!OutfitSwapBodies.IsSkin(innerMesh.Materials[section.Material]))
                        foreach (var v in section.Triangles) innerCloth[v] = !hugging[inner][v];
                var kept = OutfitSwapClearance.KeepUnder(innerMesh, fitted[inner][..count], innerCloth, before, after);
                Array.Copy(kept, fitted[inner], count);
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

    /// <summary>
    /// Moves each small connected part of <paramref name="candidates"/> (under <paramref name="largest"/> points) the way
    /// the nearest point of the larger cloth around it moved, when that cloth lay within <paramref name="reach"/>.
    /// </summary>
    private static Vector3[] HangFromHosts(Vector3[] donor, Vector3[] fitted, bool[] candidates, bool[] cloth, IReadOnlyList<int> triangles,
        int largest = 300, float reach = 0.02f)
    {
        var parent = Enumerable.Range(0, donor.Length).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        for (var i = 0; i + 2 < triangles.Count; i += 3)
        {
            parent[Find(triangles[i])] = Find(triangles[i + 1]);
            parent[Find(triangles[i + 1])] = Find(triangles[i + 2]);
        }
        var size = new Dictionary<int, int>();
        foreach (var v in Enumerable.Range(0, donor.Length).Where(v => cloth[v])) size[Find(v)] = size.GetValueOrDefault(Find(v)) + 1;
        var hosts = Enumerable.Range(0, donor.Length).Where(v => cloth[v] && size[Find(v)] >= largest).ToArray();
        if (hosts.Length == 0) return fitted;
        var tree = new OutfitSwapKdTree(hosts.SelectMany(v => new[] { donor[v].X, donor[v].Y, donor[v].Z }).ToArray(), 3);
        var result = (Vector3[])fitted.Clone();
        Span<int> index = stackalloc int[1];
        Span<float> distance = stackalloc float[1];
        foreach (var part in Enumerable.Range(0, donor.Length).Where(v => candidates[v] && size[Find(v)] < largest).GroupBy(Find))
        {
            // The part's nearest point to the host cloth decides where it hangs from.
            var best = (Distance: float.MaxValue, Host: -1);
            foreach (var v in part)
                if (tree.Nearest([donor[v].X, donor[v].Y, donor[v].Z], 1, index, distance) > 0 && distance[0] < best.Distance)
                    best = (distance[0], hosts[index[0]]);
            if (best.Distance >= reach) continue;
            var anchor = part.MinBy(v => Vector3.Distance(donor[v], donor[best.Host]));
            var shift = fitted[best.Host] - donor[best.Host] - (fitted[anchor] - donor[anchor]);
            foreach (var v in part) result[v] = fitted[v] + shift;
        }
        return result;
    }

    /// <summary>The cloth of every connected part whose points are mostly (<paramref name="share"/>) marked.</summary>
    private static bool[] WholeParts(bool[] marked, bool[] cloth, IReadOnlyList<int> triangles, float share)
    {
        var parent = Enumerable.Range(0, marked.Length).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        for (var i = 0; i + 2 < triangles.Count; i += 3)
            if (cloth[triangles[i]] && cloth[triangles[i + 1]] && cloth[triangles[i + 2]])
            {
                parent[Find(triangles[i])] = Find(triangles[i + 1]);
                parent[Find(triangles[i + 1])] = Find(triangles[i + 2]);
            }
        var counts = new Dictionary<int, (int Marked, int All)>();
        for (var v = 0; v < marked.Length; v++)
        {
            if (!cloth[v]) continue;
            var (m, all) = counts.GetValueOrDefault(Find(v));
            counts[Find(v)] = (m + (marked[v] ? 1 : 0), all + 1);
        }
        return Enumerable.Range(0, marked.Length).Select(v => cloth[v] && counts[Find(v)] is var (m, all) && m >= share * all).ToArray();
    }

    /// <summary>
    /// A bare foot in a shoe, and the shoe's inner layers, bend with the shoe's bottom: each point takes the bone weights
    /// of the lowest shoe surface straight below it. Outfits often weight toes and the insole mostly to the toe bone and
    /// a heel's rigid sole mostly to the foot; on a body whose animations bend the toes further than the outfit's own class
    /// does (a battle stance), the toes then go through the sole.
    /// </summary>
    private static OutfitSwapMesh BendWithShoes(OutfitSwapMesh mesh, float[] feet, float reach = 0.03f)
    {
        var shoe = new List<int>();
        var bare = new HashSet<int>();
        foreach (var section in mesh.Sections)
        {
            var skin = OutfitSwapBodies.IsSkin(mesh.Materials[section.Material]);
            for (var i = 0; i + 2 < section.Triangles.Length; i += 3)
            {
                int a = section.Triangles[i], b = section.Triangles[i + 1], c = section.Triangles[i + 2];
                if (skin) { foreach (var v in new[] { a, b, c }) if (feet[v] >= 0.5f) bare.Add(v); }
                else if (feet[a] >= 0.1f && feet[b] >= 0.1f && feet[c] >= 0.1f)
                {
                    shoe.AddRange([a, b, c]);
                    bare.UnionWith([a, b, c]);
                }
            }
        }
        if (shoe.Count == 0 || bare.Count == 0) return mesh;
        var p = mesh.Positions;
        var tree = new OutfitSwapKdTree(Enumerable.Range(0, shoe.Count / 3)
            .SelectMany(t => ((p[shoe[t * 3]] + p[shoe[t * 3 + 1]] + p[shoe[t * 3 + 2]]) / 3) is var m ? new[] { m.X, m.Y, m.Z } : []).ToArray(), 3);
        var joints = (int[])mesh.Joints.Clone();
        var weights = (float[])mesh.Weights.Clone();
        var near = new List<int>();
        foreach (var v in bare)
        {
            // The shoe's bottom straight below: the furthest triangle a ray downwards meets.
            tree.Within([p[v].X, p[v].Y - reach / 2, p[v].Z], reach, near);
            var best = 0f;
            (int Triangle, float U, float W)? hit = null;
            foreach (var t in near)
            {
                if (shoe[t * 3] == v || shoe[t * 3 + 1] == v || shoe[t * 3 + 2] == v) continue;
                Vector3 a = p[shoe[t * 3]], e1 = p[shoe[t * 3 + 1]] - a, e2 = p[shoe[t * 3 + 2]] - a;
                var q = Vector3.Cross(-Vector3.UnitY, e2);
                var det = Vector3.Dot(e1, q);
                if (MathF.Abs(det) < 1e-12f) continue;
                var s0 = p[v] - a;
                var u = Vector3.Dot(s0, q) / det;
                var r = Vector3.Cross(s0, e1);
                var w = Vector3.Dot(-Vector3.UnitY, r) / det;
                var along = Vector3.Dot(e2, r) / det;
                if (u < 0 || w < 0 || u + w > 1 || along <= 1e-4f || along > reach || along <= best) continue;
                best = along;
                hit = (t, u, w);
            }
            if (hit is not { } found) continue;
            var sum = new Dictionary<int, float>();
            var corners = new[] { (shoe[found.Triangle * 3], 1 - found.U - found.W), (shoe[found.Triangle * 3 + 1], found.U), (shoe[found.Triangle * 3 + 2], found.W) };
            foreach (var (corner, share) in corners)
                for (var k = 0; k < 4; k++)
                    if (mesh.Weights[corner * 4 + k] > 0)
                        sum[mesh.Joints[corner * 4 + k]] = sum.GetValueOrDefault(mesh.Joints[corner * 4 + k]) + mesh.Weights[corner * 4 + k] * share;
            var top = sum.OrderByDescending(pair => pair.Value).Take(4).ToArray();
            var total = top.Sum(pair => pair.Value);
            if (total <= 0) continue;
            for (var k = 0; k < 4; k++)
            {
                joints[v * 4 + k] = k < top.Length ? top[k].Key : 0;
                weights[v * 4 + k] = k < top.Length ? top[k].Value / total : 0;
            }
        }
        return new OutfitSwapMesh
        {
            Positions = mesh.Positions, Normals = mesh.Normals, Tangents = mesh.Tangents, Uv0 = mesh.Uv0, Uv1 = mesh.Uv1, Colours = mesh.Colours,
            Joints = joints, Weights = weights, Sections = mesh.Sections, Materials = mesh.Materials, BoneNames = mesh.BoneNames,
            BonePositions = mesh.BonePositions, BoneParents = mesh.BoneParents, OwnPictureSkin = mesh.OwnPictureSkin,
        };
    }

    /// <summary>
    /// Bare feet and hands keep the outfit's own shape (they are not laid on the new body), so the new body's skin
    /// picture, laid out for its own toes and fingers, cannot draw them: their skin triangles move to a copy of the skin
    /// material that keeps the outfit's own picture (<see cref="OutfitSwapMesh.OwnPictureSkin"/>).
    /// </summary>
    private static OutfitSwapPiece OwnPictureFeetAndHands(OutfitSwapPiece piece, float[] feet, bool allSkin = false)
    {
        var mesh = piece.Mesh;
        var features = OutfitSwapFitting.Features(mesh, Enumerable.Range(0, mesh.VertexCount).ToArray());
        bool Held(int v) => allSkin || v < feet.Length && feet[v] >= 0.5f || features[v * OutfitSwapFitting.FeatureCount + OutfitSwapFitting.LeftHand] +
            features[v * OutfitSwapFitting.FeatureCount + OutfitSwapFitting.RightHand] >= 0.5f;
        var materials = mesh.Materials.ToList();
        var pieceMaterials = piece.Materials.ToList();
        var sections = new List<OutfitSwapSection>();
        var own = new HashSet<int>();
        foreach (var section in mesh.Sections)
        {
            if (!OutfitSwapBodies.IsSkin(mesh.Materials[section.Material]) || mesh.NativeSkin.Contains(section.Material)) { sections.Add(section); continue; }
            var kept = new List<int>();
            var held = new List<int>();
            var t = section.Triangles;
            for (var i = 0; i + 2 < t.Length; i += 3)
                (Held(t[i]) && Held(t[i + 1]) && Held(t[i + 2]) ? held : kept).AddRange([t[i], t[i + 1], t[i + 2]]);
            if (held.Count == 0) { sections.Add(section); continue; }
            if (kept.Count > 0) sections.Add(new OutfitSwapSection(section.Material, kept.ToArray()));
            var copy = materials.Count;
            materials.Add(mesh.Materials[section.Material]);
            if (pieceMaterials.FirstOrDefault(material => material.Slot == section.Material) is { } original)
                pieceMaterials.Add(original with { Slot = copy });
            sections.Add(new OutfitSwapSection(copy, held.ToArray()));
            own.Add(copy);
        }
        if (own.Count == 0) return piece;
        return piece with
        {
            Materials = pieceMaterials,
            Mesh = new OutfitSwapMesh
            {
                Positions = mesh.Positions, Normals = mesh.Normals, Tangents = mesh.Tangents, Uv0 = mesh.Uv0, Uv1 = mesh.Uv1,
                Colours = mesh.Colours, Joints = mesh.Joints, Weights = mesh.Weights, Sections = sections, Materials = materials,
                BoneNames = mesh.BoneNames, BonePositions = mesh.BonePositions, BoneParents = mesh.BoneParents, OwnPictureSkin = own,
                NativeSkin = mesh.NativeSkin,
            },
        };
    }

    /// <summary>The piece's large connected parts of cloth (layers), each as sections of its triangles.</summary>
    private static IEnumerable<OutfitSwapSection[]> Layers(OutfitSwapMesh mesh, int minimumTriangles = 300)
    {
        var parent = Enumerable.Range(0, mesh.VertexCount).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        var cloth = mesh.Sections.Where(section => !OutfitSwapBodies.IsSkin(mesh.Materials[section.Material])).ToArray();
        foreach (var section in cloth)
            for (var i = 0; i + 2 < section.Triangles.Length; i += 3)
            {
                parent[Find(section.Triangles[i])] = Find(section.Triangles[i + 1]);
                parent[Find(section.Triangles[i + 1])] = Find(section.Triangles[i + 2]);
            }
        var groups = new Dictionary<int, Dictionary<int, List<int>>>();
        foreach (var section in cloth)
            for (var i = 0; i + 2 < section.Triangles.Length; i += 3)
            {
                var root = Find(section.Triangles[i]);
                if (!groups.TryGetValue(root, out var bySection)) groups[root] = bySection = [];
                if (!bySection.TryGetValue(section.Material, out var list)) bySection[section.Material] = list = [];
                list.AddRange([section.Triangles[i], section.Triangles[i + 1], section.Triangles[i + 2]]);
            }
        foreach (var group in groups.Values.Where(group => group.Values.Sum(list => list.Count) / 3 >= minimumTriangles))
            yield return group.Select(pair => new OutfitSwapSection(pair.Key, pair.Value.ToArray())).ToArray();
    }

    /// <summary>The height of the outfit's ankle joint (its foot bone), or null when it has none.</summary>
    private static float? DonorAnkle(OutfitSwapMesh mesh) =>
        Array.IndexOf(mesh.BoneNames, "bip001-l-foot") is var foot and >= 0 ? mesh.BonePositions[foot].Y : null;

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
