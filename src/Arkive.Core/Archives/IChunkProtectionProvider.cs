using Arkive.Core.Packages;

namespace Arkive.Core.Archives;

public interface IChunkProtectionProvider
{
    string Name { get; }

    bool CanUnprotect(PackageSummary summary, CompressedChunk chunk);

    bool CanProtect(PackageSummary summary, CompressedChunk chunk);

    void UnprotectBlock(
        PackageSummary summary,
        CompressedChunk chunk,
        int blockIndex,
        Span<byte> compressedBlock);

    void ProtectBlock(
        PackageSummary summary,
        CompressedChunk chunk,
        int blockIndex,
        Span<byte> compressedBlock);
}
