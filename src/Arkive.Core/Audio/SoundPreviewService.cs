using System.Diagnostics;
using System.Security.Cryptography;
using Arkive.Core.External;

namespace Arkive.Core.Audio;

/// <summary>
/// Turns a clip into a .wav anyone can play. Arkive's own replacements are already PCM; the game's Wwise Vorbis audio
/// is decoded by vgmstream (tools\external\vgmstream\vgmstream-cli.exe). Results are kept in cache\sounds\preview.
/// </summary>
public sealed class SoundPreviewService
{
    public static readonly string DecoderRelativePath = Path.Combine("vgmstream", "vgmstream-cli.exe");
    private const long CacheLimitBytes = 512L * 1024 * 1024;
    private readonly string _cacheDirectory;

    public SoundPreviewService(string dataRoot)
    {
        _cacheDirectory = Path.Combine(dataRoot, "cache", "sounds", "preview");
    }

    public static string? FindDecoder() => UmodelLocator.FindTool(DecoderRelativePath);

    public async Task<string> GetWavAsync(SoundClip clip, CancellationToken cancellationToken = default)
    {
        var wem = await Task.Run(() => SoundCatalog.ReadAudio(clip), cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_cacheDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(wem), 0, 8);
        var wavPath = Path.Combine(_cacheDirectory, $"{clip.SourceId}-{hash}.wav");
        if (File.Exists(wavPath))
        {
            File.SetLastWriteTimeUtc(wavPath, DateTime.UtcNow);
            return wavPath;
        }

        var format = WemAudio.ReadFormat(wem);
        if (format?.IsPcm == true)
        {
            await File.WriteAllBytesAsync(wavPath, WemAudio.PcmWemToWav(wem), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var decoder = FindDecoder() ?? throw new FileNotFoundException(
                "Arkive's sound decoder is missing (tools\\external\\vgmstream). Reinstall Arkive to restore it.");
            var wemPath = Path.Combine(_cacheDirectory, $"decode-{Guid.NewGuid():N}.wem");
            var partial = wavPath + ".partial.wav";
            try
            {
                await File.WriteAllBytesAsync(wemPath, wem, cancellationToken).ConfigureAwait(false);
                await RunDecoderAsync(decoder, wemPath, partial, cancellationToken).ConfigureAwait(false);
                File.Move(partial, wavPath, overwrite: true);
            }
            finally
            {
                TryDelete(wemPath);
                TryDelete(partial);
            }
        }
        Trim();
        return wavPath;
    }

    private static async Task RunDecoderAsync(string decoder, string input, string output, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(decoder)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(decoder)!,
        };
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add(input);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The sound decoder did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        var message = (await stderr.ConfigureAwait(false)).Trim();
        await stdout.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidDataException($"The sound could not be decoded: {(message.Length > 0 ? message : $"exit code {process.ExitCode}")}");
    }

    private void Trim()
    {
        var files = new DirectoryInfo(_cacheDirectory).GetFiles("*.wav")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Length;
            if (total > CacheLimitBytes) TryDelete(file.FullName);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
