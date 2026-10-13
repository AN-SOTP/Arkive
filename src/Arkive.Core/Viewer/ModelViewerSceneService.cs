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
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;
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
    public const int CurrentSchemaVersion = 9;
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
    private readonly StaticMeshReconnaissanceService _staticReconnaissance;
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
        _staticReconnaissance = new StaticMeshReconnaissanceService(reader, _protectionProvider, umodelBridge: null);
    }

    public MaterialRecipeCatalog Recipes => _recipes;
    public ModelViewerCache Cache => _cache;

    public Task<ModelViewerSceneResult> PrepareAsync(
        ModelViewerRequest request,
        IProgress<ModelViewerProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(request, edits: null, progress, cancellationToken);

    /// <summary>
    /// Prepares the object as the game currently has it (rebuilt whenever its package or any package
    /// its materials and textures came from changes) and, when <paramref name="edits"/> are given, shows a
    /// modder's working files on top of it.
    /// </summary>
    public async Task<ModelViewerSceneResult> PrepareAsync(
        ModelViewerRequest request,
        ModelViewerEdits? edits,
        IProgress<ModelViewerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var original = await PrepareOriginalAsync(request, progress, cancellationToken).ConfigureAwait(false);
        if (edits is null || edits.IsEmpty) return original;
        progress?.Report(new("Applying your edits", edits.Label));
        return await ApplyEditsAsync(request, original, edits, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModelViewerSceneResult> PrepareOriginalAsync(
        ModelViewerRequest request,
        IProgress<ModelViewerProgress>? progress,
        CancellationToken cancellationToken)
    {
        var key = ModelViewerCache.Key(request.PackagePath, request.ExportIndex, request.ObjectName, CurrentSchemaVersion);
        var directory = _cache.DirectoryFor(key);
        var scenePath = Path.Combine(directory, "scene.json");
        if (_cache.IsReady(key))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<ModelViewerScene>(await File.ReadAllBytesAsync(scenePath, cancellationToken)
                    .ConfigureAwait(false), JsonOptions);
                // Linked materials and textures live in other packages; a change to any of them (a mod being
                // applied or restored) invalidates the scene just like a change to the object's own package.
                if (cached?.SchemaVersion == CurrentSchemaVersion && cached.Packages?.All(stamp => stamp.Unchanged) == true)
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
        // UModel cannot read every object; Arkive's own decoder may still show its geometry.
        var exportTask = Tolerate(_bridge.ExportForViewerAsync(request.PackagePath, asset,
            Path.Combine(directory, UmodelFolder), request.PackageSearchRoot, cancellationToken));
        var dumpTask = Tolerate(_bridge.DumpObjectAsync(request.PackagePath, asset, cancellationToken));
        await Task.WhenAll(exportTask, dumpTask).ConfigureAwait(false);
        var (exported, exportFailure) = await exportTask.ConfigureAwait(false);
        var (dumped, dumpFailure) = await dumpTask.ConfigureAwait(false);
        var export = exported ?? new UmodelExportResult(Path.GetFullPath(request.PackagePath), asset,
            Path.Combine(directory, UmodelFolder), [], [], TimeSpan.Zero, false);
        var dump = dumped ?? new UmodelObjectDumpResult(Path.GetFullPath(request.PackagePath), asset, string.Empty, [], TimeSpan.Zero);
        var umodelFailed = (exportFailure ?? dumpFailure) is not null;
        if (umodelFailed)
            diagnostics.Add("UModel cannot read this object, so its materials, textures and properties are unavailable. " +
                            "The geometry comes from Arkive's own decoder.");
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

        if (asset.Kind == AssetKind.StaticMesh)
        {
            progress?.Report(new("Decoding every LOD", request.ObjectName));
            try
            {
                var native = await BuildNativeStaticMeshAsync(request, dump, directory, cancellationToken)
                    .ConfigureAwait(false);
                lods.AddRange(native.Lods);
                slotNames = native.SlotNames;
                vertexColors = native.HasVertexColors;
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
        var objectPackages = LoadedObjects.Parse(request, export.Diagnostics);
        using var objects = new LoadedObjects(request, objectPackages, _catalogReader, _protectionProvider);
        var viewerMaterials = new List<ModelViewerMaterial>();
        for (var slot = 0; slot < slotNames.Count; slot++)
        {
            var reference = slotNames[slot];
            var description = FindMaterial(materials, reference);
            if (description is null)
            {
                if (!umodelFailed) diagnostics.Add($"Material slot {slot} ({reference}) was not exported by UModel.");
                description = MaterialInstanceDescriptionReader.Resolve(reference,
                    new Dictionary<string, (string, UmodelPropertyNode)>());
            }
            viewerMaterials.Add(BuildMaterial(slot, reference, description, textures, shaderIndex, objects, diagnostics, progress));
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
            stages,
            objectPackages,
            objects.PackageFiles.Prepend(Path.GetFullPath(request.PackagePath))
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(ModelViewerPackageStamp.Of).ToArray());
        await File.WriteAllBytesAsync(Path.Combine(directory, "scene.json"),
            JsonSerializer.SerializeToUtf8Bytes(scene, JsonOptions), cancellationToken).ConfigureAwait(false);
        _cache.Trim(keepKey: key);
        return new ModelViewerSceneResult(directory, Path.Combine(directory, "scene.json"), scene, FromCache: false);
    }

    private static async Task<(T? Result, string? Failure)> Tolerate<T>(Task<T> task) where T : class
    {
        try { return (await task.ConfigureAwait(false), null); }
        catch (UmodelBridgeException exception) { return (null, exception.Message); }
    }

    private ModelViewerMaterial BuildMaterial(
        int slot,
        string reference,
        MaterialInstanceDescription description,
        IReadOnlyDictionary<string, ModelViewerTexture> textures,
        ShaderCacheIndex? shaderIndex,
        LoadedObjects objects,
        List<string> diagnostics,
        IProgress<ModelViewerProgress>? progress)
    {
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
                    objects.TextureSrgb, out gameShaderFailure);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException
                                                  or NotSupportedException or DllNotFoundException)
            {
                gameShaderFailure = exception.Message;
            }
            if (Environment.GetEnvironmentVariable("ARKIVE_VIEWER_TRACE") == "1")
                Console.Error.WriteLine($"[viewer] {description.Name}: {shaderWatch.Elapsed.TotalSeconds:N2}s");
        }
        return new ModelViewerMaterial(
            slot, description.Name, reference, description.ClassName, description.ParentChain,
            description.RootMaterial, description.BlendMode, description.TwoSided, description.Masked,
            description.OpacityMaskClipValue, description.Scalars, description.Vectors, description.Textures,
            description.Textures
                .Where(pair => textures.ContainsKey(pair.Value))
                .ToDictionary(pair => pair.Key, pair => textures[pair.Value].Url, StringComparer.OrdinalIgnoreCase),
            plan,
            _recipes.Plan(description, textures.ContainsKey, storedTextureOnly: true),
            gameShader,
            gameShaderFailure);
    }

    /// <summary>
    /// Shows a modder's working files on top of a prepared scene: the edited GLB replaces the mesh, edited
    /// images replace the textures they belong to, and materials with changed values are rebuilt. Each call
    /// writes a fresh folder (so the page never shows a stale cached image) and removes older ones.
    /// </summary>
    private async Task<ModelViewerSceneResult> ApplyEditsAsync(
        ModelViewerRequest request,
        ModelViewerSceneResult original,
        ModelViewerEdits edits,
        IProgress<ModelViewerProgress>? progress,
        CancellationToken cancellationToken)
    {
        var scene = original.Scene;
        var editsRoot = Path.Combine(original.Directory, "edits");
        var stamp = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..26];
        var folder = Path.Combine(editsRoot, stamp);
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateDirectories(editsRoot).Where(path => !path.Equals(folder, StringComparison.OrdinalIgnoreCase)))
        {
            try { Directory.Delete(old, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        var applied = new List<string>();
        var problems = new List<string>();
        var diagnostics = scene.Diagnostics.ToList();

        // Textures: the edited image takes the place of the exported original.
        var textures = scene.Textures.ToDictionary(texture => texture.Name, StringComparer.OrdinalIgnoreCase);
        var urlChanges = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, image) in edits.TextureImages.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!textures.TryGetValue(name, out var originalTexture)) continue;
            try
            {
                var target = Path.Combine(folder, SafeFileName(name) + ".png");
                var (width, height, format) = await WriteEditedImageAsync(image, target, cancellationToken).ConfigureAwait(false);
                var editedTexture = new ModelViewerTexture(name, Relative(original.Directory, target), width, height,
                    $"{format} (edited)", new FileInfo(target).Length);
                textures[name] = editedTexture;
                urlChanges[originalTexture.Url] = editedTexture.Url;
                applied.Add($"Texture {name}: your edited image");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException
                                                  or UnauthorizedAccessException or FormatException)
            {
                problems.Add($"Texture {name}: {exception.Message}");
            }
        }

        // Materials: rebuilt when their values changed, otherwise pointed at the edited textures.
        var materials = scene.Materials.ToList();
        var changed = materials.Where(material => edits.Materials.ContainsKey(material.Name)).ToArray();
        var changedSlots = changed.Select(material => material.Slot).ToHashSet();
        if (changed.Length > 0)
        {
            var descriptions = MaterialInstanceDescriptionReader.ReadExportFolder(Path.Combine(original.Directory, UmodelFolder));
            var shaderIndex = await LoadShaderIndexAsync(request, progress, cancellationToken).ConfigureAwait(false);
            using var objects = new LoadedObjects(request,
                scene.ObjectPackages ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                _catalogReader, _protectionProvider);
            foreach (var material in changed)
            {
                var edit = edits.Materials[material.Name];
                var description = FindMaterial(descriptions, material.Reference ?? material.Name)
                    ?? MaterialInstanceDescriptionReader.Resolve(material.Reference ?? material.Name,
                        new Dictionary<string, (string, UmodelPropertyNode)>());
                var textureParameters = new Dictionary<string, string>(description.Textures, StringComparer.OrdinalIgnoreCase);
                foreach (var (parameter, texture) in edit.Textures)
                {
                    if (textures.ContainsKey(texture)) textureParameters[parameter] = texture;
                    else problems.Add($"{material.Name}: {parameter} now uses {texture}, which is not loaded with this object; showing the original texture.");
                }
                description = description with
                {
                    Scalars = Merge(description.Scalars, edit.Scalars),
                    Vectors = Merge(description.Vectors, edit.Vectors),
                    Textures = textureParameters,
                };
                materials[material.Slot] = BuildMaterial(material.Slot, material.Reference ?? material.Name, description,
                    textures, shaderIndex, objects, diagnostics, progress);
                var count = edit.Scalars.Count + edit.Vectors.Count + edit.Textures.Count;
                applied.Add($"Material {material.Name}: {count} changed value(s)");
            }
        }
        for (var index = 0; index < materials.Count; index++)
        {
            if (changedSlots.Contains(index) || urlChanges.Count == 0) continue;
            var material = materials[index];
            materials[index] = material with
            {
                TextureUrls = material.TextureUrls.ToDictionary(pair => pair.Key,
                    pair => urlChanges.GetValueOrDefault(pair.Value, pair.Value), StringComparer.OrdinalIgnoreCase),
                GameShader = material.GameShader is { } shader
                    ? shader with
                    {
                        Samplers = shader.Samplers.ToDictionary(pair => pair.Key,
                            pair => urlChanges.GetValueOrDefault(pair.Value, pair.Value), StringComparer.Ordinal),
                    }
                    : null,
            };
        }

        // Mesh: the edited GLB, with LOD counts read from it.
        var modelUrl = scene.ModelUrl;
        var geometrySource = scene.GeometrySource;
        var units = scene.GeometryUnits;
        var lods = scene.Lods;
        if (edits.MeshGlbPath is { } glbPath)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(glbPath, cancellationToken).ConfigureAwait(false);
                var target = Path.Combine(folder, ModelFile);
                await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
                modelUrl = Relative(original.Directory, target);
                geometrySource = "edited";
                units = "metres";
                lods = EditedLods(bytes, scene.Lods);
                applied.Add($"Mesh: your edited {Path.GetFileName(glbPath)}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                                  or ArgumentOutOfRangeException or InvalidOperationException)
            {
                problems.Add($"Mesh: {exception.Message}");
            }
        }

        var edited = scene with
        {
            ModelUrl = modelUrl,
            GeometrySource = geometrySource,
            GeometryUnits = units,
            Lods = lods,
            Materials = materials,
            Textures = textures.Values.OrderBy(texture => texture.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            Diagnostics = diagnostics.Distinct().ToArray(),
            Edits = new ModelViewerEditSummary(edits.Label, applied, problems),
        };
        var scenePath = Path.Combine(folder, "scene.json");
        await File.WriteAllBytesAsync(scenePath, JsonSerializer.SerializeToUtf8Bytes(edited, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        return new ModelViewerSceneResult(original.Directory, scenePath, edited, original.FromCache);
    }

    private static IReadOnlyDictionary<string, T> Merge<T>(IReadOnlyDictionary<string, T> values, IReadOnlyDictionary<string, T> changes)
    {
        var merged = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values) merged[name] = value;
        foreach (var (name, value) in changes) merged[name] = value;
        return merged;
    }

    private static async Task<(int Width, int Height, string Format)> WriteEditedImageAsync(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        if (Path.GetExtension(source).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            await using (var output = File.Create(target))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            return ReadPngHeader(target);
        }
        var loaded = await TextureEditableImageService.LoadAsync(source, cancellationToken).ConfigureAwait(false);
        await PngRgbaCodec.WriteAsync(target, loaded.Image, cancellationToken).ConfigureAwait(false);
        return (loaded.Image.Width, loaded.Image.Height, loaded.SourceFormat);
    }

    /// <summary>
    /// Vertex and triangle counts per LOD of an edited GLB. Meshes are grouped by the <c>arkiveLod</c> extra
    /// Arkive writes or, after an editor dropped extras, the <c>_LOD#</c> suffix of their names.
    /// </summary>
    private static IReadOnlyList<ModelViewerLod> EditedLods(byte[] glb, IReadOnlyList<ModelViewerLod> original)
    {
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12)));
        using var document = JsonDocument.Parse(glb.AsMemory(20, jsonLength));
        var root = document.RootElement;
        if (!root.TryGetProperty("meshes", out var meshes) || !root.TryGetProperty("accessors", out var accessors)) return original;
        var counts = new SortedDictionary<int, (int Vertices, int Triangles)>();
        var index = 0;
        foreach (var mesh in meshes.EnumerateArray())
        {
            var lod = LodOf(mesh, index++);
            var (vertices, triangles) = counts.GetValueOrDefault(lod);
            var seen = new HashSet<int>();
            foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                var position = primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
                if (seen.Add(position)) vertices += accessors[position].GetProperty("count").GetInt32();
                triangles += primitive.TryGetProperty("indices", out var indices)
                    ? accessors[indices.GetInt32()].GetProperty("count").GetInt32() / 3
                    : accessors[position].GetProperty("count").GetInt32() / 3;
            }
            counts[lod] = (vertices, triangles);
        }
        return counts.Select(pair =>
        {
            var source = original.FirstOrDefault(item => item.Index == pair.Key);
            return new ModelViewerLod(pair.Key, pair.Value.Vertices, pair.Value.Triangles, source?.UvSets ?? 1,
                source?.Sections ?? []);
        }).ToArray();
    }

    private static int LodOf(JsonElement mesh, int index)
    {
        if (mesh.TryGetProperty("extras", out var extras) && extras.ValueKind == JsonValueKind.Object &&
            extras.TryGetProperty("arkiveLod", out var lod) && lod.TryGetInt32(out var value)) return value;
        var name = mesh.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
        var match = System.Text.RegularExpressions.Regex.Match(name, @"_LOD(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    private static string SafeFileName(string name) =>
        new(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());

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
        private const string MeshKey = "\0mesh";
        private readonly IReadOnlyDictionary<string, string> _packageByObject;
        private readonly Dictionary<string, (AssetCatalog Catalog, LogicalPackageStream Stream)?> _packages =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _srgb = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _packageFolder;
        private readonly AssetCatalogReader _reader;
        private readonly IChunkProtectionProvider _provider;

        public LoadedObjects(
            ModelViewerRequest request,
            IReadOnlyDictionary<string, string> packageByObject,
            AssetCatalogReader reader,
            IChunkProtectionProvider provider)
        {
            _reader = reader;
            _provider = provider;
            _packageFolder = request.PackageSearchRoot ?? Path.GetDirectoryName(Path.GetFullPath(request.PackagePath))!;
            var map = new Dictionary<string, string>(packageByObject, StringComparer.OrdinalIgnoreCase);
            map.TryAdd(MeshKey, Path.GetFileName(request.PackagePath));
            _packageByObject = map;
        }

        /// <summary>Object name to package file, from UModel's "Loading Class name from package file" lines.</summary>
        public static Dictionary<string, string> Parse(ModelViewerRequest request, IEnumerable<string> umodelOutput)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in umodelOutput)
            {
                const string from = " from package ";
                if (!line.StartsWith("Loading ", StringComparison.Ordinal)) continue;
                var at = line.IndexOf(from, StringComparison.Ordinal);
                if (at < 0) continue;
                var parts = line[8..at].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) continue;
                result.TryAdd(parts[1], line[(at + from.Length)..].Trim());
            }
            return result;
        }

        /// <summary>Every existing package file the scene's objects came from.</summary>
        public IEnumerable<string> PackageFiles => _packageByObject.Values.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(file => Path.GetFullPath(Path.Combine(_packageFolder, file))).Where(File.Exists);

        public bool TextureSrgb(string name)
        {
            if (_srgb.TryGetValue(name, out var cached)) return cached;
            var srgb = Read(name, AssetKind.Texture) is { } found
                ? TexturePropertyProbe.SamplesSrgb(TexturePropertyProbe.Read(found.Bytes, found.Names))
                : !name.EndsWith("_n", StringComparison.OrdinalIgnoreCase);
            return _srgb[name] = srgb;
        }

        public (byte[] Bytes, IReadOnlyList<UnrealNameEntry> Names)? Read(string objectName, AssetKind kind)
        {
            var file = _packageByObject.GetValueOrDefault(objectName) ?? _packageByObject[MeshKey];
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

    private async Task<NativeSkeletalMesh> BuildNativeStaticMeshAsync(
        ModelViewerRequest request,
        UmodelObjectDumpResult dump,
        string directory,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(request.PackagePath);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")));
        var report = await _staticReconnaissance.InspectAsync(
            new StaticMeshReconnaissanceRequest(request.PackagePath, request.PackageAlias, request.ExportIndex,
                AssetKind.StaticMesh, request.ClassName, request.ObjectName, request.SerialOffset,
                request.SerialSize, identity) { SkipUmodelOracle = true },
            cancellationToken).ConfigureAwait(false);
        var layout = report.NativeLayout
            ?? throw new InvalidDataException(report.Issues.FirstOrDefault(issue => issue.Severity == "Error")?.Message
                ?? "the native layout was not proven");
        report = report with { Oracle = StaticMeshReconnaissanceService.ParseDump(dump.RawOutput, dump.Duration, request.ClassName) };

        var catalog = _catalogReader.Read(request.PackagePath);
        using var logical = new LogicalPackageStream(catalog.Summary, _protectionProvider);
        var bytes = new byte[checked((int)request.SerialSize)];
        logical.Position = request.SerialOffset;
        logical.ReadExactly(bytes);
        var decoded = new StaticMeshSemanticDecoder().Decode(bytes, layout);
        var glb = new ArkiveStaticMeshGlbWriter().Build(report, decoded);
        await File.WriteAllBytesAsync(Path.Combine(directory, ModelFile), glb.Bytes, cancellationToken)
            .ConfigureAwait(false);

        // The static GLB has one material per LOD section; the viewer works with distinct materials.
        var materialNames = ReadGlbMaterialNames(glb.Bytes);
        var slots = materialNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lods = decoded.Select(lod => new ModelViewerLod(
            lod.Evidence.LodIndex,
            lod.Evidence.VertexCount,
            lod.Indices.Length / 3,
            lod.Evidence.TextureCoordinateCount,
            lod.Evidence.Sections.Select(section =>
            {
                var name = report.Oracle?.Lods.SingleOrDefault(item => item.LodIndex == lod.Evidence.LodIndex)?
                    .Sections.SingleOrDefault(item => item.SectionIndex == section.SectionIndex)?.Material;
                var slot = name is null ? -1 : slots.FindIndex(candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase));
                return new ModelViewerSection(section.SectionIndex, slot, name, section.FirstIndex, section.TriangleCount);
            }).ToArray())).ToArray();
        return new NativeSkeletalMesh(lods, [], decoded.Any(lod => lod.Colours is not null), slots);
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
