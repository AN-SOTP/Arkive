using Arkive.Core.Textures;

namespace Arkive.Core.Outfits;

/// <summary>
/// What a first painting showed about the replaced outfit's shaders, so a second painting can match the donor: how much
/// brighter or darker each colour picture must be (from lit bakes), and which picture values give the roughness and
/// metalness the donor's shader computed (from surface bakes). Keyed by picture name.
/// </summary>
public sealed class OutfitSwapCalibration
{
    public Dictionary<string, OutfitSwapColourCurve> Colour { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For an ORM or specular picture: the channel that sets roughness, and the curve from wanted roughness to it.</summary>
    public Dictionary<string, (int Channel, OutfitSwapChannelCurve Curve)> Roughness { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For an ORM picture: the channel that sets metalness, and the curve from wanted metalness to it.</summary>
    public Dictionary<string, (int Channel, OutfitSwapChannelCurve Curve)> Metalness { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="wantedColours">The donor's lit colours painted into the replaced outfit's layout.</param>
    /// <param name="painted">The first painting's pictures.</param>
    /// <param name="litBakes">Lit bakes of the replaced pieces with the first painting, by piece and material slot.</param>
    /// <param name="surfaceBakes">Surface (roughness, metalness) bakes of the same.</param>
    public static async Task<OutfitSwapCalibration> LearnAsync(
        IReadOnlyDictionary<string, string>? wantedColours,
        IReadOnlyDictionary<string, string> painted,
        IReadOnlyList<OutfitSwapScene> targetScenes,
        IReadOnlyList<IReadOnlyDictionary<int, string>>? litBakes,
        IReadOnlyList<IReadOnlyDictionary<int, string>>? surfaceBakes,
        CancellationToken cancellationToken)
    {
        var result = new OutfitSwapCalibration();
        async Task<RgbaImage?> Read(string? path, int width = 0, int height = 0)
        {
            if (path is null || !File.Exists(path)) return null;
            var image = await PngRgbaCodec.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            return width > 0 ? OutfitSwapPictures.Resize(image, width, height) : image;
        }
        for (var piece = 0; piece < targetScenes.Count; piece++)
            foreach (var material in targetScenes[piece].Materials)
            {
                var colour = OutfitSwapPlanner.Texture(material, OutfitSwapPlanner.Role.Colour);
                if (colour is not null && !result.Colour.ContainsKey(colour) && wantedColours?.GetValueOrDefault(colour) is { } wantedPath &&
                    await Read(litBakes?.ElementAtOrDefault(piece)?.GetValueOrDefault(material.Slot)).ConfigureAwait(false) is { } lit &&
                    await Read(wantedPath, lit.Width, lit.Height).ConfigureAwait(false) is { } wanted &&
                    OutfitSwapColourCurve.Learn(wanted, lit) is { } gain)
                    result.Colour[colour] = gain;

                if (await Read(surfaceBakes?.ElementAtOrDefault(piece)?.GetValueOrDefault(material.Slot)).ConfigureAwait(false) is not { } surface) continue;
                var orm = OutfitSwapPlanner.Texture(material, OutfitSwapPlanner.Role.Orm);
                var specular = OutfitSwapPlanner.Texture(material, OutfitSwapPlanner.Role.Specular);
                if (orm is not null && painted.GetValueOrDefault(orm) is { } ormPath && !result.Roughness.ContainsKey(orm) &&
                    await Read(ormPath, surface.Width, surface.Height).ConfigureAwait(false) is { } ormPicture)
                {
                    if (OutfitSwapChannelCurve.Learn(ormPicture, 1, surface, 0) is { } rough) result.Roughness[orm] = (1, rough);
                    if (OutfitSwapChannelCurve.Learn(ormPicture, 2, surface, 1) is { } metal) result.Metalness[orm] = (2, metal);
                }
                else if (specular is not null && painted.GetValueOrDefault(specular) is { } specularPath && !result.Roughness.ContainsKey(specular) &&
                         await Read(specularPath, surface.Width, surface.Height).ConfigureAwait(false) is { } specularPicture &&
                         OutfitSwapChannelCurve.Learn(specularPicture, 3, surface, 0) is { } gloss)
                    result.Roughness[specular] = (3, gloss);
            }
        return result;
    }
}

/// <summary>
/// How one channel of a material's picture turns into one value its shader computes (roughness, metalness), and back:
/// which picture value gives a wanted value. Learned from a painted picture and a bake of it; may run either way.
/// </summary>
public sealed class OutfitSwapChannelCurve
{
    private readonly byte[] _inverse = new byte[256];

    private OutfitSwapChannelCurve(float[] forward)
    {
        for (var wanted = 0; wanted < 256; wanted++)
        {
            var best = 0;
            var bestError = float.MaxValue;
            for (var value = 0; value < 256; value++)
            {
                var error = MathF.Abs(forward[value] - wanted);
                if (error < bestError)
                {
                    best = value;
                    bestError = error;
                }
            }
            _inverse[wanted] = (byte)best;
        }
    }

    /// <summary>The curve, or null when the painting covers too little of the channel's range to tell.</summary>
    public static OutfitSwapChannelCurve? Learn(RgbaImage picture, int channel, RgbaImage baked, int bakedChannel)
    {
        var bins = new List<byte>[256];
        for (var v = 0; v < 256; v++) bins[v] = [];
        for (var i = 0; i < baked.Pixels.Length; i += 4)
            if (baked.Pixels[i + 3] >= 200) bins[picture.Pixels[i + channel]].Add(baked.Pixels[i + bakedChannel]);
        var known = new List<int>();
        var forward = new float[256];
        for (var v = 0; v < 256; v++)
            if (bins[v].Count >= 20)
            {
                bins[v].Sort();
                forward[v] = bins[v][bins[v].Count / 2];
                known.Add(v);
            }
        if (known.Count < 6 || known[^1] - known[0] < 64) return null;
        for (var v = 0; v < 256; v++)
        {
            var after = known.FindIndex(k => k >= v);
            if (after == 0) forward[v] = forward[known[0]];
            else if (after < 0) forward[v] = forward[known[^1]];
            else if (known[after] != v)
            {
                int a = known[after - 1], b = known[after];
                forward[v] = forward[a] + (forward[b] - forward[a]) * (v - a) / (b - a);
            }
        }
        return new OutfitSwapChannelCurve(forward);
    }

    public byte Map(byte wanted) => _inverse[wanted];
}
