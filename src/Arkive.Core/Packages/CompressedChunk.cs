namespace Arkive.Core.Packages;

public sealed record CompressedChunk(
    uint UncompressedOffset,
    uint UncompressedSize,
    uint CompressedOffset,
    uint CompressedSize,
    uint LostArkFlags)
{
    public ulong UncompressedEnd => (ulong)UncompressedOffset + UncompressedSize;

    public ulong CompressedEnd => (ulong)CompressedOffset + CompressedSize;
}
