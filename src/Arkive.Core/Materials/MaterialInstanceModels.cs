using System.Text.Json.Serialization;
using Arkive.Core.Assets;

namespace Arkive.Core.Materials;

public enum MaterialInstanceReadiness
{
    Refused,
    EvidenceOnly,
    Editable,
}

public enum MaterialParameterKind
{
    Scalar,
    Vector,
    Texture,
}

public enum MaterialObjectReferenceScope
{
    None,
    Export,
    Import,
}

public sealed record MaterialInstanceReconnaissanceRequest(
    string PackagePath,
    string PackageAlias,
    int ExportIndex,
    AssetKind Kind,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    string? KnownPackageSha256 = null);

public sealed record MaterialPropertyEvidence(
    long RelativeOffset,
    long LogicalOffset,
    string Name,
    string Type,
    int Size,
    int ArrayIndex,
    long ValueRelativeOffset,
    string ValuePreview,
    string RawValuePrefix);

public sealed record MaterialScalarParameterEvidence(
    int ParameterIndex,
    string Name,
    float Value,
    long ElementRelativeOffset,
    long ValueRelativeOffset,
    Guid ExpressionGuid);

public sealed record MaterialVectorParameterEvidence(
    int ParameterIndex,
    string Name,
    float Red,
    float Green,
    float Blue,
    float Alpha,
    long ElementRelativeOffset,
    long ValueRelativeOffset,
    Guid ExpressionGuid);

public sealed record MaterialTextureParameterEvidence(
    int ParameterIndex,
    string Name,
    int ObjectReference,
    string ResolvedObject,
    long ElementRelativeOffset,
    long ValueRelativeOffset,
    Guid ExpressionGuid);

public sealed record MaterialReconnaissanceIssue(string Severity, string Code, string Message);

public sealed record MaterialInstanceReconnaissanceReport(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string PackagePath,
    string PhysicalPackageName,
    string PackageAlias,
    long PackageLength,
    long PackageLastWriteUtcTicks,
    string PackageSha256,
    string ProtectionProviderName,
    int ExportIndex,
    int? NativeExportIndex,
    AssetKind Kind,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    string ExportPayloadSha256,
    long? PropertyStreamRelativeOffset,
    long? PropertyStreamEndRelativeOffset,
    int? ParentObjectReference,
    string? ParentObject,
    IReadOnlyList<MaterialPropertyEvidence> Properties,
    IReadOnlyList<MaterialScalarParameterEvidence> ScalarParameters,
    IReadOnlyList<MaterialVectorParameterEvidence> VectorParameters,
    IReadOnlyList<MaterialTextureParameterEvidence> TextureParameters,
    MaterialInstanceReadiness Readiness,
    IReadOnlyList<MaterialReconnaissanceIssue> Issues);

public sealed record MaterialScalarEdit(string Name, float SourceValue, float Value)
{
    [JsonIgnore]
    public bool Changed => BitConverter.SingleToInt32Bits(SourceValue) != BitConverter.SingleToInt32Bits(Value);
}

public sealed record MaterialVectorEdit(
    string Name,
    float SourceRed,
    float SourceGreen,
    float SourceBlue,
    float SourceAlpha,
    float Red,
    float Green,
    float Blue,
    float Alpha)
{
    [JsonIgnore]
    public bool Changed =>
        BitConverter.SingleToInt32Bits(SourceRed) != BitConverter.SingleToInt32Bits(Red) ||
        BitConverter.SingleToInt32Bits(SourceGreen) != BitConverter.SingleToInt32Bits(Green) ||
        BitConverter.SingleToInt32Bits(SourceBlue) != BitConverter.SingleToInt32Bits(Blue) ||
        BitConverter.SingleToInt32Bits(SourceAlpha) != BitConverter.SingleToInt32Bits(Alpha);
}

public sealed record MaterialTextureReference(
    string Name,
    int ObjectReference,
    string ResolvedObject)
{
    public int SourceObjectReference { get; init; } = ObjectReference;

    public string SourceResolvedObject { get; init; } = ResolvedObject;

    public string SourceIdentity { get; init; } = string.Empty;

    public string TargetIdentity { get; init; } = string.Empty;
}

public sealed record MaterialParentReference(
    int SourceObjectReference,
    string SourceResolvedObject,
    string SourceIdentity,
    int ObjectReference,
    string ResolvedObject,
    string TargetIdentity);

public sealed record MaterialObjectReferenceOption(
    int ObjectReference,
    string ClassName,
    string ObjectName,
    string ObjectPath,
    string ResolvedObject,
    string StableIdentity,
    MaterialObjectReferenceScope Scope)
{
    public string DisplayName => ObjectReference == 0
        ? "None"
        : $"{ObjectName}  [{ClassName}; {Scope}; ref {ObjectReference}]";
}

public sealed record MaterialReferenceCatalog(
    IReadOnlyList<MaterialObjectReferenceOption> TextureTargets,
    IReadOnlyList<MaterialObjectReferenceOption> ParentTargets);

public sealed record MaterialObjectReferenceChange(
    string Field,
    int SourceObjectReference,
    string SourceResolvedObject,
    int TargetObjectReference,
    string TargetResolvedObject,
    long ValueRelativeOffset);

public sealed record MaterialInstanceEditDocument(
    int SchemaVersion,
    string EditId,
    DateTimeOffset CreatedAt,
    string DisplayName,
    string SourceReconnaissancePath,
    string SourcePackagePath,
    string SourcePackageSha256,
    string PackageAlias,
    int ExportIndex,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    string SourceExportSha256,
    IReadOnlyList<MaterialScalarEdit> Scalars,
    IReadOnlyList<MaterialVectorEdit> Vectors,
    IReadOnlyList<MaterialTextureReference> Textures)
{
    public DateTimeOffset UpdatedAt { get; init; } = CreatedAt;

    public bool IncludedInPackageBuild { get; init; } = true;

    public bool IsFavorite { get; init; }

    public MaterialParentReference? Parent { get; init; }

    public string? LatestCandidateManifestPath { get; init; }

    public string? LatestCandidatePath { get; init; }

    public string? LatestCandidateSha256 { get; init; }
}

public sealed record MaterialInstanceEditEntry(
    string DirectoryPath,
    string DocumentPath,
    MaterialInstanceEditDocument Document);

public sealed record MaterialInstanceCandidateManifest(
    int SchemaVersion,
    string CandidateId,
    DateTimeOffset CreatedAt,
    string EditDocumentPath,
    string SourceReconnaissancePath,
    string SourcePackagePath,
    string SourcePackageSha256,
    string PackageAlias,
    int ExportIndex,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    string SourceExportSha256,
    string CandidateExportSha256,
    string CandidateRelativePath,
    int ChangedScalarCount,
    int ChangedVectorCount,
    int ChangedByteCount,
    bool SameSize,
    bool LockedBytesPreserved,
    bool GameFilesModified)
{
    public int ChangedTextureReferenceCount { get; init; }

    public bool ParentReferenceChanged { get; init; }

    public IReadOnlyList<MaterialObjectReferenceChange> ReferenceChanges { get; init; } = [];
}

public sealed record MaterialInstanceCandidateResult(
    string DirectoryPath,
    string ManifestPath,
    string CandidatePath,
    MaterialInstanceCandidateManifest Manifest);
