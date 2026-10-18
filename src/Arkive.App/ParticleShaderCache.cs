using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Materials;
using Arkive.Core.Packages;
using Arkive.Core.Particles;
using Arkive.Core.Viewer;
using Arkive.Core.Viewer.Shaders;

namespace Arkive.App;

/// <summary>
/// Effect materials' own compiled shaders (see <see cref="ParticleShaderPlanner"/>), built once from the game's shader
/// cache and kept next to the material's exported textures (shader2.json), for the particle window and skill preview.
/// One instance serves one batch of materials, opening each package it reads once.
/// </summary>
public sealed class ParticleShaderCache(UpkPackageReader packageReader, IChunkProtectionProvider provider) : IDisposable
{
    private const int MaximumParents = 8;

    /// <summary>Finds a material or texture object by name and class ("material", "texture") in the game packages.</summary>
    public delegate Task<PackageIndexMatch?> FindObject(string name, string classFragment, CancellationToken cancellationToken);

    private readonly AssetCatalogReader _catalogs = new(packageReader, provider);
    private readonly MaterialInstanceReconnaissanceService _materials = new(packageReader, provider);
    private readonly Dictionary<string, (AssetCatalog Catalog, LogicalPackageStream Stream)?> _packages = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="folder">The material's folder: UModel's export of it under "export".</param>
    /// <param name="root">The folder the page's texture references are relative to.</param>
    public async Task<ParticleShaderSet?> GetAsync(ShaderCacheIndex index, string material, string folder, string root,
        FindObject findObject, Action<string> log, CancellationToken cancellationToken)
    {
        var record = Path.Combine(folder, "shader2.json");
        try
        {
            if (File.Exists(record))
                return JsonSerializer.Deserialize<Record>(await File.ReadAllTextAsync(record, cancellationToken))?.Shader;
            var exportRoot = Path.Combine(folder, "export");
            var exported = Directory.Exists(exportRoot)
                ? MaterialInstanceDescriptionReader.ReadExportFolder(exportRoot).Values.ToArray()
                : [];
            // UModel exports nothing for materials without textures (glows and rings drawn by their shader alone);
            // those are read from the game package instead.
            var description = exported.FirstOrDefault(item => item.Name.Equals(material, StringComparison.OrdinalIgnoreCase))
                              ?? exported.FirstOrDefault(item => !exported.Any(other =>
                                  other.ParentChain.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
                              ?? await DescribeFromPackageAsync(material, findObject, cancellationToken);
            Directory.CreateDirectory(folder);
            if (description is null)
            {
                log($"Particle material shader not used: material={material}, reason=the material was not found in the game packages.");
                await File.WriteAllTextAsync(record, JsonSerializer.Serialize(new Record(null, "not found")), cancellationToken);
                return null;
            }
            var pngs = Directory.Exists(exportRoot)
                ? Directory.EnumerateFiles(exportRoot, "*.png", SearchOption.AllDirectories)
                    .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
                : [];
            // The planner reads material and texture exports synchronously; they are found in the game packages first.
            var bytes = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in description.ParentChain.Prepend(description.Name).Distinct(StringComparer.OrdinalIgnoreCase))
                bytes[name] = await findObject(name, "material", cancellationToken) is { } match ? Read(match)?.Bytes : null;
            var srgb = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in pngs.Keys)
                srgb[name] = await findObject(name, "texture", cancellationToken) is { } match && Read(match) is { } found
                    ? TexturePropertyProbe.SamplesSrgb(TexturePropertyProbe.Read(found.Bytes, found.Catalog.Names))
                    : !name.EndsWith("_n", StringComparison.OrdinalIgnoreCase);
            string? failure = null;
            var shader = await Task.Run(() => ParticleShaderPlanner.Build(index, description,
                name => bytes.GetValueOrDefault(name),
                name => pngs.TryGetValue(name, out var png) ? Path.GetRelativePath(root, png) : null,
                name => srgb.GetValueOrDefault(name, true), out failure), cancellationToken);
            if (shader is null) log($"Particle material shader not used: material={material}, reason={failure}");
            await File.WriteAllTextAsync(record, JsonSerializer.Serialize(new Record(shader, failure)), cancellationToken);
            return shader;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                              InvalidDataException or FormatException or ArgumentException)
        {
            log($"Particle material shader not built: material={material}, error={exception.Message}");
            return null;
        }
    }

