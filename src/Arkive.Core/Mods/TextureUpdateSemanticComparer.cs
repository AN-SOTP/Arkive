using Arkive.Core.Inspection;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

public sealed record TextureUpdateSemanticComparison(
    bool IsEquivalent,
    string Code,
    string Message);

/// <summary>
/// Proves that a Texture2D changed only in package-relative serialization data.
/// Absolute offsets and raw name-table references may move during a client
/// recook; every value that controls the texture and every stored mip payload
/// must remain identical before automatic rebase is allowed.
/// </summary>
public static class TextureUpdateSemanticComparer
{
    public static TextureUpdateSemanticComparison Compare(
        NativeTextureExportManifest source,
        TextureReconnaissanceReport previous,
        TextureReconnaissanceReport current)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        if (!source.SourcePayloadSha256.Equals(
                previous.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            !source.SourcePackageSha256.Equals(
                previous.PackageSha256, StringComparison.OrdinalIgnoreCase))
            return Refuse("CTS001",
                "The retained native texture manifest is not backed by its referenced reconnaissance report.");

        if (!SameIdentity(source.ClassName, source.ObjectName, previous) ||
            !SameIdentity(source.ClassName, source.ObjectName, current))
            return Refuse("CTS002",
                "The class/object identity differs from the retained native texture source.");

        if (!NativeTextureExportService.TryValidateReconnaissanceGate(
                previous, out var previousFormat, out var previousGate))
            return Refuse("CTS003", $"The retained source no longer passes the native texture gate: {previousGate}");
        if (!NativeTextureExportService.TryValidateReconnaissanceGate(
                current, out var currentFormat, out var currentGate))
            return Refuse("CTS004", $"The updated texture does not pass the native texture gate: {currentGate}");
        if (previousFormat is null || currentFormat is null ||
            !previousFormat.ManifestPixelFormat.Equals(
                currentFormat.ManifestPixelFormat, StringComparison.OrdinalIgnoreCase) ||
            !source.PixelFormat.Equals(
                currentFormat.ManifestPixelFormat, StringComparison.OrdinalIgnoreCase))
            return Refuse("CTS005", "The verified native pixel format changed.");

        if (!TryReadDimensions(previous, out var previousDimensions) ||
            !TryReadDimensions(current, out var currentDimensions))
            return Refuse("CTS006", "One or more required texture dimensions could not be verified.");
        var sourceDimensions = new TextureDimensions(
            source.Width, source.Height, source.OriginalWidth, source.OriginalHeight);
        if (previousDimensions != sourceDimensions || currentDimensions != sourceDimensions)
            return Refuse("CTS007",
                $"The texture dimensions changed from {source.Width}x{source.Height} " +
                $"(original {source.OriginalWidth}x{source.OriginalHeight}).");

        var previousProperties = SemanticProperties(previous);
        var currentProperties = SemanticProperties(current);
        if (!previousProperties.SequenceEqual(currentProperties, StringComparer.Ordinal))
            return Refuse("CTS008",
                "One or more serialized Texture2D property names, types, or semantic values changed.");

        if (previous.MipCandidates.Count != 1 || current.MipCandidates.Count != 1)
            return Refuse("CTS009", "A unique native mip chain was not available on both sides of the update.");
        var previousChain = previous.MipCandidates[0];
        var currentChain = current.MipCandidates[0];
        if (source.MipCount != source.Mips.Count ||
            previousChain.Mips.Count != source.MipCount ||
            currentChain.Mips.Count != source.MipCount ||
            previousChain.DeclaredMipCount != source.MipCount ||
            currentChain.DeclaredMipCount != source.MipCount)
            return Refuse("CTS010", $"The verified mip count changed from {source.MipCount:N0}.");

        for (var index = 0; index < source.MipCount; index++)
        {
            var retained = source.Mips[index];
            var before = previousChain.Mips[index];
            var after = currentChain.Mips[index];
            if (!MatchesRetainedMip(retained, before))
                return Refuse("CTS011",
                    $"Retained mip {index:N0} evidence does not match its source reconnaissance report.");
            if (after.MipIndex != retained.MipIndex ||
                after.Width != retained.Width || after.Height != retained.Height ||
                after.BulkDataFlags != retained.BulkDataFlags ||
                after.StoredSize != retained.StoredPayloadBytes ||
                after.ElementCount != before.ElementCount ||
                !after.Storage.Equals(before.Storage, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(after.PayloadSha256) ||
                !after.PayloadSha256.Equals(
                    retained.StoredPayloadSha256, StringComparison.OrdinalIgnoreCase))
                return Refuse("CTS012",
                    $"Mip {index:N0} changed dimensions, storage layout, byte count, or stored payload content.");
        }

        return new TextureUpdateSemanticComparison(
            true,
            "CTS000",
            $"The serialized export moved or was rewritten, but all {source.MipCount:N0} verified mip payloads, " +
            "Texture2D property values, dimensions, and format are identical. Automatic source rebase is safe.");
    }

    private static bool SameIdentity(
        string className,
        string objectName,
        TextureReconnaissanceReport report) =>
        report.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase) &&
        report.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesRetainedMip(
        NativeTextureMipExport retained,
        TextureMipEvidence evidence) =>
        evidence.MipIndex == retained.MipIndex &&
        evidence.Width == retained.Width && evidence.Height == retained.Height &&
        evidence.BulkDataFlags == retained.BulkDataFlags &&
        evidence.StoredSize == retained.StoredPayloadBytes &&
        !string.IsNullOrWhiteSpace(evidence.PayloadSha256) &&
        evidence.PayloadSha256.Equals(
            retained.StoredPayloadSha256, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> SemanticProperties(TextureReconnaissanceReport report) =>
        report.Properties
            .Select(property => string.Join('\u001F',
                property.Name,
                property.Type,
                property.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                property.ArrayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                property.TypeMetadata ?? string.Empty,
                property.ValuePreview,
                property.NumericValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static bool TryReadDimensions(
        TextureReconnaissanceReport report,
        out TextureDimensions dimensions)
    {
        dimensions = default;
        if (!TryPositiveInt(report, "SizeX", out var width) ||
            !TryPositiveInt(report, "SizeY", out var height) ||
            !TryPositiveInt(report, "OriginalSizeX", out var originalWidth) ||
            !TryPositiveInt(report, "OriginalSizeY", out var originalHeight))
            return false;
        dimensions = new TextureDimensions(width, height, originalWidth, originalHeight);
        return true;
    }

    private static bool TryPositiveInt(
        TextureReconnaissanceReport report,
        string name,
        out int value)
    {
        value = 0;
        var property = report.Properties.SingleOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (property?.NumericValue is not { } numeric || numeric is <= 0 or > 65_536)
            return false;
        value = checked((int)numeric);
        return true;
    }

    private static TextureUpdateSemanticComparison Refuse(string code, string message) =>
        new(false, code, message);

    private readonly record struct TextureDimensions(
        int Width,
        int Height,
        int OriginalWidth,
        int OriginalHeight);
}
