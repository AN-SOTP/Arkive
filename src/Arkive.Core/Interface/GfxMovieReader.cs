using System.Buffers.Binary;
using System.Text;

namespace Arkive.Core.Interface;

/// <summary>A picture the movie draws from a texture in its package (Scaleform's DefineExternalImage2).</summary>
/// <param name="FileName">"Effect_I317.tga"; the texture export is named after it (<see cref="TextureName"/>).</param>
/// <param name="Width">The size the movie was laid out for; sub-image rectangles are in these pixels.</param>
public sealed record GfxExternalImage(int CharacterId, string ExportName, string FileName, int Width = 0, int Height = 0)
{
    /// <summary>The texture export the picture comes from: "Effect_I3.nopack.tga" is effect_i3_nopack.</summary>
    public string TextureName => Path.GetFileNameWithoutExtension(FileName).Replace('.', '_').ToLowerInvariant();

    /// <summary>
    /// A picture exported on its own ("nopack") rather than as an atlas of packed elements. Its number is not a
    /// character id: it repeats the atlases' numbers, and scripts load it by its export name.
    /// </summary>
    public bool IsStandalone => FileName.Contains(".nopack", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One element packed into an atlas picture: a rectangle of it, in the picture's laid-out pixels.</summary>
public sealed record GfxSubImage(int ImageId, int Left, int Top, int Right, int Bottom);

/// <summary>A character placed on a timeline (the root timeline is sprite 0), with the instance name scripts use.</summary>
public sealed record GfxPlacement(int CharacterId, string? InstanceName);

/// <summary>What a movie draws and how it is put together, as far as naming its pictures needs.</summary>
/// <param name="Name">The movie's name as its author saved it ("QuickSlot"), from Scaleform's exporter record.</param>
public sealed record GfxMovieStructure(
    string? Name,
    IReadOnlyList<GfxExternalImage> Images,
    IReadOnlyDictionary<int, GfxSubImage> SubImages,
    IReadOnlyDictionary<int, IReadOnlySet<int>> ShapeBitmaps,
    IReadOnlyDictionary<int, IReadOnlyList<GfxPlacement>> Timelines,
    IReadOnlyDictionary<int, IReadOnlyList<string>> SymbolNames);

/// <summary>
/// Reads the parts of a Scaleform movie that say which pictures it uses and where: external images, the shapes that
/// fill with them, and the sprites, buttons and instance names that place those shapes. Drawing data is skipped.
/// </summary>
public static class GfxMovieReader
{
    private const int End = 0;
    private const int DefineShape = 2;
    private const int PlaceObject2 = 26;
    private const int DefineShape2 = 22;
    private const int DefineShape3 = 32;
    private const int DefineButton2 = 34;
    private const int DefineSprite = 39;
    private const int ExportAssets = 56;
    private const int PlaceObject3 = 70;
    private const int SymbolClass = 76;
    private const int DefineShape4 = 83;
    private const int ExporterInfo = 1000;
    private const int DefineSubImage = 1008;
    private const int DefineExternalImage2 = 1009;

    public static GfxMovieStructure Read(byte[] movie)
    {
        var data = GfxMovieFile.Inflate(movie);
        var state = new State();
        var bits = new BitReader(data, 8);
        bits.SkipRect();
        ReadTags(data, bits.ByteOffset + 4, data.Length, 0, state);
        return new GfxMovieStructure(
            state.Name,
            state.Images,
            state.SubImages,
            state.Shapes.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<int>)pair.Value),
            state.Timelines.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<GfxPlacement>)pair.Value),
            state.Symbols.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value));
    }

    private static void ReadTags(byte[] data, int offset, int end, int timeline, State state)
    {
        var depths = new Dictionary<int, int>();
        while (offset + 2 <= end)
        {
            var header = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
            var type = header >> 6;
            var length = header & 0x3F;
            offset += 2;
            if (length == 0x3F)
            {
                if (offset + 4 > end) return;
                length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                offset += 4;
            }
            if (length < 0 || length > end - offset) return;
            var body = data.AsSpan(offset, length);
            try
            {
                switch (type)
                {
                    case End:
                        return;
                    case DefineExternalImage2 when body.Length >= 12:
                        var exportName = PascalString(body, 10, out var next);
                        state.Images.Add(new GfxExternalImage(
                            BinaryPrimitives.ReadUInt16LittleEndian(body), exportName, PascalString(body, next, out _),
                            BinaryPrimitives.ReadUInt16LittleEndian(body[6..]), BinaryPrimitives.ReadUInt16LittleEndian(body[8..])));
                        break;
                    case ExporterInfo when body.Length >= 10:
                        PascalString(body, 8, out var nameOffset);
                        state.Name = PascalString(body, nameOffset, out _);
                        break;
                    case DefineSubImage when body.Length >= 12:
                        state.SubImages[BinaryPrimitives.ReadUInt16LittleEndian(body)] = new GfxSubImage(
                            BinaryPrimitives.ReadUInt16LittleEndian(body[2..]), BinaryPrimitives.ReadUInt16LittleEndian(body[4..]),
                            BinaryPrimitives.ReadUInt16LittleEndian(body[6..]), BinaryPrimitives.ReadUInt16LittleEndian(body[8..]),
                            BinaryPrimitives.ReadUInt16LittleEndian(body[10..]));
                        break;
                    case DefineShape or DefineShape2 or DefineShape3 or DefineShape4:
                        ReadShape(data, offset, type, state);
                        break;
                    case DefineSprite when body.Length >= 4:
                        var sprite = BinaryPrimitives.ReadUInt16LittleEndian(body);
                        ReadTags(data, offset + 4, offset + length, sprite, state);
                        break;
                    case DefineButton2 when body.Length >= 5:
                        ReadButton(data, offset, offset + length, state);
                        break;
                    case PlaceObject2 or PlaceObject3:
                        ReadPlacement(data, offset, type, timeline, depths, state);
                        break;
                    case SymbolClass or ExportAssets:
                        ReadSymbols(body, state);
                        break;
                }
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // A tag this reader misjudges only loses the names it would have given; the rest still count.
            }
            offset += length;
        }
    }

    private static void ReadShape(byte[] data, int offset, int type, State state)
    {
        var id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        var bits = new BitReader(data, offset + 2);
        bits.SkipRect();
        if (type == DefineShape4)
        {
            bits.SkipRect();
            bits.Skip(8);
        }
        if (!state.Shapes.TryGetValue(id, out var bitmaps)) state.Shapes[id] = bitmaps = [];
        ReadFillStyles(bits, type, bitmaps);
        SkipLineStyles(bits, type, bitmaps);
        var fillBits = (int)bits.Read(4);
        var lineBits = (int)bits.Read(4);
        while (true)
        {
            if (bits.Read(1) == 0)
            {
                var flags = (int)bits.Read(5);
                if (flags == 0) return;
                if ((flags & 0x01) != 0) bits.Skip(2 * (int)bits.Read(5));
                if ((flags & 0x02) != 0) bits.Skip(fillBits);
                if ((flags & 0x04) != 0) bits.Skip(fillBits);
                if ((flags & 0x08) != 0) bits.Skip(lineBits);
                if ((flags & 0x10) != 0)
                {
                    ReadFillStyles(bits, type, bitmaps);
                    SkipLineStyles(bits, type, bitmaps);
                    fillBits = (int)bits.Read(4);
                    lineBits = (int)bits.Read(4);
                }
            }
            else if (bits.Read(1) == 1)
            {
                var size = (int)bits.Read(4) + 2;
                if (bits.Read(1) == 1) bits.Skip(2 * size);
                else bits.Skip(1 + size);
            }
            else
            {
                bits.Skip(4 * ((int)bits.Read(4) + 2));
            }
        }
    }

    private static void ReadFillStyles(BitReader bits, int type, HashSet<int> bitmaps)
    {
        bits.Align();
        var count = bits.Byte();
        if (count == 0xFF && type != DefineShape) count = bits.UInt16();
        for (var index = 0; index < count; index++) ReadFillStyle(bits, type, bitmaps);
    }

    private static void ReadFillStyle(BitReader bits, int type, HashSet<int>? bitmaps)
    {
        var style = bits.Byte();
        var alpha = type is DefineShape3 or DefineShape4;
        switch (style)
        {
            case 0x00:
                bits.SkipBytes(alpha ? 4 : 3);
                break;
            case 0x10 or 0x12 or 0x13:
                bits.SkipMatrix();
                var gradients = bits.Byte() & 0x0F;
                bits.SkipBytes(gradients * (alpha ? 5 : 4) + (style == 0x13 ? 2 : 0));
                break;
            case >= 0x40 and <= 0x43:
                var bitmap = bits.UInt16();
                if (bitmap != 0xFFFF) bitmaps?.Add(bitmap);
                bits.SkipMatrix();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown fill style.");
        }
    }

    private static void SkipLineStyles(BitReader bits, int type, HashSet<int> bitmaps)
    {
        bits.Align();
        var count = bits.Byte();
        if (count == 0xFF && type != DefineShape) count = bits.UInt16();
        for (var index = 0; index < count; index++)
        {
            bits.SkipBytes(2);
            if (type != DefineShape4)
            {
                bits.SkipBytes(type == DefineShape3 ? 4 : 3);
                continue;
            }
            var flags = bits.UInt16();
            if ((flags & 0x0030) == 0x0020) bits.SkipBytes(2);
            if ((flags & 0x0008) != 0) ReadFillStyle(bits, type, bitmaps);
            else bits.SkipBytes(4);
        }
    }

    private static void ReadPlacement(byte[] data, int offset, int type, int timeline, Dictionary<int, int> depths, State state)
    {
        var bits = new BitReader(data, offset);
        var flags = bits.Byte();
        var more = type == PlaceObject3 ? bits.Byte() : 0;
        var depth = bits.UInt16();
        if (type == PlaceObject3 && ((more & 0x08) != 0 || ((more & 0x10) != 0 && (flags & 0x02) != 0))) bits.String();
        int character;
        if ((flags & 0x02) != 0) depths[depth] = character = bits.UInt16();
        else if (!depths.TryGetValue(depth, out character)) return;
        if ((flags & 0x04) != 0) bits.SkipMatrix();
        if ((flags & 0x08) != 0) bits.SkipColorTransform();
        if ((flags & 0x10) != 0) bits.SkipBytes(2);
        var name = (flags & 0x20) != 0 ? bits.String() : null;
        if ((flags & 0x02) == 0 && name is null) return;
        if (!state.Timelines.TryGetValue(timeline, out var placements)) state.Timelines[timeline] = placements = [];
        placements.Add(new GfxPlacement(character, name));
    }

    private static void ReadButton(byte[] data, int offset, int end, State state)
    {
        var button = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        var bits = new BitReader(data, offset + 5);
        if (!state.Timelines.TryGetValue(button, out var placements)) state.Timelines[button] = placements = [];
        while (bits.ByteOffset < end)
        {
            var flags = bits.Byte();
            if (flags == 0) return;
            placements.Add(new GfxPlacement(bits.UInt16(), null));
            bits.SkipBytes(2);
            bits.SkipMatrix();
            bits.SkipColorTransform();
            // Filter lists vary in size; the records after one are given up rather than misread.
            if ((flags & 0x10) != 0) return;
            if ((flags & 0x20) != 0) bits.SkipBytes(1);
        }
    }

    private static void ReadSymbols(ReadOnlySpan<byte> body, State state)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(body);
        var offset = 2;
        for (var index = 0; index < count && offset + 2 < body.Length; index++)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
            var terminator = body[(offset + 2)..].IndexOf((byte)0);
            if (terminator < 0) return;
            var name = Encoding.UTF8.GetString(body.Slice(offset + 2, terminator));
            offset += 3 + terminator;
            if (!state.Symbols.TryGetValue(id, out var names)) state.Symbols[id] = names = [];
            if (name.Length > 0 && !names.Contains(name)) names.Add(name);
        }
    }

    private static string PascalString(ReadOnlySpan<byte> body, int offset, out int next)
    {
        var length = body[offset];
        next = offset + 1 + length;
        return Encoding.UTF8.GetString(body.Slice(offset + 1, length));
    }

    private sealed class State
    {
        public string? Name { get; set; }
        public List<GfxExternalImage> Images { get; } = [];
        public Dictionary<int, GfxSubImage> SubImages { get; } = [];
        public Dictionary<int, HashSet<int>> Shapes { get; } = [];
        public Dictionary<int, List<GfxPlacement>> Timelines { get; } = [];
        public Dictionary<int, List<string>> Symbols { get; } = [];
    }

    /// <summary>SWF's bit-packed records (RECT, MATRIX, CXFORM) and the byte fields between them.</summary>
    private sealed class BitReader(byte[] data, int offset)
    {
        private int _bit = offset * 8;

        public int ByteOffset => (_bit + 7) / 8;

        public uint Read(int count)
        {
            uint value = 0;
            for (var index = 0; index < count; index++)
            {
                var current = data[_bit >> 3];
                value = (value << 1) | (uint)((current >> (7 - (_bit & 7))) & 1);
                _bit++;
            }
            return value;
        }

        public void Skip(int count)
        {
            _bit += count;
            if (_bit > data.Length * 8) throw new ArgumentOutOfRangeException(nameof(count));
        }

        public void Align() => _bit = ByteOffset * 8;

        public int Byte()
        {
            Align();
            var value = data[_bit >> 3];
            _bit += 8;
            return value;
        }

        public int UInt16()
        {
            Align();
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_bit >> 3));
            _bit += 16;
            return value;
        }

        public void SkipBytes(int count)
        {
            Align();
            Skip(count * 8);
        }

        public string String()
        {
            Align();
            var start = _bit >> 3;
            var terminator = Array.IndexOf(data, (byte)0, start);
            if (terminator < 0) throw new ArgumentOutOfRangeException(nameof(data));
            _bit = (terminator + 1) * 8;
            return Encoding.UTF8.GetString(data, start, terminator - start);
        }

        public void SkipRect()
        {
            Align();
            Skip(4 * (int)Read(5));
            Align();
        }

        public void SkipMatrix()
        {
            Align();
            if (Read(1) == 1) Skip(2 * (int)Read(5));
            if (Read(1) == 1) Skip(2 * (int)Read(5));
            Skip(2 * (int)Read(5));
            Align();
        }

        public void SkipColorTransform()
        {
            Align();
            var add = Read(1) == 1;
            var multiply = Read(1) == 1;
            var size = (int)Read(4);
            Skip(((add ? 4 : 0) + (multiply ? 4 : 0)) * size);
            Align();
        }
    }
}
