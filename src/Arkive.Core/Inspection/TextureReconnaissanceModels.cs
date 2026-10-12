using Arkive.Core.Assets;

namespace Arkive.Core.Inspection;

public enum TextureReconnaissanceConfidence
{
    Unresolved,
    Candidate,
    StrongCandidate,
    Confirmed,
}

public sealed record TextureReconnaissanceRequest(
    string PackagePath,
    string PackageAlias,
    int ExportIndex,
    AssetKind Kind,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    string? KnownPackageSha256 = null);

public sealed record TexturePropertyEvidence(
    long RelativeOffset,
    long LogicalOffset,
    string Name,
    string Type,
    int Size,
    int ArrayIndex,
    string? TypeMetadata,
    long ValueRelativeOffset,
    long ValueLogicalOffset,
    string ValuePreview,
    long? NumericValue,
    string RawValuePrefix);

public sealed record TextureMipEvidence(
    int MipIndex,
    long HeaderRelativeOffset,
    long HeaderLogicalOffset,
    uint BulkDataFlags,
    int ElementCount,
    int StoredSize,
    int BulkDataOffset,
    int Width,
    int Height,
    string Storage,
    long? PayloadRelativeOffset,
    long? PayloadLogicalOffset,
    string? PayloadSha256,
    IReadOnlyList<int> PackageChunkNumbers,
    bool BulkOffsetMatchesPayload,
    bool StoredSizeMatchesFormat);

public sealed record TextureMipChainEvidence(
    int CandidateNumber,
    long ArrayRelativeOffset,
    long ArrayLogicalOffset,
    int DeclaredMipCount,
    int Score,
    TextureReconnaissanceConfidence Confidence,
    string PixelFormatEvidence,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<TextureMipEvidence> Mips);

public sealed record TextureStorageEvidence(
    string StorageKind,
    int? ChunkNumber,
    long LogicalOffset,
    long LogicalSize,
    long PhysicalOffset,
    long PhysicalSize,
    uint? LostArkFlags);

public sealed record TextureReconnaissanceIssue(
    string Severity,
    string Code,
    string Message);

public sealed record TexturePackageProfile(
    ushort FileVersion,
    ushort LicenseeVersion,
    int EngineVersion,
    int CookerVersion,
    uint PackageFlags,
    uint CompressionFlags,
    Guid PackageGuid,
    int ChunkCount,
    long LogicalLength);

public sealed record TextureReconnaissanceReport(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string PackagePath,
    string PhysicalPackageName,
    string PackageAlias,
    long PackageLength,
    long PackageLastWriteUtcTicks,
    string PackageSha256,
    TexturePackageProfile PackageProfile,
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
    long? NativeDataRelativeOffset,
    string PixelFormatEvidence,
    IReadOnlyList<TexturePropertyEvidence> Properties,
    IReadOnlyList<TextureMipChainEvidence> MipCandidates,
    IReadOnlyList<TextureStorageEvidence> ExportStorage,
    IReadOnlyList<TextureReconnaissanceIssue> Issues,
    TimeSpan Duration);

public sealed record TextureReconnaissanceEntry(
    string DirectoryPath,
    string JsonPath,
    string TextPath,
    TextureReconnaissanceReport Report);
