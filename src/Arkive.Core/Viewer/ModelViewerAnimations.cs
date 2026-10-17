using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arkive.Core.External;

namespace Arkive.Core.Viewer;

/// <summary>One animation the viewer can play.</summary>
public sealed record ModelViewerAnimation(string Name, string Group, int Frames, double Rate, string File);

/// <summary>Animations offered for one mesh: the prepared set and UModel's rest pose of the mesh's skeleton (a file in its folder).</summary>
public sealed record ModelViewerAnimationOffer(ModelViewerAnimationSet Set, string RestPoseFile);

/// <summary>A character's animations, prepared for the viewer: an index and one small file per animation.</summary>
public sealed record ModelViewerAnimationSet(
    string AnimSetName,
    string Directory,
    IReadOnlyList<string> Bones,
    IReadOnlyList<ModelViewerAnimation> Animations);

/// <summary>
/// Turns a character's AnimSet into files the viewer can play. UModel exports the set as an ActorX .psa (raw keys
/// for every sequence and bone, about 1 GB for a player class); Arkive keeps only the animations useful for checking
/// an outfit (idles, walking and running, character select, emotes, dances and poses), stores each bone's track once
/// when it does not move, packs rotations into 16-bit numbers, converts everything to the viewer's (glTF) axes and
/// deletes the .psa. The result is cached per package version.
/// </summary>
public sealed partial class ModelViewerAnimationService(UmodelBridge bridge, string cacheRoot)
{
    private const int FormatVersion = 2;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string CacheRoot { get; } = Path.GetFullPath(cacheRoot);

