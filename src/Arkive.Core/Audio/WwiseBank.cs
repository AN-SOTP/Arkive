using System.Buffers.Binary;

namespace Arkive.Core.Audio;

/// <summary>
/// Where a bank says a sound's audio comes from. The offsets are inside the bank, so a replacement can rewrite the
/// codec and the stored size in place (both are fixed-width).
/// </summary>
public sealed record WwiseBankSource(
    uint SourceId,
    uint PluginId,
    byte StreamType,
    uint InMemorySize,
    int PluginIdOffset,
    int InMemorySizeOffset)
{
    /// <summary>True for audio Arkive can replace: codec-backed media (Vorbis, PCM, ADPCM...), not a generator plug-in.</summary>
    public bool IsCodec => (PluginId & 0xF) == 1;
}

/// <summary>A music track source with the length (milliseconds) the music system plays it for.</summary>
public sealed record WwiseMusicSource(WwiseBankSource Source, double DurationMs);

/// <summary>
/// The parts of a Wwise sound bank (bank version 134, Wwise 2019.1) Arkive needs to name and replace audio: events,
/// their play actions, the object tree, and which audio each sound or music track plays.
/// </summary>
public sealed class WwiseBank
{
    public const uint SupportedVersion = 134;
    public const uint PcmPluginId = 0x00010001;
    public const uint VorbisPluginId = 0x00040001;
    private const ushort PlayAction = 0x0403;

    public uint Version { get; private set; }
    public uint BankId { get; private set; }

    /// <summary>Event id to the ids of the objects its play actions start.</summary>
    public Dictionary<uint, List<uint>> EventTargets { get; } = [];

    /// <summary>Object id to its parent in the actor-mixer or music tree.</summary>
    public Dictionary<uint, uint> Parents { get; } = [];

    public Dictionary<uint, WwiseBankSource> Sounds { get; } = [];

    public Dictionary<uint, List<WwiseMusicSource>> MusicTracks { get; } = [];

    /// <summary>Media stored inside the bank itself (rare in Lost Ark); Arkive cannot replace these yet.</summary>
    public HashSet<uint> EmbeddedMedia { get; } = [];

    public static WwiseBank Parse(ReadOnlySpan<byte> data)
    {
        var bank = new WwiseBank();
        var events = new Dictionary<uint, uint[]>();
        var actions = new Dictionary<uint, (ushort Type, uint Target)>();
        var at = 0;
        while (at + 8 <= data.Length)
        {
            var tag = data.Slice(at, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            var body = at + 8;
            if (size < 0 || body + size > data.Length) break;
            if (tag.SequenceEqual("BKHD"u8) && size >= 8)
            {
                bank.Version = BinaryPrimitives.ReadUInt32LittleEndian(data[body..]);
                bank.BankId = BinaryPrimitives.ReadUInt32LittleEndian(data[(body + 4)..]);
                if (bank.Version != SupportedVersion) return bank;
            }
            else if (tag.SequenceEqual("DIDX"u8))
            {
                for (var entry = body; entry + 12 <= body + size; entry += 12)
                    bank.EmbeddedMedia.Add(BinaryPrimitives.ReadUInt32LittleEndian(data[entry..]));
            }
            else if (tag.SequenceEqual("HIRC"u8) && bank.Version == SupportedVersion)
            {
                bank.ReadHierarchy(data.Slice(body, size), body, events, actions);
            }
            at = body + size;
        }

        foreach (var (eventId, actionIds) in events)
        {
            var targets = new List<uint>();
            foreach (var actionId in actionIds)
                if (actions.TryGetValue(actionId, out var action) && action.Type == PlayAction)
                    targets.Add(action.Target);
            bank.EventTargets[eventId] = targets;
        }
        return bank;
    }

    private void ReadHierarchy(
        ReadOnlySpan<byte> hirc,
        int hircOffset,
        Dictionary<uint, uint[]> events,
        Dictionary<uint, (ushort Type, uint Target)> actions)
    {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(hirc);
        var at = 4;
        for (var index = 0; index < count && at + 9 <= hirc.Length; index++)
        {
            var type = hirc[at];
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(hirc[(at + 1)..]);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(hirc[(at + 5)..]);
            if (length < 4 || at + 5 + length > hirc.Length) return;
            var body = hirc.Slice(at + 9, length - 4);
            var bodyOffset = hircOffset + at + 9;
            try
            {
                ReadObject(type, id, body, bodyOffset, events, actions);
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // A layout this reader does not know: skip the object, keep the rest of the bank.
            }
            at += 5 + length;
        }
    }

    private void ReadObject(
        byte type,
        uint id,
        ReadOnlySpan<byte> body,
        int bodyOffset,
        Dictionary<uint, uint[]> events,
        Dictionary<uint, (ushort Type, uint Target)> actions)
    {
        switch (type)
        {
            case 2: // Sound
            {
                var source = ReadSource(body, 0, bodyOffset, out var next);
                Sounds[id] = source;
                Parents[id] = ReadParent(body, next);
                break;
            }
            case 3: // Action
                actions[id] = (BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body[2..]));
                break;
            case 4: // Event
            {
                var actionCount = body[0];
                var ids = new uint[actionCount];
                for (var index = 0; index < actionCount; index++)
                    ids[index] = BinaryPrimitives.ReadUInt32LittleEndian(body[(1 + index * 4)..]);
                events[id] = ids;
                break;
            }
            case 5 or 6 or 7 or 9: // Random/sequence, switch, actor-mixer, layer containers
                Parents[id] = ReadParent(body, 0);
                break;
            case 10 or 12 or 13: // Music segment, music switch, music playlist (one flags byte first)
                Parents[id] = ReadParent(body, 1);
                break;
            case 11: // Music track
                ReadMusicTrack(id, body, bodyOffset);
                break;
        }
    }

