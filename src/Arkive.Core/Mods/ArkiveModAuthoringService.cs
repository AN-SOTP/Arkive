using System.Text.Json;
using Arkive.Core.Interface;
using Arkive.Core.Materials;
using Arkive.Core.Meshes;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

public sealed record ArkiveModCapturedAsset(
    ArkiveModFileAsset Descriptor,
    IReadOnlyList<ArkiveModPackFile> Files,
    ArkiveModLocalEdit Edit);

/// <summary>What deleting a mod removed besides its file: working files, exports, drafts and unpacked contents.</summary>
/// <param name="KeptEditIds">Working files it was bound to that another mod or a draft still names; they are only unchecked.</param>
public sealed record ArkiveModRemoval(
    IReadOnlyList<string> DeletedEditIds,
    IReadOnlyList<string> KeptEditIds,
    IReadOnlyList<string> DeletedExports,
    IReadOnlyList<string> DeletedDraftIds,
    IReadOnlyList<string> DeletedUnpackedFolders);

/// <summary>What releasing a mod's working files would delete; it is refused while <see cref="Problems"/> is not empty.</summary>
/// <param name="Folders">Working-file folders, game-mesh exports and unpacked files that would go.</param>
public sealed record ArkiveModReleasePlan(
    string ModId,
    string ModName,
    IReadOnlyList<string> EditIds,
    IReadOnlyList<string> Folders,
    long Bytes,
    IReadOnlyList<string> Problems);

