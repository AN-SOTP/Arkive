using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Arkive.Core.Interface;
using Arkive.Core.Textures;

namespace Arkive.App;

/// <summary>
/// Arkive's 2D texture viewer for textures and interface pictures. It shows the picture with the parts the game's
/// interface movies name on it, and, for a texture in a mod, recolours the whole picture or one part and saves the
/// result to the mod's working file.
/// </summary>
public partial class TextureViewerWindow : Window
{
    private sealed record PartRow(string Label, InterfaceTexturePart? Part);

    private readonly string? _workingPath;
    private readonly Func<Task<RgbaImage>> _loadOriginal;
    private readonly Action<string> _log;
    private readonly Stack<RgbaImage> _undo = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private RgbaImage _picture;
    private RgbaImage? _original;
    private bool _savedChanges = true;

    /// <param name="workingPath">The mod's working PNG to save to; null shows the picture without editing.</param>
    /// <param name="loadOriginal">The game's own picture, for "Show the game's picture" and resetting.</param>
    /// <param name="loadParts">The named parts of the picture, read in the background.</param>
    public TextureViewerWindow(
        string title,
        RgbaImage picture,
        string? workingPath,
        Func<Task<RgbaImage>> loadOriginal,
        Func<Task<IReadOnlyList<InterfaceTexturePart>>> loadParts,
        string effectiveTheme,
        Action<string> log)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => AppearanceManager.ApplyWindowChrome(this, effectiveTheme);
        _picture = picture;
        _workingPath = workingPath;
        _loadOriginal = loadOriginal;
        _log = log;
        Title = $"Arkive Texture Viewer · {title}";
        HeaderText.Text = $"{title} · {picture.Width:N0}×{picture.Height:N0}";
        HintText.Text = workingPath is null
            ? "This is the game's picture. Add it to a mod, then use Edit colours there to change it."
            : "Pick a part (or the whole picture), choose what to change, a colour and strength, then Apply. Save writes the mod's working file; Save mod and Apply mods put it in the game.";
        EditPanel.Visibility = SaveButton.Visibility = workingPath is null ? Visibility.Collapsed : Visibility.Visible;
        // The shared colour picker's colours, so effects and interface pictures offer the same choices.
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
        PartsList.ItemsSource = new[] { new PartRow("Whole picture", null) };
        PartsList.SelectedIndex = 0;
        Checkerboard.Fill = CheckerBrush();
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            Render();
        };
        Loaded += async (_, _) =>
        {
            Fit();
            Render();
            await LoadPartsAsync(loadParts);
        };
        Closing += (_, e) =>
        {
            if (_savedChanges || _workingPath is null) return;
            if (MessageBox.Show(this, "Close without saving your colour changes?", "Arkive Texture Viewer",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                e.Cancel = true;
        };
        UpdateAdjustmentText();
    }

    /// <summary>Raised after the working file is saved, so the mod editor can refresh.</summary>
    public event Action? Saved;

    private TextureRecolour Recolour => new((float)RedSlider.Value, (float)GreenSlider.Value, (float)BlueSlider.Value,
        (float)StrengthSlider.Value, (float)BrightnessSlider.Value)
    {
        Selection = Selection,
        RangeHue = (float)RangeHueSlider.Value,
        RangeWidth = (float)RangeWidthSlider.Value,
    };

    private TextureColourSelection Selection =>
        Enum.TryParse<TextureColourSelection>((SelectionComboBox.SelectedItem as ComboBoxItem)?.Tag as string, out var selection)
            ? selection : TextureColourSelection.All;

    private bool HasPendingChange => StrengthSlider.Value > 0 || BrightnessSlider.Value != 0;

    private InterfaceRegion? SelectedRegion => (PartsList.SelectedItem as PartRow)?.Part?.Region;

    private async Task LoadPartsAsync(Func<Task<IReadOnlyList<InterfaceTexturePart>>> loadParts)
    {
        try
        {
            var parts = await loadParts();
            // Several places can share a name (a button's normal, over and down looks); number them in picture order.
            var rows = parts.Where(part => part.Region is not null)
                .GroupBy(part => part.Name, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => group.OrderBy(part => part.Region!.Top).ThenBy(part => part.Region!.Left)
                    .Select((part, index) => new PartRow(group.Count() > 1 ? $"{part.Name} · {index + 1}" : part.Name, part)))
                .OrderBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
                .Prepend(new PartRow("Whole picture", null)).ToArray();
            PartsList.ItemsSource = rows;
            PartsList.SelectedIndex = 0;
            var names = parts.Where(part => part.Region is null).SelectMany(part => part.Names).Distinct().Take(3).ToArray();
            PartsHint.Text = parts.Count == 0
                ? "No interface movie names parts of this picture."
                : rows.Length > 1
                    ? $"{rows.Length - 1:N0} named part(s) from {string.Join(", ", parts.Select(part => part.Screen).Distinct().Take(3))}. Hover the list to see all names; the selected part is outlined."
                    : $"The game's interface uses this whole picture as: {string.Join(", ", names)}.";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            PartsHint.Text = "The parts could not be read: " + exception.Message;
            _log($"Asset viewer parts failed: {exception}");
        }
    }

    private void Render()
    {
        RgbaImage shown;
        if (ShowOriginalCheckBox.IsChecked == true && _original is not null) shown = _original;
        else if (_workingPath is not null && HasPendingChange && Recolour.IsValid)
        {
            shown = _picture.DeepCopy();
            Recolour.Apply(shown, SelectedRegion);
        }
        else shown = _picture;
        PictureImage.Source = ToBitmap(shown);
        Checkerboard.Width = shown.Width;
        Checkerboard.Height = shown.Height;
        Overlay.Width = shown.Width;
        Overlay.Height = shown.Height;
        if (SelectedRegion is { } region)
        {
            var (left, top, right, bottom) = TextureRecolour.Bounds(shown, region);
            Canvas.SetLeft(PartHighlight, left);
            Canvas.SetTop(PartHighlight, top);
            PartHighlight.Width = Math.Max(1, right - left);
            PartHighlight.Height = Math.Max(1, bottom - top);
            PartHighlight.Visibility = Visibility.Visible;
        }
        else PartHighlight.Visibility = Visibility.Collapsed;
        ApplyButton.IsEnabled = HasPendingChange && Recolour.IsValid;
        UndoButton.IsEnabled = _undo.Count > 0;
        SaveButton.IsEnabled = !_savedChanges || HasPendingChange;
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
        RangePanel.Visibility = Selection == TextureColourSelection.ColourRange ? Visibility.Visible : Visibility.Collapsed;
        if (!IsLoaded) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    /// <summary>While "Pick the colour" is on, a click on the picture takes the hue under the pointer.</summary>
    private void PictureImage_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PickHueButton.IsChecked != true) return;
        var point = e.GetPosition(PictureImage);
        var (x, y) = ((int)point.X, (int)point.Y);
        if (x < 0 || y < 0 || x >= _picture.Width || y >= _picture.Height) return;
        var offset = (y * _picture.Width + x) * 4;
        var (red, green, blue) = (_picture.Pixels[offset], _picture.Pixels[offset + 1], _picture.Pixels[offset + 2]);
        PickHueButton.IsChecked = false;
        e.Handled = true;
        if (Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) < 12)
        {
            StatusText.Text = "That spot is grey, so it has no hue to pick. Use Light and white areas for greys and whites.";
            return;
        }
        RangeHueSlider.Value = Math.Round(TextureRecolour.Hue(red, green, blue));
        StatusText.Text = $"Picked hue {RangeHueSlider.Value:0}° from pixel {x:N0}, {y:N0}.";
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

    private void PartsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (PartsList.SelectedItem is PartRow { Part: { } part })
            PartsList.ToolTip = string.Join("\n", part.Names);
        Render();
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasPendingChange || !Recolour.IsValid) return;
        CommitPending();
        Render();
    }

    private void CommitPending()
    {
        _undo.Push(_picture.DeepCopy());
        Recolour.Apply(_picture, SelectedRegion);
        var part = (PartsList.SelectedItem as PartRow)?.Label ?? "the picture";
        StatusText.Text = Selection switch
        {
            TextureColourSelection.ColourRange => $"Recoloured the {RangeHueSlider.Value:0}° hues of {part}.",
            TextureColourSelection.LightAreas => $"Recoloured the light areas of {part}.",
            _ => $"Recoloured {part}.",
        };
        _savedChanges = false;
        StrengthSlider.Value = 0;
        BrightnessSlider.Value = 0;
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        _picture = _undo.Pop();
        _savedChanges = false;
        StatusText.Text = "Undid the last change.";
        Render();
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var original = _original ??= await _loadOriginal();
            _undo.Push(_picture);
            _picture = original.DeepCopy();
            _savedChanges = false;
            StatusText.Text = "Back to the game's picture. Save to keep it.";
            Render();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusText.Text = "The game's picture could not be read: " + exception.Message;
        }
    }

    private async void ShowOriginal_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowOriginalCheckBox.IsChecked == true && _original is null)
        {
            try
            {
                _original = await _loadOriginal();
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                StatusText.Text = "The game's picture could not be read: " + exception.Message;
                ShowOriginalCheckBox.IsChecked = false;
                return;
            }
        }
        Render();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workingPath is null) return;
        if (HasPendingChange && Recolour.IsValid) CommitPending();
        SaveButton.IsEnabled = false;
        try
        {
            var temporary = _workingPath + ".saving.png";
            File.Delete(temporary);
            await PngRgbaCodec.WriteAsync(temporary, _picture);
            File.Move(temporary, _workingPath, overwrite: true);
            _savedChanges = true;
            StatusText.Text = "Saved to the working file. Save the mod and apply mods to see it in game.";
            _log($"Asset viewer saved {_workingPath}.");
            Saved?.Invoke();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not save: " + exception.Message;
        }
        Render();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageScale is null) return;
        ImageScale.ScaleX = ImageScale.ScaleY = ZoomSlider.Value;
        PartHighlight.StrokeThickness = 1.5 / ZoomSlider.Value;
        ZoomText.Text = $"{ZoomSlider.Value:P0}";
    }

    private void FitButton_Click(object sender, RoutedEventArgs e) => Fit();

    private void Fit()
    {
        var width = Math.Max(1, ImageScroll.ActualWidth - 24);
        var height = Math.Max(1, ImageScroll.ActualHeight - 24);
        ZoomSlider.Value = Math.Clamp(Math.Min(width / _picture.Width, height / _picture.Height), ZoomSlider.Minimum, ZoomSlider.Maximum);
    }

    private void ZoomToPartButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRegion is not { } region) return;
        var (left, top, right, bottom) = TextureRecolour.Bounds(_picture, region);
        var zoom = Math.Clamp(Math.Min((ImageScroll.ActualWidth - 24) / Math.Max(1, right - left),
            (ImageScroll.ActualHeight - 24) / Math.Max(1, bottom - top)) * 0.6, ZoomSlider.Minimum, ZoomSlider.Maximum);
        ZoomSlider.Value = zoom;
        ImageScroll.UpdateLayout();
        ImageScroll.ScrollToHorizontalOffset(Math.Max(0, (left + right) / 2.0 * zoom - ImageScroll.ViewportWidth / 2));
        ImageScroll.ScrollToVerticalOffset(Math.Max(0, (top + bottom) / 2.0 * zoom - ImageScroll.ViewportHeight / 2));
    }
}
