using System.Buffers.Binary;
using System.Security.Cryptography;
using Arkive.Core.Assets;
using Arkive.Core.Inspection;

namespace Arkive.Core.Meshes;

public sealed class SkeletalMeshNativeLayoutParser
{
    private const int MaximumMaterials = 256;
    private const int MaximumBones = 4_096;
    private const int MaximumLods = 16;
    private const int MaximumSections = 1_024;
    private const int MaximumChunks = 4_096;
    private const int MaximumElements = 100_000_000;

    public bool TryParse(
        ReadOnlySpan<byte> exportBytes,
        long serialOffset,
        int nativeDataRelativeOffset,
        IReadOnlyList<UnrealNameEntry> names,
        bool hasVertexColors,
        out SkeletalMeshNativeLayoutEvidence? layout,
        out string refusal)
    {
        layout = null;
        refusal = string.Empty;
        try
        {
            var header = ParseHeader(exportBytes, nativeDataRelativeOffset, names);
            var failures = new List<string>();
            foreach (var useLostArkExtension in new[] { true, false })
            {
                foreach (var useLodExtension in new[] { false, true })
                {
                    try
                    {
                        layout = ParseLods(
                            exportBytes, serialOffset, header, hasVertexColors,
                            useLostArkExtension, useLodExtension);
                        return true;
                    }
                    catch (LayoutParseException exception)
                    {
                        failures.Add(
                            (useLostArkExtension ? "updated" : "legacy") +
                            (useLodExtension ? "/extended-lod" : "/standard-lod") +
                            ": " + exception.Message);
                    }
                }
            }

            refusal = string.Join(" | ", failures);
            return false;
        }
        catch (LayoutParseException exception)
        {
            refusal = exception.Message;
            return false;
        }
    }

    private static ParsedHeader ParseHeader(
        ReadOnlySpan<byte> bytes,
        int nativeStart,
        IReadOnlyList<UnrealNameEntry> names)
    {
        var reader = new SpanReader(bytes, nativeStart);
        var ranges = new List<SkeletalMeshNativeRangeEvidence>();
        var boundsStart = reader.Position;
        reader.Skip(28, "bounds");
        ranges.Add(Range(bytes, "Bounds", boundsStart, reader.Position - boundsStart, true, true));

        var materialsStart = reader.Position;
        var materialCount = reader.ReadCount("material reference", MaximumMaterials);
        var materials = new int[materialCount];
        for (var index = 0; index < materialCount; index++) materials[index] = reader.ReadInt32("material reference");
        ranges.Add(Range(bytes, "Material references", materialsStart, reader.Position - materialsStart, true, false));

        var transformStart = reader.Position;
        reader.Skip(12, "mesh origin");
        reader.Skip(12, "rotation origin");
        ranges.Add(Range(bytes, "Mesh and rotation origin", transformStart, reader.Position - transformStart, true, true));

        var skeletonStart = reader.Position;
        var boneCount = reader.ReadCount("reference bone", MaximumBones);
        var bones = new SkeletalMeshNativeBoneEvidence[boneCount];
        for (var boneIndex = 0; boneIndex < boneCount; boneIndex++)
        {
            var boneStart = reader.Position;
            var nameIndex = reader.ReadInt32("bone name index");
            var nameNumber = reader.ReadInt32("bone name number");
            if (nameIndex < 0 || nameIndex >= names.Count || nameNumber < 0)
                throw new LayoutParseException($"Bone {boneIndex} has invalid FName ({nameIndex}, {nameNumber}).");
            var name = new UnrealNameReference(nameIndex, nameNumber).Resolve(names);
            reader.ReadInt32("bone flags");
            var orientationX = reader.ReadSingle("bone orientation X");
            var orientationY = reader.ReadSingle("bone orientation Y");
            var orientationZ = reader.ReadSingle("bone orientation Z");
            var orientationW = reader.ReadSingle("bone orientation W");
            var positionX = reader.ReadSingle("bone position X");
            var positionY = reader.ReadSingle("bone position Y");
            var positionZ = reader.ReadSingle("bone position Z");
            if (!AllFinite(orientationX, orientationY, orientationZ, orientationW,
                    positionX, positionY, positionZ))
                throw new LayoutParseException($"Bone {boneIndex} has non-finite reference-pose values.");
            var childCount = reader.ReadInt32("bone child count");
            var parentIndex = reader.ReadInt32("bone parent index");
            reader.ReadInt32("bone color");
            if (childCount < 0 || childCount > boneCount || parentIndex < -1 || parentIndex >= boneCount)
                throw new LayoutParseException($"Bone {boneIndex} has invalid child/parent values {childCount}/{parentIndex}.");
            bones[boneIndex] = new SkeletalMeshNativeBoneEvidence(
                boneIndex, name, parentIndex, childCount,
                orientationX, orientationY, orientationZ, orientationW,
                positionX, positionY, positionZ,
                boneStart, Sha(bytes.Slice(boneStart, 52)));
        }
        ranges.Add(Range(bytes, "Reference skeleton", skeletonStart, reader.Position - skeletonStart, true, false));
        var skeletalDepthOffset = reader.Position;
        var skeletalDepth = reader.ReadInt32("skeletal depth");
        if (skeletalDepth < 0 || skeletalDepth > boneCount)
            throw new LayoutParseException($"Skeletal depth {skeletalDepth} is outside the reference skeleton.");
        ranges.Add(Range(bytes, "Skeletal depth", skeletalDepthOffset, 4, true, false));
        return new ParsedHeader(
            nativeStart, reader.Position, materials, bones, skeletalDepth, ranges);
    }

