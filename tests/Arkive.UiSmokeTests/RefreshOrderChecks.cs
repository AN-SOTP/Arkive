using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Arkive.App;
using Arkive.Core.Mods;
using static Program;

/// <summary>When two refreshes of a list overlap, the newer request decides what the list shows.</summary>
internal static class RefreshOrderChecks
{
    public static async Task VerifyAsync(MainWindow window, string root)
    {
        await UntilAsync(() => Control<Panel>(window, "BusyOverlay").Visibility != Visibility.Visible);
        var refresh = typeof(MainWindow).GetMethod("RefreshModAuthoringAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var combo = Control<ComboBox>(window, "AuthoringModComboBox");
        var drafts = new ArkiveModAuthoringWorkspace(Path.Combine(root, "staging", "mod-library", "authoring"));

        // The first refresh is reading when the draft appears and a second refresh asks to select it. Before, the second
        // returned at once because one was running, and the first put back its older list with nothing selected.
        var first = (Task)refresh.Invoke(window, [null, null])!;
        var draft = await drafts.CreateAsync(new ArkiveModMetadata("UI late draft", "1.0.0"));
        var second = (Task)refresh.Invoke(window, [draft.DraftId, null])!;
        await Task.WhenAll(first, second);
        string? Id(object? choice) => choice?.GetType().GetProperty("Id")!.GetValue(choice) as string;
        Check(combo.Items.Cast<object>().Any(choice => Id(choice) == draft.DraftId) && Id(combo.SelectedItem) == draft.DraftId,
            "A refresh of Edit mods asked for while another was running was dropped: " +
            $"listed={combo.Items.Cast<object>().Any(choice => Id(choice) == draft.DraftId)}, selected={Id(combo.SelectedItem) ?? "none"}.");
        Check(combo.IsEnabled, "Edit mods' mod list stayed greyed out after two overlapping refreshes.");
        Console.WriteLine("PASS: overlapping refreshes of Edit mods show the newer request's list and selection");

        await drafts.DeleteAsync(draft.DraftId);
        await (Task)refresh.Invoke(window, [null, null])!;
    }
}
