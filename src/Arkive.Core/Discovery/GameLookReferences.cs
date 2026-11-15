using System.Collections.Concurrent;

namespace Arkive.Core.Discovery;

/// <summary>
/// What the game's looks add to the game-name database: for every database reference to a look
/// (EFDLItem_PC_MG_LF_00-5_Helmet.PC_MG_LF_00-5_Helmet), a reference to each mesh, material, effect and texture the
/// look draws of its own (see <see cref="GameLookSidecar"/>). A mesh stands for its family: the piece's mesh and the
/// textures named after it.
///
/// The references are kept once per look, not once per item: some hundred thousand items, NPCs and props share about
/// sixty thousand looks, and a reference is only made into a <see cref="GameAssetAssociation"/> for the assets and
/// entries a search or view actually shows.
/// </summary>
internal sealed class GameLookReferences
{
    public static GameLookReferences Empty { get; } = new(
        new Dictionary<string, Look>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, Target[]>(StringComparer.Ordinal));

    /// <summary>Something a look draws: its package and object (upper case) and, for a mesh, the family name it stands for.</summary>
    internal sealed class Target
    {
        private string? _reference;

        public Target(string className, string package, string objectName)
        {
            Class = className;
            Package = package;
            Object = objectName;
            Family = GameLookSidecar.IsMeshClass(className) ? GameLookSidecar.MeshFamily(objectName) : objectName;
        }

        public string Class { get; }
        public string Package { get; }
        public string Object { get; }
        public string Family { get; }
        public string[] Looks { get; set; } = [];
        public string Reference => _reference ??= $"{Package}.{Object}";
    }

    private sealed record Look(Target[] Targets, GameAssetAssociation[] Users);

    private readonly Dictionary<string, Look> _looks;
    private readonly Dictionary<string, Target[]> _packages;
    private readonly ConcurrentDictionary<(string Field, string Class), string> _fields = new();

    private GameLookReferences(Dictionary<string, Look> looks, Dictionary<string, Target[]> packages)
    {
        _looks = looks;
        _packages = packages;
        ReferenceCount = looks.Values.Sum(look => (long)look.Targets.Length * look.Users.Length);
    }

    /// <summary>How many references the looks add, counted once for every database entry that uses a look.</summary>
    public long ReferenceCount { get; }

    public static GameLookReferences Load(string metadataPath, IReadOnlyList<GameAssetAssociation> associations) =>
        GameLookSidecar.TryLoad(GameLookSidecar.GetDefaultPath(metadataPath)) is { } document
            ? Create(document, associations)
            : Empty;

    public static GameLookReferences Create(GameLookDocument document, IReadOnlyList<GameAssetAssociation> associations)
    {
        var users = new Dictionary<string, List<GameAssetAssociation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var association in associations)
        {
            if (!UsesLook(association) || !document.Looks.ContainsKey(association.AssetReference.ToUpperInvariant())) continue;
            if (!users.TryGetValue(association.AssetReference, out var list)) users[association.AssetReference] = list = [];
            list.Add(association);
        }
        if (users.Count == 0) return Empty;

        // Only what a look in use draws is kept, each thing once, with the package names shared.
        var targets = new Target?[document.Targets.Length];
        var targetLooks = new List<string>?[document.Targets.Length];
        var packageNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var looks = new Dictionary<string, Look>(users.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lookUsers) in users)
        {
            var places = document.Looks[name.ToUpperInvariant()];
            var drawn = new List<Target>(places.Length);
            foreach (var place in places)
            {
                if ((uint)place >= (uint)targets.Length) continue;
                if (targets[place] is not { } target)
                {
                    var parts = document.Targets[place].Split('|');
                    if (parts.Length != 3) continue;
                    if (!packageNames.TryGetValue(parts[1], out var package)) packageNames[parts[1]] = package = parts[1];
                    targets[place] = target = new Target(parts[0], package, parts[2]);
                    targetLooks[place] = [];
                }
                drawn.Add(target);
                targetLooks[place]!.Add(name);
            }
            if (drawn.Count > 0) looks[name] = new Look(drawn.ToArray(), lookUsers.ToArray());
        }
        for (var place = 0; place < targets.Length; place++)
            if (targets[place] is { } target) target.Looks = targetLooks[place]!.ToArray();
        return new GameLookReferences(looks,
            targets.Where(target => target is not null).Cast<Target>().GroupBy(target => target.Package, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal));
    }

    /// <summary>The references the look behind a database reference adds for it; none when it names no look.</summary>
    public IEnumerable<GameAssetAssociation> For(GameAssetAssociation association)
    {
        if (!UsesLook(association) || !_looks.TryGetValue(association.AssetReference, out var look)) yield break;
        foreach (var target in look.Targets) yield return Create(association, target);
    }

    /// <summary>What the look behind a database reference draws, without making references of it.</summary>
    public IReadOnlyList<Target> TargetsOf(GameAssetAssociation association) =>
        UsesLook(association) && _looks.TryGetValue(association.AssetReference, out var look) ? look.Targets : [];

    public string FieldName(GameAssetAssociation association, Target target) =>
        _fields.GetOrAdd((association.Field, target.Class), key => $"{key.Field}.Look.{key.Class}");

    /// <summary>
    /// Adds the look references that lead to the asset <paramref name="objectName"/> in <paramref name="packageName"/>
    /// (both normalised): one for every database entry whose look draws it or the family it belongs to.
    /// </summary>
    public void AddMatches(List<GameAssetAssociation> destination, string packageName, string objectName)
    {
        if (!_packages.TryGetValue(packageName, out var targets)) return;
        foreach (var target in targets)
        {
            if (!GameAssetMetadataIndex.IsAssetFamilyMember(objectName, target.Family)) continue;
            foreach (var name in target.Looks)
            foreach (var user in _looks[name].Users)
                destination.Add(Create(user, target));
        }
    }

    private GameAssetAssociation Create(GameAssetAssociation association, Target target) => association with
    {
        Field = FieldName(association, target),
        AssetReference = target.Reference,
        PackageName = target.Package,
        ObjectName = target.Family,
        Confidence = GameAssetAssociationConfidence.Verified,
        Relation = GameAssetAssociationRelation.LookFileReference,
        SourceAssetReference = association.AssetReference,
    };

    private static bool UsesLook(GameAssetAssociation association) =>
        association.Relation.Equals(GameAssetAssociationRelation.DirectDatabaseReference, StringComparison.Ordinal) &&
        association.PackageName.StartsWith("EFDL", StringComparison.OrdinalIgnoreCase);
}