    private static SkeletalMeshNativeLayoutEvidence ParseLods(
        ReadOnlySpan<byte> bytes,
        long serialOffset,
        ParsedHeader header,
        bool hasVertexColors,
        bool useLostArkExtension,
        bool useLodExtension)
    {
        var reader = new SpanReader(bytes, header.EndOffset);
        var globalRanges = header.Ranges.ToList();
        int extensionVersion;
        int[] extensionValues;
        if (useLostArkExtension)
        {
            var extensionStart = reader.Position;
            extensionVersion = reader.ReadInt32("Lost Ark mesh extension version");
            if (extensionVersion != 4)
                throw new LayoutParseException(
                    $"Lost Ark mesh extension version {extensionVersion} is not the proven Western value 4.");
            var extensionCount = reader.ReadCount("Lost Ark mesh extension", 64);
            extensionValues = new int[extensionCount];
            for (var index = 0; index < extensionCount; index++)
                extensionValues[index] = reader.ReadInt32("Lost Ark mesh extension value");
            globalRanges.Add(Range(bytes, "Lost Ark mesh extension", extensionStart,
                reader.Position - extensionStart, true, false));
        }
        else
        {
            extensionVersion = 0;
            extensionValues = [];
        }

        var lodArrayOffset = reader.Position;
        var lodCount = reader.ReadCount("LOD", MaximumLods);
        if (lodCount == 0) throw new LayoutParseException("SkeletalMesh contains no native LOD models.");
        var lods = new SkeletalMeshNativeLodEvidence[lodCount];
        for (var lodIndex = 0; lodIndex < lodCount; lodIndex++)
            lods[lodIndex] = ParseLod(
                bytes, serialOffset, ref reader, lodIndex, hasVertexColors, useLodExtension);
        globalRanges.Add(Range(bytes, "LOD array count", lodArrayOffset, 4, true, false));
        foreach (var lod in lods) globalRanges.AddRange(lod.Ranges);

        var trailingStart = reader.Position;
        var trailingBytes = bytes.Length - trailingStart;
        if (trailingBytes > 0)
            globalRanges.Add(Range(bytes, "Opaque trailing native data", trailingStart, trailingBytes, false, false));
        return new SkeletalMeshNativeLayoutEvidence(
            (useLostArkExtension ? "lostark-western-868-updated" : "lostark-western-868-legacy") +
            (useLodExtension ? "-extended-lod" : "-standard-lod"),
            header.NativeStart,
            reader.Position,
            header.MaterialReferences.Count,
            header.MaterialReferences,
            header.Bones.Count,
            header.SkeletalDepth,
            extensionVersion,
            extensionValues,
            header.Bones,
            lods,
            globalRanges.OrderBy(range => range.RelativeOffset).ToArray(),
            true,
            trailingBytes == 0,
            trailingBytes == 0 ? Sha([]) : Sha(bytes[trailingStart..]));
    }

