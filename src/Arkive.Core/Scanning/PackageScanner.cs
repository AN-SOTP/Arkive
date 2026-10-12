using Arkive.Core.Packages;

namespace Arkive.Core.Scanning;

public sealed class PackageScanner(UpkPackageReader reader)
{
    public PackageScanReport Scan(string directoryPath, int? limit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        var fullPath = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Package directory does not exist: {fullPath}");
        }

        if (limit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Scan limit must be positive.");
        }

        var candidates = Directory
            .EnumerateFiles(fullPath, "*.upk", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selected = limit.HasValue ? candidates.Take(limit.Value) : candidates;

        var packages = new List<PackageSummary>();
        var failures = new List<PackageScanFailure>();

        foreach (var packagePath in selected)
        {
            try
            {
                packages.Add(reader.Read(packagePath));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(new PackageScanFailure(packagePath, exception.Message));
            }
        }

        return new PackageScanReport
        {
            DirectoryPath = fullPath,
            CandidateCount = candidates.Length,
            ScannedCount = packages.Count + failures.Count,
            Packages = packages,
            Failures = failures,
        };
    }
}
