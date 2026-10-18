using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Mods;
using Arkive.Core.Particles;

namespace Arkive.App;

public partial class MainWindow
{
    private string ParticleTextureRoot => Path.Combine(_projectRoot, "cache", "particle-textures");

    private static bool IsParticleRow(LibraryAssetRow row) =>
        AssetTypes.IndexedKind(row.Kind, row.ClassName) == AssetKind.ParticleSystem;

    /// <summary>Opens a particle system found in the asset library or Edit mods search, to watch and try changes on.</summary>
    private async void OpenLibraryParticleEditor(LibraryAssetRow row)
    {
        try
        {
            var clean = await _arkiveModApply.ResolveCleanPackageAsync(row.SourcePath);
            OpenParticleEditor(new ParticleEditorRequest(clean, row.PackageAlias, row.ObjectName, null), ParticleEffectRecipe.Unchanged, save: null);
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            StatusText.Text = $"The particle effect could not be opened: {exception.Message}";
            Log($"Particle editor could not open {row.ObjectName}: {exception}");
        }
    }

    /// <summary>Opens an effect of the mod in Edit mods; Save changes writes the settings into its working copy.</summary>
    private void OpenAuthoringParticleEditor(ParticleEffectEditEntry particle)
    {
        var document = particle.Document;
        OpenParticleEditor(
            new ParticleEditorRequest(document.SourcePackagePath, document.PackageAlias, document.SystemObjectName, document.SystemObjectPath),
            document.Recipe,
            async recipe =>
            {
                var hash = await ArkiveModEditSnapshot.HashFileCachedAsync(document.SourcePackagePath);
                if (!hash.Equals(document.SourcePackageSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{document.PhysicalPackageName} changed since this effect was added. Add the effect again.");
                var report = await Task.Run(() => ParticleEffects.Inspect(document.SourcePackagePath, hash,
                    document.SystemObjectName, document.SystemObjectPath, _protectionProvider));
                var patches = ParticleEffects.RecipePatches(report, recipe);
                var current = await _arkiveModEdits.ParticleEdits.LoadEntryAsync(particle.DocumentPath);
                await _arkiveModEdits.ParticleEdits.SaveAsync(current with { Document = current.Document with { Recipe = recipe, Patches = patches } });
                Log($"Particle effect settings saved: effect={document.SystemObjectName}, recipe={recipe}, tables={patches.Count}.");
                await RefreshModAuthoringAsync();
                return patches.Count == 0
                    ? "Saved: the effect is back to the game's own settings."
                    : $"Saved {patches.Count:N0} changed value table(s) into the working copy. Save the mod in Edit mods to keep them.";
            });
    }

    private async void OpenParticleEditor(ParticleEditorRequest request, ParticleEffectRecipe recipe, Func<ParticleEffectRecipe, Task<string>>? save)
    {
        IReadOnlyDictionary<string, string>? packageFiles = null;
        if (!string.IsNullOrWhiteSpace(_libraryRootPath) && _umodelBridge is not null)
        {
            try { packageFiles = await ParticleAssets().PackageFilesAsync(CancellationToken.None); }
            catch (Exception exception) when (exception is IOException or InvalidDataException) { Log($"Particle editor: package aliases unavailable: {exception.Message}"); }
        }
        var window = new ParticleEditorWindow(request, recipe, Path.Combine(_projectRoot, "cache", "webview2"), ParticleTextureRoot,
            _effectiveAppearanceTheme, _protectionProvider, save,
            _umodelBridge is null || string.IsNullOrWhiteSpace(_libraryRootPath) ? null : FindParticleAssetsAsync, Log)
        {
            Owner = this,
            PackageFiles = packageFiles,
        };
        window.Show();
    }

    private ParticleAssetFinder? _particleAssets;

    /// <summary>Finds and caches (under cache\particle-textures) what effects draw with; shared by every viewer.</summary>
    private ParticleAssetFinder ParticleAssets() => _particleAssets ??= new ParticleAssetFinder(
        _packageIndexStore, () => _libraryRootPath, () => _gameMetadataIndex, _umodelBridge!, _reader, _protectionProvider,
        token => ModelViewerService().ShaderIndexAsync(_libraryRootPath, null, token), ParticleTextureRoot, Log);

    /// <summary>The textures and meshes an effect's emitters draw with, exported once and kept under cache\particle-textures.</summary>
    private Task<ParticleEditorAssets> FindParticleAssetsAsync(ParticleEffectModel model, CancellationToken cancellationToken) =>
        ParticleAssets().FindAsync(model.Emitters, null, cancellationToken);

}
