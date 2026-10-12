using System.Diagnostics;
using System.Security.Cryptography;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

public sealed record GuidedTextureModProgress(int Step, int TotalSteps, string Message);

public sealed record GuidedTextureModBuildResult(
    GuidedTextureModRevision Revision,
    string RevisionDirectory,
    TimeSpan Duration);

/// <summary>
/// Runs Arkive's already verified texture replacement stages as one guided action.
/// Every intermediate manifest remains in the revision folder for advanced diagnosis.
/// </summary>
public sealed class GuidedTextureModBuildService
{
    public const int TotalSteps = 9;

    private readonly TextureImportCandidateService _candidateService;
    private readonly TextureReplacementPayloadService _payloadService;
    private readonly TextureExportAssemblyService _assemblyService;
    private readonly PackageRewritePlanService _rewriteService;
    private readonly LogicalPackageImageService _logicalService;
    private readonly PhysicalPackagePlanService _physicalService;
    private readonly NoOpUpkReconstructionService _noOpService;
    private readonly ReplacementUpkBuildService _replacementService;
    private readonly ArkiveModBundleService _bundleService;

    public GuidedTextureModBuildService(
        TextureImportCandidateService candidateService,
        TextureReplacementPayloadService payloadService,
        TextureExportAssemblyService assemblyService,
        PackageRewritePlanService rewriteService,
        LogicalPackageImageService logicalService,
        PhysicalPackagePlanService physicalService,
        NoOpUpkReconstructionService noOpService,
        ReplacementUpkBuildService replacementService,
        ArkiveModBundleService bundleService)
    {
        _candidateService = candidateService;
        _payloadService = payloadService;
        _assemblyService = assemblyService;
        _rewriteService = rewriteService;
        _logicalService = logicalService;
        _physicalService = physicalService;
        _noOpService = noOpService;
        _replacementService = replacementService;
        _bundleService = bundleService;
    }

    public async Task<GuidedTextureModBuildResult> BuildAsync(
        GuidedTextureModProjectEntry project,
        string revisionDirectory,
        string backupRootDirectory,
        IProgress<GuidedTextureModProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var watch = Stopwatch.StartNew();
        var root = Path.GetFullPath(revisionDirectory);
        Directory.CreateDirectory(root);
        if (Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException("The guided texture-mod revision directory must be empty.");
        }
        if (!File.Exists(project.Manifest.NativeTextureManifestPath))
        {
            throw new FileNotFoundException("The project's native texture manifest is missing.", project.Manifest.NativeTextureManifestPath);
        }
        if (!File.Exists(project.Manifest.EditableImagePath))
        {
            throw new FileNotFoundException("The project's editable image is missing.", project.Manifest.EditableImagePath);
        }

        string Stage(int number, string name)
        {
            var path = Path.Combine(root, $"{number:00}-{name}");
            Directory.CreateDirectory(path);
            return path;
        }
        void Report(int step, string message) =>
            progress?.Report(new GuidedTextureModProgress(step, TotalSteps, message));

        Report(1, "Importing the edited image and restoring the native texture layout...");
        var candidate = await _candidateService.CreateAsync(
            new TextureImportCandidateRequest(
                project.Manifest.NativeTextureManifestPath,
                project.Manifest.EditableImagePath,
                Stage(1, "candidate"),
                project.Manifest.AlphaPolicy),
            cancellationToken).ConfigureAwait(false);

        Report(2, "Encoding and decode-checking native mip payloads...");
        var payloads = await _payloadService.BuildAsync(
            new TextureReplacementPayloadRequest(candidate.ManifestPath, Stage(2, "payloads")),
            cancellationToken).ConfigureAwait(false);

        Report(3, "Assembling and replay-checking the Texture2D export...");
        var assembly = await _assemblyService.AssembleAsync(
            new TextureExportAssemblyRequest(payloads.ManifestPath, Stage(3, "assembly")),
            cancellationToken).ConfigureAwait(false);

        Report(4, "Planning the package rewrite...");
        var rewrite = await _rewriteService.PlanAsync(
            new PackageRewritePlanRequest(assembly.ManifestPath, Stage(4, "rewrite")),
            cancellationToken).ConfigureAwait(false);

        Report(5, "Building and verifying the logical package image...");
        var logical = await _logicalService.BuildAsync(
            new LogicalPackageImageRequest(rewrite.ManifestPath, Stage(5, "logical")),
            cancellationToken).ConfigureAwait(false);

        Report(6, "Planning protected physical chunks...");
        var physical = await _physicalService.PlanAsync(
            new PhysicalPackagePlanRequest(logical.ManifestPath, Stage(6, "physical")),
            cancellationToken).ConfigureAwait(false);

        Report(7, "Proving a source-identical no-op reconstruction...");
        var noOp = await _noOpService.ReconstructAsync(
            new NoOpUpkReconstructionRequest(physical.ManifestPath, Stage(7, "noop")),
            cancellationToken).ConfigureAwait(false);

        Report(8, "Constructing and validating the modified UPK...");
        var replacement = await _replacementService.BuildAsync(
            new ReplacementUpkBuildRequest(noOp.ManifestPath, Stage(8, "upk")),
            cancellationToken).ConfigureAwait(false);

        Report(9, "Preparing the reversible install bundle...");
        var bundle = await _bundleService.BuildAsync(
            new ArkiveModBundleRequest(
                replacement.ManifestPath,
                Stage(9, "bundle"),
                backupRootDirectory,
                project.Manifest.DisplayName),
            cancellationToken).ConfigureAwait(false);

        watch.Stop();
        string editedHash;
        await using (var editedStream = File.OpenRead(project.Manifest.EditableImagePath))
        {
            editedHash = Convert.ToHexString(await SHA256.HashDataAsync(
                editedStream, cancellationToken).ConfigureAwait(false));
        }
        var revision = new GuidedTextureModRevision(
            Path.GetFileName(root),
            DateTimeOffset.UtcNow,
            editedHash,
            candidate.ManifestPath,
            payloads.ManifestPath,
            assembly.ManifestPath,
            rewrite.ManifestPath,
            logical.ManifestPath,
            physical.ManifestPath,
            noOp.ManifestPath,
            replacement.ManifestPath,
            replacement.OutputUpkPath,
            bundle.BundleManifestPath,
            bundle.ReadinessManifestPath,
            bundle.PayloadPath,
            bundle.Bundle.PayloadBytes,
            bundle.Bundle.PayloadSha256,
            watch.Elapsed.TotalSeconds);
        return new GuidedTextureModBuildResult(revision, root, watch.Elapsed);
    }
}
