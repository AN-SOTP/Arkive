using Arkive.Core.Inspection;

namespace Arkive.Core.Textures;

public sealed record NativeTextureExportRequest(
    TextureReconnaissanceReport Reconnaissance,
    string OutputDirectory,
    string ReconnaissanceJsonPath);

public sealed record NativeTextureMipExport(
    int MipIndex,
    int Width,
    int Height,
    long SourcePayloadLogicalOffset,
    uint BulkDataFlags,
    string StorageCodec,
    int StoredPayloadBytes,
    string StoredPayloadSha256,
    string StoredPayloadRelativePath,
    int StorageHeaderBytes,
    int DecodedGpuBytes,
    string DecodedGpuSha256,
    long DdsPayloadOffset);

public sealed record NativeTextureExportManifest(
    int SchemaVersion,
    string ExportId,
    DateTimeOffset CreatedAt,
    string SourcePackagePath,
    string PhysicalPackageName,
    string PackageAlias,
    long SourcePackageLength,
    long SourcePackageLastWriteUtcTicks,
    string SourcePackageSha256,
    string ProtectionProviderName,
    int SourceExportIndex,
    int? NativeExportIndex,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    string SourcePayloadSha256,
    long PropertyStreamRelativeOffset,
    long NativeDataRelativeOffset,
    string PixelFormat,
    string Compression,
    int Width,
    int Height,
    int OriginalWidth,
    int OriginalHeight,
    int MipCount,
    string Decoder,
    string DdsRelativePath,
    long DdsBytes,
    string DdsSha256,
    IReadOnlyList<NativeTextureMipExport> Mips,
    string ReconnaissanceJsonPath,
    double DurationSeconds,
    string? RgbViewDdsRelativePath = null,
    long? RgbViewDdsBytes = null,
    string? RgbViewDdsSha256 = null,
    string? EditImageRelativePath = null,
    long? EditImageBytes = null,
    string? EditImageSha256 = null,
    string? EditImageSemantic = null);

public sealed record NativeTextureExportResult(
    string OutputDirectory,
    string DdsPath,
    string ManifestPath,
    NativeTextureExportManifest Manifest,
    IReadOnlyList<string> OutputFiles,
    ArtifactInspectionResult DdsInspection,
    TimeSpan Duration);

public sealed class NativeTextureExportRefusalException : NotSupportedException
{
    public NativeTextureExportRefusalException(string code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public string Code { get; }
}
