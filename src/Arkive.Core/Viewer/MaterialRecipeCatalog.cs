using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Arkive.Core.Viewer;

/// <summary>How far a recipe has been checked against the game.</summary>
public enum MaterialRecipeStatus
{
    /// <summary>No recipe matched; the main texture is shown as stored, like UModel.</summary>
    Fallback,
    /// <summary>Written from texture channels and parameter names, not yet checked against the game's shader.</summary>
    Inferred,
    /// <summary>Checked against the game's compiled shader or in-game captures.</summary>
    Verified,
}

public sealed record MaterialRecipeDefinition(
    string Id,
    string Label,
    MaterialRecipeStatus Status,
    IReadOnlyList<string> Masters,
    string BaseColor,
    string? Opacity,
    string? Emissive,
    string? Roughness,
    string? Metalness,
    string? Occlusion,
    string? Normal,
    bool FlipNormalGreen,
    string? Notes,
    IReadOnlyDictionary<string, string>? Defines = null,
    string? Evidence = null);

public sealed record MaterialRecipeCatalogDocument(
    int SchemaVersion,
    IReadOnlyList<MaterialRecipeDefinition> Families,
    MaterialRecipeDefinition Fallback,
    IReadOnlyDictionary<string, string>? Defines = null);

/// <summary>GLSL and uniforms for one material, ready for the preview.</summary>
public sealed record MaterialRecipePlan(
    string FamilyId,
    string FamilyLabel,
    MaterialRecipeStatus Status,
    string? MatchedMaster,
    string BaseColorGlsl,
    string? OpacityGlsl,
    string? EmissiveGlsl,
    string? RoughnessGlsl,
    string? MetalnessGlsl,
    string? OcclusionGlsl,
    string? NormalTextureParameter,
    bool FlipNormalGreen,
    IReadOnlyList<RecipeUniform> Uniforms,
    IReadOnlyDictionary<string, string> Samplers,
    bool UsesSecondUv,
    string? Evidence,
    IReadOnlyList<string> Warnings);

public sealed class MaterialRecipeCatalog
{
    public const string ResourceName = "Arkive.Core.Viewer.material-recipes.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IReadOnlyList<(MaterialRecipeDefinition Recipe, Regex[] Patterns)> _families;

    public MaterialRecipeCatalog(MaterialRecipeCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document with
        {
            Families = document.Families.Select(recipe => WithSharedDefines(recipe, document.Defines)).ToArray(),
            Fallback = WithSharedDefines(document.Fallback, document.Defines),
        };
        document = Document;
        _families = document.Families
            .Select(recipe => (recipe, recipe.Masters.Select(Glob).ToArray()))
            .ToArray();
        // Parse everything up front so a broken recipe fails at load, not mid-preview.
        foreach (var recipe in document.Families.Append(document.Fallback))
            foreach (var expression in Expressions(recipe))
                RecipeParser.Parse(expression, recipe.Defines);
    }

    public MaterialRecipeCatalogDocument Document { get; }

    public static MaterialRecipeCatalog LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded recipe catalog {ResourceName} is missing.");
        return Load(stream);
    }

    public static MaterialRecipeCatalog Load(Stream stream) =>
        new(JsonSerializer.Deserialize<MaterialRecipeCatalogDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException("The material recipe catalog is empty."));

    /// <summary>
    /// Picks a recipe by walking from the instance towards its root material,
    /// so a recipe for a specific master instance wins over its base material.
    /// </summary>
    public (MaterialRecipeDefinition Recipe, string? Master) Match(MaterialInstanceDescription material)
    {
        foreach (var name in material.ParentChain.Prepend(material.Name))
            foreach (var (recipe, patterns) in _families)
                if (patterns.Any(pattern => pattern.IsMatch(name)))
                    return (recipe, name);
        return (Document.Fallback, null);
    }

    public MaterialRecipePlan Plan(
        MaterialInstanceDescription material,
        Func<string, bool> textureAvailable,
        bool storedTextureOnly = false)
    {
        ArgumentNullException.ThrowIfNull(material);
        var (recipe, master) = storedTextureOnly ? (Document.Fallback, null) : Match(material);
        var bindings = new MaterialBindings(material, textureAvailable);
        var warnings = new List<string>();
        var compiler = new RecipeGlslCompiler(bindings);
        string? Optional(string? expression, int width)
        {
            if (string.IsNullOrWhiteSpace(expression)) return null;
            try { return compiler.Compile(RecipeParser.Parse(expression, recipe.Defines), width); }
            catch (RecipeSyntaxException exception)
            {
                warnings.Add(exception.Message);
                return null;
            }
        }

        var baseColor = Optional(recipe.BaseColor, 3);
        if (baseColor is null)
        {
            // A family recipe that cannot run for this instance falls back to the stored texture.
            warnings.Add($"Recipe {recipe.Id} could not be applied; showing the stored texture.");
            recipe = Document.Fallback;
            master = null;
            compiler = new RecipeGlslCompiler(bindings);
            baseColor = compiler.Compile(RecipeParser.Parse(recipe.BaseColor, recipe.Defines), 3);
        }
        var normal = recipe.Normal?
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(bindings.HasTexture);
        return new MaterialRecipePlan(
            recipe.Id,
            recipe.Label,
            recipe.Status,
            master,
            baseColor,
            Optional(recipe.Opacity, 1),
            Optional(recipe.Emissive, 3),
            Optional(recipe.Roughness, 1),
            Optional(recipe.Metalness, 1),
            Optional(recipe.Occlusion, 1),
            normal,
            recipe.FlipNormalGreen,
            compiler.Uniforms.ToArray(),
            compiler.Samplers.ToDictionary(pair => pair.Value, pair => pair.Key),
            compiler.UsesSecondUv,
            recipe.Evidence,
            warnings);
    }

    public static IEnumerable<string> Expressions(MaterialRecipeDefinition recipe) =>
        new[] { recipe.BaseColor, recipe.Opacity, recipe.Emissive, recipe.Roughness, recipe.Metalness, recipe.Occlusion }
            .Where(expression => !string.IsNullOrWhiteSpace(expression))
            .Select(expression => expression!);

    private static MaterialRecipeDefinition WithSharedDefines(
        MaterialRecipeDefinition recipe,
        IReadOnlyDictionary<string, string>? shared)
    {
        if (shared is null || shared.Count == 0) return recipe;
        var merged = new Dictionary<string, string>(shared, StringComparer.Ordinal);
        foreach (var (name, body) in recipe.Defines ?? new Dictionary<string, string>()) merged[name] = body;
        return recipe with { Defines = merged };
    }

    private static Regex Glob(string pattern) => new(
        "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed class MaterialBindings(MaterialInstanceDescription material, Func<string, bool> textureAvailable)
        : IRecipeBindings
    {
        public bool HasTexture(string parameter) =>
            material.Textures.TryGetValue(parameter, out var texture) && textureAvailable(texture);

        public float[]? Vector(string parameter) =>
            First(parameter, name => material.Vectors.TryGetValue(name, out var value) ? value : null);

        public float? Scalar(string parameter) =>
            First(parameter, name => material.Scalars.TryGetValue(name, out var value) ? value : (float?)null);

        public bool? Switch(string parameter) =>
            First(parameter, name => material.StaticSwitches.TryGetValue(name, out var value) ? value : (bool?)null);

        private static T? First<T>(string alternatives, Func<string, T?> lookup) =>
            alternatives.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(lookup).FirstOrDefault(value => value is not null);
    }
}
