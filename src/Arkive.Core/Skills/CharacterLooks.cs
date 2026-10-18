using System.Text.RegularExpressions;

namespace Arkive.Core.Skills;

/// <summary>A mesh the game names as "Package.Group.Object": the package alias and the object.</summary>
public sealed record LookMesh(string Package, string Object);

/// <summary>A class weapon: its mesh, the socket prefix it attaches by (WP_DDK_R) and its own AnimSet.</summary>
/// <param name="AnimSetPackage">The package of the weapon's AnimSet (it plays the skill's animations too), or null.</param>
public sealed record LookWeapon(string Item, LookMesh Mesh, string Slot, string? AnimSetPackage, string? AnimSetName)
{
    /// <summary>The body socket the weapon is held by during skills (WP_DDK_R_Battle).</summary>
    public string BattleSocket { get; init; } = Slot + "_Battle";
}

/// <summary>A class's default look: the outfit parts a new character wears and its first weapon in each hand.</summary>
/// <param name="DefaultMesh">The class's skeleton mesh (PC_DK_Shadow_SK), which holds the sockets its skills use.</param>
public sealed record ClassLook(string Name, LookMesh? DefaultMesh, IReadOnlyDictionary<string, LookMesh> Parts, IReadOnlyList<LookWeapon> Weapons)
{
    /// <summary>The look's AnimSets (the body's, then the class's own, which holds its skill animations).</summary>
    public IReadOnlyList<LookMesh> AnimSets { get; init; } = [];
}

/// <summary>
/// The game's look files (data4.lpk, XmlData\LookInfo): a class's default parts (EFDLChar_PC_*) and each item's mesh
/// (EFDLItem_*). They are the client's serialised objects; Arkive reads the object paths in them.
/// </summary>
public static partial class CharacterLooks
{
    private static readonly string[] PartNames = ["Upper", "Lower", "Arm", "Face", "Hair", "Helmet", "Shoulder"];

    public static string? FindArchive(string packagesRoot)
    {
        var path = Path.GetFullPath(Path.Combine(packagesRoot, "..", "..", "data4.lpk"));
        return File.Exists(path) ? path : null;
    }

