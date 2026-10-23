using System.Collections.Concurrent;

namespace Arkive.Core.Mods;

/// <summary>
/// Working-copy records read on every change in the Mods list (thousands with large mods), kept parsed while each
/// file's size and write time stay the same. Every save replaces the file, which changes its write time.
/// </summary>
internal sealed class ParsedRecordCache<T> where T : class
{
    // Write times only move on with the file system's clock tick (up to 2 s on FAT drives), so a save landing in the
    // same tick as the one read could keep both the size and the time. A record read that soon after it was written
    // is read again next time.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, (long Length, DateTime WriteTimeUtc, bool Settled, T Value)> _parsed =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<T> GetAsync(string path, Func<string, CancellationToken, Task<T>> parse, CancellationToken cancellationToken)
    {
        // The file is checked before it is read: a save landing in between is read now and parsed again next time.
        var now = DateTime.UtcNow;
        var info = new FileInfo(path);
        if (_parsed.TryGetValue(path, out var parsed) && parsed.Settled && info.Exists &&
            parsed.Length == info.Length && parsed.WriteTimeUtc == info.LastWriteTimeUtc)
            return parsed.Value;
        var value = await parse(path, cancellationToken).ConfigureAwait(false);
        _parsed[path] = (info.Length, info.LastWriteTimeUtc, now - info.LastWriteTimeUtc > SettleTime, value);
        return value;
    }
}
