using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Arkive.Core.StaticMeshes;

internal sealed record StaticMeshGlbBuildResult(
    byte[] Bytes,
    IReadOnlyList<StaticMeshEditableLodManifest> Lods);

internal sealed class ArkiveStaticMeshGlbWriter
{
    private const int ArrayBuffer = 34962;
    private const int ElementArrayBuffer = 34963;
    private const int Float = 5126;
    private const int UnsignedByte = 5121;
    private const int UnsignedShort = 5123;
    private const int UnsignedInt = 5125;

    public StaticMeshGlbBuildResult Build(
        StaticMeshReconnaissanceReport report,
        IReadOnlyList<DecodedStaticMeshLod> lods)
    {
        var binary = new GlbBinaryBuilder();
        var accessors = new List<Dictionary<string, object?>>();
        var meshes = new List<Dictionary<string, object?>>();
        var nodes = new List<Dictionary<string, object?>>();
        var materials = BuildMaterials(report);
        var manifests = new List<StaticMeshEditableLodManifest>(lods.Count);

        foreach (var lod in lods)
        {
            var vertexCount = lod.Evidence.VertexCount;
            var positionAccessor = AddAccessor(
                binary,
                accessors,
                StaticMeshSemanticDecoder.EncodeVector3(lod.Positions),
                Float,
                vertexCount,
                "VEC3",
                ArrayBuffer,
                new[] { lod.Minimum.X, lod.Minimum.Y, lod.Minimum.Z },
                new[] { lod.Maximum.X, lod.Maximum.Y, lod.Maximum.Z });
            var normalAccessor = AddAccessor(
                binary, accessors, StaticMeshSemanticDecoder.EncodeVector3(lod.Normals),
                Float, vertexCount, "VEC3", ArrayBuffer);
            var tangentAccessor = AddAccessor(
                binary, accessors, StaticMeshSemanticDecoder.EncodeVector4(lod.Tangents),
                Float, vertexCount, "VEC4", ArrayBuffer);
            var idBytes = StaticMeshSemanticDecoder.EncodeUInt32(
                Enumerable.Range(0, vertexCount).Select(value => (uint)value));
            var idAccessor = AddAccessor(
                binary,
                accessors,
                idBytes,
                UnsignedInt,
                vertexCount,
                "SCALAR",
                ArrayBuffer,
                new uint[] { 0 },
                new uint[] { checked((uint)Math.Max(0, vertexCount - 1)) },
                extras: new Dictionary<string, object?>
                {
                    ["arkiveStableVertexId"] = true,
                    ["lod"] = lod.Evidence.LodIndex,
                });
            var uvAccessors = lod.TextureCoordinates.Select(channel => AddAccessor(
                binary,
                accessors,
                StaticMeshSemanticDecoder.EncodeVector2(channel),
                Float,
                vertexCount,
                "VEC2",
                ArrayBuffer)).ToArray();
            var stableIdUvSet = uvAccessors.Length;
            var stableUv = StaticMeshSemanticDecoder.EncodeVector2(
                Enumerable.Range(0, vertexCount)
                    .Select(value => new System.Numerics.Vector2(value, 0.625f)).ToArray());
            var stableUvAccessor = AddAccessor(
                binary,
                accessors,
                stableUv,
                Float,
                vertexCount,
                "VEC2",
                ArrayBuffer,
                extras: new Dictionary<string, object?>
                {
                    ["arkiveStableVertexIdCarrier"] = true,
                    ["arkiveSentinel"] = 0.625f,
                    ["lod"] = lod.Evidence.LodIndex,
                });
            int? colourAccessor = lod.Colours is null
                ? null
                : AddAccessor(binary, accessors, lod.Colours,
                    UnsignedByte, vertexCount, "VEC4", ArrayBuffer, normalized: true);

            var primitives = new List<Dictionary<string, object?>>(lod.Evidence.Sections.Count);
            foreach (var section in lod.Evidence.Sections)
            {
                var sectionIndices = new ushort[checked(section.TriangleCount * 3)];
                Array.Copy(lod.Indices, section.FirstIndex, sectionIndices, 0, sectionIndices.Length);
                var indexAccessor = AddAccessor(
                    binary,
                    accessors,
                    StaticMeshSemanticDecoder.EncodeUInt16(sectionIndices),
                    UnsignedShort,
                    sectionIndices.Length,
                    "SCALAR",
                    ElementArrayBuffer,
                    new[] { sectionIndices.DefaultIfEmpty().Min() },
                    new[] { sectionIndices.DefaultIfEmpty().Max() });
                var attributes = new Dictionary<string, object?>
                {
                    ["POSITION"] = positionAccessor,
                    ["NORMAL"] = normalAccessor,
                    ["TANGENT"] = tangentAccessor,
                    ["_ARKIVE_VERTEX_ID"] = idAccessor,
                };
                for (var uvIndex = 0; uvIndex < uvAccessors.Length; uvIndex++)
                    attributes[$"TEXCOORD_{uvIndex}"] = uvAccessors[uvIndex];
                attributes[$"TEXCOORD_{stableIdUvSet}"] = stableUvAccessor;
                if (colourAccessor is not null) attributes["COLOR_0"] = colourAccessor.Value;
                primitives.Add(new Dictionary<string, object?>
                {
                    ["attributes"] = attributes,
                    ["indices"] = indexAccessor,
                    ["material"] = MaterialIndex(lod.Evidence.LodIndex, section.SectionIndex, report),
                    ["mode"] = 4,
                    ["extras"] = new Dictionary<string, object?>
                    {
                        ["arkiveSection"] = section.SectionIndex,
                        ["nativeMaterialReference"] = section.MaterialReference,
                        ["nativeFirstIndex"] = section.FirstIndex,
                        ["nativeTriangleCount"] = section.TriangleCount,
                    },
                });
            }

            var meshIndex = meshes.Count;
            var meshName = $"{report.ObjectName}_LOD{lod.Evidence.LodIndex}";
            meshes.Add(new Dictionary<string, object?>
            {
                ["name"] = meshName,
                ["primitives"] = primitives,
                ["extras"] = new Dictionary<string, object?>
                {
                    ["arkiveLod"] = lod.Evidence.LodIndex,
                    ["nativeVertexCount"] = vertexCount,
                    ["arkiveStableVertexIdUvSet"] = stableIdUvSet,
                },
            });
            nodes.Add(new Dictionary<string, object?>
            {
                ["name"] = meshName + "_EDIT",
                ["mesh"] = meshIndex,
                ["extras"] = new Dictionary<string, object?>
                {
                    ["arkiveEditableStaticMesh"] = true,
                    ["arkiveLod"] = lod.Evidence.LodIndex,
                },
            });
            manifests.Add(new StaticMeshEditableLodManifest(
                lod.Evidence.LodIndex,
                vertexCount,
                lod.Evidence.Indices.ElementCount,
                lod.Evidence.Indices.ElementCount / 3,
                lod.Evidence.Sections.Count,
                lod.Evidence.TextureCoordinateCount,
                lod.Evidence.UsesFullPrecisionUvs,
                stableIdUvSet,
                lod.Evidence.Positions.DataSha256,
                lod.Evidence.VertexAttributes.DataSha256,
                lod.Evidence.Indices.DataSha256,
                lod.PositionSha256,
                lod.StableVertexIdSha256));
        }

        var root = new Dictionary<string, object?>
        {
            ["asset"] = new Dictionary<string, object?>
            {
                ["version"] = "2.0",
                ["generator"] = "Arkive native Lost Ark StaticMesh exporter",
                ["extras"] = new Dictionary<string, object?>
                {
                    ["arkiveSchema"] = StaticMeshEditableExportService.CurrentSchemaVersion,
                    ["sourceObject"] = report.ObjectName,
                    ["nativeParserProfile"] = report.NativeLayout!.ParserProfile,
                },
            },
            ["scene"] = 0,
            ["scenes"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = report.ObjectName + "_ArkiveStaticEdit",
                    ["nodes"] = Enumerable.Range(0, nodes.Count).ToArray(),
                },
            },
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["materials"] = materials,
            ["accessors"] = accessors,
            ["bufferViews"] = binary.BufferViews,
            ["buffers"] = new[] { new Dictionary<string, object?> { ["byteLength"] = binary.Length } },
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        return new StaticMeshGlbBuildResult(BuildGlb(json, binary.ToArray()), manifests);
    }

