using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Inspection;

namespace Arkive.Core.Textures;

public enum TextureImportAlphaPolicy
{
    PreserveOriginal,
    UseEdited,
    ForceOpaque,
}

public enum TextureImportPreflightStatus
{
    Passed,
    Information,
    Warning,
}

public sealed record TextureImportPreflightCheck(
    string Code,
    TextureImportPreflightStatus Status,
    string Message);

public sealed record TextureImportCandidateRequest(
    string NativeTextureManifestPath,
    string EditedImagePath,
    string OutputDirectory,
    TextureImportAlphaPolicy AlphaPolicy = TextureImportAlphaPolicy.PreserveOriginal);

public sealed record TextureImportCandidateManifest(
    int SchemaVersion,
    string CandidateId,
    DateTimeOffset CreatedAt,
    string NativeTextureManifestPath,
    string SourceExportId,
    string SourcePackagePath,
    string SourcePackageSha256,
    string PackageAlias,
    int SourceExportIndex,
    int? NativeExportIndex,
    string ClassName,
    string ObjectName,
    string PixelFormat,
    string DdsFourCc,
    int Width,
    int Height,
    int MipCount,
    TextureImportAlphaPolicy AlphaPolicy,
    string AlphaAction,
    string OriginalDdsPath,
    long OriginalDdsBytes,
    string OriginalDdsSha256,
    string EditedDdsPath,
    long EditedDdsBytes,
    string EditedDdsSha256,
    string CandidateDdsRelativePath,
    long CandidateDdsBytes,
    string CandidateDdsSha256,
    long TotalBlocks,
    long EditedBlocksDifferentFromOriginal,
    long EditedColourBlocksDifferentFromOriginal,
    long EditedAlphaBlocksDifferentFromOriginal,
    long CandidateBlocksDifferentFromOriginal,
    IReadOnlyList<TextureImportPreflightCheck> PreflightChecks,
    int CandidateInspectionErrors,
    int CandidateInspectionWarnings,
    double DurationSeconds,
    string EditedInputFormat = "Legacy block-compressed DDS",
    int EditedInputMipCount = 1,
    int GeneratedMipCount = 0,
    long EditedPixels = 0,
    long TouchedBlocks = 0,
    long UnchangedBlocksCopied = 0,
    bool AutomaticNormalization = false,
    string TextureSemantic = "Colour",
    int UpscaleLevels = 0);

public sealed record TextureImportCandidateResult(
    string OutputDirectory,
    string CandidateDdsPath,
    string ManifestPath,
    TextureImportCandidateManifest Manifest,
    IReadOnlyList<string> OutputFiles,
    ArtifactInspectionResult OriginalInspection,
    ArtifactInspectionResult EditedInspection,
    ArtifactInspectionResult CandidateInspection,
    TimeSpan Duration);

