using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkive.Core.Mods;

public enum ModSetInstallationState
{
    NotInstalled,
    Partial,
    Installed,
    Restored,
    UpdateRequired,
    Conflict,
    MissingTarget,
}

public enum ModSetConflictSeverity { Information, Blocking }

public sealed record ArkiveModSetMember(
    string MemberId,
    string PackageProjectId,
    string PackageProjectManifestPath,
    string PackageAlias,
    string PhysicalPackageName,
    bool Included = true,
    bool TrackLatestRevision = true,
    string? PinnedRevisionId = null);

public sealed record ArkiveModSetManifest(
    int SchemaVersion,
    string ModSetId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string DisplayName,
    string? Notes,
    IReadOnlyList<ArkiveModSetMember> Members)
{
    public bool IsFavorite { get; init; }

    // Utility sets use the same transactional deployment engine for one-click UI actions,
    // but are intentionally hidden from the user's named Mod Sets workspace.
    public bool IsUtility { get; init; }
}

public sealed record ArkiveModSetEntry(
    string ManifestPath,
    string DirectoryPath,
    ArkiveModSetManifest Manifest);

public sealed record ModSetConflict(
    ModSetConflictSeverity Severity,
    string Code,
    string Message,
    IReadOnlyList<string> MemberIds,
    string? TargetPath = null,
    IReadOnlyList<string>? ObjectNames = null);

public sealed record ResolvedModSetMember(
    ArkiveModSetMember Member,
    GuidedTexturePackageEntry PackageProject,
    GuidedTexturePackageRevision Revision,
    ArkiveModDeploymentRequest DeploymentRequest,
    ModDeploymentInspection Deployment);

public sealed record ModSetInspection(
    ArkiveModSetEntry Set,
    IReadOnlyList<ResolvedModSetMember> Members,
    IReadOnlyList<ModSetConflict> Conflicts,
    ModSetInstallationState State,
    bool CanInstall,
    bool CanRestore,
    string Summary);

public sealed record ModSetMemberReceipt(
    string MemberId,
    string ModId,
    string PackageAlias,
    string TargetPath,
    string RequiredSourceSha256,
    string PayloadSha256,
    ModInstallationState State,
    bool ChangedByTransaction);

public sealed record ModSetInstallationReceipt(
    int SchemaVersion,
    string TransactionId,
    string ModSetId,
    string DisplayName,
    ModSetInstallationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? RestoredAt,
    string SetManifestPath,
    IReadOnlyList<ModSetMemberReceipt> Members,
    IReadOnlyList<ModReadinessCheck> Checks);

public sealed record ModSetDeploymentRequest(
    string ModSetManifestPath,
    string BackupRootDirectory,
    string StateRootDirectory);

public sealed record ModSetDeploymentResult(
    ModSetInspection Inspection,
    ModSetInstallationReceipt Receipt,
    int ChangedPackageCount,
    bool RolledBack,
    TimeSpan Duration);

public sealed class ArkiveModSetRefusalException : NotSupportedException
{
    public ArkiveModSetRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

public sealed class ArkiveModSetWorkspace
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "arkive-mod-set.json";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public ArkiveModSetWorkspace(string rootDirectory) =>
        RootDirectory = Path.GetFullPath(rootDirectory);

    public string RootDirectory { get; }

