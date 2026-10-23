using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Serialization;
using Arkive.Core.Skills;

namespace Arkive.Core.Outfits;

/// <summary>
/// One material slot of an item's look file (data4.lpk, XmlData\LookInfo\Item) changed by a mod: the material, and the
/// one used when the item is dyed, that the game puts on that slot of the item's mesh. The originals are the game's
/// own, kept to tell when a game update changes the look; a slot the mod adds after the look's own has none.
/// </summary>
public sealed record OutfitLookEdit(string Entry, int Slot, string Material, string DyeMaterial, string? OriginalMaterial, string? OriginalDyeMaterial)
{
    [JsonIgnore]
    public string Key => $"{Entry.ToLowerInvariant()}#{Slot}";

    [JsonIgnore]
    public bool Added => OriginalMaterial is null;
}

/// <summary>An item look: the mesh it puts on the character and the materials (by mesh slot) it draws it with, normally and dyed.</summary>
public sealed record OutfitLook(string Entry, string Mesh, IReadOnlyList<string> Materials, IReadOnlyList<string> DyeMaterials);

/// <summary>
/// The game's item look files. The game draws an outfit piece with the materials its item's look lists, slot by slot,
/// not with the materials stored in the mesh, so an outfit drawn with other materials needs its look changed too. A
/// look is a run of values; text is a length (with its terminator) and the characters, and each per-slot list is its
/// count then its entries: the materials, a setting per slot (a name and four values), then the dyed materials.
/// </summary>
public static class OutfitLooks
{
    public const string Folder = "/EFGame_Extra/ClientData/XmlData/LookInfo/Item/";

    /// <summary>
    /// The look's mesh and its two material lists, or null when the file is not a single-mesh item look. A look with no
    /// dyed list (an item that cannot be dyed) reads as empty dyed entries.
    /// </summary>
    public static OutfitLook? Read(string entry, byte[] bytes) => Find(bytes) is { } layout
        ? new OutfitLook(entry, layout.Mesh.Text, layout.Materials.Items.Select(item => item.Text).ToArray(),
            layout.Materials.Items.Select((_, slot) => slot < layout.Dyes.Items.Count ? layout.Dyes.Items[slot].Text : "").ToArray())
        : null;

