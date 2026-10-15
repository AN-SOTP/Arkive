namespace Arkive.SmokeTests;

internal static class SmokeTestSupport
{
    internal static void WriteSkeletalMeshJointCollapseEdit(string sourcePath, string destinationPath)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        using var document = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var root = document.RootElement;
        var accessorIndex = root.GetProperty("meshes")[0].GetProperty("primitives")[0]
            .GetProperty("attributes").GetProperty("JOINTS_0").GetInt32();
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var binaryStart = checked(20 + jsonLength + 8);
        var offset = checked(binaryStart +
            (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) +
            (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0));
        var componentBytes = accessor.GetProperty("componentType").GetInt32() switch
        {
            5121 => 1,
            5123 => 2,
            _ => throw new InvalidOperationException("Synthetic mesh used an unsupported joint component type."),
        };
        var byteCount = checked(accessor.GetProperty("count").GetInt32() * 4 * componentBytes);
        Array.Clear(bytes, offset, byteCount);
        File.WriteAllBytes(destinationPath, bytes);
    }

    internal static void WriteUnrealString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        writer.Write(bytes.Length + 1);
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    internal static void NudgeFirstGlbPosition(string sourcePath, string outputPath, float deltaMetres)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) == 0x46546C67,
            "editable StaticMesh was not a GLB");
        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4));
        using var json = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var root = json.RootElement;
        var primitive = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
        var accessorIndex = primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var viewIndex = accessor.GetProperty("bufferView").GetInt32();
        var view = root.GetProperty("bufferViews")[viewIndex];
        var accessorOffset = accessor.TryGetProperty("byteOffset", out var accessorOffsetValue)
            ? accessorOffsetValue.GetInt32() : 0;
        var viewOffset = view.TryGetProperty("byteOffset", out var viewOffsetValue)
            ? viewOffsetValue.GetInt32() : 0;
        var binHeader = 20 + jsonLength;
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(binHeader + 4, 4)) == 0x004E4942,
            "editable StaticMesh GLB had no BIN chunk");
        var positionOffset = binHeader + 8 + viewOffset + accessorOffset;
        var value = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(positionOffset, 4)));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(positionOffset, 4),
            BitConverter.SingleToInt32Bits(value + deltaMetres));
        File.WriteAllBytes(outputPath, bytes);
    }

    internal static void RewriteAsSingleCompressedPackage(string path)
    {
        var source = File.ReadAllBytes(path);
        var summary = new UpkPackageReader().Read(path);
        Assert(summary.CompressedChunks.Count == 0,
            "synthetic compression wrapper requires an uncompressed source package");
        var logicalOffset = checked((uint)summary.ParsedSummarySize);
        var logicalPayload = source.AsSpan(checked((int)logicalOffset)).ToArray();
        var compressed = Arkive.Core.Compression.Lz4BlockEncoder.Encode(logicalPayload);
        var physicalOffset = checked((uint)(summary.ParsedSummarySize + 20));
        var physicalBytes = checked((uint)(24 + compressed.Length));

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(source.AsSpan(0, checked((int)summary.CompressedChunkCountOffset)));
        writer.Write(1);
        writer.Write(logicalOffset);
        writer.Write(checked((uint)logicalPayload.Length));
        writer.Write(physicalOffset);
        writer.Write(physicalBytes);
        writer.Write(0u);
        writer.Write(source.AsSpan(
            checked((int)summary.PackageSourceOffset),
            checked((int)(summary.ParsedSummarySize - summary.PackageSourceOffset))));
        while (output.Position < physicalOffset) writer.Write((byte)0);
        writer.Write(PackageConstants.UnrealPackageTag);
        writer.Write(0x20000);
        writer.Write(compressed.Length);
        writer.Write(logicalPayload.Length);
        writer.Write(compressed.Length);
        writer.Write(logicalPayload.Length);
        writer.Write(compressed);
        writer.Flush();
        File.WriteAllBytes(path, output.ToArray());
    }

    internal static void WriteSkeletalMeshMaterialRename(string sourcePath, string destinationPath)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        var json = bytes.AsSpan(20, jsonLength);
        var original = Encoding.UTF8.GetBytes("material_slot_00");
        var replacement = Encoding.UTF8.GetBytes("xaterial_slot_00");
        var offset = json.IndexOf(original);
        if (offset < 0) throw new InvalidOperationException("Synthetic GLB material name was not found.");
        replacement.CopyTo(json[offset..]);
        File.WriteAllBytes(destinationPath, bytes);
    }

    internal static void AppendDuplicateTriangleToFirstPrimitive(string sourcePath, string outputPath)
    {
        var source = File.ReadAllBytes(sourcePath);
        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(12, 4));
        var binHeader = 20 + jsonLength;
        var binLength = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(binHeader, 4));
        var bin = source.AsSpan(binHeader + 8, binLength).ToArray();
        var root = JsonNode.Parse(Encoding.UTF8.GetString(source, 20, jsonLength))!.AsObject();
        var primitive = root["meshes"]!.AsArray()[0]!["primitives"]!.AsArray()[0]!.AsObject();
        var originalAccessorIndex = primitive["indices"]!.GetValue<int>();
        var accessors = root["accessors"]!.AsArray();
        var views = root["bufferViews"]!.AsArray();
        var originalAccessor = accessors[originalAccessorIndex]!.AsObject();
        Assert(originalAccessor["componentType"]!.GetValue<int>() == 5123,
            "StaticMesh editable indices were not uint16");
        var originalView = views[originalAccessor["bufferView"]!.GetValue<int>()]!.AsObject();
        var originalOffset = (originalView["byteOffset"]?.GetValue<int>() ?? 0) +
                             (originalAccessor["byteOffset"]?.GetValue<int>() ?? 0);
        var originalCount = originalAccessor["count"]!.GetValue<int>();
        var originalBytes = bin.AsSpan(originalOffset, checked(originalCount * 2)).ToArray();
        var appendedOffset = (bin.Length + 3) & ~3;
        var replacementIndices = new byte[originalBytes.Length + 6];
        originalBytes.CopyTo(replacementIndices, 0);
        originalBytes.AsSpan(0, 6).CopyTo(replacementIndices.AsSpan(originalBytes.Length));
        var rebuiltBin = new byte[appendedOffset + replacementIndices.Length];
        bin.CopyTo(rebuiltBin, 0);
        replacementIndices.CopyTo(rebuiltBin, appendedOffset);
        var newView = new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = appendedOffset,
            ["byteLength"] = replacementIndices.Length,
            ["target"] = 34963,
        };
        var newViewIndex = views.Count;
        views.Add(newView);
        var newAccessor = originalAccessor.DeepClone().AsObject();
        newAccessor["bufferView"] = newViewIndex;
        newAccessor["byteOffset"] = 0;
        newAccessor["count"] = originalCount + 3;
        var newAccessorIndex = accessors.Count;
        accessors.Add(newAccessor);
        primitive["indices"] = newAccessorIndex;
        root["buffers"]!.AsArray()[0]!["byteLength"] = rebuiltBin.Length;
        var jsonBytes = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
        }));
        var paddedJsonLength = (jsonBytes.Length + 3) & ~3;
        var paddedBinLength = (rebuiltBin.Length + 3) & ~3;
        var output = new byte[20 + paddedJsonLength + 8 + paddedBinLength];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, 4), 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8, 4), output.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12, 4), paddedJsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16, 4), 0x4E4F534A);
        output.AsSpan(20, paddedJsonLength).Fill(0x20);
        jsonBytes.CopyTo(output, 20);
        var outputBinHeader = 20 + paddedJsonLength;
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outputBinHeader, 4), paddedBinLength);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outputBinHeader + 4, 4), 0x004E4942);
        rebuiltBin.CopyTo(output, outputBinHeader + 8);
        File.WriteAllBytes(outputPath, output);
    }

    internal static void WriteSkeletalMeshExpandedTopology(string sourcePath, string destinationPath)
    {
        var source = File.ReadAllBytes(sourcePath);
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(12)));
        var binaryHeader = checked(20 + jsonLength);
        var binaryLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(binaryHeader)));
        var binaryStart = checked(binaryHeader + 8);
        var originalBinary = source.AsSpan(binaryStart, binaryLength).ToArray();
        var root = JsonNode.Parse(source.AsSpan(20, jsonLength))!.AsObject();
        var accessors = root["accessors"]!.AsArray();
        var views = root["bufferViews"]!.AsArray();
        var primitive = root["meshes"]![0]!["primitives"]![0]!.AsObject();
        var attributes = primitive["attributes"]!.AsObject();
        var indexAccessorIndex = primitive["indices"]!.GetValue<int>();
        var originalRoot = JsonDocument.Parse(source.AsMemory(20, jsonLength)).RootElement.Clone();
        using var binary = new MemoryStream();
        binary.Write(originalBinary);

        static int ComponentBytes(int componentType) => componentType switch
        {
            5120 or 5121 => 1,
            5122 or 5123 => 2,
            5125 or 5126 => 4,
            _ => throw new InvalidOperationException($"Unsupported synthetic component type {componentType}."),
        };
        static int ComponentCount(string type) => type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            _ => throw new InvalidOperationException($"Unsupported synthetic accessor type {type}."),
        };
        static uint ReadIndex(ReadOnlySpan<byte> data, int componentType, int index) => componentType switch
        {
            5121 => data[index],
            5123 => BinaryPrimitives.ReadUInt16LittleEndian(data[(index * 2)..]),
            5125 => BinaryPrimitives.ReadUInt32LittleEndian(data[(index * 4)..]),
            _ => throw new InvalidOperationException("Unsupported synthetic index component type."),
        };
        static void WriteIndex(Span<byte> data, int componentType, int index, uint value)
        {
            switch (componentType)
            {
                case 5121: data[index] = checked((byte)value); break;
                case 5123: BinaryPrimitives.WriteUInt16LittleEndian(data[(index * 2)..], checked((ushort)value)); break;
                case 5125: BinaryPrimitives.WriteUInt32LittleEndian(data[(index * 4)..], value); break;
                default: throw new InvalidOperationException("Unsupported synthetic index component type.");
            }
        }

        byte[] ReadPayload(int accessorIndex)
        {
            var accessor = originalRoot.GetProperty("accessors")[accessorIndex];
            var view = originalRoot.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
            var componentType = accessor.GetProperty("componentType").GetInt32();
            var elementBytes = ComponentBytes(componentType) * ComponentCount(accessor.GetProperty("type").GetString()!);
            var count = accessor.GetProperty("count").GetInt32();
            var offset = (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) +
                         (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0);
            return originalBinary.AsSpan(offset, checked(count * elementBytes)).ToArray();
        }

        void Retarget(int accessorIndex, byte[] payload, int count)
        {
            while ((binary.Position & 3) != 0) binary.WriteByte(0);
            var offset = checked((int)binary.Position);
            binary.Write(payload);
            var accessor = accessors[accessorIndex]!.AsObject();
            var view = views[accessor["bufferView"]!.GetValue<int>()]!.AsObject();
            view["byteOffset"] = offset;
            view["byteLength"] = payload.Length;
            accessor["byteOffset"] = 0;
            accessor["count"] = count;
        }

        var indexAccessorNode = accessors[indexAccessorIndex]!.AsObject();
        var indexType = indexAccessorNode["componentType"]!.GetValue<int>();
        var oldIndices = ReadPayload(indexAccessorIndex);
        var a = ReadIndex(oldIndices, indexType, 0);
        var b = ReadIndex(oldIndices, indexType, 1);
        var c = ReadIndex(oldIndices, indexType, 2);
        var indexBytes = ComponentBytes(indexType);
        var newIndices = new byte[checked(indexBytes * 6)];
        WriteIndex(newIndices, indexType, 0, a);
        WriteIndex(newIndices, indexType, 1, b);
        WriteIndex(newIndices, indexType, 2, c);
        WriteIndex(newIndices, indexType, 3, a);
        WriteIndex(newIndices, indexType, 4, c);
        WriteIndex(newIndices, indexType, 5, 3);
        Retarget(indexAccessorIndex, newIndices, 6);

        foreach (var attribute in attributes.ToArray())
        {
            var accessorIndex = attribute.Value!.GetValue<int>();
            var accessor = accessors[accessorIndex]!.AsObject();
            var componentType = accessor["componentType"]!.GetValue<int>();
            var components = ComponentCount(accessor["type"]!.GetValue<string>());
            var elementBytes = checked(ComponentBytes(componentType) * components);
            var oldPayload = ReadPayload(accessorIndex);
            var oldCount = accessor["count"]!.GetValue<int>();
            Assert(oldCount == 3, "synthetic topology fixture expected three source vertices");
            var expanded = new byte[checked(elementBytes * 4)];
            oldPayload.CopyTo(expanded, 0);
            oldPayload.AsSpan(checked((int)a * elementBytes), elementBytes)
                .CopyTo(expanded.AsSpan(elementBytes * 3));
            if (attribute.Key == "POSITION")
            {
                for (var component = 0; component < 3; component++)
                {
                    float ReadPosition(uint vertex) => BitConverter.Int32BitsToSingle(
                        BinaryPrimitives.ReadInt32LittleEndian(oldPayload.AsSpan(
                            checked((int)vertex * elementBytes + component * 4))));
                    var value = ReadPosition(a) + ReadPosition(c) - ReadPosition(b);
                    BinaryPrimitives.WriteInt32LittleEndian(
                        expanded.AsSpan(elementBytes * 3 + component * 4), BitConverter.SingleToInt32Bits(value));
                }
            }
            else if (attribute.Key == "_ARKIVE_VERTEX_ID")
            {
                BinaryPrimitives.WriteUInt32LittleEndian(expanded.AsSpan(elementBytes * 3), 3);
            }
            else if (attribute.Key == "TEXCOORD_1")
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    expanded.AsSpan(elementBytes * 3), BitConverter.SingleToInt32Bits(3f));
            }
            Retarget(accessorIndex, expanded, 4);
        }

        while ((binary.Position & 3) != 0) binary.WriteByte(0);
        root["buffers"]![0]!["byteLength"] = checked((int)binary.Length);
        var newJson = Encoding.UTF8.GetBytes(root.ToJsonString());
        File.WriteAllBytes(destinationPath, BuildSyntheticGlb(newJson, binary.ToArray()));
    }

    internal sealed class SyntheticCrunchDecoder : ICrunchDecoder
    {
        private readonly NativeTextureFormatProfile _format;
        private readonly byte[] _decodedBlock;

        public static byte[] DecodedBlock { get; } =
            Enumerable.Range(0, 16).Select(index => (byte)(0xF0 - index)).ToArray();

        public static byte[] Dxt1DecodedBlock { get; } =
            Enumerable.Range(0, 8).Select(index => (byte)(0x80 + index)).ToArray();

        public SyntheticCrunchDecoder(
            NativeTextureFormatProfile? format = null,
            byte[]? decodedBlock = null)
        {
            _format = format ?? NativeTextureFormats.Dxt5;
            _decodedBlock = decodedBlock?.ToArray() ?? DecodedBlock.ToArray();
        }

        public string Name => "Synthetic Crunch decoder";

        public CrunchTextureInfo Inspect(ReadOnlySpan<byte> crnData) => new(
            4,
            4,
            1,
            1,
            _format.CrunchFormat,
            _format.CrunchFourCc,
            _format.BytesPerBlock,
            crnData.Length,
            Math.Min(8, crnData.Length));

        public byte[] DecodeDxt1(ReadOnlySpan<byte> crnData) =>
            _format == NativeTextureFormats.Dxt1
                ? Dxt1DecodedBlock.ToArray()
                : throw new NotSupportedException("Synthetic decoder is not configured for DXT1.");

        public byte[] DecodeDxt5(ReadOnlySpan<byte> crnData) =>
            _format == NativeTextureFormats.Dxt5
                ? _decodedBlock.ToArray()
                : throw new NotSupportedException("Synthetic decoder is not configured for DXT5.");

        public byte[] DecodeBc5(ReadOnlySpan<byte> crnData) =>
            _format == NativeTextureFormats.Bc5
                ? _decodedBlock.ToArray()
                : throw new NotSupportedException("Synthetic decoder is not configured for BC5.");
    }

    internal sealed class SyntheticModDeploymentProcessGuard : IModDeploymentProcessGuard
    {
        private readonly Action? _onCheck;
        private readonly string[] _blockingProcesses;

        public SyntheticModDeploymentProcessGuard(params string[] blockingProcesses) =>
            _blockingProcesses = blockingProcesses;

        public SyntheticModDeploymentProcessGuard(Action onCheck, params string[] blockingProcesses)
        {
            _onCheck = onCheck;
            _blockingProcesses = blockingProcesses;
        }

        public IReadOnlyList<string> FindBlockingProcesses()
        {
            _onCheck?.Invoke();
            return _blockingProcesses;
        }
    }

    internal static void WriteSkeletalMeshIndexEdit(string sourcePath, string destinationPath)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        using var document = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var root = document.RootElement;
        var indexAccessorIndex = root.GetProperty("meshes")[0].GetProperty("primitives")[0]
            .GetProperty("indices").GetInt32();
        var accessor = root.GetProperty("accessors")[indexAccessorIndex];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var binaryStart = checked(20 + jsonLength + 8);
        var offset = checked(binaryStart +
            (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) +
            (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0));
        switch (accessor.GetProperty("componentType").GetInt32())
        {
            case 5121:
                bytes[offset] = (byte)((bytes[offset] + 1) % 3);
                break;
            case 5123:
                var shortIndex = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)((shortIndex + 1) % 3));
                break;
            case 5125:
                var intIndex = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), (intIndex + 1) % 3);
                break;
            default:
                throw new InvalidOperationException("Synthetic mesh used an unsupported index type.");
        }
        File.WriteAllBytes(destinationPath, bytes);
    }

    internal static void WriteNameReference(BinaryWriter writer, int index, int number = 0)
    {
        writer.Write(index);
        writer.Write(number);
    }

    internal static void WriteSkeletalMeshPositionEdit(string sourcePath, string destinationPath, float deltaX)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        using var document = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var root = document.RootElement;
        var positionAccessorIndex = root.GetProperty("meshes")[0].GetProperty("primitives")[0]
            .GetProperty("attributes").GetProperty("POSITION").GetInt32();
        var accessor = root.GetProperty("accessors")[positionAccessorIndex];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var binaryStart = checked(20 + jsonLength + 8);
        var positionOffset = checked(binaryStart +
            (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) +
            (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0));
        var original = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(positionOffset)));
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(positionOffset), BitConverter.SingleToInt32Bits(original + deltaX));
        File.WriteAllBytes(destinationPath, bytes);
    }

    internal static void WriteCataloguePackage(string path)
    {
        var names = new[] { "None", "Core", "Class", "Texture2D", "Package", "TestTexture" };
        using var tableStream = new MemoryStream();
        using var tableWriter = new BinaryWriter(tableStream, Encoding.UTF8, leaveOpen: true);

        foreach (var name in names)
        {
            WriteUnrealString(tableWriter, name);
            tableWriter.Write(0UL);
        }

        var importOffset = checked(129 + (int)tableStream.Length);
        WriteNameReference(tableWriter, 1);
        WriteNameReference(tableWriter, 2);
        tableWriter.Write(0);
        WriteNameReference(tableWriter, 3);

        var exportOffset = checked(129 + (int)tableStream.Length);
        tableWriter.Write(-1);
        tableWriter.Write(0);
        tableWriter.Write(0);
        WriteNameReference(tableWriter, 5);
        tableWriter.Write(0);
        tableWriter.Write(0U);
        tableWriter.Write(0U);
        tableWriter.Write(0);
        tableWriter.Write(0);
        tableWriter.Write(0U);
        tableWriter.Write(0);
        tableWriter.Write(Guid.Empty.ToByteArray());
        tableWriter.Write(0);

        var tableEnd = checked(129 + (int)tableStream.Length);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        WriteSummary(
            writer,
            tableEnd,
            names.Length,
            129,
            1,
            exportOffset,
            1,
            importOffset,
            tableEnd,
            tableEnd,
            []);

        while (stream.Position < 129)
        {
            writer.Write((byte)0);
        }

        writer.Write(tableStream.ToArray());
    }

    internal static (int SerialOffset, int SerialSize) WriteSkeletalMeshReconPackage(
        string path,
        string childBoneName = "ChildBone",
        float childHeightCentimetres = 50f)
    {
        var names = new[]
        {
            "None", "Core", "Class", "SkeletalMesh", "Package", "TestMesh",
            "lodinfo", "ArrayProperty", "bhasbeensimplified", "BoolProperty",
            "clothtearfactor", "FloatProperty", childBoneName,
        };
        using var namesStream = new MemoryStream();
        using (var writer = new BinaryWriter(namesStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var name in names)
            {
                WriteUnrealString(writer, name);
                writer.Write(0UL);
            }
        }

        using var importStream = new MemoryStream();
        using (var writer = new BinaryWriter(importStream, Encoding.UTF8, leaveOpen: true))
        {
            WriteNameReference(writer, 1);
            WriteNameReference(writer, 2);
            writer.Write(0);
            WriteNameReference(writer, 3);
        }

        const int exportRecordSize = 68;
        const int nameOffset = 129;
        var importOffset = checked(nameOffset + (int)namesStream.Length);
        var exportOffset = checked(importOffset + (int)importStream.Length);
        var serialOffset = checked(exportOffset + exportRecordSize);
        using var payloadStream = new MemoryStream();
        using (var writer = new BinaryWriter(payloadStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x12345678); // preserved UObject/state prefix
            WriteNameReference(writer, 6);
            WriteNameReference(writer, 7);
            writer.Write(4);
            writer.Write(0);
            writer.Write(0); // empty LODInfo property array in the structural fixture
            WriteNameReference(writer, 8);
            WriteNameReference(writer, 9);
            writer.Write(0);
            writer.Write(0);
            writer.Write((byte)1);
            WriteNameReference(writer, 10);
            WriteNameReference(writer, 11);
            writer.Write(4);
            writer.Write(0);
            writer.Write(3f);
            WriteNameReference(writer, 0);

            // Minimal Lost Ark/UE3 v868 native SkeletalMesh layout.
            writer.Write(new byte[28]); // bounds
            writer.Write(0); // material references
            writer.Write(new byte[24]); // mesh origin and rotation origin
            writer.Write(2); // reference skeleton count
            WriteNameReference(writer, 5); // TestMesh/root
            writer.Write(0); // bone flags
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f); // quaternion
            writer.Write(0f); writer.Write(0f); writer.Write(0f); // position
            writer.Write(1); // children
            writer.Write(-1); // root parent
            writer.Write(-1); // bone colour
            WriteNameReference(writer, 12); // ChildBone
            writer.Write(0); // bone flags
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f); // quaternion
            writer.Write(0f); writer.Write(0f); writer.Write(childHeightCentimetres); // position
            writer.Write(0); // children
            writer.Write(0); // parent root
            writer.Write(-1); // bone colour
            writer.Write(2); // skeletal depth
            writer.Write(4); // Lost Ark extension version
            writer.Write(1); // Lost Ark extension count
            writer.Write(0); // Lost Ark extension value
            writer.Write(1); // LOD count

            writer.Write(1); // section count
            writer.Write((short)0); // material index
            writer.Write((short)0); // chunk index
            writer.Write(0); // first index
            writer.Write(1); // triangle count
            writer.Write((byte)0); // section flags

            writer.Write(1); // index-buffer revision
            writer.Write((byte)2); // index item size
            writer.Write(2); // bulk element size
            writer.Write(3); // index count
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2);

            writer.Write(2); // active-bone count
            writer.Write((short)0);
            writer.Write((short)1);
            writer.Write(1); // chunk count
            writer.Write(0); // first vertex
            writer.Write(0); // CPU rigid vertices
            writer.Write(0); // CPU soft vertices
            writer.Write(2); // bone-map count
            writer.Write((short)0);
            writer.Write((short)1);
            writer.Write(3); // declared rigid vertices
            writer.Write(0); // declared soft vertices
            writer.Write(1); // max influences
            writer.Write(0); // LOD size field
            writer.Write(3); // vertex count
            writer.Write(2); // required-bone count
            writer.Write((byte)0);
            writer.Write((byte)1);

            writer.Write(0U); // legacy bulk flags
            writer.Write(0); // legacy bulk elements
            writer.Write(0); // legacy bulk stored size
            writer.Write(0); // legacy bulk offset
            writer.Write(1); // UV-set count
            writer.Write(1); // GPU UV-set count
            writer.Write(0); // half-precision UVs
            writer.Write(0); // unpacked positions
            writer.Write(1f); writer.Write(1f); writer.Write(1f); // mesh extension
            writer.Write(0f); writer.Write(0f); writer.Write(0f); // mesh origin
            writer.Write(32); // GPU vertex stride
            writer.Write(3); // GPU vertex count
            var positions = new[]
            {
                (X: 0f, Y: 0f, Z: 0f, Bone: 0),
                (X: 100f, Y: 0f, Z: 0f, Bone: 1),
                (X: 0f, Y: 0f, Z: 100f, Bone: 1),
            };
            foreach (var position in positions)
            {
                writer.Write(new byte[] { 255, 128, 128, 255 }); // tangent +X
                writer.Write(new byte[] { 128, 255, 128, 255 }); // normal +Y -> glTF +Z, handedness +
                writer.Write(position.Bone); // four byte-sized chunk-local bone indices
                writer.Write(new byte[] { 255, 0, 0, 0 });
                writer.Write(position.X); writer.Write(position.Y); writer.Write(position.Z);
                writer.Write((ushort)0); writer.Write((ushort)0); // half-precision UV0
            }
            writer.Write(0); // extra influence sets
            writer.Write(0); // adjacency revision
            writer.Write((byte)2); // adjacency item size
            writer.Write(2); // adjacency bulk element size
            writer.Write(0); // adjacency index count
        }
        var serialSize = checked((int)payloadStream.Length);

        using var exportStream = new MemoryStream();
        using (var writer = new BinaryWriter(exportStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(-1);
            writer.Write(0);
            writer.Write(0);
            WriteNameReference(writer, 5);
            writer.Write(0);
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(serialSize);
            writer.Write(serialOffset);
            writer.Write(0U);
            writer.Write(0);
            writer.Write(Guid.Empty.ToByteArray());
            writer.Write(0);
        }
        Assert(exportStream.Length == exportRecordSize, "synthetic SkeletalMesh export record size changed");

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var packageWriter = new BinaryWriter(stream);
        WriteSummary(packageWriter, serialOffset, names.Length, nameOffset, 1, exportOffset,
            1, importOffset, serialOffset, serialOffset, []);
        while (stream.Position < nameOffset) packageWriter.Write((byte)0);
        packageWriter.Write(namesStream.ToArray());
        packageWriter.Write(importStream.ToArray());
        packageWriter.Write(exportStream.ToArray());
        packageWriter.Write(payloadStream.ToArray());
        return (serialOffset, serialSize);
    }

    internal static void WriteSkeletalMeshStableIdentityRemoval(string sourcePath, string destinationPath)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        using var document = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var root = document.RootElement;
        var attributes = root.GetProperty("meshes")[0].GetProperty("primitives")[0]
            .GetProperty("attributes");
        var carrierAccessorIndex = attributes.GetProperty("TEXCOORD_1").GetInt32();
        var accessor = root.GetProperty("accessors")[carrierAccessorIndex];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var binaryStart = checked(20 + jsonLength + 8);
        var carrierOffset = checked(binaryStart +
            (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) +
            (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0));
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(carrierOffset + 4), BitConverter.SingleToInt32Bits(0.123f));

        var json = bytes.AsSpan(20, jsonLength);
        var original = Encoding.UTF8.GetBytes("_ARKIVE_VERTEX_ID");
        var replacement = Encoding.UTF8.GetBytes("_REMOVE_VERTEX_ID");
        Assert(original.Length == replacement.Length, "synthetic stable-ID key replacement changed JSON length");
        var keyOffset = json.IndexOf(original);
        if (keyOffset < 0) throw new InvalidOperationException("Synthetic GLB stable-ID key was not found.");
        replacement.CopyTo(json[keyOffset..]);
        File.WriteAllBytes(destinationPath, bytes);
    }

    /// <summary>
    /// Rewrites a GLB the way a typical Blender export differs from Arkive's: a shape key on the
    /// first primitive, a second UV map, and no tangents.
    /// </summary>
    internal static void WriteSkeletalMeshBlenderStyleExport(string sourcePath, string destinationPath)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        var root = JsonNode.Parse(bytes.AsSpan(20, jsonLength))!.AsObject();
        foreach (var primitive in root["meshes"]!.AsArray().SelectMany(mesh => mesh!["primitives"]!.AsArray()))
        {
            var attributes = primitive!["attributes"]!.AsObject();
            attributes.Remove("TANGENT");
            attributes["TEXCOORD_7"] = attributes["TEXCOORD_0"]!.GetValue<int>();
            primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = attributes["NORMAL"]!.GetValue<int>() });
        }
        var json = Encoding.UTF8.GetBytes(root.ToJsonString());
        var paddedJsonLength = (json.Length + 3) & ~3;
        var binaryChunk = bytes.AsSpan(20 + jsonLength);
        var output = new byte[20 + paddedJsonLength + binaryChunk.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, 4), 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8, 4), output.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12, 4), paddedJsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16, 4), 0x4E4F534A);
        output.AsSpan(20, paddedJsonLength).Fill(0x20);
        json.CopyTo(output, 20);
        binaryChunk.CopyTo(output.AsSpan(20 + paddedJsonLength));
        File.WriteAllBytes(destinationPath, output);
    }

    internal static void WriteSkeletalMeshNormalEdit(string sourcePath, string destinationPath, float deltaX)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        using var document = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var root = document.RootElement;
        var accessorIndex = root.GetProperty("meshes")[0].GetProperty("primitives")[0]
            .GetProperty("attributes").GetProperty("NORMAL").GetInt32();
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var binaryStart = checked(20 + jsonLength + 8);
        var offset = checked(binaryStart +
            (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) +
            (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0));
        var original = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)));
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(offset), BitConverter.SingleToInt32Bits(original + deltaX));
        File.WriteAllBytes(destinationPath, bytes);
    }

    internal static (int SerialOffset, int SerialSize) WriteMaterialInstancePackage(string path)
    {
        var names = new[]
        {
            "None", "Core", "Class", "MaterialInstanceConstant", "Package", "TestMaterial",
            "ScalarParameterValues", "ArrayProperty", "TextureParameterValues", "VectorParameterValues",
            "Parent", "ObjectProperty", "ParameterName", "NameProperty", "ParameterValue",
            "FloatProperty", "StructProperty", "ExpressionGUID", "Guid", "LinearColor",
            "Roughness", "Tint", "DiffuseTexture", "Texture2D", "AltTexture",
            "AltParent", "Engine", "bHasStaticPermutationResource", "BoolProperty",
        };
        using var namesStream = new MemoryStream();
        using (var writer = new BinaryWriter(namesStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var name in names) { WriteUnrealString(writer, name); writer.Write(0UL); }
        }
        using var importStream = new MemoryStream();
        using (var writer = new BinaryWriter(importStream, Encoding.UTF8, leaveOpen: true))
        {
            WriteNameReference(writer, 1); WriteNameReference(writer, 2);
            writer.Write(0); WriteNameReference(writer, 3);
            WriteNameReference(writer, 26); WriteNameReference(writer, 23);
            writer.Write(0); WriteNameReference(writer, 24);
            WriteNameReference(writer, 26); WriteNameReference(writer, 3);
            writer.Write(0); WriteNameReference(writer, 25);
        }
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            var scalar = BuildArray(element =>
            {
                WriteTag(element, 12, 13, 8); WriteNameReference(element, 20);
                WriteTag(element, 14, 15, 4); element.Write(0.5f);
                WriteStructTag(element, 17, 16, 16, 18); element.Write(Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray());
                WriteNameReference(element, 0);
            });
            WriteTag(writer, 6, 7, scalar.Length); writer.Write(scalar);

            var texture = BuildArray(element =>
            {
                WriteTag(element, 12, 13, 8); WriteNameReference(element, 22);
                WriteTag(element, 14, 11, 4); element.Write(0);
                WriteStructTag(element, 17, 16, 16, 18); element.Write(Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE").ToByteArray());
                WriteNameReference(element, 0);
            });
            WriteTag(writer, 8, 7, texture.Length); writer.Write(texture);

            var vector = BuildArray(element =>
            {
                WriteTag(element, 12, 13, 8); WriteNameReference(element, 21);
                WriteStructTag(element, 14, 16, 16, 19);
                element.Write(1f); element.Write(0.5f); element.Write(0.25f); element.Write(1f);
                WriteStructTag(element, 17, 16, 16, 18); element.Write(Guid.Parse("12345678-1234-1234-1234-123456789ABC").ToByteArray());
                WriteNameReference(element, 0);
            });
            WriteTag(writer, 9, 7, vector.Length); writer.Write(vector);
            WriteTag(writer, 10, 11, 4); writer.Write(0);
            WriteTag(writer, 27, 28, 0); writer.Write((byte)1);
            WriteNameReference(writer, 0);
        }

        const int exportRecordSize = 68;
        var nameOffset = 129;
        var importOffset = checked(nameOffset + (int)namesStream.Length);
        var exportOffset = checked(importOffset + (int)importStream.Length);
        var serialOffset = checked(exportOffset + exportRecordSize);
        var serialSize = checked((int)payload.Length);
        var total = checked(serialOffset + serialSize);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var packageWriter = new BinaryWriter(stream);
        WriteSummary(packageWriter, total, names.Length, nameOffset, 1, exportOffset, 3,
            importOffset, total, total, []);
        while (stream.Position < nameOffset) packageWriter.Write((byte)0);
        packageWriter.Write(namesStream.ToArray());
        packageWriter.Write(importStream.ToArray());
        packageWriter.Write(-1); packageWriter.Write(0); packageWriter.Write(0);
        WriteNameReference(packageWriter, 5); packageWriter.Write(0);
        packageWriter.Write(0U); packageWriter.Write(0U);
        packageWriter.Write(serialSize); packageWriter.Write(serialOffset);
        packageWriter.Write(0U); packageWriter.Write(0);
        packageWriter.Write(Guid.Empty.ToByteArray()); packageWriter.Write(0);
        packageWriter.Write(payload.ToArray());
        return (serialOffset, serialSize);

        static byte[] BuildArray(Action<BinaryWriter> writeElement)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(1);
            writeElement(writer);
            writer.Flush();
            return stream.ToArray();
        }
        static void WriteTag(BinaryWriter writer, int name, int type, int size)
        {
            WriteNameReference(writer, name); WriteNameReference(writer, type);
            writer.Write(size); writer.Write(0);
        }
        static void WriteStructTag(BinaryWriter writer, int name, int type, int size, int metadata)
        {
            WriteTag(writer, name, type, size); WriteNameReference(writer, metadata);
        }
    }

    internal static void WritePackage(string path, IReadOnlyList<CompressedChunk> chunks)
    {
        var firstPhysicalChunkOffset = 129 + (chunks.Count * 20);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        WriteSummary(writer, chunks.Count == 0 ? 129 : 161, 0, 129, 0, 129, 0, 129, 129, 129, chunks);

        while (stream.Position < firstPhysicalChunkOffset)
        {
            writer.Write((byte)0);
        }

        foreach (var chunk in chunks)
        {
            while (stream.Position < chunk.CompressedOffset)
            {
                writer.Write((byte)0);
            }

            writer.Write(new byte[checked((int)chunk.CompressedSize)]);
        }

        while (stream.Length < 129)
        {
            writer.Write((byte)0);
        }
    }

    internal static void WriteSkeletalMeshInverseBindEdit(string sourcePath, string destinationPath, float delta)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        using var document = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var root = document.RootElement;
        var accessorIndex = root.GetProperty("skins")[0].GetProperty("inverseBindMatrices").GetInt32();
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var binaryStart = checked(20 + jsonLength + 8);
        var offset = checked(binaryStart +
            (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) +
            (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0));
        var original = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)));
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(offset), BitConverter.SingleToInt32Bits(original + delta));
        File.WriteAllBytes(destinationPath, bytes);
    }

    internal static void WriteCompressedPayloadPackage(
        string path,
        byte[] payload,
        bool protect,
        byte[]? protectionKey = null)
    {
        var compressed = EncodeLz4LiteralBlock(payload);
        if (protect)
        {
            var encryptedLength = Math.Min(4096, compressed.Length);
            encryptedLength -= encryptedLength % 16;
            if (encryptedLength > 0)
            {
                using var aes = Aes.Create();
                aes.KeySize = 256;
                aes.Key = protectionKey ?? testKey;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.EncryptEcb(compressed.AsSpan(0, encryptedLength), compressed.AsSpan(0, encryptedLength), PaddingMode.None);
            }
        }

        const uint compressedOffset = 149;
        var physicalChunkSize = checked((uint)(24 + compressed.Length));
        var chunk = new CompressedChunk(
            129,
            checked((uint)payload.Length),
            compressedOffset,
            physicalChunkSize,
            protect ? 1u : 0u);

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        WriteSummary(writer, 129 + payload.Length, 0, 129, 0, 129, 0, 129, 129, 129, [chunk]);

        while (stream.Position < compressedOffset)
        {
            writer.Write((byte)0);
        }

        writer.Write(PackageConstants.UnrealPackageTag);
        writer.Write(512);
        writer.Write(compressed.Length);
        writer.Write(payload.Length);
        writer.Write(compressed.Length);
        writer.Write(payload.Length);
        writer.Write(compressed);
    }

    internal static byte[] EncodeLz4LiteralBlock(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream();
        var literalNibble = Math.Min(15, payload.Length);
        stream.WriteByte((byte)(literalNibble << 4));
        if (payload.Length >= 15)
        {
            var remaining = payload.Length - 15;
            while (remaining >= 255)
            {
                stream.WriteByte(255);
                remaining -= 255;
            }

            stream.WriteByte((byte)remaining);
        }

        stream.Write(payload);
        return stream.ToArray();
    }

    internal static void AssertNoValidationErrors(PackageSummary summary)
    {
        var errors = PackageValidator.Validate(summary)
            .Where(issue => issue.Severity == ValidationSeverity.Error)
            .ToArray();
        Assert(errors.Length == 0, string.Join("; ", errors.Select(error => error.Message)));
    }

    internal static byte[] BuildSyntheticGlb(byte[] json, byte[] binary)
    {
        static int Align4(int value) => checked((value + 3) & ~3);
        var jsonLength = Align4(json.Length);
        var binaryLength = Align4(binary.Length);
        var result = new byte[checked(12 + 8 + jsonLength + 8 + binaryLength)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)result.Length));
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

    internal sealed class SyntheticModPackageExternalValidator : IModPackageExternalValidator
    {
        public Task<ModExternalValidationResult> ValidateAsync(
            string packagePath,
            string className,
            string objectName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(packagePath))
            {
                throw new FileNotFoundException(
                    "Synthetic external validator received a missing package.", packagePath);
            }
            return Task.FromResult(new ModExternalValidationResult(
                "Synthetic UModel-compatible validator",
                AssetCount: 1,
                ValidationFileCount: 1,
                CataloguePassed: true,
                NativeLoadPassed: true,
                ExpectedObjectFound: true,
                Diagnostics: [$"Loaded {className} {objectName}."],
                Duration: TimeSpan.FromMilliseconds(1)));
        }
    }

    internal static void WriteSummary(
        BinaryWriter writer,
        int headerSize,
        int nameCount,
        int nameOffset,
        int exportCount,
        int exportOffset,
        int importCount,
        int importOffset,
        int dependsOffset,
        int guidOffset,
        IReadOnlyList<CompressedChunk> chunks)
    {
        writer.Write(PackageConstants.UnrealPackageTag);
        writer.Write(PackageConstants.WesternFileVersion);
        writer.Write(PackageConstants.WesternLicenseeVersion);
        writer.Write(headerSize);
        WriteUnrealString(writer, "None");
        writer.Write(0x22080009u);
        writer.Write(nameCount);
        writer.Write(nameOffset);
        writer.Write(exportCount);
        writer.Write(exportOffset);
        writer.Write(importCount);
        writer.Write(importOffset);
        writer.Write(dependsOffset);
        writer.Write(guidOffset);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(Guid.Empty.ToByteArray());
        writer.Write(1);
        writer.Write(exportCount);
        writer.Write(nameCount);
        writer.Write(exportCount);
        writer.Write(12097);
        writer.Write(136);
        writer.Write(PackageConstants.WesternCompressionFlags);
        writer.Write(chunks.Count);

        foreach (var chunk in chunks)
        {
            writer.Write(chunk.UncompressedOffset);
            writer.Write(chunk.UncompressedSize);
            writer.Write(chunk.CompressedOffset);
            writer.Write(chunk.CompressedSize);
            writer.Write(chunk.LostArkFlags);
        }

        writer.Write(0x12345678u);
        writer.Write(0);
    }

    internal static void WriteSkeletalMeshWeightSumEdit(string sourcePath, string destinationPath, float firstWeight)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        using var document = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var root = document.RootElement;
        var accessorIndex = root.GetProperty("meshes")[0].GetProperty("primitives")[0]
            .GetProperty("attributes").GetProperty("WEIGHTS_0").GetInt32();
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var binaryStart = checked(20 + jsonLength + 8);
        var offset = checked(binaryStart +
            (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) +
            (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0));
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(offset), BitConverter.SingleToInt32Bits(firstWeight));
        File.WriteAllBytes(destinationPath, bytes);
    }

    internal sealed class SyntheticClientUpdateRebaseService : IClientUpdateRebaseService
    {
        private readonly Func<GuidedTexturePackageEntry,
            IReadOnlyList<GuidedTextureModProjectEntry>,
            IReadOnlyList<SkeletalMeshEditWorkspaceEntry>, string,
            CancellationToken, Task<ClientUpdateRebaseResult>> _handler;

        public SyntheticClientUpdateRebaseService(
            Func<GuidedTexturePackageEntry,
                IReadOnlyList<GuidedTextureModProjectEntry>,
                IReadOnlyList<SkeletalMeshEditWorkspaceEntry>, string,
                CancellationToken, Task<ClientUpdateRebaseResult>> handler) => _handler = handler;

        public Task<ClientUpdateRebaseResult> RebaseAsync(
            GuidedTexturePackageEntry package,
            IReadOnlyList<GuidedTextureModProjectEntry> textures,
            IReadOnlyList<SkeletalMeshEditWorkspaceEntry> meshes,
            IReadOnlyList<MaterialInstanceEditEntry> materials,
            string outputRootDirectory,
            CancellationToken cancellationToken = default) =>
            _handler(package, textures, meshes, outputRootDirectory, cancellationToken);
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void WriteUncompressedBgraDds(string path, RgbaImage image)
    {
        image.Validate();
        var bytes = new byte[checked(128 + image.Width * image.Height * 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x20534444);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x0000100F);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), checked((uint)image.Height));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), checked((uint)image.Width));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), checked((uint)(image.Width * 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), 0x41);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92), 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100), 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104), 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108), 0x1000);
        for (var pixel = 0; pixel < image.Width * image.Height; pixel++)
        {
            var source = pixel * 4;
            var destination = 128 + source;
            bytes[destination] = image.Pixels[source + 2];
            bytes[destination + 1] = image.Pixels[source + 1];
            bytes[destination + 2] = image.Pixels[source];
            bytes[destination + 3] = image.Pixels[source + 3];
        }

        File.WriteAllBytes(path, bytes);
    }

    internal static (int SerialOffset, int SerialSize) WriteTextureReconPackage(
        string path,
        bool lostArkTagExtensions = false,
        string pixelFormatName = "PF_DXT5",
        byte[]? storedMipOverride = null,
        int textureWidth = 4,
        int textureHeight = 4,
        bool omitCrunchProperty = false,
        int? mipElementCountOverride = null)
    {
        var names = new[]
        {
            "None", "Core", "Class", "Texture2D", "Package", "TestTexture",
            "SizeX", "IntProperty", "SizeY", "Format", "ByteProperty", "EPixelFormat",
            pixelFormatName, "bUseCrunchCompression", "BoolProperty", "OriginalSizeX", "OriginalSizeY",
        };
        using var namesStream = new MemoryStream();
        using (var namesWriter = new BinaryWriter(namesStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var name in names)
            {
                WriteUnrealString(namesWriter, name);
                namesWriter.Write(0UL);
            }
        }

        using var importStream = new MemoryStream();
        using (var importWriter = new BinaryWriter(importStream, Encoding.UTF8, leaveOpen: true))
        {
            WriteNameReference(importWriter, 1);
            WriteNameReference(importWriter, 2);
            importWriter.Write(0);
            WriteNameReference(importWriter, 3);
        }

        const int exportRecordSize = 68;
        var nameOffset = 129;
        var importOffset = checked(nameOffset + (int)namesStream.Length);
        var exportOffset = checked(importOffset + (int)importStream.Length);
        var serialOffset = checked(exportOffset + exportRecordSize);

        using var payloadStream = new MemoryStream();
        using (var payloadWriter = new BinaryWriter(payloadStream, Encoding.UTF8, leaveOpen: true))
        {
            if (lostArkTagExtensions)
            {
                payloadWriter.Write(0x12345678);
            }

            WriteNameReference(payloadWriter, 6);
            WriteNameReference(payloadWriter, 7);
            payloadWriter.Write(4);
            payloadWriter.Write(0);
            if (lostArkTagExtensions)
            {
                WriteNameReference(payloadWriter, 0);
            }
            payloadWriter.Write(textureWidth);

            WriteNameReference(payloadWriter, 8);
            WriteNameReference(payloadWriter, 7);
            payloadWriter.Write(4);
            payloadWriter.Write(0);
            if (lostArkTagExtensions)
            {
                WriteNameReference(payloadWriter, 0);
            }
            payloadWriter.Write(textureHeight);

            if (lostArkTagExtensions)
            {
                WriteNameReference(payloadWriter, 15);
                WriteNameReference(payloadWriter, 7);
                payloadWriter.Write(4);
                payloadWriter.Write(0);
                WriteNameReference(payloadWriter, 0);
                payloadWriter.Write(textureWidth);

                WriteNameReference(payloadWriter, 16);
                WriteNameReference(payloadWriter, 7);
                payloadWriter.Write(4);
                payloadWriter.Write(0);
                WriteNameReference(payloadWriter, 0);
                payloadWriter.Write(textureHeight);
            }

            WriteNameReference(payloadWriter, 9);
            WriteNameReference(payloadWriter, 10);
            payloadWriter.Write(lostArkTagExtensions ? 8 : 1);
            payloadWriter.Write(0);
            WriteNameReference(payloadWriter, 11);
            if (lostArkTagExtensions)
            {
                WriteNameReference(payloadWriter, 12);
            }
            else
            {
                payloadWriter.Write((byte)5);
            }

            if (lostArkTagExtensions && !omitCrunchProperty)
            {
                WriteNameReference(payloadWriter, 13);
                WriteNameReference(payloadWriter, 14);
                payloadWriter.Write(0);
                payloadWriter.Write(0);
                payloadWriter.Write((byte)1);
            }

            WriteNameReference(payloadWriter, 0);

            payloadWriter.Write(1);
            var bulkHeaderRelativeOffset = checked((int)payloadStream.Position);
            payloadWriter.Write(lostArkTagExtensions ? 0x200u : 0x40u);
            var mipBytes = storedMipOverride?.Length ?? (lostArkTagExtensions &&
                           (pixelFormatName.Equals("PF_DXT5", StringComparison.OrdinalIgnoreCase) ||
                            pixelFormatName.Equals("PF_BC5", StringComparison.OrdinalIgnoreCase))
                ? 16
                : 8);
            payloadWriter.Write(mipElementCountOverride ?? mipBytes);
            payloadWriter.Write(mipBytes);
            payloadWriter.Write(checked(serialOffset + bulkHeaderRelativeOffset + 16));
            var storedMip = storedMipOverride?.ToArray() ??
                            Enumerable.Range(0, mipBytes).Select(index => (byte)(index + 1)).ToArray();
            if (lostArkTagExtensions && storedMipOverride is null)
            {
                storedMip[0] = (byte)'H';
                storedMip[1] = (byte)'x';
            }
            payloadWriter.Write(storedMip);
            payloadWriter.Write(textureWidth);
            payloadWriter.Write(textureHeight);
        }

        var serialSize = checked((int)payloadStream.Length);
        using var exportStream = new MemoryStream();
        using (var exportWriter = new BinaryWriter(exportStream, Encoding.UTF8, leaveOpen: true))
        {
            exportWriter.Write(-1);
            exportWriter.Write(0);
            exportWriter.Write(0);
            WriteNameReference(exportWriter, 5);
            exportWriter.Write(0);
            exportWriter.Write(0U);
            exportWriter.Write(0U);
            exportWriter.Write(serialSize);
            exportWriter.Write(serialOffset);
            exportWriter.Write(0U);
            exportWriter.Write(0);
            exportWriter.Write(Guid.Empty.ToByteArray());
            exportWriter.Write(0);
        }
        Assert(exportStream.Length == exportRecordSize, "synthetic export record size changed");

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        WriteSummary(
            writer,
            serialOffset,
            names.Length,
            nameOffset,
            1,
            exportOffset,
            1,
            importOffset,
            serialOffset,
            serialOffset,
            []);
        while (stream.Position < 129)
        {
            writer.Write((byte)0);
        }

        writer.Write(namesStream.ToArray());
        writer.Write(importStream.ToArray());
        writer.Write(exportStream.ToArray());
        writer.Write(payloadStream.ToArray());
        return (serialOffset, serialSize);
    }

}
