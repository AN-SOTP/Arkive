using System.Text;
using Arkive.Core.Discovery;

namespace Arkive.Core.Audio;

/// <summary>Where one copy of a clip's audio lives: an entry in a .pck package, or a loose .wem file.</summary>
public sealed record SoundClipFile(string Path, WwisePackageEntry? Entry, string Language)
{
    public bool IsLoose => Entry is null;
}

/// <summary>A place in a sound bank that records a clip's codec and stored size.</summary>
public sealed record SoundBankField(
    string PackagePath,
    long PluginIdOffset,
    long InMemorySizeOffset,
    byte StreamType,
    uint PluginId,
    uint InMemorySize);

/// <summary>
/// A music segment that plays a clip and nothing else, in the bank at <paramref name="BankOffset"/> of
/// <paramref name="PackagePath"/>: where its length and exit cue are, and where its one track's timing for the clip
/// is (see <see cref="WwiseMusicClip"/>).
/// </summary>
public sealed record SoundMusicPlacement(
    string PackagePath, long BankOffset, uint BankSize, long SegmentDurationOffset, long ExitCueOffset, long ClipOffset);

/// <summary>
/// The segment a playlist plays once before looping the clip's segment. Fitting points its track at the clip as well
/// (<paramref name="SourceOffset"/> is its 14-byte source record, <paramref name="LoopSourceOffset"/> the looping
/// track's), so a replacement plays through and loops as one song.
/// </summary>
public sealed record SoundMusicIntro(
    uint SourceId, long SourceOffset, long LoopSourceOffset, SoundMusicPlacement Placement);

/// <summary>Every place the game times a music clip, when all of them can be fitted to a new length.</summary>
public sealed record SoundMusicFit(IReadOnlyList<SoundMusicPlacement> Placements, IReadOnlyList<SoundMusicIntro> Intros);

/// <summary>One piece of audio (a Wwise source): every copy of it and every bank field that describes it.</summary>
public sealed record SoundClip(
    uint SourceId,
    IReadOnlyList<SoundClipFile> Files,
    IReadOnlyList<SoundBankField> BankFields,
    double? MusicDurationMs)
{
    public bool IsMusic => MusicDurationMs is not null;

    /// <summary>
    /// Where the music system times this clip, or null when it is not music or some segment that plays it does more
    /// than play this one clip (several tracks or clips in one segment), so its timing cannot simply follow the clip.
    /// </summary>
    public SoundMusicFit? MusicFit { get; init; }
}

/// <summary>A named game sound (a Wwise event) and the clips it can play.</summary>
public sealed record SoundEvent(string Name, string Set, uint EventId, IReadOnlyList<uint> SourceIds);

public sealed record SoundCatalogProgress(int Completed, int Total, string Stage);

/// <summary>
/// Every sound Arkive can show: the named events (from the AkEvent objects in the game's packages), the clips each
/// one plays (from the sound banks in WwiseAudioPackage), and where each clip's audio is stored.
/// </summary>
public sealed class SoundCatalog
{
    private readonly Dictionary<uint, SoundClip> _clips;
    private readonly Dictionary<uint, List<uint>> _eventTargets;
    private readonly Dictionary<uint, List<uint>> _children;
    private readonly Dictionary<uint, uint> _soundSources;
    private readonly Dictionary<uint, List<uint>> _trackSources;
    private readonly List<(string Name, string Set, uint Id)> _events;

    private SoundCatalog(
        string audioRoot,
        Dictionary<uint, SoundClip> clips,
        Dictionary<uint, List<uint>> eventTargets,
        Dictionary<uint, List<uint>> children,
        Dictionary<uint, uint> soundSources,
        Dictionary<uint, List<uint>> trackSources,
        List<(string Name, string Set, uint Id)> events,
        int bankCount,
        int unsupportedBankCount)
    {
        AudioRoot = audioRoot;
        _clips = clips;
        _eventTargets = eventTargets;
        _children = children;
        _soundSources = soundSources;
        _trackSources = trackSources;
        _events = events;
        BankCount = bankCount;
        UnsupportedBankCount = unsupportedBankCount;
    }

    public string AudioRoot { get; }
    public int BankCount { get; }
    public int UnsupportedBankCount { get; }
    public int EventCount => _events.Count;
    public int ClipCount => _clips.Count;

