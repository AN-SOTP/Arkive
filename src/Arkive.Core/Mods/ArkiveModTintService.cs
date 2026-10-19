using Arkive.Core.Archives;
using Arkive.Core.Materials;
using Arkive.Core.Particles;
using Arkive.Core.Textures;

namespace Arkive.Core.Mods;

/// <summary>
/// Arkive's tint for many working copies: a picture is recoloured from the game's original pixels (so tinting twice
/// does not stack), a material's colour settings take the tint and get a fresh verified candidate, and an effect's
/// colour tables take it through the effect's own recipe. Effects whose colour comes only from their materials can
/// have those materials added and tinted instead.
/// </summary>
public sealed class ArkiveModTintService : IMaterialTint
{
    private readonly ArkiveModEditWorkspaces _edits;
    private readonly IChunkProtectionProvider _protection;
    private readonly MaterialInstanceEditService _materialEdits;
    private readonly string _materialCandidateRoot;

    /// <param name="addEffectMaterials">
    /// Adds an effect's editable materials to the mod as working copies (the app resolves which packages they live in);
    /// null when effect materials cannot be added here.
    /// </param>
    public ArkiveModTintService(ArkiveModEditWorkspaces edits, IChunkProtectionProvider protection, string materialCandidateRoot,
        Func<ParticleEffectEditEntry, CancellationToken, Task<IReadOnlyList<ArkiveModLocalEdit>>>? addEffectMaterials = null)
    {
        _edits = edits ?? throw new ArgumentNullException(nameof(edits));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
        _materialEdits = new MaterialInstanceEditService(protection);
        _materialCandidateRoot = materialCandidateRoot;
        AddEffectMaterials = addEffectMaterials;
    }

    public Func<ParticleEffectEditEntry, CancellationToken, Task<IReadOnlyList<ArkiveModLocalEdit>>>? AddEffectMaterials { get; init; }

    public async Task<MaterialTintResult> TintAsync(MaterialTintRequest request, IProgress<MaterialTintProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Tint.IsValid) throw new ArgumentException("The tint has a value out of range.", nameof(request));
        var pending = new Queue<ArkiveModLocalEdit>(request.Edits);
        var seen = new HashSet<string>(request.Edits.Select(edit => edit.EditId), StringComparer.Ordinal);
        var skipped = new List<MaterialTintSkip>();
        var added = new List<ArkiveModLocalEdit>();
        int changed = 0, unchanged = 0, done = 0, total = pending.Count;
        while (pending.TryDequeue(out var edit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new MaterialTintProgress(done, total, edit.ObjectName));
            try
            {
                var outcome = edit.Entry switch
                {
                    GuidedTextureModProjectEntry texture => await TintTextureAsync(texture, request.Tint, request.FromOriginal, cancellationToken)
                        .ConfigureAwait(false),
                    MaterialInstanceEditEntry material => await TintMaterialAsync(material, request, cancellationToken)
                        .ConfigureAwait(false),
                    ParticleEffectEditEntry particle => await TintEffectAsync(particle, request.Tint, cancellationToken)
                        .ConfigureAwait(false),
                    _ => (bool?)null,
                };
                if (outcome is null)
                    skipped.Add(new(edit.ObjectName, "meshes and interface movies take no tint; edit their files instead."));
                else if (outcome.Value) changed++;
                else unchanged++;

                if (edit.Entry is ParticleEffectEditEntry effect && request.IncludeEffectMaterials && AddEffectMaterials is not null)
                {
                    foreach (var material in await AddEffectMaterials(effect, cancellationToken).ConfigureAwait(false))
                        if (seen.Add(material.EditId))
                        {
                            added.Add(material);
                            pending.Enqueue(material);
                            total++;
                        }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                skipped.Add(new(edit.ObjectName, exception.Message));
            }
            done++;
        }
        progress?.Report(new MaterialTintProgress(done, total, string.Empty));
        return new MaterialTintResult(changed, unchanged, skipped) { AddedEdits = added };
    }

