using System.Buffers.Binary;

namespace Arkive.Core.LoginScreens;

/// <summary>
/// The scramble on the game's login videos (EFGame\Movies\*.ipk). Each is a Bink 2 video XORed with its own 48-byte
/// key, and every key is one shared 48-byte part XOR a 16-byte part of the file's own. So any video's key is
/// <see cref="Reference"/> (one video's key) XOR a repeating 16 bytes, and those 16 bytes come from the header fields
/// every login video shares: 'KB2' at 0, the file size - 8 at 4, the frame count at 8 (read from the frame index at 48,
/// whose first entry is 48 + 4 * (frames + 1) and whose high byte is 0) and the frame rate's numerator at 28
/// (10,000,000 / 333,333 in most, 30 / 1 in the oldest).
/// </summary>
public static class LoginVideoCipher
{
    public const int KeyLength = 48;
    private const int IndexStart = 48;
    private static readonly uint[] FrameRates = [10_000_000, 30, 30_000, 60, 25, 24];

    // The key of login_cinema_eclipse.
    private static readonly byte[] Reference = Convert.FromHexString(
        "7935565f598635c648b680ec039d4e85e33afa8c6f5aafc9e465b6309992e256d5e66083c38999157e6a1ae3af4e7859");

    /// <summary>The key every other one is found from (login_cinema_eclipse's).</summary>
    public static ReadOnlySpan<byte> ReferenceKey => Reference;

    /// <summary>
    /// The video's key, or null when the file does not read as a login video with it (another format, or a
    /// damaged file). The frame index must then run in order up to the file's end.
    /// </summary>
    public static byte[]? FindKey(string path)
    {
        using var stream = File.OpenRead(path);
        var size = stream.Length;
        if (size < 1024) return null;
        var head = new byte[IndexStart + 4];
        stream.ReadExactly(head);
        var pattern = new byte[16];
        void Learn(int at, ReadOnlySpan<byte> plain)
        {
            for (var index = 0; index < plain.Length; index++)
                pattern[(at + index) % 16] = (byte)(head[at + index] ^ Reference[(at + index) % KeyLength] ^ plain[index]);
        }

        Learn(0, "KB2"u8);
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)(size - 8));
        Learn(4, word);
        // Index entry 0's high byte (file offset 51) is 0; its low three bytes use the pattern's first three.
        pattern[3] = (byte)(head[IndexStart + 3] ^ Reference[3]);
        var first = 0u;
        for (var index = 0; index < 4; index++)
            first |= (uint)(head[IndexStart + index] ^ Reference[index] ^ pattern[index]) << (8 * index);
        var frames = (long)((first & ~1u) - IndexStart) / 4 - 1;
        if (frames is < 1 or > 100_000) return null;
        BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)frames);
        Learn(8, word);
        stream.Position = 28;
        stream.ReadExactly(head.AsSpan(28, 4));
        foreach (var rate in FrameRates)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(word, rate);
            Learn(28, word);
            var key = new byte[KeyLength];
            for (var index = 0; index < KeyLength; index++) key[index] = (byte)(Reference[index] ^ pattern[index % 16]);
            if (Fits(stream, key, frames, size)) return key;
        }
        return null;
    }

    private static bool Fits(FileStream stream, byte[] key, long frames, long size)
    {
        var header = Read(stream, key, 0, IndexStart);
        if (!header.AsSpan(0, 3).SequenceEqual("KB2"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)) != frames) return false;
        var width = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        var height = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
        if (width is 0 or > 16384 || height is 0 or > 16384) return false;
        var index = Read(stream, key, IndexStart, checked((int)(4 * (frames + 1))));
        var previous = 0L;
        for (var frame = 0; frame <= frames; frame++)
        {
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(4 * frame)) & ~1u;
            if (offset <= previous || offset > size) return false;
            previous = offset;
        }
        return previous == size;
    }

    private static byte[] Read(FileStream stream, byte[] key, long offset, int count)
    {
        var data = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(data);
        Apply(data, key, offset);
        return data;
    }

    /// <summary>XORs <paramref name="data"/>, which starts at <paramref name="offset"/> in its file, with a key.</summary>
    public static void Apply(Span<byte> data, ReadOnlySpan<byte> key, long offset)
    {
        var at = (int)(offset % KeyLength);
        for (var index = 0; index < data.Length; index++)
        {
            data[index] ^= key[at];
            if (++at == KeyLength) at = 0;
        }
    }

    /// <summary>
    /// Writes <paramref name="source"/>'s video to <paramref name="destination"/> scrambled with another key, so the
    /// game reads it in place of the video that key belongs to. A null <paramref name="destinationKey"/> writes the plain
    /// Bink video (for previews).
    /// </summary>
    public static void Transcode(string source, byte[] sourceKey, string destination, byte[]? destinationKey,
        CancellationToken cancellationToken = default)
    {
        var both = new byte[KeyLength];
        for (var index = 0; index < KeyLength; index++)
            both[index] = (byte)(sourceKey[index] ^ (destinationKey?[index] ?? 0));
        using var input = File.OpenRead(source);
        using var output = File.Create(destination);
        var buffer = new byte[KeyLength * 21_845];
        long offset = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Apply(buffer.AsSpan(0, read), both, offset);
            output.Write(buffer, 0, read);
            offset += read;
        }
        output.Flush(flushToDisk: true);
    }
}
