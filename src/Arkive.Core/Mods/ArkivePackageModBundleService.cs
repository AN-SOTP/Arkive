using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Packages;
using Arkive.Core.Validation;

namespace Arkive.Core.Mods;

public sealed record ArkivePackageModBundleRequest(
    string PayloadUpkPath,
    string SourcePackagePath,
    string RequiredSourcePackageSha256,
    string OutputDirectory,
    string BackupRootDirectory,
    string DisplayName,
    string CompositionId,
    IReadOnlyList<ArkiveModAsset> Assets)
{
    public bool PreviewOnly { get; init; }

    public int VariableSizeMeshCount { get; init; }

    public bool VariableSizeDeploymentGatePassed { get; init; }
}

public sealed class ArkivePackageModBundleRefusalException : NotSupportedException
{
    public ArkivePackageModBundleRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Creates one deployable bundle from a fully composed package. Unlike the
/// legacy texture and single-mesh wrappers, this service is intentionally
/// asset-type agnostic: a bundle may contain Texture2D, material, SkeletalMesh,
/// and StaticMesh replacements together, but still targets exactly one physical
/// UPK and one verified clean source identity.
/// </summary>
public sealed class ArkivePackageModBundleService
{
    private const int BufferBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly UpkPackageReader _reader;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly IModPackageExternalValidator _externalValidator;

    public ArkivePackageModBundleService(
        IModPackageExternalValidator externalValidator,
        UpkPackageReader? reader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _externalValidator = externalValidator ??
                             throw new ArgumentNullException(nameof(externalValidator));
        _reader = reader ?? new UpkPackageReader();
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
    }

