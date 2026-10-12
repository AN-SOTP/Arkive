using Arkive.Core.Packages;

namespace Arkive.Core.Validation;

public static class PackageValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(PackageSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var issues = new List<ValidationIssue>();

        if (summary.HeaderSize <= 0)
        {
            AddError(issues, "UPK001", $"Logical header size {summary.HeaderSize:N0} is not positive.");
        }

        ValidateTable(issues, "name", summary.NameCount, summary.NameOffset, "UPK010");
        ValidateTable(issues, "export", summary.ExportCount, summary.ExportOffset, "UPK011");
        ValidateTable(issues, "import", summary.ImportCount, summary.ImportOffset, "UPK012");

        ValidateLogicalOffset(issues, "dependency", summary.DependsOffset, "UPK013");
        ValidateLogicalOffset(issues, "import/export GUID", summary.ImportExportGuidsOffset, "UPK014");
        ValidateLogicalOffset(issues, "thumbnail", summary.ThumbnailTableOffset, "UPK015", allowZero: true);

        if (summary.ParsedSummarySize > summary.PhysicalFileSize)
        {
            AddError(
                issues,
                "UPK020",
                $"Parsed summary ends at 0x{summary.ParsedSummarySize:X}, beyond the physical file size 0x{summary.PhysicalFileSize:X}.");
        }

        if (!summary.IsWesternLostArkProfile)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "UPK030",
                $"Package profile is {summary.FileVersion}/{summary.LicenseeVersion}, compression 0x{summary.CompressionFlags:X8}; the prototype currently targets Western Lost Ark 868/16, compression 0x44."));
        }

        ValidateChunks(summary, issues);

        if (issues.Count == 0)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Information,
                "UPK000",
                "Package summary and compressed-chunk table passed structural validation."));
        }

        return issues;
    }

    private static void ValidateChunks(PackageSummary summary, List<ValidationIssue> issues)
    {
        ulong previousLogicalEnd = 0;
        ulong previousPhysicalEnd = 0;

        for (var index = 0; index < summary.CompressedChunks.Count; index++)
        {
            var chunk = summary.CompressedChunks[index];
            var number = index + 1;

            if (chunk.UncompressedSize == 0 || chunk.CompressedSize == 0)
            {
                AddError(issues, "UPK100", $"Compressed chunk {number} has a zero size.");
            }

            if (chunk.CompressedOffset < summary.ParsedSummarySize)
            {
                AddError(
                    issues,
                    "UPK101",
                    $"Compressed chunk {number} begins at 0x{chunk.CompressedOffset:X}, inside the parsed package summary ending at 0x{summary.ParsedSummarySize:X}.");
            }

            if (chunk.CompressedEnd > (ulong)summary.PhysicalFileSize)
            {
                AddError(
                    issues,
                    "UPK102",
                    $"Compressed chunk {number} ends at 0x{chunk.CompressedEnd:X}, beyond physical file size 0x{summary.PhysicalFileSize:X}.");
            }

            if (index > 0 && chunk.UncompressedOffset < previousLogicalEnd)
            {
                AddError(
                    issues,
                    "UPK103",
                    $"Compressed chunk {number} overlaps or precedes the previous logical range.");
            }

            if (index > 0 && chunk.CompressedOffset < previousPhysicalEnd)
            {
                AddError(
                    issues,
                    "UPK104",
                    $"Compressed chunk {number} overlaps or precedes the previous physical range.");
            }

            if (chunk.LostArkFlags is not 0 and not 1)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    "UPK105",
                    $"Compressed chunk {number} uses unrecognized Lost Ark flags 0x{chunk.LostArkFlags:X8}."));
            }

            previousLogicalEnd = chunk.UncompressedEnd;
            previousPhysicalEnd = chunk.CompressedEnd;
        }
    }

    private static void ValidateTable(
        List<ValidationIssue> issues,
        string label,
        int count,
        int offset,
        string code)
    {
        if (count > 0 && offset <= 0)
        {
            AddError(issues, code, $"The {label} table contains {count:N0} entries but has invalid logical offset {offset:N0}.");
        }

        if (count == 0 && offset < 0)
        {
            AddError(issues, code, $"The empty {label} table has negative logical offset {offset:N0}.");
        }
    }

    private static void ValidateLogicalOffset(
        List<ValidationIssue> issues,
        string label,
        int offset,
        string code,
        bool allowZero = false)
    {
        if (offset < 0 || (!allowZero && offset == 0))
        {
            AddError(issues, code, $"The {label} table has invalid logical offset {offset:N0}.");
        }
    }

    private static void AddError(List<ValidationIssue> issues, string code, string message) =>
        issues.Add(new ValidationIssue(ValidationSeverity.Error, code, message));
}
