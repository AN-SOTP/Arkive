using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Arkive.Core.Inspection;

namespace Arkive.Core.Meshes;

internal sealed record DecodedSkeletalMeshLod(
    SkeletalMeshNativeLodEvidence Evidence,
    Vector3[] Positions,
    Vector3[] Normals,
    Vector4[] Tangents,
    ushort[] Joints,
    Vector4[] Weights,
    Vector2[][] TextureCoordinates,
    byte[]? Colors,
    uint[] Indices,
    bool ReverseWinding,
    int FaceOrientationTriangleCount,
    int DegenerateTriangleCount,
    int OpposedFaceNormalTriangleCount,
    SkeletalMeshEditableBounds Bounds,
    string VertexIdSha256,
    string PositionSha256,
    string NormalSha256,
    string TangentSha256,
    string JointSha256,
    string WeightSha256,
    IReadOnlyList<string> UvSha256,
    string? ColorSha256);

internal sealed class SkeletalMeshSemanticDecoder
{
    public IReadOnlyList<DecodedSkeletalMeshLod> Decode(
        ReadOnlySpan<byte> exportBytes,
        SkeletalMeshNativeLayoutEvidence layout)
    {
        var decoded = new DecodedSkeletalMeshLod[layout.Lods.Count];
        for (var index = 0; index < layout.Lods.Count; index++)
            decoded[index] = DecodeLod(exportBytes, layout, layout.Lods[index]);
        return decoded;
    }

