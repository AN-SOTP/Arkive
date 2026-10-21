using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Inspection;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.Packages;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;
using Arkive.Core.Validation;

namespace Arkive.Core.Mods;

public enum ClientPackageCompatibilityState
{
    ExactSource,
    InstalledPayload,
    SafeAutomaticRebase,
    ReviewRequired,
    MissingPackage,
    InvalidPackage,
}

public enum ClientAssetCompatibilityState
{
    Unchanged,
    Changed,
    Missing,
    Ambiguous,
    Unreadable,
}

public enum ClientPackageFormatState
{
    Compatible,
    InstalledPayloadNotProbed,
    MissingPackage,
    InvalidSummary,
    StructuralErrors,
    UnsupportedProfile,
    ProtectionUnavailable,
    DecodeFailed,
}

public enum ClientCompatibilityCheckSeverity { Information, Warning, Blocking }

public sealed record ClientCompatibilityCheck(
    ClientCompatibilityCheckSeverity Severity,
    string Code,
    string Message);

public sealed record ClientPackageFormatProbe(
    ClientPackageFormatState State,
    string SourcePackagePath,
    string ProtectionProvider,
    long? PhysicalBytes,
    string? PackageSha256,
    int? FileVersion,
    int? LicenseeVersion,
    uint? CompressionFlags,
    int CompressedChunkCount,
    int ProtectedChunkCount,
    int DecodedChunkCount,
    int? CataloguedAssetCount,
    bool WesternProfile,
    bool CanRead,
    bool CanReconstruct,
    IReadOnlyList<ClientCompatibilityCheck> Checks,
    double DurationSeconds);

public sealed record ClientAssetCompatibility(
    string AssetId,
    string AssetKind,
    string ClassName,
    string ObjectName,
    int? PreviousNativeExportIndex,
    int? CurrentNativeExportIndex,
    long? CurrentSerialOffset,
    long? CurrentSerialSize,
    string ExpectedExportSha256,
    string? CurrentExportSha256,
    ClientAssetCompatibilityState State,
    string Message);

public sealed record ClientPackageCompatibilityReport(
    int SchemaVersion,
    DateTimeOffset CheckedAt,
    string PackageProjectId,
    string PackageAlias,
    string PhysicalPackageName,
    string SourcePackagePath,
    string? ExpectedSourcePackageSha256,
    string? CurrentPackageSha256,
    string? LatestPayloadSha256,
    ClientPackageCompatibilityState State,
    bool CanRebuildWithoutRebase,
    bool CanAutomaticallyRebase,
    IReadOnlyList<ClientAssetCompatibility> Assets,
    string Summary,
    double DurationSeconds)
{
    public ClientPackageFormatProbe? FormatProbe { get; init; }

    public string? InstalledRevisionId { get; init; }
}

/// <summary>
/// Separates an expected Lost Ark client update from an unknown deployment
/// conflict. Automatic rebase is offered only when every selected native export
/// remains byte-identical or has verified Texture2D semantics and mip payloads,
/// and remains uniquely resolvable in the updated package.
/// </summary>
public sealed class ClientUpdateCompatibilityService
{
    public const int CurrentSchemaVersion = 2;
    private readonly UpkPackageReader _reader;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly AssetCatalogReader _catalogReader;
    private readonly TextureReconnaissanceService _textureReconnaissance;

    public ClientUpdateCompatibilityService(
        UpkPackageReader? reader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _reader = reader ?? new UpkPackageReader();
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(_reader, _protectionProvider);
        _textureReconnaissance = new TextureReconnaissanceService(_reader, _protectionProvider);
    }

    public async Task<ClientPackageCompatibilityReport> InspectAsync(
        GuidedTexturePackageEntry package,
        IReadOnlyList<GuidedTextureModProjectEntry> textures,
        IReadOnlyList<SkeletalMeshEditWorkspaceEntry> meshes,
        CancellationToken cancellationToken = default)
        => await InspectAsync(package, textures, meshes, [], deepFormatProbe: false, cancellationToken)
            .ConfigureAwait(false);

    public async Task<ClientPackageCompatibilityReport> InspectAsync(
        GuidedTexturePackageEntry package,
        IReadOnlyList<GuidedTextureModProjectEntry> textures,
        IReadOnlyList<SkeletalMeshEditWorkspaceEntry> meshes,
        bool deepFormatProbe,
        CancellationToken cancellationToken = default)
        => await InspectAsync(package, textures, meshes, [], deepFormatProbe, cancellationToken)
            .ConfigureAwait(false);

