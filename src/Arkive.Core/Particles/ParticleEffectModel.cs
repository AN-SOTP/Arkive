using System.Buffers.Binary;
using Arkive.Core.Archives;
using Arkive.Core.Assets;

namespace Arkive.Core.Particles;

/// <summary>A baked value table as the preview samples it.</summary>
/// <param name="Dimensions">1 for a float setting, 3 for a vector or colour.</param>
/// <param name="ChunkSize">Floats per sample: Dimensions, or twice that for a uniform (random low–high) range.</param>
/// <param name="Kind">The recipe part that changes it, when one does.</param>
public sealed record ParticleTableModel(
    float[] Values,
    int Dimensions,
    int ChunkSize,
    float TimeScale,
    float StartTime,
    string? Kind);

/// <summary>One module of an emitter: its class and its settings by property name.</summary>
public sealed record ParticleModuleModel(string Class, string Name, IReadOnlyDictionary<string, object> Values);

/// <summary>One emitter at its first detail level, with the modules that shape its particles.</summary>
public sealed record ParticleEmitterModel(
    int Index,
    string Name,
    bool Enabled,
    string? Material,
    string? Mesh,
    IReadOnlyList<ParticleModuleModel> Modules);

/// <summary>What the particle preview plays: a system's emitters and their settings, as the game stores them.</summary>
public sealed record ParticleEffectModel(string System, string PackageAlias, IReadOnlyList<ParticleEmitterModel> Emitters);

public static class ParticleEffectModels
{
    /// <summary>Reads the system's emitters (first detail level) and their modules for the live preview.</summary>
    public static ParticleEffectModel Build(
        string packagePath, string packageAlias, string systemObjectName, string? systemObjectPath,
        IChunkProtectionProvider protectionProvider)
    {
        var catalog = new AssetCatalogReader(protectionProvider: protectionProvider).Read(packagePath);
        var system = ParticleEffects.FindSystem(catalog, packagePath, systemObjectName, systemObjectPath);
        var byIndex = catalog.Assets.ToDictionary(asset => asset.ExportIndex);
        using var logical = new LogicalPackageStream(catalog.Summary, protectionProvider);
        (byte[] Bytes, IReadOnlyList<ParticleProperty> Properties)? Load(int reference)
        {
            if (!byIndex.TryGetValue(reference, out var asset)) return null;
            var bytes = ParticleEffects.Read(logical, asset);
            return ParticlePropertyStream.Read(bytes, catalog.Names) is { } properties ? (bytes, properties) : null;
        }
        var systemData = Load(system.ExportIndex) ?? throw new InvalidDataException($"{systemObjectName}'s properties could not be read.");
        var emitters = new List<ParticleEmitterModel>();
        var emitterRefs = Find(systemData.Properties, "emitters") is { } list
            ? ParticlePropertyStream.ReadObjectArray(systemData.Bytes, list) ?? []
            : [];
        foreach (var emitterRef in emitterRefs)
        {
            if (Load(emitterRef) is not { } emitter) continue;
            var name = Find(emitter.Properties, "emittername") is { } nameProperty ? NameValue(emitter.Bytes, nameProperty, catalog) : $"Emitter {emitters.Count + 1}";
            var lods = Find(emitter.Properties, "lodlevels") is { } lodList ? ParticlePropertyStream.ReadObjectArray(emitter.Bytes, lodList) ?? [] : [];
            if (lods.Count == 0 || Load(lods[0]) is not { } lod) continue;
            var moduleRefs = new List<int>();
            foreach (var single in new[] { "requiredmodule", "spawnmodule", "typedatamodule" })
                if (Find(lod.Properties, single) is { Size: 4 } reference)
                    moduleRefs.Add(BinaryPrimitives.ReadInt32LittleEndian(lod.Bytes.AsSpan(reference.ValueOffset)));
            if (Find(lod.Properties, "modules") is { } modules)
                moduleRefs.AddRange(ParticlePropertyStream.ReadObjectArray(lod.Bytes, modules) ?? []);
            var enabled = Find(lod.Properties, "benabled") is not { } enabledProperty || lod.Bytes[enabledProperty.ValueOffset] != 0;
            var models = new List<ParticleModuleModel>();
            string? material = null, mesh = null;
            foreach (var moduleRef in moduleRefs.Where(reference => reference > 0).Distinct())
            {
                if (Load(moduleRef) is not { } module) continue;
                var asset = byIndex[moduleRef];
                var values = Values(module.Bytes, module.Properties, catalog);
                if (asset.ClassName.Contains("required", StringComparison.OrdinalIgnoreCase) && values.TryGetValue("material", out var found))
                    material = found as string;
                if (asset.ClassName.Contains("typedatamesh", StringComparison.OrdinalIgnoreCase) && values.TryGetValue("mesh", out var meshName))
                    mesh = meshName as string;
                models.Add(new ParticleModuleModel(asset.ClassName.ToLowerInvariant(), asset.ObjectName, values));
            }
            emitters.Add(new ParticleEmitterModel(emitters.Count, name, enabled, material, mesh, models));
        }
        return new ParticleEffectModel(system.ObjectName, packageAlias, emitters);
    }

