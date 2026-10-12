namespace Arkive.Core.External;

public sealed record UmodelDependencyProbeResult(
    string ModelPackagePath,
    UmodelAssetEntry Asset,
    IReadOnlyList<string> PhysicalPackageNames,
    IReadOnlyList<string> Diagnostics,
    TimeSpan Duration);
