using System.Text;
using Arkive.Core.Archives;
using Arkive.Core.Packages;

namespace Arkive.Core.Assets;

public sealed class AssetCatalogReader
{
    private const int MaximumNetObjectCounts = 1_000_000;

    private readonly UpkPackageReader _summaryReader;
    private readonly IChunkProtectionProvider _protectionProvider;

    public AssetCatalogReader(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _summaryReader = summaryReader ?? new UpkPackageReader();
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
    }

    public AssetCatalog Read(string filePath)
    {
        var summary = _summaryReader.Read(filePath);
        return Read(summary);
    }

    public AssetCatalog Read(PackageSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!summary.IsWesternLostArkProfile)
        {
            throw new PackageFormatException(
                "The asset catalogue currently supports only the Western Lost Ark 868/16 profile.");
        }

        using var logicalStream = new LogicalPackageStream(summary, _protectionProvider);
        using var reader = new BinaryReader(logicalStream, Encoding.UTF8, leaveOpen: true);

        var names = ReadNames(reader, summary);
        var imports = ReadImports(reader, summary, names);
        var exports = ReadExports(reader, summary, names);
        var assets = BuildAssetEntries(summary, names, imports, exports);

        return new AssetCatalog
        {
            Summary = summary,
            Names = names,
            Imports = imports,
            Exports = exports,
            Assets = assets,
            ProtectionProviderName = logicalStream.ProtectionProviderName,
        };
    }

    private static IReadOnlyList<UnrealNameEntry> ReadNames(
        BinaryReader reader,
        PackageSummary summary)
    {
        reader.BaseStream.Position = summary.NameOffset;
        var names = new List<UnrealNameEntry>(summary.NameCount);
        for (var index = 0; index < summary.NameCount; index++)
        {
            var value = ReadUnrealString(reader);
            var flags = reader.ReadUInt64();
            names.Add(new UnrealNameEntry(index, value, flags));
        }

        return names;
    }

    private static IReadOnlyList<UnrealObjectImport> ReadImports(
        BinaryReader reader,
        PackageSummary summary,
        IReadOnlyList<UnrealNameEntry> names)
    {
        reader.BaseStream.Position = summary.ImportOffset;
        var imports = new List<UnrealObjectImport>(summary.ImportCount);
        for (var index = 0; index < summary.ImportCount; index++)
        {
            var item = new UnrealObjectImport(
                TableIndex: -(index + 1),
                ClassPackage: ReadNameReference(reader, names, "import class package"),
                ClassName: ReadNameReference(reader, names, "import class"),
                OuterIndex: reader.ReadInt32(),
                ObjectName: ReadNameReference(reader, names, "import object"));
            imports.Add(item);
        }

        return imports;
    }

    private static IReadOnlyList<UnrealObjectExport> ReadExports(
        BinaryReader reader,
        PackageSummary summary,
        IReadOnlyList<UnrealNameEntry> names)
    {
        reader.BaseStream.Position = summary.ExportOffset;
        var exports = new List<UnrealObjectExport>(summary.ExportCount);
        for (var index = 0; index < summary.ExportCount; index++)
        {
            var classIndex = reader.ReadInt32();
            var superIndex = reader.ReadInt32();
            var outerIndex = reader.ReadInt32();
            var objectName = ReadNameReference(reader, names, "export object");
            var archetypeIndex = reader.ReadInt32();
            var flagsLow = reader.ReadUInt32();
            var flagsHigh = reader.ReadUInt32();
            var serialSize = reader.ReadInt32();
            var serialOffset = reader.ReadInt32();
            var exportFlags = reader.ReadUInt32();
            var netObjectCount = ReadBoundedCount(
                reader,
                "export net-object",
                MaximumNetObjectCounts);
            var netObjectCounts = new int[netObjectCount];
            for (var netIndex = 0; netIndex < netObjectCount; netIndex++)
            {
                netObjectCounts[netIndex] = reader.ReadInt32();
            }

            var guidBytes = reader.ReadBytes(16);
            if (guidBytes.Length != 16)
            {
                throw new EndOfStreamException();
            }

            var unknownTail = reader.ReadInt32();
            if (serialSize < 0 || serialOffset < 0 ||
                (serialSize > 0 && (long)serialOffset + serialSize > reader.BaseStream.Length))
            {
                throw new PackageFormatException(
                    $"Export {index + 1} has invalid logical payload range " +
                    $"0x{serialOffset:X}+0x{serialSize:X}.");
            }

            exports.Add(new UnrealObjectExport(
                TableIndex: index + 1,
                ClassIndex: classIndex,
                SuperIndex: superIndex,
                OuterIndex: outerIndex,
                ObjectName: objectName,
                ArchetypeIndex: archetypeIndex,
                ObjectFlags: flagsLow | ((ulong)flagsHigh << 32),
                SerialSize: serialSize,
                SerialOffset: serialOffset,
                ExportFlags: exportFlags,
                NetObjectCounts: netObjectCounts,
                PackageGuid: new Guid(guidBytes),
                UnknownTail: unknownTail));
        }

        return exports;
    }

    private static IReadOnlyList<AssetCatalogEntry> BuildAssetEntries(
        PackageSummary summary,
        IReadOnlyList<UnrealNameEntry> names,
        IReadOnlyList<UnrealObjectImport> imports,
        IReadOnlyList<UnrealObjectExport> exports)
    {
        var packageName = Path.GetFileNameWithoutExtension(summary.FilePath);
        var entries = new AssetCatalogEntry[exports.Count];
        for (var index = 0; index < exports.Count; index++)
        {
            var item = exports[index];
            var objectName = item.ObjectName.Resolve(names);
            var outerPath = ResolveObjectPath(
                item.OuterIndex,
                names,
                imports,
                exports,
                new HashSet<int>());
            var path = string.IsNullOrEmpty(outerPath)
                ? $"{packageName}.{objectName}"
                : $"{packageName}.{outerPath}.{objectName}";
            var className = ResolveClassName(item.ClassIndex, names, imports, exports);

            entries[index] = new AssetCatalogEntry(
                item.TableIndex,
                objectName,
                path,
                className,
                InferKind(className),
                item.SerialSize,
                item.SerialOffset);
        }

        return entries;
    }

    private static string ResolveClassName(
        int classIndex,
        IReadOnlyList<UnrealNameEntry> names,
        IReadOnlyList<UnrealObjectImport> imports,
        IReadOnlyList<UnrealObjectExport> exports)
    {
        if (classIndex < 0)
        {
            var index = -classIndex - 1;
            return index < imports.Count
                ? imports[index].ObjectName.Resolve(names)
                : $"<invalid-import:{classIndex}>";
        }

        if (classIndex > 0)
        {
            var index = classIndex - 1;
            return index < exports.Count
                ? exports[index].ObjectName.Resolve(names)
                : $"<invalid-export:{classIndex}>";
        }

        return "Class";
    }

    private static string ResolveObjectPath(
        int packageIndex,
        IReadOnlyList<UnrealNameEntry> names,
        IReadOnlyList<UnrealObjectImport> imports,
        IReadOnlyList<UnrealObjectExport> exports,
        HashSet<int> visited)
    {
        if (packageIndex == 0)
        {
            return string.Empty;
        }

        if (!visited.Add(packageIndex) || visited.Count > 128)
        {
            return "<outer-cycle>";
        }

        int outerIndex;
        string name;
        if (packageIndex < 0)
        {
            var index = -packageIndex - 1;
            if (index >= imports.Count)
            {
                return $"<invalid-import:{packageIndex}>";
            }

            var item = imports[index];
            outerIndex = item.OuterIndex;
            name = item.ObjectName.Resolve(names);
        }
        else
        {
            var index = packageIndex - 1;
            if (index >= exports.Count)
            {
                return $"<invalid-export:{packageIndex}>";
            }

            var item = exports[index];
            outerIndex = item.OuterIndex;
            name = item.ObjectName.Resolve(names);
        }

        var outer = ResolveObjectPath(outerIndex, names, imports, exports, visited);
        return string.IsNullOrEmpty(outer) ? name : $"{outer}.{name}";
    }

    private static AssetKind InferKind(string className) => AssetTypes.CatalogKind(className);

    private static UnrealNameReference ReadNameReference(
        BinaryReader reader,
        IReadOnlyList<UnrealNameEntry> names,
        string label)
    {
        var reference = new UnrealNameReference(reader.ReadInt32(), reader.ReadInt32());
        if (reference.Index < 0 || reference.Index >= names.Count || reference.Number < 0)
        {
            throw new PackageFormatException(
                $"The {label} name reference ({reference.Index}, {reference.Number}) is invalid.");
        }

        return reference;
    }

    private static int ReadBoundedCount(BinaryReader reader, string label, int maximum)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum)
        {
            throw new PackageFormatException($"Invalid {label} count {count:N0}.");
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
            throw new PackageFormatException($"Invalid Unreal string length {characterCount:N0}.");
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
        var bytesUtf16 = reader.ReadBytes(byteCount);
        if (bytesUtf16.Length != byteCount)
        {
            throw new EndOfStreamException();
        }

        var contentBytes =
            bytesUtf16.Length >= 2 && bytesUtf16[^2] == 0 && bytesUtf16[^1] == 0
                ? bytesUtf16.Length - 2
                : bytesUtf16.Length;
        return Encoding.Unicode.GetString(bytesUtf16, 0, contentBytes);
    }
}
