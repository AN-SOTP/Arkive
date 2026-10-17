namespace Arkive.SmokeTests;

internal static class ArkiveModTestFixture
{
    internal sealed record ArkiveTestEnvironment(
        string Root,
        ArkiveModLibrary Library,
        ArkiveModEditWorkspaces Edits,
        GuidedTexturePackageWorkspace Packages,
        ArkiveModSetWorkspace ModSets,
        ArkiveModDeploymentService Deployment,
        MaterialInstanceWorkspaceStore MaterialReconnaissance,
        string MaterialCandidates,
        string Backups,
        string State);

    internal static ArkiveTestEnvironment CreateEnvironment(string root)
    {
        Directory.CreateDirectory(root);
        return new ArkiveTestEnvironment(
            root,
            new ArkiveModLibrary(Path.Combine(root, "mods"), Path.Combine(root, "staging", "mod-library")),
            new ArkiveModEditWorkspaces(
                new GuidedTextureModWorkspace(Path.Combine(root, "staging", "texture-mods")),
                new SkeletalMeshEditWorkspaceStore(Path.Combine(root, "staging", "skeletal-mesh-edits")),
                new StaticMeshEditWorkspaceStore(Path.Combine(root, "staging", "static-mesh-edits")),
                new MaterialInstanceEditWorkspaceStore(Path.Combine(root, "staging", "material-edits"))),
            new GuidedTexturePackageWorkspace(Path.Combine(root, "staging", "texture-mods", "_packages")),
            new ArkiveModSetWorkspace(Path.Combine(root, "staging", "mod-sets")),
            new ArkiveModDeploymentService(processGuard: new SyntheticModDeploymentProcessGuard()),
            new MaterialInstanceWorkspaceStore(Path.Combine(root, "staging", "material-reconnaissance")),
            Path.Combine(root, "staging", "material-candidates"),
            Path.Combine(root, "backups", "packages"),
            Path.Combine(root, "backups", "installations"));
    }

    internal static ArkiveModApplyContext Context(ArkiveTestEnvironment environment, string gamePackages)
    {
        environment.Library.SetGamePackagesDirectoryAsync(gamePackages).GetAwaiter().GetResult();
        return new ArkiveModApplyContext(
            environment.Library, environment.Edits, environment.Packages, environment.ModSets,
            environment.Deployment, environment.Backups, environment.State);
    }

    internal static ArkiveModMaterializationService CreateMaterializer(ArkiveTestEnvironment environment)
    {
        var root = environment.Root;
        var services = new ArkiveModMaterializationServices(
            new UpkPackageReader(), NoChunkProtectionProvider.Instance,
            new TextureReconnaissanceService(),
            new TextureReconnaissanceWorkspaceStore(Path.Combine(root, "staging", "texture-reconnaissance")),
            null,
            new SkeletalMeshReconnaissanceService(),
            new SkeletalMeshReconnaissanceWorkspaceStore(Path.Combine(root, "staging", "skeletal-mesh-reconnaissance")),
            new SkeletalMeshEditableExportService(),
            new StaticMeshReconnaissanceService(),
            new StaticMeshReconnaissanceWorkspaceStore(Path.Combine(root, "staging", "static-mesh-reconnaissance")),
            new StaticMeshEditableExportService(),
            new MaterialInstanceReconnaissanceService(),
            environment.MaterialReconnaissance,
            new MaterialInstanceEditService(),
            environment.MaterialCandidates);
        return new ArkiveModMaterializationService(environment.Library, environment.Edits, services);
    }

    internal static ArkiveModApplyService CreateApplyService(
        ArkiveTestEnvironment environment, string gamePackages, IModPackageExternalValidator? validator = null) =>
        new(Context(environment, gamePackages), CreateMaterializer(environment), Composer(validator));

