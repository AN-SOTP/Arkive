using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Arkive.Core.Interface;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Baselines;
using Arkive.Core.Discovery;
using Arkive.Core.External;
using Arkive.Core.Exports;
using Arkive.Core.Inspection;
using Arkive.Core.Meshes;
using Arkive.Core.Materials;
using Arkive.Core.Mods;
using Arkive.Core.Packages;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;
using Arkive.Core.Validation;
using Microsoft.Win32;

namespace Arkive.App;

public partial class MainWindow : Window
{
    private readonly UpkPackageReader _reader = new();
    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly UmodelBridge? _umodelBridge;
    private readonly SessionLog _sessionLog = new();
    private readonly PackageIndexStore _packageIndexStore;
    private readonly AssetDiscoveryService _assetDiscoveryService = new();
    private readonly string _projectRoot;
    private readonly ArkiveSettingsStore _settingsStore;
    private readonly ExportWorkspaceStore _exportWorkspaceStore;
    private readonly BaselineWorkspaceStore _baselineWorkspaceStore;
    private readonly TextureReconnaissanceService _textureReconnaissanceService;
    private readonly TextureReconnaissanceWorkspaceStore _textureReconnaissanceWorkspaceStore;
    private readonly SkeletalMeshReconnaissanceService _skeletalMeshReconnaissanceService;
    private readonly SkeletalMeshReconnaissanceWorkspaceStore _skeletalMeshReconnaissanceWorkspaceStore;
    private readonly SkeletalMeshNoOpService _skeletalMeshNoOpService;
    private readonly SkeletalMeshEditableExportService _skeletalMeshEditableExportService;
    private readonly SkeletalMeshAssetKitService? _skeletalMeshAssetKitService;
    private readonly SkeletalMeshEditableWorkspaceStore _skeletalMeshEditableWorkspaceStore;
    private readonly SkeletalMeshEditWorkspaceStore _skeletalMeshEditWorkspaceStore;
    private readonly SkeletalMeshNativeEncodingService _skeletalMeshNativeEncodingService;
    private readonly SkeletalMeshTopologyCompilerService _skeletalMeshTopologyCompilerService;
    private readonly SkeletalMeshStagedUpkService _skeletalMeshStagedUpkService;
    private readonly SkeletalMeshSkeletonCompatibilityService _skeletalMeshSkeletonCompatibilityService = new();
    private readonly SkeletalMeshRetargetWorkspaceService _skeletalMeshRetargetWorkspaceService = new();
    private readonly StaticMeshReconnaissanceService _staticMeshReconnaissanceService;
    private readonly StaticMeshReconnaissanceWorkspaceStore _staticMeshReconnaissanceWorkspaceStore;
    private readonly StaticMeshEditableExportService _staticMeshEditableExportService;
    private readonly StaticMeshEditWorkspaceStore _staticMeshEditWorkspaceStore;
    private readonly StaticMeshCompilationService _staticMeshCompilationService;
    private readonly NativeTextureExportService? _nativeTextureExportService;
    private readonly string? _nativeTextureExportUnavailableReason;
    private readonly ExportArtifactInspector _artifactInspector = new();
    private readonly TextureImportCandidateService _textureImportCandidateService = new();
    private readonly TextureLayoutConfirmationService? _textureLayoutConfirmationService;
    private readonly TextureReplacementPayloadService? _textureReplacementPayloadService;
    private readonly TextureExportAssemblyService? _textureExportAssemblyService;
    private readonly PackageRewritePlanService? _packageRewritePlanService;
    private readonly LogicalPackageImageService? _logicalPackageImageService;
    private readonly PhysicalPackagePlanService? _physicalPackagePlanService;
    private readonly NoOpUpkReconstructionService? _noOpUpkReconstructionService;
    private readonly ReplacementUpkBuildService? _replacementUpkBuildService;
    private readonly ArkiveModBundleService? _modBundleService;
    private readonly ArkiveMeshModBundleService? _meshModBundleService;
    private readonly ArkivePackageModBundleService? _packageModBundleService;
    private readonly ArkiveModDeploymentService _modDeploymentService = new();
    private readonly GuidedTextureModWorkspace _guidedTextureModWorkspace;
    private readonly GuidedTexturePackageWorkspace _guidedTexturePackageWorkspace;
    private readonly ArkiveModSetWorkspace _modSetWorkspace;
    private readonly ArkiveModSetService _modSetService;
    private readonly ModConflictDependencyService _modConflictDependencyService = new();
    private readonly UnifiedGuidedWorkflowService _unifiedGuidedWorkflowService = new();
    private readonly ClientUpdateCompatibilityService _clientUpdateCompatibilityService;
    private readonly ClientUpdateRebaseService? _clientUpdateRebaseService;
    private readonly ClientUpdateAuditService _clientUpdateAuditService;
    private readonly ClientUpdateAuditWorkspaceStore _clientUpdateAuditWorkspace;
    private readonly ClientUpdateBulkRebaseService? _clientUpdateBulkRebaseService;
    private readonly MaterialInstanceReconnaissanceService _materialReconnaissanceService;
    private readonly MaterialInstanceWorkspaceStore _materialWorkspaceStore;
    private readonly MaterialInstanceEditService _materialEditService;
    private readonly MaterialInstanceEditWorkspaceStore _materialEditWorkspaceStore;
    private readonly GuidedTextureModBuildService? _guidedTextureModBuildService;
    private readonly GuidedTexturePackageBuildService? _guidedTexturePackageBuildService;
    private readonly UnifiedPackageComposerService? _unifiedPackageComposerService;
    private readonly string? _textureReplacementUnavailableReason;
    private readonly ArkiveSettings _restoredSettings;
    private IReadOnlyList<AssetRow> _assets = [];
    private string? _currentPackagePath;
    private string? _libraryRootPath;
    private string? _libraryScratchPath;
    private CancellationTokenSource? _libraryIndexCancellation;
    private CancellationTokenSource? _librarySearchCancellation;
    private CancellationTokenSource? _artifactInspectionCancellation;
    private CancellationTokenSource? _relationshipCancellation;
    private CancellationTokenSource? _entityBrowserCancellation;
    private CancellationTokenSource? _baselineCancellation;
    private CancellationTokenSource? _assetLinkCancellation;
    private CancellationTokenSource? _modelPreviewResolutionCancellation;
    private PackageIndexMatch? _relationshipSeed;
    private GameAssetMetadataIndex? _gameMetadataIndex;
    private IReadOnlyList<GameEntityRecord> _entityBrowserEntities = [];
    private string? _gameMetadataPath;
    private bool _usingUmodelCatalogue;
    private bool _suppressLibrarySearchOptionRefresh;
    private bool _suppressEntityCustomizationSelection;
    private bool _exportInProgress;
    private bool _baselineOperationInProgress;
    private bool _textureReconnaissanceInProgress;
    private bool _skeletalMeshReconnaissanceInProgress;
    private bool _skeletalMeshNoOpInProgress;
    private bool _skeletalMeshEditableExportInProgress;
    private bool _skeletalMeshAssetKitInProgress;
    private bool _nativeTextureExportInProgress;
    private bool _textureImportCandidateInProgress;
    private bool _textureReplacementInProgress;
    private TextureReconnaissanceEntry? _currentTextureReconnaissance;
    private SkeletalMeshReconnaissanceEntry? _currentSkeletalMeshReconnaissance;
    private SkeletalMeshEditableExportResult? _currentSkeletalMeshEditableExport;
    private StaticMeshReconnaissanceEntry? _currentStaticMeshReconnaissance;
    private IReadOnlyList<StaticMeshEditableExportRow> _staticMeshEditableExports = [];
    private IReadOnlyList<StaticMeshEditRow> _staticMeshEdits = [];
    private bool _staticMeshWorkspaceBusy;
    private MaterialInstanceReconnaissanceEntry? _currentMaterialReconnaissance;
    private MaterialInstanceEditEntry? _currentMaterialEdit;
    private IReadOnlyList<MaterialScalarEditRow> _materialScalarRows = [];
    private IReadOnlyList<MaterialVectorEditRow> _materialVectorRows = [];
    private IReadOnlyList<MaterialTextureEditRow> _materialTextureRows = [];
    private IReadOnlyList<MaterialParentEditRow> _materialParentRows = [];
    private IReadOnlyList<MaterialProjectRow> _materialProjectRows = [];
    private IReadOnlyList<SkeletalMeshEditableExportRow> _skeletalMeshEditableExports = [];
    private IReadOnlyList<SkeletalMeshEditWorkspaceEntry> _skeletalMeshEditedGlbs = [];
    private bool _skeletalMeshEditableWorkspaceBusy;
    private bool _skeletalMeshDeploymentBusy;
    private int _skeletalMeshDeploymentRefreshVersion;
    private CancellationTokenSource? _skeletalMeshDeploymentCancellation;
    private ModDeploymentInspection? _selectedSkeletalMeshDeploymentInspection;
    private TextureImportCandidateManifest? _selectedTextureImportCandidate;
    private string? _selectedTextureImportCandidatePath;
    private string? _selectedTextureLayoutConfirmationPath;
    private string? _selectedTextureReplacementPayloadPath;
    private string? _selectedTextureExportAssemblyPath;
    private string? _selectedPackageRewritePlanPath;
    private string? _selectedLogicalPackageImagePath;
    private string? _selectedPhysicalPackagePlanPath;
    private string? _selectedNoOpUpkReconstructionPath;
    private string? _selectedReplacementUpkBuildPath;
    private string? _selectedReplacementUpkOutputPath;
    private string? _selectedModBundlePath;
    private string? _selectedDeploymentReadinessPath;
    private string? _selectedModPayloadPath;
    private string? _selectedModInstallationReceiptPath;
    private string? _selectedModBackupPath;
    private ModDeploymentInspection? _selectedModDeploymentInspection;
    private ModDeploymentInspection? _guidedDeploymentInspection;
    private string? _guidedDeploymentPackageProjectId;
    private string? _guidedDeploymentRevisionId;
    private IReadOnlyList<GuidedTexturePackageRow> _guidedPackageRows = [];
    private IReadOnlyList<GuidedTextureModRow> _guidedVisibleAssets = [];
    private IReadOnlyList<GuidedMeshModRow> _guidedVisibleMeshes = [];
    private IReadOnlyList<GuidedStaticMeshModRow> _guidedVisibleStaticMeshes = [];
    private IReadOnlyList<GuidedMaterialModRow> _guidedVisibleMaterials = [];
    private IReadOnlyList<ModifiedMeshUpkRow> _modifiedMeshUpkRows = [];
    private IReadOnlyList<ModSetRow> _modSetRows = [];
    private bool _workshopBusy;
    private ModConflictDependencyReport? _modConflictDependencyReport;
    private bool _modGraphBusy;
    private bool _modSetBusy;
    private ClientPackageCompatibilityReport? _selectedClientCompatibility;
    private ClientUpdateAuditEntry? _latestClientUpdateAudit;
    private bool _clientUpdateAuditBusy;
    private CancellationTokenSource? _clientUpdateAuditCancellation;
    private GuidedNextAction _guidedNextAction;
    private bool _guidedTextureModInProgress;
    private string _appearanceTheme = "System";
    private string _effectiveAppearanceTheme = "Light";
    private string _bannerArtworkPreference = BannerArtworkManager.RandomPreference;
    private BannerArtworkDefinition _activeBannerArtwork = BannerArtworkManager.Artwork[0];
    private bool _compactDensity;
    private bool _showValidationPane;
    private double _lastValidationPaneRatio = 0.2;

