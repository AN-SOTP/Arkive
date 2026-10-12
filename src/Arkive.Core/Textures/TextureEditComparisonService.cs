namespace Arkive.Core.Textures;

public sealed record TextureEditComparisonResult(
    string OriginalImagePath,
    string EditedImagePath,
    string DifferenceImagePath,
    int Width,
    int Height,
    long ChangedPixels,
    double ChangedPercent,
    int MaximumChannelDelta);

public static class TextureEditComparisonService
{
    public static async Task<TextureEditComparisonResult> CompareAsync(
        string originalImagePath,
        string editedImagePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var originalPath = Path.GetFullPath(originalImagePath);
        var editedPath = Path.GetFullPath(editedImagePath);
        if (!File.Exists(originalPath))
        {
            throw new FileNotFoundException("The original editable texture is missing.", originalPath);
        }
        if (!File.Exists(editedPath))
        {
            throw new FileNotFoundException("The edited texture is missing.", editedPath);
        }

        var original = await PngRgbaCodec.ReadAsync(originalPath, cancellationToken).ConfigureAwait(false);
        var edited = await PngRgbaCodec.ReadAsync(editedPath, cancellationToken).ConfigureAwait(false);
        if (original.Width != edited.Width || original.Height != edited.Height)
        {
            throw new NotSupportedException(
                $"The edited image is {edited.Width}x{edited.Height}; expected {original.Width}x{original.Height}.");
        }

        var differencePixels = new byte[original.Pixels.Length];
        long changed = 0;
        var maximumDelta = 0;
        for (var pixel = 0; pixel < original.Width * original.Height; pixel++)
        {
            var offset = pixel * 4;
            var pixelChanged = false;
            for (var channel = 0; channel < 4; channel++)
            {
                var delta = Math.Abs(original.Pixels[offset + channel] - edited.Pixels[offset + channel]);
                maximumDelta = Math.Max(maximumDelta, delta);
                differencePixels[offset + channel] = channel == 3
                    ? (byte)255
                    : (byte)Math.Min(255, delta * 4);
                pixelChanged |= delta != 0;
            }
            if (pixelChanged) changed++;
        }

        Directory.CreateDirectory(outputDirectory);
        var differencePath = Path.Combine(Path.GetFullPath(outputDirectory), "difference-amplified.png");
        await PngRgbaCodec.WriteAsync(
            differencePath,
            new RgbaImage(original.Width, original.Height, differencePixels),
            cancellationToken).ConfigureAwait(false);
        var total = checked((long)original.Width * original.Height);
        return new TextureEditComparisonResult(
            originalPath,
            editedPath,
            differencePath,
            original.Width,
            original.Height,
            changed,
            total == 0 ? 0 : changed * 100d / total,
            maximumDelta);
    }
}
