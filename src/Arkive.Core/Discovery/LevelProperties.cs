using System.Buffers.Binary;
using System.Numerics;
using Arkive.Core.Assets;

namespace Arkive.Core.Discovery;

/// <summary>One tagged property of a level object (an actor or a component).</summary>
/// <param name="StructName">The struct's type for a StructProperty (vector, rotator), the enum for a ByteProperty.</param>
/// <param name="Text">A NameProperty's name.</param>
public sealed record LevelProperty(string Name, string Type, string? StructName, int ArrayIndex, byte[] Value, byte BoolValue,
    string? Text = null);

/// <summary>
/// Reads the tagged properties at the start of a level object's data: where an actor stands (Location, Rotation,
/// DrawScale3D), which mesh a component draws and with which materials. Names are lower case in Lost Ark's
/// packages, and an IntProperty's tag carries one extra name after its size. The stream's start is found by trying
/// each offset until a run of tags ends with None.
/// </summary>
public static class LevelProperties
{
    private const int MaximumProperties = 2_048;
    private const int MaximumStart = 256;

    public static IReadOnlyList<LevelProperty>? Read(ReadOnlySpan<byte> bytes, IReadOnlyList<UnrealNameEntry> names, out int end)
    {
        for (var start = 0; start <= Math.Min(MaximumStart, bytes.Length - 8); start++)
            if (TryRead(bytes, names, start, out var properties, out end) && properties.Count > 0)
                return properties;
        end = 0;
        return null;
    }

    /// <summary>Properties starting exactly at <paramref name="start"/> (an element of an array of structs).</summary>
    public static IReadOnlyList<LevelProperty>? ReadAt(ReadOnlySpan<byte> bytes, IReadOnlyList<UnrealNameEntry> names, int start, out int end) =>
        TryRead(bytes, names, start, out var properties, out end) ? properties : null;

