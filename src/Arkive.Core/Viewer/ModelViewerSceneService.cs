using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.External;
using Arkive.Core.Inspection;
using Arkive.Core.Meshes;
using Arkive.Core.Packages;
using Arkive.Core.Viewer.Shaders;

namespace Arkive.Core.Viewer;

/// <summary>
/// Prepares everything Arkive's model viewer shows for one object: geometry for every
/// LOD from Arkive's native decoder (UModel's glTF when the native layout is not
/// proven), materials and textures from UModel's export, UModel's object properties,
/// and a colour recipe per material.
/// </summary>
public sealed class ModelViewerSceneService
{
    public const int CurrentSchemaVersion = 3;
    public const string UmodelFolder = "umodel";
    public const string ModelFile = "model.glb";
    public const string PropertiesFile = "properties.txt";
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly UmodelBridge _bridge;
    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly AssetCatalogReader _catalogReader;
    private readonly SkeletalMeshReconnaissanceService _skeletalReconnaissance;
    private readonly MaterialRecipeCatalog _recipes;
    private readonly ModelViewerCache _cache;
    private readonly UpkPackageReader? _reader;
    private readonly SemaphoreSlim _shaderIndexGate = new(1, 1);
    private ShaderCacheIndex? _shaderIndex;
    private string? _shaderIndexFailure;

    public ModelViewerSceneService(
        UmodelBridge bridge,
        ModelViewerCache cache,
        MaterialRecipeCatalog? recipes = null,
        UpkPackageReader? reader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _recipes = recipes ?? MaterialRecipeCatalog.LoadEmbedded();
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _reader = reader;
        _catalogReader = new AssetCatalogReader(reader, _protectionProvider);
        _skeletalReconnaissance = new SkeletalMeshReconnaissanceService(reader, _protectionProvider, umodelBridge: null);
    }

    public MaterialRecipeCatalog Recipes => _recipes;
    public ModelViewerCache Cache => _cache;

