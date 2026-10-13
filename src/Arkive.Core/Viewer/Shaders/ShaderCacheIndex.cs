using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Packages;

namespace Arkive.Core.Viewer.Shaders;

/// <summary>Where one material shader map lives inside a shader cache export.</summary>
public sealed record ShaderCacheMaterialMap(
    string MaterialId,
    string FriendlyName,
    int CacheIndex,
    long ExpressionsOffset,
    string? BasePassPixelShaderId);

/// <summary>One shader cache export: its package and logical byte range.</summary>
public sealed record ShaderCacheExport(string PackagePath, long PackageLength, long PackageTicks, long Offset, long Size);

public sealed record ShaderCacheIndexDocument(
    int SchemaVersion,
    IReadOnlyList<ShaderCacheExport> Caches,
    IReadOnlyList<ShaderCacheMaterialMap> Maps,
    IReadOnlyDictionary<string, ShaderCacheRecord> PixelShaderRecords);

/// <summary>Where a serialized shader object (with its parameter bindings) starts.</summary>
public sealed record ShaderCacheRecord(int CacheIndex, long Offset);

/// <summary>Constant-buffer registers the compiled shader uses for its uniform expressions.</summary>
/// <param name="TextureSlots">The t# slot of each 2D texture expression, in expression order (empty when unknown).</param>
public sealed record ShaderParameterBindings(
    IReadOnlyList<int> ScalarRegisters,
    IReadOnlyList<int> VectorRegisters,
    IReadOnlyList<int> TextureSlots);

/// <summary>
/// Indexes Lost Ark's UE3 shader caches (the ReleasePC packages whose exports are
/// <c>ShaderCache</c> objects): which compiled shaders exist and, for every material
/// shader map, its friendly name, uniform expression set and base-pass pixel shader.
///
/// Layout (868/16 Western client), relative to a ShaderCache export:
/// +0x1D u32 shader count, then 24-byte entries (GUID, u16 blob, u32 offset, u16 size);
/// u32 blob count, then blobs (u32 raw size, u32 packed size, LZ4 block);
/// later, material shader maps: shader type/GUID pairs, a vertex factory, the
/// length-prefixed friendly name, the material GUID, 16 bytes, then the uniform
/// expression lists.
/// </summary>
public sealed class ShaderCacheIndex
{
    public const int CurrentSchemaVersion = 2;
    private const int IndexOffset = 0x1D;
    private const int EntrySize = 24;
    private const string BasePassPixelShader = "tbasepasspixelshaderfnolightmappolicyskylight";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly Dictionary<string, ShaderCacheMaterialMap> _maps;
    private readonly Dictionary<string, List<ShaderCacheMaterialMap>> _mapsByName;
    private readonly Dictionary<int, CacheState> _caches = new();
    private readonly object _gate = new();

