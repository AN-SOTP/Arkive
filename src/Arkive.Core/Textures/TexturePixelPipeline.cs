using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Arkive.Core.Textures;

public enum BlockTextureFormat
{
    Bc1,
    Bc3,
    Bc5,
}

public enum TextureEditSemantic
{
    Colour,
    NormalXY,
}

public sealed record EditableTextureImageResult(
    string SourceDdsPath,
    string OutputPngPath,
    int Width,
    int Height,
    BlockTextureFormat Format,
    TextureEditSemantic Semantic,
    long OutputBytes,
    string OutputSha256);

public sealed record LoadedEditableTextureImage(
    string Path,
    string SourceFormat,
    int DeclaredMipCount,
    RgbaImage Image,
    string Sha256);

public sealed record TextureBlockRebuildResult(
    byte[] DdsBytes,
    long TotalBlocks,
    long DirtyBlocks,
    long TouchedBlocks,
    long UnchangedBlocksCopied,
    long CandidateBlocksDifferentFromOriginal,
    long EditedPixels,
    long EditedAlphaBlocks,
    string AlphaAction,
    TextureEditSemantic Semantic);

public sealed record DdsBlockMip(
    int Index,
    int Width,
    int Height,
    int Offset,
    int Length);

public sealed class DdsBlockTexture
{
    private const uint DdsMagic = 0x20534444;

    private DdsBlockTexture(
        string path,
        byte[] bytes,
        int width,
        int height,
        int mipCount,
        string fourCc,
        BlockTextureFormat format,
        int bytesPerBlock,
        IReadOnlyList<DdsBlockMip> mips)
    {
        Path = path;
        Bytes = bytes;
        Width = width;
        Height = height;
        MipCount = mipCount;
        FourCc = fourCc;
        Format = format;
        BytesPerBlock = bytesPerBlock;
        Mips = mips;
    }

    public string Path { get; }
    public byte[] Bytes { get; }
    public int Width { get; }
    public int Height { get; }
    public int MipCount { get; }
    public string FourCc { get; }
    public BlockTextureFormat Format { get; }
    public int BytesPerBlock { get; }
    public IReadOnlyList<DdsBlockMip> Mips { get; }

    public static async Task<DdsBlockTexture> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return Parse(fullPath, bytes);
    }

    public static DdsBlockTexture Parse(string path, byte[] bytes)
    {
        if (bytes.Length < DdsBlockCompressedWriter.HeaderBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != DdsMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 124 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(76)) != 32)
        {
            throw new InvalidDataException("A legacy 128-byte DDS header is required.");
        }

        var width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        var height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        var mipCount = checked((int)Math.Max(
            1u,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28))));
        var depth = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24));
        var caps2 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(112));
        var fourCc = Encoding.ASCII.GetString(bytes, 84, 4).TrimEnd('\0', ' ');
        if (width <= 0 || height <= 0 || depth > 1 || caps2 != 0)
        {
            throw new NotSupportedException(
                "Only ordinary two-dimensional, non-array, non-cubemap DDS files are supported.");
        }

        var (format, bytesPerBlock) = fourCc.ToUpperInvariant() switch
        {
            "DXT1" => (BlockTextureFormat.Bc1, 8),
            "DXT5" => (BlockTextureFormat.Bc3, 16),
            "ATI2" or "BC5U" => (BlockTextureFormat.Bc5, 16),
            _ => throw new NotSupportedException(
                $"DDS FourCC '{fourCc}' is not a supported BC1, BC3, or BC5 format."),
        };
        var maximumMipCount = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
        if (mipCount > maximumMipCount)
        {
            throw new InvalidDataException(
                $"DDS declares {mipCount} mips, but {width}x{height} supports at most {maximumMipCount}.");
        }

        var offset = DdsBlockCompressedWriter.HeaderBytes;
        var mips = new List<DdsBlockMip>(mipCount);
        for (var index = 0; index < mipCount; index++)
        {
            var mipWidth = Math.Max(1, width >> index);
            var mipHeight = Math.Max(1, height >> index);
            var length = NativeCrunchDecoder.CalculateBlockCompressedBytes(
                mipWidth,
                mipHeight,
                bytesPerBlock);
            if (offset > bytes.Length - length)
            {
                throw new InvalidDataException(
                    $"DDS is truncated within mip {index}; expected {length:N0} compressed bytes.");
            }

            mips.Add(new DdsBlockMip(index, mipWidth, mipHeight, offset, length));
            offset = checked(offset + length);
        }

        if (offset != bytes.Length)
        {
            throw new InvalidDataException(
                $"DDS contains {bytes.Length - offset:N0} bytes after its declared mip chain.");
        }

        return new DdsBlockTexture(
            path,
            bytes,
            width,
            height,
            mipCount,
            fourCc,
            format,
            bytesPerBlock,
            mips);
    }

    public RgbaImage DecodeMip(int mipIndex, bool editableView)
    {
        var mip = Mips[mipIndex];
        var pixels = new byte[checked(mip.Width * mip.Height * 4)];
        var blocksWide = Math.Max(1, (mip.Width + 3) / 4);
        var blocksHigh = Math.Max(1, (mip.Height + 3) / 4);
        Span<byte> blockPixels = stackalloc byte[64];
        for (var blockY = 0; blockY < blocksHigh; blockY++)
        {
            for (var blockX = 0; blockX < blocksWide; blockX++)
            {
                var blockOffset = mip.Offset + (blockY * blocksWide + blockX) * BytesPerBlock;
                var block = Bytes.AsSpan(blockOffset, BytesPerBlock);
                switch (Format)
                {
                    case BlockTextureFormat.Bc1:
                        BlockCompression.DecodeBc1(block, blockPixels, allowTransparency: true);
                        break;
                    case BlockTextureFormat.Bc3:
                        BlockCompression.DecodeBc3(block, blockPixels);
                        break;
                    case BlockTextureFormat.Bc5:
                        BlockCompression.DecodeBc5(block, blockPixels);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                for (var y = 0; y < 4; y++)
                {
                    var outputY = blockY * 4 + y;
                    if (outputY >= mip.Height) break;
                    for (var x = 0; x < 4; x++)
                    {
                        var outputX = blockX * 4 + x;
                        if (outputX >= mip.Width) break;
                        var source = (y * 4 + x) * 4;
                        var destination = (outputY * mip.Width + outputX) * 4;
                        blockPixels.Slice(source, 4).CopyTo(pixels.AsSpan(destination, 4));
                        if (editableView) pixels[destination + 3] = 255;
                    }
                }
            }
        }

        return new RgbaImage(mip.Width, mip.Height, pixels);
    }
}

