using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Assets;
using Arkive.Core.GameData;

namespace Arkive.Core.Mods;

public static class ArkiveModFileService
{
    private const int BufferBytes = 1024 * 1024;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<ArkiveModFileReadResult> ReadAsync(
        string path,
        bool verifyFiles = true,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The .arkive mod file was not found.", fullPath);
        var info = new FileInfo(fullPath);
        if (info.Length <= 0 || info.Length > ArkiveModFileFormat.MaximumArchiveBytes)
            Refuse("ARK001", $"{info.Name} is empty or larger than the {ArkiveModFileFormat.MaximumArchiveBytes:N0}-byte .arkive limit.");

        ArkiveModFileManifest manifest;
        await using (var stream = new FileStream(
                         fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            ZipArchive archive;
            try
            {
                archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            }
            catch (InvalidDataException exception)
            {
                Refuse("ARK002", $"{info.Name} is not a readable .arkive archive: {exception.Message}");
                throw;
            }

            using (archive)
            {
                var entries = ValidateEntries(archive);
                if (!entries.TryGetValue(ArkiveModFileFormat.ManifestEntryName, out var manifestEntry))
                    Refuse("ARK003", $"{info.Name} has no {ArkiveModFileFormat.ManifestEntryName} manifest.");
                if (manifestEntry.Length > ArkiveModFileFormat.MaximumManifestBytes)
                    Refuse("ARK004", "The .arkive manifest is larger than Arkive's manifest limit.");
                try
                {
                    await using var manifestStream = manifestEntry.Open();
                    manifest = await JsonSerializer.DeserializeAsync<ArkiveModFileManifest>(
                                   manifestStream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                               throw new JsonException("The manifest is empty.");
                }
                catch (JsonException exception)
                {
                    Refuse("ARK005", $"The .arkive manifest is not valid JSON for this format: {exception.Message}");
                    throw;
                }

                ValidateManifest(manifest);
                var referenced = manifest.Assets.SelectMany(asset => asset.Files)
                    .ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
                foreach (var name in entries.Keys)
                {
                    if (name.Equals(ArkiveModFileFormat.ManifestEntryName, StringComparison.Ordinal)) continue;
                    if (!referenced.ContainsKey(name))
                        Refuse("ARK006", $"The archive contains an unreferenced entry: {name}");
                }
                foreach (var file in referenced.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!entries.TryGetValue(file.Path, out var entry))
                        Refuse("ARK007", $"The manifest references a missing file: {file.Path}");
                    if (!entry.FullName.Equals(file.Path, StringComparison.Ordinal))
                        Refuse("ARK008", $"The manifest path casing differs from the archive entry: {file.Path}");
                    if (entry.Length != file.Bytes)
                        Refuse("ARK009", $"{file.Path} is {entry.Length:N0} bytes; the manifest requires {file.Bytes:N0}.");
                    if (!verifyFiles) continue;
                    await using var content = entry.Open();
                    var hash = await CopyAndHashAsync(content, Stream.Null, entry.Length, "ARK009", cancellationToken)
                        .ConfigureAwait(false);
                    if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        Refuse("ARK010", $"{file.Path} does not match its manifest SHA-256.");
                }
            }
        }

        var archiveHash = await HashFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
        info.Refresh();
        return new ArkiveModFileReadResult(
            fullPath, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            archiveHash, manifest);
    }

    public static async Task<ArkiveModFileReadResult> WriteAsync(
        string outputPath,
        ArkiveModFileManifest manifest,
        IReadOnlyList<ArkiveModPackAsset> assets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(assets);
        var fullOutput = Path.GetFullPath(outputPath);
        if (!fullOutput.EndsWith(ArkiveModFileFormat.Extension, StringComparison.OrdinalIgnoreCase))
            Refuse("ARK040", $"Arkive mod files must use the {ArkiveModFileFormat.Extension} extension.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);

        var packedAssets = new List<ArkiveModFileAsset>(assets.Count);
        var sources = new List<(ArkiveModFileEntry Entry, string SourcePath)>();
        foreach (var asset in assets)
        {
            var files = new List<ArkiveModFileEntry>(asset.Files.Count);
            foreach (var file in asset.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.GetFullPath(file.SourcePath);
                if (!File.Exists(source))
                    throw new FileNotFoundException(
                        $"The source file for {asset.Descriptor.DisplayName} was not found.", source);
                var entryPath = $"{ArkiveModFileFormat.AssetDirectory}/{asset.Descriptor.AssetId}/{SafeEntryFileName(file.FileName)}";
                var entry = new ArkiveModFileEntry(
                    file.Role, entryPath, new FileInfo(source).Length,
                    await HashFileAsync(source, cancellationToken).ConfigureAwait(false));
                files.Add(entry);
                sources.Add((entry, source));
            }
            packedAssets.Add(asset.Descriptor with { Files = files });
        }

        var finalManifest = manifest with
        {
            Format = ArkiveModFileFormat.FormatId,
            // Older Arkive versions read version 1, so only mods that need version 2 are written as it.
            FormatVersion = manifest.GameData is null ? 1 : ArkiveModFileFormat.GameDataFormatVersion,
            Assets = packedAssets,
        };
        ValidateManifest(finalManifest);

        var temporary = Path.Combine(
            Path.GetDirectoryName(fullOutput)!,
            $".{Path.GetFileName(fullOutput)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                             BufferBytes, FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var timestamp = finalManifest.UpdatedAt;
                    var manifestEntry = archive.CreateEntry(
                        ArkiveModFileFormat.ManifestEntryName, CompressionLevel.Optimal);
                    manifestEntry.LastWriteTime = timestamp;
                    await using (var manifestStream = manifestEntry.Open())
                    {
                        await JsonSerializer.SerializeAsync(
                                manifestStream, finalManifest, JsonOptions, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    foreach (var (entry, source) in sources)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var compression = entry.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                            ? CompressionLevel.NoCompression
                            : CompressionLevel.Optimal;
                        var zipEntry = archive.CreateEntry(entry.Path, compression);
                        zipEntry.LastWriteTime = timestamp;
                        await using var input = new FileStream(
                            source, FileMode.Open, FileAccess.Read, FileShare.Read,
                            BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var output = zipEntry.Open();
                        await input.CopyToAsync(output, BufferBytes, cancellationToken).ConfigureAwait(false);
                    }
                }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            var verified = await ReadAsync(temporary, verifyFiles: true, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, fullOutput, overwrite: true);
            return verified with { Path = fullOutput };
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Rewrites the .arkive file at <paramref name="path"/> with <paramref name="manifest"/>, keeping its asset files
    /// as they are; the manifest must list the same asset files.
    /// </summary>
    public static async Task<ArkiveModFileReadResult> RewriteManifestAsync(
        string path,
        ArkiveModFileManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var finalManifest = manifest with
        {
            FormatVersion = manifest.GameData is null ? 1 : ArkiveModFileFormat.GameDataFormatVersion,
        };
        ValidateManifest(finalManifest);
        var temporary = Path.Combine(
            Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                             BufferBytes, FileOptions.Asynchronous))
            {
                using (var source = ZipFile.OpenRead(fullPath))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var manifestEntry = archive.CreateEntry(ArkiveModFileFormat.ManifestEntryName, CompressionLevel.Optimal);
                    manifestEntry.LastWriteTime = finalManifest.UpdatedAt;
                    await using (var manifestStream = manifestEntry.Open())
                        await JsonSerializer.SerializeAsync(manifestStream, finalManifest, JsonOptions, cancellationToken)
                            .ConfigureAwait(false);
                    foreach (var entry in source.Entries.Where(entry =>
                                 !entry.FullName.Equals(ArkiveModFileFormat.ManifestEntryName, StringComparison.Ordinal)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var copy = archive.CreateEntry(entry.FullName, entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                            ? CompressionLevel.NoCompression
                            : CompressionLevel.Optimal);
                        copy.LastWriteTime = finalManifest.UpdatedAt;
                        await using var input = entry.Open();
                        await using var output = copy.Open();
                        await input.CopyToAsync(output, BufferBytes, cancellationToken).ConfigureAwait(false);
                    }
                }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            var verified = await ReadAsync(temporary, verifyFiles: true, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, fullPath, overwrite: true);
            return verified with { Path = fullPath };
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static async Task<IReadOnlyDictionary<string, string>> ExtractAssetFilesAsync(
        string arkivePath,
        ArkiveModFileAsset asset,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var stream = new FileStream(
            Path.GetFullPath(arkivePath), FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entries = ValidateEntries(archive);
        foreach (var file in asset.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(file.Path, out var entry) || entry.Length != file.Bytes)
                Refuse("ARK011", $"{file.Path} is missing or changed inside the .arkive file.");
            var destination = Path.GetFullPath(Path.Combine(root, SafeEntryFileName(Path.GetFileName(file.Path))));
            if (!destination.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                Refuse("ARK012", $"{file.Path} would extract outside its working directory.");
            var temporary = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                string hash;
                await using (var input = entry.Open())
                await using (var output = new FileStream(
                                 temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 BufferBytes, FileOptions.Asynchronous))
                {
                    hash = await CopyAndHashAsync(input, output, entry.Length, "ARK013", cancellationToken)
                        .ConfigureAwait(false);
                }
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    Refuse("ARK013", $"{file.Path} failed SHA-256 verification during extraction.");
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            extracted[file.Role] = destination;
        }
        return extracted;
    }

    public const int MaximumGameDataEdits = 200_000;

    private static void ValidateGameData(ArkiveModGameData gameData)
    {
        if (gameData.Edits is null || gameData.Edits.Count is 0 or > MaximumGameDataEdits)
            Refuse("ARK046", $"Game table changes must list between 1 and {MaximumGameDataEdits:N0} cells.");
        var cells = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in gameData.Edits)
        {
            if (edit is null || !GameDataValues.IsDatabaseName(edit.Database) || !GameDataValues.IsIdentifier(edit.Table) ||
                !GameDataValues.IsIdentifier(edit.Column) || edit.Row is null || edit.Row.Count is 0 or > 8 ||
                edit.Row.Keys.Any(column => !GameDataValues.IsIdentifier(column)))
                Refuse("ARK046", "A game table change names an invalid database, table, row or column.");
            try
            {
                foreach (var value in edit.Row.Values.Append(edit.Value).Append(edit.Original))
                    if (GameDataValues.ToSql(value) is string text && text.Length > 1_000_000)
                        Refuse("ARK046", $"A value for {edit.Table}.{edit.Column} is too long.");
            }
            catch (NotSupportedException)
            {
                Refuse("ARK046", $"{edit.Table}.{edit.Column} has a value that is not text, a number or empty.");
            }
            if (!cells.Add(edit.CellKey))
                Refuse("ARK046", $"{edit.Table}.{edit.Column} for {edit.RowLabel} is changed twice.");
        }
    }

    public static string ComputeContentSha256(ArkiveModFileAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Kind == UnifiedPackageAssetKind.MaterialInstanceConstant && asset.Material is not null)
            return ComputeMaterialChangesSha256(asset.Material);
        if (asset.Kind == UnifiedPackageAssetKind.ParticleSystem && asset.Particle is not null)
            return Particles.ParticleEffects.ContentSha256(asset.Particle.Patches);
        var files = string.Join("|", asset.Files
            .OrderBy(file => file.Role, StringComparer.Ordinal)
            .Select(file => $"{file.Role}:{file.Sha256.ToUpperInvariant()}"));
        return asset.Files.Count == 1
            ? asset.Files[0].Sha256.ToUpperInvariant()
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(files)));
    }

    public static string ComputeMaterialChangesSha256(ArkiveModMaterialData material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var builder = new StringBuilder();
        foreach (var scalar in material.Scalars.Where(item => item.Changed)
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            builder.Append("s|").Append(scalar.Name.ToUpperInvariant()).Append('|')
                .Append(Bits(scalar.Value)).Append('\n');
        foreach (var vector in material.Vectors.Where(item => item.Changed)
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            builder.Append("v|").Append(vector.Name.ToUpperInvariant()).Append('|')
                .Append(Bits(vector.Red)).Append(',').Append(Bits(vector.Green)).Append(',')
                .Append(Bits(vector.Blue)).Append(',').Append(Bits(vector.Alpha)).Append('\n');
        foreach (var texture in material.Textures.Where(item => item.Changed)
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            builder.Append("t|").Append(texture.Name.ToUpperInvariant()).Append('|')
                .Append(texture.TargetIdentity.ToLowerInvariant()).Append('\n');
        if (material.Parent is { Changed: true } parent)
            builder.Append("p|").Append(parent.TargetIdentity.ToLowerInvariant()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));

        static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
    }

    public static string NormalizePhysicalPackageName(string physicalPackageName)
    {
        var name = Path.GetFileName(physicalPackageName.Trim());
        return name.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? name : name + ".upk";
    }

    public static string PhysicalPackageKey(string physicalPackageNameOrPath) =>
        NormalizePhysicalPackageName(physicalPackageNameOrPath).ToUpperInvariant();

    public static void ValidateManifest(ArkiveModFileManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Format, ArkiveModFileFormat.FormatId, StringComparison.Ordinal))
            Refuse("ARK020", $"This is not an Arkive mod manifest (format '{manifest.Format}').");
        if (manifest.FormatVersion > ArkiveModFileFormat.CurrentFormatVersion)
            Refuse("ARK021",
                $"This .arkive uses format version {manifest.FormatVersion}, which needs a newer Arkive. " +
                $"This build reads version {ArkiveModFileFormat.CurrentFormatVersion}.");
        if (manifest.FormatVersion < 1)
            Refuse("ARK022", $"Format version {manifest.FormatVersion} is invalid.");
        if (!IsIdentifier(manifest.ModId))
            Refuse("ARK023", "The mod ID must be 32 hexadecimal characters.");
        RequireText(manifest.Name, 120, "ARK024", "mod name");
        RequireText(manifest.Version, 40, "ARK025", "mod version");
        OptionalText(manifest.Author, 120, "ARK026", "author");
        OptionalText(manifest.Description, 8000, "ARK026", "description", allowLineBreaks: true);
        OptionalText(manifest.Website, 500, "ARK026", "website");
        if (!string.IsNullOrWhiteSpace(manifest.Website) &&
            (!Uri.TryCreate(manifest.Website, UriKind.Absolute, out var website) ||
             website.Scheme is not ("http" or "https")))
            Refuse("ARK026", "The website must be an absolute http or https address.");
        if (manifest.Tags is null || manifest.Tags.Count > 32)
            Refuse("ARK027", "A mod may declare at most 32 tags.");
        foreach (var tag in manifest.Tags) RequireText(tag, 40, "ARK027", "tag");
        if (!string.Equals(manifest.GameProfile, ArkiveModBundleService.WesternGlobalProfile, StringComparison.Ordinal))
            Refuse("ARK028",
                $"This mod targets game profile '{manifest.GameProfile}'. Arkive supports {ArkiveModBundleService.WesternGlobalProfile}.");
        if (manifest.UpdatedAt < manifest.CreatedAt)
            Refuse("ARK029", "The mod's update time precedes its creation time.");
        if (manifest.Assets is null || manifest.Assets.Count == 0 && manifest.GameData is null ||
            manifest.Assets.Count > ArkiveModFileFormat.MaximumAssets)
            Refuse("ARK030",
                $"A mod must contain between 1 and {ArkiveModFileFormat.MaximumAssets:N0} assets.");
        if (manifest.GameData is not null)
        {
            if (manifest.FormatVersion < ArkiveModFileFormat.GameDataFormatVersion)
                Refuse("ARK046", $"Game table changes need format version {ArkiveModFileFormat.GameDataFormatVersion}.");
            ValidateGameData(manifest.GameData);
        }

        var assetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<ArkiveModTargetKey>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in manifest.Assets)
        {
            if (asset is null) Refuse("ARK031", "The manifest contains an empty asset.");
            if (!IsIdentifier(asset.AssetId) || !assetIds.Add(asset.AssetId))
                Refuse("ARK031", $"Asset ID '{asset.AssetId}' is malformed or duplicated.");
            if (!Enum.IsDefined(asset.Kind))
                Refuse("ARK032", $"Asset {asset.AssetId} has an unsupported kind.");
            RequireText(asset.DisplayName, 200, "ARK033", "asset display name");
            ValidateTarget(asset);
            if (!targets.Add(ArkiveModTargetKey.From(asset)))
                Refuse("ARK034",
                    $"The mod replaces {asset.Target.ObjectName} in {asset.Target.PhysicalPackageName} more than once.");
            ValidateAssetData(asset);
            if (asset.Files is null) Refuse("ARK035", $"Asset {asset.AssetId} has no file list.");
            foreach (var file in asset.Files)
            {
                if (file is null || string.IsNullOrWhiteSpace(file.Role) || file.Role.Length > 32)
                    Refuse("ARK035", $"Asset {asset.AssetId} contains a file with no role.");
                var prefix = $"{ArkiveModFileFormat.AssetDirectory}/{asset.AssetId}/";
                if (!IsSafeEntryName(file.Path) ||
                    !file.Path.StartsWith(prefix, StringComparison.Ordinal) ||
                    file.Path.IndexOf('/', prefix.Length) >= 0 ||
                    !paths.Add(file.Path))
                    Refuse("ARK036", $"{file.Path} is not a unique file directly beneath {prefix}.");
                if (file.Bytes <= 0 || file.Bytes > ArkiveModFileFormat.MaximumEntryBytes)
                    Refuse("ARK037", $"{file.Path} has an invalid size.");
                if (!IsSha256(file.Sha256))
                    Refuse("ARK038", $"{file.Path} has a malformed SHA-256.");
            }
        }
    }

    private static void ValidateTarget(ArkiveModFileAsset asset)
    {
        var target = asset.Target ?? throw new ArkiveModRefusalException(
            "ARK039", $"Asset {asset.AssetId} has no target identity.");
        RequireText(target.PackageAlias, 200, "ARK039", "package alias");
        RequireText(target.PhysicalPackageName, 200, "ARK039", "physical package name");
        if (!target.PhysicalPackageName.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ||
            target.PhysicalPackageName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            target.PhysicalPackageName.Contains("..", StringComparison.Ordinal))
            Refuse("ARK039", $"'{target.PhysicalPackageName}' is not a plain .upk file name.");
        RequireText(target.ClassName, 200, "ARK039", "class name");
        RequireText(target.ObjectName, 400, "ARK039", "object name");
        if (target.ObjectPath is not null && !ArkiveModObjectPath.IsValid(target.ObjectPath, target.ObjectName))
            Refuse("ARK039", $"Asset {asset.AssetId} has a malformed object path.");
        if (target.NativeExportIndex is <= 0)
            Refuse("ARK039", $"Asset {asset.AssetId} declares an invalid native export index.");
        if (!IsSha256(target.SourcePackageSha256) || !IsSha256(target.SourceExportSha256))
            Refuse("ARK039", $"Asset {asset.AssetId} has malformed source identity hashes.");
        if (!KindMatches(asset.Kind, target.ClassName))
            Refuse("ARK039", $"Asset {asset.AssetId} declares {asset.Kind} but targets class {target.ClassName}.");
    }

    internal static bool KindMatches(UnifiedPackageAssetKind kind, string className) =>
        AssetTypes.Moddable.FirstOrDefault(type => type.ModKind == kind)?.AcceptsClass(className) == true;

    private static void ValidateAssetData(ArkiveModFileAsset asset)
    {
        var files = asset.Files ?? [];
        switch (asset.Kind)
        {
            case UnifiedPackageAssetKind.Texture2D:
                if (asset.Texture is not { Width: > 0 and <= 16384, Height: > 0 and <= 16384 } texture ||
                    string.IsNullOrWhiteSpace(texture.PixelFormat) || !Enum.IsDefined(texture.AlphaPolicy))
                    Refuse("ARK041", $"Texture asset {asset.AssetId} has no valid texture layout.");
                if (files.Count != 1 || files[0].Role != ArkiveModFileFormat.ImageRole ||
                    !(files[0].Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                      files[0].Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)))
                    Refuse("ARK041", $"Texture asset {asset.AssetId} must contain exactly one PNG or DDS image.");
                if (asset.Mesh is not null || asset.Material is not null)
                    Refuse("ARK041", $"Texture asset {asset.AssetId} contains unrelated data.");
                break;
            case UnifiedPackageAssetKind.SkeletalMesh:
            case UnifiedPackageAssetKind.StaticMesh:
                if (asset.Mesh is null || string.IsNullOrWhiteSpace(asset.Mesh.ImportRoute) ||
                    asset.Mesh.ImportRoute.Length > 64 ||
                    asset.Mesh.SourceGlbSha256 is { } sourceGlb && !IsSha256(sourceGlb))
                    Refuse("ARK042", $"Mesh asset {asset.AssetId} has no valid mesh data.");
                if (files.Count != 1 || files[0].Role != ArkiveModFileFormat.GlbRole ||
                    !files[0].Path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                    Refuse("ARK042", $"Mesh asset {asset.AssetId} must contain exactly one GLB.");
                if (asset.Texture is not null || asset.Material is not null)
                    Refuse("ARK042", $"Mesh asset {asset.AssetId} contains unrelated data.");
                break;
            case UnifiedPackageAssetKind.MaterialInstanceConstant:
                if (asset.Material is null || files.Count != 0 ||
                    asset.Texture is not null || asset.Mesh is not null || asset.Particle is not null)
                    Refuse("ARK043", $"Material asset {asset.AssetId} must contain only material parameter data.");
                ValidateMaterial(asset.AssetId, asset.Material);
                break;
            case UnifiedPackageAssetKind.ParticleSystem:
                var particle = asset.Particle;
                if (particle?.Recipe is not { IsValid: true } || particle.Patches is not { Count: > 0 and <= 100_000 } || files.Count != 0 ||
                    asset.Texture is not null || asset.Mesh is not null || asset.Material is not null)
                    Refuse("ARK044", $"Particle asset {asset.AssetId} must contain only a valid recipe and its value patches.");
                if (particle.Patches.Any(patch => patch.Original is null || patch.Value is null ||
                                                  patch.Original.Length != patch.Value.Length || patch.Value.Length is 0 or > 65_536 ||
                                                  patch.ValueOffset < 0 || string.IsNullOrWhiteSpace(patch.ObjectPath) ||
                                                  patch.Value.Any(value => !float.IsFinite(value)) || !IsSha256(patch.SourceExportSha256)))
                    Refuse("ARK044", $"Particle asset {asset.AssetId} has a malformed value patch.");
                break;
            case UnifiedPackageAssetKind.InterfaceMovie:
                if (files.Count != 1 || files[0].Role != ArkiveModFileFormat.MovieRole ||
                    !files[0].Path.EndsWith(".gfx", StringComparison.OrdinalIgnoreCase))
                    Refuse("ARK045", $"Interface movie asset {asset.AssetId} must contain exactly one GFX movie.");
                if (asset.Texture is not null || asset.Mesh is not null || asset.Material is not null || asset.Particle is not null)
                    Refuse("ARK045", $"Interface movie asset {asset.AssetId} contains unrelated data.");
                break;
        }
    }

    private static void ValidateMaterial(string assetId, ArkiveModMaterialData material)
    {
        if (material.Scalars is null || material.Vectors is null || material.Textures is null ||
            material.Scalars.Count + material.Vectors.Count + material.Textures.Count > 4096)
            Refuse("ARK043", $"Material asset {assetId} has an invalid parameter set.");
        RequireUnique(material.Scalars.Select(item => item?.Name));
        RequireUnique(material.Vectors.Select(item => item?.Name));
        RequireUnique(material.Textures.Select(item => item?.Name));
        foreach (var scalar in material.Scalars)
            if (!float.IsFinite(scalar.SourceValue) || !float.IsFinite(scalar.Value))
                Refuse("ARK043", $"Material scalar '{scalar.Name}' is not finite.");
        foreach (var vector in material.Vectors)
            if (!new[] { vector.SourceRed, vector.SourceGreen, vector.SourceBlue, vector.SourceAlpha,
                         vector.Red, vector.Green, vector.Blue, vector.Alpha }.All(float.IsFinite))
                Refuse("ARK043", $"Material vector '{vector.Name}' is not finite.");
        foreach (var reference in material.Textures.Append(material.Parent).OfType<ArkiveModMaterialReference>())
        {
            RequireText(reference.Name, 200, "ARK043", "material reference name");
            if (string.IsNullOrWhiteSpace(reference.SourceIdentity) ||
                string.IsNullOrWhiteSpace(reference.TargetIdentity) ||
                reference.SourceIdentity.Length > 1000 || reference.TargetIdentity.Length > 1000)
                Refuse("ARK043", $"Material reference '{reference.Name}' has no portable identity.");
        }

        void RequireUnique(IEnumerable<string?> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                RequireText(name, 200, "ARK043", "material parameter name");
                if (!seen.Add(name!))
                    Refuse("ARK043", $"Material asset {assetId} repeats parameter '{name}'.");
            }
        }
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > ArkiveModFileFormat.MaximumEntries)
            Refuse("ARK014", $"The archive has more than {ArkiveModFileFormat.MaximumEntries:N0} entries.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.EndsWith('/') && entry.Length == 0)
            {
                if (!IsSafeEntryName(name.TrimEnd('/')))
                    Refuse("ARK015", $"The archive contains an unsafe directory name: {name}");
                continue;
            }
            if (!IsSafeEntryName(name))
                Refuse("ARK015", $"The archive contains an unsafe entry name: {name}");
            if (!entries.TryAdd(name, entry))
                Refuse("ARK016", $"The archive contains duplicate entries named {name}.");
            if (entry.Length < 0 || entry.Length > ArkiveModFileFormat.MaximumEntryBytes)
                Refuse("ARK017", $"{name} exceeds Arkive's per-file limit.");
            if (entry.Length > ArkiveModFileFormat.CompressionRatioCheckMinimumEntryBytes &&
                (entry.CompressedLength <= 0 ||
                 entry.Length / (double)entry.CompressedLength > ArkiveModFileFormat.MaximumCompressionRatio))
                Refuse("ARK018", $"{name} has an unsafe compression ratio.");
            total += entry.Length;
            if (total > ArkiveModFileFormat.MaximumTotalUncompressedBytes)
                Refuse("ARK019", "The archive expands beyond Arkive's total size limit.");
        }
        return entries;
    }

