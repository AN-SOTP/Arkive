using System.Buffers.Binary;
using System.Text;
using Arkive.Core.Assets;

namespace Arkive.Core.Packages;

/// <summary>An export moved to the end of the package to make room for the grown header.</summary>
public sealed record PackageRelocatedExport(int ExportIndex, string ObjectName, string ClassName, int SourceOffset, int OutputOffset, int Size);

/// <summary>A summary field the grown header changes, at its offset in the package.</summary>
public sealed record PackageSummaryPatch(string Field, long Offset, int SourceValue, int OutputValue);

/// <summary>
/// A package with names and imports added: the new logical image and what changed in it. The name and import tables keep
/// every original entry where it was, in order, and the new ones follow; the export table and everything after it move
/// down by the added bytes, and the exports the grown header now covers are moved, unchanged, to the end.
/// </summary>
public sealed record PackageImportGrowthPlan(
    byte[] Logical,
    int AddedNames,
    int AddedImports,
    long HeaderStart,
    long SourceHeaderEnd,
    long OutputHeaderEnd,
    IReadOnlyList<PackageSummaryPatch> SummaryPatches,
    IReadOnlyList<PackageRelocatedExport> Relocated,
    IReadOnlyDictionary<string, int> Imports);

public sealed class PackageImportGrowthRefusalException(string code, string message) : NotSupportedException($"{code}: {message}")
{
    public string Code { get; } = code;
}

/// <summary>
/// Plans adding imports (and the names they need) to a cooked Lost Ark package, so one of its objects can reference an
/// object in another package. Only exports that hold no file offsets of their own (material instances, redirectors,
/// group packages) are ever moved; anything else in the way refuses.
/// </summary>
public static class PackageImportGrowth
{
    public const int AppendAlignment = 4;