    private static SkeletalMeshNativeLodEvidence ParseLod(
        ReadOnlySpan<byte> bytes,
        long serialOffset,
        ref SpanReader reader,
        int lodIndex,
        bool hasVertexColors,
        bool useLodExtension)
    {
        var lodStart = reader.Position;
        var ranges = new List<SkeletalMeshNativeRangeEvidence>();
        var sectionsStart = reader.Position;
        var sectionCount = reader.ReadCount($"LOD {lodIndex} section", MaximumSections);
        var sections = new SkeletalMeshNativeSectionEvidence[sectionCount];
        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            var start = reader.Position;
            var material = reader.ReadInt16("section material index");
            var chunk = reader.ReadInt16("section chunk index");
            var firstIndex = reader.ReadInt32("section first index");
            var triangles = reader.ReadInt32("section triangle count");
            var flags = reader.ReadByte("section flags");
            if (material < 0 || chunk < 0 || firstIndex < 0 || triangles < 0)
                throw new LayoutParseException($"LOD {lodIndex} section {sectionIndex} contains negative indices/counts.");
            sections[sectionIndex] = new(
                sectionIndex, material, chunk, firstIndex, triangles, flags, start, reader.Position - start);
        }
        ranges.Add(Range(bytes, $"LOD {lodIndex} sections", sectionsStart,
            reader.Position - sectionsStart, true, false));

        var index = ReadIndexBuffer(bytes, ref reader, $"LOD {lodIndex} index buffer");
        ranges.Add(Range(bytes, index.BufferKind, checked((int)index.HeaderRelativeOffset),
            checked((int)(index.DataRelativeOffset + index.DataBytes - index.HeaderRelativeOffset)), true, false));

        var activeStart = reader.Position;
        var activeCount = reader.ReadCount($"LOD {lodIndex} active bone", MaximumBones);
        var activeBones = new short[activeCount];
        for (var i = 0; i < activeCount; i++) activeBones[i] = reader.ReadInt16("active bone");
        ranges.Add(Range(bytes, $"LOD {lodIndex} active bones", activeStart,
            reader.Position - activeStart, true, false));

