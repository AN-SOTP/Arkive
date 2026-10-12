using System.Windows;

namespace Arkive.App;

public partial class MainWindow : Window
{
    private void ApplyBannerArtwork(BannerArtworkDefinition artwork)
    {
        BannerArtworkManager.ApplyPalette(artwork, _effectiveAppearanceTheme);
        var image = BannerArtworkManager.LoadImage(artwork);
        BannerBackdropArtworkBrush.ImageSource = image;
        BannerArtworkBrush.ImageSource = image;
        BannerArtworkBrush.Viewbox = artwork.Viewbox;
        BannerArtworkSurface.ToolTip = $"{artwork.DisplayName} — official Lost Ark promotional artwork";
    }
}
