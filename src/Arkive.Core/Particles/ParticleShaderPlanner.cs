using Arkive.Core.Viewer;
using Arkive.Core.Viewer.Shaders;

namespace Arkive.Core.Particles;

/// <summary>One of an effect material's compiled pixel shaders, translated and bound for the particle preview.</summary>
/// <param name="Samplers">Uniform name (ark_gt#) to the texture, as the caller's texture reference (a relative path).</param>
public sealed record ParticleShader(
    string ShaderId,
    string Declarations,
    string Function,
    IReadOnlyDictionary<string, float[]> ConstantBuffers,
    IReadOnlyDictionary<string, string> Samplers,
    IReadOnlyList<GameShaderInput> Inputs,
    int InstructionCount)
{
    /// <summary>How fast each ark_cb0 value changes per second (see <see cref="GameShaderPlan.TimeRates"/>), or null.</summary>
    public float[]? TimeRates { get; init; }
}

/// <summary>
/// An effect material's own pixel shaders as the game draws particles with them: one per vertex factory (sprites,
/// sprites with dynamic parameters, flipbooks, beams and trails, meshes), several of which often share a shader.
/// </summary>
/// <param name="Factories">Vertex factory type (fparticledynamicparametervertexfactory, flocalvertexfactory...) to shader id.</param>
public sealed record ParticleShaderSet(
    string MaterialMap,
    bool ExactVariant,
    string BlendMode,
    bool TwoSided,
    IReadOnlyDictionary<string, string> Factories,
    IReadOnlyDictionary<string, ParticleShader> Shaders,
    IReadOnlyList<string> Notes);

/// <summary>Builds an effect material's <see cref="ParticleShaderSet"/> from the game's shader cache.</summary>
public static class ParticleShaderPlanner
{
    /// <summary>The vertex factory meshes are drawn with; its shader is the map's base pass when not listed.</summary>
    public const string MeshFactory = "flocalvertexfactory";

    /// <param name="description">The material's parameters along its parent chain.</param>
    /// <param name="materialBytes">Export bytes of a material by name, to find the static permutation the game uses.</param>
    /// <param name="textureReference">An exported texture's reference for the page (null when it was not exported).</param>
    public static ParticleShaderSet? Build(
        ShaderCacheIndex index,
        MaterialInstanceDescription description,
        Func<string, byte[]?> materialBytes,
        Func<string, string?> textureReference,
        Func<string, bool> textureSrgb,
        out string? failure)
    {
        var notes = new List<string>();
        if (GameShaderMaterial.SelectMap(index, description, materialBytes, notes, out var exact, out failure) is not { } map) return null;
        // Effects are never opaque; an export that lacks its base material's settings reads as opaque. Lost Ark names
        // effect materials by blend mode (_Ad additive, _Tr translucent), which stands in then.
        if (description.BlendMode.Contains("Opaque", StringComparison.OrdinalIgnoreCase) || description.BlendMode.Length == 0)
        {
            var names = description.ParentChain.Prepend(description.Name).Append(description.RootMaterial).Append(map.FriendlyName).ToArray();
            var inferred = names.Any(name => name.EndsWith("_ad", StringComparison.OrdinalIgnoreCase)) ? "BLEND_Additive"
                : names.Any(name => name.EndsWith("_tr", StringComparison.OrdinalIgnoreCase)) ? "BLEND_Translucent" : null;
            if (inferred is not null) description = description with { BlendMode = inferred };
        }
        var factories = new Dictionary<string, string>(map.VertexFactoryPixelShaders ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        if (map.BasePassPixelShaderId is { } basePass) factories.TryAdd(MeshFactory, basePass);
        var shaders = new Dictionary<string, ParticleShader>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in factories.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var plan = GameShaderMaterial.Build(index, map, id, exact, [], description, textureReference, textureSrgb, out var shaderFailure);
            if (plan is null)
            {
                notes.Add(shaderFailure ?? $"Shader {id} could not be used.");
                continue;
            }
            shaders[id] = new ParticleShader(id, plan.Declarations, plan.Function, NeutralSceneDepth(plan), plan.Samplers, plan.Inputs,
                plan.InstructionCount) { TimeRates = plan.TimeRates };
        }
        if (shaders.Count == 0)
        {
            failure = notes.FirstOrDefault() ?? $"{map.FriendlyName} has no particle shaders in the cache.";
            return null;
        }
        foreach (var factory in factories.Where(pair => !shaders.ContainsKey(pair.Value)).Select(pair => pair.Key).ToArray())
            factories.Remove(factory);
        failure = null;
        return new ParticleShaderSet(map.FriendlyName, exact, description.BlendMode, description.TwoSided, factories, shaders, notes);
    }

    // Soft particles fade where they meet the scene behind them, read from the scene depth (the shader's first texture,
    // which the preview does not have) as 1 / (depth * cb2[1].z - cb2[1].w) + depth * cb2[1].x + cb2[1].y. These
    // values put the scene far behind every particle, so nothing fades.
    private static IReadOnlyDictionary<string, float[]> NeutralSceneDepth(GameShaderPlan plan)
    {
        if (!plan.ConstantBuffers.TryGetValue("ark_cb2", out var cb2) || cb2.Length < 8 ||
            !plan.Inputs.Any(input => input.Semantic == "TEXCOORD" && input.Index == 5) ||
            !plan.Function.Contains("ark_cb2[1]", StringComparison.Ordinal)) return plan.ConstantBuffers;
        var buffers = new Dictionary<string, float[]>(plan.ConstantBuffers, StringComparer.Ordinal);
        var depth = (float[])cb2.Clone();
        depth[0] = 1; depth[1] = 1; depth[2] = 0; depth[3] = 0;
        depth[4] = 0; depth[5] = 1e6f; depth[6] = 0; depth[7] = -1;
        buffers["ark_cb2"] = depth;
        return buffers;
    }
}
