using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Arkive.Core.StaticMeshes;

/// <summary>
/// Bounded parser for the Western Lost Ark UE3 StaticMesh3 payload written by
/// package version 868. It intentionally describes render streams and retains
/// collision/unknown regions as source-owned bytes.
/// </summary>
public sealed class StaticMeshNativeLayoutParser
{
    private const int MaximumLods = 16;
    private const int MaximumSections = 4_096;
    private const int MaximumElements = 100_000_000;

    public bool TryParse(
        ReadOnlySpan<byte> exportBytes,
        long serialOffset,
        int nativeDataRelativeOffset,
        out StaticMeshNativeLayoutEvidence? layout,
        out string refusal)
    {
        layout = null;
        refusal = string.Empty;
        try
        {
            layout = Parse(exportBytes, serialOffset, nativeDataRelativeOffset);
            return true;
        }
        catch (StaticMeshLayoutException exception)
        {
            refusal = exception.Message;
            return false;
        }
        catch (OverflowException exception)
        {
            refusal = "A StaticMesh native count overflowed Arkive's bounded parser: " + exception.Message;
            return false;
        }
    }

    private static StaticMeshNativeLayoutEvidence Parse(
        ReadOnlySpan<byte> bytes,
        long serialOffset,
        int nativeStart)
    {
        var reader = new SpanReader(bytes, nativeStart);
        var prefixStart = reader.Position;

        reader.Skip(28, "bounds");
        var bodySetup = reader.ReadInt32("BodySetup reference");
        reader.Skip(24, "quantized kDOP bounds");
        var collisionNodes = ReadBulkArray(bytes, ref reader, "kDOP nodes", expectedElementSize: 6);
        var collisionTriangles = ReadBulkArray(bytes, ref reader, "kDOP triangles", expectedElementSize: 8);
        var internalVersion = reader.ReadInt32("StaticMesh internal version");
        if (internalVersion is < 16 or > 32)
            throw new StaticMeshLayoutException(
                $"Unexpected StaticMesh internal version {internalVersion}; Western version 868 assets use 18.");

        var extraLodFlag = reader.ReadInt32("extra LOD flag");
        if (extraLodFlag is not 0 and not 1)
            throw new StaticMeshLayoutException($"Invalid extra LOD flag {extraLodFlag}.");
        if (extraLodFlag == 1)
            _ = ParseLod(bytes, serialOffset, ref reader, -1);

        var unknownArrayCount = reader.ReadCount("StaticMesh extra metadata", 1_000_000);
        reader.Skip(checked(unknownArrayCount * 7), "StaticMesh extra metadata values");
        reader.ReadInt32("StaticMesh extra metadata scalar");
        reader.ReadInt32("StaticMesh version 859 scalar");

        var lodCount = reader.ReadCount("StaticMesh LOD", MaximumLods);
        if (lodCount <= 0)
            throw new StaticMeshLayoutException("The StaticMesh contains no render LOD models.");
        var prefixEnd = reader.Position;
        var lods = new StaticMeshLodEvidence[lodCount];
        for (var lodIndex = 0; lodIndex < lodCount; lodIndex++)
            lods[lodIndex] = ParseLod(bytes, serialOffset, ref reader, lodIndex);

        var trailingBytes = bytes.Length - reader.Position;
        return new StaticMeshNativeLayoutEvidence(
            "lostark-western-staticmesh3-868-v18",
            nativeStart,
            reader.Position,
            internalVersion,
            bodySetup,
            collisionNodes.ElementCount,
            collisionTriangles.ElementCount,
            true,
            extraLodFlag == 1,
            lods,
            Sha(bytes.Slice(prefixStart, prefixEnd - prefixStart)),
            Sha(bytes[reader.Position..]),
            trailingBytes,
            trailingBytes == 0);
    }

