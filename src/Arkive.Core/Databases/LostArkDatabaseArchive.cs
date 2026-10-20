using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Arkive.Core.Skills;

namespace Arkive.Core.Databases;

internal sealed record DatabaseEntry(string Name, long Offset, int Size, int EncryptedSize);

internal static class LostArkDatabaseArchive
{
    private const int EntrySize = 528;
    private const int MaximumEntries = 100_000;
    private const int MaximumDatabaseSize = 512 * 1024 * 1024;
    private static readonly byte[] IndexKey = Encoding.ASCII.GetBytes("83657ea6ffa1e671375c689a2e99a598");
    private static readonly byte[] DatabaseBase = Convert.FromHexString("1069D88738C5C75F82B44A1F0A382768");

    internal static IReadOnlyList<DatabaseEntry> ReadEntries(Stream stream, CancellationToken cancellation)
    {
        Span<byte> header = stackalloc byte[4];
        stream.ReadExactly(header);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count is < 1 or > MaximumEntries || (long)count * EntrySize + 8 > stream.Length)
            throw new InvalidDataException("Unsupported or truncated Western Lost Ark LPK index.");
        var table = new byte[count * EntrySize];
        stream.ReadExactly(table);
        var cipher = new BlowfishEngine();
        cipher.Init(false, new KeyParameter(IndexKey));
        for (var offset = 0; offset < table.Length; offset += cipher.GetBlockSize())
        {
            if ((offset & 0xFFFF) == 0) cancellation.ThrowIfCancellationRequested();
            // The LPK cipher stores each Blowfish word little-endian.
            ReverseWords(table.AsSpan(offset, 8));
            cipher.ProcessBlock(table, offset, table, offset);
            ReverseWords(table.AsSpan(offset, 8));
        }

