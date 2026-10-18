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

/// <summary>
/// A setting the effect takes from whoever starts it: a skill hands named values (Color, Size...) to the effects it
/// plays. Without one the setting is <see cref="Constant"/>; with one, Normal mode maps the value from the input range to
/// the output range, Abs does so for its absolute value and Direct uses it as it is.
/// </summary>
/// <param name="Multiply">For Lost Ark's multiply distributions: the value scales <see cref="Base"/> instead.</param>
public sealed record ParticleParameterModel(
    string Name,
    int Dimensions,
    float[] Constant,
    float[] MinInput,
    float[] MaxInput,
    float[] MinOutput,
    float[] MaxOutput,
    string[] Modes,
    bool Multiply,
    ParticleTableModel? Base,
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
        using var logical = new LogicalPackageStream(catalog.Summary, protectionProvider);
        var reader = new Reader(catalog, logical);
        var systemData = reader.Load(system.ExportIndex) ?? throw new InvalidDataException($"{systemObjectName}'s properties could not be read.");
        var emitters = new List<ParticleEmitterModel>();
        var emitterRefs = Find(systemData.Properties, "emitters") is { } list
            ? ParticlePropertyStream.ReadObjectArray(systemData.Bytes, list) ?? []
            : [];
        foreach (var emitterRef in emitterRefs)
        {
            if (reader.Load(emitterRef) is not { } emitter) continue;
            var name = Find(emitter.Properties, "emittername") is { } nameProperty ? reader.NameValue(emitter.Bytes, nameProperty) : $"Emitter {emitters.Count + 1}";
            var lods = Find(emitter.Properties, "lodlevels") is { } lodList ? ParticlePropertyStream.ReadObjectArray(emitter.Bytes, lodList) ?? [] : [];
            if (lods.Count == 0 || reader.Load(lods[0]) is not { } lod) continue;
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
                if (reader.Load(moduleRef) is not { } module) continue;
                var asset = reader.Exports[moduleRef];
                var values = reader.Values(module.Bytes, module.Properties);
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

    private static ParticleProperty? Find(IReadOnlyList<ParticleProperty> properties, string name) =>
        properties.FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static float Float(byte[] bytes, ParticleProperty? property, int offset = 0) =>
        property is { Size: >= 4 } ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(property.ValueOffset + offset)) : 0;

    private sealed class Reader(AssetCatalog catalog, LogicalPackageStream logical)
    {
        public IReadOnlyDictionary<int, AssetCatalogEntry> Exports { get; } = catalog.Assets.ToDictionary(asset => asset.ExportIndex);

        public (byte[] Bytes, IReadOnlyList<ParticleProperty> Properties)? Load(int reference)
        {
            if (!Exports.TryGetValue(reference, out var asset)) return null;
            var bytes = ParticleEffects.Read(logical, asset);
            return ParticlePropertyStream.Read(bytes, catalog.Names) is { } properties ? (bytes, properties) : null;
        }

        public Dictionary<string, object> Values(byte[] bytes, IReadOnlyList<ParticleProperty> properties)
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
                    "byteproperty" when property.Size == 8 => NameValue(bytes, property),
                    "byteproperty" when property.Size == 1 => (int)bytes[property.ValueOffset],
                    "nameproperty" when property.Size == 8 => NameValue(bytes, property),
                    "objectproperty" when property.Size == 4 => ObjectName(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(property.ValueOffset))),
                    "structproperty" => Struct(bytes, property),
                    "arrayproperty" when property.Name.Equals("burstlist", StringComparison.OrdinalIgnoreCase) =>
                        ParticlePropertyStream.ReadStructArray(bytes, property, catalog.Names)?.Select(element => Values(bytes, element)).ToArray(),
                    _ => null,
                };
                if (value is not null) values[key] = value;
            }
            return values;
        }

        private object? Struct(byte[] bytes, ParticleProperty property)
        {
            var vector = ParticleEffects.IsVector(property);
            if (vector || string.Equals(property.StructName, "rawdistributionfloat", StringComparison.OrdinalIgnoreCase))
            {
                var baked = Table(bytes, property, vector);
                // Distributions that read a value handed in by the skill cannot be baked; their own object holds the mapping.
                if (property.Child("distribution") is { Size: 4 } reference &&
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(reference.ValueOffset)) is > 0 and var index &&
                    Exports.TryGetValue(index, out var distribution) &&
                    distribution.ClassName.Contains("particleparameter", StringComparison.OrdinalIgnoreCase) &&
                    Load(index) is { } data)
                    return Parameter(data.Bytes, data.Properties, distribution.ClassName, vector ? 3 : 1, baked,
                        ParticleEffects.KindOf(property)?.ToString());
                return baked;
            }
            if (property.Children.Count == 0 && property.Size == 12)
                return new[] { Float(bytes, property, 0), Float(bytes, property, 4), Float(bytes, property, 8) };
            return null;
        }

        private ParticleParameterModel Parameter(byte[] bytes, IReadOnlyList<ParticleProperty> properties, string className,
            int dimensions, ParticleTableModel? baked, string? kind)
        {
            float[] Read(string name, float fallback)
            {
                var found = Find(properties, name);
                if (found is { Type: "floatproperty" }) return Enumerable.Repeat(Float(bytes, found), dimensions).ToArray();
                return found is { Size: >= 12 }
                    ? [Float(bytes, found, 0), Float(bytes, found, 4), Float(bytes, found, 8)]
                    : Enumerable.Repeat(fallback, dimensions).ToArray();
            }
            var modes = new string[dimensions];
            for (var axis = 0; axis < dimensions; axis++)
            {
                var mode = properties.FirstOrDefault(item => item.Name is "parammode" or "parammodes" && item.ArrayIndex == axis);
                modes[axis] = mode switch
                {
                    { Size: 8 } => NameValue(bytes, mode).ToLowerInvariant(),
                    { Size: 1 } => bytes[mode.ValueOffset] switch { 1 => "dpm_abs", 2 => "dpm_direct", _ => "dpm_normal" },
                    _ => "dpm_normal",
                };
            }
            var name = Find(properties, "parametername") is { Size: 8 } parameter ? NameValue(bytes, parameter) : string.Empty;
            var multiply = className.Contains("multiply", StringComparison.OrdinalIgnoreCase);
            // A multiply distribution scales another distribution (its first nested raw distribution) by the parameter.
            var nested = multiply ? Values(bytes, properties).Values.OfType<ParticleTableModel>().FirstOrDefault() : null;
            return new ParticleParameterModel(name, dimensions, Read("constant", 0), Read("mininput", 0), Read("maxinput", 1),
                Read("minoutput", 0), Read("maxoutput", 1), modes, multiply, nested ?? baked, kind);
        }

        private static ParticleTableModel? Table(byte[] bytes, ParticleProperty property, bool vector)
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

        public string NameValue(byte[] bytes, ParticleProperty property)
        {
            var index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(property.ValueOffset));
            var number = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(property.ValueOffset + 4));
            return index >= 0 && index < catalog.Names.Count ? new UnrealNameReference(index, number).Resolve(catalog.Names) : string.Empty;
        }

        private string? ObjectName(int reference) => reference switch
        {
            > 0 when reference <= catalog.Exports.Count => catalog.Exports[reference - 1].ObjectName.Resolve(catalog.Names),
            < 0 when -reference <= catalog.Imports.Count => catalog.Imports[-reference - 1].ObjectName.Resolve(catalog.Names),
            _ => null,
        };
    }
}
