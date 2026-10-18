using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Packages;

namespace Arkive.Core.Viewer.Shaders;

/// <summary>Where one material shader map (one compiled permutation of a material) lives in a shader cache.</summary>
/// <param name="MaterialId">The base material GUID every permutation of the material shares.</param>
/// <param name="VariantKey">The <see cref="StaticParameterSet.Key"/> the permutation was compiled for.</param>
/// <param name="OnSwitches">Its static switches that are on (<see cref="StaticParameterSet.OnSwitches"/>).</param>
public sealed record ShaderCacheMaterialMap(
    string MaterialId,
    string FriendlyName,
    int CacheIndex,
    long ExpressionsOffset,
    string? BasePassPixelShaderId,
    string VariantKey,
    IReadOnlyList<string> OnSwitches,
    int SwitchCount)
{
    /// <summary>
    /// The base-pass pixel shader compiled for each vertex factory the material is used with (lower-case type names
    /// such as fparticledynamicparametervertexfactory); particles need their own, as their inputs differ from meshes'.
    /// </summary>
    public IReadOnlyDictionary<string, string>? VertexFactoryPixelShaders { get; init; }
}

/// <summary>The shader map chosen for a material, and how it was matched.</summary>
/// <param name="Exact">The map was compiled for exactly the material's static parameters.</param>
/// <param name="Differences">Static switches that differ, for a nearest match.</param>
public sealed record ShaderVariantMatch(ShaderCacheMaterialMap Map, bool Exact, int Differences, bool FromStaticParameters);

/// <summary>One shader cache export: its package and logical byte range.</summary>
public sealed record ShaderCacheExport(string PackagePath, long PackageLength, long PackageTicks, long Offset, long Size);

public sealed record ShaderCacheIndexDocument(
    int SchemaVersion,
    IReadOnlyList<ShaderCacheExport> Caches,
    IReadOnlyList<ShaderCacheMaterialMap> Maps,
    IReadOnlyDictionary<string, ShaderCacheRecord> PixelShaderRecords,
    IReadOnlyDictionary<int, long[]>? BlobOffsets = null);

/// <summary>Where a serialized shader object (with its parameter bindings) starts.</summary>
public sealed record ShaderCacheRecord(int CacheIndex, long Offset);

