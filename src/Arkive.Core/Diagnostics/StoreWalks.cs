using System.Collections.Concurrent;

namespace Arkive.Core.Diagnostics;

/// <summary>
/// Counts each time a store of working files or package projects is walked (its folders listed and every record read
/// or checked), per store, so measuring runs can show how often launch reads each one. Counting costs nothing worth
/// noticing; the counts only reach measurement results.
/// </summary>
public static class StoreWalks
{
    public const string Textures = "textures";
    public const string SkeletalMeshEdits = "skeletal-mesh-edits";
    public const string SkeletalMeshExports = "skeletal-mesh-exports";
    public const string StaticMeshEdits = "static-mesh-edits";
    public const string MaterialEdits = "material-edits";
    public const string ParticleEdits = "particle-edits";
    public const string PhysicsEdits = "physics-edits";
    public const string InterfaceMovieEdits = "interface-movie-edits";
    public const string PackageProjects = "package-projects";
    public const string ExportSessions = "export-sessions";

    private static readonly ConcurrentDictionary<string, int> Counts = new(StringComparer.Ordinal);

    public static void Record(string store) => Counts.AddOrUpdate(store, 1, static (_, count) => count + 1);

    /// <summary>The walks counted so far, per store.</summary>
    public static IReadOnlyDictionary<string, int> Current() => new SortedDictionary<string, int>(Counts, StringComparer.Ordinal);

    /// <summary>The walks counted since <paramref name="before"/>, per store, or null when there were none.</summary>
    public static IReadOnlyDictionary<string, int>? Since(IReadOnlyDictionary<string, int> before)
    {
        ArgumentNullException.ThrowIfNull(before);
        var since = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (store, count) in Counts)
        {
            var walks = count - before.GetValueOrDefault(store);
            if (walks > 0) since[store] = walks;
        }
        return since.Count == 0 ? null : since;
    }
}
