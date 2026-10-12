using System.Security.Cryptography;
using Arkive.Core.Textures;

namespace Arkive.Core.Inspection;

internal sealed class PngArtifactInspector
{
    public async Task<ArtifactInspectionResult> InspectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        try
        {
            var image = await PngRgbaCodec.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            return new ArtifactInspectionResult(
                file.FullName,
                "PNG",
                $"{image.Width} x {image.Height} editable RGBA image",
                [
                    new ArtifactInspectionProperty("File", "Name", file.Name),
                    new ArtifactInspectionProperty("File", "Size", $"{file.Length:N0} bytes"),
                    new ArtifactInspectionProperty("File", "SHA-256", hash),
                    new ArtifactInspectionProperty("Image", "Dimensions", $"{image.Width} x {image.Height}"),
                    new ArtifactInspectionProperty("Image", "Decoded channels", "8-bit RGBA"),
                    new ArtifactInspectionProperty("Image", "Mip levels", "1 (Arkive regenerates the native mip chain on import)"),
                ],
                [],
                []);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or NotSupportedException)
        {
            return new ArtifactInspectionResult(
                file.FullName,
                "PNG",
                "Invalid or unsupported PNG",
                [
                    new ArtifactInspectionProperty("File", "Name", file.Name),
                    new ArtifactInspectionProperty("File", "Size", $"{file.Length:N0} bytes"),
                ],
                [],
                [
                    new ArtifactInspectionIssue(
                        ArtifactInspectionSeverity.Error,
                        "PNG_DECODE_FAILED",
                        exception.Message),
                ]);
        }
    }
}
