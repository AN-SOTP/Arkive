namespace Arkive.Core.Inspection;

public sealed class ExportArtifactInspector
{
    private readonly DdsArtifactInspector _ddsInspector = new();
    private readonly PngArtifactInspector _pngInspector = new();
    private readonly GltfArtifactInspector _gltfInspector = new();

    public static bool IsSupported(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".dds" or ".png" or ".gltf" or ".glb";

    public async Task<ArtifactInspectionResult> InspectAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The exported artifact was not found.", fullPath);
        }

        return Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".dds" => await _ddsInspector.InspectAsync(fullPath, cancellationToken)
                .ConfigureAwait(false),
            ".png" => await _pngInspector.InspectAsync(fullPath, cancellationToken)
                .ConfigureAwait(false),
            ".gltf" or ".glb" => await _gltfInspector.InspectAsync(fullPath, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new NotSupportedException(
                $"Arkive does not inspect {Path.GetExtension(fullPath)} files yet."),
        };
    }
}
