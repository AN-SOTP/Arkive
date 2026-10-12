namespace Arkive.Core.Mods;

/// <summary>Export indices are deliberately excluded because they move between client versions.</summary>
public readonly record struct ArkiveModTargetKey(
    string PhysicalPackageName,
    UnifiedPackageAssetKind Kind,
    string ObjectName)
{
    public static ArkiveModTargetKey From(ArkiveModFileAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return From(asset.Target.PhysicalPackageName, asset.Kind, asset.Target.ObjectName);
    }

    public static ArkiveModTargetKey From(
        string physicalPackageName,
        UnifiedPackageAssetKind kind,
        string objectName) => new(
        ArkiveModFileService.PhysicalPackageKey(physicalPackageName),
        kind,
        objectName.Trim().ToUpperInvariant());

    public override string ToString() => $"{PhysicalPackageName}:{Kind}:{ObjectName}";
}

public enum ArkiveModAssetOutcome
{
    Winning,
    Overridden,
    Tied,
    Disabled,
}

public sealed record ArkiveModAssetContender(
    string ModId,
    string ModName,
    int Priority,
    bool Enabled,
    ArkiveModFileAsset Asset);

public sealed record ArkiveModTargetResolution(
    ArkiveModTargetKey Key,
    string PackageAlias,
    string PhysicalPackageName,
    string ClassName,
    string ObjectName,
    IReadOnlyList<ArkiveModAssetContender> Contenders,
    ArkiveModAssetContender? Winner,
    IReadOnlyList<ArkiveModAssetContender> Tied)
{
    public IEnumerable<ArkiveModAssetContender> EnabledContenders =>
        Contenders.Where(contender => contender.Enabled);

    public bool IsBlockingTie => Tied.Count > 1;
}

public sealed record ArkiveModPairConflict(
    string OtherModId,
    string OtherModName,
    int OtherPriority,
    bool OtherEnabled,
    IReadOnlyList<string> SharedObjects,
    string Outcome);

public sealed class ArkiveModResolution
{
    private readonly Dictionary<(string ModId, string AssetId), ArkiveModTargetResolution> _byAsset;
    private readonly Dictionary<string, ArkiveModLibraryEntry> _mods;

    internal ArkiveModResolution(
        IReadOnlyList<ArkiveModLibraryEntry> mods,
        IReadOnlyList<ArkiveModTargetResolution> targets)
    {
        Targets = targets;
        _mods = mods.ToDictionary(mod => mod.ModId, StringComparer.OrdinalIgnoreCase);
        _byAsset = targets
            .SelectMany(target => target.Contenders.Select(contender => (target, contender)))
            .ToDictionary(
                item => (item.contender.ModId.ToUpperInvariant(), item.contender.Asset.AssetId.ToUpperInvariant()),
                item => item.target);
    }

    public IReadOnlyList<ArkiveModTargetResolution> Targets { get; }

    public IReadOnlyList<ArkiveModTargetResolution> BlockingTies =>
        Targets.Where(target => target.IsBlockingTie).ToArray();

    private ArkiveModTargetResolution? TargetFor(string modId, string assetId) =>
        _byAsset.GetValueOrDefault((modId.ToUpperInvariant(), assetId.ToUpperInvariant()));

    public ArkiveModAssetOutcome OutcomeFor(string modId, string assetId)
    {
        var target = TargetFor(modId, assetId);
        if (target is null || _mods.GetValueOrDefault(modId) is not { Enabled: true })
            return ArkiveModAssetOutcome.Disabled;
        if (target.Tied.Any(item => item.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase)))
            return ArkiveModAssetOutcome.Tied;
        return target.Winner?.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase) == true
            ? ArkiveModAssetOutcome.Winning
            : ArkiveModAssetOutcome.Overridden;
    }

    public ArkiveModAssetContender? StrongestCompetitor(string modId, string assetId) =>
        TargetFor(modId, assetId)?.EnabledContenders
            .FirstOrDefault(contender => !contender.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ArkiveModPairConflict> ConflictsFor(string modId)
    {
        if (_mods.GetValueOrDefault(modId) is not { } mod) return [];
        return Targets
            .Where(target => target.Contenders.Any(item => item.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(target => target.Contenders
                .Where(item => !item.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
                .Select(other => (target, other)))
            .GroupBy(item => item.other.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var other = group.First().other;
                string outcome;
                if (!mod.Enabled || !other.Enabled)
                    outcome = "Inactive — both mods must be enabled to compete";
                else if (mod.Priority == other.Priority)
                    outcome = "Tie — give one mod a higher priority";
                else if (mod.Priority > other.Priority)
                    outcome = "This mod has the higher priority";
                else
                    outcome = $"{other.ModName} has the higher priority";
                return new ArkiveModPairConflict(
                    other.ModId, other.ModName, other.Priority, other.Enabled,
                    group.Select(item => $"{item.target.ObjectName} ({item.target.PackageAlias})")
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                    outcome);
            })
            .OrderByDescending(item => item.OtherEnabled)
            .ThenByDescending(item => item.OtherPriority)
            .ThenBy(item => item.OtherModName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public int PriorityToWinAll(string modId)
    {
        if (_mods.GetValueOrDefault(modId) is not { } mod) return 0;
        var competitors = Targets
            .Where(target => target.Contenders.Any(item => item.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(target => target.EnabledContenders)
            .Where(item => !item.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Priority)
            .ToArray();
        return competitors.Length == 0 ? mod.Priority : Math.Max(mod.Priority, competitors.Max() + 1);
    }
}

public static class ArkiveModConflictResolver
{
    public static ArkiveModResolution Resolve(IReadOnlyList<ArkiveModLibraryEntry> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);
        var targets = mods
            .SelectMany(mod => mod.Manifest.Assets.Select(asset => new ArkiveModAssetContender(
                mod.ModId, mod.Manifest.Name, mod.Priority, mod.Enabled, asset)))
            .GroupBy(contender => ArkiveModTargetKey.From(contender.Asset))
            .Select(group =>
            {
                var contenders = group
                    .OrderByDescending(item => item.Enabled)
                    .ThenByDescending(item => item.Priority)
                    .ThenBy(item => item.ModName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.ModId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var enabled = contenders.Where(item => item.Enabled).ToArray();
                var top = enabled.Length == 0 ? null : enabled[0];
                var tied = top is null
                    ? []
                    : enabled.Where(item => item.Priority == top.Priority).ToArray();
                var first = contenders[0].Asset.Target;
                return new ArkiveModTargetResolution(
                    group.Key, first.PackageAlias,
                    ArkiveModFileService.NormalizePhysicalPackageName(first.PhysicalPackageName),
                    first.ClassName, first.ObjectName, contenders,
                    tied.Length == 1 ? top : null,
                    tied.Length > 1 ? tied : []);
            })
            .OrderBy(target => target.PackageAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ArkiveModResolution(mods, targets);
    }
}
