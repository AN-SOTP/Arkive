using System.Numerics;
using System.Text.RegularExpressions;

namespace Arkive.Core.Outfits;

/// <summary>Where outfit swaps read outfit pieces from: the indexed game library.</summary>
public interface IOutfitSwapLibrary
{
    /// <summary>The names of the library's skeletal meshes that start with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyCollection<string>> FindAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>A piece's editable mesh from its clean package, or null when Arkive cannot read it.</summary>
    Task<OutfitSwapMesh?> LoadAsync(string objectName, CancellationToken cancellationToken);
}

/// <summary>
/// Learns how two bodies differ from the costumes the game makes for both: the same costume number for two classes
/// shares its materials and its cloth layout, refitted by the game's artists. Comparing the two versions point by point
/// (after the skeleton has moved one onto the other) shows the body-shape difference. Results are cached per body pair.
/// </summary>
public sealed class OutfitSwapLearning(IOutfitSwapLibrary library, string cacheDirectory)
{
    private const int CacheVersion = 3;
    private const int CostumeLimit = 20;

    public async Task<OutfitSwapShapeField?> LearnAsync(OutfitSwapBodyInfo from, OutfitSwapBodyInfo to, string gameBuild,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (from.AvatarCode == to.AvatarCode) return null;
        var cache = Path.Combine(cacheDirectory, $"v{CacheVersion}-{Safe(gameBuild)}-{from.Profile.Id}--{to.Profile.Id}.bin");
        if (Read(cache) is { } cached) return cached;

        var fromPieces = await CostumesAsync(from.AvatarCode, cancellationToken).ConfigureAwait(false);
        var toPieces = await CostumesAsync(to.AvatarCode, cancellationToken).ConfigureAwait(false);
        var shared = fromPieces.Keys.Intersect(toPieces.Keys)
            .OrderBy(key => key.Part != "dress").ThenBy(key => key.Number, StringComparer.Ordinal).ToArray();
        var moved = new List<Vector3>();
        var features = new List<float>();
        var corrections = new List<Vector3>();
        var costumes = 0;
        foreach (var key in shared)
        {
            if (costumes >= CostumeLimit) break;
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Learning {from.Profile.DisplayName} → {to.Profile.DisplayName} from shared costumes ({costumes} of {CostumeLimit})");
            var a = await library.LoadAsync(fromPieces[key], cancellationToken).ConfigureAwait(false);
            var b = await library.LoadAsync(toPieces[key], cancellationToken).ConfigureAwait(false);
            if (a is null || b is null || !a.HasBodySkeleton || !b.HasBodySkeleton) continue;
            var pairs = Match(a, b);
            if (pairs.Count < 500) continue;
            var maps = OutfitSwapFitting.BoneMaps(a, new OutfitSwapSkeleton([b]));
            var vertices = pairs.Select(pair => pair.From).ToArray();
            var feature = OutfitSwapFitting.Features(a, vertices);
            var retargeted = OutfitSwapFitting.Retarget(a, maps);
            foreach (var (fromVertex, toVertex) in pairs)
            {
                moved.Add(retargeted[fromVertex]);
                corrections.Add(b.Positions[toVertex] - retargeted[fromVertex]);
            }
            features.AddRange(feature);
            costumes++;
        }
        if (costumes == 0) return null;
        var field = new OutfitSwapShapeField(moved.ToArray(), features.ToArray(), corrections.ToArray(), costumes);
        Write(cache, moved, features, corrections, costumes);
        return field;
    }

    private async Task<Dictionary<(string Number, string Part), string>> CostumesAsync(string code, CancellationToken cancellationToken)
    {
        var pattern = new Regex($@"^pc_{Regex.Escape(code)}_av_(\d+[a-z]?)_(dress|upper|lower)_sk$", RegexOptions.IgnoreCase);
        var result = new Dictionary<(string, string), string>();
        foreach (var name in await library.FindAsync($"pc_{code}_av_", cancellationToken).ConfigureAwait(false))
            if (pattern.Match(name) is { Success: true } match)
                result.TryAdd((match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value.ToLowerInvariant()), name);
        return result;
    }

    /// <summary>Vertex pairs of the same costume made for two bodies: same material, same UV position, nearest place.</summary>
    internal static List<(int From, int To)> Match(OutfitSwapMesh a, OutfitSwapMesh b)
    {
        var result = new List<(int, int)>();
        var index = new int[1];
        var distance = new float[1];
        var query = new float[5];
        foreach (var target in b.Sections)
        {
            var material = OutfitSwapMesh.ShortMaterialName(b.Materials[target.Material]);
            if (OutfitSwapBodies.IsSkin(material)) continue;
            var source = a.Sections.FirstOrDefault(section => OutfitSwapMesh.ShortMaterialName(a.Materials[section.Material]) == material);
            if (source is null) continue;
            var to = target.Triangles.Distinct().ToArray();
            var from = source.Triangles.Distinct().ToArray();
            var points = new float[to.Length * 5];
            for (var i = 0; i < to.Length; i++)
            {
                var v = to[i];
                points[i * 5] = b.Uv0[v].X * 1000; points[i * 5 + 1] = b.Uv0[v].Y * 1000;
                points[i * 5 + 2] = b.Positions[v].X; points[i * 5 + 3] = b.Positions[v].Y; points[i * 5 + 4] = b.Positions[v].Z;
            }
            var tree = new OutfitSwapKdTree(points, 5);
            foreach (var v in from)
            {
                query[0] = a.Uv0[v].X * 1000; query[1] = a.Uv0[v].Y * 1000;
                query[2] = a.Positions[v].X; query[3] = a.Positions[v].Y; query[4] = a.Positions[v].Z;
                if (tree.Nearest(query, 1, index, distance) == 0) continue;
                var match = to[index[0]];
                if (Vector2.Distance(a.Uv0[v], b.Uv0[match]) < 1e-4f) result.Add((v, match));
            }
        }
        return result;
    }

    private static string Safe(string text) => new(text.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());

    private static OutfitSwapShapeField? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadInt32() != CacheVersion) return null;
            var costumes = reader.ReadInt32();
            var count = reader.ReadInt32();
            var moved = new Vector3[count];
            var corrections = new Vector3[count];
            var features = new float[count * OutfitSwapFitting.FeatureCount];
            for (var i = 0; i < count; i++)
            {
                moved[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                corrections[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                for (var f = 0; f < OutfitSwapFitting.FeatureCount; f++) features[i * OutfitSwapFitting.FeatureCount + f] = reader.ReadByte() / 255f;
            }
            return new OutfitSwapShapeField(moved, features, corrections, costumes);
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException) { return null; }
    }

    private static void Write(string path, List<Vector3> moved, List<float> features, List<Vector3> corrections, int costumes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        using (var writer = new BinaryWriter(File.Create(temporary)))
        {
            writer.Write(CacheVersion);
            writer.Write(costumes);
            writer.Write(moved.Count);
            for (var i = 0; i < moved.Count; i++)
            {
                writer.Write(moved[i].X); writer.Write(moved[i].Y); writer.Write(moved[i].Z);
                writer.Write(corrections[i].X); writer.Write(corrections[i].Y); writer.Write(corrections[i].Z);
                for (var f = 0; f < OutfitSwapFitting.FeatureCount; f++)
                    writer.Write((byte)Math.Clamp(MathF.Round(features[i * OutfitSwapFitting.FeatureCount + f] * 255), 0, 255));
            }
        }
        File.Move(temporary, path, overwrite: true);
    }
}
