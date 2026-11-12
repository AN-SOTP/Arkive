using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Arkive.Core.Mods;

/// <summary>
/// Drops what a finished build no longer needs, in two passes, each done once per build and recorded in
/// <see cref="MarkerFileName"/> (the second pass writes version 2 into it; markers written before have no version).
/// <para>
/// First pass: the copies of the game package. A build writes the whole package several times: a no-change rebuild
/// that proves Arkive reproduces the original, a decompressed image of it, each step's staged package, and the install
/// payload. Once the build is verified only the payload and the final staged package are read again, and those two are
/// byte-identical; so the final staged package becomes a hard link to the payload and the other copies are deleted.
/// </para>
/// <para>
/// Second pass (<see cref="IsUnneededFile"/>): the inputs a combined build kept from each step. After a build is
/// committed, Arkive reads only its bundle (install, restore, install state, mod sets), its composition record and final
/// package (verification, previews), and from each step its small records: the mesh reconnaissance and editable
/// reference records (dependency view), the encoding or compilation records (the package view's animation proof) and
/// the staged-package records. Nothing reads the step's copies of meshes (.glb), encoded buffers and candidates (.bin),
/// texture images (.dds, .png, .crn, .ue3bulk), or the rebased copy of the edited mesh, so these are deleted. Builds
/// without a composition record (texture builds from before combined builds) are left as they are.
/// </para>
/// </summary>
public static partial class PackageRevisionTrimmer
{
    public const string MarkerFileName = "arkive-trimmed.json";

    /// <summary>The marker version the second pass writes.</summary>
    public const int CurrentMarkerVersion = 2;

    private const string LogicalImageFileName = "arkive-logical-package-image.bin";

    private static readonly HashSet<string> UnneededExtensions =
        new([".glb", ".bin", ".dds", ".png", ".crn", ".ue3bulk"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The first pass only; see <see cref="TrimAsync(string, GuidedTexturePackageRevision, bool, CancellationToken)"/>.</summary>
    public static Task<long> TrimAsync(
        string revisionDirectory, GuidedTexturePackageRevision revision, CancellationToken cancellationToken = default) =>
        TrimAsync(revisionDirectory, revision, unneededFiles: false, cancellationToken);

    /// <param name="unneededFiles">Also run the second pass, when the build passes every check again.</param>
    /// <returns>The bytes freed; 0 when there was nothing left to trim or the build does not look as expected.</returns>
    public static async Task<long> TrimAsync(
        string revisionDirectory, GuidedTexturePackageRevision revision, bool unneededFiles,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(revisionDirectory);
        var marker = Path.Combine(root, MarkerFileName);
        if (!Directory.Exists(root)) return 0;
        var version = MarkerVersion(marker);
        var freed = 0L;
        if (version == 0)
        {
            var copies = await TrimPackageCopiesAsync(root, revision, cancellationToken).ConfigureAwait(false);
            if (copies is null) return 0;
            freed = copies.Value;
            await WriteMarkerAsync(marker, 1, freed, cancellationToken).ConfigureAwait(false);
            version = 1;
        }
        if (!unneededFiles || version >= CurrentMarkerVersion) return freed;

        var files = await VerifiedUnneededFilesAsync(root, revision, cancellationToken).ConfigureAwait(false);
        if (files is null) return freed;
        var removed = 0L;
        var emptied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = file.Length;
            file.Delete();
            removed += bytes;
            emptied.Add(file.DirectoryName!);
        }
        // Folders left empty by this pass go too (a step's rebased-edit folder); none outside the build.
        foreach (var start in emptied.OrderByDescending(path => path.Length))
            for (var directory = start; IsWithin(root, directory) && Directory.Exists(directory) &&
                                        !Directory.EnumerateFileSystemEntries(directory).Any();
                 directory = Path.GetDirectoryName(directory)!)
                Directory.Delete(directory);
        await WriteMarkerAsync(marker, CurrentMarkerVersion, MarkerFreedBytes(marker) + removed, cancellationToken)
            .ConfigureAwait(false);
        return freed + removed;
    }

    /// <summary>
    /// What the second pass would delete from this build now, without checking the build's hashes again (for a plan
    /// shown to the user; the trim itself checks first). Empty when the build has had it, or is not a combined build
    /// laid out as Arkive lays them out.
    /// </summary>
    public static IReadOnlyList<FileInfo> PlanUnneededFiles(string revisionDirectory, GuidedTexturePackageRevision revision)
    {
        var root = Path.GetFullPath(revisionDirectory);
        if (!Directory.Exists(root) || MarkerVersion(Path.Combine(root, MarkerFileName)) >= CurrentMarkerVersion ||
            !LooksComposed(root, revision)) return [];
        return UnneededFiles(root, revision);
    }

    /// <summary>
    /// Whether the second pass deletes <paramref name="relativePath"/> (relative to the build folder): a mesh, buffer
    /// or image copy outside the bundle, or anything in a step's rebased copy of the edited mesh.
    /// </summary>
    public static bool IsUnneededFile(string relativePath)
    {
        var parts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0].Equals("bundle", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts.Length >= 3 && StepFolder().IsMatch(parts[0]) &&
            parts[1].Equals("03-rebased-edit", StringComparison.OrdinalIgnoreCase)) return true;
        return UnneededExtensions.Contains(Path.GetExtension(parts[^1]));
    }

