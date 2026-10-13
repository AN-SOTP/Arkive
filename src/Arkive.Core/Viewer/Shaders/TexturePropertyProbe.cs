using System.Buffers.Binary;
using Arkive.Core.Assets;

namespace Arkive.Core.Viewer.Shaders;

/// <summary>The sampling-relevant settings of a cooked Texture2D.</summary>
public sealed record TextureSamplingInfo(bool Srgb, string? CompressionSettings, string? Format);

/// <summary>
/// Reads a Texture2D export's tagged properties (SRGB, CompressionSettings, Format) to decide
/// whether the game samples it with sRGB decoding. Unset SRGB means the engine default (true);
/// normal, grayscale, displacement and HDR compression settings sample linearly.
/// </summary>
public static class TexturePropertyProbe
{
    private const int MaximumProperties = 256;

    public static TextureSamplingInfo Read(ReadOnlySpan<byte> export, IReadOnlyList<UnrealNameEntry> names)
    {
        for (var start = 0; start <= Math.Min(64, export.Length - 8); start += 4)
            foreach (var extendedIntTag in new[] { false, true })
                if (TryParse(export, start, names, extendedIntTag, out var info))
                    return info;
        return new TextureSamplingInfo(true, null, null);
    }

    public static bool SamplesSrgb(TextureSamplingInfo info) =>
        info.Srgb &&
        !(info.CompressionSettings ?? "").Contains("Normalmap", StringComparison.OrdinalIgnoreCase) &&
        !(info.CompressionSettings ?? "").Contains("Grayscale", StringComparison.OrdinalIgnoreCase) &&
        !(info.CompressionSettings ?? "").Contains("Displacement", StringComparison.OrdinalIgnoreCase) &&
        !(info.CompressionSettings ?? "").Contains("HighDynamicRange", StringComparison.OrdinalIgnoreCase) &&
        !(info.Format ?? "").Contains("BC5", StringComparison.OrdinalIgnoreCase) &&
        !(info.Format ?? "").Contains("BC4", StringComparison.OrdinalIgnoreCase) &&
        !(info.Format ?? "").Contains("FloatRGB", StringComparison.OrdinalIgnoreCase);

    private static bool TryParse(
        ReadOnlySpan<byte> bytes, int cursor, IReadOnlyList<UnrealNameEntry> names, bool extendedIntTag,
        out TextureSamplingInfo info)
    {
        info = null!;
        bool? srgb = null;
        string? compression = null;
        string? format = null;
        for (var property = 0; property < MaximumProperties; property++)
        {
            if (!TryName(bytes, ref cursor, names, out var name)) return false;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                if (property == 0) return false;
                info = new TextureSamplingInfo(srgb ?? true, compression, format);
                return true;
            }
            if (!TryName(bytes, ref cursor, names, out var type) ||
                !type.EndsWith("Property", StringComparison.OrdinalIgnoreCase) || cursor > bytes.Length - 8) return false;
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes[cursor..]);
            var arrayIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes[(cursor + 4)..]);
            cursor += 8;
            if (size < 0 || arrayIndex < 0) return false;
            string? enumName = null;
            if (type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryName(bytes, ref cursor, names, out enumName)) return false;
            }
            else if (extendedIntTag && type.Equals("IntProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryName(bytes, ref cursor, names, out _)) return false;
            }
            else if (type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (cursor >= bytes.Length) return false;
                if (name.Equals("SRGB", StringComparison.OrdinalIgnoreCase)) srgb = bytes[cursor] != 0;
                cursor++;
            }
            if (size > bytes.Length - cursor) return false;
            if (type.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase) && size == 8 && enumName is not null)
            {
                var valueCursor = cursor;
                if (TryName(bytes, ref valueCursor, names, out var value))
                {
                    if (name.Equals("CompressionSettings", StringComparison.OrdinalIgnoreCase)) compression = value;
                    else if (name.Equals("Format", StringComparison.OrdinalIgnoreCase)) format = value;
                }
            }
            cursor += size;
        }
        return false;
    }

    private static bool TryName(ReadOnlySpan<byte> bytes, ref int cursor, IReadOnlyList<UnrealNameEntry> names, out string value)
    {
        value = string.Empty;
        if (cursor < 0 || cursor > bytes.Length - 8) return false;
        var index = BinaryPrimitives.ReadInt32LittleEndian(bytes[cursor..]);
        var number = BinaryPrimitives.ReadInt32LittleEndian(bytes[(cursor + 4)..]);
        cursor += 8;
        if (index < 0 || index >= names.Count || number < 0) return false;
        value = new UnrealNameReference(index, number).Resolve(names);
        return true;
    }
}