    private static bool TryRead(ReadOnlySpan<byte> bytes, IReadOnlyList<UnrealNameEntry> names, int start,
        out List<LevelProperty> properties, out int end)
    {
        properties = [];
        end = start;
        var cursor = start;
        for (var count = 0; count < MaximumProperties; count++)
        {
            if (!TryName(bytes, cursor, names, out var name)) return false;
            if (name.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                end = cursor + 8;
                return true;
            }
            if (cursor > bytes.Length - 24 || !TryName(bytes, cursor + 8, names, out var type) ||
                !type.EndsWith("property", StringComparison.OrdinalIgnoreCase)) return false;
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes[(cursor + 16)..]);
            var index = BinaryPrimitives.ReadInt32LittleEndian(bytes[(cursor + 20)..]);
            if (size < 0 || index < 0) return false;
            cursor += 24;
            string? structName = null;
            byte boolValue = 0;
            type = type.ToLowerInvariant();
            if (type is "structproperty" or "byteproperty")
            {
                if (!TryName(bytes, cursor, names, out var named)) return false;
                structName = named.ToLowerInvariant();
                cursor += 8;
            }
            else if (type == "boolproperty")
            {
                if (cursor >= bytes.Length) return false;
                boolValue = bytes[cursor++];
            }
            else if (type == "intproperty" && TryName(bytes, cursor, names, out var marker) &&
                     marker.Equals("none", StringComparison.OrdinalIgnoreCase) && cursor + 8 + size <= bytes.Length)
            {
                cursor += 8;
            }
            if (size > bytes.Length - cursor) return false;
            string? text = null;
            if (type == "nameproperty" && size >= 8 && TryName(bytes, cursor, names, out var value)) text = value;
            properties.Add(new LevelProperty(name.ToLowerInvariant(), type, structName, index, bytes.Slice(cursor, size).ToArray(), boolValue, text));
            cursor += size;
        }
        return false;
    }

    private static bool TryName(ReadOnlySpan<byte> bytes, int cursor, IReadOnlyList<UnrealNameEntry> names, out string value)
    {
        value = string.Empty;
        if (cursor < 0 || cursor > bytes.Length - 8) return false;
        var index = BinaryPrimitives.ReadInt32LittleEndian(bytes[cursor..]);
        var number = BinaryPrimitives.ReadInt32LittleEndian(bytes[(cursor + 4)..]);
        if (index < 0 || index >= names.Count || number < 0) return false;
        value = new UnrealNameReference(index, number).Resolve(names);
        return true;
    }

    public static LevelProperty? Find(this IReadOnlyList<LevelProperty> properties, string name) =>
        properties.FirstOrDefault(property => property.Name == name && property.ArrayIndex == 0);

    public static Vector3? Vector(this IReadOnlyList<LevelProperty> properties, string name) =>
        properties.Find(name) is { StructName: "vector", Value.Length: 12 } property
            ? new Vector3(BitConverter.ToSingle(property.Value, 0), BitConverter.ToSingle(property.Value, 4),
                BitConverter.ToSingle(property.Value, 8))
            : null;

    public static Vector4? Vector4(this IReadOnlyList<LevelProperty> properties, string name) =>
        properties.Find(name) is { Value.Length: 16 } property
            ? new Vector4(BitConverter.ToSingle(property.Value, 0), BitConverter.ToSingle(property.Value, 4),
                BitConverter.ToSingle(property.Value, 8), BitConverter.ToSingle(property.Value, 12))
            : null;

    /// <summary>Pitch, yaw and roll in Unreal units (65536 a full turn).</summary>
    public static (int Pitch, int Yaw, int Roll)? Rotator(this IReadOnlyList<LevelProperty> properties, string name) =>
        properties.Find(name) is { StructName: "rotator", Value.Length: 12 } property
            ? (BinaryPrimitives.ReadInt32LittleEndian(property.Value), BinaryPrimitives.ReadInt32LittleEndian(property.Value.AsSpan(4)),
                BinaryPrimitives.ReadInt32LittleEndian(property.Value.AsSpan(8)))
            : null;

    public static float? Float(this IReadOnlyList<LevelProperty> properties, string name) =>
        properties.Find(name) is { Type: "floatproperty", Value.Length: 4 } property ? BitConverter.ToSingle(property.Value) : null;

    public static int? Int(this IReadOnlyList<LevelProperty> properties, string name) =>
        properties.Find(name) is { Type: "intproperty", Value.Length: 4 } property
            ? BinaryPrimitives.ReadInt32LittleEndian(property.Value) : null;

    public static string? Text(this IReadOnlyList<LevelProperty> properties, string name) => properties.Find(name)?.Text;

    /// <summary>A ByteProperty's value (a plain byte, not an enum name).</summary>
    public static int? Byte(this IReadOnlyList<LevelProperty> properties, string name) =>
        properties.Find(name) is { Type: "byteproperty", Value.Length: 1 } property ? property.Value[0] : null;

    /// <summary>An array of structs: each element is its own run of tagged properties.</summary>
    public static IReadOnlyList<IReadOnlyList<LevelProperty>> Structs(this IReadOnlyList<LevelProperty> properties, string name,
        IReadOnlyList<UnrealNameEntry> names)
    {
        if (properties.Find(name) is not { Type: "arrayproperty", Value.Length: >= 4 } property) return [];
        var count = BinaryPrimitives.ReadInt32LittleEndian(property.Value);
        var elements = new List<IReadOnlyList<LevelProperty>>();
        var cursor = 4;
        for (var index = 0; index < count && cursor < property.Value.Length; index++)
        {
            if (ReadAt(property.Value, names, cursor, out var end) is not { } element) break;
            elements.Add(element);
            cursor = end;
        }
        return elements;
    }

    public static bool Bool(this IReadOnlyList<LevelProperty> properties, string name) =>
        properties.Find(name) is { Type: "boolproperty" } property && property.BoolValue != 0;

    /// <summary>An object reference: above 0 an export of the package (1-based), below 0 an import.</summary>
    public static int Object(this IReadOnlyList<LevelProperty> properties, string name) =>
        properties.Find(name) is { Type: "objectproperty", Value.Length: 4 } property
            ? BinaryPrimitives.ReadInt32LittleEndian(property.Value) : 0;

    public static int[] Objects(this IReadOnlyList<LevelProperty> properties, string name)
    {
        if (properties.Find(name) is not { Type: "arrayproperty", Value.Length: >= 4 } property) return [];
        var count = BinaryPrimitives.ReadInt32LittleEndian(property.Value);
        if (count < 0 || 4 + count * 4L != property.Value.Length) return [];
        var values = new int[count];
        for (var index = 0; index < count; index++)
            values[index] = BinaryPrimitives.ReadInt32LittleEndian(property.Value.AsSpan(4 + index * 4));
        return values;
    }
}
