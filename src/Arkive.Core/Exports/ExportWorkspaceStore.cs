using System.Text.Json;
using Arkive.Core.Assets;
using Arkive.Core.Baselines;
using Arkive.Core.External;
using Arkive.Core.Inspection;
using Arkive.Core.Mods;
using Arkive.Core.Textures;

namespace Arkive.Core.Exports;

public sealed class ExportWorkspaceStore
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "arkive-export.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public ExportWorkspaceStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public string CreateSessionDirectory(string packageAlias, string objectName)
    {
        var packageDirectory = Path.Combine(RootDirectory, SafePathSegment(packageAlias));
        var sessionName =
            $"{SafePathSegment(objectName)}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var sessionDirectory = Path.Combine(packageDirectory, sessionName);
        Directory.CreateDirectory(sessionDirectory);
        return sessionDirectory;
    }

    public async Task<ExportWorkspaceEntry> CommitAsync(
        UmodelExportResult result,
        string packageAlias,
        IReadOnlyList<string> inGameNames,
        string backendPath,
        CancellationToken cancellationToken = default,
        AssetBaselineEntry? assetBaseline = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageAlias);
        ArgumentNullException.ThrowIfNull(inGameNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(backendPath);

        var outputDirectory = EnsureWithinWorkspace(result.OutputDirectory);
        var source = new FileInfo(result.PackagePath);
        var outputFiles = result.OutputFiles
            .Where(File.Exists)
            .Select(path => new FileInfo(path))
            .Select(file => new ExportWorkspaceFile(
                Path.GetRelativePath(outputDirectory, file.FullName),
                file.Length))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var manifest = new ExportWorkspaceManifest(
            CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            source.FullName,
            source.Name,
            packageAlias,
            source.Exists ? source.Length : 0,
            source.Exists ? source.LastWriteTimeUtc.Ticks : 0,
            result.Asset.ExportIndex,
            result.Asset.Kind,
            result.Asset.ClassName,
            result.Asset.ObjectName,
            inGameNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            outputFiles,
            outputFiles.Sum(file => file.Length),
            result.UsedPackageWideFallback,
            result.Duration.TotalSeconds,
            Path.GetFullPath(backendPath),
            assetBaseline?.ManifestPath,
            assetBaseline?.Baseline.PackageSha256,
            assetBaseline?.Baseline.LogicalPayloadSha256);
        return await CommitManifestAsync(outputDirectory, manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExportWorkspaceEntry> CommitNativeTextureAsync(
        NativeTextureExportResult result,
        TextureReconnaissanceReport report,
        IReadOnlyList<string> inGameNames,
        CancellationToken cancellationToken = default,
        AssetBaselineEntry? assetBaseline = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(inGameNames);

        var outputDirectory = EnsureWithinWorkspace(result.OutputDirectory);
        var source = new FileInfo(report.PackagePath);
        var outputFiles = result.OutputFiles
            .Where(File.Exists)
            .Select(path => new FileInfo(path))
            .Select(file => new ExportWorkspaceFile(
                Path.GetRelativePath(outputDirectory, file.FullName),
                file.Length))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var manifest = new ExportWorkspaceManifest(
            CurrentSchemaVersion,
            result.Manifest.ExportId,
            result.Manifest.CreatedAt,
            source.FullName,
            source.Name,
            report.PackageAlias,
            source.Exists ? source.Length : 0,
            source.Exists ? source.LastWriteTimeUtc.Ticks : 0,
            report.ExportIndex,
            report.Kind,
            report.ClassName,
            report.ObjectName,
            inGameNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            outputFiles,
            outputFiles.Sum(file => file.Length),
            UsedPackageWideFallback: false,
            result.Duration.TotalSeconds,
            result.Manifest.Decoder,
            assetBaseline?.ManifestPath,
            assetBaseline?.Baseline.PackageSha256,
            assetBaseline?.Baseline.LogicalPayloadSha256,
            ExportMethod: $"Arkive native {result.Manifest.PixelFormat} DDS",
            NativeDetailsManifestPath: result.ManifestPath,
            ReconnaissanceJsonPath: result.Manifest.ReconnaissanceJsonPath,
            NativeExportIndex: report.NativeExportIndex);
        return await CommitManifestAsync(outputDirectory, manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExportWorkspaceEntry> CommitTextureImportCandidateAsync(
        TextureImportCandidateResult result,
        ExportWorkspaceEntry sourceExport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sourceExport);

        var outputDirectory = EnsureWithinWorkspace(result.OutputDirectory);
        var source = sourceExport.Manifest;
        var outputFiles = result.OutputFiles
            .Where(File.Exists)
            .Select(path => new FileInfo(path))
            .Select(file => new ExportWorkspaceFile(
                Path.GetRelativePath(outputDirectory, file.FullName),
                file.Length))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var manifest = new ExportWorkspaceManifest(
            CurrentSchemaVersion,
            result.Manifest.CandidateId,
            result.Manifest.CreatedAt,
            source.SourcePackagePath,
            source.PhysicalPackageName,
            source.PackageAlias,
            source.SourcePackageLength,
            source.SourcePackageLastWriteUtcTicks,
            source.ExportIndex,
            source.Kind,
            source.ClassName,
            source.ObjectName,
            source.InGameNames,
            outputFiles,
            outputFiles.Sum(file => file.Length),
            UsedPackageWideFallback: false,
            result.Duration.TotalSeconds,
            BackendPath: "Arkive managed DDS import-candidate builder",
            source.AssetBaselineManifestPath,
            result.Manifest.SourcePackageSha256,
            source.LogicalPayloadSha256,
            ExportMethod: $"Texture import candidate ({result.Manifest.AlphaPolicy})",
            NativeDetailsManifestPath: null,
            source.ReconnaissanceJsonPath,
            result.Manifest.NativeExportIndex,
            TextureImportCandidateManifestPath: result.ManifestPath,
            ParentExportId: source.ExportId);
        return await CommitManifestAsync(outputDirectory, manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExportWorkspaceEntry> CommitTextureLayoutConfirmationAsync(
        TextureLayoutConfirmationResult result,
        ExportWorkspaceEntry sourceExport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sourceExport);
        var outputDirectory = EnsureWithinWorkspace(result.OutputDirectory);
        var source = sourceExport.Manifest;
        var outputFiles = Inventory(result.OutputFiles, outputDirectory);
        var manifest = new ExportWorkspaceManifest(
            CurrentSchemaVersion,
            result.Manifest.ConfirmationId,
            result.Manifest.CreatedAt,
            source.SourcePackagePath,
            source.PhysicalPackageName,
            source.PackageAlias,
            source.SourcePackageLength,
            source.SourcePackageLastWriteUtcTicks,
            source.ExportIndex,
            source.Kind,
            source.ClassName,
            source.ObjectName,
            source.InGameNames,
            outputFiles,
            outputFiles.Sum(file => file.Length),
            UsedPackageWideFallback: false,
            result.Duration.TotalSeconds,
            BackendPath: "Arkive managed Texture2D layout confirmation",
            source.AssetBaselineManifestPath,
            result.Manifest.SourcePackageSha256,
            source.LogicalPayloadSha256,
            ExportMethod: "Texture mip layout confirmed",
            source.NativeDetailsManifestPath,
            source.ReconnaissanceJsonPath,
            result.Manifest.NativeExportIndex,
            TextureImportCandidateManifestPath: null,
            ParentExportId: source.ExportId,
            TextureLayoutConfirmationManifestPath: result.ManifestPath);
        return await CommitManifestAsync(outputDirectory, manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExportWorkspaceEntry> CommitTextureReplacementPayloadsAsync(
        TextureReplacementPayloadResult result,
        ExportWorkspaceEntry candidateExport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(candidateExport);
        var outputDirectory = EnsureWithinWorkspace(result.OutputDirectory);
        var source = candidateExport.Manifest;
        var outputFiles = Inventory(result.OutputFiles, outputDirectory);
        var manifest = new ExportWorkspaceManifest(
            CurrentSchemaVersion,
            result.Manifest.PayloadSetId,
            result.Manifest.CreatedAt,
            source.SourcePackagePath,
            source.PhysicalPackageName,
            source.PackageAlias,
            source.SourcePackageLength,
            source.SourcePackageLastWriteUtcTicks,
            source.ExportIndex,
            source.Kind,
            source.ClassName,
            source.ObjectName,
            source.InGameNames,
            outputFiles,
            outputFiles.Sum(file => file.Length),
            UsedPackageWideFallback: false,
            result.Duration.TotalSeconds,
            BackendPath: result.Manifest.CrunchEncoder,
            source.AssetBaselineManifestPath,
            result.Manifest.SourcePackageSha256,
            source.LogicalPayloadSha256,
            ExportMethod: result.Manifest.PayloadSetReadyForWriter
                ? "Verified native texture payloads"
                : "Native texture payloads (quality warning)",
            NativeDetailsManifestPath: null,
            source.ReconnaissanceJsonPath,
            result.Manifest.NativeExportIndex,
            source.TextureImportCandidateManifestPath,
            ParentExportId: source.ExportId,
            TextureLayoutConfirmationManifestPath: result.LayoutConfirmation.ManifestPath,
            TextureReplacementPayloadManifestPath: result.ManifestPath);
        return await CommitManifestAsync(outputDirectory, manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExportWorkspaceEntry> CommitTextureExportAssemblyAsync(
        TextureExportAssemblyResult result,
        ExportWorkspaceEntry payloadExport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(payloadExport);
        var outputDirectory = EnsureWithinWorkspace(result.OutputDirectory);
        var source = payloadExport.Manifest;
        var outputFiles = Inventory(result.OutputFiles, outputDirectory);
        var manifest = new ExportWorkspaceManifest(
            CurrentSchemaVersion,
            result.Manifest.AssemblyId,
            result.Manifest.CreatedAt,
            source.SourcePackagePath,
            source.PhysicalPackageName,
            source.PackageAlias,
            source.SourcePackageLength,
            source.SourcePackageLastWriteUtcTicks,
            source.ExportIndex,
            source.Kind,
            source.ClassName,
            source.ObjectName,
            source.InGameNames,
            outputFiles,
            outputFiles.Sum(file => file.Length),
            UsedPackageWideFallback: false,
            result.Duration.TotalSeconds,
            BackendPath: "Arkive managed Texture2D export assembler",
            source.AssetBaselineManifestPath,
            result.Manifest.SourcePackageSha256,
            source.LogicalPayloadSha256,
            ExportMethod: "Verified Texture2D export blob",
            NativeDetailsManifestPath: null,
            source.ReconnaissanceJsonPath,
            result.Manifest.NativeExportIndex,
            source.TextureImportCandidateManifestPath,
            ParentExportId: source.ExportId,
            result.Manifest.LayoutConfirmationManifestPath,
            result.Manifest.TextureReplacementPayloadManifestPath,
            TextureExportAssemblyManifestPath: result.ManifestPath);
        return await CommitManifestAsync(outputDirectory, manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExportWorkspaceEntry> CommitPackageRewritePlanAsync(
        PackageRewritePlanResult result,
        ExportWorkspaceEntry assemblyExport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(assemblyExport);
        var outputDirectory = EnsureWithinWorkspace(result.OutputDirectory);
        var source = assemblyExport.Manifest;
        var outputFiles = Inventory(result.OutputFiles, outputDirectory);
        var manifest = new ExportWorkspaceManifest(
            CurrentSchemaVersion,
            result.Manifest.PlanId,
            result.Manifest.CreatedAt,
            source.SourcePackagePath,
            source.PhysicalPackageName,
            source.PackageAlias,
            source.SourcePackageLength,
            source.SourcePackageLastWriteUtcTicks,
            source.ExportIndex,
            source.Kind,
            source.ClassName,
            source.ObjectName,
            source.InGameNames,
            outputFiles,
            outputFiles.Sum(file => file.Length),
            UsedPackageWideFallback: false,
            result.Duration.TotalSeconds,
            BackendPath: "Arkive read-only package rewrite planner",
            source.AssetBaselineManifestPath,
            result.Manifest.SourcePackageSha256,
            source.LogicalPayloadSha256,
            ExportMethod: "Package rewrite dry run",
            NativeDetailsManifestPath: null,
            source.ReconnaissanceJsonPath,
            result.Manifest.NativeExportIndex,
            source.TextureImportCandidateManifestPath,
            ParentExportId: source.ExportId,
            source.TextureLayoutConfirmationManifestPath,
            source.TextureReplacementPayloadManifestPath,
            source.TextureExportAssemblyManifestPath,
            PackageRewritePlanManifestPath: result.ManifestPath);
        return await CommitManifestAsync(outputDirectory, manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExportWorkspaceEntry> CommitLogicalPackageImageAsync(
        LogicalPackageImageResult result,
        ExportWorkspaceEntry rewritePlanExport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(rewritePlanExport);
        var outputDirectory = EnsureWithinWorkspace(result.OutputDirectory);
        var source = rewritePlanExport.Manifest;
        var outputFiles = Inventory(result.OutputFiles, outputDirectory);
        var manifest = new ExportWorkspaceManifest(
            CurrentSchemaVersion,
            result.Manifest.ImageId,
            result.Manifest.CreatedAt,
            source.SourcePackagePath,
            source.PhysicalPackageName,
            source.PackageAlias,
            source.SourcePackageLength,
            source.SourcePackageLastWriteUtcTicks,
            source.ExportIndex,
            source.Kind,
            source.ClassName,
            source.ObjectName,
            source.InGameNames,
            outputFiles,
            outputFiles.Sum(file => file.Length),
            UsedPackageWideFallback: false,
            result.Duration.TotalSeconds,
            BackendPath: "Arkive verified logical package-image builder",
            source.AssetBaselineManifestPath,
            result.Manifest.SourcePackageSha256,
            source.LogicalPayloadSha256,
            ExportMethod: "Verified logical package image",
            NativeDetailsManifestPath: null,
            source.ReconnaissanceJsonPath,
            result.Manifest.NativeExportIndex,
            source.TextureImportCandidateManifestPath,
            ParentExportId: source.ExportId,
            source.TextureLayoutConfirmationManifestPath,
            source.TextureReplacementPayloadManifestPath,
            source.TextureExportAssemblyManifestPath,
            source.PackageRewritePlanManifestPath,
            LogicalPackageImageManifestPath: result.ManifestPath);
        return await CommitManifestAsync(outputDirectory, manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExportWorkspaceEntry> CommitPhysicalPackagePlanAsync(
        PhysicalPackagePlanResult result,
        ExportWorkspaceEntry logicalImageExport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(logicalImageExport);
        var outputDirectory = EnsureWithinWorkspace(result.OutputDirectory);
        var source = logicalImageExport.Manifest;
        var outputFiles = Inventory(result.OutputFiles, outputDirectory);
        var manifest = new ExportWorkspaceManifest(
            CurrentSchemaVersion,
            result.Manifest.PhysicalPlanId,
            result.Manifest.CreatedAt,
            source.SourcePackagePath,
            source.PhysicalPackageName,
            source.PackageAlias,
            source.SourcePackageLength,
            source.SourcePackageLastWriteUtcTicks,
            source.ExportIndex,
            source.Kind,
            source.ClassName,
            source.ObjectName,
            source.InGameNames,
            outputFiles,
            outputFiles.Sum(file => file.Length),
            UsedPackageWideFallback: false,
            result.Duration.TotalSeconds,
            BackendPath: "Arkive physical chunk-layout planner",
            source.AssetBaselineManifestPath,
            result.Manifest.SourcePackageSha256,
            source.LogicalPayloadSha256,
            ExportMethod: "Physical chunk plan",
            NativeDetailsManifestPath: null,
            source.ReconnaissanceJsonPath,
            result.Manifest.NativeExportIndex,
            source.TextureImportCandidateManifestPath,
            ParentExportId: source.ExportId,
            source.TextureLayoutConfirmationManifestPath,
            source.TextureReplacementPayloadManifestPath,
            source.TextureExportAssemblyManifestPath,
            source.PackageRewritePlanManifestPath,
            source.LogicalPackageImageManifestPath,
            PhysicalPackagePlanManifestPath: result.ManifestPath);
        return await CommitManifestAsync(outputDirectory, manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExportWorkspaceEntry> CommitNoOpUpkReconstructionAsync(
        NoOpUpkReconstructionResult result,
        ExportWorkspaceEntry physicalPlanExport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(physicalPlanExport);
        var outputDirectory = EnsureWithinWorkspace(result.OutputDirectory);
        var source = physicalPlanExport.Manifest;
        var outputFiles = Inventory(result.OutputFiles, outputDirectory);
        var manifest = new ExportWorkspaceManifest(
            CurrentSchemaVersion,
            result.Manifest.ReconstructionId,
            result.Manifest.CreatedAt,
            source.SourcePackagePath,
            source.PhysicalPackageName,
            source.PackageAlias,
            source.SourcePackageLength,
            source.SourcePackageLastWriteUtcTicks,
            source.ExportIndex,
            source.Kind,
            source.ClassName,
            source.ObjectName,
            source.InGameNames,
            outputFiles,
            outputFiles.Sum(file => file.Length),
            UsedPackageWideFallback: false,
            result.Duration.TotalSeconds,
            BackendPath: "Arkive copy-preserving no-op UPK reconstructor",
            source.AssetBaselineManifestPath,
            result.Manifest.SourcePackageSha256,
            source.LogicalPayloadSha256,
            ExportMethod: "Verified no-op UPK reconstruction",
            NativeDetailsManifestPath: null,
            source.ReconnaissanceJsonPath,
            result.Manifest.NativeExportIndex,
            source.TextureImportCandidateManifestPath,
            ParentExportId: source.ExportId,
            source.TextureLayoutConfirmationManifestPath,
            source.TextureReplacementPayloadManifestPath,
            source.TextureExportAssemblyManifestPath,
            source.PackageRewritePlanManifestPath,
            source.LogicalPackageImageManifestPath,
            source.PhysicalPackagePlanManifestPath,
            NoOpUpkReconstructionManifestPath: result.ManifestPath);
        return await CommitManifestAsync(outputDirectory, manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExportWorkspaceEntry> CommitReplacementUpkBuildAsync(
        ReplacementUpkBuildResult result,
        ExportWorkspaceEntry noOpExport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(noOpExport);
        var outputDirectory = EnsureWithinWorkspace(result.OutputDirectory);
        var source = noOpExport.Manifest;
        var outputFiles = Inventory(result.OutputFiles, outputDirectory);
        var manifest = new ExportWorkspaceManifest(
            CurrentSchemaVersion,
            result.Manifest.BuildId,
            result.Manifest.CreatedAt,
            source.SourcePackagePath,
            source.PhysicalPackageName,
            source.PackageAlias,
            source.SourcePackageLength,
            source.SourcePackageLastWriteUtcTicks,
            source.ExportIndex,
            source.Kind,
            source.ClassName,
            source.ObjectName,
            source.InGameNames,
            outputFiles,
            outputFiles.Sum(file => file.Length),
            UsedPackageWideFallback: false,
            result.Duration.TotalSeconds,
            BackendPath: "Arkive verified replacement-bearing staging UPK builder",
            source.AssetBaselineManifestPath,
            result.Manifest.SourcePackageSha256,
            source.LogicalPayloadSha256,
            ExportMethod: "Verified replacement staging UPK",
            NativeDetailsManifestPath: null,
            source.ReconnaissanceJsonPath,
            result.Manifest.NativeExportIndex,
            source.TextureImportCandidateManifestPath,
            ParentExportId: source.ExportId,
            source.TextureLayoutConfirmationManifestPath,
            source.TextureReplacementPayloadManifestPath,
            source.TextureExportAssemblyManifestPath,
            source.PackageRewritePlanManifestPath,
            source.LogicalPackageImageManifestPath,
            source.PhysicalPackagePlanManifestPath,
            source.NoOpUpkReconstructionManifestPath,
            ReplacementUpkBuildManifestPath: result.ManifestPath);
        return await CommitManifestAsync(outputDirectory, manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExportWorkspaceEntry> CommitModBundleAsync(
        ArkiveModBundleResult result,
        ExportWorkspaceEntry replacementExport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(replacementExport);
        var outputDirectory = EnsureWithinWorkspace(result.OutputDirectory);
        var source = replacementExport.Manifest;
        var outputFiles = Inventory(result.OutputFiles, outputDirectory);
        var manifest = new ExportWorkspaceManifest(
            CurrentSchemaVersion,
            result.Bundle.ModId,
            result.Bundle.CreatedAt,
            source.SourcePackagePath,
            source.PhysicalPackageName,
            source.PackageAlias,
            source.SourcePackageLength,
            source.SourcePackageLastWriteUtcTicks,
            source.ExportIndex,
            source.Kind,
            source.ClassName,
            source.ObjectName,
            source.InGameNames,
            outputFiles,
            outputFiles.Sum(file => file.Length),
            UsedPackageWideFallback: false,
            result.Duration.TotalSeconds,
            BackendPath: result.Readiness.ExternalValidator,
            source.AssetBaselineManifestPath,
            result.Bundle.RequiredSourcePackageSha256,
            source.LogicalPayloadSha256,
            ExportMethod: "Arkive mod bundle and deployment rehearsal",
            NativeDetailsManifestPath: null,
            source.ReconnaissanceJsonPath,
            source.NativeExportIndex,
            source.TextureImportCandidateManifestPath,
            ParentExportId: source.ExportId,
            source.TextureLayoutConfirmationManifestPath,
            source.TextureReplacementPayloadManifestPath,
            source.TextureExportAssemblyManifestPath,
            source.PackageRewritePlanManifestPath,
            source.LogicalPackageImageManifestPath,
            source.PhysicalPackagePlanManifestPath,
            source.NoOpUpkReconstructionManifestPath,
            source.ReplacementUpkBuildManifestPath,
            ModBundleManifestPath: result.BundleManifestPath,
            DeploymentReadinessManifestPath: result.ReadinessManifestPath);
        return await CommitManifestAsync(outputDirectory, manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<ExportWorkspaceFile> Inventory(
        IEnumerable<string> paths,
        string outputDirectory) =>
        paths.Where(File.Exists)
            .Select(path => new FileInfo(path))
            .Select(file => new ExportWorkspaceFile(
                Path.GetRelativePath(outputDirectory, file.FullName),
                file.Length))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static async Task<ExportWorkspaceEntry> CommitManifestAsync(
        string outputDirectory,
        ExportWorkspaceManifest manifest,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        var temporaryPath = manifestPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    manifest,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new ExportWorkspaceEntry(manifestPath, outputDirectory, manifest);
    }

    public async Task<ExportWorkspaceSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory))
        {
            return new ExportWorkspaceSnapshot(RootDirectory, [], 0, 0);
        }

        var entries = new List<ExportWorkspaceEntry>();
        var unreadable = 0;
        foreach (var manifestPath in Directory.EnumerateFiles(
                     RootDirectory,
                     ManifestFileName,
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<ExportWorkspaceManifest>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                if (manifest is null || manifest.SchemaVersion != CurrentSchemaVersion)
                {
                    unreadable++;
                    continue;
                }

                entries.Add(new ExportWorkspaceEntry(
                    Path.GetFullPath(manifestPath),
                    Path.GetDirectoryName(Path.GetFullPath(manifestPath))!,
                    manifest));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                unreadable++;
            }
        }

        var ordered = entries
            .OrderByDescending(entry => entry.Manifest.CreatedAt)
            .ToArray();
        return new ExportWorkspaceSnapshot(
            RootDirectory,
            ordered,
            unreadable,
            ordered.Sum(entry => entry.Manifest.OutputBytes));
    }

    private string EnsureWithinWorkspace(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootPrefix = RootDirectory.TrimEnd(Path.DirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Export output must remain inside the Arkive workspace: {RootDirectory}");
        }

        return fullPath;
    }

    private static string SafePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "unnamed";
        }

        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }
}

public sealed record ExportWorkspaceFile(string RelativePath, long Length);

public sealed record ExportWorkspaceManifest(
    int SchemaVersion,
    string ExportId,
    DateTimeOffset CreatedAt,
    string SourcePackagePath,
    string PhysicalPackageName,
    string PackageAlias,
    long SourcePackageLength,
    long SourcePackageLastWriteUtcTicks,
    int ExportIndex,
    AssetKind Kind,
    string ClassName,
    string ObjectName,
    IReadOnlyList<string> InGameNames,
    IReadOnlyList<ExportWorkspaceFile> OutputFiles,
    long OutputBytes,
    bool UsedPackageWideFallback,
    double DurationSeconds,
    string BackendPath,
    string? AssetBaselineManifestPath = null,
    string? SourcePackageSha256 = null,
    string? LogicalPayloadSha256 = null,
    string ExportMethod = "UModel",
    string? NativeDetailsManifestPath = null,
    string? ReconnaissanceJsonPath = null,
    int? NativeExportIndex = null,
    string? TextureImportCandidateManifestPath = null,
    string? ParentExportId = null,
    string? TextureLayoutConfirmationManifestPath = null,
    string? TextureReplacementPayloadManifestPath = null,
    string? TextureExportAssemblyManifestPath = null,
    string? PackageRewritePlanManifestPath = null,
    string? LogicalPackageImageManifestPath = null,
    string? PhysicalPackagePlanManifestPath = null,
    string? NoOpUpkReconstructionManifestPath = null,
    string? ReplacementUpkBuildManifestPath = null,
    string? ModBundleManifestPath = null,
    string? DeploymentReadinessManifestPath = null);

public sealed record ExportWorkspaceEntry(
    string ManifestPath,
    string OutputDirectory,
    ExportWorkspaceManifest Manifest);

public sealed record ExportWorkspaceSnapshot(
    string RootDirectory,
    IReadOnlyList<ExportWorkspaceEntry> Entries,
    int UnreadableManifestCount,
    long OutputBytes);
