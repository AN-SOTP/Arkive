using Arkive.Core.Inspection;

namespace Arkive.Core.Textures;

public sealed record NativeTextureFormatProfile(
    int PixelFormatValue,
    string PixelFormatName,
    string GpuFormatName,
    string CrunchFourCc,
    string DdsFourCc,
    int CrunchFormat,
    int BytesPerBlock)
{
    public string ManifestPixelFormat => $"{PixelFormatName} / {GpuFormatName}";

    /// <summary>Plain 32-bit pixels (PF_A8R8G8B8: colour-grading LUTs, some sky layers) rather than 4x4 compressed blocks.</summary>
    public bool IsUncompressed => PixelFormatValue == NativeTextureFormats.Bgra8.PixelFormatValue;

    public DdsBlockFormat DdsFormat => new(DdsFourCc, BytesPerBlock, GpuFormatName);

    public int CalculateMipBytes(int width, int height) => IsUncompressed
        ? checked(width * height * 4)
        : NativeCrunchDecoder.CalculateBlockCompressedBytes(width, height, BytesPerBlock);

    public byte[] DecodeCrunch(ICrunchDecoder decoder, ReadOnlySpan<byte> data) =>
        PixelFormatValue switch
        {
            5 => decoder.DecodeDxt1(data),
            7 => decoder.DecodeDxt5(data),
            24 => decoder.DecodeBc5(data),
            _ => throw new NotSupportedException(
                $"No Crunch decoder is registered for {PixelFormatName}."),
        };
}

public static class NativeTextureFormats
{
    public static NativeTextureFormatProfile Dxt1 { get; } = new(
        5,
        "PF_DXT1",
        "BC1",
        "DXT1",
        "DXT1",
        NativeCrunchDecoder.Dxt1Format,
        NativeCrunchDecoder.Dxt1BlockBytes);

    public static NativeTextureFormatProfile Dxt5 { get; } = new(
        7,
        "PF_DXT5",
        "BC3",
        "DXT5",
        "DXT5",
        NativeCrunchDecoder.Dxt5Format,
        NativeCrunchDecoder.Dxt5BlockBytes);

    public static NativeTextureFormatProfile Bc5 { get; } = new(
        24,
        "PF_BC5",
        "BC5",
        "A2XY",
        "ATI2",
        NativeCrunchDecoder.DxnXyFormat,
        NativeCrunchDecoder.Bc5BlockBytes);

    /// <summary>
    /// Uncompressed BGRA, one 4-byte pixel at a time in rows (UE3 PF_A8R8G8B8). It has no Crunch form; its mips are stored
    /// as Lost Ark LZ4 bulk data. BytesPerBlock is the bytes of one pixel.
    /// </summary>
    public static NativeTextureFormatProfile Bgra8 { get; } = new(
        2,
        "PF_A8R8G8B8",
        "B8G8R8A8",
        "",
        "BGRA",
        -1,
        4);

    public static IReadOnlyList<NativeTextureFormatProfile> Supported { get; } = [Dxt1, Dxt5, Bc5, Bgra8];

    public static bool TryResolve(TextureReconnaissanceReport report, out NativeTextureFormatProfile profile)
    {
        ArgumentNullException.ThrowIfNull(report);
        var property = report.Properties.SingleOrDefault(item =>
            item.Name.Equals("Format", StringComparison.OrdinalIgnoreCase));
        profile = Supported.FirstOrDefault(item =>
            property?.NumericValue == item.PixelFormatValue &&
            property.ValuePreview.Equals(item.PixelFormatName, StringComparison.OrdinalIgnoreCase))!;
        return profile is not null;
    }
}