    public async Task<ModelViewerSceneResult> PrepareAsync(
        ModelViewerRequest request,
        IProgress<ModelViewerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = ModelViewerCache.Key(request.PackagePath, request.ExportIndex, request.ObjectName, CurrentSchemaVersion);
        var directory = _cache.DirectoryFor(key);
        var scenePath = Path.Combine(directory, "scene.json");
        if (_cache.IsReady(key))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<ModelViewerScene>(await File.ReadAllBytesAsync(scenePath, cancellationToken)
                    .ConfigureAwait(false), JsonOptions);
                if (cached?.SchemaVersion == CurrentSchemaVersion)
                {
                    _cache.Touch(key);
                    return new ModelViewerSceneResult(directory, scenePath, cached, FromCache: true);
                }
            }
            catch (JsonException) { }
        }

        var watch = Stopwatch.StartNew();
        var stages = new Dictionary<string, double>(StringComparer.Ordinal);
        var stageStart = TimeSpan.Zero;
        void Stage(string name)
        {
            stages[name] = Math.Round((watch.Elapsed - stageStart).TotalSeconds, 2);
            stageStart = watch.Elapsed;
        }
        directory = _cache.Prepare(key);
        var asset = new UmodelAssetEntry(request.ExportIndex, request.SerialOffset, request.SerialSize,
            request.ClassName, request.ObjectName);
        var diagnostics = new List<string>();

        progress?.Report(new("Exporting materials and textures", request.ObjectName));
        var exportTask = _bridge.ExportForViewerAsync(request.PackagePath, asset,
            Path.Combine(directory, UmodelFolder), request.PackageSearchRoot, cancellationToken);
        var dumpTask = _bridge.DumpObjectAsync(request.PackagePath, asset, cancellationToken);
        await Task.WhenAll(exportTask, dumpTask).ConfigureAwait(false);
        var export = await exportTask.ConfigureAwait(false);
        var dump = await dumpTask.ConfigureAwait(false);
        diagnostics.AddRange(export.Diagnostics.Where(IsUsefulDiagnostic).Distinct().Take(20));
        await File.WriteAllTextAsync(Path.Combine(directory, PropertiesFile), dump.RawOutput, cancellationToken)
            .ConfigureAwait(false);
        var properties = UmodelPropertyTree.Parse(dump.RawOutput);
        Stage("umodel");

        var lods = new List<ModelViewerLod>();
        var bones = new List<ModelViewerBone>();
        bool? vertexColors = null;
        string? modelUrl = null;
        string geometrySource = "none";
        var units = "metres";
        IReadOnlyList<string> slotNames = [];

        if (asset.Kind == AssetKind.SkeletalMesh)
        {
            progress?.Report(new("Decoding every LOD", request.ObjectName));
            try
            {
                var native = await BuildNativeSkeletalMeshAsync(request, dump, directory, cancellationToken)
                    .ConfigureAwait(false);
                lods.AddRange(native.Lods);
                bones.AddRange(native.Bones);
                vertexColors = native.HasVertexColors;
                slotNames = native.SlotNames;
                modelUrl = ModelFile;
                geometrySource = "arkive-native";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add($"Arkive's native decoder could not read this mesh ({exception.Message}); showing UModel's LOD 0 export.");
            }
        }

        if (modelUrl is null && asset.Kind is AssetKind.SkeletalMesh or AssetKind.StaticMesh)
        {
            var gltf = export.OutputFiles.FirstOrDefault(path => path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase));
            if (gltf is not null)
            {
                modelUrl = Relative(directory, gltf);
                geometrySource = "umodel-gltf";
                units = "unreal";
                (lods, slotNames) = OracleLods(dump.RawOutput, asset.Kind);
                diagnostics.Add("Only LOD 0 geometry is available for this object.");
            }
        }

        Stage("geometry");
        progress?.Report(new("Resolving materials", request.ObjectName));
        var exportRoot = Path.Combine(directory, UmodelFolder);
        var textures = IndexTextures(directory, exportRoot);
        var materials = MaterialInstanceDescriptionReader.ReadExportFolder(exportRoot);
        Stage("materials");
        var shaderIndex = await LoadShaderIndexAsync(request, progress, cancellationToken).ConfigureAwait(false);
        Stage("shader index");
        if (shaderIndex is null && _shaderIndexFailure is not null) diagnostics.Add(_shaderIndexFailure);
        // UModel reports which physical package each object it loaded came from.
        using var objects = new LoadedObjects(request, export.Diagnostics, _catalogReader, _protectionProvider);
        var srgbCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool TextureSrgb(string name)
        {
            if (srgbCache.TryGetValue(name, out var cached)) return cached;
            var read = objects.Read(name, AssetKind.Texture);
            var srgb = read is { } found
                ? TexturePropertyProbe.SamplesSrgb(TexturePropertyProbe.Read(found.Bytes, found.Names))
                : !name.EndsWith("_n", StringComparison.OrdinalIgnoreCase);
            return srgbCache[name] = srgb;
        }

        var viewerMaterials = new List<ModelViewerMaterial>();
        for (var slot = 0; slot < slotNames.Count; slot++)
        {
            var reference = slotNames[slot];
            var description = FindMaterial(materials, reference);
            if (description is null)
            {
                diagnostics.Add($"Material slot {slot} ({reference}) was not exported by UModel.");
                description = MaterialInstanceDescriptionReader.Resolve(reference,
                    new Dictionary<string, (string, UmodelPropertyNode)>());
            }
            var plan = _recipes.Plan(description, textures.ContainsKey);
            diagnostics.AddRange(plan.Warnings.Select(warning => $"{description.Name}: {warning}"));
            GameShaderPlan? gameShader = null;
            string? gameShaderFailure = null;
            if (shaderIndex is not null)
            {
                progress?.Report(new("Translating game shaders", description.Name));
                var shaderWatch = Stopwatch.StartNew();
                try
                {
                    gameShader = GameShaderMaterial.Build(shaderIndex, description,
                        name => objects.Read(name, AssetKind.Material)?.Bytes,
                        name => textures.TryGetValue(name, out var texture) ? texture.Url : null,
                        TextureSrgb, out gameShaderFailure);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException
                                                      or NotSupportedException or DllNotFoundException)
                {
                    gameShaderFailure = exception.Message;
                }
                if (Environment.GetEnvironmentVariable("ARKIVE_VIEWER_TRACE") == "1")
                    Console.Error.WriteLine($"[viewer] {description.Name}: {shaderWatch.Elapsed.TotalSeconds:N2}s");
            }
            viewerMaterials.Add(new ModelViewerMaterial(
                slot, description.Name, reference, description.ClassName, description.ParentChain,
                description.RootMaterial, description.BlendMode, description.TwoSided, description.Masked,
                description.OpacityMaskClipValue, description.Scalars, description.Vectors, description.Textures,
                description.Textures
                    .Where(pair => textures.ContainsKey(pair.Value))
                    .ToDictionary(pair => pair.Key, pair => textures[pair.Value].Url, StringComparer.OrdinalIgnoreCase),
                plan,
                _recipes.Plan(description, textures.ContainsKey, storedTextureOnly: true),
                gameShader,
                gameShaderFailure));
        }

        // The dump lists sockets twice: the object's own properties (with offsets) first,
        // then UModel's converted summary (name and bone only).
        Stage("shaders");
        var sockets = properties.Elements("Sockets")
            .Select(socket => new ModelViewerSocket(
                socket.ChildValue("SocketName") ?? socket.ChildValue("Name") ?? socket.Name,
                socket.ChildValue("BoneName") ?? socket.ChildValue("Bone") ?? "",
                Components(socket.Child("RelativeLocation"), "X", "Y", "Z"),
                Components(socket.Child("RelativeRotation"), "Pitch", "Yaw", "Roll"),
                Components(socket.Child("RelativeScale"), "X", "Y", "Z")))
            .ToArray();
        var scene = new ModelViewerScene(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            new ModelViewerObject(Path.GetFullPath(request.PackagePath), Path.GetFileName(request.PackagePath),
                request.PackageAlias, request.ClassName, request.ObjectName, request.ExportIndex, asset.Kind),
            geometrySource,
            modelUrl,
            units,
            lods,
            bones,
            sockets,
            vertexColors,
            viewerMaterials,
            textures.Values.OrderBy(texture => texture.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            PropertiesFile,
            diagnostics.Distinct().ToArray(),
            Math.Round(watch.Elapsed.TotalSeconds, 2),
            RecipeGlslCompiler.Prelude,
            stages);
        await File.WriteAllBytesAsync(Path.Combine(directory, "scene.json"),
            JsonSerializer.SerializeToUtf8Bytes(scene, JsonOptions), cancellationToken).ConfigureAwait(false);
        _cache.Trim(keepKey: key);
        return new ModelViewerSceneResult(directory, Path.Combine(directory, "scene.json"), scene, FromCache: false);
    }

    /// <summary>Loads the game shader index once; it is built on first use and reused while the game is unchanged.</summary>
    private async Task<ShaderCacheIndex?> LoadShaderIndexAsync(
        ModelViewerRequest request,
        IProgress<ModelViewerProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_shaderIndex is not null) return _shaderIndex;
        var packages = request.PackageSearchRoot ?? Path.GetDirectoryName(request.PackagePath);
        var releasePc = packages is null ? null : Directory.GetParent(Path.GetFullPath(packages))?.FullName;
        if (releasePc is null) return null;
        await _shaderIndexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_shaderIndex is not null) return _shaderIndex;
            var indexPath = Path.Combine(Path.GetDirectoryName(_cache.RootDirectory) ?? _cache.RootDirectory,
                "game-shader-index.json");
            var indexProgress = new Progress<string>(text => progress?.Report(new(text, "first time only")));
            var index = await Task.Run(() => ShaderCacheIndex.LoadOrBuild(
                releasePc, indexPath, _reader, _protectionProvider, indexProgress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            if (index.Document.Maps.Count == 0)
            {
                _shaderIndexFailure = "No game shader cache was found next to the Packages folder; showing colour recipes instead.";
                return null;
            }
            _shaderIndexFailure = null;
            return _shaderIndex = index;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
                                              or PackageFormatException or NotSupportedException)
        {
            _shaderIndexFailure = $"The game's shader cache could not be indexed ({exception.Message}); showing colour recipes instead.";
            return null;
        }
        finally
        {
            _shaderIndexGate.Release();
        }
    }

    /// <summary>
    /// Reads exports UModel loaded for the scene (materials and textures), using UModel's
    /// "Loading Class name from package file" lines to find their physical packages.
    /// </summary>
    private sealed class LoadedObjects : IDisposable
    {
        private readonly Dictionary<string, string> _packageByObject = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (AssetCatalog Catalog, LogicalPackageStream Stream)?> _packages =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly string _packageFolder;
        private readonly AssetCatalogReader _reader;
        private readonly IChunkProtectionProvider _provider;

        public LoadedObjects(
            ModelViewerRequest request,
            IEnumerable<string> umodelOutput,
            AssetCatalogReader reader,
            IChunkProtectionProvider provider)
        {
            _reader = reader;
            _provider = provider;
            _packageFolder = request.PackageSearchRoot ?? Path.GetDirectoryName(Path.GetFullPath(request.PackagePath))!;
            foreach (var line in umodelOutput)
            {
                const string from = " from package ";
                if (!line.StartsWith("Loading ", StringComparison.Ordinal)) continue;
                var at = line.IndexOf(from, StringComparison.Ordinal);
                if (at < 0) continue;
                var parts = line[8..at].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) continue;
                _packageByObject.TryAdd(parts[1], line[(at + from.Length)..].Trim());
            }
            _packageByObject.TryAdd("\0mesh", Path.GetFileName(request.PackagePath));
        }

        public (byte[] Bytes, IReadOnlyList<UnrealNameEntry> Names)? Read(string objectName, AssetKind kind)
        {
            var file = _packageByObject.GetValueOrDefault(objectName) ?? _packageByObject["\0mesh"];
            var package = Open(file);
            if (package is not { } opened) return null;
            var asset = opened.Catalog.Assets.FirstOrDefault(candidate =>
                candidate.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase) && candidate.Kind == kind);
            if (asset is null || asset.SerialSize is <= 0 or > 64 << 20) return null;
            var bytes = new byte[checked((int)asset.SerialSize)];
            opened.Stream.Position = asset.SerialOffset;
            opened.Stream.ReadExactly(bytes);
            return (bytes, opened.Catalog.Names);
        }

        private (AssetCatalog Catalog, LogicalPackageStream Stream)? Open(string file)
        {
            if (_packages.TryGetValue(file, out var cached)) return cached;
            (AssetCatalog, LogicalPackageStream)? opened = null;
            try
            {
                var path = Path.Combine(_packageFolder, file);
                if (File.Exists(path))
                {
                    var catalog = _reader.Read(path);
                    opened = (catalog, new LogicalPackageStream(catalog.Summary, _provider));
                }
            }
            catch (Exception exception) when (exception is IOException or PackageFormatException or InvalidDataException)
            {
            }
            return _packages[file] = opened;
        }

        public void Dispose()
        {
            foreach (var package in _packages.Values) package?.Stream.Dispose();
        }
    }

    private sealed record NativeSkeletalMesh(
        IReadOnlyList<ModelViewerLod> Lods,
        IReadOnlyList<ModelViewerBone> Bones,
        bool? HasVertexColors,
        IReadOnlyList<string> SlotNames);

    private async Task<NativeSkeletalMesh> BuildNativeSkeletalMeshAsync(
        ModelViewerRequest request,
        UmodelObjectDumpResult dump,
        string directory,
        CancellationToken cancellationToken)
    {
        // The viewer never persists this report as evidence, so it identifies the package
        // by path, size and time instead of hashing a possibly very large file.
        var file = new FileInfo(request.PackagePath);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")));
        var report = await _skeletalReconnaissance.InspectAsync(
            new SkeletalMeshReconnaissanceRequest(request.PackagePath, request.PackageAlias, request.ExportIndex,
                AssetKind.SkeletalMesh, request.ClassName, request.ObjectName, request.SerialOffset,
                request.SerialSize, identity) { SkipUmodelOracle = true },
            cancellationToken).ConfigureAwait(false);
        var layout = report.NativeLayout
            ?? throw new InvalidDataException(report.Issues.FirstOrDefault(issue => issue.Severity == "Error")?.Message
                ?? "the native layout was not proven");
        var oracle = SkeletalMeshReconnaissanceService.ParseSkeletalMeshDump(dump.RawOutput, dump.Duration, request.ClassName);
        report = report with { Oracle = oracle };

        var catalog = _catalogReader.Read(request.PackagePath);
        using var logical = new LogicalPackageStream(catalog.Summary, _protectionProvider);
        var bytes = new byte[checked((int)request.SerialSize)];
        logical.Position = request.SerialOffset;
        logical.ReadExactly(bytes);
        var decoded = new SkeletalMeshSemanticDecoder().Decode(bytes, layout);
        var glb = new ArkiveSkeletalMeshGlbWriter().Build(report, decoded);
        await File.WriteAllBytesAsync(Path.Combine(directory, ModelFile), glb.Bytes, cancellationToken)
            .ConfigureAwait(false);

        var slotNames = ReadGlbMaterialNames(glb.Bytes);
        var lods = decoded.Select(lod => new ModelViewerLod(
            lod.Evidence.LodIndex,
            lod.Evidence.VertexCount,
            lod.Indices.Length / 3,
            lod.Evidence.TextureCoordinateCount,
            lod.Evidence.Sections.Select(section => new ModelViewerSection(
                section.SectionIndex,
                section.MaterialIndex,
                section.MaterialIndex < slotNames.Count ? slotNames[section.MaterialIndex] : null,
                section.FirstIndex,
                section.TriangleCount)).ToArray())).ToArray();
        var bones = layout.Bones.Select(bone => new ModelViewerBone(bone.BoneIndex, bone.Name, bone.ParentIndex)).ToArray();
        return new NativeSkeletalMesh(lods, bones, decoded.Any(lod => lod.Colors is not null), slotNames);
    }

    private static (List<ModelViewerLod> Lods, IReadOnlyList<string> SlotNames) OracleLods(string dump, AssetKind kind)
    {
        IReadOnlyList<SkeletalMeshLodEvidence> evidence = kind == AssetKind.SkeletalMesh
            ? SkeletalMeshReconnaissanceService.ParseSkeletalMeshDump(dump, TimeSpan.Zero).Lods
            : Arkive.Core.StaticMeshes.StaticMeshReconnaissanceService.ParseDump(dump, TimeSpan.Zero).Lods;
        var slots = evidence.SelectMany(lod => lod.Sections).Select(section => section.Material)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lods = evidence.Select(lod => new ModelViewerLod(
            lod.LodIndex, lod.VertexCount, lod.TriangleCount, lod.TextureCoordinateCount,
            lod.Sections.Select(section => new ModelViewerSection(section.SectionIndex,
                slots.FindIndex(name => name.Equals(section.Material, StringComparison.OrdinalIgnoreCase)),
                section.Material, section.FirstIndex, section.TriangleCount)).ToArray())).ToList();
        return (lods, slots);
    }

    private static float[]? Components(UmodelPropertyNode? node, params string[] names)
    {
        if (node is null) return null;
        var values = new float[names.Length];
        for (var index = 0; index < names.Length; index++)
            if (!UmodelPropertyTree.TryParseFloat(node.ChildValue(names[index]), out values[index])) return null;
        return values;
    }

    private static MaterialInstanceDescription? FindMaterial(
        IReadOnlyDictionary<string, MaterialInstanceDescription> materials,
        string reference)
    {
        if (MaterialInstanceDescriptionReader.KeyForReference(reference) is { } key &&
            materials.TryGetValue(key, out var exact)) return exact;
        var name = UmodelPropertyTree.ParseObjectReference(reference)?.Name ?? reference;
        return materials.Values
            .Where(material => material.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(material => material.ClassName.Contains("Instance", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
    }

    private static Dictionary<string, ModelViewerTexture> IndexTextures(string sceneDirectory, string exportRoot)
    {
        var result = new Dictionary<string, ModelViewerTexture>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(exportRoot)) return result;
        foreach (var path in Directory.EnumerateFiles(exportRoot, "*.png", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (result.ContainsKey(name)) continue;
            var (width, height, format) = ReadPngHeader(path);
            result[name] = new ModelViewerTexture(name, Relative(sceneDirectory, path), width, height, format,
                new FileInfo(path).Length);
        }
        return result;
    }

    private static (int Width, int Height, string Format) ReadPngHeader(string path)
    {
        Span<byte> header = stackalloc byte[26];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) < 26) return (0, 0, "unknown");
        var width = BinaryPrimitives.ReadInt32BigEndian(header[16..]);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[20..]);
        var format = header[25] switch
        {
            0 => "grey",
            2 => "RGB",
            3 => "palette",
            4 => "grey+alpha",
            6 => "RGBA",
            _ => "unknown",
        };
        return (width, height, format);
    }

    private static IReadOnlyList<string> ReadGlbMaterialNames(byte[] glb)
    {
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12)));
        using var document = JsonDocument.Parse(glb.AsMemory(20, jsonLength));
        return document.RootElement.TryGetProperty("materials", out var materials)
            ? materials.EnumerateArray().Select(material => material.GetProperty("name").GetString() ?? "").ToArray()
            : [];
    }

    // UModel reports progress and harmless unknown-property notes on the same stream as real problems.
    private static bool IsUsefulDiagnostic(string line) =>
        (line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("WARNING", StringComparison.Ordinal) ||
         line.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
         line.Contains("can't", StringComparison.OrdinalIgnoreCase)) &&
        !line.StartsWith("WARNING: Unknown class", StringComparison.OrdinalIgnoreCase);

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