/// <summary>What releasing a mod's working files deleted.</summary>
/// <param name="KeptEditIds">Working files that could not be deleted; their bindings stay.</param>
public sealed record ArkiveModRelease(
    IReadOnlyList<string> DeletedEditIds,
    IReadOnlyList<string> KeptEditIds,
    IReadOnlyList<string> DeletedExports,
    IReadOnlyList<string> DeletedDraftIds,
    IReadOnlyList<string> DeletedUnpackedFolders);

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

    /// <summary>
    /// Finds the clean backup of a game package a mod is installed in. When set, saving first moves working copies whose
    /// game package has since been rebuilt back onto that backup, so a mod can be saved again after it was applied.
    /// </summary>
    public ArkiveModCleanSources.Resolver? CleanPackageResolver { get; init; }

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
            Particles.ParticleEffectEditEntry particle => CaptureParticle(edit, particle, id),
            Physics.PhysicsAssetEditEntry physics => CapturePhysics(edit, physics, id),
            InterfaceMovieEditEntry movie => await CaptureInterfaceMovieAsync(edit, movie, id, cancellationToken)
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

    private static async Task<ArkiveModCapturedAsset> CaptureInterfaceMovieAsync(
        ArkiveModLocalEdit edit,
        InterfaceMovieEditEntry movie,
        string assetId,
        CancellationToken cancellationToken)
    {
        var manifest = movie.Manifest;
        try
        {
            await InterfaceMovieEditWorkspaceStore.ReadEditedMovieAsync(movie, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            Refuse("AUT035", exception.Message);
        }
        var descriptor = new ArkiveModFileAsset(
            assetId, UnifiedPackageAssetKind.InterfaceMovie, manifest.DisplayName,
            new ArkiveModAssetTarget(
                manifest.PackageAlias, PhysicalName(manifest.SourcePackagePath), manifest.ClassName, manifest.ObjectName,
                manifest.NativeExportIndex, manifest.SourcePackageSha256.ToUpperInvariant(),
                manifest.SourceExportSha256.ToUpperInvariant()),
            []);
        return new ArkiveModCapturedAsset(
            descriptor,
            [new ArkiveModPackFile(ArkiveModFileFormat.MovieRole, manifest.EditedMoviePath, manifest.ObjectName + GfxMovieFile.Extension)],
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
            Refuse("AUT020", $"{manifest.DisplayName} failed mesh preflight; resolve its errors before saving it to a mod. " +
                string.Join(" ", manifest.Preflight.Issues.Where(issue => issue.Severity == SkeletalMeshEditIssueSeverity.Error)
                    .Take(3).Select(issue => $"{issue.Code}: {issue.Message}")));
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

    private static ArkiveModCapturedAsset CaptureParticle(ArkiveModLocalEdit edit, Particles.ParticleEffectEditEntry particle, string assetId)
    {
        var document = particle.Document;
        if (document.Patches.Count == 0)
            Refuse("AUT045", $"{document.DisplayName} changes nothing in {document.SystemObjectName} yet. Open it with Edit effect and change a setting first.");
        var descriptor = new ArkiveModFileAsset(
            assetId, UnifiedPackageAssetKind.ParticleSystem, document.DisplayName,
            new ArkiveModAssetTarget(
                document.PackageAlias, PhysicalName(document.SourcePackagePath), "particlesystem",
                document.SystemObjectName, document.SystemExportIndex, document.SourcePackageSha256.ToUpperInvariant(),
                document.SystemExportSha256.ToUpperInvariant()),
            [])
        {
            Particle = new ArkiveModParticleData(document.Recipe, document.Patches),
        };
        return new ArkiveModCapturedAsset(descriptor, [], edit);
    }

    private static ArkiveModCapturedAsset CapturePhysics(ArkiveModLocalEdit edit, Physics.PhysicsAssetEditEntry physics, string assetId)
    {
        var document = physics.Document;
        if (document.Patches.Count == 0)
            Refuse("AUT046", $"{document.DisplayName} changes nothing in {document.AssetObjectName}.");
        var descriptor = new ArkiveModFileAsset(
            assetId, UnifiedPackageAssetKind.PhysicsAsset, document.DisplayName,
            new ArkiveModAssetTarget(
                document.PackageAlias, PhysicalName(document.SourcePackagePath), "physicsasset",
                document.AssetObjectName, document.AssetExportIndex, document.SourcePackageSha256.ToUpperInvariant(),
                document.AssetExportSha256.ToUpperInvariant()),
            [])
        {
            Physics = new ArkiveModPhysicsData(document.Fit, document.Patches),
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
            if (packAssets.Count + carried.Count == 0 && current.Manifest.GameData is null)
                Refuse("AUT062", "A mod must keep at least one asset. Remove the mod instead.");

            var name = metadata ?? new ArkiveModMetadata(
                current.Manifest.Name, current.Manifest.Version, current.Manifest.Author,
                current.Manifest.Description, current.Manifest.Website, current.Manifest.Tags);
            var manifest = CreateManifest(current.Manifest.ModId, name, current.Manifest.CreatedAt, DateTimeOffset.UtcNow) with
            {
                GameData = current.Manifest.GameData,
                // Item looks go with the outfit meshes; a mod left with no assets has none to go with.
                Looks = packAssets.Count + carried.Count == 0 ? null : current.Manifest.Looks,
            };
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

    /// <param name="skippedMeshes">
    /// When given, meshes that fail their check are left out of the mod (each is added here with its first problem)
    /// instead of refusing the whole save; used when one batch reshapes hundreds of pieces.
    /// </param>
    /// <param name="parallelism">How many working assets to check at the same time (each check is independent).</param>
    /// <param name="progress">Each working asset checked, then collected, then the mod file being written.</param>
    public async Task<ArkiveModLibraryEntry> SaveDraftAsync(
        ArkiveModAuthoringDraft draft, CancellationToken cancellationToken = default, ICollection<string>? skippedMeshes = null,
        int parallelism = 1, IProgress<ArkiveModSaveProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Metadata.Name)) Refuse("AUT090", "Give the mod a name before saving.");
        if (CleanPackageResolver is not null)
            await ArkiveModCleanSources.RepointAsync(_edits, draft.EditIds, CleanPackageResolver, cancellationToken)
                .ConfigureAwait(false);
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_edits, cancellationToken).ConfigureAwait(false);
        var locals = draft.EditIds.Select(editId => snapshot.Find(editId) ??
            throw new ArkiveModRefusalException("AUT091", "A working asset is missing. Prepare the mod for editing again.")).ToArray();
        var validated = new ArkiveModLocalEdit[locals.Length];
        var checkedCount = 0;
        progress?.Report(new ArkiveModSaveProgress(ArkiveModSaveStage.Checking, 0, locals.Length));
        if (parallelism > 1)
            await Parallel.ForEachAsync(Enumerable.Range(0, locals.Length),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                async (index, token) =>
                {
                    validated[index] = await ValidateEditAsync(locals[index], token).ConfigureAwait(false);
                    progress?.Report(new ArkiveModSaveProgress(ArkiveModSaveStage.Checking, Interlocked.Increment(ref checkedCount),
                        locals.Length));
                })
                .ConfigureAwait(false);
        else
            for (var index = 0; index < locals.Length; index++)
            {
                validated[index] = await ValidateEditAsync(locals[index], cancellationToken).ConfigureAwait(false);
                progress?.Report(new ArkiveModSaveProgress(ArkiveModSaveStage.Checking, index + 1, locals.Length));
            }
        var captured = new List<ArkiveModCapturedAsset>();
        for (var index = 0; index < validated.Length; index++)
        {
            var local = validated[index];
            progress?.Report(new ArkiveModSaveProgress(ArkiveModSaveStage.Collecting, index, validated.Length));
            if (skippedMeshes is not null && ArkiveModEditCheck.Read(local) is { Passed: false } check)
            {
                skippedMeshes.Add($"{local.DisplayName}: {check.Problems.FirstOrDefault() ?? "failed the mesh check"}");
                continue;
            }
            captured.Add(await CaptureAsync(local, cancellationToken: cancellationToken).ConfigureAwait(false));
        }
        // Two copies of one asset that make the same change (added twice) are kept once; copies that disagree are
        // the user's to choose between.
        captured = captured.GroupBy(asset => (ArkiveModTargetKey.From(asset.Descriptor), ArkiveModFileService.ComputeContentSha256(asset.Descriptor)))
            .Select(group => group.First()).ToList();
        if (captured.GroupBy(asset => ArkiveModTargetKey.From(asset.Descriptor)).Where(group => group.Count() > 1).ToArray() is { Length: > 0 } clashes)
            Refuse("AUT092", "The mod has different working copies of the same asset: " +
                             string.Join(", ", clashes.Take(5).Select(group => group.First().Descriptor.DisplayName)) +
                             (clashes.Length > 5 ? $" and {clashes.Length - 5:N0} more" : string.Empty) +
                             ". Remove all but one copy of each.");
        progress?.Report(new ArkiveModSaveProgress(ArkiveModSaveStage.Writing, 0, 0));
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

    internal static async Task<string?> PortableContentSha256Async(
        ArkiveModLocalEdit edit,
        CancellationToken cancellationToken)
    {
        if (edit.Entry is ArkiveModIndexedEdit indexed) return indexed.PortableSha256;
        if (edit.Entry is Particles.ParticleEffectEditEntry particle) return particle.Document.ContentSha256;
        if (edit.Entry is Physics.PhysicsAssetEditEntry physics) return physics.Document.ContentSha256;
        if (edit.Entry is not MaterialInstanceEditEntry material)
        {
            var path = edit.Entry switch
            {
                GuidedTextureModProjectEntry texture => texture.Manifest.EditableImagePath,
                SkeletalMeshEditWorkspaceEntry mesh => mesh.EditedGlbPath,
                StaticMeshEditEntry mesh => mesh.Manifest.EditedGlbPath,
                InterfaceMovieEditEntry movie => movie.Manifest.EditedMoviePath,
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

    /// <summary>
    /// Where game-mesh exports are kept. When set, deleting a mod also deletes the exports that only its edited meshes
    /// were made from.
    /// </summary>
    public SkeletalMeshEditableWorkspaceStore? MeshExports { get; init; }

    /// <summary>The authoring drafts; by default the ones in the library's authoring folder.</summary>
    public ArkiveModAuthoringWorkspace? Drafts { get; init; }

    /// <summary>
    /// Deletes a mod's .arkive file and library entry, whatever its origin, and the working files it leaves that
    /// nothing else can use: each working file it was bound to that no other mod's binding or other draft names, the
    /// game-mesh exports only those edited meshes were made from, its own draft, and its unpacked contents. Callers
    /// must take the mod out of the game first. A working file that is kept is released (unchecked) as before.
    /// </summary>
    public async Task<ArkiveModRemoval> RemoveModAsync(string modId, CancellationToken cancellationToken = default)
    {
        var library = await _library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var mod = library.Find(modId) ??
                  throw new ArkiveModRefusalException("AUT060", $"Mod {modId} is not in the library.");
        var drafts = Drafts ?? new ArkiveModAuthoringWorkspace(Path.Combine(_library.StateDirectory, "authoring"));
        var allDrafts = await drafts.LoadAsync(cancellationToken).ConfigureAwait(false);
        var own = allDrafts.Where(draft => mod.ModId.Equals(draft.ModId, StringComparison.OrdinalIgnoreCase)).ToArray();
        // A draft that cannot be read might name any working file, so then none is deleted.
        var draftsUnreadable = drafts.LoadIssues.Count > 0;
        var named = allDrafts.Except(own)
            .SelectMany(draft => draft.EditIds.Concat(draft.ReferenceEditIds).Concat(draft.SavedBindings.Values))
            .Concat(library.Mods.Where(other => !other.ModId.Equals(mod.ModId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(other => other.Bindings.Select(binding => binding.EditId)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_edits, cancellationToken).ConfigureAwait(false);
        var released = mod.Bindings
            .Select(binding => new ArkiveModReleasedEdit(binding.EditId, !draftsUnreadable && !named.Contains(binding.EditId)))
            .ToArray();
        await ArkiveModSelectionService.ReleaseAsync(_edits, released, cancellationToken, snapshot).ConfigureAwait(false);
        var deleted = released.Where(item => item.Delete && snapshot.Find(item.EditId) is { } edit && !File.Exists(edit.ManifestPath))
            .Select(item => item.EditId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = released.Where(item => snapshot.Find(item.EditId) is not null && !deleted.Contains(item.EditId))
            .Select(item => item.EditId).ToArray();

        var exports = await DeleteUnusedExportsAsync(snapshot, deleted, cancellationToken).ConfigureAwait(false);
        foreach (var draft in own) await drafts.DeleteAsync(draft.DraftId, cancellationToken).ConfigureAwait(false);
        var unpacked = await DeleteUnpackedContentsAsync(mod, library, snapshot, kept, cancellationToken).ConfigureAwait(false);
        await _library.RemoveAsync(modId, deleteFile: true, cancellationToken).ConfigureAwait(false);
        return new ArkiveModRemoval(deleted.ToArray(), kept, exports, own.Select(draft => draft.DraftId).ToArray(), unpacked);
    }

    /// <summary>The game-mesh exports that only the deleted edited meshes were made from, unless one was changed by hand.</summary>
    private async Task<IReadOnlyList<string>> DeleteUnusedExportsAsync(
        ArkiveModEditSnapshot before, IReadOnlySet<string> deleted, CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        foreach (var export in await UnusedExportsAsync(before, deleted, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                Directory.Delete(export, recursive: true);
                var parent = Path.GetDirectoryName(export)!;
                if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
                removed.Add(export);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// <summary>
    /// The game-mesh exports only the given edited meshes were made from, unless one was changed by hand. While those
    /// edits still exist (a plan made before deleting them), they do not count as using their exports.
    /// </summary>
    private async Task<IReadOnlyList<string>> UnusedExportsAsync(
        ArkiveModEditSnapshot before, IReadOnlySet<string> deleted, CancellationToken cancellationToken)
    {
        if (MeshExports is null || deleted.Count == 0) return [];
        var root = Path.GetFullPath(MeshExports.RootDirectory);
        string? ExportOf(SkeletalMeshEditWorkspaceEntry mesh)
        {
            var full = Path.GetFullPath(mesh.Manifest.SourceManifestPath);
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;
            var parts = Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar);
            return parts.Length >= 3 ? Path.Combine(root, parts[0], parts[1]) : null;
        }
        var meshes = await _edits.SkeletalMeshEdits.LoadAsync(cancellationToken).ConfigureAwait(false);
        // An edited mesh whose record cannot be read may have been made from any export.
        if (meshes.Issues.Count > 0) return [];
        var stillUsed = meshes.Entries.Where(mesh => !deleted.Contains(mesh.Manifest.EditId))
            .Select(ExportOf).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = before.Edits.Where(edit => deleted.Contains(edit.EditId))
            .Select(edit => edit.Entry).OfType<SkeletalMeshEditWorkspaceEntry>()
            .Select(ExportOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length == 0) return [];
        var editable = await MeshExports.LoadAsync(cancellationToken).ConfigureAwait(false);
        var handEdited = editable.Entries
            .Where(entry => !entry.GlbExists || !entry.GlbLengthMatches ||
                            File.GetLastWriteTimeUtc(entry.GlbPath) > File.GetLastWriteTimeUtc(entry.ManifestPath).AddSeconds(5))
            .Select(entry => entry.ManifestPath).Concat(editable.Issues.Select(issue => issue.ManifestPath))
            .ToArray();
        return candidates.Where(export => !stillUsed.Contains(export) && Directory.Exists(export) &&
                                          !handEdited.Any(path => path.StartsWith(export + Path.DirectorySeparatorChar,
                                              StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    /// <summary>
    /// The mod's unpacked contents under staging\mod-library\materialized, unless another mod shares the folder or a
    /// working file kept from it, or any texture project, still names a path inside.
    /// </summary>
    private async Task<IReadOnlyList<string>> DeleteUnpackedContentsAsync(
        ArkiveModLibraryEntry mod, ArkiveModLibraryManifest library, ArkiveModEditSnapshot snapshot,
        IReadOnlyCollection<string> keptEditIds, CancellationToken cancellationToken)
    {
        if (await UnpackedContentsAsync(mod, library, snapshot, keptEditIds, new HashSet<string>(), cancellationToken).ConfigureAwait(false)
            is not { } folder) return [];
        try
        {
            Directory.Delete(folder, recursive: true);
            return [folder];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The folder <see cref="DeleteUnpackedContentsAsync"/> would delete, or null. Records in
    /// <paramref name="goingRecords"/> belong to working files about to be deleted and do not keep it.
    /// </summary>
    private async Task<string?> UnpackedContentsAsync(
        ArkiveModLibraryEntry mod, ArkiveModLibraryManifest library, ArkiveModEditSnapshot snapshot,
        IReadOnlyCollection<string> keptEditIds, IReadOnlySet<string> goingRecords, CancellationToken cancellationToken)
    {
        var prefix = mod.ModId[..Math.Min(8, mod.ModId.Length)];
        var folder = Path.GetFullPath(Path.Combine(_library.MaterializationRoot, prefix));
        if (!Directory.Exists(folder) || prefix.Length < 8 ||
            library.Mods.Any(other => !other.ModId.Equals(mod.ModId, StringComparison.OrdinalIgnoreCase) &&
                                      other.ModId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return null;
        var kept = keptEditIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var records = snapshot.Edits
            .Where(edit => kept.Contains(edit.EditId) || edit.Kind == UnifiedPackageAssetKind.Texture2D)
            .Select(edit => edit.ManifestPath).Where(path => File.Exists(path) && !goingRecords.Contains(path));
        var escaped = folder.Replace(@"\", @"\\");
        foreach (var record in records)
        {
            var text = await File.ReadAllTextAsync(record, cancellationToken).ConfigureAwait(false);
            if (text.Contains(folder, StringComparison.OrdinalIgnoreCase) || text.Contains(escaped, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return folder;
    }

    /// <summary>
    /// What releasing a disabled mod's working files would delete, and why it cannot when it cannot (see
    /// <see cref="ReleaseWorkingFilesAsync"/>). <paramref name="installs"/> tells which builds are in the game.
    /// </summary>
    public async Task<ArkiveModReleasePlan> PlanReleaseAsync(
        string modId, ArkiveModApplyContext installs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installs);
        var library = await _library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var mod = library.Find(modId) ??
                  throw new ArkiveModRefusalException("AUT070", $"Mod {modId} is not in the library.");
        var problems = new List<string>();
        if (mod.Enabled) problems.Add("It is turned on. Turn it off and choose Apply changes first.");
        // The working files are set up again from the .arkive file, so it must be there, as the library knows it.
        var file = _library.GetModFilePath(mod);
        if (!File.Exists(file))
            problems.Add("Its .arkive file is missing from the mods folder, so it could not be set up again.");
        else if (!(await ArkiveModEditSnapshot.HashFileCachedAsync(file, cancellationToken).ConfigureAwait(false))
                     .Equals(mod.FileSha256, StringComparison.OrdinalIgnoreCase))
            problems.Add("Its .arkive file changed since the library last read it. Refresh the mod list first.");
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_edits, cancellationToken).ConfigureAwait(false);
        var bound = mod.Bindings.Select(binding => (Binding: binding, Edit: snapshot.Find(binding.EditId)))
            .Where(item => item.Edit is not null).Select(item => (item.Binding, Edit: item.Edit!)).ToArray();
        string AssetName(ArkiveModAssetBinding binding) =>
            mod.Manifest.Assets.FirstOrDefault(asset => asset.AssetId.Equals(binding.AssetId, StringComparison.OrdinalIgnoreCase))
                ?.DisplayName ?? binding.AssetId;

        // In the game: an installed build of a package it targets holds one of its working files.
        var inGame = await EditIdsInGameAsync(installs, library, mod, cancellationToken).ConfigureAwait(false);
        if (bound.Any(item => inGame.Contains(item.Binding.EditId)))
            problems.Add("Its assets are in the game. Turn it off and choose Apply changes first.");

        var drafts = Drafts ?? new ArkiveModAuthoringWorkspace(Path.Combine(_library.StateDirectory, "authoring"));
        var allDrafts = await drafts.LoadAsync(cancellationToken).ConfigureAwait(false);
        var own = allDrafts.Where(draft => mod.ModId.Equals(draft.ModId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (drafts.LoadIssues.Count > 0)
            problems.Add("An authoring draft cannot be read, so Arkive cannot tell whether it uses these working files.");
        if (own.Any(draft => Unsaved(draft, mod)))
            problems.Add("It is open for editing with changes that are not saved to its .arkive file. Save or discard them first.");
        var named = allDrafts.Except(own)
            .SelectMany(draft => draft.EditIds.Concat(draft.ReferenceEditIds).Concat(draft.SavedBindings.Values))
            .Concat(library.Mods.Where(other => !other.ModId.Equals(mod.ModId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(other => other.Bindings.Select(binding => binding.EditId)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (binding, edit) in bound)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (named.Contains(binding.EditId))
                problems.Add($"{AssetName(binding)}: another mod or a draft uses its working file.");
            var content = await PortableContentSha256Async(edit, cancellationToken).ConfigureAwait(false);
            if (content is null)
                problems.Add($"{AssetName(binding)}: its working file cannot be read.");
            else if (!content.Equals(binding.PackedContentSha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{AssetName(binding)}: it has changes that are not saved to the .arkive file.");
        }
        if (bound.Length == 0) problems.Add("It has no working files on this computer.");

        var folders = new List<string>();
        foreach (var (binding, edit) in bound)
        {
            try
            {
                folders.AddRange(WorkingFolders(edit));
            }
            catch (IOException)
            {
                problems.Add($"{AssetName(binding)}: its builds are not where Arkive keeps them.");
            }
        }
        var deleted = bound.Select(item => item.Binding.EditId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        folders.AddRange(await UnusedExportsAsync(snapshot, deleted, cancellationToken).ConfigureAwait(false));
        var going = bound.Select(item => item.Edit.ManifestPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (await UnpackedContentsAsync(mod, library, snapshot, [], going, cancellationToken).ConfigureAwait(false) is { } unpacked)
            folders.Add(unpacked);
        folders = folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var bytes = folders.Sum(folder => new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(file => file.Length));
        return new ArkiveModReleasePlan(mod.ModId, mod.Manifest.Name, bound.Select(item => item.Binding.EditId).ToArray(),
            folders, bytes, problems);
    }

    /// <summary>
    /// Deletes a disabled mod's working files, so they take no space until the mod is used again: Apply (or Prepare for
    /// editing) then sets them up from the .arkive file, as for a freshly imported mod. Refused, with nothing deleted,
    /// unless the mod is off and out of the game, every working file still holds exactly what its .arkive file holds,
    /// no other mod's binding or draft names one, and the mod has no draft with unsaved changes. Also deletes the
    /// game-mesh exports only those edits were made from, the mod's unpacked files when nothing else uses them, and its
    /// unchanged draft; then clears the mod's bindings, so its assets show as not set up.
    /// </summary>
    public async Task<ArkiveModRelease> ReleaseWorkingFilesAsync(
        string modId, ArkiveModApplyContext installs, CancellationToken cancellationToken = default)
    {
        var plan = await PlanReleaseAsync(modId, installs, cancellationToken).ConfigureAwait(false);
        if (plan.Problems.Count > 0)
            throw new ArkiveModRefusalException("AUT071", "The working files were not released: " + string.Join(" ", plan.Problems));
        var library = await _library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var mod = library.Find(modId)!;
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_edits, cancellationToken).ConfigureAwait(false);
        var released = plan.EditIds.Select(id => new ArkiveModReleasedEdit(id, Delete: true)).ToArray();
        await ArkiveModSelectionService.ReleaseAsync(_edits, released, cancellationToken, snapshot).ConfigureAwait(false);
        var deleted = plan.EditIds.Where(id => snapshot.Find(id) is { } edit && !File.Exists(edit.ManifestPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = plan.EditIds.Where(id => !deleted.Contains(id)).ToArray();
        var exports = await DeleteUnusedExportsAsync(snapshot, deleted, cancellationToken).ConfigureAwait(false);
        var drafts = Drafts ?? new ArkiveModAuthoringWorkspace(Path.Combine(_library.StateDirectory, "authoring"));
        var own = (await drafts.LoadAsync(cancellationToken).ConfigureAwait(false))
            .Where(draft => mod.ModId.Equals(draft.ModId, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var draft in own) await drafts.DeleteAsync(draft.DraftId, cancellationToken).ConfigureAwait(false);
        var unpacked = await DeleteUnpackedContentsAsync(mod, library, snapshot, kept, cancellationToken).ConfigureAwait(false);
        // A binding whose working file is gone (deleted now, or missing before) is cleared; one that could not be
        // deleted stays, so its asset stays set up.
        await _library.UpdateBindingsAsync(modId, current => current
            .Where(binding => !deleted.Contains(binding.EditId) && snapshot.Find(binding.EditId) is not null)
            .ToArray(), cancellationToken).ConfigureAwait(false);
        return new ArkiveModRelease(deleted.ToArray(), kept, exports, own.Select(draft => draft.DraftId).ToArray(), unpacked);
    }

    /// <summary>The draft differs from the saved mod: other assets, metadata or bindings.</summary>
    private static bool Unsaved(ArkiveModAuthoringDraft draft, ArkiveModLibraryEntry mod)
    {
        var bindings = mod.Bindings.Select(binding => binding.EditId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var manifest = mod.Manifest;
        return !bindings.SetEquals(draft.EditIds) || draft.ReferenceEditIds.Count > 0 || draft.RemovedAssetIds.Count > 0 ||
               draft.SavedBindings.Count != mod.Bindings.Count ||
               mod.Bindings.Any(binding => !draft.SavedBindings.TryGetValue(binding.AssetId, out var id) ||
                                           !id.Equals(binding.EditId, StringComparison.OrdinalIgnoreCase)) ||
               draft.Metadata.Name != manifest.Name || draft.Metadata.Version != manifest.Version ||
               (draft.Metadata.Author ?? string.Empty) != (manifest.Author ?? string.Empty) ||
               (draft.Metadata.Description ?? string.Empty) != (manifest.Description ?? string.Empty) ||
               (draft.Metadata.Website ?? string.Empty) != (manifest.Website ?? string.Empty);
    }

    /// <summary>The folders a working file lives in: its own, and a texture project's builds.</summary>
    private IEnumerable<string> WorkingFolders(ArkiveModLocalEdit edit) => edit.Entry switch
    {
        GuidedTextureModProjectEntry texture => [texture.DirectoryPath, .. _edits.Textures.OwnedRevisionDirectories(texture)],
        _ => [Path.GetDirectoryName(edit.ManifestPath)!],
    };

    /// <summary>The working files that installed builds of the mod's target packages hold.</summary>
    private static async Task<IReadOnlySet<string>> EditIdsInGameAsync(
        ArkiveModApplyContext installs, ArkiveModLibraryManifest library, ArkiveModLibraryEntry mod,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packages = await installs.Packages.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var physical in mod.Manifest.Assets
                     .Select(asset => ArkiveModFileService.NormalizePhysicalPackageName(asset.Target.PhysicalPackageName))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var project in packages.Where(package =>
                         Path.GetFileName(package.Manifest.SourcePackagePath).Equals(physical, StringComparison.OrdinalIgnoreCase) ||
                         ArkiveModFileService.NormalizePhysicalPackageName(package.Manifest.PhysicalPackageName)
                             .Equals(physical, StringComparison.OrdinalIgnoreCase)))
            {
                if (await installs.FindActiveDeploymentAsync(project, cancellationToken).ConfigureAwait(false) is not { } active)
                    continue;
                var revision = active.Revision;
                ids.UnionWith(revision.Assets.Select(asset => asset.ProjectId)
                    .Concat(revision.MeshAssets.Select(asset => asset.EditId))
                    .Concat(revision.StaticMeshAssets.Select(asset => asset.EditId))
                    .Concat(revision.MaterialAssets.Select(asset => asset.EditId))
                    .Concat(revision.ParticleAssets.Select(asset => asset.EditId))
                    .Concat(revision.PhysicsAssets.Select(asset => asset.EditId))
                    .Concat(revision.InterfaceMovieAssets.Select(asset => asset.EditId)));
            }
        }
        return ids;
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

public enum ArkiveModSaveStage
{
    /// <summary>Each working asset is checked again (meshes against the game's skeleton and animations).</summary>
    Checking,
    /// <summary>Each checked asset's files are gathered for the mod.</summary>
    Collecting,
    /// <summary>The .arkive file is written; it reports no count.</summary>
    Writing,
}

/// <summary>How far saving a mod has got: Done of Total in the current stage (Total is 0 when there is no count).</summary>
public sealed record ArkiveModSaveProgress(ArkiveModSaveStage Stage, int Done, int Total);