    internal static bool IsSafeEntryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > ArkiveModFileFormat.MaximumEntryNameLength ||
            name.Contains('\\') || name.StartsWith('/') || name.Contains(':') ||
            name.Any(char.IsControl))
            return false;
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var segment in name.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(invalid) >= 0 ||
                segment.EndsWith('.') || segment.EndsWith(' ') || segment.StartsWith(' '))
                return false;
        }
        return true;
    }

    internal static string SafeEntryFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(Path.GetFileName(fileName)
                .Select(character => invalid.Contains(character) || char.IsControl(character) ||
                                     character is ':' or '\\' or '/'
                    ? '_'
                    : character)
                .ToArray())
            .Trim().Trim('.');
        if (safe.Length == 0) safe = "file";
        if (safe.Length > 100)
        {
            var extension = Path.GetExtension(safe);
            safe = safe[..(100 - extension.Length)] + extension;
        }
        return safe;
    }

    internal static bool IsIdentifier(string? value) =>
        value is { Length: 32 } && value.All(Uri.IsHexDigit);

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void RequireText(
        string? value, int maximumLength, string code, string field, bool allowLineBreaks = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => char.IsControl(character) &&
                                   !(allowLineBreaks && character is '\r' or '\n' or '\t')))
            Refuse(code, $"The {field} is missing, too long, or contains control characters.");
    }

    private static void OptionalText(
        string? value, int maximumLength, string code, string field, bool allowLineBreaks = false)
    {
        if (!string.IsNullOrWhiteSpace(value)) RequireText(value, maximumLength, code, field, allowLineBreaks);
    }

    internal static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<string> CopyAndHashAsync(
        Stream input, Stream output, long expectedBytes, string sizeCode, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferBytes];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > expectedBytes)
                Refuse(sizeCode, "An archive entry expanded beyond its declared size.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (total != expectedBytes) Refuse(sizeCode, "An archive entry is shorter than its declared size.");
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    [DoesNotReturn]
    internal static void Refuse(string code, string message) =>
        throw new ArkiveModRefusalException(code, message);
}
