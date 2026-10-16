namespace Arkive.Core.Mods;

/// <summary>
/// A game package that mods in the library target, which is not the clean version those mods were made for and
/// which this Arkive data folder has no record of changing: another Arkive data folder, another tool or a game patch
/// changed it. Building on it would treat changed bytes as the original.
/// </summary>
public sealed record ArkiveModOutsideChange(
    string PhysicalPackageName,
    string PackageAlias,
    string GamePath,
    string GameSha256,
    IReadOnlyList<string> CleanSha256s,
    IReadOnlyList<string> ChangedObjects);

/// <summary>A verified original, found in another Arkive data folder's backups, that can replace a changed game package.</summary>
public sealed record ArkiveModOutsideRestoreItem(
    string PhysicalPackageName,
    string PackageAlias,
    string GamePath,
    string BackupPath,
    string OriginalSha256);

public sealed record ArkiveModOutsideRestorePlan(
    string OtherDataFolder,
    IReadOnlyList<ArkiveModOutsideRestoreItem> Items,
    IReadOnlyList<ArkiveModOutsideChange> Unresolved);

public sealed record ArkiveModOutsideRestoreResult(
    IReadOnlyList<ArkiveModOutsideRestoreItem> Restored,
    string SafetyCopyDirectory);
