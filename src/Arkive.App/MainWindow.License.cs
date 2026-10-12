using System.Diagnostics;
using System.IO;
using System.Windows;
using Arkive.Core.External;
using Arkive.Core.Updates;

namespace Arkive.App;

public partial class MainWindow
{
    private void AboutLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        var root = File.Exists(Path.Combine(AppContext.BaseDirectory, "LICENSE.txt"))
            ? AppContext.BaseDirectory : UmodelLocator.FindProjectRoot();
        var licence = root is null ? null : Path.Combine(root, "LICENSE.txt");
        var message = $"Arkive {ArkiveReleasePolicy.CurrentVersion.ToString(3)}\nCopyright (c) 2026 Arkive contributors\n\n" +
            "Arkive's original code is free software under AGPL-3.0-only. You may redistribute and modify it under that licence. " +
            "It is provided without warranty, including merchantability or fitness for a particular purpose.\n\n" +
            "Third-party components and Lost Ark artwork retain their own rights and notices.\n" +
            "Source and build instructions: https://github.com/AN-SOTP/Arkive\n\nView the full licence?";
        if (MessageBox.Show(this, message, "About Arkive / licence", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        try
        {
            Process.Start(new ProcessStartInfo(licence is not null && File.Exists(licence)
                ? licence : "https://www.gnu.org/licenses/agpl-3.0.html")
            { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            Log($"Could not open the licence: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Arkive licence", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