    public async Task<IReadOnlyList<ArkiveModSetEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var entries = new List<ArkiveModSetEntry>();
        foreach (var path in Directory.EnumerateFiles(
                     RootDirectory, ManifestFileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var entry = await LoadManifestAsync(path, cancellationToken).ConfigureAwait(false);
                if (!entry.Manifest.IsUtility) entries.Add(entry);
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or NotSupportedException)
            {
                // A damaged set is isolated from healthy set manifests.
            }
        }
        return entries.OrderByDescending(entry => entry.Manifest.UpdatedAt).ToArray();
    }

    public async Task<ArkiveModSetEntry> CreateAsync(
        string displayName,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A mod-set name is required.", nameof(displayName));
        Directory.CreateDirectory(RootDirectory);
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(RootDirectory, $"{Sanitize(displayName)}-{id[..8]}");
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        var manifest = new ArkiveModSetManifest(
            CurrentSchemaVersion, id, now, now, displayName.Trim(),
            string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), []);
        var path = Path.Combine(directory, ManifestFileName);
        await SaveAtomicAsync(path, manifest, cancellationToken).ConfigureAwait(false);
        return new ArkiveModSetEntry(path, directory, manifest);
    }

    public async Task<ArkiveModSetEntry> LoadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<ArkiveModSetManifest>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                       throw new JsonException("The Arkive mod-set manifest was empty.");
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Mod-set schema {manifest.SchemaVersion} is unsupported; Arkive requires {CurrentSchemaVersion}.");
        return new ArkiveModSetEntry(fullPath, Path.GetDirectoryName(fullPath)!, manifest);
    }

    public async Task<ArkiveModSetEntry> UpdateMetadataAsync(
        string manifestPath,
        string displayName,
        string? notes,
        bool? favorite = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A mod-set name is required.", nameof(displayName));
        var entry = await LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var updated = entry.Manifest with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            DisplayName = displayName.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            IsFavorite = favorite ?? entry.Manifest.IsFavorite,
        };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public async Task<ArkiveModSetEntry> SetPackageAsync(
        string manifestPath,
        GuidedTexturePackageEntry package,
        bool included,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var entry = await LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var existing = entry.Manifest.Members.FirstOrDefault(member =>
            member.PackageProjectId.Equals(
                package.Manifest.PackageProjectId, StringComparison.OrdinalIgnoreCase));
        var member = existing is null
            ? new ArkiveModSetMember(
                Guid.NewGuid().ToString("N"), package.Manifest.PackageProjectId,
                Path.GetFullPath(package.ManifestPath), package.Manifest.PackageAlias,
                package.Manifest.PhysicalPackageName, included)
            : existing with
            {
                Included = included,
                PackageProjectManifestPath = Path.GetFullPath(package.ManifestPath),
                PackageAlias = package.Manifest.PackageAlias,
                PhysicalPackageName = package.Manifest.PhysicalPackageName,
            };
        var members = entry.Manifest.Members
            .Where(item => !item.PackageProjectId.Equals(
                package.Manifest.PackageProjectId, StringComparison.OrdinalIgnoreCase))
            .Append(member)
            .OrderBy(item => item.PackageAlias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var updated = entry.Manifest with { UpdatedAt = DateTimeOffset.UtcNow, Members = members };
        await SaveAtomicAsync(entry.ManifestPath, updated, cancellationToken).ConfigureAwait(false);
        return entry with { Manifest = updated };
    }

    public async Task<ArkiveModSetEntry> UpsertUtilitySetAsync(
        string utilityId,
        string displayName,
        IReadOnlyList<GuidedTexturePackageEntry> packages,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(utilityId))
            throw new ArgumentException("A utility-set identifier is required.", nameof(utilityId));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A utility-set display name is required.", nameof(displayName));
        ArgumentNullException.ThrowIfNull(packages);

        var distinctPackages = packages
            .GroupBy(package => package.Manifest.PackageProjectId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(package => package.Manifest.PackageAlias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctPackages.Length == 0)
            throw new ArgumentException("A utility set must contain at least one package project.", nameof(packages));

        Directory.CreateDirectory(RootDirectory);
        var safeId = Sanitize(utilityId.Trim());
        var directory = Path.Combine(RootDirectory, "_quick-actions", safeId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ManifestFileName);
        var now = DateTimeOffset.UtcNow;
        var createdAt = now;
        if (File.Exists(path))
        {
            try
            {
                createdAt = (await LoadManifestAsync(path, cancellationToken).ConfigureAwait(false))
                    .Manifest.CreatedAt;
            }
            catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
            {
                // A damaged hidden quick-action manifest can be safely replaced. Package projects,
                // builds, backups and installation receipts are stored outside this directory.
            }
        }

        var members = distinctPackages.Select(package => new ArkiveModSetMember(
            package.Manifest.PackageProjectId,
            package.Manifest.PackageProjectId,
            Path.GetFullPath(package.ManifestPath),
            package.Manifest.PackageAlias,
            package.Manifest.PhysicalPackageName,
            Included: true)).ToArray();
        var manifest = new ArkiveModSetManifest(
            CurrentSchemaVersion,
            $"utility-{safeId}",
            createdAt,
            now,
            displayName.Trim(),
            "Arkive-managed quick action. Hidden from the named Mod Sets workspace.",
            members)
        {
            IsUtility = true,
        };
        await SaveAtomicAsync(path, manifest, cancellationToken).ConfigureAwait(false);
        return new ArkiveModSetEntry(path, directory, manifest);
    }

    public Task DeleteAsync(ArkiveModSetEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = RequireWithinRoot(entry.DirectoryPath);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    internal static JsonSerializerOptions CreateJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static async Task SaveAtomicAsync<T>(
        string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(
                         temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private string RequireWithinRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(RootDirectory, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("The mod-set directory is outside Arkive's workspace.");
        return fullPath;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "mod-set" : safe;
    }
}

public sealed class ArkiveModSetService
{
    public const int CurrentReceiptSchemaVersion = 1;
    public const string ReceiptFileName = "arkive-mod-set-installation.json";

    private static readonly JsonSerializerOptions JsonOptions = ArkiveModSetWorkspace.CreateJsonOptions();
    private readonly ArkiveModSetWorkspace _workspace;
    private readonly GuidedTexturePackageWorkspace _packageWorkspace;
    private readonly ArkiveModDeploymentService _deployment;

    public ArkiveModSetService(
        ArkiveModSetWorkspace workspace,
        GuidedTexturePackageWorkspace packageWorkspace,
        ArkiveModDeploymentService deployment)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _packageWorkspace = packageWorkspace ?? throw new ArgumentNullException(nameof(packageWorkspace));
        _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
    }

    public async Task<ModSetInspection> InspectAsync(
        ModSetDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var set = await _workspace.LoadManifestAsync(
            request.ModSetManifestPath, cancellationToken).ConfigureAwait(false);
        var resolved = new List<ResolvedModSetMember>();
        var conflicts = new List<ModSetConflict>();
        foreach (var member in set.Manifest.Members.Where(member => member.Included))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(member.PackageProjectManifestPath))
            {
                conflicts.Add(Block(
                    "SET001", $"Package project manifest is missing for {member.PackageAlias}.", member));
                continue;
            }
            GuidedTexturePackageEntry package;
            try
            {
                package = await _packageWorkspace.LoadManifestAsync(
                    member.PackageProjectManifestPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or NotSupportedException)
            {
                conflicts.Add(Block("SET002",
                    $"Package project {member.PackageAlias} could not be loaded: {exception.Message}", member));
                continue;
            }
            var revision = ResolveRevision(member, package);
            if (revision is null)
            {
                conflicts.Add(Block("SET003",
                    $"{member.PackageAlias} has no built revision. Rebuild its checked assets first.", member));
                continue;
            }
            if (!File.Exists(revision.ModBundleManifestPath))
            {
                conflicts.Add(Block("SET004",
                    $"The selected build for {member.PackageAlias} has no mod bundle.", member));
                continue;
            }
            var deploymentRequest = new ArkiveModDeploymentRequest(
                revision.ModBundleManifestPath,
                Path.GetFullPath(request.BackupRootDirectory),
                Path.GetFullPath(request.StateRootDirectory));
            try
            {
                var inspection = await _deployment.InspectAsync(
                    deploymentRequest, cancellationToken).ConfigureAwait(false);
                resolved.Add(new ResolvedModSetMember(
                    member, package, revision, deploymentRequest, inspection));
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or NotSupportedException or ArgumentException)
            {
                conflicts.Add(Block("SET005",
                    $"{member.PackageAlias} failed bundle inspection: {exception.Message}", member));
            }
        }

        foreach (var group in resolved.GroupBy(item =>
                     Path.GetFullPath(item.Deployment.TargetPath), StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToArray();
            if (members.Length <= 1) continue;
            var payloads = members.Select(item => item.Deployment.Bundle.PayloadSha256)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var overlappingObjects = members.SelectMany(item => item.Deployment.Bundle.Assets)
                .GroupBy(asset => $"{asset.ClassName}:{asset.ObjectName}", StringComparer.OrdinalIgnoreCase)
                .Where(objects => objects.Count() > 1)
                .Select(objects => objects.First().ObjectName)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            conflicts.Add(new ModSetConflict(
                payloads.Length == 1 ? ModSetConflictSeverity.Information : ModSetConflictSeverity.Blocking,
                payloads.Length == 1 ? "SET_DUPLICATE_PAYLOAD" :
                    overlappingObjects.Length > 0 ? "SET_OBJECT_CONFLICT" : "SET_PACKAGE_CONFLICT",
                payloads.Length == 1
                    ? $"{members.Length} members resolve to the same payload; Arkive will install it once."
                    : overlappingObjects.Length > 0
                        ? $"Multiple members replace the same UPK and overlap on: {string.Join(", ", overlappingObjects)}. Recompose those assets in one package project."
                        : "Multiple members replace the same physical UPK with different payloads. Recompose them in one package project.",
                members.Select(item => item.Member.MemberId).ToArray(), group.Key, overlappingObjects));
        }

        foreach (var member in resolved)
        {
            var deployment = member.Deployment;
            if (deployment.State == ModInstallationState.MissingTarget)
                conflicts.Add(Block("SET_TARGET_MISSING", deployment.Summary, member.Member, deployment.TargetPath));
            else if (deployment.State == ModInstallationState.Conflict)
                conflicts.Add(Block("SET_CLIENT_CHANGED", deployment.Summary, member.Member, deployment.TargetPath));
        }

        var blocking = conflicts.Any(item => item.Severity == ModSetConflictSeverity.Blocking);
        var expectedCount = set.Manifest.Members.Count(member => member.Included);
        var complete = expectedCount > 0 && resolved.Count == expectedCount;
        var distinctMembers = resolved
            .GroupBy(item => Path.GetFullPath(item.Deployment.TargetPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
        var installed = distinctMembers.Count(item => item.Deployment.State is
            ModInstallationState.Installed or ModInstallationState.Prepared);
        var restorable = distinctMembers.Count(item => item.Deployment.CanRestore);
        var state = blocking
            ? conflicts.Any(item => item.Code == "SET_TARGET_MISSING")
                ? ModSetInstallationState.MissingTarget
                : conflicts.Any(item => item.Code == "SET_CLIENT_CHANGED")
                    ? ModSetInstallationState.UpdateRequired
                    : ModSetInstallationState.Conflict
            : installed == 0
                ? distinctMembers.Any(item => item.Deployment.State == ModInstallationState.Restored)
                    ? ModSetInstallationState.Restored
                    : ModSetInstallationState.NotInstalled
                : installed == distinctMembers.Length
                    ? ModSetInstallationState.Installed
                    : ModSetInstallationState.Partial;
        var canInstall = complete && !blocking && distinctMembers.All(item =>
            item.Deployment.State is ModInstallationState.Installed or ModInstallationState.Prepared ||
            item.Deployment.CanInstall);
        var canRestore = complete && !blocking && restorable > 0 && distinctMembers.All(item =>
            item.Deployment.CanRestore || item.Deployment.State is
                ModInstallationState.NotInstalled or ModInstallationState.Restored);
        var summary = expectedCount == 0
            ? "This Mod Set contains no checked package projects."
            : blocking
                ? conflicts.First(item => item.Severity == ModSetConflictSeverity.Blocking).Message
                : state switch
                {
                    ModSetInstallationState.Installed =>
                        $"All {distinctMembers.Length:N0} package payloads are installed.",
                    ModSetInstallationState.Partial =>
                        $"{installed:N0} of {distinctMembers.Length:N0} package payloads are installed; Arkive can complete or restore the set.",
                    ModSetInstallationState.Restored =>
                        $"All {distinctMembers.Length:N0} package projects are restored and ready.",
                    _ => $"All {distinctMembers.Length:N0} package projects passed conflict and client checks.",
                };
        return new ModSetInspection(
            set, resolved, conflicts, state, canInstall, canRestore, summary);
    }

    public async Task<ModSetDeploymentResult> InstallAsync(
        ModSetDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        await using var setLock = AcquireSetLock(request.StateRootDirectory);
        var inspection = await InspectAsync(request, cancellationToken).ConfigureAwait(false);
        if (!inspection.CanInstall) Refuse("SET006", inspection.Summary);
        var members = DistinctTargets(inspection.Members);
        var changed = new List<ResolvedModSetMember>();
        var now = DateTimeOffset.UtcNow;
        var receipt = CreateReceipt(inspection, ModSetInstallationState.Partial, now,
            "SET_INSTALL_PREFLIGHT", "Every package, payload, target, conflict and backup path passed preflight.");
        var receiptPath = ReceiptPath(request.StateRootDirectory, inspection.Set.Manifest.ModSetId);
        await SaveReceiptAsync(receiptPath, receipt, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (member.Deployment.State == ModInstallationState.Installed) continue;
                var result = await _deployment.InstallAsync(
                    member.DeploymentRequest, cancellationToken).ConfigureAwait(false);
                if (result.ChangedTarget) changed.Add(member);
                receipt = UpdateMemberReceipt(receipt, member.Member.MemberId,
                    result.Inspection.State, result.ChangedTarget) with { UpdatedAt = DateTimeOffset.UtcNow };
                await SaveReceiptAsync(receiptPath, receipt, cancellationToken).ConfigureAwait(false);
            }
            var final = await InspectAsync(request, cancellationToken).ConfigureAwait(false);
            if (final.State != ModSetInstallationState.Installed)
                Refuse("SET007", $"Post-install verification returned {final.State}: {final.Summary}");
            now = DateTimeOffset.UtcNow;
            receipt = receipt with
            {
                State = ModSetInstallationState.Installed,
                UpdatedAt = now,
                InstalledAt = now,
                Members = MemberReceipts(final.Members, changed.Select(item => item.Member.MemberId)),
                Checks = Append(receipt.Checks, "SET_INSTALL_COMMITTED",
                    $"Atomically installed and reverified {members.Count:N0} physical package payloads."),
            };
            await SaveReceiptAsync(receiptPath, receipt, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            return new ModSetDeploymentResult(final, receipt, changed.Count, false, watch.Elapsed);
        }
        catch
        {
            var rollbackErrors = new List<string>();
            foreach (var member in changed.AsEnumerable().Reverse())
            {
                try
                {
                    await _deployment.RestoreAsync(
                        member.DeploymentRequest, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception rollback) when (
                    rollback is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    rollbackErrors.Add($"{member.Member.PackageAlias}: {rollback.Message}");
                }
            }
            var rolledBack = rollbackErrors.Count == 0;
            receipt = receipt with
            {
                State = rolledBack ? ModSetInstallationState.Restored : ModSetInstallationState.Conflict,
                UpdatedAt = DateTimeOffset.UtcNow,
                RestoredAt = rolledBack ? DateTimeOffset.UtcNow : null,
                Checks = Append(receipt.Checks,
                    rolledBack ? "SET_INSTALL_ROLLED_BACK" : "SET_INSTALL_ROLLBACK_FAILED",
                    rolledBack
                        ? "A member installation failed; every package changed by this transaction was restored."
                        : $"Rollback needs attention: {string.Join(" | ", rollbackErrors)}"),
            };
            await SaveReceiptAsync(receiptPath, receipt, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ModSetDeploymentResult> RestoreAsync(
        ModSetDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        await using var setLock = AcquireSetLock(request.StateRootDirectory);
        var inspection = await InspectAsync(request, cancellationToken).ConfigureAwait(false);
        if (!inspection.CanRestore) Refuse("SET008", inspection.Summary);
        var members = DistinctTargets(inspection.Members);
        var restored = new List<ResolvedModSetMember>();
        try
        {
            foreach (var member in members.AsEnumerable().Reverse())
            {
                if (!member.Deployment.CanRestore) continue;
                var result = await _deployment.RestoreAsync(
                    member.DeploymentRequest, cancellationToken).ConfigureAwait(false);
                if (result.ChangedTarget) restored.Add(member);
            }
            var final = await InspectAsync(request, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var receipt = CreateReceipt(final, ModSetInstallationState.Restored, now,
                "SET_RESTORE_COMMITTED",
                $"Restored and reverified {restored.Count:N0} physical package payloads.") with
            {
                RestoredAt = now,
            };
            await SaveReceiptAsync(
                ReceiptPath(request.StateRootDirectory, final.Set.Manifest.ModSetId),
                receipt, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            return new ModSetDeploymentResult(final, receipt, restored.Count, false, watch.Elapsed);
        }
        catch
        {
            var recoveryErrors = new List<string>();
            foreach (var member in restored.AsEnumerable().Reverse())
            {
                try
                {
                    await _deployment.InstallAsync(
                        member.DeploymentRequest, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception recovery) when (
                    recovery is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    recoveryErrors.Add($"{member.Member.PackageAlias}: {recovery.Message}");
                }
            }
            if (recoveryErrors.Count > 0)
                Refuse("SET009",
                    $"Set restore and recovery both need attention: {string.Join(" | ", recoveryErrors)}");
            throw;
        }
    }

    public static async Task<ModSetInstallationReceipt?> LoadReceiptAsync(
        string stateRootDirectory,
        string modSetId,
        CancellationToken cancellationToken = default)
    {
        var path = ReceiptPath(stateRootDirectory, modSetId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var receipt = await JsonSerializer.DeserializeAsync<ModSetInstallationReceipt>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (receipt is not null && receipt.SchemaVersion != CurrentReceiptSchemaVersion)
            throw new NotSupportedException(
                $"Mod-set receipt schema {receipt.SchemaVersion} is unsupported.");
        return receipt;
    }

    private static GuidedTexturePackageRevision? ResolveRevision(
        ArkiveModSetMember member, GuidedTexturePackageEntry package)
    {
        var id = member.TrackLatestRevision
            ? package.Manifest.LatestRevisionId
            : member.PinnedRevisionId;
        if (string.IsNullOrWhiteSpace(id)) return package.Manifest.Revisions.LastOrDefault();
        return package.Manifest.Revisions.FirstOrDefault(revision =>
            revision.RevisionId.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ResolvedModSetMember> DistinctTargets(
        IReadOnlyList<ResolvedModSetMember> members) => members
        .GroupBy(member => Path.GetFullPath(member.Deployment.TargetPath),
            StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First()).ToArray();

    private static ModSetConflict Block(
        string code, string message, ArkiveModSetMember member, string? target = null) =>
        new(ModSetConflictSeverity.Blocking, code, message, [member.MemberId], target);

    private static ModSetInstallationReceipt CreateReceipt(
        ModSetInspection inspection,
        ModSetInstallationState state,
        DateTimeOffset now,
        string code,
        string message) => new(
        CurrentReceiptSchemaVersion, Guid.NewGuid().ToString("N"),
        inspection.Set.Manifest.ModSetId, inspection.Set.Manifest.DisplayName,
        state, now, now, null, null, inspection.Set.ManifestPath,
        MemberReceipts(inspection.Members, []),
        [new ModReadinessCheck(code, ModReadinessCheckStatus.Passed, message)]);

    private static IReadOnlyList<ModSetMemberReceipt> MemberReceipts(
        IEnumerable<ResolvedModSetMember> members,
        IEnumerable<string> changedIds)
    {
        var changed = changedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return DistinctTargets(members.ToArray()).Select(item => new ModSetMemberReceipt(
            item.Member.MemberId, item.Deployment.Bundle.ModId, item.Member.PackageAlias,
            item.Deployment.TargetPath, item.Deployment.Bundle.RequiredSourcePackageSha256,
            item.Deployment.Bundle.PayloadSha256, item.Deployment.State,
            changed.Contains(item.Member.MemberId))).ToArray();
    }

    private static ModSetInstallationReceipt UpdateMemberReceipt(
        ModSetInstallationReceipt receipt,
        string memberId,
        ModInstallationState state,
        bool changed)
    {
        var members = receipt.Members.Select(member =>
            member.MemberId.Equals(memberId, StringComparison.OrdinalIgnoreCase)
                ? member with { State = state, ChangedByTransaction = member.ChangedByTransaction || changed }
                : member).ToArray();
        return receipt with { Members = members };
    }

    private static IReadOnlyList<ModReadinessCheck> Append(
        IReadOnlyList<ModReadinessCheck> checks, string code, string message) =>
        checks.Concat([
            new ModReadinessCheck(code, ModReadinessCheckStatus.Passed, message),
        ]).ToArray();

    private static async Task SaveReceiptAsync(
        string path,
        ModSetInstallationReceipt receipt,
        CancellationToken cancellationToken) =>
        await ArkiveModSetWorkspace.SaveAtomicAsync(path, receipt, cancellationToken)
            .ConfigureAwait(false);

    private static string ReceiptPath(string stateRootDirectory, string modSetId) =>
        Path.Combine(Path.GetFullPath(stateRootDirectory), "sets", modSetId, ReceiptFileName);

    private static FileStream AcquireSetLock(string stateRootDirectory)
    {
        var directory = Path.Combine(Path.GetFullPath(stateRootDirectory), "sets");
        Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(Path.Combine(directory, ".deployment.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            Refuse("SET010", $"Another Mod Set operation is already running: {exception.Message}");
            throw;
        }
    }

    private static void Refuse(string code, string message) =>
        throw new ArkiveModSetRefusalException(code, message);
}
