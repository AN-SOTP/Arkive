using System.Security.Cryptography;
using System.Text.Json;

namespace Arkive.Core.Meshes;

/// <summary>
/// Reuses parsed GLBs during one Apply, where each mesh's files are otherwise read several times over (the edit check
/// before and inside encoding, then encoding itself). A parsed GLB is reused only for the exact same file bytes (by
/// SHA-256), the same read profile and the same source LOD contract, which are everything the reader depends on, so a
/// reused result is the one a fresh read would give. Failed reads are never kept. Outside an Apply nothing is cached.
/// </summary>
internal sealed class SkeletalMeshGlbReadCache
{
    private static readonly AsyncLocal<SkeletalMeshGlbReadCache?> CurrentCache = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, SkeletalMeshGlbSemanticDocument Document)>> _entries =
        new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, SkeletalMeshGlbSemanticDocument Document)> _recent = new();
    private readonly int _capacity;

    private SkeletalMeshGlbReadCache(int capacity) => _capacity = Math.Max(1, capacity);

    internal static SkeletalMeshGlbReadCache? Current => CurrentCache.Value;

    /// <summary>Starts reusing parsed GLBs on this call path and the tasks it starts, until disposed.</summary>
    public static IDisposable Begin(int capacity)
    {
        var previous = CurrentCache.Value;
        CurrentCache.Value = new SkeletalMeshGlbReadCache(capacity);
        return new Scope(() => CurrentCache.Value = previous);
    }

    internal static string Key(
        ReadOnlySpan<byte> bytes,
        SkeletalMeshEditableExportManifest sourceManifest,
        SkeletalMeshGlbReadProfile profile)
    {
        var contract = JsonSerializer.SerializeToUtf8Bytes(sourceManifest.Lods);
        return $"{profile}:{Convert.ToHexString(SHA256.HashData(bytes))}:{Convert.ToHexString(SHA256.HashData(contract))}";
    }

    internal bool TryGet(string key, out SkeletalMeshGlbSemanticDocument document)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                document = node.Value.Document;
                return true;
            }
        }
        document = null!;
        return false;
    }

    internal void Add(string key, SkeletalMeshGlbSemanticDocument document)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(key)) return;
            _entries.Add(key, _recent.AddFirst((key, document)));
            while (_entries.Count > _capacity)
            {
                var oldest = _recent.Last!;
                _recent.RemoveLast();
                _entries.Remove(oldest.Value.Key);
            }
        }
    }

    private sealed class Scope(Action end) : IDisposable
    {
        private Action? _end = end;
        public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
    }
}
