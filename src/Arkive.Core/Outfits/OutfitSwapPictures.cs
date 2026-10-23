using Arkive.Core.Textures;
using Arkive.Core.Viewer;

namespace Arkive.Core.Outfits;

/// <summary>A piece's materials as the viewer prepared them: the scene's folder holds every picture the materials use.</summary>
public sealed record OutfitSwapScene(string Directory, ModelViewerScene Scene)
{
    public IReadOnlyList<OutfitSwapMaterial> Materials => Scene.Materials.Select(material => new OutfitSwapMaterial(
        material.Slot, material.Name, material.BlendMode.Contains("Translucent", StringComparison.OrdinalIgnoreCase),
        material.Textures, material.Vectors, material.Scalars)).ToArray();

    public string? PicturePath(string texture) =>
        Scene.Textures.FirstOrDefault(item => item.Name.Equals(texture, StringComparison.OrdinalIgnoreCase)) is { } found
            ? Path.Combine(Directory, Uri.UnescapeDataString(found.Url).Replace('/', Path.DirectorySeparatorChar))
            : null;

    public (int Width, int Height)? PictureSize(string texture) =>
        Scene.Textures.FirstOrDefault(item => item.Name.Equals(texture, StringComparison.OrdinalIgnoreCase)) is { } found
            ? (found.Width, found.Height) : null;
}

/// <summary>The target's pictures repainted with the donor outfit, and the material values that go with them.</summary>
public sealed record OutfitSwapPictureResult(
    IReadOnlyDictionary<string, string> Pictures,
    IReadOnlyDictionary<string, ModelViewerMaterialEdit> Materials);

