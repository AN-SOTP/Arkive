using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Arkive.Core.Mods;

/// <summary>
/// Drops the copies of the game package a finished build no longer needs. A build writes the whole package several
/// times: a no-change rebuild that proves Arkive reproduces the original, a decompressed image of it, each step's
/// staged package, and the install payload. Once the build is verified only the payload and the final staged
/// package are read again, and those two are byte-identical; so the final staged package becomes a hard link to the
/// payload and the other copies are deleted. Each revision is trimmed once, marked by <see cref="MarkerFileName"/>.
/// </summary>
public static class PackageRevisionTrimmer
{
    public const string MarkerFileName = "arkive-trimmed.json";
    private const string LogicalImageFileName = "arkive-logical-package-image.bin";

    /// <returns>The bytes freed; 0 when the revision was already trimmed or does not look as expected.</returns>
    public static async Task<long> TrimAsync(
        string revisionDirectory, GuidedTexturePackageRevision revision, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(revisionDirectory);
        var marker = Path.Combine(root, MarkerFileName);
        if (!Directory.Exists(root) || File.Exists(marker)) return 0;
        var output = Path.GetFullPath(revision.OutputUpkPath);
        var payload = Path.GetFullPath(revision.PayloadPath);
        // Only revisions laid out as this Arkive builds them, whose payload is still the verified one.
        if (!IsWithin(root, output) || !IsWithin(root, payload) || output.Equals(payload, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(output) || !File.Exists(payload) ||
            !(await HashAsync(payload, cancellationToken).ConfigureAwait(false))
                .Equals(revision.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            !(await HashAsync(output, cancellationToken).ConfigureAwait(false))
                .Equals(revision.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            return 0;

        var freed = 0L;
        var outputBytes = new FileInfo(output).Length;
        var link = output + ".link";
        if (TryCreateHardLink(link, payload))
        {
            File.Move(link, output, overwrite: true);
            freed += outputBytes;
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(file);
            if (full.Equals(output, StringComparison.OrdinalIgnoreCase) || full.Equals(payload, StringComparison.OrdinalIgnoreCase)) continue;
            if (!full.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(full).Equals(LogicalImageFileName, StringComparison.OrdinalIgnoreCase)) continue;
            var bytes = new FileInfo(full).Length;
            File.Delete(full);
            freed += bytes;
        }
        await File.WriteAllTextAsync(marker,
            $"{{\"trimmedAt\":\"{DateTimeOffset.UtcNow:O}\",\"freedBytes\":{freed}}}", cancellationToken).ConfigureAwait(false);
        return freed;
    }

    private static bool TryCreateHardLink(string link, string existing)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (File.Exists(link)) File.Delete(link);
        // Fails on drives without hard links (FAT32, exFAT); the copy is then kept.
        return CreateHardLink(link, existing, IntPtr.Zero);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static bool IsWithin(string root, string path) =>
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
