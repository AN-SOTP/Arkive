using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Arkive.Core.External;
using Arkive.Core.Updates;

return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    string? executable = null;
    var parentExited = false;
    try
    {
        if (arguments.Length != 5 || !int.TryParse(arguments[2], out var parentId) || parentId <= 0 ||
            !ArkiveReleasePolicy.Sha256Pattern().IsMatch(arguments[1])) throw new InvalidDataException("Invalid update request.");
        var installer = Path.GetFullPath(arguments[0]);
        var applicationDirectory = Path.GetFullPath(arguments[3]);
        var dataRoot = Path.GetFullPath(arguments[4]);
        executable = Path.Combine(applicationDirectory, "Arkive.App.exe");
        if (!File.Exists(executable) || !File.Exists(Path.Combine(applicationDirectory, ArkiveRuntimePaths.InstallationMarker)) ||
            !File.Exists(installer) || !Path.GetFileName(installer).StartsWith("Arkive-Setup-", StringComparison.Ordinal) ||
            !Path.GetExtension(installer).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            new FileInfo(installer).Length > ArkiveReleasePolicy.MaximumInstallerSize)
            throw new InvalidDataException("The installer or installed application is missing.");
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (!string.Equals(parent.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The updater parent is not this installed Arkive.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await parent.WaitForExitAsync(timeout.Token);
            parentExited = true;
        }
        catch (ArgumentException) { parentExited = true; }
        using var stream = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(arguments[1], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installer verification failed. Nothing was executed.");
        var start = new ProcessStartInfo(installer) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS", $"/DIR={applicationDirectory}", $"/LOG={Path.Combine(Path.GetDirectoryName(installer)!, "install.log")}" })
            start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("The installer did not start.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException($"The installer returned {process.ExitCode}. Check the install.log beside the downloaded update.");
        Restart(executable, dataRoot);
        return 0;
    }
    catch (Exception exception)
    {
        NativeMessageBox(IntPtr.Zero, $"Arkive was not updated successfully.\n\n{exception.Message}\n\nYour mods and backups were not intentionally changed. Restart Arkive manually if it does not reopen.", "Arkive update", 0x10);
        if (parentExited && executable is not null && File.Exists(executable))
            try { Restart(executable, arguments[4]); } catch (Exception) { }
        return 1;
    }
}

static void Restart(string executable, string dataRoot)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
    start.Environment["ARKIVE_DATA_ROOT"] = dataRoot;
    Process.Start(start)?.Dispose();
}

[DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
static extern int NativeMessageBox(IntPtr window, string text, string caption, uint type);
