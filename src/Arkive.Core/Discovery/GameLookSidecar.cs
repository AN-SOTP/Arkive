using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arkive.Core.Skills;

namespace Arkive.Core.Discovery;

/// <param name="Targets">
/// Everything the looks draw, each once, as Class|PACKAGE|OBJECT (SkeletalMesh|PC_MG_LF_00|PC_MG_LF_00_HELMET_SK).
/// </param>
/// <param name="Looks">
/// Each look file by its name in upper case (EFDLITEM_PC_MG_LF_00-5_HELMET.PC_MG_LF_00-5_HELMET), with what it draws
/// as places in <paramref name="Targets"/>.
/// </param>
public sealed record GameLookDocument(
    int Version,
    DateTimeOffset GeneratedAt,
    long ArchiveLength,
    long ArchiveLastWriteUtcTicks,
    string[] Targets,
    Dictionary<string, int[]> Looks);

/// <summary>
/// What each of the game's looks draws. The tables name an item, NPC or prop's model as a look (EFDLItem_..., a file
/// in data4.lpk, XmlData\LookInfo), and the look names the mesh, materials and effects the game really loads, which
/// need not follow the look's own name: a crafted outfit's hat is drawn with a mesh shared by every colour of it, and
/// one class's outfit often uses another class's materials. Reading the looks is what lets an in-game name reach those
/// assets. Kept beside the game-name database and rebuilt whenever the game's data4.lpk changes.
///
/// Two things a look draws are left out, because they are not the look's own: the body skin and the hair an outfit
/// piece shows (the character's), and anything drawn by more than <see cref="SharedTargetLimit"/> looks (a plain
/// crate, a default hit effect).
/// </summary>
public static partial class GameLookSidecar
{
    public const int CurrentVersion = 1;
    public const string LookFolder = "/XmlData/LookInfo/";
    public const int SharedTargetLimit = 250;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly HashSet<string> MeshClasses = new(StringComparer.OrdinalIgnoreCase)
        { "SkeletalMesh", "StaticMesh", "FracturedStaticMesh" };

    private static readonly HashSet<string> OtherClasses = new(StringComparer.OrdinalIgnoreCase)
        { "MaterialInstanceConstant", "Material", "ParticleSystem", "Texture2D" };

    public static string GetDefaultPath(string metadataPath)
    {
        var fullPath = Path.GetFullPath(metadataPath);
        return Path.Combine(Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(fullPath) + ".looks.json.gz");
    }

    /// <summary>EFGame\data4.lpk for a Packages folder (EFGame\ReleasePC\Packages), or null when it is missing.</summary>
    public static string? FindArchive(string packagesRoot)
    {
        var path = Path.GetFullPath(Path.Combine(packagesRoot, "..", "..", "data4.lpk"));
        return File.Exists(path) ? path : null;
    }

