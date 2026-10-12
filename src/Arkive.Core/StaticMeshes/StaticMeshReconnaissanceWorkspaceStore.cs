using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkive.Core.StaticMeshes;

public sealed class StaticMeshReconnaissanceWorkspaceStore
{
    public const string ManifestFileName = "arkive-staticmesh-recon.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public StaticMeshReconnaissanceWorkspaceStore(string rootDirectory) =>
        RootDirectory = Path.GetFullPath(rootDirectory);

    public string RootDirectory { get; }

    public async Task<StaticMeshReconnaissanceEntry> SaveAsync(
        StaticMeshReconnaissanceReport report,
        string? oracleText = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        Directory.CreateDirectory(RootDirectory);
        var directory = Path.Combine(
            RootDirectory,
            $"{Sanitize(report.PackageAlias)}-{Sanitize(report.ObjectName)}-{report.NativeExportIndex ?? report.ExportIndex}");
        Directory.CreateDirectory(directory);
        var jsonPath = Path.Combine(directory, ManifestFileName);
        var textPath = Path.Combine(directory, "arkive-staticmesh-recon.txt");
        var oraclePath = string.IsNullOrWhiteSpace(oracleText)
            ? null
            : Path.Combine(directory, "umodel-staticmesh-dump.txt");
        await SaveJsonAtomicAsync(jsonPath, report, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(textPath, Format(report), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        if (oraclePath is not null)
            await File.WriteAllTextAsync(oraclePath, oracleText!, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
        return new StaticMeshReconnaissanceEntry(directory, jsonPath, textPath, oraclePath, report);
    }

    public async Task<IReadOnlyList<StaticMeshReconnaissanceEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var entries = new List<StaticMeshReconnaissanceEntry>();
        foreach (var path in Directory.EnumerateFiles(RootDirectory, ManifestFileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var report = await JsonSerializer.DeserializeAsync<StaticMeshReconnaissanceReport>(
                    stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                if (report is null || report.SchemaVersion != StaticMeshReconnaissanceService.CurrentSchemaVersion)
                    continue;
                var directory = Path.GetDirectoryName(path)!;
                entries.Add(new StaticMeshReconnaissanceEntry(
                    directory,
                    path,
                    Path.Combine(directory, "arkive-staticmesh-recon.txt"),
                    File.Exists(Path.Combine(directory, "umodel-staticmesh-dump.txt"))
                        ? Path.Combine(directory, "umodel-staticmesh-dump.txt")
                        : null,
                    report));
            }
            catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
            {
                // A damaged report must not hide healthy reports.
            }
        }
        return entries.OrderByDescending(entry => entry.Report.CreatedAt).ToArray();
    }

    public static async Task<StaticMeshReconnaissanceReport> LoadReportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var report = await JsonSerializer.DeserializeAsync<StaticMeshReconnaissanceReport>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                     throw new JsonException("The StaticMesh reconnaissance report was empty.");
        if (report.SchemaVersion != StaticMeshReconnaissanceService.CurrentSchemaVersion)
            throw new NotSupportedException(
                $"StaticMesh reconnaissance schema {report.SchemaVersion} is unsupported.");
        return report;
    }

    private static string Format(StaticMeshReconnaissanceReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Arkive StaticMesh reconnaissance schema {report.SchemaVersion}");
        builder.AppendLine($"Package: {report.PackageAlias} ({report.PhysicalPackageName})");
        builder.AppendLine($"Object: {report.ClassName} {report.ObjectName}");
        builder.AppendLine($"Export: #{report.NativeExportIndex} 0x{report.SerialOffset:X}+0x{report.SerialSize:X}");
        builder.AppendLine($"Readiness: {report.Readiness}");
        if (report.NativeLayout is { } native)
        {
            builder.AppendLine($"Profile: {native.ParserProfile}; internal version {native.InternalVersion}");
            builder.AppendLine($"Collision: {native.CollisionNodeCount:N0} nodes, {native.CollisionTriangleCount:N0} triangles (preserved)");
            foreach (var lod in native.Lods)
                builder.AppendLine(
                    $"LOD {lod.LodIndex}: {lod.VertexCount:N0} vertices, {lod.Indices.ElementCount:N0} indices, " +
                    $"{lod.TextureCoordinateCount:N0} UVs, {lod.Sections.Count:N0} sections");
        }
        foreach (var issue in report.Issues)
            builder.AppendLine($"{issue.Severity} {issue.Code}: {issue.Message}");
        return builder.ToString();
    }

    private static async Task SaveJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var text = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(text) ? "staticmesh" : text;
    }
}
