namespace Arkive.Core.Compression;

public static class Lz4BlockDecoder
{
    public static byte[] Decode(ReadOnlySpan<byte> source, int expectedLength)
    {
        if (expectedLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLength));
        }

        var output = new byte[expectedLength];
        var sourceIndex = 0;
        var outputIndex = 0;

        while (sourceIndex < source.Length && outputIndex < output.Length)
        {
            var token = source[sourceIndex++];
            var literalLength = ReadExtendedLength(source, ref sourceIndex, token >> 4);

            if (literalLength > source.Length - sourceIndex ||
                literalLength > output.Length - outputIndex)
            {
                throw new InvalidDataException("LZ4 literal run exceeds its input or output buffer.");
            }

            source.Slice(sourceIndex, literalLength)
                .CopyTo(output.AsSpan(outputIndex, literalLength));
            sourceIndex += literalLength;
            outputIndex += literalLength;

            if (sourceIndex == source.Length || outputIndex == output.Length)
            {
                break;
            }

            if (source.Length - sourceIndex < 2)
            {
                throw new InvalidDataException("LZ4 match offset is truncated.");
            }

            var matchOffset = source[sourceIndex] | (source[sourceIndex + 1] << 8);
            sourceIndex += 2;
            if (matchOffset == 0 || matchOffset > outputIndex)
            {
                throw new InvalidDataException($"LZ4 match offset {matchOffset} is invalid.");
            }

            var matchLength = ReadExtendedLength(source, ref sourceIndex, token & 0x0F) + 4;
            if (matchLength > output.Length - outputIndex)
            {
                throw new InvalidDataException("LZ4 match exceeds the expected output size.");
            }

            var matchIndex = outputIndex - matchOffset;
            for (var index = 0; index < matchLength; index++)
            {
                output[outputIndex++] = output[matchIndex + index];
            }
        }

        if (outputIndex != expectedLength)
        {
            throw new InvalidDataException(
                $"LZ4 decoded {outputIndex:N0} bytes; expected {expectedLength:N0}.");
        }

        return output;
    }

    private static int ReadExtendedLength(
        ReadOnlySpan<byte> source,
        ref int sourceIndex,
        int initialLength)
    {
        var length = initialLength;
        if (initialLength != 15)
        {
            return length;
        }

        while (true)
        {
            if (sourceIndex >= source.Length)
            {
                throw new InvalidDataException("LZ4 extended length is truncated.");
            }

            var extension = source[sourceIndex++];
            length = checked(length + extension);
            if (extension != byte.MaxValue)
            {
                return length;
            }
        }
    }
}
