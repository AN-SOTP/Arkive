using System.Windows;

namespace Arkive.App;

/// <summary>Names a mod pack and picks its mods: the New pack… and Edit pack… window of the Mods list.</summary>
public partial class ModPackWindow : Window
{
    public sealed class Choice(string modId, string name, string note, bool included)
    {
        public string ModId { get; } = modId;

        public string Name { get; } = name;

        /// <summary>Shown under the name, such as the pack the mod is in now.</summary>
        public string Note { get; } = note;

        public bool Included { get; set; } = included;
    }

    private readonly IReadOnlyList<Choice> _choices;

    public ModPackWindow(bool editing, string name, IReadOnlyList<Choice> choices)
    {
        InitializeComponent();
        _choices = choices;
        Title = editing ? "Edit mod pack" : "New mod pack";
        SaveButton.Content = editing ? "Save pack" : "Create pack";
        NameTextBox.Text = name;
        ModsListBox.ItemsSource = choices;
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
            if (choices.FirstOrDefault(choice => choice.Included) is { } first) ModsListBox.ScrollIntoView(first);
        };
    }

    public string PackName => NameTextBox.Text.Trim();

    public IReadOnlyList<string> ModIds => _choices.Where(choice => choice.Included).Select(choice => choice.ModId).ToArray();

    /// <summary>Why the pack cannot be saved yet, or null when it can.</summary>
    public string? Problem => PackName.Length == 0
        ? "Give the pack a name."
        : ModIds.Count < 2
            ? "Tick at least two mods. A single mod is switched with its own On box."
            : null;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (Problem is { } problem)
        {
            MessageText.Text = problem;
            MessageText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    /// <summary>
    /// The name a new pack starts with: the part of a mod's name before " - ", so "Holy Kazeros - golden sky" suggests
    /// "Holy Kazeros" and the other mods named that way.
    /// </summary>
    public static string SuggestedName(string modName)
    {
        var dash = modName.IndexOf(" - ", StringComparison.Ordinal);
        return (dash > 0 ? modName[..dash] : modName).Trim();
    }

    public static bool BelongsWith(string suggestedName, string modName) =>
        modName.Trim().Equals(suggestedName, StringComparison.OrdinalIgnoreCase) ||
        modName.StartsWith(suggestedName + " - ", StringComparison.OrdinalIgnoreCase);
}
