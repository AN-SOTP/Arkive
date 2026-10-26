using System.IO.Compression;
using System.Text.Json;
using Arkive.Core.Interface;
using Arkive.Core.Interface.Abc;

namespace Arkive.Core.Tweaks;

/// <summary>Arkive's compiled tweak code (eng/tweaks, built with Haxe), with the settings written into it.</summary>
public static class TweakCode
{
    public const string ConfigPlaceholder = "__ARKIVE_TWEAKS_CONFIG__";
    public const string AbcName = "arkive/tweaks";
    private const string ResourceName = "Arkive.Core.Tweaks.arkive-tweaks.swf";

    /// <summary>The compiled SWF shipped inside Arkive.Core.</summary>
    public static byte[] LoadSwf()
    {
        using var stream = typeof(TweakCode).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Arkive's tweak code is missing from this build.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// The code blocks to add to a movie, one per class as the Haxe compiler's SWC holds them, with the chosen tweaks
    /// and options (JSON) written into Options, and every typed null made untyped (see AbcInstructions.UntypeNulls).
    /// </summary>
    public static IReadOnlyList<GfxTag> ForSettings(byte[] swf, TweakSettings settings)
    {
        var blocks = ReadSwfBlocks(swf);
        var configured = 0;
        var tags = new List<GfxTag>();
        foreach (var (name, data) in blocks)
        {
            var abc = AbcFile.Read(data);
            var index = abc.Pool.Strings.IndexOf(ConfigPlaceholder);
            if (index >= 0)
            {
                abc.Pool.Strings[index] = settings.ToGameJson();
                configured++;
            }
            foreach (var body in abc.Bodies) AbcInstructions.UntypeNulls(body);
            tags.Add(GfxTag.NewDoAbc(AbcName + "/" + name, abc.Write()));
        }
        if (configured != 1) throw new InvalidDataException("Arkive's tweak code has no single place for its settings.");
        return tags;
    }

    /// <summary>The code blocks of a SWF the Haxe compiler wrote ("FWS", or "CWS" when zlib-compressed), by name.</summary>
    public static IReadOnlyList<(string Name, byte[] Abc)> ReadSwfBlocks(byte[] swf)
    {
        if (swf.Length < 8 || swf[1] != 'W' || swf[2] != 'S' || swf[0] is not ((byte)'F' or (byte)'C'))
            throw new InvalidDataException("The tweak code is not a SWF file.");
        byte[] movie;
        if (swf[0] == 'C')
        {
            var length = BitConverter.ToInt32(swf, 4);
            movie = new byte[length];
            swf.AsSpan(0, 8).CopyTo(movie);
            using var input = new ZLibStream(new MemoryStream(swf, 8, swf.Length - 8), CompressionMode.Decompress);
            input.ReadExactly(movie, 8, length - 8);
        }
        else
        {
            movie = (byte[])swf.Clone();
        }
        movie[0] = (byte)'G';
        movie[1] = (byte)'F';
        movie[2] = (byte)'X';
        var blocks = GfxMovieTags.Read(movie).Tags.Where(tag => tag.Code == GfxTag.DoAbc)
            .Select(tag => (tag.AbcName, tag.AbcData.ToArray())).ToArray();
        if (blocks.Length == 0) throw new InvalidDataException("The tweak code holds no code blocks.");
        return blocks;
    }
}

public sealed record TweakMovieResult(byte[] Movie, IReadOnlyList<string> MissingHooks);

/// <summary>Adds the tweak code to one game movie and routes the hooked methods through it.</summary>
public static class TweakMovieBuilder
{
    public static TweakMovieResult Build(byte[] gameMovie, IEnumerable<Interface.Abc.AbcHook> hooks, IReadOnlyList<GfxTag> tweakCode)
    {
        var tags = GfxMovieTags.Read(gameMovie);
        if (tags.Tags.Any(tag => tag.Code == GfxTag.DoAbc && tag.AbcName.StartsWith(TweakCode.AbcName + "/", StringComparison.Ordinal)))
            throw new InvalidDataException("This movie already holds Arkive's tweak code; build from the game's own movie.");
        var main = tags.MainAbc;
        var abc = AbcFile.Read(main.AbcData);
        var missing = AbcHookInjector.Inject(abc, hooks.DistinctBy(hook => hook.Id));
        tags.Tags[tags.Tags.IndexOf(main)] = main.WithAbc(abc.Write());
        foreach (var block in tweakCode) tags.AppendAbc(block);
        return new TweakMovieResult(tags.Write(), missing);
    }
}

/// <summary>The tweaks a player turned on and their options.</summary>
public sealed class TweakSettings
{
    public Dictionary<string, Dictionary<string, JsonElement>> Enabled { get; set; } = [];

    /// <summary>{"tweaks":{"lobbycp":{},"extracurrency":{"showLeapstones":true}}}, the shape the tweak code reads.</summary>
    public string ToGameJson() => JsonSerializer.Serialize(new { tweaks = Enabled });
}
