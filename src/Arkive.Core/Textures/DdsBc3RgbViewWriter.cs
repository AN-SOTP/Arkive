using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Arkive.Core.Textures;

public sealed record DdsBc3RgbViewResult(
    string SourcePath,
    string OutputPath,
    int Width,
    int Height,
    int MipCount,
    long AlphaBlocksOverridden,
    string SourceSha256,
    string OutputSha256);

/// <summary>
/// Creates an explicitly derived BC3 viewing copy whose alpha is opaque. RGB colour
/// blocks, dimensions, and mip order are copied byte-for-byte. The source DDS is never
/// modified and the result must not be mistaken for a faithful replacement payload.
/// </summary>
public static class DdsBc3RgbViewWriter
{
    private const uint DdsMagic = 0x20534444;
    private const int HeaderBytes = 128;

    public static async Task<DdsBc3RgbViewResult> CreateAsync(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var source = Path.GetFullPath(sourcePath);
        var output = Path.GetFullPath(outputPath);
        if (source.Equals(output, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The RGB-view DDS must not overwrite its source.", nameof(outputPath));
        }

        var bytes = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        if (bytes.Length < HeaderBytes || ReadUInt32(bytes, 0) != DdsMagic ||
            ReadUInt32(bytes, 4) != 124 || ReadUInt32(bytes, 76) != 32 ||
            Encoding.ASCII.GetString(bytes, 84, 4) != "DXT5")
        {
            throw new NotSupportedException(
                "An RGB-view copy currently requires a legacy DXT5/BC3 DDS with a 128-byte header.");
        }

        var height = checked((int)ReadUInt32(bytes, 12));
        var width = checked((int)ReadUInt32(bytes, 16));
        var mipCount = checked((int)Math.Max(1u, ReadUInt32(bytes, 28)));
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("The source DDS has invalid dimensions.");
        }

        var offset = HeaderBytes;
        long blockCount = 0;
        for (var mip = 0; mip < mipCount; mip++)
        {
            var mipWidth = Math.Max(1, width >> mip);
            var mipHeight = Math.Max(1, height >> mip);
            var blocksWide = Math.Max(1, (mipWidth + 3) / 4);
            var blocksHigh = Math.Max(1, (mipHeight + 3) / 4);
            var levelBlocks = checked(blocksWide * blocksHigh);
            var levelBytes = checked(levelBlocks * 16);
            if (offset > bytes.Length - levelBytes)
            {
                throw new InvalidDataException(
                    $"The source DDS is truncated within mip {mip}; an RGB-view copy was not created.");
            }

            for (var block = 0; block < levelBlocks; block++)
            {
                var alphaOffset = checked(offset + block * 16);
                bytes[alphaOffset] = 255;
                bytes[alphaOffset + 1] = 255;
                bytes.AsSpan(alphaOffset + 2, 6).Clear();
            }

            blockCount += levelBlocks;
            offset += levelBytes;
        }

        if (offset != bytes.Length)
        {
            throw new InvalidDataException(
                $"The source DDS contains {bytes.Length - offset:N0} unrecognized trailing bytes; " +
                "an RGB-view copy was not created.");
        }

        var sourceHash = await HashFileAsync(source, cancellationToken).ConfigureAwait(false);
        await using (var stream = new FileStream(
                         output,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        return new DdsBc3RgbViewResult(
            source,
            output,
            width,
            height,
            mipCount,
            blockCount,
            sourceHash,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
