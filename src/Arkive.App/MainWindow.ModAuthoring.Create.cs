using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arkive.Core.Mods;

namespace Arkive.App;

public partial class MainWindow : Window
{
    private void NewModButton_Click(object sender, RoutedEventArgs e)
    {
        if (_authoringBusy || _modLibraryBusy || _guidedTextureModInProgress || _modSetBusy || _workshopBusy) return;
        CreateModTab.IsSelected = true;
        CreateModNameTextBox.Focus();
    }

    private async void CreateModButton_Click(object sender, RoutedEventArgs e) => await CreateModAsync();

    private void CreateModNameTextBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (CreateModStatusText is not null && !string.IsNullOrWhiteSpace(CreateModNameTextBox.Text))
            CreateModStatusText.Text = "After creation, add individual assets or a complete mesh asset kit.";
    }

    private async void CreateModTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await CreateModAsync();
    }

    private async Task CreateModAsync()
    {
        if (string.IsNullOrWhiteSpace(CreateModNameTextBox.Text))
        {
            CreateModStatusText.Text = "Enter a name for your mod.";
            CreateModNameTextBox.Focus();
            return;
        }

        await RunAuthoringOperationAsync(async () =>
        {
            await PersistAuthoringDraftAsync();
            var metadata = new ArkiveModMetadata(CreateModNameTextBox.Text.Trim(),
                string.IsNullOrWhiteSpace(CreateModVersionTextBox.Text) ? "1.0.0" : CreateModVersionTextBox.Text.Trim(),
                CreateModAuthorTextBox.Text.Trim(), CreateModDescriptionTextBox.Text.Trim());
            _authoringDraft = await _modAuthoringWorkspace.CreateAsync(metadata);
            LoadAuthoringMetadata();
            WorkshopTab.IsSelected = true;
            AuthoringSearchExpander.IsExpanded = true;
            AuthoringStatusText.Text = "Draft created. Search for assets to add, then edit their working files.";
            CreateModNameTextBox.Clear();
            CreateModVersionTextBox.Text = "1.0.0";
            CreateModDescriptionTextBox.Clear();
            CreateModStatusText.Text = "After creation, add individual assets or a complete mesh asset kit.";
        });
        if (WorkshopTab.IsSelected) WorkshopSearchTextBox.Focus();
    }
}
