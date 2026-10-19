using System.Buffers.Binary;
using System.Text;

namespace Arkive.Core.Textures;

public sealed record DdsBlockFormat(string FourCc, int BytesPerBlock, string Name);

public static class DdsBlockCompressedWriter
{
    public const int HeaderBytes = 128;

    private const uint DdsMagic = 0x20534444;
    private const uint HeaderFlags = 0x000A1007;
    private const uint PixelFormatFourCc = 0x4;
    private const uint PixelFormatRgb = 0x40;
    private const uint PixelFormatAlpha = 0x1;
    private const uint CapsTexture = 0x1000;
    private const uint CapsComplex = 0x8;
    private const uint CapsMipmap = 0x400000;

    public static async Task WriteAsync(
        string path,
        int width,
        int height,
        DdsBlockFormat format,
        IReadOnlyList<ReadOnlyMemory<byte>> mipPayloads,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(mipPayloads);
        if (width <= 0 || height <= 0 || mipPayloads.Count == 0)
        {
            throw new ArgumentException("DDS output requires positive dimensions and at least one mip.");
        }

        // Uncompressed BGRA is written with "BGRA" as its label and 4 bytes per pixel instead of a FourCC block format.
        var uncompressed = format.BytesPerBlock == 4;
        if (!uncompressed && (format.FourCc.Length != 4 || format.BytesPerBlock is not (8 or 16)))
        {
            throw new ArgumentException(
                "A block-compressed DDS format requires a four-character code and 8- or 16-byte blocks.",
                nameof(format));
        }

        var maximumMipCount = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
        if (mipPayloads.Count > maximumMipCount)
        {
            throw new ArgumentException(
                $"A {width}x{height} texture supports at most {maximumMipCount} mip levels.");
        }

        for (var index = 0; index < mipPayloads.Count; index++)
        {
            var mipWidth = Math.Max(1, width >> index);
            var mipHeight = Math.Max(1, height >> index);
            var expected = uncompressed
                ? checked(mipWidth * mipHeight * 4)
                : NativeCrunchDecoder.CalculateBlockCompressedBytes(mipWidth, mipHeight, format.BytesPerBlock);
            if (mipPayloads[index].Length != expected)
            {
                throw new InvalidDataException(
                    $"Decoded mip {index} has {mipPayloads[index].Length:N0} bytes; " +
                    $"{format.Name} {mipWidth}x{mipHeight} requires {expected:N0}.");
            }
        }

        var header = new byte[HeaderBytes];
        WriteUInt32(header, 0, DdsMagic);
        WriteUInt32(header, 4, 124);
        WriteUInt32(header, 8, HeaderFlags);
        WriteUInt32(header, 12, checked((uint)height));
        WriteUInt32(header, 16, checked((uint)width));
        WriteUInt32(header, 20, checked((uint)mipPayloads[0].Length));
        WriteUInt32(header, 28, checked((uint)mipPayloads.Count));
        WriteUInt32(header, 76, 32);
        if (uncompressed)
        {
            // An ordinary 32-bit BGRA DDS: pitch instead of linear size, RGB with alpha and its channel masks.
            WriteUInt32(header, 8, 0x0002100F);
            WriteUInt32(header, 20, checked((uint)(width * 4)));
            WriteUInt32(header, 80, PixelFormatRgb | PixelFormatAlpha);
            WriteUInt32(header, 88, 32);
            WriteUInt32(header, 92, 0x00FF0000);
            WriteUInt32(header, 96, 0x0000FF00);
            WriteUInt32(header, 100, 0x000000FF);
            WriteUInt32(header, 104, 0xFF000000);
        }
        else
        {
            WriteUInt32(header, 80, PixelFormatFourCc);
            Encoding.ASCII.GetBytes(format.FourCc, header.AsSpan(84, 4));
        }
        var caps = CapsTexture;
        if (mipPayloads.Count > 1)
        {
            caps |= CapsComplex | CapsMipmap;
        }

        WriteUInt32(header, 108, caps);

        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        foreach (var mip in mipPayloads)
        {
            await stream.WriteAsync(mip, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void WriteUInt32(Span<byte> output, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(output[offset..], value);
}

public static class DdsBc1Writer
{
    public const int HeaderBytes = DdsBlockCompressedWriter.HeaderBytes;

    public static Task WriteAsync(
        string path,
        int width,
        int height,
        IReadOnlyList<ReadOnlyMemory<byte>> mipPayloads,
        CancellationToken cancellationToken = default) =>
        DdsBlockCompressedWriter.WriteAsync(
            path,
            width,
            height,
            NativeTextureFormats.Dxt1.DdsFormat,
            mipPayloads,
            cancellationToken);
}

public static class DdsBc3Writer
{
    public const int HeaderBytes = DdsBlockCompressedWriter.HeaderBytes;

    public static Task WriteAsync(
        string path,
        int width,
        int height,
        IReadOnlyList<ReadOnlyMemory<byte>> mipPayloads,
        CancellationToken cancellationToken = default) =>
        DdsBlockCompressedWriter.WriteAsync(
            path,
            width,
            height,
            NativeTextureFormats.Dxt5.DdsFormat,
            mipPayloads,
            cancellationToken);
}
