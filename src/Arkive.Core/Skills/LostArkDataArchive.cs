using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Arkive.Core.Skills;

/// <summary>One file stored in a Lost Ark data archive (.lpk).</summary>
public sealed record LostArkDataEntry(string Path, long Offset, int Size, int EncryptedSize, int CompressedSize);

/// <summary>
/// Reads the client's data archives (EFGame\data*.lpk). The index and compressed entries are Blowfish encrypted with
/// the archive key, stored as little-endian words; compressed entries are zlib streams once decrypted. The game
/// databases in data2.lpk use a different, per-file scheme (see Databases.LostArkDatabaseArchive).
/// </summary>
public static class LostArkDataArchive
{
    private const int EntrySize = 528;
    private const int MaximumEntries = 200_000;
    private const int MaximumEntrySize = 256 * 1024 * 1024;
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("83657ea6ffa1e671375c689a2e99a598");

    public static IReadOnlyList<LostArkDataEntry> ReadEntries(Stream stream, CancellationToken cancellationToken = default)
    {
        Span<byte> header = stackalloc byte[4];
        stream.Position = 0;
        stream.ReadExactly(header);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count is < 1 or > MaximumEntries || (long)count * EntrySize + 8 > stream.Length)
            throw new InvalidDataException("Unsupported or truncated Lost Ark data archive index.");
        var table = new byte[count * EntrySize];
        stream.ReadExactly(table);
        Decrypt(table);
        var entries = new List<LostArkDataEntry>(count);
        long offset = table.Length + 8;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = table.AsSpan(index * EntrySize, EntrySize);
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(record);
            if (nameLength is < 1 or > EntrySize - 20)
                throw new InvalidDataException("Invalid data archive entry; the archive format may have changed.");
            var name = Encoding.UTF8.GetString(record.Slice(4, nameLength)).TrimEnd('\0').Replace('\\', '/');
            var size = BinaryPrimitives.ReadInt32LittleEndian(record[^12..]);
            var encrypted = BinaryPrimitives.ReadInt32LittleEndian(record[^8..]);
            var compressed = BinaryPrimitives.ReadInt32LittleEndian(record[^4..]);
            if (size < 0 || encrypted < 0 || compressed < 0 || offset > stream.Length - encrypted)
                throw new InvalidDataException("A data archive entry exceeds the archive.");
            entries.Add(new LostArkDataEntry(name, offset, size, encrypted, compressed));
            offset += encrypted;
        }
        return entries;
    }

    /// <summary>The entry's contents. Only compressed entries are supported: the others use per-file keys.</summary>
    public static byte[] Read(Stream stream, LostArkDataEntry entry)
    {
        if (entry.CompressedSize <= 0 || entry.CompressedSize > entry.EncryptedSize || entry.Size > MaximumEntrySize)
            throw new InvalidDataException($"{entry.Path} is not a compressed data archive entry.");
        var data = new byte[entry.EncryptedSize];
        stream.Position = entry.Offset;
        stream.ReadExactly(data);
        Decrypt(data);
        var result = new byte[entry.Size];
        using var zlib = new ZLibStream(new MemoryStream(data, 0, entry.CompressedSize), CompressionMode.Decompress);
        zlib.ReadExactly(result);
        return result;
    }

    private static void Decrypt(byte[] data)
    {
        var cipher = new BlowfishEngine();
        cipher.Init(false, new KeyParameter(Key));
        for (var offset = 0; offset + 8 <= data.Length; offset += 8)
        {
            var block = data.AsSpan(offset, 8);
            block[..4].Reverse();
            block[4..].Reverse();
            cipher.ProcessBlock(data, offset, data, offset);
            block[..4].Reverse();
            block[4..].Reverse();
        }
    }
}
