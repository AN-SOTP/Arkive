using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Discovery;
using Arkive.Core.External;
using Arkive.Core.Inspection;

namespace Arkive.Core.Meshes;

public sealed record SkeletalMeshAssetKitRequest(
    SkeletalMeshReconnaissanceEntry Reconnaissance,
    SkeletalMeshEditableExportResult EditableExport,
    string OutputDirectory,
    IReadOnlyList<string>? InGameNames = null,
    string? PackageSearchRoot = null);

public sealed record SkeletalMeshAssetKitFile(
    string RelativePath,
    string Role,
    long Bytes,
    string Sha256);

public sealed record SkeletalMeshAssetKitDependency(
    string PackageName,
    string ObjectName,
    string ClassName,
    string Evidence,
    IReadOnlyList<string> RelativeFiles,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ObjectPath = null);

public sealed record SkeletalMeshAssetKitManifest(
    int SchemaVersion,
    string KitId,
    DateTimeOffset CreatedAt,
    string SourcePackagePath,
    string PhysicalPackageName,
    string PackageAlias,
    string SourcePackageSha256,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    IReadOnlyList<string> InGameNames,
    string EditableGlbRelativePath,
    string EditableManifestRelativePath,
    int LodCount,
    int BoneCount,
    IReadOnlyList<string> MaterialSlots,
    IReadOnlyList<SkeletalMeshAssetKitDependency> Dependencies,
    IReadOnlyList<SkeletalMeshAssetKitFile> Files,
    IReadOnlyList<string> UnresolvedMaterialSlots,
    IReadOnlyList<string> Diagnostics,
    double DurationSeconds);

public sealed record SkeletalMeshAssetKitResult(
    string OutputDirectory,
    string ManifestPath,
    string SummaryPath,
    SkeletalMeshAssetKitManifest Manifest,
    TimeSpan Duration);

