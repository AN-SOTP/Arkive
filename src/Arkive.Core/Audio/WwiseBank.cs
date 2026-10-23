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
/// Where in its segment a music track plays a source (milliseconds). <paramref name="Offset"/> is the 44-byte record
/// in the bank: track id, source id, event id, then the four timing values as doubles.
/// </summary>
public sealed record WwiseMusicClip(
    uint SourceId, double PlayAt, double BeginTrim, double EndTrim, double SourceDuration, int Offset)
{
    public const int SourceIdField = 4, PlayAtField = 12, BeginTrimField = 20, EndTrimField = 28, SourceDurationField = 36;
}

/// <summary>
/// A music segment: the tracks it plays together, its length, and its exit cue, where the playlist moves on to the
/// next segment or starts this one again. Offsets are of the doubles in the bank.
/// </summary>
public sealed record WwiseMusicSegment(
    IReadOnlyList<uint> TrackIds, double Duration, int DurationOffset, double ExitCue, int ExitCueOffset);

/// <summary>A segment in a music playlist and how many times it plays (0: until the music changes).</summary>
public sealed record WwiseMusicPlaylistItem(uint SegmentId, short Loop);

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

    /// <summary>Music track id to where it plays each of its sources.</summary>
    public Dictionary<uint, List<WwiseMusicClip>> MusicClips { get; } = [];

    /// <summary>Music segments whose length and cues Arkive can read (all of Lost Ark's but a handful).</summary>
    public Dictionary<uint, WwiseMusicSegment> MusicSegments { get; } = [];

    /// <summary>Music playlist id to its segments in playing order (groups left out).</summary>
    public Dictionary<uint, List<WwiseMusicPlaylistItem>> MusicPlaylists { get; } = [];

    // The marker id Wwise gives every music segment's exit cue.
    private const uint ExitCueId = 1539036744;

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

        foreach (var (id, body, bodyOffset) in bank._segmentBodies)
            if (bank.ReadMusicSegment(body, bodyOffset) is { } segment) bank.MusicSegments[id] = segment;
        var segmentIds = bank._segmentBodies.Select(item => item.Id).ToHashSet();
        foreach (var (id, body, _) in bank._playlistBodies)
            if (ReadPlaylistItems(body, segmentIds) is { } items) bank.MusicPlaylists[id] = items;
        bank._segmentBodies.Clear();
        bank._playlistBodies.Clear();

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
                // Segments and playlists are read by the ids of their tracks and segments once every object is known.
                if (type == 10) _segmentBodies.Add((id, body.ToArray(), bodyOffset));
                if (type == 13) _playlistBodies.Add((id, body.ToArray(), bodyOffset));
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
        var clips = new List<WwiseMusicClip>();
        var playlistCount = BinaryPrimitives.ReadUInt32LittleEndian(body[at..]);
        at += 4;
        for (var index = 0; index < playlistCount; index++)
        {
            // track id, source id, event id, play at, begin trim, end trim, source duration
            var clip = new WwiseMusicClip(
                BinaryPrimitives.ReadUInt32LittleEndian(body[(at + WwiseMusicClip.SourceIdField)..]),
                BinaryPrimitives.ReadDoubleLittleEndian(body[(at + WwiseMusicClip.PlayAtField)..]),
                BinaryPrimitives.ReadDoubleLittleEndian(body[(at + WwiseMusicClip.BeginTrimField)..]),
                BinaryPrimitives.ReadDoubleLittleEndian(body[(at + WwiseMusicClip.EndTrimField)..]),
                BinaryPrimitives.ReadDoubleLittleEndian(body[(at + WwiseMusicClip.SourceDurationField)..]),
                bodyOffset + at);
            clips.Add(clip);
            durations[clip.SourceId] = clip.SourceDuration;
            at += 44;
        }
        MusicClips[id] = clips;
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

    private readonly List<(uint Id, byte[] Body, int BodyOffset)> _segmentBodies = [], _playlistBodies = [];

    /// <summary>
    /// A segment's tracks, length and exit cue. The node parameters before them vary in length, so the reader finds
    /// the child list (a count, then that many track ids of this bank) and checks that everything after it, up to the
    /// end of the object, is the segment layout: meter, stingers, length and the cue markers.
    /// </summary>
    private WwiseMusicSegment? ReadMusicSegment(byte[] body, int bodyOffset)
    {
        for (var at = 1; at + 8 <= body.Length; at++)
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at));
            if (count is 0 or > 64 || at + 4 + count * 4 > body.Length) continue;
            var tracks = new uint[count];
            for (var index = 0; index < count; index++)
                tracks[index] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 4 + index * 4));
            if (!tracks.All(MusicTracks.ContainsKey)) continue;
            // Meter: grid period and offset (doubles), tempo (float), time signature (two bytes), then a flags byte
            // and the stingers (24 bytes each).
            var next = at + 4 + (int)count * 4 + 8 + 8 + 4 + 2 + 1;
            if (next + 4 > body.Length) return null;
            var stingers = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(next));
            if (stingers > 64) return null;
            next += 4 + (int)stingers * 24;
            if (next + 12 > body.Length) return null;
            var durationOffset = next;
            var duration = BinaryPrimitives.ReadDoubleLittleEndian(body.AsSpan(next));
            var markers = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(next + 8));
            next += 12;
            int? exitOffset = null;
            for (var index = 0; index < markers; index++)
            {
                if (next + 16 > body.Length) return null;
                var markerId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(next));
                var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(next + 12));
                if (nameLength > 256) return null;
                if (markerId == ExitCueId) exitOffset = next + 4;
                next += 16 + (int)nameLength;
            }
            if (next != body.Length || exitOffset is not { } exit || !double.IsFinite(duration) || duration <= 0) return null;
            var exitCue = BinaryPrimitives.ReadDoubleLittleEndian(body.AsSpan(exit));
            return new WwiseMusicSegment(tracks, duration, bodyOffset + durationOffset, exitCue, bodyOffset + exit);
        }
        return null;
    }

    /// <summary>
    /// A playlist's segments in order. The items end the object: a count, then 30 bytes each (segment id, item id,
    /// child count, play type, loop count and its range, weight, avoid-repeat count, two flags). Group items (segment
    /// 0) are left out. A count is taken only when every item names a segment of this bank (or is a group) and has an
    /// item id.
    /// </summary>
    private static List<WwiseMusicPlaylistItem>? ReadPlaylistItems(byte[] body, HashSet<uint> segmentIds)
    {
        for (var count = 1; count <= 256 && 4 + count * 30 <= body.Length; count++)
        {
            var start = body.Length - count * 30 - 4;
            if (BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(start)) != count) continue;
            var items = new List<WwiseMusicPlaylistItem>();
            var valid = true;
            for (var index = 0; index < count && valid; index++)
            {
                var item = body.AsSpan(start + 4 + index * 30);
                var segment = BinaryPrimitives.ReadUInt32LittleEndian(item);
                valid = (segment == 0 || segmentIds.Contains(segment)) && BinaryPrimitives.ReadUInt32LittleEndian(item[4..]) != 0;
                if (segment != 0) items.Add(new WwiseMusicPlaylistItem(segment, BinaryPrimitives.ReadInt16LittleEndian(item[16..])));
            }
            if (valid && items.Count > 0) return items;
        }
        return null;
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
