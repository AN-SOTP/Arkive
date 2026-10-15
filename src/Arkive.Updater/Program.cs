using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Arkive.Core.Updates;

// Same file name as ArkiveRuntimePaths.InstallationMarker, which the helper doesn't link.
const string InstallationMarker = "arkive-installation.json";

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
        if (!File.Exists(executable) || !File.Exists(Path.Combine(applicationDirectory, InstallationMarker)) ||
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
            try { await parent.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException exception)
            {
                throw new ArkiveUpdateException("Arkive did not close within 2 minutes, so the installer was not started.",
                    "Close Arkive, including any dialog it is showing, then choose Install update and restart again.", exception);
            }
            parentExited = true;
        }
        catch (ArgumentException) { parentExited = true; }
        using var stream = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(arguments[1], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded installer changed after Arkive verified it, so it was not run.");
        var log = Path.Combine(Path.GetDirectoryName(installer)!, "install.log");
        var start = new ProcessStartInfo(installer) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS", $"/DIR={applicationDirectory}", $"/LOG={log}" })
            start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new ArkiveUpdateException("Windows did not start the installer.",
            $"Security software may have blocked it. Allow it, or run the downloaded installer yourself: {installer}");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw ArkiveUpdateFailure.InstallerExit(process.ExitCode, installer, log);
        Restart(executable, dataRoot);
        return 0;
    }
    catch (Exception exception)
    {
        var failure = ArkiveUpdateFailure.Describe(ArkiveUpdateStage.Install, exception);
        var reopen = parentExited && executable is not null && File.Exists(executable);
        NativeMessageBox(IntPtr.Zero, failure.Text + (reopen
            ? "\n\nArkive will reopen when you close this message."
            : "\n\nStart Arkive again when you are ready."), "Arkive update failed", 0x10);
        if (reopen)
            try { Restart(executable!, arguments[4]); } catch (Exception) { }
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