    private static IReadOnlyList<Dictionary<string, object?>> BuildMaterials(
        StaticMeshReconnaissanceReport report)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var lod in report.NativeLayout!.Lods)
        {
            var oracleLod = report.Oracle?.Lods.SingleOrDefault(item => item.LodIndex == lod.LodIndex);
            foreach (var section in lod.Sections)
            {
                var name = oracleLod?.Sections.SingleOrDefault(item => item.SectionIndex == section.SectionIndex)?.Material;
                result.Add(new Dictionary<string, object?>
                {
                    ["name"] = string.IsNullOrWhiteSpace(name)
                        ? $"material_lod{lod.LodIndex}_section{section.SectionIndex}"
                        : name,
                    ["doubleSided"] = true,
                    ["extras"] = new Dictionary<string, object?>
                    {
                        ["arkiveLod"] = lod.LodIndex,
                        ["arkiveSection"] = section.SectionIndex,
                        ["nativeMaterialReference"] = section.MaterialReference,
                    },
                    ["pbrMetallicRoughness"] = new Dictionary<string, object?>
                    {
                        ["baseColorFactor"] = new[] { 0.65, 0.65, 0.65, 1.0 },
                        ["metallicFactor"] = 0.0,
                        ["roughnessFactor"] = 0.8,
                    },
                });
            }
        }
        return result;
    }

    private static int MaterialIndex(int lodIndex, int sectionIndex, StaticMeshReconnaissanceReport report)
    {
        var index = 0;
        foreach (var lod in report.NativeLayout!.Lods)
        {
            foreach (var section in lod.Sections)
            {
                if (lod.LodIndex == lodIndex && section.SectionIndex == sectionIndex) return index;
                index++;
            }
        }
        throw new InvalidDataException("StaticMesh material/section mapping was not found.");
    }

    private static int AddAccessor(
        GlbBinaryBuilder binary,
        ICollection<Dictionary<string, object?>> accessors,
        byte[] data,
        int componentType,
        int count,
        string type,
        int? target,
        object? minimum = null,
        object? maximum = null,
        bool normalized = false,
        Dictionary<string, object?>? extras = null)
    {
        var view = binary.Append(data, target);
        var accessor = new Dictionary<string, object?>
        {
            ["bufferView"] = view,
            ["byteOffset"] = 0,
            ["componentType"] = componentType,
            ["count"] = count,
            ["type"] = type,
        };
        if (minimum is not null) accessor["min"] = minimum;
        if (maximum is not null) accessor["max"] = maximum;
        if (normalized) accessor["normalized"] = true;
        if (extras is not null) accessor["extras"] = extras;
        var index = accessors.Count;
        accessors.Add(accessor);
        return index;
    }

    private static byte[] BuildGlb(byte[] json, byte[] binary)
    {
        var paddedJsonLength = Align4(json.Length);
        var paddedBinaryLength = Align4(binary.Length);
        var total = checked(12 + 8 + paddedJsonLength + 8 + paddedBinaryLength);
        var output = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(output, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), checked((uint)total));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), checked((uint)paddedJsonLength));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), 0x4E4F534A);
        json.CopyTo(output.AsSpan(20));
        output.AsSpan(20 + json.Length, paddedJsonLength - json.Length).Fill((byte)' ');
        var binaryHeader = 20 + paddedJsonLength;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(binaryHeader), checked((uint)paddedBinaryLength));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(binaryHeader + 4), 0x004E4942);
        binary.CopyTo(output.AsSpan(binaryHeader + 8));
        return output;
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
            };
            if (target is not null) view["target"] = target.Value;
            var index = BufferViews.Count;
            BufferViews.Add(view);
            return index;
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