public sealed class TextureImportCandidateRefusalException : NotSupportedException
{
    public TextureImportCandidateRefusalException(string code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Converts a normal editor-authored PNG or DDS into the source texture's exact BC/mip
/// layout. Original package and DDS provenance are verified; no UPK is written.
/// </summary>
public sealed class TextureImportCandidateService
{
    public const int CurrentManifestSchemaVersion = 2;
    public const int MaximumUpscaledSize = 4096;

    /// <summary>
    /// How many times <paramref name="editedWidth"/>x<paramref name="editedHeight"/> doubles the original on both
    /// sides (0 for the same size), or -1 when it is not a supported size.
    /// </summary>
    public static int UpscaleLevels(int width, int height, int editedWidth, int editedHeight)
    {
        for (var levels = 0; levels <= 2; levels++)
        {
            if (editedWidth == width << levels && editedHeight == height << levels)
                return levels == 0 || Math.Max(editedWidth, editedHeight) <= MaximumUpscaledSize ? levels : -1;
        }
        return -1;
    }
    public const int MinimumNativeTextureManifestSchemaVersion = 3;
    public const string ManifestFileName = "arkive-texture-import-candidate.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ExportArtifactInspector _artifactInspector = new();

    public static async Task<TextureImportCandidateManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = RequireFile(path, "TIC019", "texture import-candidate manifest");
        try
        {
            await using var stream = File.OpenRead(fullPath);
            var manifest = await JsonSerializer.DeserializeAsync<TextureImportCandidateManifest>(
                               stream,
                               JsonOptions,
                               cancellationToken).ConfigureAwait(false) ??
                           throw new JsonException("The texture import-candidate manifest was empty.");
            if (manifest.SchemaVersion < 1 || manifest.SchemaVersion > CurrentManifestSchemaVersion)
            {
                Refuse(
                    "TIC020",
                    $"Texture import-candidate manifest schema {manifest.SchemaVersion} is not supported; " +
                    $"Arkive supports schemas 1 through {CurrentManifestSchemaVersion}.");
            }

            return manifest;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new TextureImportCandidateRefusalException(
                "TIC021",
                $"The texture import-candidate manifest could not be read: {exception.Message}");
        }
    }

    public async Task<TextureImportCandidateResult> CreateAsync(
        TextureImportCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var manifestPath = RequireFile(
            request.NativeTextureManifestPath,
            "TIC001",
            "native texture manifest");
        var editedPath = RequireFile(request.EditedImagePath, "TIC002", "edited PNG or DDS");
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            Refuse("TIC003", "The texture import-candidate output directory must be empty.");
        }

        NativeTextureExportManifest nativeManifest;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            nativeManifest = await JsonSerializer.DeserializeAsync<NativeTextureExportManifest>(
                                 stream,
                                 JsonOptions,
                                 cancellationToken).ConfigureAwait(false) ??
                             throw new JsonException("The native texture manifest was empty.");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new TextureImportCandidateRefusalException(
                "TIC004",
                $"The native texture manifest could not be read: {exception.Message}");
        }

        var checks = new List<TextureImportPreflightCheck>();
        if (nativeManifest.SchemaVersion < MinimumNativeTextureManifestSchemaVersion ||
            nativeManifest.SchemaVersion > NativeTextureExportService.CurrentManifestSchemaVersion)
        {
            Refuse(
                "TIC005",
                $"Native texture manifest schema {nativeManifest.SchemaVersion} is not supported; " +
                $"Arkive supports schemas {MinimumNativeTextureManifestSchemaVersion} through " +
                $"{NativeTextureExportService.CurrentManifestSchemaVersion}.");
        }

        if (nativeManifest.SchemaVersion < NativeTextureExportService.CurrentManifestSchemaVersion)
        {
            checks.Add(new TextureImportPreflightCheck(
                "TIC_NATIVE_MANIFEST_COMPATIBILITY",
                TextureImportPreflightStatus.Information,
                $"Compatible legacy native-export schema {nativeManifest.SchemaVersion} was accepted and normalized."));
        }

