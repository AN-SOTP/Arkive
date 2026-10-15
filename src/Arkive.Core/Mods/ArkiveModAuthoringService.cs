using System.Text.Json;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

public sealed record ArkiveModCapturedAsset(
    ArkiveModFileAsset Descriptor,
    IReadOnlyList<ArkiveModPackFile> Files,
    ArkiveModLocalEdit Edit);

public sealed class ArkiveModAuthoringService
{
    public const string CurrentCreatedWith = "Arkive";
    private readonly ArkiveModLibrary _library;
    private readonly ArkiveModEditWorkspaces _edits;

    public ArkiveModAuthoringService(ArkiveModLibrary library, ArkiveModEditWorkspaces edits)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _edits = edits ?? throw new ArgumentNullException(nameof(edits));
    }

    public static async Task<ArkiveModCapturedAsset> CaptureAsync(
        ArkiveModLocalEdit edit,
        string? assetId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var id = assetId ?? Guid.NewGuid().ToString("N");
        var captured = edit.Entry switch
        {
            GuidedTextureModProjectEntry texture => await CaptureTextureAsync(edit, texture, id, cancellationToken)
                .ConfigureAwait(false),
            SkeletalMeshEditWorkspaceEntry mesh => await CaptureSkeletalMeshAsync(edit, mesh, id, cancellationToken)
                .ConfigureAwait(false),
            StaticMeshEditEntry mesh => await CaptureStaticMeshAsync(edit, mesh, id, cancellationToken)
                .ConfigureAwait(false),
            MaterialInstanceEditEntry material => await CaptureMaterialAsync(edit, material, id, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ArkiveModRefusalException("AUT001", $"{edit.DisplayName} is not a supported edit."),
        };
        return edit.ObjectPath is null ? captured : captured with
        {
            Descriptor = captured.Descriptor with
            {
                Target = captured.Descriptor.Target with { ObjectPath = edit.ObjectPath },
            },
        };
    }

    private static async Task<ArkiveModCapturedAsset> CaptureTextureAsync(
        ArkiveModLocalEdit edit,
        GuidedTextureModProjectEntry texture,
        string assetId,
        CancellationToken cancellationToken)
    {
        var manifest = texture.Manifest;
        var image = Path.GetFullPath(manifest.EditableImagePath);
        if (!File.Exists(image))
            Refuse("AUT010", $"{manifest.DisplayName} has no editable image to save.");
        var extension = Path.GetExtension(image).ToLowerInvariant();
        if (extension is not (".png" or ".dds"))
            Refuse("AUT011", $"{manifest.DisplayName} uses an unsupported editable image type ({extension}).");
        var native = await NativeTextureExportService.LoadManifestAsync(
            manifest.NativeTextureManifestPath, cancellationToken).ConfigureAwait(false);
        var descriptor = new ArkiveModFileAsset(
            assetId, UnifiedPackageAssetKind.Texture2D, manifest.DisplayName,
            new ArkiveModAssetTarget(
                manifest.PackageAlias, PhysicalName(manifest.SourcePackagePath), manifest.ClassName,
                manifest.ObjectName, native.NativeExportIndex, native.SourcePackageSha256.ToUpperInvariant(),
                native.SourcePayloadSha256.ToUpperInvariant()),
            [])
        {
            Texture = new ArkiveModTextureData(manifest.PixelFormat, manifest.Width, manifest.Height, manifest.AlphaPolicy),
        };
        return new ArkiveModCapturedAsset(
            descriptor,
            [new ArkiveModPackFile(ArkiveModFileFormat.ImageRole, image, manifest.ObjectName + "-edit" + extension)],
            edit);
    }

    private static async Task<ArkiveModCapturedAsset> CaptureSkeletalMeshAsync(
        ArkiveModLocalEdit edit,
        SkeletalMeshEditWorkspaceEntry mesh,
        string assetId,
        CancellationToken cancellationToken)
    {
        var manifest = mesh.Manifest;
        if (manifest.Preflight.Readiness == SkeletalMeshEditReadiness.Refused || manifest.Preflight.ErrorCount != 0)
            Refuse("AUT020", $"{manifest.DisplayName} failed mesh preflight; resolve its errors before saving it to a mod.");
        await RequireFileHashAsync(mesh.EditedGlbPath, manifest.EditedGlbSha256, manifest.DisplayName, cancellationToken)
            .ConfigureAwait(false);
        var source = await ArkiveModEditSnapshot.LoadSkeletalSourceAsync(mesh, cancellationToken).ConfigureAwait(false);
        var descriptor = new ArkiveModFileAsset(
            assetId, UnifiedPackageAssetKind.SkeletalMesh, manifest.DisplayName,
            new ArkiveModAssetTarget(
                source.PackageAlias, PhysicalName(source.SourcePackagePath), source.ClassName, source.ObjectName,
                source.NativeExportIndex, source.SourcePackageSha256.ToUpperInvariant(),
                source.SourceExportSha256.ToUpperInvariant()),
            [])
        {
            Mesh = new ArkiveModMeshData(
                manifest.Preflight.Readiness == SkeletalMeshEditReadiness.ReadyForTopologyCompilation
                    ? "TopologyReplacement"
                    : "TopologyPreserving",
                manifest.SourceGlbSha256.ToUpperInvariant()),
        };
        return new ArkiveModCapturedAsset(
            descriptor,
            [new ArkiveModPackFile(ArkiveModFileFormat.GlbRole, mesh.EditedGlbPath, source.ObjectName + "-edit.glb")],
            edit);
    }

    private static async Task<ArkiveModCapturedAsset> CaptureStaticMeshAsync(
        ArkiveModLocalEdit edit,
        StaticMeshEditEntry mesh,
        string assetId,
        CancellationToken cancellationToken)
    {
        var manifest = mesh.Manifest;
        if (manifest.Preflight.Readiness == StaticMeshEditReadiness.Refused)
            Refuse("AUT030", $"{manifest.DisplayName} failed StaticMesh preflight; resolve it before saving it to a mod.");
        await RequireFileHashAsync(manifest.EditedGlbPath, manifest.EditedGlbSha256, manifest.DisplayName, cancellationToken)
            .ConfigureAwait(false);
        var source = await StaticMeshEditableExportService.LoadManifestAsync(
            manifest.SourceEditableManifestPath, cancellationToken).ConfigureAwait(false);
        var descriptor = new ArkiveModFileAsset(
            assetId, UnifiedPackageAssetKind.StaticMesh, manifest.DisplayName,
            new ArkiveModAssetTarget(
                source.PackageAlias, PhysicalName(source.SourcePackagePath), source.ClassName, source.ObjectName,
                source.NativeExportIndex, source.SourcePackageSha256.ToUpperInvariant(),
                source.SourceExportSha256.ToUpperInvariant()),
            [])
        {
            Mesh = new ArkiveModMeshData(
                manifest.Preflight.TopologyChanged ? "TopologyReplacement" : "TopologyPreserving",
                source.GlbSha256.ToUpperInvariant()),
        };
        return new ArkiveModCapturedAsset(
            descriptor,
            [new ArkiveModPackFile(ArkiveModFileFormat.GlbRole, manifest.EditedGlbPath, source.ObjectName + "-edit.glb")],
            edit);
    }

    private static async Task<ArkiveModCapturedAsset> CaptureMaterialAsync(
        ArkiveModLocalEdit edit,
        MaterialInstanceEditEntry material,
        string assetId,
        CancellationToken cancellationToken)
    {
        var document = material.Document;
        MaterialInstanceReconnaissanceReport report;
        await using (var stream = new FileStream(
                         Path.GetFullPath(document.SourceReconnaissancePath), FileMode.Open, FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous))
        {
            report = await JsonSerializer.DeserializeAsync<MaterialInstanceReconnaissanceReport>(
                         stream, MaterialInstanceWorkspaceStore.SerializerOptions, cancellationToken)
                         .ConfigureAwait(false) ??
                     throw new InvalidDataException("The material reconnaissance report was empty.");
        }
        var descriptor = new ArkiveModFileAsset(
            assetId, UnifiedPackageAssetKind.MaterialInstanceConstant, document.DisplayName,
            new ArkiveModAssetTarget(
                document.PackageAlias, PhysicalName(document.SourcePackagePath), document.ClassName,
                document.ObjectName, report.NativeExportIndex, document.SourcePackageSha256.ToUpperInvariant(),
                document.SourceExportSha256.ToUpperInvariant()),
            [])
        {
            Material = MaterialData(document),
        };
        return new ArkiveModCapturedAsset(descriptor, [], edit);
    }

    private static ArkiveModMaterialData MaterialData(MaterialInstanceEditDocument document)
    {
        var textures = document.Textures.Select(item => Reference(
            item.Name, item.SourceIdentity, item.SourceResolvedObject, item.TargetIdentity, item.ResolvedObject,
            document.DisplayName)).ToArray();
        var parent = document.Parent is null
            ? null
            : Reference("Parent", document.Parent.SourceIdentity, document.Parent.SourceResolvedObject,
                document.Parent.TargetIdentity, document.Parent.ResolvedObject, document.DisplayName);
        return new ArkiveModMaterialData(document.Scalars, document.Vectors, textures, parent);

        static ArkiveModMaterialReference Reference(
            string name, string sourceIdentity, string sourceResolved, string targetIdentity,
            string targetResolved, string displayName)
        {
            var source = string.IsNullOrWhiteSpace(sourceIdentity) ? targetIdentity : sourceIdentity;
            var target = string.IsNullOrWhiteSpace(targetIdentity) ? source : targetIdentity;
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                Refuse("AUT040",
                    $"{displayName}'s '{name}' reference has no stable identity. Open and save the material project once, then retry.");
            if (!source.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                target.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase))
                Refuse("AUT041",
                    $"{displayName}'s '{name}' replacement uses a legacy local reference that would not survive sharing. Re-select it in the material editor.");
            return new ArkiveModMaterialReference(name, source, sourceResolved, target, targetResolved);
        }
    }

    public async Task<ArkiveModLibraryEntry> CreateModAsync(
        ArkiveModMetadata metadata,
        IReadOnlyList<ArkiveModCapturedAsset> assets,
        ArkiveModOrigin origin,
        bool enabled,
        string? authoredForPackageProjectId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(assets);
        if (assets.Count == 0) Refuse("AUT050", "A mod needs at least one asset.");
        var library = await _library.LoadAsync(cancellationToken).ConfigureAwait(false);
        RequireUnowned(library, assets, ownModId: null);
        var now = DateTimeOffset.UtcNow;
        var modId = Guid.NewGuid().ToString("N");
        var manifest = CreateManifest(modId, metadata, now, now);
        var fileName = _library.UniqueFileName(ArkiveModLibrary.SuggestFileName(metadata.Name), library);
        var written = await ArkiveModFileService.WriteAsync(
                Path.Combine(_library.ModsDirectory, fileName), manifest,
                assets.Select(asset => new ArkiveModPackAsset(asset.Descriptor, asset.Files)).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        return await _library.RegisterWrittenFileAsync(
                written, origin, enabled, priority: 0, Bindings(written.Manifest, assets),
                authoredForPackageProjectId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ArkiveModLibraryEntry> SaveModAsync(
        string modId,
        ArkiveModMetadata? metadata = null,
        IReadOnlyList<ArkiveModCapturedAsset>? addOrReplace = null,
        IReadOnlyCollection<string>? removeAssetIds = null,
        CancellationToken cancellationToken = default)
    {
        var library = await _library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var entry = library.Find(modId) ??
                    throw new ArkiveModRefusalException("AUT060", $"Mod {modId} is not in the library.");
        var modPath = _library.GetModFilePath(entry);
        if (!File.Exists(modPath))
            Refuse("AUT061", $"{entry.Manifest.Name}'s .arkive file is missing: {modPath}");
        var current = await ArkiveModFileService.ReadAsync(modPath, verifyFiles: false, cancellationToken)
            .ConfigureAwait(false);
        var removed = (removeAssetIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incoming = (addOrReplace ?? []).ToList();
        RequireUnowned(library, incoming, entry.ModId);
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_edits, cancellationToken).ConfigureAwait(false);
        var packAssets = new List<ArkiveModCapturedAsset>();
        var carried = new List<ArkiveModPackAsset>();
        var scratch = _library.CreateWorkingDirectory(entry.ModId, "save", "repack");
        try
        {
            foreach (var asset in current.Manifest.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (removed.Contains(asset.AssetId)) continue;
                var key = ArkiveModTargetKey.From(asset);
                var replacement = incoming.FindIndex(item => ArkiveModTargetKey.From(item.Descriptor) == key);
                if (replacement >= 0)
                {
                    var captured = incoming[replacement];
                    incoming.RemoveAt(replacement);
                    packAssets.Add(captured with { Descriptor = captured.Descriptor with { AssetId = asset.AssetId } });
                    continue;
                }
                var binding = entry.BindingFor(asset.AssetId);
                var local = binding is null ? null : snapshot.Find(binding.EditId);
                if (local is not null)
                {
                    var recaptured = await CaptureAsync(local, asset.AssetId, cancellationToken).ConfigureAwait(false);
                    packAssets.Add(recaptured with
                    {
                        Descriptor = recaptured.Descriptor with { DisplayName = asset.DisplayName },
                    });
                    continue;
                }
                var directory = Path.Combine(scratch, asset.AssetId);
                var files = await ArkiveModFileService.ExtractAssetFilesAsync(
                    modPath, asset, directory, cancellationToken).ConfigureAwait(false);
                carried.Add(new ArkiveModPackAsset(
                    asset,
                    asset.Files.Select(file => new ArkiveModPackFile(
                        file.Role, files[file.Role], Path.GetFileName(file.Path))).ToArray()));
            }
            packAssets.AddRange(incoming);
            if (packAssets.Count + carried.Count == 0)
                Refuse("AUT062", "A mod must keep at least one asset. Remove the mod instead.");

            var name = metadata ?? new ArkiveModMetadata(
                current.Manifest.Name, current.Manifest.Version, current.Manifest.Author,
                current.Manifest.Description, current.Manifest.Website, current.Manifest.Tags);
            var manifest = CreateManifest(current.Manifest.ModId, name, current.Manifest.CreatedAt, DateTimeOffset.UtcNow);
            var written = await ArkiveModFileService.WriteAsync(
                    modPath, manifest,
                    packAssets.Select(asset => new ArkiveModPackAsset(asset.Descriptor, asset.Files))
                        .Concat(carried).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            var bindings = Bindings(written.Manifest, packAssets);
            var updated = await _library.RegisterWrittenFileAsync(
                    written, entry.Origin, entry.Enabled, entry.Priority, bindings,
                    entry.AuthoredForPackageProjectId, cancellationToken)
                .ConfigureAwait(false);

            var rebound = bindings.ToDictionary(binding => binding.AssetId, StringComparer.OrdinalIgnoreCase);
            var released = entry.Bindings.Where(binding =>
                    removed.Contains(binding.AssetId) ||
                    (rebound.TryGetValue(binding.AssetId, out var current) &&
                     !current.EditId.Equals(binding.EditId, StringComparison.OrdinalIgnoreCase)))
                .Select(binding => new ArkiveModReleasedEdit(binding.EditId, Delete: false))
                .ToArray();
            await ArkiveModSelectionService.ReleaseAsync(_edits, released, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
        }
    }

    public async Task<ArkiveModLibraryEntry> SaveDraftAsync(
        ArkiveModAuthoringDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Metadata.Name)) Refuse("AUT090", "Give the mod a name before saving.");
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_edits, cancellationToken).ConfigureAwait(false);
        var captured = new List<ArkiveModCapturedAsset>();
        foreach (var editId in draft.EditIds)
        {
            var local = snapshot.Find(editId) ??
                throw new ArkiveModRefusalException("AUT091", "A working asset is missing. Prepare the mod for editing again.");
            local = await ValidateEditAsync(local, cancellationToken).ConfigureAwait(false);
            captured.Add(await CaptureAsync(local, cancellationToken: cancellationToken).ConfigureAwait(false));
        }
        if (captured.Select(asset => ArkiveModTargetKey.From(asset.Descriptor)).Distinct().Count() != captured.Count)
            Refuse("AUT092", "The mod contains multiple working copies of the same asset. Keep one copy per asset.");
        return draft.ModId is null
            ? await CreateModAsync(draft.Metadata, captured, ArkiveModOrigin.Created, enabled: false,
                cancellationToken: cancellationToken).ConfigureAwait(false)
            : await SaveModAsync(draft.ModId, draft.Metadata, captured, draft.RemovedAssetIds, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<ArkiveModLocalEdit> ValidateEditAsync(
        ArkiveModLocalEdit edit, CancellationToken cancellationToken = default)
    {
        switch (edit.Entry)
        {
            case GuidedTextureModProjectEntry texture:
                var checkDirectory = _library.CreateWorkingDirectory(Guid.NewGuid().ToString("N"), "texture", "validate");
                try
                {
                    await new TextureImportCandidateService().CreateAsync(new TextureImportCandidateRequest(
                        texture.Manifest.NativeTextureManifestPath, texture.Manifest.EditableImagePath,
                        checkDirectory, texture.Manifest.AlphaPolicy), cancellationToken).ConfigureAwait(false);
                    return edit;
                }
                finally
                {
                    if (Directory.Exists(checkDirectory)) Directory.Delete(checkDirectory, recursive: true);
                }
            case SkeletalMeshEditWorkspaceEntry mesh:
                var manifest = await ArkiveModEditSnapshot.LoadSkeletalSourceAsync(mesh, cancellationToken)
                    .ConfigureAwait(false);
                var source = new SkeletalMeshEditableWorkspaceEntry(
                    Path.GetDirectoryName(mesh.Manifest.SourceManifestPath)!, mesh.Manifest.SourceManifestPath,
                    Path.Combine(Path.GetDirectoryName(mesh.Manifest.SourceManifestPath)!, manifest.GlbRelativePath),
                    manifest, true, true, true);
                var revalidated = await _edits.SkeletalMeshEdits.RevalidateAsync(mesh, source, cancellationToken)
                    .ConfigureAwait(false);
                return edit with { Entry = revalidated };
            case StaticMeshEditEntry mesh:
                return edit with
                {
                    Entry = await _edits.StaticMeshEdits.RevalidateAsync(mesh, cancellationToken).ConfigureAwait(false),
                };
            default:
                return edit;
        }
    }

    public async Task<ArkiveModLibraryEntry?> SavePackageProjectAsync(
        GuidedTexturePackageEntry package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var library = await _library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_edits, cancellationToken).ConfigureAwait(false);
        var drafts = snapshot.DraftsFor(package.Manifest.SourcePackagePath, library);
        var authored = library.AuthoredFor(package.Manifest.PackageProjectId);
        var resolution = ArkiveModConflictResolver.Resolve(library.Mods);
        foreach (var draft in drafts)
        {
            var winner = resolution.Targets.FirstOrDefault(target => target.Key == draft.TargetKey)?.Winner;
            if (winner is not null &&
                (authored is null || !winner.ModId.Equals(authored.ModId, StringComparison.OrdinalIgnoreCase)))
                Refuse("AUT080",
                    $"The checked draft '{draft.DisplayName}' replaces {draft.ObjectName}, which the enabled mod " +
                    $"'{winner.ModName}' supplies. Uncheck the draft, or disable that mod first.");
        }
        var captured = new List<ArkiveModCapturedAsset>();
        foreach (var draft in drafts)
            captured.Add(await CaptureAsync(draft, cancellationToken: cancellationToken).ConfigureAwait(false));

        if (authored is null)
        {
            if (captured.Count == 0) return null;
            return await CreateModAsync(
                    new ArkiveModMetadata(PackageDisplayName(package), "1.0.0", Description: package.Manifest.Notes),
                    captured, ArkiveModOrigin.Created, enabled: true,
                    package.Manifest.PackageProjectId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (captured.Count == 0 && !await HasLocalChangesAsync(authored, snapshot, cancellationToken).ConfigureAwait(false))
            return authored;
        var saved = await SaveModAsync(authored.ModId, addOrReplace: captured, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (saved.Enabled || captured.Count == 0) return saved;
        var enabled = await _library.SetEnabledAsync(saved.ModId, true, cancellationToken).ConfigureAwait(false);
        return enabled.Find(saved.ModId) ?? saved;
    }

    private static async Task<bool> HasLocalChangesAsync(
        ArkiveModLibraryEntry mod,
        ArkiveModEditSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var binding in mod.Bindings)
        {
            if (snapshot.Find(binding.EditId) is { } local &&
                await IsModifiedAsync(local, binding, cancellationToken).ConfigureAwait(false))
                return true;
        }
        return false;
    }

    public static async Task<bool> IsModifiedAsync(
        ArkiveModLocalEdit edit,
        ArkiveModAssetBinding binding,
        CancellationToken cancellationToken = default) =>
        await PortableContentSha256Async(edit, cancellationToken).ConfigureAwait(false) is { } content &&
        !content.Equals(binding.PackedContentSha256, StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> PortableContentSha256Async(
        ArkiveModLocalEdit edit,
        CancellationToken cancellationToken)
    {
        if (edit.Entry is not MaterialInstanceEditEntry material)
        {
            var path = edit.Entry switch
            {
                GuidedTextureModProjectEntry texture => texture.Manifest.EditableImagePath,
                SkeletalMeshEditWorkspaceEntry mesh => mesh.EditedGlbPath,
                StaticMeshEditEntry mesh => mesh.Manifest.EditedGlbPath,
                _ => null,
            };
            return path is not null && File.Exists(path)
                ? await ArkiveModEditSnapshot.HashFileCachedAsync(path, cancellationToken).ConfigureAwait(false)
                : null;
        }
        try
        {
            return ArkiveModFileService.ComputeMaterialChangesSha256(MaterialData(material.Document));
        }
        catch (ArkiveModRefusalException)
        {
            return null;
        }
    }

    public async Task RemoveModAsync(string modId, CancellationToken cancellationToken = default)
    {
        var library = await _library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var mod = library.Find(modId) ??
                  throw new ArkiveModRefusalException("AUT060", $"Mod {modId} is not in the library.");
        await ArkiveModSelectionService.ReleaseAsync(_edits, mod.Bindings
                .Select(binding => new ArkiveModReleasedEdit(binding.EditId, mod.Origin == ArkiveModOrigin.Imported))
                .ToArray(), cancellationToken)
            .ConfigureAwait(false);
        await _library.RemoveAsync(modId, deleteFile: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArkiveModLibraryScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var scan = await _library.ScanAsync(cancellationToken).ConfigureAwait(false);
        await ArkiveModSelectionService.ReleaseAsync(_edits, scan.Released, cancellationToken).ConfigureAwait(false);
        return scan;
    }

    public async Task<ArkiveModImportResult> ImportAsync(
        string arkivePath,
        bool replace = false,
        CancellationToken cancellationToken = default)
    {
        var import = await _library.ImportAsync(arkivePath, replace, cancellationToken).ConfigureAwait(false);
        await ArkiveModSelectionService.ReleaseAsync(_edits, import.Released, cancellationToken).ConfigureAwait(false);
        return import;
    }

    public static string PackageDisplayName(GuidedTexturePackageEntry package) =>
        string.IsNullOrWhiteSpace(package.Manifest.DisplayName)
            ? $"{package.Manifest.PackageAlias} texture pack"
            : package.Manifest.DisplayName.Trim();

    public static ArkiveModFileManifest CreateManifest(
        string modId,
        ArkiveModMetadata metadata,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt) => new(
        ArkiveModFileFormat.FormatId,
        ArkiveModFileFormat.CurrentFormatVersion,
        modId,
        metadata.Name.Trim(),
        string.IsNullOrWhiteSpace(metadata.Version) ? "1.0.0" : metadata.Version.Trim(),
        string.IsNullOrWhiteSpace(metadata.Author) ? null : metadata.Author.Trim(),
        string.IsNullOrWhiteSpace(metadata.Description) ? null : metadata.Description.Trim(),
        string.IsNullOrWhiteSpace(metadata.Website) ? null : metadata.Website.Trim(),
        (metadata.Tags ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        ArkiveModBundleService.WesternGlobalProfile,
        createdAt,
        updatedAt < createdAt ? createdAt : updatedAt,
        CurrentCreatedWith,
        []);

    private static IReadOnlyList<ArkiveModAssetBinding> Bindings(
        ArkiveModFileManifest written,
        IReadOnlyList<ArkiveModCapturedAsset> captured) => captured.Select(item =>
    {
        var asset = written.Assets.Single(candidate => candidate.AssetId.Equals(
            item.Descriptor.AssetId, StringComparison.OrdinalIgnoreCase));
        return new ArkiveModAssetBinding(
            asset.AssetId, asset.Kind, item.Edit.EditId, item.Edit.SourcePackagePath,
            ArkiveModFileService.ComputeContentSha256(asset));
    }).ToArray();

    private static void RequireUnowned(
        ArkiveModLibraryManifest library,
        IEnumerable<ArkiveModCapturedAsset> assets,
        string? ownModId)
    {
        foreach (var asset in assets)
        {
            var owner = library.FindOwner(asset.Edit.EditId);
            if (owner is not null && (ownModId is null ||
                                      !owner.Value.Mod.ModId.Equals(ownModId, StringComparison.OrdinalIgnoreCase)))
                Refuse("AUT070",
                    $"{asset.Edit.DisplayName} already belongs to mod '{owner.Value.Mod.Manifest.Name}'. Remove it there first.");
        }
    }

    private static async Task RequireFileHashAsync(
        string path, string expectedSha256, string displayName, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            Refuse("AUT012", $"{displayName}'s edited file is missing: {path}");
        var hash = await ArkiveModEditSnapshot.HashFileCachedAsync(path, cancellationToken).ConfigureAwait(false);
        if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("AUT013", $"{displayName}'s edited file changed after preflight. Revalidate it before saving the mod.");
    }

    private static string PhysicalName(string sourcePackagePath) =>
        ArkiveModFileService.NormalizePhysicalPackageName(Path.GetFileName(sourcePackagePath));

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new ArkiveModRefusalException(code, message);
}