    private static StaticMeshLodEvidence ParseLod(
        ReadOnlySpan<byte> bytes,
        long serialOffset,
        ref SpanReader reader,
        int lodIndex)
    {
        var start = reader.Position;
        var legacyBulkStart = reader.Position;
        SkipByteBulkData(ref reader, serialOffset, "LOD legacy bulk");
        var legacyBulkBytes = reader.Position - legacyBulkStart;

        var sectionCount = reader.ReadCount("StaticMesh section", MaximumSections);
        var sections = new StaticMeshSectionEvidence[sectionCount];
        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            var sectionStart = reader.Position;
            var materialReference = reader.ReadInt32("section material reference");
            reader.ReadInt32("section collision flag 0");
            reader.ReadInt32("section collision flag 1");
            var shadow = reader.ReadInt32("section shadow flag");
            var firstIndex = reader.ReadInt32("section first index");
            var triangleCount = reader.ReadInt32("section triangle count");
            var minimumVertex = reader.ReadInt32("section minimum vertex");
            var maximumVertex = reader.ReadInt32("section maximum vertex");
            reader.ReadInt32("section ordinal");
            var pairedArrayCount = reader.ReadCount("section paired metadata", MaximumElements);
            reader.Skip(checked(pairedArrayCount * 8), "section paired metadata values");
            var hasPs3Data = reader.ReadByte("section PS3 data flag");
            if (hasPs3Data != 0)
                throw new StaticMeshLayoutException(
                    $"LOD {lodIndex} section {sectionIndex} contains unsupported platform data.");
            if (firstIndex < 0 || triangleCount < 0 || minimumVertex < 0 || maximumVertex < minimumVertex)
                throw new StaticMeshLayoutException(
                    $"LOD {lodIndex} section {sectionIndex} has invalid index/vertex bounds.");
            sections[sectionIndex] = new StaticMeshSectionEvidence(
                sectionIndex,
                materialReference,
                $"ObjectRef({materialReference})",
                firstIndex,
                triangleCount,
                minimumVertex,
                maximumVertex,
                shadow,
                reader.Position - sectionStart,
                sectionStart);
        }

        var vertexSize = reader.ReadInt32("position stream item size");
        var vertexCount = reader.ReadInt32("position stream vertex count");
        if (vertexSize != 12 || vertexCount <= 0 || vertexCount > MaximumElements)
            throw new StaticMeshLayoutException(
                $"LOD {lodIndex} position stream is {vertexCount:N0} x {vertexSize}; expected FVector positions.");
        var positions = ReadBulkArray(bytes, ref reader, "positions", expectedElementSize: vertexSize);
        RequireCount(positions, vertexCount, $"LOD {lodIndex} positions");

        var textureCoordinates = reader.ReadInt32("UV stream channel count");
        var attributeItemSize = reader.ReadInt32("UV stream item size");
        var attributeVertexCount = reader.ReadInt32("UV stream vertex count");
        var fullPrecisionValue = reader.ReadInt32("full-precision UV flag");
        if (textureCoordinates is < 1 or > 8 || fullPrecisionValue is not 0 and not 1)
            throw new StaticMeshLayoutException(
                $"LOD {lodIndex} has unsupported UV metadata ({textureCoordinates} channels, flag {fullPrecisionValue}).");
        var expectedAttributeSize = checked(8 + textureCoordinates * (fullPrecisionValue == 1 ? 8 : 4));
        if (attributeItemSize != expectedAttributeSize || attributeVertexCount != vertexCount)
            throw new StaticMeshLayoutException(
                $"LOD {lodIndex} attribute stream metadata is inconsistent: " +
                $"{attributeVertexCount:N0} x {attributeItemSize}, expected {vertexCount:N0} x {expectedAttributeSize}.");
        var attributes = ReadBulkArray(bytes, ref reader, "vertex attributes", expectedElementSize: attributeItemSize);
        RequireCount(attributes, vertexCount, $"LOD {lodIndex} vertex attributes");

        var colourItemSize = reader.ReadInt32("colour stream item size");
        var colourVertexCount = reader.ReadInt32("colour stream vertex count");
        StaticMeshBufferEvidence? colours = null;
        if (colourVertexCount != 0)
        {
            if (colourItemSize != 4 || colourVertexCount != vertexCount)
                throw new StaticMeshLayoutException(
                    $"LOD {lodIndex} colour stream is {colourVertexCount:N0} x {colourItemSize}, expected {vertexCount:N0} x 4.");
            colours = ReadBulkArray(bytes, ref reader, "vertex colours", expectedElementSize: 4);
            RequireCount(colours, vertexCount, $"LOD {lodIndex} colours");
        }
        else if (colourItemSize is not 0 and not 4)
        {
            throw new StaticMeshLayoutException(
                $"LOD {lodIndex} empty colour stream has unexpected item size {colourItemSize}.");
        }

        var repeatedVertexCount = reader.ReadInt32("LOD vertex count");
        if (repeatedVertexCount != vertexCount)
            throw new StaticMeshLayoutException(
                $"LOD {lodIndex} vertex counts disagree ({vertexCount:N0}/{repeatedVertexCount:N0}).");

