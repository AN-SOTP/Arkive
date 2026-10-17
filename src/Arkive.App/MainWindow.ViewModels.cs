using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private enum GuidedNextAction
    {
        None,
        OpenEdit,
        Build,
        Install,
        Restore,
    }

    private sealed record GuidedTextureSelection(
        string PackagePath,
        string PackageAlias,
        string PhysicalPackageName,
        int ExportIndex,
        AssetKind Kind,
        string ClassName,
        string ObjectName,
        long SerialOffset,
        long SerialSize,
        IReadOnlyList<string> InGameNames);

    private sealed record SkeletalMeshAssetKitSelection(
        string PackagePath,
        string PackageAlias,
        string PhysicalPackageName,
        int ExportIndex,
        AssetKind Kind,
        string ClassName,
        string ObjectName,
        long SerialOffset,
        long SerialSize,
        IReadOnlyList<string> InGameNames);

    private sealed record ModGraphPackageFilterRow(string PackageProjectId, string DisplayName);

    private sealed record ModGraphConflictRow(ModProjectConflict Conflict)
    {
        public string Severity => Conflict.Severity.ToString();
        public string Code => Conflict.Code;
        public string PackageAlias => Conflict.PackageAlias;
        public string PackageProjectId => Conflict.PackageProjectIds.FirstOrDefault() ?? string.Empty;
        public string Objects => Conflict.ObjectNames.Count == 0
            ? "—"
            : string.Join(", ", Conflict.ObjectNames);
        public string Target => Conflict.Target;
        public string Message => Conflict.Message;
    }

    private sealed record ModGraphDependencyRow(ModAssetDependency Dependency)
    {
        public string PackageProjectId => Dependency.PackageProjectId;
        public string PackageAlias => Dependency.PackageAlias;
        public string SourceKind => Dependency.SourceKind;
        public string SourceObject => Dependency.SourceObject;
        public string Relation => Dependency.Relation;
        public string TargetKind => Dependency.TargetKind;
        public string TargetObject => Dependency.TargetObject;
        public string Resolution => Dependency.Resolution;
        public string Evidence => Dependency.Evidence;
    }


    private sealed class ModSetRow
    {
        public ModSetRow(ArkiveModSetEntry entry, ModSetInspection? inspection, string? error)
        { Entry = entry; Inspection = inspection; Error = error; }
        public ArkiveModSetEntry Entry { get; }
        public ModSetInspection? Inspection { get; }
        public string? Error { get; }
        public string DisplayName => Entry.Manifest.DisplayName;
        public string PackageSummary => $"{Entry.Manifest.Members.Count(member => member.Included):N0} / {Entry.Manifest.Members.Count:N0}";
        public string State => Error is not null ? "Attention required" : Inspection?.State.ToString() ?? "Not checked";
        public string ConflictSummary => Error ?? (Inspection?.Conflicts.Count > 0
            ? $"{Inspection.Conflicts.Count(conflict => conflict.Severity == ModSetConflictSeverity.Blocking):N0} blocking, " +
              $"{Inspection.Conflicts.Count(conflict => conflict.Severity == ModSetConflictSeverity.Information):N0} info"
            : "None");
        public string Summary => Error ?? Inspection?.Summary ?? "Set has not been inspected.";
    }

    private sealed class ModSetPackageRow
    {
        public ModSetPackageRow(GuidedTexturePackageRow package, bool included)
        { Package = package; Included = included; }
        public GuidedTexturePackageRow Package { get; }
        public bool Included { get; set; }
        public string DisplayName => Package.DisplayName;
        public string PackageAlias => Package.PackageAlias;
        public string PhysicalPackageName => Package.PhysicalPackageName;
        public string AssetSummary => Package.AssetSummary;
        public string State => Package.LatestRevision is null ? "Build package first" : Package.State;
    }

    private sealed class ModSetConflictRow
    {
        public ModSetConflictRow(ModSetConflict conflict) => Conflict = conflict;
        public ModSetConflict Conflict { get; }
        public string Severity => Conflict.Severity.ToString();
        public string Code => Conflict.Code;
        public string Target => Conflict.TargetPath ?? "â€”";
        public string Message => Conflict.Message;
    }

    private sealed class MaterialScalarEditRow
    {
        public MaterialScalarEditRow(MaterialScalarEdit item)
        { Name = item.Name; SourceValue = item.SourceValue; Value = item.Value; }
        public string Name { get; }
        public float SourceValue { get; }
        public float Value { get; set; }
        public MaterialScalarEdit ToEdit() => new(Name, SourceValue, Value);
    }

    private sealed record MaterialProjectRow(MaterialInstanceEditEntry Entry)
    {
        public string DisplayName => Entry.Document.DisplayName;
        public string PackageAlias => Entry.Document.PackageAlias;
        public string ObjectName => Entry.Document.ObjectName;
        public string ParameterSummary =>
            $"{Entry.Document.Scalars.Count:N0} scalar + {Entry.Document.Vectors.Count:N0} vector + " +
            $"{Entry.Document.Textures.Count:N0} texture + {(Entry.Document.Parent is null ? 0 : 1)} parent";
        public string State => string.IsNullOrWhiteSpace(Entry.Document.LatestCandidateManifestPath) ||
                               !File.Exists(Entry.Document.LatestCandidateManifestPath)
            ? "Editing — verify changes"
            : Entry.Document.IncludedInPackageBuild
                ? "Verified — checked for build"
                : "Verified — excluded";
        public string Updated => Entry.Document.UpdatedAt.ToLocalTime().ToString("g");
        public string DirectoryPath => Entry.DirectoryPath;
    }

    private sealed class MaterialVectorEditRow
    {
        public MaterialVectorEditRow(MaterialVectorEdit item)
        {
            Name = item.Name; SourceRed = item.SourceRed; SourceGreen = item.SourceGreen;
            SourceBlue = item.SourceBlue; SourceAlpha = item.SourceAlpha;
            Red = item.Red; Green = item.Green; Blue = item.Blue; Alpha = item.Alpha;
        }
        public string Name { get; }
        public float SourceRed { get; }
        public float SourceGreen { get; }
        public float SourceBlue { get; }
        public float SourceAlpha { get; }
        public float Red { get; set; }
        public float Green { get; set; }
        public float Blue { get; set; }
        public float Alpha { get; set; }
        public string Original => $"{SourceRed:R}, {SourceGreen:R}, {SourceBlue:R}, {SourceAlpha:R}";
        public void Reset() { Red = SourceRed; Green = SourceGreen; Blue = SourceBlue; Alpha = SourceAlpha; }
        public MaterialVectorEdit ToEdit() => new(Name, SourceRed, SourceGreen, SourceBlue, SourceAlpha,
            Red, Green, Blue, Alpha);
    }

    private sealed class MaterialTextureEditRow
    {
        private readonly MaterialTextureReference _source;

        public MaterialTextureEditRow(
            MaterialTextureReference item,
            IReadOnlyList<MaterialObjectReferenceOption> available)
        {
            _source = item;
            Name = item.Name;
            SourceResolvedObject = item.SourceResolvedObject;
            var fallback = new MaterialObjectReferenceOption(
                item.ObjectReference, "Unknown", item.ResolvedObject, item.ResolvedObject,
                item.ResolvedObject, item.TargetIdentity, item.ObjectReference == 0
                    ? MaterialObjectReferenceScope.None
                    : MaterialObjectReferenceScope.Export);
            var selected = available.FirstOrDefault(option =>
                               option.ObjectReference == item.ObjectReference &&
                               (string.IsNullOrWhiteSpace(item.TargetIdentity) ||
                                option.StableIdentity.Equals(item.TargetIdentity, StringComparison.OrdinalIgnoreCase))) ??
                           available.FirstOrDefault(option => option.ObjectReference == item.ObjectReference) ?? fallback;
            Options = available.Any(option => option.ObjectReference == selected.ObjectReference)
                ? available
                : [selected, .. available];
            SelectedOption = selected;
        }

        public string Name { get; }
        public string SourceResolvedObject { get; }
        public IReadOnlyList<MaterialObjectReferenceOption> Options { get; }
        public MaterialObjectReferenceOption SelectedOption { get; set; }

        public void Reset()
        {
            SelectedOption = Options.FirstOrDefault(option =>
                                 option.ObjectReference == _source.SourceObjectReference) ??
                             SelectedOption;
        }

        public MaterialTextureReference ToEdit()
        {
            var sourceOption = Options.FirstOrDefault(option =>
                option.ObjectReference == _source.SourceObjectReference);
            return new(Name, SelectedOption.ObjectReference, SelectedOption.ResolvedObject)
            {
                SourceObjectReference = _source.SourceObjectReference,
                SourceResolvedObject = _source.SourceResolvedObject,
                SourceIdentity = string.IsNullOrWhiteSpace(_source.SourceIdentity)
                    ? sourceOption?.StableIdentity ?? string.Empty
                    : _source.SourceIdentity,
                TargetIdentity = SelectedOption.StableIdentity,
            };
        }
    }

    private sealed class MaterialParentEditRow
    {
        private readonly MaterialParentReference _source;

        public MaterialParentEditRow(
            MaterialParentReference item,
            IReadOnlyList<MaterialObjectReferenceOption> available)
        {
            _source = item;
            var fallback = new MaterialObjectReferenceOption(
                item.ObjectReference, "Unknown", item.ResolvedObject, item.ResolvedObject,
                item.ResolvedObject, item.TargetIdentity, item.ObjectReference == 0
                    ? MaterialObjectReferenceScope.None
                    : MaterialObjectReferenceScope.Export);
            var selected = available.FirstOrDefault(option =>
                               option.ObjectReference == item.ObjectReference &&
                               (string.IsNullOrWhiteSpace(item.TargetIdentity) ||
                                option.StableIdentity.Equals(item.TargetIdentity, StringComparison.OrdinalIgnoreCase))) ??
                           available.FirstOrDefault(option => option.ObjectReference == item.ObjectReference) ?? fallback;
            Options = available.Any(option => option.ObjectReference == selected.ObjectReference)
                ? available
                : [selected, .. available];
            SelectedOption = selected;
        }

        public string Name => "Parent";
        public string SourceResolvedObject => _source.SourceResolvedObject;
        public IReadOnlyList<MaterialObjectReferenceOption> Options { get; }
        public MaterialObjectReferenceOption SelectedOption { get; set; }

        public void Reset()
        {
            SelectedOption = Options.FirstOrDefault(option =>
                                 option.ObjectReference == _source.SourceObjectReference) ??
                             SelectedOption;
        }

        public MaterialParentReference ToEdit()
        {
            var sourceOption = Options.FirstOrDefault(option =>
                option.ObjectReference == _source.SourceObjectReference);
            return new(_source.SourceObjectReference, _source.SourceResolvedObject,
                string.IsNullOrWhiteSpace(_source.SourceIdentity)
                    ? sourceOption?.StableIdentity ?? string.Empty
                    : _source.SourceIdentity,
                SelectedOption.ObjectReference, SelectedOption.ResolvedObject,
                SelectedOption.StableIdentity);
        }
    }

    private abstract class GuidedAssetRow
    {
        public abstract string EditId { get; }
        public bool Included { get; set; }
        public string? OwnerModName { get; set; }
        public bool CanToggleInclude => OwnerModName is null;
        public string ModColumn => OwnerModName ?? "Draft (no mod)";
        public string IncludeToolTip => OwnerModName is null
            ? "Draft edit: tick it to include it in this UPK's next build."
            : $"Managed by mod '{OwnerModName}'. Enable the mod or change its priority in Mod library.";
    }

    private sealed class GuidedTextureModRow : GuidedAssetRow
    {
        public GuidedTextureModRow(
            GuidedTextureModProjectEntry entry,
            GuidedTexturePackageRevision? packageRevision,
            string? currentEditableSha256,
            IReadOnlyList<GameAssetAssociation> metadata,
            ModDeploymentInspection? deploymentInspection)
        {
            Entry = entry;
            Included = entry.Manifest.IncludedInPackageBuild is not false;
            var builtAsset = packageRevision?.Assets.FirstOrDefault(asset =>
                asset.ProjectId.Equals(entry.Manifest.ProjectId, StringComparison.OrdinalIgnoreCase));
            NeedsBuild = Included
                ? builtAsset is null ||
                  string.IsNullOrWhiteSpace(currentEditableSha256) ||
                  !builtAsset.EditedImageSha256.Equals(
                      currentEditableSha256,
                      StringComparison.OrdinalIgnoreCase)
                : builtAsset is not null;
            var mappedUses = metadata
                .Select(item => string.IsNullOrWhiteSpace(item.Archetype)
                    ? item.DisplayName
                    : $"{item.DisplayName} ({item.Archetype})")
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToArray();
            var verifiedUse = WesternGlobalCustomizationMappings.DescribeVerifiedUses(entry.Manifest.ObjectName);
            ImpactNames = mappedUses.Length > 0
                ? string.Join(", ", mappedUses)
                : verifiedUse;
            ImpactSummary = string.IsNullOrWhiteSpace(ImpactNames) || ImpactNames.Equals("Unmapped", StringComparison.OrdinalIgnoreCase)
                ? "Impact: global on this client. No verified in-game use is mapped yet; every actor that references this texture will change."
                : $"Impact: global on this client — affects {ImpactNames}.";

            var installed = deploymentInspection?.CanRestore == true ||
                            deploymentInspection?.State is ModInstallationState.Installed or ModInstallationState.Prepared;
            State = !File.Exists(entry.Manifest.EditableImagePath)
                ? "Missing editable image"
                : installed && NeedsBuild
                    ? "Installed build outdated"
                    : installed && Included
                        ? "Installed"
                        : NeedsBuild
                            ? !Included ? "Excluded — rebuild to remove" :
                                builtAsset is null ? "Editing — not built" : "Editing — changes not built"
                            : !Included ? "Excluded"
                            : "Built — current";
        }

        public GuidedTextureModProjectEntry Entry { get; }

        public override string EditId => Entry.Manifest.ProjectId;


        public bool NeedsBuild { get; }

        public string Favorite => Entry.Manifest.IsFavorite ? "★" : string.Empty;

        public string ImpactNames { get; }

        public string ImpactSummary { get; }

        public GuidedTextureModRevision? LatestRevision => Entry.Manifest.LatestRevisionId is null
            ? Entry.Manifest.Revisions.LastOrDefault()
            : Entry.Manifest.Revisions.FirstOrDefault(revision => revision.RevisionId.Equals(
                Entry.Manifest.LatestRevisionId, StringComparison.OrdinalIgnoreCase));

        public string Updated => (Entry.Manifest.LastOpenedAt ?? Entry.Manifest.UpdatedAt).LocalDateTime.ToString("g");

        public string DisplayName => Entry.Manifest.DisplayName;

        public string PackageAlias => Entry.Manifest.PackageAlias;

        public string ObjectName => Entry.Manifest.ObjectName;

        public string VerifiedUse => ImpactNames;

        public string Format => $"{Entry.Manifest.PixelFormat} {Entry.Manifest.Width}x{Entry.Manifest.Height}";

        public string State { get; }

        public string EditableImage => Entry.Manifest.EditableImagePath;

        public int RevisionCount => Entry.Manifest.Revisions.Count;

        public string OutputUpk => LatestRevision?.OutputUpkPath ?? "Not built yet";

        public string PayloadSize => LatestRevision is null
            ? "-"
            : $"{LatestRevision.PayloadBytes / 1024d / 1024d:N2} MiB";
    }

    private sealed class GuidedMeshModRow : GuidedAssetRow
    {
        public GuidedMeshModRow(
            SkeletalMeshEditWorkspaceEntry entry,
            GuidedTexturePackageRevision? packageRevision,
            string? currentEditedGlbSha256,
            ModDeploymentInspection? deploymentInspection)
        {
            Entry = entry;
            Included = entry.Manifest.IncludedInPackageBuild;
            var builtAsset = packageRevision?.MeshAssets.FirstOrDefault(asset =>
                asset.EditId.Equals(entry.Manifest.EditId, StringComparison.OrdinalIgnoreCase));
            RegistrationHasCurrentAnimationProof =
                entry.Manifest.Preflight.SchemaVersion == SkeletalMeshEditPreflightService.CurrentSchemaVersion &&
                entry.Manifest.Preflight.AnimationValidation is { Passed: true };
            BuiltAssetHasCurrentAnimationProof = builtAsset is null || HasCurrentAnimationProof(builtAsset);
            // Legacy registrations remain immutable provenance. A current package build reruns
            // preflight against the exact edited-GLB hash and stores that proof in its encoding
            // manifest, so that newer build proof supersedes an older registration schema.
            NeedsBuild = Included
                ? builtAsset is null || string.IsNullOrWhiteSpace(currentEditedGlbSha256) ||
                  !builtAsset.EditedGlbSha256.Equals(
                      currentEditedGlbSha256, StringComparison.OrdinalIgnoreCase) ||
                  !BuiltAssetHasCurrentAnimationProof
                : builtAsset is not null;
            var installed = deploymentInspection?.CanRestore == true ||
                            deploymentInspection?.State is ModInstallationState.Installed or
                                ModInstallationState.Prepared;
            State = !File.Exists(entry.EditedGlbPath)
                ? "Missing edited GLB"
                : entry.Manifest.Preflight.Readiness == SkeletalMeshEditReadiness.Refused
                    ? "Refused — review mesh diagnostics"
                    : !RegistrationHasCurrentAnimationProof && NeedsBuild
                        ? installed
                            ? "Installed safety proof outdated — restore, then rebuild"
                            : "Animation safety outdated — rebuild"
                        : !BuiltAssetHasCurrentAnimationProof
                            ? installed
                                ? "Installed proof outdated — restore, then rebuild"
                                : "Built proof outdated — rebuild"
                    : installed && NeedsBuild
                        ? "Installed build outdated"
                        : installed && Included
                            ? "Installed"
                            : NeedsBuild
                                ? !Included ? "Excluded — rebuild to remove" :
                                    builtAsset is null ? "Editing — not built" : "Editing — changes not built"
                                : !Included ? "Excluded" : "Built — current";
        }

        public SkeletalMeshEditWorkspaceEntry Entry { get; }
        public override string EditId => Entry.Manifest.EditId;
        public bool NeedsBuild { get; }
        public bool RegistrationHasCurrentAnimationProof { get; }
        public bool BuiltAssetHasCurrentAnimationProof { get; }
        public string DisplayName => Entry.Manifest.DisplayName;
        public string ObjectName => Entry.Manifest.ObjectName;
        public string ImportProfile => Entry.Manifest.Preflight.Readiness ==
                                       SkeletalMeshEditReadiness.ReadyForTopologyCompilation
            ? "Variable-size topology"
            : "Same-size deformation";
        public string ChangedPositions => Entry.Manifest.Preflight.ChangedPositionVertexCount.ToString("N0");
        public string MaximumDelta => $"{Entry.Manifest.Preflight.MaximumPositionDeltaMetres * 100d:N2} cm";
        public string State { get; }
        public string EditedGlbPath => Entry.EditedGlbPath;

        private static bool HasCurrentAnimationProof(GuidedMeshPackageAssetRevision built)
        {
            var path = built.VariableSizeReplacement
                ? built.TopologyCompilationManifestPath
                : built.NativeEncodingManifestPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                var expectedSchema = built.VariableSizeReplacement
                    ? SkeletalMeshTopologyCompilerService.CurrentSchemaVersion
                    : SkeletalMeshNativeEncodingService.CurrentSchemaVersion;
                return root.TryGetProperty("schemaVersion", out var schema) &&
                       schema.TryGetInt32(out var value) && value == expectedSchema &&
                       root.TryGetProperty("preflight", out var preflight) &&
                       preflight.TryGetProperty("schemaVersion", out var preflightSchema) &&
                       preflightSchema.TryGetInt32(out var preflightValue) &&
                       preflightValue == SkeletalMeshEditPreflightService.CurrentSchemaVersion &&
                       preflight.TryGetProperty("animationValidation", out var animation) &&
                       animation.TryGetProperty("passed", out var passed) && passed.ValueKind == JsonValueKind.True;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private sealed class GuidedMaterialModRow : GuidedAssetRow
    {
        public GuidedMaterialModRow(
            MaterialInstanceEditEntry entry,
            GuidedTexturePackageRevision? packageRevision,
            ModDeploymentInspection? deploymentInspection)
        {
            Entry = entry;
            Included = entry.Document.IncludedInPackageBuild;
            var built = packageRevision?.MaterialAssets.FirstOrDefault(asset =>
                asset.EditId.Equals(entry.Document.EditId, StringComparison.OrdinalIgnoreCase));
            var candidateReady = !string.IsNullOrWhiteSpace(entry.Document.LatestCandidateManifestPath) &&
                                 File.Exists(entry.Document.LatestCandidateManifestPath) &&
                                 !string.IsNullOrWhiteSpace(entry.Document.LatestCandidateSha256);
            NeedsBuild = Included
                ? built is null || !candidateReady ||
                  !built.CandidateExportSha256.Equals(
                      entry.Document.LatestCandidateSha256, StringComparison.OrdinalIgnoreCase)
                : built is not null;
            var installed = deploymentInspection?.CanRestore == true ||
                            deploymentInspection?.State is ModInstallationState.Installed or
                                ModInstallationState.Prepared;
            State = !candidateReady
                ? "Editing — save & verify first"
                : installed && NeedsBuild
                    ? "Installed build outdated"
                    : installed && Included
                        ? "Installed"
                        : NeedsBuild
                            ? !Included ? "Excluded — rebuild to remove" :
                                built is null ? "Verified — not built" : "Changes not built"
                            : !Included ? "Excluded" : "Built — current";
        }

        public MaterialInstanceEditEntry Entry { get; }
        public override string EditId => Entry.Document.EditId;
        public bool NeedsBuild { get; }
        public string DisplayName => Entry.Document.DisplayName;
        public string ObjectName => Entry.Document.ObjectName;
        public string Parameters =>
            $"{Entry.Document.Scalars.Count:N0} scalar + {Entry.Document.Vectors.Count:N0} vector";
        public string Updated => Entry.Document.UpdatedAt.ToLocalTime().ToString("g");
        public string State { get; }
        public string ProjectPath => Entry.DirectoryPath;
    }

    private sealed class GuidedStaticMeshModRow : GuidedAssetRow
    {
        public GuidedStaticMeshModRow(
            StaticMeshEditEntry entry,
            StaticMeshEditableExportManifest source,
            GuidedTexturePackageRevision? packageRevision,
            string? currentEditedGlbSha256,
            ModDeploymentInspection? deploymentInspection)
        {
            Entry = entry;
            Source = source;
            Included = entry.Manifest.IncludedInPackageBuild;
            var built = packageRevision?.StaticMeshAssets.FirstOrDefault(asset =>
                asset.EditId.Equals(entry.Manifest.EditId, StringComparison.OrdinalIgnoreCase));
            NeedsBuild = Included
                ? built is null || string.IsNullOrWhiteSpace(currentEditedGlbSha256) ||
                  !built.EditedGlbSha256.Equals(currentEditedGlbSha256, StringComparison.OrdinalIgnoreCase)
                : built is not null;
            var installed = deploymentInspection?.CanRestore == true ||
                            deploymentInspection?.State is ModInstallationState.Installed or
                                ModInstallationState.Prepared;
            State = !File.Exists(entry.Manifest.EditedGlbPath)
                ? "Missing edited GLB"
                : entry.Manifest.Preflight.Readiness == StaticMeshEditReadiness.Refused
                    ? "Refused — review diagnostics"
                    : installed && NeedsBuild
                        ? "Installed build outdated"
                        : installed && Included
                            ? "Installed"
                            : NeedsBuild
                                ? !Included ? "Excluded — rebuild to remove" :
                                    built is null ? "Editing — not built" : "Editing — changes not built"
                                : !Included ? "Excluded" : "Built — current";
        }

        public StaticMeshEditEntry Entry { get; }
        public override string EditId => Entry.Manifest.EditId;
        public StaticMeshEditableExportManifest Source { get; }
        public bool NeedsBuild { get; }
        public string DisplayName => Entry.Manifest.DisplayName;
        public string ObjectName => Source.ObjectName;
        public string ImportProfile => Entry.Manifest.Preflight.TopologyChanged
            ? "Variable-size topology" : "Same-topology geometry";
        public string Collision => Entry.Manifest.Preflight.TopologyChanged
            ? "Source collision retained — review fit" : "Source collision retained";
        public string State { get; }
        public string EditedGlbPath => Entry.Manifest.EditedGlbPath;
    }

    private sealed record GuidedModelPreviewCandidate(
        TextureModelReference Reference,
        PackageIndexMatch Match)
    {
        public string Label =>
            $"{Reference.DisplayName}" +
            (string.IsNullOrWhiteSpace(Reference.Archetype) ? string.Empty : $" · {GameClassNames.Label(Reference.Archetype)}") +
            $" — {Match.PackageAlias}.{Match.ObjectName} ({Match.Kind})";
    }

    private sealed record GuidedDeploymentRevisionState(
        GuidedTexturePackageRevision Revision,
        ModDeploymentInspection? Inspection,
        string? Error);

    private sealed class GuidedTexturePackageRow
    {
        public GuidedTexturePackageRow(
            GuidedTexturePackageEntry entry,
            IReadOnlyList<GuidedTextureModRow> assets,
            IReadOnlyList<GuidedMeshModRow> meshAssets,
            IReadOnlyList<GuidedStaticMeshModRow> staticMeshAssets,
            IReadOnlyList<GuidedMaterialModRow> materialAssets,
            GuidedTexturePackageRevision? deploymentRevision,
            ModDeploymentInspection? inspection,
            string? inspectionError)
        {
            Entry = entry;
            Assets = assets;
            MeshAssets = meshAssets;
            StaticMeshAssets = staticMeshAssets;
            MaterialAssets = materialAssets;
            DeploymentRevision = deploymentRevision;
            Inspection = inspection;
            InspectionError = inspectionError;
        }

        public GuidedTexturePackageEntry Entry { get; }

        public IReadOnlyList<GuidedTextureModRow> Assets { get; }

        public IReadOnlyList<GuidedMeshModRow> MeshAssets { get; }

        public IReadOnlyList<GuidedStaticMeshModRow> StaticMeshAssets { get; }

        public IReadOnlyList<GuidedMaterialModRow> MaterialAssets { get; }

        public IEnumerable<GuidedAssetRow> AllAssets =>
            Assets.Concat<GuidedAssetRow>(MaterialAssets).Concat(MeshAssets).Concat(StaticMeshAssets);

        public GuidedTexturePackageRevision? DeploymentRevision { get; }

        public ModDeploymentInspection? Inspection { get; }

        public string? InspectionError { get; }

        public GuidedTexturePackageRevision? LatestRevision =>
            Entry.Manifest.LatestRevisionId is null
                ? Entry.Manifest.Revisions.LastOrDefault()
                : Entry.Manifest.Revisions.FirstOrDefault(revision => revision.RevisionId.Equals(
                    Entry.Manifest.LatestRevisionId, StringComparison.OrdinalIgnoreCase));

        public string PackageAlias => Entry.Manifest.PackageAlias;

        public string DisplayName => string.IsNullOrWhiteSpace(Entry.Manifest.DisplayName)
            ? $"{Entry.Manifest.PackageAlias} texture pack"
            : Entry.Manifest.DisplayName!;

        public string Favorite => Entry.Manifest.IsFavorite ? "★" : string.Empty;

        public string PhysicalPackageName => Entry.Manifest.PhysicalPackageName;

        public int IncludedTextureCount => Assets.Count(asset => asset.Included);

        public int IncludedMeshCount => MeshAssets.Count(asset => asset.Included);

        public int IncludedMaterialCount => MaterialAssets.Count(asset => asset.Included);

        public int IncludedStaticMeshCount => StaticMeshAssets.Count(asset => asset.Included);

        public int IncludedAssetCount => IncludedTextureCount + IncludedMaterialCount +
                                         IncludedMeshCount + IncludedStaticMeshCount;

        public bool BuildOutdated
        {
            get
            {
                if (Assets.Any(asset => asset.NeedsBuild) || MaterialAssets.Any(asset => asset.NeedsBuild) ||
                    MeshAssets.Any(asset => asset.NeedsBuild) ||
                    StaticMeshAssets.Any(asset => asset.NeedsBuild)) return true;
                if (LatestRevision is null) return IncludedAssetCount > 0;
                if (!string.IsNullOrWhiteSpace(Entry.Manifest.ClientBaselineSha256) &&
                    !LatestRevision.SourcePackageSha256.Equals(
                        Entry.Manifest.ClientBaselineSha256, StringComparison.OrdinalIgnoreCase)) return true;
                var projectIds = Assets.Select(asset => asset.Entry.Manifest.ProjectId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var editIds = MeshAssets.Select(asset => asset.Entry.Manifest.EditId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var materialIds = MaterialAssets.Select(asset => asset.Entry.Document.EditId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var staticMeshIds = StaticMeshAssets.Select(asset => asset.Entry.Manifest.EditId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return LatestRevision.Assets.Any(asset => !projectIds.Contains(asset.ProjectId)) ||
                       LatestRevision.MeshAssets.Any(asset => !editIds.Contains(asset.EditId)) ||
                       LatestRevision.StaticMeshAssets.Any(asset => !staticMeshIds.Contains(asset.EditId)) ||
                       LatestRevision.MaterialAssets.Any(asset => !materialIds.Contains(asset.EditId));
            }
        }

        public string AssetSummary =>
            $"{IncludedTextureCount:N0}T + {IncludedMaterialCount:N0}I + {IncludedMeshCount:N0}Sk + {IncludedStaticMeshCount:N0}St / " +
            $"{Assets.Count + MaterialAssets.Count + MeshAssets.Count + StaticMeshAssets.Count:N0}";

        public string State => LatestRevision is null
            ? "Not built"
            : !string.IsNullOrWhiteSpace(InspectionError)
                ? "Attention required"
                : LatestRevision.PreviewOnly && !BuildOutdated
                    ? "Built — preview only"
                : Inspection?.State is ModInstallationState.Installed or ModInstallationState.Prepared
                    ? DeploymentRevision is not null && LatestRevision is not null &&
                      !DeploymentRevision.RevisionId.Equals(
                          LatestRevision.RevisionId, StringComparison.OrdinalIgnoreCase)
                        ? "Installed — earlier revision"
                        : "Installed"
                    : DeploymentRevision is not null && LatestRevision is not null &&
                      !DeploymentRevision.RevisionId.Equals(
                          LatestRevision.RevisionId, StringComparison.OrdinalIgnoreCase) &&
                      Inspection?.State is ModInstallationState.NotInstalled or ModInstallationState.Restored
                        ? "Clean original restored — rebuild"
                    : Inspection?.State == ModInstallationState.Conflict
                        ? "Conflict"
                        : BuildOutdated
                            ? "Build outdated"
                            : LatestRevision?.ContainsVariableSizeMeshReplacements == true &&
                              LatestRevision.VariableSizeDeploymentApproved &&
                              Inspection?.CanInstall == true
                                ? "Built — topology install ready"
                            : Inspection?.CanInstall == true
                                ? "Built — ready to install"
                                : "Built — verified";

        public string Conflict => !string.IsNullOrWhiteSpace(InspectionError)
            ? InspectionError!
            : Inspection?.State == ModInstallationState.Conflict
                ? Inspection.Summary
                : "—";

        public string ImpactSummary
        {
            get
            {
                var uses = Assets
                    .Where(asset => asset.Included)
                    .Select(asset => asset.ImpactNames)
                    .Where(value => !string.IsNullOrWhiteSpace(value) &&
                                    !value.Equals("Unmapped", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToArray();
                return uses.Length == 0
                    ? MeshAssets.Any(asset => asset.Included) || StaticMeshAssets.Any(asset => asset.Included)
                        ? string.Join(", ", MeshAssets.Where(asset => asset.Included)
                            .Select(asset => asset.ObjectName)
                            .Concat(StaticMeshAssets.Where(asset => asset.Included).Select(asset => asset.ObjectName))
                            .Take(8))
                        : "No verified in-game uses are mapped for the checked assets."
                    : string.Join(", ", uses);
            }
        }

        public string OutputUpk => LatestRevision?.OutputUpkPath ?? "Not built yet";

        public string PayloadSize => LatestRevision is null
            ? "-"
            : $"{LatestRevision.PayloadBytes / 1024d / 1024d:N2} MiB";
    }

}
