using System.IO;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Packages;
using Arkive.Core.Particles;
using Arkive.Core.Proportions;
using Arkive.Core.Skills;
using Arkive.Core.Viewer;

namespace Arkive.App;

/// <summary>The classes and skills the viewer offers for a player body, and the class it starts on.</summary>
public sealed record SkillPreviewOffer(string DefaultClass, IReadOnlyList<SkillPreviewClass> Classes);

public sealed record SkillPreviewClass(string File, string Name, IReadOnlyList<SkillPreviewSkill> Skills);

public sealed record SkillPreviewSkill(int Id, string Name, IReadOnlyList<string> Branches);

/// <summary>A prepared skill: the scene to show it on (the character with its weapon and any missing outfit half;
/// null to keep the shown scene) and the playback message for the page.</summary>
public sealed record SkillPlayback(string? SceneJson, object Message);

/// <summary>
/// The model viewer's skill preview: a player character plays one of its class's skills from the game's own action
/// data, with every effect the skill starts placed at its time and socket.
/// </summary>
public sealed class SkillPreviewService(
    PackageIndexStore index,
    Func<string?> libraryRoot,
    Func<GameAssetMetadataIndex?> metadata,
    UpkPackageReader reader,
    IChunkProtectionProvider protectionProvider,
    ModelViewerAnimationService animations,
    ModelViewerSceneService scenes,
    string sceneCacheRoot,
    ParticleAssetFinder assets,
    Action<string> log)
{
    private Task<SkillCatalog?>? _catalog;
    private IReadOnlyList<SkillPreviewClass>? _classes;
    private readonly Dictionary<string, ClassLook?> _looks = new(StringComparer.OrdinalIgnoreCase);

    // The class each player body is most often seen as; the viewer lets the user pick another.
    private static readonly IReadOnlyDictionary<string, string> DefaultClass = new Dictionary<string, string>
    {
        ["guardianknight"] = "DRAGONKNIGHT", ["female-assassin"] = "BLADE", ["female-warrior"] = "BERSERKER_FEMALE",
        ["male-warrior"] = "BERSERKER", ["female-mage"] = "ELEMENTALMASTER", ["female-martial-artist"] = "BATTLEMASTER",
        ["male-gunner"] = "DEVILHUNTER", ["female-specialist"] = "YINYANGSHI", ["male-specialist"] = "SPECIALIST_MALE",
    };

    private Task<SkillCatalog?> CatalogAsync() => _catalog ??= Task.Run(() =>
    {
        try
        {
            return libraryRoot() is { Length: > 0 } root ? SkillCatalog.Load(root) : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log($"Skill preview unavailable: {exception.Message}");
            _catalog = null;
            return null;
        }
    });

    /// <summary>Whether the mesh is a player body, which can play its class's skills (decided without reading anything).</summary>
    public static bool CanOffer(ModelViewerRequest request) =>
        request.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
        BodyProfiles.ForObjectName(request.ObjectName) is { IsNpc: false };

    /// <summary>Null when the mesh is not a player body or the game's skill data can't be read.</summary>
    public async Task<SkillPreviewOffer?> OfferAsync(ModelViewerRequest request, CancellationToken cancellationToken)
    {
        if (!CanOffer(request) || BodyProfiles.ForObjectName(request.ObjectName) is not { } profile) return null;
        var classes = _classes ??= await Task.Run(ClassesAsync, cancellationToken);
        if (classes is null) return null;
        var preferred = DefaultClass.GetValueOrDefault(profile.Id);
        var start = classes.FirstOrDefault(item => item.File.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ?? classes.FirstOrDefault();
        return start is null ? null : new SkillPreviewOffer(start.File, classes);
    }

    // Every class with skills of its own, read once.
    private async Task<IReadOnlyList<SkillPreviewClass>?> ClassesAsync()
    {
        if (await CatalogAsync() is not { } catalog) return null;
        var classNames = metadata()?.Entities
            .Where(entity => entity.EntityType.Equals("PC", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entity => entity.EntityId).ToDictionary(group => group.Key, group => group.First().DisplayName) ?? [];
        var classes = catalog.Classes.Where(file => catalog.Skills(file).Any(skill => skill.LearnClass != 0)).Select(file =>
        {
            var skills = catalog.Skills(file);
            var learnClass = skills.Where(skill => skill.LearnClass != 0).GroupBy(skill => skill.LearnClass)
                .OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault();
            var name = classNames.TryGetValue(learnClass, out var display) ? GameClassNames.Label(display) : file;
            // Only the class's own skills: an action file also holds shared actions (dying, standing up).
            return new SkillPreviewClass(file, name, skills.Where(skill => skill.LearnClass != 0).Select(skill => new SkillPreviewSkill(skill.Id, skill.Name,
                SkillCatalog.Branches(skill.Action).Select(branch => branch.Name).ToArray())).ToArray());
        }).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return classes;
    }

    /// <summary>
    /// Everything the viewer needs to play one branch of a skill: the class's skill animations, each effect's
    /// particle model (from the game's current packages, so installed mods show) and their textures and meshes.
    /// </summary>
    /// <param name="progress">What is being done and how far along (0-1).</param>
    public async Task<SkillPlayback> PrepareAsync(ModelViewerRequest request, string classFile, int skillId, int branchIndex,
        Action<string, double?> progress, CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var timings = new List<string>();
        void Mark(string step) { timings.Add($"{step} {watch.Elapsed.TotalSeconds:0.0}s"); watch.Restart(); }
        progress("Reading the skill…", 0.02);
        var catalog = await CatalogAsync() ?? throw new InvalidDataException("The game's skill data could not be read.");
        var skill = catalog.Skills(classFile).FirstOrDefault(item => item.Id == skillId) ??
                    throw new InvalidDataException("That skill is not in the game's data.");
        var branches = SkillCatalog.Branches(skill.Action);
        var branch = branches[Math.Clamp(branchIndex, 0, branches.Count - 1)];
        var root = libraryRoot() ?? throw new InvalidOperationException("Choose the game's Packages folder first.");
        var look = await LookAsync(root, classFile, cancellationToken);
        Mark("look");

        // The class's skill animations: the AnimSet holding the skill's first animation, exported once with its SK_ sequences.
        progress("Preparing the skill's animations (the first time for a class takes a while)…", 0.05);
        var animNames = branch.Stages.SelectMany(stage => stage.Notifies)
            .Where(notify => notify.Kind == "Anim" && notify.Asset is not null).Select(notify => notify.Asset!).ToArray();
        var animSet = await FindAnimSetAsync(root, animNames[0], look, cancellationToken) ??
                      throw new InvalidDataException($"The animation {animNames[0]} was not found in the indexed packages.");
        var set = await animations.PrepareAsync(animSet.Package, animSet.Name, cancellationToken: cancellationToken, skills: true);
        Mark("animations");

        // Each effect's particle model, by "Package.Object", read side by side from the packages the skill names.
        progress("Reading the skill's effects…", 0.25);
        var notifies = branch.Stages.SelectMany(stage => stage.Notifies)
            .Where(notify => notify.IsParticle && !notify.IsCamera && notify.Asset is not null &&
                             // FX_Post effects are screen effects (blur, colour noise) drawn over the whole view.
                             !notify.Asset.StartsWith("FX_Post.", StringComparison.OrdinalIgnoreCase)).ToArray();
        var systems = new System.Collections.Concurrent.ConcurrentDictionary<string, ParticleEffectModel>(StringComparer.OrdinalIgnoreCase);
        // Effects share modules with effects in other packages, found by their package's alias.
        var packageFiles = await assets.PackageFilesAsync(cancellationToken);
        var effectNames = notifies.Select(notify => notify.Asset!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var read = 0;
        await Parallel.ForEachAsync(effectNames, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (asset, token) =>
            {
                var separator = asset.IndexOf('.');
                var (alias, name) = (asset[..separator], asset[(separator + 1)..]);
                var found = await assets.FindObjectAsync(name, "particlesystem", alias, token);
                if (found is null)
                {
                    log($"Skill preview: effect {asset} was not found in the index.");
                }
                else
                {
                    try
                    {
                        systems[asset] = await Task.Run(() => ParticleEffectModels.Build(found.PackagePath, alias, found.Entry.ObjectName, null,
                            protectionProvider, other => packageFiles?.GetValueOrDefault(other), assets.Catalogs.Read), token);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException)
                    {
                        log($"Skill preview: effect {asset} could not be read: {exception.Message}");
                    }
                }
                var count = Interlocked.Increment(ref read);
                progress($"Reading the skill's effects ({count} of {effectNames.Length})…", 0.25 + 0.15 * count / effectNames.Length);
            });
        Mark("effects");

        // The character as the skill shows it: the class's weapon in its hand and, for half an outfit, the class's
        // default other half.
        progress("Preparing the class's weapon and outfit…", 0.4);
        var (sceneJson, weapons) = await PrepareLookAsync(request, root, classFile, look, cancellationToken);
        Mark("weapon");

        progress("Preparing the effects' textures, shaders and meshes…", 0.5);
        var emitters = systems.Values.SelectMany(model => model.Emitters).ToArray();
        var found = await assets.FindAsync(emitters, new Progress<double>(fraction =>
            progress($"Preparing the effects' textures, shaders and meshes ({fraction:P0})…", 0.5 + 0.45 * fraction)), cancellationToken);
        Mark("assets");
        progress("Starting the skill…", 0.95);
        var animationFolder = Path.GetRelativePath(sceneCacheRoot, set.Directory).Replace('\\', '/');
        log($"Skill preview ready: class={classFile}, skill={skill.Id} {skill.Name}, branch={branch.Name}, stages={branch.Stages.Count}, " +
            $"effects={systems.Count}/{notifies.Select(notify => notify.Asset).Distinct().Count()}, animset={set.AnimSetName}, " +
            $"times: {string.Join(", ", timings)}.");
        return new SkillPlayback(sceneJson, new
        {
            type = "skill",
            weapons,
            name = skill.Name,
            branch = branch.Name,
            animations = new
            {
                baseUrl = $"https://scenes.arkive.local/{animationFolder}/",
                bones = set.Bones,
                list = set.Animations.Select(item => new { name = item.Name, frames = item.Frames, rate = item.Rate, file = item.File }),
            },
            stages = branch.Stages.Select(stage => new
            {
                name = stage.Name,
                duration = stage.Duration,
                rate = stage.Rate,
                anim = stage.Notifies.FirstOrDefault(notify => notify.Kind == "Anim")?.Asset,
                notifies = stage.Notifies.Where(notify => notify.IsParticle && !notify.IsCamera && notify.Asset is not null &&
                                                          systems.ContainsKey(notify.Asset))
                    .Select(notify => new
                    {
                        start = notify.Start, duration = notify.Duration, system = notify.Asset, socket = notify.Socket, group = notify.Group,
                        offset = notify.Offset, rotation = notify.Rotation, scale = notify.Scale,
                        parameters = notify.Parameters.Select(parameter => new
                        {
                            name = parameter.Name, isVector = parameter.IsVector, value = parameter.Value,
                            x = parameter.X, y = parameter.Y, z = parameter.Z,
                        }),
                    }),
            }),
            systems,
            assets = ParticleEditorWindow.AssetsMessage(found),
        });
    }

    /// <summary>
    /// The scene a skill plays on: the shown mesh, the class's weapons and, when the mesh is only an outfit's upper or
    /// lower half, the class's default other half; joined like the proportion editor's outfit. Null when nothing is
    /// added. The weapons list says which socket holds each weapon and the weapon's own sockets.
    /// </summary>
    private async Task<ClassLook?> LookAsync(string root, string classFile, CancellationToken cancellationToken)
    {
        if (_looks.TryGetValue(classFile, out var look)) return look;
        try
        {
            look = await Task.Run(() => CharacterLooks.ForClass(root, classFile, cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            log($"Skill preview: the class look of {classFile} could not be read: {exception.Message}");
            look = null;
        }
        return _looks[classFile] = look;
    }

    private async Task<(string? SceneJson, object[] Weapons)> PrepareLookAsync(
        ModelViewerRequest request, string root, string classFile, ClassLook? look, CancellationToken cancellationToken)
    {
        if (look is null) return (null, []);
        var name = request.ObjectName.ToLowerInvariant();
        var halves = new List<LookMesh>();
        if (name.Contains("upper", StringComparison.Ordinal) && look.Parts.TryGetValue("Lower", out var lower)) halves.Add(lower);
        if (name.Contains("lower", StringComparison.Ordinal) && look.Parts.TryGetValue("Upper", out var upper)) halves.Add(upper);
        var pieces = new List<ModelViewerSceneResult> { await Task.Run(() => scenes.PrepareAsync(request, null, cancellationToken), cancellationToken) };
        foreach (var half in halves)
            if (await FindMeshAsync(root, half, cancellationToken) is { } halfRequest)
                pieces.Add(await Task.Run(() => scenes.PrepareAsync(halfRequest, null, cancellationToken), cancellationToken));
        var weapons = new List<object>();
        // The class's skeleton mesh carries the sockets skills place effects at; outfit pieces often have none.
        if (look.DefaultMesh is { } skeleton && await FindMeshAsync(root, skeleton, cancellationToken) is { } skeletonRequest)
        {
            try
            {
                var sockets = await Task.Run(() => scenes.PrepareAsync(skeletonRequest, null, cancellationToken), cancellationToken);
                weapons.Add(new { part = (string?)null, socket = (string?)null, sockets = sockets.Scene.Sockets });
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or Arkive.Core.External.UmodelBridgeException)
            {
                log($"Skill preview: the sockets of {skeleton.Object} could not be read: {exception.Message}");
            }
        }
        // A weapon used in both hands (Gunslinger's pistols) is one mesh; the preview shows it once.
        foreach (var weapon in look.Weapons.DistinctBy(weapon => weapon.Mesh.Object, StringComparer.OrdinalIgnoreCase))
        {
            if (await FindMeshAsync(root, weapon.Mesh, cancellationToken) is not { } weaponRequest) continue;
            var scene = await Task.Run(() => scenes.PrepareAsync(weaponRequest, null, cancellationToken), cancellationToken);
            pieces.Add(scene);
            weapons.Add(new { part = scene.Scene.Object.ObjectName, socket = weapon.BattleSocket, sockets = scene.Scene.Sockets });
        }
        if (pieces.Count == 1) return (null, weapons.ToArray());
        log($"Skill preview look: class={classFile}, look={look.Name}, halves={halves.Count}, weapons={weapons.Count}.");
        return (BodyProportionSceneCombiner.Combine(pieces, sceneCacheRoot).SceneJson, weapons.ToArray());
    }

    private async Task<ModelViewerRequest?> FindMeshAsync(string root, LookMesh mesh, CancellationToken cancellationToken)
    {
        // Through the look's own package name first; the index search finds pieces whose package was renamed.
        if (await assets.FindObjectAsync(mesh.Object, "skeletalmesh", mesh.Package, cancellationToken) is { } located &&
            located.Entry.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            return new ModelViewerRequest(located.PackagePath, mesh.Package, root, located.Entry.ExportIndex, located.Entry.SerialOffset,
                located.Entry.SerialSize, located.Entry.ClassName, located.Entry.ObjectName);
        var found = await index.SearchAsync(root, mesh.Object, null, metadata(), PackageIndexSearchScope.ObjectName, null, resultLimit: 50);
        var match = found.Matches.FirstOrDefault(item => item.ObjectName.Equals(mesh.Object, StringComparison.OrdinalIgnoreCase) &&
                                                         item.PackageAlias.Equals(mesh.Package, StringComparison.OrdinalIgnoreCase) &&
                                                         item.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
                                                         File.Exists(item.SourcePath))
                    ?? found.Matches.FirstOrDefault(item => item.ObjectName.Equals(mesh.Object, StringComparison.OrdinalIgnoreCase) &&
                                                            item.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
                                                            File.Exists(item.SourcePath));
        if (match is null)
        {
            log($"Skill preview: {mesh.Package}.{mesh.Object} was not found in the index.");
            return null;
        }
        return new ModelViewerRequest(match.SourcePath, match.PackageAlias, root, match.ExportIndex, match.SerialOffset,
            match.SerialSize, match.ClassName, match.ObjectName);
    }

    private readonly Dictionary<string, (string Package, string Name)> _animSets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The package and AnimSet holding an animation sequence. The asset index does not list sequences, so the player
    /// AnimSets it lists are checked for the sequence's name in their package's name table.
    /// </summary>
    private async Task<(string Package, string Name)?> FindAnimSetAsync(string root, string sequence, ClassLook? look,
        CancellationToken cancellationToken)
    {
        if (_animSets.TryGetValue(sequence, out var known)) return known;
        // The class look names its AnimSets; the class's own (listed after the body's) usually holds its skills.
        foreach (var named in (look?.AnimSets ?? []).Reverse())
        {
            if (await assets.FindObjectAsync(named.Object, "animset", named.Package, cancellationToken) is not { } located) continue;
            if (!located.Catalog.Names.Any(name => name.Value.Equals(sequence, StringComparison.OrdinalIgnoreCase))) continue;
            return _animSets[sequence] = (located.PackagePath, located.Entry.ObjectName);
        }
        var found = await index.SearchAsync(root, "_ani", "Object", metadata(), PackageIndexSearchScope.ObjectName, null, resultLimit: 5000);
        var candidates = found.Matches
            .Where(item => item.ClassName.Equals("AnimSet", StringComparison.OrdinalIgnoreCase) &&
                           item.PackageAlias.StartsWith("pc_", StringComparison.OrdinalIgnoreCase) && File.Exists(item.SourcePath))
            .GroupBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = await Task.Run(() =>
        {
            foreach (var package in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AssetCatalog catalogue;
                try
                {
                    catalogue = new AssetCatalogReader(reader, protectionProvider).Read(package.Key);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException)
                {
                    continue;
                }
                if (!catalogue.Names.Any(name => name.Value.Equals(sequence, StringComparison.OrdinalIgnoreCase))) continue;
                var set = catalogue.Assets.FirstOrDefault(asset => asset.ClassName.Equals("AnimSet", StringComparison.OrdinalIgnoreCase));
                if (set is not null) return ((string Package, string Name)?)(package.Key, set.ObjectName);
            }
            return null;
        }, cancellationToken);
        if (result is { } value) _animSets[sequence] = value;
        return result;
    }
}
