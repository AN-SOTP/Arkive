using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Arkive.Core.Viewer.Shaders;

/// <summary>
/// A UE3 <c>FStaticParameterSet</c>: the base material plus the static switch and component mask values
/// a compiled shader permutation was built with. Material instances store the one they use, and every
/// material shader map in the cache opens with the one it was compiled for, so it is the variant key.
///
/// Layout: base material GUID; u32 n, n x (name, u32 value, u32 override, GUID) switches;
/// u32 n, n x (name, u32 R, G, B, A, u32 override, GUID) component masks; u32 n, n x (name, u8
/// compression, u32 override, GUID) normal parameters; u32 n, n x (name, i32 weightmap, u32 override,
/// GUID) terrain layer weights. Switches and masks are identified by their expression GUIDs, which do
/// not depend on a package's name table.
/// </summary>
public sealed record StaticParameterSet(
    string BaseMaterialId,
    IReadOnlyList<(string Expression, bool Value)> Switches,
    IReadOnlyList<(string Expression, int Mask)> ComponentMasks)
{
    private const int MaxParameters = 512;

    /// <summary>A short hash of the switch and mask values, equal for equal permutations.</summary>
    public string Key
    {
        get
        {
            var text = new StringBuilder(BaseMaterialId);
            foreach (var (expression, value) in Switches.OrderBy(item => item.Expression, StringComparer.Ordinal))
                text.Append('|').Append(expression).Append(value ? '1' : '0');
            foreach (var (expression, mask) in ComponentMasks.OrderBy(item => item.Expression, StringComparer.Ordinal))
                text.Append('#').Append(expression).Append(mask);
            return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(text.ToString())), 0, 8);
        }
    }

    /// <summary>The switches that are on, by a short prefix of their expression GUID (for nearest matches).</summary>
    public IReadOnlyList<string> OnSwitches =>
        Switches.Where(item => item.Value).Select(item => item.Expression[..8]).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Reads a set whose base material GUID starts at <paramref name="at"/>.</summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, int at, out StaticParameterSet set, out int end)
    {
        set = null!;
        end = at;
        if (at < 0 || at + 16 > bytes.Length) return false;
        var baseId = Convert.ToHexString(bytes.Slice(at, 16));
        var position = at + 16;

        if (!TryCount(bytes, ref position, 32, out var count)) return false;
        var switches = new List<(string, bool)>(count);
        for (var index = 0; index < count; index++, position += 32)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(position + 8)..]);
            var overridden = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(position + 12)..]);
            if (value > 1 || overridden > 1) return false;
            switches.Add((Convert.ToHexString(bytes.Slice(position + 16, 16)), value == 1));
        }

        if (!TryCount(bytes, ref position, 44, out count)) return false;
        var masks = new List<(string, int)>(count);
        for (var index = 0; index < count; index++, position += 44)
        {
            var mask = 0;
            for (var channel = 0; channel < 5; channel++)
            {
                var flag = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(position + 8 + channel * 4)..]);
                if (flag > 1) return false;
                if (channel < 4 && flag == 1) mask |= 1 << channel;
            }
            masks.Add((Convert.ToHexString(bytes.Slice(position + 28, 16)), mask));
        }

        if (!TryCount(bytes, ref position, 29, out count)) return false;
        position += count * 29;
        if (!TryCount(bytes, ref position, 32, out count)) return false;
        position += count * 32;

        set = new StaticParameterSet(baseId, switches, masks);
        end = position;
        return true;
    }

    private static bool TryCount(ReadOnlySpan<byte> bytes, ref int position, int entrySize, out int count)
    {
        count = 0;
        if (position + 4 > bytes.Length) return false;
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]);
        if (value > MaxParameters || position + 4 + (long)value * entrySize > bytes.Length) return false;
        count = (int)value;
        position += 4;
        return true;
    }
}
