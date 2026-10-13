using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Arkive.Core.Assets;

namespace Arkive.Core.External;

public sealed partial class UmodelBridge
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    public const int DefaultMaxPackageWideFallbackExports = 5_000;

    public UmodelBridge(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ExecutablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(ExecutablePath))
        {
            throw new FileNotFoundException("The Lost Ark UModel executable was not found.", ExecutablePath);
        }
    }

    public string ExecutablePath { get; }

    public async Task<UmodelListResult> ListAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
        => await ListAsync(packagePath, hardLinkScratchRoot: null, cancellationToken)
            .ConfigureAwait(false);

    public async Task<UmodelListResult> ListAsync(
        string packagePath,
        string? hardLinkScratchRoot,
        CancellationToken cancellationToken = default)
    {
        var fullPackagePath = ValidatePackage(packagePath);
        var stagedPackagePath = fullPackagePath;
        string? stagingDirectory = null;
        if (!string.IsNullOrWhiteSpace(hardLinkScratchRoot))
        {
            (stagedPackagePath, stagingDirectory) = TryStageHardLink(
                fullPackagePath,
                hardLinkScratchRoot);
        }

        var stopwatch = Stopwatch.StartNew();
        ProcessResult result;
        try
        {
            result = await RunAsync(
                ["-list", .. CommonArguments(resolvePackageNames: true), stagedPackagePath],
                DefaultTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CleanupHardLink(stagedPackagePath, stagingDirectory, fullPackagePath);
        }

        var assets = result.ExitCode == 0
            ? ParseListOutput(result.StandardOutput)
            : [];
        if (!stagedPackagePath.Equals(fullPackagePath, StringComparison.OrdinalIgnoreCase) &&
            (result.ExitCode != 0 || assets.Count == 0))
        {
            // Some packages may genuinely depend on neighboring cooked files.
            // Preserve correctness by retrying those from their original folder.
            result = await RunAsync(
                ["-list", .. CommonArguments(resolvePackageNames: true), fullPackagePath],
                DefaultTimeout,
                cancellationToken).ConfigureAwait(false);
            assets = result.ExitCode == 0
                ? ParseListOutput(result.StandardOutput)
                : [];
        }

        if (result.ExitCode != 0)
        {
            throw CreateProcessException("list package contents", result);
        }

        if (assets.Count == 0)
        {
            throw new UmodelBridgeException(
                "UModel completed without returning any parseable package exports.");
        }

        return new UmodelListResult(
            fullPackagePath,
            assets,
            CollectDiagnostics(result),
            stopwatch.Elapsed);
    }

    public async Task<UmodelExportResult> ExportAsync(
        string packagePath,
        UmodelAssetEntry asset,
        string outputDirectory,
        bool allowPackageWideFallback = true,
        CancellationToken cancellationToken = default)
        => await ExportCoreAsync(
            packagePath, asset, outputDirectory, packageSearchRoot: null,
            allowPackageWideFallback, cancellationToken).ConfigureAwait(false);

    public async Task<UmodelExportResult> ExportInPackageRootAsync(
        string packagePath,
        UmodelAssetEntry asset,
        string outputDirectory,
        string packageSearchRoot,
        bool allowPackageWideFallback = true,
        CancellationToken cancellationToken = default)
        => await ExportCoreAsync(
            packagePath, asset, outputDirectory, packageSearchRoot,
            allowPackageWideFallback, cancellationToken).ConfigureAwait(false);

    private async Task<UmodelExportResult> ExportCoreAsync(
        string packagePath,
        UmodelAssetEntry asset,
        string outputDirectory,
        string? packageSearchRoot,
        bool allowPackageWideFallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var fullPackagePath = ValidatePackage(packagePath);
        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);

        var arguments = new List<string> { "-export" };
        arguments.AddRange(CommonArguments(resolvePackageNames: false));
        if (!string.IsNullOrWhiteSpace(packageSearchRoot))
        {
            var fullSearchRoot = Path.GetFullPath(packageSearchRoot);
            if (!Directory.Exists(fullSearchRoot))
            {
                throw new DirectoryNotFoundException(
                    $"The UModel package search root was not found: {fullSearchRoot}");
            }
            arguments.Add($"-path={fullSearchRoot}");
        }
        arguments.Add($"-out={fullOutputDirectory}");
        arguments.Add("-nooverwrite");

        switch (asset.Kind)
        {
            case AssetKind.Texture:
                arguments.Add("-dds");
                break;
            case AssetKind.StaticMesh:
            case AssetKind.SkeletalMesh:
                arguments.Add("-gltf");
                arguments.Add("-lods");
                arguments.Add("-morphs");
                break;
        }

        arguments.Add(fullPackagePath);
        arguments.Add(asset.ObjectName);

        var stopwatch = Stopwatch.StartNew();
        var result = await RunAsync(arguments, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        var usedPackageWideFallback = false;
        var diagnostics = CollectDiagnostics(result).ToList();
        if (result.ExitCode != 0 &&
            string.Join(Environment.NewLine, result.StandardOutput, result.StandardError)
                .Contains("was not found in specified package(s)", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowPackageWideFallback)
            {
                throw new UmodelBridgeException(
                    "The backend rejected selected-object lookup. Arkive disabled package-wide fallback " +
                    "because this package is too large for a safe automatic bulk export. Use UModel's " +
                    "interactive viewer to export the specific object.");
            }

            // The Lost Ark v7 fork lists resolved object names but its command-line
            // name lookup can reject those same names. Keep the operation useful by
            // exporting the package into this asset's isolated staging directory.
            arguments.RemoveAt(arguments.Count - 1);
            result = await RunAsync(arguments, DefaultTimeout, cancellationToken).ConfigureAwait(false);
            usedPackageWideFallback = true;
            diagnostics.Add(
                "The backend rejected selected-object lookup; Arkive used package-wide export fallback.");
            diagnostics.AddRange(CollectDiagnostics(result));
        }

        if (result.ExitCode != 0)
        {
            throw CreateProcessException($"export {asset.ObjectName}", result);
        }

        var files = Directory
            .EnumerateFiles(fullOutputDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new UmodelExportResult(
            fullPackagePath,
            asset,
            fullOutputDirectory,
            files,
            diagnostics,
            stopwatch.Elapsed,
            usedPackageWideFallback);
    }

    /// <summary>
    /// Exports an object with its materials and PNG textures for Arkive's model viewer.
    /// Unlike <see cref="ExportAsync"/> it never falls back to a package-wide export.
    /// </summary>
    public async Task<UmodelExportResult> ExportForViewerAsync(
        string packagePath,
        UmodelAssetEntry asset,
        string outputDirectory,
        string? packageSearchRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var fullPackagePath = ValidatePackage(packagePath);
        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);
        var arguments = new List<string> { "-export" };
        arguments.AddRange(CommonArguments(resolvePackageNames: false));
        if (!string.IsNullOrWhiteSpace(packageSearchRoot) && Directory.Exists(packageSearchRoot))
            arguments.Add($"-path={Path.GetFullPath(packageSearchRoot)}");
        arguments.Add($"-out={fullOutputDirectory}");
        arguments.Add("-png");
        if (asset.Kind is AssetKind.StaticMesh or AssetKind.SkeletalMesh) arguments.Add("-gltf");
        arguments.Add(fullPackagePath);
        arguments.Add(asset.ObjectName);

        var stopwatch = Stopwatch.StartNew();
        var result = await RunAsync(arguments, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw CreateProcessException($"export {asset.ObjectName} for preview", result);
        var files = Directory.EnumerateFiles(fullOutputDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new UmodelExportResult(fullPackagePath, asset, fullOutputDirectory, files,
            CollectDiagnostics(result), stopwatch.Elapsed, false);
    }

    public Process StartPreview(string packagePath)
    {
        var fullPackagePath = ValidatePackage(packagePath);
        var startInfo = CreateStartInfo(redirectOutput: false);
        startInfo.ArgumentList.Add("-view");
        foreach (var argument in CommonArguments(resolvePackageNames: true))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(fullPackagePath);
        return Process.Start(startInfo) ??
            throw new UmodelBridgeException("UModel preview process could not be started.");
    }

    public Process StartAssetPreview(string packagePath, UmodelAssetEntry asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var fullPackagePath = ValidatePackage(packagePath);
        var startInfo = CreateStartInfo(redirectOutput: false);
        startInfo.ArgumentList.Add("-view");
        foreach (var argument in CommonArguments(resolvePackageNames: false))
        {
            startInfo.ArgumentList.Add(argument);
        }

        // This Lost Ark fork can locate object names when package-name resolution
        // and the class selector are omitted. Both options cause its lookup to
        // reject names that were printed successfully by -list.
        startInfo.ArgumentList.Add(fullPackagePath);
        startInfo.ArgumentList.Add(asset.ObjectName);

        return Process.Start(startInfo) ??
            throw new UmodelBridgeException("UModel asset preview process could not be started.");
    }

    /// <summary>
    /// Loads a model from its clean source package while preloading a staged
    /// replacement package. UModel resolves the model's material dependencies
    /// against the preloaded replacement without Arkive changing the game files.
    /// </summary>
    public Process StartAssetPreviewWithPackageOverlay(
        string modelPackagePath,
        UmodelAssetEntry asset,
        string replacementPackagePath,
        string packageSearchRoot,
        string? logPath = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var fullModelPath = ValidatePackage(modelPackagePath);
        var fullReplacementPath = ValidatePackage(replacementPackagePath);
        var fullSearchRoot = Path.GetFullPath(packageSearchRoot);
        if (!Directory.Exists(fullSearchRoot))
        {
            throw new DirectoryNotFoundException(
                $"The UModel package search root was not found: {fullSearchRoot}");
        }

        var startInfo = CreateStartInfo(redirectOutput: false);
        startInfo.ArgumentList.Add("-view");
        foreach (var argument in CommonArguments(resolvePackageNames: false))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add($"-path={fullSearchRoot}");
        startInfo.ArgumentList.Add($"-pkg={fullReplacementPath}");
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            var fullLogPath = Path.GetFullPath(logPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullLogPath)!);
            startInfo.ArgumentList.Add($"-log={fullLogPath}");
        }

        startInfo.ArgumentList.Add(fullModelPath);
        startInfo.ArgumentList.Add(asset.ObjectName);
        return Process.Start(startInfo) ??
            throw new UmodelBridgeException("UModel model-overlay preview process could not be started.");
    }

    public async Task<UmodelDependencyProbeResult> ProbeAssetDependenciesAsync(
        string modelPackagePath,
        UmodelAssetEntry asset,
        string packageSearchRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var fullModelPath = ValidatePackage(modelPackagePath);
        var fullSearchRoot = Path.GetFullPath(packageSearchRoot);
        if (!Directory.Exists(fullSearchRoot))
        {
            throw new DirectoryNotFoundException(
                $"The UModel package search root was not found: {fullSearchRoot}");
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await RunAsync(
            [
                "-dump",
                .. CommonArguments(resolvePackageNames: false),
                $"-path={fullSearchRoot}",
                fullModelPath,
                asset.ObjectName,
            ],
            DefaultTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateProcessException($"probe dependencies for {asset.ObjectName}", result);
        }

        var packages = string.Join(Environment.NewLine, result.StandardOutput, result.StandardError)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => LoadedPackageRegex().Match(line))
            .Where(match => match.Success)
            .Select(match => Path.GetFileName(match.Groups["package"].Value.Trim()))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (packages.Length == 0)
        {
            throw new UmodelBridgeException(
                $"UModel loaded {asset.ObjectName} but did not report its package dependencies.");
        }

        return new UmodelDependencyProbeResult(
            fullModelPath,
            asset,
            packages,
            CollectDiagnostics(result),
            stopwatch.Elapsed);
    }

    public async Task<UmodelObjectDumpResult> DumpObjectAsync(
        string packagePath,
        UmodelAssetEntry asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var fullPackagePath = ValidatePackage(packagePath);
        var stopwatch = Stopwatch.StartNew();
        var result = await RunAsync(
            [
                "-dump",
                .. CommonArguments(resolvePackageNames: false),
                fullPackagePath,
                asset.ObjectName,
            ],
            DefaultTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateProcessException($"dump {asset.ObjectName}", result);
        }

        var raw = string.Join(Environment.NewLine, result.StandardOutput, result.StandardError);
        if (!raw.Contains("Object info:", StringComparison.OrdinalIgnoreCase))
        {
            throw new UmodelBridgeException(
                $"UModel loaded {asset.ObjectName} but returned no object-information block.");
        }

        return new UmodelObjectDumpResult(
            fullPackagePath,
            asset,
            raw,
            CollectDiagnostics(result),
            stopwatch.Elapsed);
    }

    public Process StartAssetPreviewInPackageRoot(
        string modelPackagePath,
        UmodelAssetEntry asset,
        string packageSearchRoot,
        string? logPath = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var fullModelPath = ValidatePackage(modelPackagePath);
        var fullSearchRoot = Path.GetFullPath(packageSearchRoot);
        if (!Directory.Exists(fullSearchRoot))
        {
            throw new DirectoryNotFoundException(
                $"The UModel package search root was not found: {fullSearchRoot}");
        }

        var startInfo = CreateStartInfo(redirectOutput: false);
        startInfo.ArgumentList.Add("-view");
        foreach (var argument in CommonArguments(resolvePackageNames: false))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add($"-path={fullSearchRoot}");
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            var fullLogPath = Path.GetFullPath(logPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullLogPath)!);
            startInfo.ArgumentList.Add($"-log={fullLogPath}");
        }

        startInfo.ArgumentList.Add(fullModelPath);
        startInfo.ArgumentList.Add(asset.ObjectName);
        return Process.Start(startInfo) ??
            throw new UmodelBridgeException("UModel isolated model preview process could not be started.");
    }

    public static IReadOnlyList<UmodelAssetEntry> ParseListOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var assets = new List<UmodelAssetEntry>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = AssetLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups["index"].Value, out var index) ||
                !long.TryParse(
                    match.Groups["offset"].Value,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var offset) ||
                !long.TryParse(
                    match.Groups["size"].Value,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var size))
            {
                continue;
            }

            assets.Add(new UmodelAssetEntry(
                index,
                offset,
                size,
                match.Groups["class"].Value,
                match.Groups["name"].Value.Trim()));
        }

        return assets;
    }

    private static IReadOnlyList<string> CommonArguments(bool resolvePackageNames)
    {
        var arguments = new List<string> { "-game=lostark", "-na" };
        if (resolvePackageNames)
        {
            arguments.Add("-nameresolve");
        }

        arguments.Add("-noanim");
        return arguments;
    }

    private async Task<ProcessResult> RunAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(redirectOutput: true);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new UmodelBridgeException("UModel process could not be started.");
            }
        }
        catch (Exception exception) when (exception is not UmodelBridgeException)
        {
            throw new UmodelBridgeException("UModel process could not be started.", exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new UmodelBridgeException($"UModel exceeded the {timeout.TotalMinutes:N0}-minute timeout.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutputTask.ConfigureAwait(false),
            await standardErrorTask.ConfigureAwait(false));
    }

    private ProcessStartInfo CreateStartInfo(bool redirectOutput) => new()
    {
        FileName = ExecutablePath,
        WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
        UseShellExecute = false,
        CreateNoWindow = redirectOutput,
        RedirectStandardOutput = redirectOutput,
        RedirectStandardError = redirectOutput,
    };

    private static string ValidatePackage(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var fullPath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The package was not found.", fullPath);
        }

        return fullPath;
    }

    private static (string PackagePath, string? StagingDirectory) TryStageHardLink(
        string sourcePath,
        string scratchRoot)
    {
        try
        {
            var fullScratchRoot = Path.GetFullPath(scratchRoot);
            if (!string.Equals(
                    Path.GetPathRoot(sourcePath),
                    Path.GetPathRoot(fullScratchRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                return (sourcePath, null);
            }

            Directory.CreateDirectory(fullScratchRoot);
            var stagingDirectory = Path.Combine(
                fullScratchRoot,
                $"arkive-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            var hardLinkPath = Path.Combine(stagingDirectory, Path.GetFileName(sourcePath));
            if (!OperatingSystem.IsWindows() ||
                !CreateHardLinkWindows(hardLinkPath, sourcePath, IntPtr.Zero))
            {
                throw new IOException(
                    $"Could not create an indexing hard link (Windows error {Marshal.GetLastPInvokeError()}).");
            }

            return (hardLinkPath, stagingDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (sourcePath, null);
        }
    }

    private static void CleanupHardLink(
        string stagedPackagePath,
        string? stagingDirectory,
        string sourcePath)
    {
        if (stagingDirectory is null ||
            stagedPackagePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(stagedPackagePath);
            Directory.Delete(stagingDirectory);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static UmodelBridgeException CreateProcessException(string operation, ProcessResult result)
    {
        var detail = CollectDiagnostics(result).LastOrDefault() ?? "No diagnostic was returned.";
        return new UmodelBridgeException(
            $"UModel could not {operation} (exit code {result.ExitCode}): {detail}");
    }

    private static IReadOnlyList<string> CollectDiagnostics(ProcessResult result) =>
        string.Join(Environment.NewLine, result.StandardOutput, result.StandardError)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !AssetLineRegex().IsMatch(line))
            .ToArray();

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    [GeneratedRegex(
        @"^\s*(?<index>\d+)\s+(?<offset>[0-9A-Fa-f]+)\s+(?<size>[0-9A-Fa-f]+)\s+(?<class>\S+)\s+(?<name>.+?)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AssetLineRegex();

    [GeneratedRegex(
        @"^Loading package:\s+(?<package>.+?\.upk)\s+Ver:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoadedPackageRegex();

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