    private static DecodedSkeletalMeshLod DecodeLod(
        ReadOnlySpan<byte> exportBytes,
        SkeletalMeshNativeLayoutEvidence layout,
        SkeletalMeshNativeLodEvidence lod)
    {
        var wide = lod.VertexPositionOffset == 20;
        if (lod.VertexPositionOffset is not 16 and not 20)
            Refuse("SME120", $"LOD {lod.LodIndex} uses unsupported native position offset {lod.VertexPositionOffset}.");
        var expectedStride = 28 + lod.TextureCoordinateCount * (lod.UsesFullPrecisionUvs ? 8 : 4) + (wide ? 4 : 0);
        if (lod.VertexBuffer.ElementSize != expectedStride)
            Refuse("SME121", $"LOD {lod.LodIndex} vertex stride {lod.VertexBuffer.ElementSize} does not match the proven semantic layout {expectedStride}.");

        var vertexStart = CheckedRange(exportBytes, lod.VertexBuffer.DataRelativeOffset,
            lod.VertexBuffer.DataBytes, $"LOD {lod.LodIndex} GPU vertices");
        var vertexBytes = exportBytes.Slice(vertexStart, checked((int)lod.VertexBuffer.DataBytes));
        VerifyHash(vertexBytes, lod.VertexBuffer.DataSha256, "SME122", $"LOD {lod.LodIndex} GPU vertex buffer");

        var vertexChunks = BuildVertexChunkMap(lod);
        var positions = new Vector3[lod.VertexCount];
        var normals = new Vector3[lod.VertexCount];
        var tangents = new Vector4[lod.VertexCount];
        var joints = new ushort[checked(lod.VertexCount * 4)];
        var weights = new Vector4[lod.VertexCount];
        var uvs = Enumerable.Range(0, lod.TextureCoordinateCount)
            .Select(_ => new Vector2[lod.VertexCount]).ToArray();
        Span<float> normalizedWeights = stackalloc float[4];

        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        for (var vertexIndex = 0; vertexIndex < lod.VertexCount; vertexIndex++)
        {
            var record = vertexBytes.Slice(vertexIndex * lod.VertexBuffer.ElementSize,
                lod.VertexBuffer.ElementSize);
            var tangentOffset = 0;
            var normalOffset = 4;
            var boneOffset = 8;
            var weightOffset = wide ? 16 : 12;
            var uvOffset = lod.VertexPositionOffset + 12;

            var tangentPacked = DecodePackedNormal(record.Slice(tangentOffset, 4));
            var normalPacked = DecodePackedNormal(record.Slice(normalOffset, 4));
            var tangentDirection = TransformDirection(new Vector3(
                tangentPacked.X, tangentPacked.Y, tangentPacked.Z));
            var normalDirection = TransformDirection(new Vector3(
                normalPacked.X, normalPacked.Y, normalPacked.Z));
            if (!TryNormalize(tangentDirection, out tangentDirection) ||
                !TryNormalize(normalDirection, out normalDirection))
                Refuse("SME123", $"LOD {lod.LodIndex} vertex {vertexIndex} has a zero or non-finite tangent frame.");
            normals[vertexIndex] = normalDirection;
            tangents[vertexIndex] = new Vector4(
                tangentDirection, normalPacked.W < 0 ? -1f : 1f);

            var nativePosition = new Vector3(
                ReadSingle(record, lod.VertexPositionOffset),
                ReadSingle(record, lod.VertexPositionOffset + 4),
                ReadSingle(record, lod.VertexPositionOffset + 8));
            if (!IsFinite(nativePosition))
                Refuse("SME124", $"LOD {lod.LodIndex} vertex {vertexIndex} has a non-finite position.");
            var position = TransformPosition(nativePosition);
            positions[vertexIndex] = position;
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);

            var chunk = lod.Chunks[vertexChunks[vertexIndex]];
            var weightSum = 0;
            normalizedWeights.Clear();
            for (var influence = 0; influence < 4; influence++)
            {
                var nativeWeight = record[weightOffset + influence];
                weightSum += nativeWeight;
                if (nativeWeight == 0)
                {
                    joints[vertexIndex * 4 + influence] = 0;
                    continue;
                }
                var localBone = wide
                    ? BinaryPrimitives.ReadUInt16LittleEndian(record[(boneOffset + influence * 2)..])
                    : record[boneOffset + influence];
                if (localBone >= chunk.BoneMap.Count)
                    Refuse("SME125", $"LOD {lod.LodIndex} vertex {vertexIndex} influence {influence} references local bone {localBone}, outside chunk {chunk.ChunkIndex}'s {chunk.BoneMap.Count}-bone map.");
                var referenceBone = chunk.BoneMap[localBone];
                if (referenceBone < 0 || referenceBone >= layout.BoneCount)
                    Refuse("SME126", $"LOD {lod.LodIndex} vertex {vertexIndex} resolves to invalid reference bone {referenceBone}.");
                joints[vertexIndex * 4 + influence] = checked((ushort)referenceBone);
                normalizedWeights[influence] = nativeWeight;
            }
            if (weightSum <= 0)
                Refuse("SME127", $"LOD {lod.LodIndex} vertex {vertexIndex} has no non-zero skin weight.");
            weights[vertexIndex] = new Vector4(
                normalizedWeights[0] / weightSum,
                normalizedWeights[1] / weightSum,
                normalizedWeights[2] / weightSum,
                normalizedWeights[3] / weightSum);

            for (var uvIndex = 0; uvIndex < lod.TextureCoordinateCount; uvIndex++)
            {
                var offset = uvOffset + uvIndex * (lod.UsesFullPrecisionUvs ? 8 : 4);
                var uv = lod.UsesFullPrecisionUvs
                    ? new Vector2(ReadSingle(record, offset), ReadSingle(record, offset + 4))
                    : new Vector2(ReadHalf(record, offset), ReadHalf(record, offset + 2));
                if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
                    Refuse("SME128", $"LOD {lod.LodIndex} vertex {vertexIndex} UV {uvIndex} is non-finite.");
                uvs[uvIndex][vertexIndex] = uv;
            }
        }

