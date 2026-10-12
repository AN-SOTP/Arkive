using System.Text.Json;
using Arkive.Core.Materials;

namespace Arkive.Core.Mods;

public enum ModGraphSeverity { Information, Warning, Blocking }

public sealed record ModProjectConflict(
    ModGraphSeverity Severity,
    string Code,
    string PackageAlias,
    string Target,
    string Message,
    IReadOnlyList<string> PackageProjectIds,
    IReadOnlyList<string> ObjectNames);

public sealed record ModAssetDependency(
    string PackageProjectId,
    string PackageAlias,
    string SourceKind,
    string SourceObject,
    string Relation,
    string TargetKind,
    string TargetObject,
    string Resolution,
    string Evidence,
    string? SourceManifestPath = null);

public sealed record ModConflictDependencyReport(
    DateTimeOffset CreatedAt,
    int PackageProjectCount,
    int BuiltProjectCount,
    int AssetCount,
    IReadOnlyList<ModProjectConflict> Conflicts,
    IReadOnlyList<ModAssetDependency> Dependencies)
{
    public int BlockingConflictCount => Conflicts.Count(item => item.Severity == ModGraphSeverity.Blocking);
    public int WarningCount => Conflicts.Count(item => item.Severity == ModGraphSeverity.Warning);
    public string Summary =>
        $"{PackageProjectCount:N0} package project(s), {AssetCount:N0} composed asset(s), " +
        $"{BlockingConflictCount:N0} blocking conflict(s), {WarningCount:N0} warning(s), " +
        $"{Dependencies.Count:N0} dependency edge(s).";
}

