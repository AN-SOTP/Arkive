using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkive.Core.Materials;

public sealed record MaterialInstanceReconnaissanceEntry(
    string DirectoryPath,
    string JsonPath,
    MaterialInstanceReconnaissanceReport Report);

public sealed class MaterialInstanceWorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _rootPath;

    public MaterialInstanceWorkspaceStore(string rootPath) => _rootPath = Path.GetFullPath(rootPath);

    public async Task<MaterialInstanceReconnaissanceEntry> SaveAsync(
        MaterialInstanceReconnaissanceReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var directory = Path.Combine(_rootPath, Safe(report.PackageAlias),
            $"{report.ExportIndex:D6}-{Safe(report.ObjectName)}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "arkive-material-recon.json");
        await SaveJsonAtomicAsync(path, report, cancellationToken).ConfigureAwait(false);
        return new(directory, path, report);
    }

    internal static async Task SaveJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static JsonSerializerOptions SerializerOptions => JsonOptions;

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "material" : result;
    }
}
