namespace Arkive.Core.Assets;

public sealed record UnrealObjectImport(
    int TableIndex,
    UnrealNameReference ClassPackage,
    UnrealNameReference ClassName,
    int OuterIndex,
    UnrealNameReference ObjectName);