    private static Dictionary<string, object> Values(byte[] bytes, IReadOnlyList<ParticleProperty> properties, AssetCatalog catalog)
    {
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            var key = property.ArrayIndex > 0 ? $"{property.Name}[{property.ArrayIndex}]" : property.Name;
            object? value = property.Type switch
            {
                "floatproperty" when property.Size == 4 => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(property.ValueOffset)),
                "intproperty" when property.Size == 4 => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(property.ValueOffset)),
                "boolproperty" => bytes[property.ValueOffset] != 0,
                "byteproperty" when property.Size == 8 => NameValue(bytes, property, catalog),
                "byteproperty" when property.Size == 1 => (int)bytes[property.ValueOffset],
                "nameproperty" when property.Size == 8 => NameValue(bytes, property, catalog),
                "objectproperty" when property.Size == 4 => ObjectName(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(property.ValueOffset)), catalog),
                "structproperty" => Struct(bytes, property, catalog),
                "arrayproperty" when property.Name.Equals("burstlist", StringComparison.OrdinalIgnoreCase) => Bursts(bytes, property, catalog),
                _ => null,
            };
            if (value is not null) values[key] = value;
        }
        return values;
    }

    private static object? Struct(byte[] bytes, ParticleProperty property, AssetCatalog catalog)
    {
        var vector = ParticleEffects.IsVector(property);
        if (vector || string.Equals(property.StructName, "rawdistributionfloat", StringComparison.OrdinalIgnoreCase))
        {
            if (property.Child("lookuptable") is not { Type: "arrayproperty", Size: >= 4 } table) return null;
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(table.ValueOffset));
            if (count < 3 || table.Size != 4 + count * 4) return null;
            var values = new float[count];
            for (var index = 0; index < count; index++)
                values[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(table.ValueOffset + 4 + index * 4));
            var dimensions = vector ? 3 : 1;
            var chunk = property.Child("lookuptablechunksize") is { Type: "byteproperty", Size: 1 } chunkProperty
                ? bytes[chunkProperty.ValueOffset] : dimensions;
            return new ParticleTableModel(values, dimensions, Math.Max(1, (int)chunk),
                Float(bytes, property.Child("lookuptabletimescale")), Float(bytes, property.Child("lookuptablestarttime")),
                ParticleEffects.KindOf(property)?.ToString());
        }
        if (property.Children.Count == 0 && property.Size == 12)
            return new[] { Float(bytes, property, 0), Float(bytes, property, 4), Float(bytes, property, 8) };
        return null;
    }

    private static IReadOnlyList<Dictionary<string, object>>? Bursts(byte[] bytes, ParticleProperty property, AssetCatalog catalog) =>
        ParticlePropertyStream.ReadStructArray(bytes, property, catalog.Names)?.Select(element => Values(bytes, element, catalog)).ToArray();

    private static float Float(byte[] bytes, ParticleProperty? property, int offset = 0) =>
        property is { Size: >= 4 } ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(property.ValueOffset + offset)) : 0;

    private static ParticleProperty? Find(IReadOnlyList<ParticleProperty> properties, string name) =>
        properties.FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string NameValue(byte[] bytes, ParticleProperty property, AssetCatalog catalog)
    {
        var index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(property.ValueOffset));
        var number = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(property.ValueOffset + 4));
        return index >= 0 && index < catalog.Names.Count ? new UnrealNameReference(index, number).Resolve(catalog.Names) : string.Empty;
    }

    private static string? ObjectName(int reference, AssetCatalog catalog) => reference switch
    {
        > 0 when reference <= catalog.Exports.Count => catalog.Exports[reference - 1].ObjectName.Resolve(catalog.Names),
        < 0 when -reference <= catalog.Imports.Count => catalog.Imports[-reference - 1].ObjectName.Resolve(catalog.Names),
        _ => null,
    };
}
