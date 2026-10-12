using System.Diagnostics;
using System.Security.Cryptography;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Inspection;
using Arkive.Core.Packages;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

public sealed record GuidedTexturePackageBuildProgress(
    int Step,
    int TotalSteps,
    string ObjectName,
    string Message);

public sealed record GuidedTexturePackageBuildResult(
    GuidedTexturePackageRevision Revision,
    string RevisionDirectory,
    TimeSpan Duration);

public sealed record GuidedTexturePackageStageResult(
    string SourcePackagePath,
    string SourcePackageSha256,
    IReadOnlyList<GuidedTexturePackageAssetRevision> Assets,
    string OutputUpkPath,
    string FinalReplacementUpkBuildManifestPath,
    TimeSpan Duration);

/// <summary>
/// Composes several independently editable Texture2D projects into one package.
/// Each replacement is applied to a private staging UPK; the live source remains
/// read-only and only the final cumulative result is bundled for deployment.
/// </summary>
public sealed class GuidedTexturePackageBuildService
{
    private const int StepsPerAsset = 9;

    private readonly AssetCatalogReader _catalogReader;
    private readonly TextureReconnaissanceService _reconnaissanceService;
    private readonly NativeTextureExportService _nativeExportService;
    private readonly TextureImportCandidateService _candidateService;
    private readonly TextureReplacementPayloadService _payloadService;
    private readonly TextureExportAssemblyService _assemblyService;
    private readonly PackageRewritePlanService _rewriteService;
    private readonly LogicalPackageImageService _logicalService;
    private readonly PhysicalPackagePlanService _physicalService;
    private readonly NoOpUpkReconstructionService _noOpService;
    private readonly ReplacementUpkBuildService _replacementService;
    private readonly ArkiveModBundleService _bundleService;

