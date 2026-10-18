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
    IReadOnlyList<string> Notes,
    bool EmitsSceneColour = false,
    bool Unlit = false,
    bool WritesSurface = false)
{
    /// <summary>
    /// How fast each ark_cb0 value changes per second of game time, for the material's time-driven values (panning,
    /// scrolling, pulsing) that change at a steady rate; null when none does. Others stay at time zero.
    /// </summary>
    public float[]? TimeRates { get; init; }

    /// <summary>The first ark_cb0 register the material's own parameters use; those before it are the engine's.</summary>
    public int FirstParameterRegister { get; init; }
}

/// <summary>Finds, translates and binds the game's base-pass pixel shader for one material instance.</summary>
public static class GameShaderMaterial
{
    /// <summary>
    /// Whether the material sets up glow. The scene colour (o0) of a lit shader also holds rim light and
    /// specular terms that depend on lighting inputs the viewer can only approximate (hair turns pink with
    /// them), so it is added only for materials that emit light.
    /// </summary>
    private static bool HasEmissiveParameter(MaterialInstanceDescription material) =>
        material.Textures.Keys.Concat(material.Vectors.Keys).Concat(material.Scalars.Keys)
            .Any(name => name.Contains("emissive", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("glow", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What an unset texture parameter reads: empty make-up and decal masks (their alpha weights the colour),
    /// a flat normal, and white for colour, occlusion and other multipliers.
    /// </summary>
    private static string NeutralTexture(string parameter, bool customization)
    {
        if (customization) return "vec4(0.0)";
        if (parameter.Contains("normal", StringComparison.OrdinalIgnoreCase)) return "vec4(0.5, 0.5, 1.0, 1.0)";
        if (parameter.Contains("mask", StringComparison.OrdinalIgnoreCase) || parameter.Contains("emissive", StringComparison.OrdinalIgnoreCase) ||
            parameter.Contains("fx", StringComparison.OrdinalIgnoreCase) || parameter.Contains("pan", StringComparison.OrdinalIgnoreCase))
            return "vec4(0.0, 0.0, 0.0, 1.0)";
        return "vec4(1.0)";
    }

    private static readonly System.Text.RegularExpressions.Regex UnlitAlbedo = new(
        @"ark_o3\.xyz = \(floatBitsToUint\(ark_cb2\[\d+\]\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <param name="materialBytes">
    /// Export bytes of a material by name (null when unavailable). The instance and then its parents are
    /// searched for the static parameters of the compiled permutation they use.
    /// </param>
    /// <param name="textureUrl">Resolves an exported texture name to a scene URL (null when it was not exported).</param>
    /// <param name="textureSrgb">Whether the game samples a texture with sRGB decoding.</param>
    public static GameShaderPlan? Build(
        ShaderCacheIndex index,
        MaterialInstanceDescription material,
        Func<string, byte[]?> materialBytes,
        Func<string, string?> textureUrl,
        Func<string, bool> textureSrgb,
        out string? failure,
        string? pixelShaderId = null)
    {
        var notes = new List<string>();
        if (SelectMap(index, material, materialBytes, notes, out var exactVariant, out failure) is not { } map) return null;
        return Build(index, map, pixelShaderId ?? map.BasePassPixelShaderId, exactVariant, notes, material, textureUrl, textureSrgb, out failure);
    }

    /// <summary>
    /// The compiled shader map of the material's static permutation: the first material in its parent chain with its
    /// own permutation decides it, else one compiled variant of its base material.
    /// </summary>
    public static ShaderCacheMaterialMap? SelectMap(
        ShaderCacheIndex index,
        MaterialInstanceDescription material,
        Func<string, byte[]?> materialBytes,
        List<string> notes,
        out bool exactVariant,
        out string? failure)
    {
        failure = null;
        // The first material in the chain with its own static permutation decides the variant; a material
        // without one renders with its parent's.
        ShaderVariantMatch? match = null;
        string? owner = null;
        foreach (var name in material.ParentChain.Prepend(material.Name))
        {
            if (materialBytes(name) is not { } bytes || index.FindVariant(bytes) is not { } found) continue;
            if (match is null || found.FromStaticParameters)
            {
                match = found;
                owner = name;
            }
            if (found.FromStaticParameters) break;
        }
        var map = match?.Map;
        exactVariant = match?.Exact == true;
        if (match is not null)
        {
            if (!owner!.Equals(material.Name, StringComparison.OrdinalIgnoreCase))
                notes.Add($"Uses the static permutation of its parent {owner}.");
            if (!match.Exact)
                notes.Add(match.Differences > 0
                    ? $"The game's exact permutation is not in the cache; using the nearest one ({match.Differences} static switch(es) differ)."
                    : "The game's exact permutation is not in the cache; using the nearest one.");
        }
        if (map is null)
        {
            var candidates = index.FindMapsByName(material.RootMaterial);
            map = candidates.FirstOrDefault(candidate => candidate.BasePassPixelShaderId is not null || candidate.VertexFactoryPixelShaders is { Count: > 0 });
            if (map is null)
            {
                failure = $"The shader cache has no compiled shader for {material.RootMaterial}.";
                return null;
            }
            notes.Add(candidates.Count > 1
                ? $"Using one of {candidates.Count} compiled variants of {map.FriendlyName}; this instance's own variant was not found."
                : $"Using the base material's compiled shader ({map.FriendlyName}).");
        }
        return map;
    }

    /// <summary>Translates and binds one of the map's pixel shaders (its base pass, or a particle vertex factory's).</summary>
    public static GameShaderPlan? Build(
        ShaderCacheIndex index,
        ShaderCacheMaterialMap map,
        string? pixelShaderId,
        bool exactVariant,
        IReadOnlyList<string> mapNotes,
        MaterialInstanceDescription material,
        Func<string, string?> textureUrl,
        Func<string, bool> textureSrgb,
        out string? failure,
        Func<int, string?>? engineTexture = null)
    {
        failure = null;
        var notes = mapNotes.ToList();
        if (pixelShaderId is null)
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
        var dxbc = index.ReadShader(pixelShaderId, map.CacheIndex);
        if (dxbc is null)
        {
            failure = $"The compiled shader {pixelShaderId} is missing from the cache.";
            return null;
        }

        var bindings = index.ReadBindings(pixelShaderId);
        var exactBindings = bindings is not null;

        // Each 2D texture expression is bound to the t# slot the shader record names (or, when that is
        // unknown, to the slot matching its position).
        var slotTextures = new Dictionary<int, string>();
        var neutralSlots = new Dictionary<int, string>();
        var slotParameters = new Dictionary<int, (string? Parameter, bool Customization)>();
        for (var expression = 0; expression < expressions.Textures.Count; expression++)
        {
            if (expressions.Textures[expression] is not UniformTexture texture) continue;
            var slot = bindings is { TextureSlots.Count: > 0 }
                ? expression < bindings.TextureSlots.Count ? bindings.TextureSlots[expression] : -1
                : expression;
            if (slot < 0) continue;
            string? name = null;
            if (texture.Parameter is not null) material.Textures.TryGetValue(texture.Parameter, out name);
            // Character-creator textures (var_*_ui) the instance leaves unset hold placeholders such as flat_red;
            // the game supplies them from customization data, so they stay unbound (no make-up or decal).
            var customization = texture.Parameter is { } parameter &&
                                parameter.StartsWith("var_", StringComparison.OrdinalIgnoreCase) &&
                                parameter.EndsWith("_ui", StringComparison.OrdinalIgnoreCase);
            // An unset texture parameter's default is not recoverable from the cooked data (its texture index does
            // not address ReferencedTextures), so it reads a neutral value; only non-parameter textures use the index.
            if (name is null && texture.Parameter is null && material.ReferencedTextures is { } referenced &&
                texture.TextureIndex >= 0 && texture.TextureIndex < referenced.Count)
                name = referenced[texture.TextureIndex];
            // A material that references exactly one texture leaves no doubt which one an unset parameter means
            // (effect masters like FX_C_Pa_SmokeSeq_01_Tr keep no collected default for their texturesubuv).
            if (name is null && texture.Parameter is not null && !customization && material.ReferencedTextures is { Count: 1 } only)
                name = only[0];
            // The engine's "null" texture is transparent black; its exported PNG has lost the alpha, which as a
            // decal mask would paint the whole region (Beatrice's face turned white).
            if (string.Equals(name, "null", StringComparison.OrdinalIgnoreCase)) neutralSlots[slot] = "vec4(0.0)";
            else if (!string.IsNullOrWhiteSpace(name))
            {
                slotTextures[slot] = name;
                slotParameters[slot] = (texture.Parameter, customization);
            }
            else if (texture.Parameter is { } unset) neutralSlots[slot] = NeutralTexture(unset, customization);
            if (Environment.GetEnvironmentVariable("ARKIVE_VIEWER_TRACE") == "1")
                Console.Error.WriteLine($"[texture] {material.Name} t{slot} <- {texture.Parameter ?? "-"} #{texture.TextureIndex} = {name ?? "(none)"}");
        }
        var samplers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (slot, name) in slotTextures)
        {
            if (textureUrl(name) is { } url) samplers[$"ark_gt{slot}"] = url;
            // A texture that was not exported (an engine noise for a death dissolve, say) reads what an unset parameter
            // would; left unbound it would read black, which dissolves or masks out the whole surface.
            else if (slotParameters.GetValueOrDefault(slot) is { Parameter: { } parameter } owner)
                neutralSlots[slot] = NeutralTexture(parameter, owner.Customization);
        }

        DxbcTranslation translation;
        try
        {
            // A slot the material does not bind is the engine's (the scene's depth for soft particles); the caller
            // can supply it.
            bool Engine(int slot) => engineTexture?.Invoke(slot) is not null && !slotTextures.ContainsKey(slot) && !neutralSlots.ContainsKey(slot);
            translation = DxbcGlslTranslator.Translate(
                DxbcDisassembler.Disassemble(dxbc),
                slot => samplers.ContainsKey($"ark_gt{slot}") || Engine(slot),
                slot => slotTextures.TryGetValue(slot, out var name) && textureSrgb(name),
                neutralSlots.GetValueOrDefault);
            foreach (var slot in translation.Textures2D.Where(Engine))
                samplers[$"ark_gt{slot}"] = engineTexture!(slot)!;
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

        float[]? Vector(string name) => material.Vectors.TryGetValue(name, out var value) ? value : null;
        float? Scalar(string name) => material.Scalars.TryGetValue(name, out var value) ? value : null;
        float[] Constants(float time)
        {
            var values = new float[Math.Max(1, translation.ConstantBufferSizes.GetValueOrDefault(0)) * 4];
            for (var k = 0; k < expressions.Vectors.Count && k < bindings.VectorRegisters.Count; k++)
            {
                var register = bindings.VectorRegisters[k];
                if (register < 0 || register * 4 + 3 >= values.Length) continue;
                var value = UniformExpressionEvaluator.Evaluate(expressions.Vectors[k], Vector, Scalar, time);
                Array.Copy(value, 0, values, register * 4, 4);
            }
            for (var k = 0; k < expressions.Scalars.Count; k++)
            {
                if (k / 4 >= bindings.ScalarRegisters.Count) break;
                if (bindings.ScalarRegisters[k / 4] < 0) continue;
                var slot = bindings.ScalarRegisters[k / 4] * 4 + k % 4;
                if (slot >= values.Length) continue;
                values[slot] = UniformExpressionEvaluator.Evaluate(expressions.Scalars[k], Vector, Scalar, time)[0];
            }
            return values;
        }
        var cb0 = Constants(0);
        // Values that grow steadily with time (the same step from 0 to 1 s as from 1 to 2 s) are animated by the page.
        var (one, two) = (Constants(1), Constants(2));
        float[]? rates = null;
        for (var slot = 0; slot < cb0.Length; slot++)
        {
            var step = one[slot] - cb0[slot];
            if (step == 0 || !float.IsFinite(step) || Math.Abs(two[slot] - one[slot] - step) > 1e-4f * Math.Max(1, Math.Abs(step))) continue;
            (rates ??= new float[cb0.Length])[slot] = step;
        }

        if (Environment.GetEnvironmentVariable("ARKIVE_VIEWER_TRACE") == "1")
        {
            Console.Error.WriteLine($"[shader] {material.Name} {map.FriendlyName} scalars@{string.Join(',', bindings.ScalarRegisters)} vectors@{string.Join(',', bindings.VectorRegisters)} textures@{string.Join(',', bindings.TextureSlots)}");
            for (var k = 0; k < expressions.Scalars.Count; k++) Console.Error.WriteLine($"   s{k}: {expressions.Scalars[k]}");
            for (var k = 0; k < expressions.Vectors.Count; k++) Console.Error.WriteLine($"   v{k}: {expressions.Vectors[k]}");
        }

        // Engine constant buffers are zero, except the per-object tint cb2[3] (rgb added, w scales),
        // whose identity is (0,0,0,1).
        var buffers = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var (buffer, size) in translation.ConstantBufferSizes)
            buffers[$"ark_cb{buffer}"] = buffer == 0 ? cb0 : new float[Math.Max(1, size) * 4];
        if (buffers.TryGetValue("ark_cb2", out var cb2) && cb2.Length >= 16) cb2[15] = 1;

        // Translucent shaders scale their opacity by engine constants held in the registers before the material's
        // own parameters (1 while the object is fully faded in), so those are set to 1.
        var translucent = material.BlendMode.Contains("Translucent", StringComparison.OrdinalIgnoreCase) ||
                          material.BlendMode.Contains("Additive", StringComparison.OrdinalIgnoreCase);
        var firstParameter = bindings.VectorRegisters.Concat(bindings.ScalarRegisters).Where(register => register >= 0)
            .DefaultIfEmpty(cb0.Length / 4).Min();
        if (translucent)
            for (var slot = 0; slot < Math.Min(firstParameter * 4, cb0.Length); slot++) cb0[slot] = 1;

        // Effect shaders write only the per-object tint to the albedo target; their colour is the scene colour
        // they emit (o0), with its alpha as opacity.
        var unlit = UnlitAlbedo.IsMatch(translation.Function);
        if (unlit) notes.Add("Effect material: shown as the light it emits.");
        var albedo = translation.Outputs.Contains(3) && !unlit ? 3 : 0;
        return new GameShaderPlan(
            map.FriendlyName,
            map.MaterialId,
            pixelShaderId,
            exactVariant,
            exactBindings,
            translation.Declarations,
            translation.Function,
            buffers,
            samplers,
            translation.Inputs.Select(input => new GameShaderInput(input.Register, input.Semantic, input.Index)).ToArray(),
            albedo,
            translation.InstructionCount,
            notes,
            albedo != 0 && translation.Outputs.Contains(0) && HasEmissiveParameter(material),
            unlit,
            // The deferred surface target: o2.z roughness, o2.w metalness (checked against RealPBR, PBR and hair).
            albedo == 3 && translation.Outputs.Contains(2))
        {
            TimeRates = rates,
            FirstParameterRegister = firstParameter,
        };
    }
}