public static class TextureEditableImageService
{
    public static async Task<EditableTextureImageResult> CreatePngAsync(
        string sourceDdsPath,
        string outputPngPath,
        string objectName,
        CancellationToken cancellationToken = default)
    {
        var texture = await DdsBlockTexture.ReadAsync(sourceDdsPath, cancellationToken)
            .ConfigureAwait(false);
        var semantic = InferSemantic(texture.Format, objectName);
        var image = texture.DecodeMip(0, editableView: true);
        await PngRgbaCodec.WriteAsync(outputPngPath, image, cancellationToken).ConfigureAwait(false);
        var file = new FileInfo(outputPngPath);
        return new EditableTextureImageResult(
            texture.Path,
            file.FullName,
            image.Width,
            image.Height,
            texture.Format,
            semantic,
            file.Length,
            await HashFileAsync(file.FullName, cancellationToken).ConfigureAwait(false));
    }

    public static async Task<LoadedEditableTextureImage> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        RgbaImage image;
        string format;
        int mipCount;
        switch (extension)
        {
            case ".png":
                image = await PngRgbaCodec.ReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
                format = "PNG RGBA8";
                mipCount = 1;
                break;
            case ".dds":
                (image, format, mipCount) = await ReadDdsTopMipAsync(fullPath, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException(
                    $"Edited texture input must be PNG or DDS; received {extension}.");
        }

        image.Validate();
        return new LoadedEditableTextureImage(
            fullPath,
            format,
            mipCount,
            image,
            await HashFileAsync(fullPath, cancellationToken).ConfigureAwait(false));
    }

    public static TextureEditSemantic InferSemantic(BlockTextureFormat format, string objectName)
    {
        _ = objectName;
        return format == BlockTextureFormat.Bc5
            ? TextureEditSemantic.NormalXY
            : TextureEditSemantic.Colour;
    }

