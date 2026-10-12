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
    private void UpdateExportWorkspaceActionState()
    {
        var row = ExportWorkspaceGrid?.SelectedItem as ExportWorkspaceRow;
        var selected = row is not null;
        if (ExportWorkspaceOpenButton is not null)
        {
            ExportWorkspaceOpenButton.IsEnabled = selected;
            ExportWorkspaceCopyPathButton.IsEnabled = selected;
            ExportWorkspaceInspectButton.IsEnabled = selected && _umodelBridge is not null;
        }

        if (CreateTextureImportCandidateButton is not null)
        {
            var nativeManifest = row?.Entry.Manifest.NativeDetailsManifestPath;
            var isNativeTextureExport = row is not null &&
                                        !string.IsNullOrWhiteSpace(nativeManifest) &&
                                        row.Entry.Manifest.TextureImportCandidateManifestPath is null &&
                                        row.Entry.Manifest.TextureLayoutConfirmationManifestPath is null &&
                                        row.Entry.Manifest.TextureReplacementPayloadManifestPath is null;
            var canPrepare = !_textureImportCandidateInProgress &&
                             !_textureReplacementInProgress &&
                             isNativeTextureExport &&
                             !string.IsNullOrWhiteSpace(nativeManifest) &&
                             File.Exists(nativeManifest);
            CreateTextureImportCandidateButton.IsEnabled = canPrepare;
            TextureImportAlphaPolicyComboBox.IsEnabled = canPrepare;
            OpenEditableTextureButton.IsEnabled = canPrepare &&
                                                   FindEditableTexturePath(row!) is not null;
            TextureImportCandidateSummaryText.Text = row is null
                ? "Select a native texture export, edit its visible image, then import it without DDS settings."
                : canPrepare
                    ? "Open the editable image, paint normally, save it, then import it—Arkive restores alpha, compression and mips."
                    : row.Entry.Manifest.TextureImportCandidateManifestPath is not null
                        ? "This row is an import candidate; select its parent native export to prepare another."
                        : "The selected session is not an Arkive native texture export.";
        }

        if (VerifyTextureLayoutButton is not null)
        {
            var manifest = row?.Entry.Manifest;
            var nativePath = manifest?.NativeDetailsManifestPath;
            var isNativeTextureExport = manifest is not null &&
                                        !string.IsNullOrWhiteSpace(nativePath) &&
                                        manifest.TextureImportCandidateManifestPath is null &&
                                        manifest.TextureLayoutConfirmationManifestPath is null &&
                                        manifest.TextureReplacementPayloadManifestPath is null;
            var isCandidate = manifest?.TextureImportCandidateManifestPath is not null &&
                              manifest.TextureReplacementPayloadManifestPath is null;
            var isPayloadSet = manifest?.TextureReplacementPayloadManifestPath is not null &&
                               manifest.TextureExportAssemblyManifestPath is null;
            var isExportAssembly = manifest?.TextureExportAssemblyManifestPath is not null &&
                                   manifest.PackageRewritePlanManifestPath is null;
            var isRewritePlan = manifest?.PackageRewritePlanManifestPath is not null &&
                                manifest.LogicalPackageImageManifestPath is null;
            var isLogicalImage = manifest?.LogicalPackageImageManifestPath is not null &&
                                 manifest.PhysicalPackagePlanManifestPath is null;
            var isPhysicalPlan = manifest?.PhysicalPackagePlanManifestPath is not null &&
                                 manifest.NoOpUpkReconstructionManifestPath is null;
            var isNoOpProof = manifest?.NoOpUpkReconstructionManifestPath is not null &&
                              manifest.ReplacementUpkBuildManifestPath is null;
            var isReplacementUpk = manifest?.ReplacementUpkBuildManifestPath is not null &&
                                   manifest.ModBundleManifestPath is null;
            var canVerify = !_textureReplacementInProgress &&
                            _textureLayoutConfirmationService is not null &&
                            isNativeTextureExport && File.Exists(nativePath);
            var canBuild = !_textureReplacementInProgress &&
                           _textureReplacementPayloadService is not null &&
                           isCandidate &&
                           File.Exists(manifest!.TextureImportCandidateManifestPath!);
            var canAssemble = !_textureReplacementInProgress &&
                              _textureExportAssemblyService is not null &&
                              isPayloadSet &&
                              File.Exists(manifest!.TextureReplacementPayloadManifestPath!);
            var canPlan = !_textureReplacementInProgress &&
                          _packageRewritePlanService is not null &&
                          isExportAssembly &&
                          File.Exists(manifest!.TextureExportAssemblyManifestPath!);
            var canBuildLogicalImage = !_textureReplacementInProgress &&
                                       _logicalPackageImageService is not null &&
                                       isRewritePlan &&
                                       File.Exists(manifest!.PackageRewritePlanManifestPath!);
            var canPlanPhysical = !_textureReplacementInProgress &&
                                  _physicalPackagePlanService is not null &&
                                  isLogicalImage &&
                                  File.Exists(manifest!.LogicalPackageImageManifestPath!);
            var canReconstructNoOp = !_textureReplacementInProgress &&
                                     _noOpUpkReconstructionService is not null &&
                                     isPhysicalPlan &&
                                     File.Exists(manifest!.PhysicalPackagePlanManifestPath!);
            var canBuildReplacementUpk = !_textureReplacementInProgress &&
                                         _replacementUpkBuildService is not null &&
                                         isNoOpProof &&
                                         File.Exists(manifest!.NoOpUpkReconstructionManifestPath!);
            var canPrepareModBundle = !_textureReplacementInProgress &&
                                      _modBundleService is not null &&
                                      isReplacementUpk &&
                                      File.Exists(manifest!.ReplacementUpkBuildManifestPath!);
            VerifyTextureLayoutButton.IsEnabled = canVerify;
            BuildTexturePayloadsButton.IsEnabled = canBuild;
            AssembleTextureExportButton.IsEnabled = canAssemble;
            PlanPackageRewriteButton.IsEnabled = canPlan;
            BuildLogicalPackageImageButton.IsEnabled = canBuildLogicalImage;
            PlanPhysicalPackageButton.IsEnabled = canPlanPhysical;
            ReconstructNoOpUpkButton.IsEnabled = canReconstructNoOp;
            BuildReplacementUpkButton.IsEnabled = canBuildReplacementUpk;
            PrepareModBundleButton.IsEnabled = canPrepareModBundle;
            TextureReplacementSummaryText.Text = row is null
                ? "Verify a native export's layout, then select an import candidate to build replacement-ready mip payloads."
                : canVerify
                    ? "Re-read the source and prove every mip boundary, codec header and decoded DDS level."
                    : canBuild
                        ? "Build isolated Unity CRN / UE3-LZ4 mip payloads and decode every result back for verification."
                        : canAssemble
                            ? "Reconstruct a byte-identical no-op export and a validated replacement Texture2D blob."
                            : canPlan
                                ? "Calculate the append-and-retarget package rewrite without writing any package bytes."
                            : canBuildLogicalImage
                                ? "Build and byte-verify a separate uncompressed logical staging image from this plan."
                            : canPlanPhysical
                                ? "Inventory physical chunks, compression blocks, protection, copy regions, and replacement impacts."
                            : canReconstructNoOp
                                ? "Reassemble a separate source-identical UPK and prove physical, logical, and catalogue equivalence."
                            : canBuildReplacementUpk
                                ? "Build the validated replacement-bearing UPK in Arkive staging; the installed game package remains read-only."
                            : canPrepareModBundle
                                ? "Create a portable mod bundle and rehearse its exact target, backup, rollback, and UModel checks without touching the game."
                        : manifest?.TextureReplacementPayloadManifestPath is not null
                            ? manifest.TextureExportAssemblyManifestPath is not null
                                ? manifest.PackageRewritePlanManifestPath is not null
                                    ? manifest.LogicalPackageImageManifestPath is not null
                                        ? manifest.PhysicalPackagePlanManifestPath is not null
                                            ? manifest.NoOpUpkReconstructionManifestPath is not null
                                                ? manifest.ReplacementUpkBuildManifestPath is not null
                                                    ? manifest.ModBundleManifestPath is not null
                                                        ? "The mod bundle passed its rehearsal. Select it, review Installed state, then use Install mod or Restore original."
                                                        : "The replacement-bearing staging UPK passed validation. Prepare its reversible mod bundle next."
                                                    : "The source-identical no-op UPK proof is complete; build the staged mod UPK next."
                                                : "The physical chunk plan is complete; reconstruct its no-op UPK next."
                                            : "The verified logical staging image is complete; plan its physical chunks next."
                                        : "The read-only package rewrite plan is complete; build its verified logical image next."
                                    : "This export assembly is complete; select it to create the package rewrite dry run."
                                : "This payload set is complete; assemble its standalone Texture2D export blob next."
                            : manifest?.TextureLayoutConfirmationManifestPath is not null
                                ? "This layout report is complete; select an import candidate to build native mip payloads."
                                : _textureReplacementUnavailableReason is not null
                                    ? $"Replacement workflow unavailable: {_textureReplacementUnavailableReason}"
                                    : "Select a native texture export or texture import candidate.";
        }
    }

    private static string? FindEditableTexturePath(ExportWorkspaceRow row)
    {
        var patterns = new[]
        {
            "*-edit.png",
            "*-rgb-view-opaque-alpha.dds",
        };
        foreach (var pattern in patterns)
        {
            var path = Directory
                .EnumerateFiles(row.Entry.OutputDirectory, pattern, SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (path is not null) return Path.GetFullPath(path);
        }

        return null;
    }

}