/// <summary>
/// Builds an auditable graph from committed Package Project revisions. Hard
/// conflicts are based on physical target or exact object identity; referenced
/// materials, textures, skeleton contracts and retained collision are shown as
/// dependencies and are never promoted from naming similarity alone.
/// </summary>
public sealed class ModConflictDependencyService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<ModConflictDependencyReport> AnalyzeAsync(
        IReadOnlyList<GuidedTexturePackageEntry> packages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var conflicts = new List<ModProjectConflict>();
        var dependencies = new List<ModAssetDependency>();
        var built = packages.Select(package => (Package: package, Revision: Latest(package)))
            .ToArray();

        foreach (var targetGroup in packages.GroupBy(
                     package => NormalizePath(package.Manifest.SourcePackagePath),
                     StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            var members = targetGroup.ToArray();
            conflicts.Add(new ModProjectConflict(
                ModGraphSeverity.Blocking,
                "MCV001",
                string.Join(", ", members.Select(item => item.Manifest.PackageAlias).Distinct(
                    StringComparer.OrdinalIgnoreCase)),
                targetGroup.Key,
                "Multiple Package Projects target the same physical client UPK. Keep one project and compose every desired asset into it.",
                members.Select(item => item.Manifest.PackageProjectId).ToArray(),
                []));
        }

        foreach (var (package, revision) in built)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(package.Manifest.SourcePackagePath))
            {
                conflicts.Add(Issue(package, ModGraphSeverity.Blocking, "MCV002",
                    package.Manifest.SourcePackagePath,
                    "The clean source/client UPK is missing.", []));
            }
            if (revision is null)
            {
                conflicts.Add(Issue(package, ModGraphSeverity.Warning, "MCV003",
                    package.Manifest.SourcePackagePath,
                    "This Package Project has no committed build yet; its editable projects are not represented in the install graph.", []));
                continue;
            }
            if (!File.Exists(revision.PayloadPath) || !File.Exists(revision.ModBundleManifestPath))
            {
                conflicts.Add(Issue(package, ModGraphSeverity.Blocking, "MCV004",
                    revision.PayloadPath,
                    "The latest committed package payload or bundle manifest is missing. Rebuild the project.", []));
            }

            var assets = Assets(revision).ToArray();
            foreach (var duplicate in assets.GroupBy(
                         item => NormalizeObject(item.ObjectName),
                         StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            {
                var rows = duplicate.ToArray();
                conflicts.Add(Issue(package, ModGraphSeverity.Blocking, "MCV005",
                    package.Manifest.SourcePackagePath,
                    "The latest revision contains more than one replacement for the same native object.",
                    rows.Select(item => $"{item.Kind}:{item.ObjectName}").ToArray()));
            }

            var modifiedNames = assets.GroupBy(
                    item => NormalizeObject(item.ObjectName),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets)
            {
                dependencies.Add(new ModAssetDependency(
                    package.Manifest.PackageProjectId,
                    package.Manifest.PackageAlias,
                    "Package",
                    package.Manifest.PackageAlias,
                    "Composes replacement",
                    asset.Kind,
                    asset.ObjectName,
                    "Modified in this package",
                    "Committed Arkive package revision",
                    asset.ManifestPath));
            }

            foreach (var material in revision.MaterialAssets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AddMaterialDependenciesAsync(
                    package, material, modifiedNames, dependencies, conflicts, cancellationToken)
                    .ConfigureAwait(false);
            }
            foreach (var mesh in revision.MeshAssets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AddMeshDependenciesAsync(
                    package, "SkeletalMesh", mesh.ObjectName, mesh.RebasedEditableManifestPath,
                    modifiedNames, dependencies, conflicts, cancellationToken).ConfigureAwait(false);
            }
            foreach (var mesh in revision.StaticMeshAssets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AddMeshDependenciesAsync(
                    package, "StaticMesh", mesh.ObjectName, mesh.RebasedEditableManifestPath,
                    modifiedNames, dependencies, conflicts, cancellationToken).ConfigureAwait(false);
                dependencies.Add(new ModAssetDependency(
                    package.Manifest.PackageProjectId, package.Manifest.PackageAlias,
                    "StaticMesh", mesh.ObjectName, "Uses cooked collision", "Collision",
                    "Source BodySetup / kDOP", "Preserved from clean source",
                    mesh.VariableSizeReplacement
                        ? "Topology changed; source collision requires an in-game fit review."
                        : "Collision bytes are retained unchanged.",
                    mesh.CompilationManifestPath));
            }
        }

        AddCrossPackageDependencyResolution(dependencies);
        return new ModConflictDependencyReport(
            DateTimeOffset.UtcNow,
            packages.Count,
            built.Count(item => item.Revision is not null),
            built.Where(item => item.Revision is not null).Sum(item => item.Revision!.TotalAssetCount),
            conflicts.OrderByDescending(item => item.Severity)
                .ThenBy(item => item.PackageAlias, StringComparer.OrdinalIgnoreCase).ToArray(),
            dependencies.OrderBy(item => item.PackageAlias, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SourceObject, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Relation, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static async Task AddMaterialDependenciesAsync(
        GuidedTexturePackageEntry package,
        GuidedMaterialPackageAssetRevision material,
        IReadOnlyDictionary<string, AssetIdentity> modifiedNames,
        ICollection<ModAssetDependency> dependencies,
        ICollection<ModProjectConflict> conflicts,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(material.EditDocumentPath))
        {
            conflicts.Add(Issue(package, ModGraphSeverity.Warning, "MCV010",
                material.EditDocumentPath,
                $"Dependency details for material {material.ObjectName} are unavailable because its edit document is missing.",
                [material.ObjectName]));
            return;
        }
        try
        {
            await using var stream = File.OpenRead(material.EditDocumentPath);
            var document = await JsonSerializer.DeserializeAsync<MaterialInstanceEditDocument>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (document is null) throw new JsonException("Material edit document is empty.");
            foreach (var texture in document.Textures.Where(item => item.ObjectReference != 0))
            {
                AddReference(package, "Material", material.ObjectName,
                    $"Texture parameter: {texture.Name}", "Texture", texture.ResolvedObject,
                    material.EditDocumentPath, modifiedNames, dependencies);
            }
            if (document.Parent is { ObjectReference: not 0 } parent)
            {
                AddReference(package, "Material", material.ObjectName,
                    "Parent material", "Material", parent.ResolvedObject,
                    material.EditDocumentPath, modifiedNames, dependencies);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            conflicts.Add(Issue(package, ModGraphSeverity.Warning, "MCV011",
                material.EditDocumentPath,
                $"Could not read dependencies for material {material.ObjectName}: {exception.Message}",
                [material.ObjectName]));
        }
    }

    private static async Task AddMeshDependenciesAsync(
        GuidedTexturePackageEntry package,
        string kind,
        string objectName,
        string editableManifestPath,
        IReadOnlyDictionary<string, AssetIdentity> modifiedNames,
        ICollection<ModAssetDependency> dependencies,
        ICollection<ModProjectConflict> conflicts,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(editableManifestPath))
        {
            conflicts.Add(Issue(package, ModGraphSeverity.Warning, "MCV020",
                editableManifestPath,
                $"Dependency details for {objectName} are unavailable because its rebased editable manifest is missing.",
                [objectName]));
            return;
        }
        try
        {
            await using var stream = File.OpenRead(editableManifestPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            if (kind == "SkeletalMesh" && root.TryGetProperty("referenceSkeletonSha256", out var skeleton))
            {
                dependencies.Add(new ModAssetDependency(
                    package.Manifest.PackageProjectId, package.Manifest.PackageAlias,
                    kind, objectName, "Requires exact reference skeleton", "Skeleton contract",
                    Short(skeleton.GetString()), "Embedded / preserved",
                    "Arkive preflight and animation stress validation require the target skeleton identity.",
                    editableManifestPath));
            }
            if (!root.TryGetProperty("reconnaissanceJsonPath", out var reconProperty) ||
                string.IsNullOrWhiteSpace(reconProperty.GetString()) ||
                !File.Exists(reconProperty.GetString())) return;
            await AddOracleMaterialsAsync(package, kind, objectName, reconProperty.GetString()!,
                modifiedNames, dependencies, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            conflicts.Add(Issue(package, ModGraphSeverity.Warning, "MCV021", editableManifestPath,
                $"Could not read dependencies for {objectName}: {exception.Message}", [objectName]));
        }
    }

    private static async Task AddOracleMaterialsAsync(
        GuidedTexturePackageEntry package,
        string kind,
        string objectName,
        string reconnaissancePath,
        IReadOnlyDictionary<string, AssetIdentity> modifiedNames,
        ICollection<ModAssetDependency> dependencies,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(reconnaissancePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("oracle", out var oracle) ||
            oracle.ValueKind != JsonValueKind.Object ||
            !oracle.TryGetProperty("materials", out var materials) ||
            materials.ValueKind != JsonValueKind.Array) return;
        foreach (var material in materials.EnumerateArray().Select(item => item.GetString())
                     .Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddReference(package, kind, objectName, "Material slot", "Material", material!,
                reconnaissancePath, modifiedNames, dependencies);
        }
    }

    private static void AddReference(
        GuidedTexturePackageEntry package,
        string sourceKind,
        string sourceObject,
        string relation,
        string targetKind,
        string targetObject,
        string evidencePath,
        IReadOnlyDictionary<string, AssetIdentity> modifiedNames,
        ICollection<ModAssetDependency> dependencies)
    {
        var normalized = NormalizeObject(targetObject);
        var qualifier = PackageQualifier(targetObject);
        var targetsLocalPackage = string.IsNullOrWhiteSpace(qualifier) ||
                                  qualifier.Equals(package.Manifest.PackageAlias,
                                      StringComparison.OrdinalIgnoreCase) ||
                                  qualifier.Equals(Path.GetFileNameWithoutExtension(
                                          package.Manifest.PhysicalPackageName),
                                      StringComparison.OrdinalIgnoreCase);
        AssetIdentity? target = null;
        var local = targetsLocalPackage && modifiedNames.TryGetValue(normalized, out target);
        dependencies.Add(new ModAssetDependency(
            package.Manifest.PackageProjectId, package.Manifest.PackageAlias,
            sourceKind, sourceObject, relation, local ? target!.Kind : targetKind,
            targetObject, local ? "Modified in this package" : "Existing source dependency",
            local
                ? "Exact object-name identity in the committed revision."
                : "Serialized object reference reported by the material document or UModel oracle.",
            evidencePath));
    }

    private static void AddCrossPackageDependencyResolution(IList<ModAssetDependency> dependencies)
    {
        var modified = dependencies.Where(item => item.Relation == "Composes replacement")
            .GroupBy(item => NormalizeObject(item.TargetObject), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < dependencies.Count; index++)
        {
            var dependency = dependencies[index];
            if (dependency.Relation == "Composes replacement" ||
                dependency.Resolution == "Modified in this package") continue;
            var qualifier = PackageQualifier(dependency.TargetObject);
            if (string.IsNullOrWhiteSpace(qualifier) ||
                !modified.TryGetValue(NormalizeObject(dependency.TargetObject), out var targets)) continue;
            var cross = targets.FirstOrDefault(target =>
                !target.PackageProjectId.Equals(dependency.PackageProjectId, StringComparison.OrdinalIgnoreCase) &&
                target.PackageAlias.Equals(qualifier, StringComparison.OrdinalIgnoreCase));
            if (cross is null) continue;
            dependencies[index] = dependency with
            {
                Resolution = $"Modified by package project {cross.PackageAlias}",
                Evidence = dependency.Evidence + " Exact object identity also appears in another committed Package Project.",
            };
        }
    }

    private static IEnumerable<AssetIdentity> Assets(GuidedTexturePackageRevision revision) =>
        revision.Assets.Select(item => new AssetIdentity("Texture", item.ObjectName,
                item.ReplacementUpkBuildManifestPath))
            .Concat(revision.MaterialAssets.Select(item => new AssetIdentity(
                "Material", item.ObjectName, item.EditDocumentPath)))
            .Concat(revision.MeshAssets.Select(item => new AssetIdentity(
                "SkeletalMesh", item.ObjectName, item.RebasedEditManifestPath)))
            .Concat(revision.StaticMeshAssets.Select(item => new AssetIdentity(
                "StaticMesh", item.ObjectName, item.RebasedEditManifestPath)));

    private static GuidedTexturePackageRevision? Latest(GuidedTexturePackageEntry package) =>
        package.Manifest.LatestRevisionId is null
            ? package.Manifest.Revisions.LastOrDefault()
            : package.Manifest.Revisions.FirstOrDefault(item => item.RevisionId.Equals(
                package.Manifest.LatestRevisionId, StringComparison.OrdinalIgnoreCase));

    private static ModProjectConflict Issue(
        GuidedTexturePackageEntry package,
        ModGraphSeverity severity,
        string code,
        string target,
        string message,
        IReadOnlyList<string> objects) => new(
        severity, code, package.Manifest.PackageAlias, target, message,
        [package.Manifest.PackageProjectId], objects);

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return path.Trim(); }
    }

    private static string NormalizeObject(string value)
    {
        var trimmed = value.Trim().Trim('\'', '"');
        var colon = trimmed.LastIndexOf(':');
        if (colon >= 0) trimmed = trimmed[(colon + 1)..];
        var dot = trimmed.LastIndexOf('.');
        if (dot >= 0) trimmed = trimmed[(dot + 1)..];
        return trimmed.Trim().ToUpperInvariant();
    }

    private static string? PackageQualifier(string value)
    {
        var trimmed = value.Trim().Trim('\'', '"');
        var quote = trimmed.IndexOf('\'');
        if (quote >= 0) trimmed = trimmed[(quote + 1)..].TrimEnd('\'');
        var colon = trimmed.LastIndexOf(':');
        if (colon >= 0) trimmed = trimmed[(colon + 1)..];
        var dot = trimmed.LastIndexOf('.');
        return dot > 0 ? trimmed[..dot].Trim() : null;
    }

    private static string Short(string? value) => string.IsNullOrWhiteSpace(value)
        ? "unknown"
        : value[..Math.Min(16, value.Length)] + (value.Length > 16 ? "..." : string.Empty);

    private sealed record AssetIdentity(string Kind, string ObjectName, string ManifestPath);
}
