namespace Arkive.Core.Mods;

public enum UnifiedGuidedWorkflowStage
{
    Find,
    Edit,
    Build,
    Deploy,
}

public sealed record UnifiedGuidedWorkflowSnapshot(
    bool HasSearchSelection,
    bool HasPackageProject,
    int IncludedAssetCount,
    bool HasCommittedBuild,
    bool BuildOutdated,
    bool CanInstall,
    bool CanRestore);

public sealed record UnifiedGuidedWorkflowRecommendation(
    UnifiedGuidedWorkflowStage Stage,
    string Heading,
    string Guidance,
    string PrimaryAction);

/// <summary>
/// Small deterministic state machine shared by the simple workshop UI. Native
/// validation remains in the existing build/deployment services; this class
/// only chooses the safest next user-facing action.
/// </summary>
public sealed class UnifiedGuidedWorkflowService
{
    public UnifiedGuidedWorkflowRecommendation Recommend(UnifiedGuidedWorkflowSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.HasPackageProject)
        {
            return state.HasSearchSelection
                ? new(UnifiedGuidedWorkflowStage.Edit, "Create or open the edit",
                    "The asset is selected. Open its texture, material, or mesh workflow.",
                    "Start / open editor")
                : new(UnifiedGuidedWorkflowStage.Find, "Find an asset",
                    "Search by an in-game name or technical object name.", "Search");
        }

        if (state.IncludedAssetCount <= 0)
            return new(UnifiedGuidedWorkflowStage.Edit, "Choose project assets",
                "Check at least one compatible edit for this Package Project.",
                "Check an asset");
        if (!state.HasCommittedBuild || state.BuildOutdated)
            return new(UnifiedGuidedWorkflowStage.Build, "Rebuild the UPK",
                state.HasCommittedBuild
                    ? "A checked edit changed after the latest package build."
                    : "The checked edits have not been composed into a package yet.",
                "Rebuild checked assets");
        if (state.CanRestore)
            return new(UnifiedGuidedWorkflowStage.Deploy, "Mod installed",
                "The modified package is active and its verified original can be restored.",
                "Restore original");
        if (state.CanInstall)
            return new(UnifiedGuidedWorkflowStage.Deploy, "Ready to install",
                "The latest package build passed its validation and deployment gates.",
                "Install mod");
        return new(UnifiedGuidedWorkflowStage.Build, "Review the package build",
            "The latest build is retained but is not currently installable. Open Package Projects for its validation details.",
            "Open advanced Package Projects");
    }
}
