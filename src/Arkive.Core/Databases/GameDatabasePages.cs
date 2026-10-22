using System.Buffers.Binary;

namespace Arkive.Core.Databases;

/// <summary>
/// The bytes the game's databases end every page with: SQLite's reserved space, filled the way a padded AES block
/// is (16 bytes of 0x10). The game rejects a page that ends otherwise, and SQLite leaves new and rebuilt pages'
/// reserved space empty, so it is filled again after every change (<see cref="Mark"/>).
/// </summary>
internal static class GameDatabasePages
{
    /// <summary>The marker <paramref name="database"/> ends its pages with (empty when it reserves no space).</summary>
    internal static byte[] Marker(string database)
    {
        using var stream = File.OpenRead(database);
        var header = new byte[100];
        stream.ReadExactly(header);
        var pageSize = PageSize(header);
        int reserved = header[20];
        if (reserved == 0) return [];
        var marker = new byte[reserved];
        stream.Position = pageSize - reserved;
        stream.ReadExactly(marker);
        if (marker.Any(value => value != reserved))
            throw new InvalidDataException($"{Path.GetFileName(database)} is stored in a way Arkive does not know, so it was not changed.");
        return marker;
    }

    /// <summary>Ends every page of <paramref name="database"/> with <paramref name="marker"/> again.</summary>
    internal static void Mark(string database, byte[] marker)
    {
        if (marker.Length == 0) return;
        using var stream = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var header = new byte[100];
        stream.ReadExactly(header);
        var pageSize = PageSize(header);
        if (header[20] != marker.Length || stream.Length % pageSize != 0)
            throw new InvalidDataException($"The changed {Path.GetFileName(database)} has an unexpected layout, so it was not written.");
        for (long end = pageSize; end <= stream.Length; end += pageSize)
        {
            stream.Position = end - marker.Length;
            stream.Write(marker);
        }
    }

    private static int PageSize(byte[] header) =>
        BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16)) is var size && size == 1 ? 65536 : size;
}
