using Arkive.Core.Assets;
using Arkive.Core.Mods;
using Arkive.Core.Textures;

namespace Arkive.App;

/// <summary>
/// Recolour this model: one step from the model viewer to a recolour of a boss (or any model) with everything that
/// gives it colour: its body, parts and weapons' pictures and materials, and every effect of its skills (all its
/// forms) and of its look.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Adds a model's pictures, materials and effects to the mod open in Edit mods (or a new mod named after it), then
    /// opens Recolour on all of them, starting from white with gold highlights. Says what it did.
    /// </summary>
    private async Task<string> RecolourModelAsync(Arkive.Core.Viewer.ModelViewerRequest request, string classFile)
    {
        if (_gameMetadataIndex is null || string.IsNullOrEmpty(_libraryRootPath))
            return "Load the game DB mapping and the Asset Library first: they find the model's packages and effects.";
        var offer = await SkillPreview().OfferAsync(request, CancellationToken.None);
        var name = offer?.Classes.FirstOrDefault(item => item.File.Equals(classFile, StringComparison.OrdinalIgnoreCase))?.Name
                   ?? offer?.Classes.FirstOrDefault()?.Name ?? request.ObjectName;
        if (_authoringDraft is null)
        {
            _authoringDraft = await _modAuthoringWorkspace.CreateAsync(new ArkiveModMetadata($"{name} recolour", "1.0.0"));
            LoadAuthoringMetadata();
        }
        WorkshopTab.IsSelected = true;

        // The body, parts and weapons: their colour pictures and their materials.
        var packages = await SkillPreview().ModelPackagesAsync(request, classFile, CancellationToken.None);
        var documents = await _packageIndexStore.LoadPackagesByAliasAsync(_libraryRootPath, packages, _gameMetadataIndex);
        var modelRows = documents.SelectMany(document => document.Assets
                .Where(asset => AssetTypes.IndexedKind(asset.Kind, asset.ClassName) switch
                {
                    AssetKind.Texture => TextureNames.IsColour(asset.ObjectName),
                    AssetKind.Material => asset.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase),
                    _ => false,
                })
                .Select(asset => new LibraryAssetRow(document.SourcePath, document.PackageAlias, document.PhysicalPackageName,
                    document.Assets.Count, asset.ExportIndex, AssetTypes.IndexedKind(asset.Kind, asset.ClassName), asset.ClassName,
                    asset.ObjectName, asset.SerialSize, asset.SerialOffset, [])))
            .ToArray();

        // Every skill effect of every form (its action files), and the look's always-on effects.
        var files = offer?.Npc == true ? offer.Classes.Select(item => item.File).ToArray() : [classFile];
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sharedCount = 0;
        foreach (var file in files)
        {
            var (effects, shared) = await SkillPreview().EffectsForModAsync(request, file, null, CancellationToken.None);
            own.UnionWith(effects);
            sharedCount += shared.Count;
        }
        var byPackage = own.Where(effect => effect.Contains('.'))
            .GroupBy(effect => effect[..effect.IndexOf('.')], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(effect => effect[(effect.IndexOf('.') + 1)..])
                .ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var effectDocuments = await _packageIndexStore.LoadPackagesByAliasAsync(_libraryRootPath, byPackage.Keys, _gameMetadataIndex);
        var effectRows = effectDocuments.SelectMany(document => document.Assets
                .Where(asset => AssetTypes.IndexedKind(asset.Kind, asset.ClassName) == AssetKind.ParticleSystem &&
                                byPackage.TryGetValue(document.PackageAlias, out var names) && names.Contains(asset.ObjectName))
                .Select(asset => new LibraryAssetRow(document.SourcePath, document.PackageAlias, document.PhysicalPackageName,
                    document.Assets.Count, asset.ExportIndex, AssetKind.ParticleSystem, asset.ClassName, asset.ObjectName,
                    asset.SerialSize, asset.SerialOffset, [])))
            .ToArray();
        var rows = modelRows.Concat(effectRows)
            .DistinctBy(row => (row.PhysicalPackageName.ToUpperInvariant(), row.ObjectName.ToUpperInvariant(), row.ExportIndex))
            .ToArray();
        if (rows.Length == 0) return $"Arkive found nothing to recolour for {name} in the Asset Library.";

        var edits = new List<ArkiveModLocalEdit>();
        var materialOnly = 0;
        var failed = new List<string>();
        await RunAuthoringExtractionAsync(async cancellationToken =>
        {
            var session = await CreateAuthoringAddSessionAsync(cancellationToken);
            for (var index = 0; index < rows.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportAuthoringExtraction($"Adding {name}'s pictures, materials and effects", index, rows.Length);
                try { edits.Add(await AddAuthoringAssetAsync(rows[index], session)); }
                catch (ArkiveModRefusalException exception) when (exception.Code == "AMM060")
                {
                    // Its colour is in its materials: add the ones in its own package (no other effect family shares them).
                    materialOnly++;
                    try
                    {
                        var clean = await _arkiveModApply.ResolveCleanPackageAsync(rows[index].SourcePath, cancellationToken);
                        edits.AddRange(await AddEffectMaterialsAsync(clean, rows[index].PackageAlias, rows[index].ObjectName, null,
                            session, null, ownPackageOnly: true, cancellationToken));
                    }
                    catch (Exception inner) when (ArkiveModApplyService.IsOperational(inner))
                    {
                        Log($"Recolour model: materials of {rows[index].ObjectName} not added: {inner.Message}");
                    }
                }
                catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
                {
                    failed.Add($"{rows[index].ObjectName}: {exception.Message}");
                }
            }
            ReportAuthoringExtraction("Ready to recolour", rows.Length, rows.Length);
        });
        foreach (var failure in failed) Log($"Recolour model: not added {failure}");
        if (edits.Count == 0) return $"None of {name}'s {rows.Length:N0} assets could be added. Diagnostics says why.";

        Activate();
        await RecolourAuthoringAssetsAsync(edits, new TextureRecolour(1, 0.97f, 0.9f)
        {
            Style = TextureRecolourStyle.TwoTone, HighlightRed = 1, HighlightGreen = 0.78f, HighlightBlue = 0.36f,
            HighlightAmount = 0.2f, Lift = 0.55f,
        }, $"{name} and its effects");
        var text = $"Added {edits.Count:N0} of {name}'s assets to {_authoringDraft?.Metadata.Name}: " +
                   $"{edits.Count(edit => edit.Kind == UnifiedPackageAssetKind.Texture2D):N0} pictures, " +
                   $"{edits.Count(edit => edit.Kind == UnifiedPackageAssetKind.MaterialInstanceConstant):N0} materials, " +
                   $"{edits.Count(edit => edit.Kind == UnifiedPackageAssetKind.ParticleSystem):N0} effects. " + AuthoringStatusText.Text;
        if (materialOnly > 0) text += $" {materialOnly:N0} effects take their colour only from their materials; their own materials were added instead.";
        if (sharedCount > 0) text += $" Effects from packages many skills share were left out, so other bosses keep theirs.";
        if (failed.Count > 0) text += $" {failed.Count:N0} could not be added (Diagnostics lists them).";
        return text;
    }
}
