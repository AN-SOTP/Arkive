using System.IO;

namespace Arkive.App;

/// <summary>The model viewer's skill preview (see <see cref="SkillPreviewService"/>).</summary>
public partial class MainWindow
{
    private SkillPreviewService? _skillPreview;

    private SkillPreviewService SkillPreview() => _skillPreview ??= new SkillPreviewService(
        _packageIndexStore, () => _libraryRootPath, () => _gameMetadataIndex, _reader, _protectionProvider,
        ViewerAnimations(), ModelViewerService().Cache.RootDirectory, FindParticleAssetsAsync, Log);

    private Task<SkillPreviewOffer?> FindSkillPreviewAsync(Arkive.Core.Viewer.ModelViewerRequest request, CancellationToken cancellationToken) =>
        _umodelBridge is null ? Task.FromResult<SkillPreviewOffer?>(null) : SkillPreview().OfferAsync(request, cancellationToken);

    private Task<object> PrepareSkillPreviewAsync(string classFile, int skillId, int branch, Action<string> progress,
        CancellationToken cancellationToken) =>
        SkillPreview().PrepareAsync(classFile, skillId, branch, progress, cancellationToken);
}
