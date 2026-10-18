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

public static class ArkiveModSelectionService
{
    public static async Task<IReadOnlyList<ArkiveModSelectionChange>> SynchronizeAsync(
        ArkiveModLibrary modLibrary,
        ArkiveModEditWorkspaces edits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modLibrary);
        ArgumentNullException.ThrowIfNull(edits);
        var library = await modLibrary.LoadAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(edits, cancellationToken).ConfigureAwait(false);
        var resolution = ArkiveModConflictResolver.Resolve(library.Mods);
        var changes = new List<ArkiveModSelectionChange>();
        foreach (var mod in library.Mods)
        {
            foreach (var binding in mod.Bindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var local = snapshot.Find(binding.EditId);
                if (local is null) continue;
                var include = resolution.OutcomeFor(mod.ModId, binding.AssetId) == ArkiveModAssetOutcome.Winning;
                if (local.Included == include) continue;
                await SetIncludedAsync(edits, local, include, cancellationToken).ConfigureAwait(false);
                changes.Add(new ArkiveModSelectionChange(
                    mod.Manifest.Name, local.Kind, local.ObjectName, local.EditId, include));
            }
        }
        return changes;
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

    public static async Task SetIncludedAsync(
        ArkiveModEditWorkspaces edits,
        ArkiveModLocalEdit edit,
        bool included,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(edit);
        switch (edit.Entry)
        {
            case GuidedTextureModProjectEntry texture:
                await edits.Textures.SetIncludedAsync(texture.ManifestPath, included, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case SkeletalMeshEditWorkspaceEntry mesh:
                await edits.SkeletalMeshEdits.SetIncludedAsync(mesh, included, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case StaticMeshEditEntry mesh:
                await edits.StaticMeshEdits.SetIncludedAsync(mesh, included, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case MaterialInstanceEditEntry material:
                await edits.MaterialEdits.SetIncludedAsync(material, included, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case Particles.ParticleEffectEditEntry particle:
                await edits.ParticleEdits.SetIncludedAsync(particle, included, cancellationToken).ConfigureAwait(false);
                break;
            case InterfaceMovieEditEntry movie when edits.InterfaceMovies is { } movies:
                await movies.SetIncludedAsync(movie, included, cancellationToken).ConfigureAwait(false);
                break;
        }
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
            case InterfaceMovieEditEntry movie when edits.InterfaceMovies is { } movies:
                await movies.DeleteAsync(movie, cancellationToken).ConfigureAwait(false);
                break;
        }
    }
}