        if (!nativeManifest.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
        {
            Refuse("TIC006", $"The manifest describes {nativeManifest.ClassName}, not Texture2D.");
        }

        var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
        var originalPath = ResolveManifestFile(
            manifestDirectory,
            nativeManifest.DdsRelativePath,
            "TIC007",
            "authoritative DDS");
        if (editedPath.Equals(originalPath, StringComparison.OrdinalIgnoreCase))
        {
            Refuse(
                "TIC008",
                "The authoritative DDS was selected as the edited image. Edit Arkive's -edit.png or another copy instead.");
        }

        var originalInfo = new FileInfo(originalPath);
        var originalHash = await HashFileAsync(originalPath, cancellationToken).ConfigureAwait(false);
        if (originalInfo.Length != nativeManifest.DdsBytes ||
            !originalHash.Equals(nativeManifest.DdsSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse(
                "TIC009",
                "The authoritative DDS no longer matches the size and SHA-256 recorded by its native export.");
        }

        checks.Add(new TextureImportPreflightCheck(
            "TIC_ORIGINAL_HASH",
            TextureImportPreflightStatus.Passed,
            $"Authoritative DDS matches {nativeManifest.DdsSha256}."));

        var sourcePackage = RequireFile(
            nativeManifest.SourcePackagePath,
            "TIC010",
            "source package recorded by the native export");
        var sourceInfo = new FileInfo(sourcePackage);
        if (sourceInfo.Length != nativeManifest.SourcePackageLength ||
            sourceInfo.LastWriteTimeUtc.Ticks != nativeManifest.SourcePackageLastWriteUtcTicks)
        {
            Refuse(
                "TIC011",
                "The source package changed after native texture export. Run reconnaissance and export again.");
        }

        var packageHash = await HashFileAsync(sourcePackage, cancellationToken).ConfigureAwait(false);
        if (!packageHash.Equals(nativeManifest.SourcePackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            Refuse(
                "TIC012",
                "The source package SHA-256 changed after native texture export. Run reconnaissance and export again.");
        }

        sourceInfo.Refresh();
        if (!sourceInfo.Exists || sourceInfo.Length != nativeManifest.SourcePackageLength ||
            sourceInfo.LastWriteTimeUtc.Ticks != nativeManifest.SourcePackageLastWriteUtcTicks)
        {
            Refuse("TIC012", "The source package changed while its SHA-256 was being verified.");
        }

        checks.Add(new TextureImportPreflightCheck(
            "TIC_PACKAGE_SNAPSHOT",
            TextureImportPreflightStatus.Passed,
            $"Source package metadata and SHA-256 still match {packageHash}."));

        DdsBlockTexture original;
        DdsBlockTexture? compatibleEditedDds = null;
        LoadedEditableTextureImage edited;
        try
        {
            original = await DdsBlockTexture.ReadAsync(originalPath, cancellationToken)
                .ConfigureAwait(false);
            edited = await TextureEditableImageService.LoadAsync(editedPath, cancellationToken)
                .ConfigureAwait(false);
            if (Path.GetExtension(editedPath).Equals(".dds", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var editedDds = await DdsBlockTexture.ReadAsync(editedPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (editedDds.Format == original.Format && editedDds.Width == original.Width &&
                        editedDds.Height == original.Height && editedDds.MipCount == original.MipCount)
                        compatibleEditedDds = editedDds;
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or NotSupportedException)
                {
                    // Uncompressed editor-authored DDS remains supported by the
                    // pixel-domain path; only compatible BC DDS can use the
                    // exact compressed-block transplant fast path.
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or NotSupportedException)
        {
            throw new TextureImportCandidateRefusalException(
                "TIC013",
                $"Edited-image decoding failed: {exception.Message}");
        }

        var parsedOriginalHash = Convert.ToHexString(SHA256.HashData(original.Bytes));
        if (!parsedOriginalHash.Equals(originalHash, StringComparison.OrdinalIgnoreCase))
        {
            Refuse("TIC009", "The authoritative DDS changed while import preflight was reading it.");
        }

        var expectedProfile = NativeTextureFormats.Supported.FirstOrDefault(profile =>
            profile.ManifestPixelFormat.Equals(nativeManifest.PixelFormat, StringComparison.OrdinalIgnoreCase));
        var expectedFormat = expectedProfile?.DdsFourCc.ToUpperInvariant() switch
        {
            "DXT1" => BlockTextureFormat.Bc1,
            "DXT5" => BlockTextureFormat.Bc3,
            "ATI2" => BlockTextureFormat.Bc5,
            _ => (BlockTextureFormat?)null,
        };
        if (expectedProfile is null || expectedFormat is null || original.Format != expectedFormat)
        {
            Refuse(
                "TIC015",
                $"The authoritative DDS {original.FourCc} does not match manifest format {nativeManifest.PixelFormat}.");
        }

        if (original.Width != nativeManifest.Width ||
            original.Height != nativeManifest.Height ||
            original.MipCount != nativeManifest.MipCount)
        {
            Refuse(
                "TIC016",
                "The authoritative DDS dimensions or mip count no longer match its native texture manifest.");
        }

        var upscaleLevels = UpscaleLevels(original.Width, original.Height, edited.Image.Width, edited.Image.Height);
        if (upscaleLevels < 0)
        {
            Refuse(
                "TIC014",
                $"The edited image is {edited.Image.Width}x{edited.Image.Height}; " +
                $"{nativeManifest.ObjectName} requires {original.Width}x{original.Height}, or that size doubled " +
                $"or quadrupled on both sides (up to {MaximumUpscaledSize}x{MaximumUpscaledSize}). " +
                "Arkive handles format, alpha, compression, and mips automatically.");
        }
        if (upscaleLevels > 0)
        {
            // The original mips become the lower levels of the larger texture, so unchanged areas keep their blocks.
            original = TextureBlockRebuilder.AddUpscaledTopMips(original, upscaleLevels);
            compatibleEditedDds = null;
            checks.Add(new TextureImportPreflightCheck(
                "TIC_UPSCALED",
                TextureImportPreflightStatus.Passed,
                $"The edited image enlarges {nativeManifest.ObjectName} from {nativeManifest.Width}x{nativeManifest.Height} " +
                $"to {original.Width}x{original.Height}; Arkive will write {original.MipCount} mips and the new size."));
        }

        checks.Add(new TextureImportPreflightCheck(
            "TIC_EDITED_IMAGE_DECODED",
            TextureImportPreflightStatus.Passed,
            $"Loaded {edited.SourceFormat} {edited.Image.Width}x{edited.Image.Height} with " +
            $"{edited.DeclaredMipCount} declared mip(s). No compression or mip options are required from the editor."));
        checks.Add(new TextureImportPreflightCheck(
            "TIC_LAYOUT_NORMALIZED",
            TextureImportPreflightStatus.Passed,
            $"Arkive will restore {nativeManifest.PixelFormat} and generate the original " +
            $"{original.MipCount}-level mip chain automatically."));

        TextureBlockRebuildResult rebuild;
        try
        {
            rebuild = compatibleEditedDds is null
                ? TextureBlockRebuilder.Rebuild(
                    original,
                    edited.Image,
                    nativeManifest.ObjectName,
                    request.AlphaPolicy,
                    cancellationToken)
                : TextureBlockRebuilder.TransplantExactDds(
                    original,
                    compatibleEditedDds,
                    nativeManifest.ObjectName,
                    cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            throw new TextureImportCandidateRefusalException(
                "TIC017",
                $"Texture normalization failed: {exception.Message}");
        }

        checks.Add(new TextureImportPreflightCheck(
            "TIC_ALPHA_POLICY",
            TextureImportPreflightStatus.Passed,
            rebuild.AlphaAction));
        if (compatibleEditedDds is not null)
            checks.Add(new TextureImportPreflightCheck(
                "TIC_EXACT_DDS_TRANSPLANT",
                TextureImportPreflightStatus.Passed,
                "The edited DDS exactly matched the target BC format, dimensions, and mip count; " +
                "all compressed mip blocks were transplanted without a pixel-domain re-encode."));
        checks.Add(new TextureImportPreflightCheck(
            "TIC_UNCHANGED_BLOCKS_PRESERVED",
            TextureImportPreflightStatus.Passed,
            $"Copied {rebuild.UnchangedBlocksCopied:N0} unchanged compressed block(s) byte-for-byte; " +
            $"only {rebuild.TouchedBlocks:N0} affected block(s) required normalization."));
        checks.Add(new TextureImportPreflightCheck(
            "TIC_EDIT_DIFFERENCE",
            rebuild.EditedPixels == 0
                ? TextureImportPreflightStatus.Warning
                : TextureImportPreflightStatus.Passed,
            rebuild.EditedPixels == 0
                ? "No edited colour/channel pixels differ from Arkive's exported edit image."
                : $"Detected {rebuild.EditedPixels:N0} edited top-level pixel(s) across " +
                  $"{rebuild.DirtyBlocks:N0} affected mip block(s)."));

        var safeObjectName = SafePathSegment(nativeManifest.ObjectName);
        var candidatePath = Path.Combine(outputDirectory, safeObjectName + "-import-candidate.dds");
        await File.WriteAllBytesAsync(candidatePath, rebuild.DdsBytes, cancellationToken)
            .ConfigureAwait(false);

        var originalInspection = await _artifactInspector
            .InspectAsync(originalPath, cancellationToken).ConfigureAwait(false);
        var editedInspection = await InspectEditedAsync(edited, cancellationToken).ConfigureAwait(false);
        var candidateInspection = await _artifactInspector
            .InspectAsync(candidatePath, cancellationToken).ConfigureAwait(false);
        if (originalInspection.ErrorCount != 0 || editedInspection.ErrorCount != 0 ||
            candidateInspection.ErrorCount != 0)
        {
            Refuse(
                "TIC018",
                $"Structural inspection found errors: original={originalInspection.ErrorCount}, " +
                $"edited={editedInspection.ErrorCount}, candidate={candidateInspection.ErrorCount}.");
        }

        var candidateInfo = new FileInfo(candidatePath);
        var candidateHash = Convert.ToHexString(SHA256.HashData(rebuild.DdsBytes));
        watch.Stop();
        var candidateManifest = new TextureImportCandidateManifest(
            CurrentManifestSchemaVersion,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            manifestPath,
            nativeManifest.ExportId,
            sourcePackage,
            packageHash,
            nativeManifest.PackageAlias,
            nativeManifest.SourceExportIndex,
            nativeManifest.NativeExportIndex,
            nativeManifest.ClassName,
            nativeManifest.ObjectName,
            nativeManifest.PixelFormat,
            original.FourCc,
            original.Width,
            original.Height,
            original.MipCount,
            request.AlphaPolicy,
            rebuild.AlphaAction,
            originalPath,
            originalInfo.Length,
            originalHash,
            edited.Path,
            new FileInfo(edited.Path).Length,
            edited.Sha256,
            Path.GetRelativePath(outputDirectory, candidatePath),
            candidateInfo.Length,
            candidateHash,
            rebuild.TotalBlocks,
            rebuild.DirtyBlocks,
            rebuild.DirtyBlocks,
            rebuild.EditedAlphaBlocks,
            rebuild.CandidateBlocksDifferentFromOriginal,
            checks,
            candidateInspection.ErrorCount,
            candidateInspection.WarningCount,
            watch.Elapsed.TotalSeconds,
            edited.SourceFormat,
            edited.DeclaredMipCount,
            original.MipCount,
            rebuild.EditedPixels,
            rebuild.TouchedBlocks,
            rebuild.UnchangedBlocksCopied,
            AutomaticNormalization: true,
            rebuild.Semantic.ToString(),
            upscaleLevels);
        var candidateManifestPath = Path.Combine(outputDirectory, ManifestFileName);
        await SaveJsonAtomicAsync(candidateManifestPath, candidateManifest, cancellationToken)
            .ConfigureAwait(false);

        var outputFiles = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new TextureImportCandidateResult(
            outputDirectory,
            candidatePath,
            candidateManifestPath,
            candidateManifest,
            outputFiles,
            originalInspection,
            editedInspection,
            candidateInspection,
            watch.Elapsed);
    }

    private async Task<ArtifactInspectionResult> InspectEditedAsync(
        LoadedEditableTextureImage edited,
        CancellationToken cancellationToken)
    {
        if (Path.GetExtension(edited.Path).Equals(".dds", StringComparison.OrdinalIgnoreCase))
        {
            return await _artifactInspector.InspectAsync(edited.Path, cancellationToken)
                .ConfigureAwait(false);
        }

        return new ArtifactInspectionResult(
            edited.Path,
            "PNG",
            $"{edited.Image.Width} x {edited.Image.Height} {edited.SourceFormat}",
            [
                new ArtifactInspectionProperty("Image", "Dimensions", $"{edited.Image.Width} x {edited.Image.Height}"),
                new ArtifactInspectionProperty("Image", "Input format", edited.SourceFormat),
                new ArtifactInspectionProperty("Image", "Declared mip levels", edited.DeclaredMipCount.ToString()),
                new ArtifactInspectionProperty("File", "SHA-256", edited.Sha256),
            ],
            [],
            []);
    }

    private static string RequireFile(string path, string code, string description)
    {
        if (string.IsNullOrWhiteSpace(path)) Refuse(code, $"No {description} path was supplied.");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) Refuse(code, $"The {description} was not found: {fullPath}");
        return fullPath;
    }

    private static string ResolveManifestFile(
        string manifestDirectory,
        string relativePath,
        string code,
        string description)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            Refuse(code, $"The {description} path in the manifest is not a safe relative path.");
        }

        var root = Path.GetFullPath(manifestDirectory).TrimEnd(Path.DirectorySeparatorChar) +
                   Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(manifestDirectory, relativePath));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
        {
            Refuse(code, $"The {description} is missing or outside its export session: {candidate}");
        }

        return candidate;
    }

    private static string SafePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .Trim()
            .TrimEnd('.');
        if (string.IsNullOrWhiteSpace(cleaned)) return "texture";
        return cleaned.Length <= 100 ? cleaned : cleaned[..100];
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task SaveJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void Refuse(string code, string message) =>
        throw new TextureImportCandidateRefusalException(code, message);
}
