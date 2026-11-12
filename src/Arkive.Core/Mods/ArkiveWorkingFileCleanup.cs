using System.Text;
using System.Text.Json;
using Arkive.Core.Meshes;

namespace Arkive.Core.Mods;

/// <summary>The kinds of working file a clean-up looks at, each kept in its own folder under staging.</summary>
public enum ArkiveWorkingFileKind
{
    EditedMesh,
    MeshExport,
    TextureProject,
    EffectEdit,
    MaterialEdit,
    PhysicsEdit,
    InterfaceMovieEdit,
    StaticMeshEdit,
}

public sealed record ArkiveWorkingFileItem(string Description, string DirectoryPath, long Bytes)
{
    public ArkiveWorkingFileKind Kind { get; init; } = ArkiveWorkingFileKind.EditedMesh;

    /// <summary>Why nothing needs it, in words for the clean-up report.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Other folders that belong to this working file alone and go with it (a texture project's own builds).</summary>
    public IReadOnlyList<string> OwnedDirectories { get; init; } = [];
}

/// <summary>A working file the clean-up looked at and kept, and why.</summary>
public sealed record ArkiveWorkingFileKeptItem(
    ArkiveWorkingFileKind Kind, string Description, string DirectoryPath, string Reason);

public sealed record ArkiveWorkingFileCleanupPlan(IReadOnlyList<ArkiveWorkingFileItem> Items)
{
    public long Bytes => Items.Sum(item => item.Bytes);

    public IReadOnlyList<ArkiveWorkingFileKeptItem> Kept { get; init; } = [];
}

/// <summary>What a clean-up deleted, and the planned items it left because they were no longer unused.</summary>
public sealed record ArkiveWorkingFileRemoval(
    IReadOnlyList<ArkiveWorkingFileItem> Removed,
    IReadOnlyList<ArkiveWorkingFileKeptItem> NotRemoved)
{
    public long Bytes => Removed.Sum(item => item.Bytes);
}

/// <summary>
/// Finds and deletes working files that nothing uses any more: edited meshes and the game-mesh exports they (or the
/// proportion editor's previews) were made from, and, when built with every workspace, texture projects and effect,
/// material, physics, interface movie and static mesh edits. Replaced Simple upscale pieces, removed draft assets and
/// mods deleted before 0.7.6 leave these behind.
/// <para>
/// A working file is kept when it is selected for a package build, changed within <c>minimumAge</c>, or named by any
/// record in the data folder (by its id or by a path into its folder): mod bindings, drafts, package builds, other
/// working files that are themselves kept. Records inside an unused working file's own folder do not keep anything,
/// so a chain of unused files goes in one clean-up. Callers must not run it while Arkive is creating working files.
/// </para>
/// </summary>
public sealed class ArkiveWorkingFileCleanup
{
    private const int IdLength = 32;
    private readonly string _dataRoot;
    private readonly SkeletalMeshEditWorkspaceStore _meshEdits;
    private readonly SkeletalMeshEditableWorkspaceStore _meshExports;
    private readonly ArkiveModEditWorkspaces? _edits;
    private readonly string _editRoot;
    private readonly string _exportRoot;

