using System.IO;
using System.Windows;
using System.ComponentModel;
using Arkive.Core.Assets;
using Arkive.Core.Mods;

namespace Arkive.App;

public partial class MainWindow : Window
{
    private sealed class ModLibraryRow
    {
        public ModLibraryRow(ArkiveModStatus status, string filePath)
        {
            Status = status;
            FilePath = filePath;
        }

        public ArkiveModStatus Status { get; }

        public ArkiveModLibraryEntry Entry => Status.Mod;

        public string FilePath { get; }

        public bool Enabled => Entry.Enabled;

        public int Priority => Entry.Priority;

        public string DisplayName => Entry.Manifest.Name;

        public string Version => Entry.Manifest.Version;

        public string Author => string.IsNullOrWhiteSpace(Entry.Manifest.Author) ? "—" : Entry.Manifest.Author!;

        /// <summary>The author as typed, empty when there is none (the list shows a dash).</summary>
        public string AuthorText => Entry.Manifest.Author ?? string.Empty;

        public string AssetSummary
        {
            get
            {
                var counts = Entry.Manifest.Assets.GroupBy(asset => asset.Kind)
                    .OrderBy(group => group.Key)
                    .Select(group => $"{group.Count():N0} {KindLabel(group.Key, group.Count())}");
                return string.Join(", ", counts);
            }
        }

        public string Packages => string.Join(", ", Entry.Manifest.Assets
            .Select(asset => asset.Target.PackageAlias)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase));

        public bool AuthoringPending { get; init; }
        public bool NeedsGameUpdate { get; init; }

        public string State => NeedsGameUpdate ? "Needs game update" :
            AuthoringPending || Status.ModifiedLocally ? "Unsaved edits" : Status.State;

        public string Conflicts => Status.ConflictSummary;

        public string FileName => Entry.FileName;

        public string Origin => Entry.Origin switch
        {
            ArkiveModOrigin.Migrated => "Converted package project",
            ArkiveModOrigin.Created => "Created in Arkive",
            _ => "Imported",
        };

        public bool HasActiveAssets => Status.Assets.Any(asset => asset.InGame == true);

        /// <summary>The pack this mod is listed under, or the run of mods outside packs it sits in.</summary>
        public ModListGroup Group { get; set; } = null!;

