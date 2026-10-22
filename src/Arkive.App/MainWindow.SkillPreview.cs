using System.IO;
using Arkive.Core.Assets;
using Arkive.Core.Mods;

namespace Arkive.App;

/// <summary>The model viewer's skill preview (see <see cref="SkillPreviewService"/>).</summary>
public partial class MainWindow
{
    private SkillPreviewService? _skillPreview;

    private SkillPreviewService SkillPreview() => _skillPreview ??= new SkillPreviewService(
        _packageIndexStore, () => _libraryRootPath, () => _gameMetadataIndex, _reader, _protectionProvider,
        ViewerAnimations(), ModelViewerService(), ModelViewerService().Cache.RootDirectory, ParticleAssets(), Log);

    private Task<SkillPreviewOffer?> FindSkillPreviewAsync(Arkive.Core.Viewer.ModelViewerRequest request, CancellationToken cancellationToken) =>
        _umodelBridge is null ? Task.FromResult<SkillPreviewOffer?>(null) : SkillPreview().OfferAsync(request, cancellationToken);

    /// <summary>
    /// Adds a model's skill effects (one skill's, or all its skills' and its look's when <paramref name="skillId"/> is
    /// null) to the mod open in Edit mods, as effect working copies to tint there. Effects from packages many skills
    /// share are left out and counted, and effects whose look comes only from their materials are counted too.
    /// </summary>
    private async Task<string> AddSkillEffectsToModAsync(Arkive.Core.Viewer.ModelViewerRequest request, string classFile, int? skillId)
    {
        if (_authoringDraft is null) return "Open Edit mods and choose or create the mod to add the effects to, then try again.";
        if (_gameMetadataIndex is null || string.IsNullOrEmpty(_libraryRootPath))
            return "Load the game DB mapping and the Asset Library first: they find each effect's package.";
        var (own, shared) = await SkillPreview().EffectsForModAsync(request, classFile, skillId, CancellationToken.None);
        if (own.Count == 0)
            return shared.Count == 0 ? "This has no effects to add." :
                $"Its {shared.Count:N0} effect(s) all come from effect packages many skills share, so none were added.";
        var byPackage = own.GroupBy(effect => effect[..effect.IndexOf('.')], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(effect => effect[(effect.IndexOf('.') + 1)..])
                .ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var documents = await _packageIndexStore.LoadPackagesByAliasAsync(_libraryRootPath, byPackage.Keys, _gameMetadataIndex);
        var rows = documents.SelectMany(document => document.Assets
                .Where(asset => AssetTypes.IndexedKind(asset.Kind, asset.ClassName) == AssetKind.ParticleSystem &&
                                byPackage.TryGetValue(document.PackageAlias, out var names) && names.Contains(asset.ObjectName))
                .Select(asset => new LibraryAssetRow(document.SourcePath, document.PackageAlias, document.PhysicalPackageName,
                    document.Assets.Count, asset.ExportIndex, AssetKind.ParticleSystem, asset.ClassName, asset.ObjectName,
                    asset.SerialSize, asset.SerialOffset, [])))
            .DistinctBy(row => (row.PhysicalPackageName.ToUpperInvariant(), row.ObjectName.ToUpperInvariant()))
            .ToArray();
        int added = 0, already = 0, unchangeable = 0;
        await RunAuthoringExtractionAsync(async cancellationToken =>
        {
            var session = await CreateAuthoringAddSessionAsync(cancellationToken);
            ReportAuthoringExtraction("Adding the model's effects", 0, rows.Length);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.Existing.ContainsKey(AuthoringAssetKey(row.SourcePath, row.ObjectName))) already++;
                else
                    try
                    {
                        await AddAuthoringAssetAsync(row, session);
                        added++;
                    }
                    catch (ArkiveModRefusalException exception) when (exception.Code == "AMM060")
                    {
                        unchangeable++;
                    }
                ReportAuthoringExtraction("Adding the model's effects", added + already + unchangeable, rows.Length);
            }
        });
        var missing = own.Count - rows.Length;
        Log($"Skill effects added to {_authoringDraft?.Metadata.Name}: class={classFile}, skill={skillId?.ToString() ?? "all"}, " +
            $"added={added}, already={already}, materialOnly={unchangeable}, shared={shared.Count}, notIndexed={missing}.");
        var text = $"Added {added:N0} effect(s) to {_authoringDraft?.Metadata.Name ?? "the mod"}";
        if (already > 0) text += $" ({already:N0} were already in it)";
        text += ". Open them in Edit mods to tint them, then save the mod.";
        if (unchangeable > 0) text += $" {unchangeable:N0} take their look only from their materials, so there is nothing to tint in them.";
        if (shared.Count > 0) text += $" {shared.Count:N0} from effect packages many skills share were left out: changing them would change other skills and bosses too.";
        if (missing > 0) text += $" {missing:N0} were not found in the Asset Library's index.";
        return text;
    }

    private Task<SkillPlayback> PrepareSkillPreviewAsync(Arkive.Core.Viewer.ModelViewerRequest request, string classFile, int skillId,
        int branch, Arkive.Core.Viewer.ModelViewerEdits? edits, Action<string, double?> progress, CancellationToken cancellationToken) =>
        SkillPreview().PrepareAsync(request, classFile, skillId, branch, edits, progress, cancellationToken);
}
