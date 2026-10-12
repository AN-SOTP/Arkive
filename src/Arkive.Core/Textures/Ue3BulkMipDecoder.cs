using System.Buffers.Binary;
using Arkive.Core.Compression;
using Arkive.Core.Packages;

namespace Arkive.Core.Textures;

public sealed record Ue3BulkDecodeInfo(
    int HeaderBytes,
    int BlockSize,
    int CompressedBytes,
    int UncompressedBytes,
    int BlockCount,
    string Codec);

public static class Ue3BulkMipDecoder
{
    public const uint LostArkCompressedBulkFlag = 0x80;
    private const int SummaryBytes = 16;
    private const int DescriptorBytes = 8;
    private const int MaximumBlockCount = 1_024;

    public static byte[] DecodeLostArkLz4(
        ReadOnlySpan<byte> payload,
        int expectedLength,
        out Ue3BulkDecodeInfo info)
    {
        if (expectedLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLength));
        }

        if (payload.Length < SummaryBytes + DescriptorBytes)
        {
            throw Invalid("UE3 compressed bulk payload is shorter than its header.");
        }

        var tag = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (tag != PackageConstants.UnrealPackageTag)
        {
            throw Invalid($"UE3 compressed bulk tag is 0x{tag:X8}; expected 0x{PackageConstants.UnrealPackageTag:X8}.");
        }

        var blockSize = ReadPositive(payload, 4, "block size");
        var summaryCompressed = ReadPositive(payload, 8, "summary compressed size");
        var summaryUncompressed = ReadPositive(payload, 12, "summary uncompressed size");
        if (summaryUncompressed != expectedLength)
        {
            throw Invalid(
                $"UE3 compressed bulk declares {summaryUncompressed:N0} output bytes; " +
                $"the mip requires {expectedLength:N0}.");
        }

        var descriptors = new List<(int Compressed, int Uncompressed)>();
        var cursor = SummaryBytes;
        long totalCompressed = 0;
        long totalUncompressed = 0;
        while (totalCompressed < summaryCompressed && totalUncompressed < summaryUncompressed)
        {
            if (descriptors.Count >= MaximumBlockCount || payload.Length - cursor < DescriptorBytes)
            {
                throw Invalid("UE3 compressed bulk block table is truncated or excessive.");
            }

            var compressed = ReadPositive(payload, cursor, "block compressed size");
            var uncompressed = ReadPositive(payload, cursor + 4, "block uncompressed size");
            cursor += DescriptorBytes;
            totalCompressed += compressed;
            totalUncompressed += uncompressed;
            if (totalCompressed > summaryCompressed || totalUncompressed > summaryUncompressed ||
                uncompressed > blockSize)
            {
                throw Invalid("UE3 compressed bulk block sizes exceed their summary.");
            }

            descriptors.Add((compressed, uncompressed));
        }

        if (totalCompressed != summaryCompressed || totalUncompressed != summaryUncompressed ||
            payload.Length - cursor != summaryCompressed)
        {
            throw Invalid("UE3 compressed bulk sizes do not exactly cover the stored payload.");
        }

        var output = new byte[summaryUncompressed];
        var outputOffset = 0;
        foreach (var descriptor in descriptors)
        {
            var compressed = payload.Slice(cursor, descriptor.Compressed);
            cursor += descriptor.Compressed;
            // Lost Ark's 0x80 bulk-data flag maps to UE3 COMPRESS_LZO's game
            // override, which this UModel fork always sends through LZ4. Equal
            // stored/uncompressed sizes are not a raw-block escape hatch. Older
            // Arkive builds accepted such blocks as raw, allowing invalid tiny
            // mips to pass decode-back and then fail in UModel.
            var block = Lz4BlockDecoder.Decode(compressed, descriptor.Uncompressed);

            block.CopyTo(output, outputOffset);
            outputOffset += block.Length;
        }

        info = new Ue3BulkDecodeInfo(
            SummaryBytes + descriptors.Count * DescriptorBytes,
            blockSize,
            summaryCompressed,
            summaryUncompressed,
            descriptors.Count,
            "UE3 compressed bulk / Lost Ark LZ4");
        return output;
    }

    private static int ReadPositive(ReadOnlySpan<byte> payload, int offset, string label)
    {
        if (offset < 0 || payload.Length - offset < sizeof(int))
        {
            throw Invalid($"UE3 compressed bulk {label} is truncated.");
        }

        var value = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
        if (value <= 0)
        {
            throw Invalid($"UE3 compressed bulk {label} is invalid ({value:N0}).");
        }

        return value;
    }

    private static InvalidDataException Invalid(string message) => new(message);
}

public static class Ue3BulkMipEncoder
{
    private const int DefaultBlockSize = 0x20000;

    public static byte[] EncodeLostArkLz4(
        ReadOnlySpan<byte> gpuPayload,
        int blockSize = DefaultBlockSize)
    {
        if (gpuPayload.IsEmpty)
        {
            throw new ArgumentException("A UE3 bulk mip cannot be empty.", nameof(gpuPayload));
        }
        if (blockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        }

        var blocks = new List<(byte[] Stored, int Uncompressed)>();
        for (var offset = 0; offset < gpuPayload.Length; offset += blockSize)
        {
            var length = Math.Min(blockSize, gpuPayload.Length - offset);
            var raw = gpuPayload.Slice(offset, length).ToArray();
            var compressed = Arkive.Core.Compression.Lz4BlockEncoder.Encode(raw);
            // Lost Ark/UModel treat this bulk-data flag as an unconditional LZ4
            // contract. Even when a tiny mip grows by a few bytes, retain the valid
            // LZ4 token stream instead of substituting equal-size raw GPU bytes.
            blocks.Add((compressed, raw.Length));
        }

        var compressedBytes = blocks.Sum(block => block.Stored.Length);
        var result = new byte[checked(16 + blocks.Count * 8 + compressedBytes)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            result, PackageConstants.UnrealPackageTag);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), blockSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), compressedBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), gpuPayload.Length);
        var cursor = 16;
        foreach (var block in blocks)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                result.AsSpan(cursor), block.Stored.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                result.AsSpan(cursor + 4), block.Uncompressed);
            cursor += 8;
        }
        foreach (var block in blocks)
        {
            block.Stored.CopyTo(result, cursor);
            cursor += block.Stored.Length;
        }
        return result;
    }
}
