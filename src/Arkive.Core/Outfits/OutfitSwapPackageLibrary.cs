using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Inspection;
using Arkive.Core.Meshes;
using Arkive.Core.Packages;
using Arkive.Core.Viewer;

namespace Arkive.Core.Outfits;

/// <summary>A skeletal mesh found in the library: where it is and how the viewer opens it.</summary>
public sealed record OutfitSwapSource(string ObjectName, string PackageAlias, string CleanPackagePath, ModelViewerRequest ViewerRequest);

/// <summary>
/// Outfit pieces from the indexed game library, read from their clean packages (a backup when the game file carries a
/// mod). Editable GLBs are exported once per package version and kept in a cache folder.
/// </summary>
public sealed class OutfitSwapPackageLibrary(
    PackageIndexStore index,
    string packagesRoot,
    UpkPackageReader reader,
    IChunkProtectionProvider protectionProvider,
    SkeletalMeshReconnaissanceService reconnaissance,
    SkeletalMeshReconnaissanceWorkspaceStore reconnaissanceStore,
    SkeletalMeshEditableExportService editableExport,
    string cacheRoot,
    Func<string, CancellationToken, Task<string>>? cleanPackage = null) : IOutfitSwapLibrary
{
    private readonly AssetCatalogReader _catalogReader = new(reader, protectionProvider);
    private readonly ConcurrentDictionary<string, Task<OutfitSwapSource?>> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<string?>> _exports = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _exportGate = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));

    public async Task<IReadOnlyCollection<string>> FindAsync(string prefix, CancellationToken cancellationToken)
    {
        var result = await index.SearchAsync(packagesRoot, prefix, "SkeletalMesh", null, PackageIndexSearchScope.ObjectName,
            null, resultLimit: 20_000, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var match in result.Matches) _sources.TryAdd(match.ObjectName, Task.FromResult<OutfitSwapSource?>(Source(match)));
        return result.Matches.Select(match => match.ObjectName)
            .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<OutfitSwapSource?> LocateAsync(string objectName, CancellationToken cancellationToken)
    {
        var source = await _sources.GetOrAdd(objectName, async name =>
        {
            var result = await index.SearchAsync(packagesRoot, name, "SkeletalMesh", null, PackageIndexSearchScope.ObjectName,
                null, resultLimit: 50, cancellationToken: cancellationToken).ConfigureAwait(false);
            var match = result.Matches.FirstOrDefault(item => item.ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase));
            return match is null ? null : Source(match);
        }).ConfigureAwait(false);
        // The viewer reads the piece's materials and pictures from its clean package: with a mod applied (an earlier
        // swap onto this outfit, say), the game's file holds the mod's materials.
        if (source is null || cleanPackage is null) return source;
        var clean = await cleanPackage(source.CleanPackagePath, cancellationToken).ConfigureAwait(false);
        return source with { ViewerRequest = source.ViewerRequest with { PackagePath = clean } };
    }

    private OutfitSwapSource Source(PackageIndexMatch match) => new(match.ObjectName, match.PackageAlias, match.SourcePath,
        new ModelViewerRequest(match.SourcePath, match.PackageAlias, packagesRoot, match.ExportIndex, match.SerialOffset, match.SerialSize,
            match.ClassName, match.ObjectName));

    /// <summary>The piece's editable GLB exported from its clean package, or null when Arkive cannot rebuild the piece.</summary>
    public Task<string?> EditableGlbAsync(string objectName, CancellationToken cancellationToken) =>
        _exports.GetOrAdd(objectName, name => ExportAsync(name, cancellationToken));

    public async Task<OutfitSwapMesh?> LoadAsync(string objectName, CancellationToken cancellationToken)
    {
        var glb = await EditableGlbAsync(objectName, cancellationToken).ConfigureAwait(false);
        if (glb is null) return null;
        try { return OutfitSwapMesh.Read(await File.ReadAllBytesAsync(glb, cancellationToken).ConfigureAwait(false)); }
        catch (InvalidDataException) { return null; }
    }

    private async Task<string?> ExportAsync(string objectName, CancellationToken cancellationToken)
    {
        var source = await LocateAsync(objectName, cancellationToken).ConfigureAwait(false);
        if (source is null) return null;
        var packagePath = cleanPackage is null ? source.CleanPackagePath
            : await cleanPackage(source.CleanPackagePath, cancellationToken).ConfigureAwait(false);
        var package = new FileInfo(packagePath);
        if (!package.Exists) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{package.FullName}|{package.Length}|{package.LastWriteTimeUtc.Ticks}|{objectName}|{SkeletalMeshEditableExportService.CurrentSchemaVersion}")))[..24];
        var directory = Path.Combine(cacheRoot, key);
        var glb = Path.Combine(directory, "piece.glb");
        if (File.Exists(glb)) return glb;
        await _exportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(glb)) return glb;
            var export = _catalogReader.Read(package.FullName).Assets.FirstOrDefault(asset =>
                asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase) &&
                asset.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
            if (export is null) return null;
            var report = await reconnaissance.InspectAsync(new SkeletalMeshReconnaissanceRequest(package.FullName, source.PackageAlias,
                export.ExportIndex - 1, AssetKind.SkeletalMesh, export.ClassName, export.ObjectName, export.SerialOffset,
                export.SerialSize) { PackageSearchRoot = packagesRoot }, cancellationToken).ConfigureAwait(false);
            if (report.Readiness != SkeletalMeshReadiness.NoOpReady) return null;
            var saved = await reconnaissanceStore.SaveAsync(report, cancellationToken).ConfigureAwait(false);
            var temporary = Path.Combine(cacheRoot, $"{key}.{Guid.NewGuid():N}.tmp");
            var result = await editableExport.ExportAsync(new SkeletalMeshEditableExportRequest(report, saved.JsonPath, temporary),
                cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(directory);
            File.Copy(result.GlbPath, glb + ".tmp", overwrite: true);
            File.Move(glb + ".tmp", glb, overwrite: true);
            try { Directory.Delete(temporary, recursive: true); }
            catch (IOException) { }
            return glb;
        }
        finally
        {
            _exportGate.Release();
        }
    }
}
