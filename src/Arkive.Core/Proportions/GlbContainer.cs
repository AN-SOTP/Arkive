using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Arkive.Core.Proportions;

/// <summary>A binary glTF split into its JSON document and BIN chunk, for reading and rewriting vertex data in place.</summary>
internal sealed class GlbContainer
{
    private const uint Magic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;

    public JsonObject Json { get; }
    public byte[] Bin { get; }

    private GlbContainer(JsonObject json, byte[] bin)
    {
        Json = json;
        Bin = bin;
    }

    public static GlbContainer Parse(byte[] bytes)
    {
        if (bytes.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
            throw new InvalidDataException("The file is not a binary glTF (GLB).");
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) != JsonChunk || 20 + jsonLength > bytes.Length)
            throw new InvalidDataException("The GLB has no JSON chunk.");
        var json = JsonNode.Parse(bytes.AsSpan(20, jsonLength))?.AsObject()
            ?? throw new InvalidDataException("The GLB JSON chunk is empty.");
        var offset = 20 + jsonLength;
        var bin = Array.Empty<byte>();
        if (offset + 8 <= bytes.Length && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4)) == BinChunk)
        {
            var binLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
            if (offset + 8 + binLength > bytes.Length) throw new InvalidDataException("The GLB BIN chunk is truncated.");
            bin = bytes.AsSpan(offset + 8, binLength).ToArray();
        }
        return new GlbContainer(json, bin);
    }

    public byte[] ToBytes()
    {
        var json = Encoding.UTF8.GetBytes(Json.ToJsonString());
        var jsonPadded = (json.Length + 3) & ~3;
        var binPadded = (Bin.Length + 3) & ~3;
        var total = 12 + 8 + jsonPadded + (Bin.Length > 0 ? 8 + binPadded : 0);
        var output = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(output, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), JsonChunk);
        json.CopyTo(output, 20);
        output.AsSpan(20 + json.Length, jsonPadded - json.Length).Fill((byte)' ');
        if (Bin.Length > 0)
        {
            var offset = 20 + jsonPadded;
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset), (uint)binPadded);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 4), BinChunk);
            Bin.CopyTo(output, offset + 8);
        }
        return output;
    }

    /// <summary>Where an accessor's elements live in the BIN chunk.</summary>
    public AccessorView Accessor(int index)
    {
        var accessor = Json["accessors"]![index]!.AsObject();
        var view = Json["bufferViews"]![(int)accessor["bufferView"]!]!.AsObject();
        var componentType = (int)accessor["componentType"]!;
        var components = (string)accessor["type"]! switch
        {
            "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16,
            var other => throw new InvalidDataException($"Unsupported accessor type {other}."),
        };
        var componentSize = componentType switch
        {
            5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4,
            _ => throw new InvalidDataException($"Unsupported component type {componentType}."),
        };
        var offset = ((int?)view["byteOffset"] ?? 0) + ((int?)accessor["byteOffset"] ?? 0);
        var stride = (int?)view["byteStride"] ?? componentSize * components;
        var count = (int)accessor["count"]!;
        if (count > 0 && offset + (count - 1) * stride + componentSize * components > Bin.Length)
            throw new InvalidDataException($"Accessor {index} lies outside the BIN chunk.");
        return new AccessorView(accessor, componentType, components, offset, stride, count);
    }

    public float ReadFloat(AccessorView view, int element, int component) =>
        BinaryPrimitives.ReadSingleLittleEndian(Bin.AsSpan(view.Offset + element * view.Stride + component * 4));

    public void WriteFloat(AccessorView view, int element, int component, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(Bin.AsSpan(view.Offset + element * view.Stride + component * 4), value);

    /// <summary>Reads an integer component (unsigned byte, short or int).</summary>
    public int ReadInt(AccessorView view, int element, int component)
    {
        var at = view.Offset + element * view.Stride;
        return view.ComponentType switch
        {
            5121 => Bin[at + component],
            5123 => BinaryPrimitives.ReadUInt16LittleEndian(Bin.AsSpan(at + component * 2)),
            5125 => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(Bin.AsSpan(at + component * 4))),
            _ => throw new InvalidDataException($"Component type {view.ComponentType} is not an integer type."),
        };
    }

    /// <summary>Reads a weight component, normalising unsigned byte and short weights.</summary>
    public float ReadWeight(AccessorView view, int element, int component)
    {
        var at = view.Offset + element * view.Stride;
        return view.ComponentType switch
        {
            5126 => BinaryPrimitives.ReadSingleLittleEndian(Bin.AsSpan(at + component * 4)),
            5121 => Bin[at + component] / 255f,
            5123 => BinaryPrimitives.ReadUInt16LittleEndian(Bin.AsSpan(at + component * 2)) / 65535f,
            _ => throw new InvalidDataException($"Component type {view.ComponentType} is not a weight type."),
        };
    }
}

internal sealed record AccessorView(JsonObject Accessor, int ComponentType, int Components, int Offset, int Stride, int Count);
