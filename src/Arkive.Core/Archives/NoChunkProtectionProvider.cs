using Arkive.Core.Packages;

namespace Arkive.Core.Archives;

public sealed class NoChunkProtectionProvider : IChunkProtectionProvider
{
    public static NoChunkProtectionProvider Instance { get; } = new();

    private NoChunkProtectionProvider()
    {
    }

    public string Name => "none";

    public bool CanUnprotect(PackageSummary summary, CompressedChunk chunk) => false;

    public bool CanProtect(PackageSummary summary, CompressedChunk chunk) => false;

    public void UnprotectBlock(
        PackageSummary summary,
        CompressedChunk chunk,
        int blockIndex,
        Span<byte> compressedBlock) =>
        throw new PackageAccessException(
            "This package contains protected Lost Ark blocks. Configure an authorized " +
            "chunk-protection provider before opening its logical asset tables.");

    public void ProtectBlock(
        PackageSummary summary,
        CompressedChunk chunk,
        int blockIndex,
        Span<byte> compressedBlock) =>
        throw new PackageAccessException(
            "This package requires protected Lost Ark blocks. Configure an authorized " +
            "chunk-protection provider before writing a staging package.");
}