    private static async Task<(RgbaImage Image, string Format, int MipCount)> ReadDdsTopMipAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x20534444 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 124 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(76)) != 32)
        {
            throw new InvalidDataException("Edited DDS has an invalid legacy header.");
        }

        var width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        var height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        var mipCount = checked((int)Math.Max(
            1u,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28))));
        var pixelFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80));
        var fourCc = Encoding.ASCII.GetString(bytes, 84, 4).TrimEnd('\0', ' ');
        if ((pixelFlags & 0x4) != 0 && fourCc is "DXT1" or "DXT5" or "ATI2" or "BC5U")
        {
            var texture = await DdsBlockTexture.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            return (texture.DecodeMip(0, editableView: false), $"DDS {texture.FourCc}", texture.MipCount);
        }

        if ((pixelFlags & 0x40) == 0)
        {
            throw new NotSupportedException(
                $"Edited DDS uses unsupported pixel flags 0x{pixelFlags:X8} and FourCC '{fourCc}'.");
        }

        var bits = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(88)));
        if (bits is not (24 or 32))
        {
            throw new NotSupportedException($"Edited RGB DDS uses unsupported {bits}-bit pixels.");
        }

        var bytesPerPixel = bits / 8;
        var minimumPitch = checked(width * bytesPerPixel);
        var declaredPitch = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)));
        var pitch = declaredPitch >= minimumPitch ? declaredPitch : minimumPitch;
        if (width <= 0 || height <= 0 || 128L + (long)pitch * height > bytes.Length)
        {
            throw new InvalidDataException("Edited RGB DDS top mip is truncated or has invalid dimensions.");
        }

        var redMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(92));
        var greenMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(96));
        var blueMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(100));
        var alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(104));
        if (redMask == 0 || greenMask == 0 || blueMask == 0)
        {
            throw new NotSupportedException("Edited RGB DDS does not declare usable RGB channel masks.");
        }

        var output = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var source = 128 + y * pitch + x * bytesPerPixel;
                uint packed = bytesPerPixel == 4
                    ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(source, 4))
                    : (uint)(bytes[source] | bytes[source + 1] << 8 | bytes[source + 2] << 16);
                var destination = (y * width + x) * 4;
                output[destination] = ExtractMasked(packed, redMask);
                output[destination + 1] = ExtractMasked(packed, greenMask);
                output[destination + 2] = ExtractMasked(packed, blueMask);
                output[destination + 3] = alphaMask == 0 ? (byte)255 : ExtractMasked(packed, alphaMask);
            }
        }

        return (new RgbaImage(width, height, output), $"DDS uncompressed RGB{bits}", mipCount);
    }

    private static byte ExtractMasked(uint packed, uint mask)
    {
        var shift = BitOperations.TrailingZeroCount(mask);
        var maximum = mask >> shift;
        var value = (packed & mask) >> shift;
        return checked((byte)((value * 255u + maximum / 2u) / maximum));
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}

public static class TextureBlockRebuilder
{
    private const int EditorRoundTripTolerance = 2;