    /// <summary>Recolours the picture from the game's original pixels, so the result never stacks on an earlier tint.</summary>
    private static async Task<bool?> TintTextureAsync(GuidedTextureModProjectEntry texture, TextureRecolour tint,
        bool fromOriginal, CancellationToken cancellationToken)
    {
        var target = texture.Manifest.EditableImagePath;
        if (!Path.GetExtension(target).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("its working file is a DDS; recolour it in your image editor.");
        var source = fromOriginal ? await OriginalImagePathAsync(texture, cancellationToken).ConfigureAwait(false) : target;
        var image = (await TextureEditableImageService.LoadAsync(source, cancellationToken).ConfigureAwait(false)).Image;
        var original = (byte[])image.Pixels.Clone();
        TextureNames.ForPicture(tint, texture.Manifest.ObjectName).Apply(image);
        var temporary = target + $".{Guid.NewGuid():N}.saving.png";
        await PngRgbaCodec.WriteAsync(temporary, image, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, target, overwrite: true);
        return !image.Pixels.AsSpan().SequenceEqual(original);
    }

    /// <summary>The game's own picture for a texture working copy, as its native export decoded it.</summary>
    public static async Task<string> OriginalImagePathAsync(GuidedTextureModProjectEntry texture,
        CancellationToken cancellationToken = default)
    {
        var native = await NativeTextureExportService.LoadManifestAsync(texture.Manifest.NativeTextureManifestPath,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(native.EditImageRelativePath))
            throw new NotSupportedException("the game's original picture was not kept with its working copy.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(texture.Manifest.NativeTextureManifestPath))!;
        var path = Path.GetFullPath(Path.Combine(directory, native.EditImageRelativePath));
        var relative = Path.GetRelativePath(directory, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("The original picture's path escapes its export folder.");
        return path;
    }

    private async Task<bool?> TintMaterialAsync(MaterialInstanceEditEntry material, MaterialTintRequest request,
        CancellationToken cancellationToken)
    {
        var (recipe, glows) = MaterialRecipes(request.Tint, request.LightenSurfaces);
        // From the source values: tinting again replaces the earlier tint instead of stacking on it.
        var fresh = material.Document with
        {
            Vectors = material.Document.Vectors.Select(vector => vector with
            {
                Red = vector.SourceRed, Green = vector.SourceGreen, Blue = vector.SourceBlue, Alpha = vector.SourceAlpha,
            }).ToArray(),
            Scalars = material.Document.Scalars.Select(scalar => scalar with { Value = scalar.SourceValue }).ToArray(),
        };
        var document = MaterialTints.Apply(fresh, recipe, glows);
        if (!document.Vectors.Any(vector => vector.Changed) && !document.Scalars.Any(scalar => scalar.Changed)) return false;
        var saved = await _edits.MaterialEdits.SaveAsync(material with { Document = document }, cancellationToken)
            .ConfigureAwait(false);
        var candidate = await _materialEdits.BuildCandidateAsync(saved.DocumentPath, _materialCandidateRoot, cancellationToken)
            .ConfigureAwait(false);
        await _edits.MaterialEdits.RecordCandidateAsync(saved, candidate, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The material tints a recolour stands for: surfaces and glows take the colour, or, for two-tone, surfaces the
    /// main colour and glows the highlight colour.
    /// </summary>
    public static (MaterialTintRecipe Surfaces, MaterialTintRecipe Glows) MaterialRecipes(TextureRecolour tint, float lightenSurfaces)
    {
        var surfaces = new MaterialTintRecipe(tint.Red, tint.Green, tint.Blue, tint.Strength, Math.Max(0, 1 + tint.Brightness),
            tint.Selection, tint.RangeHue, tint.RangeWidth) { LightenSurfaces = lightenSurfaces };
        var glows = tint.Style == TextureRecolourStyle.TwoTone
            ? surfaces with { Red = tint.HighlightRed, Green = tint.HighlightGreen, Blue = tint.HighlightBlue }
            : surfaces;
        return (surfaces, glows);
    }

    private async Task<bool?> TintEffectAsync(ParticleEffectEditEntry particle, TextureRecolour tint, CancellationToken cancellationToken)
    {
        var document = particle.Document;
        var report = await Task.Run(() => ParticleEffects.Inspect(document.SourcePackagePath, document.SourcePackageSha256,
            document.SystemObjectName, document.SystemObjectPath, _protection, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        var recipe = document.Recipe with
        {
            // An effect is light, so a two-tone recolour gives it the highlight colour (gold glows on white armour).
            Tint = tint.Style == TextureRecolourStyle.TwoTone
                ? new ParticleEffectTint(tint.HighlightRed, tint.HighlightGreen, tint.HighlightBlue)
                : new ParticleEffectTint(tint.Red, tint.Green, tint.Blue),
            TintStrength = tint.Strength,
            Brightness = Math.Clamp(1 + tint.Brightness, 0, ParticleEffectRecipe.MaximumMultiplier),
            TintSelection = tint.Selection,
            TintHue = tint.RangeHue,
            TintRange = tint.RangeWidth,
        };
        var patches = ParticleEffects.RecipePatches(report, recipe);
        if (patches.Count == 0) return false;
        await _edits.ParticleEdits.SaveAsync(particle with { Document = document with { Recipe = recipe, Patches = patches } },
            cancellationToken).ConfigureAwait(false);
        return true;
    }
}
