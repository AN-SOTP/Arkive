using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Arkive.Core.Textures;

namespace Arkive.App;

/// <summary>
/// The colour choice for recolouring many assets at once: style (tint or two-tone), which colours change, the colour
/// (and highlight colour), strength and brightness. <see cref="Recolour"/> is the choice; <see cref="Changed"/> fires on
/// every change so previews can follow.
/// </summary>
public partial class RecolourPanel : UserControl
{
    private (float Red, float Green, float Blue) _colour = (1, 0.97f, 0.9f);
    private (float Red, float Green, float Blue) _highlight = (1, 0.78f, 0.36f);

    public RecolourPanel()
    {
        InitializeComponent();
        foreach (var swatch in ColourPickerWindow.Swatches)
        {
            SwatchPanel.Children.Add(Swatch(swatch, () => SetColour(swatch.Red, swatch.Green, swatch.Blue)));
            HighlightSwatchPanel.Children.Add(Swatch(swatch, () => SetHighlight(swatch.Red, swatch.Green, swatch.Blue)));
        }
        Loaded += (_, _) => Refresh();
    }

    /// <summary>Raised whenever any part of the choice changes.</summary>
    public event Action? Changed;

    public TextureRecolourStyle RecolourStyle =>
        Enum.TryParse<TextureRecolourStyle>((StyleComboBox.SelectedItem as ComboBoxItem)?.Tag as string, out var style)
            ? style : TextureRecolourStyle.Tint;

    public ColourSelection Selection =>
        Enum.TryParse<ColourSelection>((SelectionComboBox.SelectedItem as ComboBoxItem)?.Tag as string, out var selection)
            ? selection : ColourSelection.All;

    public TextureRecolour Recolour => new(_colour.Red, _colour.Green, _colour.Blue, (float)StrengthSlider.Value,
        (float)BrightnessSlider.Value)
    {
        Style = RecolourStyle,
        Selection = Selection,
        RangeHue = (float)RangeHueSlider.Value,
        RangeWidth = (float)RangeWidthSlider.Value,
        HighlightRed = _highlight.Red,
        HighlightGreen = _highlight.Green,
        HighlightBlue = _highlight.Blue,
        HighlightAmount = (float)HighlightAmountSlider.Value,
        Lift = (float)LiftSlider.Value,
    };

    /// <summary>Starts from a choice (a helper's suggested colours, say).</summary>
    public void Set(TextureRecolour recolour)
    {
        StyleComboBox.SelectedIndex = recolour.Style == TextureRecolourStyle.TwoTone ? 1 : 0;
        SelectionComboBox.SelectedIndex = (int)recolour.Selection;
        RangeHueSlider.Value = recolour.RangeHue;
        RangeWidthSlider.Value = recolour.RangeWidth;
        StrengthSlider.Value = recolour.Strength;
        BrightnessSlider.Value = recolour.Brightness;
        HighlightAmountSlider.Value = recolour.HighlightAmount;
        LiftSlider.Value = recolour.Lift;
        _highlight = (recolour.HighlightRed, recolour.HighlightGreen, recolour.HighlightBlue);
        SetColour(recolour.Red, recolour.Green, recolour.Blue);
    }

    private Button Swatch((string Name, float Red, float Green, float Blue) swatch, Action pick)
    {
        var button = new Button
        {
            ToolTip = swatch.Name, Width = 26, Height = 22, Margin = new Thickness(0, 0, 5, 4), MinWidth = 0, Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromScRgb(1, swatch.Red, swatch.Green, swatch.Blue)),
        };
        button.Click += (_, _) => pick();
        return button;
    }

    private void SetColour(float red, float green, float blue)
    {
        _colour = (red, green, blue);
        Refresh();
    }

    private void SetHighlight(float red, float green, float blue)
    {
        _highlight = (red, green, blue);
        Refresh();
    }

    private void ColourButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColourPickerWindow(RecolourStyle == TextureRecolourStyle.TwoTone ? "Main colour" : "New colour",
            RecolourStyle == TextureRecolourStyle.TwoTone
                ? "Pictures are repainted in this colour with their shading; materials' surfaces take it."
                : "Each changed colour keeps its brightness and takes this colour's hue.",
            _colour.Red, _colour.Green, _colour.Blue) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() == true) SetColour(picker.Red, picker.Green, picker.Blue);
    }

    private void HighlightButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColourPickerWindow("Highlight colour",
            "Pictures' lightest details, materials' glows and effects take this colour.",
            _highlight.Red, _highlight.Green, _highlight.Blue) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() == true) SetHighlight(picker.Red, picker.Green, picker.Blue);
    }

    private void Choice_Changed(object sender, SelectionChangedEventArgs e) => Refresh();

    private void Value_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Refresh();

    private void Refresh()
    {
        if (LiftText is null || !IsInitialized) return;
        RangePanel.Visibility = Selection == ColourSelection.ColourRange ? Visibility.Visible : Visibility.Collapsed;
        var twoTone = RecolourStyle == TextureRecolourStyle.TwoTone;
        TwoTonePanel.Visibility = twoTone ? Visibility.Visible : Visibility.Collapsed;
        ColourHeading.Text = twoTone ? "Main colour" : "New colour";
        ColourButton.Background = new SolidColorBrush(Color.FromScRgb(1, _colour.Red, _colour.Green, _colour.Blue));
        HighlightButton.Background = new SolidColorBrush(Color.FromScRgb(1, _highlight.Red, _highlight.Green, _highlight.Blue));
        RangeHueText.Text = $"{RangeHueSlider.Value:0}°";
        RangeWidthText.Text = $"±{RangeWidthSlider.Value:0}°";
        RangePreview.Background = TextureViewerWindow.HueBand(RangeHueSlider.Value, RangeWidthSlider.Value);
        StrengthText.Text = $"{StrengthSlider.Value:P0}";
        BrightnessText.Text = $"{BrightnessSlider.Value:+0%;-0%;0%}";
        HighlightAmountText.Text = $"{HighlightAmountSlider.Value:P0}";
        LiftText.Text = $"{LiftSlider.Value:P0}";
        Changed?.Invoke();
    }
}
