using System.Text.Json;
using Arkive.Core.Archives;

namespace Arkive.Core.Materials;

/// <summary>
/// Persistent workspace for user-authored material parameter projects. The edit
/// document is the project manifest; native candidates are reproducible build
/// artifacts and are linked only after their locked-byte proof succeeds.
/// </summary>
public sealed class MaterialInstanceEditWorkspaceStore
{
    public const string DocumentFileName = "arkive-material-edit.json";

    private readonly MaterialInstanceEditService _editService;

    public MaterialInstanceEditWorkspaceStore(
        string rootDirectory,
        IChunkProtectionProvider? protectionProvider = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        _editService = new MaterialInstanceEditService(protectionProvider);
    }

    public string RootDirectory { get; }

    private readonly Mods.ParsedRecordCache<MaterialInstanceEditEntry> _parsed = new();

    public async Task<IReadOnlyList<MaterialInstanceEditEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var entries = new List<MaterialInstanceEditEntry>();
        foreach (var path in Directory.EnumerateFiles(
                     RootDirectory, DocumentFileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                entries.Add(await _parsed.GetAsync(path, LoadEntryAsync, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                // One damaged project must not hide healthy projects.
            }
        }
        return entries.OrderByDescending(entry => entry.Document.UpdatedAt).ToArray();
    }

    public async Task<MaterialInstanceEditEntry> LoadEntryAsync(
        string documentPath,
        CancellationToken cancellationToken = default)
    {
        var path = RequireWithinRoot(documentPath);
        if (!File.Exists(path)) throw new FileNotFoundException("The material edit project was not found.", path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<MaterialInstanceEditDocument>(
                           stream, MaterialInstanceWorkspaceStore.SerializerOptions, cancellationToken)
                           .ConfigureAwait(false) ??
                       throw new InvalidDataException("The material edit project was empty.");
        if (document.SchemaVersion != MaterialInstanceEditService.CurrentEditSchemaVersion)
            throw new NotSupportedException(
                $"Material edit schema {document.SchemaVersion} is unsupported; Arkive requires {MaterialInstanceEditService.CurrentEditSchemaVersion}.");
        return new MaterialInstanceEditEntry(Path.GetDirectoryName(path)!, path, document);
    }

    public async Task<MaterialInstanceEditEntry> SaveAsync(
        MaterialInstanceEditEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = RequireWithinRoot(entry.DocumentPath);
        var updated = entry.Document with { UpdatedAt = DateTimeOffset.UtcNow };
        await MaterialInstanceWorkspaceStore.SaveJsonAtomicAsync(path, updated, cancellationToken)
            .ConfigureAwait(false);
        return entry with { Document = updated };
    }

    public async Task<MaterialInstanceEditEntry> SetIncludedAsync(
        MaterialInstanceEditEntry entry,
        bool included,
        CancellationToken cancellationToken = default) =>
        await SaveAsync(entry with
        {
            Document = entry.Document with { IncludedInPackageBuild = included },
        }, cancellationToken).ConfigureAwait(false);

    public async Task<MaterialInstanceEditEntry> RecordCandidateAsync(
        MaterialInstanceEditEntry entry,
        MaterialInstanceCandidateResult candidate,
        CancellationToken cancellationToken = default) =>
        await SaveAsync(entry with
        {
            Document = entry.Document with
            {
                LatestCandidateManifestPath = Path.GetFullPath(candidate.ManifestPath),
                LatestCandidatePath = Path.GetFullPath(candidate.CandidatePath),
                LatestCandidateSha256 = candidate.Manifest.CandidateExportSha256,
            },
        }, cancellationToken).ConfigureAwait(false);

    public async Task<MaterialInstanceEditEntry> RebaseSourceAsync(
        MaterialInstanceEditEntry entry,
        MaterialInstanceReconnaissanceReport report,
        string reconnaissancePath,
        CancellationToken cancellationToken = default)
    {
        if (report.Readiness != MaterialInstanceReadiness.Editable || report.NativeExportIndex is null)
            throw new NotSupportedException("The updated material source is not safely editable.");
        var scalars = report.ScalarParameters.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var vectors = report.VectorParameters.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        if (entry.Document.Scalars.Any(item => !scalars.TryGetValue(item.Name, out var source) ||
                BitConverter.SingleToInt32Bits(item.SourceValue) != BitConverter.SingleToInt32Bits(source.Value)) ||
            entry.Document.Vectors.Any(item => !vectors.TryGetValue(item.Name, out var source) ||
                BitConverter.SingleToInt32Bits(item.SourceRed) != BitConverter.SingleToInt32Bits(source.Red) ||
                BitConverter.SingleToInt32Bits(item.SourceGreen) != BitConverter.SingleToInt32Bits(source.Green) ||
                BitConverter.SingleToInt32Bits(item.SourceBlue) != BitConverter.SingleToInt32Bits(source.Blue) ||
                BitConverter.SingleToInt32Bits(item.SourceAlpha) != BitConverter.SingleToInt32Bits(source.Alpha)))
            throw new NotSupportedException(
                "The updated material parameter layout differs from the byte-identical source evidence.");
        var referenceCatalog = _editService.GetReferenceCatalog(report);
        var previousTextures = entry.Document.Textures.ToDictionary(
            item => item.Name, StringComparer.OrdinalIgnoreCase);
        if (previousTextures.Count != report.TextureParameters.Count ||
            report.TextureParameters.Any(item => !previousTextures.ContainsKey(item.Name)))
            throw new NotSupportedException(
                "The updated material texture-parameter layout differs from the source evidence.");
        var rebasedTextures = report.TextureParameters.Select(source =>
        {
            var previous = previousTextures[source.Name];
            var sourceOption = referenceCatalog.TextureTargets.FirstOrDefault(option =>
                option.ObjectReference == source.ObjectReference) ??
                               throw new NotSupportedException(
                                   $"Updated texture parameter '{source.Name}' does not resolve to a compatible target.");
            RequireSameSourceIdentity(previous.SourceIdentity, previous.SourceResolvedObject,
                sourceOption, $"Texture parameter '{source.Name}'");
            var target = ResolveRebasedTarget(previous.ObjectReference == previous.SourceObjectReference,
                previous.TargetIdentity, sourceOption, referenceCatalog.TextureTargets,
                $"Texture parameter '{source.Name}'");
            return new MaterialTextureReference(source.Name, target.ObjectReference, target.ResolvedObject)
            {
                SourceObjectReference = source.ObjectReference,
                SourceResolvedObject = source.ResolvedObject,
                SourceIdentity = sourceOption.StableIdentity,
                TargetIdentity = target.StableIdentity,
            };
        }).ToArray();
        MaterialParentReference? rebasedParent = null;
        if (report.ParentObjectReference is { } sourceParentReference)
        {
            var sourceOption = referenceCatalog.ParentTargets.FirstOrDefault(option =>
                option.ObjectReference == sourceParentReference) ??
                               throw new NotSupportedException(
                                   "The updated parent material reference does not resolve to a compatible target.");
            var previous = entry.Document.Parent;
            if (previous is not null)
                RequireSameSourceIdentity(previous.SourceIdentity, previous.SourceResolvedObject,
                    sourceOption, "Parent material");
            var target = previous is null
                ? sourceOption
                : ResolveRebasedTarget(previous.ObjectReference == previous.SourceObjectReference,
                    previous.TargetIdentity, sourceOption, referenceCatalog.ParentTargets,
                    "Parent material");
            rebasedParent = new(sourceParentReference, report.ParentObject ?? sourceOption.ResolvedObject,
                sourceOption.StableIdentity, target.ObjectReference, target.ResolvedObject,
                target.StableIdentity);
        }
        else if (entry.Document.Parent is not null)
        {
            throw new NotSupportedException("The updated material no longer exposes its parent reference.");
        }
        var updated = entry.Document with
        {
            SourceReconnaissancePath = Path.GetFullPath(reconnaissancePath),
            SourcePackagePath = report.PackagePath,
            SourcePackageSha256 = report.PackageSha256,
            ExportIndex = report.ExportIndex,
            SerialOffset = report.SerialOffset,
            SerialSize = report.SerialSize,
            SourceExportSha256 = report.ExportPayloadSha256,
            Textures = rebasedTextures,
            Parent = rebasedParent,
            LatestCandidateManifestPath = null,
            LatestCandidatePath = null,
            LatestCandidateSha256 = null,
        };
        return await SaveAsync(entry with { Document = updated }, cancellationToken).ConfigureAwait(false);
    }

    private static MaterialObjectReferenceOption ResolveRebasedTarget(
        bool wasUnchanged,
        string targetIdentity,
        MaterialObjectReferenceOption sourceOption,
        IReadOnlyList<MaterialObjectReferenceOption> options,
        string field)
    {
        if (wasUnchanged || string.IsNullOrWhiteSpace(targetIdentity)) return sourceOption;
        return options.FirstOrDefault(option => option.StableIdentity.Equals(
                   targetIdentity, StringComparison.OrdinalIgnoreCase)) ??
               throw new NotSupportedException(
                   $"{field}'s selected replacement is no longer referenced by the updated UPK.");
    }

    private static void RequireSameSourceIdentity(
        string sourceIdentity,
        string sourceResolvedObject,
        MaterialObjectReferenceOption current,
        string field)
    {
        if (!string.IsNullOrWhiteSpace(sourceIdentity))
        {
            if (!sourceIdentity.Equals(current.StableIdentity, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"{field}'s source object identity changed in the updated UPK.");
            return;
        }
        if (!string.IsNullOrWhiteSpace(sourceResolvedObject) &&
            !sourceResolvedObject.Equals(current.ResolvedObject, StringComparison.OrdinalIgnoreCase) &&
            !ResolvedLeaf(sourceResolvedObject).Equals(
                ResolvedLeaf(current.ResolvedObject), StringComparison.OrdinalIgnoreCase) &&
            !(current.ObjectReference == 0 && sourceResolvedObject.Equals("None", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException(
                $"{field}'s source object changed in the updated UPK.");
    }

    private static string ResolvedLeaf(string value)
    {
        var quote = value.IndexOf('\'');
        var className = quote > 0 ? value[..quote] : string.Empty;
        var path = quote >= 0 ? value[(quote + 1)..].TrimEnd('\'') : value;
        var separator = path.LastIndexOf('.');
        var leaf = separator >= 0 ? path[(separator + 1)..] : path;
        return $"{className}:{leaf}";
    }

    public Task DeleteAsync(MaterialInstanceEditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = RequireWithinRoot(entry.DirectoryPath);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private string RequireWithinRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(RootDirectory, full);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The material project path is outside Arkive's workspace.");
        return full;
    }
}
