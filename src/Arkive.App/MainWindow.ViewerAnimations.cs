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
