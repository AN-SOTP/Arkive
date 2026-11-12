using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkive.Core.Meshes;

/// <summary>
/// The full skeleton report of one mesh check, kept gzip-compressed beside the edit record or build manifest that used
/// to embed it. The report is about 300 KB per mesh, compresses about 11 to 1 and is only ever shown or logged, so
/// records stay small enough to read at every launch and the report is read only when someone asks for it.
/// </summary>
public static class SkeletalMeshSkeletonCompatibilityFile
{
    public const string FileName = "arkive-skeleton-compatibility.json.gz";
    public const int CurrentSchemaVersion = 1;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // The check's time and edited-GLB hash say which check the report belongs to: a record written again later (by a
    // new check, or by Arkive 0.7.5, which knows nothing of this file) must not be shown an older check's report.
    private sealed record Stored(
        int SchemaVersion,
        DateTimeOffset PreflightCreatedAt,
        string CandidateGlbSha256,
        SkeletalMeshSkeletonCompatibilityReport? Report);

    public static string PathIn(string directory) => Path.Combine(directory, FileName);

    /// <summary>The check as records and build manifests now store it: without the full report, with its summary.</summary>
    public static SkeletalMeshEditPreflightReport Slim(SkeletalMeshEditPreflightReport preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        return preflight with
        {
            SkeletonCompatibility = null,
            SkeletonCompatibilitySummary = preflight.SkeletonCompatibility is { } report
                ? SkeletalMeshSkeletonCompatibilitySummary.From(report)
                : preflight.SkeletonCompatibilitySummary,
        };
    }

    /// <summary>Writes the check's full report beside a record, replacing any older one. Does nothing without a report.</summary>
    public static void Write(string directory, SkeletalMeshEditPreflightReport preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        if (preflight.SkeletonCompatibility is null) return;
        var path = PathIn(directory);
        var temporary = TemporaryPath(path);
        try
        {
            WriteTo(temporary, preflight);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string TemporaryPath(string path) => path + $".{Guid.NewGuid():N}.tmp";

    internal static void WriteTo(string path, SkeletalMeshEditPreflightReport preflight)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gzip, new Stored(
                CurrentSchemaVersion, preflight.CreatedAt, preflight.CandidateGlbSha256,
                preflight.SkeletonCompatibility), JsonOptions);
        file.Flush(flushToDisk: true);
    }

    /// <summary>
    /// The report beside a record for the check made at <paramref name="preflightCreatedAt"/> on the edited GLB with
    /// <paramref name="candidateGlbSha256"/>; null when there is none, or it belongs to another check.
    /// </summary>
    public static Task<SkeletalMeshSkeletonCompatibilityReport?> ReadAsync(
        string directory,
        DateTimeOffset preflightCreatedAt,
        string candidateGlbSha256,
        CancellationToken cancellationToken = default) =>
        ReadFromAsync(PathIn(directory), preflightCreatedAt, candidateGlbSha256, cancellationToken);

    internal static async Task<SkeletalMeshSkeletonCompatibilityReport?> ReadFromAsync(
        string path,
        DateTimeOffset preflightCreatedAt,
        string candidateGlbSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var stored = await JsonSerializer.DeserializeAsync<Stored>(gzip, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return stored is { SchemaVersion: CurrentSchemaVersion } &&
               stored.PreflightCreatedAt == preflightCreatedAt &&
               string.Equals(stored.CandidateGlbSha256, candidateGlbSha256, StringComparison.OrdinalIgnoreCase)
            ? stored.Report
            : null;
    }
}
