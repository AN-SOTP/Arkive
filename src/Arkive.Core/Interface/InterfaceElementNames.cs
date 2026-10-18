using System.Text;
using System.Text.RegularExpressions;

namespace Arkive.Core.Interface;

/// <summary>
/// One part of an interface texture as the game's movie names it: "Castbar fill HP HUD (health bar)". The words in
/// brackets are what players search for that the game's identifiers spell differently.
/// </summary>
/// <param name="Region">Where the part sits on its texture, as fractions of its width and height; null for the whole picture.</param>
public sealed record InterfaceElement(string Name, string Identifier, InterfaceRegion? Region = null);

/// <summary>A rectangle of a texture, as fractions of its size so it fits whatever size the texture is stored at.</summary>
public sealed record InterfaceRegion(double Left, double Top, double Width, double Height);

/// <summary>The parts an interface movie draws from one texture, nearest names first.</summary>
public sealed record InterfaceTextureNames(string TextureName, IReadOnlyList<InterfaceElement> Elements);

/// <summary>
/// Names interface textures after what the game's own movies call the parts that draw them: the picture's export
/// name, the instance names scripts use ("playerHP") and the classes of the sprites around it ("QuickSlot_PlayerHP").
/// </summary>
public static partial class InterfaceElementNames
{
    private const int MaximumElements = 400;
    private const int NamesPerElement = 3;
    private const int MaximumDepth = 8;

    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["btn"] = "button", ["bg"] = "background", ["img"] = "image", ["ico"] = "icon", ["wnd"] = "window",
        ["txt"] = "text", ["tf"] = "text", ["hp"] = "HP", ["mp"] = "MP", ["exp"] = "EXP", ["ui"] = "UI",
        ["hud"] = "HUD", ["pvp"] = "PvP", ["npc"] = "NPC", ["quickslot"] = "quick slot", ["gage"] = "gauge",
        ["minimap"] = "minimap", ["tooltip"] = "tooltip",
    };

    // Words players search with that the game's identifiers spell differently.
    private static readonly (string Word, string Meaning)[] Meanings =
    [
        ("hp", "health"), ("mp", "mana"), ("exp", "experience"), ("gauge", "bar"), ("gage", "bar"),
        ("progress", "bar"), ("castbar", "bar"), ("bar", "bar"), ("btn", "button"),
    ];

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "mc", "movieclip", "clip", "sprite", "shape", "symbol", "instance", "nopack", "loc", "int", "img", "image", "i",
        "new", "copy", "the", "of",
    };

    /// <summary>Every texture the movie draws, with the names found for it.</summary>
    public static IReadOnlyList<InterfaceTextureNames> Find(GfxMovieStructure movie)
    {
        var parents = new Dictionary<int, List<(int Timeline, string? Instance)>>();
        foreach (var (timeline, placements) in movie.Timelines)
            foreach (var placement in placements)
            {
                if (!parents.TryGetValue(placement.CharacterId, out var list)) parents[placement.CharacterId] = list = [];
                if (!list.Contains((timeline, placement.InstanceName))) list.Add((timeline, placement.InstanceName));
            }
        var shapesByBitmap = new Dictionary<int, List<int>>();
        foreach (var (shape, bitmaps) in movie.ShapeBitmaps)
            foreach (var bitmap in bitmaps)
            {
                if (!shapesByBitmap.TryGetValue(bitmap, out var list)) shapesByBitmap[bitmap] = list = [];
                list.Add(shape);
            }

        var results = new List<InterfaceTextureNames>();
        foreach (var texture in movie.Images.GroupBy(image => image.TextureName, StringComparer.OrdinalIgnoreCase))
        {
            var elements = new List<InterfaceElement>();
            foreach (var image in texture) AddElements(elements, [image.ExportName], null);
            // Packed atlases are drawn through sub-images, one per element; a standalone ("nopack") picture shares
            // its number with an atlas, so sub-images never point at it.
            var atlases = texture.Where(image => !image.IsStandalone).Select(image => image.CharacterId).ToHashSet();
            var size = texture.FirstOrDefault(image => !image.IsStandalone);
            foreach (var (bitmap, part) in movie.SubImages.Where(pair => atlases.Contains(pair.Value.ImageId)).OrderBy(pair => pair.Key))
            {
                if (elements.Count >= MaximumElements) break;
                var chain = new List<string>();
                var frontier = shapesByBitmap.GetValueOrDefault(bitmap)?.Distinct().ToList() ?? [];
                var seen = frontier.ToHashSet();
                for (var depth = 0; depth < MaximumDepth && frontier.Count > 0 && chain.Count < NamesPerElement; depth++)
                {
                    var next = new List<int>();
                    foreach (var character in frontier)
                    {
                        foreach (var symbol in movie.SymbolNames.GetValueOrDefault(character) ?? []) Add(chain, symbol);
                        foreach (var (timeline, instance) in parents.GetValueOrDefault(character) ?? [])
                        {
                            Add(chain, instance);
                            if (timeline != 0 && seen.Add(timeline)) next.Add(timeline);
                        }
                    }
                    frontier = next;
                }
                var region = size is { Width: > 0, Height: > 0 } && part.Right > part.Left && part.Bottom > part.Top
                    ? new InterfaceRegion((double)part.Left / size.Width, (double)part.Top / size.Height,
                        (double)(part.Right - part.Left) / size.Width, (double)(part.Bottom - part.Top) / size.Height)
                    : null;
                AddElements(elements, chain.Take(NamesPerElement).ToArray(), region);
            }
            results.Add(new InterfaceTextureNames(texture.Key, elements.Take(MaximumElements).ToArray()));
        }
        return results;
    }

    /// <summary>"QuickSlot_PlayerHP" becomes "Quick slot player HP"; null when nothing readable is left.</summary>
    public static string? Humanize(string identifier)
    {
        var words = Split(identifier)
            .Where(word => !Noise.Contains(word) && !IsNumber(word))
            .Select(word => Words.TryGetValue(word, out var replacement) ? replacement : word.ToLowerInvariant())
            .ToList();
        // Identifiers often repeat their screen's name ("QuickSlot_QuickSlotHPProgress").
        for (var run = 1; run <= 3; run++)
            for (var index = words.Count - run; index >= run; index--)
                if (Enumerable.Range(0, run).All(offset =>
                        words[index + offset].Equals(words[index - run + offset], StringComparison.OrdinalIgnoreCase)))
                    words.RemoveRange(index, run);
        if (words.Count == 0 || words.All(word => word.Length < 2)) return null;
        var text = string.Join(' ', words);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>The searchable words a name implies but doesn't say, such as "health bar" for "hpGauge".</summary>
    public static IReadOnlyList<string> Meaning(IEnumerable<string> identifiers)
    {
        var words = identifiers.SelectMany(Split).Select(word => word.ToLowerInvariant()).ToHashSet();
        return Meanings.Where(item => words.Contains(item.Word) || words.Any(word => word.EndsWith(item.Word, StringComparison.Ordinal) && item.Word.Length > 3))
            .Select(item => item.Meaning)
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// "Quick slot" for the movie the author saved as "QuickSlot"; the export's own name ("quickslot") is lowercase,
    /// so its words can't be told apart.
    /// </summary>
    public static string ScreenName(GfxMovieStructure movie, string movieName)
    {
        var name = !string.IsNullOrWhiteSpace(movie.Name) && movie.Name.Replace("_", "").Length >= movieName.Replace("_", "").Length / 2
            ? movie.Name
            : movieName;
        return Humanize(name) ?? movieName;
    }

    /// <summary>
    /// One element per readable name in a chain (nearest the picture first). Each is told what it and the names nearer
    /// the picture mean, so "QuickSlot_PlayerHP" around "castbarFill_HP" is a "health bar"; names further out are
    /// shared with sibling parts and would give the MP bar the HP bar's words.
    /// </summary>
    private static void AddElements(List<InterfaceElement> elements, IReadOnlyList<string> chain, InterfaceRegion? region)
    {
        var identifiers = new List<string>();
        foreach (var identifier in chain) Add(identifiers, identifier);
        for (var index = 0; index < identifiers.Count; index++)
        {
            var identifier = identifiers[index];
            var meaning = Meaning(identifiers.Take(index + 1));
            var readable = Humanize(identifier)!;
            var words = Split(readable).Select(word => word.ToLowerInvariant()).ToHashSet();
            var missing = meaning.Where(word => !words.Contains(word)).ToArray();
            var name = missing.Length == 0 ? readable : $"{readable} ({string.Join(' ', missing)})";
            if (!elements.Any(element => element.Region == region && element.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                elements.Add(new InterfaceElement(name, identifier, region));
        }
    }

    private static void Add(List<string> names, string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.StartsWith("__", StringComparison.Ordinal) ||
            AutomaticName().IsMatch(identifier))
            return;
        var name = identifier.EndsWith(".nopack", StringComparison.OrdinalIgnoreCase) ? identifier[..^7] : identifier;
        // A dotted AS3 class name ("ui.quickslot.HpGauge") is named by its last part.
        name = name[(name.LastIndexOf('.') + 1)..];
        var readable = Humanize(name);
        if (readable is not null && !names.Any(existing => string.Equals(Humanize(existing), readable, StringComparison.OrdinalIgnoreCase)))
            names.Add(name);
    }

    private static IEnumerable<string> Split(string identifier)
    {
        var word = new StringBuilder();
        for (var index = 0; index < identifier.Length; index++)
        {
            var character = identifier[index];
            if (!char.IsLetterOrDigit(character))
            {
                if (word.Length > 0) yield return word.ToString();
                word.Clear();
                continue;
            }
            var previous = index > 0 ? identifier[index - 1] : '\0';
            var following = index + 1 < identifier.Length ? identifier[index + 1] : '\0';
            var boundary = word.Length > 0 &&
                           ((char.IsUpper(character) && (char.IsLower(previous) || char.IsLower(following) && char.IsUpper(previous))) ||
                            char.IsDigit(character) != char.IsDigit(previous));
            if (boundary)
            {
                yield return word.ToString();
                word.Clear();
            }
            word.Append(character);
        }
        if (word.Length > 0) yield return word.ToString();
    }

    private static bool IsNumber(string word) => word.All(char.IsDigit);

    [GeneratedRegex(@"^(instance|__id|_?mc|mc_?|shape|sprite|symbol|txt|text|tf)\d*$", RegexOptions.IgnoreCase)]
    private static partial Regex AutomaticName();
}
