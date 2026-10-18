using System.Security.Cryptography;
using System.Text.Json;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Packages;

namespace Arkive.Core.Interface;

/// <summary>
/// A working copy of one interface movie: the movie file as the game has it, and the copy the user edits (in a
/// Scaleform-aware editor such as JPEXS) that Arkive puts back into the package.
/// </summary>
public sealed record InterfaceMovieEditManifest(
    int SchemaVersion,
    string EditId,
    string DisplayName,
    DateTimeOffset CreatedAt,
    string PackageAlias,
    string PhysicalPackageName,
    string SourcePackagePath,
    string SourcePackageSha256,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    string SourceExportSha256,
    string OriginalMovieSha256,
    string OriginalMoviePath,
    string EditedMoviePath,
    bool IncludedInPackageBuild);

public sealed record InterfaceMovieEditEntry(string ManifestPath, InterfaceMovieEditManifest Manifest);

public sealed class InterfaceMovieEditWorkspaceStore
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "arkive-interface-movie-edit.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IChunkProtectionProvider _protectionProvider;

    public InterfaceMovieEditWorkspaceStore(string rootDirectory, IChunkProtectionProvider? protectionProvider = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
    }

    public string RootDirectory { get; }

    public async Task<IReadOnlyList<InterfaceMovieEditEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var entries = new List<InterfaceMovieEditEntry>();
        foreach (var path in Directory.EnumerateFiles(RootDirectory, ManifestFileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                entries.Add(await LoadEntryAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                                  NotSupportedException or InvalidDataException)
            {
                // One damaged working copy must not hide the others.
            }
        }
        return entries.OrderByDescending(entry => entry.Manifest.CreatedAt).ToArray();
    }

    public static async Task<InterfaceMovieEditEntry> LoadEntryAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous);
        var manifest = await JsonSerializer.DeserializeAsync<InterfaceMovieEditManifest>(stream, JsonOptions, cancellationToken)
                           .ConfigureAwait(false) ??
                       throw new InvalidDataException("The interface movie working copy record is empty.");
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Interface movie working copy schema {manifest.SchemaVersion} is unsupported.");
        return new InterfaceMovieEditEntry(Path.GetFullPath(manifestPath), manifest);
    }

    /// <summary>
    /// Copies an interface movie out of a clean package into a new working copy. <paramref name="editedMovie"/> (a
    /// movie from a mod) becomes the edited file; otherwise the edited file starts as the game's movie.
    /// </summary>
    public async Task<InterfaceMovieEditEntry> CreateAsync(
        string packagePath,
        string packageAlias,
        string packageSha256,
        AssetCatalogEntry export,
        string displayName,
        string? editedMovie = null,
        CancellationToken cancellationToken = default)
    {
        var exportBytes = ReadExport(packagePath, export);
        var movie = GfxMovieFile.Extract(exportBytes);
        GfxMovieReader.Read(movie);
        var editId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(RootDirectory, editId);
        Directory.CreateDirectory(directory);
        var original = Path.Combine(directory, "original" + GfxMovieFile.Extension);
        var edited = Path.Combine(directory, SafeFileName(export.ObjectName) + GfxMovieFile.Extension);
        await File.WriteAllBytesAsync(original, movie, cancellationToken).ConfigureAwait(false);
        if (editedMovie is null) await File.WriteAllBytesAsync(edited, movie, cancellationToken).ConfigureAwait(false);
        else File.Copy(editedMovie, edited, overwrite: false);
        var manifest = new InterfaceMovieEditManifest(
            CurrentSchemaVersion, editId, displayName, DateTimeOffset.UtcNow, packageAlias, Path.GetFileName(packagePath),
            Path.GetFullPath(packagePath), packageSha256.ToUpperInvariant(), export.ExportIndex, export.ClassName,
            export.ObjectName, export.SerialOffset, export.SerialSize, Hash(exportBytes), Hash(movie), original, edited,
            IncludedInPackageBuild: true);
        var entry = new InterfaceMovieEditEntry(Path.Combine(directory, ManifestFileName), manifest);
        await SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        return entry;
    }

    public async Task<InterfaceMovieEditEntry> SetIncludedAsync(
        InterfaceMovieEditEntry entry, bool included, CancellationToken cancellationToken = default)
    {
        var updated = entry with { Manifest = entry.Manifest with { IncludedInPackageBuild = included } };
        await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public Task DeleteAsync(InterfaceMovieEditEntry entry, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(entry.ManifestPath))!;
        if (!directory.StartsWith(RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The working copy is outside the interface movie workspace.");
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>The edited movie, checked to still be a Scaleform movie Arkive can read.</summary>
    public static async Task<byte[]> ReadEditedMovieAsync(InterfaceMovieEditEntry entry, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(entry.Manifest.EditedMoviePath))
            throw new FileNotFoundException($"{entry.Manifest.DisplayName}'s edited movie is missing.", entry.Manifest.EditedMoviePath);
        var movie = await File.ReadAllBytesAsync(entry.Manifest.EditedMoviePath, cancellationToken).ConfigureAwait(false);
        try
        {
            GfxMovieReader.Read(movie);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new InvalidDataException(
                $"{entry.Manifest.DisplayName}'s edited file is not a Scaleform movie Arkive can read. Save it as GFX (not SWF). {exception.Message}");
        }
        return movie;
    }

    /// <summary>True while the edited movie is byte-identical to the game's.</summary>
    public static bool IsUnchanged(InterfaceMovieEditEntry entry)
    {
        var edited = new FileInfo(entry.Manifest.EditedMoviePath);
        var original = new FileInfo(entry.Manifest.OriginalMoviePath);
        return edited.Exists && original.Exists && edited.Length == original.Length &&
               File.ReadAllBytes(edited.FullName).AsSpan().SequenceEqual(File.ReadAllBytes(original.FullName));
    }

    private byte[] ReadExport(string packagePath, AssetCatalogEntry export)
    {
        var summary = new UpkPackageReader().Read(packagePath);
        using var logical = new LogicalPackageStream(summary, _protectionProvider);
        var bytes = new byte[export.SerialSize];
        logical.Position = export.SerialOffset;
        logical.ReadExactly(bytes);
        return bytes;
    }

    private async Task SaveAsync(InterfaceMovieEditEntry entry, CancellationToken cancellationToken)
    {
        var temporary = entry.ManifestPath + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(entry.Manifest, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporary, entry.ManifestPath, overwrite: true);
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
