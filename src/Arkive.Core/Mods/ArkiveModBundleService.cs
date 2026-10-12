using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.External;
using Arkive.Core.Packages;
using Arkive.Core.Textures;
using Arkive.Core.Validation;

namespace Arkive.Core.Mods;

public enum ModReadinessCheckStatus { Passed, Information }

public sealed record ModReadinessCheck(
    string Code,
    ModReadinessCheckStatus Status,
    string Message);

public sealed record ArkiveModAsset(
    string PackageAlias,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    string PixelFormat,
    long ReplacementSerialOffset,
    long ReplacementSerialSize);

public sealed record ArkiveModBundleManifest(
    int SchemaVersion,
    string ModId,
    DateTimeOffset CreatedAt,
    string DisplayName,
    string GameProfile,
    string ReplacementBuildId,
    string SourcePackageRelativePath,
    string SourcePhysicalPackageName,
    long RequiredSourcePackageBytes,
    string RequiredSourcePackageSha256,
    string PayloadRelativePath,
    long PayloadBytes,
    string PayloadSha256,
    string DeploymentReadinessRelativePath,
    IReadOnlyList<ArkiveModAsset> Assets,
    bool ExactClientMatchRequired,
    bool BackupRequired,
    bool ReversibleInstallPlanned,
    bool ReadyForControlledInstall,
    bool InstallationEnabled,
    bool GameFilesModified,
    IReadOnlyList<ModReadinessCheck> Checks)
{
    public int VariableSizeMeshCount { get; init; }

    public bool VariableSizeDeploymentApproved { get; init; }

    public string? VariableSizeDeploymentGateRelativePath { get; init; }

    public string? VariableSizeDeploymentGateSha256 { get; init; }
}

public sealed record ModDeploymentReadinessManifest(
    int SchemaVersion,
    string AuditId,
    DateTimeOffset CreatedAt,
    string ModId,
    string BundleManifestRelativePath,
    string SourcePackagePath,
    string InstallTargetPath,
    string ProposedBackupPath,
    string PayloadPath,
    string SourcePackageSha256,
    string PayloadSha256,
    long SourcePackageBytes,
    long PayloadBytes,
    long RequiredBackupBytes,
    long AvailableBackupDriveBytes,
    Guid SourcePackageGuid,
    Guid PayloadPackageGuid,
    ushort FileVersion,
    ushort LicenseeVersion,
    int EngineVersion,
    int CookerVersion,
    int NameCount,
    int ImportCount,
    int ExportCount,
    int CompressedChunkCount,
    string ExternalValidator,
    int ExternalAssetCount,
    int ExternalValidationFileCount,
    bool SourceIdentityMatches,
    bool PayloadIdentityMatchesManifest,
    bool PayloadUsesOriginalPhysicalName,
    bool PackageSummaryIdentityRetained,
    bool StructuralValidationPassed,
    bool ExternalCataloguePassed,
    bool ExternalNativeLoadPassed,
    bool ExpectedObjectFound,
    bool BackupSpaceAvailable,
    bool ReadyForControlledInstall,
    bool InstallationPerformed,
    bool GameFilesModified,
    IReadOnlyList<string> PlannedInstallSteps,
    IReadOnlyList<ModReadinessCheck> Checks,
    IReadOnlyList<string> ExternalDiagnostics,
    double DurationSeconds)
{
    public int VariableSizeMeshCount { get; init; }

    public bool VariableSizeDeploymentApproved { get; init; }

    public string? VariableSizeDeploymentGateRelativePath { get; init; }

    public string? VariableSizeDeploymentGateSha256 { get; init; }
}

public sealed record ModExternalValidationResult(
    string Validator,
    int AssetCount,
    int ValidationFileCount,
    bool CataloguePassed,
    bool NativeLoadPassed,
    bool ExpectedObjectFound,
    IReadOnlyList<string> Diagnostics,
    TimeSpan Duration);

public interface IModPackageExternalValidator
{
    Task<ModExternalValidationResult> ValidateAsync(
        string packagePath,
        string className,
        string objectName,
        CancellationToken cancellationToken = default);

    Task<ModExternalValidationResult> ValidateInPackageRootAsync(
        string packagePath,
        string packageSearchRoot,
        string className,
        string objectName,
        CancellationToken cancellationToken = default) =>
        ValidateAsync(packagePath, className, objectName, cancellationToken);
}

