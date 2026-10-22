using System.Text.Json;
using System.IO.Compression;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Baselines;
using Arkive.Core.Interface;
using Arkive.Core.GameData;
using Arkive.Core.Languages;
using Arkive.Core.Discovery;
using Arkive.Core.Databases;
using Arkive.Core.External;
using Arkive.Core.Exports;
using Arkive.Core.Inspection;
using Arkive.Core.Mods;
using Arkive.Core.Outfits;
using Arkive.Core.Meshes;
using Arkive.Core.Materials;
using Arkive.Core.Packages;
using Arkive.Core.Proportions;
using Arkive.Core.Scanning;
using Arkive.Core.Textures;
using Arkive.Core.Validation;
using Arkive.Core.Viewer;
using Arkive.Core.StaticMeshes;

return Run(args);

static int Run(string[] arguments)
{
    if (arguments is ["--version"])
    {
        Console.WriteLine(Arkive.Core.Updates.ArkiveReleasePolicy.CurrentVersion.ToString(3));
        return 0;
    }
    if (arguments is ["--help"] or ["help"])
    {
        PrintUsage();
        return 0;
    }
    if (arguments.Length < 2)
    {
        PrintUsage();
        return 2;
    }

    try
    {
        var command = arguments[0].ToLowerInvariant();
        var target = arguments[1];
        var json = arguments.Contains("--json", StringComparer.OrdinalIgnoreCase);

        return command switch
        {
            "database-build" => DatabaseBuild(target, ReadTextOption(arguments, "--out"),
                ReadTextOption(arguments, "--language"), json),
            "inspect" => Inspect(target, json),
            "artifact-inspect" => ArtifactInspect(target, json),
            "dds-rgb-view" => DdsRgbView(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "texture-import-candidate" => TextureImportCandidate(
                target,
                ReadTextOption(arguments, "--edited"),
                ReadTextOption(arguments, "--out"),
                ReadTextOption(arguments, "--alpha"),
                json),
            "texture-export-native" => TextureExportNative(
                target,
                ReadTextOption(arguments, "--object"),
                ReadTextOption(arguments, "--alias"),
                ReadTextOption(arguments, "--out"),
                json),
            "texture-project-create" => TextureProjectCreate(
                target,
                ReadTextOption(arguments, "--edited"),
                ReadTextOption(arguments, "--workspace"),
                ReadTextOption(arguments, "--name"),
                ReadTextOption(arguments, "--alpha"),
                json),
            "model-viewer-scene" => ModelViewerScene(
                target,
                ReadTextOption(arguments, "--object"),
                ReadTextOption(arguments, "--alias"),
                ReadTextOption(arguments, "--cache"),
                ReadTextOption(arguments, "--edits"),
                json),
            "skeletal-mesh-recon" => SkeletalMeshRecon(
                target,
                ReadTextOption(arguments, "--object"),
                ReadTextOption(arguments, "--alias"),
                ReadTextOption(arguments, "--out"),
                json),
            "static-mesh-recon" => StaticMeshRecon(
                target,
                ReadTextOption(arguments, "--object"),
                ReadTextOption(arguments, "--alias"),
                ReadTextOption(arguments, "--out"),
                json),
            "static-mesh-export-editable" => StaticMeshExportEditable(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "static-mesh-preflight" => StaticMeshPreflight(
                target,
                ReadTextOption(arguments, "--edited"),
                json),
            "static-mesh-register-edit" => StaticMeshRegisterEdit(
                target,
                ReadTextOption(arguments, "--edited"),
                ReadTextOption(arguments, "--workspace"),
                ReadTextOption(arguments, "--name"),
                json),
            "static-mesh-compile" => StaticMeshCompile(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "material-recon" => MaterialRecon(
                target,
                ReadTextOption(arguments, "--object"),
                ReadTextOption(arguments, "--alias"),
                ReadTextOption(arguments, "--out"),
                json),
            "material-create-edit" => MaterialCreateEdit(
                target,
                ReadTextOption(arguments, "--out"),
                ReadTextOption(arguments, "--name"),
                json),
            "material-build-candidate" => MaterialBuildCandidate(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "skeletal-mesh-reconstruct-noop" => SkeletalMeshReconstructNoOp(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "skeletal-mesh-export-editable" => SkeletalMeshExportEditable(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "outfit-swap" => OutfitSwap(
                target,
                ReadTextOption(arguments, "--index"),
                ReadTextOption(arguments, "--wear"),
                ReadTextOption(arguments, "--replace"),
                ReadTextOption(arguments, "--out"),
                ReadTextOption(arguments, "--cache"),
                ReadTextOption(arguments, "--bakes"),
                ReadTextOption(arguments, "--target-bakes"),
                ReadTextOption(arguments, "--lit-bakes"),
                ReadTextOption(arguments, "--surface-bakes"),
                ReadTextOption(arguments, "--target-surface-bakes"),
                json),
            "outfit-swap-mod" => OutfitSwapMod(
                target,
                ReadTextOption(arguments, "--index"),
                ReadTextOption(arguments, "--wear"),
                ReadTextOption(arguments, "--replace"),
                ReadTextOption(arguments, "--root"),
                ReadTextOption(arguments, "--name"),
                ReadTextOption(arguments, "--cache"),
                ReadTextOption(arguments, "--game"),
                json),
            "body-proportions" => BodyProportions(
                target,
                ReadTextOption(arguments, "--out"),
                ReadTextOptions(arguments, "--set"),
                new BodyProportionFollow(arguments.Contains("--loose-clothing", StringComparer.OrdinalIgnoreCase),
                    arguments.Contains("--hair", StringComparer.OrdinalIgnoreCase)),
                json),
            "model-viewer-animations" => ModelViewerAnimations(
                target,
                ReadTextOption(arguments, "--animset") ?? throw new ArgumentException("model-viewer-animations requires --animset NAME."),
                ReadTextOption(arguments, "--cache"),
                arguments.Contains("--all", StringComparer.OrdinalIgnoreCase),
                json),
            "skeletal-mesh-preflight" => SkeletalMeshPreflight(
                target,
                ReadTextOption(arguments, "--edited"),
                json),
            "skeletal-mesh-compare-skeletons" => SkeletalMeshCompareSkeletons(
                target,
                ReadTextOption(arguments, "--donor"),
                json),
            "skeletal-mesh-create-retarget" => SkeletalMeshCreateRetarget(
                target,
                ReadTextOption(arguments, "--donor"),
                ReadTextOption(arguments, "--out"),
                ReadTextOption(arguments, "--name"),
                json),
            "skeletal-mesh-register-edit" => SkeletalMeshRegisterEdit(
                target,
                ReadTextOption(arguments, "--edited"),
                ReadTextOption(arguments, "--workspace"),
                ReadTextOption(arguments, "--name"),
                json),
            "skeletal-mesh-encode-native" => SkeletalMeshEncodeNative(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "skeletal-mesh-compile-topology" => SkeletalMeshCompileTopology(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "skeletal-mesh-build-staged-upk" => SkeletalMeshBuildStagedUpk(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "skeletal-mesh-build-topology-upk" => SkeletalMeshBuildTopologyUpk(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "skeletal-mesh-prepare-mod" => SkeletalMeshPrepareMod(
                target,
                ReadTextOption(arguments, "--out"),
                ReadTextOption(arguments, "--backup-root"),
                ReadTextOption(arguments, "--name"),
                json),
            "package-compose" => PackageCompose(
                target,
                ReadTextOptions(arguments, "--texture"),
                ReadTextOptions(arguments, "--mesh"),
                ReadTextOptions(arguments, "--material"),
                ReadTextOptions(arguments, "--particle"),
                ReadTextOption(arguments, "--out"),
                ReadTextOption(arguments, "--backup-root"),
                ReadTextOption(arguments, "--name"),
                arguments.Contains("--preview-only", StringComparer.OrdinalIgnoreCase),
                arguments.Contains("--commit", StringComparer.OrdinalIgnoreCase),
                json),
            "face-map-projects" => FaceMapProjects(
                target,
                ReadTextOption(arguments, "--prefix"),
                ReadTextOption(arguments, "--alias"),
                ReadTextOption(arguments, "--workspace"),
                ReadTextOption(arguments, "--exports"),
                ReadTextOption(arguments, "--recon"),
                ReadTextOption(arguments, "--baselines"),
                json),
            "texture-pixel-diff" => TexturePixelDiff(
                target,
                ReadTextOption(arguments, "--edited"),
                json),
            "texture-to-png" => TextureToPng(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "texture-layout-confirm" => TextureLayoutConfirm(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "texture-build-payloads" => TextureBuildPayloads(
                target,
                ReadTextOption(arguments, "--out"),
                ReadTextOption(arguments, "--crunch"),
                json),
            "texture-assemble-export" => TextureAssembleExport(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "texture-plan-rewrite" => TexturePlanRewrite(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "texture-build-logical-image" => TextureBuildLogicalImage(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "texture-plan-physical" => TexturePlanPhysical(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "texture-reconstruct-noop-upk" => TextureReconstructNoOpUpk(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "texture-build-replacement-upk" => TextureBuildReplacementUpk(
                target,
                ReadTextOption(arguments, "--out"),
                json),
            "mod-build-bundle" => ModBuildBundle(
                target,
                ReadTextOption(arguments, "--out"),
                ReadTextOption(arguments, "--backup-root"),
                ReadTextOption(arguments, "--name"),
                json),
            "mod-status" => ModDeployment(
                target,
                "status",
                ReadTextOption(arguments, "--backup-root"),
                ReadTextOption(arguments, "--state-root"),
                confirmed: true,
                json),
            "mod-install" => ModDeployment(
                target,
                "install",
                ReadTextOption(arguments, "--backup-root"),
                ReadTextOption(arguments, "--state-root"),
                arguments.Contains("--confirm", StringComparer.OrdinalIgnoreCase),
                json),
            "mod-restore" => ModDeployment(
                target,
                "restore",
                ReadTextOption(arguments, "--backup-root"),
                ReadTextOption(arguments, "--state-root"),
                arguments.Contains("--confirm", StringComparer.OrdinalIgnoreCase),
                json),
            "mod-set-inspect" => ModSetDeployment(
                target, "inspect", ReadTextOption(arguments, "--backup-root"),
                ReadTextOption(arguments, "--state-root"), confirmed: true, json),
            "mod-set-install" => ModSetDeployment(
                target, "install", ReadTextOption(arguments, "--backup-root"),
                ReadTextOption(arguments, "--state-root"),
                arguments.Contains("--confirm", StringComparer.OrdinalIgnoreCase), json),
            "mod-set-restore" => ModSetDeployment(
                target, "restore", ReadTextOption(arguments, "--backup-root"),
                ReadTextOption(arguments, "--state-root"),
                arguments.Contains("--confirm", StringComparer.OrdinalIgnoreCase), json),
            "arkive-inspect" => ArkiveInspect(target, json),
            "arkive-list" => ArkiveList(target, json),
            "arkive-import" => ArkiveImport(
                target, ReadTextOption(arguments, "--root"),
                arguments.Contains("--enable", StringComparer.OrdinalIgnoreCase),
                ReadIntegerOption(arguments, "--priority"),
                arguments.Contains("--replace", StringComparer.OrdinalIgnoreCase), json),
            "arkive-set" => ArkiveSet(
                target, ReadTextOption(arguments, "--mod"),
                arguments.Contains("--enable", StringComparer.OrdinalIgnoreCase),
                arguments.Contains("--disable", StringComparer.OrdinalIgnoreCase),
                ReadIntegerOption(arguments, "--priority"), json),
            "arkive-conflicts" => ArkiveConflicts(target, json),
            "arkive-migrate" => ArkiveMigrate(target, ReadTextOption(arguments, "--author"), json),
            "arkive-plan" => ArkivePlan(target, json),
            "arkive-particle-tint" => ArkiveParticleTint(
                target,
                ReadTextOptions(arguments, "--package"),
                ReadTextOption(arguments, "--match") ?? throw new ArgumentException("arkive-particle-tint requires --match NAME[,NAME...]."),
                ReadTextOption(arguments, "--color") ?? throw new ArgumentException("arkive-particle-tint requires --color R,G,B."),
                ReadTextOption(arguments, "--name") ?? throw new ArgumentException("arkive-particle-tint requires --name TEXT."),
                json),
            "skill-actions" => SkillActions(target, ReadTextOption(arguments, "--class"), ReadIntegerOption(arguments, "--skill"), json),
            "arkive-apply" => ArkiveApply(
                target, arguments.Contains("--confirm", StringComparer.OrdinalIgnoreCase), json),
            "arkive-update" => ArkiveUpdate(target, ReadTextOption(arguments, "--mod"), json),
            "client-update-check-all" => ClientUpdateCheckAll(
                target, ReadTextOption(arguments, "--out"),
                ReadTextOption(arguments, "--state-root"), json),
            "catalog" => Catalog(target, ReadTextOption(arguments, "--filter"), json),
            "umodel-list" => UmodelList(target, ReadTextOption(arguments, "--filter"), json),
            "interface-names" => InterfaceNames(target, ReadTextOption(arguments, "--metadata"),
                ReadTextOption(arguments, "--filter"), json),
            "zone-names" => ZoneNames(target, ReadTextOption(arguments, "--metadata"),
                ReadTextOption(arguments, "--filter"), json),
            "game-data-mod" => GameDataMod(target, ReadTextOption(arguments, "--sql"), ReadTextOption(arguments, "--name"),
                ReadTextOption(arguments, "--mod"), json),
            "language" => Language(target, ReadTextOption(arguments, "--data"), ReadTextOption(arguments, "--use"),
                arguments.Contains("--restore", StringComparer.OrdinalIgnoreCase), json),
            "metadata-resolve" => MetadataResolve(
                target,
                ReadTextOption(arguments, "--package"),
                ReadTextOption(arguments, "--object"),
                json),
            "metadata-build-dependencies" => MetadataBuildDependencies(
                target,
                ReadTextOption(arguments, "--index"),
                ReadTextOption(arguments, "--root"),
                ReadTextOption(arguments, "--out"),
                ReadTextOption(arguments, "--scratch"),
                ReadTextOptions(arguments, "--query"),
                ReadPositiveIntOption(arguments, "--workers"),
                json),
            "character-texture-audit" => CharacterTextureAudit(
                target,
                ReadTextOption(arguments, "--index"),
                ReadTextOption(arguments, "--root"),
                ReadTextOption(arguments, "--out"),
                arguments.Contains("--recon", StringComparer.OrdinalIgnoreCase),
                ReadPositiveIntOption(arguments, "--recon-limit"),
                json),
            "index-build" => IndexBuild(
                target,
                ReadTextOption(arguments, "--cache"),
                ReadPositiveIntOption(arguments, "--workers"),
                ReadTextOption(arguments, "--scratch"),
                json),
            "index-search" => IndexSearch(
                target,
                ReadTextOption(arguments, "--cache"),
                ReadTextOption(arguments, "--metadata"),
                ReadTextOption(arguments, "--query"),
                ReadTextOption(arguments, "--category"),
                ReadTextOption(arguments, "--scope"),
                ReadTextOption(arguments, "--archetype"),
                ReadPositiveIntOption(arguments, "--limit"),
                json),
            "umodel-export" => UmodelExport(
                target,
                ReadTextOption(arguments, "--object"),
                ReadTextOption(arguments, "--class"),
                ReadTextOption(arguments, "--out"),
                json),
            "scan" => Scan(target, ReadLimit(arguments), json),
            _ => UnknownCommand(command),
        };
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
            InvalidDataException or JsonException)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
}

static int DatabaseBuild(string archive, string? output, string? language, bool json)
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        var progress = new InlineProgress<GameDatabaseBuildProgress>(value =>
            Console.Error.WriteLine($"{value.Stage}: {value.Completed}/{value.Total} {value.CurrentFile}"));
        var result = new GameDatabaseBuilder().BuildAsync(archive,
            output ?? Path.Combine(ArkiveRuntimePaths.ResolveDataRoot(), "staging", "lpk", "data2-fixed", "arkive-asset-metadata.json"),
            language ?? "English", progress, cancellation.Token).GetAwaiter().GetResult();
        if (json) WriteJson(result);
        else Console.WriteLine($"Built {result.AssociationCount:N0} asset references, {result.EntityCount:N0} entities and {result.CustomizationCount:N0} customization options: {result.OutputPath}");
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Database build cancelled; the previous mapping was preserved.");
        return 1;
    }
    finally { Console.CancelKeyPress -= cancel; }
}

static int ArtifactInspect(string filePath, bool json)
{
    var result = new ExportArtifactInspector()
        .InspectAsync(filePath)
        .GetAwaiter()
        .GetResult();
    if (json)
    {
        WriteJson(result);
    }
    else
    {
        Console.WriteLine(result.FilePath);
        Console.WriteLine($"  Format:       {result.Format}");
        Console.WriteLine($"  Summary:      {result.Summary}");
        Console.WriteLine($"  Validation:   {result.ErrorCount:N0} errors, {result.WarningCount:N0} warnings");
        Console.WriteLine("  Properties:");
        foreach (var property in result.Properties)
        {
            Console.WriteLine($"    {property.Group} / {property.Name}: {property.Value}");
        }

        if (result.Dependencies.Count > 0)
        {
            Console.WriteLine("  Dependencies:");
            foreach (var dependency in result.Dependencies)
            {
                var state = dependency.IsEmbedded
                    ? "embedded"
                    : dependency.Exists
                        ? "present"
                        : "missing";
                Console.WriteLine($"    {dependency.Kind}: {dependency.Reference} ({state})");
            }
        }

        if (result.Issues.Count > 0)
        {
            Console.WriteLine("  Issues:");
            foreach (var issue in result.Issues)
            {
                Console.WriteLine($"    {issue.Severity} {issue.Code}: {issue.Message}");
            }
        }
    }

    return result.ErrorCount == 0 ? 0 : 1;
}

static int DdsRgbView(string filePath, string? outputPath, bool json)
{
    var source = Path.GetFullPath(filePath);
    var destination = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(
            Path.GetDirectoryName(source)!,
            Path.GetFileNameWithoutExtension(source) + "-rgb-view-opaque-alpha.dds")
        : Path.GetFullPath(outputPath);
    var result = DdsBc3RgbViewWriter
        .CreateAsync(source, destination)
        .GetAwaiter()
        .GetResult();
    var inspection = new ExportArtifactInspector()
        .InspectAsync(destination)
        .GetAwaiter()
        .GetResult();
    if (json)
    {
        WriteJson(new { Result = result, Inspection = inspection });
    }
    else
    {
        Console.WriteLine(result.OutputPath);
        Console.WriteLine($"  Source SHA-256:       {result.SourceSha256}");
        Console.WriteLine($"  RGB-view SHA-256:     {result.OutputSha256}");
        Console.WriteLine($"  Dimensions:           {result.Width} x {result.Height}");
        Console.WriteLine($"  Mips:                 {result.MipCount:N0}");
        Console.WriteLine($"  Alpha blocks changed: {result.AlphaBlocksOverridden:N0}");
        Console.WriteLine("  Warning:              Viewing copy only; preserve alpha from the original DDS.");
    }

    return inspection.ErrorCount == 0 ? 0 : 1;
}

static int TextureImportCandidate(
    string manifestPath,
    string? editedPath,
    string? outputPath,
    string? alphaPolicy,
    bool json)
{
    if (string.IsNullOrWhiteSpace(editedPath))
    {
        throw new ArgumentException("texture-import-candidate requires --edited FILE.");
    }

    var manifest = Path.GetFullPath(manifestPath);
    var destination = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(
            Path.GetDirectoryName(manifest)!,
            "import-candidates",
            $"candidate-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}")
        : Path.GetFullPath(outputPath);
    var policy = ParseTextureImportAlphaPolicy(alphaPolicy);
    var result = new TextureImportCandidateService()
        .CreateAsync(new TextureImportCandidateRequest(
            manifest,
            Path.GetFullPath(editedPath),
            destination,
            policy))
        .GetAwaiter()
        .GetResult();
    if (json)
    {
        WriteJson(result);
    }
    else
    {
        Console.WriteLine(result.CandidateDdsPath);
        Console.WriteLine($"  Texture:              {result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Format:               {result.Manifest.PixelFormat}");
        Console.WriteLine($"  Dimensions:           {result.Manifest.Width} x {result.Manifest.Height}");
        Console.WriteLine($"  Mips:                 {result.Manifest.MipCount:N0}");
        Console.WriteLine($"  Alpha policy:         {result.Manifest.AlphaPolicy}");
        Console.WriteLine($"  Affected mip blocks: {result.Manifest.TouchedBlocks,12:N0}");
        Console.WriteLine($"  Source blocks copied: {result.Manifest.UnchangedBlocksCopied,12:N0}");
        Console.WriteLine($"  Candidate SHA-256:    {result.Manifest.CandidateDdsSha256}");
        Console.WriteLine($"  Manifest:             {result.ManifestPath}");
        foreach (var check in result.Manifest.PreflightChecks)
        {
            Console.WriteLine($"  {check.Status,-11} {check.Code}: {check.Message}");
        }
    }

    return result.CandidateInspection.ErrorCount == 0 ? 0 : 1;
}

static int TextureExportNative(
    string packagePath,
    string? objectName,
    string? packageAlias,
    string? outputPath,
    bool json)
{
    if (string.IsNullOrWhiteSpace(objectName))
    {
        throw new ArgumentException("texture-export-native requires --object NAME.");
    }
    var package = Path.GetFullPath(packagePath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(package)!, "arkive-native-export", SafeFileName(objectName))
        : Path.GetFullPath(outputPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var catalog = new AssetCatalogReader(protectionProvider: provider).Read(package);
    var matches = catalog.Assets.Where(asset =>
        asset.Kind == AssetKind.Texture &&
        asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (matches.Length != 1)
    {
        throw new InvalidOperationException(
            $"Expected one Texture2D named '{objectName}', found {matches.Length:N0}.");
    }
    var asset = matches[0];
    var alias = string.IsNullOrWhiteSpace(packageAlias)
        ? Path.GetFileNameWithoutExtension(package)
        : packageAlias.Trim();
    var report = new TextureReconnaissanceService(protectionProvider: provider)
        .InspectAsync(new TextureReconnaissanceRequest(
            package, alias, asset.ExportIndex, asset.Kind, asset.ClassName,
            asset.ObjectName, asset.SerialOffset, asset.SerialSize))
        .GetAwaiter().GetResult();
    var reconRoot = Path.Combine(Path.GetDirectoryName(output)!, "texture-reconnaissance-cli");
    var recon = new TextureReconnaissanceWorkspaceStore(reconRoot)
        .SaveAsync(report).GetAwaiter().GetResult();
    var result = new NativeTextureExportService(protectionProvider: provider)
        .ExportAsync(new NativeTextureExportRequest(report, output, recon.JsonPath))
        .GetAwaiter().GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Format:                {result.Manifest.PixelFormat}");
        Console.WriteLine($"  Dimensions:            {result.Manifest.Width} x {result.Manifest.Height}");
        Console.WriteLine($"  Mips:                  {result.Manifest.MipCount:N0}");
        Console.WriteLine($"  Manifest:              {result.ManifestPath}");
        Console.WriteLine($"  Editable image:        {result.Manifest.EditImageRelativePath}");
        Console.WriteLine($"  Reconnaissance:        {recon.JsonPath}");
    }
    return 0;
}

static int TextureProjectCreate(
    string nativeManifestPath,
    string? editedPath,
    string? workspacePath,
    string? displayName,
    string? alphaPolicy,
    bool json)
{
    if (string.IsNullOrWhiteSpace(editedPath))
        throw new ArgumentException("texture-project-create requires --edited FILE.");
    if (string.IsNullOrWhiteSpace(workspacePath))
        throw new ArgumentException("texture-project-create requires --workspace DIR.");
    var nativePath = Path.GetFullPath(nativeManifestPath);
    var edited = Path.GetFullPath(editedPath);
    var native = NativeTextureExportService.LoadManifestAsync(nativePath)
        .GetAwaiter().GetResult();
    if (native.NativeExportIndex is null)
        throw new NotSupportedException(
            $"{native.ObjectName} has no confirmed native export identity.");
    var name = string.IsNullOrWhiteSpace(displayName)
        ? native.ObjectName + " texture replacement"
        : displayName.Trim();
    var project = new GuidedTextureModWorkspace(Path.GetFullPath(workspacePath))
        .CreateAsync(
            name, native.PackageAlias, native.PhysicalPackageName,
            native.SourcePackagePath, native.ClassName, native.ObjectName,
            native.NativeExportIndex.Value, native.PixelFormat, native.Width, native.Height,
            nativePath, edited, ParseTextureImportAlphaPolicy(alphaPolicy))
        .GetAwaiter().GetResult();
    if (json) WriteJson(project.Manifest);
    else
    {
        Console.WriteLine($"{project.Manifest.PackageAlias}.{project.Manifest.ObjectName}");
        Console.WriteLine($"  Editable image:      {project.Manifest.EditableImagePath}");
        Console.WriteLine($"  Alpha policy:        {project.Manifest.AlphaPolicy}");
        Console.WriteLine($"  Project manifest:    {project.ManifestPath}");
    }
    return 0;
}

static int ModelViewerScene(
    string packagePath,
    string? objectName,
    string? packageAlias,
    string? cachePath,
    string? editsPath,
    bool json)
{
    if (string.IsNullOrWhiteSpace(objectName))
        throw new ArgumentException("model-viewer-scene requires --object NAME.");
    var edits = string.IsNullOrWhiteSpace(editsPath)
        ? null
        : JsonSerializer.Deserialize<ModelViewerEdits>(File.ReadAllText(editsPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))
          ?? throw new InvalidDataException($"{editsPath} holds no edits.");
    var package = Path.GetFullPath(packagePath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var catalog = new AssetCatalogReader(protectionProvider: provider).Read(package);
    var matches = catalog.Assets.Where(asset =>
        asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (matches.Length != 1)
        throw new InvalidOperationException($"Expected one object named '{objectName}', found {matches.Length:N0}.");
    var asset = matches[0];
    var executable = UmodelLocator.FindExecutable()
        ?? throw new InvalidOperationException("The Asset Viewer needs the Lost Ark UModel backend.");
    var cache = new ModelViewerCache(string.IsNullOrWhiteSpace(cachePath)
        ? Path.Combine(ArkiveRuntimePaths.ResolveDataRoot(), "cache", "model-viewer")
        : Path.GetFullPath(cachePath));
    var service = new ModelViewerSceneService(new UmodelBridge(executable), cache, protectionProvider: provider);
    var result = service.PrepareAsync(new ModelViewerRequest(
        package,
        string.IsNullOrWhiteSpace(packageAlias) ? Path.GetFileNameWithoutExtension(package) : packageAlias.Trim(),
        Path.GetDirectoryName(package),
        asset.ExportIndex, asset.SerialOffset, asset.SerialSize, asset.ClassName, asset.ObjectName), edits)
        .GetAwaiter().GetResult();
    if (json) WriteJson(result.Scene);
    else
    {
        var scene = result.Scene;
        Console.WriteLine($"{scene.Object.PackageAlias}.{scene.Object.ObjectName} ({scene.Object.ClassName})");
        Console.WriteLine($"  Geometry:   {scene.GeometrySource}, {scene.Lods.Count} LOD(s), {scene.Bones.Count} bone(s), {scene.Sockets.Count} socket(s)");
        foreach (var lod in scene.Lods)
            Console.WriteLine($"    LOD {lod.Index}: {lod.Vertices:N0} verts, {lod.Triangles:N0} tris, {lod.Sections.Count} section(s), {lod.UvSets} UV set(s)");
        Console.WriteLine($"  Materials:  {scene.Materials.Count}");
        foreach (var material in scene.Materials)
        {
            Console.WriteLine($"    {material.Slot}: {material.Name} <- {material.RootMaterial} [{material.Recipe.Status}: {material.Recipe.FamilyId}] {material.TextureUrls.Count} texture(s)");
            Console.WriteLine(material.GameShader is { } shader
                ? $"       game shader: {shader.MaterialMap} ({(shader.ExactVariant ? "exact variant" : "nearest variant")}, {(shader.ExactBindings ? "exact registers" : "assumed registers")}, {shader.InstructionCount} instructions)"
                : $"       game shader: unavailable ({material.GameShaderFailure ?? "no index"})");
        }
        Console.WriteLine($"  Textures:   {scene.Textures.Count}");
        foreach (var diagnostic in scene.Diagnostics) Console.WriteLine($"  Note:       {diagnostic}");
        Console.WriteLine($"  Prepared:   {(result.FromCache ? "from cache" : $"{scene.PreparationSeconds:N2}s")} -> {result.SceneJsonPath}");
        if (!result.FromCache && scene.StageSeconds is { } stages)
            Console.WriteLine($"  Stages:     {string.Join(", ", stages.Select(stage => $"{stage.Key} {stage.Value:N2}s"))}");
    }
    return 0;
}

static int SkeletalMeshRecon(
    string packagePath,
    string? objectName,
    string? packageAlias,
    string? outputPath,
    bool json)
{
    if (string.IsNullOrWhiteSpace(objectName))
        throw new ArgumentException("skeletal-mesh-recon requires --object NAME.");
    var package = Path.GetFullPath(packagePath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var catalog = new AssetCatalogReader(protectionProvider: provider).Read(package);
    var matches = catalog.Assets.Where(asset =>
        asset.Kind == AssetKind.SkeletalMesh &&
        asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (matches.Length != 1)
        throw new InvalidOperationException($"Expected one SkeletalMesh named '{objectName}', found {matches.Length:N0}.");
    var asset = matches[0];
    var alias = string.IsNullOrWhiteSpace(packageAlias)
        ? Path.GetFileNameWithoutExtension(package)
        : packageAlias.Trim();
    var root = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(package)!, "arkive-skeletalmesh-recon")
        : Path.GetFullPath(outputPath);
    var executable = UmodelLocator.FindExecutable();
    var service = new SkeletalMeshReconnaissanceService(
        protectionProvider: provider,
        umodelBridge: executable is null ? null : new UmodelBridge(executable));
    var report = service.InspectAsync(new SkeletalMeshReconnaissanceRequest(
        package, alias, asset.ExportIndex, asset.Kind, asset.ClassName,
        asset.ObjectName, asset.SerialOffset, asset.SerialSize)).GetAwaiter().GetResult();
    var entry = new SkeletalMeshReconnaissanceWorkspaceStore(root)
        .SaveAsync(report).GetAwaiter().GetResult();
    if (json) WriteJson(report);
    else
    {
        Console.WriteLine($"{report.PackageAlias}.{report.ObjectName}");
        Console.WriteLine($"  Export:              0x{report.SerialOffset:X}+0x{report.SerialSize:X}");
        Console.WriteLine($"  Properties/native:  +0x{report.NativeDataRelativeOffset:X} / {report.NativeDataSize:N0} bytes");
        Console.WriteLine($"  Readiness:           {report.Readiness}");
        Console.WriteLine($"  Oracle:              {(report.Oracle is null ? "unavailable" : $"{report.Oracle.LodCount} LODs, {report.Oracle.BoneCount} bones")}");
        if (report.DeformationDependencies is { } dependencies)
        {
            Console.WriteLine($"  Cloth:               {dependencies.ClothState} / {dependencies.ActiveClothReferenceCount:N0} active ref(s)");
            Console.WriteLine($"  Morphs:              {dependencies.MorphState} / {dependencies.MorphTargetCount:N0} target(s) / {dependencies.MorphDeltaCount:N0} delta(s)");
            Console.WriteLine($"  Changed topology:    {(dependencies.TopologyChangingSupported ? "supported" : "refused")}");
        }
        Console.WriteLine($"  Report:              {entry.JsonPath}");
    }
    return report.Readiness == SkeletalMeshReadiness.Refused ? 1 : 0;
}

static int StaticMeshRecon(
    string packagePath,
    string? objectName,
    string? packageAlias,
    string? outputPath,
    bool json)
{
    if (string.IsNullOrWhiteSpace(objectName))
        throw new ArgumentException("static-mesh-recon requires --object NAME.");
    var package = Path.GetFullPath(packagePath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var catalog = new AssetCatalogReader(protectionProvider: provider).Read(package);
    var matches = catalog.Assets.Where(asset =>
        asset.Kind == AssetKind.StaticMesh &&
        asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (matches.Length != 1)
        throw new InvalidOperationException($"Expected one StaticMesh named '{objectName}', found {matches.Length:N0}.");
    var asset = matches[0];
    var alias = string.IsNullOrWhiteSpace(packageAlias)
        ? Path.GetFileNameWithoutExtension(package)
        : packageAlias.Trim();
    var root = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(package)!, "arkive-staticmesh-recon")
        : Path.GetFullPath(outputPath);
    var executable = UmodelLocator.FindExecutable();
    var report = new StaticMeshReconnaissanceService(
            protectionProvider: provider,
            umodelBridge: executable is null ? null : new UmodelBridge(executable))
        .InspectAsync(new StaticMeshReconnaissanceRequest(
            package,
            alias,
            asset.ExportIndex,
            asset.Kind,
            asset.ClassName,
            asset.ObjectName,
            asset.SerialOffset,
            asset.SerialSize)).GetAwaiter().GetResult();
    var entry = new StaticMeshReconnaissanceWorkspaceStore(root)
        .SaveAsync(report).GetAwaiter().GetResult();
    if (json) WriteJson(report);
    else
    {
        Console.WriteLine($"{report.PackageAlias}.{report.ObjectName}");
        Console.WriteLine($"  Export:              0x{report.SerialOffset:X}+0x{report.SerialSize:X}");
        Console.WriteLine($"  Native start:        +0x{report.NativeDataRelativeOffset:X}");
        Console.WriteLine($"  Readiness:           {report.Readiness}");
        Console.WriteLine($"  Oracle:              {(report.Oracle is null ? "unavailable" : $"{report.Oracle.LodCount} LOD(s)")}");
        if (report.NativeLayout is { } native)
        {
            Console.WriteLine($"  Collision retained:  {native.CollisionNodeCount:N0} nodes / {native.CollisionTriangleCount:N0} triangles");
            foreach (var lod in native.Lods)
                Console.WriteLine($"  LOD {lod.LodIndex}:              {lod.VertexCount:N0} vertices / {lod.Indices.ElementCount:N0} indices / {lod.TextureCoordinateCount:N0} UV(s)");
        }
        foreach (var issue in report.Issues)
            Console.WriteLine($"    {issue.Severity} {issue.Code}: {issue.Message}");
        Console.WriteLine($"  Report:              {entry.JsonPath}");
    }
    return report.Readiness == StaticMeshReadiness.Refused ? 1 : 0;
}

static int StaticMeshExportEditable(string reconPath, string? outputPath, bool json)
{
    var path = Path.GetFullPath(reconPath);
    var report = StaticMeshReconnaissanceWorkspaceStore.LoadReportAsync(path)
        .GetAwaiter().GetResult();
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(path)!, "editable")
        : Path.GetFullPath(outputPath);
    var result = new StaticMeshEditableExportService(
            protectionProvider: ChunkProtectionProviderFactory.CreateWesternGlobal())
        .ExportAsync(new StaticMeshEditableExportRequest(report, path, output))
        .GetAwaiter().GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        foreach (var lod in result.Manifest.Lods)
            Console.WriteLine($"  LOD {lod.LodIndex}:              {lod.VertexCount:N0} vertices / {lod.IndexCount:N0} indices / {lod.TextureCoordinateCount:N0} UV(s)");
        Console.WriteLine($"  Editable GLB:        {result.GlbPath}");
        Console.WriteLine($"  Manifest:            {result.ManifestPath}");
    }
    return 0;
}

static int StaticMeshPreflight(string editableManifestPath, string? editedPath, bool json)
{
    if (string.IsNullOrWhiteSpace(editedPath))
        throw new ArgumentException("static-mesh-preflight requires --edited FILE.glb.");
    var report = new StaticMeshEditPreflightService(
            protectionProvider: ChunkProtectionProviderFactory.CreateWesternGlobal())
        .InspectAsync(Path.GetFullPath(editableManifestPath), Path.GetFullPath(editedPath))
        .GetAwaiter().GetResult();
    if (json) WriteJson(report);
    else
    {
        Console.WriteLine($"Readiness:            {report.Readiness}");
        Console.WriteLine($"Topology changed:     {report.TopologyChanged}");
        Console.WriteLine($"Materials match:      {report.MaterialSlotsMatch}");
        Console.WriteLine($"Stable vertices:      {report.EveryVertexMapped}");
        Console.WriteLine($"Collision retained:   {report.CollisionWillRemainFromSource}");
        foreach (var issue in report.Issues) Console.WriteLine("  " + issue);
    }
    return report.Readiness == StaticMeshEditReadiness.Refused ? 1 : 0;
}

static int StaticMeshRegisterEdit(
    string editableManifestPath,
    string? editedPath,
    string? workspacePath,
    string? displayName,
    bool json)
{
    if (string.IsNullOrWhiteSpace(editedPath) || string.IsNullOrWhiteSpace(workspacePath))
        throw new ArgumentException("static-mesh-register-edit requires --edited FILE.glb --workspace DIR.");
    var entry = new StaticMeshEditWorkspaceStore(
            Path.GetFullPath(workspacePath),
            protectionProvider: ChunkProtectionProviderFactory.CreateWesternGlobal())
        .RegisterAsync(Path.GetFullPath(editableManifestPath), Path.GetFullPath(editedPath), displayName)
        .GetAwaiter().GetResult();
    if (json) WriteJson(entry.Manifest);
    else
    {
        Console.WriteLine($"Edit:                 {entry.Manifest.DisplayName}");
        Console.WriteLine($"Readiness:            {entry.Manifest.Preflight.Readiness}");
        Console.WriteLine($"Topology changed:     {entry.Manifest.Preflight.TopologyChanged}");
        Console.WriteLine($"Manifest:             {entry.ManifestPath}");
    }
    return entry.Manifest.Preflight.Readiness == StaticMeshEditReadiness.Refused ? 1 : 0;
}

static int StaticMeshCompile(string editManifestPath, string? outputPath, bool json)
{
    var editPath = Path.GetFullPath(editManifestPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(editPath)!, "compiled")
        : Path.GetFullPath(outputPath);
    var result = new StaticMeshCompilationService(
            protectionProvider: ChunkProtectionProviderFactory.CreateWesternGlobal())
        .CompileAsync(editPath, output).GetAwaiter().GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Topology changed:    {result.Manifest.TopologyChanged}");
        Console.WriteLine($"  Serial size:         {result.Manifest.SourceSerialSize:N0} -> {result.Manifest.CompiledSerialSize:N0} ({result.Manifest.SerialSizeDelta:+#;-#;0})");
        Console.WriteLine($"  Decode-back:         {result.Manifest.DecodeBackPassed}");
        Console.WriteLine($"  Collision retained:  {result.Manifest.CollisionPreservedFromSource}");
        Console.WriteLine($"  Candidate export:    {result.CompiledExportPath}");
        Console.WriteLine($"  Proof GLB:           {result.DecodedProofGlbPath}");
        Console.WriteLine($"  Manifest:            {result.ManifestPath}");
    }
    return result.Manifest.DecodeBackPassed ? 0 : 1;
}

static int MaterialRecon(
    string packagePath,
    string? objectName,
    string? packageAlias,
    string? outputPath,
    bool json)
{
    if (string.IsNullOrWhiteSpace(objectName))
        throw new ArgumentException("material-recon requires --object NAME.");
    var package = Path.GetFullPath(packagePath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var catalog = new AssetCatalogReader(protectionProvider: provider).Read(package);
    var matches = catalog.Assets.Where(asset =>
        asset.Kind == AssetKind.Material &&
        asset.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) &&
        asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (matches.Length != 1)
        throw new InvalidOperationException($"Expected one MaterialInstanceConstant named '{objectName}', found {matches.Length:N0}.");
    var asset = matches[0];
    var alias = string.IsNullOrWhiteSpace(packageAlias)
        ? Path.GetFileNameWithoutExtension(package)
        : packageAlias.Trim();
    var root = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(package)!, "arkive-material-recon")
        : Path.GetFullPath(outputPath);
    var report = new MaterialInstanceReconnaissanceService(protectionProvider: provider)
        .InspectAsync(new MaterialInstanceReconnaissanceRequest(
            package, alias, asset.ExportIndex, asset.Kind, asset.ClassName,
            asset.ObjectName, asset.SerialOffset, asset.SerialSize)).GetAwaiter().GetResult();
    var entry = new MaterialInstanceWorkspaceStore(root).SaveAsync(report).GetAwaiter().GetResult();
    if (json) WriteJson(report);
    else
    {
        Console.WriteLine($"{report.PackageAlias}.{report.ObjectName}");
        Console.WriteLine($"  Parent:              {report.ParentObject ?? "unresolved"}");
        Console.WriteLine($"  Scalar/vector:       {report.ScalarParameters.Count:N0} / {report.VectorParameters.Count:N0}");
        Console.WriteLine($"  Texture references:  {report.TextureParameters.Count:N0} (same-UPK targets)");
        Console.WriteLine($"  Readiness:           {report.Readiness}");
        Console.WriteLine($"  Report:              {entry.JsonPath}");
    }
    return report.Readiness == MaterialInstanceReadiness.Refused ? 1 : 0;
}

static int MaterialCreateEdit(string reconPath, string? outputPath, string? displayName, bool json)
{
    var path = Path.GetFullPath(reconPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(path)!, "material-edits")
        : Path.GetFullPath(outputPath);
    var entry = new MaterialInstanceEditService(
            ChunkProtectionProviderFactory.CreateWesternGlobal())
        .CreateEditAsync(path, output, displayName).GetAwaiter().GetResult();
    if (json) WriteJson(entry.Document);
    else
    {
        Console.WriteLine($"{entry.Document.PackageAlias}.{entry.Document.ObjectName}");
        Console.WriteLine($"  Scalar/vector:       {entry.Document.Scalars.Count:N0} / {entry.Document.Vectors.Count:N0}");
        Console.WriteLine($"  Texture references:  {entry.Document.Textures.Count:N0}");
        Console.WriteLine($"  Parent reference:    {(entry.Document.Parent is null ? "not proven" : "editable")}");
        Console.WriteLine($"  Editable JSON:       {entry.DocumentPath}");
    }
    return 0;
}

static int MaterialBuildCandidate(string editPath, string? outputPath, bool json)
{
    var path = Path.GetFullPath(editPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(path)!, "candidates")
        : Path.GetFullPath(outputPath);
    var result = new MaterialInstanceEditService(
            ChunkProtectionProviderFactory.CreateWesternGlobal())
        .BuildCandidateAsync(path, output).GetAwaiter().GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Scalars/vectors:     {result.Manifest.ChangedScalarCount:N0} / {result.Manifest.ChangedVectorCount:N0} changed");
        Console.WriteLine($"  Texture references:  {result.Manifest.ChangedTextureReferenceCount:N0} changed");
        Console.WriteLine($"  Parent reference:    {(result.Manifest.ParentReferenceChanged ? "changed" : "unchanged")}");
        Console.WriteLine($"  Changed bytes:       {result.Manifest.ChangedByteCount:N0}");
        Console.WriteLine($"  Locked preserved:    {result.Manifest.LockedBytesPreserved}");
        Console.WriteLine($"  Candidate:           {result.CandidatePath}");
        Console.WriteLine($"  Manifest:            {result.ManifestPath}");
    }
    return result.Manifest.SameSize && result.Manifest.LockedBytesPreserved ? 0 : 1;
}

static int SkeletalMeshReconstructNoOp(string reconPath, string? outputPath, bool json)
{
    var path = Path.GetFullPath(reconPath);
    var report = JsonSerializer.Deserialize<SkeletalMeshReconnaissanceReport>(
        File.ReadAllText(path),
        new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        }) ?? throw new InvalidDataException("The SkeletalMesh reconnaissance JSON is empty.");
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(path)!, "no-op-proof")
        : Path.GetFullPath(outputPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var result = new SkeletalMeshNoOpService(protectionProvider: provider)
        .ReconstructAsync(new SkeletalMeshNoOpRequest(report, path, output))
        .GetAwaiter().GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Byte-identical:      {result.Manifest.ByteIdentical}");
        Console.WriteLine($"  Source SHA-256:      {result.Manifest.SourceExportSha256}");
        Console.WriteLine($"  Rebuilt SHA-256:     {result.Manifest.ReconstructedExportSha256}");
        Console.WriteLine($"  Export proof:        {result.ExportPath}");
        Console.WriteLine($"  Manifest:            {result.ManifestPath}");
    }
    return result.Manifest.ByteIdentical ? 0 : 1;
}

static int SkeletalMeshExportEditable(string reconPath, string? outputPath, bool json)
{
    var path = Path.GetFullPath(reconPath);
    var report = JsonSerializer.Deserialize<SkeletalMeshReconnaissanceReport>(
        File.ReadAllText(path),
        new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        }) ?? throw new InvalidDataException("The SkeletalMesh reconnaissance JSON is empty.");
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(path)!, "editable-mesh")
        : Path.GetFullPath(outputPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var result = new SkeletalMeshEditableExportService(protectionProvider: provider)
        .ExportAsync(new SkeletalMeshEditableExportRequest(report, path, output))
        .GetAwaiter().GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Editable GLB:        {result.GlbPath}");
        Console.WriteLine($"  LODs / bones:        {result.Manifest.Lods.Count} / {result.Manifest.BoneCount:N0}");
        Console.WriteLine($"  Stable vertex IDs:   {result.Manifest.StableVertexAttribute}");
        Console.WriteLine($"  Inspection:          {result.Manifest.GlbInspectionErrors} errors, {result.Manifest.GlbInspectionWarnings} warnings");
        Console.WriteLine($"  Manifest:            {result.ManifestPath}");
    }
    return result.Manifest.GlbInspectionErrors == 0 ? 0 : 1;
}

/// <summary>
/// Swaps one player outfit onto another body's outfit, headless: writes each replaced piece's new GLB, its repainted
/// pictures and a viewer edits file (for model-viewer-scene --edits), and prints how well it fitted. Colours come from
/// baked PNGs in --bakes (piece number/slot.png, from the viewer's bake) when given, otherwise from the stored pictures;
/// --lit-bakes and --surface-bakes hold the donor's lit and roughness/metalness bakes; --target-bakes and
/// --target-surface-bakes the same of an earlier run's result (the replaced pieces with that run's pictures), from which
/// the replaced outfit's shaders are learned and the pictures painted again to match the donor.
/// </summary>
static int OutfitSwap(string packagesRoot, string? indexCache, string? wear, string? replace, string? outPath, string? cachePath,
    string? bakesPath, string? targetBakesPath, string? litBakesPath, string? surfaceBakesPath, string? targetSurfaceBakesPath, bool json)
{
    if (string.IsNullOrWhiteSpace(indexCache) || string.IsNullOrWhiteSpace(wear) || string.IsNullOrWhiteSpace(replace) ||
        string.IsNullOrWhiteSpace(outPath))
        throw new ArgumentException("outfit-swap requires --index DIR --wear PIECE[,PIECE] --replace PIECE[,PIECE] --out DIR.");
    var root = Path.GetFullPath(packagesRoot);
    var output = Path.GetFullPath(outPath);
    var cache = Path.GetFullPath(cachePath ?? Path.Combine(output, "cache"));
    Directory.CreateDirectory(output);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var reader = new UpkPackageReader();
    var executable = UmodelLocator.FindExecutable() ?? throw new InvalidOperationException("Outfit swaps need the Lost Ark UModel backend.");
    var bridge = new UmodelBridge(executable);
    var library = new OutfitSwapPackageLibrary(new PackageIndexStore(Path.GetFullPath(indexCache)), root, reader, provider,
        new SkeletalMeshReconnaissanceService(reader, provider, bridge),
        new SkeletalMeshReconnaissanceWorkspaceStore(Path.Combine(cache, "reconnaissance")),
        new SkeletalMeshEditableExportService(reader, provider), Path.Combine(cache, "pieces"));
    var scenes = new ModelViewerSceneService(bridge, new ModelViewerCache(Path.Combine(cache, "model-viewer")), protectionProvider: provider);
    var token = CancellationToken.None;

    async Task<(OutfitSwapPiece Piece, OutfitSwapScene Scene, string Glb)> Load(string name)
    {
        var source = await library.LocateAsync(name, token) ?? throw new InvalidOperationException($"{name} is not in the library index.");
        var glb = await library.EditableGlbAsync(name, token) ?? throw new InvalidOperationException($"Arkive cannot rebuild {name}.");
        var mesh = OutfitSwapMesh.Read(await File.ReadAllBytesAsync(glb, token));
        var prepared = await scenes.PrepareAsync(source.ViewerRequest, null, token);
        var scene = new OutfitSwapScene(prepared.Directory, prepared.Scene);
        return (new OutfitSwapPiece(name, mesh, scene.Materials), scene, glb);
    }

    var progress = new Progress<string>(text => { if (!json) Console.WriteLine($"  {text}"); });
    var donors = wear.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(name => Load(name).GetAwaiter().GetResult()).ToArray();
    var targets = replace.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(name => Load(name).GetAwaiter().GetResult()).ToArray();
    var service = new OutfitSwapService(library, Path.Combine(cache, "outfit-swap"));
    var fit = service.FitAsync(donors.Select(item => item.Piece).ToArray(), targets.Select(item => item.Piece).ToArray(),
        "cli", progress, token).GetAwaiter().GetResult();
    IReadOnlyDictionary<int, string>[] Bakes(string? root, int count) => Enumerable.Range(0, count).Select(index =>
    {
        var folder = root is null ? null : Path.Combine(Path.GetFullPath(root), index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return (IReadOnlyDictionary<int, string>)(folder is null || !Directory.Exists(folder)
            ? new Dictionary<int, string>()
            : Directory.EnumerateFiles(folder, "*.png").ToDictionary(
                path => int.Parse(new string(Path.GetFileNameWithoutExtension(path).TakeWhile(char.IsDigit).ToArray()), System.Globalization.CultureInfo.InvariantCulture),
                path => path));
    }).ToArray();
    var baked = Bakes(bakesPath, donors.Length);
    var targetBaked = targetBakesPath is null ? null : Bakes(targetBakesPath, targets.Length);
    var litBaked = litBakesPath is null ? null : Bakes(litBakesPath, donors.Length);
    var surfaceBaked = surfaceBakesPath is null ? null : Bakes(surfaceBakesPath, donors.Length);
    var targetSurfaceBaked = targetSurfaceBakesPath is null ? null : Bakes(targetSurfaceBakesPath, targets.Length);
    // With bakes of an earlier run's result (lit, and roughness/metalness), learn how the replaced outfit's shaders draw
    // the pictures and paint again to match the donor's lit colours and surfaces.
    var picturesFolder = Path.Combine(output, "pictures");
    OutfitSwapCalibration? calibration = null;
    if ((targetBaked is not null || targetSurfaceBaked is not null) && Directory.Exists(picturesFolder))
    {
        var painted = Directory.EnumerateFiles(picturesFolder, "*.png").ToDictionary(path => Path.GetFileNameWithoutExtension(path), path => path, StringComparer.OrdinalIgnoreCase);
        var wanted = litBaked is null ? null : OutfitSwapPictures.PaintAsync(fit.Plan, donors.Select(item => item.Piece).ToArray(), donors.Select(item => item.Scene).ToArray(),
            litBaked, targets.Select(item => item.Scene).ToArray(), Path.Combine(output, "wanted"), token).GetAwaiter().GetResult().Pictures;
        calibration = OutfitSwapCalibration.LearnAsync(wanted, painted, targets.Select(item => item.Scene).ToArray(), targetBaked, targetSurfaceBaked, token)
            .GetAwaiter().GetResult();
        foreach (var (name, curve) in calibration.Colour) Console.Error.WriteLine($"colour gain {name}: {curve.Gain:N2}");
        foreach (var name in calibration.Roughness.Keys) Console.Error.WriteLine($"roughness curve {name}");
    }
    var pictures = OutfitSwapPictures.PaintAsync(fit.Plan, donors.Select(item => item.Piece).ToArray(), donors.Select(item => item.Scene).ToArray(),
        baked, targets.Select(item => item.Scene).ToArray(), picturesFolder, token, targetBaked, calibration, surfaceBaked).GetAwaiter().GetResult();
    var results = new List<object>();
    for (var i = 0; i < targets.Length; i++)
    {
        var piece = fit.Plan.Pieces[i];
        var glbPath = Path.Combine(output, piece.ObjectName + ".glb");
        File.WriteAllBytes(glbPath, OutfitSwapMesh.WriteInto(File.ReadAllBytes(targets[i].Glb), piece.Mesh));
        var textureNames = targets[i].Scene.Scene.Textures.Select(texture => texture.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var materialNames = targets[i].Scene.Scene.Materials.Select(material => material.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edits = new ModelViewerEdits("Outfit swap", glbPath,
            pictures.Pictures.Where(pair => textureNames.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value),
            pictures.Materials.Where(pair => materialNames.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value), []);
        var editsPath = Path.Combine(output, piece.ObjectName + ".edits.json");
        File.WriteAllText(editsPath, JsonSerializer.Serialize(edits, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        results.Add(new
        {
            piece = piece.ObjectName, hidden = piece.Hidden, glb = glbPath, edits = editsPath, vertices = piece.Mesh.VertexCount,
            triangles = piece.Mesh.Sections.Sum(section => section.Triangles.Length) / 3,
        });
    }
    var report = fit.Report;
    if (json)
        WriteJson(new
        {
            from = report.From.Id, to = report.To.Id, report.LearnedCostumes, report.ClippingBeforePercent, report.ClippingPercent,
            report.StretchedPercent, report.Notes, canvases = fit.Plan.Canvases.Select(canvas => new { canvas.Key, tiles = canvas.Tiles.Count }),
            pieces = results,
        });
    else
    {
        Console.WriteLine($"{report.From.DisplayName} -> {report.To.DisplayName}: learned from {report.LearnedCostumes} shared costume(s)");
        Console.WriteLine($"  Clipping: {report.ClippingBeforePercent:N2}% before push-out, {report.ClippingPercent:N2}% after; stretched faces {report.StretchedPercent:N2}%");
        foreach (var canvas in fit.Plan.Canvases) Console.WriteLine($"  Picture {canvas.Key}: {canvas.Tiles.Count} material(s)");
        foreach (var note in report.Notes) Console.WriteLine($"  Note: {note}");
        foreach (var piece in fit.Plan.Pieces) Console.WriteLine($"  {piece.ObjectName}: {(piece.Hidden ? "hidden" : $"{piece.Mesh.VertexCount:N0} vertices")}");
        Console.WriteLine($"  Output: {output}");
    }
    return 0;
}

/// <summary>
/// Swaps one player outfit onto another body's outfit with the outfit's own materials (the replaced pieces draw each
/// donor material from its own package, nothing is repainted) and saves it as a mod in an Arkive project root, headless.
/// </summary>
/// <param name="gamePath">The Packages folder the mod is made for (a scratch copy for tests); the library folder by default.</param>
static int OutfitSwapMod(string packagesRoot, string? indexCache, string? wear, string? replace, string? projectRoot, string? name,
    string? cachePath, string? gamePath, bool json)
{
    if (string.IsNullOrWhiteSpace(indexCache) || string.IsNullOrWhiteSpace(wear) || string.IsNullOrWhiteSpace(replace) ||
        string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(name))
        throw new ArgumentException("outfit-swap-mod requires --index DIR --wear PIECE[,PIECE] --replace PIECE[,PIECE] --root DIR --name TEXT.");
    var root = Path.GetFullPath(packagesRoot);
    var projectDirectory = Path.GetFullPath(projectRoot);
    Directory.CreateDirectory(projectDirectory);
    var cache = Path.GetFullPath(cachePath ?? Path.Combine(projectDirectory, "cache"));
    var environment = CreateModEnvironment(projectDirectory);
    var game = Path.GetFullPath(gamePath ?? root);
    environment.Context.Library.SetGamePackagesDirectoryAsync(game).GetAwaiter().GetResult();
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var reader = new UpkPackageReader();
    var catalogReader = new AssetCatalogReader(reader, provider);
    var executable = UmodelLocator.FindExecutable() ?? throw new InvalidOperationException("Outfit swaps need the Lost Ark UModel backend.");
    var bridge = new UmodelBridge(executable);
    var token = CancellationToken.None;
    var library = new OutfitSwapPackageLibrary(new PackageIndexStore(Path.GetFullPath(indexCache)), root, reader, provider,
        new SkeletalMeshReconnaissanceService(reader, provider, bridge),
        new SkeletalMeshReconnaissanceWorkspaceStore(Path.Combine(cache, "reconnaissance")),
        new SkeletalMeshEditableExportService(reader, provider), Path.Combine(cache, "pieces"),
        // Pieces whose package the game folder holds are read from there (the clean copies a test folder carries).
        (path, cancellationToken) => File.Exists(Path.Combine(game, Path.GetFileName(path)))
            ? Task.FromResult(Path.Combine(game, Path.GetFileName(path)))
            : environment.Apply.ResolveCleanPackageAsync(path, cancellationToken));

    OutfitSwapPiece Load(string piece)
    {
        var glb = library.EditableGlbAsync(piece, token).GetAwaiter().GetResult() ??
                  throw new InvalidOperationException($"Arkive cannot rebuild {piece}.");
        return new OutfitSwapPiece(piece, OutfitSwapMesh.Read(File.ReadAllBytes(glb)), []);
    }
    var progress = new Progress<string>(text => { if (!json) Console.WriteLine($"  {text}"); });
    var donors = wear.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Load).ToArray();
    var targets = replace.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Load).ToArray();
    var targetBody = targets.Select(piece => OutfitSwapBodies.Detect(piece.Mesh, piece.ObjectName)).FirstOrDefault(profile => profile is not null);
    var sources = OutfitSwapMaterialSources.ReadAsync(library, donors,
        targetBody is null ? null : OutfitSwapBodies.For(targetBody), token).GetAwaiter().GetResult();
    var service = new OutfitSwapService(library, Path.Combine(cache, "outfit-swap"));
    var fit = service.FitAsync(donors, targets, "cli", progress, token, sources).GetAwaiter().GetResult();

    var report = fit.Report;
    var description = $"Outfit swap: {string.Join(", ", donors.Select(piece => piece.ObjectName))} ({report.From.DisplayName}) worn in place of " +
                      $"{string.Join(", ", targets.Select(piece => piece.ObjectName))} ({report.To.DisplayName}), with the outfit's own materials.";
    var workspace = new ArkiveModAuthoringWorkspace(Path.Combine(environment.Context.Library.StateDirectory, "authoring"));
    var draft = workspace.CreateAsync(new ArkiveModMetadata(name, "1.0.0", "", description,
        Tags: ["outfit-swap", "outfit-swap:own-materials", $"outfit-swap:from={report.From.Id}", $"outfit-swap:to={report.To.Id}"]), token)
        .GetAwaiter().GetResult();
    var writer = new OutfitSwapModWriter(environment.Context.Edits, provider, Path.Combine(projectDirectory, "staging", "material-candidates"));
    for (var i = 0; i < targets.Length; i++)
    {
        var piece = fit.Plan.Pieces[i];
        var source = library.LocateAsync(piece.ObjectName, token).GetAwaiter().GetResult()!;
        var clean = environment.Apply.ResolveCleanPackageAsync(Path.Combine(game, Path.GetFileName(source.CleanPackagePath)), token)
            .GetAwaiter().GetResult();
        var catalogue = catalogReader.Read(clean);
        var export = catalogue.Assets.First(asset => asset.ObjectName.Equals(piece.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                                                     asset.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
        if (!json) Console.WriteLine($"  Adding {piece.ObjectName}");
        var edit = environment.Materializer.CreateEditableAsync(new ArkiveModAssetSelection(clean, catalogue.Summary.FolderName,
            export.ClassName, export.ObjectName, null, export.SerialOffset, export.ExportIndex), token, null).GetAwaiter().GetResult();
        draft = workspace.SaveAsync(draft with { EditIds = draft.EditIds.Append(edit.EditId).ToArray() }, token).GetAwaiter().GetResult();
        writer.WriteMeshAsync(edit, piece.Mesh, null, token).GetAwaiter().GetResult();
    }
    if (!json) Console.WriteLine("  Checking the meshes and saving the mod");
    var saved = environment.Authoring.SaveDraftAsync(draft, token).GetAwaiter().GetResult();
    var file = Path.Combine(environment.Context.Library.ModsDirectory, saved.FileName);
    if (json)
        WriteJson(new
        {
            mod = saved.ModId, file, from = report.From.Id, to = report.To.Id, report.ClippingPercent, report.Notes,
            pieces = fit.Plan.Pieces.Select(piece => new
            {
                piece = piece.ObjectName, piece.Hidden, materials = piece.Mesh.MaterialReferences.Values.Select(reference => new
                {
                    material = reference.Material.Path, reference.Replaces,
                }),
            }),
        });
    else
    {
        Console.WriteLine($"{report.From.DisplayName} -> {report.To.DisplayName}: clipping {report.ClippingPercent:N2}%");
        foreach (var piece in fit.Plan.Pieces)
            foreach (var reference in piece.Mesh.MaterialReferences.Values)
                Console.WriteLine($"  {piece.ObjectName}: {reference.Material.Path}" + (reference.Replaces is null ? " (added slot)" : $" in place of {reference.Replaces}"));
        foreach (var note in report.Notes) Console.WriteLine($"  Note: {note}");
        Console.WriteLine($"  Saved: {file}");
    }
    return 0;
}

static int BodyProportions(string glbPath, string? outPath, IReadOnlyList<string> settings, BodyProportionFollow follow, bool json)
{
    var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    foreach (var setting in settings)
    {
        var parts = setting.Split('=', 2);
        if (parts.Length != 2 || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"--set expects AREA=PERCENT, for example chest=20; got '{setting}'.");
        values[parts[0]] = value;
    }
    var bytes = File.ReadAllBytes(glbPath);
    // Editable exports are named OBJECT.arkive-edit.glb; the object name identifies tuned NPCs.
    var model = BodyProportionEngine.Build(bytes, follow, objectName: Path.GetFileName(glbPath).Split('.')[0]);
    var unknown = values.Keys.Where(key => model.Profile.Zones.All(zone => !zone.Id.Equals(key, StringComparison.OrdinalIgnoreCase))).ToArray();
    if (unknown.Length > 0)
        throw new ArgumentException($"Unknown area(s) {string.Join(", ", unknown)}; this body has {string.Join(", ", model.Profile.Zones.Select(zone => zone.Id))}.");
    var canonical = model.Profile.Zones.Where(zone => values.ContainsKey(zone.Id)).ToDictionary(zone => zone.Id, zone => values[zone.Id]);
    if (outPath is not null) File.WriteAllBytes(outPath, BodyProportionEngine.Apply(bytes, model, canonical));
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            profile = model.Profile.Id,
            looseClothing = follow.LooseClothing,
            hair = follow.Hair,
            looseVertices = model.LooseVertexCount,
            hairVertices = model.HairVertexCount,
            areas = model.AffectedZones,
            meshes = model.Meshes.Select(mesh => new { mesh.MeshName, mesh.VertexCount, areas = mesh.Offsets.Keys }),
            output = outPath,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    Console.WriteLine($"Body:          {model.Profile.DisplayName}");
    Console.WriteLine($"Meshes (LODs): {model.Meshes.Count} ({string.Join(", ", model.Meshes.Select(mesh => $"{mesh.MeshName} {mesh.VertexCount:N0}"))})");
    Console.WriteLine($"Areas:         {string.Join(", ", model.Profile.Zones.Where(zone => model.AffectedZones.Contains(zone.Id)).Select(zone => zone.Id))}");
    Console.WriteLine($"Loose cloth:   {model.LooseVertexCount:N0} vertices{(follow.LooseClothing ? " (follow the body)" : " (unchanged)")}");
    Console.WriteLine($"Hair:          {model.HairVertexCount:N0} vertices{(follow.Hair ? " (follows the body)" : " (unchanged)")}");
    if (outPath is not null) Console.WriteLine($"Reshaped GLB:  {Path.GetFullPath(outPath)}");
    return 0;
}

static int ModelViewerAnimations(string packagePath, string animSet, string? cacheRoot, bool keepAll, bool json)
{
    var umodel = UmodelLocator.FindExecutable() ?? throw new InvalidOperationException("UModel was not found.");
    var service = new ModelViewerAnimationService(new UmodelBridge(umodel),
        cacheRoot ?? Path.Combine(Path.GetTempPath(), "arkive-animations"));
    var set = service.PrepareAsync(packagePath, animSet, keepAll).GetAwaiter().GetResult();
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(set, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    Console.WriteLine($"AnimSet:     {set.AnimSetName}");
    Console.WriteLine($"Bones:       {set.Bones.Count:N0}");
    Console.WriteLine($"Animations:  {set.Animations.Count:N0} ({string.Join(", ", set.Animations.GroupBy(item => item.Group).Select(group => $"{group.Key} {group.Count()}"))})");
    Console.WriteLine($"Folder:      {set.Directory}");
    return 0;
}

static int SkeletalMeshPreflight(string sourceManifestPath, string? editedPath, bool json)
{
    if (string.IsNullOrWhiteSpace(editedPath))
        throw new ArgumentException("skeletal-mesh-preflight requires --edited FILE.glb.");
    var manifestPath = Path.GetFullPath(sourceManifestPath);
    var manifest = JsonSerializer.Deserialize<SkeletalMeshEditableExportManifest>(
        File.ReadAllText(manifestPath),
        new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        }) ?? throw new InvalidDataException("The editable SkeletalMesh manifest is empty.");
    var directory = Path.GetDirectoryName(manifestPath)!;
    var glbPath = Path.GetFullPath(Path.Combine(directory, manifest.GlbRelativePath));
    var glb = new FileInfo(glbPath);
    var source = new SkeletalMeshEditableWorkspaceEntry(
        directory,
        manifestPath,
        glbPath,
        manifest,
        glb.Exists,
        glb.Exists && glb.Length == manifest.GlbBytes,
        manifest.SchemaVersion == SkeletalMeshEditableExportService.CurrentSchemaVersion);
    var report = new SkeletalMeshEditPreflightService()
        .InspectAsync(source, Path.GetFullPath(editedPath))
        .GetAwaiter().GetResult();
    if (json) WriteJson(report);
    else
    {
        Console.WriteLine($"{report.PackageAlias}.{report.ObjectName}");
        Console.WriteLine($"  Readiness:           {report.Readiness}");
        Console.WriteLine($"  Import profile:      {report.ImportProfile}");
        Console.WriteLine($"  Changed positions:   {report.ChangedPositionVertexCount:N0}");
        Console.WriteLine($"  Changed normals:     {report.ChangedNormalVertexCount:N0}");
        Console.WriteLine($"  Changed tangents:    {report.ChangedTangentVertexCount:N0}");
        Console.WriteLine($"  Maximum displacement:{report.MaximumPositionDeltaMetres,12:N6} m");
        Console.WriteLine($"  Validation:          {report.ErrorCount:N0} errors, {report.WarningCount:N0} warnings");
        if (report.Topology is { } topology)
        {
            Console.WriteLine($"  Candidate geometry:  {topology.CandidateVertexCount:N0} vertices / {topology.CandidateTriangleCount:N0} triangles");
            Console.WriteLine($"  LOD strategy:        {topology.LodStrategy}");
            Console.WriteLine($"  Materials / bones:   {topology.CandidateMaterialCount:N0} / {topology.CandidateUsedBoneCount:N0}");
        }
        if (report.SkeletonCompatibility is { } skeleton)
        {
            Console.WriteLine($"  Skeleton contract:   {skeleton.Compatibility}");
            Console.WriteLine($"  Skeleton mapping:    {skeleton.ExactNameMappingCount:N0}/{skeleton.Source.BoneCount:N0} names, " +
                              $"{skeleton.MappedSourceWeightPercent:N3}% weighted coverage");
        }
        if (report.AnimationValidation is { } animation)
        {
            Console.WriteLine($"  Animation stress:    {(animation.Passed ? "passed" : "refused")} / " +
                              $"{animation.PoseCount:N0} deterministic pose(s)");
            Console.WriteLine($"  Bone influence:      distance={animation.BoneWeightDistributionDistance:P2}, " +
                              $"missing={animation.MissingSignificantSourceWeightPercent:N2}%");
            Console.WriteLine($"  Animated geometry:   bounds={animation.MaximumCandidateBoundsExpansionRatio:N3}x, " +
                              $"edge={animation.MaximumCandidateEdgeStretchRatio:N3}x, " +
                              $"non-finite={animation.NonFiniteCandidateVertexCount:N0}");
            Console.WriteLine("  Animation scope:     synthetic CPU-skinned stress poses; game clips are not executed");
        }
        if (report.DeformationDependencies is { } dependencies)
        {
            Console.WriteLine($"  Cloth dependencies:  {dependencies.ClothState} / {dependencies.ActiveClothReferenceCount:N0} active ref(s)");
            Console.WriteLine($"  Morph dependencies:  {dependencies.MorphState} / {dependencies.MorphTargetCount:N0} target(s) / {dependencies.MorphDeltaCount:N0} delta(s)");
            Console.WriteLine($"  Topology dependency: {(dependencies.TopologyChangingSupported ? "compatible" : "refused")} — {dependencies.TopologyChangingReason}");
        }
        foreach (var issue in report.Issues)
            Console.WriteLine($"    {issue.Severity} {issue.Code}: {issue.Message}");
    }
    return report.Readiness == SkeletalMeshEditReadiness.Refused ? 1 : 0;
}

static int SkeletalMeshCompareSkeletons(string targetManifestPath, string? donorManifestPath, bool json)
{
    if (string.IsNullOrWhiteSpace(donorManifestPath))
        throw new ArgumentException("skeletal-mesh-compare-skeletons requires --donor MANIFEST.json.");
    var donor = LoadEditableSkeletalMeshEntry(donorManifestPath);
    var target = LoadEditableSkeletalMeshEntry(targetManifestPath);
    var report = new SkeletalMeshSkeletonCompatibilityService()
        .AnalyzeAsync(donor, target).GetAwaiter().GetResult();
    if (json) WriteJson(report);
    else
    {
        Console.WriteLine($"Donor:  {report.Source.PackageAlias}.{report.Source.ObjectName}");
        Console.WriteLine($"Target: {report.Target.PackageAlias}.{report.Target.ObjectName}");
        Console.WriteLine($"  Classification:      {report.Compatibility}");
        Console.WriteLine($"  Exact target rig:    {report.ExactSkeletonIdentity}");
        Console.WriteLine($"  Bones:               {report.Source.BoneCount:N0} donor / {report.Target.BoneCount:N0} target");
        Console.WriteLine($"  Exact name mappings: {report.ExactNameMappingCount:N0}");
        Console.WriteLine($"  Mapped weight:       {report.MappedSourceWeightPercent:N3}%");
        Console.WriteLine($"  Hierarchy weight:    {report.HierarchyConsistentSourceWeightPercent:N3}%");
        Console.WriteLine($"  Missing / core:      {report.MissingSourceBoneCount:N0} / {report.MissingCoreBoneCount:N0}");
        Console.WriteLine($"  Bind delta:          {report.MaximumMappedBindTranslationDeltaMetres:N6} m / " +
                          $"{report.MaximumMappedBindRotationDeltaDegrees:N3} degrees");
        Console.WriteLine($"  Summary:             {report.Summary}");
        foreach (var issue in report.Issues)
            Console.WriteLine($"    {issue.Severity} {issue.Code}: {issue.Message}");
    }
    return report.Compatibility == SkeletalMeshSkeletonCompatibilityKind.Incompatible ? 1 : 0;
}

static int SkeletalMeshCreateRetarget(
    string targetManifestPath,
    string? donorManifestPath,
    string? outputPath,
    string? displayName,
    bool json)
{
    if (string.IsNullOrWhiteSpace(donorManifestPath))
        throw new ArgumentException("skeletal-mesh-create-retarget requires --donor MANIFEST.json.");
    var donor = LoadEditableSkeletalMeshEntry(donorManifestPath);
    var target = LoadEditableSkeletalMeshEntry(targetManifestPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(targetManifestPath))!,
            $"retarget-{DateTime.UtcNow:yyyyMMdd-HHmmss}")
        : Path.GetFullPath(outputPath);
    var result = new SkeletalMeshRetargetWorkspaceService().CreateAsync(
        new SkeletalMeshRetargetWorkspaceRequest(donor, target, output, displayName))
        .GetAwaiter().GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.DonorPackageAlias}.{result.Manifest.DonorObjectName} -> " +
                          $"{result.Manifest.TargetPackageAlias}.{result.Manifest.TargetObjectName}");
        Console.WriteLine($"  Classification:      {result.Manifest.Compatibility}");
        Console.WriteLine($"  Mapped weight:       {result.Manifest.MappedSourceWeightPercent:N3}%");
        Console.WriteLine($"  Missing / core:      {result.Manifest.MissingSourceBoneCount:N0} / {result.Manifest.MissingCoreBoneCount:N0}");
        Console.WriteLine($"  Automatic retarget:  {result.Manifest.AutomaticRetargetPerformed}");
        Console.WriteLine($"  Blender setup:       {result.BlenderSetupScriptPath}");
        Console.WriteLine($"  Instructions:        {result.InstructionsPath}");
        Console.WriteLine($"  Manifest:            {result.ManifestPath}");
    }
    return 0;
}

static SkeletalMeshEditableWorkspaceEntry LoadEditableSkeletalMeshEntry(string manifestValue)
{
    var manifestPath = Path.GetFullPath(manifestValue);
    var manifest = JsonSerializer.Deserialize<SkeletalMeshEditableExportManifest>(
        File.ReadAllText(manifestPath),
        new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        }) ?? throw new InvalidDataException("The editable SkeletalMesh manifest is empty.");
    var directory = Path.GetDirectoryName(manifestPath) ??
                    throw new InvalidDataException("The editable SkeletalMesh directory is unavailable.");
    if (Path.IsPathRooted(manifest.GlbRelativePath))
        throw new InvalidDataException("The editable SkeletalMesh GLB path must be relative.");
    var glbPath = Path.GetFullPath(Path.Combine(directory, manifest.GlbRelativePath));
    var file = new FileInfo(glbPath);
    return new SkeletalMeshEditableWorkspaceEntry(
        directory, manifestPath, glbPath, manifest, file.Exists,
        file.Exists && file.Length == manifest.GlbBytes,
        manifest.SchemaVersion == SkeletalMeshEditableExportService.CurrentSchemaVersion);
}

static int SkeletalMeshRegisterEdit(
    string sourceManifestPath,
    string? editedPath,
    string? workspacePath,
    string? displayName,
    bool json)
{
    if (string.IsNullOrWhiteSpace(editedPath) || string.IsNullOrWhiteSpace(workspacePath))
        throw new ArgumentException(
            "skeletal-mesh-register-edit requires --edited FILE.glb --workspace DIR.");
    var manifestPath = Path.GetFullPath(sourceManifestPath);
    var manifest = JsonSerializer.Deserialize<SkeletalMeshEditableExportManifest>(
        File.ReadAllText(manifestPath),
        new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        }) ?? throw new InvalidDataException("The editable SkeletalMesh manifest is empty.");
    var directory = Path.GetDirectoryName(manifestPath)!;
    var glbPath = Path.GetFullPath(Path.Combine(directory, manifest.GlbRelativePath));
    var glb = new FileInfo(glbPath);
    var source = new SkeletalMeshEditableWorkspaceEntry(
        directory, manifestPath, glbPath, manifest, glb.Exists,
        glb.Exists && glb.Length == manifest.GlbBytes,
        manifest.SchemaVersion == SkeletalMeshEditableExportService.CurrentSchemaVersion);
    var result = new SkeletalMeshEditWorkspaceStore(Path.GetFullPath(workspacePath))
        .RegisterAsync(source, Path.GetFullPath(editedPath), displayName)
        .GetAwaiter().GetResult();
    if (json) WriteJson(result.Entry.Manifest);
    else
    {
        Console.WriteLine($"{result.Entry.Manifest.PackageAlias}.{result.Entry.Manifest.ObjectName}");
        Console.WriteLine($"  Edit:                {result.Entry.Manifest.DisplayName}");
        Console.WriteLine($"  Readiness:           {result.Preflight.Readiness}");
        Console.WriteLine($"  Import profile:      {result.Preflight.ImportProfile}");
        Console.WriteLine($"  Changed positions:   {result.Preflight.ChangedPositionVertexCount:N0}");
        Console.WriteLine($"  Validation:          {result.Preflight.ErrorCount:N0} errors, {result.Preflight.WarningCount:N0} warnings");
        Console.WriteLine($"  Stored GLB:          {result.Entry.EditedGlbPath}");
        Console.WriteLine($"  Manifest:            {result.Entry.ManifestPath}");
    }
    return result.Preflight.Readiness == SkeletalMeshEditReadiness.Refused ? 1 : 0;
}

static int SkeletalMeshEncodeNative(string editManifestPath, string? outputPath, bool json)
{
    var manifestPath = Path.GetFullPath(editManifestPath);
    var manifest = JsonSerializer.Deserialize<SkeletalMeshEditRegistrationManifest>(
        File.ReadAllText(manifestPath),
        new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        }) ?? throw new InvalidDataException("The edited SkeletalMesh manifest is empty.");
    var directory = Path.GetDirectoryName(manifestPath) ??
                    throw new InvalidDataException("The edited SkeletalMesh directory is unavailable.");
    var editedGlbPath = Path.GetFullPath(Path.Combine(directory, manifest.EditedGlbRelativePath));
    var entry = new SkeletalMeshEditWorkspaceEntry(
        directory, manifestPath, editedGlbPath, manifest);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(directory, "native-encoding")
        : Path.GetFullPath(outputPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var result = new SkeletalMeshNativeEncodingService(protectionProvider: provider)
        .EncodeAsync(new SkeletalMeshNativeEncodingRequest(entry, output))
        .GetAwaiter().GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Edit:                {result.Manifest.EditDisplayName}");
        Console.WriteLine($"  Position writes:     {result.Manifest.Lods.Sum(lod => lod.PositionWriteCount):N0}");
        Console.WriteLine($"  Tangent-frame writes:{result.Manifest.Lods.Sum(lod => lod.TangentFrameWriteCount),12:N0}");
        Console.WriteLine($"  Changed bytes:       {result.Manifest.ActualChangedByteCount:N0}");
        Console.WriteLine($"  Locked bytes changed:{result.Manifest.ChangedLockedByteCount,12:N0}");
        Console.WriteLine($"  Decode-back passed:  {result.Manifest.DecodeBackPassed}");
        Console.WriteLine($"  Encoded export:      {result.EncodedExportPath}");
        Console.WriteLine($"  Decoded proof GLB:   {result.DecodedProofGlbPath}");
        Console.WriteLine($"  Manifest:            {result.ManifestPath}");
    }
    return result.Manifest.LockedBytesPreserved && result.Manifest.DecodeBackPassed ? 0 : 1;
}

static int SkeletalMeshCompileTopology(string editManifestPath, string? outputPath, bool json)
{
    var manifestPath = Path.GetFullPath(editManifestPath);
    var manifest = JsonSerializer.Deserialize<SkeletalMeshEditRegistrationManifest>(
        File.ReadAllText(manifestPath),
        new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        }) ?? throw new InvalidDataException("The edited SkeletalMesh manifest is empty.");
    var directory = Path.GetDirectoryName(manifestPath) ??
                    throw new InvalidDataException("The edited SkeletalMesh directory is unavailable.");
    var entry = new SkeletalMeshEditWorkspaceEntry(
        directory, manifestPath,
        Path.GetFullPath(Path.Combine(directory, manifest.EditedGlbRelativePath)), manifest);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(directory, "topology-compilation")
        : Path.GetFullPath(outputPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var result = new SkeletalMeshTopologyCompilerService(protectionProvider: provider)
        .CompileAsync(new SkeletalMeshTopologyCompilationRequest(entry, output))
        .GetAwaiter().GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Edit:                {result.Manifest.EditDisplayName}");
        Console.WriteLine($"  Native LOD slots:    {result.Manifest.Lods.Count:N0}");
        Console.WriteLine($"  Compiled vertices:   {result.Manifest.Lods.Sum(lod => lod.CompiledVertexCount):N0}");
        Console.WriteLine($"  Compiled triangles:  {result.Manifest.Lods.Sum(lod => lod.CompiledTriangleCount):N0}");
        Console.WriteLine($"  Export size delta:   {result.Manifest.SerialSizeDelta:+#,#;-#,#;0} bytes");
        Console.WriteLine($"  Decode-back passed:  {result.Manifest.DecodeBackPassed}");
        Console.WriteLine($"  Package integration: {result.Manifest.PackageIntegrationEnabled}");
        Console.WriteLine($"  Compiled export:     {result.CompiledExportPath}");
        Console.WriteLine($"  Decoded proof GLB:   {result.DecodedProofGlbPath}");
        Console.WriteLine($"  Manifest:            {result.ManifestPath}");
    }
    return result.Manifest.DecodeBackPassed && result.Manifest.PackageIntegrationEnabled ? 0 : 1;
}

static int SkeletalMeshBuildStagedUpk(string encodingManifestPath, string? outputPath, bool json)
{
    var manifestPath = Path.GetFullPath(encodingManifestPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(manifestPath)!, "staged-upk")
        : Path.GetFullPath(outputPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var result = new SkeletalMeshStagedUpkService(protectionProvider: provider)
        .BuildAsync(new SkeletalMeshStagedUpkRequest(manifestPath, output))
        .GetAwaiter().GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Staged UPK:          {result.OutputUpkPath}");
        Console.WriteLine($"  Rebuilt chunks:      {result.Manifest.RebuiltChunkCount:N0}");
        Console.WriteLine($"  Byte-identical chunks:{result.Manifest.ByteIdenticalChunkCount,11:N0}");
        Console.WriteLine($"  Export table same:   {result.Manifest.ExportTableUnchanged}");
        Console.WriteLine($"  Native decode:       {result.Manifest.TargetNativeDecodePassed}");
        Console.WriteLine($"  UModel preview ready:{result.Manifest.UmodelPreviewReady,12}");
        Console.WriteLine($"  Deployment ready:    {result.Manifest.DeploymentReady}");
        Console.WriteLine($"  Manifest:            {result.ManifestPath}");
    }
    return result.Manifest.UmodelPreviewReady && !result.Manifest.DeploymentReady ? 0 : 1;
}

static int SkeletalMeshBuildTopologyUpk(string compilationManifestPath, string? outputPath, bool json)
{
    var manifestPath = Path.GetFullPath(compilationManifestPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(manifestPath)!, "topology-staged-upk")
        : Path.GetFullPath(outputPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var result = new SkeletalMeshStagedUpkService(protectionProvider: provider)
        .BuildTopologyAsync(new SkeletalMeshTopologyStagedUpkRequest(manifestPath, output))
        .GetAwaiter().GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Topology staged UPK: {result.OutputUpkPath}");
        Console.WriteLine($"  Export bytes:        {result.Manifest.OriginalSerialSize:N0} -> {result.Manifest.AppendedSerialSize:N0}");
        Console.WriteLine($"  Appended offset:     0x{result.Manifest.AppendedSerialOffset:X}");
        Console.WriteLine($"  Rebuilt chunks:      {result.Manifest.RebuiltChunkCount:N0}");
        Console.WriteLine($"  Unrelated exports:   {result.Manifest.UnrelatedExportsEquivalent}");
        Console.WriteLine($"  Native decode:       {result.Manifest.TargetNativeDecodePassed}");
        Console.WriteLine($"  UModel preview ready:{result.Manifest.UmodelPreviewReady,12}");
        Console.WriteLine($"  Deployment ready:    {result.Manifest.DeploymentReady}");
        Console.WriteLine($"  Manifest:            {result.ManifestPath}");
    }
    return result.Manifest.UmodelPreviewReady && !result.Manifest.DeploymentReady ? 0 : 1;
}

static int SkeletalMeshPrepareMod(
    string stagedManifestPath,
    string? outputPath,
    string? backupRootPath,
    string? displayName,
    bool json)
{
    var manifestPath = Path.GetFullPath(stagedManifestPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(manifestPath)!, "deployment-bundle")
        : Path.GetFullPath(outputPath);
    var projectRoot = UmodelLocator.FindProjectRoot() ?? Environment.CurrentDirectory;
    var backupRoot = string.IsNullOrWhiteSpace(backupRootPath)
        ? Path.Combine(projectRoot, "backups", "packages")
        : Path.GetFullPath(backupRootPath);
    var umodelPath = UmodelLocator.FindExecutable() ??
                     throw new FileNotFoundException(
                         "The Lost Ark UModel backend was not found beneath tools/external.");
    var staged = SkeletalMeshStagedUpkService.LoadManifestAsync(manifestPath)
        .GetAwaiter().GetResult();
    var name = string.IsNullOrWhiteSpace(displayName)
        ? $"{staged.ObjectName} mesh mod"
        : displayName.Trim();
    var result = new ArkiveMeshModBundleService(
            new UmodelModPackageExternalValidator(new UmodelBridge(umodelPath)))
        .BuildAsync(new ArkiveMeshModBundleRequest(
            manifestPath, output, backupRoot, name))
        .GetAwaiter().GetResult();
    if (json) WriteJson(result.Readiness);
    else
    {
        Console.WriteLine(result.Bundle.DisplayName);
        Console.WriteLine($"  Object:                {staged.ClassName} {staged.ObjectName}");
        Console.WriteLine($"  Mod ID:                {result.Bundle.ModId}");
        Console.WriteLine($"  Target package:        {result.Readiness.InstallTargetPath}");
        Console.WriteLine($"  Source SHA-256:         {result.Bundle.RequiredSourcePackageSha256}");
        Console.WriteLine($"  Payload SHA-256:        {result.Bundle.PayloadSha256}");
        Console.WriteLine($"  UModel exact-object load:{result.Readiness.ExpectedObjectFound,10}");
        Console.WriteLine($"  Backup bytes required: {result.Readiness.RequiredBackupBytes:N0}");
        Console.WriteLine($"  Ready to install:       {result.Readiness.ReadyForControlledInstall}");
        Console.WriteLine($"  Game files modified:    {result.Readiness.GameFilesModified}");
        Console.WriteLine($"  Bundle:                 {result.BundleManifestPath}");
        Console.WriteLine($"  Readiness:              {result.ReadinessManifestPath}");
        Console.WriteLine($"  Payload:                {result.PayloadPath}");
    }
    return result.Readiness.ReadyForControlledInstall && !result.Readiness.GameFilesModified ? 0 : 1;
}

static int PackageCompose(
    string packageManifestPath,
    IReadOnlyList<string> textureManifestPaths,
    IReadOnlyList<string> meshManifestPaths,
    IReadOnlyList<string> materialManifestPaths,
    IReadOnlyList<string> particleDocumentPaths,
    string? outputPath,
    string? backupRootPath,
    string? displayName,
    bool previewOnly,
    bool commit,
    bool json)
{
    if (textureManifestPaths.Count == 0 && meshManifestPaths.Count == 0 && materialManifestPaths.Count == 0 &&
        particleDocumentPaths.Count == 0)
        throw new ArgumentException(
            "package-compose requires at least one --texture, --material, --particle, or --mesh project manifest.");
    var packageManifest = Path.GetFullPath(packageManifestPath);
    var package = new GuidedTexturePackageWorkspace(Path.GetDirectoryName(packageManifest)!)
        .LoadAsync().GetAwaiter().GetResult()
        .Single(entry => entry.ManifestPath.Equals(
            packageManifest, StringComparison.OrdinalIgnoreCase));
    var textures = textureManifestPaths.Select(path =>
    {
        var manifest = Path.GetFullPath(path);
        return new GuidedTextureModWorkspace(Path.GetDirectoryName(manifest)!)
            .LoadAsync().GetAwaiter().GetResult()
            .Single(entry => entry.ManifestPath.Equals(manifest, StringComparison.OrdinalIgnoreCase));
    }).ToArray();
    var meshes = meshManifestPaths.Select(path =>
    {
        var manifest = Path.GetFullPath(path);
        return new SkeletalMeshEditWorkspaceStore(Path.GetDirectoryName(manifest)!)
            .LoadAsync().GetAwaiter().GetResult().Entries
            .Single(entry => entry.ManifestPath.Equals(manifest, StringComparison.OrdinalIgnoreCase));
    }).ToArray();
    var materials = materialManifestPaths.Select(path =>
    {
        var manifest = Path.GetFullPath(path);
        return new MaterialInstanceEditWorkspaceStore(Path.GetDirectoryName(manifest)!)
            .LoadEntryAsync(manifest).GetAwaiter().GetResult();
    }).ToArray();
    var particles = particleDocumentPaths.Select(path =>
    {
        var document = Path.GetFullPath(path);
        return new Arkive.Core.Particles.ParticleEffectEditWorkspaceStore(Path.GetDirectoryName(Path.GetDirectoryName(document))!)
            .LoadEntryAsync(document).GetAwaiter().GetResult();
    }).ToArray();

    var projectRoot = UmodelLocator.FindProjectRoot() ?? Environment.CurrentDirectory;
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(projectRoot, "work", "package-compositions",
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..25])
        : Path.GetFullPath(outputPath);
    var backupRoot = string.IsNullOrWhiteSpace(backupRootPath)
        ? Path.Combine(projectRoot, "backups", "packages")
        : Path.GetFullPath(backupRootPath);
    var umodelPath = UmodelLocator.FindExecutable() ??
                     throw new FileNotFoundException(
                         "The Lost Ark UModel backend was not found beneath tools/external.");
    var crunchPath = CrunchEncoderLocator.FindExecutable() ??
                     throw new FileNotFoundException(
                         $"{CrunchEncoderLocator.ExecutableFileName} was not found beneath tools/external.");
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var reader = new UpkPackageReader();
    var composer = CreateComposer(
        reader, provider, new UmodelBridge(umodelPath), crunchPath, new NativeTextureExportService(reader, provider),
        new MaterialInstanceEditService(provider));
    var progress = json
        ? null
        : new Progress<UnifiedPackageBuildProgress>(value => Console.Error.WriteLine(
            $"[{value.Step:N0}/{value.TotalSteps:N0}] {value.Stage} {value.ObjectName}: {value.Message}"));
    var result = composer.BuildAsync(
            new UnifiedPackageCompositionRequest(
                package, textures, meshes, output, backupRoot,
                string.IsNullOrWhiteSpace(displayName)
                    ? $"{package.Manifest.PackageAlias} combined mod"
                    : displayName.Trim())
            {
                MaterialEdits = materials,
                ParticleEdits = particles,
                ForcePreviewOnly = previewOnly,
            },
            progress)
        .GetAwaiter().GetResult();
    GuidedTexturePackageEntry? committedPackage = null;
    if (commit)
    {
        committedPackage = new GuidedTexturePackageWorkspace(Path.GetDirectoryName(packageManifest)!)
            .CommitRevisionAsync(packageManifest, result.Revision)
            .GetAwaiter().GetResult();
    }
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine(result.Manifest.PackageAlias);
        Console.WriteLine($"  Textures:              {result.Manifest.TextureCount:N0}");
        Console.WriteLine($"  Materials:             {result.Manifest.MaterialCount:N0}");
        Console.WriteLine($"  Skeletal meshes:       {result.Manifest.SkeletalMeshCount:N0}");
        Console.WriteLine($"  Variable-size meshes:  {result.Manifest.VariableSizeMeshCount:N0}");
        Console.WriteLine($"  Stages:                {result.Manifest.Stages.Count:N0}");
        Console.WriteLine($"  Exact-object validation:{result.Manifest.ExactObjectExternalValidationPassed,7}");
        Console.WriteLine($"  Preview only:          {result.Manifest.PreviewOnly}");
        Console.WriteLine($"  Deployment gate:       {result.Manifest.VariableSizeDeploymentGatePassed}");
        Console.WriteLine($"  Gate manifest:         {result.Manifest.VariableSizeDeploymentGateManifestPath ?? "-"}");
        Console.WriteLine($"  Final UPK:             {result.Manifest.FinalOutputUpkPath}");
        Console.WriteLine($"  Final SHA-256:          {result.Manifest.FinalOutputUpkSha256}");
        Console.WriteLine($"  Bundle:                {result.Manifest.BundleManifestPath}");
        Console.WriteLine($"  Payload:               {result.Manifest.PayloadPath}");
        Console.WriteLine($"  Composition manifest:  {result.CompositionManifestPath}");
        Console.WriteLine($"  Package project:       {(committedPackage is null ? "Not changed" : committedPackage.ManifestPath)}");
        Console.WriteLine($"  Registered revision:   {(committedPackage is null ? "No" : result.Revision.RevisionId)}");
        Console.WriteLine("  Game files modified:    False");
    }
    return result.Manifest.EveryStageVerified &&
           result.Manifest.EverySelectedAssetCatalogued &&
           result.Manifest.OnePhysicalPayloadProduced ? 0 : 1;
}

static int FaceMapProjects(
    string packagePath,
    string? objectPrefix,
    string? packageAlias,
    string? workspacePath,
    string? exportsPath,
    string? reconnaissancePath,
    string? baselinesPath,
    bool json)
{
    if (string.IsNullOrWhiteSpace(objectPrefix))
    {
        throw new ArgumentException("face-map-projects requires --prefix PLAYER_CODE.");
    }
    if (string.IsNullOrWhiteSpace(workspacePath) ||
        string.IsNullOrWhiteSpace(exportsPath) ||
        string.IsNullOrWhiteSpace(reconnaissancePath) ||
        string.IsNullOrWhiteSpace(baselinesPath))
    {
        throw new ArgumentException(
            "face-map-projects requires --workspace DIR --exports DIR --recon DIR --baselines DIR.");
    }

    var package = Path.GetFullPath(packagePath);
    var alias = string.IsNullOrWhiteSpace(packageAlias)
        ? Path.GetFileNameWithoutExtension(package)
        : packageAlias.Trim();
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var workspace = new GuidedTextureModWorkspace(Path.GetFullPath(workspacePath));
    var exportWorkspace = new ExportWorkspaceStore(Path.GetFullPath(exportsPath));
    var service = new FaceTextureMappingService(
        new AssetCatalogReader(protectionProvider: provider),
        new TextureReconnaissanceService(protectionProvider: provider),
        new TextureReconnaissanceWorkspaceStore(Path.GetFullPath(reconnaissancePath)),
        new NativeTextureExportService(protectionProvider: provider),
        exportWorkspace,
        workspace,
        new BaselineWorkspaceStore(Path.GetFullPath(baselinesPath)));
    var progress = json
        ? null
        : new Progress<(int Completed, int Total, string ObjectName)>(value =>
            Console.Error.WriteLine(
                $"[{value.Completed:N0}/{value.Total:N0}] {value.ObjectName}"));
    var result = service.CreateAsync(
            package,
            alias,
            Path.GetFileName(package),
            objectPrefix,
            progress)
        .GetAwaiter().GetResult();
    if (json)
    {
        WriteJson(result.Manifest);
    }
    else
    {
        Console.WriteLine(result.ManifestPath);
        Console.WriteLine($"  Package:    {result.Manifest.PackageAlias}");
        Console.WriteLine($"  Prefix:     {result.Manifest.ObjectPrefix}");
        Console.WriteLine($"  Textures:   {result.Manifest.CandidateCount:N0}");
        Console.WriteLine($"  Backups:    {Path.Combine(result.SessionDirectory, "previous-edits")}");
    }
    return 0;
}

static TextureImportAlphaPolicy ParseTextureImportAlphaPolicy(string? value) =>
    value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "preserve" or "preserve-original" =>
            TextureImportAlphaPolicy.PreserveOriginal,
        "edited" or "use-edited" => TextureImportAlphaPolicy.UseEdited,
        "opaque" or "force-opaque" => TextureImportAlphaPolicy.ForceOpaque,
        _ => throw new ArgumentException(
            $"Unknown alpha policy '{value}'. Use preserve-original, use-edited, or force-opaque."),
    };

static int TexturePixelDiff(string originalPath, string? editedPath, bool json)
{
    if (string.IsNullOrWhiteSpace(editedPath))
    {
        throw new ArgumentException("texture-pixel-diff requires --edited FILE.");
    }

    var originalTexture = DdsBlockTexture.ReadAsync(originalPath).GetAwaiter().GetResult();
    var original = originalTexture.DecodeMip(0, editableView: true);
    var edited = TextureEditableImageService.LoadAsync(editedPath).GetAwaiter().GetResult();
    if (original.Width != edited.Image.Width || original.Height != edited.Image.Height)
    {
        throw new InvalidDataException("Image dimensions do not match.");
    }

    var transformations = new[]
    {
        (Name: "identity", FlipX: false, FlipY: false),
        (Name: "flip-x", FlipX: true, FlipY: false),
        (Name: "flip-y", FlipX: false, FlipY: true),
        (Name: "flip-xy", FlipX: true, FlipY: true),
    };
    var transformMetrics = transformations.Select(transform =>
    {
        long sum = 0;
        long above15 = 0;
        for (var y = 0; y < original.Height; y++)
        {
            var editedY = transform.FlipY ? original.Height - 1 - y : y;
            for (var x = 0; x < original.Width; x++)
            {
                var editedX = transform.FlipX ? original.Width - 1 - x : x;
                var source = (y * original.Width + x) * 4;
                var destination = (editedY * original.Width + editedX) * 4;
                var difference = Math.Max(
                    Math.Abs(original.Pixels[source] - edited.Image.Pixels[destination]),
                    Math.Max(
                        Math.Abs(original.Pixels[source + 1] - edited.Image.Pixels[destination + 1]),
                        Math.Abs(original.Pixels[source + 2] - edited.Image.Pixels[destination + 2])));
                sum += difference;
                if (difference > 15) above15++;
            }
        }

        return new
        {
            transform.Name,
            MeanMaxDifference = (double)sum / (original.Width * original.Height),
            PixelsAbove15 = above15,
        };
    }).ToArray();
    var thresholds = new[] { 0, 1, 2, 3, 4, 7, 15, 31, 63, 127 };
    var counts = thresholds.ToDictionary(value => value, _ => 0L);
    var histogram = new long[256];
    for (var pixel = 0; pixel < original.Width * original.Height; pixel++)
    {
        var offset = pixel * 4;
        var difference = Math.Max(
            Math.Abs(original.Pixels[offset] - edited.Image.Pixels[offset]),
            Math.Max(
                Math.Abs(original.Pixels[offset + 1] - edited.Image.Pixels[offset + 1]),
                Math.Abs(original.Pixels[offset + 2] - edited.Image.Pixels[offset + 2])));
        histogram[difference]++;
        foreach (var threshold in thresholds)
        {
            if (difference > threshold) counts[threshold]++;
        }
    }

    var result = new
    {
        Original = Path.GetFullPath(originalPath),
        Edited = edited.Path,
        original.Width,
        original.Height,
        EditedFormat = edited.SourceFormat,
        TransformMetrics = transformMetrics,
        CountsAboveThreshold = counts,
        NonzeroHistogram = histogram
            .Select((count, difference) => new { Difference = difference, Count = count })
            .Where(item => item.Count > 0)
            .ToArray(),
    };
    if (json)
    {
        WriteJson(result);
    }
    else
    {
        Console.WriteLine($"{original.Width} x {original.Height} | {edited.SourceFormat}");
        foreach (var metric in transformMetrics)
        {
            Console.WriteLine(
                $"  {metric.Name,-8}: mean max diff {metric.MeanMaxDifference,7:N2}, " +
                $">15 {metric.PixelsAbove15,12:N0}");
        }
        foreach (var threshold in thresholds)
        {
            Console.WriteLine($"  RGB max difference > {threshold,3}: {counts[threshold],12:N0}");
        }
    }

    return 0;
}

static int TextureToPng(string inputPath, string? outputPath, bool json)
{
    var input = Path.GetFullPath(inputPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input) + ".png")
        : Path.GetFullPath(outputPath);
    var loaded = TextureEditableImageService.LoadAsync(input).GetAwaiter().GetResult();
    for (var pixel = 0; pixel < loaded.Image.Width * loaded.Image.Height; pixel++)
    {
        loaded.Image.Pixels[pixel * 4 + 3] = 255;
    }

    PngRgbaCodec.WriteAsync(output, loaded.Image).GetAwaiter().GetResult();
    if (json)
    {
        WriteJson(new { Input = input, Output = output, loaded.SourceFormat, loaded.Image.Width, loaded.Image.Height });
    }
    else
    {
        Console.WriteLine(output);
    }

    return 0;
}

static int TextureLayoutConfirm(string nativeManifestPath, string? outputPath, bool json)
{
    var input = Path.GetFullPath(nativeManifestPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(input)!, "layout-confirmation")
        : Path.GetFullPath(outputPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var result = new TextureLayoutConfirmationService(protectionProvider: provider)
        .ConfirmAsync(new TextureLayoutConfirmationRequest(input, output))
        .GetAwaiter()
        .GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Mip payload layout: {result.Manifest.MipPayloadLayoutConfirmed}");
        Console.WriteLine($"  Package rewrite:    {result.Manifest.PackageRewriteConfirmed}");
        Console.WriteLine($"  Mips:               {result.Manifest.MipCount:N0}");
        Console.WriteLine($"  Array:               +0x{result.Manifest.MipArrayRelativeOffset:X}..+0x{result.Manifest.MipArrayEndRelativeOffset:X}");
        Console.WriteLine($"  Manifest:            {result.ManifestPath}");
    }
    return 0;
}

static int TextureBuildPayloads(
    string candidateManifestPath,
    string? outputPath,
    string? crunchPath,
    bool json)
{
    var input = Path.GetFullPath(candidateManifestPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(input)!, "native-payloads")
        : Path.GetFullPath(outputPath);
    var encoderPath = string.IsNullOrWhiteSpace(crunchPath)
        ? CrunchEncoderLocator.FindExecutable() ??
          throw new FileNotFoundException(
              $"{CrunchEncoderLocator.ExecutableFileName} was not found beneath tools/external.")
        : Path.GetFullPath(crunchPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var decoder = new NativeCrunchDecoder();
    var layout = new TextureLayoutConfirmationService(
        protectionProvider: provider,
        crunchDecoder: decoder);
    var service = new TextureReplacementPayloadService(
        layout,
        new UnityCrunchEncoder(encoderPath),
        decoder);
    var result = service.BuildAsync(new TextureReplacementPayloadRequest(input, output))
        .GetAwaiter()
        .GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Writer-ready payloads: {result.Manifest.PayloadSetReadyForWriter}");
        Console.WriteLine($"  Source copied:         {result.Manifest.SourcePayloadsCopied:N0}");
        Console.WriteLine($"  CRN encoded:           {result.Manifest.CrunchPayloadsEncoded:N0}");
        Console.WriteLine($"  UE3/LZ4 encoded:       {result.Manifest.Lz4PayloadsEncoded:N0}");
        Console.WriteLine($"  Stored bytes:          {result.Manifest.SourceStoredBytes:N0} -> {result.Manifest.ReplacementStoredBytes:N0}");
        Console.WriteLine($"  Package modified:      {result.Manifest.PackageWasModified}");
        Console.WriteLine($"  Manifest:              {result.ManifestPath}");
    }
    return 0;
}

static int TextureAssembleExport(string payloadManifestPath, string? outputPath, bool json)
{
    var input = Path.GetFullPath(payloadManifestPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(input)!, "export-assembly")
        : Path.GetFullPath(outputPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var result = new TextureExportAssemblyService(protectionProvider: provider)
        .AssembleAsync(new TextureExportAssemblyRequest(input, output))
        .GetAwaiter()
        .GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  No-op identical:       {result.Manifest.NoOpExportByteIdentical}");
        Console.WriteLine($"  Replacement verified: {result.Manifest.ReplacementBlobReadyForRewritePlan}");
        Console.WriteLine($"  Export bytes:          {result.Manifest.SourceSerialSize:N0} -> {result.Manifest.ReplacementExportBytes:N0}");
        Console.WriteLine($"  Size delta:            {result.Manifest.ReplacementSizeDelta:+#,0;-#,0;0}");
        Console.WriteLine($"  Package modified:      {result.Manifest.PackageWasModified}");
        Console.WriteLine($"  Manifest:              {result.ManifestPath}");
    }
    return result.Manifest.ReplacementBlobReadyForRewritePlan ? 0 : 1;
}

static int TexturePlanRewrite(string assemblyManifestPath, string? outputPath, bool json)
{
    var input = Path.GetFullPath(assemblyManifestPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(input)!, "package-rewrite-plan")
        : Path.GetFullPath(outputPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var result = new PackageRewritePlanService(protectionProvider: provider)
        .PlanAsync(new PackageRewritePlanRequest(input, output))
        .GetAwaiter()
        .GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Strategy:              {result.Manifest.SelectedStrategy}");
        Console.WriteLine($"  Planned serial range:  0x{result.Manifest.PlannedSerialOffset:X}+0x{result.Manifest.PlannedSerialSize:X}");
        Console.WriteLine($"  Logical growth:        {result.Manifest.PlannedLogicalGrowthBytes:N0} bytes");
        Console.WriteLine($"  Existing exports moved:{result.Manifest.ExistingExportsRelocated,12:N0}");
        Console.WriteLine($"  Logical plan ready:    {result.Manifest.PlanReadyForLogicalImageWriter}");
        Console.WriteLine($"  Physical writer ready: {result.Manifest.PackageFileWriterReady}");
        Console.WriteLine($"  Package modified:      {result.Manifest.PackageWasModified}");
        Console.WriteLine($"  Manifest:              {result.ManifestPath}");
    }
    return result.Manifest.PlanReadyForLogicalImageWriter ? 0 : 1;
}

static int TextureBuildLogicalImage(string rewritePlanManifestPath, string? outputPath, bool json)
{
    var input = Path.GetFullPath(rewritePlanManifestPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(input)!, "logical-package-image")
        : Path.GetFullPath(outputPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var result = new LogicalPackageImageService(protectionProvider: provider)
        .BuildAsync(new LogicalPackageImageRequest(input, output))
        .GetAwaiter()
        .GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Logical image:         {result.Manifest.LogicalImageBytes:N0} bytes");
        Console.WriteLine($"  Image SHA-256:         {result.Manifest.LogicalImageSha256}");
        Console.WriteLine($"  Unplanned bytes exact: {result.Manifest.AllUnplannedSourceBytesIdentical}");
        Console.WriteLine($"  Table patches:         {result.Manifest.TablePatches.Count:N0}");
        Console.WriteLine($"  Bulk rebases:          {result.Manifest.BulkOffsetRebases.Count:N0}");
        Console.WriteLine($"  Physical writer ready: {result.Manifest.PhysicalChunkWriterReady}");
        Console.WriteLine($"  Package modified:      {result.Manifest.PackageWasModified}");
        Console.WriteLine($"  Manifest:              {result.ManifestPath}");
        Console.WriteLine($"  Image:                 {result.LogicalImagePath}");
    }
    return result.Manifest.LogicalImageReadyForPhysicalWriter ? 0 : 1;
}

static int TexturePlanPhysical(string logicalImageManifestPath, string? outputPath, bool json)
{
    var input = Path.GetFullPath(logicalImageManifestPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(input)!, "physical-package-plan")
        : Path.GetFullPath(outputPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var result = new PhysicalPackagePlanService(protectionProvider: provider)
        .PlanAsync(new PhysicalPackagePlanRequest(input, output))
        .GetAwaiter()
        .GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Chunks:                {result.Manifest.CompressedChunkCount:N0}");
        Console.WriteLine($"  Protected / raw:       {result.Manifest.ProtectedChunkCount:N0} / {result.Manifest.RawChunkCount:N0}");
        Console.WriteLine($"  Compression blocks:    {result.Manifest.TotalBlockCount:N0}");
        Console.WriteLine($"  Physical copy regions: {result.Manifest.CopyRegions.Count:N0}");
        Console.WriteLine($"  Replacement impacts:   {result.Manifest.ReplacementAffectedChunkCount:N0} chunk(s)");
        Console.WriteLine($"  No-op writer ready:    {result.Manifest.PlanReadyForNoOpReconstruction}");
        Console.WriteLine($"  Replacement ready:     {result.Manifest.ReplacementPhysicalWriterReady}");
        Console.WriteLine($"  Package modified:      {result.Manifest.PackageWasModified}");
        Console.WriteLine($"  Manifest:              {result.ManifestPath}");
    }
    return result.Manifest.PlanReadyForNoOpReconstruction ? 0 : 1;
}

static int TextureReconstructNoOpUpk(string physicalPlanManifestPath, string? outputPath, bool json)
{
    var input = Path.GetFullPath(physicalPlanManifestPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(input)!, "noop-upk-reconstruction")
        : Path.GetFullPath(outputPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var result = new NoOpUpkReconstructionService(protectionProvider: provider)
        .ReconstructAsync(new NoOpUpkReconstructionRequest(input, output))
        .GetAwaiter()
        .GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Physical bytes:        {result.Manifest.OutputPhysicalPackageBytes:N0}");
        Console.WriteLine($"  Physical byte-exact:   {result.Manifest.PhysicalFileByteIdentical}");
        Console.WriteLine($"  Logical byte-exact:    {result.Manifest.LogicalImageByteIdentical}");
        Console.WriteLine($"  Catalogue equivalent: {result.Manifest.AssetCatalogueEquivalent}");
        Console.WriteLine($"  Structural validation:{result.Manifest.StructuralValidationPassed,12}");
        Console.WriteLine($"  Proof passed:          {result.Manifest.NoOpReconstructionProofPassed}");
        Console.WriteLine($"  Contains replacement:  {result.Manifest.OutputContainsReplacement}");
        Console.WriteLine($"  Package modified:      {result.Manifest.PackageWasModified}");
        Console.WriteLine($"  Manifest:              {result.ManifestPath}");
        Console.WriteLine($"  Reconstructed UPK:     {result.OutputUpkPath}");
    }
    return result.Manifest.NoOpReconstructionProofPassed ? 0 : 1;
}

static int TextureBuildReplacementUpk(string noOpManifestPath, string? outputPath, bool json)
{
    var input = Path.GetFullPath(noOpManifestPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(input)!, "replacement-staging-upk")
        : Path.GetFullPath(outputPath);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var result = new ReplacementUpkBuildService(protectionProvider: provider)
        .BuildAsync(new ReplacementUpkBuildRequest(input, output))
        .GetAwaiter()
        .GetResult();
    if (json) WriteJson(result.Manifest);
    else
    {
        Console.WriteLine($"{result.Manifest.PackageAlias}.{result.Manifest.ObjectName}");
        Console.WriteLine($"  Physical bytes:        {result.Manifest.OutputPhysicalPackageBytes:N0}");
        Console.WriteLine($"  Chunks rebuilt/copied: {result.Manifest.RebuiltChunkCount:N0} / {result.Manifest.ByteIdenticalChunkCount:N0}");
        Console.WriteLine($"  Protected rebuilt:     {result.Manifest.ProtectedRebuiltChunkCount:N0}");
        Console.WriteLine($"  Logical image exact:   {result.Manifest.ExpectedLogicalImageMatched}");
        Console.WriteLine($"  Target retargeted:     {result.Manifest.TargetExportRetargeted}");
        Console.WriteLine($"  Unrelated exports exact:{result.Manifest.UnrelatedExportsEquivalent,11}");
        Console.WriteLine($"  Build passed:          {result.Manifest.ReplacementPackageBuildPassed}");
        Console.WriteLine($"  Deployment ready:      {result.Manifest.DeploymentReady}");
        Console.WriteLine($"  Source package changed:{result.Manifest.PackageWasModified,11}");
        Console.WriteLine($"  Manifest:              {result.ManifestPath}");
        Console.WriteLine($"  Staging UPK:           {result.OutputUpkPath}");
    }
    return result.Manifest.ReplacementPackageBuildPassed ? 0 : 1;
}

static int ModBuildBundle(
    string replacementManifestPath,
    string? outputPath,
    string? backupRootPath,
    string? displayName,
    bool json)
{
    var input = Path.GetFullPath(replacementManifestPath);
    var output = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(Path.GetDirectoryName(input)!, "mod-bundle")
        : Path.GetFullPath(outputPath);
    var projectRoot = UmodelLocator.FindProjectRoot() ?? Environment.CurrentDirectory;
    var backupRoot = string.IsNullOrWhiteSpace(backupRootPath)
        ? Path.Combine(projectRoot, "backups", "packages")
        : Path.GetFullPath(backupRootPath);
    var umodelPath = UmodelLocator.FindExecutable() ??
                     throw new FileNotFoundException(
                         "The Lost Ark UModel backend was not found beneath tools/external.");
    var replacement = ReplacementUpkBuildService.LoadManifestAsync(input)
        .GetAwaiter()
        .GetResult();
    var name = string.IsNullOrWhiteSpace(displayName)
        ? $"{replacement.ObjectName} texture mod"
        : displayName;
    var result = new ArkiveModBundleService(
            new UmodelModPackageExternalValidator(new UmodelBridge(umodelPath)))
        .BuildAsync(new ArkiveModBundleRequest(input, output, backupRoot, name))
        .GetAwaiter()
        .GetResult();
    if (json) WriteJson(result.Readiness);
    else
    {
        Console.WriteLine(result.Bundle.DisplayName);
        Console.WriteLine($"  Mod ID:                {result.Bundle.ModId}");
        Console.WriteLine($"  Target package:        {result.Bundle.SourcePackageRelativePath}");
        Console.WriteLine($"  Payload bytes:         {result.Bundle.PayloadBytes:N0}");
        Console.WriteLine($"  Exact client required: {result.Bundle.ExactClientMatchRequired}");
        Console.WriteLine($"  UModel native load:    {result.Readiness.ExternalNativeLoadPassed}");
        Console.WriteLine($"  Backup bytes required: {result.Readiness.RequiredBackupBytes:N0}");
        Console.WriteLine($"  Controlled-install audit: {result.Readiness.ReadyForControlledInstall}");
        Console.WriteLine($"  Installation enabled:  {result.Bundle.InstallationEnabled}");
        Console.WriteLine($"  Game files modified:   {result.Bundle.GameFilesModified}");
        Console.WriteLine($"  Bundle:                 {result.BundleManifestPath}");
        Console.WriteLine($"  Readiness:              {result.ReadinessManifestPath}");
        Console.WriteLine($"  Payload:                {result.PayloadPath}");
    }
    return result.Readiness.ReadyForControlledInstall ? 0 : 1;
}

static int ModDeployment(
    string bundleManifestPath,
    string action,
    string? backupRootPath,
    string? stateRootPath,
    bool confirmed,
    bool json)
{
    if (action is "install" or "restore" && !confirmed)
    {
        throw new ArgumentException(
            $"mod-{action} changes an installed game package. Review mod-status first, then pass --confirm.");
    }

    var projectRoot = UmodelLocator.FindProjectRoot() ?? Environment.CurrentDirectory;
    var request = new ArkiveModDeploymentRequest(
        Path.GetFullPath(bundleManifestPath),
        string.IsNullOrWhiteSpace(backupRootPath)
            ? Path.Combine(projectRoot, "backups", "packages")
            : Path.GetFullPath(backupRootPath),
        string.IsNullOrWhiteSpace(stateRootPath)
            ? Path.Combine(projectRoot, "backups", "installations")
            : Path.GetFullPath(stateRootPath));
    var service = new ArkiveModDeploymentService();

    if (action == "status")
    {
        var inspection = service.InspectAsync(request).GetAwaiter().GetResult();
        if (json) WriteJson(inspection);
        else PrintDeploymentInspection(inspection);
        return inspection.State == ModInstallationState.Conflict ||
               inspection.State == ModInstallationState.MissingTarget
            ? 1
            : 0;
    }

    var result = action == "install"
        ? service.InstallAsync(request).GetAwaiter().GetResult()
        : service.RestoreAsync(request).GetAwaiter().GetResult();
    if (json) WriteJson(result.Receipt);
    else
    {
        Console.WriteLine(action == "install" ? "Mod installed and verified." : "Original package restored and verified.");
        PrintDeploymentInspection(result.Inspection);
        Console.WriteLine($"  Target changed:        {result.ChangedTarget}");
        Console.WriteLine($"  Transaction recovered: {result.RecoveredInterruptedOperation}");
        Console.WriteLine($"  Receipt:               {result.Inspection.ReceiptPath}");
    }
    return 0;
}

static void PrintDeploymentInspection(ModDeploymentInspection inspection)
{
    Console.WriteLine(inspection.Bundle.DisplayName);
    Console.WriteLine($"  State:                 {inspection.State}");
    Console.WriteLine($"  Summary:               {inspection.Summary}");
    Console.WriteLine($"  Target:                {inspection.TargetPath}");
    Console.WriteLine($"  Current SHA-256:       {inspection.CurrentTargetSha256 ?? "missing"}");
    Console.WriteLine($"  Payload verified:      {inspection.PayloadVerified}");
    Console.WriteLine($"  Backup:                {inspection.BackupPath}");
    Console.WriteLine($"  Backup verified:       {inspection.BackupVerified}");
    Console.WriteLine($"  Can install:           {inspection.CanInstall}");
    Console.WriteLine($"  Can restore:           {inspection.CanRestore}");
}

static int ModSetDeployment(
    string setManifestPath,
    string action,
    string? backupRootPath,
    string? stateRootPath,
    bool confirmed,
    bool json)
{
    if (action != "inspect" && !confirmed)
        throw new ArgumentException($"mod-set-{action} requires --confirm.");
    var setPath = Path.GetFullPath(setManifestPath);
    var projectRoot = UmodelLocator.FindProjectRoot() ?? Environment.CurrentDirectory;
    var backupRoot = string.IsNullOrWhiteSpace(backupRootPath)
        ? Path.Combine(projectRoot, "backups", "packages") : Path.GetFullPath(backupRootPath);
    var stateRoot = string.IsNullOrWhiteSpace(stateRootPath)
        ? Path.Combine(projectRoot, "backups", "installations") : Path.GetFullPath(stateRootPath);
    var workspace = new ArkiveModSetWorkspace(Path.GetDirectoryName(setPath)!);
    var service = new ArkiveModSetService(workspace,
        new GuidedTexturePackageWorkspace(Path.GetDirectoryName(setPath)!),
        new ArkiveModDeploymentService());
    var request = new ModSetDeploymentRequest(setPath, backupRoot, stateRoot);
    if (action == "inspect")
    {
        var inspection = service.InspectAsync(request).GetAwaiter().GetResult();
        if (json) WriteJson(inspection);
        else PrintModSetInspection(inspection);
        return inspection.Conflicts.Any(conflict => conflict.Severity == ModSetConflictSeverity.Blocking) ? 1 : 0;
    }
    var result = action == "install"
        ? service.InstallAsync(request).GetAwaiter().GetResult()
        : service.RestoreAsync(request).GetAwaiter().GetResult();
    if (json) WriteJson(result.Receipt);
    else
    {
        Console.WriteLine(action == "install" ? "Mod Set installed and verified." : "Mod Set restored and verified.");
        PrintModSetInspection(result.Inspection);
        Console.WriteLine($"  Packages changed:      {result.ChangedPackageCount:N0}");
        Console.WriteLine($"  Transaction receipt:   {result.Receipt.TransactionId}");
    }
    return 0;
}

static void PrintModSetInspection(ModSetInspection inspection)
{
    Console.WriteLine(inspection.Set.Manifest.DisplayName);
    Console.WriteLine($"  State:                 {inspection.State}");
    Console.WriteLine($"  Summary:               {inspection.Summary}");
    Console.WriteLine($"  Package payloads:      {inspection.Members.Count:N0}");
    Console.WriteLine($"  Blocking conflicts:    {inspection.Conflicts.Count(conflict => conflict.Severity == ModSetConflictSeverity.Blocking):N0}");
    Console.WriteLine($"  Can install:           {inspection.CanInstall}");
    Console.WriteLine($"  Can restore:           {inspection.CanRestore}");
    foreach (var conflict in inspection.Conflicts)
        Console.WriteLine($"  [{conflict.Severity}] {conflict.Code}: {conflict.Message}");
}

static int ArkiveInspect(string arkivePath, bool json)
{
    var read = ArkiveModFileService.ReadAsync(arkivePath).GetAwaiter().GetResult();
    var manifest = read.Manifest;
    if (json)
    {
        WriteJson(new { read.Path, read.Bytes, read.Sha256, Manifest = manifest });
        return 0;
    }
    Console.WriteLine($"{manifest.Name} {manifest.Version}");
    Console.WriteLine($"  Valid .arkive:         yes (every file size and SHA-256 verified)");
    Console.WriteLine($"  Mod ID:                {manifest.ModId}");
    Console.WriteLine($"  Author:                {manifest.Author ?? "-"}");
    Console.WriteLine($"  Format:                {manifest.Format} v{manifest.FormatVersion}");
    Console.WriteLine($"  Game profile:          {manifest.GameProfile}");
    Console.WriteLine($"  Created / updated:     {manifest.CreatedAt:u} / {manifest.UpdatedAt:u}");
    Console.WriteLine($"  File:                  {read.Bytes:N0} bytes, SHA-256 {read.Sha256}");
    Console.WriteLine($"  Target packages:       {string.Join(", ", manifest.PhysicalPackageNames)}");
    if (!string.IsNullOrWhiteSpace(manifest.Description))
        Console.WriteLine($"  Description:           {manifest.Description.ReplaceLineEndings(" ")}");
    Console.WriteLine($"  Assets ({manifest.Assets.Count:N0}):");
    foreach (var asset in manifest.Assets)
        Console.WriteLine($"    {asset.Kind,-24} {asset.Target.PackageAlias}.{asset.Target.ObjectName} " +
                          $"({asset.Target.PhysicalPackageName}) — {asset.DisplayName}" +
                          (asset.Files.Count == 0 ? string.Empty : $" [{asset.Files.Sum(file => file.Bytes):N0} bytes]"));
    return 0;
}

static int ArkiveList(string projectRoot, bool json)
{
    var environment = CreateModEnvironment(projectRoot);
    var scan = environment.Authoring.ScanAsync().GetAwaiter().GetResult();
    var snapshot = ArkiveModEditSnapshot.LoadAsync(environment.Context.Edits).GetAwaiter().GetResult();
    var active = new Dictionary<string, GuidedTexturePackageRevision?>(StringComparer.Ordinal);
    var projects = environment.Context.Packages.LoadAsync().GetAwaiter().GetResult();
    foreach (var physical in projects.Select(package => Path.GetFileName(package.Manifest.SourcePackagePath))
                 .Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (ArkiveModApplyService.FindPackageProject(physical, scan.Library.GamePackagesDirectory, projects) is not { } project)
            continue;
        active[ArkiveModFileService.PhysicalPackageKey(physical)] =
            environment.Context.FindActiveDeploymentAsync(project).GetAwaiter().GetResult()?.Revision;
    }
    var statuses = ArkiveModApplyService.DescribeAsync(
        environment.Context.Library, scan.Library, snapshot, active).GetAwaiter().GetResult();
    if (json)
    {
        WriteJson(statuses.Select(status => new
        {
            status.Mod.ModId, status.Mod.Manifest.Name, status.Mod.Manifest.Version, status.Mod.Enabled,
            status.Mod.Priority, status.Mod.FileName, status.State, status.ConflictSummary, status.ModifiedLocally,
            Assets = status.Assets.Select(asset => new
            {
                asset.Asset.Kind, asset.Asset.Target.PackageAlias, asset.Asset.Target.ObjectName,
                Outcome = asset.Outcome.ToString(), Setup = asset.Setup.ToString(), asset.InGame, asset.ModifiedLocally,
            }),
        }));
        return 0;
    }
    Console.WriteLine($"Mods folder:  {environment.Context.Library.ModsDirectory}");
    Console.WriteLine($"Game folder:  {scan.Library.GamePackagesDirectory ?? "unknown"}");
    foreach (var issue in scan.Issues) Console.WriteLine($"  [ignored] {issue.Path}: {issue.Message}");
    foreach (var status in statuses.OrderByDescending(item => item.Mod.Enabled)
                 .ThenByDescending(item => item.Mod.Priority))
        Console.WriteLine(
            $"  [{(status.Mod.Enabled ? 'x' : ' ')}] {status.Mod.Priority,6:N0}  {status.Mod.Manifest.Name} " +
            $"{status.Mod.Manifest.Version} — {status.State}; conflicts: {status.ConflictSummary}" +
            (status.ModifiedLocally ? "; unsaved local changes" : string.Empty) +
            $" ({status.Mod.ModId[..8]}, {status.Mod.FileName})");
    if (statuses.Count == 0) Console.WriteLine("  No mods. Use arkive-import or arkive-migrate.");
    return 0;
}

static int ArkiveImport(string arkivePath, string? projectRoot, bool enable, int? priority, bool replace, bool json)
{
    var environment = CreateModEnvironment(projectRoot ?? ArkiveRuntimePaths.ResolveDataRoot());
    var library = environment.Context.Library;
    var result = environment.Authoring.ImportAsync(arkivePath, replace).GetAwaiter().GetResult();
    if (enable) library.SetEnabledAsync(result.Entry.ModId, true).GetAwaiter().GetResult();
    if (priority is not null) library.SetPriorityAsync(result.Entry.ModId, priority.Value).GetAwaiter().GetResult();
    ArkiveModSelectionService.SynchronizeAsync(library, environment.Context.Edits).GetAwaiter().GetResult();
    var entry = library.LoadAsync().GetAwaiter().GetResult().Find(result.Entry.ModId)!;
    if (json) WriteJson(new { entry.ModId, entry.Manifest.Name, entry.FileName, entry.Enabled, entry.Priority, result.UpdatedExistingMod, ReleasedEdits = result.Released.Count });
    else
    {
        Console.WriteLine($"{(result.UpdatedExistingMod ? "Updated" : "Imported")} {entry.Manifest.Name} {entry.Manifest.Version}");
        Console.WriteLine($"  File:                  {library.GetModFilePath(entry)}");
        Console.WriteLine($"  Enabled / priority:    {entry.Enabled} / {entry.Priority:N0}");
        if (result.Released.Count > 0)
            Console.WriteLine($"  Changed assets:        {result.Released.Count:N0} will be set up again on apply");
        Console.WriteLine("  Run arkive-plan, then arkive-apply --confirm to put enabled mods in game.");
    }
    return 0;
}

static int ArkiveSet(string projectRoot, string? modReference, bool enable, bool disable, int? priority, bool json)
{
    if (enable && disable) throw new ArgumentException("Pass either --enable or --disable, not both.");
    if (!enable && !disable && priority is null)
        throw new ArgumentException("arkive-set requires --enable, --disable, or --priority N.");
    var environment = CreateModEnvironment(projectRoot);
    var library = environment.Context.Library;
    var mod = ResolveModReference(library.LoadAsync().GetAwaiter().GetResult(), modReference);
    if (enable || disable) library.SetEnabledAsync(mod.ModId, enable).GetAwaiter().GetResult();
    if (priority is not null) library.SetPriorityAsync(mod.ModId, priority.Value).GetAwaiter().GetResult();
    var changes = ArkiveModSelectionService.SynchronizeAsync(library, environment.Context.Edits).GetAwaiter().GetResult();
    var updated = library.LoadAsync().GetAwaiter().GetResult().Find(mod.ModId)!;
    if (json) WriteJson(new { updated.ModId, updated.Manifest.Name, updated.Enabled, updated.Priority, SelectionChanges = changes });
    else
    {
        Console.WriteLine($"{updated.Manifest.Name}: enabled={updated.Enabled}, priority={updated.Priority:N0}");
        foreach (var change in changes)
            Console.WriteLine($"  {(change.Included ? "Checked" : "Unchecked")} {change.Kind} {change.ObjectName} ({change.ModName})");
        Console.WriteLine("  Run arkive-plan, then arkive-apply --confirm to update the game.");
    }
    return 0;
}

static int ArkiveConflicts(string projectRoot, bool json)
{
    var environment = CreateModEnvironment(projectRoot);
    var library = environment.Context.Library.LoadAsync().GetAwaiter().GetResult();
    var resolution = ArkiveModConflictResolver.Resolve(library.Mods);
    var shared = resolution.Targets.Where(target => target.Contenders.Count > 1).ToArray();
    if (json)
    {
        WriteJson(shared.Select(target => new
        {
            target.PackageAlias, target.PhysicalPackageName, target.ClassName, target.ObjectName,
            Winner = target.Winner?.ModName, Tied = target.Tied.Select(item => item.ModName),
            Contenders = target.Contenders.Select(item => new { item.ModName, item.Priority, item.Enabled }),
        }));
        return resolution.BlockingTies.Count > 0 ? 1 : 0;
    }
    if (shared.Length == 0) Console.WriteLine("No two mods replace the same asset.");
    foreach (var target in shared)
    {
        Console.WriteLine($"{target.ObjectName} ({target.PackageAlias}, {target.PhysicalPackageName})");
        foreach (var contender in target.Contenders)
        {
            var outcome = resolution.OutcomeFor(contender.ModId, contender.Asset.AssetId) switch
            {
                ArkiveModAssetOutcome.Winning => "wins",
                ArkiveModAssetOutcome.Overridden => "overridden",
                ArkiveModAssetOutcome.Tied => "TIED",
                _ => "disabled",
            };
            Console.WriteLine($"  {contender.Priority,6:N0}  {contender.ModName,-40} {outcome}");
        }
    }
    return resolution.BlockingTies.Count > 0 ? 1 : 0;
}

static int ArkiveMigrate(string projectRoot, string? author, bool json)
{
    var environment = CreateModEnvironment(projectRoot);
    environment.Authoring.ScanAsync().GetAwaiter().GetResult();
    var result = new ArkiveModMigrationService(environment.Context)
        .MigratePackageProjectsAsync(author, json ? null : new InlineProgress<string>(Console.Error.WriteLine))
        .GetAwaiter().GetResult();
    if (json)
    {
        WriteJson(result);
        return 0;
    }
    foreach (var migrated in result.Migrated)
    {
        Console.WriteLine($"Converted {migrated.PackageName}: {migrated.AssetCount:N0} asset(s), " +
                          $"enabled={migrated.Enabled} -> {migrated.ModFilePath}");
        foreach (var warning in migrated.Warnings) Console.WriteLine($"  warning: {warning}");
    }
    foreach (var skip in result.Skipped)
        Console.WriteLine($"Skipped {skip.PackageName}: {skip.Reason}");
    foreach (var change in result.SelectionChanges)
        Console.WriteLine($"  {(change.Included ? "Checked" : "Unchecked")} {change.Kind} {change.ObjectName} ({change.ModName})");
    Console.WriteLine($"{result.Migrated.Count:N0} package project(s) converted; no game file was changed.");
    return 0;
}

/// <summary>
/// Makes one mod that tints every particle system whose name contains one of the --match words, in each --package
/// (a game package; its clean copy is used when a mod is already installed in it). The mod is added to the library
/// disabled, like a mod saved in Edit mods.
/// </summary>
static int ArkiveParticleTint(string projectRoot, IReadOnlyList<string> packages, string match, string color, string name, bool json)
{
    if (packages.Count == 0) throw new ArgumentException("arkive-particle-tint requires at least one --package FILE.upk.");
    var channels = color.Split(',').Select(value => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    if (channels.Length != 3) throw new ArgumentException("--color expects R,G,B, for example 1,0.05,0.03.");
    var recipe = new Arkive.Core.Particles.ParticleEffectRecipe(new Arkive.Core.Particles.ParticleEffectTint(channels[0], channels[1], channels[2]));
    var words = match.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var environment = CreateModEnvironment(projectRoot);
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var captured = new List<ArkiveModCapturedAsset>();
    var created = new List<Arkive.Core.Particles.ParticleEffectEditEntry>();
    try
    {
        foreach (var gamePackage in packages.Select(Path.GetFullPath))
        {
            var clean = environment.Apply.ResolveCleanPackageAsync(gamePackage).GetAwaiter().GetResult();
            var catalog = new AssetCatalogReader(protectionProvider: provider).Read(clean);
            var alias = Arkive.Core.Discovery.LostArkPackageName.GetDisplayName(gamePackage);
            var hash = ArkiveModEditSnapshot.HashFileCachedAsync(clean).GetAwaiter().GetResult();
            foreach (var system in catalog.Assets.Where(asset => asset.ClassName.Equals("particlesystem", StringComparison.OrdinalIgnoreCase) &&
                                                                 words.Any(word => asset.ObjectName.Contains(word, StringComparison.OrdinalIgnoreCase))))
            {
                var report = Arkive.Core.Particles.ParticleEffects.Inspect(clean, hash, system.ObjectName, system.ObjectPath, provider);
                if (report.Tables.Count == 0) continue;
                var edit = environment.Context.Edits.ParticleEdits.CreateAsync(report, alias, system.ObjectName, recipe).GetAwaiter().GetResult();
                created.Add(edit);
                if (edit.Document.Patches.Count == 0) continue;
                var local = new ArkiveModLocalEdit(UnifiedPackageAssetKind.ParticleSystem, edit.Document.EditId, edit.DocumentPath,
                    system.ObjectName, Path.GetFullPath(clean), system.ObjectName, false, edit);
                captured.Add(ArkiveModAuthoringService.CaptureAsync(local).GetAwaiter().GetResult());
            }
        }
        if (captured.Count == 0) throw new InvalidOperationException("No particle system with colour values matched.");
        var mod = environment.Authoring.CreateModAsync(new ArkiveModMetadata(name, "1.0.0", "Arkive",
                $"Tints {captured.Count:N0} particle effect(s) matching {match} to {color}."),
            captured, ArkiveModOrigin.Created, enabled: false).GetAwaiter().GetResult();
        var patches = captured.Sum(asset => asset.Descriptor.Particle!.Patches.Count);
        if (json) WriteJson(new { mod.ModId, mod.FileName, Effects = captured.Count, Patches = patches });
        else Console.WriteLine($"Created {mod.FileName}: {captured.Count:N0} effect(s), {patches:N0} colour table(s). Enable it with arkive-set --mod {mod.ModId[..8]} --enable.");
        return 0;
    }
    finally
    {
        // The mod keeps its own copy of the patches; the library sets up working copies again when it is enabled.
        foreach (var edit in created)
            environment.Context.Edits.ParticleEdits.DeleteAsync(edit).GetAwaiter().GetResult();
    }
}

static int SkillActions(string archivePath, string? className, int? skillId, bool json)
{
    using var stream = File.OpenRead(archivePath);
    var entries = Arkive.Core.Skills.LostArkDataArchive.ReadEntries(stream)
        .Where(entry => entry.Path.Contains("/XmlData/Action/", StringComparison.OrdinalIgnoreCase) &&
                        (className is null || Path.GetFileNameWithoutExtension(entry.Path).Equals(className, StringComparison.OrdinalIgnoreCase)))
        .ToArray();
    var found = new List<object>();
    foreach (var entry in entries)
    {
        var actions = Arkive.Core.Skills.SkillActionReader.Read(Arkive.Core.Skills.LostArkDataArchive.Read(stream, entry));
        foreach (var action in actions.Where(action => skillId is null || action.Id == skillId))
        {
            if (json) { found.Add(new { File = Path.GetFileName(entry.Path), action }); continue; }
            Console.WriteLine($"{Path.GetFileName(entry.Path)} {action.Id} {action.Name}: {action.Stages.Count} stage(s), {action.ParticleSystems.Count()} effect(s)");
            if (skillId is null) continue;
            foreach (var stage in action.Stages)
            {
                Console.WriteLine($"  stage '{stage.Name}' {stage.Duration:0.###}s x{stage.Rate:0.##}");
                foreach (var notify in stage.Notifies)
                    Console.WriteLine($"    {notify.Start,6:0.###} {notify.Duration,6:0.###} {notify.Kind} {notify.Name} {notify.Asset} {(notify.Socket is null ? "" : "@" + notify.Socket)} " +
                                      string.Join(" ", notify.Parameters.Select(p => p.IsVector ? $"{p.Name}=({p.X:0.##},{p.Y:0.##},{p.Z:0.##})" : $"{p.Name}={p.Value:0.###}")));
            }
        }
    }
    if (json) WriteJson(found);
    return 0;
}

static int ArkivePlan(string projectRoot, bool json)
{
    var environment = CreateModEnvironment(projectRoot);
    environment.Authoring.ScanAsync().GetAwaiter().GetResult();
    var plan = environment.Apply.PlanAsync().GetAwaiter().GetResult();
    if (json) WriteJson(PlanJson(plan));
    else PrintModPlan(plan, environment.Limitations);
    return plan.CanApply ? 0 : 1;
}

static int ArkiveApply(string projectRoot, bool confirmed, bool json)
{
    if (!confirmed)
        throw new ArgumentException(
            "arkive-apply restores, rebuilds, and installs game packages. Review arkive-plan first, then pass --confirm.");
    var environment = CreateModEnvironment(projectRoot);
    environment.Authoring.ScanAsync().GetAwaiter().GetResult();
    var plan = environment.Apply.PlanAsync().GetAwaiter().GetResult();
    if (!json) PrintModPlan(plan, environment.Limitations);
    if (!plan.CanApply) return 1;
    if (!plan.HasChanges) return 0;
    var result = environment.Apply.ApplyAsync(
            plan.Fingerprint,
            json ? null : new InlineProgress<ArkiveModApplyProgress>(update => Console.Error.WriteLine(
                $"[{update.Step:N0}/{update.TotalSteps:N0}] {update.Package}: {update.Message}")))
        .GetAwaiter().GetResult();
    if (json) WriteJson(new { result.Succeeded, result.SetUpAssetCount, result.RebuiltPackageCount, result.InstalledPackageCount, result.RestoredPackageCount, result.Packages });
    else
    {
        foreach (var package in result.Packages)
            Console.WriteLine($"  {(package.Succeeded ? "ok  " : "FAIL")} {package.PhysicalPackageName}: {package.Message}");
        Console.WriteLine($"Set up {result.SetUpAssetCount:N0} asset(s); rebuilt {result.RebuiltPackageCount:N0}, " +
                          $"installed {result.InstalledPackageCount:N0}, restored {result.RestoredPackageCount:N0} package(s) " +
                          $"in {result.Duration.TotalSeconds:N1}s.");
    }
    return result.Succeeded ? 0 : 1;
}

static int ArkiveUpdate(string projectRoot, string? modReference, bool json)
{
    var environment = CreateModEnvironment(projectRoot);
    var mod = ResolveModReference(environment.Context.Library.LoadAsync().GetAwaiter().GetResult(), modReference);
    var outcomes = environment.Apply.UpdateModForCurrentClientAsync(mod.ModId).GetAwaiter().GetResult();
    if (json) WriteJson(outcomes);
    else
    {
        Console.WriteLine($"{mod.Manifest.Name}: updated for the installed client");
        foreach (var outcome in outcomes)
            Console.WriteLine($"  {(outcome.Succeeded ? "ok  " : "FAIL")} {outcome.DisplayName}: {outcome.Message}");
        Console.WriteLine("  Run arkive-plan, then arkive-apply --confirm to rebuild and install it.");
    }
    return outcomes.All(outcome => outcome.Succeeded) ? 0 : 1;
}

static void PrintModPlan(ArkiveModApplyPlan plan, IReadOnlyList<string> limitations)
{
    foreach (var limitation in limitations) Console.WriteLine($"note: {limitation}");
    foreach (var package in plan.Packages)
    {
        Console.WriteLine($"{package.PackageAlias} ({package.PhysicalPackageName}): {package.Describe}");
        Console.WriteLine($"  {package.Reason}");
        foreach (var contributor in package.Contributors)
            Console.WriteLine($"    {contributor.ObjectName} <- {(contributor.IsDraft ? "local draft" : $"{contributor.ModName} (priority {contributor.Priority:N0})")}");
        foreach (var setup in package.Setups)
            Console.WriteLine($"    set up: {setup.Asset.DisplayName} from {setup.ModName} — {setup.Reason}");
    }
    foreach (var warning in plan.Warnings) Console.WriteLine($"warning: {warning}");
    foreach (var problem in plan.BlockingProblems) Console.WriteLine($"BLOCKED: {problem}");
    Console.WriteLine(plan.Summary);
}

static object PlanJson(ArkiveModApplyPlan plan) => new
{
    plan.CanApply, plan.HasChanges, plan.Summary, plan.BlockingProblems, plan.Warnings, plan.Fingerprint,
    Packages = plan.Packages.Select(package => new
    {
        package.PackageAlias, package.PhysicalPackageName, Action = package.Action.ToString(), package.RestoreFirst,
        package.Reason, package.Contributors, Setups = package.Setups.Select(setup => new { setup.ModName, setup.Asset.DisplayName, setup.Reason }),
    }),
};

static ArkiveModLibraryEntry ResolveModReference(ArkiveModLibraryManifest library, string? reference)
{
    if (string.IsNullOrWhiteSpace(reference)) throw new ArgumentException("--mod requires a mod ID or name.");
    var value = reference.Trim();
    var byId = library.Mods.Where(mod => mod.ModId.StartsWith(value, StringComparison.OrdinalIgnoreCase) &&
                                         value.Length >= 6).ToArray();
    if (byId.Length == 1) return byId[0];
    var byName = library.Mods.Where(mod => mod.Manifest.Name.Equals(value, StringComparison.OrdinalIgnoreCase)).ToArray();
    return byName.Length switch
    {
        1 => byName[0],
        0 => throw new ArgumentException($"No library mod matches '{value}'. Run arkive-list to see mod IDs."),
        _ => throw new ArgumentException($"More than one mod is named '{value}'; use its ID from arkive-list."),
    };
}

static CliModEnvironment CreateModEnvironment(string projectRoot)
{
    var root = Path.GetFullPath(projectRoot);
    if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Arkive project root was not found: {root}");
    var staging = Path.Combine(root, "staging");
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var reader = new UpkPackageReader();
    var limitations = new List<string>();
    var umodelPath = UmodelLocator.FindExecutable();
    var bridge = umodelPath is null ? null : new UmodelBridge(umodelPath);
    var library = new ArkiveModLibrary(Path.Combine(root, "mods"), Path.Combine(staging, "mod-library"));
    var edits = new ArkiveModEditWorkspaces(
        new GuidedTextureModWorkspace(Path.Combine(staging, "texture-mods")),
        new SkeletalMeshEditWorkspaceStore(Path.Combine(staging, "skeletal-mesh-edits")),
        new StaticMeshEditWorkspaceStore(Path.Combine(staging, "static-mesh-edits"), reader, provider),
        new MaterialInstanceEditWorkspaceStore(Path.Combine(staging, "material-edits"), provider));
    var deployment = new ArkiveModDeploymentService(reader);
    var context = new ArkiveModApplyContext(
        library, edits, new GuidedTexturePackageWorkspace(Path.Combine(staging, "texture-mods", "_packages")),
        new ArkiveModSetWorkspace(Path.Combine(staging, "mod-sets")), deployment,
        Path.Combine(root, "backups", "packages"), Path.Combine(root, "backups", "installations"))
    {
        GameDataRoot = root,
    };
    NativeTextureExportService? nativeTextures = null;
    try
    {
        nativeTextures = new NativeTextureExportService(reader, provider);
    }
    catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
                                          BadImageFormatException or NotSupportedException)
    {
        limitations.Add($"Native texture export is unavailable ({exception.Message}); texture mods cannot be set up.");
    }
    var materialEdit = new MaterialInstanceEditService(provider);
    var candidates = Path.Combine(staging, "material-candidates");
    var materializer = new ArkiveModMaterializationService(library, edits, new ArkiveModMaterializationServices(
        reader, provider,
        new TextureReconnaissanceService(reader, provider),
        new TextureReconnaissanceWorkspaceStore(Path.Combine(staging, "texture-reconnaissance")), nativeTextures,
        new SkeletalMeshReconnaissanceService(reader, provider, bridge),
        new SkeletalMeshReconnaissanceWorkspaceStore(Path.Combine(staging, "skeletal-mesh-reconnaissance")),
        new SkeletalMeshEditableExportService(reader, provider),
        new StaticMeshReconnaissanceService(reader, provider, bridge),
        new StaticMeshReconnaissanceWorkspaceStore(Path.Combine(staging, "static-mesh-reconnaissance")),
        new StaticMeshEditableExportService(reader, provider),
        new MaterialInstanceReconnaissanceService(reader, provider),
        new MaterialInstanceWorkspaceStore(Path.Combine(staging, "material-reconnaissance")),
        materialEdit, candidates));
    UnifiedPackageComposerService? composer = null;
    var crunchPath = CrunchEncoderLocator.FindExecutable();
    if (bridge is null)
        limitations.Add("The Lost Ark UModel backend was not found; packages can be planned but not rebuilt.");
    else if (crunchPath is null || nativeTextures is null)
        limitations.Add("The pinned Crunch encoder or native texture support is missing; packages can be planned but not rebuilt.");
    else
        composer = CreateComposer(reader, provider, bridge, crunchPath, nativeTextures, materialEdit);
    var apply = new ArkiveModApplyService(context, materializer, composer);
    return new CliModEnvironment(context, new ArkiveModAuthoringService(library, edits)
    {
        CleanPackageResolver = (path, token) => apply.ResolveCleanPackageAsync(path, token),
    }, apply, limitations)
    {
        Materializer = materializer,
    };
}

static UnifiedPackageComposerService CreateComposer(
    UpkPackageReader reader,
    IChunkProtectionProvider provider,
    UmodelBridge bridge,
    string crunchPath,
    NativeTextureExportService nativeTextures,
    MaterialInstanceEditService materialEdit)
{
    var external = new UmodelModPackageExternalValidator(bridge);
    var decoder = new NativeCrunchDecoder();
    var textureBuilder = new GuidedTexturePackageBuildService(
        reader, provider, nativeTextures, new TextureImportCandidateService(),
        new TextureReplacementPayloadService(
            new TextureLayoutConfirmationService(reader, provider, decoder),
            new UnityCrunchEncoder(crunchPath), decoder),
        new TextureExportAssemblyService(reader, provider, decoder),
        new PackageRewritePlanService(reader, provider), new LogicalPackageImageService(reader, provider),
        new PhysicalPackagePlanService(reader, provider), new NoOpUpkReconstructionService(reader, provider),
        new ReplacementUpkBuildService(reader, provider), new ArkiveModBundleService(external, reader));
    return new UnifiedPackageComposerService(
        reader, provider, new SkeletalMeshReconnaissanceService(reader, provider, bridge),
        new SkeletalMeshEditableExportService(reader, provider), new SkeletalMeshNativeEncodingService(reader, provider),
        new SkeletalMeshStagedUpkService(reader, provider), textureBuilder,
        new ArkivePackageModBundleService(external, reader, provider), materialEdit,
        staticMeshReconnaissance: new StaticMeshReconnaissanceService(reader, provider, bridge),
        staticMeshEditableExport: new StaticMeshEditableExportService(reader, provider),
        staticMeshCompilation: new StaticMeshCompilationService(reader, provider));
}

static int? ReadIntegerOption(string[] arguments, string option)
{
    var value = ReadTextOption(arguments, option);
    if (value is null) return null;
    return int.TryParse(value, out var parsed)
        ? parsed
        : throw new ArgumentException($"{option} requires a whole number.");
}

static int ClientUpdateCheckAll(
    string projectRoot,
    string? outputRoot,
    string? stateRoot,
    bool json)
{
    var root = Path.GetFullPath(projectRoot);
    if (!Directory.Exists(root))
        throw new DirectoryNotFoundException($"Arkive project root was not found: {root}");
    var packageWorkspace = new GuidedTexturePackageWorkspace(
        Path.Combine(root, "staging", "texture-mods", "_packages"));
    var textureWorkspace = new GuidedTextureModWorkspace(
        Path.Combine(root, "staging", "texture-mods"));
    var meshWorkspace = new SkeletalMeshEditWorkspaceStore(
        Path.Combine(root, "staging", "skeletal-mesh-edits"));
    var packages = packageWorkspace.LoadAsync().GetAwaiter().GetResult();
    var textures = textureWorkspace.LoadAsync().GetAwaiter().GetResult();
    var meshes = meshWorkspace.LoadAsync().GetAwaiter().GetResult().Entries;
    var meshSources = meshes.ToDictionary(
        mesh => mesh.Manifest.EditId,
        mesh => LoadEditableMeshSourcePath(mesh.Manifest.SourceManifestPath),
        StringComparer.OrdinalIgnoreCase);
    var inputs = packages.Select(package => new ClientUpdatePackageInput(
        package,
        textures.Where(texture => SameFullPath(
            texture.Manifest.SourcePackagePath, package.Manifest.SourcePackagePath)).ToArray(),
        meshes.Where(mesh => SameFullPath(
            meshSources[mesh.Manifest.EditId], package.Manifest.SourcePackagePath)).ToArray())).ToArray();
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var service = new ClientUpdateAuditService(
        new ClientUpdateCompatibilityService(protectionProvider: provider));
    var progress = json ? null : new InlineProgress<ClientUpdateAuditProgress>(update =>
        Console.Error.WriteLine(
            $"[{update.CompletedPackages:N0}/{update.TotalPackages:N0}] {update.PackageName}: {update.Message}"));
    var report = service.InspectAllAsync(
            inputs,
            Path.GetFullPath(stateRoot ?? Path.Combine(root, "backups", "installations")),
            progress)
        .GetAwaiter().GetResult();
    var workspace = new ClientUpdateAuditWorkspaceStore(
        Path.GetFullPath(outputRoot ?? Path.Combine(root, "staging", "client-update-audits")));
    var entry = workspace.SaveAsync(report).GetAwaiter().GetResult();
    if (json)
    {
        WriteJson(report);
    }
    else
    {
        Console.WriteLine($"Client update state:    {report.State}");
        Console.WriteLine($"Package projects:      {report.Packages.Count:N0}");
        Console.WriteLine($"Exact clean packages:  {report.ExactPackageCount:N0}");
        Console.WriteLine($"Safe rebase packages:  {report.SafeRebasePackageCount:N0}");
        Console.WriteLine($"Review packages:       {report.ReviewPackageCount:N0}");
        Console.WriteLine($"Installation targets:  {report.InstallationTargets.Count:N0}");
        Console.WriteLine($"Format compatible:     {report.EveryAffectedPackageFormatCompatible}");
        Console.WriteLine($"Restore required:      {report.RequiresRestoreBeforeUpdateCheck}");
        Console.WriteLine($"Report:                {entry.ManifestPath}");
        Console.WriteLine(report.Summary);
        foreach (var package in report.Packages)
            Console.WriteLine($"  {package.PackageAlias}: {package.State} — {package.Summary}");
    }
    return report.State is ClientUpdateAuditState.Compatible or
        ClientUpdateAuditState.RebaseAvailable or ClientUpdateAuditState.NoModifiedPackages
        ? 0 : 1;

    static string LoadEditableMeshSourcePath(string manifestPath)
    {
        using var stream = File.OpenRead(Path.GetFullPath(manifestPath));
        var manifest = JsonSerializer.Deserialize<SkeletalMeshEditableExportManifest>(
                           stream, new JsonSerializerOptions
                           {
                               PropertyNameCaseInsensitive = true,
                               Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                           }) ??
                       throw new InvalidDataException(
                           $"Editable mesh source manifest was empty: {manifestPath}");
        return manifest.SourcePackagePath;
    }

    static bool SameFullPath(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

static int IndexBuild(
    string rootPath,
    string? cachePath,
    int? workers,
    string? scratchPath,
    bool json)
{
    var executable = UmodelLocator.FindExecutable() ??
        throw new FileNotFoundException(
            "No Lost Ark UModel executable was found. Place it in tools/external or set " +
            UmodelLocator.EnvironmentVariable + ".");
    var destination = string.IsNullOrWhiteSpace(cachePath)
        ? Path.Combine(
            UmodelLocator.FindProjectRoot() ?? Environment.CurrentDirectory,
            "cache",
            "library-index-cli")
        : Path.GetFullPath(cachePath);
    var parallelism = workers ?? PackageIndexStore.RecommendedMaxParallelism;
    var report = new PackageIndexStore(destination)
        .BuildAsync(
            rootPath,
            new UmodelBridge(executable),
            cancellationToken: default,
            maxParallelism: parallelism,
            hardLinkScratchRoot: scratchPath)
        .GetAwaiter()
        .GetResult();
    if (json)
    {
        WriteJson(new
        {
            report,
            Workers = parallelism,
            CachePath = destination,
            HardLinkScratch = scratchPath,
        });
    }
    else
    {
        Console.WriteLine(report.RootPath);
        Console.WriteLine($"  Workers:            {parallelism:N0}");
        Console.WriteLine($"  Packages:           {report.TotalPackages:N0}");
        Console.WriteLine($"  Indexed/cached:     {report.IndexedPackages:N0}/{report.CachedPackages:N0}");
        Console.WriteLine($"  Failed:             {report.FailedPackages:N0}");
        Console.WriteLine($"  Assets:             {report.IndexedAssets:N0}");
        Console.WriteLine($"  Duration:           {report.Duration.TotalSeconds:N2} seconds");
        Console.WriteLine($"  Cache:              {destination}");
        Console.WriteLine($"  Fast scratch:       {scratchPath ?? "disabled"}");
    }

    return report.FailedPackages == 0 ? 0 : 1;
}

static int InterfaceNames(string packagesRoot, string? metadataPath, string? filter, bool json)
{
    if (string.IsNullOrWhiteSpace(metadataPath))
        throw new ArgumentException("interface-names requires --metadata FILE (the names are saved beside it).");
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var document = InterfaceNameSidecar.BuildAsync(metadataPath, packagesRoot, ChunkProtectionProviderFactory.CreateWesternGlobal())
        .GetAwaiter().GetResult();
    var matches = document.Associations
        .Where(item => string.IsNullOrWhiteSpace(filter) || AssetSearch.MatchesDisplayNames(filter, [item.DisplayName]))
        .ToArray();
    if (json) WriteJson(new { document.Packages.Count, Names = document.Associations.Count, Matches = matches });
    else
    {
        Console.WriteLine($"Interface packages: {document.Packages.Count:N0}");
        Console.WriteLine($"Names:              {document.Associations.Count:N0} in {watch.Elapsed.TotalSeconds:N1}s");
        Console.WriteLine($"Saved:              {InterfaceNameSidecar.GetDefaultPath(metadataPath)}");
        if (!string.IsNullOrWhiteSpace(filter))
            foreach (var match in matches.Take(200))
                Console.WriteLine($"  {match.PackageName}.{match.ObjectName}: {match.DisplayName}");
    }
    return 0;
}

static int ZoneNames(string packagesRoot, string? metadataPath, string? filter, bool json)
{
    if (string.IsNullOrWhiteSpace(metadataPath))
        throw new ArgumentException("zone-names requires --metadata FILE (the names are saved beside it).");
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var document = ZoneMapSidecar.BuildAsync(metadataPath, packagesRoot).GetAwaiter().GetResult() ??
                   throw new FileNotFoundException("The game's leveldata1.lpk was not found beside its Packages folder.");
    var zones = string.IsNullOrWhiteSpace(filter) ? document.Zones : ZoneMapSidecar.Match(document, filter);
    if (json) WriteJson(new { Zones = document.Zones.Count, Matches = zones });
    else
    {
        Console.WriteLine($"Zones:              {document.Zones.Count:N0} in {watch.Elapsed.TotalSeconds:N1}s");
        Console.WriteLine($"Saved:              {ZoneMapSidecar.GetDefaultPath(metadataPath)}");
        if (!string.IsNullOrWhiteSpace(filter))
            foreach (var zone in zones.Take(200))
                Console.WriteLine($"  {zone.Id} {zone.Name}: {string.Join(", ", zone.Levels)}");
    }
    return 0;
}

// Makes a Game Data mod from a LostArkPatcher-style .sql patch, or lists the table changes of one mod or of data2.lpk.
static int GameDataMod(string projectRoot, string? sqlPath, string? name, string? modReference, bool json)
{
    var environment = CreateModEnvironment(projectRoot);
    var library = environment.Context.Library;
    var manifest = library.LoadAsync().GetAwaiter().GetResult();
    var service = environment.Context.GameDataFor(manifest) ??
                  throw new ArgumentException("The library's game folder is not set or has no data2.lpk; set it with the Mods tab's Game folder.");
    if (!string.IsNullOrWhiteSpace(sqlPath))
    {
        var import = GameDataAuthoring.ImportSqlAsync(service.Archive, File.ReadAllText(sqlPath)).GetAwaiter().GetResult();
        var target = modReference is null ? null : ResolveModReference(manifest, modReference);
        var entry = GameDataAuthoring.SaveModAsync(library, target?.ModId,
            target is null ? new ArkiveModMetadata(name ?? Path.GetFileNameWithoutExtension(sqlPath), "1.0.0") : null,
            import.Edits).GetAwaiter().GetResult();
        if (json) WriteJson(new { entry.ModId, entry.Manifest.Name, entry.FileName, Edits = import.Edits, import.Notes });
        else
        {
            Console.WriteLine($"{entry.Manifest.Name}: {import.Edits.Count:N0} table cell(s) ({entry.FileName}, {entry.ModId[..8]})");
            foreach (var edit in import.Edits.Take(50))
                Console.WriteLine($"  {edit.Database} {edit.Table}.{edit.Column} [{edit.RowLabel}]: {GameDataValues.Describe(edit.Original)} -> {GameDataValues.Describe(edit.Value)}");
            if (import.Edits.Count > 50) Console.WriteLine($"  ...and {import.Edits.Count - 50:N0} more");
            foreach (var note in import.Notes) Console.WriteLine($"note: {note}");
            Console.WriteLine("  Enable it with arkive-set --enable, then arkive-plan and arkive-apply --confirm.");
        }
        return 0;
    }
    var edits = modReference is null
        ? service.Archive.Current().Edits
        : ResolveModReference(manifest, modReference).Manifest.GameData?.Edits ?? [];
    if (json) WriteJson(new { service.Archive.ArchivePath, Current = service.Archive.Current(), Edits = edits });
    else
    {
        Console.WriteLine(modReference is null ? $"In {service.Archive.ArchivePath}:" : "The mod changes:");
        if (modReference is null) Console.WriteLine($"  Language: {service.Archive.Current().Language ?? "the game's own"}");
        foreach (var edit in edits)
            Console.WriteLine($"  {edit.Database} {edit.Table}.{edit.Column} [{edit.RowLabel}] = {GameDataValues.Describe(edit.Value)}");
        if (edits.Count == 0) Console.WriteLine("  No table changes.");
    }
    return 0;
}

static int Language(string efGame, string? dataRoot, string? use, bool restore, bool json)
{
    if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("language requires --data DIR (Arkive's data folder).");
    var service = new GameLanguageService(efGame, dataRoot, new LostArkProcessGuard());
    var watch = System.Diagnostics.Stopwatch.StartNew();
    if (service.NeedsSaving()) service.SaveAsync().GetAwaiter().GetResult();
    var saved = watch.Elapsed;
    int? lines = null;
    if (restore) service.RestoreAsync().GetAwaiter().GetResult();
    else if (!string.IsNullOrWhiteSpace(use)) lines = service.ApplyAsync(use).GetAwaiter().GetResult();
    var languages = service.Languages();
    if (json) WriteJson(new { Current = service.CurrentLanguage(), Lines = lines, Languages = languages });
    else
    {
        Console.WriteLine($"Saved text:         {saved.TotalSeconds:N1}s");
        foreach (var language in languages)
            Console.WriteLine($"  {language.Title,-22} {language.Coverage:P1} ({language.Translated:N0} of {language.Lines:N0}){(language.InGame ? string.Empty : " saved copy only")}");
        if (lines is not null) Console.WriteLine($"Applied {use}:     {lines:N0} lines in {(watch.Elapsed - saved).TotalSeconds:N1}s");
        Console.WriteLine($"Current:            {service.CurrentLanguage()}");
    }
    return 0;
}

static int MetadataResolve(
    string metadataPath,
    string? packageName,
    string? objectName,
    bool json)
{
    if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(objectName))
    {
        throw new ArgumentException("metadata-resolve requires --package NAME and --object NAME.");
    }

    var index = GameAssetMetadataIndex.Load(metadataPath);
    var matches = index.Find(packageName, packageName, objectName);
    if (json)
    {
        WriteJson(new
        {
            index.SourcePath,
            index.Language,
            index.AssociationCount,
            Package = packageName,
            Object = objectName,
            Matches = matches,
        });
    }
    else
    {
        Console.WriteLine($"{packageName}.{objectName}");
        Console.WriteLine($"  Mapping: {index.Language}, {index.AssociationCount:N0} relationships");
        Console.WriteLine($"  Matches: {matches.Count:N0}");
        foreach (var match in matches)
        {
            Console.WriteLine(
                $"  {match.DisplayName} | {match.EntityType} {match.EntityId} | " +
                $"{match.Field}={match.AssetReference}");
        }
    }

    return matches.Count > 0 ? 0 : 1;
}

static int CharacterTextureAudit(
    string metadataPath,
    string? indexDirectory,
    string? libraryRoot,
    string? outputPath,
    bool runReconnaissance,
    int? reconnaissanceLimit,
    bool json)
{
    if (string.IsNullOrWhiteSpace(indexDirectory) || string.IsNullOrWhiteSpace(libraryRoot))
    {
        throw new ArgumentException(
            "character-texture-audit requires --index DIR and --root PACKAGES_DIR.");
    }

    var metadata = GameAssetMetadataIndex.Load(metadataPath);
    var progress = new Progress<string>(message => Console.Error.WriteLine(message));
    var report = new CharacterTextureCoverageAuditService(
            ChunkProtectionProviderFactory.CreateWesternGlobal())
        .AuditAsync(new CharacterTextureCoverageRequest(
            metadata,
            indexDirectory,
            libraryRoot,
            runReconnaissance,
            reconnaissanceLimit), progress)
        .GetAwaiter()
        .GetResult();
    var destination = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(
            UmodelLocator.FindProjectRoot() ?? Environment.CurrentDirectory,
            "staging",
            "audits",
            $"player-character-textures-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json.gz")
        : Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    var reportJsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
    };
    if (destination.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
    {
        using var file = File.Create(destination);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        JsonSerializer.Serialize(gzip, report, reportJsonOptions);
    }
    else
    {
        File.WriteAllText(destination, JsonSerializer.Serialize(report, reportJsonOptions));
    }

    if (json)
    {
        WriteJson(new { ReportPath = destination, Report = report });
    }
    else
    {
        Console.WriteLine(destination);
        Console.WriteLine($"  Scope:               {report.Scope}");
        Console.WriteLine($"  Player classes:      {report.PlayerClassCount:N0}");
        Console.WriteLine($"  Appearance packages: {report.AppearancePackageCount:N0}");
        Console.WriteLine($"  Texture2D assets:    {report.TextureCount:N0}");
        Console.WriteLine($"  Reconnoitred:        {report.ReconnoitredTextureCount:N0}");
        Console.WriteLine($"  Native supported:    {report.NativeExportSupportedCount:N0}");
        Console.WriteLine($"  Gate not satisfied:  {report.NativeExportGateNotSatisfiedCount:N0}");
        Console.WriteLine($"  Failed/unresolved:   {report.FailedCount:N0}");
        Console.WriteLine($"  Duration:            {report.DurationSeconds:N2} seconds");
    }

    return report.FailedCount == 0 ? 0 : 1;
}

static int UmodelExport(
    string filePath,
    string? objectName,
    string? className,
    string? outputDirectory,
    bool json)
{
    if (string.IsNullOrWhiteSpace(objectName))
    {
        throw new ArgumentException("umodel-export requires --object NAME.");
    }

    var executable = UmodelLocator.FindExecutable() ??
        throw new FileNotFoundException(
            "No Lost Ark UModel executable was found. Place it in tools/external or set " +
            UmodelLocator.EnvironmentVariable + ".");
    var bridge = new UmodelBridge(executable);
    var listing = bridge.ListAsync(filePath).GetAwaiter().GetResult();
    var matches = listing.Assets
        .Where(asset =>
            string.Equals(asset.ObjectName, objectName, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(className) ||
             string.Equals(asset.ClassName, className, StringComparison.OrdinalIgnoreCase)))
        .ToArray();

    if (matches.Length == 0)
    {
        throw new ArgumentException(
            $"No export named '{objectName}'" +
            (string.IsNullOrWhiteSpace(className) ? string.Empty : $" with class '{className}'") +
            " was found in the package.");
    }

    if (matches.Length > 1)
    {
        throw new ArgumentException(
            $"'{objectName}' matched {matches.Length} exports. Add --class CLASS to select one.");
    }

    var asset = matches[0];
    var destination = string.IsNullOrWhiteSpace(outputDirectory)
        ? Path.Combine(
            UmodelLocator.FindProjectRoot() ?? Environment.CurrentDirectory,
            "staging",
            "umodel-cli",
            SafeFileName(Path.GetFileNameWithoutExtension(filePath)),
            $"{SafeFileName(asset.ObjectName)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}")
        : Path.GetFullPath(outputDirectory);
    var result = bridge.ExportAsync(
        filePath,
        asset,
        destination,
        allowPackageWideFallback:
            listing.Assets.Count <= UmodelBridge.DefaultMaxPackageWideFallbackExports).GetAwaiter().GetResult();

    if (json)
    {
        WriteJson(new
        {
            result.PackagePath,
            Backend = executable,
            result.Asset,
            result.OutputDirectory,
            result.OutputFiles,
            result.Diagnostics,
            result.Duration,
            result.UsedPackageWideFallback,
        });
    }
    else
    {
        Console.WriteLine($"Exported {asset.ClassName} {asset.ObjectName}");
        Console.WriteLine($"  Backend:            {executable}");
        Console.WriteLine($"  Output directory:   {result.OutputDirectory}");
        Console.WriteLine($"  Files produced:     {result.OutputFiles.Count:N0}");
        Console.WriteLine($"  Package fallback:   {result.UsedPackageWideFallback}");
        Console.WriteLine($"  Duration:           {result.Duration.TotalSeconds:N1} seconds");
        foreach (var outputFile in result.OutputFiles)
        {
            Console.WriteLine($"  File:               {outputFile}");
        }
    }

    return result.OutputFiles.Count > 0 ? 0 : 1;
}

static int IndexSearch(
    string packageRoot,
    string? cacheDirectory,
    string? metadataPath,
    string? query,
    string? category,
    string? scope,
    string? archetype,
    int? limit,
    bool json)
{
    if (string.IsNullOrWhiteSpace(cacheDirectory))
    {
        throw new ArgumentException("index-search requires --cache DIR.");
    }

    if (string.IsNullOrWhiteSpace(metadataPath))
    {
        throw new ArgumentException("index-search requires --metadata FILE.");
    }

    if (string.IsNullOrWhiteSpace(query))
    {
        throw new ArgumentException("index-search requires --query TEXT.");
    }

    var searchScope = Enum.TryParse<PackageIndexSearchScope>(scope, true, out var parsedScope)
        ? parsedScope
        : PackageIndexSearchScope.InGameName;
    var result = new PackageIndexStore(cacheDirectory)
        .SearchAsync(
            packageRoot,
            query,
            category,
            gameMetadata: GameAssetMetadataIndex.Load(metadataPath),
            searchScope: searchScope,
            archetype: archetype,
            resultLimit: limit ?? 5_000)
        .GetAwaiter()
        .GetResult();
    if (json)
    {
        WriteJson(result);
    }
    else
    {
        Console.WriteLine(
            $"{result.TotalMatches:N0} matches in {result.PackageCount:N0} packages " +
            $"({result.FailedPackageCount:N0} failed)");
        foreach (var match in result.Matches)
        {
            Console.WriteLine(
                $"  {match.PackageAlias} | {match.Kind} | {match.ObjectName} | " +
                GameAssetMetadataIndex.FormatNames(match.GameMetadata));
        }
    }

    return 0;
}

static int MetadataBuildDependencies(
    string metadataPath,
    string? indexDirectory,
    string? packageRoot,
    string? outputPath,
    string? scratchRoot,
    IReadOnlyList<string> queries,
    int? workers,
    bool json)
{
    if (string.IsNullOrWhiteSpace(indexDirectory))
    {
        throw new ArgumentException("metadata-build-dependencies requires --index DIR.");
    }

    if (string.IsNullOrWhiteSpace(packageRoot))
    {
        throw new ArgumentException("metadata-build-dependencies requires --root PACKAGES_DIR.");
    }

    var executable = UmodelLocator.FindExecutable() ??
        throw new FileNotFoundException(
            "No Lost Ark UModel executable was found. Place it in tools/external or set " +
            UmodelLocator.EnvironmentVariable + ".");
    var projectRoot = UmodelLocator.FindProjectRoot() ?? Environment.CurrentDirectory;
    var destination = string.IsNullOrWhiteSpace(outputPath)
        ? GameAssetDependencySidecar.GetDefaultPath(metadataPath)
        : Path.GetFullPath(outputPath);
    var scratch = string.IsNullOrWhiteSpace(scratchRoot)
        ? Path.Combine(projectRoot, "cache", "dependency-scratch")
        : Path.GetFullPath(scratchRoot);
    var resolver = new GameAssetDependencyResolver(
        new PackageIndexStore(indexDirectory),
        new UmodelBridge(executable));
    var progress = json
        ? null
        : new Progress<GameAssetDependencyProgress>(item =>
            Console.Error.WriteLine(
                $"[{item.Completed:N0}/{item.Total:N0}] cached={item.Cached:N0} " +
                $"failed={item.Failed:N0} {item.CurrentAsset}"));
    var report = resolver.BuildManyAsync(
            metadataPath,
            packageRoot,
            destination,
            scratch,
            queries,
            workers ?? 2,
            progress)
        .GetAwaiter()
        .GetResult();
    if (json)
    {
        WriteJson(report);
    }
    else
    {
        Console.WriteLine(report.OutputPath);
        Console.WriteLine($"  Query:                    {report.Query ?? "all qualified items"}");
        Console.WriteLine($"  Seed relationships:       {report.SeedAssociationCount:N0}");
        Console.WriteLine($"  Source meshes:            {report.SourceAssetCount:N0}");
        Console.WriteLine($"  Probed:                   {report.ProbedAssetCount:N0}");
        Console.WriteLine($"  Reused from cache:        {report.CachedAssetCount:N0}");
        Console.WriteLine($"  Failed:                   {report.FailedAssetCount:N0}");
        Console.WriteLine($"  Verified relationships:   {report.DependencyAssociationCount:N0}");
        Console.WriteLine($"  Duration:                 {report.Duration.TotalSeconds:N1} seconds");
    }

    return report.FailedAssetCount == 0 ? 0 : 1;
}

static int UmodelList(string filePath, string? filter, bool json)
{
    var executable = UmodelLocator.FindExecutable() ??
        throw new FileNotFoundException(
            "No Lost Ark UModel executable was found. Place it in tools/external or set " +
            UmodelLocator.EnvironmentVariable + ".");
    var result = new UmodelBridge(executable).ListAsync(filePath).GetAwaiter().GetResult();
    var assets = string.IsNullOrWhiteSpace(filter)
        ? result.Assets
        : result.Assets
            .Where(asset =>
                asset.ObjectName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                asset.ClassName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                asset.Kind.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    if (json)
    {
        WriteJson(new
        {
            result.PackagePath,
            Backend = executable,
            result.Duration,
            TotalAssets = result.Assets.Count,
            Assets = assets,
        });
    }
    else
    {
        Console.WriteLine(Path.GetFileName(result.PackagePath));
        Console.WriteLine($"  Backend:            {executable}");
        Console.WriteLine($"  Exports shown:      {assets.Count:N0} of {result.Assets.Count:N0}");
        Console.WriteLine($"  Duration:           {result.Duration.TotalSeconds:N1} seconds");
        Console.WriteLine();

        foreach (var asset in assets)
        {
            Console.WriteLine(
                $"{asset.ExportIndex,6}  {asset.Kind,-12}  {asset.ClassName,-24}  " +
                $"0x{asset.SerialOffset:X8}+0x{asset.SerialSize:X8}  {asset.ObjectName}");
        }
    }

    return 0;
}

static int Catalog(string filePath, string? filter, bool json)
{
    var provider = ChunkProtectionProviderFactory.CreateWesternGlobal();
    var catalog = new AssetCatalogReader(protectionProvider: provider).Read(filePath);
    var assets = string.IsNullOrWhiteSpace(filter)
        ? catalog.Assets
        : catalog.Assets
            .Where(asset =>
                asset.ObjectPath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                asset.ClassName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                asset.Kind.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    if (json)
    {
        WriteJson(new
        {
            catalog.Summary.FilePath,
            catalog.ProtectionProviderName,
            NameCount = catalog.Names.Count,
            ImportCount = catalog.Imports.Count,
            ExportCount = catalog.Exports.Count,
            Assets = assets,
        });
    }
    else
    {
        Console.WriteLine(Path.GetFileName(catalog.Summary.FilePath));
        Console.WriteLine($"  Protection:         {catalog.ProtectionProviderName}");
        Console.WriteLine($"  Names/imports:      {catalog.Names.Count:N0}/{catalog.Imports.Count:N0}");
        Console.WriteLine($"  Exports shown:      {assets.Count:N0} of {catalog.Exports.Count:N0}");
        Console.WriteLine();

        foreach (var asset in assets)
        {
            Console.WriteLine(
                $"{asset.ExportIndex,6}  {asset.Kind,-12}  {asset.ClassName,-24}  " +
                $"0x{asset.SerialOffset:X8}+0x{asset.SerialSize:X8}  {asset.ObjectPath}");
        }
    }

    return 0;
}

static int Inspect(string filePath, bool json)
{
    var summary = new UpkPackageReader().Read(filePath);
    var issues = PackageValidator.Validate(summary);

    if (json)
    {
        WriteJson(new { summary, validation = issues });
    }
    else
    {
        Console.WriteLine(Path.GetFileName(summary.FilePath));
        Console.WriteLine($"  Path:              {summary.FilePath}");
        Console.WriteLine($"  Physical size:     {summary.PhysicalFileSize:N0} bytes");
        Console.WriteLine($"  Version:           {summary.FileVersion}/{summary.LicenseeVersion}");
        Console.WriteLine($"  Engine/cooker:     {summary.EngineVersion}/{summary.CookerVersion}");
        Console.WriteLine($"  Logical header:    {summary.HeaderSize:N0} bytes");
        Console.WriteLine($"  Package flags:     0x{summary.PackageFlags:X8}");
        Console.WriteLine($"  Compression flags: 0x{summary.CompressionFlags:X8}");
        Console.WriteLine($"  Names:             {summary.NameCount:N0} at 0x{summary.NameOffset:X}");
        Console.WriteLine($"  Imports:           {summary.ImportCount:N0} at 0x{summary.ImportOffset:X}");
        Console.WriteLine($"  Exports:           {summary.ExportCount:N0} at 0x{summary.ExportOffset:X}");
        Console.WriteLine($"  Chunks:            {summary.CompressedChunks.Count:N0}");
        Console.WriteLine($"  Western profile:   {summary.IsWesternLostArkProfile}");

        Console.WriteLine("  Validation:");
        foreach (var issue in issues)
        {
            Console.WriteLine($"    [{issue.Severity}] {issue.Code}: {issue.Message}");
        }
    }

    return issues.Any(issue => issue.Severity == ValidationSeverity.Error) ? 1 : 0;
}

static int Scan(string directoryPath, int? limit, bool json)
{
    var report = new PackageScanner(new UpkPackageReader()).Scan(directoryPath, limit);
    var validation = report.Packages
        .Select(package => new
        {
            package.FilePath,
            Issues = PackageValidator.Validate(package),
        })
        .ToArray();
    var errorPackageCount = validation.Count(item =>
        item.Issues.Any(issue => issue.Severity == ValidationSeverity.Error));

    var result = new
    {
        report.DirectoryPath,
        report.CandidateCount,
        report.ScannedCount,
        ParsedCount = report.Packages.Count,
        FailureCount = report.Failures.Count,
        ValidationErrorPackageCount = errorPackageCount,
        Versions = report.Packages
            .GroupBy(package => $"{package.FileVersion}/{package.LicenseeVersion}")
            .OrderByDescending(group => group.Count())
            .ToDictionary(group => group.Key, group => group.Count()),
        PackageFlags = report.Packages
            .GroupBy(package => $"0x{package.PackageFlags:X8}")
            .OrderByDescending(group => group.Count())
            .ToDictionary(group => group.Key, group => group.Count()),
        CompressionFlags = report.Packages
            .GroupBy(package => $"0x{package.CompressionFlags:X8}")
            .OrderByDescending(group => group.Count())
            .ToDictionary(group => group.Key, group => group.Count()),
        MinimumChunkCount = report.Packages.Count == 0
            ? 0
            : report.Packages.Min(package => package.CompressedChunks.Count),
        MaximumChunkCount = report.Packages.Count == 0
            ? 0
            : report.Packages.Max(package => package.CompressedChunks.Count),
        report.Failures,
    };

    if (json)
    {
        WriteJson(result);
    }
    else
    {
        Console.WriteLine(result.DirectoryPath);
        Console.WriteLine($"  Candidates:         {result.CandidateCount:N0}");
        Console.WriteLine($"  Scanned:            {result.ScannedCount:N0}");
        Console.WriteLine($"  Parsed:             {result.ParsedCount:N0}");
        Console.WriteLine($"  Parse failures:     {result.FailureCount:N0}");
        Console.WriteLine($"  Validation errors:  {result.ValidationErrorPackageCount:N0} package(s)");
        Console.WriteLine($"  Chunk-count range:  {result.MinimumChunkCount:N0}-{result.MaximumChunkCount:N0}");
        PrintDistribution("Versions", result.Versions);
        PrintDistribution("Package flags", result.PackageFlags);
        PrintDistribution("Compression flags", result.CompressionFlags);

        foreach (var failure in report.Failures.Take(20))
        {
            Console.WriteLine($"  Failure: {Path.GetFileName(failure.FilePath)}: {failure.Error}");
        }
    }

    return report.Failures.Count > 0 || errorPackageCount > 0 ? 1 : 0;
}

static int? ReadLimit(string[] arguments)
{
    for (var index = 2; index < arguments.Length; index++)
    {
        if (!string.Equals(arguments[index], "--limit", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= arguments.Length ||
            !int.TryParse(arguments[index + 1], out var limit) ||
            limit <= 0)
        {
            throw new ArgumentException("--limit requires a positive integer.");
        }

        return limit;
    }

    return null;
}

static int? ReadPositiveIntOption(string[] arguments, string option)
{
    var value = ReadTextOption(arguments, option);
    if (value is null)
    {
        return null;
    }

    if (!int.TryParse(value, out var parsed) || parsed <= 0)
    {
        throw new ArgumentException($"{option} requires a positive integer.");
    }

    return parsed;
}

static string? ReadTextOption(string[] arguments, string option)
{
    for (var index = 2; index < arguments.Length; index++)
    {
        if (!string.Equals(arguments[index], option, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return arguments[index + 1];
    }

    return null;
}

static IReadOnlyList<string> ReadTextOptions(string[] arguments, string option)
{
    var values = new List<string>();
    for (var index = 2; index < arguments.Length; index++)
    {
        if (!string.Equals(arguments[index], option, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= arguments.Length ||
            arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        values.Add(arguments[++index]);
    }

    return values;
}

static void PrintDistribution(string label, IReadOnlyDictionary<string, int> distribution)
{
    Console.WriteLine($"  {label}:");
    foreach (var pair in distribution)
    {
        Console.WriteLine($"    {pair.Key}: {pair.Value:N0}");
    }
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"error: unknown command '{command}'.");
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
    Console.WriteLine("Arkive - offline Western Lost Ark UPK inspector");
    Console.WriteLine("Copyright (c) 2026 Arkive contributors. AGPL-3.0-only; no warranty. See LICENSE.txt and https://github.com/AN-SOTP/Arkive for source.");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  arkive database-build <data2.lpk> [--out FILE.json] [--language English] [--json]");
    Console.WriteLine("  arkive inspect <package.upk> [--json]");
    Console.WriteLine("  arkive artifact-inspect <texture.dds|model.gltf|model.glb> [--json]");
    Console.WriteLine("  arkive dds-rgb-view <dxt5.dds> [--out FILE] [--json]");
    Console.WriteLine("  arkive texture-export-native <package.upk> --object NAME [--alias NAME] [--out DIR] [--json]");
    Console.WriteLine("  arkive texture-project-create <arkive-native-texture.json> --edited FILE --workspace DIR [--name TEXT] [--alpha preserve-original|use-edited|force-opaque] [--json]");
    Console.WriteLine("  arkive outfit-swap-mod <Packages-directory> --index DIR --wear PIECE[,PIECE] --replace PIECE[,PIECE] --root Arkive-project-root --name TEXT [--game PACKAGES_DIR] [--cache DIR] [--json]");
    Console.WriteLine("  arkive outfit-swap <Packages-directory> --index DIR --wear PIECE[,PIECE] --replace PIECE[,PIECE] --out DIR [--cache DIR] [--bakes DIR] [--lit-bakes DIR] [--surface-bakes DIR] [--target-bakes DIR] [--target-surface-bakes DIR] [--json]");
    Console.WriteLine("  arkive model-viewer-scene <package.upk> --object NAME [--alias NAME] [--cache DIR] [--edits EDITS.json] [--json]");
    Console.WriteLine("  arkive skeletal-mesh-recon <package.upk> --object NAME [--alias NAME] [--out DIR] [--json]");
    Console.WriteLine("  arkive static-mesh-recon <package.upk> --object NAME [--alias NAME] [--out DIR] [--json]");
    Console.WriteLine("  arkive static-mesh-export-editable <arkive-staticmesh-recon.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive static-mesh-preflight <arkive-editable-staticmesh.json> --edited FILE.glb [--json]");
    Console.WriteLine("  arkive static-mesh-register-edit <arkive-editable-staticmesh.json> --edited FILE.glb --workspace DIR [--name NAME] [--json]");
    Console.WriteLine("  arkive static-mesh-compile <arkive-staticmesh-edit.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive material-recon <package.upk> --object NAME [--alias NAME] [--out DIR] [--json]");
    Console.WriteLine("  arkive material-create-edit <arkive-material-recon.json> [--name TEXT] [--out DIR] [--json]");
    Console.WriteLine("  arkive material-build-candidate <arkive-material-edit.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive skeletal-mesh-reconstruct-noop <arkive-skeletalmesh-recon.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive skeletal-mesh-export-editable <arkive-skeletalmesh-recon.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive body-proportions <player-mesh.arkive-edit.glb> [--set AREA=PERCENT ...] [--loose-clothing] [--hair] [--out FILE.glb] [--json]");
    Console.WriteLine("  arkive model-viewer-animations <package.upk> --animset NAME [--cache DIR] [--all] [--json]");
    Console.WriteLine("  arkive skeletal-mesh-preflight <arkive-editable-skeletalmesh.json> --edited FILE.glb [--json]");
    Console.WriteLine("  arkive skeletal-mesh-compare-skeletons <target-arkive-editable-skeletalmesh.json> --donor DONOR-MANIFEST [--json]");
    Console.WriteLine("  arkive skeletal-mesh-create-retarget <target-arkive-editable-skeletalmesh.json> --donor DONOR-MANIFEST [--name TEXT] [--out DIR] [--json]");
    Console.WriteLine("  arkive skeletal-mesh-register-edit <arkive-editable-skeletalmesh.json> --edited FILE.glb --workspace DIR [--name TEXT] [--json]");
    Console.WriteLine("  arkive skeletal-mesh-encode-native <arkive-edited-skeletalmesh.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive skeletal-mesh-compile-topology <arkive-edited-skeletalmesh.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive skeletal-mesh-build-staged-upk <arkive-skeletalmesh-native-encoding.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive skeletal-mesh-build-topology-upk <arkive-skeletalmesh-topology-compilation.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive skeletal-mesh-prepare-mod <arkive-skeletalmesh-staged-upk.json> [--name TEXT] [--out DIR] [--backup-root DIR] [--json]");
    Console.WriteLine("  arkive package-compose <arkive-package-mod.json> [--texture arkive-texture-mod.json ...] [--material arkive-material-edit.json ...] [--particle arkive-particle-edit.json ...] [--mesh arkive-edited-skeletalmesh.json ...] [--name TEXT] [--out DIR] [--backup-root DIR] [--preview-only] [--commit] [--json]");
    Console.WriteLine("  arkive face-map-projects <package.upk> --prefix PLAYER_CODE [--alias NAME] --workspace DIR --exports DIR --recon DIR --baselines DIR [--json]");
    Console.WriteLine(
        "  arkive texture-import-candidate <arkive-native-texture.json> --edited FILE [--alpha preserve-original|use-edited|force-opaque] [--out DIR] [--json]");
    Console.WriteLine("  arkive texture-pixel-diff <original.dds> --edited FILE [--json]");
    Console.WriteLine("  arkive texture-to-png <texture.png|texture.dds> [--out FILE] [--json]");
    Console.WriteLine("  arkive texture-layout-confirm <arkive-native-texture.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive texture-build-payloads <arkive-texture-import-candidate.json> [--out DIR] [--crunch EXE] [--json]");
    Console.WriteLine("  arkive texture-assemble-export <arkive-texture-replacement-payloads.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive texture-plan-rewrite <arkive-texture-export-assembly.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive texture-build-logical-image <arkive-package-rewrite-plan.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive texture-plan-physical <arkive-logical-package-image.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive texture-reconstruct-noop-upk <arkive-physical-package-plan.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive texture-build-replacement-upk <arkive-noop-upk-reconstruction.json> [--out DIR] [--json]");
    Console.WriteLine("  arkive mod-build-bundle <arkive-replacement-upk.json> [--name TEXT] [--out DIR] [--backup-root DIR] [--json]");
    Console.WriteLine("  arkive mod-status <arkive-mod.json> [--backup-root DIR] [--state-root DIR] [--json]");
    Console.WriteLine("  arkive mod-install <arkive-mod.json> --confirm [--backup-root DIR] [--state-root DIR] [--json]");
    Console.WriteLine("  arkive mod-restore <arkive-mod.json> --confirm [--backup-root DIR] [--state-root DIR] [--json]");
    Console.WriteLine("  arkive mod-set-inspect <arkive-mod-set.json> [--backup-root DIR] [--state-root DIR] [--json]");
    Console.WriteLine("  arkive mod-set-install <arkive-mod-set.json> --confirm [--backup-root DIR] [--state-root DIR] [--json]");
    Console.WriteLine("  arkive mod-set-restore <arkive-mod-set.json> --confirm [--backup-root DIR] [--state-root DIR] [--json]");
    Console.WriteLine("  arkive arkive-inspect <mod.arkive> [--json]");
    Console.WriteLine("  arkive arkive-list <Arkive-project-root> [--json]");
    Console.WriteLine("  arkive arkive-import <mod.arkive> [--root Arkive-project-root] [--enable] [--priority N] [--replace] [--json]");
    Console.WriteLine("  arkive arkive-set <Arkive-project-root> --mod ID|NAME [--enable|--disable] [--priority N] [--json]");
    Console.WriteLine("  arkive arkive-conflicts <Arkive-project-root> [--json]");
    Console.WriteLine("  arkive arkive-migrate <Arkive-project-root> [--author NAME] [--json]");
    Console.WriteLine("  arkive arkive-plan <Arkive-project-root> [--json]");
    Console.WriteLine("  arkive arkive-apply <Arkive-project-root> --confirm [--json]");
    Console.WriteLine("  arkive arkive-update <Arkive-project-root> --mod ID|NAME [--json]");
    Console.WriteLine("  arkive client-update-check-all <Arkive-project-root> [--out DIR] [--state-root DIR] [--json]");
    Console.WriteLine("  arkive catalog <package.upk> [--filter TEXT] [--json]");
    Console.WriteLine("  arkive interface-names <PACKAGES_DIR> --metadata arkive-asset-metadata.json [--filter TEXT] [--json]");
    Console.WriteLine("  arkive zone-names <PACKAGES_DIR> --metadata arkive-asset-metadata.json [--filter TEXT] [--json]");
    Console.WriteLine("  arkive game-data-mod <Arkive-project-root> [--sql PATCH.sql [--name NAME | --mod ID|NAME]] [--mod ID|NAME] [--json]");
    Console.WriteLine("  arkive language <EFGAME_DIR> --data DIR [--use LANGUAGE | --restore] [--json]");
    Console.WriteLine("  arkive umodel-list <package.upk> [--filter TEXT] [--json]");
    Console.WriteLine(
        "  arkive metadata-resolve <metadata.json> --package NAME --object NAME [--json]");
    Console.WriteLine(
        "  arkive metadata-build-dependencies <metadata.json> --index DIR --root PACKAGES_DIR [--query TEXT ...] [--workers N] [--out FILE] [--scratch DIR] [--json]");
    Console.WriteLine(
        "  arkive character-texture-audit <metadata.json> --index DIR --root PACKAGES_DIR [--recon] [--recon-limit N] [--out FILE] [--json]");
    Console.WriteLine(
        "  arkive index-build <Packages-directory> [--workers N] [--cache DIR] [--scratch DIR] [--json]");
    Console.WriteLine(
        "  arkive index-search <Packages-directory> --cache DIR --metadata FILE --query TEXT [--category KIND] [--scope InGameName|ObjectName|ObjectAndInGameName] [--archetype NAME] [--limit N] [--json]");
    Console.WriteLine(
        "  arkive umodel-export <package.upk> --object NAME [--class CLASS] [--out DIR] [--json]");
    Console.WriteLine("  arkive scan <Packages-directory> [--limit N] [--json]");
    Console.WriteLine();
    Console.WriteLine(
        $"Protected catalogues use Arkive's built-in {ChunkProtectionProviderFactory.WesternGlobalProfileId} profile.");
}

static string SafeFileName(string value)
{
    var invalid = Path.GetInvalidFileNameChars();
    return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
}


static void WriteJson<T>(T value)
{
    Console.WriteLine(JsonSerializer.Serialize(
        value,
        new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
}

sealed record CliModEnvironment(
    ArkiveModApplyContext Context,
    ArkiveModAuthoringService Authoring,
    ArkiveModApplyService Apply,
    IReadOnlyList<string> Limitations)
{
    public required ArkiveModMaterializationService Materializer { get; init; }
}

sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Action<T> _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public void Report(T value) => _handler(value);
}
