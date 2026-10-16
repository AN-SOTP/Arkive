using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arkive.Core.External;

namespace Arkive.Core.Viewer;

/// <summary>One of the game's environment cubemaps, as six PNG faces in the viewer's (three.js) order: +X, -X, +Y, -Y, +Z, -Z.</summary>
public sealed record ModelViewerEnvironment(string Name, string Label, IReadOnlyList<string> Faces);

public sealed record ModelViewerEnvironmentSet(string Directory, IReadOnlyList<ModelViewerEnvironment> Environments);

/// <summary>
/// The environment cubemaps the game lights characters with (package efpbrenvcubemaps: reflections and sky light
/// for each area), exported once by UModel as PNG faces and cached per package version, for the viewer's lighting
/// and background.
/// </summary>
public sealed partial class ModelViewerEnvironmentService(UmodelBridge bridge, string cacheRoot)
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string CacheRoot { get; } = Path.GetFullPath(cacheRoot);

    public async Task<ModelViewerEnvironmentSet> PrepareAsync(
        string packagePath, IReadOnlyList<string> cubemapNames, CancellationToken cancellationToken = default)
    {
        var package = new FileInfo(packagePath);
        if (!package.Exists) throw new FileNotFoundException("The environment package was not found.", packagePath);
        var key = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{package.FullName}|{package.Length}|{package.LastWriteTimeUtc.Ticks}|{string.Join(",", cubemapNames)}|{FormatVersion}")))[..24];
        var directory = Path.Combine(CacheRoot, key);
        var indexPath = Path.Combine(directory, "environments.json");
        if (File.Exists(indexPath) &&
            JsonSerializer.Deserialize<ModelViewerEnvironmentSet>(await File.ReadAllTextAsync(indexPath, cancellationToken)
                .ConfigureAwait(false), Json) is { } cached)
            return cached with { Directory = directory };

        var staging = directory + ".tmp";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        var environments = new List<ModelViewerEnvironment>();
        foreach (var name in cubemapNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = Path.Combine(staging, name);
            var files = await bridge.ExportNamedObjectAsync(package.FullName, name, folder, cancellationToken).ConfigureAwait(false);
            // UModel writes the six faces as cubemapface0..5 in Unreal order: +X, -X, +Y, -Y, +Z, -Z (Z up).
            var faces = Enumerable.Range(0, 6)
                .Select(face => files.FirstOrDefault(file => Path.GetFileNameWithoutExtension(file)
                    .Equals($"cubemapface{face}", StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (faces.Any(face => face is null)) continue;
            // Unreal Y becomes the viewer's Z and Unreal Z its Y (up).
            int[] order = [0, 1, 4, 5, 2, 3];
            environments.Add(new ModelViewerEnvironment(name, Label(name),
                order.Select(face => Path.GetRelativePath(staging, faces[face]!).Replace('\\', '/')).ToArray()));
        }
        Directory.CreateDirectory(staging);
        var set = new ModelViewerEnvironmentSet(directory, environments);
        await File.WriteAllTextAsync(Path.Combine(staging, "environments.json"), JsonSerializer.Serialize(set, Json), cancellationToken)
            .ConfigureAwait(false);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.Move(staging, directory);
        return set;
    }

    /// <summary>A readable name: pbr_cubemap_03 → Game environment 3, pbr_cubemap_env_01 → Game sky 1, pbr_khargh_ed_02_cubemap_00 → Khargh ed 02.</summary>
    public static string Label(string name)
    {
        var match = NumberedCubemap().Match(name);
        if (match.Success)
            return (match.Groups["inside"].Success ? "Game interior " : match.Groups["sky"].Success ? "Game sky " : "Game environment ") +
                   int.Parse(match.Groups["number"].Value);
        var words = name.Replace("pbr_", "").Replace("_cubemap_00", "").Replace("_cubemap", "").Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select((word, index) => index == 0 && word.Length > 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word));
    }

    [GeneratedRegex(@"^pbr_(?<inside>in)?cubemap_(?<sky>env_)?(?<number>\d+)$")]
    private static partial Regex NumberedCubemap();
}