public sealed class UmodelModPackageExternalValidator : IModPackageExternalValidator
{
    private readonly UmodelBridge _bridge;

    public UmodelModPackageExternalValidator(UmodelBridge bridge) =>
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

    public async Task<ModExternalValidationResult> ValidateAsync(
        string packagePath,
        string className,
        string objectName,
        CancellationToken cancellationToken = default) =>
        await ValidateCoreAsync(
            packagePath, packageSearchRoot: null, className, objectName, cancellationToken)
            .ConfigureAwait(false);

    public async Task<ModExternalValidationResult> ValidateInPackageRootAsync(
        string packagePath,
        string packageSearchRoot,
        string className,
        string objectName,
        CancellationToken cancellationToken = default) =>
        await ValidateCoreAsync(
            packagePath, packageSearchRoot, className, objectName, cancellationToken)
            .ConfigureAwait(false);

    private async Task<ModExternalValidationResult> ValidateCoreAsync(
        string packagePath,
        string? packageSearchRoot,
        string className,
        string objectName,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var diagnostics = new List<string>();
        var list = await _bridge.ListAsync(packagePath, cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(list.Diagnostics);
        var target = list.Assets.FirstOrDefault(asset =>
            asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase) &&
            asset.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase));
        target ??= list.Assets.FirstOrDefault(asset =>
            asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            throw new UmodelBridgeException(
                $"UModel listed the mod package but did not find {className} {objectName}.");
        }

        // UModel is not long-path aware when it appends package/class/object
        // folders beneath -out. Bundle sessions are already deeply nested, so a
        // sibling validation directory can silently produce zero files despite a
        // successful exit code. Keep disposable exports under a short temp root.
        var validationDirectory = Path.Combine(
            Path.GetTempPath(),
            "Arkive",
            "mod-validation",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            var native = string.IsNullOrWhiteSpace(packageSearchRoot)
                ? await _bridge.ExportAsync(
                        packagePath, target, validationDirectory,
                        allowPackageWideFallback: false, cancellationToken)
                    .ConfigureAwait(false)
                : await _bridge.ExportInPackageRootAsync(
                        packagePath, target, validationDirectory, packageSearchRoot,
                        allowPackageWideFallback: false, cancellationToken)
                    .ConfigureAwait(false);
            diagnostics.AddRange(native.Diagnostics);
            if (native.OutputFiles.Count == 0)
            {
                var diagnostic = native.Diagnostics.LastOrDefault() ??
                                 "UModel returned no diagnostic.";
                throw new UmodelBridgeException(
                    $"UModel loaded {objectName} but produced no validation export. " +
                    $"Output path length={validationDirectory.Length}; {diagnostic}");
            }
            if ((className.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase) ||
                 className.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase)) &&
                !native.OutputFiles.Any(path =>
                    Path.GetFileNameWithoutExtension(path).Equals(
                        objectName, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase)))
            {
                throw new UmodelBridgeException(
                    $"UModel loaded the staged package but did not export mesh " +
                    $"{objectName} as glTF.");
            }

            watch.Stop();
            return new ModExternalValidationResult(
                $"UModel {Path.GetFileName(_bridge.ExecutablePath)}",
                list.Assets.Count,
                native.OutputFiles.Count,
                CataloguePassed: true,
                NativeLoadPassed: true,
                ExpectedObjectFound: true,
                diagnostics
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .Take(100)
                    .ToArray(),
                watch.Elapsed);
        }
        finally
        {
            if (Directory.Exists(validationDirectory))
            {
                Directory.Delete(validationDirectory, recursive: true);
            }
        }
    }
}

public sealed record ArkiveModBundleRequest(
    string ReplacementUpkBuildManifestPath,
    string OutputDirectory,
    string BackupRootDirectory,
    string DisplayName,
    string? DeploymentSourcePackagePath = null,
    string? RequiredSourcePackageSha256 = null,
    IReadOnlyList<ArkiveModAsset>? Assets = null);

public sealed record ArkiveModBundleResult(
    string OutputDirectory,
    string BundleManifestPath,
    string ReadinessManifestPath,
    string PayloadPath,
    ArkiveModBundleManifest Bundle,
    ModDeploymentReadinessManifest Readiness,
    IReadOnlyList<string> OutputFiles,
    TimeSpan Duration);

