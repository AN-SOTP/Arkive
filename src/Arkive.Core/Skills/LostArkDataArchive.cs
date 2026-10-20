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
    /// Writes a copy of the archive at <paramref name="sourcePath"/> to <paramref name="outputPath"/> with the entries in
    /// <paramref name="changes"/> (by path, such as /Binaries/Fonts/FontMap.xml) replaced, or added in name order when the
    /// archive has none by that path. Changed entries are compressed and encrypted the way the game stores them, and every
    /// other entry is copied byte for byte.
    /// </summary>
    public static void Rewrite(string sourcePath, IReadOnlyDictionary<string, byte[]> changes, string outputPath,
        CancellationToken cancellationToken = default)
    {
        using var source = File.OpenRead(sourcePath);
        var entries = ReadEntries(source, cancellationToken);
        source.Position = 4;
        var table = new byte[entries.Count * EntrySize];
        source.ReadExactly(table);
        Decrypt(table);

        var pending = changes.ToDictionary(change => Normalise(change.Key), change => change.Value, StringComparer.OrdinalIgnoreCase);
        var items = new List<(byte[] Record, LostArkDataEntry? Copy, byte[]? Contents)>();
        for (var index = 0; index < entries.Count; index++)
        {
            var record = table.AsSpan(index * EntrySize, EntrySize).ToArray();
            if (pending.Remove(entries[index].Path, out var contents)) items.Add((record, null, contents));
            else items.Add((record, entries[index], null));
        }
        // Every entry added so far sorts before the first existing entry greater than the next one.
        foreach (var (path, contents) in pending.OrderBy(change => change.Key, StringComparer.OrdinalIgnoreCase))
        {
            var at = entries.ToList().FindIndex(entry => StringComparer.OrdinalIgnoreCase.Compare(entry.Path, path) > 0);
            items.Insert(at < 0 ? items.Count : at + items.Count - entries.Count, (NewRecord(path), null, contents));
        }
        Write(source, items, outputPath, cancellationToken);
    }

    /// <summary>Writes a new archive holding <paramref name="files"/> (path and contents), in the order given.</summary>
    public static void Create(IEnumerable<KeyValuePair<string, byte[]>> files, string outputPath, CancellationToken cancellationToken = default) =>
        Write(Stream.Null, files.Select(file => (NewRecord(Normalise(file.Key)), (LostArkDataEntry?)null, (byte[]?)file.Value)).ToList(),
            outputPath, cancellationToken);

    private static string Normalise(string path) => "/" + path.Replace('\\', '/').TrimStart('/');

    // The game stores names with backslashes and no terminator; the rest of the record before the sizes is zero.
    private static byte[] NewRecord(string path)
    {
        var name = Encoding.UTF8.GetBytes(path.Replace('/', '\\'));
        if (name.Length > EntrySize - 20) throw new ArgumentException($"{path} is too long for a data archive entry.");
        var record = new byte[EntrySize];
        BinaryPrimitives.WriteInt32LittleEndian(record, name.Length);
        name.CopyTo(record, 4);
        return record;
    }

    private static void Write(Stream source, List<(byte[] Record, LostArkDataEntry? Copy, byte[]? Contents)> items, string outputPath,
        CancellationToken cancellationToken)
    {
        if (items.Count is < 1 or > MaximumEntries) throw new ArgumentException("A data archive holds 1 to 200,000 entries.");
        var payloads = new byte[items.Count][];
        var table = new byte[items.Count * EntrySize];
        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (record, copy, contents) = items[index];
            if (contents is not null)
            {
                using var packed = new MemoryStream();
                using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(contents);
                var compressed = (int)packed.Length;
                // The game always pads to the next whole 8-byte block, a full block when the data already ends on one.
                var encrypted = new byte[(compressed / 8 + 1) * 8];
                packed.ToArray().CopyTo(encrypted, 0);
                Crypt(encrypted, encrypt: true);
                payloads[index] = encrypted;
                BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(EntrySize - 12), contents.Length);
                BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(EntrySize - 8), encrypted.Length);
                BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(EntrySize - 4), compressed);
            }
            record.CopyTo(table, index * EntrySize);
        }
        Crypt(table, encrypt: true);

        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        var count = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, items.Count);
        output.Write(count);
        output.Write(table);
        output.Write(new byte[4]);
        for (var index = 0; index < items.Count; index++)
            if (items[index].Copy is { } copy) CopyRange(source, output, copy.Offset, copy.EncryptedSize, cancellationToken);
            else output.Write(payloads[index]);
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
