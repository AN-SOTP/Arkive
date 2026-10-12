using System.Runtime.InteropServices;
using System.Text;

namespace Arkive.Core.Textures;

public interface ICrunchDecoder
{
    string Name { get; }

    CrunchTextureInfo Inspect(ReadOnlySpan<byte> crnData);

    byte[] DecodeDxt1(ReadOnlySpan<byte> crnData);

    byte[] DecodeDxt5(ReadOnlySpan<byte> crnData);

    byte[] DecodeBc5(ReadOnlySpan<byte> crnData);
}

public sealed record CrunchTextureInfo(
    int Width,
    int Height,
    int Levels,
    int Faces,
    int Format,
    string FourCc,
    int BytesPerBlock,
    int ActualDataSize,
    int HeaderSize);

public sealed class CrunchDecodeException : IOException
{
    public CrunchDecodeException(int status, string operation)
        : base($"Crunch {operation} failed with status {status}: {Describe(status)}")
    {
        Status = status;
    }

    public int Status { get; }

    private static string Describe(int status) => status switch
    {
        1 => "invalid argument",
        2 => "invalid CRN header",
        3 => "unsupported CRN layout or format",
        4 => "destination buffer is too small",
        5 => "decoder initialization failed",
        6 => "mip transcoding failed",
        7 => "CRN checksum validation failed",
        8 => "CRN stream is shorter than the decoder header structure",
        9 => "CRN signature does not match Hx",
        10 => "declared CRN header is shorter than the decoder header structure",
        11 => "declared CRN data size exceeds the supplied stream",
        12 => "CRN texture metadata could not be read after header validation",
        13 => "CRN packed signature interpretation does not match its Hx bytes",
        _ => "unknown native decoder error",
    };
}

public sealed class NativeCrunchDecoder : ICrunchDecoder
{
    public const uint SupportedAbiVersion = 4;
    public const int Dxt1Format = 0;
    public const int Dxt1BlockBytes = 8;
    public const int Dxt5Format = 2;
    public const int Dxt5BlockBytes = 16;
    public const int DxnXyFormat = 7;
    public const int Bc5BlockBytes = 16;

    private const string LibraryName = "Arkive.Crunch.Native.dll";
    private readonly uint _abiVersion;

    public NativeCrunchDecoder()
    {
        _abiVersion = NativeMethods.GetAbiVersion();
        if (_abiVersion != SupportedAbiVersion)
        {
            throw new NotSupportedException(
                $"The native Crunch decoder ABI is {_abiVersion}; Arkive requires " +
                $"{SupportedAbiVersion}.");
        }
    }

    public string Name => $"Arkive Unity Crunch native ABI {_abiVersion} / CRND 1.04";

    public CrunchTextureInfo Inspect(ReadOnlySpan<byte> crnData)
    {
        if (crnData.IsEmpty)
        {
            throw new ArgumentException("CRN data cannot be empty.", nameof(crnData));
        }

        var bytes = crnData.ToArray();
        var status = NativeMethods.GetInfo(bytes, checked((uint)bytes.Length), out var info);
        if (status != 0)
        {
            throw new CrunchDecodeException(status, "inspection");
        }

        if (info.AbiVersion != SupportedAbiVersion)
        {
            throw new InvalidDataException(
                $"The CRN information structure reports ABI {info.AbiVersion}; expected " +
                $"{SupportedAbiVersion}.");
        }

        return new CrunchTextureInfo(
            checked((int)info.Width),
            checked((int)info.Height),
            checked((int)info.Levels),
            checked((int)info.Faces),
            checked((int)info.Format),
            FourCc(info.FourCc),
            checked((int)info.BytesPerBlock),
            checked((int)info.ActualDataSize),
            checked((int)info.HeaderSize));
    }

    public byte[] DecodeDxt5(ReadOnlySpan<byte> crnData)
        => DecodeBlockFormat(
            crnData,
            Dxt5Format,
            "DXT5",
            Dxt5BlockBytes,
            NativeMethods.UnpackDxt5);

