using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Arkive.Core.Materials;
using Arkive.Core.Mods;
using Arkive.Core.Particles;
using Arkive.Core.Textures;

namespace Arkive.App;

/// <summary>
/// Recolours many of a mod's working copies with one choice: pictures, materials and effects together, with a live
/// before-and-after preview of the pictures and material colours.
/// </summary>
public partial class RecolourWindow : Window
{
    private const int PreviewPictures = 8;
    private const int PreviewColours = 48;
    private const int Thumbnail = 132;

    private readonly IReadOnlyList<ArkiveModLocalEdit> _edits;
    private readonly IMaterialTint _tint;
    private readonly Action<string> _log;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly List<(RgbaImage Original, Image After, string Name)> _pictures = [];
    private readonly List<(MaterialVectorEdit Source, Border After)> _colours = [];
    private bool _running;

    public RecolourWindow(IReadOnlyList<ArkiveModLocalEdit> edits, IMaterialTint tint, string effectiveTheme, Action<string> log,
        TextureRecolour? start = null, string? subject = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => AppearanceManager.ApplyWindowChrome(this, effectiveTheme);
        _edits = edits;
        _tint = tint;
        _log = log;
        var pictures = edits.Count(edit => edit.Entry is GuidedTextureModProjectEntry);
        var materials = edits.Count(edit => edit.Entry is MaterialInstanceEditEntry);
        var effects = edits.Count(edit => edit.Entry is ParticleEffectEditEntry);
        var others = edits.Count - pictures - materials - effects;
        var parts = new[] { Count(pictures, "picture"), Count(materials, "material"), Count(effects, "effect") }
            .Where(part => part.Length > 0).ToArray();
        HeaderText.Text = $"Recolour {subject ?? Count(pictures + materials + effects, "asset")}: {string.Join(", ", parts)}" +
                          (others > 0 ? $" ({Count(others, "mesh or movie")} left out: they take no tint)" : string.Empty);
        RecolourButton.Content = $"Recolour {Count(pictures + materials + effects, "asset")}";
        RecolourButton.IsEnabled = pictures + materials + effects > 0;
        MaterialOptions.Visibility = materials > 0 ? Visibility.Visible : Visibility.Collapsed;
        EffectOptions.Visibility = effects > 0 ? Visibility.Visible : Visibility.Collapsed;
        PictureOptions.Visibility = pictures > 0 ? Visibility.Visible : Visibility.Collapsed;
        PicturesHeading.Visibility = PicturesHint.Visibility = pictures > 0 ? Visibility.Visible : Visibility.Collapsed;
        MaterialsHeading.Visibility = MaterialsHint.Visibility = materials > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (start is not null) Choice.Set(start);
        Choice.Changed += SchedulePreview;
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            UpdatePreviews();
        };
        LightenText.Text = $"{LightenSlider.Value:P0}";
        Loaded += async (_, _) =>
        {
            BuildMaterialPreviews();
            await LoadPicturePreviewsAsync();
            UpdatePreviews();
        };
        Closing += (_, e) => e.Cancel = _running;
    }

    /// <summary>What the recolour did, once it ran; null when the window was cancelled.</summary>
    public MaterialTintResult? Result { get; private set; }

    private static string Count(int count, string noun) => count == 0 ? string.Empty : count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";

    private void SchedulePreview()
    {
        if (!IsLoaded) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void Lighten_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LightenText is null) return;
        LightenText.Text = $"{LightenSlider.Value:P0}";
        SchedulePreview();
    }

    private void Option_Changed(object sender, RoutedEventArgs e) => SchedulePreview();

    private async Task LoadPicturePreviewsAsync()
    {
        var textures = _edits.Select(edit => edit.Entry).OfType<GuidedTextureModProjectEntry>().ToArray();
        PicturesHint.Text = textures.Length > PreviewPictures
            ? $"The first {PreviewPictures} of {textures.Length:N0} pictures, small. Game's colours left, new right."
            : "Game's colours left, new right.";
        foreach (var texture in textures.Take(PreviewPictures))
        {
            try
            {
                var path = FromOriginalCheckBox.IsChecked == true
                    ? await ArkiveModTintService.OriginalImagePathAsync(texture)
                    : texture.Manifest.EditableImagePath;
                var image = await Task.Run(async () => Shrink((await TextureEditableImageService.LoadAsync(path)).Image));
                var before = new Image { Source = ToBitmap(image), Width = image.Width, Height = image.Height, Margin = new Thickness(0, 0, 4, 0) };
                var after = new Image { Width = image.Width, Height = image.Height };
                var pair = new StackPanel { Margin = new Thickness(0, 0, 12, 10) };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(before);
                row.Children.Add(after);
                pair.Children.Add(row);
                pair.Children.Add(new TextBlock
                {
                    Text = texture.Manifest.ObjectName, MaxWidth = image.Width * 2 + 4, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"), Margin = new Thickness(0, 2, 0, 0),
                });
                PicturePreviews.Children.Add(pair);
                _pictures.Add((image, after, texture.Manifest.ObjectName));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or
                                                  UnauthorizedAccessException)
            {
                _log($"Recolour preview skipped {texture.Manifest.ObjectName}: {exception.Message}");
            }
        }
    }

    private void BuildMaterialPreviews()
    {
        var colours = _edits.Select(edit => edit.Entry).OfType<MaterialInstanceEditEntry>()
            .SelectMany(material => MaterialTints.Colours(material.Document).Select(colour => (material.Document.ObjectName, colour)))
            .ToArray();
        MaterialsHint.Text = colours.Length > PreviewColours
            ? $"The first {PreviewColours} of {colours.Length:N0} colour settings. Game's colour on top, new below."
            : colours.Length == 0 ? "These materials have no colour settings; only Lighten changes them." : "Game's colour on top, new below.";
        foreach (var (material, colour) in colours.Take(PreviewColours))
        {
            var after = new Border { Width = 34, Height = 16, CornerRadius = new CornerRadius(0, 0, 3, 3) };
            var pair = new StackPanel { ToolTip = $"{material} · {colour.Name}" };
            pair.Children.Add(new Border
            {
                Width = 34, Height = 16, CornerRadius = new CornerRadius(3, 3, 0, 0),
                Background = Swatch(colour.SourceRed, colour.SourceGreen, colour.SourceBlue),
            });
            pair.Children.Add(after);
            MaterialPreviews.Children.Add(new Border
            {
                Child = pair, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 6),
                BorderBrush = (Brush)FindResource("BorderBrush"),
            });
            _colours.Add((colour, after));
        }
    }

    private void UpdatePreviews()
    {
        var recolour = Choice.Recolour;
        if (!recolour.IsValid) return;
        foreach (var (original, after, name) in _pictures)
        {
            var copy = original.DeepCopy();
            TextureNames.ForPicture(recolour, name).Apply(copy);
            after.Source = ToBitmap(copy);
        }
        var (surfaces, glows) = ArkiveModTintService.MaterialRecipes(recolour, (float)LightenSlider.Value);
        foreach (var (source, after) in _colours)
        {
            var single = MaterialTints.IsSurfaceColour(source.Name) ? surfaces : glows;
            var tinted = MaterialTints.Apply(Document(source), single).Vectors[0];
            after.Background = Swatch(tinted.Red, tinted.Green, tinted.Blue);
        }
        StatusText.Text = string.Empty;
    }

    private MaterialInstanceEditDocument Document(MaterialVectorEdit vector) =>
        _edits.Select(edit => edit.Entry).OfType<MaterialInstanceEditEntry>().First().Document with { Vectors = [vector], Scalars = [] };

    private async void RecolourButton_Click(object sender, RoutedEventArgs e)
    {
        var recolour = Choice.Recolour;
        if (!recolour.IsValid || _running) return;
        _running = true;
        RecolourButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        var progress = new Progress<MaterialTintProgress>(step =>
        {
            Progress.Maximum = Math.Max(1, step.Total);
            Progress.Value = step.Completed;
            StatusText.Text = step.ObjectName.Length > 0 ? $"Recolouring {step.ObjectName} ({step.Completed + 1:N0} of {step.Total:N0})…" : "Finishing…";
        });
        try
        {
            var request = new MaterialTintRequest(_edits, recolour)
            {
                LightenSurfaces = (float)LightenSlider.Value,
                IncludeEffectMaterials = EffectMaterialsCheckBox.IsChecked == true,
                FromOriginal = FromOriginalCheckBox.IsChecked == true,
            };
            Result = await Task.Run(() => _tint.TintAsync(request, progress));
            _running = false;
            DialogResult = true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or
                                              UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _log($"Recolour failed: {exception}");
            StatusText.Text = "Could not recolour: " + exception.Message;
            _running = false;
            RecolourButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private static SolidColorBrush Swatch(float red, float green, float blue)
    {
        // Glow colours run above 1; show their hue at full brightness.
        var peak = Math.Max(1, Math.Max(red, Math.Max(green, blue)));
        var brush = new SolidColorBrush(Color.FromScRgb(1, Math.Max(0, red / peak), Math.Max(0, green / peak), Math.Max(0, blue / peak)));
        brush.Freeze();
        return brush;
    }

    private static RgbaImage Shrink(RgbaImage image)
    {
        var scale = Math.Min(1.0, (double)Thumbnail / Math.Max(image.Width, image.Height));
        var width = Math.Max(1, (int)(image.Width * scale));
        var height = Math.Max(1, (int)(image.Height * scale));
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var source = ((int)(y / scale) * image.Width + (int)(x / scale)) * 4;
                Array.Copy(image.Pixels, Math.Min(source, image.Pixels.Length - 4), pixels, (y * width + x) * 4, 4);
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
            bgra[index + 3] = 255;
        }
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, bgra, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