    /// <summary>
    /// Returns <paramref name="original"/> with <paramref name="levels"/> larger mips added on top, so an
    /// upscaled edit can be rebuilt against it: the original mips become the lower levels byte-for-byte, and
    /// each new level is a nearest-neighbour enlargement of the original top mip (alpha included).
    /// </summary>
    public static DdsBlockTexture AddUpscaledTopMips(DdsBlockTexture original, int levels)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 1);
        var top = original.DecodeMip(0, editableView: false);
        var width = original.Width << levels;
        var height = original.Height << levels;
        var newMips = new List<byte[]>(levels);
        for (var level = 0; level < levels; level++)
        {
            var scale = 1 << (levels - level);
            newMips.Add(EncodeBlocks(NearestUpscale(top, scale), original.Format, original.BytesPerBlock));
        }

        var header = original.Bytes.AsSpan(0, DdsBlockCompressedWriter.HeaderBytes).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), checked((uint)newMips[0].Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), checked((uint)(original.MipCount + levels)));
        var bytes = new byte[header.Length + newMips.Sum(mip => mip.Length) +
                             original.Bytes.Length - DdsBlockCompressedWriter.HeaderBytes];
        var offset = 0;
        foreach (var part in newMips.Prepend(header))
        {
            part.CopyTo(bytes, offset);
            offset += part.Length;
        }
        original.Bytes.AsSpan(DdsBlockCompressedWriter.HeaderBytes).CopyTo(bytes.AsSpan(offset));
        return DdsBlockTexture.Parse(original.Path, bytes);
    }

    private static RgbaImage NearestUpscale(RgbaImage source, int scale)
    {
        var width = source.Width * scale;
        var height = source.Height * scale;
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            source.Pixels.AsSpan(((y / scale) * source.Width + x / scale) * 4, 4)
                .CopyTo(pixels.AsSpan((y * width + x) * 4, 4));
        return new RgbaImage(width, height, pixels);
    }

    private static byte[] EncodeBlocks(RgbaImage image, BlockTextureFormat format, int bytesPerBlock)
    {
        var blocksWide = Math.Max(1, (image.Width + 3) / 4);
        var blocksHigh = Math.Max(1, (image.Height + 3) / 4);
        var output = new byte[blocksWide * blocksHigh * bytesPerBlock];
        Span<byte> pixels = stackalloc byte[64];
        for (var blockY = 0; blockY < blocksHigh; blockY++)
        for (var blockX = 0; blockX < blocksWide; blockX++)
        {
            CopyPixelBlock(image, blockX, blockY, pixels);
            var block = output.AsSpan((blockY * blocksWide + blockX) * bytesPerBlock, bytesPerBlock);
            switch (format)
            {
                case BlockTextureFormat.Bc1:
                    BlockCompression.EncodeBc1(pixels, block, allowTransparency: true);
                    break;
                case BlockTextureFormat.Bc3:
                    BlockCompression.EncodeBc4Channel(pixels, 3, block[..8]);
                    BlockCompression.EncodeBc1(pixels, block[8..], allowTransparency: false);
                    break;
                case BlockTextureFormat.Bc5:
                    BlockCompression.EncodeBc4Channel(pixels, 0, block[..8]);
                    BlockCompression.EncodeBc4Channel(pixels, 1, block[8..]);
                    break;
            }
        }
        return output;
    }

    public static TextureBlockRebuildResult TransplantExactDds(
        DdsBlockTexture original,
        DdsBlockTexture edited,
        string objectName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        if (original.Format != edited.Format || original.Width != edited.Width ||
            original.Height != edited.Height || original.MipCount != edited.MipCount)
            throw new InvalidDataException(
                "Exact DDS transplant requires identical format, dimensions, and mip count.");
        var candidate = original.Bytes.ToArray();
        long totalBlocks = 0;
        long changedBlocks = 0;
        for (var mipIndex = 0; mipIndex < original.MipCount; mipIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetMip = original.Mips[mipIndex];
            var sourceMip = edited.Mips[mipIndex];
            if (targetMip.Width != sourceMip.Width || targetMip.Height != sourceMip.Height ||
                targetMip.Length != sourceMip.Length)
                throw new InvalidDataException($"Edited DDS mip {mipIndex} has an incompatible layout.");
            var target = candidate.AsSpan(targetMip.Offset, targetMip.Length);
            var source = edited.Bytes.AsSpan(sourceMip.Offset, sourceMip.Length);
            var originalBytes = original.Bytes.AsSpan(targetMip.Offset, targetMip.Length);
            var blockCount = targetMip.Length / original.BytesPerBlock;
            totalBlocks += blockCount;
            for (var block = 0; block < blockCount; block++)
                if (!originalBytes.Slice(block * original.BytesPerBlock, original.BytesPerBlock)
                        .SequenceEqual(source.Slice(block * original.BytesPerBlock, original.BytesPerBlock)))
                    changedBlocks++;
            source.CopyTo(target);
        }
        var originalTop = original.DecodeMip(0, editableView: true);
        var editedTop = edited.DecodeMip(0, editableView: true);
        var colourDirty = ComparePixels(originalTop, editedTop, original.Format, compareAlpha: false);
        var alphaDirty = CompareAlpha(
            original.DecodeMip(0, editableView: false),
            edited.DecodeMip(0, editableView: false));
        var semantic = TextureEditableImageService.InferSemantic(original.Format, objectName);
        var alphaAction = original.Format switch
        {
            BlockTextureFormat.Bc3 =>
                "Imported BC3 alpha and colour blocks directly from the compatible edited DDS.",
            BlockTextureFormat.Bc1 =>
                "Imported BC1 colour and one-bit transparency blocks directly from the compatible edited DDS.",
            _ => "Imported BC5 normal/data blocks directly from the compatible edited DDS.",
        };
        return new TextureBlockRebuildResult(
            candidate,
            totalBlocks,
            changedBlocks,
            totalBlocks,
            0,
            changedBlocks,
            colourDirty.LongCount(value => value),
            alphaDirty.LongCount(value => value),
            alphaAction,
            semantic);
    }

    public static TextureBlockRebuildResult Rebuild(
        DdsBlockTexture original,
        RgbaImage editedTopMip,
        string objectName,
        TextureImportAlphaPolicy alphaPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(editedTopMip);
        editedTopMip.Validate();
        if (editedTopMip.Width != original.Width || editedTopMip.Height != original.Height)
        {
            throw new InvalidDataException(
                $"Edited image is {editedTopMip.Width}x{editedTopMip.Height}; " +
                $"the texture requires {original.Width}x{original.Height}.");
        }

        var semantic = TextureEditableImageService.InferSemantic(original.Format, objectName);
        var originalEditTop = original.DecodeMip(0, editableView: true);
        var colourDirty = ComparePixels(originalEditTop, editedTopMip, original.Format, compareAlpha: false);
        var alphaDirty = CompareAlpha(original.DecodeMip(0, editableView: false), editedTopMip);
        long editedPixels = colourDirty.Count(value => value);
        var editedMips = new List<RgbaImage>(original.MipCount) { editedTopMip.DeepCopy() };
        var colourDirtyMips = new List<bool[]>(original.MipCount) { colourDirty };
        var alphaDirtyMips = new List<bool[]>(original.MipCount) { alphaDirty };
        for (var mip = 1; mip < original.MipCount; mip++)
        {
            editedMips.Add(Downsample(editedMips[^1], semantic));
            colourDirtyMips.Add(DownsampleDirty(
                colourDirtyMips[^1],
                editedMips[mip - 1].Width,
                editedMips[mip - 1].Height,
                editedMips[mip].Width,
                editedMips[mip].Height));
            alphaDirtyMips.Add(DownsampleDirty(
                alphaDirtyMips[^1],
                editedMips[mip - 1].Width,
                editedMips[mip - 1].Height,
                editedMips[mip].Width,
                editedMips[mip].Height));
        }

        var candidate = original.Bytes.ToArray();
        long totalBlocks = 0;
        long dirtyBlocks = 0;
        long touchedBlocks = 0;
        long changedBlocks = 0;
        long editedAlphaBlocks = 0;
        var blockPixelsBuffer = new byte[64];
        var originalPixelBlockBuffer = new byte[64];
        for (var mipIndex = 0; mipIndex < original.MipCount; mipIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mip = original.Mips[mipIndex];
            var edited = editedMips[mipIndex];
            var originalPixels = original.DecodeMip(mipIndex, editableView: false);
            var blocksWide = Math.Max(1, (mip.Width + 3) / 4);
            var blocksHigh = Math.Max(1, (mip.Height + 3) / 4);
            var blockPixels = blockPixelsBuffer.AsSpan();
            var originalPixelBlock = originalPixelBlockBuffer.AsSpan();
            for (var blockY = 0; blockY < blocksHigh; blockY++)
            {
                for (var blockX = 0; blockX < blocksWide; blockX++)
                {
                    totalBlocks++;
                    var colourChanged = BlockHasDirtyPixel(
                        colourDirtyMips[mipIndex], mip.Width, mip.Height, blockX, blockY);
                    var alphaChanged = BlockHasDirtyPixel(
                        alphaDirtyMips[mipIndex], mip.Width, mip.Height, blockX, blockY);
                    if (colourChanged) dirtyBlocks++;
                    if (alphaChanged) editedAlphaBlocks++;
                    var offset = mip.Offset + (blockY * blocksWide + blockX) * original.BytesPerBlock;
                    var sourceBlock = original.Bytes.AsSpan(offset, original.BytesPerBlock);
                    var destinationBlock = candidate.AsSpan(offset, original.BytesPerBlock);
                    var touched = false;

                    CopyPixelBlock(edited, blockX, blockY, blockPixels);
                    CopyPixelBlock(originalPixels, blockX, blockY, originalPixelBlock);
                    switch (original.Format)
                    {
                        case BlockTextureFormat.Bc1:
                            if (colourChanged ||
                                (alphaPolicy == TextureImportAlphaPolicy.UseEdited && alphaChanged) ||
                                alphaPolicy == TextureImportAlphaPolicy.ForceOpaque)
                            {
                                if (alphaPolicy == TextureImportAlphaPolicy.PreserveOriginal)
                                {
                                    for (var pixel = 0; pixel < 16; pixel++)
                                    {
                                        blockPixels[pixel * 4 + 3] = originalPixelBlock[pixel * 4 + 3];
                                    }
                                }
                                else if (alphaPolicy == TextureImportAlphaPolicy.ForceOpaque)
                                {
                                    for (var pixel = 0; pixel < 16; pixel++) blockPixels[pixel * 4 + 3] = 255;
                                }

                                BlockCompression.EncodeBc1(blockPixels, destinationBlock, allowTransparency: true);
                                touched = true;
                            }

                            break;
                        case BlockTextureFormat.Bc3:
                            if (colourChanged)
                            {
                                BlockCompression.EncodeBc1(
                                    blockPixels,
                                    destinationBlock[8..],
                                    allowTransparency: false);
                                touched = true;
                            }

                            if (alphaPolicy == TextureImportAlphaPolicy.UseEdited && alphaChanged)
                            {
                                BlockCompression.EncodeBc4Channel(blockPixels, 3, destinationBlock[..8]);
                                touched = true;
                            }
                            else if (alphaPolicy == TextureImportAlphaPolicy.ForceOpaque)
                            {
                                destinationBlock[0] = 255;
                                destinationBlock[1] = 255;
                                destinationBlock[2..8].Clear();
                                touched = true;
                            }

                            break;
                        case BlockTextureFormat.Bc5:
                            if (colourChanged)
                            {
                                BlockCompression.EncodeBc4Channel(blockPixels, 0, destinationBlock[..8]);
                                BlockCompression.EncodeBc4Channel(blockPixels, 1, destinationBlock[8..]);
                                touched = true;
                            }

                            break;
                    }

                    if (touched) touchedBlocks++;
                    if (!sourceBlock.SequenceEqual(destinationBlock)) changedBlocks++;
                }
            }
        }

        var alphaAction = original.Format switch
        {
            BlockTextureFormat.Bc3 when alphaPolicy == TextureImportAlphaPolicy.PreserveOriginal =>
                "Original BC3 alpha blocks were copied byte-for-byte; edited RGB was normalized and encoded automatically.",
            BlockTextureFormat.Bc3 when alphaPolicy == TextureImportAlphaPolicy.UseEdited =>
                "Edited alpha was mipmapped and encoded with edited RGB.",
            BlockTextureFormat.Bc3 => "BC3 alpha was forced fully opaque.",
            BlockTextureFormat.Bc1 when alphaPolicy == TextureImportAlphaPolicy.PreserveOriginal =>
                "Original BC1 one-bit transparency was preserved while changed colour blocks were encoded.",
            BlockTextureFormat.Bc1 when alphaPolicy == TextureImportAlphaPolicy.UseEdited =>
                "Edited one-bit transparency was encoded with changed BC1 colour blocks.",
            BlockTextureFormat.Bc1 => "BC1 was encoded as fully opaque.",
            _ => "BC5 stores two normal/data channels and has no alpha channel.",
        };
        return new TextureBlockRebuildResult(
            candidate,
            totalBlocks,
            dirtyBlocks,
            touchedBlocks,
            totalBlocks - touchedBlocks,
            changedBlocks,
            editedPixels,
            editedAlphaBlocks,
            alphaAction,
            semantic);
    }

    private static bool[] ComparePixels(
        RgbaImage original,
        RgbaImage edited,
        BlockTextureFormat format,
        bool compareAlpha)
    {
        var dirty = new bool[checked(original.Width * original.Height)];
        for (var pixel = 0; pixel < dirty.Length; pixel++)
        {
            var offset = pixel * 4;
            dirty[pixel] = format == BlockTextureFormat.Bc5
                ? ChannelChanged(original.Pixels[offset], edited.Pixels[offset]) ||
                  ChannelChanged(original.Pixels[offset + 1], edited.Pixels[offset + 1])
                : ChannelChanged(original.Pixels[offset], edited.Pixels[offset]) ||
                  ChannelChanged(original.Pixels[offset + 1], edited.Pixels[offset + 1]) ||
                  ChannelChanged(original.Pixels[offset + 2], edited.Pixels[offset + 2]);
            if (compareAlpha)
            {
                dirty[pixel] |= ChannelChanged(original.Pixels[offset + 3], edited.Pixels[offset + 3]);
            }
        }

        return dirty;
    }

    private static bool[] CompareAlpha(RgbaImage original, RgbaImage edited)
    {
        var dirty = new bool[checked(original.Width * original.Height)];
        for (var pixel = 0; pixel < dirty.Length; pixel++)
        {
            dirty[pixel] = ChannelChanged(
                original.Pixels[pixel * 4 + 3],
                edited.Pixels[pixel * 4 + 3]);
        }

        return dirty;
    }

    private static bool ChannelChanged(byte original, byte edited) =>
        Math.Abs(original - edited) > EditorRoundTripTolerance;

    private static RgbaImage Downsample(RgbaImage source, TextureEditSemantic semantic)
    {
        var width = Math.Max(1, source.Width / 2);
        var height = Math.Max(1, source.Height / 2);
        var output = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var count = 0;
                var red = 0d;
                var green = 0d;
                var blue = 0d;
                var alpha = 0d;
                for (var dy = 0; dy < 2; dy++)
                {
                    var sourceY = y * 2 + dy;
                    if (sourceY >= source.Height) continue;
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var sourceX = x * 2 + dx;
                        if (sourceX >= source.Width) continue;
                        var offset = (sourceY * source.Width + sourceX) * 4;
                        if (semantic == TextureEditSemantic.NormalXY)
                        {
                            var nx = source.Pixels[offset] / 127.5d - 1d;
                            var ny = source.Pixels[offset + 1] / 127.5d - 1d;
                            var nz = Math.Sqrt(Math.Max(0d, 1d - nx * nx - ny * ny));
                            red += nx;
                            green += ny;
                            blue += nz;
                        }
                        else
                        {
                            red += source.Pixels[offset];
                            green += source.Pixels[offset + 1];
                            blue += source.Pixels[offset + 2];
                        }

                        alpha += source.Pixels[offset + 3];
                        count++;
                    }
                }

                var destination = (y * width + x) * 4;
                if (semantic == TextureEditSemantic.NormalXY)
                {
                    var length = Math.Sqrt(red * red + green * green + blue * blue);
                    if (length <= double.Epsilon)
                    {
                        red = green = 0;
                        blue = 1;
                    }
                    else
                    {
                        red /= length;
                        green /= length;
                        blue /= length;
                    }

                    output[destination] = ToNormalByte(red);
                    output[destination + 1] = ToNormalByte(green);
                    output[destination + 2] = ToNormalByte(blue);
                }
                else
                {
                    output[destination] = checked((byte)Math.Clamp(Math.Round(red / count), 0, 255));
                    output[destination + 1] = checked((byte)Math.Clamp(Math.Round(green / count), 0, 255));
                    output[destination + 2] = checked((byte)Math.Clamp(Math.Round(blue / count), 0, 255));
                }

                output[destination + 3] = checked((byte)Math.Clamp(Math.Round(alpha / count), 0, 255));
            }
        }

        return new RgbaImage(width, height, output);
    }

    private static byte ToNormalByte(double value) =>
        checked((byte)Math.Clamp(Math.Round((value * 0.5d + 0.5d) * 255d), 0, 255));

    private static bool[] DownsampleDirty(
        bool[] source,
        int sourceWidth,
        int sourceHeight,
        int width,
        int height)
    {
        var output = new bool[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dirty = false;
                for (var dy = 0; dy < 2 && !dirty; dy++)
                {
                    var sourceY = y * 2 + dy;
                    if (sourceY >= sourceHeight) continue;
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var sourceX = x * 2 + dx;
                        if (sourceX < sourceWidth && source[sourceY * sourceWidth + sourceX])
                        {
                            dirty = true;
                            break;
                        }
                    }
                }

                output[y * width + x] = dirty;
            }
        }

        return output;
    }

    private static bool BlockHasDirtyPixel(
        bool[] dirty,
        int width,
        int height,
        int blockX,
        int blockY)
    {
        for (var y = 0; y < 4; y++)
        {
            var pixelY = blockY * 4 + y;
            if (pixelY >= height) break;
            for (var x = 0; x < 4; x++)
            {
                var pixelX = blockX * 4 + x;
                if (pixelX < width && dirty[pixelY * width + pixelX]) return true;
            }
        }

        return false;
    }

    private static void CopyPixelBlock(RgbaImage image, int blockX, int blockY, Span<byte> output)
    {
        for (var y = 0; y < 4; y++)
        {
            var pixelY = Math.Min(image.Height - 1, blockY * 4 + y);
            for (var x = 0; x < 4; x++)
            {
                var pixelX = Math.Min(image.Width - 1, blockX * 4 + x);
                image.Pixels.AsSpan((pixelY * image.Width + pixelX) * 4, 4)
                    .CopyTo(output.Slice((y * 4 + x) * 4, 4));
            }
        }
    }
}

