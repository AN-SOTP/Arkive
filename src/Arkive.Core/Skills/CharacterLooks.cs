using System.Text.RegularExpressions;

namespace Arkive.Core.Skills;

/// <summary>A mesh the game names as "Package.Group.Object": the package alias and the object.</summary>
public sealed record LookMesh(string Package, string Object);

/// <summary>A class weapon: its mesh, the socket prefix it attaches by (WP_DDK_R) and its own AnimSet.</summary>
/// <param name="AnimSetPackage">The package of the weapon's AnimSet (it plays the skill's animations too), or null.</param>
public sealed record LookWeapon(string Item, LookMesh Mesh, string Slot, string? AnimSetPackage, string? AnimSetName)
{
    /// <summary>The body socket the weapon is held by during skills (WP_DDK_R_Battle).</summary>
    public string BattleSocket => Slot + "_Battle";
}

/// <summary>A class's default look: the outfit parts a new character wears and its first weapon in each hand.</summary>
/// <param name="DefaultMesh">The class's skeleton mesh (PC_DK_Shadow_SK), which holds the sockets its skills use.</param>
public sealed record ClassLook(string Name, LookMesh? DefaultMesh, IReadOnlyDictionary<string, LookMesh> Parts, IReadOnlyList<LookWeapon> Weapons);

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
            var slots = strings.Where(value => WeaponSlot().IsMatch(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (slots.Length == 0) continue;
            var parts = new Dictionary<string, LookMesh>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in strings.Where(value => value.StartsWith("EFDLItem_", StringComparison.OrdinalIgnoreCase)))
            {
                var part = PartNames.FirstOrDefault(name => item.EndsWith("_" + name, StringComparison.OrdinalIgnoreCase));
                if (part is null || !items.TryGetValue(item, out var itemEntry)) continue;
                if (Mesh(Strings(itemEntry)) is { } mesh) parts.TryAdd(part, mesh);
            }
            return new ClassLook(Path.GetFileNameWithoutExtension(entry.Path), Mesh(strings), parts,
                slots.Select(slot => Weapon(slot, items, Strings)).OfType<LookWeapon>().ToArray());
        }
        return null;
    }

    // The class's first weapon for a socket: the lowest-numbered weapon item of its code that is not an avatar
    // (WP_DDK_HR_00) and goes in that socket.
    private static LookWeapon? Weapon(string slot, IReadOnlyDictionary<string, LostArkDataEntry> items,
        Func<LostArkDataEntry, IReadOnlyList<string>> strings)
    {
        var code = WeaponSlot().Match(slot).Groups["code"].Value;
        var candidates = items.Keys
            .Where(name => name.StartsWith($"EFDLItem_WP_{code}_", StringComparison.OrdinalIgnoreCase) &&
                           !name.Contains("_AV_", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var name in candidates)
        {
            var values = strings(items[name]);
            if (Mesh(values) is not { } mesh) continue;
            var itemSlot = values.FirstOrDefault(value => WeaponSlot().IsMatch(value)) ?? slot;
            if (!itemSlot.Equals(slot, StringComparison.OrdinalIgnoreCase)) continue;
            var animSet = values.Select(value => ObjectPath().Match(value)).FirstOrDefault(match => match.Success &&
                match.Groups["class"].Value.Equals("AnimSet", StringComparison.OrdinalIgnoreCase));
            return new LookWeapon(name, mesh, itemSlot, animSet?.Groups["package"].Value, animSet?.Groups["object"].Value);
        }
        return null;
    }

    private static LookMesh? Mesh(IReadOnlyList<string> strings) =>
        strings.Select(value => ObjectPath().Match(value))
            .Where(match => match.Success && match.Groups["class"].Value.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            .Select(match => new LookMesh(match.Groups["package"].Value, match.Groups["object"].Value))
            .FirstOrDefault();

    // SkeletalMesh'PC_DK_00.Mesh.PC_DK_00_Upper2_SK' -> class, package and object (groups between are dropped).
    [GeneratedRegex(@"^(?<class>[A-Za-z0-9_]+)'(?<package>[^.']+)\.(?:[^.']+\.)*(?<object>[^.']+)'$")]
    private static partial Regex ObjectPath();

    // WP_DDK_R, WP_DBL_1: the socket prefix of a class weapon, by the class's weapon code and hand.
    [GeneratedRegex(@"^WP_(?<code>[A-Za-z0-9]+(?:_[FM])?)_(?:[LR]|\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex WeaponSlot();

    [GeneratedRegex(@"_ISBZ_|Dummy|Cust|_\d\d$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialLook();
}
