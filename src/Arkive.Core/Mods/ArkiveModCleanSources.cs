using System.Text.Json;
using Arkive.Core.Materials;
using Arkive.Core.Particles;
using Arkive.Core.Physics;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

/// <summary>
/// Points working copies back at the clean backup of their game package when the game package they were made from has
/// since had a mod installed into it. Applying a mod prepares its working copies against the game package and then
/// rebuilds that package, so without this the next save of the same mod refuses ("the source package changed").
/// </summary>
/// <remarks>
/// A copy is only moved when the backup is byte-for-byte the file the copy was made from (same SHA-256), so the move
/// changes where Arkive reads the original, never what the original is. Meshes and interface movies are left alone:
/// they keep their own source checks and are re-prepared by Apply.
/// </remarks>
public static class ArkiveModCleanSources
{
    /// <summary>Resolves the clean copy of a game package (the package itself when no mod is installed in it).</summary>
    public delegate Task<string> Resolver(string packagePath, CancellationToken cancellationToken);

    /// <summary>Moves every stale working copy in <paramref name="editIds"/> to its clean backup; returns how many moved.</summary>
    public static async Task<int> RepointAsync(
        ArkiveModEditWorkspaces edits,
        IEnumerable<string> editIds,
        Resolver resolve,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(resolve);
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(edits, cancellationToken).ConfigureAwait(false);
        var moved = 0;
        foreach (var editId in editIds.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Find(editId) is not { } edit) continue;
            try
            {
                var changed = edit.Entry switch
                {
                    GuidedTextureModProjectEntry texture => await RepointTextureAsync(edits, texture, resolve, cancellationToken)
                        .ConfigureAwait(false),
                    MaterialInstanceEditEntry material => await RepointMaterialAsync(edits, material, resolve, cancellationToken)
                        .ConfigureAwait(false),
                    ParticleEffectEditEntry particle => await RepointParticleAsync(edits, particle, resolve, cancellationToken)
                        .ConfigureAwait(false),
                    PhysicsAssetEditEntry physics => await RepointPhysicsAsync(edits, physics, resolve, cancellationToken)
                        .ConfigureAwait(false),
                    _ => false,
                };
                if (changed) moved++;
            }
            catch (Exception exception) when (exception is ArkiveModRefusalException or IOException or JsonException or
                                                  InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                // No verified backup (or an unreadable copy): leave it, and let the save report the copy's own problem.
            }
        }
        return moved;
    }

    private static async Task<bool> RepointTextureAsync(ArkiveModEditWorkspaces edits, GuidedTextureModProjectEntry texture,
        Resolver resolve, CancellationToken cancellationToken)
    {
        var manifestPath = texture.Manifest.NativeTextureManifestPath;
        var native = await NativeTextureExportService.LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var source = new FileInfo(native.SourcePackagePath);
        if (source.Exists && source.Length == native.SourcePackageLength &&
            source.LastWriteTimeUtc.Ticks == native.SourcePackageLastWriteUtcTicks) return false;
        if (await CleanCopyAsync(native.SourcePackagePath, native.SourcePackageSha256, resolve, cancellationToken)
                .ConfigureAwait(false) is not { } clean) return false;
        var updated = native with
        {
            SourcePackagePath = clean.FullName,
            SourcePackageLength = clean.Length,
            SourcePackageLastWriteUtcTicks = clean.LastWriteTimeUtc.Ticks,
        };
        // A new manifest beside the old one: its DDS path is relative to the folder, and the old record stays as it was.
        var updatedPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!,
            $"arkive-native-texture.clean-{Guid.NewGuid():N}.json");
        await NativeTextureExportService.SaveManifestAsync(updatedPath, updated, cancellationToken).ConfigureAwait(false);
        await edits.Textures.RebaseSourceAsync(texture.ManifestPath, updated, updatedPath, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> RepointMaterialAsync(ArkiveModEditWorkspaces edits, MaterialInstanceEditEntry material,
        Resolver resolve, CancellationToken cancellationToken)
    {
        var document = material.Document;
        if (await IsSameFileAsync(document.SourcePackagePath, document.SourcePackageSha256, cancellationToken).ConfigureAwait(false))
            return false;
        if (await CleanCopyAsync(document.SourcePackagePath, document.SourcePackageSha256, resolve, cancellationToken)
                .ConfigureAwait(false) is not { } clean) return false;
        MaterialInstanceReconnaissanceReport report;
        await using (var stream = File.OpenRead(document.SourceReconnaissancePath))
            report = await JsonSerializer.DeserializeAsync<MaterialInstanceReconnaissanceReport>(stream,
                         MaterialInstanceWorkspaceStore.SerializerOptions, cancellationToken).ConfigureAwait(false) ??
                     throw new InvalidDataException("The material reconnaissance record was empty.");
        var updated = report with
        {
            PackagePath = clean.FullName,
            PackageLength = clean.Length,
            PackageLastWriteUtcTicks = clean.LastWriteTimeUtc.Ticks,
        };
        var updatedPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(document.SourceReconnaissancePath))!,
            $"material-reconnaissance.clean-{Guid.NewGuid():N}.json");
        await MaterialInstanceWorkspaceStore.SaveJsonAtomicAsync(updatedPath, updated, cancellationToken).ConfigureAwait(false);
        await edits.MaterialEdits.RebaseSourceAsync(material, updated, updatedPath, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> RepointParticleAsync(ArkiveModEditWorkspaces edits, ParticleEffectEditEntry particle,
        Resolver resolve, CancellationToken cancellationToken)
    {
        var document = particle.Document;
        if (await IsSameFileAsync(document.SourcePackagePath, document.SourcePackageSha256, cancellationToken).ConfigureAwait(false))
            return false;
        if (await CleanCopyAsync(document.SourcePackagePath, document.SourcePackageSha256, resolve, cancellationToken)
                .ConfigureAwait(false) is not { } clean) return false;
        await edits.ParticleEdits.SaveAsync(particle with { Document = document with { SourcePackagePath = clean.FullName } },
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> RepointPhysicsAsync(ArkiveModEditWorkspaces edits, PhysicsAssetEditEntry physics,
        Resolver resolve, CancellationToken cancellationToken)
    {
        var document = physics.Document;
        if (await IsSameFileAsync(document.SourcePackagePath, document.SourcePackageSha256, cancellationToken).ConfigureAwait(false))
            return false;
        if (await CleanCopyAsync(document.SourcePackagePath, document.SourcePackageSha256, resolve, cancellationToken)
                .ConfigureAwait(false) is not { } clean) return false;
        await edits.PhysicsEdits.SaveAsync(physics with { Document = document with { SourcePackagePath = clean.FullName } },
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> IsSameFileAsync(string path, string sha256, CancellationToken cancellationToken) =>
        File.Exists(path) && (await ArkiveModEditSnapshot.HashFileCachedAsync(path, cancellationToken).ConfigureAwait(false))
            .Equals(sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>The clean backup of <paramref name="packagePath"/> when it is exactly the file with <paramref name="sha256"/>.</summary>
    private static async Task<FileInfo?> CleanCopyAsync(string packagePath, string sha256, Resolver resolve,
        CancellationToken cancellationToken)
    {
        var clean = await resolve(packagePath, cancellationToken).ConfigureAwait(false);
        if (string.Equals(Path.GetFullPath(clean), Path.GetFullPath(packagePath), StringComparison.OrdinalIgnoreCase) ||
            !await IsSameFileAsync(clean, sha256, cancellationToken).ConfigureAwait(false)) return null;
        return new FileInfo(clean);
    }
}