    public static GameLookDocument? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var document = JsonSerializer.Deserialize<GameLookDocument>(gzip, JsonOptions);
            return document?.Version == CurrentVersion && document.Looks is not null && document.Targets is not null ? document : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the saved looks were read from the game's own data4.lpk as it is now. <paramref name="archivePath"/> is
    /// that file, or Arkive's copy of it while Arkive's outfit looks are in the game; the copy keeps the game file's
    /// size and date, so putting looks in and taking them out does not read the looks again. Only the start of the
    /// saved file is read.
    /// </summary>
    public static bool IsCurrent(string metadataPath, string archivePath)
    {
        if (!File.Exists(archivePath)) return true;
        var path = GetDefaultPath(metadataPath);
        if (!File.Exists(path)) return false;
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var head = new byte[512];
            var read = gzip.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            var match = StampPattern().Match(Encoding.UTF8.GetString(head, 0, read));
            var info = new FileInfo(archivePath);
            return match.Success &&
                   match.Groups["version"].Value == CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                   match.Groups["length"].Value == info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                   match.Groups["ticks"].Value == info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads every look in <paramref name="archivePath"/> (the game's own data4.lpk) and saves them beside the game-name database.</summary>
    public static async Task<GameLookDocument> BuildAsync(string metadataPath, string archivePath, CancellationToken cancellationToken = default)
    {
        var drawn = new Dictionary<string, List<(string Class, string Package, string Object)>>(StringComparer.Ordinal);
        var info = new FileInfo(archivePath);
        await using (var stream = File.OpenRead(archivePath))
        {
            foreach (var entry in LostArkDataArchive.ReadEntries(stream, cancellationToken))
            {
                if (!entry.Path.Contains(LookFolder, StringComparison.OrdinalIgnoreCase) ||
                    !entry.Path.EndsWith(".loa", StringComparison.OrdinalIgnoreCase)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                byte[] bytes;
                try
                {
                    bytes = LostArkDataArchive.Read(stream, entry);
                }
                catch (InvalidDataException)
                {
                    continue;
                }
                var name = Path.GetFileNameWithoutExtension(entry.Path).ToUpperInvariant();
                if (!drawn.TryGetValue(name, out var targets)) drawn[name] = targets = [];
                foreach (var path in ObjectPaths(bytes))
                    if (ParsePath(path) is { } target && !targets.Any(other => other.Package == target.Package && other.Object == target.Object))
                        targets.Add(target);
            }
        }
        var document = Create(drawn, info.Length, info.LastWriteTimeUtc.Ticks);
        await SaveAsync(GetDefaultPath(metadataPath), document, cancellationToken).ConfigureAwait(false);
        return document;
    }

    /// <summary>The looks as they are saved: what each draws of its own, every drawn thing listed once.</summary>
    internal static GameLookDocument Create(
        IReadOnlyDictionary<string, List<(string Class, string Package, string Object)>> drawn, long archiveLength, long archiveTicks)
    {
        var uses = new Dictionary<(string Package, string Object), int>();
        foreach (var target in drawn.Values.SelectMany(targets => targets))
            uses[(target.Package, target.Object)] = uses.GetValueOrDefault((target.Package, target.Object)) + 1;

        var places = new Dictionary<(string Package, string Object), int>();
        var all = new List<string>();
        var looks = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var (name, targets) in drawn.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var item = name.StartsWith("EFDLITEM_", StringComparison.Ordinal);
            var meshPackages = targets.Where(target => IsMeshClass(target.Class)).Select(target => target.Package).ToHashSet(StringComparer.Ordinal);
            var own = new List<int>();
            foreach (var target in targets)
            {
                if (uses[(target.Package, target.Object)] > SharedTargetLimit) continue;
                // The body skin and the hair under a hat are the character's own, drawn by nearly every outfit piece.
                if (item && !IsMeshClass(target.Class) && !meshPackages.Contains(target.Package) &&
                    (target.Package.Contains("BASEBODY", StringComparison.Ordinal) || target.Package.EndsWith("_HAIR", StringComparison.Ordinal)))
                    continue;
                if (!places.TryGetValue((target.Package, target.Object), out var place))
                {
                    places[(target.Package, target.Object)] = place = all.Count;
                    all.Add($"{target.Class}|{target.Package}|{target.Object}");
                }
                own.Add(place);
            }
            if (own.Count > 0) looks[name] = own.ToArray();
        }
        return new GameLookDocument(CurrentVersion, DateTimeOffset.UtcNow, archiveLength, archiveTicks, all.ToArray(), looks);
    }

    /// <summary>
    /// The meshes, materials, effects and textures a look file names, in the order it names them. A look is a run of
    /// values; text is its length (with the terminator), the characters and a zero byte.
    /// </summary>
    public static IReadOnlyList<string> ObjectPaths(ReadOnlySpan<byte> bytes)
    {
        var paths = new List<string>();
        var first = true;
        for (var at = 0; at + 4 < bytes.Length; at++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]);
            if (length is < 4 or > 1024 || at + 4 + length > bytes.Length || bytes[at + 3 + length] != 0 ||
                bytes.Slice(at + 4, length - 1).IndexOfAnyExceptInRange((byte)0x20, (byte)0x7e) >= 0) continue;
            var text = Encoding.ASCII.GetString(bytes.Slice(at + 4, length - 1));
            at += 3 + length;
            // The first text is the look's own name, which reads like a path.
            if (first)
            {
                first = false;
                if (text.StartsWith("EFDL", StringComparison.OrdinalIgnoreCase)) continue;
            }
            if (ParsePath(text) is not null && !paths.Contains(text, StringComparer.OrdinalIgnoreCase)) paths.Add(text);
        }
        return paths;
    }

