using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Meshes;
using Arkive.Core.Materials;
using Arkive.Core.StaticMeshes;

namespace Arkive.Core.Mods;

public enum ClientUpdateAuditState
{
    NoModifiedPackages,
    Compatible,
    RebaseAvailable,
    ReviewRequired,
    RestoreRequired,
    UnsupportedClientFormat,
}

public enum ClientInstallationTargetState
{
    ActivePayloadRestoreAvailable,
    ActivePayloadBackupUnavailable,
    CleanOriginal,
    ClientChanged,
    MissingTarget,
    InvalidReceipt,
}

public sealed record ClientUpdatePackageInput(
    GuidedTexturePackageEntry Package,
    IReadOnlyList<GuidedTextureModProjectEntry> Textures,
    IReadOnlyList<SkeletalMeshEditWorkspaceEntry> Meshes)
{
    public IReadOnlyList<MaterialInstanceEditEntry> Materials { get; init; } = [];

    public IReadOnlyList<StaticMeshEditEntry> StaticMeshes { get; init; } = [];
}

public sealed record ClientInstallationTargetStatus(
    ClientInstallationTargetState State,
    string TargetPath,
    string DisplayName,
    string? CurrentTargetSha256,
    string? MatchingPayloadSha256,
    string? MatchingOriginalSha256,
    string? BackupPath,
    bool BackupExists,
    bool BackupVerified,
    IReadOnlyList<string> ReceiptPaths,
    string Message);

public sealed record ClientUpdateAuditProgress(
    int CompletedPackages,
    int TotalPackages,
    string PackageName,
    string Message);

public sealed record ClientUpdateAuditReport(
    int SchemaVersion,
    string AuditId,
    DateTimeOffset CheckedAt,
    string ProtectionProvider,
    ClientUpdateAuditState State,
    bool RequiresRestoreBeforeUpdateCheck,
    bool EveryAffectedPackageFormatCompatible,
    bool CanBulkRebase,
    int UniquePackageCount,
    IReadOnlyList<ClientPackageCompatibilityReport> Packages,
    IReadOnlyList<ClientPackageFormatProbe> AdditionalPackageProbes,
    IReadOnlyList<ClientInstallationTargetStatus> InstallationTargets,
    string Summary,
    double DurationSeconds)
{
    public int ExactPackageCount => Packages.Count(package =>
        package.State == ClientPackageCompatibilityState.ExactSource);

    public int SafeRebasePackageCount => Packages.Count(package =>
        package.State == ClientPackageCompatibilityState.SafeAutomaticRebase);

    public int ReviewPackageCount => Packages.Count(package => package.State is
        ClientPackageCompatibilityState.ReviewRequired or
        ClientPackageCompatibilityState.MissingPackage or
        ClientPackageCompatibilityState.InvalidPackage);
}

/// <summary>
/// Audits every package represented by Arkive projects and every target retained
/// in a deployment receipt. Active payloads are detected before clean-client
/// compatibility is inferred. A deep audit decodes every compressed chunk and
/// requires both read and reconstruction support for the current profile.
/// </summary>
public sealed class ClientUpdateAuditService
{
    public const int CurrentSchemaVersion = 1;
    private readonly ClientUpdateCompatibilityService _compatibility;

