namespace Arkive.Core.Audio;

/// <summary>
/// Lost Ark scrambles every file in EFGame\ReleasePC\WwiseAudioPackage (the .pck packages and the loose .wem music)
/// the same way: a 4-byte marker, then the real file XORed with a fixed 854-byte key and a counter that steps every
/// 854 bytes. XOR is its own inverse, so one call both reads and writes. The key was recovered from the game's own
/// package tables and checked against every package, sound bank and audio stream of the 2026-10 client.
/// </summary>
public static class WwiseAudioCipher
{
    /// <summary>Bytes in front of the scrambled content; offsets inside a file are counted after them.</summary>
    public const int MarkerLength = 4;

    private const int KeyLength = 854;

    private static readonly byte[] MarkerBytes = [0x3E, 0xCE, 0xA6, 0x74];

    private static readonly byte[] Key = Convert.FromBase64String(
        "vAwy7nLjGuv5VykCRBWEsYDbiEQzguErcGOSELmBCC1yVCICbstFnpcGk61U6+GVDuw5L4AtnTkpBn0OUQkyd0/7ju/ygUO0leirLO0z1oOB" +
        "+wLMSNirRLXn12dNNk2N0xtdWFzxoouphJiXgXFK9C75jFUGZC92T0o5CqjaZl6aHVfS1X4mj1h2IsM/E8JEnOE4U6n3hm7q5ggSt5fhzR45" +
        "+2MZdx+v0vz7j0FrNjyH823FoRK0opbn5u12FNbAk1SlBRZeKiXCiICs+vYtf6/KisMqGywLhFnMMY8xOL0lOvrsxTElKMlHAxiQB6aeumc+" +
        "qogSlncnmX+8kfiUkFDBhsD6P7l+pWL54RgTen2xLXYo8ow0E6OCxCuwZ5XQsnHHzX/lo3usvgUmGLRfFB8CC2SBbJ3CjXMcd3mNzD3LKS+l" +
        "S6v8yon6ejOGDfnuI7AMrlsxTkXSBEAMySsBH/HGWu6EuchyBT/ahvFqPH95LFTIgR3hmdno0Udr7wFd+FtAv7IKaWjnmKkcVsZTaMfAkpzV" +
        "OJzT5L8Um+aABxQ9fIyVKnzLbsLq3+dzxEMTlGl/ql1kh8C9a9ny9M2BZpGU3dk5vD5L8yPZ6VezXx98AQZ9AVy12PTL172h2HnzaXlW3uZp" +
        "2NYrNLU6iZxoCQu5dfLGBGQ03QTjbOR/qBup5IDdXYA4EigvUcyle05XY82x5mKFLWUUIQDhhGvwqoqakKQbiauKgWPKl7GXjmK0iwkXzcC3" +
        "I7YbyTt/VY27PaVCC+qpPNkiJmX+upWyfJKdLPdi2dzI4mztAL5vCivVDlkLVIJ3C08v86Zat2ggaE2o4SmUWWLIeCh9uzsashw6Kas7picP" +
        "JSGnMuQ2bioEzJdxxSgCQLP2Qtta+U8TFEGKII675MAmmLsH6yZ8kQr9eO6jqOvyOOj2RGNKtCvQlB2jqk6aR+bVSi1r+VONea/RW4Bgb2tV" +
        "lualxKBc2+KQNlcJ2OW7YjEHzV2+hz6Uacmyd1iqbyWxThRm84JGeG5J2sL6hKDIFvhOWaAOlg6oEFrVqWOJ5MUyVonRVG2uwFTudKx6mOQo" +
        "6GFfWxaDorcFveInb84LvvT7xy0W9sGKn5hMHyFhLXd0rb8BKomurrPRy5Y=");

    public static ReadOnlySpan<byte> Marker => MarkerBytes;

