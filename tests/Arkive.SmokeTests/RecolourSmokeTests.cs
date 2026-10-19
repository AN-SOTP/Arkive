using static Arkive.SmokeTests.ArkiveModTestFixture;

namespace Arkive.SmokeTests;

internal static class RecolourSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("tints only a material's colour settings, by range, with surfaces and glows apart", TestMaterialTints),
        new("repaints a picture in two tones: the colour with its shading, and highlights on light details", TestTwoToneRecolour),
        new("tints material working copies with a verified candidate and replaces an earlier tint", TestTintService),
    ];

    private static MaterialVectorEdit Vector(string name, float red, float green, float blue) =>
        new(name, red, green, blue, 1, red, green, blue, 1);

    private static bool Near(MaterialVectorEdit vector, float red, float green, float blue) =>
        Math.Abs(vector.Red - red) < 1e-4 && Math.Abs(vector.Green - green) < 1e-4 && Math.Abs(vector.Blue - blue) < 1e-4;

    private static void TestMaterialTints()
    {
        var env = CreateEnvironment(Path.Combine(fixtureDirectory, "recolour-material-tints"));
        var client = CreateMaterialClient(Path.Combine(fixtureDirectory, "recolour-material-tints"), out _);
        var document = CreateMaterialEdit(env, client, "Stone", 0.5f, null, candidate: false).Document with
        {
            Vectors =
            [
                Vector("emissive_color", 0.25f, 0.06f, 1), Vector("diffuse_color", 0.6f, 1, 0.6f),
                Vector("occlusion_color", 0.02f, 0.01f, 0.01f), Vector("uv_tiling_and_panning", 5, 5, 0.02f),
                Vector("fx_panning_color", 1, 0.1f, 0.05f),
            ],
            Scalars =
            [
                new MaterialScalarEdit("diffuse_brightness", 0.2f, 0.2f), new MaterialScalarEdit("emissive_intensity", 3, 3),
                new MaterialScalarEdit("layer01_brightness", 1.5f, 1.5f),
            ],
        };
        var gold = new MaterialTintRecipe(1, 0.8f, 0.4f);
        var all = MaterialTints.Apply(document, gold);
        Assert(Near(all.Vectors[0], 1, 0.8f, 0.4f) && Near(all.Vectors[1], 1, 0.8f, 0.4f) &&
               !all.Vectors[2].Changed && !all.Vectors[3].Changed && all.Scalars.All(scalar => !scalar.Changed),
            "an every-colour tint did not keep each colour's brightness or touched occlusion, tiling or brightness settings: " +
            string.Join("; ", all.Vectors.Select(vector => $"{vector.Name} {vector.Red},{vector.Green},{vector.Blue}")));

        var purpleOnly = MaterialTints.Apply(document, gold with { Selection = ColourSelection.ColourRange, Hue = 255, Range = 20 });
        Assert(purpleOnly.Vectors[0].Changed && !purpleOnly.Vectors[1].Changed && !purpleOnly.Vectors[4].Changed,
            "a purple colour range changed colours outside it");

        var white = new MaterialTintRecipe(1, 0.97f, 0.9f) { LightenSurfaces = 0.5f };
        var twoTone = MaterialTints.Apply(document, white, gold);
        Assert(Near(twoTone.Vectors[1], 1, 0.97f, 0.9f) && Near(twoTone.Vectors[0], 1, 0.8f, 0.4f) &&
               Near(twoTone.Vectors[4], 1, 0.8f, 0.4f) &&
               Math.Abs(twoTone.Scalars[0].Value - 0.6f) < 1e-5 && !twoTone.Scalars[1].Changed && !twoTone.Scalars[2].Changed,
            "surfaces and glows did not take their own colours, or darkened surfaces were not lifted half way");
    }

    private static void TestTwoToneRecolour()
    {
        // Three greys: dark, middle and light. Two-tone white with gold highlights on the lightest quarter.
        var image = new RgbaImage(3, 1, [40, 40, 40, 255, 110, 110, 110, 255, 200, 200, 200, 128]);
        new TextureRecolour(1, 1, 1)
        {
            Style = TextureRecolourStyle.TwoTone, HighlightRed = 1, HighlightGreen = 0.8f, HighlightBlue = 0.4f,
            HighlightAmount = 0.25f, Lift = 0.5f,
        }.Apply(image);
        var p = image.Pixels;
        Assert(p[0] == p[1] && p[1] == p[2] && p[0] is >= 120 and <= 135 &&
               p[4] == p[5] && p[4] > p[0] &&
               p[8] == 255 && p[9] is >= 200 and <= 208 && p[10] is >= 100 and <= 104 && p[11] == 128,
            "two-tone did not lift the dark grey to half white, keep shading, gold the lightest detail and keep alpha: " +
            string.Join(",", p));
    }

    private static void TestTintService()
    {
        var root = Path.Combine(fixtureDirectory, "recolour-tint-service");
        var env = CreateEnvironment(root);
        var client = CreateMaterialClient(root, out _);
        var edit = CreateMaterialEdit(env, client, "Face", 0.5f, null, candidate: false);
        var service = new ArkiveModTintService(env.Edits, NoChunkProtectionProvider.Instance, env.MaterialCandidates);
        ArkiveModLocalEdit Local() => ArkiveModEditSnapshot.LoadAsync(env.Edits).GetAwaiter().GetResult().Find(edit.Document.EditId)!;

        var red = service.TintAsync(new MaterialTintRequest([Local()], new TextureRecolour(1, 0, 0))).GetAwaiter().GetResult();
        var once = env.Edits.MaterialEdits.LoadEntryAsync(edit.DocumentPath).GetAwaiter().GetResult();
        Assert(red.Changed == 1 && red.Skipped.Count == 0 && once.Document.Vectors.Single() is { Red: 1, Green: 0, Blue: 0 } &&
               once.Document.LatestCandidateManifestPath is { } candidate && File.Exists(candidate),
            "the material's tint colour was not tinted red with a verified candidate: " +
            string.Join(" | ", red.Skipped.Select(skip => skip.Reason)));

        // Tinting again starts from the game's colour (1, 0.5, 0.25), not from the red.
        var blue = service.TintAsync(new MaterialTintRequest([Local()], new TextureRecolour(0, 0, 1) { Strength = 0.5f }))
            .GetAwaiter().GetResult();
        var twice = env.Edits.MaterialEdits.LoadEntryAsync(edit.DocumentPath).GetAwaiter().GetResult().Document.Vectors.Single();
        Assert(blue.Changed == 1 && Math.Abs(twice.Red - 0.5f) < 1e-5 && Math.Abs(twice.Green - 0.25f) < 1e-5 &&
               Math.Abs(twice.Blue - 0.625f) < 1e-5,
            $"a second tint stacked on the first instead of starting from the game's colour: {twice.Red},{twice.Green},{twice.Blue}");

        var none = service.TintAsync(new MaterialTintRequest([Local()],
            new TextureRecolour(0, 1, 0) { Selection = ColourSelection.ColourRange, RangeHue = 200, RangeWidth = 10 })).GetAwaiter().GetResult();
        Assert(none.Changed == 0 && none.Unchanged == 1, "a colour range that selects nothing was not reported as unchanged");
    }
}
