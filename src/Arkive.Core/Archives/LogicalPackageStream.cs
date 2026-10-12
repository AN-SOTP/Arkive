using System.Buffers.Binary;
using System.Security.Cryptography;
using Arkive.Core.Compression;
using Arkive.Core.Packages;

namespace Arkive.Core.Archives;

public sealed class LogicalPackageStream : Stream
{
    private const int MaximumBlockCount = 1_000_000;
    private const int MaximumBlockSize = 256 * 1024 * 1024;

    private readonly PackageSummary _summary;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly FileStream _physicalStream;
    private long _position;
    private int _cachedChunkIndex = -1;
    private byte[]? _cachedChunk;

    public LogicalPackageStream(
        PackageSummary summary,
        IChunkProtectionProvider? protectionProvider = null)
    {
        ArgumentNullException.ThrowIfNull(summary);

        _summary = summary;
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _physicalStream = new FileStream(
            summary.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.RandomAccess);

        Length = summary.CompressedChunks.Count == 0
            ? summary.PhysicalFileSize
            : checked((long)summary.CompressedChunks[^1].UncompressedEnd);
    }

    public PackageSummary Summary => _summary;

    public string ProtectionProviderName => _protectionProvider.Name;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(!_physicalStream.CanRead, this);
        if (_position >= Length || buffer.Length == 0)
        {
            return 0;
        }

        var requested = (int)Math.Min(buffer.Length, Length - _position);
        var destination = buffer[..requested];
        var totalRead = 0;

        while (!destination.IsEmpty)
        {
            if (_summary.CompressedChunks.Count == 0)
            {
                totalRead += ReadPhysical(_position, destination);
                _position += destination.Length;
                break;
            }

            var chunkIndex = FindChunk(_position);
            if (chunkIndex < 0)
            {
                var firstChunkOffset = _summary.CompressedChunks[0].UncompressedOffset;
                if (_position >= firstChunkOffset)
                {
                    throw new PackageFormatException(
                        $"Logical offset 0x{_position:X} falls in a gap between compressed chunks.");
                }

                var available = checked((int)Math.Min(
                    destination.Length,
                    firstChunkOffset - _position));
                var read = ReadPhysical(_position, destination[..available]);
                _position += read;
                totalRead += read;
                destination = destination[read..];
                continue;
            }

            var chunk = _summary.CompressedChunks[chunkIndex];
            var decoded = GetDecodedChunk(chunkIndex);
            var offsetInChunk = checked((int)(_position - chunk.UncompressedOffset));
            var availableInChunk = decoded.Length - offsetInChunk;
            var toCopy = Math.Min(destination.Length, availableInChunk);
            decoded.AsSpan(offsetInChunk, toCopy).CopyTo(destination);

            _position += toCopy;
            totalRead += toCopy;
            destination = destination[toCopy..];
        }

        return totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (newPosition < 0 || newPosition > Length)
        {
            throw new IOException(
                $"Logical seek 0x{newPosition:X} is outside the package length 0x{Length:X}.");
        }

