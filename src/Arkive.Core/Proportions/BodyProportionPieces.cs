using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Inspection;
using Arkive.Core.Meshes;
using Arkive.Core.Packages;
using Arkive.Core.Viewer;

namespace Arkive.Core.Proportions;

/// <summary>One outfit piece to reshape, read from its clean (unmodded) package.</summary>
/// <param name="PackageSearchRoot">The game's Packages folder, where the piece's materials, textures and shader cache are
/// found. The clean package can be a backup copy outside it (when the game file carries a mod).</param>
public sealed record BodyProportionPieceRequest(string CleanPackagePath, string PackageAlias, string ObjectName, string? PackageSearchRoot = null);

/// <summary>An outfit piece ready for the proportion editor: its editable GLB and the viewer request for its materials.</summary>
public sealed record BodyProportionPiece(
    BodyProportionPieceRequest Request,
    string EditableGlbPath,
    ModelViewerRequest ViewerRequest,
    BodyProfile Profile);

/// <summary>
/// Prepares outfit pieces for previewing proportion changes: an editable GLB per piece (the same export a mod
/// edit starts from, so the preview and the saved mod use identical vertices), cached by package version.
/// Nothing here creates a mod or touches game files.
/// </summary>
public sealed class BodyProportionPieceService(
    UpkPackageReader reader,
    IChunkProtectionProvider protectionProvider,
    SkeletalMeshReconnaissanceService reconnaissance,
    SkeletalMeshReconnaissanceWorkspaceStore reconnaissanceStore,
    SkeletalMeshEditableExportService editableExport,
    string cacheRoot)
{
    private readonly AssetCatalogReader _catalogReader = new(reader, protectionProvider);

    public async Task<BodyProportionPiece> PrepareAsync(BodyProportionPieceRequest request, CancellationToken cancellationToken = default)
    {
        var package = new FileInfo(request.CleanPackagePath);
        if (!package.Exists) throw new FileNotFoundException("The outfit's package was not found.", request.CleanPackagePath);
        var catalogue = _catalogReader.Read(package.FullName);
        var matches = catalogue.Assets.Where(asset => asset.ObjectName.Equals(request.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                                                       asset.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"{request.ObjectName} is missing or appears more than once in {package.Name}.");
        var export = matches[0];
        var viewerRequest = new ModelViewerRequest(package.FullName, request.PackageAlias, request.PackageSearchRoot ?? package.DirectoryName, export.ExportIndex,
            export.SerialOffset, export.SerialSize, export.ClassName, export.ObjectName);

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{package.FullName}|{package.Length}|{package.LastWriteTimeUtc.Ticks}|{export.ObjectName}|{SkeletalMeshEditableExportService.CurrentSchemaVersion}")))[..24];
        var directory = Path.Combine(cacheRoot, key);
        var glb = Path.Combine(directory, "piece.glb");
        if (!File.Exists(glb))
        {
            var report = await reconnaissance.InspectAsync(new SkeletalMeshReconnaissanceRequest(package.FullName, request.PackageAlias,
                export.ExportIndex - 1, AssetKind.SkeletalMesh, export.ClassName, export.ObjectName, export.SerialOffset,
                export.SerialSize), cancellationToken).ConfigureAwait(false);
            if (report.Readiness != SkeletalMeshReadiness.NoOpReady)
                throw new InvalidDataException(report.Issues.FirstOrDefault(issue => issue.Severity == "Error")?.Message
                    ?? $"Arkive cannot rebuild {export.ObjectName} yet.");
            var saved = await reconnaissanceStore.SaveAsync(report, cancellationToken).ConfigureAwait(false);
            var temporary = Path.Combine(cacheRoot, $"{key}.{Guid.NewGuid():N}.tmp");
            var result = await editableExport.ExportAsync(new SkeletalMeshEditableExportRequest(report, saved.JsonPath, temporary),
                cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(directory);
            File.Copy(result.GlbPath, glb + ".tmp", overwrite: true);
            File.Move(glb + ".tmp", glb, overwrite: true);
            try { Directory.Delete(temporary, recursive: true); }
            catch (IOException) { }
        }
        var profile = BodyProportionEngine.Build(await File.ReadAllBytesAsync(glb, cancellationToken).ConfigureAwait(false), default,
            objectName: export.ObjectName).Profile;
        return new BodyProportionPiece(request, glb, viewerRequest, profile);
    }
}

/// <summary>
/// Joins the viewer scenes of several outfit pieces into one scene, so a whole outfit is shown and reshaped
/// together. Each piece keeps its own model and materials; file links are rewritten relative to the cache root.
/// </summary>
public static class BodyProportionSceneCombiner
{
    public sealed record CombinedScene(string SceneJson, IReadOnlyList<CombinedPart> Parts);

    public sealed record CombinedPart(string ObjectName, string ModelUrl, int MaterialOffset, int MaterialCount);

    public static CombinedScene Combine(IReadOnlyList<ModelViewerSceneResult> pieces, string cacheRoot)
    {
        if (pieces.Count == 0) throw new ArgumentException("No pieces to combine.", nameof(pieces));
        JsonObject? combined = null;
        var materials = new JsonArray();
        var textures = new JsonArray();
        var diagnostics = new JsonArray();
        var parts = new List<CombinedPart>();
        foreach (var piece in pieces)
        {
            var folder = Path.GetRelativePath(cacheRoot, piece.Directory).Replace('\\', '/');
            var node = JsonSerializer.SerializeToNode(piece.Scene, ModelViewerSceneService.JsonOptions)!.AsObject();
            Rebase(node, piece.Directory, folder);
            var offset = materials.Count;
            var pieceMaterials = node["materials"]?.AsArray() ?? [];
            foreach (var material in pieceMaterials.ToArray())
            {
                pieceMaterials.Remove(material);
                if (material is JsonObject entry && entry["slot"] is JsonValue slot) entry["slot"] = (int)slot + offset;
                materials.Add(material);
            }
            foreach (var texture in (node["textures"]?.AsArray() ?? []).ToArray())
            {
                texture!.Parent!.AsArray().Remove(texture);
                textures.Add(texture);
            }
            foreach (var line in (node["diagnostics"]?.AsArray() ?? []).ToArray())
            {
                var text = (string?)line;
                if (text is not null && diagnostics.All(existing => (string?)existing != $"{piece.Scene.Object.ObjectName}: {text}"))
                    diagnostics.Add($"{piece.Scene.Object.ObjectName}: {text}");
            }
            parts.Add(new CombinedPart(piece.Scene.Object.ObjectName, (string?)node["modelUrl"] ?? "", offset, materials.Count - offset));
            combined ??= node;
        }
        combined!["materials"] = materials;
        combined["textures"] = textures;
        combined["diagnostics"] = diagnostics;
        combined["parts"] = JsonSerializer.SerializeToNode(parts, ModelViewerSceneService.JsonOptions);
        return new CombinedScene(combined.ToJsonString(ModelViewerSceneService.JsonOptions), parts);
    }

    /// <summary>Prefixes every string that names a file in the piece's folder with that folder's path.</summary>
    private static void Rebase(JsonNode? node, string directory, string folder)
    {
        switch (node)
        {
            case JsonObject entry:
                foreach (var (name, child) in entry.ToArray())
                {
                    if (child is JsonValue value && value.TryGetValue<string>(out var text) && IsFile(directory, text))
                        entry[name] = $"{folder}/{text}";
                    else Rebase(child, directory, folder);
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is JsonValue value && value.TryGetValue<string>(out var text) && IsFile(directory, text))
                        array[index] = $"{folder}/{text}";
                    else Rebase(array[index], directory, folder);
                }
                break;
        }
    }

    private static bool IsFile(string directory, string text)
    {
        if (text.Length is 0 or > 400 || text.Contains('\n') || Path.IsPathRooted(text) || text.Contains("://")) return false;
        try { return File.Exists(Path.Combine(directory, Uri.UnescapeDataString(text))); }
        catch (ArgumentException) { return false; }
    }
}
