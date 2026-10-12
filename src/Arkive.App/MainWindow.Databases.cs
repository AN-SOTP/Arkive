using System.IO;
using System.Windows;
using System.Windows.Controls;
using Arkive.Core.Databases;
using Microsoft.Win32;

namespace Arkive.App;

public partial class MainWindow
{
    private CancellationTokenSource? _databaseBuildCancellation;

    private async void BuildGameDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_databaseBuildCancellation is not null || _assetLinkCancellation is not null) return;
        var gameRoot = string.IsNullOrWhiteSpace(_libraryRootPath) ? null : Directory.GetParent(_libraryRootPath)?.Parent?.FullName;
        var dialog = new OpenFileDialog
        {
            Title = "Build the game-name database from Lost Ark data2.lpk",
            Filter = "Lost Ark database archive (data2.lpk)|data2.lpk",
            CheckFileExists = true,
            FileName = "data2.lpk",
            InitialDirectory = gameRoot ?? "",
        };
        if (dialog.ShowDialog(this) != true) return;
        using var cancellation = new CancellationTokenSource();
        _databaseBuildCancellation = cancellation;
        var output = Path.Combine(_projectRoot, "staging", "lpk", "data2-fixed", "arkive-asset-metadata.json");
        var language = (GameDatabaseLanguageComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "English";
        UpdateGameAssetLinkActionState();
        GameDatabaseProgressPanel.Visibility = Visibility.Visible;
        GameDatabaseCancelButton.IsEnabled = true;
        GameDatabaseProgressBar.Value = 0;
        GameDatabaseProgressText.Text = "Reading the game archive...";
        var progress = new Progress<GameDatabaseBuildProgress>(value =>
        {
            if (!ReferenceEquals(_databaseBuildCancellation, cancellation)) return;
            GameDatabaseProgressBar.Maximum = Math.Max(1, value.Total);
            GameDatabaseProgressBar.Value = value.Completed;
            GameDatabaseProgressText.Text = $"{value.Stage}: {value.Completed}/{value.Total} — {value.CurrentFile}";
        });
        try
        {
            Log($"Game database build started: archive={dialog.FileName}, language={language}.");
            var result = await new GameDatabaseBuilder().BuildAsync(dialog.FileName, output, language, progress, cancellation.Token);
            await LoadGameMetadataAsync(result.OutputPath, showErrors: true, loadTrigger: "game database build");
            StatusText.Text = $"Game database ready: {result.AssociationCount:N0} native references from {result.DatabaseCount:N0} tables.";
            Log($"Game database build completed: references={result.AssociationCount}, entities={result.EntityCount}, customizations={result.CustomizationCount}, output={result.OutputPath}.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText.Text = "Game database build cancelled; the previous mapping was preserved.";
            Log("Game database build cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            StatusText.Text = "Game database build failed; the previous mapping was preserved.";
            Log($"Game database build failed: {exception}");
            MessageBox.Show(this, exception.Message, "Arkive game database", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _databaseBuildCancellation = null;
            GameDatabaseProgressPanel.Visibility = Visibility.Collapsed;
            UpdateGameAssetLinkActionState();
        }
    }

    private void GameDatabaseCancelButton_Click(object sender, RoutedEventArgs e)
    {
        GameDatabaseCancelButton.IsEnabled = false;
        _databaseBuildCancellation?.Cancel();
        GameDatabaseProgressText.Text = "Cancelling database build...";
    }
}
