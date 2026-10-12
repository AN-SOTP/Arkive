using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Arkive.Core.StaticMeshes;

internal sealed record DecodedStaticMeshLod(
    StaticMeshLodEvidence Evidence,
    Vector3[] Positions,
    Vector3[] Normals,
    Vector4[] Tangents,
    Vector2[][] TextureCoordinates,
    byte[]? Colours,
    ushort[] Indices,
    Vector3 Minimum,
    Vector3 Maximum,
    string PositionSha256,
    string StableVertexIdSha256);

internal sealed class StaticMeshSemanticDecoder
{
    public IReadOnlyList<DecodedStaticMeshLod> Decode(
        ReadOnlySpan<byte> exportBytes,
        StaticMeshNativeLayoutEvidence layout)
    {
        var lods = new DecodedStaticMeshLod[layout.Lods.Count];
        for (var index = 0; index < lods.Length; index++)
            lods[index] = DecodeLod(exportBytes, layout.Lods[index]);
        return lods;
    }

    private static DecodedStaticMeshLod DecodeLod(
        ReadOnlySpan<byte> bytes,
        StaticMeshLodEvidence lod)
    {
        var positionBytes = SliceChecked(bytes, lod.Positions);
        var attributeBytes = SliceChecked(bytes, lod.VertexAttributes);
        Verify(positionBytes, lod.Positions.DataSha256, "STM400", "position buffer");
        Verify(attributeBytes, lod.VertexAttributes.DataSha256, "STM401", "attribute buffer");
        var positions = new Vector3[lod.VertexCount];
        var normals = new Vector3[lod.VertexCount];
        var tangents = new Vector4[lod.VertexCount];
        var uvs = Enumerable.Range(0, lod.TextureCoordinateCount)
            .Select(_ => new Vector2[lod.VertexCount]).ToArray();
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        for (var vertexIndex = 0; vertexIndex < lod.VertexCount; vertexIndex++)
        {
            var nativePosition = new Vector3(
                ReadSingle(positionBytes, vertexIndex * 12),
                ReadSingle(positionBytes, vertexIndex * 12 + 4),
                ReadSingle(positionBytes, vertexIndex * 12 + 8));
            var position = new Vector3(
                nativePosition.X * 0.01f,
                nativePosition.Z * 0.01f,
                nativePosition.Y * 0.01f);
            RequireFinite(position, "STM402", $"LOD {lod.LodIndex} vertex {vertexIndex} position");
            positions[vertexIndex] = position;
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);

            var recordOffset = vertexIndex * lod.VertexAttributes.ElementSize;
            var packedTangent = DecodePacked(attributeBytes.Slice(recordOffset, 4));
            var packedNormal = DecodePacked(attributeBytes.Slice(recordOffset + 4, 4));
            var tangent = Normalize(new Vector3(packedTangent.X, packedTangent.Z, packedTangent.Y),
                "STM403", $"LOD {lod.LodIndex} vertex {vertexIndex} tangent");
            var normal = Normalize(new Vector3(packedNormal.X, packedNormal.Z, packedNormal.Y),
                "STM404", $"LOD {lod.LodIndex} vertex {vertexIndex} normal");
            normals[vertexIndex] = normal;
            tangents[vertexIndex] = new Vector4(tangent, packedNormal.W < 0 ? -1f : 1f);

            for (var uvIndex = 0; uvIndex < lod.TextureCoordinateCount; uvIndex++)
            {
                var uvOffset = recordOffset + 8 + uvIndex * (lod.UsesFullPrecisionUvs ? 8 : 4);
                var uv = lod.UsesFullPrecisionUvs
                    ? new Vector2(ReadSingle(attributeBytes, uvOffset), ReadSingle(attributeBytes, uvOffset + 4))
                    : new Vector2(ReadHalf(attributeBytes, uvOffset), ReadHalf(attributeBytes, uvOffset + 2));
                RequireFinite(uv, "STM405", $"LOD {lod.LodIndex} vertex {vertexIndex} UV {uvIndex}");
                uvs[uvIndex][vertexIndex] = uv;
            }
        }

