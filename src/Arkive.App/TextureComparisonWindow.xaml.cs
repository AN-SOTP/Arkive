using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Arkive.Core.Textures;

namespace Arkive.App;

public partial class TextureComparisonWindow : Window
{
    public TextureComparisonWindow(TextureEditComparisonResult result)
    {
        InitializeComponent();
        SummaryText.Text =
            $"{result.Width:N0}x{result.Height:N0} · " +
            $"{result.ChangedPixels:N0} changed pixel(s) ({result.ChangedPercent:N3}%) · " +
            $"maximum channel difference {result.MaximumChannelDelta}/255. " +
            "The difference view is amplified 4× for visibility; it is diagnostic only.";
        OriginalImage.Source = Load(result.OriginalImagePath);
        EditedImage.Source = Load(result.EditedImagePath);
        DifferenceImage.Source = Load(result.DifferenceImagePath);
    }

    private static BitmapImage Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(path));
        image.EndInit();
        image.Freeze();
        return image;
    }
}
