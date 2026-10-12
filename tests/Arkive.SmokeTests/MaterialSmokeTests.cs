namespace Arkive.SmokeTests;

internal static class MaterialSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("edits only proven MaterialInstanceConstant values and object references", TestMaterialInstanceEditing),
    ];

    private static void TestMaterialInstanceEditing()
    {
        var path = Path.Combine(fixtureDirectory, "material-client", "EFGame", "ReleasePC",
            "Packages", "material-instance.upk");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var range = WriteMaterialInstancePackage(path);
        var service = new MaterialInstanceReconnaissanceService();
        var report = service.InspectAsync(new MaterialInstanceReconnaissanceRequest(
                path, "material_instance", 1, AssetKind.Material,
                "MaterialInstanceConstant", "TestMaterial", range.SerialOffset, range.SerialSize))
            .GetAwaiter().GetResult();
        Assert(report.Readiness == MaterialInstanceReadiness.Editable &&
               report.ScalarParameters is [{ Name: "Roughness", Value: 0.5f }] &&
               report.VectorParameters is [{ Name: "Tint", Red: 1f, Green: 0.5f, Blue: 0.25f, Alpha: 1f }] &&
               report.TextureParameters is [{ Name: "DiffuseTexture", ObjectReference: 0 }] &&
               report.ParentObjectReference == 0 &&
               report.Issues.Any(issue => issue.Code == "MAT122"),
            "native MaterialInstanceConstant nested parameter arrays were not proven");
        var recon = new MaterialInstanceWorkspaceStore(Path.Combine(fixtureDirectory, "material-recon"))
            .SaveAsync(report).GetAwaiter().GetResult();
        var editService = new MaterialInstanceEditService();
        var edit = editService.CreateEditAsync(recon.JsonPath,
                Path.Combine(fixtureDirectory, "material-edits"), "Fixture material")
            .GetAwaiter().GetResult();
        var referenceCatalog = editService.GetReferenceCatalog(report);
        var alternateTexture = referenceCatalog.TextureTargets.Single(option =>
            option.ObjectReference == -2 && option.ClassName == "Texture2D");
        var alternateParent = referenceCatalog.ParentTargets.Single(option =>
            option.ObjectReference == -3 && option.ClassName == "MaterialInstanceConstant");
        var changed = edit.Document with
        {
            Scalars = edit.Document.Scalars.Select(item => item with { Value = 0.75f }).ToArray(),
            Vectors = edit.Document.Vectors.Select(item => item with { Blue = 0.75f }).ToArray(),
            Textures = edit.Document.Textures.Select(item => item with
            {
                ObjectReference = alternateTexture.ObjectReference,
                ResolvedObject = alternateTexture.ResolvedObject,
                TargetIdentity = alternateTexture.StableIdentity,
            }).ToArray(),
            Parent = edit.Document.Parent! with
            {
                ObjectReference = alternateParent.ObjectReference,
                ResolvedObject = alternateParent.ResolvedObject,
                TargetIdentity = alternateParent.StableIdentity,
            },
        };
        File.WriteAllText(edit.DocumentPath, JsonSerializer.Serialize(changed, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        }));
        var candidate = editService.BuildCandidateAsync(edit.DocumentPath,
                Path.Combine(fixtureDirectory, "material-candidates"))
            .GetAwaiter().GetResult();
        var bytes = File.ReadAllBytes(candidate.CandidatePath);
        var scalar = report.ScalarParameters.Single();
        var vector = report.VectorParameters.Single();
        var texture = report.TextureParameters.Single();
        var parent = report.Properties.Single(property =>
            property.Name.Equals("Parent", StringComparison.OrdinalIgnoreCase));
        Assert(candidate.Manifest.SameSize && candidate.Manifest.LockedBytesPreserved &&
               candidate.Manifest.ChangedScalarCount == 1 && candidate.Manifest.ChangedVectorCount == 1 &&
               candidate.Manifest.ChangedTextureReferenceCount == 1 &&
               candidate.Manifest.ParentReferenceChanged && candidate.Manifest.ReferenceChanges.Count == 2 &&
               BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                   bytes.AsSpan(checked((int)scalar.ValueRelativeOffset), 4))) == 0.75f &&
               BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                   bytes.AsSpan(checked((int)vector.ValueRelativeOffset + 8), 4))) == 0.75f &&
               BinaryPrimitives.ReadInt32LittleEndian(
                   bytes.AsSpan(checked((int)texture.ValueRelativeOffset), 4)) == -2 &&
               BinaryPrimitives.ReadInt32LittleEndian(
                   bytes.AsSpan(checked((int)parent.ValueRelativeOffset), 4)) == -3,
            "material candidate did not patch only the requested proven values and references");
        var source = File.ReadAllBytes(path).AsSpan(range.SerialOffset, range.SerialSize).ToArray();
        var writable = new HashSet<int>();
        foreach (var index in Enumerable.Range(checked((int)scalar.ValueRelativeOffset), 4)) writable.Add(index);
        foreach (var index in Enumerable.Range(checked((int)vector.ValueRelativeOffset), 16)) writable.Add(index);
        foreach (var index in Enumerable.Range(checked((int)texture.ValueRelativeOffset), 4)) writable.Add(index);
        foreach (var index in Enumerable.Range(checked((int)parent.ValueRelativeOffset), 4)) writable.Add(index);
        Assert(Enumerable.Range(0, bytes.Length).All(index => writable.Contains(index) || bytes[index] == source[index]),
            "material candidate changed a byte outside proven scalar/vector/object-reference ranges");

        var materialWorkspace = new MaterialInstanceEditWorkspaceStore(
            Path.Combine(fixtureDirectory, "material-edits"));
        var persistentEdit = materialWorkspace.LoadAsync().GetAwaiter().GetResult().Single();
        persistentEdit = materialWorkspace.RecordCandidateAsync(
                persistentEdit with { Document = changed }, candidate)
            .GetAwaiter().GetResult();
        Assert(persistentEdit.Document.IncludedInPackageBuild &&
               persistentEdit.Document.LatestCandidateSha256 == candidate.Manifest.CandidateExportSha256,
            "material project did not persist its verified candidate and package selection");
        var packageWorkspace = new GuidedTexturePackageWorkspace(
            Path.Combine(fixtureDirectory, "material-package-workspace"));
        var packageProject = packageWorkspace.GetOrCreateAsync(
                report.PackageAlias, Path.GetFileName(path), path)
            .GetAwaiter().GetResult();
        var composer = new UnifiedPackageComposerService(
            new UpkPackageReader(), NoChunkProtectionProvider.Instance,
            new SkeletalMeshReconnaissanceService(), new SkeletalMeshEditableExportService(),
            new SkeletalMeshNativeEncodingService(), new SkeletalMeshStagedUpkService(), null!,
            new ArkivePackageModBundleService(
                new SyntheticModPackageExternalValidator(),
                protectionProvider: NoChunkProtectionProvider.Instance));
        var composition = composer.BuildAsync(
                new UnifiedPackageCompositionRequest(
                    packageProject, [], [], Path.Combine(fixtureDirectory, "material-composition"),
                    Path.Combine(fixtureDirectory, "material-composition-backups"),
                    "Material-only smoke test")
                {
                    MaterialEdits = [persistentEdit],
                })
            .GetAwaiter().GetResult();
        var compositionVerification = UnifiedPackageComposerService.VerifyOutputAsync(
                composition.CompositionManifestPath)
            .GetAwaiter().GetResult();
        var materialBundle = ArkiveModBundleService.LoadBundleAsync(
                composition.Revision.ModBundleManifestPath)
            .GetAwaiter().GetResult();
        Assert(composition.Manifest.MaterialCount == 1 && composition.Manifest.MaterialsAppliedBeforeMeshes &&
               composition.Manifest.Stages is [{ Kind: UnifiedPackageAssetKind.MaterialInstanceConstant }] &&
               composition.Revision.MaterialAssets is [{ EditId: var builtEdit }] &&
               builtEdit == persistentEdit.Document.EditId &&
               compositionVerification.OutputUpkPath == composition.Revision.OutputUpkPath &&
               materialBundle.Assets is [{ ClassName: "MaterialInstanceConstant" }] &&
               materialBundle.InstallationEnabled,
            "unified package composer did not produce a verified material-only payload");
        var deployment = new ArkiveModDeploymentService(
            processGuard: new SyntheticModDeploymentProcessGuard());
        var deploymentRequest = new ArkiveModDeploymentRequest(
            composition.Revision.ModBundleManifestPath,
            Path.Combine(fixtureDirectory, "material-composition-backups"),
            Path.Combine(fixtureDirectory, "material-composition-state"));
        var installed = deployment.InstallAsync(deploymentRequest).GetAwaiter().GetResult();
        var restored = deployment.RestoreAsync(deploymentRequest).GetAwaiter().GetResult();
        Assert(installed.Inspection.State == ModInstallationState.Installed &&
               restored.Inspection.State == ModInstallationState.Restored &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == report.PackageSha256,
            "material-only bundle did not install and restore its clean source exactly");

        var rebasedMaterial = materialWorkspace.RebaseSourceAsync(
                persistentEdit, report, recon.JsonPath)
            .GetAwaiter().GetResult();
        Assert(rebasedMaterial.Document.Textures.Single().ObjectReference == -2 &&
               rebasedMaterial.Document.Parent?.ObjectReference == -3 &&
               rebasedMaterial.Document.LatestCandidateManifestPath is null,
            "material rebase did not preserve stable texture/parent target identities and invalidate its candidate");

        var wrongClass = changed with
        {
            Textures = changed.Textures.Select(item => item with
            {
                ObjectReference = alternateParent.ObjectReference,
                ResolvedObject = alternateParent.ResolvedObject,
                TargetIdentity = alternateParent.StableIdentity,
            }).ToArray(),
        };
        File.WriteAllText(edit.DocumentPath, JsonSerializer.Serialize(wrongClass));
        try
        {
            _ = editService.BuildCandidateAsync(edit.DocumentPath,
                    Path.Combine(fixtureDirectory, "material-wrong-class-candidate"))
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("material candidate accepted a non-texture texture target");
        }
        catch (MaterialInstanceEditRefusalException)
        {
        }

        var tampered = changed with
        {
            Scalars = changed.Scalars.Select(item => item with { SourceValue = 0.25f }).ToArray(),
        };
        File.WriteAllText(edit.DocumentPath, JsonSerializer.Serialize(tampered));
        try
        {
            _ = editService.BuildCandidateAsync(edit.DocumentPath,
                    Path.Combine(fixtureDirectory, "material-tamper-candidate"))
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("material candidate accepted a tampered locked source value");
        }
        catch (MaterialInstanceEditRefusalException)
        {
        }
    }

}