    public MainWindow() : this(ArkiveRuntimePaths.ResolveDataRoot())
    {
    }

    public MainWindow(string projectRoot)
    {
        _protectionProvider = ChunkProtectionProviderFactory.CreateWesternGlobal();
        _catalogReader = new AssetCatalogReader(_reader, _protectionProvider);

        var umodelPath = UmodelLocator.FindExecutable();
        if (umodelPath is not null)
        {
            _umodelBridge = new UmodelBridge(umodelPath);
        }

        _projectRoot = Path.GetFullPath(projectRoot);
        if (_umodelBridge is not null)
        {
            var externalValidator = new UmodelModPackageExternalValidator(_umodelBridge);
            _modBundleService = new ArkiveModBundleService(externalValidator);
            _meshModBundleService = new ArkiveMeshModBundleService(externalValidator);
            _packageModBundleService = new ArkivePackageModBundleService(
                externalValidator, _reader, _protectionProvider);
        }
        _packageIndexStore = new PackageIndexStore(
            Path.Combine(_projectRoot, "cache", "library-index"));
        _settingsStore = new ArkiveSettingsStore(
            Path.Combine(_projectRoot, "cache", "arkive-settings.json"));
        _exportWorkspaceStore = new ExportWorkspaceStore(
            Path.Combine(_projectRoot, "staging", "exports"));
        _guidedTextureModWorkspace = new GuidedTextureModWorkspace(
            Path.Combine(_projectRoot, "staging", "texture-mods"));
        _guidedTexturePackageWorkspace = new GuidedTexturePackageWorkspace(
            Path.Combine(_projectRoot, "staging", "texture-mods", "_packages"));
        _modSetWorkspace = new ArkiveModSetWorkspace(
            Path.Combine(_projectRoot, "staging", "mod-sets"));
        _modSetService = new ArkiveModSetService(
            _modSetWorkspace, _guidedTexturePackageWorkspace, _modDeploymentService);
        _clientUpdateCompatibilityService = new ClientUpdateCompatibilityService(
            _reader, _protectionProvider);
        _clientUpdateAuditService = new ClientUpdateAuditService(
            _clientUpdateCompatibilityService);
        _clientUpdateAuditWorkspace = new ClientUpdateAuditWorkspaceStore(
            Path.Combine(_projectRoot, "staging", "client-update-audits"));
        _baselineWorkspaceStore = new BaselineWorkspaceStore(
            Path.Combine(_projectRoot, "staging", "baselines"));
        _textureReconnaissanceService = new TextureReconnaissanceService(_reader, _protectionProvider);
        _textureReconnaissanceWorkspaceStore = new TextureReconnaissanceWorkspaceStore(
            Path.Combine(_projectRoot, "staging", "texture-reconnaissance"));
        _skeletalMeshReconnaissanceService = new SkeletalMeshReconnaissanceService(
            _reader, _protectionProvider, _umodelBridge);
        _skeletalMeshReconnaissanceWorkspaceStore = new SkeletalMeshReconnaissanceWorkspaceStore(
            Path.Combine(_projectRoot, "staging", "skeletal-mesh-reconnaissance"));
        _skeletalMeshNoOpService = new SkeletalMeshNoOpService(_reader, _protectionProvider);
        _skeletalMeshEditableExportService = new SkeletalMeshEditableExportService(
            _reader, _protectionProvider);
        if (_umodelBridge is not null)
            _skeletalMeshAssetKitService = new SkeletalMeshAssetKitService(_umodelBridge);
        _skeletalMeshEditableWorkspaceStore = new SkeletalMeshEditableWorkspaceStore(
            _skeletalMeshReconnaissanceWorkspaceStore.RootDirectory);
        _skeletalMeshEditWorkspaceStore = new SkeletalMeshEditWorkspaceStore(
            Path.Combine(_projectRoot, "staging", "skeletal-mesh-edits"));
        _skeletalMeshNativeEncodingService = new SkeletalMeshNativeEncodingService(
            _reader, _protectionProvider);
        _skeletalMeshTopologyCompilerService = new SkeletalMeshTopologyCompilerService(
            _reader, _protectionProvider);
        _skeletalMeshStagedUpkService = new SkeletalMeshStagedUpkService(
            _reader, _protectionProvider);
        _staticMeshReconnaissanceService = new StaticMeshReconnaissanceService(
            _reader, _protectionProvider, _umodelBridge);
        _staticMeshReconnaissanceWorkspaceStore = new StaticMeshReconnaissanceWorkspaceStore(
            Path.Combine(_projectRoot, "staging", "static-mesh-reconnaissance"));
        _staticMeshEditableExportService = new StaticMeshEditableExportService(
            _reader, _protectionProvider);
        _staticMeshEditWorkspaceStore = new StaticMeshEditWorkspaceStore(
            Path.Combine(_projectRoot, "staging", "static-mesh-edits"), _reader, _protectionProvider);
        _staticMeshCompilationService = new StaticMeshCompilationService(_reader, _protectionProvider);
        _materialReconnaissanceService = new MaterialInstanceReconnaissanceService(
            _reader, _protectionProvider);
        _materialWorkspaceStore = new MaterialInstanceWorkspaceStore(
            Path.Combine(_projectRoot, "staging", "material-reconnaissance"));
        _materialEditService = new MaterialInstanceEditService(_protectionProvider);
        _materialEditWorkspaceStore = new MaterialInstanceEditWorkspaceStore(
            Path.Combine(_projectRoot, "staging", "material-edits"), _protectionProvider);
        try
        {
            _nativeTextureExportService = new NativeTextureExportService(
                _reader,
                _protectionProvider);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or
                BadImageFormatException or NotSupportedException)
        {
            _nativeTextureExportUnavailableReason = exception.Message;
        }
        try
        {
            var decoder = new NativeCrunchDecoder();
            _textureLayoutConfirmationService = new TextureLayoutConfirmationService(
                _reader,
                _protectionProvider,
                decoder);
            _textureExportAssemblyService = new TextureExportAssemblyService(
                _reader,
                _protectionProvider,
                decoder);
            _packageRewritePlanService = new PackageRewritePlanService(
                _reader,
                _protectionProvider);
            _logicalPackageImageService = new LogicalPackageImageService(
                _reader,
                _protectionProvider);
            _physicalPackagePlanService = new PhysicalPackagePlanService(
                _reader,
                _protectionProvider);
            _noOpUpkReconstructionService = new NoOpUpkReconstructionService(
                _reader,
                _protectionProvider);
            _replacementUpkBuildService = new ReplacementUpkBuildService(
                _reader,
                _protectionProvider);
            var encoderPath = CrunchEncoderLocator.FindExecutable() ??
                              throw new FileNotFoundException(
                                  $"{CrunchEncoderLocator.ExecutableFileName} was not found beneath tools/external.");
            var encoder = new UnityCrunchEncoder(encoderPath);
            _textureReplacementPayloadService = new TextureReplacementPayloadService(
                _textureLayoutConfirmationService,
                encoder,
                decoder);
            if (_textureExportAssemblyService is not null &&
                _packageRewritePlanService is not null &&
                _logicalPackageImageService is not null &&
                _physicalPackagePlanService is not null &&
                _noOpUpkReconstructionService is not null &&
                _replacementUpkBuildService is not null &&
                _modBundleService is not null)
            {
                _guidedTextureModBuildService = new GuidedTextureModBuildService(
                    _textureImportCandidateService,
                    _textureReplacementPayloadService,
                    _textureExportAssemblyService,
                    _packageRewritePlanService,
                    _logicalPackageImageService,
                    _physicalPackagePlanService,
                    _noOpUpkReconstructionService,
                    _replacementUpkBuildService,
                    _modBundleService);
                if (_nativeTextureExportService is not null)
                {
                    _clientUpdateRebaseService = new ClientUpdateRebaseService(
                        _clientUpdateCompatibilityService, _textureReconnaissanceService,
                        _textureReconnaissanceWorkspaceStore, _nativeTextureExportService,
                        _guidedTextureModWorkspace, _skeletalMeshReconnaissanceService,
                        _skeletalMeshReconnaissanceWorkspaceStore, _skeletalMeshEditableExportService,
                        _skeletalMeshEditWorkspaceStore, _materialReconnaissanceService,
                        _materialWorkspaceStore, _materialEditWorkspaceStore);
                    _clientUpdateBulkRebaseService = new ClientUpdateBulkRebaseService(
                        _clientUpdateRebaseService, _guidedTexturePackageWorkspace,
                        _skeletalMeshEditWorkspaceStore);
                    _guidedTexturePackageBuildService = new GuidedTexturePackageBuildService(
                        _reader,
                        _protectionProvider,
                        _nativeTextureExportService,
                        _textureImportCandidateService,
                        _textureReplacementPayloadService,
                        _textureExportAssemblyService,
                        _packageRewritePlanService,
                        _logicalPackageImageService,
                        _physicalPackagePlanService,
                        _noOpUpkReconstructionService,
                        _replacementUpkBuildService,
                        _modBundleService);
                    if (_packageModBundleService is not null)
                    {
                        _unifiedPackageComposerService = new UnifiedPackageComposerService(
                            _reader, _protectionProvider, _skeletalMeshReconnaissanceService,
                            _skeletalMeshEditableExportService, _skeletalMeshNativeEncodingService,
                            _skeletalMeshStagedUpkService, _guidedTexturePackageBuildService,
                            _packageModBundleService, _materialEditService,
                            staticMeshReconnaissance: _staticMeshReconnaissanceService,
                            staticMeshEditableExport: _staticMeshEditableExportService,
                            staticMeshCompilation: _staticMeshCompilationService);
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or DllNotFoundException or EntryPointNotFoundException or
                BadImageFormatException or NotSupportedException)
        {
            _textureReplacementUnavailableReason = exception.Message;
        }
        _arkiveModLibrary = new ArkiveModLibrary(
            Path.Combine(_projectRoot, "mods"),
            Path.Combine(_projectRoot, "staging", "mod-library"));
        _arkiveModEdits = new ArkiveModEditWorkspaces(
            _guidedTextureModWorkspace, _skeletalMeshEditWorkspaceStore,
            _staticMeshEditWorkspaceStore, _materialEditWorkspaceStore)
        {
            InterfaceMovies = new InterfaceMovieEditWorkspaceStore(
                Path.Combine(_projectRoot, "staging", "interface-movie-edits"), _protectionProvider),
        };
        _arkiveModContext = new ArkiveModApplyContext(
            _arkiveModLibrary, _arkiveModEdits, _guidedTexturePackageWorkspace, _modSetWorkspace,
            _modDeploymentService, Path.Combine(_projectRoot, "backups", "packages"),
            Path.Combine(_projectRoot, "backups", "installations"));
        _arkiveModAuthoring = new ArkiveModAuthoringService(_arkiveModLibrary, _arkiveModEdits)
        {
            CleanPackageResolver = (path, token) => _arkiveModApply!.ResolveCleanPackageAsync(path, token),
        };
        _modAuthoringWorkspace = new ArkiveModAuthoringWorkspace(
            Path.Combine(_arkiveModLibrary.StateDirectory, "authoring"));
        _authoringMetadataTimer.Tick += AuthoringMetadataTimer_Tick;
        _modMaterializer = new ArkiveModMaterializationService(
                _arkiveModLibrary, _arkiveModEdits,
                new ArkiveModMaterializationServices(
                    _reader, _protectionProvider,
                    _textureReconnaissanceService, _textureReconnaissanceWorkspaceStore, _nativeTextureExportService,
                    _skeletalMeshReconnaissanceService, _skeletalMeshReconnaissanceWorkspaceStore,
                    _skeletalMeshEditableExportService,
                    _staticMeshReconnaissanceService, _staticMeshReconnaissanceWorkspaceStore,
                    _staticMeshEditableExportService,
                    _materialReconnaissanceService, _materialWorkspaceStore, _materialEditService,
                    Path.Combine(_projectRoot, "staging", "material-candidates")));
        _arkiveModApply = new ArkiveModApplyService(_arkiveModContext, _modMaterializer, _unifiedPackageComposerService,
            _umodelBridge is null ? @"UModel was not found in the Arkive folder's tools\external folder"
                : _textureReplacementUnavailableReason ?? _nativeTextureExportUnavailableReason);
        _restoredSettings = _settingsStore.Load();

        _appearanceTheme = AppearanceManager.NormalizeTheme(_restoredSettings.Theme);
        _bannerArtworkPreference = BannerArtworkManager.NormalizePreference(
            _restoredSettings.BannerArtwork);
        _activeBannerArtwork = BannerArtworkManager.ResolveLaunchSelection(
            _bannerArtworkPreference);
        _compactDensity = _restoredSettings.CompactDensity;
        _showValidationPane = _restoredSettings.ShowValidationPane;
        _effectiveAppearanceTheme = AppearanceManager.Apply(_appearanceTheme, _compactDensity);
        BannerArtworkManager.ApplyPalette(_activeBannerArtwork, _effectiveAppearanceTheme);

        InitializeComponent();
        InitializeBodyProportionsTab();
        InitializeWorkshopKinds();
        ApplyBannerArtwork(_activeBannerArtwork);
        SourceInitialized += (_, _) =>
            AppearanceManager.ApplyWindowChrome(this, _effectiveAppearanceTheme);
        ApplyRestoredSettings(_restoredSettings);
        Log($"Arkive session started. Log file: {_sessionLog.FilePath}");
        Log($"Arkive {typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"} " +
            $"from {AppContext.BaseDirectory}; {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}; " +
            $"{RuntimeInformation.FrameworkDescription}.");
        Log(
            $"Chunk protection provider selected: {_protectionProvider.Name}; " +
            $"profile={ChunkProtectionProviderFactory.WesternGlobalProfileId}. " +
            "No environment variable or persisted setting is required.");
        if (_nativeTextureExportService is not null)
        {
            Log(
                $"Native Texture2D exporter available: decoder={_nativeTextureExportService.DecoderName}, " +
                "profiles=exact PF_DXT1/BC1, PF_DXT5/BC3, and PF_BC5/ATI2 with inline one-level CRN or UE3/LZ4 mips.");
        }
        else
        {
            Log(
                $"Native Texture2D exporter unavailable: " +
                $"{_nativeTextureExportUnavailableReason ?? "decoder initialization failed"}.");
        }
        if (_textureReplacementPayloadService is not null)
        {
            Log(
                $"Texture replacement-payload workflow available: " +
                $"encoder={_textureReplacementPayloadService.EncoderName}; " +
                $"sha256={CrunchEncoderLocator.ExpectedSha256}; layout confirmation and decode-back are mandatory.");
            Log(
                "Texture export-assembly, rewrite-planning, logical-image, physical-plan, no-op, and staged replacement-UPK workflows available: " +
                "byte-identical no-op proof, standalone replacement replay, append-and-retarget dry run, " +
                "byte-verified uncompressed logical staging images, complete physical chunk inventories, and " +
                "source-identical plus replacement-bearing staging UPK reconstruction. Live deployment is available only " +
                "from a separately verified mod bundle with explicit confirmation and a durable backup receipt.");
        }
        else
        {
            Log(
                $"Texture replacement-payload workflow unavailable: " +
                $"{_textureReplacementUnavailableReason ?? "initialization failed"}.");
        }
        if (_umodelBridge is not null)
        {
            Log($"Lost Ark UModel backend detected: {_umodelBridge.ExecutablePath}");
            Log(
                "UModel catalogue profile: -game=lostark -na -nameresolve -noanim. " +
                "Selected-object inspection/export omits -nameresolve and the class selector for fork compatibility.");
            Log(
                "Mod-bundle deployment rehearsal available: exact-client identity, original physical filename, " +
                "package structure, backup capacity, UModel catalogue, and native object-load validation are mandatory. " +
                "Verified backup-first install and exact-payload restore are available from completed mod bundles. " +
                "Lost Ark and its anti-cheat launcher must be closed; every target mutation receives a durable receipt.");
            StatusText.Text =
                $"Lost Ark UModel backend detected: {Path.GetFileName(_umodelBridge.ExecutablePath)}";
        }
        else
        {
            Log("No Lost Ark UModel backend was detected.");
        }


        Log($"Library index cache: {_packageIndexStore.CacheDirectory}");
        Log($"Export workspace: {_exportWorkspaceStore.RootDirectory}");
        Log($"Baseline workspace: {_baselineWorkspaceStore.RootDirectory}");
        Log($"Texture reconnaissance workspace: {_textureReconnaissanceWorkspaceStore.RootDirectory}");
        Log($"SkeletalMesh reconnaissance workspace: {_skeletalMeshReconnaissanceWorkspaceStore.RootDirectory}");
        Log($"SkeletalMesh edited-GLB workspace: {_skeletalMeshEditWorkspaceStore.RootDirectory}");
        Log($"StaticMesh workspace: recon={_staticMeshReconnaissanceWorkspaceStore.RootDirectory}; edits={_staticMeshEditWorkspaceStore.RootDirectory}. Native UE3 StaticMesh3 parsing, stable GLB export, same/variable-topology compilation, retained collision evidence, cumulative composition, exact-object UModel validation, and reversible package deployment are enabled.");
        Log($"Mod library: folder={_arkiveModLibrary.ModsDirectory}, state={_arkiveModLibrary.ManifestPath}. " +
            "Portable .arkive mods (edited assets plus clean-client identity, never a game package), per-asset priority " +
            "resolution with blocking ties, first-run package-project conversion, setup from the user's own clean client, " +
            "and a reviewed restore/rebuild/install Apply are enabled.");
        Log($"Mod Set workspace: {_modSetWorkspace.RootDirectory}. Transactional multi-package preflight, conflict detection, reverse-order rollback, durable set receipts, Package Project batch rebuild/multi-install, and restore-all-active quick actions are enabled.");
        Log("Asset discovery and unified workshop enabled: evidence-backed name suggestions, relevance ranking, linked-only filtering, result facets, automatic friendly/object search routing, editor handoff, checked-asset composition, and package install/restore remain backed by the existing native validation gates.");
        Log("Package conflict/dependency viewer enabled: exact physical-target and native-object conflicts are blocking; serialized material references, mesh material slots, reference-skeleton contracts, cross-project exact identities, and retained StaticMesh collision are reported as evidence-labelled graph edges.");
        Log($"MaterialInstanceConstant workspace: {_materialEditWorkspaceStore.RootDirectory}. Persistent scalar/vector, same-UPK texture-reference and parent-reference projects, same-size candidates, locked-byte proof, cumulative package composition, UModel inspection, and reversible install/restore are enabled; cross-package imports and cooked static permutations remain locked.");
        Log($"Client-update safety workspace: {_clientUpdateAuditWorkspace.RootDirectory}. Full checks scan all modified package projects and retained installation targets, require restored originals, validate the Western package profile and read/write protection capability, decode every compressed chunk, and compare every checked native asset. Bulk safe rebase is manifest-transactional, preserves editable PNG/GLB files, and never changes game files.");
        Log("Native SkeletalMesh stage: Western 868/16 only; reference skeleton, LOD sections/chunks, GPU/index/colour buffers, UModel cross-checks, byte-identical replay, Blender-safe editable GLB export, durable edited-GLB registration, strict topology-preserving preflight, and topology-capable LOD0 replacement preflight are enabled. Native cloth arrays and exact MorphTargetSet/target/LOD/delta relationships are decoded: same-topology edits preserve active dependencies byte-for-byte, while topology replacement requires proven absent or inactive mappings. Topology replacements also verify exact source skeleton identity, original material names, bounded geometry, UV/colour streams, four-weight skinning, winding, and a duplicate-LOD0 target plan. Variable-size native topology compilation, decode-back proof, append-and-retarget staged-UPK reconstruction, exact unrelated-export validation, unified texture/material composition, original-name UModel export, and the second-batch controlled install/restore gate are enabled. Older preview-only revisions remain locked until rebuilt.");
        Log(
            $"Settings restored: file={_settingsStore.FilePath}, " +
            $"savedRoot={_restoredSettings.LibraryRootPath ?? "none"}, " +
            $"activeRoot={_libraryRootPath ?? "none"}, " +
            $"scratch={_libraryScratchPath ?? "disabled"}, " +
            $"workers={_restoredSettings.LibraryWorkers}, " +
            $"query={_restoredSettings.LibrarySearchText}, " +
            $"scope={_restoredSettings.LibrarySearchScope}, " +
            $"category={_restoredSettings.LibraryCategory}, " +
            $"archetype={_restoredSettings.LibraryArchetype}, " +
            $"tab={MainTabControl.SelectedIndex}, paneRatio={_restoredSettings.ResultsPaneRatio:N3}, " +
            $"theme={_appearanceTheme} (effective {_effectiveAppearanceTheme}), " +
            $"bannerPreference={_bannerArtworkPreference}, banner={_activeBannerArtwork.Id}, " +
            $"density={(_compactDensity ? "compact" : "comfortable")}, " +
            $"validationDrawer={_showValidationPane}, " +
            $"window={Width:N0}x{Height:N0}, maximized={WindowState == WindowState.Maximized}.");
        Loaded += MainWindow_Loaded;
    }

}