        private static string KindLabel(UnifiedPackageAssetKind kind, int count) =>
            count == 1 ? AssetTypes.For(kind).Singular : AssetTypes.For(kind).Plural;
    }

    private sealed class ModAssetRow
    {
        public ModAssetRow(ArkiveModAssetStatus status) => Status = status;

        public ArkiveModAssetStatus Status { get; }

        public string Kind => AssetTypes.For(Status.Asset.Kind).Title;

        public string DisplayName => Status.Asset.DisplayName;

        public string ObjectName => Status.Asset.Target.ObjectName;

        public string Package => $"{Status.Asset.Target.PackageAlias} ({Status.Asset.Target.PhysicalPackageName})";

        public string Resolution => Status.Outcome switch
        {
            ArkiveModAssetOutcome.Winning => Status.Competitor is null
                ? "Active"
                : $"Wins over {Status.Competitor.ModName} (priority {Status.Competitor.Priority:N0})",
            ArkiveModAssetOutcome.Overridden => Status.Competitor is null
                ? "Overridden"
                : $"Overridden by {Status.Competitor.ModName} (priority {Status.Competitor.Priority:N0})",
            ArkiveModAssetOutcome.Tied => Status.Competitor is null
                ? "Tied — set a priority"
                : $"Tied with {Status.Competitor.ModName} — set a priority",
            _ => "Mod disabled",
        };

        public string Setup => Status.Setup switch
        {
            ArkiveModSetupState.Ready => "Set up",
            ArkiveModSetupState.EditMissing => "Working copy missing — re-set up on apply",
            _ => "Not set up yet — happens on apply",
        };

        public string InGame => Status.InGame switch
        {
            true => "In game",
            false when Status.Outcome == ArkiveModAssetOutcome.Winning => "Not in game yet",
            _ => "—",
        };

        public string LocalChanges => Status.ModifiedLocally ? "Unsaved local changes" : string.Empty;

        public string Route => Status.Asset switch
        {
            { Texture: { } texture } => $"{texture.PixelFormat} {texture.Width}x{texture.Height}",
            { Mesh.ImportRoute: "TopologyReplacement" } => "Variable-size topology",
            { Mesh: not null } => "Same-size deformation",
            { Material: { } material } => $"{ChangedParameterCount(material):N0} changed parameter(s)",
            _ => string.Empty,
        };

        private static int ChangedParameterCount(ArkiveModMaterialData material) =>
            material.Scalars.Count(item => item.Changed) +
            material.Vectors.Count(item => item.Changed) +
            material.Textures.Count(item => item.Changed) +
            (material.Parent?.Changed == true ? 1 : 0);
    }

    /// <summary>
    /// A heading in the Mods list. For a pack it shows the pack's switch, name and state; consecutive mods outside packs
    /// share a group with no heading, so the list reads as before.
    /// </summary>
    private sealed class ModListGroup(ArkiveModPack? pack)
    {
        public ArkiveModPack? Pack { get; } = pack;

        public bool IsPack => Pack is not null;

        public List<ModLibraryRow> Rows { get; } = [];

        public string Title => Pack?.Name ?? string.Empty;

        /// <summary>On when every mod is on, off when none is, and part-way (shown as a filled box) otherwise.</summary>
        public bool? OnState => Rows.All(row => row.Enabled) ? true : Rows.Any(row => row.Enabled) ? null : false;

        public string Summary => Rows.Count(row => row.Enabled) switch
        {
            0 => $"Pack of {Rows.Count:N0} mods · all off",
            var on when on == Rows.Count => $"Pack of {Rows.Count:N0} mods · all on",
            var on => $"Pack of {Rows.Count:N0} mods · {on:N0} on",
        };

        /// <summary>Set when a mod of this pack changes the same assets as an enabled mod outside it.</summary>
        public string Warning { get; set; } = string.Empty;

        public bool IsExpanded { get; set; } = !(pack?.Collapsed ?? false);
    }

    private sealed class ModListGrouping : GroupDescription
    {
        public override object GroupNameFromItem(object item, int level, System.Globalization.CultureInfo culture) =>
            ((ModLibraryRow)item).Group;
    }

    /// <summary>A mod, or a pack counted as one, on one side of an overlap.</summary>
    private sealed record ModOverlapSide(string ModId, string ModName, int Priority, ArkiveModPack? Pack)
    {
        public string Label => Pack is null ? ModName : $"the {Pack.Name} pack";

        public string SentenceLabel => Pack is null ? ModName : $"The {Pack.Name} pack";
    }

    /// <summary>
    /// One line of the overlap warning above the Mods list. Mods of one pack that overlap the same mod share a line,
    /// so a pack reads as one switch here too; a tie is always about two single mods.
    /// </summary>
    private sealed record ModOverlapRow(ModOverlapSide Winner, ModOverlapSide Other, bool Tied, IReadOnlyList<string> SharedAssets)
    {
        public string Text => Tied
            ? $"{Winner.SentenceLabel} and {Other.Label} both change {AssetCount} and have the same priority " +
              $"({Winner.Priority:N0}). Choose which one wins before applying."
            : Winner.Pack is null && Other.Pack is null
                ? $"{Winner.SentenceLabel} and {Other.Label} both change {AssetCount}. {Winner.SentenceLabel} wins them " +
                  $"because its priority is higher ({Winner.Priority:N0} over {Other.Priority:N0})."
                : $"{Winner.SentenceLabel} and {Other.Label} both change {AssetCount}. {Winner.SentenceLabel} wins them " +
                  "because it has the higher priority.";

        public string MakeOtherWinLabel => $"Make {Other.Label} win";

        public string MakeWinnerWinLabel => $"Make {Winner.Label} win";

        public Visibility TiedVisibility => Tied ? Visibility.Visible : Visibility.Collapsed;

        public string ToolTip => "Shared assets: " + string.Join(", ", SharedAssets.Take(12)) +
                                 (SharedAssets.Count > 12 ? $" and {SharedAssets.Count - 12:N0} more" : string.Empty);

        private string AssetCount => SharedAssets.Count == 1 ? "1 asset" : $"{SharedAssets.Count:N0} assets";
    }

    private sealed record ModConflictRow(ArkiveModPairConflict Conflict)
    {
        public string OtherMod => Conflict.OtherModName;

        public string OtherState => Conflict.OtherEnabled ? "Enabled" : "Disabled";

        public string OtherPriority => Conflict.OtherPriority.ToString("N0");

        public string SharedCount => Conflict.SharedObjects.Count.ToString("N0");

        public string Objects => string.Join(", ", Conflict.SharedObjects);

        public string Outcome => Conflict.Outcome;
    }

    private sealed record ModPlanRow(ArkiveModPackagePlan Plan)
    {
        public string Package => $"{Plan.PackageAlias} ({Plan.PhysicalPackageName})";

        public string Action => Plan.Describe;

        public string Contributors => Plan.Contributors.Count == 0
            ? "—"
            : string.Join(", ", Plan.Contributors.Select(item => item.IsDraft
                ? $"{item.ObjectName} (draft)"
                : $"{item.ObjectName} ← {item.ModName}"));

        public string Setup => Plan.Setups.Count == 0 ? "—" : $"{Plan.Setups.Count:N0} asset(s)";

        public string Reason => Plan.Reason;
    }

    private static string? ExistingDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? Path.GetFullPath(path) : null;
}
