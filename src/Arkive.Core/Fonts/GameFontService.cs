using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Arkive.Core.Mods;
using Arkive.Core.Skills;

namespace Arkive.Core.Fonts;

/// <summary>The kinds of text the game gives their own font.</summary>
public enum GameFontSlot
{
    /// <summary>Chat, quests, menus and most reading text.</summary>
    BodyText,
    /// <summary>Titles, headings and names.</summary>
    Titles,
    /// <summary>The numbers that pop up over enemies; they use the titles font unless given their own.</summary>
    DamageNumbers,
}

/// <summary>A font the person chose, and Arkive's own copy of it (so it can be put back after a game update).</summary>
public sealed record GameFontChoice(GameFontSlot Slot, string Name, string StoredPath, string Sha256);

/// <summary>What Arkive changed: font.lpk now holds <paramref name="Fonts"/>; the game's copy is kept at <paramref name="BackupPath"/>.</summary>
public sealed record GameFontState(
    int Version, string ArchivePath, string OriginalSha256, string AppliedSha256, string BackupPath,
    IReadOnlyList<GameFontChoice> Fonts, DateTimeOffset AppliedAt);

/// <summary>
/// The game's fonts, in EFGame\font.lpk. Each language folder in it has a FontMap.xml that names the font file each
/// kind of text uses (on the Western languages: ExuberanceF for body text, MonarchaF for titles and damage numbers).
/// Arkive rebuilds font.lpk from the game's own copy with the chosen fonts stored under those files' names, so the
/// game loads them as its own; damage numbers given a font of their own get a new file and their FontMap entry points
/// at it. The game's copy is kept, so going back puts it back exactly, and applying again never stacks changes.
/// </summary>
public sealed partial class GameFontService
{
    public const string ArchiveName = "font.lpk";

    /// <summary>The language folders that share the Western client's fonts.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["English", "French", "German", "Spanish"];

    private const int StateVersion = 1;
    private const long MaximumFontSize = 64L * 1024 * 1024;
    private const string DamageKey = "$eventDamageFont";
    private const string DamageFile = "ArkiveDamageNumbers.ttf";
    private static readonly string[] BodyKeys = ["$YG760", "$YG760_12pt", "$YG330"];
    private static readonly string[] TitleKeys = ["$YoonGasiIIM", "$YoonGasiIIM_Small2pt", "$ChronologyFont"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _statePath;
    private readonly string _backupDirectory;
    private readonly string _libraryDirectory;
    private readonly string _previewDirectory;
    private readonly IModDeploymentProcessGuard _processGuard;

    /// <param name="efGameDirectory">The game's EFGame folder (the one holding font.lpk).</param>
    /// <param name="dataRoot">Arkive's data folder, for the backup, the chosen fonts and the record of what was changed.</param>
    public GameFontService(string efGameDirectory, string dataRoot, IModDeploymentProcessGuard processGuard)
    {
        ArchivePath = Path.Combine(Path.GetFullPath(efGameDirectory), ArchiveName);
        _statePath = Path.Combine(dataRoot, "fonts", "state.json");
        _libraryDirectory = Path.Combine(dataRoot, "fonts", "chosen");
        _previewDirectory = Path.Combine(dataRoot, "cache", "fonts");
        _backupDirectory = Path.Combine(dataRoot, "backups", "fonts");
        _processGuard = processGuard;
    }

    /// <summary>The game's font.lpk.</summary>
    public string ArchivePath { get; }

    public bool GameHasFonts => File.Exists(ArchivePath);

    /// <summary>Why <paramref name="path"/> cannot be used as a game font, or null when it can.</summary>
    public static string? Problem(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "The font file was not found.";
            if (info.Length > MaximumFontSize) return "The font file is too large (over 64 MB).";
            Span<byte> head = stackalloc byte[4];
            using (var stream = info.OpenRead())
                if (stream.Read(head) < 4) return "This is not a TrueType font file.";
            if (head.SequenceEqual(new byte[] { 0, 1, 0, 0 }) || head.SequenceEqual("true"u8)) return null;
            if (head.SequenceEqual("OTTO"u8)) return "This font has PostScript outlines (.otf). The game reads TrueType fonts; choose a .ttf font.";
            if (head.SequenceEqual("ttcf"u8)) return "This is a font collection (.ttc). Choose a single .ttf font.";
            return "This is not a TrueType font file.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "The font file could not be read: " + exception.Message;
        }
    }

