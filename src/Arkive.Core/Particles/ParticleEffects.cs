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
/// What a particle edit does to an effect: an optional tint (blended in by <paramref name="TintStrength"/>), then
/// multipliers on its colour brightness, opacity, particle size, particle lifetime, spawn rate and starting speed.
/// </summary>
public sealed record ParticleEffectRecipe(
    ParticleEffectTint? Tint = null,
    float TintStrength = 1,
    float Brightness = 1,
    float Opacity = 1,
    float Size = 1,
    float Lifetime = 1,
    float SpawnRate = 1,
    float Speed = 1)
{
    public const float MaximumMultiplier = 20;

    public static ParticleEffectRecipe Unchanged { get; } = new();

    public bool IsValid => (Tint is null || Tint.IsValid) && float.IsFinite(TintStrength) && TintStrength is >= 0 and <= 1 &&
                           new[] { Brightness, Opacity, Size, Lifetime, SpawnRate, Speed }
                               .All(value => float.IsFinite(value) && value is >= 0 and <= MaximumMultiplier);

    public bool IsUnchanged => (Tint is null || TintStrength == 0) &&
                               Brightness == 1 && Opacity == 1 && Size == 1 && Lifetime == 1 && SpawnRate == 1 && Speed == 1;
}

/// <summary>Which setting a value table holds, and so which part of a recipe changes it.</summary>
public enum ParticleTableKind
{
    Color,
    Alpha,
    Size,
    Lifetime,
    SpawnRate,
    Speed,
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

/// <summary>A value table found in a particle system: the module it is in, what it holds and its floats.</summary>
public sealed record ParticleValueTable(
    AssetCatalogEntry Export,
    string ExportSha256,
    string Property,
    ParticleTableKind Kind,
    int ValueOffset,
    float[] Values);

/// <summary>A particle system in a package and the value tables its emitters use.</summary>
public sealed record ParticleEffectReport(
    string PackagePath,
    string PackageSha256,
    AssetCatalogEntry System,
    string SystemExportSha256,
    int ExportCount,
    IReadOnlyList<ParticleValueTable> Tables);

/// <summary>
/// Finds particle systems' value tables and edits them. A system's emitters, LOD levels and modules are the exports
/// inside it (their object path starts with the system's). Each setting is a module's RawDistributionFloat or
/// RawDistributionVector property; its LookupTable is two header floats, then the samples (a uniform range stores its
/// low and high value per sample). Colours are vector tables whose name holds "color" (StartColor, ColorOverLife,
/// ColorScaleOverLife…), opacity float tables whose name holds "alpha"; size is StartSize, lifetime Lifetime, spawn rate
/// Rate or SpawnRate, speed StartVelocity and StartVelocityRadial.
/// </summary>
public static class ParticleEffects
{
    public static ParticleEffectReport Inspect(
        string packagePath, string packageSha256, string systemObjectName, string? systemObjectPath,
        IChunkProtectionProvider protectionProvider, CancellationToken cancellationToken = default)
    {
        var catalog = new AssetCatalogReader(protectionProvider: protectionProvider).Read(packagePath);
        var system = FindSystem(catalog, packagePath, systemObjectName, systemObjectPath);
        using var logical = new LogicalPackageStream(catalog.Summary, protectionProvider);
        var members = Members(catalog, system);
        var tables = new List<ParticleValueTable>();
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!member.ClassName.Contains("module", StringComparison.OrdinalIgnoreCase)) continue;
            var bytes = Read(logical, member);
            var properties = ParticlePropertyStream.Read(bytes, catalog.Names);
            if (properties is null) continue;
            string? hash = null;
            foreach (var property in properties)
            {
                if (KindOf(property) is not { } kind) continue;
                if (property.Child("lookuptable") is not { Type: "arrayproperty" } table || table.Size < 4) continue;
                var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(table.ValueOffset));
                var vector = IsVector(property);
                if (count < 3 || table.Size != 4 + count * 4 || (vector && (count - 2) % 3 != 0)) continue;
                var values = new float[count];
                for (var index = 0; index < count; index++)
                    values[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(table.ValueOffset + 4 + index * 4));
                hash ??= Convert.ToHexString(SHA256.HashData(bytes));
                tables.Add(new ParticleValueTable(member, hash, property.Name, kind, table.ValueOffset + 4, values));
            }
        }
        return new ParticleEffectReport(Path.GetFullPath(packagePath), packageSha256, system,
            Convert.ToHexString(SHA256.HashData(Read(logical, system))), members.Count, tables);
    }

    internal static AssetCatalogEntry FindSystem(AssetCatalog catalog, string packagePath, string systemObjectName, string? systemObjectPath)
    {
        var systems = catalog.Assets.Where(asset => asset.ClassName.Equals("particlesystem", StringComparison.OrdinalIgnoreCase) &&
                                                     asset.ObjectName.Equals(systemObjectName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (systemObjectPath is not null)
            systems = systems.Where(asset => asset.ObjectPath.Equals(systemObjectPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        return systems.Length == 1
            ? systems[0]
            : throw new InvalidDataException($"{systemObjectName} is {(systems.Length == 0 ? "not" : "more than once")} a particle system in {Path.GetFileName(packagePath)}.");
    }

    internal static IReadOnlyList<AssetCatalogEntry> Members(AssetCatalog catalog, AssetCatalogEntry system)
    {
        var prefix = system.ObjectPath + ".";
        return catalog.Assets.Where(asset => asset.ObjectPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => asset.ExportIndex).ToArray();
    }

    /// <summary>The setting a module property holds, or null when no recipe part changes it.</summary>
    public static ParticleTableKind? KindOf(ParticleProperty property)
    {
        if (property.Type != "structproperty") return null;
        var name = property.Name;
        if (IsVector(property))
        {
            if (name.Contains("color", StringComparison.OrdinalIgnoreCase)) return ParticleTableKind.Color;
            if (name.Equals("startsize", StringComparison.OrdinalIgnoreCase)) return ParticleTableKind.Size;
            if (name.Equals("startvelocity", StringComparison.OrdinalIgnoreCase)) return ParticleTableKind.Speed;
            return null;
        }
        if (!string.Equals(property.StructName, "rawdistributionfloat", StringComparison.OrdinalIgnoreCase)) return null;
        if (name.Contains("alpha", StringComparison.OrdinalIgnoreCase)) return ParticleTableKind.Alpha;
        if (name.Equals("lifetime", StringComparison.OrdinalIgnoreCase)) return ParticleTableKind.Lifetime;
        if (name.Equals("rate", StringComparison.OrdinalIgnoreCase) || name.Equals("spawnrate", StringComparison.OrdinalIgnoreCase))
            return ParticleTableKind.SpawnRate;
        if (name.Equals("startvelocityradial", StringComparison.OrdinalIgnoreCase)) return ParticleTableKind.Speed;
        return null;
    }

    internal static bool IsVector(ParticleProperty property) =>
        string.Equals(property.StructName, "rawdistributionvector", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The table recoloured to <paramref name="tint"/>. Its first two floats are left as they are: the game fills them
    /// with a range that does not always match the samples (uniform ranges keep other values there).
    /// </summary>
    public static float[] Tint(float[] table, ParticleEffectTint tint, float strength = 1)
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
            result[index] += (strongest * red - result[index]) * strength;
            result[index + 1] += (strongest * green - result[index + 1]) * strength;
            result[index + 2] += (strongest * blue - result[index + 2]) * strength;
        }
        return result;
    }

    /// <summary>The table with the recipe applied: colours tinted, then every table scaled by its kind's multiplier.</summary>
    public static float[] Apply(ParticleValueTable table, ParticleEffectRecipe recipe)
    {
        var values = table.Kind == ParticleTableKind.Color && recipe.Tint is { } tint && recipe.TintStrength > 0
            ? Tint(table.Values, tint, recipe.TintStrength)
            : (float[])table.Values.Clone();
        // Brightness and opacity scale an emitter's base colour (StartColor, ColorOverLife…) once; its scale-over-life
        // tables multiply that base, so scaling them too would apply the change twice.
        var multiplierOfBase = !table.Property.Contains("scale", StringComparison.OrdinalIgnoreCase);
        var scale = table.Kind switch
        {
            ParticleTableKind.Color => multiplierOfBase ? recipe.Brightness : 1,
            ParticleTableKind.Alpha => multiplierOfBase ? recipe.Opacity : 1,
            ParticleTableKind.Size => recipe.Size,
            ParticleTableKind.Lifetime => recipe.Lifetime,
            ParticleTableKind.SpawnRate => recipe.SpawnRate,
            _ => recipe.Speed,
        };
        // A multiplier scales the header range with the samples; a tint leaves the header alone.
        if (scale != 1)
            for (var index = 0; index < values.Length; index++)
                values[index] *= scale;
        return values;
    }

    /// <summary>The patches that apply the recipe to every table of the report it changes.</summary>
    public static IReadOnlyList<ParticleEffectPatch> RecipePatches(ParticleEffectReport report, ParticleEffectRecipe recipe)
    {
        if (!recipe.IsValid) throw new ArgumentException("The particle edit has a value out of range.", nameof(recipe));
        return report.Tables
            .Select(table => (Table: table, Value: Apply(table, recipe)))
            .Where(item => !item.Value.Select(BitConverter.SingleToInt32Bits).SequenceEqual(item.Table.Values.Select(BitConverter.SingleToInt32Bits)))
            .Select(item => new ParticleEffectPatch(
                item.Table.Export.ObjectPath, item.Table.Export.ClassName, item.Table.Export.ExportIndex,
                item.Table.Export.SerialOffset, item.Table.Export.SerialSize, item.Table.ExportSha256,
                item.Table.Property, item.Table.ValueOffset, item.Table.Values, item.Value))
            .ToArray();
    }

    /// <summary>Identifies what a set of patches writes: changes whenever any patched value does.</summary>
    public static string ContentSha256(IEnumerable<ParticleEffectPatch> patches) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|",
            patches.OrderBy(patch => patch.ObjectPath, StringComparer.OrdinalIgnoreCase).ThenBy(patch => patch.ValueOffset)
                .Select(patch => $"{patch.ObjectPath.ToUpperInvariant()}:{patch.ValueOffset}:" +
                                 string.Join(",", patch.Value.Select(BitConverter.SingleToInt32Bits)))))));

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

    internal static byte[] Read(LogicalPackageStream logical, AssetCatalogEntry export)
    {
        var bytes = new byte[export.SerialSize];
        logical.Position = export.SerialOffset;
        logical.ReadExactly(bytes);
        return bytes;
    }
}
