using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Arkive.Core.Assets;
using Arkive.Core.Baselines;
using Arkive.Core.Exports;
using Arkive.Core.Inspection;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

public sealed record FaceTextureMappingAsset(
    string ObjectName,
    int TextureNumber,
    int ExportIndex,
    string ProjectId,
    string ProjectManifestPath,
    string EditableImagePath,
    bool ProjectCreated,
    string? PreviousEditBackupPath);

public sealed record FaceTextureMappingManifest(
    int SchemaVersion,
    string MappingId,
    DateTimeOffset CreatedAt,
    string SourcePackagePath,
    string PackageAlias,
    string ObjectPrefix,
    int CandidateCount,
    IReadOnlyList<FaceTextureMappingAsset> Assets);

public sealed record FaceTextureMappingResult(
    string SessionDirectory,
    string ManifestPath,
    FaceTextureMappingManifest Manifest,
    IReadOnlyList<GuidedTextureModProjectEntry> Projects);

/// <summary>
/// Creates a reversible, empirical face-identification set. Lost Ark's customization
/// table orders UI rows independently from texture suffixes, so each diffuse is
/// marked with its own suffix and can be mapped by observing the character creator.
/// </summary>
public sealed partial class FaceTextureMappingService
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "arkive-face-texture-map.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AssetCatalogReader _catalogReader;
    private readonly TextureReconnaissanceService _reconnaissanceService;
    private readonly TextureReconnaissanceWorkspaceStore _reconnaissanceWorkspace;
    private readonly NativeTextureExportService _nativeTextureExportService;
    private readonly ExportWorkspaceStore _exportWorkspace;
    private readonly GuidedTextureModWorkspace _modWorkspace;
    private readonly BaselineWorkspaceStore _baselineWorkspace;

    public FaceTextureMappingService(
        AssetCatalogReader catalogReader,
        TextureReconnaissanceService reconnaissanceService,
        TextureReconnaissanceWorkspaceStore reconnaissanceWorkspace,
        NativeTextureExportService nativeTextureExportService,
        ExportWorkspaceStore exportWorkspace,
        GuidedTextureModWorkspace modWorkspace,
        BaselineWorkspaceStore baselineWorkspace)
    {
        _catalogReader = catalogReader;
        _reconnaissanceService = reconnaissanceService;
        _reconnaissanceWorkspace = reconnaissanceWorkspace;
        _nativeTextureExportService = nativeTextureExportService;
        _exportWorkspace = exportWorkspace;
        _modWorkspace = modWorkspace;
        _baselineWorkspace = baselineWorkspace;
    }

    public async Task<FaceTextureMappingResult> CreateAsync(
        string sourcePackagePath,
        string packageAlias,
        string physicalPackageName,
        string objectPrefix,
        IProgress<(int Completed, int Total, string ObjectName)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePackagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPackageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectPrefix);

        var packagePath = Path.GetFullPath(sourcePackagePath);
        var prefix = objectPrefix.Trim();
        var matcher = FaceDiffuseRegex(prefix);
        var catalog = _catalogReader.Read(packagePath);
        var candidates = catalog.Assets
            .Where(asset =>
                asset.Kind == AssetKind.Texture &&
                asset.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
            .Select(asset => (Asset: asset, Match: matcher.Match(asset.ObjectName)))
            .Where(item => item.Match.Success)
            .Select(item => (item.Asset, Number: int.Parse(item.Match.Groups["number"].Value)))
            .GroupBy(item => item.Number)
            .Select(group => group.Count() == 1
                ? group.Single()
                : throw new InvalidDataException(
                    $"Face texture suffix {group.Key:00} resolves to more than one Texture2D."))
            .OrderBy(item => item.Number)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new InvalidDataException(
                $"No numbered diffuse face Texture2Ds matching '{prefix}_NN_face_d' or " +
                $"'{prefix}_face_NN_d' were found in {Path.GetFileName(packagePath)}.");
        }

        var mappingRoot = Path.Combine(_modWorkspace.RootDirectory, "_face-maps");
        var mappingId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..25];
        var sessionDirectory = Path.Combine(mappingRoot, mappingId);
        var backupDirectory = Path.Combine(sessionDirectory, "previous-edits");
        Directory.CreateDirectory(backupDirectory);

        var packageSha256 = await _baselineWorkspace.GetPackageSha256Async(
            packagePath, cancellationToken).ConfigureAwait(false);
        var existing = (await _modWorkspace.LoadAsync(cancellationToken).ConfigureAwait(false))
            .Where(entry => Path.GetFullPath(entry.Manifest.SourcePackagePath)
                .Equals(packagePath, StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.Manifest.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key.ToUpperInvariant(),
                group => group
                    .OrderBy(entry => entry.Manifest.CreatedAt)
                    .First());
        var completed = new List<FaceTextureMappingAsset>(candidates.Length);
        var projects = new List<GuidedTextureModProjectEntry>(candidates.Length);

        try
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = candidate.Asset.ObjectName.ToUpperInvariant();
                var selectionExportIndex = checked(candidate.Asset.ExportIndex - 1);
                var created = false;
                string? backupPath = null;
                GuidedTextureModProjectEntry project;
                string originalEditable;
                if (existing.TryGetValue(key, out var current))
                {
                    project = current;
                    project = await _modWorkspace.NormalizeSelectionExportIndexAsync(
                        project.ManifestPath, selectionExportIndex, cancellationToken).ConfigureAwait(false);
                    backupPath = Path.Combine(
                        backupDirectory,
                        $"{candidate.Asset.ObjectName}-before-map.png");
                    File.Copy(project.Manifest.EditableImagePath, backupPath, overwrite: false);
                    var native = await NativeTextureExportService.LoadManifestAsync(
                        project.Manifest.NativeTextureManifestPath, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(native.EditImageRelativePath))
                    {
                        throw new InvalidDataException(
                            $"The native export for {candidate.Asset.ObjectName} has no pristine editable image.");
                    }
                    originalEditable = Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(project.Manifest.NativeTextureManifestPath)!,
                        native.EditImageRelativePath));
                    project = await _modWorkspace.ResetEditableAsync(
                        project.ManifestPath, originalEditable, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var report = await _reconnaissanceService.InspectAsync(
                        new TextureReconnaissanceRequest(
                            packagePath,
                            packageAlias,
                            candidate.Asset.ExportIndex,
                            candidate.Asset.Kind,
                            candidate.Asset.ClassName,
                            candidate.Asset.ObjectName,
                            candidate.Asset.SerialOffset,
                            candidate.Asset.SerialSize,
                            packageSha256),
                        cancellationToken).ConfigureAwait(false);
                    var recon = await _reconnaissanceWorkspace.SaveAsync(
                        report, cancellationToken).ConfigureAwait(false);
                    var nativeDirectory = _exportWorkspace.CreateSessionDirectory(
                        packageAlias, candidate.Asset.ObjectName + "-face-map-native");
                    var native = await _nativeTextureExportService.ExportAsync(
                        new NativeTextureExportRequest(report, nativeDirectory, recon.JsonPath),
                        cancellationToken).ConfigureAwait(false);
                    await _exportWorkspace.CommitNativeTextureAsync(
                        native, report, [], cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(native.Manifest.EditImageRelativePath))
                    {
                        throw new InvalidDataException(
                            $"The native export for {candidate.Asset.ObjectName} produced no editable PNG.");
                    }
                    originalEditable = Path.Combine(
                        native.OutputDirectory, native.Manifest.EditImageRelativePath);
                    project = await _modWorkspace.CreateAsync(
                        $"Face map {candidate.Number:00} - {candidate.Asset.ObjectName}",
                        packageAlias,
                        physicalPackageName,
                        packagePath,
                        candidate.Asset.ClassName,
                        candidate.Asset.ObjectName,
                        selectionExportIndex,
                        native.Manifest.PixelFormat,
                        native.Manifest.Width,
                        native.Manifest.Height,
                        native.ManifestPath,
                        originalEditable,
                        cancellationToken).ConfigureAwait(false);
                    created = true;
                    existing[key] = project;
                }

                await FaceTextureNumberOverlay.ApplyAsync(
                    project.Manifest.EditableImagePath,
                    candidate.Number,
                    cancellationToken).ConfigureAwait(false);
                project = await _modWorkspace.SetIncludedAsync(
                    project.ManifestPath, true, cancellationToken).ConfigureAwait(false);
                projects.Add(project);
                completed.Add(new FaceTextureMappingAsset(
                    candidate.Asset.ObjectName,
                    candidate.Number,
                    candidate.Asset.ExportIndex,
                    project.Manifest.ProjectId,
                    project.ManifestPath,
                    project.Manifest.EditableImagePath,
                    created,
                    backupPath));
                progress?.Report((completed.Count, candidates.Length, candidate.Asset.ObjectName));
            }

            var manifest = new FaceTextureMappingManifest(
                CurrentSchemaVersion,
                mappingId,
                DateTimeOffset.UtcNow,
                packagePath,
                packageAlias,
                prefix,
                candidates.Length,
                completed);
            var manifestPath = Path.Combine(sessionDirectory, ManifestFileName);
            await SaveAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            return new FaceTextureMappingResult(
                sessionDirectory, manifestPath, manifest, projects);
        }
        catch
        {
            foreach (var asset in completed.Where(item => item.PreviousEditBackupPath is not null))
            {
                try
                {
                    File.Copy(asset.PreviousEditBackupPath!, asset.EditableImagePath, overwrite: true);
                }
                catch (IOException)
                {
                    // Preserve the original failure. The backup remains in the session directory.
                }
            }
            throw;
        }
    }

    [GeneratedRegex(
        "^(?:placeholder_(?<number>\\d{2})_face_d|placeholder_face_(?<number>\\d{2})_d)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderFaceDiffuseRegex();

    public static bool TryParseDiffuseObjectName(
        string objectPrefix,
        string objectName,
        out int textureNumber)
    {
        textureNumber = 0;
        if (string.IsNullOrWhiteSpace(objectPrefix) || string.IsNullOrWhiteSpace(objectName))
        {
            return false;
        }

        var match = FaceDiffuseRegex(objectPrefix.Trim()).Match(objectName);
        return match.Success &&
               int.TryParse(match.Groups["number"].Value, out textureNumber);
    }

    private static Regex FaceDiffuseRegex(string prefix)
    {
        // GeneratedRegex cannot parameterize the escaped player-family prefix.
        _ = PlaceholderFaceDiffuseRegex(); // Keeps the intended pattern compile-time checked.
        return new Regex(
            $"^(?:{Regex.Escape(prefix)}_(?<number>\\d{{2}})_face_d|" +
            $"{Regex.Escape(prefix)}_face_(?<number>\\d{{2}})_d)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    private static async Task SaveAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public static class FaceTextureNumberOverlay
{
    private static readonly string[][] Digits =
    [
        ["111", "101", "101", "101", "111"],
        ["010", "110", "010", "010", "111"],
        ["111", "001", "111", "100", "111"],
        ["111", "001", "111", "001", "111"],
        ["101", "101", "111", "001", "001"],
        ["111", "100", "111", "001", "111"],
        ["111", "100", "111", "101", "111"],
        ["111", "001", "001", "001", "001"],
        ["111", "101", "111", "101", "111"],
        ["111", "101", "111", "001", "111"],
    ];

    public static async Task ApplyAsync(
        string pngPath,
        int textureNumber,
        CancellationToken cancellationToken = default)
    {
        if (textureNumber is < 0 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(textureNumber));
        }
        var fullPath = Path.GetFullPath(pngPath);
        var image = await PngRgbaCodec.ReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var pixels = image.Pixels;
        var scale = Math.Max(2, Math.Min(image.Width / 18, image.Height / 10));
        var digitWidth = 3 * scale;
        var digitHeight = 5 * scale;
        var gap = scale;
        var contentWidth = 2 * digitWidth + gap;
        var padding = scale;
        var left = Math.Max(0, (image.Width - contentWidth) / 2);
        var top = Math.Max(0, (image.Height - digitHeight) / 2);
        FillRect(image, left - padding, top - padding,
            contentWidth + padding * 2, digitHeight + padding * 2, 8, 8, 12);
        DrawBorder(image, left - padding, top - padding,
            contentWidth + padding * 2, digitHeight + padding * 2,
            Math.Max(2, scale / 7), 255, 0, 210);

        var text = textureNumber.ToString("00");
        DrawDigit(image, text[0] - '0', left, top, scale);
        DrawDigit(image, text[1] - '0', left + digitWidth + gap, top, scale);

        var temporary = fullPath + $".{Guid.NewGuid():N}.face-map.tmp";
        try
        {
            await PngRgbaCodec.WriteAsync(
                temporary, new RgbaImage(image.Width, image.Height, pixels), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void DrawDigit(RgbaImage image, int digit, int left, int top, int scale)
    {
        var rows = Digits[digit];
        for (var row = 0; row < rows.Length; row++)
        for (var column = 0; column < rows[row].Length; column++)
        {
            if (rows[row][column] == '1')
            {
                FillRect(image, left + column * scale, top + row * scale,
                    scale, scale, 255, 238, 0);
            }
        }
    }

    private static void DrawBorder(
        RgbaImage image, int x, int y, int width, int height, int thickness,
        byte red, byte green, byte blue)
    {
        FillRect(image, x, y, width, thickness, red, green, blue);
        FillRect(image, x, y + height - thickness, width, thickness, red, green, blue);
        FillRect(image, x, y, thickness, height, red, green, blue);
        FillRect(image, x + width - thickness, y, thickness, height, red, green, blue);
    }

    private static void FillRect(
        RgbaImage image, int x, int y, int width, int height,
        byte red, byte green, byte blue)
    {
        var startX = Math.Clamp(x, 0, image.Width);
        var startY = Math.Clamp(y, 0, image.Height);
        var endX = Math.Clamp(x + width, 0, image.Width);
        var endY = Math.Clamp(y + height, 0, image.Height);
        for (var row = startY; row < endY; row++)
        for (var column = startX; column < endX; column++)
        {
            var offset = (row * image.Width + column) * 4;
            image.Pixels[offset] = red;
            image.Pixels[offset + 1] = green;
            image.Pixels[offset + 2] = blue;
            // Preserve the editable image's alpha. Import also preserves game alpha.
        }
    }
}
