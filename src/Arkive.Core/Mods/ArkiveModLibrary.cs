using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Arkive.Core.Mods;

public enum ArkiveModOrigin
{
    Imported,
    Created,
    Migrated,
}

public sealed record ArkiveModAssetBinding(
    string AssetId,
    UnifiedPackageAssetKind Kind,
    string EditId,
    string SourcePackagePath,
    string PackedContentSha256);

public sealed record ArkiveModLibraryEntry(
    string ModId,
    string FileName,
    string FileSha256,
    long FileBytes,
    DateTimeOffset FileLastWriteTimeUtc,
    bool Enabled,
    int Priority,
    ArkiveModOrigin Origin,
    ArkiveModFileManifest Manifest,
    IReadOnlyList<ArkiveModAssetBinding> Bindings)
{
    public string? AuthoredForPackageProjectId { get; init; }

    public ArkiveModAssetBinding? BindingFor(string assetId) => Bindings.FirstOrDefault(binding =>
        binding.AssetId.Equals(assetId, StringComparison.OrdinalIgnoreCase));
}

public sealed record ArkiveModLibraryManifest(
    int SchemaVersion,
    DateTimeOffset UpdatedAt,
    string? GamePackagesDirectory,
    IReadOnlyList<ArkiveModLibraryEntry> Mods)
{
    public bool PackageProjectsMigrated { get; init; }

    public ArkiveModLibraryEntry? Find(string modId) => Mods.FirstOrDefault(mod =>
        mod.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase));

    public ArkiveModLibraryEntry? AuthoredFor(string packageProjectId) => Mods.FirstOrDefault(mod =>
        packageProjectId.Equals(mod.AuthoredForPackageProjectId, StringComparison.OrdinalIgnoreCase));

    public (ArkiveModLibraryEntry Mod, ArkiveModAssetBinding Binding)? FindOwner(string editId)
    {
        foreach (var mod in Mods)
        {
            var binding = mod.Bindings.FirstOrDefault(item =>
                item.EditId.Equals(editId, StringComparison.OrdinalIgnoreCase));
            if (binding is not null) return (mod, binding);
        }
        return null;
    }
}

public sealed record ArkiveModLibraryIssue(string Path, string Message);

public sealed record ArkiveModReleasedEdit(string EditId, bool Delete);

public sealed record ArkiveModLibraryScanResult(
    ArkiveModLibraryManifest Library,
    IReadOnlyList<string> RegisteredModIds,
    IReadOnlyList<string> RefreshedModIds,
    IReadOnlyList<ArkiveModLibraryIssue> Issues,
    IReadOnlyList<ArkiveModReleasedEdit> Released);

public sealed record ArkiveModImportResult(
    ArkiveModLibraryEntry Entry,
    bool UpdatedExistingMod,
    IReadOnlyList<ArkiveModReleasedEdit> Released,
    ArkiveModLibraryManifest Library);

