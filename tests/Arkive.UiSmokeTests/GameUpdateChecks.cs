using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Arkive.App;

/// <summary>The game-update actions sit on the Mod Library page, and every advanced Mods page leads back to it.</summary>
internal static class GameUpdateChecks
{
    public static void Verify(MainWindow window)
    {
        var tabs = Program.Control<TabControl>(window, "MainTabControl");
        var inner = Program.Control<TabControl>(window, "ModifiedUpksInnerTabControl");
        var library = Program.Control<TabItem>(window, "ModLibraryTab");
        var bar = Program.Control<FrameworkElement>(window, "ModsAdvancedBar");
        tabs.SelectedItem = Program.Control<TabItem>(window, "ModifiedUpksTab");
        window.UpdateLayout();
        Program.Check(inner.SelectedItem == library &&
                      Program.Control<Button>(window, "ModLibraryRestoreAllButton").IsVisible &&
                      Program.Control<Button>(window, "ModLibraryCheckUpdateButton").IsVisible &&
                      !Program.Control<Button>(window, "ModLibraryRebaseButton").IsVisible,
            "Restore all game files and Check after game update are not on the Mod Library page, or Rebase mods shows with nothing to rebase.");
        Console.WriteLine("PASS: Restore all game files and Check after game update are on the Mod Library page; Rebase mods waits for mods to rebase");

        typeof(MainWindow).GetMethod("AdvancedNavigationMenuItem_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new MenuItem { Tag = "GameSafety" }, new RoutedEventArgs()]);
        window.UpdateLayout();
        Program.Check(inner.SelectedItem == Program.Control<TabItem>(window, "ClientUpdateSafetyTab") && bar.IsVisible,
            "The advanced Game Update Safety page does not show the way back to the Mod Library.");
        Program.Control<Button>(window, "ModsAdvancedBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Program.Check(inner.SelectedItem == library && !bar.IsVisible, "Back to Mod Library did not return to the mod list.");

        inner.SelectedItem = Program.Control<TabItem>(window, "GuidedPackageProjectsTab");
        tabs.SelectedItem = Program.Control<TabItem>(window, "LibraryTab");
        tabs.SelectedItem = Program.Control<TabItem>(window, "ModifiedUpksTab");
        Program.Check(inner.SelectedItem == library, "Opening the Mods tab again left an advanced page showing.");
        Console.WriteLine("PASS: every advanced Mods page leads back to the Mod Library, and the Mods tab opens on it");
    }
}