    /// <summary>WwiseAudioPackage beside the indexed Packages folder.</summary>
    public static string AudioRootFor(string packagesFolder) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(packagesFolder)))!,
            "WwiseAudioPackage");

    /// <summary>Wwise's id for a name: 32-bit FNV-1 of the lower-case name.</summary>
    public static uint WwiseId(string name)
    {
        var hash = 2166136261u;
        foreach (var value in Encoding.UTF8.GetBytes(name.ToLowerInvariant()))
        {
            hash *= 16777619u;
            hash ^= value;
        }
        return hash;
    }

    public SoundClip? Clip(uint sourceId) => _clips.GetValueOrDefault(sourceId);

    /// <summary>Named sounds matching <paramref name="query"/> (every word must appear), with their clips.</summary>
    public IReadOnlyList<SoundEvent> Search(string query, int limit)
    {
        var expanded = ExpandWesternNames(query);
        var results = new List<SoundEvent>();
        foreach (var (name, set, id) in _events)
        {
            if (!AssetSearch.Matches(expanded, name, set)) continue;
            var sources = SourcesFor(id);
            if (sources.Count == 0) continue;
            results.Add(new SoundEvent(name, set, id, sources));
            if (results.Count >= limit) break;
        }
        return results;
    }

    /// <summary>The clips an event can play: every sound and music track under its play actions' targets.</summary>
    public IReadOnlyList<uint> SourcesFor(uint eventId)
    {
        if (!_eventTargets.TryGetValue(eventId, out var targets)) return [];
        var sources = new List<uint>();
        var seen = new HashSet<uint>();
        var pending = new Stack<uint>(targets);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!seen.Add(node)) continue;
            if (_soundSources.TryGetValue(node, out var source) && _clips.ContainsKey(source)) sources.Add(source);
            if (_trackSources.TryGetValue(node, out var trackSources))
                sources.AddRange(trackSources.Where(_clips.ContainsKey));
            if (_children.TryGetValue(node, out var children))
                foreach (var child in children) pending.Push(child);
        }
        return sources.Distinct().ToArray();
    }

    /// <summary>The clip's current audio as the game would load it (after any replacement), up to <paramref name="maxBytes"/>.</summary>
    public static byte[] ReadAudio(SoundClip clip, int maxBytes = int.MaxValue)
    {
        var file = clip.Files.FirstOrDefault() ?? throw new InvalidDataException($"Clip {clip.SourceId} has no audio file.");
        using var audio = WwiseAudioFile.Open(file.Path);
        if (file.Entry is null)
            return audio.Read(0, (int)Math.Min(audio.ContentLength, maxBytes));
        var entry = ReadCurrentEntry(audio, file.Entry);
        return audio.Read(entry.DataOffset, (int)Math.Min(entry.Size, (uint)maxBytes));
    }

    /// <summary>The table entry as the package holds it now (a replacement repoints it).</summary>
    public static WwisePackageEntry ReadCurrentEntry(WwiseAudioFile package, WwisePackageEntry entry)
    {
        var current = WwisePackageEntry.Decode(
            entry.Section, package.Read(entry.EntryOffset, entry.EntryLength), entry.EntryOffset, entry.WideId);
        if (current.Id != entry.Id)
            throw new InvalidDataException("The game's audio package changed since Arkive read it. Search again to reload it.");
        return current;
    }

    public static WemFormat? ReadFormat(SoundClip clip) =>
        WemAudio.ReadFormat(ReadAudio(clip, WemAudio.HeaderProbeLength));

    /// <summary>Reads every package and bank under <paramref name="audioRoot"/> and names the events.</summary>
    public static SoundCatalog Build(
        string audioRoot,
        IReadOnlyList<(string Name, string Set)> eventNames,
        IProgress<SoundCatalogProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(audioRoot))
            throw new DirectoryNotFoundException($"The game's audio folder was not found: {audioRoot}");
        var packagePaths = Directory.EnumerateFiles(audioRoot, "*.pck", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var files = new Dictionary<uint, List<SoundClipFile>>();
        var fields = new Dictionary<uint, List<SoundBankField>>();
        var musicDurations = new Dictionary<uint, double>();
        var placements = new Dictionary<uint, List<SoundMusicPlacement>>();
        var intros = new Dictionary<uint, List<SoundMusicIntro>>();
        var unfitted = new HashSet<uint>();
        var eventTargets = new Dictionary<uint, List<uint>>();
        var children = new Dictionary<uint, List<uint>>();
        var soundSources = new Dictionary<uint, uint>();
        var trackSources = new Dictionary<uint, List<uint>>();
        var parentsSeen = new HashSet<uint>();
        var bankCount = 0;
        var unsupported = 0;

        for (var index = 0; index < packagePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = packagePaths[index];
            progress?.Report(new SoundCatalogProgress(index, packagePaths.Length, "Reading the game's sound banks"));
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
            foreach (var entry in package.Entries)
            {
                var language = package.Languages.GetValueOrDefault(entry.LanguageId, "sfx");
                if (entry.Section != WwisePackageSection.SoundBank)
                {
                    if (entry.Id <= uint.MaxValue)
                        Add(files, (uint)entry.Id, new SoundClipFile(path, entry, language));
                    continue;
                }
                if (entry.DataOffset + entry.Size > package.ContentLength) continue;
                bankCount++;
                var bank = WwiseBank.Parse(audio.Read(entry.DataOffset, (int)entry.Size));
                if (bank.Version != WwiseBank.SupportedVersion)
                {
                    unsupported++;
                    continue;
                }
                foreach (var (eventId, targets) in bank.EventTargets)
                {
                    if (!eventTargets.TryGetValue(eventId, out var known)) eventTargets[eventId] = known = [];
                    known.AddRange(targets.Where(target => !known.Contains(target)));
                }
                foreach (var (node, parent) in bank.Parents)
                    if (parentsSeen.Add(node)) Add(children, parent, node);
                foreach (var (soundId, source) in bank.Sounds)
                {
                    if (!source.IsCodec || bank.EmbeddedMedia.Contains(source.SourceId)) continue;
                    soundSources.TryAdd(soundId, source.SourceId);
                    Add(fields, source.SourceId, Field(path, entry, source));
                }
                foreach (var (trackId, sources) in bank.MusicTracks)
                {
                    foreach (var music in sources.Where(music => music.Source.IsCodec))
                    {
                        if (!trackSources.TryGetValue(trackId, out var list)) trackSources[trackId] = list = [];
                        if (!list.Contains(music.Source.SourceId)) list.Add(music.Source.SourceId);
                        Add(fields, music.Source.SourceId, Field(path, entry, music.Source));
                        if (music.DurationMs > 0) musicDurations.TryAdd(music.Source.SourceId, music.DurationMs);
                    }
                }
                ReadMusicTiming(path, entry.DataOffset, entry.Size, bank, placements, intros, unfitted);
            }
        }

        // Loose files are music the packages do not hold, named by the client's scramble of the source id.
        // Voiced ones have a copy in each language's folder (English(US), Korean...).
        var looseNames = Directory.EnumerateFiles(audioRoot, "*.wem", SearchOption.AllDirectories)
            .ToLookup(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
        foreach (var sourceId in fields.Keys.Concat(files.Keys).Distinct().ToArray())
            foreach (var loose in looseNames[LostArkAudioFileNames.ForSource(sourceId)])
            {
                var folder = Path.GetRelativePath(audioRoot, Path.GetDirectoryName(loose)!);
                Add(files, sourceId, new SoundClipFile(loose, null, folder == "." ? "sfx" : folder));
            }

        var clips = new Dictionary<uint, SoundClip>();
        foreach (var (sourceId, clipFiles) in files)
        {
            clips[sourceId] = new SoundClip(
                sourceId,
                clipFiles,
                fields.GetValueOrDefault(sourceId) is { } clipFields ? clipFields.Distinct().ToArray() : [],
                musicDurations.TryGetValue(sourceId, out var duration) ? duration : null)
            {
                MusicFit = placements.TryGetValue(sourceId, out var placed) && !unfitted.Contains(sourceId)
                    ? new SoundMusicFit(placed, intros.GetValueOrDefault(sourceId) ?? [])
                    : null,
            };
        }

        var events = eventNames
            .Select(item => (item.Name, item.Set, Id: WwiseId(item.Name)))
            .Where(item => eventTargets.ContainsKey(item.Id))
            .DistinctBy(item => item.Id)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        progress?.Report(new SoundCatalogProgress(packagePaths.Length, packagePaths.Length, "Sound banks read"));
        return new SoundCatalog(
            audioRoot, clips, eventTargets, children, soundSources, trackSources, events, bankCount, unsupported);
    }

    /// <summary>
    /// Records, for every music clip in <paramref name="bank"/>, the segments that play only that clip (their timing
    /// can follow a replacement's length) and marks clips some segment plays alongside other audio as not fittable.
    /// </summary>
    private static void ReadMusicTiming(
        string path,
        long bankOffset,
        uint bankSize,
        WwiseBank bank,
        Dictionary<uint, List<SoundMusicPlacement>> placements,
        Dictionary<uint, List<SoundMusicIntro>> intros,
        HashSet<uint> unfitted)
    {
        var sole = new Dictionary<uint, (uint SourceId, WwiseMusicClip Clip, WwiseBankSource Source, SoundMusicPlacement Placement)>();
        var segmented = new HashSet<uint>();
        foreach (var (segmentId, segment) in bank.MusicSegments)
        {
            var clips = segment.TrackIds.SelectMany(track => bank.MusicClips.GetValueOrDefault(track) ?? []).ToArray();
            var sources = segment.TrackIds.SelectMany(track => bank.MusicTracks[track]).ToArray();
            segmented.UnionWith(segment.TrackIds);
            if (segment.TrackIds.Count == 1 && clips.Length == 1 && sources.Length == 1 &&
                sources[0].Source.SourceId == clips[0].SourceId && sources[0].Source.IsCodec)
            {
                var placement = new SoundMusicPlacement(path, bankOffset, bankSize, bankOffset + segment.DurationOffset,
                    bankOffset + segment.ExitCueOffset, bankOffset + clips[0].Offset);
                sole[segmentId] = (clips[0].SourceId, clips[0], sources[0].Source, placement);
                Add(placements, clips[0].SourceId, placement);
            }
            else
            {
                unfitted.UnionWith(clips.Select(clip => clip.SourceId));
            }
        }
        // A track this reader found no segment for may be timed by one it could not read.
        foreach (var (track, clips) in bank.MusicClips)
            if (!segmented.Contains(track)) unfitted.UnionWith(clips.Select(clip => clip.SourceId));

        foreach (var items in bank.MusicPlaylists.Values)
        {
            if (items is not [{ Loop: 1 } intro, { Loop: 0 } loop] ||
                !sole.TryGetValue(intro.SegmentId, out var first) || !sole.TryGetValue(loop.SegmentId, out var looping) ||
                first.SourceId == looping.SourceId)
                continue;
            Add(intros, looping.SourceId, new SoundMusicIntro(first.SourceId,
                bankOffset + first.Source.PluginIdOffset, bankOffset + looping.Source.PluginIdOffset,
                first.Placement));
        }
    }

    /// <summary>
    /// The clip's music timing as the banks that hold <paramref name="known"/> say now. A replacement that fitted the
    /// clip points its intro's track at it, so the catalog, if read meanwhile, sees no intro; once that replacement
    /// is restored, reading the banks again finds it.
    /// </summary>
    public static SoundMusicFit? ReadMusicFit(uint sourceId, IEnumerable<SoundMusicPlacement> known)
    {
        var placements = new Dictionary<uint, List<SoundMusicPlacement>>();
        var intros = new Dictionary<uint, List<SoundMusicIntro>>();
        var unfitted = new HashSet<uint>();
        foreach (var bankAt in known.DistinctBy(placement => (placement.PackagePath.ToUpperInvariant(), placement.BankOffset)))
        {
            using var audio = WwiseAudioFile.Open(bankAt.PackagePath);
            var bank = WwiseBank.Parse(audio.Read(bankAt.BankOffset, (int)bankAt.BankSize));
            ReadMusicTiming(bankAt.PackagePath, bankAt.BankOffset, bankAt.BankSize, bank, placements, intros, unfitted);
        }
        return placements.TryGetValue(sourceId, out var placed) && !unfitted.Contains(sourceId)
            ? new SoundMusicFit(placed, intros.GetValueOrDefault(sourceId) ?? [])
            : null;
    }

    private static SoundBankField Field(string path, WwisePackageEntry bank, WwiseBankSource source) => new(
        path,
        bank.DataOffset + source.PluginIdOffset,
        bank.DataOffset + source.InMemorySizeOffset,
        source.StreamType,
        source.PluginId,
        source.InMemorySize);

    private static void Add<T>(Dictionary<uint, List<T>> map, uint key, T value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }

    // Players know the raid bosses by their Western names; the sound names use the Korean ones.
    private static readonly (string Western, string Internal)[] WesternNames =
    [
        ("kazeros", "kazeroth"),
        ("thaemine", "kamen"),
        ("akkan", "illiakan"),
        ("brelshaza", "abrelshud"),
        ("vykas", "biackiss"),
    ];

    private static string ExpandWesternNames(string query)
    {
        foreach (var (western, internalName) in WesternNames)
            query = query.Replace(western, internalName, StringComparison.OrdinalIgnoreCase);
        return query;
    }
}