    /// <summary>The name a mesh shares with its textures: PC_MG_LF_00_UPPER for PC_MG_LF_00_UPPER_SK_LOC_INT.</summary>
    internal static string MeshFamily(string meshObject)
    {
        var stem = meshObject.EndsWith("_LOC_INT", StringComparison.Ordinal) ? meshObject[..^8] : meshObject;
        return stem.EndsWith("_SK", StringComparison.Ordinal) && stem.Length > 3 ? stem[..^3] : stem;
    }

    internal static bool IsMeshClass(string className) => MeshClasses.Contains(className);

    /// <summary>
    /// The class, package and object (both upper case) a look's text names, or null when it is not a mesh, material,
    /// effect or texture: SkeletalMesh'PC_MG_LF_00.Mesh.PC_MG_LF_00_Helmet_SK', or the path alone, which older looks
    /// write for some meshes and materials (read as "SkeletalMesh" and "Material").
    /// </summary>
    internal static (string Class, string Package, string Object)? ParsePath(string text)
    {
        var quoted = QuotedPath().Match(text);
        if (quoted.Success)
        {
            var className = quoted.Groups["class"].Value;
            return MeshClasses.Contains(className) || OtherClasses.Contains(className)
                ? (className, quoted.Groups["package"].Value.ToUpperInvariant(), quoted.Groups["object"].Value.ToUpperInvariant())
                : null;
        }
        // A path without its class is only taken for a mesh or material when it reads like one: Package.Mesh.Object
        // or ..._SK, Package.Mat.Object or ..._MI. Anything else (an animation, a sound set) is left out.
        var bare = BarePath().Match(text);
        if (!bare.Success) return null;
        var package = bare.Groups["package"].Value.ToUpperInvariant();
        var objectName = bare.Groups["object"].Value.ToUpperInvariant();
        if (text.Contains(".Mesh.", StringComparison.OrdinalIgnoreCase) || objectName.EndsWith("_SK", StringComparison.Ordinal) ||
            objectName.EndsWith("_SK_LOC_INT", StringComparison.Ordinal))
            return ("SkeletalMesh", package, objectName);
        return text.Contains(".Mat.", StringComparison.OrdinalIgnoreCase) || objectName.Contains("_MI", StringComparison.Ordinal)
            ? ("Material", package, objectName)
            : null;
    }

    // The saved file starts {"version":1,"generatedAt":"...","archiveLength":N,"archiveLastWriteUtcTicks":N,...
    [GeneratedRegex("^\\{\"version\":(?<version>\\d+),\"generatedAt\":\"[^\"]*\",\"archiveLength\":(?<length>\\d+),\"archiveLastWriteUtcTicks\":(?<ticks>\\d+),", RegexOptions.CultureInvariant)]
    private static partial Regex StampPattern();

    private static async Task SaveAsync(string path, GameLookDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                await JsonSerializer.SerializeAsync(gzip, document, JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    [GeneratedRegex(@"^(?<class>[A-Za-z0-9_]+)'(?<path>(?<package>[^.'\s]+)\.(?:[^.'\s]+\.)*(?<object>[^.'\s]+))'$", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPath();

    // Package.Group.Object with nothing else: at least two dots, so a socket or bone name is not taken for one.
    [GeneratedRegex(@"^(?<package>[A-Za-z0-9_\-]+)\.(?:[A-Za-z0-9_\-]+\.)+(?<object>[A-Za-z0-9_\-]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex BarePath();
}