    /// <param name="keepAll">Keep every animation (NPC and monster sets are small and named differently).</param>
    public async Task<ModelViewerAnimationSet> PrepareAsync(
        string packagePath, string animSetName, bool keepAll = false, CancellationToken cancellationToken = default)
    {
        var package = new FileInfo(packagePath);
        if (!package.Exists) throw new FileNotFoundException("The animation package was not found.", packagePath);
        var key = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{package.FullName}|{package.Length}|{package.LastWriteTimeUtc.Ticks}|{animSetName}|{keepAll}|{FormatVersion}")))[..24];
        var directory = Path.Combine(CacheRoot, key);
        var indexPath = Path.Combine(directory, "animations.json");
        if (File.Exists(indexPath))
        {
            var cached = JsonSerializer.Deserialize<ModelViewerAnimationSet>(await File.ReadAllTextAsync(indexPath, cancellationToken)
                .ConfigureAwait(false), Json);
            if (cached is not null)
            {
                ModelViewerCache.TouchDirectory(directory);
                return cached with { Directory = directory };
            }
        }

        var work = Path.Combine(CacheRoot, $"export-{Guid.NewGuid():N}");
        try
        {
            var psa = await bridge.ExportAnimSetAsync(package.FullName, animSetName, work, cancellationToken).ConfigureAwait(false);
            var staging = directory + ".tmp";
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            var set = await Task.Run(() => Convert(psa, animSetName, staging, keepAll, cancellationToken), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, "animations.json"), JsonSerializer.Serialize(set, Json), cancellationToken)
                .ConfigureAwait(false);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Directory.Move(staging, directory);
            return set with { Directory = directory };
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// UModel's rest pose of a mesh's skeleton, in the same axes as the animation files. UModel orients some bones
    /// differently from Arkive's decoder (the pelvis and upper arms of player bodies, for example); the viewer uses
    /// both rest poses to move each animation key into Arkive's bone frames, so bodies are not twisted. Written once
    /// per mesh next to the animations; returns the file name.
    /// </summary>
    public async Task<string> PrepareRestPoseAsync(
        ModelViewerAnimationSet set, string meshPackagePath, string meshName, CancellationToken cancellationToken = default)
    {
        var mesh = new FileInfo(meshPackagePath);
        var key = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{mesh.FullName}|{mesh.Length}|{mesh.LastWriteTimeUtc.Ticks}|{meshName}|{FormatVersion}")))[..16];
        var file = $"rest-{key}.json";
        var path = Path.Combine(set.Directory, file);
        if (File.Exists(path)) return file;
        var work = Path.Combine(CacheRoot, $"skeleton-{Guid.NewGuid():N}");
        try
        {
            var psk = await bridge.ExportSkeletonAsync(mesh.FullName, meshName, work, cancellationToken).ConfigureAwait(false);
            var rest = ReadRestPose(await File.ReadAllBytesAsync(psk, cancellationToken).ConfigureAwait(false));
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(rest, Json), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return file;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>Each bone's rest rotation and position from a .psk, converted exactly as animation keys are.</summary>
    public static Dictionary<string, float[]> ReadRestPose(byte[] psk)
    {
        var rest = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (offset + 32 <= psk.Length)
        {
            var id = Encoding.ASCII.GetString(psk, offset, 20).TrimEnd('\0');
            var size = BinaryPrimitives.ReadInt32LittleEndian(psk.AsSpan(offset + 24));
            var count = BinaryPrimitives.ReadInt32LittleEndian(psk.AsSpan(offset + 28));
            offset += 32;
            if (id.Equals("REFSKELT", StringComparison.OrdinalIgnoreCase) && size >= 104)
            {
                for (var bone = 0; bone < count; bone++)
                {
                    var at = offset + bone * size;
                    var name = Encoding.ASCII.GetString(psk, at, 64).Split('\0')[0];
                    float F(int position) => BinaryPrimitives.ReadSingleLittleEndian(psk.AsSpan(at + position));
                    // Rotation (x, y, z, w) at 76, position at 92, converted exactly as the animation keys.
                    var rotation = ToViewerRotation(F(76), F(80), F(84), F(88), bone == 0);
                    var position = ToViewerPosition(F(92), F(96), F(100), bone == 0);
                    rest.TryAdd(name, [.. rotation, .. position]);
                }
            }
            offset += checked(size * count);
        }
        return rest;
    }

    /// <summary>Which group an animation belongs to, or null for ones not worth offering (mounts, props, hit reactions).</summary>
    public static string? GroupOf(string name)
    {
        var lower = name.ToLowerInvariant();
        if (UnwantedAnimation().IsMatch(lower)) return null;
        if (lower.StartsWith("idle", StringComparison.Ordinal) || lower.StartsWith("standup", StringComparison.Ordinal)) return "Idle";
        if (lower.StartsWith("characterselection", StringComparison.Ordinal) || lower.StartsWith("characterdeselection", StringComparison.Ordinal) ||
            lower.StartsWith("charactercreation", StringComparison.Ordinal)) return "Character select";
        if (lower.StartsWith("run", StringComparison.Ordinal) || lower.StartsWith("walk", StringComparison.Ordinal) ||
            lower.Contains("_walk", StringComparison.Ordinal) || lower.Contains("_run", StringComparison.Ordinal)) return "Walk and run";
        if (lower.Contains("dance", StringComparison.Ordinal)) return "Dances";
        if (lower.StartsWith("pose", StringComparison.Ordinal)) return "Poses";
        if (lower.StartsWith("act", StringComparison.Ordinal) || lower.StartsWith("sc_", StringComparison.Ordinal) ||
            lower.StartsWith("evt", StringComparison.Ordinal) || lower.StartsWith("event", StringComparison.Ordinal)) return "Emotes";
        return null;
    }

    private static readonly string[] GroupOrder = ["Idle", "Character select", "Walk and run", "Emotes", "Dances", "Poses", "Other"];

    private static ModelViewerAnimationSet Convert(string psaPath, string animSetName, string outputDirectory, bool keepAll,
        CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(psaPath);
        using var reader = new BinaryReader(stream);
        var chunks = new Dictionary<string, (long Offset, int Size, int Count)>(StringComparer.OrdinalIgnoreCase);
        while (stream.Position + 32 <= stream.Length)
        {
            var header = reader.ReadBytes(32);
            var id = Encoding.ASCII.GetString(header, 0, 20).TrimEnd('\0');
            var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24));
            var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28));
            chunks[id] = (stream.Position, size, count);
            stream.Seek((long)size * count, SeekOrigin.Current);
        }
        if (!chunks.TryGetValue("BONENAMES", out var boneChunk) || !chunks.TryGetValue("ANIMINFO", out var infoChunk) ||
            !chunks.TryGetValue("ANIMKEYS", out var keyChunk) || keyChunk.Size < 32)
            throw new InvalidDataException("The exported animation file is not an ActorX .psa.");

        stream.Position = boneChunk.Offset;
        var bones = new string[boneChunk.Count];
        for (var bone = 0; bone < bones.Length; bone++)
            bones[bone] = Encoding.ASCII.GetString(reader.ReadBytes(boneChunk.Size), 0, 64).Split('\0')[0];

