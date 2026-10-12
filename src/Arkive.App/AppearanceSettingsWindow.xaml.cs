using System.Windows;
using System.Windows.Controls;

namespace Arkive.App;

public partial class AppearanceSettingsWindow : Window
{
    private readonly Func<Task>? _checkUpdates;

    public AppearanceSettingsWindow(
        string theme,
        string bannerArtwork,
        bool compactDensity,
        bool showValidationPane,
        bool checkForUpdatesOnStartup = true,
        Func<Task>? checkUpdates = null)
    {
        InitializeComponent();
        _checkUpdates = checkUpdates;
        CheckUpdatesCheckBox.IsChecked = checkForUpdatesOnStartup;
        UpdateVersionText.Text = $"Arkive {Arkive.Core.Updates.ArkiveReleasePolicy.CurrentVersion.ToString(3)} — application updates";
        UpdateStatusText.Text = "Downloads and installation happen only after you click Install update.";
        RegisterFilesButton.IsEnabled = ArkiveFileAssociation.CanRegister;
        if (!RegisterFilesButton.IsEnabled)
            FileAssociationStatusText.Text = "Launch the published Arkive.App.exe to register Windows file icons.";
        SelectTag(ThemeComboBox, AppearanceManager.NormalizeTheme(theme));
        BannerArtworkComboBox.Items.Add(new ComboBoxItem
        {
            Content = "Random on each launch",
            Tag = BannerArtworkManager.RandomPreference,
        });
        foreach (var artwork in BannerArtworkManager.Artwork)
        {
            BannerArtworkComboBox.Items.Add(new ComboBoxItem
            {
                Content = $"{artwork.DisplayName} — {TitleCase(artwork.Caption)}",
                Tag = artwork.Id,
            });
        }
        SelectTag(
            BannerArtworkComboBox,
            BannerArtworkManager.NormalizePreference(bannerArtwork));
        SelectTag(DensityComboBox, compactDensity ? "Compact" : "Comfortable");
        ShowValidationCheckBox.IsChecked = showValidationPane;
        SourceInitialized += (_, _) => AppearanceManager.ApplyWindowChrome(
            this,
            AppearanceManager.ResolveEffectiveTheme(theme));
    }

    public string SelectedTheme { get; private set; } = "System";

    public string SelectedBannerArtwork { get; private set; } =
        BannerArtworkManager.RandomPreference;

    public bool CompactDensity { get; private set; }

    public bool ShowValidationPane { get; private set; }

    public bool CheckForUpdatesOnStartup { get; private set; }

    private void SaveUpdateToken_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateCredentialStore.Save(UpdateTokenBox.Password);
            UpdateTokenBox.Clear();
            UpdateStatusText.Text = "Access token saved in Windows Credential Manager.";
        }
        catch (Exception exception) when (exception is ArgumentException or System.ComponentModel.Win32Exception)
        { UpdateStatusText.Text = exception is ArgumentException ? exception.Message : "Windows could not save the credential."; }
    }

    private void ForgetUpdateToken_Click(object sender, RoutedEventArgs e)
    {
        try { UpdateCredentialStore.Delete(); UpdateTokenBox.Clear(); UpdateStatusText.Text = "Access token removed."; }
        catch (System.ComponentModel.Win32Exception) { UpdateStatusText.Text = "Windows could not remove the credential."; }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_checkUpdates is null) return;
        var button = (Button)sender;
        button.IsEnabled = false;
        try { await _checkUpdates(); UpdateStatusText.Text = "Update check finished; any available update appears in the main window."; }
        finally { button.IsEnabled = true; }
    }

    private void RegisterFilesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var assigned = ArkiveFileAssociation.RegisterForCurrentUser();
            FileAssociationStatusText.Text = assigned
                ? "Registered for this user. Windows may require Open with → Arkive if another default app was chosen."
                : "Arkive is available in Open with. Your existing default app was kept; choose Arkive to show its icon.";
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or
                                           System.Security.SecurityException or InvalidOperationException or ArgumentException)
        {
            FileAssociationStatusText.Text = $"Could not register .arkive files: {exception.Message}";
            SessionLog.WriteUnhandled("file association registration", exception);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedTheme = (ThemeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";
        SelectedBannerArtwork =
            (BannerArtworkComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
            BannerArtworkManager.RandomPreference;
        CompactDensity = string.Equals(
            (DensityComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            "Compact",
            StringComparison.OrdinalIgnoreCase);
        ShowValidationPane = ShowValidationCheckBox.IsChecked == true;
        CheckForUpdatesOnStartup = CheckUpdatesCheckBox.IsChecked == true;
        DialogResult = true;
    }

    private static void SelectTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private static string TitleCase(string value) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            value.ToLowerInvariant());
}
