using System.Buffers.Binary;
using Arkive.Core.Audio;

namespace Arkive.Core.LoginScreens;

/// <summary>One action of a sound event in a bank: what it does and where its target id is stored.</summary>
/// <param name="Offset">Where the action's target id sits in the package's content (past the scramble marker).</param>
public sealed record LoginSoundAction(string Event, string PackagePath, long Offset, ushort Type, uint Target)
{
    public bool IsPlay => Type == 0x0403;

    // 0x01xx are the stop actions (stop this object, stop all but it...).
    public bool IsStop => Type >> 8 == 0x01;
}

/// <summary>A changed target id: what the game shipped there and what Arkive wrote.</summary>
public sealed record LoginSoundPatch(string PackagePath, long Offset, uint Original, uint Applied);

/// <summary>
/// The sounds of a login screen: its music (bgm_login_*) and its ambience (amb_intro_*_start, _stop and _pagechange1),
/// all events in the game's sound banks. The game plays the ones of the screen it shows. To give that screen another
/// screen's sounds, Arkive points those events' play and stop actions at the other screen's music and ambience: one
/// 4-byte id each, in the bank, so the real tracks play with their own looping.
/// </summary>
public static class LoginScreenSounds
{
    /// <summary>The events' actions, from the banks under <paramref name="audioRoot"/> (smallest packages first, stopping once all are found).</summary>
    public static IReadOnlyList<LoginSoundAction> FindActions(string audioRoot, IReadOnlyCollection<string> eventNames,
        CancellationToken cancellationToken = default)
    {
        var wanted = eventNames.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(SoundCatalog.WwiseId, name => name);
        var found = new List<LoginSoundAction>();
        if (!Directory.Exists(audioRoot) || wanted.Count == 0) return found;
        var seen = new HashSet<uint>();
        var packages = Directory.EnumerateFiles(audioRoot, "*.pck", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path)).OrderBy(file => file.Length).Select(file => file.FullName);
        foreach (var path in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var audio = WwiseAudioFile.Open(path);
            WwisePackage package;
            try
            {
                package = WwisePackage.Read(path, audio);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            foreach (var entry in package.Entries.Where(entry => entry.Section == WwisePackageSection.SoundBank))
            {
                if (entry.DataOffset + entry.Size > package.ContentLength || entry.Size > int.MaxValue) continue;
                ReadBank(audio.Read(entry.DataOffset, (int)entry.Size), entry.DataOffset, path, wanted, seen, found);
            }
            if (seen.Count == wanted.Count) break;
        }
        return found;
    }

    private static void ReadBank(byte[] data, long bankOffset, string path, Dictionary<uint, string> wanted,
        HashSet<uint> seen, List<LoginSoundAction> found)
    {
        var at = 0;
        while (at + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 4));
            var body = at + 8;
            if (size < 0 || body + size > data.Length) return;
            if (data.AsSpan(at, 4).SequenceEqual("HIRC"u8)) ReadHierarchy(data, body, size, bankOffset, path, wanted, seen, found);
            at = body + size;
        }
    }

    private static void ReadHierarchy(byte[] data, int start, int size, long bankOffset, string path,
        Dictionary<uint, string> wanted, HashSet<uint> seen, List<LoginSoundAction> found)
    {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start));
        var end = start + size;
        var at = start + 4;
        var actions = new Dictionary<uint, (ushort Type, uint Target, long Offset)>();
        var events = new List<(string Name, uint[] Actions)>();
        for (var index = 0; index < count && at + 9 <= end; index++)
        {
            var type = data[at];
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 1));
            var id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 5));
            var body = at + 9;
            if (length < 4 || at + 5 + length > end) return;
            if (type == 3 && length >= 10)
                actions[id] = (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body)),
                    BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body + 2)), bankOffset + body + 2);
            else if (type == 4 && wanted.TryGetValue(id, out var name) && length >= 5)
            {
                var actionCount = data[body];
                if (length >= 5 + 4 * actionCount)
                    events.Add((name, Enumerable.Range(0, actionCount)
                        .Select(item => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body + 1 + 4 * item))).ToArray()));
            }
            at += 5 + length;
        }
        foreach (var (name, ids) in events)
        {
            seen.Add(SoundCatalog.WwiseId(name));
            foreach (var actionId in ids)
                if (actions.TryGetValue(actionId, out var action))
                    found.Add(new LoginSoundAction(name, path, action.Offset, action.Type, action.Target));
        }
    }

    /// <summary>
    /// The changes that give <paramref name="live"/>'s events <paramref name="chosen"/>'s sounds. Each pair is an
    /// event of the screen the game shows and the matching event of the chosen screen; the first play action's
    /// target is swapped, in play and stop actions alike. An event missing on either side is left alone.
    /// </summary>
    public static IReadOnlyList<LoginSoundPatch> Plan(IReadOnlyList<LoginSoundAction> actions,
        IEnumerable<(string Live, string Chosen)> pairs)
    {
        var targets = new Dictionary<uint, uint>();
        var liveEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (live, chosen) in pairs)
        {
            var from = actions.FirstOrDefault(action => action.IsPlay && action.Event.Equals(live, StringComparison.OrdinalIgnoreCase));
            var to = actions.FirstOrDefault(action => action.IsPlay && action.Event.Equals(chosen, StringComparison.OrdinalIgnoreCase));
            liveEvents.Add(live);
            if (from is not null && to is not null && from.Target != to.Target) targets.TryAdd(from.Target, to.Target);
        }
        return actions
            .Where(action => liveEvents.Contains(action.Event) && (action.IsPlay || action.IsStop) &&
                             targets.ContainsKey(action.Target))
            .Select(action => new LoginSoundPatch(action.PackagePath, action.Offset, action.Target, targets[action.Target]))
            .DistinctBy(patch => (patch.PackagePath.ToUpperInvariant(), patch.Offset))
            .ToArray();
    }

    /// <summary>Writes the patches (each only where the package still holds the value it expects).</summary>
    public static void Write(IEnumerable<LoginSoundPatch> patches, bool restore)
    {
        var value = new byte[4];
        foreach (var group in patches.GroupBy(patch => patch.PackagePath, StringComparer.OrdinalIgnoreCase))
        {
            using var audio = WwiseAudioFile.Open(group.Key, writable: true);
            foreach (var patch in group)
            {
                var (expected, write) = restore ? (patch.Applied, patch.Original) : (patch.Original, patch.Applied);
                if (patch.Offset + 4 > audio.ContentLength ||
                    BinaryPrimitives.ReadUInt32LittleEndian(audio.Read(patch.Offset, 4)) != expected) continue;
                BinaryPrimitives.WriteUInt32LittleEndian(value, write);
                audio.Write(patch.Offset, value);
            }
            audio.Flush();
        }
    }

    /// <summary>True when every patch is still in place (the game has not replaced the packages).</summary>
    public static bool InPlace(IEnumerable<LoginSoundPatch> patches)
    {
        foreach (var group in patches.GroupBy(patch => patch.PackagePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(group.Key)) return false;
            using var audio = WwiseAudioFile.Open(group.Key);
            if (group.Any(patch => patch.Offset + 4 > audio.ContentLength ||
                                   BinaryPrimitives.ReadUInt32LittleEndian(audio.Read(patch.Offset, 4)) != patch.Applied))
                return false;
        }
        return true;
    }
}
