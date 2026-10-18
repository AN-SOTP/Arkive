using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Packages;

namespace Arkive.Core.Materials;

public sealed class MaterialInstanceReconnaissanceService
{
    public const int CurrentSchemaVersion = 1;
    private const int MaximumExportBytes = 64 * 1024 * 1024;
    private const int MaximumPropertyCount = 4_096;
    private const int MaximumParameterCount = 65_536;

    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;

    public MaterialInstanceReconnaissanceService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(summaryReader, _protectionProvider);
    }

    public Task<MaterialInstanceReconnaissanceReport> InspectAsync(
        MaterialInstanceReconnaissanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Inspect(request, cancellationToken), cancellationToken);
    }

    private MaterialInstanceReconnaissanceReport Inspect(
        MaterialInstanceReconnaissanceRequest request,
        CancellationToken cancellationToken)
    {
        var packagePath = Path.GetFullPath(request.PackagePath);
        var file = new FileInfo(packagePath);
        if (!file.Exists) throw new FileNotFoundException("The material source package was not found.", packagePath);
        if (request.SerialOffset < 0 || request.SerialSize <= 0 || request.SerialSize > MaximumExportBytes)
            throw new ArgumentOutOfRangeException(nameof(request), "Material reconnaissance requires a bounded positive export range.");

        var initialLength = file.Length;
        var initialWriteTicks = file.LastWriteTimeUtc.Ticks;
        var catalog = _catalogReader.Read(packagePath);
        using var logical = new LogicalPackageStream(catalog.Summary, _protectionProvider);
        if (request.SerialOffset > logical.Length - request.SerialSize)
            throw new PackageFormatException("The selected material export range is outside the logical package image.");
        var bytes = new byte[checked((int)request.SerialSize)];
        logical.Position = request.SerialOffset;
        logical.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();

        var issues = new List<MaterialReconnaissanceIssue>();
        var matches = catalog.Assets.Where(asset =>
            asset.SerialOffset == request.SerialOffset && asset.SerialSize == request.SerialSize &&
            asset.ObjectName.Equals(request.ObjectName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var nativeExportIndex = matches.Length == 1 ? matches[0].ExportIndex : (int?)null;
        if (nativeExportIndex is null)
            issues.Add(new("Error", "MAT004", "The selected object/range did not resolve uniquely to the native export table."));
        if (request.Kind != AssetKind.Material ||
            !request.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase))
            issues.Add(new("Error", "MAT001", $"The selected row is {request.Kind}/{request.ClassName}, not MaterialInstanceConstant."));

        var parse = FindPropertyStream(bytes, request.SerialOffset, catalog);
        if (parse is null)
            issues.Add(new("Error", "MAT100", "Arkive could not prove the MaterialInstanceConstant property stream."));

        var scalars = new List<MaterialScalarParameterEvidence>();
        var vectors = new List<MaterialVectorParameterEvidence>();
        var textures = new List<MaterialTextureParameterEvidence>();
        var collected = new List<MaterialCollectedTexture>();
        int? parentReference = null;
        string? parentObject = null;
        if (parse is not null)
        {
            foreach (var property in parse.Properties)
            {
                var valueOffset = checked((int)property.ValueRelativeOffset);
                var value = bytes.AsSpan(valueOffset, property.Size);
                if (property.Name.Equals("Parent", StringComparison.OrdinalIgnoreCase) &&
                    property.Type.Equals("ObjectProperty", StringComparison.OrdinalIgnoreCase) && value.Length == 4)
                {
                    parentReference = ReadInt32(value, 0);
                    parentObject = ResolveObject(parentReference.Value, catalog);
                }
                else if (property.Name.Equals("ScalarParameterValues", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseScalars(value, valueOffset, catalog.Names, scalars, out var refusal))
                        issues.Add(new("Error", "MAT110", refusal));
                }
                else if (property.Name.Equals("VectorParameterValues", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseVectors(value, valueOffset, catalog.Names, vectors, out var refusal))
                        issues.Add(new("Error", "MAT111", refusal));
                }
                else if (property.Name.Equals("TextureParameterValues", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseTextures(value, valueOffset, catalog, textures, out var refusal))
                        issues.Add(new("Error", "MAT112", refusal));
                }
                // Defaults only inform previews, so an unreadable list is skipped rather than reported.
                else if (property.Name.Equals("CollectedTextureParameters", StringComparison.OrdinalIgnoreCase))
                    TryParseCollectedTextures(value, catalog, collected);
            }
            if (scalars.Count == 0 && vectors.Count == 0 && textures.Count == 0 && parentReference is null)
                issues.Add(new("Warning", "MAT120", "This material instance has no proven editable parameter or parent reference."));
            if (textures.Count > 0)
                issues.Add(new("Information", "MAT121",
                    "Texture parameter references may be redirected to compatible exports or existing imports in this UPK."));
            if (parse.Properties.Any(property =>
                    property.Name.Equals("bHasStaticPermutationResource", StringComparison.OrdinalIgnoreCase) &&
                    property.ValuePreview.Equals("True", StringComparison.OrdinalIgnoreCase)))
                issues.Add(new("Information", "MAT122",
                    "This instance has a cooked static shader permutation. Static switches and component masks remain locked."));
        }

        var packageHash = string.IsNullOrWhiteSpace(request.KnownPackageSha256)
            ? ComputeFileHash(packagePath, cancellationToken)
            : NormalizeSha256(request.KnownPackageSha256);
        var current = new FileInfo(packagePath);
        if (!current.Exists || current.Length != initialLength || current.LastWriteTimeUtc.Ticks != initialWriteTicks)
            throw new IOException("The source package changed during material reconnaissance; the report was discarded.");

        var readiness = !issues.Any(issue => issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)) &&
                        parse is not null &&
                        (scalars.Count > 0 || vectors.Count > 0 || textures.Count > 0 || parentReference is not null)
            ? MaterialInstanceReadiness.Editable
            : issues.Any(issue => issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
                ? MaterialInstanceReadiness.Refused
                : MaterialInstanceReadiness.EvidenceOnly;

        return new MaterialInstanceReconnaissanceReport(
            CurrentSchemaVersion, DateTimeOffset.UtcNow, packagePath, file.Name,
            request.PackageAlias, initialLength, initialWriteTicks, packageHash,
            logical.ProtectionProviderName, request.ExportIndex, nativeExportIndex,
            request.Kind, request.ClassName, request.ObjectName, request.SerialOffset,
            request.SerialSize, Sha256(bytes), parse?.StartOffset, parse?.EndOffset,
            parentReference, parentObject, parse?.Properties ?? [], scalars, vectors,
            textures, readiness, issues) { CollectedTextures = collected };
    }

    private static PropertyParseResult? FindPropertyStream(
        ReadOnlySpan<byte> bytes, long serialOffset, AssetCatalog catalog)
    {
        PropertyParseResult? best = null;
        var bestScore = 0;
        var maximumStart = Math.Min(64 * 1024, Math.Max(0, bytes.Length - 8));
        for (var start = 0; start <= maximumStart; start += 4)
        {
            foreach (var extendedIntTag in new[] { false, true })
            {
                if (!TryParsePropertyStream(bytes, serialOffset, catalog, start, extendedIntTag, out var candidate)) continue;
                var score = RootPropertyScore(candidate);
                if (score <= 0) continue;
                if (best is null || score > bestScore || score == bestScore && candidate.StartOffset < best.StartOffset)
                {
                    best = candidate;
                    bestScore = score;
                }
            }
        }
        return best;
    }

    private static int RootPropertyScore(PropertyParseResult candidate)
    {
        var score = 0;
        foreach (var property in candidate.Properties)
        {
            if (property.Name.Equals("ScalarParameterValues", StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (property.Name.Equals("VectorParameterValues", StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (property.Name.Equals("TextureParameterValues", StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (property.Name.Equals("Parent", StringComparison.OrdinalIgnoreCase)) score += 25;
            else if (property.Name.Contains("FontParameterValues", StringComparison.OrdinalIgnoreCase)) score += 10;
        }
        return score;
    }

    private static bool TryParsePropertyStream(
        ReadOnlySpan<byte> bytes, long serialOffset, AssetCatalog catalog, int start,
        bool extendedIntTag, out PropertyParseResult result)
    {
        result = null!;
        var cursor = start;
        var properties = new List<MaterialPropertyEvidence>();
        for (var propertyIndex = 0; propertyIndex < MaximumPropertyCount; propertyIndex++)
        {
            var tagStart = cursor;
            if (!TryReadName(bytes, ref cursor, catalog.Names, out var propertyName)) return false;
            if (propertyName.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                result = new(start, cursor, properties);
                return properties.Count > 0;
            }
            if (!TryReadName(bytes, ref cursor, catalog.Names, out var propertyType) ||
                cursor > bytes.Length - 8 || !propertyType.EndsWith("Property", StringComparison.OrdinalIgnoreCase)) return false;
            var size = ReadInt32(bytes, cursor);
            var arrayIndex = ReadInt32(bytes, cursor + 4);
            cursor += 8;
            if (size < 0 || arrayIndex < 0) return false;
            byte? taggedBool = null;
            if (propertyType.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) ||
                propertyType.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadName(bytes, ref cursor, catalog.Names, out _)) return false;
            }
            else if (extendedIntTag && propertyType.Equals("IntProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadName(bytes, ref cursor, catalog.Names, out _)) return false;
            }
            else if (propertyType.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (cursor >= bytes.Length) return false;
                taggedBool = bytes[cursor++];
            }
            if (size > bytes.Length - cursor) return false;
            var value = bytes.Slice(cursor, size);
            properties.Add(new(tagStart, serialOffset + tagStart, propertyName, propertyType,
                size, arrayIndex, cursor, PreviewValue(propertyType, value, taggedBool, catalog),
                Convert.ToHexString(value[..Math.Min(16, value.Length)])));
            cursor += size;
        }
        return false;
    }

    private static bool TryParseScalars(
        ReadOnlySpan<byte> value, int valueOffset, IReadOnlyList<UnrealNameEntry> names,
        ICollection<MaterialScalarParameterEvidence> output, out string refusal)
    {
        refusal = string.Empty;
        if (!TryGetArrayCount(value, out var count, out refusal)) return false;
        var cursor = 4;
        for (var index = 0; index < count; index++)
        {
            var element = cursor;
            if (!TryParseNestedProperties(value, ref cursor, names, out var properties, out refusal)) return false;
            if (!TryGetNested(properties, "ParameterName", "NameProperty", 8, out var nameProperty) ||
                !TryGetNested(properties, "ParameterValue", "FloatProperty", 4, out var valueProperty) ||
                !TryGetNested(properties, "ExpressionGUID", "StructProperty", 16, out var guidProperty))
            { refusal = $"Scalar parameter {index:N0} does not have the proven UE3 name/value/GUID layout."; return false; }
            var nameCursor = nameProperty.ValueOffset;
            if (!TryReadName(value, ref nameCursor, names, out var name))
            { refusal = $"Scalar parameter {index:N0} has an invalid name reference."; return false; }
            var parameterValue = ReadSingle(value, valueProperty.ValueOffset);
            if (!float.IsFinite(parameterValue))
            {
                refusal = $"Scalar parameter '{name}' contains a non-finite value.";
                return false;
            }
            output.Add(new(index, name, parameterValue, valueOffset + element,
                valueOffset + valueProperty.ValueOffset,
                new Guid(value.Slice(guidProperty.ValueOffset, 16))));
        }
        if (cursor != value.Length)
        { refusal = $"Scalar parameter array has {value.Length - cursor:N0} trailing bytes."; return false; }
        return true;
    }

    private static bool TryParseVectors(
        ReadOnlySpan<byte> value, int valueOffset, IReadOnlyList<UnrealNameEntry> names,
        ICollection<MaterialVectorParameterEvidence> output, out string refusal)
    {
        refusal = string.Empty;
        if (!TryGetArrayCount(value, out var count, out refusal)) return false;
        var cursor = 4;
        for (var index = 0; index < count; index++)
        {
            var element = cursor;
            if (!TryParseNestedProperties(value, ref cursor, names, out var properties, out refusal)) return false;
            if (!TryGetNested(properties, "ParameterName", "NameProperty", 8, out var nameProperty) ||
                !TryGetNested(properties, "ParameterValue", "StructProperty", 16, out var valueProperty) ||
                !TryGetNested(properties, "ExpressionGUID", "StructProperty", 16, out var guidProperty))
            { refusal = $"Vector parameter {index:N0} does not have the proven UE3 name/value/GUID layout."; return false; }
            var nameCursor = nameProperty.ValueOffset;
            if (!TryReadName(value, ref nameCursor, names, out var name))
            { refusal = $"Vector parameter {index:N0} has an invalid name reference."; return false; }
            var components = new[] { ReadSingle(value, valueProperty.ValueOffset), ReadSingle(value, valueProperty.ValueOffset + 4),
                ReadSingle(value, valueProperty.ValueOffset + 8), ReadSingle(value, valueProperty.ValueOffset + 12) };
            if (components.Any(component => !float.IsFinite(component)))
            {
                refusal = $"Vector parameter '{name}' contains a non-finite component.";
                return false;
            }
            output.Add(new(index, name, components[0], components[1], components[2], components[3],
                valueOffset + element, valueOffset + valueProperty.ValueOffset,
                new Guid(value.Slice(guidProperty.ValueOffset, 16))));
        }
        if (cursor != value.Length)
        { refusal = $"Vector parameter array has {value.Length - cursor:N0} trailing bytes."; return false; }
        return true;
    }

    private static bool TryParseTextures(
        ReadOnlySpan<byte> value, int valueOffset, AssetCatalog catalog,
        ICollection<MaterialTextureParameterEvidence> output, out string refusal)
    {
        refusal = string.Empty;
        if (!TryGetArrayCount(value, out var count, out refusal)) return false;
        var cursor = 4;
        for (var index = 0; index < count; index++)
        {
            var element = cursor;
            if (!TryParseNestedProperties(value, ref cursor, catalog.Names, out var properties, out refusal)) return false;
            if (!TryGetNested(properties, "ParameterName", "NameProperty", 8, out var nameProperty) ||
                !TryGetNested(properties, "ParameterValue", "ObjectProperty", 4, out var valueProperty) ||
                !TryGetNested(properties, "ExpressionGUID", "StructProperty", 16, out var guidProperty))
            { refusal = $"Texture parameter {index:N0} does not have the proven UE3 name/value/GUID layout."; return false; }
            var nameCursor = nameProperty.ValueOffset;
            if (!TryReadName(value, ref nameCursor, catalog.Names, out var name))
            { refusal = $"Texture parameter {index:N0} has an invalid name reference."; return false; }
            var reference = ReadInt32(value, valueProperty.ValueOffset);
            output.Add(new(index, name, reference, ResolveObject(reference, catalog),
                valueOffset + element, valueOffset + valueProperty.ValueOffset,
                new Guid(value.Slice(guidProperty.ValueOffset, 16))));
        }
        if (cursor != value.Length)
        { refusal = $"Texture parameter array has {value.Length - cursor:N0} trailing bytes."; return false; }
        return true;
    }

    // CollectedTextureParameters: an array of { Texture (object), Name (name) } structs.
    private static void TryParseCollectedTextures(ReadOnlySpan<byte> value, AssetCatalog catalog, ICollection<MaterialCollectedTexture> output)
    {
        if (!TryGetArrayCount(value, out var count, out _)) return;
        var cursor = 4;
        for (var index = 0; index < count; index++)
        {
            if (!TryParseNestedProperties(value, ref cursor, catalog.Names, out var properties, out _)) return;
            if (!TryGetNested(properties, "Name", "NameProperty", 8, out var nameProperty) ||
                !TryGetNested(properties, "Texture", "ObjectProperty", 4, out var textureProperty)) continue;
            var nameCursor = nameProperty.ValueOffset;
            if (!TryReadName(value, ref nameCursor, catalog.Names, out var name)) continue;
            output.Add(new(name, ResolveObject(ReadInt32(value, textureProperty.ValueOffset), catalog)));
        }
    }

    private static bool TryGetArrayCount(ReadOnlySpan<byte> value, out int count, out string refusal)
    {
        count = 0;
        refusal = string.Empty;
        if (value.Length < 4) { refusal = "Parameter array is shorter than its count field."; return false; }
        count = ReadInt32(value, 0);
        if (count < 0 || count > MaximumParameterCount)
        { refusal = $"Parameter array count {count:N0} is invalid."; return false; }
        return true;
    }

    private static bool TryParseNestedProperties(
        ReadOnlySpan<byte> bytes, ref int cursor, IReadOnlyList<UnrealNameEntry> names,
        out IReadOnlyList<NestedProperty> properties, out string refusal)
    {
        var result = new List<NestedProperty>();
        refusal = string.Empty;
        for (var propertyIndex = 0; propertyIndex < 32; propertyIndex++)
        {
            if (!TryReadName(bytes, ref cursor, names, out var name))
            { properties = []; refusal = "A nested material parameter property has an invalid name."; return false; }
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase))
            { properties = result; return result.Count > 0; }
            if (!TryReadName(bytes, ref cursor, names, out var type) || cursor > bytes.Length - 8 ||
                !type.EndsWith("Property", StringComparison.OrdinalIgnoreCase))
            { properties = []; refusal = $"Nested property '{name}' has an invalid type."; return false; }
            var size = ReadInt32(bytes, cursor);
            var arrayIndex = ReadInt32(bytes, cursor + 4);
            cursor += 8;
            if (size < 0 || arrayIndex < 0)
            { properties = []; refusal = $"Nested property '{name}' has an invalid size/index."; return false; }
            string? metadata = null;
            if (type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadName(bytes, ref cursor, names, out metadata))
                { properties = []; refusal = $"Nested property '{name}' has invalid type metadata."; return false; }
            }
            else if (type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (cursor >= bytes.Length)
                { properties = []; refusal = $"Nested bool property '{name}' is truncated."; return false; }
                cursor++;
            }
            if (size > bytes.Length - cursor)
            { properties = []; refusal = $"Nested property '{name}' exceeds its parameter array."; return false; }
            result.Add(new(name, type, size, arrayIndex, metadata, cursor));
            cursor += size;
        }
        properties = [];
        refusal = "Nested material parameter property count exceeded the safety limit.";
        return false;
    }

    private static bool TryGetNested(
        IReadOnlyList<NestedProperty> properties, string name, string type, int size,
        out NestedProperty property)
    {
        var matches = properties.Where(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                               item.Type.Equals(type, StringComparison.OrdinalIgnoreCase) &&
                                               item.Size == size).ToArray();
        property = matches.Length == 1 ? matches[0] : null!;
        return matches.Length == 1;
    }

    private static string PreviewValue(string type, ReadOnlySpan<byte> value, byte? taggedBool, AssetCatalog catalog)
    {
        if (type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase) && taggedBool is not null)
            return taggedBool == 0 ? "False" : "True";
        if (type.Equals("ObjectProperty", StringComparison.OrdinalIgnoreCase) && value.Length == 4)
            return ResolveObject(ReadInt32(value, 0), catalog);
        if (type.Equals("IntProperty", StringComparison.OrdinalIgnoreCase) && value.Length >= 4)
            return ReadInt32(value, 0).ToString(CultureInfo.InvariantCulture);
        if (type.Equals("FloatProperty", StringComparison.OrdinalIgnoreCase) && value.Length >= 4)
            return ReadSingle(value, 0).ToString("R", CultureInfo.InvariantCulture);
        if (type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase) && value.Length >= 4)
            return $"{ReadInt32(value, 0):N0} entries";
        return value.IsEmpty ? "(empty)" : Convert.ToHexString(value[..Math.Min(16, value.Length)]);
    }

    private static string ResolveObject(int reference, AssetCatalog catalog)
    {
        if (reference == 0) return "None";
        if (reference > 0)
        {
            var index = reference - 1;
            return index < catalog.Assets.Count
                ? $"{catalog.Assets[index].ClassName}'{catalog.Assets[index].ObjectPath}'"
                : $"<invalid-export:{reference}>";
        }
        var importIndex = -reference - 1;
        if (importIndex < 0 || importIndex >= catalog.Imports.Count) return $"<invalid-import:{reference}>";
        var import = catalog.Imports[importIndex];
        return $"{import.ClassName.Resolve(catalog.Names)}'{import.ObjectName.Resolve(catalog.Names)}'";
    }

    private static bool TryReadName(ReadOnlySpan<byte> bytes, ref int cursor,
        IReadOnlyList<UnrealNameEntry> names, out string value)
    {
        value = string.Empty;
        if (cursor < 0 || cursor > bytes.Length - 8) return false;
        var index = ReadInt32(bytes, cursor);
        var number = ReadInt32(bytes, cursor + 4);
        cursor += 8;
        if (index < 0 || index >= names.Count || number < 0) return false;
        value = new UnrealNameReference(index, number).Resolve(names);
        return true;
    }

    private static string ComputeFileHash(string path, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("The supplied package SHA-256 is invalid.");
        return normalized;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) => BitConverter.Int32BitsToSingle(ReadInt32(bytes, offset));

    private sealed record PropertyParseResult(int StartOffset, int EndOffset, IReadOnlyList<MaterialPropertyEvidence> Properties);
    private sealed record NestedProperty(
        string Name, string Type, int Size, int ArrayIndex, string? Metadata, int ValueOffset);
}
