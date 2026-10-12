using System.Text.Json;

namespace Arkive.Core.Mods;

public sealed record ArkiveModAuthoringDraft(
    int SchemaVersion,
    string DraftId,
    string? ModId,
    ArkiveModMetadata Metadata,
    IReadOnlyList<string> EditIds,
    DateTimeOffset UpdatedAt)
{
    public IReadOnlyList<string> RemovedAssetIds { get; init; } = [];
    public IReadOnlyList<string> ReferenceEditIds { get; init; } = [];
    public IReadOnlyDictionary<string, string> SavedBindings { get; init; } = new Dictionary<string, string>();

    public ArkiveModAuthoringDraft RemoveAssets(IEnumerable<string> editIds, IEnumerable<string> assetIds)
    {
        var edits = editIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assets = assetIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in SavedBindings.Where(binding => edits.Contains(binding.Value)))
            assets.Add(binding.Key);
        foreach (var binding in SavedBindings.Where(binding => assets.Contains(binding.Key)))
            edits.Add(binding.Value);
        return this with
        {
            EditIds = EditIds.Where(id => !edits.Contains(id)).ToArray(),
            ReferenceEditIds = ReferenceEditIds.Where(id => !edits.Contains(id)).ToArray(),
            RemovedAssetIds = RemovedAssetIds.Concat(assets).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }
}

public sealed class ArkiveModAuthoringWorkspace
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ArkiveModAuthoringWorkspace(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }
    public IReadOnlyList<string> LoadIssues { get; private set; } = [];

    public async Task<IReadOnlyList<ArkiveModAuthoringDraft>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var drafts = new List<ArkiveModAuthoringDraft>();
        var issues = new List<string>();
        foreach (var path in Directory.EnumerateFiles(RootDirectory, "*.json"))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var draft = await JsonSerializer.DeserializeAsync<ArkiveModAuthoringDraft>(
                    stream, ArkiveModFileService.JsonOptions, cancellationToken).ConfigureAwait(false);
                if (draft is null || draft.SchemaVersion != 1 ||
                    !Guid.TryParseExact(draft.DraftId, "N", out _) || draft.EditIds is null ||
                    draft.Metadata is null || draft.ReferenceEditIds is null || draft.RemovedAssetIds is null ||
                    draft.SavedBindings is null || draft.Metadata.Name is null || draft.Metadata.Version is null ||
                    !Path.GetFileNameWithoutExtension(path).Equals(draft.DraftId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The authoring draft is invalid: {Path.GetFileName(path)}");
                drafts.Add(draft);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            { issues.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
        }
        LoadIssues = issues;
        return drafts.OrderByDescending(draft => draft.UpdatedAt).ToArray();
    }

    public Task<ArkiveModAuthoringDraft> CreateAsync(
        ArkiveModMetadata metadata, CancellationToken cancellationToken = default) =>
        SaveAsync(new ArkiveModAuthoringDraft(1, Guid.NewGuid().ToString("N"), null, metadata, [],
            DateTimeOffset.UtcNow), cancellationToken);

    public async Task<ArkiveModAuthoringDraft> BeginEditAsync(
        ArkiveModLibraryEntry mod, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        var existing = (await LoadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(draft =>
            mod.ModId.Equals(draft.ModId, StringComparison.OrdinalIgnoreCase));
        return existing ?? await SaveAsync(new ArkiveModAuthoringDraft(
            1, Guid.NewGuid().ToString("N"), mod.ModId,
            new ArkiveModMetadata(mod.Manifest.Name, mod.Manifest.Version, mod.Manifest.Author,
                mod.Manifest.Description, mod.Manifest.Website, mod.Manifest.Tags),
            mod.Bindings.Select(binding => binding.EditId).ToArray(), DateTimeOffset.UtcNow)
        { SavedBindings = mod.Bindings.ToDictionary(binding => binding.AssetId, binding => binding.EditId) },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArkiveModAuthoringDraft> ReconcileAsync(
        ArkiveModAuthoringDraft draft, ArkiveModLibraryEntry mod, CancellationToken cancellationToken = default)
    {
        var replacements = draft.SavedBindings.ToDictionary(binding => binding.Value,
            binding => mod.BindingFor(binding.Key)?.EditId, StringComparer.OrdinalIgnoreCase);
        var edits = draft.EditIds.Select(id => replacements.TryGetValue(id, out var replacement) ? replacement : id)
            .Where(id => id is not null).Select(id => id!).Distinct().ToArray();
        var bindings = mod.Bindings.ToDictionary(binding => binding.AssetId, binding => binding.EditId);
        if (edits.SequenceEqual(draft.EditIds) && bindings.Count == draft.SavedBindings.Count &&
            bindings.All(binding => draft.SavedBindings.GetValueOrDefault(binding.Key) == binding.Value)) return draft;
        return await SaveAsync(draft with { EditIds = edits, SavedBindings = bindings }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArkiveModAuthoringDraft> SaveAsync(
        ArkiveModAuthoringDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.SchemaVersion != 1 || !Guid.TryParseExact(draft.DraftId, "N", out _))
            throw new ArgumentException("The authoring draft identity is invalid.", nameof(draft));
        var updated = draft with
        {
            EditIds = draft.EditIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var path = Path.Combine(RootDirectory, draft.DraftId + ".json");
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(RootDirectory);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, updated, ArkiveModFileService.JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return updated;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { _gate.Release(); }
        }
    }

    public async Task DeleteAsync(string draftId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(draftId, "N", out _)) throw new ArgumentException("Invalid draft identity.", nameof(draftId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(RootDirectory, draftId + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
        finally { _gate.Release(); }
    }
}
