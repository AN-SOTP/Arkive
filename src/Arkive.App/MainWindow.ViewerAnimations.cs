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
            set = set with { Skies = await PrepareViewerSkiesAsync() };
            Log($"Viewer environments ready: {set.Environments.Count} game cubemaps in {set.Directory}, {set.Skies?.Skies.Count ?? 0} skies.");
            return set;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log($"Viewer environments unavailable: {exception.Message}");
            _viewerEnvironmentSet = null;
            return null;
        }
    }

    /// <summary>The game's sky backgrounds (package lv_dynamicsky), or null when they can't be found or exported.</summary>
    private async Task<ModelViewerSkySet?> PrepareViewerSkiesAsync()
    {
        try
        {
            const string probe = "lv_dynamicskypack01_01_d";
            var found = await _packageIndexStore.SearchAsync(_libraryRootPath!, probe, null, _gameMetadataIndex,
                PackageIndexSearchScope.ObjectName, null, resultLimit: 50);
            var match = found.Matches.FirstOrDefault(item => item.PackageAlias.Equals("lv_dynamicsky", StringComparison.OrdinalIgnoreCase) &&
                                                             item.ObjectName.Equals(probe, StringComparison.OrdinalIgnoreCase));
            if (match is null || !File.Exists(match.SourcePath)) return null;
            var set = await _viewerEnvironments!.PrepareSkiesAsync(match.SourcePath);
            return set with { Skies = await Task.Run(() => SkyColours(set)) };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log($"Viewer skies unavailable: {exception.Message}");
            return null;
        }
    }

    // Each sky's average colour in its lower third (where it meets the horizon), measured once and kept in colours.json.
    private static IReadOnlyList<ModelViewerSky> SkyColours(ModelViewerSkySet set)
    {
        var record = Path.Combine(set.Directory, "colours.json");
        Dictionary<string, float[]>? known = null;
        try { if (File.Exists(record)) known = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(record)); }
        catch (System.Text.Json.JsonException) { }
        known ??= [];
        var changed = false;
        foreach (var sky in set.Skies.Where(sky => !known.ContainsKey(sky.Name)))
        {
            try
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(Path.Combine(set.Directory, sky.File));
                image.DecodePixelWidth = 16;
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.EndInit();
                var pixels = new System.Windows.Media.Imaging.FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int width = pixels.PixelWidth, height = pixels.PixelHeight;
                var data = new byte[width * height * 4];
                pixels.CopyPixels(data, width * 4, 0);
                double r = 0, g = 0, b = 0;
                var count = 0;
                for (var y = height * 2 / 3; y < height; y++)
                for (var x = 0; x < width; x++, count++)
                {
                    var at = (y * width + x) * 4;
                    b += data[at]; g += data[at + 1]; r += data[at + 2];
                }
                known[sky.Name] = [(float)(r / count / 255), (float)(g / count / 255), (float)(b / count / 255)];
                changed = true;
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or InvalidOperationException or UriFormatException)
            {
            }
        }
        if (changed) File.WriteAllText(record, System.Text.Json.JsonSerializer.Serialize(known));
        return set.Skies.Select(sky => known.TryGetValue(sky.Name, out var colour) ? sky with { Colour = colour } : sky).ToArray();
    }

    /// <summary>Null when the mesh has no animations Arkive can find.</summary>
    private async Task<ModelViewerAnimationOffer?> FindViewerAnimationsAsync(ModelViewerRequest request, CancellationToken cancellationToken)
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
        var rest = await ViewerAnimations().PrepareRestPoseAsync(set, request.PackagePath, request.ObjectName, cancellationToken);
        Log($"Viewer animations ready: animset={set.AnimSetName}, animations={set.Animations.Count}, folder={set.Directory}, rest={rest}.");
        return new ModelViewerAnimationOffer(set, rest);
    }
}