    private ShaderCacheIndex(ShaderCacheIndexDocument document, IChunkProtectionProvider protectionProvider)
    {
        Document = document;
        _protectionProvider = protectionProvider;
        _maps = new Dictionary<string, ShaderCacheMaterialMap>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in document.Maps) _maps.TryAdd(map.MaterialId, map);
        _mapsByName = document.Maps.GroupBy(map => map.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public ShaderCacheIndexDocument Document { get; }

    public ShaderCacheMaterialMap? FindMap(string materialId) => _maps.GetValueOrDefault(materialId);

    public IReadOnlyList<ShaderCacheMaterialMap> FindMapsByName(string friendlyName) =>
        _mapsByName.TryGetValue(friendlyName, out var maps) ? maps : [];

    /// <summary>Finds the first 16-byte window of <paramref name="bytes"/> that is a known material id.</summary>
    public ShaderCacheMaterialMap? FindMapIn(ReadOnlySpan<byte> bytes)
    {
        for (var index = bytes.Length - 16; index >= 0; index--)
        {
            var window = bytes.Slice(index, 16);
            if (window.IndexOfAnyExcept((byte)0) < 0) continue;
            if (_maps.TryGetValue(Convert.ToHexString(window), out var map)) return map;
        }
        return null;
    }

    /// <summary>Loads a saved index when the cache packages are unchanged, otherwise rebuilds and saves it.</summary>
    public static ShaderCacheIndex LoadOrBuild(
        string releasePcDirectory,
        string indexPath,
        UpkPackageReader? reader,
        IChunkProtectionProvider protectionProvider,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var caches = DiscoverCaches(releasePcDirectory, reader, protectionProvider);
        if (File.Exists(indexPath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<ShaderCacheIndexDocument>(File.ReadAllBytes(indexPath), JsonOptions);
                if (saved is not null && saved.SchemaVersion == CurrentSchemaVersion && saved.Caches.SequenceEqual(caches))
                    return new ShaderCacheIndex(saved, protectionProvider);
            }
            catch (JsonException) { }
        }

        var maps = new List<ShaderCacheMaterialMap>();
        var records = new Dictionary<string, ShaderCacheRecord>(StringComparer.OrdinalIgnoreCase);
        for (var cacheIndex = 0; cacheIndex < caches.Count; cacheIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Indexing game shaders ({cacheIndex + 1}/{caches.Count})");
            maps.AddRange(ScanMaterialMaps(caches[cacheIndex], cacheIndex, reader, protectionProvider, records, cancellationToken));
        }
        var document = new ShaderCacheIndexDocument(CurrentSchemaVersion, caches, maps, records);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(indexPath))!);
        var temporary = indexPath + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
        File.Move(temporary, indexPath, overwrite: true);
        return new ShaderCacheIndex(document, protectionProvider);
    }

    private static List<ShaderCacheExport> DiscoverCaches(
        string releasePcDirectory,
        UpkPackageReader? reader,
        IChunkProtectionProvider protectionProvider)
    {
        var result = new List<ShaderCacheExport>();
        if (!Directory.Exists(releasePcDirectory)) return result;
        var catalogReader = new AssetCatalogReader(reader, protectionProvider);
        foreach (var path in Directory.EnumerateFiles(releasePcDirectory, "*.upk", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            AssetCatalog catalog;
            try { catalog = catalogReader.Read(path); }
            catch (Exception exception) when (exception is PackageFormatException or InvalidDataException or IOException
                                                  or NotSupportedException or ArgumentException)
            {
                continue;
            }
            var file = new FileInfo(path);
            foreach (var asset in catalog.Assets.Where(asset => asset.ClassName.Equals("shadercache", StringComparison.OrdinalIgnoreCase)))
                result.Add(new ShaderCacheExport(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, asset.SerialOffset, asset.SerialSize));
        }
        return result;
    }

    /// <summary>Finds material shader maps by their length-prefixed friendly names followed by valid expression lists.</summary>
    private static IEnumerable<ShaderCacheMaterialMap> ScanMaterialMaps(
        ShaderCacheExport cache,
        int cacheIndex,
        UpkPackageReader? reader,
        IChunkProtectionProvider protectionProvider,
        Dictionary<string, ShaderCacheRecord> records,
        CancellationToken cancellationToken)
    {
        var summary = (reader ?? new UpkPackageReader()).Read(cache.PackagePath);
        using var stream = new LogicalPackageStream(summary, protectionProvider);
        var names = new AssetCatalogReader(reader, protectionProvider).Read(summary).Names;
        var nameIndex = names.Select((name, index) => (name.Value, index))
            .GroupBy(pair => pair.Value, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().index);
        if (!nameIndex.TryGetValue(BasePassPixelShader, out var basePassName)) basePassName = -1;

        // Skip the shader index and blobs; material maps follow them.
        stream.Position = cache.Offset + IndexOffset;
        var count = ReadUInt32(stream);
        stream.Position = cache.Offset + IndexOffset + 4 + (long)count * EntrySize;
        var blobs = ReadUInt32(stream);
        for (var blob = 0; blob < blobs; blob++)
        {
            ReadUInt32(stream);
            var packed = ReadUInt32(stream);
            stream.Position += packed;
        }

        const int window = 8 << 20;
        const int overlap = 1 << 20;
        var buffer = new byte[window + overlap];
        var start = stream.Position;
        var end = cache.Offset + cache.Size;
        var previousEnd = start;
        var carried = 0;
        var bufferStart = start;
        while (bufferStart + carried < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = bufferStart + carried;
            var read = stream.Read(buffer, carried, (int)Math.Min(window, end - stream.Position));
            if (read <= 0) break;
            var length = carried + read;
            var limit = bufferStart + length >= end ? length : length - overlap;
            for (var i = 0; i < limit - 5; i++)
            {
                // A serialized base-pass pixel shader: its type name, GUID and a 20-byte hash (not the type again,
                // which is how material maps list their shaders).
                if (basePassName >= 0 && i + 52 <= length &&
                    BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i)) == basePassName &&
                    BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i + 4)) == 0 &&
                    !(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i + 24)) == basePassName &&
                      BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i + 28)) == 0))
                {
                    records.TryAdd(Convert.ToHexString(buffer, i + 8, 16), new ShaderCacheRecord(cacheIndex, bufferStart + i));
                }

                // A friendly name: u32 length (incl. NUL), ASCII identifier, NUL, then a 16-byte material GUID.
                var nameLength = buffer[i];
                if (nameLength is < 4 or > 96 || buffer[i + 1] != 0 || buffer[i + 2] != 0 || buffer[i + 3] != 0) continue;
                if (i + 4 + nameLength + 48 > length) continue;
                if (buffer[i + 4 + nameLength - 1] != 0 || !IsIdentifier(buffer.AsSpan(i + 4, nameLength - 1))) continue;
                var guidAt = i + 4 + nameLength;
                var listAt = guidAt + 32;
                // The first list (or, when it is empty, the next one) must start with a uniform expression class.
                var vectors = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(listAt));
                if (vectors is < 0 or > 256) continue;
                var probe = vectors > 0 ? listAt + 4 : listAt + 8;
                if (vectors == 0 && BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(listAt + 4)) is <= 0 or > 256) continue;
                var firstClass = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(probe));
                if (firstClass < 0 || firstClass >= names.Count ||
                    !names[firstClass].Value.StartsWith("fmaterialuniformexpression", StringComparison.Ordinal)) continue;

                var friendly = Encoding.ASCII.GetString(buffer, i + 4, nameLength - 1);
                var id = Convert.ToHexString(buffer, guidAt, 16);
                var absolute = bufferStart + i;
                string? basePass = basePassName < 0 ? null
                    : FindLastShaderPair(buffer, Math.Max(0, (int)(previousEnd - bufferStart)), i, basePassName);
                yield return new ShaderCacheMaterialMap(id, friendly, cacheIndex, bufferStart + listAt, basePass);
                previousEnd = absolute + 4 + nameLength;
                i = guidAt + 16;
            }
            if (bufferStart + length >= end) break;
            // Keep the last megabyte so names crossing the window edge are still seen.
            Buffer.BlockCopy(buffer, length - overlap, buffer, 0, overlap);
            bufferStart += length - overlap;
            carried = overlap;
        }
    }

    /// <summary>The GUID of the last "type, GUID, type" pair for <paramref name="typeName"/> in a range.</summary>
    private static string? FindLastShaderPair(byte[] buffer, int from, int to, int typeName)
    {
        for (var i = to - 24 - 8; i >= from; i--)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i)) != typeName ||
                BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i + 4)) != 0 ||
                BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i + 24)) != typeName ||
                BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i + 28)) != 0) continue;
            return Convert.ToHexString(buffer, i + 8, 16);
        }
        return null;
    }

    private static bool IsIdentifier(ReadOnlySpan<byte> text)
    {
        if (text.Length < 3 || !char.IsAsciiLetter((char)text[0])) return false;
        foreach (var c in text)
            if (!(char.IsAsciiLetterOrDigit((char)c) || c is (byte)'_' or (byte)'-' or (byte)'.' or (byte)' ')) return false;
        return true;
    }

    /// <summary>Reads the uniform expression lists of a material shader map.</summary>
    public ShaderMaterialExpressions ReadExpressions(ShaderCacheMaterialMap map)
    {
        var cache = Open(map.CacheIndex);
        lock (_gate)
        {
            cache.Stream.Position = map.ExpressionsOffset;
            var reader = new UniformExpressionReader(cache.Stream, cache.Names);
            var vectors = reader.ReadList();
            var scalars = reader.ReadList();
            var textures = reader.ReadList();
            return new ShaderMaterialExpressions(vectors, scalars, textures);
        }
    }

    /// <summary>
    /// Reads the uniform bindings serialized with a base-pass pixel shader: two arrays of
    /// (u32 index, u16 byte offset, u16 size, u16 buffer) for scalars then vectors, followed by an
    /// array of (u32 index, u16 texture slot, u16 count, u16 sampler) for the 2D textures.
    /// </summary>
    public ShaderParameterBindings? ReadBindings(string shaderId)
    {
        if (!Document.PixelShaderRecords.TryGetValue(shaderId, out var record)) return null;
        var cache = Open(record.CacheIndex);
        byte[] bytes;
        lock (_gate)
        {
            cache.Stream.Position = record.Offset;
            bytes = new byte[(int)Math.Min(4096, cache.Stream.Length - record.Offset)];
            cache.Stream.ReadExactly(bytes);
        }
        var arrays = new List<List<int>>();
        for (var i = 0; i + 4 < bytes.Length && arrays.Count < 2;)
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i));
            if (count is >= 1 and <= 64 && i + 4 + count * 10 <= bytes.Length && TryReadRegisters(bytes, i + 4, count, out var registers))
            {
                arrays.Add(registers);
                i += 4 + count * 10;
                continue;
            }
            if (arrays.Count == 1) break;
            i++;
        }
        if (arrays.Count != 2) return null;
        return new ShaderParameterBindings(arrays[0], arrays[1], ReadTextureSlots(bytes, arrays));
    }

    private static IReadOnlyList<int> ReadTextureSlots(byte[] bytes, List<List<int>> arrays)
    {
        // Find where the vector array ends by re-locating it, then read the texture array after it.
        for (var i = 0; i + 4 < bytes.Length; i++)
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i));
            if (count != arrays[1].Count || i + 4 + count * 10 > bytes.Length ||
                !TryReadRegisters(bytes, i + 4, count, out var registers) || !registers.SequenceEqual(arrays[1])) continue;
            var at = i + 4 + count * 10;
            if (at + 4 > bytes.Length) return [];
            var textures = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
            if (textures is < 0 or > 32 || at + 4 + textures * 10 > bytes.Length) return [];
            var slots = new List<int>(textures);
            for (var k = 0; k < textures; k++)
            {
                var row = bytes.AsSpan(at + 4 + k * 10, 10);
                if (BinaryPrimitives.ReadInt32LittleEndian(row) != k || BinaryPrimitives.ReadUInt16LittleEndian(row[6..]) != 1) return [];
                slots.Add(BinaryPrimitives.ReadUInt16LittleEndian(row[4..]));
            }
            return slots;
        }
        return [];
    }

    private static bool TryReadRegisters(byte[] bytes, int at, int count, out List<int> registers)
    {
        registers = new List<int>(count);
        for (var k = 0; k < count; k++)
        {
            var row = bytes.AsSpan(at + k * 10, 10);
            var index = BinaryPrimitives.ReadInt32LittleEndian(row);
            var offset = BinaryPrimitives.ReadUInt16LittleEndian(row[4..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(row[6..]);
            if (index != k || size != 16 || offset % 16 != 0) return false;
            registers.Add(offset / 16);
        }
        return true;
    }

    /// <summary>Returns the DXBC bytes of a compiled shader, searching every cache export.</summary>
    public byte[]? ReadShader(string shaderId)
    {
        for (var index = 0; index < Document.Caches.Count; index++)
        {
            var cache = Open(index);
            lock (_gate)
            {
                if (!cache.Entries.TryGetValue(shaderId, out var entry)) continue;
                var blob = cache.ReadBlob(entry.Blob);
                if (entry.Offset + entry.Size > blob.Length) return null;
                return blob.AsSpan(entry.Offset, entry.Size).ToArray();
            }
        }
        return null;
    }

    private CacheState Open(int index)
    {
        lock (_gate)
        {
            if (_caches.TryGetValue(index, out var state)) return state;
            state = new CacheState(Document.Caches[index], _protectionProvider);
            _caches[index] = state;
            return state;
        }
    }

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private sealed class CacheState
    {
        private readonly List<(long Offset, int Raw, int Packed)> _blobs = [];
        private readonly Dictionary<int, byte[]> _decoded = new();

        public CacheState(ShaderCacheExport cache, IChunkProtectionProvider protectionProvider)
        {
            var summary = new UpkPackageReader().Read(cache.PackagePath);
            Stream = new LogicalPackageStream(summary, protectionProvider);
            Names = new AssetCatalogReader(null, protectionProvider).Read(summary).Names.Select(name => name.Value).ToArray();
            Stream.Position = cache.Offset + IndexOffset;
            var count = (int)ReadUInt32(Stream);
            var table = new byte[checked(count * EntrySize)];
            Stream.ReadExactly(table);
            for (var i = 0; i < count; i++)
            {
                var row = table.AsSpan(i * EntrySize, EntrySize);
                Entries[Convert.ToHexString(row[..16])] = (
                    BinaryPrimitives.ReadUInt16LittleEndian(row[16..]),
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(row[18..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(row[22..]));
            }
            var blobs = (int)ReadUInt32(Stream);
            for (var i = 0; i < blobs; i++)
            {
                var raw = (int)ReadUInt32(Stream);
                var packed = (int)ReadUInt32(Stream);
                _blobs.Add((Stream.Position, raw, packed));
                Stream.Position += packed;
            }
        }

        public LogicalPackageStream Stream { get; }
        public IReadOnlyList<string> Names { get; }
        public Dictionary<string, (int Blob, int Offset, int Size)> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

        public byte[] ReadBlob(int index)
        {
            if (_decoded.TryGetValue(index, out var data)) return data;
            var (offset, raw, packed) = _blobs[index];
            var source = new byte[packed];
            Stream.Position = offset;
            Stream.ReadExactly(source);
            data = Arkive.Core.Compression.Lz4BlockDecoder.Decode(source, raw);
            if (_decoded.Count > 64) _decoded.Clear();
            _decoded[index] = data;
            return data;
        }
    }
}
