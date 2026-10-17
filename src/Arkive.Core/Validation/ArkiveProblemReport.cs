using System.IO.Compression;
using System.Text;

namespace Arkive.Core.Validation;

/// <summary>What goes into a problem report: a summary, text sections and files to copy (never game files or settings).</summary>
public sealed record ArkiveProblemReportContent(
    string Summary,
    IReadOnlyDictionary<string, string> Sections,
    IReadOnlyDictionary<string, string> Files);

/// <summary>
/// Packs what's needed to diagnose a problem into one zip a user can send: a summary (version, folders, Windows),
/// the recent session logs, the mod list and the checks of the meshes being edited.
/// </summary>
public static class ArkiveProblemReport
{
    /// <summary>Largest single file copied; longer logs keep their end, where the problem usually is.</summary>
    public const long MaximumFileBytes = 8L << 20;

    public static async Task<string> CreateAsync(
        string reportDirectory, ArkiveProblemReportContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        Directory.CreateDirectory(reportDirectory);
        var path = Path.Combine(reportDirectory, $"arkive-problem-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            await WriteTextAsync(zip, "summary.txt", content.Summary, cancellationToken).ConfigureAwait(false);
            foreach (var (name, text) in content.Sections)
                await WriteTextAsync(zip, name, text, cancellationToken).ConfigureAwait(false);
            foreach (var (name, source) in content.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(source)) continue;
                await CopyAsync(zip, name, source, cancellationToken).ConfigureAwait(false);
            }
        }
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    private static async Task WriteTextAsync(ZipArchive zip, string name, string text, CancellationToken cancellationToken)
    {
        await using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await entry.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyAsync(ZipArchive zip, string name, string source, CancellationToken cancellationToken)
    {
        // Shared read: the current session's log is still open for writing.
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > MaximumFileBytes) input.Seek(-MaximumFileBytes, SeekOrigin.End);
        await using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await input.CopyToAsync(entry, cancellationToken).ConfigureAwait(false);
    }
}