public sealed class ArkiveModBundleRefusalException : NotSupportedException
{
    public ArkiveModBundleRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Packages one validated replacement UPK into a portable Arkive mod bundle and
/// rehearses its future on-disk installation. The live game directory is read-only.
/// </summary>
public sealed class ArkiveModBundleService
{
    public const int CurrentManifestSchemaVersion = 1;
    public const string BundleManifestFileName = "arkive-mod.json";
    public const string ReadinessManifestFileName = "arkive-deployment-readiness.json";
    public const string WesternGlobalProfile = "lost-ark-western-global-steam";

    private const int BufferBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly UpkPackageReader _reader;
    private readonly IModPackageExternalValidator _externalValidator;

    public ArkiveModBundleService(
        IModPackageExternalValidator externalValidator,
        UpkPackageReader? reader = null)
    {
        _externalValidator = externalValidator ??
                             throw new ArgumentNullException(nameof(externalValidator));
        _reader = reader ?? new UpkPackageReader();
    }

    public static async Task<ArkiveModBundleManifest> LoadBundleAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var manifest = await LoadAsync<ArkiveModBundleManifest>(
            path, BundleManifestFileName, cancellationToken).ConfigureAwait(false);
        if (manifest.SchemaVersion != CurrentManifestSchemaVersion)
        {
            Refuse(
                "MOD013",
                $"Mod-bundle schema {manifest.SchemaVersion} is unsupported; " +
                $"Arkive requires {CurrentManifestSchemaVersion}.");
        }
        return manifest;
    }

