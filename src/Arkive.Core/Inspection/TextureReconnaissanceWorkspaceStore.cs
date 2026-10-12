using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkive.Core.Inspection;

public sealed class TextureReconnaissanceWorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public TextureReconnaissanceWorkspaceStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public async Task<TextureReconnaissanceEntry> SaveAsync(
        TextureReconnaissanceReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var id = $"{report.CreatedAt:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var directory = Path.Combine(
            RootDirectory,
            SafePathSegment(report.PackageAlias),
            SafePathSegment(report.ObjectName) + "-" + id);
        var jsonPath = Path.Combine(directory, "arkive-texture-recon.json");
        var textPath = Path.Combine(directory, "arkive-texture-recon.txt");
        Directory.CreateDirectory(directory);
        await SaveJsonAtomicAsync(jsonPath, report, cancellationToken).ConfigureAwait(false);
        await SaveTextAtomicAsync(textPath, RenderText(report), cancellationToken).ConfigureAwait(false);
        return new TextureReconnaissanceEntry(directory, jsonPath, textPath, report);
    }

    private static async Task SaveJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task SaveTextAtomicAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                value,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string RenderText(TextureReconnaissanceReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Arkive native Texture2D reconnaissance");
        builder.AppendLine($"Created: {report.CreatedAt:O}");
        builder.AppendLine($"Package: {report.PackagePath}");
        builder.AppendLine($"Package alias: {report.PackageAlias}");
        builder.AppendLine($"Package SHA-256: {report.PackageSha256}");
        builder.AppendLine($"Object: {report.ObjectName}");
        builder.AppendLine($"Class: {report.ClassName}");
        builder.AppendLine($"Selection export index: {report.ExportIndex}");
        builder.AppendLine($"Native UE export index: {report.NativeExportIndex?.ToString() ?? "unresolved"}");
        builder.AppendLine($"Logical export: 0x{report.SerialOffset:X}+0x{report.SerialSize:X}");
        builder.AppendLine($"Export SHA-256: {report.ExportPayloadSha256}");
        builder.AppendLine($"Protection provider: {report.ProtectionProviderName}");
        builder.AppendLine($"Property stream: {FormatOptionalOffset(report.PropertyStreamRelativeOffset)}");
        builder.AppendLine($"Native data: {FormatOptionalOffset(report.NativeDataRelativeOffset)}");
        builder.AppendLine($"Pixel format: {report.PixelFormatEvidence}");
        builder.AppendLine($"Duration: {report.Duration.TotalSeconds:N3}s");
        builder.AppendLine();

        builder.AppendLine($"Properties ({report.Properties.Count})");
        foreach (var property in report.Properties)
        {
            builder.AppendLine(
                $"  +0x{property.RelativeOffset:X} {property.Name} : {property.Type} " +
                $"size={property.Size} array={property.ArrayIndex} value={property.ValuePreview}");
        }

        builder.AppendLine();
        builder.AppendLine($"Mip candidates ({report.MipCandidates.Count})");
        foreach (var candidate in report.MipCandidates)
        {
            builder.AppendLine(
                $"  Candidate {candidate.CandidateNumber}: +0x{candidate.ArrayRelativeOffset:X}, " +
                $"mips={candidate.DeclaredMipCount}, score={candidate.Score}, " +
                $"confidence={candidate.Confidence}");
            foreach (var mip in candidate.Mips)
            {
                builder.AppendLine(
                    $"    mip {mip.MipIndex}: {mip.Width}x{mip.Height}, {mip.Storage}, " +
                    $"elements={mip.ElementCount}, stored={mip.StoredSize}, " +
                    $"flags=0x{mip.BulkDataFlags:X8}, bulkOffset=0x{mip.BulkDataOffset:X}");
            }
            foreach (var reason in candidate.Reasons)
            {
                builder.AppendLine($"    evidence: {reason}");
            }
        }

        builder.AppendLine();
        builder.AppendLine($"Export storage ({report.ExportStorage.Count})");
        foreach (var storage in report.ExportStorage)
        {
            builder.AppendLine(
                $"  {storage.StorageKind}: logical=0x{storage.LogicalOffset:X}+0x{storage.LogicalSize:X}, " +
                $"physical=0x{storage.PhysicalOffset:X}+0x{storage.PhysicalSize:X}, " +
                $"chunk={(storage.ChunkNumber?.ToString() ?? "-")}, " +
                $"flags={(storage.LostArkFlags is null ? "-" : $"0x{storage.LostArkFlags:X8}")}");
        }

        builder.AppendLine();
        builder.AppendLine($"Issues ({report.Issues.Count})");
        foreach (var issue in report.Issues)
        {
            builder.AppendLine($"  [{issue.Severity}] {issue.Code}: {issue.Message}");
        }

        return builder.ToString();
    }

    private static string FormatOptionalOffset(long? value) =>
        value is null ? "unresolved" : $"+0x{value:X}";

    private static string SafePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "unnamed";
        }

        return sanitized.Length <= 80 ? sanitized : sanitized[..80];
    }
}