internal static class BlockCompression
{
    public static void DecodeBc1(
        ReadOnlySpan<byte> block,
        Span<byte> rgba,
        bool allowTransparency)
    {
        var color0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var color1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        Span<byte> palette = stackalloc byte[16];
        BuildColourPalette(color0, color1, palette, allowTransparency);
        var indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        for (var pixel = 0; pixel < 16; pixel++)
        {
            var index = (int)((indices >> (pixel * 2)) & 3);
            palette.Slice(index * 4, 4).CopyTo(rgba.Slice(pixel * 4, 4));
        }
    }

    public static void DecodeBc3(ReadOnlySpan<byte> block, Span<byte> rgba)
    {
        DecodeBc1(block[8..], rgba, allowTransparency: false);
        Span<byte> alpha = stackalloc byte[16];
        DecodeBc4(block[..8], alpha);
        for (var pixel = 0; pixel < 16; pixel++) rgba[pixel * 4 + 3] = alpha[pixel];
    }

    public static void DecodeBc5(ReadOnlySpan<byte> block, Span<byte> rgba)
    {
        Span<byte> red = stackalloc byte[16];
        Span<byte> green = stackalloc byte[16];
        DecodeBc4(block[..8], red);
        DecodeBc4(block[8..], green);
        for (var pixel = 0; pixel < 16; pixel++)
        {
            var offset = pixel * 4;
            rgba[offset] = red[pixel];
            rgba[offset + 1] = green[pixel];
            var nx = red[pixel] / 127.5d - 1d;
            var ny = green[pixel] / 127.5d - 1d;
            var nz = Math.Sqrt(Math.Max(0d, 1d - nx * nx - ny * ny));
            rgba[offset + 2] = checked((byte)Math.Clamp(
                Math.Round((nz * 0.5d + 0.5d) * 255d), 0, 255));
            rgba[offset + 3] = 255;
        }
    }

