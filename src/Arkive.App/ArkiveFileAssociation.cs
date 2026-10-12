using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Arkive.App;

internal static class ArkiveFileAssociation
{
    internal const string ProgId = "Arkive.Mod";

    internal static bool CanRegister => Environment.ProcessPath is { } path &&
        Path.GetFileName(path).Equals("Arkive.App.exe", StringComparison.OrdinalIgnoreCase);

    internal static bool RegisterForCurrentUser()
    {
        if (!CanRegister) throw new InvalidOperationException("Launch the published Arkive executable before registering .arkive files.");
        using var effective = Registry.ClassesRoot.OpenSubKey(".arkive");
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        var assigned = Register(Environment.ProcessPath!, classes, effective?.GetValue(null) as string);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        return assigned;
    }

    internal static bool Register(string executablePath, RegistryKey classes, string? effectiveDefault = null)
    {
        var path = Path.GetFullPath(executablePath);
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            path.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new ArgumentException("A valid Arkive executable path is required.", nameof(executablePath));
        using var mod = classes.CreateSubKey(ProgId);
        mod.SetValue(null, "Arkive mod", RegistryValueKind.String);
        using (var icon = mod.CreateSubKey("DefaultIcon"))
            icon.SetValue(null, $"\"{path}\",0", RegistryValueKind.String);
        using (var command = mod.CreateSubKey(@"shell\open\command"))
            command.SetValue(null, $"\"{path}\" \"%1\"", RegistryValueKind.String);
        using var extension = classes.CreateSubKey(".arkive");
        using (var choices = extension.CreateSubKey("OpenWithProgids"))
            choices.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        var current = extension.GetValue(null) as string ?? effectiveDefault;
        var assigned = string.IsNullOrEmpty(current) || current.Equals(ProgId, StringComparison.OrdinalIgnoreCase);
        if (assigned) extension.SetValue(null, ProgId, RegistryValueKind.String);
        return assigned;
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
