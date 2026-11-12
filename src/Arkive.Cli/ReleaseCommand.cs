using System.Text.Json;
using Arkive.Core.Meshes;
using Arkive.Core.Mods;

/// <summary>
/// arkive-release: the window's Release working files, for one mod of a data folder. Deletes a disabled mod's working
/// files, which Apply sets up again from its .arkive file when the mod is next turned on.
/// </summary>
internal static class ReleaseCommand
{
    public static int Run(string dataRoot, ArkiveModApplyContext context, string? modReference,
        Func<ArkiveModLibraryManifest, string?, ArkiveModLibraryEntry> resolve, bool dryRun, bool json)
    {
        var root = Path.GetFullPath(dataRoot);
        var mod = resolve(context.Library.LoadAsync().GetAwaiter().GetResult(), modReference);
        var author = new ArkiveModAuthoringService(context.Library, context.Edits)
        {
            MeshExports = new SkeletalMeshEditableWorkspaceStore(Path.Combine(root, "staging", "skeletal-mesh-reconnaissance")),
        };
        var plan = author.PlanReleaseAsync(mod.ModId, context).GetAwaiter().GetResult();
        ArkiveModRelease? release = null;
        if (!dryRun && plan.Problems.Count == 0)
            release = author.ReleaseWorkingFilesAsync(mod.ModId, context).GetAwaiter().GetResult();
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                plan.ModId, plan.ModName, DryRun = dryRun, CanRelease = plan.Problems.Count == 0, plan.Problems,
                WorkingFiles = plan.EditIds.Count, Folders = plan.Folders, plan.Bytes,
                Released = release is null ? null : new
                {
                    release.DeletedEditIds, release.KeptEditIds, release.DeletedExports, release.DeletedDraftIds,
                    release.DeletedUnpackedFolders,
                },
            }, new JsonSerializerOptions { WriteIndented = true }));
            return plan.Problems.Count == 0 ? 0 : 1;
        }
        Console.WriteLine($"{plan.ModName}: {plan.EditIds.Count:N0} working file(s), {plan.Folders.Count:N0} folder(s), " +
                          $"{ArkiveStorageCleanup.FormatBytes(plan.Bytes)}.");
        if (plan.Problems.Count > 0)
        {
            Console.WriteLine("Nothing was released:");
            foreach (var problem in plan.Problems) Console.WriteLine($"  {problem}");
            return 1;
        }
        foreach (var folder in plan.Folders) Console.WriteLine($"  {(dryRun ? "Would delete" : "Deleted")} {folder}");
        if (release is not null)
        {
            Console.WriteLine($"Released {release.DeletedEditIds.Count:N0} working file(s); " +
                              $"{release.KeptEditIds.Count:N0} could not be deleted and stay set up.");
            Console.WriteLine("The mod is set up again from its .arkive file when it is next applied.");
        }
        return 0;
    }
}
