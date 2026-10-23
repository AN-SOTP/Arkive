using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using Arkive.App;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Proportions;

/// <summary>Outfit Swap's class box shows a class's own skins and its body's outfits, and only those.</summary>
internal static class OutfitSwapClassChecks
{
    public static void Verify(MainWindow window, string root)
    {
        var path = Path.Combine(root, "outfit-classes-metadata.json");
        GameAssetAssociation Look(string name, long id, string code) =>
            new(name, name, "PC", id, "LookInfo", $"EFDLChar_{code}.{code}", $"EFDLChar_{code}", code);
        File.WriteAllText(path, JsonSerializer.Serialize(new GameAssetMetadataDocument(2, "English", "fixture", DateTimeOffset.UtcNow,
            [Look("Warrior (Female)", 111, "PC_WR_F"), Look("Slayer", 112, "PC_WBK_F"), Look("Valkyrie", 113, "PC_WHK_F")],
            [
                new GameEntityDefinition("Warrior (Female)", "Warrior_Female", "PC", 111, "Playable class", "base=111", 111, "EFDLChar_PC_WR_F.PC_WR_F"),
                new GameEntityDefinition("Slayer", "Berserker_Female", "PC", 112, "Playable class", "base=111", 111, "EFDLChar_PC_WBK_F.PC_WBK_F"),
                new GameEntityDefinition("Valkyrie", "HolyKnight_Female", "PC", 113, "Playable class", "base=111", 111, "EFDLChar_PC_WHK_F.PC_WHK_F"),
            ])));
        var type = typeof(MainWindow);
        var original = type.GetField("_gameMetadataIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        type.GetField("_gameMetadataIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, GameAssetMetadataIndex.Load(path));
        try
        {
            type.GetMethod("FillOutfitSwapClasses", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var box = Program.Control<ComboBox>(window, "OutfitSwapWearClassBox");
            var grid = Program.Control<DataGrid>(window, "OutfitSwapWearGrid");
            var rowType = type.GetNestedType("OutfitSwapRow", BindingFlags.NonPublic)!;
            var libraryRowType = type.GetNestedType("LibraryAssetRow", BindingFlags.NonPublic)!;
            var libraryRowConstructor = libraryRowType.GetConstructors().OrderByDescending(item => item.GetParameters().Length).First();
            object Row(string stem)
            {
                var piece = stem + "_upper_sk";
                object?[] values = [@"C:\fixture\" + stem + ".upk", stem, stem + ".upk", 1, 1, AssetKind.SkeletalMesh, "SkeletalMesh", piece, 1L, 1L,
                    Array.Empty<GameAssetAssociation>()];
                var args = libraryRowConstructor.GetParameters().Select((parameter, index) =>
                    index < values.Length ? values[index] : parameter.DefaultValue).ToArray();
                var pieces = Array.CreateInstance(libraryRowType, 1);
                pieces.SetValue(libraryRowConstructor.Invoke(args), 0);
                return Activator.CreateInstance(rowType, [stem, BodyProfiles.ForObjectName("pc_wr_f_00_upper_sk")!, pieces])!;
            }
            var rows = Array.CreateInstance(rowType, 4);
            string[] stems = ["pc_wbk_f_hr_00", "pc_whk_f_av_137", "pc_wr_f_av_200", "pc_mar_av_137"];
            for (var index = 0; index < stems.Length; index++) rows.SetValue(Row(stems[index]), index);
            grid.Tag = rows;
            string[] Shown() => grid.Items.Cast<object>().Select(item => (string)rowType.GetProperty("Stem")!.GetValue(item)!).ToArray();
            box.SelectedItem = box.Items.Cast<object>().First(item => item.ToString() == "Slayer");
            Program.Check(Shown().SequenceEqual(["pc_wbk_f_hr_00", "pc_wr_f_av_200"]),
                "The Slayer class showed other outfits than its own skins and its body's: " + string.Join(", ", Shown()));
            box.SelectedItem = box.Items.Cast<object>().First(item => item.ToString() == "Warrior (Female)");
            Program.Check(Shown().SequenceEqual(["pc_wbk_f_hr_00", "pc_whk_f_av_137", "pc_wr_f_av_200"]),
                "The Female Warrior body did not show its classes' skins and its own outfits: " + string.Join(", ", Shown()));
            box.SelectedIndex = 0;
            Program.Check(Shown().Length == 4, "All classes did not show every outfit found.");
            Program.Check(grid.ContextMenu?.Items.OfType<MenuItem>().Any(item => item.Header as string == "Preview in Asset Viewer") == true,
                "Right-clicking an outfit does not offer Preview in Asset Viewer.");
            grid.Tag = null;
            grid.ItemsSource = null;
            Console.WriteLine("PASS: Outfit Swap's class box shows a class's own skins and its body's outfits, and right-click previews one");
        }
        finally
        {
            type.GetField("_gameMetadataIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, original);
        }
    }
}
