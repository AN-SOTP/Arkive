using System.Buffers.Binary;

namespace Arkive.Core.Interface;

/// <summary>One tag of a movie; <see cref="LongHeader"/> keeps the header form the exporter used so it writes back unchanged.</summary>
public sealed class GfxTag(int code, byte[] body, bool longHeader)
{
    public const int End = 0, DoAbc = 82;

    public int Code { get; } = code;
    public byte[] Body { get; set; } = body;
    public bool LongHeader { get; } = longHeader;

    /// <summary>A DoABC tag's name ("" for the movie's own code; "frame1" style names for others).</summary>
    public string AbcName => Code == DoAbc ? System.Text.Encoding.UTF8.GetString(Body, 4, Body.AsSpan(4).IndexOf((byte)0)) : "";

    /// <summary>A DoABC tag's bytecode (after its flags and name).</summary>
    public ReadOnlySpan<byte> AbcData => Body.AsSpan(4 + Body.AsSpan(4).IndexOf((byte)0) + 1);

    public static GfxTag NewDoAbc(string name, ReadOnlySpan<byte> abc, bool lazy = true)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var body = new byte[4 + nameBytes.Length + 1 + abc.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, lazy ? 1u : 0u);
        nameBytes.CopyTo(body.AsSpan(4));
        abc.CopyTo(body.AsSpan(4 + nameBytes.Length + 1));
        return new GfxTag(DoAbc, body, longHeader: true);
    }

    public GfxTag WithAbc(ReadOnlySpan<byte> abc)
    {
        var prefix = 4 + Body.AsSpan(4).IndexOf((byte)0) + 1;
        var body = new byte[prefix + abc.Length];
        Body.AsSpan(0, prefix).CopyTo(body);
        abc.CopyTo(body.AsSpan(prefix));
        return new GfxTag(Code, body, LongHeader);
    }
}

/// <summary>
/// A Scaleform movie split into its header and top-level tags, for edits that add or replace whole tags (the code
/// blocks). Sprites' inner tags stay inside their DefineSprite bodies.
/// </summary>
public sealed class GfxMovieTags
{
    private GfxMovieTags(byte[] header, List<GfxTag> tags)
    {
        Header = header;
        Tags = tags;
    }

    /// <summary>Signature, version, length, frame rectangle, rate and count, as in the file.</summary>
    public byte[] Header { get; }

    public List<GfxTag> Tags { get; }

    public static GfxMovieTags Read(byte[] movie)
    {
        var data = GfxMovieFile.Inflate(movie);
        var rectBits = data[8] >> 3;
        var position = 8 + (5 + 4 * rectBits + 7) / 8 + 4;
        var tags = new List<GfxTag>();
        while (position + 2 <= data.Length)
        {
            var header = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
            position += 2;
            var code = header >> 6;
            var length = header & 0x3F;
            var longHeader = length == 0x3F;
            if (longHeader)
            {
                length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position));
                position += 4;
            }
            if (length < 0 || position + length > data.Length) throw new InvalidDataException("A movie tag runs past the end of the file.");
            tags.Add(new GfxTag(code, data.AsSpan(position, length).ToArray(), longHeader));
            position += length;
            if (code == GfxTag.End) break;
        }
        var start = 8 + (5 + 4 * rectBits + 7) / 8 + 4;
        return new GfxMovieTags(data.AsSpan(0, start).ToArray(), tags);
    }

    /// <summary>The movie as an uncompressed "GFX" file with its length field updated.</summary>
    public byte[] Write()
    {
        using var output = new MemoryStream();
        output.Write(Header);
        Span<byte> buffer = stackalloc byte[6];
        foreach (var tag in Tags)
        {
            if (tag.LongHeader || tag.Body.Length >= 0x3F)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)((tag.Code << 6) | 0x3F));
                BinaryPrimitives.WriteInt32LittleEndian(buffer[2..], tag.Body.Length);
                output.Write(buffer);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)((tag.Code << 6) | tag.Body.Length));
                output.Write(buffer[..2]);
            }
            output.Write(tag.Body);
        }
        var bytes = output.ToArray();
        bytes[0] = (byte)'G';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length);
        return bytes;
    }

    /// <summary>The movie's own code: the first DoABC tag.</summary>
    public GfxTag MainAbc => Tags.FirstOrDefault(tag => tag.Code == GfxTag.DoAbc)
        ?? throw new InvalidDataException("The movie has no ActionScript 3 code.");

    /// <summary>Adds a code block after the last existing one (before the first ShowFrame if there is none).</summary>
    public void AppendAbc(GfxTag tag)
    {
        var last = Tags.FindLastIndex(existing => existing.Code == GfxTag.DoAbc);
        Tags.Insert(last + 1, tag);
    }
}
