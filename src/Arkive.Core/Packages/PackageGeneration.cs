namespace Arkive.Core.Packages;

public sealed record PackageGeneration(
    int ExportCount,
    int NameCount,
    int NetObjectCount);
