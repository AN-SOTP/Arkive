using Arkive.Core.Interface;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.StaticMeshes;

namespace Arkive.Core.Mods;

public sealed record ArkiveModSelectionChange(
    string ModName,
    UnifiedPackageAssetKind Kind,
    string ObjectName,
    string EditId,
    bool Included);

/// <param name="Snapshot">The working files as they are after the changes, for a caller that reads them next.</param>
public sealed record ArkiveModSelectionResult(
    IReadOnlyList<ArkiveModSelectionChange> Changes,
    ArkiveModEditSnapshot Snapshot);

public static class ArkiveModSelectionService
{
    public static async Task<IReadOnlyList<ArkiveModSelectionChange>> SynchronizeAsync(
        ArkiveModLibrary modLibrary,
        ArkiveModEditWorkspaces edits,
        CancellationToken cancellationToken = default) =>
        (await SynchronizeWithSnapshotAsync(modLibrary, edits, cancellationToken).ConfigureAwait(false)).Changes;

    public static async Task<ArkiveModSelectionResult> SynchronizeWithSnapshotAsync(
        ArkiveModLibrary modLibrary,
        ArkiveModEditWorkspaces edits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modLibrary);
        ArgumentNullException.ThrowIfNull(edits);
        var library = await modLibrary.LoadAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(edits, cancellationToken).ConfigureAwait(false);
        var resolution = ArkiveModConflictResolver.Resolve(library.Mods);
        var pending = new List<(ArkiveModLibraryEntry Mod, ArkiveModLocalEdit Local, bool Include)>();
        foreach (var mod in library.Mods)
        {
            foreach (var binding in mod.Bindings)
            {
                var local = snapshot.Find(binding.EditId);
                if (local is null) continue;
                var include = resolution.OutcomeFor(mod.ModId, binding.AssetId) == ArkiveModAssetOutcome.Winning;
                if (local.Included != include) pending.Add((mod, local, include));
            }
        }
        return await WriteAsync(edits, snapshot, pending, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="SynchronizeWithSnapshotAsync"/> after only <paramref name="changedModIds"/> were turned on or off or
    /// moved in priority. Only the targets those mods change can get another winner, so only the working files of
    /// the mods competing there are read again (from disk, not taken from <paramref name="known"/>) and rewritten; the
    /// rest of <paramref name="known"/> is handed on. When one of those working files is not in
    /// <paramref name="known"/> or can no longer be read, every working file is read, as the full pass does.
    /// </summary>
    public static async Task<ArkiveModSelectionResult> SynchronizeChangedAsync(
        ArkiveModLibrary modLibrary,
        ArkiveModEditWorkspaces edits,
        ArkiveModEditSnapshot known,
        IReadOnlyCollection<string> changedModIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modLibrary);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(changedModIds);
        var library = await modLibrary.LoadAsync(cancellationToken).ConfigureAwait(false);
        var resolution = ArkiveModConflictResolver.Resolve(library.Mods);
        var changed = changedModIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var affected = library.Mods.Where(mod => changed.Contains(mod.ModId))
            .SelectMany(mod => mod.Bindings.Select(binding => (Mod: mod, Binding: binding)))
            .Concat(resolution.Targets
                .Where(target => target.Contenders.Any(contender => changed.Contains(contender.ModId)))
                .SelectMany(target => target.Contenders)
                .Select(contender => library.Find(contender.ModId) is { } mod &&
                                     mod.BindingFor(contender.Asset.AssetId) is { } binding
                    ? (Mod: mod, Binding: binding)
                    : default)
                .Where(item => item.Mod is not null))
            .DistinctBy(item => item.Binding.EditId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var reloaded = new List<ArkiveModLocalEdit>(affected.Length);
        var pending = new List<(ArkiveModLibraryEntry Mod, ArkiveModLocalEdit Local, bool Include)>();
        foreach (var (mod, binding) in affected)
        {
            if (known.Find(binding.EditId) is not { } cached ||
                await ArkiveModEditSnapshot.ReloadAsync(edits, cached, cancellationToken).ConfigureAwait(false) is not { } local)
                return await SynchronizeWithSnapshotAsync(modLibrary, edits, cancellationToken).ConfigureAwait(false);
            reloaded.Add(local);
            var include = resolution.OutcomeFor(mod.ModId, binding.AssetId) == ArkiveModAssetOutcome.Winning;
            if (local.Included != include) pending.Add((mod, local, include));
        }
        return await WriteAsync(edits, known.Replace(reloaded), pending, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ArkiveModSelectionResult> WriteAsync(
        ArkiveModEditWorkspaces edits,
        ArkiveModEditSnapshot snapshot,
        IReadOnlyList<(ArkiveModLibraryEntry Mod, ArkiveModLocalEdit Local, bool Include)> pending,
        CancellationToken cancellationToken)
    {
        // Each change rewrites one working file's own record, so they are written side by side: turning a pack of
        // large mods on or off changes over a thousand.
        var written = new ArkiveModLocalEdit[pending.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, pending.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var (_, local, include) = pending[index];
                var entry = await SetIncludedEntryAsync(edits, local, include, token).ConfigureAwait(false);
                written[index] = entry is null ? local : local with { Included = include, Entry = entry };
            }).ConfigureAwait(false);
        var changes = pending.Select(item => new ArkiveModSelectionChange(
            item.Mod.Manifest.Name, item.Local.Kind, item.Local.ObjectName, item.Local.EditId, item.Include)).ToArray();
        return new ArkiveModSelectionResult(changes, snapshot.Replace(written));
    }

    public static async Task ReleaseAsync(
        ArkiveModEditWorkspaces edits,
        IReadOnlyCollection<ArkiveModReleasedEdit> released,
        CancellationToken cancellationToken = default)
    {
        if (released.Count == 0) return;
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(edits, cancellationToken).ConfigureAwait(false);
        foreach (var item in released)
        {
            if (snapshot.Find(item.EditId) is not { } local) continue;
            await SetIncludedAsync(edits, local, false, cancellationToken).ConfigureAwait(false);
            if (!item.Delete) continue;
            try
            {
                await DeleteEditAsync(edits, local, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static Task SetIncludedAsync(
        ArkiveModEditWorkspaces edits,
        ArkiveModLocalEdit edit,
        bool included,
        CancellationToken cancellationToken = default) =>
        SetIncludedEntryAsync(edits, edit, included, cancellationToken);

    /// <returns>The working file's record as written, or null for a kind this workspace set does not edit.</returns>
    private static async Task<object?> SetIncludedEntryAsync(
        ArkiveModEditWorkspaces edits,
        ArkiveModLocalEdit edit,
        bool included,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(edit);
        return edit.Entry switch
        {
            GuidedTextureModProjectEntry texture => await edits.Textures
                .SetIncludedAsync(texture.ManifestPath, included, cancellationToken).ConfigureAwait(false),
            SkeletalMeshEditWorkspaceEntry mesh => await edits.SkeletalMeshEdits
                .SetIncludedAsync(mesh, included, cancellationToken).ConfigureAwait(false),
            StaticMeshEditEntry mesh => await edits.StaticMeshEdits
                .SetIncludedAsync(mesh, included, cancellationToken).ConfigureAwait(false),
            MaterialInstanceEditEntry material => await edits.MaterialEdits
                .SetIncludedAsync(material, included, cancellationToken).ConfigureAwait(false),
            Particles.ParticleEffectEditEntry particle => await edits.ParticleEdits
                .SetIncludedAsync(particle, included, cancellationToken).ConfigureAwait(false),
            Physics.PhysicsAssetEditEntry physics => await edits.PhysicsEdits
                .SetIncludedAsync(physics, included, cancellationToken).ConfigureAwait(false),
            InterfaceMovieEditEntry movie when edits.InterfaceMovies is { } movies => await movies
                .SetIncludedAsync(movie, included, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    private static async Task DeleteEditAsync(
        ArkiveModEditWorkspaces edits,
        ArkiveModLocalEdit edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(edit);
        switch (edit.Entry)
        {
            case GuidedTextureModProjectEntry texture:
                await edits.Textures.DeleteAsync(texture, cancellationToken).ConfigureAwait(false);
                break;
            case SkeletalMeshEditWorkspaceEntry mesh:
                await edits.SkeletalMeshEdits.DeleteAsync(mesh, cancellationToken).ConfigureAwait(false);
                break;
            case StaticMeshEditEntry mesh:
                await edits.StaticMeshEdits.DeleteAsync(mesh, cancellationToken).ConfigureAwait(false);
                break;
            case MaterialInstanceEditEntry material:
                await edits.MaterialEdits.DeleteAsync(material, cancellationToken).ConfigureAwait(false);
                break;
            case Particles.ParticleEffectEditEntry particle:
                await edits.ParticleEdits.DeleteAsync(particle, cancellationToken).ConfigureAwait(false);
                break;
            case Physics.PhysicsAssetEditEntry physics:
                await edits.PhysicsEdits.DeleteAsync(physics, cancellationToken).ConfigureAwait(false);
                break;
            case InterfaceMovieEditEntry movie when edits.InterfaceMovies is { } movies:
                await movies.DeleteAsync(movie, cancellationToken).ConfigureAwait(false);
                break;
        }
    }
}
