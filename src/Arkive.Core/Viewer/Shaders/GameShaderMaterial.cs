namespace Arkive.Core.Viewer.Shaders;

public sealed record GameShaderInput(int Register, string Semantic, int Index);

/// <summary>A material's own compiled pixel shader, translated and bound for the preview.</summary>
public sealed record GameShaderPlan(
    string MaterialMap,
    string MaterialId,
    string ShaderId,
    bool ExactVariant,
    bool ExactBindings,
    string Declarations,
    string Function,
    IReadOnlyDictionary<string, float[]> ConstantBuffers,
    IReadOnlyDictionary<string, string> Samplers,
    IReadOnlyList<GameShaderInput> Inputs,
    int AlbedoOutput,
    int InstructionCount,
    IReadOnlyList<string> Notes);

/// <summary>Finds, translates and binds the game's base-pass pixel shader for one material instance.</summary>
public static class GameShaderMaterial
{
    /// <param name="materialBytes">
    /// Export bytes of a material by name (null when unavailable). The instance and then its parents are
    /// searched for the GUID of the compiled variant they use.
    /// </param>
    /// <param name="textureUrl">Resolves an exported texture name to a scene URL (null when it was not exported).</param>
    /// <param name="textureSrgb">Whether the game samples a texture with sRGB decoding.</param>
    public static GameShaderPlan? Build(
        ShaderCacheIndex index,
        MaterialInstanceDescription material,
        Func<string, byte[]?> materialBytes,
        Func<string, string?> textureUrl,
        Func<string, bool> textureSrgb,
        out string? failure)
    {
        failure = null;
        var notes = new List<string>();
        ShaderCacheMaterialMap? map = null;
        foreach (var name in material.ParentChain.Prepend(material.Name))
        {
            if (materialBytes(name) is not { } bytes) continue;
            map = index.FindMapIn(bytes);
            if (map is not null)
            {
                if (!name.Equals(material.Name, StringComparison.OrdinalIgnoreCase))
                    notes.Add($"Uses the compiled variant of its parent {name}.");
                break;
            }
        }
        var exactVariant = map is not null;
        if (map is null)
        {
            var candidates = index.FindMapsByName(material.RootMaterial);
            map = candidates.FirstOrDefault(candidate => candidate.BasePassPixelShaderId is not null);
            if (map is null)
            {
                failure = $"The shader cache has no compiled shader for {material.RootMaterial}.";
                return null;
            }
            notes.Add(candidates.Count > 1
                ? $"Using one of {candidates.Count} compiled variants of {map.FriendlyName}; this instance's own variant was not found."
                : $"Using the base material's compiled shader ({map.FriendlyName}).");
        }
        if (map.BasePassPixelShaderId is null)
        {
            failure = $"{map.FriendlyName} has no base-pass pixel shader in the cache.";
            return null;
        }

        ShaderMaterialExpressions expressions;
        try { expressions = index.ReadExpressions(map); }
        catch (Exception exception) when (exception is FormatException or EndOfStreamException or IOException)
        {
            failure = $"Could not read {map.FriendlyName}'s uniform expressions: {exception.Message}";
            return null;
        }
        var dxbc = index.ReadShader(map.BasePassPixelShaderId, map.CacheIndex);
        if (dxbc is null)
        {
            failure = $"The compiled shader {map.BasePassPixelShaderId} is missing from the cache.";
            return null;
        }

        var bindings = index.ReadBindings(map.BasePassPixelShaderId);
        var exactBindings = bindings is not null;

        // Each 2D texture expression is bound to the t# slot the shader record names (or, when that is
        // unknown, to the slot matching its position).
        var slotTextures = new Dictionary<int, string>();
        for (var expression = 0; expression < expressions.Textures.Count; expression++)
        {
            if (expressions.Textures[expression] is not UniformTexture texture) continue;
            var slot = bindings is { TextureSlots.Count: > 0 } && expression < bindings.TextureSlots.Count
                ? bindings.TextureSlots[expression]
                : expression;
            string? name = null;
            if (texture.Parameter is not null) material.Textures.TryGetValue(texture.Parameter, out name);
            if (name is null && material.ReferencedTextures is { } referenced &&
                texture.TextureIndex >= 0 && texture.TextureIndex < referenced.Count)
                name = referenced[texture.TextureIndex];
            if (!string.IsNullOrWhiteSpace(name)) slotTextures[slot] = name;
        }
        var samplers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (slot, name) in slotTextures)
            if (textureUrl(name) is { } url) samplers[$"ark_gt{slot}"] = url;

        DxbcTranslation translation;
        try
        {
            translation = DxbcGlslTranslator.Translate(
                DxbcDisassembler.Disassemble(dxbc),
                slot => samplers.ContainsKey($"ark_gt{slot}"),
                slot => slotTextures.TryGetValue(slot, out var name) && textureSrgb(name));
        }
        catch (Exception exception) when (exception is DxbcTranslationException or InvalidDataException or DllNotFoundException)
        {
            failure = $"{map.FriendlyName}'s shader could not be translated: {exception.Message}";
            return null;
        }

        if (bindings is null)
        {
            notes.Add("The shader's parameter registers were not found; assumed the usual layout.");
            var vectorRegisters = Enumerable.Range(2, expressions.Vectors.Count).ToList();
            bindings = new ShaderParameterBindings(
                Enumerable.Range(2 + expressions.Vectors.Count, (expressions.Scalars.Count + 3) / 4).ToList(),
                vectorRegisters,
                []);
        }

        var cb0 = new float[Math.Max(1, translation.ConstantBufferSizes.GetValueOrDefault(0)) * 4];
        float[]? Vector(string name) => material.Vectors.TryGetValue(name, out var value) ? value : null;
        float? Scalar(string name) => material.Scalars.TryGetValue(name, out var value) ? value : null;
        for (var k = 0; k < expressions.Vectors.Count && k < bindings.VectorRegisters.Count; k++)
        {
            var register = bindings.VectorRegisters[k];
            if (register * 4 + 3 >= cb0.Length) continue;
            var value = UniformExpressionEvaluator.Evaluate(expressions.Vectors[k], Vector, Scalar);
            Array.Copy(value, 0, cb0, register * 4, 4);
        }
        for (var k = 0; k < expressions.Scalars.Count; k++)
        {
            if (k / 4 >= bindings.ScalarRegisters.Count) break;
            var slot = bindings.ScalarRegisters[k / 4] * 4 + k % 4;
            if (slot >= cb0.Length) continue;
            cb0[slot] = UniformExpressionEvaluator.Evaluate(expressions.Scalars[k], Vector, Scalar)[0];
        }

        // Engine constant buffers are zero, except the per-object tint cb2[3] (rgb added, w scales),
        // whose identity is (0,0,0,1).
        var buffers = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var (buffer, size) in translation.ConstantBufferSizes)
            buffers[$"ark_cb{buffer}"] = buffer == 0 ? cb0 : new float[Math.Max(1, size) * 4];
        if (buffers.TryGetValue("ark_cb2", out var cb2) && cb2.Length >= 16) cb2[15] = 1;

        var albedo = translation.Outputs.Contains(3) ? 3 : 0;
        return new GameShaderPlan(
            map.FriendlyName,
            map.MaterialId,
            map.BasePassPixelShaderId,
            exactVariant,
            exactBindings,
            translation.Declarations,
            translation.Function,
            buffers,
            samplers,
            translation.Inputs.Select(input => new GameShaderInput(input.Register, input.Semantic, input.Index)).ToArray(),
            albedo,
            translation.InstructionCount,
            notes);
    }
}
