namespace Arkive.Core.External;

public sealed record UmodelObjectDumpResult(
    string PackagePath,
    UmodelAssetEntry Asset,
    string RawOutput,
    IReadOnlyList<string> Diagnostics,
    TimeSpan Duration);
