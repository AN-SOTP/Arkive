namespace Arkive.Core.Assets;

public sealed record UnrealObjectExport(
    int TableIndex,
    int ClassIndex,
    int SuperIndex,
    int OuterIndex,
    UnrealNameReference ObjectName,
    int ArchetypeIndex,
    ulong ObjectFlags,
    int SerialSize,
    int SerialOffset,
    uint ExportFlags,
    IReadOnlyList<int> NetObjectCounts,
    Guid PackageGuid,
    int UnknownTail);
