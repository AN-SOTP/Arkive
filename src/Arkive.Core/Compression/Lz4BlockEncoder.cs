namespace Arkive.Core.Compression;

/// <summary>
/// Small deterministic LZ4 block encoder for Arkive's UE3 bulk-data writer.
/// It emits ordinary raw LZ4 blocks (no frame header) understood by
/// <see cref="Lz4BlockDecoder"/>.
/// </summary>
public static class Lz4BlockEncoder
{
    private const int MinimumMatch = 4;
    private const int LastLiterals = 5;
    private const int MatchFindLimit = 12;
    private const int HashBits = 16;
    private const int HashSize = 1 << HashBits;

    public static byte[] Encode(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
        {
            return [];
        }

        var output = new List<byte>(source.Length + source.Length / 255 + 16);
        var table = new int[HashSize];
        Array.Fill(table, -1);
        var anchor = 0;
        var cursor = 0;
        // Standard LZ4 block decoders may use wild-copy loops. The format therefore
        // requires the final five bytes to be literals and the final match to begin at
        // least twelve bytes before the end of the block. Arkive's decoder is bounds
        // checked, but emitted blocks must also satisfy native UE3/UModel decoders.
        while (cursor <= source.Length - MatchFindLimit)
        {
            var sequence = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source[cursor..]);
            var hash = Hash(sequence);
            var match = table[hash];
            table[hash] = cursor;
            if (match < 0 || cursor - match > ushort.MaxValue ||
                !source.Slice(match, MinimumMatch).SequenceEqual(source.Slice(cursor, MinimumMatch)))
            {
                cursor++;
                continue;
            }

            var matchLength = MinimumMatch;
            while (cursor + matchLength < source.Length - LastLiterals &&
                   source[match + matchLength] == source[cursor + matchLength])
            {
                matchLength++;
            }

            WriteSequence(output, source[anchor..cursor], cursor - match, matchLength);
            cursor += matchLength;
            anchor = cursor;

            // Seed positions inside the match so immediately following runs can
            // reference them without changing deterministic output.
            var seedStart = Math.Max(anchor - matchLength + 1, 0);
            for (var seed = seedStart; seed < anchor && seed <= source.Length - MatchFindLimit; seed++)
            {
                var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source[seed..]);
                table[Hash(value)] = seed;
            }
        }

        WriteLastLiterals(output, source[anchor..]);
        return output.ToArray();
    }

    /// <summary>
    /// Emits one standards-compliant literal-only LZ4 sequence. Its stored size
    /// depends only on input length, which is useful for self-referential package
    /// metadata whose values contain the resulting compressed size.
    /// </summary>
    public static byte[] EncodeLiteralOnly(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty) return [];
        var output = new List<byte>(source.Length + source.Length / 255 + 16);
        WriteLastLiterals(output, source);
        return output.ToArray();
    }

    private static int Hash(uint value) =>
        (int)((value * 2654435761u) >> (32 - HashBits));

    private static void WriteSequence(
        List<byte> output,
        ReadOnlySpan<byte> literals,
        int matchOffset,
        int matchLength)
    {
        var literalNibble = Math.Min(15, literals.Length);
        var matchNibble = Math.Min(15, matchLength - MinimumMatch);
        output.Add((byte)((literalNibble << 4) | matchNibble));
        WriteExtendedLength(output, literals.Length, literalNibble);
        foreach (var value in literals) output.Add(value);
        output.Add((byte)matchOffset);
        output.Add((byte)(matchOffset >> 8));
        WriteExtendedLength(output, matchLength - MinimumMatch, matchNibble);
    }

    private static void WriteLastLiterals(List<byte> output, ReadOnlySpan<byte> literals)
    {
        var literalNibble = Math.Min(15, literals.Length);
        output.Add((byte)(literalNibble << 4));
        WriteExtendedLength(output, literals.Length, literalNibble);
        foreach (var value in literals) output.Add(value);
    }

    private static void WriteExtendedLength(List<byte> output, int length, int nibble)
    {
        if (nibble != 15) return;
        var remaining = length - 15;
        while (remaining >= byte.MaxValue)
        {
            output.Add(byte.MaxValue);
            remaining -= byte.MaxValue;
        }
        output.Add((byte)remaining);
    }
}
