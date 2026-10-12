using System.Buffers.Binary;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Inspection;

namespace Arkive.Core.Meshes;

public sealed class SkeletalMeshDeformationDependencyService
{
    public const int CurrentSchemaVersion = 1;
    private const int MaximumArrayEntries = 1_000_000;
    private const int MaximumMorphLods = 32;
    private const int MaximumMorphDeltas = 16_000_000;
    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;

    public SkeletalMeshDeformationDependencyService(
        AssetCatalogReader? catalogReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _protectionProvider = protectionProvider ?? ChunkProtectionProviderFactory.CreateWesternGlobal();
        _catalogReader = catalogReader ?? new AssetCatalogReader(protectionProvider: _protectionProvider);
    }

    public SkeletalMeshDeformationDependencyReport Analyze(
        SkeletalMeshReconnaissanceReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var cloth = new List<SkeletalMeshClothBindingEvidence>();
        var morphSets = new List<SkeletalMeshMorphSetEvidence>();
        var issues = new List<SkeletalMeshDeformationDependencyIssue>();
        var meshExportIndex = report.NativeExportIndex ?? report.ExportIndex;
        AssetCatalog? catalog = null;
        LogicalPackageStream? logical = null;
        try
        {
            catalog = _catalogReader.Read(report.PackagePath);
            logical = new LogicalPackageStream(catalog.Summary, _protectionProvider);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or ArgumentException or NotSupportedException)
        {
            issues.Add(new("Warning", "SMD001",
                "The package catalogue could not be reopened for dependency resolution: " + exception.Message));
        }

        try
        {
            foreach (var property in report.Properties.Where(property => IsClothingProperty(property.Name)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] value;
                try
                {
                    value = ReadPropertyValue(report, property, logical);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or
                                                  ArgumentException or OverflowException)
                {
                    issues.Add(new("Warning", "SMD010",
                        $"Cloth property {property.Name} could not be read completely: {exception.Message}"));
                    value = ParseHex(property.RawValuePrefix);
                }

                var references = ParseObjectReferences(property, value, out var declaredCount, out var complete);
                var resolved = references.Where(reference => reference != 0)
                    .Select(reference => ResolveObject(catalog, reference)).ToArray();
                var active = references.Any(reference => reference != 0);
                var detail = !complete
                    ? "The serialized reference array was truncated or invalid."
                    : declaredCount == 0
                        ? "Empty cloth reference array."
                        : active
                            ? $"{references.Count(reference => reference != 0):N0} active object reference(s)."
                            : $"{declaredCount:N0} null placeholder reference(s); no cloth asset is bound.";
                cloth.Add(new(property.Name, property.Type, declaredCount, references, resolved, active, detail));
            }

            if (catalog is not null && logical is not null)
            {
                var expectedSetName = report.ObjectName + "_morphtargetset";
                var candidates = new List<AssetCatalogEntry>();
                foreach (var asset in catalog.Assets.Where(asset =>
                             asset.ClassName.Equals("morphtargetset", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var exactName = asset.ObjectName.Equals(expectedSetName, StringComparison.OrdinalIgnoreCase) ||
                                    asset.ObjectPath.EndsWith("." + expectedSetName, StringComparison.OrdinalIgnoreCase);
                    try
                    {
                        var payload = ReadExport(logical, asset);
                        var linkedByBase = TryReadMorphSetProperties(
                            payload, catalog.Names, out var baseReference, out _) &&
                                           baseReference == meshExportIndex;
                        if (exactName || linkedByBase) candidates.Add(asset);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or
                                                      ArgumentException or OverflowException)
                    {
                        if (exactName)
                        {
                            candidates.Add(asset);
                            issues.Add(new("Warning", "SMD020",
                                $"Named MorphTargetSet {asset.ObjectName} could not be inspected while resolving its base mesh: {exception.Message}"));
                        }
                    }
                }
                foreach (var set in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        morphSets.Add(ReadMorphSet(
                            catalog, logical, set, meshExportIndex, cancellationToken, issues));
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or
                                                      ArgumentException or OverflowException)
                    {
                        issues.Add(new("Warning", "SMD025",
                            $"MorphTargetSet {set.ObjectName} could not be read completely: {exception.Message}"));
                    }
                }
            }
        }
        finally
        {
            logical?.Dispose();
        }

        var clothState = issues.Any(issue => issue.Code == "SMD010")
            ? SkeletalMeshDependencyState.Unresolved
            : cloth.Any(binding => binding.IsActive)
                ? SkeletalMeshDependencyState.ActiveVertexBoundData
                : cloth.Count > 0
                    ? SkeletalMeshDependencyState.InactivePlaceholder
                    : SkeletalMeshDependencyState.None;
        var morphParseFailed = morphSets.SelectMany(set => set.Targets).Any(target => !target.Parsed) ||
                               issues.Any(issue => issue.Code is "SMD001" or "SMD020" or "SMD021" or "SMD022" or
                                   "SMD023" or "SMD024" or "SMD025");
        var morphDeltas = morphSets.SelectMany(set => set.Targets).Sum(target => target.DeltaCount);
        var morphState = morphParseFailed
            ? SkeletalMeshDependencyState.Unresolved
            : morphDeltas > 0
                ? SkeletalMeshDependencyState.ActiveVertexBoundData
                : morphSets.Count > 0
                    ? SkeletalMeshDependencyState.InactivePlaceholder
                    : SkeletalMeshDependencyState.None;
        // The bounded same-topology encoder never rewrites dependency exports and locks every
        // non-vertex byte in the SkeletalMesh. Stable vertex/section identity therefore makes
        // preservation safe even when an optional dependency could not be semantically decoded.
        const bool preservingSupported = true;
        var changingSupported = clothState != SkeletalMeshDependencyState.Unresolved &&
                                morphState != SkeletalMeshDependencyState.Unresolved &&
                                clothState != SkeletalMeshDependencyState.ActiveVertexBoundData &&
                                morphState != SkeletalMeshDependencyState.ActiveVertexBoundData;
        var reason = clothState == SkeletalMeshDependencyState.Unresolved ||
                     morphState == SkeletalMeshDependencyState.Unresolved
            ? "Dependency evidence is incomplete; Arkive cannot prove vertex-bound data will remain valid."
            : clothState == SkeletalMeshDependencyState.ActiveVertexBoundData
                ? "Active cloth bindings reference the source mesh's vertex/section identity."
                : morphState == SkeletalMeshDependencyState.ActiveVertexBoundData
                    ? "External morph deltas reference source LOD vertex indices."
                    : "No active cloth binding or morph delta depends on the source topology.";
        return new(
            CurrentSchemaVersion, DateTimeOffset.UtcNow, report.PackagePath, report.PackageSha256,
            meshExportIndex, report.ObjectName, clothState, morphState,
            cloth.SelectMany(binding => binding.ObjectReferences).Count(reference => reference != 0),
            morphSets.Count, morphSets.Sum(set => set.ResolvedTargetCount), morphDeltas,
            preservingSupported, changingSupported, reason, cloth, morphSets, issues);
    }

    private static SkeletalMeshMorphSetEvidence ReadMorphSet(
        AssetCatalog catalog,
        LogicalPackageStream logical,
        AssetCatalogEntry set,
        int meshExportIndex,
        CancellationToken token,
        ICollection<SkeletalMeshDeformationDependencyIssue> issues)
    {
        var bytes = ReadExport(logical, set);
        int? baseReference = null;
        var declaredTargets = -1;
        var targetReferences = Array.Empty<int>();
        if (!TryReadMorphSetProperties(bytes, catalog.Names, out baseReference, out targetReferences))
        {
            issues.Add(new("Warning", "SMD021",
                $"MorphTargetSet {set.ObjectName} did not match the proven UE3 property layout."));
        }
        else declaredTargets = targetReferences.Length;

        var children = catalog.Assets.Where(asset =>
            asset.ClassName.Equals("morphtarget", StringComparison.OrdinalIgnoreCase) &&
            catalog.Exports[asset.ExportIndex - 1].OuterIndex == set.ExportIndex).ToArray();
        var selected = targetReferences.Length > 0
            ? targetReferences.Where(reference => reference > 0 && reference <= catalog.Assets.Count)
                .Select(reference => catalog.Assets[reference - 1])
                .Where(asset => asset.ClassName.Equals("morphtarget", StringComparison.OrdinalIgnoreCase)).ToArray()
            : children;
        var targets = new List<SkeletalMeshMorphTargetEvidence>(selected.Length);
        foreach (var target in selected)
        {
            token.ThrowIfCancellationRequested();
            var payload = ReadExport(logical, target);
            if (TryParseMorphTarget(payload, catalog.Names, out var lods, out var refusal))
            {
                targets.Add(new(target.ExportIndex, target.ObjectName, target.ObjectPath, target.SerialSize,
                    true, lods.Sum(lod => lod.DeltaCount), lods, "UE3 morph LOD/delta layout decoded."));
            }
            else
            {
                issues.Add(new("Warning", "SMD022", $"MorphTarget {target.ObjectName}: {refusal}"));
                targets.Add(new(target.ExportIndex, target.ObjectName, target.ObjectPath, target.SerialSize,
                    false, 0, [], refusal));
            }
        }

        var baseMatches = baseReference == meshExportIndex;
        if (!baseMatches)
            issues.Add(new("Warning", "SMD023",
                $"MorphTargetSet {set.ObjectName} resolves BaseSkelMesh={baseReference?.ToString() ?? "null"}, " +
                $"not the selected mesh export {meshExportIndex}."));
        if (declaredTargets >= 0 && declaredTargets != targets.Count)
            issues.Add(new("Warning", "SMD024",
                $"MorphTargetSet {set.ObjectName} declares {declaredTargets:N0} target(s), but " +
                $"{targets.Count:N0} target export(s) resolved."));
        return new(set.ExportIndex, set.ObjectName, set.ObjectPath, baseReference,
            baseMatches, Math.Max(0, declaredTargets), targets.Count, targets);
    }

    public static bool TryParseMorphTarget(
        ReadOnlySpan<byte> bytes,
        IReadOnlyList<UnrealNameEntry> names,
        out IReadOnlyList<SkeletalMeshMorphLodEvidence> lods,
        out string refusal)
    {
        lods = [];
        refusal = string.Empty;
        var nativeOffset = FindNoneTerminator(bytes, names);
        if (nativeOffset < 0 || nativeOffset > bytes.Length - 4)
        {
            refusal = "The UObject property terminator was not found.";
            return false;
        }
        var cursor = nativeOffset;
        var lodCount = ReadInt32(bytes, ref cursor);
        if (lodCount < 0 || lodCount > MaximumMorphLods)
        {
            refusal = $"Invalid morph LOD count {lodCount}.";
            return false;
        }
        var result = new List<SkeletalMeshMorphLodEvidence>(lodCount);
        for (var lodIndex = 0; lodIndex < lodCount; lodIndex++)
        {
            if (cursor > bytes.Length - 4)
            {
                refusal = $"Morph LOD {lodIndex} is truncated before its delta count.";
                return false;
            }
            var count = ReadInt32(bytes, ref cursor);
            if (count < 0 || count > MaximumMorphDeltas || count > (bytes.Length - cursor - 4L) / 20L)
            {
                refusal = $"Morph LOD {lodIndex} has invalid delta count {count:N0}.";
                return false;
            }
            var maximum = -1;
            var indicesInRange = true;
            for (var delta = 0; delta < count; delta++)
            {
                cursor += 16; // FVector position delta + packed tangent-Z delta.
                var sourceIndex = ReadInt32(bytes, ref cursor);
                maximum = Math.Max(maximum, sourceIndex);
                if (sourceIndex < 0) indicesInRange = false;
            }
            if (cursor > bytes.Length - 4)
            {
                refusal = $"Morph LOD {lodIndex} is truncated before NumBaseMeshVerts.";
                return false;
            }
            var baseVertices = ReadInt32(bytes, ref cursor);
            indicesInRange &= baseVertices >= 0 && (maximum < 0 || maximum < baseVertices);
            result.Add(new(lodIndex, count, baseVertices, maximum, indicesInRange));
        }
        if (cursor != bytes.Length)
        {
            refusal = $"Morph payload has {bytes.Length - cursor:N0} unexplained trailing byte(s).";
            return false;
        }
        lods = result;
        return true;
    }

    private static bool TryReadMorphSetProperties(
        ReadOnlySpan<byte> bytes,
        IReadOnlyList<UnrealNameEntry> names,
        out int? baseReference,
        out int[] targets)
    {
        baseReference = null;
        targets = [];
        for (var start = 0; start <= Math.Min(64, bytes.Length - 8); start += 4)
        {
            var cursor = start;
            int? candidateBase = null;
            int[]? candidateTargets = null;
            var valid = true;
            for (var property = 0; property < 64; property++)
            {
                if (!TryReadName(bytes, ref cursor, names, out var name)) { valid = false; break; }
                if (name.Equals("none", StringComparison.OrdinalIgnoreCase)) break;
                if (!TryReadName(bytes, ref cursor, names, out var type) || cursor > bytes.Length - 8)
                { valid = false; break; }
                var size = ReadInt32(bytes, ref cursor);
                _ = ReadInt32(bytes, ref cursor);
                if (size < 0 || size > bytes.Length - cursor) { valid = false; break; }
                var value = bytes.Slice(cursor, size);
                if (name.Equals("baseskelmesh", StringComparison.OrdinalIgnoreCase) && size >= 4)
                    candidateBase = BinaryPrimitives.ReadInt32LittleEndian(value);
                else if (name.Equals("targets", StringComparison.OrdinalIgnoreCase) &&
                         type.Equals("arrayproperty", StringComparison.OrdinalIgnoreCase) && size >= 4)
                {
                    var count = BinaryPrimitives.ReadInt32LittleEndian(value);
                    if (count < 0 || count > MaximumArrayEntries || 4L + count * 4L > value.Length)
                    { valid = false; break; }
                    candidateTargets = new int[count];
                    for (var index = 0; index < count; index++)
                        candidateTargets[index] = BinaryPrimitives.ReadInt32LittleEndian(
                            value[(4 + index * 4)..]);
                }
                cursor += size;
            }
            if (valid && candidateTargets is not null)
            {
                baseReference = candidateBase;
                targets = candidateTargets;
                return true;
            }
        }
        return false;
    }

    private static byte[] ReadPropertyValue(
        SkeletalMeshReconnaissanceReport report,
        SkeletalMeshPropertyEvidence property,
        LogicalPackageStream? logical)
    {
        if (logical is null) return ParseHex(property.RawValuePrefix);
        if (property.Size < 0 || report.SerialOffset + property.ValueRelativeOffset < 0 ||
            report.SerialOffset + property.ValueRelativeOffset > logical.Length - property.Size)
            throw new InvalidDataException("The property value range is outside the logical package image.");
        var value = new byte[property.Size];
        logical.Position = report.SerialOffset + property.ValueRelativeOffset;
        logical.ReadExactly(value);
        return value;
    }

    private static int[] ParseObjectReferences(
        SkeletalMeshPropertyEvidence property,
        ReadOnlySpan<byte> value,
        out int declaredCount,
        out bool complete)
    {
        declaredCount = 0;
        complete = true;
        if (property.Type.Equals("ObjectProperty", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Length < 4) { complete = false; return []; }
            declaredCount = 1;
            return [BinaryPrimitives.ReadInt32LittleEndian(value)];
        }
        if (value.Length < 4) { complete = false; return []; }
        declaredCount = BinaryPrimitives.ReadInt32LittleEndian(value);
        if (declaredCount < 0 || declaredCount > MaximumArrayEntries || 4L + declaredCount * 4L > value.Length)
        {
            complete = false;
            return [];
        }
        var references = new int[declaredCount];
        for (var index = 0; index < declaredCount; index++)
            references[index] = BinaryPrimitives.ReadInt32LittleEndian(value[(4 + index * 4)..]);
        return references;
    }

    private static int FindNoneTerminator(ReadOnlySpan<byte> bytes, IReadOnlyList<UnrealNameEntry> names)
    {
        for (var offset = 0; offset <= Math.Min(64 * 1024, bytes.Length - 8); offset += 4)
        {
            var index = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
            var number = BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 4)..]);
            if (number == 0 && index >= 0 && index < names.Count &&
                names[index].Value.Equals("none", StringComparison.OrdinalIgnoreCase)) return offset + 8;
        }
        return -1;
    }

    private static bool TryReadName(
        ReadOnlySpan<byte> bytes,
        ref int cursor,
        IReadOnlyList<UnrealNameEntry> names,
        out string value)
    {
        value = string.Empty;
        if (cursor < 0 || cursor > bytes.Length - 8) return false;
        var index = ReadInt32(bytes, ref cursor);
        var number = ReadInt32(bytes, ref cursor);
        if (index < 0 || index >= names.Count || number < 0) return false;
        value = names[index].Value;
        return true;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, ref int cursor)
    {
        if (cursor < 0 || cursor > bytes.Length - 4) throw new EndOfStreamException();
        var value = BinaryPrimitives.ReadInt32LittleEndian(bytes[cursor..]);
        cursor += 4;
        return value;
    }

    private static byte[] ReadExport(LogicalPackageStream logical, AssetCatalogEntry asset)
    {
        var bytes = new byte[asset.SerialSize];
        logical.Position = asset.SerialOffset;
        logical.ReadExactly(bytes);
        return bytes;
    }

    private static string ResolveObject(AssetCatalog? catalog, int reference)
    {
        if (catalog is null) return reference.ToString();
        if (reference > 0 && reference <= catalog.Assets.Count)
            return $"{reference}:{catalog.Assets[reference - 1].ClassName}:{catalog.Assets[reference - 1].ObjectPath}";
        if (reference < 0 && -reference <= catalog.Imports.Count)
        {
            var item = catalog.Imports[-reference - 1];
            return $"{reference}:import:{item.ObjectName.Resolve(catalog.Names)}";
        }
        return $"{reference}:unresolved";
    }

    private static bool IsClothingProperty(string name) =>
        name.Equals("clothingassets", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("apexclothingassets", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("clothassets", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("clothingasset", StringComparison.OrdinalIgnoreCase);

    private static byte[] ParseHex(string value)
    {
        try { return string.IsNullOrWhiteSpace(value) ? [] : Convert.FromHexString(value); }
        catch (FormatException) { return []; }
    }
}
