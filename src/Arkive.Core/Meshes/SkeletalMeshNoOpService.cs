using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Inspection;
using Arkive.Core.Packages;

namespace Arkive.Core.Meshes;

public sealed class SkeletalMeshNoOpService
{
    public const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;

    public SkeletalMeshNoOpService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(summaryReader, _protectionProvider);
    }

    public Task<SkeletalMeshNoOpResult> ReconstructAsync(
        SkeletalMeshNoOpRequest request,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Reconstruct(request, cancellationToken), cancellationToken);

    private SkeletalMeshNoOpResult Reconstruct(SkeletalMeshNoOpRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var recon = request.Reconnaissance;
        ValidateReconnaissance(recon);
        var packagePath = Path.GetFullPath(recon.PackagePath);
        var file = new FileInfo(packagePath);
        if (!file.Exists) throw new FileNotFoundException("The source package was not found.", packagePath);
        if (file.Length != recon.PackageLength || file.LastWriteTimeUtc.Ticks != recon.PackageLastWriteUtcTicks)
            Refuse("SKN010", "The source package identity metadata changed; rerun reconnaissance.");
        var currentPackageHash = ComputeFileSha256(packagePath, token);
        if (!currentPackageHash.Equals(recon.PackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SKN013", "The source package SHA-256 changed; rerun reconnaissance.");

        var catalog = _catalogReader.Read(packagePath);
        var asset = catalog.Assets.SingleOrDefault(item => item.ExportIndex == recon.NativeExportIndex);
        if (asset is null || asset.SerialOffset != recon.SerialOffset || asset.SerialSize != recon.SerialSize ||
            !asset.ObjectName.Equals(recon.ObjectName, StringComparison.OrdinalIgnoreCase) ||
            !asset.ClassName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            Refuse("SKN011", "The current native export table no longer matches the reconnaissance report.");

        using var logical = new LogicalPackageStream(catalog.Summary, _protectionProvider);
        var source = new byte[checked((int)recon.SerialSize)];
        logical.Position = recon.SerialOffset;
        logical.ReadExactly(source);
        token.ThrowIfCancellationRequested();
        var sourceHash = Sha256(source);
        if (!sourceHash.Equals(recon.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SKN012", "The source export hash changed; rerun reconnaissance.");

        var boundary = checked((int)recon.NativeDataRelativeOffset!.Value);
        var nativeLayout = recon.NativeLayout!;
        var reconstructed = new byte[source.Length];
        // Replay the property/prefix and every independently hashed native range. Any gap remains
        // opaque but byte-preserved; parsed fields are never regenerated from unverified assumptions.
        source.AsSpan(0, boundary).CopyTo(reconstructed);
        var cursor = boundary;
        foreach (var range in nativeLayout.Ranges.OrderBy(item => item.RelativeOffset))
        {
            var offset = checked((int)range.RelativeOffset);
            var size = checked((int)range.Size);
            if (offset < cursor || offset < boundary || size < 0 || offset > source.Length - size)
                Refuse("SKN020", $"Native range '{range.Name}' is overlapping or outside the export.");
            var actualRangeHash = Sha256(source.AsSpan(offset, size));
            if (!actualRangeHash.Equals(range.Sha256, StringComparison.OrdinalIgnoreCase))
                Refuse("SKN021", $"Native range '{range.Name}' no longer matches its reconnaissance hash.");
            source.AsSpan(cursor, offset - cursor).CopyTo(reconstructed.AsSpan(cursor));
            source.AsSpan(offset, size).CopyTo(reconstructed.AsSpan(offset));
            cursor = offset + size;
        }
        source.AsSpan(cursor).CopyTo(reconstructed.AsSpan(cursor));
        var reconstructedHash = Sha256(reconstructed);
        var firstDifference = FindFirstDifference(source, reconstructed);
        if (firstDifference >= 0 || !sourceHash.Equals(reconstructedHash, StringComparison.Ordinal))
            Refuse("SKN100", $"No-op serialization changed the export at +0x{firstDifference:X}.");

        Directory.CreateDirectory(request.OutputDirectory);
        var safeName = Safe(recon.ObjectName);
        var exportPath = Path.Combine(request.OutputDirectory, safeName + ".skeletalmesh.noop.bin");
        var manifestPath = Path.Combine(request.OutputDirectory, "arkive-skeletalmesh-noop.json");
        WriteBytesAtomic(exportPath, reconstructed);
        watch.Stop();
        var manifest = new SkeletalMeshNoOpManifest(
            CurrentSchemaVersion, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            packagePath, recon.PhysicalPackageName, recon.PackageAlias,
            recon.PackageLength, recon.PackageLastWriteUtcTicks, recon.PackageSha256,
            recon.ProtectionProviderName, recon.ExportIndex, recon.NativeExportIndex!.Value,
            recon.ClassName, recon.ObjectName, recon.SerialOffset, recon.SerialSize,
            boundary, source.LongLength - boundary, nativeLayout.ParserProfile,
            nativeLayout.Ranges.Count, sourceHash, reconstructedHash,
            Path.GetFileName(exportPath), true, -1,
            Path.GetFullPath(request.ReconnaissanceJsonPath), watch.Elapsed.TotalSeconds);
        WriteJsonAtomic(manifestPath, manifest);
        return new(request.OutputDirectory, exportPath, manifestPath, manifest, watch.Elapsed);
    }

    private static void ValidateReconnaissance(SkeletalMeshReconnaissanceReport recon)
    {
        if (recon.SchemaVersion != SkeletalMeshReconnaissanceService.CurrentSchemaVersion)
            Refuse("SKN001", "The reconnaissance schema is stale; rerun SkeletalMesh reconnaissance.");
        if (recon.Readiness != SkeletalMeshReadiness.NoOpReady || recon.NativeExportIndex is null ||
            recon.NativeDataRelativeOffset is null || recon.NativeLayout is null)
            Refuse("SKN002", "The report did not prove a SkeletalMesh export and native-data boundary.");
        if (recon.PackageProfile.FileVersion != PackageConstants.WesternFileVersion ||
            recon.PackageProfile.LicenseeVersion != PackageConstants.WesternLicenseeVersion ||
            recon.PackageProfile.CompressionFlags != PackageConstants.WesternCompressionFlags)
            Refuse("SKN003", "Only the Western Lost Ark 868/16 package profile is authorized.");
        if (recon.NativeDataRelativeOffset < 0 || recon.NativeDataRelativeOffset > recon.SerialSize)
            Refuse("SKN004", "The native-data boundary is outside the export.");
        if (!recon.NativeLayout!.AllRangesInBounds || recon.NativeLayout.Ranges.Count == 0)
            Refuse("SKN005", "The report does not contain a bounded native-range inventory.");
    }

    private static int FindFirstDifference(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var length = Math.Min(left.Length, right.Length);
        for (var index = 0; index < length; index++) if (left[index] != right[index]) return index;
        return left.Length == right.Length ? -1 : length;
    }

    private static void WriteBytesAtomic(string path, byte[] bytes)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string ComputeFileSha256(string path, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "skeletal-mesh" : result;
    }
    private static void Refuse(string code, string message) => throw new SkeletalMeshNoOpRefusalException(code, message);
}
