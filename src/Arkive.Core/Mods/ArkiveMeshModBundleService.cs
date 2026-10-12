using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Meshes;
using Arkive.Core.Packages;
using Arkive.Core.Validation;

namespace Arkive.Core.Mods;

public sealed record ArkiveMeshModBundleRequest(
    string StagedUpkManifestPath,
    string OutputDirectory,
    string BackupRootDirectory,
    string DisplayName);

public sealed class ArkiveMeshModBundleRefusalException : NotSupportedException
{
    public ArkiveMeshModBundleRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Converts one verified staged SkeletalMesh UPK into the generic Arkive bundle
/// format consumed by the guarded atomic deployment service. This is a read-only
/// rehearsal: source identity, package structure, backup capacity, and an actual
/// UModel glTF export of the selected mesh must all pass before a bundle is emitted.
/// </summary>
public sealed class ArkiveMeshModBundleService
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
    private readonly IModPackageExternalValidator _externalValidator;

    public ArkiveMeshModBundleService(
        IModPackageExternalValidator externalValidator,
        UpkPackageReader? reader = null)
    {
        _externalValidator = externalValidator ??
                             throw new ArgumentNullException(nameof(externalValidator));
        _reader = reader ?? new UpkPackageReader();
    }

    public async Task<ArkiveModBundleResult> BuildAsync(
        ArkiveMeshModBundleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            Refuse("MMB001", "The mesh deployment-bundle directory must be empty.");
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            Refuse("MMB002", "The mesh mod display name is empty.");

        var verification = await SkeletalMeshStagedUpkService.VerifyOutputAsync(
            request.StagedUpkManifestPath, cancellationToken).ConfigureAwait(false);
        var staged = verification.Manifest;
        if (!staged.UmodelPreviewReady || staged.DeploymentReady || staged.PackageWasModified ||
            !staged.SameSizeInPlaceReplacement || !staged.ExportTableUnchanged ||
            !staged.AllUnrelatedLogicalBytesIdentical || !staged.TargetNativeDecodePassed)
            Refuse("MMB003", "The staged SkeletalMesh UPK does not retain all required safety gates.");

        var sourcePath = Path.GetFullPath(staged.SourcePackagePath);
        if (!File.Exists(sourcePath))
            Refuse("MMB004", $"The source game package was not found: {sourcePath}");
        var sourceName = Path.GetFileName(sourcePath);
        if (!sourceName.Equals(staged.PhysicalPackageName, StringComparison.OrdinalIgnoreCase))
            Refuse("MMB005", "The staged manifest does not use the source package's physical filename.");
        var sourceInfo = new FileInfo(sourcePath);
        var sourceHashBefore = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (sourceInfo.Length != staged.SourcePhysicalPackageBytes ||
            !sourceHashBefore.Equals(staged.SourcePackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("MMB006", "The source game package changed after staged mesh reconstruction.");

        var installRelativePath = Path.Combine("EFGame", "ReleasePC", "Packages", sourceName);
        var payloadRelativePath = Path.Combine("payload", installRelativePath);
        var payloadPath = ResolveWithin(outputDirectory, payloadRelativePath, "MMB007");
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        await CopyFileAsync(verification.OutputUpkPath, payloadPath, cancellationToken)
            .ConfigureAwait(false);
        var payloadInfo = new FileInfo(payloadPath);
        var payloadHash = await HashFileAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        if (payloadInfo.Length != staged.OutputPhysicalPackageBytes ||
            !payloadHash.Equals(staged.OutputPackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("MMB008", "The bundle payload differs from the verified staged SkeletalMesh UPK.");

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
            Refuse("MMB009", "The mesh payload did not retain package identity and catalogue shape.");
        var structural = PackageValidator.Validate(payloadSummary)
            .All(issue => issue.Severity != ValidationSeverity.Error);
        if (!structural)
            Refuse("MMB010", "The mesh deployment payload failed structural package validation.");

        var external = await _externalValidator.ValidateInPackageRootAsync(
            payloadPath, Path.GetDirectoryName(sourcePath)!, staged.ClassName,
            staged.ObjectName, cancellationToken).ConfigureAwait(false);
        if (!external.CataloguePassed || !external.NativeLoadPassed || !external.ExpectedObjectFound ||
            external.ValidationFileCount <= 0)
            Refuse("MMB011", "The mesh deployment payload failed UModel native-load validation.");

        var backupRoot = Path.GetFullPath(request.BackupRootDirectory);
        var proposedBackupPath = Path.Combine(backupRoot, sourceHashBefore, sourceName);
        var driveRoot = Path.GetPathRoot(backupRoot);
        if (string.IsNullOrWhiteSpace(driveRoot))
            Refuse("MMB012", "The backup root has no resolvable drive.");
        var availableBackupBytes = new DriveInfo(driveRoot).AvailableFreeSpace;
        var backupSpace = availableBackupBytes >= sourceInfo.Length;
        if (!backupSpace)
            Refuse("MMB013", "The backup drive does not have enough free space.");
        var sourceHashAfter = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
            Refuse("MMB014", "The source package changed during the read-only mesh rehearsal.");

        var modId = Guid.NewGuid().ToString("N");
        var auditId = Guid.NewGuid().ToString("N");
        var asset = new ArkiveModAsset(
            staged.PackageAlias, staged.NativeExportIndex, staged.ClassName,
            staged.ObjectName, "NativeSkeletalMesh", staged.SerialOffset, staged.SerialSize);
        var checks = new List<ModReadinessCheck>
        {
            new("MESH_SOURCE_IDENTITY", ModReadinessCheckStatus.Passed,
                $"Source matches {sourceInfo.Length:N0} bytes and SHA-256 {sourceHashAfter}."),
            new("MESH_STAGED_PROOF", ModReadinessCheckStatus.Passed,
                $"Staged build {staged.BuildId} preserves the export table and all unrelated logical bytes."),
            new("MESH_PAYLOAD_IDENTITY", ModReadinessCheckStatus.Passed,
                $"Payload retains physical name {sourceName} and SHA-256 {payloadHash}."),
            new("MESH_PACKAGE_IDENTITY", ModReadinessCheckStatus.Passed,
                $"Package GUID {payloadSummary.PackageGuid}, versions, tables, and {payloadSummary.CompressedChunks.Count:N0} chunks remain compatible."),
            new("MESH_EXTERNAL_LOAD", ModReadinessCheckStatus.Passed,
                $"{external.Validator} listed {external.AssetCount:N0} assets and exported {staged.ObjectName} as glTF."),
            new("MESH_BACKUP_CAPACITY", ModReadinessCheckStatus.Passed,
                $"Backup needs {sourceInfo.Length:N0} bytes; {availableBackupBytes:N0} are available."),
            new("MESH_REHEARSAL_ONLY", ModReadinessCheckStatus.Information,
                "Preparation did not create a backup or change any Lost Ark file."),
        };
        var plannedSteps = new[]
        {
            "Require Lost Ark, its launcher, and Easy Anti-Cheat to be closed.",
            "Recheck the exact source package size and SHA-256.",
            "Create and hash-verify a content-addressed backup of the original package.",
            "Copy and hash-verify the mesh payload to a temporary sibling of the target.",
            "Atomically replace the target and reopen the installed UPK structurally.",
            "Retain a durable receipt binding source, payload, target, and backup identities.",
            "Restore only while the target still matches this payload, then verify the original hash.",
            "Refuse after a client update or any unrecognized target modification.",
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
            ReadyForControlledInstall: true, InstallationPerformed: false,
            GameFilesModified: false, plannedSteps, checks, external.Diagnostics,
            watch.Elapsed.TotalSeconds);
        var bundle = new ArkiveModBundleManifest(
            ArkiveModBundleService.CurrentManifestSchemaVersion, modId, readiness.CreatedAt,
            request.DisplayName.Trim(), ArkiveModBundleService.WesternGlobalProfile,
            staged.BuildId, installRelativePath, sourceName, sourceInfo.Length, sourceHashAfter,
            payloadRelativePath, payloadInfo.Length, payloadHash,
            ArkiveModBundleService.ReadinessManifestFileName, [asset],
            ExactClientMatchRequired: true, BackupRequired: true,
            ReversibleInstallPlanned: true, ReadyForControlledInstall: true,
            InstallationEnabled: true, GameFilesModified: false, checks);

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
        throw new ArkiveMeshModBundleRefusalException(code, message);
}