/// <summary>
/// Paints the donor outfit's pictures into the target's own pictures. The colour comes from the donor material as the
/// game's shader draws it (a bake), so it looks the same whichever kind of material the target uses; the cut-out mask
/// stays the donor picture's own alpha. Normal, gloss and glow pictures are converted between the game's two material
/// kinds (older diffuse/specular and newer base colour/ORM).
/// </summary>
public static class OutfitSwapPictures
{
    public static async Task<OutfitSwapPictureResult> PaintAsync(
        OutfitSwapPlan plan,
        IReadOnlyList<OutfitSwapPiece> donors,
        IReadOnlyList<OutfitSwapScene> donorScenes,
        IReadOnlyList<IReadOnlyDictionary<int, string>> bakedColours,
        IReadOnlyList<OutfitSwapScene> targetScenes,
        string outputDirectory,
        CancellationToken cancellationToken,
        IReadOnlyList<IReadOnlyDictionary<int, string>>? targetBakes = null,
        OutfitSwapCalibration? calibration = null,
        IReadOnlyList<IReadOnlyDictionary<int, string>>? surfaceBakes = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var pictures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var materials = new Dictionary<string, ModelViewerMaterialEdit>(StringComparer.OrdinalIgnoreCase);
        var targetMaterials = targetScenes.SelectMany((scene, piece) => scene.Materials.Select(material => (Scene: scene, Material: material))).ToArray();
        byte[]? skinTone = null;
        foreach (var canvas in plan.Canvases)
        {
            var holders = targetMaterials.Where(item =>
                (OutfitSwapPlanner.Texture(item.Material, OutfitSwapPlanner.Role.Colour) ?? item.Material.Name)
                .Equals(canvas.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (holders.Length == 0) continue;
            var (holderScene, holder) = holders[0];
            var curve = calibration?.Colour.GetValueOrDefault(canvas.Key);
            var tiles = new List<(Maps Maps, System.Numerics.Vector4? Rect, OutfitSwapMaterial Material)>();
            foreach (var (piece, slot) in canvas.Tiles)
            {
                var donorMaterial = donors[piece].Mesh.Materials[slot];
                var scene = donorScenes[piece];
                var material = scene.Materials.FirstOrDefault(item => item.Slot == slot)
                               ?? throw new InvalidDataException($"{donors[piece].ObjectName} has no material in slot {slot}.");
                string? baked = bakedColours[piece].GetValueOrDefault(slot);
                // Skin with no skin slot to go to keeps the outfit's own skin picture (its UVs are laid out for it; the
                // new body's picture is laid out differently and would put nails and toe gaps in the wrong places),
                // toned to the new body's skin.
                var skinPaint = OutfitSwapBodies.IsSkin(donorMaterial);
                var maps = await ReadMapsAsync(scene, material, baked, cancellationToken).ConfigureAwait(false);
                if (skinPaint)
                {
                    skinTone ??= await SkinToneAsync(targetMaterials, targetScenes, targetBakes, cancellationToken).ConfigureAwait(false);
                    Tone(maps.Colour, skinTone);
                }
                if (material.Translucent && !holder.Translucent)
                {
                    skinTone ??= await SkinToneAsync(targetMaterials, targetScenes, targetBakes, cancellationToken).ConfigureAwait(false);
                    Flatten(maps.Colour, maps.Opacity, skinTone);
                }
                if (baked is not null) curve?.Apply(maps.Colour);
                // The roughness and metalness the donor's shader computed, to paint the target's own maps from.
                if (baked is not null && surfaceBakes?[piece].GetValueOrDefault(slot) is { } surface && File.Exists(surface))
                    maps.Surface = Resize(await PngRgbaCodec.ReadAsync(surface, cancellationToken).ConfigureAwait(false), maps.Colour.Width, maps.Colour.Height);
                tiles.Add((maps, canvas.Rect(piece, slot), material));
            }
            foreach (var role in new[] { OutfitSwapPlanner.Role.Colour, OutfitSwapPlanner.Role.Normal, OutfitSwapPlanner.Role.Mask,
                         OutfitSwapPlanner.Role.Specular, OutfitSwapPlanner.Role.Orm, OutfitSwapPlanner.Role.Emissive,
                         OutfitSwapPlanner.Role.Panning })
            {
                var name = OutfitSwapPlanner.Texture(holder, role);
                // Only the class's own pictures; the game's shared pictures (and other classes') are used elsewhere too.
                if (name is null || !OutfitSwapBodies.OwnsPicture(holderScene.Scene.Object.ObjectName, name)) continue;
                var size = holderScene.PictureSize(name) ?? (1024, 1024);
                RgbaImage image;
                RgbaImage Source(Maps maps) => Surfaced(maps, role, name, calibration);
                if (canvas.Grid == 1)
                {
                    var source = Source(tiles[0].Maps);
                    var scale = Math.Clamp(1 << (int)Math.Ceiling(Math.Log2(Math.Max(1.0, source.Width / (double)size.Width))), 1, 4);
                    image = Resize(source, size.Width * scale, size.Height * scale);
                }
                else
                {
                    image = new RgbaImage(size.Width * canvas.Grid, size.Height * canvas.Grid, new byte[size.Width * canvas.Grid * size.Height * canvas.Grid * 4]);
                    for (var i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = 255;
                    foreach (var (maps, rect, _) in tiles)
                    {
                        var r = rect!.Value;
                        int x0 = (int)(r.X * image.Width), y0 = (int)(r.Y * image.Height), x1 = (int)(r.Z * image.Width), y1 = (int)(r.W * image.Height);
                        Blit(Resize(Source(maps), x1 - x0, y1 - y0), image, x0, y0);
                    }
                }
                var path = Path.Combine(outputDirectory, SafeName(name) + ".png");
                var temporary = path + $".{Guid.NewGuid():N}.tmp";
                await PngRgbaCodec.WriteAsync(temporary, image, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
                pictures[name] = path;
            }
            // Colours are in the pictures now: the colour values go neutral (and the colour-change mask is cleared).
            foreach (var (_, material) in holders)
            {
                var vectors = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in new[] { "diffusecolor", "basecolor_color", "diffusecolor_a", "diffusecolor_b", "diffusecolor_c" })
                    if (material.Vectors.ContainsKey(key)) vectors[key] = [1, 1, 1, 1];
                var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                if (canvas.Grid == 1)
                {
                    var donor = tiles[0].Material;
                    if (donor.Vectors.TryGetValue("emissive_color", out var glow) && material.Vectors.ContainsKey("emissive_color"))
                        vectors["emissive_color"] = glow;
                    foreach (var key in new[] { "emissive_intensity", "emissive_intensitymin", "emissive_flicker_speed" })
                        if (donor.Scalars.TryGetValue(key, out var value) && material.Scalars.ContainsKey(key)) scalars[key] = value;
                }
                materials[material.Name] = new ModelViewerMaterialEdit(scalars, vectors, new Dictionary<string, string>());
            }
        }
        return new OutfitSwapPictureResult(pictures, materials);
    }

    private sealed class Maps
    {
        public required RgbaImage Colour { get; init; }
        public required RgbaImage Normal { get; init; }
        public required RgbaImage Mask { get; init; }
        public required RgbaImage Specular { get; init; }
        public required RgbaImage Orm { get; init; }
        public required RgbaImage Emissive { get; init; }

        /// <summary>How see-through the game's shader draws each texel (from the bake), when known.</summary>
        public byte[]? Opacity { get; init; }

        /// <summary>Roughness (red) and metalness (green) as the donor's shader computes them, when baked.</summary>
        public RgbaImage? Surface { get; set; }

        public RgbaImage For(OutfitSwapPlanner.Role role) => role switch
        {
            OutfitSwapPlanner.Role.Colour => Colour,
            OutfitSwapPlanner.Role.Normal => Normal,
            OutfitSwapPlanner.Role.Mask => Mask,
            OutfitSwapPlanner.Role.Specular => Specular,
            // The replaced outfit's moving pattern (lace, sparkle) follows its own layout; with another outfit painted in
            // it would show as blotches, so it is cleared.
            OutfitSwapPlanner.Role.Panning => Solid(Colour.Width, Colour.Height, 0, 0, 0, 0),
            OutfitSwapPlanner.Role.Orm => Orm,
            _ => Emissive,
        };
    }

    private static async Task<Maps> ReadMapsAsync(OutfitSwapScene scene, OutfitSwapMaterial material, string? baked, CancellationToken token)
    {
        async Task<RgbaImage?> Read(OutfitSwapPlanner.Role role)
        {
            var name = OutfitSwapPlanner.Texture(material, role);
            var path = name is null ? null : scene.PicturePath(name);
            return path is not null && File.Exists(path) ? await PngRgbaCodec.ReadAsync(path, token).ConfigureAwait(false) : null;
        }
        var colour = await Read(OutfitSwapPlanner.Role.Colour).ConfigureAwait(false)
                     ?? throw new InvalidDataException($"{material.Name} has no colour picture Arkive can read.");
        int w = colour.Width, h = colour.Height;
        byte[]? opacity = null;
        if (baked is not null && File.Exists(baked))
        {
            // The game shader's colour, with the picture's own alpha (the cut-out mask both material kinds read).
            var shaded = Resize(await PngRgbaCodec.ReadAsync(baked, token).ConfigureAwait(false), w, h);
            // Texels the shader never drew (cut away, or between pieces of the layout) keep the picture's own colour, toned
            // like the drawn ones, so a cut-out's edges do not darken when the game blends the picture's smaller copies.
            var tone = new float[3];
            for (var c = 0; c < 3; c++)
            {
                var ratios = new List<float>();
                for (var i = 0; i < w * h; i += 3)
                    if (shaded.Pixels[i * 4 + 3] >= 200 && Linear(colour.Pixels[i * 4 + c]) > 0.03f)
                        ratios.Add(Linear(shaded.Pixels[i * 4 + c]) / Linear(colour.Pixels[i * 4 + c]));
                ratios.Sort();
                tone[c] = ratios.Count > 0 ? ratios[ratios.Count / 2] : 1;
            }
            var merged = new byte[w * h * 4];
            for (var i = 0; i < w * h; i++)
            {
                var drawn = shaded.Pixels[i * 4 + 3] > 0;
                for (var c = 0; c < 3; c++)
                    merged[i * 4 + c] = drawn ? shaded.Pixels[i * 4 + c] : Encode(Math.Min(1, Linear(colour.Pixels[i * 4 + c]) * tone[c]));
                merged[i * 4 + 3] = colour.Pixels[i * 4 + 3];
            }
            opacity = new byte[w * h];
            for (var i = 0; i < w * h; i++) opacity[i] = shaded.Pixels[i * 4 + 3];
            colour = new RgbaImage(w, h, merged);
        }
        var normal = await Read(OutfitSwapPlanner.Role.Normal).ConfigureAwait(false) is { } n ? Resize(n, w, h) : Solid(w, h, 128, 128, 255, 255);
        var orm = await Read(OutfitSwapPlanner.Role.Orm).ConfigureAwait(false) is { } o ? Resize(o, w, h) : null;
        var specular = await Read(OutfitSwapPlanner.Role.Specular).ConfigureAwait(false) is { } s ? Resize(s, w, h) : null;
        if (orm is null)
        {
            // From an older material's gloss: no occlusion, roughness = 1 − gloss, not metal.
            orm = Solid(w, h, 255, 0, 0, 255);
            for (var i = 0; i < w * h; i++) orm.Pixels[i * 4 + 1] = (byte)(255 - (specular?.Pixels[i * 4 + 3] ?? 128));
        }
        if (specular is null)
        {
            // For an older material: specular colour (dull, or the base colour on metal) and gloss = 1 − roughness.
            specular = Solid(w, h, 0, 0, 0, 255);
            for (var i = 0; i < w * h; i++)
            {
                float occlusion = orm.Pixels[i * 4] / 255f, rough = orm.Pixels[i * 4 + 1] / 255f, metal = orm.Pixels[i * 4 + 2] / 255f;
                for (var c = 0; c < 3; c++)
                    specular.Pixels[i * 4 + c] = (byte)Math.Clamp((0.35f * (1 - metal) + colour.Pixels[i * 4 + c] / 255f * metal) * occlusion * 255, 0, 255);
                specular.Pixels[i * 4 + 3] = (byte)Math.Clamp((1 - rough) * 255, 0, 255);
            }
        }
        var emissiveName = OutfitSwapPlanner.Texture(material, OutfitSwapPlanner.Role.Emissive);
        var emissive = emissiveName is not null && emissiveName.StartsWith("pc_", StringComparison.OrdinalIgnoreCase) &&
                       await Read(OutfitSwapPlanner.Role.Emissive).ConfigureAwait(false) is { } e
            ? Resize(e, w, h) : Solid(w, h, 0, 0, 0, 255);
        return new Maps
        {
            Colour = colour, Normal = normal, Mask = Solid(w, h, 0, 0, 0, 255), Specular = specular, Orm = orm, Emissive = emissive,
            Opacity = opacity,
        };
    }

    /// <summary>
    /// See-through cloth (sheer sleeves, stockings) drawn by a solid or cut-out material: what showed through it is
    /// painted in instead (bare skin, as most sheer cloth lies on it), so it neither turns solid grey nor full of holes.
    /// Nearly clear parts are cut out.
    /// </summary>
    private static void Flatten(RgbaImage colour, byte[]? opacity, byte[] under)
    {
        var pixels = colour.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // The shader's own opacity where the bake gave one (sheer cloth is often drawn fainter than its picture).
            var alpha = Math.Min(pixels[i + 3], opacity?[i / 4] ?? (byte)255) / 255f;
            // Faint parts (the sheer body of a stocking, the fringe of fur) are cut away rather than painted as skin:
            // painted, they cover the skin's own detail (toes, nails) with a flat skin-coloured layer.
            if (alpha < 0.5f) { pixels[i + 3] = 0; continue; }
            for (var c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Clamp(MathF.Round(pixels[i + c] * alpha + under[c] * (1 - alpha)), 0, 255);
            pixels[i + 3] = 255;
        }
    }

    /// <summary>
    /// The new body's skin colour as its shader draws it (its bake), or its skin picture when there is no bake; a light skin
    /// tone when neither can be read. The median of each channel, so tattoos and the space around the layout do not shift it.
    /// </summary>
    private static async Task<byte[]> SkinToneAsync(IEnumerable<(OutfitSwapScene Scene, OutfitSwapMaterial Material)> materials,
        IReadOnlyList<OutfitSwapScene> targetScenes, IReadOnlyList<IReadOnlyDictionary<int, string>>? targetBakes, CancellationToken token)
    {
        foreach (var (scene, material) in materials)
        {
            if (!OutfitSwapBodies.IsSkin(material.Name)) continue;
            var piece = targetScenes.ToList().IndexOf(scene);
            RgbaImage? image = null;
            var bakedOnly = false;
            if (targetBakes is not null && piece >= 0 && targetBakes[piece].TryGetValue(material.Slot, out var bake) && File.Exists(bake))
            {
                image = await PngRgbaCodec.ReadAsync(bake, token).ConfigureAwait(false);
                bakedOnly = true;
            }
            else if (OutfitSwapPlanner.Texture(material, OutfitSwapPlanner.Role.Colour) is { } name && scene.PicturePath(name) is { } path && File.Exists(path))
                image = await PngRgbaCodec.ReadAsync(path, token).ConfigureAwait(false);
            if (image is null) continue;
            var tone = new byte[3];
            for (var c = 0; c < 3; c++)
            {
                var values = new List<byte>();
                for (var i = 0; i < image.Pixels.Length; i += 4 * 7)
                    if (!bakedOnly || image.Pixels[i + 3] > 200) values.Add(image.Pixels[i + c]);
                if (values.Count == 0) break;
                values.Sort();
                tone[c] = values[values.Count / 2];
            }
            return tone;
        }
        return [222, 190, 180];
    }

    /// <summary>
    /// A tile's ORM or specular picture with the roughness (and metalness) channel set so the target's shader computes the
    /// donor's values: the donor's shader often reshapes its maps (per dye region, by powers) in ways a plain conversion
    /// of its pictures misses, which is how cloth loses its sheen.
    /// </summary>
    private static RgbaImage Surfaced(Maps maps, OutfitSwapPlanner.Role role, string name, OutfitSwapCalibration? calibration)
    {
        var source = maps.For(role);
        if (maps.Surface is not { } surface || calibration is null || role is not (OutfitSwapPlanner.Role.Orm or OutfitSwapPlanner.Role.Specular))
            return source;
        var rough = calibration.Roughness.TryGetValue(name, out var r) ? r : default;
        var metal = calibration.Metalness.TryGetValue(name, out var m) ? m : default;
        if (rough.Curve is null && metal.Curve is null) return source;
        var pixels = (byte[])source.Pixels.Clone();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (rough.Curve is not null) pixels[i + rough.Channel] = rough.Curve.Map(surface.Pixels[i]);
            if (metal.Curve is not null) pixels[i + metal.Channel] = metal.Curve.Map(surface.Pixels[i + 1]);
        }
        return new RgbaImage(source.Width, source.Height, pixels);
    }

    /// <summary>Scales a picture's colours (in linear light, per channel) so its median matches <paramref name="tone"/>.</summary>
    private static void Tone(RgbaImage colour, byte[] tone)
    {
        var pixels = colour.Pixels;
        var table = new byte[3][];
        for (var c = 0; c < 3; c++)
        {
            var values = new List<byte>();
            for (var i = 0; i < pixels.Length; i += 4 * 5)
                if (pixels[i + 3] > 128) values.Add(pixels[i + c]);
            if (values.Count < 100) return;
            values.Sort();
            var gain = Math.Clamp(Linear(tone[c]) / Math.Max(Linear(values[values.Count / 2]), 1e-3f), 0.7f, 1.4f);
            table[c] = new byte[256];
            for (var v = 0; v < 256; v++) table[c][v] = Encode(Math.Min(1, Linear((byte)v) * gain));
        }
        for (var i = 0; i < pixels.Length; i += 4)
            for (var c = 0; c < 3; c++) pixels[i + c] = table[c][pixels[i + c]];
    }

    private static float Linear(byte value)
    {
        var c = value / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private static byte Encode(float linear)
    {
        var c = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(c * 255), 0, 255);
    }

    private static RgbaImage Solid(int w, int h, byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { pixels[i * 4] = r; pixels[i * 4 + 1] = g; pixels[i * 4 + 2] = b; pixels[i * 4 + 3] = a; }
        return new RgbaImage(w, h, pixels);
    }

    /// <summary>Bilinear resize (box-averaged first when shrinking by more than half).</summary>
    internal static RgbaImage Resize(RgbaImage source, int width, int height)
    {
        if (source.Width == width && source.Height == height) return source;
        while (source.Width >= width * 2 && source.Height >= height * 2) source = Half(source);
        var result = new byte[width * height * 4];
        Parallel.For(0, height, y =>
        {
            var sy = Math.Clamp((y + 0.5f) * source.Height / height - 0.5f, 0, source.Height - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, source.Height - 1);
            var fy = sy - y0;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + 0.5f) * source.Width / width - 0.5f, 0, source.Width - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, source.Width - 1);
                var fx = sx - x0;
                for (var c = 0; c < 4; c++)
                {
                    float a = source.Pixels[(y0 * source.Width + x0) * 4 + c], b = source.Pixels[(y0 * source.Width + x1) * 4 + c];
                    float d = source.Pixels[(y1 * source.Width + x0) * 4 + c], e = source.Pixels[(y1 * source.Width + x1) * 4 + c];
                    result[(y * width + x) * 4 + c] = (byte)Math.Clamp(MathF.Round((a * (1 - fx) + b * fx) * (1 - fy) + (d * (1 - fx) + e * fx) * fy), 0, 255);
                }
            }
        });
        return new RgbaImage(width, height, result);
    }

    private static RgbaImage Half(RgbaImage source)
    {
        int w = source.Width / 2, h = source.Height / 2;
        var result = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 4; c++)
                    result[(y * w + x) * 4 + c] = (byte)((source.Pixels[(2 * y * source.Width + 2 * x) * 4 + c] + source.Pixels[(2 * y * source.Width + 2 * x + 1) * 4 + c] +
                                                          source.Pixels[((2 * y + 1) * source.Width + 2 * x) * 4 + c] + source.Pixels[((2 * y + 1) * source.Width + 2 * x + 1) * 4 + c] + 2) / 4);
        return new RgbaImage(w, h, result);
    }

    private static void Blit(RgbaImage tile, RgbaImage canvas, int x0, int y0)
    {
        for (var y = 0; y < tile.Height; y++)
            Buffer.BlockCopy(tile.Pixels, y * tile.Width * 4, canvas.Pixels, ((y0 + y) * canvas.Width + x0) * 4, tile.Width * 4);
    }

    private static string SafeName(string name) => new(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
}
