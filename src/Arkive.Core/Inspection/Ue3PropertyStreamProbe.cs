using System.Buffers.Binary;
using System.Globalization;
using Arkive.Core.Assets;

namespace Arkive.Core.Inspection;

internal sealed record Ue3PropertyStreamCandidate(
    int StartOffset,
    int EndOffset,
    IReadOnlyList<SkeletalMeshPropertyEvidence> Properties);

/// <summary>
/// Shared bounded UE3 tagged-property probe. Callers must validate the bytes
/// after EndOffset with their class-specific native serializer before trusting
/// a candidate.
/// </summary>
internal static class Ue3PropertyStreamProbe
{
    private const int MaximumPropertyCount = 4_096;

    public static IReadOnlyList<Ue3PropertyStreamCandidate> FindCandidates(
        ReadOnlySpan<byte> bytes,
        long serialOffset,
        IReadOnlyList<UnrealNameEntry> names,
        int maximumStartBytes = 64 * 1024)
    {
        var maximumStart = Math.Min(maximumStartBytes, Math.Max(0, bytes.Length - 8));
        var results = new List<Ue3PropertyStreamCandidate>();
        var keys = new HashSet<(int Start, int End)>();
        for (var start = 0; start <= maximumStart; start += 4)
        {
            foreach (var extendedIntTag in new[] { false, true })
            {
                if (!TryParse(bytes, serialOffset, names, start, extendedIntTag, out var candidate) ||
                    !keys.Add((candidate.StartOffset, candidate.EndOffset)))
                    continue;
                results.Add(candidate);
            }
        }
        return results;
    }

    private static bool TryParse(
        ReadOnlySpan<byte> bytes,
        long serialOffset,
        IReadOnlyList<UnrealNameEntry> names,
        int start,
        bool extendedIntTag,
        out Ue3PropertyStreamCandidate result)
    {
        result = null!;
        var cursor = start;
        var properties = new List<SkeletalMeshPropertyEvidence>();
        for (var propertyIndex = 0; propertyIndex < MaximumPropertyCount; propertyIndex++)
        {
            var tagStart = cursor;
            if (!TryReadName(bytes, ref cursor, names, out var propertyName)) return false;
            if (propertyName.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                result = new Ue3PropertyStreamCandidate(start, cursor, properties);
                return true;
            }
            if (!TryReadName(bytes, ref cursor, names, out var propertyType) ||
                cursor > bytes.Length - 8 ||
                !propertyType.EndsWith("Property", StringComparison.OrdinalIgnoreCase))
                return false;
            var size = ReadInt32(bytes, cursor);
            var arrayIndex = ReadInt32(bytes, cursor + 4);
            cursor += 8;
            if (size < 0 || arrayIndex < 0) return false;

            string? metadata = null;
            byte? taggedBool = null;
            if (propertyType.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) ||
                propertyType.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadName(bytes, ref cursor, names, out metadata)) return false;
            }
            else if (extendedIntTag && propertyType.Equals("IntProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadName(bytes, ref cursor, names, out var marker)) return false;
                metadata = $"Lost Ark tag marker: {marker}";
            }
            else if (propertyType.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (cursor >= bytes.Length) return false;
                taggedBool = bytes[cursor++];
            }

            if (size > bytes.Length - cursor) return false;
            var value = bytes.Slice(cursor, size);
            properties.Add(new SkeletalMeshPropertyEvidence(
                tagStart,
                serialOffset + tagStart,
                propertyName,
                propertyType,
                size,
                arrayIndex,
                metadata,
                cursor,
                Preview(propertyType, value, taggedBool, names),
                Convert.ToHexString(value[..Math.Min(16, value.Length)])));
            cursor += size;
        }
        return false;
    }

    private static string Preview(
        string type,
        ReadOnlySpan<byte> value,
        byte? taggedBool,
        IReadOnlyList<UnrealNameEntry> names)
    {
        if (type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase) && taggedBool is not null)
            return taggedBool == 0 ? "False" : "True";
        if ((type.Equals("IntProperty", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("ObjectProperty", StringComparison.OrdinalIgnoreCase)) && value.Length >= 4)
            return ReadInt32(value, 0).ToString(CultureInfo.InvariantCulture);
        if (type.Equals("FloatProperty", StringComparison.OrdinalIgnoreCase) && value.Length >= 4)
            return BitConverter.Int32BitsToSingle(ReadInt32(value, 0)).ToString("R", CultureInfo.InvariantCulture);
        if (type.Equals("NameProperty", StringComparison.OrdinalIgnoreCase) && value.Length >= 8)
        {
            var cursor = 0;
            if (TryReadName(value, ref cursor, names, out var name)) return name;
        }
        return value.IsEmpty ? "(empty)" : Convert.ToHexString(value[..Math.Min(16, value.Length)]);
    }

    private static bool TryReadName(
        ReadOnlySpan<byte> bytes,
        ref int cursor,
        IReadOnlyList<UnrealNameEntry> names,
        out string value)
    {
        value = string.Empty;
        if (cursor < 0 || cursor > bytes.Length - 8) return false;
        var index = ReadInt32(bytes, cursor);
        var number = ReadInt32(bytes, cursor + 4);
        cursor += 8;
        if (index < 0 || index >= names.Count || number < 0) return false;
        value = new UnrealNameReference(index, number).Resolve(names);
        return true;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
}