    public static void EncodeBc1(
        ReadOnlySpan<byte> rgba,
        Span<byte> output,
        bool allowTransparency)
    {
        var hasTransparency = false;
        var opaqueCount = 0;
        var minR = 255;
        var minG = 255;
        var minB = 255;
        var maxR = 0;
        var maxG = 0;
        var maxB = 0;
        for (var pixel = 0; pixel < 16; pixel++)
        {
            var offset = pixel * 4;
            if (allowTransparency && rgba[offset + 3] < 128)
            {
                hasTransparency = true;
                continue;
            }

            opaqueCount++;
            minR = Math.Min(minR, rgba[offset]);
            minG = Math.Min(minG, rgba[offset + 1]);
            minB = Math.Min(minB, rgba[offset + 2]);
            maxR = Math.Max(maxR, rgba[offset]);
            maxG = Math.Max(maxG, rgba[offset + 1]);
            maxB = Math.Max(maxB, rgba[offset + 2]);
        }

        if (opaqueCount == 0)
        {
            output.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(output[4..], 0xFFFFFFFFu);
            return;
        }

        var endpointMax = PackRgb565(maxR, maxG, maxB);
        var endpointMin = PackRgb565(minR, minG, minB);
        ushort color0;
        ushort color1;
        if (hasTransparency)
        {
            color0 = Math.Min(endpointMax, endpointMin);
            color1 = Math.Max(endpointMax, endpointMin);
            if (color0 == color1)
            {
                if (color1 < ushort.MaxValue) color1++;
                else if (color0 > 0) color0--;
            }
        }
        else
        {
            color0 = Math.Max(endpointMax, endpointMin);
            color1 = Math.Min(endpointMax, endpointMin);
            if (color0 == color1)
            {
                if (color0 < ushort.MaxValue) color0++;
                else if (color1 > 0) color1--;
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(output, color0);
        BinaryPrimitives.WriteUInt16LittleEndian(output[2..], color1);
        Span<byte> palette = stackalloc byte[16];
        BuildColourPalette(color0, color1, palette, allowTransparency: hasTransparency);
        uint indices = 0;
        var paletteCount = hasTransparency ? 3 : 4;
        for (var pixel = 0; pixel < 16; pixel++)
        {
            var offset = pixel * 4;
            var best = 0;
            if (hasTransparency && rgba[offset + 3] < 128)
            {
                best = 3;
            }
            else
            {
                var bestDistance = int.MaxValue;
                for (var index = 0; index < paletteCount; index++)
                {
                    var paletteOffset = index * 4;
                    var dr = rgba[offset] - palette[paletteOffset];
                    var dg = rgba[offset + 1] - palette[paletteOffset + 1];
                    var db = rgba[offset + 2] - palette[paletteOffset + 2];
                    var distance = dr * dr + dg * dg + db * db;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = index;
                    }
                }
            }

            indices |= (uint)best << (pixel * 2);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output[4..], indices);
    }

    public static void EncodeBc4Channel(
        ReadOnlySpan<byte> rgba,
        int channel,
        Span<byte> output)
    {
        Span<byte> values = stackalloc byte[16];
        for (var pixel = 0; pixel < 16; pixel++) values[pixel] = rgba[pixel * 4 + channel];
        EncodeBc4(values, output);
    }

    private static void DecodeBc4(ReadOnlySpan<byte> block, Span<byte> values)
    {
        Span<byte> palette = stackalloc byte[8];
        BuildAlphaPalette(block[0], block[1], palette);
        ulong indices = 0;
        for (var index = 0; index < 6; index++) indices |= (ulong)block[index + 2] << (index * 8);
        for (var pixel = 0; pixel < 16; pixel++)
        {
            values[pixel] = palette[(int)((indices >> (pixel * 3)) & 7)];
        }
    }

    private static void EncodeBc4(ReadOnlySpan<byte> values, Span<byte> output)
    {
        byte minimum = 255;
        byte maximum = 0;
        foreach (var value in values)
        {
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }

        output.Clear();
        output[0] = maximum;
        output[1] = minimum;
        if (minimum == maximum) return;
        Span<byte> palette = stackalloc byte[8];
        BuildAlphaPalette(maximum, minimum, palette);
        ulong indices = 0;
        for (var pixel = 0; pixel < 16; pixel++)
        {
            var best = 0;
            var distance = int.MaxValue;
            for (var index = 0; index < 8; index++)
            {
                var candidate = Math.Abs(values[pixel] - palette[index]);
                if (candidate < distance)
                {
                    distance = candidate;
                    best = index;
                }
            }

            indices |= (ulong)best << (pixel * 3);
        }

        for (var index = 0; index < 6; index++) output[index + 2] = (byte)(indices >> (index * 8));
    }

    private static void BuildColourPalette(
        ushort color0,
        ushort color1,
        Span<byte> palette,
        bool allowTransparency)
    {
        UnpackRgb565(color0, palette[..4]);
        UnpackRgb565(color1, palette.Slice(4, 4));
        if (!allowTransparency || color0 > color1)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                palette[8 + channel] = (byte)((2 * palette[channel] + palette[4 + channel]) / 3);
                palette[12 + channel] = (byte)((palette[channel] + 2 * palette[4 + channel]) / 3);
            }

            palette[11] = palette[15] = 255;
        }
        else
        {
            for (var channel = 0; channel < 3; channel++)
            {
                palette[8 + channel] = (byte)((palette[channel] + palette[4 + channel]) / 2);
            }

            palette[11] = 255;
            palette[12] = palette[13] = palette[14] = palette[15] = 0;
        }
    }

    private static void BuildAlphaPalette(byte alpha0, byte alpha1, Span<byte> palette)
    {
        palette[0] = alpha0;
        palette[1] = alpha1;
        if (alpha0 > alpha1)
        {
            for (var index = 2; index < 8; index++)
            {
                palette[index] = (byte)(((8 - index) * alpha0 + (index - 1) * alpha1) / 7);
            }
        }
        else
        {
            for (var index = 2; index < 6; index++)
            {
                palette[index] = (byte)(((6 - index) * alpha0 + (index - 1) * alpha1) / 5);
            }

            palette[6] = 0;
            palette[7] = 255;
        }
    }

    private static ushort PackRgb565(int red, int green, int blue) =>
        checked((ushort)(((red * 31 + 127) / 255 << 11) |
                         ((green * 63 + 127) / 255 << 5) |
                         ((blue * 31 + 127) / 255)));

    private static void UnpackRgb565(ushort value, Span<byte> rgba)
    {
        var red = (value >> 11) & 31;
        var green = (value >> 5) & 63;
        var blue = value & 31;
        rgba[0] = (byte)((red << 3) | (red >> 2));
        rgba[1] = (byte)((green << 2) | (green >> 4));
        rgba[2] = (byte)((blue << 3) | (blue >> 2));
        rgba[3] = 255;
    }
}
