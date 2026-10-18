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

    private void OpenParticleEditor(ParticleEditorRequest request, ParticleEffectRecipe recipe, Func<ParticleEffectRecipe, Task<string>>? save)
    {
        var window = new ParticleEditorWindow(request, recipe, Path.Combine(_projectRoot, "cache", "webview2"), ParticleTextureRoot,
            _effectiveAppearanceTheme, _protectionProvider, save,
            _umodelBridge is null || string.IsNullOrWhiteSpace(_libraryRootPath) ? null : FindParticleTexturesAsync, Log)
        {
            Owner = this,
        };
        window.Show();
    }

    /// <summary>
    /// The first texture and blend mode of each effect material, exported once with UModel from the package the asset
    /// index finds the material in, and kept under cache\particle-textures.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ParticleEditorTexture>> FindParticleTexturesAsync(
        IReadOnlyList<string> materials, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, ParticleEditorTexture>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in materials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = Path.Combine(ParticleTextureRoot, SafeFolderName(material));
            var record = Path.Combine(folder, "texture.json");
            try
            {
                if (File.Exists(record) && JsonSerializer.Deserialize<ParticleEditorTexture>(await File.ReadAllTextAsync(record, cancellationToken)) is { } cached)
                {
                    found[material] = cached;
                    continue;
                }
                var search = await _packageIndexStore.SearchAsync(_libraryRootPath!, material, null, _gameMetadataIndex,
                    PackageIndexSearchScope.ObjectName, null, resultLimit: 20);
                var match = search.Matches.FirstOrDefault(item => item.ObjectName.Equals(material, StringComparison.OrdinalIgnoreCase) &&
                                                                  item.ClassName.Contains("material", StringComparison.OrdinalIgnoreCase) &&
                                                                  File.Exists(item.SourcePath));
                if (match is null) continue;
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                var files = await _umodelBridge!.ExportNamedObjectAsync(match.SourcePath, material, Path.Combine(folder, "export"), cancellationToken);
                var texture = ParticleMaterialTexture(files, material);
                var blend = files.Where(path => path.EndsWith(".props.txt", StringComparison.OrdinalIgnoreCase))
                    .Any(path => File.ReadAllText(path).Contains("BLEND_Additive", StringComparison.OrdinalIgnoreCase)) ? "additive" : "translucent";
                var result = new ParticleEditorTexture(texture is null ? null : Path.GetRelativePath(ParticleTextureRoot, texture), blend);
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(record, JsonSerializer.Serialize(result), cancellationToken);
                found[material] = result;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                                  Core.External.UmodelBridgeException)
            {
                Log($"Particle material texture not found: material={material}, error={exception.Message}");
            }
        }
        return found;
    }

    /// <summary>The material's first texture as exported: UModel lists a material's textures in its .mat file.</summary>
    private static string? ParticleMaterialTexture(IReadOnlyList<string> files, string material)
    {
        var mat = files.FirstOrDefault(path => Path.GetFileName(path).Equals(material + ".mat", StringComparison.OrdinalIgnoreCase));
        var pngs = files.Where(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (mat is not null)
            foreach (var line in File.ReadLines(mat))
            {
                var value = line.Split('=', 2) is [_, var name] ? name.Trim() : null;
                if (value is null) continue;
                var png = pngs.FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(value, StringComparison.OrdinalIgnoreCase));
                if (png is not null) return png;
            }
        return pngs.FirstOrDefault();
    }

    private static string SafeFolderName(string value) => Regex.Replace(value, @"[^A-Za-z0-9_.-]", "_");
}
