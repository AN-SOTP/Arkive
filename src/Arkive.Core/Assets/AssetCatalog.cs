using Arkive.Core.Packages;

namespace Arkive.Core.Assets;

public sealed record AssetCatalog
{
    public required PackageSummary Summary { get; init; }

    public required IReadOnlyList<UnrealNameEntry> Names { get; init; }

    public required IReadOnlyList<UnrealObjectImport> Imports { get; init; }

    public required IReadOnlyList<UnrealObjectExport> Exports { get; init; }

    public required IReadOnlyList<AssetCatalogEntry> Assets { get; init; }

    public required string ProtectionProviderName { get; init; }
}
