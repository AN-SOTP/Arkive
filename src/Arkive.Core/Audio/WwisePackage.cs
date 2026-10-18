using System.Buffers.Binary;
using System.Text;

namespace Arkive.Core.Audio;

public enum WwisePackageSection
{
    SoundBank,
    StreamedFile,
    ExternalFile,
}

/// <summary>
/// One file inside a Wwise .pck package, as its table lists it. Offsets are content offsets (see
/// <see cref="WwiseAudioFile"/>); <see cref="EntryOffset"/> is where the table entry itself sits, for patching.
/// </summary>
public sealed record WwisePackageEntry(
    WwisePackageSection Section,
    ulong Id,
    uint BlockSize,
    uint Size,
    uint StartBlock,
    uint LanguageId,
    long EntryOffset,
    bool WideId)
{
    public long DataOffset => (long)StartBlock * BlockSize;

    public int EntryLength => WideId ? 24 : 20;

    /// <summary>The table entry's bytes with a new size and start, everything else kept.</summary>
    public byte[] EncodeEntry(uint size, uint startBlock)
    {
        var bytes = new byte[EntryLength];
        var at = 0;
        if (WideId)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, Id);
            at = 8;
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)Id);
            at = 4;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 4), size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 8), startBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 12), LanguageId);
        return bytes;
    }

    public static WwisePackageEntry Decode(
        WwisePackageSection section, ReadOnlySpan<byte> bytes, long entryOffset, bool wideId)
    {
        var at = wideId ? 8 : 4;
        return new WwisePackageEntry(
            section,
            wideId ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 8)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 12)..]),
            entryOffset,
            wideId);
    }
}

/// <summary>A Wwise file package (AKPK): its languages and the table of banks and audio files it holds.</summary>
public sealed record WwisePackage(
    string Path,
    bool Scrambled,
    long ContentLength,
    IReadOnlyDictionary<uint, string> Languages,
    IReadOnlyList<WwisePackageEntry> Entries)
{
    private const int FixedHeaderLength = 28;

    public static WwisePackage Read(string path)
    {
        using var file = WwiseAudioFile.Open(path);
        return Read(path, file);
    }

    public static WwisePackage Read(string path, WwiseAudioFile file)
    {
        if (file.ContentLength < FixedHeaderLength)
            throw new InvalidDataException($"{path} is too short to be a Wwise package.");
        var fixedHeader = file.Read(0, FixedHeaderLength);
        if (!fixedHeader.AsSpan(0, 4).SequenceEqual("AKPK"u8))
            throw new InvalidDataException($"{path} is not a Wwise package (AKPK).");
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.AsSpan(4));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.AsSpan(8));
        if (version != 1)
            throw new InvalidDataException($"{path} is Wwise package version {version}; Arkive reads version 1.");
        if (headerSize + 8L > file.ContentLength || headerSize > 64 * 1024 * 1024)
            throw new InvalidDataException($"{path} has a damaged package header.");
        var header = file.Read(0, checked((int)headerSize + 8));
        var languageMapSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        var sizes = new[]
        {
            (WwisePackageSection.SoundBank, (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16))),
            (WwisePackageSection.StreamedFile, (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20))),
            (WwisePackageSection.ExternalFile, (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24))),
        };
        var at = FixedHeaderLength;
        var languages = ReadLanguages(header.AsSpan(at, languageMapSize));
        at += languageMapSize;
        var entries = new List<WwisePackageEntry>();
        foreach (var (section, size) in sizes)
        {
            if (size >= 4 && at + size <= header.Length)
            {
                var table = header.AsSpan(at, size);
                var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
                // Externals use 64-bit ids; a table whose size fits neither layout is skipped rather than misread.
                var entryLength = count > 0 && size - 4 == count * 24L ? 24 : size - 4 == count * 20L ? 20 : 0;
                for (var index = 0; entryLength > 0 && index < count; index++)
                {
                    var offset = 4 + index * entryLength;
                    entries.Add(WwisePackageEntry.Decode(
                        section, table.Slice(offset, entryLength), at + offset, entryLength == 24));
                }
            }
            at += size;
        }
        return new WwisePackage(path, file.Scrambled, file.ContentLength, languages, entries);
    }

    private static Dictionary<uint, string> ReadLanguages(ReadOnlySpan<byte> map)
    {
        var languages = new Dictionary<uint, string>();
        if (map.Length < 4) return languages;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(map);
        for (var index = 0; index < count && 4 + index * 8 + 8 <= map.Length; index++)
        {
            var nameOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(map[(4 + index * 8)..]);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(map[(8 + index * 8)..]);
            var name = new StringBuilder();
            for (var at = nameOffset; at + 1 < map.Length; at += 2)
            {
                var character = (char)BinaryPrimitives.ReadUInt16LittleEndian(map[at..]);
                if (character == '\0') break;
                name.Append(character);
            }
            languages[id] = name.ToString();
        }
        return languages;
    }
}
