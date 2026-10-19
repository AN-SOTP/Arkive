using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Arkive.Core.Assets;
using Arkive.Core.Discovery;
using Arkive.Core.Materials;
using Arkive.Core.Mods;
using Arkive.Core.Textures;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Arkive.App;

public sealed record ZoneViewerProgress(string Stage, int Completed, int Total);

/// <summary>What the Zone Viewer asks the main window to do: read the map, preview parts and add them to a mod.</summary>
/// <param name="ReadUsage">The index of which maps share which parts; built (about half a minute) the first time.</param>
/// <param name="TargetMod">The name of the mod open in Edit Mods, which parts are added to; null when none is.</param>
/// <param name="AddToMod">Adds parts to that mod as working copies, recoloured when a tint is given; returns what happened.</param>
/// <param name="Export">Exports parts into a folder; returns what happened.</param>
/// <param name="BuildScene">Builds (or finds) the map's 3D scene and returns its folder; null hides the 3D Map tab.</param>
public sealed record ZoneViewerHost(
    Func<IProgress<string>, CancellationToken, Task<ZoneMapReport>> ReadMap,
    Func<IProgress<ZoneUsageProgress>, CancellationToken, Task<ZoneUsageIndex?>> ReadUsage,
    Func<PackageIndexMatch, CancellationToken, Task<RgbaImage>> LoadPicture,
    Func<PackageIndexMatch, CancellationToken, Task<MaterialInstanceReconnaissanceReport>> ReadMaterial,
    Action<PackageIndexMatch> Open,
    Func<string?> TargetMod,
    Action ShowEditMods,
    Func<IReadOnlyList<PackageIndexMatch>, TextureRecolour?, IProgress<ZoneViewerProgress>, CancellationToken, Task<string>> AddToMod,
    Func<IReadOnlyList<PackageIndexMatch>, string, IProgress<ZoneViewerProgress>, CancellationToken, Task<string>> Export,
    Action<string> FindInLibrary,
    Action<string> Log,
    Func<IProgress<ZoneSceneProgress>, CancellationToken, Task<string>>? BuildScene = null,
    string? WebViewDataFolder = null);

/// <summary>
/// A whole map in one window: every part its levels use, grouped (terrain, sky, fog, lighting, effects, characters,
/// meshes, materials, textures), each marked as only this map's or shared with other maps, with a live preview of
/// the selected part and a recolour that adds the chosen parts to the mod open in Edit Mods.
/// </summary>
public partial class ZoneViewerWindow : Window
{
    private const int PreviewSize = 512;
    // Adding this many parts at once asks first: each becomes a working copy on disk.
    private const int ConfirmAddCount = 60;

    private readonly ZoneMap _map;
    private readonly ZoneViewerHost _host;
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly DispatcherTimer _selectionTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly List<ZonePartRow> _rows = [];
    private readonly ListCollectionView _view;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _operation;
    private ZoneUsageIndex? _usage;
    private ZonePartRow? _previewRow;
    private RgbaImage? _previewPicture;
    private IReadOnlyList<VectorRow> _vectors = [];
    private bool _busy;