    public async Task<ClientPackageCompatibilityReport> InspectAsync(
        GuidedTexturePackageEntry package,
        IReadOnlyList<GuidedTextureModProjectEntry> textures,
        IReadOnlyList<SkeletalMeshEditWorkspaceEntry> meshes,
        IReadOnlyList<MaterialInstanceEditEntry> materials,
        CancellationToken cancellationToken = default)
        => await InspectAsync(package, textures, meshes, materials, deepFormatProbe: false, cancellationToken)
            .ConfigureAwait(false);

    public async Task<ClientPackageCompatibilityReport> InspectAsync(
        GuidedTexturePackageEntry package,
        IReadOnlyList<GuidedTextureModProjectEntry> textures,
        IReadOnlyList<SkeletalMeshEditWorkspaceEntry> meshes,
        IReadOnlyList<MaterialInstanceEditEntry> materials,
        bool deepFormatProbe,
        CancellationToken cancellationToken = default)
        => await InspectAsync(package, textures, meshes, materials, [], deepFormatProbe, cancellationToken)
            .ConfigureAwait(false);

    public async Task<ClientPackageCompatibilityReport> InspectAsync(
        GuidedTexturePackageEntry package,
        IReadOnlyList<GuidedTextureModProjectEntry> textures,
        IReadOnlyList<SkeletalMeshEditWorkspaceEntry> meshes,
        IReadOnlyList<MaterialInstanceEditEntry> materials,
        IReadOnlyList<StaticMeshEditEntry> staticMeshes,
        bool deepFormatProbe = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(textures);
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(staticMeshes);
        var started = DateTimeOffset.UtcNow;
        var path = Path.GetFullPath(package.Manifest.SourcePackagePath);
        var latest = ResolveLatest(package.Manifest);
        var expectedSource = package.Manifest.ClientBaselineSha256 ?? latest?.SourcePackageSha256;
        var latestPayload = latest?.PayloadSha256;
        if (!File.Exists(path))
            return Report(ClientPackageCompatibilityState.MissingPackage, null, [],
                "The Lost Ark package is missing. Repair or update the client before rebuilding.");

        string currentHash;
        try
        {
            currentHash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Report(ClientPackageCompatibilityState.InvalidPackage, null, [],
                $"The package could not be hashed: {exception.Message}");
        }
        var installedRevision = package.Manifest.Revisions
            .LastOrDefault(revision => currentHash.Equals(
                revision.PayloadSha256, StringComparison.OrdinalIgnoreCase));
        if (installedRevision is not null)
        {
            var installedProbe = new ClientPackageFormatProbe(
                ClientPackageFormatState.InstalledPayloadNotProbed, path,
                _protectionProvider.Name, new FileInfo(path).Length, currentHash,
                null, null, null, 0, 0, 0, null, false,
                CanRead: false, CanReconstruct: false,
                [new ClientCompatibilityCheck(
                    ClientCompatibilityCheckSeverity.Blocking, "CUF001",
                    "An Arkive payload is installed. Restore the verified original before client compatibility is assessed.")],
                (DateTimeOffset.UtcNow - started).TotalSeconds);
            return Report(ClientPackageCompatibilityState.InstalledPayload, currentHash, [],
                    "An Arkive package payload is installed. Restore it before checking or rebasing after a client update.")
                with { FormatProbe = installedProbe, InstalledRevisionId = installedRevision.RevisionId };
        }

        try
        {
            var summary = _reader.Read(path);
            var formatProbe = ProbeFormat(
                summary, currentHash, deepFormatProbe, cancellationToken);
            if (!formatProbe.CanRead || !formatProbe.CanReconstruct)
                return Report(ClientPackageCompatibilityState.InvalidPackage, currentHash, [],
                        $"The current package is not safe for Arkive reconstruction: " +
                        formatProbe.Checks.FirstOrDefault(check =>
                            check.Severity == ClientCompatibilityCheckSeverity.Blocking)?.Message)
                    with { FormatProbe = formatProbe };
            var catalogue = _catalogReader.Read(summary);
            formatProbe = formatProbe with { CataloguedAssetCount = catalogue.Assets.Count };
            if (!string.IsNullOrWhiteSpace(expectedSource) &&
                currentHash.Equals(expectedSource, StringComparison.OrdinalIgnoreCase))
                return Report(ClientPackageCompatibilityState.ExactSource, currentHash, [],
                        "The package exactly matches the clean client used by the latest build and passed the package-format probe.")
                    with { FormatProbe = formatProbe };
            var assets = new List<ClientAssetCompatibility>();
            foreach (var texture in textures.Where(item =>
                         item.Manifest.IncludedInPackageBuild is not false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var source = await NativeTextureExportService.LoadManifestAsync(
                        texture.Manifest.NativeTextureManifestPath, cancellationToken).ConfigureAwait(false);
                    assets.Add(await CheckTextureAssetAsync(
                        catalogue, summary, package.Manifest.PackageAlias,
                        texture.Manifest.ProjectId, texture.Manifest.ClassName,
                        texture.Manifest.ObjectName, texture.Manifest.NativeTextureManifestPath,
                        source, currentHash, cancellationToken).ConfigureAwait(false));
                    expectedSource ??= source.SourcePackageSha256;
                }
                catch (Exception exception) when (
                    exception is IOException or JsonException or NotSupportedException or ArgumentException)
                {
                    assets.Add(Unreadable(texture.Manifest.ProjectId, "Texture2D",
                        texture.Manifest.ClassName, texture.Manifest.ObjectName, exception.Message));
                }
            }
            foreach (var mesh in meshes.Where(item => item.Manifest.IncludedInPackageBuild))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var source = await LoadEditableMeshAsync(
                        mesh.Manifest.SourceManifestPath, cancellationToken).ConfigureAwait(false);
                    assets.Add(CheckAsset(
                        catalogue, summary, mesh.Manifest.EditId, "SkeletalMesh",
                        source.ClassName, source.ObjectName, source.NativeExportIndex,
                        source.SourceExportSha256, cancellationToken));
                    expectedSource ??= source.SourcePackageSha256;
                }
                catch (Exception exception) when (
                    exception is IOException or JsonException or NotSupportedException or ArgumentException)
                {
                    assets.Add(Unreadable(mesh.Manifest.EditId, "SkeletalMesh",
                        "SkeletalMesh", mesh.Manifest.ObjectName, exception.Message));
                }
            }
            foreach (var material in materials.Where(item => item.Document.IncludedInPackageBuild))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var report = await LoadMaterialReportAsync(
                        material.Document.SourceReconnaissancePath, cancellationToken).ConfigureAwait(false);
                    assets.Add(CheckAsset(
                        catalogue, summary, material.Document.EditId, "MaterialInstanceConstant",
                        material.Document.ClassName, material.Document.ObjectName,
                        report.NativeExportIndex, report.ExportPayloadSha256, cancellationToken));
                    expectedSource ??= report.PackageSha256;
                }
                catch (Exception exception) when (
                    exception is IOException or JsonException or NotSupportedException or ArgumentException)
                {
                    assets.Add(Unreadable(material.Document.EditId, "MaterialInstanceConstant",
                        material.Document.ClassName, material.Document.ObjectName, exception.Message));
                }
            }
            foreach (var mesh in staticMeshes.Where(item => item.Manifest.IncludedInPackageBuild))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var source = await StaticMeshEditableExportService.LoadManifestAsync(
                        mesh.Manifest.SourceEditableManifestPath, cancellationToken).ConfigureAwait(false);
                    assets.Add(CheckAsset(
                        catalogue, summary, mesh.Manifest.EditId, "StaticMesh",
                        source.ClassName, source.ObjectName, source.NativeExportIndex,
                        source.SourceExportSha256, cancellationToken));
                    expectedSource ??= source.SourcePackageSha256;
                }
                catch (Exception exception) when (
                    exception is IOException or JsonException or NotSupportedException or ArgumentException)
                {
                    assets.Add(Unreadable(mesh.Manifest.EditId, "StaticMesh",
                        "StaticMesh", mesh.Manifest.DisplayName, exception.Message));
                }
            }

            var safe = assets.Count > 0 && assets.All(asset =>
                asset.State == ClientAssetCompatibilityState.Unchanged) && staticMeshes.Count == 0;
            var state = safe
                ? ClientPackageCompatibilityState.SafeAutomaticRebase
                : ClientPackageCompatibilityState.ReviewRequired;
            var summaryText = safe
                ? $"The client package changed, but all {assets.Count:N0} selected native assets are byte-identical or semantically unchanged. Arkive can safely refresh their source manifests and rebuild."
                : assets.Count == 0
                    ? "The client package changed and this package project has no selected source assets to verify."
                    : staticMeshes.Count > 0 && assets.All(asset =>
                        asset.State == ClientAssetCompatibilityState.Unchanged)
                        ? "The client package changed and its selected StaticMesh exports are byte-identical, but automatic StaticMesh source-manifest rebasing is not enabled yet. Re-export and register those meshes from the updated clean UPK before rebuilding."
                        : $"The client package changed and {assets.Count(asset => asset.State != ClientAssetCompatibilityState.Unchanged):N0} selected assets need review before rebuilding.";
            return Report(state, currentHash, assets, summaryText) with { FormatProbe = formatProbe };
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or ArgumentException)
        {
            return Report(ClientPackageCompatibilityState.InvalidPackage, currentHash, [],
                $"The changed package could not be catalogued safely: {exception.Message}");
        }

        ClientPackageCompatibilityReport Report(
            ClientPackageCompatibilityState state,
            string? hash,
            IReadOnlyList<ClientAssetCompatibility> assets,
            string summary) => new(
            CurrentSchemaVersion, DateTimeOffset.UtcNow,
            package.Manifest.PackageProjectId, package.Manifest.PackageAlias,
            package.Manifest.PhysicalPackageName, path, expectedSource, hash,
            latestPayload, state,
            state == ClientPackageCompatibilityState.ExactSource,
            state == ClientPackageCompatibilityState.SafeAutomaticRebase,
            assets, summary, (DateTimeOffset.UtcNow - started).TotalSeconds);
    }

    private static async Task<MaterialInstanceReconnaissanceReport> LoadMaterialReportAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<MaterialInstanceReconnaissanceReport>(
                   stream, MaterialInstanceWorkspaceStore.SerializerOptions, cancellationToken)
                   .ConfigureAwait(false) ??
               throw new InvalidDataException("The material reconnaissance report was empty.");
    }

    public async Task<ClientPackageFormatProbe> ProbeFormatAsync(
        string packagePath,
        bool deepFormatProbe = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var started = DateTimeOffset.UtcNow;
        var path = Path.GetFullPath(packagePath);
        if (!File.Exists(path))
            return new ClientPackageFormatProbe(
                ClientPackageFormatState.MissingPackage, path, _protectionProvider.Name,
                null, null, null, null, null, 0, 0, 0, null, false,
                false, false,
                [new ClientCompatibilityCheck(ClientCompatibilityCheckSeverity.Blocking,
                    "CUF002", "The package is missing.")],
                (DateTimeOffset.UtcNow - started).TotalSeconds);
        try
        {
            var hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
            var summary = _reader.Read(path);
            var probe = ProbeFormat(summary, hash, deepFormatProbe, cancellationToken);
            if (probe.CanRead && probe.CanReconstruct)
            {
                var catalogue = _catalogReader.Read(summary);
                probe = probe with { CataloguedAssetCount = catalogue.Assets.Count };
            }
            return probe;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or ArgumentException)
        {
            return new ClientPackageFormatProbe(
                ClientPackageFormatState.InvalidSummary, path, _protectionProvider.Name,
                File.Exists(path) ? new FileInfo(path).Length : null, null,
                null, null, null, 0, 0, 0, null, false,
                false, false,
                [new ClientCompatibilityCheck(ClientCompatibilityCheckSeverity.Blocking,
                    "CUF003", $"The package could not be parsed safely: {exception.Message}")],
                (DateTimeOffset.UtcNow - started).TotalSeconds);
        }
    }

    private ClientPackageFormatProbe ProbeFormat(
        PackageSummary summary,
        string packageSha256,
        bool deep,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var checks = new List<ClientCompatibilityCheck>();
        var protectedChunks = summary.CompressedChunks.Count(chunk => chunk.LostArkFlags == 1);
        var validation = PackageValidator.Validate(summary);
        foreach (var issue in validation.Where(issue => issue.Severity == ValidationSeverity.Error))
            checks.Add(new ClientCompatibilityCheck(
                ClientCompatibilityCheckSeverity.Blocking, issue.Code, issue.Message));
        if (checks.Count > 0)
            return CreateProbe(ClientPackageFormatState.StructuralErrors, false, false, 0);

        if (!summary.IsWesternLostArkProfile)
        {
            checks.Add(new ClientCompatibilityCheck(
                ClientCompatibilityCheckSeverity.Blocking, "CUF010",
                $"The package profile changed to UE {summary.FileVersion}/{summary.LicenseeVersion} " +
                $"with compression 0x{summary.CompressionFlags:X8}; Arkive supports the Western 868/16 profile with compression 0x44."));
            return CreateProbe(ClientPackageFormatState.UnsupportedProfile, false, false, 0);
        }

        var unsupportedFlags = summary.CompressedChunks.FirstOrDefault(chunk => chunk.LostArkFlags is not 0 and not 1);
        if (unsupportedFlags is not null)
        {
            checks.Add(new ClientCompatibilityCheck(
                ClientCompatibilityCheckSeverity.Blocking, "CUF011",
                $"A compressed chunk uses unsupported Lost Ark flags 0x{unsupportedFlags.LostArkFlags:X8}."));
            return CreateProbe(ClientPackageFormatState.UnsupportedProfile, false, false, 0);
        }
        var unavailable = summary.CompressedChunks.FirstOrDefault(chunk =>
            chunk.LostArkFlags == 1 &&
            (!_protectionProvider.CanUnprotect(summary, chunk) ||
             !_protectionProvider.CanProtect(summary, chunk)));
        if (unavailable is not null)
        {
            checks.Add(new ClientCompatibilityCheck(
                ClientCompatibilityCheckSeverity.Blocking, "CUF012",
                "The configured protection provider cannot both decode and reconstruct the protected chunk profile."));
            return CreateProbe(ClientPackageFormatState.ProtectionUnavailable, false, false, 0);
        }

        var decoded = 0;
        if (deep && summary.CompressedChunks.Count > 0)
        {
            try
            {
                using var logical = new LogicalPackageStream(summary, _protectionProvider);
                foreach (var chunk in summary.CompressedChunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    logical.Position = chunk.UncompressedOffset;
                    if (logical.ReadByte() < 0)
                        throw new EndOfStreamException(
                            $"Compressed chunk at logical offset 0x{chunk.UncompressedOffset:X} decoded no data.");
                    decoded++;
                }
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                checks.Add(new ClientCompatibilityCheck(
                    ClientCompatibilityCheckSeverity.Blocking, "CUF013",
                    $"A compressed/protected package chunk could not be decoded: {exception.Message}"));
                return CreateProbe(ClientPackageFormatState.DecodeFailed, false, false, decoded);
            }
        }

        checks.Add(new ClientCompatibilityCheck(
            ClientCompatibilityCheckSeverity.Information, "CUF000",
            deep
                ? $"Validated the package summary, Western profile, protection write capability, and decoded all {decoded:N0} compressed chunks."
                : "Validated the package summary, Western profile, and protection read/write capability."));
        return CreateProbe(ClientPackageFormatState.Compatible, true, true, decoded);

        ClientPackageFormatProbe CreateProbe(
            ClientPackageFormatState state, bool readable, bool reconstructable, int decodedCount) =>
            new(state, summary.FilePath, _protectionProvider.Name,
                summary.PhysicalFileSize, packageSha256, summary.FileVersion,
                summary.LicenseeVersion, summary.CompressionFlags,
                summary.CompressedChunks.Count, protectedChunks, decodedCount,
                null, summary.IsWesternLostArkProfile, readable, reconstructable,
                checks.ToArray(), (DateTimeOffset.UtcNow - started).TotalSeconds);
    }

    private ClientAssetCompatibility CheckAsset(
        AssetCatalog catalogue,
        PackageSummary summary,
        string id,
        string kind,
        string className,
        string objectName,
        int? previousIndex,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        var matches = catalogue.Assets.Where(asset =>
            asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase) &&
            (asset.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase) ||
             asset.ClassName.Contains(kind, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length == 0)
            return new ClientAssetCompatibility(id, kind, className, objectName,
                previousIndex, null, null, null, expectedHash, null,
                ClientAssetCompatibilityState.Missing,
                "The object no longer exists under the same class and name.");
        if (matches.Length > 1)
        {
            // Same-named exports in one package are told apart by content while they are unchanged.
            using var logical = new LogicalPackageStream(summary, _protectionProvider);
            var identical = matches.Where(asset => logical.HashRangeSha256(asset.SerialOffset, asset.SerialSize, cancellationToken)
                .Equals(expectedHash, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (identical.Length == 1) matches = identical;
        }
        if (matches.Length > 1)
            return new ClientAssetCompatibility(id, kind, className, objectName,
                previousIndex, null, null, null, expectedHash, null,
                ClientAssetCompatibilityState.Ambiguous,
                $"{matches.Length:N0} current exports share this class and object name.");
        var match = matches[0];
        string hash;
        using (var logical = new LogicalPackageStream(summary, _protectionProvider))
            hash = logical.HashRangeSha256(match.SerialOffset, match.SerialSize, cancellationToken);
        var unchanged = hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
        return new ClientAssetCompatibility(id, kind, className, objectName,
            previousIndex, match.ExportIndex, match.SerialOffset, match.SerialSize,
            expectedHash, hash,
            unchanged ? ClientAssetCompatibilityState.Unchanged : ClientAssetCompatibilityState.Changed,
            unchanged
                ? "The native serialized export is byte-identical in the updated package."
                : "The native serialized export changed and requires structural review before rebasing.");
    }

    private async Task<ClientAssetCompatibility> CheckTextureAssetAsync(
        AssetCatalog catalogue,
        PackageSummary summary,
        string packageAlias,
        string id,
        string className,
        string objectName,
        string sourceManifestPath,
        NativeTextureExportManifest source,
        string currentPackageSha256,
        CancellationToken cancellationToken)
    {
        var serialized = CheckAsset(
            catalogue, summary, id, "Texture2D", className, objectName,
            source.NativeExportIndex, source.SourcePayloadSha256, cancellationToken);
        if (serialized.State != ClientAssetCompatibilityState.Changed ||
            serialized.CurrentNativeExportIndex is not { } exportIndex ||
            serialized.CurrentSerialOffset is not { } serialOffset ||
            serialized.CurrentSerialSize is not { } serialSize)
            return serialized;

        try
        {
            var priorReconPath = ResolveReconnaissancePath(
                sourceManifestPath, source.ReconnaissanceJsonPath);
            var previous = await LoadTextureReconnaissanceAsync(
                priorReconPath, cancellationToken).ConfigureAwait(false);
            var current = await _textureReconnaissance.InspectAsync(
                new TextureReconnaissanceRequest(
                    summary.FilePath, packageAlias, checked(exportIndex - 1),
                    AssetKind.Texture, className, objectName,
                    serialOffset, serialSize, currentPackageSha256),
                cancellationToken).ConfigureAwait(false);
            var semantic = TextureUpdateSemanticComparer.Compare(source, previous, current);
            return semantic.IsEquivalent
                ? serialized with
                {
                    State = ClientAssetCompatibilityState.Unchanged,
                    Message = $"{semantic.Code}: {semantic.Message}",
                }
                : serialized with
                {
                    Message = $"{semantic.Code}: {semantic.Message} " +
                              "The updated source requires manual review before rebuilding.",
                };
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException or
                NotSupportedException or ArgumentException)
        {
            return serialized with
            {
                Message = "The serialized export changed and Arkive could not complete its " +
                          $"semantic Texture2D comparison: {exception.Message}",
            };
        }
    }

    private static string ResolveReconnaissancePath(
        string sourceManifestPath,
        string reconnaissancePath)
    {
        if (string.IsNullOrWhiteSpace(reconnaissancePath))
            throw new InvalidDataException(
                "The retained native texture manifest has no reconnaissance path.");
        return Path.GetFullPath(Path.IsPathRooted(reconnaissancePath)
            ? reconnaissancePath
            : Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(sourceManifestPath))!,
                reconnaissancePath));
    }

    private static async Task<TextureReconnaissanceReport> LoadTextureReconnaissanceAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<TextureReconnaissanceReport>(
                   stream, new JsonSerializerOptions
                   {
                       PropertyNameCaseInsensitive = true,
                       Converters = { new JsonStringEnumConverter() },
                   }, cancellationToken).ConfigureAwait(false) ??
               throw new InvalidDataException(
                   "The retained texture reconnaissance report was empty.");
    }

    private static ClientAssetCompatibility Unreadable(
        string id, string kind, string className, string objectName, string message) =>
        new(id, kind, className, objectName, null, null, null, null, string.Empty, null,
            ClientAssetCompatibilityState.Unreadable, message);

    private static GuidedTexturePackageRevision? ResolveLatest(GuidedTexturePackageManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.LatestRevisionId)
            ? manifest.Revisions.LastOrDefault()
            : manifest.Revisions.FirstOrDefault(revision => revision.RevisionId.Equals(
                manifest.LatestRevisionId, StringComparison.OrdinalIgnoreCase));

    private static async Task<SkeletalMeshEditableExportManifest> LoadEditableMeshAsync(
        string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        return await JsonSerializer.DeserializeAsync<SkeletalMeshEditableExportManifest>(
                   stream, new JsonSerializerOptions
                   {
                       PropertyNameCaseInsensitive = true,
                       PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                   }, cancellationToken).ConfigureAwait(false) ??
               throw new InvalidDataException("The editable mesh source manifest was empty.");
    }

    private static async Task<string> HashFileAsync(
        string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