public sealed class ArkiveModLibrary
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "arkive-mod-library.json";
    public const int MinimumPriority = -1_000_000;
    public const int MaximumPriority = 1_000_000;
    private const string LockFileName = ".arkive-mod-library.lock";
    // Not indented: with large mods the indented file was 2.4 times the size, and it is written on every change.
    private static readonly JsonSerializerOptions StateJsonOptions = new(ArkiveModFileService.JsonOptions) { WriteIndented = false };
    private readonly ConcurrentDictionary<string, (long Length, DateTime WriteTime, string Message)> _unreadable =
        new(StringComparer.OrdinalIgnoreCase);

    public ArkiveModLibrary(string modsDirectory, string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ModsDirectory = Path.GetFullPath(modsDirectory);
        StateDirectory = Path.GetFullPath(stateDirectory);
    }

    public string ModsDirectory { get; }

    public string StateDirectory { get; }

    public string ManifestPath => Path.Combine(StateDirectory, ManifestFileName);

    public string MaterializationRoot => Path.Combine(StateDirectory, "materialized");

    public string GetModFilePath(ArkiveModLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RequireWithinMods(Path.Combine(ModsDirectory, entry.FileName));
    }

    public string CreateWorkingDirectory(string modId, string assetId, string purpose)
    {
        var path = Path.Combine(
            MaterializationRoot, modId[..Math.Min(8, modId.Length)],
            $"{assetId[..Math.Min(8, assetId.Length)]}-{purpose}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task<ArkiveModLibraryManifest> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ManifestPath))
            return new ArkiveModLibraryManifest(CurrentSchemaVersion, DateTimeOffset.UtcNow, null, []);
        byte[] bytes;
        await using (var stream = new FileStream(
                         ManifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            using var buffer = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, int.MaxValue) : 0);
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }
        // The file embeds every mod's manifest (tens of MB with large mods) and is read several times for each
        // change in the Mods list. Parsing it takes most of a second; checking its bytes are the ones parsed last
        // takes a few milliseconds, and unlike a size and time check it cannot miss a write.
        var sha256 = SHA256.HashData(bytes);
        if (_loaded is { } loaded && loaded.Sha256.AsSpan().SequenceEqual(sha256))
            return loaded.Manifest;
        var manifest = JsonSerializer.Deserialize<ArkiveModLibraryManifest>(bytes, ArkiveModFileService.JsonOptions) ??
                       throw new InvalidDataException("The Arkive mod library manifest was empty.");
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Mod library schema {manifest.SchemaVersion} is unsupported; Arkive requires {CurrentSchemaVersion}.");
        manifest = Freeze(manifest);
        _loaded = new LoadedManifest(sha256, manifest);
        return manifest;
    }

    private sealed record LoadedManifest(byte[] Sha256, ArkiveModLibraryManifest Manifest);

    private volatile LoadedManifest? _loaded;

    /// <summary>One parsed library is handed to every caller, so its lists are arrays no caller can add to.</summary>
    private static ArkiveModLibraryManifest Freeze(ArkiveModLibraryManifest manifest) => manifest with
    {
        Mods = (manifest.Mods ?? []).Select(mod => mod with { Bindings = (mod.Bindings ?? []).ToArray() }).ToArray(),
    };

    public async Task<ArkiveModLibraryManifest> UpdateAsync(
        Func<ArkiveModLibraryManifest, ArkiveModLibraryManifest> mutate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await using var libraryLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var mutated = mutate(current);
        if (ReferenceEquals(mutated, current)) return current;
        var updated = mutated with { SchemaVersion = CurrentSchemaVersion, UpdatedAt = DateTimeOffset.UtcNow };
        var duplicate = updated.Mods.GroupBy(mod => mod.ModId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            Refuse("LIB001", $"The library would contain mod {duplicate.Key} more than once.");
        var duplicateFile = updated.Mods.GroupBy(mod => mod.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateFile is not null)
            Refuse("LIB002", $"Two library mods would share the file {duplicateFile.Key}.");
        var sharedEdit = updated.Mods.SelectMany(mod => mod.Bindings.Select(binding => (mod, binding)))
            .GroupBy(item => item.binding.EditId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (sharedEdit is not null)
            Refuse("LIB003",
                $"Local edit {sharedEdit.Key} cannot belong to more than one mod asset " +
                $"({string.Join(", ", sharedEdit.Select(item => item.mod.Manifest.Name))}).");
        await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<ArkiveModLibraryScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModsDirectory);
        var library = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var registered = new List<string>();
        var refreshed = new List<string>();
        var released = new List<ArkiveModReleasedEdit>();
        var issues = new List<ArkiveModLibraryIssue>();
        var reads = new Dictionary<string, ArkiveModFileReadResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(
                     ModsDirectory, "*" + ArkiveModFileFormat.Extension, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            var existing = library.Mods.FirstOrDefault(mod =>
                mod.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            var info = new FileInfo(path);
            if (existing is not null && existing.FileBytes == info.Length &&
                existing.FileLastWriteTimeUtc.UtcDateTime == info.LastWriteTimeUtc) continue;
            if (_unreadable.TryGetValue(path, out var failed) &&
                failed.Length == info.Length && failed.WriteTime == info.LastWriteTimeUtc)
            {
                issues.Add(new ArkiveModLibraryIssue(path, failed.Message));
                continue;
            }
            try
            {
                reads[fileName] = await ArkiveModFileService.ReadAsync(path, true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is ArkiveModRefusalException or IOException or UnauthorizedAccessException)
            {
                _unreadable[path] = (info.Length, info.LastWriteTimeUtc, exception.Message);
                issues.Add(new ArkiveModLibraryIssue(path, exception.Message));
            }
        }
        if (reads.Count == 0)
            return new ArkiveModLibraryScanResult(library, [], [], issues, []);

        library = await UpdateAsync(current =>
        {
            var mods = current.Mods.ToList();
            foreach (var (fileName, read) in reads)
            {
                var byFile = mods.FindIndex(mod => mod.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
                var byId = mods.FindIndex(mod => mod.ModId.Equals(read.Manifest.ModId, StringComparison.OrdinalIgnoreCase));
                if (byFile >= 0 && !mods[byFile].ModId.Equals(read.Manifest.ModId, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ArkiveModLibraryIssue(read.Path,
                        $"The file now contains a different mod ({read.Manifest.Name}). Import it under a new name instead."));
                    continue;
                }
                if (byFile < 0 && byId >= 0)
                {
                    issues.Add(new ArkiveModLibraryIssue(read.Path,
                        $"{read.Manifest.Name} is already in the library as {mods[byId].FileName}; remove the duplicate file."));
                    continue;
                }
                if (byFile >= 0)
                {
                    mods[byFile] = Refresh(mods[byFile], read, out var dropped);
                    released.AddRange(dropped);
                    refreshed.Add(read.Manifest.ModId);
                }
                else
                {
                    mods.Add(NewEntry(read, ArkiveModOrigin.Imported, enabled: false, priority: 0, []));
                    registered.Add(read.Manifest.ModId);
                }
            }
            return registered.Count + refreshed.Count == 0 ? current : current with { Mods = mods };
        }, cancellationToken).ConfigureAwait(false);
        return new ArkiveModLibraryScanResult(library, registered, refreshed, issues, released);
    }

    public async Task<ArkiveModImportResult> ImportAsync(
        string arkivePath,
        bool replace = false,
        CancellationToken cancellationToken = default)
    {
        var read = await ArkiveModFileService.ReadAsync(arkivePath, true, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(ModsDirectory);
        var library = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var existing = library.Find(read.Manifest.ModId);
        if (existing is not null && !replace && !Path.GetFullPath(read.Path).Equals(
                GetModFilePath(existing), StringComparison.OrdinalIgnoreCase))
        {
            if (existing.Origin != ArkiveModOrigin.Imported)
                Refuse("LIB012", $"{existing.Manifest.Name} was made on this computer. Importing would replace your copy.");
            if (read.Manifest.UpdatedAt < existing.Manifest.UpdatedAt)
                Refuse("LIB012", $"The library already has a newer {existing.Manifest.Name} ({existing.Manifest.Version}).");
        }
        var sourceInMods = Path.GetDirectoryName(read.Path)!.TrimEnd(Path.DirectorySeparatorChar)
            .Equals(ModsDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        var fileName = existing?.FileName ??
                       (sourceInMods && library.Mods.All(mod => !mod.FileName.Equals(
                           Path.GetFileName(read.Path), StringComparison.OrdinalIgnoreCase))
                           ? Path.GetFileName(read.Path)
                           : UniqueFileName(SuggestFileName(read.Manifest.Name), library));
        var destination = RequireWithinMods(Path.Combine(ModsDirectory, fileName));
        if (!destination.Equals(read.Path, StringComparison.OrdinalIgnoreCase))
        {
            var temporary = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.Copy(read.Path, temporary, overwrite: false);
                var copiedHash = await ArkiveModFileService.HashFileAsync(temporary, cancellationToken)
                    .ConfigureAwait(false);
                if (!copiedHash.Equals(read.Sha256, StringComparison.OrdinalIgnoreCase))
                    Refuse("LIB010", "The imported copy did not match the validated .arkive file.");
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            var copied = new FileInfo(destination);
            read = read with
            {
                Path = destination,
                LastWriteTimeUtc = new DateTimeOffset(copied.LastWriteTimeUtc, TimeSpan.Zero),
            };
        }

        IReadOnlyList<ArkiveModReleasedEdit> released = [];
        ArkiveModLibraryEntry? result = null;
        library = await UpdateAsync(current =>
        {
            var mods = current.Mods.ToList();
            var index = mods.FindIndex(mod => mod.ModId.Equals(read.Manifest.ModId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                mods[index] = Refresh(mods[index] with { FileName = fileName }, read, out var dropped);
                released = dropped;
                result = mods[index];
            }
            else
            {
                result = NewEntry(read, ArkiveModOrigin.Imported, enabled: false, priority: 0, []);
                mods.Add(result);
            }
            return current with { Mods = mods };
        }, cancellationToken).ConfigureAwait(false);
        return new ArkiveModImportResult(result!, existing is not null, released, library);
    }

    public async Task<ArkiveModLibraryEntry> RegisterWrittenFileAsync(
        ArkiveModFileReadResult written,
        ArkiveModOrigin origin,
        bool enabled,
        int priority,
        IReadOnlyList<ArkiveModAssetBinding> bindings,
        string? authoredForPackageProjectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(written);
        var fileName = Path.GetFileName(RequireWithinMods(written.Path));
        ValidatePriority(priority);
        ArkiveModLibraryEntry? result = null;
        await UpdateAsync(current =>
        {
            var mods = current.Mods.ToList();
            var index = mods.FindIndex(mod => mod.ModId.Equals(written.Manifest.ModId, StringComparison.OrdinalIgnoreCase));
            var assetIds = written.Manifest.Assets.Select(asset => asset.AssetId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var retained = bindings.Where(binding => assetIds.Contains(binding.AssetId)).ToArray();
            if (index >= 0)
            {
                result = Describe(mods[index] with { FileName = fileName, Bindings = retained }, written) with
                {
                    AuthoredForPackageProjectId = authoredForPackageProjectId ?? mods[index].AuthoredForPackageProjectId,
                };
                mods[index] = result;
            }
            else
            {
                result = NewEntry(written, origin, enabled, priority, retained) with
                {
                    AuthoredForPackageProjectId = authoredForPackageProjectId,
                };
                mods.Add(result);
            }
            return current with { Mods = mods };
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    public Task<ArkiveModLibraryManifest> SetEnabledAsync(
        string modId, bool enabled, CancellationToken cancellationToken = default) =>
        UpdateModAsync(modId, mod => mod with { Enabled = enabled }, cancellationToken);

    /// <summary>Turns several mods on or off in one library update, as a mod pack's switch does.</summary>
    public Task<ArkiveModLibraryManifest> SetEnabledAsync(
        IReadOnlyCollection<string> modIds, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modIds);
        var ids = modIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return UpdateAsync(current =>
        {
            if (ids.FirstOrDefault(id => current.Find(id) is null) is { } missing)
                Refuse("LIB020", $"Mod {missing} is not in the library.");
            return current with
            {
                Mods = current.Mods.Select(mod => ids.Contains(mod.ModId) ? mod with { Enabled = enabled } : mod).ToArray(),
            };
        }, cancellationToken);
    }

    /// <summary>Moves several mods' priorities by the same amount, keeping their order among themselves.</summary>
    public Task<ArkiveModLibraryManifest> RaisePrioritiesAsync(
        IReadOnlyCollection<string> modIds, int raise, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modIds);
        var ids = modIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return UpdateAsync(current =>
        {
            var mods = current.Mods.Select(mod => ids.Contains(mod.ModId)
                ? mod with { Priority = mod.Priority + raise }
                : mod).ToArray();
            foreach (var mod in mods.Where(mod => ids.Contains(mod.ModId))) ValidatePriority(mod.Priority);
            return current with { Mods = mods };
        }, cancellationToken);
    }

    public Task<ArkiveModLibraryManifest> SetPriorityAsync(
        string modId, int priority, CancellationToken cancellationToken = default)
    {
        ValidatePriority(priority);
        return UpdateModAsync(modId, mod => mod with { Priority = priority }, cancellationToken);
    }

    public Task<ArkiveModLibraryManifest> SetGamePackagesDirectoryAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(directory);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"The Lost Ark package directory was not found: {full}");
        return UpdateAsync(current => current with { GamePackagesDirectory = full }, cancellationToken);
    }

    public async Task<string?> InferGamePackagesDirectoryAsync(
        IEnumerable<string?> knownPackagePathsOrFolders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knownPackagePathsOrFolders);
        var library = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (library.GamePackagesDirectory is { } existing && Directory.Exists(existing)) return existing;
        foreach (var candidate in knownPackagePathsOrFolders)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var full = Path.GetFullPath(candidate);
            var directory = File.Exists(full) ? Path.GetDirectoryName(full) : Directory.Exists(full) ? full : null;
            if (directory is null || !Directory.EnumerateFiles(directory, "*.upk").Any()) continue;
            await SetGamePackagesDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
            return directory;
        }
        return null;
    }

    public Task<ArkiveModLibraryManifest> UpdateBindingsAsync(
        string modId,
        Func<IReadOnlyList<ArkiveModAssetBinding>, IReadOnlyList<ArkiveModAssetBinding>> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return UpdateModAsync(modId, mod => mod with { Bindings = update(mod.Bindings) }, cancellationToken);
    }

    public Task<ArkiveModLibraryManifest> RebindEditAsync(
        string previousEditId,
        string currentEditId,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(current => current with
        {
            Mods = current.Mods.Select(mod => mod with
            {
                Bindings = mod.Bindings.Select(binding =>
                    binding.EditId.Equals(previousEditId, StringComparison.OrdinalIgnoreCase)
                        ? binding with { EditId = currentEditId }
                        : binding).ToArray(),
            }).ToArray(),
        }, cancellationToken);

    public async Task<ArkiveModLibraryManifest> RemoveAsync(
        string modId,
        bool deleteFile,
        CancellationToken cancellationToken = default)
    {
        string? path = null;
        var updated = await UpdateAsync(current =>
        {
            var mod = current.Find(modId) ??
                      throw new ArkiveModRefusalException("LIB020", $"Mod {modId} is not in the library.");
            path = GetModFilePath(mod);
            return current with
            {
                Mods = current.Mods.Where(item => !item.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
            };
        }, cancellationToken).ConfigureAwait(false);
        if (deleteFile && path is not null && File.Exists(path)) File.Delete(path);
        return updated;
    }

    public static string SuggestFileName(string modName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(modName.Select(character =>
                invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray())
            .Trim().Trim('.');
        if (safe.Length == 0) safe = "mod";
        if (safe.Length > 100) safe = safe[..100].TrimEnd();
        return safe + ArkiveModFileFormat.Extension;
    }

    public string UniqueFileName(string preferred, ArkiveModLibraryManifest library)
    {
        var stem = Path.GetFileNameWithoutExtension(preferred);
        for (var attempt = 1; attempt < 1000; attempt++)
        {
            var candidate = attempt == 1 ? stem + ArkiveModFileFormat.Extension : $"{stem} ({attempt}){ArkiveModFileFormat.Extension}";
            if (library.Mods.Any(mod => mod.FileName.Equals(candidate, StringComparison.OrdinalIgnoreCase)) ||
                File.Exists(Path.Combine(ModsDirectory, candidate))) continue;
            return candidate;
        }
        throw new IOException("Arkive could not choose a unique mod file name.");
    }

    internal static ArkiveModLibraryEntry Describe(ArkiveModLibraryEntry entry, ArkiveModFileReadResult read) =>
        entry with
        {
            FileSha256 = read.Sha256,
            FileBytes = read.Bytes,
            FileLastWriteTimeUtc = read.LastWriteTimeUtc,
            Manifest = read.Manifest,
        };

    private static ArkiveModLibraryEntry NewEntry(
        ArkiveModFileReadResult read,
        ArkiveModOrigin origin,
        bool enabled,
        int priority,
        IReadOnlyList<ArkiveModAssetBinding> bindings) => new(
        read.Manifest.ModId.ToLowerInvariant(), Path.GetFileName(read.Path), read.Sha256, read.Bytes,
        read.LastWriteTimeUtc, enabled, priority, origin, read.Manifest, bindings);

    private static ArkiveModLibraryEntry Refresh(
        ArkiveModLibraryEntry entry,
        ArkiveModFileReadResult read,
        out IReadOnlyList<ArkiveModReleasedEdit> released)
    {
        var assets = read.Manifest.Assets.ToDictionary(asset => asset.AssetId, StringComparer.OrdinalIgnoreCase);
        var retained = new List<ArkiveModAssetBinding>();
        var dropped = new List<ArkiveModReleasedEdit>();
        foreach (var binding in entry.Bindings)
        {
            if (assets.TryGetValue(binding.AssetId, out var asset) && asset.Kind == binding.Kind &&
                ArkiveModFileService.ComputeContentSha256(asset)
                    .Equals(binding.PackedContentSha256, StringComparison.OrdinalIgnoreCase) &&
                entry.Manifest.Assets.FirstOrDefault(previous => previous.AssetId.Equals(
                    binding.AssetId, StringComparison.OrdinalIgnoreCase)) is { } previousAsset &&
                ArkiveModTargetKey.From(previousAsset) == ArkiveModTargetKey.From(asset))
                retained.Add(binding);
            else dropped.Add(new ArkiveModReleasedEdit(binding.EditId, entry.Origin == ArkiveModOrigin.Imported));
        }
        released = dropped;
        return Describe(entry with { Bindings = retained }, read);
    }

    private Task<ArkiveModLibraryManifest> UpdateModAsync(
        string modId,
        Func<ArkiveModLibraryEntry, ArkiveModLibraryEntry> update,
        CancellationToken cancellationToken) =>
        UpdateAsync(current =>
        {
            if (current.Find(modId) is null)
                throw new ArkiveModRefusalException("LIB020", $"Mod {modId} is not in the library.");
            return current with
            {
                Mods = current.Mods.Select(mod =>
                    mod.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase) ? update(mod) : mod).ToArray(),
            };
        }, cancellationToken);

    private static void ValidatePriority(int priority)
    {
        if (priority is < MinimumPriority or > MaximumPriority)
            Refuse("LIB030", $"Mod priority must be between {MinimumPriority:N0} and {MaximumPriority:N0}.");
    }

    private string RequireWithinMods(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(ModsDirectory, full);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.Contains(Path.DirectorySeparatorChar))
            Refuse("LIB040", $"The mod file is outside Arkive's mods folder: {full}");
        return full;
    }

    private async Task SaveAsync(ArkiveModLibraryManifest manifest, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(StateDirectory);
        var temporary = ManifestPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, StateJsonOptions);
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            // A reader (another Arkive window, antivirus, a backup tool) can hold the library file for a moment.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporary, ManifestPath, overwrite: true);
                    break;
                }
                catch (Exception exception) when (attempt < 10 && exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(100 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
            _loaded = new LoadedManifest(SHA256.HashData(bytes), Freeze(manifest));
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(StateDirectory);
        var path = Path.Combine(StateDirectory, LockFileName);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.Asynchronous);
            }
            catch (IOException) when (attempt < 40)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                Refuse("LIB050", $"Another Arkive operation is updating the mod library: {exception.Message}");
                throw;
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new ArkiveModRefusalException(code, message);
}
