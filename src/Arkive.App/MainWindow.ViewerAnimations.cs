using System.IO;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Proportions;
using Arkive.Core.Viewer;

namespace Arkive.App;

/// <summary>
/// Finds and prepares the animations a viewed mesh can play: a player body's class animations from its base
/// package, or an NPC's or monster's own AnimSet from the mesh's package.
/// </summary>
public partial class MainWindow
{
    private ModelViewerAnimationService? _viewerAnimations;

    private ModelViewerAnimationService ViewerAnimations() => _viewerAnimations ??= new ModelViewerAnimationService(
        _umodelBridge!, Path.Combine(ModelViewerService().Cache.RootDirectory, "animations"));

    private ModelViewerEnvironmentService? _viewerEnvironments;
    private Task<ModelViewerEnvironmentSet?>? _viewerEnvironmentSet;

    /// <summary>
    /// The game's environment cubemaps (package efpbrenvcubemaps), prepared once per session for the viewer's
    /// lighting and backgrounds. Null when the package is not in the indexed library.
    /// </summary>
    private Task<ModelViewerEnvironmentSet?> FindViewerEnvironmentsAsync(CancellationToken cancellationToken) =>
        _viewerEnvironmentSet ??= PrepareViewerEnvironmentsAsync();

    private async Task<ModelViewerEnvironmentSet?> PrepareViewerEnvironmentsAsync()
    {
        try
        {
            if (_umodelBridge is null || string.IsNullOrWhiteSpace(_libraryRootPath)) return null;
            const string probe = "pbr_cubemap_00";
            var found = await _packageIndexStore.SearchAsync(_libraryRootPath, probe, null, _gameMetadataIndex,
                PackageIndexSearchScope.ObjectName, null, resultLimit: 50);
            var match = found.Matches.FirstOrDefault(item => item.ObjectName.Equals(probe, StringComparison.OrdinalIgnoreCase) &&
                                                             item.ClassName.Equals("TextureCube", StringComparison.OrdinalIgnoreCase));
            if (match is null || !File.Exists(match.SourcePath)) return null;
            var catalogue = await Task.Run(() => new AssetCatalogReader(_reader, _protectionProvider).Read(match.SourcePath));
            var names = catalogue.Assets
                .Where(asset => asset.ClassName.Equals("TextureCube", StringComparison.OrdinalIgnoreCase) &&
                                asset.ObjectName.StartsWith("pbr_", StringComparison.OrdinalIgnoreCase))
                .Select(asset => asset.ObjectName).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            _viewerEnvironments ??= new ModelViewerEnvironmentService(_umodelBridge,
                Path.Combine(ModelViewerService().Cache.RootDirectory, "environments"));
            var set = await _viewerEnvironments.PrepareAsync(match.SourcePath, names);
            Log($"Viewer environments ready: {set.Environments.Count} game cubemaps in {set.Directory}.");
            return set;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log($"Viewer environments unavailable: {exception.Message}");
            _viewerEnvironmentSet = null;
            return null;
        }
    }

    /// <summary>Null when the mesh has no animations Arkive can find.</summary>
    private async Task<ModelViewerAnimationSet?> FindViewerAnimationsAsync(ModelViewerRequest request, CancellationToken cancellationToken)
    {
        if (_umodelBridge is null || !request.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) return null;
        var profile = BodyProfiles.ForObjectName(request.ObjectName);
        string? package = null;
        string? animSet = null;
        if (profile is { IsNpc: false } && BodyProfiles.AnimSetName(profile) is { } name &&
            !string.IsNullOrWhiteSpace(_libraryRootPath))
        {
            var found = await _packageIndexStore.SearchAsync(_libraryRootPath, name, null, _gameMetadataIndex,
                PackageIndexSearchScope.ObjectName, null, resultLimit: 50);
            var match = found.Matches.FirstOrDefault(item => item.ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                                             item.ClassName.Equals("animset", StringComparison.OrdinalIgnoreCase));
            if (match is not null && File.Exists(match.SourcePath)) (package, animSet) = (match.SourcePath, name);
        }
        if (package is null)
        {
            // NPCs and monsters keep their AnimSet beside the mesh, named after the package (mn_sden_00_ani).
            var catalogue = await Task.Run(() => new AssetCatalogReader(_reader, _protectionProvider).Read(request.PackagePath), cancellationToken);
            var sets = catalogue.Assets.Where(asset => asset.ClassName.Equals("animset", StringComparison.OrdinalIgnoreCase)).ToArray();
            var own = sets.FirstOrDefault(asset => asset.ObjectName.Equals(request.PackageAlias + "_ani", StringComparison.OrdinalIgnoreCase))
                      ?? sets.FirstOrDefault(asset => asset.ObjectName.EndsWith("_ani", StringComparison.OrdinalIgnoreCase) &&
                                                      !asset.ObjectName.Contains("evt", StringComparison.OrdinalIgnoreCase));
            if (own is null) return null;
            (package, animSet) = (request.PackagePath, own.ObjectName);
        }
        var set = await ViewerAnimations().PrepareAsync(package, animSet!, keepAll: profile is null || profile.IsNpc, cancellationToken);
        Log($"Viewer animations ready: animset={set.AnimSetName}, animations={set.Animations.Count}, folder={set.Directory}.");
        return set;
    }
}