    /// <summary>
    /// The look with <paramref name="edits"/> written into its slots. Slots added after the look's own are appended to
    /// each per-slot list. Refuses when a slot no longer holds the original the edit was made against.
    /// </summary>
    public static byte[] Patch(string entry, byte[] bytes, IEnumerable<OutfitLookEdit> edits)
    {
        var layout = Find(bytes) ?? throw new InvalidDataException($"{entry} is not an item look Arkive can change.");
        var count = layout.Materials.Items.Count;
        var changes = new List<(int Start, int End, byte[] Bytes)>();
        var added = new SortedDictionary<int, OutfitLookEdit>();
        foreach (var edit in edits)
        {
            if (edit.Added)
            {
                if (!added.TryAdd(edit.Slot, edit)) throw new InvalidDataException($"{entry} slot {edit.Slot} is added twice.");
                continue;
            }
            if (edit.Slot < 0 || edit.Slot >= count)
                throw new InvalidDataException($"{entry} has no material slot {edit.Slot}.");
            var material = layout.Materials.Items[edit.Slot];
            var dye = layout.Dyes.Items.Count > 0 ? layout.Dyes.Items[edit.Slot] : null;
            if (!material.Text.Equals(edit.OriginalMaterial, StringComparison.OrdinalIgnoreCase) ||
                !(dye?.Text ?? "").Equals(edit.OriginalDyeMaterial ?? "", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{entry} slot {edit.Slot} is no longer {edit.OriginalMaterial} in this game version.");
            changes.Add((material.Start, material.End, Text(edit.Material)));
            // An item with no dyed list cannot be dyed, so it keeps none.
            if (dye is not null) changes.Add((dye.Start, dye.End, Text(edit.DyeMaterial)));
        }
        if (added.Count > 0)
        {
            if (added.Keys.First() != count || added.Keys.Last() != count + added.Count - 1)
                throw new InvalidDataException($"{entry} has {count} material slots; slots added after them must follow on from slot {count}.");
            var total = Int(count + added.Count);
            changes.Add((layout.Materials.Start, layout.Materials.Start + 4, total));
            changes.Add((layout.Materials.Items[^1].End, layout.Materials.Items[^1].End, [.. added.Values.SelectMany(edit => Text(edit.Material))]));
            changes.Add((layout.SettingsStart, layout.SettingsStart + 4, total));
            // A new slot has no setting of its own: the default every look uses, None and four zeros.
            changes.Add((layout.SettingsEnd, layout.SettingsEnd, [.. Enumerable.Range(0, added.Count).SelectMany(_ => Text("None").Concat(new byte[16]))]));
            if (layout.Dyes.Items.Count > 0)
            {
                changes.Add((layout.Dyes.Start, layout.Dyes.Start + 4, total));
                changes.Add((layout.Dyes.Items[^1].End, layout.Dyes.Items[^1].End, [.. added.Values.SelectMany(edit => Text(edit.DyeMaterial))]));
            }
        }
        using var output = new MemoryStream();
        var at = 0;
        // Insertions go before a change that starts at the same place (the settings end where the dyed list starts).
        foreach (var (start, end, replacement) in changes.OrderBy(change => change.Start).ThenBy(change => change.End - change.Start))
        {
            output.Write(bytes, at, start - at);
            output.Write(replacement);
            at = end;
        }
        output.Write(bytes, at, bytes.Length - at);
        return output.ToArray();
    }

    /// <summary>The material path a look names: MaterialInstanceConstant'Package.Group.Object'.</summary>
    public static string MaterialPath(string className, string path) =>
        $"{(className.Equals("materialinstanceconstant", StringComparison.OrdinalIgnoreCase) ? "MaterialInstanceConstant" : className)}'{path}'";

    /// <summary>The Package.Group.Object a look's material or mesh path names, without its class.</summary>
    public static string InnerPath(string path)
    {
        var inner = path.Trim().TrimEnd('\'');
        var quote = inner.IndexOf('\'');
        return quote >= 0 ? inner[(quote + 1)..] : inner;
    }

    /// <summary>The object a look's material or mesh path names (PC_DL_AV_151_Upper_MI), without its package and group.</summary>
    public static string ObjectName(string path)
    {
        var inner = path.Trim().TrimEnd('\'');
        var quote = inner.IndexOf('\'');
        if (quote >= 0) inner = inner[(quote + 1)..];
        return inner[(inner.LastIndexOf('.') + 1)..];
    }

    /// <summary>
    /// The item looks (by entry) whose mesh is <paramref name="meshObject"/>, from the archive's entries: the item's own
    /// look and its colour variants (EFDLItem_PC_WBK_F_AV_137-1_Lower), which can draw the same mesh.
    /// </summary>
    public static IReadOnlyList<OutfitLook> ForMesh(Stream archive, IReadOnlyList<LostArkDataEntry> entries, string meshObject,
        CancellationToken cancellationToken = default)
    {
        var looks = new List<OutfitLook>();
        // The look files are named after the item's package (EFDLItem_PC_WBK_F_HR_00_Upper for pc_wbk_f_hr_00_upper_sk).
        var stem = PackageStem(meshObject);
        foreach (var entry in entries.Where(entry => entry.Path.StartsWith(Folder, StringComparison.OrdinalIgnoreCase) &&
                                                     entry.Path.Contains(stem, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Read(entry.Path, LostArkDataArchive.Read(archive, entry)) is { } look &&
                ObjectName(look.Mesh).Equals(meshObject, StringComparison.OrdinalIgnoreCase))
                looks.Add(look);
        }
        return looks.OrderBy(look => look.Entry, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// The package a piece's looks are named after: PC_WBK_F_AV_137 for pc_wbk_f_av_137_lower_sk, PC_FLM_HR_02 for the
    /// international client's pc_flm_hr_02_dress_sk_loc_int.
    /// </summary>
    public static string PackageStem(string meshObject)
    {
        var stem = meshObject.EndsWith("_loc_int", StringComparison.OrdinalIgnoreCase) ? meshObject[..^8] : meshObject;
        stem = stem.EndsWith("_sk", StringComparison.OrdinalIgnoreCase) ? stem[..^3] : stem;
        var part = stem.LastIndexOf('_');
        return part > 0 ? stem[..part] : stem;
    }

    /// <summary>
    /// Which colour variant of its item a look is: "-1" for EFDLItem_PC_WBK_F_AV_137-1_Lower, empty for the item's own
    /// look (EFDLItem_PC_WBK_F_AV_137_Lower).
    /// </summary>
    public static string Variant(string entry, string meshObject)
    {
        var name = entry.Split('/')[^1].Split('.')[0];
        var stem = PackageStem(meshObject);
        var at = name.IndexOf(stem, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "";
        var rest = name[(at + stem.Length)..];
        var part = rest.IndexOf('_');
        return part < 0 ? rest : rest[..part];
    }

    private sealed record Token(int Start, int End, string Text);

    private sealed record TokenList(int Start, IReadOnlyList<Token> Items);

    private sealed record Layout(Token Mesh, TokenList Materials, int SettingsStart, int SettingsEnd, TokenList Dyes);

    // The mesh, its material list, the per-slot settings, and right after them the dyed list, all one entry per slot. An item
    // that cannot be dyed has a dyed list of empty entries, or none at all (a count of 0).
    private static Layout? Find(byte[] bytes)
    {
        var meshes = Meshes(bytes).ToArray();
        if (meshes.Length != 1) return null;
        if (Lists(bytes).FirstOrDefault(list => list.Start > meshes[0].Start) is not { } materials) return null;
        var count = materials.Items.Count;
        for (var at = materials.Items[^1].End; at + 4 <= bytes.Length; at++)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at)) != count) continue;
            var next = at + 4;
            for (var slot = 0; slot < count && next > 0; slot++)
                next = TextAt(bytes, next) is { Text.Length: > 0 } name ? name.End + 16 : -1;
            if (next > 0 && ListAt(bytes, next) is { } dyes && (dyes.Items.Count == count || dyes.Items.Count == 0))
                return new Layout(meshes[0], materials, at, next, dyes);
        }
        return null;
    }

    // A list of materials at a place in the look: its count, then that many material paths or empty entries.
    private static TokenList? ListAt(byte[] bytes, int at)
    {
        if (at + 4 > bytes.Length) return null;
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
        if (count is < 0 or > 64) return null;
        var items = new List<Token>(count);
        var next = at + 4;
        while (items.Count < count && TextAt(bytes, next) is { } token &&
               (token.Text.Length == 0 || token.Text.StartsWith("Material", StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(token);
            next = token.End;
        }
        return items.Count == count ? new TokenList(at, items) : null;
    }

    private static byte[] Int(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    // Text is its length with the terminator, then the characters; an empty entry is a length of 0.
    private static byte[] Text(string text)
    {
        var characters = Encoding.ASCII.GetBytes(text);
        return characters.Length == 0 ? Int(0) : [.. Int(characters.Length + 1), .. characters, 0];
    }

    // Text at a place in the look: its length (with the terminator) then printable characters; a length of 0 is empty.
    private static Token? TextAt(byte[] bytes, int at)
    {
        if (at < 0 || at + 4 > bytes.Length) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
        if (length == 0) return new Token(at, at + 4, "");
        if (length is < 2 or > 1024 || at + 4 + length > bytes.Length || bytes[at + 3 + length] != 0 ||
            bytes.AsSpan(at + 4, length - 1).IndexOfAnyExceptInRange((byte)0x20, (byte)0x7e) >= 0)
            return null;
        return new Token(at, at + 4 + length, Encoding.ASCII.GetString(bytes, at + 4, length - 1));
    }

    private static IEnumerable<Token> Meshes(byte[] bytes)
    {
        for (var at = 0; at + 4 < bytes.Length; at++)
            if (TextAt(bytes, at) is { Text.Length: > 0 } token && token.Text.StartsWith("SkeletalMesh'", StringComparison.OrdinalIgnoreCase))
            {
                yield return token;
                at = token.End - 1;
            }
    }

    // Lists of materials: a count, then that many material paths (or empty entries) back to back, at least one a path.
    private static IEnumerable<TokenList> Lists(byte[] bytes)
    {
        for (var at = 0; at + 8 <= bytes.Length; at++)
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
            if (count is < 1 or > 64) continue;
            var items = new List<Token>(count);
            var next = at + 4;
            while (items.Count < count && TextAt(bytes, next) is { } token &&
                   (token.Text.Length == 0 || token.Text.StartsWith("Material", StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(token);
                next = token.End;
            }
            if (items.Count != count || items.All(item => item.Text.Length == 0)) continue;
            yield return new TokenList(at, items);
            at = next - 1;
        }
    }
}
