using System.Buffers.Binary;
using System.Security.Cryptography;
using Arkive.Core.Archives;
using Arkive.Core.Assets;

namespace Arkive.Core.Particles;

/// <summary>
/// A colour a particle effect is tinted to. Each colour of the effect keeps its brightness (its strongest channel) and
/// takes this colour's hue; colours that are fully negative (used to darken) are left alone.
/// </summary>
public sealed record ParticleEffectTint(float Red, float Green, float Blue)
{
    public bool IsValid => float.IsFinite(Red) && float.IsFinite(Green) && float.IsFinite(Blue) &&
                           Red >= 0 && Green >= 0 && Blue >= 0 && Math.Max(Red, Math.Max(Green, Blue)) > 0;
}

/// <summary>
/// One value table of one particle export that an edit rewrites: the floats of a RawDistribution's baked LookupTable,
/// which the game reads directly (its Distribution object is not used at run time).
/// </summary>
/// <param name="ValueOffset">Where the first float sits, counted from the start of the export.</param>
public sealed record ParticleEffectPatch(
    string ObjectPath,
    string ClassName,
    int ExportIndex,
    long SerialOffset,
    int SerialSize,
    string SourceExportSha256,
    string Property,
    int ValueOffset,
    float[] Original,
    float[] Value);

/// <summary>A colour table found in a particle system: the module it is in and its floats.</summary>
public sealed record ParticleColorTable(
    AssetCatalogEntry Export,
    string ExportSha256,
    string Property,
    int ValueOffset,
    float[] Values);

/// <summary>A particle system in a package and the colour tables its emitters use.</summary>
public sealed record ParticleEffectReport(
    string PackagePath,
    string PackageSha256,
    AssetCatalogEntry System,
    string SystemExportSha256,
    int ExportCount,
    IReadOnlyList<ParticleColorTable> ColorTables);

/// <summary>
/// Finds particle systems' colour tables and recolours them. A system's emitters, LOD levels and modules are the
/// exports inside it (their object path starts with the system's). Colours live in the modules' RawDistributionVector
/// properties whose name holds "color" (StartColor, ColorOverLife, ColorScaleOverLife…); each LookupTable is two
/// header floats, then red, green, blue per sample (a uniform range stores its low and high colour per sample).
/// </summary>
public static class ParticleEffects
{
    public static ParticleEffectReport Inspect(
        string packagePath, string packageSha256, string systemObjectName, string? systemObjectPath,
        IChunkProtectionProvider protectionProvider, CancellationToken cancellationToken = default)
    {
        var catalog = new AssetCatalogReader(protectionProvider: protectionProvider).Read(packagePath);
        var systems = catalog.Assets.Where(asset => asset.ClassName.Equals("particlesystem", StringComparison.OrdinalIgnoreCase) &&
                                                     asset.ObjectName.Equals(systemObjectName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (systemObjectPath is not null)
            systems = systems.Where(asset => asset.ObjectPath.Equals(systemObjectPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (systems.Length != 1)
            throw new InvalidDataException($"{systemObjectName} is {(systems.Length == 0 ? "not" : "more than once")} a particle system in {Path.GetFileName(packagePath)}.");
        var system = systems[0];
        using var logical = new LogicalPackageStream(catalog.Summary, protectionProvider);
        var prefix = system.ObjectPath + ".";
        var members = catalog.Assets.Where(asset => asset.ObjectPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => asset.ExportIndex).ToArray();
        var tables = new List<ParticleColorTable>();
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!member.ClassName.Contains("module", StringComparison.OrdinalIgnoreCase)) continue;
            var bytes = Read(logical, member);
            var properties = ParticlePropertyStream.Read(bytes, catalog.Names);
            if (properties is null) continue;
            string? hash = null;
            foreach (var property in properties.Where(IsColorDistribution))
            {
                if (property.Child("lookuptable") is not { Type: "arrayproperty" } table || table.Size < 4) continue;
                var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(table.ValueOffset));
                if (count < 5 || (count - 2) % 3 != 0 || table.Size != 4 + count * 4) continue;
                var values = new float[count];
                for (var index = 0; index < count; index++)
                    values[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(table.ValueOffset + 4 + index * 4));
                hash ??= Convert.ToHexString(SHA256.HashData(bytes));
                tables.Add(new ParticleColorTable(member, hash, property.Name, table.ValueOffset + 4, values));
            }
        }
        return new ParticleEffectReport(Path.GetFullPath(packagePath), packageSha256, system,
            Convert.ToHexString(SHA256.HashData(Read(logical, system))), members.Length, tables);
    }

    private static bool IsColorDistribution(ParticleProperty property) =>
        property.Type == "structproperty" &&
        string.Equals(property.StructName, "rawdistributionvector", StringComparison.OrdinalIgnoreCase) &&
        property.Name.Contains("color", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The table recoloured to <paramref name="tint"/>. Its first two floats are left as they are: the game fills them
    /// with a range that does not always match the samples (uniform ranges keep other values there).
    /// </summary>
    public static float[] Tint(float[] table, ParticleEffectTint tint)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!tint.IsValid) throw new ArgumentException("A tint needs finite, non-negative channels and some colour.", nameof(tint));
        var peak = Math.Max(tint.Red, Math.Max(tint.Green, tint.Blue));
        var (red, green, blue) = (tint.Red / peak, tint.Green / peak, tint.Blue / peak);
        var result = (float[])table.Clone();
        for (var index = 2; index + 2 < result.Length; index += 3)
        {
            var strongest = Math.Max(result[index], Math.Max(result[index + 1], result[index + 2]));
            if (strongest <= 0) continue;
            result[index] = strongest * red;
            result[index + 1] = strongest * green;
            result[index + 2] = strongest * blue;
        }
        return result;
    }