        stream.Position = infoChunk.Offset;
        var sequences = new List<(string Name, int First, int Frames, float Rate)>();
        for (var index = 0; index < infoChunk.Count; index++)
        {
            var info = reader.ReadBytes(infoChunk.Size);
            var name = Encoding.ASCII.GetString(info, 0, 64).Split('\0')[0];
            var rate = BinaryPrimitives.ReadSingleLittleEndian(info.AsSpan(152));
            var first = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(160));
            var frames = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(164));
            sequences.Add((name, first, frames, rate));
        }

        var animations = new List<ModelViewerAnimation>();
        var keys = new byte[0];
        foreach (var (name, first, frames, rate) in sequences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = GroupOf(name) ?? (keepAll ? "Other" : null);
            if (group is null || frames <= 0) continue;
            var length = checked(frames * bones.Length * keyChunk.Size);
            if (keys.Length < length) keys = new byte[length];
            stream.Position = keyChunk.Offset + (long)first * bones.Length * keyChunk.Size;
            stream.ReadExactly(keys, 0, length);
            var file = $"{animations.Count:D4}.bin";
            File.WriteAllBytes(Path.Combine(outputDirectory, file), Pack(keys, frames, bones.Length, keyChunk.Size));
            animations.Add(new ModelViewerAnimation(name, group, frames, rate > 0 ? rate : 30, file));
        }
        animations = animations
            .OrderBy(animation => Array.IndexOf(GroupOrder, animation.Group))
            .ThenBy(animation => animation.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ModelViewerAnimationSet(animSetName, outputDirectory, bones, animations);
    }

    /// <summary>
    /// One animation, bone by bone: a flags byte (1 = the rotation changes, 2 = the position changes), then the rotation
    /// as 4 signed 16-bit numbers per frame (or once) and the position as 3 floats per frame (or once), in glTF axes and
    /// metres. A .psa stores Unreal rotations conjugated; converting them to the viewer's axes swaps Y and Z.
    /// </summary>
    private static byte[] Pack(byte[] keys, int frames, int boneCount, int keySize)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(frames);
        writer.Write(boneCount);
        for (var bone = 0; bone < boneCount; bone++)
        {
            float Read(int frame, int component) =>
                BinaryPrimitives.ReadSingleLittleEndian(keys.AsSpan((frame * boneCount + bone) * keySize + component * 4));
            bool Changes(int from, int count)
            {
                for (var frame = 1; frame < frames; frame++)
                    for (var component = from; component < from + count; component++)
                        if (Math.Abs(Read(frame, component) - Read(0, component)) > 1e-5f) return true;
                return false;
            }
            var rotates = Changes(3, 4);
            var moves = Changes(0, 3);
            writer.Write((byte)((rotates ? 1 : 0) | (moves ? 2 : 0)));
            for (var frame = 0; frame < (rotates ? frames : 1); frame++)
                foreach (var value in ToViewerRotation(Read(frame, 3), Read(frame, 4), Read(frame, 5), Read(frame, 6), bone == 0))
                    writer.Write((short)Math.Clamp(MathF.Round(value * 32767f), -32767, 32767));
            for (var frame = 0; frame < (moves ? frames : 1); frame++)
                foreach (var value in ToViewerPosition(Read(frame, 0), Read(frame, 1), Read(frame, 2), bone == 0))
                    writer.Write(value);
        }
        return output.ToArray();
    }

    /// <summary>
    /// An ActorX rotation (.psa key or .psk rest pose) in glTF axes, as Arkive's own mesh decoder gives the bone. ActorX
    /// stores the root bone's rotation as it is and every other bone's conjugated, so the root swaps Y and Z
    /// (x, z, y, w) while the others are also mirrored back through the bone's own frame (-x, -z, y, w). Converting
    /// the others like the root mirrored the skeleton front to back, and motions bent the wrong way.
    /// </summary>
    internal static float[] ToViewerRotation(float x, float y, float z, float w, bool root)
    {
        var length = MathF.Sqrt(x * x + y * y + z * z + w * w);
        if (length < 1e-8f) return [0, 0, 0, 1];
        return root ? [x / length, z / length, y / length, w / length] : [-x / length, -z / length, y / length, w / length];
    }

    /// <summary>An ActorX position in glTF axes and metres, matching <see cref="ToViewerRotation"/>.</summary>
    internal static float[] ToViewerPosition(float x, float y, float z, bool root) =>
        root ? [x * 0.01f, z * 0.01f, y * 0.01f] : [x * 0.01f, z * 0.01f, -y * 0.01f];

    [GeneratedRegex(@"^(ride|pr|knockdown|twistknockdown|airborne|psychokinesis|bound|abn|att|lp|stun|down|death|die|hit)_")]
    private static partial Regex UnwantedAnimation();
}
