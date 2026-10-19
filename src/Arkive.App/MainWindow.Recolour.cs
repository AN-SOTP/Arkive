using System.IO;
using System.Windows;
using Arkive.Core.Mods;
using Arkive.Core.Particles;
using Arkive.Core.Textures;

namespace Arkive.App;

public partial class MainWindow
{
    private ArkiveModTintService? _materialTint;

    /// <summary>The one tint for pictures, materials and effects that Recolour selected and the boss and zone helpers use.</summary>
    private IMaterialTint MaterialTint => _materialTint ??= new ArkiveModTintService(
        _arkiveModEdits, _protectionProvider, Path.Combine(_projectRoot, "staging", "material-candidates"));

    private async void AuthoringRecolourButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AuthoringRecolourButton.IsEnabled) return;
        var edits = AuthoringAssetsGrid.SelectedItems.OfType<AuthoringAssetRow>().Select(row => row.Edit)
            .OfType<ArkiveModLocalEdit>().ToArray();
        if (edits.Length == 0)
            edits = AuthoringAssetsGrid.Items.OfType<AuthoringAssetRow>().Select(row => row.Edit).OfType<ArkiveModLocalEdit>().ToArray();
        await RecolourAuthoringAssetsAsync(edits, start: null, subject: null);
    }

    /// <summary>
    /// Opens Recolour selected for some of the open mod's working copies; effect materials it adds join the mod.
    /// </summary>
    private async Task RecolourAuthoringAssetsAsync(IReadOnlyList<ArkiveModLocalEdit> edits, TextureRecolour? start, string? subject)
    {
        if (_authoringDraft is null || edits.Count == 0) return;
        await RunAuthoringOperationAsync(async () =>
        {
            await PersistAuthoringDraftAsync();
            var session = await CreateAuthoringAddSessionAsync(CancellationToken.None);
            IReadOnlyDictionary<string, string>? packageFiles = null;
            var tint = new ArkiveModTintService(_arkiveModEdits, _protectionProvider,
                Path.Combine(_projectRoot, "staging", "material-candidates"))
            {
                AddEffectMaterials = async (effect, token) =>
                {
                    packageFiles ??= string.IsNullOrWhiteSpace(_libraryRootPath) ? null
                        : await _packageIndexStore.PackageFilesByAliasAsync(_libraryRootPath, _gameMetadataIndex, token);
                    var document = effect.Document;
                    return await AddEffectMaterialsAsync(document.SourcePackagePath, document.PackageAlias, document.SystemObjectName,
                        document.SystemObjectPath, session, packageFiles, ownPackageOnly: false, token);
                },
            };
            var window = new RecolourWindow(edits, tint, _effectiveAppearanceTheme, Log, start, subject) { Owner = this };
            if (window.ShowDialog() != true || window.Result is not { } result) return;
            if (result.AddedEdits.Count > 0)
                _authoringDraft = await _modAuthoringWorkspace.SaveAsync(_authoringDraft with
                {
                    EditIds = _authoringDraft.EditIds.Concat(result.AddedEdits.Select(edit => edit.EditId)).Distinct().ToArray(),
                });
            foreach (var skip in result.Skipped) Log($"Recolour skipped {skip.ObjectName}: {skip.Reason}");
            AuthoringStatusText.Text = $"Recoloured {result.Changed:N0} asset(s)" +
                (result.Unchanged > 0 ? $"; {result.Unchanged:N0} had no colours in the chosen range" : string.Empty) +
                (result.AddedEdits.Count > 0 ? $"; added {result.AddedEdits.Count:N0} effect material(s) to the mod" : string.Empty) +
                (result.Skipped.Count > 0
                    ? $"; {result.Skipped.Count:N0} could not be recoloured (first: {result.Skipped[0].ObjectName}: {result.Skipped[0].Reason})"
                    : string.Empty) +
                ". Save the mod to keep it.";
        }, busyTitle: null);
    }

    /// <summary>
    /// Adds an effect's materials (each emitter's material instance) to the mod as working copies, from the clean game
    /// files, reusing copies the mod already has. Base materials have no settings to change and are left out.
    /// </summary>
    /// <param name="ownPackageOnly">Only materials kept in the effect's own package, which no other effect family uses.</param>
    private async Task<IReadOnlyList<ArkiveModLocalEdit>> AddEffectMaterialsAsync(string packagePath, string packageAlias,
        string systemName, string? systemPath, AuthoringAddSession session, IReadOnlyDictionary<string, string>? packageFiles,
        bool ownPackageOnly, CancellationToken cancellationToken)
    {
        var model = await Task.Run(() => ParticleEffectModels.Build(packagePath, packageAlias, systemName, systemPath,
            _protectionProvider, alias => packageFiles?.GetValueOrDefault(alias)), cancellationToken);
        var added = new List<ArkiveModLocalEdit>();
        foreach (var material in model.Emitters.Where(emitter => emitter.Material is not null)
                     .Select(emitter => (Name: emitter.Material!, Package: emitter.MaterialPackage ?? packageAlias))
                     .Distinct())
        {
            var own = material.Package.Equals(packageAlias, StringComparison.OrdinalIgnoreCase);
            if (ownPackageOnly && !own) continue;
            var package = own ? packagePath : packageFiles?.GetValueOrDefault(material.Package);
            if (package is null) continue;
            var key = AuthoringAssetKey(package, material.Name);
            if (session.Existing.TryGetValue(key, out var existing))
            {
                added.Add(existing);
                continue;
            }
            try
            {
                var clean = await _arkiveModApply.ResolveCleanPackageAsync(package, cancellationToken);
                var created = await _modMaterializer.CreateEditableAsync(new ArkiveModAssetSelection(
                    clean, material.Package, "MaterialInstanceConstant", material.Name), cancellationToken, null);
                session.Existing.TryAdd(key, created);
                added.Add(created);
            }
            catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
            {
                // A base Material, or one Arkive cannot edit safely: the effect keeps that part's colour.
                Log($"Effect material {material.Package}.{material.Name} was not added: {exception.Message}");
            }
        }
        return added;
    }
}
