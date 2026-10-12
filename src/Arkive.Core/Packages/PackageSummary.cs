namespace Arkive.Core.Packages;

public sealed record PackageSummary
{
    public required string FilePath { get; init; }

    public required long PhysicalFileSize { get; init; }

    public required uint Tag { get; init; }

    public required ushort FileVersion { get; init; }

    public required ushort LicenseeVersion { get; init; }

    public required int HeaderSize { get; init; }

    public required string FolderName { get; init; }

    public required uint PackageFlags { get; init; }

    public required int NameCount { get; init; }

    public required int NameOffset { get; init; }

    public required int ExportCount { get; init; }

    public required int ExportOffset { get; init; }

    public required int ImportCount { get; init; }

    public required int ImportOffset { get; init; }

    public required int DependsOffset { get; init; }

    public required int ImportExportGuidsOffset { get; init; }

    public required int ImportGuidsCount { get; init; }

    public required int ExportGuidsCount { get; init; }

    public required int ThumbnailTableOffset { get; init; }

    public required Guid PackageGuid { get; init; }

    public required IReadOnlyList<PackageGeneration> Generations { get; init; }

    public required int EngineVersion { get; init; }

    public required int CookerVersion { get; init; }

    public required uint CompressionFlags { get; init; }

    public long CompressionFlagsOffset { get; init; }

    public long CompressedChunkCountOffset { get; init; }

    public long CompressedChunkTableOffset { get; init; }

    public required IReadOnlyList<CompressedChunk> CompressedChunks { get; init; }

    public required uint PackageSource { get; init; }

    public long PackageSourceOffset { get; init; }

    public required long ParsedSummarySize { get; init; }

    public bool IsWesternLostArkProfile =>
        FileVersion == PackageConstants.WesternFileVersion &&
        LicenseeVersion == PackageConstants.WesternLicenseeVersion &&
        CompressionFlags == PackageConstants.WesternCompressionFlags;
}