    /// <summary>The default look of the class whose action file is <paramref name="classFile"/> (DRAGONKNIGHT), or null.</summary>
    public static ClassLook? ForClass(string packagesRoot, string classFile, CancellationToken cancellationToken = default)
    {
        if (FindArchive(packagesRoot) is not { } archive) return null;
        using var stream = File.OpenRead(archive);
        var entries = LostArkDataArchive.ReadEntries(stream, cancellationToken);
        var items = entries.Where(entry => entry.Path.Contains("/LookInfo/Item/", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(entry => Path.GetFileNameWithoutExtension(entry.Path), StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> Strings(LostArkDataEntry entry) => SkillActionReader.Strings(LostArkDataArchive.Read(stream, entry));
        // The class's look names the class (DragonKnight) and the sockets its weapons go in (WP_DDK_R...). Event and
        // test characters (PC_ISBZ_06, PC_Dummy...) reuse the class name, so the plain class looks are tried first.
        var looks = entries.Where(entry => entry.Path.Contains("/LookInfo/Human/EFDLChar_PC_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => SpecialLook().IsMatch(Path.GetFileNameWithoutExtension(entry.Path)) ? 1 : 0);
        foreach (var entry in looks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var strings = Strings(entry);
            if (!strings.Any(value => value.Equals(classFile, StringComparison.OrdinalIgnoreCase))) continue;
            var lookName = Path.GetFileNameWithoutExtension(entry.Path);
            if (ClassCode().Match(lookName) is not { Success: true } code) continue;
            var parts = new Dictionary<string, LookMesh>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in strings.Where(value => value.StartsWith("EFDLItem_", StringComparison.OrdinalIgnoreCase)))
            {
                var part = PartNames.FirstOrDefault(name => item.EndsWith("_" + name, StringComparison.OrdinalIgnoreCase));
                if (part is null || !items.TryGetValue(item, out var itemEntry)) continue;
                if (Mesh(Strings(itemEntry)) is { } mesh) parts.TryAdd(part, mesh);
            }
            var animSets = strings.Select(value => ObjectPath().Match(value))
                .Where(match => match.Success && match.Groups["class"].Value.Equals("AnimSet", StringComparison.OrdinalIgnoreCase))
                .Select(match => new LookMesh(match.Groups["package"].Value, match.Groups["object"].Value)).ToArray();
            return new ClassLook(lookName, Mesh(strings), parts, Weapons(strings, code.Groups["code"].Value, items, Strings)) { AnimSets = animSets };
        }
        return null;
    }

    /// <summary>
    /// The look of a monster or NPC model (LookInfo\Monster\EFDLChar_MN_CDKCN_00.MN_CDKCN_00): its mesh, AnimSet and
    /// extra parts. Parts the look puts in a socket (a boss's sword in wpn_02) are its weapons; parts without one share
    /// the body's skeleton (armour plates, a cape) and are listed in <see cref="ClassLook.Parts"/> as Part1, Part2...
    /// The look whose mesh is <paramref name="meshObject"/> is preferred among the looks named after the package.
    /// </summary>
    public static ClassLook? ForModel(string packagesRoot, string package, string meshObject, CancellationToken cancellationToken = default)
    {
        if (FindArchive(packagesRoot) is not { } archive) return null;
        using var stream = File.OpenRead(archive);
        var prefix = "EFDLChar_" + package + ".";
        var candidates = LostArkDataArchive.ReadEntries(stream, cancellationToken)
            .Where(entry => entry.Path.Contains("/LookInfo/", StringComparison.OrdinalIgnoreCase) &&
                            Path.GetFileNameWithoutExtension(entry.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        ClassLook? first = null;
        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var look = ModelLook(Path.GetFileNameWithoutExtension(entry.Path), SkillActionReader.Strings(LostArkDataArchive.Read(stream, entry)));
            if (look.DefaultMesh?.Object.Equals(meshObject, StringComparison.OrdinalIgnoreCase) == true) return look;
            first ??= look;
        }
        return first;
    }

    private static ClassLook ModelLook(string name, IReadOnlyList<string> strings)
    {
        var animSets = strings.Select(value => ObjectPath().Match(value))
            .Where(match => match.Success && match.Groups["class"].Value.Equals("AnimSet", StringComparison.OrdinalIgnoreCase))
            .Select(match => new LookMesh(match.Groups["package"].Value, match.Groups["object"].Value)).ToArray();
        var parts = new Dictionary<string, LookMesh>(StringComparer.OrdinalIgnoreCase);
        var weapons = new List<LookWeapon>();
        // Each part: CEFData_PartsMesh, its SkeletalMesh, materials and physics, then the socket it goes in (or NONE).
        for (var index = 0; index < strings.Count; index++)
        {
            if (!strings[index].Equals("CEFData_PartsMesh", StringComparison.OrdinalIgnoreCase)) continue;
            LookMesh? mesh = null;
            string? socket = null;
            for (var next = index + 1; next < strings.Count && !strings[next].StartsWith("CEFData_", StringComparison.OrdinalIgnoreCase); next++)
            {
                var value = strings[next];
                if (ObjectPath().Match(value) is { Success: true } path)
                {
                    if (mesh is null && path.Groups["class"].Value.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                        mesh = new LookMesh(path.Groups["package"].Value, path.Groups["object"].Value);
                }
                else if (mesh is not null)
                {
                    // The first plain name after the part's paths is its socket; NONE (or anything else) means it has none.
                    if (SocketName().IsMatch(value) && !value.Equals("NONE", StringComparison.OrdinalIgnoreCase) &&
                        !value.StartsWith("CEF", StringComparison.OrdinalIgnoreCase)) socket = value;
                    break;
                }
            }
            if (mesh is null) continue;
            if (socket is null) parts.TryAdd($"Part{parts.Count + 1}", mesh);
            else weapons.Add(new LookWeapon(name, mesh, socket, null, null) { BattleSocket = socket });
        }
        return new ClassLook(name, Mesh(strings), parts, weapons) { AnimSets = animSets };
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex SocketName();

    // The class's first weapon: an item whose pieces go in the look's weapon sockets, preferring items named after the
    // look's own code (PC_WBK_F -> WP_WBK_F_HR_00), then its class or socket code (WP_WBK_HR_00, WP_DDM for PC_DMN),
    // then other non-avatar items, then avatars. An item lists each piece's mesh followed by its socket (WP_DDM_2,
    // WP_DDM_1); fighters' items name no socket, so their _L/_R meshes go in the look's left and right hands.
    private static IReadOnlyList<LookWeapon> Weapons(IReadOnlyList<string> look, string lookCode,
        IReadOnlyDictionary<string, LostArkDataEntry> items, Func<LostArkDataEntry, IReadOnlyList<string>> strings)
    {
        var battle = look.Where(value => value.EndsWith("_Battle", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var slots = look.Concat(battle.Select(value => value[..^"_Battle".Length]))
            .Where(value => WeaponSlot().IsMatch(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var classCode = GenderSuffix().Replace(lookCode, "");
        // Looks without weapon sockets (the fighters) hold them by the class code's hands.
        if (slots.Count == 0) slots = [$"WP_{classCode}_L", $"WP_{classCode}_R"];
        var slotCodes = slots.Select(slot => WeaponSlot().Match(slot).Groups["code"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int Rank(string name)
        {
            var rest = name["EFDLItem_WP_".Length..];
            if (rest.StartsWith(lookCode + "_", StringComparison.OrdinalIgnoreCase) &&
                (lookCode.Length != classCode.Length || !GenderedItem().IsMatch(rest[(lookCode.Length + 1)..]))) return 0;
            if (rest.StartsWith(classCode + "_", StringComparison.OrdinalIgnoreCase) ||
                slotCodes.Any(code => rest.StartsWith(code + "_", StringComparison.OrdinalIgnoreCase))) return 1;
            return 2;
        }
        var candidates = items.Keys
            .Where(name => name.StartsWith("EFDLItem_WP_", StringComparison.OrdinalIgnoreCase))
            .Select(name => (name, rank: Rank(name)))
            .OrderBy(item => item.rank)
            .ThenBy(item => item.name.Contains("_AV_", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(item => item.name.Contains("_HR_", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.name, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rank) in candidates)
        {
            var values = strings(items[name]);
            var animSet = values.Select(value => ObjectPath().Match(value)).FirstOrDefault(match => match.Success &&
                match.Groups["class"].Value.Equals("AnimSet", StringComparison.OrdinalIgnoreCase));
            var weapons = new List<LookWeapon>();
            foreach (var (mesh, named) in Pieces(values))
            {
                // A piece without a socket goes in the hand its mesh is named for, and only for the class's own items.
                var slot = named ?? (rank < 2 ? HandSlot(mesh, slots) : null);
                if (slot is null || !slots.Contains(slot, StringComparer.OrdinalIgnoreCase)) continue;
                var held = BattleSocket(slot, battle);
                if (weapons.Any(weapon => weapon.BattleSocket.Equals(held, StringComparison.OrdinalIgnoreCase))) continue;
                weapons.Add(new LookWeapon(name, mesh, slot, animSet?.Groups["package"].Value, animSet?.Groups["object"].Value) { BattleSocket = held });
            }
            if (weapons.Count > 0) return weapons;
        }
        return [];
    }

    // An item's pieces: each skeletal mesh and the weapon socket named after it, if any, before the next mesh.
    private static List<(LookMesh Mesh, string? Slot)> Pieces(IReadOnlyList<string> values)
    {
        var pieces = new List<(LookMesh Mesh, string? Slot)>();
        foreach (var value in values)
        {
            if (Mesh([value]) is { } mesh) pieces.Add((mesh, null));
            else if (pieces.Count > 0 && pieces[^1].Slot is null && WeaponSlot().IsMatch(value)) pieces[^1] = (pieces[^1].Mesh, value);
        }
        return pieces;
    }

    // WP_FIF_HR_00_L_SK -> the look's left-hand socket; a single socket takes any piece.
    private static string? HandSlot(LookMesh mesh, IReadOnlyList<string> slots)
    {
        if (slots.Count == 1) return slots[0];
        var hand = HandMesh().Match(mesh.Object) is { Success: true } match ? match.Groups["hand"].Value : "R";
        return slots.FirstOrDefault(slot => slot.EndsWith("_" + hand, StringComparison.OrdinalIgnoreCase));
    }

    // The socket a weapon is held by in battle: WP_DDK_R_Battle, or for a holstered one (WP_GDH_H_R) its hand's
    // (WP_GDH_R_Battle); otherwise its own socket.
    private static string BattleSocket(string slot, IReadOnlyList<string> battle) =>
        new[] { slot + "_Battle", Holster().Replace(slot, "_") + "_Battle" }
            .FirstOrDefault(name => battle.Contains(name, StringComparer.OrdinalIgnoreCase)) ?? slot + "_Battle";

    private static LookMesh? Mesh(IReadOnlyList<string> strings) =>
        strings.Select(value => ObjectPath().Match(value))
            .Where(match => match.Success && match.Groups["class"].Value.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            .Select(match => new LookMesh(match.Groups["package"].Value, match.Groups["object"].Value))
            .FirstOrDefault();

    // SkeletalMesh'PC_DK_00.Mesh.PC_DK_00_Upper2_SK' -> class, package and object (groups between are dropped).
    [GeneratedRegex(@"^(?<class>[A-Za-z0-9_]+)'(?<package>[^.']+)\.(?:[^.']+\.)*(?<object>[^.']+)'$")]
    private static partial Regex ObjectPath();

    // WP_DDK_R, WP_DBL_1: the socket prefix of a class weapon, by the class's weapon code and hand.
    // WP_GDH_H_R (holstered) and WP_GDH_S (slung) too.
    [GeneratedRegex(@"^WP_(?<code>[A-Za-z0-9]+(?:_[FM])?)_(?:H_)?(?:[LRS]|\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex WeaponSlot();

    [GeneratedRegex(@"_H_(?=[LR]$)", RegexOptions.IgnoreCase)]
    private static partial Regex Holster();

    // WP_FIF_HR_00_L_SK, WP_GDH_F_HR_00H_SK: the hand a socketless piece is for.
    [GeneratedRegex(@"[_\d](?<hand>[LR])_SK$", RegexOptions.IgnoreCase)]
    private static partial Regex HandMesh();

    [GeneratedRegex(@"_[FM]$", RegexOptions.IgnoreCase)]
    private static partial Regex GenderSuffix();

    // The rest of an item name after the class code: F_HR_00 is the female class's item.
    [GeneratedRegex(@"^[FM]_", RegexOptions.IgnoreCase)]
    private static partial Regex GenderedItem();

    [GeneratedRegex(@"_ISBZ_|Dummy|Cust|_\d\d$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialLook();

    // EFDLChar_PC_WBK_F.PC_WBK_F -> WBK_F: the look's code its own items are named by.
    [GeneratedRegex(@"^EFDLChar_PC_(?<code>[A-Za-z0-9]+(?:_[FM])?)\.", RegexOptions.IgnoreCase)]
    private static partial Regex ClassCode();
}