    public async Task<ArkiveModBundleResult> BuildAsync(
        ArkivePackageModBundleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            Refuse("CPB001", "The combined package-bundle directory must be empty.");
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            Refuse("CPB002", "The combined mod display name is empty.");
        if (string.IsNullOrWhiteSpace(request.CompositionId))
            Refuse("CPB003", "The package composition ID is empty.");
        if (request.Assets.Count == 0)
            Refuse("CPB004", "A combined bundle must contain at least one selected asset.");
        if (request.VariableSizeMeshCount < 0)
            Refuse("CPB018", "The variable-size mesh count cannot be negative.");
        if (request.PreviewOnly && request.VariableSizeDeploymentGatePassed)
            Refuse("CPB019", "A preview-only bundle cannot also claim deployment approval.");
        if (request.VariableSizeMeshCount > 0 &&
            !request.PreviewOnly && !request.VariableSizeDeploymentGatePassed)
            Refuse("CPB020", "Variable-size meshes require the controlled deployment gate or preview-only mode.");

        var duplicate = request.Assets.GroupBy(asset => asset.NativeExportIndex)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            Refuse("CPB005", $"More than one replacement targets native export {duplicate.Key}.");
        var assets = request.Assets.OrderBy(asset => asset.NativeExportIndex).ToArray();
        if (assets.Any(asset => string.IsNullOrWhiteSpace(asset.ClassName) ||
                                string.IsNullOrWhiteSpace(asset.ObjectName)))
            Refuse("CPB006", "Every combined asset must have a class and object name.");

        var sourcePath = Path.GetFullPath(request.SourcePackagePath);
        var payloadUpkPath = Path.GetFullPath(request.PayloadUpkPath);
        if (!File.Exists(sourcePath))
            Refuse("CPB007", $"The clean source package was not found: {sourcePath}");
        if (!File.Exists(payloadUpkPath))
            Refuse("CPB008", $"The composed package was not found: {payloadUpkPath}");
        var sourceInfo = new FileInfo(sourcePath);
        var sourceHashBefore = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!sourceHashBefore.Equals(
                request.RequiredSourcePackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("CPB009", "The installed package is not the clean version required by this composition.");

        var sourceName = Path.GetFileName(sourcePath);
        var installRelativePath = Path.Combine("EFGame", "ReleasePC", "Packages", sourceName);
        var payloadRelativePath = Path.Combine("payload", installRelativePath);
        var payloadPath = ResolveWithin(outputDirectory, payloadRelativePath, "CPB010");
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        await CopyFileAsync(payloadUpkPath, payloadPath, cancellationToken).ConfigureAwait(false);
        var payloadInfo = new FileInfo(payloadPath);
        var payloadHash = await HashFileAsync(payloadPath, cancellationToken).ConfigureAwait(false);

        var sourceSummary = _reader.Read(sourcePath);
        var payloadSummary = _reader.Read(payloadPath);
        var summaryIdentity = sourceSummary.PackageGuid == payloadSummary.PackageGuid &&
                              sourceSummary.FileVersion == payloadSummary.FileVersion &&
                              sourceSummary.LicenseeVersion == payloadSummary.LicenseeVersion &&
                              sourceSummary.EngineVersion == payloadSummary.EngineVersion &&
                              sourceSummary.CookerVersion == payloadSummary.CookerVersion &&
                              sourceSummary.NameCount == payloadSummary.NameCount &&
                              sourceSummary.ImportCount == payloadSummary.ImportCount &&
                              sourceSummary.ExportCount == payloadSummary.ExportCount &&
                              sourceSummary.CompressedChunks.Count == payloadSummary.CompressedChunks.Count;
        if (!summaryIdentity)
            Refuse("CPB011", "The combined payload did not retain package identity and catalogue shape.");
        var structural = PackageValidator.Validate(payloadSummary)
            .All(issue => issue.Severity != ValidationSeverity.Error);
        if (!structural)
            Refuse("CPB012", "The combined payload failed structural package validation.");

        var catalogue = new AssetCatalogReader(_reader, _protectionProvider).Read(payloadPath);
        foreach (var asset in assets)
        {
            var match = catalogue.Assets.SingleOrDefault(candidate =>
                candidate.ExportIndex == asset.NativeExportIndex);
            if (match is null ||
                !match.ObjectName.Equals(asset.ObjectName, StringComparison.OrdinalIgnoreCase))
                Refuse("CPB013",
                    $"Native export {asset.NativeExportIndex} is not the expected {asset.ObjectName} in the combined payload.");
        }

        var externalResults = new List<ModExternalValidationResult>(assets.Length);
        var packageRoot = Path.GetDirectoryName(sourcePath)!;
        foreach (var asset in assets)
        {
            externalResults.Add(await _externalValidator.ValidateInPackageRootAsync(
                    payloadPath, packageRoot, asset.ClassName, asset.ObjectName, cancellationToken)
                .ConfigureAwait(false));
        }
        if (externalResults.Any(result => !result.CataloguePassed || !result.NativeLoadPassed ||
                                          !result.ExpectedObjectFound ||
                                          result.ValidationFileCount <= 0))
            Refuse("CPB014", "One or more combined assets failed exact-object external validation.");
        var external = new ModExternalValidationResult(
            string.Join("; ", externalResults.Select(result => result.Validator).Distinct()),
            externalResults.Max(result => result.AssetCount),
            externalResults.Sum(result => result.ValidationFileCount),
            CataloguePassed: true, NativeLoadPassed: true, ExpectedObjectFound: true,
            externalResults.SelectMany(result => result.Diagnostics)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).Take(200).ToArray(),
            TimeSpan.FromSeconds(externalResults.Sum(result => result.Duration.TotalSeconds)));

        var backupRoot = Path.GetFullPath(request.BackupRootDirectory);
        var proposedBackupPath = Path.Combine(backupRoot, sourceHashBefore, sourceName);
        var driveRoot = Path.GetPathRoot(backupRoot);
        if (string.IsNullOrWhiteSpace(driveRoot))
            Refuse("CPB015", "The backup root has no resolvable drive.");
        var availableBackupBytes = new DriveInfo(driveRoot).AvailableFreeSpace;
        if (availableBackupBytes < sourceInfo.Length)
            Refuse("CPB016", "The backup drive does not have enough free space.");
        var sourceHashAfter = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
            Refuse("CPB017", "The clean source package changed during bundle rehearsal.");

        var textureCount = assets.Count(asset =>
            asset.ClassName.Contains("Texture", StringComparison.OrdinalIgnoreCase));
        var skeletalMeshCount = assets.Count(asset =>
            asset.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
        var staticMeshCount = assets.Count(asset =>
            asset.ClassName.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase) &&
            !asset.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
        var meshCount = skeletalMeshCount + staticMeshCount;
        if (request.VariableSizeMeshCount > meshCount)
            Refuse("CPB021", "The variable-size mesh count exceeds the combined skeletal/static mesh asset count.");
        var variableSizeDeploymentApproved = request.VariableSizeMeshCount > 0 &&
                                             request.VariableSizeDeploymentGatePassed &&
                                             !request.PreviewOnly;
        var controlledInstallReady = !request.PreviewOnly &&
                                     (request.VariableSizeMeshCount == 0 || variableSizeDeploymentApproved);
        var modId = Guid.NewGuid().ToString("N");
        var auditId = Guid.NewGuid().ToString("N");
        var checks = new List<ModReadinessCheck>
        {
            new("COMBINED_SOURCE_IDENTITY", ModReadinessCheckStatus.Passed,
                $"Clean source matches {sourceInfo.Length:N0} bytes and SHA-256 {sourceHashAfter}."),
            new("COMBINED_ASSET_SET", ModReadinessCheckStatus.Passed,
                $"Composition {request.CompositionId} contains {textureCount:N0} texture(s), " +
                $"{skeletalMeshCount:N0} skeletal mesh(es), and {staticMeshCount:N0} StaticMesh(es)."),
            new("COMBINED_PAYLOAD_IDENTITY", ModReadinessCheckStatus.Passed,
                $"One payload retains physical name {sourceName} and SHA-256 {payloadHash}."),
            new("COMBINED_PACKAGE_IDENTITY", ModReadinessCheckStatus.Passed,
                $"Package GUID {payloadSummary.PackageGuid}, versions, tables, and {payloadSummary.CompressedChunks.Count:N0} chunks remain compatible."),
            new("COMBINED_EXTERNAL_LOAD", ModReadinessCheckStatus.Passed,
                $"{external.Validator} loaded all {assets.Length:N0} selected object(s) from the final package."),
            new("COMBINED_BACKUP_CAPACITY", ModReadinessCheckStatus.Passed,
                $"Backup needs {sourceInfo.Length:N0} bytes; {availableBackupBytes:N0} are available."),
            new("COMBINED_REHEARSAL_ONLY", ModReadinessCheckStatus.Information,
                "Composition and bundle preparation did not change any Lost Ark file."),
        };
        if (request.PreviewOnly)
            checks.Add(new ModReadinessCheck(
                "COMBINED_PREVIEW_ONLY", ModReadinessCheckStatus.Information,
                "This composition contains a variable-size mesh replacement. Installation is disabled until the combined-package acceptance gate is approved."));
        if (variableSizeDeploymentApproved)
            checks.Add(new ModReadinessCheck(
                "COMBINED_VARIABLE_SIZE_DEPLOYMENT_GATE", ModReadinessCheckStatus.Passed,
                $"Approved {request.VariableSizeMeshCount:N0} variable-size mesh replacement(s) for controlled install after native compilation/decode-back, append-and-retarget reconstruction, exact-object external export, source identity, backup-capacity, and original-physical-name checks passed."));
        var plannedSteps = request.PreviewOnly
            ? new[]
            {
                "Retain the final composed payload outside the Lost Ark directory.",
                "Recheck the exact clean source package and final payload hashes.",
                "Require every selected object to load and export from the exact physical package name in UModel.",
                "Inspect the variable-size mesh and accompanying textures/materials in UModel.",
                "Keep installation disabled until the variable-size deployment acceptance gate is approved.",
            }
            : new[]
            {
                "Require Lost Ark, its launcher, and Easy Anti-Cheat to be closed.",
                "Recheck the exact clean source package size and SHA-256.",
                "Create and hash-verify one content-addressed backup of the original package.",
                "Copy and hash-verify the combined payload to a temporary sibling of the target.",
                "Atomically replace the one install target with the combined package.",
                "Retain one durable receipt binding every asset, the payload, target, and backup.",
                "Restore only while the target still matches this payload, then verify the original hash.",
                "Refuse after a client update or any unrecognized package mutation.",
            };
        watch.Stop();
        var readiness = new ModDeploymentReadinessManifest(
            ArkiveModBundleService.CurrentManifestSchemaVersion, auditId, DateTimeOffset.UtcNow,
            modId, ArkiveModBundleService.BundleManifestFileName, sourcePath, sourcePath,
            proposedBackupPath, payloadPath, sourceHashAfter, payloadHash,
            sourceInfo.Length, payloadInfo.Length, sourceInfo.Length, availableBackupBytes,
            sourceSummary.PackageGuid, payloadSummary.PackageGuid, payloadSummary.FileVersion,
            payloadSummary.LicenseeVersion, payloadSummary.EngineVersion, payloadSummary.CookerVersion,
            payloadSummary.NameCount, payloadSummary.ImportCount, payloadSummary.ExportCount,
            payloadSummary.CompressedChunks.Count, external.Validator, external.AssetCount,
            external.ValidationFileCount, SourceIdentityMatches: true,
            PayloadIdentityMatchesManifest: true, PayloadUsesOriginalPhysicalName: true,
            PackageSummaryIdentityRetained: true, StructuralValidationPassed: true,
            ExternalCataloguePassed: true, ExternalNativeLoadPassed: true,
            ExpectedObjectFound: true, BackupSpaceAvailable: true,
            ReadyForControlledInstall: controlledInstallReady, InstallationPerformed: false,
            GameFilesModified: false, plannedSteps, checks, external.Diagnostics,
            watch.Elapsed.TotalSeconds)
        {
            VariableSizeMeshCount = request.VariableSizeMeshCount,
            VariableSizeDeploymentApproved = variableSizeDeploymentApproved,
        };
        var bundle = new ArkiveModBundleManifest(
            ArkiveModBundleService.CurrentManifestSchemaVersion, modId, readiness.CreatedAt,
            request.DisplayName.Trim(), ArkiveModBundleService.WesternGlobalProfile,
            request.CompositionId, installRelativePath, sourceName, sourceInfo.Length,
            sourceHashAfter, payloadRelativePath, payloadInfo.Length, payloadHash,
            ArkiveModBundleService.ReadinessManifestFileName, assets,
            ExactClientMatchRequired: true, BackupRequired: true,
            ReversibleInstallPlanned: controlledInstallReady,
            ReadyForControlledInstall: controlledInstallReady,
            InstallationEnabled: controlledInstallReady, GameFilesModified: false, checks)
        {
            VariableSizeMeshCount = request.VariableSizeMeshCount,
            VariableSizeDeploymentApproved = variableSizeDeploymentApproved,
        };

        var readinessPath = Path.Combine(
            outputDirectory, ArkiveModBundleService.ReadinessManifestFileName);
        var bundlePath = Path.Combine(outputDirectory, ArkiveModBundleService.BundleManifestFileName);
        await SaveJsonAtomicAsync(readinessPath, readiness, cancellationToken).ConfigureAwait(false);
        await SaveJsonAtomicAsync(bundlePath, bundle, cancellationToken).ConfigureAwait(false);
        return new ArkiveModBundleResult(
            outputDirectory, bundlePath, readinessPath, payloadPath, bundle, readiness,
            [payloadPath, readinessPath, bundlePath], watch.Elapsed);
    }

    private static string ResolveWithin(string directory, string relativePath, string code)
    {
        var root = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            Refuse(code, $"Bundle path escapes its output directory: {relativePath}");
        return path;
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var temporary = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var source = new FileStream(
                             sourcePath, FileMode.Open, FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete, BufferBytes,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, BufferBytes, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(destination, BufferBytes, cancellationToken)
                    .ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, destinationPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
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

    [DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new ArkivePackageModBundleRefusalException(code, message);
}
