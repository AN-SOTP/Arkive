using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Arkive.Core.Inspection;

internal sealed class DdsArtifactInspector
{
    private const uint DdsMagic = 0x20534444;
    private const uint PixelFormatFourCc = 0x4;
    private const uint PixelFormatRgb = 0x40;
    private const uint Caps2Cubemap = 0x200;
    private const uint Caps2CubemapFaces = 0xFC00;
    private const uint Caps2Volume = 0x200000;
    private const uint Dx10TextureCube = 0x4;
    private const long MaxBc3AlphaAnalysisBytes = 64L * 1024 * 1024;

    public async Task<ArtifactInspectionResult> InspectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        var properties = new List<ArtifactInspectionProperty>
        {
            new("File", "Name", file.Name),
            new("File", "Size", $"{file.Length:N0} bytes"),
        };
        var issues = new List<ArtifactInspectionIssue>();
        var header = new byte[(int)Math.Min(file.Length, 148L)];
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 4096,
                         useAsync: true))
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        }

        if (header.Length < 128)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "DDS_HEADER_TRUNCATED",
                $"A DDS file requires at least 128 header bytes; this file has {header.Length:N0}."));
            return BuildResult(path, properties, issues, "Invalid or truncated DDS header");
        }

        if (ReadUInt32(header, 0) != DdsMagic)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "DDS_MAGIC_INVALID",
                "The file does not begin with the DDS magic value."));
            return BuildResult(path, properties, issues, "Invalid DDS signature");
        }

        var headerSize = ReadUInt32(header, 4);
        var flags = ReadUInt32(header, 8);
        var height = ReadUInt32(header, 12);
        var width = ReadUInt32(header, 16);
        var pitchOrLinearSize = ReadUInt32(header, 20);
        var depth = ReadUInt32(header, 24);
        var declaredMipCount = ReadUInt32(header, 28);
        var pixelFormatSize = ReadUInt32(header, 76);
        var pixelFormatFlags = ReadUInt32(header, 80);
        var fourCc = ReadFourCc(header.AsSpan(84, 4));
        var rgbBits = ReadUInt32(header, 88);
        var caps = ReadUInt32(header, 108);
        var caps2 = ReadUInt32(header, 112);
        var isDx10 = string.Equals(fourCc, "DX10", StringComparison.Ordinal);
        var headerBytes = isDx10 ? 148 : 128;

        properties.AddRange(
        [
            new("DDS header", "Header size", headerSize.ToString()),
            new("DDS header", "Flags", $"0x{flags:X8}"),
            new("DDS header", "Pixel-format size", pixelFormatSize.ToString()),
            new("DDS header", "Pixel-format flags", $"0x{pixelFormatFlags:X8}"),
            new("DDS header", "Caps", $"0x{caps:X8}"),
            new("DDS header", "Caps2", $"0x{caps2:X8}"),
            new("DDS header", "Pitch/linear size", $"{pitchOrLinearSize:N0}"),
        ]);

        if (headerSize != 124)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "DDS_HEADER_SIZE_INVALID",
                $"The DDS header declares {headerSize} bytes instead of 124."));
        }

        if (pixelFormatSize != 32)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "DDS_PIXEL_FORMAT_SIZE_INVALID",
                $"The DDS pixel-format structure declares {pixelFormatSize} bytes instead of 32."));
        }

        if (width == 0 || height == 0)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "DDS_DIMENSIONS_INVALID",
                $"DDS dimensions must be non-zero; the header reports {width} × {height}."));
        }

        uint arraySize = 1;
        uint resourceDimension = 0;
        uint dxgiFormat = 0;
        uint dx10MiscFlag = 0;
        if (isDx10)
        {
            if (header.Length < 148)
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "DDS_DX10_HEADER_TRUNCATED",
                    "The DX10 FourCC is present, but its 20-byte extension header is missing."));
                return BuildResult(path, properties, issues, "Truncated DDS DX10 header");
            }

            dxgiFormat = ReadUInt32(header, 128);
            resourceDimension = ReadUInt32(header, 132);
            dx10MiscFlag = ReadUInt32(header, 136);
            arraySize = ReadUInt32(header, 140);
            properties.AddRange(
            [
                new("DX10 header", "DXGI format", $"{dxgiFormat} ({DescribeDxgiFormat(dxgiFormat)})"),
                new("DX10 header", "Resource dimension", DescribeResourceDimension(resourceDimension)),
                new("DX10 header", "Array size", arraySize.ToString()),
                new("DX10 header", "Misc flags", $"0x{dx10MiscFlag:X8}"),
            ]);
            if (arraySize == 0)
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "DDS_ARRAY_SIZE_INVALID",
                    "The DX10 array size is zero."));
                arraySize = 1;
            }
        }

        var format = DescribeFormat(pixelFormatFlags, fourCc, rgbBits, dxgiFormat, isDx10);
        var isCube = (caps2 & Caps2Cubemap) != 0 ||
                     (isDx10 && (dx10MiscFlag & Dx10TextureCube) != 0);
        var isVolume = (caps2 & Caps2Volume) != 0 || (isDx10 && resourceDimension == 4);
        var faceCount = 1u;
        if (isCube)
        {
            faceCount = isDx10
                ? 6u
                : (uint)BitOperations.PopCount(caps2 & Caps2CubemapFaces);
            if (faceCount == 0)
            {
                faceCount = 6;
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Warning,
                    "DDS_CUBEMAP_FACES_UNDECLARED",
                    "The cubemap flag is set without individual face flags; six faces were assumed."));
            }
        }

        var mipCount = Math.Max(1u, declaredMipCount);
        var effectiveDepth = Math.Max(1u, depth);
        var subresourceCount = checked((long)arraySize * faceCount);
        var maximumUsefulMipCount = width == 0 || height == 0
            ? 0
            : 1 + (int)Math.Floor(Math.Log2(Math.Max(width, Math.Max(height, effectiveDepth))));
        if (maximumUsefulMipCount > 0 && mipCount > maximumUsefulMipCount)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Warning,
                "DDS_MIP_COUNT_EXCESSIVE",
                $"The header declares {mipCount} mip levels, but {maximumUsefulMipCount} reaches 1 × 1 for these dimensions."));
        }

        properties.AddRange(
        [
            new("Texture", "Dimensions", isVolume
                ? $"{width} × {height} × {effectiveDepth}"
                : $"{width} × {height}"),
            new("Texture", "Format", format.Name),
            new("Texture", "Mip levels", mipCount.ToString()),
            new("Texture", "Resource type", isVolume ? "Volume" : isCube ? "Cubemap" : "2D"),
            new("Texture", "Array elements", arraySize.ToString()),
            new("Texture", "Faces per element", faceCount.ToString()),
            new("Texture", "Subresources", subresourceCount.ToString("N0")),
            new("Texture", "Likely role", InferTextureRole(file.Name, format.Name)),
        ]);

        long? expectedPayloadBytes = null;
        try
        {
            expectedPayloadBytes = CalculatePayloadBytes(
                width,
                height,
                isVolume ? effectiveDepth : 1,
                mipCount,
                arraySize,
                faceCount,
                format.BlockBytes,
                format.BitsPerPixel);
        }
        catch (OverflowException)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "DDS_SIZE_OVERFLOW",
                "The declared dimensions and subresource counts overflow a 64-bit payload size."));
        }

        var actualPayloadBytes = Math.Max(0, file.Length - headerBytes);
        properties.Add(new ArtifactInspectionProperty(
            "Payload",
            "Actual bytes",
            $"{actualPayloadBytes:N0}"));
        if (expectedPayloadBytes is { } expected)
        {
            properties.Add(new ArtifactInspectionProperty(
                "Payload",
                "Expected minimum bytes",
                $"{expected:N0}"));
            properties.Add(new ArtifactInspectionProperty(
                "Payload",
                "Size delta",
                $"{actualPayloadBytes - expected:+#,0;-#,0;0}"));
            if (actualPayloadBytes < expected)
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "DDS_PAYLOAD_TRUNCATED",
                    $"The declared mip chain requires at least {expected:N0} bytes, but only {actualPayloadBytes:N0} follow the header."));
            }
        }
        else
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Warning,
                "DDS_FORMAT_SIZE_UNKNOWN",
                $"Arkive does not yet know how to calculate payload size for {format.Name}."));
        }

        var isBc3 = string.Equals(fourCc, "DXT5", StringComparison.Ordinal) ||
                    (isDx10 && dxgiFormat is 77 or 78);
        if (isBc3 && width > 0 && height > 0)
        {
            var alpha = await AnalyzeBc3TopMipAlphaAsync(
                path,
                headerBytes,
                width,
                height,
                cancellationToken).ConfigureAwait(false);
            if (alpha is not null)
            {
                properties.AddRange(
                [
                    new("Channels", "Top-mip alpha range", $"{alpha.Minimum}–{alpha.Maximum}"),
                    new("Channels", "Top-mip alpha mean", alpha.Mean.ToString("N1")),
                    new("Channels", "Nearly transparent pixels", $"{alpha.NearlyTransparentPercent:N1}% (α ≤ 5)"),
                    new("Channels", "Nearly opaque pixels", $"{alpha.NearlyOpaquePercent:N1}% (α ≥ 250)"),
                ]);
                if (alpha.NearlyTransparentPercent >= 50)
                {
                    issues.Add(new ArtifactInspectionIssue(
                        ArtifactInspectionSeverity.Warning,
                        "DDS_BC3_ALPHA_MOSTLY_TRANSPARENT",
                        $"The top mip is {alpha.NearlyTransparentPercent:N1}% transparent according to its BC3 alpha channel. " +
                        "Image editors such as GIMP will hide the RGB colour image and may show only mask details. " +
                        "Do not use Remove Alpha Channel because GIMP composites transparent pixels against its background. " +
                        "Edit Arkive's fully visible -edit.png instead; import automatically restores this authoritative game alpha."));
                }
            }
        }

        var summary =
            $"{width} × {height} {format.Name}, {mipCount} mip{(mipCount == 1 ? string.Empty : "s")}, " +
            $"{actualPayloadBytes:N0} payload bytes";
        return BuildResult(path, properties, issues, summary);
    }

    private static ArtifactInspectionResult BuildResult(
        string path,
        IReadOnlyList<ArtifactInspectionProperty> properties,
        IReadOnlyList<ArtifactInspectionIssue> issues,
        string summary) => new(
        path,
        "DDS",
        summary,
        properties,
        [],
        issues);

    private static async Task<Bc3AlphaStatistics?> AnalyzeBc3TopMipAlphaAsync(
        string path,
        int headerBytes,
        uint width,
        uint height,
        CancellationToken cancellationToken)
    {
        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        var byteCount = checked((long)blocksWide * blocksHigh * 16);
        if (byteCount <= 0 || byteCount > MaxBc3AlphaAnalysisBytes || byteCount > int.MaxValue)
        {
            return null;
        }

        var file = new FileInfo(path);
        if (file.Length < headerBytes + byteCount)
        {
            return null;
        }

        var payload = new byte[checked((int)byteCount)];
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            stream.Position = headerBytes;
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        long total = 0;
        long sum = 0;
        long nearlyTransparent = 0;
        long nearlyOpaque = 0;
        var minimum = byte.MaxValue;
        var maximum = byte.MinValue;
        Span<byte> palette = stackalloc byte[8];
        for (var blockY = 0u; blockY < blocksHigh; blockY++)
        {
            for (var blockX = 0u; blockX < blocksWide; blockX++)
            {
                var offset = checked((int)((blockY * blocksWide + blockX) * 16));
                var block = payload.AsSpan(offset, 8);
                BuildBc3AlphaPalette(block[0], block[1], palette);
                ulong indices = 0;
                for (var indexByte = 0; indexByte < 6; indexByte++)
                {
                    indices |= (ulong)block[2 + indexByte] << (indexByte * 8);
                }

                for (var pixelY = 0u; pixelY < 4 && blockY * 4 + pixelY < height; pixelY++)
                {
                    for (var pixelX = 0u; pixelX < 4 && blockX * 4 + pixelX < width; pixelX++)
                    {
                        var pixel = checked((int)(pixelY * 4 + pixelX));
                        var value = palette[(int)((indices >> (pixel * 3)) & 7)];
                        total++;
                        sum += value;
                        minimum = Math.Min(minimum, value);
                        maximum = Math.Max(maximum, value);
                        if (value <= 5) nearlyTransparent++;
                        if (value >= 250) nearlyOpaque++;
                    }
                }
            }
        }

        return total == 0
            ? null
            : new Bc3AlphaStatistics(
                minimum,
                maximum,
                (double)sum / total,
                100d * nearlyTransparent / total,
                100d * nearlyOpaque / total);
    }

    private static void BuildBc3AlphaPalette(byte alpha0, byte alpha1, Span<byte> palette)
    {
        palette[0] = alpha0;
        palette[1] = alpha1;
        if (alpha0 > alpha1)
        {
            for (var index = 2; index < 8; index++)
            {
                palette[index] = checked((byte)(
                    ((8 - index) * alpha0 + (index - 1) * alpha1) / 7));
            }
        }
        else
        {
            for (var index = 2; index < 6; index++)
            {
                palette[index] = checked((byte)(
                    ((6 - index) * alpha0 + (index - 1) * alpha1) / 5));
            }

            palette[6] = 0;
            palette[7] = 255;
        }
    }

    private sealed record Bc3AlphaStatistics(
        byte Minimum,
        byte Maximum,
        double Mean,
        double NearlyTransparentPercent,
        double NearlyOpaquePercent);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private static string ReadFourCc(ReadOnlySpan<byte> bytes) =>
        Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');

    private static (string Name, int? BlockBytes, int? BitsPerPixel) DescribeFormat(
        uint pixelFormatFlags,
        string fourCc,
        uint rgbBits,
        uint dxgiFormat,
        bool isDx10)
    {
        if (isDx10)
        {
            return dxgiFormat switch
            {
                28 => ("R8G8B8A8_UNORM", null, 32),
                29 => ("R8G8B8A8_UNORM_SRGB", null, 32),
                71 => ("BC1_UNORM", 8, null),
                72 => ("BC1_UNORM_SRGB", 8, null),
                74 => ("BC2_UNORM", 16, null),
                75 => ("BC2_UNORM_SRGB", 16, null),
                77 => ("BC3_UNORM", 16, null),
                78 => ("BC3_UNORM_SRGB", 16, null),
                80 => ("BC4_UNORM", 8, null),
                81 => ("BC4_SNORM", 8, null),
                83 => ("BC5_UNORM", 16, null),
                84 => ("BC5_SNORM", 16, null),
                95 => ("BC6H_UF16", 16, null),
                96 => ("BC6H_SF16", 16, null),
                98 => ("BC7_UNORM", 16, null),
                99 => ("BC7_UNORM_SRGB", 16, null),
                _ => ($"DXGI_{dxgiFormat}", null, null),
            };
        }

        if ((pixelFormatFlags & PixelFormatFourCc) != 0)
        {
            return fourCc switch
            {
                "DXT1" => ("BC1 / DXT1", 8, null),
                "DXT3" => ("BC2 / DXT3", 16, null),
                "DXT5" => ("BC3 / DXT5", 16, null),
                "ATI1" or "BC4U" => ("BC4 UNORM", 8, null),
                "BC4S" => ("BC4 SNORM", 8, null),
                "ATI2" or "BC5U" => ("BC5 UNORM", 16, null),
                "BC5S" => ("BC5 SNORM", 16, null),
                _ => (string.IsNullOrEmpty(fourCc) ? "Unknown FourCC" : fourCc, null, null),
            };
        }

        return (pixelFormatFlags & PixelFormatRgb) != 0 && rgbBits > 0
            ? ($"Uncompressed RGB(A) {rgbBits}-bit", null, checked((int)rgbBits))
            : ("Unrecognized", null, null);
    }

    private static string DescribeDxgiFormat(uint format) =>
        DescribeFormat(PixelFormatFourCc, "DX10", 0, format, isDx10: true).Name;

    private static string DescribeResourceDimension(uint dimension) => dimension switch
    {
        1 => "Buffer",
        2 => "Texture1D",
        3 => "Texture2D",
        4 => "Texture3D",
        _ => $"Unknown ({dimension})",
    };

    private static long? CalculatePayloadBytes(
        uint width,
        uint height,
        uint depth,
        uint mipCount,
        uint arraySize,
        uint faceCount,
        int? blockBytes,
        int? bitsPerPixel)
    {
        if (width == 0 || height == 0 || (blockBytes is null && bitsPerPixel is null))
        {
            return null;
        }

        long total = 0;
        var mipWidth = width;
        var mipHeight = height;
        var mipDepth = depth;
        for (var mip = 0u; mip < mipCount; mip++)
        {
            long levelBytes;
            checked
            {
                if (blockBytes is { } blockSize)
                {
                    var blocksWide = Math.Max(1u, (mipWidth + 3) / 4);
                    var blocksHigh = Math.Max(1u, (mipHeight + 3) / 4);
                    levelBytes = (long)blocksWide * blocksHigh * mipDepth * blockSize;
                }
                else
                {
                    levelBytes = ((long)mipWidth * mipHeight * mipDepth * bitsPerPixel!.Value + 7) / 8;
                }

                total += levelBytes;
            }

            mipWidth = Math.Max(1u, mipWidth / 2);
            mipHeight = Math.Max(1u, mipHeight / 2);
            mipDepth = Math.Max(1u, mipDepth / 2);
        }

        return checked(total * arraySize * faceCount);
    }

    private static string InferTextureRole(string fileName, string format)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (name.Contains("normal", StringComparison.Ordinal) ||
            name.EndsWith("_n", StringComparison.Ordinal) ||
            name.EndsWith("_nm", StringComparison.Ordinal) ||
            format.Contains("BC5", StringComparison.OrdinalIgnoreCase))
        {
            return "Likely normal map (inferred)";
        }

        if (name.Contains("mask", StringComparison.Ordinal) ||
            name.EndsWith("_m", StringComparison.Ordinal) ||
            name.Contains("orm", StringComparison.Ordinal))
        {
            return "Likely packed mask (inferred)";
        }

        if (name.EndsWith("_d", StringComparison.Ordinal) ||
            name.Contains("diffuse", StringComparison.Ordinal) ||
            name.Contains("albedo", StringComparison.Ordinal))
        {
            return "Likely diffuse/base colour (inferred)";
        }

        if (name.EndsWith("_e", StringComparison.Ordinal))
        {
            return "Likely emissive map (inferred)";
        }

        if (name.Contains("lightmap", StringComparison.Ordinal))
        {
            return "Likely lightmap (inferred)";
        }

        return "Unknown";
    }
}