    public ClientUpdateAuditService(ClientUpdateCompatibilityService compatibility) =>
        _compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility));

    public async Task<ClientUpdateAuditReport> InspectAllAsync(
        IReadOnlyList<ClientUpdatePackageInput> packageInputs,
        string installationStateRoot,
        IProgress<ClientUpdateAuditProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageInputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationStateRoot);
        var started = DateTimeOffset.UtcNow;
        var inputs = packageInputs
            .GroupBy(input => Path.GetFullPath(input.Package.Manifest.SourcePackagePath),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new ClientUpdatePackageInput(
                group.First().Package,
                group.SelectMany(input => input.Textures)
                    .GroupBy(texture => texture.Manifest.ProjectId, StringComparer.OrdinalIgnoreCase)
                    .Select(items => items.First()).ToArray(),
                group.SelectMany(input => input.Meshes)
                    .GroupBy(mesh => mesh.Manifest.EditId, StringComparer.OrdinalIgnoreCase)
                    .Select(items => items.First()).ToArray())
            {
                Materials = group.SelectMany(input => input.Materials)
                    .GroupBy(material => material.Document.EditId, StringComparer.OrdinalIgnoreCase)
                    .Select(items => items.First()).ToArray(),
                StaticMeshes = group.SelectMany(input => input.StaticMeshes)
                    .GroupBy(mesh => mesh.Manifest.EditId, StringComparer.OrdinalIgnoreCase)
                    .Select(items => items.First()).ToArray(),
            })
            .OrderBy(input => input.Package.Manifest.PackageAlias, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var installationTargets = await InspectInstallationTargetsAsync(
            installationStateRoot, cancellationToken).ConfigureAwait(false);
        var reports = new List<ClientPackageCompatibilityReport>(inputs.Length);
        for (var index = 0; index < inputs.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = inputs[index];
            progress?.Report(new ClientUpdateAuditProgress(
                index, inputs.Length, input.Package.Manifest.PackageAlias,
                "Hashing, validating tables, and decoding every compressed package chunk..."));
            var report = await _compatibility.InspectAsync(
                input.Package, input.Textures, input.Meshes,
                input.Materials, input.StaticMeshes, deepFormatProbe: true, cancellationToken)
                .ConfigureAwait(false);
            if (report.FormatProbe is null &&
                report.State != ClientPackageCompatibilityState.InstalledPayload)
            {
                var probe = await _compatibility.ProbeFormatAsync(
                    input.Package.Manifest.SourcePackagePath, deepFormatProbe: true,
                    cancellationToken).ConfigureAwait(false);
                report = report with { FormatProbe = probe };
            }
            reports.Add(report);
            progress?.Report(new ClientUpdateAuditProgress(
                index + 1, inputs.Length, input.Package.Manifest.PackageAlias,
                report.Summary));
        }

        var knownPaths = reports.Select(report => Path.GetFullPath(report.SourcePackagePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additional = new List<ClientPackageFormatProbe>();
        foreach (var target in installationTargets.Where(target =>
                     target.State is ClientInstallationTargetState.CleanOriginal or
                         ClientInstallationTargetState.ClientChanged &&
                     !string.IsNullOrWhiteSpace(target.TargetPath) &&
                     !knownPaths.Contains(Path.GetFullPath(target.TargetPath))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            additional.Add(await _compatibility.ProbeFormatAsync(
                target.TargetPath, deepFormatProbe: true, cancellationToken).ConfigureAwait(false));
        }

        var requiresRestore = reports.Any(report =>
                                  report.State == ClientPackageCompatibilityState.InstalledPayload) ||
                              installationTargets.Any(target => target.State is
                                  ClientInstallationTargetState.ActivePayloadRestoreAvailable or
                                  ClientInstallationTargetState.ActivePayloadBackupUnavailable);
        var formatCompatible = reports.All(report =>
                                   report.State == ClientPackageCompatibilityState.InstalledPayload ||
                                   report.FormatProbe is { CanRead: true, CanReconstruct: true }) &&
                               additional.All(probe => probe.CanRead && probe.CanReconstruct);
        var hasInvalidReceipt = installationTargets.Any(target =>
            target.State == ClientInstallationTargetState.InvalidReceipt);
        var review = reports.Any(report => report.State is
                         ClientPackageCompatibilityState.ReviewRequired or
                         ClientPackageCompatibilityState.MissingPackage or
                         ClientPackageCompatibilityState.InvalidPackage) ||
                     installationTargets.Any(target => target.State is
                         ClientInstallationTargetState.ActivePayloadBackupUnavailable or
                         ClientInstallationTargetState.MissingTarget) ||
                     hasInvalidReceipt;
        var safeCount = reports.Count(report =>
            report.State == ClientPackageCompatibilityState.SafeAutomaticRebase);
        var state = inputs.Length == 0 && installationTargets.Count == 0
            ? ClientUpdateAuditState.NoModifiedPackages
            : requiresRestore
                ? ClientUpdateAuditState.RestoreRequired
                : !formatCompatible
                    ? ClientUpdateAuditState.UnsupportedClientFormat
                    : review
                        ? ClientUpdateAuditState.ReviewRequired
                        : safeCount > 0
                            ? ClientUpdateAuditState.RebaseAvailable
                            : ClientUpdateAuditState.Compatible;
        var canBulkRebase = !requiresRestore && formatCompatible && safeCount > 0;
        var summary = state switch
        {
            ClientUpdateAuditState.NoModifiedPackages =>
                "No Arkive package projects or installation receipts were found.",
            ClientUpdateAuditState.RestoreRequired =>
                "One or more Arkive payloads are still installed. Restore every affected original package before assessing the updated client.",
            ClientUpdateAuditState.UnsupportedClientFormat =>
                "At least one affected UPK failed the Western package, compression, protection, or full chunk-decode probe. Rebuilding and installation are unsafe until Arkive is updated or the client is repaired.",
            ClientUpdateAuditState.ReviewRequired =>
                $"The client format is readable, but {reports.Count(report => report.State is ClientPackageCompatibilityState.ReviewRequired or ClientPackageCompatibilityState.MissingPackage or ClientPackageCompatibilityState.InvalidPackage):N0} package project(s) or retained receipts require review.",
            ClientUpdateAuditState.RebaseAvailable =>
                $"All affected package formats passed. {safeCount:N0} changed package project(s) contain byte-identical or semantically unchanged selected assets and can be safely rebased.",
            _ =>
                $"All {reports.Count:N0} package project(s) match their recorded clean client sources and passed the full format probe.",
        };
        return new ClientUpdateAuditReport(
            CurrentSchemaVersion, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            reports.Select(report => report.FormatProbe?.ProtectionProvider)
                .Concat(additional.Select(probe => probe.ProtectionProvider))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Unknown",
            state, requiresRestore, formatCompatible, canBulkRebase,
            knownPaths.Concat(installationTargets
                    .Where(target => !string.IsNullOrWhiteSpace(target.TargetPath))
                    .Select(target => Path.GetFullPath(target.TargetPath)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            reports.ToArray(), additional.ToArray(), installationTargets,
            summary, (DateTimeOffset.UtcNow - started).TotalSeconds);
    }

    private static async Task<IReadOnlyList<ClientInstallationTargetStatus>> InspectInstallationTargetsAsync(
        string stateRoot,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(stateRoot);
        if (!Directory.Exists(root)) return [];
        var receipts = new List<(string Path, ModInstallationReceipt Receipt)>();
        var invalid = new List<ClientInstallationTargetStatus>();
        foreach (var path in Directory.EnumerateFiles(
                     root, ArkiveModDeploymentService.ReceiptFileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                receipts.Add((Path.GetFullPath(path),
                    await ArkiveModDeploymentService.LoadReceiptAsync(path, cancellationToken)
                        .ConfigureAwait(false)));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or
                    InvalidDataException or NotSupportedException or ArgumentException)
            {
                invalid.Add(new ClientInstallationTargetStatus(
                    ClientInstallationTargetState.InvalidReceipt, string.Empty,
                    "Unreadable Arkive installation receipt", null, null, null, null,
                    false, false, [Path.GetFullPath(path)],
                    $"The retained installation receipt could not be validated: {exception.Message}"));
            }
        }

        var results = new List<ClientInstallationTargetStatus>(invalid);
        foreach (var group in receipts.GroupBy(item => Path.GetFullPath(item.Receipt.TargetPath),
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = group.Key;
            var candidates = group.OrderByDescending(item => item.Receipt.UpdatedAt).ToArray();
            if (!File.Exists(targetPath))
            {
                results.Add(Status(ClientInstallationTargetState.MissingTarget, null, null, null,
                    null, false, false,
                    "A package retained in Arkive installation history is missing from the client."));
                continue;
            }
            string current;
            try { current = await HashFileAsync(targetPath, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                results.Add(Status(ClientInstallationTargetState.ClientChanged, null, null, null,
                    null, false, false, $"The package could not be hashed safely: {exception.Message}"));
                continue;
            }
            var active = candidates.FirstOrDefault(item => current.Equals(
                item.Receipt.PayloadSha256, StringComparison.OrdinalIgnoreCase));
            if (active.Receipt is not null)
            {
                var backupExists = File.Exists(active.Receipt.BackupPath);
                var backupVerified = false;
                if (backupExists)
                {
                    try
                    {
                        var info = new FileInfo(active.Receipt.BackupPath);
                        var hash = await HashFileAsync(active.Receipt.BackupPath, cancellationToken)
                            .ConfigureAwait(false);
                        backupVerified = info.Length == active.Receipt.OriginalBytes &&
                                         hash.Equals(active.Receipt.OriginalSha256,
                                             StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        backupVerified = false;
                    }
                }
                results.Add(Status(
                    backupVerified
                        ? ClientInstallationTargetState.ActivePayloadRestoreAvailable
                        : ClientInstallationTargetState.ActivePayloadBackupUnavailable,
                    current, active.Receipt.PayloadSha256, active.Receipt.OriginalSha256,
                    active.Receipt.BackupPath, backupExists, backupVerified,
                    backupVerified
                        ? "An Arkive payload is installed and its exact original backup is verified. Restore it before the game update or compatibility scan."
                        : "An Arkive payload is installed but its exact original backup is unavailable or invalid. Do not update, rebuild, or overwrite this package automatically."));
                continue;
            }
            var clean = candidates.FirstOrDefault(item => current.Equals(
                item.Receipt.OriginalSha256, StringComparison.OrdinalIgnoreCase));
            results.Add(clean.Receipt is not null
                ? Status(ClientInstallationTargetState.CleanOriginal, current, null,
                    clean.Receipt.OriginalSha256, clean.Receipt.BackupPath,
                    File.Exists(clean.Receipt.BackupPath), true,
                    "The target matches a clean original recorded by Arkive.")
                : Status(ClientInstallationTargetState.ClientChanged, current, null, null,
                    null, false, false,
                    "The target is not an active Arkive payload and differs from historical originals, which is expected after a client update; its package format is probed separately."));

            ClientInstallationTargetStatus Status(
                ClientInstallationTargetState state, string? currentHash,
                string? payloadHash, string? originalHash, string? backupPath,
                bool backupExists, bool backupVerified, string message) => new(
                state, targetPath, candidates[0].Receipt.DisplayName, currentHash,
                payloadHash, originalHash, backupPath, backupExists, backupVerified,
                candidates.Select(item => item.Path).ToArray(), message);
        }
        return results.OrderBy(status => status.TargetPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false));
    }
}

public sealed record ClientUpdateAuditEntry(
    string DirectoryPath,
    string ManifestPath,
    ClientUpdateAuditReport Report);

public sealed class ClientUpdateAuditWorkspaceStore
{
    public const string ManifestFileName = "arkive-client-update-audit.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ClientUpdateAuditWorkspaceStore(string rootDirectory) =>
        RootDirectory = Path.GetFullPath(rootDirectory);

    public string RootDirectory { get; }

    public async Task<ClientUpdateAuditEntry> SaveAsync(
        ClientUpdateAuditReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var directory = Path.Combine(RootDirectory,
            $"{report.CheckedAt:yyyyMMdd-HHmmssfff}-{report.AuditId[..8]}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ManifestFileName);
        await SaveAtomicAsync(path, report, cancellationToken).ConfigureAwait(false);
        return new ClientUpdateAuditEntry(directory, path, report);
    }

    public async Task<ClientUpdateAuditEntry?> LoadLatestAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return null;
        ClientUpdateAuditEntry? latest = null;
        foreach (var path in Directory.EnumerateFiles(
                     RootDirectory, ManifestFileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var report = await JsonSerializer.DeserializeAsync<ClientUpdateAuditReport>(
                                 stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                             throw new InvalidDataException("The client-update audit was empty.");
                if (report.SchemaVersion != ClientUpdateAuditService.CurrentSchemaVersion)
                    continue;
                var entry = new ClientUpdateAuditEntry(
                    Path.GetDirectoryName(path)!, Path.GetFullPath(path), report);
                if (latest is null || entry.Report.CheckedAt > latest.Report.CheckedAt) latest = entry;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or
                    InvalidDataException or NotSupportedException)
            {
                // A damaged old report must not hide the latest healthy audit.
            }
        }
        return latest;
    }

    private static async Task SaveAtomicAsync<T>(
        string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
