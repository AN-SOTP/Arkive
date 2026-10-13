namespace Arkive.Core.Viewer;

/// <summary>
/// A modder's working changes to show on top of an object's original game data: the edited mesh, edited
/// texture images and changed material values. The viewer re-reads these files whenever they change.
/// </summary>
/// <param name="Label">What the edits belong to (for example the mod's name).</param>
/// <param name="MeshGlbPath">The edited GLB of the viewed mesh, when it has one.</param>
/// <param name="TextureImages">Edited texture images (PNG or DDS) by texture object name.</param>
/// <param name="Materials">Changed material instance values by material name.</param>
/// <param name="WatchedFiles">Further files whose change should refresh the view (edit documents).</param>
public sealed record ModelViewerEdits(
    string Label,
    string? MeshGlbPath,
    IReadOnlyDictionary<string, string> TextureImages,
    IReadOnlyDictionary<string, ModelViewerMaterialEdit> Materials,
    IReadOnlyList<string> WatchedFiles)
{
    public bool IsEmpty => MeshGlbPath is null && TextureImages.Count == 0 && Materials.Count == 0;

    /// <summary>Every file the edits read, for watching.</summary>
    public IEnumerable<string> Files =>
        new[] { MeshGlbPath }.OfType<string>().Concat(TextureImages.Values).Concat(WatchedFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Material instance values a modder changed; texture parameters name the texture object they now use.</summary>
public sealed record ModelViewerMaterialEdit(
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, float[]> Vectors,
    IReadOnlyDictionary<string, string> Textures);

/// <summary>What the viewer shows from a modder's edits.</summary>
/// <param name="Applied">One line per edit that changed the view.</param>
/// <param name="Problems">Edits that could not be shown, and why.</param>
public sealed record ModelViewerEditSummary(string Label, IReadOnlyList<string> Applied, IReadOnlyList<string> Problems);
