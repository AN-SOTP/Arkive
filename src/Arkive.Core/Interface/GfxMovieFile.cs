using System.Buffers.Binary;
using System.IO.Compression;

namespace Arkive.Core.Interface;

/// <summary>
/// Where the Scaleform movie sits inside an EFSwfMovie export. The export's RawData byte-array property holds the
/// movie file exactly as Scaleform's exporter wrote it ("GFX", or "CFX" when zlib-compressed); the properties before
/// it and the list of referenced textures after it stay as the game has them.
/// </summary>
/// <param name="PropertySizeOffset">The RawData property tag's size field (array count + 4).</param>
/// <param name="CountOffset">The RawData array count.</param>
/// <param name="DataOffset">The first byte of the movie file.</param>
/// <param name="DataLength">The movie file's length in the export.</param>
public sealed record GfxMovieLocation(int PropertySizeOffset, int CountOffset, int DataOffset, int DataLength, bool Compressed);

public static class GfxMovieFile
{
    public const string Extension = ".gfx";

    /// <summary>Finds the movie file in an EFSwfMovie export, or null when the export holds none.</summary>
    public static GfxMovieLocation? Locate(ReadOnlySpan<byte> export)
    {
        for (var offset = 12; offset + 8 <= export.Length; offset++)
        {
            if (export[offset + 1] != 'F' || export[offset + 2] != 'X' || export[offset] is not ((byte)'G' or (byte)'C'))
                continue;
            var count = BinaryPrimitives.ReadInt32LittleEndian(export[(offset - 4)..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(export[(offset - 12)..]);
            if (count < 8 || count > export.Length - offset || size != count + 4) continue;
            var compressed = export[offset] == 'C';
            if (!compressed && BinaryPrimitives.ReadInt32LittleEndian(export[(offset + 4)..]) != count) continue;
            return new GfxMovieLocation(offset - 12, offset - 4, offset, count, compressed);
        }
        return null;
    }

    /// <summary>The movie file inside an export, as stored.</summary>
    public static byte[] Extract(ReadOnlySpan<byte> export) =>
        Locate(export) is { } location
            ? export.Slice(location.DataOffset, location.DataLength).ToArray()
            : throw new InvalidDataException("The export holds no Scaleform movie.");

    /// <summary>The movie with its body inflated, so tags can be read ("CFX" becomes "GFX").</summary>
    public static byte[] Inflate(byte[] movie)
    {
        if (movie.Length < 8 || movie[1] != 'F' || movie[2] != 'X')
            throw new InvalidDataException("This is not a Scaleform movie file.");
        if (movie[0] == 'G') return movie;
        if (movie[0] != 'C') throw new InvalidDataException("This is not a Scaleform movie file.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(movie.AsSpan(4));
        if (length < 8 || length > 256 * 1024 * 1024) throw new InvalidDataException("The compressed movie declares an invalid length.");
        var inflated = new byte[length];
        movie.AsSpan(0, 8).CopyTo(inflated);
        inflated[0] = (byte)'G';
        using var input = new ZLibStream(new MemoryStream(movie, 8, movie.Length - 8), CompressionMode.Decompress);
        input.ReadExactly(inflated, 8, length - 8);
        return inflated;
    }

    /// <summary>
    /// The export with its movie replaced. Only the RawData bytes and their two length fields change; the properties
    /// before it and the texture references after it are copied unchanged.
    /// </summary>
    public static byte[] Replace(ReadOnlySpan<byte> export, ReadOnlySpan<byte> movie)
    {
        var location = Locate(export) ?? throw new InvalidDataException("The export holds no Scaleform movie.");
        if (movie.Length < 8 || movie[1] != 'F' || movie[2] != 'X' || movie[0] is not ((byte)'G' or (byte)'C'))
            throw new InvalidDataException("The replacement is not a Scaleform movie file (it must start with GFX or CFX).");
        var tail = export[(location.DataOffset + location.DataLength)..];
        var output = new byte[location.DataOffset + movie.Length + tail.Length];
        export[..location.DataOffset].CopyTo(output);
        movie.CopyTo(output.AsSpan(location.DataOffset));
        tail.CopyTo(output.AsSpan(location.DataOffset + movie.Length));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(location.PropertySizeOffset), movie.Length + 4);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(location.CountOffset), movie.Length);
        return output;
    }
}