    private void ReadMusicTrack(uint id, ReadOnlySpan<byte> body, int bodyOffset)
    {
        var at = 1;
        var sourceCount = BinaryPrimitives.ReadUInt32LittleEndian(body[at..]);
        at += 4;
        var sources = new List<WwiseBankSource>();
        for (var index = 0; index < sourceCount; index++)
            sources.Add(ReadSource(body, at, bodyOffset, out at));
        var durations = new Dictionary<uint, double>();
        var playlistCount = BinaryPrimitives.ReadUInt32LittleEndian(body[at..]);
        at += 4;
        for (var index = 0; index < playlistCount; index++)
        {
            // track id, source id, event id, play at, begin trim, end trim, source duration
            var sourceId = BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 4)..]);
            durations[sourceId] = BinaryPrimitives.ReadDoubleLittleEndian(body[(at + 36)..]);
            at += 44;
        }
        if (playlistCount > 0) at += 4; // sub-track count
        var automationCount = BinaryPrimitives.ReadUInt32LittleEndian(body[at..]);
        at += 4;
        for (var index = 0; index < automationCount; index++)
        {
            var points = BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 8)..]);
            at += 12 + (int)points * 12;
        }
        Parents[id] = ReadParent(body, at);
        MusicTracks[id] = sources
            .Select(source => new WwiseMusicSource(source, durations.GetValueOrDefault(source.SourceId)))
            .ToList();
    }

    private static WwiseBankSource ReadSource(ReadOnlySpan<byte> body, int at, int bodyOffset, out int next)
    {
        var pluginId = BinaryPrimitives.ReadUInt32LittleEndian(body[at..]);
        var source = new WwiseBankSource(
            BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 5)..]),
            pluginId,
            body[at + 4],
            BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 9)..]),
            bodyOffset + at,
            bodyOffset + at + 9);
        next = at + 14;
        // A source plug-in (type 2) carries its own parameter block after the source data.
        if ((pluginId & 0xF) == 2)
            next += 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(body[next..]);
        return source;
    }

    /// <summary>Reads DirectParentID from the node base parameters starting at <paramref name="at"/>.</summary>
    private static uint ReadParent(ReadOnlySpan<byte> body, int at)
    {
        var effectCount = body[at + 1];
        at += 2;
        if (effectCount > 0) at += 1 + effectCount * 7;
        at += 1; // override attachment parameters
        return BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 4)..]); // after the override bus id
    }
}