    public byte[] DecodeDxt1(ReadOnlySpan<byte> crnData)
        => DecodeBlockFormat(
            crnData,
            Dxt1Format,
            "DXT1",
            Dxt1BlockBytes,
            NativeMethods.UnpackDxt1);

    public byte[] DecodeBc5(ReadOnlySpan<byte> crnData)
        => DecodeBlockFormat(
            crnData,
            DxnXyFormat,
            "A2XY",
            Bc5BlockBytes,
            NativeMethods.UnpackDxnXy);

    private byte[] DecodeBlockFormat(
        ReadOnlySpan<byte> crnData,
        int expectedFormat,
        string expectedFourCc,
        int expectedBlockBytes,
        NativeDecode decode)
    {
        var info = Inspect(crnData);
        if (info.Levels != 1 || info.Faces != 1 || info.Format != expectedFormat ||
            info.FourCc != expectedFourCc || info.BytesPerBlock != expectedBlockBytes)
        {
            throw new NotSupportedException(
                $"Arkive's constrained decoder requires one-face, one-level plain {expectedFourCc} CRN data; " +
                $"this stream is {info.Width}x{info.Height}, levels={info.Levels}, faces={info.Faces}, " +
                $"format={info.Format}/{info.FourCc}, blockBytes={info.BytesPerBlock}.");
        }

        var outputSize = CalculateBlockCompressedBytes(
            info.Width,
            info.Height,
            expectedBlockBytes);
        var input = crnData.ToArray();
        var output = new byte[outputSize];
        var status = decode(
            input,
            checked((uint)input.Length),
            output,
            checked((uint)output.Length));
        if (status != 0)
        {
            throw new CrunchDecodeException(status, $"{expectedFourCc} transcoding");
        }

        return output;
    }

    public static int CalculateDxt5Bytes(int width, int height)
        => CalculateBlockCompressedBytes(width, height, Dxt5BlockBytes);

    public static int CalculateDxt1Bytes(int width, int height)
        => CalculateBlockCompressedBytes(width, height, Dxt1BlockBytes);

    public static int CalculateBlockCompressedBytes(int width, int height, int bytesPerBlock)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Block-compressed texture dimensions must be positive.");
        }

        if (bytesPerBlock is not (Dxt1BlockBytes or Dxt5BlockBytes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytesPerBlock),
                "Block-compressed textures must use 8-byte or 16-byte blocks.");
        }

        return checked(Math.Max(1, (width + 3) / 4) *
                       Math.Max(1, (height + 3) / 4) * bytesPerBlock);
    }

    private static string FourCc(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)value;
        bytes[1] = (byte)(value >> 8);
        bytes[2] = (byte)(value >> 16);
        bytes[3] = (byte)(value >> 24);
        return Encoding.ASCII.GetString(bytes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCrunchInfo
    {
        public uint AbiVersion;
        public uint Width;
        public uint Height;
        public uint Levels;
        public uint Faces;
        public uint Format;
        public uint FourCc;
        public uint BytesPerBlock;
        public uint ActualDataSize;
        public uint HeaderSize;
    }

    private static class NativeMethods
    {
        [DllImport(LibraryName, EntryPoint = "arkive_crunch_abi_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint GetAbiVersion();

        [DllImport(LibraryName, EntryPoint = "arkive_crunch_get_info", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetInfo(
            [In] byte[] data,
            uint dataSize,
            out NativeCrunchInfo info);

        [DllImport(LibraryName, EntryPoint = "arkive_crunch_unpack_dxt5", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int UnpackDxt5(
            [In] byte[] data,
            uint dataSize,
            [Out] byte[] output,
            uint outputSize);

        [DllImport(LibraryName, EntryPoint = "arkive_crunch_unpack_dxt1", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int UnpackDxt1(
            [In] byte[] data,
            uint dataSize,
            [Out] byte[] output,
            uint outputSize);

        [DllImport(LibraryName, EntryPoint = "arkive_crunch_unpack_dxn_xy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int UnpackDxnXy(
            [In] byte[] data,
            uint dataSize,
            [Out] byte[] output,
            uint outputSize);
    }

    private delegate int NativeDecode(byte[] data, uint dataSize, byte[] output, uint outputSize);
}
