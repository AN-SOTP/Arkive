using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Arkive.Core.Viewer.Shaders;

/// <summary>Disassembles DXBC with the D3DCompiler_47 library that ships with Windows.</summary>
public static class DxbcDisassembler
{
    public static string Disassemble(ReadOnlySpan<byte> dxbc)
    {
        if (dxbc.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(dxbc) != 0x43425844)
            throw new InvalidDataException("The shader is not DXBC.");
        // Some cached blobs carry trailing bytes; the container states its own size.
        var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(dxbc[24..]), (uint)dxbc.Length);
        var bytes = dxbc[..size].ToArray();
        var result = D3DDisassemble(bytes, (nuint)bytes.Length, 0, null, out var blob);
        if (result < 0 || blob == IntPtr.Zero)
            throw new InvalidDataException($"D3DDisassemble failed (0x{result:X8}).");
        try
        {
            var vtable = Marshal.ReadIntPtr(blob);
            var getPointer = Marshal.GetDelegateForFunctionPointer<BlobPointer>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
            var getSize = Marshal.GetDelegateForFunctionPointer<BlobSize>(Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size));
            return Marshal.PtrToStringAnsi(getPointer(blob), (int)getSize(blob)).TrimEnd('\0');
        }
        finally
        {
            var release = Marshal.GetDelegateForFunctionPointer<Release>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(blob), 2 * IntPtr.Size));
            release(blob);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr BlobPointer(IntPtr blob);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nuint BlobSize(IntPtr blob);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint Release(IntPtr blob);

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DDisassemble(
        byte[] source,
        nuint sourceSize,
        uint flags,
        [MarshalAs(UnmanagedType.LPStr)] string? comments,
        out IntPtr disassembly);
}