/// <summary>
/// Builds a self-contained authoring/reference kit for one proven SkeletalMesh.
/// The editable GLB comes from Arkive's native decoder; materials and textures
/// come from an exact UModel selected-object dependency export. This service is
/// read-only with respect to the game package and does not create mod projects.
/// </summary>
public sealed class SkeletalMeshAssetKitService
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly UmodelBridge _bridge;

    public SkeletalMeshAssetKitService(UmodelBridge bridge) =>
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

    /// <param name="packageSearchRoot">The game's Packages folder, where the mesh's linked packages live. The mesh
    /// itself may come from a clean backup elsewhere when a mod is installed over it.</param>
    public async Task<SkeletalMeshAssetKitResult> ExtractFromEditableAsync(
        string editableManifestPath, string outputDirectory, IReadOnlyList<string>? inGameNames = null,
        string? packageSearchRoot = null, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.GetFullPath(editableManifestPath);
        var editable = await ReadAsync<SkeletalMeshEditableExportManifest>(manifestPath, cancellationToken).ConfigureAwait(false);
        var report = await ReadAsync<SkeletalMeshReconnaissanceReport>(editable.ReconnaissanceJsonPath, cancellationToken)
            .ConfigureAwait(false);
        ValidateIdentity(report, editable);
        var directory = Path.GetDirectoryName(manifestPath)!;
        if (Path.IsPathRooted(editable.GlbRelativePath)) throw new InvalidDataException("The retained GLB path is invalid.");
        var glbPath = Path.GetFullPath(Path.Combine(directory, editable.GlbRelativePath));
        if (!glbPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The retained GLB is outside its export workspace.");
        await VerifyHashAsync(glbPath, editable.GlbSha256, cancellationToken).ConfigureAwait(false);
        await VerifyHashAsync(editable.SourcePackagePath, editable.SourcePackageSha256, cancellationToken).ConfigureAwait(false);
        var inspection = await new ExportArtifactInspector().InspectAsync(glbPath, cancellationToken).ConfigureAwait(false);
        if (inspection.ErrorCount != 0) throw new InvalidDataException("The retained editable GLB failed structural inspection.");
        var reconDirectory = Path.GetDirectoryName(editable.ReconnaissanceJsonPath)!;
        var oraclePath = Path.Combine(reconDirectory, "umodel-semantic-evidence.txt");
        var recon = new SkeletalMeshReconnaissanceEntry(reconDirectory, editable.ReconnaissanceJsonPath,
            Path.Combine(reconDirectory, "arkive-skeletalmesh-recon.txt"), File.Exists(oraclePath) ? oraclePath : null, report);
        var export = new SkeletalMeshEditableExportResult(directory, glbPath, manifestPath, editable, inspection,
            TimeSpan.FromSeconds(editable.DurationSeconds));
        return await ExtractAsync(new SkeletalMeshAssetKitRequest(recon, export, outputDirectory, inGameNames, packageSearchRoot),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"The retained mesh evidence is empty: {path}");
    }

    private static async Task VerifyHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The retained mesh source changed: {path}. Export it again from the clean package.");
    }

    public async Task<SkeletalMeshAssetKitResult> ExtractAsync(
        SkeletalMeshAssetKitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var report = request.Reconnaissance.Report;
        var editable = request.EditableExport;
        ValidateIdentity(report, editable.Manifest);

        var output = Path.GetFullPath(request.OutputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException($"The asset-kit directory is not empty: {output}");
        Directory.CreateDirectory(output);

        var meshDirectory = Path.Combine(output, "editable-mesh");
        var dependencyDirectory = Path.Combine(output, "reference-dependencies");
        Directory.CreateDirectory(meshDirectory);
        Directory.CreateDirectory(dependencyDirectory);

        var glbPath = Path.Combine(meshDirectory, Path.GetFileName(editable.GlbPath));
        var editableManifestPath = Path.Combine(meshDirectory, Path.GetFileName(editable.ManifestPath));
        File.Copy(editable.GlbPath, glbPath, overwrite: false);
        File.Copy(editable.ManifestPath, editableManifestPath, overwrite: false);
        File.Copy(request.Reconnaissance.JsonPath,
            Path.Combine(meshDirectory, Path.GetFileName(request.Reconnaissance.JsonPath)), overwrite: false);
        File.Copy(request.Reconnaissance.TextPath,
            Path.Combine(meshDirectory, Path.GetFileName(request.Reconnaissance.TextPath)), overwrite: false);
        if (!string.IsNullOrWhiteSpace(request.Reconnaissance.OracleTextPath) &&
            File.Exists(request.Reconnaissance.OracleTextPath))
        {
            File.Copy(request.Reconnaissance.OracleTextPath,
                Path.Combine(meshDirectory, Path.GetFileName(request.Reconnaissance.OracleTextPath)), overwrite: false);
        }

        var selected = new UmodelAssetEntry(
            report.NativeExportIndex ?? report.ExportIndex,
            report.SerialOffset,
            report.SerialSize,
            report.ClassName,
            report.ObjectName);
        var packageRoot = request.PackageSearchRoot ?? Path.GetDirectoryName(report.PackagePath) ??
                          throw new InvalidDataException("The source package has no parent directory.");
        var exported = await _bridge.ExportInPackageRootAsync(
            report.PackagePath,
            selected,
            dependencyDirectory,
            packageRoot,
            allowPackageWideFallback: false,
            cancellationToken).ConfigureAwait(false);
        var targets = GameAssetDependencyResolver.ParseExportTargets(
            dependencyDirectory,
            report.ObjectName,
            exported.OutputFiles);
        // UModel's glTF names materials without their group; the mesh's own slot references carry it.
        var materialPaths = (report.Oracle?.Materials ?? [])
            .Select(ReferencePath)
            .GroupBy(NormalizeObjectReference, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var dependencies = targets
            .SelectMany(target => target.ObjectPath is null &&
                                  target.ClassName.Contains("Material", StringComparison.OrdinalIgnoreCase) &&
                                  materialPaths.TryGetValue(target.ObjectName, out var paths)
                ? paths.Select(path => target with { ObjectPath = path })
                : [target])
            .Select(target => new SkeletalMeshAssetKitDependency(
                target.PackageName,
                target.ObjectName,
                target.ClassName,
                target.ClassName.Contains("Material", StringComparison.OrdinalIgnoreCase)
                    ? "Material name is used by the selected mesh's glTF material table."
                    : "Texture2D is referenced by one of the selected mesh's direct material property exports.",
                FindDependencyFiles(dependencyDirectory, exported.OutputFiles, target),
                target.ObjectPath))
            .ToArray();

        var materialSlots = (report.Oracle?.Materials ?? [])
            .Select(NormalizeObjectReference)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var resolvedMaterials = dependencies
            .Where(item => item.ClassName.Contains("Material", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.ObjectName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unresolved = materialSlots.Where(name => !resolvedMaterials.Contains(name)).ToArray();

        var files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals(
                "arkive-skeletalmesh-asset-kit.json", StringComparison.OrdinalIgnoreCase) &&
                           !Path.GetFileName(path).Equals(
                               "arkive-skeletalmesh-asset-kit.txt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => DescribeFile(output, path))
            .ToArray();
        watch.Stop();
        var kitId = Guid.NewGuid().ToString("N");
        var manifest = new SkeletalMeshAssetKitManifest(
            CurrentSchemaVersion,
            kitId,
            DateTimeOffset.UtcNow,
            report.PackagePath,
            report.PhysicalPackageName,
            report.PackageAlias,
            report.PackageSha256,
            report.NativeExportIndex ?? report.ExportIndex,
            report.ClassName,
            report.ObjectName,
            (request.InGameNames ?? [])
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Relative(output, glbPath),
            Relative(output, editableManifestPath),
            editable.Manifest.Lods.Count,
            editable.Manifest.BoneCount,
            materialSlots,
            dependencies,
            files,
            unresolved,
            exported.Diagnostics,
            watch.Elapsed.TotalSeconds);
        var manifestPath = Path.Combine(output, "arkive-skeletalmesh-asset-kit.json");
        var summaryPath = Path.Combine(output, "arkive-skeletalmesh-asset-kit.txt");
        await WriteJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        await WriteTextAtomicAsync(summaryPath, RenderSummary(manifest), cancellationToken).ConfigureAwait(false);
        return new SkeletalMeshAssetKitResult(output, manifestPath, summaryPath, manifest, watch.Elapsed);
    }

    private static void ValidateIdentity(
        SkeletalMeshReconnaissanceReport report,
        SkeletalMeshEditableExportManifest editable)
    {
        if (report.Readiness != SkeletalMeshReadiness.NoOpReady || report.NativeLayout is null)
            throw new NotSupportedException("Native mesh reconnaissance has not proven an editable layout.");
        if (!Path.GetFullPath(report.PackagePath).Equals(
                Path.GetFullPath(editable.SourcePackagePath), StringComparison.OrdinalIgnoreCase) ||
            !report.PackageSha256.Equals(editable.SourcePackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !report.ObjectName.Equals(editable.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            (report.NativeExportIndex ?? report.ExportIndex) != editable.NativeExportIndex)
        {
            throw new InvalidDataException(
                "The editable GLB and reconnaissance report do not describe the same clean mesh export.");
        }
        if (!File.Exists(editable.SourcePackagePath) || !File.Exists(editable.ReconnaissanceJsonPath))
            throw new FileNotFoundException("The editable mesh's clean source evidence is incomplete.");
    }

    private static IReadOnlyList<string> FindDependencyFiles(
        string outputDirectory,
        IReadOnlyList<string> outputFiles,
        GameAssetDependencyTarget target) => outputFiles
        .Where(path =>
        {
            var relative = Relative(outputDirectory, path);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Length < 3 ||
                !parts[0].Equals(target.PackageName, StringComparison.OrdinalIgnoreCase) ||
                !parts[1].Equals(target.ClassName, StringComparison.OrdinalIgnoreCase)) return false;
            var file = Path.GetFileName(path);
            var name = file.EndsWith(".props.txt", StringComparison.OrdinalIgnoreCase)
                ? file[..^".props.txt".Length]
                : Path.GetFileNameWithoutExtension(file);
            return name.Equals(target.ObjectName, StringComparison.OrdinalIgnoreCase);
        })
        .Select(path => Relative(outputDirectory, path))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static SkeletalMeshAssetKitFile DescribeFile(string root, string path)
    {
        var relative = Relative(root, path);
        var role = relative.StartsWith("editable-mesh", StringComparison.OrdinalIgnoreCase)
            ? "Arkive editable mesh evidence"
            : relative.Contains($"{Path.DirectorySeparatorChar}Texture2D{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
                ? "Referenced texture image"
                : relative.Contains($"{Path.DirectorySeparatorChar}Material", StringComparison.OrdinalIgnoreCase)
                    ? "Referenced material evidence"
                    : "UModel mesh/reference export";
        var info = new FileInfo(path);
        using var stream = File.OpenRead(path);
        return new SkeletalMeshAssetKitFile(
            relative,
            role,
            info.Length,
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static string NormalizeObjectReference(string value)
    {
        var objectPath = ReferencePath(value);
        var dot = objectPath.LastIndexOf('.');
        return dot >= 0 ? objectPath[(dot + 1)..] : objectPath;
    }

    /// <summary>MaterialInstanceConstant'mat.name' -> mat.name</summary>
    private static string ReferencePath(string value)
    {
        var quote = value.IndexOf('\'');
        var endQuote = value.LastIndexOf('\'');
        return quote >= 0 && endQuote > quote ? value[(quote + 1)..endQuote] : value;
    }

    private static string RenderSummary(SkeletalMeshAssetKitManifest manifest)
    {
        var materials = manifest.Dependencies.Count(item =>
            item.ClassName.Contains("Material", StringComparison.OrdinalIgnoreCase));
        var textures = manifest.Dependencies.Count(item =>
            item.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase));
        var builder = new StringBuilder();
        builder.AppendLine("Arkive SkeletalMesh complete asset kit");
        builder.AppendLine($"Created: {manifest.CreatedAt:O}");
        builder.AppendLine($"Source: {manifest.PackageAlias}.{manifest.ObjectName}");
        if (manifest.InGameNames.Count > 0)
            builder.AppendLine($"In-game name(s): {string.Join("; ", manifest.InGameNames)}");
        builder.AppendLine($"Physical package: {manifest.PhysicalPackageName}");
        builder.AppendLine($"Package SHA-256: {manifest.SourcePackageSha256}");
        builder.AppendLine($"Editable GLB: {manifest.EditableGlbRelativePath}");
        builder.AppendLine($"LODs / bones: {manifest.LodCount} / {manifest.BoneCount}");
        builder.AppendLine($"Exact dependencies: {materials} material(s), {textures} texture(s)");
        builder.AppendLine();
        builder.AppendLine("Materials and textures below were resolved from the selected mesh's actual glTF material table and serialized material Texture2D references.");
        builder.AppendLine("They are reference exports. Use Start texture mod or Start material mod in Arkive for any asset you intend to rebuild into a UPK.");
        builder.AppendLine();
        builder.AppendLine("Dependencies");
        foreach (var item in manifest.Dependencies)
            builder.AppendLine($"  {item.ClassName} {item.PackageName}.{item.ObjectName}");
        if (manifest.UnresolvedMaterialSlots.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Material slots not exported by UModel (usually shared/engine dependencies)");
            foreach (var item in manifest.UnresolvedMaterialSlots) builder.AppendLine($"  {item}");
        }
        builder.AppendLine();
        builder.AppendLine("No game package was modified.");
        return builder.ToString();
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task WriteTextAtomicAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, value, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
