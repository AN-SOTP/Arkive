namespace Arkive.Core.Assets;

public sealed record AssetCatalogEntry(
    int ExportIndex,
    string ObjectName,
    string ObjectPath,
    string ClassName,
    AssetKind Kind,
    int SerialSize,
    int SerialOffset);
