using Arkive.Core.Packages;

namespace Arkive.Core.Scanning;

public sealed record PackageScanReport
{
    public required string DirectoryPath { get; init; }

    public required int CandidateCount { get; init; }

    public required int ScannedCount { get; init; }

    public required IReadOnlyList<PackageSummary> Packages { get; init; }

    public required IReadOnlyList<PackageScanFailure> Failures { get; init; }
}
