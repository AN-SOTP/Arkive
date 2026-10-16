using System.Diagnostics;
using System.Security.Cryptography;
using Arkive.Core.External;

namespace Arkive.Core.Textures;

public sealed record CrunchEncodeResult(
    string OutputPath,
    long OutputBytes,
    string OutputSha256,
    int QualityLevel,
    string FormatArgument,
    string CommandSummary,
    IReadOnlyList<string> Diagnostics,
    TimeSpan Duration);

public interface ICrunchEncoder
{
    string Name { get; }
    string? ExecutablePath { get; }

    Task<CrunchEncodeResult> EncodeAsync(
        string singleMipImagePath,
        string outputCrnPath,
        NativeTextureFormatProfile format,
        CancellationToken cancellationToken = default);
}

public static class CrunchEncoderLocator
{
    public const string ExecutableFileName = "crunch_x64.exe";
    public const string ExpectedSha256 =
        "8AE949BFC3E3E4A1717DCA8845CE8ED480638DE68CBF1D7CBE912E99E35CE06F";

    // Installed copies have no Arkive.slnx, so the encoder is looked up beside the application too.
    public static string? FindExecutable() => UmodelLocator.FindTool(ExecutableFileName);
}

/// <summary>
/// Invokes the exact Unity-Technologies/crunch v1.04 encoder pinned by Arkive.
/// Modern Binomial Crunch streams are deliberately rejected because Lost Ark's
/// decoder expects this older CRN dialect.
/// </summary>
public sealed class UnityCrunchEncoder : ICrunchEncoder
{
    public const int QualityLevel = 255;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public UnityCrunchEncoder(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ExecutablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(ExecutablePath))
        {
            throw new FileNotFoundException("The pinned Unity Crunch encoder was not found.", ExecutablePath);
        }

        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ExecutablePath)));
        if (!hash.Equals(CrunchEncoderLocator.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Crunch encoder SHA-256 {hash} is not Arkive's pinned Unity build " +
                $"{CrunchEncoderLocator.ExpectedSha256}.");
        }
    }

    public string Name =>
        "Unity-Technologies Crunch v1.04 commit 8708900e, quality 255";

    public string ExecutablePath { get; }

    public async Task<CrunchEncodeResult> EncodeAsync(
        string singleMipImagePath,
        string outputCrnPath,
        NativeTextureFormatProfile format,
        CancellationToken cancellationToken = default)
    {
        var input = Path.GetFullPath(singleMipImagePath);
        var output = Path.GetFullPath(outputCrnPath);
        if (!File.Exists(input))
        {
            throw new FileNotFoundException("The single-mip DDS input was not found.", input);
        }
        if (File.Exists(output))
        {
            throw new IOException($"Crunch output already exists: {output}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var formatArgument = format.PixelFormatValue switch
        {
            5 => "-dxt1",
            7 => "-dxt5",
            24 => "-DXN",
            _ => throw new NotSupportedException(
                $"Unity Crunch encoding is not registered for {format.PixelFormatName}."),
        };
        var arguments = new List<string>
        {
            "-file", input,
            "-out", output,
            "-fileformat", "crn",
            formatArgument,
            "-mipMode", "None",
            "-quality", QualityLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-helperThreads", "0",
            "-noprogress",
            "-forcewrite",
            "-nostats",
        };
        if (format.PixelFormatValue == 24)
        {
            arguments.Add("-uniformMetrics");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(input)!,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo) ??
                            throw new IOException("The Unity Crunch encoder could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process exited between the state check and termination.
            }
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Unity Crunch encoding exceeded {Timeout.TotalMinutes:N0} minutes.");
            }
            throw;
        }

        var stdout = await standardOutput.ConfigureAwait(false);
        var stderr = await standardError.ConfigureAwait(false);
        watch.Stop();
        if (process.ExitCode != 0 || !File.Exists(output))
        {
            throw new IOException(
                $"Unity Crunch encoding failed with exit code {process.ExitCode}. " +
                FirstUsefulLine(stderr, stdout));
        }

        var bytes = await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
        var diagnostics = string.Join(Environment.NewLine, stdout, stderr)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line =>
                line.Contains("Source texture:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Compressing using", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("successfully written", StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToArray();
        return new CrunchEncodeResult(
            output,
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)),
            QualityLevel,
            formatArgument,
            $"{Path.GetFileName(ExecutablePath)} {formatArgument} -mipMode None -quality {QualityLevel}",
            diagnostics,
            watch.Elapsed);
    }

    private static string FirstUsefulLine(params string[] values)
    {
        var lines = values
            .SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        return lines.FirstOrDefault(line =>
                   line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("failed", StringComparison.OrdinalIgnoreCase)) ??
               lines.LastOrDefault() ??
               "No encoder diagnostics were returned.";
    }
}
