using Arkive.Core.Databases;

namespace Arkive.Core.Skills;

/// <summary>A class skill the preview can play: its action and its in-game name.</summary>
/// <param name="ClassFile">The action file it comes from (DRAGONKNIGHT).</param>
/// <param name="Name">The in-game name, or the developers' name when the skill table has none.</param>
/// <param name="LearnClass">The class id that learns it, 0 when the skill table does not say.</param>
public sealed record SkillCatalogEntry(string ClassFile, int Id, string Name, long LearnClass, SkillAction Action);

/// <summary>One way a skill plays: the stages from where it starts to where it ends.</summary>
/// <param name="Name">The developers' name of the branch's first stage ("일반 상태"), or "Default".</param>
public sealed record SkillBranch(string Name, IReadOnlyList<SkillActionStage> Stages);

/// <summary>
/// Every class's skills from the game's action files (data3.lpk) with their names (data2.lpk), for the skill preview.
/// Only skills that play an animation and start at least one effect are listed.
/// </summary>
public sealed class SkillCatalog
{
    private readonly Dictionary<string, IReadOnlyList<SkillCatalogEntry>> _classes;

    private SkillCatalog(Dictionary<string, IReadOnlyList<SkillCatalogEntry>> classes) => _classes = classes;

    /// <summary>The action files with skills, by name (DRAGONKNIGHT, BLADE...).</summary>
    public IReadOnlyList<string> Classes => _classes.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<SkillCatalogEntry> Skills(string classFile) =>
        _classes.TryGetValue(classFile, out var skills) ? skills : [];

    public static SkillCatalog Load(string packagesRoot, string language = "English", CancellationToken cancellationToken = default)
    {
        var data3 = SkillEffectNameSidecar.FindArchive(packagesRoot) ??
                    throw new FileNotFoundException("The game's data3.lpk was not found beside its Packages folder.");
        var data2 = Path.GetFullPath(Path.Combine(packagesRoot, "..", "..", "data2.lpk"));
        var names = File.Exists(data2) ? GameSkillNames.Read(data2, language, cancellationToken) : new Dictionary<long, GameSkillName>();
        var classes = new Dictionary<string, IReadOnlyList<SkillCatalogEntry>>(StringComparer.OrdinalIgnoreCase);
        using var stream = File.OpenRead(data3);
        foreach (var entry in SkillEffectNameSidecar.FindActionFiles(LostArkDataArchive.ReadEntries(stream, cancellationToken)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = Path.GetFileNameWithoutExtension(entry.Path);
            IReadOnlyList<SkillAction> actions;
            try
            {
                actions = SkillActionReader.Read(LostArkDataArchive.Read(stream, entry));
            }
            catch (InvalidDataException)
            {
                continue;
            }
            var skills = actions
                .Where(action => action.Notifies.Any(notify => notify.Kind == "Anim" && notify.Asset is not null) &&
                                 action.ParticleSystems.Any())
                .Select(action => names.TryGetValue(action.Id, out var name)
                    ? new SkillCatalogEntry(file, action.Id, name.Name, name.LearnClass, action)
                    : new SkillCatalogEntry(file, action.Id, DevelopersName(action.Name, action.Id), 0, action))
                .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (skills.Length > 0) classes[file] = skills;
        }
        return new SkillCatalog(classes);
    }

    /// <summary>
    /// The ways a skill plays. One action holds each version of a skill (its tripod and rune variants, and Guardianknight
    /// forms) one after another: a version starts at a "Main" stage, or after a stage without an animation (a state
    /// check). Where a stage plays an animation its version has already played, it is a different way through the same
    /// opening (Brutal Impact's overcharge plays its charge again): it starts a version made of the opening before that
    /// animation and the stages from there on. Versions that play the same stages are listed once.
    /// </summary>
    public static IReadOnlyList<SkillBranch> Branches(SkillAction action)
    {
        var branches = new List<SkillBranch>();
        List<SkillActionStage>? current = null;
        string? named = null;
        var previousAnimated = false;
        static string? AnimOf(SkillActionStage stage) =>
            stage.Notifies.FirstOrDefault(notify => notify.Kind == "Anim" && notify.Asset is not null)?.Asset;
        static bool Meaningful(string name) => name.Length > 0 && name is not ("StageName" or "Main");
        void Close()
        {
            if (current is not { Count: > 0 }) return;
            var lead = current[0];
            var name = named ?? (Meaningful(lead.Name) ? lead.Name : AnimOf(lead) ?? "Default");
            branches.Add(new SkillBranch(name, current));
        }
        foreach (var stage in action.Stages)
        {
            var anim = AnimOf(stage);
            if (anim is null)
            {
                previousAnimated = false;
                continue;
            }
            if (current is null || !previousAnimated || stage.Name == "Main")
            {
                Close();
                current = [];
                named = null;
            }
            else if (current.FindIndex(earlier => string.Equals(AnimOf(earlier), anim, StringComparison.OrdinalIgnoreCase)) is var again and >= 0)
            {
                Close();
                current = current.Take(again).ToList();
                named = Meaningful(stage.Name) ? stage.Name : anim;
            }
            current!.Add(stage);
            previousAnimated = true;
        }
        Close();
        // Versions are told apart by their animations and the names of their later stages (the first stage's name is
        // the state the version is for, which often differs between versions that play the same).
        var distinct = branches
            .GroupBy(branch => string.Join("|", branch.Stages.Select((stage, i) => (i > 0 ? stage.Name + ":" : "") + AnimOf(stage))),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        // Names are how the preview offers them, so each is different.
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return distinct.Select(branch =>
        {
            var count = seen[branch.Name] = seen.GetValueOrDefault(branch.Name) + 1;
            return count == 1 ? branch : branch with { Name = $"{branch.Name} ({count})" };
        }).ToArray();
    }

    // "가디언즈 크래쉬 (구) 노토스 크래시" -> the current name before "(구)" ("old"), or the id when there is none.
    private static string DevelopersName(string name, int id)
    {
        var current = name.Split("(구)", 2)[0].Trim();
        return current.Length > 0 ? current : $"Skill {id}";
    }
}