        var indices = DecodeIndices(exportBytes, lod);
        var orientation = ValidateFaceOrientation(indices, positions, normals, lod.LodIndex);
        // Lost Ark's native index order is already the order required after the
        // Western UE3 -> glTF coordinate conversion. Reversing it makes every
        // supplied vertex normal point behind its face in Blender.
        const bool reverseWinding = false;
        var colors = DecodeColors(exportBytes, lod);
        return new DecodedSkeletalMeshLod(
            lod, positions, normals, tangents, joints, weights, uvs, colors, indices,
            reverseWinding, orientation.CheckedTriangles, orientation.DegenerateTriangles,
            orientation.OpposedTriangles,
            new SkeletalMeshEditableBounds(minimum.X, minimum.Y, minimum.Z,
                maximum.X, maximum.Y, maximum.Z),
            Sha(EncodeUInt32(Enumerable.Range(0, lod.VertexCount).Select(value => (uint)value))),
            Sha(EncodeVector3(positions)), Sha(EncodeVector3(normals)),
            Sha(EncodeVector4(tangents)), Sha(EncodeUInt16(joints)),
            Sha(EncodeVector4(weights)), uvs.Select(channel => Sha(EncodeVector2(channel))).ToArray(),
            colors is null ? null : Sha(colors));
    }

    private static FaceOrientationEvidence ValidateFaceOrientation(
        IReadOnlyList<uint> indices,
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        int lodIndex)
    {
        var checkedTriangles = 0;
        var degenerateTriangles = 0;
        var opposedTriangles = 0;
        var checkedArea = 0d;
        var opposedArea = 0d;
        for (var index = 0; index + 2 < indices.Count; index += 3)
        {
            var a = checked((int)indices[index]);
            var b = checked((int)indices[index + 1]);
            var c = checked((int)indices[index + 2]);
            var face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            var faceLengthSquared = face.LengthSquared();
            if (!float.IsFinite(faceLengthSquared) || faceLengthSquared < 1e-20f)
            {
                degenerateTriangles++;
                continue;
            }
            var faceLength = MathF.Sqrt(faceLengthSquared);
            face /= faceLength;
            var vertexNormal = normals[a] + normals[b] + normals[c];
            if (!TryNormalize(vertexNormal, out vertexNormal))
            {
                degenerateTriangles++;
                continue;
            }
            checkedTriangles++;
            checkedArea += faceLength;
            if (Vector3.Dot(face, vertexNormal) < 0f)
            {
                opposedTriangles++;
                opposedArea += faceLength;
            }
        }
        if (checkedTriangles == 0)
            Refuse("SME146", $"LOD {lodIndex} contains no non-degenerate triangle with a usable normal.");
        // Shipped reduced LODs contain clusters of sub-millimetre triangles whose faces
        // no longer follow their inherited normals. A real winding error covers the surface,
        // so only refuse when the opposed faces also cover a meaningful share of its area.
        var maximumOpposed = Math.Max(2, (int)Math.Ceiling(checkedTriangles * 0.005));
        var opposedAreaShare = opposedArea / checkedArea;
        if (opposedTriangles > maximumOpposed && opposedAreaShare > 0.005)
            Refuse("SME147",
                $"LOD {lodIndex} face winding disagrees with decoded normals for " +
                $"{opposedTriangles:N0}/{checkedTriangles:N0} triangles covering {opposedAreaShare:P2} of its area; " +
                $"maximum tolerated is {maximumOpposed:N0} triangles or 0.50% of the area.");
        return new FaceOrientationEvidence(
            checkedTriangles, degenerateTriangles, opposedTriangles);
    }

    private static int[] BuildVertexChunkMap(SkeletalMeshNativeLodEvidence lod)
    {
        var map = Enumerable.Repeat(-1, lod.VertexCount).ToArray();
        foreach (var chunk in lod.Chunks)
        {
            var count = checked(chunk.DeclaredRigidVertexCount + chunk.DeclaredSoftVertexCount);
            if (count <= 0 || chunk.FirstVertex < 0 || chunk.FirstVertex > lod.VertexCount - count)
                Refuse("SME130", $"LOD {lod.LodIndex} chunk {chunk.ChunkIndex} has invalid vertex range {chunk.FirstVertex}+{count}.");
            for (var vertex = chunk.FirstVertex; vertex < chunk.FirstVertex + count; vertex++)
            {
                if (map[vertex] >= 0)
                    Refuse("SME131", $"LOD {lod.LodIndex} vertex {vertex} belongs to overlapping chunks.");
                map[vertex] = chunk.ChunkIndex;
            }
        }
        var missing = Array.IndexOf(map, -1);
        if (missing >= 0)
            Refuse("SME132", $"LOD {lod.LodIndex} vertex {missing} is not covered by a native chunk.");
        return map;
    }

    private static uint[] DecodeIndices(ReadOnlySpan<byte> exportBytes, SkeletalMeshNativeLodEvidence lod)
    {
        var start = CheckedRange(exportBytes, lod.IndexBuffer.DataRelativeOffset,
            lod.IndexBuffer.DataBytes, $"LOD {lod.LodIndex} indices");
        var bytes = exportBytes.Slice(start, checked((int)lod.IndexBuffer.DataBytes));
        VerifyHash(bytes, lod.IndexBuffer.DataSha256, "SME140", $"LOD {lod.LodIndex} index buffer");
        var indices = new uint[lod.IndexBuffer.ElementCount];
        for (var index = 0; index < indices.Length; index++)
        {
            indices[index] = lod.IndexBuffer.ElementSize switch
            {
                2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes[(index * 2)..]),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes[(index * 4)..]),
                _ => throw new SkeletalMeshEditableExportRefusalException(
                    "SME141", $"LOD {lod.LodIndex} uses {lod.IndexBuffer.ElementSize}-byte indices."),
            };
            if (indices[index] >= lod.VertexCount)
                Refuse("SME142", $"LOD {lod.LodIndex} index {index} references vertex {indices[index]}, outside {lod.VertexCount} vertices.");
        }
        foreach (var section in lod.Sections)
        {
            var count = checked(section.TriangleCount * 3);
            if (section.FirstIndex < 0 || section.FirstIndex > indices.Length - count)
                Refuse("SME143", $"LOD {lod.LodIndex} section {section.SectionIndex} exceeds the index buffer.");
        }
        var sectionCoverage = new bool[indices.Length];
        foreach (var section in lod.Sections)
        {
            var count = checked(section.TriangleCount * 3);
            for (var index = section.FirstIndex; index < section.FirstIndex + count; index++)
            {
                if (sectionCoverage[index])
                    Refuse("SME144", $"LOD {lod.LodIndex} index {index} belongs to overlapping sections.");
                sectionCoverage[index] = true;
            }
        }
        var uncovered = Array.IndexOf(sectionCoverage, false);
        if (uncovered >= 0)
            Refuse("SME145", $"LOD {lod.LodIndex} native index {uncovered} is not covered by a section.");
        return indices;
    }

    private static byte[]? DecodeColors(ReadOnlySpan<byte> exportBytes, SkeletalMeshNativeLodEvidence lod)
    {
        if (lod.VertexColorBuffer is null) return null;
        var evidence = lod.VertexColorBuffer;
        var start = CheckedRange(exportBytes, evidence.DataRelativeOffset, evidence.DataBytes,
            $"LOD {lod.LodIndex} vertex colors");
        var source = exportBytes.Slice(start, checked((int)evidence.DataBytes));
        VerifyHash(source, evidence.DataSha256, "SME150", $"LOD {lod.LodIndex} vertex-color buffer");
        var colors = new byte[source.Length];
        for (var index = 0; index < lod.VertexCount; index++)
        {
            // UE3 FColor is serialized BGRA; glTF COLOR_0 is RGBA.
            colors[index * 4] = source[index * 4 + 2];
            colors[index * 4 + 1] = source[index * 4 + 1];
            colors[index * 4 + 2] = source[index * 4];
            colors[index * 4 + 3] = source[index * 4 + 3];
        }
        return colors;
    }

    internal static byte[] EncodeVector2(IReadOnlyList<Vector2> values)
    {
        var bytes = new byte[checked(values.Count * 8)];
        for (var index = 0; index < values.Count; index++)
        {
            WriteSingle(bytes, index * 8, values[index].X);
            WriteSingle(bytes, index * 8 + 4, values[index].Y);
        }
        return bytes;
    }

    internal static byte[] EncodeVector3(IReadOnlyList<Vector3> values)
    {
        var bytes = new byte[checked(values.Count * 12)];
        for (var index = 0; index < values.Count; index++)
        {
            WriteSingle(bytes, index * 12, values[index].X);
            WriteSingle(bytes, index * 12 + 4, values[index].Y);
            WriteSingle(bytes, index * 12 + 8, values[index].Z);
        }
        return bytes;
    }

    internal static byte[] EncodeVector4(IReadOnlyList<Vector4> values)
    {
        var bytes = new byte[checked(values.Count * 16)];
        for (var index = 0; index < values.Count; index++)
        {
            WriteSingle(bytes, index * 16, values[index].X);
            WriteSingle(bytes, index * 16 + 4, values[index].Y);
            WriteSingle(bytes, index * 16 + 8, values[index].Z);
            WriteSingle(bytes, index * 16 + 12, values[index].W);
        }
        return bytes;
    }

    internal static byte[] EncodeUInt16(IReadOnlyList<ushort> values)
    {
        var bytes = new byte[checked(values.Count * 2)];
        for (var index = 0; index < values.Count; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 2), values[index]);
        return bytes;
    }

    internal static byte[] EncodeUInt32(IEnumerable<uint> values)
    {
        var array = values as uint[] ?? values.ToArray();
        var bytes = new byte[checked(array.Length * 4)];
        for (var index = 0; index < array.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), array[index]);
        return bytes;
    }

    private static Vector4 DecodePackedNormal(ReadOnlySpan<byte> bytes) => new(
        (bytes[0] - 128f) / 127f,
        (bytes[1] - 128f) / 127f,
        (bytes[2] - 128f) / 127f,
        (bytes[3] - 128f) / 127f);

    private static Vector3 TransformPosition(Vector3 value) =>
        new(value.X * 0.01f, value.Z * 0.01f, value.Y * 0.01f);

    private static Vector3 TransformDirection(Vector3 value) => new(value.X, value.Z, value.Y);

    private static bool TryNormalize(Vector3 value, out Vector3 normalized)
    {
        var lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-12f)
        {
            normalized = default;
            return false;
        }
        normalized = value / MathF.Sqrt(lengthSquared);
        return IsFinite(normalized);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));

    private static float ReadHalf(ReadOnlySpan<byte> bytes, int offset) =>
        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]));

    private static void WriteSingle(Span<byte> bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], BitConverter.SingleToInt32Bits(value));

    private static int CheckedRange(ReadOnlySpan<byte> bytes, long offset, long size, string name)
    {
        if (offset < 0 || size < 0 || offset > bytes.Length - size || offset > int.MaxValue || size > int.MaxValue)
            Refuse("SME110", $"{name} is outside the export boundary.");
        return checked((int)offset);
    }

    private static void VerifyHash(ReadOnlySpan<byte> bytes, string expected, string code, string name)
    {
        var actual = Sha(bytes);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            Refuse(code, $"{name} no longer matches reconnaissance ({actual} != {expected}).");
    }

    internal static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void Refuse(string code, string message) =>
        throw new SkeletalMeshEditableExportRefusalException(code, message);

    private sealed record FaceOrientationEvidence(
        int CheckedTriangles,
        int DegenerateTriangles,
        int OpposedTriangles);
}