    public GameFontState? LoadState()
    {
        if (!File.Exists(_statePath)) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(_statePath));
            return node?["Version"]?.GetValue<int>() == StateVersion ? node.Deserialize<GameFontState>(JsonOptions) : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the game has replaced Arkive's font.lpk since it was made (an update or a file check): the game's own
    /// fonts are back, and applying again puts the chosen ones in the new file.
    /// </summary>
    public async Task<bool> GameUpdatedAsync(CancellationToken cancellationToken = default) =>
        LoadState() is { } state &&
        (!File.Exists(ArchivePath) || !(await HashAsync(ArchivePath, cancellationToken).ConfigureAwait(false))
            .Equals(state.AppliedSha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The game's own font files for <paramref name="language"/>, saved where they can be previewed: one per slot, the
    /// damage numbers' being the titles' unless the game gives them another. Read from the game's copy of font.lpk.
    /// </summary>
    public async Task<IReadOnlyDictionary<GameFontSlot, string>> GameFontsAsync(string language = "English", CancellationToken cancellationToken = default)
    {
        var source = await CleanSourceAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = File.OpenRead(source);
        var entries = LostArkDataArchive.ReadEntries(stream, cancellationToken);
        var map = ReadFontMap(stream, entries, language) ?? throw new InvalidDataException($"font.lpk has no {language} fonts.");
        Directory.CreateDirectory(_previewDirectory);
        var fonts = new Dictionary<GameFontSlot, string>();
        foreach (var (slot, key) in new[] { (GameFontSlot.BodyText, BodyKeys[0]), (GameFontSlot.Titles, TitleKeys[0]), (GameFontSlot.DamageNumbers, DamageKey) })
        {
            if (!map.TryGetValue(key, out var file) || Find(entries, language, file) is not { } entry) continue;
            var saved = Path.Combine(_previewDirectory, $"{language}-{entry.Size}-{file}");
            if (!File.Exists(saved))
            {
                var temporary = $"{saved}.{Guid.NewGuid():N}.tmp";
                await File.WriteAllBytesAsync(temporary, LostArkDataArchive.Read(stream, entry), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, saved, overwrite: true);
            }
            fonts[slot] = saved;
        }
        return fonts;
    }

    /// <summary>
    /// Puts <paramref name="fonts"/> (a .ttf file for each slot to change) in the game. Slots left out keep the game's
    /// own font; with none, the game's own fonts are put back.
    /// </summary>
    public async Task ApplyAsync(IReadOnlyDictionary<GameFontSlot, string> fonts, CancellationToken cancellationToken = default)
    {
        RefuseWhileRunning();
        if (fonts.Count == 0)
        {
            await RestoreAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        foreach (var (slot, path) in fonts)
            if (Problem(path) is { } problem) throw new InvalidDataException($"{Describe(slot)}: {problem}");
        if (!GameHasFonts) throw new FileNotFoundException("The game's font.lpk was not found in its EFGame folder.");

        // Arkive's own copies first, so the choice can be applied again after the game replaces font.lpk.
        Directory.CreateDirectory(_libraryDirectory);
        var chosen = new List<GameFontChoice>();
        var contents = new Dictionary<GameFontSlot, byte[]>();
        foreach (var (slot, path) in fonts.OrderBy(font => font.Key))
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            var stored = Path.Combine(_libraryDirectory, sha[..16] + ".ttf");
            if (!Path.GetFullPath(path).Equals(Path.GetFullPath(stored), StringComparison.OrdinalIgnoreCase))
                await File.WriteAllBytesAsync(stored, bytes, cancellationToken).ConfigureAwait(false);
            // Applying again reads Arkive's copies, which keep the name of the file the person chose.
            var name = LoadState()?.Fonts.FirstOrDefault(font => font.Sha256 == sha)?.Name ?? Path.GetFileName(path);
            chosen.Add(new GameFontChoice(slot, name, stored, sha));
            contents[slot] = bytes;
        }

        var source = await CleanSourceAsync(cancellationToken).ConfigureAwait(false);
        var changes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        await using (var stream = File.OpenRead(source))
        {
            var entries = LostArkDataArchive.ReadEntries(stream, cancellationToken);
            if (ForeignChange(entries) is { } foreign) throw new InvalidDataException(foreign);
            var languages = 0;
            foreach (var language in Languages)
            {
                var mapEntry = Find(entries, language, "FontMap.xml");
                if (mapEntry is null) continue;
                var mapBytes = LostArkDataArchive.Read(stream, mapEntry);
                var map = ParseFontMap(mapBytes);
                var body = Files(map, BodyKeys);
                var titles = Files(map, TitleKeys);
                languages++;
                if (contents.TryGetValue(GameFontSlot.BodyText, out var bodyFont)) Replace(language, body, titles, bodyFont, "body text", "titles");
                if (contents.TryGetValue(GameFontSlot.Titles, out var titleFont)) Replace(language, titles, body, titleFont, "titles", "body text");
                if (contents.TryGetValue(GameFontSlot.DamageNumbers, out var damageFont))
                {
                    changes[$"/Binaries/Fonts/{language}/{DamageFile}"] = damageFont;
                    changes[mapEntry.Path] = PointDamageAt(mapBytes, DamageFile, language);
                }

                void Replace(string folder, IReadOnlyList<string> files, IReadOnlyList<string> others, byte[] font, string what, string other)
                {
                    if (files.Count == 0) throw new InvalidDataException($"font.lpk's {folder} FontMap.xml names no font for {what}.");
                    if (files.Intersect(others, StringComparer.OrdinalIgnoreCase).Any())
                        throw new InvalidDataException($"In font.lpk's {folder} folder, {what} and {other} share one font file, so they cannot have different fonts.");
                    foreach (var file in files)
                        changes[(Find(entries, folder, file) ?? throw new InvalidDataException($"font.lpk has no {folder}\\{file}.")).Path] = font;
                }
            }
            if (languages == 0)
                throw new InvalidDataException("font.lpk has none of the English, French, German or Spanish fonts, so Arkive does not know which files to change.");
        }

        // The game's copy is kept before anything changes.
        var original = await HashAsync(source, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_backupDirectory);
        var backup = Path.Combine(_backupDirectory, $"font-{original[..16]}.lpk");
        if (!File.Exists(backup) || !(await HashAsync(backup, cancellationToken).ConfigureAwait(false)).Equals(original, StringComparison.OrdinalIgnoreCase))
        {
            var copying = backup + ".copying";
            File.Copy(source, copying, overwrite: true);
            File.Move(copying, backup, overwrite: true);
        }

        var staged = ArchivePath + ".arkive-new";
        if (File.Exists(staged)) File.Delete(staged);
        LostArkDataArchive.Rewrite(backup, changes, staged, cancellationToken);
        await using (var check = File.OpenRead(staged))
        {
            var written = LostArkDataArchive.ReadEntries(check, cancellationToken);
            foreach (var (path, bytes) in changes)
                if (written.FirstOrDefault(entry => entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is not { } entry ||
                    !LostArkDataArchive.Read(check, entry).AsSpan().SequenceEqual(bytes))
                {
                    check.Close();
                    File.Delete(staged);
                    throw new InvalidDataException($"The rebuilt font.lpk did not read back {path} as written.");
                }
        }
        var applied = await HashAsync(staged, cancellationToken).ConfigureAwait(false);
        SaveState(new GameFontState(StateVersion, ArchivePath, original, applied, backup, chosen, DateTimeOffset.UtcNow));
        File.Move(staged, ArchivePath, overwrite: true);

        // Copies of font.lpk from before a game update are no longer needed.
        foreach (var old in Directory.EnumerateFiles(_backupDirectory, "font-*.lpk"))
            if (!old.Equals(backup, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
        foreach (var old in Directory.EnumerateFiles(_libraryDirectory, "*.ttf"))
            if (!chosen.Any(font => font.StoredPath.Equals(old, StringComparison.OrdinalIgnoreCase))) File.Delete(old);
    }

    /// <summary>Puts the chosen fonts in the game again, after a game update replaced font.lpk.</summary>
    public Task ReapplyAsync(CancellationToken cancellationToken = default) =>
        LoadState() is { } state
            ? ApplyAsync(state.Fonts.ToDictionary(font => font.Slot, font => font.StoredPath), cancellationToken)
            : Task.CompletedTask;

    /// <summary>Puts the game's own font.lpk back (only when it still holds Arkive's change).</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        RefuseWhileRunning();
        if (LoadState() is not { } state)
        {
            if (File.Exists(_statePath)) File.Delete(_statePath);
            return;
        }
        if (File.Exists(ArchivePath) &&
            (await HashAsync(ArchivePath, cancellationToken).ConfigureAwait(false)).Equals(state.AppliedSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(state.BackupPath) || !(await HashAsync(state.BackupPath, cancellationToken).ConfigureAwait(false))
                    .Equals(state.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Arkive's copy of the game's font.lpk is missing or damaged. Verify the game files in Steam to put the original back.");
            var restoring = ArchivePath + ".arkive-restore";
            File.Copy(state.BackupPath, restoring, overwrite: true);
            File.Move(restoring, ArchivePath, overwrite: true);
        }
        File.Delete(_statePath);
    }

    /// <summary>
    /// Why the game's font.lpk cannot be used as it is, when another font tool has changed it (FontChanger hides a
    /// replaced font by renaming it to 1_1_… and adds the new one), or null.
    /// </summary>
    public async Task<string?> ForeignChangeAsync(CancellationToken cancellationToken = default)
    {
        var source = await CleanSourceAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = File.OpenRead(source);
        return ForeignChange(LostArkDataArchive.ReadEntries(stream, cancellationToken));
    }

    private static string? ForeignChange(IReadOnlyList<LostArkDataEntry> entries)
    {
        var names = entries.Select(entry => entry.Path).ToArray();
        var hidden = names.Any(name => Path.GetFileName(name).StartsWith("1_1_", StringComparison.Ordinal));
        return hidden || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length
            ? "The game's font.lpk was changed by another font tool. Verify the game files in Steam to get the game's own fonts back, then apply here."
            : null;
    }

    // The game's own font.lpk: Arkive's copy of it while Arkive's change is in place, else the file itself.
    private async Task<string> CleanSourceAsync(CancellationToken cancellationToken)
    {
        if (!GameHasFonts) throw new FileNotFoundException("The game's font.lpk was not found in its EFGame folder.");
        if (LoadState() is not { } state ||
            !(await HashAsync(ArchivePath, cancellationToken).ConfigureAwait(false)).Equals(state.AppliedSha256, StringComparison.OrdinalIgnoreCase))
            return ArchivePath;
        if (!File.Exists(state.BackupPath) || !(await HashAsync(state.BackupPath, cancellationToken).ConfigureAwait(false))
                .Equals(state.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Arkive's copy of the game's font.lpk is missing or damaged. Verify the game files in Steam to put the original back.");
        return state.BackupPath;
    }

    private static LostArkDataEntry? Find(IReadOnlyList<LostArkDataEntry> entries, string language, string file) =>
        entries.FirstOrDefault(entry => entry.Path.Equals($"/Binaries/Fonts/{language}/{file}", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, string>? ReadFontMap(Stream stream, IReadOnlyList<LostArkDataEntry> entries, string language) =>
        Find(entries, language, "FontMap.xml") is { } entry ? ParseFontMap(LostArkDataArchive.Read(stream, entry)) : null;

    private static Dictionary<string, string> ParseFontMap(byte[] xml)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match item in FontMapItem().Matches(Encoding.UTF8.GetString(xml)))
            map.TryAdd(item.Groups["key"].Value, item.Groups["file"].Value);
        return map;
    }

    private static IReadOnlyList<string> Files(Dictionary<string, string> map, IEnumerable<string> keys) =>
        keys.Select(key => map.GetValueOrDefault(key)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // Only the damage font's File attribute changes; the rest of the file (and its byte order mark) stays as it is.
    private static byte[] PointDamageAt(byte[] xml, string file, string language)
    {
        var bom = xml.AsSpan().StartsWith("﻿"u8) ? 3 : 0;
        var text = Encoding.UTF8.GetString(xml, bom, xml.Length - bom);
        if (!DamageItem().IsMatch(text))
            throw new InvalidDataException($"font.lpk's {language} FontMap.xml has no damage number font to change.");
        var changed = DamageItem().Replace(text, match => match.Groups["start"].Value + file + match.Groups["end"].Value, 1);
        return [.. xml.AsSpan(0, bom), .. Encoding.UTF8.GetBytes(changed)];
    }

    [GeneratedRegex("""<Item\b[^>]*?\bKey="(?<key>[^"]+)"[^>]*?\bFile="(?<file>[^"]+)""")]
    private static partial Regex FontMapItem();

    [GeneratedRegex("""(?<start><Item\b[^>]*?\bKey="\$eventDamageFont"[^>]*?\bFile=")[^"]*(?<end>")""")]
    private static partial Regex DamageItem();

    /// <summary>A readable name for a slot.</summary>
    public static string Describe(GameFontSlot slot) => slot switch
    {
        GameFontSlot.BodyText => "Body text",
        GameFontSlot.Titles => "Titles",
        _ => "Damage numbers",
    };

    private void RefuseWhileRunning()
    {
        var running = _processGuard.FindBlockingProcesses();
        if (running.Count > 0)
            throw new InvalidOperationException($"Close Lost Ark first: {string.Join(", ", running)} is running.");
    }

    private void SaveState(GameFontState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporary = _statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, _statePath, overwrite: true);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