    // A material instance and its parents as the package holds them: their scalar and colour parameters (the instance's
    // own win), and the base material's blend mode and sidedness.
    private async Task<MaterialInstanceDescription?> DescribeFromPackageAsync(string material, FindObject findObject,
        CancellationToken cancellationToken)
    {
        var chain = new List<string>();
        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var vectors = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        var blendMode = "BLEND_Opaque";
        var twoSided = false;
        for (string? name = material; name is not null && chain.Count < MaximumParents && !chain.Contains(name, StringComparer.OrdinalIgnoreCase);)
        {
            if (await findObject(name, "material", cancellationToken) is not { } match) break;
            chain.Add(name);
            var report = await _materials.InspectAsync(new MaterialInstanceReconnaissanceRequest(match.SourcePath, match.PackageAlias,
                match.ExportIndex, match.Kind, match.ClassName, match.ObjectName, match.SerialOffset, match.SerialSize,
                KnownPackageSha256: new string('0', 64)), cancellationToken);
            foreach (var scalar in report.ScalarParameters) scalars.TryAdd(scalar.Name, scalar.Value);
            foreach (var vector in report.VectorParameters) vectors.TryAdd(vector.Name, [vector.Red, vector.Green, vector.Blue, vector.Alpha]);
            if (Read(match) is { } export && BlendModeOf(export.Bytes, export.Catalog) is { } blend) blendMode = blend;
            if (report.Properties.Any(property => property.Name.Equals("TwoSided", StringComparison.OrdinalIgnoreCase) &&
                                                  property.ValuePreview.Equals("True", StringComparison.OrdinalIgnoreCase)))
                twoSided = true;
            name = report.ParentObject is { } parent && parent != "None" ? ObjectName(parent) : null;
        }
        if (chain.Count == 0) return null;
        // Lost Ark names effect materials by blend mode (_Ad additive, _Tr translucent) when the base sets none it reads.
        if (blendMode == "BLEND_Opaque")
            blendMode = chain.Any(name => name.EndsWith("_ad", StringComparison.OrdinalIgnoreCase)) ? "BLEND_Additive"
                : chain.Any(name => name.EndsWith("_tr", StringComparison.OrdinalIgnoreCase)) ? "BLEND_Translucent" : blendMode;
        return new MaterialInstanceDescription(material, "MaterialInstanceConstant", chain.Skip(1).ToArray(), chain[^1],
            scalars, vectors, new Dictionary<string, string>(), new Dictionary<string, bool>(), twoSided, false, blendMode, 0.3333f);
    }

    // A base material's BlendMode tag: its name, "ByteProperty", size and index, the enum's name, then the value's name
    // (BLEND_Additive...).
    private static string? BlendModeOf(byte[] bytes, AssetCatalog catalog)
    {
        var names = catalog.Names;
        var tag = names.FirstOrDefault(name => name.Value.Equals("BlendMode", StringComparison.OrdinalIgnoreCase))?.Index;
        if (tag is null) return null;
        for (var at = 0; at + 40 <= bytes.Length; at++)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at)) != tag || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4)) != 0)
                continue;
            foreach (var offset in (int[])[32, 24])
            {
                var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + offset));
                if (value >= 0 && value < names.Count && names[value].Value.StartsWith("BLEND_", StringComparison.OrdinalIgnoreCase))
                    return names[value].Value;
            }
        }
        return null;
    }

    // Material3'Pkg.Group.Name' -> Name
    private static string ObjectName(string reference)
    {
        var start = reference.IndexOf('\'');
        var path = start >= 0 ? reference[(start + 1)..].TrimEnd('\'') : reference;
        return path[(path.LastIndexOf('.') + 1)..];
    }

    /// <summary>A texture's export and its package's catalog, for reading its sampling settings.</summary>
    public (byte[] Bytes, AssetCatalog Catalog)? Read(PackageIndexMatch match)
    {
        if (match.SerialSize is <= 0 or > 64 << 20) return null;
        if (!_packages.TryGetValue(match.SourcePath, out var package))
        {
            try
            {
                var catalog = _catalogs.Read(match.SourcePath);
                package = (catalog, new LogicalPackageStream(catalog.Summary, provider));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                package = null;
            }
            _packages[match.SourcePath] = package;
        }
        if (package is not { } opened) return null;
        var bytes = new byte[match.SerialSize];
        opened.Stream.Position = match.SerialOffset;
        opened.Stream.ReadExactly(bytes);
        return (bytes, opened.Catalog);
    }

    public void Dispose()
    {
        foreach (var package in _packages.Values) package?.Stream.Dispose();
    }

    private sealed record Record(ParticleShaderSet? Shader, string? Failure);
}
