using System.Text;

namespace Arkive.Core.Packages;

public sealed class UpkPackageReader
{
    public PackageSummary Read(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            var tag = reader.ReadUInt32();
            if (tag != PackageConstants.UnrealPackageTag)
            {
                throw new PackageFormatException(
                    $"Unexpected package tag 0x{tag:X8}; expected 0x{PackageConstants.UnrealPackageTag:X8}.");
            }

            var fileVersion = reader.ReadUInt16();
            var licenseeVersion = reader.ReadUInt16();
            var headerSize = reader.ReadInt32();
            var folderName = ReadUnrealString(reader);

            var packageFlags = reader.ReadUInt32();
            var nameCount = ReadTableCount(reader, "name");
            var nameOffset = reader.ReadInt32();
            var exportCount = ReadTableCount(reader, "export");
            var exportOffset = reader.ReadInt32();
            var importCount = ReadTableCount(reader, "import");
            var importOffset = reader.ReadInt32();
            var dependsOffset = reader.ReadInt32();
            var importExportGuidsOffset = reader.ReadInt32();
            var importGuidsCount = ReadTableCount(reader, "import GUID");
            var exportGuidsCount = ReadTableCount(reader, "export GUID");
            var thumbnailTableOffset = reader.ReadInt32();
            var packageGuid = ReadUnrealGuid(reader);

            var generationCount = ReadBoundedCount(
                reader,
                "generation",
                max: 100_000);
            var generations = new List<PackageGeneration>(generationCount);
            for (var index = 0; index < generationCount; index++)
            {
                generations.Add(new PackageGeneration(
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32()));
            }

            var engineVersion = reader.ReadInt32();
            var cookerVersion = reader.ReadInt32();
            var compressionFlagsOffset = stream.Position;
            var compressionFlags = reader.ReadUInt32();
            var compressedChunkCountOffset = stream.Position;
            var compressedChunkCount = ReadBoundedCount(
                reader,
                "compressed chunk",
                PackageConstants.MaxReasonableCompressedChunks);

            var compressedChunkTableOffset = stream.Position;
            var compressedChunks = new List<CompressedChunk>(compressedChunkCount);
            for (var index = 0; index < compressedChunkCount; index++)
            {
                compressedChunks.Add(new CompressedChunk(
                    reader.ReadUInt32(),
                    reader.ReadUInt32(),
                    reader.ReadUInt32(),
                    reader.ReadUInt32(),
                    reader.ReadUInt32()));
            }

            var packageSourceOffset = stream.Position;
            var packageSource = reader.ReadUInt32();

            return new PackageSummary
            {
                FilePath = fullPath,
                PhysicalFileSize = stream.Length,
                Tag = tag,
                FileVersion = fileVersion,
                LicenseeVersion = licenseeVersion,
                HeaderSize = headerSize,
                FolderName = folderName,
                PackageFlags = packageFlags,
                NameCount = nameCount,
                NameOffset = nameOffset,
                ExportCount = exportCount,
                ExportOffset = exportOffset,
                ImportCount = importCount,
                ImportOffset = importOffset,
                DependsOffset = dependsOffset,
                ImportExportGuidsOffset = importExportGuidsOffset,
                ImportGuidsCount = importGuidsCount,
                ExportGuidsCount = exportGuidsCount,
                ThumbnailTableOffset = thumbnailTableOffset,
                PackageGuid = packageGuid,
                Generations = generations,
                EngineVersion = engineVersion,
                CookerVersion = cookerVersion,
                CompressionFlags = compressionFlags,
                CompressionFlagsOffset = compressionFlagsOffset,
                CompressedChunkCountOffset = compressedChunkCountOffset,
                CompressedChunkTableOffset = compressedChunkTableOffset,
                CompressedChunks = compressedChunks,
                PackageSource = packageSource,
                PackageSourceOffset = packageSourceOffset,
                ParsedSummarySize = stream.Position,
            };
        }
        catch (EndOfStreamException exception)
        {
            throw new PackageFormatException(
                $"Package summary is truncated at physical offset 0x{stream.Position:X}.",
                exception);
        }
    }

    private static int ReadTableCount(BinaryReader reader, string label) =>
        ReadBoundedCount(reader, label, PackageConstants.MaxReasonableTableEntries);

    private static int ReadBoundedCount(BinaryReader reader, string label, int max)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > max)
        {
            throw new PackageFormatException(
                $"Invalid {label} count {count:N0} at physical offset 0x{reader.BaseStream.Position - 4:X}.");
        }

        return count;
    }

    private static string ReadUnrealString(BinaryReader reader)
    {
        var characterCount = reader.ReadInt32();
        if (characterCount == 0)
        {
            return string.Empty;
        }

        if (characterCount == int.MinValue ||
            Math.Abs(characterCount) > PackageConstants.MaxReasonableStringCharacters)
        {
            throw new PackageFormatException(
                $"Invalid Unreal string length {characterCount:N0} at physical offset 0x{reader.BaseStream.Position - 4:X}.");
        }

        if (characterCount > 0)
        {
            var bytes = reader.ReadBytes(characterCount);
            if (bytes.Length != characterCount)
            {
                throw new EndOfStreamException();
            }

            var contentLength = bytes[^1] == 0 ? bytes.Length - 1 : bytes.Length;
            return Encoding.Latin1.GetString(bytes, 0, contentLength);
        }

        var utf16CharacterCount = -characterCount;
        var byteCount = checked(utf16CharacterCount * 2);
        var utf16Bytes = reader.ReadBytes(byteCount);
        if (utf16Bytes.Length != byteCount)
        {
            throw new EndOfStreamException();
        }

        var utf16ContentLength =
            utf16Bytes.Length >= 2 && utf16Bytes[^2] == 0 && utf16Bytes[^1] == 0
                ? utf16Bytes.Length - 2
                : utf16Bytes.Length;
        return Encoding.Unicode.GetString(utf16Bytes, 0, utf16ContentLength);
    }

    private static Guid ReadUnrealGuid(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16)
        {
            throw new EndOfStreamException();
        }

        return new Guid(bytes);
    }
}