/// <summary>Constant-buffer registers the compiled shader uses for its uniform expressions (-1 where unused).</summary>
/// <param name="ScalarRegisters">The register of each group of four scalar expressions.</param>
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
    public const int CurrentSchemaVersion = 6;
    private const int IndexOffset = 0x1D;
    private const int EntrySize = 24;
    private const string BasePassPixelShader = "tbasepasspixelshaderfnolightmappolicyskylight";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly Dictionary<string, List<ShaderCacheMaterialMap>> _mapsByBase;
    private readonly Dictionary<string, ShaderCacheMaterialMap> _mapsByVariant;
    private readonly Dictionary<string, List<ShaderCacheMaterialMap>> _mapsByName;
    private readonly Dictionary<int, CacheState> _caches = new();
    private readonly object _gate = new();

    private ShaderCacheIndex(ShaderCacheIndexDocument document, IChunkProtectionProvider protectionProvider)
    {
        Document = document;
        _protectionProvider = protectionProvider;
        // Several caches can hold the same permutation; the first one with a pixel shader wins.
        var usable = document.Maps.OrderBy(map => map.BasePassPixelShaderId is null ? 1 : 0).ToList();
        _mapsByBase = usable.GroupBy(map => map.MaterialId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        _mapsByVariant = new Dictionary<string, ShaderCacheMaterialMap>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in usable) _mapsByVariant.TryAdd(map.VariantKey, map);
        _mapsByName = document.Maps.GroupBy(map => map.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public ShaderCacheIndexDocument Document { get; }

    public IReadOnlyList<ShaderCacheMaterialMap> FindMapsByName(string friendlyName) =>
        _mapsByName.TryGetValue(friendlyName, out var maps) ? maps : [];

    /// <summary>
    /// Finds the permutation a material export uses. A material instance with its own static permutation
    /// stores an <see cref="StaticParameterSet"/>: the map compiled for the same values is an exact match,
    /// otherwise the map of the same base material with the fewest differing switches. Without a set, a
    /// known base material GUID in the bytes selects that material's default (switch-free) permutation.
    /// </summary>
    public ShaderVariantMatch? FindVariant(ReadOnlySpan<byte> bytes)
    {
        ShaderVariantMatch? baseOnly = null;
        for (var index = 0; index + 16 <= bytes.Length; index++)
        {
            var window = bytes.Slice(index, 16);
            if (window.IndexOfAnyExcept((byte)0) < 0) continue;
            if (!_mapsByBase.TryGetValue(Convert.ToHexString(window), out var maps)) continue;
            if (StaticParameterSet.TryRead(bytes, index, out var set, out _) && set.Switches.Count + set.ComponentMasks.Count > 0)
                return Match(set, maps);
            baseOnly ??= Default(maps);
        }
        return baseOnly;
    }

    private ShaderVariantMatch Match(StaticParameterSet set, List<ShaderCacheMaterialMap> maps)
    {
        if (_mapsByVariant.TryGetValue(set.Key, out var exact)) return new ShaderVariantMatch(exact, true, 0, true);
        var on = set.OnSwitches.ToHashSet(StringComparer.Ordinal);
        var nearest = maps.Where(map => map.BasePassPixelShaderId is not null)
            .Select(map => (Map: map, Differences: map.OnSwitches.Count(item => !on.Contains(item)) +
                                                   on.Count(item => !map.OnSwitches.Contains(item, StringComparer.Ordinal))))
            .OrderBy(item => item.Differences)
            .ThenBy(item => Math.Abs(item.Map.SwitchCount - set.Switches.Count))
            .FirstOrDefault();
        return nearest.Map is null ? Default(maps) : new ShaderVariantMatch(nearest.Map, false, nearest.Differences, true);
    }

    private static ShaderVariantMatch Default(List<ShaderCacheMaterialMap> maps)
    {
        var plain = maps.FirstOrDefault(map => map.SwitchCount == 0 && map.BasePassPixelShaderId is not null);
        return plain is not null
            ? new ShaderVariantMatch(plain, true, 0, false)
            : new ShaderVariantMatch(maps.FirstOrDefault(map => map.BasePassPixelShaderId is not null) ?? maps[0], false, -1, false);
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
        if (File.Exists(indexPath))
        {
            try
            {
                // Reuse the saved index while every cache package it came from is unchanged.
                var saved = JsonSerializer.Deserialize<ShaderCacheIndexDocument>(File.ReadAllBytes(indexPath), JsonOptions);
                if (saved is not null && saved.SchemaVersion == CurrentSchemaVersion && saved.Caches.Count > 0 &&
                    saved.Caches.All(Unchanged))
                    return new ShaderCacheIndex(saved, protectionProvider);
            }
            catch (JsonException) { }
        }
        var caches = DiscoverCaches(releasePcDirectory, reader, protectionProvider);

        var maps = new List<ShaderCacheMaterialMap>();
        var records = new Dictionary<string, ShaderCacheRecord>(StringComparer.OrdinalIgnoreCase);
        var blobOffsets = new Dictionary<int, long[]>();
        for (var cacheIndex = 0; cacheIndex < caches.Count; cacheIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Indexing game shaders ({cacheIndex + 1}/{caches.Count})");
            var offsets = new List<long>();
            maps.AddRange(ScanMaterialMaps(caches[cacheIndex], cacheIndex, reader, protectionProvider, records, offsets,
                cancellationToken));
            blobOffsets[cacheIndex] = offsets.ToArray();
        }
        // Caches repeat permutations; keep one map for each, preferring one with a base-pass pixel shader.
        maps = maps.GroupBy(map => map.VariantKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.FirstOrDefault(map => map.BasePassPixelShaderId is not null) ?? group.First())
            .ToList();
        var used = maps.Select(map => map.BasePassPixelShaderId).OfType<string>()
            .Concat(maps.SelectMany(map => map.VertexFactoryPixelShaders?.Values ?? []))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        records = records.Where(pair => used.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var document = new ShaderCacheIndexDocument(CurrentSchemaVersion, caches, maps, records, blobOffsets);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(indexPath))!);
        var temporary = indexPath + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
        File.Move(temporary, indexPath, overwrite: true);
        return new ShaderCacheIndex(document, protectionProvider);
    }

    private static bool Unchanged(ShaderCacheExport cache)
    {
        var file = new FileInfo(cache.PackagePath);
        return file.Exists && file.Length == cache.PackageLength && file.LastWriteTimeUtc.Ticks == cache.PackageTicks;
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

    /// <summary>
    /// Finds material shader maps: a length-prefixed friendly name, then the static parameter set the map
    /// was compiled for, then valid uniform expression lists.
    /// </summary>
    private static IEnumerable<ShaderCacheMaterialMap> ScanMaterialMaps(
        ShaderCacheExport cache,
        int cacheIndex,
        UpkPackageReader? reader,
        IChunkProtectionProvider protectionProvider,
        Dictionary<string, ShaderCacheRecord> records,
        List<long> blobOffsets,
        CancellationToken cancellationToken)
    {
        var summary = (reader ?? new UpkPackageReader()).Read(cache.PackagePath);
        using var stream = new LogicalPackageStream(summary, protectionProvider);
        var names = new AssetCatalogReader(reader, protectionProvider).Read(summary).Names;
        var nameIndex = names.Select((name, index) => (name.Value, index))
            .GroupBy(pair => pair.Value, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().index);
        if (!nameIndex.TryGetValue(BasePassPixelShader, out var basePassName)) basePassName = -1;
        var vertexFactoryNames = names.Select((name, index) => (name.Value, index))
            .Where(pair => pair.Value.EndsWith("vertexfactory", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.index, pair => pair.Value.ToLowerInvariant());

        // Skip the shader index and blobs; material maps follow them.
        stream.Position = cache.Offset + IndexOffset;
        var count = ReadUInt32(stream);
        stream.Position = cache.Offset + IndexOffset + 4 + (long)count * EntrySize;
        var blobs = ReadUInt32(stream);
        for (var blob = 0; blob < blobs; blob++)
        {
            blobOffsets.Add(stream.Position);
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
            // The logical stream may return less than asked (it stops at chunk edges), so fill the window.
            var wanted = (int)Math.Min(window, end - stream.Position);
            var read = stream.ReadAtLeast(buffer.AsSpan(carried, wanted), wanted, throwOnEndOfStream: false);
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

                // A friendly name: u32 length (incl. NUL), ASCII identifier, NUL, then the static parameter set.
                var nameLength = buffer[i];
                if (nameLength is < 4 or > 96 || buffer[i + 1] != 0 || buffer[i + 2] != 0 || buffer[i + 3] != 0) continue;
                if (i + 4 + nameLength + 48 > length) continue;
                if (buffer[i + 4 + nameLength - 1] != 0 || !IsIdentifier(buffer.AsSpan(i + 4, nameLength - 1))) continue;
                var guidAt = i + 4 + nameLength;
                if (!StaticParameterSet.TryRead(buffer.AsSpan(0, length), guidAt, out var parameters, out var listAt) ||
                    listAt + 12 > length) continue;
                // The first list (or, when it is empty, the next one) must start with a uniform expression class.
                var vectors = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(listAt));
                if (vectors is < 0 or > 256) continue;
                var probe = vectors > 0 ? listAt + 4 : listAt + 8;
                if (vectors == 0 && BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(listAt + 4)) is <= 0 or > 256) continue;
                var firstClass = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(probe));
                if (firstClass < 0 || firstClass >= names.Count ||
                    !names[firstClass].Value.StartsWith("fmaterialuniformexpression", StringComparison.Ordinal)) continue;

                var friendly = Encoding.ASCII.GetString(buffer, i + 4, nameLength - 1);
                var absolute = bufferStart + i;
                string? basePass = basePassName < 0 ? null
                    : FindLastShaderPair(buffer, Math.Max(0, (int)(previousEnd - bufferStart)), i, basePassName);
                var perFactory = basePassName < 0 ? null
                    : FindVertexFactoryShaders(buffer, Math.Max(0, (int)(previousEnd - bufferStart)), i, basePassName, vertexFactoryNames);
                yield return new ShaderCacheMaterialMap(parameters.BaseMaterialId, friendly, cacheIndex, bufferStart + listAt,
                    basePass, parameters.Key, parameters.OnSwitches, parameters.Switches.Count)
                {
                    VertexFactoryPixelShaders = perFactory is { Count: > 0 } ? perFactory : null,
                };
                previousEnd = absolute + 4 + nameLength;
                i = listAt;
            }
            if (bufferStart + length >= end) break;
            // Keep the last megabyte so names crossing the window edge are still seen.
            Buffer.BlockCopy(buffer, length - overlap, buffer, 0, overlap);
            bufferStart += length - overlap;
            carried = overlap;
        }
    }

    /// <summary>
    /// A material map lists its shaders per vertex factory: the factory's type name, then (type, GUID, type) entries.
    /// Returns the base-pass pixel shader GUID that follows each factory name in the range.
    /// </summary>
    private static Dictionary<string, string> FindVertexFactoryShaders(byte[] buffer, int from, int to, int typeName,
        IReadOnlyDictionary<int, string> vertexFactories)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? factory = null;
        for (var i = from; i + 32 <= to; i++)
        {
            var index = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i));
            if (BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i + 4)) != 0) continue;
            if (vertexFactories.TryGetValue(index, out var name))
            {
                factory = name;
                i += 7;
                continue;
            }
            if (factory is null || index != typeName ||
                BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i + 24)) != typeName ||
                BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i + 28)) != 0) continue;
            result.TryAdd(factory, Convert.ToHexString(buffer, i + 8, 16));
            factory = null;
            i += 31;
        }
        return result;
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
        var end = 0;
        for (var i = 0; i + 4 < bytes.Length && arrays.Count < 2;)
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i));
            if (count is >= 1 and <= 64 && i + 4 + count * 10 <= bytes.Length && TryReadRegisters(bytes, i + 4, count, out var registers))
            {
                arrays.Add(registers);
                i += 4 + count * 10;
                end = i;
                continue;
            }
            if (arrays.Count == 1)
            {
                // A shader without scalar parameters: the one array is the vectors when textures follow it.
                if (IsTextureArray(bytes, i)) arrays.Insert(0, []);
                break;
            }
            i++;
        }
        if (arrays.Count != 2) return null;
        return new ShaderParameterBindings(arrays[0], arrays[1], ReadTextureSlots(bytes, end));
    }

    private static bool IsTextureArray(byte[] bytes, int at)
    {
        if (at + 4 > bytes.Length) return false;
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
        if (count is < 1 or > 32 || at + 4 + count * 10 > bytes.Length) return false;
        for (var k = 0; k < count; k++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 4 + k * 10 + 6)) != 1) return false;
        return true;
    }

    /// <summary>Reads the texture array that follows the vector array.</summary>
    private static IReadOnlyList<int> ReadTextureSlots(byte[] bytes, int at)
    {
        if (at + 4 > bytes.Length) return [];
        var textures = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
        if (textures is < 0 or > 32 || at + 4 + textures * 10 > bytes.Length) return [];
        // Rows are (u32 expression index, u16 slot, u16 count = 1, u16 sampler); unused textures are dropped.
        var slots = new List<int>(textures);
        for (var k = 0; k < textures; k++)
        {
            var row = bytes.AsSpan(at + 4 + k * 10, 10);
            var index = BinaryPrimitives.ReadInt32LittleEndian(row);
            if (index < slots.Count || index > 255 || BinaryPrimitives.ReadUInt16LittleEndian(row[6..]) != 1) return [];
            while (slots.Count < index) slots.Add(-1);
            slots.Add(BinaryPrimitives.ReadUInt16LittleEndian(row[4..]));
        }
        return slots;
    }

    /// <summary>
    /// Reads (u32 expression index, u16 byte offset, u16 size, u16 buffer) rows. The compiler drops unused
    /// parameters, so indices only increase; the result holds -1 for the dropped ones.
    /// </summary>
    private static bool TryReadRegisters(byte[] bytes, int at, int count, out List<int> registers)
    {
        registers = new List<int>(count);
        for (var k = 0; k < count; k++)
        {
            var row = bytes.AsSpan(at + k * 10, 10);
            var index = BinaryPrimitives.ReadInt32LittleEndian(row);
            var offset = BinaryPrimitives.ReadUInt16LittleEndian(row[4..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(row[6..]);
            if (index < registers.Count || index > 255 || size != 16 || offset % 16 != 0) return false;
            while (registers.Count < index) registers.Add(-1);
            registers.Add(offset / 16);
        }
        return true;
    }

    /// <summary>Returns the DXBC bytes of a compiled shader, trying its material map's cache export first.</summary>
    public byte[]? ReadShader(string shaderId, int preferredCache = 0)
    {
        var order = Enumerable.Range(0, Document.Caches.Count)
            .OrderBy(index => index == preferredCache ? 0 : 1);
        foreach (var index in order)
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
            state = new CacheState(Document.Caches[index], _protectionProvider, Document.BlobOffsets?.GetValueOrDefault(index));
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
        private readonly List<long> _blobs = [];
        private readonly Dictionary<int, byte[]> _decoded = new();

        public CacheState(ShaderCacheExport cache, IChunkProtectionProvider protectionProvider, long[]? blobOffsets)
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
            if (blobOffsets is not null)
            {
                _blobs.AddRange(blobOffsets);
                return;
            }
            var blobs = (int)ReadUInt32(Stream);
            for (var i = 0; i < blobs; i++)
            {
                _blobs.Add(Stream.Position);
                ReadUInt32(Stream);
                var packed = (int)ReadUInt32(Stream);
                Stream.Position += packed;
            }
        }

        public LogicalPackageStream Stream { get; }
        public IReadOnlyList<string> Names { get; }
        public Dictionary<string, (int Blob, int Offset, int Size)> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

        public byte[] ReadBlob(int index)
        {
            if (_decoded.TryGetValue(index, out var data)) return data;
            Stream.Position = _blobs[index];
            var raw = (int)ReadUInt32(Stream);
            var packed = (int)ReadUInt32(Stream);
            if (raw is < 0 or > 64 << 20 || packed is < 0 or > 64 << 20) throw new InvalidDataException("Invalid shader blob size.");
            var source = new byte[packed];
            Stream.ReadExactly(source);
            data = Arkive.Core.Compression.Lz4BlockDecoder.Decode(source, raw);
            if (_decoded.Count > 64) _decoded.Clear();
            _decoded[index] = data;
            return data;
        }
    }
}
