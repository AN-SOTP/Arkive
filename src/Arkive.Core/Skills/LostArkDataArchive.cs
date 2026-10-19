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

    /// <summary>
    /// Writes a copy of the archive at <paramref name="sourcePath"/> to <paramref name="outputPath"/> with one compressed
    /// entry's contents replaced: the entry is compressed and encrypted the way the game stores it, its sizes updated in
    /// the index, and every other entry copied byte for byte.
    /// </summary>
    public static void ReplaceEntry(string sourcePath, string entryPath, byte[] contents, string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        using var source = File.OpenRead(sourcePath);
        var entries = ReadEntries(source, cancellationToken);
        var normalised = entryPath.Replace(Path.DirectorySeparatorChar, '/');
        var index = entries.ToList().FindIndex(entry => entry.Path.Equals(normalised, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidDataException($"{entryPath} is not in {Path.GetFileName(sourcePath)}.");
        var target = entries[index];
        if (target.CompressedSize <= 0) throw new InvalidDataException($"{entryPath} is not a compressed data archive entry.");

        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(contents);
        var compressed = (int)packed.Length;
        // The game always pads to the next whole 8-byte block, a full block when the data already ends on one.
        var encrypted = new byte[(compressed / 8 + 1) * 8];
        packed.ToArray().CopyTo(encrypted, 0);
        Crypt(encrypted, encrypt: true);

        source.Position = 0;
        var header = new byte[4 + entries.Count * EntrySize + 4];
        source.ReadExactly(header);
        var table = header.AsSpan(4, entries.Count * EntrySize).ToArray();
        Decrypt(table);
        var record = table.AsSpan(index * EntrySize, EntrySize);
        BinaryPrimitives.WriteInt32LittleEndian(record[^12..], contents.Length);
        BinaryPrimitives.WriteInt32LittleEndian(record[^8..], encrypted.Length);
        BinaryPrimitives.WriteInt32LittleEndian(record[^4..], compressed);
        Crypt(table, encrypt: true);
        table.CopyTo(header, 4);

        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        output.Write(header);
        CopyRange(source, output, header.Length, target.Offset - header.Length, cancellationToken);
        output.Write(encrypted);
        var after = target.Offset + target.EncryptedSize;
        CopyRange(source, output, after, source.Length - after, cancellationToken);
    }

    private static void CopyRange(Stream source, Stream output, long offset, long length, CancellationToken cancellationToken)
    {
        source.Position = offset;
        var buffer = new byte[1 << 20];
        while (length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (read <= 0) throw new EndOfStreamException("The data archive ended early.");
            output.Write(buffer, 0, read);
            length -= read;
        }
    }

    private static void Decrypt(byte[] data) => Crypt(data, encrypt: false);

    private static void Crypt(byte[] data, bool encrypt)
    {
        var cipher = new BlowfishEngine();
        cipher.Init(encrypt, new KeyParameter(Key));
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
