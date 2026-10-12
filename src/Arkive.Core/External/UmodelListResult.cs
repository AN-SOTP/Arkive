namespace Arkive.Core.External;

public sealed record UmodelListResult(
    string PackagePath,
    IReadOnlyList<UmodelAssetEntry> Assets,
    IReadOnlyList<string> Diagnostics,
    TimeSpan Duration);
