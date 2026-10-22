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
        CancellationToken cancellationToken)
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
            var tiles = new List<(Maps Maps, System.Numerics.Vector4? Rect, OutfitSwapMaterial Material)>();
            foreach (var (piece, slot) in canvas.Tiles)
            {
                var donorMaterial = donors[piece].Mesh.Materials[slot];
                var scene = donorScenes[piece];
                var material = scene.Materials.FirstOrDefault(item => item.Slot == slot)
                               ?? throw new InvalidDataException($"{donors[piece].ObjectName} has no material in slot {slot}.");
                string? baked = bakedColours[piece].GetValueOrDefault(slot);
                if (OutfitSwapBodies.IsSkin(donorMaterial))
                {
                    // Skin with no skin slot to go to: painted with the new body's own skin picture.
                    var lower = OutfitSwapBodies.IsLowerSkin(donorMaterial);
                    if (targetMaterials.FirstOrDefault(item => OutfitSwapBodies.IsSkin(item.Material.Name) &&
                                                               OutfitSwapBodies.IsLowerSkin(item.Material.Name) == lower) is { Material: not null } skin)
                    {
                        scene = skin.Scene;
                        material = skin.Material;
                        baked = null;
                    }
                }
                var maps = await ReadMapsAsync(scene, material, baked, cancellationToken).ConfigureAwait(false);
                if (material.Translucent && !holder.Translucent)
                {
                    skinTone ??= await SkinToneAsync(targetMaterials, cancellationToken).ConfigureAwait(false);
                    Flatten(maps.Colour, skinTone);
                }
                tiles.Add((maps, canvas.Rect(piece, slot), material));
            }
            foreach (var role in new[] { OutfitSwapPlanner.Role.Colour, OutfitSwapPlanner.Role.Normal, OutfitSwapPlanner.Role.Mask,
                         OutfitSwapPlanner.Role.Specular, OutfitSwapPlanner.Role.Orm, OutfitSwapPlanner.Role.Emissive })
            {
                var name = OutfitSwapPlanner.Texture(holder, role);
                // Only the class's own pictures; the game's shared pictures (and other classes') are used elsewhere too.
                if (name is null || !OutfitSwapBodies.OwnsPicture(holderScene.Scene.Object.ObjectName, name)) continue;
                var size = holderScene.PictureSize(name) ?? (1024, 1024);
                RgbaImage image;
                if (canvas.Grid == 1)
                {
                    var source = tiles[0].Maps.For(role);
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
                        Blit(Resize(maps.For(role), x1 - x0, y1 - y0), image, x0, y0);
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

        public RgbaImage For(OutfitSwapPlanner.Role role) => role switch
        {
            OutfitSwapPlanner.Role.Colour => Colour,
            OutfitSwapPlanner.Role.Normal => Normal,
            OutfitSwapPlanner.Role.Mask => Mask,
            OutfitSwapPlanner.Role.Specular => Specular,
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
        if (baked is not null && File.Exists(baked))
        {
            // The game shader's colour, with the picture's own alpha (the cut-out mask both material kinds read).
            var shaded = Resize(await PngRgbaCodec.ReadAsync(baked, token).ConfigureAwait(false), w, h);
            var merged = new byte[w * h * 4];
            for (var i = 0; i < w * h; i++)
            {
                merged[i * 4] = shaded.Pixels[i * 4];
                merged[i * 4 + 1] = shaded.Pixels[i * 4 + 1];
                merged[i * 4 + 2] = shaded.Pixels[i * 4 + 2];
                merged[i * 4 + 3] = colour.Pixels[i * 4 + 3];
            }
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
        };
    }

    /// <summary>
    /// See-through cloth (sheer sleeves, stockings) drawn by a solid or cut-out material: what showed through it is
    /// painted in instead (bare skin, as most sheer cloth lies on it), so it neither turns solid grey nor full of holes.
    /// Fully clear parts stay cut out.
    /// </summary>
    private static void Flatten(RgbaImage colour, byte[] under)
    {
        var pixels = colour.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3] / 255f;
            if (alpha < 0.02f) { pixels[i + 3] = 0; continue; }
            for (var c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Clamp(MathF.Round(pixels[i + c] * alpha + under[c] * (1 - alpha)), 0, 255);
            pixels[i + 3] = 255;
        }
    }

    /// <summary>The average colour of the new body's skin picture (a light skin tone when it has none Arkive can read).</summary>
    private static async Task<byte[]> SkinToneAsync(IEnumerable<(OutfitSwapScene Scene, OutfitSwapMaterial Material)> materials, CancellationToken token)
    {
        foreach (var (scene, material) in materials)
        {
            if (!OutfitSwapBodies.IsSkin(material.Name) || OutfitSwapPlanner.Texture(material, OutfitSwapPlanner.Role.Colour) is not { } name ||
                scene.PicturePath(name) is not { } path || !File.Exists(path)) continue;
            var image = await PngRgbaCodec.ReadAsync(path, token).ConfigureAwait(false);
            // The median of each channel, so tattoos and the empty space around the layout do not shift it.
            var tone = new byte[3];
            for (var c = 0; c < 3; c++)
            {
                var values = new List<byte>();
                for (var i = 0; i < image.Pixels.Length; i += 4 * 7) values.Add(image.Pixels[i + c]);
                values.Sort();
                tone[c] = values[values.Count / 2];
            }
            return tone;
        }
        return [222, 190, 180];
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