    public ZoneViewerWindow(ZoneMap map, ZoneViewerHost host, string effectiveTheme)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => AppearanceManager.ApplyWindowChrome(this, effectiveTheme);
        _theme = effectiveTheme;
        _map = map;
        _host = host;
        Title = ZoneViewerText.WindowTitle(map.Name);
        HeaderText.Text = map.Name;
        DetailText.Text = ZoneViewerText.MapDetail(map);
        SharingText.Text = ZoneViewerText.ReadingMap;
        _view = new ListCollectionView(_rows) { Filter = item => Shows((ZonePartRow)item) };
        PartsGrid.ItemsSource = _view;
        Checkerboard.Fill = CheckerBrush();
        foreach (var swatch in ColourPickerWindow.Swatches)
        {
            var button = new Button
            {
                ToolTip = swatch.Name, Width = 28, Height = 22, Margin = new Thickness(0, 0, 5, 5),
                Background = new SolidColorBrush(Color.FromScRgb(1, swatch.Red, swatch.Green, swatch.Blue)),
            };
            button.Click += (_, _) =>
            {
                RedSlider.Value = swatch.Red;
                GreenSlider.Value = swatch.Green;
                BlueSlider.Value = swatch.Blue;
                if (StrengthSlider.Value == 0) StrengthSlider.Value = 1;
            };
            SwatchPanel.Children.Add(button);
        }
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            RenderPreview();
        };
        _selectionTimer.Tick += async (_, _) =>
        {
            _selectionTimer.Stop();
            await ShowPreviewAsync();
        };
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _previewCancellation?.Cancel();
            _operation?.Cancel();
            // Ends the 3D map's browser processes, which otherwise hold its data folder.
            MapBrowser.Dispose();
        };
        UpdateAdjustmentText();
        UpdateActions();
        ShowNoSelection();
        if (host.BuildScene is null) MapTab.Visibility = Visibility.Collapsed;
    }

    /// <summary>The map's parts as listed, for tests and the main window.</summary>
    public IReadOnlyList<ZonePartRow> Rows => _rows;

    /// <summary>True once the parts are listed (sharing may still be being read).</summary>
    public bool PartsLoaded { get; private set; }

    /// <summary>True once each part shows whether other maps use it, or reading that failed.</summary>
    public bool SharingLoaded { get; private set; }

    internal TextureRecolour Recolour => new((float)RedSlider.Value, (float)GreenSlider.Value, (float)BlueSlider.Value,
        (float)StrengthSlider.Value, (float)BrightnessSlider.Value)
    {
        Selection = Selection,
        RangeHue = (float)RangeHueSlider.Value,
        RangeWidth = (float)RangeWidthSlider.Value,
    };

    private ColourSelection Selection =>
        Enum.TryParse<ColourSelection>((SelectionComboBox.SelectedItem as ComboBoxItem)?.Tag as string, out var selection)
            ? selection : ColourSelection.All;

    private bool HasPendingChange => StrengthSlider.Value > 0 || BrightnessSlider.Value != 0;

    private IReadOnlyList<ZonePartRow> SelectedRows => PartsGrid.SelectedItems.OfType<ZonePartRow>().ToArray();

    private async Task LoadAsync()
    {
        var token = _closing.Token;
        ShowProgress(ZoneViewerText.ReadingMap, null, null);
        try
        {
            var report = await _host.ReadMap(new Progress<string>(stage => ShowProgress(stage + "…", null, null)), token);
            _rows.AddRange(report.Parts.Select(part => new ZonePartRow(part, _map)));
            _view.Refresh();
            SettingsText.Text = ZoneViewerText.Settings(report.Settings);
            DetailText.Text = ZoneViewerText.MapDetail(_map, report);
            BuildGroups();
            PartsLoaded = true;
            UpdateCount();
            _host.Log($"Zone viewer read {_map.Name}: parts={report.Parts.Count}, unreadLevels={report.UnreadLevelPackages}, " +
                      $"notIndexed={report.NotIndexed}.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or
                                              UnauthorizedAccessException)
        {
            HideProgress();
            SharingText.Text = ZoneViewerText.MapFailed(exception.Message);
            _host.Log($"Zone viewer could not read {_map.Name}: {exception}");
            return;
        }

        ShowProgress(ZoneViewerText.CheckingSharing, null, null);
        try
        {
            _usage = await _host.ReadUsage(new Progress<ZoneUsageProgress>(progress =>
                ShowProgress(ZoneViewerText.ReadingOtherMaps(progress.Completed, progress.Total), progress.Completed, progress.Total)), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _host.Log($"Zone viewer could not read which maps share parts: {exception}");
        }
        HideProgress();
        foreach (var row in _rows)
            row.SetUsers(_usage?.MapsUsing(row.Asset.PackageAlias, row.Asset.ObjectName).Where(other => other.Key != _map.Key).ToArray());
        SharingLoaded = true;
        SharingText.Text = _usage is null ? ZoneViewerText.SharingUnknown : ZoneViewerText.Sharing(_rows);
        BuildGroups();
        _view.Refresh();
        UpdateCount();
        UpdateActions();
        if (_previewRow is not null) ShowPartText(_previewRow);
    }

    private void BuildGroups()
    {
        var selected = (GroupsList.SelectedItem as GroupRow)?.Group;
        var groups = new List<GroupRow> { new(null, ZoneViewerText.AllParts, ZoneViewerText.AllPartsDescription, _rows.Where(row => row.Group != ZonePartGroup.GeneratedMaps).ToArray()) };
        groups.AddRange(Enum.GetValues<ZonePartGroup>()
            .Select(group => new GroupRow(group, ZoneViewerText.GroupTitle(group), ZoneViewerText.GroupDescription(group),
                _rows.Where(row => row.Group == group).ToArray()))
            .Where(group => group.Rows.Count > 0));
        GroupsList.ItemsSource = groups;
        GroupsList.SelectedItem = groups.FirstOrDefault(group => group.Group == selected) ?? groups[0];
    }

    private bool Shows(ZonePartRow row)
    {
        var group = (GroupsList?.SelectedItem as GroupRow)?.Group;
        if (group is null ? row.Group == ZonePartGroup.GeneratedMaps : row.Group != group) return false;
        var show = (ShowComboBox?.SelectedItem as ComboBoxItem)?.Tag as string;
        if (show == "Only" && row.OtherMaps is not 0) return false;
        if (show == "Shared" && row.OtherMaps is null or 0) return false;
        var filter = FilterTextBox?.Text.Trim();
        return string.IsNullOrEmpty(filter) ||
               row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               row.Package.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateCount()
    {
        if (!PartsLoaded) return;
        CountText.Text = ZoneViewerText.Count(_view.Count, SelectedRows.Count);
    }

    private void GroupsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _view.Refresh();
        UpdateCount();
    }

    private void ShowComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _view.Refresh();
        UpdateCount();
    }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _view.Refresh();
        UpdateCount();
    }

    private void PartsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PostMapSelection();
        UpdateCount();
        UpdateActions();
        _selectionTimer.Stop();
        _selectionTimer.Start();
    }

    private void PartsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(PartsGrid, source) is not DataGridRow) return;
        OpenButton_Click(sender, e);
        e.Handled = true;
    }

    private void UpdateActions()
    {
        if (AddButton is null) return;
        var selected = SelectedRows;
        var mod = _host.TargetMod();
        var idle = !_busy && PartsLoaded;
        var recolourable = selected.Count(row => row.CanRecolour);
        TargetModText.Text = mod is null ? ZoneViewerText.NoMod : ZoneViewerText.AddsTo(mod);
        ShowEditModsButton.Visibility = mod is null ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Content = ZoneViewerText.AddLabel(selected.Count);
        TintButton.Content = ZoneViewerText.TintLabel(recolourable);
        AddButton.IsEnabled = idle && mod is not null && selected.Count > 0;
        TintButton.IsEnabled = idle && mod is not null && recolourable > 0 && HasPendingChange && Recolour.IsValid;
        ExportButton.IsEnabled = idle && selected.Count > 0;
        OpenButton.IsEnabled = PartsGrid.SelectedItem is ZonePartRow;
        TintMenuItem.Header = TintButton.Content;
        AddMenuItem.Header = AddButton.Content;
        TintMenuItem.IsEnabled = TintButton.IsEnabled;
        AddMenuItem.IsEnabled = AddButton.IsEnabled;
        ExportMenuItem.IsEnabled = ExportButton.IsEnabled;
        TintButton.ToolTip = !HasPendingChange ? ZoneViewerText.TintNeedsColour : ZoneViewerText.TintTip;
    }

    private void PartsMenu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateActions();
        var row = PartsGrid.SelectedItem as ZonePartRow;
        OpenMenuItem.IsEnabled = FindMenuItem.IsEnabled = CopyMenuItem.IsEnabled = row is not null;
        SelectOnlyMenuItem.IsEnabled = SharingLoaded && _usage is not null;
    }

    // ---- Preview ----

    private void ShowNoSelection()
    {
        PreviewImage.Source = null;
        Checkerboard.Visibility = Visibility.Collapsed;
        PreviewMessage.Text = ZoneViewerText.ChoosePart;
        PartNameText.Text = PartDetailText.Text = PartUsedByText.Text = string.Empty;
        MaterialPanel.Visibility = Visibility.Collapsed;
    }

    private async Task ShowPreviewAsync()
    {
        _previewCancellation?.Cancel();
        if (PartsGrid.SelectedItem is not ZonePartRow row)
        {
            _previewRow = null;
            ShowNoSelection();
            return;
        }
        if (ReferenceEquals(row, _previewRow)) return;
        _previewRow = row;
        _previewPicture = null;
        _vectors = [];
        ShowPartText(row);
        PreviewImage.Source = null;
        Checkerboard.Visibility = Visibility.Collapsed;
        MaterialPanel.Visibility = Visibility.Collapsed;
        var cancellation = _previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        try
        {
            switch (row.Asset.Kind)
            {
                case AssetKind.Texture:
                    PreviewMessage.Text = ZoneViewerText.LoadingPicture;
                    var picture = await _host.LoadPicture(row.Asset, cancellation.Token);
                    if (cancellation.IsCancellationRequested) return;
                    _previewPicture = await Task.Run(() => Shrink(picture, PreviewSize), cancellation.Token);
                    PreviewMessage.Text = string.Empty;
                    break;
                case AssetKind.Material:
                    PreviewMessage.Text = ZoneViewerText.ReadingMaterial;
                    var material = await _host.ReadMaterial(row.Asset, cancellation.Token);
                    if (cancellation.IsCancellationRequested) return;
                    ShowMaterial(material);
                    break;
                default:
                    PreviewMessage.Text = ZoneViewerText.OpenToSee(row.Asset.Kind);
                    break;
            }
            RenderPreview();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or
                                              InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (cancellation.IsCancellationRequested) return;
            PreviewMessage.Text = ZoneViewerText.PreviewFailed(exception.Message);
            _host.Log($"Zone viewer preview of {row.Package}.{row.Name} failed: {exception}");
        }
    }

    private void ShowPartText(ZonePartRow row)
    {
        PartNameText.Text = row.Name;
        PartDetailText.Text = ZoneViewerText.PartDetail(row);
        PartUsedByText.Text = row.UsedByDetail;
        PartUsedByText.Foreground = (Brush)FindResource(row.OtherMaps is > 0 ? "WarningTextBrush" : "TextPrimaryBrush");
    }

    private void ShowMaterial(MaterialInstanceReconnaissanceReport material)
    {
        _vectors = material.VectorParameters.Select(vector => new VectorRow(vector.Name, vector.Red, vector.Green, vector.Blue)).ToArray();
        VectorsList.ItemsSource = _vectors;
        var names = _rows.ToLookup(row => row.Name, StringComparer.OrdinalIgnoreCase);
        MaterialTexturesList.ItemsSource = material.TextureParameters
            .Select(texture => (texture.Name, Object: ZoneParts.ReferencedName(texture.ResolvedObject)))
            .Where(texture => texture.Object is not null)
            .Select(texture => new MaterialTextureRow(texture.Name, texture.Object!, names[texture.Object!].FirstOrDefault()))
            .ToArray();
        MaterialPanel.Visibility = Visibility.Visible;
        PreviewMessage.Text = _vectors.Count == 0 ? ZoneViewerText.MaterialWithoutColours : ZoneViewerText.MaterialColours;
    }

    private void RenderPreview()
    {
        var tint = HasPendingChange && Recolour.IsValid && ShowOriginalCheckBox.IsChecked != true ? Recolour : null;
        if (_previewPicture is { } picture)
        {
            var shown = picture;
            if (tint is not null)
            {
                shown = picture.DeepCopy();
                tint.Apply(shown);
            }
            PreviewImage.Source = ToBitmap(shown);
            Checkerboard.Visibility = Visibility.Visible;
        }
        foreach (var vector in _vectors) vector.Tint(tint);
        PostMapTint(tint);
        UpdateActions();
    }

    private void MaterialTexturesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MaterialTexturesList.SelectedItem is not MaterialTextureRow { Row: { } row }) return;
        Reveal(row);
    }

    /// <summary>Shows a part in the list, switching to All parts when its group or the filter hides it.</summary>
    private void Reveal(ZonePartRow row)
    {
        if (!Shows(row))
        {
            FilterTextBox.Text = string.Empty;
            ShowComboBox.SelectedIndex = 0;
            if (GroupsList.ItemsSource is IEnumerable<GroupRow> groups)
                GroupsList.SelectedItem = groups.FirstOrDefault(group => group.Group == row.Group);
            _view.Refresh();
        }
        PartsGrid.SelectedItem = row;
        PartsGrid.ScrollIntoView(row);
    }

    private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PickHueButton.IsChecked != true || _previewPicture is not { } picture || PreviewImage.Source is not BitmapSource bitmap) return;
        var point = e.GetPosition(PreviewImage);
        var x = (int)(point.X / PreviewImage.ActualWidth * bitmap.PixelWidth);
        var y = (int)(point.Y / PreviewImage.ActualHeight * bitmap.PixelHeight);
        if (x < 0 || y < 0 || x >= picture.Width || y >= picture.Height) return;
        var offset = (y * picture.Width + x) * 4;
        var (red, green, blue) = (picture.Pixels[offset], picture.Pixels[offset + 1], picture.Pixels[offset + 2]);
        PickHueButton.IsChecked = false;
        e.Handled = true;
        if (Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) < 12)
        {
            StatusText.Text = ZoneViewerText.GreyPick;
            return;
        }
        RangeHueSlider.Value = Math.Round(TextureRecolour.Hue(red, green, blue));
        StatusText.Text = ZoneViewerText.Picked(RangeHueSlider.Value);
    }

    // ---- Recolour controls ----

    private void UpdateAdjustmentText()
    {
        if (BrightnessText is null) return;
        RedText.Text = $"{RedSlider.Value:0.00}";
        GreenText.Text = $"{GreenSlider.Value:0.00}";
        BlueText.Text = $"{BlueSlider.Value:0.00}";
        StrengthText.Text = $"{StrengthSlider.Value:P0}";
        RangeHueText.Text = $"{RangeHueSlider.Value:0}°";
        RangeWidthText.Text = $"±{RangeWidthSlider.Value:0}°";
        RangePreview.Background = HueBand(RangeHueSlider.Value, RangeWidthSlider.Value);
        BrightnessText.Text = $"{BrightnessSlider.Value:+0%;-0%;0%}";
        ColourPreview.Background = new SolidColorBrush(Color.FromScRgb(1, (float)RedSlider.Value, (float)GreenSlider.Value,
            (float)BlueSlider.Value));
    }

    private void Adjustment_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText is null || !IsLoaded) return;
        UpdateAdjustmentText();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void SelectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RangePanel is null) return;
        RangePanel.Visibility = Selection == ColourSelection.ColourRange ? Visibility.Visible : Visibility.Collapsed;
        if (!IsLoaded) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void ShowOriginal_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RenderPreview();
    }

    // ---- Actions ----

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (PartsGrid.SelectedItem is ZonePartRow row) _host.Open(row.Asset);
    }

    private void ShowEditModsButton_Click(object sender, RoutedEventArgs e) => _host.ShowEditMods();

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AddButton.IsEnabled) return;
        await AddToModAsync(SelectedRows, null);
    }

    private async void TintButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TintButton.IsEnabled) return;
        await AddToModAsync(SelectedRows.Where(row => row.CanRecolour).ToArray(), Recolour);
    }

    private async Task AddToModAsync(IReadOnlyList<ZonePartRow> rows, TextureRecolour? tint)
    {
        if (rows.Count == 0 || _host.TargetMod() is not { } mod) return;
        var shared = rows.Count(row => row.OtherMaps is > 0);
        if (rows.Count >= ConfirmAddCount || shared > 0)
        {
            var question = ZoneViewerText.ConfirmAdd(rows.Count, shared, mod, rows.Sum(row => row.Asset.SerialSize));
            if (MessageBox.Show(this, question, Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) !=
                MessageBoxResult.Yes) return;
        }
        await RunAsync(ZoneViewerText.Adding, (progress, token) =>
            _host.AddToMod(rows.Select(row => row.Asset).ToArray(), tint, progress, token));
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (!ExportButton.IsEnabled || rows.Count == 0) return;
        var dialog = new OpenFolderDialog { Title = ZoneViewerText.ExportTitle(rows.Count) };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync(ZoneViewerText.Exporting, (progress, token) =>
            _host.Export(rows.Select(row => row.Asset).ToArray(), dialog.FolderName, progress, token));
    }

    private async Task RunAsync(string title, Func<IProgress<ZoneViewerProgress>, CancellationToken, Task<string>> operation)
    {
        _busy = true;
        UpdateActions();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _operation = cancellation;
        StopButton.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;
        ShowProgress(title, null, null);
        try
        {
            StatusText.Text = await operation(new Progress<ZoneViewerProgress>(progress =>
                ShowProgress(ZoneViewerText.Step(progress), progress.Completed, progress.Total)), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText.Text = ZoneViewerText.Stopped;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or
                                              UnauthorizedAccessException or NotSupportedException)
        {
            StatusText.Text = exception.Message;
            _host.Log($"Zone viewer action failed: {exception}");
        }
        finally
        {
            _operation = null;
            _busy = false;
            StopButton.Visibility = Visibility.Collapsed;
            HideProgress();
            UpdateActions();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        StopButton.IsEnabled = false;
    }

    private void SelectOnlyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        PartsGrid.SelectedItems.Clear();
        foreach (var row in _view.OfType<ZonePartRow>().Where(row => row.OtherMaps == 0)) PartsGrid.SelectedItems.Add(row);
    }

    private void SelectAllMenuItem_Click(object sender, RoutedEventArgs e) => PartsGrid.SelectAll();

    private void FindMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (PartsGrid.SelectedItem is ZonePartRow row) _host.FindInLibrary(row.Name);
    }

    private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (PartsGrid.SelectedItem is not ZonePartRow row) return;
        try
        {
            Clipboard.SetText($"{row.Package}.{row.Name}");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowProgress(string text, int? completed, int? total)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = text;
        ProgressBar.IsIndeterminate = total is not > 0;
        ProgressBar.Maximum = Math.Max(1, total ?? 1);
        ProgressBar.Value = completed ?? 0;
    }

    private void HideProgress() => ProgressPanel.Visibility = Visibility.Collapsed;

    // ---- 3D map ----

    private const string ViewerHost = "viewer.arkive.local";
    private const string MapHost = "zonemap.arkive.local";
    private readonly string _theme;
    private bool _mapStarted;
    private bool _mapReady;

    /// <summary>True once the 3D map has drawn its scene.</summary>
    public bool MapLoaded { get; private set; }

    private async void ViewTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection changes of the lists inside the tabs bubble up here too.
        if (!ReferenceEquals(e.OriginalSource, ViewTabs) || !ReferenceEquals(ViewTabs.SelectedItem, MapTab) || _mapStarted ||
            _host.BuildScene is null) return;
        _mapStarted = true;
        await StartMapAsync(_host.BuildScene);
    }

    private async Task StartMapAsync(Func<IProgress<ZoneSceneProgress>, CancellationToken, Task<string>> buildScene)
    {
        var token = _closing.Token;
        MapMessage.Text = ZoneViewerText.BuildingMap;
        ShowProgress(ZoneViewerText.BuildingMap, null, null);
        string directory;
        try
        {
            directory = await buildScene(new Progress<ZoneSceneProgress>(progress =>
                ShowProgress(ZoneViewerText.MapStep(progress), progress.Completed, progress.Total)), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or
                                              UnauthorizedAccessException or NotSupportedException)
        {
            HideProgress();
            MapMessage.Text = ZoneViewerText.MapFailed3D(exception.Message);
            _host.Log($"Zone viewer 3D map failed: {exception}");
            return;
        }
        HideProgress();
        try
        {
            var folder = _host.WebViewDataFolder ?? Path.Combine(Path.GetTempPath(), "Arkive", "webview2");
            Directory.CreateDirectory(folder);
            var environment = await CoreWebView2Environment.CreateAsync(null, folder);
            await MapBrowser.EnsureCoreWebView2Async(environment);
            var core = MapBrowser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(ViewerHost, Path.Combine(AppContext.BaseDirectory, "Viewer"),
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.SetVirtualHostNameToFolderMapping(MapHost, directory, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += (_, args) => OnMapMessage(args.WebMessageAsJson);
            core.NewWindowRequested += (_, args) => args.Handled = true;
            MapBrowser.Visibility = Visibility.Visible;
            MapMessage.Text = string.Empty;
            var page = new FileInfo(Path.Combine(AppContext.BaseDirectory, "Viewer", "map.js"));
            core.Navigate($"https://{ViewerHost}/map.html?v={(page.Exists ? page.LastWriteTimeUtc.Ticks : 0)}");
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException or
                                              IOException or UnauthorizedAccessException)
        {
            MapBrowser.Visibility = Visibility.Collapsed;
            MapMessage.Text = exception is WebView2RuntimeNotFoundException ? ZoneViewerText.NoWebView : ZoneViewerText.MapFailed3D(exception.Message);
            _host.Log($"Zone viewer could not start WebView2: {exception}");
        }
    }

    private void OnMapMessage(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() : null;
        switch (Text("type"))
        {
            case "ready":
                _mapReady = true;
                PostMap(new Dictionary<string, string>
                {
                    ["type"] = "load",
                    ["scene"] = $"https://{MapHost}/{ZoneMapScene.FileName}",
                    ["base"] = $"https://{MapHost}/",
                    ["theme"] = _theme,
                });
                break;
            case "loaded":
                MapLoaded = true;
                StatusText.Text = ZoneViewerText.MapLoaded(root.GetProperty("placements").GetInt32(), root.GetProperty("meshes").GetInt32(),
                    root.GetProperty("terrain").GetInt32());
                PostMapSelection();
                PostMapTint(HasPendingChange && Recolour.IsValid ? Recolour : null);
                break;
            case "pick":
                SelectPicked(Text("material"), Text("mesh"));
                break;
            case "error":
                StatusText.Text = ZoneViewerText.MapFailed3D(Text("message") ?? string.Empty);
                _host.Log($"Zone viewer 3D map page error: {Text("message")}");
                break;
        }
    }

    /// <summary>Selects what was clicked on the map: its material (which takes a recolour) and its mesh.</summary>
    private void SelectPicked(string? material, string? mesh)
    {
        var rows = new[] { material, mesh }.OfType<string>()
            .Select(key => _rows.FirstOrDefault(row => Key(row).Equals(key, StringComparison.OrdinalIgnoreCase)))
            .OfType<ZonePartRow>().ToArray();
        if (rows.Length == 0)
        {
            StatusText.Text = ZoneViewerText.PickedNothing;
            return;
        }
        if (rows.Any(row => !Shows(row)))
        {
            FilterTextBox.Text = string.Empty;
            ShowComboBox.SelectedIndex = 0;
            if (GroupsList.ItemsSource is IEnumerable<GroupRow> groups) GroupsList.SelectedItem = groups.First();
            _view.Refresh();
        }
        PartsGrid.SelectedItems.Clear();
        foreach (var row in rows.Where(Shows)) PartsGrid.SelectedItems.Add(row);
        if (PartsGrid.SelectedItem is ZonePartRow first) PartsGrid.ScrollIntoView(first);
    }

    private static string Key(ZonePartRow row) => (row.Package + "." + row.Name).ToLowerInvariant();

    private void PostMapSelection()
    {
        if (MapLoaded) PostMap(new { type = "select", keys = SelectedRows.Select(Key).ToArray() });
    }

    private void PostMapTint(TextureRecolour? tint)
    {
        if (!MapLoaded) return;
        PostMap(new
        {
            type = "tint",
            keys = SelectedRows.Where(row => row.CanRecolour).Select(Key).ToArray(),
            tint = tint is null ? null : new
            {
                selection = tint.Selection.ToString(), rangeHue = tint.RangeHue, rangeWidth = tint.RangeWidth,
                red = tint.Red, green = tint.Green, blue = tint.Blue, strength = tint.Strength, brightness = tint.Brightness,
            },
        });
    }

    private void PostMap(object message)
    {
        if (_mapReady && MapBrowser.CoreWebView2 is { } core) core.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(message));
    }

    // ---- Drawing ----

    /// <summary>A copy no larger than <paramref name="size"/> on its longer side, averaging the pixels it merges.</summary>
    internal static RgbaImage Shrink(RgbaImage picture, int size)
    {
        var factor = (int)Math.Ceiling(Math.Max(picture.Width, picture.Height) / (double)size);
        if (factor <= 1) return picture;
        var width = Math.Max(1, picture.Width / factor);
        var height = Math.Max(1, picture.Height / factor);
        var pixels = new byte[width * height * 4];
        Span<int> sum = stackalloc int[4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                sum.Clear();
                for (var dy = 0; dy < factor; dy++)
                {
                    var row = (y * factor + dy) * picture.Width;
                    for (var dx = 0; dx < factor; dx++)
                    {
                        var source = (row + x * factor + dx) * 4;
                        for (var channel = 0; channel < 4; channel++) sum[channel] += picture.Pixels[source + channel];
                    }
                }
                var target = (y * width + x) * 4;
                for (var channel = 0; channel < 4; channel++) pixels[target + channel] = (byte)(sum[channel] / (factor * factor));
            }
        return new RgbaImage(width, height, pixels);
    }

    private static BitmapSource ToBitmap(RgbaImage image)
    {
        var bgra = new byte[image.Pixels.Length];
        for (var index = 0; index < bgra.Length; index += 4)
        {
            bgra[index] = image.Pixels[index + 2];
            bgra[index + 1] = image.Pixels[index + 1];
            bgra[index + 2] = image.Pixels[index];
            bgra[index + 3] = image.Pixels[index + 3];
        }
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, bgra, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static DrawingBrush CheckerBrush()
    {
        var light = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        var dark = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute,
        };
    }

    // The hues a colour range changes, drawn as a strip from one end of the range to the other.
    private static LinearGradientBrush HueBand(double hue, double width)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var step = 0; step <= 6; step++)
            brush.GradientStops.Add(new GradientStop(FromHue((hue - width + width * step / 3 + 360) % 360), step / 6.0));
        brush.Freeze();
        return brush;
    }

    private static Color FromHue(double hue)
    {
        var sector = hue / 60;
        var x = (byte)Math.Round(255 * (1 - Math.Abs(sector % 2 - 1)));
        return (int)sector switch
        {
            0 => Color.FromRgb(255, x, 0),
            1 => Color.FromRgb(x, 255, 0),
            2 => Color.FromRgb(0, 255, x),
            3 => Color.FromRgb(0, x, 255),
            4 => Color.FromRgb(x, 0, 255),
            _ => Color.FromRgb(255, 0, x),
        };
    }

    private sealed record GroupRow(ZonePartGroup? Group, string Title, string Description, IReadOnlyList<ZonePartRow> Rows)
    {
        public string Summary => ZoneViewerText.GroupSummary(Rows);
    }

    private sealed record MaterialTextureRow(string Parameter, string ObjectName, ZonePartRow? Row)
    {
        public string Label => Row is null ? ZoneViewerText.TextureNotInMap(Parameter, ObjectName) : $"{Parameter}: {ObjectName}";
    }

    /// <summary>A material colour parameter, with how it looks after the recolour being set up.</summary>
    private sealed class VectorRow(string name, float red, float green, float blue) : INotifyPropertyChanged
    {
        public string Name { get; } = name;

        public string Values => $"{red:0.###}, {green:0.###}, {blue:0.###}";

        public Brush Original { get; } = Swatch(red, green, blue);

        public Brush Tinted { get; private set; } = Swatch(red, green, blue);

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Tint(TextureRecolour? tint)
        {
            var (r, g, b) = tint is null ? (red, green, blue) : WorkingCopyTint.Recolour(tint, red, green, blue);
            Tinted = Swatch(r, g, b);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tinted)));
        }

        // Glowing colours go above 1; the swatch shows their hue at full strength.
        private static Brush Swatch(float red, float green, float blue)
        {
            var peak = Math.Max(1, Math.Max(red, Math.Max(green, blue)));
            var brush = new SolidColorBrush(Color.FromScRgb(1, Math.Max(0, red / peak), Math.Max(0, green / peak), Math.Max(0, blue / peak)));
            brush.Freeze();
            return brush;
        }
    }
}