        var indicesBytes = SliceChecked(bytes, lod.Indices);
        Verify(indicesBytes, lod.Indices.DataSha256, "STM406", "triangle index buffer");
        var indices = new ushort[lod.Indices.ElementCount];
        for (var index = 0; index < indices.Length; index++)
        {
            indices[index] = BinaryPrimitives.ReadUInt16LittleEndian(indicesBytes[(index * 2)..]);
            if (indices[index] >= lod.VertexCount)
                Refuse("STM407", $"LOD {lod.LodIndex} index {index} references vertex {indices[index]}, outside {lod.VertexCount} vertices.");
        }

        byte[]? colours = null;
        if (lod.Colours is { } colourEvidence)
        {
            var nativeColours = SliceChecked(bytes, colourEvidence);
            Verify(nativeColours, colourEvidence.DataSha256, "STM408", "colour buffer");
            colours = new byte[nativeColours.Length];
            for (var vertexIndex = 0; vertexIndex < lod.VertexCount; vertexIndex++)
            {
                colours[vertexIndex * 4] = nativeColours[vertexIndex * 4 + 2];
                colours[vertexIndex * 4 + 1] = nativeColours[vertexIndex * 4 + 1];
                colours[vertexIndex * 4 + 2] = nativeColours[vertexIndex * 4];
                colours[vertexIndex * 4 + 3] = nativeColours[vertexIndex * 4 + 3];
            }
        }

        return new DecodedStaticMeshLod(
            lod,
            positions,
            normals,
            tangents,
            uvs,
            colours,
            indices,
            minimum,
            maximum,
            Sha(EncodeVector3(positions)),
            Sha(EncodeUInt32(Enumerable.Range(0, lod.VertexCount).Select(value => (uint)value))));
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

    private static ReadOnlySpan<byte> SliceChecked(ReadOnlySpan<byte> bytes, StaticMeshBufferEvidence evidence)
    {
        if (evidence.DataRelativeOffset < 0 || evidence.DataBytes < 0 ||
            evidence.DataRelativeOffset > bytes.Length - evidence.DataBytes ||
            evidence.DataRelativeOffset > int.MaxValue || evidence.DataBytes > int.MaxValue)
            Refuse("STM410", $"{evidence.Kind} is outside the export boundary.");
        return bytes.Slice(checked((int)evidence.DataRelativeOffset), checked((int)evidence.DataBytes));
    }

    private static Vector4 DecodePacked(ReadOnlySpan<byte> bytes) => new(
        (bytes[0] - 128f) / 127f,
        (bytes[1] - 128f) / 127f,
        (bytes[2] - 128f) / 127f,
        (bytes[3] - 128f) / 127f);

    private static Vector3 Normalize(Vector3 value, string code, string label)
    {
        var length = value.LengthSquared();
        if (!float.IsFinite(length) || length < 1e-12f) Refuse(code, label + " is zero or non-finite.");
        return Vector3.Normalize(value);
    }

    private static void RequireFinite(Vector3 value, string code, string label)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            Refuse(code, label + " is non-finite.");
    }

    private static void RequireFinite(Vector2 value, string code, string label)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            Refuse(code, label + " is non-finite.");
    }

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));

    private static float ReadHalf(ReadOnlySpan<byte> bytes, int offset) =>
        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]));

    private static void WriteSingle(Span<byte> bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], BitConverter.SingleToInt32Bits(value));

    private static void Verify(ReadOnlySpan<byte> bytes, string expected, string code, string label)
    {
        var actual = Sha(bytes);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            Refuse(code, $"The {label} no longer matches reconnaissance.");
    }

    internal static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void Refuse(string code, string message) => throw new StaticMeshRefusalException(code, message);
}
