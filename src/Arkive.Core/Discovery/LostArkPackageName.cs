namespace Arkive.Core.Discovery;

/// <summary>
/// Resolves the reversible filename encoding used by Lost Ark packages. This
/// only produces an in-memory alias; Arkive never renames the source package.
/// </summary>
public static class LostArkPackageName
{
    private static readonly (string Encoded, char Decoded, int Position)[] Substitutions =
    [
        ("QP", 'Q', 0), ("QD", 'Q', 1), ("QW", 'Q', 2), ("Q4", 'Q', 3),
        ("QL", '-', 0), ("QB", '-', 1), ("QO", '-', 2), ("Q5", '-', 3),
        ("QC", '_', 0), ("QN", '_', 1), ("QT", '_', 2), ("Q9", '_', 3),
        ("XU", 'X', 0), ("XN", 'X', 1), ("XH", 'X', 2), ("X3", 'X', 3),
        ("XW", '!', 0), ("XS", '!', 1), ("XZ", '!', 2), ("X0", '!', 3),
    ];

    public static bool TryDecodeFileName(string pathOrFileName, out string decodedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrFileName);
        var stem = Path.GetFileNameWithoutExtension(pathOrFileName).ToUpperInvariant();
        decodedName = string.Empty;

        if (stem.Length < 20 || stem.Any(character =>
                !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character)))
        {
            return false;
        }

        var shifted = new char[stem.Length];
        for (var index = 0; index < stem.Length; index++)
        {
            var value = ToBase36Ascii(stem[index]);
            var decoded = Mod(31 * (value - stem.Length - 'A'), 36) + 'A';
            if (decoded >= '[')
            {
                decoded -= 43;
            }

            shifted[index] = (char)decoded;
        }

        var unescaped = Unescape(new string(shifted));
        var paddingMarker = unescaped.IndexOf('!');
        if (paddingMarker >= 0)
        {
            unescaped = unescaped[..paddingMarker];
        }

        if (string.IsNullOrWhiteSpace(unescaped) ||
            unescaped.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
            unescaped.Any(character => character is < ' ' or > '~') ||
            !Encode(unescaped).Equals(stem, StringComparison.Ordinal))
        {
            return false;
        }

        decodedName = unescaped.ToLowerInvariant();
        return true;
    }

    public static string GetDisplayName(string pathOrFileName) =>
        TryDecodeFileName(pathOrFileName, out var decoded)
            ? decoded
            : Path.GetFileNameWithoutExtension(pathOrFileName);

    private static string Unescape(string input)
    {
        var output = new System.Text.StringBuilder(input.Length);
        var index = 0;
        while (index < input.Length)
        {
            var substitution = Substitutions.FirstOrDefault(candidate =>
                candidate.Position == index % 4 &&
                input.AsSpan(index).StartsWith(candidate.Encoded, StringComparison.Ordinal));
            if (substitution.Encoded is not null)
            {
                output.Append(substitution.Decoded);
                index += substitution.Encoded.Length;
            }
            else
            {
                output.Append(input[index]);
                index++;
            }
        }

        return output.ToString();
    }

    private static string Encode(string original)
    {
        var input = Escape(original);
        var unpaddedLength = original.Length;
        if (unpaddedLength < 20)
        {
            input = Escape(original + "!" + new string('.', 20 - unpaddedLength - 1));
        }

        var characters = input.ToCharArray();
        var output = new System.Text.StringBuilder(characters.Length);
        for (var index = 0; index < characters.Length; index++)
        {
            var value = ToBase36Ascii(characters[index]);
            var encoded = Mod(input.Length + (7 * (value - 'A')), 36) + 'A';
            if (encoded >= '[')
            {
                encoded -= 43;
            }

            var encodedCharacter = (char)encoded;
            output.Append(encodedCharacter);
            if (index + unpaddedLength < characters.Length &&
                characters[index + unpaddedLength] == '.')
            {
                characters[index + unpaddedLength] = encodedCharacter;
            }
        }

        return output.ToString();
    }

    private static string Escape(string input)
    {
        var output = new System.Text.StringBuilder(input.Length);
        foreach (var character in input.ToUpperInvariant())
        {
            var substitution = Substitutions.FirstOrDefault(candidate =>
                candidate.Decoded == character && candidate.Position == output.Length % 4);
            if (substitution.Encoded is not null)
            {
                output.Append(substitution.Encoded);
            }
            else
            {
                output.Append(character);
            }
        }

        return output.ToString();
    }

    private static int ToBase36Ascii(char character) =>
        char.IsAsciiDigit(character) ? character + 43 : character;

    private static int Mod(int value, int modulus) => ((value % modulus) + modulus) % modulus;
}
