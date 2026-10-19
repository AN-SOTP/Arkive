using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

/// <summary>
/// The one entry point for recolouring many of a mod's working copies at once (a map's textures and materials, say).
/// Callers add the assets to the mod as normal working copies first, then pass those edits with the colour change.
/// </summary>
public interface IMaterialTint
{
    Task<MaterialTintResult> TintAsync(
        MaterialTintRequest request,
        IProgress<MaterialTintProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <param name="Edits">The mod's working copies to recolour.</param>
/// <param name="Tint">Which colours change and to what, as the Texture Viewer picks them.</param>
public sealed record MaterialTintRequest(IReadOnlyList<ArkiveModLocalEdit> Edits, TextureRecolour Tint);

public sealed record MaterialTintProgress(int Completed, int Total, string ObjectName);

/// <param name="Changed">Working copies whose colours changed.</param>
/// <param name="Unchanged">Working copies with no colour the tint selects.</param>
/// <param name="Skipped">Working copies the tint could not change, and why.</param>
public sealed record MaterialTintResult(int Changed, int Unchanged, IReadOnlyList<MaterialTintSkip> Skipped);

public sealed record MaterialTintSkip(string ObjectName, string Reason);