        _position = newPosition;
        return _position;
    }

    public string HashRangeSha256(long offset, long length, CancellationToken cancellationToken = default)
    {
        Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (read <= 0) throw new EndOfStreamException($"The export at logical offset 0x{offset:X} is truncated.");
            hash.AppendData(buffer, 0, read);
            length -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _physicalStream.Dispose();
            _cachedChunk = null;
        }

        base.Dispose(disposing);
    }

    private int ReadPhysical(long offset, Span<byte> destination)
    {
        _physicalStream.Position = offset;
        _physicalStream.ReadExactly(destination);
        return destination.Length;
    }

    private int FindChunk(long logicalOffset)
    {
        var low = 0;
        var high = _summary.CompressedChunks.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var chunk = _summary.CompressedChunks[middle];
            if ((ulong)logicalOffset < chunk.UncompressedOffset)
            {
                high = middle - 1;
            }
            else if ((ulong)logicalOffset >= chunk.UncompressedEnd)
            {
                low = middle + 1;
            }
            else
            {
                return middle;
            }
        }

        return -1;
    }

    private byte[] GetDecodedChunk(int chunkIndex)
    {
        if (_cachedChunkIndex == chunkIndex && _cachedChunk is not null)
        {
            return _cachedChunk;
        }

        var chunk = _summary.CompressedChunks[chunkIndex];
        var physical = new byte[checked((int)chunk.CompressedSize)];
        ReadPhysical(chunk.CompressedOffset, physical);

        byte[] decoded;
        if (chunk.LostArkFlags == 0 && chunk.CompressedSize == chunk.UncompressedSize)
        {
            decoded = physical;
        }
        else
        {
            decoded = DecodeCompressedChunk(chunk, physical);
        }

        if (decoded.Length != chunk.UncompressedSize)
        {
            throw new PackageFormatException(
                $"Chunk {chunkIndex + 1} decoded to {decoded.Length:N0} bytes; " +
                $"expected {chunk.UncompressedSize:N0}.");
        }

        _cachedChunkIndex = chunkIndex;
        _cachedChunk = decoded;
        return decoded;
    }

    private byte[] DecodeCompressedChunk(CompressedChunk chunk, ReadOnlySpan<byte> physical)
    {
        var cursor = 0;
        var tag = ReadUInt32(physical, ref cursor, "compressed chunk tag");
        if (tag != PackageConstants.UnrealPackageTag)
        {
            throw new PackageFormatException(
                $"Compressed chunk at 0x{chunk.CompressedOffset:X} has tag 0x{tag:X8}; " +
                $"expected 0x{PackageConstants.UnrealPackageTag:X8}.");
        }

        var blockSize = ReadInt32(physical, ref cursor, "compressed block size");
        var summaryCompressedSize = ReadInt32(physical, ref cursor, "chunk compressed size");
        var summaryUncompressedSize = ReadInt32(physical, ref cursor, "chunk uncompressed size");
        if (blockSize <= 0 || blockSize > MaximumBlockSize ||
            summaryCompressedSize <= 0 || summaryUncompressedSize <= 0)
        {
            throw new PackageFormatException("Compressed chunk header contains invalid sizes.");
        }

        if (summaryUncompressedSize != chunk.UncompressedSize)
        {
            throw new PackageFormatException(
                $"Compressed chunk header expects {summaryUncompressedSize:N0} output bytes; " +
                $"the package table expects {chunk.UncompressedSize:N0}.");
        }

        var blocks = new List<BlockDescriptor>();
        long totalCompressed = 0;
        long totalUncompressed = 0;
        while (totalCompressed < summaryCompressedSize &&
               totalUncompressed < summaryUncompressedSize)
        {
            if (blocks.Count >= MaximumBlockCount)
            {
                throw new PackageFormatException("Compressed chunk has too many blocks.");
            }

            var compressedSize = ReadInt32(physical, ref cursor, "block compressed size");
            var uncompressedSize = ReadInt32(physical, ref cursor, "block uncompressed size");
            if (compressedSize <= 0 || uncompressedSize <= 0 ||
                compressedSize > MaximumBlockSize || uncompressedSize > MaximumBlockSize)
            {
                throw new PackageFormatException("Compressed chunk has an invalid block descriptor.");
            }

            totalCompressed += compressedSize;
            totalUncompressed += uncompressedSize;
            if (totalCompressed > summaryCompressedSize ||
                totalUncompressed > summaryUncompressedSize)
            {
                throw new PackageFormatException("Compressed block sizes exceed their chunk summary.");
            }

            blocks.Add(new BlockDescriptor(compressedSize, uncompressedSize));
        }

        if (totalUncompressed != summaryUncompressedSize ||
            totalCompressed > summaryCompressedSize)
        {
            throw new PackageFormatException("Compressed block sizes do not match their chunk summary.");
        }

        if (physical.Length - cursor < totalCompressed)
        {
            throw new PackageFormatException("Compressed chunk payload is truncated.");
        }

        var output = new byte[summaryUncompressedSize];
        var outputOffset = 0;
        for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            var block = blocks[blockIndex];
            var compressed = physical.Slice(cursor, block.CompressedSize).ToArray();
            cursor += block.CompressedSize;

            if (chunk.LostArkFlags == 1)
            {
                if (!_protectionProvider.CanUnprotect(_summary, chunk))
                {
                    throw new PackageAccessException(
                        $"Chunk at logical offset 0x{chunk.UncompressedOffset:X} is protected; " +
                        "no authorized provider is configured.");
                }

                _protectionProvider.UnprotectBlock(
                    _summary,
                    chunk,
                    blockIndex,
                    compressed);
            }

            byte[] uncompressed;
            if (block.CompressedSize == block.UncompressedSize)
            {
                uncompressed = compressed;
            }
            else
            {
                try
                {
                    uncompressed = Lz4BlockDecoder.Decode(compressed, block.UncompressedSize);
                }
                catch (InvalidDataException exception)
                {
                    throw new PackageFormatException(
                        $"LZ4 block {blockIndex + 1} at physical chunk " +
                        $"0x{chunk.CompressedOffset:X} could not be decoded.",
                        exception);
                }
            }

            uncompressed.CopyTo(output, outputOffset);
            outputOffset += uncompressed.Length;
        }

        return output;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int cursor, string label)
    {
        EnsureAvailable(data, cursor, sizeof(uint), label);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
        cursor += sizeof(uint);
        return value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int cursor, string label)
    {
        EnsureAvailable(data, cursor, sizeof(int), label);
        var value = BinaryPrimitives.ReadInt32LittleEndian(data[cursor..]);
        cursor += sizeof(int);
        return value;
    }

    private static void EnsureAvailable(
        ReadOnlySpan<byte> data,
        int cursor,
        int count,
        string label)
    {
        if (cursor < 0 || count < 0 || data.Length - cursor < count)
        {
            throw new PackageFormatException($"The {label} is truncated.");
        }
    }

    private sealed record BlockDescriptor(int CompressedSize, int UncompressedSize);
}
