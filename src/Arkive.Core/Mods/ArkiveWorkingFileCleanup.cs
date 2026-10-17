using System.Text.Json;
using Arkive.Core.Meshes;

namespace Arkive.Core.Mods;

public sealed record ArkiveWorkingFileItem(string Description, string DirectoryPath, long Bytes);

public sealed record ArkiveWorkingFileCleanupPlan(IReadOnlyList<ArkiveWorkingFileItem> Items)
{
    public long Bytes => Items.Sum(item => item.Bytes);
}

/// <summary>
/// Finds and deletes mesh working files that nothing uses any more: edited meshes that no draft, mod, package build
/// or record names, and the game-mesh exports they (or the proportion editor's previews) were made from. Replaced
/// Simple upscale pieces and removed draft assets leave these behind, at a few MB per piece.
/// <para>
/// Anything named anywhere in Arkive's records (every JSON file in the data folder outside the caches and the two
/// mesh workspaces) is kept, as is anything changed within <c>minimumAge</c>, so pieces a running save has made but
/// not yet added to its draft are never touched. Callers must not run it while Arkive is creating working files.
/// </para>
/// </summary>
public sealed class ArkiveWorkingFileCleanup(
    string dataRoot, SkeletalMeshEditWorkspaceStore meshEdits, SkeletalMeshEditableWorkspaceStore meshExports)
{
    private readonly string _dataRoot = Path.GetFullPath(dataRoot);
    private readonly string _editRoot = Path.GetFullPath(meshEdits.RootDirectory);
    private readonly string _exportRoot = Path.GetFullPath(meshExports.RootDirectory);

    /// <param name="onlyEditIds">When given, only these edited meshes (and the exports they were made from) are considered.</param>
    public async Task<ArkiveWorkingFileCleanupPlan> PlanAsync(
        TimeSpan minimumAge, IReadOnlyCollection<string>? onlyEditIds = null, CancellationToken cancellationToken = default)
    {
        var records = await ReadRecordsAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var editSnapshot = await meshEdits.LoadAsync(cancellationToken).ConfigureAwait(false);
        var edits = editSnapshot.Entries;
        // An edited mesh whose record cannot be read may have been made from any export, so none is deleted.
        var exportsInUseUnknown = editSnapshot.Issues.Count > 0;
        var only = onlyEditIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = new List<ArkiveWorkingFileItem>();
        var keptExports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // An exported mesh changed since Arkive wrote it may be someone's work in progress (edited in Blender and saved
        // over the export, not yet added as an edit); it is kept, as is an export whose record cannot be read.
        var exportSnapshot = await meshExports.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var editable in exportSnapshot.Entries)
        {
            if (editable.GlbExists && editable.GlbLengthMatches &&
                File.GetLastWriteTimeUtc(editable.GlbPath) <= File.GetLastWriteTimeUtc(editable.ManifestPath).AddSeconds(5)) continue;
            if (ExportFolderOf(editable.ManifestPath) is { } changed) keptExports.Add(changed);
        }
        foreach (var issue in exportSnapshot.Issues)
            if (ExportFolderOf(issue.ManifestPath) is { } unreadable) keptExports.Add(unreadable);
        var droppedExports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var export = ExportFolderOf(edit.Manifest.SourceManifestPath);
            var unused = !edit.Manifest.IncludedInPackageBuild &&
                         (only is null || only.Contains(edit.Manifest.EditId)) &&
                         !records.Strings.Contains(edit.Manifest.EditId.ToLowerInvariant()) &&
                         !records.Folders.Contains(Path.GetFullPath(edit.DirectoryPath)) &&
                         now - edit.Manifest.CreatedAt > minimumAge &&
                         Untouched(edit.DirectoryPath, minimumAge);
            if (!unused)
            {
                if (export is not null) keptExports.Add(export);
                continue;
            }
            items.Add(new ArkiveWorkingFileItem($"Edited mesh {edit.Manifest.ObjectName}", edit.DirectoryPath, Size(edit.DirectoryPath)));
            if (export is not null) droppedExports.Add(export);
        }

        if (!exportsInUseUnknown && Directory.Exists(_exportRoot))
        {
            foreach (var folder in Directory.EnumerateDirectories(_exportRoot).SelectMany(Directory.EnumerateDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(folder);
                if (keptExports.Contains(path) || records.Folders.Contains(path) ||
                    (only is not null && !droppedExports.Contains(path)) || !Untouched(path, minimumAge)) continue;
                items.Add(new ArkiveWorkingFileItem($"Game mesh export {Path.GetFileName(path)}", path, Size(path)));
            }
        }
        return new ArkiveWorkingFileCleanupPlan(items);
    }

    /// <summary>
    /// Deletes what a fresh plan with the same settings still finds unused, so nothing that became used since
    /// <paramref name="plan"/> was shown is deleted. Returns the bytes freed.
    /// </summary>
    public async Task<long> DeleteAsync(
        ArkiveWorkingFileCleanupPlan plan, TimeSpan minimumAge, IReadOnlyCollection<string>? onlyEditIds = null,
        CancellationToken cancellationToken = default)
    {
        var confirmed = (await PlanAsync(minimumAge, onlyEditIds, cancellationToken).ConfigureAwait(false)).Items
            .Select(item => item.DirectoryPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var freed = 0L;
        foreach (var item in plan.Items.Where(item => confirmed.Contains(item.DirectoryPath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = IsWithin(_editRoot, item.DirectoryPath) ? _editRoot
                : IsWithin(_exportRoot, item.DirectoryPath) ? _exportRoot
                : throw new InvalidOperationException("A working-file folder lies outside Arkive's mesh workspaces.");
            // Both workspaces keep each item exactly two folders below their root.
            if (!string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(item.DirectoryPath)), root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A working-file folder is not where Arkive keeps them.");
            try
            {
                Directory.Delete(item.DirectoryPath, recursive: true);
                freed += item.Bytes;
                var parent = Path.GetDirectoryName(item.DirectoryPath)!;
                if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return freed;
    }

    private sealed record Records(HashSet<string> Strings, HashSet<string> Folders);

    /// <summary>Every string in Arkive's records, and the working-file folders those strings point into.</summary>
    private async Task<Records> ReadRecordsAsync(CancellationToken cancellationToken)
    {
        var strings = new HashSet<string>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] skipped =
        [
            _editRoot, _exportRoot, Path.Combine(_dataRoot, "cache"), Path.Combine(_dataRoot, "logs"),
            Path.Combine(_dataRoot, "updates"), Path.Combine(_dataRoot, "staging", "lpk"),
        ];
        var files = Directory.Exists(_dataRoot)
            ? Directory.EnumerateFiles(_dataRoot, "*.json", SearchOption.AllDirectories)
                .Where(file => !skipped.Any(root => IsWithin(root, file)))
            : [];
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Unreadable records stop the cleanup: a missed reference could delete something still in use.
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var values = new List<string>();
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
                Collect(document.RootElement, values);
            }
            catch (JsonException)
            {
                // A damaged record still counts: every id and path written in it is kept.
                values.Add(text.Replace(@"\\", @"\"));
                values.AddRange(text.Split('"', ',', '\r', '\n', ' ', '\t'));
            }
            foreach (var value in values)
            {
                strings.Add(value.ToLowerInvariant());
                AddFolder(value, folders);
            }
        }
        return new Records(strings, folders);
    }

    private void AddFolder(string value, HashSet<string> folders)
    {
        foreach (var root in (string[])[_editRoot, _exportRoot])
        {
            var at = value.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var rest = value[(at + root.Length)..].TrimStart('\\', '/').Split('\\', '/');
            if (rest.Length >= 2 && rest[0].Length > 0 && rest[1].Length > 0)
                folders.Add(Path.Combine(root, rest[0], rest[1].Split('"', '\'', ' ', ',')[0]));
        }
    }

    private static void Collect(JsonElement element, List<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    values.Add(property.Name);
                    Collect(property.Value, values);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Collect(item, values);
                break;
            case JsonValueKind.String:
                values.Add(element.GetString() ?? string.Empty);
                break;
        }
    }

    /// <summary>The game-mesh export folder (two levels below the export root) an editable-mesh manifest lies in.</summary>
    private string? ExportFolderOf(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !IsWithin(_exportRoot, manifestPath)) return null;
        var parts = Path.GetRelativePath(_exportRoot, Path.GetFullPath(manifestPath)).Split(Path.DirectorySeparatorChar);
        return parts.Length >= 3 ? Path.Combine(_exportRoot, parts[0], parts[1]) : null;
    }

    private static bool Untouched(string directory, TimeSpan minimumAge)
    {
        var info = new DirectoryInfo(directory);
        var newest = info.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .Select(entry => entry.LastWriteTimeUtc).Append(info.LastWriteTimeUtc).Max();
        return DateTime.UtcNow - newest > minimumAge;
    }

    private static bool IsWithin(string root, string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static long Size(string directory) =>
        new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
}
