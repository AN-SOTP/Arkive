using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Packages;
using Arkive.Core.Validation;

namespace Arkive.Core.Mods;

public enum ModInstallationState
{
    NotInstalled,
    Prepared,
    Installed,
    Restored,
    Conflict,
    MissingTarget,
}

public sealed record ModInstallationReceipt(
    int SchemaVersion,
    string InstallationId,
    string ModId,
    string DisplayName,
    ModInstallationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? RestoredAt,
    string BundleManifestPath,
    string ReadinessManifestPath,
    string TargetPath,
    string BackupPath,
    string PayloadPath,
    long OriginalBytes,
    string OriginalSha256,
    long PayloadBytes,
    string PayloadSha256,
    DateTimeOffset OriginalLastWriteTimeUtc,
    bool BackupVerified,
    bool TargetVerified,
    bool RecoveredInterruptedOperation,
    bool GameFilesModified,
    IReadOnlyList<ModReadinessCheck> Checks);

public sealed record ArkiveModDeploymentRequest(
    string BundleManifestPath,
    string BackupRootDirectory,
    string StateRootDirectory);

public sealed record ModDeploymentInspection(
    ArkiveModBundleManifest Bundle,
    ModDeploymentReadinessManifest Readiness,
    ModInstallationReceipt? Receipt,
    ModInstallationState State,
    string BundleManifestPath,
    string ReadinessManifestPath,
    string PayloadPath,
    string TargetPath,
    string BackupPath,
    string ReceiptPath,
    string? CurrentTargetSha256,
    bool PayloadVerified,
    bool BackupExists,
    bool BackupVerified,
    bool CanInstall,
    bool CanRestore,
    string Summary)
{
    /// <summary>
    /// True only when the current target's verified byte identity proves this payload is active.
    /// A retained receipt may still say Installed after Steam repairs or reinstalls the client;
    /// receipt history alone must never block rebuilds or make restore-all overwrite a clean file.
    /// </summary>
    public bool HasActiveDeployment =>
        CanRestore || State is ModInstallationState.Installed or ModInstallationState.Prepared;
}

public sealed record ModDeploymentOperationResult(
    ModDeploymentInspection Inspection,
    ModInstallationReceipt Receipt,
    bool ChangedTarget,
    bool RecoveredInterruptedOperation,
    TimeSpan Duration);

public interface IModDeploymentProcessGuard
{
    IReadOnlyList<string> FindBlockingProcesses();
}