    /// <summary>True when <paramref name="start"/> (the first bytes of a file) carries the scramble marker.</summary>
    public static bool HasMarker(ReadOnlySpan<byte> start) =>
        start.Length >= MarkerLength && start[..MarkerLength].SequenceEqual(MarkerBytes);

    /// <summary>
    /// Scrambles or unscrambles <paramref name="data"/> in place. <paramref name="payloadOffset"/> is where the data
    /// starts in the file, counted after the marker.
    /// </summary>
    public static void Apply(Span<byte> data, long payloadOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadOffset);
        var keyIndex = (int)(payloadOffset % KeyLength);
        var counter = (int)(payloadOffset / KeyLength % 255);
        for (var index = 0; index < data.Length; index++)
        {
            data[index] ^= (byte)(Key[keyIndex] ^ counter);
            if (++keyIndex == KeyLength)
            {
                keyIndex = 0;
                if (++counter == 255) counter = 0;
            }
        }
    }
}

/// <summary>Reads and writes the content of one WwiseAudioPackage file, scrambled or not.</summary>
public sealed class WwiseAudioFile : IDisposable
{
    private readonly FileStream _stream;

    private WwiseAudioFile(FileStream stream, bool scrambled)
    {
        _stream = stream;
        Scrambled = scrambled;
    }

    public bool Scrambled { get; }

    /// <summary>Where the content starts in the file: after the marker when the file is scrambled.</summary>
    public int ContentStart => Scrambled ? WwiseAudioCipher.MarkerLength : 0;

    /// <summary>The content length (the file length without the marker).</summary>
    public long ContentLength => _stream.Length - ContentStart;

    public static WwiseAudioFile Open(string path, bool writable = false)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            writable ? FileAccess.ReadWrite : FileAccess.Read,
            writable ? FileShare.Read : FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.RandomAccess);
        try
        {
            Span<byte> marker = stackalloc byte[WwiseAudioCipher.MarkerLength];
            var read = stream.ReadAtLeast(marker, marker.Length, throwOnEndOfStream: false);
            return new WwiseAudioFile(stream, WwiseAudioCipher.HasMarker(marker[..read]));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Reads <paramref name="count"/> plain bytes of content starting at <paramref name="offset"/>.</summary>
    public byte[] Read(long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > ContentLength)
            throw new InvalidDataException($"A read of {count:N0} bytes at {offset:N0} runs past the end of {_stream.Name}.");
        var buffer = new byte[count];
        _stream.Position = ContentStart + offset;
        _stream.ReadExactly(buffer);
        if (Scrambled) WwiseAudioCipher.Apply(buffer, offset);
        return buffer;
    }

    /// <summary>Writes plain bytes as content at <paramref name="offset"/> (scrambling them when the file is).</summary>
    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var buffer = data.ToArray();
        if (Scrambled) WwiseAudioCipher.Apply(buffer, offset);
        _stream.Position = ContentStart + offset;
        _stream.Write(buffer);
    }

    /// <summary>Cuts the content back to <paramref name="contentLength"/> bytes.</summary>
    public void Truncate(long contentLength) => _stream.SetLength(ContentStart + contentLength);

    public void Flush() => _stream.Flush(flushToDisk: true);

    public void Dispose() => _stream.Dispose();

    /// <summary>A whole loose file's plain content.</summary>
    public static byte[] ReadAll(string path)
    {
        using var file = Open(path);
        return file.Read(0, checked((int)file.ContentLength));
    }

    /// <summary>The bytes of a scrambled file holding <paramref name="content"/>, as the game stores it.</summary>
    public static byte[] Scramble(ReadOnlySpan<byte> content)
    {
        var bytes = new byte[WwiseAudioCipher.MarkerLength + content.Length];
        WwiseAudioCipher.Marker.CopyTo(bytes);
        content.CopyTo(bytes.AsSpan(WwiseAudioCipher.MarkerLength));
        WwiseAudioCipher.Apply(bytes.AsSpan(WwiseAudioCipher.MarkerLength), 0);
        return bytes;
    }
}
