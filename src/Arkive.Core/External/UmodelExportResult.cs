namespace Arkive.Core.External;

public sealed record UmodelExportResult(
    string PackagePath,
    UmodelAssetEntry Asset,
    string OutputDirectory,
    IReadOnlyList<string> OutputFiles,
    IReadOnlyList<string> Diagnostics,
    TimeSpan Duration,
    bool UsedPackageWideFallback);