    private static IReadOnlyList<FileInfo> UnneededFiles(string root, GuidedTexturePackageRevision revision)
    {
        var output = Path.GetFullPath(revision.OutputUpkPath);
        var payload = Path.GetFullPath(revision.PayloadPath);
        return new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => !file.FullName.Equals(output, StringComparison.OrdinalIgnoreCase) &&
                           !file.FullName.Equals(payload, StringComparison.OrdinalIgnoreCase) &&
                           IsUnneededFile(Path.GetRelativePath(root, file.FullName)))
            .ToArray();
    }

    private static bool LooksComposed(string root, GuidedTexturePackageRevision revision)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(revision.CompositionManifestPath) &&
                   Path.GetFullPath(revision.CompositionManifestPath).Equals(
                       Path.Combine(root, UnifiedPackageComposerService.ManifestFileName), StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(revision.CompositionManifestPath) &&
                   IsWithin(Path.Combine(root, "bundle"), Path.GetFullPath(revision.ModBundleManifestPath)) &&
                   IsWithin(Path.Combine(root, "bundle"), Path.GetFullPath(revision.PayloadPath)) &&
                   IsWithin(root, Path.GetFullPath(revision.OutputUpkPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The second pass's files, once the build passes the checks its readers make: the composition verifies (its final
    /// package and payload match it), and it, the bundle and the package project record agree on the payload.
    /// </summary>
    private static async Task<IReadOnlyList<FileInfo>?> VerifiedUnneededFilesAsync(
        string root, GuidedTexturePackageRevision revision, CancellationToken cancellationToken)
    {
        if (!LooksComposed(root, revision)) return null;
        try
        {
            var verification = await UnifiedPackageComposerService.VerifyOutputAsync(
                revision.CompositionManifestPath!, cancellationToken).ConfigureAwait(false);
            var manifest = verification.Manifest;
            var bundle = await ArkiveModBundleService.LoadBundleAsync(revision.ModBundleManifestPath, cancellationToken)
                .ConfigureAwait(false);
            if (!Same(verification.OutputUpkPath, revision.OutputUpkPath) || !Same(manifest.PayloadPath, revision.PayloadPath) ||
                !Same(manifest.BundleManifestPath, revision.ModBundleManifestPath) ||
                !manifest.FinalOutputUpkSha256.Equals(revision.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
                !bundle.PayloadSha256.Equals(revision.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                              JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
        return UnneededFiles(root, revision);

        static bool Same(string left, string right) =>
            Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The first pass; null when the build is not laid out as expected or its payload is not the verified one.</summary>
    private static async Task<long?> TrimPackageCopiesAsync(
        string root, GuidedTexturePackageRevision revision, CancellationToken cancellationToken)
    {
        var output = Path.GetFullPath(revision.OutputUpkPath);
        var payload = Path.GetFullPath(revision.PayloadPath);
        // Only revisions laid out as this Arkive builds them, whose payload is still the verified one.
        if (!IsWithin(root, output) || !IsWithin(root, payload) || output.Equals(payload, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(output) || !File.Exists(payload) ||
            !(await HashAsync(payload, cancellationToken).ConfigureAwait(false))
                .Equals(revision.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            !(await HashAsync(output, cancellationToken).ConfigureAwait(false))
                .Equals(revision.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            return null;

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
        return freed;
    }

    /// <summary>0 without a marker; 1 for a marker from before versions were written (or one that cannot be read).</summary>
    public static int MarkerVersion(string markerPath)
    {
        if (!File.Exists(markerPath)) return 0;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(markerPath));
            return document.RootElement.TryGetProperty("version", out var version) && version.TryGetInt32(out var value)
                ? Math.Max(1, value)
                : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return 1;
        }
    }

    private static long MarkerFreedBytes(string markerPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(markerPath));
            return document.RootElement.TryGetProperty("freedBytes", out var freed) && freed.TryGetInt64(out var value) ? value : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return 0;
        }
    }

    private static Task WriteMarkerAsync(string marker, int version, long freed, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(marker,
            $"{{\"trimmedAt\":\"{DateTimeOffset.UtcNow:O}\",\"freedBytes\":{freed},\"version\":{version}}}", cancellationToken);

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

    /// <summary>A mesh or static mesh step folder of a combined build: m01-1a2b3c4d, s02-...</summary>
    [GeneratedRegex("^[ms][0-9]{2,3}-[0-9a-f]{8}$", RegexOptions.IgnoreCase)]
    private static partial Regex StepFolder();

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
