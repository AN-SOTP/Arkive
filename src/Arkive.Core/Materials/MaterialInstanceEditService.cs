using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Arkive.Core.Archives;
using Arkive.Core.Assets;

namespace Arkive.Core.Materials;

public sealed class MaterialInstanceEditRefusalException(string message) : InvalidOperationException(message);

public sealed class MaterialInstanceEditService
{
    public const int CurrentEditSchemaVersion = 1;
    public const int CurrentCandidateSchemaVersion = 1;

    private readonly IChunkProtectionProvider _protectionProvider;
    private readonly AssetCatalogReader _catalogReader;

    public MaterialInstanceEditService(IChunkProtectionProvider? protectionProvider = null)
    {
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(protectionProvider: _protectionProvider);
    }

    public MaterialReferenceCatalog GetReferenceCatalog(MaterialInstanceReconnaissanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ValidateReconnaissance(report);
        var catalog = _catalogReader.Read(report.PackagePath);
        return BuildReferenceCatalog(report, catalog);
    }

    public async Task<MaterialInstanceEditEntry> CreateEditAsync(
        string reconnaissancePath,
        string outputRoot,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        var reconPath = Path.GetFullPath(reconnaissancePath);
        var report = await ReadJsonAsync<MaterialInstanceReconnaissanceReport>(reconPath, cancellationToken)
            .ConfigureAwait(false);
        ValidateReconnaissance(report);
        var editId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetFullPath(outputRoot), Safe(report.PackageAlias),
            $"{report.ExportIndex:D6}-{Safe(report.ObjectName)}-{editId[..8]}");
        Directory.CreateDirectory(directory);
        var referenceCatalog = GetReferenceCatalog(report);
        var textures = report.TextureParameters.Select(item =>
        {
            var option = FindReference(referenceCatalog.TextureTargets, item.ObjectReference);
            return new MaterialTextureReference(item.Name, item.ObjectReference, item.ResolvedObject)
            {
                SourceObjectReference = item.ObjectReference,
                SourceResolvedObject = item.ResolvedObject,
                SourceIdentity = option?.StableIdentity ?? LegacyIdentity(item.ObjectReference, item.ResolvedObject),
                TargetIdentity = option?.StableIdentity ?? LegacyIdentity(item.ObjectReference, item.ResolvedObject),
            };
        }).ToArray();
        MaterialParentReference? parent = null;
        if (report.ParentObjectReference is { } parentReference)
        {
            var option = FindReference(referenceCatalog.ParentTargets, parentReference);
            var resolved = report.ParentObject ?? "None";
            var identity = option?.StableIdentity ?? LegacyIdentity(parentReference, resolved);
            parent = new(parentReference, resolved, identity, parentReference, resolved, identity);
        }
        var document = new MaterialInstanceEditDocument(
            CurrentEditSchemaVersion, editId, DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(displayName) ? report.ObjectName : displayName.Trim(),
            reconPath, report.PackagePath, report.PackageSha256, report.PackageAlias,
            report.ExportIndex, report.ClassName, report.ObjectName, report.SerialOffset,
            report.SerialSize, report.ExportPayloadSha256,
            report.ScalarParameters.Select(item => new MaterialScalarEdit(item.Name, item.Value, item.Value)).ToArray(),
            report.VectorParameters.Select(item => new MaterialVectorEdit(item.Name,
                item.Red, item.Green, item.Blue, item.Alpha,
                item.Red, item.Green, item.Blue, item.Alpha)).ToArray(),
            textures)
        {
            Parent = parent,
        };
        var path = Path.Combine(directory, MaterialInstanceEditWorkspaceStore.DocumentFileName);
        await MaterialInstanceWorkspaceStore.SaveJsonAtomicAsync(path, document, cancellationToken).ConfigureAwait(false);
        return new(directory, path, document);
    }

    public async Task<MaterialInstanceCandidateResult> BuildCandidateAsync(
        string editDocumentPath,
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        var editPath = Path.GetFullPath(editDocumentPath);
        var edit = await ReadJsonAsync<MaterialInstanceEditDocument>(editPath, cancellationToken).ConfigureAwait(false);
        if (edit.SchemaVersion != CurrentEditSchemaVersion)
            throw new MaterialInstanceEditRefusalException(
                $"Material edit schema {edit.SchemaVersion} is unsupported; Arkive requires {CurrentEditSchemaVersion}.");
        var report = await ReadJsonAsync<MaterialInstanceReconnaissanceReport>(
            edit.SourceReconnaissancePath, cancellationToken).ConfigureAwait(false);
        ValidateReconnaissance(report);
        ValidateIdentity(edit, report);

        var catalog = _catalogReader.Read(report.PackagePath);
        var references = BuildReferenceCatalog(report, catalog);
        var bytes = ReadCurrentExport(report, catalog, cancellationToken);
        if (!Sha256(bytes).Equals(report.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new MaterialInstanceEditRefusalException(
                "The source material export no longer matches its reconnaissance report. Re-run material reconnaissance after the client update.");
        var candidate = bytes.ToArray();
        var writable = new bool[candidate.Length];

        var scalarEdits = edit.Scalars.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var vectorEdits = edit.Vectors.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var textureEdits = edit.Textures.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (scalarEdits.Any(group => group.Count() != 1) ||
            vectorEdits.Any(group => group.Count() != 1) ||
            textureEdits.Any(group => group.Count() != 1))
            throw new MaterialInstanceEditRefusalException("Material edit parameter names must be unique.");
        if (scalarEdits.Length != report.ScalarParameters.Count ||
            vectorEdits.Length != report.VectorParameters.Count ||
            textureEdits.Length != report.TextureParameters.Count)
            throw new MaterialInstanceEditRefusalException("The material edit parameter set differs from the proven source layout.");

        var changedScalars = 0;
        foreach (var source in report.ScalarParameters)
        {
            var item = scalarEdits.SingleOrDefault(group => group.Key.Equals(source.Name, StringComparison.OrdinalIgnoreCase))?.Single()
                       ?? throw new MaterialInstanceEditRefusalException($"Scalar parameter '{source.Name}' is missing.");
            RequireSameBits(item.SourceValue, source.Value, $"Scalar parameter '{source.Name}' source value was altered.");
            RequireFinite(item.Value, source.Name);
            var offset = checked((int)source.ValueRelativeOffset);
            MarkWritable(writable, offset, 4);
            if (BitConverter.SingleToInt32Bits(item.Value) != BitConverter.SingleToInt32Bits(source.Value)) changedScalars++;
            WriteSingle(candidate, offset, item.Value);
        }

        var changedVectors = 0;
        foreach (var source in report.VectorParameters)
        {
            var item = vectorEdits.SingleOrDefault(group => group.Key.Equals(source.Name, StringComparison.OrdinalIgnoreCase))?.Single()
                       ?? throw new MaterialInstanceEditRefusalException($"Vector parameter '{source.Name}' is missing.");
            RequireSameBits(item.SourceRed, source.Red, $"Vector parameter '{source.Name}' source red was altered.");
            RequireSameBits(item.SourceGreen, source.Green, $"Vector parameter '{source.Name}' source green was altered.");
            RequireSameBits(item.SourceBlue, source.Blue, $"Vector parameter '{source.Name}' source blue was altered.");
            RequireSameBits(item.SourceAlpha, source.Alpha, $"Vector parameter '{source.Name}' source alpha was altered.");
            RequireFinite(item.Red, source.Name); RequireFinite(item.Green, source.Name);
            RequireFinite(item.Blue, source.Name); RequireFinite(item.Alpha, source.Name);
            var values = new[] { item.Red, item.Green, item.Blue, item.Alpha };
            var sources = new[] { source.Red, source.Green, source.Blue, source.Alpha };
            var offset = checked((int)source.ValueRelativeOffset);
            MarkWritable(writable, offset, 16);
            if (values.Where((value, index) => BitConverter.SingleToInt32Bits(value) !=
                                                BitConverter.SingleToInt32Bits(sources[index])).Any()) changedVectors++;
            for (var index = 0; index < 4; index++) WriteSingle(candidate, offset + index * 4, values[index]);
        }

        var referenceChanges = new List<MaterialObjectReferenceChange>();
        var changedTextures = 0;
        foreach (var source in report.TextureParameters)
        {
            var item = textureEdits.SingleOrDefault(group => group.Key.Equals(
                           source.Name, StringComparison.OrdinalIgnoreCase))?.Single()
                       ?? throw new MaterialInstanceEditRefusalException(
                           $"Texture parameter '{source.Name}' is missing.");
            if (item.SourceObjectReference != source.ObjectReference ||
                !string.Equals(item.SourceResolvedObject, source.ResolvedObject, StringComparison.OrdinalIgnoreCase))
                throw new MaterialInstanceEditRefusalException(
                    $"Texture parameter '{source.Name}' source reference was altered.");
            ValidateSourceIdentity(item.SourceIdentity, source.ObjectReference,
                references.TextureTargets, source.Name);
            var target = RequireReferenceTarget(
                references.TextureTargets, item.ObjectReference, item.TargetIdentity,
                item.ResolvedObject, $"Texture parameter '{source.Name}'");
            var offset = checked((int)source.ValueRelativeOffset);
            MarkWritable(writable, offset, 4);
            WriteInt32(candidate, offset, target.ObjectReference);
            if (target.ObjectReference == source.ObjectReference) continue;
            changedTextures++;
            referenceChanges.Add(new(
                $"TextureParameter:{source.Name}", source.ObjectReference,
                source.ResolvedObject, target.ObjectReference, target.ResolvedObject,
                source.ValueRelativeOffset));
        }

        var parentChanged = false;
        if (edit.Parent is { } parent)
        {
            if (report.ParentObjectReference is not { } sourceParentReference)
                throw new MaterialInstanceEditRefusalException(
                    "The edit contains a parent reference but reconnaissance did not prove a Parent ObjectProperty.");
            var sourceParent = report.ParentObject ?? "None";
            if (parent.SourceObjectReference != sourceParentReference ||
                !string.Equals(parent.SourceResolvedObject, sourceParent, StringComparison.OrdinalIgnoreCase))
                throw new MaterialInstanceEditRefusalException("The source parent material reference was altered.");
            ValidateSourceIdentity(parent.SourceIdentity, sourceParentReference,
                references.ParentTargets, "Parent material");
            var target = RequireReferenceTarget(
                references.ParentTargets, parent.ObjectReference, parent.TargetIdentity,
                parent.ResolvedObject, "Parent material");
            if (report.NativeExportIndex is { } nativeExportIndex &&
                target.ObjectReference == nativeExportIndex)
                throw new MaterialInstanceEditRefusalException(
                    "A material instance cannot be its own parent.");
            if (target.ObjectReference == 0 && target.ObjectReference != sourceParentReference)
                throw new MaterialInstanceEditRefusalException(
                    "Removing a cooked material instance parent is not supported.");
            var parentProperty = report.Properties.Where(property =>
                    property.Name.Equals("Parent", StringComparison.OrdinalIgnoreCase) &&
                    property.Type.Equals("ObjectProperty", StringComparison.OrdinalIgnoreCase) &&
                    property.Size == 4)
                .ToArray();
            if (parentProperty.Length != 1)
                throw new MaterialInstanceEditRefusalException(
                    "The parent material ObjectProperty does not have one proven 4-byte field.");
            var offset = checked((int)parentProperty[0].ValueRelativeOffset);
            MarkWritable(writable, offset, 4);
            WriteInt32(candidate, offset, target.ObjectReference);
            if (target.ObjectReference != sourceParentReference)
            {
                parentChanged = true;
                referenceChanges.Add(new(
                    "Parent", sourceParentReference, sourceParent,
                    target.ObjectReference, target.ResolvedObject,
                    parentProperty[0].ValueRelativeOffset));
            }
        }

        var changedBytes = 0;
        var lockedPreserved = true;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == candidate[index]) continue;
            changedBytes++;
            if (!writable[index]) lockedPreserved = false;
        }
        if (!lockedPreserved)
            throw new MaterialInstanceEditRefusalException("A locked material export byte changed; candidate generation was refused.");
        if (changedBytes == 0)
            throw new MaterialInstanceEditRefusalException(
                "No scalar, vector, texture reference, or parent reference differs from the source material. " +
                "Change at least one value before verifying the project.");

        var candidateId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetFullPath(outputRoot), Safe(report.PackageAlias),
            $"{report.ExportIndex:D6}-{Safe(report.ObjectName)}-{candidateId[..8]}");
        Directory.CreateDirectory(directory);
        var candidateName = Safe(report.ObjectName) + ".arkive-material-export.bin";
        var candidatePath = Path.Combine(directory, candidateName);
        await File.WriteAllBytesAsync(candidatePath, candidate, cancellationToken).ConfigureAwait(false);
        var manifest = new MaterialInstanceCandidateManifest(
            CurrentCandidateSchemaVersion, candidateId, DateTimeOffset.UtcNow, editPath,
            edit.SourceReconnaissancePath, report.PackagePath, report.PackageSha256,
            report.PackageAlias, report.ExportIndex, report.ClassName, report.ObjectName,
            report.SerialOffset, report.SerialSize, report.ExportPayloadSha256, Sha256(candidate),
            candidateName, changedScalars, changedVectors, changedBytes,
            candidate.Length == bytes.Length, lockedPreserved, false)
        {
            ChangedTextureReferenceCount = changedTextures,
            ParentReferenceChanged = parentChanged,
            ReferenceChanges = referenceChanges,
        };
        var manifestPath = Path.Combine(directory, "arkive-material-candidate.json");
        await MaterialInstanceWorkspaceStore.SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken)
            .ConfigureAwait(false);
        return new(directory, manifestPath, candidatePath, manifest);
    }

    private byte[] ReadCurrentExport(
        MaterialInstanceReconnaissanceReport report,
        AssetCatalog catalog,
        CancellationToken cancellationToken)
    {
        using var logical = new LogicalPackageStream(catalog.Summary, _protectionProvider);
        if (report.SerialOffset < 0 || report.SerialSize <= 0 || report.SerialSize > int.MaxValue ||
            report.SerialOffset > logical.Length - report.SerialSize)
            throw new MaterialInstanceEditRefusalException("The source material export range is invalid.");
        var bytes = new byte[checked((int)report.SerialSize)];
        logical.Position = report.SerialOffset;
        logical.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    private static void ValidateReconnaissance(MaterialInstanceReconnaissanceReport report)
    {
        if (report.SchemaVersion != MaterialInstanceReconnaissanceService.CurrentSchemaVersion)
            throw new MaterialInstanceEditRefusalException("The material reconnaissance schema is unsupported.");
        if (report.Readiness != MaterialInstanceReadiness.Editable ||
            report.Issues.Any(issue => issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
            throw new MaterialInstanceEditRefusalException("Material reconnaissance has not proven a safe editable layout.");
    }

    private static void ValidateIdentity(MaterialInstanceEditDocument edit, MaterialInstanceReconnaissanceReport report)
    {
        if (!edit.SourcePackagePath.Equals(report.PackagePath, StringComparison.OrdinalIgnoreCase) ||
            edit.ExportIndex != report.ExportIndex || edit.SerialOffset != report.SerialOffset ||
            edit.SerialSize != report.SerialSize ||
            !edit.ObjectName.Equals(report.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            !edit.SourceExportSha256.Equals(report.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new MaterialInstanceEditRefusalException("The material edit identity differs from its source reconnaissance report.");
    }

    private static MaterialReferenceCatalog BuildReferenceCatalog(
        MaterialInstanceReconnaissanceReport report,
        AssetCatalog catalog)
    {
        var options = new List<MaterialObjectReferenceOption>
        {
            new(0, "None", "None", "None", "None", "none", MaterialObjectReferenceScope.None),
        };
        foreach (var asset in catalog.Assets)
        {
            if (!IsTextureClass(asset.ClassName) && !IsParentClass(asset.ClassName)) continue;
            var objectPath = StripPhysicalPackagePrefix(asset.ObjectPath);
            options.Add(new(asset.ExportIndex, asset.ClassName, asset.ObjectName, objectPath,
                $"{asset.ClassName}'{asset.ObjectPath}'",
                StableIdentity(MaterialObjectReferenceScope.Export, asset.ClassName, objectPath),
                MaterialObjectReferenceScope.Export));
        }
        foreach (var import in catalog.Imports)
        {
            var className = import.ClassName.Resolve(catalog.Names);
            if (!IsTextureClass(className) && !IsParentClass(className)) continue;
            var objectName = import.ObjectName.Resolve(catalog.Names);
            var objectPath = ResolveImportPath(import.TableIndex, catalog);
            options.Add(new(import.TableIndex, className, objectName, objectPath,
                $"{className}'{objectPath}'",
                StableIdentity(MaterialObjectReferenceScope.Import, className, objectPath),
                MaterialObjectReferenceScope.Import));
        }
        var unique = options.GroupBy(option => option.ObjectReference)
            .Select(group => group.First()).ToArray();
        var textures = unique.Where(option => option.ObjectReference == 0 || IsTextureClass(option.ClassName))
            .OrderBy(option => option.ObjectReference == 0 ? 0 : 1)
            .ThenBy(option => option.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.ObjectReference)
            .ToArray();
        var parents = unique.Where(option =>
                IsParentClass(option.ClassName) ||
                option.ObjectReference == 0 && report.ParentObjectReference == 0)
            .Where(option => report.NativeExportIndex is null ||
                             option.ObjectReference != report.NativeExportIndex.Value)
            .OrderBy(option => option.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.ObjectReference)
            .ToArray();
        return new(textures, parents);
    }

    private static MaterialObjectReferenceOption RequireReferenceTarget(
        IReadOnlyList<MaterialObjectReferenceOption> options,
        int objectReference,
        string targetIdentity,
        string resolvedObject,
        string field)
    {
        var option = FindReference(options, objectReference) ??
                     throw new MaterialInstanceEditRefusalException(
                         $"{field} target {objectReference} is not a compatible object already referenced by this UPK.");
        if (!string.IsNullOrWhiteSpace(targetIdentity) && !IsLegacyFor(targetIdentity, objectReference) &&
            !targetIdentity.Equals(option.StableIdentity, StringComparison.OrdinalIgnoreCase))
            throw new MaterialInstanceEditRefusalException(
                $"{field} target identity does not match object reference {objectReference}.");
        if (!string.IsNullOrWhiteSpace(resolvedObject) &&
            !resolvedObject.Equals(option.ResolvedObject, StringComparison.OrdinalIgnoreCase) &&
            !ResolvedLeaf(resolvedObject).Equals(
                ResolvedLeaf(option.ResolvedObject), StringComparison.OrdinalIgnoreCase) &&
            !(objectReference == 0 && resolvedObject.Equals("None", StringComparison.OrdinalIgnoreCase)))
            throw new MaterialInstanceEditRefusalException(
                $"{field} resolved target does not match object reference {objectReference}.");
        return option;
    }

    private static void ValidateSourceIdentity(
        string sourceIdentity,
        int sourceReference,
        IReadOnlyList<MaterialObjectReferenceOption> options,
        string field)
    {
        if (string.IsNullOrWhiteSpace(sourceIdentity)) return;
        var option = FindReference(options, sourceReference);
        if (IsLegacyFor(sourceIdentity, sourceReference)) return;
        if (option is null || !sourceIdentity.Equals(option.StableIdentity, StringComparison.OrdinalIgnoreCase))
            throw new MaterialInstanceEditRefusalException(
                $"{field} source object identity no longer matches this package catalogue.");
    }

    private static MaterialObjectReferenceOption? FindReference(
        IReadOnlyList<MaterialObjectReferenceOption> options,
        int objectReference) => options.FirstOrDefault(option => option.ObjectReference == objectReference);

    private static string ResolveImportPath(int reference, AssetCatalog catalog)
    {
        var names = new Stack<string>();
        var current = reference;
        var visited = new HashSet<int>();
        while (current < 0 && visited.Add(current) && visited.Count <= 128)
        {
            var index = -current - 1;
            if (index < 0 || index >= catalog.Imports.Count) break;
            var import = catalog.Imports[index];
            names.Push(import.ObjectName.Resolve(catalog.Names));
            current = import.OuterIndex;
        }
        return names.Count == 0 ? $"<invalid-import:{reference}>" : string.Join('.', names);
    }

    private static bool IsTextureClass(string className) =>
        className.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("LightMapTexture2D", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("ShadowMapTexture2D", StringComparison.OrdinalIgnoreCase);

    private static bool IsParentClass(string className) =>
        className.Equals("Material", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("DecalMaterial", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("MaterialInstanceTimeVarying", StringComparison.OrdinalIgnoreCase);

    private static string StripPhysicalPackagePrefix(string objectPath)
    {
        var separator = objectPath.IndexOf('.');
        return separator >= 0 && separator < objectPath.Length - 1
            ? objectPath[(separator + 1)..]
            : objectPath;
    }

    private static string StableIdentity(
        MaterialObjectReferenceScope scope,
        string className,
        string objectPath) => $"{scope}:{className}:{objectPath}".ToLowerInvariant();

    /// <summary>
    /// An identity recorded when Arkive did not list this kind of target (a decal material parent, say) only ever named
    /// the reference number, so it still matches that same reference.
    /// </summary>
    private static bool IsLegacyFor(string identity, int objectReference) =>
        identity.StartsWith($"legacy:{objectReference}:", StringComparison.OrdinalIgnoreCase);

    private static string LegacyIdentity(int objectReference, string resolvedObject) =>
        $"legacy:{objectReference}:{resolvedObject}".ToLowerInvariant();

    private static string ResolvedLeaf(string value)
    {
        var quote = value.IndexOf('\'');
        var className = quote > 0 ? value[..quote] : string.Empty;
        var path = quote >= 0 ? value[(quote + 1)..].TrimEnd('\'') : value;
        var separator = path.LastIndexOf('.');
        var leaf = separator >= 0 ? path[(separator + 1)..] : path;
        return $"{className}:{leaf}";
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, MaterialInstanceWorkspaceStore.SerializerOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException($"JSON document '{path}' is empty.");
    }

    private static void RequireFinite(float value, string name)
    {
        if (!float.IsFinite(value))
            throw new MaterialInstanceEditRefusalException($"Material parameter '{name}' must be finite.");
    }

    private static void RequireSameBits(float actual, float expected, string message)
    {
        if (BitConverter.SingleToInt32Bits(actual) != BitConverter.SingleToInt32Bits(expected))
            throw new MaterialInstanceEditRefusalException(message);
    }

    private static void MarkWritable(bool[] mask, int offset, int length)
    {
        if (offset < 0 || offset > mask.Length - length)
            throw new MaterialInstanceEditRefusalException("A proven material parameter range is outside the export.");
        for (var index = 0; index < length; index++) mask[offset + index] = true;
    }

    private static void WriteSingle(Span<byte> bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], BitConverter.SingleToInt32Bits(value));

    private static void WriteInt32(Span<byte> bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "material" : result;
    }
}
