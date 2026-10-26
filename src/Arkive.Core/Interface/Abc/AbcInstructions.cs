namespace Arkive.Core.Interface.Abc;

/// <summary>Walks AVM2 method bodies instruction by instruction (operand sizes from the AVM2 overview).</summary>
public static class AbcInstructions
{
    private enum Operands { None, U30, U30U30, U8, S24, Switch, Debug }

    private static readonly Operands[] Table = BuildTable();

    /// <summary>The start offset of every instruction in a method body.</summary>
    public static IEnumerable<int> Starts(byte[] code)
    {
        var position = 0;
        while (position < code.Length)
        {
            yield return position;
            position = Next(code, position);
        }
    }

    /// <summary>The offset of the instruction after the one at <paramref name="position"/>.</summary>
    public static int Next(byte[] code, int position)
    {
        var opcode = code[position++];
        switch (Table[opcode])
        {
            case Operands.U30:
                SkipU30(code, ref position);
                break;
            case Operands.U30U30:
                SkipU30(code, ref position);
                SkipU30(code, ref position);
                break;
            case Operands.U8:
                position++;
                break;
            case Operands.S24:
                position += 3;
                break;
            case Operands.Switch:
                position += 3;
                var cases = ReadU30(code, ref position);
                position += 3 * (cases + 1);
                break;
            case Operands.Debug:
                position++;
                SkipU30(code, ref position);
                position++;
                SkipU30(code, ref position);
                break;
        }
        return position;
    }

    /// <summary>
    /// Rewrites every <c>pushnull; coerce T</c> and <c>pushnull; astype T</c> to <c>pushnull; coerce_a</c> padded with
    /// nops, keeping every offset. The Haxe compiler emits these typed nulls; the game's own code never does, and
    /// Scaleform's player is not known to handle them. Returns how many it rewrote.
    /// </summary>
    public static int UntypeNulls(AbcMethodBody body)
    {
        var code = body.Code;
        var rewritten = 0;
        var previous = -1;
        foreach (var start in Starts(code).ToArray())
        {
            if (previous >= 0 && code[previous] == 0x20 && code[start] is 0x80 or 0x86)
            {
                var end = Next(code, start);
                code[start] = 0x82; // coerce_a
                for (var index = start + 1; index < end; index++) code[index] = 0x02; // nop
                rewritten++;
            }
            previous = start;
        }
        return rewritten;
    }

    private static void SkipU30(byte[] code, ref int position) => ReadU30(code, ref position);

    private static int ReadU30(byte[] code, ref int position)
    {
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var value = code[position++];
            result |= (value & 0x7F) << shift;
            if ((value & 0x80) == 0) break;
        }
        return result;
    }

    private static Operands[] BuildTable()
    {
        var table = new Operands[256];
        foreach (var opcode in new[]
                 {
                     0x04, 0x05, 0x06, 0x08, 0x25, 0x2C, 0x2D, 0x2E, 0x2F, 0x31, 0x40, 0x41, 0x42, 0x49, 0x53, 0x55, 0x56,
                     0x58, 0x59, 0x5A, 0x5D, 0x5E, 0x5F, 0x60, 0x61, 0x62, 0x63, 0x65, 0x66, 0x68, 0x6A, 0x6C, 0x6D, 0x6E,
                     0x6F, 0x80, 0x86, 0x92, 0x94, 0xB2, 0xC2, 0xC3, 0xF0, 0xF1, 0xF2,
                 })
            table[opcode] = Operands.U30;
        foreach (var opcode in new[] { 0x32, 0x43, 0x44, 0x45, 0x46, 0x4A, 0x4C, 0x4E, 0x4F })
            table[opcode] = Operands.U30U30;
        table[0x24] = Operands.U8;
        for (var opcode = 0x0C; opcode <= 0x1A; opcode++) table[opcode] = Operands.S24;
        table[0x1B] = Operands.Switch;
        table[0xEF] = Operands.Debug;
        return table;
    }
}
