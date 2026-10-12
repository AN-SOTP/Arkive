using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Arkive.Core.Textures;

public sealed record RgbaImage(int Width, int Height, byte[] Pixels)
{
    public int Stride => checked(Width * 4);

    public RgbaImage DeepCopy() => new(Width, Height, Pixels.ToArray());

    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || Pixels.Length != checked(Width * Height * 4))
        {
            throw new InvalidDataException(
                $"RGBA image storage does not match {Width}x{Height} dimensions.");
        }
    }
}

/// <summary>
/// Small, dependency-free PNG codec for Arkive's editable texture images. It writes
/// ordinary 8-bit RGBA, non-interlaced PNG and reads the common 8-bit grayscale,
/// RGB, indexed, grayscale-alpha, and RGBA forms emitted by desktop image editors.
/// </summary>
public static class PngRgbaCodec
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static async Task WriteAsync(
        string path,
        RgbaImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(image);
        image.Validate();

        var scanlineBytes = checked(image.Width * 4);
        var filtered = new byte[checked((scanlineBytes + 1) * image.Height)];
        for (var y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = image.Pixels.AsSpan(y * scanlineBytes, scanlineBytes);
            var destination = filtered.AsSpan(y * (scanlineBytes + 1), scanlineBytes + 1);
            destination[0] = 1; // Sub filter: compact and deterministic for texture rows.
            for (var x = 0; x < scanlineBytes; x++)
            {
                var left = x >= 4 ? source[x - 4] : 0;
                destination[x + 1] = unchecked((byte)(source[x] - left));
            }
        }

        byte[] compressed;
        using (var compressedStream = new MemoryStream())
        {
            await using (var zlib = new ZLibStream(
                             compressedStream,
                             CompressionLevel.SmallestSize,
                             leaveOpen: true))
            {
                await zlib.WriteAsync(filtered, cancellationToken).ConfigureAwait(false);
            }

            compressed = compressedStream.ToArray();
        }

        var temporaryPath = Path.GetFullPath(path) + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(Signature, cancellationToken).ConfigureAwait(false);
                var ihdr = new byte[13];
                BinaryPrimitives.WriteUInt32BigEndian(ihdr, checked((uint)image.Width));
                BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), checked((uint)image.Height));
                ihdr[8] = 8;
                ihdr[9] = 6;
                await WriteChunkAsync(stream, "IHDR", ihdr, cancellationToken).ConfigureAwait(false);
                await WriteChunkAsync(stream, "IDAT", compressed, cancellationToken).ConfigureAwait(false);
                await WriteChunkAsync(
                    stream,
                    "IEND",
                    ReadOnlyMemory<byte>.Empty,
                    cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, Path.GetFullPath(path), overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static async Task<RgbaImage> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length < Signature.Length || !bytes.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("The file does not have a PNG signature.");
        }

        int width = 0;
        int height = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        byte interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var idat = new MemoryStream();
        var offset = Signature.Length;
        var sawHeader = false;
        var sawEnd = false;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset > bytes.Length - 12)
            {
                throw new InvalidDataException("PNG ends inside a chunk header.");
            }

            var lengthValue = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (lengthValue > int.MaxValue)
            {
                throw new InvalidDataException("PNG chunk is too large.");
            }

            var length = checked((int)lengthValue);
            var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var dataOffset = checked(offset + 8);
            if (dataOffset > bytes.Length - length - 4)
            {
                throw new InvalidDataException($"PNG {type} chunk is truncated.");
            }

            var data = bytes.AsSpan(dataOffset, length);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(
                bytes.AsSpan(dataOffset + length, 4));
            var actualCrc = ComputeCrc32(bytes.AsSpan(offset + 4, length + 4));
            if (actualCrc != expectedCrc)
            {
                throw new InvalidDataException($"PNG {type} chunk failed its CRC check.");
            }

            switch (type)
            {
                case "IHDR":
                    if (sawHeader || length != 13)
                    {
                        throw new InvalidDataException("PNG has an invalid IHDR chunk.");
                    }

                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                    bitDepth = data[8];
                    colorType = data[9];
                    if (data[10] != 0 || data[11] != 0)
                    {
                        throw new NotSupportedException("PNG uses an unsupported compression or filter method.");
                    }

                    interlace = data[12];
                    sawHeader = true;
                    break;
                case "PLTE":
                    palette = data.ToArray();
                    break;
                case "tRNS":
                    transparency = data.ToArray();
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    sawEnd = true;
                    break;
                default:
                    if (type.Length == 4 && char.IsUpper(type[0]))
                    {
                        throw new NotSupportedException($"PNG uses unknown critical chunk {type}.");
                    }

                    break;
            }

            offset = checked(dataOffset + length + 4);
            if (sawEnd) break;
        }

        if (!sawHeader || !sawEnd || width <= 0 || height <= 0)
        {
            throw new InvalidDataException("PNG is missing a valid IHDR or IEND chunk.");
        }

        if (bitDepth != 8 || interlace != 0 || colorType is not (0 or 2 or 3 or 4 or 6))
        {
            throw new NotSupportedException(
                $"Arkive accepts non-interlaced 8-bit PNG; this file uses bit depth {bitDepth}, " +
                $"colour type {colorType}, interlace {interlace}.");
        }

        var bytesPerPixel = colorType switch
        {
            0 or 3 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => throw new UnreachableException(),
        };
        var stride = checked(width * bytesPerPixel);
        var filteredBytes = checked((stride + 1) * height);
        var filtered = new byte[filteredBytes];
        idat.Position = 0;
        await using (var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
        {
            await zlib.ReadExactlyAsync(filtered, cancellationToken).ConfigureAwait(false);
            if (zlib.ReadByte() != -1)
            {
                throw new InvalidDataException("PNG contains excess decompressed scanline data.");
            }
        }

        var raw = new byte[checked(stride * height)];
        for (var y = 0; y < height; y++)
        {
            var filter = filtered[y * (stride + 1)];
            var source = filtered.AsSpan(y * (stride + 1) + 1, stride);
            var current = raw.AsSpan(y * stride, stride);
            var previous = y == 0 ? Span<byte>.Empty : raw.AsSpan((y - 1) * stride, stride);
            for (var x = 0; x < stride; x++)
            {
                var left = x >= bytesPerPixel ? current[x - bytesPerPixel] : 0;
                var up = y == 0 ? 0 : previous[x];
                var upperLeft = y == 0 || x < bytesPerPixel ? 0 : previous[x - bytesPerPixel];
                current[x] = filter switch
                {
                    0 => source[x],
                    1 => unchecked((byte)(source[x] + left)),
                    2 => unchecked((byte)(source[x] + up)),
                    3 => unchecked((byte)(source[x] + ((left + up) >> 1))),
                    4 => unchecked((byte)(source[x] + Paeth(left, up, upperLeft))),
                    _ => throw new InvalidDataException($"PNG scanline {y} uses invalid filter {filter}."),
                };
            }
        }

        var rgba = new byte[checked(width * height * 4)];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var input = pixel * bytesPerPixel;
            var output = pixel * 4;
            switch (colorType)
            {
                case 0:
                    rgba[output] = rgba[output + 1] = rgba[output + 2] = raw[input];
                    rgba[output + 3] = 255;
                    break;
                case 2:
                    rgba[output] = raw[input];
                    rgba[output + 1] = raw[input + 1];
                    rgba[output + 2] = raw[input + 2];
                    rgba[output + 3] = 255;
                    break;
                case 3:
                    if (palette is null || palette.Length % 3 != 0)
                    {
                        throw new InvalidDataException("Indexed PNG is missing a valid palette.");
                    }

                    var index = raw[input];
                    if (index >= palette.Length / 3)
                    {
                        throw new InvalidDataException("Indexed PNG references a palette entry that does not exist.");
                    }

                    rgba[output] = palette[index * 3];
                    rgba[output + 1] = palette[index * 3 + 1];
                    rgba[output + 2] = palette[index * 3 + 2];
                    rgba[output + 3] = transparency is not null && index < transparency.Length
                        ? transparency[index]
                        : (byte)255;
                    break;
                case 4:
                    rgba[output] = rgba[output + 1] = rgba[output + 2] = raw[input];
                    rgba[output + 3] = raw[input + 1];
                    break;
                case 6:
                    raw.AsSpan(input, 4).CopyTo(rgba.AsSpan(output, 4));
                    break;
            }
        }

        return new RgbaImage(width, height, rgba);
    }

    private static async Task WriteChunkAsync(
        Stream stream,
        string type,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)data.Length));
        Encoding.ASCII.GetBytes(type, header.AsSpan(4, 4));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        var crcInput = new byte[checked(4 + data.Length)];
        header.AsSpan(4, 4).CopyTo(crcInput);
        data.Span.CopyTo(crcInput.AsSpan(4));
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, ComputeCrc32(crcInput));
        await stream.WriteAsync(crc, cancellationToken).ConfigureAwait(false);
    }

    private static int Paeth(int left, int up, int upperLeft)
    {
        var estimate = left + up - upperLeft;
        var leftDistance = Math.Abs(estimate - left);
        var upDistance = Math.Abs(estimate - up);
        var upperLeftDistance = Math.Abs(estimate - upperLeft);
        return leftDistance <= upDistance && leftDistance <= upperLeftDistance
            ? left
            : upDistance <= upperLeftDistance ? up : upperLeft;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