    /// <summary>A clean-up of edited meshes and game-mesh exports only.</summary>
    public ArkiveWorkingFileCleanup(
        string dataRoot, SkeletalMeshEditWorkspaceStore meshEdits, SkeletalMeshEditableWorkspaceStore meshExports)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _meshEdits = meshEdits;
        _meshExports = meshExports;
        _editRoot = Path.GetFullPath(meshEdits.RootDirectory);
        _exportRoot = Path.GetFullPath(meshExports.RootDirectory);
    }

    /// <summary>A clean-up of every kind of working file in <paramref name="edits"/>.</summary>
    public ArkiveWorkingFileCleanup(
        string dataRoot, ArkiveModEditWorkspaces edits, SkeletalMeshEditableWorkspaceStore meshExports)
        : this(dataRoot, edits.SkeletalMeshEdits, meshExports) => _edits = edits;

    /// <param name="onlyEditIds">When given, only these edited meshes (and the exports they were made from) are considered.</param>
    /// <param name="removedBuilds">
    /// Package builds a clean-up is about to remove: their records are read as if already gone, so what only they
    /// name is planned too.
    /// </param>
    public async Task<ArkiveWorkingFileCleanupPlan> PlanAsync(
        TimeSpan minimumAge, IReadOnlyCollection<string>? onlyEditIds = null,
        IReadOnlyCollection<ArkiveBuildCleanupItem>? removedBuilds = null, CancellationToken cancellationToken = default)
    {
        var (candidates, _) = await ResolveAsync(minimumAge, onlyEditIds, removedBuilds, cancellationToken).ConfigureAwait(false);
        return ToPlan(candidates);
    }

    /// <summary>
    /// Deletes what a fresh plan with the same settings still finds unused, so nothing that became used since
    /// <paramref name="plan"/> was shown is deleted. Returns the bytes freed.
    /// </summary>
    public async Task<long> DeleteAsync(
        ArkiveWorkingFileCleanupPlan plan, TimeSpan minimumAge, IReadOnlyCollection<string>? onlyEditIds = null,
        CancellationToken cancellationToken = default) =>
        (await RemoveAsync(plan, minimumAge, onlyEditIds, cancellationToken).ConfigureAwait(false)).Bytes;

    /// <summary>
    /// <see cref="DeleteAsync"/>, saying what was deleted. Each item is checked once more just before it is deleted:
    /// it must still be unused in a plan made now, still be where Arkive keeps it, and still be unchanged.
    /// </summary>
    public async Task<ArkiveWorkingFileRemoval> RemoveAsync(
        ArkiveWorkingFileCleanupPlan plan, TimeSpan minimumAge, IReadOnlyCollection<string>? onlyEditIds = null,
        CancellationToken cancellationToken = default)
    {
        var (candidates, _) = await ResolveAsync(minimumAge, onlyEditIds, null, cancellationToken).ConfigureAwait(false);
        var fresh = candidates.Where(candidate => candidate.Removable)
            .ToDictionary(candidate => candidate.Folder, StringComparer.OrdinalIgnoreCase);
        var current = candidates.ToDictionary(candidate => candidate.Folder, StringComparer.OrdinalIgnoreCase);
        var removed = new List<ArkiveWorkingFileItem>();
        var notRemoved = new List<ArkiveWorkingFileKeptItem>();
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = Path.GetFullPath(item.DirectoryPath);
            if (!fresh.TryGetValue(folder, out var candidate))
            {
                notRemoved.Add(new ArkiveWorkingFileKeptItem(item.Kind, item.Description, item.DirectoryPath,
                    current.TryGetValue(folder, out var kept) ? kept.KeepReason ?? "in use now" : "no longer there"));
                continue;
            }
            if (!Directory.Exists(folder) || !Untouched(candidate.Folders, minimumAge))
            {
                notRemoved.Add(new ArkiveWorkingFileKeptItem(item.Kind, item.Description, item.DirectoryPath,
                    "changed since the plan was made"));
                continue;
            }
            try
            {
                await DeleteCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
                removed.Add(item);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                notRemoved.Add(new ArkiveWorkingFileKeptItem(item.Kind, item.Description, item.DirectoryPath,
                    "could not be deleted: " + exception.Message));
            }
        }
        return new ArkiveWorkingFileRemoval(removed, notRemoved);
    }

    private async Task DeleteCandidateAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        var root = candidate.Root;
        foreach (var folder in candidate.Folders)
        {
            // Every folder lies inside its workspace, below the workspace root itself.
            if (!IsWithin(root, folder))
                throw new InvalidOperationException("A working-file folder lies outside Arkive's workspaces.");
        }
        if (candidate.Kind == ArkiveWorkingFileKind.MeshExport &&
            !string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(candidate.Folder)), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A working-file folder is not where Arkive keeps them.");
        if (candidate.Entry is GuidedTextureModProjectEntry { Manifest.Revisions.Count: > 0 } texture && _edits is not null)
        {
            // The texture workspace also notes the project's imported builds, so they are not imported again.
            await _edits.Textures.DeleteAsync(texture, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            foreach (var folder in candidate.Folders.Where(Directory.Exists))
                Directory.Delete(folder, recursive: true);
        }
        foreach (var folder in candidate.Folders)
        {
            var parent = Path.GetDirectoryName(folder)!;
            if (IsWithin(root, parent) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
    }

    private static ArkiveWorkingFileCleanupPlan ToPlan(IReadOnlyList<Candidate> candidates)
    {
        var items = candidates.Where(candidate => candidate.Removable)
            .OrderBy(candidate => candidate.Kind).ThenBy(candidate => candidate.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new ArkiveWorkingFileItem(candidate.Description, candidate.Folder,
                candidate.Folders.Where(Directory.Exists).Sum(Size))
            {
                Kind = candidate.Kind,
                Reason = candidate.Kind == ArkiveWorkingFileKind.MeshExport
                    ? "no kept edited mesh was made from it and no record names it"
                    : "not selected for a package build, and no mod, draft, kept build or other record names it",
                OwnedDirectories = candidate.Folders.Skip(1).ToArray(),
            })
            .ToArray();
        var kept = candidates.Where(candidate => !candidate.Removable)
            .OrderBy(candidate => candidate.Kind).ThenBy(candidate => candidate.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new ArkiveWorkingFileKeptItem(candidate.Kind, candidate.Description, candidate.Folder,
                candidate.KeepReason!))
            .ToArray();
        return new ArkiveWorkingFileCleanupPlan(items) { Kept = kept };
    }

    private sealed class Candidate(ArkiveWorkingFileKind kind, string root, string folder, string description)
    {
        public ArkiveWorkingFileKind Kind { get; } = kind;
        public string Root { get; } = Path.GetFullPath(root);
        public string Folder { get; } = Path.GetFullPath(folder);
        public string Description { get; } = description;
        public List<string> Folders { get; } = [Path.GetFullPath(folder)];
        public string? Id { get; init; }
        public object? Entry { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }

        /// <summary>Why it is kept; null while nothing found keeps it.</summary>
        public string? KeepReason { get; set; }

        /// <summary>The working files this one's records name, kept with it when it is kept.</summary>
        public HashSet<Candidate> Names { get; } = [];

        public bool Removable => KeepReason is null;
    }

    private async Task<(IReadOnlyList<Candidate> Candidates, int Records)> ResolveAsync(
        TimeSpan minimumAge, IReadOnlyCollection<string>? onlyEditIds,
        IReadOnlyCollection<ArkiveBuildCleanupItem>? removedBuilds, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var only = onlyEditIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Candidate>();
        var age = minimumAge.TotalHours >= 1 ? $"{minimumAge.TotalHours:0.#} hour(s)" : $"{minimumAge.TotalMinutes:0.#} minute(s)";
        var recent = $"made or changed within the last {age}";

        // Edited meshes, and the exports they were made from.
        var editSnapshot = await _meshEdits.LoadAsync(cancellationToken).ConfigureAwait(false);
        // An edited mesh whose record cannot be read may have been made from any export, so none is deleted.
        var exportsInUseUnknown = editSnapshot.Issues.Count > 0;
        var exportOf = new Dictionary<Candidate, string>();
        var onlyExports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in editSnapshot.Entries)
        {
            var candidate = new Candidate(ArkiveWorkingFileKind.EditedMesh, _editRoot, edit.DirectoryPath,
                $"Edited mesh {edit.Manifest.ObjectName}")
            {
                Id = edit.Manifest.EditId, CreatedAt = edit.Manifest.CreatedAt, Entry = edit,
            };
            if (edit.Manifest.IncludedInPackageBuild) candidate.KeepReason = "selected for a package build";
            else if (only is not null && !only.Contains(edit.Manifest.EditId)) candidate.KeepReason = "not part of this clean-up";
            candidates.Add(candidate);
            if (ExportFolderOf(edit.Manifest.SourceManifestPath) is { } export)
            {
                exportOf[candidate] = export;
                if (only?.Contains(edit.Manifest.EditId) == true) onlyExports.Add(export);
            }
        }

        // An exported mesh changed since Arkive wrote it may be someone's work in progress (edited in Blender and saved
        // over the export, not yet added as an edit); it is kept, as is an export whose record cannot be read.
        var changedExports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exportSnapshot = await _meshExports.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var editable in exportSnapshot.Entries)
        {
            if (editable.GlbExists && editable.GlbLengthMatches &&
                File.GetLastWriteTimeUtc(editable.GlbPath) <= File.GetLastWriteTimeUtc(editable.ManifestPath).AddSeconds(5)) continue;
            if (ExportFolderOf(editable.ManifestPath) is { } changed)
                changedExports.TryAdd(changed, "its exported mesh was changed after Arkive exported it");
        }
        foreach (var issue in exportSnapshot.Issues)
            if (ExportFolderOf(issue.ManifestPath) is { } unreadable) changedExports[unreadable] = "its record cannot be read";
        var exportCandidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_exportRoot))
        {
            foreach (var folder in Directory.EnumerateDirectories(_exportRoot).SelectMany(Directory.EnumerateDirectories))
            {
                var candidate = new Candidate(ArkiveWorkingFileKind.MeshExport, _exportRoot, folder,
                    $"Game mesh export {Path.GetFileName(folder)}");
                if (exportsInUseUnknown) candidate.KeepReason = "an edited mesh record cannot be read";
                else if (changedExports.TryGetValue(candidate.Folder, out var reason)) candidate.KeepReason = reason;
                else if (only is not null && !onlyExports.Contains(candidate.Folder)) candidate.KeepReason = "not part of this clean-up";
                candidates.Add(candidate);
                exportCandidates[candidate.Folder] = candidate;
            }
        }
        foreach (var (edit, export) in exportOf)
            if (exportCandidates.TryGetValue(export, out var exportCandidate)) edit.Names.Add(exportCandidate);

        if (_edits is not null)
        {
            foreach (var texture in await _edits.Textures.LoadAsync(cancellationToken).ConfigureAwait(false))
            {
                var candidate = new Candidate(ArkiveWorkingFileKind.TextureProject, _edits.Textures.RootDirectory,
                    texture.DirectoryPath, $"Texture project {texture.Manifest.ObjectName}")
                {
                    Id = texture.Manifest.ProjectId, CreatedAt = texture.Manifest.CreatedAt, Entry = texture,
                };
                try
                {
                    candidate.Folders.AddRange(_edits.Textures.OwnedRevisionDirectories(texture));
                }
                catch (IOException)
                {
                    candidate.KeepReason = "its builds are not where Arkive keeps them";
                }
                if (texture.Manifest.IncludedInPackageBuild is not false) candidate.KeepReason ??= "selected for a package build";
                // Someone's work in progress: the editable image was saved after Arkive last wrote the project.
                else if (File.Exists(texture.Manifest.EditableImagePath) &&
                         File.GetLastWriteTimeUtc(texture.Manifest.EditableImagePath) >
                         File.GetLastWriteTimeUtc(texture.ManifestPath).AddSeconds(5))
                    candidate.KeepReason ??= "its editable image was changed after Arkive last saved the project";
                candidates.Add(candidate);
            }
            foreach (var material in await _edits.MaterialEdits.LoadAsync(cancellationToken).ConfigureAwait(false))
                candidates.Add(Edit(ArkiveWorkingFileKind.MaterialEdit, _edits.MaterialEdits.RootDirectory,
                    material.DirectoryPath, $"Material edit {material.Document.ObjectName}", material.Document.EditId,
                    material.Document.CreatedAt, material.Document.IncludedInPackageBuild, material));
            foreach (var particle in await _edits.ParticleEdits.LoadAsync(cancellationToken).ConfigureAwait(false))
                candidates.Add(Edit(ArkiveWorkingFileKind.EffectEdit, _edits.ParticleEdits.RootDirectory,
                    particle.DirectoryPath, $"Effect edit {particle.Document.SystemObjectName}", particle.Document.EditId,
                    particle.Document.CreatedAt, particle.Document.IncludedInPackageBuild, particle));
            foreach (var physics in await _edits.PhysicsEdits.LoadAsync(cancellationToken).ConfigureAwait(false))
                candidates.Add(Edit(ArkiveWorkingFileKind.PhysicsEdit, _edits.PhysicsEdits.RootDirectory,
                    physics.DirectoryPath, $"Physics edit {physics.Document.AssetObjectName}", physics.Document.EditId,
                    physics.Document.CreatedAt, physics.Document.IncludedInPackageBuild, physics));
            foreach (var mesh in await _edits.StaticMeshEdits.LoadAsync(cancellationToken).ConfigureAwait(false))
                candidates.Add(Edit(ArkiveWorkingFileKind.StaticMeshEdit, _edits.StaticMeshEdits.RootDirectory,
                    mesh.DirectoryPath, $"Static mesh edit {mesh.Manifest.DisplayName}", mesh.Manifest.EditId,
                    mesh.Manifest.CreatedAt, mesh.Manifest.IncludedInPackageBuild, mesh));
            if (_edits.InterfaceMovies is { } movies)
                foreach (var movie in await movies.LoadAsync(cancellationToken).ConfigureAwait(false))
                    candidates.Add(Edit(ArkiveWorkingFileKind.InterfaceMovieEdit, movies.RootDirectory,
                        Path.GetDirectoryName(movie.ManifestPath)!, $"Interface movie edit {movie.Manifest.ObjectName}",
                        movie.Manifest.EditId, movie.Manifest.CreatedAt, movie.Manifest.IncludedInPackageBuild, movie));
        }

        // Each working file must sit alone in its own folder inside its workspace; anything else is left alone.
        var byFolder = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!IsWithin(candidate.Root, candidate.Folder)) candidate.KeepReason ??= "not where Arkive keeps working files";
            foreach (var folder in candidate.Folders)
            {
                if (byFolder.TryGetValue(folder, out var other))
                {
                    candidate.KeepReason ??= "shares its folder with another working file";
                    other.KeepReason ??= "shares its folder with another working file";
                }
                else byFolder[folder] = candidate;
            }
        }
        foreach (var (folder, candidate) in byFolder)
        {
            for (var parent = Path.GetDirectoryName(folder); parent is not null && IsWithin(_dataRoot, parent);
                 parent = Path.GetDirectoryName(parent))
            {
                if (!byFolder.TryGetValue(parent, out var outer) || ReferenceEquals(outer, candidate)) continue;
                candidate.KeepReason ??= "lies inside another working file's folder";
                outer.KeepReason ??= "holds another working file's folder";
            }
        }

        foreach (var candidate in candidates.Where(candidate => candidate.Removable))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.CreatedAt is { } created && now - created <= minimumAge ||
                !Untouched(candidate.Folders, minimumAge))
                candidate.KeepReason = recent;
        }

        var records = await ReadRecordsAsync(candidates, byFolder, removedBuilds, cancellationToken).ConfigureAwait(false);

        // Everything kept keeps what its own records name, and so on down the chain.
        var queue = new Queue<Candidate>(candidates.Where(candidate => !candidate.Removable));
        while (queue.TryDequeue(out var kept))
        {
            foreach (var named in kept.Names)
            {
                if (!named.Removable) continue;
                named.KeepReason = $"used by {kept.Description}, which is kept";
                queue.Enqueue(named);
            }
        }
        return (candidates, records);

        Candidate Edit(ArkiveWorkingFileKind kind, string root, string folder, string description, string id,
            DateTimeOffset createdAt, bool included, object entry)
        {
            var candidate = new Candidate(kind, root, folder, description) { Id = id, CreatedAt = createdAt, Entry = entry };
            if (included) candidate.KeepReason = "selected for a package build";
            return candidate;
        }
    }

    /// <summary>
    /// Reads every JSON record in the data folder outside the caches, logs and game-mesh exports. A record inside a
    /// working file's folder adds to what that working file names; any other record keeps whatever it names.
    /// Returns the number of records read.
    /// </summary>
    private async Task<int> ReadRecordsAsync(
        IReadOnlyList<Candidate> candidates, IReadOnlyDictionary<string, Candidate> byFolder,
        IReadOnlyCollection<ArkiveBuildCleanupItem>? removedBuilds, CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Where(candidate => candidate.Id is not null))
            byId.TryAdd(candidate.Id!, candidate);
        // Paths are matched by the part from the workspace folder on, so a record written under an older location of
        // the data folder still keeps what it names.
        var roots = candidates.Select(candidate => candidate.Root).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => (Root: root, Key: Key(root)))
            .ToArray();
        string[] skipped =
        [
            _exportRoot, Path.Combine(_dataRoot, "cache"), Path.Combine(_dataRoot, "logs"),
            Path.Combine(_dataRoot, "updates"), Path.Combine(_dataRoot, "staging", "lpk"),
        ];
        var materialCandidates = Path.Combine(_dataRoot, "staging", "material-candidates");
        var removedFolders = (removedBuilds ?? []).Select(build => Path.GetFullPath(build.DirectoryPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedRevisions = (removedBuilds ?? [])
            .GroupBy(build => Path.GetFullPath(build.PackageManifestPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(build => build.RevisionId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var files = Directory.Exists(_dataRoot)
            ? Directory.EnumerateFiles(_dataRoot, "*.json", SearchOption.AllDirectories)
                .Where(file => !skipped.Any(root => IsWithin(root, file)) && !InAny(file, removedFolders))
            : [];
        var count = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            var owner = OwnerOf(file, byFolder);
            // Unreadable records stop the cleanup: a missed reference could delete something still in use.
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            var values = new List<string>();
            if (removedRevisions.TryGetValue(Path.GetFullPath(file), out var revisions))
                CollectPackageProject(bytes, revisions, values);
            else
                Collect(bytes, values);
            var names = values.SelectMany(value => Resolve(value, byId, roots, byFolder))
                .Where(named => !ReferenceEquals(named, owner)).ToHashSet();
            // A material candidate is built from one material edit and names it; that is the edit's own output, not a
            // use of it, so it keeps the edit only while the edit is kept for some other reason.
            if (owner is null && IsWithin(materialCandidates, file) &&
                names.Count(named => named.Kind == ArkiveWorkingFileKind.MaterialEdit) == 1)
            {
                owner = names.Single(named => named.Kind == ArkiveWorkingFileKind.MaterialEdit);
                names.Remove(owner);
            }
            foreach (var named in names)
            {
                if (owner is not null) owner.Names.Add(named);
                else named.KeepReason ??= "named by " + Path.GetRelativePath(_dataRoot, file);
            }
        }
        return count;
    }

    private bool InAny(string file, IReadOnlySet<string> folders)
    {
        if (folders.Count == 0) return false;
        for (var folder = Path.GetDirectoryName(Path.GetFullPath(file)); folder is not null && IsWithin(_dataRoot, folder);
             folder = Path.GetDirectoryName(folder))
            if (folders.Contains(folder)) return true;
        return false;
    }

    private string Key(string root)
    {
        var relative = Path.GetRelativePath(_dataRoot, root);
        return Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal)
            ? root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar
            : Path.DirectorySeparatorChar + relative.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    private Candidate? OwnerOf(string file, IReadOnlyDictionary<string, Candidate> byFolder)
    {
        for (var folder = Path.GetDirectoryName(Path.GetFullPath(file)); folder is not null && IsWithin(_dataRoot, folder);
             folder = Path.GetDirectoryName(folder))
            if (byFolder.TryGetValue(folder, out var owner)) return owner;
        return null;
    }

    /// <summary>The working files a record value names: by an id anywhere in it, or by a path into a working file's folder.</summary>
    private static IEnumerable<Candidate> Resolve(
        string value, IReadOnlyDictionary<string, Candidate> byId, IReadOnlyList<(string Root, string Key)> roots,
        IReadOnlyDictionary<string, Candidate> byFolder)
    {
        if (value.Length == 0) yield break;
        if (byId.TryGetValue(value.Trim(), out var exact)) yield return exact;
        for (var start = 0; start + IdLength <= value.Length;)
        {
            if (!Uri.IsHexDigit(value[start])) { start++; continue; }
            var end = start;
            while (end < value.Length && Uri.IsHexDigit(value[end])) end++;
            if (end - start == IdLength && byId.TryGetValue(value[start..end], out var named)) yield return named;
            start = end;
        }
        if (value.IndexOfAny(['\\', '/']) < 0) yield break;
        var path = value.Replace('/', '\\');
        foreach (var (root, key) in roots)
        {
            for (var at = path.IndexOf(key, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = path.IndexOf(key, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                var segments = path[(at + key.Length)..].Split('\\');
                var folder = root;
                for (var index = 0; index < Math.Min(4, segments.Length); index++)
                {
                    var segment = segments[index].Split('"', '\'', ',', '\r', '\n', '\t')[0];
                    if (segment.Length == 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) break;
                    folder = Path.Combine(folder, segment);
                    if (byFolder.TryGetValue(folder, out var named)) { yield return named; break; }
                }
            }
        }
    }

    /// <summary>Every property name and string in a record; for a damaged record, every token in its text.</summary>
    private static void Collect(byte[] bytes, List<string> values)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
            });
            while (reader.Read())
                if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String)
                    values.Add(reader.GetString() ?? string.Empty);
        }
        catch (JsonException)
        {
            // A damaged record still counts: every id and path written in it is kept.
            var text = Encoding.UTF8.GetString(bytes);
            values.Add(text.Replace(@"\\", @"\"));
            values.AddRange(text.Replace(@"\\", @"\").Split('"', ',', '\r', '\n', ' ', '\t'));
        }
    }

    /// <summary>A package project's record, without the builds a clean-up is about to remove from it.</summary>
    private static void CollectPackageProject(byte[] bytes, IReadOnlySet<string> removedRevisions, List<string> values)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = true });
            CollectElement(document.RootElement, values, removedRevisions);
        }
        catch (JsonException)
        {
            Collect(bytes, values);
        }
    }

    private static void CollectElement(JsonElement element, List<string> values, IReadOnlySet<string>? removedRevisions = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    values.Add(property.Name);
                    if (removedRevisions is not null && property.Value.ValueKind == JsonValueKind.Array &&
                        property.Name.Equals("revisions", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var revision in property.Value.EnumerateArray())
                        {
                            if (revision.ValueKind == JsonValueKind.Object &&
                                revision.EnumerateObject().FirstOrDefault(item =>
                                    item.Name.Equals("revisionId", StringComparison.OrdinalIgnoreCase)).Value is
                                    { ValueKind: JsonValueKind.String } id &&
                                removedRevisions.Contains(id.GetString()!)) continue;
                            CollectElement(revision, values);
                        }
                        continue;
                    }
                    CollectElement(property.Value, values);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectElement(item, values);
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

    private static bool Untouched(IEnumerable<string> directories, TimeSpan minimumAge) =>
        directories.Where(Directory.Exists).All(directory => Untouched(directory, minimumAge));

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
