namespace Arkive.Core.Mods;

/// <summary>
/// Reads a store's working-copy records several at a time. Read one after another, about a thousand small records took
/// 5 to 6 s at launch when none was cached yet; the mesh stores already read theirs this way.
/// </summary>
internal static class WorkingRecordReader
{
    /// <summary>
    /// Reads every path with <paramref name="read"/> and returns the records in the order of <paramref name="paths"/>,
    /// so callers sort exactly as they did when reading in turn. A record whose read throws an exception that
    /// <paramref name="skip"/> accepts is left out (one damaged record must not hide the others); any other exception
    /// ends the load, as it did before.
    /// </summary>
    public static async Task<List<T>> ReadAllAsync<T>(
        IReadOnlyList<string> paths,
        Func<string, CancellationToken, Task<T>> read,
        Func<Exception, bool> skip,
        CancellationToken cancellationToken)
        where T : class
    {
        var records = new T?[paths.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                try
                {
                    records[index] = await read(paths[index], token).ConfigureAwait(false);
                }
                catch (Exception exception) when (skip(exception))
                {
                }
            }).ConfigureAwait(false);
        return records.OfType<T>().ToList();
    }
}
