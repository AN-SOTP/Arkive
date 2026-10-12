using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Arkive.App;

public partial class MainWindow : Window
{
    private double _authoringSearchPaneRatio = 0.65;
    private double _modDetailsPaneRatio = 0.5;

    private static double WorkflowPaneRatio(double value, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.05, 0.95) : fallback;

    private static double ReadWorkflowPaneRatio(RowDefinition upper, RowDefinition lower, double fallback)
    {
        var total = upper.ActualHeight + lower.ActualHeight;
        return total > 0 ? WorkflowPaneRatio(lower.ActualHeight / total, fallback) : fallback;
    }

    private static void SetWorkflowPaneRatio(RowDefinition upper, RowDefinition lower, double ratio)
    {
        upper.Height = new GridLength(1 - ratio, GridUnitType.Star);
        lower.Height = new GridLength(ratio, GridUnitType.Star);
    }

    private void RestoreWorkflowLayout(ArkiveSettings settings)
    {
        _authoringSearchPaneRatio = WorkflowPaneRatio(settings.AuthoringSearchPaneRatio, 0.65);
        _modDetailsPaneRatio = WorkflowPaneRatio(settings.ModDetailsPaneRatio, 0.5);
        SetWorkflowPaneRatio(ModLibraryModsRow, ModLibraryDetailsRow, _modDetailsPaneRatio);
        LibrarySetupExpander.IsExpanded = _libraryRootPath is null || settings.LibrarySetupExpanded;
        AuthoringMetadataExpander.IsExpanded = settings.AuthoringDetailsExpanded;
        AuthoringSearchExpander.IsExpanded = settings.AuthoringSearchExpanded;
    }

    private void CaptureWorkflowLayout()
    {
        if (AuthoringSearchExpander.IsExpanded)
            _authoringSearchPaneRatio = ReadWorkflowPaneRatio(AuthoringAssetsRow, AuthoringSearchRow, _authoringSearchPaneRatio);
        _modDetailsPaneRatio = ReadWorkflowPaneRatio(ModLibraryModsRow, ModLibraryDetailsRow, _modDetailsPaneRatio);
    }

    private void WorkflowPaneSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => CaptureWorkflowLayout();

    private void WorkflowPaneGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender == AuthoringPaneGrid && AuthoringSearchExpander.IsExpanded)
            ScaleWorkflowPaneMinimums(AuthoringPaneGrid, AuthoringAssetsRow, AuthoringSearchRow, 125, 190);
        else if (sender == ModLibraryPaneGrid)
            ScaleWorkflowPaneMinimums(ModLibraryPaneGrid, ModLibraryModsRow, ModLibraryDetailsRow, 120, 140);
    }

    private static void ScaleWorkflowPaneMinimums(Grid grid, RowDefinition upper, RowDefinition lower,
        double upperMinimum, double lowerMinimum)
    {
        var scale = Math.Clamp((grid.ActualHeight - 8) / (upperMinimum + lowerMinimum), 0, 1);
        upper.MinHeight = upperMinimum * scale;
        lower.MinHeight = lowerMinimum * scale;
    }

    private void AuthoringSearchExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, AuthoringSearchExpander) || AuthoringPaneSplitter is null) return;
        AuthoringSearchRow.MinHeight = 190;
        AuthoringSplitterRow.Height = new GridLength(8);
        AuthoringPaneSplitter.Visibility = Visibility.Visible;
        SetWorkflowPaneRatio(AuthoringAssetsRow, AuthoringSearchRow, _authoringSearchPaneRatio);
        ScaleWorkflowPaneMinimums(AuthoringPaneGrid, AuthoringAssetsRow, AuthoringSearchRow, 125, 190);
    }

    private void AuthoringSearchExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, AuthoringSearchExpander) || AuthoringPaneSplitter is null) return;
        if (AuthoringSearchRow.Height.IsStar)
            _authoringSearchPaneRatio = ReadWorkflowPaneRatio(AuthoringAssetsRow, AuthoringSearchRow, _authoringSearchPaneRatio);
        AuthoringSearchRow.MinHeight = 0;
        AuthoringSearchRow.Height = GridLength.Auto;
        AuthoringAssetsRow.Height = new GridLength(1, GridUnitType.Star);
        AuthoringSplitterRow.Height = new GridLength(0);
        AuthoringPaneSplitter.Visibility = Visibility.Collapsed;
    }
}