/// <summary>A part of the map as the Zone Viewer lists it.</summary>
public sealed class ZonePartRow(ZoneMapPart part, ZoneMap map) : INotifyPropertyChanged
{
    private IReadOnlyList<ZoneMap>? _others;

    public ZoneMapPart Part { get; } = part;

    public PackageIndexMatch Asset => Part.Asset;

    public ZonePartGroup Group => Part.Group;

    public string Name => Asset.ObjectName;

    public string Package => Asset.PackageAlias;

    public string Type => ZoneViewerText.TypeOf(Asset);

    /// <summary>The level packages that use it, without the map's shared stem (lv_kur_death_ed_05_sl01 → sl01).</summary>
    public string Levels => string.Join(", ", Part.Levels.Select(level => ZoneViewerText.ShortLevel(map, level)));

    /// <summary>How many other maps use it; null until that has been read.</summary>
    public int? OtherMaps => _others?.Count;

    public IReadOnlyList<ZoneMap> SharedWith => _others ?? [];

    /// <summary>Textures and materials take a recolour; meshes and effects have their own editors.</summary>
    public bool CanRecolour => Asset.Kind is AssetKind.Texture or AssetKind.Material;

    public string UsedBy => _unknown ? ZoneViewerText.SharingNotKnown : ZoneViewerText.UsedBy(OtherMaps);

    public string? UsedByTip => _others is { Count: > 0 } others ? ZoneViewerText.SharedNames(others, 25) : null;

    public string UsedByDetail => _others switch
    {
        null => UsedBy,
        { Count: 0 } => ZoneViewerText.OnlyThisMapDetail,
        var others => ZoneViewerText.SharedDetail(others),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _unknown;

    /// <param name="others">The other maps that use the part; null when that could not be read.</param>
    internal void SetUsers(IReadOnlyList<ZoneMap>? others)
    {
        _others = others;
        _unknown = others is null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsedBy)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsedByTip)));
    }
}
