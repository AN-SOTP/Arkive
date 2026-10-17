using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Arkive.Core.Particles;

namespace Arkive.App;

/// <summary>Picks the colour a particle effect is tinted to: a few ready colours, or red, green and blue sliders.</summary>
public partial class ParticleTintWindow : Window
{
    private static readonly (string Name, float Red, float Green, float Blue)[] Swatches =
    [
        ("Red", 1, 0.04f, 0.02f), ("Orange", 1, 0.35f, 0.02f), ("Gold", 1, 0.75f, 0.1f), ("Green", 0.1f, 1, 0.15f),
        ("Cyan", 0.05f, 0.85f, 1), ("Blue", 0.1f, 0.3f, 1), ("Purple", 0.6f, 0.1f, 1), ("Pink", 1, 0.2f, 0.65f),
        ("White", 1, 1, 1),
    ];

    public ParticleTintWindow(string effectName, ParticleEffectTint current)
    {
        InitializeComponent();
        EffectText.Text = effectName;
        foreach (var swatch in Swatches)
        {
            var button = new Button
            {
                ToolTip = swatch.Name, Width = 30, Height = 24, Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(Color.FromScRgb(1, swatch.Red, swatch.Green, swatch.Blue)),
            };
            button.Click += (_, _) => Set(new ParticleEffectTint(swatch.Red, swatch.Green, swatch.Blue));
            SwatchPanel.Children.Add(button);
        }
        Set(current);
    }

    public ParticleEffectTint Tint { get; private set; } = new(1, 1, 1);

    private void Set(ParticleEffectTint tint)
    {
        RedSlider.Value = tint.Red;
        GreenSlider.Value = tint.Green;
        BlueSlider.Value = tint.Blue;
    }

    private void Channel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlueSlider is null || Preview is null) return;
        Tint = new ParticleEffectTint((float)RedSlider.Value, (float)GreenSlider.Value, (float)BlueSlider.Value);
        RedText.Text = $"{RedSlider.Value:0.00}";
        GreenText.Text = $"{GreenSlider.Value:0.00}";
        BlueText.Text = $"{BlueSlider.Value:0.00}";
        Preview.Background = new SolidColorBrush(Color.FromScRgb(1, Tint.Red, Tint.Green, Tint.Blue));
        OkButton.IsEnabled = Tint.IsValid;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
