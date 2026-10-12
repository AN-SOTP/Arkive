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
    private sealed record ChunkRow(
        int Number,
        string LogicalOffset,
        string LogicalSize,
        string PhysicalOffset,
        string PhysicalSize,
        string Flags);

    private sealed record AssetRow(
        int ExportIndex,
        AssetKind Kind,
        string ClassName,
        string ObjectPath,
        long SerialSize,
        long SerialOffset,
        IReadOnlyList<GameAssetAssociation> GameMetadata,
        UmodelAssetEntry? ExternalAsset)
    {
        public string Size => $"{SerialSize:N0}";

        public string Offset => $"0x{SerialOffset:X}";

        public string InGameNames => GameAssetMetadataIndex.FormatNames(GameMetadata);

        public string Capability => ExternalAsset?.IsExportable == true
            ? "Exportable"
            : ClassName.EndsWith("actor", StringComparison.OrdinalIgnoreCase) ||
              ClassName.EndsWith("component", StringComparison.OrdinalIgnoreCase)
                ? "Scene reference"
                : "Metadata only";
    }

    private sealed record TextureReconPropertyRow(TexturePropertyEvidence Evidence)
    {
        public string RelativeOffset => $"+0x{Evidence.RelativeOffset:X}";

        public string LogicalOffset => $"0x{Evidence.LogicalOffset:X}";

        public string Name => Evidence.Name;

        public string Type => Evidence.Type;

        public int Size => Evidence.Size;

        public int ArrayIndex => Evidence.ArrayIndex;

        public string TypeMetadata => Evidence.TypeMetadata ?? "-";

        public string Value => string.IsNullOrEmpty(Evidence.ValuePreview)
            ? Evidence.RawValuePrefix
            : Evidence.ValuePreview;
    }

    private sealed record TextureReconMipRow(
        TextureMipChainEvidence CandidateEvidence,
        TextureMipEvidence Evidence)
    {
        public int Candidate => CandidateEvidence.CandidateNumber;

        public TextureReconnaissanceConfidence Confidence => CandidateEvidence.Confidence;

        public int Score => CandidateEvidence.Score;

        public int Mip => Evidence.MipIndex;

        public string Dimensions => $"{Evidence.Width} x {Evidence.Height}";

        public string Storage => Evidence.Storage;

        public string Elements => $"{Evidence.ElementCount:N0}";

        public string Stored => $"{Evidence.StoredSize:N0}";

        public string Flags => $"0x{Evidence.BulkDataFlags:X8}";

        public string BulkOffset => $"0x{Evidence.BulkDataOffset:X}";

        public string PayloadLogical => Evidence.PayloadLogicalOffset is { } offset
            ? $"0x{offset:X}"
            : "-";

        public bool OffsetMatches => Evidence.BulkOffsetMatchesPayload;

        public bool FormatMatches => Evidence.StoredSizeMatchesFormat;

        public string PayloadHash => Evidence.PayloadSha256 ?? "-";
    }

    private sealed record TextureReconStorageRow(TextureStorageEvidence Evidence)
    {
        public string StorageKind => Evidence.StorageKind;

        public string Chunk => Evidence.ChunkNumber?.ToString() ?? "-";

        public string LogicalRange => $"0x{Evidence.LogicalOffset:X}+0x{Evidence.LogicalSize:X}";

        public string PhysicalRange => $"0x{Evidence.PhysicalOffset:X}+0x{Evidence.PhysicalSize:X}";

        public string Flags => Evidence.LostArkFlags is { } flags ? $"0x{flags:X8}" : "-";
    }

    private sealed record SkeletalMeshPropertyRow(SkeletalMeshPropertyEvidence Evidence)
    {
        public string RelativeOffset => $"+0x{Evidence.RelativeOffset:X}";
        public string Name => Evidence.Name;
        public string Type => Evidence.Type;
        public int Size => Evidence.Size;
        public string Value => string.IsNullOrWhiteSpace(Evidence.ValuePreview)
            ? Evidence.RawValuePrefix
            : Evidence.ValuePreview;
    }

    private sealed record SkeletalMeshLodRow(
        SkeletalMeshLodEvidence LodEvidence,
        SkeletalMeshSectionEvidence? SectionEvidence)
    {
        public int Lod => LodEvidence.LodIndex;
        public string Vertices => LodEvidence.VertexCount.ToString("N0");
        public string Indices => LodEvidence.IndexCount.ToString("N0");
        public string Triangles => LodEvidence.TriangleCount.ToString("N0");
        public int UvSets => LodEvidence.TextureCoordinateCount;
        public string Section => SectionEvidence?.SectionIndex.ToString() ?? "-";
        public string FirstIndex => SectionEvidence?.FirstIndex.ToString("N0") ?? "-";
        public string SectionTriangles => SectionEvidence?.TriangleCount.ToString("N0") ?? "-";
        public string Material => SectionEvidence?.Material ?? "-";
    }

    private sealed record SkeletalMeshNativeLodRow(SkeletalMeshNativeLodEvidence Evidence)
    {
        public int Lod => Evidence.LodIndex;
        public string Vertices => Evidence.VertexCount.ToString("N0");
        public string Indices => Evidence.IndexBuffer.ElementCount.ToString("N0");
        public int Sections => Evidence.Sections.Count;
        public int Chunks => Evidence.Chunks.Count;
        public int UvSets => Evidence.TextureCoordinateCount;
        public int VertexStride => Evidence.VertexBuffer.ElementSize;
        public int PositionOffset => Evidence.VertexPositionOffset;
        public int RequiredBoneWidth => Evidence.RequiredBoneElementSize;
        public string IndexRange => $"+0x{Evidence.IndexBuffer.DataRelativeOffset:X}+0x{Evidence.IndexBuffer.DataBytes:X}";
        public string VertexRange => $"+0x{Evidence.VertexBuffer.DataRelativeOffset:X}+0x{Evidence.VertexBuffer.DataBytes:X}";
        public string ColourRange => Evidence.VertexColorBuffer is { } color
            ? $"+0x{color.DataRelativeOffset:X}+0x{color.DataBytes:X}"
            : "absent";
    }

    private sealed record SkeletalMeshNativeRangeRow(SkeletalMeshNativeRangeEvidence Evidence)
    {
        public string RelativeRange => $"+0x{Evidence.RelativeOffset:X}+0x{Evidence.Size:X}";
        public bool Parsed => Evidence.IsParsed;
        public bool Editable => Evidence.IsEditableCandidate;
        public string Name => Evidence.Name;
        public string Sha256 => Evidence.Sha256;
    }

    private sealed record SkeletalMeshBoneRow(SkeletalMeshNativeBoneEvidence Evidence)
    {
        public int Index => Evidence.BoneIndex;
        public string Name => Evidence.Name;
        public int Parent => Evidence.ParentIndex;
        public int Children => Evidence.ChildCount;
        public string RelativeOffset => $"+0x{Evidence.RelativeOffset:X}";
        public string Sha256 => Evidence.RawSha256;
    }

    private sealed record SkeletalMeshDependencyRow(
        string Feature,
        string Asset,
        string State,
        string Count,
        string Preserving,
        string TopologyChanging,
        string Detail)
    {
        public static IReadOnlyList<SkeletalMeshDependencyRow> Build(
            SkeletalMeshDeformationDependencyReport? report)
        {
            if (report is null) return [];
            var rows = new List<SkeletalMeshDependencyRow>
            {
                new("Cloth", "Summary", report.ClothState.ToString(),
                    $"{report.ActiveClothReferenceCount:N0} active",
                    report.TopologyPreservingSupported ? "Preserved" : "Refused",
                    report.TopologyChangingSupported ? "Supported" : "Refused",
                    report.ClothBindings.Count == 0
                        ? "No serialized clothing property was found."
                        : "Clothing properties were decoded as UE3 object-reference arrays."),
            };
            rows.AddRange(report.ClothBindings.Select(binding => new SkeletalMeshDependencyRow(
                "Cloth", binding.PropertyName,
                binding.IsActive ? "Active binding" : "Inactive placeholder",
                $"{binding.DeclaredReferenceCount:N0} refs",
                binding.IsActive ? "Preserved" : "Not needed",
                binding.IsActive ? "Refused" : "Supported",
                binding.Detail + (binding.ResolvedObjects.Count == 0
                    ? string.Empty
                    : " " + string.Join("; ", binding.ResolvedObjects)))));
            rows.Add(new("Morph", "Summary", report.MorphState.ToString(),
                $"{report.MorphTargetCount:N0} / {report.MorphDeltaCount:N0}",
                report.TopologyPreservingSupported ? "Preserved" : "Refused",
                report.TopologyChangingSupported ? "Supported" : "Refused",
                report.MorphTargetSetCount == 0
                    ? "No exact linked MorphTargetSet was found."
                    : "Morph sets, target exports, LOD arrays, delta counts, base vertex counts and source indices were decoded."));
            foreach (var set in report.MorphSets)
            {
                rows.Add(new("Morph set", set.ObjectName,
                    set.BaseSkeletalMeshMatches ? "Linked" : "Base mismatch",
                    $"{set.ResolvedTargetCount:N0} targets",
                    "Preserved", "Refused",
                    $"Export {set.ExportIndex}; BaseSkelMesh={set.BaseSkeletalMeshReference?.ToString() ?? "unresolved"}; " +
                    $"declared={set.DeclaredTargetCount:N0}."));
                rows.AddRange(set.Targets.Select(target => new SkeletalMeshDependencyRow(
                    "Morph target", target.ObjectName,
                    target.Parsed ? "Decoded" : "Unresolved",
                    $"{target.DeltaCount:N0} deltas",
                    target.Parsed ? "Preserved" : "Refused",
                    target.DeltaCount == 0 && target.Parsed ? "No mapping" : "Refused",
                    target.Parsed
                        ? string.Join("; ", target.Lods.Select(lod =>
                            $"LOD{lod.LodIndex}: {lod.DeltaCount:N0} deltas / {lod.BaseMeshVertexCount:N0} base verts / max {lod.MaximumSourceVertexIndex:N0}"))
                        : target.Detail)));
            }
            return rows;
        }
    }

    private sealed record SkeletalMeshEditableExportRow(SkeletalMeshEditableWorkspaceEntry Entry)
    {
        public string Created => Entry.Manifest.CreatedAt.LocalDateTime.ToString("g");
        public string PackageAlias => Entry.Manifest.PackageAlias;
        public string ObjectName => Entry.Manifest.ObjectName;
        public int LodCount => Entry.Manifest.Lods.Count;
        public int BoneCount => Entry.Manifest.BoneCount;
        public string VertexCount => Entry.Manifest.Lods.Sum(lod => lod.VertexCount).ToString("N0");
        public string Size => Entry.GlbExists
            ? $"{new FileInfo(Entry.GlbPath).Length / 1024d / 1024d:N2} MiB"
            : "-";
        public string State => !Entry.GlbExists
            ? "Missing GLB"
            : !Entry.GlbLengthMatches
                ? "Size mismatch"
                : !Entry.IsCurrentSchema
                    ? $"Legacy schema {Entry.Manifest.SchemaVersion}"
                    : Entry.Manifest.GlbInspectionErrors > 0
                        ? $"{Entry.Manifest.GlbInspectionErrors} error(s)"
                        : Entry.Manifest.GlbInspectionWarnings > 0
                            ? $"Ready — {Entry.Manifest.GlbInspectionWarnings} warning(s)"
                            : "Ready";
        public string GlbPath => Entry.GlbPath;
    }

    private sealed record StaticMeshLodRow(StaticMeshLodEvidence Evidence)
    {
        public int Lod => Evidence.LodIndex;
        public string Vertices => Evidence.VertexCount.ToString("N0");
        public string Indices => Evidence.Indices.ElementCount.ToString("N0");
        public string Triangles => (Evidence.Indices.ElementCount / 3).ToString("N0");
        public int Sections => Evidence.Sections.Count;
        public int UvSets => Evidence.TextureCoordinateCount;
        public string Colours => Evidence.Colours is null ? "No" : "Yes";
        public string NativeBytes => Evidence.SerializedBytes.ToString("N0");
        public string PositionSha => Evidence.Positions.DataSha256;
    }

    private sealed record StaticMeshSectionRow(int LodIndex, StaticMeshSectionEvidence Evidence)
    {
        public int Lod => LodIndex;
        public int Section => Evidence.SectionIndex;
        public string Material => Evidence.MaterialName;
        public string FirstIndex => Evidence.FirstIndex.ToString("N0");
        public string Triangles => Evidence.TriangleCount.ToString("N0");
        public string VertexRange => $"{Evidence.MinimumVertexIndex:N0}–{Evidence.MaximumVertexIndex:N0}";
        public string Shadow => Evidence.ShadowCasting == 0 ? "No" : "Yes";
    }

    private sealed record StaticMeshEditableExportRow(
        string ManifestPath,
        string GlbPath,
        StaticMeshEditableExportManifest Manifest)
    {
        public string Created => Manifest.CreatedAt.ToLocalTime().ToString("g");
        public string Package => Manifest.PackageAlias;
        public string ObjectName => Manifest.ObjectName;
        public int Lods => Manifest.Lods.Count;
        public string Geometry =>
            $"{Manifest.Lods.Sum(lod => lod.VertexCount):N0} verts / " +
            $"{Manifest.Lods.Sum(lod => lod.TriangleCount):N0} tris";
    }

    private sealed record StaticMeshEditRow(StaticMeshEditEntry Entry)
    {
        public string Created => Entry.Manifest.CreatedAt.ToLocalTime().ToString("g");
        public string DisplayName => Entry.Manifest.DisplayName;
        public string ObjectName
        {
            get
            {
                try
                {
                    return StaticMeshEditableExportService.LoadManifestAsync(
                        Entry.Manifest.SourceEditableManifestPath).GetAwaiter().GetResult().ObjectName;
                }
                catch
                {
                    return Path.GetFileNameWithoutExtension(Entry.Manifest.EditedGlbPath);
                }
            }
        }
        public string Route => Entry.Manifest.Preflight.Readiness switch
        {
            StaticMeshEditReadiness.ReadyForTopologyCompilation => "Variable-size topology",
            StaticMeshEditReadiness.ReadyForSameTopologyCompilation => "Same-topology geometry",
            _ => "Refused",
        };
        public string Collision => Entry.Manifest.Preflight.CollisionWillRemainFromSource
            ? Entry.Manifest.Preflight.TopologyChanged ? "Retained — review fit" : "Retained unchanged"
            : "Unverified";
        public string Compilation => !string.IsNullOrWhiteSpace(Entry.Manifest.CompilationManifestPath) &&
                                     File.Exists(Entry.Manifest.CompilationManifestPath)
            ? "Decode-back passed" : "Not compiled";
        public string Staged => !string.IsNullOrWhiteSpace(Entry.Manifest.StagedUpkManifestPath) &&
                                File.Exists(Entry.Manifest.StagedUpkManifestPath)
            ? "Ready" : "Not built";
        public string Diagnostics => Entry.Manifest.Preflight.Issues.Count == 0
            ? "No preflight issues"
            : string.Join(" | ", Entry.Manifest.Preflight.Issues);
    }

    private sealed record SkeletalMeshEditedGlbRow(SkeletalMeshEditWorkspaceEntry Entry)
    {
        public string Created => Entry.Manifest.CreatedAt.LocalDateTime.ToString("g");
        public string DisplayName => Entry.Manifest.DisplayName;
        public string Readiness => Entry.Manifest.Preflight.Readiness switch
        {
            SkeletalMeshEditReadiness.ReadyForPositionTangentEncoding => "Ready for native encoding",
            SkeletalMeshEditReadiness.ReadyForTopologyCompilation => "Topology preflight passed",
            SkeletalMeshEditReadiness.ReadyNoChanges => "Ready — no changes",
            _ => "Refused — review diagnostics",
        };
        public string ImportProfile => Entry.Manifest.Preflight.ImportProfile switch
        {
            SkeletalMeshImportProfile.ReplaceLod0AndDuplicate => "Replace LOD0 topology",
            _ => "Preserve source topology",
        };
        public string AnimationSafety => Entry.Manifest.Preflight.AnimationValidation is { } animation
            ? animation.Passed
                ? animation.StaticOnly
                    ? "Bind-space proof passed"
                    : $"Passed — {animation.PoseCount:N0} stress poses"
                : "Refused — animation risk"
            : "Not checked — revalidate";
        public string SkeletonCompatibility => Entry.Manifest.Preflight.SkeletonCompatibility is { } skeleton
            ? skeleton.Compatibility switch
            {
                SkeletalMeshSkeletonCompatibilityKind.ExactTargetSkeleton => "Exact target skeleton",
                SkeletalMeshSkeletonCompatibilityKind.SameNamedHierarchyAlteredBindPose => "Altered bind pose — refused",
                SkeletalMeshSkeletonCompatibilityKind.RetargetAuthoringCandidate => "Retarget authoring required",
                SkeletalMeshSkeletonCompatibilityKind.ArtistRiggingRequired => "Artist re-rig required",
                _ => "Incompatible — refused",
            }
            : "Not checked — revalidate";
        public string DeformationDependencies => Entry.Manifest.Preflight.DeformationDependencies is { } dependencies
            ? dependencies.IsResolved
                ? dependencies.HasActiveClothData || dependencies.HasActiveMorphTargets
                    ? Entry.Manifest.Preflight.ImportProfile == SkeletalMeshImportProfile.TopologyPreserving
                        ? $"Preserved — {dependencies.ActiveClothReferenceCount:N0} cloth / {dependencies.MorphTargetCount:N0} morph"
                        : "Active mapping — refused"
                    : "No active mappings"
                : "Unresolved — refused"
            : "Not checked — revalidate";
        public string LodPlan => Entry.Manifest.Preflight.Topology is { } topology
            ? $"LOD0 → {topology.TargetLods.Count:N0} native LOD(s)"
            : "Retain every source LOD";
        public string CandidateGeometry => Entry.Manifest.Preflight.Topology is { } topology
            ? $"{topology.CandidateVertexCount:N0} v / {topology.CandidateTriangleCount:N0} t"
            : Entry.Manifest.Preflight.Lods.Count == 0
                ? "—"
                : $"{Entry.Manifest.Preflight.Lods.Sum(lod => lod.CandidateVertexCount):N0} v / " +
                  $"{Entry.Manifest.Preflight.Lods.Sum(lod => lod.CandidateTriangleCount):N0} t";
        public string ChangedPositions => Entry.Manifest.Preflight.ChangedPositionVertexCount.ToString("N0");
        public string MaximumDelta => $"{Entry.Manifest.Preflight.MaximumPositionDeltaMetres * 100d:N2} cm";
        public int Errors => Entry.Manifest.Preflight.ErrorCount;
        public int Warnings => Entry.Manifest.Preflight.WarningCount;
        public string NativeEncodingDirectory => Path.Combine(Entry.DirectoryPath, "native-encoding");
        public string NativeEncodingManifestPath => Path.Combine(
            NativeEncodingDirectory, "arkive-skeletalmesh-native-encoding.json");
        public string NativeEncodingPayloadPath => Path.Combine(
            NativeEncodingDirectory, Entry.Manifest.ObjectName + ".skeletalmesh.edit.bin");
        public string NativeEncodingProofGlbPath => Path.Combine(
            NativeEncodingDirectory, Entry.Manifest.ObjectName + ".native-decode-proof.glb");
        public string TopologyCompilationDirectory => Path.Combine(
            Entry.DirectoryPath, "topology-compilation");
        public string TopologyCompilationManifestPath => Path.Combine(
            TopologyCompilationDirectory, SkeletalMeshTopologyCompilerService.ManifestFileName);
        public string TopologyCompilationPayloadPath => Path.Combine(
            TopologyCompilationDirectory, Entry.Manifest.ObjectName + ".skeletalmesh.topology.bin");
        public string TopologyCompilationProofGlbPath => Path.Combine(
            TopologyCompilationDirectory, Entry.Manifest.ObjectName + ".topology-decode-proof.glb");
        public string StagedUpkDirectory => Path.Combine(Entry.DirectoryPath, "staged-upk");
        public string StagedUpkManifestPath => Path.Combine(
            StagedUpkDirectory, SkeletalMeshStagedUpkService.ManifestFileName);
        public bool StagedUpkOutputExists => Directory.Exists(StagedUpkDirectory) &&
                                             Directory.EnumerateFiles(
                                                 StagedUpkDirectory, "*.upk", SearchOption.TopDirectoryOnly)
                                                 .Any();
        public string TopologyStagedUpkDirectory => Path.Combine(
            Entry.DirectoryPath, "topology-staged-upk");
        public string TopologyStagedUpkManifestPath => Path.Combine(
            TopologyStagedUpkDirectory, SkeletalMeshStagedUpkService.TopologyManifestFileName);
        public bool TopologyStagedUpkOutputExists => Directory.Exists(TopologyStagedUpkDirectory) &&
                                                     Directory.EnumerateFiles(
                                                         TopologyStagedUpkDirectory, "*.upk",
                                                         SearchOption.TopDirectoryOnly).Any();
        public string NativeCandidateState => File.Exists(NativeEncodingManifestPath) &&
                                              File.Exists(NativeEncodingPayloadPath) &&
                                              File.Exists(NativeEncodingProofGlbPath)
            ? "Verified"
            : "Not built";
        public string TopologyProofState => File.Exists(TopologyCompilationManifestPath) &&
                                            File.Exists(TopologyCompilationPayloadPath) &&
                                            File.Exists(TopologyCompilationProofGlbPath)
            ? "Verified"
            : Entry.Manifest.Preflight.Readiness == SkeletalMeshEditReadiness.ReadyForTopologyCompilation
                ? "Ready to compile"
                : "Not applicable";
        public string StagedUpkState =>
            File.Exists(TopologyStagedUpkManifestPath) && TopologyStagedUpkOutputExists
                ? "Topology preview ready"
                : File.Exists(StagedUpkManifestPath) && StagedUpkOutputExists
                    ? "Preview ready"
                    : "Not built";
        public string MeshDeploymentDirectory => Path.Combine(Entry.DirectoryPath, "deployment-bundle");
        public string MeshBundleManifestPath => Path.Combine(
            MeshDeploymentDirectory, ArkiveModBundleService.BundleManifestFileName);
        public string DeploymentBundleState => File.Exists(MeshBundleManifestPath)
            ? "Prepared"
            : "Not prepared";
        public string Diagnostics
        {
            get
            {
                var significant = Entry.Manifest.Preflight.Issues
                    .Where(issue => issue.Severity != SkeletalMeshEditIssueSeverity.Info)
                    .Take(3)
                    .Select(issue => $"{issue.Code}: {issue.Message}")
                    .ToArray();
                return significant.Length == 0
                    ? "All protected invariants match."
                    : string.Join(" | ", significant);
            }
        }
        public string GlbPath => Entry.EditedGlbPath;
    }

    private sealed record ModifiedMeshUpkRow(
        SkeletalMeshEditWorkspaceEntry Entry,
        string BundleManifestPath,
        ArkiveModBundleManifest? Bundle,
        ModDeploymentInspection? Inspection,
        string? Error)
    {
        public string DisplayName => Bundle?.DisplayName ?? Entry.Manifest.DisplayName;
        public string PackageAlias => Entry.Manifest.PackageAlias;
        public string PhysicalPackageName => Bundle?.SourcePhysicalPackageName ?? "Unknown";
        public string ObjectName => Entry.Manifest.ObjectName;
        public string State => Error is not null
            ? "Attention required"
            : Inspection?.State switch
            {
                ModInstallationState.NotInstalled => "Built — ready to install",
                ModInstallationState.Restored => "Restored — ready to install",
                ModInstallationState.Installed => "Installed",
                ModInstallationState.Prepared => "Install interrupted",
                ModInstallationState.Conflict => "Conflict",
                ModInstallationState.MissingTarget => "Source missing",
                _ => "Prepared",
            };
        public string BackupState => Inspection is null
            ? "Unknown"
            : !Inspection.BackupExists
                ? "On install"
                : Inspection.BackupVerified ? "Verified" : "Invalid";
        public string Status => Error ?? Inspection?.Summary ??
            "The bundle exists but deployment status has not been inspected.";
        public string OutputUpk => Inspection?.PayloadPath ??
                                   (Bundle is null
                                       ? "Unavailable"
                                       : Path.Combine(Path.GetDirectoryName(BundleManifestPath)!,
                                           Bundle.PayloadRelativePath));
        public string PayloadSize => Bundle is null
            ? "-"
            : FormatByteSize(Bundle.PayloadBytes);
    }

}
