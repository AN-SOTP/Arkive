using System.Security.Cryptography;
using Arkive.Core.Meshes;
using Arkive.Core.StaticMeshes;

namespace Arkive.Core.Mods;

/// <summary>The result of a working mesh's last check (mesh preflight): whether it can be saved into a mod, and why not.</summary>
public sealed record ArkiveModEditCheckResult(bool Passed, IReadOnlyList<string> Problems);

/// <summary>
/// Reads the last mesh check of a working asset and tells whether its edited file changed since (for example after
/// saving it again in Blender), so Edit mods can show problems before the mod is saved.
/// </summary>
public static class ArkiveModEditCheck
{
    /// <summary>The last check of a mesh edit, or null for assets that have none (textures, materials).</summary>
    public static ArkiveModEditCheckResult? Read(ArkiveModLocalEdit edit) => edit.Entry switch
    {
        SkeletalMeshEditWorkspaceEntry mesh => new ArkiveModEditCheckResult(
            mesh.Manifest.Preflight.Readiness != SkeletalMeshEditReadiness.Refused && mesh.Manifest.Preflight.ErrorCount == 0,
            mesh.Manifest.Preflight.Issues.Where(issue => issue.Severity == SkeletalMeshEditIssueSeverity.Error)
                .Select(issue => $"{issue.Code}: {issue.Message}").ToArray()),
        StaticMeshEditEntry mesh => new ArkiveModEditCheckResult(
            mesh.Manifest.Preflight.Readiness != StaticMeshEditReadiness.Refused,
            mesh.Manifest.Preflight.Readiness == StaticMeshEditReadiness.Refused ? mesh.Manifest.Preflight.Issues : []),
        _ => null,
    };

    /// <summary>The edited mesh file and the hash it had when last checked, or null for assets without a mesh check.</summary>
    public static (string Path, string CheckedSha256)? CheckedFile(ArkiveModLocalEdit edit) => edit.Entry switch
    {
        SkeletalMeshEditWorkspaceEntry mesh => (mesh.EditedGlbPath, mesh.Manifest.EditedGlbSha256),
        StaticMeshEditEntry mesh => (mesh.Manifest.EditedGlbPath, mesh.Manifest.EditedGlbSha256),
        _ => null,
    };

    /// <summary>True when the edited mesh file changed after its last check.</summary>
    public static async Task<bool> IsStaleAsync(ArkiveModLocalEdit edit, CancellationToken cancellationToken = default)
    {
        if (CheckedFile(edit) is not { } file || !File.Exists(file.Path)) return false;
        return !(await Sha256Async(file.Path, cancellationToken).ConfigureAwait(false))
            .Equals(file.CheckedSha256, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
