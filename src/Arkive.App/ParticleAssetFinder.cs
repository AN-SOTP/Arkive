using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.External;
using Arkive.Core.Packages;
using Arkive.Core.Particles;
using Arkive.Core.Viewer.Shaders;

namespace Arkive.App;

/// <summary>
/// What effects draw with: each emitter material's texture, settings and compiled game shaders, and each mesh
/// particle's mesh, exported once with UModel and kept in a cache folder. Objects are found through the packages that
/// refer to them (their imports name the package; the index's alias map gives its file), falling back to an index
/// search, and several materials are prepared at once.
/// </summary>
public sealed partial class ParticleAssetFinder(
    PackageIndexStore index,
    Func<string?> libraryRoot,
    Func<GameAssetMetadataIndex?> metadata,
    UmodelBridge umodel,
    UpkPackageReader reader,
    IChunkProtectionProvider protectionProvider,
    Func<CancellationToken, Task<ShaderCacheIndex?>> shaderIndex,
    string cacheRoot,
    Action<string> log)
{
    private const int Parallelism = 4;

    private readonly AssetCatalogCache _catalogs = new(new AssetCatalogReader(reader, protectionProvider));
    private readonly ParticleShaderCache _shaders = new(reader, protectionProvider);
    private readonly ConcurrentDictionary<string, Task<LocatedObject?>> _searches = new(StringComparer.OrdinalIgnoreCase);
    private Task<IReadOnlyDictionary<string, string>?>? _packageFiles;

    /// <summary>The folder the cached textures and meshes are in (the pages' asset host).</summary>
    public string CacheRoot => cacheRoot;

    /// <summary>The shared catalogue cache, for reading the effects' own packages too.</summary>
    public AssetCatalogCache Catalogs => _catalogs;

    /// <summary>Each package's file by its alias, once the index's routing is known.</summary>
    public Task<IReadOnlyDictionary<string, string>?> PackageFilesAsync(CancellationToken cancellationToken)
    {
        if (_packageFiles is { IsCompletedSuccessfully: true, Result: not null }) return _packageFiles;
        return _packageFiles = libraryRoot() is { Length: > 0 } root
            ? index.PackageFilesByAliasAsync(root, metadata(), cancellationToken)
            : Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
    }

    /// <summary>The textures, shaders and meshes the emitters draw with; progress goes from 0 to 1.</summary>
    public async Task<ParticleEditorAssets> FindAsync(IEnumerable<ParticleEmitterModel> emitters, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var list = emitters.ToArray();
        var materials = list.Where(emitter => emitter.Material is not null)
            .GroupBy(emitter => emitter.Material!, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.Key, Package: group.Select(emitter => emitter.MaterialPackage).FirstOrDefault(alias => alias is not null)))
            .ToArray();
        var meshes = list.Where(emitter => emitter.Mesh is not null)
            .GroupBy(emitter => emitter.Mesh!, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.Key, Package: group.Select(emitter => emitter.MeshPackage).FirstOrDefault(alias => alias is not null)))
            .ToArray();
        var textures = new ConcurrentDictionary<string, ParticleEditorTexture>(StringComparer.OrdinalIgnoreCase);
        var meshFiles = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var total = Math.Max(1, materials.Length + meshes.Length);
        var done = 0;
        void Step() => progress?.Report(Interlocked.Increment(ref done) / (double)total);
        var shaders = await shaderIndex(cancellationToken);
        await Parallel.ForEachAsync(materials, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (material, token) =>
            {
                if (await TextureAsync(material.Name, material.Package, shaders, token) is { } texture) textures[material.Name] = texture;
                Step();
            });
        await Parallel.ForEachAsync(meshes, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (mesh, token) =>
            {
                if (await MeshAsync(mesh.Name, mesh.Package, token) is { } file) meshFiles[mesh.Name] = file;
                Step();
            });
        return new ParticleEditorAssets(textures, meshFiles);
    }

    // The material's first texture, blend mode and settings (texture4.json) and its game shaders (shader6.json).
    private async Task<ParticleEditorTexture?> TextureAsync(string material, string? package, ShaderCacheIndex? shaders,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(cacheRoot, SafeFolderName(material));
        // Version 3 records also hold the material's colour and scalar settings; version 4 follows instances that set
        // no parameters to their parent.
        var record = Path.Combine(folder, "texture4.json");
        try
        {
            ParticleEditorTexture? texture = null;
            if (File.Exists(record)) texture = JsonSerializer.Deserialize<ParticleEditorTexture>(await File.ReadAllTextAsync(record, cancellationToken));
            var found = texture is null || shaders is not null && !File.Exists(Path.Combine(folder, "shader6.json"))
                ? await FindObjectAsync(material, "material", package, cancellationToken)
                : null;
            if (texture is null)
            {
                if (found is null) return null;
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                var files = await umodel.ExportNamedObjectAsync(found.PackagePath, material, Path.Combine(folder, "export"), cancellationToken);
                var info = ParticleMaterialInfo.Read(files, material);
                texture = new ParticleEditorTexture(info.TexturePath is null ? null : Path.GetRelativePath(cacheRoot, info.TexturePath),
                    info.Additive ? "additive" : "translucent", info.Tint, info.Scalars);
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(record, JsonSerializer.Serialize(texture), cancellationToken);
            }
            if (shaders is null) return texture;
            return texture with
            {
                Shader = await _shaders.GetAsync(shaders, material, found?.PackagePath, folder, cacheRoot, LocateAsync, log, cancellationToken),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or UmodelBridgeException)
        {
            log($"Particle material texture not found: material={material}, error={exception.Message}");
            return null;
        }
    }

    // The mesh as glTF (mesh.txt holds its path).
    private async Task<string?> MeshAsync(string mesh, string? package, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(cacheRoot, "meshes", SafeFolderName(mesh));
        var record = Path.Combine(folder, "mesh.txt");
        try
        {
            if (File.Exists(record))
            {
                var cached = (await File.ReadAllTextAsync(record, cancellationToken)).Trim();
                return cached.Length > 0 && File.Exists(Path.Combine(cacheRoot, cached)) ? cached : null;
            }
            if (await FindObjectAsync(mesh, "staticmesh", package, cancellationToken) is not { } found) return null;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            var entry = found.Entry;
            var export = await umodel.ExportForViewerAsync(found.PackagePath,
                new UmodelAssetEntry(entry.ExportIndex, entry.SerialOffset, entry.SerialSize, entry.ClassName, entry.ObjectName),
                Path.Combine(folder, "export"), libraryRoot(), cancellationToken);
            var model = export.OutputFiles.FirstOrDefault(path => path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) ||
                                                            path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase));
            var relative = model is null ? string.Empty : Path.GetRelativePath(cacheRoot, model);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(record, relative, cancellationToken);
            return relative.Length > 0 ? relative : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or UmodelBridgeException)
        {
            log($"Particle mesh not exported: mesh={mesh}, error={exception.Message}");
            return null;
        }
    }

    /// <summary>An object in the package named by its alias, else wherever the index finds it.</summary>
    public async Task<LocatedObject?> FindObjectAsync(string name, string classFragment, string? package, CancellationToken cancellationToken)
    {
        if (package is not null && (await PackageFilesAsync(cancellationToken))?.GetValueOrDefault(package) is { } file &&
            InPackage(file, name, classFragment) is { } inPackage)
            return inPackage;
        return await LocateAsync(name, classFragment, null, cancellationToken);
    }

    /// <summary>
    /// An object by name and class: in the package that refers to it (its own export, or the package its import names),
    /// or, without one, wherever the index finds it.
    /// </summary>
    public async Task<LocatedObject?> LocateAsync(string name, string classFragment, string? nearPackage, CancellationToken cancellationToken)
    {
        if (nearPackage is not null)
        {
            try
            {
                if (InPackage(nearPackage, name, classFragment) is { } own) return own;
                var catalog = _catalogs.Read(nearPackage);
                var files = await PackageFilesAsync(cancellationToken);
                foreach (var import in catalog.Imports.Where(item => item.ObjectName.Resolve(catalog.Names).Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    var outer = import.OuterIndex;
                    string? alias = null;
                    while (outer < 0 && -outer <= catalog.Imports.Count)
                    {
                        var item = catalog.Imports[-outer - 1];
                        if (item.OuterIndex == 0) alias = item.ObjectName.Resolve(catalog.Names);
                        outer = item.OuterIndex;
                    }
                    if (alias is not null && files?.GetValueOrDefault(alias) is { } file && InPackage(file, name, classFragment) is { } found)
                        return found;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
            }
            return null;
        }
        return await _searches.GetOrAdd($"{classFragment}|{name}", _ => SearchAsync(name, classFragment, cancellationToken));
    }

    private LocatedObject? InPackage(string file, string name, string classFragment)
    {
        if (!File.Exists(file)) return null;
        var catalog = _catalogs.Read(file);
        var entry = catalog.Assets.FirstOrDefault(asset => asset.ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                                           asset.ClassName.Contains(classFragment, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : new LocatedObject(file, catalog, entry);
    }

    private async Task<LocatedObject?> SearchAsync(string name, string classFragment, CancellationToken cancellationToken)
    {
        if (libraryRoot() is not { Length: > 0 } root) return null;
        var search = await index.SearchAsync(root, name, null, metadata(), PackageIndexSearchScope.ObjectName, null, resultLimit: 20,
            cancellationToken: cancellationToken);
        var match = search.Matches.FirstOrDefault(item => item.ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                                          item.ClassName.Contains(classFragment, StringComparison.OrdinalIgnoreCase) &&
                                                          File.Exists(item.SourcePath));
        return match is null ? null : InPackage(match.SourcePath, name, classFragment);
    }

    private static string SafeFolderName(string value) => SafeName().Replace(value, "_");

    [GeneratedRegex(@"[^A-Za-z0-9_.-]")]
    private static partial Regex SafeName();
}
