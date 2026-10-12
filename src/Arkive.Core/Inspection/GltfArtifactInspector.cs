using System.Buffers.Binary;
using System.Text.Json;

namespace Arkive.Core.Inspection;

internal sealed class GltfArtifactInspector
{
    private const uint GlbMagic = 0x46546C67;
    private const uint GlbJsonChunk = 0x4E4F534A;
    private const uint GlbBinChunk = 0x004E4942;
    private const int MaximumJsonBytes = 64 * 1024 * 1024;

    public async Task<ArtifactInspectionResult> InspectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        var format = file.Extension.Equals(".glb", StringComparison.OrdinalIgnoreCase)
            ? "GLB"
            : "glTF";
        var properties = new List<ArtifactInspectionProperty>
        {
            new("File", "Name", file.Name),
            new("File", "Size", $"{file.Length:N0} bytes"),
            new("File", "Container", format),
        };
        var issues = new List<ArtifactInspectionIssue>();
        var dependencies = new List<ArtifactInspectionDependency>();

        byte[] jsonBytes;
        long? glbBinBytes = null;
        if (format == "GLB")
        {
            (jsonBytes, glbBinBytes) = await ReadGlbAsync(
                path,
                file.Length,
                properties,
                issues,
                cancellationToken).ConfigureAwait(false);
            if (jsonBytes.Length == 0)
            {
                return BuildResult(
                    path,
                    format,
                    "Invalid GLB container",
                    properties,
                    dependencies,
                    issues);
            }
        }
        else
        {
            if (file.Length > MaximumJsonBytes)
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "GLTF_JSON_TOO_LARGE",
                    $"The glTF JSON is {file.Length:N0} bytes; Arkive's inspection limit is {MaximumJsonBytes:N0}."));
                return BuildResult(
                    path,
                    format,
                    "glTF JSON exceeds the inspection limit",
                    properties,
                    dependencies,
                    issues);
            }

            jsonBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                jsonBytes,
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false,
                    MaxDepth = 128,
                });
        }
        catch (JsonException exception)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "GLTF_JSON_INVALID",
                $"The container JSON could not be parsed: {exception.Message}"));
            return BuildResult(
                path,
                format,
                "Invalid glTF JSON",
                properties,
                dependencies,
                issues);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "GLTF_ROOT_INVALID",
                    "The glTF JSON root is not an object."));
                return BuildResult(
                    path,
                    format,
                    "Invalid glTF root",
                    properties,
                    dependencies,
                    issues);
            }

            var version = root.TryGetProperty("asset", out var asset) &&
                          asset.ValueKind == JsonValueKind.Object &&
                          asset.TryGetProperty("version", out var versionElement) &&
                          versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString()
                : null;
            properties.Add(new ArtifactInspectionProperty(
                "Asset",
                "glTF version",
                version ?? "Missing"));
            if (!string.Equals(version, "2.0", StringComparison.Ordinal))
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "GLTF_VERSION_UNSUPPORTED",
                    $"Arkive expects glTF 2.0; the asset reports {version ?? "no version"}."));
            }

            if (asset.ValueKind == JsonValueKind.Object &&
                asset.TryGetProperty("generator", out var generator) &&
                generator.ValueKind == JsonValueKind.String)
            {
                properties.Add(new ArtifactInspectionProperty(
                    "Asset",
                    "Generator",
                    generator.GetString() ?? string.Empty));
            }

            var scenes = GetArray(root, "scenes");
            var nodes = GetArray(root, "nodes");
            var meshes = GetArray(root, "meshes");
            var materials = GetArray(root, "materials");
            var textures = GetArray(root, "textures");
            var images = GetArray(root, "images");
            var samplers = GetArray(root, "samplers");
            var accessors = GetArray(root, "accessors");
            var buffers = GetArray(root, "buffers");
            var bufferViews = GetArray(root, "bufferViews");
            var skins = GetArray(root, "skins");
            var animations = GetArray(root, "animations");
            var cameras = GetArray(root, "cameras");

            if (root.TryGetProperty("scene", out var defaultScene) &&
                !TryReadIndex(defaultScene, scenes.Length, out _))
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "GLTF_DEFAULT_SCENE_INVALID",
                    "The default scene index is outside the scenes array."));
            }

            foreach (var scene in scenes)
            {
                if (!scene.TryGetProperty("nodes", out var sceneNodes) ||
                    sceneNodes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var sceneNode in sceneNodes.EnumerateArray())
                {
                    if (!TryReadIndex(sceneNode, nodes.Length, out _))
                    {
                        issues.Add(new ArtifactInspectionIssue(
                            ArtifactInspectionSeverity.Error,
                            "GLTF_SCENE_NODE_INVALID",
                            "A scene references a node outside the nodes array."));
                    }
                }
            }

            var primitiveCount = 0;
            long primitiveVertices = 0;
            long triangleCount = 0;
            var morphTargetCount = 0;
            var attributes = new HashSet<string>(StringComparer.Ordinal);
            double[]? minimumBounds = null;
            double[]? maximumBounds = null;

            for (var meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
            {
                var mesh = meshes[meshIndex];
                if (!mesh.TryGetProperty("primitives", out var primitives) ||
                    primitives.ValueKind != JsonValueKind.Array)
                {
                    issues.Add(new ArtifactInspectionIssue(
                        ArtifactInspectionSeverity.Error,
                        "GLTF_MESH_PRIMITIVES_MISSING",
                        $"Mesh {meshIndex} has no primitives array."));
                    continue;
                }

                var primitiveIndex = 0;
                foreach (var primitive in primitives.EnumerateArray())
                {
                    primitiveCount++;
                    var mode = GetInt32(primitive, "mode") ?? 4;
                    int? positionAccessorIndex = null;
                    if (primitive.TryGetProperty("attributes", out var attributeObject) &&
                        attributeObject.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var attribute in attributeObject.EnumerateObject())
                        {
                            attributes.Add(attribute.Name);
                            if (!TryReadIndex(attribute.Value, accessors.Length, out var accessorIndex))
                            {
                                issues.Add(new ArtifactInspectionIssue(
                                    ArtifactInspectionSeverity.Error,
                                    "GLTF_ATTRIBUTE_ACCESSOR_INVALID",
                                    $"Mesh {meshIndex} primitive {primitiveIndex} attribute {attribute.Name} references an invalid accessor."));
                                continue;
                            }

                            if (attribute.NameEquals("POSITION"))
                            {
                                positionAccessorIndex = accessorIndex;
                            }
                        }
                    }
                    else
                    {
                        issues.Add(new ArtifactInspectionIssue(
                            ArtifactInspectionSeverity.Error,
                            "GLTF_PRIMITIVE_ATTRIBUTES_MISSING",
                            $"Mesh {meshIndex} primitive {primitiveIndex} has no attributes object."));
                    }

                    long elementCount = 0;
                    if (positionAccessorIndex is { } positionIndex)
                    {
                        var positionAccessor = accessors[positionIndex];
                        var count = GetInt64(positionAccessor, "count") ?? 0;
                        primitiveVertices += count;
                        elementCount = count;
                        ExpandBounds(positionAccessor, ref minimumBounds, ref maximumBounds);
                    }

                    if (primitive.TryGetProperty("indices", out var indicesElement))
                    {
                        if (TryReadIndex(indicesElement, accessors.Length, out var indicesAccessor))
                        {
                            elementCount = GetInt64(accessors[indicesAccessor], "count") ?? 0;
                        }
                        else
                        {
                            issues.Add(new ArtifactInspectionIssue(
                                ArtifactInspectionSeverity.Error,
                                "GLTF_INDEX_ACCESSOR_INVALID",
                                $"Mesh {meshIndex} primitive {primitiveIndex} references an invalid index accessor."));
                        }
                    }

                    triangleCount += mode switch
                    {
                        4 => elementCount / 3,
                        5 or 6 when elementCount >= 3 => elementCount - 2,
                        _ => 0,
                    };

                    if (primitive.TryGetProperty("material", out var materialElement) &&
                        !TryReadIndex(materialElement, materials.Length, out _))
                    {
                        issues.Add(new ArtifactInspectionIssue(
                            ArtifactInspectionSeverity.Error,
                            "GLTF_MATERIAL_INDEX_INVALID",
                            $"Mesh {meshIndex} primitive {primitiveIndex} references an invalid material."));
                    }

                    if (primitive.TryGetProperty("targets", out var targets) &&
                        targets.ValueKind == JsonValueKind.Array)
                    {
                        morphTargetCount += targets.GetArrayLength();
                    }

                    primitiveIndex++;
                }
            }

            ValidateOptionalObjectIndex(nodes, "mesh", meshes.Length, "node mesh", issues);
            ValidateOptionalObjectIndex(nodes, "skin", skins.Length, "node skin", issues);
            ValidateOptionalObjectIndex(textures, "source", images.Length, "texture source", issues);
            ValidateOptionalObjectIndex(textures, "sampler", samplers.Length, "texture sampler", issues);
            ValidateObjectIndex(bufferViews, "buffer", buffers.Length, "buffer view", issues);
            ValidateOptionalObjectIndex(images, "bufferView", bufferViews.Length, "image buffer view", issues);
            ValidateOptionalObjectIndex(accessors, "bufferView", bufferViews.Length, "accessor buffer view", issues);
            ValidateBufferViewRanges(bufferViews, buffers, issues);
            ValidateAccessors(accessors, issues);

            var jointCount = 0;
            var maximumSkinJoints = 0;
            foreach (var skin in skins)
            {
                if (skin.TryGetProperty("joints", out var joints) &&
                    joints.ValueKind == JsonValueKind.Array)
                {
                    jointCount += joints.GetArrayLength();
                    maximumSkinJoints = Math.Max(maximumSkinJoints, joints.GetArrayLength());
                    foreach (var joint in joints.EnumerateArray())
                    {
                        if (!TryReadIndex(joint, nodes.Length, out _))
                        {
                            issues.Add(new ArtifactInspectionIssue(
                                ArtifactInspectionSeverity.Error,
                                "GLTF_SKIN_JOINT_INVALID",
                                "A skin references a joint outside the nodes array."));
                        }
                    }
                }
                else
                {
                    issues.Add(new ArtifactInspectionIssue(
                        ArtifactInspectionSeverity.Error,
                        "GLTF_SKIN_JOINTS_MISSING",
                        "A skin has no joints array."));
                }
            }

            dependencies.AddRange(InspectDependencies(
                path,
                buffers,
                images,
                glbBinBytes,
                issues));

            var extensionsUsed = ReadStringArray(root, "extensionsUsed");
            var extensionsRequired = ReadStringArray(root, "extensionsRequired");
            AddCountProperties(
                properties,
                scenes.Length,
                nodes.Length,
                meshes.Length,
                primitiveCount,
                primitiveVertices,
                triangleCount,
                materials.Length,
                textures.Length,
                images.Length,
                accessors.Length,
                buffers.Length,
                bufferViews.Length,
                skins.Length,
                jointCount,
                maximumSkinJoints,
                animations.Length,
                morphTargetCount,
                cameras.Length);
            properties.Add(new ArtifactInspectionProperty(
                "Geometry",
                "Attributes",
                attributes.Count == 0 ? "None" : string.Join(", ", attributes.Order())));
            properties.Add(new ArtifactInspectionProperty(
                "Extensions",
                "Used",
                extensionsUsed.Length == 0 ? "None" : string.Join(", ", extensionsUsed)));
            properties.Add(new ArtifactInspectionProperty(
                "Extensions",
                "Required",
                extensionsRequired.Length == 0 ? "None" : string.Join(", ", extensionsRequired)));
            if (minimumBounds is not null && maximumBounds is not null)
            {
                properties.Add(new ArtifactInspectionProperty(
                    "Geometry",
                    "Bounds",
                    $"[{FormatVector(minimumBounds)}] to [{FormatVector(maximumBounds)}]"));
            }

            properties.Add(new ArtifactInspectionProperty(
                "Dependencies",
                "External files",
                dependencies.Count(dependency => !dependency.IsEmbedded).ToString()));
            properties.Add(new ArtifactInspectionProperty(
                "Dependencies",
                "Missing files",
                dependencies.Count(dependency => !dependency.IsEmbedded && !dependency.Exists).ToString()));

            var summary =
                $"{meshes.Length:N0} meshes, {primitiveCount:N0} primitives, " +
                $"{triangleCount:N0} triangles, {materials.Length:N0} materials, " +
                $"{skins.Length:N0} skins, {animations.Length:N0} animations";
            return BuildResult(
                path,
                format,
                summary,
                properties,
                dependencies,
                issues);
        }
    }

    private static async Task<(byte[] JsonBytes, long? BinBytes)> ReadGlbAsync(
        string path,
        long fileLength,
        ICollection<ArtifactInspectionProperty> properties,
        ICollection<ArtifactInspectionIssue> issues,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            useAsync: true);
        if (fileLength < 12)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "GLB_HEADER_TRUNCATED",
                "A GLB container requires a 12-byte header."));
            return ([], null);
        }

        var header = new byte[12];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        properties.Add(new ArtifactInspectionProperty("GLB header", "Version", version.ToString()));
        properties.Add(new ArtifactInspectionProperty("GLB header", "Declared bytes", $"{declaredLength:N0}"));
        if (magic != GlbMagic)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "GLB_MAGIC_INVALID",
                "The file does not begin with the GLB magic value."));
            return ([], null);
        }

        if (version != 2)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "GLB_VERSION_UNSUPPORTED",
                $"Arkive expects GLB version 2; this container reports version {version}."));
        }

        if (declaredLength != fileLength)
        {
            issues.Add(new ArtifactInspectionIssue(
                declaredLength > fileLength
                    ? ArtifactInspectionSeverity.Error
                    : ArtifactInspectionSeverity.Warning,
                "GLB_LENGTH_MISMATCH",
                $"The GLB header declares {declaredLength:N0} bytes, while the file contains {fileLength:N0}."));
        }

        byte[] jsonBytes = [];
        long? binBytes = null;
        var chunkIndex = 0;
        while (stream.Position + 8 <= fileLength)
        {
            var chunkHeader = new byte[8];
            await stream.ReadExactlyAsync(chunkHeader, cancellationToken).ConfigureAwait(false);
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader);
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4));
            if (chunkIndex == 0 && chunkType != GlbJsonChunk)
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "GLB_FIRST_CHUNK_NOT_JSON",
                    "The first GLB chunk must contain JSON."));
            }

            if (chunkLength % 4 != 0)
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Warning,
                    "GLB_CHUNK_ALIGNMENT_INVALID",
                    $"Chunk {chunkIndex} is not padded to a four-byte boundary."));
            }

            if (chunkLength > fileLength - stream.Position)
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "GLB_CHUNK_TRUNCATED",
                    $"Chunk {chunkIndex} declares {chunkLength:N0} bytes beyond the end of the file."));
                break;
            }

            if (chunkType == GlbJsonChunk)
            {
                if (jsonBytes.Length > 0)
                {
                    issues.Add(new ArtifactInspectionIssue(
                        ArtifactInspectionSeverity.Warning,
                        "GLB_JSON_CHUNK_DUPLICATE",
                        "The GLB contains more than one JSON chunk."));
                    stream.Seek(chunkLength, SeekOrigin.Current);
                }
                else if (chunkLength > MaximumJsonBytes)
                {
                    issues.Add(new ArtifactInspectionIssue(
                        ArtifactInspectionSeverity.Error,
                        "GLB_JSON_TOO_LARGE",
                        $"The GLB JSON chunk exceeds Arkive's {MaximumJsonBytes:N0}-byte inspection limit."));
                    stream.Seek(chunkLength, SeekOrigin.Current);
                }
                else
                {
                    jsonBytes = new byte[checked((int)chunkLength)];
                    await stream.ReadExactlyAsync(jsonBytes, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                if (chunkType == GlbBinChunk && binBytes is null)
                {
                    binBytes = chunkLength;
                }

                stream.Seek(chunkLength, SeekOrigin.Current);
            }

            chunkIndex++;
        }

        properties.Add(new ArtifactInspectionProperty("GLB header", "Chunks", chunkIndex.ToString()));
        properties.Add(new ArtifactInspectionProperty(
            "GLB header",
            "BIN bytes",
            binBytes?.ToString("N0") ?? "None"));
        if (jsonBytes.Length == 0)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "GLB_JSON_CHUNK_MISSING",
                "The GLB contains no readable JSON chunk."));
        }

        return (jsonBytes, binBytes);
    }

    private static IReadOnlyList<ArtifactInspectionDependency> InspectDependencies(
        string containerPath,
        IReadOnlyList<JsonElement> buffers,
        IReadOnlyList<JsonElement> images,
        long? glbBinBytes,
        ICollection<ArtifactInspectionIssue> issues)
    {
        var dependencies = new List<ArtifactInspectionDependency>();
        for (var index = 0; index < buffers.Count; index++)
        {
            var buffer = buffers[index];
            var declaredBytes = GetInt64(buffer, "byteLength");
            if (declaredBytes is null or < 0)
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "GLTF_BUFFER_LENGTH_INVALID",
                    $"Buffer {index} has no valid non-negative byteLength."));
            }

            if (!buffer.TryGetProperty("uri", out var uriElement) ||
                uriElement.ValueKind != JsonValueKind.String)
            {
                var actual = index == 0 ? glbBinBytes : null;
                dependencies.Add(new ArtifactInspectionDependency(
                    "Buffer",
                    $"Embedded buffer {index}",
                    null,
                    IsEmbedded: true,
                    Exists: actual is not null,
                    declaredBytes,
                    actual));
                if (actual is null)
                {
                    issues.Add(new ArtifactInspectionIssue(
                        ArtifactInspectionSeverity.Error,
                        "GLTF_EMBEDDED_BUFFER_MISSING",
                        $"Buffer {index} has no URI and no matching GLB BIN chunk."));
                }
                else if (declaredBytes is { } declared && actual < declared)
                {
                    issues.Add(new ArtifactInspectionIssue(
                        ArtifactInspectionSeverity.Error,
                        "GLTF_BUFFER_TRUNCATED",
                        $"Embedded buffer {index} declares {declared:N0} bytes but the BIN chunk has {actual:N0}."));
                }

                continue;
            }

            var uri = uriElement.GetString() ?? string.Empty;
            dependencies.Add(InspectUriDependency(
                containerPath,
                "Buffer",
                uri,
                declaredBytes,
                issues));
        }

        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            if (image.TryGetProperty("uri", out var uriElement) &&
                uriElement.ValueKind == JsonValueKind.String)
            {
                dependencies.Add(InspectUriDependency(
                    containerPath,
                    "Image",
                    uriElement.GetString() ?? string.Empty,
                    declaredBytes: null,
                    issues));
            }
            else if (image.TryGetProperty("bufferView", out _))
            {
                dependencies.Add(new ArtifactInspectionDependency(
                    "Image",
                    $"Embedded image {index}",
                    null,
                    IsEmbedded: true,
                    Exists: true,
                    DeclaredBytes: null,
                    ActualBytes: null));
            }
            else
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Warning,
                    "GLTF_IMAGE_SOURCE_MISSING",
                    $"Image {index} has neither a URI nor a bufferView."));
            }
        }

        return dependencies;
    }

    private static ArtifactInspectionDependency InspectUriDependency(
        string containerPath,
        string kind,
        string reference,
        long? declaredBytes,
        ICollection<ArtifactInspectionIssue> issues)
    {
        if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return new ArtifactInspectionDependency(
                kind,
                "data URI",
                null,
                IsEmbedded: true,
                Exists: true,
                declaredBytes,
                ActualBytes: null);
        }

        if (Uri.TryCreate(reference, UriKind.Absolute, out var absoluteUri) &&
            !absoluteUri.IsFile)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Warning,
                "GLTF_REMOTE_DEPENDENCY",
                $"{kind} dependency {reference} is remote; Arkive does not fetch it."));
            return new ArtifactInspectionDependency(
                kind,
                reference,
                null,
                IsEmbedded: false,
                Exists: false,
                declaredBytes,
                ActualBytes: null);
        }

        var baseDirectory = Path.GetDirectoryName(containerPath)!;
        string resolvedPath;
        try
        {
            var decoded = absoluteUri?.IsFile == true
                ? absoluteUri.LocalPath
                : Uri.UnescapeDataString(reference.Replace('/', Path.DirectorySeparatorChar));
            resolvedPath = Path.GetFullPath(Path.IsPathRooted(decoded)
                ? decoded
                : Path.Combine(baseDirectory, decoded));
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "GLTF_DEPENDENCY_URI_INVALID",
                $"{kind} dependency {reference} could not be resolved: {exception.Message}"));
            return new ArtifactInspectionDependency(
                kind,
                reference,
                null,
                IsEmbedded: false,
                Exists: false,
                declaredBytes,
                ActualBytes: null);
        }

        var basePrefix = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
        if (!resolvedPath.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "GLTF_DEPENDENCY_ESCAPES_EXPORT",
                $"{kind} dependency {reference} resolves outside the export session."));
            return new ArtifactInspectionDependency(
                kind,
                reference,
                resolvedPath,
                IsEmbedded: false,
                Exists: false,
                declaredBytes,
                ActualBytes: null);
        }

        var exists = File.Exists(resolvedPath);
        long? actualBytes = exists ? new FileInfo(resolvedPath).Length : null;
        if (!exists)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "GLTF_DEPENDENCY_MISSING",
                $"{kind} dependency {reference} was not found beside the export."));
        }
        else if (declaredBytes is { } declared && actualBytes < declared)
        {
            issues.Add(new ArtifactInspectionIssue(
                ArtifactInspectionSeverity.Error,
                "GLTF_BUFFER_TRUNCATED",
                $"Buffer {reference} declares {declared:N0} bytes but contains {actualBytes:N0}."));
        }

        return new ArtifactInspectionDependency(
            kind,
            reference,
            resolvedPath,
            IsEmbedded: false,
            exists,
            declaredBytes,
            actualBytes);
    }

    private static void ValidateObjectIndex(
        IReadOnlyList<JsonElement> objects,
        string propertyName,
        int targetCount,
        string description,
        ICollection<ArtifactInspectionIssue> issues)
    {
        for (var index = 0; index < objects.Count; index++)
        {
            if (!objects[index].TryGetProperty(propertyName, out var value) ||
                !TryReadIndex(value, targetCount, out _))
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "GLTF_INDEX_INVALID",
                    $"{description} {index} has a missing or invalid {propertyName} index."));
            }
        }
    }

    private static void ValidateOptionalObjectIndex(
        IReadOnlyList<JsonElement> objects,
        string propertyName,
        int targetCount,
        string description,
        ICollection<ArtifactInspectionIssue> issues)
    {
        for (var index = 0; index < objects.Count; index++)
        {
            if (objects[index].TryGetProperty(propertyName, out var value) &&
                !TryReadIndex(value, targetCount, out _))
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "GLTF_INDEX_INVALID",
                    $"{description} {index} has an invalid {propertyName} index."));
            }
        }
    }

    private static void ValidateBufferViewRanges(
        IReadOnlyList<JsonElement> bufferViews,
        IReadOnlyList<JsonElement> buffers,
        ICollection<ArtifactInspectionIssue> issues)
    {
        for (var index = 0; index < bufferViews.Count; index++)
        {
            var view = bufferViews[index];
            if (!view.TryGetProperty("buffer", out var bufferElement) ||
                !TryReadIndex(bufferElement, buffers.Count, out var bufferIndex))
            {
                continue;
            }

            var offset = GetInt64(view, "byteOffset") ?? 0;
            var length = GetInt64(view, "byteLength");
            if (offset < 0 || length is null or < 0)
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "GLTF_BUFFER_VIEW_RANGE_INVALID",
                    $"Buffer view {index} has an invalid byteOffset or byteLength."));
                continue;
            }

            var bufferLength = GetInt64(buffers[bufferIndex], "byteLength");
            if (bufferLength is { } available &&
                (offset > available || length.Value > available - offset))
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "GLTF_BUFFER_VIEW_OUT_OF_RANGE",
                    $"Buffer view {index} extends beyond buffer {bufferIndex}."));
            }
        }
    }

    private static void ValidateAccessors(
        IReadOnlyList<JsonElement> accessors,
        ICollection<ArtifactInspectionIssue> issues)
    {
        for (var index = 0; index < accessors.Count; index++)
        {
            var accessor = accessors[index];
            var count = GetInt64(accessor, "count");
            if (count is null or < 0)
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "GLTF_ACCESSOR_COUNT_INVALID",
                    $"Accessor {index} has no valid non-negative count."));
            }

            if (GetInt32(accessor, "componentType") is null ||
                !accessor.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String)
            {
                issues.Add(new ArtifactInspectionIssue(
                    ArtifactInspectionSeverity.Error,
                    "GLTF_ACCESSOR_FORMAT_INVALID",
                    $"Accessor {index} is missing componentType or type."));
            }
        }
    }

    private static bool TryReadIndex(JsonElement element, int targetCount, out int index)
    {
        index = -1;
        return element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out index) &&
               index >= 0 &&
               index < targetCount;
    }

    private static JsonElement[] GetArray(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : [];

    private static long? GetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var result)
            ? result
            : null;

    private static int? GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;

    private static string[] ReadStringArray(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : [];

    private static void ExpandBounds(
        JsonElement accessor,
        ref double[]? minimum,
        ref double[]? maximum)
    {
        if (!TryReadVector(accessor, "min", out var accessorMinimum) ||
            !TryReadVector(accessor, "max", out var accessorMaximum) ||
            accessorMinimum.Length != accessorMaximum.Length)
        {
            return;
        }

        if (minimum is null || maximum is null || minimum.Length != accessorMinimum.Length)
        {
            minimum = accessorMinimum;
            maximum = accessorMaximum;
            return;
        }

        for (var index = 0; index < minimum.Length; index++)
        {
            minimum[index] = Math.Min(minimum[index], accessorMinimum[index]);
            maximum[index] = Math.Max(maximum[index], accessorMaximum[index]);
        }
    }

    private static bool TryReadVector(
        JsonElement element,
        string propertyName,
        out double[] vector)
    {
        vector = [];
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = new List<double>();
        foreach (var item in value.EnumerateArray())
        {
            if (!item.TryGetDouble(out var number))
            {
                return false;
            }

            values.Add(number);
        }

        vector = values.ToArray();
        return vector.Length > 0;
    }

    private static string FormatVector(IEnumerable<double> vector) =>
        string.Join(", ", vector.Select(value => value.ToString("G6")));

    private static void AddCountProperties(
        ICollection<ArtifactInspectionProperty> properties,
        int scenes,
        int nodes,
        int meshes,
        int primitives,
        long vertices,
        long triangles,
        int materials,
        int textures,
        int images,
        int accessors,
        int buffers,
        int bufferViews,
        int skins,
        int joints,
        int maximumSkinJoints,
        int animations,
        int morphTargets,
        int cameras)
    {
        foreach (var property in new ArtifactInspectionProperty[]
        {
            new("Scene", "Scenes", scenes.ToString("N0")),
            new("Scene", "Nodes", nodes.ToString("N0")),
            new("Scene", "Cameras", cameras.ToString("N0")),
            new("Geometry", "Meshes", meshes.ToString("N0")),
            new("Geometry", "Primitives", primitives.ToString("N0")),
            new("Geometry", "Primitive vertices", vertices.ToString("N0")),
            new("Geometry", "Triangles", triangles.ToString("N0")),
            new("Geometry", "Morph targets", morphTargets.ToString("N0")),
            new("Materials", "Materials", materials.ToString("N0")),
            new("Materials", "Textures", textures.ToString("N0")),
            new("Materials", "Images", images.ToString("N0")),
            new("Skinning", "Skins", skins.ToString("N0")),
            new("Skinning", "Joint references", joints.ToString("N0")),
            new("Skinning", "Maximum joints in a skin", maximumSkinJoints.ToString("N0")),
            new("Animation", "Animations", animations.ToString("N0")),
            new("Storage", "Accessors", accessors.ToString("N0")),
            new("Storage", "Buffers", buffers.ToString("N0")),
            new("Storage", "Buffer views", bufferViews.ToString("N0")),
        })
        {
            properties.Add(property);
        }
    }

    private static ArtifactInspectionResult BuildResult(
        string path,
        string format,
        string summary,
        IReadOnlyList<ArtifactInspectionProperty> properties,
        IReadOnlyList<ArtifactInspectionDependency> dependencies,
        IReadOnlyList<ArtifactInspectionIssue> issues) => new(
        path,
        format,
        summary,
        properties,
        dependencies,
        issues);
}