        var indices = ReadBulkArray(bytes, ref reader, "triangle indices", expectedElementSize: 2);
        var wireframe = ReadBulkArray(bytes, ref reader, "wireframe indices", expectedElementSize: 2);
        var extra = ReadBulkArray(bytes, ref reader, "extra indices", expectedElementSize: 2);
        if (indices.ElementCount == 0 || indices.ElementCount % 3 != 0)
            throw new StaticMeshLayoutException(
                $"LOD {lodIndex} triangle index count {indices.ElementCount:N0} is invalid.");
        foreach (var section in sections)
        {
            var endIndex = checked(section.FirstIndex + section.TriangleCount * 3);
            if (endIndex > indices.ElementCount || section.MaximumVertexIndex >= vertexCount)
                throw new StaticMeshLayoutException(
                    $"LOD {lodIndex} section {section.SectionIndex} exceeds the decoded buffers.");
        }

        return new StaticMeshLodEvidence(
            lodIndex,
            start,
            reader.Position - start,
            vertexCount,
            textureCoordinates,
            fullPrecisionValue == 1,
            sections,
            positions,
            attributes,
            colours,
            indices,
            wireframe,
            extra,
            legacyBulkStart,
            legacyBulkBytes,
            Sha(bytes.Slice(start, reader.Position - start)));
    }

    private static void SkipByteBulkData(ref SpanReader reader, long serialOffset, string label)
    {
        var flags = reader.ReadUInt32(label + " flags");
        var elements = reader.ReadInt32(label + " element count");
        var storedBytes = reader.ReadInt32(label + " stored bytes");
        var offset = reader.ReadInt32(label + " offset");
        if (elements < 0 || storedBytes < 0 || storedBytes > MaximumElements)
            throw new StaticMeshLayoutException(
                $"{label} has invalid dimensions flags=0x{flags:X8}, elements={elements}, bytes={storedBytes}.");
        if (storedBytes > 0 && offset == serialOffset + reader.Position)
            reader.Skip(storedBytes, label + " inline payload");
    }

    private static StaticMeshBufferEvidence ReadBulkArray(
        ReadOnlySpan<byte> bytes,
        ref SpanReader reader,
        string kind,
        int expectedElementSize)
    {
        var header = reader.Position;
        var elementSize = reader.ReadInt32(kind + " element size");
        var elementCount = reader.ReadInt32(kind + " element count");
        if (elementSize != expectedElementSize || elementCount < 0 || elementCount > MaximumElements)
            throw new StaticMeshLayoutException(
                $"{kind} has invalid bulk dimensions {elementCount:N0} x {elementSize}; expected item size {expectedElementSize}.");
        var dataBytes = checked((long)elementSize * elementCount);
        if (dataBytes > int.MaxValue)
            throw new StaticMeshLayoutException($"{kind} exceeds Arkive's bounded parser limit.");
        var data = reader.Position;
        reader.Skip((int)dataBytes, kind + " data");
        return new StaticMeshBufferEvidence(
            kind,
            header,
            data,
            elementSize,
            elementCount,
            dataBytes,
            Sha(bytes.Slice(data, (int)dataBytes)));
    }

    private static void RequireCount(StaticMeshBufferEvidence buffer, int expected, string label)
    {
        if (buffer.ElementCount != expected)
            throw new StaticMeshLayoutException(
                $"{label} contains {buffer.ElementCount:N0} items; expected {expected:N0}.");
    }

    private static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _bytes;

        public SpanReader(ReadOnlySpan<byte> bytes, int position)
        {
            _bytes = bytes;
            Position = position;
        }

        public int Position { get; private set; }

        public byte ReadByte(string label)
        {
            Ensure(1, label);
            return _bytes[Position++];
        }

        public int ReadInt32(string label)
        {
            Ensure(4, label);
            var value = BinaryPrimitives.ReadInt32LittleEndian(_bytes[Position..]);
            Position += 4;
            return value;
        }

        public uint ReadUInt32(string label)
        {
            Ensure(4, label);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes[Position..]);
            Position += 4;
            return value;
        }

        public int ReadCount(string label, int maximum)
        {
            var value = ReadInt32(label + " count");
            if (value < 0 || value > maximum)
                throw new StaticMeshLayoutException(
                    $"Invalid {label} count {value:N0} at +0x{Position - 4:X}.");
            return value;
        }

        public void Skip(int count, string label)
        {
            Ensure(count, label);
            Position += count;
        }

        private void Ensure(int count, string label)
        {
            if (count < 0 || Position < 0 || Position > _bytes.Length - count)
                throw new StaticMeshLayoutException(
                    $"{label} at +0x{Position:X} exceeds the StaticMesh export boundary.");
        }
    }

    private sealed class StaticMeshLayoutException(string message) : Exception(message);
}
