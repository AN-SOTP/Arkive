using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Arkive.App;
using Microsoft.Win32;
using static Program;

internal static class FileAssociationChecks
{
    internal static void Verify(MainWindow window, string? screenshotDirectory)
    {
        Check(window.Icon is not null, "The Arkive window has no app icon.");
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Arkive.App;component/Assets/Arkive.ico"))!;
        using (resource.Stream)
        {
            var decoder = BitmapDecoder.Create(resource.Stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Check(decoder.Frames.Any(frame => frame.PixelWidth == 256) && decoder.Frames.Any(frame => frame.PixelWidth == 16),
                "The app icon is missing its high-resolution or small Explorer sizes.");
        }
        var arguments = typeof(App).GetMethod("ModFileArguments", BindingFlags.Static | BindingFlags.NonPublic)!;
        var files = (IReadOnlyList<string>)arguments.Invoke(null, [new[] { "one.arkive", "ONE.arkive", "two.ARKIVE", "ignored.exe", "--apply" }])!;
        Check(files.Count == 2 && files.All(Path.IsPathFullyQualified), "Shell opening accepts non-mod arguments or duplicate paths.");
        Console.WriteLine("PASS: app icons include 16–256 pixel sizes and shell opening only routes .arkive files to import");

        var settings = new AppearanceSettingsWindow("Dark", "random", false, false)
        { Owner = window, Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual };
        settings.Show();
        settings.UpdateLayout();
        var registerButton = (Button)settings.FindName("RegisterFilesButton");
        var position = registerButton.TranslatePoint(new Point(), settings);
        Check(settings.Icon is not null && registerButton.IsVisible && position.Y + registerButton.ActualHeight < settings.ActualHeight,
            "The icon registration option is missing or outside the settings window.");
        if (screenshotDirectory is not null) Capture(settings, screenshotDirectory, "settings.png");
        settings.Close();

        var path = @"Software\Arkive.UiSmokeTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(path);
            var type = typeof(App).Assembly.GetType("Arkive.App.ArkiveFileAssociation")!;
            var register = type.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!;
            var exe = Environment.ProcessPath!;
            Check((bool)register.Invoke(null, [exe, root, null])!, "Unclaimed .arkive files were not registered.");
            using (var extension = root.OpenSubKey(".arkive", writable: true))
            {
                Check(extension!.GetValue(null) as string == "Arkive.Mod", "The extension points to the wrong ProgID.");
                extension.SetValue(null, "Existing.Mod.App");
            }
            using (var icon = root.OpenSubKey(@"Arkive.Mod\DefaultIcon"))
                Check(icon!.GetValue(null) as string == $"\"{exe}\",0", "Explorer is not using the executable icon.");
            using (var command = root.OpenSubKey(@"Arkive.Mod\shell\open\command"))
                Check(command!.GetValue(null) as string == $"\"{exe}\" \"%1\"", "The executable or mod argument is not safely quoted.");
            using (var choices = root.OpenSubKey(@".arkive\OpenWithProgids"))
                Check(choices!.GetValueNames().Contains("Arkive.Mod"), "Arkive is unavailable in Open with.");
            Check(!(bool)register.Invoke(null, [exe, root, null])!, "Registration overwrote another app's default.");
            using (var extension = root.OpenSubKey(".arkive", writable: true))
            {
                Check(extension!.GetValue(null) as string == "Existing.Mod.App", "The existing association was lost.");
                extension.DeleteValue(string.Empty);
            }
            Check(!(bool)register.Invoke(null, [exe, root, "Machine.Mod.App"])!, "Registration overwrote an inherited machine association.");
            Console.WriteLine("PASS: per-user registration provides an icon and quoted import command while preserving existing associations");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }
}
