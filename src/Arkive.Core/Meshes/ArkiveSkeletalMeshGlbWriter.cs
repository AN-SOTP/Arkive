using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Arkive.Core.Inspection;

namespace Arkive.Core.Meshes;

internal sealed record ArkiveGlbBuildResult(byte[] Bytes, IReadOnlyList<SkeletalMeshEditableLodManifest> Lods);

internal sealed class ArkiveSkeletalMeshGlbWriter
{
    private const int ArrayBuffer = 34962;
    private const int ElementArrayBuffer = 34963;
    private const int Float = 5126;
    private const int UnsignedByte = 5121;
    private const int UnsignedShort = 5123;
    private const int UnsignedInt = 5125;

    public ArkiveGlbBuildResult Build(
        SkeletalMeshReconnaissanceReport report,
        IReadOnlyList<DecodedSkeletalMeshLod> lods)
    {
        var layout = report.NativeLayout ?? throw new InvalidOperationException("Native layout is required.");
        var binary = new GlbBinaryBuilder();
        var accessors = new List<Dictionary<string, object?>>();
        var meshes = new List<Dictionary<string, object?>>();
        var nodes = new List<Dictionary<string, object?>>();
        var lodManifests = new List<SkeletalMeshEditableLodManifest>(lods.Count);
        var materialCount = Math.Max(layout.MaterialReferenceCount,
            layout.Lods.SelectMany(lod => lod.Sections).Select(section => section.MaterialIndex + 1).DefaultIfEmpty().Max());
        var materials = BuildMaterials(report, materialCount);

        foreach (var lod in lods)
        {
            var vertexCount = lod.Evidence.VertexCount;
            var positionAccessor = AddAccessor(binary, accessors,
                SkeletalMeshSemanticDecoder.EncodeVector3(lod.Positions), Float, vertexCount, "VEC3", ArrayBuffer,
                minimum: new[] { lod.Bounds.MinimumX, lod.Bounds.MinimumY, lod.Bounds.MinimumZ },
                maximum: new[] { lod.Bounds.MaximumX, lod.Bounds.MaximumY, lod.Bounds.MaximumZ });
            var normalAccessor = AddAccessor(binary, accessors,
                SkeletalMeshSemanticDecoder.EncodeVector3(lod.Normals), Float, vertexCount, "VEC3", ArrayBuffer);
            var tangentAccessor = AddAccessor(binary, accessors,
                SkeletalMeshSemanticDecoder.EncodeVector4(lod.Tangents), Float, vertexCount, "VEC4", ArrayBuffer);
            var jointAccessor = AddAccessor(binary, accessors,
                SkeletalMeshSemanticDecoder.EncodeUInt16(lod.Joints), UnsignedShort, vertexCount, "VEC4", ArrayBuffer);
            var weightAccessor = AddAccessor(binary, accessors,
                SkeletalMeshSemanticDecoder.EncodeVector4(lod.Weights), Float, vertexCount, "VEC4", ArrayBuffer);
            var idBytes = SkeletalMeshSemanticDecoder.EncodeUInt32(
                Enumerable.Range(0, vertexCount).Select(value => (uint)value));
            var vertexIdAccessor = AddAccessor(binary, accessors, idBytes,
                UnsignedInt, vertexCount, "SCALAR", ArrayBuffer,
                minimum: new uint[] { 0 }, maximum: new uint[] { checked((uint)Math.Max(0, vertexCount - 1)) },
                extras: new Dictionary<string, object?>
                {
                    ["arkiveStableVertexId"] = true,
                    ["lod"] = lod.Evidence.LodIndex,
                });
            var uvAccessors = lod.TextureCoordinates.Select(channel => AddAccessor(
                binary, accessors, SkeletalMeshSemanticDecoder.EncodeVector2(channel),
                Float, vertexCount, "VEC2", ArrayBuffer)).ToArray();
            var stableIdUvSet = uvAccessors.Length;
            var stableIdUvBytes = SkeletalMeshSemanticDecoder.EncodeVector2(
                Enumerable.Range(0, vertexCount)
                    .Select(value => new Vector2(value, 0.375f))
                    .ToArray());
            var stableIdUvAccessor = AddAccessor(
                binary, accessors, stableIdUvBytes, Float, vertexCount, "VEC2", ArrayBuffer,
                extras: new Dictionary<string, object?>
                {
                    ["arkiveStableVertexIdCarrier"] = true,
                    ["arkiveSentinel"] = 0.375f,
                    ["lod"] = lod.Evidence.LodIndex,
                });
            int? colorAccessor = lod.Colors is null
                ? null
                : AddAccessor(binary, accessors, lod.Colors,
                    UnsignedByte, vertexCount, "VEC4", ArrayBuffer, normalized: true);

            var primitives = new List<Dictionary<string, object?>>(lod.Evidence.Sections.Count);
            foreach (var section in lod.Evidence.Sections)
            {
                var sectionIndices = new uint[checked(section.TriangleCount * 3)];
                Array.Copy(lod.Indices, section.FirstIndex, sectionIndices, 0, sectionIndices.Length);
                if (lod.ReverseWinding)
                {
                    for (var index = 0; index < sectionIndices.Length; index += 3)
                        (sectionIndices[index + 1], sectionIndices[index + 2]) =
                            (sectionIndices[index + 2], sectionIndices[index + 1]);
                }
                var maximumIndex = sectionIndices.DefaultIfEmpty().Max();
                var useShort = maximumIndex <= ushort.MaxValue;
                var indexData = useShort
                    ? EncodeUInt16(sectionIndices.Select(value => checked((ushort)value)).ToArray())
                    : SkeletalMeshSemanticDecoder.EncodeUInt32(sectionIndices);
                var indexAccessor = AddAccessor(binary, accessors, indexData,
                    useShort ? UnsignedShort : UnsignedInt, sectionIndices.Length,
                    "SCALAR", ElementArrayBuffer,
                    minimum: new[] { sectionIndices.DefaultIfEmpty().Min() }, maximum: new[] { maximumIndex });
                var attributes = new Dictionary<string, object?>
                {
                    ["POSITION"] = positionAccessor,
                    ["NORMAL"] = normalAccessor,
                    ["TANGENT"] = tangentAccessor,
                    ["JOINTS_0"] = jointAccessor,
                    ["WEIGHTS_0"] = weightAccessor,
                    ["_ARKIVE_VERTEX_ID"] = vertexIdAccessor,
                };
                for (var uvIndex = 0; uvIndex < uvAccessors.Length; uvIndex++)
                    attributes[$"TEXCOORD_{uvIndex}"] = uvAccessors[uvIndex];
                attributes[$"TEXCOORD_{stableIdUvSet}"] = stableIdUvAccessor;
                if (colorAccessor is not null) attributes["COLOR_0"] = colorAccessor.Value;
                primitives.Add(new Dictionary<string, object?>
                {
                    ["attributes"] = attributes,
                    ["indices"] = indexAccessor,
                    ["material"] = section.MaterialIndex,
                    ["mode"] = 4,
                    ["extras"] = new Dictionary<string, object?>
                    {
                        ["arkiveSection"] = section.SectionIndex,
                        ["arkiveChunk"] = section.ChunkIndex,
                        ["nativeFirstIndex"] = section.FirstIndex,
                        ["nativeTriangleCount"] = section.TriangleCount,
                    },
                });
            }

            var meshName = $"{report.ObjectName}_LOD{lod.Evidence.LodIndex}";
            var nodeName = meshName + "_EDIT";
            var meshIndex = meshes.Count;
            meshes.Add(new Dictionary<string, object?>
            {
                ["name"] = meshName,
                ["primitives"] = primitives,
                ["extras"] = new Dictionary<string, object?>
                {
                    ["arkiveLod"] = lod.Evidence.LodIndex,
                    ["nativeVertexCount"] = vertexCount,
                    ["nativeVertexStride"] = lod.Evidence.VertexBuffer.ElementSize,
                    ["nativePositionOffset"] = lod.Evidence.VertexPositionOffset,
                    ["arkiveStableVertexIdUvSet"] = stableIdUvSet,
                },
            });
            nodes.Add(new Dictionary<string, object?>
            {
                ["name"] = nodeName,
                ["mesh"] = meshIndex,
                ["skin"] = 0,
                ["extras"] = new Dictionary<string, object?>
                {
                    ["arkiveEditableMesh"] = true,
                    ["arkiveLod"] = lod.Evidence.LodIndex,
                },
            });
            lodManifests.Add(new SkeletalMeshEditableLodManifest(
                lod.Evidence.LodIndex, meshName, nodeName, vertexCount,
                lod.Evidence.IndexBuffer.ElementCount,
                lod.Evidence.Sections.Sum(section => section.TriangleCount),
                lod.Evidence.Sections.Count, lod.Evidence.Chunks.Count,
                lod.Evidence.TextureCoordinateCount, lod.Evidence.VertexBuffer.ElementSize,
                lod.Evidence.VertexPositionOffset, lod.Evidence.VertexPositionOffset == 20 ? 2 : 1,
                lod.Evidence.VertexPositionOffset == 20,
                stableIdUvSet,
                lod.ReverseWinding, lod.FaceOrientationTriangleCount,
                lod.DegenerateTriangleCount, lod.OpposedFaceNormalTriangleCount,
                lod.FaceOrientationTriangleCount == 0 ? 0d :
                    lod.OpposedFaceNormalTriangleCount * 100d / lod.FaceOrientationTriangleCount,
                lod.Evidence.IndexBuffer.DataRelativeOffset,
                lod.Evidence.VertexBuffer.DataRelativeOffset,
                lod.Evidence.IndexBuffer.DataSha256, lod.Evidence.VertexBuffer.DataSha256,
                lod.VertexIdSha256, SkeletalMeshSemanticDecoder.Sha(stableIdUvBytes),
                lod.PositionSha256, lod.NormalSha256,
                lod.TangentSha256, lod.JointSha256, lod.WeightSha256,
                lod.UvSha256, lod.ColorSha256, lod.Bounds));
        }

        var meshNodeCount = nodes.Count;
        var (boneNodes, inverseBindMatrices, rootBoneIndices) = BuildSkeleton(layout.Bones, meshNodeCount);
        nodes.AddRange(boneNodes);
        var inverseBindAccessor = AddAccessor(binary, accessors, inverseBindMatrices,
            Float, layout.BoneCount, "MAT4", null);
        var skins = new[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = report.ObjectName + "_ReferenceSkeleton",
                ["inverseBindMatrices"] = inverseBindAccessor,
                ["skeleton"] = meshNodeCount + rootBoneIndices[0],
                ["joints"] = Enumerable.Range(meshNodeCount, layout.BoneCount).ToArray(),
            },
        };
        var sceneNodes = Enumerable.Range(0, meshNodeCount)
            .Concat(rootBoneIndices.Select(index => meshNodeCount + index)).ToArray();
        var root = new Dictionary<string, object?>
        {
            ["asset"] = new Dictionary<string, object?>
            {
                ["version"] = "2.0",
                ["generator"] = "Arkive native Lost Ark SkeletalMesh exporter",
                ["extras"] = new Dictionary<string, object?>
                {
                    ["arkiveSchema"] = SkeletalMeshEditableExportService.CurrentSchemaVersion,
                    ["sourceObject"] = report.ObjectName,
                    ["nativeParserProfile"] = layout.ParserProfile,
                },
            },
            ["scene"] = 0,
            ["scenes"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = report.ObjectName + "_ArkiveEdit",
                    ["nodes"] = sceneNodes,
                },
            },
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["materials"] = materials,
            ["skins"] = skins,
            ["accessors"] = accessors,
            ["bufferViews"] = binary.BufferViews,
            ["buffers"] = new[] { new Dictionary<string, object?> { ["byteLength"] = binary.Length } },
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        return new ArkiveGlbBuildResult(BuildGlb(json, binary.ToArray()), lodManifests);
    }

    private static IReadOnlyList<Dictionary<string, object?>> BuildMaterials(
        SkeletalMeshReconnaissanceReport report,
        int count)
    {
        var names = new string[count];
        for (var index = 0; index < count; index++) names[index] = $"material_slot_{index:D2}";
        if (report.Oracle is { } oracle && report.NativeLayout is { } native)
        {
            foreach (var oracleLod in oracle.Lods)
            {
                var nativeLod = native.Lods.SingleOrDefault(lod => lod.LodIndex == oracleLod.LodIndex);
                if (nativeLod is null) continue;
                foreach (var oracleSection in oracleLod.Sections)
                {
                    var nativeSection = nativeLod.Sections.SingleOrDefault(section =>
                        section.SectionIndex == oracleSection.SectionIndex);
                    if (nativeSection is not null && nativeSection.MaterialIndex >= 0 &&
                        nativeSection.MaterialIndex < names.Length &&
                        !string.IsNullOrWhiteSpace(oracleSection.Material))
                        names[nativeSection.MaterialIndex] = oracleSection.Material;
                }
            }
        }
        return names.Select((name, index) => new Dictionary<string, object?>
        {
            ["name"] = name,
            ["doubleSided"] = true,
            ["pbrMetallicRoughness"] = new Dictionary<string, object?>
            {
                ["baseColorFactor"] = DebugColor(index),
                ["metallicFactor"] = 0f,
                ["roughnessFactor"] = 0.7f,
            },
            ["extras"] = new Dictionary<string, object?> { ["arkiveMaterialSlot"] = index },
        }).ToArray();
    }

    private static float[] DebugColor(int index)
    {
        var hue = (index * 0.61803398875) % 1.0;
        var angle = hue * Math.PI * 2;
        return [(float)(0.55 + 0.3 * Math.Cos(angle)),
            (float)(0.55 + 0.3 * Math.Cos(angle + 2.094)),
            (float)(0.55 + 0.3 * Math.Cos(angle + 4.188)), 1f];
    }

    private static (IReadOnlyList<Dictionary<string, object?>> Nodes, byte[] InverseBindMatrices, int[] Roots)
        BuildSkeleton(IReadOnlyList<SkeletalMeshNativeBoneEvidence> bones, int nodeOffset)
    {
        var nodes = new Dictionary<string, object?>[bones.Count];
        var localMatrices = new Matrix4x4[bones.Count];
        var worldMatrices = new Matrix4x4[bones.Count];
        var roots = bones.Where(bone => bone.BoneIndex == 0 || bone.ParentIndex < 0 || bone.ParentIndex == bone.BoneIndex)
            .Select(bone => bone.BoneIndex).Distinct().ToArray();
        if (roots.Length == 0) roots = [0];
        for (var index = 0; index < bones.Count; index++)
        {
            var bone = bones[index];
            var orientation = new Quaternion(
                bone.OrientationX, bone.OrientationY, bone.OrientationZ, bone.OrientationW);
            if (orientation.LengthSquared() < 1e-12f || !float.IsFinite(orientation.LengthSquared()))
                orientation = Quaternion.Identity;
            else orientation = Quaternion.Normalize(orientation);
            orientation = index == 0
                ? Quaternion.Normalize(new Quaternion(
                    -orientation.X, -orientation.Z, -orientation.Y, orientation.W))
                : Quaternion.Normalize(new Quaternion(
                    orientation.X, orientation.Z, orientation.Y, -orientation.W));
            var translation = new Vector3(
                bone.PositionX * 0.01f, bone.PositionZ * 0.01f, bone.PositionY * 0.01f);
            localMatrices[index] = Matrix4x4.CreateFromQuaternion(orientation) *
                                   Matrix4x4.CreateTranslation(translation);
            var children = bones.Where(candidate => candidate.BoneIndex != index && candidate.ParentIndex == index)
                .Select(candidate => nodeOffset + candidate.BoneIndex).ToArray();
            nodes[index] = new Dictionary<string, object?>
            {
                ["name"] = UniqueBoneName(bone.Name, index),
                ["translation"] = new[] { translation.X, translation.Y, translation.Z },
                ["rotation"] = new[] { orientation.X, orientation.Y, orientation.Z, orientation.W },
                ["children"] = children.Length == 0 ? null : children,
                ["extras"] = new Dictionary<string, object?>
                {
                    ["arkiveBoneIndex"] = index,
                    ["nativeBoneName"] = bone.Name,
                },
            };
        }
        var visiting = new bool[bones.Count];
        var complete = new bool[bones.Count];
        Matrix4x4 ResolveWorld(int index)
        {
            if (complete[index]) return worldMatrices[index];
            if (visiting[index]) throw new SkeletalMeshEditableExportRefusalException(
                "SME210", $"Reference skeleton contains a parent cycle at bone {index}.");
            visiting[index] = true;
            var parent = bones[index].ParentIndex;
            worldMatrices[index] = index == 0 || parent < 0 || parent == index
                ? localMatrices[index]
                : localMatrices[index] * ResolveWorld(parent);
            visiting[index] = false;
            complete[index] = true;
            return worldMatrices[index];
        }
        var inverseBytes = new byte[checked(bones.Count * 64)];
        for (var index = 0; index < bones.Count; index++)
        {
            var world = ResolveWorld(index);
            if (!Matrix4x4.Invert(world, out var inverse))
                throw new SkeletalMeshEditableExportRefusalException(
                    "SME211", $"Reference bone {index} has a non-invertible bind transform.");
            WriteMatrix(inverseBytes.AsSpan(index * 64), inverse);
        }
        return (nodes, inverseBytes, roots);
    }

    private static string UniqueBoneName(string name, int index) =>
        string.IsNullOrWhiteSpace(name) ? $"bone_{index:D4}" : $"{name}__ARKIVE_{index:D4}";

    private static void WriteMatrix(Span<byte> bytes, Matrix4x4 matrix)
    {
        var values = new[]
        {
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        };
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(index * 4)..],
                BitConverter.SingleToInt32Bits(values[index]));
    }

    private static int AddAccessor(
        GlbBinaryBuilder binary,
        ICollection<Dictionary<string, object?>> accessors,
        byte[] data,
        int componentType,
        int count,
        string type,
        int? target,
        bool normalized = false,
        object? minimum = null,
        object? maximum = null,
        object? extras = null)
    {
        var view = binary.Append(data, target);
        var accessor = new Dictionary<string, object?>
        {
            ["bufferView"] = view,
            ["byteOffset"] = 0,
            ["componentType"] = componentType,
            ["count"] = count,
            ["type"] = type,
            ["normalized"] = normalized ? true : null,
            ["min"] = minimum,
            ["max"] = maximum,
            ["extras"] = extras,
        };
        var index = accessors.Count;
        accessors.Add(accessor);
        return index;
    }

    private static byte[] EncodeUInt16(IReadOnlyList<ushort> values) =>
        SkeletalMeshSemanticDecoder.EncodeUInt16(values);

    private static byte[] BuildGlb(byte[] json, byte[] binary)
    {
        var jsonLength = Align4(json.Length);
        var binaryLength = Align4(binary.Length);
        var total = checked(12 + 8 + jsonLength + 8 + binaryLength);
        var result = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)total));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), checked((uint)jsonLength));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 0x4E4F534A);
        json.CopyTo(result.AsSpan(20));
        result.AsSpan(20 + json.Length, jsonLength - json.Length).Fill(0x20);
        var binaryHeader = 20 + jsonLength;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(binaryHeader), checked((uint)binaryLength));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(binaryHeader + 4), 0x004E4942);
        binary.CopyTo(result.AsSpan(binaryHeader + 8));
        return result;
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private sealed class GlbBinaryBuilder
    {
        private readonly MemoryStream _stream = new();
        public List<Dictionary<string, object?>> BufferViews { get; } = [];
        public int Length => checked((int)_stream.Length);

        public int Append(byte[] data, int? target)
        {
            while ((_stream.Position & 3) != 0) _stream.WriteByte(0);
            var offset = checked((int)_stream.Position);
            _stream.Write(data);
            var view = new Dictionary<string, object?>
            {
                ["buffer"] = 0,
                ["byteOffset"] = offset,
                ["byteLength"] = data.Length,
                ["target"] = target,
            };
            var index = BufferViews.Count;
            BufferViews.Add(view);
            return index;
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