    /// <summary>The patches that tint every colour table of the report.</summary>
    public static IReadOnlyList<ParticleEffectPatch> TintPatches(ParticleEffectReport report, ParticleEffectTint tint) =>
        report.ColorTables
            .Select(table => (Table: table, Value: Tint(table.Values, tint)))
            .Where(item => !item.Value.SequenceEqual(item.Table.Values))
            .Select(item => new ParticleEffectPatch(
                item.Table.Export.ObjectPath, item.Table.Export.ClassName, item.Table.Export.ExportIndex,
                item.Table.Export.SerialOffset, item.Table.Export.SerialSize, item.Table.ExportSha256,
                item.Table.Property, item.Table.ValueOffset, item.Table.Values, item.Value))
            .ToArray();

    /// <summary>Identifies what a set of patches writes: changes whenever any patched value does.</summary>
    public static string ContentSha256(IEnumerable<ParticleEffectPatch> patches) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|",
            patches.OrderBy(patch => patch.ObjectPath, StringComparer.OrdinalIgnoreCase).ThenBy(patch => patch.ValueOffset)
                .Select(patch => $"{patch.ObjectPath.ToUpperInvariant()}:{patch.ValueOffset}:" +
                                 string.Join(",", patch.Value.Select(BitConverter.SingleToInt32Bits))))))) ;

    /// <summary>
    /// The export bytes with their patches written in, after checking each patch still finds its original values.
    /// </summary>
    public static byte[] ApplyPatches(ReadOnlySpan<byte> export, IEnumerable<ParticleEffectPatch> patches)
    {
        var result = export.ToArray();
        foreach (var patch in patches)
        {
            if (patch.Original.Length != patch.Value.Length || patch.ValueOffset < 0 ||
                patch.ValueOffset > result.Length - patch.Value.Length * 4)
                throw new InvalidDataException($"{patch.ObjectPath}'s {patch.Property} patch does not fit its export.");
            for (var index = 0; index < patch.Original.Length; index++)
            {
                var offset = patch.ValueOffset + index * 4;
                if (BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(offset)) != BitConverter.SingleToInt32Bits(patch.Original[index]))
                    throw new InvalidDataException($"{patch.ObjectPath}'s {patch.Property} no longer holds the values the edit was made for.");
                if (!float.IsFinite(patch.Value[index]))
                    throw new InvalidDataException($"{patch.ObjectPath}'s {patch.Property} patch has a value that is not a number.");
                BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(offset), patch.Value[index]);
            }
        }
        return result;
    }

    private static byte[] Read(LogicalPackageStream logical, AssetCatalogEntry export)
    {
        var bytes = new byte[export.SerialSize];
        logical.Position = export.SerialOffset;
        logical.ReadExactly(bytes);
        return bytes;
    }
}