    internal static UnifiedPackageComposerService Composer(IModPackageExternalValidator? validator = null) => new(
        new UpkPackageReader(), NoChunkProtectionProvider.Instance,
        new SkeletalMeshReconnaissanceService(), new SkeletalMeshEditableExportService(),
        new SkeletalMeshNativeEncodingService(), new SkeletalMeshStagedUpkService(), null!,
        new ArkivePackageModBundleService(
            validator ?? new SyntheticModPackageExternalValidator(), protectionProvider: NoChunkProtectionProvider.Instance));

    /// <summary>A UModel that cannot read the named packages (all of them when none are named).</summary>
    internal sealed class UnreadableValidator(Func<string, bool> unreadable) : IModPackageExternalValidator
    {
        public Task<ModExternalValidationResult> ValidateAsync(
            string packagePath, string className, string objectName, CancellationToken cancellationToken = default) =>
            unreadable(packagePath)
                ? throw new UmodelBridgeException($"UModel could not export {objectName} (exit code 1): Serializing behind stopper")
                : new SyntheticModPackageExternalValidator().ValidateAsync(packagePath, className, objectName, cancellationToken);
    }

    internal static string CreateMaterialClient(string root, out string cleanHash)
    {
        var path = Path.Combine(root, "client", "EFGame", "ReleasePC", "Packages", "material-instance.upk");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteMaterialInstancePackage(path);
        cleanHash = Sha(path);
        return path;
    }

    internal static MaterialInstanceEditEntry CreateMaterialEdit(
        ArkiveTestEnvironment environment,
        string packagePath,
        string name,
        float roughness,
        float? blue,
        bool candidate)
    {
        var report = ReadMaterial(packagePath);
        var recon = environment.MaterialReconnaissance.SaveAsync(report).GetAwaiter().GetResult();
        var edit = new MaterialInstanceEditService().CreateEditAsync(
            recon.JsonPath, environment.Edits.MaterialEdits.RootDirectory, name).GetAwaiter().GetResult();
        var changed = edit.Document with
        {
            Scalars = edit.Document.Scalars.Select(item => item with { Value = roughness }).ToArray(),
            Vectors = edit.Document.Vectors.Select(item => blue is null ? item : item with { Blue = blue.Value }).ToArray(),
        };
        edit = environment.Edits.MaterialEdits.SaveAsync(edit with { Document = changed }).GetAwaiter().GetResult();
        if (!candidate) return edit;
        var built = new MaterialInstanceEditService().BuildCandidateAsync(
            edit.DocumentPath, environment.MaterialCandidates).GetAwaiter().GetResult();
        return environment.Edits.MaterialEdits.RecordCandidateAsync(edit, built).GetAwaiter().GetResult();
    }

    internal static string AuthorMaterialMod(string root, string packagePath, string name, float roughness, float? blue)
    {
        var author = CreateEnvironment(Path.Combine(root, "author-" + name.Replace(' ', '-')));
        var edit = CreateMaterialEdit(author, packagePath, name, roughness, blue, candidate: false);
        var local = ArkiveModEditSnapshot.LoadAsync(author.Edits).GetAwaiter().GetResult()
            .Find(edit.Document.EditId)!;
        var captured = ArkiveModAuthoringService.CaptureAsync(local).GetAwaiter().GetResult();
        var mod = new ArkiveModAuthoringService(author.Library, author.Edits).CreateModAsync(
                new ArkiveModMetadata(name, "1.0.0", "Smoke author"), [captured],
                ArkiveModOrigin.Created, enabled: true)
            .GetAwaiter().GetResult();
        return author.Library.GetModFilePath(mod);
    }

    internal static MaterialInstanceReconnaissanceReport ReadMaterial(string packagePath)
    {
        var catalogue = new AssetCatalogReader(protectionProvider: NoChunkProtectionProvider.Instance).Read(packagePath);
        var export = catalogue.Assets.Single(asset => asset.ClassName == "MaterialInstanceConstant");
        return new MaterialInstanceReconnaissanceService().InspectAsync(new MaterialInstanceReconnaissanceRequest(
                packagePath, "material_instance", export.ExportIndex - 1, AssetKind.Material,
                export.ClassName, export.ObjectName, export.SerialOffset, export.SerialSize))
            .GetAwaiter().GetResult();
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