    public static async Task<ModDeploymentReadinessManifest> LoadReadinessAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var manifest = await LoadAsync<ModDeploymentReadinessManifest>(
            path, ReadinessManifestFileName, cancellationToken).ConfigureAwait(false);
        if (manifest.SchemaVersion != CurrentManifestSchemaVersion)
        {
            Refuse(
                "MOD014",
                $"Deployment-readiness schema {manifest.SchemaVersion} is unsupported; " +
                $"Arkive requires {CurrentManifestSchemaVersion}.");
        }
        return manifest;
    }

    public async Task<ArkiveModBundleResult> BuildAsync(
        ArkiveModBundleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            Refuse("MOD001", "The mod-bundle output directory must be empty.");
        }
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            Refuse("MOD002", "The mod display name is empty.");
        }

        var verification = await ReplacementUpkBuildService.VerifyOutputAsync(
            request.ReplacementUpkBuildManifestPath, cancellationToken).ConfigureAwait(false);
        var replacement = verification.Manifest;
        var sourcePath = Path.GetFullPath(
            request.DeploymentSourcePackagePath ?? replacement.SourcePackagePath);
        if (!File.Exists(sourcePath))
        {
            Refuse("MOD003", $"The installed source package was not found: {sourcePath}");
        }
        var sourceInfo = new FileInfo(sourcePath);
        var sourceHashBefore = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var requiredSourceHash = request.RequiredSourcePackageSha256 ?? replacement.SourcePackageSha256;
        var sourceMatches = sourceHashBefore.Equals(requiredSourceHash, StringComparison.OrdinalIgnoreCase);
        if (!sourceMatches)
        {
            Refuse("MOD004", "The source package changed since this replacement was built.");
        }

        var sourceName = Path.GetFileName(sourcePath);
        var installRelativePath = Path.Combine("EFGame", "ReleasePC", "Packages", sourceName);
        var payloadRelativePath = Path.Combine("payload", installRelativePath);
        var payloadPath = ResolveWithin(outputDirectory, payloadRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        await CopyFileAsync(verification.OutputUpkPath, payloadPath, cancellationToken)
            .ConfigureAwait(false);
        var payloadInfo = new FileInfo(payloadPath);
        var payloadHash = await HashFileAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        var payloadIdentity = payloadInfo.Length == verification.OutputBytes &&
                              payloadHash.Equals(
                                  verification.OutputSha256,
                                  StringComparison.OrdinalIgnoreCase);
        if (!payloadIdentity)
        {
            Refuse("MOD005", "The renamed bundle payload differs from the staged UPK.");
        }

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
        {
            Refuse("MOD006", "The renamed payload did not retain package identity and table shape.");
        }
        var structural = PackageValidator.Validate(payloadSummary)
            .All(issue => issue.Severity != ValidationSeverity.Error);
        if (!structural)
        {
            Refuse("MOD007", "The renamed payload failed structural package validation.");
        }

        var assets = request.Assets is { Count: > 0 }
            ? request.Assets
                .GroupBy(asset => asset.NativeExportIndex)
                .Select(group => group.Single())
                .OrderBy(asset => asset.NativeExportIndex)
                .ToArray()
            : [new ArkiveModAsset(
                replacement.PackageAlias, replacement.NativeExportIndex,
                replacement.ClassName, replacement.ObjectName, replacement.PixelFormat,
                replacement.ReplacementSerialOffset, replacement.ReplacementSerialSize)];
        var externalResults = new List<ModExternalValidationResult>(assets.Length);
        foreach (var asset in assets)
        {
            externalResults.Add(await _externalValidator.ValidateAsync(
                    payloadPath, asset.ClassName, asset.ObjectName, cancellationToken)
                .ConfigureAwait(false));
        }
        var externalPassed = externalResults.All(result =>
            result.CataloguePassed && result.NativeLoadPassed && result.ExpectedObjectFound);
        if (!externalPassed)
        {
            Refuse("MOD008", "The renamed payload failed external object-load validation.");
        }
        var external = new ModExternalValidationResult(
            string.Join("; ", externalResults.Select(result => result.Validator).Distinct()),
            externalResults.Max(result => result.AssetCount),
            externalResults.Sum(result => result.ValidationFileCount),
            CataloguePassed: externalResults.All(result => result.CataloguePassed),
            NativeLoadPassed: externalResults.All(result => result.NativeLoadPassed),
            ExpectedObjectFound: externalResults.All(result => result.ExpectedObjectFound),
            externalResults.SelectMany(result => result.Diagnostics).Distinct().Take(100).ToArray(),
            TimeSpan.FromSeconds(externalResults.Sum(result => result.Duration.TotalSeconds)));

        var backupRoot = Path.GetFullPath(request.BackupRootDirectory);
        var proposedBackupPath = Path.Combine(
            backupRoot, sourceHashBefore, sourceName);
        var driveRoot = Path.GetPathRoot(backupRoot);
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            Refuse("MOD009", "The backup root has no resolvable drive.");
        }
        var availableBackupBytes = new DriveInfo(driveRoot).AvailableFreeSpace;
        var requiredBackupBytes = sourceInfo.Length;
        var backupSpace = availableBackupBytes >= requiredBackupBytes;
        if (!backupSpace)
        {
            Refuse("MOD010", "The backup drive does not have enough free space.");
        }
        var sourceHashAfter = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("MOD011", "The source package changed during the read-only rehearsal.");
        }

        var modId = Guid.NewGuid().ToString("N");
        var auditId = Guid.NewGuid().ToString("N");
        var checks = new List<ModReadinessCheck>
        {
            new("MOD_SOURCE_IDENTITY", ModReadinessCheckStatus.Passed,
                $"Source matches {sourceInfo.Length:N0} bytes and SHA-256 {sourceHashAfter}."),
            new("MOD_RENAMED_PAYLOAD", ModReadinessCheckStatus.Passed,
                $"Staged UPK was copied once as {installRelativePath}; SHA-256 {payloadHash}."),
            new("MOD_PACKAGE_IDENTITY", ModReadinessCheckStatus.Passed,
                $"Package GUID {payloadSummary.PackageGuid}, versions, tables, and {payloadSummary.CompressedChunks.Count:N0} chunks remain compatible."),
            new("MOD_STRUCTURE", ModReadinessCheckStatus.Passed,
                "Renamed payload passed Arkive structural validation."),
            new("MOD_EXTERNAL_LOAD", ModReadinessCheckStatus.Passed,
                $"{external.Validator} listed {external.AssetCount:N0} assets and loaded {assets.Length:N0} selected texture(s)."),
            new("MOD_BACKUP_CAPACITY", ModReadinessCheckStatus.Passed,
                $"Backup needs {requiredBackupBytes:N0} bytes; {availableBackupBytes:N0} are available."),
            new("MOD_DRY_RUN_ONLY", ModReadinessCheckStatus.Information,
                "No backup was created and no Lost Ark file was written, renamed, or replaced."),
        };
        var plannedSteps = new[]
        {
            "Require Lost Ark and its launcher to be closed.",
            "Recheck the installed source package size and SHA-256.",
            "Copy the original package to the backup path and verify its hash.",
            "Copy the payload to a temporary sibling of the install target and verify its hash.",
            "Atomically replace the install target while retaining the verified backup.",
            "On uninstall, restore the backup and verify the original source SHA-256.",
            "Refuse install or restore after a client update until compatibility is re-established.",
        };
        var ready = sourceMatches && payloadIdentity && summaryIdentity && structural &&
                    external.CataloguePassed && external.NativeLoadPassed &&
                    external.ExpectedObjectFound && backupSpace;
        watch.Stop();
        var readiness = new ModDeploymentReadinessManifest(
            CurrentManifestSchemaVersion, auditId, DateTimeOffset.UtcNow, modId,
            BundleManifestFileName, sourcePath, sourcePath, proposedBackupPath, payloadPath,
            sourceHashAfter, payloadHash, sourceInfo.Length, payloadInfo.Length,
            requiredBackupBytes, availableBackupBytes, sourceSummary.PackageGuid,
            payloadSummary.PackageGuid, payloadSummary.FileVersion,
            payloadSummary.LicenseeVersion, payloadSummary.EngineVersion,
            payloadSummary.CookerVersion, payloadSummary.NameCount,
            payloadSummary.ImportCount, payloadSummary.ExportCount,
            payloadSummary.CompressedChunks.Count, external.Validator,
            external.AssetCount, external.ValidationFileCount,
            SourceIdentityMatches: sourceMatches,
            PayloadIdentityMatchesManifest: payloadIdentity,
            PayloadUsesOriginalPhysicalName: sourceName.Equals(
                Path.GetFileName(payloadPath), StringComparison.OrdinalIgnoreCase),
            PackageSummaryIdentityRetained: summaryIdentity,
            StructuralValidationPassed: structural,
            ExternalCataloguePassed: external.CataloguePassed,
            ExternalNativeLoadPassed: external.NativeLoadPassed,
            ExpectedObjectFound: external.ExpectedObjectFound,
            BackupSpaceAvailable: backupSpace,
            ReadyForControlledInstall: ready,
            InstallationPerformed: false,
            GameFilesModified: false,
            plannedSteps, checks, external.Diagnostics, watch.Elapsed.TotalSeconds);
        var bundle = new ArkiveModBundleManifest(
            CurrentManifestSchemaVersion, modId, readiness.CreatedAt,
            request.DisplayName.Trim(), WesternGlobalProfile, replacement.BuildId,
            installRelativePath, sourceName, sourceInfo.Length, sourceHashAfter,
            payloadRelativePath, payloadInfo.Length, payloadHash,
            ReadinessManifestFileName,
            assets,
            ExactClientMatchRequired: true,
            BackupRequired: true,
            ReversibleInstallPlanned: true,
            ReadyForControlledInstall: ready,
            InstallationEnabled: false,
            GameFilesModified: false,
            checks);

        var readinessPath = Path.Combine(outputDirectory, ReadinessManifestFileName);
        var bundlePath = Path.Combine(outputDirectory, BundleManifestFileName);
        await SaveJsonAtomicAsync(readinessPath, readiness, cancellationToken).ConfigureAwait(false);
        await SaveJsonAtomicAsync(bundlePath, bundle, cancellationToken).ConfigureAwait(false);
        return new ArkiveModBundleResult(
            outputDirectory, bundlePath, readinessPath, payloadPath, bundle, readiness,
            [payloadPath, readinessPath, bundlePath], watch.Elapsed);
    }

    private static async Task<T> LoadAsync<T>(
        string path,
        string label,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The {label} was not found.", fullPath);
        }
        await using var stream = File.OpenRead(fullPath);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                   .ConfigureAwait(false) ??
               throw new InvalidDataException($"The {label} was empty.");
    }

    private static string ResolveWithin(string directory, string relativePath)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var fullPath = Path.GetFullPath(Path.Combine(fullDirectory, relativePath));
        var prefix = fullDirectory.TrimEnd(Path.DirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("MOD012", $"Bundle path escapes its session: {relativePath}");
        }
        return fullPath;
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var source = new FileStream(
                             sourcePath, FileMode.Open, FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete,
                             BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, BufferBytes, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(destination, BufferBytes, cancellationToken)
                    .ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task SaveJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new ArkiveModBundleRefusalException(code, message);
}
