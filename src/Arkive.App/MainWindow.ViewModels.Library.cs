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
    private sealed record LibraryAssetRow(
        string SourcePath,
        string PackageAlias,
        string PhysicalPackageName,
        int PackageAssetCount,
        int ExportIndex,
        AssetKind Kind,
        string ClassName,
        string ObjectName,
        long SerialSize,
        long SerialOffset,
        IReadOnlyList<GameAssetAssociation> GameMetadata,
        int Relevance = 0,
        string MatchReason = "")
    {
        public string Size => $"{SerialSize:N0}";

        public string InGameNames => GameAssetMetadataIndex.FormatNames(GameMetadata);

        public string LinkEvidence
        {
            get
            {
                if (GameMetadata.Any(item => item.Relation.Equals(
                        GameAssetAssociationRelation.VerifiedSerializedDependency,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return "Verified dependency";
                }

                if (GameMetadata.Any(item => item.Relation.Equals(
                        GameAssetAssociationRelation.DeterministicItemAsset,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return "Item mapping";
                }

                if (GameMetadata.Any(item => item.Relation.Equals(
                        GameAssetAssociationRelation.DeterministicModelAsset,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return "Model mapping";
                }

                return GameMetadata.Count == 0 ? "-" : "Direct DB";
            }
        }
    }

    private sealed record EntityBrowserEntityRow(GameEntityRecord Record)
    {
        public string DisplayName => Record.DisplayName;

        public string NameKey => Record.NameKey;

        public string Kind => Record.Kind == GameEntityKind.PlayableCharacter
            ? "Playable character"
            : "NPC";

        public string Category => Record.Category;

        public long EntityId => Record.EntityId;

        public string PrimaryAssetReference => Record.PrimaryAssetReference ?? "-";

        public string Detail => Record.Detail;
    }

    private sealed record EntityBrowserCustomizationRow(CharacterCustomizationOption Option)
    {
        public string Category => Option.Category;

        public int UiPosition => Option.UiPosition;

        public int ObjectUnit => Option.ObjectUnit;

        public string CandidateAssets => Option.CandidateObjectNames.Count == 0
            ? "-"
            : string.Join(", ", Option.CandidateObjectNames);

        public string Evidence => Option.Evidence;
    }

    private sealed record EntityBrowserAssetRow(EntityAssetMatch Match)
    {
        public string SourcePath => Match.Asset.SourcePath;

        public string PackageAlias => Match.Asset.PackageAlias;

        public string PhysicalPackageName => Match.Asset.PhysicalPackageName;

        public int ExportIndex => Match.Asset.ExportIndex;

        public AssetKind Kind => Match.Asset.Kind;

        public string ClassName => Match.Asset.ClassName;

        public string ObjectName => Match.Asset.ObjectName;

        public string Evidence => Match.Evidence switch
        {
            EntityAssetEvidence.DirectDatabaseReference => "Direct DB reference",
            EntityAssetEvidence.DatabaseAssetFamily => "DB asset family",
            EntityAssetEvidence.AppearancePackageFamily => "Appearance package",
            EntityAssetEvidence.CustomizationCandidate => "Customization candidate",
            _ => Match.Evidence.ToString(),
        };

        public string Confidence => Match.Confidence.ToString();

        public string Reason => Match.Reason;

        public UmodelAssetEntry ExternalAsset => new(
            Match.Asset.ExportIndex,
            Match.Asset.SerialOffset,
            Match.Asset.SerialSize,
            Match.Asset.ClassName,
            Match.Asset.ObjectName);
    }

    private sealed record RelationshipEntityRow(GameAssetAssociation Association)
    {
        public string DisplayName => Association.DisplayName;

        public string Entity => $"{Association.EntityType} {Association.EntityId}";

        public string Field => Association.Field;

        public string AssetReference => Association.AssetReference;
    }

    private sealed record RelationshipAssetRow(
        AssetRelationshipMatch Match,
        int ExportCount)
    {
        public string SourcePath => Match.Asset.SourcePath;

        public string PackageAlias => Match.Asset.PackageAlias;

        public string PhysicalPackageName => Match.Asset.PhysicalPackageName;

        public int ExportIndex => Match.Asset.ExportIndex;

        public AssetKind Kind => Match.Asset.Kind;

        public string ClassName => Match.Asset.ClassName;

        public string ObjectName => Match.Asset.ObjectName;

        public string Evidence => Match.Evidence switch
        {
            AssetRelationshipEvidence.DirectDatabaseReference => "Direct DB reference",
            AssetRelationshipEvidence.DatabaseAssetFamily => "DB asset family",
            AssetRelationshipEvidence.SamePackage => "Same package",
            AssetRelationshipEvidence.PackageFamily => "Package family",
            AssetRelationshipEvidence.ObjectNameFamily => "Object-name family",
            _ => "Selected seed",
        };

        public string Confidence => Match.Confidence.ToString();

        public string Reason => Match.Reason;

        public string InGameNames => GameAssetMetadataIndex.FormatNames(Match.Asset.GameMetadata);

        public string Exported => ExportCount == 0 ? "No" : $"Yes ({ExportCount:N0})";

        public UmodelAssetEntry ExternalAsset => new(
            Match.Asset.ExportIndex,
            Match.Asset.SerialOffset,
            Match.Asset.SerialSize,
            Match.Asset.ClassName,
            Match.Asset.ObjectName);
    }

    private sealed record ClientUpdatePackageAuditRow(ClientPackageCompatibilityReport Report)
    {
        public string PackageAlias => Report.PackageAlias;

        public string PhysicalPackageName => Report.PhysicalPackageName;

        public string State => Report.State switch
        {
            ClientPackageCompatibilityState.ExactSource => "Exact clean source",
            ClientPackageCompatibilityState.InstalledPayload => "Restore required",
            ClientPackageCompatibilityState.SafeAutomaticRebase => "Safe rebase",
            ClientPackageCompatibilityState.ReviewRequired => "Review required",
            ClientPackageCompatibilityState.MissingPackage => "Package missing",
            ClientPackageCompatibilityState.InvalidPackage => "Format unsupported",
            _ => Report.State.ToString(),
        };

        public string FormatState => Report.FormatProbe?.State.ToString() ?? "Not available";

        public string AssetSummary => Report.Assets.Count == 0
            ? Report.State == ClientPackageCompatibilityState.ExactSource ? "Hash-identical" : "No evidence"
            : $"{Report.Assets.Count(asset => asset.State == ClientAssetCompatibilityState.Unchanged):N0} unchanged / " +
              $"{Report.Assets.Count(asset => asset.State != ClientAssetCompatibilityState.Unchanged):N0} review";

        public string ChunkSummary => Report.FormatProbe is not { } probe
            ? "-"
            : $"{probe.DecodedChunkCount:N0} / {probe.CompressedChunkCount:N0}";

        public string Summary => Report.Summary;
    }

    private sealed record ClientUpdateAssetAuditRow(ClientAssetCompatibility Asset)
    {
        public string AssetKind => Asset.AssetKind;
        public string ObjectName => Asset.ObjectName;
        public ClientAssetCompatibilityState State => Asset.State;
        public string PreviousExport => Asset.PreviousNativeExportIndex?.ToString("N0") ?? "-";
        public string CurrentExport => Asset.CurrentNativeExportIndex?.ToString("N0") ?? "-";
        public string ExpectedHash => ShortHash(Asset.ExpectedExportSha256);
        public string CurrentHash => ShortHash(Asset.CurrentExportSha256);
        public string Message => Asset.Message;
    }

    private sealed record ClientUpdateInstallationRow(ClientInstallationTargetStatus Status)
    {
        public ClientInstallationTargetState State => Status.State;
        public string DisplayName => Status.DisplayName;
        public string TargetPath => string.IsNullOrWhiteSpace(Status.TargetPath)
            ? Status.ReceiptPaths.FirstOrDefault() ?? "-"
            : Status.TargetPath;
        public string BackupState => Status.State == ClientInstallationTargetState.ActivePayloadRestoreAvailable
            ? "Verified"
            : Status.BackupExists ? Status.BackupVerified ? "Verified" : "Invalid" : "Not present";
        public string Message => Status.Message;
    }

    private sealed record ClientUpdateFormatCheckRow(
        string PackageName,
        ClientCompatibilityCheckSeverity Severity,
        string Code,
        string Profile,
        string Chunks,
        string Message)
    {
        public static IReadOnlyList<ClientUpdateFormatCheckRow> Create(
            string packageName, ClientPackageFormatProbe probe)
        {
            var profile = probe.FileVersion is null
                ? probe.State.ToString()
                : $"{probe.FileVersion}/{probe.LicenseeVersion} · 0x{probe.CompressionFlags:X8}";
            var chunks = $"{probe.DecodedChunkCount:N0}/{probe.CompressedChunkCount:N0}" +
                         (probe.ProtectedChunkCount > 0 ? $" ({probe.ProtectedChunkCount:N0} protected)" : string.Empty);
            return probe.Checks.Count > 0
                ? probe.Checks.Select(check => new ClientUpdateFormatCheckRow(
                    packageName, check.Severity, check.Code, profile, chunks, check.Message)).ToArray()
                : [new ClientUpdateFormatCheckRow(packageName,
                    probe.CanRead && probe.CanReconstruct
                        ? ClientCompatibilityCheckSeverity.Information
                        : ClientCompatibilityCheckSeverity.Blocking,
                    "CUF_STATUS", profile, chunks, probe.State.ToString())];
        }
    }

    private sealed record ClientSnapshotRow(ClientSnapshotEntry Entry)
    {
        public string Created => Entry.Snapshot.CreatedAt.ToLocalTime().ToString("g");

        public string Label => Entry.Snapshot.Label;

        public string PackageCount => $"{Entry.Snapshot.Packages.Count:N0}";

        public string PackageBytes => FormatByteSize(Entry.Snapshot.PackageBytes);

        public string IndexedAssets => Entry.Snapshot.IndexManifest is null
            ? "-"
            : $"{Entry.Snapshot.IndexManifest.AssetCount:N0}";

        public string BackendFingerprint => ShortHash(Entry.Snapshot.IndexManifest?.BackendSha256);

        public string MetadataFingerprint => ShortHash(Entry.Snapshot.GameMetadataSha256);

        public string LibraryRoot => Entry.Snapshot.LibraryRoot;
    }

    private sealed record ClientPackageChangeRow(ClientPackageChange Change)
    {
        public ClientPackageChangeKind Kind => Change.Kind;

        public string RelativePath => Change.RelativePath;

        public string PreviousSize => Change.PreviousLength is { } bytes
            ? FormatByteSize(bytes)
            : "-";

        public string CurrentSize => Change.CurrentLength is { } bytes
            ? FormatByteSize(bytes)
            : "-";

        public string PreviousModified => FormatTimestamp(Change.PreviousLastWriteUtcTicks);

        public string CurrentModified => FormatTimestamp(Change.CurrentLastWriteUtcTicks);
    }

    private sealed record AssetBaselineRow(AssetBaselineEntry Entry)
    {
        public string Created => Entry.Baseline.CreatedAt.ToLocalTime().ToString("g");

        public string PackageAlias => Entry.Baseline.PackageAlias;

        public AssetKind Kind => Entry.Baseline.Kind;

        public string ClassName => Entry.Baseline.ClassName;

        public string ObjectName => Entry.Baseline.ObjectName;

        public string LogicalRange =>
            $"0x{Entry.Baseline.SerialOffset:X}+0x{Entry.Baseline.SerialSize:X}";

        public int StorageSpanCount => Entry.Baseline.StorageRanges.Count;

        public string PackageHash => ShortHash(Entry.Baseline.PackageSha256);

        public string PayloadHash => ShortHash(Entry.Baseline.LogicalPayloadSha256);

        public string PayloadStatus => Entry.Baseline.LogicalPayloadHashStatus;
    }

    private static string ShortHash(string? value) => string.IsNullOrWhiteSpace(value)
        ? "-"
        : value[..Math.Min(16, value.Length)] + (value.Length > 16 ? "..." : string.Empty);

    private static string FormatTimestamp(long? utcTicks) => utcTicks is null
        ? "-"
        : new DateTime(utcTicks.Value, DateTimeKind.Utc).ToLocalTime().ToString("g");

    private sealed record ExportWorkspaceRow(ExportWorkspaceEntry Entry)
    {
        public string Created => Entry.Manifest.CreatedAt.ToLocalTime().ToString("g");

        public string Method => Entry.Manifest.ExportMethod;

        public string PackageAlias => Entry.Manifest.PackageAlias;

        public AssetKind Kind => Entry.Manifest.Kind;

        public string ClassName => Entry.Manifest.ClassName;

        public string ObjectName => Entry.Manifest.ObjectName;

        public string InGameNames => string.Join(", ", Entry.Manifest.InGameNames);

        public int FileCount => Entry.Manifest.OutputFiles.Count;

        public string Size => FormatByteSize(Entry.Manifest.OutputBytes);

        public string Fallback => Entry.Manifest.ExportMethod == "UModel"
            ? Entry.Manifest.UsedPackageWideFallback ? "Package" : "No"
                : "-";
    }

    private sealed record TextureImportCandidateDetailRow(
        string Group,
        string Name,
        string Value);

    private sealed class ExportArtifactFileRow
    {
        public ExportArtifactFileRow(string outputDirectory, ExportWorkspaceFile file)
        {
            RelativePath = file.RelativePath;
            var root = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
            FullPath = Path.GetFullPath(Path.Combine(outputDirectory, file.RelativePath));
            IsWithinSession = FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            Exists = IsWithinSession && File.Exists(FullPath);
            ActualLength = Exists ? new FileInfo(FullPath).Length : file.Length;
        }

        public string RelativePath { get; }

        public string FullPath { get; }

        public bool IsWithinSession { get; }

        public bool Exists { get; }

        public long ActualLength { get; }

        public string Name => Path.GetFileName(RelativePath);

        public string Type => string.IsNullOrEmpty(Path.GetExtension(RelativePath))
            ? "Other"
            : Path.GetExtension(RelativePath).TrimStart('.').ToUpperInvariant();

        public string Size => $"{ActualLength:N0}";

        public bool IsSupported => IsWithinSession && ExportArtifactInspector.IsSupported(FullPath);
    }

    private sealed record ExportArtifactDependencyRow(ArtifactInspectionDependency Dependency)
    {
        public string Kind => Dependency.Kind;

        public string Reference => Dependency.Reference;

        public string State => Dependency.IsEmbedded
            ? "Embedded"
            : Dependency.Exists
                ? "Present"
                : "Missing";

        public string Bytes => Dependency.DeclaredBytes is { } declared
            ? Dependency.ActualBytes is { } actual
                ? $"{actual:N0} / {declared:N0}"
                : $"declared {declared:N0}"
            : Dependency.ActualBytes is { } size
                ? size.ToString("N0")
                : "-";
    }
}
