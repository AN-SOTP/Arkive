using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Inspection;
using Arkive.Core.Interface;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.Packages;
using Arkive.Core.StaticMeshes;

namespace Arkive.Core.Mods;

public enum UnifiedPackageAssetKind
{
    Texture2D,
    MaterialInstanceConstant,
    SkeletalMesh,
    StaticMesh,
    ParticleSystem,
    InterfaceMovie,
}

public sealed record UnifiedPackageBuildProgress(
    int Step,
    int TotalSteps,
    string ObjectName,
    string Stage,
    string Message);

public sealed record UnifiedPackageCompositionAsset(
    UnifiedPackageAssetKind Kind,
    string ProjectId,
    string DisplayName,
    string PackageAlias,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    string SourceArtifactSha256,
    string FinalArtifactSha256,
    long FinalSerialOffset,
    long FinalSerialSize,
    string VerificationManifestPath)
{
    public bool VariableSizeReplacement { get; init; }
}

public sealed record UnifiedPackageCompositionStage(
    int Order,
    UnifiedPackageAssetKind Kind,
    string ObjectName,
    int NativeExportIndex,
    string InputPackageSha256,
    string OutputPackageSha256,
    string OutputPackagePath,
    string VerificationManifestPath)
{
    public bool VariableSizeReplacement { get; init; }
}

public sealed record UnifiedPackageCompositionManifest(
    int SchemaVersion,
    string CompositionId,
    DateTimeOffset CreatedAt,
    string PackageProjectId,
    string PackageAlias,
    string PhysicalPackageName,
    string CleanSourcePackagePath,
    long CleanSourcePackageBytes,
    string CleanSourcePackageSha256,
    string FinalOutputUpkPath,
    long FinalOutputUpkBytes,
    string FinalOutputUpkSha256,
    int TextureCount,
    int SkeletalMeshCount,
    bool TexturesAppliedBeforeMeshes,
    bool CleanSourceIdentityRetained,
    bool EveryStageVerified,
    bool EverySelectedAssetCatalogued,
    bool OnePhysicalPayloadProduced,
    IReadOnlyList<UnifiedPackageCompositionAsset> Assets,
    IReadOnlyList<UnifiedPackageCompositionStage> Stages,
    string BundleManifestPath,
    string DeploymentReadinessManifestPath,
    string PayloadPath,
    double DurationSeconds)
{
    public int MaterialCount { get; init; }

    public int StaticMeshCount { get; init; }

    public int ParticleEffectCount { get; init; }
    public int InterfaceMovieCount { get; init; }

    public bool MaterialsAppliedBeforeMeshes { get; init; }

    public int VariableSizeMeshCount { get; init; }

    public bool PreviewOnly { get; init; }

    public bool ExactObjectExternalValidationPassed { get; init; }

    public bool VariableSizeDeploymentGatePassed { get; init; }

    public string? VariableSizeDeploymentGateManifestPath { get; init; }
}

public sealed record VariableSizeDeploymentGateManifest(
    int SchemaVersion,
    string GateId,
    DateTimeOffset CreatedAt,
    string CompositionId,
    string GameProfile,
    int VariableSizeMeshCount,
    string SourcePackagePath,
    long SourcePackageBytes,
    string SourcePackageSha256,
    string PayloadPath,
    long PayloadBytes,
    string PayloadSha256,
    bool PackageSizeChanged,
    bool EveryTopologyStageVerified,
    bool EveryUnrelatedExportRetained,
    bool EveryAffectedChunkDecodedBack,
    bool ExactObjectExternalValidationPassed,
    bool OriginalPhysicalPackageNameRetained,
    bool BackupCapacityPassed,
    bool AtomicVariableSizeReplacementSupported,
    bool ControlledInstallApproved,
    IReadOnlyList<string> TopologyStagingManifestPaths,
    IReadOnlyList<ModReadinessCheck> Checks);

public sealed record UnifiedPackageCompositionRequest(
    GuidedTexturePackageEntry Package,
    IReadOnlyList<GuidedTextureModProjectEntry> TextureProjects,
    IReadOnlyList<SkeletalMeshEditWorkspaceEntry> MeshEdits,
    string RevisionDirectory,
    string BackupRootDirectory,
    string DisplayName)
{
    public IReadOnlyList<MaterialInstanceEditEntry> MaterialEdits { get; init; } = [];

    public IReadOnlyList<StaticMeshEditEntry> StaticMeshEdits { get; init; } = [];

    public IReadOnlyList<Particles.ParticleEffectEditEntry> ParticleEdits { get; init; } = [];
    public IReadOnlyList<InterfaceMovieEditEntry> InterfaceMovieEdits { get; init; } = [];

    public bool ForcePreviewOnly { get; init; }
}

public sealed record UnifiedPackageCompositionResult(
    GuidedTexturePackageRevision Revision,
    string RevisionDirectory,
    string CompositionManifestPath,
    UnifiedPackageCompositionManifest Manifest,
    TimeSpan Duration);

public sealed record UnifiedPackageCompositionVerification(
    string ManifestPath,
    string OutputUpkPath,
    UnifiedPackageCompositionManifest Manifest);

/// <summary>
/// Builds one UPK from every selected project that targets the same clean
/// physical package. Variable-length Texture2D rewrites run first; each
/// SkeletalMesh edit is then semantically rebased onto the current cumulative
/// package. Topology-preserving edits use same-size replacement; topology edits
/// compile a variable-size export and append/retarget it. Only the final package
/// is externally validated. Compositions containing variable-size meshes remain
/// preview-only until their deployment gate is approved.
/// </summary>
public sealed class UnifiedPackageComposerService
{
    // Schema 3 already bound the UModel oracle to the package alias, native export index,
    // object name, and exact logical export hash. Schema 4 added deformation-dependency
    // evidence used by current preflight, but did not strengthen that oracle identity.
    // Keep accepting a retained v3 oracle during cumulative rebuilds; the source export is
    // still hash-proven below and a fresh current-schema recon/preflight is produced before
    // any replacement is staged.
    private const int MinimumHashBoundSourceOracleSchemaVersion = 3;
    public const int CurrentSchemaVersion = 1;
    public const int CurrentVariableSizeDeploymentGateSchemaVersion = 1;
    public const string ManifestFileName = "arkive-package-composition.json";
    public const string VariableSizeDeploymentGateManifestFileName =
        "arkive-variable-size-deployment-gate.json";
    private const int MeshSteps = 5;
    private const int MaterialSteps = 2;
    private const int InterfaceMovieSteps = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly SkeletalMeshReconnaissanceService _meshReconnaissance;
    private readonly SkeletalMeshEditableExportService _meshEditableExport;
    private readonly SkeletalMeshNativeEncodingService _meshNativeEncoding;
    private readonly SkeletalMeshTopologyCompilerService _meshTopologyCompiler;
    private readonly SkeletalMeshStagedUpkService _meshStaging;
    private readonly GuidedTexturePackageBuildService _textureBuilder;
    private readonly ArkivePackageModBundleService _bundleService;
    private readonly MaterialInstanceEditService _materialEditService;
    private readonly StaticMeshReconnaissanceService _staticMeshReconnaissance;
    private readonly StaticMeshEditableExportService _staticMeshEditableExport;
    private readonly StaticMeshCompilationService _staticMeshCompilation;

    public UnifiedPackageComposerService(
        UpkPackageReader summaryReader,
        IChunkProtectionProvider protectionProvider,
        SkeletalMeshReconnaissanceService meshReconnaissance,
        SkeletalMeshEditableExportService meshEditableExport,
        SkeletalMeshNativeEncodingService meshNativeEncoding,
        SkeletalMeshStagedUpkService meshStaging,
        GuidedTexturePackageBuildService textureBuilder,
        ArkivePackageModBundleService bundleService,
        MaterialInstanceEditService? materialEditService = null,
        SkeletalMeshTopologyCompilerService? meshTopologyCompiler = null,
        StaticMeshReconnaissanceService? staticMeshReconnaissance = null,
        StaticMeshEditableExportService? staticMeshEditableExport = null,
        StaticMeshCompilationService? staticMeshCompilation = null)
    {
        _catalogReader = new AssetCatalogReader(summaryReader, protectionProvider);
        _protectionProvider = protectionProvider;
        _meshReconnaissance = meshReconnaissance;
        _meshEditableExport = meshEditableExport;
        _meshNativeEncoding = meshNativeEncoding;
        _meshTopologyCompiler = meshTopologyCompiler ??
                                new SkeletalMeshTopologyCompilerService(summaryReader, protectionProvider);
        _meshStaging = meshStaging;
        _textureBuilder = textureBuilder;
        _bundleService = bundleService;
        _materialEditService = materialEditService ?? new MaterialInstanceEditService(protectionProvider);
        _staticMeshReconnaissance = staticMeshReconnaissance ??
                                    new StaticMeshReconnaissanceService(summaryReader, protectionProvider);
        _staticMeshEditableExport = staticMeshEditableExport ??
                                    new StaticMeshEditableExportService(summaryReader, protectionProvider);
        _staticMeshCompilation = staticMeshCompilation ??
                                 new StaticMeshCompilationService(summaryReader, protectionProvider);
    }

    public static async Task<UnifiedPackageCompositionManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The package-composition manifest was not found.", fullPath);
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<UnifiedPackageCompositionManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new InvalidDataException("The package-composition manifest was empty.");
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Package-composition schema {manifest.SchemaVersion} is unsupported; Arkive requires {CurrentSchemaVersion}.");
        return manifest;
    }

    public static async Task<VariableSizeDeploymentGateManifest> LoadVariableSizeDeploymentGateAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                "The variable-size deployment-gate manifest was not found.", fullPath);
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<VariableSizeDeploymentGateManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new InvalidDataException(
                           "The variable-size deployment-gate manifest was empty.");
        if (manifest.SchemaVersion != CurrentVariableSizeDeploymentGateSchemaVersion)
            throw new NotSupportedException(
                $"Variable-size deployment-gate schema {manifest.SchemaVersion} is unsupported; " +
                $"Arkive requires {CurrentVariableSizeDeploymentGateSchemaVersion}.");
        return manifest;
    }

    public static async Task<UnifiedPackageCompositionVerification> VerifyOutputAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var manifest = await LoadManifestAsync(fullManifestPath, cancellationToken).ConfigureAwait(false);
        if (!manifest.CleanSourceIdentityRetained || !manifest.EveryStageVerified ||
            !manifest.EverySelectedAssetCatalogued || !manifest.OnePhysicalPayloadProduced ||
            manifest.Assets.Count == 0 ||
            manifest.Assets.Count != manifest.TextureCount + manifest.SkeletalMeshCount +
                                     manifest.StaticMeshCount + manifest.MaterialCount + manifest.InterfaceMovieCount)
            throw new InvalidDataException("The package composition does not retain every required proof gate.");
        if (manifest.VariableSizeMeshCount < 0 ||
            manifest.Assets.Count(asset => asset.VariableSizeReplacement) !=
            manifest.VariableSizeMeshCount ||
            manifest.PreviewOnly && manifest.VariableSizeMeshCount <= 0 ||
            manifest.VariableSizeMeshCount > 0 && !manifest.ExactObjectExternalValidationPassed)
            throw new InvalidDataException(
                "The composition does not retain its variable-size mesh and external-load proof.");
        var outputPath = Path.GetFullPath(manifest.FinalOutputUpkPath);
        if (!File.Exists(outputPath))
            throw new FileNotFoundException("The composed UPK was not found.", outputPath);
        var info = new FileInfo(outputPath);
        var hash = await HashFileAsync(outputPath, cancellationToken).ConfigureAwait(false);
        if (info.Length != manifest.FinalOutputUpkBytes ||
            !hash.Equals(manifest.FinalOutputUpkSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The composed UPK no longer matches its manifest.");
        var payloadPath = Path.GetFullPath(manifest.PayloadPath);
        if (!File.Exists(payloadPath))
            throw new FileNotFoundException("The composed physical-name-safe payload was not found.", payloadPath);
        var payloadInfo = new FileInfo(payloadPath);
        var payloadHash = await HashFileAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        if (payloadInfo.Length != manifest.FinalOutputUpkBytes ||
            !payloadHash.Equals(manifest.FinalOutputUpkSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The physical-name-safe payload no longer matches the verified composed UPK.");

        if (manifest.VariableSizeMeshCount > 0)
        {
            var bundle = await ArkiveModBundleService.LoadBundleAsync(
                manifest.BundleManifestPath, cancellationToken).ConfigureAwait(false);
            var readiness = await ArkiveModBundleService.LoadReadinessAsync(
                manifest.DeploymentReadinessManifestPath, cancellationToken).ConfigureAwait(false);
            if (manifest.PreviewOnly)
            {
                if (manifest.VariableSizeDeploymentGatePassed ||
                    !string.IsNullOrWhiteSpace(manifest.VariableSizeDeploymentGateManifestPath) ||
                    bundle.InstallationEnabled || bundle.ReadyForControlledInstall ||
                    bundle.ReversibleInstallPlanned || bundle.VariableSizeDeploymentApproved ||
                    readiness.ReadyForControlledInstall || readiness.VariableSizeDeploymentApproved ||
                    !string.IsNullOrWhiteSpace(bundle.VariableSizeDeploymentGateRelativePath) ||
                    !string.IsNullOrWhiteSpace(bundle.VariableSizeDeploymentGateSha256) ||
                    !string.IsNullOrWhiteSpace(readiness.VariableSizeDeploymentGateRelativePath) ||
                    !string.IsNullOrWhiteSpace(readiness.VariableSizeDeploymentGateSha256))
                    throw new InvalidDataException(
                        "The preview-only variable-size composition unexpectedly enables installation.");
            }
            else
            {
                if (!manifest.VariableSizeDeploymentGatePassed ||
                    string.IsNullOrWhiteSpace(manifest.VariableSizeDeploymentGateManifestPath) ||
                    !bundle.InstallationEnabled || !bundle.ReadyForControlledInstall ||
                    !bundle.ReversibleInstallPlanned || !bundle.VariableSizeDeploymentApproved ||
                    bundle.VariableSizeMeshCount != manifest.VariableSizeMeshCount ||
                    !readiness.ReadyForControlledInstall ||
                    !readiness.VariableSizeDeploymentApproved ||
                    readiness.VariableSizeMeshCount != manifest.VariableSizeMeshCount ||
                    string.IsNullOrWhiteSpace(bundle.VariableSizeDeploymentGateRelativePath) ||
                    string.IsNullOrWhiteSpace(bundle.VariableSizeDeploymentGateSha256) ||
                    !string.Equals(
                        bundle.VariableSizeDeploymentGateRelativePath,
                        readiness.VariableSizeDeploymentGateRelativePath,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        bundle.VariableSizeDeploymentGateSha256,
                        readiness.VariableSizeDeploymentGateSha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "The variable-size composition does not retain its controlled deployment approval.");
                var gate = await LoadVariableSizeDeploymentGateAsync(
                    manifest.VariableSizeDeploymentGateManifestPath, cancellationToken).ConfigureAwait(false);
                var gateHash = await HashFileAsync(
                    manifest.VariableSizeDeploymentGateManifestPath, cancellationToken).ConfigureAwait(false);
                var expectedGatePath = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(manifest.BundleManifestPath)!,
                    bundle.VariableSizeDeploymentGateRelativePath));
                if (gate.SchemaVersion != CurrentVariableSizeDeploymentGateSchemaVersion ||
                    !expectedGatePath.Equals(
                        Path.GetFullPath(manifest.VariableSizeDeploymentGateManifestPath),
                        StringComparison.OrdinalIgnoreCase) ||
                    !gateHash.Equals(
                        bundle.VariableSizeDeploymentGateSha256, StringComparison.OrdinalIgnoreCase) ||
                    !gate.CompositionId.Equals(manifest.CompositionId, StringComparison.Ordinal) ||
                    !gate.GameProfile.Equals(bundle.GameProfile, StringComparison.Ordinal) ||
                    gate.VariableSizeMeshCount != manifest.VariableSizeMeshCount ||
                    !Path.GetFullPath(gate.SourcePackagePath).Equals(
                        Path.GetFullPath(manifest.CleanSourcePackagePath),
                        StringComparison.OrdinalIgnoreCase) ||
                    !gate.SourcePackageSha256.Equals(
                        manifest.CleanSourcePackageSha256, StringComparison.OrdinalIgnoreCase) ||
                    gate.SourcePackageBytes != manifest.CleanSourcePackageBytes ||
                    !Path.GetFullPath(gate.PayloadPath).Equals(
                        Path.GetFullPath(manifest.PayloadPath),
                        StringComparison.OrdinalIgnoreCase) ||
                    !gate.PayloadSha256.Equals(
                        manifest.FinalOutputUpkSha256, StringComparison.OrdinalIgnoreCase) ||
                    gate.PayloadBytes != manifest.FinalOutputUpkBytes ||
                    !gate.EveryTopologyStageVerified || !gate.EveryUnrelatedExportRetained ||
                    !gate.EveryAffectedChunkDecodedBack ||
                    !gate.ExactObjectExternalValidationPassed ||
                    !gate.OriginalPhysicalPackageNameRetained || !gate.BackupCapacityPassed ||
                    !gate.AtomicVariableSizeReplacementSupported || !gate.ControlledInstallApproved ||
                    gate.TopologyStagingManifestPaths.Count != manifest.VariableSizeMeshCount ||
                    gate.Checks.Count == 0 ||
                    gate.Checks.Any(check => check.Status != ModReadinessCheckStatus.Passed))
                    throw new InvalidDataException(
                        "The variable-size deployment-gate manifest failed verification.");
            }
        }
        return new UnifiedPackageCompositionVerification(fullManifestPath, outputPath, manifest);
    }

    public async Task<UnifiedPackageCompositionResult> BuildAsync(
        UnifiedPackageCompositionRequest request,
        IProgress<UnifiedPackageBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Package);
        if (request.TextureProjects.Count == 0 && request.MaterialEdits.Count == 0 &&
            request.MeshEdits.Count == 0 && request.StaticMeshEdits.Count == 0 && request.ParticleEdits.Count == 0 &&
            request.InterfaceMovieEdits.Count == 0)
            throw new NotSupportedException(
                "Select at least one texture, material, skeletal mesh, static mesh, particle effect or interface movie before rebuilding the package.");
        foreach (var particle in request.ParticleEdits)
            if (particle.Document.Patches.Count == 0)
                throw new NotSupportedException($"{particle.Document.DisplayName} has no colour change to write.");
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            throw new NotSupportedException("The package mod display name is empty.");

        var watch = Stopwatch.StartNew();
        var root = Path.GetFullPath(request.RevisionDirectory);
        Directory.CreateDirectory(root);
        if (Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("The unified package revision directory must be empty.");
        var sourcePath = Path.GetFullPath(request.Package.Manifest.SourcePackagePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The clean source package was not found.", sourcePath);
        if (request.TextureProjects.Any(project => !SamePath(project.Manifest.SourcePackagePath, sourcePath)))
            throw new NotSupportedException("Every selected texture must belong to this package project.");

        var sourceHash = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var sourceBytes = new FileInfo(sourcePath).Length;
        var materialInputs = new List<(MaterialInstanceEditEntry Edit, MaterialInstanceReconnaissanceReport Report)>();
        foreach (var edit in request.MaterialEdits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SamePath(edit.Document.SourcePackagePath, sourcePath) ||
                !edit.Document.SourcePackageSha256.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"{edit.Document.ObjectName} was created from a different client package version. Run the game-update check before rebuilding.");
            var report = await ReadJsonAsync<MaterialInstanceReconnaissanceReport>(
                edit.Document.SourceReconnaissancePath, cancellationToken).ConfigureAwait(false);
            if (report.Readiness != MaterialInstanceReadiness.Editable || report.NativeExportIndex is null ||
                !report.ExportPayloadSha256.Equals(edit.Document.SourceExportSha256, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"{edit.Document.ObjectName} does not retain an editable native material proof.");
            materialInputs.Add((edit, report));
        }
        var meshInputs = new List<(SkeletalMeshEditWorkspaceEntry Edit, SkeletalMeshEditableExportManifest Source)>();
        foreach (var edit in request.MeshEdits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (edit.Manifest.Preflight.Readiness is not (
                    SkeletalMeshEditReadiness.ReadyForPositionTangentEncoding or
                    SkeletalMeshEditReadiness.ReadyForTopologyCompilation) ||
                edit.Manifest.Preflight.ErrorCount != 0)
                throw new NotSupportedException(
                    $"{edit.Manifest.ObjectName} is not ready for native package composition.");
            if (!File.Exists(edit.EditedGlbPath))
                throw new FileNotFoundException("The selected edited GLB was not found.", edit.EditedGlbPath);
            var editHash = await HashFileAsync(edit.EditedGlbPath, cancellationToken).ConfigureAwait(false);
            if (!editHash.Equals(edit.Manifest.EditedGlbSha256, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"{edit.Manifest.ObjectName}'s edited GLB changed after preflight; revalidate it first.");
            var source = await ReadJsonAsync<SkeletalMeshEditableExportManifest>(
                edit.Manifest.SourceManifestPath, cancellationToken).ConfigureAwait(false);
            if (!SamePath(source.SourcePackagePath, sourcePath) ||
                !source.SourcePackageSha256.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"{edit.Manifest.ObjectName} was created from a different client package version. Re-export it from the clean UPK.");
            meshInputs.Add((edit, source));
        }

        var staticMeshInputs = new List<(StaticMeshEditEntry Edit, StaticMeshEditableExportManifest Source)>();
        foreach (var edit in request.StaticMeshEdits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (edit.Manifest.Preflight.Readiness == StaticMeshEditReadiness.Refused)
                throw new NotSupportedException(
                    $"{edit.Manifest.DisplayName} is not ready for native StaticMesh package composition.");
            if (!File.Exists(edit.Manifest.EditedGlbPath))
                throw new FileNotFoundException("The selected edited StaticMesh GLB was not found.",
                    edit.Manifest.EditedGlbPath);
            var editHash = await HashFileAsync(edit.Manifest.EditedGlbPath, cancellationToken)
                .ConfigureAwait(false);
            if (!editHash.Equals(edit.Manifest.EditedGlbSha256, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"{edit.Manifest.DisplayName}'s edited GLB changed after preflight; register it again.");
            var source = await StaticMeshEditableExportService.LoadManifestAsync(
                edit.Manifest.SourceEditableManifestPath, cancellationToken).ConfigureAwait(false);
            if (!SamePath(source.SourcePackagePath, sourcePath) ||
                !source.SourcePackageSha256.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"{source.ObjectName} was created from a different client package version. Re-export it from the clean UPK.");
            staticMeshInputs.Add((edit, source));
        }

        foreach (var edit in request.InterfaceMovieEdits)
            if (!SamePath(edit.Manifest.SourcePackagePath, sourcePath) ||
                !edit.Manifest.SourcePackageSha256.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"{edit.Manifest.ObjectName} was created from a different client package version. Run the game-update check before rebuilding.");

        var textureNativeIndices = new List<int>();
        foreach (var texture in request.TextureProjects)
        {
            var native = await Textures.NativeTextureExportService.LoadManifestAsync(
                texture.Manifest.NativeTextureManifestPath, cancellationToken).ConfigureAwait(false);
            if (native.NativeExportIndex is null)
                throw new NotSupportedException($"{texture.Manifest.ObjectName} has no confirmed native export index.");
            textureNativeIndices.Add(native.NativeExportIndex.Value);
        }
        foreach (var particle in request.ParticleEdits)
            if (!SamePath(particle.Document.SourcePackagePath, sourcePath) ||
                !particle.Document.SourcePackageSha256.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"{particle.Document.SystemObjectName} was created from a different client package version. Run the game-update check before rebuilding.");
        var targetIndices = textureNativeIndices
            .Concat(materialInputs.Select(input => input.Report.NativeExportIndex!.Value))
            .Concat(meshInputs.Select(input => input.Source.NativeExportIndex))
            .Concat(staticMeshInputs.Select(input => input.Source.NativeExportIndex))
            .Concat(request.ParticleEdits.SelectMany(particle => particle.Document.Patches.Select(patch => patch.ExportIndex).Distinct()))
            .Concat(request.InterfaceMovieEdits.Select(edit => edit.Manifest.NativeExportIndex)).ToArray();
        var duplicate = targetIndices.GroupBy(value => value).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new NotSupportedException(
                $"More than one selected project targets native export {duplicate.Key}; Arkive cannot choose which edit wins.");

        var totalSteps = checked(request.TextureProjects.Count * 9 +
                                 request.MaterialEdits.Count * MaterialSteps +
                                 request.MeshEdits.Count * MeshSteps +
                                 request.StaticMeshEdits.Count * MeshSteps +
                                 request.ParticleEdits.Count +
                                 request.InterfaceMovieEdits.Count * InterfaceMovieSteps + 2);
        var step = 0;
        void Report(string objectName, string stage, string message) => progress?.Report(
            new UnifiedPackageBuildProgress(++step, totalSteps, objectName, stage, message));

        string currentPackagePath = sourcePath;
        string? finalTextureManifest = null;
        IReadOnlyList<GuidedTexturePackageAssetRevision> textureAssets = [];
        var stages = new List<UnifiedPackageCompositionStage>();
        if (request.TextureProjects.Count > 0)
        {
            var textureRoot = Path.Combine(root, "01-textures");
            var textureProgress = progress is null
                ? null
                : new Progress<GuidedTexturePackageBuildProgress>(update => progress.Report(
                    new UnifiedPackageBuildProgress(
                        update.Step, totalSteps, update.ObjectName, "Texture2D", update.Message)));
            var textureStage = await _textureBuilder.BuildStagingAsync(
                request.Package, request.TextureProjects, textureRoot, textureProgress,
                cancellationToken).ConfigureAwait(false);
            step = request.TextureProjects.Count * 9;
            currentPackagePath = textureStage.OutputUpkPath;
            finalTextureManifest = textureStage.FinalReplacementUpkBuildManifestPath;
            textureAssets = textureStage.Assets;
            var previousHash = sourceHash;
            foreach (var asset in textureAssets)
            {
                var replacement = await Textures.ReplacementUpkBuildService.VerifyOutputAsync(
                    asset.ReplacementUpkBuildManifestPath, cancellationToken).ConfigureAwait(false);
                stages.Add(new UnifiedPackageCompositionStage(
                    stages.Count + 1, UnifiedPackageAssetKind.Texture2D, asset.ObjectName,
                    asset.NativeExportIndex, previousHash, replacement.OutputSha256,
                    replacement.OutputUpkPath, asset.ReplacementUpkBuildManifestPath));
                previousHash = replacement.OutputSha256;
            }
        }

        // Materials and particle effects keep their size, so every one of them goes into the package in a single
        // pass: writing and re-checking the whole package once per effect made a 200-effect package take minutes.
        // Each candidate is still proven against the bytes it was made from, and the one staged copy is checked
        // export by export (unrelated bytes identical, blocks decode back, catalogue unchanged).
        var materialAssets = new List<GuidedMaterialPackageAssetRevision>(materialInputs.Count);
        var particleAssets = new List<GuidedParticlePackageAssetRevision>(request.ParticleEdits.Count);
        if (materialInputs.Count > 0 || request.ParticleEdits.Count > 0)
        {
            var valuesRoot = Path.Combine(root, "v01-values");
            Directory.CreateDirectory(valuesRoot);
            var inputPackageHash = await HashFileAsync(currentPackagePath, cancellationToken).ConfigureAwait(false);
            var catalogue = _catalogReader.Read(currentPackagePath);
            var replacements = new List<SerializedExportReplacement>();
            var materialCandidates = new List<(MaterialInstanceEditEntry Edit, int NativeIndex, MaterialInstanceCandidateResult Candidate)>();
            foreach (var (edit, report) in materialInputs.OrderBy(input => input.Report.NativeExportIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nativeIndex = report.NativeExportIndex!.Value;
                var asset = catalogue.Assets.SingleOrDefault(candidate => candidate.ExportIndex == nativeIndex) ??
                            throw new NotSupportedException(
                                $"Native export {nativeIndex} for {edit.Document.ObjectName} is missing from the cumulative UPK.");
                if (!asset.ObjectName.Equals(edit.Document.ObjectName, StringComparison.OrdinalIgnoreCase) ||
                    !asset.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException(
                        $"Native export {nativeIndex} no longer identifies MaterialInstanceConstant {edit.Document.ObjectName}.");
                var currentExportHash = await HashLogicalExportAsync(
                    catalogue.Summary, asset, cancellationToken).ConfigureAwait(false);
                if (!currentExportHash.Equals(report.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException(
                        $"{edit.Document.ObjectName}'s source bytes changed before its material stage.");

                Report(edit.Document.ObjectName, "Material candidate",
                    "Building the locked-byte-preserving value/reference candidate...");
                var candidate = await _materialEditService.BuildCandidateAsync(
                    edit.DocumentPath, Path.Combine(valuesRoot, $"i{materialCandidates.Count + 1:00}-{edit.Document.EditId[..8]}"),
                    cancellationToken).ConfigureAwait(false);
                if (!candidate.Manifest.SameSize || !candidate.Manifest.LockedBytesPreserved ||
                    candidate.Manifest.ChangedByteCount <= 0)
                    throw new NotSupportedException(
                        $"{edit.Document.ObjectName} has no verified parameter change to compose.");
                var bytes = await File.ReadAllBytesAsync(candidate.CandidatePath, cancellationToken).ConfigureAwait(false);
                if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(candidate.Manifest.CandidateExportSha256, StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException($"{edit.Document.ObjectName}'s candidate changed after it was verified.");
                replacements.Add(new SerializedExportReplacement(nativeIndex, asset.ClassName, asset.ObjectName,
                    asset.SerialOffset, asset.SerialSize, currentExportHash, bytes));
                materialCandidates.Add((edit, nativeIndex, candidate));
                Report(edit.Document.ObjectName, "Material package", "Queued for the package's single values pass...");
            }

            var particleExports = new List<(Particles.ParticleEffectEditEntry Particle, int[] ExportIndices)>();
            using (var logical = new LogicalPackageStream(catalogue.Summary, _protectionProvider))
            {
                foreach (var particle in request.ParticleEdits.OrderBy(edit => edit.Document.SystemExportIndex))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var document = particle.Document;
                    Report(document.SystemObjectName, "Particle effect", "Writing the effect's new colour values into the package...");
                    var indices = new List<int>();
                    foreach (var group in document.Patches.GroupBy(patch => patch.ExportIndex))
                    {
                        var first = group.First();
                        var asset = catalogue.Assets.SingleOrDefault(candidate => candidate.ExportIndex == group.Key) ??
                                    throw new NotSupportedException($"{first.ObjectPath} is missing from the cumulative UPK.");
                        if (!asset.ObjectPath.Equals(first.ObjectPath, StringComparison.OrdinalIgnoreCase) ||
                            !asset.ClassName.Equals(first.ClassName, StringComparison.OrdinalIgnoreCase) ||
                            asset.SerialSize != first.SerialSize)
                            throw new NotSupportedException($"Native export {group.Key} no longer identifies {first.ObjectPath}.");
                        var bytes = new byte[asset.SerialSize];
                        logical.Position = asset.SerialOffset;
                        logical.ReadExactly(bytes);
                        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(first.SourceExportSha256, StringComparison.OrdinalIgnoreCase))
                            throw new NotSupportedException($"{first.ObjectPath}'s bytes changed during an earlier cumulative stage.");
                        replacements.Add(new SerializedExportReplacement(asset.ExportIndex, asset.ClassName, asset.ObjectName,
                            asset.SerialOffset, asset.SerialSize, first.SourceExportSha256,
                            Particles.ParticleEffects.ApplyPatches(bytes, group)));
                        indices.Add(asset.ExportIndex);
                    }
                    particleExports.Add((particle, [.. indices]));
                }
            }

            // The staged copy is bound to everything that went into it: each material candidate and each effect document.
            var evidencePath = Path.Combine(valuesRoot, "arkive-values-pass.json");
            await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(new
            {
                Materials = materialCandidates.Select(item => new { item.Edit.DocumentPath, item.Candidate.ManifestPath }),
                ParticleEffects = particleExports.Select(item => new { item.Particle.DocumentPath, item.Particle.Document.ContentSha256 }),
            }, JsonOptions), cancellationToken).ConfigureAwait(false);
            var staged = await _meshStaging.BuildSerializedExportsAsync(
                new SerializedExportsStagedUpkRequest(
                    currentPackagePath, inputPackageHash, Path.GetFileName(sourcePath), request.Package.Manifest.PackageAlias,
                    replacements, evidencePath, Path.Combine(valuesRoot, "staged-upk")),
                cancellationToken).ConfigureAwait(false);
            currentPackagePath = staged.OutputUpkPath;
            var stagedExports = staged.Manifest.Exports.ToDictionary(export => export.NativeExportIndex);
            foreach (var (edit, nativeIndex, candidate) in materialCandidates)
            {
                var export = stagedExports[nativeIndex];
                stages.Add(new UnifiedPackageCompositionStage(
                    stages.Count + 1, UnifiedPackageAssetKind.MaterialInstanceConstant,
                    edit.Document.ObjectName, nativeIndex, inputPackageHash,
                    staged.Manifest.OutputPackageSha256, staged.OutputUpkPath, staged.ManifestPath));
                materialAssets.Add(new GuidedMaterialPackageAssetRevision(
                    edit.Document.EditId, edit.Document.DisplayName, edit.Document.ObjectName,
                    nativeIndex, edit.DocumentPath, candidate.ManifestPath,
                    candidate.Manifest.CandidateExportSha256, staged.ManifestPath,
                    export.SerialOffset, export.SerialSize));
            }
            foreach (var (particle, indices) in particleExports)
            {
                var document = particle.Document;
                stages.Add(new UnifiedPackageCompositionStage(
                    stages.Count + 1, UnifiedPackageAssetKind.ParticleSystem, document.SystemObjectName,
                    document.SystemExportIndex, inputPackageHash, staged.Manifest.OutputPackageSha256,
                    staged.OutputUpkPath, staged.ManifestPath));
                particleAssets.Add(new GuidedParticlePackageAssetRevision(
                    document.EditId, document.DisplayName, document.SystemObjectName, document.SystemExportIndex,
                    particle.DocumentPath, document.ContentSha256, staged.ManifestPath,
                    indices.Select(index => stagedExports[index]).ToArray()));
            }
        }

        var meshAssets = new List<GuidedMeshPackageAssetRevision>(meshInputs.Count);
        foreach (var (edit, originalSource) in meshInputs.OrderBy(input => input.Source.NativeExportIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var meshRoot = Path.Combine(root, $"m{meshAssets.Count + 1:00}-{edit.Manifest.EditId[..8]}");
            Directory.CreateDirectory(meshRoot);
            var inputPackageHash = await HashFileAsync(currentPackagePath, cancellationToken).ConfigureAwait(false);
            var catalogue = _catalogReader.Read(currentPackagePath);
            var asset = catalogue.Assets.SingleOrDefault(candidate =>
                candidate.ExportIndex == originalSource.NativeExportIndex) ??
                        throw new NotSupportedException(
                            $"Native export {originalSource.NativeExportIndex} for {edit.Manifest.ObjectName} is missing from the cumulative UPK.");
            if (!asset.ObjectName.Equals(edit.Manifest.ObjectName, StringComparison.OrdinalIgnoreCase) ||
                !asset.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"Native export {asset.ExportIndex} no longer identifies SkeletalMesh {edit.Manifest.ObjectName}.");
            var cumulativeSourceExportHash = await HashLogicalExportAsync(
                catalogue.Summary, asset, cancellationToken).ConfigureAwait(false);
            if (!cumulativeSourceExportHash.Equals(
                    originalSource.SourceExportSha256, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"{edit.Manifest.ObjectName}'s native source bytes changed during an earlier cumulative stage.");

            Report(edit.Manifest.ObjectName, "Mesh recon", "Reconnoitring the mesh in the cumulative texture/mesh package...");
            var reconReport = await _meshReconnaissance.InspectAsync(
                new SkeletalMeshReconnaissanceRequest(
                    currentPackagePath, request.Package.Manifest.PackageAlias, asset.ExportIndex,
                    AssetKind.SkeletalMesh, asset.ClassName, asset.ObjectName,
                    asset.SerialOffset, asset.SerialSize, inputPackageHash)
                {
                    SkipUmodelOracle = true,
                }, cancellationToken)
                .ConfigureAwait(false);
            if (reconReport.Readiness == SkeletalMeshReadiness.Refused ||
                reconReport.NativeExportIndex != originalSource.NativeExportIndex)
                throw new NotSupportedException(
                    $"{edit.Manifest.ObjectName} could not be safely reconnoitred in the cumulative package.");

            // UModel resolves Lost Ark package imports partly from the physical filename. Every
            // cumulative texture stage intentionally has a generic Arkive staging filename, so
            // asking UModel for material names here can return an incomplete/incorrect mapping.
            // The complete oracle from the original editable export is safe to reuse only because
            // the logical SkeletalMesh export was hash-proven byte-identical immediately above.
            // The final package is still written under its original filename and must pass a fresh
            // exact-object UModel validation before this composition can be retained.
            var trustedOracle = await LoadTrustedSourceOracleAsync(
                originalSource, cumulativeSourceExportHash, cancellationToken).ConfigureAwait(false);
            if (trustedOracle is not null)
            {
                reconReport = reconReport with
                {
                    Oracle = trustedOracle,
                    Issues = reconReport.Issues.Append(new SkeletalMeshReconnaissanceIssue(
                        "Information", "SKM304",
                        "Reused the hash-bound source UModel material oracle while rebasing this byte-identical export from a cumulative staging filename; final exact-object validation remains mandatory.")).ToArray(),
                };
            }
            var recon = await new SkeletalMeshReconnaissanceWorkspaceStore(
                    Path.Combine(meshRoot, "01-recon"))
                .SaveAsync(reconReport, cancellationToken).ConfigureAwait(false);

            Report(edit.Manifest.ObjectName, "Mesh rebase", "Exporting a stable editable reference from the cumulative package...");
            var editable = await _meshEditableExport.ExportAsync(
                new SkeletalMeshEditableExportRequest(
                    reconReport, recon.JsonPath, Path.Combine(meshRoot, "02-editable-reference")),
                cancellationToken).ConfigureAwait(false);
            var editableInfo = new FileInfo(editable.GlbPath);
            var editableEntry = new SkeletalMeshEditableWorkspaceEntry(
                editable.OutputDirectory, editable.ManifestPath, editable.GlbPath, editable.Manifest,
                editableInfo.Exists, editableInfo.Exists && editableInfo.Length == editable.Manifest.GlbBytes,
                editable.Manifest.SchemaVersion == SkeletalMeshEditableExportService.CurrentSchemaVersion);

            Report(edit.Manifest.ObjectName, "Mesh preflight", "Rebasing and revalidating the edited GLB without changing the user's file...");
            var rebased = await new SkeletalMeshEditWorkspaceStore(
                    Path.Combine(meshRoot, "03-rebased-edit"))
                .RegisterAsync(editableEntry, edit.EditedGlbPath, edit.Manifest.DisplayName,
                    cancellationToken).ConfigureAwait(false);
            if (rebased.Preflight.Readiness != edit.Manifest.Preflight.Readiness ||
                rebased.Preflight.ErrorCount != 0)
                throw new NotSupportedException(
                    $"{edit.Manifest.ObjectName} did not retain its {edit.Manifest.Preflight.Readiness} route while rebasing onto the cumulative package.");

            if (rebased.Preflight.Readiness == SkeletalMeshEditReadiness.ReadyForTopologyCompilation)
            {
                Report(edit.Manifest.ObjectName, "Topology compilation",
                    "Compiling variable-size native LODs and decoding them back semantically...");
                var compilation = await _meshTopologyCompiler.CompileAsync(
                    new SkeletalMeshTopologyCompilationRequest(
                        rebased.Entry, Path.Combine(meshRoot, "04-topology-compilation")),
                    cancellationToken).ConfigureAwait(false);
                if (!compilation.Manifest.PackageIntegrationEnabled ||
                    !compilation.Manifest.DecodeBackPassed ||
                    !compilation.Manifest.PrefixPreservedExceptBounds ||
                    !compilation.Manifest.TrailingBytesPreserved)
                    throw new NotSupportedException(
                        $"{edit.Manifest.ObjectName}'s topology compiler proof is incomplete.");

                Report(edit.Manifest.ObjectName, "Mesh package",
                    "Appending and retargeting the variable-size mesh in the cumulative UPK...");
                var staged = await _meshStaging.BuildTopologyAsync(
                    new SkeletalMeshTopologyStagedUpkRequest(
                        compilation.ManifestPath, Path.Combine(meshRoot, "05-topology-staged-upk")),
                    cancellationToken).ConfigureAwait(false);
                if (!staged.Manifest.UmodelPreviewReady || staged.Manifest.DeploymentReady ||
                    !staged.Manifest.AllUnplannedSourceBytesIdentical ||
                    !staged.Manifest.UnrelatedExportsEquivalent ||
                    !staged.Manifest.TargetNativeDecodePassed ||
                    !staged.Manifest.OriginalExportBytesRetained)
                    throw new NotSupportedException(
                        $"{edit.Manifest.ObjectName}'s cumulative topology stage did not retain every proof gate.");
                currentPackagePath = staged.OutputUpkPath;
                stages.Add(new UnifiedPackageCompositionStage(
                    stages.Count + 1, UnifiedPackageAssetKind.SkeletalMesh,
                    edit.Manifest.ObjectName, originalSource.NativeExportIndex,
                    inputPackageHash, staged.Manifest.OutputPackageSha256,
                    staged.OutputUpkPath, staged.ManifestPath)
                {
                    VariableSizeReplacement = true,
                });
                meshAssets.Add(new GuidedMeshPackageAssetRevision(
                    edit.Manifest.EditId, edit.Manifest.DisplayName, edit.Manifest.ObjectName,
                    originalSource.NativeExportIndex, edit.Manifest.EditedGlbSha256,
                    editable.ManifestPath, rebased.Entry.ManifestPath, string.Empty,
                    staged.ManifestPath, staged.Manifest.AppendedSerialOffset,
                    staged.Manifest.AppendedSerialSize)
                {
                    VariableSizeReplacement = true,
                    TopologyCompilationManifestPath = compilation.ManifestPath,
                });
            }
            else
            {
                Report(edit.Manifest.ObjectName, "Mesh encoding",
                    "Encoding bounded native position/tangent changes and decode-checking them...");
                var encoding = await _meshNativeEncoding.EncodeAsync(
                    new SkeletalMeshNativeEncodingRequest(
                        rebased.Entry, Path.Combine(meshRoot, "04-native-encoding")), cancellationToken)
                    .ConfigureAwait(false);

                Report(edit.Manifest.ObjectName, "Mesh package",
                    "Applying the verified same-size mesh export to the cumulative UPK...");
                var staged = await _meshStaging.BuildAsync(
                    new SkeletalMeshStagedUpkRequest(
                        encoding.ManifestPath, Path.Combine(meshRoot, "05-staged-upk")), cancellationToken)
                    .ConfigureAwait(false);
                if (!staged.Manifest.UmodelPreviewReady || staged.Manifest.DeploymentReady ||
                    !staged.Manifest.AllUnrelatedLogicalBytesIdentical ||
                    !staged.Manifest.TargetNativeDecodePassed)
                    throw new NotSupportedException(
                        $"{edit.Manifest.ObjectName}'s cumulative mesh stage did not retain every proof gate.");
                currentPackagePath = staged.OutputUpkPath;
                stages.Add(new UnifiedPackageCompositionStage(
                    stages.Count + 1, UnifiedPackageAssetKind.SkeletalMesh,
                    edit.Manifest.ObjectName, originalSource.NativeExportIndex,
                    inputPackageHash, staged.Manifest.OutputPackageSha256,
                    staged.OutputUpkPath, staged.ManifestPath));
                meshAssets.Add(new GuidedMeshPackageAssetRevision(
                    edit.Manifest.EditId, edit.Manifest.DisplayName, edit.Manifest.ObjectName,
                    originalSource.NativeExportIndex, edit.Manifest.EditedGlbSha256,
                    editable.ManifestPath, rebased.Entry.ManifestPath, encoding.ManifestPath,
                    staged.ManifestPath, staged.Manifest.SerialOffset, staged.Manifest.SerialSize));
            }
        }

        var staticMeshAssets = new List<GuidedStaticMeshPackageAssetRevision>(staticMeshInputs.Count);
        foreach (var (edit, originalSource) in staticMeshInputs.OrderBy(input => input.Source.NativeExportIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var staticRoot = Path.Combine(root, $"s{staticMeshAssets.Count + 1:00}-{edit.Manifest.EditId[..8]}");
            Directory.CreateDirectory(staticRoot);
            var inputPackageHash = await HashFileAsync(currentPackagePath, cancellationToken).ConfigureAwait(false);
            var catalogue = _catalogReader.Read(currentPackagePath);
            var asset = catalogue.Assets.SingleOrDefault(candidate =>
                candidate.ExportIndex == originalSource.NativeExportIndex) ??
                        throw new NotSupportedException(
                            $"Native export {originalSource.NativeExportIndex} for {originalSource.ObjectName} is missing from the cumulative UPK.");
            if (!asset.ObjectName.Equals(originalSource.ObjectName, StringComparison.OrdinalIgnoreCase) ||
                !asset.ClassName.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase) ||
                asset.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"Native export {asset.ExportIndex} no longer identifies StaticMesh {originalSource.ObjectName}.");
            var cumulativeSourceExportHash = await HashLogicalExportAsync(
                catalogue.Summary, asset, cancellationToken).ConfigureAwait(false);
            if (!cumulativeSourceExportHash.Equals(originalSource.SourceExportSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"{originalSource.ObjectName}'s native source bytes changed during an earlier cumulative stage.");

            Report(originalSource.ObjectName, "StaticMesh recon",
                "Reconnoitring render geometry, materials, and retained collision in the cumulative package...");
            var reconReport = await _staticMeshReconnaissance.InspectAsync(
                new StaticMeshReconnaissanceRequest(
                    currentPackagePath, request.Package.Manifest.PackageAlias, asset.ExportIndex,
                    AssetKind.StaticMesh, asset.ClassName, asset.ObjectName,
                    asset.SerialOffset, asset.SerialSize, inputPackageHash)
                {
                    SkipUmodelOracle = true,
                }, cancellationToken).ConfigureAwait(false);
            if (reconReport.Readiness != StaticMeshReadiness.Editable ||
                reconReport.NativeExportIndex != originalSource.NativeExportIndex ||
                reconReport.NativeLayout is null || reconReport.NativeDataRelativeOffset is null)
                throw new NotSupportedException(
                    $"{originalSource.ObjectName} could not be safely reconnoitred in the cumulative package.");
            var recon = await new StaticMeshReconnaissanceWorkspaceStore(Path.Combine(staticRoot, "01-recon"))
                .SaveAsync(reconReport, cancellationToken: cancellationToken).ConfigureAwait(false);

            Report(originalSource.ObjectName, "StaticMesh rebase",
                "Exporting a stable native reference from the cumulative package...");
            var editable = await _staticMeshEditableExport.ExportAsync(
                new StaticMeshEditableExportRequest(
                    reconReport, recon.JsonPath, Path.Combine(staticRoot, "02-editable-reference")),
                cancellationToken).ConfigureAwait(false);
            Report(originalSource.ObjectName, "StaticMesh preflight",
                "Rebasing the edited GLB and validating vertex identity, sections, bounds, and collision policy...");
            var rebasedStore = new StaticMeshEditWorkspaceStore(
                Path.Combine(staticRoot, "03-rebased-edit"), protectionProvider: _protectionProvider);
            var rebased = await rebasedStore.RegisterAsync(
                editable.ManifestPath, edit.Manifest.EditedGlbPath, edit.Manifest.DisplayName,
                cancellationToken).ConfigureAwait(false);
            if (rebased.Manifest.Preflight.Readiness == StaticMeshEditReadiness.Refused)
                throw new NotSupportedException(
                    $"{originalSource.ObjectName} failed StaticMesh preflight after cumulative package rebasing: " +
                    string.Join(" | ", rebased.Manifest.Preflight.Issues));

            Report(originalSource.ObjectName, "StaticMesh compilation",
                "Compiling native StaticMesh3 render streams and independently decoding them back...");
            var compilation = await _staticMeshCompilation.CompileAsync(
                rebased.ManifestPath, Path.Combine(staticRoot, "04-compilation"), cancellationToken)
                .ConfigureAwait(false);
            if (!compilation.Manifest.DecodeBackPassed || !compilation.Manifest.CollisionPreservedFromSource)
                throw new NotSupportedException(
                    $"{originalSource.ObjectName}'s StaticMesh compiler proof is incomplete.");
            var variableSize = compilation.Manifest.CompiledSerialSize != compilation.Manifest.SourceSerialSize;

            string stagedManifestPath;
            string stagedOutputPath;
            string stagedOutputHash;
            long finalOffset;
            long finalSize;
            if (variableSize)
            {
                Report(originalSource.ObjectName, "StaticMesh package",
                    "Appending and retargeting the variable-size StaticMesh in the cumulative UPK...");
                var staged = await _meshStaging.BuildVariableSerializedExportAsync(
                    new VariableSerializedExportStagedUpkRequest(
                        currentPackagePath, inputPackageHash, Path.GetFileName(sourcePath),
                        request.Package.Manifest.PackageAlias, asset.ExportIndex, asset.ClassName,
                        asset.ObjectName, asset.SerialOffset, asset.SerialSize,
                        cumulativeSourceExportHash, compilation.CompiledExportPath,
                        compilation.Manifest.CompiledExportSha256, compilation.ManifestPath,
                        reconReport.NativeDataRelativeOffset.Value,
                        Path.Combine(staticRoot, "05-variable-staged-upk")), cancellationToken)
                    .ConfigureAwait(false);
                if (!staged.Manifest.UmodelPreviewReady || !staged.Manifest.TargetNativeDecodePassed ||
                    !staged.Manifest.OriginalExportBytesRetained ||
                    !staged.Manifest.AllUnplannedSourceBytesIdentical)
                    throw new NotSupportedException(
                        $"{originalSource.ObjectName}'s variable-size StaticMesh stage lost a required proof gate.");
                stagedManifestPath = staged.ManifestPath;
                stagedOutputPath = staged.OutputUpkPath;
                stagedOutputHash = staged.Manifest.OutputPackageSha256;
                finalOffset = staged.Manifest.AppendedSerialOffset;
                finalSize = staged.Manifest.AppendedSerialSize;
            }
            else
            {
                Report(originalSource.ObjectName, "StaticMesh package",
                    "Applying the verified same-size StaticMesh export to the cumulative UPK...");
                var staged = await _meshStaging.BuildSerializedExportAsync(
                    new SerializedExportStagedUpkRequest(
                        currentPackagePath, inputPackageHash, Path.GetFileName(sourcePath),
                        request.Package.Manifest.PackageAlias, asset.ExportIndex, asset.ClassName,
                        asset.ObjectName, asset.SerialOffset, asset.SerialSize,
                        cumulativeSourceExportHash, compilation.CompiledExportPath,
                        compilation.Manifest.CompiledExportSha256, compilation.ManifestPath,
                        Path.Combine(staticRoot, "05-staged-upk")), cancellationToken)
                    .ConfigureAwait(false);
                stagedManifestPath = staged.ManifestPath;
                stagedOutputPath = staged.OutputUpkPath;
                stagedOutputHash = staged.Manifest.OutputPackageSha256;
                finalOffset = staged.Manifest.SerialOffset;
                finalSize = staged.Manifest.SerialSize;
            }
            currentPackagePath = stagedOutputPath;
            stages.Add(new UnifiedPackageCompositionStage(
                stages.Count + 1, UnifiedPackageAssetKind.StaticMesh, originalSource.ObjectName,
                originalSource.NativeExportIndex, inputPackageHash, stagedOutputHash,
                stagedOutputPath, stagedManifestPath)
            {
                VariableSizeReplacement = variableSize,
            });
            staticMeshAssets.Add(new GuidedStaticMeshPackageAssetRevision(
                edit.Manifest.EditId, edit.Manifest.DisplayName, originalSource.ObjectName,
                originalSource.NativeExportIndex, edit.Manifest.EditedGlbSha256,
                editable.ManifestPath, rebased.ManifestPath, compilation.ManifestPath,
                stagedManifestPath, finalOffset, finalSize, variableSize));
        }

        var movieAssets = new List<GuidedInterfaceMoviePackageAssetRevision>(request.InterfaceMovieEdits.Count);
        foreach (var edit in request.InterfaceMovieEdits.OrderBy(item => item.Manifest.NativeExportIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var movie = edit.Manifest;
            var movieRoot = Path.Combine(root, $"u{movieAssets.Count + 1:00}-{movie.EditId[..8]}");
            Directory.CreateDirectory(movieRoot);
            var inputPackageHash = await HashFileAsync(currentPackagePath, cancellationToken).ConfigureAwait(false);
            var catalogue = _catalogReader.Read(currentPackagePath);
            var asset = catalogue.Assets.SingleOrDefault(candidate => candidate.ExportIndex == movie.NativeExportIndex) ??
                        throw new NotSupportedException(
                            $"Native export {movie.NativeExportIndex} for {movie.ObjectName} is missing from the cumulative UPK.");
            if (!asset.ObjectName.Equals(movie.ObjectName, StringComparison.OrdinalIgnoreCase) ||
                !AssetTypes.InterfaceMovie.CanEditClass(asset.ClassName))
                throw new NotSupportedException(
                    $"Native export {asset.ExportIndex} no longer identifies interface movie {movie.ObjectName}.");
            var exportBytes = await ReadLogicalExportAsync(catalogue.Summary, asset, cancellationToken).ConfigureAwait(false);
            var currentExportHash = Convert.ToHexString(SHA256.HashData(exportBytes));
            if (!currentExportHash.Equals(movie.SourceExportSha256, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"{movie.ObjectName}'s source bytes changed before its interface movie stage.");

            Report(movie.ObjectName, "Interface movie", "Putting the edited movie into the game's export...");
            var edited = await InterfaceMovieEditWorkspaceStore.ReadEditedMovieAsync(edit, cancellationToken).ConfigureAwait(false);
            var candidate = await InterfaceMovieCandidateService.BuildAsync(
                exportBytes, inputPackageHash, asset.ExportIndex, asset.ClassName, asset.ObjectName, asset.SerialOffset,
                edited, Path.Combine(movieRoot, "01-candidate"), cancellationToken).ConfigureAwait(false);

            Report(movie.ObjectName, "Interface movie package", "Appending the edited interface movie to the cumulative UPK...");
            var staged = await _meshStaging.BuildInterfaceMovieAsync(
                new VariableSerializedExportStagedUpkRequest(
                    currentPackagePath, inputPackageHash, Path.GetFileName(sourcePath),
                    request.Package.Manifest.PackageAlias, asset.ExportIndex, asset.ClassName, asset.ObjectName,
                    asset.SerialOffset, asset.SerialSize, currentExportHash, candidate.CandidatePath,
                    candidate.Manifest.CandidateExportSha256, candidate.ManifestPath, 0,
                    Path.Combine(movieRoot, "02-staged-upk")), cancellationToken).ConfigureAwait(false);
            currentPackagePath = staged.OutputUpkPath;
            stages.Add(new UnifiedPackageCompositionStage(
                stages.Count + 1, UnifiedPackageAssetKind.InterfaceMovie, movie.ObjectName, asset.ExportIndex,
                inputPackageHash, staged.Manifest.OutputPackageSha256, staged.OutputUpkPath, staged.ManifestPath)
            {
                VariableSizeReplacement = true,
            });
            movieAssets.Add(new GuidedInterfaceMoviePackageAssetRevision(
                movie.EditId, movie.DisplayName, movie.ObjectName, asset.ExportIndex, candidate.Manifest.EditedMovieSha256,
                candidate.Manifest.CandidateExportSha256, staged.ManifestPath, staged.Manifest.AppendedSerialOffset,
                staged.Manifest.AppendedSerialSize));
        }

        Report("Package", "Final validation", "Verifying the final catalogue and clean-source identity...");
        var finalHash = await HashFileAsync(currentPackagePath, cancellationToken).ConfigureAwait(false);
        var finalInfo = new FileInfo(currentPackagePath);
        var finalCatalogue = _catalogReader.Read(currentPackagePath);
        var compositionAssets = new List<UnifiedPackageCompositionAsset>();
        foreach (var asset in textureAssets)
        {
            var catalogueAsset = finalCatalogue.Assets.SingleOrDefault(candidate =>
                candidate.ExportIndex == asset.NativeExportIndex &&
                candidate.ObjectName.Equals(asset.ObjectName, StringComparison.OrdinalIgnoreCase)) ??
                                 throw new InvalidDataException(
                                     $"The final package lost Texture2D {asset.ObjectName}.");
            compositionAssets.Add(new UnifiedPackageCompositionAsset(
                UnifiedPackageAssetKind.Texture2D, asset.ProjectId, asset.ObjectName,
                request.Package.Manifest.PackageAlias, asset.NativeExportIndex,
                catalogueAsset.ClassName, asset.ObjectName, asset.EditedImageSha256,
                await HashLogicalExportAsync(finalCatalogue.Summary, catalogueAsset, cancellationToken)
                    .ConfigureAwait(false), catalogueAsset.SerialOffset, catalogueAsset.SerialSize,
                asset.ReplacementUpkBuildManifestPath));
        }
        foreach (var asset in meshAssets)
        {
            var catalogueAsset = finalCatalogue.Assets.SingleOrDefault(candidate =>
                candidate.ExportIndex == asset.NativeExportIndex &&
                candidate.ObjectName.Equals(asset.ObjectName, StringComparison.OrdinalIgnoreCase)) ??
                                 throw new InvalidDataException(
                                     $"The final package lost SkeletalMesh {asset.ObjectName}.");
            var finalExportHash = await HashLogicalExportAsync(
                finalCatalogue.Summary, catalogueAsset, cancellationToken).ConfigureAwait(false);
            if (asset.VariableSizeReplacement)
            {
                if (string.IsNullOrWhiteSpace(asset.TopologyCompilationManifestPath))
                    throw new InvalidDataException(
                        $"The topology compilation identity for {asset.ObjectName} is missing.");
                var compilation = await SkeletalMeshTopologyCompilerService.LoadManifestAsync(
                    asset.TopologyCompilationManifestPath, cancellationToken).ConfigureAwait(false);
                if (!finalExportHash.Equals(
                        compilation.CompiledExportSha256, StringComparison.OrdinalIgnoreCase) ||
                    catalogueAsset.SerialOffset != asset.SerialOffset ||
                    catalogueAsset.SerialSize != asset.SerialSize)
                    throw new InvalidDataException(
                        $"The final package no longer contains the retargeted topology mesh bytes for {asset.ObjectName}.");
            }
            else
            {
                var encoding = await SkeletalMeshNativeEncodingService.LoadManifestAsync(
                    asset.NativeEncodingManifestPath, cancellationToken).ConfigureAwait(false);
                if (!finalExportHash.Equals(
                        encoding.EncodedExportSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"The final package no longer contains the encoded mesh bytes for {asset.ObjectName}.");
            }
            compositionAssets.Add(new UnifiedPackageCompositionAsset(
                UnifiedPackageAssetKind.SkeletalMesh, asset.EditId, asset.DisplayName,
                request.Package.Manifest.PackageAlias, asset.NativeExportIndex,
                catalogueAsset.ClassName, asset.ObjectName, asset.EditedGlbSha256,
                finalExportHash, catalogueAsset.SerialOffset, catalogueAsset.SerialSize,
                asset.StagedUpkManifestPath)
            {
                VariableSizeReplacement = asset.VariableSizeReplacement,
            });
        }
        foreach (var asset in staticMeshAssets)
        {
            var catalogueAsset = finalCatalogue.Assets.SingleOrDefault(candidate =>
                candidate.ExportIndex == asset.NativeExportIndex &&
                candidate.ObjectName.Equals(asset.ObjectName, StringComparison.OrdinalIgnoreCase)) ??
                                 throw new InvalidDataException(
                                     $"The final package lost StaticMesh {asset.ObjectName}.");
            var finalExportHash = await HashLogicalExportAsync(
                finalCatalogue.Summary, catalogueAsset, cancellationToken).ConfigureAwait(false);
            var compilation = await StaticMeshCompilationService.LoadManifestAsync(
                asset.CompilationManifestPath, cancellationToken).ConfigureAwait(false);
            if (!finalExportHash.Equals(compilation.CompiledExportSha256, StringComparison.OrdinalIgnoreCase) ||
                catalogueAsset.SerialOffset != asset.SerialOffset ||
                catalogueAsset.SerialSize != asset.SerialSize)
                throw new InvalidDataException(
                    $"The final package no longer contains the compiled StaticMesh bytes for {asset.ObjectName}.");
            compositionAssets.Add(new UnifiedPackageCompositionAsset(
                UnifiedPackageAssetKind.StaticMesh, asset.EditId, asset.DisplayName,
                request.Package.Manifest.PackageAlias, asset.NativeExportIndex,
                catalogueAsset.ClassName, asset.ObjectName, asset.EditedGlbSha256,
                finalExportHash, catalogueAsset.SerialOffset, catalogueAsset.SerialSize,
                asset.StagedUpkManifestPath)
            {
                VariableSizeReplacement = asset.VariableSizeReplacement,
            });
        }
        foreach (var asset in materialAssets)
        {
            var catalogueAsset = finalCatalogue.Assets.SingleOrDefault(candidate =>
                candidate.ExportIndex == asset.NativeExportIndex &&
                candidate.ObjectName.Equals(asset.ObjectName, StringComparison.OrdinalIgnoreCase)) ??
                                 throw new InvalidDataException(
                                     $"The final package lost MaterialInstanceConstant {asset.ObjectName}.");
            var finalExportHash = await HashLogicalExportAsync(
                finalCatalogue.Summary, catalogueAsset, cancellationToken).ConfigureAwait(false);
            if (!finalExportHash.Equals(asset.CandidateExportSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"The final package no longer contains the material candidate for {asset.ObjectName}.");
            compositionAssets.Add(new UnifiedPackageCompositionAsset(
                UnifiedPackageAssetKind.MaterialInstanceConstant, asset.EditId, asset.DisplayName,
                request.Package.Manifest.PackageAlias, asset.NativeExportIndex,
                catalogueAsset.ClassName, asset.ObjectName, asset.CandidateExportSha256,
                finalExportHash, catalogueAsset.SerialOffset, catalogueAsset.SerialSize,
                asset.StagedUpkManifestPath));
        }
        foreach (var particle in particleAssets)
        foreach (var export in particle.Exports)
        {
            var catalogueAsset = finalCatalogue.Assets.SingleOrDefault(candidate =>
                candidate.ExportIndex == export.NativeExportIndex &&
                candidate.ObjectName.Equals(export.ObjectName, StringComparison.OrdinalIgnoreCase)) ??
                                 throw new InvalidDataException($"The final package lost {export.ObjectName} of {particle.SystemObjectName}.");
            var finalExportHash = await HashLogicalExportAsync(finalCatalogue.Summary, catalogueAsset, cancellationToken)
                .ConfigureAwait(false);
            if (!finalExportHash.Equals(export.CandidateExportSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The final package no longer contains the new colours of {particle.SystemObjectName}.");
            compositionAssets.Add(new UnifiedPackageCompositionAsset(
                UnifiedPackageAssetKind.ParticleSystem, particle.EditId, particle.DisplayName,
                request.Package.Manifest.PackageAlias, export.NativeExportIndex, catalogueAsset.ClassName, export.ObjectName,
                particle.ContentSha256, finalExportHash, catalogueAsset.SerialOffset, catalogueAsset.SerialSize,
                particle.StagedUpkManifestPath));
        }
        foreach (var asset in movieAssets)
        {
            var catalogueAsset = finalCatalogue.Assets.SingleOrDefault(candidate =>
                candidate.ExportIndex == asset.NativeExportIndex &&
                candidate.ObjectName.Equals(asset.ObjectName, StringComparison.OrdinalIgnoreCase)) ??
                                 throw new InvalidDataException($"The final package lost interface movie {asset.ObjectName}.");
            var finalExportHash = await HashLogicalExportAsync(
                finalCatalogue.Summary, catalogueAsset, cancellationToken).ConfigureAwait(false);
            if (!finalExportHash.Equals(asset.CandidateExportSha256, StringComparison.OrdinalIgnoreCase) ||
                catalogueAsset.SerialOffset != asset.SerialOffset || catalogueAsset.SerialSize != asset.SerialSize)
                throw new InvalidDataException(
                    $"The final package no longer contains the edited interface movie for {asset.ObjectName}.");
            compositionAssets.Add(new UnifiedPackageCompositionAsset(
                UnifiedPackageAssetKind.InterfaceMovie, asset.EditId, asset.DisplayName,
                request.Package.Manifest.PackageAlias, asset.NativeExportIndex,
                catalogueAsset.ClassName, asset.ObjectName, asset.EditedMovieSha256,
                finalExportHash, catalogueAsset.SerialOffset, catalogueAsset.SerialSize,
                asset.StagedUpkManifestPath)
            {
                VariableSizeReplacement = true,
            });
        }
        var sourceHashAfter = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!sourceHashAfter.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The clean source package changed during composition.");

        var compositionId = Guid.NewGuid().ToString("N");
        // Interface movies change length too, so they take the same append-and-retarget route and deployment gate.
        var variableSizeMeshCount = meshAssets.Count(asset => asset.VariableSizeReplacement) +
                                    staticMeshAssets.Count(asset => asset.VariableSizeReplacement) +
                                    movieAssets.Count;
        var previewOnly = variableSizeMeshCount > 0 && request.ForcePreviewOnly;
        var variableSizeDeploymentGateRequested = variableSizeMeshCount > 0 && !previewOnly;
        var bundleAssets = compositionAssets.Select(asset => new ArkiveModAsset(
            asset.PackageAlias, asset.NativeExportIndex, asset.ClassName, asset.ObjectName,
            asset.Kind switch
            {
                UnifiedPackageAssetKind.Texture2D => request.TextureProjects.First(project =>
                    project.Manifest.ProjectId.Equals(asset.ProjectId, StringComparison.OrdinalIgnoreCase))
                    .Manifest.PixelFormat,
                UnifiedPackageAssetKind.MaterialInstanceConstant => "MaterialInstanceParameters",
                UnifiedPackageAssetKind.ParticleSystem => ArkivePackageModBundleService.ParticleSystemValuesFormat,
                UnifiedPackageAssetKind.StaticMesh => "NativeStaticMesh",
                UnifiedPackageAssetKind.InterfaceMovie => "ScaleformMovie",
                _ => "NativeSkeletalMesh",
            },
            asset.FinalSerialOffset, asset.FinalSerialSize)).ToArray();
        Report("Package", "Bundle", "Preparing one reversible combined bundle and deployment receipt...");
        var bundle = await _bundleService.BuildAsync(
            new ArkivePackageModBundleRequest(
                currentPackagePath, sourcePath, sourceHash, Path.Combine(root, "bundle"),
                request.BackupRootDirectory, request.DisplayName, compositionId, bundleAssets)
            {
                // Keep every variable-size bundle install-disabled until the
                // independent deployment receipt below has been written and
                // hash-bound into both manifests. A failed/interrupted build
                // therefore cannot leave an installable partial bundle.
                PreviewOnly = variableSizeMeshCount > 0,
                VariableSizeMeshCount = variableSizeMeshCount,
                VariableSizeDeploymentGatePassed = false,
            },
            cancellationToken).ConfigureAwait(false);

        var exactObjectExternalValidationPassed =
            bundle.Readiness.ExternalCataloguePassed &&
            bundle.Readiness.ExternalNativeLoadPassed &&
            bundle.Readiness.ExpectedObjectFound;
        string? variableSizeDeploymentGateManifestPath = null;
        var variableSizeDeploymentGatePassed = false;
        if (variableSizeDeploymentGateRequested)
        {
            var topologyVerifications = new List<SkeletalMeshTopologyStagedUpkVerification>(
                variableSizeMeshCount);
            foreach (var asset in meshAssets.Where(asset => asset.VariableSizeReplacement))
            {
                topologyVerifications.Add(await SkeletalMeshStagedUpkService.VerifyTopologyOutputAsync(
                        asset.StagedUpkManifestPath, cancellationToken)
                    .ConfigureAwait(false));
            }
            foreach (var asset in staticMeshAssets.Where(asset => asset.VariableSizeReplacement))
            {
                topologyVerifications.Add(await SkeletalMeshStagedUpkService.VerifyTopologyOutputAsync(
                        asset.StagedUpkManifestPath, cancellationToken)
                    .ConfigureAwait(false));
            }
            foreach (var asset in movieAssets)
                topologyVerifications.Add(await SkeletalMeshStagedUpkService.VerifyTopologyOutputAsync(
                        asset.StagedUpkManifestPath, cancellationToken)
                    .ConfigureAwait(false));
            var everyTopologyStageVerified = topologyVerifications.Count == variableSizeMeshCount &&
                topologyVerifications.All(item =>
                    item.Manifest.AppendAndRetargetReplacement &&
                    item.Manifest.ExportTablePatched &&
                    item.Manifest.TargetExportRetargeted &&
                    item.Manifest.ReplacementExportBytesVerified &&
                    item.Manifest.ExpectedLogicalImageMatched &&
                    item.Manifest.StructuralValidationPassed &&
                    item.Manifest.SourcePackageIdentityRetained &&
                    item.Manifest.OutputContainsReplacement);
            var everyUnrelatedExportRetained = topologyVerifications.All(item =>
                item.Manifest.OriginalExportBytesRetained &&
                item.Manifest.AllUnplannedSourceBytesIdentical &&
                item.Manifest.UnrelatedExportsEquivalent &&
                item.Manifest.AssetCatalogueShapeRetained &&
                item.Manifest.AllUnaffectedChunksByteIdentical);
            var everyAffectedChunkDecodedBack = topologyVerifications.All(item =>
                item.Manifest.AllEncodedBlocksDecodeBack &&
                item.Manifest.TargetNativeDecodePassed);
            var originalPhysicalPackageNameRetained =
                bundle.Readiness.PayloadUsesOriginalPhysicalName &&
                Path.GetFileName(bundle.PayloadPath).Equals(
                    request.Package.Manifest.PhysicalPackageName, StringComparison.OrdinalIgnoreCase);
            var backupCapacityPassed = bundle.Readiness.BackupSpaceAvailable;
            var atomicVariableSizeReplacementSupported = true;
            variableSizeDeploymentGatePassed =
                everyTopologyStageVerified && everyUnrelatedExportRetained &&
                everyAffectedChunkDecodedBack && exactObjectExternalValidationPassed &&
                originalPhysicalPackageNameRetained && backupCapacityPassed &&
                atomicVariableSizeReplacementSupported &&
                !bundle.Bundle.InstallationEnabled &&
                !bundle.Bundle.ReadyForControlledInstall &&
                !bundle.Readiness.ReadyForControlledInstall;
            var gateChecks = new[]
            {
                new ModReadinessCheck("VSD_TOPOLOGY", ModReadinessCheckStatus.Passed,
                    $"Verified native compilation, decode-back, and append-and-retarget manifests for {variableSizeMeshCount:N0} variable-size mesh replacement(s)."),
                new ModReadinessCheck("VSD_ISOLATION", ModReadinessCheckStatus.Passed,
                    "Every unrelated export, unaudited source byte, and unaffected stored chunk retained its required identity."),
                new ModReadinessCheck("VSD_EXTERNAL", ModReadinessCheckStatus.Passed,
                    "The final original-name payload passed exact-object UModel catalogue, native-load, and export validation for every selected asset."),
                new ModReadinessCheck("VSD_SOURCE", ModReadinessCheckStatus.Passed,
                    $"The clean source remained {sourceBytes:N0} bytes at SHA-256 {sourceHash}."),
                new ModReadinessCheck("VSD_BACKUP_ATOMIC_RESTORE", ModReadinessCheckStatus.Passed,
                    "The deployment contract requires a content-addressed byte-exact backup, exclusive target lock, sibling temporary copy, atomic replacement, post-write hash/structure verification, durable receipt, and guarded restore."),
            };
            var gate = new VariableSizeDeploymentGateManifest(
                CurrentVariableSizeDeploymentGateSchemaVersion, Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow, compositionId, ArkiveModBundleService.WesternGlobalProfile,
                variableSizeMeshCount, sourcePath, sourceBytes, sourceHash,
                bundle.PayloadPath, bundle.Bundle.PayloadBytes, bundle.Bundle.PayloadSha256,
                PackageSizeChanged: sourceBytes != bundle.Bundle.PayloadBytes,
                everyTopologyStageVerified, everyUnrelatedExportRetained,
                everyAffectedChunkDecodedBack, exactObjectExternalValidationPassed,
                originalPhysicalPackageNameRetained, backupCapacityPassed,
                atomicVariableSizeReplacementSupported, variableSizeDeploymentGatePassed,
                topologyVerifications.Select(item => item.ManifestPath).ToArray(), gateChecks);
            if (!variableSizeDeploymentGatePassed)
                throw new InvalidDataException(
                    "The variable-size controlled deployment gate did not retain every required proof.");
            variableSizeDeploymentGateManifestPath = Path.Combine(
                bundle.OutputDirectory, VariableSizeDeploymentGateManifestFileName);
            await SaveJsonAtomicAsync(
                variableSizeDeploymentGateManifestPath, gate, cancellationToken).ConfigureAwait(false);

            var gateHash = await HashFileAsync(
                variableSizeDeploymentGateManifestPath, cancellationToken).ConfigureAwait(false);
            var gateRelativePath = Path.GetFileName(variableSizeDeploymentGateManifestPath);
            var approvedCheck = new ModReadinessCheck(
                "COMBINED_VARIABLE_SIZE_DEPLOYMENT_GATE", ModReadinessCheckStatus.Passed,
                $"Approved {variableSizeMeshCount:N0} variable-size mesh replacement(s); " +
                $"deployment receipt SHA-256 {gateHash}.");
            var approvedChecks = bundle.Bundle.Checks
                .Where(check => !check.Code.Equals(
                    "COMBINED_PREVIEW_ONLY", StringComparison.Ordinal))
                .Append(approvedCheck)
                .ToArray();
            var approvedSteps = new[]
            {
                "Require Lost Ark, its launcher, and Easy Anti-Cheat to be closed.",
                "Recheck the exact clean source package size and SHA-256.",
                "Reopen and hash-verify the variable-size deployment-gate receipt.",
                "Create and hash-verify one content-addressed backup of the original package.",
                "Copy and hash-verify the combined payload to a temporary sibling of the target.",
                "Atomically replace the one install target with the combined package.",
                "Retain one durable receipt binding every asset, the payload, target, and backup.",
                "Restore only while the target still matches this payload, then verify the original hash.",
                "Refuse after a client update or any unrecognized package mutation.",
            };
            var approvedBundle = bundle.Bundle with
            {
                ReversibleInstallPlanned = true,
                ReadyForControlledInstall = true,
                InstallationEnabled = true,
                Checks = approvedChecks,
                VariableSizeDeploymentApproved = true,
                VariableSizeDeploymentGateRelativePath = gateRelativePath,
                VariableSizeDeploymentGateSha256 = gateHash,
            };
            var approvedReadiness = bundle.Readiness with
            {
                ReadyForControlledInstall = true,
                PlannedInstallSteps = approvedSteps,
                Checks = approvedChecks,
                VariableSizeDeploymentApproved = true,
                VariableSizeDeploymentGateRelativePath = gateRelativePath,
                VariableSizeDeploymentGateSha256 = gateHash,
            };
            await SaveJsonAtomicAsync(
                bundle.BundleManifestPath, approvedBundle, cancellationToken).ConfigureAwait(false);
            await SaveJsonAtomicAsync(
                bundle.ReadinessManifestPath, approvedReadiness, cancellationToken).ConfigureAwait(false);
            bundle = bundle with
            {
                Bundle = approvedBundle,
                Readiness = approvedReadiness,
                OutputFiles = bundle.OutputFiles
                    .Append(variableSizeDeploymentGateManifestPath)
                    .ToArray(),
            };
        }

        watch.Stop();
        var manifest = new UnifiedPackageCompositionManifest(
            CurrentSchemaVersion, compositionId, DateTimeOffset.UtcNow,
            request.Package.Manifest.PackageProjectId, request.Package.Manifest.PackageAlias,
            request.Package.Manifest.PhysicalPackageName, sourcePath, sourceBytes, sourceHash,
            currentPackagePath, finalInfo.Length, finalHash, textureAssets.Count, meshAssets.Count,
            TexturesAppliedBeforeMeshes: true, CleanSourceIdentityRetained: true,
            EveryStageVerified: true, EverySelectedAssetCatalogued: true,
            OnePhysicalPayloadProduced: true, compositionAssets, stages,
            bundle.BundleManifestPath, bundle.ReadinessManifestPath, bundle.PayloadPath,
            watch.Elapsed.TotalSeconds)
        {
            MaterialCount = materialAssets.Count,
            StaticMeshCount = staticMeshAssets.Count,
            ParticleEffectCount = particleAssets.Count,
            InterfaceMovieCount = movieAssets.Count,
            MaterialsAppliedBeforeMeshes = true,
            VariableSizeMeshCount = variableSizeMeshCount,
            PreviewOnly = previewOnly,
            ExactObjectExternalValidationPassed = exactObjectExternalValidationPassed,
            VariableSizeDeploymentGatePassed = variableSizeDeploymentGatePassed,
            VariableSizeDeploymentGateManifestPath = variableSizeDeploymentGateManifestPath,
        };
        var manifestPath = Path.Combine(root, ManifestFileName);
        await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        var revision = new GuidedTexturePackageRevision(
            Path.GetFileName(root), manifest.CreatedAt, sourceHash, textureAssets,
            currentPackagePath, finalTextureManifest ?? string.Empty,
            bundle.BundleManifestPath, bundle.ReadinessManifestPath, bundle.PayloadPath,
            bundle.Bundle.PayloadBytes, bundle.Bundle.PayloadSha256, watch.Elapsed.TotalSeconds)
        {
            MeshAssets = meshAssets,
            StaticMeshAssets = staticMeshAssets,
            MaterialAssets = materialAssets,
            ParticleAssets = particleAssets,
            InterfaceMovieAssets = movieAssets,
            CompositionManifestPath = manifestPath,
            PreviewOnly = previewOnly,
            ContainsVariableSizeMeshReplacements = variableSizeMeshCount > 0,
            VariableSizeDeploymentApproved = variableSizeDeploymentGatePassed,
            VariableSizeDeploymentGateManifestPath = variableSizeDeploymentGateManifestPath,
        };
        return new UnifiedPackageCompositionResult(
            revision, root, manifestPath, manifest, watch.Elapsed);
    }

    private static async Task<SkeletalMeshOracleEvidence?> LoadTrustedSourceOracleAsync(
        SkeletalMeshEditableExportManifest source,
        string expectedExportSha256,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(source.ReconnaissanceJsonPath);
        if (!File.Exists(path)) return null;
        var report = await ReadJsonAsync<SkeletalMeshReconnaissanceReport>(
            path, cancellationToken).ConfigureAwait(false);
        if (report.SchemaVersion < MinimumHashBoundSourceOracleSchemaVersion ||
            report.SchemaVersion > SkeletalMeshReconnaissanceService.CurrentSchemaVersion ||
            source.ReconnaissanceSchemaVersion != report.SchemaVersion ||
            report.Readiness != SkeletalMeshReadiness.NoOpReady ||
            report.NativeExportIndex != source.NativeExportIndex ||
            !report.ObjectName.Equals(source.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            !report.PackageAlias.Equals(source.PackageAlias, StringComparison.OrdinalIgnoreCase) ||
            !report.ExportPayloadSha256.Equals(expectedExportSha256, StringComparison.OrdinalIgnoreCase) ||
            !source.SourceExportSha256.Equals(expectedExportSha256, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"The retained UModel oracle for {source.ObjectName} is not bound to the byte-identical source export.");
        return report.Oracle;
    }

    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private async Task<string> HashLogicalExportAsync(
        PackageSummary summary,
        AssetCatalogEntry asset,
        CancellationToken cancellationToken) =>
        Convert.ToHexString(SHA256.HashData(await ReadLogicalExportAsync(summary, asset, cancellationToken).ConfigureAwait(false)));

    private async Task<byte[]> ReadLogicalExportAsync(
        PackageSummary summary,
        AssetCatalogEntry asset,
        CancellationToken cancellationToken)
    {
        if (asset.SerialSize > int.MaxValue)
            throw new NotSupportedException($"{asset.ObjectName}'s export is too large to read safely.");
        using var logical = new LogicalPackageStream(summary, _protectionProvider);
        logical.Position = asset.SerialOffset;
        var buffer = new byte[checked((int)asset.SerialSize)];
        await logical.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private static async Task<T> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("A required composition manifest was not found.", fullPath);
        await using var stream = File.OpenRead(fullPath);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                   .ConfigureAwait(false) ??
               throw new InvalidDataException($"{Path.GetFileName(fullPath)} was empty.");
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false));
    }

    private static async Task SaveJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
