using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Arkive.App;

/// <summary>
/// Arkive's shared colour picker: a few ready colours, or red, green and blue sliders (linear 0–1, as Arkive's tints
/// use them). Particle effects, UI parts and textures all choose their tint here.
/// </summary>
public partial class ColourPickerWindow : Window
{
    /// <summary>The ready colours offered everywhere a tint is chosen, in linear red, green, blue.</summary>
    public static readonly IReadOnlyList<(string Name, float Red, float Green, float Blue)> Swatches =
    [
        ("Red", 1, 0.04f, 0.02f), ("Orange", 1, 0.35f, 0.02f), ("Gold", 1, 0.75f, 0.1f), ("Green", 0.1f, 1, 0.15f),
        ("Cyan", 0.05f, 0.85f, 1), ("Blue", 0.1f, 0.3f, 1), ("Purple", 0.6f, 0.1f, 1), ("Pink", 1, 0.2f, 0.65f),
        ("White", 1, 1, 1),
    ];

    /// <param name="subject">What is being coloured, shown at the top.</param>
    /// <param name="explanation">How the colour is used, shown under the subject.</param>
    public ColourPickerWindow(string subject, string explanation, float red, float green, float blue)
    {
        InitializeComponent();
        SubjectText.Text = subject;
        ExplanationText.Text = explanation;
        foreach (var swatch in Swatches)
        {
            var button = new Button
            {
                ToolTip = swatch.Name, Width = 30, Height = 24, Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(Color.FromScRgb(1, swatch.Red, swatch.Green, swatch.Blue)),
            };
            button.Click += (_, _) => Set(swatch.Red, swatch.Green, swatch.Blue);
            SwatchPanel.Children.Add(button);
        }
        Set(red, green, blue);
    }

    public float Red => (float)RedSlider.Value;

    public float Green => (float)GreenSlider.Value;

    public float Blue => (float)BlueSlider.Value;

    private void Set(float red, float green, float blue)
    {
        RedSlider.Value = red;
        GreenSlider.Value = green;
        BlueSlider.Value = blue;
    }

    private void Channel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlueSlider is null || Preview is null) return;
        RedText.Text = $"{RedSlider.Value:0.00}";
        GreenText.Text = $"{GreenSlider.Value:0.00}";
        BlueText.Text = $"{BlueSlider.Value:0.00}";
        Preview.Background = new SolidColorBrush(Color.FromScRgb(1, Red, Green, Blue));
        OkButton.IsEnabled = Math.Max(Red, Math.Max(Green, Blue)) > 0;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