    private static readonly HashSet<string> MovableClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "materialinstanceconstant", "objectredirector", "package",
    };

    /// <summary>The plan, or null when the package already imports every reference.</summary>
    public static PackageImportGrowthPlan? Plan(AssetCatalog catalog, byte[] logical, IReadOnlyList<PackageObjectReference> references)
    {
        var summary = catalog.Summary;
        if (references.Any(reference => reference.Chain.Count < 2))
            Refuse("PIG001", "A material reference must name a package and an object in it.");
        var names = catalog.Names.Select(name => name.Value).ToList();
        var nameIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++) nameIndex.TryAdd(names[i], i);
        // New names take the flags the package gives its other names.
        var flags = catalog.Names.GroupBy(name => name.Flags).OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault();
        var addedNames = new List<string>();
        var imports = catalog.Imports.Select(import => (import.ClassPackage, import.ClassName, import.OuterIndex, import.ObjectName)).ToList();
        var resolved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        UnrealNameReference Name(string text)
        {
            var (value, number) = PackageObjectReference.SplitName(text);
            if (value.Length == 0 || value.Any(c => c is < ' ' or > '~'))
                Refuse("PIG002", $"'{text}' is not a name Arkive can add to a package.");
            if (!nameIndex.TryGetValue(value, out var index))
            {
                index = names.Count;
                names.Add(value);
                addedNames.Add(value);
                nameIndex[value] = index;
            }
            return new UnrealNameReference(index, number);
        }

        string Text(UnrealNameReference reference) => reference.Number == 0 ? names[reference.Index] : $"{names[reference.Index]}_{reference.Number - 1}";

        foreach (var reference in references)
        {
            var outer = 0;
            foreach (var link in reference.Chain)
            {
                var found = 0;
                for (var i = 0; i < imports.Count && found == 0; i++)
                {
                    var import = imports[i];
                    if (import.OuterIndex == outer && Text(import.ObjectName).Equals(link.Name, StringComparison.OrdinalIgnoreCase) &&
                        Text(import.ClassName).Equals(link.ClassName, StringComparison.OrdinalIgnoreCase) &&
                        Text(import.ClassPackage).Equals(link.ClassPackage, StringComparison.OrdinalIgnoreCase))
                        found = -(i + 1);
                }
                if (found == 0)
                {
                    imports.Add((Name(link.ClassPackage), Name(link.ClassName), outer, Name(link.Name)));
                    found = -imports.Count;
                }
                outer = found;
            }
            resolved[reference.Path] = outer;
        }
        var addedImports = imports.Count - catalog.Imports.Count;
        if (addedImports == 0) return null;

        // The tables must be the usual cooked layout: names, imports, exports, depends, then the GUID list up to the
        // header's end, back to back, with the first export right after the header.
        var nameBytes = checked(summary.ImportOffset - summary.NameOffset);
        var importBytes = checked(summary.ExportOffset - summary.ImportOffset);
        if (summary.NameOffset <= 0 || nameBytes <= 0 || importBytes != catalog.Imports.Count * 28 ||
            summary.DependsOffset <= summary.ExportOffset || summary.HeaderSize < summary.DependsOffset ||
            (summary.ImportExportGuidsOffset != 0 && (summary.ImportExportGuidsOffset < summary.DependsOffset ||
                                                      summary.ImportExportGuidsOffset > summary.HeaderSize)) ||
            summary.ThumbnailTableOffset != 0 || summary.HeaderSize > logical.Length)
            Refuse("PIG003", "The package's tables are not laid out the way Arkive can grow them.");
        if (summary.CompressedChunks.Count > 0 && summary.NameOffset < (long)summary.CompressedChunks[0].UncompressedOffset)
            Refuse("PIG004", "The package's name table is not inside its compressed data.");
        if (MeasureNames(logical, summary.NameOffset, summary.NameCount) != nameBytes)
            Refuse("PIG005", "The package's name table does not end where its import table starts.");

        using var tables = new MemoryStream();
        using (var writer = new BinaryWriter(tables, Encoding.Latin1, leaveOpen: true))
        {
            writer.Write(logical, summary.NameOffset, nameBytes);
            foreach (var name in addedNames)
            {
                writer.Write(name.Length + 1);
                writer.Write(Encoding.ASCII.GetBytes(name));
                writer.Write((byte)0);
                writer.Write(flags);
            }
            writer.Write(logical, summary.ImportOffset, importBytes);
            foreach (var import in imports.Skip(catalog.Imports.Count))
            {
                writer.Write(import.ClassPackage.Index); writer.Write(import.ClassPackage.Number);
                writer.Write(import.ClassName.Index); writer.Write(import.ClassName.Number);
                writer.Write(import.OuterIndex);
                writer.Write(import.ObjectName.Index); writer.Write(import.ObjectName.Number);
            }
        }
        var delta = checked((int)(tables.Length - nameBytes - importBytes));
        var addedNameBytes = delta - addedImports * 28;
        var outputHeaderEnd = checked(summary.HeaderSize + delta);

        // Exports the grown header now covers move to the end; they must cover that stretch with nothing in between.
        var firstExport = catalog.Exports.Where(export => export.SerialSize > 0).MinBy(export => export.SerialOffset);
        if (firstExport is null || firstExport.SerialOffset != summary.HeaderSize)
            Refuse("PIG006", "The package's first export does not start right after its header.");
        var moved = catalog.Exports.Where(export => export.SerialSize > 0 && export.SerialOffset < outputHeaderEnd)
            .OrderBy(export => export.SerialOffset).ToArray();
        long covered = summary.HeaderSize;
        foreach (var export in moved)
        {
            if (export.SerialOffset != covered)
                Refuse("PIG007", "Something other than whole exports sits right after the package's header.");
            covered = export.SerialOffset + (long)export.SerialSize;
            var className = catalog.Assets[export.TableIndex - 1].ClassName;
            if (!MovableClasses.Contains(className))
                Refuse("PIG008", $"Making room in the header would move {className} {export.ObjectName.Resolve(catalog.Names)}, which Arkive does not move.");
        }
        if (covered < outputHeaderEnd) Refuse("PIG007", "Something other than whole exports sits right after the package's header.");

        var end = AlignUp(logical.LongLength);
        var relocated = new List<PackageRelocatedExport>();
        foreach (var export in moved)
        {
            relocated.Add(new PackageRelocatedExport(export.TableIndex, export.ObjectName.Resolve(catalog.Names),
                catalog.Assets[export.TableIndex - 1].ClassName, export.SerialOffset, checked((int)end), export.SerialSize));
            end = AlignUp(end + export.SerialSize);
        }
        var output = new byte[checked((int)(relocated[^1].OutputOffset + (long)relocated[^1].Size))];
        logical.CopyTo(output, 0);
        foreach (var item in relocated) logical.AsSpan(item.SourceOffset, item.Size).CopyTo(output.AsSpan(item.OutputOffset));

        // The header: grown names and imports, then the export table (moved exports pointing at their new place), the
        // depends map and the GUID list, all unchanged otherwise.
        var exportTable = logical.AsSpan(summary.ExportOffset, summary.DependsOffset - summary.ExportOffset).ToArray();
        var offsets = ExportOffsetFields(exportTable, catalog.Exports.Count);
        foreach (var item in relocated)
        {
            var field = offsets[item.ExportIndex - 1];
            if (BinaryPrimitives.ReadInt32LittleEndian(exportTable.AsSpan(field)) != item.SourceOffset)
                Refuse("PIG009", $"Export {item.ExportIndex}'s table entry does not match the catalogue.");
            BinaryPrimitives.WriteInt32LittleEndian(exportTable.AsSpan(field), item.OutputOffset);
        }
        var position = summary.NameOffset;
        tables.ToArray().CopyTo(output, position);
        position += checked((int)tables.Length);
        exportTable.CopyTo(output, position);
        position += exportTable.Length;
        logical.AsSpan(summary.DependsOffset, summary.HeaderSize - summary.DependsOffset).CopyTo(output.AsSpan(position));
        position += summary.HeaderSize - summary.DependsOffset;
        if (position != outputHeaderEnd) Refuse("PIG010", "The grown header did not come out at its planned size.");

        var fields = SummaryFields(logical);
        var patches = new List<PackageSummaryPatch>
        {
            new("HeaderSize", fields.HeaderSize, summary.HeaderSize, outputHeaderEnd),
            new("NameCount", fields.NameCount, summary.NameCount, names.Count),
            new("ImportCount", fields.ImportCount, summary.ImportCount, imports.Count),
            new("ImportOffset", fields.ImportOffset, summary.ImportOffset, summary.ImportOffset + addedNameBytes),
            new("ExportOffset", fields.ExportOffset, summary.ExportOffset, summary.ExportOffset + delta),
            new("DependsOffset", fields.DependsOffset, summary.DependsOffset, summary.DependsOffset + delta),
        };
        if (summary.ImportExportGuidsOffset != 0)
            patches.Add(new("ImportExportGuidsOffset", fields.ImportExportGuidsOffset, summary.ImportExportGuidsOffset, summary.ImportExportGuidsOffset + delta));
        if (summary.Generations.Count > 0 && summary.Generations[^1].NameCount == summary.NameCount)
            patches.Add(new("LatestGenerationNameCount", fields.LatestGenerationNameCount, summary.NameCount, names.Count));
        foreach (var patch in patches)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(logical.AsSpan(checked((int)patch.Offset))) != patch.SourceValue)
                Refuse("PIG011", $"The package summary's {patch.Field} is not where Arkive expects it.");
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(checked((int)patch.Offset)), patch.OutputValue);
        }
        return new PackageImportGrowthPlan(output, addedNames.Count, addedImports, summary.NameOffset, summary.HeaderSize, outputHeaderEnd,
            patches, relocated, resolved);
    }

    private static int MeasureNames(byte[] logical, int offset, int count)
    {
        var position = offset;
        for (var i = 0; i < count; i++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(logical.AsSpan(position));
            position += 4 + (length >= 0 ? length : checked(-length * 2)) + 8;
        }
        return position - offset;
    }

    /// <summary>Where each export's SerialOffset field sits within the export table.</summary>
    private static int[] ExportOffsetFields(byte[] table, int count)
    {
        var fields = new int[count];
        var position = 0;
        for (var i = 0; i < count; i++)
        {
            fields[i] = position + 36;
            var netCount = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(position + 44));
            position += 48 + netCount * 4 + 16 + 4;
        }
        if (position != table.Length) Refuse("PIG012", "The export table does not end where the depends map starts.");
        return fields;
    }

    private sealed record Fields(long HeaderSize, long NameCount, long ImportCount, long ImportOffset, long ExportOffset, long DependsOffset,
        long ImportExportGuidsOffset, long LatestGenerationNameCount);

    /// <summary>Offsets of the summary fields that follow the folder name.</summary>
    private static Fields SummaryFields(byte[] logical)
    {
        var folderLength = BinaryPrimitives.ReadInt32LittleEndian(logical.AsSpan(12));
        long at = 16 + (folderLength >= 0 ? folderLength : checked(-folderLength * 2L));
        // flags, then name count/offset, export count/offset, import count/offset, depends, import/export GUIDs offset,
        // import and export GUID counts, thumbnail table, package GUID, generation count and generations.
        var generations = BinaryPrimitives.ReadInt32LittleEndian(logical.AsSpan(checked((int)(at + 64))));
        return new Fields(8, at + 4, at + 20, at + 24, at + 16, at + 28, at + 32,
            at + 68 + (generations - 1) * 12L + 4);
    }

    private static long AlignUp(long value) => (value + AppendAlignment - 1) / AppendAlignment * AppendAlignment;

    private static void Refuse(string code, string message) => throw new PackageImportGrowthRefusalException(code, message);
}
