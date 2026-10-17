using System.Buffers.Binary;
using Arkive.Core.Assets;

namespace Arkive.Core.Particles;

/// <summary>One tagged property of a particle export, with its value's place in the export's bytes.</summary>
/// <param name="ValueOffset">Where the value starts, counted from the start of the export.</param>
/// <param name="Children">The properties inside a struct value (a RawDistribution's LookupTable…), when it has any.</param>
public sealed record ParticleProperty(
    string Name,
    string Type,
    string? StructName,
    int ArrayIndex,
    int ValueOffset,
    int Size,
    IReadOnlyList<ParticleProperty> Children)
{
    public ParticleProperty? Child(string name) =>
        Children.FirstOrDefault(child => child.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads the tagged property stream of Lost Ark's particle exports (systems, emitters, LOD levels, modules,
/// distributions). Every one of them is a plain property stream with no native data after it. The client's format
/// differs from stock Unreal 3 in three ways: names are stored in lower case, a BoolProperty's value is a single byte
/// after its size and index (so the stream goes unaligned), and an IntProperty tag carries an extra name, as a
/// StructProperty or ByteProperty tag does.
/// </summary>
public static class ParticlePropertyStream
{
    private const int MaximumProperties = 1024;

    /// <summary>
    /// The export's properties, or null when no start offset gives a stream that ends exactly at the end of the
    /// export (particle objects start at byte 4, distributions at byte 8).
    /// </summary>
    public static IReadOnlyList<ParticleProperty>? Read(ReadOnlySpan<byte> export, IReadOnlyList<UnrealNameEntry> names)
    {
        for (var start = 0; start <= Math.Min(64, export.Length - 8); start += 4)
        {
            var properties = new List<ParticleProperty>();
            if (TryParse(export, start, export.Length, names, properties, out var end) && end == export.Length && properties.Count > 0)
                return properties;
        }
        return null;
    }

    private static bool TryParse(ReadOnlySpan<byte> bytes, int start, int limit, IReadOnlyList<UnrealNameEntry> names,
        List<ParticleProperty> output, out int end)
    {
        var cursor = start;
        end = start;
        for (var count = 0; count < MaximumProperties; count++)
        {
            if (!TryName(bytes, ref cursor, limit, names, out var name)) return false;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                end = cursor;
                return true;
            }
            if (!TryName(bytes, ref cursor, limit, names, out var type) ||
                !type.EndsWith("property", StringComparison.OrdinalIgnoreCase) || cursor > limit - 8)
                return false;
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes[cursor..]);
            var arrayIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes[(cursor + 4)..]);
            cursor += 8;
            if (size < 0 || arrayIndex < 0) return false;
            string? structName = null;
            if (Is(type, "structproperty") || Is(type, "byteproperty") || Is(type, "intproperty"))
            {
                if (!TryName(bytes, ref cursor, limit, names, out var extra)) return false;
                structName = extra;
            }
            else if (Is(type, "boolproperty"))
            {
                if (cursor >= limit) return false;
                output.Add(new(name, type.ToLowerInvariant(), null, arrayIndex, cursor, 1, []));
                cursor += 1;
                continue;
            }
            if (size > limit - cursor) return false;
            var children = new List<ParticleProperty>();
            if (Is(type, "structproperty") && size > 8 &&
                !(TryParse(bytes, cursor, cursor + size, names, children, out var childEnd) && childEnd == cursor + size))
                children.Clear();
            output.Add(new(name, type.ToLowerInvariant(), structName, arrayIndex, cursor, size, children));
            cursor += size;
        }
        return false;
    }

    private static bool Is(string type, string expected) => type.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool TryName(ReadOnlySpan<byte> bytes, ref int cursor, int limit, IReadOnlyList<UnrealNameEntry> names,
        out string value)
    {
        value = string.Empty;
        if (cursor < 0 || cursor > limit - 8) return false;
        var index = BinaryPrimitives.ReadInt32LittleEndian(bytes[cursor..]);
        var number = BinaryPrimitives.ReadInt32LittleEndian(bytes[(cursor + 4)..]);
        cursor += 8;
        if (index < 0 || index >= names.Count || number < 0 || number > 1_000_000) return false;
        value = new UnrealNameReference(index, number).Resolve(names);
        return true;
    }
}