        var entries = new List<DatabaseEntry>();
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long payloadOffset = table.Length + 8;
        long totalSelected = 0;
        for (var index = 0; index < count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var record = table.AsSpan(index * EntrySize, EntrySize);
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(record);
            if (nameLength is < 1 or > EntrySize - 20)
                throw new InvalidDataException("Invalid LPK entry name length; the protection profile may have changed.");
            var name = Encoding.UTF8.GetString(record.Slice(4, nameLength)).TrimEnd('\0');
            var size = BinaryPrimitives.ReadInt32LittleEndian(record[^12..]);
            var encryptedSize = BinaryPrimitives.ReadInt32LittleEndian(record[^8..]);
            var compressedSize = BinaryPrimitives.ReadInt32LittleEndian(record[^4..]);
            if (size < 0 || encryptedSize < 0 || compressedSize < 0 ||
                payloadOffset > stream.Length - encryptedSize)
                throw new InvalidDataException("An LPK entry exceeds its archive bounds.");
            var normalizedName = name.Replace('\\', '/').TrimStart('/');
            var fileName = normalizedName.Split('/')[^1];
            if (GameDatabaseMetadataReader.RequiredFiles.Contains(fileName) && normalizedName.Equals(
                    "EFGame_Extra/ClientData/TableData/" + fileName, StringComparison.OrdinalIgnoreCase))
            {
                if (compressedSize != 0 || size < 16 || size > MaximumDatabaseSize ||
                    encryptedSize < size || encryptedSize - size > 15 || encryptedSize % 16 != 0)
                    throw new InvalidDataException($"Unsupported database payload layout: {fileName}.");
                if (!selected.Add(fileName))
                    throw new InvalidDataException($"The LPK contains duplicate database identities: {fileName}.");
                totalSelected += encryptedSize;
                if (totalSelected > 1024L * 1024 * 1024)
                    throw new InvalidDataException("The selected database payloads exceed the supported size limit.");
                entries.Add(new DatabaseEntry(fileName, payloadOffset, size, encryptedSize));
            }
            payloadOffset += encryptedSize;
        }
        if (!selected.Contains("EFTable_GameMsg.db"))
            throw new InvalidDataException("The archive has no supported game-message database. Select EFGame\\data2.lpk from a Western/global Steam client.");
        return entries;
    }

    private static void ReverseWords(Span<byte> block)
    {
        block[..4].Reverse();
        block[4..].Reverse();
    }

    internal static void Extract(Stream archive, DatabaseEntry entry, string output, CancellationToken cancellation)
    {
        archive.Position = entry.Offset;
        using var aes = Aes.Create();
        aes.Key = DeriveKey(entry.Name);
        aes.IV = new byte[16];
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var encrypted = new byte[128 * 1024];
        var plaintext = new byte[encrypted.Length];
        var previous = new byte[16];
        var consumed = 0;
        while (consumed < entry.EncryptedSize)
        {
            cancellation.ThrowIfCancellationRequested();
            var length = Math.Min(encrypted.Length, entry.EncryptedSize - consumed);
            archive.ReadExactly(encrypted.AsSpan(0, length));
            if (decryptor.TransformBlock(encrypted, 0, length, plaintext, 0) != length)
                throw new InvalidDataException("The database decoder returned an incomplete block.");
            // Lost Ark resets the CBC IV every 0x400 bytes; undo continuous CBC's boundary XOR.
            for (var reset = 0; reset < length; reset += 0x400)
            {
                if (consumed + reset == 0) continue;
                var preceding = reset == 0 ? previous.AsSpan() : encrypted.AsSpan(reset - 16, 16);
                for (var index = 0; index < 16; index++) plaintext[reset + index] ^= preceding[index];
            }
            if (consumed == 0 && !plaintext.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
                throw new InvalidDataException($"Decryption failed for {entry.Name}; the database protection profile may have changed.");
            destination.Write(plaintext, 0, Math.Min(length, Math.Max(0, entry.Size - consumed)));
            encrypted.AsSpan(length - 16, 16).CopyTo(previous);
            consumed += length;
        }
        decryptor.TransformFinalBlock([], 0, 0);
    }

    /// <summary>Decrypts the database stored at <paramref name="entryPath"/> (EFGame_Extra/ClientData/TableData/...) to <paramref name="output"/>.</summary>
    internal static void Extract(string archivePath, string entryPath, string output, CancellationToken cancellation)
    {
        using var archive = File.OpenRead(archivePath);
        var entry = Find(archive, entryPath, cancellation);
        if (entry.CompressedSize != 0 || entry.EncryptedSize % 16 != 0 || entry.EncryptedSize < entry.Size)
            throw new InvalidDataException($"{entryPath} is not a database entry.");
        Extract(archive, new DatabaseEntry(entry.Path.Split('/')[^1], entry.Offset, entry.Size, entry.EncryptedSize), output, cancellation);
    }

    /// <summary>
    /// Writes a copy of <paramref name="archivePath"/> to <paramref name="outputPath"/> with the database at
    /// <paramref name="entryPath"/> replaced by <paramref name="databasePath"/>, encrypted the way the game stores it.
    /// </summary>
    internal static void Replace(string archivePath, string entryPath, string databasePath, string outputPath, CancellationToken cancellation)
    {
        using var source = File.OpenRead(archivePath);
        var target = Find(source, entryPath, cancellation);
        if (target.CompressedSize != 0) throw new InvalidDataException($"{entryPath} is not a database entry.");
        var size = new FileInfo(databasePath).Length;
        if (size < 16 || size > MaximumDatabaseSize) throw new InvalidDataException($"{Path.GetFileName(databasePath)} is not a usable database.");

        // The index with the entry's sizes changed; every other entry is copied byte for byte.
        source.Position = 0;
        var header = new byte[8];
        source.ReadExactly(header.AsSpan(0, 4));
        var count = BinaryPrimitives.ReadInt32LittleEndian(header);
        var table = new byte[count * EntrySize];
        source.ReadExactly(table);
        CryptIndex(table, encrypt: false);
        var index = Enumerable.Range(0, count).First(item =>
        {
            var record = table.AsSpan(item * EntrySize, EntrySize);
            var name = Encoding.UTF8.GetString(record.Slice(4, BinaryPrimitives.ReadInt32LittleEndian(record))).TrimEnd('\0');
            return name.Replace('\\', '/').Equals(target.Path, StringComparison.OrdinalIgnoreCase);
        });
        var entry = table.AsSpan(index * EntrySize, EntrySize);
        BinaryPrimitives.WriteInt32LittleEndian(entry[^12..], (int)size);
        BinaryPrimitives.WriteInt32LittleEndian(entry[^8..], (int)((size + 15) / 16 * 16));
        CryptIndex(table, encrypt: true);

        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        output.Write(header.AsSpan(0, 4));
        output.Write(table);
        output.Write(header.AsSpan(4, 4));
        CopyRange(source, output, 8 + table.Length, target.Offset - 8 - table.Length, cancellation);
        Encrypt(target.Path.Split('/')[^1], databasePath, output, cancellation);
        var after = target.Offset + target.EncryptedSize;
        CopyRange(source, output, after, source.Length - after, cancellation);
    }

    // The LPK cipher: Blowfish with each word stored little-endian.
    private static void CryptIndex(byte[] table, bool encrypt)
    {
        var cipher = new BlowfishEngine();
        cipher.Init(encrypt, new KeyParameter(IndexKey));
        for (var offset = 0; offset < table.Length; offset += 8)
        {
            ReverseWords(table.AsSpan(offset, 8));
            cipher.ProcessBlock(table, offset, table, offset);
            ReverseWords(table.AsSpan(offset, 8));
        }
    }

    private static void CopyRange(Stream source, Stream output, long offset, long length, CancellationToken cancellation)
    {
        source.Position = offset;
        var buffer = new byte[1 << 20];
        while (length > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (read <= 0) throw new EndOfStreamException("The data archive ended early.");
            output.Write(buffer, 0, read);
            length -= read;
        }
    }

    private static LostArkDataEntry Find(Stream archive, string entryPath, CancellationToken cancellation)
    {
        var normalised = "/" + entryPath.Replace('\\', '/').TrimStart('/');
        return LostArkDataArchive.ReadEntries(archive, cancellation)
                   .FirstOrDefault(entry => entry.Path.Equals(normalised, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidDataException($"{entryPath} is not in the game's data archive.");
    }

    // The game's cipher: AES-CBC from a zero IV, restarted every 0x400 bytes; the last block is zero padded.
    private static void Encrypt(string name, string databasePath, Stream output, CancellationToken cancellation)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey(name);
        var iv = new byte[16];
        var chunk = new byte[0x400];
        var encrypted = new byte[0x400];
        using var source = File.OpenRead(databasePath);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = source.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false);
            if (read == 0) break;
            var length = (read + 15) / 16 * 16;
            chunk.AsSpan(read, length - read).Clear();
            aes.EncryptCbc(chunk.AsSpan(0, length), iv, encrypted, PaddingMode.None);
            output.Write(encrypted, 0, length);
            if (read < chunk.Length) break;
        }
    }

    private static byte[] DeriveKey(string filename)
    {
        var keyName = filename[(filename.LastIndexOf('_') + 1)..].Split('.')[0];
        var digest = MD5.HashData(Encoding.Unicode.GetBytes(keyName));
        var mixed = new byte[16];
        for (var index = 0; index < mixed.Length; index++) mixed[index] = (byte)(DatabaseBase[index] ^ digest[15 - index]);
        return SHA256.HashData(Encoding.ASCII.GetBytes(Convert.ToHexString(mixed).ToLowerInvariant()));
    }
}