public sealed class LostArkProcessGuard : IModDeploymentProcessGuard
{
    private static readonly HashSet<string> BlockingNames = new(
        [
            "LOSTARK",
            "LostArkLauncher",
            "start_protected_game",
            "EasyAntiCheat",
            "EasyAntiCheat_EOS",
        ],
        StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> FindBlockingProcesses()
    {
        var matches = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (BlockingNames.Contains(process.ProcessName))
                    {
                        matches.Add($"{process.ProcessName} (PID {process.Id})");
                    }
                }
                catch (InvalidOperationException)
                {
                    // A process may exit while the snapshot is being inspected.
                }
            }
        }

        return matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed class ArkiveModDeploymentRefusalException : NotSupportedException
{
    public ArkiveModDeploymentRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Installs one verified Arkive mod bundle by creating a durable, content-addressed
/// backup and atomically replacing the exact package used to build the bundle.
/// Restoration is allowed only while the target still matches that bundle payload.
/// </summary>
public sealed class ArkiveModDeploymentService
{
    public const int CurrentReceiptSchemaVersion = 1;
    public const string ReceiptFileName = "arkive-installation.json";

    private const int BufferBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly UpkPackageReader _reader;
    private readonly IModDeploymentProcessGuard _processGuard;

    public ArkiveModDeploymentService(
        UpkPackageReader? reader = null,
        IModDeploymentProcessGuard? processGuard = null)
    {
        _reader = reader ?? new UpkPackageReader();
        _processGuard = processGuard ?? new LostArkProcessGuard();
    }

    /// <summary>The game, launcher or anti-cheat processes that must be closed before game files change.</summary>
    public IReadOnlyList<string> FindBlockingProcesses() => _processGuard.FindBlockingProcesses();

    public async Task<ModDeploymentInspection> InspectAsync(
        ArkiveModDeploymentRequest request,
        CancellationToken cancellationToken = default)
        => await InspectCoreAsync(
                request, requireControlledInstallReadiness: true,
                validateVariableSizeGate: true, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Performs the same byte-identity and receipt checks as normal deployment inspection, but
    /// permits recovery from a DEP030 stale variable-size approval receipt. This path is read-only,
    /// never authorizes installation, and exists so an exactly identified installed payload can
    /// still be restored to its verified backup after ancillary approval evidence was changed.
    /// </summary>
    public async Task<ModDeploymentInspection> InspectForRecoveryAsync(
        ArkiveModDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await InspectCoreAsync(
                    request, requireControlledInstallReadiness: true,
                    validateVariableSizeGate: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArkiveModDeploymentRefusalException exception) when (exception.Code == "DEP030")
        {
            var inspection = await InspectCoreAsync(
                    request, requireControlledInstallReadiness: true,
                    validateVariableSizeGate: false, cancellationToken)
                .ConfigureAwait(false);
            return inspection with
            {
                CanInstall = false,
                Summary = inspection.Summary +
                          " The variable-size approval receipt changed after the build; installation remains blocked, but Arkive can still use exact payload, receipt, and backup hashes for a verified restore.",
            };
        }
    }

    /// <summary>
    /// Reads immutable bundle identities, receipts, backups, and the current target without
    /// requiring an obsolete or preview-only revision to have passed install authorization.
    /// This method never authorizes installation and is intended only for fail-closed cleanup
    /// decisions across retained revision history.
    /// </summary>
    public async Task<ModDeploymentInspection> InspectForDeletionIdentityAsync(
        ArkiveModDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectCoreAsync(
                request, requireControlledInstallReadiness: false,
                validateVariableSizeGate: false, cancellationToken)
            .ConfigureAwait(false);
        return inspection with
        {
            CanInstall = false,
            Summary = inspection.Summary +
                      " This revision was inspected for deletion identity only; it is not authorized for installation.",
        };
    }

    private async Task<ModDeploymentInspection> InspectCoreAsync(
        ArkiveModDeploymentRequest request,
        bool requireControlledInstallReadiness,
        bool validateVariableSizeGate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bundlePath = Path.GetFullPath(request.BundleManifestPath);
        var bundle = await ArkiveModBundleService.LoadBundleAsync(bundlePath, cancellationToken)
            .ConfigureAwait(false);
        var bundleDirectory = Path.GetDirectoryName(bundlePath)!;
        var readinessPath = ResolveWithin(
            bundleDirectory, bundle.DeploymentReadinessRelativePath, "DEP001");
        var readiness = await ArkiveModBundleService.LoadReadinessAsync(
            readinessPath, cancellationToken).ConfigureAwait(false);
        ValidateManifestPair(bundle, readiness, requireControlledInstallReadiness);

        var payloadPath = ResolveWithin(bundleDirectory, bundle.PayloadRelativePath, "DEP002");
        if (!File.Exists(payloadPath))
        {
            Refuse("DEP003", $"The mod payload is missing: {payloadPath}");
        }
        var payloadInfo = new FileInfo(payloadPath);
        var payloadHash = await HashFileAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        var payloadVerified = payloadInfo.Length == bundle.PayloadBytes &&
                              payloadHash.Equals(bundle.PayloadSha256, StringComparison.OrdinalIgnoreCase);
        if (!payloadVerified)
        {
            Refuse("DEP004", "The mod payload no longer matches its manifest.");
        }
        if (validateVariableSizeGate)
        {
            await ValidateVariableSizeDeploymentGateAsync(
                    bundleDirectory, payloadPath, bundle, readiness, cancellationToken)
                .ConfigureAwait(false);
        }

        var targetPath = Path.GetFullPath(readiness.InstallTargetPath);
        ValidateTargetPath(targetPath, readiness, bundle);
        var backupRoot = Path.GetFullPath(request.BackupRootDirectory);
        var stateRoot = Path.GetFullPath(request.StateRootDirectory);
        var backupPath = ResolveWithin(
            backupRoot,
            Path.Combine(bundle.RequiredSourcePackageSha256, bundle.SourcePhysicalPackageName),
            "DEP005");
        var receiptPath = ResolveWithin(
            stateRoot,
            Path.Combine(bundle.ModId, ReceiptFileName),
            "DEP006");
        var receipt = File.Exists(receiptPath)
            ? await LoadReceiptAsync(receiptPath, cancellationToken).ConfigureAwait(false)
            : null;
        if (receipt is not null)
        {
            ValidateReceipt(receipt, bundle, targetPath, backupPath, payloadPath);
        }

        var backupExists = File.Exists(backupPath);
        var backupVerified = false;
        if (backupExists)
        {
            var backupInfo = new FileInfo(backupPath);
            var backupHash = await HashFileAsync(backupPath, cancellationToken).ConfigureAwait(false);
            backupVerified = backupInfo.Length == bundle.RequiredSourcePackageBytes &&
                             backupHash.Equals(
                                 bundle.RequiredSourcePackageSha256,
                                 StringComparison.OrdinalIgnoreCase);
        }

        if (!File.Exists(targetPath))
        {
            return new ModDeploymentInspection(
                bundle, readiness, receipt, ModInstallationState.MissingTarget,
                bundlePath, readinessPath, payloadPath, targetPath, backupPath, receiptPath,
                null, payloadVerified, backupExists, backupVerified,
                CanInstall: false, CanRestore: false,
                "The expected Lost Ark package is missing. Repair or update the client before continuing.");
        }

        var targetInfo = new FileInfo(targetPath);
        var targetHash = await HashFileAsync(targetPath, cancellationToken).ConfigureAwait(false);
        var isOriginal = targetInfo.Length == bundle.RequiredSourcePackageBytes &&
                         targetHash.Equals(
                             bundle.RequiredSourcePackageSha256,
                             StringComparison.OrdinalIgnoreCase);
        var isPayload = targetInfo.Length == bundle.PayloadBytes &&
                        targetHash.Equals(bundle.PayloadSha256, StringComparison.OrdinalIgnoreCase);

        if (isOriginal)
        {
            var state = receipt?.State == ModInstallationState.Restored
                ? ModInstallationState.Restored
                : ModInstallationState.NotInstalled;
            var backupUsable = !backupExists || backupVerified;
            return new ModDeploymentInspection(
                bundle, readiness, receipt, state,
                bundlePath, readinessPath, payloadPath, targetPath, backupPath, receiptPath,
                targetHash, payloadVerified, backupExists, backupVerified,
                CanInstall: backupUsable, CanRestore: false,
                backupUsable
                    ? state == ModInstallationState.Restored
                        ? "The original package is restored. This mod can be installed again."
                        : "The exact supported client package is present and ready for installation."
                    : "A file occupies the required backup path but does not match the original package.");
        }

        if (isPayload && receipt is not null)
        {
            var state = receipt.State == ModInstallationState.Prepared
                ? ModInstallationState.Prepared
                : ModInstallationState.Installed;
            return new ModDeploymentInspection(
                bundle, readiness, receipt, state,
                bundlePath, readinessPath, payloadPath, targetPath, backupPath, receiptPath,
                targetHash, payloadVerified, backupExists, backupVerified,
                CanInstall: state == ModInstallationState.Prepared && backupVerified,
                CanRestore: backupVerified,
                state == ModInstallationState.Prepared
                    ? "The payload is installed but the prior transaction was interrupted; Arkive can finalize it or restore the backup."
                    : "The mod payload is installed and its verified original backup is available for restoration.");
        }

        return new ModDeploymentInspection(
            bundle, readiness, receipt, ModInstallationState.Conflict,
            bundlePath, readinessPath, payloadPath, targetPath, backupPath, receiptPath,
            targetHash, payloadVerified, backupExists, backupVerified,
            CanInstall: false, CanRestore: false,
            isPayload
                ? "The payload is present without its matching Arkive receipt; automatic restore is unsafe."
                : "The installed package matches neither the required client nor this mod payload. It may have been updated or changed by another tool.");
    }

    public async Task<ModDeploymentOperationResult> InstallAsync(
        ArkiveModDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        Directory.CreateDirectory(Path.GetFullPath(request.StateRootDirectory));
        await using var deploymentLock = AcquireDeploymentLock(request.StateRootDirectory);
        var inspection = await InspectAsync(request, cancellationToken).ConfigureAwait(false);

        if (inspection.State == ModInstallationState.Prepared &&
            inspection.Receipt is not null && inspection.BackupVerified)
        {
            var recovered = inspection.Receipt with
            {
                State = ModInstallationState.Installed,
                UpdatedAt = DateTimeOffset.UtcNow,
                InstalledAt = inspection.Receipt.InstalledAt ?? DateTimeOffset.UtcNow,
                TargetVerified = true,
                RecoveredInterruptedOperation = true,
                GameFilesModified = true,
                Checks = AppendCheck(
                    inspection.Receipt.Checks,
                    "DEP_INSTALL_RECOVERED",
                    "Recovered an interrupted transaction after verifying the installed payload and original backup."),
            };
            await SaveReceiptAsync(inspection.ReceiptPath, recovered, cancellationToken)
                .ConfigureAwait(false);
            watch.Stop();
            var finalInspection = await InspectAsync(request, cancellationToken).ConfigureAwait(false);
            return new ModDeploymentOperationResult(
                finalInspection, recovered, ChangedTarget: false,
                RecoveredInterruptedOperation: true, watch.Elapsed);
        }

        if (inspection.State == ModInstallationState.Installed && inspection.Receipt is not null)
        {
            watch.Stop();
            return new ModDeploymentOperationResult(
                inspection, inspection.Receipt, ChangedTarget: false,
                RecoveredInterruptedOperation: false, watch.Elapsed);
        }
        if (!inspection.CanInstall)
        {
            Refuse("DEP007", inspection.Summary);
        }

        EnsureProcessesClosed();
        EnsureTargetCanBeExclusivelyOpened(inspection.TargetPath);
        var targetInfo = new FileInfo(inspection.TargetPath);
        var originalTimestamp = new DateTimeOffset(targetInfo.LastWriteTimeUtc, TimeSpan.Zero);
        EnsureBackupCapacity(
            inspection.BackupPath,
            inspection.Bundle.RequiredSourcePackageBytes);
        await EnsureVerifiedBackupAsync(
            inspection.TargetPath,
            inspection.BackupPath,
            inspection.Bundle.RequiredSourcePackageBytes,
            inspection.Bundle.RequiredSourcePackageSha256,
            originalTimestamp,
            cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var receipt = new ModInstallationReceipt(
            CurrentReceiptSchemaVersion,
            Guid.NewGuid().ToString("N"),
            inspection.Bundle.ModId,
            inspection.Bundle.DisplayName,
            ModInstallationState.Prepared,
            now,
            now,
            InstalledAt: null,
            RestoredAt: null,
            inspection.BundleManifestPath,
            inspection.ReadinessManifestPath,
            inspection.TargetPath,
            inspection.BackupPath,
            inspection.PayloadPath,
            inspection.Bundle.RequiredSourcePackageBytes,
            inspection.Bundle.RequiredSourcePackageSha256,
            inspection.Bundle.PayloadBytes,
            inspection.Bundle.PayloadSha256,
            originalTimestamp,
            BackupVerified: true,
            TargetVerified: false,
            RecoveredInterruptedOperation: false,
            GameFilesModified: false,
            [
                new ModReadinessCheck(
                    "DEP_INSTALL_PREFLIGHT",
                    ModReadinessCheckStatus.Passed,
                    "Bundle, readiness audit, payload, client identity, process gate, target lock, and backup were verified."),
            ]);
        await SaveReceiptAsync(inspection.ReceiptPath, receipt, cancellationToken)
            .ConfigureAwait(false);

        var temporaryPath = CreateSiblingTemporaryPath(inspection.TargetPath, "installing");
        var targetChanged = false;
        try
        {
            await CopyVerifiedAsync(
                inspection.PayloadPath,
                temporaryPath,
                inspection.Bundle.PayloadBytes,
                inspection.Bundle.PayloadSha256,
                cancellationToken).ConfigureAwait(false);
            EnsureTargetCanBeExclusivelyOpened(inspection.TargetPath);
            await VerifyFileIdentityAsync(
                inspection.TargetPath,
                inspection.Bundle.RequiredSourcePackageBytes,
                inspection.Bundle.RequiredSourcePackageSha256,
                "The installed package changed after preflight; Arkive refused to overwrite it.",
                cancellationToken).ConfigureAwait(false);
            File.Replace(temporaryPath, inspection.TargetPath, destinationBackupFileName: null,
                ignoreMetadataErrors: true);
            targetChanged = true;
            await VerifyPackageAsync(
                inspection.TargetPath,
                inspection.Bundle.PayloadBytes,
                inspection.Bundle.PayloadSha256,
                cancellationToken).ConfigureAwait(false);

            now = DateTimeOffset.UtcNow;
            receipt = receipt with
            {
                State = ModInstallationState.Installed,
                UpdatedAt = now,
                InstalledAt = now,
                TargetVerified = true,
                GameFilesModified = true,
                Checks = AppendCheck(
                    receipt.Checks,
                    "DEP_INSTALL_COMMITTED",
                    "The verified payload atomically replaced the supported package and was re-read successfully."),
            };
            await SaveReceiptAsync(inspection.ReceiptPath, receipt, cancellationToken)
                .ConfigureAwait(false);
            watch.Stop();
            var finalInspection = await InspectAsync(request, cancellationToken).ConfigureAwait(false);
            return new ModDeploymentOperationResult(
                finalInspection, receipt, ChangedTarget: true,
                RecoveredInterruptedOperation: false, watch.Elapsed);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or ArgumentException)
        {
            if (targetChanged)
            {
                try
                {
                    await ReplaceWithVerifiedFileAsync(
                        inspection.BackupPath,
                        inspection.TargetPath,
                        inspection.Bundle.RequiredSourcePackageBytes,
                        inspection.Bundle.RequiredSourcePackageSha256,
                        cancellationToken).ConfigureAwait(false);
                    File.SetLastWriteTimeUtc(inspection.TargetPath, originalTimestamp.UtcDateTime);
                    receipt = receipt with
                    {
                        State = ModInstallationState.Restored,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        RestoredAt = DateTimeOffset.UtcNow,
                        TargetVerified = true,
                        GameFilesModified = false,
                        Checks = AppendCheck(
                            receipt.Checks,
                            "DEP_INSTALL_ROLLED_BACK",
                            "Installation verification failed; Arkive atomically restored and reverified the original package."),
                    };
                    await SaveReceiptAsync(inspection.ReceiptPath, receipt, cancellationToken)
                        .ConfigureAwait(false);
                    Refuse("DEP008", $"Installation failed and the original package was restored: {exception.Message}");
                }
                catch (ArkiveModDeploymentRefusalException)
                {
                    throw;
                }
                catch (Exception rollbackException) when (
                    rollbackException is IOException or UnauthorizedAccessException or
                        InvalidDataException or NotSupportedException or ArgumentException)
                {
                    Refuse(
                        "DEP009",
                        $"Installation failed and automatic rollback also failed. Do not launch the game. " +
                        $"Restore '{inspection.BackupPath}' to '{inspection.TargetPath}'. " +
                        $"Install error: {exception.Message}; rollback error: {rollbackException.Message}");
                }
            }

            receipt = receipt with
            {
                State = ModInstallationState.Restored,
                UpdatedAt = DateTimeOffset.UtcNow,
                RestoredAt = DateTimeOffset.UtcNow,
                TargetVerified = true,
                GameFilesModified = false,
                Checks = AppendCheck(
                    receipt.Checks,
                    "DEP_INSTALL_ABORTED",
                    "Installation stopped before the target package was replaced."),
            };
            await SaveReceiptAsync(inspection.ReceiptPath, receipt, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task<ModDeploymentOperationResult> RestoreAsync(
        ArkiveModDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        Directory.CreateDirectory(Path.GetFullPath(request.StateRootDirectory));
        await using var deploymentLock = AcquireDeploymentLock(request.StateRootDirectory);
        var inspection = await InspectForRecoveryAsync(request, cancellationToken).ConfigureAwait(false);
        if (inspection.Receipt is null)
        {
            Refuse("DEP010", "No matching Arkive installation receipt exists; automatic restore is unsafe.");
        }
        if (inspection.State is ModInstallationState.NotInstalled or ModInstallationState.Restored)
        {
            var alreadyRestored = inspection.Receipt with
            {
                State = ModInstallationState.Restored,
                UpdatedAt = DateTimeOffset.UtcNow,
                RestoredAt = inspection.Receipt.RestoredAt ?? DateTimeOffset.UtcNow,
                TargetVerified = true,
                GameFilesModified = false,
            };
            await SaveReceiptAsync(inspection.ReceiptPath, alreadyRestored, cancellationToken)
                .ConfigureAwait(false);
            watch.Stop();
            var finalInspection = await InspectForRecoveryAsync(request, cancellationToken).ConfigureAwait(false);
            return new ModDeploymentOperationResult(
                finalInspection, alreadyRestored, ChangedTarget: false,
                RecoveredInterruptedOperation: inspection.Receipt.State == ModInstallationState.Prepared,
                watch.Elapsed);
        }
        if (!inspection.CanRestore || !inspection.BackupVerified)
        {
            Refuse("DEP011", inspection.Summary);
        }

        EnsureProcessesClosed();
        EnsureTargetCanBeExclusivelyOpened(inspection.TargetPath);
        var temporaryPath = CreateSiblingTemporaryPath(inspection.TargetPath, "restoring");
        var targetChanged = false;
        try
        {
            await CopyVerifiedAsync(
                inspection.BackupPath,
                temporaryPath,
                inspection.Bundle.RequiredSourcePackageBytes,
                inspection.Bundle.RequiredSourcePackageSha256,
                cancellationToken).ConfigureAwait(false);
            EnsureTargetCanBeExclusivelyOpened(inspection.TargetPath);
            await VerifyFileIdentityAsync(
                inspection.TargetPath,
                inspection.Bundle.PayloadBytes,
                inspection.Bundle.PayloadSha256,
                "The installed mod package changed after restore preflight; Arkive refused to overwrite it.",
                cancellationToken).ConfigureAwait(false);
            File.Replace(temporaryPath, inspection.TargetPath, destinationBackupFileName: null,
                ignoreMetadataErrors: true);
            targetChanged = true;
            File.SetLastWriteTimeUtc(
                inspection.TargetPath, inspection.Receipt.OriginalLastWriteTimeUtc.UtcDateTime);
            await VerifyPackageAsync(
                inspection.TargetPath,
                inspection.Bundle.RequiredSourcePackageBytes,
                inspection.Bundle.RequiredSourcePackageSha256,
                cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var restored = inspection.Receipt with
            {
                State = ModInstallationState.Restored,
                UpdatedAt = now,
                RestoredAt = now,
                TargetVerified = true,
                GameFilesModified = false,
                Checks = AppendCheck(
                    inspection.Receipt.Checks,
                    "DEP_RESTORE_COMMITTED",
                    "The verified original backup atomically replaced the mod payload and was re-read successfully."),
            };
            await SaveReceiptAsync(inspection.ReceiptPath, restored, cancellationToken)
                .ConfigureAwait(false);
            watch.Stop();
            var finalInspection = await InspectForRecoveryAsync(request, cancellationToken).ConfigureAwait(false);
            return new ModDeploymentOperationResult(
                finalInspection, restored, ChangedTarget: true,
                RecoveredInterruptedOperation: inspection.State == ModInstallationState.Prepared,
                watch.Elapsed);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or ArgumentException)
        {
            if (targetChanged)
            {
                try
                {
                    await ReplaceWithVerifiedFileAsync(
                        inspection.PayloadPath,
                        inspection.TargetPath,
                        inspection.Bundle.PayloadBytes,
                        inspection.Bundle.PayloadSha256,
                        cancellationToken).ConfigureAwait(false);
                    Refuse("DEP012", $"Restore verification failed; Arkive put the verified mod payload back: {exception.Message}");
                }
                catch (ArkiveModDeploymentRefusalException)
                {
                    throw;
                }
                catch (Exception rollbackException) when (
                    rollbackException is IOException or UnauthorizedAccessException or
                        InvalidDataException or NotSupportedException or ArgumentException)
                {
                    Refuse(
                        "DEP013",
                        $"Restore and recovery both failed. Do not launch the game. " +
                        $"Restore '{inspection.BackupPath}' to '{inspection.TargetPath}'. " +
                        $"Restore error: {exception.Message}; recovery error: {rollbackException.Message}");
                }
            }
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static async Task<ModInstallationReceipt> LoadReceiptAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The Arkive installation receipt was not found.", fullPath);
        }
        await using var stream = File.OpenRead(fullPath);
        var receipt = await JsonSerializer.DeserializeAsync<ModInstallationReceipt>(
                          stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                      throw new InvalidDataException("The Arkive installation receipt was empty.");
        if (receipt.SchemaVersion != CurrentReceiptSchemaVersion)
        {
            Refuse(
                "DEP014",
                $"Installation receipt schema {receipt.SchemaVersion} is unsupported; " +
                $"Arkive requires {CurrentReceiptSchemaVersion}.");
        }
        return receipt;
    }

    private async Task VerifyPackageAsync(
        string path,
        long expectedBytes,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (info.Length != expectedBytes ||
            !hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The package failed post-replacement size or SHA-256 verification.");
        }
        var summary = _reader.Read(path);
        if (PackageValidator.Validate(summary).Any(issue => issue.Severity == ValidationSeverity.Error))
        {
            throw new InvalidDataException("The package failed post-replacement structural validation.");
        }
    }

    private static void ValidateManifestPair(
        ArkiveModBundleManifest bundle,
        ModDeploymentReadinessManifest readiness,
        bool requireControlledInstallReadiness)
    {
        if (requireControlledInstallReadiness &&
            (!bundle.ReadyForControlledInstall || !readiness.ReadyForControlledInstall))
        {
            Refuse("DEP015", "The bundle did not pass its controlled-install readiness audit.");
        }
        if (!bundle.ModId.Equals(readiness.ModId, StringComparison.Ordinal) ||
            !readiness.SourcePackageSha256.Equals(
                bundle.RequiredSourcePackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !readiness.PayloadSha256.Equals(bundle.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            readiness.SourcePackageBytes != bundle.RequiredSourcePackageBytes ||
            readiness.PayloadBytes != bundle.PayloadBytes ||
            readiness.VariableSizeMeshCount != bundle.VariableSizeMeshCount ||
            readiness.VariableSizeDeploymentApproved != bundle.VariableSizeDeploymentApproved ||
            !string.Equals(
                readiness.VariableSizeDeploymentGateRelativePath,
                bundle.VariableSizeDeploymentGateRelativePath,
                StringComparison.Ordinal) ||
            !string.Equals(
                readiness.VariableSizeDeploymentGateSha256,
                bundle.VariableSizeDeploymentGateSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            Refuse("DEP016", "The mod manifest and deployment-readiness report do not describe the same bundle.");
        }
        if (requireControlledInstallReadiness && bundle.VariableSizeMeshCount > 0 &&
            (!bundle.VariableSizeDeploymentApproved ||
             !readiness.VariableSizeDeploymentApproved ||
             string.IsNullOrWhiteSpace(bundle.VariableSizeDeploymentGateRelativePath) ||
             string.IsNullOrWhiteSpace(bundle.VariableSizeDeploymentGateSha256) ||
             !bundle.Checks.Any(check =>
                 check.Code.Equals("COMBINED_VARIABLE_SIZE_DEPLOYMENT_GATE", StringComparison.Ordinal) &&
                 check.Status == ModReadinessCheckStatus.Passed) ||
             !readiness.Checks.Any(check =>
                 check.Code.Equals("COMBINED_VARIABLE_SIZE_DEPLOYMENT_GATE", StringComparison.Ordinal) &&
                 check.Status == ModReadinessCheckStatus.Passed)))
        {
            Refuse("DEP026",
                "The variable-size package does not retain its controlled deployment acceptance gate.");
        }
        if (requireControlledInstallReadiness && bundle.VariableSizeMeshCount == 0 &&
            (bundle.VariableSizeDeploymentApproved ||
             !string.IsNullOrWhiteSpace(bundle.VariableSizeDeploymentGateRelativePath) ||
             !string.IsNullOrWhiteSpace(bundle.VariableSizeDeploymentGateSha256)))
        {
            Refuse("DEP027",
                "A fixed-size package unexpectedly claims a variable-size deployment gate.");
        }
    }

    private static async Task ValidateVariableSizeDeploymentGateAsync(
        string bundleDirectory,
        string payloadPath,
        ArkiveModBundleManifest bundle,
        ModDeploymentReadinessManifest readiness,
        CancellationToken cancellationToken)
    {
        if (bundle.VariableSizeMeshCount == 0) return;

        var gatePath = ResolveWithin(
            bundleDirectory, bundle.VariableSizeDeploymentGateRelativePath!, "DEP028");
        if (!File.Exists(gatePath))
            Refuse("DEP029", "The variable-size deployment-gate receipt is missing.");
        var gateHash = await HashFileAsync(gatePath, cancellationToken).ConfigureAwait(false);
        if (!gateHash.Equals(
                bundle.VariableSizeDeploymentGateSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("DEP030", "The variable-size deployment-gate receipt was changed after approval.");

        var gate = await UnifiedPackageComposerService.LoadVariableSizeDeploymentGateAsync(
            gatePath, cancellationToken).ConfigureAwait(false);
        if (!gate.ControlledInstallApproved ||
            gate.VariableSizeMeshCount != bundle.VariableSizeMeshCount ||
            !gate.CompositionId.Equals(bundle.ReplacementBuildId, StringComparison.Ordinal) ||
            !gate.GameProfile.Equals(bundle.GameProfile, StringComparison.Ordinal) ||
            !Path.GetFullPath(gate.SourcePackagePath).Equals(
                Path.GetFullPath(readiness.SourcePackagePath),
                StringComparison.OrdinalIgnoreCase) ||
            gate.SourcePackageBytes != bundle.RequiredSourcePackageBytes ||
            !gate.SourcePackageSha256.Equals(
                bundle.RequiredSourcePackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(gate.PayloadPath).Equals(
                Path.GetFullPath(payloadPath), StringComparison.OrdinalIgnoreCase) ||
            gate.PayloadBytes != bundle.PayloadBytes ||
            !gate.PayloadSha256.Equals(bundle.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            !gate.EveryTopologyStageVerified || !gate.EveryUnrelatedExportRetained ||
            !gate.EveryAffectedChunkDecodedBack ||
            !gate.ExactObjectExternalValidationPassed ||
            !gate.OriginalPhysicalPackageNameRetained || !gate.BackupCapacityPassed ||
            !gate.AtomicVariableSizeReplacementSupported ||
            gate.TopologyStagingManifestPaths.Count != bundle.VariableSizeMeshCount ||
            gate.Checks.Count == 0 ||
            gate.Checks.Any(check => check.Status != ModReadinessCheckStatus.Passed))
        {
            Refuse("DEP031",
                "The variable-size deployment-gate receipt does not match this exact source and payload.");
        }
    }

    private static void ValidateTargetPath(
        string targetPath,
        ModDeploymentReadinessManifest readiness,
        ArkiveModBundleManifest bundle)
    {
        var readinessTarget = Path.GetFullPath(readiness.SourcePackagePath);
        if (!targetPath.Equals(readinessTarget, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(targetPath).Equals(
                bundle.SourcePhysicalPackageName, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("DEP017", "The deployment target does not match the exact package audited by Arkive.");
        }
        var normalizedRelative = bundle.SourcePackageRelativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        if (!targetPath.EndsWith(
                Path.DirectorySeparatorChar + normalizedRelative,
                StringComparison.OrdinalIgnoreCase))
        {
            Refuse("DEP018", "The deployment target is outside the expected Western/global client layout.");
        }
    }

    private static void ValidateReceipt(
        ModInstallationReceipt receipt,
        ArkiveModBundleManifest bundle,
        string targetPath,
        string backupPath,
        string payloadPath)
    {
        if (!receipt.ModId.Equals(bundle.ModId, StringComparison.Ordinal) ||
            !Path.GetFullPath(receipt.TargetPath).Equals(targetPath, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(receipt.BackupPath).Equals(backupPath, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(receipt.PayloadPath).Equals(payloadPath, StringComparison.OrdinalIgnoreCase) ||
            !receipt.OriginalSha256.Equals(
                bundle.RequiredSourcePackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !receipt.PayloadSha256.Equals(bundle.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            receipt.OriginalBytes != bundle.RequiredSourcePackageBytes ||
            receipt.PayloadBytes != bundle.PayloadBytes)
        {
            Refuse("DEP019", "The existing installation receipt does not match this bundle and target.");
        }
    }

    private void EnsureProcessesClosed()
    {
        var blocking = _processGuard.FindBlockingProcesses();
        if (blocking.Count > 0)
        {
            Refuse(
                "DEP020",
                "Close Lost Ark and its anti-cheat launcher before installing or restoring: " +
                string.Join(", ", blocking));
        }
    }

    private static void EnsureTargetCanBeExclusivelyOpened(string targetPath)
    {
        try
        {
            using var stream = new FileStream(
                targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Refuse(
                "DEP021",
                $"The package cannot be locked for an exclusive update. Close Lost Ark and any viewer using it: {exception.Message}");
        }
    }

    private static async Task EnsureVerifiedBackupAsync(
        string sourcePath,
        string backupPath,
        long expectedBytes,
        string expectedSha256,
        DateTimeOffset originalTimestamp,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        if (!File.Exists(backupPath))
        {
            var temporaryPath = backupPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await CopyVerifiedAsync(
                    sourcePath, temporaryPath, expectedBytes, expectedSha256, cancellationToken)
                    .ConfigureAwait(false);
                File.SetLastWriteTimeUtc(temporaryPath, originalTimestamp.UtcDateTime);
                try
                {
                    File.Move(temporaryPath, backupPath);
                }
                catch (IOException) when (File.Exists(backupPath))
                {
                    // Another verified operation may have won the create-only race.
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        var info = new FileInfo(backupPath);
        var hash = await HashFileAsync(backupPath, cancellationToken).ConfigureAwait(false);
        if (info.Length != expectedBytes ||
            !hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("DEP022", "The content-addressed backup path exists but is not the required original package.");
        }
    }

    private static void EnsureBackupCapacity(string backupPath, long requiredBytes)
    {
        if (File.Exists(backupPath)) return;
        var root = Path.GetPathRoot(Path.GetFullPath(backupPath));
        if (string.IsNullOrWhiteSpace(root) ||
            new DriveInfo(root).AvailableFreeSpace < requiredBytes)
        {
            Refuse(
                "DEP024",
                $"The backup drive does not currently have the required {requiredBytes:N0} free bytes.");
        }
    }

    private static async Task VerifyFileIdentityAsync(
        string path,
        long expectedBytes,
        string expectedSha256,
        string refusalMessage,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (info.Length != expectedBytes ||
            !hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("DEP025", refusalMessage);
        }
    }

    private static async Task ReplaceWithVerifiedFileAsync(
        string sourcePath,
        string targetPath,
        long expectedBytes,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var temporaryPath = CreateSiblingTemporaryPath(targetPath, "recovering");
        try
        {
            await CopyVerifiedAsync(
                sourcePath, temporaryPath, expectedBytes, expectedSha256, cancellationToken)
                .ConfigureAwait(false);
            File.Replace(temporaryPath, targetPath, destinationBackupFileName: null,
                ignoreMetadataErrors: true);
            var targetInfo = new FileInfo(targetPath);
            var targetHash = await HashFileAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (targetInfo.Length != expectedBytes ||
                !targetHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Atomic recovery did not produce the expected package.");
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task CopyVerifiedAsync(
        string sourcePath,
        string destinationPath,
        long expectedBytes,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using (var source = new FileStream(
                         sourcePath, FileMode.Open, FileAccess.Read,
                         FileShare.Read, BufferBytes,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(
                         destinationPath, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, BufferBytes,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(destination, BufferBytes, cancellationToken)
                .ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }

        var info = new FileInfo(destinationPath);
        var hash = await HashFileAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        if (info.Length != expectedBytes ||
            !hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A staged deployment copy failed size or SHA-256 verification.");
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

    private static FileStream AcquireDeploymentLock(string stateRootDirectory)
    {
        var lockPath = Path.Combine(Path.GetFullPath(stateRootDirectory), "arkive-deployment.lock");
        try
        {
            return new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.Asynchronous);
        }
        catch (IOException exception)
        {
            Refuse("DEP023", $"Another Arkive deployment operation is active: {exception.Message}");
            throw;
        }
    }

    private static async Task SaveReceiptAsync(
        string path,
        ModInstallationReceipt receipt,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, receipt, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static IReadOnlyList<ModReadinessCheck> AppendCheck(
        IReadOnlyList<ModReadinessCheck> checks,
        string code,
        string message) =>
        checks.Concat([
            new ModReadinessCheck(code, ModReadinessCheckStatus.Passed, message),
        ]).ToArray();

    private static string CreateSiblingTemporaryPath(string targetPath, string action) =>
        Path.Combine(
            Path.GetDirectoryName(targetPath)!,
            $".{Path.GetFileName(targetPath)}.arkive-{action}-{Guid.NewGuid():N}.tmp");

    private static string ResolveWithin(string directory, string relativePath, string code)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var fullPath = Path.GetFullPath(Path.Combine(fullDirectory, relativePath));
        var prefix = fullDirectory.TrimEnd(Path.DirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            Refuse(code, $"A deployment path escapes its controlled root: {relativePath}");
        }
        return fullPath;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new ArkiveModDeploymentRefusalException(code, message);
}