    public GuidedTexturePackageBuildService(
        UpkPackageReader summaryReader,
        IChunkProtectionProvider protectionProvider,
        NativeTextureExportService nativeExportService,
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
        _catalogReader = new AssetCatalogReader(summaryReader, protectionProvider);
        _reconnaissanceService = new TextureReconnaissanceService(summaryReader, protectionProvider);
        _nativeExportService = nativeExportService;
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

    public async Task<GuidedTexturePackageBuildResult> BuildAsync(
        GuidedTexturePackageEntry package,
        IReadOnlyList<GuidedTextureModProjectEntry> selectedProjects,
        string revisionDirectory,
        string backupRootDirectory,
        IProgress<GuidedTexturePackageBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(revisionDirectory);
        var totalSteps = checked(selectedProjects.Count * StepsPerAsset + 1);
        var stageProgress = progress is null
            ? null
            : new Progress<GuidedTexturePackageBuildProgress>(update => progress.Report(
                update with { TotalSteps = totalSteps }));
        var stage = await BuildStagingAsync(
            package, selectedProjects, root, stageProgress, cancellationToken).ConfigureAwait(false);

        ReportFinal(progress, totalSteps, totalSteps,
            "Preparing one reversible package-level install bundle...");
        var bundleAssets = stage.Assets.Select(asset => new ArkiveModAsset(
            package.Manifest.PackageAlias, asset.NativeExportIndex, "Texture2D", asset.ObjectName,
            asset.PixelFormat, asset.ReplacementSerialOffset, asset.ReplacementSerialSize)).ToArray();
        var bundle = await _bundleService.BuildAsync(
            new ArkiveModBundleRequest(
                stage.FinalReplacementUpkBuildManifestPath, Path.Combine(root, "bundle"),
                backupRootDirectory,
                string.IsNullOrWhiteSpace(package.Manifest.DisplayName)
                    ? $"{package.Manifest.PackageAlias} texture pack"
                    : package.Manifest.DisplayName!,
                stage.SourcePackagePath, stage.SourcePackageSha256, bundleAssets), cancellationToken)
            .ConfigureAwait(false);
        var duration = stage.Duration + bundle.Duration;
        var revision = new GuidedTexturePackageRevision(
            Path.GetFileName(root), DateTimeOffset.UtcNow, stage.SourcePackageSha256, stage.Assets,
            stage.OutputUpkPath, stage.FinalReplacementUpkBuildManifestPath,
            bundle.BundleManifestPath, bundle.ReadinessManifestPath, bundle.PayloadPath,
            bundle.Bundle.PayloadBytes, bundle.Bundle.PayloadSha256, duration.TotalSeconds);
        return new GuidedTexturePackageBuildResult(revision, root, duration);
    }

    /// <summary>
    /// Builds only the cumulative Texture2D staging package. This is the reusable
    /// first half of a unified package composition; it deliberately emits no
    /// deployment bundle so a later mesh stage cannot be accidentally installed
    /// as a competing payload for the same physical UPK.
    /// </summary>
    public async Task<GuidedTexturePackageStageResult> BuildStagingAsync(
        GuidedTexturePackageEntry package,
        IReadOnlyList<GuidedTextureModProjectEntry> selectedProjects,
        string stagingDirectory,
        IProgress<GuidedTexturePackageBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(selectedProjects);
        if (selectedProjects.Count == 0)
        {
            throw new NotSupportedException("Select at least one texture before rebuilding the package.");
        }
        var sourcePath = Path.GetFullPath(package.Manifest.SourcePackagePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The clean source package was not found.", sourcePath);
        }
        if (selectedProjects.Any(project => !Path.GetFullPath(project.Manifest.SourcePackagePath)
                .Equals(sourcePath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new NotSupportedException("Every selected texture must belong to the same source UPK.");
        }

        var inputs = new List<(GuidedTextureModProjectEntry Project, NativeTextureExportManifest Native)>();
        foreach (var project in selectedProjects)
        {
            var native = await NativeTextureExportService.LoadManifestAsync(
                project.Manifest.NativeTextureManifestPath, cancellationToken).ConfigureAwait(false);
            if (native.NativeExportIndex is null)
            {
                throw new NotSupportedException($"{project.Manifest.ObjectName} has no confirmed native export index.");
            }
            inputs.Add((project, native));
        }
        var duplicate = inputs.GroupBy(input => input.Native.NativeExportIndex!.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new NotSupportedException(
                $"More than one selected project targets native export {duplicate.Key}.");
        }
        var cleanHashes = inputs.Select(input => input.Native.SourcePackageSha256)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (cleanHashes.Length != 1)
        {
            throw new NotSupportedException(
                "The selected textures were extracted from different package versions. Recreate stale projects from the current clean UPK.");
        }
        var cleanHash = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!cleanHash.Equals(cleanHashes[0], StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "The game UPK is not the clean package version used by these texture projects. Restore the original package before rebuilding.");
        }

        var root = Path.GetFullPath(stagingDirectory);
        Directory.CreateDirectory(root);
        if (Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException("The package-build revision directory must be empty.");
        }
        var watch = Stopwatch.StartNew();
        var totalSteps = checked(inputs.Count * StepsPerAsset);
        var step = 0;
        string currentPackagePath = sourcePath;
        var builtAssets = new List<GuidedTexturePackageAssetRevision>(inputs.Count);
        ReplacementUpkBuildResult? finalReplacement = null;

        foreach (var (project, originalNative) in inputs.OrderBy(input => input.Native.NativeExportIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var objectName = project.Manifest.ObjectName;
            var nativeExportIndex = originalNative.NativeExportIndex ??
                                    throw new NotSupportedException(
                                        $"{objectName} has no confirmed native export index.");
            var assetRoot = Path.Combine(
                root, $"a{builtAssets.Count + 1:00}-{project.Manifest.ProjectId[..8]}");
            Directory.CreateDirectory(assetRoot);
            string Stage(int number, string name)
            {
                var path = Path.Combine(assetRoot, $"{number:00}-{name}");
                Directory.CreateDirectory(path);
                return path;
            }
            void Report(string message) => progress?.Report(
                new GuidedTexturePackageBuildProgress(++step, totalSteps, objectName, message));

            Report("Reconnoitring this texture in the cumulative staging package...");
            var catalog = _catalogReader.Read(currentPackagePath);
            var currentAsset = catalog.Assets.SingleOrDefault(asset =>
                asset.ExportIndex == nativeExportIndex) ??
                throw new NotSupportedException(
                    $"Native export {originalNative.NativeExportIndex} for {objectName} is missing from the staging package.");
            if (!currentAsset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"Native export {currentAsset.ExportIndex} is {currentAsset.ObjectName}, not the expected {objectName}.");
            }
            var currentHash = await HashFileAsync(currentPackagePath, cancellationToken).ConfigureAwait(false);
            var report = await _reconnaissanceService.InspectAsync(
                new TextureReconnaissanceRequest(
                    currentPackagePath, package.Manifest.PackageAlias,
                    project.Manifest.ExportIndex, AssetKind.Texture,
                    currentAsset.ClassName, currentAsset.ObjectName,
                    currentAsset.SerialOffset, currentAsset.SerialSize, currentHash),
                cancellationToken).ConfigureAwait(false);
            var reconStore = new TextureReconnaissanceWorkspaceStore(Stage(0, "recon"));
            var recon = await reconStore.SaveAsync(report, cancellationToken).ConfigureAwait(false);

            Report("Exporting the current native texture layout...");
            var native = await _nativeExportService.ExportAsync(
                new NativeTextureExportRequest(report, Stage(1, "native"), recon.JsonPath),
                cancellationToken).ConfigureAwait(false);

            Report("Importing the edited image and preserving game alpha...");
            var candidate = await _candidateService.CreateAsync(
                new TextureImportCandidateRequest(
                    native.ManifestPath, project.Manifest.EditableImagePath,
                    Stage(2, "candidate"), project.Manifest.AlphaPolicy),
                cancellationToken).ConfigureAwait(false);

            Report("Encoding and decode-checking native mip payloads...");
            var payloads = await _payloadService.BuildAsync(
                new TextureReplacementPayloadRequest(candidate.ManifestPath, Stage(3, "payloads")),
                cancellationToken).ConfigureAwait(false);

            Report("Assembling and replay-checking the Texture2D export...");
            var assembly = await _assemblyService.AssembleAsync(
                new TextureExportAssemblyRequest(payloads.ManifestPath, Stage(4, "assembly")),
                cancellationToken).ConfigureAwait(false);

            Report("Planning the cumulative package rewrite...");
            var rewrite = await _rewriteService.PlanAsync(
                new PackageRewritePlanRequest(assembly.ManifestPath, Stage(5, "rewrite")),
                cancellationToken).ConfigureAwait(false);

            Report("Building and verifying the cumulative logical package image...");
            var logical = await _logicalService.BuildAsync(
                new LogicalPackageImageRequest(rewrite.ManifestPath, Stage(6, "logical")),
                cancellationToken).ConfigureAwait(false);

            Report("Planning physical chunks and proving no-op reconstruction...");
            var physical = await _physicalService.PlanAsync(
                new PhysicalPackagePlanRequest(logical.ManifestPath, Stage(7, "physical")),
                cancellationToken).ConfigureAwait(false);
            var noOp = await _noOpService.ReconstructAsync(
                new NoOpUpkReconstructionRequest(physical.ManifestPath, Stage(8, "noop")),
                cancellationToken).ConfigureAwait(false);

            Report("Constructing and validating this cumulative modified UPK...");
            finalReplacement = await _replacementService.BuildAsync(
                new ReplacementUpkBuildRequest(noOp.ManifestPath, Stage(9, "upk")),
                cancellationToken).ConfigureAwait(false);
            currentPackagePath = finalReplacement.OutputUpkPath;

            string editHash;
            await using (var edit = File.OpenRead(project.Manifest.EditableImagePath))
            {
                editHash = Convert.ToHexString(await SHA256.HashDataAsync(edit, cancellationToken)
                    .ConfigureAwait(false));
            }
            builtAssets.Add(new GuidedTexturePackageAssetRevision(
                project.Manifest.ProjectId, objectName, project.Manifest.ExportIndex,
                nativeExportIndex, project.Manifest.PixelFormat, editHash,
                finalReplacement.ManifestPath,
                finalReplacement.Manifest.ReplacementSerialOffset,
                finalReplacement.Manifest.ReplacementSerialSize));
        }

        watch.Stop();
        return new GuidedTexturePackageStageResult(
            sourcePath, cleanHash, builtAssets, finalReplacement!.OutputUpkPath,
            finalReplacement.ManifestPath, watch.Elapsed);
    }

    private static void ReportFinal(
        IProgress<GuidedTexturePackageBuildProgress>? progress,
        int step,
        int total,
        string message) => progress?.Report(
        new GuidedTexturePackageBuildProgress(step, total, "Package", message));

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false));
    }

}
