using System.Collections.Concurrent;
using Arkive.Core.Assets;

namespace Arkive.Core.Discovery;

/// <summary>
/// A small Bloom filter per index document over the character trigrams of every name a
/// search can match in that document (object, class, package and file names). Object-name
/// and all-field searches use it to skip packages that cannot contain every query term,
/// instead of decoding all of the library's index documents. A false positive only costs
/// one extra document read; a package without a current entry is always read.
/// </summary>
internal sealed class PackageNameFilter
{
    private const int FileVersion = 1;
    private const int BitsPerTrigram = 12;
    private const int HashCount = 4;
    private const uint Magic = 0x464E4B41; // "AKNF"

    private static readonly string[] KindNames = Enum.GetNames<AssetKind>();

    private readonly ConcurrentDictionary<string, Entry> _entries;
    private int _dirty;

    private PackageNameFilter(ConcurrentDictionary<string, Entry> entries) => _entries = entries;

    /// <summary>Bits is null for a document with nothing searchable (an index error).</summary>
    internal sealed record Entry(long Length, long LastWriteUtcTicks, ulong[]? Bits);

    public bool IsDirty => Volatile.Read(ref _dirty) != 0;

    public int Count => _entries.Count;

    /// <summary>
    /// Returns the trigram sets of the query terms that every matching asset's searchable
    /// names must contain, or null when no term can narrow the search.
    /// </summary>
    public static IReadOnlyList<uint[]>? PlanTerms(string query, PackageIndexSearchScope scope)
    {
        if (scope is not (PackageIndexSearchScope.ObjectName or
            PackageIndexSearchScope.ObjectAndInGameName or
            PackageIndexSearchScope.AllFields) || string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var terms = new List<uint[]>();
        foreach (var term in query.Split(
                     (char[]?)null,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Excluded terms only remove matches, and a term shorter than a trigram can't be filtered.
            if (term.Length < 3 || term[0] == '-')
            {
                continue;
            }

            // All-field searches also match the asset kind and export index, which the filter doesn't hold.
            if (scope == PackageIndexSearchScope.AllFields &&
                (term.All(char.IsAsciiDigit) ||
                 KindNames.Any(kind => kind.Contains(term, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            terms.Add(Trigrams(term).ToArray());
        }

        return terms.Count == 0 ? null : terms;
    }

    public static PackageNameFilter Load(string path)
    {
        var entries = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return new PackageNameFilter(entries);
        }

        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FileVersion)
            {
                return new PackageNameFilter(entries);
            }

            var count = reader.ReadInt32();
            for (var index = 0; index < count; index++)
            {
                var name = reader.ReadString();
                var length = reader.ReadInt64();
                var ticks = reader.ReadInt64();
                var words = reader.ReadInt32();
                ulong[]? bits = null;
                if (words >= 0)
                {
                    bits = new ulong[words];
                    for (var word = 0; word < words; word++)
                    {
                        bits[word] = reader.ReadUInt64();
                    }
                }

                entries[name] = new Entry(length, ticks, bits);
            }
        }
        catch (Exception exception) when (
            exception is IOException or EndOfStreamException or UnauthorizedAccessException)
        {
            entries.Clear();
        }

        return new PackageNameFilter(entries);
    }

    /// <summary>Writes the entries for the cache files that still exist.</summary>
    public void Save(string path, IEnumerable<string> liveFileNames)
    {
        var live = liveFileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = _entries.Where(pair => live.Contains(pair.Key)).ToArray();
        Interlocked.Exchange(ref _dirty, 0);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var writer = new BinaryWriter(new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024)))
            {
                writer.Write(Magic);
                writer.Write(FileVersion);
                writer.Write(snapshot.Length);
                foreach (var (name, entry) in snapshot)
                {
                    writer.Write(name);
                    writer.Write(entry.Length);
                    writer.Write(entry.LastWriteUtcTicks);
                    writer.Write(entry.Bits?.Length ?? -1);
                    foreach (var word in entry.Bits ?? [])
                    {
                        writer.Write(word);
                    }
                }
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public bool HasCurrentEntry(FileInfo file) =>
        _entries.TryGetValue(file.Name, out var entry) && IsCurrent(entry, file);

    /// <summary>False only when the file's current entry proves no asset can match every term.</summary>
    public bool MayContain(FileInfo file, IReadOnlyList<uint[]> terms)
    {
        if (!_entries.TryGetValue(file.Name, out var entry) || !IsCurrent(entry, file))
        {
            return true;
        }

        if (entry.Bits is not { } bits)
        {
            return false;
        }

        foreach (var term in terms)
        foreach (var trigram in term)
        {
            if (!Test(bits, trigram))
            {
                return false;
            }
        }

        return true;
    }

    public void Add(FileInfo file, PackageIndexDocument document)
    {
        _entries[file.Name] = new Entry(file.Length, file.LastWriteTimeUtc.Ticks, Build(document));
        Interlocked.Exchange(ref _dirty, 1);
    }

    private static bool IsCurrent(Entry entry, FileInfo file) =>
        entry.Length == file.Length && entry.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks;

    private static ulong[]? Build(PackageIndexDocument document)
    {
        if (document.Error is not null)
        {
            return null;
        }

        var trigrams = new HashSet<uint>();
        void AddField(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                trigrams.UnionWith(Trigrams(value));
            }
        }

        AddField(document.PackageAlias);
        AddField(document.PhysicalPackageName);
        if (!string.IsNullOrEmpty(document.SourcePath))
        {
            AddField(Path.GetFileName(document.SourcePath));
            AddField(document.PackageAlias + Path.GetExtension(document.SourcePath));
        }

        foreach (var asset in document.Assets)
        {
            AddField(asset.ObjectName);
            AddField(asset.ClassName);
        }

        var words = Math.Max(1, (trigrams.Count * BitsPerTrigram + 63) / 64);
        var bits = new ulong[words];
        foreach (var trigram in trigrams)
        {
            Set(bits, trigram);
        }

        return bits;
    }

    // Characters are folded with the invariant upper-case mapping that OrdinalIgnoreCase uses,
    // so a term that Contains() would find always shares its trigrams with the field.
    private static IEnumerable<uint> Trigrams(string value)
    {
        for (var index = 0; index + 2 < value.Length; index++)
        {
            yield return (Fold(value[index]) << 16) | (Fold(value[index + 1]) << 8) | Fold(value[index + 2]);
        }
    }

    private static uint Fold(char value)
    {
        var upper = char.ToUpperInvariant(value);
        return (uint)(upper ^ (upper >> 8)) & 0xFF;
    }

    private static void Set(ulong[] bits, uint trigram)
    {
        var size = (ulong)bits.Length * 64;
        var (first, step) = Hashes(trigram);
        for (var index = 0UL; index < HashCount; index++)
        {
            var bit = (first + index * step) % size;
            bits[bit >> 6] |= 1UL << (int)(bit & 63);
        }
    }

    private static bool Test(ulong[] bits, uint trigram)
    {
        var size = (ulong)bits.Length * 64;
        var (first, step) = Hashes(trigram);
        for (var index = 0UL; index < HashCount; index++)
        {
            var bit = (first + index * step) % size;
            if ((bits[bit >> 6] & (1UL << (int)(bit & 63))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static (ulong First, ulong Step) Hashes(uint trigram)
    {
        var mixed = trigram * 0x9E3779B97F4A7C15UL;
        return (mixed >> 32, ((mixed & 0xFFFFFFFF) | 1));
    }
}

public sealed partial class PackageIndexStore
{
    private const string NameFilterFileName = "name-filter-v1.bin";

    private readonly SemaphoreSlim _nameFilterGate = new(1, 1);
    private PackageNameFilter? _nameFilter;

    private string NameFilterPath => Path.Combine(_cacheDirectory, NameFilterFileName);

    private async Task<PackageNameFilter> LoadNameFilterAsync(CancellationToken cancellationToken)
    {
        if (_nameFilter is { } loaded)
        {
            return loaded;
        }

        await _nameFilterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _nameFilter ??= await Task.Run(
                () => PackageNameFilter.Load(NameFilterPath), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _nameFilterGate.Release();
        }
    }

    private async Task SaveNameFilterAsync(
        PackageNameFilter filter,
        IEnumerable<string> liveFileNames,
        CancellationToken cancellationToken)
    {
        await _nameFilterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (filter.IsDirty)
            {
                await Task.Run(() => filter.Save(NameFilterPath, liveFileNames), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The filter only speeds searches up; the next search rebuilds missing entries.
        }
        finally
        {
            _nameFilterGate.Release();
        }
    }
}
