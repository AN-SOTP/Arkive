using System.Collections.Concurrent;
using Arkive.Core.Discovery;
using Arkive.Core.Inspection;

namespace Arkive.Core.Textures;

/// <summary>
/// Reads the mips a streamed texture keeps outside its package. A Texture2D whose larger mips have bulk flag 0x1 names
/// its texture file cache (TextureFileCacheName, "textures"); the game ships that cache next to the Packages folder
/// under a scrambled name like every other file (ReleasePC\LOD9LS7OEDEP1EBMOHOT5O.upk decodes to "textures"), and a
/// mip is the StoredSize raw bytes at its BulkDataOffset there, encoded as the package's own mips are (Crunch or
/// Lost Ark LZ4). Read-only: the game's cache is never written.
/// </summary>
public static class TextureFileCache
{
    public const uint SeparateFileFlag = 0x1;
    private const string CacheNameProperty = "TextureFileCacheName";
    private static readonly ConcurrentDictionary<(string Directory, string Name), string?> Located =
        new(new FolderNameComparer());

    /// <summary>True when the mip's bytes are in the texture file cache rather than in the package.</summary>
    public static bool IsExternal(TextureMipEvidence mip) =>
        mip.PayloadRelativeOffset is null && (mip.BulkDataFlags & SeparateFileFlag) != 0 &&
        mip.StoredSize > 0 && mip.BulkDataOffset >= 0;

    /// <summary>The texture's cache name ("textures"), or null when it names none.</summary>
    public static string? CacheName(TextureReconnaissanceReport report) =>
        report.Properties.FirstOrDefault(item => item.Name.Equals(CacheNameProperty, StringComparison.OrdinalIgnoreCase))
            ?.ValuePreview is { Length: > 0 } name && !name.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? name
            : null;

    /// <summary>
    /// The cache file for <paramref name="cacheName"/>: looked for in the package's folder and the folder above it
    /// (Packages\..\), by scrambled name or as a plain &lt;name&gt;.tfc. Null when there is none.
    /// </summary>
    public static string? Locate(string packagePath, string cacheName)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(packagePath))!;
        // The folder above first: the game keeps its cache in ReleasePC, beside a dozen files, not among the
        // tens of thousands in Packages.
        foreach (var directory in new[] { Path.GetDirectoryName(folder), folder }.OfType<string>())
        {
            var found = Located.GetOrAdd((directory, cacheName), key => Find(key.Directory, key.Name));
            if (found is not null && File.Exists(found)) return found;
        }
        return null;
    }

    /// <summary>The stored (still encoded) bytes of an external mip.</summary>
    /// <exception cref="FileNotFoundException">The texture names no cache, or the cache file is not there.</exception>
    /// <exception cref="InvalidDataException">The mip's range is outside the cache file.</exception>
    public static byte[] ReadMip(TextureReconnaissanceReport report, TextureMipEvidence mip)
    {
        var name = CacheName(report) ?? throw new FileNotFoundException(
            $"{report.ObjectName} streams mip {mip.MipIndex} from a texture file cache but names none.");
        var path = Locate(report.PackagePath, name) ?? throw new FileNotFoundException(
            $"The texture file cache \"{name}\" that {report.ObjectName} streams its larger mips from was not found next to the package.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (mip.BulkDataOffset > stream.Length - mip.StoredSize)
            throw new InvalidDataException(
                $"Mip {mip.MipIndex} of {report.ObjectName} lies outside the texture file cache \"{name}\".");
        var bytes = new byte[mip.StoredSize];
        stream.Position = mip.BulkDataOffset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string? Find(string directory, string cacheName)
    {
        if (!Directory.Exists(directory)) return null;
        var plain = Path.Combine(directory, cacheName + ".tfc");
        if (File.Exists(plain)) return plain;
        foreach (var file in Directory.EnumerateFiles(directory))
            if (LostArkPackageName.TryDecodeFileName(Path.GetFileName(file), out var decoded) &&
                decoded.Equals(cacheName, StringComparison.OrdinalIgnoreCase))
                return file;
        return null;
    }

    private sealed class FolderNameComparer : IEqualityComparer<(string Directory, string Name)>
    {
        public bool Equals((string Directory, string Name) x, (string Directory, string Name) y) =>
            string.Equals(x.Directory, y.Directory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Directory, string Name) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Directory),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }
}