        var chunksStart = reader.Position;
        var chunkCount = reader.ReadCount($"LOD {lodIndex} chunk", MaximumChunks);
        var chunks = new SkeletalMeshNativeChunkEvidence[chunkCount];
        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var chunkStart = reader.Position;
            var firstVertex = reader.ReadInt32("chunk first vertex");
            var rigidCount = reader.ReadCount("rigid CPU vertex", MaximumElements);
            if (rigidCount != 0)
                throw new LayoutParseException($"LOD {lodIndex} chunk {chunkIndex} has {rigidCount} CPU rigid vertices; this profile is not implemented.");
            var softCount = reader.ReadCount("soft CPU vertex", MaximumElements);
            if (softCount != 0)
                throw new LayoutParseException($"LOD {lodIndex} chunk {chunkIndex} has {softCount} CPU soft vertices; this profile is not implemented.");
            var boneMapCount = reader.ReadCount("chunk bone map", MaximumBones);
            var boneMap = new short[boneMapCount];
            for (var i = 0; i < boneMapCount; i++) boneMap[i] = reader.ReadInt16("chunk bone map item");
            var declaredRigid = reader.ReadInt32("declared rigid vertex count");
            var declaredSoft = reader.ReadInt32("declared soft vertex count");
            var maxInfluences = reader.ReadInt32("maximum influences");
            if (firstVertex < 0 || declaredRigid < 0 || declaredSoft < 0 || maxInfluences is < 0 or > 4)
                throw new LayoutParseException($"LOD {lodIndex} chunk {chunkIndex} contains invalid vertex/influence counts.");
            chunks[chunkIndex] = new(
                chunkIndex, firstVertex, rigidCount, softCount, declaredRigid, declaredSoft,
                maxInfluences, boneMap, chunkStart, reader.Position - chunkStart);
        }
        ranges.Add(Range(bytes, $"LOD {lodIndex} chunks", chunksStart,
            reader.Position - chunksStart, true, false));

        var countsStart = reader.Position;
        reader.ReadInt32("LOD size field");
        var vertexCount = reader.ReadInt32("LOD vertex count");
        if (vertexCount <= 0 || vertexCount > MaximumElements)
            throw new LayoutParseException($"LOD {lodIndex} has invalid vertex count {vertexCount}.");
        int? preRequiredBonesValue = null;
        if (useLodExtension)
        {
            preRequiredBonesValue = reader.ReadInt32("Lost Ark pre-required-bones LOD value");
            if (preRequiredBonesValue is < 0 or > 8)
                throw new LayoutParseException(
                    $"LOD {lodIndex} has invalid Lost Ark pre-required-bones value {preRequiredBonesValue}.");
        }
        var requiredCount = reader.ReadCount("required bone", MaximumBones);
        var requiredBones = new short[requiredCount];
        if (useLodExtension)
        {
            for (var requiredIndex = 0; requiredIndex < requiredCount; requiredIndex++)
                requiredBones[requiredIndex] = reader.ReadInt16("required bone");
        }
        else
        {
            var rawRequiredBones = reader.ReadBytes(requiredCount, "required bones");
            for (var requiredIndex = 0; requiredIndex < requiredCount; requiredIndex++)
                requiredBones[requiredIndex] = rawRequiredBones[requiredIndex];
        }
        ranges.Add(Range(bytes, $"LOD {lodIndex} counts and required bones", countsStart,
            reader.Position - countsStart, true, false));

        var bulkStart = reader.Position;
        var bulkFlags = reader.ReadUInt32("LOD bulk flags");
        var bulkElements = reader.ReadInt32("LOD bulk element count");
        var bulkSize = reader.ReadInt32("LOD bulk stored size");
        var bulkOffset = reader.ReadInt32("LOD bulk offset");
        if (bulkElements < 0 || bulkSize < 0 || bulkElements > MaximumElements || bulkSize > bytes.Length)
            throw new LayoutParseException(
                $"LOD {lodIndex} has invalid bulk metadata at +0x{bulkStart:X}: " +
                $"flags=0x{bulkFlags:X8}, elements={bulkElements}, size={bulkSize}, offset=0x{bulkOffset:X8}.");
        if (bulkSize > 0 && bulkOffset == serialOffset + reader.Position)
            reader.Skip(bulkSize, "inline LOD bulk payload");
        ranges.Add(Range(bytes, $"LOD {lodIndex} legacy bulk ({bulkFlags:X8})", bulkStart,
            reader.Position - bulkStart, true, false));

        var textureCoordinates = reader.ReadInt32("LOD texture coordinate count");
        if (textureCoordinates is < 1 or > 8)
            throw new LayoutParseException(
                $"LOD {lodIndex} has invalid UV-set count {textureCoordinates} at +0x{reader.Position - 4:X}; " +
                $"legacy bulk at +0x{bulkStart:X} was flags=0x{bulkFlags:X8}, elements={bulkElements}, " +
                $"size={bulkSize}, offset=0x{bulkOffset:X8}.");
        var gpuStart = reader.Position;
        var gpuUvCount = reader.ReadInt32("GPU UV-set count");
        var fullPrecision = reader.ReadInt32("full-precision UV flag");
        var packedPosition = reader.ReadInt32("packed-position flag");
        if (gpuUvCount != textureCoordinates || fullPrecision is < 0 or > 1 || packedPosition is < 0 or > 1)
            throw new LayoutParseException($"LOD {lodIndex} GPU vertex metadata is inconsistent.");
        reader.Skip(24, "GPU mesh extension/origin");
        var vertexBuffer = ReadBulkArray(bytes, ref reader, $"LOD {lodIndex} GPU vertex buffer");
        var expectedStride = checked(
            28 + gpuUvCount * (fullPrecision == 1 ? 8 : 4) + (useLodExtension ? 4 : 0));
        if (vertexBuffer.ElementCount != vertexCount || vertexBuffer.ElementSize != expectedStride)
            throw new LayoutParseException(
                $"LOD {lodIndex} GPU buffer is {vertexBuffer.ElementCount} x {vertexBuffer.ElementSize}; expected {vertexCount} x {expectedStride}.");
        ranges.Add(Range(bytes, $"LOD {lodIndex} GPU metadata", gpuStart,
            checked((int)(vertexBuffer.HeaderRelativeOffset - gpuStart)), true, false));
        ranges.Add(Range(bytes, vertexBuffer.BufferKind, checked((int)vertexBuffer.HeaderRelativeOffset),
            checked((int)(vertexBuffer.DataRelativeOffset + vertexBuffer.DataBytes - vertexBuffer.HeaderRelativeOffset)), true, true));

        SkeletalMeshNativeBufferEvidence? colorBuffer = null;
        if (hasVertexColors)
        {
            colorBuffer = ReadBulkArray(bytes, ref reader, $"LOD {lodIndex} vertex colors");
            if (colorBuffer.ElementSize != 4 || colorBuffer.ElementCount != vertexCount)
                throw new LayoutParseException($"LOD {lodIndex} vertex-color buffer does not match the vertex count.");
            ranges.Add(Range(bytes, colorBuffer.BufferKind, checked((int)colorBuffer.HeaderRelativeOffset),
                checked((int)(colorBuffer.DataRelativeOffset + colorBuffer.DataBytes - colorBuffer.HeaderRelativeOffset)), true, true));
        }

        var extraStart = reader.Position;
        var extraInfluences = reader.ReadCount("extra vertex influence set", MaximumSections);
        if (extraInfluences != 0)
            throw new LayoutParseException($"LOD {lodIndex} has {extraInfluences} extra influence sets; this profile is not implemented.");
        ranges.Add(Range(bytes, $"LOD {lodIndex} extra influences", extraStart, 4, true, false));

        var adjacency = ReadIndexBuffer(bytes, ref reader, $"LOD {lodIndex} adjacency buffer");
        ranges.Add(Range(bytes, adjacency.BufferKind, checked((int)adjacency.HeaderRelativeOffset),
            checked((int)(adjacency.DataRelativeOffset + adjacency.DataBytes - adjacency.HeaderRelativeOffset)), true, false));

        return new SkeletalMeshNativeLodEvidence(
            lodIndex, lodStart, reader.Position - lodStart, vertexCount, textureCoordinates,
            preRequiredBonesValue, useLodExtension ? 2 : 1,
            fullPrecision == 1, packedPosition == 1, useLodExtension ? 20 : 16,
            sections, chunks, index, vertexBuffer, colorBuffer, adjacency,
            activeBones, requiredBones, ranges);
    }

    private static SkeletalMeshNativeBufferEvidence ReadIndexBuffer(
        ReadOnlySpan<byte> bytes,
        ref SpanReader reader,
        string name)
    {
        var start = reader.Position;
        reader.ReadInt32(name + " revision");
        var itemSize = reader.ReadByte(name + " item size");
        if (itemSize is not 2 and not 4)
            throw new LayoutParseException($"{name} uses unsupported {itemSize}-byte indices.");
        var buffer = ReadBulkArray(bytes, ref reader, name);
        if (buffer.ElementSize != itemSize)
            throw new LayoutParseException($"{name} item-size fields disagree ({itemSize}/{buffer.ElementSize}).");
        return buffer with { HeaderRelativeOffset = start };
    }

    private static SkeletalMeshNativeBufferEvidence ReadBulkArray(
        ReadOnlySpan<byte> bytes,
        ref SpanReader reader,
        string name)
    {
        var header = reader.Position;
        var elementSize = reader.ReadInt32(name + " element size");
        var count = reader.ReadInt32(name + " element count");
        if (elementSize < 0 || elementSize > 4_096 || count < 0 || count > MaximumElements)
            throw new LayoutParseException($"{name} has invalid bulk dimensions {count} x {elementSize}.");
        var dataBytes = checked((long)elementSize * count);
        if (dataBytes > int.MaxValue)
            throw new LayoutParseException($"{name} exceeds Arkive's bounded parser limit.");
        var data = reader.Position;
        reader.Skip((int)dataBytes, name + " data");
        return new SkeletalMeshNativeBufferEvidence(
            name, header, data, elementSize, count, dataBytes,
            Sha(bytes.Slice(data, (int)dataBytes)));
    }

    private static SkeletalMeshNativeRangeEvidence Range(
        ReadOnlySpan<byte> bytes,
        string name,
        int offset,
        int size,
        bool parsed,
        bool editable) => new(name, offset, size, Sha(bytes.Slice(offset, size)), parsed, editable);

    private static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static bool AllFinite(params float[] values) => values.All(float.IsFinite);

    private sealed record ParsedHeader(
        int NativeStart,
        int EndOffset,
        IReadOnlyList<int> MaterialReferences,
        IReadOnlyList<SkeletalMeshNativeBoneEvidence> Bones,
        int SkeletalDepth,
        IReadOnlyList<SkeletalMeshNativeRangeEvidence> Ranges);

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        public SpanReader(ReadOnlySpan<byte> bytes, int position) { _bytes = bytes; Position = position; }
        public int Position { get; private set; }
        public byte ReadByte(string label) { Ensure(1, label); return _bytes[Position++]; }
        public short ReadInt16(string label) { Ensure(2, label); var value = BinaryPrimitives.ReadInt16LittleEndian(_bytes[Position..]); Position += 2; return value; }
        public int ReadInt32(string label) { Ensure(4, label); var value = BinaryPrimitives.ReadInt32LittleEndian(_bytes[Position..]); Position += 4; return value; }
        public float ReadSingle(string label) { var bits = ReadInt32(label); return BitConverter.Int32BitsToSingle(bits); }
        public uint ReadUInt32(string label) { Ensure(4, label); var value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes[Position..]); Position += 4; return value; }
        public int ReadCount(string label, int maximum) { var value = ReadInt32(label + " count"); if (value < 0 || value > maximum) throw new LayoutParseException($"Invalid {label} count {value:N0} at +0x{Position - 4:X}."); return value; }
        public ReadOnlySpan<byte> ReadBytes(int count, string label) { Ensure(count, label); var value = _bytes.Slice(Position, count); Position += count; return value; }
        public void Skip(int count, string label) { Ensure(count, label); Position += count; }
        private void Ensure(int count, string label) { if (count < 0 || Position < 0 || Position > _bytes.Length - count) throw new LayoutParseException($"{label} at +0x{Position:X} exceeds the export boundary."); }
    }

    private sealed class LayoutParseException(string message) : Exception(message);
}
