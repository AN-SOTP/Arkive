using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Arkive.App;

internal static class AppearanceManager
{
    private const string LightTheme = "Light";
    private const string DarkTheme = "Dark";
    private const string BlackTheme = "Black";
    private const string SystemTheme = "System";

    public static string NormalizeTheme(string? theme) => theme switch
    {
        LightTheme => LightTheme,
        DarkTheme => DarkTheme,
        BlackTheme => BlackTheme,
        _ => SystemTheme,
    };

    public static string Apply(string? requestedTheme, bool compactDensity)
    {
        var normalized = NormalizeTheme(requestedTheme);
        var effective = ResolveEffectiveTheme(normalized);
        var resources = Application.Current.Resources;
        var themeDictionary = resources.MergedDictionaries.FirstOrDefault(
            dictionary => dictionary.Source?.OriginalString.Contains(
                "Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) == true ||
                          dictionary.Source?.OriginalString.Contains(
                "Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true ||
                          dictionary.Source?.OriginalString.Contains(
                "Themes/Black.xaml", StringComparison.OrdinalIgnoreCase) == true);
        var source = new Uri($"/Arkive.App;component/Themes/{effective}.xaml", UriKind.Relative);
        if (themeDictionary is null)
        {
            resources.MergedDictionaries.Insert(0, new ResourceDictionary { Source = source });
        }
        else if (!string.Equals(themeDictionary.Source?.OriginalString, source.OriginalString,
                     StringComparison.OrdinalIgnoreCase))
        {
            themeDictionary.Source = source;
        }

        // Native WPF selection templates fall back to Windows system colours when the
        // owning selector loses keyboard focus. Keep active and inactive selections in
        // Arkive's palette so switching tabs never produces a white highlight with white
        // text in dark themes. These aliases must be refreshed whenever the theme changes.
        RefreshSystemSelectionBrushes();

        resources["ControlPadding"] = compactDensity
            ? new Thickness(9, 4, 9, 4)
            : new Thickness(12, 6, 12, 6);
        resources["ControlMargin"] = compactDensity
            ? new Thickness(0, 0, 5, 0)
            : new Thickness(0, 0, 8, 0);
        resources["TabPadding"] = compactDensity
            ? new Thickness(10, 6, 10, 6)
            : new Thickness(13, 8, 13, 8);
        resources["RowHeight"] = compactDensity ? 27d : 31d;
        return effective;
    }

    public static void RefreshSystemSelectionBrushes()
    {
        var resources = Application.Current.Resources;
        resources[SystemColors.HighlightBrushKey] = resources["SelectionBrush"];
        resources[SystemColors.HighlightTextBrushKey] = resources["SelectionTextBrush"];
        resources[SystemColors.InactiveSelectionHighlightBrushKey] =
            resources["InactiveSelectionBrush"];
        resources[SystemColors.InactiveSelectionHighlightTextBrushKey] =
            resources["InactiveSelectionTextBrush"];
    }

    public static string ResolveEffectiveTheme(string? requestedTheme)
    {
        var normalized = NormalizeTheme(requestedTheme);
        return normalized == SystemTheme ? ResolveSystemTheme() : normalized;
    }

    public static void ApplyWindowChrome(Window window, string? effectiveTheme)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var useDark = string.Equals(effectiveTheme, DarkTheme, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(effectiveTheme, BlackTheme, StringComparison.OrdinalIgnoreCase);
        var enabled = useDark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE is 20 on current Windows builds and 19 on
        // older supported builds. Failure simply leaves Windows in charge of the caption.
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
    }

    private static string ResolveSystemTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int light && light == 0 ? DarkTheme : LightTheme;
        }
        catch
        {
            return LightTheme;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
